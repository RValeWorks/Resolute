using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class NaturalPikeAreaSearch
    {
        internal static GlobalPosition NavigationPoint(Missile missile, GlobalPosition area, Vector3 course)
        {
            Vector3 offset = area - missile.GlobalPosition();
            PikeAreaSearchPolicy.Direction(offset.x, offset.z, course.x, course.z, out double x, out double z);
            Vector3 direction = new Vector3((float)x, 0f, (float)z);
            return missile.GlobalPosition() + direction * PikeAreaSearchPolicy.LookAhead(missile.speed);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Initialize))]
    internal static class PikeAreaSeekerInitialize
    {
        private static void Postfix(ARHSeeker __instance, ref GlobalPosition ___knownPos, ref Vector3 ___knownVel)
        {
            Missile missile = __instance.GetComponent<Missile>();
            if (!ResoluteStrikeOrders.TryGetAreaPoint(missile, out GlobalPosition area)) return;
            // Initialize already installs the native lifecycle and jam handler.
            // Only replace its default 100km forward navigation aim.
            ___knownPos = area; ___knownVel = Vector3.zero;
            missile.SetAimpoint(area, Vector3.zero);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class PikeAreaSeekerNavigation
    {
        private static bool Prefix(ARHSeeker __instance, ref bool ___armed, float ___armDelay,
            ref bool ___guidance, float ___guidanceDelay, ref float ___jamAccumulation,
            float ___jamTolerance, ref bool ___isJammed, ref GlobalPosition ___knownPos, ref Vector3 ___knownVel)
        {
            Missile missile = __instance.GetComponent<Missile>();
            if (missile == null || !missile.LocalSim || missile.targetID.IsValid ||
                !ResoluteStrikeOrders.TryGetAreaPoint(missile, out GlobalPosition area)) return true;
            // Native Seek returns before these operations when targetID is
            // empty. Keep precisely its arming, deployment and jam clocks for
            // this explicit navigation mode; never manufacture a target ID.
            if (!___armed && missile.timeSinceSpawn > ___armDelay)
            { ___armed = true; missile.Arm(); missile.SetTangible(true); }
            if (!___guidance && missile.timeSinceSpawn > ___guidanceDelay)
            { ___guidance = true; missile.DeployFins(); }
            ___jamAccumulation -= Mathf.Max(___jamAccumulation, .2f) * Mathf.Max(___jamTolerance, .1f) * Time.deltaTime;
            ___jamAccumulation = Mathf.Clamp01(___jamAccumulation);
            ___isJammed = ___jamAccumulation > ___jamTolerance;
            ___knownPos = area; ___knownVel = Vector3.zero;
            NaturalPikeGroupTargeting group = missile.GetComponent<NaturalPikeGroupTargeting>();
            missile.NetworkseekerMode = group != null && group.LeaderRadarActive
                ? Missile.SeekerMode.activeSearch : Missile.SeekerMode.passive;
            missile.SetAimpoint(___guidance ? area : missile.GlobalPosition() + missile.transform.forward * 10000f, Vector3.zero);
            return false; // The existing sea-skim postfix owns native cruise/formation navigation.
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "SlowChecks")]
    internal static class PikeAreaSeekerLifetime
    {
        private static bool Prefix(ARHSeeker __instance, float ___selfDestructAtSpeed)
        {
            Missile missile = __instance.GetComponent<Missile>();
            if (missile == null || !missile.LocalSim || missile.targetID.IsValid ||
                !ResoluteStrikeOrders.HasAreaObjective(missile)) return true;
            // A missing target and passing an area are expected here. Keep the
            // native physical minimum-speed retirement and finite motor fuel.
            if ((missile.timeSinceSpawn > 10f || !missile.EngineOn() && missile.timeSinceSpawn > 2f) &&
                missile.speed < ___selfDestructAtSpeed)
                missile.Detonate(missile.rb.velocity, false, false);
            return false;
        }
    }
}
