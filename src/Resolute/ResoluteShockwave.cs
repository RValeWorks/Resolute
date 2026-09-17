using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native Shockwave creates one receiver per collider. Resolute's convex
    // collision regions share a single native UnitPart, so those regions must
    // represent one compartment when the wave applies damage and impulse.
    internal static class ResoluteShockwave
    {
        private static readonly Type ReceiverType = typeof(Shockwave).GetNestedType("InfluencedObject", BindingFlags.NonPublic);
        private static readonly FieldInfo ColliderField = AccessTools.Field(ReceiverType, "collider");
        private static readonly FieldInfo DamageableField = AccessTools.Field(ReceiverType, "damageable");
        private static readonly FieldInfo RadiusField = AccessTools.Field(ReceiverType, "averageRadius");
        private static readonly MethodInfo ColliderBounds = AccessTools.PropertyGetter(typeof(Collider), nameof(Collider.bounds));
        private static readonly MethodInfo CompartmentBounds = AccessTools.Method(typeof(ResoluteShockwave), nameof(GetReceiverBounds));

        internal sealed class ReceiverAudit
        {
            internal int NativeEntries, NativeSectionEntries, DistinctSectionOwners, RemovedDuplicates;
            internal int ResultEntries, ResultSectionEntries, DuplicateSectionOwners, RadiusMismatches, BoundsMismatches;
            internal int OtherEntries;
            internal bool OtherEntriesUnchanged;
        }

        // The extra verification only runs when a diagnostic subscribes. In
        // normal play there is no observer snapshot or mesh-vertex inspection.
        internal static event Action<Shockwave, ReceiverAudit> ReceiversPrepared;

        private static ResoluteStructuralSection Section(Collider collider)
        {
            if (collider == null) return null;
            ResoluteStructuralSection section = collider.GetComponent<ResoluteStructuralSection>();
            return section != null && section.Part != null && section.ClosedMesh != null ? section : null;
        }

        private static Bounds GetSectionBounds(ResoluteStructuralSection section)
        {
            // ClosedMesh is in the owning UnitPart's local coordinates. Read
            // its current transform so native detached and moving parts keep
            // their own world-space center; never use a cached ship position.
            Bounds local = section.ClosedMesh.bounds;
            Vector3 minimum = local.min, maximum = local.max;
            Matrix4x4 matrix = section.transform.localToWorldMatrix;
            Bounds world = new Bounds(matrix.MultiplyPoint3x4(minimum), Vector3.zero);
            for (int corner = 1; corner < 8; corner++)
                world.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                    (corner & 1) == 0 ? minimum.x : maximum.x,
                    (corner & 2) == 0 ? minimum.y : maximum.y,
                    (corner & 4) == 0 ? minimum.z : maximum.z)));
            return world;
        }

        private static Bounds GetReceiverBounds(Collider collider)
        {
            ResoluteStructuralSection section = Section(collider);
            return section != null ? GetSectionBounds(section) : collider.bounds;
        }

        private static float AverageRadius(Bounds bounds)
        {
            Vector3 extents = bounds.extents;
            return (extents.x + extents.y + extents.z) * .333f;
        }

        [HarmonyPatch(typeof(Shockwave), "Start")]
        private static class PrepareReceivers
        {
            // Run before ordinary diagnostic postfixes so their native list
            // count measures the receivers that Update will actually process.
            [HarmonyPriority(Priority.First)]
            private static void Postfix(Shockwave __instance, object ___influencedObjects)
            {
                IList receivers = (IList)___influencedObjects;
                Action<Shockwave, ReceiverAudit> observer = ReceiversPrepared;
                ReceiverAudit audit = observer != null ? new ReceiverAudit { NativeEntries = receivers.Count } : null;
                List<object> originalOthers = observer != null ? new List<object>() : null;
                HashSet<UnitPart> owners = null;
                int write = 0;
                int originalCount = receivers.Count;
                for (int read = 0; read < originalCount; read++)
                {
                    // IList boxes the private native struct. Write the edited
                    // box back into the list after changing its radius.
                    object receiver = receivers[read];
                    Collider collider = (Collider)ColliderField.GetValue(receiver);
                    ResoluteStructuralSection section = Section(collider);
                    if (section != null && ReferenceEquals(DamageableField.GetValue(receiver), section.Part))
                    {
                        if (audit != null) audit.NativeSectionEntries++;
                        if (owners == null) owners = new HashSet<UnitPart>();
                        if (!owners.Add(section.Part)) continue;
                        RadiusField.SetValue(receiver, AverageRadius(GetSectionBounds(section)));
                    }
                    else if (originalOthers != null) originalOthers.Add(receiver);
                    if (read != write || section != null) receivers[write] = receiver;
                    write++;
                }
                // Compact once in native overlap order. Ordinary receivers keep
                // their collider, radius, damageable and Rigidbody unmodified.
                for (int index = originalCount - 1; index >= write; index--) receivers.RemoveAt(index);
                if (observer == null) return;
                audit.DistinctSectionOwners = owners != null ? owners.Count : 0;
                audit.RemovedDuplicates = originalCount - receivers.Count;
                VerifyReceivers(receivers, originalOthers, audit);
                observer(__instance, audit);
            }
        }

        [HarmonyPatch]
        private static class ReachCompartment
        {
            private static MethodBase TargetMethod() => AccessTools.Method(ReceiverType, "HasShockwaveReached");

            private static void Prefix(Collider ___collider, float ___averageRadius,
                Vector3 blastOrigin, float blastPropagation, float blastPower)
            {
                ResoluteStructuralSection section = Section(___collider);
                if (section == null) return;
                float reachedRadius = blastPropagation + ___averageRadius;
                if (Vector3.SqrMagnitude(GetSectionBounds(section).center - blastOrigin) <= reachedRadius * reachedRadius)
                    section.RememberShockwave(blastOrigin, blastPower);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                int replacements = 0;
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(ColliderBounds))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = CompartmentBounds;
                        replacements++;
                    }
                    yield return instruction;
                }
                // This game version uses bounds once for arrival and twice for
                // impulse. Refuse a silent partial patch if that contract changes.
                if (replacements != 3)
                    throw new InvalidOperationException("Unexpected native Shockwave bounds contract: " + replacements);
            }
        }

        private static void VerifyReceivers(IList receivers, List<object> originalOthers, ReceiverAudit audit)
        {
            audit.ResultEntries = receivers.Count;
            audit.OtherEntriesUnchanged = true;
            var seen = new HashSet<UnitPart>();
            int otherIndex = 0;
            for (int index = 0; index < receivers.Count; index++)
            {
                object receiver = receivers[index];
                Collider collider = (Collider)ColliderField.GetValue(receiver);
                ResoluteStructuralSection section = Section(collider);
                if (section == null || !ReferenceEquals(DamageableField.GetValue(receiver), section.Part))
                {
                    if (otherIndex >= originalOthers.Count || !Equals(receiver, originalOthers[otherIndex]))
                        audit.OtherEntriesUnchanged = false;
                    otherIndex++;
                    continue;
                }
                audit.ResultSectionEntries++;
                if (!seen.Add(section.Part)) audit.DuplicateSectionOwners++;
                Bounds bounds = GetReceiverBounds(collider);
                if (Mathf.Abs((float)RadiusField.GetValue(receiver) - AverageRadius(bounds)) > .0001f)
                    audit.RadiusMismatches++;
                // Independent geometric containment check against every source
                // mesh vertex, with a small world-coordinate rounding allowance.
                Bounds envelope = bounds;
                envelope.Expand(.02f);
                Matrix4x4 matrix = section.transform.localToWorldMatrix;
                foreach (Vector3 vertex in section.ClosedMesh.vertices)
                    if (!envelope.Contains(matrix.MultiplyPoint3x4(vertex)))
                    { audit.BoundsMismatches++; break; }
            }
            audit.OtherEntries = otherIndex;
            audit.OtherEntriesUnchanged &= otherIndex == originalOthers.Count;
        }
    }
}
