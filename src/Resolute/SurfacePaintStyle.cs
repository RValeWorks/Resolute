using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    // Rendering only: fracture topology, source meshes and collision meshes
    // retain their authored coordinates. Native repeating plate textures use
    // metres in the original ship frame, including after a piece detaches.
    internal static class SurfacePaintStyle
    {
        internal const float NativeUvPerMetre = .0345425f;
        internal const float NativeDeckUvPerMetre = .0455092f;
        internal const float NativeWeaponUvPerMetre = .041097f;
        internal const int MaterialCount = 4;
        private static readonly string[] GreyAtlas = { "10001111", "00011111", "00001111", "00011111",
            "11111111", "11111111", "11111111", "10000000" };
        private static readonly Dictionary<string, Mesh> Cache = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, Dictionary<string, object>> Groups = new Dictionary<string, Dictionary<string, object>>();
        private static readonly List<object> MeshAudits = new List<object>();
        private static readonly Dictionary<Mesh, FaceAtlas> FaceAtlases = new Dictionary<Mesh, FaceAtlas>();
        private static readonly Dictionary<string, Mesh> DamageBackfaces = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, Mesh> LocalCollisions = new Dictionary<string, Mesh>();

        internal static Mesh PieceLocalCollision(Mesh source, Vector3 center)
        {
            string key = source.GetInstanceID() + "/" + center.ToString("R");
            Mesh result;
            if (!LocalCollisions.TryGetValue(key, out result))
            {
                result = UnityEngine.Object.Instantiate(source);
                result.name = "Resolute shared piece-local collision";
                result.vertices = source.vertices.Select(vertex => vertex - center).ToArray();
                result.RecalculateBounds();
                UnityEngine.Object.DontDestroyOnLoad(result); LocalCollisions.Add(key, result);
            }
            return result;
        }

        internal static bool NeedsDamageBackfaces(Renderer renderer) => renderer.name.StartsWith("rsl_hull_", StringComparison.Ordinal) ||
            renderer.name.StartsWith("rsl_top_mast", StringComparison.Ordinal) ||
            renderer.name.StartsWith("rsl_radar_antennas", StringComparison.Ordinal) ||
            renderer.name.StartsWith("rsl_detail22_arrays", StringComparison.Ordinal) ||
            renderer.name.StartsWith("rsl_nav_radar", StringComparison.Ordinal);

        internal static MeshRenderer AddDamageBackfaces(Renderer original, UnitPart owner)
        {
            // ShipTilingShader has fixed back-face culling. A material _Cull
            // override cannot make the authored open shell visible from inside.
            // Keep a reverse render copy dormant until this actual part takes
            // damage. It shares the native mask/UVs, with no source/collider edit.
            Mesh source = original.GetComponent<MeshFilter>().sharedMesh;
            Material[] materials = original.sharedMaterials;
            int mask = 0;
            for (int i = 0; i < source.subMeshCount && i < materials.Length; i++)
                if (NavalMaterials.UsesNativeDamage(materials[i])) mask |= 1 << i;
            if (mask == 0) throw new InvalidOperationException("Missing native mast/shell damage material: " + original.name);
            Mesh backing = CreateDamageBackfaces(source, mask);
            var obj = new GameObject(original.name + " damage reverse faces") { layer = original.gameObject.layer };
            obj.transform.SetParent(original.transform, false);
            obj.AddComponent<MeshFilter>().sharedMesh = backing;
            MeshRenderer renderer = obj.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = original.receiveShadows;
            renderer.enabled = false;
            var feedback = obj.AddComponent<ResoluteDamageBackfaces>();
            feedback.Part = owner; feedback.Source = original; feedback.Faces = renderer;
            ResoluteStructuralSection section = owner.GetComponent<ResoluteStructuralSection>();
            feedback.Section = section != null && section.Body == original ? section : null;
            return renderer;
        }

        internal static Mesh CreateDamageBackfaces(Mesh source, int mask)
        {
            string key = source.GetInstanceID() + "/" + mask;
            Mesh backing;
            if (!DamageBackfaces.TryGetValue(key, out backing))
            {
                backing = new Mesh { name = source.name + " / damage backfaces", indexFormat = source.indexFormat,
                    vertices = source.vertices, normals = source.normals.Select(n => -n).ToArray(), uv = source.uv,
                    uv2 = source.uv2, colors32 = source.colors32, subMeshCount = source.subMeshCount };
                Vector4[] tangents = source.tangents;
                if (tangents.Length == source.vertexCount)
                    backing.tangents = tangents.Select(t => new Vector4(t.x, t.y, t.z, -t.w)).ToArray();
                for (int slot = 0; slot < source.subMeshCount; slot++)
                {
                    int[] indices = (mask & (1 << slot)) == 0 ? new int[0] : source.GetTriangles(slot);
                    for (int i = 0; i < indices.Length; i += 3) { int first = indices[i]; indices[i] = indices[i + 1]; indices[i + 1] = first; }
                    backing.SetTriangles(indices, slot);
                }
                backing.bounds = source.bounds;
                UnityEngine.Object.DontDestroyOnLoad(backing); DamageBackfaces.Add(key, backing);
            }
            return backing;
        }
        internal static object LastAudit => new Dictionary<string, object> {
            ["operation"] = "Render copies assign native deck to walkable authored deck faces and native hull plating to mast/funnel caps. The separately authored flight-deck base keeps native deck. Ship-frame metric projection changes their UV0 only. Aviation markings and remaining accent UVs/materials, UV1, positions, normals and colors are preserved; no source or collision mesh is modified.",
            ["nativeUvPerMetre"] = NativeUvPerMetre, ["nativeAtlasPixels"] = 1024,
            ["nativeDeckUvPerMetre"] = NativeDeckUvPerMetre, ["nativeWeaponUvPerMetre"] = NativeWeaponUvPerMetre,
            ["materialSlots"] = new[] { "nativePlating", "sourceAccent", "innerSteel", "nativeDeck" },
            ["maximumProjectionStretch"] = Math.Sqrt(3), ["greyAtlasRowsBottomToTop"] = GreyAtlas,
            ["groups"] = Groups, ["meshes"] = MeshAudits };

        internal static bool HasValidAudit => Groups.ContainsKey("intactHull") &&
            Convert.ToDouble(Groups["intactHull"]["nativeGreyAreaM2"]) > 5000 &&
            Convert.ToDouble(Groups["intactHull"]["nativeDeckAreaM2"]) > 2500 &&
            Convert.ToDouble(Groups["intactHull"]["preservedAccentAreaM2"]) > 5000 &&
            Groups.ContainsKey("weapon") && Convert.ToDouble(Groups["weapon"]["nativeGreyAreaM2"]) > 250 &&
            MeshAudits.Cast<Dictionary<string, object>>().All(a => Convert.ToBoolean(a["sourceGeometryUnchanged"]) &&
                Convert.ToBoolean(a["renderCornersPreserved"]) && Convert.ToBoolean(a["accentUvPreserved"]) &&
                Convert.ToDouble(a["maximumNativeStretch"]) <= 1.733);

        internal static bool IsPaint(Material material) => material != null && material.name == "Resolute_rsl_paint";

        private static bool IsStyled(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            return materials.Length == MaterialCount && materials[0] == NavalMaterials.NativePlating && materials[3] == NavalMaterials.NativeDeck;
        }

        internal static void ApplyRenderer(Renderer renderer, Transform source, string group)
        {
            if (IsStyled(renderer)) return;
            // This separate flat base uses the finish atlas, while its aviation
            // markings are separate renderers. Give the base the same native
            // non-slip deck treatment as the rest of the ship, including LOD
            // and compartment-split copies; keep those markings authored.
            bool flightDeck = renderer.name == "rsl_flight_deck_surface48" ||
                renderer.name.StartsWith("rsl_flight_deck_surface48_", StringComparison.Ordinal);
            if (!IsPaint(renderer.sharedMaterial) && !flightDeck) return;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            filter.sharedMesh = Create(filter.sharedMesh, source.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                Vector3.zero, group, null, flightDeck);
            renderer.sharedMaterials = new[] { NavalMaterials.NativePlating, renderer.sharedMaterial,
                NavalMaterials.CreateInterior(renderer.sharedMaterial), NavalMaterials.NativeDeck };
        }

        internal static int ApplyMergedFlightDeck(Renderer renderer, Transform source, string group, MeshFilter flightDeck)
        {
            if (IsStyled(renderer)) return 0;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            HashSet<int> faces = FindFlightDeckFaces(filter.sharedMesh, source.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                flightDeck.sharedMesh, source.worldToLocalMatrix * flightDeck.transform.localToWorldMatrix);
            if (faces.Count == 0) return 0;
            Material accent = renderer.sharedMaterial;
            filter.sharedMesh = Create(filter.sharedMesh, source.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                Vector3.zero, group, null, false, faces);
            renderer.sharedMaterials = new[] { NavalMaterials.NativePlating, accent, NavalMaterials.CreateInterior(accent), NavalMaterials.NativeDeck };
            return faces.Count;
        }

        internal static HashSet<int> FindFlightDeckFaces(Mesh merged, Matrix4x4 mergedToSource, Mesh flightDeck, Matrix4x4 deckToSource)
        {
            // The LOD exporter retains this envelope verbatim but merges it
            // with other finish-atlas assemblies. Recover only those original
            // faces by position AND UV, so overlaid markings stay untouched.
            // Adjacent bins tolerate float-rounding at cell boundaries.
            Vector3[] deckPositions = flightDeck.vertices.Select(deckToSource.MultiplyPoint3x4).ToArray();
            Vector2[] deckUv = flightDeck.uv;
            int[] deckIndices = flightDeck.triangles;
            var cells = new Dictionary<Vector3, List<int>>();
            for (int face = 0; face < deckIndices.Length; face += 3)
            {
                Vector3 center = (deckPositions[deckIndices[face]] + deckPositions[deckIndices[face + 1]] + deckPositions[deckIndices[face + 2]]) / 3f;
                Vector3 key = FlightDeckCell(center); List<int> bucket;
                if (!cells.TryGetValue(key, out bucket)) cells.Add(key, bucket = new List<int>());
                bucket.Add(face);
            }
            var matches = new HashSet<int>();
            Vector3[] positions = merged.vertices.Select(mergedToSource.MultiplyPoint3x4).ToArray();
            Vector2[] uv = merged.uv; int[] indices = merged.triangles;
            for (int face = 0; face < indices.Length; face += 3)
            {
                Vector3 center = (positions[indices[face]] + positions[indices[face + 1]] + positions[indices[face + 2]]) / 3f;
                Vector3 key = FlightDeckCell(center); bool matched = false;
                for (int x = -1; x <= 1 && !matched; x++) for (int y = -1; y <= 1 && !matched; y++) for (int z = -1; z <= 1 && !matched; z++)
                {
                    List<int> bucket;
                    if (!cells.TryGetValue(key + new Vector3(x, y, z), out bucket)) continue;
                    foreach (int reference in bucket)
                    {
                        for (int corner = 0; corner < 3 && !matched; corner++)
                        {
                            bool same = true;
                            for (int offset = 0; offset < 3 && same; offset++)
                            {
                                int a = indices[face + offset], b = deckIndices[reference + (corner + offset) % 3];
                                same = (positions[a] - deckPositions[b]).sqrMagnitude <= .000001f && (uv[a] - deckUv[b]).sqrMagnitude <= .0000000001f;
                            }
                            matched = same;
                        }
                        if (matched) break;
                    }
                }
                if (matched) matches.Add(face / 3);
            }
            return matches;
        }

        private static Vector3 FlightDeckCell(Vector3 point) => new Vector3(Mathf.FloorToInt(point.x * 10f),
            Mathf.FloorToInt(point.y * 10f), Mathf.FloorToInt(point.z * 10f));

        internal static Mesh MergeRetained(IList<Mesh> meshes, IList<Vector3> centers, string name)
        {
            int count = meshes.Sum(mesh => mesh.vertexCount), offset = 0;
            var positions = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector4[count];
            var uv = new Vector2[count]; var uv1 = new Vector2[count]; var colors = new Color32[count];
            var triangles = Enumerable.Range(0, MaterialCount).Select(i => new List<int>()).ToArray();
            for (int i = 0; i < meshes.Count; i++)
            {
                Mesh mesh = meshes[i]; Vector3[] input = mesh.vertices;
                for (int vertex = 0; vertex < input.Length; vertex++) positions[offset + vertex] = input[vertex] + centers[i];
                Array.Copy(mesh.normals, 0, normals, offset, input.Length);
                Array.Copy(mesh.tangents, 0, tangents, offset, input.Length);
                Array.Copy(mesh.uv, 0, uv, offset, input.Length); Array.Copy(mesh.uv2, 0, uv1, offset, input.Length);
                Array.Copy(mesh.colors32, 0, colors, offset, input.Length);
                for (int material = 0; material < MaterialCount; material++)
                    foreach (int vertex in mesh.GetTriangles(material)) triangles[material].Add(vertex + offset);
                offset += input.Length;
            }
            var result = new Mesh { name = name, indexFormat = IndexFormat.UInt32, vertices = positions,
                normals = normals, tangents = tangents, uv = uv, uv2 = uv1, colors32 = colors, subMeshCount = MaterialCount };
            for (int material = 0; material < MaterialCount; material++) result.SetTriangles(triangles[material], material);
            result.RecalculateBounds(); return result;
        }

        internal static Mesh Create(Mesh original, Matrix4x4 localToSource, Vector3 center, string group, Mesh exteriorReference = null,
            bool authoredFlightDeck = false, HashSet<int> mergedFlightDeckFaces = null)
        {
            float greyDensity = group == "weapon" ? NativeWeaponUvPerMetre : NativeUvPerMetre;
            string key = original.GetInstanceID() + "/" + localToSource.ToString("R") + "/" + center.ToString("R") + "/" +
                (exteriorReference != null ? exteriorReference.GetInstanceID() : 0) + "/" + greyDensity.ToString("R") + "/" + authoredFlightDeck + "/" +
                (mergedFlightDeckFaces != null ? string.Join(",", mergedFlightDeckFaces.OrderBy(face => face)) : "");
            Mesh cached; if (Cache.TryGetValue(key, out cached)) return cached;
            FaceAtlas atlas = null;
            if (exteriorReference != null && !FaceAtlases.TryGetValue(exteriorReference, out atlas))
                FaceAtlases.Add(exteriorReference, atlas = new FaceAtlas(exteriorReference, localToSource));
            string before = GeometryHash(original);
            Vector3[] positions = original.vertices, normals = original.normals;
            Vector2[] uv = original.uv, uv1 = original.uv2;
            Color32[] colors = original.colors32;
            var resultPositions = new List<Vector3>(); var resultNormals = new List<Vector3>();
            var resultUv = new List<Vector2>(); var resultUv1 = new List<Vector2>(); var resultColors = new List<Color32>();
            var native = new List<int>(); var accent = new List<int>(); var interior = new List<int>(); var deck = new List<int>();
            double nativeArea = 0, deckArea = 0, accentArea = 0, maximumStretch = 0, minimumDensity = double.PositiveInfinity, maximumDensity = 0;
            bool cornersPreserved = true, accentUvPreserved = true;
            int mappedTriangles = 0, degenerateUnmappedTriangles = 0;
            for (int submesh = 0; submesh < original.subMeshCount; submesh++)
            {
                int[] triangles = original.GetTriangles(submesh);
                for (int index = 0; index < triangles.Length; index += 3)
                {
                    int a = triangles[index], b = triangles[index + 1], c = triangles[index + 2];
                    Vector3 pa = localToSource.MultiplyPoint3x4(positions[a] + center),
                        pb = localToSource.MultiplyPoint3x4(positions[b] + center),
                        pc = localToSource.MultiplyPoint3x4(positions[c] + center);
                    Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                    double area = cross.magnitude * .5;
                    int surface = submesh == 0 ?
                        authoredFlightDeck && cross.y > cross.magnitude * .7f ? 3 : Classify(uv[a], uv[b], uv[c], cross, (pa + pb + pc) / 3f) : 2;
                    if (mergedFlightDeckFaces != null)
                        surface = mergedFlightDeckFaces.Contains(index / 3) ? cross.y > cross.magnitude * .7f ? 3 : Classify(uv[a], uv[b], uv[c], cross, (pa + pb + pc) / 3f) : 1;
                    if (submesh == 0 && atlas != null)
                    {
                        int referenceSurface;
                        if (atlas.TryFind((positions[a] + positions[b] + positions[c]) / 3f + center,
                            (uv[a] + uv[b] + uv[c]) * (1f / 3f), out referenceSurface))
                        { surface = referenceSurface; mappedTriangles++; }
                        else
                        {
                            if (area > .0001) throw new InvalidOperationException("Torn paint surface has no intact source triangle: " + original.name);
                            degenerateUnmappedTriangles++;
                        }
                    }
                    bool grey = surface == 0, nativeDeck = surface == 3, metric = grey || nativeDeck;
                    float targetDensity = nativeDeck ? NativeDeckUvPerMetre : greyDensity;
                    int axis = 0;
                    if (Mathf.Abs(cross.y) > Mathf.Abs(cross.x)) axis = 1;
                    if (Mathf.Abs(cross.z) > Mathf.Abs(cross[axis])) axis = 2;
                    float sign = cross[axis] >= 0 ? 1 : -1;
                    if (submesh == 0)
                    {
                        if (grey) nativeArea += area; else if (nativeDeck) deckArea += area; else accentArea += area;
                        if (metric && area > .0000001)
                        {
                            // Orthogonal projection's singular values are 1
                            // and the absolute dominant unit-normal component.
                            double dominant = Math.Abs(cross[axis]) / cross.magnitude;
                            maximumStretch = Math.Max(maximumStretch, 1 / dominant);
                            double density = targetDensity * Math.Sqrt(dominant);
                            minimumDensity = Math.Min(minimumDensity, density); maximumDensity = Math.Max(maximumDensity, density);
                        }
                    }
                    List<int> destination = surface == 2 ? interior : grey ? native : nativeDeck ? deck : accent;
                    foreach (int vertex in new[] { a, b, c })
                    {
                        Vector2 nextUv = metric ? Project(localToSource.MultiplyPoint3x4(positions[vertex] + center), axis, sign, targetDensity) : uv[vertex];
                        int next = resultPositions.Count; destination.Add(next);
                        resultPositions.Add(positions[vertex]); resultNormals.Add(normals[vertex]); resultUv.Add(nextUv);
                        resultUv1.Add(uv1.Length == positions.Length ? uv1[vertex] : Vector2.zero);
                        resultColors.Add(colors.Length == positions.Length ? colors[vertex] : new Color32(255, 255, 255, 255));
                        cornersPreserved &= resultPositions[next].Equals(positions[vertex]) && resultNormals[next].Equals(normals[vertex]);
                        if (!metric) accentUvPreserved &= nextUv.Equals(uv[vertex]);
                    }
                }
            }
            var result = new Mesh { name = original.name + " / native metric paint", indexFormat = IndexFormat.UInt32 };
            result.SetVertices(resultPositions); result.SetNormals(resultNormals); result.SetUVs(0, resultUv); result.SetUVs(1, resultUv1);
            result.SetColors(resultColors); result.subMeshCount = MaterialCount;
            result.SetTriangles(native, 0); result.SetTriangles(accent, 1); result.SetTriangles(interior, 2); result.SetTriangles(deck, 3);
            result.RecalculateBounds(); result.RecalculateTangents();
            string after = GeometryHash(original);
            if (before != after || !cornersPreserved || !accentUvPreserved || maximumStretch > 1.733)
                throw new InvalidOperationException("Native paint changed authoritative geometry or stretched its metric projection: " + original.name);
            UnityEngine.Object.DontDestroyOnLoad(result); Cache.Add(key, result);
            var audit = new Dictionary<string, object> { ["mesh"] = original.name, ["group"] = group,
                ["authoredFlightDeckBase"] = authoredFlightDeck,
                ["mergedFlightDeckFaces"] = mergedFlightDeckFaces != null ? mergedFlightDeckFaces.Count : 0,
                ["sourceGeometrySha256Before"] = before, ["sourceGeometrySha256After"] = after,
                ["sourceGeometryUnchanged"] = before == after, ["renderCornersPreserved"] = cornersPreserved,
                ["accentUvPreserved"] = accentUvPreserved, ["sourceTriangleCount"] = original.triangles.Length / 3,
                ["renderTriangleCount"] = (native.Count + accent.Count + interior.Count + deck.Count) / 3,
                ["nativeGreyTriangles"] = native.Count / 3, ["preservedAccentTriangles"] = accent.Count / 3,
                ["nativeDeckTriangles"] = deck.Count / 3, ["nativeDeckAreaM2"] = deckArea,
                ["nativeGreyAreaM2"] = nativeArea, ["preservedAccentAreaM2"] = accentArea,
                ["maximumNativeStretch"] = maximumStretch, ["minimumNativeUvPerM"] = double.IsInfinity(minimumDensity) ? 0 : minimumDensity,
                ["maximumNativeUvPerM"] = maximumDensity, ["intactSourceMappedTriangles"] = mappedTriangles,
                ["degenerateUnmappedTriangles"] = degenerateUnmappedTriangles,
                ["intactSourceReference"] = exteriorReference != null ? exteriorReference.name : null };
            MeshAudits.Add(audit);
            Dictionary<string, object> total;
            if (!Groups.TryGetValue(group, out total)) Groups.Add(group, total = new Dictionary<string, object> {
                ["meshes"] = 0, ["nativeGreyAreaM2"] = 0d, ["nativeDeckAreaM2"] = 0d, ["preservedAccentAreaM2"] = 0d });
            total["meshes"] = Convert.ToInt32(total["meshes"]) + 1;
            total["nativeGreyAreaM2"] = Convert.ToDouble(total["nativeGreyAreaM2"]) + nativeArea;
            total["nativeDeckAreaM2"] = Convert.ToDouble(total["nativeDeckAreaM2"]) + deckArea;
            total["preservedAccentAreaM2"] = Convert.ToDouble(total["preservedAccentAreaM2"]) + accentArea;
            return result;
        }

        private static Vector2 Project(Vector3 point, int axis, float sign, float density)
        {
            // Vertical seams remain vertical on both sides; the deck is mapped
            // along length and beam. Opposite faces receive a fixed phase.
            Vector2 coordinates = axis == 0 ? new Vector2(point.z * sign, point.y) :
                axis == 1 ? new Vector2(point.x * sign, point.z) : new Vector2(-point.x * sign, point.y);
            return coordinates * density + new Vector2(axis * .173f + (sign < 0 ? .413f : 0f), axis * .317f);
        }

        private static int Classify(Vector2 a, Vector2 b, Vector2 c, Vector3 sourceCross, Vector3 sourceCenter)
        {
            // The original dark non-slip deck is the atlas's bottom-row second
            // cell. Require all three UVs to stay inside that cell and the
            // evaluated surface to face upward; windows, black machinery,
            // hull antifouling and painted safety markings stay authored.
            if (sourceCross.y > sourceCross.magnitude * .7f && InDeck(a) && InDeck(b) && InDeck(c))
                return IsEquipmentTop(sourceCenter) ? 0 : 3;
            return IsGrey(a, b, c) ? 0 : 1;
        }

        private static bool IsEquipmentTop(Vector3 point)
        {
            // Measured from the authored hull, in ship metres (Y=waterline,
            // +Z=bow). These regions contain equipment roofs, not walkways.
            // Their paint faces survive unchanged in both distant LODs. Use
            // original ship coordinates so compartment cuts and torn pieces
            // receive the same material as the intact shell.
            // Mast roof: Y=36.0585; small mast ledges also lie above Y=27.29.
            // The surrounding walkable superstructure roof is at Y=24.6935.
            if (Mathf.Abs(point.x) <= 5.4f && point.z >= 3.1f && point.z <= 12.1f &&
                point.y >= 27.2f && point.y <= 44f) return true;
            // Forward funnel roof: Y=23.1965, above the surrounding decks.
            if (Mathf.Abs(point.x) <= 3.9f && point.z >= -10.15f && point.z <= .8f &&
                point.y >= 23.1f && point.y <= 23.3f) return true;
            // Aft funnel roof strips: Y=17.0703..17.0760. Native gray plating
            // fills them; the separate exhaust openings and rims are retained.
            return Mathf.Abs(point.x) <= 3.05f && point.z >= -78.75f && point.z <= -69f &&
                point.y >= 16.97f && point.y <= 17.18f;
        }

        private static bool InDeck(Vector2 uv)
        {
            float u = uv.x - Mathf.Floor(uv.x), v = uv.y - Mathf.Floor(uv.y);
            return u >= .125f && u < .25f && v >= 0f && v < .125f;
        }

        private static bool IsGrey(Vector2 a, Vector2 b, Vector2 c)
        {
            // The original 8x8 atlas reserves lower-left swatches for dark deck,
            // antifouling, windows, signals and markings. Keep a whole triangle
            // authored if any of its atlas bounding cells contains an accent.
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * 8), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * 8);
            int y0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * 8), y1 = Mathf.FloorToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * 8);
            if (x1 - x0 > 16 || y1 - y0 > 16) return false;
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                if (GreyAtlas[(y % 8 + 8) % 8][(x % 8 + 8) % 8] != '1') return false;
            return true;
        }

        private sealed class FaceAtlas
        {
            private sealed class Face
            { internal Vector3 A, B, C, N; internal Vector2 Ua, Ub, Uc; internal int Surface, Axis; }
            private readonly Dictionary<string, List<Face>> buckets = new Dictionary<string, List<Face>>();
            private static int Grid(float value) => Mathf.FloorToInt(value / 12f);
            private static string Key(int x, int y, int z) => x + "/" + y + "/" + z;
            internal FaceAtlas(Mesh reference, Matrix4x4 localToSource)
            {
                Vector3[] p = reference.vertices; Vector2[] uv = reference.uv; int[] t = reference.GetTriangles(0);
                for (int i = 0; i < t.Length; i += 3)
                {
                    var face = new Face { A = p[t[i]], B = p[t[i + 1]], C = p[t[i + 2]], Ua = uv[t[i]], Ub = uv[t[i + 1]], Uc = uv[t[i + 2]] };
                    Vector3 cross = Vector3.Cross(face.B - face.A, face.C - face.A);
                    if (cross.sqrMagnitude < 1e-16f) continue;
                    face.N = cross / cross.magnitude;
                    face.Axis = Mathf.Abs(cross.y) > Mathf.Abs(cross.x) ? 1 : 0;
                    if (Mathf.Abs(cross.z) > Mathf.Abs(cross[face.Axis])) face.Axis = 2;
                    Vector3 sourceCross = Vector3.Cross(localToSource.MultiplyVector(face.B - face.A), localToSource.MultiplyVector(face.C - face.A));
                    face.Surface = Classify(face.Ua, face.Ub, face.Uc, sourceCross,
                        localToSource.MultiplyPoint3x4((face.A + face.B + face.C) / 3f));
                    Vector3 lo = Vector3.Min(face.A, Vector3.Min(face.B, face.C)) - Vector3.one * .002f;
                    Vector3 hi = Vector3.Max(face.A, Vector3.Max(face.B, face.C)) + Vector3.one * .002f;
                    for (int x = Grid(lo.x); x <= Grid(hi.x); x++) for (int y = Grid(lo.y); y <= Grid(hi.y); y++) for (int z = Grid(lo.z); z <= Grid(hi.z); z++)
                    {
                        string key = Key(x, y, z); List<Face> list;
                        if (!buckets.TryGetValue(key, out list)) buckets.Add(key, list = new List<Face>());
                        list.Add(face);
                    }
                }
            }
            internal bool TryFind(Vector3 point, Vector2 sourceUv, out int surface)
            {
                surface = 1; List<Face> faces;
                if (!buckets.TryGetValue(Key(Grid(point.x), Grid(point.y), Grid(point.z)), out faces)) return false;
                foreach (Face face in faces)
                {
                    if (Mathf.Abs(Vector3.Dot(point - face.A, face.N)) > .002f) continue;
                    int u = (face.Axis + 1) % 3, v = (face.Axis + 2) % 3;
                    double ax = face.B[u] - face.A[u], ay = face.B[v] - face.A[v], bx = face.C[u] - face.A[u], by = face.C[v] - face.A[v];
                    double px = point[u] - face.A[u], py = point[v] - face.A[v], determinant = ax * by - ay * bx;
                    if (Math.Abs(determinant) < 1e-12) continue;
                    double b = (px * by - py * bx) / determinant, c = (ax * py - ay * px) / determinant, a = 1 - b - c;
                    if (a < -.002 || b < -.002 || c < -.002) continue;
                    Vector2 expected = face.Ua * (float)a + face.Ub * (float)b + face.Uc * (float)c;
                    if (Mathf.Abs(expected.x - sourceUv.x) > .00005f || Mathf.Abs(expected.y - sourceUv.y) > .00005f) continue;
                    surface = face.Surface; return true;
                }
                return false;
            }
        }

        private static string GeometryHash(Mesh mesh)
        {
            using (var data = new MemoryStream()) using (var writer = new BinaryWriter(data))
            {
                foreach (Vector3[] values in new[] { mesh.vertices, mesh.normals }) foreach (Vector3 p in values)
                { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
                foreach (Vector2[] values in new[] { mesh.uv, mesh.uv2 }) foreach (Vector2 p in values) { writer.Write(p.x); writer.Write(p.y); }
                foreach (Color32 color in mesh.colors32) { writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a); }
                writer.Write(mesh.subMeshCount); for (int i = 0; i < mesh.subMeshCount; i++) foreach (int vertex in mesh.GetTriangles(i)) writer.Write(vertex);
                writer.Flush(); using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    // Event-driven: healthy vessels pay no extra draw calls or Update callbacks.
    // Real fracture solids already have inner steel. Stop drawing the original
    // reverse shell as soon as a piece tears, so it cannot cover the new opening.
    internal sealed class ResoluteDamageBackfaces : MonoBehaviour
    {
        public UnitPart Part;
        public Renderer Source, Faces;
        public ResoluteStructuralSection Section;

        private void Awake()
        {
            Faces.enabled = false;
            Part.onApplyDamage += Damaged;
            if (Section != null) Section.SurfacePieceReleased += Torn;
        }

        private void Damaged(UnitPart.OnApplyDamage damage)
        { Faces.enabled = damage.hitPoints < 100f && Source.enabled && (Section == null || Section.ReleasedCount == 0); }

        private void Torn(ResoluteStructuralSection section, int piece, Transform debris)
        { Faces.enabled = false; }

        private void OnDestroy()
        {
            if (Part != null) Part.onApplyDamage -= Damaged;
            if (Section != null) Section.SurfacePieceReleased -= Torn;
        }
    }
}
