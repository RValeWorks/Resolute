using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Lance alone replaces the donor's aerodynamic controller. Native seekers,
    // networking, swept collisions, countermeasures and damage remain in place.
    internal sealed class NaturalLanceFlight : MonoBehaviour
    {
        internal const string Key = "rsl_scramjet";
        public VLSBooster Booster;
        public Vector3 FlightMinimum, FlightMaximum;
        public float NativeJammingIntensity;
        private static readonly Dictionary<Missile, NaturalLanceFlight> Live = new Dictionary<Missile, NaturalLanceFlight>();
        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> Aim = AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");
        private static readonly AccessTools.FieldRef<ARHSeeker, GlobalPosition> Known = AccessTools.FieldRefAccess<ARHSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> KnownVelocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVel");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> Guidance = AccessTools.FieldRefAccess<ARHSeeker, bool>("guidance");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> Locked = AccessTools.FieldRefAccess<ARHSeeker, bool>("radarLockEstablished");
        private static readonly AccessTools.FieldRef<Missile, float> EngineForce = AccessTools.FieldRefAccess<Missile, float>("engineCurrentThrust");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> JamTolerance = AccessTools.FieldRefAccess<ARHSeeker, float>("jamTolerance");
        private Missile missile;
        private ARHSeeker seeker;
        private LanceVisuals visuals;
        private object motor;
        private readonly LanceManeuverState turn = new LanceManeuverState();
        private GlobalPosition origin;
        private Vector3 previousVelocity, airVelocity, desiredForceG, lastDirection;
        private bool started, separated, failedBoost, previousSample, usableEstimate, radarThreatReceived;
        private bool observerKnown, observerMotorStopped;
        private LancePresentationState observer;
        private float density, airspeed, altitude, horizontal, lastThrust, separationAge, nextLog, originalJamTolerance;
        private float actualG, dragForce, actualLift, targetRange, predictedArrival;
        private int previousTimeouts;
        private string phase = "unlaunched";

        internal static bool Try(Missile value, out NaturalLanceFlight flight)
        { flight = null; return value != null && Live.TryGetValue(value, out flight) && flight != null; }

        internal static void ConfigurePropulsion(Missile missile, NaturalWeapons.SourceWeapon source, Encyclopedia encyclopedia)
        {
            NaturalSpearBooster.Configure(missile, source, missile, encyclopedia);
            VLSBooster booster = missile.GetComponentInChildren<VLSBooster>(true);
            if (booster == null) throw new InvalidOperationException("Lance requires a detachable native VLS booster.");
            booster.transform.localPosition = Vector3.zero;
            booster.transform.localRotation = Quaternion.identity;
            booster.transform.localScale = Vector3.one;
            NaturalWeapons.Set(booster, "missile", missile);
            NaturalWeapons.Set(booster, "fuelMass", (float)LanceFlightModel.BoosterFuel);
            NaturalWeapons.Set(booster, "dryMass", (float)LanceFlightModel.BoosterDryMass);
            NaturalWeapons.Set(booster, "thrust", (float)LanceFlightModel.BoostThrust);
            NaturalWeapons.Set(booster, "burnTime", (float)(LanceFlightModel.BoosterFuel * LanceFlightModel.BoostExhaustSpeed / LanceFlightModel.BoostThrust));
            NaturalWeapons.Set(booster, "delayTimer", 0f);
            NaturalWeapons.Set(booster, "activated", false);
            NaturalWeapons.Set(booster, "separated", false);
            Array original = (Array)NaturalWeapons.Get(missile, "motors");
            Array motors = Array.CreateInstance(original.GetType().GetElementType(), 1);
            object cruise = NaturalWeapons.CopyManaged(original.GetValue(original.Length - 1));
            NaturalWeapons.Set(cruise, "fuelMass", (float)LanceFlightModel.CruiseFuel);
            NaturalWeapons.Set(cruise, "thrust", (float)LanceFlightModel.CruiseStaticThrust);
            NaturalWeapons.Set(cruise, "burnTime", 600f);
            NaturalWeapons.Set(cruise, "topSpeed", float.MaxValue);
            NaturalWeapons.Set(cruise, "delayTimer", 0f);
            NaturalWeapons.Set(cruise, "activated", false);
            NaturalWeapons.Set(cruise, "burnRate", 0f);
            motors.SetValue(cruise, 0); NaturalWeapons.Set(missile, "motors", motors);
            NaturalWeapons.Set(missile, "gLimit", 40f);
            NaturalWeapons.Set(missile, "finArea", (float)LanceFlightModel.ControlArea);
            NaturalWeapons.Set(missile, "maxTurnRate", 180f);
            // The owned drag law is used during flight. Keep a consistent
            // zero-incidence donor coefficient for native UI estimates.
            NaturalWeapons.Set(missile, "dragCurve", AnimationCurve.Linear(0, (float)(LanceFlightModel.CruiseDragArea / LanceFlightModel.ControlArea), 1, .5f));
            var controller = missile.gameObject.AddComponent<NaturalLanceFlight>();
            controller.Booster = booster;
            if (source.Text("OnBoardSystems", "ECM") == "rsl_missile_ecm")
            {
                // Same native defensive ECM scale as the existing missile
                // adapter. Lance does not inherit Pike's flares or group ECM.
                RadarJammer donor = encyclopedia.aircraft.Where(d => d != null && d.unitPrefab != null && !d.jsonKey.StartsWith("rsl_"))
                    .OrderBy(d => d.jsonKey == "Fighter1" ? 0 : 1).ThenBy(d => d.jsonKey, StringComparer.Ordinal)
                    .Select(d => d.unitPrefab.GetComponentInChildren<RadarJammer>(true))
                    .FirstOrDefault(j => j != null && j.GetMaxJammingIntensity() > 0);
                if (donor == null) throw new InvalidOperationException("Lance requires an installed native defensive ECM reference.");
                controller.NativeJammingIntensity = donor.GetMaxJammingIntensity();
            }
            var bounds = source.stages["flight"];
            controller.FlightMinimum = new Vector3(bounds.min[0], bounds.min[1], bounds.min[2]);
            controller.FlightMaximum = new Vector3(bounds.max[0], bounds.max[1], bounds.max[2]);
            Rigidbody body = missile.GetComponent<Rigidbody>();
            body.drag = 0f; body.angularDrag = .1f;
        }

        private void Awake()
        {
            missile = GetComponent<Missile>(); seeker = GetComponent<ARHSeeker>(); visuals = GetComponent<LanceVisuals>();
            if (missile == null) return;
            Live[missile] = this;
            motor = ((Array)NaturalWeapons.Get(missile, "motors")).GetValue(0);
            if (seeker != null) originalJamTolerance = JamTolerance(seeker);
            missile.onInitialize += Initialize;
        }

        private void Initialize()
        {
            started = true; origin = missile.GlobalPosition();
            turn.Seed = missile.persistentID.Id | 1u;
            turn.AttitudeUp = turn.UprightReference = transform.up;
            lastDirection = transform.forward;
            if (missile.LocalSim) missile.rb.useGravity = true;
        }

        private void OnDestroy()
        {
            LancePresentationNetworking.Remove(missile);
            if (missile != null) { Live.Remove(missile); missile.onInitialize -= Initialize; }
        }

        internal void BeginPhysics()
        {
            if (!started || missile.disabled || missile.rb == null) return;
            Vector3 velocity = missile.rb.velocity;
            altitude = (float)missile.GlobalPosition().y;
            density = Mathf.Max(0, GameAssets.i.airDensityAltitude.Evaluate(altitude * .001f));
            Vector3 wind = NetworkSceneSingleton<LevelInfo>.i != null ? NetworkSceneSingleton<LevelInfo>.i.GetWind(missile.GlobalPosition()) : Vector3.zero;
            airVelocity = velocity - wind; airspeed = airVelocity.magnitude;
            Vector3 displacement = missile.GlobalPosition() - origin; displacement.y = 0;
            horizontal = displacement.magnitude;
            if (!missile.LocalSim)
            {
                // Smoothed observer positions can skip the narrow separation
                // gate. Only the host's separate presentation message decides
                // the visual stage; native snapshots retain all motion.
                if (LancePresentationNetworking.TryGet(missile, out observer))
                {
                    observerKnown = true;
                    if (observer.Separated && !separated)
                    { failedBoost = observer.Failed; Separate(observer.Failed); }
                    if (observer.Separated && !observer.CruiseActive && !observerMotorStopped)
                    {
                        observerMotorStopped = true;
                        AccessTools.Method(motor.GetType(), "Burnout").Invoke(motor, new object[] { true });
                    }
                }
                return;
            }
            if (previousSample && missile.LocalSim)
            {
                turn.MeasuredG = Vector3.ProjectOnPlane((velocity - previousVelocity) / Mathf.Max(.001f, Time.fixedDeltaTime), lastDirection) / (float)LanceFlightModel.G;
                actualG = turn.MeasuredG.magnitude;
            }
            previousVelocity = velocity; previousSample = true;
            lastDirection = velocity.sqrMagnitude > 1 ? velocity.normalized : transform.forward;
            if (!separated && !failedBoost && LanceFlightModel.SeparationReady(horizontal, altitude, airspeed)) Separate(false);
            if (seeker != null) JamTolerance(seeker) = originalJamTolerance - .05f * Sheath;
            if (ResoluteDiagnostics.Enabled && missile.LocalSim && Time.timeSinceLevelLoad >= nextLog)
            {
                nextLog = Time.timeSinceLevelLoad + 1f;
                NaturalMissileDiagnostics.GuidanceEvent(missile, "lance-physics", Capture());
            }
        }

        internal bool PrepareMotor()
        {
            if (!started) return true;
            if (!missile.LocalSim)
            {
                if (!observerKnown || !observer.CruiseActive)
                { EngineForce(missile) = 0; return false; }
                // Native Motor.Thrust also consumes fuel on observers. Keep
                // its activation/audio/particles, but let the host's finite
                // fuel clock determine when those effects stop.
                lastThrust = (float)LanceFlightModel.CruiseThrust(density, airspeed);
                NaturalWeapons.Set(motor, "thrust", lastThrust);
                NaturalWeapons.Set(motor, "burnRate", 0f);
                NaturalWeapons.Set(motor, "burnTime", float.MaxValue);
                return true;
            }
            if (!separated) { EngineForce(missile) = lastThrust; return false; }
            if (failedBoost) { EngineForce(missile) = 0; return false; }
            float remaining = (float)NaturalWeapons.Get(motor, "fuelMass");
            float ignition = Mathf.SmoothStep(0, 1, (missile.timeSinceSpawn - separationAge) / .25f);
            lastThrust = (float)LanceFlightModel.CruiseThrust(density, airspeed) * ignition;
            float flow = lastThrust / (float)LanceFlightModel.CruiseExhaustSpeed;
            flow = Mathf.Min(flow, Mathf.Max(0, remaining) / Mathf.Max(.001f, Time.fixedDeltaTime));
            NaturalWeapons.Set(motor, "thrust", lastThrust);
            NaturalWeapons.Set(motor, "burnRate", flow);
            NaturalWeapons.Set(motor, "burnTime", remaining / Mathf.Max(.000001f, flow));
            return true; // Native Motor.Thrust owns fuel/mass, IR, particles and thrust.
        }

        internal float Boost()
        {
            if (!started || separated || missile.disabled || Booster == null) return 0;
            // Remote boosters retain native particles/audio, but do not run a
            // second fuel clock or guess separation independently of the host.
            if (!missile.LocalSim)
            { lastThrust = observerKnown && observer.BoostActive ? (float)LanceFlightModel.BoostThrust : 0; return lastThrust; }
            float fuel = (float)NaturalWeapons.Get(Booster, "fuelMass");
            float dt = Time.fixedDeltaTime;
            lastThrust = (float)LanceFlightModel.BoostThrust * Mathf.SmoothStep(0, 1, missile.timeSinceSpawn / .35f);
            float used = (float)LanceFlightModel.FuelUsed(lastThrust, LanceFlightModel.BoostExhaustSpeed, fuel, dt);
            NaturalWeapons.Set(Booster, "fuelMass", fuel - used);
            missile.rb.mass -= used;
            if (fuel <= used)
            {
                failedBoost = true; Separate(true); lastThrust = 0;
                NaturalMissileDiagnostics.GuidanceEvent(missile, "lance-boost-exhausted-before-gate", Capture());
                return 0;
            }
            if (missile.LocalSim) missile.rb.AddForce(lastThrust * transform.forward);
            EngineForce(missile) = lastThrust;
            return lastThrust;
        }

        private void Separate(bool failed)
        {
            if (separated || Booster == null) return;
            separated = true; separationAge = missile.timeSinceSpawn;
            float dropped = (float)NaturalWeapons.Get(Booster, "fuelMass") + (float)LanceFlightModel.BoosterDryMass;
            missile.rb.mass = Mathf.Max(1, missile.rb.mass - dropped);
            NaturalWeapons.Set(Booster, "dryMass", dropped);
            NaturalWeapons.Set(Booster, "fuelMass", 0f);
            Booster.Burnout();
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                capsule.center = (FlightMinimum + FlightMaximum) * .5f;
                capsule.height = Mathf.Max(capsule.radius * 2, FlightMaximum.z - FlightMinimum.z);
            }
            NaturalMissileDiagnostics.GuidanceEvent(missile, failed ? "lance-failed-separation" : "lance-separation", Capture());
        }

        internal bool Guide()
        {
            if (!started || missile.disabled || !missile.LocalSim) return false;
            float dt = Time.fixedDeltaTime;
            Vector3 forward = airspeed > 1 ? airVelocity / airspeed : transform.forward;
            turn.Before = forward;
            GlobalPosition tracked;
            bool coordinate = ResoluteLancePosition.TryGetPoint(missile, out tracked);
            usableEstimate = coordinate || seeker != null && missile.targetID.IsValid && Guidance(seeker) &&
                (!Locked(seeker) || missile.seekerMode == Missile.SeekerMode.activeLock);
            if (!coordinate && seeker != null) tracked = Known(seeker);
            Vector3 native = Aim(missile) - missile.GlobalPosition();
            if (!Finite(native) || native.sqrMagnitude < .001f) native = forward;
            Vector3 objective = native.normalized;
            desiredForceG = Vector3.zero;
            bool clear = separated || missile.timeSinceSpawn > 1f && altitude >= origin.y + 20f;
            if (!clear) { phase = "native-vls-departure"; return true; }
            if (!usableEstimate || failedBoost)
            {
                phase = "native-target-memory";
                turn.Evasive = false;
            }
            else
            {
                Vector3 offset = tracked - missile.GlobalPosition();
                targetRange = offset.magnitude;
                // Recompute from fresh known position and physical current
                // speed, never from a steering waypoint or hidden target.
                predictedArrival = targetRange / Mathf.Max(airspeed, 1);
                turn.Range = targetRange; turn.TimeToTarget = predictedArrival;
                Vector3 flat = native; flat.y = 0;
                if (flat.sqrMagnitude < .001f) flat = Vector3.ProjectOnPlane(forward, Vector3.up);
                if (flat.sqrMagnitude < .001f) flat = Vector3.forward;
                flat.Normalize();
                if (!separated)
                {
                    float wanted = (float)LanceFlightModel.BoostHeight(origin.y, horizontal);
                    float slope = (float)LanceFlightModel.BoostSlope(origin.y, horizontal);
                    objective = (flat + Vector3.up * (slope + Mathf.Clamp((wanted - altitude) / 1800f, -.8f, .8f))).normalized;
                    phase = "curved-boost";
                }
                else
                {
                    Vector3 targetVelocity = coordinate || seeker == null ? Vector3.zero : KnownVelocity(seeker);
                    objective = LanceApproach.Navigate(turn, native, offset, airVelocity, targetVelocity,
                        altitude, density, missile.rb.mass);
                    phase = turn.Diving ? "steep-dive" : "high-cruise";
                }
                // Native retirement sees the active navigation leg while
                // climbing, rather than falsely treating the ground below as
                // already passed. Each next native Seek refreshes its own aim.
                if (!separated || targetRange > 12 * 1852f)
                    missile.SetAimpoint(missile.GlobalPosition() + objective * Mathf.Max(2000, airspeed * 2), Vector3.zero);
                turn.Evasive = separated && !coordinate && usableEstimate;
                // Fixed coordinates remain legitimate estimates for evasion.
                if (coordinate && separated) turn.Evasive = true;
            }
            turn.Warning = radarThreatReceived;
            if (!separated)
            {
                Vector3 transverse = Vector3.ProjectOnPlane(objective, forward);
                float angle = Vector3.Angle(forward, objective) * Mathf.Deg2Rad;
                desiredForceG = transverse.normalized * Mathf.Min(25, angle * airspeed / ((float)LanceFlightModel.G * .4f));
                turn.CommandG = desiredForceG.magnitude;
                // Boost remains upright without a discontinuity at vertical.
                Vector3 up = Vector3.ProjectOnPlane(Vector3.up, forward);
                if (up.sqrMagnitude > .04f) turn.AttitudeUp = Vector3.RotateTowards(turn.AttitudeUp, up.normalized, Mathf.PI * dt, 1);
                turn.SteeringG = desiredForceG;
            }
            else
            {
                LanceManeuver.PrepareManeuver(turn, objective, dt);
                desiredForceG = LanceManeuver.Turn(turn, objective, airspeed, dt);
                if (turn.ManeuverTimeouts != previousTimeouts)
                {
                    previousTimeouts = turn.ManeuverTimeouts;
                    NaturalMissileDiagnostics.GuidanceEvent(missile, "lance-pull-timeout", Capture());
                }
            }
            return true;
        }

        internal bool ApplyForces()
        {
            if (!started || missile.disabled || !missile.LocalSim) return false;
            float mass = missile.rb.mass;
            Vector3 direction = airspeed > 1 ? airVelocity.normalized : transform.forward;
            Vector3 gravityNormal = Vector3.ProjectOnPlane(Physics.gravity, direction);
            Vector3 needed = Vector3.ProjectOnPlane(desiredForceG * (float)LanceFlightModel.G - gravityNormal, direction) * mass;
            if (!separated && (missile.timeSinceSpawn <= 1f || altitude < origin.y + 20f)) needed = Vector3.zero;
            float liftLimit = (float)LanceFlightModel.LiftLimit(density, airspeed, mass, !separated);
            Vector3 lift = Vector3.ClampMagnitude(needed, liftLimit);
            actualLift = lift.magnitude;
            dragForce = (float)LanceFlightModel.Drag(density, airspeed, actualLift, !separated);
            missile.rb.AddForce(lift - direction * dragForce);
            // Actual integrated flight direction sets pitch/yaw; the finite
            // roll controller supplies bank. No change to Rigidbody velocity.
            Vector3 up = Vector3.ProjectOnPlane(turn.AttitudeUp, direction);
            if (up.sqrMagnitude < .001f) up = Vector3.ProjectOnPlane(transform.up, direction);
            if (up.sqrMagnitude > .001f) missile.rb.MoveRotation(Quaternion.LookRotation(direction, up.normalized));
            return true;
        }

        private void LateUpdate()
        {
            if (visuals != null && started && missile != null)
            {
                // Stage ownership and engine activity are distinct. A failed
                // boost can detach physically without ever igniting cruise.
                // Fuel/activity, rather than an instantaneous thrust sample,
                // keeps the steady plume from flickering near equilibrium.
                bool boostBurning = !separated && !failedBoost && Booster != null &&
                    (float)NaturalWeapons.Get(Booster, "fuelMass") > 0f;
                bool cruiseBurning = separated && !failedBoost && motor != null &&
                    (float)NaturalWeapons.Get(motor, "fuelMass") > 0f;
                if (missile.LocalSim)
                    LancePresentationNetworking.Send(missile, separated, failedBoost, boostBurning, cruiseBurning, transform.up);
                else
                {
                    boostBurning = observerKnown && observer.BoostActive;
                    cruiseBurning = observerKnown && observer.CruiseActive;
                }
                visuals.UpdateFlight(!missile.disabled, separated, boostBurning, cruiseBurning,
                    airspeed, altitude, missile.rb != null ? missile.rb.velocity : Vector3.zero);
                if (!missile.LocalSim && LancePresentationNetworking.TryGetVisualUp(missile, transform.forward, Time.deltaTime, out var up))
                    visuals.ApplyObserverBank(up);
            }
        }

        internal float Sheath => !started || missile == null || missile.disabled ? 0 :
            Mathf.SmoothStep(0, 1, Mathf.InverseLerp(4, 8, airspeed / (float)LanceFlightModel.SeaLevelSound)) *
            (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(30000, 60000, altitude)));

        internal float EcmIntensity => started && missile != null && !missile.disabled && missile.EngineOn() ? NativeJammingIntensity : 0f;

        internal void ObserveThreat(Unit emitter, float signal, RadarParams radar)
        {
            // An ordinary nearby sweep is insufficient. The native query must
            // be detectable and belong to a hostile interceptor targeting us.
            if (!(emitter is Missile incoming) || incoming.disabled || incoming.targetID != missile.persistentID ||
                incoming.NetworkHQ == null || incoming.NetworkHQ == missile.NetworkHQ || signal <= radar.minSignal ||
                incoming.GetComponent<ARHSeeker>() == null && incoming.GetComponent<SARHSeeker>() == null) return;
            radarThreatReceived = true;
        }

        internal object Capture() => new Dictionary<string, object> {
            ["phase"] = phase, ["separated"] = separated, ["failedBoost"] = failedBoost,
            ["altitudeM"] = altitude, ["horizontalM"] = horizontal, ["actualSpeedMps"] = airspeed,
            ["actualMachSL"] = airspeed / LanceFlightModel.SeaLevelSound,
            ["massKg"] = missile != null && missile.rb != null ? missile.rb.mass : 0,
            ["thrustN"] = lastThrust, ["dragN"] = dragForce, ["liftN"] = actualLift, ["measuredBendG"] = actualG,
            ["commandedG"] = turn.CommandG, ["pulseG"] = turn.ManeuverCommandG, ["alignedHoldS"] = turn.ManeuverHold,
            ["requiredHoldS"] = turn.ManeuverHoldDuration, ["rollError"] = turn.RollError, ["courseRecovery"] = turn.GuidancePriority,
            ["timeouts"] = turn.ManeuverTimeouts, ["targetRangeM"] = targetRange, ["nominalSecondsToTarget"] = predictedArrival,
            ["nativeEstimateUsable"] = usableEstimate, ["seaLevelReferenceMps"] = LanceFlightModel.SeaLevelSound
            ,["diveCommitted"] = turn.Diving, ["terminalRecovery"] = turn.TerminalRecovery,
            ["targetDepressionDegrees"] = turn.Depression, ["recoveryDemandG"] = turn.RecoveryDemandG,
            ["availableRecoveryG"] = turn.AvailableG, ["recoveryPredictionSeconds"] = turn.RecoveryHorizon
        };
        private static bool Finite(Vector3 v) => LanceFlightModel.Finite(v.x) && LanceFlightModel.Finite(v.y) && LanceFlightModel.Finite(v.z);
    }

    [HarmonyPatch(typeof(Missile), "FixedUpdate")]
    internal static class LanceBeginTick
    {
        private static void Prefix(Missile __instance) { if (NaturalLanceFlight.Try(__instance, out var flight)) flight.BeginPhysics(); }
    }
    [HarmonyPatch(typeof(Missile), "MotorThrust")]
    internal static class LanceMotor
    {
        private static bool Prefix(Missile __instance) => !NaturalLanceFlight.Try(__instance, out var flight) || flight.PrepareMotor();
    }
    [HarmonyPatch(typeof(VLSBooster), nameof(VLSBooster.Thrust))]
    internal static class LanceBoosterThrust
    {
        private static bool Prefix(Missile ___missile, ref float __result)
        { if (!NaturalLanceFlight.Try(___missile, out var flight)) return true; __result = flight.Boost(); return false; }
    }
    [HarmonyPatch(typeof(Missile), "Steering")]
    internal static class LanceSteering
    {
        private static bool Prefix(Missile __instance) => !NaturalLanceFlight.Try(__instance, out var flight) || !flight.Guide();
    }
    [HarmonyPatch(typeof(Missile), "ApplyAero")]
    internal static class LanceAerodynamics
    {
        private static bool Prefix(Missile __instance) => !NaturalLanceFlight.Try(__instance, out var flight) || !flight.ApplyForces();
    }
    [HarmonyPatch(typeof(Missile), "PenetrateObject")]
    internal static class LancePenetrationDepth
    {
        internal const float Metres = 10f;
        private static void Postfix(Missile __instance, Vector3 hitPoint, bool __result)
        {
            if (!__result || !NaturalLanceFlight.Try(__instance, out _)) return;
            Vector3 direction = __instance.rb.velocity.sqrMagnitude > .01f
                ? __instance.rb.velocity.normalized : __instance.transform.forward;
            Vector3 position = hitPoint + direction * Metres;
            __instance.rb.MovePosition(position);
            __instance.transform.position = position;
        }
    }
    [HarmonyPatch(typeof(Missile), nameof(Missile.GetRadarReturn))]
    internal static class LanceRadarSheath
    {
        private static void Postfix(Missile __instance, Unit emitter, RadarParams radarParameters, bool triggerWarning, ref float __result)
        {
            if (!NaturalLanceFlight.Try(__instance, out var flight)) return;
            __result *= 1f - .2f * flight.Sheath;
            // SARH (including stock R9) queries the real illuminator's return
            // with triggerWarning=false. A successful hostile guidance return
            // is still evidence of illumination; nearby missiles alone are not.
            if (triggerWarning || emitter is Missile incoming && incoming.GetComponent<SARHSeeker>() != null)
                flight.ObserveThreat(emitter, __result, radarParameters);
        }
    }
}
