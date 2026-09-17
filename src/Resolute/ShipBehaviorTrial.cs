using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NuclearOption.SavedMission;
using UnityEngine;

namespace Resolute
{
    // Only invoked by the explicitly selected, isolated mission trial. This uses
    // a fresh native-spawned subject so weapon tests and nearby target ships do
    // not contaminate the speed and destructive-damage measurements.
    internal static class ShipBehaviorTrial
    {
        private sealed class SpeedSample
        {
            internal float Seconds, Speed, Throttle, Steering;
            internal object Navigation;
            internal object Json()
            {
                return new Dictionary<string, object> { ["simulationSeconds"] = Seconds, ["speedMps"] = Speed,
                    ["speedKnots"] = Speed / .5144444f, ["throttle"] = Throttle, ["steering"] = Steering,
                    ["navigation"] = Navigation };
            }
        }

        internal static IEnumerator Run(Ship subject, Dictionary<string, object> report, List<object> checks, string output)
        {
            report["phase"] = "source-structure-and-speed"; Save(output, report);
            GlobalPosition spawn, destination;
            Vector3 heading;
            FindTestLeg(out spawn, out destination, out heading);
            spawn.y = Datum.SeaLevel.y + subject.definition.spawnOffset.y;
            var saved = new SavedShip("resolute_behavior_trial")
            {
                type = subject.definition.jsonKey, faction = subject.NetworkHQ.faction.factionName,
                globalPosition = spawn, rotation = Quaternion.LookRotation(heading, Vector3.up), skill = 1, holdPosition = true
            };
            Ship ship;
            Require(checks, "behavior-subject-normal-spawn", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship));
            ship.LinkSavedUnit(saved);
            MissionTrial.HoldControllers(ship);
            // Give the native AI its real route before its first Update. Leaving
            // a fresh ship at its initial destination for even one frame starts
            // ShipAI's 120-second holding timer, which later cancels the command
            // and permits retargeting to leftover hostile tracks in a full trial.
            ship.SetHoldPosition(false);
            ship.UnitCommand.SetDestination(destination, false);
            ShipAI navigationAI = ship.GetComponent<ShipAI>();
            Require(checks, "behavior-native-destination-command", navigationAI != null && navigationAI.state == ShipAI.ShipAIState.navigating);
            yield return null; yield return new WaitForFixedUpdate();

            Transform source = ship.transform.Find("ResoluteVisual");
            MeshRenderer[] bodies = ship.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r.name.StartsWith("rsl_hull_", StringComparison.Ordinal)).ToArray();
            var structure = new Dictionary<string, object>
            {
                ["subject"] = ship.persistentID.ToString(), ["spawn"] = V(spawn.AsVector3()),
                ["destination"] = V(destination.AsVector3()),
                ["sections"] = bodies.Select(r => new Dictionary<string, object>
                {
                    ["name"] = r.name, ["owner"] = r.GetComponentInParent<ShipPart>().name,
                    ["surfaceTriangles"] = (r.GetComponent<MeshFilter>().sharedMesh.GetTriangles(0).Length +
                        r.GetComponent<MeshFilter>().sharedMesh.GetTriangles(1).Length) / 3,
                    ["interiorTriangles"] = InteriorTriangles(r), ["enabled"] = r.enabled,
                    ["interiorFacesPresentWhileIntact"] = Section(r).InteriorFaces.All(f => f.enabled)
                }).ToArray()
            };
            report["structuralGeometry"] = structure;
            Require(checks, "configured-physical-hull-sections", bodies.Length == StructuralGeometry.ExpectedCompartmentCount);
            Require(checks, "structural-sections-have-interior-faces", bodies.All(r => InteriorTriangles(r) > 0 && r.enabled));
            Require(checks, "interior-faces-present-before-damage", bodies.All(r => Section(r).InteriorFaces.All(f => f.enabled)));
            Require(checks, "vertical-superstructure-columns-supported-by-hull", new[] { "Hull_ExhaustStack", "Hull_Bridge", "Hull_Radar", "Hull_UpperStarboard" }
                .All(name => Named(ship, name).parent == ship.transform));
            Require(checks, "aft-roof-supported-by-hangar-deck", new[] { "Hull_hangarRoof", "Hull_hangarRoofStarboard" }
                .All(name => Named(ship, name).parent == Named(ship, "Hull_hangarFloor")));
            Require(checks, "full-height-aft-section-supports-stern", Named(ship, "Hull_hangarFloor").parent == Named(ship, "Hull_CRAft"));
            Dictionary<string, object> collision = InspectHullCollision(ship, source, bodies);
            structure["hullCollisionSamples"] = collision;
            Save(output, report);
            Require(checks, "fitted-hull-collision-has-surface-coverage", (bool)collision["allSamplesHitSubject"] && (int)collision["count"] >= 24);
            Require(checks, "fitted-hull-collision-within-eighty-centimetres", (float)collision["maxSurfaceErrorMetres"] < .8f);

