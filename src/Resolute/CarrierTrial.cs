using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mirage;
using NuclearOption;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Resolute
{
    // Explicit copied-game diagnostic. No production pilot, gear, collision or
    // hangar methods are patched by this test.
    internal static class CarrierTrial
    {
        internal static IEnumerator Run(Ship ship, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Carrier trial is restricted to the isolated test-game copy.");
            report["phase"] = "native-carrier";
            Airbase airbase = ship.GetComponent<Airbase>();
            Transform visual = ship.transform.Find("ResoluteVisual");
            var carrier = new Dictionary<string, object>
            {
                ["scope"] = "Native attached-airbase spawn, actual gear contact, ejection recovery and helicopter takeoff. Tests both native helicopter types, a networked player's non-reserved airframe and allocation, rotor clearance, and spectator map-icon follow while the ship moves. Autonomous landing approach is outside this test.",
                ["airframes"] = new List<object>()
            };
            report["carrier"] = carrier;
            Require(checks, "carrier-native-airbase", airbase != null && airbase.AttachedAirbase && airbase.CurrentHQ == ship.NetworkHQ);
            Require(checks, "carrier-native-hangar-registration", airbase.hangars.Count == 1 && airbase.hangars[0].parentAirbase == airbase);
            Hangar hangar = airbase.hangars[0];
            carrier["simulationAtEntry"] = SimulationState();
            Require(checks, "carrier-native-hangar-functional", hangar.IsFunctional() && hangar.Available);
            // Reproduce a saved 0.2.0 attached-airbase radius, then relink through
            // exactly the native path used by loading an existing mission.
            airbase.SavedAirbase.CaptureRange = 90f;
            airbase.SetupAttachedAirbase(ship);
            carrier["savedRadiusMigration"] = new Dictionary<string, object>
            { ["savedLegacyRadius"] = 90f, ["afterNativeRelinkRadius"] = airbase.GetRadius() };
            Require(checks, "carrier-existing-save-radius-upgraded", airbase.GetRadius() >= ship.definition.length * .5f + 25f);
            ship.SetHoldPosition(false);
            ship.UnitCommand.SetDestination(ship.GlobalPosition() + ship.transform.forward * 4000f, false);
            float movementDeadline = Time.realtimeSinceStartup + 50f;
            while (ship.speed < 3.2f && Time.realtimeSinceStartup < movementDeadline) yield return null;
            Require(checks, "carrier-moving-faster-than-native-world-recovery-limit", ship.speed > 3f);
            IEnumerator cameraProof = FollowCamera(ship, carrier, checks, output, report);
            while (cameraProof.MoveNext()) yield return cameraProof.Current;
            AircraftDefinition[] choices = airbase.GetAvailableAircraft().OrderBy(a => a.jsonKey).ToArray();
            carrier["availableAircraft"] = choices.Select(a => new Dictionary<string, object>
                { ["key"] = a.jsonKey, ["name"] = a.unitName }).ToList();
            Require(checks, "carrier-two-native-helicopters", choices.Length == 2 && choices.Any(a => a.jsonKey == "AttackHelo1") && choices.Any(a => a.jsonKey == "UtilityHelo1"));
            Vector3 padInSource = visual.InverseTransformPoint(hangar.GetSpawnTransform().position);
            carrier["takeoffPointInSource"] = V(padInSource);
            carrier["takeoffForwardInSource"] = V(visual.InverseTransformDirection(hangar.GetSpawnTransform().forward));
            carrier["nativeRecoveryRadiusMetres"] = airbase.GetRadius();
            carrier["landingPointsInSource"] = airbase.verticalLandingPoints.Select(p => V(visual.InverseTransformPoint(p.point.position))).ToList();
            Require(checks, "carrier-source-landing-circle", Vector2.Distance(new Vector2(padInSource.x, padInSource.z), CarrierIntegration.LandingCircle) < .02f);
            Require(checks, "carrier-takeoff-faces-bow-like-dynamo", Vector3.Dot(hangar.GetSpawnTransform().forward, ship.transform.forward) > .99f);
            Require(checks, "carrier-preview-faces-bow-like-dynamo", Vector3.Dot(airbase.aircraftSelectionTransform.forward, ship.transform.forward) > .99f);
            Require(checks, "carrier-approach-faces-stern-like-dynamo", Vector3.Dot(airbase.verticalLandingPoints[0].point.forward, -ship.transform.forward) > .99f);
            Airbase near;
            Require(checks, "carrier-pad-within-native-recovery-radius", ship.NetworkHQ.AnyNearAirbase(hangar.GetSpawnTransform().position, out near) && near == airbase);
            Transform service;
            Require(checks, "carrier-native-service-point", airbase.TryGetNearestServicePoint(hangar.GetSpawnTransform().position, out service) &&
                Vector3.Distance(service.position, hangar.GetSpawnTransform().position) < .02f);

            var deckProbes = new List<object>();
            carrier["deckProbes"] = deckProbes;
            foreach (float x in new[] { -8f, 0f, 8f })
            foreach (float z in new[] { -108f, -102f, -94f })
            {
                Vector3 expected = new Vector3(x, padInSource.y, z);
                RaycastHit hit;
                bool found = Physics.Raycast(visual.TransformPoint(expected + Vector3.up * 25), -visual.up, out hit, 50,
                    PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore);
                Vector3 actual = found ? visual.InverseTransformPoint(hit.point) : Vector3.zero;
                float planeError = found ? actual.y - padInSource.y : float.PositiveInfinity;
                deckProbes.Add(new Dictionary<string, object>
                { ["sourcePoint"] = V(expected), ["hit"] = found, ["part"] = found ? hit.collider.name : null,
                  ["hitSourcePoint"] = V(actual), ["planeErrorMetres"] = found ? (object)planeError : null });
                Save(output, report);
                Require(checks, "carrier-deck-probe-" + deckProbes.Count, found && hit.collider.attachedRigidbody == ship.rb && Mathf.Abs(planeError) < .05f);
            }

            foreach (AircraftDefinition definition in choices)
            {
                string key = definition.jsonKey;
                int supplyBefore = ship.NetworkHQ.GetUnitSupply(definition);
                ship.NetworkHQ.AddSupplyUnit(definition, 1);
                Require(checks, "carrier-" + key + "-available", airbase.CanSpawnAircraft(definition));
                var existing = new HashSet<PersistentID>(UnitRegistry.allAircraft.Select(a => a.persistentID));
                Airbase.TrySpawnResult spawn = airbase.TrySpawnAircraft(null, definition, new LiveryKey(0),
                    definition.aircraftParameters.loadouts[1], .5f);
                Require(checks, "carrier-" + key + "-native-spawn-accepted", spawn.Allowed && spawn.Hangar == hangar);
                Aircraft aircraft = UnitRegistry.allAircraft.FirstOrDefault(a => !existing.Contains(a.persistentID) && a.NetworkspawningHangar == hangar);
                float deadline = Time.realtimeSinceStartup + 15;
                while (aircraft == null && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    aircraft = UnitRegistry.allAircraft.FirstOrDefault(a => !existing.Contains(a.persistentID) && a.NetworkspawningHangar == hangar);
                }
                Require(checks, "carrier-" + key + "-native-aircraft", aircraft != null && aircraft.LocalSim && aircraft.NetworkHQ == ship.NetworkHQ);
                foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(pilot.parkedState);
                aircraft.NetworkIgnition = false;
                ControlInputs inputs = aircraft.GetInputs();
                inputs.throttle = 0; inputs.brake = 1; inputs.pitch = inputs.roll = inputs.yaw = 0;

                var samples = new List<object>();
                var airframe = new Dictionary<string, object>
                {
                    ["key"] = key, ["name"] = definition.unitName, ["spawnHangarCorrect"] = aircraft.NetworkspawningHangar == hangar,
                    ["spawnGlobalPosition"] = V(aircraft.GlobalPosition().AsVector3()), ["samples"] = samples,
                    ["supplyBeforeProvision"] = supplyBefore, ["supplyAfterNativeSpawn"] = ship.NetworkHQ.GetUnitSupply(definition)
                };
                ((List<object>)carrier["airframes"]).Add(airframe);
                float spawnHeadingDot = Vector3.Dot(Vector3.ProjectOnPlane(aircraft.transform.forward, ship.transform.up).normalized, ship.transform.forward);
                airframe["spawnForwardInShip"] = V(ship.transform.InverseTransformDirection(aircraft.transform.forward));
                airframe["spawnHeadingDotBow"] = spawnHeadingDot;
                Require(checks, "carrier-" + key + "-actual-spawn-faces-bow", spawnHeadingDot > .99f);
                // Full aircraft physics reparents AeroPart rigidbodies to scene
                // roots. The native part registry remains their ownership source.
                LandingGear[] gear = aircraft.GetAllParts().Where(p => p != null)
                    .SelectMany(p => p.GetComponentsInChildren<LandingGear>(true))
                    .Where(g => g.gameObject.activeInHierarchy && CarrierIntegration.Read<Aircraft>(g, "aircraft") == aircraft)
                    .Distinct().ToArray();
                int expectedGear = definition.unitPrefab.GetComponentsInChildren<LandingGear>(true).Length;
                airframe["nativeGearDiscovery"] = new Dictionary<string, object>
                {
                    ["source"] = "Aircraft.GetAllParts with LandingGear.aircraft owner verification",
                    ["prefabGearCount"] = expectedGear, ["liveGearCount"] = gear.Length,
                    ["rootChildGearCount"] = aircraft.GetComponentsInChildren<LandingGear>(true).Length,
                    ["gear"] = gear.Select(g => new Dictionary<string, object>
                        { ["name"] = g.name, ["wheels"] = CarrierIntegration.Read<Transform[]>(g, "wheels").Length,
                          ["aircraftRootDescendant"] = g.transform.IsChildOf(aircraft.transform) }).ToList()
                };
                Save(output, report);
                Require(checks, "carrier-" + key + "-native-landing-gear", expectedGear > 0 && gear.Length == expectedGear);
                float started = Time.realtimeSinceStartup;
                Vector3 shipStart = ship.GlobalPosition().AsVector3();
                Vector3 aircraftStart = aircraft.GlobalPosition().AsVector3();
                Vector3 relativeStart = ship.transform.InverseTransformPoint(aircraft.transform.position);
                int maxContacts = 0, finalContacts = 0;
                float greatestPlaneError = 0;
                for (int sample = 0; sample < 9; sample++)
                {
                    if (sample > 0) yield return new WaitForSecondsRealtime(1);
                    Require(checks, "carrier-" + key + "-healthy-" + sample, aircraft != null && !aircraft.disabled && !aircraft.HasEjected());
                    var contacts = new List<object>();
                    finalContacts = 0;
                    foreach (LandingGear leg in gear)
                    {
                        float force = CarrierIntegration.Read<float>(leg, "compressionForce");
                        Collider collider = CarrierIntegration.Read<Collider>(leg, "contactCollider");
                        RaycastHit hit = CarrierIntegration.Read<RaycastHit>(leg, "hit");
                        bool restingOnShip = force > 0 && collider != null && collider.attachedRigidbody == ship.rb;
                        float planeError = restingOnShip ? visual.InverseTransformPoint(hit.point).y - padInSource.y : 0;
                        if (restingOnShip)
                        {
                            finalContacts++;
                            greatestPlaneError = Mathf.Max(greatestPlaneError, Mathf.Abs(planeError));
                        }
                        contacts.Add(new Dictionary<string, object>
                        { ["leg"] = leg.name, ["restingOnShip"] = restingOnShip, ["forceNewtons"] = force,
                          ["compressionMetres"] = CarrierIntegration.Read<float>(leg, "compressionDistance"),
                          ["part"] = collider != null ? collider.name : null,
                          ["planeErrorMetres"] = restingOnShip ? (object)planeError : null });
                    }
                    maxContacts = Mathf.Max(maxContacts, finalContacts);
                    Vector3 relativeVelocity = aircraft.rb.velocity - ship.rb.GetPointVelocity(aircraft.transform.position);
                    samples.Add(new Dictionary<string, object>
                    { ["elapsedSeconds"] = Time.realtimeSinceStartup - started, ["gearState"] = aircraft.gearState.ToString(),
                      ["contacts"] = contacts, ["rootInShip"] = V(ship.transform.InverseTransformPoint(aircraft.transform.position)),
                      ["relativeVelocityMps"] = V(relativeVelocity), ["nativeLanded"] = aircraft.IsLanded() });
                    Save(output, report);
                }
                Vector3 relativeEnd = ship.transform.InverseTransformPoint(aircraft.transform.position);
                Vector3 horizontalDrift = relativeEnd - relativeStart; horizontalDrift.y = 0;
                Vector3 shipTravel = ship.GlobalPosition().AsVector3() - shipStart; shipTravel.y = 0;
                Vector3 aircraftTravel = aircraft.GlobalPosition().AsVector3() - aircraftStart; aircraftTravel.y = 0;
                airframe["shipTravelMetres"] = shipTravel.magnitude;
                airframe["aircraftTravelMetres"] = aircraftTravel.magnitude;
                airframe["relativeDeckDriftMetres"] = horizontalDrift.magnitude;
                airframe["maxContactCount"] = maxContacts;
                airframe["finalContactCount"] = finalContacts;
                airframe["maxContactPlaneErrorMetres"] = greatestPlaneError;
                airframe["captures"] = Capture(ship, aircraft, visual, padInSource,
                    Path.Combine(Path.GetDirectoryName(output), "game_previews"));
                Save(output, report);
                Require(checks, "carrier-" + key + "-all-gear-contact", maxContacts == gear.Length && finalContacts >= 2);
                Require(checks, "carrier-" + key + "-contact-on-source-deck", greatestPlaneError < .05f);
                Require(checks, "carrier-" + key + "-rests-with-ship", aircraft.IsLanded() && horizontalDrift.magnitude < 1.5f);
                Airbase.VerticalLandingPoint landing;
                Require(checks, "carrier-" + key + "-native-landing-point", airbase.TryRequestVerticalLanding(aircraft,
                    new RunwayQuery { MinSize = Mathf.Max(definition.length, definition.width) * .5f }, out landing) &&
                    landing.unitPart != null && landing.unitPart.parentUnit == ship);

                Ship contactCarrier; float relativeSpeed;
                Require(checks, "carrier-" + key + "-actual-recoverable-contact", CarrierRecovery.DeckContact(aircraft, out contactCarrier, out relativeSpeed) && contactCarrier == ship && relativeSpeed < 2f);
                airframe["nativeAbandonment"] = new Dictionary<string, object>
                { ["worldSpeedMps"] = aircraft.rb.velocity.magnitude, ["nativeSurfaceRelativeSpeedMps"] = aircraft.speed,
                  ["deckRelativeSpeedMps"] = relativeSpeed, ["trigger"] = "Aircraft.StartEjectionSequence" };
                aircraft.StartEjectionSequence();
                deadline = Time.realtimeSinceStartup + 20;
                bool returned = false;
                while (aircraft != null && !aircraft.disabled && Time.realtimeSinceStartup < deadline) yield return null;
                returned = aircraft != null && aircraft.NetworkunitState == Unit.UnitState.Returned;
                Require(checks, "carrier-" + key + "-native-abandonment-returned", returned);
                while ((aircraft != null || !hangar.Available) && Time.realtimeSinceStartup < deadline) yield return null;
                Require(checks, "carrier-" + key + "-native-inventory-return", aircraft == null && hangar.Available);
                ship.NetworkHQ.AddSupplyUnit(definition, -1);
                airframe["supplyAfterReturnAndUnprovision"] = ship.NetworkHQ.GetUnitSupply(definition);
                Require(checks, "carrier-" + key + "-inventory-restored", ship.NetworkHQ.GetUnitSupply(definition) == supplyBefore);
                Save(output, report);
            }
            IEnumerator ownershipProof = OwnedRecovery(ship, airbase, hangar, choices[0], carrier, checks, output, report);
            while (ownershipProof.MoveNext()) yield return ownershipProof.Current;
            IEnumerator takeoffProof = Takeoffs(ship, airbase, hangar, choices, carrier, checks, output, report);
            while (takeoffProof.MoveNext()) yield return takeoffProof.Current;
            ship.SetHoldPosition(true);
            carrier["success"] = true;
            Save(output, report);
        }

        internal static IEnumerator FollowCamera(Ship ship, Dictionary<string, object> carrier, List<object> checks, string output,
            Dictionary<string, object> report, bool requireTravel = true, string checkPrefix = "carrier-spectator", bool requireSpectatorMap = true)
        {
            CameraStateManager camera = SceneSingleton<CameraStateManager>.i;
            DynamicMap map = SceneSingleton<DynamicMap>.i;
            var proof = new Dictionary<string, object>
            {
                ["cameraPresent"] = camera != null, ["mapPresent"] = map != null,
                ["shipDisabled"] = ship.disabled, ["shipRigidBodyPresent"] = ship.rb != null,
                ["detachedNativeParts"] = ship.parts.Count(p => p != null && p.IsDetached()),
                ["localHQPresent"] = GameManager.GetLocalHQ(out var _),
                ["nativeMapFactionMode"] = map != null ? DynamicMap.GetFactionMode().ToString() : null,
                ["attachedAirbaseIconCount"] = Resources.FindObjectsOfTypeAll<AirbaseMapIcon>().Count(i => i.gameObject.scene.IsValid() && i.airbase == ship.GetComponent<Airbase>()),
                ["paths"] = new List<object>()
            };
            carrier["spectatorCamera"] = proof;
            Save(output, report);
            bool prerequisites = camera != null && map != null && ship.rb != null && (!requireSpectatorMap || DynamicMap.GetFactionMode() == FactionMode.Spectator);
            Check(checks, checkPrefix + "-camera-prerequisites", prerequisites);
            if (!prerequisites) yield break;
            UnitMapIcon icon = map.GetOrAddIcon(ship);
            foreach (string route in new[] { "native-unit-map-icon", "native-SetFollowingUnit" })
            {
                var path = new Dictionary<string, object> { ["route"] = route };
                ((List<object>)proof["paths"]).Add(path);
                bool requested = false;
                try
                {
                    camera.SetFollowingUnit(null);
                    if (route == "native-unit-map-icon")
                    {
                        bool cursorInside = map.IsCursorInMapRectangle();
                        path["cursorInsideHiddenTestWindowMap"] = cursorInside;
                        icon.ClickIcon(MapIcon.ClickSource.Controller);
                        path["publicClickSelectedShip"] = camera.followingUnit == ship;
                        if (camera.followingUnit != ship && !cursorInside)
                        {
                            // A hidden copied-game window cannot own the user's pointer.
                            // Invoke the same native selection handler after its cursor gate.
                            MethodInfo select = typeof(UnitMapIcon).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                                .SingleOrDefault(m => m.Name.Contains("g__Select|") && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(bool));
                            path["nativeSelectionHandler"] = select?.Name;
                            if (select != null) select.Invoke(icon, new object[] { false });
                        }
                    }
                    else camera.SetFollowingUnit(ship);
                    requested = camera.followingUnit == ship;
                }
                catch (Exception error) { path["error"] = error.ToString(); }
                Vector3 started = ship.GlobalPosition().AsVector3();
                float stop = Time.time + 3f;
                float maxPivotError = 0f;
                bool allLocked = requested;
                while (Time.time < stop)
                {
                    yield return null;
                    allLocked &= camera.followingUnit == ship && camera.followingRB == ship.rb && camera.currentState == camera.orbitState && camera.cameraPivot.parent == ship.rb.transform;
                    maxPivotError = Mathf.Max(maxPivotError, Vector3.Distance(camera.cameraPivot.position, ship.rb.transform.position));
                }
                float travel = Vector3.Distance(started, ship.GlobalPosition().AsVector3());
                path["shipTravelMetres"] = travel; path["nativeOrbitThroughout"] = allLocked; path["maxPivotErrorMetres"] = maxPivotError;
                path["finalState"] = camera.currentState?.GetType().Name;
                path["followingUnit"] = camera.followingUnit != null ? camera.followingUnit.definition.jsonKey : null;
                path["followingRigidBody"] = camera.followingRB != null ? camera.followingRB.name : null;
                path["pilotMoveLongitudinalInput"] = GameManager.playerInput.GetAxis("Move Longitudinal");
                path["pilotMoveLateralInput"] = GameManager.playerInput.GetAxis("Move Lateral");
                path["rendering"] = CameraRendering(ship, camera);
                if (allLocked) path["capture"] = CaptureMainCamera(camera, Path.Combine(Path.GetDirectoryName(output), "game_previews", checkPrefix + "_" + route + ".png"));
                Save(output, report);
                Check(checks, checkPrefix + "-" + route + "-locks-ship", allLocked && (!requireTravel || travel > 6f) && maxPivotError < .1f);
            }
            if (checkPrefix.StartsWith("post-death", StringComparison.Ordinal) && camera.followingUnit == ship)
            {
                var distances = new List<object>(); proof["nativeOrbitDistanceSamples"] = distances;
                FieldInfo adjustment = typeof(CameraOrbitState).GetField("viewDistAdjust", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (float distance in new[] { 600f, 1200f, 2400f })
                {
                    adjustment.SetValue(camera.orbitState, Mathf.Clamp(distance / (2f * (1f + ship.maxRadius)) - 1f, 0f, 10f));
                    yield return null; yield return null;
                    Dictionary<string, object> sample = CameraRendering(ship, camera);
                    sample["requestedDistanceMetres"] = distance;
                    sample["capture"] = CaptureMainCamera(camera, Path.Combine(Path.GetDirectoryName(output), "game_previews", checkPrefix + "_distance_" + distance + ".png"));
                    distances.Add(sample); Save(output, report);
                }
            }
            camera.SetFollowingUnit(null);
        }

        private static IEnumerator OwnedRecovery(Ship ship, Airbase airbase, Hangar hangar, AircraftDefinition definition,
            Dictionary<string, object> carrier, List<object> checks, string output, Dictionary<string, object> report)
        {
            var manager = NetworkManagerNuclearOption.i;
            NetworkIdentity previousCharacter = manager.Server.LocalPlayer.Identity;
            GameManager.GetLocalPlayer<BasePlayer>(out BasePlayer previousLocalPlayer);
            Player player = UnityEngine.Object.Instantiate(CarrierIntegration.Read<Player>(manager, "gamePlayerPrefab"));
            // Native Aircraft.CheckIfLocalSim uses Player.IsLocalPlayer, so the
            // diagnostic must actually become the host character before spawning.
            manager.ServerObjectManager.ReplaceCharacter(manager.Server.LocalPlayer, player.Identity, true);
            yield return null;
            player.SetFaction(ship.NetworkHQ, true);
            Require(checks, "carrier-real-local-player", player.IsLocalPlayer && GameManager.IsLocalPlayer(player));
            player.OwnedAirframes.Add(new OwnedAirframe(definition, false));
            int before = player.OwnedAirframes.Count;
            float allocation = player.Allocation;
            float score = player.PlayerScore;
            int supply = ship.NetworkHQ.GetUnitSupply(definition);
            var existing = new HashSet<Aircraft>(UnitRegistry.allAircraft);
            Airbase.TrySpawnResult accepted = airbase.TrySpawnAircraft(player, definition, new LiveryKey(0), definition.aircraftParameters.loadouts[1], .5f);
            Aircraft aircraft = UnitRegistry.allAircraft.FirstOrDefault(a => !existing.Contains(a) && a.Player == player);
            Require(checks, "carrier-owned-airframe-native-spawn", accepted.Allowed && aircraft != null && aircraft.LocalSim && player.AirframeInUse.HasValue && player.OwnedAirframes.Count == before - 1);
            // Native cockpit apps read CombatHUD.aircraft in Start. Let their
            // spawn-frame initialization finish before leaving player control.
            yield return null; yield return null;
            foreach (Pilot pilot in aircraft.pilots) pilot.SwitchState(pilot.parkedState);
            aircraft.NetworkIgnition = false;
            aircraft.GetInputs().throttle = 0f; aircraft.GetInputs().brake = 1f;
            Ship contactShip; float relativeSpeed = float.PositiveInfinity;
            float deadline = Time.realtimeSinceStartup + 20f;
            while ((!CarrierRecovery.DeckContact(aircraft, out contactShip, out relativeSpeed) || relativeSpeed >= 1f) && Time.realtimeSinceStartup < deadline) yield return null;
            var recovery = new Dictionary<string, object>
            {
                ["definition"] = definition.jsonKey, ["networkedPlayer"] = player.NetId, ["aircraftPlayerMatches"] = aircraft.Player == player,
                ["ownedBeforeSpawn"] = before, ["ownedAfterSpawn"] = player.OwnedAirframes.Count,
                ["worldSpeedAtAbandonment"] = aircraft.rb.velocity.magnitude, ["nativeSurfaceRelativeSpeedAtAbandonment"] = aircraft.speed,
                ["deckRelativeSpeedAtAbandonment"] = relativeSpeed, ["localSimulation"] = aircraft.LocalSim,
                ["allocationBefore"] = allocation, ["playerScoreBefore"] = score, ["trigger"] = "Aircraft.StartEjectionSequence"
            };
            carrier["ownedAirframeRecovery"] = recovery;
            recovery["simulationAtAbandonment"] = SimulationState();
            Save(output, report);
            Require(checks, "carrier-owned-airframe-resting-on-moving-deck", CarrierRecovery.DeckContact(aircraft, out contactShip, out relativeSpeed) && contactShip == ship && relativeSpeed < 2f && aircraft.rb.velocity.magnitude > 2.5f);
            aircraft.StartEjectionSequence();
            deadline = Time.realtimeSinceStartup + 20f;
            while (aircraft != null && !aircraft.disabled && Time.realtimeSinceStartup < deadline) yield return null;
            recovery["nativeReturnedState"] = aircraft != null && aircraft.NetworkunitState == Unit.UnitState.Returned;
            recovery["ownedAfterRecovery"] = player.OwnedAirframes.Count;
            recovery["airframeInUseCleared"] = !player.AirframeInUse.HasValue;
            recovery["nonReservedFramePreserved"] = player.OwnedAirframes.Any(a => a.Definition == definition && !a.Reserved);
            recovery["allocationAfter"] = player.Allocation;
            recovery["playerScoreAfter"] = player.PlayerScore;
            recovery["factionSupplyUnchanged"] = supply == ship.NetworkHQ.GetUnitSupply(definition);
            recovery["simulationAtNativeReturn"] = SimulationState();
            Save(output, report);
            Require(checks, "carrier-owned-airframe-recovered-without-loss", (bool)recovery["nativeReturnedState"] && player.OwnedAirframes.Count == before &&
                !player.AirframeInUse.HasValue && (bool)recovery["nonReservedFramePreserved"] && Mathf.Approximately(player.Allocation, allocation) &&
                Mathf.Approximately(player.PlayerScore, score) && (bool)recovery["factionSupplyUnchanged"]);
            SceneSingleton<CameraStateManager>.i.SetFollowingUnit(null);
            while ((aircraft != null || !hangar.Available) && Time.realtimeSinceStartup < deadline) yield return null;
            recovery["simulationAtPadClearance"] = SimulationState();
            recovery["aircraftDestroyedAtPadClearance"] = aircraft == null;
            recovery["hangarAvailableAtPadClearance"] = hangar.Available;
            recovery["hangarDisabledAtPadClearance"] = hangar.Disabled;
            Save(output, report);
            Require(checks, "carrier-owned-recovery-clears-pad", aircraft == null && hangar.Available);
            IEnumerator postDeath = PostDeathCamera(ship, player, airbase, hangar, definition, carrier, checks, output, report);
            while (postDeath.MoveNext()) yield return postDeath.Current;
            deadline = Time.realtimeSinceStartup + 10f;
            while (!hangar.Available && Time.realtimeSinceStartup < deadline) yield return null;
            Require(checks, "carrier-post-death-cleanup-releases-native-hangar", hangar.Available);
            manager.ServerObjectManager.ReplaceCharacter(manager.Server.LocalPlayer, previousCharacter, true);
            GameManager.SetLocalPlayer(previousLocalPlayer);
            SceneSingleton<CombatHUD>.i.RemoveAircraft();
            SceneSingleton<DynamicMap>.i.SetFaction(null);
            UnityEngine.Object.Destroy(player.gameObject);
            yield return null;
        }

        private static Dictionary<string, object> SimulationState()
        {
            return new Dictionary<string, object>
            {
                ["simulatedSeconds"] = Time.time,
                ["realtimeSeconds"] = Time.realtimeSinceStartup,
                ["timeScale"] = Time.timeScale,
                ["nativeSlowMotion"] = GameplayUI.GameSlowMotion,
                ["applicationFocused"] = Application.isFocused,
                ["ignoreInputWhenNotFocused"] = Rewired.ReInput.configuration.ignoreInputWhenAppNotInFocus
            };
        }

        private static IEnumerator Takeoffs(Ship ship, Airbase airbase, Hangar hangar, AircraftDefinition[] choices,
            Dictionary<string, object> carrier, List<object> checks, string output, Dictionary<string, object> report)
        {
            var takeoffs = new List<object>(); carrier["takeoffs"] = takeoffs;
            foreach (AircraftDefinition definition in choices)
            {
                int supply = ship.NetworkHQ.GetUnitSupply(definition);
                ship.NetworkHQ.AddSupplyUnit(definition, 1);
                var existing = new HashSet<Aircraft>(UnitRegistry.allAircraft);
                Airbase.TrySpawnResult accepted = airbase.TrySpawnAircraft(null, definition, new LiveryKey(0), definition.aircraftParameters.loadouts[1], .5f);
                Aircraft aircraft = UnitRegistry.allAircraft.FirstOrDefault(a => !existing.Contains(a) && a.NetworkspawningHangar == hangar);
                Require(checks, "carrier-" + definition.jsonKey + "-takeoff-native-spawn", accepted.Allowed && aircraft != null);
                // Leave the helicopter's native starting AI state and engine controls intact.
                var initialParts = aircraft.GetAllParts().Where(p => p != null).ToDictionary(p => p, p => p.hitPoints);
                SwashRotor[] blades = aircraft.GetAllParts().Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<SwashRotor>(true)).Distinct().ToArray();
                var bladeLengths = blades.ToDictionary(b => b, b => b.GetLength());
                var samples = new List<object>();
                var proof = new Dictionary<string, object> { ["definition"] = definition.jsonKey, ["samples"] = samples };
                takeoffs.Add(proof);
                proof["rotorSweep"] = RotorSweep(ship, aircraft);
                Save(output, report);
                Require(checks, "carrier-" + definition.jsonKey + "-rotor-sweep-clear", ((List<Dictionary<string, object>>)proof["rotorSweep"]).Count > 0 &&
                    ((List<Dictionary<string, object>>)proof["rotorSweep"]).All(r => ((List<object>)r["shipObstructions"]).Count == 0));
                float started = Time.time, next = Time.time, deadline = Time.realtimeSinceStartup + 65f;
                float maxRelativeAltitude = 0f;
                bool gearCleared = false, nativeTakeoff = false;
                while (aircraft != null && !aircraft.disabled && !aircraft.HasEjected() && Time.realtimeSinceStartup < deadline && Time.time - started < 45f)
                {
                    if (Time.time >= next)
                    {
                        next = Time.time + 1f;
                        float altitude = Vector3.Dot(aircraft.transform.position - hangar.GetSpawnTransform().position, ship.transform.up);
                        maxRelativeAltitude = Mathf.Max(maxRelativeAltitude, altitude);
                        nativeTakeoff |= aircraft.pilots.Any(p => p.currentState is AIHeloTakeoffState);
                        Ship contactShip; float relativeSpeed;
                        bool onDeck = CarrierRecovery.DeckContact(aircraft, out contactShip, out relativeSpeed);
                        gearCleared |= altitude > 8f && !onDeck;
                        samples.Add(new Dictionary<string, object>
                        { ["simulatedSeconds"] = Time.time - started, ["rootInSource"] = V(ship.transform.Find("ResoluteVisual").InverseTransformPoint(aircraft.transform.position)),
                          ["altitudeAbovePadMetres"] = altitude, ["nativeRadarAltitude"] = aircraft.radarAlt, ["throttle"] = aircraft.GetInputs().throttle,
                          ["ignition"] = aircraft.NetworkIgnition, ["gearOnShip"] = onDeck, ["pilotState"] = aircraft.pilots[0].currentState?.GetType().Name });
                        Save(output, report);
                        if (altitude > 25f && gearCleared && hangar.Available) break;
                    }
                    yield return null;
                }
                proof["maxHeightAboveDeckMetres"] = maxRelativeAltitude;
                proof["nativeTakeoffObserved"] = nativeTakeoff;
                proof["gearCleared"] = gearCleared;
                proof["hangarAvailableAfterDeparture"] = hangar.Available;
                proof["damagedParts"] = initialParts.Where(p => p.Key == null || p.Key.hitPoints < p.Value - .1f || p.Key.IsDetached()).Select(p => p.Key != null ? p.Key.name : "destroyed").ToList();
                proof["rotorBladesIntact"] = blades.Length > 0 && bladeLengths.All(b => b.Key != null && Mathf.Abs(b.Key.GetLength() - b.Value) < .01f);
                Save(output, report);
                Require(checks, "carrier-" + definition.jsonKey + "-native-takeoff-clears-hangar", aircraft != null && !aircraft.disabled && !aircraft.HasEjected() && nativeTakeoff &&
                    gearCleared && maxRelativeAltitude > 25f && hangar.Available && ((List<string>)proof["damagedParts"]).Count == 0 && (bool)proof["rotorBladesIntact"]);
                // Diagnostic cleanup after proving flight; abandonment was tested separately.
                aircraft.ReturnToInventory();
                deadline = Time.realtimeSinceStartup + 10f;
                while (aircraft != null && Time.realtimeSinceStartup < deadline) yield return null;
                ship.NetworkHQ.AddSupplyUnit(definition, -1);
                Require(checks, "carrier-" + definition.jsonKey + "-takeoff-cleanup", aircraft == null && ship.NetworkHQ.GetUnitSupply(definition) == supply);
            }
        }

        private static IEnumerator PostDeathCamera(Ship ship, Player player, Airbase airbase, Hangar hangar, AircraftDefinition definition,
            Dictionary<string, object> carrier, List<object> checks, string output, Dictionary<string, object> report)
        {
            var existing = new HashSet<Aircraft>(UnitRegistry.allAircraft);
            Airbase.TrySpawnResult accepted = airbase.TrySpawnAircraft(player, definition, new LiveryKey(0), definition.aircraftParameters.loadouts[1], .5f);
            Aircraft aircraft = UnitRegistry.allAircraft.FirstOrDefault(a => !existing.Contains(a) && a.Player == player);
            Require(checks, "camera-post-death-native-player-aircraft", accepted.Allowed && aircraft != null && aircraft.LocalSim && GameManager.IsLocalAircraft(aircraft));
            aircraft.NetworkIgnition = false;
            yield return null; yield return null;
            bool allowRespawn = MissionManager.CurrentMission.missionSettings.allowRespawn;
            MissionManager.CurrentMission.missionSettings.allowRespawn = true;
            var death = new Dictionary<string, object>
            { ["trigger"] = "Native Pilot.TakeDamage on the actual local player's primary pilot", ["cameraBeforeDeath"] = SceneSingleton<CameraStateManager>.i.currentState?.GetType().Name };
            carrier["postDeathCamera"] = death;
            try
            {
                foreach (Pilot pilot in aircraft.pilots) pilot.TakeDamage(1000000f, 0f, 1f, 0f, 0f, default(PersistentID));
                yield return null; yield return null;
                death["primaryPilotDead"] = aircraft.pilots[0].dead;
                death["aircraftDisabled"] = aircraft.disabled;
                death["hudHasActiveAircraft"] = SceneSingleton<CombatHUD>.i.aircraft != null && !SceneSingleton<CombatHUD>.i.aircraft.disabled;
                death["localPlayerStillAssigned"] = GameManager.IsLocalPlayer(player);
                death["cameraAfterDeath"] = SceneSingleton<CameraStateManager>.i.currentState?.GetType().Name;
                Save(output, report);
                Require(checks, "camera-native-player-death-releases-controls", aircraft.pilots[0].dead && aircraft.disabled && !(bool)death["hudHasActiveAircraft"]);
                IEnumerator resolute = FollowCamera(ship, death, checks, output, report, false, "post-death-resolute", false);
                while (resolute.MoveNext()) yield return resolute.Current;
                ShipDefinition donor = Plugin.FindDonor(Encyclopedia.i);
                Ship reference = NetworkSceneSingleton<Spawner>.i.SpawnShip(donor.unitPrefab, ship.GlobalPosition() + ship.transform.right * 150f,
                    ship.transform.rotation, ship.NetworkHQ, "resolute_postdeath_dynamo_reference", 1f, true);
                MissionTrial.HoldControllers(reference);
                yield return null; yield return null;
                var native = new Dictionary<string, object>(); death["nativeDynamoComparison"] = native;
                IEnumerator comparison = FollowCamera(reference, native, checks, output, report, false, "post-death-dynamo", false);
                while (comparison.MoveNext()) yield return comparison.Current;
                UnityEngine.Object.Destroy(reference.gameObject);
            }
            finally
            {
                MissionManager.CurrentMission.missionSettings.allowRespawn = allowRespawn;
                SceneSingleton<CameraStateManager>.i.SetFollowingUnit(null);
                if (aircraft != null)
                {
                    player.RemoveAircraft(aircraft);
                    UnityEngine.Object.Destroy(aircraft.gameObject);
                }
            }
            yield return null;
        }

        private static Dictionary<string, object> CameraRendering(Ship ship, CameraStateManager camera)
        {
            Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(camera.mainCamera);
            Renderer[] renderers = ship.GetComponentsInChildren<Renderer>(true);
            return new Dictionary<string, object>
            {
                ["maxRadius"] = ship.maxRadius, ["cameraDistanceMetres"] = Vector3.Distance(camera.transform.position, ship.transform.position),
                ["cameraPositionInShip"] = V(ship.transform.InverseTransformPoint(camera.transform.position)),
                ["cullingMask"] = camera.mainCamera.cullingMask, ["nearClip"] = camera.mainCamera.nearClipPlane,
                ["farClip"] = camera.mainCamera.farClipPlane, ["fieldOfView"] = camera.mainCamera.fieldOfView,
                ["sourceHullRenderers"] = renderers.Where(r => r is MeshRenderer && (r.name.StartsWith("rsl_hull", StringComparison.Ordinal) ||
                    r.name == "LOD1_rsl_paint" || r.name == "LOD2_rsl_paint" || !Plugin.IsResolute(ship.definition))).Select(r => new Dictionary<string, object>
                { ["name"] = r.name, ["active"] = r.gameObject.activeInHierarchy, ["enabled"] = r.enabled, ["forceRenderingOff"] = r.forceRenderingOff,
                  ["layer"] = r.gameObject.layer, ["layerIncludedByCamera"] = (camera.mainCamera.cullingMask & (1 << r.gameObject.layer)) != 0,
                  ["insideCameraFrustum"] = GeometryUtility.TestPlanesAABB(frustum, r.bounds), ["visibleToAnyCamera"] = r.isVisible,
                  ["boundsCentreInShip"] = V(ship.transform.InverseTransformPoint(r.bounds.center)), ["boundsSize"] = V(r.bounds.size),
                  ["meshPresent"] = r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null,
                  ["shaders"] = r.sharedMaterials.Select(m => m != null && m.shader != null ? m.shader.name : null).ToArray() }).ToList(),
                ["lodGroups"] = ship.GetComponentsInChildren<LODGroup>(true).Select(l => new Dictionary<string, object>
                { ["name"] = l.name, ["enabled"] = l.enabled, ["size"] = l.size, ["referenceInShip"] = V(ship.transform.InverseTransformPoint(l.transform.TransformPoint(l.localReferencePoint))),
                  ["levels"] = l.GetLODs().Select(level => new Dictionary<string, object> { ["transition"] = level.screenRelativeTransitionHeight,
                      ["renderers"] = level.renderers.Count(r => r != null), ["activeRenderers"] = level.renderers.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy),
                      ["inFrustumRenderers"] = level.renderers.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(frustum, r.bounds)) }).ToList() }).ToList()
            };
        }

        private static string CaptureMainCamera(CameraStateManager camera, string output)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var texture = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            RenderTexture previous = RenderTexture.active;
            try
            {
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = texture };
                if (!RenderPipeline.SupportsRenderRequest(camera.mainCamera, request)) return null;
                RenderPipeline.SubmitRenderRequest(camera.mainCamera, request);
                RenderTexture.active = texture;
                var png = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                try
                {
                    png.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); png.Apply();
                    File.WriteAllBytes(output, ImageConversion.EncodeToPNG(png));
                }
                finally { UnityEngine.Object.Destroy(png); }
                return output;
            }
            finally { RenderTexture.active = previous; texture.Release(); UnityEngine.Object.Destroy(texture); }
        }

        private static List<Dictionary<string, object>> RotorSweep(Ship ship, Aircraft aircraft)
        {
            Transform visual = ship.transform.Find("ResoluteVisual");
            RotorShaft[] shafts = aircraft.GetAllParts().Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<RotorShaft>(true)).Distinct().ToArray();
            var result = new List<Dictionary<string, object>>();
            foreach (RotorShaft shaft in shafts)
            {
                Transform hub = CarrierIntegration.Read<Transform>(shaft, "hubRotator");
                float radius = CarrierIntegration.Read<float>(shaft, "bladeLength") + CarrierIntegration.Read<float>(shaft, "hubRadius");
                var obstructions = new List<object>();
                for (int i = 0; i < 72; i++)
                {
                    float angle = i * Mathf.PI * 2f / 72f;
                    Vector3 ray = hub.right * Mathf.Cos(angle) + hub.forward * Mathf.Sin(angle);
                    RaycastHit[] hits = Physics.RaycastAll(hub.position, ray, radius, PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore);
                    foreach (RaycastHit hit in hits.Where(h => h.collider.attachedRigidbody == ship.rb))
                        obstructions.Add(new Dictionary<string, object>
                        { ["angleDegrees"] = i * 5f, ["collider"] = hit.collider.name, ["distanceMetres"] = hit.distance, ["hitInSource"] = V(visual.InverseTransformPoint(hit.point)) });
                }
                foreach (Collider hit in Physics.OverlapSphere(hub.position, .05f, PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore).Where(c => c.attachedRigidbody == ship.rb))
                    obstructions.Add(new Dictionary<string, object> { ["collider"] = hit.name, ["hubInsideCollider"] = true });
                result.Add(new Dictionary<string, object>
                { ["shaft"] = shaft.name, ["hubInSource"] = V(visual.InverseTransformPoint(hub.position)), ["sweepRadiusMetres"] = radius,
                  ["axisInSource"] = V(visual.InverseTransformDirection(hub.up)), ["sampledRays"] = 72, ["shipObstructions"] = obstructions });
            }
            return result;
        }

        private static List<object> Capture(Ship ship, Aircraft aircraft, Transform visual, Vector3 deckPoint, string folder)
        {
            var files = new List<object>();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return files;
            Directory.CreateDirectory(folder);
            Camera camera = new GameObject("Resolute.CarrierProofCamera").AddComponent<Camera>();
            camera.enabled = false; camera.nearClipPlane = .1f; camera.farClipPlane = 30000; camera.fieldOfView = 45;
            camera.allowHDR = true; camera.clearFlags = CameraClearFlags.Skybox; camera.cullingMask = ~0;
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true; data.volumeLayerMask = ~0;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
            RenderTexture texture = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            texture.Create(); camera.targetTexture = texture;
            RenderTexture previous = RenderTexture.active;
            LODGroup[] lods = ship.GetComponentsInChildren<LODGroup>().Concat(aircraft.GetComponentsInChildren<LODGroup>()).ToArray();
            try
            {
                foreach (LODGroup lod in lods) lod.ForceLOD(0);
                string[] views = { "deck_side", "deck_top" };
                Vector3[] offsets = { new Vector3(32, 5, -12), new Vector3(0, 45, 0) };
                for (int i = 0; i < views.Length; i++)
                {
                    camera.transform.position = visual.TransformPoint(deckPoint + offsets[i]);
                    camera.transform.LookAt(visual.TransformPoint(deckPoint + Vector3.up * 1.5f), i == 1 ? ship.transform.forward : Vector3.up);
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = texture };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new InvalidOperationException("Carrier proof capture is unsupported by the active render pipeline.");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = texture;
                    var png = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                    try
                    {
                        png.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); png.Apply();
                        string path = Path.Combine(folder, "resolute_" + aircraft.definition.jsonKey + "_" + views[i] + ".png");
                        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(png));
                        files.Add(new Dictionary<string, object> { ["view"] = views[i], ["file"] = path });
                    }
                    finally { UnityEngine.Object.Destroy(png); }
                }
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (LODGroup lod in lods) if (lod != null) lod.ForceLOD(-1);
                camera.targetTexture = null; texture.Release();
                UnityEngine.Object.Destroy(texture); UnityEngine.Object.Destroy(camera.gameObject);
            }
            return files;
        }

        private static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }
        private static void Save(string output, Dictionary<string, object> report) { File.WriteAllText(output, Audit.Json(report)); }
        private static void Require(List<object> checks, string name, bool condition)
        {
            Check(checks, name, condition);
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static void Check(List<object> checks, string name, bool condition)
        { checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition }); }
    }
}
