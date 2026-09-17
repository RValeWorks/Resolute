using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit controlled-input policy tests in the isolated game. These are
    // launch authorization checks, not claims of autonomous detection or hits.
    internal static class NaturalMissileTargetingTrial
    {
        private static Ship owner;
        private static readonly List<Missile> launches = new List<Missile>();
        private static readonly FieldInfo LastFired = AccessTools.Field(typeof(Weapon), "lastFired");
        private static readonly FieldInfo IRSources = AccessTools.Field(typeof(Unit), "IRSources");
        private static readonly Type QueueType = AccessTools.Inner(typeof(FireControl), "QueuedAttack");
        private static readonly MethodInfo QueueValid = AccessTools.Method(QueueType, "StillValid");
        private static readonly MethodInfo QueueCancel = AccessTools.Method(QueueType, "Cancel");
        private static readonly HashSet<Missile> frozenMissileFixtures = new HashSet<Missile>();
        private static List<object> fixtureEvents;
        private static readonly Dictionary<string, int> suppressedFixtureSlowChecks = new Dictionary<string, int>();
        private static string fixtureStage;

        internal static IEnumerator Run(Ship ship, HashSet<string> requested, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!Environment.GetCommandLineArgs().Contains("--resolute-targeting-review") &&
                !Environment.GetCommandLineArgs().Contains("--resolute-targeting-review-only")) yield break;
            owner = ship;
            frozenMissileFixtures.Clear(); suppressedFixtureSlowChecks.Clear();
            fixtureEvents = new List<object>(); fixtureStage = "creating-fixtures";
            var rows = new List<object>();
            var fixtureStates = new List<object>();
            var result = new Dictionary<string, object> {
                ["scope"] = "Controlled native targets and tracks exercise all-eight role policy, envelope boundaries, Pike/Spear fallback, native IR acquisition and queued invalidation. Blocked cases call the real launcher with its cooldown cleared and verify unchanged ammunition and zero native spawns. Positive controls require one ammunition debit and one native spawn; their physics is retired only after launch and jamming assertions, while objects survive the native deferred startup callback. Explicit native Multirole1 aircraft and other target fixtures start in separate lanes; all of each target's rigidbodies are translated together and held stationary between probes, retaining native health, colliders and engine heat. Target motion, radar altitude, IR source lists and selected track counters are explicitly configured fixture inputs; this is not autonomous detection, flight-hit or combat-balance validation.",
                ["cases"] = rows, ["fixtureStates"] = fixtureStates, ["success"] = false,
                ["frozenMissileFixtureControl"] = "Only the exact owned stationary target missiles suppress their ARH SlowChecks callback, whose native flight self-destruct policy is incompatible with deliberately frozen targets. Their native health, collision and damage callbacks remain active. Real launched missiles retain all native callbacks. Suppression counts, target ages and genuine damage/detonation events are recorded; no target is resurrected or made invulnerable.",
                ["suppressedFixtureSlowChecks"] = suppressedFixtureSlowChecks, ["fixtureEvents"] = fixtureEvents
            };
            report["missileTargetingTrial"] = result;
            report["phase"] = "missile-targeting-policy";
            var hooks = new Harmony(Plugin.Id + ".targeting-policy-trial");
            hooks.Patch(AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnMissile), new[] {
                typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Unit), typeof(Unit) }),
                postfix: new HarmonyMethod(typeof(NaturalMissileTargetingTrial), nameof(Launched)));
            hooks.Patch(AccessTools.Method(typeof(ARHSeeker), "SlowChecks"),
                prefix: new HarmonyMethod(typeof(NaturalMissileTargetingTrial), nameof(FrozenFixtureSlowChecks)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.TakeDamage)),
                prefix: new HarmonyMethod(typeof(NaturalMissileTargetingTrial), nameof(FixtureDamaged)));
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                prefix: new HarmonyMethod(typeof(NaturalMissileTargetingTrial), nameof(FixtureDetonated)));
            var fixtures = new List<Unit>();
            var savedPikeAmmo = new Dictionary<MissileLauncher, int>();
            try
            {
                FactionHQ hostile = FactionRegistry.HqFromName(ship.NetworkHQ.faction.factionName == "Boscali" ? "Primeva" : "Boscali");
                GlobalPosition point = ship.GlobalPosition() + ship.transform.forward * 30000f;
                point.y = Datum.SeaLevel.y + 20f;
                ShipDefinition hull = Plugin.FindDonor(Encyclopedia.i);
                Ship vessel = NetworkSceneSingleton<Spawner>.i.SpawnShip(hull.unitPrefab, point, Quaternion.identity, hostile, "targeting_ship", 1f, true);
                fixtures.Add(vessel); MissionTrial.HoldControllers(vessel);
                BuildingDefinition installationDefinition = Encyclopedia.i.buildings.Where(d => d != null && d.unitPrefab != null && d.typeIdentity.surface > 0f)
                    .OrderBy(d => d.armorTier).First();
                Building installation = NetworkSceneSingleton<Spawner>.i.SpawnBuilding(installationDefinition.unitPrefab, point + Vector3.right * 2000f,
                    Quaternion.identity, hostile, null, "targeting_installation", false, null);
                fixtures.Add(installation);
                VehicleDefinition vehicleDefinition = Encyclopedia.i.vehicles.First(d => d != null && d.unitPrefab != null);
                GroundVehicle ground = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(vehicleDefinition.unitPrefab, point + Vector3.right * 4000f + Vector3.up * 1000f,
                    Quaternion.identity, Vector3.zero, hostile, "targeting_ground_vehicle", 1f, true, null);
                fixtures.Add(ground);
                ground.rb.useGravity = false;
                ground.rb.constraints = RigidbodyConstraints.FreezeRotation;
                AircraftDefinition airDefinition = Encyclopedia.i.aircraft.Single(d => d != null && d.jsonKey == "Multirole1" &&
                    d.unitPrefab != null && d.aircraftParameters != null && d.aircraftParameters.loadouts.Count > 0);
                Aircraft aircraft = NetworkSceneSingleton<Spawner>.i.SpawnAircraft(null, airDefinition.unitPrefab, airDefinition.aircraftParameters.loadouts[0],
                    .7f, new LiveryKey(0), point + Vector3.right * 6000f + Vector3.up * 3000f, Quaternion.identity, Vector3.zero, null, hostile, "targeting_aircraft", 1f, 1f);
                fixtures.Add(aircraft);
                foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(null);
                aircraft.NetworkIgnition = true;
                aircraft.GetInputs().throttle = .7f;
                NaturalWeaponsTrial.Know(hostile, ship);
                MissileDefinition ordinaryDefinition = Encyclopedia.i.missiles.First(d => d != null && d.unitPrefab != null &&
                    !d.jsonKey.StartsWith("rsl_") && d.unitPrefab.GetComponent<ARHSeeker>() != null && !NaturalBallisticSelection.IsEligibleDefinition(d));
                Missile ordinary = NetworkSceneSingleton<Spawner>.i.SpawnMissile(ordinaryDefinition, point.ToLocalPosition() + Vector3.right * 9000f + Vector3.up * 3000f,
                    Quaternion.identity, Vector3.forward * 350f, ship, vessel);
                fixtures.Add(ordinary); frozenMissileFixtures.Add(ordinary); NaturalWeapons.Set(ordinary, "seeker", null);
                Missile ballistic = ordinary;
                if (requested.Contains("rsl_bmd") || requested.Contains("rsl_bmd_exo"))
                {
                    ballistic = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeaponsTrial.NativeBallistic(), point.ToLocalPosition() + Vector3.right * 12000f + Vector3.up * 10000f,
                        Quaternion.identity, Vector3.forward * 700f, ship, vessel);
                    fixtures.Add(ballistic); frozenMissileFixtures.Add(ballistic); NaturalWeapons.Set(ballistic, "seeker", null);
                }
                foreach (Unit fixture in fixtures)
                {
                    MoveFixture(fixture, fixture.transform.position, Vector3.zero);
                    NaturalWeaponsTrial.Know(ship.NetworkHQ, fixture);
                }
                float warmUntil = Time.time + 2f;
                while (Time.time < warmUntil) { foreach (Unit fixture in fixtures) NaturalWeaponsTrial.Know(ship.NetworkHQ, fixture); yield return new WaitForFixedUpdate(); }
                CheckFixtures("after-native-initialization-warmup", fixtures, fixtureStates, checks);

                foreach (string key in new[] { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd" })
                {
                    if (!requested.Contains(key)) continue;
                    fixtureStage = key + "-policy";
                    CheckFixtures(key + "-before-policy", fixtures, fixtureStates, checks);
                    WeaponStation station = ship.weaponStations.First(s => s.WeaponInfo == NaturalWeapons.Infos[key]);
                    MissileLauncher launcher = station.Weapons.OfType<MissileLauncher>().First();
                    TargetRequirements envelope = station.WeaponInfo.targetRequirements;
                    bool surface = key == "rsl_ashm" || key == "rsl_cruise";
                    bool bmd = key == "rsl_bmd" || key == "rsl_bmd_exo";
                    Unit positive = key == "rsl_ashm" ? (Unit)vessel : key == "rsl_cruise" ? installation : bmd ? ballistic : aircraft;
                    float range = Mathf.Min(envelope.maxRange * .6f, Mathf.Max(envelope.minRange * 2f, 6000f));
                    float altitude = surface ? 20f : Mathf.Max(300f, envelope.minAltitude + 100f);
                    Position(positive, launcher, range, altitude, 0f);
                    // Native HasIRSignature tests list presence only. Actual
                    // source validity is checked by the production gate below;
                    // record source locations instead of calling this heat proof.
                    if (key == "rsl_bmd_exo" || key == "rsl_pd") Require(checks, key + "-targeting-fixture-native-ir-list-present", positive.HasIRSignature());
                    Probe(rows, checks, key + "-positive-policy", station, launcher, positive, true, null, false);
                    if (!surface && !bmd)
                    {
                        Position(ordinary, launcher, range, altitude, 0f);
                        Probe(rows, checks, key + "-ordinary-missile-policy", station, launcher, ordinary, true, null, false);
                    }

                    Unit[] forbidden = surface ? (key == "rsl_ashm" ? new Unit[] { installation, ground, aircraft, ordinary } : new Unit[] { ground, aircraft, ordinary }) :
                        bmd ? new Unit[] { installation, ground, vessel, aircraft, ordinary } : new Unit[] { installation, ground, vessel };
                    foreach (Unit target in forbidden)
                    {
                        Position(target, launcher, range, altitude, 0f);
                        Probe(rows, checks, key + "-rejects-" + target.GetType().Name + "-" + target.definition.jsonKey,
                            station, launcher, target, false, "excluded-target-role", true);
                    }

                    Position(positive, launcher, envelope.minRange - 1f, altitude, 0f);
                    Probe(rows, checks, key + "-below-minimum-range", station, launcher, positive, false, "below-minimum-range", true);
                    Position(positive, launcher, envelope.maxRange + 1f, altitude, 0f);
                    Probe(rows, checks, key + "-above-maximum-range", station, launcher, positive, false, "beyond-maximum-range", true);
                    Position(positive, launcher, range, envelope.minAltitude - 1f, 0f);
                    Probe(rows, checks, key + "-below-minimum-altitude", station, launcher, positive, false, "outside-altitude-envelope", true);
                    Position(positive, launcher, range, envelope.maxAltitude + 1f, 0f);
                    Probe(rows, checks, key + "-above-maximum-altitude", station, launcher, positive, false, "outside-altitude-envelope", true);
                    Position(positive, launcher, range, altitude, envelope.maxSpeed + 1f);
                    Probe(rows, checks, key + "-above-maximum-speed", station, launcher, positive, false, "above-maximum-target-speed", true);
                    Position(positive, launcher, range, altitude, 0f);
                    TrackingInfo track = ship.NetworkHQ.GetTrackingData(positive.persistentID);
                    track.lastSpottedTime = Time.timeSinceLevelLoad - 10f;
                    track.lastKnownPosition = positive.GlobalPosition() + Vector3.right * 3000f;
                    Probe(rows, checks, key + "-stale-inaccurate-track", station, launcher, positive, false, "unusable-datalink-track", true);
                    if (NaturalWeapons.UsesNativeAirDefenseProfile(key) || key == "rsl_ashm" || key == "rsl_cruise" || key == "rsl_pd")
                    {
                        track.lastKnownPosition = positive.GlobalPosition() + Vector3.right * (key == "rsl_pd" ? 250f : 1000f);
                        Probe(rows, checks, key + "-committed-track-within-native-tolerance", station, launcher, positive,
                            true, null, false, false);
                    }
                    NaturalWeaponsTrial.Know(ship.NetworkHQ, positive);
                    sbyte priorAttacks = track.missileAttacks;
                    track.missileAttacks = (sbyte)Mathf.Clamp(Mathf.CeilToInt(station.WeaponInfo.CalcAttacksNeeded(positive)) + 1, 1, 120);
                    Probe(rows, checks, key + "-native-demand-covered", station, launcher, positive, false, "native-attack-demand-already-covered", true);
                    track.missileAttacks = priorAttacks;
                    QueueInvalidation(rows, checks, key, station, launcher, positive, range, altitude);

                    if (bmd)
                    {
                        float savedValue = positive.definition.value;
                        try
                        {
                            positive.definition.value = NaturalBallisticSelection.MinimumValue - .1f;
                            Probe(rows, checks, key + "-live-value-below-threshold", station, launcher, positive, false, "excluded-target-role", true);
                            positive.definition.value = NaturalBallisticSelection.MinimumValue;
                            Probe(rows, checks, key + "-live-value-at-threshold", station, launcher, positive, true, null, false);
                        }
                        finally { positive.definition.value = savedValue; }
                    }

                    if (key == "rsl_bmd_exo" || key == "rsl_pd")
                    {
                        var sources = (List<IRSource>)IRSources.GetValue(positive);
                        IRSource[] saved = sources.ToArray();
                        try
                        {
                            sources.Clear();
                            Probe(rows, checks, key + "-cold-target", station, launcher, positive, false, "no-native-ir-acquisition", true);
                        }
                        finally { sources.Clear(); sources.AddRange(saved); }
                        try
                        {
                            sources.Add(new IRSource(positive.transform, 1f, true));
                            if (key == "rsl_pd")
                                Probe(rows, checks, key + "-flare-does-not-block-native-launch-policy", station, launcher, positive, true, null, false);
                            else Probe(rows, checks, key + "-ambiguous-flare-acquisition", station, launcher, positive, false, "no-native-ir-acquisition", true);
                        }
                        finally { sources.Clear(); sources.AddRange(saved); }
                        // Use the native Statics layer and LineOfSight ray, not a
                        // mocked gate. Remove the occluder before positive fire.
                        GameObject obstruction = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        try
                        {
                            Vector3 launch;
                            NaturalMissileTargeting.TryLaunchPosition(launcher, out launch);
                            obstruction.name = "targeting_diagnostic_occluder";
                            obstruction.layer = PhysicsLayers.Statics;
                            obstruction.transform.position = (launch + positive.transform.position) * .5f;
                            obstruction.transform.localScale = Vector3.one * 200f;
                            Physics.SyncTransforms();
                            Probe(rows, checks, key + "-obscured-target", station, launcher, positive, false, "no-launch-line-of-sight", true);
                        }
                        finally { obstruction.SetActive(false); Object.Destroy(obstruction); Physics.SyncTransforms(); }
                    }

                    if (key == "rsl_cruise")
                    {
                        Position(vessel, launcher, 20000f, 20f, 0f);
                        Probe(rows, checks, "spear-reserves-ship-for-ready-pike", station, launcher, vessel, false, "naval-target-reserved-for-pike", true);
                        foreach (MissileLauncher pike in ship.weaponStations.Where(s => NaturalMissileTargeting.Key(s.WeaponInfo) == "rsl_ashm")
                            .SelectMany(s => s.Weapons).OfType<MissileLauncher>()) { savedPikeAmmo[pike] = pike.ammo; pike.ammo = 0; }
                        Probe(rows, checks, "spear-ship-fallback-empty-pike", station, launcher, vessel, true, null, false);
                        foreach (var entry in savedPikeAmmo) entry.Key.ammo = entry.Value;
                        savedPikeAmmo.Clear();
                        Position(vessel, launcher, NaturalWeapons.Infos["rsl_ashm"].targetRequirements.maxRange + 2000f, 20f, 0f);
                        Probe(rows, checks, "spear-ship-fallback-beyond-pike-range", station, launcher, vessel, true, null, false);
                        Position(positive, launcher, range, altitude, 0f);
                    }

                    // End each weapon's case with a real allowed launch, so a
                    // blanket no-fire patch cannot make the negative checks pass.
                    Position(positive, launcher, range, altitude, 0f);
                    Probe(rows, checks, key + "-positive-native-launch", station, launcher, positive, true, null, true);
                    HomeOnJamProbes(key, launches[launches.Count - 1], ground, key == "rsl_cruise" ? vessel : positive, rows, checks);
                    foreach (Missile missile in launches.ToArray()) NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                    fixtureStage = key + "-parked";
                    Park(fixtures);
                    yield return null; yield return new WaitForFixedUpdate();
                    CheckFixtures(key + "-after-policy", fixtures, fixtureStates, checks);
                    Save(output, report);
                }
                result["success"] = true; Save(output, report);
            }
            finally
            {
                foreach (var entry in savedPikeAmmo) if (entry.Key != null) entry.Key.ammo = entry.Value;
                foreach (Missile missile in launches) NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                foreach (Missile missile in fixtures.OfType<Missile>()) if (missile != null) missile.SetTarget(null);
                foreach (Unit fixture in fixtures) if (fixture != null) NaturalWeaponsTrial.RetireFixtureObject(fixture.gameObject, true);
                launches.Clear(); hooks.UnpatchSelf(); owner = null;
                frozenMissileFixtures.Clear(); fixtureEvents = null; fixtureStage = null;
            }
        }

        private static void Position(Unit target, MissileLauncher launcher, float range, float radarAltitude, float speed)
        {
            Vector3 point;
            if (!NaturalMissileTargeting.TryLaunchPosition(launcher, out point)) throw new InvalidOperationException("Targeting fixture launcher unavailable.");
            // Range and radar altitude are separate controlled inputs, allowing
            // independent boundary tests without impossible geometric coupling.
            Vector3 direction = target is Aircraft || target is Missile ? (owner.transform.forward + Vector3.up * .35f).normalized : owner.transform.forward;
            point += direction * range;
            MoveFixture(target, point, owner.transform.right * speed);
            target.radarAlt = radarAltitude; target.speed = speed;
            NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            Physics.SyncTransforms();
        }

        private static void Park(List<Unit> fixtures)
        {
            // Boundary probes reuse target locations within one physics step.
            // Separate them before yielding so unrelated fixture collisions do
            // not damage the next case. Native colliders/damage remain enabled.
            for (int index = 0; index < fixtures.Count; index++)
            {
                Unit target = fixtures[index];
                if (target == null) continue;
                Vector3 point = owner.transform.position + owner.transform.forward * 40000f + owner.transform.right * (index * 3000f) + Vector3.up * 20000f;
                MoveFixture(target, point, Vector3.zero);
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
            }
            Physics.SyncTransforms();
        }

        private static void MoveFixture(Unit target, Vector3 point, Vector3 velocity)
        {
            // Complex aircraft AeroParts own unparented rigidbodies. Translating
            // just the root tears native attachments when the next step runs.
            Rigidbody[] bodies = target.GetAllParts().Where(p => p != null).Select(p => p.rb)
                .Concat(target.GetComponentsInChildren<Rigidbody>(true)).Concat(new[] { target.rb })
                .Where(body => body != null).Distinct().ToArray();
            Vector3[] positions = bodies.Select(body => body.position).ToArray();
            Vector3 delta = point - target.transform.position;
            target.transform.position = point;
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody body = bodies[i];
                // Native IR sources read Transform positions immediately. An
                // unparented part's Rigidbody.position alone is not reflected
                // there until simulation; keep both views in the same fixture.
                body.transform.position = positions[i] + delta;
                body.position = positions[i] + delta;
                body.useGravity = false;
                body.constraints = RigidbodyConstraints.FreezeAll;
                if (!body.isKinematic) { body.velocity = velocity; body.angularVelocity = Vector3.zero; }
            }
            if (fixtureEvents != null && target is Aircraft)
                fixtureEvents.Add(new Dictionary<string, object> { ["event"] = "aircraft-fixture-moved",
                    ["stage"] = fixtureStage, ["definition"] = target.definition.jsonKey,
                    ["translationMetres"] = new[] { delta.x, delta.y, delta.z }, ["irSources"] = CaptureIRSources(target) });
        }

        private static void CheckFixtures(string stage, List<Unit> fixtures, List<object> states, List<object> checks)
        {
            states.Add(new Dictionary<string, object> { ["stage"] = stage,
                ["targets"] = fixtures.Select(target => new Dictionary<string, object> {
                    ["definition"] = target != null ? target.definition.jsonKey : "destroyed",
                    ["disabled"] = target == null || target.disabled,
                    ["hasHQ"] = target != null && target.NetworkHQ != null,
                    ["altitudeMetres"] = target != null ? target.GlobalPosition().y : 0f,
                    ["missileAgeSeconds"] = target is Missile ? ((Missile)target).timeSinceSpawn : (object)null,
                    ["missileEngineOn"] = target is Missile ? ((Missile)target).EngineOn() : (object)null,
                    ["irSources"] = CaptureIRSources(target),
                    ["registeredParts"] = target != null ? target.GetAllParts().Count : 0,
                    ["detachedParts"] = target != null ? target.GetAllParts().Count(p => p != null && p.IsDetached()) : 0,
                    ["partHealthTotal"] = target != null ? target.GetAllParts().Where(p => p != null).Sum(p => p.hitPoints) : 0f
                }).ToArray() });
            Require(checks, "targeting-fixtures-live-" + stage, fixtures.All(target => target != null && !target.disabled &&
                target.NetworkHQ != null && target.NetworkHQ != owner.NetworkHQ));
        }

        private static void Probe(List<object> rows, List<object> checks, string name, WeaponStation station,
            MissileLauncher launcher, Unit target, bool expected, string expectedReason, bool fire, bool? expectedSelection = null)
        {
            string reason;
            bool allowed = NaturalMissileTargeting.CanLaunch(launcher, station, owner, target, out reason);
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            float opportunity = track != null ? CombatAI.AnalyzeTarget(station, owner, track).opportunity : 0f;
            int ammo = launcher.ammo, before = launches.Count;
            if (fire)
            {
                LastFired.SetValue(launcher, Time.timeSinceLevelLoad - 10000f);
                launcher.Fire(owner, target, owner.rb.velocity, station, default(GlobalPosition));
            }
            int spawned = launches.Count - before;
            bool effect = !fire || (expected ? launcher.ammo == ammo - 1 && spawned == 1 && launches[launches.Count - 1].targetID == target.persistentID : launcher.ammo == ammo && spawned == 0);
            rows.Add(new Dictionary<string, object> { ["name"] = name, ["weapon"] = NaturalMissileTargeting.Key(station.WeaponInfo),
                ["target"] = target.definition.jsonKey, ["expectedAllowed"] = expected, ["allowed"] = allowed, ["reason"] = reason,
                ["targetDisabled"] = target.disabled, ["targetHasHQ"] = target.NetworkHQ != null,
                ["targetGlobalAltitude"] = target.GlobalPosition().y,
                ["irSources"] = CaptureIRSources(target),
                ["nativeOpportunity"] = opportunity, ["actualLauncherCalled"] = fire, ["ammoBefore"] = ammo, ["ammoAfter"] = launcher.ammo, ["nativeSpawns"] = spawned });
            // A committed shot may remain launchable after its track leaves the
            // native fresh-selection tolerance. Demand revalidation can also
            // stop a queued shot independently of the native opportunity score.
            bool selectionMatches = reason == "native-attack-demand-already-covered" ||
                ((expectedSelection ?? expected) ? opportunity > 0f : opportunity <= 0f);
            Require(checks, name, allowed == expected && (expectedReason == null || reason == expectedReason) && effect && selectionMatches);
        }

        private static void QueueInvalidation(List<object> rows, List<object> checks, string key, WeaponStation station,
            MissileLauncher launcher, Unit target, float range, float altitude)
        {
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            int before = track.attackers;
            object queued = Activator.CreateInstance(QueueType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { target, track, station }, null);
            bool wasValid = (bool)QueueValid.Invoke(queued, new object[] { owner.NetworkHQ });
            Position(target, launcher, station.WeaponInfo.targetRequirements.maxRange + 1f, altitude, 0f);
            bool remainsValid = (bool)QueueValid.Invoke(queued, new object[] { owner.NetworkHQ });
            QueueCancel.Invoke(queued, new object[] { owner });
            rows.Add(new Dictionary<string, object> { ["name"] = key + "-queued-range-change", ["validWhenQueued"] = wasValid,
                ["validAfterTargetLeavesRange"] = remainsValid, ["attackersBefore"] = before, ["attackersAfterCancel"] = track.attackers });
            Require(checks, key + "-queued-revalidation-and-native-reservation-release", wasValid && !remainsValid && track.attackers == before);
            Position(target, launcher, range, altitude, 0f);
        }

        private static void HomeOnJamProbes(string key, Missile missile, Unit excludedJammer, Unit allowedJammer, List<object> rows, List<object> checks)
        {
            ARHSeeker seeker = missile.GetComponent<ARHSeeker>();
            if (seeker == null) return;
            bool homeOnJam = (bool)NaturalWeapons.Get(seeker, "homeOnJam");
            if (!homeOnJam) return;
            MethodInfo jam = AccessTools.Method(typeof(ARHSeeker), "ARHSeeker_OnJam");
            float before = (float)NaturalWeapons.Get(seeker, "jamAccumulation");
            float amount = (float)NaturalWeapons.Get(seeker, "jamTolerance") + 10f;
            PersistentID oldTarget = missile.targetID;
            Unit oldSeekerTarget = (Unit)NaturalWeapons.Get(seeker, "targetUnit");
            MoveJammer(excludedJammer, missile);
            jam.Invoke(seeker, new object[] { new Unit.JamEventArgs { jammingUnit = excludedJammer, jamAmount = amount } });
            float after = (float)NaturalWeapons.Get(seeker, "jamAccumulation");
            bool retained = missile.targetID == oldTarget && (Unit)NaturalWeapons.Get(seeker, "targetUnit") == oldSeekerTarget;
            rows.Add(new Dictionary<string, object> { ["name"] = key + "-excluded-ground-jammer", ["nativeJamBefore"] = before,
                ["nativeJamAfter"] = after, ["retainedAuthorizedTarget"] = retained, ["homeOnJamRestored"] = (bool)NaturalWeapons.Get(seeker, "homeOnJam") });
            Require(checks, key + "-ground-jamming-affects-receiver-without-retarget", after > before && retained && (bool)NaturalWeapons.Get(seeker, "homeOnJam") == homeOnJam);

            // An allowed jammer must still execute the native retarget branch.
            // A sentinel known position proves the branch even if it selects
            // the same unit that was already the missile's intended target.
            MoveJammer(allowedJammer, missile);
            NaturalWeapons.Set(seeker, "knownPos", missile.GlobalPosition() - missile.transform.forward * 10000f);
            jam.Invoke(seeker, new object[] { new Unit.JamEventArgs { jammingUnit = allowedJammer, jamAmount = amount } });
            bool selected = missile.targetID == allowedJammer.persistentID && (Unit)NaturalWeapons.Get(seeker, "targetUnit") == allowedJammer &&
                ((GlobalPosition)NaturalWeapons.Get(seeker, "knownPos") - allowedJammer.GlobalPosition()).sqrMagnitude < .01f;
            rows.Add(new Dictionary<string, object> { ["name"] = key + "-allowed-home-on-jam", ["jammerType"] = allowedJammer.GetType().Name,
                ["nativeRetargetBranchExecuted"] = selected, ["homeOnJamRestored"] = (bool)NaturalWeapons.Get(seeker, "homeOnJam") });
            Require(checks, key + "-authorized-native-home-on-jam-preserved", selected && (bool)NaturalWeapons.Get(seeker, "homeOnJam") == homeOnJam);
        }

        private static void MoveJammer(Unit jammer, Missile missile)
        {
            Vector3 point = missile.transform.position + missile.transform.forward * 3000f;
            MoveFixture(jammer, point, Vector3.zero);
            Physics.SyncTransforms();
        }

        private static void Launched(Unit owner, Missile __result)
        { if (owner == NaturalMissileTargetingTrial.owner && __result != null) launches.Add(__result); }
        private static object[] CaptureIRSources(Unit target)
        {
            if (target == null) return new object[0];
            var sources = (List<IRSource>)IRSources.GetValue(target);
            return sources.Select((source, index) =>
            {
                Transform transform = source != null ? source.transform : null;
                Rigidbody body = transform != null ? transform.GetComponentInParent<Rigidbody>() : null;
                UnitPart part = transform != null ? transform.GetComponentInParent<UnitPart>() : null;
                Vector3 offset = transform != null ? transform.GlobalPosition() - target.GlobalPosition() : Vector3.zero;
                float distance = transform != null ? offset.magnitude : float.NaN;
                bool finiteDistance = !float.IsNaN(distance) && !float.IsInfinity(distance);
                var row = new Dictionary<string, object> { ["index"] = index, ["sourcePresent"] = source != null,
                    ["transformPresent"] = transform != null, ["flare"] = source != null ? source.flare : (object)null,
                    ["intensity"] = source != null ? source.intensity : (object)null,
                    ["distanceFinite"] = finiteDistance, ["distanceToTargetMetres"] = finiteDistance ? distance : (object)null,
                    ["sourceOffsetFromTargetMetres"] = finiteDistance ? new[] { offset.x, offset.y, offset.z } : null,
                    ["withinNative100m"] = finiteDistance && distance <= 100f,
                    ["transformName"] = transform != null ? transform.name : null,
                    ["transformInstanceId"] = transform != null ? transform.GetInstanceID() : (object)null,
                    ["parentName"] = transform != null && transform.parent != null ? transform.parent.name : null,
                    ["ancestorNames"] = AncestorNames(transform),
                    ["rootName"] = transform != null ? transform.root.name : null,
                    ["rootInstanceId"] = transform != null ? transform.root.GetInstanceID() : (object)null,
                    ["isChildOfTarget"] = transform != null && transform.IsChildOf(target.transform),
                    ["nearestRigidbody"] = body != null ? body.name : null,
                    ["rigidbodyIsTargetRoot"] = body != null && body == target.rb,
                    ["rigidbodyIsRegisteredTargetPart"] = body != null && target.GetAllParts().Any(p => p != null && p.rb == body),
                    ["nearestUnitPart"] = part != null ? part.name : null,
                    ["partBelongsToTarget"] = part != null && part.parentUnit == target,
                    ["partDetached"] = part != null ? part.IsDetached() : (object)null };
                return (object)row;
            }).ToArray();
        }
        private static string[] AncestorNames(Transform transform)
        {
            var names = new List<string>();
            for (Transform node = transform; node != null && names.Count < 24; node = node.parent) names.Add(node.name);
            return names.ToArray();
        }
        private static bool FrozenFixtureSlowChecks(ARHSeeker __instance)
        {
            Missile missile = __instance != null ? __instance.GetComponent<Missile>() : null;
            if (owner == null || missile == null || !frozenMissileFixtures.Contains(missile)) return true;
            string key = missile.definition.jsonKey;
            int count; suppressedFixtureSlowChecks.TryGetValue(key, out count);
            suppressedFixtureSlowChecks[key] = count + 1;
            fixtureEvents.Add(new Dictionary<string, object> { ["event"] = "stationary-fixture-slow-check-suppressed",
                ["stage"] = fixtureStage, ["definition"] = key, ["ageSeconds"] = missile.timeSinceSpawn,
                ["disabled"] = missile.disabled, ["speed"] = missile.speed, ["engineOn"] = missile.EngineOn() });
            return false;
        }
        private static void FixtureDamaged(Missile __instance, float pierceDamage, float blastDamage, float amountAffected,
            float fireDamage, float impactDamage, PersistentID dealerID)
        {
            if (__instance == null || !frozenMissileFixtures.Contains(__instance)) return;
            fixtureEvents.Add(new Dictionary<string, object> { ["event"] = "native-fixture-damage", ["stage"] = fixtureStage,
                ["definition"] = __instance.definition.jsonKey, ["ageSeconds"] = __instance.timeSinceSpawn,
                ["disabledBefore"] = __instance.disabled, ["pierceDamage"] = pierceDamage, ["blastDamage"] = blastDamage,
                ["amountAffected"] = amountAffected, ["fireDamage"] = fireDamage, ["impactDamage"] = impactDamage,
                ["dealerId"] = dealerID.ToString(), ["nativeCallStack"] = new System.Diagnostics.StackTrace(1, false).ToString() });
        }
        private static void FixtureDetonated(Missile __instance, bool hitArmor, bool hitTerrain)
        {
            if (__instance == null || !frozenMissileFixtures.Contains(__instance)) return;
            fixtureEvents.Add(new Dictionary<string, object> { ["event"] = "native-fixture-detonation", ["stage"] = fixtureStage,
                ["definition"] = __instance.definition.jsonKey, ["ageSeconds"] = __instance.timeSinceSpawn,
                ["disabledBefore"] = __instance.disabled, ["hitArmor"] = hitArmor, ["hitTerrain"] = hitTerrain,
                ["nativeCallStack"] = new System.Diagnostics.StackTrace(1, false).ToString() });
        }
        private static void Require(List<object> checks, string name, bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("Targeting policy trial failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report)
        { File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented)); }
    }
}
