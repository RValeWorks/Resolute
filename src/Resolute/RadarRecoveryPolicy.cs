namespace Resolute
{
    internal enum RadarRecoveryDecision
    {
        RadarLock, NativeTerminalMemory, MissingTarget, NonHostileTarget,
        NoHq, Jammed, Occluded, NativeMemoryExpired
    }

    // Diagnostic classification only. Native ARH owns target retention and
    // seeker limits; no result from this class authorizes a guidance action.
    internal static class RadarRecoveryPolicy
    {
        internal static RadarRecoveryDecision Observe(uint targetId, bool liveHostile,
            bool hasHq, float radarReturn, float minimumSignal, bool jammed,
            float secondsWithoutReturn, float nativeLockPerseverance)
        {
            if (targetId == 0) return RadarRecoveryDecision.MissingTarget;
            if (!liveHostile) return RadarRecoveryDecision.NonHostileTarget;
            // TerminalMode uses < minimumSignal for a failed return; preserve
            // its equality boundary rather than classify it as a dropout.
            if (radarReturn >= minimumSignal) return RadarRecoveryDecision.RadarLock;
            if (radarReturn == -1f) return RadarRecoveryDecision.Occluded;
            if (secondsWithoutReturn > nativeLockPerseverance) return RadarRecoveryDecision.NativeMemoryExpired;
            if (jammed) return RadarRecoveryDecision.Jammed;
            if (!hasHq) return RadarRecoveryDecision.NoHq;
            return RadarRecoveryDecision.NativeTerminalMemory;
        }
    }
}
