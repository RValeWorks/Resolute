using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit offline fixture. Only initial poses/velocities and seeker disable
    // are controlled; collision, motor, fuse, physics and destruction stay native.
    internal static class MissileImpactTrial
    {
        private sealed class Case
        {
            internal Missile Missile;
            internal Dictionary<string, object> Record;
            internal List<object> Samples = new List<object>();
            internal int Bounces, ContactCallbacks, Detonations;
            internal bool ArmedAtDetonation, Disabled;
            internal Vector3 BeforeVelocity;
        }
        private static readonly Dictionary<Missile, Case> Cases = new Dictionary<Missile, Case>();
        private static readonly string[] Keys = { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd" };

        internal static IEnumerator Run(Ship owner, Dictionary<string, object> report, List<object> checks, string output)
        {
            MissionTrial.HoldControllers(owner);
            string requested = Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-projectile-keys") ?? "";
            string[] selected = Keys.Where(k => requested.Length == 0 || requested.Split(',').Contains(k)).ToArray();
            Vector3 land = FindLand(owner.transform.position);
            Vector3 sea = FindSea(owner.transform.position);
            var records = new List<object>();
            report["impactObservations"] = records;
            report["impactScope"] = "Native physical collisions after an explicitly controlled initial trajectory. Seeker disabled so target loss cannot mask an impact failure. Includes armed glancing/steep terrain, armed water and unarmed terrain/water contact; no live trajectory or force is overwritten.";
            report["landImpactPoint"] = V(land); report["seaImpactPoint"] = V(sea);
            var patcher = new Harmony("com.resolute.impacttrial");
            patcher.Patch(AccessTools.Method(typeof(Missile), "DetectCollisions"),
                prefix: new HarmonyMethod(typeof(MissileImpactTrial), nameof(BeforeCollision)),
                postfix: new HarmonyMethod(typeof(MissileImpactTrial), nameof(AfterCollision)));
            patcher.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)),
                prefix: new HarmonyMethod(typeof(MissileImpactTrial), nameof(Detonating)));
            bool passed = true;
            try
            {
                foreach (string key in selected)
                foreach (string scenario in new[] { "armed-glancing-terrain", "armed-steep-terrain", "armed-water", "unarmed-terrain", "unarmed-water" })
                {
                    bool armed = !scenario.StartsWith("unarmed", StringComparison.Ordinal);
                    bool water = scenario.EndsWith("water", StringComparison.Ordinal);
                    float pitch = scenario.Contains("glancing") ? 20f : 65f;
                    Vector3 direction = new Vector3(Mathf.Cos(pitch * Mathf.Deg2Rad), -Mathf.Sin(pitch * Mathf.Deg2Rad), 0f);
                    Vector3 impact = water ? sea : land;
                    Vector3 start = impact - direction * 180f;
                    Missile missile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(NaturalWeapons.Definitions[key], start,
                        Quaternion.LookRotation(direction, Vector3.up), direction * 1020f, null, owner);
                    NaturalWeapons.Set(missile, "seeker", null);
                    foreach (MissileSeeker seeker in missile.GetComponents<MissileSeeker>()) seeker.enabled = false;
                    missile.SetTangible(true);
                    NaturalWeapons.Set(NaturalWeapons.Get(missile, "warhead"), "Armed", armed);
                    var state = new Case { Missile = missile, Record = new Dictionary<string, object> {
                        ["key"] = key, ["scenario"] = scenario, ["initialPosition"] = V(start), ["initialVelocity"] = V(direction * 1020f),
                        ["initialArmed"] = armed, ["impactFuse"] = (bool)NaturalWeapons.Get(missile, "impactFuse"),
                        ["initialTangible"] = missile.IsTangible() } };
                    state.Record["samples"] = state.Samples;
                    Cases.Add(missile, state); records.Add(state.Record);
                    missile.gameObject.AddComponent<ImpactContactObserver>();
                    report["phase"] = "impact-" + key + "-" + scenario;
                    float started = Time.time;
                    while (missile != null && Time.time - started < 1.25f)
                    {
                        state.Samples.Add(new Dictionary<string, object> { ["elapsed"] = Time.time - started,
                            ["position"] = V(missile.transform.position), ["velocity"] = V(missile.rb.velocity),
                            ["disabled"] = missile.disabled, ["armed"] = missile.IsArmed() });
                        state.Disabled |= missile.disabled;
                        if (missile.disabled) break;
                        yield return new WaitForFixedUpdate();
                    }
                    CapsuleCollider remainingBody = missile != null ? missile.GetComponent<CapsuleCollider>() : null;
                    bool visibleBodyColliderEnabled = remainingBody != null && remainingBody.enabled;
                    bool spentBodyCleared = !armed || !visibleBodyColliderEnabled;
                    bool ok = state.Disabled && state.Detonations == 1 && state.Bounces == 0 && state.ArmedAtDetonation == armed && spentBodyCleared;
                    state.Record["disabledAfterImpact"] = state.Disabled;
                    state.Record["nativeDetonations"] = state.Detonations;
                    state.Record["armedAtDetonation"] = state.ArmedAtDetonation;
                    state.Record["nativeVelocityReflections"] = state.Bounces;
                    state.Record["physicalContactCallbacks"] = state.ContactCallbacks;
                    state.Record["remainingBodyColliderEnabled"] = visibleBodyColliderEnabled;
                    state.Record["armedSpentBodyColliderCleared"] = spentBodyCleared;
                    state.Record["passed"] = ok;
                    checks.Add(new Dictionary<string, object> { ["name"] = "impact-" + key + "-" + scenario, ["passed"] = ok });
                    passed &= ok;
                    Save(output, report);
                    Cases.Remove(missile);
                    if (missile != null) Object.Destroy(missile.gameObject);
                    yield return new WaitForSeconds(.15f);
                }
            }
            finally
            {
                patcher.UnpatchSelf();
                foreach (Missile missile in Cases.Keys.ToArray()) if (missile != null) Object.Destroy(missile.gameObject);
                Cases.Clear();
            }
            report["impactChecksPassed"] = passed;
            Save(output, report);
            if (!passed) throw new InvalidOperationException("Native missile impact checks failed; see per-case physical trace.");
        }

        private static void BeforeCollision(Missile __instance)
        { if (Cases.TryGetValue(__instance, out var state)) state.BeforeVelocity = __instance.rb.velocity; }
        private static void AfterCollision(Missile __instance)
        {
            if (!Cases.TryGetValue(__instance, out var state)) return;
            if (!__instance.disabled && state.BeforeVelocity.y < -10f && __instance.rb.velocity.y > 1f &&
                Vector3.Dot(state.BeforeVelocity.normalized, __instance.rb.velocity.normalized) < .8f) state.Bounces++;
        }
        private static void Detonating(Missile __instance)
        {
            if (!Cases.TryGetValue(__instance, out var state) || __instance.disabled) return;
            state.Detonations++; state.ArmedAtDetonation = __instance.IsArmed();
            state.Record["detonationPosition"] = V(__instance.transform.position);
            state.Record["detonationAge"] = __instance.timeSinceSpawn;
        }
        internal static void Contact(Missile missile)
        { if (missile != null && Cases.TryGetValue(missile, out var state)) state.ContactCallbacks++; }
        private static Vector3 FindLand(Vector3 origin)
        {
            for (int radius = 0; radius <= 30; radius++)
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                Vector3 top = new Vector3(origin.x + x * 1200f, Datum.LocalSeaY + 12000f, origin.z + z * 1200f);
                if (Physics.Raycast(top, Vector3.down, out var hit, 24000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) &&
                    hit.point.y > Datum.LocalSeaY + 20f && hit.normal.y > .92f && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial)
                    return hit.point;
            }
            throw new InvalidOperationException("No suitable native terrain patch found for impact review.");
        }
        private static Vector3 FindSea(Vector3 origin)
        {
            for (int n = 1; n <= 30; n++)
            {
                Vector3 point = origin + Vector3.right * (n * 500f); point.y = Datum.LocalSeaY;
                if (!Physics.Raycast(point + Vector3.up * 10000f, Vector3.down, out var hit, 20000f, PhysicsLayers.StaticsMask,
                    QueryTriggerInteraction.Ignore) || hit.point.y < Datum.LocalSeaY - 10f) return point;
            }
            throw new InvalidOperationException("No clear water patch found for impact review.");
        }
        private static object V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static void Save(string output, Dictionary<string, object> report) =>
            File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
    }
    internal sealed class ImpactContactObserver : MonoBehaviour
    {
        private void OnCollisionEnter(Collision collision) => MissileImpactTrial.Contact(GetComponent<Missile>());
    }
}
