using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Calls the game's actual cruise navigation method.
    // ARH remains the only root seeker; the optical pipeline is never initialized.
    internal sealed class NaturalCruiseController : MonoBehaviour
    {
        private Missile missile;
        private OpticalSeekerCruiseMissile helper;
        private NaturalPikeFormation formation;
        private FactionHQ registeredHq;
        private GlobalPosition cachedWaypoint;
        private PersistentID target;
        private float lastUpdate = -1f, terminalRange;
        private bool cached, disabledCallback;
        private int updates, maximumNeighbors, registrationAdds, registrationRemoves;
        internal int UpdatesWithNeighbors { get; private set; }
        internal int MethodUpdates => updates;
        private float minimumInterval = float.MaxValue, maximumInterval, minimumThrottle = 1f, maximumThrottle;
        private readonly List<float> updateAges = new List<float>();
        internal static event Action<NaturalCruiseController, string> Completed;
        private bool evidenceRecorded;
        private string weaponKey;
        private uint missileId;
        private string terminalReleaseReason;
        private float terminalReleaseAge = -1f;
        internal bool CruiseActive { get; private set; }
        internal bool TerminalReleased { get; private set; }
        internal int NearbyMissiles { get; private set; }
        internal float Throttle => missile != null ? (float)NaturalWeapons.Get(missile, "throttle") : 1f;
        internal float FormationSpeedCeiling => !TerminalReleased && CruiseActive && formation != null
            ? formation.SpeedCeiling : float.PositiveInfinity;

        internal static NaturalCruiseController For(Missile missile)
        {
            if (missile == null || !missile.LocalSim) return null;
            var controller = missile.GetComponent<NaturalCruiseController>();
            if (controller != null)
            {
                controller.UpdateRegistration(); controller.SynchronizeTarget();
                return controller;
            }
            controller = missile.gameObject.AddComponent<NaturalCruiseController>();
            controller.missile = missile;
            controller.weaponKey = missile.definition != null ? missile.definition.jsonKey : missile.name;
            controller.missileId = missile.persistentID.Id;
            if (controller.weaponKey == "rsl_ashm") controller.formation = NaturalPikeFormation.For(missile);
            controller.UpdateRegistration(); controller.SynchronizeTarget();
            missile.onDisableUnit += controller.MissileDisabled;
            controller.disabledCallback = true;
            return controller;
        }

        private void CreateHelper(float altitude)
        {
            if (helper != null) return;
            MissileDefinition donor = Encyclopedia.i.missiles.FirstOrDefault(d => d != null && d.jsonKey == "AShM1");
            OpticalSeekerCruiseMissile source = donor != null ? donor.unitPrefab.GetComponent<OpticalSeekerCruiseMissile>() : null;
            if (source == null) throw new InvalidOperationException("Native cruise controller cannot find the actual AShM1 optical cruise helper.");
            var child = new GameObject("ResoluteNativeCruiseNavigation");
            child.transform.SetParent(transform, false);
            helper = child.AddComponent<OpticalSeekerCruiseMissile>();
            helper.enabled = false;
            NaturalWeapons.Set(helper, "missile", missile, typeof(MissileSeeker));
            // This native field is a repulsion gain, not the desired physical
            // lane spacing. Keep the real donor value; Pike's authored 185.2 m
            // lanes are owned by the formation coordinator.
            NaturalWeapons.Set(helper, "formationSpacing", NaturalWeapons.Get(source, "formationSpacing"));
            NaturalWeapons.Set(helper, "altitudeTarget", altitude);
            NaturalWeapons.Set(helper, "terrainClearVector", transform.forward * 1000f);
            NaturalWeapons.Set(helper, "altitudeTrim", 0f);
            terminalRange = (float)NaturalWeapons.Get(source, "terminalRange");
            PikeNavigationObstacles.Register(helper, missile);
        }

        internal void PrepareTerminal(float range, bool nativeTrackUsable, bool launchCleared)
        {
            if (TerminalReleased) return;
            if (missile == null || missile.disabled || missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile)) { Suspend(); return; }
            NaturalPikeGroupTargeting targeting = missile.GetComponent<NaturalPikeGroupTargeting>();
            if (weaponKey == "rsl_ashm" && targeting != null && targeting.CanRelease &&
                PikeFormationFlightPolicy.CanCommitTerminal(range, nativeTrackUsable, launchCleared))
                ReleaseTerminal("leader-assigned-independent-attack");
        }

        internal bool TryCruise(GlobalPosition destination, float altitude, float range, out GlobalPosition waypoint)
            => TryCruise(destination, altitude, range, true, out waypoint);

        internal bool TryCruise(GlobalPosition destination, float altitude, float range, bool lowCruise, out GlobalPosition waypoint)
        {
            waypoint = destination;
            if (missile == null || missile.disabled || missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile)) { Suspend(); return false; }
            UpdateRegistration();
            CreateHelper(altitude);
            SynchronizeTarget();
            // Pike uses its authored, latched 25 nm phase handoff prepared by
            // the flight profile. Other diagnostic callers retain native gates.
            if (weaponKey != "rsl_ashm" && PikeCruisePolicy.ReleaseForNativeTerminal(missile.seekerMode == Missile.SeekerMode.activeLock,
                missile.timeSinceSpawn, range, terminalRange))
                ReleaseTerminal("locked-native-donor-terminal-range");
            if (TerminalReleased) return false;
            CruiseActive = true;
            // Match native PreTerminalMode's elapsed-time gate. Its trim and
            // smoothing are not dt-scaled, so never call once per fixed seek.
            if (!cached || Time.timeSinceLevelLoad - lastUpdate >= .5f)
            {
                if (lastUpdate >= 0f)
                {
                    float interval = Time.timeSinceLevelLoad - lastUpdate;
                    minimumInterval = Mathf.Min(minimumInterval, interval); maximumInterval = Mathf.Max(maximumInterval, interval);
                }
                lastUpdate = Time.timeSinceLevelLoad;
                missile.UpdateRadarAlt();
                float coordinatedThrottle = 1f;
                GlobalPosition coordinatedDestination = formation != null
                    ? formation.Coordinate(destination, ref altitude, lowCruise,
                        range <= PikeFormationFlightPolicy.FinalApproachRange, out coordinatedThrottle) : destination;
                NaturalWeapons.Set(helper, "altitudeTarget", altitude);
                if (formation != null)
                {
                    cachedWaypoint = helper.TerrainWaypoint(coordinatedDestination);
                    // Native TerrainWaypoint owns spacing throttle. Longitudinal
                    // assembly is a speed ceiling consumed by the motor owner;
                    // a throttle cap alone cannot prevent reaching topSpeed.
                }
                else cachedWaypoint = helper.TerrainWaypoint(destination);
                cached = true; updates++;
                if (updateAges.Count < 512) updateAges.Add(missile.timeSinceSpawn);
                minimumThrottle = Mathf.Min(minimumThrottle, Throttle); maximumThrottle = Mathf.Max(maximumThrottle, Throttle);
                NearbyMissiles = formation != null ? Mathf.Max(0, formation.Members - 1) :
                    registeredHq == null ? 0 : registeredHq.GetCruiseMissiles().Count(m => m != null && m != missile &&
                    !m.disabled && FastMath.InRange(m.GlobalPosition(), missile.GlobalPosition(), 5000f));
                maximumNeighbors = Mathf.Max(maximumNeighbors, NearbyMissiles);
                if (NearbyMissiles > 0) UpdatesWithNeighbors++;
            }
            // ARH computes a fresh aim every seek; retain the native global
            // waypoint between native update ticks rather than moving it with us.
            waypoint = cachedWaypoint;
            return true;
        }

        internal void ReleaseTerminal(string reason = "locked-clear-surface-corridor")
        {
            SynchronizeTarget();
            if (!TerminalReleased)
            {
                terminalReleaseReason = reason; terminalReleaseAge = missile.timeSinceSpawn;
                missile.GetComponent<NaturalPikeTerminalAcceleration>()?.Begin(
                    missile.rb != null ? missile.rb.velocity.magnitude : missile.speed, Time.timeSinceLevelLoad);
            }
            CruiseActive = false; TerminalReleased = true;
            formation?.Release(true);
            if (missile != null && !missile.disabled) missile.SetThrottle(1f);
        }

        internal void Suspend()
        {
            CruiseActive = false; cached = false; lastUpdate = -1f; target = PersistentID.None;
            if (!TerminalReleased) { terminalReleaseReason = null; terminalReleaseAge = -1f; }
            formation?.Release(TerminalReleased);
            if (missile != null && !missile.disabled) missile.SetThrottle(1f);
        }

        private void SynchronizeTarget()
        {
            if (missile == null || target == missile.targetID) return;
            target = missile.targetID; cached = false; lastUpdate = -1f;
            CruiseActive = false;
            if (!TerminalReleased) { terminalReleaseReason = null; terminalReleaseAge = -1f; }
            if (helper == null) return;
            NaturalWeapons.Set(helper, "terrainClearVector", transform.forward * 1000f);
            NaturalWeapons.Set(helper, "altitudeTrim", 0f);
        }

        private void UpdateRegistration()
        {
            FactionHQ current = missile != null ? missile.NetworkHQ : null;
            if (current == registeredHq) return;
            RemoveRegistration();
            if (current != null)
            {
                if (current.GetCruiseMissiles().Contains(missile))
                    throw new InvalidOperationException("Native cruise requires one registry owner; missile is already registered.");
                current.RegisterCruiseMissile(missile); registrationAdds++; registeredHq = current;
            }
        }

        private void RemoveRegistration()
        {
            if (registeredHq == null) return;
            registeredHq.DeregisterCruiseMissile(missile); registrationRemoves++; registeredHq = null;
        }

        private void MissileDisabled(Unit unit)
        {
            CruiseActive = false; RemoveRegistration();
            PikeNavigationObstacles.Unregister(helper);
            if (disabledCallback && missile != null) missile.onDisableUnit -= MissileDisabled;
            disabledCallback = false;
            RecordCompletion("native-disable");
        }

        private void OnDestroy()
        {
            RemoveRegistration();
            PikeNavigationObstacles.Unregister(helper);
            if (disabledCallback && missile != null) missile.onDisableUnit -= MissileDisabled;
            RecordCompletion("fixture-destruction");
            if (helper != null) Object.Destroy(helper.gameObject);
        }

        private void RecordCompletion(string reason)
        {
            if (evidenceRecorded) return;
            evidenceRecorded = true;
            Completed?.Invoke(this, reason);
        }

        internal object Capture() => new Dictionary<string, object> {
            ["weaponKey"] = weaponKey, ["missileId"] = missileId, ["activeCruiseAuthority"] = CruiseActive, ["terminalReleased"] = TerminalReleased,
            ["terminalReleaseReason"] = terminalReleaseReason, ["terminalReleaseAgeSeconds"] = terminalReleaseAge,
            ["nativeMethodUpdates"] = updates, ["nativeUpdateAgesSeconds"] = updateAges.ToArray(),
            ["nativeUpdatesWithNeighbors"] = UpdatesWithNeighbors,
            ["minimumUpdateIntervalSeconds"] = minimumInterval < float.MaxValue ? (object)minimumInterval : null,
            ["maximumUpdateIntervalSeconds"] = maximumInterval,
            ["nativeMinimumThrottle"] = minimumThrottle, ["nativeMaximumThrottle"] = maximumThrottle,
            ["nearbyRegisteredCruiseMissiles"] = NearbyMissiles, ["maximumNearbyRegisteredCruiseMissiles"] = maximumNeighbors,
            ["registryAdds"] = registrationAdds, ["registryRemoves"] = registrationRemoves,
            ["rootSeekerTypes"] = GetComponents<MissileSeeker>().Where(s => s != null).Select(s => s.GetType().Name).ToArray(),
            ["helperType"] = helper != null ? helper.GetType().Name : null,
            ["helperEnabled"] = helper != null && helper.enabled,
            ["nativeHostileShipTerrainFilterActive"] = weaponKey == "rsl_ashm" && PikeNativeTerrainQueriesPatch.RedirectsActive,
            ["globalNavigationQuerySaturationFallbacks"] = PikeNavigationObstacles.SaturatedQueries,
            ["helperLocalPosition"] = helper != null ? V(helper.transform.localPosition) : null,
            ["helperLocalEulerAngles"] = helper != null ? V(helper.transform.localEulerAngles) : null,
            ["helperAltitudeTargetM"] = helper != null ? NaturalWeapons.Get(helper, "altitudeTarget") : null,
            ["helperFormationSpacingM"] = helper != null ? NaturalWeapons.Get(helper, "formationSpacing") : null,
            ["donorTerminalRangeM"] = terminalRange,
            ["formationSpeedCeilingMps"] = float.IsPositiveInfinity(FormationSpeedCeiling) ? null : (object)FormationSpeedCeiling,
            ["pikeFormation"] = formation != null ? formation.Capture() : null,
            ["cachedGlobalWaypoint"] = cached ? V(cachedWaypoint.AsVector3()) : null
        };

        internal object Sample() => new Dictionary<string, object> {
            ["cruiseAuthority"] = CruiseActive, ["terminalReleased"] = TerminalReleased,
            ["terminalReleaseReason"] = terminalReleaseReason, ["terminalReleaseAgeSeconds"] = terminalReleaseAge,
            ["nativeUpdates"] = updates, ["nativeThrottle"] = Throttle,
            ["formationSpeedCeilingMps"] = float.IsPositiveInfinity(FormationSpeedCeiling) ? null : (object)FormationSpeedCeiling,
            ["nearbyRegisteredCruiseMissiles"] = NearbyMissiles,
            ["pikeFormation"] = formation != null ? formation.Capture() : null,
            ["cachedGlobalWaypoint"] = cached ? V(cachedWaypoint.AsVector3()) : null,
            ["altitudeTrim"] = helper != null ? NaturalWeapons.Get(helper, "altitudeTrim") : null,
            ["radarAltitudeM"] = missile != null ? (object)missile.radarAlt : null
        };

        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
    }
}

