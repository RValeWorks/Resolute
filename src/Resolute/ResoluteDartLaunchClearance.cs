using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // A low contact may sit below the launcher, but Dart can turn after tube
    // clearance. Elevate the native launcher enough to clear neighboring gear.
    // Query virtual poses; the native turret alone moves the actual hardware.
    [DefaultExecutionOrder(-40)]
    internal sealed class ResoluteDartLaunchClearance : MonoBehaviour
    {
        public Turret Turret;
        public MissileLauncher Launcher;
        public Transform Elevation;
        public float Minimum, Maximum;
        private float nextSample;
        private static readonly AccessTools.FieldRef<Turret, float> NativeMinimum = AccessTools.FieldRefAccess<Turret, float>("minElevation");

        internal static void Configure(Turret turret, MissileLauncher launcher, Transform elevation)
        {
            var clearance = turret.gameObject.AddComponent<ResoluteDartLaunchClearance>();
            clearance.Turret = turret; clearance.Launcher = launcher; clearance.Elevation = elevation;
        }

        // The inactive template receives its final authored arc after the
        // armament is fitted. Capture those final limits in each live clone.
        private void Awake()
        {
            Minimum = NaturalArmament.Get<float>(Turret, "minElevation");
            Maximum = NaturalArmament.Get<float>(Turret, "maxElevation");
        }

        private void FixedUpdate()
        {
            if (Turret == null || Launcher == null || Elevation == null) return;
            Unit owner = Turret.GetAttachedUnit();
            Unit target = Turret.GetTarget();
            if (owner == null || owner.disabled || !owner.LocalSim || !Turret.enabled || target == null || target.disabled || Launcher.ammo <= 0)
            { Restore(); return; }
            if (Time.timeSinceLevelLoad < nextSample) return;
            nextSample = Time.timeSinceLevelLoad + .2f;
            Transform cell = ResoluteCommandMounts.LaunchOrigin(Launcher);
            if (cell == null) { Restore(); return; }
            float current = -Mathf.DeltaAngle(0f, Elevation.localEulerAngles.x);
            float chosen = Minimum;
            for (float pitch = Minimum; pitch <= Maximum + .01f; pitch += 5f)
            {
                Quaternion turn = Quaternion.AngleAxis(current - pitch, Turret.transform.right);
                Vector3 position = Elevation.position + turn * (cell.position - Elevation.position);
                Vector3 direction = turn * cell.forward;
                chosen = Mathf.Min(pitch, Maximum);
                if (ResoluteCommandMounts.ClearRay(position, direction, target)) break;
            }
            NativeMinimum(Turret) = chosen;
        }

        private void Restore() { if (Turret != null) NativeMinimum(Turret) = Minimum; }
        private void OnDisable() { Restore(); }
    }

    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class ResoluteDartPhysicalLaunchGate
    {
        private static bool Prefix(MissileLauncher __instance, Unit owner, Unit target)
        {
            if (owner == null || !owner.LocalSim || NaturalMissileTargeting.Key(__instance.info) != "rsl_pd") return true;
            // Cover automatic native turret fire as well as public commands.
            // The stock turret checks the pitch pivot, not the occupied tube.
            return ResoluteCommandMounts.ClearCell(__instance, target);
        }
    }
}
