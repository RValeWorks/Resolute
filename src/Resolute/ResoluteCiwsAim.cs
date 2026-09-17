using System;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Preserve the donor's complete native aiming/sweep settings. The prediction
    // already compensates for ship motion, so the real gun must inherit it too.
    internal sealed class ResoluteCiwsAim : MonoBehaviour
    {
        public Turret Turret;
        public Gun Gun;
        public Transform Muzzle;

        internal static void Configure(Ship ship, Turret turret, Gun gun, Transform muzzle)
        {
            if (ship == null || turret == null || gun == null || muzzle == null)
                throw new ArgumentException("CIWS requires its owning ship, native turret, gun and muzzle.");
            // Unit.rb is initialized by OnEnable; templates are intentionally
            // inactive here. Serialize the actual owned body as a fallback so
            // Unity remaps this reference to the live ship when it is cloned.
            gun.AttachToUnit(ship);
            if (gun.velocityInherit == null) gun.velocityInherit = ship.GetComponent<Rigidbody>();
            if (gun.velocityInherit == null)
                throw new InvalidOperationException("Resolute CIWS has no owning ship rigidbody.");
            var binding = turret.gameObject.AddComponent<ResoluteCiwsAim>();
            binding.Turret = turret; binding.Gun = gun; binding.Muzzle = muzzle;
        }

        internal void BindNativeSolver(Turret turret)
        {
            if (Gun == null || Muzzle == null) return;
            Unit owner = Gun.attachedUnit;
            if (owner == null || owner.disabled) return;
            AimSolver solver = NaturalArmament.Get<AimSolver>(turret, "aimSolver");
            Unit target = NaturalArmament.Get<Unit>(turret, "target");
            // Native SetTarget uses Weapon.transform (the pitch pivot). Use the
            // actual projectile origin after fitting this different ship model.
            solver.SetTarget(owner, target, Muzzle, Gun.info);
        }

        // Called only by the once-per-second recorder bridge. Read existing
        // solver state; never advance GetAimVector or run another trajectory.
        internal object Capture(Unit target)
        {
            if (Turret == null || Gun == null || Muzzle == null) return null;
            Unit owner = Gun.attachedUnit;
            AimSolver solver = NaturalArmament.Get<AimSolver>(Turret, "aimSolver");
            Transform solverOrigin = NaturalArmament.Get<Transform>(solver, "firingTransform");
            Transform elevation = NaturalArmament.Get<Transform>(Turret, "elevationTransform");
            Vector3 aim = NaturalArmament.Get<Vector3>(Turret, "aimVector");
            Rigidbody body = owner != null ? owner.rb : null;
            Vector3 inherited = Gun.velocityInherit != null ? Gun.velocityInherit.velocity : Vector3.zero;
            Vector3 modeled = body != null && owner.speed > 1f && solverOrigin != null
                ? body.GetPointVelocity(solverOrigin.position) : Vector3.zero;
            return new {
                frame = Time.frameCount,
                inheritedBodyPresent = Gun.velocityInherit != null,
                inheritedBodyMatchesOwner = body != null && Gun.velocityInherit == body,
                inheritedVelocity = Components(inherited),
                ownerVelocity = Components(body != null ? body.velocity : Vector3.zero),
                ownerAngularVelocity = Components(body != null ? body.angularVelocity : Vector3.zero),
                nativeSimInheritedVelocityAtSnapshot = Components(modeled),
                nativeSimMinusShotVelocityAtSnapshot = Components(modeled - inherited),
                muzzleOffsetFromCentre = Components(Muzzle.position - (body != null ? body.worldCenterOfMass : Muzzle.position)),
                solverOriginIsMuzzle = solverOrigin == Muzzle,
                solverOriginMinusMuzzle = solverOrigin != null ? Components(solverOrigin.position - Muzzle.position) : null,
                solverTargetMatchesTurret = NaturalArmament.Get<Unit>(solver, "currentTarget") == target,
                muzzleForward = Components(Muzzle.forward),
                axisMismatchDegrees = elevation != null ? Vector3.Angle(elevation.forward, Muzzle.forward) : 0f,
                nativeAimVector = Components(aim),
                muzzleAimErrorDegrees = target != null && aim.sqrMagnitude > 0f ? (float?)Vector3.Angle(Muzzle.forward, aim) : null,
                targetOffsetFromMuzzle = target != null ? Components(target.transform.position - Muzzle.position) : null,
                targetVelocity = target != null && target.rb != null ? Components(target.rb.velocity) : null,
                targetSpeed = target != null ? target.speed : 0f,
                actualMuzzleVelocity = NaturalArmament.Get<float>(Gun, "muzzleVelocity"),
                configuredMuzzleVelocity = Gun.info != null ? Gun.info.muzzleVelocity : 0f,
                sweepAmount = NaturalArmament.Get<float>(solver, "rakeAmount"),
                sweepFrequency = NaturalArmament.Get<float>(solver, "rakeFrequency"),
                nativeObservedCorrectionEnabled = NaturalArmament.Get<bool>(solver, "correctShots"),
                lastSimulationGameTime = NaturalArmament.Get<float>(solver, "lastSim"),
                nativeSimulationCorrection = Components(NaturalArmament.Get<Vector3>(solver, "simCorrection")),
                nativeObservedCorrection = Components(NaturalArmament.Get<Vector3>(solver, "aimCorrection")),
                scope = "Current sampled state, not a bullet impact measurement; idle aim fields can be stale."
            };
        }

        private static float[] Components(Vector3 value) { return new[] { value.x, value.y, value.z }; }
    }

    [HarmonyPatch(typeof(Turret), "SetTarget")]
    internal static class ResoluteCiwsAimTargetPatch
    {
        private static void Postfix(Turret __instance)
        {
            ResoluteCiwsAim binding = __instance.GetComponent<ResoluteCiwsAim>();
            if (binding != null) binding.BindNativeSolver(__instance);
        }
    }
}
