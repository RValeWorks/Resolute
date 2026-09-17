using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using Object = UnityEngine.Object;

namespace Resolute
{
    // The original selector retains its protocol. Redo observations are opt-in;
    // neither mode writes health, forces, or detachment state.
    internal static class DamagePerformanceTrial
    {
        private const int Capacity = 64000;
        private static readonly double MillisecondsPerTick = 1000d / Stopwatch.Frequency;
        private static ImpactCase active;
        private static Action render;
        private static readonly Dictionary<MethodBase, int> customMethods = new Dictionary<MethodBase, int>();
        private static readonly List<string> customNames = new List<string>();
        private static Type customBatchType;
        private static List<CostRecorder> costRecorders;
        private static readonly int HitPointsProperty = Shader.PropertyToID("_HitPoints");

        private sealed class CostRecorder
        {
            internal string Name, Category, Unit, DataType, Flags;
            internal ProfilerRecorder Recorder;
            internal int ResetCalls, RestartAttempts, RestartFailures;
            internal bool RunningAfterLastRestart;
        }

        private sealed class CostSamples
        {
            internal int Samples, ZeroSamples, InvalidReads, EmptyReads;
            internal double Sum, Minimum = double.PositiveInfinity, Maximum = double.NegativeInfinity;
            internal void Read(CostRecorder source)
            {
                if (!source.Recorder.Valid || !source.Recorder.IsRunning) { InvalidReads++; return; }
                if (source.Recorder.Count == 0) { EmptyReads++; return; }
                double value = source.DataType == "Double" ? source.Recorder.LastValueAsDouble : source.Recorder.LastValue;
                Samples++; if (value == 0) ZeroSamples++;
                Sum += value; Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value);
            }
        }

        private sealed class ImpactCase
        {
            internal Ship Ship;
            internal Missile Missile;
            internal Shockwave Shockwave;
            internal PersistentID MissileID;
            internal bool Sampling, BufferOverflow, ArmedAfterHandoff, DetonatedArmed, HitArmor, HitTerrain, InNativeDetonation;
            internal int FrameCount, Detonations, BlastCalls, DamageCalls;
            internal int ShockwaveStarts, ShockwaveUpdates, ShockwaveInfluencedObjects;
            internal int Gen0, Gen1, Gen2, LastGen0;
            internal long Started, PreviousFrame, HeapBefore;
            internal long DetonationTicks, BlastTicks, DamageTicks, MaxDamageTicks;
            internal long ShockwaveStartTicks, ShockwaveUpdateTicks, MaxShockwaveUpdateTicks;
            internal float Yield;
            internal double DetonationAt = -1, BlastAt = -1;
            internal Vector3 DetonationPosition, ArmedPosition;
            internal readonly double[] Frames = new double[Capacity];
            internal readonly double[] FrameEnds = new double[Capacity];
            internal readonly bool[] Collections = new bool[Capacity];
            internal VlsStructuralAttachments Vls;
            internal bool Redo;
            internal int VlsDepth;
            internal readonly long[] CustomTicks = new long[16], CustomMaxTicks = new long[16];
            internal readonly int[] CustomCalls = new int[16];
            internal double[] RenderSubmitMs;
            internal int[] FixedSteps;
            internal int PendingFixedSteps;
            internal CostSamples[][] RecorderSamples;
        }

