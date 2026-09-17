using System;
using UnityEngine;

namespace Resolute
{
    // Selection-time clearance for two reserved hull surfaces. The existing
    // enclosing capsule radii conservatively cover every body orientation.
    // This is a candidate veto, not a flight prediction or steering controller.
    internal static class PikeApproachReservation
    {
        internal static bool Conflicts(Vector3 peerOffset, Vector3 ownToSurface, Vector3 peerToSurface,
            float ownSpeed, float peerSpeed, float requiredSeparation,
            out float closestSeconds, out float minimumMargin)
        {
            closestSeconds = 0f; minimumMargin = float.PositiveInfinity;
            if (!Finite(peerOffset) || !Finite(ownToSurface) || !Finite(peerToSurface) ||
                !Finite(ownSpeed) || !Finite(peerSpeed) || !Finite(requiredSeparation) ||
                ownSpeed <= 0f || peerSpeed <= 0f || requiredSeparation <= 0f) return false;
            double ownLength = Length(ownToSurface), peerLength = Length(peerToSurface);
            if (ownLength <= .001d || peerLength <= .001d) return false;
            // Stop when either reaches its surface: do not reserve a ghost
            // trajectory through/behind the ship after an expected impact.
            double horizon = Math.Min(ownLength / ownSpeed, peerLength / peerSpeed);
            double ownScale = ownSpeed / ownLength, peerScale = peerSpeed / peerLength;
            double dx = peerToSurface.x * peerScale - ownToSurface.x * ownScale;
            double dy = peerToSurface.y * peerScale - ownToSurface.y * ownScale;
            double dz = peerToSurface.z * peerScale - ownToSurface.z * ownScale;
            double relativeSpeedSquared = dx * dx + dy * dy + dz * dz;
            double time = relativeSpeedSquared > 1e-12d ? Math.Max(0d, Math.Min(horizon,
                -(peerOffset.x * dx + peerOffset.y * dy + peerOffset.z * dz) / relativeSpeedSquared)) : 0d;
            double x = peerOffset.x + dx * time, y = peerOffset.y + dy * time, z = peerOffset.z + dz * time;
            double separationSquared = x * x + y * y + z * z;
            closestSeconds = (float)time;
            minimumMargin = (float)(Math.Sqrt(separationSquared) - requiredSeparation);
            return separationSquared < (double)requiredSeparation * requiredSeparation;
        }

        private static double Length(Vector3 value) => Math.Sqrt((double)value.x * value.x +
            (double)value.y * value.y + (double)value.z * value.z);

        // Missile.DetectCollisions uses a static-snapshot forward linecast in
        // its speed * .25 close window, with no co-moving-body exemption.
        // Simultaneous sphere CPA alone misses a round immediately ahead.
        // Reserve that native sweep as well, with a conservative allowance for
        // observed versus proposed relative headings during this same window.
        // This remains candidate-dependent; it never changes a peer or flight.
        internal static bool NativeSweepConflicts(Vector3 peerOffset, Vector3 ownToSurface, Vector3 peerToSurface,
            Vector3 ownVelocity, Vector3 peerVelocity, Vector3 ownTargetVelocity, Vector3 peerTargetVelocity,
            float fixedDeltaTime, float requiredSeparation, out float closestSeconds, out float minimumMargin)
        {
            closestSeconds = 0f; minimumMargin = float.PositiveInfinity;
            if (!Finite(peerOffset) || !Finite(ownToSurface) || !Finite(peerToSurface) ||
                !Finite(ownVelocity) || !Finite(peerVelocity) || !Finite(ownTargetVelocity) || !Finite(peerTargetVelocity) ||
                !Finite(fixedDeltaTime) || !Finite(requiredSeparation) || fixedDeltaTime <= 0f || requiredSeparation <= 0f) return false;
            double ownLength = Length(ownToSurface), peerLength = Length(peerToSurface);
            double ownSpeed = Length(ownVelocity), peerSpeed = Length(peerVelocity);
            if (ownLength <= .001d || peerLength <= .001d || ownSpeed <= .001d || peerSpeed <= .001d) return false;
            double ownArrival = ownLength / ownSpeed, peerArrival = peerLength / peerSpeed;
            double horizon = Math.Min(ownArrival, peerArrival);
            D3 ownProposed = new D3(ownToSurface) * (ownSpeed / ownLength);
            D3 peerProposed = new D3(peerToSurface) * (peerSpeed / peerLength);
            D3 relative = peerProposed - ownProposed;
            D3 disagreement = (new D3(peerVelocity) - new D3(ownVelocity)) - relative;
            const double closeWindow = .25d, nativeSweepScale = 1.1d;
            double clearance = requiredSeparation + Math.Sqrt(D3.Dot(disagreement, disagreement)) * Math.Min(closeWindow, horizon);
            double best = double.PositiveInfinity, when = 0d;
            SweepDistance(new D3(peerOffset), relative, (ownProposed - new D3(ownTargetVelocity)) * (nativeSweepScale * fixedDeltaTime),
                Math.Max(0d, ownArrival - closeWindow), horizon, ref best, ref when);
            SweepDistance(new D3(peerOffset) * -1d, relative * -1d, (peerProposed - new D3(peerTargetVelocity)) * (nativeSweepScale * fixedDeltaTime),
                Math.Max(0d, peerArrival - closeWindow), horizon, ref best, ref when);
            if (double.IsPositiveInfinity(best)) return false;
            closestSeconds = (float)when;
            minimumMargin = (float)(Math.Sqrt(best) - clearance);
            return best < clearance * clearance;
        }

