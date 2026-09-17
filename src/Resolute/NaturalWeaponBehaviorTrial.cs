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
    // A copied-game fixture that releases one actual native battery controller.
    // Only target motion/tracks are controlled; assessment, planning, ammo and
    // launches run through the native asynchronous FireControl implementation.
    internal static class NaturalWeaponBehaviorTrial
    {
        private static FireControl activeController;
        private static Ship owner;
        private static int assessments;
        private static readonly HashSet<PersistentID> considered = new HashSet<PersistentID>();
        private static readonly List<Dictionary<string, object>> receipts = new List<Dictionary<string, object>>();
        private static readonly List<Unit> targets = new List<Unit>();
        private static readonly Dictionary<Unit, Vector3> velocities = new Dictionary<Unit, Vector3>();

        internal static IEnumerator Run(Ship ship, HashSet<string> requested, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!requested.Contains("rsl_bmd") && !requested.Contains("rsl_bmd_exo")) yield break;
            owner = ship;
            var result = new Dictionary<string, object> {
                ["scope"] = "Actual native HQ target assessment, salvo planning and VLS launches with normal ammunition. Known hostile tracks and target motion are diagnostic inputs. No manual assessment, target choice, station fire or forced salvo is used.",
                ["valueThreshold"] = NaturalBallisticSelection.MinimumValue,
                ["referenceMechanic"] = "Aryx Zenith nuclear SAM minimum-value gate plus a positive ballistic-guidance requirement.",
                ["cases"] = new List<object>()
            };
            report["ballisticSelectionTrial"] = result;
            var hooks = new Harmony(Plugin.Id + ".ballistic-selection-trial");
            hooks.Patch(AccessTools.Method(typeof(FireControl), "HQTargetAssessment"),
                postfix: new HarmonyMethod(typeof(NaturalWeaponBehaviorTrial), nameof(Assessed)));
            hooks.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnMissile), new[] {
                typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) }),
                postfix: new HarmonyMethod(typeof(NaturalWeaponBehaviorTrial), nameof(Launched)));
            Ship hostileLauncher = null;
            try
            {
                NaturalFireControlGroups routing = ship.GetComponent<NaturalFireControlGroups>();
                result["controllerInitialization"] = NaturalFireControlAudit.CaptureLiveState(ship);
                Save(output, report);
                Require(checks, "ballistic-native-controller-groups-present", routing != null && routing.RegisteredStations > 0);
                FactionHQ hostile = FactionRegistry.HqFromName(ship.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
                ShipDefinition hull = Plugin.FindDonor(Encyclopedia.i);
                GlobalPosition launcherPoint = ship.GlobalPosition() + ship.transform.forward * 40000f;
                launcherPoint.y = Datum.SeaLevel.y + hull.spawnOffset.y;
                hostileLauncher = NetworkSceneSingleton<Spawner>.i.SpawnShip(hull.unitPrefab, launcherPoint,
                    Quaternion.identity, hostile, "rsl_ballistic_fixture_launcher", 1f, true);
                MissionTrial.HoldControllers(hostileLauncher);
                NaturalWeaponsTrial.Know(hostile, ship);
                AircraftDefinition jet = NaturalWeaponsTrial.NativeAircraft();
                AircraftDefinition helicopter = Encyclopedia.i.aircraft.FirstOrDefault(d => d != null && d.unitPrefab != null &&
                    d.aircraftParameters != null && d.aircraftParameters.verticalLanding && d.aircraftParameters.maxSpeed < 130f && d.aircraftParameters.loadouts.Count > 0);
                AddAircraft(jet, hostile, 0);
                if (helicopter != null) AddAircraft(helicopter, hostile, 1);
                MissileDefinition ordinary = Encyclopedia.i.missiles.First(d => d != null && d.unitPrefab != null && !d.jsonKey.StartsWith("rsl_") &&
                    d.unitPrefab.GetComponent<ARHSeeker>() != null && !NaturalBallisticSelection.IsEligibleDefinition(d));
                AddMissile(ordinary, hostileLauncher, 2, false);
                MissileDefinition costly = Encyclopedia.i.missiles.Where(d => d != null && d.unitPrefab != null && !d.jsonKey.StartsWith("rsl_") &&
                    d.value >= NaturalBallisticSelection.MinimumValue && !NaturalBallisticSelection.IsEligibleDefinition(d) &&
                    d.unitPrefab.GetComponent<Missile>() != null).OrderBy(d => d.value).FirstOrDefault();
                if (costly != null && costly != ordinary) AddMissile(costly, hostileLauncher, 3, false);
                yield return null; yield return null;
                result["rejectedContacts"] = targets.Select(NaturalBallisticSelection.CaptureClassification).ToArray();
                foreach (string key in new[] { "rsl_bmd", "rsl_bmd_exo" })
                {
                    if (!requested.Contains(key)) continue;
                    WeaponInfo info = NaturalWeapons.Infos[key];
                    FireControl controller = routing.ControllerFor(info);
                    WeaponStation[] stations = ship.weaponStations.Where(s => s.WeaponInfo == info).ToArray();
                    Require(checks, key + "-native-stations-grouped-by-ammunition", controller != null && stations.Length > 0 &&
                        ((List<WeaponStation>)NaturalWeapons.Get(controller, "subscribedWeaponStations")).SequenceEqual(stations));
                    assessments = 0; considered.Clear(); receipts.Clear();
                    activeController = controller;
                    int ammoBefore = stations.Sum(s => s.GetAmmoLoaded());
                    MissionTrial.AllowDiagnosticFireControl(controller, true);
                    float until = Time.time + 4f;
                    while (Time.time < until) { Maintain(); yield return new WaitForFixedUpdate(); }
                    int negativeAssessments = assessments, negativeAmmo = stations.Sum(s => s.GetAmmoLoaded());
                    var negativeSeen = considered.ToArray();
                    var negativeReceipts = receipts.ToArray();
                    result["negativeFixtureState-" + key] = targets.Select(t => new Dictionary<string, object> {
                        ["definition"] = t != null ? t.definition.jsonKey : "destroyed",
                        ["disabled"] = t == null || t.disabled,
                        ["hasHQ"] = t != null && t.NetworkHQ != null,
                        ["detachedParts"] = t != null ? t.GetAllParts().Count(p => p != null && p.IsDetached()) : 0,
                        ["partHealthTotal"] = t != null ? t.GetAllParts().Where(p => p != null).Sum(p => p.hitPoints) : 0f
                    }).ToArray();
                    Require(checks, key + "-native-ai-negative-fixtures-live", targets.All(t => t != null && !t.disabled &&
                        t.NetworkHQ != null && t.NetworkHQ != ship.NetworkHQ));
                    Require(checks, key + "-native-ai-rejects-ordinary-contacts", negativeAssessments > 0 &&
                        targets.All(t => negativeSeen.Contains(t.persistentID)) && negativeAmmo == ammoBefore && negativeReceipts.Length == 0);
                    MissionTrial.AllowDiagnosticFireControl(controller, false);
                    Missile incoming = AddMissile(NaturalWeaponsTrial.NativeBallistic(), hostileLauncher, 4, true);
                    var eligible = NaturalBallisticSelection.CaptureClassification(incoming);
                    Require(checks, key + "-positive-target-is-valuable-ballistic", NaturalBallisticSelection.IsEligible(incoming));
                    until = Time.time + 2f;
                    while (Time.time < until) { Maintain(); yield return new WaitForFixedUpdate(); }
                    MissionTrial.AllowDiagnosticFireControl(controller, true);
                    until = Time.time + 8f;
                    while (Time.time < until && receipts.Count == 0) { Maintain(); yield return new WaitForFixedUpdate(); }
                    MissionTrial.AllowDiagnosticFireControl(controller, false);
                    int ammoAfter = stations.Sum(s => s.GetAmmoLoaded());
                    bool correct = receipts.Count > 0 && receipts.All(r => (string)r["definition"] == key &&
                        (string)r["targetId"] == incoming.persistentID.ToString() && (bool)r["retainedTarget"]);
                    ((List<object>)result["cases"]).Add(new Dictionary<string, object> {
                        ["key"] = key, ["nativeAssessmentCalls"] = assessments,
                        ["negativeAssessmentCalls"] = negativeAssessments, ["negativeContactsSeen"] = negativeSeen.Select(id => id.ToString()).ToArray(),
                        ["ammoBefore"] = ammoBefore, ["ammoAfterNegativeContacts"] = negativeAmmo, ["ammoAfterBallisticContact"] = ammoAfter,
                        ["negativeLaunches"] = negativeReceipts, ["nativeLaunches"] = receipts.ToArray(), ["ballisticTarget"] = eligible,
                        ["stationCount"] = stations.Length
                    });
                    Save(output, report);
                    Require(checks, key + "-native-ai-launches-at-valuable-ballistic-only", correct && ammoAfter < negativeAmmo);
                    targets.Remove(incoming); velocities.Remove(incoming);
                    NaturalWeaponsTrial.RetireMissileFixture(incoming, true);
                    foreach (Missile missile in UnitRegistry.allUnits.OfType<Missile>().Where(m => m.ownerID == ship.persistentID).ToArray())
                        NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                    yield return null;
                    activeController = null;
                }
                if (Environment.GetCommandLineArgs().Contains("--resolute-missile-visual-review"))
                {
                    // Each motor shot receives fresh native target state. Prior
                    // selection targets and hits cannot invalidate a later shot.
                    foreach (Unit previous in targets)
                        if (previous is Missile) NaturalWeaponsTrial.RetireMissileFixture((Missile)previous, true);
                        else if (previous != null) NaturalWeaponsTrial.RetireFixtureObject(previous.gameObject, true);
                    targets.Clear(); velocities.Clear();
                    result["motorTargetFixtureScope"] = "A fresh native target is created and warmed for each motor shot, then retired after that shot's observations and reservation cleanup. Spear uses a fixed installation, Pike a checked deep-water ship, T/X a real ballistic missile, and air weapons a native Multirole1. Aircraft spawn directly at the motor observation range/altitude (Dart8.5km/2km, other aircraft30km/3km) with a level lateral150m/s native starting velocity. Their subsequent positions, velocities, gravity and rotations remain native; only neutral pilot controls, ignition/throttle, weapon safety and HQ tracks are requested. T/X controlled course85km/20km and Spear fixed placement40km/20m retain the existing synchronized setup. No health restoration, collision/damage suppression, IR-source injection, launcher gate, ammunition or effect override. Native target and pilot health/liveness is reported before/after and at observed motor moments.";
                    IEnumerator motorReview = MissileVisualTrial.RunLauncherBurns(ship,
                        (key, station, fixture) => PrepareMotorTarget(ship, hostileLauncher, key, station, fixture),
                        requested, report, checks, output);
                    while (motorReview.MoveNext()) yield return motorReview.Current;
                }
                result["success"] = true; Save(output, report);
            }
            finally
            {
                if (activeController != null) MissionTrial.AllowDiagnosticFireControl(activeController, false);
                foreach (Missile missile in UnitRegistry.allUnits.OfType<Missile>().Where(m => m.ownerID == ship.persistentID).ToArray())
                    NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                foreach (Missile missile in targets.OfType<Missile>()) if (missile != null) missile.SetTarget(null);
                foreach (Unit target in targets) if (target != null) NaturalWeaponsTrial.RetireFixtureObject(target.gameObject, true);
                targets.Clear(); velocities.Clear(); receipts.Clear(); considered.Clear();
                if (hostileLauncher != null) NaturalWeaponsTrial.RetireFixtureObject(hostileLauncher.gameObject, true);
                hooks.UnpatchSelf(); activeController = null; owner = null;
            }
        }
        private static IEnumerator PrepareMotorTarget(Ship ship, Ship hostileLauncher, string key, WeaponStation station,
            MissileVisualTrial.MotorTargetFixture fixture)
        {
            bool ballistic = key == "rsl_bmd" || key == "rsl_bmd_exo";
            if (!ballistic && key != "rsl_ashm" && key != "rsl_cruise")
            {
                IEnumerator aircraftPreparation = PrepareMotorAircraft(ship, key, fixture);
                while (aircraftPreparation.MoveNext()) yield return aircraftPreparation.Current;
                yield break;
            }
            var prepared = new MissionTrial.LaunchTargetFixture();
            fixture.Retire = () => prepared.Retire();
            IEnumerator prepare = MissionTrial.PrepareLaunchTarget(ship, station, hostileLauncher, prepared, "motor_" + key);
            while (prepare.MoveNext()) yield return prepare.Current;
            Unit target = prepared.Target;
            fixture.Target = target;
            Vector3 forward = Vector3.ProjectOnPlane(ship.transform.forward, Vector3.up).normalized;
            Vector3 velocity = forward * (ballistic ? -700f : 100f);
            if (key != "rsl_ashm")
            {
                float range = ballistic ? 85000f : key == "rsl_cruise" ? 40000f : key == "rsl_pd" ? 8500f : 30000f;
                Vector3 point = ship.transform.position + forward * range;
                if (key == "rsl_cruise") point += ship.transform.right * 4000f;
                point.y = Datum.LocalSeaY + (ballistic ? 20000f : key == "rsl_cruise" ? 20f : key == "rsl_pd" ? 2000f : 3000f);
                MoveMotorTarget(target, point, target is Building ? Vector3.zero : velocity);
            }
            fixture.Maintain = () => {
                if (target == null || target.disabled) return;
                if (target is Missile && target.rb != null)
                {
                    target.rb.useGravity = false; target.rb.constraints = RigidbodyConstraints.FreezeRotation;
                    target.rb.velocity = velocity; target.rb.angularVelocity = Vector3.zero;
                }
                target.CheckRadarAlt(); NaturalWeaponsTrial.Know(ship.NetworkHQ, target);
            };
            // Allow native altitude/IR transforms to observe the synchronized
            // position; never write the reported altitude or source list.
            float settleUntil = Time.time + .25f;
            while (Time.time < settleUntil) { fixture.Maintain(); yield return new WaitForFixedUpdate(); }
            fixture.Evidence = new Dictionary<string, object> { ["nativeTargetCreation"] = prepared.Evidence,
                ["motorPositionAfterWarmup"] = MissileVisualTrial.V(target.GlobalPosition().AsVector3()),
                ["controlledAirborneVelocity"] = MissileVisualTrial.V(velocity),
                ["ballisticOwnerBeforeLaunch"] = ballistic ? MissileVisualTrial.TargetState(hostileLauncher) : null };
        }
        private static IEnumerator PrepareMotorAircraft(Ship ship, string key, MissileVisualTrial.MotorTargetFixture fixture)
        {
            FactionHQ hostile = FactionRegistry.HqFromName(ship.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
            AircraftDefinition definition = NaturalWeaponsTrial.NativeAircraft();
            Vector3 forward = Vector3.ProjectOnPlane(ship.transform.forward, Vector3.up).normalized;
            Vector3 startingVelocity = Vector3.Cross(Vector3.up, forward) * 150f;
            GlobalPosition point = ship.GlobalPosition() + forward * (key == "rsl_pd" ? 8500f : 30000f);
            point.y = Datum.SeaLevel.y + (key == "rsl_pd" ? 2000f : 3000f);
            Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, definition.unitPrefab,
                definition.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), point, Quaternion.LookRotation(startingVelocity),
                startingVelocity, null, hostile, "resolute_motor_aircraft_" + key, 1f, 1f);
            fixture.Target = aircraft;
            fixture.Retire = () => { if (aircraft != null) NaturalWeaponsTrial.RetireFixtureObject(aircraft.gameObject, true); };
            if (aircraft == null) throw new InvalidOperationException("Native motor aircraft spawn failed: " + key);
            fixture.Maintain = () => {
                if (aircraft == null || aircraft.disabled) return;
                foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                aircraft.NetworkIgnition = true;
                ControlInputs controls = aircraft.GetInputs();
                controls.throttle = .7f; controls.pitch = controls.roll = controls.yaw = 0f;
                foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
                aircraft.CheckRadarAlt(); NaturalWeaponsTrial.Know(ship.NetworkHQ, aircraft);
            };
            // Native pilot acceleration uses the previous physics velocity.
            // Spawning at the observation point avoids an artificial maneuver
            // during warmup; do not change bodies or the pilot's saved state.
            var evidence = new Dictionary<string, object> {
                ["scope"] = "Fresh native aircraft spawned directly at final observation position and lateral course. Two-second native warmup; only pilot controls/ignition/throttle, weapon safety and HQ tracks are requested. No post-spawn position, velocity, gravity, constraint, pilot-health or IR-registration writes.",
                ["requestedSpawnPosition"] = MissileVisualTrial.V(point.AsVector3()),
                ["nativeStartingVelocity"] = MissileVisualTrial.V(startingVelocity),
                ["aircraftPhysicsUnmodified"] = true };
            fixture.Evidence = evidence;
            float readyAt = Time.time + 2f;
            bool observedFirstFrame = false;
            while (Time.time < readyAt)
            {
                fixture.Maintain();
                yield return new WaitForFixedUpdate();
                if (!observedFirstFrame)
                {
                    evidence["targetAfterFirstNativeFrame"] = MissileVisualTrial.TargetState(aircraft);
                    observedFirstFrame = true;
                }
            }
            fixture.Maintain();
            evidence["targetAfterNativeWarmup"] = MissileVisualTrial.TargetState(aircraft);
        }
        private static void AddAircraft(AircraftDefinition definition, FactionHQ hostile, int lane)
        {
            Vector3 point = owner.transform.position + owner.transform.forward * 18000f + owner.transform.right * ((lane - 1) * 1800f);
            point.y = Datum.LocalSeaY + 3000f + lane * 500f;
            Vector3 velocity = owner.transform.right * Mathf.Min(180f, definition.aircraftParameters.maxSpeed * .7f);
            Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, definition.unitPrefab,
                definition.aircraftParameters.loadouts[0], .7f, new LiveryKey(0), point.ToGlobalPosition(),
                Quaternion.LookRotation(velocity), velocity, null, hostile, "rsl_ballistic_negative_air_" + lane, 1f, 1f);
            targets.Add(aircraft); velocities.Add(aircraft, velocity);
        }
        private static Missile AddMissile(MissileDefinition definition, Ship launcher, int lane, bool ballistic)
        {
            Vector3 point = owner.transform.position + owner.transform.forward * (ballistic ? 26000f : 18000f) + owner.transform.right * ((lane - 1) * 1800f);
            point.y = Datum.LocalSeaY + (ballistic ? 16000f : 4000f + lane * 500f);
            Vector3 velocity = -owner.transform.forward * (ballistic ? 700f : 350f);
            Missile missile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(definition, point, Quaternion.LookRotation(velocity), velocity, owner, launcher);
            NaturalWeapons.Set(missile, "seeker", null);
            targets.Add(missile); velocities.Add(missile, velocity);
            return missile;
        }
        private static void Maintain()
        {
            foreach (Unit target in targets)
            {
                if (target == null || target.disabled) continue;
                target.rb.useGravity = false; target.rb.constraints = RigidbodyConstraints.FreezeRotation;
                target.rb.velocity = velocities[target]; target.rb.angularVelocity = Vector3.zero;
                Aircraft aircraft = target as Aircraft;
                if (aircraft != null)
                {
                    foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                    aircraft.NetworkIgnition = true;
                    ControlInputs controls = aircraft.GetInputs(); controls.throttle = .7f; controls.pitch = controls.roll = controls.yaw = 0f;
                    foreach (Weapon weapon in aircraft.GetComponentsInChildren<Weapon>()) weapon.Safety = true;
                }
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            }
        }
        private static void MoveMotorTarget(Unit target, Vector3 point, Vector3 velocity)
        {
            // AeroParts can own unparented bodies and native IR transforms.
            // Capture every original body position before moving the root.
            Rigidbody[] bodies = target.GetAllParts().Where(p => p != null).Select(p => p.rb)
                .Concat(target.GetComponentsInChildren<Rigidbody>(true)).Concat(new[] { target.rb })
                .Where(body => body != null).Distinct().ToArray();
            Vector3[] positions = bodies.Select(body => body.position).ToArray();
            Vector3 delta = point - target.transform.position;
            target.transform.position = point;
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].transform.position = positions[i] + delta;
                bodies[i].position = positions[i] + delta;
                if (!bodies[i].isKinematic) { bodies[i].velocity = velocity; bodies[i].angularVelocity = Vector3.zero; }
            }
            Physics.SyncTransforms();
        }
        private static void Assessed(FireControl __instance, bool __runOriginal)
        {
            if (!__runOriginal || __instance != activeController) return;
            assessments++;
            foreach (TrackingInfo track in (List<TrackingInfo>)NaturalWeapons.Get(__instance, "allTargets")) considered.Add(track.id);
        }
        private static void Launched(MissileDefinition missile, Unit target, Unit owner, Missile __result)
        {
            if (activeController == null || owner != NaturalWeaponBehaviorTrial.owner || __result == null) return;
            receipts.Add(new Dictionary<string, object> { ["definition"] = missile.jsonKey,
                ["targetId"] = target != null ? target.persistentID.ToString() : "", ["targetType"] = target != null ? target.definition.jsonKey : "",
                ["retainedTarget"] = target != null && __result.targetID == target.persistentID, ["spawnedId"] = __result.persistentID.ToString(),
                ["time"] = Time.time, ["nativeController"] = activeController.name });
        }
        private static void Require(List<object> checks, string name, bool ok)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = ok });
            if (!ok) throw new InvalidOperationException("Native ballistic selection trial failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report)
        { File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented)); }
    }
}
