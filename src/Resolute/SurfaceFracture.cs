using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    /// <summary>
    /// Deterministic connected-surface fracture. A conforming refinement of the
    /// source triangles supplies a graph; unequal geodesic regions follow that
    /// graph and its creases. Separate islands are never made into one fragment.
    /// Exterior positions, UVs and normals stay on the source surface. Backing
    /// and torn edge walls provide thickness without a generated beam lattice.
    /// This file depends only on UnityEngine and the standard C# library.
    /// </summary>
    public static class SurfaceFracture
    {
        [Serializable]
        public sealed class Settings
        {
            public float TargetArea = 110f;
            public float Thickness = .12f;
            public int MaximumPieces = 128;
            public int Seed = 7139;
            public float MaximumDiameter = 24f;
            public float MaximumPlaneDeviation = 0f;
            public float MaximumConcavity = .65f;
            // A lower Z metric cost grows connected regions along the hull.
            // Coordinates, normals and physical collision limits remain metric.
            public float LongitudinalMetric = 1f;
            public float ConcaveSeamAngle = 28f;
            public float CreasePenalty = 4f;
            public float SizeVariation = .8f;
            public float MinimumFragmentArea = 2.5f;
        }

        [Serializable]
        public sealed class Piece
        {
            public Mesh Surface;
            public Mesh Solid;
            public Vector3 Center;
            public Vector3 Normal;
            public float Area;
            public float Diameter;
            public float Thickness;
        }

        public sealed class Result
        {
            public Piece[] Pieces;
            public float SourceArea;
            public float CoveredArea;
            public int SourceTriangles;
            public int OutputTriangles;
            public int InteriorMaterialIndex;
            public int ConnectedComponents;
            public Piece RetainedDetail;
            public Piece RetainedCollisionDetail;
            public int RetainedDetailComponents;
        }

        private const float Epsilon = .00001f;

        private struct Point
        {
            internal Vector3 P, N;
            internal Vector2 Uv, Uv1;
            internal Color Color;
            internal static Point Lerp(Point a, Point b, float t)
            {
                return new Point { P = Vector3.LerpUnclamped(a.P, b.P, t),
                    N = Vector3.LerpUnclamped(a.N, b.N, t).normalized,
                    Uv = Vector2.LerpUnclamped(a.Uv, b.Uv, t),
                    Uv1 = Vector2.LerpUnclamped(a.Uv1, b.Uv1, t),
                    Color = Color.LerpUnclamped(a.Color, b.Color, t) };
            }
        }

        private struct Triangle
        {
            internal Point A, B, C;
            internal int Material;
            internal float Area => Vector3.Cross(B.P - A.P, C.P - A.P).magnitude * .5f;
            internal Vector3 Center => (A.P + B.P + C.P) / 3f;
            internal Vector3 Normal => Vector3.Cross(B.P - A.P, C.P - A.P).normalized;
        }

        private struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
        {
            private readonly int x, y, z;
            internal PositionKey(Vector3 p, float precision = 10000f)
            { x = Mathf.RoundToInt(p.x * precision); y = Mathf.RoundToInt(p.y * precision); z = Mathf.RoundToInt(p.z * precision); }
            public bool Equals(PositionKey b) => x == b.x && y == b.y && z == b.z;
            public override bool Equals(object obj) => obj is PositionKey && Equals((PositionKey)obj);
            public override int GetHashCode() { unchecked { return ((x * 397) ^ y) * 397 ^ z; } }
            public int CompareTo(PositionKey b)
            { int c = x.CompareTo(b.x); if (c != 0) return c; c = y.CompareTo(b.y); return c != 0 ? c : z.CompareTo(b.z); }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly PositionKey a, b;
            internal EdgeKey(Vector3 p, Vector3 q, float precision = 10000f)
            {
                var x = new PositionKey(p, precision); var y = new PositionKey(q, precision);
                if (x.CompareTo(y) < 0) { a = x; b = y; } else { a = y; b = x; }
            }
            public bool Equals(EdgeKey other) => a.Equals(other.a) && b.Equals(other.b);
            public override bool Equals(object obj) => obj is EdgeKey && Equals((EdgeKey)obj);
            public override int GetHashCode() { unchecked { return a.GetHashCode() * 397 ^ b.GetHashCode(); } }
        }

        private sealed class Edge
        {
            internal Point A, B;
            internal int Count;
        }

        private sealed class MeshBuilder
        {
            private readonly List<Vector3> p = new List<Vector3>(), n = new List<Vector3>();
            private readonly List<Vector2> uv = new List<Vector2>(), uv1 = new List<Vector2>();
            private readonly List<Color> colors = new List<Color>();
            private readonly List<int>[] indices;
            private readonly Vector3 center;
            internal MeshBuilder(int materials, Vector3 origin)
            { indices = Enumerable.Range(0, materials).Select(_ => new List<int>()).ToArray(); center = origin; }
            internal void Add(Point a, Point b, Point c, int material)
            {
                if (Vector3.Cross(b.P - a.P, c.P - a.P).sqrMagnitude < 1e-14f) return;
                foreach (Point v in new[] { a, b, c })
                {
                    indices[material].Add(p.Count); p.Add(v.P - center); n.Add(v.N);
                    uv.Add(v.Uv); uv1.Add(v.Uv1); colors.Add(v.Color);
                }
            }
            internal Mesh Finish(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = p.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(p); mesh.SetNormals(n); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv1); mesh.SetColors(colors);
                mesh.subMeshCount = indices.Length;
                for (int i = 0; i < indices.Length; i++) mesh.SetTriangles(indices[i], i);
                mesh.RecalculateBounds(); mesh.RecalculateTangents();
                return mesh;
            }
        }

        private struct Frontier
        {
            internal int Triangle, Region;
            internal float Cost;
        }

        private sealed class MinHeap
        {
            private readonly List<Frontier> data = new List<Frontier>();
            internal int Count => data.Count;
            internal void Push(Frontier item)
            {
                int index = data.Count; data.Add(item);
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (data[parent].Cost <= item.Cost) break;
                    data[index] = data[parent]; index = parent;
                }
                data[index] = item;
            }
            internal Frontier Pop()
            {
                Frontier result = data[0], last = data[data.Count - 1]; data.RemoveAt(data.Count - 1);
                if (data.Count == 0) return result;
                int index = 0;
                while (index * 2 + 1 < data.Count)
                {
                    int child = index * 2 + 1;
                    if (child + 1 < data.Count && data[child + 1].Cost < data[child].Cost) child++;
                    if (last.Cost <= data[child].Cost) break;
                    data[index] = data[child]; index = child;
                }
                data[index] = last;
                return result;
            }
        }

        public static Result Generate(Mesh source, Settings settings = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            settings = settings ?? new Settings();
            if (!(settings.TargetArea > 0f) || !(settings.Thickness > .001f) || settings.MaximumPieces < 1 ||
                !(settings.LongitudinalMetric > 0f) || settings.LongitudinalMetric > 1f)
                throw new ArgumentException("Fracture area, thickness and piece count must be positive.");
            List<Triangle> original = Read(source);
            List<Triangle> triangles = Refine(original, Mathf.Clamp(Mathf.Sqrt(settings.TargetArea) * .34f, 1.2f, 5f), settings.Seed);
            if (triangles.Count == 0) throw new ArgumentException("The mesh has no non-degenerate surface triangles.");
            float area = triangles.Sum(t => t.Area);
            List<int>[] graph = Adjacency(triangles, settings);
            List<List<int>> components = Connected(Enumerable.Range(0, triangles.Count).ToList(), graph);
            var random = new System.Random(settings.Seed);
            var seeds = new List<int>();
            var retained = new List<int>();
            var collisionRetained = new List<int>();
            int retainedComponents = 0;
            float detailThreshold = Mathf.Min(settings.MinimumFragmentArea, area * .02f);
            foreach (List<int> component in components)
            {
                float componentArea = component.Sum(i => triangles[i].Area);
                if (componentArea < detailThreshold)
                {
                    retained.AddRange(component); retainedComponents++;
                    // Increasing visual tear size must not discard a structural
                    // island's collision. Only the original sub-2.5m² detail
                    // population remains purely decorative.
                    if (componentArea >= Mathf.Min(2.5f, area * .02f)) collisionRetained.AddRange(component);
                    continue;
                }
                int count = Mathf.Clamp(Mathf.RoundToInt(componentArea / settings.TargetArea), 1, component.Count);
                int first = component.OrderByDescending(i => triangles[i].Area).First();
                var chosen = new List<int> { first };
                var nearest = component.ToDictionary(i => i, i => float.PositiveInfinity);
                while (chosen.Count < count)
                {
                    Vector3 last = triangles[chosen[chosen.Count - 1]].Center;
                    int next = -1; float farthest = -1f;
                    foreach (int i in component)
                    {
                        nearest[i] = Mathf.Min(nearest[i], MetricSquared(triangles[i].Center - last, settings));
                        if (nearest[i] > farthest) { farthest = nearest[i]; next = i; }
                    }
                    if (next < 0 || farthest < .00001f) break;
                    chosen.Add(next);
                }
                seeds.AddRange(chosen);
            }
            int[] owners = Enumerable.Repeat(-1, triangles.Count).ToArray();
            float[] distance = Enumerable.Repeat(float.PositiveInfinity, triangles.Count).ToArray();
            float[] rates = seeds.Select(_ => Mathf.Exp(((float)random.NextDouble() - .5f) * settings.SizeVariation)).ToArray();
            var queue = new MinHeap();
            for (int i = 0; i < seeds.Count; i++)
            { distance[seeds[i]] = 0; owners[seeds[i]] = i; queue.Push(new Frontier { Triangle = seeds[i], Region = i, Cost = 0 }); }
            while (queue.Count > 0)
            {
                Frontier visit = queue.Pop();
                if (visit.Cost > distance[visit.Triangle] + Epsilon || visit.Region != owners[visit.Triangle]) continue;
                Triangle a = triangles[visit.Triangle];
                foreach (int next in graph[visit.Triangle])
                {
                    Triangle b = triangles[next];
                    float bend = 1f - Mathf.Clamp(Vector3.Dot(a.Normal, b.Normal), -1f, 1f);
                    float step = Mathf.Sqrt(MetricSquared(b.Center - a.Center, settings)) * (1f + settings.CreasePenalty * bend);
                    // A shared spatial toughness field makes the real region
                    // boundary wander through the conforming surface graph.
                    // It changes polygon ownership, never source positions.
                    step *= Mathf.Lerp(.62f, 1.55f, EdgeFraction(a.Center, b.Center, settings.Seed ^ 0x5b37));
                    if (a.Material != b.Material) step *= 2f;
                    float cost = visit.Cost + step / rates[visit.Region];
                    if (cost >= distance[next]) continue;
                    distance[next] = cost; owners[next] = visit.Region;
                    queue.Push(new Frontier { Triangle = next, Region = visit.Region, Cost = cost });
                }
            }
            var pieces = new List<List<int>>();
            foreach (IGrouping<int, int> region in Enumerable.Range(0, owners.Length).Where(i => owners[i] >= 0).GroupBy(i => owners[i]))
                foreach (List<int> connected in Connected(region.ToList(), graph))
                    SplitBounded(connected, graph, triangles, settings, pieces);
            // Tiny slivers produced at narrow creases remain attached detail.
            // They keep their exact exterior but do not create tiny colliders.
            foreach (List<int> detail in pieces.Where(p => p.Sum(i => triangles[i].Area) < detailThreshold).ToArray())
            { retained.AddRange(detail); collisionRetained.AddRange(detail); retainedComponents++; pieces.Remove(detail); }
            if (pieces.Count > settings.MaximumPieces)
                throw new InvalidOperationException("Mesh '" + source.name + "' needs " + pieces.Count +
                    " connected shell regions (maximum " + settings.MaximumPieces + ", " + components.Count + " original islands, " +
                    retainedComponents + " retained details; region areas " + pieces.Min(p => p.Sum(i => triangles[i].Area)).ToString("F2") +
                    " to " + pieces.Max(p => p.Sum(i => triangles[i].Area)).ToString("F2") + " m2). Review size and concavity limits.");
            var output = new List<Piece>();
            int outputTriangles = 0;
            foreach (List<int> indices in pieces.OrderByDescending(p => p.Sum(i => triangles[i].Area)))
            {
                List<Triangle> patch = indices.Select(i => triangles[i]).ToList();
                float thickness = settings.Thickness * Mathf.Lerp(.8f, 1.3f, (float)random.NextDouble());
                Piece piece = BuildPiece(patch, source.subMeshCount, thickness, source.name + " / connected shell region " + output.Count);
                piece.Thickness = thickness; output.Add(piece);
                outputTriangles += patch.Count;
            }
            Piece permanent = retained.Count > 0 ? BuildPiece(retained.Select(i => triangles[i]).ToList(),
                source.subMeshCount, settings.Thickness, source.name + " / attached detail") : null;
            float covered = output.Sum(p => p.Area) + (permanent != null ? permanent.Area : 0f);
            if (Mathf.Abs(area - covered) > Mathf.Max(.02f, area * .0001f))
                throw new InvalidOperationException("Fracture partition changed the source surface area: " + area + " -> " + covered);
            return new Result { Pieces = output.ToArray(), SourceArea = area, CoveredArea = covered,
                SourceTriangles = original.Count, OutputTriangles = outputTriangles + retained.Count, InteriorMaterialIndex = source.subMeshCount,
                ConnectedComponents = components.Count, RetainedDetail = permanent, RetainedDetailComponents = retainedComponents,
                RetainedCollisionDetail = collisionRetained.Count > 0 ? BuildPiece(collisionRetained.Select(i => triangles[i]).ToList(),
                    source.subMeshCount, settings.Thickness, source.name + " / retained structural collision detail") : null };
        }

        public sealed class CollisionRegion
        {
            public int PieceIndex;
            public Vector3[] Points;
        }

        public sealed class CollisionGroup
        {
            public int[] Regions;
            public Vector3[] Points;
        }

        public sealed class CollisionLayout
        {
            public CollisionRegion[] Regions;
            public CollisionGroup[] Groups;
        }

        private struct Plane
        {
            internal Vector3 N;
            internal float D;
        }

        private sealed class CollisionSet
        {
            internal readonly List<int> Regions = new List<int>();
            internal readonly List<Vector3> Exterior = new List<Vector3>();
            internal readonly List<Plane> Planes = new List<Plane>();
            internal Bounds Bounds;
        }

        /// <summary>
        /// Merge touching regions only when their combined vertices remain
        /// behind every source exterior plane. Intact collision can then use
        /// a few coherent convex groups while preserving fine tear metadata.
        /// Regions also include non-releasing structural slivers; decorative
        /// disconnected islands below MinimumFragmentArea remain visual detail.
        /// This returns points for the host's convex cooking policy, not an
        /// arbitrary boxed replacement for the surface.
        /// </summary>
        public static CollisionLayout GroupCollision(Result fracture, float maximumDiameter = 44f, float maximumDeviation = .65f)
        {
            var patches = new List<List<Triangle>>();
            var output = new List<CollisionRegion>();
            for (int i = 0; i < fracture.Pieces.Length; i++)
            {
                Piece piece = fracture.Pieces[i];
                List<Triangle> triangles = Translate(Read(piece.Surface), piece.Center);
                // Visual tear units can wrap a hull or deck corner. Decompose
                // their collision independently, retaining one physical owner
                // for every precise convex region of the connected surface.
                var settings = new Settings { TargetArea = 450f, MaximumConcavity = maximumDeviation, MaximumDiameter = 23f };
                List<int>[] adjacency = Adjacency(triangles, settings);
                var split = new List<List<int>>();
                foreach (List<int> connected in Connected(Enumerable.Range(0, triangles.Count).ToList(), adjacency))
                    SplitBounded(connected, adjacency, triangles, settings, split);
                foreach (List<int> indices in split)
                {
                    List<Triangle> patch = indices.Select(index => triangles[index]).ToList(); patches.Add(patch);
                    output.Add(new CollisionRegion { PieceIndex = i, Points = patch.SelectMany(t => new[] {
                        t.A.P, t.B.P, t.C.P, t.A.P - t.Normal * piece.Thickness, t.B.P - t.Normal * piece.Thickness,
                        t.C.P - t.Normal * piece.Thickness }).ToArray() });
                }
            }
            if (fracture.RetainedCollisionDetail != null)
            {
                Piece retained = fracture.RetainedCollisionDetail;
                List<Triangle> triangles = Translate(Read(retained.Surface), retained.Center);
                var settings = new Settings { MaximumConcavity = maximumDeviation, MaximumDiameter = 24f };
                List<int>[] adjacency = Adjacency(triangles, settings);
                var split = new List<List<int>>();
                foreach (List<int> connected in Connected(Enumerable.Range(0, triangles.Count).ToList(), adjacency))
                    SplitBounded(connected, adjacency, triangles, settings, split);
                foreach (List<int> indices in split)
                {
                    List<Triangle> patch = indices.Select(i => triangles[i]).ToList(); patches.Add(patch);
                    output.Add(new CollisionRegion { PieceIndex = -1, Points = patch.SelectMany(t => new[] {
                        t.A.P, t.B.P, t.C.P, t.A.P - t.Normal * .12f, t.B.P - t.Normal * .12f, t.C.P - t.Normal * .12f }).ToArray() });
                }
            }
            var groups = new Dictionary<int, CollisionSet>();
            var edgeOwners = new Dictionary<EdgeKey, HashSet<int>>();
            var neighbors = new HashSet<long>();
            for (int i = 0; i < patches.Count; i++)
            {
                var group = new CollisionSet(); group.Regions.Add(i);
                var uniquePoints = new Dictionary<PositionKey, Vector3>();
                var uniquePlanes = new HashSet<string>();
                foreach (Triangle triangle in patches[i])
                {
                    foreach (Vector3 p in new[] { triangle.A.P, triangle.B.P, triangle.C.P }) uniquePoints[new PositionKey(p)] = p;
                    Vector3 normal = triangle.Normal; float offset = Vector3.Dot(normal, triangle.A.P);
                    string key = Mathf.RoundToInt(normal.x * 1000f) + ":" + Mathf.RoundToInt(normal.y * 1000f) + ":" +
                        Mathf.RoundToInt(normal.z * 1000f) + ":" + Mathf.RoundToInt(offset * 1000f);
                    if (uniquePlanes.Add(key)) group.Planes.Add(new Plane { N = normal, D = offset });
                    foreach (EdgeKey edge in new[] { new EdgeKey(triangle.A.P, triangle.B.P, 1000f), new EdgeKey(triangle.B.P, triangle.C.P, 1000f), new EdgeKey(triangle.C.P, triangle.A.P, 1000f) })
                    {
                        HashSet<int> owners;
                        if (!edgeOwners.TryGetValue(edge, out owners)) edgeOwners.Add(edge, owners = new HashSet<int>());
                        foreach (int owner in owners) if (owner != i) neighbors.Add(((long)Mathf.Min(i, owner) << 32) | (uint)Mathf.Max(i, owner));
                        owners.Add(i);
                    }
                }
                group.Exterior.AddRange(uniquePoints.Values);
                group.Bounds = new Bounds(group.Exterior[0], Vector3.zero);
                foreach (Vector3 p in group.Exterior) group.Bounds.Encapsulate(p);
                groups.Add(i, group);
            }
            int[] parent = Enumerable.Range(0, patches.Count).ToArray();
            Func<int, int> root = i => { while (parent[i] != i) i = parent[i]; return i; };
            bool changed = true;
            while (changed)
            {
                changed = false;
                long[] candidates = neighbors.Select(edge => {
                    int a = root((int)(edge >> 32)), b = root((int)(edge & uint.MaxValue));
                    return a == b ? -1 : ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                }).Where(edge => edge >= 0).Distinct().OrderBy(edge =>
                    groups[(int)(edge >> 32)].Exterior.Count + groups[(int)(edge & uint.MaxValue)].Exterior.Count).ToArray();
                foreach (long edge in candidates)
                {
                    int a = root((int)(edge >> 32)), b = root((int)(edge & uint.MaxValue));
                    if (a == b) continue;
                    CollisionSet left = groups[a], right = groups[b];
                    Bounds bounds = left.Bounds; bounds.Encapsulate(right.Bounds.min); bounds.Encapsulate(right.Bounds.max);
                    if (bounds.size.magnitude > maximumDiameter || !Behind(left.Planes, right.Exterior, maximumDeviation) ||
                        !Behind(right.Planes, left.Exterior, maximumDeviation)) continue;
                    parent[b] = a; left.Regions.AddRange(right.Regions); left.Exterior.AddRange(right.Exterior);
                    left.Planes.AddRange(right.Planes); left.Bounds = bounds; groups.Remove(b); changed = true;
                }
            }
            var precise = new List<CollisionRegion>(); var intact = new List<CollisionGroup>();
            foreach (CollisionSet group in groups.Values.OrderBy(g => g.Regions.Min()))
            {
                var regionIndices = new List<int>();
                // A subset of an already accepted convex group stays within
                // that group's hull. Coalesce its regions by physical tear
                // owner so a first hit does not expand hundreds of tiny shapes.
                foreach (var owner in group.Regions.GroupBy(i => output[i].PieceIndex))
                {
                    regionIndices.Add(precise.Count);
                    precise.Add(new CollisionRegion { PieceIndex = owner.Key,
                        Points = owner.SelectMany(i => output[i].Points).ToArray() });
                }
                intact.Add(new CollisionGroup { Regions = regionIndices.ToArray(), Points = group.Regions.SelectMany(i => output[i].Points).ToArray() });
            }
            return new CollisionLayout { Regions = precise.ToArray(), Groups = intact.ToArray() };
        }

        private static bool Behind(List<Plane> planes, List<Vector3> points, float tolerance)
        {
            foreach (Plane plane in planes)
                foreach (Vector3 point in points) if (Vector3.Dot(plane.N, point) - plane.D > tolerance) return false;
            return true;
        }

        private static List<Triangle> Translate(List<Triangle> triangles, Vector3 offset)
        {
            var output = new List<Triangle>();
            foreach (Triangle t in triangles)
            {
                Triangle copy = t; copy.A.P += offset; copy.B.P += offset; copy.C.P += offset; output.Add(copy);
            }
            return output;
        }

        private static float EdgeFraction(Vector3 a, Vector3 b, int seed)
        {
            var ka = new PositionKey(a); var kb = new PositionKey(b);
            int first = ka.CompareTo(kb) <= 0 ? ka.GetHashCode() : kb.GetHashCode();
            int second = ka.CompareTo(kb) <= 0 ? kb.GetHashCode() : ka.GetHashCode();
            unchecked
            {
                uint value = (uint)first * 0x9e3779b9u ^ (uint)second * 0x85ebca6bu ^ (uint)seed;
                value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15;
                value *= 0x846ca68bu; value ^= value >> 16;
                return (value & 0x00ffffffu) / 16777215f;
            }
        }

        private static Point SplitEdge(Point a, Point b, int seed)
        {
            float fraction = Mathf.Lerp(.32f, .68f, EdgeFraction(a.P, b.P, seed));
            bool reverse = new PositionKey(a.P).CompareTo(new PositionKey(b.P)) > 0;
            Point result = Point.Lerp(a, b, reverse ? 1f - fraction : fraction);
            // Compute position in one canonical direction on both incident
            // faces; complementary interpolation can round to different keys.
            result.P = reverse ? Vector3.LerpUnclamped(b.P, a.P, fraction) : Vector3.LerpUnclamped(a.P, b.P, fraction);
            return result;
        }

        private static List<Triangle> Refine(List<Triangle> input, float maximumEdge, int seed)
        {
            float limit = maximumEdge * maximumEdge;
            for (int iteration = 0; iteration < 12; iteration++)
            {
                bool changed = false; var output = new List<Triangle>();
                foreach (Triangle t in input)
                {
                    bool ab = (t.A.P - t.B.P).sqrMagnitude > limit, bc = (t.B.P - t.C.P).sqrMagnitude > limit,
                        ca = (t.C.P - t.A.P).sqrMagnitude > limit;
                    int mask = (ab ? 1 : 0) | (bc ? 2 : 0) | (ca ? 4 : 0);
                    if (mask == 0) { output.Add(t); continue; }
                    changed = true;
                    // Both sides of a shared edge use the same unequal split
                    // point, preserving connectivity without a regular grid.
                    Point a = t.A, b = t.B, c = t.C, d = SplitEdge(a, b, seed), e = SplitEdge(b, c, seed), f = SplitEdge(c, a, seed);
                    Action<Point, Point, Point> add = (x, y, z) => output.Add(new Triangle { A = x, B = y, C = z, Material = t.Material });
                    switch (mask)
                    {
                        case 1: add(a, d, c); add(d, b, c); break;
                        case 2: add(a, b, e); add(a, e, c); break;
                        case 4: add(a, b, f); add(b, c, f); break;
                        case 3: add(a, d, c); add(d, e, c); add(d, b, e); break;
                        case 5: add(a, d, f); add(d, b, c); add(d, c, f); break;
                        case 6: add(a, b, f); add(b, e, f); add(e, c, f); break;
                        default: add(a, d, f); add(d, b, e); add(f, e, c); add(d, e, f); break;
                    }
                }
                input = output;
                if (!changed) break;
                if (input.Count > 250000) throw new InvalidOperationException("Surface refinement exceeds the bounded mesh budget.");
            }
            return input;
        }

        private static List<int>[] Adjacency(List<Triangle> triangles, Settings settings)
        {
            var graph = Enumerable.Range(0, triangles.Count).Select(_ => new List<int>(3)).ToArray();
            var edges = new Dictionary<EdgeKey, List<int>>();
            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle t = triangles[i];
                foreach (EdgeKey key in new[] { new EdgeKey(t.A.P, t.B.P), new EdgeKey(t.B.P, t.C.P), new EdgeKey(t.C.P, t.A.P) })
                {
                    List<int> prior;
                    if (!edges.TryGetValue(key, out prior)) edges.Add(key, prior = new List<int>(2));
                    foreach (int j in prior)
                    {
                        Triangle other = triangles[j];
                        float alignment = Vector3.Dot(t.Normal, other.Normal);
                        float inward = Mathf.Max(Vector3.Dot(other.Center - t.Center, t.Normal),
                            Vector3.Dot(t.Center - other.Center, other.Normal));
                        if (settings.ConcaveSeamAngle > 0 && alignment < Mathf.Cos(settings.ConcaveSeamAngle * Mathf.Deg2Rad) && inward > .005f) continue;
                        graph[i].Add(j); graph[j].Add(i);
                    }
                    prior.Add(i);
                }
            }
            return graph;
        }

        private static List<List<int>> Connected(List<int> input, List<int>[] graph)
        {
            var available = new HashSet<int>(input); var output = new List<List<int>>();
            foreach (int start in input)
            {
                if (!available.Remove(start)) continue;
                var component = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(); component.Add(i);
                    foreach (int j in graph[i]) if (available.Remove(j)) queue.Enqueue(j);
                }
                output.Add(component);
            }
            return output;
        }

        private static void SplitBounded(List<int> patch, List<int>[] graph, List<Triangle> triangles, Settings settings, List<List<int>> output)
        {
            Bounds bounds = new Bounds(triangles[patch[0]].Center, Vector3.zero);
            Vector3 normal = Vector3.zero, center = Vector3.zero; float area = 0;
            foreach (int i in patch)
            {
                Triangle t = triangles[i]; bounds.Encapsulate(t.A.P); bounds.Encapsulate(t.B.P); bounds.Encapsulate(t.C.P);
                area += t.Area; normal += t.Normal * t.Area; center += t.Center * t.Area;
            }
            center /= Mathf.Max(area, Epsilon); normal.Normalize();
            float deviation = 0;
            foreach (int i in patch)
            {
                Triangle t = triangles[i];
                deviation = Mathf.Max(deviation, Mathf.Abs(Vector3.Dot(t.A.P - center, normal)),
                    Mathf.Abs(Vector3.Dot(t.B.P - center, normal)), Mathf.Abs(Vector3.Dot(t.C.P - center, normal)));
            }
            // Convex folds can be large without projecting collision outside
            // the original surface. A strict flatness test shredded every
            // corner into slivers. Only inward folds require a tighter split.
            float concavity = 0f;
            Vector3 concavePlane = Vector3.zero; float concaveOffset = 0f;
            if (settings.MaximumConcavity > 0f)
            foreach (int face in patch)
            {
                Triangle plane = triangles[face]; Vector3 faceNormal = plane.Normal;
                foreach (int i in patch)
                {
                    Triangle t = triangles[i];
                    concavity = Mathf.Max(concavity, Vector3.Dot(t.A.P - plane.A.P, faceNormal),
                        Vector3.Dot(t.B.P - plane.A.P, faceNormal), Vector3.Dot(t.C.P - plane.A.P, faceNormal));
                    if (concavity > settings.MaximumConcavity) break;
                }
                if (concavity > settings.MaximumConcavity)
                {
                    concavePlane = faceNormal;
                    concaveOffset = Vector3.Dot(plane.A.P, faceNormal) + settings.MaximumConcavity * .5f;
                    break;
                }
            }
            if (patch.Count < 4 || (bounds.size.magnitude <= settings.MaximumDiameter &&
                (settings.MaximumPlaneDeviation <= 0 || deviation <= settings.MaximumPlaneDeviation) &&
                (settings.MaximumConcavity <= 0 || concavity <= settings.MaximumConcavity) && area < settings.TargetArea * 3.5f))
            { output.Add(patch); return; }
            List<int> left = null, right = null;
            if (concavePlane.sqrMagnitude > .5f)
            {
                left = patch.Where(i => Vector3.Dot(triangles[i].Center, concavePlane) <= concaveOffset).ToList();
                right = patch.Where(i => Vector3.Dot(triangles[i].Center, concavePlane) > concaveOffset).ToList();
            }
            if (left == null || left.Count == 0 || right.Count == 0)
            {
                Vector3 metricSize = bounds.size; metricSize.z *= settings.LongitudinalMetric;
                int axis = metricSize.x >= metricSize.y ? 0 : 1;
                if (metricSize.z > metricSize[axis]) axis = 2;
                int[] sorted = patch.OrderBy(i => triangles[i].Center[axis]).ToArray();
                left = sorted.Take(sorted.Length / 2).ToList(); right = sorted.Skip(sorted.Length / 2).ToList();
            }
            foreach (List<int> half in new[] { left, right })
                foreach (List<int> piece in Connected(half, graph)) SplitBounded(piece, graph, triangles, settings, output);
        }

        private static float MetricSquared(Vector3 delta, Settings settings) =>
            delta.x * delta.x + delta.y * delta.y + delta.z * delta.z * settings.LongitudinalMetric * settings.LongitudinalMetric;

        private static List<Triangle> Read(Mesh mesh)
        {
            Vector3[] p = mesh.vertices, n = mesh.normals;
            Vector2[] uv = mesh.uv, uv1 = mesh.uv2;
            Color[] color = mesh.colors;
            var vertices = new Point[p.Length];
            for (int i = 0; i < p.Length; i++)
                vertices[i] = new Point { P = p[i], N = n.Length == p.Length ? n[i] : Vector3.zero,
                    Uv = uv.Length == p.Length ? uv[i] : Vector2.zero,
                    Uv1 = uv1.Length == p.Length ? uv1[i] : Vector2.zero,
                    Color = color.Length == p.Length ? color[i] : Color.white };
            var triangles = new List<Triangle>();
            for (int material = 0; material < mesh.subMeshCount; material++)
            {
                int[] indices = mesh.GetTriangles(material);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var triangle = new Triangle { A = vertices[indices[i]], B = vertices[indices[i + 1]],
                        C = vertices[indices[i + 2]], Material = material };
                    if (triangle.Area < 1e-8f) continue;
                    if (triangle.A.N.sqrMagnitude < .5f) triangle.A.N = triangle.Normal;
                    if (triangle.B.N.sqrMagnitude < .5f) triangle.B.N = triangle.Normal;
                    if (triangle.C.N.sqrMagnitude < .5f) triangle.C.N = triangle.Normal;
                    triangles.Add(triangle);
                }
            }
            return triangles;
        }

        private static Vector3[] MakeSeeds(List<Triangle> triangles, float area, int count, int seed)
        {
            var random = new System.Random(seed);
            int candidateCount = Mathf.Max(count * 7, 32);
            var candidates = new Vector3[candidateCount];
            float[] cumulative = new float[triangles.Count];
            float sum = 0f;
            for (int i = 0; i < triangles.Count; i++) { sum += triangles[i].Area; cumulative[i] = sum; }
            for (int i = 0; i < candidateCount; i++)
            {
                float value = (float)random.NextDouble() * area;
                int index = Array.BinarySearch(cumulative, value);
                if (index < 0) index = ~index;
                Triangle triangle = triangles[Mathf.Min(index, triangles.Count - 1)];
                float root = Mathf.Sqrt((float)random.NextDouble()), along = (float)random.NextDouble();
                candidates[i] = triangle.A.P * (1f - root) + triangle.B.P * (root * (1f - along)) + triangle.C.P * (root * along);
            }
            var chosen = new List<Vector3> { candidates[0] };
            var distances = Enumerable.Repeat(float.PositiveInfinity, candidateCount).ToArray();
            while (chosen.Count < count)
            {
                Vector3 newest = chosen[chosen.Count - 1];
                int farthest = 0;
                for (int i = 0; i < candidates.Length; i++)
                {
                    distances[i] = Mathf.Min(distances[i], (candidates[i] - newest).sqrMagnitude);
                    if (distances[i] > distances[farthest]) farthest = i;
                }
                if (distances[farthest] < .0001f) break;
                chosen.Add(candidates[farthest]);
            }
            return chosen.ToArray();
        }

        private static List<Point> Clip(List<Point> polygon, Vector3 normal, float offset)
        {
            var output = new List<Point>(polygon.Count + 1);
            for (int i = 0; i < polygon.Count; i++)
            {
                Point a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                float da = Vector3.Dot(a.P, normal) - offset, db = Vector3.Dot(b.P, normal) - offset;
                bool insideA = da <= Epsilon, insideB = db <= Epsilon;
                if (insideA) output.Add(a);
                if (insideA != insideB)
                    output.Add(Point.Lerp(a, b, Mathf.Clamp01(da / (da - db))));
            }
            return output;
        }

        private static Piece BuildPiece(List<Triangle> triangles, int materials, float thickness, string name)
        {
            float area = triangles.Sum(t => t.Area);
            Vector3 center = Vector3.zero, normal = Vector3.zero;
            var offsetNormals = new Dictionary<PositionKey, Vector3>();
            var edges = new Dictionary<EdgeKey, Edge>();
            foreach (Triangle triangle in triangles)
            {
                float weight = triangle.Area;
                center += triangle.Center * weight; normal += triangle.Normal * weight;
                foreach (Point point in new[] { triangle.A, triangle.B, triangle.C })
                {
                    var key = new PositionKey(point.P); Vector3 prior;
                    offsetNormals.TryGetValue(key, out prior);
                    offsetNormals[key] = prior + triangle.Normal * weight;
                }
                foreach (Point[] pair in new[] { new[] { triangle.A, triangle.B }, new[] { triangle.B, triangle.C }, new[] { triangle.C, triangle.A } })
                {
                    var key = new EdgeKey(pair[0].P, pair[1].P); Edge edge;
                    if (!edges.TryGetValue(key, out edge)) edges[key] = edge = new Edge { A = pair[0], B = pair[1] };
                    edge.Count++;
                }
            }
            center /= Mathf.Max(area, Epsilon);
            var surface = new MeshBuilder(materials, center);
            var solid = new MeshBuilder(materials + 1, center);
            Func<Point, Point> backing = point =>
            {
                Vector3 inward = offsetNormals[new PositionKey(point.P)].normalized;
                if (inward.sqrMagnitude < .5f) inward = point.N;
                // Small thickness variation leaves a rough fracture edge while
                // the entire visible exterior still matches the source exactly.
                float ripple = Mathf.Sin(point.P.x * 9.17f + point.P.y * 7.39f + point.P.z * 5.83f);
                Vector3 p = point.P - inward * thickness * (1f + .18f * ripple);
                return InteriorPoint(p, -inward);
            };
            foreach (Triangle triangle in triangles)
            {
                surface.Add(triangle.A, triangle.B, triangle.C, triangle.Material);
                solid.Add(triangle.A, triangle.B, triangle.C, triangle.Material);
                solid.Add(backing(triangle.C), backing(triangle.B), backing(triangle.A), materials);
            }
            foreach (Edge edge in edges.Values)
            {
                if (edge.Count != 1) continue;
                Point a = edge.A, b = edge.B, innerA = backing(a), innerB = backing(b);
                Vector3 n = Vector3.Cross(b.P - a.P, innerA.P - a.P).normalized;
                a = InteriorPoint(a.P, n); b = InteriorPoint(b.P, n);
                innerA = InteriorPoint(innerA.P, n); innerB = InteriorPoint(innerB.P, n);
                solid.Add(a, b, innerB, materials); solid.Add(a, innerB, innerA, materials);
            }
            Mesh surfaceMesh = surface.Finish(name + " surface"), solidMesh = solid.Finish(name + " thick shell");
            return new Piece { Surface = surfaceMesh, Solid = solidMesh, Center = center, Normal = normal.normalized,
                Area = area, Diameter = solidMesh.bounds.size.magnitude };
        }

        private static Point InteriorPoint(Vector3 position, Vector3 normal)
        {
            int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
            if (Mathf.Abs(normal.z) > Mathf.Abs(normal[axis])) axis = 2;
            var uv = new Vector2(position[(axis + 1) % 3], position[(axis + 2) % 3]) * .18f;
            return new Point { P = position, N = normal, Uv = uv, Uv1 = uv, Color = Color.white };
        }
    }
}
