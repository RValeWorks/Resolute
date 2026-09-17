using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Resolute
{
    // Called only by the explicit isolated mission trial, after the normal spawn.
    internal static class AssembledPreview
    {
        internal static List<object> Capture(Ship ship, string folder)
        {
            var captures = new List<object>();
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return captures;
            Directory.CreateDirectory(folder);
            Camera camera = new GameObject("Resolute.MissionProofCamera").AddComponent<Camera>();
            camera.enabled = false;
            camera.nearClipPlane = 0.5f;
            camera.farClipPlane = 30000f;
            camera.fieldOfView = 42;
            camera.allowHDR = true;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.cullingMask = ~0;
            UniversalAdditionalCameraData cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            cameraData.antialiasingQuality = AntialiasingQuality.High;
            cameraData.volumeLayerMask = ~0;
            var texture = new RenderTexture(1920, 1200, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            camera.targetTexture = texture;
            var previous = RenderTexture.active;
            string[] names = { "assembled_bow", "assembled_stern", "assembled_top" };
            Vector3[] offsets = { new Vector3(205, 120, 255), new Vector3(-195, 110, -255), new Vector3(0, 380, 0) };
            LODGroup[] lods = ship.GetComponentsInChildren<LODGroup>(true);
            try
            {
                foreach (LODGroup lod in lods) if (lod.enabled) lod.ForceLOD(0);
                for (int i = 0; i < names.Length; i++)
                {
                    camera.transform.position = ship.transform.TransformPoint(offsets[i]);
                    camera.transform.LookAt(ship.transform.TransformPoint(new Vector3(0, 3, 0)),
                        i == 2 ? ship.transform.forward : Vector3.up);
                    var request = new UniversalRenderPipeline.SingleCameraRequest { destination = texture };
                    if (!RenderPipeline.SupportsRenderRequest(camera, request))
                        throw new InvalidOperationException("The active pipeline does not support mission proof capture.");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    RenderTexture.active = texture;
                    var png = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                    try
                    {
                        png.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                        png.Apply();
                        string file = Path.Combine(folder, "resolute_" + names[i] + ".png");
                        File.WriteAllBytes(file, ImageConversion.EncodeToPNG(png));
                        captures.Add(new Dictionary<string, object> { ["view"] = names[i], ["file"] = file });
                    }
                    finally { UnityEngine.Object.Destroy(png); }
                }
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (LODGroup lod in lods) if (lod != null && lod.enabled) lod.ForceLOD(-1);
                camera.targetTexture = null;
                texture.Release();
                UnityEngine.Object.Destroy(texture);
                UnityEngine.Object.Destroy(camera.gameObject);
            }
            return captures;
        }
    }
}
