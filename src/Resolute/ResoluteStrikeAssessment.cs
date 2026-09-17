using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Small, ship-owned evidence ledger. No enemy health polling, global sensor
    // scans or inferred kills from a missile disappearing. Native observations
    // authorize evidence; expired/unknown outcomes cannot authorize escalation.
    internal sealed class ResoluteStrikeAssessment
    {
        internal sealed class Contact
        {
            internal Unit Unit;
            internal GlobalPosition Position;
            internal Vector3 Velocity;
            internal float ObservedAt = -1f, SampledAt = -1f, LastPlanAt = -1000f;
            internal float ThreatUntil = -1f, EvidenceAt = -1f;
            internal int Hits, Interceptions, Misses, Unknown, Destroyed;
            internal bool HasPosition;
        }
        internal sealed class Shot
        {
            internal ResoluteStrikeAssessment Ledger;
            internal Missile Missile;
            internal uint InitialTarget, CurrentTarget;
            internal float LaunchedAt, Deadline, EndedAt = -1f;
            internal bool Hit, Intercepted, Miss, Counted;
        }
        private const int MaximumContacts = 128, MaximumShotHistory = 128;
        private static readonly Dictionary<uint, ResoluteStrikeAssessment> Owners = new Dictionary<uint, ResoluteStrikeAssessment>();
        private static readonly ConditionalWeakTable<Missile, Shot> ShotOwners = new ConditionalWeakTable<Missile, Shot>();
        [ThreadStatic] internal static Shot CollisionSource;
        private readonly Dictionary<uint, Contact> contacts = new Dictionary<uint, Contact>();
        private readonly List<Shot> shots = new List<Shot>(MaximumShotHistory);
        private readonly List<uint> expired = new List<uint>(MaximumContacts);
        private readonly Ship owner;
        private float nextUpdate;
        internal int ConfirmedHits, ObservedInterceptions, UnknownOutcomes, ObservedMisses;

        internal ResoluteStrikeAssessment(Ship owner)
        {
            this.owner = owner;
            Owners[owner.persistentID.Id] = this;
            nextUpdate = Time.timeSinceLevelLoad + owner.persistentID.Id % 7 * .09f;
        }

        internal void Dispose()
        {
            ResoluteStrikeAssessment current;
            if (owner != null && Owners.TryGetValue(owner.persistentID.Id, out current) && current == this)
                Owners.Remove(owner.persistentID.Id);
            foreach (Shot shot in shots) if (shot.Missile != null) ShotOwners.Remove(shot.Missile);
            shots.Clear(); contacts.Clear();
        }

        internal Contact Observe(Unit target, TrackingInfo track)
        {
            if (target == null || track == null || target.NetworkHQ == null || owner.NetworkHQ == null ||
                target.NetworkHQ == owner.NetworkHQ) return null;
            if (target is Missile incoming)
            {
                ResoluteSupplyDefense.IsIncoming(incoming, owner);
                if (track.Observed() && NaturalPikeObservedThreat.IsTargetOrObservedIncoming(incoming, owner) && incoming.owner is Ship attacker &&
                    attacker.NetworkHQ != owner.NetworkHQ) Threat(attacker.persistentID.Id, Time.timeSinceLevelLoad);
                return null;
            }
            if (!(target is Ship) && !(target is Building) && !(target is GroundVehicle)) return null;
            Contact contact;
            if (!contacts.TryGetValue(target.persistentID.Id, out contact))
            {
                if (contacts.Count >= MaximumContacts) return null;
                contact = new Contact { Unit = target };
                contacts.Add(target.persistentID.Id, contact);
            }
            float now = Time.timeSinceLevelLoad;
            if (track.Observed() && ResoluteEngagementPolicy.TrackUsable(now, track.lastSpottedTime))
            {
                // Read the normal native observation only in its observed
                // window. Never update ObservedAt from our own sampling time.
                GlobalPosition position = track.GetPosition();
                if (Finite(position))
                {
                    contact.Position = position;
                    contact.Velocity = target.rb != null ? target.rb.velocity : Vector3.zero;
                    contact.ObservedAt = track.lastSpottedTime;
                    contact.SampledAt = now; contact.HasPosition = true;
                }
            }
            return contact;
        }

        internal bool TrackUsable(Unit target, TrackingInfo track)
        {
            Contact contact = Observe(target, track);
            return contact != null && contact.HasPosition &&
                ResoluteEngagementPolicy.TrackUsable(Time.timeSinceLevelLoad, contact.ObservedAt);
        }

        internal bool SameGroup(Unit first, Unit second)
        {
            if (first == second) return true;
            Contact a, b;
            if (first == null || second == null || !contacts.TryGetValue(first.persistentID.Id, out a) ||
                !contacts.TryGetValue(second.persistentID.Id, out b) || !a.HasPosition || !b.HasPosition) return false;
            Vector3 between = a.Position - b.Position;
            if (between.sqrMagnitude > ResoluteEngagementPolicy.ContactGroupRadius * ResoluteEngagementPolicy.ContactGroupRadius) return false;
            Vector3 forwardA = a.Position - owner.GlobalPosition(), forwardB = b.Position - owner.GlobalPosition();
            forwardA.y = forwardB.y = 0f;
            return forwardA.sqrMagnitude > 1f && forwardB.sqrMagnitude > 1f && Vector3.Dot(forwardA.normalized, forwardB.normalized) >= .9848077f;
        }

        internal bool SelfDefense(Unit target)
        {
            Contact contact;
            if (!(target is Ship) || !contacts.TryGetValue(target.persistentID.Id, out contact) || !contact.HasPosition) return false;
            // Reserve Pikes counter an actively attacking ship, not an
            // arbitrary nearby hostile or the inbound missile itself.
            return (Time.timeSinceLevelLoad < contact.ThreatUntil || ResoluteSupplyDefense.IsAttacker(owner, target)) &&
                ResoluteEngagementPolicy.TrackUsable(Time.timeSinceLevelLoad, contact.ObservedAt) &&
                (contact.Position - owner.GlobalPosition()).magnitude / 849f < 180f;
        }

        private void Threat(uint id, float now)
        {
            if (owner.NetworkHQ == null) return;
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(new PersistentID { Id = id });
            Unit target;
            if (track == null || !track.TryGetUnit(out target) || !(target is Ship)) return;
            Contact contact = Observe(target, track);
            if (contact != null) contact.ThreatUntil = Mathf.Max(contact.ThreatUntil, now + 45f);
        }

        internal int GroupsFor(Unit target)
        {
            int intercepted = 0, outcomes = 0;
            foreach (Shot shot in shots)
            {
                if (!shot.Counted || shot.EndedAt < 0f || Time.timeSinceLevelLoad - shot.EndedAt >= 180f ||
                    !contacts.TryGetValue(shot.CurrentTarget, out Contact contact) || !SameGroup(target, contact.Unit)) continue;
                if (shot.Hit || shot.Miss || shot.Intercepted) outcomes++;
                if (shot.Intercepted && !shot.Hit) intercepted++;
            }
            return ResoluteEngagementPolicy.StrikeGroups(intercepted, outcomes);
        }

        internal int Demand(Unit target, TrackingInfo track, float nominal, int groups)
        {
            Contact contact = Observe(target, track);
            if (contact == null) return 0;
            int evidenceHits = 0;
            foreach (Shot shot in shots)
                if (shot.Counted && shot.Hit && shot.CurrentTarget == target.persistentID.Id && shot.EndedAt >= 0f &&
                    Time.timeSinceLevelLoad - shot.EndedAt < 180f) evidenceHits++;
            int commitments = Math.Max(Math.Max(0, (int)track.missileAttacks) + Math.Max(0, (int)track.attackers), PotentialFor(target));
            return ResoluteEngagementPolicy.StrikeDemand(nominal, evidenceHits, groups, commitments);
        }

        internal int ActiveCount
        {
            get
            {
                int count = 0; float now = Time.timeSinceLevelLoad;
                foreach (Shot shot in shots)
                    if (shot.EndedAt < 0f && shot.Missile != null && !shot.Missile.disabled && now <= shot.Deadline) count++;
                return count;
            }
        }

        internal int GroupActiveCount(Unit target)
        {
            int count = 0;
            foreach (Shot shot in shots)
                if (shot.EndedAt < 0f && shot.Missile != null && !shot.Missile.disabled && Time.timeSinceLevelLoad <= shot.Deadline &&
                    contacts.TryGetValue(shot.InitialTarget, out Contact contact) && SameGroup(target, contact.Unit)) count++;
            return count;
        }

        private int PotentialFor(Unit target)
        {
            int count = 0;
            foreach (Shot shot in shots)
                if (shot.EndedAt < 0f && shot.Missile != null && !shot.Missile.disabled &&
                    (shot.CurrentTarget == target.persistentID.Id || shot.InitialTarget == target.persistentID.Id)) count++;
            return count;
        }

        internal bool MayReassess(Unit target)
        {
            Contact contact;
            return target != null && (!contacts.TryGetValue(target.persistentID.Id, out contact) ||
                Time.timeSinceLevelLoad - contact.LastPlanAt >= ResoluteEngagementPolicy.SurfaceReassessmentSeconds);
        }

        internal void Planned(Unit[] targets)
        {
            foreach (Unit target in targets)
                if (target != null && contacts.TryGetValue(target.persistentID.Id, out Contact contact))
                    contact.LastPlanAt = Time.timeSinceLevelLoad;
        }

        internal void Register(Missile missile)
        {
            if (missile == null || !SurfaceSalvoOrderPolicy.Applies(NaturalMissileTargeting.Key(missile.GetWeaponInfo()))) return;
            if (ShotOwners.TryGetValue(missile, out Shot previous)) return;
            if (shots.Count >= MaximumShotHistory)
            {
                int oldest = shots.FindIndex(s => s.EndedAt >= 0f);
                if (oldest < 0) return;
                if (shots[oldest].Missile != null) ShotOwners.Remove(shots[oldest].Missile);
                shots.RemoveAt(oldest);
            }
            float now = Time.timeSinceLevelLoad, distance = 0f;
            uint id = missile.targetID.Id;
            TrackingInfo track = owner.NetworkHQ != null ? owner.NetworkHQ.GetTrackingData(missile.targetID) : null;
            Unit target;
            if (track != null && track.TryGetUnit(out target))
            {
                Contact contact = Observe(target, track);
                if (contact != null && contact.HasPosition) distance = (contact.Position - missile.GlobalPosition()).magnitude;
            }
            var shot = new Shot { Ledger = this, Missile = missile, InitialTarget = id, CurrentTarget = id,
                LaunchedAt = now, Deadline = now + distance / Mathf.Max(100f, missile.GetWeaponInfo().GetMaxSpeed() * .5f) + 90f };
            shots.Add(shot); ShotOwners.Add(missile, shot);
        }

        internal void Deregister(Missile missile)
        {
            if (missile != null && ShotOwners.TryGetValue(missile, out Shot shot) && shot.Ledger == this && shot.EndedAt < 0f)
                shot.EndedAt = Time.timeSinceLevelLoad;
        }

        internal void Tick()
        {
            float now = Time.timeSinceLevelLoad;
            if (now < nextUpdate) return;
            nextUpdate = now + 1f;
            foreach (Shot shot in shots)
            {
                if (shot.Missile != null && !shot.Missile.disabled)
                {
                    if (shot.Missile.targetID.IsValid) shot.CurrentTarget = shot.Missile.targetID.Id;
                    continue;
                }
                if (shot.EndedAt < 0f) shot.EndedAt = now;
                // Allow the native damage/detonation callback to finish before
                // classifying a deregistration as an unknown outcome.
                if (shot.Counted || now - shot.EndedAt < 1f) continue;
                shot.Counted = true;
                Contact contact;
                if (!contacts.TryGetValue(shot.CurrentTarget, out contact)) contacts.TryGetValue(shot.InitialTarget, out contact);
                if (contact == null) continue;
                if (now - contact.EvidenceAt >= 180f) { contact.Hits = contact.Interceptions = contact.Misses = contact.Unknown = 0; }
                if (shot.Hit) { contact.Hits++; ConfirmedHits++; }
                else if (shot.Intercepted) { contact.Interceptions++; ObservedInterceptions++; }
                else if (shot.Miss) { contact.Misses++; ObservedMisses++; }
                else { contact.Unknown++; UnknownOutcomes++; }
                contact.EvidenceAt = now;
            }
            expired.Clear();
            foreach (var pair in contacts)
            {
                Contact contact = pair.Value;
                TrackingInfo track = owner.NetworkHQ != null ? owner.NetworkHQ.GetTrackingData(new PersistentID { Id = pair.Key }) : null;
                if (contact.Unit != null && !contact.Unit.disabled && track != null) Observe(contact.Unit, track);
                if (contact.Unit != null && contact.Unit.disabled)
                {
                    // Native HQ confirms disabled targets. Do not infer how
                    // much damage a still-alive ship has behind fog of war.
                    contact.Destroyed = 1;
                    ResoluteStrikeOrders.CancelPending(owner, pair.Key);
                }
                if (now - Math.Max(contact.ObservedAt, contact.EvidenceAt) > 600f && now > contact.ThreatUntil)
                    expired.Add(pair.Key);
            }
            foreach (uint id in expired) contacts.Remove(id);
            for (int i = shots.Count - 1; i >= 0; i--)
                if (shots[i].EndedAt >= 0f && now - shots[i].EndedAt > 180f)
                { if (shots[i].Missile != null) ShotOwners.Remove(shots[i].Missile); shots.RemoveAt(i); }
        }

        internal object Capture() => new { trackedContacts = contacts.Count, shotRecords = shots.Count,
            activeCommitments = ActiveCount, confirmedHits = ConfirmedHits, observedInterceptions = ObservedInterceptions,
            observedMisses = ObservedMisses, unknownOutcomes = UnknownOutcomes, reserveWaves = ResoluteEngagementPolicy.ReserveWaves };

        private bool Observed(Unit target)
        {
            if (target == null || owner == null || owner.NetworkHQ == null) return false;
            TrackingInfo track = owner.NetworkHQ.GetTrackingData(target.persistentID);
            return track != null && track.Observed();
        }

        internal static Shot Find(Missile missile)
        { Shot shot; return missile != null && ShotOwners.TryGetValue(missile, out shot) ? shot : null; }

        internal static void DamageReported(Unit target, PersistentID dealer, float amount)
        {
            if (target == null || amount <= .1f) return;
            if (Owners.TryGetValue(target.persistentID.Id, out ResoluteStrikeAssessment attacked))
                attacked.Threat(dealer.Id, Time.timeSinceLevelLoad);
            Shot shot = CollisionSource;
            if (shot == null || shot.Missile == null || dealer != shot.Missile.ownerID ||
                !(target is Ship) || !shot.Ledger.Observed(target) || target.NetworkHQ == shot.Ledger.owner.NetworkHQ) return;
            shot.Hit = true; shot.CurrentTarget = target.persistentID.Id;
        }

        internal static void Intercepted(Missile missile, PersistentID dealer, bool wasAlive, float impactDamage)
        {
            Shot shot = Find(missile); Unit source;
            if (shot == null || !wasAlive || !missile.disabled || impactDamage > 0f || dealer == missile.ownerID ||
                dealer == missile.persistentID || !UnitRegistry.TryGetUnit(dealer, out source) || source == null ||
                source.NetworkHQ == shot.Ledger.owner.NetworkHQ || !shot.Ledger.Observed(source)) return;
            shot.Intercepted = true;
        }

        internal static void Detonating(Missile missile, bool hitTerrain)
        {
            Shot shot = Find(missile); Unit objective;
            if (shot == null || missile.disabled || !UnitRegistry.TryGetUnit(missile.targetID, out objective) ||
                !shot.Ledger.Observed(objective)) return;
            if (hitTerrain || missile.GlobalPosition().y <= .5f) shot.Miss = true;
        }
        private static bool Finite(GlobalPosition point) => ResoluteEngagementPolicy.Finite(point.x) &&
            ResoluteEngagementPolicy.Finite(point.y) && ResoluteEngagementPolicy.Finite(point.z);
    }

    [HarmonyPatch(typeof(Missile), "DetectCollisions")]
    internal static class ResoluteStrikeCollisionEvidence
    {
        private static void Prefix(Missile __instance, out ResoluteStrikeAssessment.Shot __state)
        { __state = ResoluteStrikeAssessment.CollisionSource; ResoluteStrikeAssessment.CollisionSource = ResoluteStrikeAssessment.Find(__instance); }
        private static Exception Finalizer(Exception __exception, ResoluteStrikeAssessment.Shot __state)
        { ResoluteStrikeAssessment.CollisionSource = __state; return __exception; }
    }
    [HarmonyPatch(typeof(Unit), nameof(Unit.RecordDamage))]
    internal static class ResoluteStrikeDamageEvidence
    {
        private static void Postfix(Unit __instance, PersistentID lastDamagedBy, float damageAmount)
            => ResoluteStrikeAssessment.DamageReported(__instance, lastDamagedBy, damageAmount);
    }
    [HarmonyPatch(typeof(Missile), nameof(Missile.TakeDamage))]
    internal static class ResoluteStrikeInterceptionEvidence
    {
        private static void Prefix(Missile __instance, out bool __state) { __state = !__instance.disabled; }
        private static void Postfix(Missile __instance, PersistentID dealerID, float impactDamage, bool __state)
            => ResoluteStrikeAssessment.Intercepted(__instance, dealerID, __state, impactDamage);
    }
    [HarmonyPatch(typeof(Missile), nameof(Missile.Detonate))]
    internal static class ResoluteStrikeMissEvidence
    {
        private static void Prefix(Missile __instance, bool hitTerrain) => ResoluteStrikeAssessment.Detonating(__instance, hitTerrain);
    }
}
