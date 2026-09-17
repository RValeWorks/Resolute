using System;

namespace Resolute
{
    // Immutable launch intent. Flight motion retires passed portions but never
    // rotates or expands the corridor, including after leader replacement.
    internal sealed class PikeThreatZone
    {
        internal readonly double X, Z, Radius;
        internal readonly ResolutePikeSearchZone Search;
        internal PikeThreatZone(double originX, double originZ, double x, double z, double radius, double maxRange)
        {
            X = x; Z = z; Radius = radius;
            Search = new ResolutePikeSearchZone(originX, originZ, x, z, radius,
                ResolutePikeMap.SearchHalfAngleDegrees, maxRange);
        }
        internal bool Valid => Search.Valid;
        internal bool Contains(double x, double z, double minimumAlong = 0) => Search.Contains(x, z, minimumAlong);
    }

    internal static class PikeObservedThreatPolicy
    {
        internal const double MaximumObservationGap = 4.0;

        internal static bool Contains(double x, double z, double radius)
        {
            if (!Finite(x) || !Finite(z) || !Finite(radius) || !(radius > 0.0)) return false;
            // Normalize first so even malformed extreme coordinates cannot
            // turn overflowed squared lengths into an admitted boundary.
            x /= radius; z /= radius;
            return x * x + z * z <= 1.0;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
