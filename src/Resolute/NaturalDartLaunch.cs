using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Boltstrike's RAM-45 turns during a short cold-launch coast before its
    // main motor starts. Dart and Ward share that short launch sequence,
    // retaining their own motors, seekers and normal-flight limits.
    [DefaultExecutionOrder(-100)]
    internal sealed class NaturalDartLaunch : MonoBehaviour
    {
        public float IgnitionDelay, LaunchTurnRate, FlightTurnRate;
        public Vector3 EjectionVelocity;
        public bool RadarLaunch;
        public float LaunchTorque, FlightTorque;
        internal const float WardIgnitionDelay = .75f;
        private Missile missile;
        private Bounds launchBounds;
        private bool hasLaunchBounds, clearOfTube, radarCueValidated;
        private static readonly AccessTools.FieldRef<Missile, float> TurnRate =
            AccessTools.FieldRefAccess<Missile, float>("maxTurnRate");
        private static readonly AccessTools.FieldRef<Missile, Vector3> AngularRate =
            AccessTools.FieldRefAccess<Missile, Vector3>("localAngularVel");
        private static readonly AccessTools.FieldRef<Missile, float> Torque =
            AccessTools.FieldRefAccess<Missile, float>("torque");
        private static readonly AccessTools.FieldRef<Missile, float> GLimit =
            AccessTools.FieldRefAccess<Missile, float>("gLimit");

        internal static void Configure(Missile missile, Encyclopedia encyclopedia)
        {
            MissileDefinition definition = encyclopedia.missiles.Single(d => d != null && d.jsonKey == "SAM_Radar1");
            Missile donor = definition.unitPrefab.GetComponent<Missile>();
            SARHSeeker seeker = definition.unitPrefab.GetComponent<SARHSeeker>();
            VehicleDefinition vehicle = Resources.FindObjectsOfTypeAll<VehicleDefinition>().Single(d => d.jsonKey == "RadarSAM1");
            MissileLauncher launcher = vehicle.unitPrefab.GetComponentsInChildren<MissileLauncher>(true).First(l => l.missile == definition);
            Array donorMotors = (Array)NaturalWeapons.Get(donor, "motors");
            if (donorMotors.Length != 2 || seeker == null)
                throw new InvalidOperationException("Dart requires the native Boltstrike cold-launch profile.");
            float delay = (float)NaturalWeapons.Get(donorMotors.GetValue(0), "delayTimer") +
                (float)NaturalWeapons.Get(donorMotors.GetValue(0), "burnTime") +
                (float)NaturalWeapons.Get(donorMotors.GetValue(1), "delayTimer");
            if (!(delay > 0f && delay < 3f))
                throw new InvalidOperationException("The native Boltstrike launch delay is outside the verified profile.");
            var profile = missile.gameObject.AddComponent<NaturalDartLaunch>();
            profile.RadarLaunch = missile.GetComponent<ARHSeeker>() != null;
            profile.IgnitionDelay = Mathf.Min(delay, WardIgnitionDelay);
            profile.LaunchTurnRate = TurnRate(donor);
            profile.FlightTurnRate = TurnRate(missile);
            profile.LaunchTorque = Torque(donor) * 2f;
            profile.FlightTorque = Torque(missile);
            profile.EjectionVelocity = (Vector3)NaturalWeapons.Get(launcher, "ejectionVelocity");
            Array ownMotors = (Array)NaturalWeapons.Get(missile, "motors");
            NaturalWeapons.Set(ownMotors.GetValue(0), "delayTimer", profile.IgnitionDelay);
            NaturalWeapons.Set(missile.GetComponent<MissileSeeker>(), "guidanceDelay", (float)NaturalWeapons.Get(seeker, "guidanceDelay"));
            // Native turret prediction should use the actual launch speed too.
            missile.GetWeaponInfo().muzzleVelocity = profile.EjectionVelocity.magnitude;
        }

        private void Awake()
        {
            missile = GetComponent<Missile>();
            TurnRate(missile) = LaunchTurnRate;
            Torque(missile) = LaunchTorque;
            NaturalWeaponPhase phase = GetComponent<NaturalWeaponPhase>();
            if (phase != null && phase.LaunchMesh != null && phase.LaunchMesh.sharedMesh != null)
            {
                launchBounds = phase.LaunchMesh.sharedMesh.bounds;
                hasLaunchBounds = true;
            }
        }
        private void FixedUpdate()
        {
            if (missile == null || missile.disabled || missile.timeSinceSpawn < IgnitionDelay) return;
            Vector3 rate = AngularRate(missile);
            // Do not suddenly put a still-turning body above a lower native
            // cap: ApplyAero's over-cap/opposing-input branch can amplify that
            // rate. Hand over once the native controller has settled the turn.
            // Keep launch braking torque until this handoff too. Cutting it
            // at ignition halves the available braking while the body is
            // still pitching over, letting a low-target launch nose past the
            // horizon. The native PID applies the counter-torque itself.
            float cap = Mathf.Min(FlightTurnRate * Mathf.Deg2Rad,
                9.81f * GLimit(missile) / Mathf.Max(missile.speed, 1f));
            if (Mathf.Abs(rate.x) > cap || Mathf.Abs(rate.y) > cap) return;
            Torque(missile) = FlightTorque;
            TurnRate(missile) = FlightTurnRate;
            enabled = false;
        }
        internal void CueLaunchBearing(Unit nativeTarget, GlobalPosition nativePosition, Vector3 nativeVelocity)
        {
            if (!enabled || missile == null || !missile.LocalSim || missile.disabled ||
                missile.timeSinceSpawn < 0f || missile.timeSinceSpawn >= IgnitionDelay ||
                missile.targetID.NotValid || nativeTarget == null || nativeTarget.disabled || !hasLaunchBounds) return;
            if (!clearOfTube)
            {
                Transform body = missile.transform;
                clearOfTube = NaturalFinDeployment.ClearedPlane(missile.GlobalPosition() - missile.startPosition,
                    missile.startRotation * Vector3.forward, body.right, body.up, body.forward, launchBounds);
                if (!clearOfTube)
                {
                    // Lead against a fast inbound target can lie behind the
                    // launcher before there is forward missile speed. Prevent
                    // that early opposite turn from loading the PID while the
                    // missile is still inside the tube.
                    missile.SetAimpoint(missile.GlobalPosition() +
                        missile.startRotation * Vector3.forward * 10000f, Vector3.zero);
                    return;
                }
            }
            if (!Finite(nativePosition.x) || !Finite(nativePosition.y) || !Finite(nativePosition.z)) return;
            if (RadarLaunch && !radarCueValidated)
            {
                // Native Initialize leaves a default position if its initial
                // HQ lookup fails. A target ID alone cannot validate that cue.
                // Once accepted, ordinary seeker memory remains authoritative.
                radarCueValidated = missile.seekerMode == Missile.SeekerMode.activeLock ||
                    missile.NetworkHQ != null && missile.NetworkHQ.TryGetKnownPosition(nativeTarget, out _);
                if (!radarCueValidated) return;
            }
            // Cold ejection is a turn toward the native seeker cue, before
            // loft and powered interception. Do not use owner.forward or a
            // live target transform: native track memory and IR lock still own
            // the target position. This also works abeam and astern of the ship.
            // Preserve native ARH's final sea-level bound. Its retained 3-D
            // datalink error can otherwise put a low contact below the water.
            nativePosition.y = Mathf.Max(0f, nativePosition.y);
            missile.SetAimpoint(nativePosition, nativeVelocity);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal bool AwaitingIgnition(Missile item) => item != null && !item.disabled &&
            item.timeSinceSpawn >= 0f && item.timeSinceSpawn <= IgnitionDelay + .1f &&
            item.GetRemainingBurnTime() > 0f;
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class NaturalWardLaunchBearing
    {
        private static void Postfix(Missile ___missile, Unit ___targetUnit, GlobalPosition ___knownPos, Vector3 ___knownVel, bool ___guidance)
        {
            if (___guidance && ___missile != null && ___missile.timeSinceSpawn < NaturalDartLaunch.WardIgnitionDelay)
                ___missile.GetComponent<NaturalDartLaunch>()?.CueLaunchBearing(___targetUnit, ___knownPos, ___knownVel);
        }
    }

    [HarmonyPatch(typeof(IRSeeker), nameof(IRSeeker.Seek))]
    internal static class NaturalDartLaunchBearing
    {
        private static void Postfix(Missile ___missile, Unit ___targetUnit, IRSource ___IRTarget,
            GlobalPosition ___knownPos, Vector3 ___knownVel, bool ___guidance)
        {
            // A rejected/lost IR lock never receives an HQ steering substitute.
            if (___guidance && ___missile != null && ___missile.timeSinceSpawn < NaturalDartLaunch.WardIgnitionDelay &&
                ___IRTarget != null && ___IRTarget.transform != null)
                ___missile.GetComponent<NaturalDartLaunch>()?.CueLaunchBearing(___targetUnit, ___knownPos, ___knownVel);
        }
    }

    [HarmonyPatch(typeof(IRSeeker), "SlowChecks")]
    internal static class NaturalDartIgnitionGrace
    {
        private static bool Prefix(Missile ___missile)
        {
            NaturalDartLaunch profile = ___missile != null ? ___missile.GetComponent<NaturalDartLaunch>() : null;
            // RAM-45's SARH retirement check waits for arming; native IR does
            // not. Permit only this configured pre-ignition coast, otherwise
            // IR would retire every 20 m/s Dart at its first .5-second check.
            // A rejected IR lock must not cancel the scheduled motor startup.
            // This short grace never supplies a target or changes acquisition.
            return profile == null || !profile.AwaitingIgnition(___missile);
        }
    }
}
