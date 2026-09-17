using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Shallow casing artwork, shared between all copies of each interceptor.
    // No drilled holes, added colliders, per-frame textures or text objects.
    internal static class NaturalRcsSurfaceDetails
    {
        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();
        internal static void Add(Transform model, Transform root, Vector3 point, Vector3 normal, string label, bool canted)
        {
            string key = label + (canted ? "-canted" : "-radial");
            Material material;
            if (!Materials.TryGetValue(key, out material))
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) return;
                material = new Material(shader) { name = "ResoluteRcsCasing_" + key, renderQueue = 2450 };
                material.SetTexture("_BaseMap", Artwork(label, canted));
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_AlphaClip", 1f); material.SetFloat("_Cutoff", .12f);
                material.SetFloat("_Smoothness", .12f); material.SetFloat("_Metallic", .15f);
                material.EnableKeyword("_ALPHATEST_ON");
                Materials.Add(key, material);
            }
            var item = new GameObject("RCS surface " + label);
            item.transform.SetParent(model, false);
            var mesh = new Mesh { name = "RCS flush curved marking" };
            const int segments = 4;
            var vertices = new Vector3[(segments + 1) * 2];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[segments * 6];
            float radius = new Vector2(point.x, point.y).magnitude;
            float halfWidth = canted ? .024f : .034f;
            float halfLength = canted ? .039f : .058f;
            float angle = Mathf.Atan2(normal.y, normal.x);
            Matrix4x4 conversion = model.worldToLocalMatrix * root.localToWorldMatrix;
            for (int n = 0; n <= segments; n++)
            {
                float u = (float)n / segments;
                float theta = angle + (u * 2f - 1f) * halfWidth / Mathf.Max(.01f, radius);
                Vector3 surface = new Vector3(Mathf.Cos(theta) * (radius + .001f), Mathf.Sin(theta) * (radius + .001f), point.z);
                vertices[n * 2] = conversion.MultiplyPoint3x4(surface - Vector3.forward * halfLength);
                vertices[n * 2 + 1] = conversion.MultiplyPoint3x4(surface + Vector3.forward * halfLength);
                uv[n * 2] = new Vector2(u, 0f); uv[n * 2 + 1] = new Vector2(u, 1f);
                if (n == segments) continue;
                int t = n * 6, a = n * 2;
                triangles[t] = a; triangles[t + 1] = a + 2; triangles[t + 2] = a + 1;
                triangles[t + 3] = a + 1; triangles[t + 4] = a + 2; triangles[t + 5] = a + 3;
            }
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            item.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = item.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Texture2D Artwork(string label, bool canted)
        {
            const int size = 128;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x - 63.5f) / 31f, v = (y - 65f) / (canted ? 18f : 27f);
                float r = Mathf.Sqrt(u * u + v * v);
                Color color = Color.clear;
                if (r < .77f) color = new Color(.055f, .06f, .062f, 1f);
                else if (r < .92f) color = new Color(.21f, .22f, .22f, 1f);
                else if (r < 1.04f) color = new Color(.29f, .28f, .265f, .55f);
                if ((x == 17 || x == 110) && y >= 62 && y <= 68) color = new Color(.22f, .23f, .23f, .8f);
                pixels[y * size + x] = color;
            }
            DrawText(pixels, size, "RCS", 14);
            DrawText(pixels, size, label, 105);
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            { name = "RCS subdued port " + label, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            texture.SetPixels(pixels); texture.Apply(true, true); return texture;
        }
        private static void DrawText(Color[] pixels, int width, string text, int y)
        {
            var glyphs = new Dictionary<char, string> {
                ['R']="110101110101101", ['C']="111100100100111", ['S']="111100111001111",
                ['F']="111100110100100", ['W']="101101101111101", ['D']="110101101101110",
                ['A']="010101111101101", ['T']="111010010010010", ['L']="100100100100111" };
            int start = (width - (text.Length * 8 - 2)) / 2;
            for (int c = 0; c < text.Length; c++)
            {
                string glyph; if (!glyphs.TryGetValue(text[c], out glyph)) continue;
                for (int row = 0; row < 5; row++) for (int col = 0; col < 3; col++)
                    if (glyph[row * 3 + col] == '1')
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                            pixels[(y + (4-row)*2 + dy) * width + start + c*8 + col*2 + dx] = new Color(.25f, .26f, .26f, .85f);
            }
        }
    }
}
