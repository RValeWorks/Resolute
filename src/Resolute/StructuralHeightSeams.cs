using System;
using System.Collections.Generic;
using UnityEngine;
using Vertex = Resolute.StructuralGeometry.Vertex;
using Triangle = Resolute.StructuralGeometry.Triangle;
using Section = Resolute.StructuralGeometry.Section;

namespace Resolute
{
    // A second, invertible piecewise-affine ownership map in the YZ plane.
    // Source skin is mapped back after clipping, so its authored exterior,
    // normals and UVs are retained. Neighbouring compartments share each cut.
    internal static class StructuralHeightSeams
    {
        private const float Y0 = 9.7f, YStep = 3.5f, Z0 = -30.6f, ZStep = 6f, Shift = 2.4f;
        private static readonly float[] Offsets = { 0f, 1.6f, -1.3f, 2.4f, -1.8f, .9f, -1.1f, .5f, 0f };
        private struct Key : IEquatable<Key>
        {
            internal int Y, Z, Half;
            internal Key(int y, int z, int half) { Y = y; Z = z; Half = half; }
            public bool Equals(Key other) => Y == other.Y && Z == other.Z && Half == other.Half;
            public override bool Equals(object value) => value is Key other && Equals(other);
            public override int GetHashCode() => unchecked((Y * 397 ^ Z) * 397 ^ Half);
        }
        private sealed class Tile
        {
            internal Key Key;
            internal Vector2[] Source, Mapped;
        }
        private static readonly Dictionary<Key, Tile> Tiles = new Dictionary<Key, Tile>();

        private static Tile Get(int y, int z, int half)
        {
            var key = new Key(y, z, half);
            if (Tiles.TryGetValue(key, out Tile tile)) return tile;
            int[,] corners = ((y + z) & 1) == 0
                ? (half == 0 ? new[,] { { 0, 0 }, { 1, 0 }, { 1, 1 } } : new[,] { { 0, 0 }, { 1, 1 }, { 0, 1 } })
                : (half == 0 ? new[,] { { 0, 0 }, { 1, 0 }, { 0, 1 } } : new[,] { { 1, 0 }, { 1, 1 }, { 0, 1 } });
            tile = new Tile { Key = key, Source = new Vector2[3], Mapped = new Vector2[3] };
            for (int i = 0; i < 3; i++)
            {
                int iy = y + corners[i, 0], iz = z + corners[i, 1];
                float py = Y0 + iy * YStep, pz = Z0 + iz * ZStep;
                float offset = iy >= 0 && iy < Offsets.Length ? Offsets[iy] : 0f;
                // All displacement is zero before/after the tower. In
                // particular z=32.4 and the forward gun/VLS plinths stay put.
                float envelope = Mathf.Clamp01(iz) * Mathf.Clamp01(10 - iz);
                tile.Source[i] = new Vector2(py, pz);
                tile.Mapped[i] = new Vector2(py, pz + offset * envelope);
            }
            Tiles.Add(key, tile);
            return tile;
        }

        private static float Side(Vector2 a, Vector2 b, Vector3 p)
            => (b.x - a.x) * (p.z - a.y) - (b.y - a.y) * (p.y - a.x);

        private static Tile At(Vector3 p, bool forward)
        {
            float gy = (p.y - Y0) / YStep, gz = (p.z - Z0) / ZStep;
            int y = Mathf.FloorToInt(gy), z = Mathf.FloorToInt(gz);
            if (forward)
            {
                float u = gy - y, v = gz - z;
                return Get(y, z, ((y + z) & 1) == 0 ? (u >= v ? 0 : 1) : (u + v <= 1 ? 0 : 1));
            }
            Tile best = null; float closest = float.NegativeInfinity;
            for (int iz = z - 1; iz <= z + 1; iz++) for (int half = 0; half < 2; half++)
            {
                Tile tile = Get(y, iz, half);
                float score = Mathf.Min(Side(tile.Mapped[0], tile.Mapped[1], p),
                    Side(tile.Mapped[1], tile.Mapped[2], p), Side(tile.Mapped[2], tile.Mapped[0], p));
                if (score > closest) { closest = score; best = tile; }
                if (score >= 0) return tile;
            }
            if (best == null || closest < -.003f) throw new InvalidOperationException("No inverse height-seam tile at " + p);
            return best;
        }

