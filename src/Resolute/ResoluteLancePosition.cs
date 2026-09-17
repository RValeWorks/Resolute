using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // A manually designated land point is an inertial objective. It is never
    // represented by a fabricated Unit or by Pike's group/search machinery.
    internal sealed class ResoluteLancePosition
    {
        private static readonly ConditionalWeakTable<Missile, ResoluteLancePosition> Objectives =
            new ConditionalWeakTable<Missile, ResoluteLancePosition>();
        private Missile missile;
        private GlobalPosition point;

        internal static void Register(Missile missile, GlobalPosition point)
        {
            if (missile == null || !missile.LocalSim || missile.definition?.jsonKey != "rsl_scramjet" ||
                !missile.targetID.NotValid || missile.GetComponent<ARHSeeker>() == null ||
                Objectives.TryGetValue(missile, out _)) return;
            if (!ResoluteEngagementPolicy.Finite(point.x) || !ResoluteEngagementPolicy.Finite(point.y) ||
                !ResoluteEngagementPolicy.Finite(point.z)) return;
            var objective = new ResoluteLancePosition { missile = missile, point = point };
            Objectives.Add(missile, objective);
            missile.onDisableUnit += objective.Disabled;
        }

        internal static bool TryGetPoint(Missile missile, out GlobalPosition point)
        {
            point = default(GlobalPosition);
            if (missile == null || missile.disabled || !missile.LocalSim || !missile.targetID.NotValid ||
                missile.definition?.jsonKey != "rsl_scramjet" || !Objectives.TryGetValue(missile, out var objective)) return false;
            point = objective.point;
            return true;
        }

        internal static bool IsObservedIncoming(Missile missile, Unit defender)
        {
            // Only the authoritative, explicitly registered fixed objective
            // supplies intent. The defender still needs its own current native
            // observation; this never discovers missiles or invents a target.
            if (defender == null || defender.disabled || !defender.isActiveAndEnabled ||
                !(defender is Ship || defender is Building || defender is GroundVehicle) ||
                defender.NetworkHQ == null || missile == null || !missile.isActiveAndEnabled ||
                missile.NetworkHQ == null || missile.NetworkHQ == defender.NetworkHQ ||
                !TryGetPoint(missile, out var point)) return false;
            TrackingInfo track = defender.NetworkHQ.GetTrackingData(missile.persistentID);
            if (track == null || !track.Observed()) return false;
            float age = Time.timeSinceLevelLoad - track.lastSpottedTime;
            if (!PikeObservedThreatPolicy.Finite(age) || age < 0f ||
                age >= PikeObservedThreatPolicy.MaximumObservationGap) return false;

            // A fixed land coordinate threatens a unit only while it actually
            // overlaps that unit's own physical footprint. Use its native
            // bounding radius, not a fleet-wide radius or a fabricated victim.
            GlobalPosition ownPosition = defender.GlobalPosition();
            float radius = defender.maxRadius;
            return PikeObservedThreatPolicy.Finite(radius) && radius > 0f &&
                PikeObservedThreatPolicy.Finite(ownPosition.y) &&
                System.Math.Abs(point.y - ownPosition.y) <= radius &&
                PikeObservedThreatPolicy.Contains(point.x - ownPosition.x, point.z - ownPosition.z, radius);
        }

        private void Disabled(Unit unit)
        {
            if (missile == null) return;
            missile.onDisableUnit -= Disabled;
            Objectives.Remove(missile);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.InterceptPriority))]
    internal static class ResoluteLancePositionThreat
    {
        private static void Postfix(Missile __instance, Unit toUnit, Unit ___target,
            WeaponInfo ___info, ref float __result)
        {
            if (__result == 0f && ___target == null && ___info != null && ___info.blastDamage < 500f &&
                ResoluteLancePosition.IsObservedIncoming(__instance, toUnit))
                // Native neutral priority for a live coordinate threat. All
                // weapon suitability, intercept geometry and firing stay native.
                __result = 1f;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Initialize))]
    internal static class ResoluteLancePositionInitialize
    {
        private static void Postfix(Missile ___missile, ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel, ref Vector3 ___knownVelPrev, ref Vector3 ___knownAccel, ref bool ___homeOnJam)
        {
            if (!ResoluteLancePosition.TryGetPoint(___missile, out var point)) return;
            // Keep native startup/subscriptions and its scheduled slow update.
            // ARH's targetless initializer ignores aimpoint; correct that one
            // instance after startup. A fixed coordinate does not chase jammers.
            ___knownPos = point;
            ___knownVel = ___knownVelPrev = ___knownAccel = Vector3.zero;
            ___homeOnJam = false;
            ___missile.SetAimpoint(point, Vector3.zero);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class ResoluteLancePositionSeek
    {
        private static void Prefix(Missile ___missile, ref GlobalPosition ___knownPos, ref Vector3 ___knownVel,
            ref bool ___armed, ref bool ___guidance, float ___armDelay, float ___guidanceDelay)
        {
            if (!ResoluteLancePosition.TryGetPoint(___missile, out var point)) return;
            // Native targetless Seek returns before these normal launch steps.
            // Retain their exact serialized delays, then let its inertial
            // targetless branch set the aimpoint in the ordinary way.
            if (!___armed && ___missile.timeSinceSpawn > ___armDelay)
            {
                ___armed = true;
                ___missile.Arm();
                ___missile.SetTangible(true);
            }
            if (!___guidance && ___missile.timeSinceSpawn > ___guidanceDelay)
            {
                ___guidance = true;
                ___missile.DeployFins();
            }
            ___knownPos = point;
            ___knownVel = Vector3.zero;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "SlowChecks")]
    internal static class ResoluteLancePositionRetirement
    {
        private static bool Prefix(Missile ___missile, float ___selfDestructAtSpeed,
            float ___loftAmount, ref float ___targetDist, ref float ___timeToTarget)
        {
            if (!ResoluteLancePosition.TryGetPoint(___missile, out var point)) return true;
            // Current native ARH retirement, removing only targetUnit == null:
            // that is expected for an explicit coordinate. Native time gates,
            // lost-energy/missed-flight checks, detonations and all Missile
            // collision/lifetime processing remain authoritative.
            if ((___missile.timeSinceSpawn > 10f || (!___missile.EngineOn() && ___missile.timeSinceSpawn > 2f)) &&
                (___missile.LosingGround() || ___missile.MissedTarget() || ___missile.speed < ___selfDestructAtSpeed))
                ___missile.Detonate(___missile.rb.velocity, false, false);
            if (___loftAmount > 0f)
            {
                Vector3 offset = point - ___missile.GlobalPosition();
                float closingSpeed = Vector3.Dot(offset.normalized, ___missile.rb.velocity);
                ___targetDist = offset.magnitude;
                ___timeToTarget = ___targetDist / Mathf.Max(closingSpeed, 10f);
            }
            return false;
        }
    }
}
