using System;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Use the native ballistic solver and native bullet flight, with the
    // fitted gun's real origin and motion. No extra per-frame trajectory sim.
    internal sealed class ResoluteRailgunAim : MonoBehaviour
    {
        public Gun Gun;
        public Transform Muzzle;
        private Unit previousTarget;
        private bool targetInitialized;

        private static readonly AccessTools.FieldRef<Turret, AimSolver> Solver =
            AccessTools.FieldRefAccess<Turret, AimSolver>("aimSolver");
        private static readonly AccessTools.FieldRef<AimSolver, Vector3> PreviousVelocity =
            AccessTools.FieldRefAccess<AimSolver, Vector3>("targetVelPrev");
        private static readonly AccessTools.FieldRef<AimSolver, Vector3> Acceleration =
            AccessTools.FieldRefAccess<AimSolver, Vector3>("targetAccelSmoothed");
        private static readonly AccessTools.FieldRef<AimSolver, Vector3> AccelerationVelocity =
            AccessTools.FieldRefAccess<AimSolver, Vector3>("targetAccelSmoothingVel");

        internal static void Configure(Ship ship, Turret turret, Gun gun)
        {
            Transform[] muzzles = NaturalArmament.Get<Transform[]>(gun, "muzzles");
            if (ship == null || turret == null || muzzles == null || muzzles.Length != 1 || muzzles[0] == null)
                throw new InvalidOperationException("Resolute railgun requires its fitted native muzzle.");
            gun.AttachToUnit(ship);
            gun.velocityInherit = ship.GetComponent<Rigidbody>();
            if (gun.velocityInherit == null) throw new InvalidOperationException("Resolute railgun requires its ship body.");
            var binding = turret.gameObject.AddComponent<ResoluteRailgunAim>();
            binding.Gun = gun; binding.Muzzle = muzzles[0];
            AimSolver solver = Solver(turret);
            // Native artillery mode returns before all movement compensation
            // above 20% range. Native direct-fire mode still simulates gravity
            // and drag; it also leads moving ground, sea and air targets.
            NaturalArmament.Set(solver, "artillery", false);
            // Sweeping the target velocity is appropriate for a CIWS burst,
            // not an individually aimed railgun round several seconds apart.
            NaturalArmament.Set(solver, "rakeAmount", 0f);
        }

        internal void Bind(Turret turret)
        {
            if (Gun == null || Muzzle == null || Gun.attachedUnit == null) return;
            Unit target = turret.GetTarget();
            AimSolver solver = Solver(turret);
            solver.SetTarget(Gun.attachedUnit, target, Muzzle, Gun.info);
            if (!targetInitialized || previousTarget != target)
            {
                // AimSolver resets its smoothed acceleration but not the
                // previous velocity. Avoid inventing acceleration when the
                // gun switches to a different already-moving target.
                PreviousVelocity(solver) = target != null && target.rb != null && target.speed >= 1f
                    ? target.rb.velocity : Vector3.zero;
                Acceleration(solver) = AccelerationVelocity(solver) = Vector3.zero;
                previousTarget = target; targetInitialized = true;
            }
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.SetTarget))]
    internal static class ResoluteRailgunAimTargetPatch
    {
        private static void Postfix(Turret __instance)
        {
            var binding = __instance.GetComponent<ResoluteRailgunAim>();
            if (binding != null) binding.Bind(__instance);
        }
    }
}
