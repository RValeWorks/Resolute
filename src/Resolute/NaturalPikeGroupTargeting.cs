using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Tactical membership outlives the cruise rendezvous. Only real sensor
    // returns enter the contact table; native HQ and ARH own subsequent tracks.
    internal sealed class NaturalPikeGroupTargeting : MonoBehaviour
    {
        private sealed class Contact
        {
            internal Unit Unit;
            internal GlobalPosition Position;
            internal Vector3 Velocity;
            internal float SeenAt, Return;
            internal double Tonnage, Bearing;
            internal bool NativeHqMemory;
        }
        private sealed class Group
        {
            internal uint Id, Owner, Leader, FormationLeader;
            internal Vector3 Forward, Right;
            internal readonly List<NaturalPikeGroupTargeting> Members = new List<NaturalPikeGroupTargeting>(8);
            internal readonly Dictionary<uint, Contact> Contacts = new Dictionary<uint, Contact>();
            internal readonly List<Unit> Nearby = new List<Unit>(128);
            internal readonly List<object> History = new List<object>(16);
            internal readonly List<uint> RemoveContacts = new List<uint>(64);
            internal float NextTick, FirstContactAt = -1f, SearchStartedAt = -1f, ScanStartedAt = -1f;
            internal float SearchEntryRange = -1f;
            internal bool Searching, Released, Dirty, AreaRadarActivated;
            internal int Revision, LeaderReplacements, Scans, Returns, RejectedPlans, Retargets;
            internal string LastReason = "assembling";
            internal int ScanCursor;
        }
        private static readonly Dictionary<uint, Group> Groups = new Dictionary<uint, Group>();
        private Group group;
        private Missile missile;
        private ARHSeeker seeker;
        private NaturalCruiseGuidance guidance;
        private uint assignedTarget;
        private bool subscribed, finalCommitted;
        private float nextAssignmentAt;
        private float searchSeconds = 3f;
        private readonly HashSet<uint> exhaustedTargets = new HashSet<uint>();
        private bool exhaustedHistoryFull;
        private int nativeClearEvents;
        private uint lastNativeClearedTarget;
        private float lastNativeClearAt = -1f;
        private float noObjectiveSince = -1f;
        private bool nativeObjectiveHandoff;

        internal static void Bind(Missile missile, uint cohortId, float headingX, float headingZ)
        {
            if (missile == null || !missile.LocalSim || missile.disabled || missile.owner == null || cohortId == 0) return;
            NaturalPikeGroupTargeting member = missile.GetComponent<NaturalPikeGroupTargeting>();
            if (member == null)
            {
                member = missile.gameObject.AddComponent<NaturalPikeGroupTargeting>();
                member.missile = missile; member.seeker = missile.GetComponent<ARHSeeker>();
                member.guidance = missile.GetComponent<NaturalCruiseGuidance>();
                missile.onDisableUnit += member.Disabled; member.subscribed = true;
            }
            if (member.group != null && member.group.Id == cohortId)
            {
                // The shared midcourse frame may update from a real ship
                // observation. Freeze it when local terminal allocation starts.
                if (!member.group.Searching && !member.group.Released)
                {
                    member.group.Forward = new Vector3(headingX, 0f, headingZ).normalized;
                    member.group.Right = Vector3.Cross(Vector3.up, member.group.Forward).normalized;
                }
                return;
            }
            member.Leave();
            Group value;
            if (!Groups.TryGetValue(cohortId, out value))
            {
                if (Groups.Count >= PikeCohortPolicy.MaximumLiveMembers) return;
                Vector3 forward = new Vector3(headingX, 0f, headingZ).normalized;
                value = new Group { Id = cohortId, Owner = missile.owner.persistentID.Id, Forward = forward,
                    Right = Vector3.Cross(Vector3.up, forward).normalized };
                Groups.Add(cohortId, value);
            }
            if (value.Owner != missile.owner.persistentID.Id || value.Members.Count >= PikeGroupTargetingPolicy.MaximumMembers) return;
            member.group = value; value.Members.Add(member); value.Dirty = true;
        }

        internal bool CanRelease => !YieldToNativeGuidance && group != null && group.Released && assignedTarget != 0 && missile != null &&
            missile.targetID.Id == assignedTarget;
        internal bool YieldToNativeGuidance => nativeObjectiveHandoff && !HasLiveNativeObjective();
        internal bool LeaderSearching => group != null && group.Searching && missile != null &&
            (!group.Released || ResoluteStrikeOrders.HasAreaObjective(missile) && !CanRelease) &&
            group.Leader == missile.persistentID.Id;
        internal bool LeaderRadarActive => LeaderSearching && RadarReady(this, SearchRange(this));
        internal bool TryGetSearchCourse(out Vector3 course)
        {
            course = group != null ? group.Forward : Vector3.zero;
            return LeaderSearching && course.sqrMagnitude > .5f;
        }
        internal uint LeaderId => group != null ? group.Leader : 0u;
        internal uint FormationLeaderId => group != null ? group.FormationLeader : 0u;
        internal bool FollowingValidFormation
        {
            get
            {
                if (!AwaitingAssignment || guidance == null || !guidance.NativeObservationUsable || !HasLiveNativeObjective()) return false;
                NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
                return controller != null && controller.CruiseActive && !controller.TerminalReleased;
            }
        }
        internal bool AwaitingAssignment
        {
            get
            {
                if (group == null || missile == null || missile.disabled || YieldToNativeGuidance || CanRelease) return false;
                NaturalCruiseController controller = missile.GetComponent<NaturalCruiseController>();
                return controller == null || !controller.TerminalReleased;
            }
        }

        internal bool TryGetFormationLeader(out Missile leader)
        {
            leader = null;
            if (group == null) return false;
            NaturalPikeGroupTargeting selected = Find(group, group.Leader);
            if (!FormationEligible(selected)) selected = Find(group, group.FormationLeader);
            if (!FormationEligible(selected))
            {
                var candidates = new List<PikeGroupTargetingPolicy.Member>(8);
                foreach (NaturalPikeGroupTargeting member in group.Members)
                    if (FormationEligible(member)) candidates.Add(new PikeGroupTargetingPolicy.Member(
                        member.missile.persistentID.Id,
                        Vector3.Dot(member.missile.GlobalPosition().AsVector3(), group.Right)));
                selected = Find(group, PikeGroupTargetingPolicy.CentralLeader(candidates.ToArray(), 0));
            }
            group.FormationLeader = selected != null ? selected.missile.persistentID.Id : 0u;
            if (selected == null) return false;
            leader = selected.missile;
            return true;
        }

        private static bool FormationEligible(NaturalPikeGroupTargeting member)
        {
            if (!Live(member) || member.missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(member.missile) || member.YieldToNativeGuidance) return false;
            NaturalCruiseController controller = member.missile.GetComponent<NaturalCruiseController>();
            return controller == null || !controller.TerminalReleased;
        }

        internal void NativeTargetCleared(uint targetId)
        {
            if (group == null || missile == null || missile.disabled || !missile.LocalSim || targetId == 0) return;
            nativeClearEvents++; lastNativeClearedTarget = targetId;
            lastNativeClearAt = Time.timeSinceLevelLoad;
            if (!exhaustedTargets.Contains(targetId))
            {
                // Never evict an exhausted contact: cycling through other
                // ships must not restart this receiver's expired ARH memory.
                if (exhaustedTargets.Count < PikeGroupTargetingPolicy.MaximumTargets) exhaustedTargets.Add(targetId);
                else exhaustedHistoryFull = true;
            }
            group.Dirty = true;
        }

        private bool CanAssignContact(uint targetId)
        {
            // A target already selected by native logic needs no seeker reset.
            // Group reassignment must not resurrect a contact native ARH lost.
            return missile.targetID.Id == targetId ||
                !exhaustedHistoryFull && !exhaustedTargets.Contains(targetId);
        }

        internal void BeforeGuidance()
        {
            Group value = group;
            if (value == null || missile == null || missile.disabled || !missile.LocalSim) return;
            if (CanRelease && guidance.NativeObservationUsable && guidance.ObservedRange <= PikeFlightProfile.FinalCorrectionRange)
                finalCommitted = true;
            float now = Time.timeSinceLevelLoad;
            if (now < value.NextTick) return;
            value.NextTick = now + PikeGroupTargetingPolicy.SearchIntervalSeconds;
            Tick(value, now);
        }

        private static bool Live(NaturalPikeGroupTargeting member) => member != null && member.missile != null &&
            !member.missile.disabled && member.missile.LocalSim && member.seeker != null && member.guidance != null;

        private static NaturalPikeGroupTargeting Find(Group value, uint id)
        {
            foreach (NaturalPikeGroupTargeting member in value.Members)
                if (Live(member) && member.missile.persistentID.Id == id) return member;
            return null;
        }

        private static PikeGroupTargetingPolicy.Member[] Lanes(Group value)
        {
            var result = new List<PikeGroupTargetingPolicy.Member>(8);
            foreach (NaturalPikeGroupTargeting member in value.Members)
                if (Live(member)) result.Add(new PikeGroupTargetingPolicy.Member(member.missile.persistentID.Id,
                    Vector3.Dot(member.missile.GlobalPosition().AsVector3(), value.Right)));
            return result.ToArray();
        }

        private static void Tick(Group value, float now)
        {
            NaturalPikeGroupTargeting leader = Find(value, value.Leader);
            // An area salvo can receive partial assignments, or launch fewer
            // rounds than ordered. Its still-unassigned survivors keep a real
            // scanning leader when the previous one departs for an attack.
            if (leader != null && !FormationEligible(leader))
            {
                var waiting = new List<PikeGroupTargetingPolicy.Member>(8);
                foreach (NaturalPikeGroupTargeting member in value.Members)
                    if (FormationEligible(member) && ResoluteStrikeOrders.HasAreaObjective(member.missile) && !member.CanRelease)
                        waiting.Add(new PikeGroupTargetingPolicy.Member(member.missile.persistentID.Id,
                            Vector3.Dot(member.missile.GlobalPosition().AsVector3(), value.Right)));
                if (waiting.Count > 0)
                {
                    value.Leader = PikeGroupTargetingPolicy.CentralLeader(waiting.ToArray(), 0);
                    leader = Find(value, value.Leader); value.LeaderReplacements++; value.Dirty = true;
                    if (value.Released) value.FirstContactAt = -1f;
                }
            }
            if (leader == null)
            {
                uint old = value.Leader;
                value.Leader = PikeGroupTargetingPolicy.CentralLeader(Lanes(value), 0);
                leader = Find(value, value.Leader);
                if (leader == null) return;
                if (old != 0) value.LeaderReplacements++;
                value.Dirty = true;
                value.LastReason = old == 0 ? "central-leader-elected" : "central-survivor-replaced-lost-leader";
                // A replacement inherits the scan/collection clocks and course.
                // Contact memory expires normally; it is never refreshed here.
                Remember(value, now, value.LastReason, null);
            }
            float searchRange = SearchRange(leader);
            if (!value.Searching)
            {
                if (!leader.guidance.LaunchReady || !leader.guidance.NativeObservationUsable && !ResoluteStrikeOrders.HasAreaObjective(leader.missile) ||
                    !PikeGroupSeeker.CanActivate(leader.seeker, searchRange)) return;
                // The 25nm formation/terminal envelope is not the radar's
                // activation point. Starting the search climb there produced
                // roughly 30 seconds of apparent searching with radar off.
                // Enter search only at the same 10nm gate as native ARH.
                // Re-elect using the assembled group's actual middle lanes at
                // scan entry, then latch until this leader is actually lost.
                NaturalPikeGroupTargeting selected = Find(value, PikeGroupTargetingPolicy.CentralLeader(Lanes(value), value.Leader));
                if (selected == null || !selected.guidance.LaunchReady ||
                    !selected.guidance.NativeObservationUsable && !ResoluteStrikeOrders.HasAreaObjective(selected.missile)) return;
                searchRange = SearchRange(selected);
                if (!PikeGroupSeeker.CanActivate(selected.seeker, searchRange)) return;
                leader = selected; value.Leader = leader.missile.persistentID.Id;
                value.Searching = true; value.SearchStartedAt = now;
                value.SearchEntryRange = searchRange;
                value.LastReason = "terminal-leader-climb";
                Remember(value, now, value.LastReason, null);
            }
            // Search altitude is a flight command, never a sensor or timer gate.
            // Start scanning during the climb once the seeker activation range
            // is reached; native horizon, signal, FOV and LOS still decide returns.
            if (PikeGroupSeeker.CanActivate(leader.seeker, searchRange) && ResoluteStrikeOrders.HasAreaObjective(leader.missile))
                value.AreaRadarActivated = true;
            bool radarReady = RadarReady(leader, searchRange);
            if (radarReady)
            {
                if (value.ScanStartedAt < 0f) value.ScanStartedAt = now;
                Scan(value, leader, now);
            }
            PruneContacts(value, leader, now);
            UpdateObjectiveHandoffs(value, leader, now);
            if (value.Contacts.Count == 0)
            {
                value.FirstContactAt = -1f;
                value.LastReason = "waiting-for-native-surface-return"; return;
            }
            if (!value.Released)
            {
                // Terminal search AND a valid detected contact start the
                // collection window. Empty scanning cannot spend that time.
                if (!radarReady) return;
                if (value.FirstContactAt < 0f) value.FirstContactAt = now;
                if (now - value.FirstContactAt < leader.searchSeconds) return;
            }
            foreach (NaturalPikeGroupTargeting member in value.Members)
                if (Live(member) && (member.assignedTarget == 0 || member.missile.targetID.Id != member.assignedTarget ||
                    value.Released && !KeepCurrentAssignment(value, member)))
                    value.Dirty = true;
            if (value.Dirty || !value.Released) Allocate(value, leader, now);
        }

        private static float SearchRange(NaturalPikeGroupTargeting member)
        {
            if (ResoluteStrikeOrders.TryGetGuidancePoint(member.missile, out GlobalPosition point, out Vector3 course))
            {
                Vector3 approach = point - member.missile.GlobalPosition(); approach.y = 0f;
                return approach.magnitude;
            }
            return member.guidance.ObservedRange;
        }

        private static bool RadarReady(NaturalPikeGroupTargeting member, float range)
        {
            // A commanded area search keeps looking ahead after passing its
            // coordinate. Its radar keeps the same physical detection range;
            // passing that coordinate must not turn an unassigned salvo blind.
            return PikeGroupSeeker.CanActivate(member.seeker, range) ||
                member.group.AreaRadarActivated && ResoluteStrikeOrders.HasAreaObjective(member.missile);
        }

        private bool HasLiveNativeObjective()
        {
            Unit native = seeker != null ? NaturalCruiseDatalink.Target(seeker) : null;
            if (native != null && !native.disabled) return true;
            Unit assigned;
            return missile != null && missile.targetID.TryGetUnit(out assigned) && assigned != null && !assigned.disabled;
        }

        private static void UpdateObjectiveHandoffs(Group value, NaturalPikeGroupTargeting leader, float now)
        {
            // A low leader that has never scanned has not established an empty
            // search. This handoff concerns only the newly held cruise members;
            // the established terminal flight/retirement remains native-owned.
            if (!value.Searching || value.Scans == 0) return;
            foreach (NaturalPikeGroupTargeting member in value.Members)
            {
                if (!Live(member)) continue;
                NaturalCruiseController controller = member.missile.GetComponent<NaturalCruiseController>();
                if (controller == null || controller.TerminalReleased || member.HasLiveNativeObjective() || ResoluteStrikeOrders.HasAreaObjective(member.missile))
                { member.noObjectiveSince = -1f; continue; }
                bool replacement = false;
                foreach (var pair in value.Contacts)
                    if (member.missile.NetworkHQ == leader.missile.NetworkHQ && member.CanAssignContact(pair.Key) &&
                        PikeGroupSeeker.CanReceive(member.seeker, member.missile, pair.Value.Unit, pair.Value.Position))
                    { replacement = true; break; }
                if (replacement) { member.noObjectiveSince = -1f; continue; }
                if (member.noObjectiveSince < 0f) member.noObjectiveSince = now;
                float patience = Math.Max(3f, NaturalCruiseDatalink.LockPerseverance(member.seeker));
                if (now - member.noObjectiveSince >= patience) member.nativeObjectiveHandoff = true;
            }
        }

        private static void Scan(Group value, NaturalPikeGroupTargeting leader, float now)
        {
            value.Scans++;
            RadarParams radar = leader.seeker.GetRadarParams();
            BattlefieldGrid.GetUnitsInRangeNonAlloc(leader.missile.GlobalPosition(), radar.maxRange, value.Nearby);
            // Cheap native spatial query first; bound expensive ship LOS/radar
            // checks per group per second. Rotate in unusually crowded battles.
            int count = value.Nearby.Count, checks = Math.Min(count, 256);
            for (int j = 0; j < checks; j++)
            {
                Unit target = value.Nearby[(value.ScanCursor + j) % count];
                if (!(target is Ship) || target.disabled || target.NetworkHQ == null ||
                    !ResoluteManualPikeOrders.AllowsTarget(leader.missile, target) || target.definition == null || target.persistentID.NotValid) continue;
                Contact old;
                value.Contacts.TryGetValue(target.persistentID.Id, out old);
                float previous = old != null ? old.Return : NaturalCruiseDatalink.Target(leader.seeker) == target
                    ? NaturalCruiseDatalink.Strength(leader.seeker) : 0f;
                float strength = PikeGroupSeeker.SurfaceReturn(leader.seeker, leader.missile, target, previous, searchOnly: true);
                if (!(strength > radar.minSignal)) continue;
                double mass = target.definition.mass;
                if (double.IsNaN(mass) || double.IsInfinity(mass) || mass <= 0) continue;
                Vector3 velocity = target.rb != null ? target.rb.velocity : Vector3.zero;
                if (!PikeFlightProfile.Finite(velocity.x) || !PikeFlightProfile.Finite(velocity.y) ||
                    !PikeFlightProfile.Finite(velocity.z)) continue;
                if (old == null && value.Contacts.Count >= PikeGroupTargetingPolicy.MaximumTargets)
                {
                    uint smallest = 0; double smallestMass = double.MaxValue;
                    foreach (var pair in value.Contacts)
                        if (pair.Value.Tonnage < smallestMass) { smallest = pair.Key; smallestMass = pair.Value.Tonnage; }
                    if (mass / 1000d <= smallestMass) continue;
                    value.Contacts.Remove(smallest);
                }
                if (old == null) { old = new Contact(); value.Contacts[target.persistentID.Id] = old; value.Dirty = true; }
                old.Unit = target; old.Position = target.GlobalPosition();
                old.Velocity = velocity;
                old.SeenAt = now; old.Return = strength; old.Tonnage = mass / 1000d; old.NativeHqMemory = false;
                Vector3 direction = old.Position - leader.missile.GlobalPosition();
                old.Bearing = Math.Atan2(Vector3.Dot(direction, value.Right), Vector3.Dot(direction, value.Forward)) * 180d / Math.PI;
                value.Returns++;
                // This is the game's normal detection publication, only after
                // a positive return. It does not grant any receiver radar lock.
                if (leader.missile.IsServer && leader.missile.NetworkHQ != null)
                    leader.missile.NetworkHQ.RpcUpdateTrackingInfo(target.persistentID);
            }
            if (count > 0) value.ScanCursor = (value.ScanCursor + checks) % count;
        }

        private static void PruneContacts(Group value, NaturalPikeGroupTargeting leader, float now)
        {
            value.RemoveContacts.Clear();
            float memory = NaturalCruiseDatalink.LockPerseverance(leader.seeker);
            foreach (var pair in value.Contacts)
            {
                Contact contact = pair.Value;
                if (contact.Unit == null || contact.Unit.disabled || contact.Unit.NetworkHQ == null ||
                    !ResoluteManualPikeOrders.AllowsTarget(leader.missile, contact.Unit))
                { value.RemoveContacts.Add(pair.Key); continue; }
                if (now - contact.SeenAt <= memory) continue;
                // A leader descending after allocation may lose its own radar
                // horizon before the receivers acquire theirs. Native pre-lock
                // datalink still accepts an accurate HQ fix in that situation.
                // Retain that EXISTING contact under the same 2000m accuracy
                // rule; do not fake a fresh radar return or renew its SeenAt.
                FactionHQ hq = leader.missile.NetworkHQ;
                GlobalPosition known;
                if (hq == null || !hq.IsTargetPositionAccurate(contact.Unit, 2000f) ||
                    !hq.TryGetKnownPosition(contact.Unit, out known) ||
                    !PikeFlightProfile.Finite(known.x) || !PikeFlightProfile.Finite(known.y) || !PikeFlightProfile.Finite(known.z))
                { value.RemoveContacts.Add(pair.Key); continue; }
                contact.Position = known; contact.NativeHqMemory = true;
                Vector3 direction = known - leader.missile.GlobalPosition();
                contact.Bearing = Math.Atan2(Vector3.Dot(direction, value.Right), Vector3.Dot(direction, value.Forward)) * 180d / Math.PI;
                if (hq.IsTargetBeingTracked(contact.Unit) && contact.Unit.rb != null)
                {
                    Vector3 velocity = contact.Unit.rb.velocity;
                    if (PikeFlightProfile.Finite(velocity.x) && PikeFlightProfile.Finite(velocity.y) && PikeFlightProfile.Finite(velocity.z))
                        contact.Velocity = velocity;
                }
            }
            foreach (uint id in value.RemoveContacts) { value.Contacts.Remove(id); value.Dirty = true; }
        }

        private static bool KeepCurrentAssignment(Group value, NaturalPikeGroupTargeting member)
        {
            Contact contact;
            return value.Released && member.CanRelease &&
                value.Contacts.TryGetValue(member.assignedTarget, out contact) &&
                PikeGroupSeeker.CanReceive(member.seeker, member.missile, contact.Unit, contact.Position);
        }

        private static void Allocate(Group value, NaturalPikeGroupTargeting leader, float now)
        {
            var lanes = Lanes(value);
            var targets = new List<PikeGroupTargetingPolicy.Target>(value.Contacts.Count);
            foreach (var pair in value.Contacts)
            {
                Contact contact = pair.Value;
                targets.Add(new PikeGroupTargetingPolicy.Target(pair.Key, contact.Bearing, contact.Tonnage));
            }
            bool[,] allowed = new bool[lanes.Length, targets.Count];
            for (int i = 0; i < lanes.Length; i++)
            {
                NaturalPikeGroupTargeting member = Find(value, lanes[i].Id);
                if (member.CanRelease && member.guidance.NativeObservationUsable &&
                    member.guidance.ObservedRange <= PikeFlightProfile.FinalCorrectionRange) member.finalCommitted = true;
                // Tonnage chooses the initial salvo, not a new destination for
                // every already-attacking round whenever a contact changes.
                // Keep viable native assignments; only orphaned/unassigned
                // receivers may take another observed target. Expired contacts
                // and native clears still permit genuine dynamic replacement.
                bool keepAssignment = KeepCurrentAssignment(value, member);
                for (int j = 0; j < targets.Count; j++)
                {
                    Contact contact = value.Contacts[targets[j].Id];
                    allowed[i, j] = member.missile.NetworkHQ == leader.missile.NetworkHQ &&
                        ResoluteManualPikeOrders.AllowsTarget(member.missile, contact.Unit) &&
                        (!keepAssignment || member.assignedTarget == targets[j].Id) &&
                        member.CanAssignContact(targets[j].Id) &&
                        PikeGroupSeeker.CanReceive(member.seeker, member.missile, contact.Unit, contact.Position);
                }
            }
            // Eligibility is per receiver. One off-gimbal member must not veto
            // all targets for the rest of its group. Hold its native assignment
            // when no ordered, receivable replacement is possible.
            PikeGroupTargetingPolicy.Assignment[] plan = PikeGroupTargetingPolicy.AllocateFeasible(lanes, targets.ToArray(), allowed);
            if (plan.Length == 0) { value.LastReason = "no-shared-forward-track"; return; }
            // Validate the complete plan before any target mutation. A waiting
            // member keeps its native observation; no partial plan is published.
            foreach (var assignment in plan)
            {
                NaturalPikeGroupTargeting member = Find(value, assignment.MissileId);
                Contact contact = value.Contacts[assignment.TargetId];
                if (member == null || now < member.nextAssignmentAt ||
                    !member.CanAssignContact(assignment.TargetId) ||
                    !PikeGroupSeeker.CanReceive(member.seeker, member.missile, contact.Unit, contact.Position))
                { value.RejectedPlans++; value.LastReason = "plan-not-ready"; return; }
            }
            int changed = 0, assignmentChanges = 0;
            foreach (var assignment in plan)
            {
                NaturalPikeGroupTargeting member = Find(value, assignment.MissileId);
                Contact contact = value.Contacts[assignment.TargetId];
                if (member.missile.targetID.Id != assignment.TargetId)
                {
                    if (!PikeGroupSeeker.Assign(member.seeker, member.missile, contact.Unit, contact.Position, contact.Velocity))
                    { value.RejectedPlans++; value.LastReason = "native-retarget-rejected"; return; }
                    member.nextAssignmentAt = now + 1f; changed++;
                }
                if (member.assignedTarget != assignment.TargetId)
                {
                    assignmentChanges++;
                    member.finalCommitted = false;
                }
                member.assignedTarget = assignment.TargetId;
                member.nativeObjectiveHandoff = false; member.noObjectiveSince = -1f;
            }
            value.Retargets += changed; value.Dirty = false;
            if (value.Released && changed == 0 && assignmentChanges == 0) return;
            ArmTerminalAcceleration(value, plan, now);
            value.LastReason = value.Released ? "dynamic-tonnage-reallocation" : "leader-scan-tonnage-allocation";
            value.Released = true; value.Revision++;
            Remember(value, now, value.LastReason, plan);
        }

        private static void ArmTerminalAcceleration(Group value, PikeGroupTargetingPolicy.Assignment[] plan, float now)
        {
            var targetIds = new HashSet<uint>();
            foreach (var assignment in plan) targetIds.Add(assignment.TargetId);
            foreach (uint targetId in targetIds)
            {
                // Friendly missile geometry is known. The target direction is
                // derived only from the coordinator's legitimate observation.
                Contact contact = value.Contacts[targetId];
                Vector3 center = Vector3.zero;
                int count = 0;
                // A partial monotonic plan can omit a healthy receiver whose
                // current lane crossed another lane. It keeps its native target,
                // so include it in the new recipient's spacing geometry too.
                // Do not require CanRelease: the initial allocation arms these
                // schedules immediately before the group is marked Released.
                foreach (NaturalPikeGroupTargeting member in value.Members)
                {
                    if (!Live(member) || member.assignedTarget != targetId || member.missile.targetID.Id != targetId) continue;
                    center += member.missile.GlobalPosition().AsVector3(); count++;
                }
                if (count == 0) continue;
                center /= count;
                Vector3 direction = contact.Position.AsVector3() - center; direction.y = 0f;
                direction = direction.sqrMagnitude > 1f ? direction.normalized : value.Forward;
                var members = new List<PikeTerminalAccelerationPolicy.Member>(8);
                foreach (NaturalPikeGroupTargeting member in value.Members)
                {
                    if (!Live(member) || member.assignedTarget != targetId || member.missile.targetID.Id != targetId) continue;
                    members.Add(new PikeTerminalAccelerationPolicy.Member(member.missile.persistentID.Id,
                        Vector3.Dot(member.missile.GlobalPosition().AsVector3() - center, direction),
                        Vector3.Dot(member.missile.rb.velocity, direction)));
                }
                foreach (var schedule in PikeTerminalAccelerationPolicy.Plan(members.ToArray()))
                {
                    NaturalPikeGroupTargeting member = Find(value, schedule.Id);
                    NaturalPikeTerminalAcceleration.Arm(member.missile, targetId, schedule.Rank,
                        schedule.Count, now, schedule.DelaySeconds);
                }
            }
        }

        private static void Remember(Group value, float now, string reason, PikeGroupTargetingPolicy.Assignment[] plan)
        {
            var assignments = new List<object>();
            if (plan != null) foreach (var item in plan)
            {
                Contact contact = value.Contacts[item.TargetId];
                assignments.Add(new { missileId = item.MissileId, targetId = item.TargetId,
                    targetTonnage = contact.Tonnage, targetBearingDegrees = contact.Bearing,
                    targetObservation = contact.NativeHqMemory ? "native-HQ-position-memory" : "leader-positive-radar",
                    lastLeaderRadarGameSeconds = contact.SeenAt });
            }
            if (value.History.Count == 16) value.History.RemoveAt(0);
            value.History.Add(new { gameSeconds = now, reason, leaderId = value.Leader, revision = value.Revision,
                assignments = assignments.ToArray() });
        }

        private void Disabled(Unit unit) { Leave(); Unsubscribe(); }
        private void OnDestroy() { Leave(); Unsubscribe(); }
        private void Unsubscribe()
        {
            if (subscribed && missile != null) missile.onDisableUnit -= Disabled;
            subscribed = false;
        }
        private void Leave()
        {
            if (group == null) return;
            Group previous = group; group = null;
            previous.Members.Remove(this); previous.Dirty = true;
            if (previous.Members.Count == 0) Groups.Remove(previous.Id);
        }

        internal object Capture() => group == null ? null : new {
            cohortId = group.Id, leaderId = group.Leader, formationLeaderId = group.FormationLeader, assignedTargetId = assignedTarget,
            awaitingAssignment = AwaitingAssignment,
            terminalAcceleration = GetComponent<NaturalPikeTerminalAcceleration>()?.Capture(),
            searching = group.Searching, released = group.Released, leaderSearching = LeaderSearching,
            leaderRadarActive = LeaderRadarActive,
            leaderSearchAltitudeM = PikeGroupTargetingPolicy.LeaderSearchAltitude,
            searchStartedGameSeconds = group.SearchStartedAt, firstContactGameSeconds = group.FirstContactAt,
            searchEntryObjectiveRangeM = group.SearchEntryRange,
            activeScanStartedGameSeconds = group.ScanStartedAt, assignmentSearchSeconds = searchSeconds,
            areaRadarActivated = group.AreaRadarActivated,
            searchCourse = new[] { group.Forward.x, group.Forward.y, group.Forward.z },
            allocationRevision = group.Revision, leaderReplacements = group.LeaderReplacements,
            finalCorrectionCommitted = finalCommitted,
            activeMembers = group.Members.Count, observedContacts = group.Contacts.Count,
            scans = group.Scans, positiveReturns = group.Returns, retargets = group.Retargets,
            rejectedPlans = group.RejectedPlans, lastReason = group.LastReason,
            nativeClearEvents = nativeClearEvents, lastNativeClearedTargetId = lastNativeClearedTarget,
            lastNativeClearGameSeconds = lastNativeClearAt,
            exhaustedTargetIds = new List<uint>(exhaustedTargets).ToArray(),
            exhaustedTargetHistoryFull = exhaustedHistoryFull,
            nativeObjectiveHandoff = YieldToNativeGuidance, noObjectiveSinceGameSeconds = noObjectiveSince,
            authority = "positive-native-surface-radar; native-HQ-relay; receiver-own-ARH-lock",
            targetWeight = "UnitDefinition.mass-kg/1000", ordering = "actual-missile-lateral/observed-target-bearing",
            history = group.History.ToArray()
        };
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.SetTarget))]
    internal static class NaturalPikeGroupNativeTargetClear
    {
        internal static void Prefix(Missile __instance, Unit target)
        {
            if (target != null || __instance == null || !__instance.LocalSim || __instance.disabled ||
                __instance.targetID.Id == 0) return;
            NaturalPikeGroupTargeting member = __instance.GetComponent<NaturalPikeGroupTargeting>();
            if (member != null) member.NativeTargetCleared(__instance.targetID.Id);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.MissedTarget))]
    internal static class PikeFormationMissedWaypoint
    {
        internal static void Postfix(Missile __instance, ref bool __result)
        {
            if (!__result || __instance == null || !__instance.LocalSim || __instance.disabled) return;
            NaturalPikeGroupTargeting member = __instance.GetComponent<NaturalPikeGroupTargeting>();
            // Native ARH mistakes an intentionally rearward rendezvous aim
            // for an overshot target. Only active formation navigation gets
            // this exception; native seeker loss, speed/fuel retirement and
            // every independently attacking missile keep normal behavior.
            if (member != null && member.FollowingValidFormation) __result = false;
        }
    }
}
