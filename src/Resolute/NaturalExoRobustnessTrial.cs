using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    internal static class NaturalExoRobustnessTrial
    {
        private sealed class Flight
        {
            internal string Key;
            internal Missile Target, Interceptor;
            internal bool Detonated, ReactionCaptured, Finite = true;
            internal bool ObservedDatalinkBeyondInfrared, OwnInfraredAcquired;
            internal float FirstInfraredRange = float.PositiveInfinity;
            internal int DamageCalls, Spawns;
            internal float MinimumDistance = float.PositiveInfinity, MaximumAltitude, MinimumDensity = float.PositiveInfinity;
            internal float LaunchAltitude, HealthBefore, HealthAfter;
            internal readonly List<object> Samples = new List<object>();
        }
        private static Ship owner;
        private static Flight active;
        private static float targetAltitude = 60000f;
        private static readonly MethodInfo Tick = AccessTools.Method(typeof(NaturalReactionControl), "FixedUpdate");
        private static readonly MethodInfo Jets = AccessTools.Method(typeof(NaturalReactionControl), "ShowJets");
        private static readonly FieldInfo Used = AccessTools.Field(typeof(NaturalReactionControl), "usedControlSeconds");

        internal static IEnumerator Run(Ship ship, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Exo robustness trials require the isolated test-game copy.");
            owner = ship;
            var flights = new List<object>(); var guards = new List<object>();
            var result = new Dictionary<string, object> {
                ["scope"] = "Real native WeaponStation.Fire launches from the ship to a genuine registered ballistic missile on a controlled 700 m/s crossing track at 30 km altitude for T and 60 km for X. T stays within its 25 nm automatic selection envelope. Only the target motion/track is controlled; interceptor spawn position, ejection, flight, engines, RCS, collision and damage are native. Separate component guard probes inject explicitly labelled states on disposable already-aloft projectiles; they do not count as flight/intercept or multiplayer transport evidence.",
                ["flights"] = flights, ["guardProbes"] = guards, ["success"] = false
            };
            report["exoRobustnessTrial"] = result;
            IEnumerator reference = NaturalBallisticReferenceTrial.WaitUntilReady(report, checks, output);
            while (reference.MoveNext()) yield return reference.Current;
            var hooks = new Harmony(Plugin.Id + ".exo-robustness-trial");
            hooks.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnMissile), new[] {
                typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) }),
                postfix: new HarmonyMethod(typeof(NaturalExoRobustnessTrial), nameof(Launched)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                prefix: new HarmonyMethod(typeof(NaturalExoRobustnessTrial), nameof(Detonated)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeDamage)),
                prefix: new HarmonyMethod(typeof(NaturalExoRobustnessTrial), nameof(Damaged)));
            Ship hostileLauncher = null;
            Missile target = null;
            try
            {
                MissionTrial.HoldControllers(ship);
                FactionHQ hostile = FactionRegistry.HqFromName(ship.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
                ShipDefinition hull = Plugin.FindDonor(Encyclopedia.i);
                GlobalPosition point = ship.GlobalPosition() + ship.transform.forward * 20000f;
                point.y = Datum.SeaLevel.y + hull.spawnOffset.y;
                hostileLauncher = NetworkSceneSingleton<Spawner>.i.SpawnShip(hull.unitPrefab, point, Quaternion.identity, hostile, "exo_robustness_owner", 1f, true);
                MissionTrial.HoldControllers(hostileLauncher);
                NaturalWeaponsTrial.Know(hostile, ship);
                foreach (string key in new[] { "rsl_bmd", "rsl_bmd_exo" })
                {
                    float horizontalRange = key == "rsl_bmd" ? 15000f : 80000f;
                    targetAltitude = key == "rsl_bmd" ? 30000f : 60000f;
                    Vector3 targetPoint = ship.transform.position + ship.transform.forward * horizontalRange;
                    targetPoint.y = Datum.LocalSeaY + targetAltitude;
                    Vector3 crossing = ship.transform.right * 700f;
                    target = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeaponsTrial.NativeBallistic(), targetPoint,
                        Quaternion.LookRotation(crossing), crossing, ship, hostileLauncher);
                    NaturalWeapons.Set(target, "seeker", null);
                    float warmUntil = Time.time + 2f;
                    while (Time.time < warmUntil) { Maintain(target, crossing); yield return new WaitForFixedUpdate(); }
                    Require(checks, key + "-surface-exo-genuine-hot-ballistic", NaturalBallisticSelection.IsEligible(target) && target.HasIRSignature());
                    GuardProbes(key, target, guards, checks);
                    yield return null;
                    Maintain(target, crossing);
                    WeaponStation station = ship.weaponStations.First(s => s.WeaponInfo == NaturalWeapons.Infos[key]);
                    MissileLauncher launcher = station.Weapons.OfType<MissileLauncher>().First();
                    string launchReason;
                    bool allowed = NaturalMissileTargeting.CanLaunch(launcher, station, ship, target, out launchReason);
                    var row = new Dictionary<string, object> { ["key"] = key, ["target"] = target.definition.jsonKey,
                        ["targetAltitudeMetres"] = targetAltitude, ["initialHorizontalRangeMetres"] = horizontalRange,
                        ["targetCrossingSpeedMps"] = 700f, ["launchGate"] = launchReason, ["samples"] = new List<object>() };
                    flights.Add(row); Save(output, report);
                    Require(checks, key + "-surface-exo-launch-approved", allowed);
                    CheckBatteryChoice(ship, target, key, row, checks);
                    active = new Flight { Key = key, Target = target, HealthBefore = Health(target) };
                    row["samples"] = active.Samples;
                    int ammo = launcher.ammo;
                    station.Fire(ship, target);
                    row["ammoBefore"] = ammo; row["ammoAfter"] = launcher.ammo;
                    Require(checks, key + "-surface-exo-one-native-launch", launcher.ammo == ammo - 1 && active.Spawns == 1 &&
                        active.Interceptor != null && active.Interceptor.targetID == target.persistentID && active.LaunchAltitude < 150f);
                    report["phase"] = "surface-to-exo-" + key;
                    float until = Time.time + (key == "rsl_bmd" ? 105f : 150f), sampleAt = Time.time;
                    while (Time.time < until && active.Interceptor != null && !active.Interceptor.disabled && !target.disabled)
                    {
                        Maintain(target, crossing); Observe(active);
                        CaptureReaction(active, row, output, checks);
                        if (Time.time >= sampleAt)
                        {
                            sampleAt = Time.time + .5f;
                            Missile missile = active.Interceptor;
                            NaturalReactionControl control = missile.GetComponent<NaturalReactionControl>();
                            NaturalExoInfraredSeeker infrared = missile.GetComponent<NaturalExoInfraredSeeker>();
                            active.Samples.Add(new Dictionary<string, object> { ["seconds"] = missile.timeSinceSpawn,
                                ["altitudeMetres"] = missile.GlobalPosition().y, ["speedMps"] = missile.speed,
                                ["distanceMetres"] = (target.GlobalPosition() - missile.GlobalPosition()).magnitude,
                                ["airDensity"] = missile.airDensity, ["rcsState"] = control.ControlState,
                                ["guidancePhase"] = infrared != null ? infrared.GuidancePhase : "native-radar",
                                ["ownInfraredAcquired"] = infrared != null && infrared.OwnSeekerAcquired,
                                ["ownInfraredSourceRangeMetres"] = infrared != null && infrared.OwnSeekerAcquired ? (object)InfraredSourceRange(infrared) : null,
                                ["controlSecondsUsed"] = control.UsedControlSeconds, ["controlSecondsRemaining"] = control.RemainingControlSeconds,
                                ["accelerationMps2"] = control.LastAcceleration.magnitude, ["angularAcceleration"] = control.LastAngularAcceleration.magnitude });
                        }
                        yield return new WaitForFixedUpdate();
                    }
                    Observe(active);
                    float damageUntil = Time.time + .5f;
                    while (Time.time < damageUntil) yield return new WaitForFixedUpdate();
                    active.HealthAfter = Health(target);
                    NaturalReactionControl finalControl = active.Interceptor != null ? active.Interceptor.GetComponent<NaturalReactionControl>() : null;
                    row["launchAltitudeMetres"] = active.LaunchAltitude;
                    row["maximumAltitudeMetres"] = active.MaximumAltitude;
                    row["minimumAirDensity"] = active.MinimumDensity;
                    row["minimumTargetDistanceMetres"] = active.MinimumDistance;
                    row["nativeDetonation"] = active.Detonated; row["nativeDamageCalls"] = active.DamageCalls;
                    row["targetHealthBefore"] = active.HealthBefore; row["targetHealthAfter"] = active.HealthAfter;
                    row["finiteFlight"] = active.Finite;
                    row["controlSecondsUsed"] = finalControl != null ? finalControl.UsedControlSeconds : 0f;
                    row["controlSecondsConfigured"] = finalControl != null ? finalControl.ControlSeconds : 0f;
                    row["rcsActiveSeconds"] = finalControl != null ? finalControl.ActiveSeconds : 0f;
                    if (key == "rsl_bmd_exo")
                    {
                        row["observedDatalinkBeyondInfrared"] = active.ObservedDatalinkBeyondInfrared;
                        row["ownInfraredAcquired"] = active.OwnInfraredAcquired;
                        row["firstInfraredSourceRangeMetres"] = Finite(active.FirstInfraredRange) ? (object)active.FirstInfraredRange : null;
                    }
                    Save(output, report);
                    Require(checks, key + "-surface-exo-physical-climb-and-finite-control", active.Finite && active.MaximumAltitude > targetAltitude - 10000f &&
                        active.MinimumDensity < .02f && finalControl != null && finalControl.ActiveSeconds > 1f &&
                        finalControl.UsedControlSeconds <= finalControl.ControlSeconds);
                    Require(checks, key + "-surface-exo-native-rcs-images", active.ReactionCaptured);
                    if (key == "rsl_bmd_exo")
                    {
                        Require(checks, key + "-observed-datalink-before-terminal", active.ObservedDatalinkBeyondInfrared);
                        Require(checks, key + "-own-infrared-handoff-within-ten-nm", active.OwnInfraredAcquired &&
                            active.FirstInfraredRange <= active.Interceptor.GetComponent<NaturalExoInfraredSeeker>().AcquisitionRange);
                    }
                    Require(checks, key + "-surface-exo-native-intercept", active.Detonated && active.MinimumDistance < 80f &&
                        active.DamageCalls > 0 && active.HealthAfter < active.HealthBefore);
                    NaturalWeaponsTrial.RetireMissileFixture(active.Interceptor);
                    active = null; NaturalWeaponsTrial.RetireMissileFixture(target); target = null;
                    yield return null; yield return new WaitForFixedUpdate();
                }
                IEnumerator manual = ManualLaunchChecks(ship, hostileLauncher, report, result, checks, output);
                while (manual.MoveNext()) yield return manual.Current;
                result["success"] = true; Save(output, report);
            }
            finally
            {
                if (active != null) NaturalWeaponsTrial.RetireMissileFixture(active.Interceptor, true);
                NaturalWeaponsTrial.RetireMissileFixture(target, true);
                if (hostileLauncher != null) NaturalWeaponsTrial.RetireFixtureObject(hostileLauncher.gameObject, true);
                hooks.UnpatchSelf(); active = null; owner = null;
            }
        }

        private static void CheckBatteryChoice(Ship ship, Missile target, string expected,
            Dictionary<string, object> row, List<object> checks)
        {
            var director = ship.GetComponent<ResoluteEngagementDirector>();
            var router = ship.GetComponent<NaturalFireControlGroups>();
            MethodInfo choose = AccessTools.Method(typeof(ResoluteEngagementDirector), "ChooseBattery");
            TrackingInfo track = ship.NetworkHQ.GetTrackingData(target.persistentID);
            object[] args = { target, track, router.ControllerFor(NaturalWeapons.Infos[expected]) };
            object choice = choose.Invoke(director, args);
            string chosen = choice != null ? (string)AccessTools.Field(choice.GetType(), "Key").GetValue(choice) : null;
            row["automaticBattery"] = chosen;
            Require(checks, expected + "-dedicated-ballistic-battery-preferred", chosen == expected);
            if (expected != "rsl_bmd_exo") return;
            // Inventory is the explicit fixture here. Keep all native scoring,
            // phase gates and the conventional fallback battery untouched.
            Weapon[] depleted = ship.weaponStations.Where(s => s.WeaponInfo == NaturalWeapons.Infos[expected])
                .SelectMany(s => s.Weapons).ToArray();
            int[] ammo = depleted.Select(w => w.ammo).ToArray();
            try
            {
                foreach (Weapon weapon in depleted) weapon.ammo = 0;
                choice = choose.Invoke(director, args);
                chosen = choice != null ? (string)AccessTools.Field(choice.GetType(), "Key").GetValue(choice) : null;
                row["automaticBatteryWhenXEmpty"] = chosen;
                Require(checks, "empty-X-retains-conventional-ballistic-fallback", chosen != null && chosen != expected);
            }
            finally { for (int i = 0; i < depleted.Length; i++) depleted[i].ammo = ammo[i]; }
        }

        private static IEnumerator ManualLaunchChecks(Ship ship, Ship hostileLauncher,
            Dictionary<string, object> report, Dictionary<string, object> result, List<object> checks, string output)
        {
            var manager = NetworkManagerNuclearOption.i;
            NetworkIdentity previousCharacter = manager.Server.LocalPlayer.Identity;
            GameManager.GetLocalPlayer<BasePlayer>(out BasePlayer previousLocalPlayer);
            Player player = Object.Instantiate(CarrierIntegration.Read<Player>(manager, "gamePlayerPrefab"));
            Missile target = null;
            ResoluteEngagementMode oldMode = ResoluteCommandApi.GetROE(ship);
            var rows = new List<object>(); result["manualLaunches"] = rows;
            try
            {
                manager.ServerObjectManager.ReplaceCharacter(manager.Server.LocalPlayer, player.Identity, true);
                yield return null;
                player.SetFaction(ship.NetworkHQ, true);
                Require(checks, "exo-manual-real-local-command-player", player.IsLocalPlayer && GameManager.IsLocalPlayer(player));
                string reason;
                Require(checks, "exo-manual-native-command-authority", ResoluteCommandApi.CanCommand(ship, out reason));
                foreach (string key in new[] { "rsl_bmd", "rsl_bmd_exo" })
                {
                    targetAltitude = 1000f;
                    Vector3 point = ship.transform.position + ship.transform.forward * 60000f;
                    point.y = Datum.LocalSeaY + targetAltitude;
                    Vector3 crossing = ship.transform.right * 300f;
                    target = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeaponsTrial.NativeBallistic(), point,
                        Quaternion.LookRotation(crossing), crossing, ship, hostileLauncher);
                    NaturalWeapons.Set(target, "seeker", null);
                    Maintain(target, crossing);
                    bool automatic = NaturalBallisticSelection.AutomaticAllows(NaturalWeapons.Infos[key], ship, target);
                    bool wrongType = ResoluteCommandApi.CanAttackTarget(ship, key, hostileLauncher, out reason);
                    var row = new Dictionary<string, object> { ["key"] = key,
                        ["scope"] = "Real command API and player authority, native order scheduler, VLS launch and first eight seconds of native flight. Low-altitude crossing ballistic fixture has never left atmosphere; T is also beyond 25 nm. This is launch-override evidence, not a claimed feasible intercept.",
                        ["automaticAllowed"] = automatic, ["surfaceTargetAccepted"] = wrongType,
                        ["rangeMetres"] = (target.GlobalPosition() - ship.GlobalPosition()).magnitude };
                    rows.Add(row); Save(output, report);
                    Require(checks, key + "-manual-fixture-outside-automatic-phase", !automatic);
                    Require(checks, key + "-manual-retains-ballistic-type-rule", !wrongType);
                    var definition = target.definition;
                    float value = definition.value;
                    try
                    {
                        definition.value = NaturalBallisticSelection.MinimumValue - 1f;
                        bool lowValue = ResoluteCommandApi.CanAttackTarget(ship, key, target, out reason);
                        row["manualBelowAutomaticValueAccepted"] = lowValue;
                        Require(checks, key + "-manual-cost-does-not-become-type-rule", lowValue && !NaturalBallisticSelection.IsEligible(target));
                    }
                    finally { definition.value = value; }
                    int before = ship.weaponStations.Where(s => s.WeaponInfo == NaturalWeapons.Infos[key]).Sum(s => s.Ammo);
                    active = new Flight { Key = key, Target = target };
                    bool ordered = ResoluteCommandApi.Attack(ship, key, target, 1, out reason);
                    row["orderAccepted"] = ordered; row["orderMessage"] = reason;
                    Save(output, report);
                    Require(checks, key + "-manual-outside-envelope-order-accepted", ordered);
                    float until = Time.time + 8f;
                    while (Time.time < until && active.Spawns == 0)
                    { Maintain(target, crossing); yield return new WaitForFixedUpdate(); }
                    int after = ship.weaponStations.Where(s => s.WeaponInfo == NaturalWeapons.Infos[key]).Sum(s => s.Ammo);
                    row["ammoBefore"] = before; row["ammoAfter"] = after;
                    row["spawnCount"] = active.Spawns; row["status"] = ResoluteCommandApi.GetStatus(ship);
                    Save(output, report);
                    Require(checks, key + "-manual-outside-envelope-native-launch", before - after == 1 && active.Spawns == 1 &&
                        active.Interceptor != null && active.Interceptor.targetID == target.persistentID);
                    float highest = 0f; bool finite = true;
                    until = Time.time + 8f;
                    while (Time.time < until && active.Interceptor != null && !active.Interceptor.disabled)
                    {
                        Maintain(target, crossing);
                        highest = Mathf.Max(highest, active.Interceptor.GlobalPosition().y - Datum.SeaLevel.y);
                        finite &= Finite(active.Interceptor.speed);
                        yield return new WaitForFixedUpdate();
                    }
                    row["maximumLaunchAltitudeMetres"] = highest;
                    row["liveAfterEightSeconds"] = active.Interceptor != null && !active.Interceptor.disabled;
                    Save(output, report);
                    Require(checks, key + "-manual-preserves-live-physical-launch", finite && highest > 100f && (bool)row["liveAfterEightSeconds"]);
                    ResoluteCommandApi.CeaseFire(ship, out reason);
                    NaturalWeaponsTrial.RetireMissileFixture(active.Interceptor); active = null;
                    NaturalWeaponsTrial.RetireMissileFixture(target); target = null;
                    yield return null;
                }
                IEnumerator railgun = NaturalRailgunAimTrial.Run(ship, hostileLauncher.NetworkHQ, report, result, checks, output);
                while (railgun.MoveNext()) yield return railgun.Current;
            }
            finally
            {
                ResoluteCommandApi.CeaseFire(ship, out _);
                ResoluteCommandApi.SetROE(ship, oldMode, out _);
                if (active != null) NaturalWeaponsTrial.RetireMissileFixture(active.Interceptor, true);
                active = null; NaturalWeaponsTrial.RetireMissileFixture(target, true);
                manager.ServerObjectManager.ReplaceCharacter(manager.Server.LocalPlayer, previousCharacter, true);
                GameManager.SetLocalPlayer(previousLocalPlayer);
                Object.Destroy(player.gameObject);
            }
        }

        private static void GuardProbes(string key, Missile target, List<object> rows, List<object> checks)
        {
            Vector3 position = target.transform.position - owner.transform.forward * 12000f - owner.transform.right * 900f;
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            Missile probe = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions[key], position,
                Quaternion.LookRotation(Quaternion.AngleAxis(12f, Vector3.up) * owner.transform.forward), owner.transform.forward * 1000f, target, owner);
            try
            {
                var control = probe.GetComponent<NaturalReactionControl>();
                MissileSeeker seeker = probe.GetComponent<MissileSeeker>();
                AccessTools.PropertySetter(typeof(Missile), nameof(Missile.timeSinceSpawn)).Invoke(probe, new object[] { control.SeparationSeconds + .1f }); probe.airDensity = .01f;
                NaturalWeapons.Set(seeker, "guidance", true);
                Tick.Invoke(control, null);
                Require(checks, key + "-rcs-guard-positive-command", control.LastAcceleration.sqrMagnitude > .01f && control.UsedControlSeconds > 0f);
                Require(checks, key + "-rcs-twelve-nozzle-layout", control.Jets != null && control.Jets.Length == 12 &&
                    control.JetOffsets != null && control.JetOffsets.Length == 12 &&
                    Mathf.Abs(control.JetOffsets[0].z + control.JetOffsets[4].z) < .001f);
                float centreTolerance = CentrePrecisionBound(probe.rb);
                rows.Add(new Dictionary<string, object> { ["name"] = key + "-post-separation-layout",
                    ["physicsCentre"] = new [] { probe.rb.centerOfMass.x, probe.rb.centerOfMass.y, probe.rb.centerOfMass.z },
                    ["flightCentre"] = new [] { control.FlightCentre.x, control.FlightCentre.y, control.FlightCentre.z },
                    ["centreErrorMetres"] = Vector3.Distance(probe.rb.centerOfMass, control.FlightCentre),
                    ["centreToleranceMetres"] = centreTolerance,
                    ["centreToleranceBasis"] = "Two world-coordinate float-rounding steps (shape centre construction and inverse body transform), converted to local units. The capsule's authored local centre/height retain their independent 1mm checks.",
                    ["localScale"] = V(probe.transform.localScale), ["lossyScale"] = V(probe.transform.lossyScale),
                    ["localPosition"] = V(probe.transform.position), ["rigidbodyPosition"] = V(probe.rb.position),
                    ["worldCentre"] = V(probe.rb.worldCenterOfMass), ["automaticCentre"] = probe.rb.automaticCenterOfMass,
                    ["colliders"] = probe.GetComponentsInChildren<Collider>(true).Select(c => new Dictionary<string, object> {
                        ["name"] = c.name, ["type"] = c.GetType().Name, ["enabled"] = c.enabled,
                        ["active"] = c.gameObject.activeInHierarchy, ["isTrigger"] = c.isTrigger,
                        ["attachedToMissile"] = c.attachedRigidbody == probe.rb,
                        ["position"] = V(c.transform.localPosition), ["scale"] = V(c.transform.lossyScale),
                        ["centre"] = c is CapsuleCollider capsule ? V(capsule.center) : null,
                        ["height"] = c is CapsuleCollider cap ? cap.height : 0f,
                        ["capsuleGeometry"] = c is CapsuleCollider geometry ? CapsuleGeometry(geometry) : null }).ToArray(),
                    ["flightCapsuleLength"] = control.FlightLength,
                    ["foreBankOffset"] = control.JetOffsets[0].z, ["aftBankOffset"] = control.JetOffsets[4].z,
                    ["nozzleCount"] = control.Jets.Length });
                Require(checks, key + "-remaining-stage-physical-centre", Vector3.Distance(probe.rb.centerOfMass, control.FlightCentre) <= centreTolerance &&
                    Vector3.Distance(probe.GetComponent<CapsuleCollider>().center, control.FlightCentre) < .001f &&
                    Mathf.Abs(probe.GetComponent<CapsuleCollider>().height - control.FlightLength) < .001f);

                probe.SetLocalSim(false);
                Stopped(key + "-remote-owner", control, rows, checks, "remote-simulation");
                probe.SetLocalSim(true);
                probe.SetTarget(null);
                Stopped(key + "-native-target-lost", control, rows, checks, "no-native-guidance");
                probe.SetTarget(target);
                target.disabled = true;
                try { Stopped(key + "-disabled-native-target", control, rows, checks, "no-live-hostile-native-target"); }
                finally { target.disabled = false; }
                Vector3 knownVelocity = (Vector3)NaturalWeapons.Get(seeker, "knownVel");
                try
                {
                    NaturalWeapons.Set(seeker, "knownVel", new Vector3(float.NaN, 0f, 0f));
                    Stopped(key + "-nonfinite-native-track", control, rows, checks, "invalid-flight-or-track-state");
                }
                finally { NaturalWeapons.Set(seeker, "knownVel", knownVelocity); }
                Vector3 velocity = probe.rb.velocity;
                probe.rb.velocity = Vector3.zero;
                Stopped(key + "-low-speed-jets-stop", control, rows, checks, "insufficient-speed");
                probe.rb.velocity = velocity;

                // Pure attitude command: align track/velocity exactly but keep
                // the body's 12-degree heading error. No transverse demand.
                probe.SetAimpoint(probe.GlobalPosition() + velocity.normalized * 10000f, Vector3.zero);
                NaturalWeapons.Set(seeker, "knownPos", probe.GlobalPosition() + velocity.normalized * 10000f);
                NaturalWeapons.Set(seeker, "knownVel", Vector3.zero);
                NaturalLoftGuidance loft = probe.GetComponent<NaturalLoftGuidance>();
                if (loft != null) loft.Active = false;
                float before = control.UsedControlSeconds;
                Tick.Invoke(control, null);
                rows.Add(new Dictionary<string, object> { ["name"] = key + "-pure-attitude-charged", ["transverseAcceleration"] = control.LastAcceleration.magnitude,
                    ["angularAcceleration"] = control.LastAngularAcceleration.magnitude, ["usedBefore"] = before, ["usedAfter"] = control.UsedControlSeconds });
                Require(checks, key + "-pure-attitude-consumes-control-budget", control.LastAcceleration.magnitude < .01f &&
                    control.LastAngularAcceleration.magnitude > .01f && control.UsedControlSeconds > before);
                Used.SetValue(control, control.ControlSeconds - .0001f);
                Tick.Invoke(control, null);
                Require(checks, key + "-last-control-tick-clamped", control.UsedControlSeconds <= control.ControlSeconds && control.RemainingControlSeconds < .00001f);
                Stopped(key + "-exhausted-control", control, rows, checks, "control-budget-exhausted");
            }
            finally { NaturalWeaponsTrial.RetireMissileFixture(probe, true); }
        }

        private static void Stopped(string name, NaturalReactionControl control, List<object> rows, List<object> checks, string expectedState)
        {
            Jets.Invoke(control, new object[] { Vector3.right * 10f });
            float before = control.UsedControlSeconds;
            Tick.Invoke(control, null);
            bool jetsStopped = control.Jets == null || control.Jets.All(j => j == null || !j.enabled);
            rows.Add(new Dictionary<string, object> { ["name"] = name, ["state"] = control.ControlState, ["usedBefore"] = before,
                ["usedAfter"] = control.UsedControlSeconds, ["jetsStopped"] = jetsStopped, ["finAuthority"] = control.AerodynamicAuthority });
            Require(checks, name, control.ControlState == expectedState && control.UsedControlSeconds == before &&
                control.LastAcceleration == Vector3.zero && control.LastAngularAcceleration == Vector3.zero && jetsStopped && control.AerodynamicAuthority == 1f);
        }

        private static void Maintain(Missile target, Vector3 velocity)
        {
            if (target == null || target.disabled) return;
            target.rb.useGravity = false; target.rb.constraints = RigidbodyConstraints.FreezeRotation;
            target.rb.velocity = velocity; target.rb.angularVelocity = Vector3.zero;
            Vector3 position = target.rb.position; position.y = Datum.LocalSeaY + targetAltitude; target.rb.position = position;
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
        }
        private static void Observe(Flight flight)
        {
            if (flight.Interceptor == null) return;
            Missile missile = flight.Interceptor;
            float altitude = missile.GlobalPosition().y;
            flight.MaximumAltitude = Mathf.Max(flight.MaximumAltitude, altitude);
            flight.MinimumDensity = Mathf.Min(flight.MinimumDensity, missile.airDensity);
            flight.Finite &= Finite(altitude) && Finite(missile.rb.velocity.magnitude) && Finite(missile.rb.angularVelocity.magnitude);
            if (flight.Target != null) flight.MinimumDistance = Mathf.Min(flight.MinimumDistance, (flight.Target.GlobalPosition() - missile.GlobalPosition()).magnitude);
            NaturalExoInfraredSeeker infrared = missile.GetComponent<NaturalExoInfraredSeeker>();
            if (infrared != null && flight.Target != null)
            {
                float distance = (flight.Target.GlobalPosition() - missile.GlobalPosition()).magnitude;
                flight.ObservedDatalinkBeyondInfrared |= infrared.GuidancePhase == "observed-datalink" &&
                    !infrared.OwnSeekerAcquired && distance > infrared.AcquisitionRange;
                if (!flight.OwnInfraredAcquired && infrared.OwnSeekerAcquired)
                {
                    flight.FirstInfraredRange = InfraredSourceRange(infrared);
                    flight.OwnInfraredAcquired = true;
                }
            }
        }
        private static float InfraredSourceRange(NaturalExoInfraredSeeker seeker)
        {
            IRSource source = NaturalWeapons.Get(seeker, "IRTarget") as IRSource;
            return source != null && source.transform != null ?
                Vector3.Distance(seeker.transform.position, source.transform.position) : float.PositiveInfinity;
        }
        private static void CaptureReaction(Flight flight, Dictionary<string, object> row, string output, List<object> checks)
        {
            if (flight.ReactionCaptured || flight.Interceptor == null) return;
            Missile missile = flight.Interceptor;
            NaturalReactionControl control = missile.GetComponent<NaturalReactionControl>();
            NaturalWeaponPhase phase = missile.GetComponent<NaturalWeaponPhase>();
            // Only real flight commands and visible nozzle effects can satisfy
            // image readiness. No artificial ShowJets or renderer activation.
            if (control == null || phase == null || !phase.FlightModelVisible || control.ControlState != "active" ||
                control.ActiveSeconds < .3f || control.Jets == null || !control.Jets.Any(p => p != null && p.enabled)) return;
            Vector3 focus = missile.transform.TransformPoint(control.FlightCentre);
            string side = NaturalWeaponAudit.CaptureFrame(missile.transform, control.FlightLength, output,
                "native_" + flight.Key + "_separated_rcs_side", 0, focus);
            string above = NaturalWeaponAudit.CaptureFrame(missile.transform, control.FlightLength, output,
                "native_" + flight.Key + "_separated_rcs_above", 1, focus);
            string opposite = null;
            var cameraFrame = new GameObject("RCS opposite-side diagnostic camera frame");
            try
            {
                cameraFrame.transform.SetPositionAndRotation(missile.transform.position,
                    missile.transform.rotation * Quaternion.AngleAxis(180f, Vector3.up));
                opposite = NaturalWeaponAudit.CaptureFrame(cameraFrame.transform, control.FlightLength, output,
                    "native_" + flight.Key + "_separated_rcs_opposite_side", 0, focus);
            }
            finally { Object.Destroy(cameraFrame); }
            flight.ReactionCaptured = !string.IsNullOrEmpty(side) && !string.IsNullOrEmpty(above) && !string.IsNullOrEmpty(opposite);
            bool localAxis = true, fireMaskMatches = true, sharedResources = true;
            var geometry = new List<object>();
            Mesh sharedMesh = control.Jets[0].GetComponent<MeshFilter>().sharedMesh;
            Material sharedMaterial = control.Jets[0].sharedMaterial;
            Vector3 localExhaust = control.transform.InverseTransformDirection(-control.LastAcceleration);
            Vector3 localAngular = control.transform.InverseTransformDirection(control.LastAngularAcceleration);
            for (int n = 0; n < control.Jets.Length; n++)
            {
                MeshRenderer jet = control.Jets[n];
                Mesh mesh = jet.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] vertices = mesh.vertices; Color[] colors = mesh.colors;
                float axisError = Vector3.Angle(jet.transform.localRotation * Vector3.forward, control.JetDirections[n]);
                float originError = (jet.transform.localPosition - control.FlightCentre - control.JetOffsets[n]).magnitude;
                bool endFades = true, edgeFades = true, turnsWhite = true, gentlyExpands = true;
                float firstWidth = 0f, lastWidth = 0f;
                for (int i = 0; i < vertices.Length; i++)
                {
                    float radius = new Vector2(vertices[i].x, vertices[i].y).magnitude;
                    if (vertices[i].z > NaturalRcsPlume.Length - .001f) { endFades &= colors[i].a == 0f; lastWidth = Mathf.Max(lastWidth, radius * 2f); }
                    if (vertices[i].z < .001f) firstWidth = Mathf.Max(firstWidth, radius * 2f);
                    if (i % NaturalRcsPlume.Across == 0 || i % NaturalRcsPlume.Across == NaturalRcsPlume.Across - 1) edgeFades &= colors[i].a == 0f;
                    if (vertices[i].z >= .065f) turnsWhite &= Mathf.Abs(colors[i].r - colors[i].g) < .001f && Mathf.Abs(colors[i].r - colors[i].b) < .001f;
                    gentlyExpands &= radius * 2f <= Mathf.Lerp(NaturalRcsPlume.BaseWidth, NaturalRcsPlume.TipWidth, vertices[i].z / NaturalRcsPlume.Length) + .0001f;
                }
                Vector3 torqueAxis = Vector3.Cross(control.JetOffsets[n], -control.JetDirections[n]);
                float translation = n < 8 ? Mathf.Max(0f, Vector3.Dot(localExhaust, control.JetDirections[n]) / Mathf.Max(1f, control.MaxAcceleration)) : 0f;
                float attitude = n < 8 ? Vector3.Dot(new Vector3(localAngular.x, localAngular.y, 0f), torqueAxis.normalized) / 6f : localAngular.z * Mathf.Sign(torqueAxis.z) / 6f;
                bool expectedFire = translation + attitude > .001f;
                fireMaskMatches &= jet.enabled == expectedFire;
                sharedResources &= mesh == sharedMesh && jet.sharedMaterial == sharedMaterial;
                bool noParticleOrTrail = jet.GetComponent<ParticleSystem>() == null && jet.GetComponent<TrailRenderer>() == null;
                localAxis &= axisError < .001f && originError < .001f && jet.transform.parent == control.transform &&
                    jet.transform.localScale == Vector3.one && mesh.vertexCount == 120 && mesh.triangles.Length == 504 &&
                    Mathf.Abs(mesh.bounds.size.z - NaturalRcsPlume.Length) < .001f && endFades && edgeFades && turnsWhite && gentlyExpands &&
                    Mathf.Abs(firstWidth - NaturalRcsPlume.BaseWidth) < .0001f && Mathf.Abs(lastWidth - NaturalRcsPlume.TipWidth) < .0001f && noParticleOrTrail;
                geometry.Add(new Dictionary<string, object> { ["name"] = jet.name, ["visible"] = jet.enabled,
                    ["expectedFromActualFireMask"] = expectedFire, ["localAxisErrorDegrees"] = axisError, ["localOriginErrorMetres"] = originError,
                    ["localPosition"] = V(jet.transform.localPosition), ["vertices"] = mesh.vertexCount, ["triangles"] = mesh.triangles.Length / 3,
                    ["lengthMetres"] = mesh.bounds.size.z, ["baseWidthMetres"] = firstWidth, ["tipWidthMetres"] = lastWidth,
                    ["endFades"] = endFades, ["edgeFades"] = edgeFades, ["whiteAfter65mm"] = turnsWhite,
                    ["noParticlesOrTrails"] = noParticleOrTrail, ["material"] = CaptureMaterial(jet.sharedMaterial) });
            }
            bool additive = sharedMaterial.shader.name == "Universal Render Pipeline/Particles/Unlit" &&
                sharedMaterial.GetFloat("_SrcBlend") == (float)UnityEngine.Rendering.BlendMode.SrcAlpha &&
                sharedMaterial.GetFloat("_DstBlend") == (float)UnityEngine.Rendering.BlendMode.One && sharedMaterial.GetFloat("_ZWrite") == 0f &&
                sharedMaterial.GetFloat("_Cull") == (float)UnityEngine.Rendering.CullMode.Off;
            row["reactionCapture"] = new Dictionary<string, object> {
                ["scope"] = "Close actual native-camera renders during the real surface-launched flight after body separation. Only actual thruster firing enables the nozzle-local continuous effect; guidance is untouched. Guard probe objects are not photographed.",
                ["sideImage"] = side, ["aboveImage"] = above, ["oppositeSideImage"] = opposite, ["seconds"] = missile.timeSinceSpawn,
                ["continuousPlumeGeometry"] = geometry, ["sharedMeshAndMaterial"] = sharedResources, ["nativeAdditiveUnlit"] = additive, ["missileWorldSpeedMps"] = missile.rb.velocity.magnitude,
                ["surfacePortImplementation"] = "Shallow curved texture overlays over the unchanged opaque missile casing. Port artwork adds no body cuts or holes; dark recess pixels do not remove the underlying body mesh.",
                ["altitudeMetres"] = missile.GlobalPosition().y, ["airDensity"] = missile.airDensity,
                ["acceleration"] = V(control.LastAcceleration), ["angularAcceleration"] = V(control.LastAngularAcceleration),
                ["physicsCentre"] = V(missile.rb.centerOfMass), ["flightCentre"] = V(control.FlightCentre),
                ["surfaceMaterials"] = phase.FlightModel.GetComponentsInChildren<MeshRenderer>(true)
                    .Where(r => r.name.StartsWith("Rcs", StringComparison.OrdinalIgnoreCase))
                    .Select(r => new Dictionary<string, object> { ["name"] = r.name, ["material"] = CaptureMaterial(r.sharedMaterial) }).ToArray()
            };
            Require(checks, flight.Key + "-native-continuous-rcs-stays-nozzle-local", localAxis && sharedResources && additive);
            Require(checks, flight.Key + "-native-continuous-rcs-matches-actual-fire-mask", fireMaskMatches);
        }
        private static object CaptureMaterial(Material material)
        {
            if (material == null) return null;
            return new Dictionary<string, object> { ["name"] = material.name,
                ["shader"] = material.shader != null ? material.shader.name : "",
                ["texture"] = material.mainTexture != null ? material.mainTexture.name : "",
                ["baseColour"] = material.HasProperty("_BaseColor") ? (object)material.GetColor("_BaseColor").ToString() : null,
                ["tintColour"] = material.HasProperty("_TintColor") ? (object)material.GetColor("_TintColor").ToString() : null };
        }
        private static void Launched(Unit owner, MissileDefinition missile, Missile __result)
        {
            if (active == null || owner != NaturalExoRobustnessTrial.owner || missile.jsonKey != active.Key || __result == null) return;
            active.Spawns++; active.Interceptor = __result; active.LaunchAltitude = __result.GlobalPosition().y - Datum.SeaLevel.y;
        }
        private static void Detonated(Missile __instance)
        { if (active != null && __instance == active.Interceptor && !__instance.disabled) { Observe(active); active.Detonated = true; } }
        private static void Damaged(Missile __instance, PersistentID dealerID)
        { if (active != null && __instance == active.Target && dealerID == owner.persistentID) active.DamageCalls++; }
        private static float Health(Missile missile) => missile != null ? Mathf.Max(0f, (float)NaturalWeapons.Get(missile, "hitpoints")) : 0f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static object CapsuleGeometry(CapsuleCollider capsule)
        {
            Vector3 axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
            Vector3 scaledAxis = capsule.transform.TransformVector(axis);
            return new Dictionary<string, object> { ["axisIndex"] = capsule.direction, ["localAxis"] = V(axis),
                ["scaledWorldAxis"] = V(scaledAxis), ["axialScale"] = scaledAxis.magnitude,
                ["radius"] = capsule.radius, ["worldCentre"] = V(capsule.transform.TransformPoint(capsule.center)),
                ["worldBoundsCentre"] = V(capsule.bounds.center), ["worldBoundsSize"] = V(capsule.bounds.size) };
        }
        private static float CentrePrecisionBound(Rigidbody body)
        {
            Vector3 position = body.position, centre = body.worldCenterOfMass, scale = body.transform.lossyScale;
            Vector3 steps = new Vector3(FloatSpacing(Mathf.Max(Mathf.Abs(position.x), Mathf.Abs(centre.x))),
                FloatSpacing(Mathf.Max(Mathf.Abs(position.y), Mathf.Abs(centre.y))),
                FloatSpacing(Mathf.Max(Mathf.Abs(position.z), Mathf.Abs(centre.z))));
            float minimumScale = Mathf.Max(.000001f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
            return Mathf.Max(.00001f, 2f * steps.magnitude / minimumScale);
        }
        private static float FloatSpacing(float magnitude)
        {
            if (!Finite(magnitude)) return 0f;
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(Mathf.Abs(magnitude)), 0);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0) - Mathf.Abs(magnitude);
        }
        private static void Require(List<object> checks, string name, bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("Exo robustness trial failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report)
        { File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented)); }
    }
}
