using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit copied-game diagnostic. Production damage always enters through
    // the game's normal native calls; no trial code runs in normal missions.
    internal static class DamageVisualTrial
    {
        private sealed class Subject
        {
            internal Ship Ship;
            internal string Label;
            internal Bounds Envelope;
            internal Vector3 Approach;
            internal Quaternion InitialRotation;
            internal ShipPart LastTarget;
            internal bool ParentSeparated;
            internal bool SevereOutcome;
            internal readonly HashSet<string> SevereOutcomeReasons = new HashSet<string>();
            internal readonly List<object> DamageEvents = new List<object>();
            internal readonly List<Missile> Missiles = new List<Missile>();
            internal readonly Dictionary<ShipPart, Action<UnitPart.OnApplyDamage>> Handlers = new Dictionary<ShipPart, Action<UnitPart.OnApplyDamage>>();
        }

        private static readonly Dictionary<Missile, Dictionary<string, object>> ObservedShots = new Dictionary<Missile, Dictionary<string, object>>();

        private static readonly List<Dictionary<string, object>> MatchedWaves = new List<Dictionary<string, object>>();

        private static void ObserveWave(Shockwave wave, ResoluteShockwave.ReceiverAudit audit)
        {
            Vector3 position = wave.transform.position;
            Dictionary<string, object> shot = ObservedShots.Values.Where(s => Convert.ToBoolean(s["detonated"]) &&
                Time.time - Convert.ToSingle(s["detonationTime"]) < 1f).OrderBy(s => {
                    float[] p = (float[])s["detonationPosition"]; return Vector3.Distance(position, new Vector3(p[0], p[1], p[2]));
                }).FirstOrDefault();
            if (shot == null) return;
            float[] origin = (float[])shot["detonationPosition"];
            float separation = Vector3.Distance(position, new Vector3(origin[0], origin[1], origin[2]));
            if (separation > 15f) return;
            var data = new Dictionary<string, object> {
                ["ship"] = shot["ship"], ["wavePosition"] = V(position), ["detonationSeparationM"] = separation,
                ["timeAfterDetonation"] = Time.time - Convert.ToSingle(shot["detonationTime"]),
                ["nativeEntries"] = audit.NativeEntries, ["nativeSectionEntries"] = audit.NativeSectionEntries,
                ["distinctSectionOwners"] = audit.DistinctSectionOwners, ["removedDuplicates"] = audit.RemovedDuplicates,
                ["resultEntries"] = audit.ResultEntries, ["resultSectionEntries"] = audit.ResultSectionEntries,
                ["duplicateSectionOwners"] = audit.DuplicateSectionOwners, ["radiusMismatches"] = audit.RadiusMismatches,
                ["boundsMismatches"] = audit.BoundsMismatches, ["otherEntries"] = audit.OtherEntries,
                ["otherEntriesUnchanged"] = audit.OtherEntriesUnchanged };
            object prior;
            if (!shot.TryGetValue("nativeShockwaves", out prior)) shot["nativeShockwaves"] = prior = new List<object>();
            ((List<object>)prior).Add(data); MatchedWaves.Add(data);
        }

        private static void ObserveDetonation(Missile __instance, Vector3 normal, bool hitArmor, bool hitTerrain)
        {
            Dictionary<string, object> shot;
            if (!ObservedShots.TryGetValue(__instance, out shot) || Convert.ToBoolean(shot["detonated"])) return;
            shot["detonated"] = true; shot["armedAtDetonation"] = __instance.IsArmed();
            shot["hitArmor"] = hitArmor; shot["hitTerrain"] = hitTerrain;
            shot["detonationTime"] = Time.time; shot["detonationPosition"] = V(__instance.transform.position);
            shot["detonationNormal"] = V(normal); shot["impactSpeedMps"] = __instance.rb != null ? __instance.rb.velocity.magnitude : 0f;
        }

        internal static IEnumerator Run(Ship original, Dictionary<string, object> report, List<object> checks, string output)
        {
            string folder = Path.Combine(Path.GetDirectoryName(output), "game_previews", "damage_v4");
            Directory.CreateDirectory(folder);
            report["damageUvChartAudit"] = SurfaceDamageUv.LastAudit;
            report["damageSamplerAudit"] = NavalMaterials.DamageSamplerAudit;
            report["nativePaintStyleAudit"] = SurfacePaintStyle.LastAudit;
            report["nativePlatingMaterialAudit"] = NavalMaterials.NativePlatingAudit;
            report["nativeDeckMaterialAudit"] = NavalMaterials.NativeDeckAudit;
            Require(checks, "damage-v4-native-metric-paint-preserves-geometry-and-accents", SurfacePaintStyle.HasValidAudit);
            var platingAudit = NavalMaterials.NativePlatingAudit as Dictionary<string, object>;
            Require(checks, "damage-v4-native-plating-maps-and-tiling-preserved", platingAudit != null &&
                Convert.ToBoolean(platingAudit["allNativeMapReferencesPreserved"]) && Convert.ToBoolean(platingAudit["nativeDamageTilingPreserved"]));
            var uvAudit = SurfaceDamageUv.LastAudit as Dictionary<string, object>;
            Require(checks, "damage-v4-uv-phase-preserves-paint-and-geometry", uvAudit != null &&
                Convert.ToInt32(uvAudit["shiftedCharts"]) > 0 && Convert.ToInt32(uvAudit["shiftedVertices"]) > 0 &&
                Convert.ToSingle(uvAudit["maximumUvModuloError"]) <= .000002f &&
                (string)uvAudit["positionNormalIndexSha256Before"] == (string)uvAudit["positionNormalIndexSha256After"] &&
                (string)uvAudit["originalUvSha256"] != (string)uvAudit["shiftedUvSha256"] &&
                NavalMaterials.DamageSamplerAudit.Values.Cast<Dictionary<string, object>>().All(a =>
                    Convert.ToBoolean(a["sourceBaseReferencePreserved"]) && Convert.ToBoolean(a["sourceNormalReferencePreserved"]) &&
                    Convert.ToBoolean(a["sourceResponseReferencePreserved"])));
            Require(checks, "damage-v4-native-damage-samplers-repeat", NavalMaterials.DamageSamplerAudit.Count > 0 &&
                NavalMaterials.DamageSamplerAudit.Values.Cast<Dictionary<string, object>>().All(a => Convert.ToBoolean(a["allSamplersRepeat"])));
            var trials = new List<object>(); report["matchedNativeDamageTrials"] = trials;
            MissileDefinition weapon = Encyclopedia.Lookup.Values.OfType<MissileDefinition>().First(d =>
                string.Equals(d.jsonKey, "AShM1", StringComparison.OrdinalIgnoreCase));
            Vector3 heading = Vector3.ProjectOnPlane(original.transform.forward, Vector3.up).normalized;
            if (heading.sqrMagnitude < .5f) heading = Vector3.forward;
            Quaternion rotation = Quaternion.LookRotation(heading, Vector3.up);
            report["damageProtocol"] = new Dictionary<string, object> {
                ["weapon"] = weapon.jsonKey, ["repetitions"] = 2, ["impactSpeedMps"] = 250f, ["launchStandoffM"] = 80f,
                ["attackSequence"] = new[] { "Hull_R", "Hull_FR", "Hull_hangarR", "Hull_ExhaustStack" },
                ["minimumPairedAttacks"] = 3,
                ["maximumAttacksPerSubject"] = 12,
                ["escalation"] = "Apply three paired attacks even if one ship is disabled sooner. Then retire each subject that has already reached a native severe outcome and continue the same attack sequence only against remaining subjects, to twelve attacks. Later unmatched attacks are explicitly labeled and are not paired damage comparisons. An already sinking comparator must not prevent testing the surviving ship. Use the first exposed common fallback compartment if needed; record all substitutions.",
                ["structuralCapture"] = "Each normal image is accompanied by an image from the same camera and simulation frame with only scene ParticleSystemRenderer components temporarily hidden. Original enabled states are restored in finally before simulation advances. Camera directions remain aligned with the original paired heading and world up so actual ship roll remains visible.",
                ["shortRangeArming"] = "After native launch initialization, both test missiles are explicitly armed and made tangible together. Native Detonate is observed to prove each actual hit was armed and struck armor; an unarmed kinetic hit is a failure.",
                ["secondsBetweenAttacks"] = 8f,
                ["shockwaveAssociation"] = "Native waves beginning within 15 m and one second of an observed trial missile detonation are attached to that nearest actual attack; separation and time are recorded for each wave.",
                ["scope"] = "Two fresh upright ships per repetition. Same native AShM1 prefab, starting speed, starboard incidence, standoff, heading, weather, and paired launch frame. Shots aim at each corresponding native compartment's actual collision surface; their vertical locations follow compartment geometry. All warhead yields, armor, native damage and sinking physics remain untouched. Screenshots use the same viewing direction and fit each original vessel envelope; image framing is normalized, damage is not. No hit-point setting or forced detachment is used." };
            var hooks = new Harmony("Resolute.DamageVisualTrial.v4");
            hooks.Patch(AccessTools.Method(typeof(Missile), nameof(Missile.Detonate)), prefix:
                new HarmonyMethod(AccessTools.Method(typeof(DamageVisualTrial), nameof(ObserveDetonation))));
            ResoluteShockwave.ReceiversPrepared += ObserveWave;
            try
            {
            for (int repetition = 1; repetition <= 2; repetition++)
            {
                MatchedWaves.Clear();
                var subjects = new List<Subject>();
                try
                {
                    foreach (string key in new[] { "Destroyer1", original.definition.jsonKey })
                    {
                        var definition = (ShipDefinition)Encyclopedia.Lookup[key];
                        GlobalPosition point = FindPoint(original); point.y = Datum.SeaLevel.y + definition.spawnOffset.y;
                        var saved = new SavedShip("damage_v4_" + repetition + "_" + key) { type = key,
                            faction = original.NetworkHQ.faction.factionName, globalPosition = point, rotation = rotation,
                            skill = 1f, holdPosition = true };
                        Ship ship;
                        Require(checks, "damage-v4-spawn-" + repetition + "-" + key, NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship));
                        var subject = new Subject { Ship = ship, Label = key == "Destroyer1" ? "dynamo" : "resolute", Approach = rotation * Vector3.right, InitialRotation = rotation };
                        subjects.Add(subject); ship.LinkSavedUnit(saved); MissionTrial.HoldControllers(ship); ship.SetHoldPosition(true);
                        foreach (LODGroup group in ship.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
                        yield return null; yield return new WaitForFixedUpdate();
                        subject.Envelope = Envelope(ship);
                        foreach (ShipPart part in ship.partLookup.OfType<ShipPart>())
                        {
                            ShipPart owner = part;
                            Action<UnitPart.OnApplyDamage> handler = damage => subject.DamageEvents.Add(new Dictionary<string, object> {
                                ["part"] = owner.name, ["time"] = Time.time, ["health"] = damage.hitPoints,
                                ["pierce"] = damage.pierceDamage, ["blast"] = damage.blastDamage, ["impact"] = damage.impactDamage });
                            owner.onApplyDamage += handler; subject.Handlers.Add(owner, handler);
                        }
                        Require(checks, "damage-v4-upright-" + repetition + "-" + subject.Label, Vector3.Dot(ship.transform.up, Vector3.up) > .99f);
                        if (subject.Label == "resolute") ValidateGeometry(subject, checks, repetition);
                    }
                    yield return new WaitForSeconds(2f);
                    var trial = new Dictionary<string, object> { ["repetition"] = repetition };
                    var stages = new List<object>(); trial["stages"] = stages; trials.Add(trial);
                    foreach (Subject subject in subjects)
                        stages.Add(FleetSnapshot(subject, folder, repetition, "intact"));
                    Save(output, report);
                    string[] sequence = { "Hull_R", "Hull_FR", "Hull_hangarR", "Hull_ExhaustStack" };
                    for (int attack = 0; attack < 12; attack++)
                    {
                        if (attack >= 3 && subjects.All(su => su.SevereOutcome)) break;
                        var activeSubjects = attack < 3 ? subjects : subjects.Where(su => !su.SevereOutcome).ToList();
                        string requestedTarget = attack < sequence.Length ? sequence[attack] : (attack % 2 == 0 ? "Hull_hangarFloor" : "Hull_ExhaustStack");
                        string targetName = null;
                        var contacts = new Dictionary<Subject, Vector3>();
                        var targets = new Dictionary<Subject, ShipPart>();
                        foreach (string candidate in new[] { requestedTarget, "Hull_hangarFloor", "Hull_ExhaustStack", "Hull_CR", "Hull_R", "Hull_FR", "Hull_hangarR" }.Distinct())
                        {
                            contacts.Clear(); targets.Clear();
                            foreach (Subject subject in activeSubjects)
                            {
                                ShipPart target = subject.Ship.partLookup.OfType<ShipPart>().FirstOrDefault(p => p != null && p.name == candidate);
                                Vector3 contact;
                                if (target == null || !TrySurfaceTarget(subject, target, out contact)) break;
                                targets.Add(subject, target); contacts.Add(subject, contact);
                            }
                            if (contacts.Count == activeSubjects.Count) { targetName = candidate; break; }
                        }
                        Require(checks, "damage-v4-common-exposed-target-" + repetition + "-" + attack, targetName != null);
                        report["phase"] = "matched-native-damage-repeat-" + repetition + "-attack-" + (attack + 1);
                        var launches = new List<object>();
                        var eventCounts = subjects.ToDictionary(s => s, s => s.DamageEvents.Count);
                        var missiles = new List<Missile>();
                        foreach (Subject subject in activeSubjects)
                        {
                            ShipPart target = targets[subject];
                            Vector3 contact = contacts[subject];
                            Vector3 direction = -subject.Approach;
                            subject.LastTarget = target;
                            Vector3 start = contact - direction * 80f;
                            Missile missile = NetworkSceneSingleton<Spawner>.i.SpawnMissile(weapon, start, Quaternion.LookRotation(direction), direction * 250f, subject.Ship, original);
                            subject.Missiles.Add(missile); missiles.Add(missile);
                            var launch = new Dictionary<string, object> { ["ship"] = subject.Label, ["part"] = target.name,
                                ["detonated"] = false, ["armedAtLaunch"] = missile.IsArmed(),
                                ["launch"] = V(start), ["expectedContact"] = V(contact), ["direction"] = V(direction),
                                ["yield"] = missile.GetYield(), ["speedMps"] = 250f, ["launchTime"] = Time.time };
                            launches.Add(launch); ObservedShots.Add(missile, launch);
                        }
                        var attackRecord = new Dictionary<string, object> { ["attack"] = attack + 1, ["pairedComparison"] = activeSubjects.Count == subjects.Count,
                            ["activeSubjects"] = activeSubjects.Select(su => su.Label).ToArray(),
                            ["requestedTargetCompartment"] = requestedTarget, ["targetCompartment"] = targetName, ["launches"] = launches };
                        stages.Add(attackRecord); Save(output, report);
                        // Allow native StartMissile and its launch-frame handoff to finish,
                        // then arm the short-range test approach on both missiles together.
                        yield return null; yield return new WaitForFixedUpdate();
                        foreach (Missile missile in missiles) if (missile != null)
                        {
                            missile.Arm(); missile.SetTangible(true);
                            Dictionary<string, object> shot = ObservedShots[missile];
                            shot["armedBeforeApproach"] = missile.IsArmed(); shot["tangibleBeforeApproach"] = missile.IsTangible();
                            shot["armingTime"] = Time.time; shot["armingPosition"] = V(missile.transform.position);
                        }
                        float started = Time.time;
                        var observations = new List<object>(); attackRecord["observations"] = observations;
                        foreach (float seconds in new[] { 1.5f, 8f })
                        {
                            while (Time.time - started < seconds) yield return new WaitForFixedUpdate();
                            foreach (Subject subject in subjects)
                                observations.Add(FleetSnapshot(subject, folder, repetition, "attack_" + (attack + 1) + "_" + seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s"));
                            Save(output, report);
                        }
                        foreach (Missile missile in missiles)
                        {
                            Dictionary<string, object> shot = ObservedShots[missile];
                            Require(checks, "damage-v4-armed-warhead-hit-" + repetition + "-" + attack + "-" + shot["ship"],
                                Convert.ToBoolean(shot["detonated"]) && shot.ContainsKey("armedAtDetonation") && Convert.ToBoolean(shot["armedAtDetonation"]) &&
                                shot.ContainsKey("hitArmor") && Convert.ToBoolean(shot["hitArmor"]));
                        }
                        foreach (Subject subject in activeSubjects)
                        {
                            attackRecord[subject.Label + "DamageEvents"] = subject.DamageEvents.Skip(eventCounts[subject]).ToArray();
                            Require(checks, "damage-v4-native-warhead-damaged-" + repetition + "-" + attack + "-" + subject.Label,
                                subject.DamageEvents.Skip(eventCounts[subject]).Cast<Dictionary<string, object>>().Any(e =>
                                    Convert.ToSingle(e["pierce"]) + Convert.ToSingle(e["blast"]) > 0f));
                            if (subject.Label == "resolute") ValidateRetention(subject, checks, repetition, attack);
                        }
                        Save(output, report);
                    }
                    Require(checks, "damage-v4-logical-wave-owners-" + repetition, MatchedWaves.All(w =>
                        Convert.ToInt32(w["resultSectionEntries"]) == Convert.ToInt32(w["distinctSectionOwners"]) &&
                        Convert.ToInt32(w["duplicateSectionOwners"]) == 0));
                    Require(checks, "damage-v4-native-wave-bounds-radius-" + repetition, MatchedWaves.All(w =>
                        Convert.ToInt32(w["radiusMismatches"]) == 0 && Convert.ToInt32(w["boundsMismatches"]) == 0));
                    Require(checks, "damage-v4-other-wave-receivers-unchanged-" + repetition, MatchedWaves.All(w => Convert.ToBoolean(w["otherEntriesUnchanged"])));
                    Require(checks, "damage-v4-compound-wave-dedup-observed-" + repetition, MatchedWaves.Any(w =>
                        (string)w["ship"] == "resolute" && Convert.ToInt32(w["removedDuplicates"]) > 0));
                    trial["associatedNativeShockwaveCount"] = MatchedWaves.Count;
                    foreach (Subject subject in subjects)
                        Require(checks, "damage-v4-severe-native-outcome-" + repetition + "-" + subject.Label, subject.SevereOutcome);
                    trial["complete"] = true; Save(output, report);
                }
                finally
                {
                    foreach (Subject subject in subjects)
                    {
                        foreach (var handler in subject.Handlers) if (handler.Key != null) handler.Key.onApplyDamage -= handler.Value;
                        foreach (Missile missile in subject.Missiles) if (missile != null) Object.Destroy(missile.gameObject);
                        Cleanup(subject.Ship);
                    }
                }
                yield return null; yield return null;
            }
            report["damageVisualTrialComplete"] = true; Save(output, report);
            }
            finally { ResoluteShockwave.ReceiversPrepared -= ObserveWave; hooks.UnpatchSelf(); ObservedShots.Clear(); MatchedWaves.Clear(); }
        }

        private static void ValidateGeometry(Subject subject, List<object> checks, int repetition)
        {
            ResoluteStructuralSection[] sections = subject.Ship.GetComponentsInChildren<ResoluteStructuralSection>(true);
            string prefix = "damage-v4-geometry-" + repetition + "-";
            Require(checks, prefix + "native-compartments", sections.Length == StructuralGeometry.ExpectedCompartmentCount);
            Require(checks, prefix + "source-coverage", sections.All(s => Mathf.Abs(s.SurfaceArea - s.CoveredArea) < Mathf.Max(.02f, s.SurfaceArea * .0001f)));
            Require(checks, prefix + "healthy-renderer-budget", sections.All(s => s.PlateRenderers.Length == 0 && s.Body.enabled));
            Require(checks, prefix + "bounded-connected-regions", sections.All(s => s.Pieces.Length > 0 && s.Pieces.Length <= 128 && s.Pieces.All(p => p.Area >= 2f)));
            Require(checks, prefix + "native-owner-collision", sections.All(s => s.PlateColliders.Length == s.Pieces.Length && s.PlateColliders.All(c => c == null) && s.GroupColliders.All(c => c.enabled && c.GetComponent<UnitPart>() == s.Part)));
            Require(checks, prefix + "native-paint", sections.All(s => NavalMaterials.UsesNativeDamage(s.Body.sharedMaterial)));
        }

        private static void ValidateRetention(Subject subject, List<object> checks, int repetition, int attack)
        {
            var sections = subject.Ship.partLookup.Where(p => p != null).Select(p => p.GetComponent<ResoluteStructuralSection>()).Where(s => s != null).ToArray();
            string prefix = "damage-v4-retention-" + repetition + "-" + attack + "-";
            Require(checks, prefix + "coherent-shell", sections.All(s => s.Body != null && s.Body.gameObject.activeSelf && s.RetainedSurfaceFraction >= .799f));
            Require(checks, prefix + "collision-removal", sections.All(s => s.PlateColliders.Count(c => c != null && !c.enabled) == s.ReleasedCount));
            Require(checks, prefix + "mass-conservation", sections.All(s => Mathf.Abs(s.InitialPartMass - s.Part.mass - s.ReleasedMass) < 5f));
            Require(checks, prefix + "bounded-live-debris", sections.Sum(s => s.ReleasedDebris.Count(d => d != null)) <= ResoluteStructuralSection.MaximumLiveDebrisPerShip);
        }

        private static bool TrySurfaceTarget(Subject subject, ShipPart part, out Vector3 contact)
        {
            contact = default(Vector3);
            Collider[] colliders = part.GetComponents<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            if (colliders.Length == 0) return false;
            Bounds bounds = colliders[0].bounds;
            foreach (Collider collider in colliders) bounds.Encapsulate(collider.bounds);
            Vector3 side = subject.Approach;
            var centers = new List<Vector3> { bounds.center };
            centers.AddRange(colliders.OrderBy(c => (c.bounds.center - bounds.center).sqrMagnitude).Select(c => c.bounds.center));
            Vector3 forward = subject.InitialRotation * Vector3.forward;
            foreach (float along in new[] { 0f, -.3f, .3f })
            foreach (float height in new[] { -.3f, .3f })
                centers.Add(bounds.center + forward * (along * bounds.size.magnitude) + Vector3.up * (height * bounds.size.y));
            foreach (Vector3 candidate in centers)
            {
                Vector3 center = candidate; center.y = Mathf.Max(center.y, Datum.LocalSeaY + 2f);
                if (center.y >= bounds.max.y - .05f) continue;
                var ray = new Ray(center + side * 500f, -side);
                RaycastHit best = default(RaycastHit); float distance = float.PositiveInfinity;
                foreach (Collider collider in colliders)
                {
                    RaycastHit hit;
                    if (collider.Raycast(ray, out hit, 1000f) && hit.distance < distance) { best = hit; distance = hit.distance; }
                }
                if (!float.IsPositiveInfinity(distance)) { contact = best.point; return true; }
            }
            return false;
        }

        private static Bounds Envelope(Ship ship)
        {
            bool first = true; Bounds bounds = new Bounds();
            foreach (ShipPart part in ship.partLookup.OfType<ShipPart>())
            {
                ResoluteStructuralSection section = part.GetComponent<ResoluteStructuralSection>();
                MeshFilter filter = part.GetComponent<MeshFilter>();
                Mesh mesh = section != null ? section.ClosedMesh : filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Vector3 point = ship.transform.InverseTransformPoint(part.transform.TransformPoint(vertex));
                    if (first) { bounds = new Bounds(point, Vector3.zero); first = false; } else bounds.Encapsulate(point);
                }
            }
            return bounds;
        }

        private static object FleetSnapshot(Subject subject, string folder, int repetition, string stage)
        {
            Ship ship = subject.Ship;
            Vector3 center = ship.transform.TransformPoint(subject.Envelope.center);
            float distance = subject.Envelope.size.magnitude * 1.5f;
            Vector3 offset = subject.InitialRotation * new Vector3(1f, .48f, .64f).normalized * distance;
            Vector3 sidePosition = center + subject.InitialRotation * new Vector3(1f, .18f, 0).normalized * distance;
            ShipPart[] parts = ship.partLookup.OfType<ShipPart>().Where(p => p != null).ToArray();
            var sections = parts.Select(p => p.GetComponent<ResoluteStructuralSection>()).Where(s => s != null).ToArray();
            subject.ParentSeparated |= parts.Any(p => p.IsDetached() && (p.name == "Destroyer1" || p.name == "Hull_CR" || p.name == "Hull_hangarFloor" || p.name == "Hull_ExhaustStack"));
            ShipPart core = ship.GetComponent<ShipPart>();
            if (subject.ParentSeparated) subject.SevereOutcomeReasons.Add("native-parent-separated");
            if (ship.disabled) subject.SevereOutcomeReasons.Add("native-ship-disabled");
            if (core != null && core.hitPoints <= 0f) subject.SevereOutcomeReasons.Add("native-core-destroyed");
            subject.SevereOutcome |= subject.ParentSeparated || ship.disabled || (core != null && core.hitPoints <= 0f);
            if (subject.SevereOutcome && Vector3.Dot(ship.transform.up, Vector3.up) < .7f) subject.SevereOutcomeReasons.Add("capsized");
            string stem = "repeat_" + repetition + "_" + subject.Label + "_" + stage;
            object detailCapture = null;
            Vector3 detailTarget = Vector3.zero, detailPosition = Vector3.zero;
            bool intact = stage == "intact";
            ShipPart detailPart = intact ? parts.FirstOrDefault(p => p.name == "Hull_R") : subject.LastTarget;
            float detailDistance = 0f;
            if (detailPart != null)
            {
                ResoluteStructuralSection section = detailPart.GetComponent<ResoluteStructuralSection>();
                MeshFilter filter = detailPart.GetComponent<MeshFilter>();
                Bounds bounds = section != null ? section.LocalBounds : filter.sharedMesh.bounds;
                Vector3 targetLocal = bounds.center;
                if (intact) targetLocal.y = bounds.max.y - 5f;
                detailTarget = detailPart.transform.TransformPoint(targetLocal);
                detailDistance = intact ? 75f : Mathf.Max(35f, bounds.size.magnitude * 1.4f);
                detailPosition = detailTarget + (subject.Approach + Vector3.up * .4f + subject.InitialRotation * Vector3.forward * .25f).normalized * detailDistance;
                detailCapture = Capture(detailPosition, detailTarget, Path.Combine(folder, stem + (intact ? "_intact_hull_detail.png" : "_impact_compartment.png")));
            }
            object intactBridgeCapture = null;
            ShipPart bridgePart = intact ? parts.FirstOrDefault(p => p.name == "Hull_Bridge") : null;
            if (bridgePart != null)
            {
                ResoluteStructuralSection section = bridgePart.GetComponent<ResoluteStructuralSection>();
                Bounds bounds = section != null ? section.LocalBounds : bridgePart.GetComponent<MeshFilter>().sharedMesh.bounds;
                Vector3 target = bridgePart.transform.TransformPoint(bounds.center);
                Vector3 position = target + (subject.Approach + Vector3.up * .3f + subject.InitialRotation * Vector3.forward * .45f).normalized * 50f;
                intactBridgeCapture = Capture(position, target, Path.Combine(folder, stem + "_intact_bridge_detail.png"));
            }
            object wholeCapture = Capture(center + offset, center, Path.Combine(folder, stem + "_whole.png"));
            object sideCapture = Capture(sidePosition, center, Path.Combine(folder, stem + "_side.png"));
            object clearWholeCapture, clearSideCapture, clearDetailCapture = null;
            ParticleSystemRenderer[] particles = Object.FindObjectsOfType<ParticleSystemRenderer>(true);
            bool[] particleStates = particles.Select(p => p.enabled).ToArray();
            try
            {
                foreach (ParticleSystemRenderer particle in particles) particle.enabled = false;
                clearWholeCapture = Capture(center + offset, center, Path.Combine(folder, stem + "_structure_whole.png"));
                clearSideCapture = Capture(sidePosition, center, Path.Combine(folder, stem + "_structure_side.png"));
                if (detailPart != null)
                    clearDetailCapture = Capture(detailPosition, detailTarget, Path.Combine(folder, stem + (intact ? "_structure_intact_hull_detail.png" : "_structure_impact_compartment.png")));
            }
            finally
            {
                for (int index = 0; index < particles.Length; index++) if (particles[index] != null) particles[index].enabled = particleStates[index];
            }
            return new Dictionary<string, object> {
                ["ship"] = subject.Label, ["stage"] = stage, ["time"] = Time.time, ["simulationFrame"] = Time.frameCount,
                ["originalEnvelopeM"] = V(subject.Envelope.size), ["rollUpDot"] = Vector3.Dot(ship.transform.up, Vector3.up),
                ["shipSpeedMps"] = ship.rb != null ? ship.rb.velocity.magnitude : 0f,
                ["nativeDetachedParts"] = parts.Count(p => p.IsDetached()), ["nativeParentSeparated"] = subject.ParentSeparated,
                ["nativeShipDisabled"] = ship.disabled, ["nativeCoreHealth"] = core != null ? core.hitPoints : (object)null,
                ["severeNativeOutcome"] = subject.SevereOutcome, ["severeNativeOutcomeReasons"] = subject.SevereOutcomeReasons.OrderBy(r => r).ToArray(),
                ["impactCompartmentCapture"] = intact ? null : detailCapture,
                ["intactDetailCapture"] = intact ? detailCapture : null, ["intactBridgeDetailCapture"] = intactBridgeCapture,
                ["detailPart"] = detailPart != null ? detailPart.name : null, ["detailCameraDistanceM"] = detailDistance,
                ["detailCameraPolicy"] = intact ? "Same 75 m distance and heading relative view for both Hull_R compartments; target is 5 m below each compartment's top. Bridge detail uses the same 50 m distance and heading relative view." : "Impact compartment bounds framed at 1.4 times diagonal, minimum 35 m; initial heading and world up are retained.",
                ["structuralWholeShipCapture"] = clearWholeCapture, ["structuralSideCapture"] = clearSideCapture,
                ["structuralImpactCompartmentCapture"] = intact ? null : clearDetailCapture,
                ["structuralIntactDetailCapture"] = intact ? clearDetailCapture : null, ["temporarilyHiddenParticleRenderers"] = particleStates.Count(enabled => enabled),
                ["releasedShellRegions"] = sections.Sum(s => s.ReleasedCount), ["liveShellDebris"] = sections.Sum(s => s.ReleasedDebris.Count(d => d != null)),
                ["retainedSurfaceFraction"] = sections.Length > 0 ? sections.Sum(s => s.RetainedSurfaceArea) / sections.Sum(s => s.SurfaceArea) : 1f,
                ["connectedRegions"] = sections.Sum(s => s.Pieces.Length), ["intactCollisionGroups"] = sections.Sum(s => s.GroupColliders.Length),
                ["activeHullColliders"] = sections.Sum(s => s.ActiveCollisionCount), ["expandedCollisionGroups"] = sections.Sum(s => s.ExpandedCollisionGroups), ["retainedDetailComponents"] = sections.Sum(s => s.RetainedDetailComponents),
                ["bakeSource"] = sections.FirstOrDefault()?.CacheSource,
                ["parts"] = parts.Select(p => new Dictionary<string, object> { ["part"] = p.name, ["state"] = Snapshot(p) }).ToArray(),
                ["wholeShipCapture"] = wholeCapture, ["sideCapture"] = sideCapture };
        }

        private static float[] V(Vector3 point) => new[] { point.x, point.y, point.z };

        internal static GlobalPosition FindPoint(Ship original)
        {
            Vector3 origin = original.GlobalPosition().AsVector3();
            Ship[] occupied = UnitRegistry.allUnits.OfType<Ship>().Where(s => s != null).ToArray();
            var candidates = new List<Vector3>();
            for (float radius = 700f; radius <= 4200f; radius += 350f)
            for (int index = 0; index < 16; index++)
            {
                float angle = index * Mathf.PI / 8f;
                candidates.Add(origin + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius);
            }
            // The full trial can leave its moving subject far from the original
            // clear corridor. Search the map's native sea lanes independently
            // of that current position; a lane is a candidate, not depth proof.
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            if (level != null && level.seaLanes != null)
            foreach (var road in level.seaLanes.roads.OrderByDescending(r => r.length))
            for (int segment = 1; segment < road.points.Count; segment++)
            {
                Vector3 a = road.points[segment - 1].AsVector3();
                Vector3 b = road.points[segment].AsVector3();
                a.y = b.y = Datum.SeaLevel.y;
                Vector3 along = b - a;
                int steps = Mathf.Max(1, Mathf.CeilToInt(along.magnitude / 300f));
                Vector3 across = Vector3.Cross(Vector3.up, along.normalized);
                for (int step = 0; step <= steps; step++)
                foreach (float offset in new[] { 0f, 350f, -350f, 700f, -700f })
                    candidates.Add(Vector3.Lerp(a, b, step / (float)steps) + across * offset);
            }

            // Both new subjects use the original orientation. Probe a padded
            // horizontal footprint, including its edges and interior at <=25 m
            // spacing; every accepted sample must hit actual native terrain.
            Vector3 forward = Vector3.ProjectOnPlane(original.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            ShipDefinition donor = (ShipDefinition)Encyclopedia.Lookup["Destroyer1"];
            float halfWidth = Mathf.Max(original.definition.width, donor.width) * .5f + 15f;
            float halfLength = Mathf.Max(original.definition.length, donor.length) * .5f + 25f;
            float footprintRadius = Mathf.Sqrt(halfWidth * halfWidth + halfLength * halfLength);
            int columns = Mathf.Max(2, Mathf.CeilToInt(halfWidth / 25f));
            int rows = Mathf.Max(2, Mathf.CeilToInt(halfLength / 25f));
            foreach (Vector3 candidate in candidates)
            {
                Vector3 surface = candidate; surface.y = Datum.SeaLevel.y;
                bool clear = true;
                foreach (Ship other in occupied)
                {
                    Vector3 separation = other.GlobalPosition().AsVector3() - surface; separation.y = 0;
                    float otherRadius = Mathf.Sqrt(other.definition.width * other.definition.width +
                        other.definition.length * other.definition.length) * .5f;
                    float clearance = Mathf.Max(400f, footprintRadius + otherRadius + 100f);
                    if (separation.sqrMagnitude < clearance * clearance) { clear = false; break; }
                }
                if (!clear) continue;
                var point = new GlobalPosition(surface);
                float minimumDepth = float.MaxValue;
                for (int x = -columns; x <= columns && clear; x++)
                for (int z = -rows; z <= rows; z++)
                {
                    GlobalPosition probe = point + right * (x * halfWidth / columns) + forward * (z * halfLength / rows);
                    if (!PathfindingAgent.RaycastTerrain(probe, out var hit)) { clear = false; break; }
                    float depth = Datum.LocalSeaY - hit.point.y;
                    if (float.IsNaN(depth) || float.IsInfinity(depth) || depth < 40f) { clear = false; break; }
                    minimumDepth = Mathf.Min(minimumDepth, depth);
                }
                if (!clear) continue;
                Debug.Log("[Resolute damage trial] Native terrain accepted at " + surface +
                    "; minimum footprint depth " + minimumDepth.ToString("F3") + " m across " +
                    ((columns * 2 + 1) * (rows * 2 + 1)) + " samples.");
                return point;
            }
            throw new InvalidOperationException("No clear damage-trial footprint with 40 m native terrain depth after local and map-wide sea-lane search (" + candidates.Count + " candidates).");
        }

        private static void Cleanup(Ship subject)
        {
            if (subject == null) return;
            foreach (UnitPart part in subject.partLookup.ToArray())
            {
                if (part == null) continue;
                var section = part.GetComponent<ResoluteStructuralSection>();
                if (section != null)
                    foreach (ResolutePlateDebris debris in section.ReleasedDebris) if (debris != null) Object.Destroy(debris.gameObject);
                if (part.IsDetached() && !part.transform.IsChildOf(subject.transform)) Object.Destroy(part.gameObject);
            }
            Object.Destroy(subject.gameObject);
        }

        private static Dictionary<string, object> Snapshot(ShipPart part)
        {
            ResoluteStructuralSection section = part.GetComponent<ResoluteStructuralSection>();
            var result = new Dictionary<string, object> { ["health"] = part.hitPoints, ["detached"] = part.IsDetached(),
                ["partMassKg"] = part.mass, ["rigidbodyMassKg"] = part.rb != null ? part.rb.mass : 0f };
            if (section != null)
            {
                result["plateCount"] = section.Pieces.Length; result["releasedCount"] = section.ReleasedCount;
                result["releasedMassKg"] = section.ReleasedMass; result["initialPartMassKg"] = section.InitialPartMass;
                result["activePlateColliders"] = section.PlateColliders.Count(c => c != null && c.enabled);
                result["intactCollisionGroups"] = section.GroupColliders.Length; result["expandedCollisionGroups"] = section.ExpandedCollisionGroups;
                result["activeHullColliders"] = section.ActiveCollisionCount;
                result["retainedSurfaceFraction"] = section.RetainedSurfaceFraction;
                result["localizedDamageEvents"] = section.LocalizedDamageEvents; result["unlocalizedDamageEvents"] = section.UnlocalizedDamageEvents;
                result["lastImpactSource"] = section.LastImpactSource; result["lastImpactLocal"] = V(section.LastImpactLocal);
                result["exposedFrameCount"] = section.InteriorFaces.Count(f => f.enabled);
                result["liveGibs"] = section.ReleasedDebris.Where(d => d != null).Select(d => new Dictionary<string, object>
                {
                    ["piece"] = d.PlateIndex, ["diameterM"] = d.Diameter, ["areaM2"] = d.Area,
                    ["massKg"] = d.GetComponent<Rigidbody>().mass, ["speedMps"] = d.GetComponent<Rigidbody>().velocity.magnitude,
                    ["solidMesh"] = d.GetComponent<MeshFilter>().sharedMesh.name,
                    ["convexCollider"] = d.GetComponent<MeshCollider>().convex
                }).ToArray();
                var block = new MaterialPropertyBlock();
                section.Body.GetPropertyBlock(block, 0);
                result["paintPropertyBlockHealth"] = block.GetFloat("_HitPoints");
            }
            return result;
        }

        private static object ShadingComparison(Ship ship, string folder, string paintCandidate)
        {
            Transform visual = ship.transform.Find("ResoluteVisual");
            Vector3 target = visual.TransformPoint(new Vector3(0, 7.026f, -102));
            Vector3 position = visual.TransformPoint(new Vector3(32, 10.526f, -114));
            Renderer[] renderers = ship.GetComponentsInChildren<Renderer>(true);
            var shadows = renderers.ToDictionary(r => r, r => r.shadowCastingMode);
            Renderer[] decorative = renderers.Where(r => r.name.StartsWith("rsl_", StringComparison.Ordinal) &&
                !r.name.StartsWith("rsl_hull_", StringComparison.Ordinal)).ToArray();
            var captures = new List<object>();
            try
            {
                captures.Add(Capture(position, target, Path.Combine(folder, "aft_01_baseline.png")));
                foreach (Renderer renderer in decorative.Where(r => r.name.StartsWith("rsl_flight_deck_safety_nets", StringComparison.Ordinal)))
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                captures.Add(Capture(position, target, Path.Combine(folder, "aft_015_safety_net_shadows_off.png")));
                foreach (Renderer renderer in decorative) renderer.shadowCastingMode = ShadowCastingMode.Off;
                captures.Add(Capture(position, target, Path.Combine(folder, "aft_02_decorative_shadows_off.png")));
                foreach (Renderer renderer in renderers) renderer.shadowCastingMode = ShadowCastingMode.Off;
                captures.Add(Capture(position, target, Path.Combine(folder, "aft_03_all_ship_shadows_off.png")));
            }
            finally { foreach (var item in shadows) if (item.Key != null) item.Key.shadowCastingMode = item.Value; }
            Material[] paint = renderers.SelectMany(r => r.sharedMaterials).Where(NavalMaterials.UsesNativeDamage).Distinct().ToArray();
            var originalNormal = paint.ToDictionary(m => m, m => m.GetTexture("_Normal"));
            var originalCrinkle = paint.ToDictionary(m => m, m => m.GetTexture("_Normal_Crinkle"));
            var owned = new List<Object>();
            Texture flat = NavalMaterials.FlatNormal(owned);
            try
            {
                foreach (Material material in paint) { material.SetTexture("_Normal", flat); material.SetTexture("_Normal_Crinkle", flat); }
                captures.Add(Capture(position, target, Path.Combine(folder, "aft_04_flat_paint_normals.png")));
            }
            finally
            {
                foreach (Material material in paint) { material.SetTexture("_Normal", originalNormal[material]); material.SetTexture("_Normal_Crinkle", originalCrinkle[material]); }
                foreach (Object item in owned) Object.Destroy(item);
            }
            object candidateDetails = null;
            if (File.Exists(paintCandidate))
            {
                Material[] hull = paint.Where(m => m.name.StartsWith("Resolute_rsl_paint", StringComparison.Ordinal)).ToArray();
                var originals = hull.ToDictionary(m => m, m => m.GetTexture("_BaseColor"));
                var candidate = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                    { name = "Resolute hull paint review candidate", anisoLevel = 8, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat };
                try
                {
                    if (!ImageConversion.LoadImage(candidate, File.ReadAllBytes(paintCandidate), false)) throw new InvalidDataException("The paint candidate could not be loaded.");
                    candidate.Apply(true, true);
                    foreach (Renderer renderer in decorative.Where(r => r.name.StartsWith("rsl_flight_deck_safety_nets", StringComparison.Ordinal)))
                        renderer.shadowCastingMode = ShadowCastingMode.Off;
                    captures.Add(Capture(position, target, Path.Combine(folder, "aft_05_original_paint_nets_off.png")));
                    Vector3 bowTarget = visual.TransformPoint(new Vector3(0, 6, 0));
                    Vector3 bowCamera = visual.TransformPoint(new Vector3(195, 95, 235));
                    captures.Add(Capture(bowCamera, bowTarget, Path.Combine(folder, "bow_05_original_paint.png")));
                    foreach (Material material in hull) material.SetTexture("_BaseColor", candidate);
                    captures.Add(Capture(position, target, Path.Combine(folder, "aft_06_candidate_paint_nets_off.png")));
                    captures.Add(Capture(bowCamera, bowTarget, Path.Combine(folder, "bow_06_candidate_paint.png")));
                    candidateDetails = new Dictionary<string, object> { ["file"] = paintCandidate, ["width"] = candidate.width,
                        ["height"] = candidate.height, ["mipmapCount"] = candidate.mipmapCount, ["replacedMaterialCount"] = hull.Length,
                        ["scope"] = "Only intact grey-paint albedo is replaced temporarily. Original full-resolution normal and response textures, UVs, mesh and non-paint material slots are preserved." };
                }
                finally
                {
                    foreach (var item in originals) item.Key.SetTexture("_BaseColor", item.Value);
                    foreach (var item in shadows) if (item.Key != null) item.Key.shadowCastingMode = item.Value;
                    Object.Destroy(candidate);
                }
            }
            return new Dictionary<string, object> { ["scope"] = "Same camera, same frame and light. Baseline; decorative shadows off; all ship shadows off; restored shadows with both paint normals flattened. Diagnostic changes are restored immediately.",
                ["decorativeShadowCasters"] = decorative.Select(r => r.name).ToArray(), ["captures"] = captures, ["paintCandidate"] = candidateDetails };
        }

        internal static object Capture(Vector3 position, Vector3 target, string file, int cullingMask = ~0)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return new { skipped = "Null graphics device" };
            Camera camera = new GameObject("Resolute.DamageProofCamera").AddComponent<Camera>();
            camera.enabled = false; camera.nearClipPlane = .1f; camera.farClipPlane = 30000f; camera.fieldOfView = 45f;
            camera.allowHDR = true; camera.clearFlags = CameraClearFlags.Skybox; camera.cullingMask = cullingMask;
            camera.transform.position = position; camera.transform.LookAt(target, Vector3.up);
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true; data.volumeLayerMask = ~0;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing; data.antialiasingQuality = AntialiasingQuality.High;
            var texture = new RenderTexture(1600, 1000, 24, RenderTextureFormat.ARGB32);
            texture.Create(); camera.targetTexture = texture;
            RenderTexture previous = RenderTexture.active;
            try
            {
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = texture };
                if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new InvalidOperationException("Damage proof camera is unsupported.");
                RenderPipeline.SubmitRenderRequest(camera, request); RenderTexture.active = texture;
                var png = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                try { png.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); png.Apply(); File.WriteAllBytes(file, ImageConversion.EncodeToPNG(png)); }
                finally { Object.Destroy(png); }
            }
            finally { RenderTexture.active = previous; camera.targetTexture = null; texture.Release(); Object.Destroy(texture); Object.Destroy(camera.gameObject); }
            return new Dictionary<string, object> { ["file"] = file };
        }

        private static float Median(IEnumerable<float> values) { float[] sorted = values.OrderBy(v => v).ToArray(); return sorted[sorted.Length / 2]; }
        private static void Require(List<object> checks, string name, bool condition)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = condition });
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }
        private static void Save(string output, Dictionary<string, object> report) { File.WriteAllText(output, Audit.Json(report)); }
    }
}
