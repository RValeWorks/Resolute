using System;

namespace Resolute
{
    internal static class PikeEcmGate
    {
        internal static bool IsActive(bool localSimulation, bool enteredOnAuthority, bool remoteTargetKnown,
            bool flightPhaseReady, bool engineRunning, bool disabled, double range, double terminalRange)
        {
            if (disabled || !flightPhaseReady || !engineRunning || double.IsNaN(range) || double.IsInfinity(range) ||
                double.IsNaN(terminalRange) || double.IsInfinity(terminalRange) || range <= 0 || range > terminalRange)
                return false;
            // A late observer may have received none of the six flare bursts.
            // Its native replicated target/flight state, not burst receipts,
            // determines the displayed/queryable defensive ECM state.
            return localSimulation ? enteredOnAuthority : remoteTargetKnown;
        }
    }
}
