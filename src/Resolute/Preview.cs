using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Resolute
{
    // Explicit visual diagnostic in the real game's render pipeline.
    [HarmonyPatch(typeof(MainMenu), "Awake")]
    internal static class PreviewStartPatch
    {
        private static bool Prefix()
        {
            if (!Environment.GetCommandLineArgs().Contains("--resolute-preview")) return true;
            Application.runInBackground = true;
            if (UnityEngine.Object.FindObjectOfType<ResolutePreviewRunner>() == null)
            {
                var go = new GameObject("Resolute.PreviewRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<ResolutePreviewRunner>();
            }
            return false;
        }
    }
    [HarmonyPatch(typeof(MainMenu), "Start")]
    internal static class PreviewSkipStartPatch
    {
        private static bool Prefix() { return !Environment.GetCommandLineArgs().Contains("--resolute-preview"); }
    }

    public sealed class ResolutePreviewRunner : MonoBehaviour
    {
        private Camera previewCamera;
        private RenderTexture target;
        private GameObject visual;
        private string reportPath, folder;
        private int frame, view;
        private bool ready, writing;
        private readonly string[] names = { "bow", "port", "top", "stern" };
        private readonly Vector3[] positions = { new Vector3(190, 105, 230), new Vector3(-260, 24, 0), new Vector3(0, 350, 0), new Vector3(-175, 90, -245) };
        private readonly List<object> captures = new List<object>();

        private IEnumerator Start()
        {
            reportPath = Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-preview-output");
            if (string.IsNullOrEmpty(reportPath) || !Path.IsPathRooted(reportPath)) { Application.Quit(2); yield break; }
            folder = Path.Combine(Path.GetDirectoryName(reportPath), "game_previews"); Directory.CreateDirectory(folder);
            Debug.Log("Resolute preview: startup, frame=" + Time.frameCount);
            Resources.Load<Encyclopedia>("Encyclopedia");
            var root = new GameObject("Resolute.PreviewTemplates"); root.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(root);
            GameObject template = null;
            try { template = VisualLoader.Load(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), root.transform, BepInEx.Logging.Logger.CreateLogSource("Resolute Preview")); }
            catch (Exception e) { Fail(e); yield break; }
            Debug.Log("Resolute preview: loaded template, creating visible clone");
            visual = UnityEngine.Object.Instantiate(template); visual.name = "Resolute_Preview";
            Debug.Log("Resolute preview: visible clone created");
            foreach (var t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 31;
            foreach (var group in visual.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
            foreach (var camera in Camera.allCameras) camera.enabled = false;
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>()) canvas.enabled = false;
            Debug.Log("Resolute preview: scene isolated");
            previewCamera = new GameObject("Resolute.PreviewCamera").AddComponent<Camera>();
            previewCamera.tag = "MainCamera";
            previewCamera.cullingMask = 1 << 31;
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = new Color(0.34f, 0.43f, 0.53f);
            previewCamera.nearClipPlane = 0.3f; previewCamera.farClipPlane = 2000;
            previewCamera.orthographic = true; previewCamera.allowHDR = false;
            target = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32); target.Create(); previewCamera.targetTexture = target;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.55f, 0.58f, 0.65f);
            var sun = new GameObject("Resolute.PreviewSun").AddComponent<Light>(); sun.type = LightType.Directional;
            sun.intensity = 1.3f; sun.transform.rotation = Quaternion.Euler(45, -45, 0); sun.cullingMask = 1 << 31;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            var fill = new GameObject("Resolute.PreviewFill").AddComponent<Light>(); fill.type = LightType.Directional;
            fill.intensity = 0.45f; fill.transform.rotation = Quaternion.Euler(15, 130, 0); fill.cullingMask = 1 << 31;
            SetView();
            Debug.Log("Resolute preview: camera ready; pipeline=" + GraphicsSettings.currentRenderPipeline);
            ready = true;
            while (ready)
            {
                yield return null;
                try
                {
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                    if (!RenderPipeline.SupportsRenderRequest(previewCamera, request))
                        throw new InvalidOperationException("The game renderer does not support explicit preview requests.");
                    RenderPipeline.SubmitRenderRequest(previewCamera, request);
                    Capture();
                }
                catch (Exception e) { Fail(e); }
            }
        }

        private void Update()
        {
            // Built-in fallback is only used if the game's settings select no SRP.
            // Explicit render requests above also work in a batch-mode player,
            // where the normal screen render loop is intentionally suspended.
        }
        private void OnCameraRendered(ScriptableRenderContext context, Camera camera)
        {
            if (!ready || writing || camera != previewCamera) return;
            if (++frame < 4) return;
            Capture();
        }
        private void SetView()
        {
            frame = 0;
            previewCamera.orthographicSize = view == 2 ? 125 : 84;
            previewCamera.transform.position = positions[view];
            previewCamera.transform.LookAt(new Vector3(0, 9, 0), view == 2 ? Vector3.forward : Vector3.up);
        }
        private void Capture()
        {
            writing = true;
            try
            {
                var previous = RenderTexture.active; RenderTexture.active = target;
                var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                string file = Path.Combine(folder, "resolute_" + names[view] + ".png");
                File.WriteAllBytes(file, ImageConversion.EncodeToPNG(image));
                UnityEngine.Object.Destroy(image); RenderTexture.active = previous;
                captures.Add(new Dictionary<string, object> { ["view"] = names[view], ["file"] = file });
                Debug.Log("Resolute preview captured " + names[view]);
                view++;
                if (view == names.Length)
                {
                    ready = false;
                    File.WriteAllText(reportPath, Audit.Json(new Dictionary<string, object>
                    {
                        ["success"] = true, ["mode"] = "actual-game-renderer", ["gameVersion"] = Application.version,
                        ["graphicsDevice"] = SystemInfo.graphicsDeviceName,
                        ["pipeline"] = GraphicsSettings.currentRenderPipeline == null ? "Built-in" : GraphicsSettings.currentRenderPipeline.name,
                        ["captures"] = captures, ["renderers"] = visual.GetComponentsInChildren<Renderer>().Length,
                        ["physicsTested"] = false
                    }));
                    Application.Quit(0);
                }
                else SetView();
            }
            catch (Exception e) { Fail(e); }
            finally { writing = false; }
        }
        private void Fail(Exception e)
        {
            ready = false; Debug.LogException(e);
            if (!string.IsNullOrEmpty(reportPath)) File.WriteAllText(reportPath, Audit.Json(new Dictionary<string, object> { ["success"] = false, ["error"] = e.ToString() }));
            Application.Quit(2);
        }
        private void OnDestroy() { RenderPipelineManager.endCameraRendering -= OnCameraRendered; }
    }
}
