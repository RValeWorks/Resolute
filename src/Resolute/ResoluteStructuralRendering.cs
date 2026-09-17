using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Template-only batching. Never merge a native damage owner, an articulated
    // fitting, a mesh rebuilt by tearing, or hardware followed onto VLS debris.
    internal static class ResoluteStaticBatches
    {
        private static readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();

        private static bool ValidMesh(Renderer renderer)
        {
            if (!(renderer is MeshRenderer)) return false;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return false;
            Mesh mesh = filter.sharedMesh;
            return renderer.sharedMaterials.Length >= mesh.subMeshCount &&
                Enumerable.Range(0, mesh.subMeshCount).All(slot => mesh.GetTopology(slot) == MeshTopology.Triangles);
        }

        internal static bool Eligible(Renderer renderer, UnitPart owner)
        {
            if (!(renderer is MeshRenderer) || !renderer.enabled || renderer.transform.parent != owner.transform ||
                !renderer.name.StartsWith("rsl_", StringComparison.Ordinal) || SurfacePaintStyle.NeedsDamageBackfaces(renderer) ||
                renderer.HasPropertyBlock() || renderer.sharedMaterials.Any(material => material != null && material.renderQueue > 2500)) return false;
            string name = renderer.name;
            if (new[] { "hatch", "door", "gate", "prop_", "prop_outer", "rudder", "yaw", "pitch", "gun_", "vds", "torp", "laser", "nav_radar",
                "vls", "small_p_plinth", "small_p_frames", "small_s_plinth", "small_s_frames" }.Any(name.Contains)) return false;
            return ValidMesh(renderer) && renderer.GetComponents<Component>().Length == 3;
        }

        private static string State(Renderer renderer)
        {
            Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            return renderer.gameObject.layer + "/" + renderer.shadowCastingMode + "/" + renderer.receiveShadows + "/" +
                renderer.lightProbeUsage + "/" + renderer.reflectionProbeUsage + "/" + renderer.renderingLayerMask + "/" +
                renderer.lightmapIndex + "/" + renderer.lightmapScaleOffset + "/" +
                renderer.sortingLayerID + "/" + renderer.sortingOrder + "/" + renderer.motionVectorGenerationMode + "/" +
                (renderer.probeAnchor != null ? renderer.probeAnchor.GetInstanceID() : 0) + "/" +
                (renderer.lightProbeProxyVolumeOverride != null ? renderer.lightProbeProxyVolumeOverride.GetInstanceID() : 0) + "/" +
                string.Join(",", renderer.sharedMaterials.Select(material => material != null ? material.renderQueue : 0)) + "/" +
                (mesh.normals.Length == mesh.vertexCount) + "/" + (mesh.tangents.Length == mesh.vertexCount) + "/" +
                (mesh.uv.Length == mesh.vertexCount) + "/" + (mesh.uv2.Length == mesh.vertexCount) + "/" + (mesh.colors32.Length == mesh.vertexCount);
        }

        internal static List<Renderer> Batch(UnitPart owner, IEnumerable<Renderer> inputs, bool distant = false)
        {
            return BatchAsync(owner, inputs, distant, null).GetAwaiter().GetResult();
        }

        internal static async UniTask<List<Renderer>> BatchAsync(UnitPart owner, IEnumerable<Renderer> inputs,
            bool distant = false, StartupLoadContext loading = null)
        {
            var result = new List<Renderer>();
            var candidates = new List<Renderer>();
            foreach (Renderer renderer in inputs)
            {
                if (loading != null) await loading.Step(distant ? "Preparing distant ship surfaces" : "Preparing ship surface batches");
                if (renderer.GetComponent<ResoluteAnimatedLod>() != null) { result.Add(renderer); continue; }
                if (!ValidMesh(renderer)) { result.Add(renderer); continue; }
                // Hatches keep their own renderer/pivot and are never merged.
                // Their mesh is not rebuilt by the VLS hardware tear path, so
                // unused material slots can be removed without index clients.
                if (!distant && renderer.name.StartsWith("rsl_hatch_", StringComparison.Ordinal)) CompactHatch(renderer);
                if (distant || Eligible(renderer, owner)) candidates.Add(renderer); else result.Add(renderer);
            }
            foreach (var group in candidates.GroupBy(State))
            {
                if (loading != null) await loading.Step(distant ? "Combining distant ship surfaces" : "Combining ship surfaces");
                Renderer[] originals = group.ToArray();
                Mesh[] meshes = originals.Select(r => r.GetComponent<MeshFilter>().sharedMesh).ToArray();
                Matrix4x4[] matrices = originals.Select(r => owner.transform.worldToLocalMatrix * r.transform.localToWorldMatrix).ToArray();
                Material[][] materials = originals.Select(r => r.sharedMaterials).ToArray();
                Material[] palette = materials.SelectMany((set, index) => set.Take(meshes[index].subMeshCount)
                    .Where((material, slot) => meshes[index].GetIndexCount(slot) != 0)).Distinct().ToArray();
                if (palette.Length == 0) { result.AddRange(originals); continue; }
                string key = string.Join(";", meshes.Select((mesh, i) => mesh.GetInstanceID() + "/" + matrices[i].ToString("R") + "/" +
                    string.Join(",", materials[i].Select(material => material != null ? material.GetInstanceID().ToString() : "null"))));
                Mesh combined;
                if (!Meshes.TryGetValue(key, out combined))
                {
                    if (loading != null) await loading.Step("Building a ship surface mesh");
                    combined = Merge(meshes, matrices, materials, palette);
                    Object.DontDestroyOnLoad(combined); Meshes.Add(key, combined);
                }
                var obj = new GameObject("Resolute " + (distant ? "distant" : "static") + " batch " + owner.name) { layer = originals[0].gameObject.layer };
                obj.transform.SetParent(owner.transform, false);
                obj.AddComponent<MeshFilter>().sharedMesh = combined;
                MeshRenderer output = obj.AddComponent<MeshRenderer>(); output.sharedMaterials = palette;
                Renderer reference = originals[0];
                output.shadowCastingMode = reference.shadowCastingMode; output.receiveShadows = reference.receiveShadows;
                output.lightProbeUsage = reference.lightProbeUsage; output.reflectionProbeUsage = reference.reflectionProbeUsage;
                output.renderingLayerMask = reference.renderingLayerMask;
                output.lightmapIndex = reference.lightmapIndex; output.lightmapScaleOffset = reference.lightmapScaleOffset;
                output.sortingLayerID = reference.sortingLayerID; output.sortingOrder = reference.sortingOrder;
                output.motionVectorGenerationMode = reference.motionVectorGenerationMode;
                output.probeAnchor = reference.probeAnchor; output.lightProbeProxyVolumeOverride = reference.lightProbeProxyVolumeOverride;
                foreach (Renderer renderer in originals) renderer.enabled = false;
                result.Add(output);
            }
            return result;
        }

        internal static void CompactHatch(Renderer renderer)
        {
            if (!ValidMesh(renderer) || renderer.HasPropertyBlock()) return;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter.sharedMesh; Material[] materials = renderer.sharedMaterials;
            int[] slots = Enumerable.Range(0, mesh.subMeshCount).Where(slot => mesh.GetIndexCount(slot) != 0).ToArray();
            if (slots.Length == 0 || slots.Length == materials.Length) return;
            Material[] palette = slots.Select(slot => materials[slot]).Distinct().ToArray();
            string key = "hatch/" + mesh.GetInstanceID() + "/" + string.Join(",", materials.Select(material => material != null ? material.GetInstanceID().ToString() : "null"));
            Mesh compact;
            if (!Meshes.TryGetValue(key, out compact))
            {
                compact = Merge(new[] { mesh }, new[] { Matrix4x4.identity }, new[] { materials }, palette);
                Object.DontDestroyOnLoad(compact); Meshes.Add(key, compact);
            }
            filter.sharedMesh = compact; renderer.sharedMaterials = palette;
        }

        internal static Mesh Merge(Mesh[] meshes, Matrix4x4[] matrices, Material[][] materials, Material[] palette)
        {
            var p = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector4>();
            var uv = new List<Vector2>(); var uv1 = new List<Vector2>(); var color = new List<Color32>();
            var triangles = palette.Select(material => new List<int>()).ToArray();
            for (int i = 0; i < meshes.Length; i++)
            {
                Mesh mesh = meshes[i]; Matrix4x4 matrix = matrices[i], normalMatrix = matrix.inverse.transpose;
                int offset = p.Count; bool mirrored = matrix.determinant < 0f;
                p.AddRange(mesh.vertices.Select(matrix.MultiplyPoint3x4));
                n.AddRange(mesh.normals.Select(value => normalMatrix.MultiplyVector(value).normalized));
                t.AddRange(mesh.tangents.Select(value => {
                    Vector3 direction = matrix.MultiplyVector(new Vector3(value.x, value.y, value.z)).normalized;
                    return new Vector4(direction.x, direction.y, direction.z, mirrored ? -value.w : value.w);
                }));
                uv.AddRange(mesh.uv); uv1.AddRange(mesh.uv2); color.AddRange(mesh.colors32);
                for (int slot = 0; slot < mesh.subMeshCount; slot++)
                {
                    int[] indices = mesh.GetTriangles(slot);
                    if (indices.Length == 0) continue;
                    int target = Array.IndexOf(palette, materials[i][slot]);
                    for (int index = 0; index < indices.Length; index += 3)
                    {
                        triangles[target].Add(offset + indices[index]);
                        triangles[target].Add(offset + indices[index + (mirrored ? 2 : 1)]);
                        triangles[target].Add(offset + indices[index + (mirrored ? 1 : 2)]);
                    }
                }
            }
            var output = new Mesh { name = "Resolute exact static material batch", indexFormat = IndexFormat.UInt32, subMeshCount = palette.Length };
            output.SetVertices(p); if (n.Count == p.Count) output.SetNormals(n);
            if (t.Count == p.Count) output.SetTangents(t);
            if (uv.Count == p.Count) output.SetUVs(0, uv); if (uv1.Count == p.Count) output.SetUVs(1, uv1);
            if (color.Count == p.Count) output.SetColors(color);
            for (int slot = 0; slot < triangles.Length; slot++) output.SetTriangles(triangles[slot], slot);
            output.RecalculateBounds(); return output;
        }
    }

    // A request belongs to an actual damage owner. Cosmetic repair withdraws
    // only that owner's request; a tear/detachment remains exact indefinitely.
    internal sealed class ResoluteLocalDamageLod : MonoBehaviour
    {
        public LODGroup Group;
        private readonly HashSet<ResoluteDamageVisual> damaged = new HashSet<ResoluteDamageVisual>();
        private bool structural;
        internal bool Forced => structural || damaged.Count != 0;
        internal void SetDamage(ResoluteDamageVisual owner, bool value)
        {
            if (value) damaged.Add(owner); else damaged.Remove(owner);
            Apply();
        }
        internal void StructuralChange() { structural = true; Apply(); }
        private void Apply() { if (Group != null) Group.ForceLOD(Forced ? 0 : -1); }
    }

    internal sealed class ResoluteStructureDiagnostics : MonoBehaviour
    {
        public ResoluteStructuralSection[] Sections;
        public ResoluteLocalDamageLod[] DetailGroups;
        public int NearRenderersBefore, NearRenderersAfter, NearMaterialSlotsBefore, NearMaterialSlotsAfter;
        public int DistantRenderers;

        internal object Capture()
        {
            int released = 0, live = 0, rebuilds = 0, expanded = 0, colliders = 0, localized = 0, unlocalized = 0;
            double releaseMs = 0, rebuildMs = 0;
            if (Sections != null) foreach (ResoluteStructuralSection section in Sections)
            {
                if (section == null) continue;
                released += section.ReleasedCount; rebuilds += section.RetainedMeshRebuilds;
                expanded += section.ExpandedCollisionGroups; colliders += section.ActiveCollisionCount;
                localized += section.LocalizedDamageEvents; unlocalized += section.UnlocalizedDamageEvents;
                releaseMs += section.ReleaseMilliseconds; rebuildMs += section.RebuildMilliseconds;
                foreach (ResolutePlateDebris debris in section.ReleasedDebris) if (debris != null) live++;
            }
            return new Dictionary<string, object> {
                ["sections"] = Sections == null ? 0 : Sections.Length,
                ["nearRenderersBefore"] = NearRenderersBefore, ["nearRenderersAfter"] = NearRenderersAfter,
                ["nearMaterialSlotsBefore"] = NearMaterialSlotsBefore, ["nearMaterialSlotsAfter"] = NearMaterialSlotsAfter,
                ["distantRenderers"] = DistantRenderers,
                ["forcedDetailGroups"] = DetailGroups == null ? 0 : DetailGroups.Count(group => group != null && group.Forced),
                ["releasedPlates"] = released, ["livePlateBodies"] = live, ["retainedMeshRebuilds"] = rebuilds,
                ["expandedCollisionGroups"] = expanded, ["activeStructuralColliders"] = colliders,
                ["localizedDamageEvents"] = localized, ["unlocalizedDamageEvents"] = unlocalized,
                ["releaseMillisecondsCumulative"] = releaseMs, ["retainedRebuildMillisecondsCumulative"] = rebuildMs
            };
        }
    }
}
