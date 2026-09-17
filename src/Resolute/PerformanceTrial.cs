using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Mirage;
using NuclearOption.MissionEditorScripts;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit copied-game diagnostic: invokes the same native spawner and
    // SavedUnit wrappers as editor placement/rotation, including physics sync.
    // No benchmark hooks are installed during normal play.
    internal static class PerformanceTrial
    {
        internal static IEnumerator Run(bool editor, GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game")
                throw new InvalidOperationException("Performance trial requires the isolated test-game copy.");
            var phases = new List<object>();
            var result = new Dictionary<string, object>
            {
                ["scope"] = editor ? "Native editor state, SpawnFromUnitDefinitionInEditor, SavedUnit position/rotation wrappers and Physics.SyncTransforms; empty map, one ship and four ships; identical fixed camera. No mission save."
                    : "Native offline single-player state; empty map, one ship and four ships, autonomous ship components enabled; identical fixed camera and no enemies. No mission save.",
                ["editor"] = editor, ["nativeGameState"] = GameManager.gameState.ToString(),
                ["graphicsDevice"] = SystemInfo.graphicsDeviceName,
                ["graphicsType"] = SystemInfo.graphicsDeviceType.ToString(),
                ["processor"] = SystemInfo.processorType, ["qualityLevel"] = QualitySettings.GetQualityLevel(),
                ["lodBias"] = QualitySettings.lodBias, ["maximumLODLevel"] = QualitySettings.maximumLODLevel,
                ["fixedDeltaTime"] = Time.fixedDeltaTime, ["phases"] = phases,
                ["frameSampling"] = "Stopwatch wall-clock intervals across consecutive game loops, including one explicit native URP offscreen render per loop (1280x800). Hidden batch processes do not reliably auto-render. Warmup renders every loop before measurement. Steady samples last at least 30 seconds and 600 loops; each editor manipulation uses the same 240 steps regardless of machine speed. No report serialization, mesh enumeration or forced collection inside sampling. Warmup and creation are excluded. These are controlled workload timings, not monitor-presented FPS.",
                ["spawnTimingScope"] = "One-ship phases measure the first live instance of each definition in this process and can include cold/JIT costs; the first Dynamo also warms shared native code before Resolute. Do not compare those first-spawn numbers as equivalent warm costs. Four-ship phases measure subsequent instances after both definitions have run."
            };
            report["performance"] = result;
            int oldVsync = QualitySettings.vSyncCount, oldLimit = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            Camera camera = null;
            RenderTexture target = null;
            Camera main = Camera.main;
            bool mainEnabled = main != null && main.enabled;
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                if (main != null) main.enabled = false;
                camera = new GameObject("Resolute.PerformanceCamera").AddComponent<Camera>();
                camera.enabled = false;
                camera.fieldOfView = 48f; camera.nearClipPlane = .5f; camera.farClipPlane = 25000f;
                camera.clearFlags = CameraClearFlags.Skybox;
                target = new RenderTexture(1280, 800, 24); target.Create(); camera.targetTexture = target;
            }
            RenderPipeline.StandardRequest renderRequest = camera != null ? new RenderPipeline.StandardRequest { destination = target } : null;
            Action render = () => { if (camera != null) RenderPipeline.SubmitRenderRequest(camera, renderRequest); };
            foreach (string key in new[] { "empty", "Destroyer1", Plugin.DefinitionKey, "Destroyer1", Plugin.DefinitionKey })
            {
                int count = phases.Count >= 3 ? 4 : key == "empty" ? 0 : 1;
                Vector3 gridCenter = count == 4 ? new Vector3(165f, 0f, 180f) : Vector3.zero;
                Vector3 cameraOffset = count == 4 ? new Vector3(-480f, 350f, -500f) : new Vector3(-170f, 115f, -205f);
                if (camera != null)
                {
                    camera.transform.position = origin.ToLocalPosition() + heading * (gridCenter + cameraOffset);
                    camera.transform.LookAt(origin.ToLocalPosition() + heading * (gridCenter + Vector3.up * 15f), Vector3.up);
                }
                report["phase"] = (editor ? "performance-editor-" : "performance-runtime-") + key + "-" + count;
                Save(output, report);
                var ships = new List<Ship>();
                var spawnTimes = new List<double>();
                var created = new List<object>();
                ShipDefinition definition = key == "empty" ? null : key == "Destroyer1" ? Plugin.FindDonor(Encyclopedia.i) : (ShipDefinition)Encyclopedia.Lookup[key];
                for (int i = 0; i < count; i++)
                {
                    GlobalPosition position = origin + heading * new Vector3((i % 2) * 330f, definition.spawnOffset.y, (i / 2) * 360f);
                    Stopwatch watch = Stopwatch.StartNew();
                    Ship ship = (Ship)NetworkSceneSingleton<Spawner>.i.SpawnFromUnitDefinitionInEditor(definition, position, heading, faction, "rsl_perf_" + phases.Count + "_" + i);
                    SavedShip saved = new SavedShip("rsl_perf_" + phases.Count + "_" + i);
                    saved.AfterCreate(ship); ship.LinkSavedUnit(saved);
                    if (editor) saved.AfterLoadEditor();
                    else ship.SetHoldPosition(true);
                    Physics.SyncTransforms();
                    watch.Stop(); spawnTimes.Add(watch.Elapsed.TotalMilliseconds); ships.Add(ship);
                    yield return null;
                }
                var phase = new Dictionary<string, object>
                { ["definition"] = key, ["ships"] = count, ["spawnAndRegisterMs"] = spawnTimes,
                  ["spawnTemperature"] = count == 0 ? "no-spawn" : count == 1 ? "first-live-instance-potential-cold-JIT" : "subsequent-instances-after-both-definitions",
                  ["cameraOffsetFromGridCenter"] = V(cameraOffset), ["cameraGridCenterOffset"] = V(gridCenter),
                  ["cameraTargetOffsetFromGridCenter"] = V(Vector3.up * 15f), ["cameraFieldOfView"] = camera != null ? (object)camera.fieldOfView : null,
                  ["renderWidth"] = target != null ? target.width : 0, ["renderHeight"] = target != null ? target.height : 0,
                  ["view"] = count == 4 ? "wide-four-ship-grid" : "near-single-ship",
                  ["inventorySampling"] = "After rendered warmup, before timing. Renderer.isVisible reflects any camera; per-LOD visibility is reported as an observation, not a guaranteed selected LOD index.",
                  ["inventory"] = created, ["steady"] = new Dictionary<string, object>() };
                phases.Add(phase); Save(output, report);
                float warmupUntil = Time.realtimeSinceStartup + (editor ? 4f : 10f);
                int warmupLoops = 0;
                while (Time.realtimeSinceStartup < warmupUntil)
                {
                    render(); warmupLoops++;
                    yield return null;
                }
                phase["warmupRenderedLoops"] = camera != null ? warmupLoops : 0;
                foreach (Ship ship in ships) created.Add(Inventory(ship));
                Save(output, report);
                IEnumerator steady = Sample(30f, 600, (Dictionary<string, object>)phase["steady"], null, render);
                while (steady.MoveNext()) yield return steady.Current;
                Save(output, report);
                if (editor && count > 0)
                {
                    var rotation = new Dictionary<string, object>
                    { ["operationSequence"] = "Exactly 240 steps, 1.5 degrees each, one complete 360 degree rotation." };
                    phase["continuousRotation"] = rotation;
                    int rotationStep = 0;
                    IEnumerator rotate = Sample(0f, 240, rotation, () =>
                    {
                        float yaw = ++rotationStep * 1.5f;
                        foreach (Ship ship in ships) ship.SavedUnit.RotationWrapper.SetValue(heading * Quaternion.Euler(0f, yaw, 0f), phase);
                        Physics.SyncTransforms();
                    }, render, 240);
                    while (rotate.MoveNext()) yield return rotate.Current;
                    var placement = new Dictionary<string, object>
                    { ["operationSequence"] = "Exactly 240 steps through one sine cycle with 40 metre lateral amplitude, returning to the initial grid." };
                    phase["continuousPlacement"] = placement;
                    int step = 0;
                    IEnumerator move = Sample(0f, 240, placement, () =>
                    {
                        step++;
                        float lateral = Mathf.Sin(step * (2f * Mathf.PI / 240f)) * 40f;
                        for (int i = 0; i < ships.Count; i++) ships[i].SavedUnit.PositionWrapper.SetValue(origin + heading * new Vector3((i % 2) * 330f + lateral, definition.spawnOffset.y, (i / 2) * 360f), phase);
                        Physics.SyncTransforms();
                    }, render, 240);
                    while (move.MoveNext()) yield return move.Current;
                    phase["nativeWrapperAppliedRotation"] = ships.All(s => Quaternion.Angle(s.transform.rotation, s.SavedUnit.rotation) < .02f);
                    phase["nativeWrapperAppliedPosition"] = ships.All(s => Vector3.Distance(s.GlobalPosition().AsVector3(), s.SavedUnit.globalPosition.AsVector3()) < .02f);
                }
                phase["allShipsHealthy"] = ships.All(s => s != null && !s.disabled && s.parts.All(p => p != null && p.hitPoints >= 99f));
                if (target != null)
                {
                    string capture = Path.Combine(Path.GetDirectoryName(output), "phase_" + phases.Count + "_" + key + ".png");
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = target;
                    Texture2D texture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                    texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); texture.Apply();
                    File.WriteAllBytes(capture, texture.EncodeToPNG());
                    RenderTexture.active = previous; Object.Destroy(texture);
                    phase["renderedCapture"] = capture;
                }
                Save(output, report);
                foreach (Ship ship in ships) if (ship != null) NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(ship.Identity, true);
                yield return new WaitForSecondsRealtime(3f);
            }
            result["complete"] = phases.Count == 5;
            checks.Add(new Dictionary<string, object> { ["name"] = "performance-five-matched-phases", ["passed"] = phases.Count == 5 });
            foreach (Dictionary<string, object> phase in phases)
            {
                checks.Add(new Dictionary<string, object> { ["name"] = "performance-healthy-" + phase["definition"] + "-" + phase["ships"], ["passed"] = phase["allShipsHealthy"] });
                if (editor && (int)phase["ships"] > 0)
                {
                    checks.Add(new Dictionary<string, object> { ["name"] = "performance-native-rotation-wrapper-" + phase["definition"] + "-" + phase["ships"], ["passed"] = phase["nativeWrapperAppliedRotation"] });
                    checks.Add(new Dictionary<string, object> { ["name"] = "performance-native-position-wrapper-" + phase["definition"] + "-" + phase["ships"], ["passed"] = phase["nativeWrapperAppliedPosition"] });
                }
            }
            if (camera != null) { camera.targetTexture = null; Object.Destroy(camera.gameObject); }
            if (target != null) { target.Release(); Object.Destroy(target); }
            if (main != null) main.enabled = mainEnabled;
            QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldLimit;
            report["phase"] = "complete"; Save(output, report);
        }

        private static IEnumerator Sample(float seconds, int minFrames, Dictionary<string, object> result, Action action, Action render, int fixedFrames = 0)
        {
            var samples = new List<double>(32000);
            var work = new List<double>(32000);
            var spikes = new List<object>();
            int[] collections = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            int lastCollections = collections[0];
            long heapBefore = Profiler.GetMonoUsedSizeLong();
            long started = Stopwatch.GetTimestamp(), previous = started;
            double scale = 1000d / Stopwatch.Frequency;
            while (fixedFrames > 0 ? samples.Count < fixedFrames :
                samples.Count < minFrames || (Stopwatch.GetTimestamp() - started) * scale < seconds * 1000d)
            {
                long operationStart = Stopwatch.GetTimestamp();
                action?.Invoke();
                long operationEnd = Stopwatch.GetTimestamp();
                render();
                yield return null;
                long now = Stopwatch.GetTimestamp();
                double ms = (now - previous) * scale;
                samples.Add(ms); work.Add((operationEnd - operationStart) * scale);
                int gc = GC.CollectionCount(0);
                if (ms > 50d || gc != lastCollections)
                    spikes.Add(new Dictionary<string, object> { ["elapsedSeconds"] = (now - started) * scale / 1000d,
                        ["frameMs"] = ms, ["generation0Collection"] = gc != lastCollections });
                lastCollections = gc; previous = now;
            }
            long finished = Stopwatch.GetTimestamp();
            long heapAfter = Profiler.GetMonoUsedSizeLong();
            int generation0 = GC.CollectionCount(0) - collections[0];
            int generation1 = GC.CollectionCount(1) - collections[1];
            int generation2 = GC.CollectionCount(2) - collections[2];
            // Sorting/serializing the report allocates. Take these readings
            // before summary construction so it is not counted as game churn.
            result["elapsedSeconds"] = (finished - started) * scale / 1000d;
            result["samplingLimit"] = fixedFrames > 0 ? "fixed-operation-count" : "minimum-duration-and-loop-count";
            result["frames"] = samples.Count;
            result["frameMs"] = Distribution(samples);
            result["operationMs"] = Distribution(work);
            result["framesOver33ms"] = samples.Count(x => x > 33.333);
            result["framesOver50ms"] = samples.Count(x => x > 50);
            result["framesOver100ms"] = samples.Count(x => x > 100);
            result["collectionsByGeneration"] = new[] { generation0, generation1, generation2 };
            result["monoHeapChangeBytes"] = heapAfter - heapBefore;
            result["spikes"] = spikes;
        }

        private static object Distribution(List<double> values)
        {
            double[] sorted = values.OrderBy(x => x).ToArray();
            return new Dictionary<string, object> { ["median"] = sorted[sorted.Length / 2], ["p95"] = sorted[(int)((sorted.Length - 1) * .95)],
                ["p99"] = sorted[(int)((sorted.Length - 1) * .99)], ["max"] = sorted[sorted.Length - 1], ["mean"] = values.Average() };
        }

        private static object Inventory(Ship ship)
        {
            Renderer[] renderers = ship.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = ship.GetComponentsInChildren<Collider>(true);
            LODGroup[] lodGroups = ship.GetComponentsInChildren<LODGroup>(true);
            return new Dictionary<string, object>
            { ["transforms"] = ship.GetComponentsInChildren<Transform>(true).Length, ["renderers"] = renderers.Length,
              ["enabledRenderers"] = renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy),
              ["visibleRenderersAnyCamera"] = renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy && r.isVisible), ["colliders"] = colliders.Length,
              ["enabledColliders"] = colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy),
              ["meshColliders"] = colliders.OfType<MeshCollider>().Count(), ["parts"] = ship.parts.Count,
              ["rigidbodies"] = ship.GetComponentsInChildren<Rigidbody>(true).Length,
              ["distinctMaterials"] = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count(),
              ["behaviours"] = ship.GetComponentsInChildren<MonoBehaviour>(true).Length,
              ["lodGroups"] = lodGroups.Select(group => new Dictionary<string, object>
              {
                  ["name"] = group.name, ["enabled"] = group.enabled, ["size"] = group.size,
                  ["levels"] = group.GetLODs().Select((lod, index) => new Dictionary<string, object>
                  {
                      ["index"] = index, ["screenRelativeTransitionHeight"] = lod.screenRelativeTransitionHeight,
                      ["rendererCount"] = lod.renderers.Length,
                      ["enabledRendererCount"] = lod.renderers.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy),
                      ["visibleRendererCountAnyCamera"] = lod.renderers.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy && r.isVisible)
                  }).ToList()
              }).ToList() };
        }

        private static float[] V(Vector3 value) { return new[] { value.x, value.y, value.z }; }

        private static void Save(string output, Dictionary<string, object> report)
        { File.WriteAllText(output, Audit.Json(report)); }
    }
}
