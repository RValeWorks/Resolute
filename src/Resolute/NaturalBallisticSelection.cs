using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Zenith's reference gate is minValue=25. A separate positive guidance
    // classification prevents costly aircraft or ordinary missiles qualifying.
    internal static class NaturalBallisticSelection
    {
        internal const float MinimumValue = 25f;
        internal const float MinimumAryxLoftMetres = 10000f;
        internal const float MinimumAryxDiveDegrees = 45f;
        internal const float ExoAirDensity = .1f;
        internal const float TerminalSelectionRange = 25f * 1852f;
        // This is an observed flight-history fact, not access to an unseen
        // missile's trajectory. Weak keys retire naturally with native tracks.
        private sealed class FlightHistory { internal bool LeftAtmosphere; }
        private static readonly ConditionalWeakTable<TrackingInfo, FlightHistory> Histories =
            new ConditionalWeakTable<TrackingInfo, FlightHistory>();

        internal static void ObserveAltitude(TrackingInfo track, GlobalPosition position)
        {
            if (track == null || !OutsideAerodynamicAtmosphere(position.y)) return;
            Unit target;
            if (track.TryGetUnit(out target) && IsEligible(target))
                Histories.GetValue(track, CreateHistory).LeftAtmosphere = true;
        }
        private static FlightHistory CreateHistory(TrackingInfo track) { return new FlightHistory(); }

        internal static bool AutomaticAllows(WeaponInfo weapon, Unit owner, Unit target)
        {
            if (!IsBastion(weapon)) return true;
            if (!IsEligible(target) || owner == null || owner.NetworkHQ == null) return false;
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            if (track == null || !track.Observed()) return false;
            GlobalPosition observed = track.GetPosition();
            ObserveAltitude(track, observed);
            string key = NaturalMissileTargeting.Key(weapon);
            FlightHistory history;
            bool previous = Histories.TryGetValue(track, out history) && history.LeftAtmosphere;
            return InterceptorFlightPolicy.AutomaticPhase(key == "rsl_bmd", true, previous,
                OutsideAerodynamicAtmosphere(observed.y), (observed - owner.GlobalPosition()).magnitude, TerminalSelectionRange);
        }
        internal static bool OutsideAerodynamicAtmosphere(float altitude)
        {
            // Match native BallisticMissileGuidance's coast/RCS transition.
            // A real-world Karman altitude would exclude the game's shorter
            // ballistic arcs; use the installed game's own density curve.
            return Finite(altitude) && altitude >= 0f && GameAssets.i != null &&
                GameAssets.i.airDensityAltitude != null &&
                GameAssets.i.airDensityAltitude.Evaluate(altitude * .001f) <= ExoAirDensity;
        }
        private const string AryxSeekerType = "AryxWeaponryExpansion.AryxInertialSeeker";

        private sealed class Profile
        {
            internal GameObject Prefab;
            internal bool Ballistic;
            internal string Reason = "no-positive-ballistic-guidance";
            internal string GuidanceType = "";
            internal float LoftMetres, DiveDegrees;
            internal float CheckedAt;
        }

        private sealed class AryxFields
        {
            internal readonly FieldInfo Loft, Dive;
            internal AryxFields(Type type)
            {
                Loft = AccessTools.Field(type, "cruiseAltitude");
                Dive = AccessTools.Field(type, "terminalDiveAngle");
            }
        }

        private static readonly Dictionary<UnitDefinition, Profile> Profiles = new Dictionary<UnitDefinition, Profile>();
        private static readonly Dictionary<Type, AryxFields> SeekerFields = new Dictionary<Type, AryxFields>();
        private static readonly Profile MissingPrefab = new Profile { Reason = "missing-missile-prefab" };

        internal static bool IsBastion(WeaponInfo info)
        {
            if (info == null) return false;
            WeaponInfo owned;
            return NaturalWeapons.Infos.TryGetValue("rsl_bmd", out owned) && info == owned ||
                NaturalWeapons.Infos.TryGetValue("rsl_bmd_exo", out owned) && info == owned;
        }

        internal static bool IsEligible(Unit target)
        {
            return IsCompatible(target) && Finite(target.definition.value) && target.definition.value >= MinimumValue;
        }

        // Compatibility is a weapon/type rule. Cost, observed phase and range
        // are automatic expenditure rules and must not veto an explicit order.
        internal static bool IsCompatible(Unit target) => target is Missile && !target.disabled &&
            target.definition is MissileDefinition && GetProfile(target.definition).Ballistic;

        internal static bool IsEligibleDefinition(UnitDefinition definition)
        {
            return definition is MissileDefinition && Finite(definition.value) &&
                definition.value >= MinimumValue && GetProfile(definition).Ballistic;
        }

        internal static string EligibilityReason(Unit target)
        {
            if (target == null) return "missing-target";
            if (!(target is Missile)) return "not-a-missile";
            if (target.disabled) return "disabled-target";
            if (target.definition == null) return "missing-definition";
            if (!Finite(target.definition.value)) return "invalid-target-value";
            if (target.definition.value < MinimumValue) return "below-minimum-value";
            return GetProfile(target.definition).Reason;
        }

        internal static Dictionary<string, object> CaptureClassification(Unit target)
        {
            UnitDefinition definition = target != null ? target.definition : null;
            Profile profile = definition != null && target is Missile ? GetProfile(definition) : MissingPrefab;
            return new Dictionary<string, object>
            {
                ["eligible"] = IsEligible(target),
                ["reason"] = EligibilityReason(target),
                ["targetClass"] = target != null ? target.GetType().FullName : "",
                ["definitionKey"] = definition != null ? definition.jsonKey : "",
                ["value"] = definition != null ? definition.value : 0f,
                ["minimumValue"] = MinimumValue,
                ["ballisticGuidance"] = profile.Ballistic,
                ["guidanceType"] = profile.GuidanceType,
                ["configuredLoftMetres"] = profile.LoftMetres,
                ["configuredTerminalDiveDegrees"] = profile.DiveDegrees
            };
        }

        private static Profile GetProfile(UnitDefinition definition)
        {
            GameObject prefab = definition.unitPrefab;
            if (prefab == null) return MissingPrefab;
            Profile profile;
            if (Profiles.TryGetValue(definition, out profile) && profile.Prefab == prefab &&
                (profile.Ballistic || Time.realtimeSinceStartup - profile.CheckedAt < 1f)) return profile;

            // Some mods finish patching a registered prefab asynchronously.
            // Do not permanently retain a pre-patch negative classification.
            // One retry per definition per real second bounds the scan cost.
            profile = new Profile { Prefab = prefab, CheckedAt = Time.realtimeSinceStartup };
            // Resolve once per definition/prefab. Hot selection calls only read
            // the cached profile and the current value on the live definition.
            MissileSeeker[] seekers = prefab.GetComponents<MissileSeeker>();
            for (int i = 0; i < seekers.Length; i++)
            {
                MissileSeeker seeker = seekers[i];
                if (seeker == null) continue;
                Type type = seeker.GetType();
                if (seeker is BallisticMissileGuidance)
                {
                    profile.Ballistic = true;
                    profile.GuidanceType = type.FullName;
                    profile.Reason = "native-ballistic-guidance";
                    break;
                }
                // Verified Aryx 1.0.6 Starfall and Sunfall use this custom
                // loft/cruise/dive seeker rather than BallisticMissileGuidance.
                // Neither names alone nor a generic INS seeker are sufficient.
                if (!IsVerifiedAryxKey(definition.jsonKey) || type.FullName != AryxSeekerType) continue;
                profile.GuidanceType = type.FullName;
                AryxFields fields;
                if (!SeekerFields.TryGetValue(type, out fields))
                    SeekerFields.Add(type, fields = new AryxFields(type));
                if (!TryReadFloat(fields.Loft, seeker, out profile.LoftMetres) ||
                    !TryReadFloat(fields.Dive, seeker, out profile.DiveDegrees))
                {
                    profile.Reason = "aryx-guidance-profile-unavailable";
                    continue;
                }
                profile.Ballistic = profile.LoftMetres >= MinimumAryxLoftMetres &&
                    profile.DiveDegrees >= MinimumAryxDiveDegrees;
                profile.Reason = profile.Ballistic ? "verified-aryx-loft-dive-guidance" : "aryx-guidance-below-ballistic-profile";
                if (profile.Ballistic) break;
            }
            Profiles[definition] = profile;
            return profile;
        }

        private static bool IsVerifiedAryxKey(string key)
        {
            return string.Equals(key, "Aryx_Hypersonic1", StringComparison.Ordinal) ||
                string.Equals(key, "Aryx_Hypersonic1_Nuke", StringComparison.Ordinal);
        }

        private static bool TryReadFloat(FieldInfo field, MissileSeeker seeker, out float value)
        {
            value = 0f;
            if (field == null || field.FieldType != typeof(float)) return false;
            value = (float)field.GetValue(seeker);
            return Finite(value);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
    internal static class NaturalBallisticOpportunityPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(WeaponStation weaponStation, Unit analyzer, TrackingInfo trackingInfo, ref OpportunityThreat __result)
        {
            if (weaponStation == null || !NaturalBallisticSelection.IsBastion(weaponStation.WeaponInfo)) return;
            Unit target;
            if (trackingInfo == null || !trackingInfo.TryGetUnit(out target) || !NaturalBallisticSelection.AutomaticAllows(weaponStation.WeaponInfo, analyzer, target))
                __result = new OpportunityThreat(0f, __result.threat);
            // Eligible contacts retain every native range, seeker, threat,
            // attack-count and intercept-viability decision without amplification.
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class NaturalBallisticLaunchPatch
    {
        private static bool Prefix(MissileLauncher __instance, Unit target, WeaponStation weaponStation)
        {
            if (ResoluteCommandApi.IsManualFire(__instance.attachedUnit, weaponStation, target)) return true;
            if (!NaturalBallisticSelection.IsBastion(__instance.info) &&
                !NaturalBallisticSelection.IsBastion(weaponStation != null ? weaponStation.WeaponInfo : null)) return true;
            // Native queued salvos only recheck hostility/liveness before Fire.
            // Enforce eligibility again before ammo, visibility or spawning change.
            return NaturalBallisticSelection.AutomaticAllows(__instance.info, __instance.attachedUnit, target);
        }
    }

    [HarmonyPatch(typeof(TrackingInfo), nameof(TrackingInfo.UpdateInfo))]
    internal static class NaturalBallisticHistoryPatch
    {
        private static void Postfix(TrackingInfo __instance, GlobalPosition position)
        { NaturalBallisticSelection.ObserveAltitude(__instance, position); }
    }
}