        internal static IEnumerator Run(GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output, bool shipRedoBaseline = false)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game")
                throw new InvalidOperationException("Damage performance trial requires the isolated test-game copy.");
            Require(checks, "damage-performance-native-single-player", GameManager.gameState == GameState.SinglePlayer);
            Require(checks, "damage-performance-rendering-device", SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null);
            var cases = new List<object>();
            report["damagePerformance"] = new Dictionary<string, object>
            {
                ["scope"] = "Four separate native AShM1 impacts on fresh Dynamo ships, then the same four compartments on fresh Resolute ships. Every impact starts with an intact subject. Native missile flight, armor, warhead yield, blast traces, compartment damage and breakup remain active. No health setting, forced detachment, direct detonation or direct damage calls.",
                ["protocol"] = "80 metre starboard approach at 250 m/s; explicit Arm and SetTangible after native StartMissile's first physics-frame handoff for this short test approach. 1.5 seconds of rendered warmup and a 5 second impact observation per fresh subject. Actual armed armor-impact detonation and lost native part health are required.",
                ["timingScope"] = "Consecutive game loops with one explicit native offscreen render at 1280x800. Diagnostic-only prefix/postfix timestamps separately measure Missile.Detonate, its actual owned Shockwave.Start/Update, optional DamageEffects.BlastFrag and subject UnitPart.TakeDamage. Native AShM1 yield300 uses Shockwave; BlastFrag is the native path only at yields<=200. These nested inclusive CPU times must not be added together. Missile spawn is reported separately. No screenshots, readback, scene enumeration, report serialization or summary sorting occurs inside sampled windows. These are controlled workload timings, not presented FPS.",
                ["cleanupScope"] = "Between fresh subjects, remove the diagnostic missile/ship/native detached parts and new effect/debris hierarchies identified from the preceding scene-object snapshot. The existing owner, camera and world remain present. Cleanup and its following frame are excluded from measurement.",
                ["coldCostCaveat"] = "Fixed Dynamo-first order in both versions. First native damage/effect calls can include cold/JIT work; compare corresponding compartment cases and keep the Dynamo control results alongside Resolute.",
                ["cameraOffset"] = V(new Vector3(170f, 115f, -205f)), ["cameraFov"] = 48f,
                ["qualityLevel"] = QualitySettings.GetQualityLevel(), ["lodBias"] = QualitySettings.lodBias,
                ["graphicsDevice"] = SystemInfo.graphicsDeviceName, ["cases"] = cases
            };
            ShipDefinition native = (ShipDefinition)Encyclopedia.Lookup["Destroyer1"];
            ShipDefinition resolute = (ShipDefinition)Encyclopedia.Lookup[Plugin.DefinitionKey];
            if (shipRedoBaseline)
            {
                native = ShipRedoBaselineTrial.Comparator(report);
                var info = (Dictionary<string, object>)report["damagePerformance"];
                info["scope"] = "Redo baseline: two matched native AShM1 impact stations on fresh closest-size registered non-Resolute and Resolute subjects. Ship-local stations are side z=-0.05*length and forward support z=0.20*length, at sea+3m, traced against actual compartment colliders. Actual hit part and contact are recorded; no forced damage or physics overrides.";
                info["protocol"] = "10 seconds rendered warmup, 10 seconds intact loop baseline, then native 80m/250m/s short approach with existing explicit post-handoff Arm/SetTangible. 80 seconds from launch covers the 75-second custom debris lifetime after a prompt impact; sampling pauses for the 10-second hierarchy/fragment snapshot and one following frame. Timing gaps are disclosed.";
                info["coldCostCaveat"] = "Comparator-first fixed initial order. Two locations per definition; a bounded initial baseline, not repeated statistics or a salvo reproduction.";
                info["additionalTimingScope"] = "Read-only Harmony timestamps around custom damage, collision expansion, plate release, retained mesh merging, VLS support callback/batch rebuild and native detach. Each existing SubmitRenderRequest is additionally timed with a wall-clock stopwatch; it includes native submission work and any blocking wait, and is not GPU execution time. Loop minus submission is an unclassified remainder, not a physics measurement. FixedUpdate callback counts reveal steps per rendered loop. Profiler recorders are enumerated outside timed windows and report available, running and sampled evidence; unavailable timings remain unknown. Inclusive nested times must not be added. No hierarchy/mesh enumeration or JSON inside timed windows.";
                info["simulationTimingSettings"] = new Dictionary<string, object> { ["fixedDeltaTime"] = Time.fixedDeltaTime,
                    ["maximumDeltaTime"] = Time.maximumDeltaTime, ["timeScale"] = Time.timeScale };
                info["scriptableRenderPipelineBatchingEnabled"] = GraphicsSettings.useScriptableRenderPipelineBatching;
            }
            MissileDefinition weapon = Encyclopedia.Lookup.Values.OfType<MissileDefinition>().First(d =>
                string.Equals(d.jsonKey, "AShM1", StringComparison.OrdinalIgnoreCase));
            GlobalPosition subjectPoint = FindWater(origin, heading, Mathf.Max(native.width, resolute.width), Mathf.Max(native.length, resolute.length));
            GlobalPosition ownerPoint = FindWater(subjectPoint + heading * new Vector3(1200f, 0f, 0f), heading, native.width, native.length, subjectPoint);
            Ship owner = null;
            Camera camera = null;
            RenderTexture target = null;
            DamagePerformanceFrameSampler sampler = null;
            Harmony observers = null;
            Camera main = Camera.main;
            bool mainEnabled = main != null && main.enabled;
            int oldVsync = QualitySettings.vSyncCount, oldLimit = Application.targetFrameRate;
            try
            {
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                if (main != null) main.enabled = false;
                camera = new GameObject("Resolute.DamagePerformanceCamera").AddComponent<Camera>();
                camera.enabled = false; camera.fieldOfView = 48f; camera.nearClipPlane = .5f; camera.farClipPlane = 25000f;
                camera.clearFlags = CameraClearFlags.Skybox;
                target = new RenderTexture(1280, 800, 24); target.Create(); camera.targetTexture = target;
                camera.transform.position = subjectPoint.ToLocalPosition() + heading * new Vector3(170f, 115f, -205f);
                camera.transform.LookAt(subjectPoint.ToLocalPosition() + Vector3.up * 15f, Vector3.up);
                var request = new RenderPipeline.StandardRequest { destination = target };
                render = () => RenderPipeline.SubmitRenderRequest(camera, request);
                sampler = camera.gameObject.AddComponent<DamagePerformanceFrameSampler>();
                owner = Spawn(native, ownerPoint, heading, faction, "damage_performance_owner");
                observers = new Harmony(Plugin.Id + ".damage-performance");
                observers.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                    prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeDetonate)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterDetonate)));
                observers.Patch(AccessTools.Method(typeof(DamageEffects), nameof(DamageEffects.BlastFrag)),
                    prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeBlast)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterBlast)));
                observers.Patch(AccessTools.Method(typeof(Shockwave), nameof(Shockwave.SetOwner)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(ObserveShockwaveOwner)));
                observers.Patch(AccessTools.Method(typeof(Shockwave), "Start"),
                    prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeShockwaveStart)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterShockwaveStart)));
                observers.Patch(AccessTools.Method(typeof(Shockwave), "Update"),
                    prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeShockwaveUpdate)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterShockwaveUpdate)));
                observers.Patch(AccessTools.Method(typeof(UnitPart), nameof(UnitPart.TakeDamage)),
                    prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeDamage)),
                    postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterDamage)));
                if (shipRedoBaseline) InstallCustomObservers(observers);
                yield return null; yield return new WaitForFixedUpdate();
                foreach (ShipDefinition definition in new[] { native, resolute })
                foreach (string compartment in shipRedoBaseline ? new[] { "side", "forward-support" } : new[] { "Hull_R", "Hull_FR", "Hull_hangarR", "Hull_ExhaustStack" })
                {
                    report["phase"] = "damage-performance-" + definition.jsonKey + "-" + compartment;
                    Save(output, report);
                    HashSet<GameObject> existingObjects = SceneObjects();
                    Ship subject = null;
                    var shot = new ImpactCase { Redo = shipRedoBaseline };
                    try
                    {
                        subject = Spawn(definition, subjectPoint, heading, faction, "damage_performance_" + definition.jsonKey + "_" + compartment);
                        shot.Ship = subject;
                        shot.Vls = subject.GetComponent<VlsStructuralAttachments>();
                        float warmUntil = Time.realtimeSinceStartup + (shipRedoBaseline ? 10f : 1.5f);
                        while (Time.realtimeSinceStartup < warmUntil) { render(); yield return null; }
                        if (shipRedoBaseline && costRecorders == null)
                            costRecorders = StartCostRecorders((Dictionary<string, object>)report["damagePerformance"]);
                        if (shipRedoBaseline) PrepareCostSamples(shot);
                        ShipPart[] parts = subject.partLookup.OfType<ShipPart>().Where(p => p != null).ToArray();
                        float[] healthBefore = parts.Select(p => p.hitPoints).ToArray();
                        Require(checks, "damage-performance-intact-" + definition.jsonKey + "-" + compartment,
                            !subject.disabled && parts.All(p => p.hitPoints >= 99f && !p.IsDetached()) && Vector3.Dot(subject.transform.up, Vector3.up) > .99f);
                        ShipPart hitPart;
                        Vector3 direction = -(shipRedoBaseline ? subject.transform.right : heading * Vector3.right);
                        Vector3 contact;
                        if (shipRedoBaseline) hitPart = RedoSurfaceTarget(subject, compartment, out contact);
                        else { hitPart = parts.First(p => p.name == compartment); contact = SurfaceTarget(hitPart, -direction); }
                        Vector3 launch = contact - direction * 80f;
                        object before = Inventory(subject, existingObjects);
                        object detailedBefore = shipRedoBaseline ? ShipRedoBaselineTrial.Snapshot(subject) : null;
                        UnitPart[] renderParts = shipRedoBaseline ? subject.partLookup.Where(p => p != null && p.parentUnit == subject).Distinct().ToArray() : null;
                        object renderDamageBefore = shipRedoBaseline ? RenderDamageState(renderParts) : null;
                        Matrix4x4 referenceFromWorld = subject.transform.worldToLocalMatrix;
                        object intactBaseline = null, earlyDamage = null;
                        double excludedSnapshotSeconds = 0;
                        if (shipRedoBaseline)
                        {
                            var baseline = new ImpactCase { Ship = subject, LastGen0 = GC.CollectionCount(0), Redo = true };
                            PrepareCostSamples(baseline);
                            ResetCostRecorders();
                            baseline.Started = baseline.PreviousFrame = Stopwatch.GetTimestamp(); baseline.Sampling = true; active = baseline;
                            while ((Stopwatch.GetTimestamp() - baseline.Started) * MillisecondsPerTick < 10000d) yield return null;
                            baseline.Sampling = false; active = null;
                            intactBaseline = new Dictionary<string, object> { ["loops"] = baseline.FrameCount,
                                ["loopMs"] = Distribution(baseline.Frames, baseline.FrameCount), ["bufferOverflow"] = baseline.BufferOverflow,
                                ["costAttribution"] = CostAttribution(baseline, 0, 0, double.PositiveInfinity) };
                            // Native settling continues during the baseline; trace the launch contact again.
                            hitPart = RedoSurfaceTarget(subject, compartment, out contact);
                            direction = -subject.transform.right; launch = contact - direction * 80f;
                            referenceFromWorld = subject.transform.worldToLocalMatrix;
                        }
                        shot.Gen0 = shot.LastGen0 = GC.CollectionCount(0); shot.Gen1 = GC.CollectionCount(1); shot.Gen2 = GC.CollectionCount(2);
                        shot.HeapBefore = Profiler.GetMonoUsedSizeLong();
                        if (shipRedoBaseline) ResetCostRecorders();
                        shot.Started = shot.PreviousFrame = Stopwatch.GetTimestamp();
                        shot.Sampling = true; active = shot;
                        long spawnStart = Stopwatch.GetTimestamp();
                        shot.Missile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(weapon, launch, Quaternion.LookRotation(direction), direction * 250f, subject, owner);
                        double spawnMs = (Stopwatch.GetTimestamp() - spawnStart) * MillisecondsPerTick;
                        shot.MissileID = shot.Missile.persistentID; shot.Yield = shot.Missile.GetYield();
                        // The independent LateUpdate sampler continues through
                        // these yields; the launch-frame physics cost is retained.
                        yield return null; yield return new WaitForFixedUpdate();
                        if (shot.Missile != null && !shot.Missile.disabled)
                        {
                            shot.Missile.Arm(); shot.Missile.SetTangible(true);
                            shot.ArmedAfterHandoff = shot.Missile.IsArmed(); shot.ArmedPosition = shot.Missile.transform.position;
                        }
                        if (shipRedoBaseline)
                        {
                            while ((Stopwatch.GetTimestamp() - shot.Started) * MillisecondsPerTick < 10000d) yield return null;
                            shot.Sampling = false; active = null;
                            long snapshotStart = Stopwatch.GetTimestamp();
                            earlyDamage = new Dictionary<string, object> {
                                ["elapsedSeconds"] = (snapshotStart - shot.Started) * MillisecondsPerTick / 1000d,
                                ["inventory"] = Inventory(subject, existingObjects), ["vls"] = ShipRedoBaselineTrial.Vls(subject),
                                ["fragments"] = ShipRedoBaselineTrial.FragmentSnapshot(subject, existingObjects, referenceFromWorld),
                                ["renderDamageState"] = RenderDamageState(renderParts)
                            };
                            yield return null; // Exclude the post-snapshot frame and its diagnostic allocation cost.
                            ResetCostRecorders();
                            shot.PreviousFrame = Stopwatch.GetTimestamp();
                            excludedSnapshotSeconds = (shot.PreviousFrame - snapshotStart) * MillisecondsPerTick / 1000d;
                            shot.Sampling = true; active = shot;
                        }
                        while ((Stopwatch.GetTimestamp() - shot.Started) * MillisecondsPerTick < (shipRedoBaseline ? 80000d : 5000d)) yield return null;
                        shot.Sampling = false; active = null;
                        long heapChange = Profiler.GetMonoUsedSizeLong() - shot.HeapBefore;
                        int[] collections = { GC.CollectionCount(0) - shot.Gen0, GC.CollectionCount(1) - shot.Gen1, GC.CollectionCount(2) - shot.Gen2 };
                        float healthLost = 0f;
                        for (int i = 0; i < parts.Length; i++) healthLost += Mathf.Max(0f, healthBefore[i] - (parts[i] != null ? parts[i].hitPoints : 0f));
                        bool passed = shot.ArmedAfterHandoff && shot.Detonations > 0 && shot.DetonatedArmed && shot.HitArmor &&
                            (shot.Yield > 200f ? shot.ShockwaveStarts > 0 && shot.ShockwaveUpdates > 0 : shot.BlastCalls > 0) &&
                            shot.DamageCalls > 0 && healthLost > 0f && shot.FrameCount > 0 && !shot.BufferOverflow;
                        var peaks = new List<object>();
                        for (int i = 0; i < shot.FrameCount; i++)
                            if ((shot.Frames[i] > 33.333d || shot.Collections[i]) && peaks.Count < 128)
                                peaks.Add(new Dictionary<string, object> { ["elapsedSeconds"] = shot.FrameEnds[i], ["loopMs"] = shot.Frames[i], ["generation0Collection"] = shot.Collections[i] });
                        cases.Add(new Dictionary<string, object>
                        {
                            ["definition"] = definition.jsonKey, ["compartment"] = compartment,
                            ["actualTargetPart"] = hitPart.name,
                            ["launch"] = V(launch), ["expectedContact"] = V(contact), ["direction"] = V(direction),
                            ["nativeYield"] = shot.Yield, ["missileSpawnMs"] = spawnMs,
                            ["armedAfterNativeHandoff"] = shot.ArmedAfterHandoff, ["positionWhenArmed"] = V(shot.ArmedPosition),
                            ["detonationCalls"] = shot.Detonations, ["detonatedArmed"] = shot.DetonatedArmed,
                            ["hitArmor"] = shot.HitArmor, ["hitTerrain"] = shot.HitTerrain,
                            ["detonationPosition"] = V(shot.DetonationPosition), ["detonationAtSeconds"] = shot.DetonationAt,
                            ["nativeDetonateCpuMs"] = shot.DetonationTicks * MillisecondsPerTick,
                            ["nativeBlastFragCalls"] = shot.BlastCalls, ["blastAtSeconds"] = shot.BlastAt,
                            ["nativeBlastFragCpuMs"] = shot.BlastTicks * MillisecondsPerTick,
                            ["nativeBlastPath"] = shot.Yield > 200f ? "Shockwave" : "BlastFrag",
                            ["nativeShockwaveStarts"] = shot.ShockwaveStarts, ["nativeShockwaveUpdates"] = shot.ShockwaveUpdates,
                            ["nativeShockwaveInfluencedObjects"] = shot.ShockwaveInfluencedObjects,
                            ["nativeShockwaveStartCpuMs"] = shot.ShockwaveStartTicks * MillisecondsPerTick,
                            ["nativeShockwaveUpdateTotalInclusiveCpuMs"] = shot.ShockwaveUpdateTicks * MillisecondsPerTick,
                            ["nativeShockwaveUpdateMaxCpuMs"] = shot.MaxShockwaveUpdateTicks * MillisecondsPerTick,
                            ["nativeTakeDamageCalls"] = shot.DamageCalls, ["nativeTakeDamageTotalInclusiveCpuMs"] = shot.DamageTicks * MillisecondsPerTick,
                            ["nativeTakeDamageMaxCpuMs"] = shot.MaxDamageTicks * MillisecondsPerTick,
                            ["nativePartHealthLost"] = healthLost,
                            ["loops"] = shot.FrameCount, ["sampledSeconds"] = shot.FrameCount > 0 ? shot.FrameEnds[shot.FrameCount - 1] : 0d,
                            ["loopMs"] = Distribution(shot.Frames, shot.FrameCount),
                            ["loopsOver33ms"] = shot.Frames.Take(shot.FrameCount).Count(v => v > 33.333d),
                            ["loopsOver50ms"] = shot.Frames.Take(shot.FrameCount).Count(v => v > 50d),
                            ["loopsOver100ms"] = shot.Frames.Take(shot.FrameCount).Count(v => v > 100d),
                            ["collectionsByGeneration"] = collections, ["monoHeapChangeBytes"] = heapChange,
                            ["peaksAndCollections"] = peaks, ["before"] = before, ["after"] = Inventory(subject, existingObjects),
                            ["sampleBufferOverflow"] = shot.BufferOverflow, ["passed"] = passed
                        });
                        if (shipRedoBaseline)
                        {
                            var entry = (Dictionary<string, object>)cases[cases.Count - 1];
                            entry["intactLoopBaseline"] = intactBaseline; entry["detailedBefore"] = detailedBefore;
                            entry["renderDamageStateBefore"] = renderDamageBefore;
                            entry["renderDamageStateAfter"] = RenderDamageState(renderParts);
                            entry["earlyDamageOutsideTiming"] = earlyDamage; entry["excludedSnapshotSeconds"] = excludedSnapshotSeconds;
                            entry["finalVls"] = ShipRedoBaselineTrial.Vls(subject);
                            entry["finalFragments"] = ShipRedoBaselineTrial.FragmentSnapshot(subject, existingObjects, referenceFromWorld);
                            entry["customInclusiveCpu"] = customNames.Select((name, i) => (object)new Dictionary<string, object> {
                                ["method"] = name, ["calls"] = shot.CustomCalls[i], ["totalMs"] = shot.CustomTicks[i] * MillisecondsPerTick,
                                ["maxMs"] = shot.CustomMaxTicks[i] * MillisecondsPerTick }).ToArray();
                            entry["loopSegments"] = new[] { Segment(shot, 0, 1), Segment(shot, 1, 10), Segment(shot, 10, 75), Segment(shot, 75, 80) };
                            entry["costAttribution"] = CostAttribution(shot, 0, 0, double.PositiveInfinity);
                            Require(checks, "damage-performance-cost-samples-valid-" + definition.jsonKey + "-" + compartment,
                                shot.FrameCount > 0 && Enumerable.Range(0, shot.FrameCount).All(i =>
                                    shot.RenderSubmitMs[i] >= 0 && shot.RenderSubmitMs[i] <= shot.Frames[i] && shot.FixedSteps[i] >= 0));
                            entry["elapsedSecondsIncludingSnapshotGap"] = entry["sampledSeconds"];
                            entry["sampledSeconds"] = shot.Frames.Take(shot.FrameCount).Sum() / 1000d;
                            entry["fragmentInventoryScope"] = "Actual MeshFilter/Collider geometry owned by each new Rigidbody, including current coherent ShipParts and custom torn plates. Other new native bodies are labelled separately. Particle-system fragments without a Rigidbody are not measured as coherent hull gibs. Geometry uses the pre-launch ship frame and records later body rotation; before/after orientation are not interchangeable.";
                            entry["aftermathGcCaveat"] = "Whole-window heap/collection totals include the paused diagnostic snapshot. Use loop segment timing for CPU; later collections may include diagnostic allocation debt. This run does not isolate per-method allocation bytes.";
                        }
                        Save(output, report);
                        Require(checks, "damage-performance-armed-native-impact-" + definition.jsonKey + "-" + compartment, passed);
                    }
                    finally
                    {
                        shot.Sampling = false; active = null;
                        Cleanup(subject, shot.Missile, existingObjects);
                    }
                    yield return null; yield return new WaitForFixedUpdate();
                }
                Require(checks, shipRedoBaseline ? "damage-performance-redo-four-cases" : "damage-performance-eight-matched-cases", cases.Count == (shipRedoBaseline ? 4 : 8));
                report["phase"] = "complete"; Save(output, report);
            }
            finally
            {
                active = null; render = null;
                if (observers != null) observers.UnpatchSelf();
                customMethods.Clear(); customNames.Clear();
                customBatchType = null;
                if (costRecorders != null) foreach (CostRecorder source in costRecorders) source.Recorder.Dispose();
                costRecorders = null;
                if (owner != null) Object.Destroy(owner.gameObject);
                if (camera != null) { camera.targetTexture = null; Object.Destroy(camera.gameObject); }
                if (target != null) { target.Release(); Object.Destroy(target); }
                if (main != null) main.enabled = mainEnabled;
                QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldLimit;
            }
        }

        internal static void SampleFrame()
        {
            ImpactCase shot = active;
            if (shot == null || !shot.Sampling) return;
            long beforeRender = shot.Redo ? Stopwatch.GetTimestamp() : 0;
            render();
            long now = Stopwatch.GetTimestamp();
            int generation = GC.CollectionCount(0);
            if (shot.FrameCount < Capacity)
            {
                int index = shot.FrameCount++;
                shot.Frames[index] = (now - shot.PreviousFrame) * MillisecondsPerTick;
                shot.FrameEnds[index] = (now - shot.Started) * MillisecondsPerTick / 1000d;
                shot.Collections[index] = generation != shot.LastGen0;
                if (shot.Redo)
                {
                    shot.RenderSubmitMs[index] = (now - beforeRender) * MillisecondsPerTick;
                    shot.FixedSteps[index] = shot.PendingFixedSteps; shot.PendingFixedSteps = 0;
                    int segment = CostSegment(shot.FrameEnds[index]);
                    for (int i = 0; i < costRecorders.Count; i++)
                    {
                        shot.RecorderSamples[i][0].Read(costRecorders[i]);
                        if (segment > 0) shot.RecorderSamples[i][segment].Read(costRecorders[i]);
                    }
                }
            }
            else shot.BufferOverflow = true;
            shot.PreviousFrame = now; shot.LastGen0 = generation;
        }

        internal static void SampleFixedStep()
        {
            if (active != null && active.Sampling && active.Redo) active.PendingFixedSteps++;
        }

        private static void BeforeDetonate(Missile __instance, bool hitArmor, bool hitTerrain, out long __state)
        {
            __state = 0;
            if (active == null || !active.Sampling || __instance != active.Missile) return;
            active.Detonations++; active.DetonatedArmed |= __instance.IsArmed();
            active.HitArmor |= hitArmor; active.HitTerrain |= hitTerrain;
            active.DetonationPosition = __instance.transform.position;
            active.DetonationAt = (Stopwatch.GetTimestamp() - active.Started) * MillisecondsPerTick / 1000d;
            active.InNativeDetonation = true;
            __state = Stopwatch.GetTimestamp();
        }
        private static void AfterDetonate(long __state)
        {
            if (__state == 0 || active == null) return;
            active.DetonationTicks += Stopwatch.GetTimestamp() - __state;
            active.InNativeDetonation = false;
        }
        private static void BeforeBlast(PersistentID missileID, out long __state)
        {
            __state = 0;
            if (active == null || !active.Sampling || missileID != active.MissileID) return;
            active.BlastCalls++; active.BlastAt = (Stopwatch.GetTimestamp() - active.Started) * MillisecondsPerTick / 1000d;
            __state = Stopwatch.GetTimestamp();
        }
        private static void AfterBlast(long __state) { if (__state != 0 && active != null) active.BlastTicks += Stopwatch.GetTimestamp() - __state; }
        private static void ObserveShockwaveOwner(Shockwave __instance)
        {
            if (active != null && active.Sampling && active.InNativeDetonation) active.Shockwave = __instance;
        }
        private static void BeforeShockwaveStart(Shockwave __instance, out long __state)
        {
            __state = active != null && active.Sampling && active.Shockwave == __instance ? Stopwatch.GetTimestamp() : 0;
        }
        private static void AfterShockwaveStart(long __state, object ___influencedObjects)
        {
            if (__state == 0 || active == null) return;
            active.ShockwaveStartTicks += Stopwatch.GetTimestamp() - __state;
            active.ShockwaveStarts++;
            active.ShockwaveInfluencedObjects = ((ICollection)___influencedObjects).Count;
        }
        private static void BeforeShockwaveUpdate(Shockwave __instance, out long __state)
        {
            __state = active != null && active.Sampling && active.Shockwave == __instance ? Stopwatch.GetTimestamp() : 0;
        }
        private static void AfterShockwaveUpdate(long __state)
        {
            if (__state == 0 || active == null) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            active.ShockwaveUpdates++; active.ShockwaveUpdateTicks += ticks;
            active.MaxShockwaveUpdateTicks = Math.Max(active.MaxShockwaveUpdateTicks, ticks);
        }
        private static void BeforeDamage(UnitPart __instance, out long __state)
        {
            __state = active != null && active.Sampling && __instance.parentUnit == active.Ship ? Stopwatch.GetTimestamp() : 0;
        }
        private static void AfterDamage(long __state)
        {
            if (__state == 0 || active == null) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            active.DamageCalls++; active.DamageTicks += ticks; active.MaxDamageTicks = Math.Max(active.MaxDamageTicks, ticks);
        }

        private struct CustomStamp { internal long Started; internal int Index; internal bool VlsScope; }
        private static void InstallCustomObservers(Harmony observer)
        {
            customMethods.Clear(); customNames.Clear();
            AddObserver(observer, AccessTools.Method(typeof(ResoluteStructuralSection), "Damaged"), "Hull.Damaged");
            AddObserver(observer, AccessTools.Method(typeof(ResoluteStructuralSection), "ExpandCollisionGroup"), "Hull.ExpandCollisionGroup");
            AddObserver(observer, AccessTools.Method(typeof(ResoluteStructuralSection), "Release"), "Hull.Release");
            AddObserver(observer, AccessTools.Method(typeof(SurfacePaintStyle), "MergeRetained"), "Hull.MergeRetained");
            AddObserver(observer, AccessTools.Method(typeof(VlsStructuralAttachments), "SupportReleased"), "VLS.SupportReleased");
            customBatchType = AccessTools.Inner(typeof(VlsStructuralAttachments), "Batch");
            AddObserver(observer, AccessTools.Constructor(customBatchType, new[] { typeof(VlsStructuralAttachments.Fitting), typeof(VlsStructuralAttachments.Cell[]) }), "VLS.Batch.ctor");
            AddObserver(observer, AccessTools.Method(customBatchType, "Release"), "VLS.Batch.Release");
            AddObserver(observer, AccessTools.Method(typeof(UnitPart), "Detach"), "Native.UnitPart.Detach");
            AddObserver(observer, AccessTools.Method(typeof(ShipPart), "Detach"), "Native.ShipPart.Detach");
        }
        private static void AddObserver(Harmony observer, MethodBase method, string label)
        {
            if (method == null) throw new MissingMethodException("Redo timing hook unavailable: " + label);
            customMethods.Add(method, customNames.Count); customNames.Add(label);
            observer.Patch(method, prefix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(BeforeCustom)),
                postfix: new HarmonyMethod(typeof(DamagePerformanceTrial), nameof(AfterCustom)));
        }
        private static void BeforeCustom(object __instance, MethodBase __originalMethod, out CustomStamp __state)
        {
            __state = default(CustomStamp);
            var shot = active;
            if (shot == null || !shot.Sampling || !shot.Redo || !customMethods.TryGetValue(__originalMethod, out int index)) return;
            if (__instance is ResoluteStructuralSection section && (section.Part == null || section.Part.parentUnit != shot.Ship)) return;
            if (__instance is UnitPart part && part.parentUnit != shot.Ship) return;
            if (__instance is VlsStructuralAttachments attachments && attachments != shot.Vls) return;
            if (__originalMethod.DeclaringType == customBatchType && shot.VlsDepth == 0) return;
            bool vls = __instance is VlsStructuralAttachments;
            if (vls) shot.VlsDepth++;
            __state = new CustomStamp { Started = Stopwatch.GetTimestamp(), Index = index, VlsScope = vls };
        }
        private static void AfterCustom(CustomStamp __state)
        {
            if (__state.Started == 0 || active == null) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            active.CustomCalls[__state.Index]++; active.CustomTicks[__state.Index] += elapsed;
            active.CustomMaxTicks[__state.Index] = Math.Max(active.CustomMaxTicks[__state.Index], elapsed);
            if (__state.VlsScope) active.VlsDepth--;
        }
        private static object Segment(ImpactCase shot, double start, double end)
        {
            var values = Enumerable.Range(0, shot.FrameCount).Where(i => shot.FrameEnds[i] > start && shot.FrameEnds[i] <= end).Select(i => shot.Frames[i]).ToArray();
            return new Dictionary<string, object> { ["startSeconds"] = start, ["endSeconds"] = end,
                ["loops"] = values.Length, ["loopMs"] = Distribution(values, values.Length),
                ["costAttribution"] = CostAttribution(shot, CostSegment((start + end) * .5), start, end) };
        }

        private static int CostSegment(double seconds) => seconds <= 1 ? 1 : seconds <= 10 ? 2 : seconds <= 75 ? 3 : seconds <= 80 ? 4 : 0;

        private static void PrepareCostSamples(ImpactCase shot)
        {
            shot.RenderSubmitMs = new double[Capacity]; shot.FixedSteps = new int[Capacity];
            shot.RecorderSamples = costRecorders.Select(source => Enumerable.Range(0, 5).Select(i => new CostSamples()).ToArray()).ToArray();
        }

        private static void ResetCostRecorders()
        {
            if (costRecorders == null) return;
            foreach (CostRecorder source in costRecorders)
            {
                if (!source.Recorder.Valid) continue;
                // Unity 2022.3 Reset clears samples AND stops collection.
                // A fresh observation window must explicitly resume it.
                source.Recorder.Reset(); source.ResetCalls++;
                source.Recorder.Start(); source.RestartAttempts++;
                source.RunningAfterLastRestart = source.Recorder.IsRunning;
                if (!source.RunningAfterLastRestart) source.RestartFailures++;
            }
        }

        private static List<CostRecorder> StartCostRecorders(Dictionary<string, object> info)
        {
            var output = new List<CostRecorder>();
            var available = new List<object>();
            var failures = new List<object>();
            var handles = new List<ProfilerRecorderHandle>();
            var requested = new[] { "Main Thread", "Render Thread", "Physics.Simulate", "Physics.Processing",
                "Physics.FetchResults", "Physics.SyncTransforms", "FixedBehaviourUpdate", "BehaviourUpdate", "BehaviourLateUpdate",
                "Ship.ApplyJobResults", "RenderLoop.Draw", "Gfx.WaitForPresentOnGfxThread", "Gfx.WaitForRenderThread",
                "GC.Collect", "GC Allocated In Frame", "Draw Calls Count", "Triangles Count", "Vertices Count" };
            var found = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                ProfilerRecorderHandle.GetAvailable(handles);
                foreach (ProfilerRecorderHandle handle in handles)
                {
                    try
                    {
                        ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                        string name = description.Name;
                        found.Add(name);
                        bool wanted = requested.Contains(name) || name.StartsWith("Physics.", StringComparison.Ordinal);
                        bool selected = wanted && output.Count < 24;
                        var metadata = new Dictionary<string, object> { ["name"] = name, ["category"] = description.Category.Name,
                            ["unit"] = description.UnitType.ToString(), ["dataType"] = description.DataType.ToString(),
                            ["flags"] = description.Flags.ToString(), ["requested"] = wanted, ["selected"] = selected };
                        available.Add(metadata);
                        if (!selected) continue;
                        var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                        metadata["validAtStart"] = recorder.Valid;
                        metadata["runningAtStart"] = recorder.Valid && recorder.IsRunning;
                        if (!recorder.Valid) { recorder.Dispose(); continue; }
                        output.Add(new CostRecorder { Name = name, Category = description.Category.Name,
                            Unit = description.UnitType.ToString(), DataType = description.DataType.ToString(),
                            Flags = description.Flags.ToString(), Recorder = recorder });
                    }
                    catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
                }
            }
            catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
            info["profilerRecorderAvailability"] = new Dictionary<string, object> {
                ["scope"] = "Enumerated once after the first native subject warmup. At most 24 requested native counters/timings are observed. Each sampled LateUpdate reads the recorder's latest completed frame; those values can lag the current explicit render and must not be subtracted from same-loop timings. Capacity is one with native SumAllSamplesInFrame. Recorders reset and explicitly restart before intact/impact windows and after the excluded snapshot; Unity Reset stops collection. Missing, stopped and empty recorders are unknown, never zero cost. A zero-valued available sample is recorded as zero, without proving marker execution in that frame. Values are global scene counters, not subject-only costs.",
                ["resetLifecycleDocumentation"] = "https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorder.Reset.html",
                ["available"] = available, ["startedCount"] = output.Count, ["errors"] = failures,
                ["unavailableRequestedNames"] = requested.Where(name => !found.Contains(name)).ToArray() };
            return output;
        }

        private static object CostAttribution(ImpactCase shot, int segment, double start, double end)
        {
            int[] indices = Enumerable.Range(0, shot.FrameCount).Where(i => shot.FrameEnds[i] > start && shot.FrameEnds[i] <= end).ToArray();
            double[] rendering = indices.Select(i => shot.RenderSubmitMs[i]).ToArray();
            double[] remainder = indices.Select(i => shot.Frames[i] - shot.RenderSubmitMs[i]).ToArray();
            double[] steps = indices.Select(i => (double)shot.FixedSteps[i]).ToArray();
            var recorderRows = new List<object>();
            for (int i = 0; i < costRecorders.Count; i++)
            {
                CostRecorder source = costRecorders[i]; CostSamples samples = shot.RecorderSamples[i][segment];
                bool sampled = samples.Samples > 0;
                recorderRows.Add(new Dictionary<string, object> { ["name"] = source.Name, ["category"] = source.Category,
                    ["unit"] = source.Unit, ["dataType"] = source.DataType, ["flags"] = source.Flags,
                    ["validAtSummary"] = source.Recorder.Valid, ["runningAtSummary"] = source.Recorder.Valid && source.Recorder.IsRunning,
                    ["resetCalls"] = source.ResetCalls, ["restartAttempts"] = source.RestartAttempts,
                    ["restartFailures"] = source.RestartFailures, ["runningAfterLastRestart"] = source.RunningAfterLastRestart,
                    ["latestCompletedFrameReads"] = samples.Samples, ["zeroReads"] = samples.ZeroSamples,
                    ["invalidOrStoppedReads"] = samples.InvalidReads, ["emptyReads"] = samples.EmptyReads,
                    ["rawValue"] = sampled ? new Dictionary<string, object> { ["mean"] = samples.Sum / samples.Samples,
                        ["min"] = samples.Minimum, ["max"] = samples.Maximum } : null,
                    ["meanMsIfNanoseconds"] = sampled && source.Unit == "TimeNanoseconds" ? (object)(samples.Sum / samples.Samples / 1000000d) : null });
            }
            return new Dictionary<string, object> { ["loops"] = indices.Length,
                ["renderSubmitInclusiveWallMs"] = Distribution(rendering, rendering.Length),
                ["loopMinusRenderSubmitMs"] = Distribution(remainder, remainder.Length),
                ["fixedCallbacksPerLoop"] = Distribution(steps, steps.Length), ["fixedCallbacksTotal"] = steps.Sum(),
                ["loopsWithMultipleFixedCallbacks"] = steps.Count(value => value > 1),
                ["renderSubmitTotalWallMs"] = rendering.Sum(), ["loopMinusRenderSubmitTotalMs"] = remainder.Sum(),
                ["profilerLatestCompletedFrameValues"] = recorderRows };
        }
        private static ShipPart RedoSurfaceTarget(Ship ship, string station, out Vector3 contact)
        {
            Vector3 center = ship.transform.TransformPoint(new Vector3(0, 0, ship.definition.length * (station == "forward-support" ? .20f : -.05f)));
            center.y = Datum.LocalSeaY + 3f;
            Vector3 side = ship.transform.right;
            var ray = new Ray(center + side * 500f, -side);
            float distance = float.PositiveInfinity; ShipPart target = null; contact = default(Vector3);
            foreach (ShipPart part in ship.partLookup.OfType<ShipPart>().Where(p => p != null))
            foreach (Collider collider in part.GetComponents<Collider>())
                if (collider.enabled && !collider.isTrigger && collider.Raycast(ray, out var hit, 1000f) && hit.distance < distance)
                { target = part; distance = hit.distance; contact = hit.point; }
            if (target == null) throw new InvalidOperationException("No native surface at matched redo station: " + ship.definition.jsonKey + " " + station);
            return target;
        }

        internal static Ship Spawn(ShipDefinition definition, GlobalPosition point, Quaternion heading, FactionHQ faction, string name)
        {
            point.y = Datum.SeaLevel.y + definition.spawnOffset.y;
            var saved = new SavedShip(name) { type = definition.jsonKey, faction = faction.faction.factionName,
                globalPosition = point, rotation = heading, skill = 1f, holdPosition = true };
            Ship ship;
            if (!NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship)) throw new InvalidOperationException("Damage performance ship spawn failed.");
            ship.LinkSavedUnit(saved); MissionTrial.HoldControllers(ship); ship.SetHoldPosition(true);
            return ship;
        }

        private static Vector3 SurfaceTarget(ShipPart part, Vector3 side)
        {
            Collider[] colliders = part.GetComponents<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if (colliders.Length == 0) throw new InvalidOperationException("No active native compartment collider: " + part.name);
            Bounds bounds = colliders[0].bounds;
            foreach (Collider collider in colliders) bounds.Encapsulate(collider.bounds);
            Vector3 center = bounds.center;
            center.y = Mathf.Clamp(Mathf.Max(center.y, Datum.LocalSeaY + 2f), bounds.min.y + .15f, bounds.max.y - .15f);
            var ray = new Ray(center + side * 500f, -side);
            RaycastHit best = default(RaycastHit); float distance = float.PositiveInfinity;
            foreach (Collider collider in colliders)
                if (collider.Raycast(ray, out var hit, 1000f) && hit.distance < distance) { best = hit; distance = hit.distance; }
            if (float.IsPositiveInfinity(distance)) throw new InvalidOperationException("No starboard native collision surface: " + part.name);
            return best.point;
        }

        internal static GlobalPosition FindWater(GlobalPosition origin, Quaternion heading, float width, float length, GlobalPosition? reserved = null)
        {
            float halfWidth = width * .5f + 15f, halfLength = length * .5f + 25f;
            int columns = Mathf.Max(2, Mathf.CeilToInt(halfWidth / 25f)), rows = Mathf.Max(2, Mathf.CeilToInt(halfLength / 25f));
            Ship[] occupied = UnitRegistry.allUnits.OfType<Ship>().Where(s => s != null).ToArray();
            for (float radius = 0f; radius <= 3500f; radius += 350f)
            for (int candidate = 0; candidate < (radius == 0f ? 1 : 16); candidate++)
            {
                float angle = candidate * Mathf.PI / 8f;
                GlobalPosition point = origin + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                point.y = Datum.SeaLevel.y;
                if (reserved.HasValue && Vector3.ProjectOnPlane(point.AsVector3() - reserved.Value.AsVector3(), Vector3.up).sqrMagnitude < 700f * 700f) continue;
                if (occupied.Any(s => Vector3.ProjectOnPlane(s.GlobalPosition().AsVector3() - point.AsVector3(), Vector3.up).sqrMagnitude < 700f * 700f)) continue;
                bool clear = true;
                for (int x = -columns; x <= columns && clear; x++)
                for (int z = -rows; z <= rows; z++)
                {
                    GlobalPosition probe = point + heading * new Vector3(x * halfWidth / columns, 0f, z * halfLength / rows);
                    if (!PathfindingAgent.RaycastTerrain(probe, out var hit)) { clear = false; break; }
                    float depth = Datum.LocalSeaY - hit.point.y;
                    if (float.IsNaN(depth) || float.IsInfinity(depth) || depth < 40f) { clear = false; break; }
                }
                if (clear) return point;
            }
            throw new InvalidOperationException("No padded native terrain footprint with 40 metre depth for damage performance trial.");
        }

        internal static HashSet<GameObject> SceneObjects() => new HashSet<GameObject>(Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.scene.IsValid()));
        private static object RenderDamageState(UnitPart[] originalParts)
        {
            var parts = new List<object>();
            var rendererBlock = new MaterialPropertyBlock();
            var slotBlock = new MaterialPropertyBlock();
            for (int index = 0; index < originalParts.Length; index++)
            {
                UnitPart part = originalParts[index];
                if (part == null)
                {
                    parts.Add(new Dictionary<string, object> { ["originalPartIndex"] = index, ["stillExists"] = false });
                    continue;
                }
                var renderers = new List<object>();
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r != null && r.GetComponentInParent<UnitPart>() == part))
                {
                    rendererBlock.Clear(); renderer.GetPropertyBlock(rendererBlock);
                    bool rendererHasHitPoints = rendererBlock.HasFloat(HitPointsProperty);
                    var slots = new List<object>();
                    Material[] materials = renderer.sharedMaterials;
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        Material material = materials[slot];
                        slotBlock.Clear(); renderer.GetPropertyBlock(slotBlock, slot);
                        bool slotHasHitPoints = slotBlock.HasFloat(HitPointsProperty);
                        bool materialHasHitPoints = material != null && material.HasProperty(HitPointsProperty);
                        slots.Add(new Dictionary<string, object> { ["slot"] = slot,
                            ["materialInstanceId"] = material != null ? (object)material.GetInstanceID() : null,
                            ["material"] = material != null ? material.name : null,
                            ["shader"] = material != null && material.shader != null ? material.shader.name : null,
                            ["materialHasHitPointsProperty"] = materialHasHitPoints,
                            ["materialHitPoints"] = materialHasHitPoints ? (object)material.GetFloat(HitPointsProperty) : null,
                            ["slotPropertyBlockEmpty"] = slotBlock.isEmpty, ["slotBlockHasHitPointsFloat"] = slotHasHitPoints,
                            ["slotBlockHitPoints"] = slotHasHitPoints ? (object)slotBlock.GetFloat(HitPointsProperty) : null });
                    }
                    renderers.Add(new Dictionary<string, object> { ["name"] = renderer.name, ["instanceId"] = renderer.GetInstanceID(),
                        ["type"] = renderer.GetType().Name, ["enabled"] = renderer.enabled, ["activeInHierarchy"] = renderer.gameObject.activeInHierarchy,
                        ["hasAnyPropertyBlock_rendererWideQuery"] = renderer.HasPropertyBlock(),
                        ["rendererWidePropertyBlockEmpty"] = rendererBlock.isEmpty, ["rendererWideBlockHasHitPointsFloat"] = rendererHasHitPoints,
                        ["rendererWideBlockHitPoints"] = rendererHasHitPoints ? (object)rendererBlock.GetFloat(HitPointsProperty) : null,
                        ["materialSlots"] = slots });
                }
                parts.Add(new Dictionary<string, object> { ["originalPartIndex"] = index, ["stillExists"] = true,
                    ["instanceId"] = part.GetInstanceID(), ["name"] = part.name, ["type"] = part.GetType().Name,
                    ["hitPoints"] = part.hitPoints, ["detached"] = part.IsDetached(), ["renderers"] = renderers });
            }
            return new Dictionary<string, object> {
                ["scope"] = "Read only outside timing windows. Original native UnitParts retain stable array indices across before/early/after snapshots; destroyed parts are marked missing. Renderers belong to their nearest UnitPart, including detached native parts and inactive renderers. Free custom debris without a UnitPart is outside this view. Renderer.HasPropertyBlock is a renderer-wide any-block query, not a per-slot API. Renderer-wide and indexed blocks are separately retrieved and their isEmpty/HasFloat values recorded. Missing _HitPoints is null, not zero. Material values come from sharedMaterials without instantiating or modifying materials; these are separate source values, not an inferred rendered effective value.",
                ["scriptableRenderPipelineBatchingEnabled"] = GraphicsSettings.useScriptableRenderPipelineBatching,
                ["parts"] = parts };
        }

        private static object Inventory(Ship subject, HashSet<GameObject> existing)
        {
            Rigidbody[] bodies = Resources.FindObjectsOfTypeAll<Rigidbody>().Where(b => b.gameObject.scene.IsValid() && !existing.Contains(b.gameObject)).ToArray();
            return new Dictionary<string, object>
            {
                ["newSceneRigidbodies"] = bodies.Length, ["newDynamicRigidbodies"] = bodies.Count(b => !b.isKinematic),
                ["newMeshRigidbodyObjects"] = bodies.Count(b => b.GetComponent<MeshFilter>() != null),
                ["newMeshColliderRigidbodyObjects"] = bodies.Count(b => b.GetComponent<MeshCollider>() != null),
                ["nativeDetachedParts"] = subject != null ? subject.partLookup.Count(p => p != null && p.IsDetached()) : 0,
                ["newActiveSceneRenderers"] = Resources.FindObjectsOfTypeAll<Renderer>().Count(r => r.gameObject.scene.IsValid() && !existing.Contains(r.gameObject) && r.enabled && r.gameObject.activeInHierarchy),
                ["subjectDisabled"] = subject == null || subject.disabled
            };
        }

        internal static void Cleanup(Ship subject, Missile missile, HashSet<GameObject> existing)
        {
            // Native breakup reparents parts and effects away from the root.
            // Existing scene objects were snapshotted before this fresh subject.
            var roots = new HashSet<GameObject>();
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!gameObject.scene.IsValid() || existing.Contains(gameObject)) continue;
                if (gameObject.GetComponent<Rigidbody>() == null && gameObject.GetComponent<ParticleSystem>() == null &&
                    gameObject.GetComponent<AudioSource>() == null && gameObject.GetComponent<Renderer>() == null &&
                    gameObject.GetComponent<Shockwave>() == null && gameObject.GetComponent<UnityEngine.Rendering.Universal.DecalProjector>() == null) continue;
                Transform root = gameObject.transform;
                while (root.parent != null && !existing.Contains(root.parent.gameObject)) root = root.parent;
                roots.Add(root.gameObject);
            }
            if (subject != null) roots.Add(subject.gameObject);
            if (missile != null) roots.Add(missile.gameObject);
            foreach (GameObject root in roots) if (root != null) Object.Destroy(root);
        }

        private static object Distribution(double[] values, int count)
        {
            if (count == 0) return null;
            double[] sorted = values.Take(count).OrderBy(v => v).ToArray();
            return new Dictionary<string, object> { ["median"] = sorted[count / 2], ["p95"] = sorted[(int)((count - 1) * .95)],
                ["p99"] = sorted[(int)((count - 1) * .99)], ["max"] = sorted[count - 1], ["mean"] = sorted.Average() };
        }
        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static void Save(string output, Dictionary<string, object> report) => File.WriteAllText(output, Audit.Json(report));
        private static void Require(List<object> checks, string name, bool value)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = value });
            if (!value) throw new InvalidOperationException("Damage performance check failed: " + name);
        }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class DamagePerformanceFrameSampler : MonoBehaviour
    {
        private void FixedUpdate() => DamagePerformanceTrial.SampleFixedStep();
        private void LateUpdate() => DamagePerformanceTrial.SampleFrame();
    }
}
