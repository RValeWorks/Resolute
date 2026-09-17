using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    // The source uses Marmoset specular/gloss maps. The game's opaque ship
    // structure material uses URP's metallic workflow. Reuse that shipped
    // keyword combination and convert gloss only; source RGB is not metallic.
    internal static class NavalMaterials
    {
        internal static readonly Dictionary<string, object> DamageSamplerAudit = new Dictionary<string, object>();
        internal static object NativePlatingAudit { get; private set; }
        internal static object NativeDeckAudit { get; private set; }
        private static Material nativePlating;
        private static Material nativeDeck;
        private static Material nativeInterior;
        internal static Material NativeDeck
        {
            get
            {
                if (nativeDeck != null) return nativeDeck;
                Material donor = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m =>
                    m != null && m.name == "destroyer1_deck" && UsesNativeDamage(m));
                if (donor == null) throw new InvalidOperationException("Native Dynamo deck material is unavailable.");
                nativeDeck = new Material(donor) { name = "Resolute native metric deck" };
                nativeDeck.SetTexture("_AO", Texture2D.whiteTexture);
                nativeDeck.SetFloat("_HitPoints", 100f);
                var maps = new List<object>();
                foreach (string property in new[] { "_BaseColor", "_Normal", "_MetallicRoughness", "_BaseColorDamage",
                    "_NormalDamage", "_MetallicRoughnessDamage", "_Normal_Crinkle", "_PaintMask", "_DamageMask", "_LeakMask" })
                {
                    Texture texture = nativeDeck.GetTexture(property);
                    if (texture == null || texture != donor.GetTexture(property) ||
                        texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Repeat)
                        throw new InvalidOperationException("Native deck reference or repeat sampler differs: " + property);
                    maps.Add(new Dictionary<string, object> { ["property"] = property, ["texture"] = texture.name,
                        ["donorReferencePreserved"] = true, ["wrapU"] = texture.wrapModeU.ToString(), ["wrapV"] = texture.wrapModeV.ToString() });
                }
                Vector4 tiling = nativeDeck.GetVector("_DamageTiling");
                NativeDeckAudit = new Dictionary<string, object> { ["donor"] = donor.name, ["material"] = nativeDeck.name,
                    ["shader"] = nativeDeck.shader.name, ["textures"] = maps, ["allNativeMapReferencesPreserved"] = true,
                    ["nativeDamageTiling"] = new[] { tiling.x, tiling.y, tiling.z, tiling.w },
                    ["nativeDamageTilingPreserved"] = tiling.Equals(donor.GetVector("_DamageTiling")),
                    ["sourceSpecificAoRemoved"] = nativeDeck.GetTexture("_AO") == Texture2D.whiteTexture };
                UnityEngine.Object.DontDestroyOnLoad(nativeDeck);
                return nativeDeck;
            }
        }
        internal static Material NativePlating
        {
            get
            {
                if (nativePlating != null) return nativePlating;
                Material donor = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m =>
                    m != null && m.name == "destroyer1_plating" && UsesNativeDamage(m));
                if (donor == null) throw new InvalidOperationException("Native Dynamo plating is unavailable.");
                nativePlating = new Material(donor) { name = "Resolute native metric plating" };
                nativePlating.SetTexture("_AO", Texture2D.whiteTexture);
                nativePlating.SetFloat("_HitPoints", 100f);
                var maps = new List<object>();
                foreach (string property in new[] { "_BaseColor", "_Normal", "_MetallicRoughness", "_BaseColorDamage",
                    "_NormalDamage", "_MetallicRoughnessDamage", "_Normal_Crinkle", "_PaintMask", "_DamageMask", "_LeakMask" })
                {
                    Texture texture = nativePlating.GetTexture(property);
                    bool preserved = texture != null && texture == donor.GetTexture(property);
                    if (!preserved || texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Repeat)
                        throw new InvalidOperationException("Native plating reference or repeat sampler differs: " + property);
                    maps.Add(new Dictionary<string, object> { ["property"] = property, ["texture"] = texture.name,
                        ["donorReferencePreserved"] = preserved, ["wrapU"] = texture.wrapModeU.ToString(), ["wrapV"] = texture.wrapModeV.ToString() });
                }
                Vector4 tiling = nativePlating.GetVector("_DamageTiling");
                NativePlatingAudit = new Dictionary<string, object> { ["donor"] = donor.name, ["material"] = nativePlating.name,
                    ["shader"] = nativePlating.shader.name, ["textures"] = maps, ["allNativeMapReferencesPreserved"] = true,
                    ["nativeDamageTiling"] = new[] { tiling.x, tiling.y, tiling.z, tiling.w },
                    ["nativeDamageTilingPreserved"] = tiling.Equals(donor.GetVector("_DamageTiling")),
                    ["sourceSpecificAoRemoved"] = nativePlating.GetTexture("_AO") == Texture2D.whiteTexture };
                UnityEngine.Object.DontDestroyOnLoad(nativePlating);
                return nativePlating;
            }
        }
        internal static bool UsesNativeDamage(Material material)
        {
            return material != null && material.shader != null &&
                material.shader.name == "Shader Graphs/ShipTilingShader" && material.HasProperty("_HitPoints");
        }

        internal static Material CreateHullPaint(string name, Texture baseMap, Texture normalMap,
            Texture responseMap, List<UnityEngine.Object> owned)
        {
            Material native = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m =>
                m != null && m.name == "destroyer1_plating" && UsesNativeDamage(m));
            if (native == null) throw new InvalidOperationException("The native Dynamo damage material is unavailable.");
            var material = new Material(native) { name = name };
            owned.Add(material);
            // Keep the shipped shader variant and its native damaged steel,
            // damage/leak/paint masks and crinkle normal. Only the intact atlas
            // and its PBR response belong to Resolute. The native AO atlas is
            // specific to Dynamo's UV1, so it cannot be applied to this vessel.
            material.SetTexture("_BaseColor", baseMap);
            material.SetTexture("_Normal", normalMap);
            material.SetTexture("_MetallicRoughness", responseMap);
            material.SetTexture("_AO", Texture2D.whiteTexture);
            material.SetFloat("_HitPoints", 100f);
            // Measured sqrt(UV triangle area / surface area): Resolute hull
            // 0.010600/m, Dynamo Hull_R 0.032510/m. Native mask tiling is .77,
            // hence 2.36 here. The former guess of 10 made holes repeat 4.2x
            // more densely than the native side hull, visibly like a grid.
            material.SetVector("_DamageTiling", new Vector4(2.36f, 2.36f, 0f, 0f));
            var samplers = new List<object>();
            foreach (string property in new[] { "_BaseColor", "_Normal", "_MetallicRoughness", "_BaseColorDamage", "_NormalDamage", "_MetallicRoughnessDamage", "_Normal_Crinkle", "_PaintMask", "_DamageMask", "_LeakMask" })
            {
                Texture texture = material.GetTexture(property);
                if (texture == null || texture.wrapModeU != TextureWrapMode.Repeat || texture.wrapModeV != TextureWrapMode.Repeat)
                    throw new InvalidOperationException("Native damage UV phase requires Repeat sampling: " + property);
                samplers.Add(new Dictionary<string, object> { ["property"] = property, ["texture"] = texture.name,
                    ["instanceId"] = texture.GetInstanceID(), ["wrapU"] = texture.wrapModeU.ToString(), ["wrapV"] = texture.wrapModeV.ToString() });
            }
            DamageSamplerAudit[name] = new Dictionary<string, object> { ["textures"] = samplers, ["allSamplersRepeat"] = true,
                ["sourceBaseReferencePreserved"] = material.GetTexture("_BaseColor") == baseMap,
                ["sourceNormalReferencePreserved"] = material.GetTexture("_Normal") == normalMap,
                ["sourceResponseReferencePreserved"] = material.GetTexture("_MetallicRoughness") == responseMap };
            UnityEngine.Object.DontDestroyOnLoad(material);
            return material;
        }

        internal static Material CreateInterior(Material fallback)
        {
            if (nativeInterior != null) return nativeInterior;
            Material native = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m =>
                m != null && m.name == "ship_structure" && m.shader != null &&
                m.shader.name == "Universal Render Pipeline/Lit");
            var material = new Material(native != null ? native : fallback) { name = "Resolute exposed structural steel" };
            if (native == null)
            {
                ConfigurePaint(material, "structural steel");
                material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                material.SetColor("_BaseColor", new Color(.12f, .13f, .14f));
            }
            UnityEngine.Object.DontDestroyOnLoad(material);
            nativeInterior = material;
            return material;
        }

        internal static Material FindNativePaint(Shader shader)
        {
            Material[] materials = Resources.FindObjectsOfTypeAll<Material>();
            return materials.FirstOrDefault(m => m != null && m.name == "ship_structure" && m.shader == shader)
                ?? materials.FirstOrDefault(m => m != null && m.shader == shader && m.name == "NavalSupplyContainer1");
        }

        internal static void ConfigurePaint(Material material, string id)
        {
            // Enabling a stripped specular variant at runtime can select a
            // metallic variant instead. Its default white metallic texture then
            // makes the hull reflective even when the _Metallic scalar is zero.
            material.shaderKeywords = new[] { "_METALLICSPECGLOSSMAP", "_NORMALMAP", "_OCCLUSIONMAP" };
            material.SetFloat("_WorkflowMode", 1f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 1f);
            material.SetFloat("_GlossMapScale", 1f);
            material.SetFloat("_Glossiness", 0.18f);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            material.SetFloat("_SpecularHighlights", 1f);
            material.SetFloat("_EnvironmentReflections", 1f);
            material.SetFloat("_ClearCoatMask", 0f);
            material.SetFloat("_ClearCoatSmoothness", 0f);
            material.SetFloat("_OcclusionStrength", 0f);
            material.SetTexture("_OcclusionMap", Texture2D.whiteTexture);
            material.SetTexture("_SpecGlossMap", null);
            material.SetTexture("_ParallaxMap", null);
            material.SetTexture("_EmissionMap", null);
            material.SetColor("_EmissionColor", Color.black);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_Cull", 2f);
            material.SetFloat("_ZWrite", 1f);
            material.SetFloat("_SrcBlend", 1f);
            material.SetFloat("_DstBlend", 0f);
            material.SetFloat("_SrcBlendAlpha", 1f);
            material.SetFloat("_DstBlendAlpha", 0f);
            material.SetFloat("_ReceiveShadows", 1f);
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)RenderQueue.Geometry;
        }

        internal static Texture2D LoadResponse(string path, string materialId, List<UnityEngine.Object> owned, bool hullPaint = false)
        {
            bool optical = materialId.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true)
            {
                name = "ResolutePaintResponse_" + Path.GetFileNameWithoutExtension(materialId),
                anisoLevel = 8, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat
            };
            owned.Add(texture);
            if (path != null && !ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
                throw new InvalidDataException("Invalid source gloss texture: " + path);
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                float authoredGloss = path != null ? pixels[i].a / 255f : 0.35f;
                float smoothness = optical ? Mathf.Lerp(0.38f, 0.62f, authoredGloss) :
                    hullPaint ? Mathf.Lerp(0.16f, 0.38f, authoredGloss) : Mathf.Lerp(0.06f, 0.28f, authoredGloss);
                pixels[i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(smoothness * 255f));
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            UnityEngine.Object.DontDestroyOnLoad(texture);
            return texture;
        }

        internal static Texture2D FlatNormal(List<UnityEngine.Object> owned)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { name = "ResoluteFlatNormal" };
            owned.Add(texture);
            texture.SetPixels32(new[] { new Color32(128, 128, 255, 255), new Color32(128, 128, 255, 255),
                new Color32(128, 128, 255, 255), new Color32(128, 128, 255, 255) });
            texture.Apply(false, true);
            UnityEngine.Object.DontDestroyOnLoad(texture);
            return texture;
        }
    }
}
