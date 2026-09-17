using System;
using System.Collections.Generic;

namespace Resolute
{
    // Geometry and integer allocation only. Sensor eligibility and retargeting
    // remain the responsibility of the authoritative, native-facing adapter.
    internal static class PikeGroupTargetingPolicy
    {
        internal const int MaximumMembers = 8;
        internal const int MaximumTargets = 64;
        internal const float LeaderSearchAltitude = 50f;
        internal const float SearchIntervalSeconds = 1f;

        internal struct Member
        {
            internal uint Id;
            internal double Lateral;
            internal Member(uint id, double lateral) { Id = id; Lateral = lateral; }
        }
        internal struct Target
        {
            internal uint Id;
            internal double Lateral, Tonnage;
            internal Target(uint id, double lateral, double tonnage) { Id = id; Lateral = lateral; Tonnage = tonnage; }
        }
        internal struct Assignment
        {
            internal uint MissileId, TargetId;
            internal Assignment(uint missileId, uint targetId) { MissileId = missileId; TargetId = targetId; }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static Member[] OrderedMembers(Member[] source)
        {
            if (source == null || source.Length > MaximumMembers) throw new ArgumentException("Invalid Pike group members.");
            var ids = new HashSet<uint>();
            var result = (Member[])source.Clone();
            foreach (Member member in result)
                if (member.Id == 0 || !Finite(member.Lateral) || !ids.Add(member.Id))
                    throw new ArgumentException("Pike member identity and lateral position must be unique and finite.");
            Array.Sort(result, (a, b) => { int order = a.Lateral.CompareTo(b.Lateral); return order != 0 ? order : a.Id.CompareTo(b.Id); });
            return result;
        }

        // With an even group, either middle lane is central. Keep an eligible
        // incumbent instead of switching leaders when their positions jitter.
        internal static uint CentralLeader(Member[] members, uint previousLeader)
        {
            Member[] ordered = OrderedMembers(members);
            if (ordered.Length == 0) return 0;
            int right = ordered.Length / 2, left = (ordered.Length - 1) / 2;
            if (ordered[left].Id == previousLeader || ordered[right].Id == previousLeader) return previousLeader;
            return Math.Min(ordered[left].Id, ordered[right].Id);
        }

        internal static Assignment[] Allocate(Member[] members, Target[] targets)
        {
            Member[] ordered = OrderedMembers(members);
            if (targets == null || targets.Length > MaximumTargets) throw new ArgumentException("Invalid Pike target list.");
            var ids = new HashSet<uint>();
            var selected = new List<Target>(targets.Length);
            foreach (Target target in targets)
            {
                if (target.Id == 0 || !Finite(target.Lateral) || !Finite(target.Tonnage) || target.Tonnage <= 0 || !ids.Add(target.Id))
                    throw new ArgumentException("Pike target identity, position and tonnage must be valid and unique.");
                selected.Add(target);
            }
            if (ordered.Length == 0 || selected.Count == 0) return new Assignment[0];
            selected.Sort((a, b) => { int weight = b.Tonnage.CompareTo(a.Tonnage); return weight != 0 ? weight : a.Id.CompareTo(b.Id); });
            if (selected.Count > ordered.Length) selected.RemoveRange(ordered.Length, selected.Count - ordered.Length);
            // One round per selected ship, then Hamilton's largest-remainder
            // allocation of EXTRA rounds by tonnage. Normalize before summing
            // so finite large weights cannot overflow the denominator.
            int extra = ordered.Length - selected.Count;
            double maximum = selected[0].Tonnage, total = 0;
            foreach (Target target in selected) total += target.Tonnage / maximum;
            int[] counts = new int[selected.Count];
            double[] remainders = new double[selected.Count];
            int remaining = extra;
            for (int i = 0; i < selected.Count; i++)
            {
                double share = extra * (selected[i].Tonnage / maximum) / total;
                int whole = (int)Math.Floor(share);
                counts[i] = 1 + whole; remaining -= whole;
                remainders[i] = share - whole;
            }
            var remainderOrder = new List<int>(selected.Count);
            for (int i = 0; i < selected.Count; i++) remainderOrder.Add(i);
            remainderOrder.Sort((a, b) => Math.Abs(remainders[a] - remainders[b]) > 1e-10
                ? remainders[b].CompareTo(remainders[a]) : selected[a].Id.CompareTo(selected[b].Id));
            for (int i = 0; i < remaining; i++) counts[remainderOrder[i]]++;
            var lanes = new List<Target>(ordered.Length);
            for (int i = 0; i < selected.Count; i++)
                for (int count = 0; count < counts[i]; count++) lanes.Add(selected[i]);
            lanes.Sort((a, b) => { int lateral = a.Lateral.CompareTo(b.Lateral); return lateral != 0 ? lateral : a.Id.CompareTo(b.Id); });
            var assignments = new Assignment[ordered.Length];
            for (int i = 0; i < assignments.Length; i++) assignments[i] = new Assignment(ordered[i].Id, lanes[i].Id);
            return assignments;
        }

