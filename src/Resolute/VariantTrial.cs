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
    internal static class VariantTrial
    {
        internal static IEnumerator Run(Ship original, Dictionary<string, object> report, List<object> checks, string output)
        {
            report["phase"] = "native-ship-variants";
            var details = new Dictionary<string, object> { ["success"] = false };
            report["variants"] = details;
            File.WriteAllText(output, Audit.Json(report));
            ShipDefinition definition = (ShipDefinition)Encyclopedia.Lookup[Plugin.Default3Key];
            GlobalPosition point;
            float minimumDepth;
            Require(checks, "default3-spawn-over-deep-water", FindClearWater(original, definition, out point, out minimumDepth));
            details["minimumFootprintDepthMetres"] = minimumDepth;
            point.y = Datum.SeaLevel.y + definition.spawnOffset.y;
            var saved = new SavedShip("resolute_default3_variant_trial")
            {
                type = Plugin.Default3Key, faction = original.NetworkHQ.faction.factionName,
                globalPosition = point, rotation = original.transform.rotation, skill = 1, holdPosition = true
            };
            Ship variant;
            Require(checks, "default3-native-saved-ship-spawn", NetworkSceneSingleton<Spawner>.i.TrySpawnShip(saved, out variant));
            try
            {
                variant.LinkSavedUnit(saved);
                MissionTrial.HoldControllers(variant);
                yield return null; yield return new WaitForFixedUpdate();
                Require(checks, "both-variants-coexist-in-mission", original != null && original.definition.jsonKey == Plugin.DefinitionKey &&
                    variant != original && variant.definition.jsonKey == Plugin.Default3Key &&
                    UnitRegistry.allUnits.Contains(original) && UnitRegistry.allUnits.Contains(variant) &&
                    original.persistentID != variant.persistentID);
                Require(checks, "default3-native-name-and-definition", variant.unitName == "Resolute Class Battlecruiser (Default-3)" &&
                    variant.definition == definition && variant.SavedUnit == saved && variant.rb != null && variant.LocalSim);
                var vls = variant.GetComponentsInChildren<ResoluteVlsAnimation>(true)
                    .GroupBy(a => a.Launcher.missile.jsonKey).ToDictionary(g => g.Key, g => g.Sum(a => a.Launcher.GetAmmoTotal()));
                Require(checks, "default3-live-authored-loadout", vls.Count == 7 && vls.Values.Sum() == 276 &&
                    vls["rsl_ashm"] == 48 && vls["rsl_cruise"] == 24 && vls["rsl_bastion"] == 16 &&
                    vls["rsl_bmd"] == 32 && vls["rsl_bmd_exo"] == 24 && vls["rsl_lrsam"] == 48 && vls["rsl_mrsam"] == 84);
                Require(checks, "default3-live-no-underwater-arms", variant.weaponStations.All(s => NaturalArmament.IsEnabledWeapon(s.WeaponInfo.name)));
                Require(checks, "default3-live-laser-no-ammo-count", variant.weaponStations.Where(s => EncyclopediaIntegration.IsHalo(s.WeaponInfo))
                    .Count(s => s.GetAmmoReadout() == string.Empty) == 2);
                Require(checks, "default3-live-helicopter-deck", variant.GetComponent<Airbase>() != null &&
                    variant.GetComponent<Airbase>().AttachedAirbase && variant.GetComponent<Airbase>().hangars.Count == 1);
                details["nativeVlsControllers"] = new[] {
                    NaturalFireControlAudit.Validate(original, checks, Plugin.DefinitionKey, true),
                    NaturalFireControlAudit.Validate(variant, checks, Plugin.Default3Key, true)
                };
                details["key"] = definition.jsonKey;
                details["name"] = variant.unitName;
                details["vlsRounds"] = vls;
                details["weaponStations"] = variant.weaponStations.Count;
                details["success"] = true;
                File.WriteAllText(output, Audit.Json(report));
            }
            finally
            {
                if (variant != null) Object.Destroy(variant.gameObject);
            }
            yield return null; yield return null;
        }

        private static bool FindClearWater(Ship original, ShipDefinition definition, out GlobalPosition point, out float minimumDepth)
        {
            // The first ship's sea lane is only certified for its route. A fixed
            // offset can put the second hull over a shelf, so inspect its whole
            // footprint before asking the normal native spawner to place it.
            Vector3[] occupied = UnitRegistry.allUnits.OfType<Ship>().Where(s => s != null).Select(s => s.GlobalPosition().AsVector3()).ToArray();
            for (float radius = 700f; radius <= 3500f; radius += 350f)
            for (int index = 0; index < 16; index++)
            {
                float angle = index * Mathf.PI / 8f;
                point = original.GlobalPosition() + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                Vector3 candidate = point.AsVector3();
                if (occupied.Any(p => Vector3.Distance(p, candidate) < 400f)) continue;
                minimumDepth = float.MaxValue;
                bool clear = true;
                for (int x = -1; x <= 1 && clear; x++)
                for (int z = -1; z <= 1; z++)
                {
                    GlobalPosition probe = point + original.transform.right * x * (definition.width * .5f + 10f) +
                        original.transform.forward * z * (definition.length * .5f + 25f);
                    if (!PathfindingAgent.RaycastTerrain(probe, out var hit)) { clear = false; break; }
                    minimumDepth = Mathf.Min(minimumDepth, Datum.LocalSeaY - hit.point.y);
                    if (minimumDepth < 40f) { clear = false; break; }
                }
                if (clear) return true;
            }
            point = default(GlobalPosition); minimumDepth = 0f; return false;
        }

        private static void Require(List<object> checks, string name, bool value)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = value });
            if (!value) throw new InvalidOperationException("Variant check failed: " + name);
        }
    }
}
