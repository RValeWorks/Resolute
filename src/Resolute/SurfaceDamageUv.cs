using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace Resolute
{
    // The shipped shader samples paint/normal/response at UV0, but samples its
    // damage and leak masks at UV0 * 2.36. Whole-number chart translations leave
    // every unscaled Repeat texture unchanged and decorrelate repeated damage.
    internal static class SurfaceDamageUv
    {
        internal static object LastAudit { get; private set; }
        private const float MinimumChartArea = 25f;

        private struct CornerKey : IEquatable<CornerKey>
        {
            private readonly int x, y, z, u, v;
            internal CornerKey(Vector3 point, Vector2 uv)
            {
                x = Mathf.RoundToInt(point.x * 10000f); y = Mathf.RoundToInt(point.y * 10000f); z = Mathf.RoundToInt(point.z * 10000f);
                u = Mathf.RoundToInt(uv.x * 10000f); v = Mathf.RoundToInt(uv.y * 10000f);
            }
            public bool Equals(CornerKey other) => x == other.x && y == other.y && z == other.z && u == other.u && v == other.v;
            public override bool Equals(object other) => other is CornerKey && Equals((CornerKey)other);
            public override int GetHashCode() { unchecked { return ((((x * 397 ^ y) * 397 ^ z) * 397 ^ u) * 397) ^ v; } }
        }

        internal static void Apply(string name, Vector3[] positions, Vector3[] normals, int[] triangles, ref Vector2[] uv)
        {
            string geometryBefore = GeometryHash(positions, normals, triangles);
            Vector2[] original = (Vector2[])uv.Clone();
            int count = triangles.Length / 3;
            int[] parents = Enumerable.Range(0, count).ToArray();
            var corners = new Dictionary<CornerKey, int>();
            for (int triangle = 0; triangle < count; triangle++)
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = triangles[triangle * 3 + corner];
                var key = new CornerKey(positions[vertex], original[vertex]);
                int prior;
                // Sharing even one original position+UV corner merges charts
                // conservatively. Never introduce a phase split inside a
                // continuous authored UV chart or at a normal-only seam.
                if (corners.TryGetValue(key, out prior)) Join(parents, triangle, prior);
                else corners.Add(key, triangle);
            }
            var chartAreas = new Dictionary<int, float>();
            for (int triangle = 0; triangle < count; triangle++)
            {
                int root = Find(parents, triangle);
                Vector3 a = positions[triangles[triangle * 3]], b = positions[triangles[triangle * 3 + 1]], c = positions[triangles[triangle * 3 + 2]];
                float area; chartAreas.TryGetValue(root, out area);
                chartAreas[root] = area + Vector3.Cross(b - a, c - a).magnitude * .5f;
            }
            var offsets = new Dictionary<int, Vector2>();
            foreach (var chart in chartAreas.OrderBy(c => c.Key))
            {
                if (chart.Value < MinimumChartArea) continue;
                uint hash;
                unchecked
                {
                    hash = (uint)chart.Key * 0x9e3779b9u + 7139u;
                    hash = (hash ^ (hash >> 16)) * 0x7feb352du;
                    hash = (hash ^ (hash >> 15)) * 0x846ca68bu;
                    hash ^= hash >> 16;
                }
                int x = (int)(hash % 9u) - 4, y = (int)((hash / 9u) % 9u) - 4;
                if (x == 0 && y == 0) x = 1;
                offsets.Add(chart.Key, new Vector2(x, y));
            }
            var vertexCharts = new Dictionary<int, int>();
            for (int triangle = 0; triangle < count; triangle++)
            {
                int chart = Find(parents, triangle);
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = triangles[triangle * 3 + corner], prior;
                    if (vertexCharts.TryGetValue(vertex, out prior) && prior != chart)
                        throw new InvalidOperationException("UV chart phase would split an existing source vertex.");
                    vertexCharts[vertex] = chart;
                }
            }
            int shifted = 0; float maximumModuloError = 0f;
            foreach (var entry in vertexCharts)
            {
                Vector2 offset;
                if (!offsets.TryGetValue(entry.Value, out offset)) continue;
                uv[entry.Key] = original[entry.Key] + offset;
                Vector2 delta = uv[entry.Key] - original[entry.Key] - offset;
                maximumModuloError = Mathf.Max(maximumModuloError, Mathf.Abs(delta.x), Mathf.Abs(delta.y)); shifted++;
            }
            string geometryAfter = GeometryHash(positions, normals, triangles);
            if (geometryAfter != geometryBefore || maximumModuloError > .000002f)
                throw new InvalidOperationException("UV chart phase failed source preservation.");
            LastAudit = new Dictionary<string, object> {
                ["mesh"] = name, ["sourceVertices"] = positions.Length, ["sourceTriangles"] = count,
                ["conservativeUvCharts"] = chartAreas.Count, ["shiftedCharts"] = offsets.Count, ["shiftedVertices"] = shifted,
                ["minimumChartAreaM2"] = MinimumChartArea, ["maximumUvModuloError"] = maximumModuloError,
                ["positionNormalIndexSha256Before"] = geometryBefore, ["positionNormalIndexSha256After"] = geometryAfter,
                ["originalUvSha256"] = UvHash(original), ["shiftedUvSha256"] = UvHash(uv),
                ["operation"] = "One integer UV0 translation per sizeable existing UV chart; source vertices, normals and indices are unchanged. Unscaled Repeat textures preserve their coordinates modulo one. Native damage/leak masks gain a chart-dependent phase through their existing 2.36 tiling.",
                ["charts"] = offsets.Select(o => new Dictionary<string, object> { ["firstTriangle"] = o.Key,
                    ["areaM2"] = chartAreas[o.Key], ["offset"] = new[] { o.Value.x, o.Value.y } }).ToArray() };
        }

        private static int Find(int[] parents, int index)
        { while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; } return index; }
        private static void Join(int[] parents, int a, int b)
        { a = Find(parents, a); b = Find(parents, b); if (a != b) parents[Math.Max(a, b)] = Math.Min(a, b); }
        private static string GeometryHash(Vector3[] positions, Vector3[] normals, int[] triangles)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                foreach (Vector3[] array in new[] { positions, normals }) foreach (Vector3 v in array) { writer.Write(v.x); writer.Write(v.y); writer.Write(v.z); }
                foreach (int index in triangles) writer.Write(index);
                writer.Flush(); return Hash(stream.ToArray());
            }
        }
        private static string UvHash(Vector2[] uv)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { foreach (Vector2 v in uv) { writer.Write(v.x); writer.Write(v.y); } writer.Flush(); return Hash(stream.ToArray()); }
        }
        private static string Hash(byte[] bytes)
        { using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
