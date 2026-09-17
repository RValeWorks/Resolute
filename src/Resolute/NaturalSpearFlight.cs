using System;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // The native optical seeker remains the only waypoint/formation/terminal
    // authority. Adapt its actual flight package to Spear's authored mass and
    // endurance rather than feeding native guidance a generic missile motor.
    internal sealed class NaturalSpearFlight : MonoBehaviour
    {
        public float NativeTurnRate, LaunchTurnRate, InitialFlightSeconds, LaunchClearance, LaunchGuidanceDelay;
        public float NativeGLimit, LaunchGLimit;
        public float MassScale, NativeFinArea, NativeFuelMass, NativeThrust;
        public int NativeMotorStages;
        private Missile missile;
        private GlobalPosition launchPosition;
        private Vector3 launchAxis;
        private bool initialized, rateApplied;
        private static readonly AccessTools.FieldRef<Missile, float> MaximumTurnRate =
            AccessTools.FieldRefAccess<Missile, float>("maxTurnRate");
        private static readonly AccessTools.FieldRef<Missile, float> MaximumGLimit =
            AccessTools.FieldRefAccess<Missile, float>("gLimit");

        internal static void Configure(Missile missile, NaturalWeapons.SourceWeapon source, Missile donor)
        {
            float nativeMass = (float)NaturalWeapons.Get(donor, "mass");
            float nativeArea = (float)NaturalWeapons.Get(donor, "finArea");
            Array originals = (Array)NaturalWeapons.Get(missile, "motors");
            if (nativeMass <= 0f || nativeArea <= 0f || originals == null || originals.Length == 0)
                throw new InvalidOperationException("Spear native cruise donor has invalid flight inputs: mass=" + nativeMass +
                    ", finArea=" + nativeArea + ", motorCount=" + (originals != null ? originals.Length : 0));
            VLSBooster nativeBooster = donor.GetComponentInChildren<VLSBooster>(true);
            VLSBooster spearBooster = missile.GetComponentInChildren<VLSBooster>(true);
            float nativeFlightMass = nativeMass - (nativeBooster != null ? (float)NaturalWeapons.Get(nativeBooster, "fuelMass") : 0f);
            float flightMass = source.massKg - (spearBooster != null ? (float)NaturalWeapons.Get(spearBooster, "fuelMass") : 0f);
            if (nativeFlightMass <= 0f || flightMass <= 0f)
                throw new InvalidOperationException("Spear booster leaves no cruise flight mass: native=" + nativeFlightMass +
                    ", Spear=" + flightMass + ", authoredMass=" + source.massKg);
            float massScale = flightMass / nativeFlightMass;
            NaturalWeapons.Set(missile, "finArea", nativeArea * massScale);
            // Lift and drag curves, upright preference, angular drag, PID,
            // torque, turn/g caps and cloned folding fins are retained intact.
            // Matching force/area per kg preserves native acceleration at the
            // same speed; the authored speed remains a ceiling, not a velocity
            // rewrite. Extending fuel endurance does not add extra fuel mass.
            Array motors = Array.CreateInstance(originals.GetType().GetElementType(), originals.Length);
            float fuelMass = 0f, thrust = 0f;
            for (int i = 0; i < originals.Length; i++)
            {
                object original = originals.GetValue(i);
                object motor = NaturalWeapons.CopyManaged(original);
                float fuel = (float)NaturalWeapons.Get(original, "fuelMass");
                float force = (float)NaturalWeapons.Get(original, "thrust");
                fuelMass += fuel; thrust = force;
                NaturalWeapons.Set(motor, "fuelMass", fuel * massScale);
                NaturalWeapons.Set(motor, "thrust", force * massScale);
                NaturalWeapons.Set(motor, "topSpeed", source.speedMps);
                if (i == originals.Length - 1)
                    NaturalWeapons.Set(motor, "burnTime", Mathf.Max((float)NaturalWeapons.Get(original, "burnTime"),
                        source.maxRangeM / Mathf.Max(1f, source.speedMps) * 1.2f));
                NaturalWeapons.Set(motor, "activated", false);
                NaturalWeapons.Set(motor, "burnRate", 0f);
                motors.SetValue(motor, i);
            }
            if (fuelMass >= nativeFlightMass)
                throw new InvalidOperationException("Spear native cruise donor consumes its entire flight mass as fuel: fuel=" +
                    fuelMass + ", nativeFlightMass=" + nativeFlightMass);
            NaturalWeapons.Set(missile, "motors", motors);
            var profile = missile.gameObject.AddComponent<NaturalSpearFlight>();
            missile.gameObject.AddComponent<NaturalSpearLaunchBearing>();
            profile.NativeTurnRate = (float)NaturalWeapons.Get(donor, "maxTurnRate");
            profile.NativeGLimit = (float)NaturalWeapons.Get(donor, "gLimit");
            NaturalSurfaceLaunch launch = missile.GetComponent<NaturalSurfaceLaunch>();
            profile.LaunchTurnRate = launch != null ? launch.TurnRate : profile.NativeTurnRate;
            profile.LaunchGLimit = launch != null ? launch.GLimit : profile.NativeGLimit;
            profile.InitialFlightSeconds = launch != null ? Mathf.Max(launch.GuidanceDelay, launch.IgnitionDelay + launch.BurnTime) : 0f;
            profile.LaunchGuidanceDelay = launch != null ? launch.GuidanceDelay : 0f;
            profile.LaunchClearance = Mathf.Max(1f, source.stages["launch"].max[2] - source.stages["launch"].min[2]);
            OpticalSeekerCruiseMissile seeker = missile.GetComponent<OpticalSeekerCruiseMissile>();
            // An air-launched donor's initial wait is replaced only by the
            // real ship-launch donor's wait. All later native optical flight
            // phases, fin deployment mechanics and waypoints remain intact.
            if (seeker != null && launch != null) NaturalWeapons.Set(seeker, "guidanceDelay", launch.GuidanceDelay);
            profile.MassScale = massScale; profile.NativeFinArea = nativeArea;
            profile.NativeFuelMass = fuelMass; profile.NativeThrust = thrust;
            profile.NativeMotorStages = originals.Length;
        }

        private void Awake() { missile = GetComponent<Missile>(); }

        private void FixedUpdate()
        {
            if (missile == null || missile.disabled || missile.timeSinceSpawn >= InitialFlightSeconds)
            {
                Restore(); enabled = false; return;
            }
            if (!missile.LocalSim || missile.rb == null || missile.owner == null) return;
            if (!initialized)
            {
                initialized = true;
                launchPosition = missile.GlobalPosition(); launchAxis = transform.forward;
                if (!(missile.owner is Ship) || launchAxis.y < .55f ||
                    (LaunchTurnRate == NativeTurnRate && LaunchGLimit == NativeGLimit))
                { enabled = false; return; }
            }
            // Native guidance still supplies the direction. Keep the real
            // ship-launch donor's rate/g pair together, including zero/zero
            // (no explicit limiter), briefly after clearing the ship.
            if (!rateApplied && missile.timeSinceSpawn >= LaunchGuidanceDelay &&
                Vector3.Dot(missile.GlobalPosition() - launchPosition, launchAxis) >= LaunchClearance)
            {
                MaximumTurnRate(missile) = LaunchTurnRate;
                MaximumGLimit(missile) = LaunchGLimit; rateApplied = true;
            }
        }

        private void Restore()
        {
            if (rateApplied && missile != null)
            {
                MaximumTurnRate(missile) = NativeTurnRate;
                MaximumGLimit(missile) = NativeGLimit;
            }
            rateApplied = false;
        }
        private void OnDisable() { Restore(); }
        private void OnDestroy() { Restore(); }
    }
}
