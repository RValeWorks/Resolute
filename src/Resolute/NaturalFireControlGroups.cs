using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native datalink FireControl evaluates the first subscribed ammunition and
    // distributes its accepted salvo across all subscribers. A separate native
    // controller for each ammunition keeps those native decisions consistent.
    internal sealed class NaturalFireControlGroups : MonoBehaviour
    {
        [Serializable]
        public sealed class Group
        {
            public WeaponInfo Info;
            public FireControl Controller;
        }
        public FireControl Original;
        public Group[] Groups;
        internal int RegisteredStations;
        internal int InitializationCallbacks, StationsAtInitialization;
        internal bool ServerAtInitialization;
        internal bool RebuiltFromClonedHierarchy;

        internal static void Configure(Ship ship)
        {
            FireControl donor = ship.GetComponentInChildren<FireControl>(true);
            if (donor == null) throw new InvalidOperationException("Resolute requires its native datalink fire control.");
            var router = ship.gameObject.AddComponent<NaturalFireControlGroups>();
            router.Original = donor;
            ResoluteEngagementDirector.Configure(ship);
            var groups = new List<Group>();
            WeaponInfo[] infos = ship.GetComponentsInChildren<ResoluteVlsAnimation>(true)
                .Select(a => a.Launcher.info).Distinct().OrderBy(i => i.name, StringComparer.Ordinal).ToArray();
            foreach (WeaponInfo info in infos)
            {
                var child = new GameObject("NativeFireControl_" + info.name);
                child.transform.SetParent(ship.transform, false);
                var controller = child.AddComponent<FireControl>();
                foreach (string field in new[] { "targetAcquisitionMode", "targetAssessmentInterval", "salvoInterval", "planningTimePerFire", "deployables" })
                    NaturalWeapons.Set(controller, field, NaturalWeapons.Get(donor, field));
                NaturalWeapons.Set(controller, "attachedUnit", ship);
                // The donor's 30-second anti-ship assessment and ten-second
                // planning pause are inappropriate for a defensive SAM battery.
                // Native selection, planning, salvo cadence and launch remain.
                if (info.effectiveness.antiAir > 0f || info.effectiveness.antiMissile > 0f)
                {
                    NaturalWeapons.Set(controller, "targetAssessmentInterval", 1f);
                    NaturalWeapons.Set(controller, "planningTimePerFire", .25f);
                }
                else if (SurfaceSalvoOrderPolicy.Applies(NaturalMissileTargeting.Key(info)))
                {
                    // Native PlanSalvo multiplies this pause by every planned
                    // attack. Ten seconds per cell can leave a large Pike salvo
                    // waiting minutes after a valid surface opportunity appears.
                    // Retain its native coordinated salvo and physical cadence.
                    NaturalWeapons.Set(controller, "targetAssessmentInterval", 2f);
                    NaturalWeapons.Set(controller, "planningTimePerFire", .1f);
                }
                if (NaturalMissileTargeting.Key(info) != null)
                {
                    // Native QueuedAttack consumes its reservation even when
                    // MissileLauncher.Fire returns during bank cooldown. The
                    // cloned .6s scheduler previously fed a 1.5s Spear bank,
                    // silently dropping two of every three planned launches.
                    // Keep native queue/accounting and schedule no faster than
                    // any physical bank in this ammunition group can accept.
                    float interval = (float)NaturalWeapons.Get(controller, "salvoInterval");
                    foreach (ResoluteVlsAnimation bank in ship.GetComponentsInChildren<ResoluteVlsAnimation>(true))
                        if (bank.Launcher.info == info)
                            interval = Mathf.Max(interval, (float)NaturalWeapons.Get(bank.Launcher, "fireInterval"));
                    NaturalWeapons.Set(controller, "salvoInterval", interval);
                }
                groups.Add(new Group { Info = info, Controller = controller });
            }
            router.Groups = groups.ToArray();
        }

        internal void RegisterInitializedStations(Ship ship)
        {
            InitializationCallbacks++;
            ServerAtInitialization = ship.IsServer;
            StationsAtInitialization = ship.weaponStations.Count;
            // Unity does not preserve this runtime-added component's custom
            // Group[] when it clones the assembled ship. The native child
            // components and their serialized fields do survive. Resolve the
            // mapping from this ship's hierarchy instead of prefab references.
            RebuildFromHierarchy(ship);
            if (!ship.IsServer) return;
            RegisteredStations = 0;
            var controllers = new Dictionary<WeaponInfo, FireControl>();
            foreach (Group group in Groups)
            {
                if (group == null || group.Info == null || group.Controller == null) continue;
                controllers.Add(group.Info, group.Controller);
                NaturalSurfaceSalvo.Register(group.Controller, group.Info);
                ((List<WeaponStation>)NaturalWeapons.Get(group.Controller, "subscribedWeaponStations")).Clear();
                NaturalWeapons.Set(group.Controller, "maxRange", 0f);
                NaturalWeapons.Set(group.Controller, "minRange", 0f);
            }
            var original = (List<WeaponStation>)NaturalWeapons.Get(Original, "subscribedWeaponStations");
            WeaponStation[] remaining = original.Where(s => !controllers.ContainsKey(s.WeaponInfo)).ToArray();
            // Native subscription caches the aggregate engagement ranges.
            // Removing a long-range station from its list alone would leave
            // HQ assessment scanning the original controller's stale range.
            original.Clear();
            NaturalWeapons.Set(Original, "maxRange", 0f);
            NaturalWeapons.Set(Original, "minRange", 0f);
            foreach (WeaponStation station in remaining) Original.SubscribeWeaponStation(station);
            foreach (WeaponStation station in ship.weaponStations)
            {
                FireControl controller;
                if (station.WeaponInfo == null || !controllers.TryGetValue(station.WeaponInfo, out controller)) continue;
                controller.SubscribeWeaponStation(station);
                RegisteredStations++;
            }
            ship.GetComponent<ResoluteEngagementDirector>().Register(ship, this);
        }

        private void RebuildFromHierarchy(Ship ship)
        {
            FireControl[] children = ship.GetComponentsInChildren<FireControl>(true);
            WeaponInfo[] infos = ship.GetComponentsInChildren<ResoluteVlsAnimation>(true)
                .Select(a => a.GetComponent<MissileLauncher>().info).Distinct()
                .OrderBy(i => i.name, StringComparer.Ordinal).ToArray();
            var resolved = new List<Group>();
            foreach (WeaponInfo info in infos)
            {
                string name = "NativeFireControl_" + info.name;
                FireControl controller = children.SingleOrDefault(c => c.transform.parent == ship.transform && c.name == name);
                if (controller == null || (Unit)NaturalWeapons.Get(controller, "attachedUnit") != ship)
                    throw new InvalidOperationException("Resolute cloned ship is missing its owned " + name + ".");
                resolved.Add(new Group { Info = info, Controller = controller });
            }
            if (Original == null || !Original.transform.IsChildOf(ship.transform))
                Original = children.FirstOrDefault(c => !resolved.Any(g => g.Controller == c));
            if (Original == null || (Unit)NaturalWeapons.Get(Original, "attachedUnit") != ship)
                throw new InvalidOperationException("Resolute cloned ship is missing its original native fire control.");
            Groups = resolved.ToArray();
            RebuiltFromClonedHierarchy = true;
        }

        internal FireControl ControllerFor(WeaponInfo info)
        {
            if (Groups == null) return null;
            for (int i = 0; i < Groups.Length; i++) if (Groups[i].Info == info) return Groups[i].Controller;
            return null;
        }
    }

    // The complete native ship callback is a stable boundary after every
    // onInitialize subscriber has added its station. The tiny inherited
    // Unit.InitializeUnit event dispatcher can be inlined by Mono.
    [HarmonyPatch(typeof(Ship), "OnStartClient")]
    internal static class NaturalFireControlInitializationPatch
    {
        private static void Postfix(Ship __instance)
        {
            NaturalFireControlGroups groups = __instance.GetComponent<NaturalFireControlGroups>();
            if (groups != null) groups.RegisterInitializedStations(__instance);
        }
    }
}
