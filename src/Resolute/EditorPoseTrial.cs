using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit editor experiment. No HarmonyPatch attributes: none of these
    // redirects or observers is installed by the production PatchAll call.
    internal static class EditorPoseTrial
    {
        private sealed class Subject
        {
            internal Ship Ship;
            internal GlobalPosition Origin;
            internal Collider[] Colliders;
            internal bool[] Enabled;
            internal Renderer[] BodyRenderers;
            internal Transform[] Parts;
            internal bool Kinematic;
            internal int RigidbodyCount;
        }

        private sealed class Snapshot
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Bounds[] Colliders, Bodies;
            internal Vector3[] Parts;
            internal bool NativeSelectionRayHit, SettingsUnchanged, SavedPoseMatches;
        }

        private sealed class CallbackCost
        {
            internal string Name;
            internal long Calls, Ticks, Maximum;
        }

        private sealed class MassState
        {
            internal bool AutomaticCenter, AutomaticInertia;
            internal Vector3 Center, Tensor;
            internal Quaternion TensorRotation;
            internal float Mass;
        }

        private sealed class RecorderCost : IDisposable
        {
            internal string Name, Unit;
            internal ProfilerRecorder Recorder;
            internal readonly List<double> Values = new List<double>(16000);
            internal void Read() { if (Recorder.Valid && Recorder.Count > 0) Values.Add(Recorder.LastValue); }
            public void Dispose() { Recorder.Dispose(); }
        }

        private static readonly Dictionary<Transform, Subject> Roots = new Dictionary<Transform, Subject>();
        private static readonly HashSet<object> ObservedComponents = new HashSet<object>();
        private static readonly Dictionary<MethodBase, CallbackCost> CallbackCosts = new Dictionary<MethodBase, CallbackCost>();
        private static readonly MethodInfo PositionSetter = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.position));
        private static readonly MethodInfo RotationSetter = AccessTools.PropertySetter(typeof(Transform), nameof(Transform.rotation));
        private static readonly double TickMs = 1000d / Stopwatch.Frequency;
        private static bool redirect, observeCallbacks;
        private static int redirectedPositions, redirectedRotations, fallbackPositions, fallbackRotations, ordinaryPositions, ordinaryRotations;
        private static long fixedSteps;

        internal static IEnumerator RunInertia(GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game" || GameManager.gameState != GameState.Editor)
                throw new InvalidOperationException("Inertia trial requires native Editor state in the isolated test-game copy.");
            var phases = new List<object>();
            var result = new Dictionary<string, object> {
                ["scope"] = "Diagnostic only, paused native editor. The production editor mass-property optimization is suspended during this A/B experiment. Native SavedUnit Transform setters and every collider stay unchanged. Only the test Resolute's automatic inertia flag, then automatic inertia and center-of-mass flags, are temporarily disabled while retaining the measured original mass properties. Native Dynamo is untouched in every mode. Both flags and values are restored before the final native repeat and during cleanup.",
                ["protocol"] = "One and four ships of each definition; native, inertia-fixed, both-fixed, native-restored. Every mode performs 240 full-yaw rotation steps and 240 lateral sine placement steps. Wrapper, SyncTransforms and render timings are separate. Four matching poses per mode verify immediate visible placement, post-sync collider/body bounds and selection raycasts, and the next loop. Mass properties are checked as a full local inertia matrix, allowing relative floating-point tolerance of 0.0001.",
                ["incomingTimeScale"] = Time.timeScale, ["physicsAutoSyncTransforms"] = Physics.autoSyncTransforms,
                ["phases"] = phases };
            report["editorInertia"] = result;
            float oldScale = Time.timeScale;
            int oldLimit = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount;
            Camera main = Camera.main, camera = null;
            bool mainEnabled = main != null && main.enabled;
            RenderTexture texture = null;
            var subjects = new List<Subject>();
            var originalMass = new Dictionary<Subject, MassState>();
            GameObject counterObject = null;
            try
            {
                ResoluteEditorMassProperties.SuspendForDiagnostic(true);
                TimeScaleManager.Scale = 0f; QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                if (main != null) main.enabled = false;
                camera = new GameObject("Resolute.EditorInertiaCamera").AddComponent<Camera>();
                camera.enabled = false; camera.fieldOfView = 48f; camera.nearClipPlane = .5f; camera.farClipPlane = 25000f;
                camera.clearFlags = CameraClearFlags.Skybox;
                texture = new RenderTexture(1280, 800, 24); texture.Create(); camera.targetTexture = texture;
                var renderRequest = new RenderPipeline.StandardRequest { destination = texture };
                Action render = () => RenderPipeline.SubmitRenderRequest(camera, renderRequest);
                counterObject = new GameObject("Resolute.EditorInertiaFixedStepObserver");
                counterObject.AddComponent<FixedStepObserver>();
                foreach (int count in new[] { 1, 4 })
                foreach (string key in new[] { "Destroyer1", Plugin.DefinitionKey })
                {
                    var phase = new Dictionary<string, object> { ["definition"] = key, ["ships"] = count };
                    phases.Add(phase); report["phase"] = "editor-inertia-" + key + "-" + count; Save(output, report);
                    var definition = (ShipDefinition)Encyclopedia.Lookup[key];
                    Vector3 grid = count == 4 ? new Vector3(165f, 0f, 180f) : Vector3.zero;
                    camera.transform.position = origin.ToLocalPosition() + heading * (grid + (count == 4 ? new Vector3(-480, 350, -500) : new Vector3(-170, 115, -205)));
                    camera.transform.LookAt(origin.ToLocalPosition() + heading * (grid + Vector3.up * 15f), Vector3.up);
                    for (int i = 0; i < count; i++)
                    {
                        GlobalPosition position = origin + heading * new Vector3((i % 2) * 330f, definition.spawnOffset.y, (i / 2) * 360f);
                        Ship ship = (Ship)NetworkSceneSingleton<Spawner>.i.SpawnFromUnitDefinitionInEditor(definition, position, heading, faction, "rsl_inertia_" + phases.Count + "_" + i);
                        var saved = new SavedShip("rsl_inertia_" + phases.Count + "_" + i);
                        saved.AfterCreate(ship); ship.LinkSavedUnit(saved); saved.AfterLoadEditor();
                        yield return null;
                        var subject = new Subject { Ship = ship, Origin = position,
                            Colliders = ship.GetComponentsInChildren<Collider>(true),
                            Parts = ship.partLookup.Where(p => p != null).Select(p => p.transform).ToArray(),
                            BodyRenderers = ship.partLookup.Where(p => p != null).Select(p => {
                                ResoluteStructuralSection section = p.GetComponent<ResoluteStructuralSection>();
                                return section != null ? section.Body : p.GetComponent<Renderer>();
                            }).Where(r => r != null).ToArray(),
                            Kinematic = ship.rb.isKinematic, RigidbodyCount = ship.GetComponentsInChildren<Rigidbody>(true).Length };
                        subject.Enabled = subject.Colliders.Select(c => c.enabled).ToArray(); subjects.Add(subject);
                    }
                    Physics.SyncTransforms();
                    float warmUntil = Time.realtimeSinceStartup + 4f;
                    while (Time.realtimeSinceStartup < warmUntil) { render(); yield return null; }
                    foreach (Subject subject in subjects) originalMass.Add(subject, ReadMass(subject.Ship.rb));
                    phase["initialMassProperties"] = subjects.Select(s => MassRecord(originalMass[s])).ToArray();
                    var modes = new List<object>(); phase["modes"] = modes;
                    var nativePoses = new List<Snapshot[]>();
                    foreach (string mode in new[] { "native", "inertia-fixed", "both-fixed", "native-restored" })
                    {
                        foreach (Subject subject in subjects)
                        {
                            if (!Plugin.IsResolute(subject.Ship.definition) || mode == "native") continue;
                            MassState state = originalMass[subject]; Rigidbody body = subject.Ship.rb;
                            RestoreMass(body, state);
                            if (mode == "inertia-fixed" || mode == "both-fixed")
                            {
                                body.automaticInertiaTensor = false;
                                body.inertiaTensor = state.Tensor; body.inertiaTensorRotation = state.TensorRotation;
                                if (mode == "both-fixed") { body.automaticCenterOfMass = false; body.centerOfMass = state.Center; }
                            }
                        }
                        ResetPose(subjects, heading, phase); render(); yield return null;
                        var row = new Dictionary<string, object> { ["mode"] = mode,
                            ["before"] = subjects.Select(s => MassRecord(ReadMass(s.Ship.rb))).ToArray() };
                        modes.Add(row); var operations = new List<object>(); row["operations"] = operations;
                        foreach (bool rotate in new[] { true, false })
                        {
                            int step = 0;
                            var operation = new Dictionary<string, object> { ["operation"] = rotate ? "rotation" : "placement" };
                            operations.Add(operation);
                            Action move = () => {
                                step++;
                                foreach (Subject subject in subjects)
                                    if (rotate) subject.Ship.SavedUnit.RotationWrapper.SetValue(heading * Quaternion.Euler(0f, step * 1.5f, 0f), phase);
                                    else subject.Ship.SavedUnit.PositionWrapper.SetValue(subject.Origin + heading * (Vector3.right * (Mathf.Sin(step * (2f * Mathf.PI / 240f)) * 40f)), phase);
                            };
                            IEnumerator sample = Sample(0f, 240, operation, move, true, render);
                            while (sample.MoveNext()) yield return sample.Current;
                            Save(output, report);
                        }
                        var proofs = new List<object>(); row["poseProof"] = proofs;
                        for (int pose = 0; pose < 4; pose++)
                        {
                            Quaternion rotation = heading * Quaternion.Euler(0f, 17f + pose * 83f, 0f);
                            Vector3 offset = heading * new Vector3(23f - pose * 11f, pose % 2 == 0 ? 0f : 3f, -19f + pose * 7f);
                            foreach (Subject subject in subjects)
                            {
                                subject.Ship.SavedUnit.PositionWrapper.SetValue(subject.Origin + offset, phase);
                                subject.Ship.SavedUnit.RotationWrapper.SetValue(rotation, phase);
                            }
                            Snapshot[] immediate = subjects.Select(Capture).ToArray(); Physics.SyncTransforms();
                            Snapshot[] synchronized = subjects.Select(Capture).ToArray(); render(); yield return null;
                            Snapshot[] following = subjects.Select(Capture).ToArray();
                            bool immediateCorrect = immediate.All(s => s.SavedPoseMatches && s.SettingsUnchanged);
                            bool syncCorrect = synchronized.All(s => s.SavedPoseMatches && s.SettingsUnchanged && s.NativeSelectionRayHit);
                            bool nextCorrect = following.All(s => s.SavedPoseMatches && s.SettingsUnchanged && s.NativeSelectionRayHit) && Match(synchronized, following);
                            bool nativeMatch = mode == "native" || Match(nativePoses[pose], synchronized);
                            if (mode == "native") nativePoses.Add(synchronized);
                            proofs.Add(new Dictionary<string, object> { ["pose"] = pose, ["visibleTransformImmediate"] = immediateCorrect,
                                ["synchronizedPoseAndSelection"] = syncCorrect, ["nextLoopPoseAndBounds"] = nextCorrect, ["colliderAndBodyBoundsMatchNative"] = nativeMatch });
                            Check(checks, "editor-inertia-pose-" + key + "-" + count + "-" + mode + "-" + pose, immediateCorrect && syncCorrect && nextCorrect && nativeMatch);
                        }
                        row["after"] = subjects.Select(s => MassRecord(ReadMass(s.Ship.rb))).ToArray();
                        bool properties = subjects.All(s => mode == "both-fixed" && Plugin.IsResolute(s.Ship.definition)
                            ? ExactMass(originalMass[s], ReadMass(s.Ship.rb)) : SameMass(originalMass[s], ReadMass(s.Ship.rb)));
                        row["massPropertiesPreserved"] = properties;
                        Check(checks, "editor-inertia-mass-" + key + "-" + count + "-" + mode, properties);
                        Save(output, report);
                    }
                    foreach (Subject subject in subjects) if (Plugin.IsResolute(subject.Ship.definition)) RestoreMass(subject.Ship.rb, originalMass[subject]);
                    ResetPose(subjects, heading, phase);
                    bool restored = subjects.All(s => { MassState now = ReadMass(s.Ship.rb), initial = originalMass[s];
                        return SameMass(initial, now) && now.AutomaticCenter == initial.AutomaticCenter && now.AutomaticInertia == initial.AutomaticInertia; });
                    phase["allMassSettingsRestored"] = restored;
                    Check(checks, "editor-inertia-restored-" + key + "-" + count, restored);
                    Check(checks, "editor-inertia-health-" + key + "-" + count, subjects.All(s => !s.Ship.disabled && s.Ship.parts.All(p => p != null && p.hitPoints >= 99f)));
                    Save(output, report); DestroySubjects(subjects); originalMass.Clear();
                    yield return null; yield return null;
                }
                result["complete"] = phases.Count == 4; report["phase"] = "complete"; Save(output, report);
            }
            finally
            {
                foreach (Subject subject in subjects) if (subject.Ship != null && originalMass.ContainsKey(subject) && Plugin.IsResolute(subject.Ship.definition))
                    RestoreMass(subject.Ship.rb, originalMass[subject]);
                DestroySubjects(subjects);
                if (counterObject != null) Object.Destroy(counterObject);
                if (camera != null) { camera.targetTexture = null; Object.Destroy(camera.gameObject); }
                if (texture != null) { texture.Release(); Object.Destroy(texture); }
                if (main != null) main.enabled = mainEnabled;
                TimeScaleManager.Scale = oldScale; QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldLimit;
                ResoluteEditorMassProperties.SuspendForDiagnostic(false);
            }
        }

        private static MassState ReadMass(Rigidbody body) => new MassState { AutomaticCenter = body.automaticCenterOfMass,
            AutomaticInertia = body.automaticInertiaTensor, Center = body.centerOfMass, Tensor = body.inertiaTensor,
            TensorRotation = body.inertiaTensorRotation, Mass = body.mass };

        private static void RestoreMass(Rigidbody body, MassState state)
        {
            body.automaticCenterOfMass = false; body.automaticInertiaTensor = false;
            body.centerOfMass = state.Center; body.inertiaTensor = state.Tensor; body.inertiaTensorRotation = state.TensorRotation;
            body.automaticCenterOfMass = state.AutomaticCenter; body.automaticInertiaTensor = state.AutomaticInertia;
        }

        private static object MassRecord(MassState state) => new Dictionary<string, object> {
            ["automaticCenterOfMass"] = state.AutomaticCenter, ["automaticInertiaTensor"] = state.AutomaticInertia,
            ["centerOfMass"] = new[] { state.Center.x, state.Center.y, state.Center.z },
            ["inertiaTensor"] = new[] { state.Tensor.x, state.Tensor.y, state.Tensor.z },
            ["inertiaTensorRotation"] = new[] { state.TensorRotation.x, state.TensorRotation.y, state.TensorRotation.z, state.TensorRotation.w }, ["mass"] = state.Mass };

        private static bool SameMass(MassState a, MassState b)
        {
            if (a.Mass != b.Mass || Vector3.Distance(a.Center, b.Center) > .005f) return false;
            Matrix4x4 ar = Matrix4x4.Rotate(a.TensorRotation), br = Matrix4x4.Rotate(b.TensorRotation);
            Matrix4x4 am = ar * Matrix4x4.Scale(a.Tensor) * ar.transpose, bm = br * Matrix4x4.Scale(b.Tensor) * br.transpose;
            float magnitude = 1f, error = 0f;
            for (int row = 0; row < 3; row++) for (int column = 0; column < 3; column++)
            { magnitude = Mathf.Max(magnitude, Mathf.Abs(am[row, column])); error = Mathf.Max(error, Mathf.Abs(am[row, column] - bm[row, column])); }
            return error / magnitude < .0001f;
        }

        private static bool ExactMass(MassState a, MassState b) => a.Mass == b.Mass && a.Center.Equals(b.Center) &&
            a.Tensor.Equals(b.Tensor) && a.TensorRotation.Equals(b.TensorRotation);

        private static Ship lifecycleShip;
        private static Missile lifecycleMissile;
        private static int lifecycleDamageCalls, lifecycleDetaches, lifecycleStarts, lifecycleDetonations;
        private static bool lifecycleRestoredAtDamage, lifecycleFrozenDuringDamage, lifecycleArmedImpact;

        internal static IEnumerator RunLifecycle(GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game" || GameManager.gameState != GameState.Editor)
                throw new InvalidOperationException("Editor lifecycle trial requires the native paused editor in test-game.");
            var phases = new List<object>();
            var result = new Dictionary<string, object> {
                ["scope"] = "Production editor-only mass-property preservation and native restoration on the same initialized ship instance. Tests scale-first exit, state-first exit, both return orders and direct Unity time-scale resumption. Native PlayFromEditor itself stops and reloads the mission, so this deliberately stronger same-object transition test uses its actual GameManager and TimeScaleManager setters without destroying the subject.",
                ["damageProtocol"] = "After the final restoration, up to four native AShM1 armor impacts against attached hull compartments, stopping after native ShipPart.Detach occurs. Short 80 metre approach uses native Arm/SetTangible after missile handoff. No forced health, detachment, detonation, rigidbody mass or collider changes. Native damage/part detachment and the resulting root mass/inertia recalculation are observed.",
                ["phases"] = phases };
            report["editorLifecycle"] = result;
            GameState oldState = GameManager.gameState; float oldScale = Time.timeScale;
            Ship owner = null; HashSet<GameObject> existing = null;
            var subjects = new List<Subject>();
            var hooks = new Harmony(Plugin.Id + ".editor-lifecycle");
            MethodInfo findWater = AccessTools.Method(typeof(DamagePerformanceTrial), "FindWater");
            MethodInfo surfaceTarget = AccessTools.Method(typeof(DamagePerformanceTrial), "SurfaceTarget");
            MethodInfo cleanup = AccessTools.Method(typeof(DamagePerformanceTrial), "Cleanup");
            ShipDefinition native = (ShipDefinition)Encyclopedia.Lookup["Destroyer1"];
            ShipDefinition resolute = (ShipDefinition)Encyclopedia.Lookup[Plugin.DefinitionKey];
            GlobalPosition point = (GlobalPosition)findWater.Invoke(null, new object[] { origin, heading, Mathf.Max(native.width, resolute.width), Mathf.Max(native.length, resolute.length), null });
            GlobalPosition ownerPoint = (GlobalPosition)findWater.Invoke(null, new object[] { point + heading * new Vector3(1200f, 0f, 0f), heading, native.width, native.length, (GlobalPosition?)point });
            MissileDefinition weapon = Encyclopedia.Lookup.Values.OfType<MissileDefinition>().First(d => d.jsonKey == "AShM1");
            try
            {
                hooks.Patch(AccessTools.Method(typeof(UnitPart), nameof(UnitPart.TakeDamage)), prefix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(LifecycleDamage)));
                hooks.Patch(AccessTools.Method(typeof(ShipPart), nameof(ShipPart.Detach)), postfix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(LifecycleDetached)));
                hooks.Patch(AccessTools.Method(typeof(Ship), "OnStartClient"), postfix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(LifecycleStarted)));
                hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)), prefix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(LifecycleDetonate)));
                owner = (Ship)AccessTools.Method(typeof(DamagePerformanceTrial), "Spawn").Invoke(null, new object[] { native, ownerPoint, heading, faction, "rsl_editor_lifecycle_owner" });
                yield return null;
                foreach (ShipDefinition definition in new[] { native, resolute })
                {
                    TimeScaleManager.Scale = 0f; GameManager.SetGameState(GameState.Editor);
                    existing = new HashSet<GameObject>(Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.scene.IsValid()));
                    var phase = new Dictionary<string, object> { ["definition"] = definition.jsonKey };
                    phases.Add(phase); report["phase"] = "editor-lifecycle-" + definition.jsonKey; Save(output, report);
                    GlobalPosition position = point; position.y = Datum.SeaLevel.y + definition.spawnOffset.y;
                    Ship ship = (Ship)NetworkSceneSingleton<Spawner>.i.SpawnFromUnitDefinitionInEditor(definition, position, heading, faction, "rsl_lifecycle_" + definition.jsonKey);
                    var saved = new SavedShip("rsl_lifecycle_" + definition.jsonKey);
                    saved.AfterCreate(ship); ship.LinkSavedUnit(saved); saved.AfterLoadEditor(); MissionTrial.HoldControllers(ship);
                    var subject = new Subject { Ship = ship, Origin = position,
                        Colliders = ship.GetComponentsInChildren<Collider>(true), Parts = ship.partLookup.Where(p => p != null).Select(p => p.transform).ToArray(),
                        BodyRenderers = ship.partLookup.Where(p => p != null).Select(p => { var section = p.GetComponent<ResoluteStructuralSection>(); return section != null ? section.Body : p.GetComponent<Renderer>(); }).Where(r => r != null).ToArray(),
                        Kinematic = ship.rb.isKinematic, RigidbodyCount = ship.GetComponentsInChildren<Rigidbody>(true).Length };
                    subject.Enabled = subject.Colliders.Select(c => c.enabled).ToArray(); subjects.Add(subject);
                    Physics.SyncTransforms(); yield return null; yield return null;
                    lifecycleShip = ship; lifecycleStarts = 0;
                    var preservation = ship.GetComponent<ResoluteEditorMassProperties>();
                    int id = ship.GetInstanceID(); bool isResolute = Plugin.IsResolute(definition);
                    phase["instanceId"] = id; phase["initialMassProperties"] = MassRecord(ReadMass(ship.rb));
                    Check(checks, "editor-lifecycle-initial-freeze-" + definition.jsonKey, LifecycleFlags(ship, preservation, isResolute));
                    MassState frozen = ReadMass(ship.rb);
                    for (int pose = 0; pose < 4; pose++)
                    {
                        saved.PositionWrapper.SetValue(position + heading * new Vector3(pose * 11f - 14f, pose % 2 == 0 ? 0f : 3f, pose * 7f), phase);
                        saved.RotationWrapper.SetValue(heading * Quaternion.Euler(0f, 31f + pose * 83f, 0f), phase);
                        Snapshot immediate = Capture(subject); Physics.SyncTransforms(); Snapshot synchronized = Capture(subject);
                        yield return null; Snapshot following = Capture(subject);
                        Check(checks, "editor-lifecycle-pose-" + definition.jsonKey + "-" + pose,
                            immediate.SavedPoseMatches && immediate.SettingsUnchanged && synchronized.NativeSelectionRayHit &&
                            following.SavedPoseMatches && following.SettingsUnchanged && following.NativeSelectionRayHit && Match(new[] { synchronized }, new[] { following }));
                        Check(checks, "editor-lifecycle-mass-fixed-" + definition.jsonKey + "-" + pose,
                            isResolute ? ExactMass(frozen, ReadMass(ship.rb)) : SameMass(frozen, ReadMass(ship.rb)));
                    }
                    ResetPose(subjects, heading, phase);
                    var transitions = new List<object>(); phase["transitions"] = transitions;
                    TimeScaleManager.Scale = 1f;
                    LifecycleState("scale-first-exit-before-state-change", ship, preservation, false, id, transitions, checks);
                    GameManager.SetGameState(GameState.SinglePlayer);
                    yield return new WaitForFixedUpdate();
                    LifecycleState("scale-first-first-physics", ship, preservation, false, id, transitions, checks);
                    TimeScaleManager.Scale = 0f;
                    LifecycleState("paused-live-before-editor-state", ship, preservation, false, id, transitions, checks);
                    GameManager.SetGameState(GameState.Editor);
                    LifecycleState("scale-first-return", ship, preservation, isResolute, id, transitions, checks);
                    GameManager.SetGameState(GameState.SinglePlayer);
                    LifecycleState("state-first-exit-before-time-resumes", ship, preservation, false, id, transitions, checks);
                    TimeScaleManager.Scale = 1f;
                    yield return new WaitForFixedUpdate();
                    LifecycleState("state-first-first-physics", ship, preservation, false, id, transitions, checks);
                    GameManager.SetGameState(GameState.Editor);
                    LifecycleState("editor-state-before-pausing", ship, preservation, false, id, transitions, checks);
                    TimeScaleManager.Scale = 0f;
                    LifecycleState("state-first-return", ship, preservation, isResolute, id, transitions, checks);
                    var fixedProbe = ship.gameObject.AddComponent<LifecycleFixedProbe>(); fixedProbe.Ship = ship; fixedProbe.Preservation = preservation;
                    Time.timeScale = 1f;
                    yield return new WaitForFixedUpdate();
                    LifecycleState("direct-unity-time-first-physics", ship, preservation, false, id, transitions, checks);
                    Check(checks, "editor-lifecycle-direct-resume-before-ordinary-fixed-update-" + definition.jsonKey,
                        fixedProbe.Steps > 0 && fixedProbe.RestoredBeforeEveryStep);
                    Object.Destroy(fixedProbe);
                    GameManager.SetGameState(GameState.SinglePlayer);
                    float settle = Time.realtimeSinceStartup + 1.5f;
                    while (Time.realtimeSinceStartup < settle) yield return null;
                    phase["beforeDamageMassProperties"] = MassRecord(ReadMass(ship.rb));
                    float massBefore = ship.rb.mass;
                    ShipPart[] parts = ship.partLookup.OfType<ShipPart>().Where(p => p != null).ToArray();
                    float[] healthBefore = parts.Select(p => p.hitPoints).ToArray();
                    lifecycleDamageCalls = lifecycleDetaches = lifecycleDetonations = 0;
                    lifecycleRestoredAtDamage = lifecycleFrozenDuringDamage = lifecycleArmedImpact = false;
                    var shots = new List<object>(); phase["shots"] = shots;
                    for (int shot = 0; shot < 4 && lifecycleDetaches == 0; shot++)
                    {
                        ShipPart hitPart = parts.FirstOrDefault(p => p != null && !p.IsDetached() &&
                            (p.name == "Hull_R" || p.name == "Hull_FR") && p.GetComponents<Collider>().Any(c => c.enabled && !c.isTrigger));
                        if (hitPart == null) break;
                        Vector3 side = ship.transform.right;
                        Vector3 contact = (Vector3)surfaceTarget.Invoke(null, new object[] { hitPart, side });
                        lifecycleMissile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(weapon, contact + side * 80f,
                            Quaternion.LookRotation(-side), -side * 250f, ship, owner);
                        yield return null; yield return new WaitForFixedUpdate();
                        bool armed = lifecycleMissile != null && !lifecycleMissile.disabled;
                        if (armed) { lifecycleMissile.Arm(); lifecycleMissile.SetTangible(true); armed = lifecycleMissile.IsArmed(); }
                        float until = Time.realtimeSinceStartup + 5f;
                        while (Time.realtimeSinceStartup < until) yield return null;
                        shots.Add(new Dictionary<string, object> { ["compartment"] = hitPart.name, ["armedAfterHandoff"] = armed,
                            ["nativeDetonationsSoFar"] = lifecycleDetonations, ["nativeDetachCallsSoFar"] = lifecycleDetaches });
                        Save(output, report);
                    }
                    float healthLost = 0f;
                    for (int p = 0; p < parts.Length; p++) healthLost += Mathf.Max(0f, healthBefore[p] - (parts[p] == null ? 0f : parts[p].hitPoints));
                    phase["nativeDamageCalls"] = lifecycleDamageCalls; phase["nativeDetachCalls"] = lifecycleDetaches;
                    phase["nativePartHealthLost"] = healthLost; phase["restoredAtFirstNativeDamage"] = lifecycleRestoredAtDamage;
                    phase["frozenDuringNativeDamage"] = lifecycleFrozenDuringDamage;
                    phase["afterDamageMassProperties"] = MassRecord(ReadMass(ship.rb)); phase["additionalOnStartClientCalls"] = lifecycleStarts;
                    Check(checks, "editor-lifecycle-native-armed-damage-after-restore-" + definition.jsonKey,
                        lifecycleArmedImpact && lifecycleDamageCalls > 0 && healthLost > 0f && lifecycleRestoredAtDamage && !lifecycleFrozenDuringDamage);
                    Check(checks, "editor-lifecycle-native-detachment-recalculates-mass-" + definition.jsonKey,
                        lifecycleDetaches > 0 && ship.rb.mass < massBefore && LifecycleFlags(ship, preservation, false) &&
                        ship.rb.inertiaTensor.x > 0f && ship.rb.inertiaTensor.y > 0f && ship.rb.inertiaTensor.z > 0f);
                    Check(checks, "editor-lifecycle-no-reinitialization-" + definition.jsonKey, lifecycleStarts == 0 && ship.GetInstanceID() == id);
                    lifecycleShip = null; cleanup.Invoke(null, new object[] { ship, lifecycleMissile, existing });
                    lifecycleMissile = null; subjects.Clear(); existing = null; yield return null; yield return null;
                }
                result["complete"] = phases.Count == 2; report["phase"] = "complete"; Save(output, report);
            }
            finally
            {
                hooks.UnpatchSelf(); lifecycleShip = null;
                if (existing != null && subjects.Count != 0) cleanup.Invoke(null, new object[] { subjects[0].Ship, lifecycleMissile, existing });
                else DestroySubjects(subjects);
                lifecycleMissile = null;
                if (owner != null) Object.Destroy(owner.gameObject);
                TimeScaleManager.Scale = 0f; GameManager.SetGameState(oldState); TimeScaleManager.Scale = oldScale;
            }
        }

        private static bool LifecycleFlags(Ship ship, ResoluteEditorMassProperties preservation, bool frozen) => frozen
            ? preservation != null && preservation.Frozen && preservation.enabled && !ship.rb.automaticCenterOfMass && !ship.rb.automaticInertiaTensor
            : (preservation == null || (!preservation.Frozen && !preservation.enabled)) && ship.rb.automaticCenterOfMass && ship.rb.automaticInertiaTensor;

        private static void LifecycleState(string stage, Ship ship, ResoluteEditorMassProperties preservation, bool frozen,
            int id, List<object> transitions, List<object> checks)
        {
            bool passed = ship.GetInstanceID() == id && LifecycleFlags(ship, preservation, frozen);
            transitions.Add(new Dictionary<string, object> { ["stage"] = stage, ["gameState"] = GameManager.gameState.ToString(),
                ["timeScale"] = Time.timeScale, ["sameInstance"] = ship.GetInstanceID() == id, ["passed"] = passed,
                ["massProperties"] = MassRecord(ReadMass(ship.rb)) });
            Check(checks, "editor-lifecycle-" + ship.definition.jsonKey + "-" + stage, passed);
        }

        private static void LifecycleDamage(UnitPart __instance)
        {
            if (lifecycleShip == null || __instance.parentUnit != lifecycleShip) return;
            var preservation = lifecycleShip.GetComponent<ResoluteEditorMassProperties>();
            if (lifecycleDamageCalls++ == 0) lifecycleRestoredAtDamage = LifecycleFlags(lifecycleShip, preservation, false);
            lifecycleFrozenDuringDamage |= preservation != null && preservation.Frozen;
        }
        private static void LifecycleDetached(ShipPart __instance) { if (lifecycleShip != null && __instance.parentUnit == lifecycleShip) lifecycleDetaches++; }
        private static void LifecycleStarted(Ship __instance) { if (__instance == lifecycleShip) lifecycleStarts++; }
        private static void LifecycleDetonate(Missile __instance, bool hitArmor)
        { if (__instance == lifecycleMissile) { lifecycleDetonations++; lifecycleArmedImpact |= __instance.IsArmed() && hitArmor; } }

        [DefaultExecutionOrder(-31999)]
        internal sealed class LifecycleFixedProbe : MonoBehaviour
        {
            internal Ship Ship;
            internal ResoluteEditorMassProperties Preservation;
            internal int Steps;
            internal bool RestoredBeforeEveryStep = true;
            private void FixedUpdate() { Steps++; RestoredBeforeEveryStep &= LifecycleFlags(Ship, Preservation, false); }
        }

        internal static IEnumerator Run(GlobalPosition origin, Quaternion heading, FactionHQ faction,
            Dictionary<string, object> report, List<object> checks, string output)
        {
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game")
                throw new InvalidOperationException("Editor pose trial requires the isolated test-game copy.");
            if (GameManager.gameState != GameState.Editor) throw new InvalidOperationException("Editor pose trial requires native Editor state.");
            var phases = new List<object>();
            float incomingTimeScale = Time.timeScale;
            var result = new Dictionary<string, object> {
                ["scope"] = "Diagnostic A/B only. Native SavedUnit editor callbacks are transpiled only during this run; the candidate redirects only registered Resolute roots to their existing Rigidbody pose, with an immediate Transform fallback if needed. Ordinary Dynamo roots retain native Transform setters. No collider, kinematic state, damage, material, geometry or global physics setting is changed.",
                ["protocol"] = "One and four ships of each definition; native, candidate, then repeated-native order. Each manipulation has 240 fixed steps: a complete 360 degree yaw and one 40 metre lateral sine cycle. Wrapper, explicit Physics.SyncTransforms, and explicit native URP render CPU intervals are measured separately. Whole loops include normal game work. Pose, collider bounds, body-renderer bounds, native selection raycasts and next-loop checks run separately from timed manipulation windows.",
                ["steadyProtocol"] = "Eight-second rendered, no-explicit-render, and sync-without-render windows, each at least 600 loops. The sync window measures transform flushing, not Physics.Simulate. A separate rendered window observes ship-owned Update/FixedUpdate/LateUpdate/ApplyJobResults callbacks and available Unity profiler counters. Callback times are inclusive, carry instrumentation overhead, and must not be summed across nested methods.",
                ["incomingTimeScale"] = incomingTimeScale, ["nativeEditorTimeScale"] = 0f,
                ["editorTimeScaleBasis"] = "Native MissionEditor.Start and ReturnToEditor set TimeScaleManager.Scale to zero. A/B validation uses that paused editor state so pose visibility cannot rely on a subsequent physics step.",
                ["physicsAutoSyncTransforms"] = Physics.autoSyncTransforms, ["physicsSimulationMode"] = Physics.simulationMode.ToString(),
                ["graphicsDevice"] = SystemInfo.graphicsDeviceName, ["qualityLevel"] = QualitySettings.GetQualityLevel(),
                ["phases"] = phases };
            report["editorPose"] = result;
            var hooks = new Harmony("Resolute.EditorPoseTrial.CallbackRedirect");
            var timingHooks = new Harmony("Resolute.EditorPoseTrial.CallbackTiming");
            Camera main = Camera.main, camera = null;
            bool mainEnabled = main != null && main.enabled;
            RenderTexture texture = null;
            int oldLimit = Application.targetFrameRate, oldVsync = QualitySettings.vSyncCount;
            var ships = new List<Subject>();
            GameObject counterObject = null;
            try
            {
                TimeScaleManager.Scale = 0f;
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                if (main != null) main.enabled = false;
                camera = new GameObject("Resolute.EditorPoseCamera").AddComponent<Camera>();
                camera.enabled = false; camera.fieldOfView = 48f; camera.nearClipPlane = .5f; camera.farClipPlane = 25000f;
                camera.clearFlags = CameraClearFlags.Skybox;
                texture = new RenderTexture(1280, 800, 24); texture.Create(); camera.targetTexture = texture;
                var request = new RenderPipeline.StandardRequest { destination = texture };
                Action render = () => RenderPipeline.SubmitRenderRequest(camera, request);
                counterObject = new GameObject("Resolute.EditorPoseFixedStepObserver");
                counterObject.AddComponent<FixedStepObserver>();
                MethodInfo[] callbacks = typeof(SavedUnit).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    .Where(m => m.Name.StartsWith("<AfterLoadEditor>", StringComparison.Ordinal)).ToArray();
                if (callbacks.Length != 2) throw new InvalidOperationException("Unexpected native editor callback count: " + callbacks.Length);
                foreach (MethodInfo callback in callbacks)
                    hooks.Patch(callback, transpiler: new HarmonyMethod(typeof(EditorPoseTrial), nameof(RedirectSetter)));
                result["patchedNativeCallbacks"] = callbacks.Select(m => m.DeclaringType.FullName + "." + m.Name).ToArray();

                foreach (int count in new[] { 1, 4 })
                foreach (string key in new[] { "Destroyer1", Plugin.DefinitionKey })
                {
                    var phase = new Dictionary<string, object> { ["definition"] = key, ["ships"] = count };
                    phases.Add(phase); report["phase"] = "editor-pose-" + key + "-" + count; Save(output, report);
                    ShipDefinition definition = (ShipDefinition)Encyclopedia.Lookup[key];
                    Vector3 grid = count == 4 ? new Vector3(165f, 0f, 180f) : Vector3.zero;
                    camera.transform.position = origin.ToLocalPosition() + heading * (grid + (count == 4 ? new Vector3(-480, 350, -500) : new Vector3(-170, 115, -205)));
                    camera.transform.LookAt(origin.ToLocalPosition() + heading * (grid + Vector3.up * 15f), Vector3.up);
                    for (int i = 0; i < count; i++)
                    {
                        GlobalPosition position = origin + heading * new Vector3((i % 2) * 330f, definition.spawnOffset.y, (i / 2) * 360f);
                        Ship ship = (Ship)NetworkSceneSingleton<Spawner>.i.SpawnFromUnitDefinitionInEditor(definition, position, heading, faction, "rsl_pose_" + phases.Count + "_" + i);
                        var saved = new SavedShip("rsl_pose_" + phases.Count + "_" + i);
                        saved.AfterCreate(ship); ship.LinkSavedUnit(saved); saved.AfterLoadEditor();
                        yield return null;
                        var subject = new Subject { Ship = ship, Origin = position,
                            Colliders = ship.GetComponentsInChildren<Collider>(true),
                            Parts = ship.partLookup.Where(p => p != null).Select(p => p.transform).ToArray(),
                            BodyRenderers = ship.partLookup.Where(p => p != null).Select(p => {
                                ResoluteStructuralSection section = p.GetComponent<ResoluteStructuralSection>();
                                return section != null ? section.Body : p.GetComponent<Renderer>();
                            }).Where(r => r != null).ToArray(),
                            Kinematic = ship.rb.isKinematic, RigidbodyCount = ship.GetComponentsInChildren<Rigidbody>(true).Length };
                        subject.Enabled = subject.Colliders.Select(c => c.enabled).ToArray();
                        ships.Add(subject); Roots.Add(ship.transform, subject);
                    }
                    Physics.SyncTransforms();
                    float warmUntil = Time.realtimeSinceStartup + 4f;
                    while (Time.realtimeSinceStartup < warmUntil) { render(); yield return null; }
                    phase["timeScale"] = Time.timeScale;
                    phase["inventory"] = ships.Select(s => GeometryInventory(s)).ToArray();
                    var steadyRows = new List<object>(); phase["steadyIsolation"] = steadyRows;
                    foreach (string mode in new[] { "rendered", "no-explicit-render", "sync-without-render" })
                    {
                        var row = new Dictionary<string, object> { ["mode"] = mode }; steadyRows.Add(row);
                        IEnumerator sample = Sample(8f, 600, row, null, mode == "sync-without-render", mode == "rendered" ? render : null);
                        while (sample.MoveNext()) yield return sample.Current;
                        Save(output, report);
                    }

                    InstallCallbackObservers(timingHooks, ships);
                    List<RecorderCost> recorders = StartRecorders();
                    var observed = new Dictionary<string, object>(); phase["observedSteady"] = observed;
                    observeCallbacks = true;
                    IEnumerator observedSample = Sample(8f, 600, observed, null, false, render, recorders);
                    while (observedSample.MoveNext()) yield return observedSample.Current;
                    observeCallbacks = false;
                    observed["callbacks"] = CallbackCosts.Values.OrderByDescending(c => c.Ticks).Select(c => new Dictionary<string, object> {
                        ["method"] = c.Name, ["calls"] = c.Calls, ["totalInclusiveMs"] = c.Ticks * TickMs,
                        ["maximumInclusiveMs"] = c.Maximum * TickMs }).ToArray();
                    observed["profilerCounters"] = recorders.Select(r => new Dictionary<string, object> {
                        ["name"] = r.Name, ["unit"] = r.Unit, ["values"] = Distribution(r.Values) }).ToArray();
                    foreach (RecorderCost recorder in recorders) recorder.Dispose();
                    timingHooks.UnpatchSelf(); ObservedComponents.Clear(); CallbackCosts.Clear();
                    Save(output, report);

                    var manipulations = new List<object>(); phase["manipulations"] = manipulations;
                    foreach (string route in new[] { "native", "candidate", "native-repeat" })
                    {
                        redirect = route == "candidate";
                        ResetPose(ships, heading, phase); render(); yield return null;
                        foreach (bool rotation in new[] { true, false })
                        {
                            ResetCounters();
                            int step = 0;
                            var row = new Dictionary<string, object> { ["route"] = route, ["operation"] = rotation ? "rotation" : "placement" };
                            manipulations.Add(row);
                            Action operation = () => {
                                step++;
                                foreach (Subject subject in ships)
                                    if (rotation) subject.Ship.SavedUnit.RotationWrapper.SetValue(heading * Quaternion.Euler(0f, step * 1.5f, 0f), phase);
                                    else subject.Ship.SavedUnit.PositionWrapper.SetValue(subject.Origin + heading * (Vector3.right * (Mathf.Sin(step * (2f * Mathf.PI / 240f)) * 40f)), phase);
                            };
                            IEnumerator sample = Sample(0f, 240, row, operation, true, render);
                            while (sample.MoveNext()) yield return sample.Current;
                            row["setterCounts"] = CounterSnapshot();
                            bool routed = key == "Destroyer1" || route != "candidate" ? redirectedPositions + redirectedRotations == 0 :
                                rotation ? redirectedRotations == 240 * count : redirectedPositions >= 238 * count;
                            Check(checks, "editor-pose-route-" + key + "-" + count + "-" + route + "-" + rotation, routed);
                            Save(output, report);
                        }
                    }

                    var proofRows = new List<object>(); phase["poseProof"] = proofRows;
                    var nativeSnapshots = new List<Snapshot[]>();
                    foreach (bool candidate in new[] { false, true })
                    {
                        redirect = candidate;
                        for (int pose = 0; pose < 4; pose++)
                        {
                            Quaternion rotation = heading * Quaternion.Euler(0f, 17f + pose * 83f, 0f);
                            Vector3 offset = heading * new Vector3(23f - pose * 11f, pose % 2 == 0 ? 0f : 3f, -19f + pose * 7f);
                            foreach (Subject subject in ships)
                            {
                                subject.Ship.SavedUnit.PositionWrapper.SetValue(subject.Origin + offset, phase);
                                subject.Ship.SavedUnit.RotationWrapper.SetValue(rotation, phase);
                            }
                            Snapshot[] immediate = ships.Select(Capture).ToArray();
                            Physics.SyncTransforms();
                            Snapshot[] synchronized = ships.Select(Capture).ToArray();
                            render();
                            yield return null;
                            Snapshot[] following = ships.Select(Capture).ToArray();
                            bool visibleImmediately = immediate.All(s => s.SavedPoseMatches && s.SettingsUnchanged);
                            bool synchronizedCorrect = synchronized.All(s => s.SavedPoseMatches && s.SettingsUnchanged && s.NativeSelectionRayHit);
                            bool nextCorrect = following.All(s => s.SavedPoseMatches && s.SettingsUnchanged && s.NativeSelectionRayHit);
                            bool boundsMatch = !candidate || Match(nativeSnapshots[pose], synchronized);
                            bool nextBoundsMatch = Match(synchronized, following);
                            if (!candidate) nativeSnapshots.Add(synchronized);
                            var row = new Dictionary<string, object> { ["route"] = candidate ? "candidate" : "native", ["pose"] = pose,
                                ["visibleTransformImmediate"] = visibleImmediately, ["synchronizedPoseAndSelection"] = synchronizedCorrect,
                                ["nextLoopPoseAndSelection"] = nextCorrect, ["colliderAndBodyBoundsMatchNative"] = boundsMatch,
                                ["nextLoopBoundsStable"] = nextBoundsMatch };
                            proofRows.Add(row);
                            Check(checks, "editor-pose-correct-" + key + "-" + count + "-" + candidate + "-" + pose,
                                visibleImmediately && synchronizedCorrect && nextCorrect && boundsMatch && nextBoundsMatch);
                        }
                    }
                    redirect = false; ResetPose(ships, heading, phase);
                    phase["allShipsHealthy"] = ships.All(s => s.Ship != null && !s.Ship.disabled && s.Ship.parts.All(p => p != null && p.hitPoints >= 99f));
                    Check(checks, "editor-pose-health-" + key + "-" + count, (bool)phase["allShipsHealthy"]);
                    Save(output, report);
                    DestroySubjects(ships);
                    yield return null; yield return null;
                }
                result["complete"] = phases.Count == 4;
                report["phase"] = "complete"; Save(output, report);
            }
            finally
            {
                redirect = false; observeCallbacks = false;
                hooks.UnpatchSelf(); timingHooks.UnpatchSelf();
                Roots.Clear(); ObservedComponents.Clear(); CallbackCosts.Clear();
                DestroySubjects(ships);
                if (counterObject != null) Object.Destroy(counterObject);
                if (camera != null) { camera.targetTexture = null; Object.Destroy(camera.gameObject); }
                if (texture != null) { texture.Release(); Object.Destroy(texture); }
                if (main != null) main.enabled = mainEnabled;
                TimeScaleManager.Scale = incomingTimeScale;
                QualitySettings.vSyncCount = oldVsync; Application.targetFrameRate = oldLimit;
            }
        }

        private static IEnumerable<CodeInstruction> RedirectSetter(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(PositionSetter) || instruction.Calls(RotationSetter))
                {
                    bool position = instruction.Calls(PositionSetter);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(EditorPoseTrial), position ? nameof(SetPosition) : nameof(SetRotation));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Unexpected native editor setter count: " + replaced);
        }

        private static void SetPosition(Transform target, Vector3 value)
        {
            Subject subject;
            if (redirect && Roots.TryGetValue(target, out subject) && Plugin.IsResolute(subject.Ship.definition))
            {
                redirectedPositions++;
                subject.Ship.rb.position = value;
                if ((target.position - value).sqrMagnitude > 1e-10f) { fallbackPositions++; target.position = value; }
            }
            else { ordinaryPositions++; target.position = value; }
        }

        private static void SetRotation(Transform target, Quaternion value)
        {
            Subject subject;
            if (redirect && Roots.TryGetValue(target, out subject) && Plugin.IsResolute(subject.Ship.definition))
            {
                redirectedRotations++;
                subject.Ship.rb.rotation = value;
                if (Quaternion.Angle(target.rotation, value) > .0001f) { fallbackRotations++; target.rotation = value; }
            }
            else { ordinaryRotations++; target.rotation = value; }
        }

        private static IEnumerator Sample(float seconds, int steps, Dictionary<string, object> row, Action operation,
            bool synchronize, Action render, List<RecorderCost> recorders = null)
        {
            var loop = new List<double>(32000); var wrapper = new List<double>(32000);
            var sync = new List<double>(32000); var rendering = new List<double>(32000);
            long start = Stopwatch.GetTimestamp(), previous = start, firstFixed = fixedSteps;
            int[] gc = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            while (loop.Count < steps || seconds > 0f && (Stopwatch.GetTimestamp() - start) * TickMs < seconds * 1000d)
            {
                long before = Stopwatch.GetTimestamp(); operation?.Invoke(); long afterWrapper = Stopwatch.GetTimestamp();
                if (synchronize) Physics.SyncTransforms(); long afterSync = Stopwatch.GetTimestamp();
                render?.Invoke(); long afterRender = Stopwatch.GetTimestamp();
                yield return null;
                long now = Stopwatch.GetTimestamp();
                loop.Add((now - previous) * TickMs); wrapper.Add((afterWrapper - before) * TickMs);
                sync.Add((afterSync - afterWrapper) * TickMs); rendering.Add((afterRender - afterSync) * TickMs);
                if (recorders != null) foreach (RecorderCost recorder in recorders) recorder.Read();
                previous = now;
            }
            int[] deltaGc = { GC.CollectionCount(0) - gc[0], GC.CollectionCount(1) - gc[1], GC.CollectionCount(2) - gc[2] };
            row["loops"] = loop.Count; row["elapsedSeconds"] = (Stopwatch.GetTimestamp() - start) * TickMs / 1000d;
            row["fixedSteps"] = fixedSteps - firstFixed; row["collectionsByGeneration"] = deltaGc;
            row["loopMs"] = Distribution(loop); row["wrapperCpuMs"] = Distribution(wrapper);
            row["syncCpuMs"] = Distribution(sync); row["renderCpuMs"] = Distribution(rendering);
        }

        private static void InstallCallbackObservers(Harmony hooks, List<Subject> subjects)
        {
            ObservedComponents.Clear(); CallbackCosts.Clear();
            foreach (Subject subject in subjects)
            foreach (MonoBehaviour component in subject.Ship.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                ObservedComponents.Add(component);
                foreach (string name in new[] { "Update", "FixedUpdate", "LateUpdate", "ApplyJobResults" })
                {
                    MethodInfo method = AccessTools.Method(component.GetType(), name, Type.EmptyTypes);
                    if (method == null || method.IsStatic || method.IsAbstract || method.GetMethodBody() == null || CallbackCosts.ContainsKey(method)) continue;
                    CallbackCosts.Add(method, new CallbackCost { Name = method.DeclaringType.FullName + "." + name });
                    hooks.Patch(method, prefix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(BeforeCallback)),
                        postfix: new HarmonyMethod(typeof(EditorPoseTrial), nameof(AfterCallback)));
                }
            }
        }

        private static void BeforeCallback(object __instance, out long __state)
        { __state = observeCallbacks && ObservedComponents.Contains(__instance) ? Stopwatch.GetTimestamp() : 0; }

        private static void AfterCallback(MethodBase __originalMethod, long __state)
        {
            if (__state == 0) return;
            long ticks = Stopwatch.GetTimestamp() - __state;
            CallbackCost cost = CallbackCosts[__originalMethod]; cost.Calls++; cost.Ticks += ticks; cost.Maximum = Math.Max(cost.Maximum, ticks);
        }

        private static List<RecorderCost> StartRecorders()
        {
            var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
            var recorders = new List<RecorderCost>();
            foreach (ProfilerRecorderHandle handle in handles)
            {
                ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
                string name = description.Name;
                if (!(name.StartsWith("Physics.", StringComparison.Ordinal) || name == "BehaviourUpdate" || name == "BehaviourLateUpdate" ||
                    name == "FixedBehaviourUpdate" || name == "RenderLoop.Draw" || name == "Draw Calls Count" || name == "Triangles Count" || name == "Vertices Count")) continue;
                if (recorders.Count >= 24) break;
                var recorder = new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                if (recorder.Valid) recorders.Add(new RecorderCost { Name = name, Unit = description.UnitType.ToString(), Recorder = recorder });
                else recorder.Dispose();
            }
            return recorders;
        }

        private static object GeometryInventory(Subject subject)
        {
            Renderer[] renderers = subject.Ship.GetComponentsInChildren<Renderer>(true);
            var sections = new List<object>();
            foreach (ShipPart part in subject.Ship.partLookup.OfType<ShipPart>())
            {
                Renderer[] owned = renderers.Where(r => r.GetComponentInParent<UnitPart>() == part).ToArray();
                sections.Add(new Dictionary<string, object> { ["part"] = part.name,
                    ["enabledMeshVertices"] = MeshTotal(owned.Where(r => r.enabled && r.gameObject.activeInHierarchy), false),
                    ["enabledMeshTriangles"] = MeshTotal(owned.Where(r => r.enabled && r.gameObject.activeInHierarchy), true),
                    ["visibleMeshVerticesAnyCamera"] = MeshTotal(owned.Where(r => r.enabled && r.isVisible), false),
                    ["visibleMeshTrianglesAnyCamera"] = MeshTotal(owned.Where(r => r.enabled && r.isVisible), true),
                    ["activeColliderCount"] = subject.Colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy && c.GetComponent<UnitPart>() == part) });
            }
            return new Dictionary<string, object> { ["renderers"] = renderers.Length,
                ["enabledRenderers"] = renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy),
                ["visibleRenderersAnyCamera"] = renderers.Count(r => r.isVisible),
                ["enabledMeshTriangles"] = MeshTotal(renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy), true),
                ["enabledMeshVertices"] = MeshTotal(renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy), false),
                ["colliders"] = subject.Colliders.Length, ["activeColliders"] = subject.Colliders.Count(c => c.enabled && c.gameObject.activeInHierarchy),
                ["isKinematic"] = subject.Kinematic, ["rigidbodies"] = subject.RigidbodyCount, ["sections"] = sections };
        }

        private static long MeshTotal(IEnumerable<Renderer> renderers, bool triangles)
        {
            long total = 0;
            foreach (Renderer renderer in renderers)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : (renderer as SkinnedMeshRenderer)?.sharedMesh;
                if (mesh == null) continue;
                if (!triangles) total += mesh.vertexCount;
                else for (int i = 0; i < mesh.subMeshCount; i++) if (mesh.GetTopology(i) == MeshTopology.Triangles) total += mesh.GetIndexCount(i) / 3;
            }
            return total;
        }

        private static Snapshot Capture(Subject subject)
        {
            Ship ship = subject.Ship;
            // Read the visible root pose before any collider bounds or ray query
            // can cause an automatic physics synchronization in this game build.
            Vector3 visiblePosition = ship.transform.position;
            Quaternion visibleRotation = ship.transform.rotation;
            var snapshot = new Snapshot { Position = visiblePosition, Rotation = visibleRotation,
                Colliders = subject.Colliders.Select(c => c.bounds).ToArray(), Bodies = subject.BodyRenderers.Select(r => r.bounds).ToArray(),
                Parts = subject.Parts.Select(p => p.position).ToArray(),
                SettingsUnchanged = ship.rb.isKinematic == subject.Kinematic && ship.GetComponentsInChildren<Rigidbody>(true).Length == subject.RigidbodyCount &&
                    subject.Colliders.Select((c, i) => c.enabled == subject.Enabled[i]).All(v => v),
                SavedPoseMatches = Vector3.Distance(visiblePosition, ship.SavedUnit.globalPosition.ToLocalPosition()) < .002f &&
                    Quaternion.Angle(visibleRotation, ship.SavedUnit.rotation) < .02f };
            Bounds all = new Bounds(ship.transform.position, Vector3.zero);
            foreach (Collider collider in subject.Colliders) if (collider.enabled && collider.gameObject.activeInHierarchy) all.Encapsulate(collider.bounds);
            Vector3 rayOrigin = all.center + Vector3.up * (all.extents.y + 100f);
            foreach (RaycastHit hit in Physics.RaycastAll(rayOrigin, Vector3.down, all.size.y + 200f, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider.GetComponentInParent<Unit>() == ship) { snapshot.NativeSelectionRayHit = true; break; }
            return snapshot;
        }

        private static bool Match(Snapshot[] expected, Snapshot[] actual)
        {
            if (expected.Length != actual.Length) return false;
            for (int i = 0; i < expected.Length; i++)
            {
                Snapshot a = expected[i], b = actual[i];
                if (Vector3.Distance(a.Position, b.Position) > .002f || Quaternion.Angle(a.Rotation, b.Rotation) > .02f ||
                    a.Colliders.Length != b.Colliders.Length || a.Bodies.Length != b.Bodies.Length || a.Parts.Length != b.Parts.Length) return false;
                for (int n = 0; n < a.Colliders.Length; n++) if (!SameBounds(a.Colliders[n], b.Colliders[n])) return false;
                for (int n = 0; n < a.Bodies.Length; n++) if (!SameBounds(a.Bodies[n], b.Bodies[n])) return false;
                for (int n = 0; n < a.Parts.Length; n++) if (Vector3.Distance(a.Parts[n], b.Parts[n]) > .02f) return false;
            }
            return true;
        }

        private static bool SameBounds(Bounds a, Bounds b) => Vector3.Distance(a.center, b.center) < .02f && Vector3.Distance(a.size, b.size) < .02f;
        private static void ResetPose(List<Subject> subjects, Quaternion heading, object source)
        {
            foreach (Subject subject in subjects)
            { subject.Ship.SavedUnit.PositionWrapper.SetValue(subject.Origin, source); subject.Ship.SavedUnit.RotationWrapper.SetValue(heading, source); }
            Physics.SyncTransforms();
        }
        private static void ResetCounters()
        { redirectedPositions = redirectedRotations = fallbackPositions = fallbackRotations = ordinaryPositions = ordinaryRotations = 0; }
        private static object CounterSnapshot() => new Dictionary<string, object> {
            ["redirectedPositions"] = redirectedPositions, ["redirectedRotations"] = redirectedRotations,
            ["immediateTransformPositionFallbacks"] = fallbackPositions, ["immediateTransformRotationFallbacks"] = fallbackRotations,
            ["nativePositions"] = ordinaryPositions, ["nativeRotations"] = ordinaryRotations };
        private static object Distribution(List<double> values)
        {
            if (values.Count == 0) return new Dictionary<string, object> { ["samples"] = 0 };
            double[] sorted = values.OrderBy(v => v).ToArray();
            return new Dictionary<string, object> { ["samples"] = sorted.Length, ["median"] = sorted[sorted.Length / 2],
                ["p95"] = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .95))],
                ["p99"] = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * .99))], ["max"] = sorted[sorted.Length - 1], ["mean"] = sorted.Average() };
        }
        private static void DestroySubjects(List<Subject> subjects)
        {
            foreach (Subject subject in subjects) if (subject.Ship != null)
            { Roots.Remove(subject.Ship.transform); NetworkManagerNuclearOption.i.ServerObjectManager.Destroy(subject.Ship.Identity, true); }
            subjects.Clear();
        }
        private static void Check(List<object> checks, string name, bool passed)
        { checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed }); }
        private static void Save(string output, Dictionary<string, object> report) { File.WriteAllText(output, Audit.Json(report)); }
        internal sealed class FixedStepObserver : MonoBehaviour { private void FixedUpdate() { fixedSteps++; } }
    }
}
