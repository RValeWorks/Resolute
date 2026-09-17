using System;

namespace Resolute
{
    // Pike's authored schedule supplies demands to the native force model.
    // These functions never grant a sensor contact or move a rigidbody.
    internal static class PikeFlightProfile
    {
        internal const float NauticalMile = 1852f;
        internal const float Feet = .3048f;
        internal const float Knots = 1852f / 3600f;
        internal const float FinalCorrectionRange = .8f * NauticalMile;

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static float BoosterFuel(float nativeFuel, float nativeBurn, float launchSeconds)
        {
            if (!Finite(nativeFuel) || !Finite(nativeBurn) || !Finite(launchSeconds) ||
                nativeFuel <= 0f || nativeBurn <= 0f || launchSeconds <= 0f)
                throw new ArgumentOutOfRangeException("Pike booster schedule must have finite positive fuel and duration.");
            // Keep the donor's mass flow and force. Shortening its burn does
            // not compress twelve seconds of impulse into a three-second kick.
            return nativeFuel * launchSeconds / nativeBurn;
        }

        internal static float PoweredSeconds(float range, float cruiseSpeed)
        {
            // Native fuel burns with elapsed powered time even at its speed
            // cutoff. Include climb, descent and physical salvo rendezvous.
            return Math.Max(60f, range / Math.Max(1f, cruiseSpeed) * 1.35f + 30f);
        }

        internal static float VerticalDemand(float error, float horizontalSpeed, float acceleration,
            float maximumAngleDegrees)
        {
            float available = Math.Max(0f, Math.Abs(error));
            float brake = Math.Max(1f, acceleration);
            float response = brake * 1.5f;
            float stoppingSpeed = (float)Math.Sqrt(response * response + 2f * brake * available) - response;
            float angleSpeed = Math.Max(0f, horizontalSpeed) * (float)Math.Tan(maximumAngleDegrees * Math.PI / 180d);
            float rate = Math.Min(stoppingSpeed, Math.Min(available * .65f, angleSpeed));
            return error < 0f ? -rate : rate;
        }

        internal static float GoalAltitude(bool seaSkimSelected, bool terminalReleased,
            float ceiling, float cruiseAltitude, float terminalAltitude)
            => terminalReleased ? terminalAltitude : seaSkimSelected ? cruiseAltitude : ceiling;

        internal static bool CanRestoreRates(float angularX, float angularY, float rateDegrees, float gLimit, float speed)
        {
            if (rateDegrees <= 0f && gLimit <= 0f) return true;
            float rate = Math.Min(rateDegrees * (float)Math.PI / 180f, 9.81f * gLimit / Math.Max(speed, 1f));
            // Native ApplyAero assumes its existing angular rate is inside the
            // cap. Reducing that cap during a launch turn can turn a braking
            // correction into acceleration. Let native steering settle first.
            return Math.Max(Math.Abs(angularX), Math.Abs(angularY)) <= rate * .9f;
        }
    }
}
