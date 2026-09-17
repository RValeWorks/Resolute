using System;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Measurement and an opaque shared track number only: no Unit, radar reference,
    // exact position or fire-control track escapes. Estimates allow area orders.
    public sealed class ResoluteEsmContact
    {
        public uint Id;
        public string Type;
        public GlobalPosition Position;
        public float AgeSeconds, RadialUncertaintyMetres, CrossRangeUncertaintyMetres, BearingDegrees;
        public bool Stale;
    }

    public static class ResoluteEsm
    {
        private static readonly ResoluteEsmContact[] Empty = new ResoluteEsmContact[0];

        internal static void Configure(Ship ship)
        {
            if (ship != null && Plugin.IsResolute(ship.definition) && ship.GetComponent<ResoluteEsmReceiver>() == null)
                ship.gameObject.AddComponent<ResoluteEsmReceiver>();
        }

        public static ResoluteEsmContact[] GetContacts(Ship ship)
        {
            string ignored;
            if (!ResoluteNavigationCommands.CanCommand(ship, out ignored)) return Empty;
            var receiver = ship.GetComponent<ResoluteEsmReceiver>();
            return receiver != null ? receiver.CopyContacts() : Empty;
        }
    }

    internal static class ResoluteEsmPolicy
    {
        internal const float MaximumRange = 200000f, ScanSeconds = 1f, StaleSeconds = 5f, ExpireSeconds = 120f;
        internal const int RegistryBudget = 64, Capacity = 32;
        internal const float RadialFraction = .20f, MinimumRadial = 2500f, AngularErrorDegrees = 2.5f;

        internal static float DetectionRange(float nativeRadarRange)
            => !ResoluteOffensiveEcmPolicy.Finite(nativeRadarRange) || nativeRadarRange <= 0f ? 0f :
                Mathf.Min(MaximumRange, nativeRadarRange * 2f);

        // Stable, receiver/emitter-specific calibration bias. Repeated scans of
        // a stationary situation do not jitter around the true point and allow
        // averaging into a precise fire-control solution.
        internal static float Bias(uint receiverId, uint emitterId, uint channel)
        {
            unchecked
            {
                uint value = receiverId * 747796405u ^ emitterId * 2891336453u ^ channel * 277803737u;
                value ^= value >> 16; value *= 2246822519u; value ^= value >> 13;
                value *= 3266489917u; value ^= value >> 16;
                return ((value & 0x00ffffffu) / 16777215f * 2f - 1f) * .65f;
            }
        }

        internal static GlobalPosition Estimate(GlobalPosition origin, GlobalPosition emitter, float radialBias,
            float angularBias, out float radial, out float crossRange, out float bearing)
        {
            Vector3 offset = emitter - origin; offset.y = 0f;
            float distance = offset.magnitude;
            radial = Mathf.Max(MinimumRadial, distance * RadialFraction);
            crossRange = Mathf.Max(250f, distance * Mathf.Tan(AngularErrorDegrees * Mathf.Deg2Rad));
            float angle = Mathf.Atan2(offset.x, offset.z) + angularBias * AngularErrorDegrees * Mathf.Deg2Rad;
            bearing = (angle * Mathf.Rad2Deg + 360f) % 360f;
            float range = Mathf.Max(1f, distance + radialBias * radial);
            var point = origin + new Vector3(Mathf.Sin(angle) * range, 0f, Mathf.Cos(angle) * range);
            point.y = Datum.SeaLevel.y;
            return point;
        }
    }

    internal sealed class ResoluteEsmReceiver : MonoBehaviour
    {
        private sealed class Contact
        {
            internal Unit Emitter;
            internal Radar Radar;
            internal uint Id;
            internal string Type;
            internal GlobalPosition Position;
            internal float Radial, CrossRange, Bearing, LastHeard, MovementAllowance;
        }
        private readonly List<Contact> contacts = new List<Contact>(ResoluteEsmPolicy.Capacity);
        private Ship ship;
        private ResoluteShipEcm hardware;
        private FactionHQ faction;
        private Transform receiver;
        private int cursor;
        private float nextScan;

        private void Awake()
        { ship = GetComponent<Ship>(); hardware = GetComponent<ResoluteShipEcm>(); }

        private void Update()
        {
            if (ship == null || !ship.IsServer || !ship.LocalSim || !MissionManager.IsRunning) return;
            if (faction != ship.NetworkHQ)
            { contacts.Clear(); faction = ship.NetworkHQ; }
            float now = Time.timeSinceLevelLoad;
            if (now < nextScan) return;
            nextScan = now + ResoluteEsmPolicy.ScanSeconds;
            bool operational = faction != null && hardware != null && hardware.TryGetEmitter(out receiver);
            for (int i = contacts.Count - 1; i >= 0; i--)
            {
                Contact contact = contacts[i];
                if (operational && CanHear(contact.Emitter, contact.Radar)) Refresh(contact, now);
                if (now - contact.LastHeard > ResoluteEsmPolicy.ExpireSeconds) contacts.RemoveAt(i);
            }
            if (!operational) return;
            int count = UnitRegistry.allUnits.Count;
            for (int checkedUnits = 0; checkedUnits < Math.Min(count, ResoluteEsmPolicy.RegistryBudget); checkedUnits++)
            {
                if (cursor >= count) cursor = 0;
                Unit candidate = UnitRegistry.allUnits[cursor++];
                if (candidate == null || candidate == ship || candidate.disabled || candidate.NetworkHQ == null ||
                    candidate.NetworkHQ == faction) continue;
                bool existing = false;
                for (int i = 0; i < contacts.Count; i++) if (contacts[i].Emitter == candidate) { existing = true; break; }
                if (existing) continue;
                Radar radar = candidate.radar as Radar;
                if (!CanHear(candidate, radar)) continue;
                if (contacts.Count >= ResoluteEsmPolicy.Capacity)
                {
                    // Only replace a stale estimate. Active emitters keep their
                    // stable measurement identity/bias instead of thrashing.
                    int oldest = -1; float oldestTime = now - ResoluteEsmPolicy.StaleSeconds;
                    for (int i = 0; i < contacts.Count; i++)
                        if (contacts[i].LastHeard < oldestTime) { oldest = i; oldestTime = contacts[i].LastHeard; }
                    if (oldest < 0) continue;
                    contacts.RemoveAt(oldest);
                }
                Contact added = new Contact { Emitter = candidate, Radar = radar,
                    Id = ResoluteContactIdentity.ForFaction(faction, candidate.persistentID) };
                // Coarse emitter-family classification is an intentional game
                // abstraction; it does not expose a hull class or exact model.
                if (candidate is Ship) { added.Type = "Surface radar emitter"; added.MovementAllowance = 30f; }
                else if (candidate is Aircraft) { added.Type = "Airborne radar emitter"; added.MovementAllowance = 450f; }
                else { added.Type = "Land radar emitter"; added.MovementAllowance = 20f; }
                Refresh(added, now); contacts.Add(added);
            }
        }

        private bool CanHear(Unit emitter, Radar radar)
        {
            if (emitter == null || emitter == ship || emitter.disabled || emitter.NetworkHQ == null ||
                emitter.NetworkHQ == faction || radar == null || !radar.activated || !radar.IsOperational() ||
                radar.GetAttachedUnit() != emitter || emitter.radar != radar) return false;
            Transform point = radar.GetScanPoint();
            if (point == null) return false;
            float range = ResoluteEsmPolicy.DetectionRange(radar.GetRadarRange());
            float distance = (point.position - receiver.position).magnitude;
            if (!ResoluteOffensiveEcmPolicy.Finite(distance) || distance < 1f || distance > range) return false;
            // Native static terrain/building obstruction remains. There is no
            // invented radar-horizon test; a passively heard emitter need not be
            // a normal radar/datalink track. No CmdUpdateTrackingInfo is sent.
            return !Physics.Linecast(receiver.position, point.position, PhysicsLayers.StaticsMask);
        }

        private void Refresh(Contact contact, float now)
        {
            contact.Position = ResoluteEsmPolicy.Estimate(ship.GlobalPosition(), contact.Emitter.GlobalPosition(),
                ResoluteEsmPolicy.Bias(ship.persistentID.Id, contact.Emitter.persistentID.Id, 1),
                ResoluteEsmPolicy.Bias(ship.persistentID.Id, contact.Emitter.persistentID.Id, 2),
                out contact.Radial, out contact.CrossRange, out contact.Bearing);
            contact.LastHeard = now;
        }

        internal ResoluteEsmContact[] CopyContacts()
        {
            // A faction transfer may precede our next scheduled scan. Never
            // expose the old faction's cached observations in that interval.
            if (ship == null || faction != ship.NetworkHQ) return new ResoluteEsmContact[0];
            float now = Time.timeSinceLevelLoad;
            var result = new List<ResoluteEsmContact>(contacts.Count);
            foreach (Contact contact in contacts)
            {
                float age = Mathf.Max(0f, now - contact.LastHeard);
                if (age > ResoluteEsmPolicy.ExpireSeconds) continue;
                // Keep measuring underneath a fresh radar/datalink track, but expose only
                // one selectable symbol. A stale normal track does not hide an ESM estimate.
                if (contact.Emitter != null && faction.GetTrackingData(contact.Emitter.persistentID)?.Observed() == true) continue;
                float growth = Mathf.Max(0f, age - ResoluteEsmPolicy.StaleSeconds) * contact.MovementAllowance;
                result.Add(new ResoluteEsmContact { Id = contact.Emitter != null ?
                    ResoluteContactIdentity.ForFaction(faction, contact.Emitter.persistentID) : contact.Id,
                    Type = contact.Type, Position = contact.Position,
                    AgeSeconds = age, Stale = age > ResoluteEsmPolicy.StaleSeconds,
                    RadialUncertaintyMetres = contact.Radial + growth,
                    CrossRangeUncertaintyMetres = contact.CrossRange + growth, BearingDegrees = contact.Bearing });
            }
            return result.ToArray();
        }
        private void OnDestroy() { contacts.Clear(); }
    }
}
