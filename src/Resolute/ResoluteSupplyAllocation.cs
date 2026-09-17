using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // A pallet is finite mass, not a separate stockpile for every weapon. Reserve
    // 80% for missiles when both categories need ammunition, then reuse leftovers.
    // Equalize fill fractions within each category rather than filling the first
    // station in the ship's native list. Native RPC indices never change.
    internal static class ResoluteSupplyAllocation
    {
        internal sealed class Demand
        {
            internal long Current, Capacity, Granted;
            internal double Mass;
            internal bool Missile;
            internal double Fraction => Capacity > 0 ? (double)(Current + Granted) / Capacity : 1d;
        }

        internal static void Allocate(List<Demand> rows, double budget)
        {
            if (!(budget > 0d) || double.IsInfinity(budget) || double.IsNaN(budget)) return;
            bool missiles = rows.Exists(r => r.Missile && Missing(r) > 0);
            bool guns = rows.Exists(r => !r.Missile && Missing(r) > 0);
            double missileBudget = missiles && guns ? budget * .8d : missiles ? budget : 0d;
            double spent = Fill(rows, true, missileBudget);
            spent += Fill(rows, false, guns ? budget - missileBudget : 0d);
            spent += Fill(rows, true, Math.Max(0d, budget - spent));
            Fill(rows, false, Math.Max(0d, budget - spent));
        }

        private static long Missing(Demand row) => row.Mass > 0d && !double.IsInfinity(row.Mass) && row.Capacity > 0
            ? Math.Max(0L, row.Capacity - row.Current - row.Granted) : 0L;

        private static double Fill(List<Demand> rows, bool missile, double budget)
        {
            if (!(budget > 0d)) return 0d;
            double low = 0d, high = 1d;
            // Small, bounded workload: at most the ship's ammunition families.
            for (int n = 0; n < 48; n++)
            {
                double level = (low + high) * .5d, mass = 0d;
                foreach (Demand row in rows)
                    if (row.Missile == missile && Missing(row) > 0)
                        mass += Math.Max(0d, level * row.Capacity - row.Current - row.Granted) * row.Mass;
                if (mass <= budget) low = level; else high = level;
            }
            double remaining = budget;
            foreach (Demand row in rows)
            {
                if (row.Missile != missile || Missing(row) == 0) continue;
                long count = Math.Min(Missing(row), (long)Math.Max(0d, Math.Floor(low * row.Capacity - row.Current - row.Granted)));
                count = Math.Min(count, (long)Math.Min(long.MaxValue, Math.Floor(remaining / row.Mass)));
                row.Granted += count; remaining = Math.Max(0d, remaining - count * row.Mass);
            }
            var remainder = rows.FindAll(r => r.Missile == missile && Missing(r) > 0);
            remainder.Sort((a, b) => a.Fraction.CompareTo(b.Fraction));
            foreach (Demand row in remainder)
                if (row.Mass <= remaining) { row.Granted++; remaining -= row.Mass; }
            return budget - remaining;
        }
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    internal static class ResoluteRearmAllocation
    {
        internal static bool PatchActive { get; private set; }
        internal sealed class Scope
        {
            internal Unit Unit;
            internal readonly Dictionary<WeaponStation, int> Limits = new Dictionary<WeaponStation, int>();
        }
        [ThreadStatic] private static Scope current;
        private sealed class Family
        {
            internal readonly ResoluteSupplyAllocation.Demand Demand = new ResoluteSupplyAllocation.Demand();
            internal readonly List<WeaponStation> Stations = new List<WeaponStation>();
        }

        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Rearmer __instance, Unit unitToRearm, out Scope __state)
        {
            __state = current;
            current = null;
            if (!(unitToRearm is Ship ship) || !ship.IsServer || !Plugin.IsResolute(ship.definition) ||
                !IsNavalSupply(__instance) || !(__instance.Capacity > 0f) || float.IsInfinity(__instance.Capacity)) return;
            ResoluteAmmoInventory.SynchronizeNativeCapacity(ship);
            var scope = new Scope { Unit = ship };
            var families = new Dictionary<WeaponInfo, Family>();
            foreach (WeaponStation station in ship.weaponStations)
            {
                WeaponInfo info = station?.WeaponInfo;
                if (info == null || info.cargo || info.nuclear || !(info.massPerRound > 0f) || float.IsInfinity(info.massPerRound)) continue;
                if (!families.TryGetValue(info, out Family family))
                {
                    family = new Family(); families.Add(info, family);
                    family.Demand.Mass = info.massPerRound;
                    // A launcher is a missile category; guns and their magazines
                    // share the reserve for shells. Laser stations have zero mass.
                    family.Demand.Missile = station.Weapons.Exists(w => w is MissileLauncher);
                }
                long capacity = Math.Max(0, station.FullAmmo);
                family.Demand.Capacity += capacity;
                family.Demand.Current += Math.Min(capacity, Math.Max(0, station.GetAmmoTotal()));
                family.Stations.Add(station);
            }
            var demands = new List<ResoluteSupplyAllocation.Demand>();
            foreach (Family family in families.Values) demands.Add(family.Demand);
            ResoluteSupplyAllocation.Allocate(demands, __instance.Capacity);
            foreach (Family family in families.Values)
            {
                // Split each family's share among its banks by missing rounds.
                long missing = family.Demand.Capacity - family.Demand.Current, remaining = family.Demand.Granted;
                foreach (WeaponStation station in family.Stations)
                {
                    int deficit = Math.Max(0, station.FullAmmo - station.GetAmmoTotal());
                    int given = missing > 0 ? (int)Math.Min(deficit, remaining * deficit / missing) : 0;
                    scope.Limits[station] = given; remaining -= given; missing -= deficit;
                }
            }
            current = scope;
        }

        internal static int Limit(int nativeLimit, Unit unit, WeaponStation station)
            => current != null && current.Unit == unit && current.Limits.TryGetValue(station, out int limit)
                ? Math.Min(nativeLimit, limit) : nativeLimit;

        [HarmonyPriority(Priority.Last)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var total = AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.GetAmmoTotal));
            var mass = AccessTools.Method(typeof(ResoluteRearmBudget), nameof(ResoluteRearmBudget.MassRounds));
            CodeInstruction stationLoad = null;
            int totals = 0, limits = 0;
            for (int i = 1; i < code.Count; i++)
            {
                if (Equals(code[i].operand, total)) { totals++; stationLoad = code[i - 1]; }
                if (Equals(code[i].operand, mass)) limits++;
            }
            PatchActive = totals == 1 && limits == 1 && stationLoad != null && stationLoad.opcode.Name.StartsWith("ldloc", StringComparison.Ordinal);
            if (!PatchActive)
            {
                Plugin.Instance?.LogStartupWarning("Resolute supply allocation unavailable: native station budget layout changed.");
                return code;
            }
            var result = new List<CodeInstruction>(code.Count + 3);
            foreach (var instruction in code)
            {
                result.Add(instruction);
                if (!Equals(instruction.operand, mass)) continue;
                result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                result.Add(new CodeInstruction(stationLoad.opcode, stationLoad.operand));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResoluteRearmAllocation), nameof(Limit))));
            }
            return result;
        }

        internal static bool IsNavalSupply(Rearmer rearmer)
            => rearmer?.Unit?.definition?.jsonKey != null &&
               (rearmer.Unit.definition.jsonKey.StartsWith("NavalSupply", StringComparison.Ordinal) ||
                // Ibis's native 6000 kg pallet uses a separate definition family.
                string.Equals(rearmer.Unit.definition.jsonKey, "NavalPallet1", StringComparison.Ordinal));

        private static Exception Finalizer(Exception __exception, Scope __state)
        { current = __state; return __exception; }
    }

    [HarmonyPatch(typeof(RearmMissionController), nameof(RearmMissionController.TryGetRearmer))]
    internal static class ResoluteSupplyPickup
    {
        internal static bool PatchActive { get; private set; }
        internal static float Range(Rearmer rearmer, Unit requester)
            => requester is Ship && Plugin.IsResolute(requester.definition) &&
                ResoluteRearmAllocation.IsNavalSupply(rearmer) ? 400f : rearmer.Range;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var range = AccessTools.Field(typeof(Rearmer), nameof(Rearmer.Range));
            int count = 0;
            foreach (var instruction in code) if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, range)) count++;
            PatchActive = count == 1;
            if (!PatchActive)
            {
                Plugin.Instance?.LogStartupWarning("Resolute supply pickup extension unavailable: native range check changed.");
                return code;
            }
            var result = new List<CodeInstruction>(code.Count + 1);
            foreach (var instruction in code)
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, range))
                {
                    var requester = new CodeInstruction(OpCodes.Ldarg_1);
                    requester.labels.AddRange(instruction.labels); requester.blocks.AddRange(instruction.blocks);
                    result.Add(requester);
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResoluteSupplyPickup), nameof(Range))));
                }
                else result.Add(instruction);
            return result;
        }
    }
}
