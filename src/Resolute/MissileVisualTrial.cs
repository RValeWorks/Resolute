using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit diagnostic: intact art is copied into an inactive visual-only
    // hierarchy before activation; no native missile logic is run here.
    internal static class MissileVisualTrial
    {
        internal static IEnumerator Run(Ship owner, Dictionary<string, object> report, string output)
        {
            var rows = new List<object>();
            report["missileVisualReference"] = rows;
            foreach (MissileDefinition definition in Encyclopedia.i.missiles.Where(d => d != null && d.unitPrefab != null &&
                (new[] { "AAM1", "AAM2", "AShM1" }.Contains(d.jsonKey) || NaturalWeapons.Definitions.ContainsKey(d.jsonKey))))
            {
                var holder = new GameObject("Resolute.MissileVisualReference"); holder.SetActive(false);
                GameObject copy = CopyArt(definition.unitPrefab.transform, holder.transform);
                copy.transform.SetPositionAndRotation(owner.transform.position + Vector3.up * 200f, Quaternion.identity);
                NaturalWeaponPhase phase = definition.unitPrefab.GetComponent<NaturalWeaponPhase>();
                GameObject launch = phase != null ? copy.transform.Find("OriginalWeaponGeometry/" + phase.LaunchModel.name).gameObject : null;
                GameObject flight = phase != null ? copy.transform.Find("OriginalWeaponGeometry/" + phase.FlightModel.name).gameObject : null;
                foreach (LODGroup group in copy.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
                holder.SetActive(true); copy.SetActive(true);
                yield return null;
                foreach (string stage in phase != null ? new[] { "launch", "flight" } : new[] { "native" })
                {
                    if (launch != null) launch.SetActive(stage == "launch");
                    if (flight != null) flight.SetActive(stage == "flight");
                    Renderer[] renderers = copy.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && r.GetComponent<MeshFilter>()?.sharedMesh != null).Cast<Renderer>().ToArray();
                    if (renderers.Length == 0) continue;
                    Bounds bounds = renderers[0].bounds;
                    foreach (Renderer r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                    float length = Mathf.Max(bounds.size.z, definition.length);
                    var row = new Dictionary<string, object> { ["key"] = definition.jsonKey, ["stage"] = stage,
                        ["scope"] = "Unlit effects disabled for an intact-art comparison. Actual installed native or custom prefab geometry rendered with the same original game camera and environment.",
                        ["dimensions"] = new[] { bounds.size.x, bounds.size.y, bounds.size.z },
                        ["side"] = NaturalWeaponAudit.CaptureFrame(copy.transform, length * .82f, output, "missile_art_" + definition.jsonKey + "_" + stage + "_side", 0, bounds.center),
                        ["above"] = NaturalWeaponAudit.CaptureFrame(copy.transform, length * .82f, output, "missile_art_" + definition.jsonKey + "_" + stage + "_above", 1, bounds.center),
                        ["rear"] = NaturalWeaponAudit.CaptureFrame(copy.transform, length * .45f, output, "missile_art_" + definition.jsonKey + "_" + stage + "_rear", 2, bounds.center),
                        ["materials"] = renderers.SelectMany(r => r.sharedMaterials).Distinct().Select(m => D(
                            "name", m.name, "shader", m.shader.name, "keywords", m.shaderKeywords,
                            "baseMap", m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap")?.name : null,
                            "color", m.HasProperty("_BaseColor") ? (object)m.GetColor("_BaseColor").ToString() : null,
                            "metallic", m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : -1,
                            "smoothness", m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : -1
                        )).ToArray(),
                        ["motors"] = MotorState(definition.unitPrefab.GetComponent<Missile>()) };
                    rows.Add(row);
                    yield return null;
                }
                Object.Destroy(holder);
                yield return null;
            }
        }

        private static GameObject CopyArt(Transform source, Transform parent)
        {
            var result = new GameObject(source.name); result.transform.SetParent(parent, false);
            result.transform.localPosition = source.localPosition; result.transform.localRotation = source.localRotation;
            result.transform.localScale = source.localScale; result.layer = source.gameObject.layer;
            MeshFilter filter = source.GetComponent<MeshFilter>(); MeshRenderer renderer = source.GetComponent<MeshRenderer>();
            if (filter != null && filter.sharedMesh != null && renderer != null && renderer.enabled)
            {
                result.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var clone = result.AddComponent<MeshRenderer>(); clone.sharedMaterials = renderer.sharedMaterials;
                clone.shadowCastingMode = renderer.shadowCastingMode; clone.receiveShadows = renderer.receiveShadows;
            }
            foreach (Transform child in source) CopyArt(child, result.transform);
            result.SetActive(source.gameObject.activeSelf); return result;
        }

        internal sealed class MotorTargetFixture
        {
            internal Unit Target;
            internal Action Maintain, Retire;
            internal object Evidence;
        }

        internal static IEnumerator RunLauncherBurns(Ship owner, Func<string, WeaponStation, MotorTargetFixture, IEnumerator> prepareTarget,
            HashSet<string> requested, Dictionary<string, object> report, List<object> checks, string output)
        {
            var rows = new List<object>();
            report["nativeLauncherMotorReview"] = D("scope",
                "Explicit native WeaponStation.Fire commands from the actual Resolute launchers with normal ammunition, ejection, target arguments and missile physics. Every shot has a fresh warmed native target and independent cleanup. Target/pilot health, liveness and part detachments are observed before, during and after the motor window. Initial target placement, pilot controls, ballistic course and HQ tracks are diagnostic inputs; aircraft motion remains native after spawn, and health, collisions and actual hits remain active. No effect activation, particle scaling or motor timing is forced by this fixture.", "shots", rows);
            foreach (string key in NaturalWeapons.Definitions.Keys.Where(requested.Contains))
            {
                WeaponStation station = owner.weaponStations.FirstOrDefault(s => s.WeaponInfo == NaturalWeapons.Infos[key] && s.GetAmmoLoaded() > 0);
                if (station == null) { rows.Add(D("key", key, "spawned", false, "reason", "No loaded owned station")); Check(checks, key + "-native-launcher-spawned-and-spent-ammo", false); continue; }
                float readyAt = Time.time;
                foreach (MissileLauncher launcher in station.Weapons.OfType<MissileLauncher>())
                    readyAt = Mathf.Max(readyAt, Time.time + (float)NaturalWeapons.Get(launcher, "fireInterval") -
                        (Time.timeSinceLevelLoad - (float)NaturalWeapons.Get(launcher, "lastFired")) + .1f);
                while (Time.time < readyAt) yield return null;
                var fixture = new MotorTargetFixture();
                try
                {
                IEnumerator prepare = prepareTarget(key, station, fixture);
                while (prepare.MoveNext()) yield return prepare.Current;
                Unit target = fixture.Target;
                var row = D("key", key, "spawned", false, "station", station.Number,
                    "target", target != null && target.definition != null ? target.definition.jsonKey : null,
                    "targetId", target != null ? target.persistentID.ToString() : null,
                    "freshTargetPreparation", fixture.Evidence, "targetBeforeLaunch", TargetState(target),
                    "ownerBeforeLaunch", TargetState(owner), "moments", new List<object>());
                rows.Add(row);
                System.IO.File.WriteAllText(output, Audit.Json(report));
                if (target == null || target.disabled) throw new InvalidOperationException("Native motor fixture target unavailable: " + key);
                NaturalWeaponsTrial.Know(owner.NetworkHQ, target);
                MissileLauncher launch = station.Weapons.OfType<MissileLauncher>().FirstOrDefault(l => l.ammo > 0);
                string eligibilityReason;
                bool eligible = NaturalMissileTargeting.CanLaunch(launch, station, owner, target, out eligibilityReason);
                Check(checks, key + "-native-motor-fixture-eligible", eligible);
                TrackingInfo targetTrack = owner.NetworkHQ.GetTrackingData(target.persistentID);
                int attacksBefore = targetTrack != null ? targetTrack.missileAttacks : -1;
                var before = new HashSet<Missile>(UnitRegistry.allUnits.OfType<Missile>());
                int ammo = station.GetAmmoLoaded(); station.Fire(owner, target);
                yield return null;
                Missile missile = UnitRegistry.allUnits.OfType<Missile>().FirstOrDefault(m => !before.Contains(m) &&
                    m.ownerID == owner.persistentID && m.definition.jsonKey == key);
                row["spawned"] = missile != null; row["ammoBefore"] = ammo; row["ammoAfter"] = station.GetAmmoLoaded();
                row["fixtureEligibility"] = eligibilityReason;
                row["fixtureRangeMetres"] = (target.GlobalPosition() - owner.GlobalPosition()).magnitude;
                row["fixtureRadarAltitudeMetres"] = target.radarAlt;
                row["fixtureHasNativeIRList"] = target.HasIRSignature();
                row["nativeMissileAttacksBefore"] = attacksBefore;
                Check(checks, key + "-native-launcher-spawned-and-spent-ammo", missile != null && station.GetAmmoLoaded() == ammo - 1);
                if (missile == null) { row["targetAfterObservation"] = TargetState(target); continue; }
                object motor = ((Array)NaturalWeapons.Get(missile, "motors")).GetValue(0);
                var phase = missile.GetComponent<NaturalWeaponPhase>();
                var shot = new LaunchShot { Missile = missile, Row = row, Cutoff = (float)NaturalWeapons.Get(motor, "burnTime") + .05f,
                    Separation = phase != null ? phase.SwitchSeconds : 0f };
                row["configuredFirstBurnSeconds"] = NaturalWeapons.Get(motor, "burnTime");
                row["sourceModelSwitchSeconds"] = shot.Separation;
                float until = Time.time + Mathf.Max(shot.Cutoff, shot.Separation) + .8f;
                // Observe each live launch immediately. Waiting until every
                // station fired could label a late frame as its ignition.
                while (Time.time < until && missile != null && !missile.disabled)
                {
                    fixture.Maintain?.Invoke();
                    float time = missile.timeSinceSpawn;
                    foreach (var moment in new[] {
                        new KeyValuePair<string, float>("ignition", .12f), new KeyValuePair<string, float>("first_burn", .8f),
                        new KeyValuePair<string, float>("before_separation", Mathf.Max(.15f, shot.Separation - .2f)),
                        new KeyValuePair<string, float>("after_separation", shot.Separation + .2f),
                        new KeyValuePair<string, float>("before_first_motor_cutoff", shot.Cutoff - .2f),
                        new KeyValuePair<string, float>("after_first_motor_cutoff", shot.Cutoff + .2f) })
                    {
                        if (time < moment.Value || !shot.Captured.Add(moment.Key)) continue;
                        string name = "native_launch_" + missile.definition.jsonKey + "_" + moment.Key;
                        NaturalWeaponEffects effects = missile.GetComponent<NaturalWeaponEffects>();
                        ParticleSystem[] flames = ((ParticleSystem[])NaturalWeapons.Get(motor, "particleSystems"))
                            .Where(p => p.name.IndexOf("fire", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                        bool fireVisible = flames.Length > 0 && flames.All(p => p.isEmitting && p.particleCount > 0 &&
                            p.gameObject.activeInHierarchy && p.GetComponent<ParticleSystemRenderer>().enabled);
                        float expectedTail = time < shot.Separation ? effects.LaunchTail : effects.FlightTail;
                        Vector3 localNozzle = missile.transform.InverseTransformPoint(effects.Nozzle.position);
                        bool positioned = Vector3.Distance(localNozzle, new Vector3(0, 0, expectedTail)) < .008f;
                        shot.Positioned &= positioned;
                        shot.Timely &= time < moment.Value + .18f;
                        if (moment.Key == "first_burn" || moment.Key == "before_first_motor_cutoff")
                            shot.Burning &= fireVisible && missile.EngineOn();
                        if (moment.Key == "first_burn")
                        {
                            Light[] lights = (Light[])NaturalWeapons.Get(motor, "lights");
                            AudioSource[] audio = (AudioSource[])NaturalWeapons.Get(motor, "audioSources");
                            TrailEmitter[] trails = (TrailEmitter[])NaturalWeapons.Get(motor, "trailEmitters");
                            shot.LaunchSupport = lights.Length > 0 && lights.All(l => l.enabled && l.gameObject.activeInHierarchy) &&
                                audio.Length > 0 && audio.Any(a => a.enabled && a.isPlaying) &&
                                trails.Length > 0 && trails.All(t => t.enabled && t.gameObject.activeInHierarchy);
                        }
                        if (moment.Key == "after_first_motor_cutoff")
                        {
                            var current = ((Array)NaturalWeapons.Get(missile, "motors")).Cast<object>().Skip(1).ToArray();
                            var retainedParticles = new HashSet<ParticleSystem>(current.SelectMany(m => (ParticleSystem[])NaturalWeapons.Get(m, "particleSystems")));
                            var retainedLights = new HashSet<Light>(current.SelectMany(m => (Light[])NaturalWeapons.Get(m, "lights")));
                            var retainedAudio = new HashSet<AudioSource>(current.SelectMany(m => (AudioSource[])NaturalWeapons.Get(m, "audioSources")));
                            var retainedTrails = new HashSet<TrailEmitter>(current.SelectMany(m => (TrailEmitter[])NaturalWeapons.Get(m, "trailEmitters")));
                            shot.CutoffObserved = ((ParticleSystem[])NaturalWeapons.Get(motor, "particleSystems")).Where(p => !retainedParticles.Contains(p)).All(p => !p.isEmitting) &&
                                ((Light[])NaturalWeapons.Get(motor, "lights")).Where(l => !retainedLights.Contains(l)).All(l => !l.enabled) &&
                                ((AudioSource[])NaturalWeapons.Get(motor, "audioSources")).Where(a => !retainedAudio.Contains(a)).All(a => !a.isPlaying) &&
                                ((TrailEmitter[])NaturalWeapons.Get(motor, "trailEmitters")).Where(t => !retainedTrails.Contains(t)).All(t => !t.enabled);
                        }
                        if (moment.Key == "before_separation") shot.BeforeModel = phase.LaunchModel.activeInHierarchy;
                        if (moment.Key == "after_separation") shot.AfterModel = !phase.LaunchModel.activeInHierarchy && phase.FlightModel.activeInHierarchy;
                        ((List<object>)shot.Row["moments"]).Add(D("moment", moment.Key, "time", time,
                            "targetState", TargetState(target),
                            "requestedTime", moment.Value, "firstMotorFlameEmitting", fireVisible,
                            "expectedNozzleTail", expectedTail, "nozzlePosition", V(localNozzle), "emitterPositionCorrect", positioned,
                            "engineOn", missile.EngineOn(), "stage", NaturalWeapons.Get(missile, "motorStage"), "motors", MotorState(missile),
                            "launchModelVisible", missile.GetComponent<NaturalWeaponPhase>()?.LaunchModel.activeInHierarchy,
                            "side", NaturalWeaponAudit.CaptureFlight(missile, output, name + "_side"),
                            "rear", NaturalWeaponAudit.CaptureFlight(missile, output, name + "_rear", 2)));
                    }
                    yield return null;
                }
                row["targetAfterObservation"] = TargetState(target);
                row["ownerAfterObservation"] = TargetState(owner);
                row["missileAfterObservation"] = TargetState(shot.Missile);
                shot.Row["capturedMoments"] = shot.Captured.ToArray();
                Check(checks, key + "-native-launcher-six-timely-motor-moments", shot.Captured.Count == 6 && shot.Timely);
                Check(checks, key + "-native-first-motor-flame-through-cutoff", shot.Burning && shot.Captured.Contains("before_first_motor_cutoff"));
                Check(checks, key + "-native-first-motor-light-audio-smoke-active", shot.LaunchSupport);
                Check(checks, key + "-native-first-motor-effects-stop-at-cutoff", shot.CutoffObserved);
                Check(checks, key + "-native-motor-emitter-follows-source-nozzle", shot.Positioned && shot.Captured.Count == 6);
                Check(checks, key + "-native-launch-flight-model-transition", shot.BeforeModel && shot.AfterModel);
                NaturalWeaponsTrial.RetireMissileFixture(shot.Missile, true);
                row["nativeMissileAttacksAfterRetirement"] = targetTrack != null ? targetTrack.missileAttacks : -1;
                Check(checks, key + "-native-motor-review-reservation-released", targetTrack != null && targetTrack.missileAttacks == attacksBefore);
                System.IO.File.WriteAllText(output, Audit.Json(report));
                yield return null;
                }
                finally { fixture.Retire?.Invoke(); }
            }
            Check(checks, "native-launcher-motor-review-all-requested-weapons", rows.Count == requested.Count &&
                rows.Cast<Dictionary<string, object>>().All(r => (bool)r["spawned"]));
        }

        internal static object TargetState(Unit target)
        {
            if (target == null) return D("exists", false);
            UnitPart[] parts = target.GetAllParts().Where(p => p != null).ToArray();
            Aircraft aircraft = target as Aircraft;
            return D("exists", true, "id", target.persistentID.ToString(), "definition", target.definition != null ? target.definition.jsonKey : null,
                "disabled", target.disabled, "position", V(target.GlobalPosition().AsVector3()), "speed", target.speed,
                "radarAltitude", target.radarAlt, "nativeIrListPresent", target.HasIRSignature(),
                "missileHitPoints", target is Missile ? NaturalWeapons.Get(target, "hitpoints") : null,
                "pilots", aircraft != null ? aircraft.pilots.Where(p => p != null).Select(p => D(
                    "name", p.name, "hitPoints", NaturalWeapons.Get(p, "hitPoints"), "dead", p.dead, "ejected", p.ejected,
                    "nativeAccelerationG", V(p.accel), "nativeGForce", p.gForce, "previousVelocity", V(p.velocityPrev))).ToArray() : null,
                "partHealthTotal", parts.Sum(p => p.hitPoints), "detachedParts", parts.Count(p => p.IsDetached()),
                "parts", parts.Select(p => D("name", p.name, "instanceId", p.GetInstanceID(), "hitPoints", p.hitPoints, "detached", p.IsDetached())).ToArray());
        }
        private sealed class LaunchShot
        {
            internal Missile Missile;
            internal Dictionary<string, object> Row;
            internal float Cutoff, Separation;
            internal bool Positioned = true, Timely = true, Burning = true, CutoffObserved, BeforeModel, AfterModel, LaunchSupport;
            internal readonly HashSet<string> Captured = new HashSet<string>();
        }

        internal static object MotorState(Missile missile)
        {
            return ((Array)NaturalWeapons.Get(missile, "motors")).Cast<object>().Select((m, i) => D(
                "stage", i, "activated", NaturalWeapons.Get(m, "activated"), "fuel", NaturalWeapons.Get(m, "fuelMass"),
                "particles", ((ParticleSystem[])NaturalWeapons.Get(m, "particleSystems")).Select(p => D(
                    "name", p.name, "active", p.gameObject.activeInHierarchy, "emitting", p.isEmitting, "count", p.particleCount,
                    "renderer", p.GetComponent<ParticleSystemRenderer>().enabled,
                    "scale", V(p.transform.lossyScale), "localPosition", V(missile.transform.InverseTransformPoint(p.transform.position)),
                    "material", p.GetComponent<ParticleSystemRenderer>().sharedMaterial.name,
                    "lifetime", p.main.startLifetime.constantMax, "speed", p.main.startSpeed.constantMax,
                    "rate", p.emission.rateOverTime.constantMax, "loop", p.main.loop, "duration", p.main.duration,
                    "shapeRadius", p.shape.radius, "size", p.main.startSizeMultiplier
                )).ToArray(),
                "lights", ((Light[])NaturalWeapons.Get(m, "lights")).Select(l => D("name", l.name, "enabled", l.enabled, "active", l.gameObject.activeInHierarchy, "intensity", l.intensity, "range", l.range)).ToArray(),
                "audio", ((AudioSource[])NaturalWeapons.Get(m, "audioSources")).Select(a => D("name", a.name, "enabled", a.enabled, "active", a.gameObject.activeInHierarchy, "playing", a.isPlaying, "loop", a.loop, "clip", a.clip?.name, "volume", a.volume)).ToArray(),
                "trails", ((TrailEmitter[])NaturalWeapons.Get(m, "trailEmitters")).Select(t => D("name", t.name, "active", t.gameObject.activeInHierarchy, "enabled", t.enabled)).ToArray()
            )).ToArray();
        }
        internal static float[] V(Vector3 v) { return new[] { v.x, v.y, v.z }; }
        private static void Check(List<object> checks, string name, bool passed)
        { checks.Add(D("name", name, "passed", passed)); }
        internal static Dictionary<string, object> D(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
            return result;
        }
    }
}
