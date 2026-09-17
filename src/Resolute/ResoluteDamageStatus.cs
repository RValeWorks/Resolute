using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Resolute
{
    public sealed class ResoluteDamageEntry
    {
        public int Id;
        public string Name, State;
        public float IntegrityPercent;
    }

    public sealed class ResoluteDamageSnapshot
    {
        public string ShipState;
        public ResoluteDamageEntry[] Parts = new ResoluteDamageEntry[0];
        public int DamagedCount, CriticalCount, LostCount;
    }

    // Read-only operating hook. The addon requests this only while its damage
    // panel is open. No polling component, repair orders or damage thresholds.
    public static class ResoluteDamageStatus
    {
        private sealed class Names { internal readonly Dictionary<int, string> ById = new Dictionary<int, string>(); }
        private static readonly ConditionalWeakTable<Ship, Names> Cache = new ConditionalWeakTable<Ship, Names>();

        public static ResoluteDamageSnapshot GetSnapshot(Ship ship)
        {
            var result = new ResoluteDamageSnapshot { ShipState = "Unavailable" };
            if (ship == null || !Plugin.IsResolute(ship.definition)) return result;
            FactionHQ local;
            if (GameManager.GetLocalHQ(out local) && ship.NetworkHQ != local) return result;
            result.ShipState = ship.disabled ? "Disabled / sinking" : "Active";
            var names = Cache.GetValue(ship, _ => new Names());
            var rows = new List<ResoluteDamageEntry>();
            // The native registry retains removed slots, unlike hierarchy scans
            // that silently drop destroyed or detached sections from the list.
            for (int i = 0; i < ship.damageables.Count; i++)
            {
                DamageablePart registered = ship.damageables[i];
                UnitPart part = registered.Damageable as UnitPart;
                if (part != null && !names.ById.ContainsKey(i)) names.ById[i] = DisplayName(part.name);
                string name;
                if (!names.ById.TryGetValue(i, out name)) name = "Removed section " + (i + 1);
                var row = new ResoluteDamageEntry { Id = i, Name = name, IntegrityPercent = float.NaN };
                if (registered.Removed) row.State = "Removed";
                else if (part == null || float.IsNaN(part.hitPoints) || float.IsInfinity(part.hitPoints)) row.State = "Unavailable";
                else
                {
                    // Native UnitPart.Awake initializes integrity to 100. Negative
                    // values are possible; 0 does not universally mean destroyed.
                    row.IntegrityPercent = Mathf.Clamp(part.hitPoints, 0f, 100f);
                    if (part.IsDetached()) row.State = "Detached";
                    else if (part is ShipPart compartment && compartment.IsCriticallyDamaged()) row.State = "Critical";
                    else if (part.hitPoints <= 0f) row.State = "Integrity depleted";
                    else if (part.hitPoints < 100f) row.State = "Damaged";
                    else row.State = "Healthy";
                }
                if (row.State == "Removed" || row.State == "Detached") result.LostCount++;
                else if (row.State == "Critical" || row.State == "Integrity depleted") result.CriticalCount++;
                else if (row.State == "Damaged") result.DamagedCount++;
                rows.Add(row);
            }
            result.Parts = rows.ToArray();
            return result;
        }

        private static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed section";
            switch (name)
            {
                case "RSL_Resolute": case "Destroyer1": return "Main hull";
                case "Hull_CF": return "Forward centre hull";
                case "Hull_CFF": return "Bow";
                case "Hull_FL": return "Forward port hull";
                case "Hull_FR": return "Forward starboard hull";
                case "Hull_CR": return "Aft centre hull";
                case "Hull_CRAft": return "Aft hull / hangar support";
                case "Hull_Bridge": return "Bridge structure";
                case "Hull_Radar": return "Radar mast structure";
                case "Hull_ExhaustStack": return "Port funnel structure";
                case "Hull_UpperStarboard": return "Starboard upper structure";
                case "turret_F": return "Railgun mount";
                case "CIWS_FR": return "Forward port CIWS";
                case "CIWS_FL": return "Forward starboard CIWS";
                case "CIWS_RR": return "Aft starboard CIWS";
                case "CIWS_RL": return "Aft port CIWS";
            }
            return Regex.Replace(name.Replace("Hull_", "Hull ").Replace("Resolute", "").Replace('_', ' '),
                "([a-z])([A-Z])", "$1 $2").Trim();
        }
    }
}
