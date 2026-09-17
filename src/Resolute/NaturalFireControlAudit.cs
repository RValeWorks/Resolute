using System;
using System.Collections.Generic;
using System.Linq;

namespace Resolute
{
    // Read-only prefab/live checks of the native controller ownership graph.
    internal static class NaturalFireControlAudit
    {
        private static readonly string[] Keys = {
            "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam", "rsl_bastion", "rsl_bmd", "rsl_bmd_exo"
        };

        internal static object CaptureLiveState(Ship ship)
        {
            NaturalFireControlGroups router = ship.GetComponent<NaturalFireControlGroups>();
            ResoluteEngagementDirector director = ship.GetComponent<ResoluteEngagementDirector>();
            return new Dictionary<string, object> {
                ["shipIsServer"] = ship.IsServer, ["shipLocalSim"] = ship.LocalSim,
                ["routerPresent"] = router != null, ["shipStationCount"] = ship.weaponStations.Count,
                ["initializationCallbacks"] = router != null ? router.InitializationCallbacks : 0,
                ["serverAtInitialization"] = router != null && router.ServerAtInitialization,
                ["stationsAtInitialization"] = router != null ? router.StationsAtInitialization : 0,
                ["registeredStations"] = router != null ? router.RegisteredStations : 0,
                ["rebuiltFromClonedHierarchy"] = router != null && router.RebuiltFromClonedHierarchy,
                ["engagementDirectorPresent"] = director != null,
                ["engagementDirector"] = director != null ? director.Capture() : null,
                ["fixedVlsTurrets"] = FixedVlsTurrets(ship).Select(t => new Dictionary<string, object> {
                    ["turret"] = t.name,
                    ["targetAcquisitionMode"] = NaturalWeapons.Get(t, "targetAcquisitionMode").ToString(),
                    ["usesOriginalController"] = router != null && (FireControl)NaturalWeapons.Get(t, "fireControl") == router.Original,
                    ["assignedDetectorCount"] = ((List<TargetDetector>)NaturalWeapons.Get(t, "targetDetectors"))?.Count ?? 0
                }).ToArray(),
                ["originalController"] = router != null ? CaptureOriginalState(router) : null,
                ["stationInfos"] = ship.weaponStations.Select(s => s.WeaponInfo != null ? s.WeaponInfo.name : "null").ToArray(),
                ["groups"] = router != null && router.Groups != null ? router.Groups.Select(g => new Dictionary<string, object> {
                    ["info"] = g != null && g.Info != null ? g.Info.name : "null",
                    ["infoIsRegisteredReference"] = g != null && g.Info != null && NaturalWeapons.Infos.Values.Contains(g.Info),
                    ["controller"] = g != null && g.Controller != null ? g.Controller.name : "null",
                    ["attachedToThisShip"] = g != null && g.Controller != null && (Unit)NaturalWeapons.Get(g.Controller, "attachedUnit") == ship,
                    ["subscribedStations"] = g != null && g.Controller != null ?
                        ((List<WeaponStation>)NaturalWeapons.Get(g.Controller, "subscribedWeaponStations")).Select(s => s.WeaponInfo != null ? s.WeaponInfo.name : "null").ToArray() : new string[0]
                }).ToArray() : new object[0]
            };
        }

