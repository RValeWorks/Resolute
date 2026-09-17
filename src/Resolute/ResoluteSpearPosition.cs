using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // A coordinate is an inertial objective, not a hidden Unit. The native
    // cruise seeker still owns startup, fins, terrain routing, formation and
    // propulsion. Only its target-required terminal/retirement branches need
    // a coordinate equivalent for a manually ordered Spear.
    internal sealed class ResoluteSpearPosition
    {
        private static readonly ConditionalWeakTable<OpticalSeekerCruiseMissile, ResoluteSpearPosition> Objectives =
            new ConditionalWeakTable<OpticalSeekerCruiseMissile, ResoluteSpearPosition>();
        internal Missile Missile;
        internal GlobalPosition Point;
        private float nextTerminalSightCheck;
        private bool terminalSightClear;

        internal static bool TryResolveLand(GlobalPosition selected, out GlobalPosition surface)
        {
            surface = selected;
            Vector3 column = selected.ToLocalPosition();
            column.y = Datum.LocalSeaY + 20000f;
            // Once per order, independent of target/track truth. The static
            // mask includes land/buildings, not ships or moving contacts.
            if (!Physics.Raycast(column, Vector3.down, out RaycastHit hit, 40000f,
                (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) || hit.point.y < Datum.LocalSeaY)
                return false;
            surface = hit.point.ToGlobalPosition();
            return true;
        }

        internal static void Register(Missile missile, GlobalPosition point)
        {
            if (missile == null || !missile.LocalSim || missile.definition?.jsonKey != "rsl_cruise" ||
                !missile.targetID.NotValid) return;
            OpticalSeekerCruiseMissile seeker = missile.GetComponent<OpticalSeekerCruiseMissile>();
            if (seeker == null || Objectives.TryGetValue(seeker, out _)) return;
            var objective = new ResoluteSpearPosition { Missile = missile, Point = point };
            Objectives.Add(seeker, objective);
            missile.onDisableUnit += objective.Disabled;
        }

        internal static bool TryGet(OpticalSeekerCruiseMissile seeker, out ResoluteSpearPosition objective)
        {
            objective = null;
            return seeker != null && Objectives.TryGetValue(seeker, out objective) &&
                objective.Missile != null && objective.Missile.LocalSim && !objective.Missile.disabled;
        }

        private void Disabled(Unit unit)
        {
            if (Missile == null) return;
            Missile.onDisableUnit -= Disabled;
            OpticalSeekerCruiseMissile seeker = Missile.GetComponent<OpticalSeekerCruiseMissile>();
            if (seeker != null) Objectives.Remove(seeker);
        }

        internal void PreTerminal(OpticalSeekerCruiseMissile seeker, ref float lastCheck,
            ref GlobalPosition aim, ref bool terminal, float terminalRange)
        {
            if (Time.timeSinceLevelLoad - lastCheck < .5f) return;
            lastCheck = Time.timeSinceLevelLoad;
            aim = Point;
            Missile.SetAimpoint(seeker.TerrainWaypoint(aim), Vector3.zero);
            if (Missile.timeSinceSpawn <= 6f || !FastMath.InRange(Missile.GlobalPosition(), Point, terminalRange)) return;
            // Enter the terminal dive only with an unobstructed line to the
            // chosen surface. Until then native terrain routing continues.
            if (!ClearCoordinateSight()) return;
            terminalSightClear = true;
            nextTerminalSightCheck = Time.timeSinceLevelLoad + .25f;
            terminal = true;
            Missile.Arm();
        }

        private bool ClearCoordinateSight()
        {
            Vector3 destination = Point.ToLocalPosition();
            Vector3 from = Missile.transform.position;
            float distance = Vector3.Distance(from, destination);
            return !Physics.Linecast(from, destination + Vector3.up * .25f, out RaycastHit hit,
                (int)PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) || hit.distance + 1f >= distance;
        }

        internal void Terminal(float altitudeTarget, JinkEvasion jink, TopAttack topAttack, TerminalBoost boost,
            ref GlobalPosition aim)
        {
            Vector3 offset = Point - Missile.GlobalPosition();
            offset.y = 0f;
            float range = offset.magnitude;
            if (Time.timeSinceLevelLoad >= nextTerminalSightCheck)
            {
                nextTerminalSightCheck = Time.timeSinceLevelLoad + .25f;
                terminalSightClear = ClearCoordinateSight();
            }
            if (!terminalSightClear)
            {
                // Native terminal guidance also climbs when the impact point
                // becomes obscured. Cache this static-only check at 4 Hz.
                aim = Point + Vector3.up * range * .5f;
                Missile.SetAimpoint(aim, Vector3.zero);
                return;
            }
            aim = Point;
            // Same stationary-target presentation and gravity compensation as
            // native TerminalMode, without looking up or assigning any unit.
            if (jink.amount > 0f) aim += jink.ApplyJink(Missile.GlobalPosition(), Point, Missile.speed, range);
            if (topAttack.Amount > 0f) aim += topAttack.ApplyTopAttack(Missile.GlobalPosition(), Point, Missile.speed);
            if (boost.Amount > 0f) boost.ApplyTerminalBoost(Missile, Missile.GlobalPosition(), Point);
            float time = range / Mathf.Max(Missile.speed, 10f);
            if (time < 4f) aim += time * time * 4.905f * Vector3.up;
            else if (Missile.radarAlt < altitudeTarget)
                aim += (altitudeTarget - 2f * Mathf.Min(Missile.rb.velocity.y, 0f) * time) * Vector3.up;
            Missile.SetAimpoint(aim, Vector3.zero);
        }

        internal void SlowChecks()
        {
            // Native cruise retirement with only its targetUnit-null test
            // removed: a fixed coordinate remains valid without a Unit.
            Missile.UpdateRadarAlt();
            if (Missile.timeSinceSpawn > 10f &&
                (Missile.LosingGround() || Missile.MissedTarget() || Missile.speed < 100f))
                Missile.Detonate(Missile.rb.velocity, false, false);
            if (!Missile.IsTangible() && Missile.timeSinceSpawn > 2f) Missile.SetTangible(true);
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.Initialize))]
    internal static class ResoluteSpearPositionInitialize
    {
        private static void Prefix(OpticalSeekerCruiseMissile __instance, ref GlobalPosition aimpoint)
        {
            if (ResoluteSpearPosition.TryGet(__instance, out var objective)) aimpoint = objective.Point;
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.PreTerminalMode))]
    internal static class ResoluteSpearPositionPreTerminal
    {
        private static bool Prefix(OpticalSeekerCruiseMissile __instance, ref float ___lastTerminalCheck,
            ref GlobalPosition ___aimPos, ref bool ___terminalMode, float ___terminalRange)
        {
            if (!ResoluteSpearPosition.TryGet(__instance, out var objective)) return true;
            objective.PreTerminal(__instance, ref ___lastTerminalCheck, ref ___aimPos, ref ___terminalMode, ___terminalRange);
            return false;
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.TerminalMode))]
    internal static class ResoluteSpearPositionTerminal
    {
        private static bool Prefix(OpticalSeekerCruiseMissile __instance, float ___altitudeTarget,
            JinkEvasion ___jinkEvasion, TopAttack ___topAttack, TerminalBoost ___terminalBoost, ref GlobalPosition ___aimPos)
        {
            if (!ResoluteSpearPosition.TryGet(__instance, out var objective)) return true;
            objective.Terminal(___altitudeTarget, ___jinkEvasion, ___topAttack, ___terminalBoost, ref ___aimPos);
            return false;
        }
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "SlowChecks")]
    internal static class ResoluteSpearPositionRetirement
    {
        private static bool Prefix(OpticalSeekerCruiseMissile __instance)
        {
            if (!ResoluteSpearPosition.TryGet(__instance, out var objective)) return true;
            objective.SlowChecks();
            return false;
        }
    }
}