            // Use the same destination command a player gives the ship. Native
            // ShipAI, engine thrust, water drag and rigidbody physics run freely.
            var samples = new List<SpeedSample>();
            var speedReport = new Dictionary<string, object>
            {
                ["scope"] = "Fresh undamaged ship, native destination command, normal AI steering and native water physics. Weapons held by the isolated trial gates.",
                ["designSpeedKnots"] = 35f, ["samples"] = new object[0]
            };
            report["steadySpeed"] = speedReport;
            float simStarted = Time.time, realStarted = Time.realtimeSinceStartup, nextSample = simStarted;
            bool steady = false;
            while (Time.time - simStarted < 240 && Time.realtimeSinceStartup - realStarted < 480)
            {
                if (ship == null || ship.disabled) throw new InvalidOperationException("The speed-test ship became disabled.");
                if (Time.time >= nextSample)
                {
                    ShipInputs inputs = ship.GetInputs();
                    samples.Add(new SpeedSample { Seconds = Time.time - simStarted,
                        Speed = Vector3.ProjectOnPlane(ship.rb.velocity, Vector3.up).magnitude,
                        Throttle = inputs.throttle, Steering = inputs.steering, Navigation = NavigationSnapshot(ship, destination) });
                    nextSample = Time.time + 5;
                    speedReport["samples"] = samples.Select(s => s.Json()).ToArray();
                    speedReport["simulationSeconds"] = Time.time - simStarted;
                    speedReport["realtimeSeconds"] = Time.realtimeSinceStartup - realStarted;
                    Save(output, report);
                    if (samples.Count >= 8 && Time.time - simStarted >= 100)
                    {
                        SpeedSample[] tail = samples.Skip(samples.Count - 7).ToArray();
                        steady = tail.All(s => s.Throttle > .97f && Mathf.Abs(s.Steering) < .04f) &&
                            tail.Min(s => s.Speed) > 10 && tail.Max(s => s.Speed) - tail.Min(s => s.Speed) < .05f;
                        if (steady) break;
                    }
                }
                yield return null;
            }
            SpeedSample[] finalSamples = samples.Skip(Mathf.Max(0, samples.Count - 7)).ToArray();
            float measuredKnots = finalSamples.Average(s => s.Speed) / .5144444f;
            speedReport["measuredSteadyKnots"] = measuredKnots;
            speedReport["measuredSteadyKmh"] = measuredKnots * 1.852f;
            speedReport["steadyWindowSeconds"] = finalSamples.Last().Seconds - finalSamples.First().Seconds;
            speedReport["steadyWindowSpeedRangeMps"] = finalSamples.Max(s => s.Speed) - finalSamples.Min(s => s.Speed);
            speedReport["steady"] = steady;
            Save(output, report);
            Require(checks, "steady-native-full-throttle-window", steady);
            Require(checks, "measured-thirty-five-knot-speed", measuredKnots >= 34.8f && measuredKnots <= 35.6f);