        internal static object Validate(Ship ship, List<object> checks, string label, bool live)
        {
            string prefix = label + ":native-vls-control-";
            NaturalFireControlGroups router = ship.GetComponent<NaturalFireControlGroups>();
            ResoluteEngagementDirector director = ship.GetComponent<ResoluteEngagementDirector>();
            Require(checks, prefix + "seven-distinct-groups", router != null && router.Groups != null && router.Groups.Length == 7 &&
                router.Groups.All(g => g != null && g.Info != null && g.Controller != null) &&
                router.Groups.Select(g => g.Controller).Distinct().Count() == 7 &&
                router.Groups.Select(g => g.Info).Distinct().Count() == 7);
            var launchers = ship.GetComponentsInChildren<ResoluteVlsAnimation>(true).Select(a => a.Launcher).ToArray();
            WeaponStation[] stations = live ? ship.weaponStations.ToArray() : ship.GetComponentsInChildren<Turret>(true)
                .SelectMany(t => t.GetWeaponStations()).Distinct().ToArray();
            WeaponStation[] expected = stations.Where(s => s.Weapons.Any(w => launchers.Contains(w))).ToArray();
            Require(checks, prefix + "authored-fixed-vls-bank-partition", NaturalVlsLayoutAudit.Validate(ship, expected));
            Require(checks, prefix + "every-vls-launcher-has-one-owned-station", launchers.Length > 0 &&
                launchers.All(l => l != null && l.attachedUnit == ship && stations.Count(s => s.Weapons.Contains(l)) == 1) &&
                expected.All(s => s.Weapons.All(w => w != null && w.info == s.WeaponInfo && launchers.Contains(w))));
            Require(checks, prefix + "original-controller-owned", router.Original != null && router.Original.transform.IsChildOf(ship.transform) &&
                (Unit)NaturalWeapons.Get(router.Original, "attachedUnit") == ship &&
                router.Groups.All(g => g.Controller != router.Original));
            Turret[] fixedTurrets = FixedVlsTurrets(ship);
            Require(checks, prefix + "one-native-decision-owner-for-fixed-vls", fixedTurrets.Length > 0 &&
                expected.All(s => fixedTurrets.Count(t => t.GetWeaponStations().Contains(s)) == 1) &&
                fixedTurrets.All(t => NaturalWeapons.Get(t, "targetAcquisitionMode").ToString() == "fireControl" &&
                    (FireControl)NaturalWeapons.Get(t, "fireControl") == router.Original &&
                    NaturalWeapons.Get(t, "targetDetectors") is List<TargetDetector> detectors && detectors.Count == 0));
            Require(checks, prefix + "engagement-director-present", director != null);
            var rows = new List<object>();
            foreach (string key in Keys)
            {
                WeaponInfo info = NaturalWeapons.Infos[key];
                NaturalFireControlGroups.Group group = router.Groups.SingleOrDefault(g => g.Info == info);
                WeaponStation[] wanted = expected.Where(s => s.WeaponInfo == info).ToArray();
                Require(checks, prefix + key + "-exact-weapon-info", group != null && wanted.Length > 0 &&
                    router.ControllerFor(info) == group.Controller &&
                    launchers.Where(l => l.info == info).All(l => l.missile == NaturalWeapons.Definitions[key]) &&
                    group.Controller.GetType() == typeof(FireControl) && group.Controller.transform.IsChildOf(ship.transform) &&
                    (Unit)NaturalWeapons.Get(group.Controller, "attachedUnit") == ship);
                FireControl controller = group.Controller;
                var subscribed = (List<WeaponStation>)NaturalWeapons.Get(controller, "subscribedWeaponStations");
                string mode = NaturalWeapons.Get(controller, "targetAcquisitionMode").ToString();
                float interval = (float)NaturalWeapons.Get(controller, "targetAssessmentInterval");
                float planning = (float)NaturalWeapons.Get(controller, "planningTimePerFire");
                bool defensive = info.effectiveness.antiAir > 0f || info.effectiveness.antiMissile > 0f;
                Require(checks, prefix + key + "-native-datalink-cadence", mode == "datalink" &&
                    (!defensive || Math.Abs(interval - 1f) < .001f &&
                        Math.Abs(planning - .25f / (live ? Math.Max(ship.skill, .1f) : 1f)) < .001f));
                if (SurfaceSalvoOrderPolicy.Applies(key))
                    Require(checks, prefix + key + "-responsive-coordinated-planning", Math.Abs(interval - 2f) < .001f &&
                        Math.Abs(planning - .1f / (live ? Math.Max(ship.skill, .1f) : 1f)) < .001f);
                if (live)
                    Require(checks, prefix + key + "-live-exclusive-subscriptions", subscribed.Count == wanted.Length &&
                        subscribed.Distinct().Count() == wanted.Length && wanted.All(subscribed.Contains) &&
                        subscribed.All(s => s.WeaponInfo == info) &&
                        Math.Abs((float)NaturalWeapons.Get(controller, "maxRange") - info.targetRequirements.maxRange) < .1f);
                rows.Add(new Dictionary<string, object> {
                    ["weaponKey"] = key, ["weaponInfo"] = info.name, ["controller"] = controller.name,
                    ["expectedStations"] = wanted.Length, ["subscribedStations"] = subscribed.Count,
                    ["expectedLaunchers"] = launchers.Count(l => l.info == info),
                    ["targetAcquisitionMode"] = mode, ["assessmentIntervalSeconds"] = interval,
                    ["planningSecondsPerShot"] = planning, ["maxRangeMetres"] = NaturalWeapons.Get(controller, "maxRange")
                });
            }
            if (live)
            {
                var originalSubscriptions = (List<WeaponStation>)NaturalWeapons.Get(router.Original, "subscribedWeaponStations");
                Require(checks, prefix + "original-range-matches-remaining-stations",
                    Math.Abs((float)NaturalWeapons.Get(router.Original, "maxRange") - ExpectedOriginalRange(originalSubscriptions)) < .1f &&
                    Math.Abs((float)NaturalWeapons.Get(router.Original, "minRange")) < .1f);
                var routed = router.Groups.SelectMany(g => (List<WeaponStation>)NaturalWeapons.Get(g.Controller, "subscribedWeaponStations")).ToArray();
                Require(checks, prefix + "live-all-stations-routed-once", router.RegisteredStations == expected.Length &&
                    routed.Length == expected.Length && routed.Distinct().Count() == expected.Length && expected.All(routed.Contains) &&
                    !originalSubscriptions.Any(expected.Contains));
                FireControl[] others = ship.GetComponentsInChildren<FireControl>(true).Where(c => !router.Groups.Any(g => g.Controller == c)).ToArray();
                Require(checks, prefix + "live-no-other-controller-owns-vls", others.All(c =>
                    !((List<WeaponStation>)NaturalWeapons.Get(c, "subscribedWeaponStations")).Any(expected.Contains)));
                Require(checks, prefix + "live-director-owns-all-seven-batteries", router.Groups.All(g => {
                    ResoluteEngagementDirector owner;
                    return ResoluteEngagementDirector.TryGet(g.Controller, out owner) && owner == director;
                }));
            }
            return new Dictionary<string, object> {
                ["scope"] = live ? "live-native-subscriptions" : "serialized-prefab-routing",
                ["shipKey"] = ship.definition.jsonKey, ["groupCount"] = router.Groups.Length,
                ["vlsStationCount"] = expected.Length, ["vlsLauncherCount"] = launchers.Length,
                ["fixedVlsTurretCount"] = fixedTurrets.Length,
                ["engagementDirectorPresent"] = director != null,
                ["registeredStations"] = router.RegisteredStations, ["groups"] = rows
            };
        }

