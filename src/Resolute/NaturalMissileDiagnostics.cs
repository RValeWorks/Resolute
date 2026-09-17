using System;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Event-fed, bounded observations for the recorder. No scene searches,
    // timers, per-frame serialization or log output. Capture is read on demand.
    internal static class NaturalMissileDiagnostics
    {
        private static readonly string[] Keys = { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam",
            "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd" };
        private static readonly string[] Stages = { "selection", "launch", "queue", "actual-launch" };
        private sealed class Counter
        {
            internal int Allowed, Blocked;
            internal string LastReason;
            internal uint Target;
            internal float LastTime;
            internal float? LastNativeOpportunity;
            internal readonly Dictionary<string, int> Reasons = new Dictionary<string, int>();
        }
        private sealed class Flight
        {
            internal Missile Missile;
            internal NaturalWeaponPhase Phase;
        }
        private static readonly Counter[,] Decisions = new Counter[4, 8];
        private static readonly Flight[] Flights = new Flight[128];
        private static readonly Queue<object> GuidanceEvents = new Queue<object>();
        private static int omittedGuidanceEvents;
        private static int snapshotStart, untrackedFlights;

        internal static void GuidanceEvent(Missile missile, string kind, object observation)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            if (GuidanceEvents.Count == 64) { GuidanceEvents.Dequeue(); omittedGuidanceEvents++; }
            GuidanceEvents.Enqueue(new { kind, id = missile.persistentID.Id,
                weapon = missile.definition != null ? missile.definition.jsonKey : null,
                targetIdBeforeEvent = missile.targetID.Id, gameSeconds = Time.timeSinceLevelLoad,
                ageSeconds = missile.timeSinceSpawn, observation });
        }

        private static double[] Position(GlobalPosition value) => new double[] { value.x, value.y, value.z };
        private static float[] Vector(Vector3 value) => new[] { value.x, value.y, value.z };

        internal static void Decision(int stage, WeaponInfo info, Unit target, bool allowed, string reason, float? nativeOpportunity = null)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            string key = NaturalMissileTargeting.Key(info);
            int index = Array.IndexOf(Keys, key);
            if (index < 0 || stage < 0 || stage >= Stages.Length) return;
            Counter counter = Decisions[stage, index];
            if (counter == null) Decisions[stage, index] = counter = new Counter();
            if (allowed) { if (counter.Allowed < int.MaxValue) counter.Allowed++; }
            else
            {
                if (counter.Blocked < int.MaxValue) counter.Blocked++;
                reason = reason ?? "unspecified";
                int count;
                if (counter.Reasons.TryGetValue(reason, out count))
                { if (count < int.MaxValue) counter.Reasons[reason] = count + 1; }
                else if (counter.Reasons.Count < 32) counter.Reasons.Add(reason, 1);
            }
            counter.LastReason = allowed ? "eligible" : reason;
            counter.Target = target != null ? target.persistentID.Id : 0;
            counter.LastTime = Time.timeSinceLevelLoad;
            counter.LastNativeOpportunity = nativeOpportunity;
        }

        internal static void Register(Missile missile, NaturalWeaponPhase phase)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            for (int i = 0; i < Flights.Length; i++)
            {
                if (Flights[i] != null && Flights[i].Missile != null && !Flights[i].Missile.disabled) continue;
                Flights[i] = new Flight { Missile = missile, Phase = phase };
                return;
            }
            if (untrackedFlights < int.MaxValue) untrackedFlights++;
        }

        internal static void Unregister(Missile missile)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            for (int i = 0; i < Flights.Length; i++)
                if (Flights[i] != null && ReferenceEquals(Flights[i].Missile, missile)) Flights[i] = null;
        }

        internal static object Capture()
        {
            var decisions = new List<object>();
            for (int stage = 0; stage < Stages.Length; stage++)
                for (int key = 0; key < Keys.Length; key++)
                {
                    Counter counter = Decisions[stage, key];
                    if (counter == null) continue;
                    decisions.Add(new Dictionary<string, object> {
                        ["stage"] = Stages[stage], ["weapon"] = Keys[key],
                        ["allowed"] = counter.Allowed, ["blocked"] = counter.Blocked,
                        ["reasons"] = new Dictionary<string, int>(counter.Reasons),
                        ["lastReason"] = counter.LastReason, ["lastTargetId"] = counter.Target,
                        ["lastDecisionGameSeconds"] = counter.LastTime,
                        ["nativeOpportunityBeforePolicyGate"] = counter.LastNativeOpportunity
                    });
                }
            var flights = new List<object>();
            int visited = 0;
            for (; visited < Flights.Length && flights.Count < 24; visited++)
            {
                Flight flight = Flights[(snapshotStart + visited) % Flights.Length];
                if (flight == null || flight.Missile == null || flight.Missile.disabled || flight.Phase == null) continue;
                Missile missile = flight.Missile;
                NaturalLoftGuidance loft = missile.GetComponent<NaturalLoftGuidance>();
                NaturalCruiseGuidance cruise = missile.GetComponent<NaturalCruiseGuidance>();
                OpticalSeekerCruiseMissile optical = missile.GetComponent<OpticalSeekerCruiseMissile>();
                ARHSeeker radar = missile.GetComponent<ARHSeeker>();
                NaturalPikeCountermeasures pike = missile.GetComponent<NaturalPikeCountermeasures>();
                NaturalPikeFormation formation = missile.GetComponent<NaturalPikeFormation>();
                NaturalCruiseTactics tactics = missile.GetComponent<NaturalCruiseTactics>();
                NaturalSpearFlight spear = missile.GetComponent<NaturalSpearFlight>();
                NaturalRadarTrackRecovery recovery = missile.GetComponent<NaturalRadarTrackRecovery>();
                if (recovery != null) recovery.Observe();
                Unit target; missile.targetID.TryGetUnit(out target);
                flights.Add(new Dictionary<string, object> {
                    ["id"] = missile.persistentID.Id, ["weapon"] = missile.definition != null ? missile.definition.jsonKey : missile.name,
                    ["targetId"] = missile.targetID.Id, ["ageSeconds"] = missile.timeSinceSpawn,
                    ["nativePrefabDonor"] = flight.Phase.NativePrefabDonor,
                    ["speedMps"] = missile.speed, ["altitudeM"] = missile.GlobalPosition().y,
                    ["positionM"] = Position(missile.GlobalPosition()),
                    ["velocityMps"] = Vector(missile.rb.velocity), ["radarAltitudeM"] = missile.radarAlt,
                    ["nativeAimpointM"] = Position((GlobalPosition)NaturalWeapons.Get(missile, "aimPoint")),
                    ["targetPositionM"] = target != null ? Position(target.GlobalPosition()) : null,
                    ["targetDisabled"] = target != null ? (object)target.disabled : null,
                    ["throttle"] = NaturalWeapons.Get(missile, "throttle"),
                    ["motorStage"] = NaturalWeapons.Get(missile, "motorStage"),
                    ["nativeBoosterAttached"] = missile.boosterIsAttached,
                    ["hqPresent"] = missile.NetworkHQ != null,
                    ["seekerMode"] = missile.seekerMode.ToString(),
                    ["seekerType"] = optical != null ? "OpticalSeekerCruiseMissile" : radar != null ? "ARHSeeker" : "IRSeeker",
                    ["radarActivationDistanceM"] = radar != null ? NaturalWeapons.Get(radar, "terminalRange") : null,
                    ["phase"] = optical != null ? ((bool)NaturalWeapons.Get(optical, "terminalMode") ? "native-optical-terminal" : "native-optical-cruise") :
                        cruise != null ? cruise.FlightPhase : loft != null ? loft.FlightPhase : "native-intercept",
                    ["commandedAltitudeM"] = cruise != null ? (object)cruise.CommandedAltitude : loft != null ? (object)loft.CommandedAltitude : null,
                    ["nativeLoftAmount"] = loft != null && loft.NativeAirDefense ? (object)loft.AppliedNativeLoft : null,
                    ["nativeLoftDonor"] = loft != null && loft.NativeAirDefense ? loft.NativeLoftDonor : null,
                    ["nativeLoftAvailable"] = loft != null && loft.NativeAirDefense ? (object)(loft.NativeLoftAmount > 0f) : null,
                    ["terminalBoost"] = flight.Phase.TerminalBoostActive,
                    ["pikePairsEmitted"] = pike != null ? (object)pike.BurstsEmitted : null,
                    ["pikeCountermeasures"] = pike != null ? pike.Capture() : null,
                    ["pikeFormation"] = formation != null ? formation.Capture() : null,
                    ["cruiseEvasion"] = tactics != null ? tactics.Capture() : null,
                    ["spearNativeFlight"] = spear != null ? new {
                        donorTurnRate = spear.NativeTurnRate, donorFinArea = spear.NativeFinArea,
                        donorCruiseThrust = spear.NativeThrust, donorFuelMass = spear.NativeFuelMass,
                        massScale = spear.MassScale, donorMotorStages = spear.NativeMotorStages
                    } : null,
                    ["radarRecovery"] = recovery != null ? recovery.Capture() : null,
                    ["nativeOpticalFormationSpacingM"] = optical != null ? NaturalWeapons.Get(optical, "formationSpacing") : null,
                    ["nativeOpticalAltitudeTargetM"] = optical != null ? NaturalWeapons.Get(optical, "altitudeTarget") : null
                });
            }
            snapshotStart = (snapshotStart + visited) % Flights.Length;
            int activeRegistered = 0;
            foreach (Flight item in Flights)
                if (item != null && item.Missile != null && !item.Missile.disabled) activeRegistered++;
            object[] events = GuidanceEvents.ToArray(); GuidanceEvents.Clear();
            return new Dictionary<string, object> {
                ["scope"] = "Eligibility counters and actual-launch ammo outcomes; up to 24 rotating samples from 128 registered missiles. Target-clear context is an observation, not proof of a particular native call-site. Guidance events are drained once per capture, capped at 64. Pike burst counts are now opposite-side pairs.",
                ["gameSeconds"] = Time.timeSinceLevelLoad, ["decisions"] = decisions,
                ["flights"] = flights, ["activeRegisteredFlights"] = activeRegistered,
                ["untrackedFlightRegistrations"] = untrackedFlights,
                ["guidanceEvents"] = events, ["omittedGuidanceEvents"] = omittedGuidanceEvents
            };
        }
    }
}
