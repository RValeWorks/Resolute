using System;

namespace Resolute
{
    internal static class ResoluteScrewSpeed
    {
        // Retain the existing full-throttle visual ceiling. This is only the
        // shaft animation; native propulsion still supplies actual thrust.
        internal const float MaximumDegreesPerSecond = 360f;
        internal const float ResponsePerSecond = .8f;

        internal static float OrderedFraction(float knots, float maximumKnots)
            => Finite(knots) && Finite(maximumKnots) && maximumKnots > 0f
                ? Math.Max(-1f, Math.Min(1f, knots / maximumKnots)) : 0f;

        internal static float Step(float current, float target, float elapsed)
        {
            if (!Finite(current)) current = 0f;
            if (!Finite(target)) target = 0f;
            target = Math.Max(-1f, Math.Min(1f, target));
            if (!Finite(elapsed) || elapsed <= 0f) return current;
            // A reversal passes through zero instead of jumping direction on
            // a slow frame. The next update then starts the opposite rotation.
            if (current * target < 0f) target = 0f;
            float delta = ResponsePerSecond * Math.Min(elapsed, .25f);
            return current < target ? Math.Min(target, current + delta) : Math.Max(target, current - delta);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
