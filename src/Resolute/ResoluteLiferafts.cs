using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Mirage;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // QoL 1.1.8.1 demonstrates using native PilotDismounted for floating,
    // damageable, recoverable rafts. This owns a separately registered prefab;
    // it never changes GameAssets.pilotDismounted or installs a global death hook.
    internal sealed class ResoluteLiferafts : MonoBehaviour
    {
        // Uses the original Resolute_Raft_0.1.0 mesh, textures and icons. Only
        // Resolute owns the prefab, evacuation component and support adapter.
        internal static bool Enabled => true;
        internal const string DefinitionKey = "rsl_liferaft";
        internal const int StationCount = 30, CrewPerRaft = 10, GlobalLimit = 90;
        private const float GateSeconds = 1.8f, LaunchSpacing = .4f, InitialDelay = 3f;
        private static UnitDefinition definition;
        [SerializeField] private Transform[] gates, anchors;
        [SerializeField] private Quaternion[] closedRotations;
        [SerializeField] private UnitPart[] supports;
        [SerializeField] private float[] openingAngles;
        private Ship ship;
        private bool started;
        private float startedAt;
        private int deploymentCount, nextDeployment;
        internal int SpawnedRafts { get; private set; }
        internal int SkippedStations { get; private set; }
        internal int AuthoredStations => gates == null ? 0 : gates.Length;

        internal static void EnsureRegistered(Encyclopedia encyclopedia, Transform inactiveRoot, string assetFolder, ManualLogSource log)
        {
            if (!Enabled) return;
            if (encyclopedia == null || inactiveRoot == null || inactiveRoot.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Resolute liferafts require an inactive prefab staging root.");
            UnitDefinition existing;
            if (Encyclopedia.Lookup.TryGetValue(DefinitionKey, out existing) && existing != definition)
                throw new InvalidOperationException("Another mod owns the Resolute liferaft definition key.");
            if (definition == null)
            {
                UnitDefinition donor = encyclopedia.otherUnits.FirstOrDefault(d => d != null && d.jsonKey == "pilotDismounted" &&
                    d.unitPrefab != null && d.unitPrefab.GetComponent<PilotDismounted>() != null);
                if (donor == null) throw new InvalidOperationException("Native dismounted pilot prefab is missing.");
                GameObject prefab = null;
                UnitDefinition created = Object.Instantiate(donor);
                try
                {
                    created.name = created.jsonKey = DefinitionKey;
                    created.unitName = "Resolute life raft"; created.code = "RAFT";
                    created.description = "Ten survivors in a Resolute life raft. Recoverable through the native rescue and sling-load systems.";
                    created.manpower = CrewPerRaft; created.width = created.length = 4f;
                    created.height = ResoluteRaftPlacement.BodySize.y;
                    created.CanSlingLoad = true; created.IsObstacle = false;
                    created.mapIcon = LoadIcon(assetFolder, "mapIcon_liferaft.png");
                    created.friendlyIcon = LoadIcon(assetFolder, "hudIcon_liferaft_friendly.png");
                    prefab = Object.Instantiate(donor.unitPrefab, inactiveRoot, false);
                    prefab.name = DefinitionKey; prefab.SetActive(true);
                    PilotDismounted pilot = prefab.GetComponent<PilotDismounted>(); pilot.definition = created;
                    created.unitPrefab = prefab;
                    foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
                    Mesh raftMesh;
                    AssetBundle bundle = AssetBundle.LoadFromFile(Path.Combine(assetFolder, "P_LifeRaft1.bundle"));
                    if (bundle == null) throw new InvalidDataException("Original Resolute raft visual bundle could not be read.");
                    try
                    {
                        MeshFilter source = bundle.LoadAllAssets<GameObject>().SelectMany(p => p.GetComponentsInChildren<MeshFilter>(true))
                            .FirstOrDefault(f => f.sharedMesh != null);
                        if (source == null) throw new InvalidDataException("Original Resolute raft bundle has no mesh.");
                        raftMesh = Object.Instantiate(source.sharedMesh); raftMesh.name = "Resolute_Original_Liferaft";
                        CorrectOriginalWinding(raftMesh);
                    }
                    finally { bundle.Unload(true); }
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null) throw new InvalidOperationException("Native URP Lit shader is unavailable for liferafts.");
                    var material = new Material(shader) { name = "Resolute_Original_Liferaft" };
                    material.SetTexture("_BaseMap", LoadTexture(assetFolder, "LifeRaft1_b.jpg", false));
                    material.SetTexture("_OcclusionMap", LoadTexture(assetFolder, "LifeRaft1_ao.jpg", true));
                    material.SetFloat("_OcclusionStrength", 1f);
                    material.SetFloat("_Metallic", 0f); material.SetFloat("_Smoothness", .15f);
                    var art = new GameObject("ResoluteLifeRaftVisual"); art.transform.SetParent(prefab.transform, false);
                    art.AddComponent<MeshFilter>().sharedMesh = raftMesh;
                    art.AddComponent<MeshRenderer>().sharedMaterial = material;
                    var box = prefab.GetComponent<BoxCollider>();
                    if (box == null) box = prefab.AddComponent<BoxCollider>();
                    Collider oldCollider = (Collider)Field(typeof(PilotDismounted), "pilotCollider").GetValue(pilot);
                    if (oldCollider != null && oldCollider != box) oldCollider.enabled = false;
                    box.center = ResoluteRaftPlacement.BodyCenter; box.size = ResoluteRaftPlacement.BodySize;
                    Field(typeof(PilotDismounted), "pilotCollider").SetValue(pilot, box);
                    prefab.AddComponent<ResoluteRaftLifetime>();
                    ConfigureIdentity(prefab);
                    Object.DontDestroyOnLoad(created); Object.DontDestroyOnLoad(raftMesh); Object.DontDestroyOnLoad(material);
                    definition = created;
                    log.LogInfo("Resolute liferaft registered with original Resolute_Raft_0.1.0 artwork and native pilot lifecycle; 10 survivors per raft.");
                }
                catch
                {
                    if (prefab != null) Object.Destroy(prefab);
                    Object.Destroy(created); throw;
                }
            }
            if (!encyclopedia.otherUnits.Contains(definition)) encyclopedia.otherUnits.Add(definition);
            Encyclopedia.Lookup[DefinitionKey] = definition;
            int index = encyclopedia.IndexLookup.IndexOf(definition);
            if (index < 0) { index = encyclopedia.IndexLookup.Count; encyclopedia.IndexLookup.Add(definition); }
            ((INetworkDefinition)definition).LookupIndex = index;
        }

        // Run before StructuralGeometry.BuildFitting reads the near LOD. Preserve
        // the 18 separate authored forward/midship gates and retain 12 aft
        // evacuation stations after the pictured rectangular panels are removed.
        internal static void Prepare(Ship ship, Transform visual)
        {
            if (!Enabled) return;
            if (definition == null) throw new InvalidOperationException("Register the Resolute raft prefab before preparing a ship.");
            var component = ship.gameObject.AddComponent<ResoluteLiferafts>();
            var near = visual.GetComponent<LODGroup>(); LOD[] lods = near.GetLODs();
            var renderers = lods[0].renderers.ToList();
            var found = renderers.Where(r => r.name.StartsWith("rsl_raft61_", StringComparison.Ordinal) && r.name.Contains("gate"))
                .OrderBy(r => r.name, StringComparer.Ordinal).Select(r => r.transform).ToList();
            if (found.Count != 18) throw new InvalidDataException("Expected 18 separately authored forward/midship liferaft gates.");
            var stations = found.Select(g => visual.InverseTransformPoint(g.position)).ToList();
            // The user removed the twelve aft rectangular door panels. Keep
            // their evacuation stations as invisible anchors owned by the same
            // local hull structure; no hidden renderer or replacement box.
            ShipPart[] parts = ship.GetComponentsInChildren<ShipPart>(true)
                .Where(p => p.GetComponent<Collider>() != null).ToArray();
            foreach (int side in new[] { -1, 1 })
                for (int row = 0; row < 2; row++)
                    foreach (float z in new[] { -51.97f, -50.4f, -48.83f })
                    {
                        Vector3 station = new Vector3(side * (row == 0 ? 13.4647f : 13.3399f), row == 0 ? 8.1453f : 8.9416f, z);
                        Vector3 world = visual.TransformPoint(station);
                        ShipPart owner = parts.OrderBy(p =>
                            (p.GetComponent<Collider>().ClosestPoint(world) - world).sqrMagnitude +
                            (p.GetComponent<Collider>().bounds.center - world).sqrMagnitude * .0001f).First();
                        var anchor = new GameObject("ResoluteAftRaftAnchor_" + found.Count).transform;
                        anchor.SetParent(owner.transform, false);
                        anchor.SetPositionAndRotation(world, visual.rotation);
                        found.Add(anchor); stations.Add(station);
                    }
            component.gates = found.ToArray(); component.anchors = new Transform[StationCount];
            component.openingAngles = stations.Select(p => p.x > 0f ? -90f : 90f).ToArray();
            for (int i = 0; i < StationCount; i++)
            {
                var marker = new GameObject("ResoluteLiferaftStation_" + i);
                marker.transform.SetParent(visual, false); marker.transform.localPosition = stations[i]; component.anchors[i] = marker.transform;
            }
        }

        // Resolute_Raft_0.1.0 used a second winding reversal while exporting
        // already Y-up coordinates. Its 346 faces are opposite their authored
        // outward normals. Correct this cloned visual once; preserve geometry,
        // normals, UVs and the original on-disk asset. No double-sided shader.
        internal static void CorrectOriginalWinding(Mesh mesh)
        {
            Vector3[] positions = mesh.vertices, normals = mesh.normals;
            int[] triangles = mesh.triangles;
            bool changed = false;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                Vector3 outward = normals[a] + normals[b] + normals[c];
                if (Vector3.Dot(face, outward) >= 0f) continue;
                triangles[i + 1] = c; triangles[i + 2] = b; changed = true;
            }
            if (changed) mesh.triangles = triangles;
        }

        // Run after the static-art ownership pass. Flat native reference arrays
        // survive prefab cloning, unlike runtime-created nested managed records.
        internal static void Complete(Ship ship)
        {
            if (!Enabled) return;
            var component = ship.GetComponent<ResoluteLiferafts>();
            if (component == null || component.gates.Length != StationCount) throw new InvalidOperationException("Resolute liferaft stations were not prepared.");
            component.supports = new UnitPart[StationCount]; component.closedRotations = new Quaternion[StationCount];
            for (int i = 0; i < StationCount; i++)
            {
                // Construction runs below an inactive template root.
                UnitPart part = component.gates[i].GetComponentInParent<UnitPart>(true);
                if (part == null) throw new InvalidOperationException("Liferaft gate has no native structural owner.");
                component.supports[i] = part; component.closedRotations[i] = component.gates[i].localRotation;
                component.anchors[i].SetParent(part.transform, true);
            }
        }

        private void Awake() { ship = GetComponent<Ship>(); }
        private void OnEnable()
        {
            if (ship == null) ship = GetComponent<Ship>();
            if (ship != null) ship.onDisableUnit += BeginEvacuation;
            enabled = true;
        }
        private void OnDisable() { if (ship != null) ship.onDisableUnit -= BeginEvacuation; }
        private void BeginEvacuation(Unit unit)
        {
            if (started || ship == null || !ship.disabled || !MissionManager.IsRunning || ship.NetworkHQ == null ||
                gates == null || gates.Length != StationCount || definition == null) return;
            deploymentCount = Mathf.Min(StationCount, Mathf.FloorToInt(ship.definition.manpower / CrewPerRaft));
            if (deploymentCount <= 0) return;
            started = true; startedAt = Time.timeSinceLevelLoad;
        }
        private void Update()
        {
            if (!Enabled) { enabled = false; return; }
            if (!started) { if (ship != null && ship.disabled) BeginEvacuation(ship); return; }
            if (!MissionManager.IsRunning || ship == null) return;
            float elapsed = Time.timeSinceLevelLoad - startedAt;
            for (int i = 0; i < deploymentCount; i++)
            {
                Transform gate = gates[i];
                if (gate == null) continue;
                float opening = Mathf.Clamp01((elapsed - i * LaunchSpacing) / GateSeconds);
                gate.localRotation = closedRotations[i] * Quaternion.AngleAxis(openingAngles[i] * opening, Vector3.forward);
            }
            if (nextDeployment >= deploymentCount) { enabled = false; return; }
            if (elapsed < InitialDelay + nextDeployment * LaunchSpacing) return;
            int index = nextDeployment++;
            if (ship.IsServer) Deploy(index);
        }
        private void Deploy(int index)
        {
            if (!ship.IsServer || ship.NetworkHQ == null ||
                ResoluteRaftLifetime.LiveCount >= GlobalLimit) { SkippedStations++; return; }
            Vector3 position;
            Quaternion rotation;
            if (!FindDeploymentPosition(out position, out rotation)) { SkippedStations++; return; }
            GameObject raft = null;
            try
            {
                raft = Object.Instantiate(definition.unitPrefab, position, rotation);
                PilotDismounted pilot = raft.GetComponent<PilotDismounted>();
                pilot.NetworkparentUnit = ship.persistentID; pilot.NetworkpilotNumber = (byte)index;
                pilot.NetworkHQ = ship.NetworkHQ; pilot.NetworkunitName = ship.unitName + " life raft " + (index + 1);
                pilot.NetworkstartPosition = position.ToGlobalPosition(); pilot.NetworkstartRotation = rotation;
                pilot.NetworkUniqueName = "resolute_raft_" + ship.persistentID + "_" + index;
                ship.ServerObjectManager.Spawn(raft);
                SpawnedRafts++;
            }
            catch (Exception error)
            {
                if (raft != null) Object.Destroy(raft);
                SkippedStations++; Debug.LogWarning("Resolute liferaft deployment failed: " + error.Message);
            }
        }

        private bool FindDeploymentPosition(out Vector3 position, out Quaternion rotation)
        {
            position = default(Vector3); rotation = Quaternion.identity;
            // Exact QoL 1.1.8.1 generation pattern: random sea-level bearing and
            // radius from 0.5 to 1.5 times the ship's greatest horizontal dimension.
            // Authored gates still open on schedule, but no longer force four-metre
            // rafts into 1.5-metre station spacing or the rolled wreck's footprint.
            int mask = ~((int)PhysicsLayers.WaterMask | (int)PhysicsLayers.ExclusionZonesMask |
                (int)PhysicsLayers.IgnoreCollisionsMask | (int)PhysicsLayers.UIMask | (int)PhysicsLayers.EffectsMask);
            for (int attempt = 0; attempt < ResoluteRaftPlacement.Attempts; attempt++)
            {
                position = ResoluteRaftPlacement.Candidate(ship.transform.position, ship.definition.length, ship.definition.width,
                    UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(0f, 1f), Datum.LocalSeaY);
                rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
                if (!ResoluteRaftLifetime.SpaceAvailable(position)) continue;
                RaycastHit terrain;
                if (Physics.Raycast(position + Vector3.up * 40f, Vector3.down, out terrain, 100f, PhysicsLayers.StaticsMask) &&
                    terrain.point.y > Datum.LocalSeaY - .5f) continue;
                // Test the real raft body plus a small margin against the current
                // wreck, other vessels, debris and previous raft bodies.
                if (Physics.CheckBox(position + ResoluteRaftPlacement.BodyCenter, ResoluteRaftPlacement.ClearanceHalfExtents, rotation,
                    mask, QueryTriggerInteraction.Ignore)) continue;
                return true;
            }
            return false;
        }

        private static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.FullName, name); }
        private static void ConfigureIdentity(GameObject prefab)
        {
            NetworkIdentity identity = prefab.GetComponent<NetworkIdentity>();
            if (identity == null) throw new InvalidOperationException("Native raft donor has no network identity.");
            int hash;
            unchecked { uint value = 2166136261; foreach (char c in "com.resolute.nuclearoption:rsl_liferaft:root") { value ^= c; value *= 16777619; } hash = (int)value; }
            if (Resources.FindObjectsOfTypeAll<NetworkIdentity>().Any(i => i != identity && i.PrefabHash == hash))
                throw new InvalidOperationException("Resolute liferaft network hash is already occupied.");
            identity.ClearSceneId(); identity.PrefabHash = hash;
            Field(typeof(NetworkIdentity), "_hasSpawned").SetValue(identity, false); identity.ClearNetworkBehaviourCache();
        }
        private static Sprite LoadIcon(string folder, string file)
        {
            Texture2D texture = LoadTexture(folder, file, false);
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f));
            Object.DontDestroyOnLoad(sprite); return sprite;
        }
        private static Texture2D LoadTexture(string folder, string file, bool linear)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "Resolute_" + file };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(Path.Combine(folder, file))))
            { Object.Destroy(texture); throw new InvalidDataException("Invalid liferaft texture: " + file); }
            texture.Apply(true, true); Object.DontDestroyOnLoad(texture); return texture;
        }
    }

    internal sealed class ResoluteRaftLifetime : MonoBehaviour
    {
        private static readonly HashSet<ResoluteRaftLifetime> Live = new HashSet<ResoluteRaftLifetime>();
        internal static int LiveCount => Live.Count;
        private PilotDismounted pilot;
        private float expires, nextCheck;
        private void Awake() { pilot = GetComponent<PilotDismounted>(); expires = Time.timeSinceLevelLoad + 600f; }
        private void OnEnable() { Live.Add(this); }
        private void OnDestroy() { Live.Remove(this); }
        internal static bool SpaceAvailable(Vector3 position)
        {
            foreach (var raft in Live)
            {
                if (raft == null) continue;
                Vector3 offset = raft.transform.position - position;
                if (Mathf.Abs(offset.y) > 4f) continue;
                if (ResoluteRaftPlacement.Overlaps(offset)) return false;
            }
            return true;
        }
        private void Update()
        {
            float now = Time.timeSinceLevelLoad;
            if (now < nextCheck) return; nextCheck = now + 1f;
            // Native rescue/capture/damage continues normally. Do not erase a
            // raft actively carried by a helicopter at its ordinary expiry.
            if (now >= expires && pilot != null && pilot.IsServer && !pilot.IsSlung()) Object.Destroy(gameObject);
        }
    }
}
