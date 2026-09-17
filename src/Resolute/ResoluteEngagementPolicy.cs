using System;

namespace Resolute
{
    // Balanced doctrine. These limits allocate additional native attacks; they
    // never replace sensor, weapon-envelope or seeker eligibility.
    internal static class ResoluteEngagementPolicy
    {
        internal const int SurfaceWaveSize = 8;
        internal const int DefensiveBatchSize = 2;
        internal const int ReserveWaves = 2;
        internal const int MaximumStrikeGroups = 3;
        internal const int MaximumCommittedSurfaceShots = SurfaceWaveSize * MaximumStrikeGroups;
        internal const float MaximumTrackAge = 20f;
        internal const float ContactGroupRadius = 12000f;
        internal const float SurfaceReassessmentSeconds = 10f;

        internal static int Reserve(int initialAmmo) => Math.Min(Math.Max(0, initialAmmo), ReserveWaves * SurfaceWaveSize);

        internal static int Budget(bool surface, int ammo, int initialAmmo)
            => Budget(surface, ammo, initialAmmo, false, 1);

        internal static int Budget(bool surface, int ammo, int initialAmmo, bool selfDefense, int groups)
        {
            int spendable = Math.Max(0, ammo - (surface && !selfDefense ? Reserve(initialAmmo) : 0));
            return Math.Min(spendable, surface ? SurfaceWaveSize * Math.Max(1, Math.Min(groups, MaximumStrikeGroups)) : DefensiveBatchSize);
        }

        // Only confirmed interception observations justify saturation. Unknown
        // disappearance and target clearance are deliberately not evidence.
        internal static int StrikeGroups(int intercepted, int observedOutcomes)
        {
            if (intercepted < 2 || observedOutcomes < intercepted) return 1;
            float fraction = intercepted / (float)Math.Max(1, observedOutcomes);
            return intercepted >= 4 && fraction >= .75f ? 3 : fraction >= .5f ? 2 : 1;
        }

        internal static int StrikeDemand(float nominal, int confirmedHits, int groups, int committed)
        {
            if (!Finite(nominal) || nominal <= 0f) return 0;
            int baseline = Math.Min(MaximumCommittedSurfaceShots, (int)Math.Ceiling(nominal));
            // An observed hit is useful evidence, not knowledge of remaining
            // HP. Limit this credit; a surviving objective still needs a shot.
            int remaining = Math.Max(1, baseline - Math.Min(Math.Max(0, confirmedHits), baseline / 2));
            return Math.Max(0, Math.Min(MaximumCommittedSurfaceShots,
                remaining * Math.Max(1, Math.Min(groups, MaximumStrikeGroups))) - Math.Max(0, committed));
        }

        internal static bool TrackUsable(float now, float observedAt) => Finite(now) && Finite(observedAt) &&
            observedAt <= now && now - observedAt <= MaximumTrackAge;

        internal static int AdditionalDemand(float nativeDemand, int airborne, int reserved)
        {
            if (!Finite(nativeDemand) || nativeDemand <= 0f) return 0;
            return Math.Max(0, (int)Math.Ceiling(Math.Min(nativeDemand, 100f) -
                Math.Max(0, airborne) - Math.Max(0, reserved)));
        }

        // Inputs are highest priority first. Cover each admitted contact before
        // putting the remaining rounds onto contacts that need multiple shots.
        internal static int[] Allocate(int[] demand, int budget)
        {
            int[] result = new int[demand.Length];
            budget = Math.Max(0, Math.Min(budget, MaximumCommittedSurfaceShots));
            bool changed = true;
            while (budget > 0 && changed)
            {
                changed = false;
                for (int i = 0; i < demand.Length && budget > 0; i++)
                    if (result[i] < Math.Max(0, demand[i]))
                    { result[i]++; budget--; changed = true; }
            }
            return result;
        }

        internal static float TimeToImpact(float distance, float closingSpeed)
        {
            return Finite(distance) && Finite(closingSpeed) && distance >= 0f && closingSpeed > 1f
                ? distance / closingSpeed : float.PositiveInfinity;
        }

        internal static float InterceptorRank(float cost, float flightSeconds, float impactSeconds)
        {
            if (!Finite(cost) || !Finite(flightSeconds) || cost < 0f || flightSeconds < 0f)
                return float.PositiveInfinity;
            // An urgent threat favors the shortest estimated engagement time.
            // Otherwise prefer an economical available layer. Native viability
            // still decides whether the candidate is eligible in the first place.
            // Escalate for an interception deadline, not merely because the
            // threat is inside an arbitrary twenty-second radius. A Ward with
            // ample time should not lose to Sentinel for saving a few seconds.
            bool urgent = Finite(impactSeconds) && flightSeconds + 4f >= impactSeconds;
            float late = Finite(impactSeconds) && flightSeconds >= impactSeconds ? 10000f : 0f;
            return late + flightSeconds * (urgent ? 10f : .2f) + cost;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
