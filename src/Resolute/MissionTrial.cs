using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Authentication;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using NuclearOption.Social;
using Rewired.UI.ControlMapper;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit diagnostic for the copied build/test-game only. This never saves a
    // mission. Ship, weapon and damage components use their normal runtime code;
    // explicit controller gates isolate the measurements from autonomous combat.
    internal static class MissionTrial
    {
        private static Harmony isolation;
        private static Ship trialShip;
        private static bool controlledWeapons;
        private static Laser activeLaserPhysics;
        private static Unit laserTarget;
        private static readonly Dictionary<Gun, int> observedBullets = new Dictionary<Gun, int>();
        private static readonly Dictionary<Laser, int> observedLaserBeams = new Dictionary<Laser, int>();
        private static readonly Dictionary<Laser, int> observedLaserDamage = new Dictionary<Laser, int>();
        private sealed class NativeMissileLaunch
        {
            internal PersistentID requestedTarget;
            internal PersistentID requestedOwner;
            internal Vector3 globalPosition;
            internal Vector3 positionInShip;
            internal float aboveSea;
            internal bool withinShipBounds;
        }
        private static readonly Dictionary<Missile, NativeMissileLaunch> observedMissiles = new Dictionary<Missile, NativeMissileLaunch>();
        private static readonly Dictionary<Behaviour, bool> heldControllers = new Dictionary<Behaviour, bool>();
        private static readonly HashSet<FireControl> allowedDiagnosticFireControls = new HashSet<FireControl>();
        private static readonly List<string> suppressedSaveMethods = new List<string>();
        private static bool? previousBackgroundInputSetting;

        internal static IEnumerator Run(string[] args, ManualLogSource log)
        {
            string output = Audit.Argument(args, "--resolute-trial-output");
            var checks = new List<object>();
            var errors = new List<string>();
            var report = new Dictionary<string, object>
            {
                ["version"] = 1, ["mode"] = "native-offline-mission-trial",
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["gameVersion"] = Application.version, ["unityVersion"] = Application.unityVersion,
                ["gameDataPath"] = Application.dataPath, ["pluginSha256"] = Audit.AssemblySha256(),
                ["assetSha256"] = Audit.AssetSha256(), ["checks"] = checks,
                ["suppressedExternalServices"] = new[] { "Steam ticket acquisition", "DiscordManager.InitClient" },
                ["nativeErrors"] = errors, ["success"] = false
            };
            if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output))
            {
                log.LogError("Mission trial requires an absolute --resolute-trial-output path.");
                Application.Quit(2); yield break;
            }
            // Never start an automated armed trial in the user's installed game.
            string gameRoot = Directory.GetParent(Application.dataPath).FullName;
            if (!string.Equals(Path.GetFileName(gameRoot), "test-game", StringComparison.OrdinalIgnoreCase))
            {
                report["error"] = "Mission trial is restricted to a copied directory named test-game.";
                Save(output, report); Application.Quit(2); yield break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Application.LogCallback collect = delegate(string message, string stack, LogType type)
            {
                if ((type == LogType.Exception || type == LogType.Error || type == LogType.Assert) && errors.Count < 100)
                    errors.Add(message + "\n" + stack);
            };
            Application.logMessageReceived += collect;
            IEnumerator body = Execute(report, checks, output, log);
            try
            {
                while (true)
                {
                    bool more;
                    try { more = body.MoveNext(); }
                    catch (Exception ex)
                    {
                        report["error"] = ex.ToString();
                        log.LogError("Resolute mission trial: " + ex);
                        break;
                    }
                    if (!more) break;
                    yield return body.Current;
                }
            }
            finally
            {
                RestoreBackgroundInput(report);
                Application.logMessageReceived -= collect;
            }
            report["suppressedPersistenceMethods"] = suppressedSaveMethods;
            report["steamInitialized"] = SteamManager.ClientInitialized || SteamManager.ServerInitialized;
            report["success"] = !report.ContainsKey("error") && errors.Count == 0 &&
                checks.Cast<Dictionary<string, object>>().All(c => (bool)c["passed"]);
            Save(output, report);
            log.LogInfo("Resolute mission trial complete: " + report["success"] + "; " + output);
            // Leave persistence guards installed until the explicit test process exits.
            Application.Quit((bool)report["success"] ? 0 : 2);
        }

        private static IEnumerator Execute(Dictionary<string, object> report, List<object> checks, string output, ManualLogSource log)
        {
            InstallIsolation();
            string selectedTrial = Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-trial-only") ?? "full";
            bool performanceEditor = selectedTrial == "performance_editor" || selectedTrial == "performance_pose" || selectedTrial == "performance_inertia" || selectedTrial == "performance_editor_lifecycle";
            report["phase"] = "preload"; Save(output, report);
            GameManager.IsHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            MainMenu menu = Resources.FindObjectsOfTypeAll<MainMenu>().FirstOrDefault(m => m.gameObject.scene.IsValid());
            if (menu == null) throw new InvalidOperationException("Trial requires the serialized main-menu resource references.");
            var graphics = (GraphicsHelperSO)AccessTools.Field(typeof(MainMenu), "graphicsSettings").GetValue(menu);
            PlayerSettings.FirstInit(graphics);
            PlayerSettings.playerName = "Resolute local trial";
            PlayerSettings.playerName_Unsanitized = PlayerSettings.playerName;

            var tasks = new[]
            {
                Encyclopedia.Preload(CancellationToken.None).AsTask(),
                GameAssets.Preload(CancellationToken.None).AsTask(),
                NetworkManagerNuclearOption.Preload(CancellationToken.None).AsTask(),
                SoundManager.Preload(CancellationToken.None).AsTask(),
                DebugUI.Preload(CancellationToken.None).AsTask(),
                ResourcesAsyncLoader.LoadPrefab("Rewired", CancellationToken.None, go =>
                {
                    GameManager.controlMapper = go.GetComponentInChildren<ControlMapper>(true);
                }).AsTask(),
                ResourcesAsyncLoader.LoadPrefab("EventSystem", CancellationToken.None, go =>
                {
                    GameManager.eventSystem = go.GetComponent<EventSystem>();
                }).AsTask()
            };
            Task preload = Task.WhenAll(tasks);
            float until = Time.realtimeSinceStartup + 180;
            while (!preload.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!preload.IsCompleted) throw new TimeoutException("Native resource preload exceeded 180 seconds.");
            preload.GetAwaiter().GetResult();
            IsolateBackgroundInput(report);
            Plugin.Instance.EnsureRegistered(Encyclopedia.i);
            Require(checks, "registered-definition", Encyclopedia.Lookup.ContainsKey(Plugin.DefinitionKey));
            Require(checks, "steam-remains-uninitialized", !SteamManager.ClientInitialized && !SteamManager.ServerInitialized);
            Require(checks, "discord-remains-uninitialized", Resources.FindObjectsOfTypeAll<DiscordManager>()
                .All(d => AccessTools.Field(typeof(DiscordManager), "_client").GetValue(d) == null));

            report["phase"] = "load-native-map"; Save(output, report);
            MissionManager.NewMission(NewMissionConfig.DefaultMission());
            Mission mission = MissionManager.CurrentMission;
            mission.Name = "Resolute isolated local trial";
            mission.environment.timeOfDay = 12;
            mission.environment.weatherIntensity = 0;
            TimeScaleManager.Scale = 1;
            Task host = NetworkManagerNuclearOption.i.StartHostAsync(
                new HostOptions(SocketType.Offline, performanceEditor ? GameState.Editor : GameState.SinglePlayer, default(MapKey))).AsTask();
            until = Time.realtimeSinceStartup + 240;
            while (!host.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!host.IsCompleted) throw new TimeoutException("Native map/host load exceeded 240 seconds.");
            host.GetAwaiter().GetResult();
            until = Time.realtimeSinceStartup + 60;
            while (((!performanceEditor && !MissionManager.IsRunning) || NetworkSceneSingleton<Spawner>.i == null ||
                   NetworkSceneSingleton<LevelInfo>.i == null || NetworkSceneSingleton<LevelInfo>.i.LoadedMapSettings == null)
                   && Time.realtimeSinceStartup < until) yield return null;
            Require(checks, "native-mission-running", performanceEditor ? GameManager.gameState == GameState.Editor : MissionManager.IsRunning);
            Require(checks, "offline-server", NetworkManagerNuclearOption.i.Server.Active && !NetworkManagerNuclearOption.i.Server.Listening);
            Require(checks, "native-spawner", NetworkSceneSingleton<Spawner>.i != null);
            report["map"] = NetworkManagerNuclearOption.i.MapKey.ToString();
            report["phase"] = "spawn"; Save(output, report);

            GlobalPosition spawn, destination, targetPosition;
            Vector3 forward;
            FindSeaCorridor(out spawn, out destination, out targetPosition, out forward);
            report["corridor"] = new Dictionary<string, object>
            { ["spawn"] = V(spawn.AsVector3()), ["destination"] = V(destination.AsVector3()), ["target"] = V(targetPosition.AsVector3()) };
            FactionHQ friendly = FactionRegistry.HqFromName("Boscali");
            FactionHQ hostile = FactionRegistry.HqFromName("Primeva");
            Require(checks, "native-factions", friendly != null && hostile != null && friendly != hostile);
            if (selectedTrial == "ship_redo_baseline")
            {
                report["selection"] = selectedTrial;
                controlledWeapons = true;
                IEnumerator shipBaseline = ShipRedoBaselineTrial.Run(spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output);
                while (shipBaseline.MoveNext()) yield return shipBaseline.Current;
                yield break;
            }
            if (selectedTrial == "performance_damage" || selectedTrial == "performance_damage_redo")
            {
                report["selection"] = selectedTrial;
                controlledWeapons = true;
                IEnumerator damagePerformance = DamagePerformanceTrial.Run(spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output, selectedTrial == "performance_damage_redo");
                while (damagePerformance.MoveNext()) yield return damagePerformance.Current;
                yield break;
            }
            if (selectedTrial == "performance_editor_lifecycle")
            {
                report["selection"] = selectedTrial;
                IEnumerator lifecycle = EditorPoseTrial.RunLifecycle(spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output);
                while (lifecycle.MoveNext()) yield return lifecycle.Current;
                yield break;
            }
            if (selectedTrial == "performance_inertia")
            {
                report["selection"] = selectedTrial;
                IEnumerator inertiaPerformance = EditorPoseTrial.RunInertia(spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output);
                while (inertiaPerformance.MoveNext()) yield return inertiaPerformance.Current;
                yield break;
            }
            if (selectedTrial == "performance_pose")
            {
                report["selection"] = selectedTrial;
                IEnumerator posePerformance = EditorPoseTrial.Run(spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output);
                while (posePerformance.MoveNext()) yield return posePerformance.Current;
                yield break;
            }
            if (selectedTrial == "performance" || performanceEditor)
            {
                report["selection"] = selectedTrial;
                IEnumerator performance = PerformanceTrial.Run(performanceEditor, spawn, Quaternion.LookRotation(forward, Vector3.up), friendly, report, checks, output);
                while (performance.MoveNext()) yield return performance.Current;
                yield break;
            }
            ShipDefinition trialDefinition = (ShipDefinition)Encyclopedia.Lookup[Plugin.DefinitionKey];
            GlobalPosition editorSpawn = spawn + Vector3.up * trialDefinition.spawnOffset.y;
            var saved = new SavedShip("resolute_trial_ship")
            {
                type = Plugin.DefinitionKey, faction = friendly.faction.factionName,
                globalPosition = editorSpawn, rotation = Quaternion.LookRotation(forward, Vector3.up), skill = 1, holdPosition = true
            };
            Ship ship;
            Require(checks, "normal-spawner-success", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship));
            trialShip = ship;
            ship.LinkSavedUnit(saved);
            yield return null; yield return null;
            Require(checks, "spawned-active", ship != null && ship.gameObject.activeInHierarchy);
            Require(checks, "native-local-simulation", ship.LocalSim && ship.IsServer && ship.rb != null);
            Require(checks, "native-unit-registry", UnitRegistry.allUnits.Contains(ship) && ship.persistentID.IsValid);
            Require(checks, "native-compartments", ship.parts.Count > 0 && ship.parts.All(p => p != null && p.parentUnit == ship));
            Require(checks, "native-weapon-stations", ship.weaponStations.Count > 0 && ship.weaponStations.All(s => s.Weapons.Count > 0));
            if (selectedTrial == "impact")
            {
                controlledWeapons = true;
                report["selection"] = selectedTrial;
                IEnumerator impacts = MissileImpactTrial.Run(ship, report, checks, output);
                while (impacts.MoveNext()) yield return impacts.Current;
                yield break;
            }
            if (selectedTrial == "cruise_behavior")
            {
                controlledWeapons = true;
                report["selection"] = selectedTrial;
                IEnumerator cruiseBehavior = NaturalCruiseBehaviorTrial.Run(ship, report, checks, output);
                while (cruiseBehavior.MoveNext()) yield return cruiseBehavior.Current;
                yield break;
            }
            if (selectedTrial == "exo_robustness")
            {
                controlledWeapons = true;
                report["selection"] = selectedTrial;
                IEnumerator exoRobustness = NaturalExoRobustnessTrial.Run(ship, report, checks, output);
                while (exoRobustness.MoveNext()) yield return exoRobustness.Current;
                yield break;
            }
            report["shipIdentity"] = new Dictionary<string, object>
            { ["definition"] = ship.definition.jsonKey, ["persistentId"] = ship.persistentID.ToString(),
              ["massKg"] = ship.rb.mass, ["parts"] = ship.parts.Count, ["weaponStations"] = ship.weaponStations.Count };
            report["phase"] = "buoyancy-settling";
            var buoyancy = new List<object>();
            report["buoyancy"] = buoyancy;
            report["editorSpawnGlobalPosition"] = V(editorSpawn.AsVector3());
            Transform sourceVisual = ship.transform.Find("ResoluteVisual");
            Require(checks, "source-waterline-reference", sourceVisual != null);
            float settleStarted = Time.realtimeSinceStartup;
            for (int sample = 0; sample <= 6; sample++)
            {
                buoyancy.Add(BuoyancySample(ship, sourceVisual, Time.realtimeSinceStartup - settleStarted));
                Save(output, report);
                if (sample < 6) yield return new WaitForSecondsRealtime(5);
            }
            Require(checks, "finite-buoyancy", Finite(ship.transform.position) && Finite(ship.rb.velocity));
            Require(checks, "afloat", Mathf.Abs(ship.GlobalPosition().y - Datum.SeaLevel.y) < 12 && !ship.disabled);
            report["assembledCaptures"] = AssembledPreview.Capture(ship, Path.Combine(Path.GetDirectoryName(output), "game_previews"));
            Save(output, report);
            Require(checks, "settled-vertical-motion", Mathf.Abs(ship.rb.velocity.y) < .25f);
            Require(checks, "settled-upright", Mathf.Abs(Mathf.DeltaAngle(0, ship.transform.eulerAngles.x)) < 5 &&
                Mathf.Abs(Mathf.DeltaAngle(0, ship.transform.eulerAngles.z)) < 5);
            Require(checks, "source-waterline-near-sea", Mathf.Abs(sourceVisual.position.ToGlobalPosition().y - Datum.SeaLevel.y) < 1.5f);

            report["phase"] = "native-movement"; Save(output, report);
            Vector3 start = ship.GlobalPosition().AsVector3();
            ship.SetHoldPosition(false);
            ship.UnitCommand.SetDestination(destination, false);
            Require(checks, "native-command-accepted", ship.UnitCommand.GetCommandCached().position == destination);
            float moveStart = Time.realtimeSinceStartup;
            float greatestTravel = 0, greatestSpeed = 0;
            while (Time.realtimeSinceStartup - moveStart < 35)
            {
                if (ship == null || ship.disabled) throw new InvalidOperationException("Ship disabled during the movement check.");
                Vector3 now = ship.GlobalPosition().AsVector3(); now.y = start.y;
                greatestTravel = Mathf.Max(greatestTravel, Vector3.Distance(now, start));
                greatestSpeed = Mathf.Max(greatestSpeed, ship.rb.velocity.magnitude);
                if (greatestTravel > 10 && greatestSpeed > 0.5f) break;
                yield return null;
            }
            report["movement"] = new Dictionary<string, object>
            { ["elapsedSeconds"] = Time.realtimeSinceStartup - moveStart, ["travelMetres"] = greatestTravel, ["maxSpeedMps"] = greatestSpeed };
            Require(checks, "native-command-causes-motion", greatestTravel > 10 && greatestSpeed > 0.5f);
            ship.SetHoldPosition(true);

            string selection = Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-trial-only") ?? "full";
            report["selection"] = selection;
            controlledWeapons = true;
            HoldControllers(ship);
            if (selection == "full" || selection == "ship_review" || selection == "style")
            {
                IEnumerator style = SurfaceStyleTrial.Run(ship, report, checks, output);
                while (style.MoveNext()) yield return style.Current;
            }
            if (selection == "full" || selection == "ship_review" || selection == "systems")
            {
                IEnumerator systems = ShipSystemsTrial.Run(ship, report, checks, output);
                while (systems.MoveNext()) yield return systems.Current;
            }
            if (selection == "full" || selection == "ship_review" || selection == "structure")
            {
                IEnumerator structure = StructureReviewTrial.Run(ship, report, checks, output);
                while (structure.MoveNext()) yield return structure.Current;
            }
            if (selection == "full" || selection == "variants")
            {
                IEnumerator variants = VariantTrial.Run(ship, report, checks, output);
                while (variants.MoveNext()) yield return variants.Current;
            }
            if (selection == "full" || selection == "carrier")
            {
            IEnumerator carrierTrial = CarrierTrial.Run(ship, report, checks, output);
            while (carrierTrial.MoveNext()) yield return carrierTrial.Current;
            }

            if (selection == "full" || selection == "weapons")
            {
            report["phase"] = "native-weapons"; Save(output, report);
            controlledWeapons = true;
            HoldControllers(ship);
            report["weaponScope"] = "Explicit native WeaponStation.Fire commands with subject/target Turret and FireControl held. Native aim helper, weapon physics, projectiles, raycasts and damage run unchanged. Missile assertions cover launch position, ownership and the actual native spawn target argument; seeker retention is reported separately. This does not test autonomous targeting, seeker lock or combat decisions. Laser TakeDamage calls may be absorbed by target armor.";
            report["controlledPhaseGates"] = new[] { "Turret.FixedUpdate", "FireControl.HQTargetAssessment", "FireControl.DistributeTurretTargets" };
            ShipDefinition donor = Plugin.FindDonor(Encyclopedia.i);
            var laserChecks = new List<object>();
            report["lasers"] = laserChecks;
            Laser[] lasers = ship.weaponStations.SelectMany(s => s.Weapons).OfType<Laser>().Distinct().ToArray();
            Require(checks, "two-native-lasers", lasers.Length == 2);
            foreach (Laser laser in lasers)
            {
                WeaponStation station = ship.weaponStations.Single(s => s.Weapons.Contains(laser));
                Turret turret = laser.GetComponentInParent<Turret>();
                Require(checks, "laser-" + station.Number + "-native-turret", turret != null);
                GlobalPosition laserTargetPosition = FindLaserTargetPosition(ship, laser);
                AircraftDefinition laserAircraftDefinition = Encyclopedia.i.aircraft.First(d => d != null && d.unitPrefab != null &&
                    !d.jsonKey.StartsWith("rsl_") && d.aircraftParameters != null && !d.aircraftParameters.verticalLanding &&
                    d.aircraftParameters.maxSpeed > 250f && d.aircraftParameters.loadouts.Count > 0);
                Vector3 laserTargetVelocity = Vector3.ProjectOnPlane(ship.transform.forward, Vector3.up).normalized * 200f;
                Aircraft laserAircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, laserAircraftDefinition.unitPrefab,
                    laserAircraftDefinition.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), laserTargetPosition,
                    Quaternion.LookRotation(laserTargetVelocity), laserTargetVelocity, null, hostile, "resolute_laser_target_" + station.Number, 1f, 1f);
                laserTarget = laserAircraft;
                float laserTargetAltitude = laserTarget.transform.position.y;
                for (int warm = 0; warm < 3; warm++)
                {
                    MaintainLaserAircraft(laserAircraft, laserTargetVelocity, laserTargetAltitude);
                    yield return new WaitForFixedUpdate();
                }
                UnitPart[] laserTargetParts = laserTarget.GetAllParts().Where(p => p != null && p.parentUnit == laserTarget).Distinct().ToArray();
                Require(checks, "laser-" + station.Number + "-native-aircraft-parts", laserTargetParts.Length > 1);
                float healthBeforeLaser = laserTargetParts.Sum(p => Mathf.Max(0f, p.hitPoints));
                observedLaserBeams[laser] = 0;
                observedLaserDamage[laser] = 0;
                laser.SetTarget(laserTarget);
                Dictionary<string, object> selectedLaserPoint;
                Transform laserAim = SelectLaserTargetPoint(laser, turret, laserTarget, out selectedLaserPoint);
                // Only the diagnostic aim input is chosen explicitly. The
                // native station command, limits, beam raycast and damage remain active.
                AccessTools.Field(typeof(Laser), "currentTargetTransform").SetValue(laser, laserAim);
                MethodInfo aim = AccessTools.Method(typeof(Turret), "AimTurret", new[] { typeof(Vector3) });
                float laserStarted = Time.realtimeSinceStartup;
                var laserGates = new List<object>();
                laserGates.Add(LaserGateSnapshot(laser, turret, "initial", laserStarted));
                float nextLaserSample = laserStarted;
                until = laserStarted + 20;
                while (observedLaserDamage[laser] == 0 && Time.realtimeSinceStartup < until)
                {
                    if (laserAim == null) throw new InvalidOperationException("The controlled native laser target point was destroyed before damage.");
                    MaintainLaserAircraft(laserAircraft, laserTargetVelocity, laserTargetAltitude);
                    RefreshLaserTargetTrack(ship.NetworkHQ, laserTarget);
                    AccessTools.Field(typeof(Laser), "currentTargetTransform").SetValue(laser, laserAim);
                    // Use the native traverse/elevation limits and rates, then let
                    // Unity invoke the original Laser.FixedUpdate on a real frame.
                    aim.Invoke(turret, new object[] { EffectiveLaserTargetPoint(laser) - laser.transform.position });
                    station.Fire(ship, laserTarget);
                    bool sampleLaser = Time.realtimeSinceStartup >= nextLaserSample;
                    if (sampleLaser) laserGates.Add(LaserGateSnapshot(laser, turret, "after-command", laserStarted));
                    yield return new WaitForFixedUpdate();
                    if (sampleLaser)
                    {
                        laserGates.Add(LaserGateSnapshot(laser, turret, "after-fixed-update", laserStarted));
                        nextLaserSample = Time.realtimeSinceStartup + 1f;
                    }
                }
                laserGates.Add(LaserGateSnapshot(laser, turret, "final", laserStarted));
                Renderer beam = (Renderer)AccessTools.Field(typeof(Laser), "beamRenderer").GetValue(laser);
                Transform hit = (Transform)AccessTools.Field(typeof(Laser), "hitTransform").GetValue(laser);
                laserChecks.Add(new Dictionary<string, object>
                { ["station"] = station.Number, ["mount"] = turret.name, ["target"] = laserTarget.persistentID.ToString(),
                  ["targetPosition"] = V(laserTarget.GlobalPosition().AsVector3()), ["nativeBeamPhysicsTicks"] = observedLaserBeams[laser],
                  ["nativeTargetDamageCalls"] = observedLaserDamage[laser], ["targetHealthBefore"] = healthBeforeLaser,
                  ["targetHealthAfter"] = laserTargetParts.Sum(p => p != null ? Mathf.Max(0f, p.hitPoints) : 0f),
                  ["targetNativePartCount"] = laserTargetParts.Length, ["targetType"] = laserTarget.definition.jsonKey,
                  ["targetCourse"] = "Native aircraft at 300 m above sea level, 200 m/s straight course; diagnostic holds attitude and altitude.",
                  ["beamReferencePresent"] = beam != null,
                  ["lastHit"] = hit != null ? hit.name : null, ["elapsed"] = Time.realtimeSinceStartup - laserStarted,
                  ["selectedAimPoint"] = selectedLaserPoint, ["nativeGateSamples"] = laserGates });
                Save(output, report);
                Require(checks, "laser-" + station.Number + "-native-beam-and-target-hit", observedLaserBeams[laser] > 0 && observedLaserDamage[laser] > 0);
                laser.SetTarget(null);
                Object.Destroy(laserAim.gameObject);
                Object.Destroy(laserAircraft.gameObject);
                yield return new WaitForSecondsRealtime(.3f);
            }
            laserTarget = null;

            Ship target = NetworkSceneSingleton<Spawner>.i.SpawnShip(donor.unitPrefab, targetPosition + Vector3.up * donor.spawnOffset.y,
                Quaternion.LookRotation(-forward, Vector3.up), hostile, "resolute_trial_target", 1, true);
            HoldControllers(target);
            yield return null; yield return null;
            IEnumerator ballisticReference = NaturalBallisticReferenceTrial.WaitUntilReady(report, checks, output);
            while (ballisticReference.MoveNext()) yield return ballisticReference.Current;
            var launches = new List<object>();
            var hatchProbes = new List<AnimationTrial.Probe>();
            report["missileLaunches"] = launches;
            foreach (WeaponStation station in ship.weaponStations.Where(s => s.Weapons.Any(w => w is MissileLauncher)))
            {
                var fixture = new LaunchTargetFixture();
                IEnumerator prepare = PrepareLaunchTarget(ship, station, target, fixture, "bank_" + station.Number);
                while (prepare.MoveNext()) yield return prepare.Current;
                int before = station.GetAmmoLoaded();
                var existing = new HashSet<PersistentID>(UnitRegistry.allUnits.OfType<Missile>().Select(m => m.persistentID));
                float firedAt = Time.realtimeSinceStartup;
                Unit launchTarget = fixture.Target;
                NaturalWeaponsTrial.Know(ship.NetworkHQ, launchTarget);
                fixture.Evidence["precommandGate"] = LaunchGateEvidence(ship, station, launchTarget);
                foreach (MissileLauncher launcher in station.Weapons.OfType<MissileLauncher>())
                    hatchProbes.Add(AnimationTrial.Begin(launcher));
                station.Fire(ship, launchTarget);
                var spawned = UnitRegistry.allUnits.OfType<Missile>().Where(m => !existing.Contains(m.persistentID) && m.ownerID == ship.persistentID).ToList();
                if (spawned.Count == 0)
                {
                    yield return null;
                    spawned = UnitRegistry.allUnits.OfType<Missile>().Where(m => !existing.Contains(m.persistentID) && m.ownerID == ship.persistentID).ToList();
                }
                int after = station.GetAmmoLoaded();
                launches.Add(new Dictionary<string, object>
                { ["station"] = station.Number, ["weapon"] = station.WeaponInfo.weaponName,
                  ["targetType"] = launchTarget.definition.jsonKey,
                  ["legalTargetFixture"] = fixture.Evidence,
                  ["ballisticClassification"] = NaturalBallisticSelection.IsBastion(station.WeaponInfo) ? NaturalBallisticSelection.CaptureClassification(launchTarget) : null,
                  ["ammoBefore"] = before, ["ammoAfter"] = after,
                  ["projectiles"] = spawned.Select(m => new Dictionary<string, object>
                    { ["type"] = m.definition.jsonKey, ["ownerCorrect"] = m.ownerID == ship.persistentID,
                      ["spawnTargetArgumentCorrect"] = observedMissiles.ContainsKey(m) && observedMissiles[m].requestedTarget == launchTarget.persistentID,
                      ["seekerRetainedTarget"] = m.targetID == launchTarget.persistentID, ["seekerTargetId"] = m.targetID.ToString(),
                      ["localPosition"] = V(m.transform.position), ["globalPosition"] = V(m.GlobalPosition().AsVector3()),
                      ["launchGlobalPosition"] = observedMissiles.ContainsKey(m) ? V(observedMissiles[m].globalPosition) : null,
                      ["launchPositionInShip"] = observedMissiles.ContainsKey(m) ? V(observedMissiles[m].positionInShip) : null,
                      ["launchAboveSeaMetres"] = observedMissiles.ContainsKey(m) ? observedMissiles[m].aboveSea : -999,
                      ["launchWithinShipBounds"] = observedMissiles.ContainsKey(m) && observedMissiles[m].withinShipBounds,
                      ["velocity"] = V(m.rb.velocity) }).ToList(), ["elapsed"] = Time.realtimeSinceStartup - firedAt });
                Save(output, report);
                Require(checks, "missile-station-" + station.Number + "-launch", after < before && spawned.Count > 0 &&
                    spawned.All(m => observedMissiles.ContainsKey(m) && observedMissiles[m].requestedTarget == launchTarget.persistentID &&
                        observedMissiles[m].requestedOwner == ship.persistentID && observedMissiles[m].aboveSea > 0 &&
                        observedMissiles[m].withinShipBounds && Finite(m.transform.position) && Finite(m.rb.velocity)));
                // Keep both target and owner alive through native StartMissile,
                // then release actual seeker reservations before the next bank.
                yield return new WaitForFixedUpdate(); yield return null;
                foreach (Missile missile in spawned) NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                fixture.Retire();
            }
            Require(checks, "missile-batteries-tested", launches.Count > 0);
            IEnumerator hatchProof = AnimationTrial.Observe(hatchProbes, report, checks, output);
            while (hatchProof.MoveNext()) yield return hatchProof.Current;

            Gun[] guns = ship.weaponStations.SelectMany(s => s.Weapons).OfType<Gun>().Distinct().ToArray();
            Require(checks, "five-native-guns", guns.Length == 5);
            var gunChecks = new List<object>();
            report["guns"] = gunChecks;
            foreach (WeaponStation station in ship.weaponStations.Where(s => s.Weapons.Any(w => w is Gun)))
            {
                Gun[] stationGuns = station.Weapons.OfType<Gun>().ToArray();
                var before = stationGuns.ToDictionary(g => g, g => g.GetAmmoLoaded());
                foreach (Gun gun in stationGuns) { observedBullets[gun] = 0; gun.SetTarget(target); }
                until = Time.realtimeSinceStartup + 3;
                while (stationGuns.Any(g => observedBullets[g] == 0) && Time.realtimeSinceStartup < until)
                {
                    station.Fire(ship, target);
                    // Native Gun.Update expires a trigger after two rendered
                    // loops. Keep the requested trigger held every game loop;
                    // a fast hidden process can run many Updates per physics
                    // step, so a physics-only pulse can expire before firing.
                    yield return null;
                }
                foreach (Gun gun in stationGuns)
                {
                    gunChecks.Add(new Dictionary<string, object>
                    { ["station"] = station.Number, ["mount"] = gun.GetComponentInParent<Turret>().name,
                      ["ammoBefore"] = before[gun], ["ammoAfter"] = gun.GetAmmoLoaded(), ["nativeBulletCalls"] = observedBullets[gun] });
                    Save(output, report);
                    Require(checks, "gun-" + station.Number + "-native-projectile", gun.GetAmmoLoaded() < before[gun] && observedBullets[gun] > 0);
                }
            }

            }
            if (selection == "full" || selection == "projectiles")
            {
            IEnumerator projectileTrial = NaturalWeaponsTrial.Run(ship, report, checks, output);
            while (projectileTrial.MoveNext()) yield return projectileTrial.Current;
            }

            if (selection == "full" || selection == "behavior")
            {
            IEnumerator behaviorTrial = ShipBehaviorTrial.Run(ship, report, checks, output);
            while (behaviorTrial.MoveNext()) yield return behaviorTrial.Current;
            }

            if (selection == "full" || selection == "damage")
            {
            IEnumerator damageVisualTrial = DamageVisualTrial.Run(ship, report, checks, output);
            while (damageVisualTrial.MoveNext()) yield return damageVisualTrial.Current;
            }

            report["phase"] = "native-damage"; Save(output, report);
            ShipPart part = ship.parts.OrderByDescending(p => p.hitPoints).First();
            float healthBefore = part.hitPoints;
            float requestedDamage = Mathf.Max(1, healthBefore * .10f);
            part.ApplyDamage(0, requestedDamage, 0, 0);
            report["damage"] = new Dictionary<string, object>
            { ["part"] = part.name, ["before"] = healthBefore, ["after"] = part.hitPoints,
              ["requestedBlastDamage"] = requestedDamage, ["leakDisplacement"] = part.leakToDisplacement,
              ["originalDisplacement"] = part.originalDisplacement };
            Require(checks, "native-compartment-damage", part.hitPoints < healthBefore && Mathf.Abs(healthBefore - requestedDamage - part.hitPoints) < .1f);
            Require(checks, "finite-after-damage", Finite(ship.transform.position) && Finite(ship.rb.velocity));
            Require(checks, "still-no-steam", !SteamManager.ClientInitialized && !SteamManager.ServerInitialized);
            RestoreControllers();
            report["phase"] = "complete"; Save(output, report);
            log.LogInfo("Native mission trial reached completion; assertions: " + checks.Count);
        }

        private static void IsolateBackgroundInput(Dictionary<string, object> report)
        {
            // The hidden copied-game process must not read background hotkeys.
            // Rewired has its own input backend, independent of Unity batch mode.
            // Configure it only after its prefab has completed initialization.
            bool previous = Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus;
            Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus = true;
            previousBackgroundInputSetting = previous;
            report["diagnosticInputIsolation"] = new Dictionary<string, object>
            {
                ["scope"] = "Ignore Rewired input while the copied-game diagnostic is not focused; native simulation and foreground controls are unchanged.",
                ["settingBefore"] = previousBackgroundInputSetting.Value,
                ["settingDuringTrial"] = Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus,
                ["applicationFocusedAtSetup"] = Application.isFocused,
                ["restoredAfterTrial"] = false
            };
        }

        private static void RestoreBackgroundInput(Dictionary<string, object> report)
        {
            if (!previousBackgroundInputSetting.HasValue) return;
            Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus = previousBackgroundInputSetting.Value;
            var state = (Dictionary<string, object>)report["diagnosticInputIsolation"];
            state["settingAfterTrial"] = Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus;
            state["restoredAfterTrial"] = (bool)state["settingAfterTrial"] == previousBackgroundInputSetting.Value;
            previousBackgroundInputSetting = null;
        }

        private static void InstallIsolation()
        {
            isolation = new Harmony(Plugin.Id + ".mission-trial");
            var skip = new HarmonyMethod(typeof(MissionTrial), nameof(SkipPersistence));
            isolation.Patch(AccessTools.Method(typeof(PlayerSettings), "LoadPrefs"), prefix: skip);
            isolation.Patch(AccessTools.Method(typeof(DiscordManager), "InitClient"), prefix: skip);
            isolation.Patch(AccessTools.Method(typeof(NuclearOption.MissionEditorScripts.MissionEditor), "CheckAutoSave"), prefix: skip);
            suppressedSaveMethods.Add("MissionEditor.CheckAutoSave (isolated editor benchmark)");
            suppressedSaveMethods.Add("PlayerSettings.LoadPrefs (creates missing stored Tobii defaults)");
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Rewired", StringComparison.Ordinal)))
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                foreach (Type type in types.Where(t => t.FullName.IndexOf("UserDataStore", StringComparison.Ordinal) >= 0))
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (method.Name.StartsWith("Save", StringComparison.Ordinal) && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null)
                    {
                        isolation.Patch(method, prefix: skip);
                        suppressedSaveMethods.Add(type.FullName + "." + method.Name);
                    }
            }
            isolation.Patch(AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "OnClientConnected"),
                prefix: new HarmonyMethod(typeof(MissionTrial), nameof(LocalHostAuthentication)));
            isolation.Patch(AccessTools.Method(typeof(BulletSim), "AddBullet"),
                postfix: new HarmonyMethod(typeof(MissionTrial), nameof(RecordBullet)));
            isolation.Patch(AccessTools.Method(typeof(Turret), "FixedUpdate"),
                prefix: new HarmonyMethod(typeof(MissionTrial), nameof(AllowTurretPhysics)));
            foreach (string method in new[] { "HQTargetAssessment", "DistributeTurretTargets" })
                isolation.Patch(AccessTools.Method(typeof(FireControl), method),
                    prefix: new HarmonyMethod(typeof(MissionTrial), nameof(AllowFireControl)));
            isolation.Patch(AccessTools.Method(typeof(Laser), "FixedUpdate"),
                prefix: new HarmonyMethod(typeof(MissionTrial), nameof(BeginLaserPhysics)),
                postfix: new HarmonyMethod(typeof(MissionTrial), nameof(EndLaserPhysics)));
            isolation.Patch(AccessTools.Method(typeof(UnitPart), "TakeDamage"),
                postfix: new HarmonyMethod(typeof(MissionTrial), nameof(RecordLaserDamage)));
            isolation.Patch(AccessTools.Method(typeof(Spawner), "SpawnMissile", new[]
                { typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) }),
                postfix: new HarmonyMethod(typeof(MissionTrial), nameof(RecordMissileLaunch)));
        }

        private static bool SkipPersistence() { return false; }

        private static bool LocalHostAuthentication(NetworkAuthenticatorNuclearOption __instance, INetworkPlayer player)
        {
            NetworkManagerNuclearOption network = NetworkManagerNuclearOption.i;
            if (!player.IsHost || network.Server.Listening) throw new InvalidOperationException("Trial authentication is restricted to a non-listening local host.");
            uint buildHash = (uint)AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "GetBuildHash").Invoke(null, null);
            var message = new NetworkAuthenticatorNuclearOption.AuthMessage
            { BuildHash = buildHash, JoinAs = PlayerType.DedicatedServer, SteamName = "Resolute local trial" };
            // Use the same native message and UDP/Offline authentication validation.
            // The skipped method's Steam ticket callback is irrelevant to a local host.
            AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "SendAuthentication")
                .Invoke(__instance, new object[] { network.Client, message });
            return false;
        }

        private static void RecordBullet(BulletSim __instance)
        {
            if (trialShip == null || !ReferenceEquals(AccessTools.Field(typeof(BulletSim), "owner").GetValue(__instance), trialShip)) return;
            Gun gun = (Gun)AccessTools.Field(typeof(BulletSim), "gun").GetValue(__instance);
            if (!observedBullets.ContainsKey(gun)) observedBullets[gun] = 0;
            observedBullets[gun]++;
        }

        internal static void HoldControllers(Ship ship)
        {
            if (ship == null) throw new InvalidOperationException("Native controlled-target spawn failed.");
            foreach (Behaviour behaviour in ship.GetComponentsInChildren<Behaviour>(true).Where(b => b is Turret || b is FireControl))
            {
                if (!heldControllers.ContainsKey(behaviour)) heldControllers.Add(behaviour, behaviour.enabled);
                behaviour.enabled = false;
            }
        }

        private static void RestoreControllers()
        {
            controlledWeapons = false;
            allowedDiagnosticFireControls.Clear();
            foreach (var entry in heldControllers) if (entry.Key != null) entry.Key.enabled = entry.Value;
            heldControllers.Clear();
        }

        private static bool AllowTurretPhysics(Turret __instance)
        {
            return !controlledWeapons || !(__instance.GetAttachedUnit() is Ship);
        }

        private static bool AllowFireControl(FireControl __instance)
        {
            // Native SlowUpdate callbacks ignore Behaviour.enabled. Gate only
            // controller assessment during this explicitly controlled phase.
            return !controlledWeapons || allowedDiagnosticFireControls.Contains(__instance) ||
                !(AccessTools.Field(typeof(FireControl), "attachedUnit").GetValue(__instance) is Ship);
        }

        internal static void AllowDiagnosticFireControl(FireControl controller, bool allowed)
        {
            if (controller == null) return;
            if (allowed) allowedDiagnosticFireControls.Add(controller); else allowedDiagnosticFireControls.Remove(controller);
        }

        private static void BeginLaserPhysics(Laser __instance)
        {
            activeLaserPhysics = __instance.attachedUnit == trialShip ? __instance : null;
        }

        private static void EndLaserPhysics(Laser __instance)
        {
            if (observedLaserBeams.ContainsKey(__instance))
            {
                Renderer beam = (Renderer)AccessTools.Field(typeof(Laser), "beamRenderer").GetValue(__instance);
                if (beam != null && beam.enabled) observedLaserBeams[__instance]++;
            }
            activeLaserPhysics = null;
        }

        private static void RecordLaserDamage(UnitPart __instance, PersistentID dealerID)
        {
            if (activeLaserPhysics != null && __instance.parentUnit == laserTarget && dealerID == trialShip.persistentID &&
                observedLaserDamage.ContainsKey(activeLaserPhysics)) observedLaserDamage[activeLaserPhysics]++;
        }

        private static void RecordMissileLaunch(Vector3 launchPosition, Unit target, Unit owner, Missile __result)
        {
            if (owner != trialShip || __result == null) return;
            Bounds bounds = new Bounds(owner.transform.position, Vector3.zero);
            foreach (Collider collider in owner.GetComponentsInChildren<Collider>().Where(c => c.enabled)) bounds.Encapsulate(collider.bounds);
            bounds.Expand(20f);
            Vector3 global = launchPosition.ToGlobalPosition().AsVector3();
            observedMissiles[__result] = new NativeMissileLaunch
            {
                requestedTarget = target != null ? target.persistentID : PersistentID.None,
                requestedOwner = owner.persistentID, globalPosition = global,
                positionInShip = owner.transform.InverseTransformPoint(launchPosition),
                aboveSea = global.y - Datum.SeaLevel.y, withinShipBounds = bounds.Contains(launchPosition)
            };
        }

        private static void MaintainLaserAircraft(Aircraft target, Vector3 velocity, float altitude)
        {
            if (target == null || target.disabled) return;
            target.rb.useGravity = false;
            target.rb.constraints = RigidbodyConstraints.FreezeRotation;
            target.rb.velocity = velocity; target.rb.angularVelocity = Vector3.zero;
            Vector3 position = target.rb.position; position.y = altitude; target.rb.position = position;
            foreach (Pilot pilot in target.pilots) pilot.SwitchState(null);
            target.NetworkIgnition = true;
            ControlInputs controls = target.GetInputs(); controls.throttle = .7f; controls.pitch = controls.roll = controls.yaw = 0f;
            foreach (Weapon weapon in target.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
        }

        private static Transform SelectLaserTargetPoint(Laser laser, Turret turret, Unit target, out Dictionary<string, object> report)
        {
            float minimum = (float)AccessTools.Field(typeof(Turret), "minElevation").GetValue(turret);
            float maximum = (float)AccessTools.Field(typeof(Turret), "maxElevation").GetValue(turret);
            FiringCone[] cones = (FiringCone[])AccessTools.Field(typeof(Turret), "firingCones").GetValue(turret);
            Collider selected = null;
            float selectedElevation = float.NegativeInfinity;
            foreach (UnitPart part in target.GetAllParts().Where(p => p != null && p.parentUnit == target && !p.IsDetached()).OrderBy(p => p.name))
            foreach (Collider collider in part.GetComponents<Collider>().Where(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy))
            {
                Vector3 vector = collider.bounds.center - laser.transform.position;
                float elevation = Mathf.Asin(Mathf.Clamp(Vector3.Dot(vector.normalized, turret.transform.parent.up), -1f, 1f)) * Mathf.Rad2Deg;
                Vector3 allowed;
                if (vector.y <= 0f || elevation < minimum + .25f || elevation > maximum - 1f ||
                    !FiringConeChecker.VectorWithinFiringCones(cones, vector, out allowed)) continue;
                if (elevation > selectedElevation) { selected = collider; selectedElevation = elevation; }
            }
            if (selected == null) throw new InvalidOperationException("No intact native target damage collider center lies safely above the laser's source minimum elevation.");
            Transform marker = new GameObject("ResoluteTrialLaserAim_" + selected.name).transform;
            marker.SetParent(selected.transform, true);
            marker.position = selected.bounds.center;
            report = new Dictionary<string, object> {
                ["scope"] = "Controlled native damage-collider center above the muzzle; ordinary target tracking is refreshed. This selects the test aim input without changing the source mount limits or native laser physics.",
                ["nativePart"] = selected.GetComponent<UnitPart>().name, ["collider"] = selected.name,
                ["globalPosition"] = V(marker.position.ToGlobalPosition().AsVector3()),
                ["elevationDegrees"] = selectedElevation, ["minimumElevationDegrees"] = minimum, ["maximumElevationDegrees"] = maximum,
                ["aboveMuzzleMetres"] = marker.position.y - laser.transform.position.y
            };
            return marker;
        }

        private static void RefreshLaserTargetTrack(FactionHQ hq, Unit target)
        {
            TrackingInfo information;
            if (!hq.trackingDatabase.TryGetValue(target.persistentID, out information))
                hq.trackingDatabase.Add(target.persistentID, information = new TrackingInfo(target));
            information.UpdateInfo(target.GlobalPosition());
        }

        private static Vector3 EffectiveLaserTargetPoint(Laser laser)
        {
            Transform part = (Transform)AccessTools.Field(typeof(Laser), "currentTargetTransform").GetValue(laser);
            Unit target = (Unit)AccessTools.Field(typeof(Weapon), "currentTarget").GetValue(laser);
            GlobalPosition known;
            if (target != null && !laser.attachedUnit.NetworkHQ.IsTargetBeingTracked(target) &&
                laser.attachedUnit.NetworkHQ.TryGetKnownPosition(target, out known)) return known.ToLocalPosition();
            return part != null ? part.position : laser.transform.position + laser.transform.forward * 20000f;
        }

        private static object LaserGateSnapshot(Laser laser, Turret turret, string stage, float started)
        {
            Transform part = (Transform)AccessTools.Field(typeof(Laser), "currentTargetTransform").GetValue(laser);
            Unit target = (Unit)AccessTools.Field(typeof(Weapon), "currentTarget").GetValue(laser);
            FactionHQ hq = laser.attachedUnit != null ? laser.attachedUnit.NetworkHQ : null;
            bool tracked = target != null && hq != null && hq.IsTargetBeingTracked(target);
            GlobalPosition known = default(GlobalPosition);
            bool hasKnown = target != null && hq != null && hq.TryGetKnownPosition(target, out known);
            Vector3 partPoint = part != null ? part.position : laser.transform.position + laser.transform.forward * 20000f;
            Vector3 effectivePoint = target != null && !tracked && hasKnown ? known.ToLocalPosition() : partPoint;
            Vector3 effectiveVector = effectivePoint - laser.transform.position;
            float maxAngle = (float)AccessTools.Field(typeof(Laser), "maxAngle").GetValue(laser);
            Vector3 nativeDirection = Vector3.RotateTowards(laser.transform.forward, effectiveVector, maxAngle * Mathf.Deg2Rad, 0f);
            Transform direction = (Transform)AccessTools.Field(typeof(Laser), "directionTransform").GetValue(laser);
            Transform hit = (Transform)AccessTools.Field(typeof(Laser), "hitTransform").GetValue(laser);
            Renderer beam = (Renderer)AccessTools.Field(typeof(Laser), "beamRenderer").GetValue(laser);
            FiringCone[] cones = (FiringCone[])AccessTools.Field(typeof(Turret), "firingCones").GetValue(turret);
            WeaponStation station = trialShip.weaponStations.Single(s => s.Weapons.Contains(laser));
            Vector3 allowed;
            bool permitted = FiringConeChecker.VectorWithinFiringCones(cones, effectiveVector, out allowed);
            var result = new Dictionary<string, object>
            {
                ["stage"] = stage, ["elapsed"] = Time.realtimeSinceStartup - started,
                ["frame"] = Time.frameCount, ["timeSinceLevelLoad"] = Time.timeSinceLevelLoad,
                ["fixedTime"] = Time.fixedTime, ["fixedDeltaTime"] = Time.fixedDeltaTime,
                ["deltaTime"] = Time.deltaTime, ["stationAmmo"] = station.Ammo, ["stationAmmoLoaded"] = station.GetAmmoLoaded(),
                ["enabled"] = laser.enabled, ["attached"] = laser.IsAttached(),
                ["ownerCorrect"] = laser.attachedUnit == trialShip,
                ["targetCorrect"] = target == laserTarget, ["targetDisabled"] = target != null && target.disabled,
                ["part"] = part != null ? part.name : null, ["partPosition"] = V(partPoint),
                ["tracked"] = tracked, ["hasKnownPosition"] = hasKnown,
                ["knownPosition"] = hasKnown ? V(known.ToLocalPosition()) : null,
                ["effectivePosition"] = V(effectivePoint), ["partToEffectiveMetres"] = Vector3.Distance(partPoint, effectivePoint),
                ["laserPosition"] = V(laser.transform.position), ["laserForward"] = V(laser.transform.forward),
                ["angleToPart"] = Vector3.Angle(laser.transform.forward, partPoint - laser.transform.position),
                ["angleToEffective"] = Vector3.Angle(laser.transform.forward, effectiveVector),
                ["maxAngle"] = maxAngle, ["nativeResidualAngle"] = Vector3.Angle(nativeDirection, effectiveVector),
                ["actualDirectionResidualAngle"] = direction != null ? Vector3.Angle(direction.forward, effectiveVector) : -1f,
                ["firingConePermitsEffective"] = permitted, ["firingConeCorrectionDegrees"] = Vector3.Angle(effectiveVector, allowed),
                ["beamEnabled"] = beam != null && beam.enabled, ["lastHit"] = hit != null ? hit.name : null,
                ["shipSpeed"] = trialShip != null ? trialShip.rb.velocity.magnitude : -1f
            };
            foreach (string field in new[] { "fireCommanded", "previousLastFired", "vehicularPowerSupply" })
                result[field] = AccessTools.Field(typeof(Laser), field).GetValue(laser);
            result["lastFired"] = AccessTools.Field(typeof(Weapon), "lastFired").GetValue(laser);
            foreach (string field in new[] { "traverseAngle", "elevationAngle", "traverseError", "elevationError", "traverseRange", "minElevation", "maxElevation", "traverseRate", "elevationRate", "onTarget" })
                result[field] = AccessTools.Field(typeof(Turret), field).GetValue(turret);
            return result;
        }

        private static GlobalPosition FindLaserTargetPosition(Ship ship, Laser laser)
        {
            float side = Mathf.Sign(ship.transform.InverseTransformPoint(laser.transform.position).x);
            Vector3 direction = Vector3.ProjectOnPlane(ship.transform.right * side, Vector3.up).normalized;
            Vector3 point = ship.GlobalPosition().AsVector3() + direction * 650f;
            point.y = Datum.SeaLevel.y + 300f;
            return new GlobalPosition(point);
        }

        private static object BuoyancySample(Ship ship, Transform sourceVisual, float elapsed)
        {
            float sourceWaterlineY = sourceVisual.position.ToGlobalPosition().y;
            return new Dictionary<string, object>
            { ["elapsedSeconds"] = elapsed, ["rootGlobalY"] = ship.GlobalPosition().y, ["seaLevelGlobalY"] = Datum.SeaLevel.y,
              ["verticalSpeedMps"] = ship.rb.velocity.y, ["pitchDegrees"] = Mathf.DeltaAngle(0, ship.transform.eulerAngles.x),
              ["rollDegrees"] = Mathf.DeltaAngle(0, ship.transform.eulerAngles.z), ["sourceWaterlineGlobalY"] = sourceWaterlineY,
              ["sourceWaterlineAboveSeaMetres"] = sourceWaterlineY - Datum.SeaLevel.y };
        }

        internal sealed class LaunchTargetFixture
        {
            internal Unit Target;
            internal bool Created;
            internal readonly Dictionary<string, object> Evidence = new Dictionary<string, object>();
            internal void Retire()
            {
                if (!Created || Target == null) return;
                Missile missile = Target as Missile;
                if (missile != null) NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                else NaturalWeaponsTrial.RetireFixtureObject(Target.gameObject, true);
            }
        }

        // Shared only by the two launch/accounting diagnostics. Production
        // role gates and native opportunity/ammunition are never overridden.
        internal static IEnumerator PrepareLaunchTarget(Ship owner, WeaponStation station, Ship fallbackSurface,
            LaunchTargetFixture fixture, string label)
        {
            string key = NaturalMissileTargeting.Key(station.WeaponInfo);
            FactionHQ hostile = FactionRegistry.HqFromName(owner.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
            TargetRequirements envelope = station.WeaponInfo.targetRequirements;
            Vector3 forward = Vector3.ProjectOnPlane(owner.transform.forward, Vector3.up).normalized;
            float range = Mathf.Min(envelope.maxRange * .6f, Mathf.Max(envelope.minRange * 2f, 6000f));
            bool ballistic = key == "rsl_bmd" || key == "rsl_bmd_exo";
            float altitude = Mathf.Min(envelope.maxAltitude - 100f, Mathf.Max(envelope.minAltitude + 200f,
                key == "rsl_bmd" ? 30000f : key == "rsl_bmd_exo" ? 60000f : 2500f));
            GlobalPosition point = owner.GlobalPosition() + forward * range;
            point.y = Datum.SeaLevel.y + altitude;
            ShipDefinition hull = Plugin.FindDonor(Encyclopedia.i);
            if (key == "rsl_ashm")
            {
                point = FindLaunchTargetWater(owner, envelope, hull, out Vector3 heading);
                fixture.Target = NetworkSceneSingleton<Spawner>.i.SpawnShip(hull.unitPrefab, point,
                    Quaternion.LookRotation(heading), hostile, "resolute_launch_ship_" + label, 1f, true);
                HoldControllers((Ship)fixture.Target); fixture.Created = true;
            }
            else if (key == "rsl_cruise")
            {
                BuildingDefinition definition = Encyclopedia.i.buildings.Where(d => d != null && d.unitPrefab != null && d.typeIdentity.surface > 0f)
                    .OrderBy(d => d.armorTier).First();
                point.y = Datum.SeaLevel.y + 20f;
                fixture.Target = NetworkSceneSingleton<Spawner>.i.SpawnBuilding(definition.unitPrefab, point,
                    Quaternion.identity, hostile, null, "resolute_launch_installation_" + label, false, null);
                fixture.Created = true;
            }
            else if (ballistic)
            {
                if (fallbackSurface == null) throw new InvalidOperationException("Ballistic launch fixture requires its live hostile owner.");
                NaturalWeaponsTrial.Know(hostile, owner);
                Missile incoming = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeaponsTrial.NativeBallistic(), point.ToLocalPosition(),
                    Quaternion.LookRotation(-forward), -forward * 700f, owner, fallbackSurface);
                NaturalWeapons.Set(incoming, "seeker", null);
                fixture.Target = incoming; fixture.Created = true;
            }
            else if (key != null)
            {
                AircraftDefinition definition = NaturalWeaponsTrial.NativeAircraft();
                Vector3 velocity = Vector3.Cross(Vector3.up, forward) * 150f;
                fixture.Target = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, definition.unitPrefab,
                    definition.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), point, Quaternion.LookRotation(velocity),
                    velocity, null, hostile, "resolute_launch_aircraft_" + label, 1f, 1f);
                fixture.Created = true;
            }
            else fixture.Target = fallbackSurface;
            if (fixture.Target == null) throw new InvalidOperationException("No native target for launch fixture " + label);
            float readyAt = Time.time + (fixture.Created ? 2f : .1f);
            foreach (MissileLauncher launcher in station.Weapons.OfType<MissileLauncher>())
            {
                float remaining = NaturalArmament.Get<float>(launcher, "fireInterval") -
                    (Time.timeSinceLevelLoad - NaturalArmament.Get<float>(launcher, "lastFired"));
                readyAt = Mathf.Max(readyAt, Time.time + Mathf.Max(0f, remaining) + .1f);
            }
            while (Time.time < readyAt)
            {
                Aircraft aircraft = fixture.Target as Aircraft;
                if (aircraft != null)
                {
                    foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                    aircraft.NetworkIgnition = true; aircraft.GetInputs().throttle = .7f;
                    foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
                }
                NaturalWeaponsTrial.Know(owner.NetworkHQ, fixture.Target);
                yield return new WaitForFixedUpdate();
            }
            Physics.SyncTransforms(); fixture.Target.CheckRadarAlt();
            NaturalWeaponsTrial.Know(owner.NetworkHQ, fixture.Target);
            fixture.Evidence["scope"] = "Native launch/accounting fixture, not autonomous acquisition or a flight-hit test. Fresh role-appropriate registered target for each governed station; Pike uses a checked deep-water ship, Spear a fixed installation at sea+20m (controlled placement, no terrain-hit claim), T/X a real ballistic definition with fixture guidance disabled at native thin-air altitudes (30/60km respectively; T remains within its 25nm slant range), air defense a warmed native Multirole1. Aircraft pilot commands are held and ignition/throttle requested; target rigidbodies, IR source registration, radar altitude, health and motion remain native. Tracks are explicitly refreshed. Real launcher cooldown is awaited, with no gate, opportunity, demand-counter or ammunition override.";
            fixture.Evidence["weaponKey"] = key; fixture.Evidence["targetId"] = fixture.Target.persistentID.ToString();
            fixture.Evidence["targetType"] = fixture.Target.definition.jsonKey;
            fixture.Evidence["targetPosition"] = V(fixture.Target.GlobalPosition().AsVector3());
            fixture.Evidence["radarAltitude"] = fixture.Target.radarAlt; fixture.Evidence["targetSpeed"] = fixture.Target.speed;
            fixture.Evidence["nativeIrSourceListPresent"] = fixture.Target.HasIRSignature();
            fixture.Evidence["ballisticClassification"] = ballistic ? NaturalBallisticSelection.CaptureClassification(fixture.Target) : null;
        }

        internal static object LaunchGateEvidence(Ship owner, WeaponStation station, Unit target)
        {
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            return new Dictionary<string, object> { ["targetLiveHostile"] = !target.disabled && target.NetworkHQ != owner.NetworkHQ,
                ["knownPositionAccurate100m"] = owner.NetworkHQ.IsTargetPositionAccurate(target, 100f),
                ["nativeMissileAttacks"] = track != null ? (object)track.missileAttacks : null,
                ["nativeAttacksNeeded"] = station.WeaponInfo.CalcAttacksNeeded(target),
                ["nativeOpportunity"] = track != null ? (object)CombatAI.AnalyzeTarget(station, owner, track).opportunity : null,
                ["launchers"] = station.Weapons.OfType<MissileLauncher>().Select(launcher => {
                    bool allowed = NaturalMissileTargeting.CanLaunch(launcher, station, owner, target, out string reason);
                    NaturalMissileTargeting.TryLaunchPosition(launcher, out Vector3 position);
                    return new Dictionary<string, object> { ["name"] = launcher.name, ["allowed"] = allowed, ["reason"] = reason,
                        ["rangeMetres"] = (target.GlobalPosition() - position.ToGlobalPosition()).magnitude };
                }).ToArray() };
        }

        private static GlobalPosition FindLaunchTargetWater(Ship owner, TargetRequirements envelope, ShipDefinition hull, out Vector3 heading)
        {
            foreach (var road in NetworkSceneSingleton<LevelInfo>.i.seaLanes.roads.OrderByDescending(r => r.length))
            for (int i = 1; i < road.points.Count; i++)
            {
                Vector3 a = road.points[i - 1].AsVector3(), b = road.points[i].AsVector3(); a.y = b.y = Datum.SeaLevel.y;
                float length = Vector3.Distance(a, b); Vector3 forward = (b - a).normalized, side = Vector3.Cross(Vector3.up, forward);
                for (float distance = 400f; distance < length - 400f; distance += 1000f)
                {
                    GlobalPosition point = new GlobalPosition(a + forward * distance);
                    float range = (point - owner.GlobalPosition()).magnitude;
                    if (range < Mathf.Max(2000f, envelope.minRange * 1.5f) || range > envelope.maxRange * .8f) continue;
                    if (UnitRegistry.allUnits.OfType<Ship>().Any(s => (s.GlobalPosition() - point).magnitude < 800f)) continue;
                    bool safe = true;
                    for (int x = -1; x <= 1 && safe; x++) for (int z = -1; z <= 1; z++)
                    {
                        GlobalPosition probe = point + side * x * (hull.width * .5f + 30f) + forward * z * (hull.length * .5f + 30f);
                        if (!PathfindingAgent.RaycastTerrain(probe, out var hit) || hit.point.y > Datum.LocalSeaY - 25f) { safe = false; break; }
                    }
                    if (!safe) continue;
                    point.y = Datum.SeaLevel.y + hull.spawnOffset.y; heading = forward; return point;
                }
            }
            throw new InvalidOperationException("No native deep-water launch target within the Pike envelope.");
        }

        private static void FindSeaCorridor(out GlobalPosition spawn, out GlobalPosition destination, out GlobalPosition target, out Vector3 forward)
        {
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            foreach (var road in level.seaLanes.roads.OrderByDescending(r => r.length))
            for (int i = 1; i < road.points.Count; i++)
            {
                Vector3 a = road.points[i - 1].AsVector3(), b = road.points[i].AsVector3();
                a.y = b.y = Datum.SeaLevel.y;
                float length = Vector3.Distance(a, b);
                if (length < 2000) continue;
                Vector3 direction = (b - a).normalized;
                Vector3 p = a + direction * 300, q = a + direction * Mathf.Min(length - 300, 5000);
                Vector3 side = Vector3.Cross(Vector3.up, direction) * 150;
                bool safe = true;
                for (int sample = 0; sample <= 12 && safe; sample++)
                for (int lateral = -1; lateral <= 1; lateral++)
                {
                    Vector3 point = Vector3.Lerp(p, q, sample / 12f) + side * lateral;
                    RaycastHit hit;
                    if (PathfindingAgent.RaycastTerrain(new GlobalPosition(point), out hit) && hit.point.y > Datum.LocalSeaY - 15) { safe = false; break; }
                }
                if (!safe) continue;
                spawn = new GlobalPosition(p); destination = new GlobalPosition(p + direction * 1200);
                target = new GlobalPosition(q); forward = direction; return;
            }
            throw new InvalidOperationException("No native sea-lane corridor passed the 15 m minimum-depth / 300 m width checks.");
        }

        private static void Require(List<object> checks, string name, bool condition)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static bool Finite(Vector3 value) { return !(float.IsNaN(value.x) || float.IsInfinity(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.z)); }
        private static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        private static void Save(string path, Dictionary<string, object> report) { File.WriteAllText(path, Audit.Json(report)); }
    }
}
