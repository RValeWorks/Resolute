using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Resolute
{
    // Reuses JammingPod's server -> Unit.Jam -> native radar/seeker pathway.
    // Aircraft power components, defensive RCS penalties and receiver patches
    // are deliberately unnecessary. One group shares one bounded transmitter.
    internal sealed class NaturalPikeOffensiveEcm : MonoBehaviour
    {
        private enum ReceiverKind { SupportingRadar, ActiveArh, Sarh, ShipRadar }
        private enum SeekerKind { Unknown, Arh, Sarh }
        private sealed class SeekerClass { internal SeekerKind Kind; }
        private struct Receiver
        {
            internal Unit Unit;
            internal Missile Threat;
            internal Radar Radar;
            internal ReceiverKind Kind;
        }
        private sealed class Group
        {
            internal ulong Key;
            internal readonly NaturalPikeOffensiveEcm[] Members = new NaturalPikeOffensiveEcm[PikeOffensiveEcmPolicy.MaximumMembers];
            internal readonly uint[] Ids = new uint[PikeOffensiveEcmPolicy.MaximumMembers];
            internal readonly bool[] Eligible = new bool[PikeOffensiveEcmPolicy.MaximumMembers];
            internal readonly List<Missile> Threats = new List<Missile>(PikeOffensiveEcmPolicy.ThreatCapacity);
            internal readonly List<Receiver> Receivers = new List<Receiver>(PikeOffensiveEcmPolicy.ReceiverCapacity);
            internal readonly PikeOffensiveEcmRotation Rotation = new PikeOffensiveEcmRotation();
            internal int Count, Cursor;
            internal float NextScan, NextDecision;
        }
        private static readonly Dictionary<ulong, Group> Groups = new Dictionary<ulong, Group>();
        private static readonly ConditionalWeakTable<Missile, SeekerClass> Seekers = new ConditionalWeakTable<Missile, SeekerClass>();
        private readonly List<Missile> earlyThreats = new List<Missile>(8);
        private Missile missile;
        private NaturalWeaponPhase phase;
        private NaturalPikeFormation formation;
        private Group group;
        private bool subscribed;
        internal uint ActiveEmitterId { get; private set; }
        internal int NativeJamCalls { get; private set; }
        internal uint LastReceiverId { get; private set; }
        internal float LastPulseStrength { get; private set; }

        internal static void Configure(GameObject prefab)
        {
            if (prefab.GetComponent<NaturalPikeOffensiveEcm>() == null)
                prefab.AddComponent<NaturalPikeOffensiveEcm>();
        }

        private void Awake()
        {
            missile = GetComponent<Missile>();
            phase = GetComponent<NaturalWeaponPhase>();
            if (missile != null) { missile.onDisableUnit += Disabled; subscribed = true; }
        }

        internal void ObserveThreat(Missile incoming)
        {
            if (missile == null || !missile.LocalSim || incoming == null || incoming.disabled ||
                incoming.targetID != missile.persistentID) return;
            List<Missile> threats = group != null ? group.Threats : earlyThreats;
            int capacity = group != null ? PikeOffensiveEcmPolicy.ThreatCapacity : 8;
            if (threats.Count < capacity && !threats.Contains(incoming)) threats.Add(incoming);
        }

        private bool Operational()
        {
            return isActiveAndEnabled && missile != null && missile.IsServer && missile.LocalSim && !missile.disabled &&
                missile.persistentID.IsValid && missile.definition != null && missile.definition.jsonKey == "rsl_ashm" &&
                missile.owner != null && missile.owner.persistentID.IsValid && missile.NetworkHQ != null &&
                MissionManager.IsRunning && phase != null && missile.timeSinceSpawn >= phase.SwitchSeconds && missile.EngineOn();
        }

        private void Update()
        {
            ActiveEmitterId = 0;
            if (!Operational()) return;
            if (formation == null) formation = GetComponent<NaturalPikeFormation>();
            uint cohort = formation != null ? formation.LastCohortId : 0;
            if (cohort == 0) return; // Registration never invents a flight group.
            ulong key = ((ulong)missile.owner.persistentID.Id << 32) | cohort;
            if (group == null || group.Key != key) Join(key);
            if (group == null) return;
            float now = Time.timeSinceLevelLoad;
            for (int i = 0; i < group.Count; i++)
                group.Eligible[i] = group.Members[i] != null && group.Members[i].Operational();
            uint selected = group.Rotation.Select(now, group.Ids, group.Eligible, group.Count);
            ActiveEmitterId = selected;
            if (selected != missile.persistentID.Id) return;
            if (now < group.NextDecision) return;
            group.NextDecision = now + (float)PikeOffensiveEcmPolicy.PulseSeconds;
            if (now >= group.NextScan)
            {
                group.NextScan = now + PikeOffensiveEcmPolicy.ScanSeconds;
                Scan(group);
            }
            Receiver best = default(Receiver);
            float bestDistance = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < group.Receivers.Count; i++)
            {
                Receiver candidate = group.Receivers[i];
                float distance;
                if (!Qualified(group, candidate, out distance) ||
                    found && !Prefer(candidate, distance, best, bestDistance)) continue;
                if (!LineClear(candidate)) continue;
                found = true; best = candidate; bestDistance = distance;
            }
            if (!found || !group.Rotation.TryPulse(now)) return;
            float amount = PikeOffensiveEcmPolicy.Strength(bestDistance);
            if (amount <= 0f) return;
            best.Unit.Jam(new Unit.JamEventArgs { jammingUnit = missile, jamAmount = amount });
            NativeJamCalls++; LastReceiverId = best.Unit.persistentID.Id; LastPulseStrength = amount;
        }

        private void Join(ulong key)
        {
            Leave();
            Group value;
            if (!Groups.TryGetValue(key, out value))
            {
                if (Groups.Count >= PikeOffensiveEcmPolicy.MaximumGroups) return;
                value = new Group { Key = key }; Groups.Add(key, value);
            }
            int slot = -1;
            for (int i = 0; i < value.Count; i++)
                if (value.Ids[i] == missile.persistentID.Id) { slot = i; break; }
            if (slot < 0)
            {
                if (value.Count >= value.Members.Length) return;
                slot = value.Count++;
                value.Ids[slot] = missile.persistentID.Id;
            }
            group = value;
            value.Members[slot] = this;
            foreach (Missile threat in earlyThreats)
                if (value.Threats.Count < PikeOffensiveEcmPolicy.ThreatCapacity && !value.Threats.Contains(threat)) value.Threats.Add(threat);
            earlyThreats.Clear();
            value.NextScan = 0f;
        }

        private static SeekerKind Classify(Missile value)
        {
            SeekerClass result;
            if (Seekers.TryGetValue(value, out result)) return result.Kind;
            MissileSeeker seeker = value.GetComponent<MissileSeeker>();
            // Modded ammunition using native components works; a custom seeker
            // or subclass is unknown rather than guessed from its display name.
            Type type = seeker != null ? seeker.GetType() : null;
            result = new SeekerClass { Kind = type == typeof(ARHSeeker) ? SeekerKind.Arh :
                type == typeof(SARHSeeker) ? SeekerKind.Sarh : SeekerKind.Unknown };
            Seekers.Add(value, result);
            return result.Kind;
        }

        private static bool Threatens(Group value, Missile incoming)
        {
            if (incoming == null || incoming.disabled || incoming.targetID.NotValid) return false;
            for (int i = 0; i < value.Count; i++)
                if (value.Members[i] != null && value.Eligible[i] && value.Ids[i] == incoming.targetID.Id) return true;
            return false;
        }

        private void Scan(Group value)
        {
            for (int i = value.Receivers.Count - 1; i >= 0; i--)
            {
                float ignored;
                if (!Qualified(value, value.Receivers[i], out ignored) || !LineClear(value.Receivers[i])) value.Receivers.RemoveAt(i);
            }
            for (int i = value.Threats.Count - 1; i >= 0; i--)
            {
                Missile threat = value.Threats[i];
                if (!Threatens(value, threat)) value.Threats.RemoveAt(i);
                else AddThreatReceivers(value, threat);
            }
            int count = UnitRegistry.allUnits.Count;
            for (int checkedUnits = 0; checkedUnits < Math.Min(count, PikeOffensiveEcmPolicy.RegistryChecks); checkedUnits++)
            {
                if (value.Cursor >= count) value.Cursor = 0;
                Unit candidate = UnitRegistry.allUnits[value.Cursor++];
                Missile incoming = candidate as Missile;
                if (incoming != null && Threatens(value, incoming))
                {
                    if (!value.Threats.Contains(incoming))
                    {
                        if (value.Threats.Count < PikeOffensiveEcmPolicy.ThreatCapacity) value.Threats.Add(incoming);
                        AddThreatReceivers(value, incoming);
                    }
                }
                else if (candidate is Ship)
                {
                    Radar radar = candidate.radar as Radar;
                    if (radar != null && radar.GetAttachedUnit() == candidate)
                        Offer(value, new Receiver { Unit = candidate, Radar = radar, Kind = ReceiverKind.ShipRadar });
                }
            }
        }

        private void AddThreatReceivers(Group value, Missile incoming)
        {
            if (incoming.NetworkHQ == null || incoming.NetworkHQ == missile.NetworkHQ ||
                !missile.NetworkHQ.IsTargetBeingTracked(incoming)) return;
            SeekerKind kind = Classify(incoming);
            if (kind == SeekerKind.Sarh)
            {
                Radar source = incoming.radar as Radar;
                if (source != null)
                    Offer(value, new Receiver { Unit = source.GetAttachedUnit(), Radar = source, Threat = incoming, Kind = ReceiverKind.SupportingRadar });
                Offer(value, new Receiver { Unit = incoming, Threat = incoming, Kind = ReceiverKind.Sarh });
            }
            else if (kind == SeekerKind.Arh)
                Offer(value, new Receiver { Unit = incoming, Threat = incoming, Kind = ReceiverKind.ActiveArh });
        }

        private bool Qualified(Group value, Receiver receiver, out float distance)
        {
            distance = float.PositiveInfinity;
            Unit target = receiver.Unit;
            if (target == null || target.disabled || target.NetworkHQ == null || target.NetworkHQ == missile.NetworkHQ ||
                !target.persistentID.IsValid || !missile.NetworkHQ.IsTargetBeingTracked(target)) return false;
            distance = (target.GlobalPosition() - missile.GlobalPosition()).magnitude;
            if (PikeOffensiveEcmPolicy.Strength(distance) <= 0f) return false;
            if (receiver.Kind == ReceiverKind.ShipRadar || receiver.Kind == ReceiverKind.SupportingRadar)
            {
                if (receiver.Radar == null || !receiver.Radar.IsOperational() || receiver.Radar.GetAttachedUnit() != target ||
                    target.radar != receiver.Radar) return false;
                if (receiver.Kind == ReceiverKind.ShipRadar) return target is Ship;
            }
            if (!Threatens(value, receiver.Threat) || receiver.Threat.NetworkHQ == null ||
                receiver.Threat.NetworkHQ == missile.NetworkHQ || !missile.NetworkHQ.IsTargetBeingTracked(receiver.Threat)) return false;
            if (receiver.Kind == ReceiverKind.SupportingRadar)
                return Classify(receiver.Threat) == SeekerKind.Sarh && receiver.Threat.radar == receiver.Radar;
            if (receiver.Kind == ReceiverKind.Sarh) return Classify(receiver.Threat) == SeekerKind.Sarh;
            return Classify(receiver.Threat) == SeekerKind.Arh &&
                (receiver.Threat.seekerMode == Missile.SeekerMode.activeLock || receiver.Threat.seekerMode == Missile.SeekerMode.activeSearch);
        }

        private void Offer(Group value, Receiver candidate)
        {
            float distance;
            if (!Qualified(value, candidate, out distance)) return;
            for (int i = 0; i < value.Receivers.Count; i++)
                if (value.Receivers[i].Unit == candidate.Unit)
                {
                    if ((int)candidate.Kind <= (int)value.Receivers[i].Kind) value.Receivers[i] = candidate;
                    return;
                }
            // An occluded high-priority radar must not fill the bounded table
            // and evict usable direct-seeker fallbacks behind it.
            if (!LineClear(candidate)) return;
            if (value.Receivers.Count < PikeOffensiveEcmPolicy.ReceiverCapacity) { value.Receivers.Add(candidate); return; }
            int worst = 0; float worstDistance;
            Qualified(value, value.Receivers[worst], out worstDistance);
            for (int i = 1; i < value.Receivers.Count; i++)
            {
                float oldDistance;
                Qualified(value, value.Receivers[i], out oldDistance);
                if (Prefer(value.Receivers[worst], worstDistance, value.Receivers[i], oldDistance))
                { worst = i; worstDistance = oldDistance; }
            }
            if (Prefer(candidate, distance, value.Receivers[worst], worstDistance)) value.Receivers[worst] = candidate;
        }

        private bool LineClear(Receiver receiver)
        {
            Transform point = receiver.Radar != null ? receiver.Radar.GetScanPoint() : receiver.Unit.transform;
            return point != null && !Physics.Linecast(missile.transform.position, point.position, PhysicsLayers.StaticsMask);
        }

        private static bool Prefer(Receiver candidate, float distance, Receiver incumbent, float incumbentDistance)
        {
            if (candidate.Kind != incumbent.Kind) return (int)candidate.Kind < (int)incumbent.Kind;
            if (distance != incumbentDistance) return distance < incumbentDistance;
            return candidate.Unit.persistentID.Id < incumbent.Unit.persistentID.Id;
        }

        private void Disabled(Unit _) { Leave(); }
        private void OnDisable() { Leave(); }
        private void OnDestroy()
        {
            Leave();
            if (subscribed && missile != null) missile.onDisableUnit -= Disabled;
            subscribed = false;
        }
        private void Leave()
        {
            ActiveEmitterId = 0;
            if (group == null) return;
            bool remaining = false;
            for (int i = 0; i < group.Count; i++)
            {
                if (group.Members[i] == this) { group.Members[i] = null; group.Eligible[i] = false; }
                remaining |= group.Members[i] != null;
            }
            if (!remaining) Groups.Remove(group.Key);
            group = null;
        }
    }
}
