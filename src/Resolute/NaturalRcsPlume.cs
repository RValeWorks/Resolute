using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    // A shared, continuous emissive volume approximation. Three crossed soft
    // ribbons stay fixed to the nozzle; there is no particle simulation, wake,
    // inherited world velocity, camera-facing rotation or per-frame allocation.
    internal static class NaturalRcsPlume
    {
        internal const float Length = 2.2f, BaseWidth = .055f, TipWidth = .11f;
        internal const int Planes = 3, Across = 5, Stations = 8;
        private static Mesh mesh;
        private static Material material;

        internal static MeshRenderer Add(Transform parent, Vector3 position, Vector3 direction, int index)
        {
            if (material == null)
            {
                // This native shader is already present on the game's rocket
                // flames. A white texel leaves all shape/glow/fade to vertices.
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) throw new System.InvalidOperationException("Native unlit RCS effect shader is unavailable.");
                material = new Material(shader) { name = "Resolute continuous RCS glow", renderQueue = 3000 };
                material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Surface", 1f); material.SetFloat("_Blend", 2f);
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)BlendMode.One);
                material.SetFloat("_SrcBlendAlpha", (float)BlendMode.Zero);
                material.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
                material.SetFloat("_ZWrite", 0f); material.SetFloat("_Cull", (float)CullMode.Off);
                material.SetFloat("_ColorMode", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.DisableKeyword("_SOFTPARTICLES_ON"); material.DisableKeyword("_FADING_ON");
                material.DisableKeyword("_DISTORTION_ON"); material.DisableKeyword("_FLIPBOOKBLENDING_ON");
                material.DisableKeyword("_COLOROVERLAY_ON"); material.DisableKeyword("_COLORCOLOR_ON");
                material.DisableKeyword("_COLORADDSUBDIFF_ON");
            }
            if (mesh == null) mesh = BuildMesh();
            var item = new GameObject("ReactionControlJet" + index);
            item.transform.SetParent(parent, false);
            item.transform.localPosition = position;
            item.transform.localRotation = Quaternion.LookRotation(direction);
            item.transform.localScale = Vector3.one;
            item.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = item.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = false;
            return renderer;
        }

        private static Mesh BuildMesh()
        {
            float[] z = { 0f, .02f, .065f, .32f, .85f, 1.4f, 1.85f, Length };
            float[] alpha = { .7f, .85f, .85f, .8f, .65f, .42f, .17f, 0f };
            var vertices = new Vector3[Planes * Stations * Across];
            var colors = new Color[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[Planes * (Stations - 1) * (Across - 1) * 6];
            int t = 0;
            for (int plane = 0; plane < Planes; plane++)
            {
                float angle = plane * Mathf.PI / Planes;
                Vector3 side = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                for (int station = 0; station < Stations; station++)
                for (int column = 0; column < Across; column++)
                {
                    int n = (plane * Stations + station) * Across + column;
                    float u = (float)column / (Across - 1), cross = 2f * u - 1f;
                    float width = Mathf.Lerp(BaseWidth, TipWidth, z[station] / Length);
                    vertices[n] = side * (cross * width * .5f) + Vector3.forward * z[station];
                    Color color = Color.Lerp(new Color(1.6f, .12f, .045f), new Color(1.6f, 1.6f, 1.6f), Mathf.Clamp01(z[station] / .065f));
                    color.a = alpha[station] * (1f - Mathf.Abs(cross));
                    colors[n] = color; uv[n] = new Vector2(u, z[station] / Length);
                    if (station == Stations - 1 || column == Across - 1) continue;
                    triangles[t++] = n; triangles[t++] = n + Across; triangles[t++] = n + 1;
                    triangles[t++] = n + 1; triangles[t++] = n + Across; triangles[t++] = n + Across + 1;
                }
            }
            var value = new Mesh { name = "RCS shared continuous tapered glow" };
            value.vertices = vertices; value.colors = colors; value.uv = uv; value.triangles = triangles;
            value.RecalculateNormals(); value.RecalculateBounds();
            return value;
        }
    }
}
