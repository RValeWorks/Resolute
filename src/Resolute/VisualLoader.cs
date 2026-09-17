using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    // Portable source-asset loader. The distribution contains only converted
    // Resolute geometry/textures; Unity and the game supply rendering code.
    internal static class VisualLoader
    {
        internal static string ShipAssetFolder { get; private set; }
        private static readonly Dictionary<GameObject, List<UnityEngine.Object>> TemplateAssets =
            new Dictionary<GameObject, List<UnityEngine.Object>>();
#pragma warning disable 0649
        [Serializable] internal sealed class Manifest
        {
            public int schemaVersion;
            public string meshFile;
            public Node[] nodes;
            public MaterialRecord[] materials;
            public LodRecord[] lods;
            public float lod0ScreenRelativeTransitionHeight;
        }
        [Serializable] internal sealed class Node
        {
            public string name, parent, meshId, materialId;
            public float[] position, rotationEuler, scale;
        }
        [Serializable] internal sealed class MaterialRecord
        {
            public string id, diffuse, specular, normal;
            public float[] baseColor;
            public float smoothness, normalScale;
        }
        [Serializable] internal sealed class LodRecord
        {
            public int level;
            public float screenRelativeTransitionHeight;
            public Node[] renderers;
        }
#pragma warning restore 0649

        internal static GameObject Load(string pluginDirectory, Transform inactiveRoot, ManualLogSource log)
        {
            return LoadFromFolder(Path.Combine(pluginDirectory, "Assets"), "ResoluteVisual", inactiveRoot, log);
        }

        internal static UniTask<GameObject> LoadAsync(string pluginDirectory, Transform inactiveRoot, ManualLogSource log,
            StartupLoadContext loading = null)
        {
            return LoadFromFolderAsync(Path.Combine(pluginDirectory, "Assets"), "ResoluteVisual", inactiveRoot, log, null, loading);
        }

        internal static GameObject LoadFromFolder(string folder, string rootName, Transform inactiveRoot, ManualLogSource log,
            Func<MaterialRecord, List<UnityEngine.Object>, Material> materialProvider = null)
        {
            // Diagnostics retain their synchronous entry point. With no loading
            // context every awaited helper completes inline on the main thread.
            return LoadFromFolderAsync(folder, rootName, inactiveRoot, log, materialProvider, null).GetAwaiter().GetResult();
        }

        internal static async UniTask<GameObject> LoadFromFolderAsync(string folder, string rootName, Transform inactiveRoot, ManualLogSource log,
            Func<MaterialRecord, List<UnityEngine.Object>, Material> materialProvider = null, StartupLoadContext loading = null)
        {
            if (inactiveRoot == null || inactiveRoot.gameObject.activeInHierarchy)
                throw new ArgumentException("Visual template requires an inactive parent.");
            string content = rootName == "ResoluteVisual" ? "ship" : rootName == "ResoluteWeapons" ? "missile" : "liferaft";
            if (loading != null) await loading.Step("Reading " + content + " assets");
            if (rootName == "ResoluteVisual") ShipAssetFolder = Path.GetFullPath(folder);
            var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            if (manifest == null || manifest.schemaVersion != 1 || manifest.nodes == null || manifest.materials == null)
                throw new InvalidDataException("Unsupported Resolute asset manifest.");
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(x => x.name == "Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("Nuclear Option's URP Lit shader is unavailable.");
            Material nativePaint = NavalMaterials.FindNativePaint(shader);

            var materials = new Dictionary<string, Material>();
            var textures = new Dictionary<string, Texture2D>();
            var owned = new List<UnityEngine.Object>();
            GameObject root = null;
            try
            {
                int materialNumber = 0;
                foreach (var record in manifest.materials)
                {
                    if (loading != null) await loading.Step("Preparing " + content + " materials " + (++materialNumber) + "/" + manifest.materials.Length);
                    Material supplied = materialProvider != null ? materialProvider(record, owned) : null;
                    if (supplied != null) { materials.Add(record.id, supplied); continue; }
                    bool damagePaint = rootName == "ResoluteVisual" &&
                        record.id.IndexOf("glass", StringComparison.OrdinalIgnoreCase) < 0 &&
                        record.id.IndexOf("interior", StringComparison.OrdinalIgnoreCase) < 0;
                    if (damagePaint)
                    {
                        Texture baseMap = !string.IsNullOrEmpty(record.diffuse)
                            ? LoadTexture(folder, record.diffuse, false, textures, owned) : Texture2D.whiteTexture;
                        Texture normalMap = !string.IsNullOrEmpty(record.normal)
                            ? LoadTexture(folder, record.normal, true, textures, owned) : NavalMaterials.FlatNormal(owned);
                        Texture response = NavalMaterials.LoadResponse(string.IsNullOrEmpty(record.specular) ? null :
                            SafeAssetPath(folder, record.specular), record.id, owned, true);
                        Material hullPaint = NavalMaterials.CreateHullPaint("Resolute_" +
                            Path.GetFileNameWithoutExtension(record.id), baseMap, normalMap, response, owned);
                        materials.Add(record.id, hullPaint);
                        continue;
                    }
                    var material = nativePaint != null ? new Material(nativePaint) : new Material(shader);
                    material.name = "Resolute_" + Path.GetFileNameWithoutExtension(record.id);
                    owned.Add(material);
                    Color color = record.baseColor == null ? Color.white : new Color(record.baseColor[0], record.baseColor[1], record.baseColor[2], record.baseColor[3]);
                    material.SetColor("_BaseColor", color);
                    material.SetColor("_Color", color);
                    NavalMaterials.ConfigurePaint(material, record.id);
                    if (!string.IsNullOrEmpty(record.diffuse))
                    {
                        var texture = LoadTexture(folder, record.diffuse, false, textures, owned);
                        material.SetTexture("_BaseMap", texture);
                        material.SetTexture("_MainTex", texture);
                    }
                    material.SetTexture("_MetallicGlossMap", NavalMaterials.LoadResponse(
                        string.IsNullOrEmpty(record.specular) ? null : SafeAssetPath(folder, record.specular), record.id, owned));
                    if (!string.IsNullOrEmpty(record.normal))
                    {
                        material.SetTexture("_BumpMap", LoadTexture(folder, record.normal, true, textures, owned));
                        material.SetFloat("_BumpScale", record.normalScale * 0.65f);
                    }
                    else material.SetTexture("_BumpMap", NavalMaterials.FlatNormal(owned));
                    UnityEngine.Object.DontDestroyOnLoad(material);
                    materials.Add(record.id, material);
                }
                var meshes = await ReadMeshesAsync(SafeAssetPath(folder, manifest.meshFile), owned, log, loading);
                root = new GameObject(rootName);
                root.transform.SetParent(inactiveRoot, false);
                var near = new GameObject("LOD0"); near.transform.SetParent(root.transform, false);
                var nodes = new Dictionary<string, Transform>();
                foreach (var n in manifest.nodes)
                {
                    var node = new GameObject(n.name).transform;
                    // A staged build can span frames. Keep even unbound nodes
                    // under the inactive template while their parents are built.
                    node.SetParent(near.transform, false);
                    nodes.Add(n.name, node);
                    if (loading != null) await loading.Step("Assembling " + content + " model");
                }
                var nearRenderers = new List<Renderer>();
                foreach (var n in manifest.nodes)
                {
                    if (loading != null) await loading.Step("Fitting " + content + " surfaces");
                    var t = nodes[n.name];
                    t.SetParent(string.IsNullOrEmpty(n.parent) ? near.transform : nodes[n.parent], false);
                    t.localPosition = Vector(n.position);
                    t.localRotation = Quaternion.Euler(Vector(n.rotationEuler));
                    t.localScale = n.scale == null ? Vector3.one : Vector(n.scale);
                    nearRenderers.Add(AddRenderer(t.gameObject, meshes[n.meshId], materials[n.materialId]));
                }
                var lods = new List<LOD> { new LOD(manifest.lod0ScreenRelativeTransitionHeight > 0 ? manifest.lod0ScreenRelativeTransitionHeight : 0.2f, nearRenderers.ToArray()) };
                Transform flightDeckTransform;
                MeshFilter flightDeck = rootName == "ResoluteVisual" && nodes.TryGetValue("rsl_flight_deck_surface48", out flightDeckTransform)
                    ? flightDeckTransform.GetComponent<MeshFilter>() : null;
                if (manifest.lods != null) foreach (var level in manifest.lods)
                {
                    var container = new GameObject("LOD" + level.level); container.transform.SetParent(root.transform, false);
                    var renderers = new List<Renderer>();
                    foreach (var n in level.renderers)
                    {
                        if (loading != null) await loading.Step("Preparing distant " + content + " appearance");
                        var go = new GameObject(n.name);
                        if (!string.IsNullOrEmpty(n.parent))
                        {
                            if (rootName != "ResoluteVisual" || !IsArticulatedPivot(n.parent) ||
                                !nodes.TryGetValue(n.parent, out var pivot))
                                throw new InvalidDataException("Unknown articulated distance-model pivot: " + n.parent);
                            go.transform.SetParent(pivot, false);
                            go.AddComponent<ResoluteAnimatedLod>();
                        }
                        else go.transform.SetParent(container.transform, false);
                        MeshRenderer farRenderer = AddRenderer(go, meshes[n.meshId], materials[n.materialId]);
                        if (rootName == "ResoluteVisual")
                        {
                            if (flightDeck != null && farRenderer.sharedMaterial == flightDeck.GetComponent<Renderer>().sharedMaterial)
                            {
                                int matched = SurfacePaintStyle.ApplyMergedFlightDeck(farRenderer, root.transform, "lod" + level.level, flightDeck);
                                int expected = flightDeck.sharedMesh.triangles.Length / 3;
                                if (matched != expected) log.LogWarning("Resolute LOD" + level.level + " recovered " + matched + "/" + expected +
                                    " original flight-deck faces; unmatched finish-atlas surfaces retain their authored material.");
                            }
                            else SurfacePaintStyle.ApplyRenderer(farRenderer, root.transform, "lod" + level.level);
                        }
                        renderers.Add(farRenderer);
                    }
                    lods.Add(new LOD(level.screenRelativeTransitionHeight, renderers.ToArray()));
                }
                var group = root.AddComponent<LODGroup>();
                group.SetLODs(lods.ToArray()); group.RecalculateBounds(); group.fadeMode = LODFadeMode.None;
                log.LogInfo("Resolute visuals loaded: " + nodes.Count + " nodes, " + materials.Count + " materials, " + lods.Count +
                    " LOD levels; painted dielectric material profile, native template=" + (nativePaint != null ? nativePaint.name : "URP Lit") + ".");
                TemplateAssets.Add(root, owned);
                return root;
            }
            catch
            {
                if (root != null) UnityEngine.Object.Destroy(root);
                foreach (var item in owned) if (item != null) UnityEngine.Object.Destroy(item);
                throw;
            }
        }

        internal static void DestroyFailedTemplate(GameObject root)
        {
            // Only the original inactive template is registered. Live visual
            // clones never enter this table and cannot release shared assets.
            List<UnityEngine.Object> owned;
            if (root != null && TemplateAssets.TryGetValue(root, out owned))
            {
                TemplateAssets.Remove(root);
                foreach (UnityEngine.Object item in owned) if (item != null) UnityEngine.Object.Destroy(item);
            }
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        private static string SafeAssetPath(string folder, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Asset path must be relative.");
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset path escapes Assets folder.");
            return full;
        }

        private static Texture2D LoadTexture(string folder, string path, bool linear, Dictionary<string, Texture2D> cache, List<UnityEngine.Object> owned)
        {
            Texture2D texture;
            if (cache.TryGetValue(path, out texture)) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
            { name = "Resolute_" + Path.GetFileNameWithoutExtension(path), anisoLevel = 8, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
            owned.Add(texture);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(SafeAssetPath(folder, path)), false))
                throw new InvalidDataException("Invalid texture: " + path);
            texture.Apply(true, true);
            UnityEngine.Object.DontDestroyOnLoad(texture);
            cache.Add(path, texture);
            return texture;
        }

        private static async UniTask<Dictionary<string, Mesh>> ReadMeshesAsync(string path, List<UnityEngine.Object> owned, ManualLogSource log,
            StartupLoadContext loading)
        {
            var result = new Dictionary<string, Mesh>();
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (new string(r.ReadChars(4)) != "RSLM" || r.ReadInt32() != 1) throw new InvalidDataException("Mesh file header");
                int count = r.ReadInt32();
                if (count < 1 || count > 10000) throw new InvalidDataException("Mesh count");
                for (int m = 0; m < count; m++)
                {
                    if (loading != null) await loading.Step("Loading geometry " + (m + 1) + "/" + count);
                    int textLength = r.ReadInt32();
                    if (textLength < 1 || textLength > 1024) throw new InvalidDataException("Mesh name length");
                    string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(textLength));
                    int nv = r.ReadInt32(), ni = r.ReadInt32();
                    if (nv < 0 || nv > 2000000 || ni < 0 || ni > 6000000 || ni % 3 != 0) throw new InvalidDataException("Mesh dimensions: " + name);
                    var vertices = new Vector3[nv]; var normals = new Vector3[nv]; var uv = new Vector2[nv];
                    for (int i = 0; i < nv; i++)
                    {
                        vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    var triangles = new int[ni];
                    for (int i = 0; i < ni; i++)
                    {
                        triangles[i] = r.ReadInt32();
                        if (triangles[i] < 0 || triangles[i] >= nv) throw new InvalidDataException("Mesh index: " + name);
                    }
                    if (name == "rsl_hull")
                    {
                        SurfaceNormals.RepairCoplanarSeams(ref vertices, ref normals, ref uv, ref triangles, log);
                        SurfaceDamageUv.Apply(name, vertices, normals, triangles, ref uv);
                    }
                    var mesh = new Mesh { name = "Resolute_" + name, indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                    owned.Add(mesh);
                    mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.uv2 = uv;
                    // Dynamo's damage shader reads vertex color as a paint tint.
                    // White preserves the original Resolute atlas exactly.
                    mesh.colors32 = Enumerable.Repeat(new Color32(255, 255, 255, 255), vertices.Length).ToArray();
                    mesh.triangles = triangles;
                    mesh.RecalculateBounds(); mesh.RecalculateTangents();
                    UnityEngine.Object.DontDestroyOnLoad(mesh);
                    result.Add(name, mesh);
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing mesh data");
            }
            return result;
        }
        private static MeshRenderer AddRenderer(GameObject go, Mesh mesh, Material material)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            // Matched-camera tests isolate the long aft-hull bands to these
            // sub-pixel safety-net bars. Preserve their visible geometry and
            // received shadows; larger rails, hangar and weapons still cast.
            renderer.shadowCastingMode = go.name.StartsWith("rsl_flight_deck_safety_nets", StringComparison.Ordinal)
                ? ShadowCastingMode.Off : ShadowCastingMode.On;
            renderer.receiveShadows = true;
            return renderer;
        }
        private static bool IsArticulatedPivot(string name)
            => name.StartsWith("rsl_prop_", StringComparison.Ordinal) ||
               name.StartsWith("rsl_ciws_", StringComparison.Ordinal) ||
               name.StartsWith("rsl_pd_", StringComparison.Ordinal) ||
               name.StartsWith("rsl_laser_", StringComparison.Ordinal) ||
               name.StartsWith("rsl_gun_", StringComparison.Ordinal) ||
               name.StartsWith("rsl_torp_", StringComparison.Ordinal);

        private static Vector3 Vector(float[] a) { return a == null ? Vector3.zero : new Vector3(a[0], a[1], a[2]); }
    }

    // Reduced weapon and propeller meshes follow their existing live pivots.
    // It keeps a separate renderer for LOD culling and its physical damage owner.
    internal sealed class ResoluteAnimatedLod : MonoBehaviour { }
}
