using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // These rules constrain our registered ammunition only. Native opportunity,
    // threat scoring, salvo size, cooldowns and ammunition accounting still run.
    internal static class NaturalMissileTargeting
    {
        private static readonly string[] Keys = { "rsl_ashm", "rsl_cruise", "rsl_lrsam", "rsl_mrsam",
            "rsl_bastion", "rsl_bmd", "rsl_bmd_exo", "rsl_pd", NaturalLanceFlight.Key };
        private static readonly FieldInfo LaunchPoints = AccessTools.Field(typeof(MissileLauncher), "launchTransforms");
        private static readonly FieldInfo CurrentCell = AccessTools.Field(typeof(MissileLauncher), "currentCell");
        private static readonly FieldInfo LaunchPoint = AccessTools.Field(typeof(MissileLauncher), "launchTransform");
        private static readonly FieldInfo InfraredSources = AccessTools.Field(typeof(Unit), "IRSources");

        internal static string Key(WeaponInfo info)
        {
            if (info == null) return null;
            foreach (string key in Keys)
            {
                WeaponInfo registered;
                if (NaturalWeapons.Infos.TryGetValue(key, out registered) && registered == info) return key;
            }
            return null;
        }

        internal static bool RoleAllows(string key, Unit target)
        {
            switch (key)
            {
                case "rsl_ashm": return target is Ship;
                case NaturalLanceFlight.Key: return target is Ship || StationaryGroundTarget(target);
                case "rsl_cruise": return target is Ship || target is Building || target is GroundVehicle;
                case "rsl_bmd": case "rsl_bmd_exo": return NaturalBallisticSelection.IsEligible(target);
                case "rsl_lrsam": case "rsl_mrsam": case "rsl_bastion": case "rsl_pd":
                    return target is Aircraft || target is Missile;
                default: return true;
            }
        }

        internal static bool ManualRoleAllows(string key, Unit target) =>
            key == "rsl_bmd" || key == "rsl_bmd_exo" ? NaturalBallisticSelection.IsCompatible(target) : RoleAllows(key, target);

        private static bool StationaryGroundTarget(Unit target)
        {
            if (target is Building) return true;
            if (!(target is GroundVehicle)) return false;
            float speed = target.rb != null ? Mathf.Max(Mathf.Abs(target.speed), target.rb.velocity.magnitude) : Mathf.Abs(target.speed);
            return Finite(speed) && speed <= 1f;
        }

        internal static bool Evaluate(WeaponInfo info, Unit owner, Unit target, Vector3 launchPosition,
            bool preference, out string reason, bool committedShot = false)
        {
            reason = "eligible";
            string key = Key(info);
            if (key == null) return true;
            if (owner == null || owner.disabled || owner.NetworkHQ == null) return Reject("unavailable-owner", out reason);
            if (target == null || target.disabled || target.definition == null || target.NetworkHQ == null ||
                target.NetworkHQ == owner.NetworkHQ) return Reject("not-live-hostile", out reason);
            if (!RoleAllows(key, target)) return Reject("excluded-target-role", out reason);
            if (!NaturalBallisticSelection.AutomaticAllows(info, owner, target))
                return Reject("outside-automatic-ballistic-phase", out reason);

            TargetRequirements requirements = info.targetRequirements;
            float range = (target.GlobalPosition() - launchPosition.ToGlobalPosition()).magnitude;
            float speed = target.rb != null ? Mathf.Max(target.speed, target.rb.velocity.magnitude) : target.speed;
            float altitude = target.radarAlt;
            if (!Finite(range) || !Finite(speed) || !Finite(altitude)) return Reject("invalid-target-state", out reason);
            // Enforce the configured envelope at selection AND at the physical
            // launch point. Native AnalyzeTarget otherwise never tests minRange.
            if (range < requirements.minRange) return Reject("below-minimum-range", out reason);
            if (range > requirements.maxRange) return Reject("beyond-maximum-range", out reason);
            if (altitude < requirements.minAltitude || altitude > requirements.maxAltitude)
                return Reject("outside-altitude-envelope", out reason);
            if (speed > requirements.maxSpeed) return Reject("above-maximum-target-speed", out reason);
            if (!Finite(target.definition.value) || target.definition.value < requirements.minValue)
                return Reject("below-minimum-target-value", out reason);
            GlobalPosition known;
            // Native HQ selection already requires a 100-m track. Once a shot
            // has been planned, honor its seeker's native acquisition tolerance
            // instead of cancelling every queued shot as that track ages.
            // Specialized T/X retain their existing gate.
            float trackTolerance = committedShot && (NaturalWeapons.UsesNativeAirDefenseProfile(key) ||
                key == "rsl_ashm" || key == "rsl_cruise" || key == NaturalLanceFlight.Key) ? 2000f : committedShot && key == "rsl_pd" ? 500f : 100f;
            if (!owner.NetworkHQ.TryGetKnownPosition(target, out known) ||
                !owner.NetworkHQ.IsTargetPositionAccurate(target, trackTolerance)) return Reject("unusable-datalink-track", out reason);

            // Bastion-X rides an observed datalink track before its own short
            // range infrared handoff; it must not require IR lock at launch.
            bool infrared = key == "rsl_pd";
            if (infrared)
            {
                // Match IRSeeker.Initialize rather than merely the serialized
                // minIR flag: a cold, obscured or inaccurate target loses lock
                // immediately and the native IR seeker cannot reacquire it.
                // Dart uses the same willingness to launch as the native IR
                // launcher. A flare can defeat acquisition through IRSeeker;
                // it must not prohibit every shot while any flare is present.
                if (key == "rsl_pd" ? !target.HasIRSignature() : !HasUnambiguousNativeIRSource(target))
                    return Reject("no-native-ir-acquisition", out reason);
                if (FastMath.OutOfRange(target.GlobalPosition(), known, 500f))
                    return Reject("outside-native-ir-track-error", out reason);
            }
            if ((infrared || requirements.lineOfSight) && !target.LineOfSight(launchPosition, 1000f))
                return Reject("no-launch-line-of-sight", out reason);
            if (requirements.minRadar > 0f && !target.HasRadarEmission()) return Reject("no-radar-emission", out reason);
            if (preference && key == "rsl_cruise" && target is Ship && !ResoluteEngagementDirector.Owns(owner) && AntiShipAvailable(owner, target))
                return Reject("naval-target-reserved-for-antiship-battery", out reason);
            return true;
        }

        internal static bool TryLaunchPosition(MissileLauncher launcher, out Vector3 position)
        {
            position = launcher != null ? launcher.transform.position : Vector3.zero;
            if (launcher == null || !launcher.isActiveAndEnabled || launcher.Safety || launcher.ammo <= 0 || !launcher.IsAttached()) return false;
            int index = (int)CurrentCell.GetValue(launcher);
            ResoluteVlsLauncher vls = launcher as ResoluteVlsLauncher;
            if (vls != null && vls.PhysicalCells != null && vls.PhysicalCells.Length > 0)
            {
                // Same occupied-cell order as ResoluteVlsLauncher.Fire. Its
                // native base call reaches our final gate after this selection.
                for (int step = 0; step < vls.PhysicalCells.Length; step++)
                {
                    int cell = (index + step) % vls.PhysicalCells.Length;
                    if (!vls.IsOccupied(cell)) continue;
                    ShipPart support = vls.CellOwners != null && cell < vls.CellOwners.Length ? vls.CellOwners[cell] : null;
                    if (support == null || support.IsDetached() || support.hitPoints <= 0f || vls.PhysicalCells[cell] == null) return false;
                    position = vls.PhysicalCells[cell].position;
                    return true;
                }
                return false;
            }
            UnitPart part = launcher.GetComponentInParent<UnitPart>();
            if (part != null && (part.IsDetached() || part.hitPoints <= 0f)) return false;
            Transform[] points = (Transform[])LaunchPoints.GetValue(launcher);
            Transform point = points != null && points.Length > 0 ? points[Mathf.Clamp(index, 0, points.Length - 1)] : (Transform)LaunchPoint.GetValue(launcher);
            if (point != null) position = point.position;
            return true;
        }

        internal static bool CanLaunch(MissileLauncher launcher, WeaponStation station, Unit owner, Unit target, out string reason)
        {
            reason = "eligible";
            if (launcher == null || Key(launcher.info) == null) return true;
            Vector3 position;
            if (!TryLaunchPosition(launcher, out position)) return Reject("unavailable-launcher", out reason);
            if (ResoluteCommandApi.IsManualFire(owner, station, target))
            {
                // Recheck the commanded type at the physical firing boundary,
                // but never re-enter automatic phase/value/range selection.
                string key = Key(launcher.info);
                return target == null && (key == "rsl_ashm" || key == "rsl_cruise" || key == NaturalLanceFlight.Key) ||
                    target != null && !target.disabled && ManualRoleAllows(key, target) ||
                    Reject("excluded-manual-target-role", out reason);
            }
            if (!ResoluteCommandApi.AllowAutomatic(owner, station, target)) return Reject("command-engagement-rule", out reason);
            if (!Evaluate(launcher.info, owner, target, position, true, out reason, true)) return false;
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            if (track == null) return Reject("missing-native-track", out reason);
            // QueuedAttack.attackers includes this shot and other reserved shots.
            // Do not count those reservations again or every salvo can cancel
            // itself. Already airborne missiles still satisfy native demand.
            if (track.missileAttacks >= Mathf.CeilToInt(launcher.info.CalcAttacksNeeded(target)) &&
                !(Key(launcher.info) == "rsl_ashm" && ResoluteStrikeOrders.IsAuthorizedLaunch(owner, target)))
                return Reject("native-attack-demand-already-covered", out reason);
            // The native controller already selected and reserved this shot.
            // Re-pricing it here re-enters our selection patch (including the
            // stricter fresh-track gate) and defeats the acquisition tolerance
            // above. Live role/envelope/LOS/track/demand checks still apply.
            return true;
        }

        private static bool AntiShipAvailable(Unit owner, Unit target)
        {
            Ship ship = owner as Ship;
            if (ship == null) return false;
            foreach (WeaponStation station in ship.weaponStations)
            {
                string key = Key(station.WeaponInfo);
                if (key != "rsl_ashm" && key != NaturalLanceFlight.Key) continue;
                foreach (Weapon weapon in station.Weapons)
                {
                    MissileLauncher launcher = weapon as MissileLauncher;
                    Vector3 point; string ignored;
                    if (!TryLaunchPosition(launcher, out point) || !Evaluate(station.WeaponInfo, owner, target, point, false, out ignored)) continue;
                    TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
                    if (track != null && CombatAI.AnalyzeTarget(station, owner, track).opportunity > 0f) return true;
                }
            }
            return false;
        }

        private static bool HasUnambiguousNativeIRSource(Unit target)
        {
            // GetIRSource randomly selects from sources within 100 m and mutates
            // its list. Inspect without consuming randomness or altering sensors.
            // Any remaining flare makes native initial acquisition uncertain.
            var sources = (List<IRSource>)InfraredSources.GetValue(target);
            bool found = false;
            foreach (IRSource source in sources)
            {
                if (source == null || source.transform == null) return false;
                if (FastMath.OutOfRange(source.transform.GlobalPosition(), target.GlobalPosition(), 100f)) continue;
                if (source.flare) return false;
                found = true;
            }
            return found;
        }

        private static bool Reject(string value, out string reason) { reason = value; return false; }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
    internal static class NaturalMissileOpportunityPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(WeaponStation weaponStation, Unit analyzer, TrackingInfo trackingInfo, ref OpportunityThreat __result)
        {
            if (weaponStation == null || NaturalMissileTargeting.Key(weaponStation.WeaponInfo) == null) return;
            Unit target;
            if (analyzer != null && trackingInfo != null && trackingInfo.TryGetUnit(out target))
                foreach (Weapon weapon in weaponStation.Weapons)
                {
                    Vector3 position; string reason = "unavailable-launcher";
                    bool allowed = NaturalMissileTargeting.TryLaunchPosition(weapon as MissileLauncher, out position) &&
                        NaturalMissileTargeting.Evaluate(weaponStation.WeaponInfo, analyzer, target, position, true, out reason);
                    NaturalMissileDiagnostics.Decision(0, weaponStation.WeaponInfo, target, allowed, reason, __result.opportunity);
                    if (allowed) return;
                }
            __result = new OpportunityThreat(0f, 0f);
            // CombinedScore is opportunity*(threat+1). Explicit exclusions also
            // clear threat so they cannot survive future native score changes.
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class NaturalMissileLaunchPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(MissileLauncher __instance, Unit owner, Unit target, WeaponStation weaponStation, out int __state)
        {
            __state = -1;
            string reason;
            bool allowed = NaturalMissileTargeting.CanLaunch(__instance, weaponStation, owner, target, out reason);
            NaturalMissileDiagnostics.Decision(1, __instance.info, target, allowed, reason);
            if (allowed && NaturalMissileTargeting.Key(__instance.info) != null)
                __state = (int)NaturalWeapons.Get(__instance, "ammo");
            return allowed;
        }
        private static void Postfix(MissileLauncher __instance, Unit target, int __state)
        {
            if (__state < 0) return;
            int after = (int)NaturalWeapons.Get(__instance, "ammo");
            bool fired = after < __state;
            string reason = after <= 0 ? "native-empty-launcher" :
                Time.timeSinceLevelLoad - (float)NaturalWeapons.Get(__instance, "lastFired") <
                    (float)NaturalWeapons.Get(__instance, "fireInterval") ? "native-launcher-cooldown" : "native-launch-not-consumed";
            NaturalMissileDiagnostics.Decision(3, __instance.info, target, fired, reason);
        }
    }

    // Cancel through the native queue so its reservation counter is released
    // normally. The launcher guard also covers direct calls outside that queue.
    [HarmonyPatch]
    internal static class NaturalMissileQueuedAttackPatch
    {
        private static readonly Type QueueType = AccessTools.Inner(typeof(FireControl), "QueuedAttack");
        private static MethodBase TargetMethod() { return AccessTools.Method(QueueType, "StillValid"); }
        private static void Postfix(WeaponStation ___weaponStation, Unit ___target, ref bool __result)
        {
            if (!__result) return;
            // The native queue entry is a private struct. Inject its reference
            // fields directly; boxing __instance produces invalid IL on Mono.
            WeaponStation station = ___weaponStation;
            if (station == null || NaturalMissileTargeting.Key(station.WeaponInfo) == null) return;
            Unit target = ___target;
            foreach (Weapon weapon in station.Weapons)
            {
                MissileLauncher launcher = weapon as MissileLauncher;
                string reason = "unavailable-launcher";
                bool allowed = launcher != null && NaturalMissileTargeting.CanLaunch(launcher, station, launcher.attachedUnit, target, out reason);
                NaturalMissileDiagnostics.Decision(2, station.WeaponInfo, target, allowed, reason);
                if (allowed) return;
            }
            __result = false;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "ARHSeeker_OnJam")]
    internal static class NaturalMissileHomeOnJamRolePatch
    {
        internal struct InvocationState
        {
            internal bool? HomeOnJam;
            internal bool Pike;
            internal Unit PreviousTarget;
        }

        private static void Prefix(ARHSeeker __instance, Unit.JamEventArgs e, out InvocationState __state)
        {
            __state = default(InvocationState);
            Missile missile = (Missile)NaturalWeapons.Get(__instance, "missile", typeof(MissileSeeker));
            string key = missile != null ? NaturalMissileTargeting.Key(missile.GetWeaponInfo()) : null;
            if (key == null || e.jammingUnit == null) return;
            __state.Pike = key == "rsl_ashm";
            if (__state.Pike) __state.PreviousTarget = (Unit)NaturalWeapons.Get(__instance, "targetUnit", typeof(MissileSeeker));
            if (NaturalMissileTargeting.RoleAllows(key, e.jammingUnit) && !e.jammingUnit.disabled &&
                e.jammingUnit.NetworkHQ != null && e.jammingUnit.NetworkHQ != missile.NetworkHQ &&
                WithinPassiveHomeOnJamRange(key, missile, e.jammingUnit)) return;
            __state.HomeOnJam = (bool)NaturalWeapons.Get(__instance, "homeOnJam");
            // Preserve native jam accumulation, attribution and receiver effects.
            // Only role-incompatible or out-of-profile HOJ selection is disabled.
            // This range gate does not invent received emissions or make the
            // receiver immune to native interference outside its HOJ envelope.
            NaturalWeapons.Set(__instance, "homeOnJam", false);
        }
        internal static bool WithinPassiveHomeOnJamRange(string key, Missile missile, Unit emitter)
        {
            if (key != "rsl_ashm") return true;
            NaturalWeaponPhase profile = missile.GetComponent<NaturalWeaponPhase>();
            float maximum = profile != null ? profile.PassiveHomeOnJamRange : 0f;
            float distance = (emitter.GlobalPosition() - missile.GlobalPosition()).magnitude;
            return !float.IsNaN(maximum) && !float.IsInfinity(maximum) && maximum > 0f &&
                !float.IsNaN(distance) && !float.IsInfinity(distance) && distance <= maximum;
        }
        private static void Postfix(ARHSeeker __instance, InvocationState __state)
        {
            Restore(__instance, __state.HomeOnJam);
            if (!__state.Pike) return;
            Unit selected = (Unit)NaturalWeapons.Get(__instance, "targetUnit", typeof(MissileSeeker));
            if (selected == null || selected == __state.PreviousTarget) return;
            Missile missile = (Missile)NaturalWeapons.Get(__instance, "missile", typeof(MissileSeeker));
            // Native HOJ owns this target change and its received-emitter fix.
            // Its former target's positive radar cache must not promote the
            // new emitter to active lock outside the active seeker envelope.
            // Keep native sampling deadlines, lock delay and memory untouched.
            NaturalWeapons.Set(__instance, "returnStrength", 0f);
            GlobalPosition known = (GlobalPosition)NaturalWeapons.Get(__instance, "knownPos");
            float activeRange = (float)NaturalWeapons.Get(__instance, "terminalRange");
            missile.NetworkseekerMode = (known - missile.GlobalPosition()).magnitude < activeRange
                ? Missile.SeekerMode.activeSearch : Missile.SeekerMode.passive;
        }
        private static Exception Finalizer(ARHSeeker __instance, InvocationState __state, Exception __exception)
        { Restore(__instance, __state.HomeOnJam); return __exception; }
        private static void Restore(ARHSeeker seeker, bool? state)
        { if (state.HasValue && seeker != null) NaturalWeapons.Set(seeker, "homeOnJam", state.Value); }
    }
}
