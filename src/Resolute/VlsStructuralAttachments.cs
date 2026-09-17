using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // A fixed launch station can span compartments. Its physical cells instead
    // follow the individual deck surfaces which carry their hinges and wells.
    internal sealed class VlsStructuralAttachments : MonoBehaviour
    {
        [Serializable]
        public sealed class Cell
        {
            public ResoluteVlsLauncher Launcher;
            public int Index, SupportPiece;
            public ResoluteStructuralSection Section;
            public Transform Hatch, Anchor;
            public Vector2 Position;
        }

        [Serializable]
        public sealed class Fitting
        {
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Matrix4x4 ShipFromMesh;
            public Mesh[] CellMeshes;
        }

        // Runtime-added MonoBehaviours do not reliably retain nested managed
        // records through Unity's prefab clone. Store their native references
        // and value fields in flat arrays, then rebuild records on each ship.
        [SerializeField] private ResoluteVlsLauncher[] cellLaunchers;
        [SerializeField] private int[] cellIndices, cellSupportPieces;
        [SerializeField] private ResoluteStructuralSection[] cellSections;
        [SerializeField] private Transform[] cellHatches, cellAnchors;
        [SerializeField] private Vector2[] cellPositions;
        [SerializeField] private MeshFilter[] fittingFilters;
        [SerializeField] private MeshRenderer[] fittingRenderers;
        [SerializeField] private Matrix4x4[] fittingShipFromMesh;
        [SerializeField] private Mesh[] fittingCellMeshes;
        [NonSerialized] private Cell[] Cells;
        [NonSerialized] private Fitting[] Fittings;
        private bool[] separated;
        private ResoluteStructuralSection[] sections;
        private Batch[] batches;
        private static readonly Dictionary<GameObject, List<Mesh>> TemplateMeshes = new Dictionary<GameObject, List<Mesh>>();

        internal static void Configure(Ship ship, StructuralGeometry.Result structure)
            => ConfigureAsync(ship, structure, null).GetAwaiter().GetResult();

        internal static async UniTask ConfigureAsync(Ship ship, StructuralGeometry.Result structure, StartupLoadContext loading = null)
        {
            var cells = new List<Cell>();
            var supportSurfaces = new Dictionary<ResoluteStructuralSection, List<SupportTriangle>>();
            foreach (var cell in structure.Cells)
            {
                if (loading != null) await loading.Step("Preparing missile launch decks");
                supportSurfaces.Add(cell.Section, await ReadSupportAsync(cell.Section, loading));
            }
            foreach (ResoluteVlsLauncher launcher in ship.GetComponentsInChildren<ResoluteVlsLauncher>(true))
            {
                Transform[] anchors = NaturalArmament.Get<Transform[]>(launcher, "launchTransforms");
                Transform[] hatches = launcher.GetComponent<ResoluteVlsAnimation>().Hatches;
                for (int i = 0; i < hatches.Length; i++)
                {
                    if (loading != null) await loading.Step("Fitting missile cell " + (i + 1) + "/" + hatches.Length);
                    Transform hatch = hatches[i];
                    // The old parent is only an imported hierarchy hint. The
                    // midships farm was below an upper deck's separate owner.
                    // Resolve the actual supporting surface across all sections.
                    var support = FindSupport(supportSurfaces, hatch.position, anchors[i].position);
                    var section = support.Section;
                    Vector3 center = ship.transform.InverseTransformPoint(anchors[i].position);
                    cells.Add(new Cell { Launcher = launcher, Index = i, Section = section,
                        SupportPiece = support.Piece, Hatch = hatch, Anchor = anchors[i],
                        Position = new Vector2(center.x, center.z) });
                    hatch.SetParent(section.transform, true);
                    anchors[i].SetParent(section.transform, true);
                }
            }
            var component = ship.gameObject.AddComponent<VlsStructuralAttachments>();
            component.Cells = cells.ToArray();
            var ownedMeshes = new List<Mesh>();
            TemplateMeshes.Add(ship.gameObject, ownedMeshes);
            try
            {
                Fitting[] original = structure.LodRenderers.OfType<MeshRenderer>()
                    .Where(r => IsCellFitting(r.name))
                    .Select(r => new Fitting { Renderer = r, Filter = r.GetComponent<MeshFilter>(),
                        ShipFromMesh = ship.transform.worldToLocalMatrix * r.transform.localToWorldMatrix })
                    .Where(f => f.Filter != null && f.Filter.sharedMesh != null).ToArray();
                var fittings = new List<Fitting>();
                ResoluteStructuralSection[] supports = cells.Select(c => c.Section).Distinct().ToArray();
                foreach (Fitting source in original)
                {
                    if (loading != null) await loading.Step("Fitting missile cells");
                    var partition = await Batch.CreateAsync(source, component.Cells, loading);
                    foreach (var support in supports)
                    {
                        if (loading != null) await loading.Step("Building missile launch supports");
                        Fitting fitted = await partition.CreateOwnedFittingAsync(component.Cells, support, ownedMeshes, loading);
                        if (fitted == null) continue;
                        fittings.Add(fitted);
                        StructuralGeometry.Add(structure, support.Part, fitted.Renderer);
                    }
                    // Register the replacement batches with the same physical
                    // support as their lids before native damage materials/LODs.
                    foreach (var renderers in structure.Renderers.Values) renderers.Remove(source.Renderer);
                    structure.LodRenderers.Remove(source.Renderer);
                    source.Renderer.enabled = false;
                }
                component.Fittings = fittings.ToArray();
                component.StoreCloneData();
            }
            catch
            {
                DestroyFailedTemplate(ship.gameObject);
                throw;
            }
        }

        private void StoreCloneData()
        {
            cellLaunchers = Cells.Select(c => c.Launcher).ToArray();
            cellIndices = Cells.Select(c => c.Index).ToArray();
            cellSupportPieces = Cells.Select(c => c.SupportPiece).ToArray();
            cellSections = Cells.Select(c => c.Section).ToArray();
            cellHatches = Cells.Select(c => c.Hatch).ToArray();
            cellAnchors = Cells.Select(c => c.Anchor).ToArray();
            cellPositions = Cells.Select(c => c.Position).ToArray();
            fittingFilters = Fittings.Select(f => f.Filter).ToArray();
            fittingRenderers = Fittings.Select(f => f.Renderer).ToArray();
            fittingShipFromMesh = Fittings.Select(f => f.ShipFromMesh).ToArray();
            // Keep the prepartitioned meshes, including null entries, in the
            // exact fitting-major/cell-minor order used during assembly.
            fittingCellMeshes = Fittings.SelectMany(f => f.CellMeshes).ToArray();
        }

        private void RestoreCloneData()
        {
            int count = cellLaunchers != null ? cellLaunchers.Length : 0;
            int fittingCount = fittingFilters != null ? fittingFilters.Length : 0;
            if (count == 0 || cellIndices == null || cellIndices.Length != count ||
                cellSupportPieces == null || cellSupportPieces.Length != count ||
                cellSections == null || cellSections.Length != count ||
                cellHatches == null || cellHatches.Length != count ||
                cellAnchors == null || cellAnchors.Length != count ||
                cellPositions == null || cellPositions.Length != count ||
                fittingRenderers == null || fittingRenderers.Length != fittingCount ||
                fittingShipFromMesh == null || fittingShipFromMesh.Length != fittingCount ||
                fittingCellMeshes == null || fittingCellMeshes.Length != count * fittingCount)
                throw new InvalidOperationException("Resolute VLS clone is missing its serialized structural layout.");
            Cells = new Cell[count];
            for (int i = 0; i < count; i++)
            {
                if (cellLaunchers[i] == null || !cellLaunchers[i].transform.IsChildOf(transform) ||
                    cellSections[i] == null || !cellSections[i].transform.IsChildOf(transform) ||
                    cellHatches[i] == null || !cellHatches[i].IsChildOf(cellSections[i].transform) ||
                    cellAnchors[i] == null || !cellAnchors[i].IsChildOf(cellSections[i].transform))
                    throw new InvalidOperationException("Resolute VLS clone has an unowned cell attachment: " + i);
                Cells[i] = new Cell { Launcher = cellLaunchers[i], Index = cellIndices[i], SupportPiece = cellSupportPieces[i],
                    Section = cellSections[i], Hatch = cellHatches[i], Anchor = cellAnchors[i], Position = cellPositions[i] };
            }
            Fittings = new Fitting[fittingCount];
            for (int i = 0; i < fittingCount; i++)
            {
                if (fittingFilters[i] == null || !fittingFilters[i].transform.IsChildOf(transform) ||
                    fittingRenderers[i] == null || fittingRenderers[i].gameObject != fittingFilters[i].gameObject)
                    throw new InvalidOperationException("Resolute VLS clone has an unowned fitting: " + i);
                var fragments = new Mesh[count];
                Array.Copy(fittingCellMeshes, i * count, fragments, 0, count);
                Fittings[i] = new Fitting { Filter = fittingFilters[i], Renderer = fittingRenderers[i],
                    ShipFromMesh = fittingShipFromMesh[i], CellMeshes = fragments };
            }
        }

        internal static void DestroyFailedTemplate(GameObject prefab)
        {
            if (!TemplateMeshes.TryGetValue(prefab, out var meshes)) return;
            TemplateMeshes.Remove(prefab);
            foreach (Mesh mesh in meshes) if (mesh != null) Object.Destroy(mesh);
        }

        private static bool IsCellFitting(string name)
        {
            return new[] { "rsl_vls_frames", "rsl_vls_wells", "rsl_large_vls_plinths",
                "rsl_small_p_plinth", "rsl_small_p_frames", "rsl_small_s_plinth", "rsl_small_s_frames" }
                .Any(prefix => name == prefix || name.StartsWith(prefix + "_", StringComparison.Ordinal));
        }

        private struct SupportTriangle
        {
            internal Vector3 A, B, C;
            internal int Piece;
        }

        private static List<SupportTriangle> ReadSupport(ResoluteStructuralSection section)
            => ReadSupportAsync(section, null).GetAwaiter().GetResult();

        private static async UniTask<List<SupportTriangle>> ReadSupportAsync(ResoluteStructuralSection section, StartupLoadContext loading)
        {
            var surfaces = new List<SupportTriangle>();
            // Surface meshes are centered on PlateCenters, not on the section.
            for (int piece = -1; piece < section.SurfaceMeshes.Length; piece++)
            {
                if (loading != null) await loading.Step("Preparing missile launch supports");
                Mesh mesh = piece < 0 ? section.RetainedDetailSolid : section.SurfaceMeshes[piece];
                if (mesh == null) continue;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                Vector3 center = piece < 0 ? section.RetainedDetailCenter : section.PlateCenters[piece];
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    if (loading != null && i % 192 == 0) await loading.Step("Preparing missile launch supports");
                    Vector3 a = vertices[triangles[i]] + center, b = vertices[triangles[i + 1]] + center,
                        c = vertices[triangles[i + 2]] + center;
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    if (normal.sqrMagnitude < 1e-10f || normal.normalized.y < .45f) continue;
                    surfaces.Add(new SupportTriangle { A = a, B = b, C = c, Piece = piece });
                }
            }
            return surfaces;
        }

        private struct SupportOwner
        {
            internal ResoluteStructuralSection Section;
            internal int Piece;
        }

        private static SupportOwner FindSupport(Dictionary<ResoluteStructuralSection, List<SupportTriangle>> sections,
            Vector3 hinge, Vector3 anchor)
        {
            // The hinge is on the supported rim; the launch point can be over
            // an opening. Prefer an actual vertical footprint at the hinge.
            // Only a bounded adjacent rim is accepted if that point is a seam.
            foreach (Vector3 position in new[] { hinge, Vector3.Lerp(hinge, anchor, .5f), anchor })
            {
                float highest = float.NegativeInfinity; SupportOwner best = default(SupportOwner);
                foreach (var pair in sections)
                foreach (SupportTriangle triangle in pair.Value)
                {
                    Vector3 a = pair.Key.transform.TransformPoint(triangle.A), b = pair.Key.transform.TransformPoint(triangle.B),
                        c = pair.Key.transform.TransformPoint(triangle.C);
                    Vector3 u = b - a, v = c - a;
                    float denominator = u.x * v.z - u.z * v.x;
                    if (Mathf.Abs(denominator) < 1e-8f) continue;
                    float x = position.x - a.x, z = position.z - a.z;
                    float s = (x * v.z - z * v.x) / denominator, t = (u.x * z - u.z * x) / denominator;
                    if (s < -.0001f || t < -.0001f || s + t > 1.0001f) continue;
                    float y = a.y + s * u.y + t * v.y;
                    if (y > hinge.y + .05f || y < hinge.y - 2f || y <= highest) continue;
                    highest = y; best = new SupportOwner { Section = pair.Key, Piece = triangle.Piece };
                }
                if (best.Section != null) return best;
            }
            float nearest = float.PositiveInfinity; SupportOwner rim = default(SupportOwner);
            foreach (var pair in sections)
            foreach (SupportTriangle triangle in pair.Value)
            {
                Vector3 a = pair.Key.transform.TransformPoint(triangle.A), b = pair.Key.transform.TransformPoint(triangle.B),
                    c = pair.Key.transform.TransformPoint(triangle.C);
                Vector3 point = ClosestPoint(hinge, a, b, c);
                float horizontal = (new Vector2(point.x - hinge.x, point.z - hinge.z)).sqrMagnitude;
                if (horizontal > .35f * .35f || point.y > hinge.y + .05f || point.y < hinge.y - 2f) continue;
                float distance = (point - hinge).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance; rim = new SupportOwner { Section = pair.Key, Piece = triangle.Piece };
            }
            if (rim.Section != null) return rim;
            throw new InvalidOperationException("No actual supporting deck within the VLS mounting footprint at " + hinge);
        }

        private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float inverse = 1f / (va + vb + vc);
            return a + ab * (vb * inverse) + ac * (vc * inverse);
        }

        private void Awake()
        {
            RestoreCloneData();
            separated = new bool[Cells.Length];
            batches = new Batch[Fittings.Length];
            sections = Cells.Select(c => c.Section).Distinct().ToArray();
            foreach (var section in sections) section.SurfacePieceReleased += SupportReleased;
        }

        private void SupportReleased(ResoluteStructuralSection section, int piece, Transform debris)
        {
            int[] lost = Enumerable.Range(0, Cells.Length)
                .Where(i => !separated[i] && Cells[i].Section == section && Cells[i].SupportPiece == piece).ToArray();
            if (lost.Length == 0) return;
            foreach (int i in lost)
            {
                Cell cell = Cells[i]; separated[i] = true;
                cell.Launcher.LoseStructuralSupport(cell.Index);
                cell.Launcher.GetComponent<ResoluteVlsAnimation>().DetachCell(cell.Index);
                if (cell.Hatch != null) cell.Hatch.SetParent(debris, true);
                if (cell.Anchor != null) cell.Anchor.SetParent(debris, true);
            }
            // Intact ships retain compartment batches; individual cell meshes
            // are immutable prefab data with no renderer or Rigidbody apiece.
            for (int i = 0; i < Fittings.Length; i++)
            {
                if (!lost.Any(cell => Fittings[i].CellMeshes[cell] != null)) continue;
                if (batches[i] == null) batches[i] = new Batch(Fittings[i], Cells);
                batches[i].Release(lost, separated, debris, section.Part.hitPoints);
            }
        }

        private void OnDestroy()
        {
            if (sections != null) foreach (var section in sections)
                if (section != null) section.SurfacePieceReleased -= SupportReleased;
            if (batches != null) foreach (Batch batch in batches) batch?.Dispose();
        }

        private struct Vertex
        {
            internal Vector3 P, N;
            internal Vector4 T;
            internal Vector2 U, U1, Projection;
            internal Color C;
            internal static Vertex Lerp(Vertex a, Vertex b, float t) => new Vertex {
                P = Vector3.LerpUnclamped(a.P, b.P, t), N = Vector3.LerpUnclamped(a.N, b.N, t).normalized,
                T = Vector4.LerpUnclamped(a.T, b.T, t), U = Vector2.LerpUnclamped(a.U, b.U, t),
                U1 = Vector2.LerpUnclamped(a.U1, b.U1, t), C = Color.LerpUnclamped(a.C, b.C, t),
                Projection = Vector2.LerpUnclamped(a.Projection, b.Projection, t) };
        }

        private sealed class Triangle
        {
            internal Vertex A, B, C;
            internal int Material;
        }

        private sealed class Batch
        {
            private readonly Fitting fitting;
            private readonly List<Triangle>[] cells;
            private readonly int materials;
            private Mesh retained;

            internal Batch(Fitting source, Cell[] layout)
                : this(source, layout, true) { }

            private Batch(Fitting source, Cell[] layout, bool populate)
            {
                fitting = source;
                cells = Enumerable.Range(0, layout.Length).Select(_ => new List<Triangle>()).ToArray();
                materials = source.Filter != null ? source.Filter.sharedMesh.subMeshCount : 0;
                if (populate) PopulateAsync(source, layout, null).GetAwaiter().GetResult();
            }

            internal static async UniTask<Batch> CreateAsync(Fitting source, Cell[] layout, StartupLoadContext loading)
            {
                var batch = new Batch(source, layout, false);
                await batch.PopulateAsync(source, layout, loading);
                return batch;
            }

            private async UniTask PopulateAsync(Fitting source, Cell[] layout, StartupLoadContext loading)
            {
                if (source.Filter == null) return;
                Mesh mesh = source.Filter.sharedMesh;
                if (source.CellMeshes != null)
                {
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (loading != null) await loading.Step("Loading missile cell " + (i + 1) + "/" + cells.Length);
                        if (source.CellMeshes[i] != null) Read(source.CellMeshes[i], source.ShipFromMesh, cells[i]);
                    }
                    return;
                }
                Vector3[] p = mesh.vertices, n = mesh.normals;
                Vector4[] t = mesh.tangents;
                Vector2[] u = mesh.uv, u1 = mesh.uv2;
                Color[] colors = mesh.colors;
                Vertex[] vertices = new Vertex[p.Length];
                for (int i = 0; i < p.Length; i++)
                {
                    if (loading != null && i % 256 == 0) await loading.Step("Fitting missile launch hardware");
                    Vector3 projected = source.ShipFromMesh.MultiplyPoint3x4(p[i]);
                    vertices[i] = new Vertex { P = p[i], N = n.Length == p.Length ? n[i] : Vector3.up,
                        T = t.Length == p.Length ? t[i] : new Vector4(1, 0, 0, 1),
                        U = u.Length == p.Length ? u[i] : Vector2.zero, U1 = u1.Length == p.Length ? u1[i] : Vector2.zero,
                        C = colors.Length == p.Length ? colors[i] : Color.white,
                        Projection = new Vector2(projected.x, projected.z) };
                }
                for (int material = 0; material < materials; material++)
                {
                    int[] indices = mesh.GetTriangles(material);
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        if (loading != null && i % 192 == 0) await loading.Step("Fitting missile cells");
                        Vertex a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                        int ca = Nearest(layout, a.Projection), cb = Nearest(layout, b.Projection), cc = Nearest(layout, c.Projection);
                        if (ca == cb && ca == cc) { Add(cells[ca], a, b, c, material); continue; }
                        // Clip triangles crossing cell boundaries. Assigning a
                        // broad plinth solely by its centroid leaves floating
                        // triangles behind when a neighboring cell tears away.
                        int[] candidates = Candidates(layout, a.Projection, b.Projection, c.Projection);
                        foreach (int cell in candidates)
                        {
                            var polygon = new List<Vertex> { a, b, c };
                            foreach (int other in candidates)
                            {
                                if (polygon.Count < 3) break;
                                if (other == cell) continue;
                                Vector2 normal = layout[other].Position - layout[cell].Position;
                                float offset = (layout[other].Position.sqrMagnitude - layout[cell].Position.sqrMagnitude) * .5f;
                                polygon = Clip(polygon, normal, offset);
                            }
                            for (int j = 1; j + 1 < polygon.Count; j++) Add(cells[cell], polygon[0], polygon[j], polygon[j + 1], material);
                        }
                    }
                }
            }

            internal Fitting CreateOwnedFitting(Cell[] layout, ResoluteStructuralSection support, List<Mesh> ownedMeshes)
                => CreateOwnedFittingAsync(layout, support, ownedMeshes, null).GetAwaiter().GetResult();

            internal async UniTask<Fitting> CreateOwnedFittingAsync(Cell[] layout, ResoluteStructuralSection support,
                List<Mesh> ownedMeshes, StartupLoadContext loading)
            {
                int[] selected = Enumerable.Range(0, layout.Length).Where(i => layout[i].Section == support && cells[i].Count > 0).ToArray();
                if (selected.Length == 0) return null;
                var obj = new GameObject(fitting.Renderer.name + "_cell_support_" + support.name) { layer = fitting.Renderer.gameObject.layer };
                obj.transform.SetParent(fitting.Filter.transform, false);
                obj.transform.SetParent(support.transform, true);
                var filter = obj.AddComponent<MeshFilter>();
                filter.sharedMesh = await BuildAsync(selected.SelectMany(i => cells[i]), materials, "Resolute fitted VLS hardware / " + support.name, loading);
                ownedMeshes.Add(filter.sharedMesh);
                MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = fitting.Renderer.sharedMaterials;
                renderer.shadowCastingMode = fitting.Renderer.shadowCastingMode;
                renderer.receiveShadows = fitting.Renderer.receiveShadows;
                var fragments = new Mesh[layout.Length];
                foreach (int i in selected)
                {
                    if (loading != null) await loading.Step("Preparing missile cell damage " + (i + 1) + "/" + layout.Length);
                    fragments[i] = await BuildAsync(cells[i], materials, "Resolute cell hardware / " + i, loading);
                    ownedMeshes.Add(fragments[i]);
                }
                return new Fitting { Filter = filter, Renderer = renderer, ShipFromMesh = fitting.ShipFromMesh, CellMeshes = fragments };
            }

            private static void Read(Mesh mesh, Matrix4x4 projection, List<Triangle> output)
            {
                Vector3[] p = mesh.vertices, n = mesh.normals; Vector4[] t = mesh.tangents;
                Vector2[] u = mesh.uv, u1 = mesh.uv2; Color[] colors = mesh.colors;
                var vertices = new Vertex[p.Length];
                for (int i = 0; i < p.Length; i++)
                {
                    Vector3 point = projection.MultiplyPoint3x4(p[i]);
                    vertices[i] = new Vertex { P = p[i], N = n[i], T = t[i], U = u[i], U1 = u1[i], C = colors[i],
                        Projection = new Vector2(point.x, point.z) };
                }
                for (int m = 0; m < mesh.subMeshCount; m++)
                {
                    int[] indices = mesh.GetTriangles(m);
                    for (int i = 0; i < indices.Length; i += 3) Add(output, vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], m);
                }
            }

            internal void Release(int[] lost, bool[] separated, Transform debris, float hitPoints)
            {
                if (fitting.Filter == null || fitting.Renderer == null) return;
                var torn = lost.SelectMany(i => cells[i]).ToList();
                if (torn.Count == 0) return;
                Mesh fragment = Build(torn, materials, "Resolute separated VLS cell hardware");
                var obj = new GameObject("Resolute VLS cell hardware") { layer = fitting.Renderer.gameObject.layer };
                obj.transform.SetParent(fitting.Filter.transform, false);
                obj.transform.SetParent(debris, true);
                obj.AddComponent<MeshFilter>().sharedMesh = fragment;
                MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = fitting.Renderer.sharedMaterials;
                renderer.shadowCastingMode = fitting.Renderer.shadowCastingMode;
                renderer.receiveShadows = fitting.Renderer.receiveShadows;
                var block = new MaterialPropertyBlock();
                Material[] sharedMaterials = renderer.sharedMaterials;
                for (int i = 0; i < sharedMaterials.Length; i++)
                {
                    fitting.Renderer.GetPropertyBlock(block, i);
                    // Cached owner materials can change on a later hit. Freeze
                    // this separated fitting's current accent colors just as
                    // copying the old source property block did. Native ship
                    // shaders use _BaseColor as a texture, so check its type.
                    Material material = sharedMaterials[i];
                    if (block.isEmpty && material != null && material.shader != null && !NavalMaterials.UsesNativeDamage(material))
                        foreach (string property in new[] { "_Color", "_BaseColor" })
                        {
                            int index = material.shader.FindPropertyIndex(property);
                            if (index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Color)
                                block.SetColor(property, material.GetColor(property));
                        }
                    // The structural callback precedes the extra native-material
                    // slot updates. Detached pieces need the current hit state,
                    // including deck slots, rather than the previous event's paint.
                    if (NavalMaterials.UsesNativeDamage(sharedMaterials[i])) block.SetFloat("_HitPoints", hitPoints);
                    renderer.SetPropertyBlock(block, i);
                }
                obj.AddComponent<ResoluteVlsFragmentMesh>().OwnedMesh = fragment;
                Mesh replacement = Build(Enumerable.Range(0, cells.Length).Where(i => !separated[i]).SelectMany(i => cells[i]),
                    materials, "Resolute retained VLS cell hardware");
                fitting.Filter.sharedMesh = replacement;
                if (retained != null) Object.Destroy(retained);
                retained = replacement;
            }

            internal void Dispose() { if (retained != null) Object.Destroy(retained); }

            private static int Nearest(Cell[] cells, Vector2 point)
            {
                int best = 0; float distance = float.PositiveInfinity;
                for (int i = 0; i < cells.Length; i++)
                { float d = (cells[i].Position - point).sqrMagnitude; if (d < distance) { distance = d; best = i; } }
                return best;
            }

            private static int[] Candidates(Cell[] cells, Vector2 a, Vector2 b, Vector2 c)
            {
                Vector2 known = cells[Nearest(cells, (a + b + c) / 3f)].Position;
                float radiusSquared = Mathf.Max((known - a).sqrMagnitude, Mathf.Max((known - b).sqrMagnitude, (known - c).sqrMagnitude));
                Vector2 min = Vector2.Min(a, Vector2.Min(b, c)), max = Vector2.Max(a, Vector2.Max(b, c));
                var candidates = new List<int>();
                for (int i = 0; i < cells.Length; i++)
                {
                    Vector2 site = cells[i].Position;
                    float dx = Mathf.Max(min.x - site.x, Mathf.Max(0f, site.x - max.x));
                    float dz = Mathf.Max(min.y - site.y, Mathf.Max(0f, site.y - max.y));
                    // Throughout the triangle, the known site is at most the
                    // farthest corner distance away. A site farther from even
                    // its enclosing AABB cannot win any point of this triangle.
                    if (dx * dx + dz * dz <= radiusSquared + .0001f) candidates.Add(i);
                }
                return candidates.ToArray();
            }

            private static List<Vertex> Clip(List<Vertex> input, Vector2 normal, float offset)
            {
                var output = new List<Vertex>(input.Count + 1);
                for (int i = 0; i < input.Count; i++)
                {
                    Vertex a = input[i], b = input[(i + 1) % input.Count];
                    float da = Vector2.Dot(a.Projection, normal) - offset, db = Vector2.Dot(b.Projection, normal) - offset;
                    if (da <= 0f) output.Add(a);
                    if ((da < 0f && db > 0f) || (da > 0f && db < 0f)) output.Add(Vertex.Lerp(a, b, da / (da - db)));
                }
                return output;
            }

            private static void Add(List<Triangle> output, Vertex a, Vertex b, Vertex c, int material)
            {
                if (Vector3.Cross(b.P - a.P, c.P - a.P).sqrMagnitude > 1e-12f)
                    output.Add(new Triangle { A = a, B = b, C = c, Material = material });
            }

            private static Mesh Build(IEnumerable<Triangle> triangles, int materials, string name)
                => BuildAsync(triangles, materials, name, null).GetAwaiter().GetResult();

            private static async UniTask<Mesh> BuildAsync(IEnumerable<Triangle> triangles, int materials, string name, StartupLoadContext loading)
            {
                var p = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector4>();
                var uv = new List<Vector2>(); var uv1 = new List<Vector2>(); var colors = new List<Color>();
                var indices = Enumerable.Range(0, materials).Select(_ => new List<int>()).ToArray();
                int count = 0;
                foreach (Triangle triangle in triangles)
                {
                    if (loading != null && count++ % 64 == 0) await loading.Step("Building missile launch hardware");
                    foreach (Vertex v in new[] { triangle.A, triangle.B, triangle.C })
                    { indices[triangle.Material].Add(p.Count); p.Add(v.P); n.Add(v.N); t.Add(v.T); uv.Add(v.U); uv1.Add(v.U1); colors.Add(v.C); }
                }
                var mesh = new Mesh { name = name, indexFormat = p.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                    subMeshCount = materials };
                mesh.SetVertices(p); mesh.SetNormals(n); mesh.SetTangents(t); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv1); mesh.SetColors(colors);
                for (int i = 0; i < materials; i++) mesh.SetTriangles(indices[i], i, false);
                mesh.RecalculateBounds(); return mesh;
            }
        }
    }

    internal sealed class ResoluteVlsFragmentMesh : MonoBehaviour
    {
        public Mesh OwnedMesh;
        private void OnDestroy() { if (OwnedMesh != null) Object.Destroy(OwnedMesh); }
    }
}
