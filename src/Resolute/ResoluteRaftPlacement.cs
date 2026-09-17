using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class ResoluteRaftPlacement
    {
        internal const int Attempts = 32;
        // Original Resolute_Raft_0.1.0 bounds in the unchanged water-datum frame:
        // X/Z -2..2 m, Y -.255..1.84 m. Keep the complete canopy inside both
        // the physical body and the slightly expanded deployment clearance.
        internal static Vector3 BodyCenter => new Vector3(0f, .7925f, 0f);
        internal static Vector3 BodySize => new Vector3(4f, 2.095f, 4f);
        internal static Vector3 ClearanceHalfExtents => new Vector3(2.5f, 1.15f, 2.5f);
        // Two 4 m square bodies plus .5 m margin per edge, at arbitrary yaw:
        // 2 * sqrt(2) * 2.5 m = 7.071 m. Round up instead of overlapping corners.
        internal const float MinimumSeparation = 7.1f;
        internal static Vector3 Candidate(Vector3 center, float length, float width, float bearingDegrees, float radiusFraction, float seaY)
        {
            float radius = Mathf.Max(length, width) * (.5f + Mathf.Clamp01(radiusFraction));
            float angle = bearingDegrees * Mathf.Deg2Rad;
            return new Vector3(center.x + Mathf.Cos(angle) * radius, seaY - .15f, center.z + Mathf.Sin(angle) * radius);
        }
        internal static bool Overlaps(Vector3 offset)
            => offset.x * offset.x + offset.z * offset.z < MinimumSeparation * MinimumSeparation;

        internal static bool SkipRaftSupport(bool resoluteRaft, bool slung, bool hitAnotherRaft)
            => resoluteRaft && !slung && hitAnotherRaft;
    }

    // Keep the native pilot's water buoyancy, drag, health, capture and sling
    // lifecycle. Only its pedestrian standing-height aid must not lift a raft
    // another 1.3 m above another raft, producing a staircase on the sea.
    [HarmonyPatch(typeof(PilotDismounted), "LocalFixedUpdate")]
    internal static class ResoluteRaftGroundSupport
    {
        [HarmonyPrepare]
        private static bool Prepare() => ResoluteLiferafts.Enabled;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var native = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(int) });
            var replacement = AccessTools.Method(typeof(ResoluteRaftGroundSupport), nameof(Linecast));
            int matches = 0;
            foreach (var instruction in code) if (instruction.Calls(native)) matches++;
            if (matches != 1)
            {
                Debug.LogWarning("Resolute raft ground-support adapter unavailable: native pilot method shape changed.");
                return code;
            }
            var result = new List<CodeInstruction>(code.Count + 1);
            foreach (var instruction in code)
            {
                if (instruction.Calls(native))
                {
                    var pilot = new CodeInstruction(OpCodes.Ldarg_0);
                    pilot.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    result.Add(pilot);
                    instruction.operand = replacement;
                }
                result.Add(instruction);
            }
            return result;
        }

        private static bool Linecast(Vector3 from, Vector3 to, out RaycastHit hit, int mask, PilotDismounted pilot)
        {
            bool found = Physics.Linecast(from, to, out hit, mask);
            if (!found || pilot == null || pilot.definition == null || pilot.definition.jsonKey != ResoluteLiferafts.DefinitionKey || pilot.IsSlung()) return found;
            PilotDismounted support = hit.collider == null ? null : hit.collider.GetComponentInParent<PilotDismounted>();
            bool otherRaft = support != null && support != pilot && support.definition != null && support.definition.jsonKey == ResoluteLiferafts.DefinitionKey;
            return !ResoluteRaftPlacement.SkipRaftSupport(true, false, otherRaft);
        }
    }
}
