using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Resolute
{
    // A bounded event diagnostic, independent of the flight recorder. Nothing
    // runs per frame or changes a native decision, target, motor or lifetime.
    internal static class NaturalLaunchDiagnostics
    {
        internal const int Limit = 24;
        internal const float MaximumAge = 12f;
        private static int emitted;
        private static readonly HashSet<int> Seen = new HashSet<int>();
        [ThreadStatic] internal static MissileSeeker SlowCheck;
        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> Aim = AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");
        private static readonly AccessTools.FieldRef<Missile, Vector3> TargetVelocity = AccessTools.FieldRefAccess<Missile, Vector3>("targetVel");
        private static readonly AccessTools.FieldRef<Missile, int> Stage = AccessTools.FieldRefAccess<Missile, int>("motorStage");
        private static readonly AccessTools.FieldRef<Missile, float> Thrust = AccessTools.FieldRefAccess<Missile, float>("engineCurrentThrust");
        private static readonly AccessTools.FieldRef<Missile, float> Hitpoints = AccessTools.FieldRefAccess<Missile, float>("hitpoints");
        private static readonly AccessTools.FieldRef<MissileSeeker, Unit> Target = AccessTools.FieldRefAccess<MissileSeeker, Unit>("targetUnit");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarMinimumSpeed = AccessTools.FieldRefAccess<ARHSeeker, float>("selfDestructAtSpeed");
        private static readonly AccessTools.FieldRef<IRSeeker, float> InfraredMinimumSpeed = AccessTools.FieldRefAccess<IRSeeker, float>("selfDestructAtSpeed");

        static NaturalLaunchDiagnostics() { MissionManager.onMissionLoad += _ => ResetBudget(); }

        internal static void ResetBudget() { emitted = 0; Seen.Clear(); SlowCheck = null; }

        internal static bool Scoped(Missile missile)
        {
            if (!ResoluteDiagnostics.Enabled || emitted >= Limit || missile == null || missile.disabled || !missile.LocalSim ||
                missile.timeSinceSpawn < 0f || missile.timeSinceSpawn > MaximumAge || missile.definition == null) return false;
            switch (missile.definition.jsonKey)
            {
                case "rsl_ashm": case "rsl_cruise": case "rsl_lrsam": case "rsl_mrsam":
                case "rsl_bastion": case "rsl_bmd": case "rsl_bmd_exo": case "rsl_pd": return true;
                default: return false;
            }
        }

        internal static void Ending(Missile missile, bool hitArmor, bool hitTerrain)
        {
            bool counted = false;
            try
            {
                // Explicit solid impacts and confirmed damage kills are not
                // the unexplained early retirement this small budget is for.
                if (!Scoped(missile) || hitArmor || hitTerrain || Hitpoints(missile) <= 0f ||
                    !Seen.Add(missile.GetInstanceID())) return;
                emitted++; counted = true;
                MissileSeeker seeker = missile.GetComponent<MissileSeeker>();
                Unit nativeTarget = seeker != null ? Target(seeker) : null;
                Vector3 velocity = missile.rb != null ? missile.rb.velocity : Vector3.zero;
                float passedDot = Vector3.Dot(Aim(missile) - missile.GlobalPosition(), velocity);
                float losingDot = Vector3.Dot(velocity, velocity - TargetVelocity(missile));
                float minimum = seeker is ARHSeeker radar ? RadarMinimumSpeed(radar) :
                    seeker is IRSeeker infrared ? InfraredMinimumSpeed(infrared) : 100f;
                bool clock = seeker is ARHSeeker ? missile.timeSinceSpawn > 10f || !missile.EngineOn() && missile.timeSinceSpawn > 2f :
                    seeker is IRSeeker ? !missile.EngineOn() : missile.timeSinceSpawn > 10f;
                bool inSlowCheck = SlowCheck != null && ReferenceEquals(SlowCheck, seeker);
                VLSBooster booster = missile.GetComponentInChildren<VLSBooster>(true);
                var motorRows = new List<object>();
                foreach (object motor in (Array)NaturalWeapons.Get(missile, "motors"))
                    motorRows.Add(new { fuelKg = NaturalWeapons.Get(motor, "fuelMass"),
                        thrustN = NaturalWeapons.Get(motor, "thrust"), burnSeconds = NaturalWeapons.Get(motor, "burnTime"),
                        ignitionDelayRemaining = NaturalWeapons.Get(motor, "delayTimer"), activated = NaturalWeapons.Get(motor, "activated") });
                var observation = new {
                    eventNumber = emitted, eventLimit = Limit, weapon = missile.definition.jsonKey,
                    id = missile.persistentID.Id, gameSeconds = Time.timeSinceLevelLoad, ageSeconds = missile.timeSinceSpawn,
                    speedMps = missile.speed, velocityMps = V(velocity), forward = V(missile.transform.forward),
                    heightAboveSeaM = missile.transform.position.y - Datum.LocalSeaY,
                    engineOn = missile.EngineOn(), currentThrustN = Thrust(missile), motorStage = Stage(missile), motors = motorRows,
                    boosterAttached = missile.boosterIsAttached,
                    booster = booster == null ? null : new { enabled = booster.enabled, active = booster.gameObject.activeInHierarchy,
                        activated = NaturalWeapons.Get(booster, "activated"), separated = NaturalWeapons.Get(booster, "separated"),
                        fuelKg = NaturalWeapons.Get(booster, "fuelMass"), ignitionDelayRemaining = NaturalWeapons.Get(booster, "delayTimer") },
                    targetId = missile.targetID.Id, nativeSeekerTargetId = nativeTarget != null ? nativeTarget.persistentID.Id : 0,
                    seeker = seeker != null ? seeker.GetType().Name : "none", mode = missile.seekerMode.ToString(),
                    armed = missile.IsArmed(), tangible = missile.IsTangible(), insideSlowChecks = inSlowCheck,
                    evidence = new { nativeRetirementClockOpen = clock, rawPassedAimDot = passedDot, rawLosingGroundDot = losingDot,
                        belowMinimumSpeed = missile.speed < minimum, minimumSpeedMps = minimum, nativeTargetNull = nativeTarget == null },
                    callerFrames = CallerFrames(),
                    interpretation = "Detonate entry observed; caller frames and simultaneous predicates are evidence, not an asserted cause or confirmed miss."
                };
                Write("[Early missile end] " + JsonConvert.SerializeObject(observation));
            }
            catch (Exception error)
            {
                // Diagnostics must never prevent the original detonation or
                // repeatedly throw when an optional observation is unavailable.
                if (!counted) { if (emitted >= Limit) return; emitted++; }
                try { Write("[Early missile end] event=" + emitted + "; snapshot unavailable: " + error.GetType().Name); }
                catch { }
            }
        }

        private static float[] V(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
        private static string[] CallerFrames()
        {
            var result = new List<string>();
            foreach (StackFrame frame in new StackTrace(false).GetFrames() ?? new StackFrame[0])
            {
                MethodBase method = frame.GetMethod();
                if (method == null || method.DeclaringType == typeof(NaturalLaunchDiagnostics) ||
                    method.DeclaringType == typeof(NaturalEarlyMissileEnd)) continue;
                result.Add((method.DeclaringType != null ? method.DeclaringType.FullName + "." : "") + method.Name);
                if (result.Count == 6) break;
            }
            return result.ToArray();
        }
        private static void Write(string message)
        {
            if (Plugin.Instance != null) Plugin.Instance.LogStartupWarning(message);
            else UnityEngine.Debug.LogWarning(message);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.Detonate))]
    internal static class NaturalEarlyMissileEnd
    {
        private static void Prefix(Missile __instance, bool hitArmor, bool hitTerrain)
            => NaturalLaunchDiagnostics.Ending(__instance, hitArmor, hitTerrain);
    }

    [HarmonyPatch]
    internal static class NaturalEarlySeekerCheckContext
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ARHSeeker), "SlowChecks");
            yield return AccessTools.Method(typeof(IRSeeker), "SlowChecks");
            yield return AccessTools.Method(typeof(OpticalSeekerCruiseMissile), "SlowChecks");
        }
        [HarmonyPriority(Priority.First)]
        private static void Prefix(MissileSeeker __instance, out MissileSeeker __state)
        {
            __state = NaturalLaunchDiagnostics.SlowCheck;
            if (!ResoluteDiagnostics.Enabled) return;
            NaturalLaunchDiagnostics.SlowCheck = NaturalLaunchDiagnostics.Scoped(__instance.GetComponent<Missile>()) ? __instance : null;
        }
        private static void Finalizer(MissileSeeker __state) { NaturalLaunchDiagnostics.SlowCheck = __state; }
    }
}
