using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class NaturalPikeObservedThreat
    {
        private sealed class Flight
        {
            internal PikeThreatZone Zone;
            internal FactionHQ Hq;
            internal float RegisteredAt;
            internal double FurthestAlong;
            internal bool Retired;
        }

        // Each member retains the same immutable group zone independently of
        // the launch ship, order queue and current leader. Weak keys retire the
        // snapshot with its missile; no mission history or all-unit scans.
        // Native Ship and Missile both set LocalSim from Server.Active. The
        // authoritative defender therefore shares these registration snapshots;
        // display-only peers neither reconstruct intent nor grant detections.
        private static readonly ConditionalWeakTable<Missile, Flight> Flights =
            new ConditionalWeakTable<Missile, Flight>();

        internal static void Register(Missile missile, PikeThreatZone zone, FactionHQ hq)
        {
            if (!ValidPike(missile) || !missile.LocalSim || zone == null || !zone.Valid ||
                hq == null || missile.NetworkHQ != hq || !PikeObservedThreatPolicy.Finite(Time.timeSinceLevelLoad)) return;
            // A manual registration can replace an automatic reservation taken
            // by an earlier native registration subscriber in the same launch.
            Flights.Remove(missile);
            Flights.Add(missile, new Flight { Zone = zone, Hq = hq, RegisteredAt = Time.timeSinceLevelLoad });
        }

        internal static bool TryGetLaunchZone(Missile missile, out PikeThreatZone zone)
        {
            zone = null;
            if (!ValidPike(missile) || !Flights.TryGetValue(missile, out Flight flight) || flight.Retired ||
                missile.NetworkHQ != flight.Hq || Time.timeSinceLevelLoad < flight.RegisteredAt) return false;
            NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
            NaturalPikeGroupTargeting targeting = missile.GetComponent<NaturalPikeGroupTargeting>();
            if (controller != null && controller.TerminalReleased || targeting != null &&
                (targeting.CanRelease || targeting.YieldToNativeGuidance))
            {
                flight.Retired = true;
                return false;
            }
            zone = flight.Zone;
            return true;
        }

        internal static bool IsTargetOrObservedIncoming(Missile missile, Unit defender)
        {
            // Preserve direct native targets and all unrelated missiles. A
            // preassignment Pike may still carry its original launch target ID;
            // every detected hostile ship in its frozen search zone also sees
            // it as targeting them until the leader actually allocates it.
            if (missile.targetID == defender.persistentID) return true;
            if (!ValidPike(missile) || !(defender is Ship) ||
                defender.disabled || defender.NetworkHQ == null || missile.NetworkHQ == null ||
                defender.NetworkHQ == missile.NetworkHQ) return false;
            TrackingInfo track = defender.NetworkHQ.GetTrackingData(missile.persistentID);
            if (track == null || !track.Observed()) return false;
            float age = Time.timeSinceLevelLoad - track.lastSpottedTime;
            if (!PikeObservedThreatPolicy.Finite(age) || age < 0f || age >= PikeObservedThreatPolicy.MaximumObservationGap ||
                !TryGetLaunchZone(missile, out PikeThreatZone zone)) return false;
            GlobalPosition position = defender.GlobalPosition();
            // The defender already observes this missile. Use its known track
            // to retire the passed area, never hidden live hostile movement.
            GlobalPosition missilePosition = track.GetPosition();
            if (!Flights.TryGetValue(missile, out Flight flight)) return false;
            double along = zone.Search.ProjectAlong(missilePosition.x, missilePosition.z);
            if (!PikeObservedThreatPolicy.Finite(along)) return false;
            flight.FurthestAlong = System.Math.Max(flight.FurthestAlong, along);
            return zone.Contains(position.x, position.z, flight.FurthestAlong);
        }

        internal static bool IsNotTargetOrObservedIncoming(Missile missile, Unit defender) =>
            !IsTargetOrObservedIncoming(missile, defender);

        // Diagnostic-only snapshot: never runs a threat predicate, advances a
        // state or reads missile motion.
        internal static object CaptureLaunchZone(Missile missile)
        {
            if (ReferenceEquals(missile, null) || !Flights.TryGetValue(missile, out Flight flight)) return null;
            return new { centerX = flight.Zone.X, centerZ = flight.Zone.Z, radiusMetres = flight.Zone.Radius,
                registeredGameSeconds = flight.RegisteredAt, retired = flight.Retired,
                originX = flight.Zone.Search.OriginX, originZ = flight.Zone.Search.OriginZ,
                directionX = flight.Zone.Search.DirectionX, directionZ = flight.Zone.Search.DirectionZ,
                activationDistance = flight.Zone.Search.ActivationDistance, maximumRange = flight.Zone.Search.MaximumRange,
                definition = "frozen launch-axis search sector and persistent corridor; observed passed area retires" };
        }

        private static bool ValidPike(Missile missile) => missile != null && !missile.disabled &&
            missile.isActiveAndEnabled && missile.definition != null && missile.definition.jsonKey == "rsl_ashm";

        // Change only the target-ID predicate. Native tracking, range, weapon
        // suitability, reservations, cooldowns and actual firing stay intact.
        internal static IEnumerable<CodeInstruction> ReplacePredicate(IEnumerable<CodeInstruction> instructions,
            bool inequality, out bool active)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo targetId = AccessTools.PropertyGetter(typeof(Missile), nameof(Missile.targetID));
            FieldInfo persistentId = AccessTools.Field(typeof(Unit), nameof(Unit.persistentID));
            MethodInfo comparison = AccessTools.Method(typeof(PersistentID), inequality ? "op_Inequality" : "op_Equality",
                new[] { typeof(PersistentID), typeof(PersistentID) });
            MethodInfo replacement = AccessTools.Method(typeof(ResoluteSupplyDefense),
                inequality ? nameof(IsNotTargetOrObservedIncoming) : nameof(IsTargetOrObservedIncoming));
            int getter = -1, compare = -1, count = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(targetId)) continue;
                // AnalyzeTarget loads its analyzer argument; Turret loads the
                // attachedUnit field. Require these exact native stack shapes.
                int c = i + (inequality ? 4 : 3);
                if (c >= code.Count || !code[c].Calls(comparison) || code[c - 1].opcode != OpCodes.Ldfld ||
                    !Equals(code[c - 1].operand, persistentId)) continue;
                bool shape = inequality
                    ? code[i + 1].opcode == OpCodes.Ldarg_0 && code[i + 2].opcode == OpCodes.Ldfld &&
                        code[i + 2].operand is FieldInfo f && f.Name == "attachedUnit" && f.FieldType == typeof(Unit)
                    : code[i + 1].opcode == OpCodes.Ldarg_1;
                for (int j = i; j <= c; j++) shape &= code[j].blocks.Count == 0 && (j == i || code[j].labels.Count == 0);
                if (shape) { getter = i; compare = c; count++; }
            }
            active = count == 1 && targetId != null && persistentId != null && comparison != null && replacement != null;
            if (!active) return code;
            code[getter].opcode = OpCodes.Nop; code[getter].operand = null;
            code[compare - 1].opcode = OpCodes.Nop; code[compare - 1].operand = null;
            code[compare].opcode = OpCodes.Call; code[compare].operand = replacement;
            return code;
        }
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
    internal static class PikeIncomingOpportunity
    {
        internal static bool PatchActive { get; private set; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> code = NaturalPikeObservedThreat.ReplacePredicate(instructions, false, out bool active);
            PatchActive = active;
            if (!active) Plugin.Instance?.LogStartupWarning("Pike incoming-track assessment unavailable: native CombatAI layout changed. Original assessment retained.");
            return code;
        }
    }

    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class PikeIncomingDefensiveTurret
    {
        internal static bool PatchActive { get; private set; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> code = NaturalPikeObservedThreat.ReplacePredicate(instructions, true, out bool active);
            PatchActive = active;
            if (!active) Plugin.Instance?.LogStartupWarning("Pike defensive-turret assessment unavailable: native Turret layout changed. Original assessment retained.");
            return code;
        }
    }
}
