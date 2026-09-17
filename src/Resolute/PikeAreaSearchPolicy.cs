using System;

namespace Resolute
{
    // Straight navigation before search. Once scanning, the group keeps its
    // frozen course; sensor range and contact eligibility remain independent.
    internal static class PikeAreaSearchPolicy
    {
        internal static float LookAhead(float speed)
        {
            return float.IsNaN(speed) || float.IsInfinity(speed) ? 3000f : Math.Max(3000f, speed * 8f);
        }

        internal static void Direction(double x, double z, double courseX, double courseZ, out double dx, out double dz)
        {
            double distance = Math.Sqrt(x * x + z * z);
            if (distance < 1d || double.IsNaN(distance) || double.IsInfinity(distance))
            { x = courseX; z = courseZ; distance = Math.Sqrt(x * x + z * z); }
            if (distance < .001d || double.IsNaN(distance) || double.IsInfinity(distance))
            { x = 0d; z = 1d; distance = 1d; }
            dx = x / distance; dz = z / distance;
        }
    }
}
