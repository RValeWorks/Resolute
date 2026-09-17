using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit diagnostic only. Guidance, propulsion, collision and damage of
    // the weapons under test run normally. Synthetic tracks remove search and
    // identification latency from these projectile-behavior measurements.
    internal static class NaturalWeaponsTrial
    {
        private sealed class Case
        {
            internal string Key;
            internal Unit Target;
            internal Shot Primary, Payload;
            internal float HealthBefore, HealthAfter;
            internal UnitPart[] OriginalParts;
            internal readonly List<Dictionary<string, object>> OriginalPartHealth = new List<Dictionary<string, object>>();
            internal int DamageCalls;
            internal Vector3 TargetVelocity;
            internal Vector3 PendingLaunchPosition, PendingLaunchDirection;
            internal float TargetAltitude;
            internal bool ControlledTarget;
            internal readonly List<Shot> Salvo = new List<Shot>();
            internal float InitialSalvoSpan, MaximumSalvoSpan, MinimumSalvoSeparation = float.MaxValue;
            internal float MinimumFormationSeparation = float.MaxValue;
            internal readonly List<object> Samples = new List<object>();
            internal readonly List<object> FineSamples = new List<object>();
            internal readonly Dictionary<string, object> Report = new Dictionary<string, object>();
        }
        private sealed class Shot
        {
            internal Case Case;
            internal string SourceKey;
            internal Missile Missile;
            internal Vector3 Start, Forward, Last;
            internal float Travel, PeakSpeed, AliveSeconds, Turn, TrackedSeconds, DetonationTime = -1000f;
            internal float UnderwaterTravel, UnderwaterSeconds, PeakUnderwaterSpeed, AboveWaterFlightSeconds;
            internal float MinimumTargetDistance = float.MaxValue, MaximumNozzleError, MaximumSteadyAttackAngle;
            internal int EmittedFlares, GroupMembers, NativeGroupingUpdates;
            internal bool NativeSourceTerminalObserved;
            internal float JinkOffset, ReactionControlSeconds, ReactionDeltaV, ReactionPeakAcceleration;
            internal bool MotorParticlesSeen, MotorAudioSeen, FlareParticlesSeen, ReactionJetsSeen;
            internal bool NativeBoosterActivated, NativeBoosterSeparated, NativeBoosterParticlesSeen, NativeOpticalTerminalSeen;
            internal float NativeBoosterAttachedSeconds, NativeOpticalLowCruiseSeconds;
            internal float NativeBoosterSeparationTime = -1f;
            internal bool AfterburnerSeen, CruiseFlameCapture, TerminalFlameCapture, FlareCapture;
            internal int FlareReadyFrame = -1;
            internal float MaximumAltitude, AltitudeAtTwentySeconds = -1f, LoftSeconds;
            internal float MidcourseSeconds, MidcourseMaximumAttackAngle, MidcourseMinimumDensity = float.MaxValue;
            internal float MidcourseBackwardsSeconds, MidcourseExcessiveTurnSeconds, MidcourseMaximumHeadingRate;
            internal Vector3 PreviousHeading;
            internal float PreviousObservationTime = -1f;
            internal bool LoftSizingCaptured, FinnedLoft;
            internal float MinimumDesignDensity;
            internal float CruisePeakSpeed, TerminalPeakSpeed, CruiseThrust, TerminalThrust, MaximumLateralSpeed;
            internal int LateralReversals, LastLateralSign;
            internal float LastLateralReversal;
            internal readonly HashSet<int> CapturedStages = new HashSet<int>();
            internal readonly HashSet<string> CapturedMotorMoments = new HashSet<string>();
            internal int ObservedStage = -1;
            internal float StageObservedTime;
            internal bool ReactionCapture;
            internal Vector3 WaterStart;
            internal Vector3 DetonationPosition;
            internal bool IntendedTargetContact;
            internal int LiveSiblingContacts;
            internal readonly List<object> NativeEvents = new List<object>();
            internal bool Finite = true, FlightModel, WaterEntry, RadarSilent, Detonated, ActiveRadarLock, ArmedFlight, ArmorImpact;
            internal object Json()
            {
                return new Dictionary<string, object> {
                    ["sourceKey"] = SourceKey, ["travelMetres"] = Travel, ["peakSpeedMps"] = PeakSpeed,
                    ["aliveSeconds"] = AliveSeconds, ["largestHeadingChangeDegrees"] = Turn,
                    ["aboveWaterFlightSeconds"] = AboveWaterFlightSeconds,
                    ["activeRadarLockSeen"] = ActiveRadarLock,
                    ["armedFlightSeen"] = ArmedFlight,
                    ["nativeArmorImpact"] = ArmorImpact,
                    ["intendedTargetPhysicalContact"] = IntendedTargetContact,
                    ["liveSiblingContactEvents"] = LiveSiblingContacts,
                    ["minimumTargetDistanceMetres"] = MinimumTargetDistance,
                    ["maximumNozzleErrorMetres"] = MaximumNozzleError,
                    ["maximumSteadyAngleOfAttackDegrees"] = MaximumSteadyAttackAngle,
                    ["motorParticlesSeen"] = MotorParticlesSeen, ["motorAudioSeen"] = MotorAudioSeen,
                    ["nativeBoosterActivated"] = NativeBoosterActivated, ["nativeBoosterSeparated"] = NativeBoosterSeparated,
                    ["nativeBoosterParticlesSeenWhileAttached"] = NativeBoosterParticlesSeen,
                    ["nativeBoosterAttachedSeconds"] = NativeBoosterAttachedSeconds,
                    ["nativeBoosterSeparationTime"] = NativeBoosterSeparationTime,
                    ["nativeOpticalTerminalSeen"] = NativeOpticalTerminalSeen,
                    ["nativeOpticalLowCruiseSeconds"] = NativeOpticalLowCruiseSeconds,
                    ["nativeAfterburnerSeen"] = AfterburnerSeen,
                    ["maximumAltitudeMetres"] = MaximumAltitude, ["altitudeAt20SecondsMetres"] = AltitudeAtTwentySeconds,
                    ["midcourseObservedSeconds"] = MidcourseSeconds,
                    ["midcourseMaximumAngleOfAttackDegrees"] = MidcourseMaximumAttackAngle,
                    ["midcourseMinimumNativeAirDensity"] = MidcourseSeconds > 0f ? (object)MidcourseMinimumDensity : null,
                    ["midcourseBackwardsFlightSeconds"] = MidcourseBackwardsSeconds,
                    ["midcourseExcessiveHeadingRateSeconds"] = MidcourseExcessiveTurnSeconds,
                    ["midcourseMaximumHeadingRateDegreesPerSecond"] = MidcourseMaximumHeadingRate,
                    ["midcourseMeasurementScope"] = "After the later of 5 seconds or source initial phase plus 1 second; while target distance exceeds both 1 km and 3 seconds of current speed. Launch turns and the final impact approach are excluded. Excessive heading rate means more than the native combined pitch/yaw turn cap plus 5 degrees per second.",
                    ["loftActiveSeconds"] = LoftSeconds, ["cruisePeakSpeedMps"] = CruisePeakSpeed,
                    ["terminalPeakSpeedMps"] = TerminalPeakSpeed, ["cruiseThrustNewtons"] = CruiseThrust,
                    ["terminalThrustNewtons"] = TerminalThrust, ["maximumLateralSpeedMps"] = MaximumLateralSpeed,
                    ["lateralVelocityReversals"] = LateralReversals,
                    ["emittedFlares"] = EmittedFlares, ["nativeFlareParticlesSeen"] = FlareParticlesSeen,
                    ["maximumNativeNeighborsExcludingSelf"] = GroupMembers, ["nativeMethodUpdatesWithNeighbors"] = NativeGroupingUpdates,
                    ["nativeSourceTerminalObserved"] = NativeSourceTerminalObserved,
                    ["largestJinkMetres"] = JinkOffset,
                    ["reactionControlSeconds"] = ReactionControlSeconds, ["reactionControlDeltaV"] = ReactionDeltaV,
                    ["reactionControlPeakAcceleration"] = ReactionPeakAcceleration, ["reactionControlJetsSeen"] = ReactionJetsSeen,
                    ["retainedTargetSeconds"] = TrackedSeconds, ["finite"] = Finite,
                    ["flightModelSeen"] = FlightModel, ["waterEntrySeen"] = WaterEntry,
                    ["underwaterTravelMetres"] = UnderwaterTravel, ["underwaterSeconds"] = UnderwaterSeconds,
                    ["peakUnderwaterSpeedMps"] = PeakUnderwaterSpeed,
                    ["submergedRadarSilent"] = RadarSilent, ["nativeDetonation"] = Detonated,
                    ["detonationGlobalPosition"] = Detonated ? V(DetonationPosition.ToGlobalPosition().AsVector3()) : null,
                    ["nativeContactAndReceivedDamageEvents"] = NativeEvents,
                    ["existsAtEnd"] = Missile != null, ["disabledAtEnd"] = Missile == null || Missile.disabled
                };
            }
        }
        private static readonly Dictionary<Missile, Shot> shots = new Dictionary<Missile, Shot>();
        private static readonly Dictionary<Unit, Case> casesByTarget = new Dictionary<Unit, Case>();
        private static readonly List<GameObject> spawned = new List<GameObject>();
        private static Ship owner;
        private static Harmony hooks;
        private static bool DenseSalvoOnly => Environment.GetCommandLineArgs().Contains("--resolute-pike-dense-salvo-only");

        internal static IEnumerator Run(Ship ship, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Projectile trials require the isolated test-game copy.");
            owner = ship;
            string[] allKeys = { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd", "rsl_torpedo", "rsl_asw_payload", "rsl_asw", "rsl_fulmar" };
            string[] enabledKeys = allKeys.Where(NaturalArmament.IsEnabledWeapon).ToArray();
            string selectedKeys = Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-projectile-keys");
            var requested = new HashSet<string>(string.IsNullOrWhiteSpace(selectedKeys) ? enabledKeys :
                selectedKeys.Split(',').Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);
            if (requested.Any(k => !allKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("Unknown original projectile key in --resolute-projectile-keys.");
            if (requested.Any(k => !NaturalArmament.IsEnabledWeapon(k)))
                throw new ArgumentException("Disabled underwater weapon requested in --resolute-projectile-keys.");
            if (DenseSalvoOnly && !requested.SetEquals(new[] { "rsl_ashm" }))
                throw new ArgumentException("Dense salvo selection requires --resolute-projectile-keys rsl_ashm.");
            if (!string.IsNullOrWhiteSpace(selectedKeys) && (Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-trial-only") ?? "full") != "projectiles")
                throw new ArgumentException("Projectile key selection is only allowed for a focused projectile trial.");
            var results = new List<object>();
            var detail = new Dictionary<string, object> {
                ["scope"] = "Native network spawns with the eight enabled original aerial weapon definitions and native radar/IR guidance. Disabled underwater definitions are excluded. Target tracks are refreshed explicitly, so this tests projectile behavior rather than autonomous detection or combat balance. The normal four-round Pike case uses an actual fresh Resolute VLS station, physical cells, unchanged eligibility and native cooldown; other flight cases use direct diagnostic spawns. The simultaneous 22 m Pike stress fixture is retained only under the explicit dense-salvo selector and is not normal VLS acceptance.",
                ["cases"] = results, ["requestedKeys"] = allKeys.Where(requested.Contains).ToArray(), ["excludedKeys"] = allKeys.Where(k => !NaturalArmament.IsEnabledWeapon(k)).ToArray(),
                ["filtered"] = requested.Count != enabledKeys.Length, ["success"] = false,
                ["missileProfiles"] = NaturalWeaponAudit.Capture(Encyclopedia.i)
            };
            report["sourceProjectileTrial"] = detail;
            report["phase"] = "original-weapon-projectiles";
            Save(output, report);
            if (Environment.GetCommandLineArgs().Contains("--resolute-missile-visual-review"))
            {
                IEnumerator visualReview = MissileVisualTrial.Run(ship, report, output);
                while (visualReview.MoveNext()) yield return visualReview.Current;
                Save(output, report);
            }
            if (requested.Contains("rsl_bmd") || requested.Contains("rsl_bmd_exo"))
            {
                IEnumerator reference = NaturalBallisticReferenceTrial.WaitUntilReady(report, checks, output);
                while (reference.MoveNext()) yield return reference.Current;
            }
            hooks = new Harmony(Plugin.Id + ".natural-projectile-trial");
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(RecordDetonation)));
            hooks.Patch(AccessTools.Method(typeof(UnitPart), nameof(UnitPart.TakeDamage)),
                postfix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(RecordDamage)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeDamage)),
                postfix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(RecordMissileDamage)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeDamage)),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(TraceIncomingDamage)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeShockwave)),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(TraceShockwave)));
            hooks.Patch(AccessTools.Method(typeof(Missile), "PenetrateObject"),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(TracePenetration)));
            hooks.Patch(AccessTools.Method(typeof(NaturalWeaponPhase), "Contact"),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(TracePhysicalContact)));
            hooks.Patch(AccessTools.Method(typeof(ARHSeeker), "SlowChecks"),
                prefix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(TargetSlowChecks)));
            hooks.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnMissile), new[] {
                typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) }),
                postfix: new HarmonyMethod(typeof(NaturalWeaponsTrial), nameof(RecordPayload)));
            bool passed = true;
            try
            {
                // The earlier bank test deliberately left missiles in flight.
                // Retire those isolated test objects before attributing damage.
                foreach (Missile prior in UnitRegistry.allUnits.OfType<Missile>().Where(m => m.ownerID == ship.persistentID).ToArray())
                    RetireMissileFixture(prior);
                // The full mission's earlier laser/gun targets otherwise occupy
                // this same sea lane. Their colliding hulls corrupt the fresh
                // target's motion and radar acceleration lead; focused runs had
                // no such objects. This scene is the isolated diagnostic host.
                Ship[] earlierTargets = UnitRegistry.allUnits.OfType<Ship>().Where(s => s != ship).ToArray();
                detail["retiredEarlierShips"] = earlierTargets.Select(s => new Dictionary<string, object> {
                    ["definition"] = s.definition.jsonKey, ["persistentId"] = s.persistentID.ToString(),
                    ["globalPosition"] = V(s.GlobalPosition().AsVector3())
                }).ToArray();
                foreach (Ship earlier in earlierTargets) Object.Destroy(earlier.gameObject);
                yield return null; yield return null; yield return new WaitForFixedUpdate();
                int earlierBullets = 0;
                foreach (BulletSim simulation in Object.FindObjectsOfType<BulletSim>())
                {
                    if ((Unit)NaturalWeapons.Get(simulation, "owner") != ship) continue;
                    var bullets = (List<BulletSim.Bullet>)NaturalWeapons.Get(simulation, "bullets");
                    earlierBullets += bullets.Count;
                    foreach (BulletSim.Bullet bullet in bullets) bullet.Remove();
                    bullets.Clear();
                }
                detail["retiredEarlierGunProjectiles"] = earlierBullets;
                Require(checks, "projectile-trial-no-earlier-target-ships", !UnitRegistry.allUnits.OfType<Ship>().Any(s => s != ship));
                FactionHQ hostile = FactionRegistry.HqFromName(ship.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
                ShipDefinition targetDefinition = Plugin.FindDonor(Encyclopedia.i);
                Require(checks, "projectile-trial-native-target-definitions", hostile != null && targetDefinition != null);

                if (DenseSalvoOnly)
                {
                    detail["scope"] = "Focused native dense Pike salvo with the existing 22 m cross-track / 12 m stagger fixture and unchanged acceptance checks. Fine native contact, received-damage and guidance telemetry is read-only. Policy, unrelated weapons and long-range cases are excluded.";
                    IEnumerator dense = SupplementalTrials(requested, hostile, targetDefinition, detail, report, checks, output, ok => passed &= ok);
                    while (dense.MoveNext()) yield return dense.Current;
                    detail["success"] = passed; Save(output, report); yield break;
                }

                IEnumerator targeting = NaturalMissileTargetingTrial.Run(ship, requested, report, checks, output);
                while (targeting.MoveNext()) yield return targeting.Current;
                if (Environment.GetCommandLineArgs().Contains("--resolute-targeting-review-only"))
                {
                    detail["scope"] = "Focused native targeting-policy trial only; direct projectile flight and hit checks were not run.";
                    detail["success"] = true; Save(output, report); yield break;
                }
                IEnumerator ballisticBehavior = NaturalWeaponBehaviorTrial.Run(ship, requested, report, checks, output);
                while (ballisticBehavior.MoveNext()) yield return ballisticBehavior.Current;
                yield return null; yield return null;

                // All aerial cases run together in distinct tracks. The two
                // surface cases use checked sea lanes; air cases start aloft.
                var aerial = new List<Case>();
                int index = 0;
                foreach (string key in new[] { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd" })
                {
                    if (!requested.Contains(key)) { index++; continue; }
                    var test = new Case { Key = key };
                    Vector3 launch, heading;
                    if (key == "rsl_ashm" || key == "rsl_cruise")
                    {
                        GlobalPosition a, b;
                        if (key == "rsl_cruise")
                        {
                            // A short sea-lane segment can end in a valid impact
                            // before Spear's native twelve-second booster burns
                            // out. Require the full corridor to observe both
                            // actual propulsion stages and optical low cruise.
                            var corridor = new Dictionary<string, object>();
                            FindLongWaterLeg(11000f, corridor, out a, out b, out heading, 800f);
                            test.Report["nativeOpticalCruiseCorridor"] = corridor;
                            test.Report["propulsionAcceptanceScope"] = "A complete 11 km native ocean corridor observes the attached VLS booster, physical burnout/separation, native cruise motor audio and native INS/Opt terminal. This direct flight fixture is not a VLS ejection test; the separate cruise_behavior scenario observes actual VLS clearance.";
                        }
                        else FindWaterLeg(index, 6000f, out a, out b, out heading);
                        test.Target = SpawnTargetShip(targetDefinition, hostile, b, heading, "aerial_" + key);
                        test.ControlledTarget = true; test.TargetVelocity = test.Target.transform.forward * 8f;
                        test.TargetAltitude = test.Target.transform.position.y;
                        launch = a.ToLocalPosition(); launch.y = Datum.LocalSeaY + 100f;
                    }
                    else if (key == "rsl_bmd" || key == "rsl_bmd_exo")
                    {
                        Vector3 axis = Quaternion.AngleAxis(index * 43f, Vector3.up) * Vector3.forward;
                        Vector3 targetPoint = ship.transform.position + axis * (18000f + index * 1300f);
                        targetPoint.y = Datum.LocalSeaY + 15000f;
                        heading = -axis;
                        Vector3 crossing = Vector3.Cross(Vector3.up, axis);
                        Ship launcher = SpawnTargetShip(targetDefinition, hostile, ship.GlobalPosition() + axis * 30000f, heading, "bmd_owner_" + key);
                        Know(hostile, ship);
                        Missile target = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NativeBallistic(), targetPoint,
                            Quaternion.LookRotation(crossing), crossing * 700f, ship, launcher);
                        spawned.Add(target.gameObject); test.Target = target; test.ControlledTarget = true;
                        test.TargetVelocity = crossing * 700f; test.TargetAltitude = targetPoint.y;
                        NaturalWeapons.Set(target, "seeker", null);
                        launch = targetPoint - heading * 10000f;
                        test.Report["ballisticClassification"] = NaturalBallisticSelection.CaptureClassification(target);
                    }
                    else
                    {
                        Vector3 axis = Quaternion.AngleAxis(index * 43f, Vector3.up) * Vector3.forward;
                        Vector3 targetPoint = ship.transform.position + axis * (10000f + index * 1300f);
                        targetPoint.y = Datum.LocalSeaY + 2500f + index * 100f;
                        heading = -axis;
                        AircraftDefinition aircraft = NativeAircraft();
                        Vector3 crossing = Vector3.Cross(Vector3.up, axis);
                        Aircraft target = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, aircraft.unitPrefab,
                            aircraft.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), targetPoint.ToGlobalPosition(),
                            Quaternion.LookRotation(crossing), crossing * 230f, null, hostile, "rsl_projectile_air_target_" + key, 1f, 1f);
                        spawned.Add(target.gameObject);
                        test.Target = target; test.ControlledTarget = true;
                        test.TargetVelocity = crossing * 230f; test.TargetAltitude = targetPoint.y;
                        float distance = key == "rsl_pd" ? 4500f : 10000f;
                        launch = targetPoint - heading * distance;
                    }
                    casesByTarget.Add(test.Target, test);
                    Know(ship.NetworkHQ, test.Target);
                    test.Report["key"] = key;
                    test.Report["targetType"] = test.Target.definition.jsonKey;
                    test.Report["targetGlobalStart"] = V(test.Target.GlobalPosition().AsVector3());
                    test.Report["samples"] = test.Samples;
                    test.Report["diagnosticLaunchGlobalPosition"] = V(launch.ToGlobalPosition().AsVector3());
                    test.Report["initialLaunchVelocityMps"] = 60f;
                    test.Report["targetCourse"] = "Constant-speed crossing track; target attitude/altitude controlled to exclude target crashes.";
                    test.Report["targetVelocityMps"] = V(test.TargetVelocity);
                    results.Add(test.Report);
                    aerial.Add(test);
                    test.PendingLaunchPosition = launch; test.PendingLaunchDirection = heading;
                    index++;
                }
                // Give native aircraft engines and target missiles time to
                // initialize real heat sources before an infrared launch.
                float warmStarted = Time.time;
                while (Time.time - warmStarted < 2f)
                {
                    foreach (Case test in aerial)
                    {
                        Know(ship.NetworkHQ, test.Target);
                        MaintainTarget(test);
                        Aircraft aircraft = test.Target as Aircraft;
                        if (aircraft != null)
                        {
                            foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                            aircraft.NetworkIgnition = true;
                            ControlInputs controls = aircraft.GetInputs();
                            controls.throttle = .7f; controls.pitch = controls.roll = controls.yaw = 0;
                            foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
                        }
                    }
                    yield return null;
                }
                foreach (Case test in aerial)
                {
                    Vector3 launch = test.PendingLaunchPosition;
                    Vector3 aim = (test.Target.transform.position - launch).normalized;
                    Quaternion rotation = Quaternion.LookRotation(Quaternion.AngleAxis(12f, Vector3.up) * aim);
                    if (test.Key == "rsl_bmd_exo")
                        Require(checks, "projectile-rsl_bmd_exo-native-target-hot", test.Target.HasIRSignature());
                    CaptureHealth(test);
                    if (test.Target is Aircraft)
                    {
                        UnitPart[] registered = test.Target.GetAllParts().Where(p => p != null && p.parentUnit == test.Target).ToArray();
                        Require(checks, "projectile-" + test.Key + "-native-registered-health-roster", test.OriginalParts.Length > 1 &&
                            test.OriginalParts.Length == registered.Length && registered.All(test.OriginalParts.Contains));
                    }
                    test.Primary = Spawn(test, launch, rotation, rotation * Vector3.forward * 60f);
                    Check(checks, "projectile-" + test.Key + "-native-spawn", test.Primary.Missile != null &&
                        test.Primary.Missile.definition == NaturalWeapons.Definitions[test.Key] && test.Primary.Missile.LocalSim, ref passed);
                }
                IEnumerator observe = Observe(aerial, requested.Contains("rsl_cruise") ? 75f : 26f, report, output);
                while (observe.MoveNext()) yield return observe.Current;
                foreach (Case test in aerial)
                {
                    Finish(test);
                    Shot shot = test.Primary;
                    float switchTime = NaturalWeapons.Definitions[test.Key].unitPrefab.GetComponent<NaturalWeaponPhase>().SwitchSeconds;
                    Check(checks, "projectile-" + test.Key + "-sustained-finite-flight", shot.Finite && shot.Travel > 300f && shot.PeakSpeed > 100f &&
                        shot.AliveSeconds >= Mathf.Max(3f, switchTime + .1f), ref passed);
                    Check(checks, "projectile-" + test.Key + "-source-flight-stage", shot.FlightModel, ref passed);
                    Check(checks, "projectile-" + test.Key + "-armed-flight", shot.ArmedFlight, ref passed);
                    Check(checks, "projectile-" + test.Key + "-guidance-turn", shot.TrackedSeconds > 1f && shot.Turn > 2f, ref passed);
                    Check(checks, "projectile-" + test.Key + "-motor-effects-attached",
                        (test.Key == "rsl_cruise" ? shot.NativeBoosterParticlesSeen : shot.MotorParticlesSeen) && shot.MaximumNozzleError < .15f, ref passed);
                    Check(checks, "projectile-" + test.Key + "-motor-audio-played", shot.MotorAudioSeen, ref passed);
                    if (test.Key == "rsl_cruise")
                        Check(checks, "projectile-rsl_cruise-native-booster-handoff", shot.NativeBoosterActivated &&
                            shot.NativeBoosterSeparated && shot.NativeBoosterAttachedSeconds > 11f && shot.FlightModel, ref passed);
                    if (test.Key == "rsl_ashm" || test.Key == "rsl_cruise")
                    {
                        Check(checks, "projectile-" + test.Key + "-sustained-sea-skimming", shot.Travel > 1000f &&
                            (test.Key == "rsl_cruise" ? shot.NativeOpticalTerminalSeen && shot.NativeOpticalLowCruiseSeconds >= 8f && !shot.WaterEntry :
                                shot.ActiveRadarLock && (shot.AboveWaterFlightSeconds >= 8f || shot.Detonated && test.DamageCalls > 0)) &&
                            (!shot.Detonated || test.DamageCalls > 0), ref passed);
                        Check(checks, "projectile-" + test.Key + "-native-hull-contact-damage", shot.ArmorImpact && test.DamageCalls > 0 &&
                            test.HealthAfter < test.HealthBefore, ref passed);
                    }
                    else
                        Check(checks, "projectile-" + test.Key + (test.Target is Missile ? "-crossing-ballistic-intercept" : "-crossing-aircraft-intercept"), shot.Detonated && shot.MinimumTargetDistance < 80f &&
                            test.DamageCalls > 0 && test.HealthAfter < test.HealthBefore, ref passed);
                }
                Save(output, report);
                ClearSpawned();
                yield return null;

                observe = SupplementalTrials(requested, hostile, targetDefinition, detail, report, checks, output, ok => passed &= ok);
                while (observe.MoveNext()) yield return observe.Current;

                foreach (string key in new[] { "rsl_torpedo", "rsl_asw_payload", "rsl_asw", "rsl_fulmar" })
                {
                    if (!requested.Contains(key)) continue;
                    bool carrier = key == "rsl_asw" || key == "rsl_fulmar";
                    var test = new Case { Key = key };
                    GlobalPosition a, b; Vector3 heading;
                    FindWaterLeg(3, carrier ? 1500f : 500f, out a, out b, out heading);
                    test.Target = SpawnTargetShip(targetDefinition, hostile, b, heading, "water_" + key);
                    casesByTarget.Add(test.Target, test);
                    Know(ship.NetworkHQ, test.Target);
                    yield return null; yield return new WaitForFixedUpdate();
                    test.HealthBefore = Health(test.Target);
                    test.Report["key"] = key;
                    test.Report["targetType"] = test.Target.definition.jsonKey;
                    test.Report["targetGlobalStart"] = V(test.Target.GlobalPosition().AsVector3());
                    test.Report["samples"] = test.Samples;
                    results.Add(test.Report);
                    Vector3 launch = a.ToLocalPosition(); launch.y = Datum.LocalSeaY + (carrier ? 90f : 12f);
                    Quaternion rotation = Quaternion.LookRotation(Quaternion.AngleAxis(carrier ? 8f : 20f, Vector3.up) * heading);
                    test.Report["diagnosticLaunchGlobalPosition"] = V(launch.ToGlobalPosition().AsVector3());
                    test.Primary = Spawn(test, launch, rotation, rotation * Vector3.forward * (carrier ? 40f : 12f));
                    report["phase"] = "original-weapon-water-" + key;
                    observe = Observe(new[] { test }, carrier ? 65f : 38f, report, output);
                    while (observe.MoveNext()) yield return observe.Current;
                    Finish(test);
                    Shot torpedo = carrier ? test.Payload : test.Primary;
                    if (carrier)
                        Check(checks, "projectile-" + key + "-native-payload-release", test.Payload != null && test.Primary.FlightModel, ref passed);
                    Check(checks, "projectile-" + key + "-underwater-running", torpedo != null && torpedo.Finite && torpedo.WaterEntry &&
                        torpedo.RadarSilent && torpedo.UnderwaterTravel > 60f && torpedo.UnderwaterSeconds > 2f &&
                        torpedo.PeakUnderwaterSpeed >= 20f && torpedo.FlightModel, ref passed);
                    Check(checks, "projectile-" + key + "-underwater-guidance", torpedo != null && torpedo.Turn > 2f && torpedo.TrackedSeconds > 1f, ref passed);
                    Check(checks, "projectile-" + key + "-native-hull-hit-damage", torpedo != null && torpedo.Detonated &&
                        test.DamageCalls > 0 && test.HealthAfter < test.HealthBefore, ref passed);
                    Save(output, report);
                    ClearSpawned();
                    yield return null;
                }
                detail["success"] = passed;
                Save(output, report);
            }
            finally
            {
                ClearSpawned();
                if (hooks != null) hooks.UnpatchSelf();
                hooks = null; owner = null;
            }
        }

        private static IEnumerator SupplementalTrials(HashSet<string> requested, FactionHQ hostile, ShipDefinition targetDefinition,
            Dictionary<string, object> detail, Dictionary<string, object> report, List<object> checks, string output, Action<bool> done)
        {
            bool passed = true;
            var results = new List<object>(); detail["extendedCases"] = results;
            if (requested.Contains("rsl_ashm") && !DenseSalvoOnly)
            {
                IEnumerator reference = NativeAfterburnerReference(report, checks, output, ok => passed &= ok);
                while (reference.MoveNext()) yield return reference.Current;
            }
            if (!DenseSalvoOnly)
            {
                IEnumerator longRange = LongRangeTrials(requested, hostile, targetDefinition, results, report, checks, output, ok => passed &= ok);
                while (longRange.MoveNext()) yield return longRange.Current;
            }
            foreach (string key in new[] { "rsl_bmd", "rsl_bmd_exo" })
            {
                if (!requested.Contains(key)) continue;
                var test = new Case { Key = key, ControlledTarget = true };
                GlobalPosition seaA, seaB; Vector3 heading;
                FindWaterLeg(key == "rsl_bmd" ? 5 : 6, 2000f, out seaA, out seaB, out heading);
                Ship launcher = SpawnTargetShip(targetDefinition, hostile, seaB, heading, "exo_owner_" + key);
                Vector3 targetPoint = seaA.ToLocalPosition() + heading * 10000f;
                targetPoint.y = Datum.LocalSeaY + 60000f;
                Vector3 crossing = Vector3.Cross(Vector3.up, heading);
                MissileDefinition native = NativeBallistic();
                Know(hostile, owner);
                Missile target = NetworkSceneSingleton<Spawner>.i.SpawnMissile(native, targetPoint, Quaternion.LookRotation(crossing), crossing * 700f, owner, launcher);
                spawned.Add(target.gameObject); test.Target = target;
                test.TargetVelocity = crossing * 700f; test.TargetAltitude = targetPoint.y;
                casesByTarget.Add(target, test);
                // This native missile is a controlled moving test body. Its
                // guidance cannot steer or self-destruct the synthetic track.
                NaturalWeapons.Set(target, "seeker", null);
                test.Report["key"] = key; test.Report["scenario"] = "exoatmospheric-crossing-missile";
                test.Report["targetType"] = target.definition.jsonKey; test.Report["targetVelocityMps"] = V(test.TargetVelocity);
                test.Report["ballisticClassification"] = NaturalBallisticSelection.CaptureClassification(target);
                test.Report["targetAltitudeMetres"] = 60000f; test.Report["samples"] = test.Samples;
                test.Report["scope"] = "Native target missile follows a controlled crossing trajectory at 60 km altitude; tested interceptor retains native collision, warhead, seeker, propulsion and physical reaction-control forces.";
                results.Add(test.Report);
                float warm = Time.time;
                while (Time.time - warm < 2f) { MaintainTarget(test); Know(owner.NetworkHQ, target); yield return new WaitForFixedUpdate(); }
                target.SetTangible(true);
                test.Report["targetTangible"] = target.IsTangible();
                Require(checks, "projectile-" + key + "-exo-target-hot", target.HasIRSignature());
                test.HealthBefore = Health(target);
                Vector3 launch = target.transform.position - heading * 8000f;
                Quaternion rotation = Quaternion.LookRotation(Quaternion.AngleAxis(12f, Vector3.up) * heading);
                test.Primary = Spawn(test, launch, rotation, rotation * Vector3.forward * 1100f);
                report["phase"] = "original-weapon-exo-" + key;
                IEnumerator observe = Observe(new[] { test }, 18f, report, output);
                while (observe.MoveNext()) yield return observe.Current;
                Finish(test);
                Check(checks, "projectile-" + key + "-physical-reaction-control", test.Primary.Finite && test.Primary.ReactionControlSeconds > 1f &&
                    test.Primary.ReactionDeltaV > 50f && test.Primary.ReactionPeakAcceleration > 1f && test.Primary.ReactionJetsSeen &&
                    test.Primary.ReactionCapture && test.Primary.MaximumSteadyAttackAngle < 35f, ref passed);
                Check(checks, "projectile-" + key + "-exo-moving-missile-intercept", test.Primary.Detonated && test.Primary.MinimumTargetDistance < 80f &&
                    test.DamageCalls > 0 && test.HealthAfter < test.HealthBefore, ref passed);
                Save(output, report); ClearSpawned(); yield return null;
            }
            // Bastion's authored AAW role no longer permits ships or land units.
            // NaturalMissileTargetingTrial exercises those blocked native launch
            // paths; its existing aircraft, missile and long-range flight cases
            // remain the positive acceptance evidence.
            if (requested.Contains("rsl_ashm") && !DenseSalvoOnly)
            {
                IEnumerator volley = NativeVlsSalvo(hostile, targetDefinition, results, report, checks, output, ok => passed &= ok);
                while (volley.MoveNext()) yield return volley.Current;
            }
            if (requested.Contains("rsl_ashm") && DenseSalvoOnly)
            {
                var test = new Case { Key = "rsl_ashm", ControlledTarget = true };
                GlobalPosition a, b; Vector3 heading;
                FindWaterLeg(0, 6000f, out a, out b, out heading);
                Require(checks, "projectile-pike-dense-lane-clear-of-owner-and-terrain", ClearDenseLane(ref a, ref b, heading, test.Report));
                test.Report["launchLaneStart"] = V(a.AsVector3()); test.Report["launchLaneEnd"] = V(b.AsVector3());
                test.Report["ownerPosition"] = V(owner.GlobalPosition().AsVector3());
                test.Target = SpawnTargetShip(targetDefinition, hostile, b, heading, "pike_salvo");
                test.TargetVelocity = test.Target.transform.forward * 8f; test.TargetAltitude = test.Target.transform.position.y;
                casesByTarget.Add(test.Target, test); Know(owner.NetworkHQ, test.Target);
                yield return null; yield return new WaitForFixedUpdate();
                // Increase the target's health only so later salvo rounds can
                // strike the same intact collider after the first warhead.
                foreach (UnitPart part in ((Ship)test.Target).parts) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);
                test.HealthBefore = Health(test.Target); test.Report["key"] = test.Key; test.Report["scenario"] = "four-missile-moving-ship-salvo";
                test.Report["scope"] = "Four native network projectiles share one moving ship target. The original short lane is translated only if required to clear existing ship hulls by radius plus500 m and sampled native terrain across a600 m corridor. Target health is raised to preserve its collider through successive real warhead impacts. Initial22 m lateral/12 m stagger spacing and native missile physics remain unchanged.";
                test.Report["samples"] = test.Samples; results.Add(test.Report);
                test.Report["fineSamples"] = test.FineSamples;
                test.Report["fineSamplingScope"] = "Every native fixed observation from missile age 5.5 through 9 seconds, including the initial disturbance and possible subsequent sibling collision. No forces or pose writes are added.";
                Vector3 launch = a.ToLocalPosition(); launch.y = Datum.LocalSeaY + 100f;
                Vector3 lateral = Vector3.Cross(Vector3.up, heading);
                for (int n = 0; n < 4; n++)
                {
                    Shot shot = Spawn(test, launch + lateral * ((n - 1.5f) * 22f) - heading * (n % 2 * 12f), Quaternion.LookRotation(heading), heading * 60f);
                    if (n == 0) test.Primary = shot; else test.Salvo.Add(shot);
                }
                SalvoDistances(test); test.InitialSalvoSpan = test.MaximumSalvoSpan;
                report["phase"] = "original-weapon-pike-salvo";
                IEnumerator observe = Observe(new[] { test }, 30f, report, output);
                while (observe.MoveNext()) yield return observe.Current;
                Finish(test);
                Shot[] volley = new[] { test.Primary }.Concat(test.Salvo).ToArray();
                // This simultaneous seeded fixture starts inside Pike's authored terminal
                // window. Native cruise grouping is proved by the 60 km trial.
                Check(checks, "projectile-pike-salvo-source-terminal-physical-separation", volley.All(s => s.Finite && s.NativeSourceTerminalObserved) &&
                    test.MaximumSalvoSpan > test.InitialSalvoSpan + 10f && test.MinimumFormationSeparation > 5f &&
                    test.MinimumFormationSeparation < float.MaxValue, ref passed);
                Check(checks, "projectile-pike-salvo-terminal-jink-and-flares", volley.All(s => s.JinkOffset > .1f && s.EmittedFlares >= 2 && s.FlareParticlesSeen), ref passed);
                Check(checks, "projectile-pike-salvo-multiple-native-hull-hits", volley.Count(s => s.ArmorImpact && s.IntendedTargetContact) >= 2 && test.DamageCalls >= 2 &&
                    test.HealthAfter < test.HealthBefore, ref passed);
                int liveSiblingContacts = volley.Sum(s => s.LiveSiblingContacts);
                bool contactTraceComplete = volley.All(s => s.NativeEvents.Count < 256);
                test.Report["liveSiblingContactEventCount"] = liveSiblingContacts;
                test.Report["nativeContactTraceComplete"] = contactTraceComplete;
                test.Report["intendedPhysicalContactCount"] = volley.Count(s => s.ArmorImpact && s.IntendedTargetContact);
                Check(checks, "projectile-pike-salvo-all-four-intended-native-hull-hits",
                    volley.Length == 4 && volley.All(s => s.ArmorImpact && s.IntendedTargetContact), ref passed);
                Check(checks, "projectile-pike-salvo-no-live-sibling-physical-contacts",
                    contactTraceComplete && liveSiblingContacts == 0, ref passed);
                Save(output, report); ClearSpawned(); yield return null;
            }
            detail["extendedSuccess"] = passed; done(passed);
        }

        private static IEnumerator NativeVlsSalvo(FactionHQ hostile, ShipDefinition targetDefinition, List<object> results,
            Dictionary<string, object> report, List<object> checks, string output, Action<bool> done)
        {
            bool passed = true;
            Ship previousOwner = owner;
            var test = new Case { Key = "rsl_ashm", ControlledTarget = true };
            test.Report["key"] = test.Key; test.Report["scenario"] = "four-native-vls-rounds-moving-ship";
            test.Report["scope"] = "Four explicit native WeaponStation.Fire commands from one fresh Resolute magazine. Physical launch transforms, vertical ejection, ammunition, hatch animation, owner physics, eligibility, attack demand, launcher cooldown and the owned native FireControl salvo interval remain unchanged. Commands are spaced by the larger native interval. Target motion is a controlled 8 m/s crossing ship with refreshed accurate HQ tracks and starting part health raised to 30000 solely to retain collision geometry through successive native impacts. This is normal VLS flight acceptance, not autonomous target selection or a guarantee against collisions in the separately retained simultaneous 22 m stress fixture.";
            test.Report["samples"] = test.Samples; test.Report["fineSamples"] = test.FineSamples;
            var launches = new List<Dictionary<string, object>>(); test.Report["nativeLaunches"] = launches;
            var hatchRest = new Dictionary<int, Quaternion>();
            results.Add(test.Report);
            report["phase"] = "original-weapon-pike-native-vls-salvo"; Save(output, report);
            try
            {
                GlobalPosition a, b; Vector3 heading;
                var corridor = new Dictionary<string, object>(); test.Report["nativeWaterCorridor"] = corridor;
                FindLongWaterLeg(7000f, corridor, out a, out b, out heading);
                corridor["scope"] = "Native terrain checked 7 km corridor; no sea-lane segment length clamping. The final translated corridor is additionally checked at 18 m minimum depth across 2 km width, with end padding for ship footprints, and against existing physical ship envelopes. The width covers the target's controlled crossing throughout the bounded observation.";
                Require(checks, "projectile-pike-vls-salvo-clear-water-lane", (b - a).magnitude >= 6500f &&
                    ClearDenseLane(ref a, ref b, heading, test.Report, 18f));
                test.Report["launchLaneStart"] = V(a.AsVector3()); test.Report["launchLaneEnd"] = V(b.AsVector3());
                owner = NetworkSceneSingleton<Spawner>.i.SpawnShip(previousOwner.definition.unitPrefab,
                    a + Vector3.up * previousOwner.definition.spawnOffset.y, Quaternion.LookRotation(heading),
                    previousOwner.NetworkHQ, "rsl_native_vls_salvo_owner", 1f, true);
                spawned.Add(owner.gameObject); MissionTrial.HoldControllers(owner); owner.SetHoldPosition(true);
                test.Target = SpawnTargetShip(targetDefinition, hostile, b, heading, "native_vls_salvo");
                casesByTarget.Add(test.Target, test);
                yield return null; yield return new WaitForFixedUpdate(); yield return null;
                test.TargetVelocity = test.Target.transform.forward * 8f; test.TargetAltitude = test.Target.transform.position.y;
                foreach (UnitPart part in ((Ship)test.Target).parts) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);
                test.HealthBefore = Health(test.Target);
                ResoluteVlsLauncher launcher = owner.GetComponentsInChildren<ResoluteVlsLauncher>()
                    .Where(l => l.missile.jsonKey == "rsl_ashm" && l.ammo >= 4).OrderBy(l => l.name, StringComparer.Ordinal).First();
                WeaponStation station = owner.weaponStations.Single(s => s.Weapons.Contains(launcher));
                FireControl controller = owner.GetComponent<NaturalFireControlGroups>().ControllerFor(launcher.info);
                float launcherInterval = NaturalArmament.Get<float>(launcher, "fireInterval");
                float salvoInterval = NaturalArmament.Get<float>(controller, "salvoInterval");
                float interval = Mathf.Max(launcherInterval, salvoInterval);
                int initialAmmo = launcher.ammo;
                test.Report["launcher"] = launcher.name; test.Report["station"] = station.Number;
                test.Report["nativeLauncherIntervalSeconds"] = launcherInterval;
                test.Report["nativeFireControlSalvoIntervalSeconds"] = salvoInterval;
                test.Report["commandIntervalSeconds"] = interval; test.Report["ammoBefore"] = initialAmmo;
                test.Report["ownerId"] = owner.persistentID.ToString(); test.Report["targetId"] = test.Target.persistentID.ToString();
                test.Report["targetDefinition"] = test.Target.definition.jsonKey;
                test.Report["nativeTargetDamageTolerance"] = test.Target.definition.damageTolerance;
                test.Report["nativeAttacksNeeded"] = launcher.info.CalcAttacksNeeded(test.Target);
                test.Report["stationWeaponCount"] = station.Weapons.Count;
                Require(checks, "projectile-pike-vls-salvo-native-owned-station", station.Weapons.Count == 1 &&
                    controller != null && owner.LocalSim && owner.IsServer && !owner.disabled && interval > 0f);
                float ready = Time.time + Mathf.Max(2f, launcherInterval -
                    (Time.timeSinceLevelLoad - NaturalArmament.Get<float>(launcher, "lastFired")) + .05f);
                while (Time.time < ready) { MaintainTarget(test); Know(owner.NetworkHQ, test.Target); yield return new WaitForFixedUpdate(); }
                float began = Time.time, nextLaunch = began;
                Action launchWhenReady = () =>
                {
                    foreach (Dictionary<string, object> row in launches)
                    {
                        int cell = (int)row["cellBefore"];
                        Quaternion rest = hatchRest[cell];
                        float angle = Quaternion.Angle(rest, launcher.PhysicalCells[cell].localRotation);
                        row["maximumObservedHatchOpenDegrees"] = Mathf.Max((float)row["maximumObservedHatchOpenDegrees"], angle);
                    }
                    if (launches.Count >= 4 || Time.time < nextLaunch) return;
                    MaintainTarget(test); Know(owner.NetworkHQ, test.Target);
                    int cellBefore = launcher.CurrentCell, ammoBefore = launcher.ammo;
                    Transform point = NaturalArmament.Get<Transform[]>(launcher, "launchTransforms")[cellBefore];
                    Vector3 inherited = owner.rb.velocity;
                    hatchRest[cellBefore] = launcher.PhysicalCells[cellBefore].localRotation;
                    var rowNew = new Dictionary<string, object> {
                        ["index"] = launches.Count, ["secondsSinceFirstCommand"] = Time.time - began,
                        ["nativeLevelTime"] = Time.timeSinceLevelLoad,
                        ["secondsSincePreviousFire"] = Time.timeSinceLevelLoad - NaturalArmament.Get<float>(launcher, "lastFired"),
                        ["cellBefore"] = cellBefore, ["cellName"] = point.name, ["ammoBefore"] = ammoBefore,
                        ["hatchName"] = launcher.PhysicalCells[cellBefore].name,
                        ["maximumObservedHatchOpenDegrees"] = 0f,
                        ["physicalLaunchGlobalPosition"] = V(point.position.ToGlobalPosition().AsVector3()),
                        ["physicalLaunchForward"] = V(point.forward), ["ownerInheritedVelocity"] = V(inherited),
                        ["nativeEjectionVelocityLocal"] = V(NaturalArmament.Get<Vector3>(launcher, "ejectionVelocity")),
                        ["precommandGate"] = MissionTrial.LaunchGateEvidence(owner, station, test.Target) };
                    launches.Add(rowNew); Save(output, report);
                    bool eligible = NaturalMissileTargeting.CanLaunch(launcher, station, owner, test.Target, out string reason);
                    Require(checks, "projectile-pike-vls-round-" + launches.Count + "-legal-native-gate", eligible);
                    var before = new HashSet<Missile>(UnitRegistry.allUnits.OfType<Missile>());
                    station.Fire(owner, test.Target);
                    Missile[] actual = UnitRegistry.allUnits.OfType<Missile>().Where(m => !before.Contains(m) &&
                        m.ownerID == owner.persistentID && m.definition.jsonKey == test.Key).ToArray();
                    rowNew["ammoAfter"] = launcher.ammo; rowNew["cellAfter"] = launcher.CurrentCell;
                    rowNew["firedCellEmpty"] = !launcher.IsOccupied(cellBefore); rowNew["nativeSpawnCount"] = actual.Length;
                    foreach (Missile missile in actual)
                    {
                        spawned.Add(missile.gameObject);
                        var shot = new Shot { Case = test, SourceKey = test.Key, Missile = missile, Start = missile.transform.position,
                            Last = missile.transform.position, Forward = missile.transform.forward };
                        shots.Add(missile, shot);
                        if (test.Primary == null) test.Primary = shot; else test.Salvo.Add(shot);
                    }
                    Require(checks, "projectile-pike-vls-round-" + launches.Count + "-one-owned-targeted-native-spawn",
                        actual.Length == 1 && actual[0].targetID == test.Target.persistentID && launcher.ammo == ammoBefore - 1 && !launcher.IsOccupied(cellBefore));
                    Missile fired = actual[0];
                    rowNew["missileId"] = fired.persistentID.ToString();
                    rowNew["actualSpawnGlobalPosition"] = V(fired.GlobalPosition().AsVector3());
                    rowNew["actualSpawnForward"] = V(fired.transform.forward);
                    rowNew["nativeStartingVelocity"] = V(fired.startingVelocity);
                    rowNew["spawnPositionErrorM"] = Vector3.Distance(fired.transform.position, point.position);
                    rowNew["nativeVerticalEjectionMps"] = Vector3.Dot(fired.startingVelocity - inherited, Vector3.up);
                    nextLaunch = Time.time + interval;
                };
                launchWhenReady();
                IEnumerator observe = Observe(new[] { test }, 85f, report, output, launchWhenReady, () => launches.Count == 4);
                while (observe.MoveNext()) yield return observe.Current;
                Finish(test);
                Shot[] volley = new[] { test.Primary }.Concat(test.Salvo).Where(s => s != null).ToArray();
                int contacts = volley.Sum(s => s.LiveSiblingContacts);
                bool complete = volley.All(s => s.NativeEvents.Count < 256);
                test.Report["ammoAfter"] = launcher.ammo; test.Report["intendedPhysicalContactCount"] = volley.Count(s => s.ArmorImpact && s.IntendedTargetContact);
                test.Report["liveSiblingContactEventCount"] = contacts; test.Report["nativeContactTraceComplete"] = complete;
                Check(checks, "projectile-pike-vls-salvo-four-native-cells-and-cadence", launches.Count == 4 && initialAmmo - launcher.ammo == 4 &&
                    launches.Select(r => (int)r["cellBefore"]).Distinct().Count() == 4 &&
                    launches.All(r => (float)r["secondsSincePreviousFire"] + .001f >= launcherInterval) &&
                    launches.Zip(launches.Skip(1), (aRow, bRow) => (float)bRow["nativeLevelTime"] - (float)aRow["nativeLevelTime"]).All(dt => dt + .001f >= interval), ref passed);
                Check(checks, "projectile-pike-vls-salvo-real-launch-clearance", volley.Length == 4 && volley.All(s => s.Finite && s.NativeSourceTerminalObserved) &&
                    launches.All(r => (float)r["spawnPositionErrorM"] < .1f && (float)r["nativeVerticalEjectionMps"] > 55f &&
                        (float)r["maximumObservedHatchOpenDegrees"] > 1f) &&
                    complete && !volley.SelectMany(s => s.NativeEvents).OfType<Dictionary<string, object>>().Any(e =>
                        e.ContainsKey("contactedId") && (string)e["contactedId"] == owner.persistentID.ToString()), ref passed);
                Check(checks, "projectile-pike-vls-salvo-terminal-jink-and-flares", volley.Length == 4 &&
                    volley.All(s => s.JinkOffset > .1f && s.EmittedFlares >= 2 && s.FlareParticlesSeen), ref passed);
                Check(checks, "projectile-pike-vls-salvo-all-four-intended-native-hull-hits", volley.Length == 4 &&
                    volley.All(s => s.ArmorImpact && s.IntendedTargetContact) && test.DamageCalls >= 4 && test.HealthAfter < test.HealthBefore, ref passed);
                Check(checks, "projectile-pike-vls-salvo-no-live-sibling-physical-contacts", complete && contacts == 0, ref passed);
                Save(output, report); done(passed);
            }
            finally
            {
                foreach (GameObject go in spawned.ToArray())
                    if (go != null && go.GetComponent<Missile>() is Missile missile) RetireMissileFixture(missile, true);
                foreach (GameObject go in spawned.ToArray())
                    if (go != null && go.GetComponent<Missile>() == null) RetireFixtureObject(go, true);
                spawned.Clear(); shots.Clear(); casesByTarget.Clear(); owner = previousOwner;
            }
            yield return null;
        }

        private static bool ClearDenseLane(ref GlobalPosition start, ref GlobalPosition end, Vector3 heading, Dictionary<string, object> report, float minimumWaterDepth = -.5f)
        {
            Vector3 lateral = Vector3.Cross(Vector3.up, heading);
            GlobalPosition originalStart = start, originalEnd = end;
            Ship[] obstacles = UnitRegistry.allUnits.OfType<Ship>().Where(s => s != null && !s.disabled).ToArray();
            var radii = new Dictionary<Ship, float>();
            var physicalHulls = new List<object>();
            foreach (Ship obstacle in obstacles)
            {
                Collider[] hulls = obstacle.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger &&
                    c.gameObject.activeInHierarchy && c.gameObject.layer != PhysicsLayers.ExclusionZones).ToArray();
                float radius = Mathf.Max(1f, obstacle.maxRadius);
                foreach (Collider hull in hulls)
                    radius = Mathf.Max(radius, Vector3.Distance(hull.bounds.center, obstacle.transform.position) + hull.bounds.extents.magnitude);
                radii[obstacle] = radius;
                physicalHulls.Add(new Dictionary<string, object> { ["unitId"] = obstacle.persistentID.ToString(),
                    ["position"] = V(obstacle.GlobalPosition().AsVector3()), ["physicalEnvelopeRadiusM"] = radius,
                    ["colliders"] = hulls.Select(c => new Dictionary<string, object> { ["name"] = c.name,
                        ["center"] = V(c.bounds.center.ToGlobalPosition().AsVector3()), ["size"] = V(c.bounds.size) }).ToArray() });
            }
            report["excludedPhysicalHullEnvelopes"] = physicalHulls;
            for (int attempt = 0; attempt < 25; attempt++)
            {
                float offset = attempt == 0 ? 0f : ((attempt + 1) / 2) * 2000f * (attempt % 2 == 0 ? -1f : 1f);
                GlobalPosition a = originalStart + lateral * offset, b = originalEnd + lateral * offset;
                Vector3 delta = b - a; float squareLength = delta.sqrMagnitude;
                bool clear = true;
                float minimumClearance = float.MaxValue;
                foreach (Ship obstacle in obstacles)
                {
                    Vector3 relative = obstacle.GlobalPosition() - a;
                    Vector3 closest = delta * Mathf.Clamp01(Vector3.Dot(relative, delta) / Mathf.Max(1f, squareLength));
                    relative -= closest; relative.y = 0f;
                    minimumClearance = Mathf.Min(minimumClearance, relative.magnitude - radii[obstacle]);
                    if (relative.magnitude < radii[obstacle] + 500f) { clear = false; break; }
                }
                if (!clear) continue;
                int firstProbe = minimumWaterDepth > 0f ? -1 : 0, lastProbe = minimumWaterDepth > 0f ? 13 : 12;
                int sideProbes = minimumWaterDepth > 0f ? 3 : 1;
                float halfWidth = minimumWaterDepth > 0f ? 1000f : 300f;
                for (int along = firstProbe; along <= lastProbe && clear; along++)
                for (int side = -sideProbes; side <= sideProbes && clear; side++)
                {
                    GlobalPosition point = a + delta * (along / 12f) + lateral * (side * halfWidth / sideProbes);
                    if (PathfindingAgent.RaycastTerrain(point, out RaycastHit hit) && hit.point.y > Datum.LocalSeaY - minimumWaterDepth) clear = false;
                }
                if (!clear) continue;
                report["minimumPhysicalHullEnvelopeClearanceM"] = minimumClearance;
                if (minimumWaterDepth > 0f)
                {
                    report["finalMinimumWaterDepthM"] = minimumWaterDepth;
                    report["finalWaterCorridorHalfWidthM"] = halfWidth;
                    report["finalWaterCorridorEndPaddingM"] = delta.magnitude / 12f;
                }
                report["laneTranslationM"] = offset;
                start = a; end = b; return true;
            }
            return false;
        }

        internal static MissileDefinition NativeBallistic()
        {
            if (NaturalBallisticReferenceTrial.AryxLoaded)
            {
                MissileDefinition starfall;
                if (NaturalBallisticReferenceTrial.TryGetStarfall(out starfall)) return starfall;
                throw new InvalidOperationException("The genuine Aryx plugin is loaded, so this fixture requires registered Starfall. Await NaturalBallisticReferenceTrial.WaitUntilReady before spawning; stock fallback is disabled for this run.");
            }
            return Encyclopedia.i.missiles.Where(d => d != null && d.unitPrefab != null && !d.jsonKey.StartsWith("rsl_") &&
                NaturalBallisticSelection.IsEligibleDefinition(d)).OrderByDescending(d => d.jsonKey == "Aryx_Hypersonic1")
                .ThenBy(d => d.value).First();
        }

        private static IEnumerator NativeAfterburnerReference(Dictionary<string, object> report, List<object> checks, string output, Action<bool> done)
        {
            var audit = new Dictionary<string, object>();
            report["nativeAfterburnerReference"] = audit;
            report["phase"] = "native-afterburner-camera-reference";
            AircraftDefinition definition = Encyclopedia.i.aircraft.First(d => d != null && d.jsonKey == "Fighter1");
            GlobalPosition position = owner.GlobalPosition() + Vector3.right * 5000f; position.y = Datum.SeaLevel.y + 1200f;
            Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, definition.unitPrefab,
                definition.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), position, Quaternion.identity,
                Vector3.forward * 300f, null, owner.NetworkHQ, "rsl_native_afterburner_reference", 1f, 1f);
            spawned.Add(aircraft.gameObject);
            yield return null; yield return new WaitForFixedUpdate();
            JetNozzle nozzle = aircraft.GetAllParts().Where(p => p != null && p.parentUnit == aircraft)
                .SelectMany(p => p.GetComponentsInChildren<JetNozzle>(true))
                .Concat(aircraft.GetComponentsInChildren<JetNozzle>(true)).Distinct()
                .First(n => ((Array)NaturalWeapons.Get(n, "afterburners")).Length > 0);
            object native = ((Array)NaturalWeapons.Get(nozzle, "afterburners")).GetValue(0);
            float throttle = (float)NaturalWeapons.Get(native, "throttleEnd");
            Transform frame = (Transform)NaturalWeapons.Get(nozzle, "thrustTransform");
            float started = Time.time, realStarted = Time.realtimeSinceStartup;
            int readyFrame = -1;
            while (Time.time - started < 30f && Time.realtimeSinceStartup - realStarted < 90f)
            {
                foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                aircraft.NetworkIgnition = true;
                ControlInputs controls = aircraft.GetInputs(); controls.throttle = throttle;
                controls.pitch = controls.roll = controls.yaw = 0f;
                aircraft.rb.useGravity = false; aircraft.rb.constraints = RigidbodyConstraints.FreezeRotation;
                aircraft.rb.velocity = Vector3.forward * 300f;
                foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
                float amount = (float)NaturalWeapons.Get(native, "afterburnerAmount");
                if (amount > .9f && readyFrame < 0) readyFrame = Time.frameCount;
                if (readyFrame >= 0 && Time.frameCount >= readyFrame + 2) break;
                yield return null;
            }
            audit["definition"] = definition.jsonKey;
            audit["nativeThrottleEnd"] = throttle;
            audit["nativeWarmupSeconds"] = Time.time - started;
            audit["renderedFramesAfterWarmup"] = readyFrame >= 0 ? Time.frameCount - readyFrame : 0;
            audit["effects"] = NaturalWeaponAudit.CaptureNativeAfterburnerState(nozzle, frame);
            float framingLength = NaturalWeapons.Definitions["rsl_ashm"].length;
            string side = NaturalWeaponAudit.CaptureFrame(frame, framingLength, output, "flight_native_fighter_afterburner_side");
            string rear = NaturalWeaponAudit.CaptureFrame(frame, framingLength, output, "flight_native_fighter_afterburner_rear", 2);
            audit["screenshots"] = new[] { side, rear };
            audit["propertyBlockControl"] = NaturalWeaponAudit.CaptureNativeGlowPropertyBlockControl(nozzle, frame, framingLength, output);
            bool passed = true;
            Check(checks, "projectile-native-afterburner-reference-warmed-and-captured", readyFrame >= 0 &&
                !string.IsNullOrEmpty(side) && !string.IsNullOrEmpty(rear), ref passed);
            Save(output, report); ClearSpawned(); yield return null; done(passed);
        }
        private static IEnumerator LongRangeTrials(HashSet<string> requested, FactionHQ hostile, ShipDefinition targetDefinition,
            List<object> results, Dictionary<string, object> report, List<object> checks, string output, Action<bool> done)
        {
            bool passed = true;
            var cases = new List<Case>();
            foreach (string key in new[] { "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd_exo" })
            {
                if (!requested.Contains(key)) continue;
                int copies = key == "rsl_bmd_exo" ? 2 : 1;
                for (int copy = 0; copy < copies; copy++)
                {
                    int lane = cases.Count;
                    Vector3 axis = Quaternion.AngleAxis(35f + lane * 54f, Vector3.up) * Vector3.forward;
                    float distance = key == "rsl_mrsam" ? 70000f : key == "rsl_lrsam" ? 120000f : 180000f;
                    Vector3 launch = owner.transform.position + axis * 3000f;
                    launch.y = Datum.LocalSeaY + 100f;
                    Vector3 targetPoint = launch + axis * distance;
                    targetPoint.y = Datum.LocalSeaY + (key == "rsl_bmd_exo" ? 20000f : 5000f);
                    Vector3 crossing = Vector3.Cross(Vector3.up, axis) * (key == "rsl_bmd_exo" ? 500f : 180f);
                    var test = new Case { Key = key, ControlledTarget = true, TargetVelocity = crossing, TargetAltitude = targetPoint.y };
                    if (key == "rsl_bmd_exo")
                    {
                        Ship launcher = SpawnTargetShip(targetDefinition, hostile, owner.GlobalPosition() + axis * 10000f, axis, "loft_owner_" + lane);
                        Know(hostile, owner);
                        Missile target = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NativeBallistic(), targetPoint,
                            Quaternion.LookRotation(crossing), crossing, owner, launcher);
                        NaturalWeapons.Set(target, "seeker", null);
                        test.Target = target; spawned.Add(target.gameObject);
                        test.Report["ballisticClassification"] = NaturalBallisticSelection.CaptureClassification(target);
                    }
                    else
                    {
                        AircraftDefinition aircraft = NativeAircraft();
                        Aircraft target = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, aircraft.unitPrefab,
                            aircraft.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), targetPoint.ToGlobalPosition(),
                            Quaternion.LookRotation(crossing), crossing, null, hostile, "rsl_loft_target_" + lane, 1f, 1f);
                        test.Target = target; spawned.Add(target.gameObject);
                    }
                    casesByTarget.Add(test.Target, test); Know(owner.NetworkHQ, test.Target);
                    test.Report["key"] = key; test.Report["scenario"] = copy == 1 ? "long-range-native-direct-comparison" : "long-range-source-loft";
                    test.Report["scope"] = "Native network projectile from 100 m altitude at 60 m/s vertical launch; target follows a controlled crossing track. Projectile motion, motor, air density, steering and collision remain physical. The comparison copy only disables the new loft component.";
                    test.Report["targetType"] = test.Target.definition.jsonKey;
                    test.Report["initialHorizontalRangeMetres"] = distance;
                    test.PendingLaunchPosition = launch; test.PendingLaunchDirection = Vector3.up;
                    test.Report["diagnosticLaunchGlobalPosition"] = V(launch.ToGlobalPosition().AsVector3());
                    test.Report["comparison"] = copy == 1;
                    test.Report["samples"] = test.Samples; results.Add(test.Report); cases.Add(test);
                }
            }
            if (requested.Contains("rsl_ashm"))
            {
                GlobalPosition a, b; Vector3 heading;
                var oceanSearch = new Dictionary<string, object>();
                report["pikeOceanSearch"] = oceanSearch;
                FindLongWaterLeg(60000f, oceanSearch, out a, out b, out heading);
                Require(checks, "projectile-pike-observes-cruise-before-terminal-range", (b - a).magnitude >= 59999f &&
                    (b - a).magnitude > NaturalWeapons.Definitions["rsl_ashm"].unitPrefab.GetComponent<NaturalWeaponPhase>().TerminalRange + 10000f);
                var test = new Case { Key = "rsl_ashm", ControlledTarget = true };
                test.Target = SpawnTargetShip(targetDefinition, hostile, b, heading, "pike_terminal_transition");
                test.TargetVelocity = test.Target.transform.forward * 8f; test.TargetAltitude = test.Target.transform.position.y;
                casesByTarget.Add(test.Target, test); Know(owner.NetworkHQ, test.Target);
                Vector3 launch = a.ToLocalPosition(); launch.y = Datum.LocalSeaY + 100f;
                test.Report["key"] = test.Key; test.Report["scenario"] = "cruise-to-terminal-afterburner";
                test.Report["scope"] = "A 60 km physical flight crosses the source's 25 nmi terminal threshold, with visible native afterburner, actual increased motor thrust, native jink steering and native flares.";
                test.Report["verifiedWaterLegMetres"] = (b - a).magnitude;
                test.Report["terrainCheckSpacingMetres"] = 1000f;
                test.Report["terrainCheckedCorridorHalfWidthMetres"] = 120f;
                test.PendingLaunchPosition = launch; test.PendingLaunchDirection = heading;
                test.Report["diagnosticLaunchGlobalPosition"] = V(launch.ToGlobalPosition().AsVector3());
                test.Report["diagnosticLaunchDirection"] = V(heading);
                test.Report["samples"] = test.Samples;
                results.Add(test.Report); cases.Add(test);
            }
            if (cases.Count == 0) { done(true); yield break; }
            float warm = Time.time + 2f;
            while (Time.time < warm)
            {
                foreach (Case test in cases) { MaintainTarget(test); Know(owner.NetworkHQ, test.Target); }
                yield return new WaitForFixedUpdate();
            }
            foreach (Case test in cases)
            {
                Vector3 launch = test.PendingLaunchPosition;
                bool pike = test.Key == "rsl_ashm";
                Vector3 direction = test.PendingLaunchDirection;
                CaptureHealth(test);
                test.Primary = Spawn(test, launch, Quaternion.LookRotation(direction, pike ? Vector3.up : Vector3.forward), direction * 60f);
                if (!pike && (bool)test.Report["comparison"]) test.Primary.Missile.GetComponent<NaturalLoftGuidance>().enabled = false;
            }
            report["phase"] = "original-weapon-long-range-profiles";
            IEnumerator observe = Observe(cases, 85f, report, output);
            while (observe.MoveNext()) yield return observe.Current;
            foreach (Case test in cases)
            {
                Finish(test);
                Shot shot = test.Primary;
                if (test.Key == "rsl_ashm")
                {
                    Check(checks, "projectile-pike-native-afterburner-and-physical-boost", shot.Finite && shot.AfterburnerSeen && shot.CruiseFlameCapture &&
                        shot.TerminalFlameCapture && shot.TerminalThrust > shot.CruiseThrust * 2f && shot.TerminalPeakSpeed > shot.CruisePeakSpeed * 1.15f, ref passed);
                    Check(checks, "projectile-pike-visible-flares-and-physical-weave", shot.EmittedFlares >= 2 && shot.FlareCapture &&
                        shot.MaximumLateralSpeed > 5f && shot.LateralReversals >= 2, ref passed);
                }
                else
                {
                    bool comparison = (bool)test.Report["comparison"];
                    string checkKey = test.Key + (comparison ? "-direct-comparison" : "");
                    if (!comparison)
                        Check(checks, "projectile-" + checkKey + "-physical-midcourse-loft", shot.Finite && shot.LoftSeconds > 5f &&
                            shot.AltitudeAtTwentySeconds > 3000f && shot.MaximumAltitude > 5000f, ref passed);
                    Check(checks, "projectile-" + checkKey + "-long-range-native-intercept", shot.Detonated && shot.MinimumTargetDistance < 80f &&
                        test.DamageCalls > 0 && test.HealthAfter < test.HealthBefore, ref passed);
                    Check(checks, "projectile-" + checkKey + "-stable-midcourse-flight", shot.Finite && shot.MidcourseSeconds > 10f &&
                        shot.MidcourseMaximumAttackAngle <= 20f && shot.MidcourseBackwardsSeconds == 0f && shot.MidcourseExcessiveTurnSeconds < .25f &&
                        (!shot.FinnedLoft || shot.MidcourseMinimumDensity >= shot.MinimumDesignDensity * .5f), ref passed);
                }
            }
            Case loft = cases.FirstOrDefault(c => c.Key == "rsl_bmd_exo" && !(bool)c.Report["comparison"]);
            Case direct = cases.FirstOrDefault(c => c.Key == "rsl_bmd_exo" && (bool)c.Report["comparison"]);
            if (loft != null && direct != null)
                Check(checks, "projectile-bastion-x-loft-clears-native-direct-profile", loft.Primary.AltitudeAtTwentySeconds > direct.Primary.AltitudeAtTwentySeconds + 500f, ref passed);
            Save(output, report); ClearSpawned(); yield return null; done(passed);
        }
        internal static AircraftDefinition NativeAircraft()
        {
            AircraftDefinition definition = Encyclopedia.i.aircraft.FirstOrDefault(d => d != null && d.jsonKey == "Multirole1" &&
                d.unitPrefab != null && d.aircraftParameters != null && d.aircraftParameters.loadouts.Count > 0);
            if (definition == null) throw new InvalidOperationException("Native aircraft diagnostic requires the registered Multirole1 prefab and loadout.");
            return definition;
        }
        private static void MaintainTarget(Case test)
        {
            if (!test.ControlledTarget || test.Target == null || test.Target.disabled || test.Target.rb == null) return;
            Rigidbody rb = test.Target.rb;
            rb.useGravity = false; rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.velocity = test.TargetVelocity; rb.angularVelocity = Vector3.zero;
            Vector3 position = rb.position; position.y = test.TargetAltitude; rb.position = position;
            if (test.Target is Aircraft aircraft)
            {
                foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                aircraft.NetworkIgnition = true;
                ControlInputs controls = aircraft.GetInputs(); controls.throttle = .7f; controls.pitch = controls.roll = controls.yaw = 0f;
                foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
            }
        }
        private static bool TargetSlowChecks(ARHSeeker __instance)
        {
            Case test;
            return !casesByTarget.TryGetValue(__instance.GetComponent<Missile>(), out test) || !test.ControlledTarget;
        }
        private static void SalvoDistances(Case test)
        {
            Shot[] salvo = new[] { test.Primary }.Concat(test.Salvo).Where(s => s != null && s.Missile != null && !s.Missile.disabled).ToArray();
            for (int a = 0; a < salvo.Length; a++)
            for (int b = a + 1; b < salvo.Length; b++)
            {
                float distance = Vector3.Distance(salvo[a].Missile.transform.position, salvo[b].Missile.transform.position);
                test.MaximumSalvoSpan = Mathf.Max(test.MaximumSalvoSpan, distance);
                test.MinimumSalvoSeparation = Mathf.Min(test.MinimumSalvoSeparation, distance);
                // Formation separation ends at native terminal closeout, when
                // missiles deliberately converge on one shared impact point.
                if (test.Target != null && new[] { salvo[a], salvo[b] }.All(s =>
                    Vector3.Distance(s.Missile.transform.position, test.Target.transform.position) > Mathf.Max(1000f, s.Missile.speed * 2f)))
                    test.MinimumFormationSeparation = Mathf.Min(test.MinimumFormationSeparation, distance);
            }
        }
        private static void CaptureFlight(Case test, string output)
        {
            Shot shot = test.Primary;
            if (shot == null || shot.Missile == null || shot.Missile.disabled || shot.Missile.timeSinceSpawn < .15f) return;
            int stage = (int)NaturalWeapons.Get(shot.Missile, "motorStage");
            if (shot.ObservedStage != stage) { shot.ObservedStage = stage; shot.StageObservedTime = Time.time; }
            string scenario = test.Report.ContainsKey("scenario") ? (string)test.Report["scenario"] : "crossing";
            if (!test.Report.ContainsKey("flightScreenshots")) test.Report["flightScreenshots"] = new List<object>();
            var screenshots = (List<object>)test.Report["flightScreenshots"];
            if (Environment.GetCommandLineArgs().Contains("--resolute-missile-visual-review"))
            {
                NaturalWeaponPhase motorPhase = shot.Missile.GetComponent<NaturalWeaponPhase>();
                float time = shot.Missile.timeSinceSpawn;
                string moment = time < .45f ? "ignition" : time > .8f && time < 1.5f ? "first_burn" :
                    motorPhase != null && time > motorPhase.SwitchSeconds - .25f && time < motorPhase.SwitchSeconds ? "before_separation" :
                    motorPhase != null && time > motorPhase.SwitchSeconds + .15f && time < motorPhase.SwitchSeconds + .8f ? "after_separation" : null;
                if (moment != null && shot.CapturedMotorMoments.Add(moment))
                {
                    test.Report["motor_" + moment] = new Dictionary<string, object> {
                        ["time"] = time, ["stage"] = stage, ["state"] = MissileVisualTrial.MotorState(shot.Missile),
                        ["side"] = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "motor_" + test.Key + "_" + scenario + "_" + moment + "_side"),
                        ["rear"] = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "motor_" + test.Key + "_" + scenario + "_" + moment + "_rear", 2) };
                }
            }
            if (shot.Missile.timeSinceSpawn < .7f) return;
            bool particles = shot.Missile.GetComponentsInChildren<ParticleSystem>().Any(p => p.particleCount > 3 &&
                !p.name.StartsWith("ReactionControlJet", StringComparison.Ordinal));
            if (shot.Missile.EngineOn() && Time.time - shot.StageObservedTime > .25f && particles && shot.CapturedStages.Add(stage))
                screenshots.Add(NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_" + test.Key + "_" + scenario + "_stage" + stage));
            NaturalWeaponEffects effects = shot.Missile.GetComponent<NaturalWeaponEffects>();
            NaturalWeaponPhase phase = shot.Missile.GetComponent<NaturalWeaponPhase>();
            if (test.Key == "rsl_cruise")
            {
                string moment = shot.Missile.boosterIsAttached && shot.NativeBoosterParticlesSeen ? "native_booster_burning" :
                    !shot.Missile.boosterIsAttached && shot.Missile.EngineOn() && shot.MotorAudioSeen ? "native_sustainer_running" : null;
                if (moment != null && shot.CapturedMotorMoments.Add(moment))
                {
                    test.Report[moment] = new Dictionary<string, object> {
                        ["time"] = shot.Missile.timeSinceSpawn, ["boosterAttached"] = shot.Missile.boosterIsAttached,
                        ["engineOn"] = shot.Missile.EngineOn(), ["mainMotorThrustNewtons"] = NaturalWeapons.Get(shot.Missile, "engineCurrentThrust"),
                        ["side"] = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_spear_" + scenario + "_" + moment + "_side"),
                        ["rear"] = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_spear_" + scenario + "_" + moment + "_rear", 2) };
                }
            }
            if (test.Key == "rsl_ashm" && phase != null && effects != null && stage == 1 && shot.Missile.timeSinceSpawn > 5f)
            {
                if (!shot.CruiseFlameCapture && !phase.TerminalBoostActive)
                {
                    test.Report["cruiseEffectsAtCapture"] = NaturalWeaponAudit.CaptureEffectsState(shot.Missile);
                    string side = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_pike_" + scenario + "_cruise_side");
                    string rear = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_pike_" + scenario + "_cruise_rear", 2);
                    shot.CruiseFlameCapture = !string.IsNullOrEmpty(side) && !string.IsNullOrEmpty(rear);
                    if (shot.CruiseFlameCapture) { screenshots.Add(side); screenshots.Add(rear); }
                }
                if (!shot.TerminalFlameCapture && effects.AfterburnerAmount > .9f)
                {
                    test.Report["terminalEffectsAtCapture"] = NaturalWeaponAudit.CaptureEffectsState(shot.Missile);
                    string side = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_pike_" + scenario + "_terminal_side");
                    string rear = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_pike_" + scenario + "_terminal_rear", 2);
                    shot.TerminalFlameCapture = !string.IsNullOrEmpty(side) && !string.IsNullOrEmpty(rear);
                    if (shot.TerminalFlameCapture) { screenshots.Add(side); screenshots.Add(rear); }
                }
                if (!shot.FlareCapture && shot.EmittedFlares >= 2)
                {
                    NaturalFlareOwner[] flares = NaturalWeaponObserver.OwnedFlares(shot.Missile);
                    NaturalFlareOwner[] pair = flares.Where(f => Vector3.Distance(f.transform.position, shot.Missile.transform.position) > 3f &&
                        NaturalWeaponAudit.NativeFlareGlowPositions(f).Length > 0).Take(2).ToArray();
                    bool ready = pair.Length == 2;
                    if (!ready) shot.FlareReadyFrame = -1;
                    if (ready && shot.FlareReadyFrame < 0) shot.FlareReadyFrame = Time.frameCount;
                    if (ready && Time.frameCount >= shot.FlareReadyFrame + 2)
                    {
                        test.Report["nativeFlareCaptureState"] = new Dictionary<string, object> {
                            ["renderedFramesAfterPairReady"] = Time.frameCount - shot.FlareReadyFrame,
                            ["view"] = "Rear quarter from above, fitted to the live missile, owned flare roots and actual native world-space glow particles after rendered updates. Smoke alone cannot satisfy readiness.",
                            ["attribution"] = "Actual flare ownership witnessed at emission and retained only by the diagnostic after native IR ownership releases beyond 100 m.",
                            ["missileGlobalPosition"] = V(shot.Missile.GlobalPosition().AsVector3()),
                            ["flares"] = pair.Select(f => new Dictionary<string, object> {
                                ["nativeFlareObjectId"] = f.GetInstanceID(),
                                ["globalPosition"] = V(f.transform.GlobalPosition().AsVector3()),
                                ["relativeToMissile"] = V(shot.Missile.transform.InverseTransformPoint(f.transform.position)),
                                ["nativeIROwnerStillRegistered"] = f.Owner == shot.Missile,
                                ["nativeGlowGlobalPositions"] = NaturalWeaponAudit.NativeFlareGlowPositions(f).Select(v => V(v.ToGlobalPosition().AsVector3())).ToArray(),
                                ["nativeParticles"] = f.GetComponentsInChildren<ParticleSystem>().Select(p => new Dictionary<string, object> {
                                    ["name"] = p.name, ["count"] = p.particleCount, ["playing"] = p.isPlaying
                                }).ToArray()
                            }).ToArray()
                        };
                        string capture = NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_pike_" + scenario + "_native_flares", 3);
                        object detailFraming;
                        string detail = NaturalWeaponAudit.CaptureNativeFlareDetail(shot.Missile, pair, output,
                            "flight_pike_" + scenario + "_native_flares_detail", out detailFraming);
                        var captureState = (Dictionary<string, object>)test.Report["nativeFlareCaptureState"];
                        captureState["wideImage"] = capture; captureState["detailImage"] = detail;
                        captureState["detailView"] = detailFraming;
                        shot.FlareCapture = !string.IsNullOrEmpty(capture) && !string.IsNullOrEmpty(detail);
                        if (shot.FlareCapture) { screenshots.Add(capture); screenshots.Add(detail); }
                    }
                }
            }
            NaturalReactionControl rcs = shot.Missile.GetComponent<NaturalReactionControl>();
            if (!shot.ReactionCapture && rcs != null && rcs.ActiveSeconds > .3f && rcs.LastAcceleration.magnitude > 15f &&
                rcs.Jets.Any(p => p != null && p.enabled))
            {
                shot.ReactionCapture = true;
                screenshots.Add(NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_" + test.Key + "_" + scenario + "_rcs_side", 0));
                screenshots.Add(NaturalWeaponAudit.CaptureFlight(shot.Missile, output, "flight_" + test.Key + "_" + scenario + "_rcs_above", 1));
                test.Report["reactionCaptureAcceleration"] = V(rcs.LastAcceleration);
                test.Report["reactionCaptureVisibleNozzles"] = rcs.Jets.Select(p => p != null && p.enabled).ToArray();
            }
        }

        private static Shot Spawn(Case test, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Missile missile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions[test.Key], position, rotation,
                velocity, test.Target, owner);
            spawned.Add(missile.gameObject);
            var shot = new Shot { Case = test, SourceKey = test.Key, Missile = missile, Start = position, Last = position, Forward = rotation * Vector3.forward };
            shots.Add(missile, shot);
            return shot;
        }
        private static IEnumerator Observe(IEnumerable<Case> sequence, float seconds, Dictionary<string, object> report, string output,
            Action beforeObservation = null, Func<bool> launchScheduleComplete = null)
        {
            Case[] cases = sequence.ToArray();
            float started = Time.time, realStarted = Time.realtimeSinceStartup, nextSample = 0;
            float allEndedAt = -1f;
            var particleObservers = cases.Where(c => c.Key == "rsl_ashm" || c.Key == "rsl_cruise")
                // The observer's optional retained-flare attribution belongs
                // to the last created observer. Preserve Pike's attribution.
                .OrderBy(c => c.Key == "rsl_ashm" ? 1 : 0)
                .Select(c => Tuple.Create(c, NaturalWeaponObserver.Create(c.Primary.Missile))).ToArray();
            try
            {
            while (Time.time - started < seconds && Time.realtimeSinceStartup - realStarted < seconds * 3f + 15f)
            {
                beforeObservation?.Invoke();
                foreach (Case test in cases)
                {
                    MaintainTarget(test);
                    if (test.Target != null) Know(owner.NetworkHQ, test.Target);
                    ObserveShot(test.Primary);
                    foreach (Shot salvo in test.Salvo) ObserveShot(salvo);
                    if (test.Salvo.Count > 0 && test.Primary.Missile != null &&
                        Time.time - started >= 5.5f && Time.time - started <= 9f)
                    {
                        if (!test.Report.ContainsKey("liveSalvoColliderInventory"))
                            test.Report["liveSalvoColliderInventory"] = new[] { test.Primary }.Concat(test.Salvo)
                                .Where(s => s.Missile != null).Select(s => new Dictionary<string, object> {
                                    ["missileId"] = s.Missile.persistentID.ToString(),
                                    ["colliders"] = s.Missile.GetComponentsInChildren<Collider>(true).Select(c => new Dictionary<string, object> {
                                        ["name"] = c.name, ["layer"] = c.gameObject.layer, ["enabled"] = c.enabled,
                                        ["trigger"] = c.isTrigger, ["type"] = c.GetType().Name,
                                        ["boundsSize"] = V(c.bounds.size), ["centerOffset"] = V(c.bounds.center - s.Missile.transform.position)
                                    }).ToArray() }).ToArray();
                        test.FineSamples.Add(new Dictionary<string, object> { ["seconds"] = Time.time - started,
                            ["projectile"] = Sample(test.Primary), ["salvo"] = test.Salvo.Select(Sample).ToArray() });
                    }
                    if (test.Salvo.Count > 0) SalvoDistances(test);
                    if (test.Payload != null) ObserveShot(test.Payload);
                    test.HealthAfter = CurrentHealth(test);
                    CaptureFlight(test, output);
                }
                if (Time.time - started >= nextSample)
                {
                    nextSample += .5f;
                    foreach (Case test in cases)
                    {
                        test.Samples.Add(new Dictionary<string, object> {
                            ["seconds"] = Time.time - started, ["projectile"] = Sample(test.Primary), ["payload"] = Sample(test.Payload),
                            ["salvo"] = test.Salvo.Select(Sample).ToArray(),
                            ["targetHealth"] = test.HealthAfter, ["nativeDamageCalls"] = test.DamageCalls
                        });
                        test.Report["primary"] = test.Primary.Json();
                        test.Report["payload"] = test.Payload != null ? test.Payload.Json() : null;
                    }
                    Save(output, report);
                }
                if ((launchScheduleComplete == null || launchScheduleComplete()) &&
                    cases.All(c => (c.Primary.Missile == null || c.Primary.Missile.disabled) &&
                    c.Salvo.All(s => s.Missile == null || s.Missile.disabled) &&
                    (c.Payload == null || c.Payload.Missile == null || c.Payload.Missile.disabled)))
                {
                    if (allEndedAt < 0f) allEndedAt = Time.time;
                    // Native fragments wait for physics; large-warhead
                    // shockwaves propagate over subsequent rendered frames.
                    if (Time.time - allEndedAt >= .75f) break;
                }
                else allEndedAt = -1f;
                yield return new WaitForFixedUpdate();
            }
            }
            finally
            {
                foreach (var observed in particleObservers)
                {
                    NaturalWeaponObserver observer = observed.Item2;
                    if (observer == null) continue;
                    observed.Item1.Report["nativeParticleObserver"] = new Dictionary<string, object> {
                        ["observedRenderedFrames"] = observer.ObservedFrames,
                        ["submittedNativeRenderRequests"] = observer.ObservedFrames,
                        ["nativeRenderRequestsSupported"] = observer.RenderRequestsSupported,
                        ["maximumRenderRequestsPerSimulatedSecond"] = 30,
                        ["attributedNativeFlares"] = observer.AttributedFlareCount,
                        ["maximumLiveFlaresWithNativeGlow"] = observer.MaximumLiveGlowFlares,
                        ["scope"] = "Explicit 128px native game-camera render requests, at most 30 per simulated second, follow this missile and its actually observed native flare roots/glow particles. Only submitted renders are counted; camera configuration and native shader globals are restored after each. Diagnostic attribution survives the production 100 m IR-owner release. Native Automatic culling, particle count, velocity and lifetime remain unchanged. Camera helpers, render texture and attribution references are destroyed after observation."
                    };
                    observer.DisposeObserver();
                }
            }
        }
        private static void ObserveShot(Shot shot)
        {
            if (shot == null || shot.Missile == null) return;
            Missile missile = shot.Missile;
            shot.Finite &= IsFinite(missile.transform.position) && IsFinite(missile.rb.velocity);
            shot.Last = missile.transform.position;
            shot.Travel = Mathf.Max(shot.Travel, Vector3.Distance(shot.Start, shot.Last));
            shot.PeakSpeed = Mathf.Max(shot.PeakSpeed, missile.rb.velocity.magnitude);
            shot.MaximumAltitude = Mathf.Max(shot.MaximumAltitude, missile.GlobalPosition().y);
            if (missile.timeSinceSpawn >= 20f && shot.AltitudeAtTwentySeconds < 0f)
                shot.AltitudeAtTwentySeconds = missile.GlobalPosition().y;
            NaturalLoftGuidance loft = missile.GetComponent<NaturalLoftGuidance>();
            if (loft != null && loft.Active && !missile.disabled) shot.LoftSeconds += Time.fixedDeltaTime;
            if (loft != null && loft.InitialRange > 0f && !shot.LoftSizingCaptured)
            {
                shot.LoftSizingCaptured = true;
                shot.FinnedLoft = missile.GetComponent<NaturalReactionControl>() == null;
                shot.MinimumDesignDensity = loft.MinimumDesignDensity;
                shot.Case.Report["loftProfileSizing"] = loft.CaptureSizing();
            }
            float distanceToTarget = shot.Case.Target != null ? Vector3.Distance(missile.transform.position, shot.Case.Target.transform.position) : 0f;
            float delta = shot.PreviousObservationTime >= 0f ? missile.timeSinceSpawn - shot.PreviousObservationTime : 0f;
            if (!missile.disabled && delta > 0f && missile.timeSinceSpawn > Mathf.Max(5f, loft != null ? loft.InitialFlightSeconds + 1f : 5f) &&
                distanceToTarget > Mathf.Max(1000f, missile.speed * 3f))
            {
                float attackAngle = Vector3.Angle(missile.transform.forward, missile.rb.velocity);
                float headingRate = Vector3.Angle(shot.PreviousHeading, missile.transform.forward) / delta;
                float nativeTurnLimit = Mathf.Min((float)NaturalWeapons.Get(missile, "maxTurnRate"),
                    (float)NaturalWeapons.Get(missile, "gLimit") * 9.81f / Mathf.Max(1f, missile.speed) * Mathf.Rad2Deg) * Mathf.Sqrt(2f) + 5f;
                shot.MidcourseSeconds += delta;
                shot.MidcourseMaximumAttackAngle = Mathf.Max(shot.MidcourseMaximumAttackAngle, attackAngle);
                shot.MidcourseMinimumDensity = Mathf.Min(shot.MidcourseMinimumDensity, missile.airDensity);
                shot.MidcourseMaximumHeadingRate = Mathf.Max(shot.MidcourseMaximumHeadingRate, headingRate);
                if (attackAngle > 90f) shot.MidcourseBackwardsSeconds += delta;
                if (headingRate > nativeTurnLimit) shot.MidcourseExcessiveTurnSeconds += delta;
            }
            shot.PreviousHeading = missile.transform.forward;
            shot.PreviousObservationTime = missile.timeSinceSpawn;
            if (shot.SourceKey == "rsl_cruise" && !missile.disabled)
            {
                NaturalSurfaceLaunch launch = missile.GetComponent<NaturalSurfaceLaunch>();
                VLSBooster booster = launch != null ? launch.Booster : null;
                if (booster != null)
                {
                    shot.NativeBoosterActivated |= (bool)NaturalWeapons.Get(booster, "activated");
                    bool separated = (bool)NaturalWeapons.Get(booster, "separated");
                    shot.NativeBoosterSeparated |= separated && !missile.boosterIsAttached && !booster.transform.IsChildOf(missile.transform);
                    if (shot.NativeBoosterSeparated && shot.NativeBoosterSeparationTime < 0f)
                        shot.NativeBoosterSeparationTime = missile.timeSinceSpawn;
                    if (missile.boosterIsAttached && booster.transform.IsChildOf(missile.transform))
                    {
                        shot.NativeBoosterAttachedSeconds += Mathf.Max(0f, delta);
                        shot.NativeBoosterParticlesSeen |= ((Array)NaturalWeapons.Get(booster, "particleSystems")).Cast<ParticleSystem>()
                            .Any(p => p != null && p.transform.IsChildOf(booster.transform) && p.isPlaying && p.particleCount > 0);
                    }
                }
                OpticalSeekerCruiseMissile optical = missile.GetComponent<OpticalSeekerCruiseMissile>();
                if (optical != null)
                {
                    bool terminal = (bool)NaturalWeapons.Get(optical, "terminalMode");
                    shot.NativeOpticalTerminalSeen |= terminal;
                    if (!missile.boosterIsAttached && !terminal && missile.timeSinceSpawn > 3f &&
                        missile.radarAlt > 1f && missile.radarAlt < 100f)
                        shot.NativeOpticalLowCruiseSeconds += Mathf.Max(0f, delta);
                }
            }
            if (shot.Case.Target != null && !shot.Case.Target.disabled)
                shot.MinimumTargetDistance = Mathf.Min(shot.MinimumTargetDistance, Vector3.Distance(missile.transform.position, shot.Case.Target.transform.position));
            if (missile.timeSinceSpawn > 3f && !missile.disabled)
                shot.MaximumSteadyAttackAngle = Mathf.Max(shot.MaximumSteadyAttackAngle, Vector3.Angle(missile.transform.forward, missile.rb.velocity));
            NaturalWeaponEffects effects = missile.GetComponent<NaturalWeaponEffects>();
            if (effects != null && effects.Nozzle != null && !missile.disabled)
            {
                shot.AfterburnerSeen |= effects.AfterburnerVisible && effects.TerminalFlame != null && effects.TerminalFlame.enabled && effects.NativeNozzleGlowReady;
                NaturalWeaponPhase visiblePhase = missile.GetComponent<NaturalWeaponPhase>();
                bool nativeCruise = visiblePhase != null && visiblePhase.NativeCruisePipeline;
                float tail = (nativeCruise ? visiblePhase.FlightModelVisible : missile.timeSinceSpawn >= effects.SwitchSeconds)
                    ? effects.FlightTail : effects.LaunchTail;
                // Reproject through the current missile frame: rendering may
                // run between fixed steps, so compare local mounting position.
                Vector3 nozzle = missile.transform.InverseTransformPoint(effects.Nozzle.position);
                float separationTime = nativeCruise ? shot.NativeBoosterSeparationTime : effects.SwitchSeconds;
                if (separationTime < 0f || Mathf.Abs(missile.timeSinceSpawn - separationTime) > .1f)
                    shot.MaximumNozzleError = Mathf.Max(shot.MaximumNozzleError, Vector3.Distance(nozzle, new Vector3(0f, 0f, tail)));
                int stage = (int)NaturalWeapons.Get(missile, "motorStage");
                Array motors = (Array)NaturalWeapons.Get(missile, "motors");
                if (stage < motors.Length)
                {
                    object motor = motors.GetValue(stage);
                    shot.MotorParticlesSeen |= ((Array)NaturalWeapons.Get(motor, "particleSystems")).Cast<ParticleSystem>().Any(p => p != null && p.isPlaying && p.particleCount > 0);
                    shot.MotorAudioSeen |= ((Array)NaturalWeapons.Get(motor, "audioSources")).Cast<AudioSource>().Any(a => a != null && a.enabled && a.isPlaying && a.clip != null);
                }
            }
            NaturalCruiseTactics tactics = missile.GetComponent<NaturalCruiseTactics>();
            if (tactics != null)
            {
                shot.EmittedFlares = tactics.FlareCount; shot.FlareParticlesSeen |= tactics.NativeFlareParticlesSeen;
                shot.GroupMembers = Mathf.Max(shot.GroupMembers, tactics.NearbyGroupMembers);
                shot.JinkOffset = tactics.LargestJink;
                NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
                if (controller != null)
                {
                    shot.NativeGroupingUpdates = controller.UpdatesWithNeighbors;
                    NaturalWeaponPhase cruisePhase = missile.GetComponent<NaturalWeaponPhase>();
                    shot.NativeSourceTerminalObserved |= controller.TerminalReleased && cruisePhase != null && cruisePhase.TerminalBoostActive;
                }
            }
            NaturalReactionControl control = missile.GetComponent<NaturalReactionControl>();
            if (control != null)
            {
                shot.ReactionControlSeconds = control.ActiveSeconds; shot.ReactionDeltaV = control.AppliedDeltaV;
                shot.ReactionPeakAcceleration = control.PeakAcceleration; shot.ReactionJetsSeen |= control.JetsSeen;
            }
            if (!missile.disabled) shot.AliveSeconds = Mathf.Max(shot.AliveSeconds, missile.timeSinceSpawn);
            shot.Turn = Mathf.Max(shot.Turn, Vector3.Angle(shot.Forward, missile.transform.forward));
            if (!missile.disabled && shot.Case.Target != null && missile.targetID == shot.Case.Target.persistentID)
                shot.TrackedSeconds += Time.fixedDeltaTime;
            shot.ActiveRadarLock |= !missile.disabled && missile.seekerMode == Missile.SeekerMode.activeLock;
            shot.ArmedFlight |= !missile.disabled && missile.IsArmed() && missile.IsTangible();
            NaturalWeaponPhase phase = missile.GetComponent<NaturalWeaponPhase>();
            if (!missile.disabled && phase != null && phase.SeaSkim && missile.timeSinceSpawn > 4f)
            {
                float thrust = (float)NaturalWeapons.Get(missile, "engineCurrentThrust");
                if (phase.TerminalBoostActive)
                {
                    shot.TerminalPeakSpeed = Mathf.Max(shot.TerminalPeakSpeed, missile.speed);
                    shot.TerminalThrust = Mathf.Max(shot.TerminalThrust, thrust);
                }
                else
                {
                    shot.CruisePeakSpeed = Mathf.Max(shot.CruisePeakSpeed, missile.speed);
                    shot.CruiseThrust = Mathf.Max(shot.CruiseThrust, thrust);
                }
                if (shot.Case.Target != null && tactics != null && tactics.CurrentJink.sqrMagnitude > 1f)
                {
                    Vector3 bearing = shot.Case.Target.transform.position - missile.transform.position; bearing.y = 0f;
                    Vector3 sideways = Vector3.Cross(Vector3.up, bearing.normalized);
                    float lateral = Vector3.Dot(missile.rb.velocity, sideways);
                    shot.MaximumLateralSpeed = Mathf.Max(shot.MaximumLateralSpeed, Mathf.Abs(lateral));
                    int sign = Mathf.Abs(lateral) > 5f ? (lateral > 0f ? 1 : -1) : 0;
                    if (sign != 0 && sign != shot.LastLateralSign && missile.timeSinceSpawn - shot.LastLateralReversal > .5f)
                    {
                        if (shot.LastLateralSign != 0) shot.LateralReversals++;
                        shot.LastLateralSign = sign; shot.LastLateralReversal = missile.timeSinceSpawn;
                    }
                }
            }
            shot.FlightModel |= phase != null && phase.FlightModel.activeSelf && !phase.LaunchModel.activeSelf;
            if (!missile.disabled && phase != null && phase.FlightModel.activeSelf && shot.Last.y > Datum.LocalSeaY + 1f)
                shot.AboveWaterFlightSeconds += Time.fixedDeltaTime;
            if (shot.Last.y < Datum.LocalSeaY - .5f)
            {
                if (!shot.WaterEntry) shot.WaterStart = shot.Last;
                shot.WaterEntry = true;
                shot.RadarSilent |= missile.RCS == 0f;
                if (!missile.disabled)
                {
                    shot.UnderwaterSeconds += Time.fixedDeltaTime;
                    shot.UnderwaterTravel = Mathf.Max(shot.UnderwaterTravel, Vector3.Distance(shot.WaterStart, shot.Last));
                    shot.PeakUnderwaterSpeed = Mathf.Max(shot.PeakUnderwaterSpeed, missile.rb.velocity.magnitude);
                }
            }
        }
        private static object Sample(Shot shot)
        {
            if (shot == null) return null;
            Missile missile = shot.Missile;
            NaturalCruiseGuidance cruise = missile != null ? missile.GetComponent<NaturalCruiseGuidance>() : null;
            NaturalCruiseTactics tactics = missile != null ? missile.GetComponent<NaturalCruiseTactics>() : null;
            return new Dictionary<string, object> {
                ["persistentId"] = missile != null ? missile.persistentID.ToString() : null,
                ["cruiseGuidancePhase"] = cruise != null ? cruise.FlightPhase : null,
                ["cruiseNativeTerminalPart"] = cruise != null ? cruise.CaptureTerminalPart() : null,
                ["cruiseCommandedAltitudeM"] = cruise != null ? (object)cruise.CommandedAltitude : null,
                ["cruiseTerrainFloorM"] = cruise != null ? (object)cruise.CurrentTerrainFloor : null,
                ["cruiseCurrentTerrainM"] = cruise != null ? (object)cruise.CurrentTerrainAltitude : null,
                ["cruiseApproachTerrainPeakM"] = cruise != null ? (object)cruise.ApproachGroundPeak : null,
                ["nativeNearbyMissilesExcludingSelf"] = tactics != null ? tactics.NearbyGroupMembers : 0,
                ["nativeGroupingUpdates"] = tactics != null ? tactics.NativeGroupingUpdates : 0,
                ["nativeCruiseController"] = missile != null && missile.GetComponent<NaturalCruiseController>() != null ? missile.GetComponent<NaturalCruiseController>().Sample() : null,
                ["exists"] = missile != null, ["disabled"] = missile == null || missile.disabled,
                ["globalPosition"] = V(shot.Last.ToGlobalPosition().AsVector3()),
                ["speedMps"] = missile != null ? missile.rb.velocity.magnitude : 0,
                ["targetRetained"] = missile != null && shot.Case.Target != null && missile.targetID == shot.Case.Target.persistentID,
                ["targetHasIRSignature"] = shot.Case.Target != null && shot.Case.Target.HasIRSignature(),
                ["targetGlobalPosition"] = shot.Case.Target != null ? V(shot.Case.Target.GlobalPosition().AsVector3()) : null,
                ["velocity"] = missile != null ? V(missile.rb.velocity) : null,
                ["angularVelocity"] = missile != null ? V(missile.rb.angularVelocity) : null,
                ["angleOfAttackDegrees"] = missile != null ? Vector3.Angle(missile.rb.velocity, missile.transform.forward) : 0f,
                ["airDensity"] = missile != null ? missile.airDensity : 0f,
                ["targetVelocity"] = shot.Case.Target != null && shot.Case.Target.rb != null ? V(shot.Case.Target.rb.velocity) : null,
                ["reactionControlAcceleration"] = missile != null && missile.GetComponent<NaturalReactionControl>() != null ? V(missile.GetComponent<NaturalReactionControl>().LastAcceleration) : null,
                ["steeringInputs"] = missile != null ? V((Vector3)NaturalWeapons.Get(missile, "inputs")) : null,
                ["forward"] = missile != null ? V(missile.transform.forward) : null,
                ["aimGlobalPosition"] = missile != null ? V(((GlobalPosition)NaturalWeapons.Get(missile, "aimPoint")).AsVector3()) : null,
                ["motorStage"] = missile != null ? NaturalWeapons.Get(missile, "motorStage") : null,
                ["engineOn"] = missile != null && missile.EngineOn(),
                ["engineThrustNewtons"] = missile != null ? NaturalWeapons.Get(missile, "engineCurrentThrust") : null,
                ["terminalBoost"] = missile != null && missile.GetComponent<NaturalWeaponPhase>() != null && missile.GetComponent<NaturalWeaponPhase>().TerminalBoostActive,
                ["afterburnerVisible"] = missile != null && missile.GetComponent<NaturalWeaponEffects>() != null && missile.GetComponent<NaturalWeaponEffects>().AfterburnerVisible,
                ["currentJink"] = missile != null && missile.GetComponent<NaturalCruiseTactics>() != null ? V(missile.GetComponent<NaturalCruiseTactics>().CurrentJink) : null,
                ["loftPhase"] = missile != null && missile.GetComponent<NaturalLoftGuidance>() != null ? missile.GetComponent<NaturalLoftGuidance>().FlightPhase : null,
                ["loftCommandAltitude"] = missile != null && missile.GetComponent<NaturalLoftGuidance>() != null ? (object)missile.GetComponent<NaturalLoftGuidance>().CommandedAltitude : null,
                ["seekerMode"] = missile != null ? missile.seekerMode.ToString() : null,
                ["nativeBoosterAttached"] = missile != null && missile.boosterIsAttached,
                ["nativeOpticalTerminal"] = missile != null && missile.GetComponent<OpticalSeekerCruiseMissile>() != null &&
                    (bool)NaturalWeapons.Get(missile.GetComponent<OpticalSeekerCruiseMissile>(), "terminalMode"),
                ["nativeRadarAltitudeMetres"] = missile != null ? (object)missile.radarAlt : null,
                ["armed"] = missile != null && missile.IsArmed(),
                ["finArea"] = missile != null ? NaturalWeapons.Get(missile, "currentFinArea") : null,
                ["seeker"] = missile != null ? missile.GetComponent<MissileSeeker>().GetSeekerType() : null
            };
        }
        private static void Finish(Case test)
        {
            test.HealthAfter = CurrentHealth(test);
            test.Report["primary"] = test.Primary.Json();
            test.Report["payload"] = test.Payload != null ? test.Payload.Json() : null;
            if (test.Salvo.Count > 0)
            {
                test.Report["salvo"] = test.Salvo.Select(s => s.Json()).ToArray();
                test.Report["initialSalvoSpan"] = test.InitialSalvoSpan;
                test.Report["maximumSalvoSpan"] = test.MaximumSalvoSpan;
                test.Report["minimumSalvoSeparation"] = test.MinimumSalvoSeparation;
                test.Report["minimumFormationSeparation"] = test.MinimumFormationSeparation;
            }
            test.Report["targetHealthBefore"] = test.HealthBefore;
            test.Report["targetHealthAfter"] = test.HealthAfter;
            test.Report["nativeDamageCalls"] = test.DamageCalls;
            if (test.OriginalParts != null)
            {
                test.Report["healthBasis"] = "The same native Unit.GetAllParts registered references captured after target warm-up and immediately before launch. Native detailed-physics parts outside the aircraft transform hierarchy remain included; destroyed parts count as zero and detached parts retain their actual remaining health.";
                test.Report["originalPartCount"] = test.OriginalParts.Length;
                for (int i = 0; i < test.OriginalParts.Length; i++)
                {
                    UnitPart part = test.OriginalParts[i];
                    test.OriginalPartHealth[i]["existsAfter"] = part != null;
                    test.OriginalPartHealth[i]["healthAfter"] = part != null ? Mathf.Max(0f, part.hitPoints) : 0f;
                    test.OriginalPartHealth[i]["detachedAfter"] = part == null || part.IsDetached();
                }
                test.Report["originalPartHealth"] = test.OriginalPartHealth;
            }
        }
        private static Ship SpawnTargetShip(ShipDefinition definition, FactionHQ hostile, GlobalPosition position, Vector3 heading, string suffix)
        {
            position.y = Datum.SeaLevel.y + definition.spawnOffset.y;
            Ship ship = NetworkSceneSingleton<Spawner>.i.SpawnShip(definition.unitPrefab, position,
                Quaternion.LookRotation(Vector3.Cross(Vector3.up, heading), Vector3.up), hostile,
                "rsl_projectile_target_" + suffix, 1f, true);
            spawned.Add(ship.gameObject);
            MissionTrial.HoldControllers(ship);
            return ship;
        }
        internal static void Know(FactionHQ hq, Unit target)
        {
            if (hq == null || target == null || !target.persistentID.IsValid || target.disabled) return;
            TrackingInfo information;
            if (!hq.trackingDatabase.TryGetValue(target.persistentID, out information))
                hq.trackingDatabase.Add(target.persistentID, information = new TrackingInfo(target));
            information.UpdateInfo(target.GlobalPosition());
        }
        private static void RecordDetonation(Missile __instance, bool hitArmor)
        {
            Shot shot;
            if (!__instance.disabled && shots.TryGetValue(__instance, out shot))
            {
                ObserveShot(shot);
                shot.Detonated = true; shot.ArmorImpact = hitArmor; shot.DetonationTime = Time.time; shot.DetonationPosition = __instance.transform.position;
            }
        }
        private static void RecordDamage(UnitPart __instance, PersistentID dealerID)
        {
            Case test;
            if (owner == null || dealerID != owner.persistentID || __instance.parentUnit == null || !casesByTarget.TryGetValue(__instance.parentUnit, out test)) return;
            Shot[] attributed = new[] { test.Primary, test.Payload }.Concat(test.Salvo).Where(s => s != null).ToArray();
            bool nearby = attributed.Any(shot => shot.Detonated && Time.time - shot.DetonationTime < 2f &&
                test.Target != null && Vector3.Distance(shot.DetonationPosition, test.Target.transform.position) < 200f);
            nearby |= attributed.Any(shot => shot.Missile != null &&
                shot.Missile.GetComponent<NaturalWeaponPhase>() is NaturalWeaponPhase phase && phase.ContactedUnit == test.Target && Time.time - phase.ContactTime < .5f);
            if (nearby) test.DamageCalls++;
        }
        private static Dictionary<string, object> NativeEvent(Missile missile, string kind)
        {
            if (missile == null || !shots.TryGetValue(missile, out Shot shot) || shot.NativeEvents.Count >= 256) return null;
            var result = new Dictionary<string, object> { ["kind"] = kind, ["seconds"] = missile.timeSinceSpawn,
                ["missileId"] = missile.persistentID.ToString(), ["position"] = V(missile.GlobalPosition().AsVector3()),
                ["velocityBefore"] = V(missile.rb.velocity), ["disabledBefore"] = missile.disabled };
            shot.NativeEvents.Add(result); return result;
        }
        private static void TraceIncomingDamage(Missile __instance, float pierceDamage, float blastDamage,
            float amountAffected, float fireDamage, float impactDamage, PersistentID dealerID)
        {
            Dictionary<string, object> item = NativeEvent(__instance, "native-Missile.TakeDamage");
            if (item == null) return;
            item["dealerId"] = dealerID.ToString();
            item["dealerKey"] = dealerID.TryGetUnit(out Unit dealer) && dealer.definition != null ? dealer.definition.jsonKey : null;
            item["pierceDamage"] = pierceDamage; item["blastDamage"] = blastDamage;
            item["amountAffected"] = amountAffected; item["fireDamage"] = fireDamage; item["impactDamage"] = impactDamage;
        }
        private static void TraceShockwave(Missile __instance, Vector3 origin, float overpressure, float blastPower)
        {
            Dictionary<string, object> item = NativeEvent(__instance, "native-Missile.TakeShockwave");
            if (item == null) return;
            item["origin"] = V(origin.ToGlobalPosition().AsVector3()); item["overpressure"] = overpressure; item["blastPower"] = blastPower;
        }
        private static void TracePenetration(Missile __instance, IDamageable damageable, Vector3 hitPoint)
        {
            if (!__instance.disabled && shots.TryGetValue(__instance, out Shot shot) && damageable != null && damageable.GetUnit() == shot.Case.Target &&
                !(damageable is UnitPart detached && detached.IsDetached())) shot.IntendedTargetContact = true;
            Unit contacted = damageable != null ? damageable.GetUnit() : null;
            RecordLiveSiblingContact(__instance, contacted);
            Dictionary<string, object> item = NativeEvent(__instance, "native-Missile.PenetrateObject");
            if (item == null) return;
            item["contactedId"] = contacted != null ? contacted.persistentID.ToString() : null;
            item["contactedKey"] = contacted != null && contacted.definition != null ? contacted.definition.jsonKey : null;
            item["sameOwnerMissile"] = contacted is Missile other && other.ownerID == __instance.ownerID;
            item["contactedDisabledAtEvent"] = contacted != null ? (object)contacted.disabled : null;
            item["contactedLocalSimAtEvent"] = contacted != null ? (object)contacted.LocalSim : null;
            item["contactedComponentEnabledAtEvent"] = contacted != null ? (object)contacted.enabled : null;
            item["contactedObjectActiveAtEvent"] = contacted != null ? (object)contacted.gameObject.activeInHierarchy : null;
            item["point"] = V(hitPoint.ToGlobalPosition().AsVector3());
        }
        private static void TracePhysicalContact(NaturalWeaponPhase __instance, Collision collision)
        {
            Missile missile = __instance.GetComponent<Missile>();
            if (missile == null || missile.disabled || !shots.ContainsKey(missile) || collision == null || collision.contactCount == 0) return;
            Collider collider = collision.collider;
            UnitPart part = collider.GetComponentInParent<UnitPart>();
            Unit contacted = part != null ? part.parentUnit : collider.GetComponentInParent<Unit>();
            RecordLiveSiblingContact(missile, contacted);
            Dictionary<string, object> item = NativeEvent(missile, "native-physics-collision-callback");
            if (item == null) return;
            item["collider"] = collider.name; item["layer"] = collider.gameObject.layer;
            item["contactedId"] = contacted != null ? contacted.persistentID.ToString() : null;
            item["contactedKey"] = contacted != null && contacted.definition != null ? contacted.definition.jsonKey : null;
            item["sameOwnerMissile"] = contacted is Missile other && other.ownerID == missile.ownerID;
            item["contactedDisabledAtEvent"] = contacted != null ? (object)contacted.disabled : null;
            item["contactedLocalSimAtEvent"] = contacted != null ? (object)contacted.LocalSim : null;
            item["contactedComponentEnabledAtEvent"] = contacted != null ? (object)contacted.enabled : null;
            item["contactedObjectActiveAtEvent"] = contacted != null ? (object)contacted.gameObject.activeInHierarchy : null;
            item["nativeTerrainMaterial"] = collider.sharedMaterial == GameAssets.i.terrainMaterial;
            item["relativeVelocity"] = V(collision.relativeVelocity); item["impulse"] = V(collision.impulse);
            item["point"] = V(collision.GetContact(0).point.ToGlobalPosition().AsVector3());
        }
        private static void RecordLiveSiblingContact(Missile missile, Unit contacted)
        {
            // Runs in native prefixes before the bounded event log can fill.
            // A prior flood of damage events cannot hide a later live collision.
            if (missile != null && !missile.disabled && contacted is Missile peer && !peer.disabled &&
                peer.ownerID == missile.ownerID && shots.TryGetValue(missile, out Shot shot)) shot.LiveSiblingContacts++;
        }
        private static void RecordMissileDamage(Missile __instance, PersistentID dealerID)
        {
            Case test;
            if (owner == null || dealerID != owner.persistentID || !casesByTarget.TryGetValue(__instance, out test)) return;
            if (test.Primary != null && test.Primary.Detonated && Time.time - test.Primary.DetonationTime < 2f) test.DamageCalls++;
        }
        private static void RecordPayload(MissileDefinition missile, Unit target, Unit owner, Missile __result)
        {
            Missile carrier = owner as Missile; Shot parent;
            if (__result == null || carrier == null || missile.jsonKey != "rsl_asw_payload" || !shots.TryGetValue(carrier, out parent)) return;
            var shot = new Shot { Case = parent.Case, SourceKey = missile.jsonKey, Missile = __result, Start = __result.transform.position,
                Last = __result.transform.position, Forward = __result.transform.forward };
            parent.Case.Payload = shot; shots.Add(__result, shot); spawned.Add(__result.gameObject);
        }
        private static void ClearSpawned()
        {
            // Release native target reservations before deferred destruction of
            // either side. ARH has no OnDestroy listener that clears targetID.
            foreach (GameObject go in spawned)
                if (go != null) go.GetComponent<Missile>()?.SetTarget(null);
            foreach (GameObject go in spawned) if (go != null) Object.Destroy(go);
            spawned.Clear(); shots.Clear(); casesByTarget.Clear();
        }
        internal static void RetireMissileFixture(Missile missile, bool waitForNativeStart = false)
        {
            if (missile == null) return;
            missile.SetTarget(null);
            RetireFixtureObject(missile.gameObject, waitForNativeStart);
        }
        internal static void RetireFixtureObject(GameObject go, bool waitForNativeStart)
        {
            if (go == null) return;
            Unit unit = go.GetComponent<Unit>();
            if (!waitForNativeStart || unit == null) { Object.Destroy(go); return; }
            // Native StartMissile resumes on the next FixedUpdate and touches
            // both the projectile and its captured owner. Keep both objects
            // alive through that callback, with diagnostic physics retired.
            Missile missile = unit as Missile;
            if (missile != null) missile.SetTarget(null);
            unit.SetLocalSim(false);
            unit.enabled = false;
            if (unit.rb != null) { unit.rb.detectCollisions = false; unit.rb.isKinematic = true; }
            foreach (Collider collider in go.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            unit.StartCoroutine(RetireAfterNativeStart(go));
        }
        private static IEnumerator RetireAfterNativeStart(GameObject go)
        {
            yield return new WaitForFixedUpdate();
            yield return null;
            if (go != null) Object.Destroy(go);
        }
        private static void FindWaterLeg(int index, float requestedLength, out GlobalPosition start, out GlobalPosition target, out Vector3 heading)
        {
            var candidates = new List<Tuple<Vector3, Vector3>>();
            foreach (var road in NetworkSceneSingleton<LevelInfo>.i.seaLanes.roads.OrderByDescending(r => r.length))
            for (int n = 1; n < road.points.Count; n++)
            {
                Vector3 a = road.points[n - 1].AsVector3(), b = road.points[n].AsVector3(); a.y = b.y = Datum.SeaLevel.y;
                if (Vector3.Distance(a, b) > 2000f) candidates.Add(Tuple.Create(a, b));
            }
            for (int candidate = 0; candidate < candidates.Count; candidate++)
            {
                var line = candidates[(candidate + index) % candidates.Count];
                Vector3 direction = (line.Item2 - line.Item1).normalized;
                float available = Vector3.Distance(line.Item1, line.Item2) - 700f;
                float length = Mathf.Min(requestedLength, available);
                Vector3 a = line.Item1 + direction * 350f, b = a + direction * length;
                bool safe = true;
                for (int s = 0; s <= 8 && safe; s++)
                for (int side = -1; side <= 1; side++)
                {
                    Vector3 point = Vector3.Lerp(a, b, s / 8f) + Vector3.Cross(Vector3.up, direction) * (side * 120f);
                    RaycastHit hit;
                    if (PathfindingAgent.RaycastTerrain(new GlobalPosition(point), out hit) && hit.point.y > Datum.LocalSeaY - 18f) { safe = false; break; }
                }
                if (!safe) continue;
                start = new GlobalPosition(a); target = new GlobalPosition(b); heading = direction; return;
            }
            throw new InvalidOperationException("No clear sea lane for the original weapon projectile trial.");
        }
        private static void FindLongWaterLeg(float length, Dictionary<string, object> audit, out GlobalPosition start, out GlobalPosition target, out Vector3 heading,
            float corridorHalfWidth = 120f)
        {
            MapSettings map = NetworkSceneSingleton<LevelInfo>.i.LoadedMapSettings;
            int intervals = Mathf.CeilToInt(length / 1000f);
            audit["mapSizeMetres"] = new[] { map.MapSize.x, map.MapSize.y };
            audit["requiredDistanceMetres"] = length;
            audit["terrainCheckSpacingMetres"] = 1000f;
            audit["corridorHalfWidthMetres"] = corridorHalfWidth;
            audit["nativeOceanPresent"] = NetworkSceneSingleton<LevelInfo>.i.ocean != null;
            audit["scope"] = "Complete native terrain probes across the full required corridor. Search covers all native sea-lane nodes and a map-wide grid, then the game's continuous ocean immediately outside the mapped area. Terrain, sea level and weather are unchanged.";
            int attempts = 0;
            // Native sea-lane endpoints are often concentrated near harbors.
            // Search the complete map as well as those nodes; a dozen short
            // lane segments cannot establish that no long ocean path exists.
            for (int pass = 0; pass < 2; pass++)
            {
                float margin = pass == 0 ? -1000f : 15000f;
                float halfX = map.MapSize.x * .5f + margin, halfZ = map.MapSize.y * .5f + margin;
                var endpoints = new List<Vector3>();
                if (pass == 0)
                    foreach (var road in NetworkSceneSingleton<LevelInfo>.i.seaLanes.roads.OrderByDescending(r => r.length))
                        foreach (GlobalPosition point in road.points) endpoints.Add(point.AsVector3());
                for (int x = 0; x <= 12; x++)
                for (int z = 0; z <= 12; z++)
                    endpoints.Add(new Vector3(Mathf.Lerp(-halfX + 150f, halfX - 150f, x / 12f), Datum.SeaLevel.y,
                        Mathf.Lerp(-halfZ + 150f, halfZ - 150f, z / 12f)));
                foreach (Vector3 endpoint in endpoints)
                for (int turn = 0; turn < 48; turn++)
                {
                    Vector3 direction = Quaternion.AngleAxis(turn * 7.5f, Vector3.up) * Vector3.forward;
                    GlobalPosition end = new GlobalPosition(endpoint.x, Datum.SeaLevel.y, endpoint.z);
                    GlobalPosition begin = end - direction * length;
                    Vector3 lateral = Vector3.Cross(Vector3.up, direction);
                    attempts++;
                    // A straight segment and its constant-width corridor fit
                    // an axis-aligned rectangle when both expanded ends fit.
                    if (Mathf.Abs(begin.x) + Mathf.Abs(lateral.x) * corridorHalfWidth > halfX ||
                        Mathf.Abs(begin.z) + Mathf.Abs(lateral.z) * corridorHalfWidth > halfZ ||
                        Mathf.Abs(end.x) + Mathf.Abs(lateral.x) * corridorHalfWidth > halfX ||
                        Mathf.Abs(end.z) + Mathf.Abs(lateral.z) * corridorHalfWidth > halfZ) continue;
                    bool safe = true;
                    int terrainHits = 0, openOceanProbes = 0;
                    float minimumDepth = float.MaxValue;
                    for (int sample = 0; sample <= intervals && safe; sample++)
                    for (int side = -1; side <= 1; side++)
                    {
                        GlobalPosition point = begin + direction * (length * sample / intervals) + lateral * (side * corridorHalfWidth);
                        RaycastHit hit;
                        if (PathfindingAgent.RaycastTerrain(point, out hit))
                        {
                            terrainHits++;
                            float depth = Datum.LocalSeaY - hit.point.y;
                            minimumDepth = Mathf.Min(minimumDepth, depth);
                            if (depth < 18f) { safe = false; break; }
                        }
                        else openOceanProbes++;
                    }
                    if (!safe) continue;
                    start = begin; target = end; heading = direction;
                    audit["candidatesTested"] = attempts;
                    audit["selectedPass"] = pass == 0 ? "within-map" : "continuous-native-ocean";
                    audit["startGlobalPosition"] = V(start.AsVector3());
                    audit["targetGlobalPosition"] = V(target.AsVector3());
                    audit["direction"] = V(direction);
                    audit["verifiedDistanceMetres"] = (target - start).magnitude;
                    audit["terrainHitProbes"] = terrainHits;
                    audit["openOceanProbes"] = openOceanProbes;
                    audit["minimumMeasuredWaterDepthMetres"] = terrainHits > 0 ? (object)minimumDepth : null;
                    audit["success"] = true;
                    return;
                }
            }
            audit["candidatesTested"] = attempts; audit["success"] = false;
            throw new InvalidOperationException("No terrain-checked 60 km ocean corridor on the loaded map or its continuous native ocean for the Pike terminal-transition trial.");
        }
        private static void CaptureHealth(Case test)
        {
            // Native detailed aircraft physics unparents individual AeroParts
            // when creating their rigidbodies. Unit's registered part roster
            // remains authoritative even when hierarchy lookup sees only the body.
            test.OriginalParts = test.Target != null && !(test.Target is Missile)
                ? test.Target.GetAllParts().Where(p => p != null && p.parentUnit == test.Target).Distinct().ToArray() : null;
            if (test.OriginalParts != null)
            {
                if (test.OriginalParts.Length == 0 || test.Target is Aircraft && test.OriginalParts.Length <= 1)
                    throw new InvalidOperationException("The native target's complete registered part roster is not initialized: " + test.Key);
                test.Report["hierarchyPartCountAtLaunch"] = test.Target.GetComponentsInChildren<UnitPart>(true).Count(p => p.parentUnit == test.Target);
                test.Report["registeredPartCountAtLaunch"] = test.OriginalParts.Length;
                test.Report["registeredPartsOutsideHierarchyAtLaunch"] = test.OriginalParts.Count(p => !p.transform.IsChildOf(test.Target.transform));
                test.OriginalPartHealth.Clear();
                foreach (UnitPart part in test.OriginalParts)
                    test.OriginalPartHealth.Add(new Dictionary<string, object> {
                        ["id"] = part.id, ["name"] = part.name, ["healthBefore"] = Mathf.Max(0f, part.hitPoints),
                        ["outsideHierarchyAtLaunch"] = !part.transform.IsChildOf(test.Target.transform),
                        ["detachedAtLaunch"] = part.IsDetached()
                    });
            }
            test.HealthBefore = CurrentHealth(test);
        }
        private static float CurrentHealth(Case test)
        {
            return test.OriginalParts != null ? test.OriginalParts.Sum(p => p != null ? Mathf.Max(0f, p.hitPoints) : 0f) : Health(test.Target);
        }
        private static float Health(Unit unit)
        {
            if (unit == null) return 0f;
            Ship ship = unit as Ship;
            if (ship != null) return ship.parts.Where(p => p != null).Sum(p => p.hitPoints);
            if (unit is Missile missile) return Mathf.Max(0f, (float)NaturalWeapons.Get(missile, "hitpoints"));
            return unit.GetAllParts().Where(p => p != null && p.parentUnit == unit).Sum(p => p.hitPoints);
        }
        private static void Check(List<object> checks, string name, bool condition, ref bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            passed &= condition;
        }
        private static void Require(List<object> checks, string name, bool condition)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static bool IsFinite(Vector3 v) { return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z); }
        private static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        private static void Save(string path, Dictionary<string, object> report) { File.WriteAllText(path, Audit.Json(report)); }
    }
}
