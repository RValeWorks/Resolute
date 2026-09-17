using System;

namespace Resolute
{
    // A sampled obstacle supplies a deadline, never a new altitude or force.
    // The caller owns the ray result and clears it when that corridor is clear.
    internal static class PikeTerrainClearancePolicy
    {
        internal const float ResponseAllowanceSeconds = .35f;
        internal const float MinimumResponseSeconds = .05f;
        // LiftAim retains its existing -20 m/s² recovery request limit.
        internal const float MaximumBrakingAcceleration = 20f;

        internal static float VerticalDemand(float normalDemand, float altitudeError,
            float horizontalSpeed, float secondsToObstacle, float maximumLoftDegrees, float brakingAcceleration)
        {
            if (!Finite(normalDemand) || !Finite(altitudeError) || !Finite(horizontalSpeed) ||
                !Finite(secondsToObstacle) || !Finite(maximumLoftDegrees) || !Finite(brakingAcceleration) || normalDemand < 0f ||
                altitudeError <= 0f || horizontalSpeed <= 0f || secondsToObstacle < 0f ||
                maximumLoftDegrees <= 0f || maximumLoftDegrees >= 89f || brakingAcceleration <= 0f)
                return normalDemand;

            double seconds = Math.Max(MinimumResponseSeconds,
                (double)secondsToObstacle - ResponseAllowanceSeconds);
            double loftLimit = Math.Min(float.MaxValue,
                horizontalSpeed * Math.Tan(maximumLoftDegrees * Math.PI / 180.0));
            double brake = Math.Min(MaximumBrakingAcceleration, brakingAcceleration);
            double response = brake * ResponseAllowanceSeconds;
            // Raising only the positive acceleration cap can leave hundreds
            // of metres of upward coast. Bound the requested speed by the
            // existing recovery authority and the clearance still remaining.
            double stoppingSpeed = Math.Sqrt(response * response + 2.0 * brake * altitudeError) - response;
            double urgentDemand = Math.Min(stoppingSpeed, Math.Min(loftLimit, altitudeError / seconds));
            // Preserve an already stronger native demand rather than silently
            // weakening it. This helper only raises a positive climb request.
            return (float)Math.Max(normalDemand, urgentDemand);
        }

        internal static float PositiveAccelerationLimit(float normalLimit, bool urgentClimb,
            float liftAtTenDegrees, float speed, float nativeTurnRateDegrees, float nativeGLimit)
        {
            if (!urgentClimb || !Finite(normalLimit) || normalLimit < 0f || !Finite(liftAtTenDegrees) ||
                !Finite(speed) || !Finite(nativeTurnRateDegrees) || !Finite(nativeGLimit) ||
                liftAtTenDegrees <= 9.81f || speed <= 0f || nativeTurnRateDegrees <= 0f || nativeGLimit <= 0f)
                return normalLimit;

            // Native rate/G settings bound each angular axis, not total
            // translational G. This is only a ceiling on requested climb
            // acceleration; native steering, lift and loft limits still apply.
            double rate = Math.Min(nativeTurnRateDegrees * Math.PI / 180.0,
                9.81 * nativeGLimit / Math.Max(speed, 1f));
            double available = Math.Min(liftAtTenDegrees - 9.81, speed * rate);
            return (float)Math.Max(normalLimit, Math.Min(float.MaxValue, available));
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
