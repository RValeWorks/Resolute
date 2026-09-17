using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // The native seekers retain acquisition, lead, countermeasures and fusing.
    // This component supplies the source's missing midcourse altitude profile
    // to native steering; it never changes a Rigidbody position or velocity.
    internal sealed class NaturalLoftGuidance : MonoBehaviour
    {
        public float MaximumAngleDegrees, MaximumAltitude, TerminalRange, InitialFlightSeconds;
        public bool TerminalLoft;
        public bool NativeAirDefense;
        public bool SustainNativeTerminalLoft;
        public float NativeLoftAmount, LaunchTurnRate;
        public string NativeLoftDonor;
        private Missile missile;
        private MissileSeeker seeker;
        private NaturalDartLaunch coldLaunch;
        private bool initialized;
        private float launchAltitude, initialRange, apexAboveBaseline;
        private bool reactionControl;
        private float sizingSpeed, sizingMass, sizingFinArea, sizingLiftCoefficient, sizingG;
        private float geometricApex, sizingAltitude, sizingDensity, requiredAcceleration, availableAcceleration;
        internal float AppliedNativeLoft, TerminalBlend;
        private const float ReferenceAttackAngle = 7.5f, LiftReserve = .65f;
        internal bool Active;
        internal float CommandedAltitude, MaximumObservedAltitude;
        internal float InitialRange => initialRange;
        internal string FlightPhase = "launch";
        internal bool AeroLimited;
        internal float MinimumDesignDensity => sizingDensity;

        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> AimPoint = AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");
        private static readonly AccessTools.FieldRef<Missile, Vector3> TargetVelocity = AccessTools.FieldRefAccess<Missile, Vector3>("targetVel");
        private static readonly AccessTools.FieldRef<ARHSeeker, GlobalPosition> RadarPosition = AccessTools.FieldRefAccess<ARHSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<IRSeeker, GlobalPosition> InfraredPosition = AccessTools.FieldRefAccess<IRSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> RadarGuidance = AccessTools.FieldRefAccess<ARHSeeker, bool>("guidance");
        private static readonly AccessTools.FieldRef<IRSeeker, bool> InfraredGuidance = AccessTools.FieldRefAccess<IRSeeker, bool>("guidance");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarLoft = AccessTools.FieldRefAccess<ARHSeeker, float>("loftAmount");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarRange = AccessTools.FieldRefAccess<ARHSeeker, float>("targetDist");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarTime = AccessTools.FieldRefAccess<ARHSeeker, float>("timeToTarget");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarTopSpeed = AccessTools.FieldRefAccess<ARHSeeker, float>("topSpeed");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> RadarVelocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVel");

        internal static void Configure(GameObject prefab, NaturalWeapons.SourceWeapon source)
        {
            float angle = source.Guidance("MaxLoftAngle");
            if (source.Surface || angle <= 0f || source.key == "rsl_pd") return;
            var profile = prefab.AddComponent<NaturalLoftGuidance>();
            profile.MaximumAngleDegrees = Mathf.Clamp(angle, 0f, 80f);
            profile.MaximumAltitude = source.Guidance("MaxLoftAlt") * .3048f;
            profile.TerminalRange = source.Guidance("TerminalApproachDist") * 1852f;
            profile.InitialFlightSeconds = source.Guidance("InitialFlightPhaseDuration", .5f);
            profile.TerminalLoft = source.Text("Guidance", "TerminalLoft") == "True";
            profile.NativeAirDefense = NaturalWeapons.UsesNativeAirDefenseProfile(source.key);
            profile.SustainNativeTerminalLoft = profile.TerminalLoft &&
                (source.key == "rsl_lrsam" || source.key == "rsl_bastion");
            if (profile.NativeAirDefense)
            {
                profile.NativeLoftAmount = RadarLoft(prefab.GetComponent<ARHSeeker>());
                NaturalWeapons.NativeLoftDonors.TryGetValue(source.key, out profile.NativeLoftDonor);
                profile.LaunchTurnRate = source.Guidance("LaunchTurnRate", 0f);
            }
        }
        private void Awake()
        {
            missile = GetComponent<Missile>();
            seeker = GetComponent<MissileSeeker>();
            coldLaunch = GetComponent<NaturalDartLaunch>();
            reactionControl = GetComponent<NaturalReactionControl>() != null;
        }
        internal void Apply()
        {
            Active = false;
            if (NativeAirDefense)
            {
                // The native seeker has just written its final lead/loft aim.
                // Observe it only; never add a second waypoint or restrict its
                // physical turn with the old 3.75-degree direction clamp.
                if (missile == null || missile.disabled || !missile.LocalSim) return;
                MaximumObservedAltitude = Mathf.Max(MaximumObservedAltitude, missile.GlobalPosition().y);
                CommandedAltitude = AimPoint(missile).y;
                Active = enabled && AppliedNativeLoft > .0001f;
                FlightPhase = missile.timeSinceSpawn < InitialFlightSeconds ? "native-launch-turn" :
                    Active ? "native-midcourse-loft" : "native-terminal-lead";
                return;
            }
            if (!enabled || missile == null || missile.disabled || !missile.LocalSim || missile.targetID.NotValid) return;
            bool guiding = seeker is ARHSeeker radar ? RadarGuidance(radar) : seeker is IRSeeker infrared && InfraredGuidance(infrared);
            if (!guiding) return;
            GlobalPosition current = missile.GlobalPosition();
            GlobalPosition tracked = seeker is ARHSeeker active ? RadarPosition(active) : InfraredPosition((IRSeeker)seeker);
            GlobalPosition nativeAim = AimPoint(missile);
            Vector3 toTarget = tracked - current;
            float horizontalRange = new Vector2(toTarget.x, toTarget.z).magnitude;
            MaximumObservedAltitude = Mathf.Max(MaximumObservedAltitude, current.y);
            if (!initialized)
            {
                initialized = true;
                launchAltitude = current.y;
                initialRange = Mathf.Max(1f, horizontalRange);
                // A parabolic arc has initial slope 4h/r. Limit that slope by
                // the authored angle and reserve the terminal leg for homing.
                // Close-range launches consequently retain native direct lead.
                float midcourseRange = Mathf.Max(0f, initialRange - TerminalRange);
                geometricApex = midcourseRange * Mathf.Tan(MaximumAngleDegrees * Mathf.Deg2Rad) * .25f;
                float heightRoom = Mathf.Max(0f, MaximumAltitude - Mathf.Max(launchAltitude, tracked.y));
                apexAboveBaseline = Mathf.Min(geometricApex, heightRoom);
                if (!reactionControl)
                {
                    NaturalWeaponPhase phase = GetComponent<NaturalWeaponPhase>();
                    sizingSpeed = Mathf.Max(100f, phase != null ? phase.CruiseSpeed : missile.speed);
                    sizingMass = Mathf.Max(1f, missile.GetPrefabMass());
                    sizingFinArea = missile.GetFinArea();
                    sizingLiftCoefficient = Mathf.Max(0f, missile.GetLiftCoeff(ReferenceAttackAngle * Mathf.Deg2Rad));
                    sizingG = (float)NaturalWeapons.Get(missile, "gLimit");
                    float high = apexAboveBaseline, low = 0f;
                    // For y=4h p(1-p), apex curvature is 8h/r².
                    // Size the arc against the actual native atmosphere and
                    // lift at 7.5 degrees AoA, retaining 35% control reserve.
                    // This changes the chosen route, never the vehicle forces.
                    for (int iteration = 0; iteration < 20; iteration++)
                    {
                        float candidate = (low + high) * .5f;
                        EvaluateApex(candidate, Mathf.Max(launchAltitude, tracked.y));
                        if (availableAcceleration * LiftReserve >= requiredAcceleration) low = candidate;
                        else high = candidate;
                    }
                    AeroLimited = low < apexAboveBaseline - 1f;
                    apexAboveBaseline = low;
                    EvaluateApex(apexAboveBaseline, Mathf.Max(launchAltitude, tracked.y));
                }
            }
            float closingSpeed = Mathf.Max(100f, Vector3.Dot(missile.rb.velocity - TargetVelocity(missile), toTarget.normalized));
            float secondsToGo = toTarget.magnitude / closingSpeed;
            // Source terminal lofting still yields to the native terminal lead
            // before closest approach. Non-terminal-loft rounds close out at
            // their source terminal distance, including Bastion-X's 40 nmi.
            float terminalBlend = TerminalLoft
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 10f, secondsToGo))
                : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(TerminalRange * .65f, TerminalRange, horizontalRange));
            if (apexAboveBaseline < 1f || terminalBlend <= .001f || horizontalRange < 500f)
            { FlightPhase = "terminal"; CommandedAltitude = nativeAim.y; return; }

            float lookAhead = Mathf.Min(horizontalRange, Mathf.Clamp(missile.speed * 4f, 500f, 6000f));
            float progress = Mathf.Clamp01(1f - (horizontalRange - lookAhead) / initialRange);
            float baseline = Mathf.Lerp(launchAltitude, tracked.y, progress);
            float height = Mathf.Min(MaximumAltitude, baseline + 4f * apexAboveBaseline * progress * (1f - progress));
            Vector3 horizontal = nativeAim - current; horizontal.y = 0f;
            if (horizontal.sqrMagnitude < 1f) return;
            GlobalPosition waypoint = current + horizontal.normalized * lookAhead;
            waypoint.y = height;
            float launchBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.35f, Mathf.Max(.6f, InitialFlightSeconds), missile.timeSinceSpawn));
            float blend = launchBlend * terminalBlend;
            // Blend directions, rather than global point coordinates with very
            // different lookahead lengths, so native lead stays authoritative.
            Vector3 direct = (nativeAim - current).normalized;
            Vector3 lofted = (waypoint - current).normalized;
            Vector3 direction = Vector3.Slerp(direct, lofted, blend).normalized;
            if (!reactionControl && missile.timeSinceSpawn > Mathf.Max(InitialFlightSeconds, 5f) && missile.speed > 100f)
            {
                // Native Steering averages body and velocity headings once
                // aligned. Half the reference AoA therefore bounds the aim
                // correction without asking the body to flip across its flow.
                direction = Vector3.RotateTowards(missile.rb.velocity.normalized, direction,
                    ReferenceAttackAngle * .5f * Mathf.Deg2Rad, 0f).normalized;
            }
            missile.SetAimpoint(current + direction * lookAhead, TargetVelocity(missile));
            CommandedAltitude = height;
            Active = blend > .001f;
            FlightPhase = missile.timeSinceSpawn < InitialFlightSeconds ? "launch" : current.y < height ? "climb" : "midcourse-descent";
        }

        internal void PrepareNativeLoft()
        {
            if (!NativeAirDefense || missile == null || !(seeker is ARHSeeker radar)) return;
            AppliedNativeLoft = 0f;
            if (!enabled || missile.disabled || !missile.LocalSim || missile.targetID.NotValid)
            {
                RadarLoft(radar) = 0f;
                return;
            }
            // Missile.SetTorque's second argument sets the PID proportional
            // limit, despite its parameter name. Leave the native controller
            // and the configured aerodynamic turn/G limits untouched here.

            GlobalPosition current = missile.GlobalPosition();
            GlobalPosition tracked = RadarPosition(radar);
            Vector3 delta = tracked - current;
            float range = delta.magnitude;
            if (!initialized) { initialized = true; initialRange = range; launchAltitude = current.y; }
            float closing = Vector3.Dot(missile.rb.velocity - RadarVelocity(radar), delta.normalized);
            // During vertical ejection use the same nominal speed reference as
            // native early lead; current forward velocity is not cruise speed.
            if (missile.timeSinceSpawn < 3f)
                closing = Mathf.Max(closing, RadarTopSpeed(radar) - Vector3.Dot(RadarVelocity(radar), delta.normalized));
            float secondsToGo = range / Mathf.Max(100f, closing);
            TerminalBlend = NativeTerminalBlend(range, TerminalRange, secondsToGo, SustainNativeTerminalLoft);

            float coefficient = Mathf.Max(0f, NativeLoftAmount) * TerminalBlend;
            // Ward first turns toward the tracked intercept during its native
            // cold-launch coast. Admit its normal loft smoothly after ignition
            // instead of making the launch turn chase a high loft waypoint.
            if (coldLaunch != null)
                coefficient *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(coldLaunch.IgnitionDelay,
                    coldLaunch.IgnitionDelay + .5f, missile.timeSinceSpawn));
            // Native Seek uses these exact terms. Cap only its added altitude
            // to the authored ceiling; never replace the native lead solution.
            float nativeRange = Mathf.Max(0f, RadarRange(radar));
            float nativeTime = missile.timeSinceSpawn < 3f ? nativeRange / Mathf.Max(1f, RadarTopSpeed(radar)) : Mathf.Max(0f, RadarTime(radar));
            float loftScale = Mathf.Min(nativeTime * nativeTime * 4.905f, nativeRange);
            if (loftScale > 0f)
                coefficient = Mathf.Min(coefficient, Mathf.Max(0f, MaximumAltitude - tracked.y) / loftScale);
            AppliedNativeLoft = coefficient;
            RadarLoft(radar) = coefficient;
        }

        internal static float NativeTerminalBlend(float range, float terminalRange, float secondsToGo, bool sustainTerminalLoft = false)
        {
            // Sentinel and standard Bastion explicitly retain loft through
            // their terminal phase. That phase boundary must not flatten a
            // 20/46 km engagement. Both still yield to native close-intercept
            // lead by time to go. Ward keeps its existing distance reserve.
            float distanceBlend = !sustainTerminalLoft && terminalRange > 0f ? Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(terminalRange, terminalRange * 2f, range)) : 1f;
            float timeBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 10f, secondsToGo));
            return Mathf.Min(distanceBlend, timeBlend);
        }

        private void EvaluateApex(float height, float baseline)
        {
            sizingAltitude = baseline + height;
            sizingDensity = GameAssets.i.airDensityAltitude.Evaluate(sizingAltitude * .001f);
            availableAcceleration = .5f * sizingDensity * sizingSpeed * sizingSpeed * sizingFinArea * sizingLiftCoefficient / sizingMass;
            if (sizingG > 0f) availableAcceleration = Mathf.Min(availableAcceleration, sizingG * 9.81f);
            requiredAcceleration = sizingSpeed * sizingSpeed * 8f * height / (initialRange * initialRange) + 9.81f;
        }

        internal object CaptureSizing()
        {
            return new Dictionary<string, object> {
                ["sourceMaximumAltitudeMetres"] = MaximumAltitude,
                ["nativeAirDefenseProfile"] = NativeAirDefense,
                ["nativeDonorLoftAmount"] = NativeLoftAmount,
                ["nativeLoftDonor"] = NativeLoftDonor,
                ["nativeLoftAvailable"] = NativeLoftAmount > 0f,
                ["appliedNativeLoftAmount"] = AppliedNativeLoft,
                ["terminalBlend"] = TerminalBlend,
                ["sustainNativeTerminalLoft"] = SustainNativeTerminalLoft,
                ["authoredLaunchTurnRate"] = LaunchTurnRate,
                ["launchControllerPolicy"] = "preserve native PID and configured aerodynamic turn/G limits",
                ["sourceMaximumAngleDegrees"] = MaximumAngleDegrees,
                ["initialHorizontalRangeMetres"] = initialRange,
                ["geometricApexAboveBaselineMetres"] = geometricApex,
                ["selectedApexAboveBaselineMetres"] = apexAboveBaseline,
                ["aerodynamicallyLimited"] = AeroLimited,
                ["reactionControlAvailable"] = reactionControl,
                ["conservativeApexSizingAltitudeMetres"] = sizingAltitude,
                ["nativeAirDensityAtSizingAltitude"] = sizingDensity,
                ["nativeSpeedLimitUsedMps"] = sizingSpeed,
                ["nativeLoadedMassKg"] = sizingMass,
                ["nativeFinAreaSquareMetres"] = sizingFinArea,
                ["nativeLiftCoefficient"] = sizingLiftCoefficient,
                ["referenceAngleOfAttackDegrees"] = ReferenceAttackAngle,
                ["usableLiftFraction"] = LiftReserve,
                ["nativeAvailableAccelerationMps2"] = availableAcceleration,
                ["requiredArcAccelerationWithGravityMps2"] = requiredAcceleration,
                ["method"] = NativeAirDefense ? "Native donor ARH lead and loft; authored terminal-loft flag, close-intercept taper and altitude ceiling. Native launch PID and configured aerodynamic limits preserved. No custom waypoint or force." :
                    reactionControl ? "Source geometric arc retained for the existing physical reaction-control system." :
                    "Choose the highest source-bounded parabolic arc whose curvature at the native speed limit can be sustained by 65% of native lift at 7.5 degrees AoA, including gravity reserve. No physical coefficient, force or motor is changed."
            };
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class NaturalRadarLoftPatch
    {
        private static void Prefix(ARHSeeker __instance)
        {
            NaturalLoftGuidance profile = __instance.GetComponent<NaturalLoftGuidance>();
            if (profile != null) profile.PrepareNativeLoft();
        }
        private static void Postfix(ARHSeeker __instance)
        {
            NaturalLoftGuidance profile = __instance.GetComponent<NaturalLoftGuidance>();
            if (profile != null) profile.Apply();
        }
    }
    [HarmonyPatch(typeof(IRSeeker), nameof(IRSeeker.Seek))]
    internal static class NaturalInfraredLoftPatch
    {
        private static void Postfix(IRSeeker __instance)
        {
            NaturalLoftGuidance profile = __instance.GetComponent<NaturalLoftGuidance>();
            if (profile != null) profile.Apply();
            NaturalDartLaunch dart = __instance.GetComponent<NaturalDartLaunch>();
            if (dart == null || dart.RadarLaunch) return;
            Missile missile = __instance.GetComponent<Missile>();
            if (missile == null || missile.disabled || !missile.LocalSim || missile.targetID.NotValid ||
                NaturalWeapons.Get(__instance, "IRTarget") == null) return;
            GlobalPosition tracked = (GlobalPosition)NaturalWeapons.Get(__instance, "knownPos");
            Vector3 velocity = (Vector3)NaturalWeapons.Get(__instance, "knownVel");
            Vector3 delta = tracked - missile.GlobalPosition();
            float closing = Mathf.Max(100f, Vector3.Dot(missile.rb.velocity - velocity, delta.normalized));
            float height = InterceptorFlightPolicy.DartLoftHeight(delta.magnitude, delta.magnitude / closing,
                missile.timeSinceSpawn, dart.IgnitionDelay);
            if (height <= .001f) return;
            // MMR-S3's IR seeker has no native loft field to copy. Use a small
            // explicit .06 coefficient of the native gravity-compensation term,
            // capped at 120m and smoothly removed before terminal interception.
            GlobalPosition aim = (GlobalPosition)NaturalWeapons.Get(missile, "aimPoint");
            aim.y += height;
            missile.SetAimpoint(aim, velocity);
        }
    }
}
