using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using NuclearOption.BuildScripts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    [BepInPlugin(Id, "Resolute", Version)]
    [BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.resolute.nuclearoption";
        public const string Version = "0.7.26";
        internal const string DefinitionKey = "rsl_resolute";
        internal const string Default3Key = "rsl_resolute_default3";
        internal static readonly string[] DefinitionKeys = { DefinitionKey, Default3Key };
        internal static bool IsResolute(UnitDefinition value) => value != null && DefinitionKeys.Contains(value.jsonKey);
        internal static Plugin Instance;
        internal static string Outfit = "Default";

        private Harmony harmony;
        private GameObject visualTemplate;
        private GameObject templateRoot;
        private readonly Dictionary<string, ShipDefinition> definitions = new Dictionary<string, ShipDefinition>();
        private bool auditMode;
        private bool smokeMode;
        private bool missionTrialMode;
        private bool encyclopediaTrialMode;
        private bool building;
        private string[] diagnosticArgs;
        private bool diagnosticStarted;
        private readonly StartupRegistrationGate startup = new StartupRegistrationGate();
        internal StartupLoadContext Loading { get; private set; }
        // Headless/command-line auto-host ordering must remain unchanged: those
        // modes may start a server immediately when the native menu is ready.
        internal bool SynchronousStartup => auditMode || smokeMode || missionTrialMode || encyclopediaTrialMode ||
            GameManager.IsHeadless || CommandLineArgParser.IsAutoStart || CommandLineArgParser.ForceSteamServerInit;

        internal void LogStartupWarning(string message) => Logger.LogWarning(message);

        private void Awake()
        {
            Instance = this;
            // Persist independently of other plugins: the ship template and its
            // runtime materials must survive menu/map scene transitions.
            transform.root.gameObject.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(transform.root.gameObject);
            string[] args = Environment.GetCommandLineArgs();
            ResoluteDiagnostics.Configure(Config.Bind("Diagnostics", "Enabled", false,
                "Enable bounded flight-event logs and missile observations for troubleshooting or the separate recorder. Disabled for normal play. Restart after changing.").Value, args);
            Outfit = Config.Bind("Armament", "Outfit", "Default",
                "Outfit for the base Resolute entry: Default, RSL_Default2, RSL_Default3, RSL_AirDefence, RSL_SurfaceStrike, RSL_LandStrike, RSL_ASW, rsl_loadout_bmd. A separate Default-3 ship is always available. Underwater weapons are disabled in all outfits. Restart after changing. All players need the same setting.").Value;
            auditMode = args.Any(x => string.Equals(x, "--resolute-audit", StringComparison.OrdinalIgnoreCase));
            smokeMode = args.Any(x => string.Equals(x, "--resolute-smoke", StringComparison.OrdinalIgnoreCase));
            missionTrialMode = args.Any(x => string.Equals(x, "--resolute-trial", StringComparison.OrdinalIgnoreCase));
            encyclopediaTrialMode = args.Any(x => string.Equals(x, "--resolute-encyclopedia", StringComparison.OrdinalIgnoreCase));
            if (auditMode)
            {
                Application.runInBackground = true;
                Audit.IsolateStartup(Logger);
                Audit.ScheduleSynchronous(args, Logger);
                diagnosticArgs = args;
                return;
            }

            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Resolute " + Version + " loaded; waiting for the ship encyclopedia.");
            if (smokeMode || missionTrialMode || encyclopediaTrialMode)
            {
                Application.runInBackground = true;
                Audit.IsolateStartup(Logger);
                diagnosticArgs = args;
                Audit.ScheduleCallback(StartDiagnostic);
            }
        }

        private void Start()
        {
            StartDiagnostic();
        }

        private void StartDiagnostic()
        {
            if (diagnosticStarted || diagnosticArgs == null) return;
            diagnosticStarted = true;
            if (auditMode && diagnosticArgs != null)
            {
                Logger.LogInfo("Resolute audit starting on frame " + Time.frameCount + ".");
                Audit.RunSynchronously(diagnosticArgs, Logger);
            }
            else if (smokeMode && diagnosticArgs != null)
            {
                Logger.LogInfo("Resolute smoke test starting on frame " + Time.frameCount + ".");
                StartCoroutine(SmokeTest.Run(diagnosticArgs, Logger));
            }
            else if (missionTrialMode && diagnosticArgs != null)
            {
                Logger.LogInfo("Resolute mission trial starting on frame " + Time.frameCount + ".");
                StartCoroutine(MissionTrial.Run(diagnosticArgs, Logger));
            }
            else if (encyclopediaTrialMode && diagnosticArgs != null)
            {
                Logger.LogInfo("Resolute encyclopedia trial starting on frame " + Time.frameCount + ".");
                StartCoroutine(EncyclopediaTrial.Run(diagnosticArgs, Logger));
            }
        }

        // Existing isolated diagnostics deliberately run a synchronous adapter.
        // Browser callbacks only reassert already-built entries; they never
        // surprise the player with another prefab build on a button click.
        internal void EnsureRegistered(Encyclopedia encyclopedia)
        {
            if (auditMode || building || encyclopedia == null) return;
            try
            {
                if (!SynchronousStartup) { ReassertRegistration(encyclopedia); return; }
                PrepareRegistrationAsync(encyclopedia, null).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                if (Loading != null) Loading.Fail(error);
                else Logger.LogError("Resolute could not be registered. " + error);
            }
        }

        internal async UniTask EnsureRegisteredAsync(Encyclopedia encyclopedia, CancellationToken cancellation)
        {
            if (auditMode || encyclopedia == null) return;
            if (SynchronousStartup) { EnsureRegistered(encyclopedia); return; }
            try
            {
                await startup.Ensure(() => {
                    Loading = new StartupLoadContext(destroyCancellationToken, Logger);
                    return PrepareRegistrationAsync(encyclopedia, Loading);
                }, cancellation);
                ReassertRegistration(encyclopedia);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // The shared build logs its own failure once. A later catalog
                // reassertion failure must not leave a misleading Ready label.
                if (Loading != null && !Loading.Failed) Loading.Fail(error);
            }
        }

        private async UniTask PrepareRegistrationAsync(Encyclopedia encyclopedia, StartupLoadContext loading)
        {
            if (building) throw new InvalidOperationException("Resolute registration is already in progress.");
            building = true;
            try
            {
                if (smokeMode) SmokeTest.CaptureNativeDonorBaseline(encyclopedia);
                if (encyclopedia.ships == null || Encyclopedia.Lookup == null || encyclopedia.IndexLookup == null)
                    throw new InvalidOperationException("Encyclopedia registration collections are not initialized.");
                foreach (string key in DefinitionKeys)
                {
                    ShipDefinition definition; definitions.TryGetValue(key, out definition);
                    UnitDefinition existing;
                    if (Encyclopedia.Lookup.TryGetValue(key, out existing) && existing != definition)
                        throw new InvalidOperationException("Another unit already uses the mission key " + key + ".");
                }
                if (templateRoot == null)
                {
                    templateRoot = new GameObject("Resolute.Templates");
                    templateRoot.SetActive(false);
                    Object.DontDestroyOnLoad(templateRoot);
                }
                if (loading != null) { loading.Phase(1, "Preparing weapons"); await loading.Step("Loading missile assets"); }
                await NaturalWeapons.EnsureRegisteredAsync(encyclopedia, templateRoot.transform, Logger, loading);
                if (loading != null) { loading.Phase(2, "Preparing ship model"); await loading.Step("Loading ship materials"); }
                ResoluteLiferafts.EnsureRegistered(encyclopedia, templateRoot.transform,
                    Path.Combine(Path.GetDirectoryName(Info.Location), "Assets", "Liferafts"), Logger);
                if (visualTemplate == null)
                    visualTemplate = await VisualLoader.LoadAsync(Path.GetDirectoryName(Info.Location), templateRoot.transform, Logger, loading);
                if (visualTemplate == null) throw new InvalidOperationException("The visual loader did not produce ResoluteVisual.");
                for (int i = 0; i < DefinitionKeys.Length; i++)
                {
                    string key = DefinitionKeys[i];
                    if (loading != null) { loading.Phase(3 + i, i == 0 ? "Assembling Resolute" : "Assembling Default-3 outfit"); await loading.Step("Preparing ship systems"); }
                    if (!definitions.ContainsKey(key))
                        definitions.Add(key, await BuildTemplateAsync(encyclopedia, key, key == Default3Key ? "RSL_Default3" : Outfit, loading));
                }
                if (loading != null) { loading.Phase(5, "Registering ships"); await loading.Step("Finishing encyclopedia entries"); }
                ReassertRegistration(encyclopedia);
                loading?.Complete();
            }
            catch (Exception error) { loading?.Fail(error); throw; }
            finally { building = false; }
        }

        private void ReassertRegistration(Encyclopedia encyclopedia)
        {
            if (encyclopedia == null || definitions.Count != DefinitionKeys.Length ||
                Encyclopedia.Lookup == null || encyclopedia.IndexLookup == null) return;
            foreach (string key in DefinitionKeys)
            {
                ShipDefinition definition = definitions[key];
                UnitDefinition existing;
                if (Encyclopedia.Lookup.TryGetValue(key, out existing) && existing != definition)
                    throw new InvalidOperationException("Another unit already uses the mission key " + key + ".");
                if (!encyclopedia.ships.Contains(definition)) encyclopedia.ships.Add(definition);
                Encyclopedia.Lookup[key] = definition;
                int index = encyclopedia.IndexLookup.IndexOf(definition);
                if (index < 0) { index = encyclopedia.IndexLookup.Count; encyclopedia.IndexLookup.Add(definition); }
                ((INetworkDefinition)definition).LookupIndex = index;
            }
            NaturalWeapons.EnsureRegistered(encyclopedia, templateRoot.transform, Logger);
        }

        private async UniTask<ShipDefinition> BuildTemplateAsync(Encyclopedia encyclopedia, string key, string outfitKey, StartupLoadContext loading)
        {
            ShipDefinition donor = FindDonor(encyclopedia);
            if (donor == null)
                throw new InvalidOperationException("The vanilla Destroyer1 ship prefab was not found; no donor was changed.");

            ShipDefinition candidate = Object.Instantiate(donor);
            candidate.name = key == Default3Key ? "RSL_Resolute_Default3" : "RSL_Resolute";
            // IHasJsonKey.JsonKey deliberately rejects writes outside Unity Editor.
            candidate.jsonKey = key;
            candidate.unitName = key == Default3Key ? "Resolute Class Battlecruiser (Default-3)" : "Resolute Class Battlecruiser";
            candidate.bogeyName = "";
            candidate.code = "SHP";
            candidate.description = "At 225 metres in length and displacing 32,000 tonnes, the Resolute class combines long-range strike capability with fleet air and ballistic missile defence. Its 276 vertical-launch cells accommodate a diverse missile battery, supported by a 155 mm railgun for surface engagements and shore bombardment. Four Dart launchers, four 30 mm CIWS turrets and two Halo lasers provide layered close-range protection, complemented by electronic warfare systems and over-the-horizon ESM. A stern flight deck supports helicopter operations and replenishment, extending the nuclear-powered battlecruiser's endurance on deployment.";
            candidate.shipType = ShipType.DDG;
            candidate.dontAutomaticallyAddToEncyclopedia = false;
            AccessTools.Field(typeof(UnitDefinition), "disabled").SetValue(candidate, false);
            AccessTools.Field(typeof(UnitDefinition), "isEventContent").SetValue(candidate, false);
            candidate.shipInfo = donor.shipInfo == null ? new ShipInfo() : new ShipInfo
            {
                mass = donor.shipInfo.mass,
                topSpeed = donor.shipInfo.topSpeed
            };

            GameObject prefab = null;
            var existingChildren = new HashSet<GameObject>();
            foreach (Transform child in templateRoot.transform)
                existingChildren.Add(child.gameObject);
            try
            {
                prefab = await ShipFactory.BuildAsync(donor, candidate, visualTemplate, templateRoot.transform, Logger, outfitKey, loading);
                if (prefab == null || prefab.GetComponent<Ship>() == null)
                    throw new InvalidOperationException("The ship factory did not produce a Ship prefab.");
                if (prefab.transform.parent != templateRoot.transform || prefab.activeInHierarchy || !prefab.activeSelf)
                    throw new InvalidOperationException("The ship template must be activeSelf under the inactive template root.");

                prefab.name = candidate.name;
                candidate.unitPrefab = prefab;
                prefab.GetComponent<Ship>().definition = candidate;
                if (loading != null) await loading.Step("Checking ship identity");
                ConfigureIdentities(prefab, key);
                candidate.mass = prefab.GetComponentsInChildren<UnitPart>(true).Sum(p => p.GetMass());
                Object.DontDestroyOnLoad(candidate);
                Logger.LogInfo("Built Resolute from vanilla donor " + donor.jsonKey + ".");
                return candidate;
            }
            catch
            {
                if (prefab != null)
                    Object.Destroy(prefab);
                foreach (Transform child in templateRoot.transform)
                    if (!existingChildren.Contains(child.gameObject))
                        Object.Destroy(child.gameObject);
                Object.Destroy(candidate);
                throw;
            }
        }

        internal static ShipDefinition FindDonor(Encyclopedia encyclopedia)
        {
            if (encyclopedia == null || encyclopedia.ships == null)
                return null;
            return encyclopedia.ships.FirstOrDefault(ship => ship != null && ship.unitPrefab != null &&
                string.Equals(ship.unitPrefab.name, "Destroyer1", StringComparison.OrdinalIgnoreCase))
                ?? encyclopedia.ships.FirstOrDefault(ship => ship != null && ship.unitPrefab != null &&
                string.Equals(ship.jsonKey, "destroyer1", StringComparison.OrdinalIgnoreCase));
        }

        private static void ConfigureIdentities(GameObject prefab, string key)
        {
            NetworkIdentity rootIdentity = prefab.GetComponent<NetworkIdentity>();
            if (rootIdentity == null)
                throw new InvalidOperationException("The ship is missing its root NetworkIdentity.");

            NetworkIdentity[] own = prefab.GetComponentsInChildren<NetworkIdentity>(true);
            var ownSet = new HashSet<NetworkIdentity>(own);
            var occupied = new HashSet<int>(Resources.FindObjectsOfTypeAll<NetworkIdentity>()
                .Where(identity => identity != null && !ownSet.Contains(identity))
                .Select(identity => identity.PrefabHash));
            FieldInfo spawnedField = AccessTools.Field(typeof(NetworkIdentity), "_hasSpawned");
            if (spawnedField == null)
                throw new MissingFieldException(typeof(NetworkIdentity).FullName, "_hasSpawned");

            foreach (NetworkIdentity identity in own)
            {
                string suffix = identity == rootIdentity ? "root" : HierarchyKey(identity.transform, prefab.transform);
                int hash = StableHash(Id + ":" + key + ":" + suffix);
                if (hash == 0 || !occupied.Add(hash))
                    throw new InvalidOperationException("Resolute network prefab hash collision for " + suffix + ".");
                identity.ClearSceneId();
                identity.PrefabHash = hash;
                spawnedField.SetValue(identity, false);
                identity.ClearNetworkBehaviourCache();
            }
        }

        private static string HierarchyKey(Transform item, Transform root)
        {
            var parts = new List<string>();
            while (item != null && item != root)
            {
                parts.Add(item.name + "[" + item.GetSiblingIndex() + "]");
                item = item.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in text)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }
    }

    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    internal static class EncyclopediaLoadedPatch
    {
        [HarmonyAfter("com.nikkorap.blueprinter")]
        private static void Postfix(Encyclopedia __instance)
        {
            if (Plugin.Instance != null && Plugin.Instance.SynchronousStartup)
                Plugin.Instance.EnsureRegistered(__instance);
        }
    }

    [HarmonyPatch(typeof(Application), nameof(Application.version), MethodType.Getter)]
    internal static class VersionCompatibilityPatch
    {
        internal static string Suffix => "_rsl-v" + Plugin.Version + "-" + Plugin.Outfit.Replace("RSL_", "").Replace("rsl_loadout_", "");

        [HarmonyPriority(Priority.Last)]
        [HarmonyAfter("com.nikkorap.blueprinter")]
        private static void Postfix(ref string __result)
        {
            __result = AppendVersion(__result);
        }

        internal static string AppendVersion(string original)
        {
            original = original ?? string.Empty;
            if (original.IndexOf(Suffix, StringComparison.Ordinal) >= 0)
                return original;
            string combined = original + Suffix;
            if (combined.Length <= 100)
                return combined;

            // Long existing mod signatures retain their identity in a compact,
            // deterministic digest, within the convention used by Blueprinter.
            int separator = original.IndexOf('_');
            string baseVersion = separator >= 0 ? original.Substring(0, separator) : original;
            using (SHA256 hash = SHA256.Create())
            {
                string digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(combined)), 0, 6)
                    .Replace("-", "").ToLowerInvariant();
                return baseVersion + "_" + digest + Suffix;
            }
        }
    }

    // Other content loaders can finish asynchronously after AfterLoad. Reassert
    // this mod's own entries at the point where the real browser reads its list.
    [HarmonyPatch(typeof(EncyclopediaBrowser), nameof(EncyclopediaBrowser.SelectShips))]
    internal static class ShipBrowserRegistrationPatch
    {
        private static void Prefix() => Plugin.Instance?.EnsureRegistered(Encyclopedia.i);
    }

    [HarmonyPatch(typeof(EncyclopediaBrowser), nameof(EncyclopediaBrowser.SelectMissiles))]
    internal static class MissileBrowserRegistrationPatch
    {
        private static void Prefix() => Plugin.Instance?.EnsureRegistered(Encyclopedia.i);
    }
}
