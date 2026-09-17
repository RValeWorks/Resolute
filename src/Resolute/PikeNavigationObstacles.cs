using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Guidance-only queries. No collider, layer, Missile.radarAlt, seeker or
    // damage state is changed. In particular a ship remains physically solid.
    internal static class PikeNavigationObstacles
    {
        private sealed class HelperOwner
        {
            internal Missile Missile;
            internal float RadarAltitude;
        }

        private static readonly Dictionary<OpticalSeekerCruiseMissile, HelperOwner> Helpers =
            new Dictionary<OpticalSeekerCruiseMissile, HelperOwner>();
        // Physics runs on Unity's main thread. Reuse bounded buffers; a full
        // NonAlloc result is unordered and may omit the real terrain behind a
        // ship, so never accept it as a complete clear-corridor result.
        private static readonly RaycastHit[][] Hits = {
            new RaycastHit[32], new RaycastHit[128], new RaycastHit[512]
        };
        internal static int SaturatedQueries { get; private set; }

        internal static bool IsPike(Missile missile) => missile != null && missile.LocalSim &&
            !missile.disabled && missile.definition != null && missile.definition.jsonKey == "rsl_ashm";

        internal static void Register(OpticalSeekerCruiseMissile helper, Missile missile)
        {
            if (helper != null && IsPike(missile))
                Helpers[helper] = new HelperOwner { Missile = missile, RadarAltitude = missile.radarAlt };
        }

        internal static void Unregister(OpticalSeekerCruiseMissile helper)
        {
            // ReferenceEquals also permits removal after Unity destroys it.
            if (!ReferenceEquals(helper, null)) Helpers.Remove(helper);
        }

        private static bool TryOwner(OpticalSeekerCruiseMissile helper, out HelperOwner owner)
        {
            owner = null;
            return helper != null && Helpers.TryGetValue(helper, out owner) && IsPike(owner.Missile);
        }

        internal static void RefreshHelperAltitude(OpticalSeekerCruiseMissile helper)
        {
            if (TryOwner(helper, out HelperOwner owner)) owner.RadarAltitude = RadarAltitude(owner.Missile);
        }

        internal static bool Excludes(Missile missile, Collider collider)
        {
            if (!IsPike(missile) || missile.NetworkHQ == null || collider == null) return false;
            UnitPart part = collider.GetComponentInParent<UnitPart>();
            // A detached chunk remains an obstacle even if its old parentUnit
            // reference still points at an enemy ship.
            if (part != null && part.IsDetached()) return false;
            Unit unitOwner = part != null ? part.parentUnit : null;
            if (unitOwner == null) unitOwner = collider.GetComponentInParent<Unit>();
            if (unitOwner == null && collider.attachedRigidbody != null)
                unitOwner = collider.attachedRigidbody.GetComponentInParent<Unit>();
            Ship owner = unitOwner as Ship;
            // Native neutral units have no HQ; different non-null HQs are the
            // game's hostile factions. Do not infer ownership from a layer,
            // name, target identity or proximity to a ship.
            return owner != null && owner.NetworkHQ != null && owner.NetworkHQ != missile.NetworkHQ;
        }

        private static bool Closest(Missile missile, RaycastHit[] buffer, int count, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            bool found = false;
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                RaycastHit candidate = buffer[index];
                if (candidate.collider == null || Excludes(missile, candidate.collider) ||
                    candidate.distance >= nearest) continue;
                hit = candidate; nearest = candidate.distance; found = true;
            }
            return found;
        }

        internal static bool Raycast(Missile missile, Vector3 origin, Vector3 direction, out RaycastHit hit,
            float distance, int mask, QueryTriggerInteraction triggers)
        {
            if (!IsPike(missile)) return Physics.Raycast(origin, direction, out hit, distance, mask, triggers);
            foreach (RaycastHit[] buffer in Hits)
            {
                int count = Physics.RaycastNonAlloc(origin, direction, buffer, distance, mask, triggers);
                if (count < buffer.Length) return Closest(missile, buffer, count, out hit);
            }
            // Rare pathological overlap: retain the native closest obstacle.
            // This can conservatively avoid an enemy hull, but cannot erase
            // terrain from a saturated result or allocate an unbounded array.
            SaturatedQueries++;
            return Physics.Raycast(origin, direction, out hit, distance, mask, triggers);
        }

        internal static bool SphereCast(Missile missile, Vector3 origin, float radius, Vector3 direction,
            out RaycastHit hit, float distance, int mask, QueryTriggerInteraction triggers)
        {
            if (!IsPike(missile)) return Physics.SphereCast(origin, radius, direction, out hit, distance, mask, triggers);
            foreach (RaycastHit[] buffer in Hits)
            {
                int count = Physics.SphereCastNonAlloc(origin, radius, direction, buffer, distance, mask, triggers);
                if (count < buffer.Length) return Closest(missile, buffer, count, out hit);
            }
            SaturatedQueries++;
            return Physics.SphereCast(origin, radius, direction, out hit, distance, mask, triggers);
        }

        internal static bool Linecast(Missile missile, Vector3 from, Vector3 to, out RaycastHit hit,
            int mask, QueryTriggerInteraction triggers)
        {
            if (!IsPike(missile)) return Physics.Linecast(from, to, out hit, mask, triggers);
            Vector3 direction = to - from;
            float distance = direction.magnitude;
            if (distance <= 0f) { hit = default(RaycastHit); return false; }
            return Raycast(missile, from, direction / distance, out hit, distance, mask, triggers);
        }

        internal static float RadarAltitude(Missile missile)
        {
            if (!IsPike(missile)) return missile != null ? missile.radarAlt : 0f;
            Vector3 position = missile.transform.position;
            float seaAltitude = position.y - Datum.LocalSeaY;
            float altitude = Raycast(missile, position, Vector3.down, out RaycastHit hit, 10000f,
                (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask, QueryTriggerInteraction.UseGlobal)
                ? hit.distance : seaAltitude;
            return Mathf.Min(altitude, seaAltitude);
        }

        // These three adapters are reachable only from the native helper's
        // TerrainWaypoint call sites. All other instances retain native calls.
        internal static bool NativeLinecastHit(Vector3 from, Vector3 to, out RaycastHit hit, int mask,
            OpticalSeekerCruiseMissile helper)
            => TryOwner(helper, out HelperOwner owner)
                ? Linecast(owner.Missile, from, to, out hit, mask, QueryTriggerInteraction.UseGlobal)
                : Physics.Linecast(from, to, out hit, mask);

        internal static bool NativeLinecast(Vector3 from, Vector3 to, int mask, OpticalSeekerCruiseMissile helper)
            => TryOwner(helper, out HelperOwner owner)
                ? Linecast(owner.Missile, from, to, out RaycastHit ignored, mask, QueryTriggerInteraction.UseGlobal)
                : Physics.Linecast(from, to, mask);

        internal static float NativeRadarAltitude(Unit unit, OpticalSeekerCruiseMissile helper)
            => TryOwner(helper, out HelperOwner owner) && owner.Missile == unit ? owner.RadarAltitude : unit.radarAlt;
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.TerrainWaypoint))]
    internal static class PikeNativeTerrainQueriesPatch
    {
        internal static bool RedirectsActive { get; private set; }
        private static bool warnedUnsupportedShape;

        private static void Prefix(OpticalSeekerCruiseMissile __instance)
        {
            if (RedirectsActive) PikeNavigationObstacles.RefreshHelperAltitude(__instance);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo lineHit = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(int) });
            MethodInfo line = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
                new[] { typeof(Vector3), typeof(Vector3), typeof(int) });
            FieldInfo radar = AccessTools.Field(typeof(Unit), nameof(Unit.radarAlt));
            MethodInfo hitAdapter = AccessTools.Method(typeof(PikeNavigationObstacles), nameof(PikeNavigationObstacles.NativeLinecastHit));
            MethodInfo lineAdapter = AccessTools.Method(typeof(PikeNavigationObstacles), nameof(PikeNavigationObstacles.NativeLinecast));
            MethodInfo radarAdapter = AccessTools.Method(typeof(PikeNavigationObstacles), nameof(PikeNavigationObstacles.NativeRadarAltitude));
            var native = new List<CodeInstruction>(instructions);
            var replacements = new Dictionary<int, MethodInfo>();
            int hitCalls = 0, lineCalls = 0, radarReads = 0;
            bool unsupportedBoundary = false;
            // Validate the entire shape before touching any instruction. A
            // future game/other-mod change must not make Resolute fail to load.
            for (int index = 0; index < native.Count; index++)
            {
                CodeInstruction original = native[index];
                MethodInfo replacement = null;
                if (original.opcode == OpCodes.Call && Equals(original.operand, lineHit))
                { replacement = hitAdapter; hitCalls++; }
                else if (original.opcode == OpCodes.Call && Equals(original.operand, line))
                { replacement = lineAdapter; lineCalls++; }
                else if (original.opcode == OpCodes.Ldfld && Equals(original.operand, radar))
                { replacement = radarAdapter; radarReads++; }
                if (replacement == null) continue;
                replacements.Add(index, replacement);
                unsupportedBoundary |= original.blocks.Count != 0;
            }
            RedirectsActive = lineHit != null && line != null && radar != null &&
                hitAdapter != null && lineAdapter != null && radarAdapter != null &&
                hitCalls == 1 && lineCalls == 1 && radarReads == 2 && !unsupportedBoundary;
            if (!RedirectsActive)
            {
                if (!warnedUnsupportedShape)
                {
                    warnedUnsupportedShape = true;
                    Plugin.Instance?.LogStartupWarning("Pike native terrain filtering is unavailable: TerrainWaypoint's query layout changed. Keeping the original native method; Resolute will still load.");
                }
                return native;
            }
            var result = new List<CodeInstruction>(native.Count + replacements.Count);
            for (int index = 0; index < native.Count; index++)
            {
                CodeInstruction original = native[index];
                if (!replacements.TryGetValue(index, out MethodInfo replacement)) { result.Add(original); continue; }
                var instance = new CodeInstruction(OpCodes.Ldarg_0);
                instance.labels.AddRange(original.labels);
                result.Add(instance);
                result.Add(new CodeInstruction(OpCodes.Call, replacement));
            }
            return result;
        }
    }
}
