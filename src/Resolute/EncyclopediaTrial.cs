using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Authentication;
using NuclearOption.SceneLoading;
using NuclearOption.Social;
using Rewired.UI.ControlMapper;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Resolute
{
    // Exercises the shipped browser scene and its real category/navigation/spawn
    // methods. This mode never supplies a replacement browse list or saves data.
    internal static class EncyclopediaTrial
    {
        private static readonly List<string> suppressedSaveMethods = new List<string>();
        private static Harmony isolation;

        internal static IEnumerator Run(string[] args, ManualLogSource log)
        {
            string output = Audit.Argument(args, "--resolute-encyclopedia-output");
            var checks = new List<object>();
            var errors = new List<string>();
            var report = new Dictionary<string, object>
            {
                ["version"] = 1, ["mode"] = "native-offline-encyclopedia-trial",
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["gameVersion"] = Application.version, ["unityVersion"] = Application.unityVersion,
                ["gameDataPath"] = Application.dataPath, ["pluginSha256"] = Audit.AssemblySha256(), ["assetSha256"] = Audit.AssetSha256(),
                ["checks"] = checks, ["nativeErrors"] = errors, ["success"] = false,
                ["captureMethod"] = "Explicit URP StandardRequest on the native encyclopedia camera/stack. Existing overlay canvases temporarily use that camera for the capture and are restored afterward; no UI is recreated.",
                ["suppressedExternalServices"] = new[] { "Steam ticket acquisition", "DiscordManager.InitClient" }
            };
            if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output))
            {
                log.LogError("Encyclopedia trial requires an absolute --resolute-encyclopedia-output path.");
                Application.Quit(2); yield break;
            }
            string gameRoot = Directory.GetParent(Application.dataPath).FullName;
            if (!string.Equals(Path.GetFileName(gameRoot), "test-game", StringComparison.OrdinalIgnoreCase))
            {
                report["error"] = "Encyclopedia trial is restricted to a copied directory named test-game.";
                Save(output, report); Application.Quit(2); yield break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Application.LogCallback collect = delegate(string message, string stack, LogType type)
            {
                if ((type == LogType.Exception || type == LogType.Error || type == LogType.Assert) && errors.Count < 100)
                    errors.Add(message + "\n" + stack);
            };
            Application.logMessageReceived += collect;
            IEnumerator body = Execute(report, checks, output);
            while (true)
            {
                bool more;
                try { more = body.MoveNext(); }
                catch (Exception ex)
                {
                    report["error"] = ex.ToString();
                    log.LogError("Resolute encyclopedia trial: " + ex);
                    break;
                }
                if (!more) break;
                yield return body.Current;
            }
            Application.logMessageReceived -= collect;
            report["suppressedPersistenceMethods"] = suppressedSaveMethods;
            report["steamInitialized"] = SteamManager.ClientInitialized || SteamManager.ServerInitialized;
            report["success"] = !report.ContainsKey("error") && errors.Count == 0;
            Save(output, report);
            log.LogInfo("Resolute encyclopedia trial complete: " + report["success"] + "; " + output);
            Application.Quit((bool)report["success"] ? 0 : 2);
        }

        private static IEnumerator Execute(Dictionary<string, object> report, List<object> checks, string output)
        {
            InstallIsolation();
            report["phase"] = "preload"; Save(output, report);
            GameManager.IsHeadless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            MainMenu menu = Resources.FindObjectsOfTypeAll<MainMenu>().FirstOrDefault(m => m.gameObject.scene.IsValid());
            if (menu == null) throw new InvalidOperationException("Trial requires the serialized main-menu resource references.");
            var graphics = (GraphicsHelperSO)AccessTools.Field(typeof(MainMenu), "graphicsSettings").GetValue(menu);
            PlayerSettings.FirstInit(graphics);
            PlayerSettings.playerName = "Resolute encyclopedia trial";
            PlayerSettings.playerName_Unsanitized = PlayerSettings.playerName;
            var tasks = new[]
            {
                Encyclopedia.Preload(CancellationToken.None).AsTask(),
                GameAssets.Preload(CancellationToken.None).AsTask(),
                NetworkManagerNuclearOption.Preload(CancellationToken.None).AsTask(),
                SoundManager.Preload(CancellationToken.None).AsTask(),
                DebugUI.Preload(CancellationToken.None).AsTask(),
                ResourcesAsyncLoader.LoadPrefab("Rewired", CancellationToken.None, go =>
                    GameManager.controlMapper = go.GetComponentInChildren<ControlMapper>(true)).AsTask(),
                ResourcesAsyncLoader.LoadPrefab("EventSystem", CancellationToken.None, go =>
                    GameManager.eventSystem = go.GetComponent<EventSystem>()).AsTask()
            };
            Task preload = Task.WhenAll(tasks);
            float until = Time.realtimeSinceStartup + 180;
            while (!preload.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!preload.IsCompleted) throw new TimeoutException("Native resource preload exceeded 180 seconds.");
            preload.GetAwaiter().GetResult();
            Plugin.Instance.EnsureRegistered(Encyclopedia.i);
            Require(checks, "registered-definition", Encyclopedia.Lookup.ContainsKey(Plugin.DefinitionKey));
            Require(checks, "steam-remains-uninitialized", !SteamManager.ClientInitialized && !SteamManager.ServerInitialized);
            Require(checks, "discord-remains-uninitialized", Resources.FindObjectsOfTypeAll<DiscordManager>()
                .All(d => AccessTools.Field(typeof(DiscordManager), "_client").GetValue(d) == null));

            // This is the exact state/map transition used by MainMenu.SelectEncyclopedia.
            report["phase"] = "load-native-encyclopedia"; Save(output, report);
            MissionManager.SetNullMission();
            TimeScaleManager.Scale = 1;
            Task host = NetworkManagerNuclearOption.i.StartHostAsync(
                new HostOptions(SocketType.Offline, GameState.Encyclopedia, MapLoader.Encyclopedia)).AsTask();
            until = Time.realtimeSinceStartup + 240;
            while (!host.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
            if (!host.IsCompleted) throw new TimeoutException("Native encyclopedia scene/host load exceeded 240 seconds.");
            host.GetAwaiter().GetResult();
            until = Time.realtimeSinceStartup + 60;
            EncyclopediaBrowser browser;
            while (((browser = SceneSingleton<EncyclopediaBrowser>.i) == null || !Read<bool>(browser, "initialized")) &&
                Time.realtimeSinceStartup < until) yield return null;
            browser = SceneSingleton<EncyclopediaBrowser>.i;
            Require(checks, "native-browser-initialized", browser != null && Read<bool>(browser, "initialized"));
            Require(checks, "native-encyclopedia-state", GameManager.gameState == GameState.Encyclopedia);
            Require(checks, "offline-server", NetworkManagerNuclearOption.i.Server.Active && !NetworkManagerNuclearOption.i.Server.Listening);
            Require(checks, "native-spawner", NetworkSceneSingleton<Spawner>.i != null);
            report["map"] = NetworkManagerNuclearOption.i.MapKey.ToString();

            report["phase"] = "native-ship-navigation"; Save(output, report);
            browser.SelectShips();
            yield return null; yield return null;
            List<UnitDefinition> shipList = Read<List<UnitDefinition>>(browser, "browseList");
            report["shipBrowseList"] = shipList.Select(Definition).ToList();
            Require(checks, "ship-in-native-browse-list-once", shipList.Count(d => d.jsonKey == Plugin.DefinitionKey) == 1);
            UnitDefinition subject = shipList.Single(d => d.jsonKey == Plugin.DefinitionKey);
            Require(checks, "ship-allowed-in-normal-browser", subject.IsAllowed(false));
            var visitedShips = new List<string>();
            report["shipNavigation"] = visitedShips;
            for (int n = 0; n < shipList.Count; n++)
            {
                Unit visible = browser.GetSpawnedUnit();
                Require(checks, "native-ship-spawn-" + n, visible is Ship && visible.gameObject.activeInHierarchy);
                visitedShips.Add(visible.definition.jsonKey);
                if (visible.definition.jsonKey == Plugin.DefinitionKey) break;
                browser.NextUnit(false);
                yield return null; yield return null;
            }
            Ship ship = browser.GetSpawnedUnit() as Ship;
            Require(checks, "native-navigation-reaches-resolute", ship != null && ship.definition.jsonKey == Plugin.DefinitionKey);
            Require(checks, "consistent-ship-definition-name", ship.definition.unitName == "Resolute Class Battlecruiser");
            TMP_Text title = Read<TMP_Text>(browser, "unitName");
            Require(checks, "native-visible-ship-title", title.gameObject.activeInHierarchy && title.text == ship.definition.unitName);
            Require(checks, "native-spawned-ship-name", ship.unitName == ship.definition.unitName && ship.UniqueName == ship.definition.unitName);
            IEnumerator shipView = WaitForNativeView(browser, ship, checks, report, "shipView");
            while (shipView.MoveNext()) yield return shipView.Current;
            Canvas.ForceUpdateCanvases();
            report["shipWeaponRows"] = InspectWeaponRows(browser, ship, checks);
            report["descriptionPanel"] = Panel(Read<RectTransform>(browser, "descriptionPanel"));
            report["shipCamera"] = Resources.FindObjectsOfTypeAll<CameraStateManager>().Where(c => c.gameObject.scene.IsValid())
                .Select(c => new Dictionary<string, object> { ["active"] = c.gameObject.activeInHierarchy, ["name"] = c.name }).ToList();
            if (!GameManager.IsHeadless)
            {
                string capture = Capture(output, "resolute_encyclopedia_ship");
                report["shipScreenshot"] = capture;
                yield return new WaitForSecondsRealtime(1);
                Require(checks, "ship-browser-screenshot-written", File.Exists(capture) && new FileInfo(capture).Length > 1000);
                report["shipScreenshotAnalysis"] = InspectCapture(capture, checks, "ship-browser-screenshot-has-content");
            }
            Save(output, report);

            report["phase"] = "native-default3-navigation"; Save(output, report);
            Require(checks, "default3-in-native-browse-list-once", shipList.Count(d => d.jsonKey == Plugin.Default3Key) == 1);
            Ship variantShip = null;
            for (int n = 0; n <= shipList.Count; n++)
            {
                variantShip = browser.GetSpawnedUnit() as Ship;
                if (variantShip != null && variantShip.definition.jsonKey == Plugin.Default3Key) break;
                browser.NextUnit(false);
                yield return null; yield return null;
            }
            Require(checks, "native-navigation-reaches-default3", variantShip != null && variantShip.definition.jsonKey == Plugin.Default3Key);
            Require(checks, "native-default3-title", title.text == "Resolute Class Battlecruiser (Default-3)" &&
                variantShip.unitName == title.text && variantShip.UniqueName == title.text);
            IEnumerator variantView = WaitForNativeView(browser, variantShip, checks, report, "default3View");
            while (variantView.MoveNext()) yield return variantView.Current;
            Canvas.ForceUpdateCanvases();
            report["default3WeaponRows"] = InspectWeaponRows(browser, variantShip, checks);
            if (!GameManager.IsHeadless)
            {
                string capture = Capture(output, "resolute_encyclopedia_default3");
                report["default3Screenshot"] = capture;
                yield return new WaitForSecondsRealtime(.5f);
                Require(checks, "default3-browser-screenshot-written", File.Exists(capture) && new FileInfo(capture).Length > 1000);
                report["default3ScreenshotAnalysis"] = InspectCapture(capture, checks, "default3-browser-screenshot-has-content");
            }
            Save(output, report);

            report["phase"] = "native-missile-navigation"; Save(output, report);
            browser.SelectMissiles();
            yield return null; yield return null;
            List<UnitDefinition> missileList = Read<List<UnitDefinition>>(browser, "browseList");
            UnitDefinition[] customMissiles = missileList.Where(d => d.jsonKey.StartsWith("rsl_", StringComparison.Ordinal)).ToArray();
            report["missileBrowseList"] = missileList.Select(Definition).ToList();
            Require(checks, "eight-active-custom-missile-browser-entries", customMissiles.Length == 8);
            Require(checks, "custom-missile-keys-unique", customMissiles.Select(d => d.jsonKey).Distinct().Count() == 8);
            Require(checks, "custom-missile-names-unique", customMissiles.Select(d => d.unitName).Distinct().Count() == 8);
            Require(checks, "underwater-weapons-absent-from-browser", customMissiles.All(d => NaturalArmament.IsEnabledWeapon(d.jsonKey)));
            var missileViews = new List<object>();
            report["customMissileViews"] = missileViews;
            var visitedMissiles = new HashSet<string>();
            for (int n = 0; n < missileList.Count; n++)
            {
                Unit visible = browser.GetSpawnedUnit();
                if (visible == null) throw new InvalidOperationException("Native missile navigation did not produce a unit.");
                UnitDefinition definition = visible.definition;
                if (!visitedMissiles.Add(definition.jsonKey)) break;
                if (definition.jsonKey.StartsWith("rsl_", StringComparison.Ordinal))
                {
                    Missile missile = visible as Missile;
                    Require(checks, "native-missile-spawn-" + definition.jsonKey, missile != null && missile.gameObject.activeInHierarchy);
                    Require(checks, "native-missile-title-" + definition.jsonKey, title.gameObject.activeInHierarchy && title.text == definition.unitName);
                    WeaponInfo info = missile.GetWeaponInfo();
                    Require(checks, "consistent-missile-name-" + definition.jsonKey, info != null && info.weaponName == definition.unitName);
                    var view = new Dictionary<string, object>
                    {
                        ["key"] = definition.jsonKey, ["title"] = title.text, ["weaponName"] = info.weaponName,
                        ["description"] = Read<TMP_Text>(browser, "unitDescription").text,
                        ["activeMeshRenderers"] = missile.GetComponentsInChildren<MeshRenderer>().Count(r => r.enabled),
                        ["nativeGuidance"] = Read<TMP_Text>(browser, "guidance").text,
                        ["nativeYield"] = Read<TMP_Text>(browser, "yield").text,
                        ["nativeMass"] = Read<TMP_Text>(browser, "mass").text
                    };
                    Require(checks, "visible-missile-model-" + definition.jsonKey, (int)view["activeMeshRenderers"] > 0);
                    missileViews.Add(view);
                    if (!GameManager.IsHeadless)
                    {
                        IEnumerator missileView = WaitForNativeView(browser, missile, checks, view, "nativeCameraView");
                        while (missileView.MoveNext()) yield return missileView.Current;
                        string capture = Capture(output, "encyclopedia_" + definition.jsonKey);
                        view["screenshot"] = capture;
                        yield return new WaitForSecondsRealtime(.5f);
                        Require(checks, "missile-screenshot-written-" + definition.jsonKey, File.Exists(capture) && new FileInfo(capture).Length > 1000);
                        view["screenshotAnalysis"] = InspectCapture(capture, checks, "missile-screenshot-has-content-" + definition.jsonKey);
                    }
                    Save(output, report);
                }
                browser.NextUnit(false);
                yield return null; yield return null;
            }
            Require(checks, "native-navigation-reaches-all-eight-custom-missiles", missileViews.Count == 8);
            report["missileNavigationCount"] = visitedMissiles.Count;
            report["phase"] = "complete";
        }

        private static object InspectWeaponRows(EncyclopediaBrowser browser, Ship ship, List<object> checks)
        {
            Array rows = Read<Array>(browser, "weaponStationDisplays");
            var expected = ship.weaponStations.GroupBy(s => s.WeaponInfo).ToDictionary(g => g.Key, g => g.Sum(s => s.FullAmmo));
            var displayed = new HashSet<WeaponInfo>();
            var details = new List<object>();
            foreach (object row in rows)
            {
                GameObject panel = Read<GameObject>(row, "panel");
                if (!panel.activeSelf) continue;
                WeaponInfo info = Read<WeaponInfo>(row, "weaponInfo");
                TMP_Text name = Read<TMP_Text>(row, "nameText");
                TMP_Text ammo = Read<TMP_Text>(row, "ammoText");
                bool known = info != null && expected.ContainsKey(info);
                Require(checks, "native-weapon-row-" + details.Count, known && displayed.Add(info) &&
                    name.text == info.weaponName && ammo.text == (EncyclopediaIntegration.IsHalo(info) ? string.Empty : "x " + expected[info]));
                details.Add(new Dictionary<string, object>
                    { ["name"] = name.text, ["ammo"] = ammo.text, ["panel"] = Panel((RectTransform)panel.transform) });
            }
            Require(checks, "all-outfit-weapons-listed", displayed.Count == expected.Count);
            Require(checks, "halo-displayed-without-ammunition", ship.weaponStations.Where(s => EncyclopediaIntegration.IsHalo(s.WeaponInfo))
                .All(s => s.GetAmmoReadout() == string.Empty) && details.OfType<Dictionary<string, object>>()
                .Any(d => ((string)d["name"]).StartsWith("Halo", StringComparison.Ordinal) && (string)d["ammo"] == string.Empty));
            return new Dictionary<string, object>
                { ["availableRows"] = rows.Length, ["weaponTypes"] = expected.Count, ["rows"] = details };
        }

        private static object Panel(RectTransform panel)
        {
            var corners = new Vector3[4]; panel.GetWorldCorners(corners);
            Canvas canvas = panel.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2[] screen = corners.Select(c => RectTransformUtility.WorldToScreenPoint(camera, c)).ToArray();
            var parents = new List<object>();
            for (Transform at = panel; at != null; at = at.parent)
                parents.Add(new Dictionary<string, object> { ["name"] = at.name,
                    ["components"] = at.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray() });
            return new Dictionary<string, object>
            {
                ["name"] = panel.name, ["active"] = panel.gameObject.activeInHierarchy,
                ["width"] = panel.rect.width, ["height"] = panel.rect.height,
                ["screenCorners"] = screen.Select(p => new[] { p.x, p.y }).ToArray(),
                ["insideScreen"] = screen.All(p => p.x >= -.5f && p.x <= Screen.width + .5f && p.y >= -.5f && p.y <= Screen.height + .5f),
                ["scrollParents"] = panel.GetComponentsInParent<ScrollRect>(true).Select(s => s.name).ToArray(),
                ["parents"] = parents
            };
        }

        private static IEnumerator WaitForNativeView(EncyclopediaBrowser browser, Unit unit, List<object> checks,
            Dictionary<string, object> report, string field)
        {
            if (GameManager.IsHeadless) yield break;
            CameraStateManager manager = SceneSingleton<CameraStateManager>.i;
            Require(checks, "native-view-camera-" + unit.definition.jsonKey, manager != null && manager.mainCamera != null);
            Camera camera = manager.mainCamera;
            float started = Time.realtimeSinceStartup;
            float desired = unit.maxRadius * 2.6f;
            int stable = 0;
            while (Time.realtimeSinceStartup - started < 15f)
            {
                if (browser.GetSpawnedUnit() != unit) throw new InvalidOperationException("Native encyclopedia selection changed before capture.");
                float distance = Read<float>(manager.encyclopediaState, "cameraDistSmoothed");
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                MeshRenderer[] models = unit.GetComponentsInChildren<MeshRenderer>()
                    .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.GetComponent<MeshFilter>() != null &&
                        r.GetComponent<MeshFilter>().sharedMesh != null).ToArray();
                int inView = models.Count(r => GeometryUtility.TestPlanesAABB(planes, r.bounds));
                bool settled = Mathf.Abs(distance - desired) < Mathf.Max(.2f, desired * .03f);
                stable = settled && inView > 0 ? stable + 1 : 0;
                report[field] = new Dictionary<string, object>
                {
                    ["waitSeconds"] = Time.realtimeSinceStartup - started, ["desiredDistanceMetres"] = desired,
                    ["nativeSmoothedDistanceMetres"] = distance, ["modelRenderersInFrustum"] = inView,
                    ["modelRenderers"] = models.Length, ["consecutiveSettledFrames"] = stable
                };
                if (stable >= 4) break;
                yield return null;
            }
            Require(checks, "native-view-settled-with-model-" + unit.definition.jsonKey, stable >= 4);
        }

        private static string Capture(string output, string name)
        {
            string directory = Path.Combine(Path.GetDirectoryName(output), "game_previews");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");
            CameraStateManager manager = SceneSingleton<CameraStateManager>.i;
            Camera camera = manager != null ? manager.mainCamera : null;
            if (camera == null) throw new InvalidOperationException("Native encyclopedia camera is unavailable.");
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            int previousMask = camera.cullingMask;
            bool previousEnabled = camera.enabled;
            int width = Math.Max(640, Screen.width), height = Math.Max(360, Screen.height);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            target.Create();
            CanvasCaptureState[] canvases = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c.gameObject.scene.IsValid() && c.isRootCanvas && c.gameObject.activeInHierarchy &&
                    c.renderMode == RenderMode.ScreenSpaceOverlay).Select(c => new CanvasCaptureState(c)).ToArray();
            try
            {
                camera.targetTexture = target;
                camera.enabled = true;
                foreach (CanvasCaptureState state in canvases)
                {
                    state.canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    state.canvas.worldCamera = camera;
                    state.canvas.planeDistance = camera.nearClipPlane + .25f;
                    foreach (Transform item in state.canvas.GetComponentsInChildren<Transform>(true))
                        camera.cullingMask |= 1 << item.gameObject.layer;
                }
                Canvas.ForceUpdateCanvases();
                var request = new RenderPipeline.StandardRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    throw new InvalidOperationException("The active render pipeline cannot capture the native encyclopedia camera stack.");
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                var png = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    png.ReadPixels(new Rect(0, 0, width, height), 0, 0); png.Apply();
                    File.WriteAllBytes(path, ImageConversion.EncodeToPNG(png));
                }
                finally { UnityEngine.Object.Destroy(png); }
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.cullingMask = previousMask;
                camera.enabled = previousEnabled;
                foreach (CanvasCaptureState state in canvases) state.Restore();
                Canvas.ForceUpdateCanvases();
                RenderTexture.active = previousActive;
                target.Release(); UnityEngine.Object.Destroy(target);
            }
            return path;
        }

        private sealed class CanvasCaptureState
        {
            internal readonly Canvas canvas;
            private readonly Camera camera;
            private readonly RenderMode mode;
            private readonly float distance;
            private readonly RectTransform rect;
            private readonly Vector2 anchorMin, anchorMax, pivot, size;
            private readonly Vector3 position, scale;
            private readonly Quaternion rotation;

            internal CanvasCaptureState(Canvas canvas)
            {
                this.canvas = canvas; camera = canvas.worldCamera; mode = canvas.renderMode; distance = canvas.planeDistance;
                rect = (RectTransform)canvas.transform;
                anchorMin = rect.anchorMin; anchorMax = rect.anchorMax; pivot = rect.pivot; size = rect.sizeDelta;
                position = rect.anchoredPosition3D; rotation = rect.localRotation; scale = rect.localScale;
            }

            internal void Restore()
            {
                canvas.renderMode = mode; canvas.worldCamera = camera; canvas.planeDistance = distance;
                rect.anchorMin = anchorMin; rect.anchorMax = anchorMax; rect.pivot = pivot; rect.sizeDelta = size;
                rect.anchoredPosition3D = position; rect.localRotation = rotation; rect.localScale = scale;
            }
        }

        private static object InspectCapture(string path, List<object> checks, string check)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
                    throw new InvalidOperationException("Native screenshot could not be decoded: " + path);
                Color32[] pixels = texture.GetPixels32();
                int min = 765, max = 0, bright = 0, sampled = 0;
                var colors = new HashSet<int>();
                for (int n = 0; n < pixels.Length; n += 17)
                {
                    Color32 color = pixels[n];
                    int value = color.r + color.g + color.b;
                    min = Math.Min(min, value); max = Math.Max(max, value);
                    if (value > 90) bright++;
                    colors.Add((color.r << 16) | (color.g << 8) | color.b);
                    sampled++;
                }
                Require(checks, check, texture.width >= 640 && texture.height >= 360 &&
                    max - min > 90 && bright > sampled / 1000 && colors.Count > 32);
                return new Dictionary<string, object>
                {
                    ["width"] = texture.width, ["height"] = texture.height, ["sampledPixels"] = sampled,
                    ["uniqueSampleColors"] = colors.Count, ["minRgbSum"] = min, ["maxRgbSum"] = max,
                    ["brightSamples"] = bright
                };
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        private static object Definition(UnitDefinition definition)
        {
            return new Dictionary<string, object>
                { ["key"] = definition.jsonKey, ["name"] = definition.unitName, ["allowed"] = definition.IsAllowed(false) };
        }

        private static void InstallIsolation()
        {
            isolation = new Harmony(Plugin.Id + ".encyclopedia-trial");
            var skip = new HarmonyMethod(typeof(EncyclopediaTrial), nameof(SkipPersistence));
            isolation.Patch(AccessTools.Method(typeof(PlayerSettings), "LoadPrefs"), prefix: skip);
            isolation.Patch(AccessTools.Method(typeof(DiscordManager), "InitClient"), prefix: skip);
            suppressedSaveMethods.Add("PlayerSettings.LoadPrefs (creates missing stored Tobii defaults)");
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Rewired", StringComparison.Ordinal)))
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                foreach (Type type in types.Where(t => t.FullName.IndexOf("UserDataStore", StringComparison.Ordinal) >= 0))
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (method.Name.StartsWith("Save", StringComparison.Ordinal) && !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null)
                    {
                        isolation.Patch(method, prefix: skip);
                        suppressedSaveMethods.Add(type.FullName + "." + method.Name);
                    }
            }
            isolation.Patch(AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "OnClientConnected"),
                prefix: new HarmonyMethod(typeof(EncyclopediaTrial), nameof(LocalHostAuthentication)));
        }

        private static bool SkipPersistence() { return false; }

        private static bool LocalHostAuthentication(NetworkAuthenticatorNuclearOption __instance, INetworkPlayer player)
        {
            NetworkManagerNuclearOption network = NetworkManagerNuclearOption.i;
            if (!player.IsHost || network.Server.Listening) throw new InvalidOperationException("Trial authentication is restricted to a non-listening local host.");
            uint buildHash = (uint)AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "GetBuildHash").Invoke(null, null);
            var message = new NetworkAuthenticatorNuclearOption.AuthMessage
                { BuildHash = buildHash, JoinAs = PlayerType.DedicatedServer, SteamName = "Resolute encyclopedia trial" };
            AccessTools.Method(typeof(NetworkAuthenticatorNuclearOption), "SendAuthentication")
                .Invoke(__instance, new object[] { network.Client, message });
            return false;
        }

        private static T Read<T>(object instance, string field)
        {
            return (T)AccessTools.Field(instance.GetType(), field).GetValue(instance);
        }

        private static void Require(List<object> checks, string name, bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("Encyclopedia assertion failed: " + name);
        }

        private static void Save(string path, object report)
        {
            File.WriteAllText(path, Audit.Json(report));
        }
    }
}
