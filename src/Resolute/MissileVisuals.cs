using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // The editable missile artwork already supplies URP metallic/smoothness
    // maps. It must not pass through the source ship's specular-map conversion.
    internal sealed class MissileVisuals
    {
        private readonly string folder;
        private readonly Encyclopedia encyclopedia;
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        internal MissileVisuals(string folder, Encyclopedia encyclopedia)
        { this.folder = Path.GetFullPath(folder); this.encyclopedia = encyclopedia; }

        internal Material CreateMaterial(VisualLoader.MaterialRecord record, List<Object> owned)
        {
            if (!record.id.EndsWith("_paint", StringComparison.Ordinal)) return null;
            string key = record.id.Substring(0, record.id.Length - "_paint".Length);
            string nativeKey = key == "rsl_ashm" || key == "rsl_cruise" ? "AShM1" : key == "rsl_pd" ? "AAM1" : "AAM2";
            MissileDefinition definition = encyclopedia.missiles.FirstOrDefault(d => d != null && d.jsonKey == nativeKey && d.unitPrefab != null);
            Material native = definition?.unitPrefab.GetComponentsInChildren<MeshRenderer>(true)
                .SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m != null && m.shader != null &&
                    m.shader.name == "Universal Render Pipeline/Lit" && m.HasProperty("_BaseMap") &&
                    (m.name.StartsWith("Missiles", StringComparison.Ordinal) || m.name.StartsWith("Weapons", StringComparison.Ordinal)));
            if (native == null) throw new InvalidOperationException("Native missile paint material unavailable: " + nativeKey);
            Material material = new Material(native) { name = "Resolute_" + record.id };
            owned.Add(material);
            Texture2D baseMap = Load(record.diffuse, false, owned);
            Texture2D normalMap = Load(record.normal, true, owned);
            Texture2D responseMap = Load(record.specular, true, owned);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_MainTex", baseMap);
            material.SetTexture("_BumpMap", normalMap);
            material.SetTexture("_MetallicGlossMap", responseMap);
            material.SetTexture("_OcclusionMap", responseMap);
            material.SetFloat("_BumpScale", 1f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 1f);
            material.SetFloat("_GlossMapScale", 1f);
            material.SetFloat("_OcclusionStrength", 1f);
            material.SetTextureScale("_BaseMap", Vector2.one);
            material.SetTextureOffset("_BaseMap", Vector2.zero);
            material.SetTextureScale("_BumpMap", Vector2.one);
            material.SetTextureOffset("_BumpMap", Vector2.zero);
            material.SetTextureScale("_MetallicGlossMap", Vector2.one);
            material.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
            material.SetTextureScale("_OcclusionMap", Vector2.one);
            material.SetTextureOffset("_OcclusionMap", Vector2.zero);
            Object.DontDestroyOnLoad(material);
            return material;
        }

        private Texture2D Load(string relative, bool linear, List<Object> owned)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Missile paint map path must be relative.");
            string path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Missile paint map escapes the asset folder.");
            string key = path + (linear ? ":linear" : ":srgb");
            Texture2D texture;
            if (textures.TryGetValue(key, out texture)) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) {
                name = "Resolute_" + Path.GetFileNameWithoutExtension(relative),
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4
            };
            owned.Add(texture);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) throw new InvalidDataException("Invalid missile map: " + relative);
            texture.Compress(true);
            texture.Apply(true, true);
            Object.DontDestroyOnLoad(texture);
            textures.Add(key, texture);
            return texture;
        }
    }
}
