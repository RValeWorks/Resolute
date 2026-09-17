using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NuclearOption.SavedMission;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit copied-game diagnostic for actual native attachment behavior.
    // Combat damage remains covered by DamageVisualTrial's real missile attacks.
    internal static class StructureReviewTrial
    {
        internal static IEnumerator Run(Ship original, Dictionary<string, object> report, List<object> checks, string output)
        {
            string folder = Path.Combine(Path.GetDirectoryName(output), "game_previews", "structure_v5");
            Directory.CreateDirectory(folder);
            var data = new Dictionary<string, object>(); report["structureReview"] = data;
            var captures = new List<object>(); data["captures"] = captures;
            data["scope"] = "Disposable ship, native partial ApplyDamage and native ShipPart.Detach exercise. This is a controlled attachment/material inspection, not a weapon-yield or combat-damage benchmark. A separately labelled cutaway hides one exterior renderer for the same-frame interior view and restores it immediately.";
            Ship ship = null;
            try
            {
                GlobalPosition point = DamageVisualTrial.FindPoint(original);
                point.y = Datum.SeaLevel.y + original.definition.spawnOffset.y;
                var saved = new SavedShip("structure_v5") { type = original.definition.jsonKey,
                    faction = original.NetworkHQ.faction.factionName, globalPosition = point,
                    rotation = original.transform.rotation, skill = 1f, holdPosition = true };
                Check(checks, "structure-v5-spawn", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out ship));
                ship.LinkSavedUnit(saved); MissionTrial.HoldControllers(ship); ship.SetHoldPosition(true);
                yield return null; yield return new WaitForFixedUpdate();
                Transform frame = ship.transform.Find("ResoluteVisual");
                LODGroup detail = frame.GetComponent<LODGroup>();
                detail.ForceLOD(0);
                yield return null;
                UnitPart[] parts = ship.partLookup.Where(p => p != null).ToArray();
                ResoluteStructuralSection[] sections = parts.Select(p => p.GetComponent<ResoluteStructuralSection>()).Where(s => s != null).ToArray();
                Renderer[] interiors = sections.SelectMany(s => s.InteriorFaces).ToArray();
                int triangles = interiors.Sum(r => r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3);
                data["interiorRenderers"] = interiors.Length; data["interiorTrianglesAfterNativePartition"] = triangles;
                data["interiorVisibleWhenHealthy"] = interiors.Count(r => r.enabled && r.gameObject.activeInHierarchy);
                Check(checks, "structure-v5-native-compartment-count", sections.Length == StructuralGeometry.ExpectedCompartmentCount);
                Check(checks, "structure-v5-real-interiors-present-before-damage", interiors.Length >= 17 && triangles > 9000 && interiors.All(r => r.enabled));
                Check(checks, "structure-v5-side-panels-absent", !ship.GetComponentsInChildren<Transform>(true).Any(t => t.name.Contains("rsl_side_panels")));
                CheckMastAndInteriors(frame, sections, data, checks);
                captures.Add(Capture(frame, new Vector3(50, 40, -34), new Vector3(0, 20, 5), folder, "024_mast_funnel_intact_starboard"));
                captures.Add(Capture(frame, new Vector3(-50, 40, -34), new Vector3(0, 20, 5), folder, "024_mast_funnel_intact_port"));

                Transform[] anchors = ship.GetComponentsInChildren<Transform>(true).Where(t =>
                    t.name.StartsWith("ResoluteLaunch_", StringComparison.Ordinal)).ToArray();
                var mounting = new List<object>(); data["cellMounting"] = mounting;
                var hatchNames = ship.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
                foreach (Transform anchor in anchors)
                {
                    string hatchName = anchor.name.Substring("ResoluteLaunch_".Length);
                    Transform hatch = hatchNames[hatchName];
                    ShipPart launchOwner = anchor.GetComponentInParent<ShipPart>();
                    ShipPart hatchOwner = hatch.GetComponentInParent<ShipPart>();
                    mounting.Add(new Dictionary<string, object> { ["hatch"] = hatchName, ["anchorOwner"] = launchOwner.name,
                        ["hatchOwner"] = hatchOwner.name, ["sourcePosition"] = V(frame.InverseTransformPoint(anchor.position)) });
                    Check(checks, "structure-v5-cell-owner-" + hatchName, launchOwner == hatchOwner && launchOwner.name != "Hull_ExhaustStack");
                }
                Check(checks, "structure-v5-cell-count", anchors.Length == 276);
                foreach (string name in new[] { "CIWS_FR", "CIWS_FL", "Resolute_rsl_pd_fwd_yaw", "Resolute_rsl_pd_fl_yaw" })
                {
                    UnitPart weapon = parts.Single(p => p.name == name);
                    string owner = weapon.transform.parent.name;
                    Check(checks, "structure-v5-forward-weapon-deck-owner-" + name, owner == "Hull_FR" || owner == "Hull_FL");
                }

                captures.Add(Capture(frame, new Vector3(58, 43, 86), new Vector3(0, 10, 47), folder, "foredeck_intact"));
                for (int level = 0; level < detail.GetLODs().Length; level++)
                {
                    detail.ForceLOD(level); yield return null;
                    captures.Add(Capture(frame, new Vector3(57, 14, 13), new Vector3(14, 7, 12), folder, "regular_side_hull_lod" + level));
                }
                detail.ForceLOD(0); yield return null;
                ShipPart side = parts.OfType<ShipPart>().Single(p => p.name == "Hull_R");
                float before = side.hitPoints;
                side.ApplyDamage(0f, 40f, 0f, 0f);
                yield return null;
                data["partialDamageBefore"] = before; data["partialDamageAfter"] = side.hitPoints;
                Check(checks, "structure-v5-early-damage-native-event", side.hitPoints > 35f && side.hitPoints < before);
                Check(checks, "structure-v5-interior-present-at-early-damage", side.GetComponent<ResoluteStructuralSection>().InteriorFaces.All(r => r.enabled));
                captures.Add(Capture(frame, new Vector3(58, 19, 16), new Vector3(5, 2, 0), folder, "partial_damage_native_side"));
                Renderer shell = side.GetComponent<ResoluteStructuralSection>().Body;
                bool previous = shell.enabled;
                try
                {
                    shell.enabled = false;
                    captures.Add(Capture(frame, new Vector3(49, 15, 19), new Vector3(2, 1, 0), folder, "inspection_cutaway_inner_decks"));
                }
                finally { shell.enabled = previous; }

                ShipPart tower = parts.OfType<ShipPart>().Single(p => p.name == "Hull_ExhaustStack");
                Transform[] forwardCells = anchors.Where(a => frame.InverseTransformPoint(a.position).z > 32.4f).ToArray();
                var beforePositions = forwardCells.ToDictionary(a => a, a => ship.transform.InverseTransformPoint(a.position));
                var beforeChainPositions = forwardCells.ToDictionary(a => a, a => PositionInAncestor(a, ship.transform));
                var beforeParents = forwardCells.ToDictionary(a => a, a => a.parent);
                var beforeLocalPositions = forwardCells.ToDictionary(a => a, a => a.localPosition);
                data["detachmentShipWorldBefore"] = V(ship.transform.position);
                tower.Detach(ship.rb.velocity + ship.transform.right * 7f + Vector3.up * 4f, Vector3.zero);
                yield return null; yield return new WaitForFixedUpdate();
                data["detachmentShipWorldAfter"] = V(ship.transform.position);
                data["foredeckAfterDetach"] = forwardCells.Select(a =>
                {
                    ShipPart owner = a == null ? null : a.GetComponentInParent<ShipPart>();
                    return (object)new Dictionary<string, object> { ["name"] = a == null ? "destroyed" : a.name,
                        ["parent"] = a == null || a.parent == null ? null : a.parent.name,
                        ["parentUnchanged"] = a != null && a.parent == beforeParents[a],
                        ["parentChain"] = a == null ? null : Chain(a),
                        ["owner"] = owner == null ? null : owner.name,
                        ["ownerDetached"] = owner != null && owner.IsDetached(),
                        ["childOfShip"] = a != null && a.IsChildOf(ship.transform),
                        ["shipFrameDelta"] = a == null ? -1f : Vector3.Distance(ship.transform.InverseTransformPoint(a.position), beforePositions[a]),
                        ["localChainDelta"] = a == null || !a.IsChildOf(ship.transform) ? -1f : Vector3.Distance(PositionInAncestor(a, ship.transform), beforeChainPositions[a]),
                        ["localChainBefore"] = a == null ? null : V(beforeChainPositions[a]),
                        ["localChainAfter"] = a == null || !a.IsChildOf(ship.transform) ? null : V(PositionInAncestor(a, ship.transform)),
                        ["parentFrameDelta"] = a == null ? -1f : Vector3.Distance(a.localPosition, beforeLocalPositions[a]),
                        ["shipFrameBefore"] = a == null ? null : V(beforePositions[a]),
                        ["shipFrameAfter"] = a == null ? null : V(ship.transform.InverseTransformPoint(a.position)) };
                }).ToArray();
                Check(checks, "structure-v5-native-tower-detached", tower.IsDetached() && !tower.transform.IsChildOf(ship.transform));
                Check(checks, "structure-v5-foredeck-cells-stay-after-tower-detachment", forwardCells.All(a => a != null && a.IsChildOf(ship.transform) &&
                    a.parent == beforeParents[a] && !a.GetComponentInParent<ShipPart>().IsDetached() &&
                    Vector3.Distance(PositionInAncestor(a, ship.transform), beforeChainPositions[a]) < .002f));
                foreach (string name in new[] { "CIWS_FR", "CIWS_FL", "Resolute_rsl_pd_fwd_yaw", "Resolute_rsl_pd_fl_yaw" })
                {
                    UnitPart weapon = parts.Single(p => p.name == name);
                    Check(checks, "structure-v5-forward-weapon-stays-" + name, !weapon.IsDetached() && weapon.transform.IsChildOf(ship.transform));
                }
                data["towerDetached"] = tower.IsDetached(); data["healthyForwardCellsRetained"] = forwardCells.Length;
                data["fixedVlsControls"] = ship.GetComponentsInChildren<Turret>(true).Where(t => t.GetWeaponStations().Length > 0 &&
                    t.GetWeaponStations().SelectMany(s => s.Weapons).All(w => w.GetComponent<ResoluteVlsAnimation>() != null)).Select(t =>
                        (object)new Dictionary<string, object> { ["name"] = t.name, ["parent"] = t.transform.parent.name,
                            ["obsoleteCriticalParts"] = ((UnitPart[])typeof(Turret).GetField("criticalParts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(t)).Length }).ToArray();
                Check(checks, "structure-v5-shared-vls-not-bound-to-old-tower", ((object[])data["fixedVlsControls"]).Length > 0 && ((object[])data["fixedVlsControls"]).Cast<Dictionary<string, object>>().All(d =>
                    (string)d["parent"] == ship.name && Convert.ToInt32(d["obsoleteCriticalParts"]) == 0));
                foreach (string legacy in new[] { "SAM_F", "SAM_R" })
                {
                    Transform controller = ship.GetComponentsInChildren<Transform>(true).Single(t => t.name == legacy);
                    Check(checks, "structure-v5-obsolete-control-collision-disabled-" + legacy,
                        controller.GetComponentsInChildren<Collider>(true).All(c => !c.enabled));
                }
                captures.Add(Capture(frame, new Vector3(61, 45, 83), new Vector3(0, 10, 43), folder, "native_tower_detached_foredeck_retained"));
                yield return new WaitForSeconds(2f);
                captures.Add(Capture(frame, new Vector3(61, 45, 83), new Vector3(0, 10, 43), folder, "native_tower_detached_after_2s"));
                // Exercise each retained native tower owner separately. The
                // complete mast belongs to Radar; detaching the neighbouring
                // broad front bay must not carry half of that mast with it.
                ShipPart radar = parts.OfType<ShipPart>().Single(p => p.name == "Hull_Radar");
                ShipPart purple = parts.OfType<ShipPart>().Single(p => p.name == "Hull_UpperStarboard");
                ShipPart bridge = parts.OfType<ShipPart>().Single(p => p.name == "Hull_Bridge");
                Renderer mastBody = radar.GetComponent<ResoluteStructuralSection>().Body;
                radar.Detach(ship.rb.velocity + ship.transform.right * 3f, Vector3.zero);
                yield return null; yield return new WaitForFixedUpdate();
                Check(checks, "structure-024-whole-mast-detaches-with-radar", radar.IsDetached() &&
                    !radar.transform.IsChildOf(ship.transform) && mastBody != null && mastBody.transform.IsChildOf(radar.transform));
                Check(checks, "structure-024-front-bays-retained-after-radar-detach", !purple.IsDetached() && !bridge.IsDetached() &&
                    purple.transform.IsChildOf(ship.transform) && bridge.transform.IsChildOf(ship.transform));
                captures.Add(Capture(frame, new Vector3(47, 38, -28), new Vector3(0, 20, 6), folder, "024_native_radar_detached_starboard"));
                captures.Add(Capture(frame, new Vector3(-47, 38, -28), new Vector3(0, 20, 6), folder, "024_native_radar_detached_port"));
                purple.Detach(ship.rb.velocity - ship.transform.right * 3f, Vector3.zero);
                yield return null; yield return new WaitForFixedUpdate();
                Check(checks, "structure-024-front-bay-detaches-independently", purple.IsDetached() &&
                    !purple.transform.IsChildOf(ship.transform) && !bridge.IsDetached() && bridge.transform.IsChildOf(ship.transform));
                Check(checks, "structure-024-mast-owner-unchanged-after-front-bay-detach", mastBody != null && mastBody.transform.IsChildOf(radar.transform));
                captures.Add(Capture(frame, new Vector3(47, 38, -28), new Vector3(0, 20, 8), folder, "024_native_front_bay_detached_starboard"));
                captures.Add(Capture(frame, new Vector3(-47, 38, -28), new Vector3(0, 20, 8), folder, "024_native_front_bay_detached_port"));
                data["complete"] = true;
                File.WriteAllText(output, Audit.Json(report));
            }
            finally
            {
                if (ship != null)
                {
                    foreach (UnitPart part in ship.partLookup.ToArray())
                        if (part != null && part.IsDetached() && !part.transform.IsChildOf(ship.transform)) Object.Destroy(part.gameObject);
                    Object.Destroy(ship.gameObject);
                }
            }
        }

        private static object Capture(Transform frame, Vector3 camera, Vector3 target, string folder, string name)
        { return DamageVisualTrial.Capture(frame.TransformPoint(camera), frame.TransformPoint(target), Path.Combine(folder, name + ".png")); }
        private static void CheckMastAndInteriors(Transform frame, ResoluteStructuralSection[] sections,
            Dictionary<string, object> data, List<object> checks)
        {
            var mast = new Dictionary<string, int>();
            var interiors = new List<object>();
            foreach (ResoluteStructuralSection section in sections)
            {
                Mesh mesh = section.Body.GetComponent<MeshFilter>().sharedMesh;
                Matrix4x4 source = frame.worldToLocalMatrix * section.Body.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices.Select(source.MultiplyPoint3x4).ToArray();
                int[] indices = mesh.triangles;
                int upper = 0;
                for (int i = 0; i < indices.Length; i += 3)
                    if (Mathf.Max(vertices[indices[i]].y, vertices[indices[i + 1]].y, vertices[indices[i + 2]].y) > 24.8f) upper++;
                if (upper > 0) mast.Add(section.Part.name, upper);
                if (!new[] { "Hull_ExhaustStack", "Hull_Radar", "Hull_UpperStarboard", "Hull_Bridge" }.Contains(section.Part.name)) continue;
                int capTriangles = section.ClosedMesh.subMeshCount > 1 ? section.ClosedMesh.GetTriangles(1).Length / 3 : 0;
                float capArea = section.ClosedMesh.subMeshCount > 1 ? MeshArea(section.ClosedMesh, section.ClosedMesh.GetTriangles(1)) : 0f;
                float innerArea = section.InteriorFaces.Sum(r => MeshArea(r.GetComponent<MeshFilter>().sharedMesh,
                    r.GetComponent<MeshFilter>().sharedMesh.triangles));
                Material material = section.Interior;
                bool opaque = material != null && material.renderQueue <= 2500 &&
                    material.GetTag("RenderType", false, "Opaque") != "Transparent" &&
                    (!material.HasProperty("_Surface") || material.GetFloat("_Surface") == 0f) &&
                    (!material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") == 1f);
                interiors.Add(new Dictionary<string, object> { ["owner"] = section.Part.name, ["capTriangles"] = capTriangles,
                    ["capArea"] = capArea, ["innerArea"] = innerArea, ["opaque"] = opaque,
                    ["shader"] = material == null || material.shader == null ? null : material.shader.name,
                    ["scope"] = "Native baked cap geometry and visible authored inner steel; paired captures inspect the rendered cut faces." });
                Check(checks, "structure-024-real-cut-cap-area-" + section.Part.name, capTriangles > 0 && capArea > 1f);
                Check(checks, "structure-024-opaque-inner-steel-" + section.Part.name, innerArea > 1f && opaque &&
                    section.InteriorFaces.Length > 0 && section.InteriorFaces.All(r => r.enabled && r.gameObject.activeInHierarchy));
            }
            data["024MastExteriorTrianglesByNativeOwner"] = mast;
            data["024NativeTowerInteriors"] = interiors;
            Check(checks, "structure-024-all-real-upper-mast-triangles-owned-by-radar", mast.Count == 1 &&
                mast.TryGetValue("Hull_Radar", out int count) && count > 100);
        }
        private static float MeshArea(Mesh mesh, int[] indices)
        {
            Vector3[] vertices = mesh.vertices; double area = 0;
            for (int i = 0; i < indices.Length; i += 3)
                area += Vector3.Cross(vertices[indices[i + 1]] - vertices[indices[i]],
                    vertices[indices[i + 2]] - vertices[indices[i]]).magnitude * .5;
            return (float)area;
        }
        private static float[] V(Vector3 p) { return new[] { p.x, p.y, p.z }; }
        private static Vector3 PositionInAncestor(Transform part, Transform ancestor)
        {
            // The game can place ships tens of kilometres from world origin.
            // Compose their small local TRS values without subtracting two
            // rounded large world positions for this millimetre-scale check.
            Vector3 point = Vector3.zero;
            for (Transform current = part; current != ancestor; current = current.parent)
            {
                if (current == null) throw new InvalidOperationException("Transform is outside the expected ship hierarchy.");
                point = current.localPosition + current.localRotation * Vector3.Scale(current.localScale, point);
            }
            return point;
        }
        private static string Chain(Transform part)
        {
            var names = new List<string>();
            for (Transform current = part; current != null; current = current.parent) names.Add(current.name);
            return string.Join(" / ", names.ToArray());
        }
        private static void Check(List<object> checks, string name, bool pass)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = pass });
            if (!pass) throw new InvalidOperationException("Assertion failed: " + name);
        }
    }
}
