using System;

namespace Resolute
{
    // Pike's authored coordination policy. Native terrain following, propulsion
    // and aerodynamics execute the request; this never moves a missile directly.
    internal static class PikeFormationFlightPolicy
    {
        internal const float CruiseSpeed = 848.833333f;
        internal const float TerminalRange = 46300f;
        internal const float FinalApproachRange = 64820f;
        internal const float MinimumSpeedFraction = .65f;
        internal static bool CanCommitTerminal(float range, bool usableNativeTrack, bool launchCleared)
            => usableNativeTrack && launchCleared && !float.IsNaN(range) && !float.IsInfinity(range) &&
                range >= 0f && range <= TerminalRange;

        // Reserve stable slots about the leader. Losses do not renumber or
        // collapse surviving lanes across one another.
        internal static float SlotOffset(int slot, float spacing)
            => slot == 0 ? 0f : (slot % 2 == 1 ? (slot + 1) / 2 : -slot / 2) * spacing;

        internal static float ResponseSeconds(float ahead, float dragDeceleration)
        {
            // Coast-down closes a gap on the scale sqrt(2*gap/(D/m)). Using the
            // full-speed aerodynamic time constant makes small corrections take
            // minutes. Keep native navigation's six-second preview minimum and
            // bound large-gap or sparse-air response, with actual drag as input.
            double time = Math.Sqrt(2d * Math.Max(ahead, 0f) / Math.Max(dragDeceleration, .05f));
            return (float)Math.Max(6d, Math.Min(60d, time));
        }

        internal static float SpeedCeiling(float ahead, float referenceSpeed, float ownAlongSpeed,
            float spacing, float dragDeceleration, int members,
            float minimumSafeSpeed = CruiseSpeed * MinimumSpeedFraction)
        {
            if (!Finite(ahead) || !Finite(referenceSpeed) || !Finite(ownAlongSpeed) ||
                !Finite(spacing) || !Finite(dragDeceleration) || !Finite(minimumSafeSpeed)) return CruiseSpeed;
            if (members < 2 || ahead <= Math.Max(2f, spacing * .1f)) return CruiseSpeed;
            float response = ResponseSeconds(ahead, dragDeceleration);
            // A leading round must coast below the rear round's speed to close
            // the launch stagger. Signed damping starts its recovery before it
            // passes the rear plane; never write velocity or add artificial drag.
            float relativeSpeed = ownAlongSpeed - referenceSpeed;
            float requested = referenceSpeed - 3f * ahead / response - .5f * relativeSpeed;
            // Early rounds must not race to a fixed 552 m/s floor while the
            // rear round is only just flying at 130 m/s. Retain the same .65
            // pacing proportion, referenced to the actual trailing round.
            // Its safe floor comes from native lift and retirement limits.
            float floor = Math.Max(Math.Max(0f, minimumSafeSpeed), Math.Max(0f, referenceSpeed) * MinimumSpeedFraction);
            return Math.Min(CruiseSpeed, Math.Max(floor, requested));
        }

        internal static float AssemblyMinimumSpeed(float density, float finArea, float liftAtTenDegrees,
            float massKg, float windSpeed, float nativeRetirementSpeed)
        {
            if (!Finite(density) || !Finite(finArea) || !Finite(liftAtTenDegrees) || !Finite(massKg) ||
                !Finite(windSpeed) || !Finite(nativeRetirementSpeed) || density <= 0f || finArea <= 0f ||
                liftAtTenDegrees <= 0f || massKg <= 0f || windSpeed < 0f || nativeRetirementSpeed <= 0f) return CruiseSpeed;
            // Native ApplyAero: L=.5*rho*v_air^2*S*Cl. Preserve two-g lift
            // capability at the existing vertical controller's ten-degree
            // reference, including room to support weight and arrest descent.
            // Adding |wind| is a conservative ground-speed conversion for any
            // wind bearing; twice native retirement speed provides a separate
            // lifecycle margin. Neither value changes native lift or forces.
            double airspeed = Math.Sqrt(4d * 9.81d * massKg / ((double)density * finArea * liftAtTenDegrees));
            return (float)Math.Min(CruiseSpeed, Math.Max(2d * nativeRetirementSpeed, airspeed + windSpeed));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