        private static Vector3 Transform(Vector3 p, Tile tile, bool forward)
        {
            Vector2[] from = forward ? tile.Source : tile.Mapped, to = forward ? tile.Mapped : tile.Source;
            Vector2 a = from[1] - from[0], b = from[2] - from[0];
            float dy = p.y - from[0].x, dz = p.z - from[0].y;
            float determinant = a.x * b.y - a.y * b.x;
            float u = (dy * b.y - dz * b.x) / determinant, v = (a.x * dz - a.y * dy) / determinant;
            // Y is invariant; retaining its exact bits also stabilizes seams
            // at the native deck plane and avoids repeated inverse drift.
            return new Vector3(p.x, p.y, to[0].y + u * (to[1].y - to[0].y) + v * (to[2].y - to[0].y));
        }

        internal static Vector3 MapPoint(Vector3 point, bool forward)
            => Transform(point, At(point, forward), forward);

        private static List<Vertex> Clip(List<Vertex> input, Vector2[] domain)
        {
            for (int edge = 0; edge < 3 && input.Count > 0; edge++)
            {
                var output = new List<Vertex>(input.Count + 1);
                for (int i = 0; i < input.Count; i++)
                {
                    Vertex a = input[i], b = input[(i + 1) % input.Count];
                    float da = Side(domain[edge], domain[(edge + 1) % 3], a.P);
                    float db = Side(domain[edge], domain[(edge + 1) % 3], b.P);
                    if (da >= 0) output.Add(a);
                    if ((da >= 0) != (db >= 0)) output.Add(Vertex.Lerp(a, b, da / (da - db)));
                }
                input = output;
            }
            return input;
        }

        internal static Section Map(Section input, bool forward)
        {
            var result = new Section();
            foreach (Triangle triangle in input.Triangles)
            {
                float minY = Mathf.Min(triangle.A.P.y, triangle.B.P.y, triangle.C.P.y);
                float maxY = Mathf.Max(triangle.A.P.y, triangle.B.P.y, triangle.C.P.y);
                float minZ = Mathf.Min(triangle.A.P.z, triangle.B.P.z, triangle.C.P.z);
                float maxZ = Mathf.Max(triangle.A.P.z, triangle.B.P.z, triangle.C.P.z);
                if (maxY <= Y0 || minY >= Y0 + 8 * YStep || maxZ <= Z0 || minZ >= Z0 + 10 * ZStep)
                { result.Triangles.Add(triangle); continue; }
                int y0 = Mathf.FloorToInt((minY - Y0) / YStep), y1 = Mathf.FloorToInt((maxY - Y0) / YStep);
                float pad = forward ? .0001f : Shift + .0001f;
                int z0 = Mathf.FloorToInt((minZ - Z0 - pad) / ZStep), z1 = Mathf.FloorToInt((maxZ - Z0 + pad) / ZStep);
                for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int half = 0; half < 2; half++)
                {
                    Tile tile = Get(y, z, half);
                    var polygon = Clip(new List<Vertex> { triangle.A, triangle.B, triangle.C }, forward ? tile.Source : tile.Mapped);
                    if (polygon.Count < 3) continue;
                    Vector3 center = Vector3.zero;
                    foreach (Vertex vertex in polygon) center += vertex.P;
                    center /= polygon.Count;
                    if (!At(center, forward).Key.Equals(tile.Key)) continue;
                    for (int i = 0; i < polygon.Count; i++)
                    { Vertex vertex = polygon[i]; vertex.P = Transform(vertex.P, tile, forward); polygon[i] = vertex; }
                    for (int i = 1; i + 1 < polygon.Count; i++)
                    {
                        Vertex a = polygon[0], b = polygon[i], c = polygon[i + 1];
                        if (Vector3.Cross(b.P - a.P, c.P - a.P).sqrMagnitude <= 1e-12f) continue;
                        result.Triangles.Add(new Triangle(a, b, c, triangle.Interior, triangle.MaterialSlot));
                    }
                }
            }
            return result;
        }
    }
}
