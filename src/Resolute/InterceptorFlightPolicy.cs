using System;

namespace Resolute
{
    // Pure numerical boundary rules also exercised outside Unity.
    internal static class InterceptorFlightPolicy
    {
        internal const float DartLoftCoefficient = .06f;
        internal static bool AutomaticPhase(bool terminalInterceptor, bool observed, bool previouslyExo, bool currentlyExo,
            float range, float terminalMaximum)
        { return observed && (terminalInterceptor ? previouslyExo && range <= terminalMaximum : currentlyExo); }

        internal static bool InfraredEnvelope(float distanceSquared, float maximumRange, float angle, float halfAngle)
        {
            return Finite(distanceSquared) && Finite(maximumRange) && Finite(angle) && distanceSquared >= 0f &&
                maximumRange > 0f && distanceSquared <= maximumRange * maximumRange && angle >= 0f && angle <= halfAngle;
        }
        internal static float DartLoftHeight(float range, float secondsToGo, float age, float ignitionDelay)
        {
            if (!Finite(range) || !Finite(secondsToGo) || !Finite(age) || !Finite(ignitionDelay) || range <= 0f || secondsToGo <= 0f) return 0f;
            float launch = Smooth((age - ignitionDelay) / .5f);
            float terminal = Smooth((secondsToGo - 3f) / 7f);
            return Math.Min(120f, Math.Min(range, secondsToGo * secondsToGo * 4.905f) * DartLoftCoefficient) * launch * terminal;
        }
        internal static float RcsBlend(float density, bool nearbyOwnLock)
        {
            if (!Finite(density)) return 0f;
            if (nearbyOwnLock) return 1f;
            return Clamp((.1f - density) / (.1f - .015f));
        }
        private static float Smooth(float x) { x = Clamp(x); return x * x * (3f - 2f * x); }
        private static float Clamp(float x) { return Math.Max(0f, Math.Min(1f, x)); }
        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
    }
}
