using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearOption.SavedMission;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit in-game comparison of the affected surfaces and working mounts.
    // Native materials and geometry are rendered as installed, without recoloring
    // the reference, altering damage, or replacing the gameplay camera lighting.
    internal static class SurfaceStyleTrial
    {
        internal static IEnumerator Run(Ship original, Dictionary<string, object> report, List<object> checks, string output)
        {
            string folder = Path.Combine(Path.GetDirectoryName(output), "game_previews", "style_v5");
            Directory.CreateDirectory(folder);
            report["phase"] = "native-deck-and-weapon-style";
            report["nativePaintStyleAudit"] = SurfacePaintStyle.LastAudit;
            report["nativePlatingMaterialAudit"] = NavalMaterials.NativePlatingAudit;
            report["nativeDeckMaterialAudit"] = NavalMaterials.NativeDeckAudit;
            var captures = new List<object>(); report["surfaceStyleCaptures"] = captures;
            Require(checks, "style-v5-painted-surfaces-and-authoritative-geometry", SurfacePaintStyle.HasValidAudit);
            var deckAudit = NavalMaterials.NativeDeckAudit as Dictionary<string, object>;
            Require(checks, "style-v5-native-deck-material", deckAudit != null &&
                Convert.ToBoolean(deckAudit["allNativeMapReferencesPreserved"]) && Convert.ToBoolean(deckAudit["nativeDamageTilingPreserved"]));

            Ship native = null;
            try
            {
                var definition = (ShipDefinition)Encyclopedia.Lookup["Destroyer1"];
                GlobalPosition position = DamageVisualTrial.FindPoint(original);
                position.y = Datum.SeaLevel.y + definition.spawnOffset.y;
                var saved = new SavedShip("resolute_style_native") { type = "Destroyer1",
                    faction = original.NetworkHQ.faction.factionName, globalPosition = position,
                    rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(original.transform.forward, Vector3.up)),
                    holdPosition = true, skill = 1f };
                Require(checks, "style-v5-native-reference-spawn", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out native));
                native.LinkSavedUnit(saved); MissionTrial.HoldControllers(native); native.SetHoldPosition(true);
                foreach (Ship ship in new[] { native, original }) foreach (LODGroup group in ship.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
                yield return new WaitForSeconds(3f);
                foreach (Ship ship in new[] { native, original })
                {
                    string label = ship == native ? "dynamo" : "resolute";
                    Vector3 deck = DeckPoint(ship);
                    Shot(ship, label, "foredeck", deck, new Vector3(.6f, .9f, .65f).normalized * 70f, folder, captures);
                    Shot(ship, label, "deck_detail", deck, new Vector3(.15f, 1f, .25f).normalized * 16f, folder, captures);
                    Turret railgun = ship.GetComponentsInChildren<Turret>(true).Single(t => t.name == "turret_F");
                    Turret ciws = ship.GetComponentsInChildren<Turret>(true).Single(t => t.name == "CIWS_FR");
                    Shot(ship, label, "railgun", ArtBounds(railgun.transform).center, new Vector3(.85f, .45f, .65f).normalized * 30f, folder, captures);
                    Shot(ship, label, "ciws", ArtBounds(ciws.transform).center, new Vector3(.9f, .55f, .6f).normalized * 13f, folder, captures);
                    var materials = new Dictionary<string, object>(); report[label + "WeaponStyleMaterials"] = materials;
                    foreach (Turret turret in new[] { railgun, ciws }) materials[turret.name] = turret.GetComponentsInChildren<MeshRenderer>(true)
                        .Where(r => r.enabled && r.gameObject.activeInHierarchy).Select(r => new Dictionary<string, object> {
                            ["renderer"] = r.name, ["materials"] = r.sharedMaterials.Select(m => m.name).ToArray() }).ToArray();
                }
                Transform source = original.transform.Find("ResoluteVisual");
                foreach (float side in new[] { -1f, 1f })
                {
                    Vector3 point = source.TransformPoint(new Vector3(14.5f * side, 6.7f, 12f));
                    Shot(original, "resolute", side < 0 ? "midship_port" : "midship_starboard", point,
                        new Vector3(21f * side, 3f, 6f), folder, captures);
                }
                Require(checks, "style-v5-ten-comparison-captures", captures.Count == 10);
                Require(checks, "style-v5-no-decorative-side-box", !original.GetComponentsInChildren<MeshRenderer>(true)
                    .Any(r => r.enabled && r.gameObject.activeInHierarchy && r.name == "rsl_side_panels"));
                File.WriteAllText(output, Audit.Json(report));
            }
            finally
            {
                foreach (LODGroup group in original.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(-1);
                if (native != null) Object.Destroy(native.gameObject);
            }
            yield return null;
        }

        private static Bounds ArtBounds(Transform root)
        {
            MeshRenderer[] visible = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled &&
                r.gameObject.activeInHierarchy && r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null).ToArray();
            if (visible.Length == 0) throw new InvalidOperationException("Weapon has no visible model: " + root.name);
            Bounds bounds = visible[0].bounds;
            foreach (MeshRenderer renderer in visible.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static Vector3 DeckPoint(Ship ship)
        {
            Vector3 desired = ship.transform.TransformPoint(new Vector3(0, 0, ship.definition.length * .25f));
            Vector3 nearest = Vector3.zero; float distance = float.PositiveInfinity;
            foreach (MeshRenderer renderer in ship.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>(); if (filter == null || filter.sharedMesh == null) continue;
                Mesh mesh = filter.sharedMesh; Material[] materials = renderer.sharedMaterials;
                if (!materials.Any(m => m != null && (m.name == "destroyer1_deck" || m == NavalMaterials.NativeDeck))) continue;
                Vector3[] vertices = mesh.vertices;
                for (int slot = 0; slot < Mathf.Min(materials.Length, mesh.subMeshCount); slot++)
                {
                    if (materials[slot].name != "destroyer1_deck" && materials[slot] != NavalMaterials.NativeDeck) continue;
                    int[] triangles = mesh.GetTriangles(slot);
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        Vector3 a = renderer.transform.TransformPoint(vertices[triangles[i]]),
                            b = renderer.transform.TransformPoint(vertices[triangles[i + 1]]), c = renderer.transform.TransformPoint(vertices[triangles[i + 2]]);
                        Vector3 normal = Vector3.Cross(b - a, c - a);
                        if (normal.y <= normal.magnitude * .75f || normal.sqrMagnitude < .000001f) continue;
                        Vector3 center = (a + b + c) / 3f;
                        float next = Vector3.ProjectOnPlane(center - desired, Vector3.up).sqrMagnitude;
                        if (next < distance) { nearest = center; distance = next; }
                    }
                }
            }
            if (float.IsInfinity(distance)) throw new InvalidOperationException("Native deck reference has no upward surface: " + ship.name);
            return nearest;
        }

        private static void Shot(Ship ship, string label, string view, Vector3 target, Vector3 offset,
            string folder, List<object> captures)
        {
            string file = Path.Combine(folder, label + "_" + view + ".png");
            Vector3 position = target + ship.transform.TransformDirection(offset);
            DamageVisualTrial.Capture(position, target, file);
            if (!File.Exists(file) || new FileInfo(file).Length < 10000) throw new InvalidOperationException("Surface capture missing: " + file);
            captures.Add(new Dictionary<string, object> { ["ship"] = label, ["view"] = view, ["file"] = file,
                ["cameraDistanceM"] = offset.magnitude, ["cameraPosition"] = new[] { position.x, position.y, position.z },
                ["target"] = new[] { target.x, target.y, target.z }, ["simulationFrame"] = Time.frameCount });
        }

        private static void Require(List<object> checks, string name, bool value)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = value });
            if (!value) throw new InvalidOperationException("Assertion failed: " + name);
        }
    }
}
