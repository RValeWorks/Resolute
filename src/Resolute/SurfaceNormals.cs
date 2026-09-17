using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace Resolute
{
    // A source hull audit found inconsistent normals across coplanar triangle
    // seams, including corners facing away from their visible triangle. Repair
    // shading only: positions, UVs, triangle order and geometric creases stay.
    internal static class SurfaceNormals
    {
        private struct PositionKey : IEquatable<PositionKey>
        {
            private readonly int x, y, z;
            internal PositionKey(Vector3 p)
            {
                x = Mathf.RoundToInt(p.x * 10000f); y = Mathf.RoundToInt(p.y * 10000f); z = Mathf.RoundToInt(p.z * 10000f);
            }
            public bool Equals(PositionKey other) { return x == other.x && y == other.y && z == other.z; }
            public override bool Equals(object obj) { return obj is PositionKey && Equals((PositionKey)obj); }
            public override int GetHashCode() { unchecked { return ((x * 397) ^ y) * 397 ^ z; } }
        }

        private struct Corner
        {
            internal int Vertex;
            internal Vector3 Face;
            internal float Weight;
        }

        private struct OutputKey : IEquatable<OutputKey>
        {
            private readonly int original, x, y, z;
            internal OutputKey(int vertex, Vector3 n)
            {
                original = vertex; x = Mathf.RoundToInt(n.x * 100000f); y = Mathf.RoundToInt(n.y * 100000f); z = Mathf.RoundToInt(n.z * 100000f);
            }
            public bool Equals(OutputKey other) { return original == other.original && x == other.x && y == other.y && z == other.z; }
            public override bool Equals(object obj) { return obj is OutputKey && Equals((OutputKey)obj); }
            public override int GetHashCode() { unchecked { return (((original * 397) ^ x) * 397 ^ y) * 397 ^ z; } }
        }

        internal static void RepairCoplanarSeams(ref Vector3[] vertices, ref Vector3[] normals, ref Vector2[] uv,
            ref int[] triangles, ManualLogSource log)
        {
            var groups = new Dictionary<PositionKey, List<Corner>>();
            var corners = new Corner[triangles.Length];
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                Vector3 a = vertices[triangles[triangle]], b = vertices[triangles[triangle + 1]], c = vertices[triangles[triangle + 2]];
                Vector3 face = Vector3.Cross(b - a, c - a).normalized;
                for (int k = 0; k < 3; k++)
                {
                    int index = triangle + k, vertex = triangles[index];
                    Vector3 p = vertices[vertex];
                    Vector3 e1 = vertices[triangles[triangle + (k + 1) % 3]] - p;
                    Vector3 e2 = vertices[triangles[triangle + (k + 2) % 3]] - p;
                    var corner = new Corner { Vertex = vertex, Face = face, Weight = Mathf.Max(0.001f, Vector3.Angle(e1, e2)) };
                    corners[index] = corner;
                    PositionKey key = new PositionKey(p);
                    List<Corner> atPosition;
                    if (!groups.TryGetValue(key, out atPosition)) groups.Add(key, atPosition = new List<Corner>());
                    atPosition.Add(corner);
                }
            }

            var positionsOut = new List<Vector3>();
            var normalsOut = new List<Vector3>();
            var uvOut = new List<Vector2>();
            var outputIndices = new int[triangles.Length];
            var lookup = new Dictionary<OutputKey, int>();
            int repairedCorners = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                Corner current = corners[i];
                Vector3 old = normals[current.Vertex].normalized;
                Vector3 sum = Vector3.zero;
                if (current.Face.sqrMagnitude > 0.5f)
                    foreach (Corner neighbor in groups[new PositionKey(vertices[current.Vertex])])
                    {
                        // Only the same geometric plane participates. Adjacent
                        // bevels, curved hull panels and sharp edges keep their
                        // own authored smoothing behavior.
                        if (Vector3.Dot(current.Face, neighbor.Face) < 0.99999f) continue;
                        Vector3 normal = normals[neighbor.Vertex].normalized;
                        if (normal.sqrMagnitude < 0.5f || Vector3.Dot(normal, current.Face) < 0.15f) normal = current.Face;
                        sum += normal * neighbor.Weight;
                    }
                Vector3 corrected = sum.sqrMagnitude > 0.00001f ? sum.normalized : old;
                if (corrected.sqrMagnitude < 0.5f) corrected = Vector3.up;
                if (Vector3.Dot(old, corrected) < 0.99999f) repairedCorners++;
                var key = new OutputKey(current.Vertex, corrected);
                int output;
                if (!lookup.TryGetValue(key, out output))
                {
                    output = positionsOut.Count;
                    lookup.Add(key, output);
                    positionsOut.Add(vertices[current.Vertex]);
                    normalsOut.Add(corrected);
                    uvOut.Add(uv[current.Vertex]);
                }
                outputIndices[i] = output;
            }
            vertices = positionsOut.ToArray(); normals = normalsOut.ToArray(); uv = uvOut.ToArray(); triangles = outputIndices;
            log.LogInfo("Resolute hull shading: repaired " + repairedCorners + " inconsistent face-corner normals; geometric positions and UVs preserved.");
        }
    }
}
