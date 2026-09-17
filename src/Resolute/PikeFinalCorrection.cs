using System;

namespace Resolute
{
    // Temporarily raises only native steering caps during an assigned terminal
    // approach, with greater authority for the final straight run.
    // Sensor acceptance, aimpoints, PID, torque, lift, bank and velocity remain
    // owned by their existing systems. Re-entry never compounds the increase.
    internal sealed class PikeFinalCorrection
    {
        internal const float Range = .6f * PikeFlightProfile.NauticalMile;
        internal const float TurnRate = 45f;
        internal const float WeaveRange = 25f * PikeFlightProfile.NauticalMile;
        internal const float WeaveTurnRate = 20f;
        private float originalRate, originalG;
        private uint targetId;
        internal bool Active { get; private set; }
        internal bool ForTarget(uint target) => Active && targetId == target;

        internal void Update(bool eligible, uint target, float range, ref float rate, ref float g, bool weaving = false)
        {
            bool use = eligible && target != 0 && PikeFlightProfile.Finite(range) && range > 0f &&
                (range <= Range || weaving && range < WeaveRange);
            if (Active && (!use || target != targetId)) Restore(ref rate, ref g);
            if (!use) return;
            if (!Active)
            {
                if (!PikeFlightProfile.Finite(rate) || !PikeFlightProfile.Finite(g) || rate <= 0f || g <= 0f) return;
                originalRate = rate; originalG = g; targetId = target; Active = true;
            }
            rate = Math.Max(originalRate, range <= Range ? TurnRate : WeaveTurnRate);
            // Native ApplyAero uses the lesser of the angular and g caps.
            // Preserve the original relationship rather than changing only a
            // field that the other cap would silently cancel at sprint speed.
            g = originalG * rate / originalRate;
        }

        internal void Restore(ref float rate, ref float g)
        {
            if (!Active) return;
            rate = originalRate; g = originalG;
            Active = false; targetId = 0;
        }
    }
}