            ResoluteDeckAnimation animation = ship.GetComponent<ResoluteDeckAnimation>();
            Quaternion[] propBefore = animation.Propellers.Select(p => p.localRotation).ToArray();
            float propStart = Time.time;
            while (Time.time - propStart < .12f) yield return null;
            var propDeltas = new List<object>();
            bool opposite = true;
            for (int i = 0; i < animation.Propellers.Length; i++)
            {
                Transform prop = animation.Propellers[i];
                Quaternion relative = Quaternion.Inverse(propBefore[i]) * prop.localRotation;
                float angle; Vector3 axis;
                relative.ToAngleAxis(out angle, out axis);
                if (angle > 180) angle -= 360;
                float signed = angle * Mathf.Sign(axis.z);
                float expectedSign = prop.name.EndsWith("_p", StringComparison.Ordinal) ? 1 : -1;
                opposite &= signed * expectedSign > 1 && Mathf.Abs(axis.z) > .98f;
                propDeltas.Add(new Dictionary<string, object> { ["propeller"] = prop.name, ["signedRotationDegrees"] = signed });
            }
            speedReport["pairedPropellerRotation"] = propDeltas;
            Require(checks, "paired-propellers-counter-rotate", opposite);
            ship.SetHoldPosition(true);
            float brakeStart = Time.time;
            while (Vector3.ProjectOnPlane(ship.rb.velocity, Vector3.up).magnitude > .5f && Time.time - brakeStart < 60) yield return null;

