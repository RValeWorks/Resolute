using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    // Offline evidence collection only. Production flight never reads this
    // harness or requires a diagnostic argument/build symbol to use native cruise.
    internal static class NaturalCruiseNativeExperiment
    {
        internal static bool Enabled { get; private set; }
        private static List<object> completedControllers = new List<object>();
        private static int opticalWaypoints;
        internal static bool Requested => Environment.GetCommandLineArgs().Contains("--resolute-native-cruise-experiment");
        internal static bool Compiled
        {
            get
            {
#if RESOLUTE_NATIVE_CRUISE_EXPERIMENT
                return true;
#else
                return false;
#endif
            }
        }

        internal static void Begin(Dictionary<string, object> detail)
        {
            End();
            bool isolated = string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName),
                "test-game", StringComparison.OrdinalIgnoreCase);
            bool offline = NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active &&
                !NetworkManagerNuclearOption.i.Server.Listening;
            bool selected = string.Equals(Audit.Argument(Environment.GetCommandLineArgs(), "--resolute-trial-only"), "cruise_behavior", StringComparison.Ordinal);
            if (!isolated || !offline || !selected || Requested && !Compiled)
                throw new InvalidOperationException("Cruise controller evidence requires isolated offline cruise_behavior; the experimental flag additionally requires its explicit build mode.");
            completedControllers = new List<object>();
            opticalWaypoints = 0;
            detail["nativeCruiseCompletedControllers"] = completedControllers;
            detail["nativeCruisePrefabMetadata"] = NativeMetadata();
            detail["nativeCruiseExperimentCompiled"] = Compiled;
            detail["nativeCruiseExperimentRequested"] = Requested;
            detail["cruiseController"] = new Dictionary<string, object> {
                ["implementation"] = "Spear retains the complete named ALCM450 (otherwise exact CruiseMissile1) OpticalSeekerCruiseMissile root and its Initialize/Seek/terrain/terminal lifecycle. Pike retains its AShM1 native TerrainWaypoint helper at the native 0.5 s gate.",
                ["authority"] = "Spear has one native optical root seeker. Pike has one ARH root seeker plus native terrain waypoints, preserving its authored terminal boost/jink/CM window.",
                ["grouping"] = "Actual native local separation among registered friendly cruise missiles within 5 km; no global slots or ETA synchronization controller.",
                ["settings"] = "Spear retains native optical seeker settings. Pike retains source-authored cruise/terminal altitude and spacing; original native donor values are recorded separately."
            };
            Enabled = true;
            NaturalCruiseController.Completed += ControllerCompleted;
        }

        internal static void End()
        {
            NaturalCruiseController.Completed -= ControllerCompleted;
            Enabled = false;
        }

        private static void ControllerCompleted(NaturalCruiseController controller, string reason)
        {
            var evidence = (Dictionary<string, object>)controller.Capture();
            evidence["completion"] = reason;
            completedControllers.Add(evidence);
        }
        internal static void RecordOpticalWaypoint() { if (Enabled) opticalWaypoints++; }

        internal static void Validate(List<object> checks)
        {
            if (!Enabled) return;
            var rows = completedControllers.Cast<Dictionary<string, object>>().ToArray();
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-guidance-actually-observed",
                ["passed"] = opticalWaypoints > 0 || rows.Length > 0, ["spearNativeWaypointCalls"] = opticalWaypoints });
            // Spear-only trials have no custom controller. Their flight cases
            // assert native optical phases, terrain clearance and real impact.
            if (rows.Length == 0) return;
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-controller-actually-ran",
                ["passed"] = rows.Length > 0 && rows.All(r => (int)r["nativeMethodUpdates"] > 0) });
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-single-root-arh-seeker",
                ["passed"] = rows.Length > 0 && rows.All(r => ((string[])r["rootSeekerTypes"]).SequenceEqual(new[] { "ARHSeeker" })) });
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-preserves-native-update-cadence",
                ["passed"] = rows.Length > 0 && rows.All(r => r["minimumUpdateIntervalSeconds"] != null &&
                    (float)r["minimumUpdateIntervalSeconds"] >= .499f && (float)r["maximumUpdateIntervalSeconds"] < .6f) });
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-helper-aligned-and-not-autonomous",
                ["passed"] = rows.Length > 0 && rows.All(r => (string)r["helperType"] == "OpticalSeekerCruiseMissile" &&
                    !(bool)r["helperEnabled"] && ((float[])r["helperLocalPosition"]).All(v => Mathf.Abs(v) < .001f) &&
                    ((float[])r["helperLocalEulerAngles"]).All(v => Mathf.Abs(v) < .001f)) });
            checks.Add(new Dictionary<string, object> { ["name"] = "native-cruise-registration-balanced-on-disable",
                ["passed"] = rows.Length > 0 && rows.All(r => (int)r["registryAdds"] == 1 && (int)r["registryRemoves"] == 1) });
        }
        private static object NativeMetadata()
        {
            var rows = new List<object>();
            foreach (string key in new[] { "AShM1", "AShM2", "CruiseMissile1", "rsl_cruise" })
            {
                MissileDefinition definition = Encyclopedia.i.missiles.FirstOrDefault(d => d != null && d.jsonKey == key);
                GameObject prefab = definition != null ? definition.unitPrefab : null;
                MissileSeeker seeker = prefab != null ? prefab.GetComponent<MissileSeeker>() : null;
                var row = new Dictionary<string, object> { ["key"] = key, ["prefabFound"] = definition != null,
                    ["seekerType"] = seeker != null ? seeker.GetType().Name : null,
                    ["seekerDisplay"] = seeker != null ? seeker.GetSeekerType() : null,
                    ["prefabName"] = prefab != null ? prefab.name : null,
                    ["nativeUnitName"] = definition != null ? definition.unitName : null,
                    ["nativeCode"] = definition != null ? definition.code : null };
                if (seeker is OpticalSeekerCruiseMissile)
                {
                    var fields = new Dictionary<string, object>();
                    foreach (string name in new[] { "armRange", "altitudeTarget", "formationSpacing", "terminalRange", "terminalSearchRadius",
                        "finDelay", "tangibleDelay", "guidanceDelay", "maxTargetSpeed" }) fields[name] = NaturalWeapons.Get(seeker, name);
                    row["nativeCruiseFields"] = fields;
                }
                rows.Add(row);
            }
            return new Dictionary<string, object> { ["applicationVersion"] = Application.version,
                ["unityVersion"] = Application.unityVersion, ["gameDataPath"] = Application.dataPath, ["prefabs"] = rows };
        }

    }
}

