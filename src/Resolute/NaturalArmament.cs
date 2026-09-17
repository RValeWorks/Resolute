using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    internal static class NaturalArmament
    {
        private const float SourceScale = 0.014880937524139881f;
        internal static readonly Dictionary<string, WeaponInfo> GunInfos = new Dictionary<string, WeaponInfo>();
        // Preserved in the source asset catalog for future underwater support.
        // These definitions and their launch stations are not registered in game.
        internal static readonly HashSet<string> DisabledWeaponKeys = new HashSet<string>(StringComparer.Ordinal)
            { "rsl_torpedo", "rsl_asw_payload", "rsl_asw", "rsl_fulmar" };
        internal static bool IsEnabledWeapon(string key) => !DisabledWeaponKeys.Contains(key);
        internal static string ActiveVlsWeapon(string sourceKey) => IsEnabledWeapon(sourceKey) ? sourceKey : "rsl_ashm";

        internal static void Configure(Ship ship, Transform visual,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts, ManualLogSource log, string outfitKey)
        {
            string assets = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets");
            JObject data = JObject.Parse(File.ReadAllText(Path.Combine(assets, "Weapons", "weapons.json")));
            JObject source = JObject.Parse(File.ReadAllText(Path.Combine(assets, "source_bindings.json")));
            JObject anchors = JObject.Parse(File.ReadAllText(Path.Combine(assets, "weapon_anchors.json")));
            JObject outfit = data["outfits"].OfType<JObject>().SingleOrDefault(o => (string)o["key"] == outfitKey);
            if (outfit == null) throw new InvalidDataException("Unknown Resolute outfit: " + outfitKey);
            Dictionary<string, JObject> systems = outfit["systems"].OfType<JObject>().ToDictionary(s => (string)s["key"]);
            Dictionary<string, JObject> cells = anchors["banks"].SelectMany(b => b["cells"]).OfType<JObject>()
                .ToDictionary(c => "ResoluteLaunch_" + (string)c["hatch"]);
            var outfitCells = new Dictionary<string, string>();
            foreach (var system in systems)
                for (int n = 1; n <= (int)Number(system.Value, "NumberOfContainers", 0); n++)
                    outfitCells.Add("ResoluteLaunch_" + (string)system.Value["Container" + n + "_Hatch1"], system.Key);
            if (outfitCells.Count != 276 || outfitCells.Keys.Any(k => !cells.ContainsKey(k)))
                throw new InvalidDataException("The selected source outfit has missing or duplicate physical cells.");
            var vls = ship.GetComponentsInChildren<MissileLauncher>(true);
            MissileLauncher launcherPrototype = vls.First();
            // Split the original compatibility banks by the source's physical
            // cells. Each native station now owns exactly its authored magazine.
            foreach (MissileLauncher previous in vls)
            {
                Turret turret = ship.GetComponentsInChildren<Turret>(true)
                    .Single(t => t.GetWeaponStations().Any(s => s.Weapons.Contains(previous)));
                Transform[] points = Get<Transform[]>(previous, "launchTransforms");
                ResoluteVlsAnimation previousAnimation = previous.GetComponent<ResoluteVlsAnimation>();
                var groups = Enumerable.Range(0, points.Length).GroupBy(i => outfitCells[points[i].name]).ToArray();
                var stations = turret.GetWeaponStations().Where(s => !s.Weapons.Contains(previous)).ToList();
                foreach (var group in groups)
                {
                    JObject system = systems[group.Key];
                    string key = ActiveVlsWeapon((string)system["Ammunition"]);
                    int[] indexes = group.ToArray();
                    MissileLauncher launcher = NewLauncher(previous, previous.transform.parent, group.Key + "_" + key);
                    launcher.missile = NaturalWeapons.Definitions[key];
                    launcher.info = NaturalWeapons.Infos[key];
                    launcher.ammo = indexes.Length;
                    launcher.attachedUnit = ship;
                    Set(launcher, "fireInterval", key == "rsl_mrsam" ? .6f : Number(system, "SharedLaunchInterval", 1.5f));
                    Set(launcher, "reloadTime", 60f);
                    Set(launcher, "launchTransforms", indexes.Select(i => points[i]).ToArray());
                    Set(launcher, "ejectionVelocity", new Vector3(0, 0, 60));
                    NaturalDartLaunch coldLaunch = launcher.missile.unitPrefab.GetComponent<NaturalDartLaunch>();
                    if (coldLaunch != null) Set(launcher, "ejectionVelocity", coldLaunch.EjectionVelocity);
                    Set(launcher, "cellColumns", 0); Set(launcher, "cellRows", 0);
                    var animation = launcher.gameObject.AddComponent<ResoluteVlsAnimation>();
                    animation.Launcher = launcher;
                    animation.Hatches = indexes.Select(i => previousAnimation.Hatches[i]).ToArray();
                    animation.OpenEuler = indexes.Select(i => previousAnimation.OpenEuler[i]).ToArray();
                    animation.OpenSeconds = indexes.Select(i => previousAnimation.OpenSeconds[i]).ToArray();
                    stations.Add(Station(ship, launcher));
                }
                Set(turret, "weaponStations", stations.ToArray());
                if (Get<Weapon>(turret, "aimSafetyWeapon") == previous)
                    Set(turret, "aimSafetyWeapon", stations.Count > 0 ? stations[0].Weapons[0] : null);
                if (stations.Count == 0) turret.enabled = false;
                if (previousAnimation != null) Object.DestroyImmediate(previousAnimation);
                // Preserve the pivot/effects object: native turret elevation and
                // sound references can legitimately point at it.
                Object.DestroyImmediate(previous);
            }
            launcherPrototype = ship.GetComponentsInChildren<MissileLauncher>(true).First();

            ConfigureGuns(ship, source);
            JObject fixedSystems = (JObject)data["fixedSystems"];
            for (int n = 15; n <= 18; n++)
            {
                JObject system = (JObject)fixedSystems["WeaponSystem" + n];
                AddTrainableLauncher(ship, visual, launcherPrototype, system, (JObject)source["weaponDefinitions"]["rsl_pd25"],
                    "rsl_pd", 25, 25, 600f, ownership, weaponParts);
            }
            // Concealed torpedo hardware remains part of the source hull artwork;
            // no weapon or reload inventory is attached while underwater arms are disabled.
            int cellsTotal = ship.GetComponentsInChildren<ResoluteVlsAnimation>(true).Sum(a => a.Launcher.ammo);
            if (cellsTotal != 276) throw new InvalidDataException("Resolute outfit must load all 276 VLS cells.");
            NaturalFireControlGroups.Configure(ship);
            log.LogInfo("Resolute original armament installed: outfit=" + outfitKey +
                "; 276 loaded VLS cells (ASW cells reassigned to Pike), 4 Dart PD-25, 5 guns and 2 Halo lasers.");
        }

        private static void ConfigureGuns(Ship ship, JObject source)
        {
            foreach (Gun gun in ship.GetComponentsInChildren<Gun>(true))
            {
                Turret mount = ship.GetComponentsInChildren<Turret>(true)
                    .Single(t => t.GetWeaponStations().Any(s => s.Weapons.Contains(gun)));
                bool rail = mount.name == "turret_F";
                string key = rail ? "rsl_155mm" : "rsl_ciws_round";
                WeaponInfo info;
                if (!GunInfos.TryGetValue(key, out info))
                {
                    info = Object.Instantiate(gun.info); info.name = key;
                    info.weaponName = rail ? "155 mm HV Railgun" : "30 mm APFSDS CIWS";
                    info.shortName = rail ? "155 mm HV" : "30 mm CIWS";
                    info.description = rail ? "Resolute's 155 mm high-velocity railgun. 60 ready rounds, 600 carried; 24 rounds per minute at 1,800 m/s."
                        : "Resolute's close-in gun system. 2,400 ready rounds and 12,000 carried per mount; 6,000 rounds per minute.";
                    info.muzzleVelocity = rail ? 1800f : 1112f;
                    info.maxSpeed = info.muzzleVelocity;
                    info.fireInterval = rail ? 2.5f : .01f;
                    info.massPerRound = rail ? 41f : .4f;
                    info.costPerRound = rail ? 41f : .2f;
                    TargetRequirements limits = info.targetRequirements;
                    limits.maxRange = rail ? 70000f : 5000f; info.targetRequirements = limits;
                    Object.DontDestroyOnLoad(info); GunInfos.Add(key, info);
                }
                gun.info = info;
                if (rail) ResoluteRailgunAim.Configure(ship, mount, gun);
                // Reuse native detection callbacks and target ranking, but do
                // not make a close-in mount wait up to the inherited two
                // seconds before considering a newly detected threat.
                if (!rail) Set(mount, "targetAssessmentInterval", .25f);
                Set(gun, "fireRate", rail ? 24f : 6000f);
                Set(gun, "magazineCapacity", rail ? 60 : 2400);
                Set(gun, "magazines", rail ? 9 : 4);
                Set(gun, "startLoaded", true);
                Set(gun, "reloadTime", rail ? 5f : 40f);
                Set(gun, "heatEnabled", false);
                Set(gun, "bulletSelfDestruct", rail ? 80f : 5f);
                gun.ammo = rail ? 600 : 12000;
                foreach (WeaponStation station in mount.GetWeaponStations())
                    if (station.Weapons.Contains(gun)) station.WeaponInfo = info;
            }
            WeaponInfo halo;
            GunInfos.TryGetValue("rsl_laser_pulse", out halo);
            foreach (Laser laser in ship.GetComponentsInChildren<Laser>(true))
            {
                if (halo == null)
                {
                    halo = Object.Instantiate(laser.info); halo.name = "rsl_laser_pulse";
                    halo.weaponName = "Halo 120 Naval Laser"; halo.shortName = "Halo 120";
                    halo.description = "Resolute's 120 kW close-range defensive laser. Requires a clear firing path.";
                    halo.energy = true;
                    halo.massPerRound = 0f;
                    halo.costPerRound = 0f;
                    Object.DontDestroyOnLoad(halo); GunInfos["rsl_laser_pulse"] = halo;
                }
                laser.info = halo;
                Color beam = Get<Color>(laser, "color");
                float intensity = Mathf.Max(beam.r, Mathf.Max(beam.g, beam.b));
                Set(laser, "color", new Color(.04f * intensity, .25f * intensity, intensity, beam.a));
                float ratio = 120f / Mathf.Max(1f, Get<float>(laser, "power"));
                Set(laser, "power", 120f);
                Set(laser, "fireDamage", Get<float>(laser, "fireDamage") * ratio);
                Set(laser, "blastDamage", Get<float>(laser, "blastDamage") * ratio);
                ResoluteLaserAim.Configure(laser);
                foreach (WeaponStation station in laser.GetComponentInParent<Turret>(true).GetWeaponStations())
                    if (station.Weapons.Contains(laser)) station.WeaponInfo = halo;
            }
        }

        private static void AddTrainableLauncher(Ship ship, Transform visual, MissileLauncher prototype,
            JObject system, JObject mechanism, string key, int ready, int reserve, float reload,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts,
            string secondKey = null, int secondCount = 0)
        {
            string yaw = (string)system["Mount"];
            Transform sourceYaw = visual.GetComponentsInChildren<Transform>(true).Single(t => t.name == yaw);
            UnitPart support = ship.GetComponentsInChildren<ShipPart>(true)
                .Where(p => p.name.StartsWith("Hull", StringComparison.Ordinal))
                .OrderBy(p => (p.transform.position - sourceYaw.position).sqrMagnitude).First();
            ShipDefinition donor = Resources.FindObjectsOfTypeAll<ShipDefinition>().Single(d => d.jsonKey == "Destroyer1");
            Turret native = donor.unitPrefab.GetComponentsInChildren<Turret>(true).First(t => t.name == "CIWS_FR");
            GameObject copy = Object.Instantiate(native.gameObject, support.transform, false);
            copy.name = "Resolute_" + yaw;
            foreach (Renderer renderer in copy.GetComponentsInChildren<Renderer>(true))
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) renderer.enabled = false;
            foreach (UnitPart p in copy.GetComponentsInChildren<UnitPart>(true))
            { p.parentUnit = ship; p.rb = null; Set(p, "criticalPart", false); }
            Turret turret = copy.GetComponent<Turret>();
            Set(turret, "attachedUnit", ship); Set(turret, "fireControl", null);
            Turret ownSensorDonor = ship.GetComponentsInChildren<Turret>(true).First(t => t.name == "CIWS_FR");
            Set(turret, "targetDetectors", new List<TargetDetector>(Get<List<TargetDetector>>(ownSensorDonor, "targetDetectors")));
            // Assigned detectors keep the new mounts within native radar/IR
            // perception and fire-control opportunity checks.
            Set(turret, "targetAcquisitionMode", Enum.ToObject(Field(turret.GetType(), "targetAcquisitionMode").FieldType, 1));
            Set(turret, "traverseRate", Number(mechanism, "HorizontalDegreesPerSecond", 90));
            Set(turret, "elevationRate", Number(mechanism, "VerticalDegreesPerSecond", 90));
            Set(turret, "traverseRange", secondKey == null ? 90f : 20f);
            // MMR-S3's native engagement/reassessment rules: allow aircraft and
            // threats to the fleet, with native threat ranking and reservations.
            // onlyDefensive means exclusively missiles targeting this ship.
            Turret samRules = donor.unitPrefab.GetComponentsInChildren<Turret>(true).First(t => t.name == "SAM_F");
            Set(turret, "onlyDefensive", Get<bool>(samRules, "onlyDefensive"));
            Set(turret, "newTargetSearchAfterFire", Get<bool>(samRules, "newTargetSearchAfterFire"));
            Set(turret, "targetAssessmentInterval", Get<float>(samRules, "targetAssessmentInterval"));
            Set(turret, "firesWithoutAiming", secondKey != null);
            Transform elevation = Get<Transform>(turret, "elevationTransform") ?? copy.GetComponentInChildren<Gun>(true).transform;
            Set(turret, "elevationTransform", elevation);
            foreach (Weapon old in copy.GetComponentsInChildren<Weapon>(true)) Object.DestroyImmediate(old);
            var launcher = NewMagazineLauncher(prototype, elevation, yaw + "_" + key, ready, reserve, reload);
            launcher.transform.localPosition = Vector3.zero; launcher.transform.localRotation = Quaternion.identity;
            launcher.attachedUnit = ship; launcher.info = NaturalWeapons.Infos[key]; launcher.missile = NaturalWeapons.Definitions[key];
            Set(launcher, "fireInterval", secondKey == null ? launcher.info.fireInterval : 4f);
            Set(launcher, "ejectionVelocity", new Vector3(0, 0, secondKey == null ? 60 : 16));
            if (key == "rsl_pd")
                Set(launcher, "ejectionVelocity", launcher.missile.unitPrefab.GetComponent<NaturalDartLaunch>().EjectionVelocity);
            Set(launcher, "launchSound", null); Set(launcher, "launchParticles", null);
            var stations = new List<WeaponStation> { Station(ship, launcher) };
            if (secondKey != null)
            {
                var other = NewMagazineLauncher(prototype, elevation, yaw + "_" + secondKey, Math.Min(3, secondCount), Math.Max(0, secondCount - 3), reload);
                other.transform.localPosition = Vector3.zero; other.transform.localRotation = Quaternion.identity;
                other.attachedUnit = ship; other.info = NaturalWeapons.Infos[secondKey]; other.missile = NaturalWeapons.Definitions[secondKey];
                Set(other, "fireInterval", 4f); Set(other, "ejectionVelocity", new Vector3(0, 0, 16));
                Set(other, "launchSound", null); Set(other, "launchParticles", null);
                stations.Add(Station(ship, other));
            }
            Set(turret, "weaponStations", stations.ToArray()); Set(turret, "aimSafetyWeapon", launcher);
            ShipFactory.BindAdditionalRig(ship, turret, visual, yaw, ownership, weaponParts);
            int tubes = (int)Number(mechanism, "NumberOfAttachments", 1);
            var launchPoints = new List<Transform>();
            for (int n = 1; n <= tubes; n++)
            {
                var point = new GameObject("ResoluteTube_" + yaw + "_" + n).transform;
                point.SetParent(elevation, false);
                point.localPosition = SourceVector((string)mechanism["AttachmentPosition" + n]);
                // The authored tube coordinates refer to seated ammunition.
                // Put the projectile centre beyond the muzzle before flight.
                if (secondKey != null) point.localPosition += Vector3.forward * 3.2f;
                point.localRotation = Quaternion.identity; launchPoints.Add(point);
            }
            foreach (var station in stations)
            {
                Set(station.Weapons[0], "launchTransforms", launchPoints.ToArray());
                Set(station.Weapons[0], "cellColumns", 0); Set(station.Weapons[0], "cellRows", 0);
            }
            FiringCone cone = new FiringCone();
            Transform coneTransform = new GameObject("ResoluteMountArc_" + yaw).transform;
            coneTransform.SetParent(support.transform, false); coneTransform.rotation = turret.transform.rotation;
            Set(cone, "transform", coneTransform); Set(cone, "coneAngle", secondKey == null ? 95f : 60f); Set(cone, "exclusion", false);
            Set(turret, "firingCones", new[] { cone });
            if (key == "rsl_pd") ResoluteDartLaunchClearance.Configure(turret, launcher, elevation);
        }

        private static WeaponStation Station(Ship ship, Weapon weapon)
        {
            var station = new WeaponStation(ship, false, false, false, false)
            { Weapons = new List<Weapon> { weapon }, Turrets = new List<Turret>(), WeaponInfo = weapon.info,
              Ammo = weapon.GetAmmoTotal(), FullAmmo = weapon.GetAmmoTotal() };
            weapon.SetWeaponStation(station); return station;
        }
        private static MissileLauncher NewLauncher(MissileLauncher source, Transform parent, string name)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            go.transform.localPosition = source.transform.localPosition; go.transform.localRotation = source.transform.localRotation;
            var result = go.AddComponent<ResoluteVlsLauncher>(); CopySerialized(source, result); return result;
        }
        private static ResoluteMagazineLauncher NewMagazineLauncher(MissileLauncher source, Transform parent, string name, int ready, int reserve, float reload)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var result = go.AddComponent<ResoluteMagazineLauncher>(); CopySerialized(source, result);
            result.ammo = ready; result.ReserveAmmo = reserve; result.ReadyCapacity = ready;
            result.ReloadSeconds = reload; result.InitialTotal = ready + reserve; return result;
        }
        private static void CopySerialized(Component from, Component to)
        {
            for (Type type = from.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.IsInitOnly && field.DeclaringType.IsInstanceOfType(to) &&
                        (field.IsPublic || field.IsDefined(typeof(SerializeField), true))) field.SetValue(to, field.GetValue(from));
        }
        internal static FieldInfo Field(Type type, string name)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null) throw new MissingFieldException(type.FullName, name); return field;
        }
        internal static T Get<T>(object obj, string name) => (T)Field(obj.GetType(), name).GetValue(obj);
        internal static void Set(object obj, string name, object value) => Field(obj.GetType(), name).SetValue(obj, value);
        private static float Number(JObject obj, string key, float fallback)
        {
            float result; return float.TryParse((string)obj[key], NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
        private static Vector3 SourceVector(string text)
        {
            float[] a = text.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture) / SourceScale).ToArray();
            return new Vector3(a[0], a[1], a[2]);
        }
    }

    // A finite ready magazine and onboard reserve; reloading does not mint
    // ammunition. The game still owns launches, ownership, damage and networking.
    internal sealed class ResoluteMagazineLauncher : MissileLauncher
    {
        public int ReadyCapacity, ReserveAmmo, InitialTotal;
        public float ReloadSeconds;
        private float reloadUntil;
        private bool magazineReloading;
        private void OnEnable()
        {
            AccessTools.Method(typeof(MissileLauncher), "OnEnable").Invoke(this, null);
        }
        public override int GetFullAmmo() => InitialTotal;
        public override int GetAmmoTotal() => ammo + ReserveAmmo;
        public override float GetReloadProgress() => magazineReloading ? Mathf.Clamp01(1f - (reloadUntil - Time.time) / ReloadSeconds) : 0f;
        public override void Fire(Unit owner, Unit target, Vector3 inheritedVelocity, WeaponStation station, GlobalPosition aimpoint)
        {
            if (magazineReloading) return;
            base.Fire(owner, target, inheritedVelocity, station, aimpoint);
            if (ammo == 0 && ReserveAmmo > 0) BeginReload(station);
        }
        private void BeginReload(WeaponStation station)
        {
            weaponStation = station; magazineReloading = true; reloadUntil = Time.time + ReloadSeconds; ReportReloading(true);
        }
        private void Update()
        {
            if (!magazineReloading || Time.time < reloadUntil) return;
            int transferred = Math.Min(ReadyCapacity - ammo, ReserveAmmo); ammo += transferred; ReserveAmmo -= transferred;
            magazineReloading = false; ReportReloading(false); weaponStation?.AccountAmmo(); weaponStation?.Updated();
        }
        public override void Rearm(int count, WeaponStation station)
        {
            weaponStation = station;
            ReserveAmmo += Math.Min(Math.Max(0, count), Math.Max(0, InitialTotal - GetAmmoTotal()));
            if (ammo == 0 && ReserveAmmo > 0 && !magazineReloading) BeginReload(station);
        }
    }
}
