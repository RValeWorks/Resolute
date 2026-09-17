using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    internal sealed class NaturalWeaponEffects : MonoBehaviour
    {
        public Transform Nozzle;
        public float LaunchTail, FlightTail, SwitchSeconds;
        public float LaunchNozzleDiameter, FlightNozzleDiameter;
        public ParticleSystem[] FittedFlames;
        public float[] FittedOriginalSizes;
        public Renderer TerminalFlame;
        public Renderer TerminalNozzleGlow;
        public AudioSource TerminalSound;
        public float TerminalBrightness, TerminalTemperature, TerminalGlowBrightness;
        public float SourceNozzleOpeningDiameter, TerminalGlowDiameter;
        public Texture NativeGlowEmissionTexture;
        private Missile missile;
        private NaturalWeaponPhase phase;
        private MaterialPropertyBlock flameProperties;
        private Material nozzleGlowMaterial;
        private float terminalAmount;
        private int previousStage;
        private int afterburnerMotorStage;
        private bool nozzleSeparated;
        internal bool AfterburnerVisible;
        internal float AfterburnerAmount => terminalAmount;
        internal bool NativeNozzleGlowReady => TerminalNozzleGlow != null && TerminalNozzleGlow.enabled && nozzleGlowMaterial != null &&
            nozzleGlowMaterial.IsKeywordEnabled("_EMISSION") && NativeGlowEmissionTexture != null &&
            nozzleGlowMaterial.GetTexture("_EmissionMap") == NativeGlowEmissionTexture &&
            Mathf.Abs(nozzleGlowMaterial.GetColor(EmissionProperty).r - terminalAmount * TerminalGlowBrightness) < .02f &&
            TerminalGlowDiameter <= SourceNozzleOpeningDiameter * 1.001f;
        private static readonly int BrightnessProperty = Shader.PropertyToID("_Brightness");
        private static readonly int TemperatureProperty = Shader.PropertyToID("_Temperature");
        private static readonly int EmissionProperty = Shader.PropertyToID("_EmissionColor");
        private static readonly AccessTools.FieldRef<Missile, int> Stage = AccessTools.FieldRefAccess<Missile, int>("motorStage");

        internal static void Configure(Missile missile, NaturalWeapons.SourceWeapon source)
        {
            Transform effects = new GameObject("OriginalMissileEffects").transform;
            effects.SetParent(missile.transform, false);
            NaturalWeapons.Set(missile, "effectsTransform", effects);
            var nozzle = new GameObject("SourceEngineNozzle").transform;
            nozzle.SetParent(effects, false);
            nozzle.SetPositionAndRotation(missile.transform.TransformPoint(new Vector3(0f, 0f, source.stages["launch"].min[2])), missile.transform.rotation);
            var transforms = new HashSet<Transform>();
            var allowedAudio = new HashSet<AudioSource>();
            bool nativeSurfaceLaunch = source.key == "rsl_cruise" || source.key == "rsl_ashm";
            VLSBooster[] nativeBoosters = nativeSurfaceLaunch ? missile.GetComponentsInChildren<VLSBooster>(true) : new VLSBooster[0];
            Func<Transform, bool> boosterOwned = t => nativeBoosters.Any(b => t == b.transform || t.IsChildOf(b.transform));
            foreach (VLSBooster booster in nativeBoosters)
                foreach (AudioSource audio in (Array)NaturalWeapons.Get(booster, "audioSources"))
                    if (audio != null) allowedAudio.Add(audio);
            int motorIndex = 0;
            foreach (object motor in (Array)NaturalWeapons.Get(missile, "motors"))
            {
                if (source.key == "rsl_ashm") RemovePikeDonorIgnition(motor, boosterOwned);
                float burnTime = (float)NaturalWeapons.Get(motor, "burnTime");
                foreach (ParticleSystem particles in (ParticleSystem[])NaturalWeapons.Get(motor, "particleSystems"))
                {
                    if (particles == null || boosterOwned(particles.transform)) continue;
                    var emission = particles.emission;
                    // AAM2's native booster flame ends after its short donor
                    // duration. Resolute's longer motor used to keep thrusting
                    // after that flame had gone dark. Continuous motor effects
                    // now run until native Motor.Burnout stops them. The native
                    // surface sustainer has blue FireParticlesJetStart ignition
                    // particles: continuous emission within that short one-shot
                    // does not make it a persistent flame. Pike's single motor
                    // is now index zero too; preserve its native looping flags,
                    // as well as Spear's, instead of repeating the ignition over
                    // the existing aircraft afterburner for the entire flight.
                    if (!nativeSurfaceLaunch && motorIndex == 0 && emission.enabled && (emission.rateOverTime.constantMax > 0f || emission.rateOverDistance.constantMax > 0f))
                    { var main = particles.main; main.loop = true; }
                }
                foreach (string field in new[] { "particleSystems", "lights", "audioSources" })
                    foreach (Component item in (Array)NaturalWeapons.Get(motor, field))
                    {
                        if (item == null) continue;
                        AudioSource sound = item as AudioSource;
                        if (sound != null) allowedAudio.Add(sound);
                        if (item.transform != missile.transform) transforms.Add(item.transform);
                    }
                AudioSource startup = (AudioSource)NaturalWeapons.Get(motor, "startupSource");
                if (startup != null) { allowedAudio.Add(startup); if (startup.transform != missile.transform) transforms.Add(startup.transform); }
                foreach (TrailEmitter trail in (Array)NaturalWeapons.Get(motor, "trailEmitters"))
                {
                    if (trail == null) continue;
                    if (boosterOwned(trail.transform)) continue;
                    NaturalWeapons.Set(trail, "emitTransform", nozzle);
                    trail.rb = missile.GetComponent<Rigidbody>();
                    NaturalWeapons.Set(trail, "emitLifetime", Mathf.Max((float)NaturalWeapons.Get(trail, "emitLifetime"), burnTime + .5f));
                    // An attached VLS booster may have owned this trail object.
                    if (trail.transform != missile.transform) transforms.Add(trail.transform);
                }
                motorIndex++;
            }
            // Anchor the actual native motor components, rather than adding a
            // second aft offset to their original donor hierarchy.
            foreach (Transform transform in transforms.OrderBy(Depth))
            {
                if (transform == effects || transform == nozzle || !transform.IsChildOf(missile.transform) || boosterOwned(transform)) continue;
                Quaternion relativeRotation = Quaternion.Inverse(missile.transform.rotation) * transform.rotation;
                Vector3 scale = transform.lossyScale;
                transform.SetParent(nozzle, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = relativeRotation;
                transform.localScale = scale;
                ParticleSystem particles = transform.GetComponent<ParticleSystem>();
                if (particles != null) { var shape = particles.shape; shape.position = Vector3.zero; }
            }
            AudioSource flight = (AudioSource)NaturalWeapons.Get(missile, "flightSound");
            if (flight != null) allowedAudio.Add(flight);
            foreach (VLSBooster booster in missile.GetComponentsInChildren<VLSBooster>(true))
            {
                // Spear uses the actual native booster ownership and burnout
                // sequence. Its child effects must detach with that booster.
                if (nativeSurfaceLaunch) continue;
                if (booster.transform == missile.transform || nozzle.IsChildOf(booster.transform) || transforms.Contains(booster.transform)) Object.DestroyImmediate(booster);
                else Object.DestroyImmediate(booster.gameObject);
            }
            foreach (AudioSource audio in missile.GetComponentsInChildren<AudioSource>(true))
            {
                audio.Stop(); audio.playOnAwake = false;
                if (!allowedAudio.Contains(audio)) audio.enabled = false;
            }
            var binding = missile.gameObject.AddComponent<NaturalWeaponEffects>();
            binding.Nozzle = nozzle;
            binding.LaunchTail = source.stages["launch"].min[2];
            binding.FlightTail = source.stages["flight"].min[2];
            binding.SwitchSeconds = source.switchSeconds;
        }

        private static void RemovePikeDonorIgnition(object motor, Func<Transform, bool> boosterOwned)
        {
            ParticleSystem[] original = (ParticleSystem[])NaturalWeapons.Get(motor, "particleSystems");
            // AShM1's one-shot blue jet-start particles run on sustainer
            // Activate, after booster separation. They are a separate donor
            // effect from Pike's existing aircraft terminal afterburner.
            var ignition = new HashSet<ParticleSystem>(original.Where(p => p != null &&
                p.name == "FireParticlesJetStart" && !boosterOwned(p.transform))
                .SelectMany(p => p.GetComponentsInChildren<ParticleSystem>(true)));
            if (ignition.Count == 0) return;
            NaturalWeapons.Set(motor, "particleSystems", original.Where(p => !ignition.Contains(p)).ToArray());
            foreach (ParticleSystem particles in ignition)
            {
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = particles.main; main.playOnAwake = false;
                var emission = particles.emission; emission.enabled = false;
            }
        }
        internal static void FitNozzles(Missile missile)
        {
            NaturalWeaponEffects binding = missile.GetComponent<NaturalWeaponEffects>();
            NaturalWeaponPhase phase = missile.GetComponent<NaturalWeaponPhase>();
            binding.LaunchNozzleDiameter = MeasureNozzleOpening(phase.LaunchModel, missile.transform, binding.LaunchTail);
            binding.FlightNozzleDiameter = MeasureNozzleOpening(phase.FlightModel, missile.transform, binding.FlightTail);
            binding.FittedFlames = ((Array)NaturalWeapons.Get(missile, "motors")).Cast<object>()
                .SelectMany(m => (ParticleSystem[])NaturalWeapons.Get(m, "particleSystems"))
                .Where(p => p != null && p.name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0).Distinct().ToArray();
            binding.FittedOriginalSizes = binding.FittedFlames.Select(p => p.main.startSizeMultiplier).ToArray();
            binding.ApplyNozzleFit(binding.LaunchNozzleDiameter);
        }
        private void ApplyNozzleFit(float diameter)
        {
            if (FittedFlames == null) return;
            for (int i = 0; i < FittedFlames.Length; i++)
            {
                ParticleSystem particles = FittedFlames[i];
                // Emit across the actual opening instead of retaining an
                // almost-point emitter from a much narrower native missile.
                var shape = particles.shape;
                if (shape.enabled) shape.radius = diameter * .35f;
                var main = particles.main;
                main.startSizeMultiplier = Mathf.Max(FittedOriginalSizes[i], diameter * 1.5f);
            }
        }
        internal static void ConfigureAfterburner(Missile missile, NaturalWeapons.SourceWeapon source, Encyclopedia encyclopedia)
        {
            if (source.Guidance("TerminalVelocity") <= source.Guidance("MaxVelocity")) return;
            NaturalWeaponEffects binding = missile.GetComponent<NaturalWeaponEffects>();
            if (binding == null) return;
            JetNozzle donor = encyclopedia.aircraft.Where(d => d != null && d.unitPrefab != null)
                .OrderBy(d => string.Equals(d.jsonKey, "Fighter1", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .SelectMany(d => d.unitPrefab.GetComponentsInChildren<JetNozzle>(true))
                .FirstOrDefault(n => ((Array)NaturalWeapons.Get(n, "afterburners")).Length > 0);
            if (donor == null) throw new InvalidOperationException("No native afterburner reference for " + source.key);
            object native = ((Array)NaturalWeapons.Get(donor, "afterburners")).GetValue(0);
            Renderer sourceRenderer = (Renderer)NaturalWeapons.Get(native, "flameRenderer");
            MeshFilter sourceFilter = sourceRenderer != null ? sourceRenderer.GetComponent<MeshFilter>() : null;
            Transform thrust = (Transform)NaturalWeapons.Get(donor, "thrustTransform");
            if (sourceFilter == null || sourceFilter.sharedMesh == null || thrust == null)
                throw new InvalidOperationException("Native afterburner mesh is unavailable for " + source.key);

            // Reuse the native animated afterburner shader/mesh, normalized in
            // the native thrust frame, with its front at this missile's nozzle.
            // This avoids carrying an aircraft's several-metre aft offset.
            // Stock meshes may have discarded their CPU vertex buffer. Their
            // GPU mesh and local bounds remain usable without reading vertices.
            Mesh mesh = sourceFilter.sharedMesh;
            Matrix4x4 nativeFrame = thrust.worldToLocalMatrix * sourceFilter.transform.localToWorldMatrix;
            Bounds localBounds = mesh.bounds;
            Bounds bounds = new Bounds(nativeFrame.MultiplyPoint3x4(localBounds.center), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                bounds.Encapsulate(nativeFrame.MultiplyPoint3x4(localBounds.center + Vector3.Scale(localBounds.extents, new Vector3(x, y, z))));
            float width = Mathf.Max(.15f, source.stages["launch"].max[0] - source.stages["launch"].min[0]);
            float radiusScale = width * .72f / Mathf.Max(.01f, Mathf.Max(bounds.size.x, bounds.size.y));
            float length = Mathf.Clamp(source.stages["flight"].max[2] - source.stages["flight"].min[2], 3f, 8f) * .70f;
            float lengthScale = length / Mathf.Max(.01f, bounds.size.z);
            var normalized = new GameObject("TerminalAfterburnerMount"); normalized.transform.SetParent(binding.Nozzle, false);
            normalized.transform.localPosition = new Vector3(-bounds.center.x * radiusScale, -bounds.center.y * radiusScale, -bounds.max.z * lengthScale);
            normalized.transform.localScale = new Vector3(radiusScale, radiusScale, lengthScale);
            var flame = new GameObject("TerminalAfterburner"); flame.transform.SetParent(normalized.transform, false);
            flame.layer = sourceRenderer.gameObject.layer;
            flame.transform.localPosition = thrust.InverseTransformPoint(sourceFilter.transform.position);
            flame.transform.localRotation = Quaternion.Inverse(thrust.rotation) * sourceFilter.transform.rotation;
            Vector3 sourceScale = sourceFilter.transform.lossyScale, thrustScale = thrust.lossyScale;
            flame.transform.localScale = new Vector3(sourceScale.x / thrustScale.x, sourceScale.y / thrustScale.y, sourceScale.z / thrustScale.z);
            flame.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = flame.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = sourceRenderer.sharedMaterials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.enabled = false;
            binding.TerminalFlame = renderer;
            binding.TerminalBrightness = (float)NaturalWeapons.Get(native, "flameBrightness");
            binding.TerminalTemperature = (float)NaturalWeapons.Get(native, "temperature");
            // Native jet exhaust includes a separate emissive nozzle core.
            // The cone's native angle/depth shader fades from behind, so the
            // complete native glow is needed for the same rear-view behavior.
            Renderer nativeGlow = (Renderer)NaturalWeapons.Get(native, "nozzleGlowRenderer");
            MeshFilter glowFilter = nativeGlow != null ? nativeGlow.GetComponent<MeshFilter>() : null;
            if (glowFilter == null || glowFilter.sharedMesh == null)
                throw new InvalidOperationException("Native afterburner nozzle glow is unavailable for " + source.key);
            Matrix4x4 glowFrame = thrust.worldToLocalMatrix * glowFilter.transform.localToWorldMatrix;
            Bounds glowLocal = glowFilter.sharedMesh.bounds;
            Bounds glowBounds = new Bounds(glowFrame.MultiplyPoint3x4(glowLocal.center), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                glowBounds.Encapsulate(glowFrame.MultiplyPoint3x4(glowLocal.center + Vector3.Scale(glowLocal.extents, new Vector3(x, y, z))));
            binding.SourceNozzleOpeningDiameter = MeasureNozzleOpening(missile.GetComponent<NaturalWeaponPhase>().FlightModel,
                missile.transform, binding.FlightTail);
            binding.TerminalGlowDiameter = binding.SourceNozzleOpeningDiameter;
            float glowScale = binding.TerminalGlowDiameter / Mathf.Max(.01f, Mathf.Max(glowBounds.size.x, glowBounds.size.y));
            var glowMount = new GameObject("TerminalNozzleGlowMount"); glowMount.transform.SetParent(binding.Nozzle, false);
            // Keep the native core just aft of the verified source nozzle
            // plane, clear of the missile mesh's opaque nozzle cap.
            glowMount.transform.localPosition = new Vector3(-glowBounds.center.x * glowScale, -glowBounds.center.y * glowScale,
                -glowBounds.max.z * glowScale - .003f);
            glowMount.transform.localScale = Vector3.one * glowScale;
            var glow = new GameObject("TerminalNozzleGlow"); glow.transform.SetParent(glowMount.transform, false);
            glow.layer = nativeGlow.gameObject.layer;
            glow.transform.localPosition = thrust.InverseTransformPoint(glowFilter.transform.position);
            glow.transform.localRotation = Quaternion.Inverse(thrust.rotation) * glowFilter.transform.rotation;
            Vector3 glowSourceScale = glowFilter.transform.lossyScale;
            glow.transform.localScale = new Vector3(glowSourceScale.x / thrustScale.x, glowSourceScale.y / thrustScale.y, glowSourceScale.z / thrustScale.z);
            glow.AddComponent<MeshFilter>().sharedMesh = glowFilter.sharedMesh;
            var glowRenderer = glow.AddComponent<MeshRenderer>(); glowRenderer.sharedMaterials = nativeGlow.sharedMaterials;
            glowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; glowRenderer.receiveShadows = false;
            glowRenderer.enabled = false;
            binding.TerminalNozzleGlow = glowRenderer;
            binding.TerminalGlowBrightness = (float)NaturalWeapons.Get(native, "nozzleGlowBrightness");
            binding.NativeGlowEmissionTexture = nativeGlow.sharedMaterial.GetTexture("_EmissionMap");
            AudioSource nativeSound = (AudioSource)NaturalWeapons.Get(native, "source");
            if (nativeSound != null && nativeSound.clip != null)
            {
                AudioSource sound = flame.AddComponent<AudioSource>(); sound.clip = nativeSound.clip;
                sound.loop = true; sound.playOnAwake = false; sound.spatialBlend = 1f;
                sound.rolloffMode = nativeSound.rolloffMode; sound.minDistance = nativeSound.minDistance;
                sound.maxDistance = nativeSound.maxDistance; sound.dopplerLevel = nativeSound.dopplerLevel;
                sound.outputAudioMixerGroup = nativeSound.outputAudioMixerGroup;
                sound.volume = 0f; binding.TerminalSound = sound;
            }
        }
        private static float MeasureNozzleOpening(GameObject model, Transform root, float tail)
        {
            var points = new List<Vector2>();
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Matrix4x4 matrix = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    Vector3 p = matrix.MultiplyPoint3x4(vertex);
                    if (Mathf.Abs(p.z - tail) < .00015f && new Vector2(p.x, p.y).magnitude > .001f)
                        points.Add(new Vector2(p.x, p.y));
                }
            }
            if (points.Count == 0) throw new InvalidOperationException("Source missile nozzle plane has no measurable mouth ring.");
            float radius = points.Min(p => p.magnitude);
            Vector2[] ring = points.Where(p => Mathf.Abs(p.magnitude - radius) < .0002f).ToArray();
            if (ring.Length >= 12 && ring.Max(p => p.x) - ring.Min(p => p.x) >= radius * 1.9f &&
                ring.Max(p => p.y) - ring.Min(p => p.y) >= radius * 1.9f)
                return radius * 2f;
            return MeasureFacetedNozzleOpening(model, root, tail);
        }
        private static float MeasureFacetedNozzleOpening(GameObject model, Transform root, float tail)
        {
            // A bevelled polygon mouth has its lip at the tail plane and both
            // adjacent faces ahead of it. Weld the UV/normal splits, then trace
            // real mesh edges; angular sorting alone would accept disconnected
            // decorations. Keep the established circular fits unchanged above.
            var points = new List<Vector2>();
            var vertexIds = new Dictionary<Vector2Int, int>();
            var edges = new HashSet<long>();
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Matrix4x4 matrix = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                Vector3[] vertices = filter.sharedMesh.vertices;
                int[] ids = new int[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 p = matrix.MultiplyPoint3x4(vertices[i]);
                    ids[i] = -1;
                    if (Mathf.Abs(p.z - tail) >= .00015f) continue;
                    var key = new Vector2Int(Mathf.RoundToInt(p.x * 100000f), Mathf.RoundToInt(p.y * 100000f));
                    if (!vertexIds.TryGetValue(key, out int id))
                    { id = points.Count; vertexIds.Add(key, id); points.Add(new Vector2(p.x, p.y)); }
                    ids[i] = id;
                }
                int[] triangles = filter.sharedMesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = ids[triangles[i]], b = ids[triangles[i + 1]], c = ids[triangles[i + 2]];
                    if (a >= 0 && b >= 0 && c >= 0)
                        throw new InvalidOperationException("Faceted nozzle mouth requires an open bevelled lip, without a planar cap.");
                    if (a < 0) a = c;
                    else if (b < 0) b = c;
                    if (a < 0 || b < 0 || a == b) continue;
                    edges.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                }
            }
            var links = new Dictionary<int, List<int>>();
            foreach (long edge in edges)
            {
                int a = (int)(edge >> 32), b = (int)edge;
                if (!links.ContainsKey(a)) links.Add(a, new List<int>());
                if (!links.ContainsKey(b)) links.Add(b, new List<int>());
                links[a].Add(b); links[b].Add(a);
            }
            if (links.Count < 3 || links.Values.Any(list => list.Count != 2))
                throw new InvalidOperationException("Faceted nozzle mouth edges do not form closed loops.");
            var unseen = new HashSet<int>(links.Keys);
            float opening = float.PositiveInfinity;
            while (unseen.Count > 0)
            {
                int start = unseen.First(), previous = -1, current = start;
                var loop = new List<Vector2>();
                do
                {
                    if (!unseen.Remove(current)) throw new InvalidOperationException("Faceted nozzle lip is not a simple loop.");
                    loop.Add(points[current]);
                    List<int> next = links[current];
                    int selected = next[0] == previous ? next[1] : next[0];
                    previous = current; current = selected;
                } while (current != start);
                bool containsAxis = false;
                float radius = float.PositiveInfinity;
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % loop.Count], delta = b - a;
                    if ((a.y > 0f) != (b.y > 0f) && a.x - a.y * delta.x / delta.y > 0f)
                        containsAxis = !containsAxis;
                    float t = Mathf.Clamp01(-Vector2.Dot(a, delta) / delta.sqrMagnitude);
                    radius = Mathf.Min(radius, (a + t * delta).magnitude);
                }
                // The native flame is circular and axial. Its diameter must
                // fit the closest actual lip edge, including an offset mouth.
                if (containsAxis && radius > .001f) opening = Mathf.Min(opening, radius * 2f);
            }
            if (float.IsInfinity(opening)) throw new InvalidOperationException("Faceted nozzle mouth does not enclose its thrust axis.");
            return opening;
        }
        private static int Depth(Transform transform)
        {
            int result = 0; for (; transform != null; transform = transform.parent) result++; return result;
        }
        private void Awake()
        {
            missile = GetComponent<Missile>(); phase = GetComponent<NaturalWeaponPhase>();
            afterburnerMotorStage = ((Array)NaturalWeapons.Get(missile, "motors")).Length - 1;
            if (TerminalFlame != null) flameProperties = new MaterialPropertyBlock();
            // Allocate only on a live clone. The inactive encyclopedia/runtime
            // definition keeps the shared native material without mutation.
            if (TerminalNozzleGlow != null && gameObject.activeInHierarchy) nozzleGlowMaterial = TerminalNozzleGlow.material;
        }
        private void OnDestroy() { if (nozzleGlowMaterial != null) Object.Destroy(nozzleGlowMaterial); }
        private void LateUpdate()
        {
            if (missile == null || Nozzle == null) return;
            int stage = Stage(missile);
            UpdateAfterburner(stage);
            if (missile.disabled) return;
            if (stage != previousStage)
            {
                Array motors = (Array)NaturalWeapons.Get(missile, "motors");
                object previous = motors.GetValue(Mathf.Min(previousStage, motors.Length - 1));
                object current = stage < motors.Length ? motors.GetValue(stage) : null;
                foreach (string field in new[] { "particleSystems", "lights", "audioSources", "trailEmitters" })
                {
                    var retained = new HashSet<Component>(current != null ? ((Array)NaturalWeapons.Get(current, field)).Cast<Component>() : new Component[0]);
                    foreach (Component item in (Array)NaturalWeapons.Get(previous, field))
                    {
                        if (item == null || retained.Contains(item)) continue;
                        if (item is ParticleSystem particles) particles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                        if (item is AudioSource audio) audio.Stop();
                        if (item is Light light) light.enabled = false;
                        if (item is TrailEmitter trail) trail.StopTrail();
                    }
                }
                previousStage = stage;
            }
            bool separated = phase != null && phase.NativeCruisePipeline ? phase.FlightModelVisible :
                missile.timeSinceSpawn >= SwitchSeconds;
            if (separated != nozzleSeparated)
            { nozzleSeparated = separated; ApplyNozzleFit(separated ? FlightNozzleDiameter : LaunchNozzleDiameter); }
            float tail = separated ? FlightTail : LaunchTail;
            Nozzle.SetPositionAndRotation(transform.TransformPoint(new Vector3(0f, 0f, tail)), transform.rotation);
        }
        private void UpdateAfterburner(int stage)
        {
            if (TerminalFlame == null || flameProperties == null) return;
            bool active = !missile.disabled && phase != null && phase.TerminalBoostActive && stage == afterburnerMotorStage && missile.EngineOn();
            terminalAmount = Mathf.MoveTowards(terminalAmount, active ? 1f : 0f, Time.deltaTime * 4f);
            AfterburnerVisible = terminalAmount > .01f;
            TerminalFlame.enabled = AfterburnerVisible;
            if (TerminalNozzleGlow != null && nozzleGlowMaterial != null)
            {
                TerminalNozzleGlow.enabled = AfterburnerVisible;
                // Match native JetNozzle.Afterburner.Run exactly, including
                // the native HDR material property's color-space handling.
                nozzleGlowMaterial.SetColor(EmissionProperty, Color.white * terminalAmount * TerminalGlowBrightness);
            }
            if (AfterburnerVisible)
            {
                flameProperties.SetFloat(BrightnessProperty, TerminalBrightness * terminalAmount);
                flameProperties.SetFloat(TemperatureProperty, TerminalTemperature * terminalAmount);
                TerminalFlame.SetPropertyBlock(flameProperties);
                TerminalFlame.transform.localScale = new Vector3(1f, 1f, 1f + Mathf.PerlinNoise(Time.timeSinceLevelLoad * 15f, 0f) * .05f);
            }
            if (TerminalSound != null)
            {
                TerminalSound.volume = terminalAmount * .35f;
                if (active && !TerminalSound.isPlaying) TerminalSound.Play();
                if (!AfterburnerVisible && TerminalSound.isPlaying) TerminalSound.Stop();
            }
        }
    }
}
