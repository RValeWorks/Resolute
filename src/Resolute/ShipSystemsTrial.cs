using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Resolute
{
    internal static class ShipSystemsTrial
    {
        internal static IEnumerator Run(Ship subject, Dictionary<string, object> report, List<object> checks, string output)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game")
                throw new InvalidOperationException("Ship systems diagnostics require the isolated test-game copy.");
            report["phase"] = "screw-pitch-and-rotation";
            IEnumerator screws = ObserveScrews(subject, report, checks, output);
            while (screws.MoveNext()) yield return screws.Current;

            var cases = new List<object>();
            report["magazineCookoff"] = new Dictionary<string, object>
            {
                ["scope"] = "Fresh native-spawned offline server ships. Native TakeDamage triggers loaded, actually fired, empty-inventory, and last-round controls. Empty and last-round controls assign starting ammunition as explicit fixtures. Existing native WeaponStation references and native MissileLauncher.Fire are exercised. Cookoff is an added Resolute feature; native Dynamo has generic compartment fire/fragments, not ammunition-aware detonations. Multiplayer observer synchronization is not exercised here.",
                ["cases"] = cases, ["maximumConventionalYieldKgPerCompartment"] = 1500f
            };
            var used = new List<Vector3>();
            foreach (string mode in new[] { "loaded", "partially-spent", "empty", "last-round" })
            {
                report["phase"] = "magazine-" + mode; Save(output, report);
                Vector3 heading;
                GlobalPosition position = SafeSpawn(used, out heading);
                position.y = Datum.SeaLevel.y + subject.definition.spawnOffset.y;
                var saved = new SavedShip("resolute_magazine_" + mode)
                {
                    type = subject.definition.jsonKey, faction = subject.NetworkHQ.faction.factionName,
                    globalPosition = position, rotation = Quaternion.LookRotation(heading), skill = 1, holdPosition = true
                };
                Ship ship;
                Require(checks, "magazine-" + mode + "-native-spawn", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship));
                ship.LinkSavedUnit(saved); MissionTrial.HoldControllers(ship);
                yield return null; yield return new WaitForFixedUpdate();
                Require(checks, "magazine-" + mode + "-server-authority", ship.IsServer && ship.LocalSim);
                ResoluteVlsLauncher[] launchers = ship.GetComponentsInChildren<ResoluteVlsLauncher>();
                Require(checks, "magazine-" + mode + "-all-vls-cells-bound", launchers.Sum(l => l.PhysicalCells.Length) == 276 &&
                    launchers.All(l => l.CellOwners.Length == l.PhysicalCells.Length && l.CellOwners.All(p => p != null)));
                // The partially spent control uses an actual legal Pike ship
                // target; a null-target command is correctly rejected now.
                ResoluteVlsLauncher firing = launchers.Where(l => l.missile.jsonKey == "rsl_ashm")
                    .OrderByDescending(l => l.CellOwners.Distinct().Count()).First();
                if (mode == "last-round") firing = launchers.First(l => !NaturalBallisticSelection.IsBastion(l.info) &&
                    l.info.blastDamage > 0f && l.info.blastDamage <= 200f);
                ShipPart part = firing.CellOwners[firing.CurrentCell];
                ResoluteMagazineCookoff magazine = part.GetComponent<ResoluteMagazineCookoff>();
                Require(checks, "magazine-" + mode + "-physical-owner", magazine != null && magazine.Part == part &&
                    magazine.Launchers.All(l => Enumerable.Range(0, l.PhysicalCells.Length)
                        .Where(i => l.CellOwners[i] == part).All(i => l.PhysicalCells[i].GetComponentInParent<ShipPart>() == part)));
                string partName = part.name;
                int actualShots = 0;
                if (mode == "partially-spent")
                {
                    WeaponStation station = ship.weaponStations.Single(s => s.Weapons.Contains(firing));
                    var launchFixture = new MissionTrial.LaunchTargetFixture();
                    IEnumerator prepareTarget = MissionTrial.PrepareLaunchTarget(ship, station, null, launchFixture, "magazine_partially_spent");
                    while (prepareTarget.MoveNext()) yield return prepareTarget.Current;
                    float interval = NaturalArmament.Get<float>(firing, "fireInterval");
                    float readyAt = Time.time + Mathf.Max(0f, interval - (Time.timeSinceLevelLoad - NaturalArmament.Get<float>(firing, "lastFired"))) + .1f;
                    while (Time.time < readyAt) yield return null;
                    var beforeMissiles = new HashSet<Missile>(UnitRegistry.allUnits.OfType<Missile>());
                    int ammoBefore = firing.ammo, cellBefore = firing.CurrentCell;
                    float ageBefore = Time.timeSinceLevelLoad - NaturalArmament.Get<float>(firing, "lastFired");
                    launchFixture.Evidence["precommandGate"] = MissionTrial.LaunchGateEvidence(ship, station, launchFixture.Target);
                    firing.Fire(ship, launchFixture.Target, ship.rb.velocity, station, default(GlobalPosition));
                    actualShots = ammoBefore - firing.ammo;
                    Missile[] immediate = UnitRegistry.allUnits.OfType<Missile>().Where(m => !beforeMissiles.Contains(m)).ToArray();
                    bool observedOwnedShot = immediate.Any(m => m.ownerID == ship.persistentID);
                    bool observedTargetedShot = immediate.Any(m => m.ownerID == ship.persistentID && m.targetID == launchFixture.Target.persistentID);
                    string[] immediateIDs = immediate.Select(m => m.persistentID.ToString()).ToArray();
                    yield return null;
                    Missile[] newMissiles = UnitRegistry.allUnits.OfType<Missile>().Where(m => !beforeMissiles.Contains(m)).ToArray();
                    observedOwnedShot |= newMissiles.Any(m => m.ownerID == ship.persistentID);
                    observedTargetedShot |= newMissiles.Any(m => m.ownerID == ship.persistentID && m.targetID == launchFixture.Target.persistentID);
                    ((Dictionary<string, object>)report["magazineCookoff"])["nativeSpentRoundProbe"] = new Dictionary<string, object>
                    {
                        ["launcher"] = firing.name, ["weapon"] = firing.info.name, ["station"] = station.Number,
                        ["legalTargetFixture"] = launchFixture.Evidence,
                        ["ammoBefore"] = ammoBefore, ["ammoAfterFire"] = ammoBefore - actualShots, ["ammoAfterFrame"] = firing.ammo,
                        ["cellBefore"] = cellBefore, ["cellAfter"] = firing.CurrentCell, ["firedCellOccupied"] = firing.IsOccupied(cellBefore),
                        ["fireInterval"] = interval, ["secondsSinceLastFireBeforeCommand"] = ageBefore,
                        ["ownerLocalSim"] = ship.LocalSim, ["ownerServer"] = ship.IsServer, ["ownerDisabled"] = ship.disabled,
                        ["immediateMissileIDs"] = immediateIDs,
                        ["missilesAfterFrame"] = newMissiles.Select(m => new Dictionary<string, object>
                        { ["id"] = m.persistentID.ToString(), ["ownerID"] = m.ownerID.ToString(), ["definition"] = m.definition != null ? m.definition.jsonKey : null }).ToArray(),
                        ["observedNativeOwnedShot"] = observedOwnedShot, ["observedNativeTargetedShot"] = observedTargetedShot
                    };
                    Save(output, report);
                    Require(checks, "magazine-partially-spent-native-shot", actualShots == 1 && !firing.IsOccupied(cellBefore) &&
                        observedOwnedShot && observedTargetedShot);
                    // Native StartMissile awaits UniTask.Yield(FixedUpdate),
                    // then reads both the missile and its owner's transform/rb.
                    // Keep both alive through that callback before fixture cleanup.
                    float beforeStartupPhysics = Time.fixedTime;
                    yield return new WaitForFixedUpdate();
                    yield return null;
                    ((Dictionary<string, object>)((Dictionary<string, object>)report["magazineCookoff"])["nativeSpentRoundProbe"])
                        ["cleanupAfterNativeStartup"] = new Dictionary<string, object>
                        {
                            ["nativeWait"] = "Missile.StartMissile: UniTask.Yield(PlayerLoopTiming.FixedUpdate)",
                            ["fixedTimeBeforeWait"] = beforeStartupPhysics, ["fixedTimeBeforeCleanup"] = Time.fixedTime,
                            ["additionalNormalFrame"] = true
                        };
                    foreach (Missile missile in immediate.Concat(newMissiles).Distinct())
                        if (missile != null && missile.ownerID == ship.persistentID)
                            NaturalWeaponsTrial.RetireMissileFixture(missile, true);
                    launchFixture.Retire();
                    Save(output, report);
                }
                else if (mode == "empty")
                {
                    foreach (ResoluteVlsLauncher launcher in magazine.Launchers) launcher.ammo = 0;
                }
                else if (mode == "last-round")
                {
                    foreach (ResoluteVlsLauncher launcher in launchers) launcher.ammo = 0;
                    firing.ammo = 1;
                }
                if (mode == "empty" || mode == "last-round")
                {
                    // Assigning fixture inventory bypasses the native station's
                    // normal firing/rearming notification. Establish correct
                    // starting totals before testing damage-driven accounting.
                    foreach (WeaponStation station in ship.weaponStations.Where(s => s.Weapons.Any(w => w is ResoluteVlsLauncher)))
                    { station.AccountAmmo(); station.Updated(); }
                }
                int beforeRounds = CountOwned(magazine, part);
                int totalBefore = launchers.Sum(l => l.ammo);
                var untouched = new Dictionary<ResoluteVlsLauncher, bool[]>();
                foreach (ResoluteVlsLauncher launcher in launchers)
                    untouched[launcher] = Enumerable.Range(0, launcher.PhysicalCells.Length).Select(launcher.IsOccupied).ToArray();
                var rotations = launchers.SelectMany(l => l.PhysicalCells).ToDictionary(t => t, t => t.localRotation);
                float healthBefore = part.hitPoints;
                var wavesBefore = new HashSet<Shockwave>(Resources.FindObjectsOfTypeAll<Shockwave>());
                Damage(part, 10f, subject.persistentID);
                Require(checks, "magazine-" + mode + "-nonlethal-control", part.hitPoints > 0f && !magazine.Triggered &&
                    magazine.ScheduledBursts == 0 && CountOwned(magazine, part) == beforeRounds);
                Damage(part, part.hitPoints + 1f, subject.persistentID);
                int totalAfter = launchers.Sum(l => l.ammo);
                bool otherCellsRetained = launchers.All(l => Enumerable.Range(0, l.PhysicalCells.Length)
                    .Where(i => l.CellOwners[i] != part).All(i => l.IsOccupied(i) == untouched[l][i]));
                Require(checks, "magazine-" + mode + "-only-owned-live-rounds-consumed", magazine.ConsumedRounds == beforeRounds &&
                    totalBefore - totalAfter == beforeRounds && CountOwned(magazine, part) == 0 && otherCellsRetained);
                Require(checks, "magazine-" + mode + "-native-station-accounting", ship.weaponStations.Where(s => s.Weapons.Any(w => w is ResoluteVlsLauncher))
                    .All(s => s.Ammo == s.Weapons.Sum(w => w.GetAmmoTotal())));
                Require(checks, "magazine-" + mode + "-native-attacker-attribution", magazine.DamageSource == subject.persistentID);
                Damage(part, 1f, subject.persistentID); Damage(part, 1f, subject.persistentID);
                int expectedBursts = beforeRounds > 0 ? 1 : 0;
                Require(checks, "magazine-" + mode + "-exactly-once-scheduled", magazine.ScheduledBursts == expectedBursts && magazine.ConsumedRounds == beforeRounds);
                if (mode == "loaded")
                    Require(checks, "magazine-loss-does-not-open-launch-hatches", rotations.All(p => Quaternion.Angle(p.Value, p.Key.localRotation) < .01f));
                // Exhausted structural cells remain unavailable to native rearming.
                foreach (ResoluteVlsLauncher launcher in magazine.Launchers)
                {
                    WeaponStation station = ship.weaponStations.Single(s => s.Weapons.Contains(launcher));
                    launcher.Rearm(276, station);
                    Require(checks, "magazine-" + mode + "-ruined-cells-cannot-rearm-" + launcher.name,
                        launcher.GetAmmoTotal() <= launcher.PhysicalCells.Length - launcher.RuinedCells);
                }
                yield return new WaitForFixedUpdate(); yield return null;
                Require(checks, "magazine-" + mode + "-one-server-native-burst", magazine.EmittedBursts == expectedBursts &&
                    magazine.ServerDamageBatches == expectedBursts && magazine.ConventionalYieldKg <= 1500f);
                Shockwave[] createdWaves = Resources.FindObjectsOfTypeAll<Shockwave>().Where(w => !wavesBefore.Contains(w)).ToArray();
                if (mode == "last-round")
                    Require(checks, "last-round-small-warhead-has-no-duplicate-large-shockwave", beforeRounds == 1 &&
                        magazine.ConventionalYieldKg <= 200f && createdWaves.All(w => NaturalArmament.Get<float>(w, "yieldKilotons") < .0002f));
                else if (expectedBursts > 0)
                    Require(checks, "magazine-" + mode + "-native-shockwave-has-correct-yield-and-owner", createdWaves.Any(w =>
                        Mathf.Abs(NaturalArmament.Get<float>(w, "yieldKilotons") * 1000000f - magazine.ConventionalYieldKg) < .1f &&
                        NaturalArmament.Get<PersistentID>(w, "ownerID") == subject.persistentID));
                var record = new Dictionary<string, object>
                {
                    ["case"] = mode, ["compartment"] = partName, ["nativeShotsBeforeDamage"] = actualShots,
                    ["healthBefore"] = healthBefore, ["loadedOwnedRoundsBefore"] = beforeRounds,
                    ["consumedRounds"] = magazine.ConsumedRounds, ["otherCompartmentCellsRetainedAtTrigger"] = otherCellsRetained,
                    ["scheduledBursts"] = magazine.ScheduledBursts, ["emittedBursts"] = magazine.EmittedBursts,
                    ["serverDamageBatches"] = magazine.ServerDamageBatches, ["conventionalYieldKg"] = magazine.ConventionalYieldKg,
                    ["dealerID"] = magazine.DamageSource.ToString(), ["spawn"] = new[] { position.x, position.y, position.z },
                    ["nativeShockwavesCreated"] = createdWaves.Select(w => new Dictionary<string, object>
                    {
                        ["name"] = w.name, ["yieldKg"] = NaturalArmament.Get<float>(w, "yieldKilotons") * 1000000f,
                        ["ownerID"] = NaturalArmament.Get<PersistentID>(w, "ownerID").ToString()
                    }).ToArray()
                };
                cases.Add(record); Save(output, report);
                if (mode == "loaded")
                {
                    float until = Time.time + .2f;
                    while (Time.time < until) yield return null;
                    record["captures"] = AssembledPreview.Capture(ship, Path.Combine(Path.GetDirectoryName(output), "magazine_loaded_cookoff"));
                    Save(output, report);
                }
                NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(ship.Identity, true);
                yield return null;
            }
            report["phase"] = "ship-systems-complete"; Save(output, report);
        }

        private static int CountOwned(ResoluteMagazineCookoff magazine, ShipPart part)
        {
            return magazine.Launchers.Sum(l => Enumerable.Range(0, l.CellOwners.Length).Count(i => l.CellOwners[i] == part && l.IsOccupied(i)));
        }

        private static void Damage(ShipPart part, float netDamage, PersistentID dealer)
        {
            ArmorProperties armor = part.GetArmorProperties();
            part.TakeDamage(armor.pierceArmor + netDamage * Mathf.Max(armor.pierceTolerance, .01f), 0f, 1f, 0f, 0f, dealer);
        }

        private static GlobalPosition SafeSpawn(List<Vector3> used, out Vector3 heading)
        {
            var existing = UnitRegistry.allUnits.OfType<Ship>().Select(s => s.GlobalPosition().AsVector3()).Concat(used).ToArray();
            foreach (var road in NetworkSceneSingleton<LevelInfo>.i.seaLanes.roads.OrderByDescending(r => r.length))
                for (int i = 1; i < road.points.Count; i++)
                {
                    Vector3 a = road.points[i - 1].AsVector3(), b = road.points[i].AsVector3();
                    a.y = b.y = Datum.SeaLevel.y;
                    float length = Vector3.Distance(a, b); Vector3 forward = (b - a).normalized;
                    for (float distance = 350f; distance < length - 350f; distance += 900f)
                    {
                        Vector3 center = a + forward * distance;
                        if (existing.Any(p => Vector3.Distance(center, p) < 1500f)) continue;
                        bool safe = true;
                        Vector3 side = Vector3.Cross(Vector3.up, forward);
                        for (int x = -1; x <= 1 && safe; x++) for (int z = -1; z <= 1; z++)
                        {
                            RaycastHit hit;
                            if (PathfindingAgent.RaycastTerrain(new GlobalPosition(center + (side * x + forward * z) * 150f), out hit) && hit.point.y > Datum.LocalSeaY - 18f)
                            { safe = false; break; }
                        }
                        if (!safe) continue;
                        heading = forward; used.Add(center); return new GlobalPosition(center);
                    }
                }
            throw new InvalidOperationException("No isolated deep-water position for magazine controls.");
        }

        private static IEnumerator ObserveScrews(Ship ship, Dictionary<string, object> report, List<object> checks, string output)
        {
            ResoluteDeckAnimation animation = ship.GetComponent<ResoluteDeckAnimation>();
            ScrewGeometry.Measurement[] geometry = animation.Propellers.Select(p => ScrewGeometry.Measure(p, ship.transform)).ToArray();
            var phases = new List<object>();
            report["screwVerification"] = new Dictionary<string, object>
            {
                ["scope"] = "Actual native-spawned world transforms and triangle surfaces determine handedness. Ahead, neutral and astern throttle fixtures drive the production Update animation with native propulsion enabled. This validates blade pitch and motion direction, not a hydrodynamic model. Shaft axis follows authored local Z; parents, scale and winding are included.",
                ["geometry"] = geometry.Select((m, i) => m.Json(animation.Propellers[i].name)).ToArray(), ["phases"] = phases
            };
            Require(checks, "four-screws-have-coherent-forward-pitch", geometry.Length == 4 && geometry.All(m => m.AgreeingArea / m.SelectedArea > .9f));
            Require(checks, "configured-screw-directions-match-actual-blades", geometry.Select((g, i) => g.ForwardRotation == animation.ForwardRotation[i]).All(v => v));
            ShipAI ai = ship.GetComponent<ShipAI>(); bool aiWasEnabled = ai != null && ai.enabled;
            ShipInputs inputs = ship.GetInputs(); float priorThrottle = inputs.throttle;
            if (ai != null) ai.enabled = false;
            try
            {
                foreach (float throttle in new[] { .7f, 0f, -.7f })
                {
                    string phase = throttle > 0 ? "ahead" : throttle < 0 ? "astern" : "neutral";
                    inputs.throttle = throttle;
                    yield return null;
                    Quaternion[] before = animation.Propellers.Select(p => p.localRotation).ToArray();
                    float start = Time.time;
                    while (Time.time - start < .16f) { inputs.throttle = throttle; yield return null; }
                    var motions = new List<object>(); bool correct = true;
                    for (int i = 0; i < before.Length; i++)
                    {
                        Quaternion change = Quaternion.Inverse(before[i]) * animation.Propellers[i].localRotation;
                        float angle; Vector3 axis; change.ToAngleAxis(out angle, out axis);
                        if (angle > 180f) angle -= 360f;
                        float signed = angle == 0f ? 0f : angle * Mathf.Sign(axis.z);
                        bool valid = throttle == 0f ? Quaternion.Angle(before[i], animation.Propellers[i].localRotation) < .05f :
                            signed * geometry[i].ForwardReaction * throttle > 0f && Mathf.Abs(axis.z) > .98f && Mathf.Abs(signed) > 1f;
                        correct &= valid;
                        motions.Add(new Dictionary<string, object> { ["propeller"] = animation.Propellers[i].name, ["signedLocalRotationDegrees"] = signed, ["passed"] = valid });
                    }
                    phases.Add(new Dictionary<string, object> { ["throttle"] = throttle, ["phase"] = phase, ["seconds"] = Time.time - start, ["motion"] = motions });
                    Save(output, report); Require(checks, "blade-pitch-correct-" + phase + "-rotation", correct);
                }
                ((Dictionary<string, object>)report["screwVerification"])["captures"] = CaptureScrews(ship, output, report);
                Save(output, report);
            }
            finally { inputs.throttle = priorThrottle; if (ai != null) ai.enabled = aiWasEnabled; }
        }

        private static object CaptureScrews(Ship ship, string output, Dictionary<string, object> report)
        {
            var captures = new List<object>();
            var proof = new List<object>(); report["screwCaptureGeometryProof"] = proof;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return captures;
            string folder = Path.Combine(Path.GetDirectoryName(output), "screw_pitch"); Directory.CreateDirectory(folder);
            Camera camera = new GameObject("Resolute.ScrewProofCamera").AddComponent<Camera>();
            camera.enabled = false; camera.nearClipPlane = .1f; camera.farClipPlane = 1000f; camera.fieldOfView = 45f;
            camera.clearFlags = CameraClearFlags.Skybox;
            var target = new RenderTexture(1600, 1000, 24); target.Create(); camera.targetTexture = target;
            RenderTexture previous = RenderTexture.active;
            try
            {
                foreach (Vector3 offset in new[] { new Vector3(0f, -18f, -137f), new Vector3(25f, -18f, -112f) })
                {
                    camera.transform.position = ship.transform.TransformPoint(offset);
                    camera.transform.LookAt(ship.transform.TransformPoint(new Vector3(0f, -14f, -98f)), ship.transform.up);
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                    RenderPipeline.SubmitRenderRequest(camera, request); RenderTexture.active = target;
                    Texture2D png = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    try
                    {
                        png.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); png.Apply();
                        string file = Path.Combine(folder, "screws_" + captures.Count + ".png");
                        File.WriteAllBytes(file, ImageConversion.EncodeToPNG(png)); captures.Add(file);
                        proof.Add(new Dictionary<string, object>
                        {
                            ["file"] = file, ["cameraWorldPosition"] = V(camera.transform.position), ["cameraLocalToShip"] = V(offset),
                            ["cameraWorldForward"] = V(camera.transform.forward), ["cameraFieldOfView"] = camera.fieldOfView,
                            ["nearClip"] = camera.nearClipPlane, ["farClip"] = camera.farClipPlane,
                            ["lowerImageRays"] = new[] { new Vector2(.3f, .03f), new Vector2(.6f, .04f), new Vector2(.75f, .07f) }
                                .Select(point => RayProof(camera, point)).ToArray()
                        });
                    }
                    finally { UnityEngine.Object.Destroy(png); }
                }
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = null; target.Release();
                UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(camera.gameObject);
            }
            return captures;
        }

        private static object RayProof(Camera camera, Vector2 viewport)
        {
            Ray ray = camera.ViewportPointToRay(viewport);
            var triangles = new List<Tuple<float, object>>();
            var unreadable = new List<object>();
            foreach (MeshRenderer renderer in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || !renderer.gameObject.scene.IsValid() ||
                    (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 || !renderer.bounds.IntersectRay(ray)) continue;
                Mesh mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                if (!mesh.isReadable)
                {
                    unreadable.Add(new Dictionary<string, object> { ["renderer"] = Hierarchy(renderer.transform), ["mesh"] = mesh.name,
                        ["layer"] = renderer.gameObject.layer, ["boundsCenter"] = V(renderer.bounds.center), ["boundsSize"] = V(renderer.bounds.size) });
                    continue;
                }
                Matrix4x4 inverse = renderer.transform.worldToLocalMatrix;
                Vector3 origin = inverse.MultiplyPoint3x4(ray.origin), direction = inverse.MultiplyVector(ray.direction);
                Vector3[] vertices = mesh.vertices; int[] indices = mesh.triangles;
                float closest = float.PositiveInfinity; int triangle = -1;
                for (int i = 0; i < indices.Length; i += 3)
                {
                    float distance;
                    if (RayTriangle(origin, direction, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], out distance) && distance < closest)
                    { closest = distance; triangle = i / 3; }
                }
                if (triangle < 0 || closest > camera.farClipPlane) continue;
                triangles.Add(Tuple.Create(closest, (object)new Dictionary<string, object>
                {
                    ["renderer"] = Hierarchy(renderer.transform), ["mesh"] = mesh.name, ["layer"] = renderer.gameObject.layer,
                    ["distance"] = closest, ["worldPoint"] = V(ray.GetPoint(closest)), ["triangle"] = triangle,
                    ["materials"] = renderer.sharedMaterials.Select(m => m != null ? m.name + " | " + m.shader.name : "null").ToArray()
                }));
            }
            return new Dictionary<string, object>
            {
                ["viewport"] = new[] { viewport.x, viewport.y }, ["origin"] = V(ray.origin), ["direction"] = V(ray.direction),
                ["colliderHits"] = Physics.RaycastAll(ray, camera.farClipPlane, ~0, QueryTriggerInteraction.Collide).OrderBy(h => h.distance)
                    .Select(h => new Dictionary<string, object> { ["object"] = Hierarchy(h.collider.transform), ["colliderType"] = h.collider.GetType().Name,
                        ["layer"] = h.collider.gameObject.layer, ["distance"] = h.distance, ["point"] = V(h.point),
                        ["unitPart"] = h.collider.GetComponent<UnitPart>()?.name }).ToArray(),
                ["readableRenderedMeshHits"] = triangles.OrderBy(t => t.Item1).Take(8).Select(t => t.Item2).ToArray(),
                ["unreadableRendererBoundsIntersectingRay"] = unreadable
            };
        }

        private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f; Vector3 first = b - a, second = c - a, h = Vector3.Cross(direction, second);
            float determinant = Vector3.Dot(first, h);
            if (Mathf.Abs(determinant) < .0000001f) return false;
            float inverse = 1f / determinant; Vector3 s = origin - a;
            float u = inverse * Vector3.Dot(s, h); if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, first); float v = inverse * Vector3.Dot(direction, q);
            if (v < 0f || u + v > 1f) return false;
            distance = inverse * Vector3.Dot(second, q); return distance >= 0f;
        }

        private static string Hierarchy(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }
        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };

        private static void Require(List<object> checks, string name, bool condition)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report) { File.WriteAllText(output, Audit.Json(report)); }
    }
}
