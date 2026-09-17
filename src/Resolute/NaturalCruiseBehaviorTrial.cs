using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit copied-game diagnostic. Only fixture placement, known hostile
    // tracks, and target durability are controlled; all tested missile motion,
    // steering, motors, impact and damage use their production components.
    internal static class NaturalCruiseBehaviorTrial
    {
        private sealed class Shot
        {
            internal Missile Missile;
            internal Unit IntendedTarget;
            internal NaturalCruiseGuidance Guidance;
            internal NaturalCruiseTactics Tactics;
            internal OpticalSeekerCruiseMissile Optical;
            internal NaturalPikeCountermeasures PikeCountermeasures;
            internal Vector3 Initial, Last;
            internal float MinimumRange = float.MaxValue, MinimumClearance = float.MaxValue;
            internal float MinimumNeighborSeparation = float.MaxValue;
            internal float ThrottleMinimum = 1f, MaximumAltitude, NativeNeighborSeconds;
            internal float Progress, PeakSpeed, DetonationTime = -1f;
            internal float FirstLowCruiseTime = -1f, LowCruiseSeconds, MaximumDescentSpeed, LargestJink;
            internal float MaximumRouteAngle, MaximumMidcourseLaneDisplacement;
            internal float FirstDirectTerminalTime = -1f;
            internal int DirectTerminalChecks;
            internal int TerrainAvoidanceEvents;
            internal int NativeNeighbors, NativeGroupingUpdates, NativeControllerUpdates, CorridorChecks, Flares;
            internal int NativeFlareBursts, UniqueNativeFlareIds, LateralReversals, LastLateralSign;
            internal float CruiseThrust, TerminalThrust, CruisePeakSpeed, TerminalPeakSpeed;
            internal float MaximumPreterminalEcm, MaximumTerminalEcm, MaximumLateralSpeed, LastLateralReversal;
            internal bool NativeFlareParticles;
            internal bool IntendedTargetContact;
            internal string ContactedUnitKey, ContactEvidence;
            internal float ContactAge = -1f;
            internal Vector3 ContactGlobalPosition;
            internal object GuidanceReport;
            internal bool Finite = true, Detonated, HitArmor, HitTerrain, TargetRetained = true;
            internal readonly List<object> Samples = new List<object>();
            internal readonly List<object> NativeEvents = new List<object>();
            internal readonly List<object> NativeAeroSamples = new List<object>();
            internal float NextAeroSample, LastAeroTime = -1f;
            internal float LastOpticalTelemetryTime;
            internal bool OpticalTerminal, NativeOpticalConfigured;
            internal Vector3 LastAeroVelocity;
            internal object Result()
            {
                return new Dictionary<string, object> {
                    ["nativeDetonation"] = Detonated, ["nativeArmorImpact"] = HitArmor, ["nativeTerrainImpact"] = HitTerrain,
                    ["intendedTargetPhysicalContact"] = IntendedTargetContact, ["contactedUnitKey"] = ContactedUnitKey,
                    ["contactEvidence"] = ContactEvidence, ["contactAgeSeconds"] = ContactAge,
                    ["contactGlobalPosition"] = ContactAge >= 0f ? V(ContactGlobalPosition) : null,
                    ["detonationSeconds"] = DetonationTime, ["minimumTargetDistanceM"] = MinimumRange,
                    ["minimumClearanceM"] = MinimumClearance, ["maximumAltitudeM"] = MaximumAltitude,
                    ["maximumRouteProgressM"] = Progress, ["peakSpeedMps"] = PeakSpeed, ["finite"] = Finite,
                    ["targetRetainedThroughout"] = TargetRetained, ["maximumNativeNeighborsExcludingSelf"] = NativeNeighbors,
                    ["minimumMidcourseNeighborSeparationM"] = MinimumNeighborSeparation < float.MaxValue ? (object)MinimumNeighborSeparation : null,
                    ["nativeCruiseMethodUpdates"] = NativeControllerUpdates, ["nativeMethodUpdatesWithNeighbors"] = NativeGroupingUpdates,
                    ["nativeCruiseNeighborObservedSeconds"] = NativeNeighborSeconds,
                    ["nativeContactAndReceivedDamageEvents"] = NativeEvents,
                    ["nativeTerminalAerodynamicSamples"] = NativeAeroSamples,
                    ["minimumNativeThrottle"] = ThrottleMinimum,
                    ["nativeFlareCount"] = Flares, ["nativeFlareBursts"] = NativeFlareBursts,
                    ["uniqueNativeFlareIds"] = UniqueNativeFlareIds, ["nativeFlareParticlesSeen"] = NativeFlareParticles,
                    ["maximumPreterminalEcm"] = MaximumPreterminalEcm, ["maximumTerminalEcm"] = MaximumTerminalEcm,
                    ["cruiseThrustNewtons"] = CruiseThrust, ["terminalThrustNewtons"] = TerminalThrust,
                    ["cruisePeakSpeedMps"] = CruisePeakSpeed, ["terminalPeakSpeedMps"] = TerminalPeakSpeed,
                    ["maximumLateralSpeedMps"] = MaximumLateralSpeed, ["lateralReversals"] = LateralReversals,
                    ["maximumJinkM"] = LargestJink,
                    ["maximumTerrainRouteAngleDegrees"] = MaximumRouteAngle, ["terrainAvoidanceEvents"] = TerrainAvoidanceEvents,
                    ["maximumMidcourseDisplacementFromInitialLaneM"] = MaximumMidcourseLaneDisplacement,
                    ["guidance"] = GuidanceReport, ["samples"] = Samples
                };
            }
            internal void ReadTelemetry()
            {
                if (Guidance != null)
                {
                    FirstLowCruiseTime = Guidance.FirstLowCruiseTime; LowCruiseSeconds = Guidance.LowCruiseSeconds;
                    MaximumDescentSpeed = Guidance.MaximumDescentSpeed; CorridorChecks = Guidance.CorridorChecks;
                    MaximumRouteAngle = Guidance.MaximumRouteAngle; TerrainAvoidanceEvents = Guidance.TerrainAvoidanceEvents;
                    FirstDirectTerminalTime = Guidance.FirstDirectTerminalTime; DirectTerminalChecks = Guidance.DirectTerminalChecks;
                }
                if (Optical != null && Missile != null)
                {
                    float elapsed = Mathf.Max(0f, Missile.timeSinceSpawn - LastOpticalTelemetryTime);
                    LastOpticalTelemetryTime = Missile.timeSinceSpawn;
                    OpticalTerminal = (bool)NaturalWeapons.Get(Optical, "terminalMode");
                    MaximumDescentSpeed = Mathf.Max(MaximumDescentSpeed, -Missile.rb.velocity.y);
                    float cruiseAltitude = (float)NaturalWeapons.Get(Optical, "altitudeTarget");
                    if (!OpticalTerminal && Missile.timeSinceSpawn > 3f && Missile.radarAlt < Mathf.Max(100f, cruiseAltitude * 2f))
                    {
                        if (FirstLowCruiseTime < 0f) FirstLowCruiseTime = Missile.timeSinceSpawn;
                        LowCruiseSeconds += elapsed;
                    }
                    if (!OpticalTerminal && NativeNeighbors > 0) NativeNeighborSeconds += elapsed;
                    if (OpticalTerminal)
                    {
                        if (FirstDirectTerminalTime < 0f) FirstDirectTerminalTime = Missile.timeSinceSpawn;
                        DirectTerminalChecks++;
                    }
                }
                if (Tactics != null) { Flares = Tactics.FlareCount; LargestJink = Tactics.LargestJink; }
                if (PikeCountermeasures != null)
                {
                    Flares = PikeCountermeasures.FlaresEmitted;
                    if (NativeFlareBursts != PikeCountermeasures.BurstsEmitted)
                    {
                        NativeFlareBursts = PikeCountermeasures.BurstsEmitted;
                        UniqueNativeFlareIds = PikeCountermeasures.BurstFlareInstanceIds.SelectMany(ids => ids).Distinct().Count();
                    }
                    NativeFlareParticles |= PikeCountermeasures.NativeFlareParticlesSeen;
                    if (Missile != null && !Missile.disabled && Missile.timeSinceSpawn > 4f)
                    {
                        float thrust = (float)NaturalWeapons.Get(Missile, "engineCurrentThrust");
                        bool terminal = Missile.GetComponent<NaturalWeaponPhase>().TerminalBoostActive;
                        if (terminal)
                        {
                            TerminalThrust = Mathf.Max(TerminalThrust, thrust); TerminalPeakSpeed = Mathf.Max(TerminalPeakSpeed, Missile.speed);
                            MaximumTerminalEcm = Mathf.Max(MaximumTerminalEcm, Missile.GetECMIntensity());
                        }
                        else
                        {
                            CruiseThrust = Mathf.Max(CruiseThrust, thrust); CruisePeakSpeed = Mathf.Max(CruisePeakSpeed, Missile.speed);
                            MaximumPreterminalEcm = Mathf.Max(MaximumPreterminalEcm, Missile.GetECMIntensity());
                        }
                    }
                }
            }
        }
        private static readonly Dictionary<Missile, Shot> Shots = new Dictionary<Missile, Shot>();
        private static readonly List<GameObject> Spawned = new List<GameObject>();
        private static Ship pikeCounterfireOwner;
        private static int blockedPikeCounterfire;

        internal static IEnumerator Run(Ship subject, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cruise behavior diagnostics require the isolated test-game copy.");
            var results = new List<object>();
            var detail = new Dictionary<string, object> {
                ["scope"] = "Spear actual native VLS launch; separately seeded dispersed native Spear salvo; actual mapped terrain crossing to a native installation; 60 km dispersed native Pike flight regression. Hostile HQ tracks are refreshed as explicit diagnostic inputs. No missile position, velocity, force, seeker return or physics coefficient is overwritten after spawn. Group salvo initial positions are explicit fixture inputs, not claimed VLS ejections. Pike fixture target counterfire is explicitly blocked; this is not combat-survival evidence.",
                ["previousMeasuredVlsProfile"] = new Dictionary<string, object> {
                    ["report"] = "reports/redo_0.7.0/cruise_candidate_1827/mission_trial.json",
                    ["maximumAltitudeM"] = 1054.145f, ["firstLowCruiseTimeSeconds"] = 29.56639f },
                ["cases"] = results
            };
            report["cruiseBehavior"] = detail;
            if (Environment.GetCommandLineArgs().Contains("--resolute-moving-coast-only") &&
                Environment.GetCommandLineArgs().Contains("--resolute-long-pike-only"))
                throw new InvalidOperationException("Select either moving-coast-only or long-pike-only, not both.");
            NaturalCruiseNativeExperiment.Begin(detail);
            int firstCheck = checks.Count;
            var hooks = new Harmony(Plugin.Id + ".cruise-behavior-trial");
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(Detonating)));
            hooks.Patch(AccessTools.Method(typeof(Missile), "PenetrateObject"),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(Penetrating)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeDamage)),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(TraceIncomingDamage)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeShockwave)),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(TraceShockwave)));
            hooks.Patch(AccessTools.Method(typeof(Missile), "ApplyAero"),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(TraceNativeAerodynamics)));
            hooks.Patch(AccessTools.Method(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.TerrainWaypoint)),
                postfix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(ObserveNativeOpticalWaypoint)));
            detail["nativeReceivedDamageAndShockwaveTraceEnabled"] = true;
            detail["nativeTerminalAerodynamicTrace"] = "Pike within 12 km only, sampled before actual native ApplyAero and after native Steering. Body, wind, air-relative motion, exact stored SetAimpoint and steering inputs are observed. Lift/drag are reconstructed from the native method's unchanged current inputs; observed acceleration is the preceding actual physics step. No force or state is overwritten.";
            hooks.Patch(AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.Fire), new[] { typeof(Unit), typeof(Unit) }),
                prefix: new HarmonyMethod(typeof(NaturalCruiseBehaviorTrial), nameof(AllowPikeFixtureCounterfire)));
            try
            {
                FactionHQ hostile = FactionRegistry.HqFromName(subject.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
                MapSettings map = NetworkSceneSingleton<LevelInfo>.i.LoadedMapSettings;
                GlobalPosition start = new GlobalPosition(map.MapSize.x * .5f + 18000f, Datum.SeaLevel.y, -10000f);
                Vector3 heading = Vector3.forward;
                GlobalPosition targetPoint = start + heading * 17000f;
                bool oceanClear = true;
                for (int i = 0; i <= 34; i++)
                for (int side = -1; side <= 1; side++)
                    oceanClear &= Ground(start + heading * (i * 500f) + Vector3.right * side * 1100f) < .5f;
                Record(checks, "cruise-native-ocean-fixture-clear", oceanClear);
                if (!oceanClear) throw new InvalidOperationException("Cruise diagnostic ocean corridor is obstructed.");
                Ship owner = NetworkSceneSingleton<Spawner>.i.SpawnShip(subject.definition.unitPrefab,
                    start + Vector3.up * subject.definition.spawnOffset.y, Quaternion.identity, subject.NetworkHQ,
                    "rsl_cruise_fixture_launcher", 1f, true);
                Spawned.Add(owner.gameObject); MissionTrial.HoldControllers(owner);
                ShipDefinition targetDefinition = Plugin.FindDonor(Encyclopedia.i);
                Ship target = NetworkSceneSingleton<Spawner>.i.SpawnShip(targetDefinition.unitPrefab,
                    targetPoint + Vector3.up * targetDefinition.spawnOffset.y, Quaternion.Euler(0f, 90f, 0f), hostile,
                    "rsl_cruise_fixture_target", 1f, true);
                Spawned.Add(target.gameObject); MissionTrial.HoldControllers(target);
                target.SetHoldPosition(true); owner.SetHoldPosition(true);
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
                yield return null; yield return new WaitForFixedUpdate(); yield return null;
                foreach (UnitPart part in target.GetAllParts()) if (part != null) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);

                if (Environment.GetCommandLineArgs().Contains("--resolute-moving-coast-only"))
                {
                    detail["isolatedMovingCoastOnly"] = true;
                    IEnumerator moving = ObserveMovingCoastalShip(owner, targetDefinition, hostile, map, results, report, checks, output);
                    while (moving.MoveNext()) yield return moving.Current;
                    NaturalCruiseNativeExperiment.Validate(checks);
                    bool movingPassed = checks.Skip(firstCheck).Cast<Dictionary<string, object>>().All(c => (bool)c["passed"]);
                    detail["success"] = movingPassed; Save(output, report);
                    if (!movingPassed) throw new InvalidOperationException("Moving coastal cruise diagnostic failed; inspect actual target motion and native contact evidence.");
                    yield break;
                }
                if (Environment.GetCommandLineArgs().Contains("--resolute-long-pike-only"))
                {
                    detail["isolatedLongPikeOnly"] = true;
                    IEnumerator longPike = ObservePikeGroup(owner, targetDefinition, hostile, start, heading, results, report, checks, output);
                    while (longPike.MoveNext()) yield return longPike.Current;
                    NaturalCruiseNativeExperiment.Validate(checks);
                    bool pikePassed = checks.Skip(firstCheck).Cast<Dictionary<string, object>>().All(c => (bool)c["passed"]);
                    detail["success"] = pikePassed; Save(output, report);
                    if (!pikePassed) throw new InvalidOperationException("Long Pike comparison has failed checks; distinguish comparative slots from physical flight, weaving, countermeasures and native contacts.");
                    yield break;
                }

                report["phase"] = "cruise-native-vls-descent"; Save(output, report);
                ResoluteVlsLauncher[] pikeLaunchers = owner.GetComponentsInChildren<ResoluteVlsLauncher>().Where(l => l.missile.jsonKey == "rsl_ashm").ToArray();
                int pikeAmmoBefore = pikeLaunchers.Sum(l => l.ammo);
                // Exercise the production naval fallback condition. A loaded
                // Pike battery should prevent this Spear shot by preference;
                // its exhaustion is explicit fixture inventory, not a bypass
                // of assessment or the final launch eligibility guard.
                foreach (ResoluteVlsLauncher pike in pikeLaunchers) pike.ammo = 0;
                foreach (WeaponStation pikeStation in owner.weaponStations.Where(s => s.Weapons.Any(w => w is ResoluteVlsLauncher pike && pike.missile.jsonKey == "rsl_ashm")))
                { pikeStation.AccountAmmo(); pikeStation.Updated(); }
                int pikeAmmoAfter = pikeLaunchers.Sum(l => l.ammo);
                detail["navalFallbackFixture"] = new Dictionary<string, object> { ["pikeAmmoBefore"] = pikeAmmoBefore,
                    ["pikeAmmoAfter"] = pikeAmmoAfter, ["scope"] = "Only this fresh fixture ship's Pike inventory is exhausted and native station totals refreshed. Production Spear preference and launch guards remain active." };
                Record(checks, "spear-naval-fallback-pike-inventory-exhausted", pikeLaunchers.Length > 0 && pikeAmmoBefore > 0 && pikeAmmoAfter == 0);
                ResoluteVlsLauncher launcher = owner.GetComponentsInChildren<ResoluteVlsLauncher>().First(l => l.missile.jsonKey == "rsl_cruise");
                WeaponStation station = owner.weaponStations.Single(s => s.Weapons.Contains(launcher));
                float interval = NaturalArmament.Get<float>(launcher, "fireInterval");
                float ready = Time.time + Mathf.Max(0f, interval - (Time.timeSinceLevelLoad - NaturalArmament.Get<float>(launcher, "lastFired"))) + .1f;
                while (Time.time < ready) { NaturalWeaponsTrial.Know(owner.NetworkHQ, target); yield return null; }
                int ammoBefore = launcher.ammo;
                var before = new HashSet<Missile>(UnitRegistry.allUnits.OfType<Missile>());
                launcher.Fire(owner, target, owner.rb.velocity, station, target.GlobalPosition());
                yield return null; yield return new WaitForFixedUpdate(); yield return null;
                Missile launched = UnitRegistry.allUnits.OfType<Missile>().FirstOrDefault(m => !before.Contains(m) &&
                    m.ownerID == owner.persistentID && m.definition.jsonKey == "rsl_cruise");
                bool launchedNative = launched != null && launcher.ammo == ammoBefore - 1;
                Record(checks, "spear-native-vls-ammo-and-owned-launch", launchedNative);
                if (launched != null)
                {
                    Shot shot = Add(launched);
                    var caseResult = new Dictionary<string, object> { ["scenario"] = "actual-vls-clearance-descent-sea-attack",
                        ["ammoBefore"] = ammoBefore, ["ammoAfter"] = launcher.ammo, ["launchDirection"] = V(launched.transform.forward),
                        ["samples"] = shot.Samples };
                    results.Add(caseResult);
                    IEnumerator observe = Observe(new[] { shot }, owner, target, heading, 115f, report, output);
                    while (observe.MoveNext()) yield return observe.Current;
                    caseResult["shot"] = shot.Result();
                    Record(checks, "spear-vls-descends-and-maintains-low-cruise", shot.FirstLowCruiseTime > 0f &&
                        shot.FirstLowCruiseTime < 20f && shot.MaximumAltitude < 500f && shot.LowCruiseSeconds > 8f && shot.MaximumDescentSpeed > 12f);
                    Record(checks, "spear-complete-native-optical-cruise-and-terminal-path",
                        shot.NativeOpticalConfigured &&
                        shot.NativeControllerUpdates > 0 && shot.FirstDirectTerminalTime > 0f);
                    Record(checks, "spear-vls-finite-and-clear-until-terminal", shot.Finite && shot.MinimumClearance > 1f);
                    Record(checks, "spear-vls-native-target-impact", shot.Detonated && shot.HitArmor && shot.IntendedTargetContact && shot.MinimumRange < 180f);
                    Save(output, report); ClearShots(); yield return null;
                }

                report["phase"] = "cruise-dispersed-cohort"; Save(output, report);
                var salvo = new List<Shot>();
                for (int i = 0; i < 4; i++)
                {
                    Vector3 initial = (start + Vector3.right * ((i - 1.5f) * 500f) + heading * (400f + i * 220f)).ToLocalPosition();
                    initial.y = Datum.LocalSeaY + 150f;
                    salvo.Add(Add(NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions["rsl_cruise"], initial,
                        Quaternion.LookRotation(heading), heading * 220f, target, owner)));
                }
                var salvoResult = new Dictionary<string, object> { ["scenario"] = "four-dispersed-spear-native-local-grouping",
                    ["initialCrossTrackSpacingM"] = 500f, ["initialAlongTrackSpacingM"] = 220f, ["samples"] = salvo.Select(s => s.Samples).ToArray() };
                results.Add(salvoResult);
                IEnumerator grouped = Observe(salvo.ToArray(), owner, target, heading, 85f, report, output);
                while (grouped.MoveNext()) yield return grouped.Current;
                salvoResult["shots"] = salvo.Select(s => s.Result()).ToArray();
                Record(checks, "spear-native-grouping-sees-three-registered-neighbors", salvo.All(s => s.NativeNeighbors == 3));
                Record(checks, "spear-native-local-grouping-runs-during-progress", salvo.All(s => s.NativeGroupingUpdates >= 4 &&
                    s.NativeNeighborSeconds > 5f && s.Progress > 10000f));
                Record(checks, "spear-cohort-uses-bounded-native-throttle", salvo.Any(s => s.ThrottleMinimum < .99f) &&
                    salvo.All(s => s.ThrottleMinimum >= .8f && s.Finite && s.MinimumClearance > 1f));
                Record(checks, "spear-cohort-retains-safe-physical-separation", salvo.All(s => s.MinimumNeighborSeparation > 8f && s.MinimumNeighborSeparation < float.MaxValue));
                Record(checks, "spear-cohort-no-custom-pike-tactics-or-countermeasures",
                    salvo.All(s => s.NativeOpticalConfigured));
                Record(checks, "spear-cohort-multiple-native-target-impacts", salvo.Count(s => s.HitArmor && s.IntendedTargetContact && s.MinimumRange < 180f) >= 2);
                Save(output, report); ClearShots(); yield return null;

                report["phase"] = "cruise-find-native-terrain-corridor"; Save(output, report);
                GlobalPosition terrainStart, terrainTarget; Vector3 terrainHeading; float ridgeDistance;
                bool terrainFound = FindTerrainLeg(map, out terrainStart, out terrainTarget, out terrainHeading, out ridgeDistance);
                detail["nativeTerrainFixtureFound"] = terrainFound;
                Record(checks, "spear-real-map-terrain-fixture-found", terrainFound);
                if (terrainFound)
                {
                    BuildingDefinition building = Encyclopedia.i.buildings.Where(d => d != null && d.unitPrefab != null &&
                        d.unitPrefab.GetComponent<Building>() != null && d.width > 15f && d.width < 160f)
                        .OrderByDescending(d => d.jsonKey.IndexOf("warehouse", StringComparison.OrdinalIgnoreCase) >= 0).First();
                    Building installation = NetworkSceneSingleton<Spawner>.i.SpawnBuilding(building.unitPrefab, terrainTarget,
                        Quaternion.identity, hostile, null, "rsl_spear_terrain_installation", false, null);
                    Spawned.Add(installation.gameObject);
                    NaturalWeaponsTrial.Know(owner.NetworkHQ, installation);
                    yield return null; yield return new WaitForFixedUpdate();
                    Vector3 initial = terrainStart.ToLocalPosition(); initial.y = Datum.LocalSeaY + 100f;
                    Shot terrainShot = Add(NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions["rsl_cruise"], initial,
                        Quaternion.LookRotation(terrainHeading), terrainHeading * 220f, installation, owner));
                    var terrainResult = new Dictionary<string, object> { ["scenario"] = "native-map-ridge-to-installation",
                        ["start"] = V(terrainStart.AsVector3()), ["target"] = V(terrainTarget.AsVector3()),
                        ["ridgeDistanceM"] = ridgeDistance, ["targetDefinition"] = building.jsonKey, ["samples"] = terrainShot.Samples };
                    terrainResult["physicalTargetColliders"] = installation.GetComponentsInChildren<Collider>()
                        .Where(c => c.enabled && !c.isTrigger && c.gameObject.layer != PhysicsLayers.ExclusionZones)
                        .Select(c => new Dictionary<string, object> { ["name"] = c.name,
                            ["center"] = V((installation.GlobalPosition() + c.bounds.center - installation.transform.position).AsVector3()),
                            ["size"] = V(c.bounds.size) }).ToArray();
                    terrainResult["nativeSurfaceRadarVisibilityProbe"] = ProbeSurfaceRadarVisibility(installation,
                        terrainStart, terrainHeading, ridgeDistance, checks);
                    results.Add(terrainResult); report["phase"] = "cruise-native-terrain-flight";
                    IEnumerator terrainFlight = Observe(new[] { terrainShot }, owner, installation, terrainHeading, 110f, report, output);
                    while (terrainFlight.MoveNext()) yield return terrainFlight.Current;
                    terrainResult["shot"] = terrainShot.Result();
                    Record(checks, "spear-native-ridge-cleared-by-physical-flight", terrainShot.Progress > ridgeDistance + 500f &&
                        terrainShot.MinimumClearance > 1f && terrainShot.Finite && terrainShot.NativeControllerUpdates > 4);
                    Record(checks, "spear-native-installation-impact-after-terrain", terrainShot.Detonated && terrainShot.HitArmor &&
                        terrainShot.IntendedTargetContact && terrainShot.MinimumRange < 180f);
                    Record(checks, "spear-installation-native-optical-terminal-part-and-los-observed",
                        terrainShot.Samples.Cast<Dictionary<string, object>>().Any(s =>
                            (bool)s["directTerminalClear"] && (bool)s["nativeOpticalTerminal"] &&
                            s["nativeOpticalTargetPart"] != null));
                    Save(output, report); ClearShots(); yield return null;
                }
                IEnumerator pikeFlight = ObservePikeGroup(owner, targetDefinition, hostile, start, heading, results, report, checks, output);
                while (pikeFlight.MoveNext()) yield return pikeFlight.Current;
                IEnumerator steepFlight = ObserveSteepTerrainGroup(owner, targetDefinition, hostile, map, results, report, checks, output);
                while (steepFlight.MoveNext()) yield return steepFlight.Current;
                NaturalCruiseNativeExperiment.Validate(checks);
                bool passed = checks.Skip(firstCheck).Cast<Dictionary<string, object>>().All(c => (bool)c["passed"]);
                detail["success"] = passed; Save(output, report);
                if (!passed) throw new InvalidOperationException("Cruise behavior checks failed; inspect cruiseBehavior cases and samples.");
            }
            finally
            {
                ClearShots();
                NaturalCruiseNativeExperiment.End();
                pikeCounterfireOwner = null;
                foreach (GameObject go in Spawned) if (go != null) Object.Destroy(go);
                Spawned.Clear(); hooks.UnpatchSelf();
            }
        }

        private static bool AllowPikeFixtureCounterfire(Unit owner)
        {
            if (pikeCounterfireOwner == null || owner == null || owner.NetworkHQ != pikeCounterfireOwner.NetworkHQ ||
                !Spawned.Contains(owner.gameObject)) return true;
            blockedPikeCounterfire++; return false;
        }

        private static IEnumerator ObservePikeGroup(Ship owner, ShipDefinition targetDefinition, FactionHQ hostile,
            GlobalPosition start, Vector3 heading, List<object> results, Dictionary<string, object> report, List<object> checks, string output)
        {
            report["phase"] = "cruise-pike-long-dispersed-flight";
            // Keep this flight corridor clear of the earlier Spear target's
            // surviving physical hull at 17 km, not merely clear of terrain.
            start += Vector3.right * 5000f;
            bool oceanClear = true;
            for (int along = 0; along <= 120; along++)
            for (int side = -1; side <= 1; side++)
                oceanClear &= Ground(start + heading * (along * 500f) + Vector3.right * side * 1500f) < .5f;
            Record(checks, "pike-60km-physical-flight-corridor-clear", oceanClear);
            if (!oceanClear) throw new InvalidOperationException("Pike's 60 km diagnostic corridor is obstructed.");
            Ship target = NetworkSceneSingleton<Spawner>.i.SpawnShip(targetDefinition.unitPrefab,
                start + heading * 60000f + Vector3.up * targetDefinition.spawnOffset.y, Quaternion.Euler(0f, 90f, 0f), hostile,
                "rsl_pike_long_group_target", 1f, true);
            Spawned.Add(target.gameObject); MissionTrial.HoldControllers(target); target.SetHoldPosition(true);
            pikeCounterfireOwner = target; blockedPikeCounterfire = 0;
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            yield return null; yield return new WaitForFixedUpdate(); yield return null;
            foreach (UnitPart part in target.GetAllParts()) if (part != null) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);
            var shots = new List<Shot>();
            float sourceTurnRate = (float)NaturalWeapons.Get(NaturalWeapons.Definitions["rsl_ashm"].unitPrefab.GetComponent<Missile>(), "maxTurnRate");
            for (int i = 0; i < 4; i++)
            {
                Vector3 initial = (start + Vector3.right * ((i - 1.5f) * 500f) + heading * (400f + i * 1000f)).ToLocalPosition();
                initial.y = Datum.LocalSeaY + 150f;
                shots.Add(Add(NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions["rsl_ashm"], initial,
                    Quaternion.LookRotation(heading), heading * 60f, target, owner)));
            }
            var result = new Dictionary<string, object> { ["scenario"] = "four-dispersed-pike-60km-cruise-terminal-regression",
                ["scope"] = "Native seeded flight fixtures at 150 m / 60 m/s with 500 m cross-track and 1000 m along-track spacing. This corridor lies 5 km east of the preceding Spear corridor to avoid its surviving target hull. Target health is raised to preserve its collider for repeated native impacts. Counterfire from all hostile ships created by this diagnostic is blocked and HQ tracks are refreshed; their radar and physical hulls remain active. Native source motors, guidance, jink, flares, ECM and physics remain active. ECM state is not claimed as organic radar suppression; the separate live Pike observation supplies that evidence.",
                ["start"] = V(start.AsVector3()), ["counterfireExcludedFixtureShips"] = Spawned.Where(go => go != null)
                    .Select(go => go.GetComponent<Ship>()).Where(ship => ship != null && ship.NetworkHQ == hostile)
                    .Select(ship => new Dictionary<string, object> { ["id"] = ship.persistentID.ToString(),
                        ["definition"] = ship.definition.jsonKey, ["position"] = V(ship.GlobalPosition().AsVector3()) }).ToArray(),
                ["initialCrossTrackSpacingM"] = 500f, ["initialAlongTrackSpacingM"] = 1000f, ["sourceMaximumTurnRate"] = sourceTurnRate,
                ["samples"] = shots.Select(s => s.Samples).ToArray() };
            results.Add(result); Save(output, report);
            IEnumerator observe = Observe(shots.ToArray(), owner, target, heading, 110f, report, output);
            while (observe.MoveNext()) yield return observe.Current;
            result["shots"] = shots.Select(s => s.Result()).ToArray();
            result["blockedFixtureCounterfire"] = blockedPikeCounterfire;
            Record(checks, "pike-long-flight-native-neighbors-observed", shots.All(s => s.NativeNeighbors == 3 && s.NativeNeighborSeconds > 10f));
            Record(checks, "pike-long-flight-native-grouping-runs-during-progress", shots.All(s => s.NativeGroupingUpdates >= 20 && s.Progress > 50000f));
            Record(checks, "pike-long-flight-finite-and-safe", shots.All(s => s.Finite && s.MinimumClearance > 1f &&
                s.MinimumNeighborSeparation > 8f && s.ThrottleMinimum >= .8f));
            Record(checks, "pike-long-flight-uses-physical-throttle", shots.Any(s => s.ThrottleMinimum < .99f));
            Record(checks, "pike-source-turn-rate-unchanged", shots.All(s => s.Samples.Cast<Dictionary<string, object>>()
                .All(sample => Mathf.Abs((float)sample["nativeMaximumTurnRateDegreesPerSecond"] - sourceTurnRate) < .01f)));
            Record(checks, "pike-cruise-to-terminal-physical-boost", shots.All(s => s.CruiseThrust > 0f &&
                s.TerminalThrust > s.CruiseThrust * 2f && s.TerminalPeakSpeed > s.CruisePeakSpeed * 1.15f));
            Record(checks, "pike-terminal-physical-weave", shots.All(s => s.LargestJink > .1f && s.MaximumLateralSpeed > 5f && s.LateralReversals >= 2));
            Record(checks, "pike-six-by-eight-native-flares-preserved", shots.All(s => s.Flares == 48 && s.NativeFlareBursts == 6 &&
                s.UniqueNativeFlareIds == 48 && s.NativeFlareParticles));
            Record(checks, "pike-ecm-gated-to-terminal-flight", shots.All(s => s.CruiseThrust > 0f &&
                s.MaximumPreterminalEcm == 0f && s.MaximumTerminalEcm > 0f));
            Record(checks, "pike-long-group-multiple-native-hull-impacts", shots.Count(s => s.HitArmor && s.IntendedTargetContact && s.MinimumRange < 180f) >= 2);
            Record(checks, "pike-long-group-all-four-native-hull-impacts", shots.All(s => s.HitArmor && s.IntendedTargetContact && s.MinimumRange < 180f));
            Record(checks, "pike-locked-hull-pursuit-preserves-live-native-jink", shots.All(PikeDirectJinkPreserved));
            Save(output, report); ClearShots(); pikeCounterfireOwner = null;
            yield return null;
        }

        private static object ProbeSurfaceRadarVisibility(Building target, GlobalPosition terrainStart,
            Vector3 terrainHeading, float ridgeDistance, List<object> checks)
        {
            var probe = new GameObject("rsl_readonly_surface_visibility_probe");
            object visible = null, occluded = null;
            int visibleAttempts = 0, occludedAttempts = 0, oldVisibleAttempts = 0, newVisibleAttempts = 0;
            try
            {
                Collider[] colliders = target.GetComponentsInChildren<Collider>().Where(c => c != null && c.enabled &&
                    !c.isTrigger && (PhysicsLayers.StaticsMask.value & (1 << c.gameObject.layer)) != 0).ToArray();
                foreach (Collider collider in colliders)
                {
                    if (visible != null) break;
                    Bounds bounds = collider.bounds;
                    // The short side ring need not expose the foundation-ray
                    // difference. Probe above a real roof intersection as well:
                    // its surface can be visible while the origin is below it.
                    for (int footprint = 0; footprint < 5 && visible == null; footprint++)
                    {
                        Vector3 above = bounds.center; above.y = bounds.max.y + 20f;
                        if (footprint > 0)
                        {
                            above.x += bounds.extents.x * (footprint % 2 == 0 ? .5f : -.5f);
                            above.z += bounds.extents.z * (footprint < 3 ? -.5f : .5f);
                        }
                        if (!collider.Raycast(new Ray(above, Vector3.down), out RaycastHit roof, bounds.size.y + 40f)) continue;
                        // PathfindingAgent.RaycastTerrain uses StaticsMask, so
                        // Ground(roof) returns this building's roof. Establish
                        // actual terrain independently for this read-only probe.
                        RaycastHit terrain = Physics.RaycastAll(above, Vector3.down, 20000f,
                            PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
                            .Where(hit => hit.collider != null && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial)
                            .OrderBy(hit => hit.distance).FirstOrDefault();
                        if (terrain.collider == null || roof.point.y < terrain.point.y + 3f) continue;
                        foreach (float clearance in new[] { 20f, 50f })
                        {
                            if (visible != null) break;
                            Vector3 source = roof.point + Vector3.up * clearance;
                            probe.transform.position = source; visibleAttempts++;
                            bool oldVisible = TargetCalc.LineOfSight(probe.transform, target.transform, 10f);
                            bool newVisible = NaturalSurfaceRadarVisibility.CanSee(probe.transform, target);
                            if (oldVisible) oldVisibleAttempts++;
                            if (newVisible) newVisibleAttempts++;
                            if (oldVisible || !newVisible || !Physics.Raycast(source, Vector3.down, out RaycastHit first,
                                clearance + .05f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) ||
                                !first.collider.transform.IsChildOf(target.transform)) continue;
                            visible = new Dictionary<string, object> { ["source"] = V(source.ToGlobalPosition().AsVector3()),
                                ["sourceKind"] = "above-actual-native-roof-intersection", ["roofClearanceM"] = clearance,
                                ["nativeTerrainPoint"] = V(terrain.point.ToGlobalPosition().AsVector3()),
                                ["roofHeightAboveNativeTerrainM"] = roof.point.y - terrain.point.y,
                                ["oldOriginLineOfSight"] = oldVisible, ["targetAwareLineOfSight"] = newVisible,
                                ["firstPhysicalSurface"] = first.collider.name, ["firstPhysicalPoint"] = V(first.point.ToGlobalPosition().AsVector3()),
                                ["testedTargetCollider"] = collider.name,
                                ["targetColliderCenter"] = V(bounds.center.ToGlobalPosition().AsVector3()), ["targetColliderSize"] = V(bounds.size) };
                        }
                    }
                    for (int angle = 0; angle < 16 && visible == null; angle++)
                    for (int height = 0; height < 3 && visible == null; height++)
                    {
                        Vector3 side = Quaternion.AngleAxis(angle * 22.5f, Vector3.up) * Vector3.forward;
                        Vector3 source = bounds.center + side * (Mathf.Max(bounds.extents.x, bounds.extents.z) + 40f);
                        source.y = Mathf.Lerp(target.transform.position.y + 5f, bounds.max.y + 5f, height * .5f);
                        if (source.y - Datum.LocalSeaY < Ground(source.ToGlobalPosition()) + 3f) continue;
                        probe.transform.position = source; visibleAttempts++;
                        bool oldVisible = TargetCalc.LineOfSight(probe.transform, target.transform, 10f);
                        bool newVisible = NaturalSurfaceRadarVisibility.CanSee(probe.transform, target);
                        if (oldVisible) oldVisibleAttempts++;
                        if (newVisible) newVisibleAttempts++;
                        if (oldVisible || !newVisible) continue;
                        // Demonstrate an actual visible target-collider surface,
                        // independently of either visibility helper's verdict.
                        Vector3 center = bounds.center; center.y = bounds.max.y - bounds.size.y * .1f;
                        Vector3 direction = (center - source).normalized;
                        if (!collider.Raycast(new Ray(source, direction), out RaycastHit ownHit, bounds.size.magnitude + 100f)) continue;
                        if (!Physics.Raycast(source, direction, out RaycastHit first, ownHit.distance + .05f,
                            PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) ||
                            !first.collider.transform.IsChildOf(target.transform)) continue;
                        visible = new Dictionary<string, object> { ["source"] = V(source.ToGlobalPosition().AsVector3()),
                            ["oldOriginLineOfSight"] = oldVisible, ["targetAwareLineOfSight"] = newVisible,
                            ["firstPhysicalSurface"] = first.collider.name, ["firstPhysicalPoint"] = V(first.point.ToGlobalPosition().AsVector3()),
                            ["testedTargetCollider"] = collider.name,
                            ["targetColliderCenter"] = V(bounds.center.ToGlobalPosition().AsVector3()), ["targetColliderSize"] = V(bounds.size) };
                    }
                }
                for (int along = 0; along < 4 && occluded == null; along++)
                for (int height = 0; height < 4 && occluded == null; height++)
                {
                    GlobalPosition source = terrainStart + terrainHeading * (ridgeDistance * along * .2f);
                    source.y = Ground(source) + 5f + height * 20f;
                    probe.transform.position = source.ToLocalPosition(); occludedAttempts++;
                    bool oldVisible = TargetCalc.LineOfSight(probe.transform, target.transform, 10f);
                    bool newVisible = NaturalSurfaceRadarVisibility.CanSee(probe.transform, target);
                    if (newVisible || oldVisible || !Physics.Linecast(probe.transform.position, target.transform.position,
                        out RaycastHit first, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) ||
                        first.collider.sharedMaterial != GameAssets.i.terrainMaterial) continue;
                    occluded = new Dictionary<string, object> { ["source"] = V(source.AsVector3()),
                        ["oldOriginLineOfSight"] = oldVisible, ["targetAwareLineOfSight"] = newVisible,
                        ["firstPhysicalSurface"] = first.collider.name, ["nativeTerrainMaterial"] = true,
                        ["firstPhysicalPoint"] = V(first.point.ToGlobalPosition().AsVector3()) };
                }
            }
            finally { Object.Destroy(probe); }
            Record(checks, "spear-building-visible-collider-not-rejected-by-origin-los", visible != null);
            Record(checks, "spear-building-real-intervening-ridge-still-blocks-radar-los", occluded != null);
            return new Dictionary<string, object> { ["scope"] = "Read-only native ray probes using a collider-free source transform around the actual spawned building and its mapped ridge. No radar return, target geometry or terrain is overridden.",
                ["targetOrigin"] = V(target.GlobalPosition().AsVector3()), ["visibleCase"] = visible,
                ["ridgeOccludedCase"] = occluded, ["visibleAttempts"] = visibleAttempts, ["occludedAttempts"] = occludedAttempts,
                ["oldVisibleAttempts"] = oldVisibleAttempts, ["targetAwareVisibleAttempts"] = newVisibleAttempts };
        }

        private static IEnumerator ObserveSteepTerrainGroup(Ship owner, ShipDefinition targetDefinition, FactionHQ hostile,
            MapSettings map, List<object> results, Dictionary<string, object> report, List<object> checks, string output)
        {
            report["phase"] = "cruise-find-steep-native-terrain-cohort"; Save(output, report);
            GlobalPosition start, end, ridge; Vector3 heading; float peak;
            bool found = FindSteepTerrainLeg(map, out start, out end, out ridge, out heading, out peak);
            Record(checks, "spear-steep-native-terrain-cohort-fixture-found", found);
            if (!found) { Save(output, report); yield break; }
            Ship target = NetworkSceneSingleton<Spawner>.i.SpawnShip(targetDefinition.unitPrefab,
                end + Vector3.up * targetDefinition.spawnOffset.y, Quaternion.LookRotation(Vector3.Cross(Vector3.up, heading)), hostile,
                "rsl_spear_steep_terrain_target", 1f, true);
            Spawned.Add(target.gameObject); MissionTrial.HoldControllers(target); target.SetHoldPosition(true);
            pikeCounterfireOwner = target; blockedPikeCounterfire = 0;
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            yield return null; yield return new WaitForFixedUpdate(); yield return null;
            foreach (UnitPart part in target.GetAllParts()) if (part != null) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);
            Vector3 side = Vector3.Cross(Vector3.up, heading);
            var shots = new List<Shot>();
            for (int i = 0; i < 4; i++)
            {
                Vector3 initial = (start + side * ((i - 1.5f) * 185.2f) + heading * (i * 80f)).ToLocalPosition();
                initial.y = Datum.LocalSeaY + 100f;
                shots.Add(Add(NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions["rsl_cruise"], initial,
                    Quaternion.LookRotation(heading), heading * 220f, target, owner)));
            }
            var result = new Dictionary<string, object> { ["scenario"] = "four-spear-native-steep-island-routing",
                ["scope"] = "Four seeded native Spear flights share an actual ship target across a native mapped island ridge. Only starting poses, HQ tracks, target durability and the diagnostic ships' counterfire exclusion are fixture inputs. No synthetic obstacle, waypoint, terrain, missile position/velocity or physics coefficient is created or overwritten. The route search requires real sea at both ends, a 250–900 m mapped crest, and a lower natural shoulder 1.5 km to at least one side.",
                ["start"] = V(start.AsVector3()), ["target"] = V(end.AsVector3()), ["ridge"] = V(ridge.AsVector3()),
                ["nativeRidgeHeightM"] = peak, ["ridgeDistanceM"] = 6500f, ["initialCrossTrackSpacingM"] = 185.2f,
                ["initialAlongTrackSpacingM"] = 80f, ["samples"] = shots.Select(s => s.Samples).ToArray(),
                ["counterfireExcludedFixtureShips"] = Spawned.Where(go => go != null).Select(go => go.GetComponent<Ship>())
                    .Where(ship => ship != null && ship.NetworkHQ == hostile).Select(ship => ship.persistentID.ToString()).ToArray() };
            results.Add(result); report["phase"] = "cruise-native-steep-terrain-cohort"; Save(output, report);
            IEnumerator observe = Observe(shots.ToArray(), owner, target, heading, 100f, report, output);
            while (observe.MoveNext()) yield return observe.Current;
            result["shots"] = shots.Select(s => s.Result()).ToArray(); result["blockedFixtureCounterfire"] = blockedPikeCounterfire;
            Record(checks, "spear-steep-terrain-native-local-grouping", shots.All(s => s.NativeNeighbors == 3 && s.NativeGroupingUpdates >= 20 && s.NativeNeighborSeconds > 10f));
            Record(checks, "spear-steep-terrain-physical-climb-or-flank", shots.All(s =>
                (s.MaximumAltitude > peak + 15f || s.MaximumMidcourseLaneDisplacement > 250f) && s.TerrainAvoidanceEvents > 0));
            Record(checks, "spear-steep-terrain-all-clear-by-native-flight", shots.All(s => s.Finite && s.MinimumClearance > 1f &&
                s.Progress > 7000f && s.MinimumNeighborSeparation > 8f));
            Record(checks, "spear-steep-terrain-locked-clear-coastal-terminal-handoff", shots.Count(s =>
                s.FirstDirectTerminalTime >= 0f && s.DirectTerminalChecks > 0) >= 2);
            Record(checks, "spear-steep-terrain-cohort-native-target-hits", shots.Count(s => s.HitArmor && s.IntendedTargetContact && s.MinimumRange < 180f) >= 2);
            Save(output, report); ClearShots(); pikeCounterfireOwner = null;
            yield return null;
        }

        private static IEnumerator ObserveMovingCoastalShip(Ship owner, ShipDefinition targetDefinition, FactionHQ hostile,
            MapSettings map, List<object> results, Dictionary<string, object> report, List<object> checks, string output)
        {
            report["phase"] = "cruise-find-moving-coastal-native-route"; Save(output, report);
            GlobalPosition start, end, ridge; Vector3 incoming; float crest;
            bool found = FindSteepTerrainLeg(map, out start, out end, out ridge, out incoming, out crest);
            Record(checks, "spear-moving-coast-actual-ridge-found", found);
            if (!found) yield break;
            GlobalPosition destination; Vector3 course; float envelopeClearance;
            bool waterRoute = FindMovingShipWaterRoute(end, incoming, out destination, out course, out envelopeClearance);
            Record(checks, "spear-moving-coast-native-water-route-clear-of-existing-hulls", waterRoute);
            if (!waterRoute) { Save(output, report); yield break; }

            Ship target = NetworkSceneSingleton<Spawner>.i.SpawnShip(targetDefinition.unitPrefab,
                end + Vector3.up * targetDefinition.spawnOffset.y, Quaternion.LookRotation(course), hostile,
                "rsl_spear_native_moving_coastal_target", 1f, true);
            Spawned.Add(target.gameObject); MissionTrial.HoldControllers(target);
            pikeCounterfireOwner = target; blockedPikeCounterfire = 0;
            yield return null; yield return new WaitForFixedUpdate(); yield return null;
            foreach (UnitPart part in target.GetAllParts()) if (part != null) part.hitPoints = Mathf.Max(part.hitPoints, 30000f);
            ShipAI ai = target.GetComponent<ShipAI>();
            var targetSamples = new List<object>();
            var result = new Dictionary<string, object> {
                ["scenario"] = "two-spear-real-shipai-moving-coastal-target",
                ["scope"] = "Two seeded Spear missiles cross the actual mapped ridge against a ship moving solely through native ShipAI, UnitCommand.SetDestination and its propulsion. No target or missile position, velocity, force or steering is overwritten after spawn. Initial poses, one native navigation command, increased fixture durability, refreshed friendly HQ tracks and excluded fixture counterfire are disclosed inputs.",
                ["start"] = V(start.AsVector3()), ["targetSpawn"] = V(end.AsVector3()), ["ridge"] = V(ridge.AsVector3()),
                ["nativeRidgeHeightM"] = crest, ["nativeShipDestination"] = V(destination.AsVector3()),
                ["minimumExistingPhysicalHullEnvelopeClearanceM"] = envelopeClearance,
                ["nativeShipAiType"] = ai != null ? ai.GetType().Name : null,
                ["nativeTargetMotionSamples"] = targetSamples
            };
            results.Add(result);
            Record(checks, "spear-moving-coast-native-shipai-enabled", ai != null && ai.enabled);
            if (ai == null || !ai.enabled) { Save(output, report); yield break; }
            GlobalPosition beforeMotion = target.GlobalPosition();
            target.SetHoldPosition(false);
            target.UnitCommand.SetDestination(destination, false);
            float motionBegan = Time.time, nextMotionSample = 0f;
            report["phase"] = "cruise-moving-coastal-native-ship-accelerating"; Save(output, report);
            while (Time.time - motionBegan < 45f)
            {
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
                if (Time.time >= nextMotionSample)
                {
                    nextMotionSample = Time.time + .5f;
                    targetSamples.Add(MovingShipSample(target, ai, Time.time - motionBegan, "native-acceleration"));
                }
                Vector3 v = target.rb.velocity; v.y = 0f;
                if (v.magnitude > 2f && (target.GlobalPosition() - beforeMotion).magnitude > 15f) break;
                yield return new WaitForFixedUpdate();
            }
            Vector3 launchVelocity = target.rb.velocity; launchVelocity.y = 0f;
            bool movingNatively = launchVelocity.magnitude > 2f && (target.GlobalPosition() - beforeMotion).magnitude > 15f;
            Record(checks, "spear-moving-coast-target-accelerated-under-native-command", movingNatively);
            result["nativeMotionWarmupSeconds"] = Time.time - motionBegan;
            result["targetVelocityAtMissileSpawn"] = V(target.rb.velocity);
            result["nativeCommandPosition"] = V(target.UnitCommand.GetCommandCached().position.AsVector3());
            if (!movingNatively) { Save(output, report); yield break; }

            GlobalPosition targetAtLaunch = target.GlobalPosition();
            Vector3 side = Vector3.Cross(Vector3.up, incoming);
            var shots = new List<Shot>();
            for (int i = 0; i < 2; i++)
            {
                Vector3 initial = (start + side * ((i - .5f) * 220f) + incoming * (i * 100f)).ToLocalPosition();
                initial.y = Datum.LocalSeaY + 100f;
                shots.Add(Add(NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions["rsl_cruise"], initial,
                    Quaternion.LookRotation(incoming), incoming * 220f, target, owner)));
            }
            result["samples"] = shots.Select(s => s.Samples).ToArray();
            report["phase"] = "cruise-native-moving-coastal-interception"; Save(output, report);
            IEnumerator observe = Observe(shots.ToArray(), owner, target, incoming, 105f, report, output);
            while (observe.MoveNext())
            {
                if (Time.time >= nextMotionSample)
                {
                    nextMotionSample = Time.time + .5f;
                    targetSamples.Add(MovingShipSample(target, ai, Time.time - motionBegan, "missile-flight"));
                }
                yield return observe.Current;
            }
            float targetTravel = (target.GlobalPosition() - targetAtLaunch).magnitude;
            result["shots"] = shots.Select(s => s.Result()).ToArray();
            result["targetTravelDuringMissileFlightM"] = targetTravel;
            result["blockedFixtureCounterfire"] = blockedPikeCounterfire;
            Record(checks, "spear-moving-coast-target-travels-during-interception", targetTravel > 80f);
            Record(checks, "spear-moving-coast-both-cross-ridge-with-clearance", shots.All(s => s.Finite && s.Progress > 7000f && s.MinimumClearance > 1f));
            Record(checks, "spear-moving-coast-locked-native-horizontal-lead-observed", shots.All(s => s.Samples.Cast<Dictionary<string, object>>()
                .Any(sample => (bool)sample["directTerminalClear"] && (float)sample["nativeHorizontalTerminalLeadM"] > 3f &&
                    (float)sample["targetHorizontalSpeedMps"] > 2f)));
            Record(checks, "spear-moving-coast-both-contact-intended-native-hull", shots.All(s => s.HitArmor && s.IntendedTargetContact));
            Save(output, report); ClearShots(); pikeCounterfireOwner = null;
            yield return null;
        }

        private static object MovingShipSample(Ship target, ShipAI ai, float seconds, string phase)
        {
            return new Dictionary<string, object> { ["seconds"] = seconds, ["phase"] = phase,
                ["position"] = V(target.GlobalPosition().AsVector3()), ["velocity"] = V(target.rb.velocity),
                ["nativeAiState"] = ai.state.ToString(), ["nativeAiEnabled"] = ai.enabled,
                ["nativeThrottle"] = target.GetInputs().throttle, ["nativeRudder"] = target.GetInputs().steering,
                ["holdPosition"] = target.holdPosition };
        }

        private static bool FindMovingShipWaterRoute(GlobalPosition start, Vector3 incoming, out GlobalPosition end,
            out Vector3 heading, out float minimumEnvelopeClearance)
        {
            end = start; heading = Vector3.Cross(Vector3.up, incoming); minimumEnvelopeClearance = 0f;
            var hulls = new List<KeyValuePair<Ship, float>>();
            foreach (Ship ship in UnitRegistry.allUnits.OfType<Ship>().Where(s => s != null && !s.disabled))
            {
                float radius = ship.maxRadius;
                foreach (Collider collider in ship.GetComponentsInChildren<Collider>())
                    if (collider != null && collider.enabled && !collider.isTrigger && collider.gameObject.layer != PhysicsLayers.ExclusionZones)
                        radius = Mathf.Max(radius, Vector3.Distance(collider.bounds.center, ship.transform.position) + collider.bounds.extents.magnitude);
                hulls.Add(new KeyValuePair<Ship, float>(ship, radius));
            }
            foreach (float angle in new[] { 90f, -90f, 60f, -60f, 120f, -120f, 45f, -45f })
            {
                Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * incoming;
                Vector3 side = Vector3.Cross(Vector3.up, direction);
                bool clear = true;
                for (int i = 0; i <= 30 && clear; i++)
                for (int cross = -1; cross <= 1; cross++)
                    if (Ground(start + direction * (i * 100f) + side * cross * 250f) > .5f) { clear = false; break; }
                if (!clear) continue;
                float clearance = float.MaxValue;
                foreach (var hull in hulls)
                {
                    Vector3 relative = hull.Key.GlobalPosition() - start; relative.y = 0f;
                    float along = Mathf.Clamp(Vector3.Dot(relative, direction), 0f, 3000f);
                    clearance = Mathf.Min(clearance, (relative - direction * along).magnitude - hull.Value);
                }
                if (clearance < 500f) continue;
                heading = direction; end = start + direction * 3000f; minimumEnvelopeClearance = clearance;
                return true;
            }
            return false;
        }

        private static bool FindSteepTerrainLeg(MapSettings map, out GlobalPosition start, out GlobalPosition end,
            out GlobalPosition ridge, out Vector3 heading, out float peak)
        {
            for (int x = 1; x < 36; x++)
            for (int z = 1; z < 36; z++)
            {
                GlobalPosition crest = new GlobalPosition((x / 36f - .5f) * map.MapSize.x, Datum.SeaLevel.y,
                    (z / 36f - .5f) * map.MapSize.y);
                float height = Ground(crest);
                if (height < 250f || height > 900f) continue;
                for (int angle = 0; angle < 16; angle++)
                {
                    Vector3 direction = Quaternion.AngleAxis(angle * 22.5f, Vector3.up) * Vector3.forward;
                    Vector3 side = Vector3.Cross(Vector3.up, direction);
                    GlobalPosition a = crest - direction * 6500f, b = crest + direction * 6500f;
                    if (Ground(a) > .5f || Ground(b) > .5f || Ground(a + side * 400f) > .5f || Ground(a - side * 400f) > .5f ||
                        Ground(a + direction * 400f) > .5f || Ground(b + side * 150f) > .5f || Ground(b - side * 150f) > .5f) continue;
                    float shoulder = Mathf.Min(Ground(crest + side * 1500f), Ground(crest - side * 1500f));
                    if (shoulder > height * .45f) continue;
                    start = a; end = b; ridge = crest; ridge.y = height; heading = direction; peak = height;
                    return true;
                }
            }
            start = end = ridge = default(GlobalPosition); heading = Vector3.forward; peak = 0f; return false;
        }

        private static Shot Add(Missile missile)
        {
            var shot = new Shot { Missile = missile, Initial = missile.transform.position, Last = missile.transform.position,
                Guidance = missile.GetComponent<NaturalCruiseGuidance>(), Tactics = missile.GetComponent<NaturalCruiseTactics>(),
                Optical = missile.GetComponent<OpticalSeekerCruiseMissile>(),
                PikeCountermeasures = missile.GetComponent<NaturalPikeCountermeasures>() };
            shot.NativeOpticalConfigured = shot.Optical != null && shot.Guidance == null && shot.Tactics == null &&
                shot.PikeCountermeasures == null && missile.GetComponents<MissileSeeker>().Length == 1;
            missile.targetID.TryGetUnit(out shot.IntendedTarget);
            Shots.Add(missile, shot); return shot;
        }
        private static IEnumerator Observe(Shot[] shots, Ship owner, Unit target, Vector3 heading, float seconds,
            Dictionary<string, object> report, string output)
        {
            float until = Time.time + seconds, nextSample = 0f, nextSave = 0f;
            while (Time.time < until && shots.Any(s => s.Missile != null && !s.Missile.disabled))
            {
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
                foreach (Shot shot in shots)
                {
                    Missile missile = shot.Missile;
                    if (missile == null || missile.disabled) continue;
                    if (shot.IntendedTarget == null) shot.IntendedTarget = target;
                    shot.ReadTelemetry();
                    shot.Last = missile.transform.position;
                    shot.Finite &= Finite(shot.Last) && Finite(missile.rb.velocity);
                    shot.PeakSpeed = Mathf.Max(shot.PeakSpeed, missile.speed);
                    shot.MaximumAltitude = Mathf.Max(shot.MaximumAltitude, missile.GlobalPosition().y);
                    shot.Progress = Mathf.Max(shot.Progress, Vector3.Dot(shot.Last - shot.Initial, heading));
                    shot.TargetRetained &= target != null && missile.targetID == target.persistentID;
                    float distance = target != null ? Vector3.Distance(shot.Last, target.transform.position) : 0f;
                    shot.MinimumRange = Mathf.Min(shot.MinimumRange, distance);
                    if (distance > 3000f && missile.timeSinceSpawn > 3f)
                        shot.MaximumMidcourseLaneDisplacement = Mathf.Max(shot.MaximumMidcourseLaneDisplacement,
                            Mathf.Abs(Vector3.Dot(shot.Last - shot.Initial, Vector3.Cross(Vector3.up, heading))));
                    if (distance > 500f && missile.timeSinceSpawn > 3f)
                        foreach (Shot neighbor in shots)
                            if (neighbor != shot && neighbor.Missile != null && !neighbor.Missile.disabled)
                                shot.MinimumNeighborSeparation = Mathf.Min(shot.MinimumNeighborSeparation,
                                    Vector3.Distance(missile.transform.position, neighbor.Missile.transform.position));
                    // Terminal intersection with the actual target/ground is
                    // reported as impact; do not confuse it with cruise clearance.
                    if (distance > 300f && missile.timeSinceSpawn > 3f)
                        shot.MinimumClearance = Mathf.Min(shot.MinimumClearance, missile.GlobalPosition().y - Ground(missile.GlobalPosition()));
                    NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
                    if (controller != null)
                    {
                        shot.NativeNeighbors = Mathf.Max(shot.NativeNeighbors, controller.NearbyMissiles);
                        shot.NativeGroupingUpdates = controller.UpdatesWithNeighbors;
                        shot.NativeControllerUpdates = controller.MethodUpdates;
                        shot.ThrottleMinimum = Mathf.Min(shot.ThrottleMinimum, controller.Throttle);
                        if (controller.CruiseActive && controller.NearbyMissiles > 0) shot.NativeNeighborSeconds += Time.fixedDeltaTime;
                    }
                    if (shot.Tactics != null)
                    {
                        if (shot.PikeCountermeasures != null && shot.Tactics.CurrentJink.sqrMagnitude > 1f)
                        {
                            Vector3 bearing = target.transform.position - missile.transform.position; bearing.y = 0f;
                            float lateral = Vector3.Dot(missile.rb.velocity, Vector3.Cross(Vector3.up, bearing.normalized));
                            shot.MaximumLateralSpeed = Mathf.Max(shot.MaximumLateralSpeed, Mathf.Abs(lateral));
                            int sign = Mathf.Abs(lateral) > 5f ? (lateral > 0f ? 1 : -1) : 0;
                            if (sign != 0 && sign != shot.LastLateralSign && missile.timeSinceSpawn - shot.LastLateralReversal > .5f)
                            {
                                if (shot.LastLateralSign != 0) shot.LateralReversals++;
                                shot.LastLateralSign = sign; shot.LastLateralReversal = missile.timeSinceSpawn;
                            }
                        }
                    }
                    if (Time.time >= nextSample)
                    {
                        if (shot.Guidance != null) shot.GuidanceReport = shot.Guidance.Capture();
                        Vector3 targetVelocity = target != null && target.rb != null ? target.rb.velocity : Vector3.zero;
                        Vector3 horizontalTargetVelocity = targetVelocity; horizontalTargetVelocity.y = 0f;
                        Transform opticalPart = shot.Optical != null ? (Transform)NaturalWeapons.Get(shot.Optical, "targetPart") : null;
                        Vector3 terminalLead = shot.Guidance != null ? shot.Guidance.TerminalAim - shot.Guidance.TerminalPhysicalSurface :
                            opticalPart != null ? (GlobalPosition)NaturalWeapons.Get(shot.Optical, "knownPos") - opticalPart.GlobalPosition() : Vector3.zero;
                        terminalLead.y = 0f;
                        shot.Samples.Add(new Dictionary<string, object> { ["seconds"] = missile.timeSinceSpawn,
                            ["position"] = V(missile.GlobalPosition().AsVector3()), ["velocity"] = V(missile.rb.velocity),
                            ["forward"] = V(missile.transform.forward),
                            ["angleOfAttackDegrees"] = Vector3.Angle(missile.rb.velocity, missile.transform.forward),
                            ["nativeSteeringInputs"] = V((Vector3)NaturalWeapons.Get(missile, "inputs")),
                            ["currentFinAreaM2"] = NaturalWeapons.Get(missile, "currentFinArea"),
                            ["currentEngineThrustNewtons"] = NaturalWeapons.Get(missile, "engineCurrentThrust"),
                            ["nativeMaximumTurnRateDegreesPerSecond"] = NaturalWeapons.Get(missile, "maxTurnRate"),
                            ["nativeMotorStage"] = NaturalWeapons.Get(missile, "motorStage"),
                            ["rangeM"] = distance, ["seekerMode"] = missile.seekerMode.ToString(),
                            ["targetPosition"] = target != null ? V(target.GlobalPosition().AsVector3()) : null,
                            ["targetVelocity"] = V(targetVelocity), ["targetHorizontalSpeedMps"] = horizontalTargetVelocity.magnitude,
                            ["nativeHorizontalTerminalLeadM"] = terminalLead.magnitude,
                            ["phase"] = shot.Guidance != null ? shot.Guidance.FlightPhase : shot.OpticalTerminal ? "native-optical-terminal" : "native-optical-cruise",
                            ["nativeOpticalTerminal"] = shot.OpticalTerminal,
                            ["nativeOpticalTargetPart"] = opticalPart != null ? opticalPart.name : null,
                            ["commandedAltitudeM"] = shot.Guidance != null ? shot.Guidance.CommandedAltitude : 0f,
                            ["terrainFloorM"] = shot.Guidance != null ? shot.Guidance.CurrentTerrainFloor : 0f,
                            ["currentTerrainM"] = shot.Guidance != null ? shot.Guidance.CurrentTerrainAltitude : 0f,
                            ["approachGroundPeakM"] = shot.Guidance != null ? shot.Guidance.ApproachGroundPeak : 0f,
                            ["directTerminalClear"] = shot.Guidance != null ? shot.Guidance.DirectTerminalActive :
                                shot.OpticalTerminal && target != null && target.LineOfSight(missile.transform.position, 1000f),
                            ["terminalAim"] = shot.Guidance != null ? V(shot.Guidance.TerminalAim.AsVector3()) : null,
                            ["terminalPhysicalSurface"] = shot.Guidance != null ? V(shot.Guidance.TerminalPhysicalSurface.AsVector3()) : null,
                            ["terminalBaseAim"] = shot.Guidance != null ? V(shot.Guidance.TerminalBaseAim.AsVector3()) : null,
                            ["terminalCollider"] = shot.Guidance != null ? shot.Guidance.TerminalColliderName : null,
                            ["terminalAimGroundM"] = shot.Guidance != null ? shot.Guidance.TerminalAimGround : 0f,
                            ["terminalPathBlockReason"] = shot.Guidance != null ? shot.Guidance.TerminalPathBlockReason : null,
                            ["terminalBlockingCollider"] = shot.Guidance != null ? shot.Guidance.TerminalBlockingCollider : null,
                            ["nativeRadarPosition"] = shot.Guidance != null ? V(shot.Guidance.NativeRadarPosition.AsVector3()) : null,
                            ["nativeNearbyMissilesExcludingSelf"] = controller != null ? controller.NearbyMissiles : shot.NativeNeighbors,
                            ["nativeGroupingUpdates"] = controller != null ? controller.UpdatesWithNeighbors : shot.NativeGroupingUpdates,
                            ["nativeThrottle"] = controller != null ? controller.Throttle : (float)NaturalWeapons.Get(missile, "throttle"),
                            ["nativeCruiseController"] = controller != null ? controller.Sample() : null });
                    }
                }
                if (Time.time >= nextSample) nextSample = Time.time + .5f;
                if (Time.time >= nextSave) { nextSave = Time.time + 10f; Save(output, report); }
                yield return new WaitForFixedUpdate();
            }
        }
        private static void ObserveNativeOpticalWaypoint(OpticalSeekerCruiseMissile __instance)
        {
            // Explicit diagnostic hook only. Pike's disabled child helper has
            // no root Missile here and continues using its own observer.
            Missile missile = __instance.GetComponent<Missile>();
            Shot shot;
            if (missile == null || !Shots.TryGetValue(missile, out shot) || shot.Optical != __instance) return;
            shot.NativeControllerUpdates++;
            NaturalCruiseNativeExperiment.RecordOpticalWaypoint();
            int neighbors = 0;
            if (missile.NetworkHQ != null)
                foreach (Missile other in missile.NetworkHQ.GetCruiseMissiles())
                    if (other != null && other != missile && !other.disabled &&
                        FastMath.InRange(other.GlobalPosition(), missile.GlobalPosition(), 5000f)) neighbors++;
            shot.NativeNeighbors = Mathf.Max(shot.NativeNeighbors, neighbors);
            if (neighbors > 0) shot.NativeGroupingUpdates++;
            shot.ThrottleMinimum = Mathf.Min(shot.ThrottleMinimum, (float)NaturalWeapons.Get(missile, "throttle"));
        }
        private static void Penetrating(Missile __instance, IDamageable damageable, Vector3 hitPoint)
        {
            if (!Shots.TryGetValue(__instance, out Shot shot) || shot.Detonated || damageable == null) return;
            Dictionary<string, object> item = NativeEvent(__instance, "native-Missile.PenetrateObject");
            Unit contacted = damageable.GetUnit();
            if (item != null)
            {
                item["contactedId"] = contacted != null ? contacted.persistentID.ToString() : null;
                item["contactedKey"] = contacted != null && contacted.definition != null ? contacted.definition.jsonKey : null;
                item["sameOwnerMissile"] = contacted is Missile other && other.ownerID == __instance.ownerID;
                item["point"] = V(hitPoint.ToGlobalPosition().AsVector3());
            }
            if (!(damageable is UnitPart detached && detached.IsDetached()))
                RecordTargetContact(shot, damageable.GetUnit(), hitPoint, "native-Missile.PenetrateObject");
        }

        private static Dictionary<string, object> NativeEvent(Missile missile, string kind)
        {
            if (missile == null || !Shots.TryGetValue(missile, out Shot shot) || shot.NativeEvents.Count >= 256) return null;
            var result = new Dictionary<string, object> { ["kind"] = kind, ["seconds"] = missile.timeSinceSpawn,
                ["missileId"] = missile.persistentID.ToString(), ["position"] = V(missile.GlobalPosition().AsVector3()),
                ["velocityBefore"] = V(missile.rb.velocity), ["disabledBefore"] = missile.disabled,
                ["commandedAltitudeM"] = shot.Guidance != null ? (object)shot.Guidance.CommandedAltitude : null,
                ["currentJink"] = shot.Tactics != null ? V(shot.Tactics.CurrentJink) : null,
                ["steeringInputs"] = V((Vector3)NaturalWeapons.Get(missile, "inputs")) };
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

        private static void TraceNativeAerodynamics(Missile __instance)
        {
            if (__instance == null || __instance.disabled || !Shots.TryGetValue(__instance, out Shot shot) ||
                shot.PikeCountermeasures == null || shot.IntendedTarget == null || shot.NativeAeroSamples.Count >= 512) return;
            float range = Vector3.Distance(__instance.transform.position, shot.IntendedTarget.transform.position);
            if (range > 12000f) return;
            Vector3 velocity = __instance.rb.velocity;
            float elapsed = shot.LastAeroTime >= 0f ? __instance.timeSinceSpawn - shot.LastAeroTime : 0f;
            Vector3 observedAcceleration = elapsed > 0f ? (velocity - shot.LastAeroVelocity) / elapsed : Vector3.zero;
            shot.LastAeroTime = __instance.timeSinceSpawn; shot.LastAeroVelocity = velocity;
            if (__instance.timeSinceSpawn < shot.NextAeroSample) return;
            shot.NextAeroSample = __instance.timeSinceSpawn + .049f;
            GlobalPosition current = __instance.GlobalPosition();
            Vector3 wind = NetworkSceneSingleton<LevelInfo>.i.GetWind(current), airVelocity = velocity - wind;
            Vector3 forward = __instance.transform.forward, up = __instance.transform.up;
            float angle = Vector3.Angle(forward, airVelocity) * Mathf.Deg2Rad;
            float liftCoefficient = __instance.GetLiftCoeff(angle);
            float dragCoefficient = ((AnimationCurve)NaturalWeapons.Get(__instance, "dragCurve")).Evaluate(angle);
            float dynamicAreaPressure = .5f * __instance.airDensity * airVelocity.sqrMagnitude * (float)NaturalWeapons.Get(__instance, "currentFinArea");
            Vector3 lift = Vector3.Cross(Vector3.Cross(forward, airVelocity), airVelocity).normalized * (-liftCoefficient * dynamicAreaPressure);
            Vector3 drag = -airVelocity.normalized * dragCoefficient * dynamicAreaPressure;
            float supersonicDrag = (float)NaturalWeapons.Get(__instance, "supersonicDrag");
            if (supersonicDrag > 0f)
            {
                float soundSpeed = LevelInfo.GetSpeedOfSound(current.y);
                if (__instance.speed > 1.1f * soundSpeed) drag *= 1f + supersonicDrag;
                else if (__instance.speed > .9f * soundSpeed)
                {
                    float blend = (.1f - Mathf.Min(Mathf.Abs((soundSpeed - __instance.speed) / soundSpeed), .1f)) / .1f;
                    drag *= 1f + blend * blend * blend * (supersonicDrag + .15f);
                }
            }
            object motor = NaturalWeapons.Get(__instance, "motor");
            float topSpeed = motor != null ? (float)NaturalWeapons.Get(motor, "topSpeed") : 0f;
            float thrust = motor != null && __instance.speed < topSpeed ?
                (float)NaturalWeapons.Get(__instance, "engineCurrentThrust") * (float)NaturalWeapons.Get(__instance, "throttle") : 0f;
            GlobalPosition aim = (GlobalPosition)NaturalWeapons.Get(__instance, "aimPoint");
            Vector3 aimDirection = (aim - current).normalized;
            bool reached = (bool)NaturalWeapons.Get(__instance, "reachedOnTarget");
            Quaternion rotation = __instance.transform.rotation;
            shot.NativeAeroSamples.Add(new Dictionary<string, object> {
                ["seconds"] = __instance.timeSinceSpawn, ["rangeM"] = range,
                ["position"] = V(current.AsVector3()), ["velocity"] = V(velocity),
                ["precedingPhysicsStepSeconds"] = elapsed, ["observedStepAcceleration"] = V(observedAcceleration),
                ["wind"] = V(wind), ["airVelocity"] = V(airVelocity), ["airAngleOfAttackDegrees"] = angle * Mathf.Rad2Deg,
                ["bodyQuaternion"] = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                ["forward"] = V(forward), ["up"] = V(up), ["right"] = V(__instance.transform.right),
                ["bankDegrees"] = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, forward), up, forward),
                ["angularVelocity"] = V(__instance.rb.angularVelocity),
                ["nativeAimPoint"] = V(aim.AsVector3()), ["nativeAimDirection"] = V(aimDirection),
                ["seekerMode"] = __instance.seekerMode.ToString(),
                ["directTerminalClear"] = shot.Guidance != null && shot.Guidance.DirectTerminalActive,
                ["terminalBaseAim"] = shot.Guidance != null ? V(shot.Guidance.TerminalBaseAim.AsVector3()) : null,
                ["terminalCollider"] = shot.Guidance != null ? shot.Guidance.TerminalColliderName : null,
                ["nativeSteeringReference"] = V(reached ? (forward + velocity.normalized) * .5f : forward),
                ["nativeSteeringInputs"] = V((Vector3)NaturalWeapons.Get(__instance, "inputs")),
                ["reachedOnTarget"] = reached, ["currentJink"] = shot.Tactics != null ? V(shot.Tactics.CurrentJink) : null,
                ["commandedAltitudeM"] = shot.Guidance != null ? (object)shot.Guidance.CommandedAltitude : null,
                ["commandedVerticalSpeedMps"] = shot.Guidance != null ? (object)shot.Guidance.CommandedVerticalSpeed : null,
                ["liftCoefficient"] = liftCoefficient, ["dragCoefficient"] = dragCoefficient,
                ["derivedNativeLiftForce"] = V(lift), ["derivedNativeDragForce"] = V(drag),
                ["derivedCurrentMotorForce"] = V(forward * thrust), ["massKg"] = __instance.rb.mass,
                ["rigidbodyDrag"] = __instance.rb.drag, ["usesGravity"] = __instance.rb.useGravity,
                ["gravity"] = V(Physics.gravity), ["airDensity"] = __instance.airDensity,
                ["currentFinAreaM2"] = NaturalWeapons.Get(__instance, "currentFinArea")
            });
        }

        private static bool PikeDirectJinkPreserved(Shot shot)
        {
            var direct = shot.NativeAeroSamples.Cast<Dictionary<string, object>>().Where(s => (bool)s["directTerminalClear"]).ToArray();
            if (direct.Length < 8 || direct.Any(s => (string)s["seekerMode"] != Missile.SeekerMode.activeLock.ToString() || s["terminalCollider"] == null)) return false;
            int activeJink = 0, closedOut = 0;
            foreach (Dictionary<string, object> sample in direct)
            {
                float[] aim = (float[])sample["nativeAimPoint"], basis = (float[])sample["terminalBaseAim"], jink = (float[])sample["currentJink"];
                Vector3 actualOffset = new Vector3(aim[0] - basis[0], aim[1] - basis[1], aim[2] - basis[2]);
                Vector3 currentJink = new Vector3(jink[0], jink[1], jink[2]);
                // Account only for global-position float precision. This
                // compares the native stored aim after Steering to the live
                // jink, including its ordinary final cutoff, not a copy of
                // the guidance function's intended return value.
                if (Vector3.Distance(actualOffset, currentJink) > .05f) return false;
                if (currentJink.sqrMagnitude > 1f) activeJink++;
                else if ((float)sample["rangeM"] < 1250f) closedOut++;
            }
            return activeJink >= 4 && closedOut >= 1;
        }

        private static void RecordTargetContact(Shot shot, Unit contacted, Vector3 point, string evidence)
        {
            if (contacted == null || shot.IntendedTargetContact) return;
            shot.ContactedUnitKey = contacted.definition != null ? contacted.definition.jsonKey : contacted.name;
            shot.ContactEvidence = evidence; shot.ContactAge = shot.Missile.timeSinceSpawn;
            shot.ContactGlobalPosition = point.ToGlobalPosition().AsVector3();
            shot.IntendedTargetContact = contacted == shot.IntendedTarget;
        }

        private static void Detonating(Missile __instance, bool hitArmor, bool hitTerrain)
        {
            if (!Shots.TryGetValue(__instance, out Shot shot) || shot.Detonated) return;
            NaturalWeaponPhase phase = __instance.GetComponent<NaturalWeaponPhase>();
            if (hitArmor && phase != null && phase.ContactedUnit != null && Time.time - phase.ContactTime < .1f)
                RecordTargetContact(shot, phase.ContactedUnit, __instance.transform.position, "native-collision-callback-contact");
            shot.ReadTelemetry();
            if (shot.Guidance != null) shot.GuidanceReport = shot.Guidance.Capture();
            shot.Detonated = true; shot.HitArmor = hitArmor; shot.HitTerrain = hitTerrain;
            shot.DetonationTime = __instance.timeSinceSpawn;
            if (__instance.targetID.TryGetUnit(out Unit target))
                shot.MinimumRange = Mathf.Min(shot.MinimumRange, Vector3.Distance(__instance.transform.position, target.transform.position));
        }
        private static float Ground(GlobalPosition point)
        {
            return PathfindingAgent.RaycastTerrain(point, out RaycastHit hit) ? Mathf.Max(0f, hit.point.y - Datum.LocalSeaY) : 0f;
        }
        private static bool FindTerrainLeg(MapSettings map, out GlobalPosition start, out GlobalPosition target, out Vector3 heading, out float ridgeDistance)
        {
            for (int x = 1; x < 18; x++)
            for (int z = 1; z < 18; z++)
            {
                GlobalPosition ridge = new GlobalPosition((x / 18f - .5f) * map.MapSize.x, Datum.SeaLevel.y,
                    (z / 18f - .5f) * map.MapSize.y);
                float peak = Ground(ridge);
                if (peak < 60f || peak > 650f) continue;
                for (int angle = 0; angle < 16; angle++)
                {
                    Vector3 direction = Quaternion.AngleAxis(angle * 22.5f, Vector3.up) * Vector3.forward;
                    GlobalPosition a = ridge - direction * 6500f, b = ridge + direction * 5000f;
                    if (Ground(a) > .5f) continue;
                    float endHeight = Ground(b);
                    if (endHeight < 5f || endHeight > 450f) continue;
                    Vector3 lateral = Vector3.Cross(Vector3.up, direction);
                    float minimum = endHeight, maximum = endHeight;
                    for (int side = -1; side <= 1; side++)
                    for (int along = -1; along <= 1; along++)
                    {
                        float ground = Ground(b + (lateral * side + direction * along) * 50f);
                        minimum = Mathf.Min(minimum, ground); maximum = Mathf.Max(maximum, ground);
                    }
                    if (maximum - minimum > 14f) continue;
                    b.y = Datum.SeaLevel.y + endHeight;
                    start = a; target = b; heading = direction; ridgeDistance = 6500f; return true;
                }
            }
            start = target = default(GlobalPosition); heading = Vector3.forward; ridgeDistance = 0f; return false;
        }
        private static void ClearShots()
        {
            foreach (Missile missile in Shots.Keys.ToArray())
                if (missile != null)
                {
                    missile.SetTarget(null);
                    Object.Destroy(missile.gameObject);
                }
            Shots.Clear();
        }
        private static bool Finite(Vector3 value) => !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) ||
            float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
        private static object V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static void Record(List<object> checks, string name, bool pass) => checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = pass });
        private static void Save(string output, Dictionary<string, object> report) => File.WriteAllText(output, Audit.Json(report));
    }
}
