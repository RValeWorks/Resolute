using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using NuclearOption.Effects;
using Object = UnityEngine.Object;

namespace Resolute
{
    internal static class NaturalWeaponAudit
    {
        internal static object Capture(Encyclopedia encyclopedia)
        {
            return encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                (d.jsonKey.StartsWith("rsl_") || d.unitPrefab.GetComponent<ARHSeeker>() != null ||
                new[] { "AShM1", "AAM1", "R9" }.Contains(d.unitPrefab.name, StringComparer.OrdinalIgnoreCase)))
                .Select(d => {
                    Missile missile = d.unitPrefab.GetComponent<Missile>();
                    var root = d.unitPrefab.transform;
                    return new Dictionary<string, object> {
                        ["key"] = d.jsonKey, ["name"] = d.unitName,
                        ["definition"] = Audit.Fields(d, root, 0),
                        ["missile"] = Audit.Fields(missile, root, 0),
                        ["seeker"] = Audit.Fields(d.unitPrefab.GetComponent<MissileSeeker>(), root, 0),
                        ["liftSamples"] = Enumerable.Range(0, 13).Select(n => new [] { n * 2.5f, missile.GetLiftCoeff(n * 2.5f * Mathf.Deg2Rad), missile.GetDragCoef(n * 2.5f * Mathf.Deg2Rad) }).ToArray(),
                        ["effects"] = d.unitPrefab.GetComponentsInChildren<Component>(true)
                            .Where(c => c is ParticleSystem || c is AudioSource || c is Light || c is TrailEmitter || c is VLSBooster)
                            .Select(c => new Dictionary<string, object> {
                                ["name"] = c.name, ["type"] = c.GetType().Name,
                                ["localToMissile"] = V(root.InverseTransformPoint(c.transform.position)),
                                ["localEulerToMissile"] = V((Quaternion.Inverse(root.rotation) * c.transform.rotation).eulerAngles),
                                ["parent"] = c.transform.parent != null ? c.transform.parent.name : null,
                                ["clip"] = c is AudioSource audio && audio.clip != null ? audio.clip.name : null,
                                ["enabled"] = c is Behaviour b ? b.enabled : true,
                                ["particleShapePosition"] = c is ParticleSystem ps ? V(ps.shape.position) : null,
                                ["particleShapeScale"] = c is ParticleSystem shape ? V(shape.shape.scale) : null,
                                ["fields"] = c is VLSBooster || c is TrailEmitter ? Audit.Fields(c, root, 0) : null
                            }).ToArray()
                    };
                }).ToArray();
        }
        internal static string CaptureFlight(Missile missile, string reportPath, string name, int angle = 0)
        {
            Vector3? focus = null;
            float distance = 0f;
            if (angle == 3)
            {
                Bounds envelope = new Bounds(missile.transform.position, Vector3.one * missile.definition.length);
                foreach (NaturalFlareOwner flare in NaturalWeaponObserver.OwnedFlares(missile))
                {
                    envelope.Encapsulate(flare.transform.position);
                    foreach (Vector3 glow in NativeFlareGlowPositions(flare)) envelope.Encapsulate(glow);
                }
                focus = envelope.center;
                distance = Mathf.Max(20f, envelope.extents.magnitude * 1.15f / Mathf.Tan(17.5f * Mathf.Deg2Rad));
            }
            return CaptureFrame(missile.transform, missile.definition.length, reportPath, name, angle, focus, distance);
        }
        internal static string CaptureNativeFlareDetail(Missile missile, NaturalFlareOwner[] pair,
            string reportPath, string name, out object framing)
        {
            framing = null;
            if (pair == null || pair.Length != 2 || pair.Any(f => f == null)) return null;
            Bounds envelope = new Bounds(pair[0].transform.position, Vector3.one * 2f);
            foreach (NaturalFlareOwner flare in pair)
            {
                Vector3[] glow = NativeFlareGlowPositions(flare);
                if (glow.Length == 0) return null;
                envelope.Encapsulate(flare.transform.position);
                foreach (Vector3 position in glow) envelope.Encapsulate(position);
            }
            envelope.Expand(2f);
            float distance = Mathf.Max(20f, envelope.extents.magnitude * 1.15f / Mathf.Tan(17.5f * Mathf.Deg2Rad));
            framing = new Dictionary<string, object> {
                ["focusGlobalPosition"] = V(envelope.center.ToGlobalPosition().AsVector3()),
                ["cameraDistanceMetres"] = distance,
                ["nativeFlareObjectIds"] = pair.Select(f => f.GetInstanceID()).ToArray(),
                ["unityFrame"] = Time.frameCount,
                ["scope"] = "Close view of only the same two live native flare roots and actual glow particle positions recorded in the wide view, captured in the same Unity frame. Camera framing changes only; particle size, brightness, lifetime and positions are untouched."
            };
            return CaptureFrame(missile.transform, missile.definition.length, reportPath, name, 3, envelope.center, distance);
        }
        internal static Vector3[] NativeFlareGlowPositions(NaturalFlareOwner owner)
        {
            IRFlare flare = owner != null ? owner.GetComponent<IRFlare>() : null;
            ParticleSystem glow = flare != null ? (ParticleSystem)NaturalWeapons.Get(flare, "flareParticles") : null;
            if (glow == null || glow.particleCount == 0) return new Vector3[0];
            var particles = new ParticleSystem.Particle[glow.particleCount];
            int count = glow.GetParticles(particles);
            var result = new Vector3[count];
            ParticleSystem.MainModule main = glow.main;
            Transform space = main.simulationSpace == ParticleSystemSimulationSpace.Custom ? main.customSimulationSpace : glow.transform;
            for (int i = 0; i < count; i++)
                result[i] = main.simulationSpace == ParticleSystemSimulationSpace.World ? particles[i].position : space.TransformPoint(particles[i].position);
            return result;
        }
        internal static string CaptureFrame(Transform frame, float length, string reportPath, string name, int angle = 0,
            Vector3? framingCenter = null, float framingDistance = 0f)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return null;
            Camera camera = Camera.main;
            if (camera == null) return null;
            // Use the actual game camera, including its native renderer,
            // volumes and transparent-effect settings. The old plain camera
            // produced opaque models but omitted visible native motor/flame FX.
            var backup = new GameObject("Resolute.MissileCameraBackup").AddComponent<Camera>();
            backup.enabled = false; backup.CopyFrom(camera);
            Vector3 previousPosition = camera.transform.position;
            Quaternion previousRotation = camera.transform.rotation;
            bool wasEnabled = camera.enabled;
            Vector4 shaderCamera = Shader.GetGlobalVector(ShaderGlobalManager.ID_Global_CameraPosition);
            Vector4 shaderTarget = Shader.GetGlobalVector(ShaderGlobalManager.ID_Global_CameraTarget);
            Vector4[] shaderPlanes = Shader.GetGlobalVectorArray(ShaderGlobalManager.ID_Global_FrustumPlanes);
            var texture = new RenderTexture(1280, 720, 24, RenderTextureFormat.DefaultHDR);
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.enabled = false; camera.nearClipPlane = .05f;
                camera.orthographic = false; camera.fieldOfView = 35f;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.ResetProjectionMatrix(); camera.ResetCullingMatrix();
                Vector3 focus = framingCenter ?? frame.position - frame.forward * length * .30f;
                camera.transform.position = angle == 0
                    ? focus + frame.right * length * 1.8f + frame.up * length * .38f
                    : angle == 1 ? focus + frame.up * length * 1.8f - frame.right * length * .45f
                    : angle == 2 ? focus - frame.forward * length * 2.2f + frame.right * length * .4f + frame.up * length * .3f
                    : focus + (-frame.forward * .78f + frame.right * .35f + frame.up * .52f).normalized * Mathf.Max(20f, framingDistance);
                camera.transform.LookAt(focus, frame.up);
                Vector3 cameraTarget;
                ShaderGlobalManager.SetCameraPlanes(camera, 0f, out cameraTarget);
                texture.Create(); camera.targetTexture = texture;
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = texture };
                if (!RenderPipeline.SupportsRenderRequest(camera, request)) return null;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = texture;
                pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                string path = Path.Combine(Path.GetDirectoryName(reportPath), name + ".png");
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                return Path.GetFileName(path);
            }
            finally
            {
                RenderTexture.active = previous;
                camera.CopyFrom(backup); camera.transform.SetPositionAndRotation(previousPosition, previousRotation); camera.enabled = wasEnabled;
                Shader.SetGlobalVector(ShaderGlobalManager.ID_Global_CameraPosition, shaderCamera);
                Shader.SetGlobalVector(ShaderGlobalManager.ID_Global_CameraTarget, shaderTarget);
                if (shaderPlanes != null && shaderPlanes.Length > 0) Shader.SetGlobalVectorArray(ShaderGlobalManager.ID_Global_FrustumPlanes, shaderPlanes);
                Object.Destroy(backup.gameObject); Object.Destroy(pixels); texture.Release(); Object.Destroy(texture);
            }
        }

        internal static object CaptureEffectsState(Missile missile)
        {
            NaturalWeaponEffects binding = missile.GetComponent<NaturalWeaponEffects>();
            Renderer flame = binding != null ? binding.TerminalFlame : null;
            Renderer glow = binding != null ? binding.TerminalNozzleGlow : null;
            var properties = new MaterialPropertyBlock();
            var glowProperties = new MaterialPropertyBlock();
            if (flame != null) flame.GetPropertyBlock(properties);
            if (glow != null) glow.GetPropertyBlock(glowProperties);
            Camera camera = Camera.main;
            var cameraData = camera != null ? camera.GetComponent<UniversalAdditionalCameraData>() : null;
            return new Dictionary<string, object> {
                ["captureMethod"] = "Temporarily posed native game camera with its original renderer, volumes and transparent-effect configuration; camera and native shader globals restored immediately afterward.",
                ["nativeCamera"] = camera != null ? camera.name : null,
                ["nativeCameraHDR"] = camera != null && camera.allowHDR,
                ["nativeCameraPostProcessing"] = cameraData != null && cameraData.renderPostProcessing,
                ["nativeCameraCullingMask"] = camera != null ? camera.cullingMask : 0,
                ["nativeCameraDepthTextureMode"] = camera != null ? camera.depthTextureMode.ToString() : null,
                ["nativeCameraRequiresDepthTexture"] = cameraData != null && cameraData.requiresDepthTexture,
                ["terminalAmount"] = binding != null ? binding.AfterburnerAmount : 0f,
                ["configuredBrightness"] = binding != null ? binding.TerminalBrightness : 0f,
                ["configuredTemperature"] = binding != null ? binding.TerminalTemperature : 0f,
                ["flameRendererEnabled"] = flame != null && flame.enabled,
                ["flameActiveInHierarchy"] = flame != null && flame.gameObject.activeInHierarchy,
                ["flameForceRenderingOff"] = flame != null && flame.forceRenderingOff,
                ["flameLayer"] = flame != null ? flame.gameObject.layer : -1,
                ["flameLocalScale"] = flame != null ? V(flame.transform.localScale) : null,
                ["flameBoundsCenterRelativeToMissile"] = flame != null ? V(missile.transform.InverseTransformPoint(flame.bounds.center)) : null,
                ["flameBoundsSizeMetres"] = flame != null ? V(flame.bounds.size) : null,
                ["propertyBlockBrightness"] = properties.GetFloat("_Brightness"),
                ["propertyBlockTemperature"] = properties.GetFloat("_Temperature"),
                ["nozzleGlowRendererEnabled"] = glow != null && glow.enabled,
                ["nozzleGlowActiveInHierarchy"] = glow != null && glow.gameObject.activeInHierarchy,
                ["nozzleGlowForceRenderingOff"] = glow != null && glow.forceRenderingOff,
                ["nozzleGlowConfiguredBrightness"] = binding != null ? binding.TerminalGlowBrightness : 0f,
                ["sourceNozzleOpeningDiameterMetres"] = binding != null ? binding.SourceNozzleOpeningDiameter : 0f,
                ["nozzleGlowDiameterMetres"] = binding != null ? binding.TerminalGlowDiameter : 0f,
                ["nozzleGlowNativeStateReady"] = binding != null && binding.NativeNozzleGlowReady,
                ["nozzleGlowLayer"] = glow != null ? glow.gameObject.layer : -1,
                ["nozzleGlowBoundsCenterRelativeToMissile"] = glow != null ? V(missile.transform.InverseTransformPoint(glow.bounds.center)) : null,
                ["nozzleGlowBoundsSizeMetres"] = glow != null ? V(glow.bounds.size) : null,
                ["nozzleGlowPropertyBlockEmission"] = new[] { glowProperties.GetColor("_EmissionColor").r, glowProperties.GetColor("_EmissionColor").g, glowProperties.GetColor("_EmissionColor").b },
                ["nozzleGlowMaterials"] = glow != null ? glow.sharedMaterials.Select(CaptureEmissionMaterial).ToArray() : new object[0],
                ["flameMaterials"] = flame != null ? flame.sharedMaterials.Select(m => new Dictionary<string, object> {
                    ["name"] = m != null ? m.name : null, ["shader"] = m != null ? m.shader.name : null,
                    ["renderQueue"] = m != null ? m.renderQueue : 0,
                    ["brightness"] = m != null && m.HasProperty("_Brightness") ? m.GetFloat("_Brightness") : 0f,
                    ["temperature"] = m != null && m.HasProperty("_Temperature") ? m.GetFloat("_Temperature") : 0f
                }).ToArray() : new object[0],
                ["nativeMotorParticles"] = missile.GetComponentsInChildren<ParticleSystem>(true).Select(p => {
                    ParticleSystemRenderer renderer = p.GetComponent<ParticleSystemRenderer>();
                    return new Dictionary<string, object> {
                        ["name"] = p.name, ["particles"] = p.particleCount, ["playing"] = p.isPlaying,
                        ["rendererEnabled"] = renderer != null && renderer.enabled,
                        ["activeInHierarchy"] = p.gameObject.activeInHierarchy,
                        ["layer"] = p.gameObject.layer,
                        ["boundsCenterRelativeToMissile"] = renderer != null ? V(missile.transform.InverseTransformPoint(renderer.bounds.center)) : null
                    };
                }).ToArray()
            };
        }
        internal static object CaptureNativeAfterburnerState(JetNozzle nozzle, Transform frame)
        {
            object native = ((Array)NaturalWeapons.Get(nozzle, "afterburners")).GetValue(0);
            Renderer flame = (Renderer)NaturalWeapons.Get(native, "flameRenderer");
            Renderer glow = (Renderer)NaturalWeapons.Get(native, "nozzleGlowRenderer");
            Func<Renderer, object> state = renderer => renderer == null ? null : new Dictionary<string, object> {
                ["name"] = renderer.name, ["enabled"] = renderer.enabled,
                ["layer"] = renderer.gameObject.layer,
                ["activeInHierarchy"] = renderer.gameObject.activeInHierarchy, ["forceRenderingOff"] = renderer.forceRenderingOff,
                ["positionRelativeToThrustFrame"] = V(frame.InverseTransformPoint(renderer.transform.position)),
                ["boundsCenterRelativeToThrustFrame"] = V(frame.InverseTransformPoint(renderer.bounds.center)),
                ["boundsSizeMetres"] = V(renderer.bounds.size),
                ["materials"] = renderer.sharedMaterials.Select(m => new Dictionary<string, object> {
                    ["name"] = m.name, ["shader"] = m.shader.name,
                    ["brightness"] = m.HasProperty("_Brightness") ? m.GetFloat("_Brightness") : 0f,
                    ["temperature"] = m.HasProperty("_Temperature") ? m.GetFloat("_Temperature") : 0f,
                    ["fadeDepthMetres"] = m.HasProperty("_FadeDepth") ? m.GetFloat("_FadeDepth") : 0f,
                    ["emissionColor"] = m.HasProperty("_EmissionColor") ? new[] { m.GetColor("_EmissionColor").r, m.GetColor("_EmissionColor").g, m.GetColor("_EmissionColor").b } : null,
                    ["emissionMaterial"] = CaptureEmissionMaterial(m)
                }).ToArray()
            };
            return new Dictionary<string, object> {
                ["nativeAfterburnerAmount"] = NaturalWeapons.Get(native, "afterburnerAmount"),
                ["nativeNozzleGlowBrightness"] = NaturalWeapons.Get(native, "nozzleGlowBrightness"),
                ["flame"] = state(flame), ["nozzleGlow"] = state(glow),
                ["scope"] = "Complete live Fighter1 native nozzle after normal engine warm-up. Same native game camera, projection, angles and framing length used by Pike. No flame-only clone or material change."
            };
        }
        private static object CaptureEmissionMaterial(Material material)
        {
            Color color = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.black;
            Color baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.black;
            Texture emission = material.HasProperty("_EmissionMap") ? material.GetTexture("_EmissionMap") : null;
            return new Dictionary<string, object> {
                ["name"] = material.name, ["shader"] = material.shader.name,
                ["renderQueue"] = material.renderQueue, ["keywords"] = material.shaderKeywords,
                ["emissionMap"] = emission != null ? emission.name : null,
                ["emissionMapInstanceId"] = emission != null ? emission.GetInstanceID() : 0,
                ["emissionColor"] = new[] { color.r, color.g, color.b, color.a },
                ["baseColor"] = new[] { baseColor.r, baseColor.g, baseColor.b, baseColor.a },
                ["activeColorSpace"] = QualitySettings.activeColorSpace.ToString()
            };
        }
        internal static object CaptureNativeGlowPropertyBlockControl(JetNozzle nozzle, Transform frame, float length, string output)
        {
            object native = ((Array)NaturalWeapons.Get(nozzle, "afterburners")).GetValue(0);
            Renderer renderer = (Renderer)NaturalWeapons.Get(native, "nozzleGlowRenderer");
            var original = new MaterialPropertyBlock(); renderer.GetPropertyBlock(original);
            Color color = renderer.sharedMaterial.GetColor("_EmissionColor");
            var control = new MaterialPropertyBlock(); control.SetColor("_EmissionColor", color);
            try
            {
                renderer.SetPropertyBlock(control);
                return new Dictionary<string, object> {
                    ["sameInputEmission"] = new[] { color.r, color.g, color.b, color.a },
                    ["screenshot"] = CaptureFrame(frame, length, output, "flight_native_fighter_afterburner_rear_propertyblock_control", 2),
                    ["scope"] = "Same live native nozzle, shader, texture, brightness and camera, with only the emission assignment routed through MaterialPropertyBlock.SetColor for this one image. Original renderer block restored immediately afterward."
                };
            }
            finally { renderer.SetPropertyBlock(original); }
        }
        private static float[] V(Vector3 vector) { return new[] { vector.x, vector.y, vector.z }; }
    }
    // Explicit projectile diagnostic camera: native looping flares use
    // Automatic culling and can pause while the player's camera is far away.
    // Keeping a normal rendered observer on the projectile exercises their
    // untouched native simulation; no particles are seeded or advanced here.
    [DefaultExecutionOrder(10000)]
    internal sealed class NaturalWeaponObserver : MonoBehaviour
    {
        internal Missile Subject;
        internal int ObservedFrames, AttributedFlareCount, MaximumLiveGlowFlares;
        internal bool RenderRequestsSupported;
        private static NaturalWeaponObserver active;
        private readonly List<NaturalFlareOwner> attributedFlares = new List<NaturalFlareOwner>();
        private NaturalCruiseTactics cruiseTactics;
        private Camera observer;
        private Camera nativeSettingsBackup;
        private RenderTexture target;
        private float nextRenderTime;
        internal static NaturalWeaponObserver Create(Missile subject)
        {
            Camera main = Camera.main;
            if (main == null || subject == null) return null;
            var go = new GameObject("Resolute.NativeParticleDiagnosticObserver");
            var result = go.AddComponent<NaturalWeaponObserver>();
            result.Subject = subject;
            result.cruiseTactics = subject.GetComponent<NaturalCruiseTactics>();
            active = result;
            result.observer = go.AddComponent<Camera>(); result.observer.CopyFrom(main);
            result.observer.nearClipPlane = .1f; result.observer.farClipPlane = 600f;
            result.observer.orthographic = false; result.observer.fieldOfView = 65f;
            result.observer.rect = new Rect(0f, 0f, 1f, 1f);
            result.observer.depth = main.depth - 1f;
            result.observer.enabled = false;
            var backup = new GameObject("NativeCameraSettingsBackup"); backup.transform.SetParent(go.transform, false);
            result.nativeSettingsBackup = backup.AddComponent<Camera>(); result.nativeSettingsBackup.enabled = false;
            result.target = new RenderTexture(128, 128, 24, RenderTextureFormat.DefaultHDR); result.target.Create();
            result.LateUpdate();
            return result;
        }
        internal static NaturalFlareOwner[] OwnedFlares(Missile subject)
        {
            if (active != null && active.Subject == subject)
            {
                active.ObserveAttribution();
                return active.attributedFlares.Where(f => f != null).ToArray();
            }
            return Object.FindObjectsOfType<NaturalFlareOwner>().Where(f => f.Owner == subject).ToArray();
        }
        private void ObserveAttribution()
        {
            attributedFlares.RemoveAll(f => f == null);
            if (cruiseTactics == null || cruiseTactics.FlareCount > AttributedFlareCount)
                foreach (NaturalFlareOwner flare in Object.FindObjectsOfType<NaturalFlareOwner>().Where(f => f.Owner == Subject))
                    if (!attributedFlares.Contains(flare)) { attributedFlares.Add(flare); AttributedFlareCount++; }
            // Production releases its IR owner at 100 m. The diagnostic keeps
            // the already witnessed emitter association while this actual flare
            // remains alive, so that its later native glow is still observable.
        }
        private void LateUpdate()
        {
            if (Subject == null || Subject.disabled || observer == null) return;
            ObserveAttribution();
            if (Time.time < nextRenderTime) return;
            nextRenderTime = Time.time + 1f / 30f;
            MaximumLiveGlowFlares = Mathf.Max(MaximumLiveGlowFlares,
                attributedFlares.Count(f => NaturalWeaponAudit.NativeFlareGlowPositions(f).Length > 0));
            Transform frame = Subject.transform;
            Bounds envelope = new Bounds(frame.position + frame.forward * 15f, Vector3.one * Subject.definition.length);
            foreach (NaturalFlareOwner flare in attributedFlares)
            {
                envelope.Encapsulate(flare.transform.position);
                foreach (Vector3 glow in NaturalWeaponAudit.NativeFlareGlowPositions(flare)) envelope.Encapsulate(glow);
            }
            Vector3 focus = envelope.center;
            float distance = Mathf.Max(75f, envelope.extents.magnitude * 1.2f / Mathf.Tan(32.5f * Mathf.Deg2Rad));
            transform.position = focus + (-frame.forward * .78f + frame.right * .35f + frame.up * .52f).normalized * distance;
            observer.farClipPlane = Mathf.Max(600f, distance + envelope.extents.magnitude * 2f);
            transform.LookAt(focus, frame.up);
            if (RenderNativeObservation()) ObservedFrames++;
        }
        private bool RenderNativeObservation()
        {
            Camera camera = Camera.main;
            if (camera == null || target == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return false;
            nativeSettingsBackup.CopyFrom(camera); nativeSettingsBackup.enabled = false;
            Vector3 position = camera.transform.position; Quaternion rotation = camera.transform.rotation;
            bool enabled = camera.enabled;
            RenderTexture previousTexture = RenderTexture.active;
            Vector4 shaderCamera = Shader.GetGlobalVector(ShaderGlobalManager.ID_Global_CameraPosition);
            Vector4 shaderTarget = Shader.GetGlobalVector(ShaderGlobalManager.ID_Global_CameraTarget);
            Vector4[] shaderPlanes = Shader.GetGlobalVectorArray(ShaderGlobalManager.ID_Global_FrustumPlanes);
            try
            {
                // An enabled auxiliary camera is not sufficient in a batch
                // player. Submit a real render through the native game camera
                // while preserving its renderer, volumes and shader globals.
                camera.enabled = false;
                camera.nearClipPlane = observer.nearClipPlane; camera.farClipPlane = observer.farClipPlane;
                camera.orthographic = false; camera.fieldOfView = observer.fieldOfView;
                camera.rect = new Rect(0f, 0f, 1f, 1f);
                camera.targetTexture = target; camera.aspect = (float)target.width / target.height;
                camera.ResetProjectionMatrix(); camera.ResetCullingMatrix();
                camera.transform.SetPositionAndRotation(transform.position, transform.rotation);
                Vector3 cameraTarget; ShaderGlobalManager.SetCameraPlanes(camera, 0f, out cameraTarget);
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                RenderRequestsSupported = RenderPipeline.SupportsRenderRequest(camera, request);
                if (!RenderRequestsSupported) return false;
                RenderPipeline.SubmitRenderRequest(camera, request);
                return true;
            }
            finally
            {
                RenderTexture.active = previousTexture;
                camera.CopyFrom(nativeSettingsBackup); camera.transform.SetPositionAndRotation(position, rotation); camera.enabled = enabled;
                Shader.SetGlobalVector(ShaderGlobalManager.ID_Global_CameraPosition, shaderCamera);
                Shader.SetGlobalVector(ShaderGlobalManager.ID_Global_CameraTarget, shaderTarget);
                if (shaderPlanes != null && shaderPlanes.Length > 0) Shader.SetGlobalVectorArray(ShaderGlobalManager.ID_Global_FrustumPlanes, shaderPlanes);
            }
        }
        internal void DisposeObserver()
        {
            if (observer != null) { observer.enabled = false; observer.targetTexture = null; }
            if (target != null) { target.Release(); Object.Destroy(target); target = null; }
            attributedFlares.Clear(); if (active == this) active = null;
            Object.Destroy(gameObject);
        }
        private void OnDestroy()
        {
            if (target != null) { target.Release(); Object.Destroy(target); target = null; }
            attributedFlares.Clear(); if (active == this) active = null;
        }
    }
}
