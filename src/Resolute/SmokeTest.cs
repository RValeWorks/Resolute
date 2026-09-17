using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Explicit local verification. This exercises resource loading, construction,
    // definition registration, and Mirage's prefab registry without opening a match.
    internal static class SmokeTest
    {
        private static Dictionary<string, object> nativeDonorBaseline;
        private static Dictionary<MissileDefinition, string> nativeMissileBaseline;

        internal static void CaptureNativeDonorBaseline(Encyclopedia encyclopedia)
        {
            if (nativeDonorBaseline != null) return;
            ShipDefinition donor = Plugin.FindDonor(encyclopedia);
            if (donor != null) nativeDonorBaseline = DonorSnapshot(donor);
            nativeMissileBaseline = encyclopedia.missiles.ToDictionary(m => m, MissileSnapshot);
        }

        private static string MissileSnapshot(MissileDefinition definition)
        {
            Missile missile = definition.unitPrefab.GetComponent<Missile>();
            MissileSeeker seeker = definition.unitPrefab.GetComponent<MissileSeeker>();
            WeaponInfo info = (WeaponInfo)AccessTools.Field(typeof(Missile), "info").GetValue(missile);
            return JsonUtility.ToJson(definition) + JsonUtility.ToJson(missile) + JsonUtility.ToJson(seeker) + JsonUtility.ToJson(info);
        }

        internal static IEnumerator Run(string[] args, ManualLogSource log)
        {
            string output = Audit.Argument(args, "--resolute-smoke-output");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
            {
                log.LogError("--resolute-smoke requires --resolute-smoke-output and an absolute output file path.");
                Application.Quit(2);
                yield break;
            }

            var checks = new List<object>();
            var report = new Dictionary<string, object>
            {
                ["smokeVersion"] = 1,
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["unityVersion"] = Application.unityVersion,
                ["gameVersion"] = Application.version,
                ["pluginSha256"] = Audit.AssemblySha256(),
                ["assetSha256"] = Audit.AssetSha256(),
                ["mode"] = "prefab-and-registration",
                ["checks"] = checks,
                ["matchTested"] = false,
                ["physicsTested"] = false,
                ["renderingTested"] = false,
                ["multiplayerSessionTested"] = false
            };

            IEnumerator operation = RunChecks(report, checks, log);
            int exitCode = 0;
            while (true)
            {
                bool next;
                try { next = operation.MoveNext(); }
                catch (Exception exception)
                {
                    report["error"] = exception.ToString();
                    log.LogError("Resolute smoke test failed: " + exception);
                    exitCode = 2;
                    break;
                }
                if (!next) break;
                yield return operation.Current;
            }
            report["success"] = exitCode == 0;
            try
            {
                string path = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Audit.Json(report), new UTF8Encoding(false));
                log.LogInfo("Resolute smoke result written: " + path);
            }
            catch (Exception exception)
            {
                log.LogError("Could not write smoke result: " + exception);
                exitCode = 2;
            }
            Application.Quit(exitCode);
        }

        private static IEnumerator RunChecks(Dictionary<string, object> report, List<object> checks, ManualLogSource log)
        {
            log.LogInfo("Resolute smoke: loading donor resource before registration.");
            ResourceRequest request = Resources.LoadAsync<Encyclopedia>("Encyclopedia");
            float start = Time.realtimeSinceStartup;
            while (!request.isDone && Time.realtimeSinceStartup - start < 120f) yield return null;
            Require(checks, "encyclopedia-resource", request.isDone && request.asset is Encyclopedia);
            var encyclopedia = (Encyclopedia)request.asset;
            Require(checks, "lobby-version-signature", Application.version.IndexOf(VersionCompatibilityPatch.Suffix, StringComparison.Ordinal) >= 0);
            ShipDefinition donor = Plugin.FindDonor(encyclopedia);
            Require(checks, "vanilla-donor-present", donor != null && donor.unitPrefab != null);
            GameObject donorPrefab = donor.unitPrefab;
            var donorBeforeData = DonorSnapshot(donor);
            string donorBefore = Fingerprint(donorBeforeData);
            report["donorKey"] = donor.jsonKey;
            report["donorResourceBeforeSha256"] = donorBefore;

            // Use the game's real callback: this runs the plugin's normal AfterLoad
            // postfix and fills all native lookup lists before any assertions.
            log.LogInfo("Resolute smoke: calling Encyclopedia.Preload.");
            var preload = Encyclopedia.Preload(CancellationToken.None);
            var awaiter = preload.GetAwaiter();
            start = Time.realtimeSinceStartup;
            while (!awaiter.IsCompleted && Time.realtimeSinceStartup - start < 120f) yield return null;
            Require(checks, "encyclopedia-preload-completed", awaiter.IsCompleted);
            awaiter.GetResult();
            Require(checks, "native-loader-instance", Encyclopedia.i == encyclopedia);
            // Vanilla WeaponMount.Initialize updates shared missile massPerRound
            // during AfterLoad. Compare every donor field from the point directly
            // before our factory executes, retaining those native cache values.
            Require(checks, "native-donor-baseline-captured", nativeDonorBaseline != null);
            donorBeforeData = nativeDonorBaseline;
            donorBefore = Fingerprint(donorBeforeData);
            report["donorBeforeSha256"] = donorBefore;

            UnitDefinition resolved;
            Require(checks, "mission-key-registered", Encyclopedia.Lookup != null &&
                Encyclopedia.Lookup.TryGetValue(Plugin.DefinitionKey, out resolved));
            resolved = Encyclopedia.Lookup[Plugin.DefinitionKey];
            ShipDefinition definition = resolved as ShipDefinition;
            Require(checks, "separate-ship-definition", definition != null && definition != donor);
            Require(checks, "native-ddg-category", definition.shipType == ShipType.DDG);
            Require(checks, "encyclopedia-visible", definition.IsAllowed(true) && encyclopedia.ships.Count(x => x == definition) == 1);
            Require(checks, "prefab-resolves-from-saved-key", encyclopedia.TryGetPrefab(Plugin.DefinitionKey, out var prefab) && prefab == definition.unitPrefab);
            Require(checks, "network-definition-index", ((INetworkDefinition)definition).LookupIndex.HasValue &&
                ReferenceEquals(encyclopedia.IndexLookup[((INetworkDefinition)definition).LookupIndex.Value], definition));
            Require(checks, "definition-size-and-mass", definition.length > 0 && definition.width > 0 && definition.height > 0 && definition.mass > 0);
            Require(checks, "separate-ship-info", donor.shipInfo == null || !ReferenceEquals(donor.shipInfo, definition.shipInfo));
            Require(checks, "new-prefab-instance", prefab != null && prefab != donorPrefab);
            ValidatePrefab(prefab, definition, donorPrefab, checks, "template");
            Require(checks, "vanilla-style-ship-name", definition.unitName == "Resolute Class Battlecruiser" && definition.code == "SHP");
            Require(checks, "eight-active-original-missile-definitions", NaturalWeapons.Definitions.Count == 8 && NaturalWeapons.Infos.Count == 8);
            Require(checks, "underwater-definitions-disabled", NaturalArmament.DisabledWeaponKeys.All(k =>
                !Encyclopedia.Lookup.ContainsKey(k) && !NaturalWeapons.Definitions.ContainsKey(k) && !NaturalWeapons.Infos.ContainsKey(k)));
            foreach (var pair in NaturalWeapons.Definitions)
            {
                var weapon = pair.Value;
                var info = NaturalWeapons.Infos[pair.Key];
                Require(checks, "original-weapon-" + pair.Key, Encyclopedia.Lookup[pair.Key] == weapon &&
                    encyclopedia.missiles.Count(m => m == weapon) == 1 && weapon.IsAllowed(false) &&
                    ((INetworkDefinition)weapon).LookupIndex.HasValue &&
                    ReferenceEquals(encyclopedia.IndexLookup[((INetworkDefinition)weapon).LookupIndex.Value], weapon) &&
                    info.weaponPrefab == weapon.unitPrefab && !nativeMissileBaseline.Keys.Any(m => m.unitPrefab == weapon.unitPrefab));
                if (pair.Key == "rsl_bmd" || pair.Key == "rsl_bmd_exo")
                    Require(checks, "original-weapon-" + pair.Key + "-valuable-ballistic-target-envelope",
                        weapon.roleIdentity.antiAir == 0f && weapon.roleIdentity.antiMissile > 0f &&
                        info.targetRequirements.minValue == 25f && info.targetRequirements.minAltitude == 0f &&
                        info.targetRequirements.maxAltitude >= 100000f);
            }
            Require(checks, "all-ship-weapons-own-info", prefab.GetComponentsInChildren<Weapon>(true)
                .All(w => NaturalWeapons.Infos.Values.Contains(w.info) || NaturalArmament.GunInfos.Values.Contains(w.info)));
            var variants = new List<object>();
            report["variants"] = variants;
            var variantHashes = new HashSet<int>();
            foreach (string key in Plugin.DefinitionKeys)
            {
                Require(checks, "variant-registered-" + key, Encyclopedia.Lookup.ContainsKey(key));
                ShipDefinition variant = (ShipDefinition)Encyclopedia.Lookup[key];
                GameObject variantPrefab = variant.unitPrefab;
                Require(checks, "variant-visible-once-" + key, variant.IsAllowed(false) && encyclopedia.ships.Count(s => s == variant) == 1);
                Require(checks, "variant-saved-key-resolves-" + key, encyclopedia.TryGetPrefab(key, out var loaded) && loaded == variantPrefab);
                Require(checks, "variant-unique-root-hash-" + key, variantHashes.Add(variantPrefab.GetComponent<NetworkIdentity>().PrefabHash));
                ValidatePrefab(variantPrefab, variant, donorPrefab, checks, key);
                MissileLauncher[] launchers = variantPrefab.GetComponentsInChildren<MissileLauncher>(true);
                Require(checks, "variant-no-underwater-launchers-" + key, launchers.All(l => NaturalArmament.IsEnabledWeapon(l.missile.jsonKey)));
                var rounds = variantPrefab.GetComponentsInChildren<ResoluteVlsAnimation>(true)
                    .GroupBy(a => a.Launcher.missile.jsonKey).ToDictionary(g => g.Key, g => g.Sum(a => a.Launcher.ammo));
                Require(checks, "variant-276-loaded-cells-" + key, rounds.Values.Sum() == 276);
                if (key == Plugin.Default3Key)
                    Require(checks, "default3-authored-vls-load", rounds["rsl_ashm"] == 48 && rounds["rsl_cruise"] == 24 &&
                        rounds["rsl_bmd"] == 32 && rounds["rsl_bmd_exo"] == 24 && rounds["rsl_bastion"] == 16 &&
                        rounds["rsl_lrsam"] == 48 && rounds["rsl_mrsam"] == 84);
                else if (Plugin.Outfit == "Default")
                    Require(checks, "default-asw-replaced-with-pike", rounds["rsl_ashm"] == 56 && rounds["rsl_cruise"] == 32 &&
                        rounds["rsl_bmd"] == 16 && rounds["rsl_bmd_exo"] == 8 && rounds["rsl_bastion"] == 16 &&
                        rounds["rsl_lrsam"] == 64 && rounds["rsl_mrsam"] == 84);
                Require(checks, "halo-energy-no-ammunition-readout-" + key,
                    variantPrefab.GetComponentsInChildren<Laser>(true).All(l => l.info.energy && l.info.massPerRound == 0 && l.info.costPerRound == 0));
                object nativeRouting = NaturalFireControlAudit.Validate(variantPrefab.GetComponent<Ship>(), checks, key, false);
                variants.Add(new Dictionary<string, object> { ["key"] = key, ["name"] = variant.unitName,
                    ["prefabHash"] = variantPrefab.GetComponent<NetworkIdentity>().PrefabHash, ["vlsRounds"] = rounds, ["nativeVlsControllers"] = nativeRouting });
            }
            var fractureDefinitions = new List<object>();
            bool fractureConsistent = true;
            int previousSectionCount = -1;
            foreach (string key in Plugin.DefinitionKeys)
            {
                var sections = ((ShipDefinition)Encyclopedia.Lookup[key]).unitPrefab.GetComponentsInChildren<ResoluteStructuralSection>(true);
                string cacheSource = sections.Length > 0 ? sections[0].CacheSource : null;
                int regions = sections.Sum(s => s.SurfaceMeshes != null ? s.SurfaceMeshes.Length : 0);
                int collisionGroups = sections.Sum(s => s.GroupColliders != null ? s.GroupColliders.Length : 0);
                bool consistent = sections.Length > 0 && new[] { "disk", "memory", "generated" }.Contains(cacheSource) &&
                    sections.All(s => s.CacheSource == cacheSource && s.SurfaceMeshes != null && s.SolidMeshes != null &&
                        s.SurfaceMeshes.Length == s.SolidMeshes.Length && s.GroupColliders != null && s.GroupColliders.Length > 0) &&
                    regions == StructuralGeometry.LastRegionCount && collisionGroups == StructuralGeometry.LastCollisionGroupCount &&
                    (previousSectionCount < 0 || sections.Length == previousSectionCount);
                previousSectionCount = sections.Length;
                fractureConsistent &= consistent;
                fractureDefinitions.Add(new Dictionary<string, object> {
                    ["key"] = key, ["cacheSource"] = cacheSource, ["sectionCount"] = sections.Length,
                    ["regionCount"] = regions, ["collisionGroupCount"] = collisionGroups,
                    ["sections"] = sections.Select(s => new Dictionary<string, object> {
                        ["name"] = s.name, ["cacheSource"] = s.CacheSource,
                        ["regionCount"] = s.SurfaceMeshes != null ? s.SurfaceMeshes.Length : 0,
                        ["collisionGroupCount"] = s.GroupColliders != null ? s.GroupColliders.Length : 0
                    }).ToArray()
                });
            }
            report["fractureCache"] = new Dictionary<string, object> {
                ["bakeVersion"] = StructuralGeometry.CurrentBakeVersion, ["lastCacheSource"] = StructuralGeometry.LastCacheSource,
                ["lastGeometryMilliseconds"] = StructuralGeometry.LastGeometryMilliseconds,
                ["lastRegionCount"] = StructuralGeometry.LastRegionCount, ["lastCollisionGroupCount"] = StructuralGeometry.LastCollisionGroupCount,
                ["definitions"] = fractureDefinitions
            };
            Require(checks, "cached-fracture-source-consistency", fractureConsistent);
            Require(checks, "native-missile-assets-unchanged", nativeMissileBaseline.All(p => MissileSnapshot(p.Key) == p.Value));

            int indexCount = encyclopedia.IndexLookup.Count;
            int shipCount = encyclopedia.ships.Count;
            int? definitionIndex = ((INetworkDefinition)definition).LookupIndex;
            Plugin.Instance.EnsureRegistered(encyclopedia);
            Plugin.Instance.EnsureRegistered(encyclopedia);
            Require(checks, "registration-is-idempotent", encyclopedia.IndexLookup.Count == indexCount &&
                encyclopedia.ships.Count == shipCount && ((INetworkDefinition)definition).LookupIndex == definitionIndex &&
                Encyclopedia.Lookup[Plugin.DefinitionKey] == definition);

            // Mirage's normal registry has no network/socket requirement. Verify the
            // exact API the game's ClientStarted uses, including harmless repeat calls.
            var staging = new GameObject("Resolute.SmokeStaging");
            staging.SetActive(false);
            Object.DontDestroyOnLoad(staging);
            var manager = staging.AddComponent<ClientObjectManager>();
            NetworkIdentity identity = prefab.GetComponent<NetworkIdentity>();
            manager.RegisterPrefab(identity);
            manager.RegisterPrefab(identity);
            Require(checks, "mirage-prefab-registration", manager.GetSpawnHandler(identity.PrefabHash).Prefab == identity);
            manager.UnregisterPrefab(identity);

            GameObject clone = Object.Instantiate(prefab, staging.transform);
            ValidatePrefab(clone, definition, donorPrefab, checks, "inactive-copy");
            List<string> templateReferences = FindHierarchyReferences(clone, prefab.transform);
            Require(checks, "copy-remaps-component-references", templateReferences.Count == 0, templateReferences);
            Require(checks, "copy-preserves-network-prefab-hash", clone.GetComponent<NetworkIdentity>().PrefabHash == identity.PrefabHash);

            var donorAfterData = DonorSnapshot(donor);
            string donorAfter = Fingerprint(donorAfterData);
            report["donorAfterSha256"] = donorAfter;
            if (donorBefore != donorAfter)
                report["donorMutationDetails"] = new Dictionary<string, object>
                {
                    ["before"] = donorBeforeData, ["after"] = donorAfterData
                };
            Require(checks, "donor-assets-unchanged", donorBefore == donorAfter && donor.unitPrefab == donorPrefab);
            report["definition"] = Audit.Fields(definition, prefab.transform, 0);
            report["prefab"] = Audit.Hierarchy(prefab);
            report["summary"] = new Dictionary<string, object>
            {
                ["shipParts"] = prefab.GetComponentsInChildren<ShipPart>(true).Length,
                ["turrets"] = prefab.GetComponentsInChildren<Turret>(true).Length,
                ["guns"] = prefab.GetComponentsInChildren<Gun>(true).Length,
                ["weapons"] = prefab.GetComponentsInChildren<Weapon>(true).Length,
                ["renderers"] = prefab.GetComponentsInChildren<Renderer>(true).Length,
                ["colliders"] = prefab.GetComponentsInChildren<Collider>(true).Length,
                ["prefabHash"] = identity.PrefabHash,
                ["lookupIndex"] = definitionIndex
            };
            Object.Destroy(staging);
            log.LogInfo("Resolute smoke: all " + checks.Count + " prefab and registration checks passed.");
        }

        private static void ValidatePrefab(GameObject prefab, ShipDefinition definition, GameObject donor,
            List<object> checks, string label)
        {
            Ship ship = prefab.GetComponent<Ship>();
            Require(checks, label + ":ship-definition-link", ship != null && ship.definition == definition);
            Require(checks, label + ":inactive-template-active-self", prefab.activeSelf && !prefab.activeInHierarchy);
            Require(checks, label + ":rigidbody", prefab.GetComponentInChildren<Rigidbody>(true) != null);
            NetworkIdentity[] identities = prefab.GetComponentsInChildren<NetworkIdentity>(true);
            Require(checks, label + ":root-network-identity", prefab.GetComponent<NetworkIdentity>() != null);
            Require(checks, label + ":unique-nonzero-identities", identities.All(i => i.PrefabHash != 0 && i.SceneId == 0) &&
                identities.Select(i => i.PrefabHash).Distinct().Count() == identities.Length &&
                identities.All(i => !(bool)AccessTools.Field(typeof(NetworkIdentity), "_hasSpawned").GetValue(i)));
            Require(checks, label + ":separate-donor-network-hash", prefab.GetComponent<NetworkIdentity>().PrefabHash != donor.GetComponent<NetworkIdentity>().PrefabHash);

            ShipPart[] parts = prefab.GetComponentsInChildren<ShipPart>(true);
            Require(checks, label + ":buoyancy-parts", parts.Length > 0 && parts.All(part =>
                part.GetComponent<Collider>() != null && part.parentUnit == ship && part.mass > 0));
            Require(checks, label + ":critical-part-references", ship.criticalParts != null &&
                ship.criticalParts.All(part => part != null && (part.transform == prefab.transform || part.transform.IsChildOf(prefab.transform))));
            var badReferences = FindHierarchyReferences(prefab, donor.transform);
            Require(checks, label + ":no-donor-hierarchy-references", badReferences.Count == 0, badReferences);
            Require(checks, label + ":no-missing-scripts", prefab.GetComponentsInChildren<Transform>(true)
                .All(transform => transform.GetComponents<Component>().All(component => component != null)));

            Weapon[] weapons = prefab.GetComponentsInChildren<Weapon>(true);
            Require(checks, label + ":weapon-info", weapons.Length > 0 && weapons.All(weapon => weapon.info != null));
            Turret[] turrets = prefab.GetComponentsInChildren<Turret>(true);
            Require(checks, label + ":turret-stations", turrets.Length > 0 && turrets.All(turret =>
                turret.GetWeaponStations() != null && turret.GetWeaponStations().Length > 0 &&
                turret.GetWeaponStations().All(station => station != null && station.Weapons != null && station.Weapons.Count > 0 &&
                    station.Weapons.All(weapon => weapon != null && weapon.info != null && weapon.transform.IsChildOf(prefab.transform)))));
            Require(checks, label + ":turret-pivots", turrets.All(turret =>
            {
                var pivot = (Transform)AccessTools.Field(typeof(Turret), "elevationTransform").GetValue(turret);
                if (pivot == null)
                    pivot = turret.GetWeaponStations()[0].Weapons[0].transform;
                return pivot != null && (pivot == prefab.transform || pivot.IsChildOf(prefab.transform));
            }));
            Require(checks, label + ":gun-muzzles", prefab.GetComponentsInChildren<Gun>(true).All(gun =>
            {
                var muzzles = (Transform[])AccessTools.Field(typeof(Gun), "muzzles").GetValue(gun);
                return muzzles != null && muzzles.Length > 0 && muzzles.All(muzzle => muzzle != null && muzzle.IsChildOf(prefab.transform));
            }));

            Renderer[] visible = prefab.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer.enabled && (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)).ToArray();
            Require(checks, label + ":visible-meshes", visible.Length > 0);
            Require(checks, label + ":visual-materials", visible.All(renderer => renderer.sharedMaterials.Length > 0 &&
                renderer.sharedMaterials.All(material => material != null && material.shader != null)));
            Require(checks, label + ":mesh-data", visible.All(renderer =>
            {
                var skin = renderer as SkinnedMeshRenderer;
                Mesh mesh = skin != null ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                return mesh != null && mesh.vertexCount > 0;
            }));
        }

        private static List<string> FindHierarchyReferences(GameObject inspected, Transform forbidden)
        {
            var matches = new List<string>();
            foreach (Component component in inspected.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                ScanFields(component, component.GetType().Name + "@" + component.name, forbidden, matches, 0);
            }
            return matches;
        }

        private static void ScanFields(object value, string path, Transform forbidden, List<string> matches, int depth)
        {
            if (value == null || depth > 7) return;
            Type type = value.GetType();
            while (type != null && type != typeof(Object) && type != typeof(Component) && type != typeof(Behaviour) && type != typeof(MonoBehaviour))
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsNotSerialized || (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField)))) continue;
                    ScanValue(field.GetValue(value), path + "." + field.Name, forbidden, matches, depth + 1);
                }
                type = type.BaseType;
            }
        }

        private static void ScanValue(object value, string path, Transform forbidden, List<string> matches, int depth)
        {
            if (value == null || depth > 7) return;
            Object unity = value as Object;
            if (value is Object)
            {
                if (unity == null) return;
                Component component = unity as Component;
                GameObject gameObject = unity as GameObject;
                Transform transform = component != null ? component.transform : gameObject != null ? gameObject.transform : null;
                if (transform != null && (transform == forbidden || transform.IsChildOf(forbidden))) matches.Add(path + " -> " + unity.name);
                return;
            }
            if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                int index = 0;
                foreach (object item in sequence) ScanValue(item, path + "[" + index++ + "]", forbidden, matches, depth + 1);
                return;
            }
            ScanFields(value, path, forbidden, matches, depth);
        }

        private static Dictionary<string, object> DonorSnapshot(ShipDefinition donor)
        {
            var definition = Audit.Fields(donor, donor.unitPrefab.transform, 0);
            // Vanilla AfterLoad recalculates this one cache itself.
            definition.Remove("mass");
            return new Dictionary<string, object>
            {
                ["definition"] = definition,
                ["prefab"] = Audit.Hierarchy(donor.unitPrefab),
                ["weaponInfo"] = donor.unitPrefab.GetComponentsInChildren<Weapon>(true)
                    .Where(weapon => weapon.info != null).Select(weapon => weapon.info).Distinct()
                    .OrderBy(info => info.name, StringComparer.Ordinal).Select(info => Audit.Fields(info, null, 0)).ToArray()
            };
        }

        private static string Fingerprint(Dictionary<string, object> values)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Audit.Json(values)))).Replace("-", "").ToLowerInvariant();
        }

        private static void Require(List<object> checks, string name, bool passed, object detail = null)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            if (!passed) throw new InvalidOperationException("Assertion failed: " + name + (detail == null ? "" : " " + Audit.Json(detail)));
        }
    }
}
