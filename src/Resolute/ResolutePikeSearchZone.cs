using System;

namespace Resolute
{
    // Launch intent, shared by the optional map renderer and native threat
    // assessment. No detection or unit coordinates are supplied by this type.
    public sealed class ResolutePikeSearchZone
    {
        public readonly double OriginX, OriginZ, DirectionX, DirectionZ;
        public readonly double ActivationDistance, RadarRange, HalfAngleDegrees, MaximumRange;
        public readonly double SelectedDistance;
        public double HalfWidth => RadarRange * Math.Sin(HalfAngleDegrees * Math.PI / 180.0);
        public double ArcEndDistance => ActivationDistance + RadarRange * Math.Cos(HalfAngleDegrees * Math.PI / 180.0);
        public bool Valid => Finite(OriginX) && Finite(OriginZ) && Finite(DirectionX) && Finite(DirectionZ) &&
            Finite(SelectedDistance) && Finite(RadarRange) && RadarRange > 0 && Finite(MaximumRange) && MaximumRange > 0 &&
            Finite(HalfAngleDegrees) && HalfAngleDegrees > 0 && HalfAngleDegrees < 90 &&
            DirectionX * DirectionX + DirectionZ * DirectionZ > .99;

        public ResolutePikeSearchZone(double originX, double originZ, double selectedX, double selectedZ,
            double radarRange, double halfAngleDegrees, double maximumRange)
        {
            OriginX = originX; OriginZ = originZ;
            double dx = selectedX - originX, dz = selectedZ - originZ;
            SelectedDistance = Math.Sqrt(dx * dx + dz * dz);
            DirectionX = SelectedDistance > .001 ? dx / SelectedDistance : 0;
            DirectionZ = SelectedDistance > .001 ? dz / SelectedDistance : 1;
            RadarRange = radarRange; HalfAngleDegrees = halfAngleDegrees; MaximumRange = maximumRange;
            ActivationDistance = Math.Max(0, SelectedDistance - radarRange);
        }

        public GlobalPosition GetPoint(double along, double across = 0)
            => new GlobalPosition((float)(OriginX + DirectionX * along + DirectionZ * across), 0,
                (float)(OriginZ + DirectionZ * along - DirectionX * across));

        public double ProjectAlong(double x, double z) => (x - OriginX) * DirectionX + (z - OriginZ) * DirectionZ;

        public bool Contains(double x, double z, double minimumAlong = 0)
        {
            if (!Valid || !Finite(x) || !Finite(z) || !Finite(minimumAlong)) return false;
            double dx = x - OriginX, dz = z - OriginZ;
            double along = dx * DirectionX + dz * DirectionZ;
            if (along < Math.Max(ActivationDistance, minimumAlong) ||
                dx * dx + dz * dz > MaximumRange * MaximumRange) return false;
            double across = Math.Abs(dx * DirectionZ - dz * DirectionX);
            double forward = along - ActivationDistance;
            // Union of the initial circular sector and its persistent strip.
            // The strip begins at the arc endpoints, not at its front midpoint.
            double angle = HalfAngleDegrees * Math.PI / 180;
            return along >= ArcEndDistance && across <= HalfWidth ||
                forward * forward + across * across <= RadarRange * RadarRange && across <= forward * Math.Tan(angle);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
