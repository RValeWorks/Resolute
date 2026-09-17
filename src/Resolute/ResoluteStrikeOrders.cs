using System;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Plans reserve group membership, not sensor authority or a launch. Only
    // the native owner's registration event consumes a matching reservation.
    internal static class ResoluteStrikeOrders
    {
        internal const int MaximumStrikeShots = 24;
        private const int MaximumOwners = 64, MaximumOrders = 256;
        private const float ObservationMemory = 20f, MaximumPredictionSeconds = 900f, MaximumLifetime = 1800f;
        private sealed class Observation
        {
            internal Unit Target;
            internal GlobalPosition Position;
            internal Vector3 Velocity;
            internal float SeenAt, SampledAt;
            internal int Weight, Pending;
            internal bool AllowFriendly;
        }
        private sealed class Order
        {
            internal uint Id;
            internal int ManualOrderId;
            internal Unit ManualTarget;
            internal FactionHQ Hq;
            internal GlobalPosition Origin, SearchPoint;
            internal Vector3 Course;
            internal PikeThreatZone ThreatZone;
            internal float Created, Expires, UsefulUntil, NextUpdate;
            internal readonly List<Observation> Targets = new List<Observation>(8);
            internal readonly List<Missile> Members = new List<Missile>(8);
            internal int Pending;
            internal bool HasPoint, Area;
        }
        private sealed class OwnerState
        {
            internal Ship Owner;
            internal readonly List<Order> Orders = new List<Order>(3);
            internal Action<Missile> Registered;
            internal Action<Unit> Disabled;
        }
        private static readonly Dictionary<Ship, OwnerState> Owners = new Dictionary<Ship, OwnerState>();
        private static readonly Dictionary<Missile, Order> Membership = new Dictionary<Missile, Order>();
        private static readonly List<Ship> RemoveOwners = new List<Ship>(MaximumOwners);
        private static uint nextId;
        private static float nextSweep, lastSweep = -1f;

        internal static bool Commit(Ship owner, Unit[] targets, int[] shots, float now)
        {
            int total = Count(shots);
            return total > 0 && total <= PikeCohortPolicy.MaximumMembers && CommitStrike(owner, targets, shots, now);
        }

        // Atomic admission of up to three groups. Round-robin distribution
        // retains each target's total while keeping each group at eight or less.
        internal static bool CommitStrike(Ship owner, Unit[] targets, int[] shots, float now)
            => CommitCore(owner, targets, shots, now, 0);

        internal static bool CommitManual(Ship owner, Unit target, int count, int orderId, int[] replaceOrderIds = null)
        {
            if (orderId <= 0 || count != 1 && count != 2 && count != 4 && count != 8 && count != 16 && count != 20) return false;
            if (owner != null && Owners.TryGetValue(owner, out OwnerState existing))
                foreach (Order order in existing.Orders) if (order.ManualOrderId == orderId) return false;
            return CommitCore(owner, new[] { target }, new[] { count }, Time.timeSinceLevelLoad, orderId, replaceOrderIds);
        }

        internal static bool CommitManualPosition(Ship owner, GlobalPosition point, int count, int orderId, int[] replaceOrderIds = null)
        {
            float now = Time.timeSinceLevelLoad;
            Sweep(now);
            if (owner == null || owner.disabled || !owner.LocalSim || owner.NetworkHQ == null || !Finite(point) ||
                !Finite(now) || orderId <= 0 || count != 1 && count != 2 && count != 4 && count != 8 && count != 16 && count != 20) return false;
            Owners.TryGetValue(owner, out OwnerState state);
            ReplacementCredit(state, replaceOrderIds, out int pendingCredit, out int orderCredit);
            if (state == null && Owners.Count >= MaximumOwners || OrderCount() - orderCredit + (count + 7) / 8 > MaximumOrders ||
                GlobalOutstanding() - pendingCredit + count > PikeCohortPolicy.MaximumLiveMembers) return false;
            if (state != null)
                foreach (Order existing in state.Orders) if (existing.ManualOrderId == orderId) return false;
            Vector3 course = point - owner.GlobalPosition(); course.y = 0f;
            if (course.sqrMagnitude < 1f) { course = owner.transform.forward; course.y = 0f; }
            if (!Finite(course) || course.sqrMagnitude < .001f) return false;
            ApplyReplacement(owner, state, replaceOrderIds);
            state = EnsureOwner(owner, state);
            float duration = Mathf.Max(181f, QueueLifetime(owner, count));
            for (int remaining = count; remaining > 0; remaining -= 8)
                state.Orders.Add(new Order { Id = NewId(), ManualOrderId = orderId, Area = true,
                    Hq = owner.NetworkHQ, Origin = owner.GlobalPosition(), SearchPoint = point,
                    Course = course.normalized, HasPoint = true, Pending = Math.Min(8, remaining),
                    Created = now, Expires = now + duration,
                    UsefulUntil = now + duration + (point - owner.GlobalPosition()).magnitude / PikeFormationFlightPolicy.CruiseSpeed + 90f });
            return true;
        }

        private static OwnerState EnsureOwner(Ship owner, OwnerState state)
        {
            if (state != null) return state;
            state = new OwnerState { Owner = owner };
            OwnerState captured = state;
            state.Registered = missile => Bind(captured, missile);
            state.Disabled = _ => ResetOwner(owner);
            owner.onRegisterMissile += state.Registered;
            owner.onDisableUnit += state.Disabled;
            Owners.Add(owner, state);
            return state;
        }

        private static bool CommitCore(Ship owner, Unit[] targets, int[] shots, float now, int manualOrderId, int[] replaceOrderIds = null)
        {
            Sweep(now);
            int total = Count(shots);
            if (owner == null || owner.disabled || !owner.LocalSim || owner.NetworkHQ == null ||
                !Finite(now) || targets == null || shots == null || targets.Length != shots.Length ||
                total <= 0 || total > MaximumStrikeShots) return false;
            OwnerState state;
            Owners.TryGetValue(owner, out state);
            ReplacementCredit(state, replaceOrderIds, out int pendingCredit, out int orderCredit);
            if (state == null && Owners.Count >= MaximumOwners || manualOrderId == 0 && UsefulOutstanding(state, now) + total > MaximumStrikeShots ||
                OrderCount() - orderCredit + (total + 7) / 8 > MaximumOrders ||
                GlobalOutstanding() - pendingCredit + total > PikeCohortPolicy.MaximumLiveMembers) return false;
            var observations = new Observation[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                if (shots[i] == 0) continue;
                Unit target = targets[i];
                if (!(target is Ship) || target.disabled || target.persistentID.NotValid ||
                    target.NetworkHQ == null || manualOrderId == 0 && target.NetworkHQ == owner.NetworkHQ) return false;
                observations[i] = new Observation { Target = target, AllowFriendly = manualOrderId != 0,
                    SeenAt = float.NegativeInfinity };
                // A player may order a poor or stale interception. This grants
                // membership only, never an observation or a seeker lock.
                if (!Sample(observations[i], owner.NetworkHQ, now) && manualOrderId == 0) return false;
            }
            float duration = QueueLifetime(owner, total);
            // Manual orders can wait behind earlier explicit salvos. Their
            // executor retires uncompleted orders after 180 seconds; retain
            // the reservation through that window plus a final tick margin.
            if (manualOrderId != 0) duration = Mathf.Max(duration, 181f);
            var plans = new List<Order>(3);
            var remaining = (int[])shots.Clone();
            Order plan = null;
            int allocated = 0;
            while (allocated < total)
                for (int i = 0; i < remaining.Length; i++)
                {
                    if (remaining[i] == 0) continue;
                    if (plan == null || plan.Pending == 8)
                    {
                        plan = new Order { Id = NewId(), Hq = owner.NetworkHQ, Origin = owner.GlobalPosition(),
                            Created = now, Expires = now + duration, ManualOrderId = manualOrderId,
                            ManualTarget = manualOrderId != 0 ? targets[i] : null };
                        plans.Add(plan);
                    }
                    Observation entry = plan.Targets.Find(o => o.Target == targets[i]);
                    if (entry == null)
                    {
                        Observation source = observations[i];
                        entry = new Observation { Target = source.Target, Position = source.Position, Velocity = source.Velocity,
                            SeenAt = source.SeenAt, SampledAt = source.SampledAt, AllowFriendly = source.AllowFriendly };
                        plan.Targets.Add(entry);
                    }
                    entry.Weight++; entry.Pending++; plan.Pending++; remaining[i]--; allocated++;
                }
            foreach (Order order in plans)
            {
                float longestRange = 0f;
                foreach (Observation observation in order.Targets)
                {
                    float range = (observation.Position - order.Origin).magnitude;
                    if (!Finite(range)) return false;
                    longestRange = Mathf.Max(longestRange, range);
                }
                // Same useful flight allowance as the ship's launch ledger:
                // Pike's 1697.67 m/s maximum * .5 is its 848.83 m/s cruise.
                // Include the queue window because these orders precede launch.
                order.UsefulUntil = now + duration + longestRange / PikeFormationFlightPolicy.CruiseSpeed + 90f;
            }
            ApplyReplacement(owner, state, replaceOrderIds);
            state = EnsureOwner(owner, state);
            state.Orders.AddRange(plans);
            foreach (Order order in plans) UpdatePoint(order, now);
            return true;
        }

        // A normal replacement may arrive while the hard global budget is
        // full. Credit only this owner's explicitly replaced, unlaunched
        // reservations. Commit their cancellation after all new validation;
        // never discard old orders merely because a replacement was rejected.
        private static void ReplacementCredit(OwnerState state, int[] orderIds, out int pending, out int orders)
        {
            pending = 0; orders = 0;
            if (state == null || orderIds == null) return;
            foreach (Order order in state.Orders)
                if (order.ManualOrderId > 0 && Array.IndexOf(orderIds, order.ManualOrderId) >= 0)
                {
                    pending += order.Pending;
                    if (order.Members.Count == 0) orders++;
                }
        }

        private static void ApplyReplacement(Ship owner, OwnerState state, int[] orderIds)
        {
            if (state == null || orderIds == null) return;
            foreach (int id in orderIds) if (id > 0) CancelManual(owner, id);
            for (int i = state.Orders.Count - 1; i >= 0; i--)
                if (state.Orders[i].Pending == 0 && state.Orders[i].Members.Count == 0) state.Orders.RemoveAt(i);
        }

        internal static bool IsAuthorizedLaunch(Unit owner, Unit target)
        {
            if (!(owner is Ship ship) || owner.disabled || target == null || target.disabled) return false;
            Sweep(Time.timeSinceLevelLoad);
            OwnerState state;
            if (!Owners.TryGetValue(ship, out state)) return false;
            foreach (Order order in state.Orders)
                if (order.ManualOrderId == 0)
                foreach (Observation observation in order.Targets)
                    if (observation.Pending > 0 && observation.Target == target) return true;
            return false;
        }

        internal static bool HasPending(Ship owner) => PendingCount(owner) > 0;
        internal static int PendingCount(Ship owner)
        {
            Sweep(Time.timeSinceLevelLoad);
            OwnerState state;
            if (owner == null || !Owners.TryGetValue(owner, out state)) return 0;
            int count = 0; foreach (Order order in state.Orders) count += order.Pending;
            return count;
        }

        internal static void CancelPending(Ship owner, uint targetId)
        {
            OwnerState state;
            if (ReferenceEquals(owner, null) || !Owners.TryGetValue(owner, out state)) return;
            foreach (Order order in state.Orders)
            {
                if (order.Area && targetId == 0) order.Pending = 0;
                foreach (Observation observation in order.Targets)
                    if (targetId == 0 || observation.Target == null || observation.Target.persistentID.Id == targetId)
                    { order.Pending -= observation.Pending; observation.Weight -= observation.Pending; observation.Pending = 0; order.NextUpdate = 0f; }
            }
        }

        internal static void CancelAutomaticPending(Ship owner)
        {
            if (ReferenceEquals(owner, null) || !Owners.TryGetValue(owner, out OwnerState state)) return;
            foreach (Order order in state.Orders)
                if (order.ManualOrderId == 0)
                    foreach (Observation observation in order.Targets)
                    { order.Pending -= observation.Pending; observation.Weight -= observation.Pending;
                        observation.Pending = 0; order.NextUpdate = 0f; }
        }

        // Destroying the launch ship cancels future launches, but its surviving
        // missiles retain their group and can still replace a lost leader.
        internal static void ResetOwner(Ship owner) => CancelPending(owner, 0);

        internal static void CancelManual(Ship owner, int orderId)
        {
            if (ReferenceEquals(owner, null) || !Owners.TryGetValue(owner, out OwnerState state)) return;
            foreach (Order order in state.Orders)
                if (order.ManualOrderId != 0 && (orderId == 0 || order.ManualOrderId == orderId))
                {
                    if (order.Area) order.Pending = 0;
                    foreach (Observation observation in order.Targets)
                    { order.Pending -= observation.Pending; observation.Weight -= observation.Pending;
                        observation.Pending = 0; order.NextUpdate = 0f; }
                }
        }

        internal static bool IsManualMissile(Missile missile) => missile != null &&
            Membership.TryGetValue(missile, out Order order) && order.ManualOrderId != 0;

        internal static bool ManualTargetAllowed(Missile missile, Unit target)
        {
            if (missile == null || target == null || target.disabled || target.NetworkHQ == null) return false;
            if (Membership.TryGetValue(missile, out Order order) && order.ManualOrderId != 0)
                return order.Area ? target is Ship && target.NetworkHQ != missile.NetworkHQ : order.ManualTarget == target;
            return target.NetworkHQ != missile.NetworkHQ;
        }

        internal static bool HasAreaObjective(Missile missile)
        {
            if (!IsAreaMissile(missile)) return false;
            NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
            return controller == null || !controller.TerminalReleased;
        }

        internal static bool IsAreaMissile(Missile missile) => missile != null && !missile.disabled &&
            Membership.TryGetValue(missile, out Order order) && order.Area;

        internal static bool TryGetAreaPoint(Missile missile, out GlobalPosition point)
        {
            point = default(GlobalPosition);
            if (!HasAreaObjective(missile)) return false;
            Order order = Membership[missile];
            if (missile.NetworkHQ == null || missile.NetworkHQ != order.Hq) return false;
            point = order.SearchPoint;
            return true;
        }

        internal static bool TryGetMapObjective(Missile missile, out GlobalPosition point)
        {
            point = default(GlobalPosition);
            if (missile == null || missile.disabled || !Membership.TryGetValue(missile, out Order order) ||
                missile.NetworkHQ == null || missile.NetworkHQ != order.Hq) return false;
            // Read-only: displaying a line must not resample a track or elect a leader.
            if (order.HasPoint) { point = order.SearchPoint; return Finite(point); }
            if (order.ThreatZone == null || !order.ThreatZone.Valid) return false;
            point = new GlobalPosition((float)order.ThreatZone.X, 0, (float)order.ThreatZone.Z);
            return true;
        }

        internal static void RegisterManualPosition(Ship owner, Missile missile, GlobalPosition point, int orderId)
        {
            if (owner == null || missile == null || missile.disabled || !missile.LocalSim || missile.owner != owner ||
                missile.definition == null || missile.definition.jsonKey != "rsl_ashm" || missile.targetID.IsValid ||
                orderId <= 0 || !Finite(point) || !Owners.TryGetValue(owner, out OwnerState state) ||
                Membership.ContainsKey(missile) || Membership.Count >= PikeCohortPolicy.MaximumLiveMembers) return;
            foreach (Order order in state.Orders)
            {
                if (!order.Area || order.ManualOrderId != orderId || order.Pending <= 0 ||
                    (order.SearchPoint - point).sqrMagnitude > .01f) continue;
                order.Pending--; order.Members.Add(missile); Membership.Add(missile, order);
                missile.onDisableUnit += MissileDisabled;
                CaptureThreatArea(order, missile);
                return;
            }
        }

        internal static void RegisterManual(Ship owner, Missile missile, Unit target, int orderId)
        {
            if (owner == null || missile == null || missile.disabled || !missile.LocalSim || missile.owner != owner ||
                missile.definition == null || missile.definition.jsonKey != "rsl_ashm" || target == null ||
                missile.targetID != target.persistentID || orderId <= 0 || !Owners.TryGetValue(owner, out OwnerState state)) return;
            foreach (Order order in state.Orders)
            {
                if (order.ManualOrderId != orderId || order.ManualTarget != target || order.Pending <= 0) continue;
                if (Membership.TryGetValue(missile, out Order previous))
                {
                    if (previous == order || previous.ManualOrderId != 0) return;
                    // Native event subscribers may run before the explicit
                    // executor's callback. Return an accidental automatic
                    // reservation before taking this manual membership.
                    foreach (Observation old in previous.Targets)
                        if (old.Target == target) { old.Pending++; previous.Pending++; break; }
                    MissileDisabled(missile);
                }
                if (Membership.Count >= PikeCohortPolicy.MaximumLiveMembers) return;
                Observation observation = order.Targets[0]; observation.Pending--; order.Pending--;
                order.Members.Add(missile); Membership.Add(missile, order);
                missile.onDisableUnit += MissileDisabled;
                CaptureThreatArea(order, missile);
                return;
            }
        }

        internal static bool TryGetOrder(Missile missile, out uint groupId, out GlobalPosition searchPoint, out Vector3 course)
        {
            groupId = 0; searchPoint = default(GlobalPosition); course = Vector3.zero;
            Sweep(Time.timeSinceLevelLoad);
            Order order;
            if (missile == null || missile.disabled || !Membership.TryGetValue(missile, out order)) return false;
            UpdatePoint(order, Time.timeSinceLevelLoad);
            groupId = order.Id; searchPoint = order.SearchPoint; course = order.Course;
            return true;
        }

        internal static bool TryGetGuidancePoint(Missile missile, out GlobalPosition point, out Vector3 course)
        {
            point = default(GlobalPosition); course = Vector3.zero;
            uint groupId;
            if (!TryGetOrder(missile, out groupId, out point, out course)) return false;
            Order order = Membership[missile];
            if (!order.HasPoint || missile.NetworkHQ == null || missile.NetworkHQ != order.Hq) return false;
            NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
            NaturalPikeGroupTargeting targeting = missile.GetComponent<NaturalPikeGroupTargeting>();
            if (controller != null && controller.TerminalReleased || targeting != null &&
                (targeting.CanRelease || targeting.YieldToNativeGuidance)) return false;
            if (order.Area) return true;
            if (missile.targetID.NotValid) return false;
            ARHSeeker seeker = missile.GetComponent<ARHSeeker>();
            float now = Time.timeSinceLevelLoad;
            foreach (Observation observation in order.Targets)
                if (Usable(observation, now) && order.Hq.IsTargetPositionAccurate(observation.Target, 2000f) &&
                    PikeGroupSeeker.CanReceive(seeker, missile, observation.Target, point)) return true;
            return false;
        }

        private static void Bind(OwnerState state, Missile missile)
        {
            float now = Time.timeSinceLevelLoad;
            Sweep(now);
            // targetID is set by Spawner before network spawn/StartMissile;
            // seeker initialization happens AFTER this registration callback.
            if (missile == null || missile.disabled || !missile.LocalSim || missile.owner != state.Owner ||
                missile.definition == null || missile.definition.jsonKey != "rsl_ashm" || missile.targetID.NotValid ||
                Membership.ContainsKey(missile) || Membership.Count >= PikeCohortPolicy.MaximumLiveMembers) return;
            foreach (Order order in state.Orders)
                if (order.ManualOrderId == 0)
                foreach (Observation observation in order.Targets)
                    if (observation.Pending > 0 && observation.Target != null && observation.Target.persistentID == missile.targetID)
                    {
                        observation.Pending--; order.Pending--;
                        order.Members.Add(missile); Membership.Add(missile, order);
                        missile.onDisableUnit += MissileDisabled;
                        CaptureThreatArea(order, missile);
                        return;
                    }
        }

        private static void CaptureThreatArea(Order order, Missile missile)
        {
            if (order.ThreatZone == null)
            {
                // Freeze once for the actual launched group, not for a queued
                // order that may never fire. Later datalink updates still steer
                // the group but cannot silently move its declared threat area.
                UpdatePoint(order, Time.timeSinceLevelLoad);
                ARHSeeker seeker = missile.GetComponent<ARHSeeker>();
                if (seeker == null) return;
                GlobalPosition center = order.SearchPoint;
                if (!order.HasPoint)
                {
                    // Manual stale-track orders pass the native target ID, not
                    // a coordinate. Native ARH.Initialize uses this same stored
                    // HQ position when the track is stale. Copy memory only for
                    // the frozen threat zone: do not refresh the track or turn
                    // the order's stale guidance point into a usable one.
                    if (order.Area || order.ManualOrderId == 0 || order.ManualTarget == null || order.Hq == null) return;
                    TrackingInfo track = order.Hq.GetTrackingData(order.ManualTarget.persistentID);
                    float age = track != null ? Time.timeSinceLevelLoad - track.lastSpottedTime : float.NaN;
                    if (track == null || !Finite(age) || age < 4f) return;
                    center = track.lastKnownPosition;
                }
                if (!Finite(center)) return;
                if (!NaturalWeapons.Infos.TryGetValue("rsl_ashm", out WeaponInfo info)) return;
                var zone = new PikeThreatZone(order.Origin.x, order.Origin.z, center.x, center.z,
                    seeker.GetRadarParams().maxRange, info.targetRequirements.maxRange);
                if (!zone.Valid) return;
                order.ThreatZone = zone;
            }
            NaturalPikeObservedThreat.Register(missile, order.ThreatZone, order.Hq);
        }

        private static void MissileDisabled(Unit unit)
        {
            Missile missile = unit as Missile;
            Order order;
            if (ReferenceEquals(missile, null) || !Membership.TryGetValue(missile, out order)) return;
            Membership.Remove(missile); order.Members.Remove(missile);
            missile.onDisableUnit -= MissileDisabled;
        }

        private static bool Sample(Observation observation, FactionHQ hq, float now)
        {
            if (hq == null || observation.Target == null || observation.Target.disabled ||
                observation.Target.NetworkHQ == null || observation.Target.NetworkHQ == hq && !observation.AllowFriendly) return false;
            if (observation.AllowFriendly && observation.Target.NetworkHQ == hq)
            {
                // Native HQ explicitly knows its friendly units. Use that
                // same accessor; do not add a hostile tracking-database row.
                if (!hq.TryGetKnownPosition(observation.Target, out GlobalPosition friendly) || !Finite(friendly)) return false;
                Vector3 friendlyVelocity = observation.Target.rb != null ? observation.Target.rb.velocity : Vector3.zero;
                if (!Finite(friendlyVelocity)) return false;
                observation.Position = friendly; observation.Velocity = Vector3.ClampMagnitude(friendlyVelocity, 100f);
                observation.SeenAt = observation.SampledAt = now;
                return true;
            }
            TrackingInfo track = hq.GetTrackingData(observation.Target.persistentID);
            if (track == null || !Finite(track.lastSpottedTime) || now < track.lastSpottedTime || now - track.lastSpottedTime >= 4f) return false;
            // GetPosition is the native accessor: its live lookup is allowed
            // only during the real four-second observed window checked above.
            GlobalPosition position = track.GetPosition();
            if (!Finite(position)) return false;
            Vector3 velocity = observation.Target.rb != null ? observation.Target.rb.velocity : Vector3.zero;
            if (!Finite(velocity)) return false;
            observation.Position = position;
            observation.Velocity = Vector3.ClampMagnitude(velocity, 100f);
            observation.SeenAt = track.lastSpottedTime; observation.SampledAt = now;
            return true;
        }

        private static bool Usable(Observation observation, float now) => observation.Weight > 0 && observation.Target != null && !observation.Target.disabled &&
            Finite(now) && now >= observation.SeenAt && now - observation.SeenAt <= ObservationMemory;

        private static void UpdatePoint(Order order, float now)
        {
            if (order.Area) return; // Explicit coordinates are navigation intent, not a contact observation.
            if (now < order.NextUpdate) return;
            order.NextUpdate = now + 1f;
            GlobalPosition origin = order.Origin;
            foreach (Missile member in order.Members)
                if (member != null && !member.disabled) { origin = member.GlobalPosition(); break; }
            Vector3 sum = Vector3.zero; int weight = 0;
            foreach (Observation observation in order.Targets)
            {
                Sample(observation, order.Hq, now);
                if (!Usable(observation, now) || observation.Target.NetworkHQ == null ||
                    observation.Target.NetworkHQ == order.Hq && !observation.AllowFriendly) continue;
                // Observation age and future intercept prediction are different
                // limits. Never keep an unobserved track alive beyond 20 s, but
                // a fresh long-range report can forecast the scan-arrival point.
                // This lead naturally falls to zero inside the local scan range.
                float age = Mathf.Clamp(now - observation.SampledAt, 0f, ObservationMemory);
                GlobalPosition currentEstimate = observation.Position + observation.Velocity * age;
                float scanDistance = Mathf.Max(0f, (currentEstimate - origin).magnitude - PikeFormationFlightPolicy.TerminalRange);
                float flight = Mathf.Clamp(scanDistance / PikeFormationFlightPolicy.CruiseSpeed, 0f, MaximumPredictionSeconds);
                GlobalPosition predicted = currentEstimate + observation.Velocity * flight;
                sum += predicted.AsVector3() * observation.Weight; weight += observation.Weight;
            }
            order.HasPoint = weight > 0;
            if (weight == 0) return; // Retained point is historical, not usable.
            order.SearchPoint = new GlobalPosition(sum / weight);
            Vector3 course = order.SearchPoint - order.Origin; course.y = 0f;
            if (course.sqrMagnitude > 1f) order.Course = course.normalized;
        }

        private static float QueueLifetime(Ship owner, int shots)
        {
            float interval = (float)PikeCohortPolicy.DefaultSalvoIntervalSeconds, planning = .1f;
            NaturalFireControlGroups groups = owner.GetComponent<NaturalFireControlGroups>();
            if (groups != null && groups.Groups != null)
                foreach (NaturalFireControlGroups.Group group in groups.Groups)
                    if (group != null && group.Controller != null && NaturalMissileTargeting.Key(group.Info) == "rsl_ashm")
                    {
                        float actualInterval = (float)NaturalWeapons.Get(group.Controller, "salvoInterval");
                        float actualPlanning = (float)NaturalWeapons.Get(group.Controller, "planningTimePerFire");
                        if (Finite(actualInterval) && actualInterval > 0f) interval = Mathf.Max(interval, actualInterval);
                        if (Finite(actualPlanning) && actualPlanning > 0f) planning = Mathf.Max(planning, actualPlanning);
                    }
            return Mathf.Clamp(shots * (interval + planning) + 10f, 15f, 600f);
        }

        private static void Sweep(float now)
        {
            if (!Finite(now) || now >= lastSweep && now < nextSweep) return;
            lastSweep = now; nextSweep = now + .5f;
            RemoveOwners.Clear();
            foreach (var pair in Owners)
            {
                OwnerState state = pair.Value;
                for (int i = state.Orders.Count - 1; i >= 0; i--)
                {
                    Order order = state.Orders[i];
                    bool retire = now < order.Created || !order.Area && now - order.Created > MaximumLifetime;
                    for (int j = order.Members.Count - 1; j >= 0; j--)
                    {
                        Missile missile = order.Members[j];
                        if (!retire && missile != null && !missile.disabled) continue;
                        if (!ReferenceEquals(missile, null)) { Membership.Remove(missile); if (missile != null) missile.onDisableUnit -= MissileDisabled; }
                        order.Members.RemoveAt(j);
                    }
                    foreach (Observation observation in order.Targets)
                        if (retire || state.Owner == null || state.Owner.disabled || now >= order.Expires || observation.Target == null || observation.Target.disabled)
                        { order.Pending -= observation.Pending; observation.Weight -= observation.Pending; observation.Pending = 0; order.NextUpdate = 0f; }
                    if (order.Area && (retire || state.Owner == null || state.Owner.disabled || now >= order.Expires)) order.Pending = 0;
                    if (order.Pending == 0 && order.Members.Count == 0) state.Orders.RemoveAt(i);
                }
                if (state.Orders.Count == 0) RemoveOwners.Add(pair.Key);
            }
            foreach (Ship owner in RemoveOwners)
            {
                OwnerState state = Owners[owner];
                if (owner != null) { owner.onRegisterMissile -= state.Registered; owner.onDisableUnit -= state.Disabled; }
                Owners.Remove(owner);
            }
        }

        private static int Count(int[] shots)
        {
            if (shots == null || shots.Length > MaximumStrikeShots) return -1;
            int total = 0;
            foreach (int count in shots) { if (count < 0 || count > MaximumStrikeShots) return -1; total += count; }
            return total;
        }
        private static int Outstanding(OwnerState state)
        { int count = 0; if (state != null) foreach (Order order in state.Orders) count += order.Pending + order.Members.Count; return count; }
        private static int UsefulOutstanding(OwnerState state, float now)
        {
            int count = 0;
            if (state != null)
                foreach (Order order in state.Orders)
                    count += order.Pending + (now <= order.UsefulUntil ? order.Members.Count : 0);
            // Overdue members retain identity, observations and leader transfer.
            // They occupy hard storage capacity but not useful strike capacity.
            return count;
        }
        private static int OrderCount() { int count = 0; foreach (OwnerState state in Owners.Values) count += state.Orders.Count; return count; }
        private static int GlobalOutstanding() { int count = 0; foreach (OwnerState state in Owners.Values) count += Outstanding(state); return count; }
        private static uint NewId() { do { nextId++; } while (nextId == 0); return nextId; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(GlobalPosition value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
