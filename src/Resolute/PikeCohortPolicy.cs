using System;
using System.Collections.Generic;

namespace Resolute
{
    internal static class PikeLaunchPolicy
    {
        internal static bool ClearToGuide(bool vertical, float age, float nativeDelay, float rise, float length)
        {
            return !vertical || age > nativeDelay && rise >= length;
        }
    }
    // Pure membership policy. A cohort may be incomplete; no launch or terminal
    // transition ever waits for another round to arrive.
    internal sealed class PikeCohortPolicy
    {
        internal const int MaximumMembers = 8;
        internal const int MaximumLiveMembers = 256;
        internal const double DefaultSalvoIntervalSeconds = 1.5;
        internal const double CorridorCosine = .984807753; // native waypoint's ten-degree heading step
        internal static double JoinWindow(double interval) => (MaximumMembers - 1) * interval + .5;
        internal sealed class Cohort
        {
            internal uint Id, Owner, OrderId;
            internal double FirstLaunch, LastLaunch, JoinWindowSeconds;
            internal float HeadingX, HeadingZ;
            internal int Joined;
            internal readonly uint[] Members = new uint[MaximumMembers];
        }
        private readonly Dictionary<uint, Cohort> memberships = new Dictionary<uint, Cohort>();
        private readonly List<Cohort> cohorts = new List<Cohort>();

        internal Cohort Join(uint id, uint owner, uint target, double launchedAt, double now,
            float headingX = 0f, float headingZ = 1f, double salvoInterval = DefaultSalvoIntervalSeconds, uint requestedGroup = 0)
        {
            if (id == 0 || owner == 0 || target == 0 && requestedGroup == 0 || double.IsNaN(now) || double.IsInfinity(now) ||
                double.IsNaN(launchedAt) || double.IsInfinity(launchedAt) || launchedAt > now ||
                double.IsNaN(salvoInterval) || double.IsInfinity(salvoInterval) || salvoInterval <= 0 ||
                float.IsNaN(headingX) || float.IsNaN(headingZ) || float.IsInfinity(headingX) || float.IsInfinity(headingZ)) return null;
            double headingLength = Math.Sqrt(headingX * headingX + headingZ * headingZ);
            if (headingLength < .001 || double.IsInfinity(headingLength)) return null;
            headingX = (float)(headingX / headingLength); headingZ = (float)(headingZ / headingLength);
            Cohort current;
            if (memberships.TryGetValue(id, out current))
            {
                if (current.Owner == owner && current.OrderId == requestedGroup) return current;
                Leave(id);
            }
            if (memberships.Count >= MaximumLiveMembers) return null;
            Cohort match = null;
            for (int i = 0; i < cohorts.Count; i++)
            {
                Cohort c = cohorts[i];
                if (c.Owner == owner && c.OrderId == requestedGroup && c.Joined < MaximumMembers &&
                    (requestedGroup != 0 || headingX * c.HeadingX + headingZ * c.HeadingZ >= CorridorCosine &&
                    Math.Max(launchedAt, c.LastLaunch) - Math.Min(launchedAt, c.FirstLaunch) <= c.JoinWindowSeconds))
                { match = c; break; }
            }
            if (match == null)
            {
                // Cohort Id remains the first real missile's globally unique ID.
                // OrderId is a separate namespace for ship-issued membership.
                match = new Cohort { Id = id, Owner = owner, OrderId = requestedGroup, FirstLaunch = launchedAt, LastLaunch = launchedAt,
                    HeadingX = headingX, HeadingZ = headingZ, JoinWindowSeconds = JoinWindow(salvoInterval) };
                cohorts.Add(match);
            }
            match.Members[match.Joined++] = id;
            match.FirstLaunch = Math.Min(match.FirstLaunch, launchedAt);
            match.LastLaunch = Math.Max(match.LastLaunch, launchedAt);
            memberships.Add(id, match);
            return match;
        }

        internal void Leave(uint id)
        {
            Cohort cohort;
            if (!memberships.TryGetValue(id, out cohort)) return;
            memberships.Remove(id);
            bool remaining = false;
            for (int i = 0; i < cohort.Members.Length; i++)
            {
                if (cohort.Members[i] == id) cohort.Members[i] = 0;
                remaining |= cohort.Members[i] != 0;
            }
            if (!remaining) cohorts.Remove(cohort);
        }

        internal static float HoldingThrottle(float aheadM, float spacingM, float speedMps, float heldSeconds, int members)
        {
            if (members < 2 || heldSeconds >= 12f || speedMps < 450f || aheadM <= spacingM) return 1f;
            float blend = Math.Max(0f, Math.Min(1f, (aheadM - spacingM) / Math.Max(250f, spacingM * 3f)));
            return 1f - .65f * blend;
        }
    }

    internal static class PikeCruisePolicy
    {
        // Match native PreTerminalMode's range/age gates while retaining the
        // ARH lock requirement; no seeker or datalink condition is relaxed.
        internal static bool ReleaseForNativeTerminal(bool activeLock, float age, float range, float nativeRange)
            => activeLock && age > 6f && range < nativeRange;
        internal static float CombineThrottle(float nativeThrottle, float rendezvousThrottle)
            => Math.Min(nativeThrottle, rendezvousThrottle);
    }

    // Reproducible per-missile timing/amplitude variation; native JinkEvasion
    // still supplies the random lateral direction and the actual steering law.
    internal sealed class PikeJinkTiming
    {
        private uint state;
        private double next = double.NegativeInfinity;
        internal PikeJinkTiming(uint seed) { state = seed ^ 0x9e3779b9u; if (state == 0) state = 1; }
        private float Next()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xffffffu) / 16777216f;
        }
        internal bool TryChange(double now, float nativePeriod, out float dwell, out float scale)
        {
            dwell = 0; scale = 0;
            if (double.IsNaN(now) || double.IsInfinity(now) || now < next) return false;
            float basis = Math.Max(.4f, Math.Min(3f, nativePeriod > 0 ? nativePeriod : 1f));
            dwell = basis * (.65f + .7f * Next());
            scale = .65f + .55f * Next();
            next = now + dwell;
            return true;
        }
    }
}
