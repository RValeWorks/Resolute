using System;
using System.Collections.Generic;

namespace Resolute
{
    // Briefly stagger identical native acceleration profiles. The final speed
    // is common to every missile; no permanent differential is introduced.
    internal static class PikeTerminalAccelerationPolicy
    {
        internal const float CruiseSpeed = 848.833333f;
        internal const float TerminalSpeed = 1697.666667f;
        internal const float DesiredGapMetres = 50f;
        internal const float RampSeconds = .2f;
        internal const int MaximumMembers = 8;

        internal struct Member
        {
            internal uint Id;
            internal double AlongPosition;
            internal float AlongSpeed;
            internal Member(uint id, double alongPosition, float alongSpeed)
            { Id = id; AlongPosition = alongPosition; AlongSpeed = alongSpeed; }
        }

        internal struct Schedule
        {
            internal uint Id;
            internal int Rank, Count;
            internal float DelaySeconds;
            internal Schedule(uint id, int rank, int count, float delay)
            { Id = id; Rank = rank; Count = count; DelaySeconds = delay; }
        }

        internal static Schedule[] Plan(Member[] members)
        {
            if (members == null || members.Length > MaximumMembers)
                throw new ArgumentException("Pike terminal acceleration needs at most eight same-target members.");
            Member[] ordered = (Member[])members.Clone();
            var ids = new HashSet<uint>();
            foreach (Member member in ordered)
                if (member.Id == 0 || !Finite(member.AlongPosition) || !Finite(member.AlongSpeed) || !ids.Add(member.Id))
                    throw new ArgumentException("Pike terminal acceleration needs unique IDs and finite observed positions and speeds.");
            Array.Sort(ordered, (a, b) => {
                int order = b.AlongPosition.CompareTo(a.AlongPosition);
                if (order == 0) order = b.AlongSpeed.CompareTo(a.AlongSpeed);
                return order != 0 ? order : a.Id.CompareTo(b.Id);
            });
            var result = new Schedule[ordered.Length];
            double delay = 0d;
            for (int rank = 0; rank < ordered.Length; rank++)
            {
                if (rank > 0)
                {
                    double existingGap = Math.Max(0d, ordered[rank - 1].AlongPosition - ordered[rank].AlongPosition);
                    // Equal acceleration histories shifted by dt gain
                    // (terminalSpeed - cruiseSpeed) * dt separation once both
                    // reach the common speed. Existing spacing needs no delay.
                    delay += Math.Max(0d, DesiredGapMetres - existingGap) / (TerminalSpeed - CruiseSpeed);
                }
                result[rank] = new Schedule(ordered[rank].Id, rank, ordered.Length, (float)delay);
            }
            return result;
        }

        internal static float SpeedCeiling(float initialSpeed, float accelerationStartsAt, float now, float finalSpeed)
        {
            if (!Finite(initialSpeed) || !Finite(accelerationStartsAt) || !Finite(now) ||
                !Finite(finalSpeed) || finalSpeed <= 0f) return finalSpeed;
            if (initialSpeed >= finalSpeed || now >= accelerationStartsAt + RampSeconds) return finalSpeed;
            // This is permission for the native motor to add force, not a
            // requested rigidbody speed. A positive near-zero hold prevents
            // intermittent full-thrust pulses whenever drag drops the missile
            // just below its initial speed. The missile simply coasts through
            // this sub-second delay while its motor/effects keep burning.
            float start = .001f;
            float progress = Math.Max(0f, Math.Min(1f, (now - accelerationStartsAt) / RampSeconds));
            float smooth = progress * progress * (3f - 2f * progress);
            return start + (finalSpeed - start) * smooth;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
