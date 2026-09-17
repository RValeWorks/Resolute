using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    public sealed class ResoluteOffensiveEcmSnapshot
    {
        public bool Enabled;
        public float RangeMetres, HalfAngleDegrees;
        public GlobalPosition Point;
        public Unit Target;
        public string Status;
        public int QueueCount;
    }

    // Operating hooks only. The separate addon supplies all controls/graphics.
    public static class ResoluteOffensiveEcm
    {
        public static bool SetPosition(Ship ship, GlobalPosition point, out string reason, bool append = false)
        {
            if (!ResoluteNavigationCommands.CanCommand(ship, out reason)) return false;
            if (!ResoluteOffensiveEcmPolicy.Finite(point.x) || !ResoluteOffensiveEcmPolicy.Finite(point.y) ||
                !ResoluteOffensiveEcmPolicy.Finite(point.z)) { reason = "Invalid ECM aim position."; return false; }
            if (ResoluteOffensiveEcmPolicy.HorizontalDistance(point - ship.GlobalPosition()) < 1f)
            { reason = "Choose an ECM bearing away from the ship."; return false; }
            if (!HardwareReady(ship, out reason)) return false;
            return State(ship).Order(point, null, out reason, append);
        }

        public static bool SetTarget(Ship ship, Unit target, out string reason, bool append = false)
        {
            if (!ResoluteNavigationCommands.CanCommand(ship, out reason)) return false;
            if (target == null || target == ship || target.disabled || target.persistentID.NotValid)
            { reason = "Choose another operational unit for the ECM bearing."; return false; }
            GlobalPosition point;
            if (ship.NetworkHQ == null || !ship.NetworkHQ.TryGetKnownPosition(target, out point))
            { reason = "No known position for that contact."; return false; }
            if (!HardwareReady(ship, out reason)) return false;
            return State(ship).Order(point, target, out reason, append);
        }

        public static bool Stop(Ship ship, out string reason)
        {
            if (!ResoluteNavigationCommands.CanCommand(ship, out reason)) return false;
            ship.GetComponent<ResoluteOffensiveEcmState>()?.Stop("Offensive ECM off.");
            reason = "Offensive ECM off."; return true;
        }

        public static ResoluteOffensiveEcmSnapshot GetSnapshot(Ship ship)
        {
            if (ship == null || !Plugin.IsResolute(ship.definition)) return null;
            var state = ship.GetComponent<ResoluteOffensiveEcmState>();
            return new ResoluteOffensiveEcmSnapshot {
                Enabled = state != null && state.Requested,
                RangeMetres = ResoluteOffensiveEcmPolicy.Range,
                HalfAngleDegrees = ResoluteOffensiveEcmPolicy.HalfAngle,
                Point = state != null ? state.Point : ship.GlobalPosition(),
                Target = state != null ? state.Target : null,
                QueueCount = state != null ? state.QueueCount : 0,
                Status = state != null ? state.Status : "Offensive ECM off."
            };
        }

        private static bool HardwareReady(Ship ship, out string reason)
        {
            Transform emitter;
            var hardware = ship.GetComponent<ResoluteShipEcm>();
            bool ready = hardware != null && hardware.TryGetEmitter(out emitter);
            reason = ready ? null : "The ECM mast is unavailable or damaged.";
            return ready;
        }
        private static ResoluteOffensiveEcmState State(Ship ship)
            => ship.GetComponent<ResoluteOffensiveEcmState>() ?? ship.gameObject.AddComponent<ResoluteOffensiveEcmState>();
    }

    internal static class ResoluteOffensiveEcmPolicy
    {
        // Strong directional ship transmitter: five times Pike's range and
        // 4.375 times its maximum pulse. Native receiver sensitivity, forward
        // seeker acceptance, interference decay and lock loss remain native.
        internal const float Range = 60000f, HalfAngle = 25f, MaximumPulse = .35f;
        internal const float PulseSeconds = .25f, ScanSeconds = .5f;
        internal const int ReceiverCapacity = 16, RegistryChecks = 64;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static float HorizontalDistance(Vector3 offset) => Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
        internal static bool InSector(Vector3 offset, Vector3 bearing, out float distance)
        {
            offset.y = bearing.y = 0f;
            distance = HorizontalDistance(offset);
            if (!Finite(distance) || distance <= 0f || distance >= Range ||
                !Finite(bearing.x) || !Finite(bearing.z) || bearing.sqrMagnitude < 1f) return false;
            return Vector3.Dot(offset, bearing.normalized) / distance >= Mathf.Cos(HalfAngle * Mathf.Deg2Rad);
        }
        internal static float Strength(float distance)
            => !Finite(distance) || distance < 0f || distance >= Range ? 0f : MaximumPulse * (1f - distance / Range);
    }

    internal sealed class ResoluteOffensiveEcmState : MonoBehaviour
    {
        private struct Receiver { internal Unit Unit; internal Radar Radar; internal Missile Missile; internal bool Arh; }
        private struct OrderPoint { internal GlobalPosition Point; internal Unit Target; internal bool IsTarget; }
        private const int MaximumOrders = 16;
        private readonly Queue<OrderPoint> pending = new Queue<OrderPoint>(MaximumOrders);
        private Ship ship;
        private ResoluteShipEcm hardware;
        private Player issuer;
        private FactionHQ issuingFaction;
        private Transform emitter;
        private readonly List<Receiver> receivers = new List<Receiver>(ResoluteOffensiveEcmPolicy.ReceiverCapacity);
        private int cursor;
        private float nextScan, nextPulse;
        private TrackingInfo track;
        private Vector3 observedVelocity;
        private GlobalPosition observedPoint;
        private float observedTime;
        private bool hasObservation;
        private bool isTargetOrder, targetUnavailable;
        internal bool Requested { get; private set; }
        internal GlobalPosition Point { get; private set; }
        internal Unit Target { get; private set; }
        internal string Status { get; private set; } = "Offensive ECM off.";
        internal int NativeJamCalls { get; private set; }
        internal int QueueCount => Requested ? 1 + pending.Count : 0;

        private void Awake()
        {
            ship = GetComponent<Ship>(); hardware = GetComponent<ResoluteShipEcm>();
            enabled = false;
        }

        internal bool Order(GlobalPosition point, Unit target, out string reason, bool append = false)
        {
            OrderPoint order = new OrderPoint { Point = point, Target = target, IsTarget = target != null };
            if (append && Requested)
            {
                if (QueueCount >= MaximumOrders) { reason = "ECM order queue is full (16 orders)."; return false; }
                pending.Enqueue(order); reason = "ECM bearing added to the order queue."; return true;
            }
            if (!GameManager.GetLocalPlayer<Player>(out issuer)) { reason = "No issuing player."; return false; }
            issuingFaction = issuer.HQ;
            pending.Clear(); Requested = true; enabled = true; nextPulse = 0f;
            BeginOrder(order);
            reason = "Offensive ECM transmitting along the ordered bearing.";
            return true;
        }

        private void BeginOrder(OrderPoint order)
        {
            ReleaseTarget(); ReleaseTrack(); hasObservation = false; observedVelocity = Vector3.zero;
            Point = order.Point; Target = order.Target; isTargetOrder = order.IsTarget; targetUnavailable = false;
            if (Target != null) Target.onDisableUnit += TargetDisabled;
            // Queue progression retains the existing transmitter pulse clock;
            // changing targets must not produce a second pulse next frame.
            receivers.Clear(); nextScan = 0f;
            TrackBearing();
        }

        internal void Stop(string status)
        {
            Requested = false; pending.Clear(); receivers.Clear(); ReleaseTarget(); ReleaseTrack(); Target = null;
            Status = status; enabled = false;
        }

        private void Update()
        {
            if (!Requested) return;
            if (ship == null || issuer == null || issuer.HQ != issuingFaction ||
                !ResoluteNavigationCommands.HasPermission(ship, issuer) || !MissionManager.IsRunning ||
                hardware == null || !hardware.TryGetEmitter(out emitter))
            { Stop("Offensive ECM stopped: command authority or emitter unavailable."); return; }
            float now = Time.timeSinceLevelLoad;
            if (now < nextPulse) return;
            nextPulse = now + ResoluteOffensiveEcmPolicy.PulseSeconds;
            TrackBearing();
            if (!Requested) return;
            if (now >= nextScan)
            { nextScan = now + ResoluteOffensiveEcmPolicy.ScanSeconds; Scan(); }
            for (int i = receivers.Count - 1; i >= 0; i--)
            {
                float distance;
                Receiver candidate = receivers[i];
                if (!Qualified(candidate, out distance)) { receivers.RemoveAt(i); continue; }
                Transform receiverPoint = candidate.Radar != null ? candidate.Radar.GetScanPoint() : candidate.Unit.transform;
                if (receiverPoint == null || Physics.Linecast(emitter.position, receiverPoint.position, PhysicsLayers.StaticsMask)) continue;
                candidate.Unit.Jam(new Unit.JamEventArgs {
                    jammingUnit = ship, jamAmount = ResoluteOffensiveEcmPolicy.Strength(distance)
                });
                NativeJamCalls++;
            }
        }

        private void TrackBearing()
        {
            if (!isTargetOrder) { Status = "Offensive ECM: ordered area bearing."; return; }
            // Native HQ tracks subscribe to onDisableUnit and remove disabled
            // units even without a fresh sighting. This is target availability,
            // not proof that our ECM or a strike caused the loss. A stale track,
            // forgotten contact, missing reference or stopped radar alone does
            // not complete a command.
            if (targetUnavailable || Target != null && Target.disabled)
            {
                if (pending.Count > 0) BeginOrder(pending.Dequeue());
                else Stop("Target no longer available; offensive ECM off.");
                return;
            }
            if (Target == null)
            {
                if (hasObservation) Point = observedPoint + observedVelocity * Mathf.Max(0f, Time.timeSinceLevelLoad - observedTime);
                Status = "Offensive ECM: contact lost, estimated bearing."; return;
            }
            TrackingInfo current = ship.NetworkHQ.GetTrackingData(Target.persistentID);
            if (current != track)
            {
                ReleaseTrack(); track = current;
                if (track != null) { track.OnSpotted += Observed; Observed(); }
            }
            // Only actual HQ observations update the remembered velocity. A
            // stale track never reads Target.transform or Target.rb.velocity.
            if (Target.NetworkHQ == ship.NetworkHQ)
            {
                GlobalPosition known;
                if (ship.NetworkHQ.TryGetKnownPosition(Target, out known)) Remember(known, Time.timeSinceLevelLoad);
            }
            else if (track != null && (!hasObservation || track.lastSpottedTime > observedTime)) Observed();
            if (hasObservation)
                Point = observedPoint + observedVelocity * Mathf.Max(0f, Time.timeSinceLevelLoad - observedTime);
            Status = track != null && track.Observed() || Target.NetworkHQ == ship.NetworkHQ
                ? "Offensive ECM: tracking contact bearing."
                : "Offensive ECM: stale contact, estimated bearing.";
        }

        private void Observed()
        { if (track != null) Remember(track.lastKnownPosition, track.lastSpottedTime); }
        private void Remember(GlobalPosition position, float time)
        {
            if (!ResoluteOffensiveEcmPolicy.Finite(time) || !ResoluteOffensiveEcmPolicy.Finite(position.x) ||
                !ResoluteOffensiveEcmPolicy.Finite(position.y) || !ResoluteOffensiveEcmPolicy.Finite(position.z)) return;
            if (hasObservation && time > observedTime + .01f)
            {
                Vector3 velocity = (position - observedPoint) / (time - observedTime);
                if (ResoluteOffensiveEcmPolicy.Finite(velocity.sqrMagnitude)) observedVelocity = velocity;
            }
            if (hasObservation && time < observedTime) observedVelocity = Vector3.zero;
            observedPoint = position; observedTime = time; hasObservation = true;
        }
        private void ReleaseTrack()
        { if (track != null) track.OnSpotted -= Observed; track = null; }
        private void TargetDisabled(Unit target) { targetUnavailable = true; }
        private void ReleaseTarget()
        { if (Target != null) Target.onDisableUnit -= TargetDisabled; }

        private void Scan()
        {
            for (int i = receivers.Count - 1; i >= 0; i--)
            { float ignored; if (!Qualified(receivers[i], out ignored)) receivers.RemoveAt(i); }
            int count = UnitRegistry.allUnits.Count;
            for (int i = 0; i < Math.Min(count, ResoluteOffensiveEcmPolicy.RegistryChecks); i++)
            {
                if (cursor >= count) cursor = 0;
                Unit candidate = UnitRegistry.allUnits[cursor++];
                if (candidate == null || candidate == ship || candidate.disabled || candidate.NetworkHQ == null ||
                    candidate.NetworkHQ == ship.NetworkHQ) continue;
                Receiver receiver = new Receiver { Unit = candidate, Radar = candidate.radar as Radar };
                if (receiver.Radar != null && receiver.Radar.GetAttachedUnit() != candidate) receiver.Radar = null;
                if (receiver.Radar == null)
                {
                    receiver.Missile = candidate as Missile;
                    if (receiver.Missile == null) continue;
                    MissileSeeker seeker = candidate.GetComponent<MissileSeeker>();
                    if (seeker == null) continue;
                    Type type = seeker.GetType();
                    if (type == typeof(ARHSeeker)) receiver.Arh = true;
                    else if (type != typeof(SARHSeeker)) continue;
                }
                float distance;
                if (!Qualified(receiver, out distance)) continue;
                bool already = false;
                for (int slot = 0; slot < receivers.Count; slot++) if (receivers[slot].Unit == candidate) { already = true; break; }
                if (already) continue;
                Transform receiverPoint = receiver.Radar != null ? receiver.Radar.GetScanPoint() : candidate.transform;
                if (receiverPoint == null || Physics.Linecast(emitter.position, receiverPoint.position, PhysicsLayers.StaticsMask)) continue;
                if (receivers.Count < ResoluteOffensiveEcmPolicy.ReceiverCapacity) { receivers.Add(receiver); continue; }
                int worst = -1; float farthest = distance;
                for (int slot = 0; slot < receivers.Count; slot++)
                {
                    float oldDistance;
                    if (Qualified(receivers[slot], out oldDistance) && oldDistance > farthest) { worst = slot; farthest = oldDistance; }
                }
                if (worst >= 0) receivers[worst] = receiver;
            }
        }

        private bool Qualified(Receiver receiver, out float distance)
        {
            distance = float.PositiveInfinity;
            Unit unit = receiver.Unit;
            if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ == ship.NetworkHQ) return false;
            if (receiver.Radar != null)
            {
                if (!receiver.Radar.IsOperational() || receiver.Radar.GetAttachedUnit() != unit || unit.radar != receiver.Radar) return false;
            }
            else if (receiver.Missile == null || receiver.Arh && receiver.Missile.seekerMode != Missile.SeekerMode.activeLock &&
                receiver.Missile.seekerMode != Missile.SeekerMode.activeSearch) return false;
            // This is physical reception inside an independently ordered beam,
            // not acquisition: unseen receivers can be affected, but no receiver
            // identity/position is returned to the ship, UI or native HQ tracks.
            // The ordered bearing never follows hidden target truth.
            return ResoluteOffensiveEcmPolicy.InSector(unit.GlobalPosition() - ship.GlobalPosition(),
                Point - ship.GlobalPosition(), out distance);
        }

        private void OnDisable() { Requested = false; pending.Clear(); receivers.Clear(); ReleaseTarget(); ReleaseTrack(); }
        private void OnDestroy() { ReleaseTarget(); ReleaseTrack(); }
    }
}