        private struct FeasibleState
        {
            internal bool Valid;
            internal int Covered, QuotaError;
            internal double CoveredWeight;
            // Eight seven-bit target indexes (zero means unassigned) fit in
            // one value. No per-candidate allocation is needed during the DP.
            internal ulong Plan;
        }

        // Receiver constraints belong to their ORIGINAL input row/column.
        // Preserve the unconstrained plan whenever it is receivable. Otherwise
        // maximize assigned rounds, then covered ships, then covered tonnage,
        // then closeness to that plan's integer Hamilton quotas. Every feasible
        // plan retains left-to-right order; an isolated receiver cannot veto
        // assignments for the rest of the group.
        internal static Assignment[] AllocateFeasible(Member[] members, Target[] targets, bool[,] allowed)
        {
            Assignment[] ideal = Allocate(members, targets); // full input validation
            if (allowed == null || allowed.GetLength(0) != members.Length || allowed.GetLength(1) != targets.Length)
                throw new ArgumentException("Pike receiver matrix must match the original member and target arrays.");
            if (ideal.Length == 0) return ideal;
            var memberIndex = new Dictionary<uint, int>(members.Length);
            var targetIndex = new Dictionary<uint, int>(targets.Length);
            for (int i = 0; i < members.Length; i++) memberIndex.Add(members[i].Id, i);
            for (int i = 0; i < targets.Length; i++) targetIndex.Add(targets[i].Id, i);
            bool idealAllowed = true;
            foreach (Assignment assignment in ideal)
                if (!allowed[memberIndex[assignment.MissileId], targetIndex[assignment.TargetId]]) { idealAllowed = false; break; }
            if (idealAllowed) return ideal;

            Member[] orderedMembers = OrderedMembers(members);
            Target[] orderedTargets = (Target[])targets.Clone();
            Array.Sort(orderedTargets, (a, b) => { int position = a.Lateral.CompareTo(b.Lateral); return position != 0 ? position : a.Id.CompareTo(b.Id); });
            var idealCounts = new Dictionary<uint, int>(targets.Length);
            foreach (Assignment assignment in ideal)
            {
                int count; idealCounts.TryGetValue(assignment.TargetId, out count);
                idealCounts[assignment.TargetId] = count + 1;
            }
            int size = 1 << orderedMembers.Length, fullMask = size - 1;
            var populations = new int[size];
            var suffixes = new int[size];
            suffixes[0] = fullMask;
            for (int mask = 1; mask < size; mask++)
            {
                populations[mask] = populations[mask >> 1] + (mask & 1);
                int highest = 0;
                for (int bits = mask; bits > 1; bits >>= 1) highest++;
                suffixes[mask] = fullMask & ~((1 << (highest + 1)) - 1);
            }
            double maximumWeight = 0;
            foreach (Target candidate in orderedTargets) maximumWeight = Math.Max(maximumWeight, candidate.Tonnage);
            var current = new FeasibleState[size];
            var next = new FeasibleState[size];
            current[0].Valid = true;
            for (int column = 0; column < orderedTargets.Length; column++)
            {
                Target candidate = orderedTargets[column];
                int expected; idealCounts.TryGetValue(candidate.Id, out expected);
                int receiverMask = 0;
                for (int row = 0; row < orderedMembers.Length; row++)
                    if (allowed[memberIndex[orderedMembers[row].Id], targetIndex[candidate.Id]]) receiverMask |= 1 << row;
                Array.Clear(next, 0, next.Length);
                for (int mask = 0; mask < size; mask++)
                {
                    FeasibleState previous = current[mask];
                    if (!previous.Valid) continue;
                    FeasibleState skipped = previous;
                    skipped.QuotaError += expected;
                    if (Better(skipped, next[mask], orderedTargets, orderedMembers.Length)) next[mask] = skipped;
                    // A later target may use only lanes after every already
                    // assigned lane. Skipped lanes stay unassigned, so this
                    // enumerates all monotonic plans without forced crossings.
                    int available = receiverMask & suffixes[mask];
                    for (int subset = available; subset != 0; subset = (subset - 1) & available)
                    {
                        FeasibleState offered = previous;
                        offered.Covered++;
                        offered.CoveredWeight += candidate.Tonnage / maximumWeight;
                        offered.QuotaError += Math.Abs(populations[subset] - expected);
                        for (int row = 0; row < orderedMembers.Length; row++)
                            if ((subset & (1 << row)) != 0) offered.Plan |= (ulong)(column + 1) << (row * 7);
                        int combined = mask | subset;
                        if (Better(offered, next[combined], orderedTargets, orderedMembers.Length)) next[combined] = offered;
                    }
                }
                FeasibleState[] temporary = current; current = next; next = temporary;
            }
            FeasibleState best = default(FeasibleState);
            int bestAssigned = -1;
            for (int mask = 0; mask < size; mask++)
                if (current[mask].Valid && (populations[mask] > bestAssigned ||
                    populations[mask] == bestAssigned && Better(current[mask], best, orderedTargets, orderedMembers.Length)))
                { best = current[mask]; bestAssigned = populations[mask]; }
            var result = new Assignment[bestAssigned];
            int at = 0;
            for (int row = 0; row < orderedMembers.Length; row++)
            {
                int column = (int)((best.Plan >> (row * 7)) & 127) - 1;
                if (column >= 0) result[at++] = new Assignment(orderedMembers[row].Id, orderedTargets[column].Id);
            }
            return result;
        }

        private static bool Better(FeasibleState candidate, FeasibleState incumbent, Target[] targets, int memberCount)
        {
            if (!candidate.Valid) return false;
            if (!incumbent.Valid) return true;
            if (candidate.Covered != incumbent.Covered) return candidate.Covered > incumbent.Covered;
            if (candidate.CoveredWeight != incumbent.CoveredWeight) return candidate.CoveredWeight > incumbent.CoveredWeight;
            if (candidate.QuotaError != incumbent.QuotaError) return candidate.QuotaError < incumbent.QuotaError;
            // Stable target IDs break otherwise equal plans; an assigned lane
            // precedes an unassigned one. This does not depend on input order.
            for (int row = 0; row < memberCount; row++)
            {
                int a = (int)((candidate.Plan >> (row * 7)) & 127) - 1;
                int b = (int)((incumbent.Plan >> (row * 7)) & 127) - 1;
                ulong first = a < 0 ? ulong.MaxValue : targets[a].Id;
                ulong second = b < 0 ? ulong.MaxValue : targets[b].Id;
                if (first != second) return first < second;
            }
            return false;
        }
    }
}
