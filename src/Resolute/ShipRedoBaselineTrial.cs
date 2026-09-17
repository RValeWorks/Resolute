using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using NuclearOption.Jobs;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Opt-in observations only. No production registration, physics or damage changes.
    internal static class ShipRedoBaselineTrial
    {
        private static Ship observed;
        private static readonly Dictionary<ShipPart, ShipPartFields> lastForces = new Dictionary<ShipPart, ShipPartFields>();

        internal static ShipDefinition Comparator(Dictionary<string, object> report)
        {
            var resolute = (ShipDefinition)Encyclopedia.Lookup[Plugin.DefinitionKey];
            var definitions = Encyclopedia.Lookup.Values.OfType<ShipDefinition>().Distinct().OrderBy(d => d.jsonKey).ToArray();
            var candidates = definitions.Where(d => d.jsonKey != Plugin.DefinitionKey && d.IsAllowed(false) &&
                d.length > 0 && d.width > 0 && d.unitPrefab != null && d.unitPrefab.GetComponent<Ship>() != null &&
                d.unitPrefab.GetComponentInChildren<ResoluteStructuralSection>(true) == null).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("No registered non-Resolute ship comparator.");
            Func<ShipDefinition, double> distance = d => Math.Abs(Math.Log(d.length / resolute.length)) + Math.Abs(Math.Log(d.width / resolute.width));
            var selected = candidates.OrderBy(distance).ThenBy(d => d.jsonKey).First();
            report["shipRedoRegisteredInventory"] = new Dictionary<string, object> {
                ["selectionRule"] = "Allowed registered non-Resolute ship prefab minimizing abs(log(length/Resolute.length))+abs(log(width/Resolute.width)); stable key tie-break. Inventory does not independently establish mod provenance; run in the isolated test-game with its plugin list retained.",
                ["selected"] = selected.jsonKey,
                ["ships"] = definitions.Select(d => (object)new Dictionary<string, object> {
                    ["key"] = d.jsonKey, ["name"] = d.unitName, ["lengthM"] = d.length, ["widthM"] = d.width,
                    ["heightM"] = d.height, ["definitionMassKg"] = d.mass, ["allowed"] = d.IsAllowed(false),
                    ["prefab"] = d.unitPrefab != null ? d.unitPrefab.name : null,
                    ["candidate"] = candidates.Contains(d), ["dimensionDistance"] = d.length > 0 && d.width > 0 ? (object)distance(d) : null
                }).ToArray()
            };
            return selected;
        }

        internal static IEnumerator Run(GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Ship redo baseline requires isolated test-game.");
            var comparator = Comparator(report);
            var resolute = (ShipDefinition)Encyclopedia.Lookup[Plugin.DefinitionKey];
            var cases = new List<object>();
            report["shipRedoBaseline"] = new Dictionary<string, object> {
                ["scope"] = "Two fresh normal native spawns per definition, at opposite headings, observed for 75 seconds each. Native hold-position AI and physics remain active. Weapon controllers held as in the existing isolated trial. No health, force, mass, flooding, attitude, velocity, throttle or physics overrides.",
                ["environment"] = "Inherited MissionTrial clear-weather/noon offline map. This is a controlled settling baseline, not a rough-sea validation or a performance run.",
                ["heelConvention"] = "Positive heel means ship-local +X rail is lower: atan2(-worldUp dot shipRight, worldUp dot shipUp). +X is labelled explicitly; verify the authored starboard convention against native views. Pitch positive means bow/local +Z up.",
                ["forceScope"] = "Latest ShipPartFields copied in an ApplyJobFields postfix after the native completed-job read. Includes native force, forcePosition, partHeight and submergedAmount. These are observed job outputs, not isolated pure buoyancy; native water drag may contribute.",
                ["acceptance"] = "Raw observation only. Finite/complete sampling checks do not certify acceptable heel. Final 30-second mean and range are supplied; two headings are an initial comparison, not three statistical repeats.",
                ["cases"] = cases
            };
            var point = DamagePerformanceTrial.FindWater(origin, heading, Mathf.Max(comparator.width, resolute.width), Mathf.Max(comparator.length, resolute.length));
            var observer = new Harmony(Plugin.Id + ".ship-redo-equilibrium");
            observer.Patch(AccessTools.Method(typeof(ShipPart), nameof(ShipPart.ApplyJobFields)),
                postfix: new HarmonyMethod(typeof(ShipRedoBaselineTrial), nameof(ObserveForce)));
            try
            {
                foreach (var definition in new[] { comparator, resolute })
                foreach (int yaw in new[] { 0, 180 })
                {
                    report["phase"] = "ship-redo-settle-" + definition.jsonKey + "-" + yaw;
                    File.WriteAllText(output, Audit.Json(report));
                    var existing = DamagePerformanceTrial.SceneObjects();
                    Ship subject = null;
                    try
                    {
                        subject = DamagePerformanceTrial.Spawn(definition, point, heading * Quaternion.Euler(0, yaw, 0), faction, "ship_redo_baseline");
                        observed = subject; lastForces.Clear();
                        yield return null; yield return new WaitForFixedUpdate();
                        object before = Snapshot(subject);
                        var samples = new List<object>(); var finalRoll = new List<double>();
                        float started = Time.realtimeSinceStartup;
                        for (int sample = 0; sample <= 75; sample++)
                        {
                            while (Time.realtimeSinceStartup - started < sample) yield return null;
                            if (subject == null || subject.rb == null) throw new InvalidOperationException("Settling subject disappeared.");
                            double roll = Heel(subject.transform);
                            samples.Add(new Dictionary<string, object> {
                                ["elapsedSeconds"] = Time.realtimeSinceStartup - started, ["heelPlusXDownDegrees"] = roll,
                                ["pitchBowUpDegrees"] = Math.Asin(Mathf.Clamp(Vector3.Dot(subject.transform.forward, Vector3.up), -1, 1)) * Mathf.Rad2Deg,
                                ["rootAboveSeaM"] = subject.GlobalPosition().y - Datum.SeaLevel.y,
                                ["sourceWaterlineAboveSeaM"] = subject.transform.Find("ResoluteVisual") != null ?
                                    (object)(subject.transform.Find("ResoluteVisual").position.ToGlobalPosition().y - Datum.SeaLevel.y) : null,
                                ["velocityWorldMps"] = V(subject.rb.velocity), ["angularVelocityShipRadiansPerSecond"] = V(subject.transform.InverseTransformDirection(subject.rb.angularVelocity)),
                                ["nativeThrottle"] = subject.GetInputs().throttle, ["nativeSteering"] = subject.GetInputs().steering,
                                ["hydro"] = sample % 5 == 0 ? Hydro(subject) : null
                            });
                            if (sample >= 45) finalRoll.Add(roll);
                        }
                        cases.Add(new Dictionary<string, object> {
                            ["definition"] = definition.jsonKey, ["headingOffsetDegrees"] = yaw,
                            ["initial"] = before, ["samples"] = samples, ["final"] = Snapshot(subject),
                            ["final30SecondsHeel"] = new Dictionary<string, object> { ["mean"] = finalRoll.Average(), ["min"] = finalRoll.Min(), ["max"] = finalRoll.Max() }
                        });
                        bool complete = samples.Count == 76 && finalRoll.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
                        checks.Add(new Dictionary<string, object> { ["name"] = "ship-redo-observations-" + definition.jsonKey + "-" + yaw, ["passed"] = complete });
                        checks.Add(new Dictionary<string, object> { ["name"] = "ship-redo-native-force-observed-" + definition.jsonKey + "-" + yaw, ["passed"] = lastForces.Count > 0 });
                        File.WriteAllText(output, Audit.Json(report));
                    }
                    finally { observed = null; lastForces.Clear(); DamagePerformanceTrial.Cleanup(subject, null, existing); }
                    yield return null; yield return new WaitForFixedUpdate();
                }
            }
            finally { observed = null; lastForces.Clear(); observer.UnpatchSelf(); }
        }

        private static void ObserveForce(ShipPart __instance)
        {
            if (observed != null && __instance.parentUnit == observed && __instance.JobFields.IsCreated)
                lastForces[__instance] = __instance.JobFields.Ref();
        }

        private static double Heel(Transform root) => Math.Atan2(-Vector3.Dot(root.right, Vector3.up), Vector3.Dot(root.up, Vector3.up)) * Mathf.Rad2Deg;
        private static T Read<T>(object instance, string field) => (T)AccessTools.Field(instance.GetType(), field).GetValue(instance);
        internal static string PathOf(Transform value)
        {
            if (value == null) return null;
            var names = new List<string>();
            for (var item = value; item != null; item = item.parent) names.Add(item.name + "#" + item.GetInstanceID());
            names.Reverse(); return string.Join("/", names);
        }
        internal static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static float[] Q(Quaternion value) => new[] { value.x, value.y, value.z, value.w };

        private static object Hydro(Ship ship)
        {
            return ship.partLookup.OfType<ShipPart>().Where(p => p != null).Distinct().Select(p => {
                var force = p.GetJobTransforms(); var haveJob = lastForces.TryGetValue(p, out var job);
                return (object)new Dictionary<string, object> {
                    ["part"] = p.name, ["instanceId"] = p.GetInstanceID(), ["massKg"] = p.mass,
                    ["centerOfMassShipM"] = p.CenterOfMass != null ? V(ship.transform.InverseTransformPoint(p.CenterOfMass.position)) : null,
                    ["configuredHeightM"] = Read<float>(p, "height"), ["displacement"] = Read<float>(p, "displacement"),
                    ["originalDisplacement"] = p.originalDisplacement, ["leakToDisplacement"] = p.leakToDisplacement,
                    ["leakRate"] = Read<float>(p, "leakRate"), ["forceTransformShipM"] = force != null ? V(ship.transform.InverseTransformPoint(force.position)) : null,
                    ["jobObserved"] = haveJob, ["jobHeightM"] = haveJob ? (object)job.partHeight : null,
                    ["jobDisplacement"] = haveJob ? (object)job.displacement : null, ["submergedAmount"] = haveJob ? (object)job.submergedAmount : null,
                    ["jobForceWorldN"] = haveJob ? V(job.force) : null,
                    ["jobForcePositionWorldM"] = haveJob ? V(job.forcePosition) : null,
                    ["jobForceShipN"] = haveJob ? V(ship.transform.InverseTransformDirection(job.force)) : null,
                    ["jobForcePositionShipM"] = haveJob ? V(ship.transform.InverseTransformPoint(job.forcePosition)) : null
                };
            }).ToArray();
        }

        internal static object Snapshot(Ship ship)
        {
            if (ship == null) return null;
            var root = ship.transform;
            return new Dictionary<string, object> {
                ["coordinateFrame"] = "Current subject transform: X transverse, Y up, Z longitudinal. Mesh vertices transformed through actual native hierarchy, including scale. PCA axis is vertex-weighted; it is not an area-weighted fracture direction metric.",
                ["instanceId"] = ship.GetInstanceID(), ["definition"] = ship.definition.jsonKey,
                ["rootUnityWorldPositionM"] = V(root.position), ["rootWorldRotation"] = Q(root.rotation),
                ["massKg"] = ship.rb.mass, ["centerOfMassShipM"] = V(root.InverseTransformPoint(ship.rb.worldCenterOfMass)),
                ["inertiaTensorKgM2"] = V(ship.rb.inertiaTensor), ["inertiaTensorRotation"] = Q(ship.rb.inertiaTensorRotation),
                ["heelPlusXDownDegrees"] = Heel(root), ["nativeInputs"] = new[] { ship.GetInputs().throttle, ship.GetInputs().steering },
                ["hydro"] = Hydro(ship), ["vls"] = Vls(ship),
                ["nativeParts"] = ship.partLookup.OfType<ShipPart>().Where(p => p != null).Distinct().Select(p => (object)new Dictionary<string, object> {
                    ["name"] = p.name, ["id"] = p.id, ["ownerPath"] = PathOf(p.transform.parent),
                    ["massKg"] = p.mass, ["health"] = p.hitPoints, ["detached"] = p.IsDetached(),
                    ["localRotation"] = Q(Quaternion.Inverse(root.rotation) * p.transform.rotation),
                    ["meshes"] = p.GetComponentsInChildren<MeshFilter>(true).Where(f => f.GetComponentInParent<UnitPart>() == p)
                        .Select(f => MeshObject(f, root.worldToLocalMatrix)).ToArray(),
                    ["colliders"] = p.GetComponents<Collider>().Select(c => ColliderObject(c, root.worldToLocalMatrix)).ToArray()
                }).ToArray(),
                ["surfaceSections"] = ship.GetComponentsInChildren<ResoluteStructuralSection>(true).Select(s => (object)new Dictionary<string, object> {
                    ["part"] = s.Part.name, ["surfaceAreaM2"] = s.SurfaceArea, ["pieceCount"] = s.SurfaceMeshes.Length,
                    ["releasedCount"] = s.ReleasedCount, ["expandedGroups"] = s.ExpandedCollisionGroups,
                    ["activeCollisionCount"] = s.ActiveCollisionCount, ["retainedMeshRebuilds"] = s.RetainedMeshRebuilds,
                    ["pieces"] = s.SurfaceMeshes.Select((mesh, i) => (object)new Dictionary<string, object> {
                        ["index"] = i, ["areaM2"] = s.PlateAreas[i], ["diameterM"] = s.PlateDiameters[i],
                        ["geometry"] = MeshGeometry(mesh, root.worldToLocalMatrix * s.transform.localToWorldMatrix * Matrix4x4.Translate(s.PlateCenters[i]))
                    }).ToArray()
                }).ToArray()
            };
        }

        internal static object FragmentSnapshot(Ship ship, HashSet<GameObject> existing, Matrix4x4 referenceFromWorld)
        {
            // Called only with sampling stopped. Includes current coherent native bodies and custom plates.
            return Resources.FindObjectsOfTypeAll<Rigidbody>().Where(b => b.gameObject.scene.IsValid() && !existing.Contains(b.gameObject)).Select(b => (object)new Dictionary<string, object> {
                ["name"] = b.name, ["instanceId"] = b.GetInstanceID(), ["massKg"] = b.mass,
                ["isKinematic"] = b.isKinematic, ["velocityWorldMps"] = V(b.velocity),
                ["population"] = b.GetComponent<ResolutePlateDebris>() != null ? "custom-torn-plate" :
                    b.GetComponent<ShipPart>() != null ? "native-ShipPart-body" : "other-new-native-body",
                ["meshes"] = b.GetComponentsInChildren<MeshFilter>(true).Where(f => f.GetComponentInParent<Rigidbody>() == b)
                    .Select(f => MeshObject(f, referenceFromWorld)).ToArray(),
                ["colliders"] = b.GetComponentsInChildren<Collider>(true).Where(c => c.attachedRigidbody == b)
                    .Select(c => ColliderObject(c, referenceFromWorld)).ToArray()
            }).ToArray();
        }

        private static object ColliderObject(Collider collider, Matrix4x4 referenceFromWorld)
        {
            var mesh = collider as MeshCollider;
            return new Dictionary<string, object> { ["type"] = collider.GetType().Name, ["enabled"] = collider.enabled,
                ["trigger"] = collider.isTrigger, ["owner"] = PathOf(collider.transform),
                ["meshGeometry"] = mesh != null ? MeshGeometry(mesh.sharedMesh, referenceFromWorld * mesh.transform.localToWorldMatrix) : null,
                ["primitiveWorldAabbOnly"] = mesh == null ? new[] { V(collider.bounds.center), V(collider.bounds.size) } : null };
        }

        private static object MeshObject(MeshFilter filter, Matrix4x4 referenceFromWorld)
        {
            var renderer = filter.GetComponent<Renderer>();
            return new Dictionary<string, object> {
                ["path"] = PathOf(filter.transform), ["active"] = filter.gameObject.activeInHierarchy,
                ["rendererEnabled"] = renderer != null && renderer.enabled,
                ["geometry"] = MeshGeometry(filter.sharedMesh, referenceFromWorld * filter.transform.localToWorldMatrix),
                ["materials"] = renderer != null ? renderer.sharedMaterials.Select(m => m == null ? null : (object)new Dictionary<string, object> {
                    ["name"] = m.name, ["shader"] = m.shader != null ? m.shader.name : null,
                    ["baseColor"] = MaterialColor(m), ["baseTexture"] = MaterialTexture(m)
                }).ToArray() : null
            };
        }
        private static float[] ColorValue(Color color) => new[] { color.r, color.g, color.b, color.a };
        private static object MaterialColor(Material material)
        {
            if (material.shader == null) return null;
            foreach (string property in new[] { "_BaseColor", "_Color" })
            {
                int index = material.shader.FindPropertyIndex(property);
                if (index >= 0 && material.shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Color)
                    return new Dictionary<string, object> { ["property"] = property, ["value"] = ColorValue(material.GetColor(property)) };
            }
            return null;
        }
        private static object MaterialTexture(Material material)
        {
            if (material.shader == null) return null;
            foreach (string property in new[] { "_BaseColor", "_BaseMap", "_MainTex" })
            {
                int index = material.shader.FindPropertyIndex(property);
                if (index < 0 || material.shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                Texture texture = material.GetTexture(property);
                if (texture != null) return new Dictionary<string, object> { ["property"] = property, ["name"] = texture.name,
                    ["width"] = texture.width, ["height"] = texture.height };
            }
            return null;
        }

        internal static object MeshGeometry(Mesh mesh, Matrix4x4 frameFromMesh)
        {
            if (mesh == null) return null;
            var result = new Dictionary<string, object> { ["name"] = mesh.name, ["vertexCount"] = mesh.vertexCount,
                ["subMeshCount"] = mesh.subMeshCount, ["readable"] = mesh.isReadable };
            if (!mesh.isReadable) { result["limitation"] = "Native mesh CPU vertex access unavailable; no guessed fragment dimensions."; return result; }
            var vertices = mesh.vertices.Select(frameFromMesh.MultiplyPoint3x4).ToArray();
            if (vertices.Length == 0) return result;
            Vector3 min = vertices[0], max = min, mean = Vector3.zero;
            foreach (var v in vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); mean += v; }
            mean /= vertices.Length;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var v in vertices) { var d = v - mean; xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z; }
            var axis = Vector3.forward; double principal = -1;
            foreach (var seed in new[] { Vector3.right, Vector3.up, Vector3.forward }) {
                var candidate = seed;
                for (int i = 0; i < 32; i++) {
                    var next = new Vector3((float)(xx * candidate.x + xy * candidate.y + xz * candidate.z), (float)(xy * candidate.x + yy * candidate.y + yz * candidate.z), (float)(xz * candidate.x + yz * candidate.y + zz * candidate.z));
                    if (next.sqrMagnitude < 1e-15f) break; candidate = next.normalized;
                }
                double variance = xx * candidate.x * candidate.x + yy * candidate.y * candidate.y + zz * candidate.z * candidate.z +
                    2 * (xy * candidate.x * candidate.y + xz * candidate.x * candidate.z + yz * candidate.y * candidate.z);
                if (variance > principal) { principal = variance; axis = candidate; }
            }
            if (axis.z < 0) axis = -axis;
            result["minShipM"] = V(min); result["maxShipM"] = V(max); result["sizeXYZMetres"] = V(max - min);
            result["vertexPrincipalAxisShip"] = V(axis); result["absLongAxisDotShipZ"] = Mathf.Abs(axis.z);
            result["principalVarianceFraction"] = xx + yy + zz > 0 ? (object)(principal / (xx + yy + zz)) : null;
            var slots = new List<object>(); var uv = mesh.uv;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) {
                var indices = mesh.GetIndices(sub); double area = 0; int upTriangles = 0;
                if (mesh.GetTopology(sub) == UnityEngine.MeshTopology.Triangles)
                    for (int i = 0; i + 2 < indices.Length; i += 3) {
                        var normal = Vector3.Cross(vertices[indices[i + 1]] - vertices[indices[i]], vertices[indices[i + 2]] - vertices[indices[i]]);
                        area += normal.magnitude * .5; if (normal.y > normal.magnitude * .7f) upTriangles++;
                    }
                Vector2 uvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity), uvMax = -uvMin;
                foreach (int index in indices) if (index < uv.Length) { uvMin = Vector2.Min(uvMin, uv[index]); uvMax = Vector2.Max(uvMax, uv[index]); }
                slots.Add(new Dictionary<string, object> { ["slot"] = sub, ["indexCount"] = indices.Length, ["areaM2"] = area,
                    ["upwardTriangles"] = upTriangles, ["uvMinMax"] = uv.Length > 0 && indices.Length > 0 ? new[] { uvMin.x, uvMin.y, uvMax.x, uvMax.y } : null });
            }
            result["slots"] = slots; return result;
        }

        internal static object Vls(Ship ship)
        {
            if (ship == null) return null;
            var attachments = ship.GetComponent<VlsStructuralAttachments>();
            if (attachments == null) return null;
            var cells = Read<VlsStructuralAttachments.Cell[]>(attachments, "Cells");
            var fittings = Read<VlsStructuralAttachments.Fitting[]>(attachments, "Fittings");
            var separated = Read<bool[]>(attachments, "separated");
            return new Dictionary<string, object> {
                ["cellCount"] = cells.Length, ["fittingCount"] = fittings.Length,
                ["cells"] = cells.Select((c, i) => (object)new Dictionary<string, object> {
                    ["recordIndex"] = i, ["launcher"] = PathOf(c.Launcher.transform), ["cellIndex"] = c.Index,
                    ["supportPart"] = c.Section != null ? c.Section.Part.name : null, ["supportPiece"] = c.SupportPiece,
                    ["initialAssignedSupportFootprint"] = SupportFootprint(c, ship.transform),
                    ["separated"] = separated[i], ["hatch"] = Pose(c.Hatch, c.Section != null ? c.Section.transform : null, ship.transform),
                    ["anchor"] = Pose(c.Anchor, c.Section != null ? c.Section.transform : null, ship.transform)
                }).ToArray(),
                ["fittings"] = fittings.Select(f => (object)new Dictionary<string, object> {
                    ["filter"] = f.Filter != null ? PathOf(f.Filter.transform) : null,
                    ["pose"] = f.Filter != null ? Pose(f.Filter.transform, f.Filter.transform.GetComponentInParent<ShipPart>()?.transform, ship.transform) : null,
                    ["ownedCellRecordIndices"] = f.CellMeshes.Select((m, i) => m != null ? i : -1).Where(i => i >= 0).ToArray()
                }).ToArray()
            };
        }
        private static object SupportFootprint(VlsStructuralAttachments.Cell cell, Transform ship)
        {
            if (cell.Section == null || cell.Hatch == null) return null;
            var section = cell.Section; int index = cell.SupportPiece;
            Mesh mesh = index >= 0 ? section.SurfaceMeshes[index] : section.RetainedDetailSolid;
            Vector3 offset = index >= 0 ? section.PlateCenters[index] : section.RetainedDetailCenter;
            if (mesh == null || !mesh.isReadable) return new Dictionary<string, object> { ["available"] = false };
            var matrix = ship.worldToLocalMatrix * section.transform.localToWorldMatrix * Matrix4x4.Translate(offset);
            var vertices = mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var point = ship.InverseTransformPoint(cell.Hatch.position); int containing = 0; float highest = float.NegativeInfinity;
            foreach (int sub in Enumerable.Range(0, mesh.subMeshCount))
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var triangles = mesh.GetIndices(sub);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]]; var b = vertices[triangles[i + 1]]; var c = vertices[triangles[i + 2]];
                    var normal = Vector3.Cross(b - a, c - a);
                    if (normal.y < normal.magnitude * .45f) continue;
                    float divisor = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(divisor) < 1e-7f) continue;
                    float u = ((b.z - c.z) * (point.x - c.x) + (c.x - b.x) * (point.z - c.z)) / divisor;
                    float v = ((c.z - a.z) * (point.x - c.x) + (a.x - c.x) * (point.z - c.z)) / divisor;
                    float w = 1f - u - v;
                    if (u < -.001f || v < -.001f || w < -.001f) continue;
                    containing++; highest = Mathf.Max(highest, u * a.y + v * b.y + w * c.y);
                }
            }
            return new Dictionary<string, object> { ["available"] = true, ["projectedUpwardTriangleCount"] = containing,
                ["hatchMinusHighestSupportYMetres"] = containing > 0 ? (object)(point.y - highest) : null,
                ["scope"] = "Assigned original piece at section pose; after separation this is not the moving debris surface. Use body-relative hatch/anchor pose and fragment meshes for separated cells." };
        }
        private static object Pose(Transform value, Transform support, Transform ship)
        {
            if (value == null) return null;
            var body = value.GetComponentInParent<Rigidbody>();
            return new Dictionary<string, object> { ["path"] = PathOf(value), ["parent"] = PathOf(value.parent),
                ["shipPositionM"] = V(ship.InverseTransformPoint(value.position)),
                ["supportPositionM"] = support != null ? V(support.InverseTransformPoint(value.position)) : null,
                ["supportRotation"] = support != null ? Q(Quaternion.Inverse(support.rotation) * value.rotation) : null,
                ["bodyOwner"] = body != null ? PathOf(body.transform) : null,
                ["bodyPositionM"] = body != null ? V(body.transform.InverseTransformPoint(value.position)) : null,
                ["bodyRotation"] = body != null ? Q(Quaternion.Inverse(body.rotation) * value.rotation) : null };
        }
    }
}
