using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Native compartments keep their shell and load-bearing volume. A connected
    // surface atlas permits a few local tears; it does not replace a damaged
    // ship with hundreds of loose tiles over an all-purpose beam grid.
    internal static class StructuralGeometry
    {
        internal sealed class Cell
        {
            internal readonly string Name;
            internal readonly Bounds Bounds;
            internal Bounds[] Volumes;
            internal UnitPart Part;
            internal ResoluteStructuralSection Section;
            internal Bounds OccupiedBounds;
            internal Cell(string name, Vector3 min, Vector3 max)
            { Name = name; Bounds = new Bounds((min + max) * .5f, max - min); Volumes = new[] { Bounds }; }
        }

        internal sealed class Result
        {
            internal Cell[] Cells;
            internal Material Interior;
            internal readonly Dictionary<UnitPart, List<Renderer>> Renderers = new Dictionary<UnitPart, List<Renderer>>();
            internal readonly List<Renderer> LodRenderers = new List<Renderer>();
            internal int CappedSections;
            internal int CapTriangles;
            internal int CollisionHulls;
            internal int PlatingPieces;
            internal float SurfaceArea;
            internal float CoveredArea;
            internal string CacheSource;
            internal double GeometryMilliseconds;
            internal string GeometryKey;
            internal Section SupportShell;
            internal Section AuthoredInterior;
            internal Dictionary<long, List<DeckFace>> DeckFaces;
            internal readonly List<CompartmentTransfer> Transfers = new List<CompartmentTransfer>();
        }

        internal sealed class CompartmentTransfer
        {
            internal ShipPart[] Donors;
            internal ShipPart Added;
            internal float Fraction;
            internal Vector3[] OriginalForcePoints;
            internal float[] OriginalDisplacements;
        }

        private const float Far = 500f;
        private const float Epsilon = .0001f;
        private const string BakeVersion = "RSLF10-height-jagged-intact-mast-2026-09-16";
        internal const int ExpectedCompartmentCount = 23;
        internal const string BulwarkMeshName = "rsl_bulwarks";
        private static readonly Dictionary<string, BakedCell[]> GeometryCache = new Dictionary<string, BakedCell[]>();
        private static readonly Dictionary<string, Section> SourceSections = new Dictionary<string, Section>();
        private static readonly Dictionary<string, Mesh> FittingMeshes = new Dictionary<string, Mesh>();
        private static byte[] algorithmBuildIdentity;
        private static Section authoredInterior;
        private static byte[] authoredInteriorContent;
        internal static string LastCachePath { get; private set; }
        internal static string LastCacheSource { get; private set; }
        internal static double LastGeometryMilliseconds { get; private set; }
        internal static int LastRegionCount { get; private set; }
        internal static int LastCollisionGroupCount { get; private set; }
        internal static string CurrentBakeVersion => BakeVersion;

        private sealed class BakedCell
        {
            internal Mesh Exterior, Closed, Interior;
            internal SurfaceFracture.Result Fracture;
            internal Mesh[] Collision, CollisionGroups;
            internal int[] CollisionRegionPieces, CollisionRegionGroups;
        }

        private static Cell C(string name, float x0, float x1, float y0, float y1, float z0, float z1)
        { return new Cell(name, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1)); }

        private static Cell[] Layout()
        {
            // Coordinates are the source vessel's metres, with Y=0 at waterline.
            // Broad lower sections carry their own upper compartments as children.
            Cell[] cells = new[]
            {
                C("Destroyer1", -6, 6, -Far, 9.7f, -24, 24),
                C("Hull_L", -Far, -6, -Far, 9.7f, -24, 24),
                C("Hull_R", 6, Far, -Far, 9.7f, -24, 2.7f),
                C("Hull_CF", -6, 6, -Far, 9.7f, 24, 55),
                C("Hull_FL", -Far, -6, -Far, 9.7f, 24, 55),
                C("Hull_FR", 6, Far, -Far, 9.7f, 24, 55),
                C("Hull_CFF", -Far, Far, -Far, Far, 55, 85),
                C("Hull_CFFF", -Far, Far, -Far, Far, 85, Far),
                C("Hull_CR", -Far, Far, -Far, 5.526f, -42.3f, -24),
                C("Hull_hangarFloor", -Far, Far, -Far, 5.526f, -98, -58),
                C("Hull_RearL", -Far, 0, -Far, 5.526f, -Far, -98),
                C("Hull_RearR", 0, Far, -Far, 5.526f, -Far, -98),
                C("Hull_hangarL", -Far, 0, 5.526f, 12, -Far, -58),
                C("Hull_hangarR", 0, Far, 5.526f, 12, -Far, -58),
                C("Hull_hangarRoof", -Far, 2.2f, 12, Far, -Far, -58),
                C("Hull_CruiseMissileBattery3", -Far, Far, 5.526f, Far, -42.3f, -24),
                // Crosswise bays keep the tower's port and starboard faces
                // together. A centreline column cut left half a mast/bridge
                // standing after damage. Retain the same four native parts
                // and complementary jagged seams without that lengthwise cut.
                C("Hull_ExhaustStack", -Far, Far, 9.7f, Far, -24, -2.7f),
                C("Hull_Bridge", -Far, Far, 9.7f, Far, 20f, 32.4f),
                C("Hull_Radar", -Far, Far, 9.7f, Far, -2.7f, 7.8f),
                C("Hull_RFore", 6, Far, -Far, 9.7f, 2.7f, 24),
                // This transverse split crosses both former aft deck levels.
                C("Hull_CRAft", -Far, Far, -Far, Far, -58, -42.3f),
                C("Hull_UpperStarboard", -Far, Far, 9.7f, Far, 7.8f, 20f),
                C("Hull_hangarRoofStarboard", 2.2f, Far, 12, Far, -Far, -58)
            };
            // The actual forward superstructure ends before z=32.4m. The two
            // independent CIWS stools begin at z=32.837m; the VLS lies farther
            // forward. Their raised plinths belong to the deck beneath them.
            // A second nonoverlapping volume preserves the existing lower hull
            // seams while removing the old all-width superstructure ownership.
            foreach (Cell cell in cells.Where(c => c.Name == "Hull_CF" || c.Name == "Hull_FL" || c.Name == "Hull_FR"))
            {
                Bounds raisedDeck = new Bounds();
                raisedDeck.SetMinMax(new Vector3(cell.Bounds.min.x, 9.7f, 32.4f),
                    new Vector3(cell.Bounds.max.x, 18.7f, 55f));
                cell.Volumes = new[] { cell.Bounds, raisedDeck };
            }
            // Preserve the old upper-envelope coverage beyond the protected
            // foredeck boundary without assigning its raised gun/VLS stools
            // to the tower columns.
            foreach (Cell cell in cells.Where(c => c.Name == "Hull_Bridge"))
            {
                Bounds upperFore = new Bounds();
                upperFore.SetMinMax(new Vector3(cell.Bounds.min.x, 18.7f, 32.4f),
                    new Vector3(cell.Bounds.max.x, Far, 55f));
                cell.Volumes = new[] { cell.Bounds, upperFore };
            }
            // Bend the radar/forward-tower break around the narrow mast base.
            // Its complete authored skin above 24 m maps above Y=21 and behind
            // Z=15. Retain both native compartments below this shoulder; the
            // front bridge bay is unchanged. This reroutes their shared break
            // instead of merging the lower blue/purple superstructure bays.
            foreach (Cell cell in cells.Where(c => c.Name == "Hull_Radar" || c.Name == "Hull_UpperStarboard"))
            {
                bool radar = cell.Name == "Hull_Radar";
                Bounds lower = new Bounds(), upper = new Bounds();
                lower.SetMinMax(cell.Bounds.min, new Vector3(cell.Bounds.max.x, 21f, cell.Bounds.max.z));
                upper.SetMinMax(new Vector3(cell.Bounds.min.x, 21f, radar ? -2.7f : 15f),
                    new Vector3(cell.Bounds.max.x, Far, radar ? 15f : 20f));
                cell.Volumes = new[] { lower, upper };
            }
            if (cells.Length != ExpectedCompartmentCount) throw new InvalidOperationException("Unexpected structural partition count.");
            return cells;
        }

        internal static Result Begin(Ship ship, Transform source, MeshRenderer hullRenderer)
        {
            var result = new Result { Cells = Layout() };
            var parts = ship.GetComponentsInChildren<ShipPart>(true).ToDictionary(p => p.name);
            AddCompartment(result, ship, parts, "Hull_RFore", new[] { "Hull_R" }, (24f - 2.7f) / 48f);
            AddCompartment(result, ship, parts, "Hull_CRAft", new[] { "Hull_CR", "Hull_CruiseMissileBattery3" }, (58f - 42.3f) / 34f);
            AddCompartment(result, ship, parts, "Hull_UpperStarboard", new[] { "Hull_ExhaustStack" }, .5f);
            AddCompartment(result, ship, parts, "Hull_hangarRoofStarboard", new[] { "Hull_hangarRoof" }, .42f);
            foreach (Cell cell in result.Cells)
                cell.Part = cell.Name == "Destroyer1" ? ship.GetComponent<ShipPart>() : parts[cell.Name];

            // Crosswise tower bays each join the central supporting hull. Keep
            // their original names and every bound sensor/weapon world pose.
            foreach (string name in new[] { "Hull_ExhaustStack", "Hull_Bridge", "Hull_Radar", "Hull_UpperStarboard", "Hull_RFore" })
                parts[name].transform.SetParent(ship.transform, true);
            parts["Hull_CRAft"].transform.SetParent(parts["Hull_CR"].transform, true);
            parts["Hull_hangarFloor"].transform.SetParent(parts["Hull_CRAft"].transform, true);
            parts["Hull_hangarRoof"].transform.SetParent(parts["Hull_hangarFloor"].transform, true);
            parts["Hull_hangarRoofStarboard"].transform.SetParent(parts["Hull_hangarFloor"].transform, true);

            var hydro = ship.gameObject.AddComponent<ResoluteHydrostatics>();
            hydro.Parts = parts.Values.ToArray();
            hydro.Heights = hydro.Parts.Select(p => OriginalHeight(p.GetComponent<Collider>())).ToArray();

            result.Interior = NavalMaterials.CreateInterior(hullRenderer.sharedMaterial);
            result.SupportShell = ReadCached(hullRenderer.GetComponent<MeshFilter>(), source);
            result.DeckFaces = IndexDeck(result.SupportShell);
            result.AuthoredInterior = authoredInterior ?? (authoredInterior = ReadAuthoredInterior());
            return result;
        }

        private static FieldInfo NativeField(string name)
        {
            for (Type type = typeof(ShipPart); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(typeof(ShipPart).FullName, name);
        }

        private static void AddCompartment(Result result, Ship ship, Dictionary<string, ShipPart> parts,
            string name, string[] donorNames, float fraction)
        {
            if (ship.gameObject.activeInHierarchy) throw new InvalidOperationException("New compartments require inactive prefab staging.");
            ShipPart[] donors = donorNames.Select(n => parts[n]).ToArray(); ShipPart donor = donors[0];
            var obj = new GameObject(name) { layer = donor.gameObject.layer };
            obj.transform.SetParent(donor.transform.parent, false);
            obj.transform.SetPositionAndRotation(donor.transform.position, donor.transform.rotation);
            obj.transform.localScale = donor.transform.localScale;
            MeshCollider originalCollider = donor.GetComponent<MeshCollider>();
            if (originalCollider == null || originalCollider.sharedMesh == null) throw new InvalidOperationException("Missing compartment donor collision: " + donor.name);
            MeshCollider collider = obj.AddComponent<MeshCollider>();
            collider.sharedMesh = originalCollider.sharedMesh; collider.convex = true;
            collider.sharedMaterial = originalCollider.sharedMaterial; collider.contactOffset = originalCollider.contactOffset;
            ShipPart added = obj.AddComponent<ShipPart>(); added.parentUnit = ship;
            // Copy configuration only. Native Awake supplies fresh registration,
            // attachment state, jobs, hit points, event handlers and rigidbody.
            foreach (string field in new[] { "leakThreshold", "sinkThreshold", "breakJointStrength", "directionalDrag", "compartmentalized",
                "criticalPart", "structuralThreshold", "integrityThreshold", "hitSound", "sinkEffect" })
                NativeField(field).SetValue(added, NativeField(field).GetValue(donor));
            var armor = new ArmorProperties();
            foreach (FieldInfo field in typeof(ArmorProperties).GetFields(BindingFlags.Instance | BindingFlags.Public))
                field.SetValue(armor, donors.Max(p => (float)field.GetValue(NativeField("armorProperties").GetValue(p))));
            NativeField("armorProperties").SetValue(added, armor);
            // A new section spanning formerly separate decks uses the stronger
            // resistance of its donors, never a weaker threshold or tolerance.
            NativeField("structuralThreshold").SetValue(added, donors.Min(p => (float)NativeField("structuralThreshold").GetValue(p)));
            NativeField("integrityThreshold").SetValue(added, donors.Min(p => (float)NativeField("integrityThreshold").GetValue(p)));
            NativeField("breakJointStrength").SetValue(added, donors.Max(p => (float)NativeField("breakJointStrength").GetValue(p)));
            ImpactDamage impact = (ImpactDamage)NativeField("impactDamage").GetValue(donor);
            NativeField("impactDamage").SetValue(added, new ImpactDamage { threshold = impact.threshold, multiplier = impact.multiplier });
            NativeField("damageMaterial").SetValue(added, new DamageMaterial { indices = new byte[0] });
            NativeField("damageEffects").SetValue(added, ((List<DamageEffect>)NativeField("damageEffects").GetValue(donor))
                .Select(effect => new DamageEffect { prefab = effect.prefab, threshold = effect.threshold }).ToList());
            NativeField("disintegrationEffects").SetValue(added, ((GameObject[])NativeField("disintegrationEffects").GetValue(donor)).ToArray());
            NativeField("disintegrateObjects").SetValue(added, new GameObject[0]);
            NativeField("connectedCompartments").SetValue(added, new ShipPart[0]);
            var transfer = new CompartmentTransfer { Donors = donors, Added = added, Fraction = fraction,
                OriginalForcePoints = donors.Select(p => ((Transform)NativeField("forceTransform").GetValue(p) ?? p.transform).position).ToArray(),
                OriginalDisplacements = donors.Select(p => (float)NativeField("displacement").GetValue(p)).ToArray() };
            foreach (ShipPart sourcePart in donors)
            {
                float movedMass = sourcePart.mass * fraction; sourcePart.mass -= movedMass; added.mass += movedMass;
                foreach (string field in new[] { "displacement", "leakRateMin", "leakRateMax" })
                {
                    FieldInfo config = NativeField(field); float original = (float)config.GetValue(sourcePart), moved = original * fraction;
                    config.SetValue(sourcePart, original - moved); config.SetValue(added, (float)config.GetValue(added) + moved);
                }
            }
            result.Transfers.Add(transfer); parts.Add(name, added);
        }

        private static bool CellsTouch(Cell a, Cell b)
        { return a.Volumes.Any(left => b.Volumes.Any(right => Touches(left, right))); }

        private static bool OccupiedCellsTouch(Cell a, Cell b)
        {
            if (!CellsTouch(a, b)) return false;
            Bounds actual = a.OccupiedBounds; actual.Expand(.04f);
            return actual.Intersects(b.OccupiedBounds);
        }

        private static void FitCompartmentForcePoints(Result result)
        {
            foreach (CompartmentTransfer transfer in result.Transfers)
            {
                float total = transfer.OriginalDisplacements.Sum();
                if (total <= 0f) continue;
                Vector3 originalCenter = Vector3.zero;
                for (int i = 0; i < transfer.Donors.Length; i++)
                    originalCenter += transfer.OriginalForcePoints[i] * (transfer.OriginalDisplacements[i] / total);
                ShipPart[] group = transfer.Donors.Concat(new[] { transfer.Added }).ToArray();
                Vector3[] points = group.Select(part =>
                {
                    Cell cell = result.Cells.Single(c => c.Part == part);
                    return part.transform.TransformPoint(cell.Section.LocalBounds.center);
                }).ToArray();
                // Keep each existing vertical force coordinate. The new column
                // receives the transferred donors' weighted vertical coordinate.
                for (int i = 0; i < transfer.Donors.Length; i++) points[i].y = transfer.OriginalForcePoints[i].y;
                points[points.Length - 1].y = originalCenter.y;
                Vector3 weighted = Vector3.zero;
                for (int i = 0; i < group.Length; i++) weighted += points[i] * ((float)NativeField("displacement").GetValue(group[i]) / total);
                Vector3 correction = originalCenter - weighted;
                for (int i = 0; i < group.Length; i++)
                {
                    var force = new GameObject(group[i].name + "_fittedForce").transform;
                    force.SetParent(group[i].transform, false); force.position = points[i] + correction;
                    NativeField("forceTransform").SetValue(group[i], force);
                }
            }
        }

        private static float OriginalHeight(Collider collider)
        {
            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null)
            {
                Bounds b = mesh.sharedMesh.bounds;
                Vector3 size = Vector3.Scale(b.size, collider.transform.lossyScale);
                return Mathf.Abs(size.y) * .5f;
            }
            BoxCollider box = collider as BoxCollider;
            if (box != null) return Mathf.Abs(box.size.y * box.transform.lossyScale.y) * .5f;
            return Mathf.Max(.1f, collider.bounds.extents.y);
        }

        internal static UnitPart Owner(Result result, Vector3 sourcePosition)
        {
            // The cells tile all occupied source space. For ornaments just outside
            // it, the nearest cell remains deterministic and connected to a hull.
            Cell best = null;
            float distance = float.PositiveInfinity;
            Vector3 partitionPosition = TileAtSource(sourcePosition).Forward.MultiplyPoint3x4(sourcePosition);
            partitionPosition = StructuralHeightSeams.MapPoint(partitionPosition, true);
            foreach (Cell cell in result.Cells)
            {
                float d = cell.Volumes.Min(volume => volume.SqrDistance(partitionPosition));
                if (d < distance) { best = cell; distance = d; }
                if (d < .0000001f) break;
            }
            return best.Part;
        }

        internal static UnitPart DeckOwner(Result result, Vector3 sourcePosition)
        {
            // Evaluate the actual upward-facing shell; a turret pivot is not its
            // supporting surface, and broad bounding boxes overlap machinery.
            float best = float.NegativeInfinity;
            List<DeckFace> faces;
            if (!result.DeckFaces.TryGetValue(DeckKey(sourcePosition.x, sourcePosition.z), out faces)) faces = EmptyDeckFaces;
            foreach (DeckFace face in faces)
            {
                Vector3 a = face.A, e1 = face.E1, e2 = face.E2;
                float denominator = face.Denominator;
                float dx = sourcePosition.x - a.x, dz = sourcePosition.z - a.z;
                float u = (dx * e2.z - dz * e2.x) / denominator;
                float v = (e1.x * dz - e1.z * dx) / denominator;
                if (u < -.0001f || v < -.0001f || u + v > 1.0001f) continue;
                float y = a.y + u * e1.y + v * e2.y;
                // The railgun's authored yaw bearing sits 0.398m below its
                // deck/skirt seat. Evaluate that actual seat above the pivot.
                if (y <= sourcePosition.y + .75f && y > best) best = y;
            }
            if (float.IsNegativeInfinity(best) || sourcePosition.y - best > 14f)
                throw new InvalidOperationException("No evaluated deck surface beneath mount at " + sourcePosition);
            return Owner(result, new Vector3(sourcePosition.x, best - .04f, sourcePosition.z));
        }

        internal sealed class DeckFace { internal Vector3 A, E1, E2; internal float Denominator; }
        private static readonly List<DeckFace> EmptyDeckFaces = new List<DeckFace>();
        private static readonly Dictionary<Section, Dictionary<long, List<DeckFace>>> DeckIndexes = new Dictionary<Section, Dictionary<long, List<DeckFace>>>();
        private static long DeckKey(float x, float z) => ((long)Mathf.FloorToInt(x / 16f) << 32) | (uint)Mathf.FloorToInt(z / 16f);

        private static Dictionary<long, List<DeckFace>> IndexDeck(Section shell)
        {
            Dictionary<long, List<DeckFace>> cached;
            if (DeckIndexes.TryGetValue(shell, out cached)) return cached;
            var index = new Dictionary<long, List<DeckFace>>();
            foreach (Triangle triangle in shell.Triangles)
            {
                Vector3 a = triangle.A.P, b = triangle.B.P, c = triangle.C.P;
                Vector3 e1 = b - a, e2 = c - a;
                if (Vector3.Cross(e1, e2).normalized.y < .45f) continue;
                float denominator = e1.x * e2.z - e1.z * e2.x;
                if (Mathf.Abs(denominator) < 1e-8f) continue;
                var face = new DeckFace { A = a, E1 = e1, E2 = e2, Denominator = denominator };
                Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
                // Include the existing barycentric tolerance in the broad phase.
                float pad = .00031f * Mathf.Max(1f, Mathf.Max(e1.magnitude, e2.magnitude));
                for (int x = Mathf.FloorToInt((lo.x - pad) / 16f); x <= Mathf.FloorToInt((hi.x + pad) / 16f); x++)
                for (int z = Mathf.FloorToInt((lo.z - pad) / 16f); z <= Mathf.FloorToInt((hi.z + pad) / 16f); z++)
                {
                    long key = ((long)x << 32) | (uint)z; List<DeckFace> bucket;
                    if (!index.TryGetValue(key, out bucket)) index.Add(key, bucket = new List<DeckFace>());
                    bucket.Add(face);
                }
            }
            DeckIndexes.Add(shell, index); return index;
        }

        internal static void Add(Result result, UnitPart part, Renderer renderer)
        {
            List<Renderer> renderers;
            if (!result.Renderers.TryGetValue(part, out renderers)) result.Renderers.Add(part, renderers = new List<Renderer>());
            renderers.Add(renderer);
            result.LodRenderers.Add(renderer);
        }

        internal static void BuildHull(Result result, Ship ship, Transform source, MeshRenderer renderer, ManualLogSource log = null)
            => BuildHullAsync(result, ship, source, renderer, log, null).GetAwaiter().GetResult();

        internal static async UniTask BuildHullAsync(Result result, Ship ship, Transform source, MeshRenderer renderer,
            ManualLogSource log = null, StartupLoadContext loading = null)
        {
            if (loading != null) await loading.Step("Preparing hull structure");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var input = ReadCached(renderer.GetComponent<MeshFilter>(), source);
            // The corrected edges are welded into rsl_hull and continue its
            // existing planes. A separate old wall would duplicate that shell
            // and reintroduce the inset ledge and blocked rear working deck.
            if (source.GetComponentsInChildren<MeshRenderer>(true).Any(candidate => candidate.name == BulwarkMeshName))
                throw new InvalidOperationException("Legacy separate bulwarks are present. Resolute 0.6.1 requires the matching integrated hull assets.");
            string signature = GeometrySignature(input, result.Cells, source);
            result.GeometryKey = signature;
            BakedCell[] records;
            string cacheSource;
            if (GeometryCache.TryGetValue(signature, out records)) cacheSource = "memory";
            else if (!Environment.GetCommandLineArgs().Contains("--resolute-regenerate-fracture") &&
                (records = await ReadBakeAsync(signature, result.Cells.Length, loading)) != null) cacheSource = "disk";
            else { records = new BakedCell[result.Cells.Length]; cacheSource = "generated"; }
            bool published = cacheSource == "memory";
            try
            {
                log?.LogInfo("Structure geometry: " + cacheSource + "; key=" + signature + ".");
                for (int index = 0; index < result.Cells.Length; index++)
                {
                    Cell cell = result.Cells[index];
                    if (loading != null) await loading.Step("Hull section " + (index + 1) + "/" + result.Cells.Length);
                    BakedCell record = records[index];
                    if (record == null)
                    {
                        log?.LogInfo("Preparing structure " + (index + 1) + "/" + result.Cells.Length + ": " + cell.Name + ".");
                        records[index] = record = await BakeCellAsync(input, result.AuthoredInterior, cell, source, index, loading);
                    }
                    Matrix4x4 paintFrame = source.worldToLocalMatrix * cell.Part.transform.localToWorldMatrix;
                    Mesh styledExterior = SurfacePaintStyle.Create(record.Exterior, paintFrame, Vector3.zero, "intactHull");
                    MeshRenderer output = MakeRenderer(renderer, styledExterior, cell.Part.transform, renderer.name + "_" + cell.Name,
                        new[] { NavalMaterials.NativePlating, renderer.sharedMaterial, result.Interior, NavalMaterials.NativeDeck });
                    Add(result, cell.Part, output);
                    cell.Section = cell.Part.gameObject.AddComponent<ResoluteStructuralSection>();
                    cell.Section.Part = cell.Part;
                    cell.Section.Body = output;
                    cell.Section.ClosedMesh = record.Closed;
                    cell.Section.LocalBounds = record.Closed.bounds;
                    Bounds localBounds = record.Closed.bounds;
                    cell.OccupiedBounds = BoundsOf(Enumerable.Range(0, 8).Select(corner => paintFrame.MultiplyPoint3x4(
                        new Vector3((corner & 1) == 0 ? localBounds.min.x : localBounds.max.x,
                            (corner & 2) == 0 ? localBounds.min.y : localBounds.max.y,
                            (corner & 4) == 0 ? localBounds.min.z : localBounds.max.z))));
                    cell.Section.Interior = result.Interior;
                    cell.Section.CacheSource = cacheSource;
                    SurfaceFracture.Result fracture = record.Fracture;
                    cell.Section.SetFracture(fracture);
                    cell.Section.StyledSolidMeshes = new Mesh[fracture.Pieces.Length];
                    for (int piece = 0; piece < fracture.Pieces.Length; piece++)
                    {
                        if (loading != null) await loading.Step("Preparing damaged hull appearance");
                        SurfaceFracture.Piece p = fracture.Pieces[piece];
                        cell.Section.StyledSolidMeshes[piece] = SurfacePaintStyle.Create(p.Solid, paintFrame, p.Center, "solidGib", record.Exterior);
                    }
                    cell.Section.StyledRetainedDetail = fracture.RetainedDetail != null ? SurfacePaintStyle.Create(fracture.RetainedDetail.Solid,
                        paintFrame, fracture.RetainedDetail.Center, "retainedDetail", record.Exterior) : null;
                    var groupColliders = new List<MeshCollider>();
                    Collider[] oldColliders = cell.Part.GetComponents<Collider>();
                    MeshCollider primary = oldColliders.OfType<MeshCollider>().FirstOrDefault();
                    if (primary == null) throw new InvalidOperationException("Expected a native mesh collider: " + cell.Name);
                    foreach (Collider collider in oldColliders) if (collider != primary) collider.enabled = false;
                    for (int i = 0; i < record.CollisionGroups.Length; i++)
                    {
                        if (loading != null) await loading.Step("Fitting solid hull sections");
                        MeshCollider collider = i == 0 ? primary : cell.Part.gameObject.AddComponent<MeshCollider>();
                        collider.sharedMesh = record.CollisionGroups[i];
                        collider.convex = true;
                        collider.enabled = true;
                        collider.contactOffset = .015f;
                        groupColliders.Add(collider);
                    }
                    // Region meshes are immutable data, not inactive GameObjects.
                    // A healthy ship has one exterior renderer per major section.
                    cell.Section.PlateRenderers = new Renderer[0];
                    cell.Section.PlateColliders = new MeshCollider[fracture.Pieces.Length];
                    cell.Section.GroupColliders = groupColliders.ToArray();
                    cell.Section.CollisionMeshes = record.Collision;
                    cell.Section.PieceLocalCollisionMeshes = new Mesh[record.Collision.Length];
                    for (int region = 0; region < record.Collision.Length; region++)
                    {
                        if (loading != null) await loading.Step("Preparing damaged hull sections");
                        cell.Section.PieceLocalCollisionMeshes[region] = record.CollisionRegionPieces[region] < 0 ? null :
                            SurfacePaintStyle.PieceLocalCollision(record.Collision[region], fracture.Pieces[record.CollisionRegionPieces[region]].Center);
                    }
                    cell.Section.CollisionRegionPieces = record.CollisionRegionPieces;
                    cell.Section.CollisionRegionGroups = record.CollisionRegionGroups;
                    result.PlatingPieces += fracture.Pieces.Length;
                    result.CollisionHulls += record.CollisionGroups.Length;
                    result.SurfaceArea += fracture.SourceArea;
                    result.CoveredArea += fracture.CoveredArea;
                    if (record.Interior.vertexCount > 0)
                    {
                        MeshRenderer face = MakeRenderer(renderer, record.Interior, cell.Part.transform,
                            "ResoluteInterior_" + cell.Name, new[] { result.Interior });
                        // Like Dynamo's second material submesh, the real interior
                        // already exists behind the shell when the first small
                        // shader openings appear; it does not wait for 35 hitpoints.
                        face.enabled = true;
                        Add(result, cell.Part, face);
                        cell.Section.InteriorFaces = new Renderer[] { face };
                    }
                    else cell.Section.InteriorFaces = new Renderer[0];
                    result.CappedSections++;
                    result.CapTriangles += record.Interior.triangles.Length / 3;
                }
                foreach (Cell cell in result.Cells)
                {
                    if (loading != null) await loading.Step("Connecting hull compartments");
                    Cell[] neighbors = result.Cells.Where(other => other != cell && OccupiedCellsTouch(cell, other)).ToArray();
                    cell.Section.NeighborFaces = neighbors.SelectMany(other => other.Section.InteriorFaces).ToArray();
                    // Empty logical upper-envelope extensions must not create
                    // remote flooding links to the bow or other distant sections.
                    NativeField("connectedCompartments").SetValue(cell.Part, neighbors.Select(other => (ShipPart)other.Part).ToArray());
                }
                FitCompartmentForcePoints(result);
                // These immutable meshes are shared by both outfit templates and
                // live clones. They must outlive scene unloads just like the source
                // mesh catalog; per-instance destruction must never dispose them.
                foreach (Mesh mesh in BakeMeshes(records)) if (mesh != null) Object.DontDestroyOnLoad(mesh);
                GeometryCache[signature] = records; published = true;
                if (cacheSource == "generated")
                {
                    try
                    {
                        string path = PersistentBakePath(signature);
                        await WriteBakeAsync(path, signature, records, loading);
                        LastCachePath = path;
                        log?.LogInfo("Saved reusable structure geometry: " + path + ".");
                    }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
                    {
                        log?.LogWarning("Structure geometry is usable, but its cache could not be saved: " + exception.Message);
                    }
                }
                await WriteRequestedBakeAsync(signature, records, loading);
                renderer.enabled = false;
                clock.Stop(); result.CacheSource = cacheSource; result.GeometryMilliseconds = clock.Elapsed.TotalMilliseconds;
                LastCacheSource = cacheSource; LastGeometryMilliseconds = result.GeometryMilliseconds; LastRegionCount = result.PlatingPieces;
                LastCollisionGroupCount = result.CollisionHulls;
            }
            finally
            {
                // Canceled construction cannot publish a partial cell array.
                // Complete memory-cache records belong to all outfit templates.
                if (!published) DestroyBakeMeshes(records);
            }
        }

        private static BakedCell BakeCell(Section input, Section authoredInterior, Cell cell, Transform source, int index)
            => BakeCellAsync(input, authoredInterior, cell, source, index, null).GetAwaiter().GetResult();

        private static async UniTask<BakedCell> BakeCellAsync(Section input, Section authoredInterior, Cell cell, Transform source,
            int index, StartupLoadContext loading)
        {
            BakedCell record = null;
            try
            {
                Section section = ClipCell(input, cell, true);
                if (section.Triangles.Count == 0) throw new InvalidOperationException("Empty source structural cell: " + cell.Name);
                var exterior = new Section(); exterior.Triangles.AddRange(section.Triangles.Where(t => !t.Interior));
                if (loading != null) await loading.Step("Preparing inner hull structure");
                Section core = ClipCell(authoredInterior, cell, false);
                record = new BakedCell();
                if (loading != null) await loading.Step("Building hull shape");
                record.Closed = ToMesh(section, source, cell.Part.transform, "Resolute structural envelope " + cell.Name);
                if (loading != null) await loading.Step("Building hull exterior");
                record.Exterior = ToMesh(exterior, source, cell.Part.transform, "Resolute retained exterior " + cell.Name);
                if (loading != null) await loading.Step("Building inner steel");
                record.Interior = ToMesh(core, source, cell.Part.transform, "Resolute layered inner steel " + cell.Name);
                if (loading != null) await loading.Step("Preparing damaged hull");
                record.Fracture = SurfaceFracture.Generate(record.Exterior, new SurfaceFracture.Settings
                { TargetArea = 450f, Thickness = .12f, MaximumDiameter = 52f, MaximumPlaneDeviation = 0f, MaximumConcavity = 0f,
                    LongitudinalMetric = .30f, MinimumFragmentArea = 20f,
                    MaximumPieces = 192, SizeVariation = .5f, CreasePenalty = 2f, Seed = 7139 + index * 127 });
                if (loading != null) await loading.Step("Preparing solid hull sections");
                SurfaceFracture.CollisionLayout collision = SurfaceFracture.GroupCollision(record.Fracture);
                record.Collision = new Mesh[collision.Regions.Length];
                for (int i = 0; i < collision.Regions.Length; i++)
                {
                    if (loading != null) await loading.Step("Fitting damaged hull sections");
                    record.Collision[i] = ConvexHull(collision.Regions[i].Points, "Resolute precise shell collision " + cell.Name + " " + i);
                }
                record.CollisionGroups = new Mesh[collision.Groups.Length];
                for (int i = 0; i < collision.Groups.Length; i++)
                {
                    if (loading != null) await loading.Step("Fitting intact hull sections");
                    record.CollisionGroups[i] = ConvexHull(collision.Groups[i].Points, "Resolute intact collision group " + cell.Name + " " + i);
                }
                record.CollisionRegionPieces = collision.Regions.Select(region => region.PieceIndex).ToArray();
                record.CollisionRegionGroups = new int[collision.Regions.Length];
                for (int i = 0; i < collision.Groups.Length; i++) foreach (int region in collision.Groups[i].Regions) record.CollisionRegionGroups[region] = i;
                return record;
            }
            catch
            {
                if (record != null) DestroyBakeMeshes(new[] { record });
                throw;
            }
        }

        private static bool Touches(Bounds a, Bounds b)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                if (Mathf.Abs(a.min[axis] - b.max[axis]) > .002f && Mathf.Abs(a.max[axis] - b.min[axis]) > .002f) continue;
                bool overlaps = true;
                for (int j = 0; j < 3; j++) if (j != axis && Mathf.Min(a.max[j], b.max[j]) - Mathf.Max(a.min[j], b.min[j]) < .001f) overlaps = false;
                if (overlaps) return true;
            }
            return false;
        }

        private static string GeometrySignature(Section input, Cell[] cells, Transform source)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(BakeVersion);
                writer.Write(authoredInteriorContent);
                // Raw method IL contains metadata-token offsets, so unrelated
                // compilation edits could invalidate the old opaque signature.
                // Key explicitly to the complete installed build instead. The
                // conservative invalidation is intentional and stable between
                // launches of that exact binary, with no reflection walk.
                if (algorithmBuildIdentity == null)
                    using (var hash = SHA256.Create())
                        algorithmBuildIdentity = hash.ComputeHash(File.ReadAllBytes(typeof(StructuralGeometry).Assembly.Location));
                writer.Write(algorithmBuildIdentity);
                foreach (Triangle triangle in input.Triangles)
                foreach (Vertex vertex in new[] { triangle.A, triangle.B, triangle.C })
                {
                    WriteVector(writer, vertex.P); WriteVector(writer, vertex.N);
                    writer.Write(vertex.Uv.x); writer.Write(vertex.Uv.y);
                }
                foreach (Cell cell in cells)
                {
                    writer.Write(cell.Name); writer.Write(cell.Volumes.Length);
                    foreach (Bounds volume in cell.Volumes) { WriteVector(writer, volume.min); WriteVector(writer, volume.max); }
                    Matrix4x4 matrix = cell.Part.transform.worldToLocalMatrix * source.localToWorldMatrix;
                    for (int i = 0; i < 16; i++) writer.Write(matrix[i]);
                }
                writer.Flush();
                using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string PersistentBakePath(string signature)
        { return Path.Combine(BepInEx.Paths.CachePath, "Resolute", signature + ".bin"); }

        private static IEnumerable<Mesh> BakeMeshes(IEnumerable<BakedCell> records)
        {
            foreach (BakedCell record in records)
            {
                if (record == null) continue;
                yield return record.Exterior; yield return record.Closed; yield return record.Interior;
                if (record.Fracture != null)
                {
                    foreach (SurfaceFracture.Piece piece in record.Fracture.Pieces) { yield return piece.Surface; yield return piece.Solid; }
                    if (record.Fracture.RetainedDetail != null) { yield return record.Fracture.RetainedDetail.Surface; yield return record.Fracture.RetainedDetail.Solid; }
                }
                if (record.Collision != null) foreach (Mesh mesh in record.Collision) yield return mesh;
                if (record.CollisionGroups != null) foreach (Mesh mesh in record.CollisionGroups) yield return mesh;
            }
        }

        private static void DestroyBakeMeshes(IEnumerable<BakedCell> records)
        {
            foreach (Mesh mesh in BakeMeshes(records).Where(mesh => mesh != null).Distinct()) Object.DestroyImmediate(mesh);
        }

        private static BakedCell[] ReadBake(string signature, int expectedCells)
            => ReadBakeAsync(signature, expectedCells, null).GetAwaiter().GetResult();

        private static async UniTask<BakedCell[]> ReadBakeAsync(string signature, int expectedCells, StartupLoadContext loading)
        {
            string shipped = string.IsNullOrEmpty(VisualLoader.ShipAssetFolder) ? null : Path.Combine(VisualLoader.ShipAssetFolder, "fracture_v4.bin");
            foreach (string path in new[] { PersistentBakePath(signature), shipped })
            {
                if (path == null || !File.Exists(path)) continue;
                var loadedMeshes = new List<Mesh>();
                bool accepted = false;
                try
                {
                    BakedCell[] records = await ReadBakeFileAsync(path, signature, expectedCells, loadedMeshes, loading);
                    if (records == null) continue;
                    accepted = true; LastCachePath = path; return records;
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                    exception is InvalidDataException || exception is ArgumentException || exception is OverflowException || exception is System.Security.SecurityException)
                {
                    Debug.LogWarning("[Resolute] Ignoring unusable structure cache " + path + ": " + exception.Message);
                }
                finally
                {
                    if (!accepted) foreach (Mesh mesh in loadedMeshes) if (mesh != null) Object.DestroyImmediate(mesh);
                }
            }
            return null;
        }

        private static BakedCell[] ReadBakeFile(string path, string signature, int expectedCells, List<Mesh> loadedMeshes)
            => ReadBakeFileAsync(path, signature, expectedCells, loadedMeshes, null).GetAwaiter().GetResult();

        private static async UniTask<BakedCell[]> ReadBakeFileAsync(string path, string signature, int expectedCells,
            List<Mesh> loadedMeshes, StartupLoadContext loading)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                if (stream.Length > 250000000) throw new InvalidDataException("The fracture bake exceeds the permitted size.");
                if (reader.ReadString() != BakeVersion || reader.ReadString() != signature) return null;
                long payloadLength = stream.Length - 32;
                long headerEnd = stream.Position;
                if (payloadLength < headerEnd) throw new InvalidDataException("Truncated structure cache.");
                byte[] checksum = await HashPrefixAsync(stream, payloadLength, loading);
                if (!reader.ReadBytes(32).SequenceEqual(checksum)) throw new InvalidDataException("Structure cache checksum mismatch.");
                stream.Position = headerEnd;
                int count = reader.ReadInt32();
                if (count != expectedCells) throw new InvalidDataException("The fracture bake has the wrong compartment count.");
                var output = new BakedCell[count];
                for (int i = 0; i < count; i++)
                {
                    if (loading != null) await loading.Step("Loading saved hull section " + (i + 1) + "/" + count);
                    var record = new BakedCell { Exterior = ReadMesh(reader, loadedMeshes), Closed = ReadMesh(reader, loadedMeshes), Interior = ReadMesh(reader, loadedMeshes) };
                    var fracture = new SurfaceFracture.Result { SourceArea = reader.ReadSingle(), CoveredArea = reader.ReadSingle(),
                        SourceTriangles = reader.ReadInt32(), OutputTriangles = reader.ReadInt32(), ConnectedComponents = reader.ReadInt32(), InteriorMaterialIndex = 1 };
                    int pieces = reader.ReadInt32();
                    if (pieces < 1 || pieces > 192) throw new InvalidDataException("Invalid fracture region count.");
                    fracture.Pieces = new SurfaceFracture.Piece[pieces];
                    for (int j = 0; j < pieces; j++)
                    {
                        if (loading != null) await loading.Step("Loading saved damaged hull");
                        fracture.Pieces[j] = new SurfaceFracture.Piece { Center = ReadVector(reader), Normal = ReadVector(reader),
                            Area = reader.ReadSingle(), Diameter = reader.ReadSingle(), Thickness = reader.ReadSingle(),
                            Surface = ReadMesh(reader, loadedMeshes), Solid = ReadMesh(reader, loadedMeshes) };
                    }
                    fracture.RetainedDetailComponents = reader.ReadInt32();
                    if (reader.ReadBoolean()) fracture.RetainedDetail = new SurfaceFracture.Piece {
                        Center = ReadVector(reader), Area = reader.ReadSingle(), Surface = ReadMesh(reader, loadedMeshes), Solid = ReadMesh(reader, loadedMeshes) };
                    int regions = reader.ReadInt32(), groups = reader.ReadInt32();
                    if (regions < pieces || regions > 12000 || groups < 1 || groups > regions) throw new InvalidDataException("Invalid collision layout count.");
                    record.Collision = new Mesh[regions]; record.CollisionGroups = new Mesh[groups];
                    record.CollisionRegionPieces = new int[regions]; record.CollisionRegionGroups = new int[regions];
                    for (int j = 0; j < regions; j++)
                    {
                        if (loading != null) await loading.Step("Loading saved hull structure");
                        record.CollisionRegionPieces[j] = reader.ReadInt32(); record.CollisionRegionGroups[j] = reader.ReadInt32();
                        if (record.CollisionRegionPieces[j] < -1 || record.CollisionRegionPieces[j] >= pieces ||
                            record.CollisionRegionGroups[j] < 0 || record.CollisionRegionGroups[j] >= groups) throw new InvalidDataException("Invalid collision region ownership.");
                        record.Collision[j] = ReadMesh(reader, loadedMeshes);
                    }
                    for (int j = 0; j < groups; j++)
                    {
                        if (loading != null) await loading.Step("Loading saved hull sections");
                        record.CollisionGroups[j] = ReadMesh(reader, loadedMeshes);
                    }
                    record.Fracture = fracture; output[i] = record;
                }
                if (stream.Position != payloadLength) throw new InvalidDataException("Unexpected trailing fracture-bake data.");
                return output;
            }
        }

        private static void WriteRequestedBake(string signature, BakedCell[] records)
            => WriteRequestedBakeAsync(signature, records, null).GetAwaiter().GetResult();

        private static async UniTask WriteRequestedBakeAsync(string signature, BakedCell[] records, StartupLoadContext loading)
        {
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.FindIndex(args, a => a == "--resolute-bake-fracture");
            if (flag < 0) return;
            if (flag + 1 >= args.Length) throw new ArgumentException("--resolute-bake-fracture needs an output filename.");
            await WriteBakeAsync(Path.GetFullPath(args[flag + 1]), signature, records, loading);
        }

        private static byte[] HashPrefix(Stream stream, long length)
            => HashPrefixAsync(stream, length, null).GetAwaiter().GetResult();

        private static async UniTask<byte[]> HashPrefixAsync(Stream stream, long length, StartupLoadContext loading)
        {
            stream.Position = 0;
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536];
                for (long remaining = length; remaining > 0;)
                {
                    if (loading != null) await loading.Step("Checking saved hull structure");
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException("Truncated structure cache.");
                    hash.TransformBlock(buffer, 0, read, buffer, 0); remaining -= read;
                }
                hash.TransformFinalBlock(new byte[0], 0, 0); return hash.Hash;
            }
        }

        private static void WriteBake(string path, string signature, BakedCell[] records)
            => WriteBakeAsync(path, signature, records, null).GetAwaiter().GetResult();

        private static async UniTask WriteBakeAsync(string path, string signature, BakedCell[] records, StartupLoadContext loading)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(BakeVersion); writer.Write(signature); writer.Write(records.Length);
                foreach (BakedCell record in records)
                {
                    if (loading != null) await loading.Step("Saving hull structure");
                    WriteMesh(writer, record.Exterior); WriteMesh(writer, record.Closed); WriteMesh(writer, record.Interior);
                    SurfaceFracture.Result fracture = record.Fracture;
                    writer.Write(fracture.SourceArea); writer.Write(fracture.CoveredArea); writer.Write(fracture.SourceTriangles);
                    writer.Write(fracture.OutputTriangles); writer.Write(fracture.ConnectedComponents); writer.Write(fracture.Pieces.Length);
                    for (int i = 0; i < fracture.Pieces.Length; i++)
                    {
                        if (loading != null) await loading.Step("Saving damaged hull");
                        SurfaceFracture.Piece piece = fracture.Pieces[i];
                        WriteVector(writer, piece.Center); WriteVector(writer, piece.Normal); writer.Write(piece.Area);
                        writer.Write(piece.Diameter); writer.Write(piece.Thickness);
                        WriteMesh(writer, piece.Surface); WriteMesh(writer, piece.Solid);
                    }
                    writer.Write(fracture.RetainedDetailComponents); writer.Write(fracture.RetainedDetail != null);
                    if (fracture.RetainedDetail != null)
                    {
                        WriteVector(writer, fracture.RetainedDetail.Center); writer.Write(fracture.RetainedDetail.Area);
                        WriteMesh(writer, fracture.RetainedDetail.Surface); WriteMesh(writer, fracture.RetainedDetail.Solid);
                    }
                    writer.Write(record.Collision.Length); writer.Write(record.CollisionGroups.Length);
                    for (int i = 0; i < record.Collision.Length; i++)
                    {
                        if (loading != null) await loading.Step("Saving hull fittings");
                        writer.Write(record.CollisionRegionPieces[i]); writer.Write(record.CollisionRegionGroups[i]); WriteMesh(writer, record.Collision[i]);
                    }
                    foreach (Mesh group in record.CollisionGroups)
                    {
                        if (loading != null) await loading.Step("Saving hull sections");
                        WriteMesh(writer, group);
                    }
                }
                writer.Flush();
                byte[] checksum = await HashPrefixAsync(stream, stream.Length, loading);
                writer.Write(checksum); writer.Flush(); stream.Flush(true);
            }
            // A crash leaves either the previous complete cache or a temporary
            // file. Readers never see a partially overwritten accepted file.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void WriteVector(BinaryWriter writer, Vector3 point)
        { writer.Write(point.x); writer.Write(point.y); writer.Write(point.z); }

        private static Vector3 ReadVector(BinaryReader reader)
        { return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }

        private static void WriteMesh(BinaryWriter writer, Mesh mesh)
        {
            writer.Write(mesh.name); writer.Write(mesh.vertexCount);
            Vector3[] positions = mesh.vertices, normals = mesh.normals;
            Vector2[] uv = mesh.uv, uv1 = mesh.uv2; Color32[] colors = mesh.colors32;
            for (int i = 0; i < positions.Length; i++)
            {
                WriteVector(writer, positions[i]); WriteVector(writer, normals.Length > i ? normals[i] : Vector3.up);
                Vector2 a = uv.Length > i ? uv[i] : Vector2.zero, b = uv1.Length > i ? uv1[i] : a;
                writer.Write(a.x); writer.Write(a.y); writer.Write(b.x); writer.Write(b.y);
                Color32 color = colors.Length > i ? colors[i] : new Color32(255, 255, 255, 255);
                writer.Write(color.r); writer.Write(color.g); writer.Write(color.b); writer.Write(color.a);
            }
            writer.Write(mesh.subMeshCount);
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                int[] indices = mesh.GetTriangles(i); writer.Write(indices.Length);
                foreach (int index in indices) writer.Write(index);
            }
        }

        private static Mesh ReadMesh(BinaryReader reader, List<Mesh> loadedMeshes)
        {
            string name = reader.ReadString(); int count = reader.ReadInt32();
            if (count < 0 || count > 750000) throw new InvalidDataException("Invalid baked mesh vertex count.");
            if (44L * count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated baked vertices.");
            var positions = new Vector3[count]; var normals = new Vector3[count];
            var uv = new Vector2[count]; var uv1 = new Vector2[count]; var colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                positions[i] = ReadVector(reader); normals[i] = ReadVector(reader);
                uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle()); uv1[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                colors[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                if (!Finite(positions[i]) || !Finite(normals[i]) || float.IsNaN(uv[i].x) || float.IsNaN(uv[i].y) ||
                    float.IsInfinity(uv[i].x) || float.IsInfinity(uv[i].y) || float.IsNaN(uv1[i].x) || float.IsNaN(uv1[i].y) ||
                    float.IsInfinity(uv1[i].x) || float.IsInfinity(uv1[i].y)) throw new InvalidDataException("Nonfinite baked vertex.");
            }
            int materials = reader.ReadInt32();
            if (materials < 1 || materials > 8) throw new InvalidDataException("Invalid baked material count.");
            var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                vertices = positions, normals = normals, uv = uv, uv2 = uv1, colors32 = colors, subMeshCount = materials };
            loadedMeshes.Add(mesh);
            for (int i = 0; i < materials; i++)
            {
                int length = reader.ReadInt32();
                if (length < 0 || length > 3000000 || length % 3 != 0) throw new InvalidDataException("Invalid baked triangle count.");
                if (4L * length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Truncated baked indices.");
                var indices = new int[length];
                for (int j = 0; j < length; j++)
                {
                    indices[j] = reader.ReadInt32();
                    if (indices[j] < 0 || indices[j] >= count) throw new InvalidDataException("Invalid baked vertex index.");
                }
                mesh.SetTriangles(indices, i);
            }
            mesh.RecalculateBounds(); if (count > 0) mesh.RecalculateTangents(); return mesh;
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
            !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);

        internal static void BuildFitting(Result result, Transform source, Renderer renderer)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Section input = ReadCached(filter, source);
            Bounds bounds = BoundsOf(input.Triangles.SelectMany(t => new[] { t.A.P, t.B.P, t.C.P }));
            // Every individually articulated object stays whole on one section.
            // Broad static batches are cut along the very same hull seams.
            bool vlsHatch = renderer.name.StartsWith("rsl_hatch_", StringComparison.Ordinal);
            bool articulated = vlsHatch || renderer.name.Contains("prop_") ||
                renderer.name.Contains("rudder") || renderer.name == "rsl_nav_radar" ||
                (renderer.name.StartsWith("rsl_raft61_", StringComparison.Ordinal) && renderer.name.Contains("gate")) ||
                renderer.name.Contains("door") || renderer.name.Contains("yaw") || renderer.name.Contains("pitch");
            // Whole, articulated fittings never need seam tessellation. The
            // 276 small hatch meshes previously paid that cost before this exit.
            if (articulated || bounds.size.magnitude < 12f)
            {
                AttachWholeFitting(result, source, renderer, bounds, vlsHatch); return;
            }
            // Ownership boxes live in partition coordinates. A source-space
            // AABB would miss fittings crossing an interlocking seam.
            Section partition = Partition(input);
            Bounds partitionBounds = BoundsOf(partition.Triangles.SelectMany(t => new[] { t.A.P, t.B.P, t.C.P }));
            Cell[] intersections = result.Cells.Where(c => c.Volumes.Any(volume => volume.Intersects(partitionBounds))).ToArray();
            if (intersections.Length <= 1)
            {
                AttachWholeFitting(result, source, renderer, bounds, vlsHatch); return;
            }
            int count = 0;
            foreach (Cell cell in intersections)
            {
                string key = result.GeometryKey + "/" + SourceKey(filter, source) + "/" + cell.Name;
                Mesh mesh;
                if (!FittingMeshes.TryGetValue(key, out mesh))
                {
                    Section section = ClipCell(input, cell, false);
                    if (section.Triangles.Count == 0) { FittingMeshes.Add(key, null); continue; }
                    mesh = ToMesh(section, source, cell.Part.transform, "Resolute fitting " + renderer.name + " / " + cell.Name);
                    Object.DontDestroyOnLoad(mesh); FittingMeshes.Add(key, mesh);
                }
                if (mesh == null) continue;
                MeshRenderer output = MakeRenderer(renderer, mesh, cell.Part.transform, renderer.name + "_" + cell.Name,
                    renderer.sharedMaterials);
                SurfacePaintStyle.ApplyRenderer(output, source, "fitting");
                Add(result, cell.Part, output);
                count++;
            }
            if (count == 0) throw new InvalidOperationException("No structural owner for " + renderer.name);
            renderer.enabled = false;
        }

        private static void AttachWholeFitting(Result result, Transform source, Renderer renderer, Bounds bounds, bool vlsHatch)
        {
            UnitPart owner = vlsHatch || renderer.name.Contains("foundation")
                ? DeckOwner(result, new Vector3(bounds.center.x, bounds.max.y, bounds.center.z)) : Owner(result, bounds.center);
            renderer.transform.SetParent(owner.transform, true);
            SurfacePaintStyle.ApplyRenderer(renderer, source, "fitting");
            Add(result, owner, renderer);
        }

        internal static Dictionary<UnitPart, List<Renderer>> PartitionDistant(Result result, Transform source, Renderer[] inputs)
            => PartitionDistantAsync(result, source, inputs, null).GetAwaiter().GetResult();

        internal static UnitPart LodSupport(UnitPart owner, HashSet<UnitPart> supports)
        {
            UnitPart current = owner;
            while (current != null && !supports.Contains(current))
            {
                Transform ancestor = current.transform.parent;
                current = ancestor != null ? ancestor.GetComponentInParent<UnitPart>(true) : null;
            }
            if (current == null) throw new InvalidOperationException("No structural LOD support for " + (owner != null ? owner.name : "unowned renderer"));
            return current;
        }

        internal static async UniTask<Dictionary<UnitPart, List<Renderer>>> PartitionDistantAsync(Result result, Transform source,
            Renderer[] inputs, StartupLoadContext loading = null)
        {
            var output = result.Cells.ToDictionary(cell => cell.Part, cell => new List<Renderer>());
            var supports = new HashSet<UnitPart>(output.Keys);
            foreach (Renderer renderer in inputs)
            {
                if (loading != null) await loading.Step("Preparing distant hull appearance");
                if (renderer.GetComponent<ResoluteAnimatedLod>() != null)
                {
                    UnitPart owner = renderer.GetComponentInParent<UnitPart>(true);
                    // A turret owns damage and motion; its supporting hull
                    // compartment owns the shared near/far screen threshold.
                    // Keep the renderer parent on the real animated pivot.
                    output[LodSupport(owner, supports)].Add(renderer);
                    continue;
                }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Section partition = null;
                foreach (Cell cell in result.Cells)
                {
                    if (loading != null) await loading.Step("Fitting distant hull sections");
                    string key = result.GeometryKey + "/distant/" + SourceKey(filter, source) + "/" + cell.Name;
                    Mesh mesh;
                    if (!FittingMeshes.TryGetValue(key, out mesh))
                    {
                        // Completed far meshes are cached below. The temporary
                        // triangle graph need not occupy SourceSections forever.
                        if (partition == null) partition = Partition(Read(filter, source, true));
                        var clipped = new Section();
                        foreach (Bounds volume in cell.Volumes) clipped.Triangles.AddRange(Clip(partition, volume, false).Triangles);
                        mesh = clipped.Triangles.Count == 0 ? null : ToMesh(MapSeams(clipped, false), source,
                            cell.Part.transform, "Resolute distant " + renderer.name + " / " + cell.Name, filter.sharedMesh.subMeshCount);
                        if (mesh != null) Object.DontDestroyOnLoad(mesh);
                        FittingMeshes.Add(key, mesh);
                    }
                    if (mesh == null) continue;
                    MeshRenderer copy = MakeRenderer(renderer, mesh, cell.Part.transform, renderer.name + "_" + cell.Name,
                        renderer.sharedMaterials);
                    SurfacePaintStyle.ApplyRenderer(copy, source, "distant");
                    output[cell.Part].Add(copy);
                }
                renderer.enabled = false;
            }
            return output;
        }

        private static string SourceKey(MeshFilter filter, Transform source)
        { return filter.sharedMesh.GetInstanceID() + "/" + (source.worldToLocalMatrix * filter.transform.localToWorldMatrix).ToString("R"); }

        private static Section ReadCached(MeshFilter filter, Transform source)
        {
            string key = SourceKey(filter, source); Section section;
            if (!SourceSections.TryGetValue(key, out section)) SourceSections.Add(key, section = Read(filter, source));
            return section;
        }

        internal static void Finish(Result result, Ship ship, HashSet<UnitPart> weaponParts, ManualLogSource log)
        {
            // Remove invisible Dynamo launcher boxes and secondary roof colliders.
            // They otherwise remain above the actual flight deck after reskinning.
            var structural = new HashSet<UnitPart>(result.Cells.Select(c => c.Part));
            Transform source = ship.transform.Find("ResoluteVisual");
            foreach (UnitPart weapon in weaponParts)
            {
                Vector3 supportPoint = source.InverseTransformPoint(weapon.transform.position);
                UnitPart support = DeckOwner(result, supportPoint);
                weapon.transform.SetParent(support.transform, true);
            }
            // All fixed VLS points and their cell art use the same evaluated
            // physical deck. Weapon collections may span several compartments.
            foreach (Transform anchor in ship.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.StartsWith("ResoluteLaunch_", StringComparison.Ordinal)))
                anchor.SetParent(DeckOwner(result, source.InverseTransformPoint(anchor.position)).transform, true);
            foreach (Collider collider in ship.GetComponentsInChildren<Collider>(true))
            {
                UnitPart part = collider.GetComponent<UnitPart>();
                if (part == null || (!structural.Contains(part) && !weaponParts.Contains(part))) collider.enabled = false;
            }
            ResoluteMassDistribution.Configure(ship, result.Cells);
            log.LogInfo("Source structure: " + result.CappedSections + " native compartments, " + result.PlatingPieces +
                " irregular removable plates, " + result.CollisionHulls + " thin fitted collision hulls, surface area " +
                result.CoveredArea.ToString("F2") + "/" + result.SurfaceArea.ToString("F2") + " m虏; native hydrostatic heights preserved.");
        }

        private static MeshRenderer MakeRenderer(Renderer original, Mesh mesh, Transform parent, string name, Material[] materials)
        {
            var obj = new GameObject(name) { layer = parent.gameObject.layer };
            obj.transform.SetParent(parent, false);
            obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer result = obj.AddComponent<MeshRenderer>();
            result.sharedMaterials = materials;
            result.shadowCastingMode = original.shadowCastingMode;
            result.receiveShadows = original.receiveShadows;
            return result;
        }

        internal struct Vertex
        {
            internal Vector3 P, N;
            internal Vector2 Uv, Uv1;
            internal Color32 Color;
            internal static Vertex Lerp(Vertex a, Vertex b, float t)
            { return new Vertex { P = Vector3.LerpUnclamped(a.P, b.P, t), N = Vector3.LerpUnclamped(a.N, b.N, t).normalized,
                Uv = Vector2.LerpUnclamped(a.Uv, b.Uv, t), Uv1 = Vector2.LerpUnclamped(a.Uv1, b.Uv1, t), Color = Color32.Lerp(a.Color, b.Color, t) }; }
        }

        internal sealed class Triangle
        {
            internal Vertex A, B, C;
            internal bool Interior;
            internal int MaterialSlot;
            internal Triangle(Vertex a, Vertex b, Vertex c, bool interior = false, int materialSlot = 0)
            { A = a; B = b; C = c; Interior = interior; MaterialSlot = materialSlot; }
        }

        internal sealed class Section
        {
            internal readonly List<Triangle> Triangles = new List<Triangle>();
            internal Section Partitioned;
        }

        private const float SeamStep = 6f, SeamOriginX = 1.7f, SeamOriginZ = 2.4f;
        private const float SeamShiftX = .65f, SeamShiftZ = .9f, SeamShiftY = 2.8f;

        private struct SeamKey : IEquatable<SeamKey>
        {
            internal int X, Z, Half;
            internal SeamKey(int x, int z, int half) { X = x; Z = z; Half = half; }
            public bool Equals(SeamKey other) { return X == other.X && Z == other.Z && Half == other.Half; }
            public override bool Equals(object other) { return other is SeamKey && Equals((SeamKey)other); }
            public override int GetHashCode() { unchecked { return (X * 397 ^ Z) * 397 ^ Half; } }
        }

        private sealed class SeamTile
        {
            internal SeamKey Key;
            internal Vector2[] Source, Mapped;
            internal Matrix4x4 Forward, Inverse;
        }

        private static readonly Dictionary<SeamKey, SeamTile> SeamTiles = new Dictionary<SeamKey, SeamTile>();

        private static float SeamNoise(int x, int z, uint salt)
        {
            unchecked
            {
                uint value = (uint)x * 0x9e3779b9u ^ (uint)z * 0x85ebca6bu ^ salt;
                value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15;
                value *= 0x846ca68bu; value ^= value >> 16;
                return (value & 0x00ffffffu) / 8388607.5f - 1f;
            }
        }

        private static Vector3 SeamNode(int x, int z)
        {
            float px = SeamOriginX + x * SeamStep, pz = SeamOriginZ + z * SeamStep;
            // Lateral displacement depends only on Z. Over either triangular
            // half-cell, dqX/dx=1, |dqX/dz|<=1.3/6, |dqZ/dx|<=1.8/6,
            // and dqZ/dz>=1-1.8/6. Thus the XZ determinant stays above .635.
            // The continuous map is a bounded perturbation of the plane with
            // no flipped triangles. Y is a pure shear: dqY/dy=1. This gives
            // complementary physical cuts without folding the source space.
            float forwardProtection = Mathf.Clamp01((Mathf.Abs(pz - 32.4f) - SeamStep) / SeamStep);
            return new Vector3(px + SeamShiftX * SeamNoise(0, z, 0x714bd59du),
                SeamShiftY * SeamNoise(x, z, 0xc2539b37u),
                pz + SeamShiftZ * SeamNoise(x, z, 0x39a71fe5u) * forwardProtection);
        }

        private static SeamTile Tile(int x, int z, int half)
        {
            var key = new SeamKey(x, z, half);
            SeamTile found;
            if (SeamTiles.TryGetValue(key, out found)) return found;
            int[,] corners;
            if (((x + z) & 1) == 0)
                corners = half == 0 ? new[,] { { 0, 0 }, { 1, 0 }, { 1, 1 } } : new[,] { { 0, 0 }, { 1, 1 }, { 0, 1 } };
            else
                corners = half == 0 ? new[,] { { 0, 0 }, { 1, 0 }, { 0, 1 } } : new[,] { { 1, 0 }, { 1, 1 }, { 0, 1 } };
            var source = new Vector2[3]; var mapped = new Vector2[3]; var nodes = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                int ix = x + corners[i, 0], iz = z + corners[i, 1];
                source[i] = new Vector2(SeamOriginX + ix * SeamStep, SeamOriginZ + iz * SeamStep);
                nodes[i] = SeamNode(ix, iz); mapped[i] = new Vector2(nodes[i].x, nodes[i].z);
            }
            Vector2 a = source[1] - source[0], b = source[2] - source[0];
            Vector3 qa = nodes[1] - nodes[0], qb = nodes[2] - nodes[0];
            float denominator = a.x * b.y - a.y * b.x;
            Vector3 dx = (qa * b.y - qb * a.y) / denominator;
            Vector3 dz = (qb * a.x - qa * b.x) / denominator;
            Vector3 origin = nodes[0] - dx * source[0].x - dz * source[0].y;
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.SetColumn(0, new Vector4(dx.x, dx.y, dx.z, 0f));
            matrix.SetColumn(1, new Vector4(0f, 1f, 0f, 0f));
            matrix.SetColumn(2, new Vector4(dz.x, dz.y, dz.z, 0f));
            matrix.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1f));
            found = new SeamTile { Key = key, Source = source, Mapped = mapped, Forward = matrix, Inverse = matrix.inverse };
            SeamTiles.Add(key, found); return found;
        }

        private static SeamTile TileAtSource(Vector3 point)
        {
            float gx = (point.x - SeamOriginX) / SeamStep, gz = (point.z - SeamOriginZ) / SeamStep;
            int x = Mathf.FloorToInt(gx), z = Mathf.FloorToInt(gz);
            float u = gx - x, v = gz - z;
            int half = ((x + z) & 1) == 0 ? (u >= v ? 0 : 1) : (u + v <= 1f ? 0 : 1);
            return Tile(x, z, half);
        }

        private static float DomainSide(Vector2 a, Vector2 b, Vector3 point)
        { return (b.x - a.x) * (point.z - a.y) - (b.y - a.y) * (point.x - a.x); }

        private static SeamTile TileAtPartition(Vector3 point)
        {
            int x0 = Mathf.FloorToInt((point.x - SeamOriginX - SeamShiftX - Epsilon) / SeamStep);
            int x1 = Mathf.FloorToInt((point.x - SeamOriginX + SeamShiftX + Epsilon) / SeamStep);
            int z0 = Mathf.FloorToInt((point.z - SeamOriginZ - SeamShiftZ - Epsilon) / SeamStep);
            int z1 = Mathf.FloorToInt((point.z - SeamOriginZ + SeamShiftZ + Epsilon) / SeamStep);
            SeamTile nearest = null; float best = float.NegativeInfinity;
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) for (int half = 0; half < 2; half++)
            {
                SeamTile tile = Tile(x, z, half);
                float distance = Mathf.Min(DomainSide(tile.Mapped[0], tile.Mapped[1], point),
                    DomainSide(tile.Mapped[1], tile.Mapped[2], point), DomainSide(tile.Mapped[2], tile.Mapped[0], point));
                if (distance > best) { best = distance; nearest = tile; }
                if (distance >= 0f) return tile;
            }
            if (nearest == null || best < -.002f) throw new InvalidOperationException("No inverse structural seam tile at " + point);
            return nearest;
        }

        private static List<Vertex> ClipDomain(List<Vertex> input, Vector2[] domain)
        {
            for (int edge = 0; edge < 3 && input.Count > 0; edge++)
            {
                var output = new List<Vertex>(input.Count + 1);
                for (int i = 0; i < input.Count; i++)
                {
                    Vertex a = input[i], b = input[(i + 1) % input.Count];
                    float da = DomainSide(domain[edge], domain[(edge + 1) % 3], a.P);
                    float db = DomainSide(domain[edge], domain[(edge + 1) % 3], b.P);
                    if (da >= 0f) output.Add(a);
                    if ((da >= 0f) != (db >= 0f)) output.Add(Vertex.Lerp(a, b, da / (da - db)));
                }
                input = output;
            }
            return input;
        }

        private static Section MapSeams(Section input, bool forward)
            => forward ? StructuralHeightSeams.Map(MapPlanarSeams(input, true), true)
                : MapPlanarSeams(StructuralHeightSeams.Map(input, false), false);

        private static Section MapPlanarSeams(Section input, bool forward)
        {
            var result = new Section();
            foreach (Triangle triangle in input.Triangles)
            {
                Vertex[] vertices = { triangle.A, triangle.B, triangle.C };
                float padX = forward ? Epsilon : SeamShiftX + Epsilon, padZ = forward ? Epsilon : SeamShiftZ + Epsilon;
                int x0 = Mathf.FloorToInt((vertices.Min(v => v.P.x) - SeamOriginX - padX) / SeamStep);
                int x1 = Mathf.FloorToInt((vertices.Max(v => v.P.x) - SeamOriginX + padX) / SeamStep);
                int z0 = Mathf.FloorToInt((vertices.Min(v => v.P.z) - SeamOriginZ - padZ) / SeamStep);
                int z1 = Mathf.FloorToInt((vertices.Max(v => v.P.z) - SeamOriginZ + padZ) / SeamStep);
                for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) for (int half = 0; half < 2; half++)
                {
                    SeamTile tile = Tile(x, z, half);
                    List<Vertex> polygon = ClipDomain(vertices.ToList(), forward ? tile.Source : tile.Mapped);
                    if (polygon.Count < 3) continue;
                    Vector3 center = polygon.Aggregate(Vector3.zero, (sum, v) => sum + v.P) / polygon.Count;
                    // Half-open ownership avoids duplicating a vertical source
                    // face that lies exactly on a lattice edge. These tile
                    // boundaries never receive artificial visible side caps.
                    SeamTile owner = forward ? TileAtSource(center) : TileAtPartition(center);
                    if (!owner.Key.Equals(tile.Key)) continue;
                    Matrix4x4 transform = forward ? tile.Forward : tile.Inverse;
                    for (int i = 0; i < polygon.Count; i++)
                    { Vertex v = polygon[i]; v.P = transform.MultiplyPoint3x4(v.P); polygon[i] = v; }
                    for (int i = 1; i + 1 < polygon.Count; i++)
                    {
                        Vertex a = polygon[0], b = polygon[i], c = polygon[i + 1];
                        Vector3 normal = Vector3.Cross(b.P - a.P, c.P - a.P);
                        if (normal.sqrMagnitude <= 1e-12f) continue;
                        if (!forward && triangle.Interior)
                        {
                            normal.Normalize(); int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
                            if (Mathf.Abs(normal.z) > Mathf.Abs(normal[axis])) axis = 2;
                            a = CapVertex(a.P, normal, axis); b = CapVertex(b.P, normal, axis); c = CapVertex(c.P, normal, axis);
                        }
                        result.Triangles.Add(new Triangle(a, b, c, triangle.Interior, triangle.MaterialSlot));
                    }
                }
            }
            return result;
        }

        private static Section Partition(Section input)
        {
            // Cache the source tessellation once, shared by every major cell.
            // Only coordinates used for ownership/cutting move; inverse affine
            // mapping returns the exterior to its original triangles with its
            // interpolated authored normals and UVs intact.
            if (input.Partitioned == null) input.Partitioned = MapSeams(input, true);
            return input.Partitioned;
        }

