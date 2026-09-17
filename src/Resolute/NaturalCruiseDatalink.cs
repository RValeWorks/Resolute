using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Retain the component name for existing diagnostics. This observes native
    // terminal memory; it does not authorize or perform a separate recovery.
    internal sealed class NaturalRadarTrackRecovery : MonoBehaviour
    {
        internal Missile Missile;
        internal ARHSeeker Seeker;
        internal RadarRecoveryDecision Decision;
        internal int TargetClears;
        internal bool HqPresent, FreshTrack, TargetLive, Jammed;
        internal float TrackAge = -1f, Range, ClosingSpeed, ForwardAngle, LastReturn;
        internal string ReturnKind, LastClearContext;

        internal void Observe()
        {
            Unit target = NaturalCruiseDatalink.Target(Seeker);
            HqPresent = Missile.NetworkHQ != null;
            TargetLive = target != null && !target.disabled && target.NetworkHQ != null &&
                target.NetworkHQ != Missile.NetworkHQ;
            FreshTrack = false; TrackAge = -1f;
            LastReturn = NaturalCruiseDatalink.Strength(Seeker);
            Jammed = NaturalCruiseDatalink.Jammed(Seeker);
            if (target != null && HqPresent)
            {
                TrackingInfo track = Missile.NetworkHQ.GetTrackingData(target.persistentID);
                if (track != null)
                {
                    TrackAge = Time.timeSinceLevelLoad - track.lastSpottedTime;
                    FreshTrack = TrackAge >= 0f && TrackAge < 4f;
                }
            }
            // Read the seeker's actual information, never a hidden target
            // position/velocity or an HQ getter that refreshes its cache.
            Vector3 delta = NaturalCruiseDatalink.KnownPosition(Seeker) - Missile.GlobalPosition();
            Range = delta.magnitude;
            ClosingSpeed = Vector3.Dot(Missile.rb.velocity - NaturalCruiseDatalink.KnownVelocity(Seeker), delta.normalized);
            ForwardAngle = Vector3.Angle(Missile.transform.forward, delta);
            Decision = RadarRecoveryPolicy.Observe(Missile.targetID.Id, TargetLive, HqPresent,
                LastReturn, Seeker.GetRadarParams().minSignal, Jammed,
                NaturalCruiseDatalink.WithoutReturn(Seeker), NaturalCruiseDatalink.LockPerseverance(Seeker));
            ReturnKind = LastReturn == -1f ? "native-occlusion-return" :
                LastReturn >= Seeker.GetRadarParams().minSignal ? "radar-lock" :
                Jammed ? "jammed" : "weak-return";
        }

        internal object Capture()
        {
            return new Dictionary<string, object> {
                ["authority"] = "native-ARH-TerminalMode", ["customRecoveryEnabled"] = false,
                ["targetClears"] = TargetClears, ["decision"] = Decision.ToString(),
                ["returnKind"] = ReturnKind, ["radarReturn"] = LastReturn,
                ["hqPresent"] = HqPresent, ["trackAgeSeconds"] = TrackAge,
                ["freshTrack"] = FreshTrack, ["targetLiveHostile"] = TargetLive,
                ["jammed"] = Jammed, ["seekerKnownRangeM"] = Range,
                ["seekerKnownClosingSpeedMps"] = ClosingSpeed,
                ["seekerKnownForwardAngleDegrees"] = ForwardAngle,
                ["nativeMaxTrackingAngleDegrees"] = NaturalCruiseDatalink.TrackingAngle(Seeker),
                ["nativeLockPerseveranceSeconds"] = NaturalCruiseDatalink.LockPerseverance(Seeker),
                ["nativeSecondsWithoutReturn"] = NaturalCruiseDatalink.WithoutReturn(Seeker),
                ["lastClearContext"] = LastClearContext
            };
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "Initialize")]
    internal static class NaturalRadarRecoveryInitialize
    {
        private static void Postfix(ARHSeeker __instance)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            Missile missile = __instance.GetComponent<Missile>();
            string key = missile != null && missile.definition != null ? missile.definition.jsonKey : null;
            if (!NaturalCruiseDatalink.Handles(key) || !missile.LocalSim) return;
            NaturalRadarTrackRecovery state = missile.GetComponent<NaturalRadarTrackRecovery>();
            if (state == null) state = missile.gameObject.AddComponent<NaturalRadarTrackRecovery>();
            state.Missile = missile; state.Seeker = __instance;
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "TerminalMode")]
    internal static class NaturalCruiseDatalink
    {
        internal static readonly AccessTools.FieldRef<MissileSeeker, Unit> Target = AccessTools.FieldRefAccess<MissileSeeker, Unit>("targetUnit");
        internal static readonly AccessTools.FieldRef<ARHSeeker, float> Strength = AccessTools.FieldRefAccess<ARHSeeker, float>("returnStrength");
        internal static readonly AccessTools.FieldRef<ARHSeeker, bool> Jammed = AccessTools.FieldRefAccess<ARHSeeker, bool>("isJammed");
        internal static readonly AccessTools.FieldRef<ARHSeeker, float> TrackingAngle = AccessTools.FieldRefAccess<ARHSeeker, float>("maxTrackingAngle");
        internal static readonly AccessTools.FieldRef<ARHSeeker, float> WithoutReturn = AccessTools.FieldRefAccess<ARHSeeker, float>("timeWithoutReturn");
        internal static readonly AccessTools.FieldRef<ARHSeeker, float> LockPerseverance = AccessTools.FieldRefAccess<ARHSeeker, float>("lockPerseverance");
        internal static readonly AccessTools.FieldRef<ARHSeeker, GlobalPosition> KnownPosition = AccessTools.FieldRefAccess<ARHSeeker, GlobalPosition>("knownPos");
        internal static readonly AccessTools.FieldRef<ARHSeeker, Vector3> KnownVelocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVel");

        internal static bool Handles(string key) => key == "rsl_ashm" || key == "rsl_lrsam" ||
            key == "rsl_mrsam" || key == "rsl_bastion";

        private static void Postfix(ARHSeeker __instance)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            NaturalRadarTrackRecovery state = __instance.GetComponent<NaturalRadarTrackRecovery>();
            if (state == null || state.Missile == null || state.Missile.disabled || !state.Missile.LocalSim) return;
            // Native TerminalMode already supplies HQ fallback while honoring
            // terrain occlusion, the terminal gimbal and lock perseverance.
            // Do not call GetRadarReturn again, accelerate DatalinkMode, change
            // its latch/timers, or replace those rules with a custom gap budget.
            state.Observe();
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.SetTarget))]
    internal static class NaturalRadarTargetClearObservation
    {
        private static void Prefix(Missile __instance, Unit target)
        {
            if (!ResoluteDiagnostics.Enabled) return;
            if (target != null || !__instance.targetID.IsValid) return;
            NaturalRadarTrackRecovery state = __instance.GetComponent<NaturalRadarTrackRecovery>();
            if (state == null || state.Seeker == null) return;
            state.Observe(); state.TargetClears++;
            // Context is an observation, not a claim that a particular guard
            // caused this SetTarget call (chaff/target death can also clear it).
            state.LastClearContext = state.Decision.ToString() + ":" + state.ReturnKind;
            NaturalMissileDiagnostics.GuidanceEvent(__instance, "target-cleared", state.Capture());
        }
    }
}
