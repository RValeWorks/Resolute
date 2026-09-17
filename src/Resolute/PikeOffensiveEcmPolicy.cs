using System;

namespace Resolute
{
    // A deliberately weak, finite-duty native interference source. These are
    // game-scale pulse limits, not a probability of defeating a receiver.
    internal static class PikeOffensiveEcmPolicy
    {
        internal const int MaximumMembers = 8, MaximumGroups = 256;
        internal const int ThreatCapacity = 32, ReceiverCapacity = 16, RegistryChecks = 64;
        internal const float Range = 12000f, MaximumPulse = .08f;
        internal const double SlotSeconds = 2, ActiveSeconds = 1.5, PulseSeconds = .25;
        internal const float ScanSeconds = .5f;

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static float Strength(float distance)
        {
            if (!Finite(distance) || distance < 0f || distance >= Range) return 0f;
            return MaximumPulse * (1f - distance / Range);
        }
        internal static bool Transmitting(double now) => Finite(now) && now >= 0 && now % SlotSeconds < ActiveSeconds;
    }

    // Appended membership slots never move when a missile dies. Rotation is
    // time-driven, not loss-driven; a dead current emitter is replaced at once.
    // One shared pulse clock prevents a handoff from doubling the group's rate.
    internal sealed class PikeOffensiveEcmRotation
    {
        private long lastSlot = -1;
        private int selected = -1;
        private double lastPulse = double.NegativeInfinity;

        internal uint Select(double now, uint[] ids, bool[] eligible, int count)
        {
            if (!PikeOffensiveEcmPolicy.Finite(now) || now < 0 || ids == null || eligible == null ||
                count < 1 || count > PikeOffensiveEcmPolicy.MaximumMembers || count > ids.Length || count > eligible.Length)
                return 0;
            long slot = (long)Math.Floor(now / PikeOffensiveEcmPolicy.SlotSeconds);
            int living = 0;
            for (int i = 0; i < count; i++) if (ids[i] != 0 && eligible[i]) living++;
            if (living == 0) { lastSlot = slot; return 0; }
            bool valid = selected >= 0 && selected < count && ids[selected] != 0 && eligible[selected];
            long steps = lastSlot >= 0 && slot > lastSlot ? (slot - lastSlot) % living : 0;
            if (!valid) steps = Math.Max(1L, steps);
            while (steps-- > 0)
                for (int checkedSlots = 0; checkedSlots < count; checkedSlots++)
                {
                    selected = (selected + 1) % count;
                    if (ids[selected] != 0 && eligible[selected]) break;
                }
            lastSlot = slot;
            return PikeOffensiveEcmPolicy.Transmitting(now) ? ids[selected] : 0u;
        }

        internal bool TryPulse(double now)
        {
            if (!PikeOffensiveEcmPolicy.Transmitting(now) || now - lastPulse + .000001 < PikeOffensiveEcmPolicy.PulseSeconds)
                return false;
            lastPulse = now;
            return true;
        }
    }
}
