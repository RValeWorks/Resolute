using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Mirage;
using UnityEngine;

namespace Resolute
{
    internal static class NaturalBallisticReferenceTrial
    {
        internal const string StarfallKey = "Aryx_Hypersonic1";
        private const string AryxId = "AryxWeaponryExpansion";
        private const string BlueprinterId = "com.nikkorap.blueprinter";
        internal static bool AryxLoaded => Chainloader.PluginInfos.ContainsKey(AryxId);

        internal static IEnumerator WaitUntilReady(Dictionary<string, object> report, List<object> checks, string output)
        {
            if (!AryxLoaded) yield break;
            var detail = new Dictionary<string, object> {
                ["requiredPlugin"] = AryxId, ["requiredDefinition"] = StarfallKey,
                ["loadedAssembly"] = Chainloader.PluginInfos[AryxId].Instance != null ?
                    Chainloader.PluginInfos[AryxId].Instance.GetType().Assembly.FullName : "not-initialized",
                ["timeoutSeconds"] = 90f, ["success"] = false
            };
            report["ballisticReference"] = detail;
            object previousPhase = report.ContainsKey("phase") ? report["phase"] : null;
            report["phase"] = "waiting-for-genuine-aryx-starfall";
            File.WriteAllText(output, Audit.Json(report));
            float started = Time.realtimeSinceStartup;
            MissileDefinition starfall;
            while (!TryGetStarfall(out starfall) && Time.realtimeSinceStartup - started < 90f)
                yield return new WaitForSecondsRealtime(.25f);
            bool ready = TryGetStarfall(out starfall);
            detail["waitedSeconds"] = Time.realtimeSinceStartup - started;
            detail["blueprinterPatchingComplete"] = BlueprinterReady();
            detail["success"] = ready;
            if (ready)
            {
                detail["definition"] = starfall.jsonKey;
                detail["value"] = starfall.value;
                detail["lookupIndex"] = ((INetworkDefinition)starfall).LookupIndex;
                detail["seekerType"] = starfall.unitPrefab.GetComponents<MissileSeeker>()
                    .Single(s => s.GetType().FullName == "AryxWeaponryExpansion.AryxInertialSeeker").GetType().FullName;
            }
            checks.Add(new Dictionary<string, object> { ["name"] = "genuine-aryx-starfall-registered-before-ballistic-fixtures", ["passed"] = ready });
            if (previousPhase != null) report["phase"] = previousPhase;
            File.WriteAllText(output, Audit.Json(report));
            if (!ready) throw new InvalidOperationException("Genuine Aryx plugin is loaded, but Starfall did not finish registration within 90 seconds. The fixture will not substitute a stock missile. Inspect the copied game's Blueprinter/Aryx loading errors and verify the audited AryxWeaponsPack_1.0.6.dll and Blueprinter_1.8.21.dll are loaded together.");
        }

        internal static bool TryGetStarfall(out MissileDefinition definition)
        {
            definition = null;
            if (!AryxLoaded || Chainloader.PluginInfos[AryxId].Instance == null || !BlueprinterReady() || Encyclopedia.i == null || Encyclopedia.Lookup == null) return false;
            UnitDefinition found;
            if (!Encyclopedia.Lookup.TryGetValue(StarfallKey, out found)) return false;
            MissileDefinition missile = found as MissileDefinition;
            if (missile == null || missile.unitPrefab == null || !Encyclopedia.i.missiles.Contains(missile)) return false;
            // Check the actual custom component before asking the production
            // classifier to cache its profile; partially patched prefabs must
            // not produce a permanent negative profile during this wait.
            if (!missile.unitPrefab.GetComponents<MissileSeeker>().Any(s => s != null &&
                s.GetType().FullName == "AryxWeaponryExpansion.AryxInertialSeeker" &&
                s.GetType().Assembly == Chainloader.PluginInfos[AryxId].Instance.GetType().Assembly)) return false;
            int? index = ((INetworkDefinition)missile).LookupIndex;
            if (!index.HasValue || index.Value < 0 || index.Value >= Encyclopedia.i.IndexLookup.Count ||
                !ReferenceEquals(Encyclopedia.i.IndexLookup[index.Value], missile) || !NaturalBallisticSelection.IsEligibleDefinition(missile)) return false;
            definition = missile;
            return true;
        }

        private static bool BlueprinterReady()
        {
            if (!Chainloader.PluginInfos.ContainsKey(BlueprinterId)) return false;
            var instance = Chainloader.PluginInfos[BlueprinterId].Instance;
            if (instance == null) return false;
            PropertyInfo ready = instance.GetType().GetProperty("PatchingComplete", BindingFlags.Instance | BindingFlags.Public);
            return ready != null && ready.PropertyType == typeof(bool) && (bool)ready.GetValue(instance, null);
        }
    }
}
