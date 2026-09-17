using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Resolute
{
    // Delivery escorts use the same observed-threat gates as self-defense.
    // A relationship ends with the native carrier's payload/destination; it
    // never grants a detection, changes missile targets or orders the carrier.
    internal static class ResoluteSupplyDefense
    {
        private sealed class Evidence
        {
            internal Unit Attacker, Carrier;
            internal float SeenAt;
        }
        private sealed class Ledger { internal readonly List<Evidence> Entries = new List<Evidence>(); }
        private static readonly ConditionalWeakTable<Ship, Ledger> Ledgers = new ConditionalWeakTable<Ship, Ledger>();

        private static bool ObservedEnemy(Ship ship, Unit unit)
        {
            if (!ResoluteReplenishment.Live(ship) || unit == null || unit.disabled || unit.NetworkHQ == null ||
                unit.NetworkHQ == ship.NetworkHQ) return false;
            TrackingInfo track = ship.NetworkHQ.GetTrackingData(unit.persistentID);
            return track != null && track.Observed() && Time.timeSinceLevelLoad >= track.lastSpottedTime;
        }

        internal static void ReportAttack(Unit attacker, Unit victim)
        {
            Ship ship;
            if (!ResoluteReplenishment.TryGetSupplyDestination(victim, out ship) || !ObservedEnemy(ship, attacker)) return;
            Ledger ledger = Ledgers.GetValue(ship, _ => new Ledger());
            float now = Time.timeSinceLevelLoad;
            for (int i = ledger.Entries.Count - 1; i >= 0; i--)
            {
                Evidence entry = ledger.Entries[i];
                if (!Current(ship, entry, now)) { ledger.Entries.RemoveAt(i); continue; }
                if (entry.Attacker == attacker && entry.Carrier == victim) { entry.SeenAt = now; return; }
            }
            if (ledger.Entries.Count < 128) ledger.Entries.Add(new Evidence { Attacker = attacker, Carrier = victim, SeenAt = now });
        }

        private static bool Current(Ship ship, Evidence entry, float now) => entry.Attacker != null && !entry.Attacker.disabled &&
            now >= entry.SeenAt && now - entry.SeenAt < 30f && ResoluteReplenishment.IsPendingSupplyAircraft(ship, entry.Carrier);

        internal static bool IsAttacker(Ship ship, Unit unit)
        {
            if (!ObservedEnemy(ship, unit) || !Ledgers.TryGetValue(ship, out Ledger ledger)) return false;
            float now = Time.timeSinceLevelLoad;
            for (int i = ledger.Entries.Count - 1; i >= 0; i--)
            {
                Evidence entry = ledger.Entries[i];
                if (!Current(ship, entry, now)) { ledger.Entries.RemoveAt(i); continue; }
                if (entry.Attacker == unit) return true;
            }
            return false;
        }

        internal static bool IsIncoming(Missile missile, Ship ship)
        {
            if (!ObservedEnemy(ship, missile) || missile.targetID.NotValid ||
                !UnitRegistry.TryGetUnit(missile.targetID, out Unit carrier) ||
                !ResoluteReplenishment.IsPendingSupplyAircraft(ship, carrier)) return false;
            ReportAttack(missile.owner, carrier);
            return true;
        }

        // Shared by the existing exact-layout native CombatAI / defensive
        // turret predicate patches. All other native suitability checks stay.
        internal static bool IsTargetOrObservedIncoming(Missile missile, Unit defender) =>
            NaturalPikeObservedThreat.IsTargetOrObservedIncoming(missile, defender) ||
            ResoluteLancePosition.IsObservedIncoming(missile, defender) || IsIncoming(missile, defender as Ship);
        internal static bool IsNotTargetOrObservedIncoming(Missile missile, Unit defender) =>
            !IsTargetOrObservedIncoming(missile, defender);
    }
}
