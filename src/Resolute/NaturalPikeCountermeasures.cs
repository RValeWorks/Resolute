using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Adapts the native aircraft-only countermeasure entry points to this one
    // missile. Native IRFlare simulation and RadarParams remain authoritative.
    internal sealed class NaturalPikeCountermeasures : MonoBehaviour
    {
        internal const string WeaponKey = "rsl_ashm";
        internal const string EmitterPrefix = "rsl_pike_flare_emitter_";
        private static readonly Dictionary<Missile, NaturalPikeCountermeasures> Live =
            new Dictionary<Missile, NaturalPikeCountermeasures>();

        public Transform[] Emitters;
        public GameObject FlarePrefab;
        public AudioClip EjectionSound;
        public float EjectionVelocity, EjectionVelocityVariance, EjectionVolume, NativeEjectionInterval;
        public float TerminalRange, TerminalSpeed, SeparationTime, NativeJammingIntensity;
        public string FlareDonorKey, JammerDonorKey, InterfaceSha256;

        private Missile missile;
        private readonly PikeCountermeasureSchedule schedule = new PikeCountermeasureSchedule();
        private AudioSource[] ejectionSources;
        private readonly PikeBurstReceipt receipt = new PikeBurstReceipt();
        private bool terminalObserved;
        private NaturalWeaponPhase phase;
        private NaturalPikeOffensiveEcm offensiveEcm;
        private readonly List<Missile> threats = new List<Missile>(16);
        private float nextDecision;
        private double closingSeconds = double.PositiveInfinity;
        private double lastRangeObservation = double.NaN;
        private bool dangerousThreat;
        private int registeredThreats, discardedThreats;

        internal int FlaresEmitted, BurstsEmitted, ReceivedBurstCount, DuplicateBurstsIgnored;
        internal bool NativeFlareParticlesSeen;
        internal float CurrentTargetRange, TerminalEntryTime, TerminalEntryRange;
        internal int NativeRadarReturnQueries, NativeRadarQueriesWithEcmInput, ReducedNativeRadarReturnQueries;
        // Only the isolated live observer enables the extra reference query.
        internal bool DiagnosticRadarObservation;
        internal float LastUnjammedReturn, LastNativeReturn, LastAppliedJammingIntensity;
        internal readonly List<float> BurstTimes = new List<float>();
        internal readonly List<float> BurstRanges = new List<float>();
        internal readonly List<int[]> BurstFlareInstanceIds = new List<int[]>();
        internal bool SequenceStarted => terminalObserved;
        internal double ExpectedTerminalSeconds => schedule.ExpectedTerminalSeconds;
        internal double NominalBurstInterval => schedule.NominalIntervalSeconds;
        internal double LastBurstLeadSeconds => schedule.LeadSeconds;
        internal bool CompressedShortEntry => schedule.CompressedShortEntry;
        internal int PairsEmitted => BurstsEmitted;
        internal int FlaresRemaining => Mathf.Max(0, PikeCountermeasureSchedule.FlareCapacity - FlaresEmitted);

#pragma warning disable 0649
        private sealed class InterfaceFile
        {
            public int schemaVersion;
            public string coordinateSpace;
            public Dictionary<string, WeaponInterfaces> weapons;
        }
        private sealed class WeaponInterfaces { public StageInterfaces flight; }
        private sealed class StageInterfaces { public EmitterPose[] flareEmitters; }
        private sealed class EmitterPose { public string name; public float[] position, rotation; }
#pragma warning restore 0649

        internal static void Configure(GameObject prefab, NaturalWeapons.SourceWeapon source, Encyclopedia encyclopedia)
        {
            if (source.key != WeaponKey) throw new ArgumentException("Pike countermeasures require the Pike source definition.");
            if (source.Text("OnBoardSystems", "ECM") != "rsl_missile_ecm")
                throw new InvalidDataException("Pike's source defensive ECM declaration is missing.");
            if (prefab.GetComponent<NaturalPikeCountermeasures>() != null)
                throw new InvalidOperationException("Pike countermeasures were configured twice.");
            NaturalWeaponPhase phase = prefab.GetComponent<NaturalWeaponPhase>();
            if (phase == null || phase.FlightModel == null)
                throw new InvalidOperationException("Pike's flight model must exist before its countermeasure interfaces.");

            var aircraft = encyclopedia.aircraft.Where(d => d != null && d.unitPrefab != null &&
                d.unitPrefab.GetComponent<Aircraft>() != null && !d.jsonKey.StartsWith("rsl_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.jsonKey == "Fighter1" ? 0 : 1).ThenBy(d => d.jsonKey, StringComparer.Ordinal).ToArray();
            var flareSource = aircraft.Select(d => new { definition = d, ejector = d.unitPrefab.GetComponentInChildren<FlareEjector>(true) })
                .FirstOrDefault(x => x.ejector != null);
            var jammerSource = aircraft.Select(d => new { definition = d, jammer = d.unitPrefab.GetComponentInChildren<RadarJammer>(true) })
                .FirstOrDefault(x => x.jammer != null && x.jammer.GetMaxJammingIntensity() > 0f);
            if (flareSource == null || jammerSource == null)
                throw new InvalidOperationException("Pike requires installed native flare and defensive jammer references.");
            GameObject flarePrefab = (GameObject)NaturalWeapons.Get(flareSource.ejector, "flarePrefab");
            if (flarePrefab == null || flarePrefab.GetComponent<IRFlare>() == null)
                throw new InvalidOperationException("The native flare reference has no IRFlare simulation.");

            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets", "Weapons", "missile_interfaces.json");
            byte[] bytes = File.ReadAllBytes(path);
            InterfaceFile interfaces = JsonConvert.DeserializeObject<InterfaceFile>(System.Text.Encoding.UTF8.GetString(bytes));
            WeaponInterfaces weapon;
            if (interfaces == null || interfaces.schemaVersion != 1 || interfaces.coordinateSpace != "stage-local-unity-metres" ||
                interfaces.weapons == null || !interfaces.weapons.TryGetValue(WeaponKey, out weapon) ||
                weapon == null || weapon.flight == null || weapon.flight.flareEmitters == null ||
                weapon.flight.flareEmitters.Length != PikeCountermeasureSchedule.EmitterCount)
                throw new InvalidDataException("Pike needs exactly eight authored flight-stage flare emitter poses.");
            EmitterPose[] poses = weapon.flight.flareEmitters.OrderBy(e => e == null ? "" : e.name, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < poses.Length; i++)
            {
                EmitterPose pose = poses[i];
                if (pose == null || pose.name != EmitterPrefix + (i + 1).ToString("00") ||
                    pose.position == null || pose.position.Length != 3 || pose.rotation == null || pose.rotation.Length != 4 ||
                    pose.position.Concat(pose.rotation).Any(v => !Finite(v)))
                    throw new InvalidDataException("Invalid or duplicate Pike emitter pose at index " + i);
                float squareLength = pose.rotation.Sum(v => v * v);
                if (Mathf.Abs(squareLength - 1f) > .001f)
                    throw new InvalidDataException("Pike emitter rotations must be normalized XYZW quaternions.");
                NaturalWeapons.SourceStage stage = source.stages["flight"];
                for (int axis = 0; axis < 3; axis++)
                    if (pose.position[axis] < stage.min[axis] - .05f || pose.position[axis] > stage.max[axis] + .05f)
                        throw new InvalidDataException("Pike emitter lies outside the authored flight-model bounds.");
            }

            var countermeasures = prefab.AddComponent<NaturalPikeCountermeasures>();
            countermeasures.Emitters = new Transform[poses.Length];
            for (int i = 0; i < poses.Length; i++)
            {
                EmitterPose pose = poses[i];
                Transform emitter = new GameObject(pose.name).transform;
                emitter.SetParent(phase.FlightModel.transform, false);
                emitter.localPosition = new Vector3(pose.position[0], pose.position[1], pose.position[2]);
                // Keep the saved physical ports, but correct their narrow
                // almost-vertical discharge to opposed 45-degree upward jets.
                emitter.localRotation = Quaternion.LookRotation(new Vector3(i < 4 ? -1f : 1f, 1f, 0f).normalized);
                emitter.localScale = Vector3.one;
                countermeasures.Emitters[i] = emitter;
            }
            countermeasures.FlarePrefab = flarePrefab;
            countermeasures.FlareDonorKey = flareSource.definition.jsonKey;
            countermeasures.JammerDonorKey = jammerSource.definition.jsonKey;
            countermeasures.EjectionSound = (AudioClip)NaturalWeapons.Get(flareSource.ejector, "ejectionSound");
            countermeasures.EjectionVelocity = 30f;
            countermeasures.EjectionVelocityVariance = Mathf.Clamp((float)NaturalWeapons.Get(flareSource.ejector, "ejectionVelocityVariance"), 0f, .15f);
            countermeasures.EjectionVolume = (float)NaturalWeapons.Get(flareSource.ejector, "ejectionVolume");
            countermeasures.NativeEjectionInterval = (float)NaturalWeapons.Get(flareSource.ejector, "ejectionInterval");
            // Source JamChance=.70 and C/X/Ku bands have no one-to-one native
            // equivalent. This is the ordinary donor's full-power game scale,
            // never a converted probability or an invented physical value.
            countermeasures.NativeJammingIntensity = jammerSource.jammer.GetMaxJammingIntensity();
            countermeasures.TerminalRange = phase.TerminalRange;
            countermeasures.TerminalSpeed = phase.TerminalSpeed;
            countermeasures.SeparationTime = phase.SwitchSeconds;
            using (SHA256 sha = SHA256.Create())
                countermeasures.InterfaceSha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (!Finite(countermeasures.NativeJammingIntensity) || countermeasures.NativeJammingIntensity <= 0f ||
                !Finite(countermeasures.TerminalRange) || countermeasures.TerminalRange <= 0f ||
                !Finite(countermeasures.TerminalSpeed) || countermeasures.TerminalSpeed <= 0f ||
                !Finite(countermeasures.NativeEjectionInterval) || countermeasures.NativeEjectionInterval < 0f)
                throw new InvalidDataException("Invalid native/source Pike countermeasure settings.");
            PikeCountermeasureNetworking.Initialize();
            NaturalPikeOffensiveEcm.Configure(prefab);
        }

        private void Awake()
        {
            missile = GetComponent<Missile>();
            phase = GetComponent<NaturalWeaponPhase>();
            offensiveEcm = GetComponent<NaturalPikeOffensiveEcm>();
            if (missile == null || missile.definition == null || missile.definition.jsonKey != WeaponKey ||
                Emitters == null || Emitters.Length != PikeCountermeasureSchedule.EmitterCount || Emitters.Any(t => t == null))
                throw new InvalidOperationException("Pike's cloned countermeasure interfaces are incomplete.");
            ejectionSources = new AudioSource[Emitters.Length];
            Live.Add(missile, this);
        }

        private void OnDestroy() { if (!ReferenceEquals(missile, null)) Live.Remove(missile); }

        internal void ObserveTargetRange(float range)
        {
            if (missile == null || !missile.LocalSim || missile.disabled || !Finite(range) || range < 0f) return;
            double now = Time.timeSinceLevelLoad, elapsed = now - lastRangeObservation;
            if (!double.IsNaN(elapsed) && elapsed > .00001 && elapsed <= 1.5)
                closingSeconds = PikeCountermeasureSchedule.ClosingSeconds(range, (CurrentTargetRange - range) / elapsed);
            else if (double.IsNaN(elapsed) || elapsed > 1.5) closingSeconds = double.PositiveInfinity;
            CurrentTargetRange = range;
            lastRangeObservation = now;
        }

        // Aryx 1.0.6's event-fed threat pattern, scoped to local Pike missiles.
        // No global missile searches and no per-frame list allocation.
        internal void RegisterThreat(Missile incoming)
        {
            if (missile == null || !missile.LocalSim || missile.disabled || incoming == null || incoming == missile ||
                incoming.disabled || incoming.targetID != missile.persistentID || threats.Contains(incoming)) return;
            if (offensiveEcm != null) offensiveEcm.ObserveThreat(incoming);
            MissileSeeker seeker = incoming.GetComponent<MissileSeeker>();
            if (seeker == null || !string.Equals(seeker.GetSeekerType(), "IR", StringComparison.OrdinalIgnoreCase)) return;
            if (threats.Count >= 32) { discardedThreats++; return; }
            threats.Add(incoming); registeredThreats++;
        }

        private void LateUpdate()
        {
            if (missile == null || !missile.LocalSim || missile.disabled || missile.rb == null ||
                Time.timeSinceLevelLoad < nextDecision) return;
            nextDecision = Time.timeSinceLevelLoad + .05f;
            bool separated = missile.timeSinceSpawn >= SeparationTime && missile.EngineOn();
            // The phase owner supplies assignment/release. Range observations
            // arrive from the native aim before local waypoints; do not read an
            // unobserved target transform to manufacture a new terminal gate.
            if (!terminalObserved && separated && CurrentTargetRange > 0f && phase != null && phase.TerminalBoostActive)
            {
                terminalObserved = true;
                TerminalEntryTime = missile.timeSinceSpawn;
                TerminalEntryRange = CurrentTargetRange;
            }
            dangerousThreat = HasDangerousThreat();
            int first = schedule.IssuedBursts;
            if (schedule.Advance(Time.timeSinceLevelLoad, CurrentTargetRange, closingSeconds, separated,
                dangerousThreat, phase != null && phase.TerminalBoostActive) != 0)
                EmitAuthoritativeBurst((byte)first);
        }

        private bool HasDangerousThreat()
        {
            bool danger = false;
            for (int i = threats.Count - 1; i >= 0; i--)
            {
                Missile incoming = threats[i];
                if (incoming == null || incoming.disabled || incoming.targetID != missile.persistentID)
                { threats.RemoveAt(i); continue; }
                Vector3 offset = incoming.GlobalPosition() - missile.GlobalPosition();
                float range = offset.magnitude;
                if (range <= 2000f) danger = true;
                else if (incoming.rb != null)
                {
                    float closing = -Vector3.Dot(incoming.rb.velocity - missile.rb.velocity, offset.normalized);
                    if (PikeCountermeasureSchedule.ClosingSeconds(range, closing) <= 4.0) danger = true;
                }
            }
            return danger;
        }

        private void EmitAuthoritativeBurst(byte index)
        {
            var message = new PikeFlarePairMessage {
                MissileId = missile.persistentID.Id, BurstIndex = index, TargetRange = CurrentTargetRange,
                Positions = new Vector3[PikeCountermeasureSchedule.FlaresPerBurst],
                Velocities = new Vector3[PikeCountermeasureSchedule.FlaresPerBurst]
            };
            int outlet = index % 4;
            for (int i = 0; i < PikeCountermeasureSchedule.FlaresPerBurst; i++)
            {
                Transform emitter = Emitters[outlet + i * 4];
                message.Positions[i] = emitter.GlobalPosition().AsVector3();
                message.Velocities[i] = missile.rb.velocity + UpwardEjectionDirection(missile.transform.forward, i) *
                    (EjectionVelocity + UnityEngine.Random.Range(-1f, 1f) * EjectionVelocityVariance * EjectionVelocity);
            }
            SpawnBurst(message);
            PikeCountermeasureNetworking.Send(missile, message);
        }

        internal static Vector3 UpwardEjectionDirection(Vector3 forward, int side)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (horizontal.sqrMagnitude < .0001f) horizontal = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, horizontal.normalized);
            // Keep both authored ports and inherited missile velocity. The
            // discharge fan stays above the horizon even at a 50-degree bank.
            return (Vector3.up + right * (side == 0 ? -1f : 1f)).normalized;
        }

        internal void ReceiveBurst(PikeFlarePairMessage message)
        {
            if (missile == null || missile.LocalSim || missile.disabled || !PikeCountermeasureNetworking.ValidMessage(message)) return;
            if (!receipt.TryAccept(message.BurstIndex)) { DuplicateBurstsIgnored++; return; }
            ReceivedBurstCount++;
            CurrentTargetRange = message.TargetRange;
            SpawnBurst(message);
        }

        private void SpawnBurst(PikeFlarePairMessage message)
        {
            var flareIds = new int[PikeCountermeasureSchedule.FlaresPerBurst];
            for (int i = 0; i < PikeCountermeasureSchedule.FlaresPerBurst; i++)
            {
                flareIds[i] = SpawnNativeFlare(new GlobalPosition(message.Positions[i]).ToLocalPosition(), message.Velocities[i]);
                PlayEjection(message.BurstIndex % 4 + i * 4);
            }
            BurstsEmitted++;
            BurstTimes.Add(missile.timeSinceSpawn);
            BurstRanges.Add(message.TargetRange);
            BurstFlareInstanceIds.Add(flareIds);
        }

        private int SpawnNativeFlare(Vector3 position, Vector3 velocity)
        {
            GameObject flareObject = NetworkSceneSingleton<Spawner>.i.SpawnLocal(FlarePrefab, Datum.origin);
            IRFlare flare = flareObject.GetComponent<IRFlare>();
            flareObject.transform.position = position - velocity * Time.deltaTime;
            var source = new IRSource(flareObject.transform, 1f, true);
            NaturalWeapons.Set(flare, "IR", source);
            NaturalWeapons.Set(flare, "velocity", velocity);
            missile.AddIRSource(source);
            NaturalFlareOwner ownership = flareObject.AddComponent<NaturalFlareOwner>();
            ownership.Owner = missile;
            ownership.Source = source;
            // These are the same initial smoke emission fields used by native
            // IRFlare.LaunchFlare. Its Update owns subsequent burn/drag/visuals.
            ParticleSystem smoke = (ParticleSystem)NaturalWeapons.Get(flare, "smokeParticles");
            ParticleSystem.MainModule main = smoke.main;
            var emit = new ParticleSystem.EmitParams {
                position = flareObject.transform.GlobalPosition().AsVector3(),
                velocity = velocity + main.startSpeed.constant * velocity.magnitude * .01f * new Vector3(
                    UnityEngine.Random.Range(-1, 1), UnityEngine.Random.Range(-1, 1), UnityEngine.Random.Range(-1, 1))
            };
            smoke.Emit(emit, 1);
            FlaresEmitted++;
            NativeFlareParticlesSeen |= flareObject.GetComponentsInChildren<ParticleSystem>().Any(p => p.isPlaying);
            return flareObject.GetInstanceID();
        }

        private void PlayEjection(int index)
        {
            if (EjectionSound == null) return;
            AudioSource audio = ejectionSources[index];
            if (audio == null)
            {
                audio = ejectionSources[index] = Emitters[index].gameObject.AddComponent<AudioSource>();
                audio.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
                audio.clip = EjectionSound;
                audio.volume = EjectionVolume;
                audio.spatialBlend = 1f;
                audio.dopplerLevel = 0f;
                audio.spread = 5f;
                audio.maxDistance = 40f;
                audio.minDistance = 5f;
            }
            audio.pitch = UnityEngine.Random.Range(.8f, 1.2f);
            audio.PlayOneShot(EjectionSound);
        }

        internal static NaturalPikeCountermeasures Find(Missile target)
        {
            NaturalPikeCountermeasures value;
            return target != null && Live.TryGetValue(target, out value) ? value : null;
        }

        internal object Capture() => new {
            localAuthority = missile != null && missile.LocalSim,
            pairsEmitted = PairsEmitted, flaresEmitted = FlaresEmitted, flaresRemaining = FlaresRemaining,
            earlyPairs = schedule.EarlyPairs, reservedPairs = schedule.TerminalPairs,
            earlyCycles = schedule.EarlyCycles, terminalCycles = schedule.TerminalCycles,
            reservedFlaresRemaining = missile != null && missile.LocalSim ? (int?)schedule.ReservedFlaresRemaining : null,
            terminalReserveReleased = schedule.ReserveReleased,
            targetRangeM = CurrentTargetRange,
            radialClosingSeconds = double.IsInfinity(closingSeconds) ? (double?)null : closingSeconds,
            dangerousIrThreat = dangerousThreat, trackedIrThreats = threats.Count,
            registeredIrThreats = registeredThreats, droppedThreatsAtCapacity = discardedThreats,
            pairIntervalSeconds = PikeCountermeasureSchedule.PairIntervalSeconds,
            cycleIntervalSeconds = PikeCountermeasureSchedule.CycleIntervalSeconds,
            pairsPerCycle = PikeCountermeasureSchedule.PairsPerCycle,
            totalFlareCapacity = PikeCountermeasureSchedule.FlareCapacity,
            ejectionSpeedMps = EjectionVelocity, outwardAngleFromUpDegrees = 45f,
            lastPairGameSeconds = double.IsInfinity(schedule.LastPairTime) ? (double?)null : schedule.LastPairTime,
            scope = "Two opposed pairs at 0/.4 s per 2 s terminal cycle; finite early and terminal budgets. Closing time is diagnostic from native aim-range history."
        };

        internal static float NativeIntensity(Missile target)
        {
            NaturalPikeCountermeasures value = Find(target);
            if (value == null || !value.isActiveAndEnabled) return 0f;
            // Offensive ECM uses native Unit.Jam. Do not also subtract the old
            // full aircraft defensive intensity from this missile's return.
            if (value.offensiveEcm != null) return 0f;
            float range = value.CurrentTargetRange;
            bool remoteRangeKnown = false;
            Unit destination;
            if (!target.LocalSim && UnitRegistry.TryGetUnit(target.targetID, out destination) && destination != null)
            {
                range = (destination.GlobalPosition() - target.GlobalPosition()).magnitude;
                remoteRangeKnown = true;
            }
            return PikeEcmGate.IsActive(target.LocalSim, value.terminalObserved, remoteRangeKnown,
                target.timeSinceSpawn >= value.SeparationTime, target.EngineOn(), target.disabled, range, value.TerminalRange)
                ? value.NativeJammingIntensity : 0f;
        }

        internal void ObserveRadarReturn(Vector3 source, float distance, float clutter, RadarParams parameters, float actual)
        {
            NativeRadarReturnQueries++;
            LastAppliedJammingIntensity = NativeIntensity(missile);
            if (LastAppliedJammingIntensity > 0f) NativeRadarQueriesWithEcmInput++;
            LastUnjammedReturn = parameters.GetSignalStrength(FastMath.NormalizedDirection(source, missile.transform.position),
                distance, missile.rb, missile.RCS, clutter, 0f);
            LastNativeReturn = actual;
            if (LastAppliedJammingIntensity > 0f && LastNativeReturn < LastUnjammedReturn - .0001f)
                ReducedNativeRadarReturnQueries++;
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    [HarmonyPatch(typeof(Missile), "TargetIDChanged")]
    internal static class PikeIncomingIrThreatPatch
    {
        private static void Postfix(Missile __instance, PersistentID newValue)
        {
            if (__instance == null || newValue.NotValid) return;
            Unit target;
            if (newValue.TryGetUnit(out target) && target is Missile protectedMissile)
                NaturalPikeCountermeasures.Find(protectedMissile)?.RegisterThreat(__instance);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.GetRadarReturn))]
    internal static class PikeNativeRadarReturnPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            MethodInfo signal = AccessTools.Method(typeof(RadarParams), nameof(RadarParams.GetSignalStrength));
            MethodInfo intensity = AccessTools.Method(typeof(NaturalPikeCountermeasures), nameof(NaturalPikeCountermeasures.NativeIntensity));
            int replacements = 0;
            for (int i = 1; i < codes.Count; i++)
            {
                if (!codes[i].Calls(signal)) continue;
                if (codes[i - 1].opcode != OpCodes.Ldc_R4 || !(codes[i - 1].operand is float) || (float)codes[i - 1].operand != 0f)
                    throw new InvalidOperationException("The native missile radar ECM argument has changed; Pike's adapter must be reviewed.");
                // Replace only the native method's constant zero ECM argument.
                // Its radar warning event and native signal calculation stay.
                codes[i - 1].opcode = OpCodes.Ldarg_0;
                codes[i - 1].operand = null;
                codes.Insert(i, new CodeInstruction(OpCodes.Call, intensity));
                i++;
                replacements++;
            }
            if (replacements != 1) throw new InvalidOperationException("Expected exactly one native missile radar signal calculation.");
            return codes;
        }

        private static void Postfix(Missile __instance, Vector3 source, float dist, float clutter, RadarParams radarParameters, float __result)
        {
            NaturalPikeCountermeasures value = NaturalPikeCountermeasures.Find(__instance);
            if (value != null && value.DiagnosticRadarObservation)
                value.ObserveRadarReturn(source, dist, clutter, radarParameters, __result);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.GetECMIntensity))]
    internal static class PikeNativeECMIntensityPatch
    {
        private static void Postfix(Missile __instance, ref float __result)
        {
            __result += NaturalPikeCountermeasures.NativeIntensity(__instance);
        }
    }
}
