using System;

namespace Resolute
{
    // Inventory and elapsed-time policy only. A missed update can issue one
    // pair, never catch up by dumping the protected terminal supply.
    internal sealed class PikeCountermeasureSchedule
    {
        internal const int EmitterCount = 8;
        internal const int FlaresPerBurst = 2;
        // Each network burst remains one opposed pair. An authored firing
        // cycle contains two such pairs, staggered by .4 seconds.
        internal const int PairsPerCycle = 2;
        internal const int BurstCapacity = 64;
        internal const int FlareCapacity = 128;
        internal const int EarlyFlareBudget = 16;
        internal const int ReservedFlareBudget = 112;
        internal const double PairIntervalSeconds = .4;
        internal const double CycleIntervalSeconds = 2;
        internal bool Started { get; private set; }
        internal bool ReserveReleased { get; private set; }
        internal int IssuedBursts { get; private set; }
        internal int EarlyPairs { get; private set; }
        internal int TerminalPairs { get; private set; }
        internal int EarlyCycles { get; private set; }
        internal int TerminalCycles { get; private set; }
        internal int RemainingFlares => FlareCapacity - IssuedBursts * FlaresPerBurst;
        internal int ReservedFlaresRemaining => ReservedFlareBudget - TerminalPairs * FlaresPerBurst;
        internal double EntryTime { get; private set; }
        internal double EntryRange { get; private set; }
        internal double ExpectedTerminalSeconds { get; private set; }
        internal double NominalIntervalSeconds => CycleIntervalSeconds;
        internal double LeadSeconds => PairIntervalSeconds;
        internal bool CompressedShortEntry { get; private set; }
        internal double LastPairTime { get; private set; } = double.NegativeInfinity;
        private double nextCycleTime = double.NegativeInfinity;
        private int pendingPairs;
        private bool pendingTerminal;

        internal int Advance(double now, double range, double closingSeconds,
            bool safelySeparated, bool dangerousIrThreat, bool terminalPhase)
        {
            if (!safelySeparated || !Finite(now) || now < 0 || !Finite(range) || range < 0 ||
                IssuedBursts >= BurstCapacity || now - LastPairTime + .000001 < PairIntervalSeconds)
                return 0;
            if (terminalPhase && !ReserveReleased)
            {
                ReserveReleased = true;
                ExpectedTerminalSeconds = closingSeconds;
                CompressedShortEntry = Finite(closingSeconds) && closingSeconds < PairIntervalSeconds;
                // Assignment/release starts its own sequence; an early
                // threat cycle cannot defer it behind the old cycle timer.
                pendingPairs = 0; nextCycleTime = now;
            }
            if (pendingPairs > 0 && pendingTerminal && !terminalPhase) pendingPairs = 0;
            if (pendingPairs == 0)
            {
                if (now + .000001 < nextCycleTime) return 0;
                if (terminalPhase && TerminalPairs < ReservedFlareBudget / FlaresPerBurst)
                {
                    pendingTerminal = true;
                    pendingPairs = Math.Min(PairsPerCycle, ReservedFlareBudget / FlaresPerBurst - TerminalPairs);
                    TerminalCycles++;
                }
                else if (dangerousIrThreat && EarlyPairs < EarlyFlareBudget / FlaresPerBurst)
                {
                    pendingTerminal = false;
                    pendingPairs = Math.Min(PairsPerCycle, EarlyFlareBudget / FlaresPerBurst - EarlyPairs);
                    EarlyCycles++;
                }
                else return 0;
                nextCycleTime = now + CycleIntervalSeconds;
            }
            if (!Started) { Started = true; EntryTime = now; EntryRange = range; }
            if (pendingTerminal) TerminalPairs++; else EarlyPairs++;
            pendingPairs--;
            LastPairTime = now;
            IssuedBursts++;
            // A frame stall may finish one late pair, never compress the next
            // complete cycle into the same update window to catch up.
            if (pendingPairs == 0 && now > nextCycleTime)
                nextCycleTime = now + CycleIntervalSeconds - PairIntervalSeconds;
            return 1;
        }

        internal static double ClosingSeconds(double range, double radialClosingSpeed)
        {
            return Finite(range) && range > 0 && Finite(radialClosingSpeed) && radialClosingSpeed > 1
                ? range / radialClosingSpeed : double.PositiveInfinity;
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    // One receipt bit for each of the 64 authoritative pairs. Handles duplicate
    // and out-of-order delivery without issuing any local ammunition.
    internal sealed class PikeBurstReceipt
    {
        private ulong mask;
        internal int AcceptedCount { get; private set; }
        internal bool TryAccept(int index)
        {
            if (index < 0 || index >= PikeCountermeasureSchedule.BurstCapacity) return false;
            ulong bit = 1ul << index;
            if ((mask & bit) != 0) return false;
            mask |= bit;
            AcceptedCount++;
            return true;
        }
    }
}
