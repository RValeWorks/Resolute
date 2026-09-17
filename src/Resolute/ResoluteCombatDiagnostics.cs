using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Resolute
{
    // Optional read-only bridge for the separate recorder. Called only on the
    // game thread; no global object scans, target selection or physics queries.
    public sealed class ResoluteCombatDiagnostics : MonoBehaviour
    {
        private static readonly List<ResoluteCombatDiagnostics> Live = new List<ResoluteCombatDiagnostics>();
        private static readonly Dictionary<string, FieldInfo> TurretFields = Fields(typeof(Turret),
            "target", "currentWeaponStation", "onTarget", "timeOnTarget", "lockTime", "traverseError", "elevationError", "stowed", "disabled");
        private static readonly Dictionary<string, FieldInfo> ControlFields = Fields(typeof(FireControl), "planningSalvo", "queuedAttacks");
        private Ship ship;
        private Turret[] mounts;
        private Dictionary<Turret, ResoluteCiwsAim> ciws;
        private FireControl[] controls;
        private ResoluteShipEcm ecm;
        private ResoluteLiferafts rafts;
        private ResoluteStructureDiagnostics structure;

        private static Dictionary<string, FieldInfo> Fields(Type type, params string[] names)
        {
            var result = new Dictionary<string, FieldInfo>();
            foreach (string name in names) result.Add(name, NaturalArmament.Field(type, name));
            return result;
        }
        private void Awake()
        {
            ship = GetComponent<Ship>();
            mounts = GetComponentsInChildren<Turret>(true);
            ciws = new Dictionary<Turret, ResoluteCiwsAim>();
            foreach (Turret mount in mounts)
            {
                ResoluteCiwsAim binding = mount.GetComponent<ResoluteCiwsAim>();
                if (binding != null) ciws.Add(mount, binding);
            }
            controls = GetComponentsInChildren<FireControl>(true);
            ecm = GetComponent<ResoluteShipEcm>(); rafts = GetComponent<ResoluteLiferafts>();
            structure = GetComponent<ResoluteStructureDiagnostics>();
        }
        private void OnEnable() { if (!Live.Contains(this)) Live.Add(this); }
        private void OnDisable() { Live.Remove(this); }
        private void OnDestroy() { Live.Remove(this); }

        public static object Capture()
        {
            var ships = new List<object>(Math.Min(16, Live.Count));
            foreach (ResoluteCombatDiagnostics item in Live)
            {
                if (ships.Count == 16) break;
                if (item == null || item.ship == null || item.mounts == null) continue;
                var mounts = new List<object>(item.mounts.Length);
                foreach (Turret mount in item.mounts)
                {
                    if (mount == null) continue;
                    Unit target = (Unit)TurretFields["target"].GetValue(mount);
                    WeaponStation station = (WeaponStation)TurretFields["currentWeaponStation"].GetValue(mount);
                    ResoluteCiwsAim aimBinding;
                    item.ciws.TryGetValue(mount, out aimBinding);
                    mounts.Add(new {
                        name = mount.name, active = mount.enabled && mount.gameObject.activeInHierarchy,
                        disabled = (bool)TurretFields["disabled"].GetValue(mount), stowed = (bool)TurretFields["stowed"].GetValue(mount),
                        weapon = station?.WeaponInfo != null ? station.WeaponInfo.name : null,
                        readyAmmo = station != null ? station.Ammo : 0, reloading = station != null && station.Reloading,
                        lastFiredGameTime = station != null ? station.LastFiredTime : 0f,
                        targetId = target != null ? target.persistentID.ToString() : null,
                        targetType = target?.definition != null ? target.definition.jsonKey : null,
                        rangeMetres = target != null ? (target.transform.position - mount.transform.position).magnitude : 0f,
                        onTarget = (bool)TurretFields["onTarget"].GetValue(mount),
                        timeOnTarget = (float)TurretFields["timeOnTarget"].GetValue(mount),
                        lockTime = (float)TurretFields["lockTime"].GetValue(mount),
                        traverseError = (float)TurretFields["traverseError"].GetValue(mount),
                        elevationError = (float)TurretFields["elevationError"].GetValue(mount),
                        ciwsAim = aimBinding != null ? aimBinding.Capture(target) : null
                    });
                }
                var controls = new List<object>(item.controls.Length);
                foreach (FireControl control in item.controls)
                {
                    if (control == null) continue;
                    controls.Add(new { name = control.name, planning = (bool)ControlFields["planningSalvo"].GetValue(control),
                        queued = ((ICollection)ControlFields["queuedAttacks"].GetValue(control)).Count });
                }
                ships.Add(new { id = item.ship.persistentID.ToString(), key = item.ship.definition.jsonKey,
                    disabled = item.ship.disabled, server = item.ship.IsServer, localSimulation = item.ship.LocalSim,
                    speed = item.ship.speed, mounts, controls,
                    ecmChannels = item.ecm != null ? item.ecm.ActiveChannels : 0,
                    nativeJamCalls = item.ecm != null ? item.ecm.NativeJamCalls : 0,
                    raftStations = item.rafts != null ? item.rafts.AuthoredStations : 0,
                    raftsSpawned = item.rafts != null ? item.rafts.SpawnedRafts : 0,
                    raftStationsSkipped = item.rafts != null ? item.rafts.SkippedStations : 0,
                    engagement = item.ship.GetComponent<ResoluteEngagementDirector>()?.Capture(),
                    structure = item.structure != null ? item.structure.Capture() : null });
            }
            return new { gameTime = Time.timeSinceLevelLoad, ships, omittedShips = Math.Max(0, Live.Count - ships.Count),
                missiles = NaturalMissileDiagnostics.Capture(),
                scope = "Read-only snapshots, max16 Resolute ships; native fields may retain last-target values when idle. No per-method/GPU timing." };
        }
    }
}
