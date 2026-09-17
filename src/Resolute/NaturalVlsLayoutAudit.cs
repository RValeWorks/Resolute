using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Resolute
{
    // Diagnostic only: derive expected banks from authored cells instead of a
    // fixed count. Presets with five partitions in the large bank have 11, not10.
    internal static class NaturalVlsLayoutAudit
    {
        private static readonly string[][] Compatibility = {
            new[] { "WeaponSystem1" }, new[] { "WeaponSystem28" },
            new[] { "WeaponSystem3", "WeaponSystem5" },
            new[] { "WeaponSystem2", "WeaponSystem4", "WeaponSystem6", "WeaponSystem27" },
            new[] { "WeaponSystem7" }, new[] { "WeaponSystem8" }
        };

        internal static bool Validate(Ship ship, WeaponStation[] stations)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets");
            string key = ship.definition.jsonKey == Plugin.Default3Key ? "RSL_Default3" : Plugin.Outfit;
            var catalog = JObject.Parse(File.ReadAllText(Path.Combine(assets, "Weapons", "weapons.json")));
            var anchors = JObject.Parse(File.ReadAllText(Path.Combine(assets, "weapon_anchors.json")));
            var systems = catalog["outfits"].Single(o => (string)o["key"] == key)["systems"].ToDictionary(s => (string)s["key"]);
            var owner = new Dictionary<string, string>();
            foreach (var system in systems)
                for (int i = 1; i <= int.Parse((string)system.Value["NumberOfContainers"], System.Globalization.CultureInfo.InvariantCulture); i++)
                    owner.Add((string)system.Value["Container" + i + "_Hatch1"], system.Key);
            var cells = anchors["banks"].SelectMany(b => b["cells"]).ToArray();
            if (owner.Count != 276 || cells.Length != 276 || !new HashSet<string>(owner.Keys).SetEquals(cells.Select(c => (string)c["hatch"]))) return false;
            var expected = Compatibility.SelectMany(bank => cells.Where(c => bank.Contains((string)c["sourceWeapon"]))
                .GroupBy(c => owner[(string)c["hatch"]]).Select(group => new {
                    Weapon = NaturalArmament.ActiveVlsWeapon((string)systems[group.Key]["Ammunition"]),
                    Hatches = group.Select(c => (string)c["hatch"]).ToArray()
                })).ToArray();
            var animations = ship.GetComponentsInChildren<ResoluteVlsAnimation>(true);
            if (animations.Length != expected.Length || stations.Length != expected.Length) return false;
            var seen = new HashSet<Transform>();
            foreach (var animation in animations)
            {
                var launcher = animation.Launcher;
                if (launcher == null || animation.Hatches == null || animation.Hatches.Any(h => h == null || !seen.Add(h))) return false;
                string[] names = animation.Hatches.Select(h => h.name).ToArray();
                var matches = expected.Where(e => e.Hatches.SequenceEqual(names)).ToArray();
                if (matches.Length != 1 || launcher.missile != NaturalWeapons.Definitions[matches[0].Weapon] ||
                    launcher.info != NaturalWeapons.Infos[matches[0].Weapon] ||
                    stations.Count(s => s.Weapons.Count == 1 && s.Weapons[0] == launcher && s.WeaponInfo == launcher.info) != 1) return false;
                var points = (Transform[])NaturalWeapons.Get(launcher, "launchTransforms");
                if (points == null || !points.Select(p => p != null ? p.name : "").SequenceEqual(names.Select(n => "ResoluteLaunch_" + n))) return false;
            }
            return seen.Count == 276;
        }
    }
}