#pragma warning disable 0649
        [Serializable] private sealed class InteriorFile { public int schemaVersion; public InteriorObject[] objects; }
        [Serializable] private sealed class InteriorObject { public string name, role; public float[][] vertices; public int[] triangles; }
#pragma warning restore 0649

        private static Section ReadAuthoredInterior()
        {
            // Asset inputs are immutable for the lifetime of the loaded catalog.
            // Hash the same byte snapshot that was decoded, even if a developer
            // replaces files on disk after the first outfit has been staged.
            authoredInteriorContent = File.ReadAllBytes(Path.Combine(VisualLoader.ShipAssetFolder, "interior_v5.json"));
            var file = JsonConvert.DeserializeObject<InteriorFile>(Encoding.UTF8.GetString(authoredInteriorContent).TrimStart('\uFEFF'));
            if (file == null || file.schemaVersion != 1 || file.objects == null) throw new InvalidDataException("Missing fitted compartment model.");
            var result = new Section();
            foreach (InteriorObject item in file.objects)
            {
                if (item.vertices == null || item.triangles == null || item.triangles.Length % 3 != 0)
                    throw new InvalidDataException("Invalid interior assembly " + item.name);
                for (int i = 0; i < item.triangles.Length; i += 3)
                {
                    var p = new Vector3[3];
                    for (int j = 0; j < 3; j++)
                    {
                        float[] v = item.vertices[item.triangles[i + j]];
                        if (v.Length != 3 || v.Any(n => float.IsNaN(n) || float.IsInfinity(n))) throw new InvalidDataException("Invalid interior vertex.");
                        p[j] = new Vector3(v[0], v[1], v[2]);
                    }
                    Vector3 normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized;
                    if (normal.sqrMagnitude < .5f) continue;
                    int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
                    if (Mathf.Abs(normal.z) > Mathf.Abs(normal[axis])) axis = 2;
                    Vertex[] vertices = p.Select(point => CapVertex(point, normal, axis)).ToArray();
                    result.Triangles.Add(new Triangle(vertices[0], vertices[1], vertices[2]));
                }
            }
            return result;
        }

        private static Section ClipCell(Section input, Cell cell, bool caps)
        {
            var output = new Section();
            Section partition = Partition(input);
            foreach (Bounds volume in cell.Volumes) output.Triangles.AddRange(Clip(partition, volume, caps).Triangles);
            // Generated caps are split at the mapped lattice too. Mapping only
            // a cap's distant corners back would flatten the intended zigzag.
            return MapSeams(output, false);
        }

        private struct Segment
        {
            internal Vector3 A, B;
            internal Segment(Vector3 a, Vector3 b) { A = a; B = b; }
        }

        private static Section Read(MeshFilter filter, Transform source, bool preserveMaterials = false)
        {
            if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("A source mesh is missing.");
            Mesh mesh = filter.sharedMesh;
            Vector3[] p = mesh.vertices, n = mesh.normals;
            Vector2[] uv = mesh.uv, uv1 = preserveMaterials ? mesh.uv2 : new Vector2[0];
            Color32[] colors = preserveMaterials ? mesh.colors32 : new Color32[0];
            var v = new Vertex[p.Length];
            Matrix4x4 transform = source.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            for (int i = 0; i < v.Length; i++)
                v[i] = new Vertex { P = transform.MultiplyPoint3x4(p[i]), N = transform.MultiplyVector(n[i]).normalized,
                    Uv = uv.Length == v.Length ? uv[i] : Vector2.zero,
                    Uv1 = uv1.Length == v.Length ? uv1[i] : Vector2.zero,
                    Color = colors.Length == v.Length ? colors[i] : new Color32(255, 255, 255, 255) };
            var result = new Section();
            // Only distant render meshes enter with the four native paint
            // categories already assigned. Keep their slot identity through
            // clipping; the structural/collision path retains its exterior /
            // generated-interior convention.
            for (int slot = 0; slot < (preserveMaterials ? mesh.subMeshCount : 1); slot++)
            {
                int[] indices = preserveMaterials ? mesh.GetTriangles(slot) : mesh.triangles;
                for (int i = 0; i < indices.Length; i += 3)
                    result.Triangles.Add(new Triangle(v[indices[i]], v[indices[i + 1]], v[indices[i + 2]], false, slot));
            }
            return result;
        }

        private static Section Clip(Section input, Bounds box, bool caps)
        {
            Section result = input;
            foreach (int axis in new[] { 2, 1, 0 })
            {
                if (box.min[axis] > -Far + 1) result = ClipPlane(result, axis, box.min[axis], true, caps);
                if (box.max[axis] < Far - 1) result = ClipPlane(result, axis, box.max[axis], false, caps);
                if (result.Triangles.Count == 0) break;
            }
            return result;
        }

        private static Section ClipPlane(Section input, int axis, float value, bool greater, bool caps)
        {
            var result = new Section();
            var segments = new List<Segment>();
            float sign = greater ? 1f : -1f;
            foreach (Triangle triangle in input.Triangles)
            {
                Vertex[] vertices = { triangle.A, triangle.B, triangle.C };
                var polygon = new List<Vertex>(4);
                for (int i = 0; i < 3; i++)
                {
                    Vertex a = vertices[i], b = vertices[(i + 1) % 3];
                    float da = (a.P[axis] - value) * sign, db = (b.P[axis] - value) * sign;
                    bool insideA = da >= -Epsilon, insideB = db >= -Epsilon;
                    if (insideA) polygon.Add(a);
                    if (insideA != insideB)
                    {
                        Vertex intersection = Vertex.Lerp(a, b, da / (da - db));
                        intersection.P[axis] = value;
                        polygon.Add(intersection);
                    }
                }
                for (int i = 1; i + 1 < polygon.Count; i++)
                    if (Vector3.Cross(polygon[i].P - polygon[0].P, polygon[i + 1].P - polygon[0].P).sqrMagnitude > 1e-12f)
                        result.Triangles.Add(new Triangle(polygon[0], polygon[i], polygon[i + 1], triangle.Interior, triangle.MaterialSlot));
                if (caps && polygon.Any(v => Mathf.Abs(v.P[axis] - value) > Epsilon))
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        Vector3 a = polygon[i].P, b = polygon[(i + 1) % polygon.Count].P;
                        // Include existing mesh edges on the cut, not just new
                        // intersections: several authored hull rings lie exactly
                        // on a bulkhead plane.
                        if (Mathf.Abs(a[axis] - value) <= Epsilon && Mathf.Abs(b[axis] - value) <= Epsilon &&
                            (a - b).sqrMagnitude > 1e-10f) segments.Add(new Segment(a, b));
                    }
            }
            if (caps && segments.Count > 0) AddCaps(result, segments, axis, greater ? -1f : 1f);
            return result;
        }

        private struct Key : IEquatable<Key>
        {
            private readonly int X, Y, Z;
            internal Key(Vector3 p) { X = Mathf.RoundToInt(p.x * 1000); Y = Mathf.RoundToInt(p.y * 1000); Z = Mathf.RoundToInt(p.z * 1000); }
            public bool Equals(Key b) { return X == b.X && Y == b.Y && Z == b.Z; }
            public override bool Equals(object b) { return b is Key && Equals((Key)b); }
            public override int GetHashCode() { unchecked { return (X * 397 ^ Y) * 397 ^ Z; } }
        }

        private static void AddCaps(Section result, List<Segment> segments, int axis, float normalSign)
        {
            var positions = new List<Vector3>();
            var map = new Dictionary<Key, int>();
            var adjacent = new List<HashSet<int>>();
            Func<Vector3, int> id = point =>
            {
                var key = new Key(point);
                int found;
                if (!map.TryGetValue(key, out found))
                { found = positions.Count; map.Add(key, found); positions.Add(point); adjacent.Add(new HashSet<int>()); }
                return found;
            };
            foreach (Segment segment in segments)
            { int a = id(segment.A), b = id(segment.B); if (a != b) { adjacent[a].Add(b); adjacent[b].Add(a); } }
            var visited = new HashSet<int>();
            Vector3 normal = Vector3.zero; normal[axis] = normalSign;
            // Imported plating contains split normals and some open seams. Joining
            // each connected cross-section before taking its planar outline gives
            // a solid interior face even where a UV seam duplicated vertices.
            for (int start = 0; start < positions.Count; start++)
            {
                if (visited.Contains(start)) continue;
                var component = new List<Vector3>();
                var stack = new Stack<int>(); stack.Push(start); visited.Add(start);
                while (stack.Count > 0)
                {
                    int current = stack.Pop(); component.Add(positions[current]);
                    foreach (int next in adjacent[current]) if (visited.Add(next)) stack.Push(next);
                }
                List<Vector3> outline = PlanarHull(component, axis);
                if (outline.Count < 3) continue;
                if (Vector3.Dot(Vector3.Cross(outline[1] - outline[0], outline[2] - outline[0]), normal) < 0) outline.Reverse();
                for (int i = 1; i + 1 < outline.Count; i++)
                    result.Triangles.Add(new Triangle(CapVertex(outline[0], normal, axis),
                        CapVertex(outline[i], normal, axis), CapVertex(outline[i + 1], normal, axis), true));
            }
        }

        private static Vertex CapVertex(Vector3 point, Vector3 normal, int axis)
        { return new Vertex { P = point, N = normal, Uv = new Vector2(point[(axis + 1) % 3], point[(axis + 2) % 3]) * .1f }; }

        private static List<Vector3> PlanarHull(IEnumerable<Vector3> points, int axis)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            var sorted = points.GroupBy(p => new Key(p)).Select(g => g.First()).OrderBy(p => p[u]).ThenBy(p => p[v]).ToList();
            if (sorted.Count < 3) return sorted;
            Func<Vector3, Vector3, Vector3, float> cross = (a, b, c) => (b[u] - a[u]) * (c[v] - a[v]) - (b[v] - a[v]) * (c[u] - a[u]);
            var hull = new List<Vector3>();
            foreach (Vector3 p in sorted)
            { while (hull.Count >= 2 && cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= Epsilon) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            int lower = hull.Count;
            for (int i = sorted.Count - 2; i >= 0; i--)
            { Vector3 p = sorted[i]; while (hull.Count > lower && cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= Epsilon) hull.RemoveAt(hull.Count - 1); hull.Add(p); }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static Mesh ToMesh(Section section, Transform source, Transform parent, string name, int materialCount = 0)
        {
            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
            var damageUvs = new List<Vector2>(); var colors = new List<Color32>();
            var surface = new List<int>(); var interior = new List<int>();
            var materialTriangles = materialCount > 0 ? Enumerable.Range(0, materialCount).Select(i => new List<int>()).ToArray() : null;
            Matrix4x4 transform = parent.worldToLocalMatrix * source.localToWorldMatrix;
            foreach (Triangle triangle in section.Triangles)
            {
                List<int> indices = materialTriangles != null ? materialTriangles[triangle.MaterialSlot] : triangle.Interior ? interior : surface;
                foreach (Vertex v in new[] { triangle.A, triangle.B, triangle.C })
                {
                    indices.Add(positions.Count); positions.Add(transform.MultiplyPoint3x4(v.P)); normals.Add(transform.MultiplyVector(v.N).normalized); uvs.Add(v.Uv);
                    if (materialTriangles != null) { damageUvs.Add(v.Uv1); colors.Add(v.Color); }
                }
            }
            var mesh = new Mesh { name = name, indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetUVs(1, materialTriangles != null ? damageUvs : uvs);
            mesh.colors32 = materialTriangles != null ? colors.ToArray() : Enumerable.Repeat(new Color32(255, 255, 255, 255), positions.Count).ToArray();
            if (materialTriangles != null)
            {
                mesh.subMeshCount = materialCount;
                for (int slot = 0; slot < materialCount; slot++) mesh.SetTriangles(materialTriangles[slot], slot);
            }
            else
            {
                mesh.subMeshCount = interior.Count > 0 ? 2 : 1;
                mesh.SetTriangles(surface, 0); if (interior.Count > 0) mesh.SetTriangles(interior, 1);
            }
            mesh.RecalculateBounds(); mesh.RecalculateTangents();
            return mesh;
        }

        private static Section BuildBulkhead(Section cap, int axis, int seed)
        {
            var output = new Section();
            List<Vector3> outline = PlanarHull(cap.Triangles.SelectMany(t => new[] { t.A.P, t.B.P, t.C.P }), axis);
            if (outline.Count < 3) return output;
            Vector3 center = outline.Aggregate(Vector3.zero, (sum, p) => sum + p) / outline.Count;
            var random = new System.Random(seed);
            var perimeter = new List<Vector3>();
            for (int i = 0; i < outline.Count; i++)
            {
                Vector3 a = outline[i], b = outline[(i + 1) % outline.Count];
                int divisions = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / 3.5f), 1, 28);
                for (int step = 0; step < divisions; step++)
                {
                    Vector3 point = Vector3.Lerp(a, b, step / (float)divisions);
                    perimeter.Add(Vector3.Lerp(point, center, .01f + (float)random.NextDouble() * .035f));
                }
            }
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            Vector3 aperture = center;
            aperture[u] += (outline.Max(p => p[u]) - outline.Min(p => p[u])) * ((float)random.NextDouble() - .5f) * .2f;
            aperture[v] += (outline.Max(p => p[v]) - outline.Min(p => p[v])) * ((float)random.NextDouble() - .5f) * .2f;
            Vector3 normal = Vector3.zero; normal[axis] = 1f;
            float thickness = Mathf.Lerp(.16f, .30f, (float)random.NextDouble());
            var inner = new List<Vector3>();
            // Native Dynamo retains broad inner steel and layered decks with
            // irregular torn rims. One offset aperture is a missing part of a
            // bulkhead; there is no repeated orthogonal lattice here.
            foreach (Vector3 outer in perimeter)
            {
                float fraction = axis == 1 ? Mathf.Lerp(.16f, .28f, (float)random.NextDouble())
                    : Mathf.Lerp(.30f, .48f, (float)random.NextDouble());
                Vector3 point = Vector3.Lerp(aperture, outer, fraction);
                point[axis] += ((float)random.NextDouble() - .5f) * thickness * 2f;
                inner.Add(point);
            }
            for (int i = 0; i < perimeter.Count; i++)
            {
                int j = (i + 1) % perimeter.Count;
                Vector3 a = perimeter[i], b = perimeter[j], c = inner[j], d = inner[i], offset = normal * thickness;
                AddSteelQuad(output, a, b, c, d);
                AddSteelQuad(output, d - offset, c - offset, b - offset, a - offset);
                AddSteelQuad(output, a - offset, b - offset, b, a);
                AddSteelQuad(output, d, c, c - offset, d - offset);
            }
            return output;
        }

        private static void AddSteelQuad(Section output, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
            if (normal.sqrMagnitude < .5f) return;
            int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
            if (Mathf.Abs(normal.z) > Mathf.Abs(normal[axis])) axis = 2;
            var points = new[] { a, b, c, d }.Select(p => new Vertex { P = p, N = normal,
                Uv = new Vector2(p[(axis + 1) % 3], p[(axis + 2) % 3]) * .08f }).ToArray();
            output.Triangles.Add(new Triangle(points[0], points[1], points[2]));
            output.Triangles.Add(new Triangle(points[0], points[2], points[3]));
        }

        private static Bounds BoundsOf(IEnumerable<Vector3> positions)
        {
            bool first = true; var result = new Bounds();
            foreach (Vector3 p in positions) { if (first) { result = new Bounds(p, Vector3.zero); first = false; } else result.Encapsulate(p); }
            return result;
        }

        private sealed class Face
        {
            internal int A, B, C;
            internal Vector3 Normal;
            internal Face(int a, int b, int c, List<Vector3> points, Vector3 inside)
            {
                A = a; B = b; C = c;
                Normal = CollisionNormal(Vector3.Cross(points[b] - points[a], points[c] - points[a]));
                if (Vector3.Dot(Normal, inside - points[a]) > 0) { B = c; C = b; Normal = -Normal; }
            }
            internal float Distance(Vector3 p, List<Vector3> points) { return Vector3.Dot(Normal, p - points[A]); }
        }

        private struct Edge : IEquatable<Edge>
        {
            internal readonly int A, B;
            internal Edge(int a, int b) { A = Mathf.Min(a, b); B = Mathf.Max(a, b); }
            public bool Equals(Edge b) { return A == b.A && B == b.B; }
            public override bool Equals(object b) { return b is Edge && Equals((Edge)b); }
            public override int GetHashCode() { return A * 397 ^ B; }
        }

        private static Vector3 CollisionNormal(Vector3 value)
        {
            float length = value.magnitude;
            return length > 1e-12f ? value / length : Vector3.zero;
        }

        internal static Mesh ConvexHull(Vector3[] input, string name)
        {
            // Keep actual surface extrema in 98 directions. The resulting hull
            // stays below PhysX's 255-face convex limit without box-shaped decks.
            var unique = input.GroupBy(p => new Key(p)).Select(g => g.First()).ToArray();
            var chosen = new HashSet<int>();
            for (int x = -2; x <= 2; x++) for (int y = -2; y <= 2; y++) for (int z = -2; z <= 2; z++)
            {
                if (Mathf.Max(Mathf.Abs(x), Mathf.Max(Mathf.Abs(y), Mathf.Abs(z))) != 2) continue;
                Vector3 direction = new Vector3(x, y, z); float best = float.NegativeInfinity; int index = 0;
                for (int i = 0; i < unique.Length; i++) { float d = Vector3.Dot(unique[i], direction); if (d > best) { best = d; index = i; } }
                chosen.Add(index);
            }
            List<Vector3> points = chosen.Select(i => unique[i]).ToList();
            if (points.Count < 2) throw new InvalidOperationException("Point-like source collision: " + name);
            int a = 0, b = Enumerable.Range(1, points.Count - 1).OrderByDescending(i => (points[i] - points[a]).sqrMagnitude).First();
            Vector3 line = points[b] - points[a];
            int c = Enumerable.Range(0, points.Count).OrderByDescending(i => Vector3.Cross(line, points[i] - points[a]).sqrMagnitude).First();
            Vector3 cross = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
            if (cross.magnitude / Mathf.Max(line.magnitude, 1e-6f) < .001f)
            {
                Vector3 perpendicular = CollisionNormal(Vector3.Cross(line.normalized,
                    Mathf.Abs(line.normalized.y) < .9f ? Vector3.up : Vector3.right));
                return ConvexHull(points.SelectMany(p => new[] { p - perpendicular * .005f, p + perpendicular * .005f }).ToArray(),
                    name + " / minimum collision width");
            }
            Vector3 normal = CollisionNormal(cross);
            int d0 = Enumerable.Range(0, points.Count).OrderByDescending(i => Mathf.Abs(Vector3.Dot(normal, points[i] - points[a]))).First();
            if (Mathf.Abs(Vector3.Dot(normal, points[d0] - points[a])) < .001f)
            {
                // Clipping can leave an extremely narrow retained strip whose
                // source width is below PhysX's usable convex precision. Give
                // only that collision dimension a one-centimetre thickness.
                // Its rendered mesh and the regular shell thickness are intact.
                return ConvexHull(points.SelectMany(p => new[] { p - normal * .005f, p + normal * .005f }).ToArray(),
                    name + " / minimum collision thickness");
            }
            Vector3 inside = (points[a] + points[b] + points[c] + points[d0]) * .25f;
            var faces = new List<Face> { new Face(a,b,c,points,inside), new Face(a,d0,b,points,inside), new Face(a,c,d0,points,inside), new Face(b,d0,c,points,inside) };
            for (int p = 0; p < points.Count; p++)
            {
                if (p == a || p == b || p == c || p == d0) continue;
                Face[] visible = faces.Where(f => f.Distance(points[p], points) > .0001f).ToArray();
                if (visible.Length == 0) continue;
                var edges = new Dictionary<Edge, int>();
                foreach (Face face in visible)
                    foreach (Edge edge in new[] { new Edge(face.A, face.B), new Edge(face.B, face.C), new Edge(face.C, face.A) })
                    { int count; edges.TryGetValue(edge, out count); edges[edge] = count + 1; }
                foreach (Face face in visible) faces.Remove(face);
                foreach (var edge in edges) if (edge.Value == 1)
                {
                    var face = new Face(edge.Key.A, edge.Key.B, p, points, inside);
                    if (face.Normal.sqrMagnitude > .5f) faces.Add(face);
                }
            }
            if (faces.Count > 250) throw new InvalidOperationException("Source collision exceeded convex face budget: " + name);
            float volume = Mathf.Abs(faces.Sum(f => Vector3.Dot(points[f.A] - inside,
                Vector3.Cross(points[f.B] - inside, points[f.C] - inside))) / 6f);
            if (!(volume > 1e-12f))
                throw new InvalidOperationException("Zero-volume collision input: " + name + " (volume " + volume + ")");
            var mesh = new Mesh { name = name };
            mesh.SetVertices(points); mesh.SetTriangles(faces.SelectMany(f => new[] { f.A, f.B, f.C }).ToArray(), 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }
    }

    [DefaultExecutionOrder(150)]
    internal sealed class ResoluteStructuralSection : MonoBehaviour
    {
        public event Action<ResoluteStructuralSection, int, Transform> SurfacePieceReleased;
        public UnitPart Part;
        public Renderer Body;
        public Mesh ClosedMesh;
        public Renderer[] InteriorFaces, NeighborFaces;
        public Mesh[] SurfaceMeshes, SolidMeshes;
        public Mesh[] StyledSolidMeshes;
        public Mesh StyledRetainedDetail;
        public Mesh RetainedDetailSolid;
        public Vector3 RetainedDetailCenter;
        public float RetainedDetailArea;
        public int RetainedDetailComponents;
        public Vector3[] PlateCenters, PlateNormals;
        public float[] PlateAreas, PlateDiameters, PlateThicknesses;
        public Renderer[] PlateRenderers;
        public MeshCollider[] PlateColliders, GroupColliders;
        public Mesh[] CollisionMeshes, PieceLocalCollisionMeshes;
        public int[] CollisionRegionPieces, CollisionRegionGroups;
        internal MeshCollider[] RegionColliders;
        private int[][] pieceCollisionRegions;
        private bool[] expandedCollisionGroups;
        internal int ExpandedCollisionGroups { get; private set; }
        internal int ActiveCollisionCount => GroupColliders.Count(c => c != null && c.enabled) +
            (RegionColliders != null ? RegionColliders.Count(c => c != null && c.enabled) : 0);
        public Material Interior;
        public Bounds LocalBounds;
        public float SurfaceArea, CoveredArea;
        public int ConnectedComponents;
        public string CacheSource;
        internal SurfaceFracture.Piece[] Pieces;
        internal int ReleasedCount { get; private set; }
        internal float ReleasedMass { get; private set; }
        internal float ReleasedArea { get; private set; }
        internal float RetainedSurfaceArea => Mathf.Max(0f, SurfaceArea - ReleasedArea);
        internal float RetainedSurfaceFraction => SurfaceArea > 0 ? RetainedSurfaceArea / SurfaceArea : 1f;
        internal float InitialPartMass { get; private set; }
        internal float PlatingMass { get; private set; }
        internal int UnlocalizedDamageEvents { get; private set; }
        internal int LocalizedDamageEvents { get; private set; }
        internal string LastImpactSource { get; private set; }
        internal Vector3 LastImpactLocal { get; private set; }
        internal int RetainedMeshRebuilds { get; private set; }
        internal double ReleaseMilliseconds { get; private set; }
        internal double RebuildMilliseconds { get; private set; }
        internal readonly List<ResolutePlateDebris> ReleasedDebris = new List<ResolutePlateDebris>();
        private bool[] released;
        private bool retainedDirty;
        private Mesh retainedMesh;
        private MeshFilter bodyFilter;
        private Material[] debrisMaterials;
        private ResoluteStructuralSection[] vesselSections;
        private Vector3 shockOrigin;
        private float shockPower, shockTime = -100f;
        private static readonly FieldInfo CollisionSize = typeof(UnitPart).GetField("collisionSize", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CollisionBounds = typeof(UnitPart).GetField("bounds", BindingFlags.Instance | BindingFlags.NonPublic);
        internal const int MaximumLiveDebrisPerShip = 24;
        internal const float MaximumRemovedSurfaceFraction = .20f;

        internal void SetFracture(SurfaceFracture.Result fracture)
        {
            Pieces = fracture.Pieces;
            SurfaceMeshes = Pieces.Select(p => p.Surface).ToArray(); SolidMeshes = Pieces.Select(p => p.Solid).ToArray();
            PlateCenters = Pieces.Select(p => p.Center).ToArray(); PlateNormals = Pieces.Select(p => p.Normal).ToArray();
            PlateAreas = Pieces.Select(p => p.Area).ToArray(); PlateDiameters = Pieces.Select(p => p.Diameter).ToArray();
            PlateThicknesses = Pieces.Select(p => p.Thickness).ToArray();
            SurfaceArea = fracture.SourceArea; CoveredArea = fracture.CoveredArea; ConnectedComponents = fracture.ConnectedComponents;
            RetainedDetailSolid = fracture.RetainedDetail?.Solid; RetainedDetailCenter = fracture.RetainedDetail?.Center ?? Vector3.zero;
            RetainedDetailArea = fracture.RetainedDetail?.Area ?? 0; RetainedDetailComponents = fracture.RetainedDetailComponents;
        }

        private void Awake()
        {
            if (SurfaceMeshes == null || SolidMeshes == null || PlateCenters == null || PlateAreas == null)
                throw new InvalidOperationException("Cloned connected-shell metadata is missing for " + name);
            if (StyledSolidMeshes == null || StyledSolidMeshes.Length != SolidMeshes.Length || Body.sharedMaterials.Length != 4)
                throw new InvalidOperationException("Cloned metric-paint shell metadata is missing for " + name);
            Pieces = Enumerable.Range(0, SurfaceMeshes.Length).Select(i => new SurfaceFracture.Piece
            {
                Surface = SurfaceMeshes[i], Solid = SolidMeshes[i], Center = PlateCenters[i], Normal = PlateNormals[i],
                Area = PlateAreas[i], Diameter = PlateDiameters[i], Thickness = PlateThicknesses[i]
            }).ToArray();
            released = new bool[Pieces.Length]; bodyFilter = Body.GetComponent<MeshFilter>();
            if (PieceLocalCollisionMeshes == null || PieceLocalCollisionMeshes.Length != CollisionMeshes.Length ||
                CollisionRegionPieces.Where((piece, region) => piece >= 0 && PieceLocalCollisionMeshes[region] == null).Any())
                throw new InvalidOperationException("Missing shared piece-local collision meshes for " + name);
            RegionColliders = new MeshCollider[CollisionMeshes.Length]; expandedCollisionGroups = new bool[GroupColliders.Length];
            pieceCollisionRegions = Enumerable.Range(0, Pieces.Length).Select(piece =>
                Enumerable.Range(0, CollisionRegionPieces.Length).Where(region => CollisionRegionPieces[region] == piece).ToArray()).ToArray();
            if (pieceCollisionRegions.Any(regions => regions.Length == 0)) throw new InvalidOperationException("Incomplete compound tear-to-collision mapping for " + name);
            InitialPartMass = Part.mass;
            PlatingMass = Mathf.Min(InitialPartMass * .14f, Pieces.Sum(p => p.Area * p.Thickness) * 7850f);
            vesselSections = Part.parentUnit.GetComponentsInChildren<ResoluteStructuralSection>(true);
            Bounds world = new Bounds(transform.TransformPoint(LocalBounds.center), Vector3.zero);
            foreach (Vector3 vertex in ClosedMesh.vertices) world.Encapsulate(transform.TransformPoint(vertex));
            CollisionSize.SetValue(Part, world.extents); CollisionBounds.SetValue(Part, world);
            Part.onApplyDamage += Damaged;
            Part.onPartDetached += StructuralSeparation;
            Part.onJointBroken += StructuralSeparation;
            enabled = false;
            // Parent detachment moves this section with its native parent body.
            // It is not another explosion and must not strip a healthy child.
        }

        internal void RememberShockwave(Vector3 origin, float power)
        { shockOrigin = origin; shockPower = power; shockTime = Time.time; }

        private void Damaged(UnitPart.OnApplyDamage damage)
        {
            if (damage.hitPoints < 35f) ShowInterior(false);
            float energeticDamage = damage.pierceDamage + damage.blastDamage + damage.impactDamage;
            if (energeticDamage <= 0f || damage.hitPoints > -40f) return;
            StructuralImpact.Scope context = StructuralImpact.Current;
            Vector3 origin; float power;
            if (context.Valid && (context.Target == null || context.Target == Part))
            {
                origin = context.Origin; power = context.BlastPower;
                LastImpactSource = context.Source;
            }
            else if (Time.time - shockTime < .3f)
            {
                origin = shockOrigin; power = shockPower; LastImpactSource = "native-shockwave";
            }
            else
            {
                // A health-only event, fire tick or remote update does not
                // contain a hit coordinate. Preserve its native material and
                // coherent compartment rather than inventing scattered tears.
                UnlocalizedDamageEvents++; return;
            }
            LocalizedDamageEvents++;
            Vector3 closest = origin; float distance = float.PositiveInfinity;
            foreach (MeshCollider collider in GroupColliders.Concat(RegionColliders))
            {
                if (collider == null || !collider.enabled) continue;
                Vector3 point = collider.ClosestPoint(origin); float square = (point - origin).sqrMagnitude;
                if (square < distance) { closest = point; distance = square; }
            }
            LastImpactLocal = transform.InverseTransformPoint(closest);
            float nativeFailure = Mathf.Max(150f, Mathf.Abs(Part.GetStructuralThreshold()));
            float severity = Mathf.Clamp01(-damage.hitPoints / nativeFailure);
            float areaBudget = SurfaceArea * Mathf.Lerp(.025f, MaximumRemovedSurfaceFraction, severity);
            float radius = Mathf.Clamp(power > 0 ? power * 3f : Mathf.Sqrt(energeticDamage) * .28f, 4.5f, 18f);
            var candidates = Enumerable.Range(0, Pieces.Length).Where(i => !released[i])
                .OrderBy(i => TearScore(Pieces[i], LastImpactLocal, origin)).ToArray();
            int emitted = 0;
            foreach (int index in candidates)
            {
                if (emitted >= 3 || LiveDebrisCount() >= MaximumLiveDebrisPerShip) break;
                SurfaceFracture.Piece piece = Pieces[index];
                if (piece.Area < 2f || ReleasedArea + piece.Area > areaBudget) continue;
                if (Vector3.Distance(piece.Center, LastImpactLocal) > radius + piece.Diameter * .3f) continue;
                // Large load-bearing deck regions are retained until a direct
                // severe attack. This also avoids orphaning every VLS hatch.
                if (piece.Normal.y > .72f && severity < .8f) continue;
                Release(index, origin, power); emitted++;
            }
        }

        private float TearScore(SurfaceFracture.Piece piece, Vector3 hit, Vector3 origin)
        {
            Vector3 direction = transform.InverseTransformPoint(origin) - piece.Center;
            float facing = direction.sqrMagnitude > .01f ? Vector3.Dot(direction.normalized, piece.Normal) : 1f;
            return Vector3.Distance(piece.Center, hit) + Mathf.Max(0f, -facing) * 30f + (piece.Normal.y > .72f ? 4f : 0f);
        }

        private int LiveDebrisCount()
        { return vesselSections.Where(s => s != null).Sum(s => s.ReleasedDebris.Count(d => d != null)); }

        private void StructuralSeparation(UnitPart part)
        { ShowInterior(true); }

        private void ShowInterior(bool separated)
        {
            foreach (Renderer face in InteriorFaces) if (face != null) face.enabled = true;
            if (separated) foreach (Renderer face in NeighborFaces) if (face != null) face.enabled = true;
        }

        private void ExpandCollisionGroup(int group)
        {
            if (expandedCollisionGroups[group]) return;
            expandedCollisionGroups[group] = true; GroupColliders[group].enabled = false; ExpandedCollisionGroups++;
            for (int i = 0; i < CollisionMeshes.Length; i++)
            {
                if (CollisionRegionGroups[i] != group) continue;
                MeshCollider collider = Part.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = CollisionMeshes[i]; collider.convex = true; collider.contactOffset = .015f;
                RegionColliders[i] = collider;
                int piece = CollisionRegionPieces[i];
                if (piece >= 0) { if (PlateColliders[piece] == null) PlateColliders[piece] = collider; collider.enabled = !released[piece]; }
            }
        }

        private void Release(int index, Vector3 origin, float power)
        {
            if (released[index]) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SurfaceFracture.Piece piece = Pieces[index];
            foreach (int region in pieceCollisionRegions[index]) ExpandCollisionGroup(CollisionRegionGroups[region]);
            released[index] = true;
            foreach (int region in pieceCollisionRegions[index]) RegionColliders[region].enabled = false;
            float mass = PlatingMass * piece.Area / SurfaceArea;
            Rigidbody parent = Part.rb;
            Vector3 point = transform.TransformPoint(piece.Center);
            Vector3 inherited = parent != null ? parent.GetPointVelocity(point) : Vector3.zero;
            Vector3 rotation = parent != null ? parent.angularVelocity : Vector3.zero;
            Part.mass -= mass;
            ResoluteMassDistribution distribution = Part.parentUnit != null ? Part.parentUnit.GetComponent<ResoluteMassDistribution>() : null;
            if (distribution != null) distribution.RemoveMass(Part, mass, point);
            if (parent != null) parent.mass = Mathf.Max(1f, parent.mass - mass);
            if (debrisMaterials == null)
            {
                debrisMaterials = Body.sharedMaterials.Select(material => NavalMaterials.UsesNativeDamage(material) ? new Material(material) : material).ToArray();
                foreach (Material material in debrisMaterials) if (NavalMaterials.UsesNativeDamage(material)) material.SetFloat("_HitPoints", 0f);
            }
            var obj = new GameObject("Resolute connected torn shell " + Part.name + " " + index);
            obj.transform.SetPositionAndRotation(point, transform.rotation); obj.transform.localScale = transform.lossyScale;
            obj.transform.SetParent(Datum.origin, true);
            obj.AddComponent<MeshFilter>().sharedMesh = StyledSolidMeshes[index];
            obj.AddComponent<MeshRenderer>().sharedMaterials = debrisMaterials;
            var localHulls = new List<Mesh>();
            foreach (int region in pieceCollisionRegions[index])
            {
                MeshCollider collision = obj.AddComponent<MeshCollider>();
                Mesh localHull = PieceLocalCollisionMeshes[region];
                collision.sharedMesh = localHull; collision.convex = true; collision.contactOffset = .01f;
                localHulls.Add(localHull);
            }
            Rigidbody body = obj.AddComponent<Rigidbody>();
            body.mass = Mathf.Max(.1f, mass); body.interpolation = RigidbodyInterpolation.Interpolate;
            body.maxLinearVelocity = 80f; body.maxAngularVelocity = 6f;
            Vector3 outward = point - origin;
            if (outward.sqrMagnitude < .01f) outward = transform.TransformDirection(piece.Normal);
            Vector3 kick = outward.normalized * Mathf.Clamp(.8f + power * .3f, .8f, 3f) + Vector3.up * .15f;
            body.velocity = inherited + kick;
            body.angularVelocity = rotation + new Vector3((index % 3) - 1, .2f, ((index / 3) % 3) - 1) * .15f;
            if (parent != null && !parent.isKinematic && Part.parentUnit != null && Part.parentUnit.LocalSim)
                parent.AddForceAtPosition(-kick * mass, point, ForceMode.Impulse);
            ResolutePlateDebris debris = obj.AddComponent<ResolutePlateDebris>();
            debris.CollisionMeshes = localHulls.ToArray(); debris.SourcePart = Part.name;
            debris.PlateIndex = index; debris.Diameter = piece.Diameter; debris.Area = piece.Area;
            ReleasedDebris.Add(debris); DebrisManager.RegisterDebris(obj); Object.Destroy(obj, 75f);
            ReleasedCount++; ReleasedMass += mass; ReleasedArea += piece.Area; retainedDirty = true; enabled = true;
            SurfacePieceReleased?.Invoke(this, index, obj.transform);
            ShowInterior(false);
            foreach (MeshCollider collider in Part.GetComponents<MeshCollider>())
                if (collider.sharedMesh != null && collider.sharedMesh.name.StartsWith(CarrierIntegration.DeckCollisionName, StringComparison.Ordinal))
                    collider.enabled = false;
            ReleaseMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void LateUpdate()
        {
            if (!retainedDirty) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            retainedDirty = false; enabled = false;
            var meshes = new List<Mesh>(); var centers = new List<Vector3>();
            if (StyledRetainedDetail != null) { meshes.Add(StyledRetainedDetail); centers.Add(RetainedDetailCenter); }
            for (int i = 0; i < Pieces.Length; i++) if (!released[i]) { meshes.Add(StyledSolidMeshes[i]); centers.Add(Pieces[i].Center); }
            // Explicit slot indices also preserve an empty accent layer. Copy
            // each retained vertex once rather than once for every material.
            Mesh replacement = SurfacePaintStyle.MergeRetained(meshes, centers, "Resolute retained torn shell " + Part.name);
            bodyFilter.sharedMesh = replacement;
            if (retainedMesh != null) Object.Destroy(retainedMesh);
            retainedMesh = replacement; RetainedMeshRebuilds++;
            RebuildMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // Body stays registered with the native DamageMaterial and LODGroup.
            // The only removed polygons are the actual released regions.
        }

        private void OnDestroy()
        {
            if (Part != null)
            {
                Part.onApplyDamage -= Damaged; Part.onPartDetached -= StructuralSeparation; Part.onJointBroken -= StructuralSeparation;
            }
            if (retainedMesh != null) Object.Destroy(retainedMesh);
            if (debrisMaterials != null) foreach (Material material in debrisMaterials)
                if (NavalMaterials.UsesNativeDamage(material)) Object.Destroy(material, 76f);
        }
    }

    internal static class StructuralImpact
    {
        internal struct Scope
        {
            internal bool Valid;
            internal Vector3 Origin;
            internal float BlastPower;
            internal UnitPart Target;
            internal string Source;
        }
        [ThreadStatic] internal static Scope Current;

        [HarmonyPatch(typeof(DamageEffects), nameof(DamageEffects.FragTrace))]
        private static class FragmentRay
        {
            private static void Prefix(Vector3 origin, float blastPower, Collider fragTarget, out Scope __state)
            {
                __state = Current;
                Current = new Scope { Valid = true, Origin = origin, BlastPower = blastPower,
                    Target = fragTarget != null ? fragTarget.GetComponent<UnitPart>() : null, Source = "native-fragment-ray" };
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { Current = __state; return __exception; }
        }

        [HarmonyPatch(typeof(DamageEffects), nameof(DamageEffects.ArmorPenetrate))]
        private static class Penetration
        {
            private static void Prefix(Vector3 position, float blastDamage, out Scope __state)
            {
                __state = Current;
                Current = new Scope { Valid = true, Origin = position, BlastPower = Mathf.Pow(Mathf.Max(0f, blastDamage), 1f / 3f), Source = "native-penetration" };
            }
            private static Exception Finalizer(Exception __exception, Scope __state) { Current = __state; return __exception; }
        }

        [HarmonyPatch(typeof(UnitPart), nameof(UnitPart.TakeShockwave))]
        private static class Shockwave
        {
            private static void Prefix(UnitPart __instance, Vector3 origin, float blastPower)
            {
                ResoluteStructuralSection section = __instance.GetComponent<ResoluteStructuralSection>();
                if (section != null) section.RememberShockwave(origin, blastPower);
            }
        }
    }

    internal sealed class ResolutePlateDebris : MonoBehaviour
    {
        public Mesh[] CollisionMeshes;
        public string SourcePart;
        public int PlateIndex;
        public float Diameter, Area;
        private Rigidbody body;
        private void Awake() { body = GetComponent<Rigidbody>(); }
        private void FixedUpdate()
        {
            if (body == null) return;
            bool wet = transform.position.y < Datum.LocalSeaY;
            body.drag = wet ? 1.2f : .015f;
            body.angularDrag = wet ? 1.8f : .08f;
            if (transform.position.y < Datum.LocalSeaY - 80f) Object.Destroy(gameObject);
        }
        // Collision meshes are immutable template assets shared by every
        // corresponding tear, just like the source convex collision meshes.
    }

    // ShipPart.Awake normally derives buoyancy height from the visual collision
    // envelope. Preserve the audited hydrostatic model while fixing collision.
    // All native Awake calls finish before Spawner registers the physics jobs.
    [DefaultExecutionOrder(100)]
    internal sealed class ResoluteHydrostatics : MonoBehaviour
    {
        public ShipPart[] Parts;
        public float[] Heights;
        private static readonly FieldInfo Height = typeof(ShipPart).GetField("height", BindingFlags.Instance | BindingFlags.NonPublic);
        private void Awake()
        { for (int i = 0; i < Parts.Length; i++) if (Parts[i] != null) Height.SetValue(Parts[i], Heights[i]); }

        internal void RestoreHeight(ShipPart part)
        {
            int index = Array.IndexOf(Parts, part);
            if (index >= 0) Height.SetValue(part, Heights[index]);
        }
    }

    [HarmonyPatch(typeof(ShipPart), nameof(ShipPart.SetupJob))]
    internal static class ResoluteHydrostaticJobPatch
    {
        private static void Prefix(ShipPart __instance)
        {
            // A final guard at the actual job boundary makes this independent of
            // Unity's Awake ordering for components loaded from a mod assembly.
            if (__instance.parentUnit == null) return;
            ResoluteHydrostatics hydro = __instance.parentUnit.GetComponent<ResoluteHydrostatics>();
            if (hydro != null) hydro.RestoreHeight(__instance);
        }
    }

    // The native Ship startup resets COM from collider volume. Thin compound
    // plating has very different collider density from the configured masses.
    // Use actual closed compartment centroids weighted by those native masses.
    // This changes only mass moments, never attitude, buoyancy or control gains.
    internal sealed class ResoluteMassDistribution : MonoBehaviour
    {
        public UnitPart[] Parts;
        public Vector3[] LocalCenters;
        private bool dirty;
        private Ship ship;

        internal static void Configure(Ship ship, StructuralGeometry.Cell[] cells)
        {
            var component = ship.gameObject.AddComponent<ResoluteMassDistribution>();
            component.Parts = ship.GetComponentsInChildren<UnitPart>(true);
            component.LocalCenters = new Vector3[component.Parts.Length];
            var structure = cells.ToDictionary(c => c.Part, c => c.Section);
            for (int i = 0; i < component.Parts.Length; i++)
            {
                UnitPart part = component.Parts[i];
                if (structure.TryGetValue(part, out var section))
                    component.LocalCenters[i] = ClosedCentroid(section.ClosedMesh);
                else if (part.CenterOfMass != null)
                    component.LocalCenters[i] = part.transform.InverseTransformPoint(part.CenterOfMass.position);
                else
                {
                    // Native non-hull parts retain their authored collision
                    // centroid, expressed in their own local coordinates.
                    MeshCollider mesh = part.GetComponent<MeshCollider>();
                    BoxCollider box = part.GetComponent<BoxCollider>();
                    component.LocalCenters[i] = mesh != null && mesh.sharedMesh != null ? ClosedCentroid(mesh.sharedMesh) :
                        box != null ? box.center : Vector3.zero;
                }
            }
        }

        internal static Vector3 ClosedCentroid(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices; int[] indices = mesh.triangles;
            Vector3 origin = mesh.bounds.center;
            double volume = 0, x = 0, y = 0, z = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                Vector3 u = a - origin, v = b - origin, w = c - origin;
                double weight = ((double)u.x * ((double)v.y * w.z - (double)v.z * w.y) +
                    (double)u.y * ((double)v.z * w.x - (double)v.x * w.z) +
                    (double)u.z * ((double)v.x * w.y - (double)v.y * w.x)) / 6.0;
                volume += weight;
                x += weight * ((double)origin.x + a.x + b.x + c.x) * .25;
                y += weight * ((double)origin.y + a.y + b.y + c.y) * .25;
                z += weight * ((double)origin.z + a.z + b.z + c.z) * .25;
            }
            if (Math.Abs(volume) < 1e-7) throw new InvalidOperationException("No closed mass volume for " + mesh.name);
            var center = new Vector3((float)(x / volume), (float)(y / volume), (float)(z / volume));
            if (float.IsNaN(center.x) || float.IsNaN(center.y) || float.IsNaN(center.z) ||
                float.IsInfinity(center.x) || float.IsInfinity(center.y) || float.IsInfinity(center.z))
                throw new InvalidOperationException("Invalid mass centroid for " + mesh.name);
            return center;
        }

        private void Awake()
        {
            ship = GetComponent<Ship>();
            if (Parts == null || LocalCenters == null || Parts.Length != LocalCenters.Length)
                throw new InvalidOperationException("Missing Resolute native mass distribution.");
            foreach (UnitPart part in Parts)
            {
                part.onPartDetached += Changed; part.onParentDetached += Changed; part.onJointBroken += Changed;
            }
        }

        private void Changed(UnitPart part) { dirty = true; }
        internal void MarkDirty() { dirty = true; }
        private void LateUpdate() { if (dirty) Apply(); }

        internal void RemoveMass(UnitPart part, float removed, Vector3 worldCenter)
        {
            int index = Array.IndexOf(Parts, part);
            if (index < 0 || part.mass <= 0f) return;
            Vector3 removedCenter = part.transform.InverseTransformPoint(worldCenter);
            LocalCenters[index] = (LocalCenters[index] * (part.mass + removed) - removedCenter * removed) / part.mass;
            dirty = true;
        }

        private sealed class Moment
        {
            internal double Mass, X, Y, Z;
        }

        internal void Apply()
        {
            dirty = false;
            if (ship == null || !ship.LocalSim) return;
            var moments = new Dictionary<Rigidbody, Moment>();
            for (int i = 0; i < Parts.Length; i++)
            {
                UnitPart part = Parts[i];
                if (part == null || part.mass <= 0f) continue;
                // Native ShipPart transfers its children's total mass when
                // detaching. Plain UnitPart descendants can retain a stale rb
                // field, so use their actual physical hierarchy for moments.
                Rigidbody body = part.GetComponentInParent<Rigidbody>();
                if (body == null) continue;
                if (!moments.TryGetValue(body, out var moment)) moments.Add(body, moment = new Moment());
                Vector3 point = body.transform.InverseTransformPoint(part.transform.TransformPoint(LocalCenters[i]));
                moment.Mass += part.mass; moment.X += (double)point.x * part.mass;
                moment.Y += (double)point.y * part.mass; moment.Z += (double)point.z * part.mass;
            }
            foreach (var pair in moments)
                if (pair.Value.Mass > 0)
                    pair.Key.centerOfMass = new Vector3((float)(pair.Value.X / pair.Value.Mass),
                        (float)(pair.Value.Y / pair.Value.Mass), (float)(pair.Value.Z / pair.Value.Mass));
        }

        private void OnDestroy()
        {
            if (Parts == null) return;
            foreach (UnitPart part in Parts) if (part != null)
            { part.onPartDetached -= Changed; part.onParentDetached -= Changed; part.onJointBroken -= Changed; }
        }
    }

    [HarmonyPatch(typeof(Ship), "OnStartClient")]
    internal static class ResoluteMassStartupPatch
    {
        private static void Postfix(Ship __instance)
        { __instance.GetComponent<ResoluteMassDistribution>()?.Apply(); }
    }

    [HarmonyPatch(typeof(UnitPart), nameof(UnitPart.ModifyMass))]
    internal static class ResoluteMassChangePatch
    {
        private static void Postfix(UnitPart __instance)
        { if (__instance.parentUnit != null) __instance.parentUnit.GetComponent<ResoluteMassDistribution>()?.MarkDirty(); }
    }

}
