using System;

namespace Resolute
{
    // A heading demand, not an achieved turn guarantee. The native PID,
    // angular caps, aerodynamic curves and motor still move the missile.
    internal static class PikeEvasionPolicy
    {
        internal const double MetresPerNauticalMile = 1852;
        internal const double PeriodSeconds = 6.28;
        internal const double MaximumHeadingDegrees = 8;
        internal const double MaximumBankDegrees = 50;

        internal static double AmplitudeDegrees(double rangeM)
        {
            if (!Finite(rangeM) || rangeM <= .6 * MetresPerNauticalMile || rangeM >= 25 * MetresPerNauticalMile) return 0;
            double nm = rangeM / MetresPerNauticalMile;
            if (nm > 10) return MaximumHeadingDegrees * (25 - nm) / 15;
            if (nm >= 2) return MaximumHeadingDegrees;
            return MaximumHeadingDegrees * (nm - .6) / 1.4;
        }

        internal static double PhaseOffset(uint missileId)
        {
            uint mixed;
            unchecked { mixed = missileId * 2654435761u; mixed ^= mixed >> 16; }
            return (mixed & 0x00ffffffu) / 16777216.0 * Math.PI * 2;
        }

        internal static double HeadingDegrees(double rangeM, double elapsed, uint missileId)
        {
            if (!Finite(elapsed) || elapsed < 0) return 0;
            return AmplitudeDegrees(rangeM) * Math.Sin(elapsed * Math.PI * 2 / PeriodSeconds + PhaseOffset(missileId));
        }

        internal static double NominalHeadingRateDegrees(double rangeM, double elapsed, uint missileId)
        {
            if (!Finite(elapsed) || elapsed < 0) return 0;
            double omega = Math.PI * 2 / PeriodSeconds;
            return AmplitudeDegrees(rangeM) * omega * Math.Cos(elapsed * omega + PhaseOffset(missileId));
        }

        internal static double BankDegrees(double speedMps, double headingRateDegrees)
        {
            if (!Finite(speedMps) || !Finite(headingRateDegrees) || speedMps <= 0) return 0;
            // Presentation follows signed turn rate smoothly. A conventional
            // coordinated-turn atan would request nearly 90 degrees at Pike's
            // lateral acceleration and clip into a square-wave bank command.
            double bank = headingRateDegrees / (MaximumHeadingDegrees * Math.PI * 2 / PeriodSeconds) * MaximumBankDegrees;
            return Math.Max(-MaximumBankDegrees, Math.Min(MaximumBankDegrees, bank));
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