            report["phase"] = "native-structural-breakup"; Save(output, report);
            var breakup = new List<object>(); report["structuralBreakup"] = breakup;
            foreach (string partName in new[] { "Hull_ExhaustStack", "Hull_CFFF" })
            {
                ShipPart part = Named(ship, partName).GetComponent<ShipPart>();
                MeshRenderer body = part.GetComponentsInChildren<MeshRenderer>(true)
                    .Single(r => r.name == "rsl_hull_" + partName);
                float before = part.hitPoints;
                // Exceed both the structural threshold and the tower's native
                // integrity threshold through the real damage entry point.
                float requiredNet = Mathf.Max(before - part.GetStructuralThreshold() + 100, 550);
                ArmorProperties armor = part.GetArmorProperties();
                float pierce = armor.pierceArmor + requiredNet * Mathf.Max(armor.pierceTolerance, .01f);
                part.TakeDamage(pierce, 0, 1, 0, 0, default(PersistentID));
                yield return new WaitForFixedUpdate(); yield return null;
                var record = new Dictionary<string, object>
                {
                    ["part"] = partName, ["healthBefore"] = before, ["healthAfter"] = part.hitPoints,
                    ["nativeTakeDamagePierce"] = pierce, ["detached"] = part.IsDetached(),
                    ["ownRigidbody"] = part.rb != null && part.rb != ship.rb,
                    ["fragmentMassKg"] = part.rb != null ? part.rb.mass : 0,
                    ["nativeJointPresent"] = part.GetComponent<ConfigurableJoint>() != null,
                    ["bodyStillRendered"] = body != null && body.enabled && body.gameObject.activeInHierarchy,
                    ["releasedPlateCount"] = Section(body).ReleasedCount,
                    ["remainingPlateRenderers"] = Section(body).PlateRenderers.Count(r => r != null && r.gameObject.activeInHierarchy),
                    ["retainedSurfaceFraction"] = Section(body).RetainedSurfaceFraction,
                    ["interiorTriangles"] = InteriorTriangles(body), ["remainingDisplacement"] = part.GetDisplacement(),
                    ["fragmentInteriorFacesVisible"] = Section(body).InteriorFaces.All(f => f.enabled),
                    ["adjacentInteriorFacesVisible"] = Section(body).NeighborFaces.All(f => f.enabled),
                    ["adjacentInteriorFaceCount"] = Section(body).NeighborFaces.Length
                };
                breakup.Add(record); Save(output, report);
                Require(checks, "native-detachment-" + partName, part.IsDetached() && part.rb != null && part.rb != ship.rb && part.rb.mass > 0);
                Require(checks, "visible-closed-fragment-" + partName, body != null && body.enabled && body.gameObject.activeInHierarchy &&
                    Section(body).RetainedSurfaceFraction >= .8f && InteriorTriangles(body) > 0 && Section(body).InteriorFaces.All(f => f.enabled));
                Require(checks, "visible-adjacent-fracture-faces-" + partName,
                    Section(body).NeighborFaces.Length > 0 && Section(body).NeighborFaces.All(f => f.enabled));
                Require(checks, "detached-section-native-flooding-" + partName, part.GetDisplacement() <= .001f);
                if (partName == "Hull_ExhaustStack")
                {
                    ShipPart[] neighbors = new[] { "Hull_Bridge", "Hull_Radar", "Hull_UpperStarboard" }
                        .Select(name => Named(ship, name).GetComponent<ShipPart>()).ToArray();
                    bool coherent = neighbors.All(p => p.transform.IsChildOf(ship.transform) && !p.IsDetached() && p.rb == ship.rb);
                    record["healthyTowerColumnsStayAttached"] = coherent;
                    Require(checks, "healthy-tower-columns-stay-on-native-hull", coherent);
                    Require(checks, "healthy-supported-tower-retains-plating", neighbors.Select(p => p.GetComponent<ResoluteStructuralSection>())
                        .All(s => s.Body.enabled && s.RetainedSurfaceFraction > .99f));
                }
            }
            float observeStarted = Time.time;
            while (Time.time - observeStarted < 5) yield return null;
            report["breakupCaptures"] = AssembledPreview.Capture(ship,
                Path.Combine(Path.GetDirectoryName(output), "game_previews", "structural_breakup"));
            var damagedCamera = new Dictionary<string, object>();
            report["damagedShipCamera"] = damagedCamera;
            IEnumerator follow = CarrierTrial.FollowCamera(ship, damagedCamera, checks, output, report, false, "damaged-ship-spectator");
            while (follow.MoveNext()) yield return follow.Current;
            Save(output, report);
        }

        private static Dictionary<string, object> InspectHullCollision(Ship ship, Transform source, MeshRenderer[] bodies)
        {
            var meshData = bodies.Select(r => new { Filter = r.GetComponent<MeshFilter>(), Mesh = r.GetComponent<MeshFilter>().sharedMesh }).ToArray();
            var samples = new List<object>(); float greatestError = 0; bool valid = true;
            foreach (float z in new[] { -105f, -92f, -70f, -45f, 0f, 40f, 70f, 98f })
            foreach (float y in new[] { 2f, 5f })
            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 origin = source.TransformPoint(new Vector3(side * 40, y, z));
                Vector3 direction = source.TransformDirection(new Vector3(-side, 0, 0));
                float expected = float.PositiveInfinity;
                foreach (var item in meshData)
                {
                    Matrix4x4 local = item.Filter.transform.worldToLocalMatrix;
                    Vector3 p = local.MultiplyPoint3x4(origin), d = local.MultiplyVector(direction);
                    Vector3[] vertices = item.Mesh.vertices; int[] indices = item.Mesh.triangles;
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        float distance;
                        if (Intersect(p, d, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], out distance))
                            expected = Mathf.Min(expected, distance);
                    }
                }
                if (!float.IsFinite(expected)) continue;
                RaycastHit hit;
                bool found = Physics.Raycast(origin, direction, out hit, 80, PhysicsLayers.ShipsMask, QueryTriggerInteraction.Ignore);
                UnitPart hitPart = found ? hit.collider.GetComponent<UnitPart>() : null;
                bool correctShip = hitPart != null && hitPart.parentUnit == ship;
                float error = correctShip ? Mathf.Abs(hit.distance - expected) : 999;
                greatestError = Mathf.Max(greatestError, error);
                valid &= correctShip;
                samples.Add(new Dictionary<string, object> { ["sourcePoint"] = new[] { side * 40, y, z },
                    ["visualSurfaceDistance"] = expected, ["collisionDistance"] = found ? hit.distance : -1,
                    ["errorMetres"] = error, ["part"] = hitPart != null ? hitPart.name : null });
            }
            return new Dictionary<string, object> { ["maxSurfaceErrorMetres"] = greatestError,
                ["allSamplesHitSubject"] = valid, ["count"] = samples.Count, ["samples"] = samples };
        }

        private static bool Intersect(Vector3 p, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0; Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
            float determinant = Vector3.Dot(e1, h);
            if (Mathf.Abs(determinant) < .000001f) return false;
            float inverse = 1 / determinant; Vector3 s = p - a;
            float u = inverse * Vector3.Dot(s, h); if (u < 0 || u > 1) return false;
            Vector3 q = Vector3.Cross(s, e1); float v = inverse * Vector3.Dot(d, q);
            if (v < 0 || u + v > 1) return false;
            distance = inverse * Vector3.Dot(e2, q); return distance >= 0;
        }

        private static int InteriorTriangles(Renderer renderer)
        {
            if (renderer == null) return 0;
            ResoluteStructuralSection section = Section(renderer);
            if (section == null || section.ClosedMesh == null) return 0;
            Mesh mesh = section.ClosedMesh;
            return mesh.subMeshCount > 1 ? mesh.GetTriangles(1).Length / 3 : 0;
        }

        private static ResoluteStructuralSection Section(Renderer renderer)
        { return renderer.GetComponentInParent<ShipPart>().GetComponent<ResoluteStructuralSection>(); }

        private static Transform Named(Ship ship, string name, Transform root = null)
        { return (root != null ? root : ship.transform).GetComponentsInChildren<Transform>(true).Single(t => t.name == name); }

        private static object NavigationSnapshot(Ship ship, GlobalPosition commanded)
        {
            ShipAI ai = ship.GetComponent<ShipAI>();
            if (ai == null) return new Dictionary<string, object> { ["aiMissing"] = true };
            PathfindingAgent path = Read<PathfindingAgent>(ai, "pathfinder");
            var waypoints = path != null ? Read<List<GlobalPosition>>(path, "waypoints") : null;
            var nodes = path != null ? Read<IList>(path, "nodes") : null;
            return new Dictionary<string, object>
            {
                ["globalPosition"] = V(ship.GlobalPosition().AsVector3()),
                ["requestedDestinationRemainingMetres"] = Vector3.Distance(ship.GlobalPosition().AsVector3(), commanded.AsVector3()),
                ["aiEnabled"] = ai.enabled, ["aiState"] = ai.state.ToString(), ["shipHoldPosition"] = ship.holdPosition,
                ["nativeCommandedDestination"] = Read<bool>(ai, "commandedDestination"),
                ["nativeDestination"] = V(Read<GlobalPosition>(ai, "destination").AsVector3()),
                ["waypointCount"] = waypoints != null ? waypoints.Count : 0, ["pathNodeCount"] = nodes != null ? nodes.Count : 0,
                ["nextWaypoints"] = waypoints != null ? waypoints.Take(3).Select(p => V(p.AsVector3())).ToArray() : new float[0][]
            };
        }

        private static T Read<T>(object instance, string field)
        {
            if (instance == null) return default(T);
            FieldInfo info = instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object value = info != null ? info.GetValue(instance) : null;
            return value is T ? (T)value : default(T);
        }

        private static void FindTestLeg(out GlobalPosition spawn, out GlobalPosition destination, out Vector3 forward)
        {
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            Vector3[] existing = UnitRegistry.allUnits.OfType<Ship>().Select(s => s.GlobalPosition().AsVector3()).ToArray();
            foreach (var road in level.seaLanes.roads.OrderByDescending(r => r.length))
            for (int i = 1; i < road.points.Count; i++)
            foreach (bool reverse in new[] { false, true })
            {
                Vector3 a = road.points[reverse ? i : i - 1].AsVector3();
                Vector3 b = road.points[reverse ? i - 1 : i].AsVector3();
                a.y = b.y = Datum.SeaLevel.y;
                if (Vector3.Distance(a, b) < 800) continue;
                Vector3 direction = (b - a).normalized;
                Vector3 p = a + direction * 350, q = p + direction * 6500;
                Vector3 lateral = Vector3.Cross(Vector3.up, direction) * 200;
                bool safe = true;
                for (int n = 0; n <= 26 && safe; n++)
                {
                    Vector3 center = Vector3.Lerp(p, q, n / 26f);
                    if (existing.Any(e => Vector3.Distance(center, e) < 1200)) { safe = false; break; }
                    for (int side = -1; side <= 1; side++)
                    {
                        RaycastHit hit;
                        if (PathfindingAgent.RaycastTerrain(new GlobalPosition(center + lateral * side), out hit) &&
                            hit.point.y > Datum.LocalSeaY - 18) { safe = false; break; }
                    }
                }
                if (!safe) continue;
                spawn = new GlobalPosition(p); destination = new GlobalPosition(q); forward = direction; return;
            }
            throw new InvalidOperationException("No clear 6.5 km native sea-lane leg for the undamaged speed trial.");
        }

        private static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        private static void Require(List<object> checks, string name, bool condition)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report) { File.WriteAllText(output, Audit.Json(report)); }
    }
}