        private static void SweepDistance(D3 offset, D3 relative, D3 sweep, double start, double end,
            ref double best, ref double when)
        {
            if (start > end) return;
            D3 a = offset + relative * start, along = relative * (end - start);
            // Convex quadratic over [0,1]^2: four finite edges and its interior
            // stationary point. No iterative minimizer or physics queries.
            double aa = D3.Dot(along, along), bb = D3.Dot(sweep, sweep), ab = D3.Dot(along, sweep);
            double ac = D3.Dot(along, a), bc = D3.Dot(sweep, a);
            Candidate(a, along, sweep, 0d, bb > 0d ? Clamp01(bc / bb) : 0d, start, end, ref best, ref when);
            Candidate(a, along, sweep, 1d, bb > 0d ? Clamp01((bc + ab) / bb) : 0d, start, end, ref best, ref when);
            Candidate(a, along, sweep, aa > 0d ? Clamp01(-ac / aa) : 0d, 0d, start, end, ref best, ref when);
            Candidate(a, along, sweep, aa > 0d ? Clamp01((ab - ac) / aa) : 0d, 1d, start, end, ref best, ref when);
            double denominator = aa * bb - ab * ab;
            if (denominator > aa * bb * 1e-12d)
            {
                double time = (ab * bc - bb * ac) / denominator, ray = (aa * bc - ab * ac) / denominator;
                if (time >= 0d && time <= 1d && ray >= 0d && ray <= 1d)
                    Candidate(a, along, sweep, time, ray, start, end, ref best, ref when);
            }
        }

        private static void Candidate(D3 a, D3 along, D3 sweep, double time, double ray, double start, double end,
            ref double best, ref double when)
        {
            D3 separation = a + along * time - sweep * ray;
            double squared = D3.Dot(separation, separation);
            if (squared < best) { best = squared; when = start + (end - start) * time; }
        }
        private static double Clamp01(double value) => Math.Max(0d, Math.Min(1d, value));
        private readonly struct D3
        {
            private readonly double x, y, z;
            internal D3(Vector3 value) { x = value.x; y = value.y; z = value.z; }
            private D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            internal static double Dot(D3 a, D3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
            public static D3 operator +(D3 a, D3 b) => new D3(a.x + b.x, a.y + b.y, a.z + b.z);
            public static D3 operator -(D3 a, D3 b) => new D3(a.x - b.x, a.y - b.y, a.z - b.z);
            public static D3 operator *(D3 a, double scale) => new D3(a.x * scale, a.y * scale, a.z * scale);
        }
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
