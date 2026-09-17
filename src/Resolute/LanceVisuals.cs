using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Presentation adapter for the approved Sea Power revision 09 artwork.
    // Flight ownership stays with the caller. All input speeds are actual m/s.
    internal sealed class LanceVisuals : MonoBehaviour
    {
        private static readonly BepInEx.Logging.ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Resolute Lance visuals");
        [SerializeField] private Transform assembly, cruise, booster, adapter;
        [SerializeField] private VLSBooster nativeBooster;
        [SerializeField] private Material smokeMaterial;
        private LanceExhaustStage cruiseEffect, boostEffect;
        private Renderer heat, shock;
        private LanceSmoke smoke;
        private bool flying, separated, boostBound, boostStarted, cruiseStarted, boostStopped, cruiseStopped;
        private bool boostBurningInput, cruiseBurningInput;
        private float boostIgnitionTime, cruiseIgnitionTime;
        private float heatStrength = -1f;
        private bool heatVisible;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        internal static LanceVisuals Attach(GameObject prefab, Encyclopedia encyclopedia, string assetFolder)
        {
            if (prefab.activeInHierarchy) throw new ArgumentException("Lance visual template must be inactive.");
            var paint = new MissileVisuals(assetFolder, encyclopedia);
            GameObject root = VisualLoader.LoadFromFolder(assetFolder, "LanceVisual", prefab.transform, Log, paint.CreateMaterial);
            // Each stage owns its distance culling after separation. A single
            // assembly LOD group would continue controlling the detached skin.
            foreach (var lod in root.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lod);
            var value = prefab.AddComponent<LanceVisuals>();
            value.assembly = root.transform;
            value.cruise = Group(root.transform, "LanceCruise");
            value.booster = Group(root.transform, "LanceBooster");
            value.adapter = Group(root.transform, "LanceAdapter");
            var donor = encyclopedia.missiles.FirstOrDefault(x => x != null && x.jsonKey == "AShM1" && x.unitPrefab != null);
            if (donor != null)
            {
                foreach (var trail in donor.unitPrefab.GetComponentsInChildren<TrailEmitter>(true))
                {
                    var system = NaturalWeapons.Get(trail, "trailSystem") as ParticleSystem;
                    var renderer = system == null ? null : system.GetComponent<ParticleSystemRenderer>();
                    if (renderer != null && renderer.sharedMaterial != null) { value.smokeMaterial = renderer.sharedMaterial; break; }
                }
            }
            FitNativeEffects(prefab.GetComponent<Missile>());
            return value;
        }

        internal static void FitNativeEffects(Missile missile)
        {
            if (missile == null || missile.transform.Find("Lance native cruise nozzle") != null) return;
            VLSBooster native = missile.GetComponentInChildren<VLSBooster>(true);
            if (native != null)
            {
                var nozzle = new GameObject("Lance native boost nozzle").transform;
                nozzle.SetParent(native.transform, false); nozzle.localPosition = new Vector3(0f, 0f, -4.651f);
                FitComponents(native, new[] { "particleSystems", "audioSources", "lights" }, nozzle, missile, null);
                FitTrails(NaturalWeapons.Get(native, "trailEmitters") as Array, nozzle, missile);
            }
            var cruiseNozzle = new GameObject("Lance native cruise nozzle").transform;
            cruiseNozzle.SetParent(missile.transform, false); cruiseNozzle.localPosition = new Vector3(0f, -.012f, -2.002f);
            Array motors = NaturalWeapons.Get(missile, "motors") as Array;
            if (motors == null) return;
            foreach (object motor in motors)
            {
                FitComponents(motor, new[] { "particleSystems", "audioSources", "lights" }, cruiseNozzle, missile, native);
                FitTrails(NaturalWeapons.Get(motor, "trailEmitters") as Array, cruiseNozzle, missile);
                AudioSource startup = NaturalWeapons.Get(motor, "startupSource") as AudioSource;
                if (startup != null && startup.transform != missile.transform && (native == null || !startup.transform.IsChildOf(native.transform)))
                { startup.transform.SetParent(cruiseNozzle, true); startup.transform.localPosition = Vector3.zero; }
            }
        }

        private static void FitComponents(object owner, string[] fields, Transform nozzle, Missile missile, VLSBooster exclude)
        {
            var parts = new HashSet<Transform>();
            foreach (string field in fields)
            {
                Array values = NaturalWeapons.Get(owner, field) as Array; if (values == null) continue;
                foreach (Component item in values)
                {
                    if (item == null || item.transform == missile.transform || item.transform == nozzle.parent ||
                        !item.transform.IsChildOf(missile.transform) || (exclude != null && item.transform.IsChildOf(exclude.transform))) continue;
                    parts.Add(item.transform);
                }
            }
            // Move only the highest selected transform. Nested smoke/flame
            // offsets and every authored particle curve remain unchanged.
            foreach (Transform part in parts.Where(p => !parts.Any(other => p != other && p.IsChildOf(other))).ToArray())
            {
                Quaternion rotation = Quaternion.Inverse(missile.transform.rotation) * part.rotation;
                part.SetParent(nozzle, true); part.localPosition = Vector3.zero; part.localRotation = rotation;
            }
        }

        private static void FitTrails(Array trails, Transform nozzle, Missile missile)
        {
            if (trails == null) return;
            foreach (TrailEmitter trail in trails)
            {
                if (trail == null) continue;
                NaturalWeapons.Set(trail, "emitTransform", nozzle); trail.rb = missile.GetComponent<Rigidbody>();
            }
        }

        private static Transform Group(Transform root, string name)
        {
            var group = new GameObject(name).transform; group.SetParent(root, false);
            foreach (var part in root.GetComponentsInChildren<MeshRenderer>(true))
                if (part.name.StartsWith(name + "_", StringComparison.Ordinal)) part.transform.SetParent(group, false);
            return group;
        }

        internal void BindBooster(VLSBooster value) { nativeBooster = value; }

        internal void ApplyObserverBank(Vector3 worldUp)
        {
            if (assembly == null) return;
            Vector3 up = Vector3.ProjectOnPlane(worldUp, transform.forward);
            if (up.sqrMagnitude < .000001f || float.IsNaN(up.sqrMagnitude) || float.IsInfinity(up.sqrMagnitude)) return;
            Quaternion rotation = Quaternion.LookRotation(transform.forward, up.normalized);
            // Rotate only the visual hierarchy around the unchanged missile
            // forward axis. Native network pose and Rigidbody stay untouched.
            assembly.rotation = rotation;
            if (!separated && boostBound && booster != null && nativeBooster != null &&
                nativeBooster.transform.IsChildOf(transform) && booster.IsChildOf(nativeBooster.transform))
                booster.rotation = rotation;
        }

        private void Awake()
        {
            if (cruise == null || booster == null || adapter == null) return;
            if (assembly == null) assembly = cruise.parent;
            cruiseEffect = LanceExhaustStage.Create(cruise, false);
            boostEffect = LanceExhaustStage.Create(booster, true);
            heat = LanceEffectResources.Render(cruise, "Lance restrained heat", LanceEffectResources.Heat, LanceEffectResources.Glow);
            shock = LanceEffectResources.Render(cruise, "Lance faint shock sheath", LanceEffectResources.Shock, LanceEffectResources.Glow);
            foreach (Transform stage in new[] { cruise, booster, adapter })
            {
                var lod = stage.gameObject.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(.001f, stage.GetComponentsInChildren<Renderer>(true)) });
                lod.RecalculateBounds(); lod.fadeMode = LODFadeMode.None;
            }
            ResetFlight();
        }

        internal void ResetFlight()
        {
            flying = separated = boostBound = boostStarted = cruiseStarted = boostStopped = cruiseStopped = false;
            boostBurningInput = cruiseBurningInput = false;
            heatStrength = -1f; heatVisible = false;
            if (assembly != null) assembly.localRotation = Quaternion.identity;
            if (cruiseEffect != null) cruiseEffect.ResetEmission();
            if (boostEffect != null) boostEffect.ResetEmission();
            if (heat != null) heat.enabled = false;
            if (shock != null) shock.enabled = false;
            if (smoke != null) { smoke.End(); smoke = null; }
        }

        internal void UpdateFlight(bool launched, bool isSeparated, bool boostBurning, bool cruiseBurning,
            float airSpeedMps, float altitudeM, Vector3 velocity)
        {
            if (cruiseEffect == null) return;
            if (!launched) { if (flying) ResetFlight(); return; }
            if (!flying)
            {
                flying = true;
            }
            if (!boostBound && nativeBooster != null && booster != null)
            {
                booster.SetParent(nativeBooster.transform, true); boostBound = true;
            }
            if (isSeparated && !separated)
            {
                separated = true;
                if (smoke != null) { smoke.End(); smoke = null; }
                boostStopped = true;
                if (boostEffect != null) boostEffect.Shutdown();
                if (!boostBound && booster != null) LanceSpentVisual.Detach(booster, velocity - transform.forward * 20f - transform.right * 3f, 22f);
                if (adapter != null) LanceSpentVisual.Detach(adapter, velocity - transform.forward * 20f + transform.right * 3f, -31f);
            }
            float expansion = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(5000f, 25000f, altitudeM));
            if (separated) cruiseEffect.Expansion(expansion);
            if (!separated && boostEffect != null) boostEffect.Expansion(expansion);
            // Structural separation does not imply a working cruise engine.
            // The caller supplies active-stage + remaining-fuel flags rather
            // than instantaneous thrust, which can be zero at equilibrium.
            boostBurningInput = boostBurning && !separated;
            cruiseBurningInput = cruiseBurning && separated;
            if (boostBurningInput && !boostStarted && !boostStopped)
            {
                boostStarted = true; boostIgnitionTime = Time.time;
                if (smokeMaterial != null) smoke = LanceSmoke.Create(transform, smokeMaterial);
            }
            if (boostStarted && !boostStopped)
            {
                if (boostBurningInput) boostEffect.Brightness(Mathf.SmoothStep(0f, 1f, (Time.time - boostIgnitionTime) / .35f));
                else
                {
                    boostStopped = true; boostEffect.Shutdown();
                    if (smoke != null) { smoke.End(); smoke = null; }
                }
            }
            if (cruiseBurningInput && !cruiseStarted && !cruiseStopped)
            { cruiseStarted = true; cruiseIgnitionTime = Time.time; }
            if (cruiseStarted && !cruiseStopped)
            {
                if (cruiseBurningInput) cruiseEffect.Brightness(Mathf.SmoothStep(0f, 1f, (Time.time - cruiseIgnitionTime) / .25f));
                else { cruiseStopped = true; cruiseEffect.Shutdown(); }
            }
            float strength = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(4f, 8f, airSpeedMps / 340.294f)) *
                (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30000f, 60000f, altitudeM)));
            // Art-only gain: modest in cruise, much stronger in a fast dive.
            // Use observed motion on both host and clients, without changing
            // radar/IR signatures or extending the network state packet.
            float descent = separated ? Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(.2f, .75f, -velocity.normalized.y)) : 0f;
            float hot = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 5f, airSpeedMps / 340.294f));
            float heatGain = strength * 1.5f + descent * hot * 3.5f;
            if (heatGain != heatStrength)
            {
                heatStrength = heatGain;
                block.Clear(); block.SetColor("_BaseColor", new Color(heatGain, heatGain, heatGain, 1f));
                heat.SetPropertyBlock(block);
                block.SetColor("_BaseColor", new Color(strength, strength, strength, 1f)); shock.SetPropertyBlock(block);
            }
            bool visible = heatGain > .001f;
            if (visible != heatVisible)
            { heatVisible = visible; heat.enabled = shock.enabled = visible; }
        }

        internal object Capture() => new Dictionary<string, object> {
            ["flying"] = flying, ["separated"] = separated, ["heatGain"] = heatStrength,
            ["boostBurningInput"] = boostBurningInput, ["cruiseBurningInput"] = cruiseBurningInput,
            ["boostStarted"] = boostStarted, ["cruiseStarted"] = cruiseStarted,
            ["boostStopped"] = boostStopped, ["cruiseStopped"] = cruiseStopped,
            ["boostBrightness"] = boostEffect != null ? boostEffect.CurrentBrightness : 0f,
            ["cruiseBrightness"] = cruiseEffect != null ? cruiseEffect.CurrentBrightness : 0f
        };

        private void OnDisable()
        {
            if (smoke != null) { smoke.End(); smoke = null; }
            if (cruiseEffect != null) cruiseEffect.Brightness(0f);
            if (boostEffect != null) boostEffect.Shutdown();
        }
    }

    internal static class LanceEffectResources
    {
        private static Material glow, backing;
        private static readonly Mesh[] meshes = new Mesh[8];
        internal static Mesh Heat { get { return meshes[6] ?? (meshes[6] = Keep(LanceHeatGeometry.Create(false))); } }
        internal static Mesh Shock { get { return meshes[7] ?? (meshes[7] = Keep(LanceHeatGeometry.Create(true))); } }
        internal static Mesh StageMesh(bool boost, int part)
        {
            int i = (boost ? 3 : 0) + part;
            return meshes[i] ?? (meshes[i] = Keep(part == 0 ? LancePlumeGeometry.CreateLiner(boost) : part == 1 ? LancePlumeGeometry.CreateOuter(boost) : LancePlumeGeometry.CreateCore(boost)));
        }
        private static Mesh Keep(Mesh value) { Object.DontDestroyOnLoad(value); return value; }
        internal static Material Glow { get { return glow ?? (glow = Material(true)); } }
        internal static Material Backing { get { return backing ?? (backing = Material(false)); } }
        private static Material Material(bool additive)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) throw new InvalidOperationException("Native unlit particle shader missing for Lance.");
            var m = new Material(shader) { name = additive ? "Lance owned non-fading glow" : "Lance exact opaque cavity", renderQueue = additive ? 3000 : 2000 };
            m.SetTexture("_BaseMap", Texture2D.whiteTexture);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Surface", additive ? 1f : 0f); m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetFloat("_SrcBlend", additive ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
            m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.Zero);
            m.SetFloat("_ZWrite", additive ? 0f : 1f); m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetFloat("_ColorMode", 0f);
            if (additive) m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); else m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            foreach (string keyword in new[] { "_ALPHAPREMULTIPLY_ON", "_SOFTPARTICLES_ON", "_FADING_ON", "_DISTORTION_ON", "_FLIPBOOKBLENDING_ON", "_COLOROVERLAY_ON", "_COLORCOLOR_ON", "_COLORADDSUBDIFF_ON" }) m.DisableKeyword(keyword);
            m.SetFloat("_SoftParticlesEnabled", 0f); m.SetFloat("_CameraFadingEnabled", 0f);
            Object.DontDestroyOnLoad(m); return m;
        }
        internal static MeshRenderer Render(Transform root, string name, Mesh mesh, Material material)
        {
            var item = new GameObject(name); item.layer = root.gameObject.layer; item.transform.SetParent(root, false);
            item.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = item.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return renderer;
        }
    }

    internal sealed class LanceExhaustStage : MonoBehaviour
    {
        // Uniform XYZ enlargement of the complete installed-09 booster flame.
        // A 0.452 m root remains inside the measured 0.464 m mouth.
        internal const float BoosterPlumeScale = 1.12f;
        private static readonly Vector3 BoosterMouth = new Vector3(0f, 0f, -4.651f);
        private Renderer liner, light, outer, core;
        private Mesh[] morph = new Mesh[2];
        private Vector3[][] low = new Vector3[2][], high = new Vector3[2][], work = new Vector3[2][];
        private float expansion = -1f, brightness = -1f, shutdown = -1f, shutdownBrightness;
        private bool boost;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        internal static LanceExhaustStage Create(Transform parent, bool boost)
        {
            var root = new GameObject(boost ? "Lance boost effects" : "Lance cruise effects"); root.transform.SetParent(parent, false); root.layer = parent.gameObject.layer;
            var value = root.AddComponent<LanceExhaustStage>(); value.boost = boost;
            Mesh cavity = LanceEffectResources.StageMesh(boost, 0);
            value.liner = LanceEffectResources.Render(root.transform, "Exact depth cavity", cavity, LanceEffectResources.Backing);
            value.light = LanceEffectResources.Render(root.transform, "Exact cavity luminous pass", cavity, LanceEffectResources.Glow);
            for (int i = 0; i < 2; i++)
            {
                Mesh source = LanceEffectResources.StageMesh(boost, i + 1);
                value.morph[i] = Object.Instantiate(source); value.morph[i].MarkDynamic();
                value.high[i] = source.vertices; value.low[i] = LancePlumeGeometry.LowVertices(boost, i == 1); value.work[i] = new Vector3[value.high[i].Length];
                if (boost) for (int v = 0; v < value.high[i].Length; v++)
                {
                    value.high[i][v] = BoosterMouth + (value.high[i][v] - BoosterMouth) * BoosterPlumeScale;
                    value.low[i][v] = BoosterMouth + (value.low[i][v] - BoosterMouth) * BoosterPlumeScale;
                }
                var bounds = new Bounds(value.high[i][0], Vector3.zero);
                foreach (var p in value.high[i]) bounds.Encapsulate(p);
                foreach (var p in value.low[i]) bounds.Encapsulate(p); value.morph[i].bounds = bounds;
                Renderer renderer = LanceEffectResources.Render(root.transform, i == 0 ? "Fitted envelope" : "Fitted core", value.morph[i], LanceEffectResources.Glow);
                if (i == 0) value.outer = renderer; else value.core = renderer;
            }
            value.Brightness(0f); value.Expansion(0f); return value;
        }
        internal void Brightness(float value)
        {
            if (brightness == value) return; brightness = value;
            block.Clear(); block.SetColor("_BaseColor", new Color(.06f, .06f, .06f, 1f)); liner.SetPropertyBlock(block);
            float radiance = (boost ? 6f : 7f) * value;
            block.SetColor("_BaseColor", new Color(radiance, radiance, radiance, 1f)); light.SetPropertyBlock(block);
            float plumeGain = (boost ? 3.5f : 3f) * value;
            block.SetColor("_BaseColor", new Color(plumeGain, plumeGain, plumeGain, 1f)); outer.SetPropertyBlock(block);
            float coreGain = (boost ? 5f : 4f) * value;
            block.SetColor("_BaseColor", new Color(coreGain, coreGain, coreGain, 1f)); core.SetPropertyBlock(block);
            light.enabled = outer.enabled = core.enabled = value > 0f;
        }
        internal void Expansion(float value)
        {
            if (Mathf.Abs(value - expansion) < .002f) return; expansion = value;
            for (int layer = 0; layer < 2; layer++)
            {
                for (int i = 0; i < work[layer].Length; i++) work[layer][i] = Vector3.LerpUnclamped(low[layer][i], high[layer][i], value);
                morph[layer].vertices = work[layer];
            }
        }
        internal float CurrentBrightness => Mathf.Max(0f, brightness);
        internal void ResetEmission() { shutdown = -1f; shutdownBrightness = 0f; Brightness(0f); }
        internal void Shutdown()
        {
            if (shutdown >= 0f) return;
            shutdown = Time.time; shutdownBrightness = Mathf.Clamp01(brightness);
        }
        private void LateUpdate()
        {
            if (shutdown >= 0f) Brightness(shutdownBrightness * (1f - Mathf.SmoothStep(0f, 1f, (Time.time - shutdown) / .18f)));
        }
        private void OnDestroy() { foreach (var mesh in morph) if (mesh != null) Object.Destroy(mesh); }
    }

    internal sealed class LanceSpentVisual : MonoBehaviour
    {
        private Vector3 velocity;
        private float age, spin;
        internal static void Detach(Transform part, Vector3 velocity, float spin)
        {
            part.SetParent(Datum.origin, true);
            var motion = part.gameObject.AddComponent<LanceSpentVisual>(); motion.velocity = velocity; motion.spin = spin;
        }
        private void LateUpdate()
        {
            float dt = Time.deltaTime; age += dt;
            if (age >= 6f) { Destroy(gameObject); return; }
            transform.position += velocity * dt + Physics.gravity * (.5f * dt * dt);
            velocity += Physics.gravity * dt; transform.Rotate(0f, spin * .35f * dt, spin * dt, Space.Self);
        }
    }
}