        private static Turret[] FixedVlsTurrets(Ship ship)
        {
            return ship.GetComponentsInChildren<Turret>(true).Where(t => {
                var weapons = t.GetWeaponStations().SelectMany(s => s.Weapons).ToArray();
                return weapons.Length > 0 && weapons.All(w => w != null && w.GetComponent<ResoluteVlsAnimation>() != null);
            }).ToArray();
        }

        private static object CaptureOriginalState(NaturalFireControlGroups router)
        {
            if (router.Original == null) return null;
            var stations = (List<WeaponStation>)NaturalWeapons.Get(router.Original, "subscribedWeaponStations");
            return new Dictionary<string, object> {
                ["name"] = router.Original.name,
                ["subscribedStations"] = stations.Select(s => s.WeaponInfo != null ? s.WeaponInfo.name : "null").ToArray(),
                ["maxRangeMetres"] = NaturalWeapons.Get(router.Original, "maxRange"),
                ["minRangeMetres"] = NaturalWeapons.Get(router.Original, "minRange"),
                ["expectedMaxRangeMetres"] = ExpectedOriginalRange(stations)
            };
        }

        private static float ExpectedOriginalRange(List<WeaponStation> stations)
        {
            return stations.Count == 0 ? 0f : stations.Max(s => s.Weapons[0].info.targetRequirements.maxRange);
        }

        private static void Require(List<object> checks, string name, bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("Native VLS controller audit failed: " + name);
        }
    }
}
