using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal sealed class ResoluteAmmoType
    {
        internal WeaponInfo Info;
        internal long Current, Capacity;
        internal double Fraction => Capacity > 0 ? Math.Min(1d, Math.Max(0d, (double)Current / Capacity)) : 1d;
    }

    // Compare fractions per ammunition type, not numbers of shells vs missiles.
    // Storage already in a magazine or being loaded is counted by GetAmmoTotal.
    internal sealed class ResoluteAmmoInventory
    {
        private readonly Dictionary<WeaponInfo, ResoluteAmmoType> byType = new Dictionary<WeaponInfo, ResoluteAmmoType>();
        private readonly HashSet<Weapon> seen = new HashSet<Weapon>();
        private readonly List<double> fractions = new List<double>();
        internal readonly List<ResoluteAmmoType> Types = new List<ResoluteAmmoType>();
        internal bool HasDeliverableDeficit { get; private set; }
        internal bool NeedsReplenishment { get; private set; }
        internal double Minimum { get; private set; }
        internal double Median { get; private set; }

        internal void Refresh(Ship ship)
        {
            byType.Clear(); seen.Clear(); Types.Clear(); fractions.Clear();
            HasDeliverableDeficit = false;
            if (ship != null && ship.weaponStations != null)
                foreach (WeaponStation station in ship.weaponStations)
                {
                    if (station == null || station.Weapons == null) continue;
                    foreach (Weapon weapon in station.Weapons)
                    {
                        if (!FiniteAmmo(weapon) || !seen.Add(weapon)) continue;
                        int capacity = Math.Max(0, weapon.GetFullAmmo());
                        if (capacity == 0) continue;
                        int current = Math.Min(capacity, Math.Max(0, weapon.GetAmmoTotal()));
                        ResoluteAmmoType row;
                        if (!byType.TryGetValue(weapon.info, out row))
                        {
                            row = new ResoluteAmmoType { Info = weapon.info };
                            byType.Add(weapon.info, row); Types.Add(row);
                        }
                        row.Current += current; row.Capacity += capacity;
                        // Native naval cargo cannot generate nuclear warheads.
                        if (!weapon.info.nuclear && current < capacity) HasDeliverableDeficit = true;
                    }
                }
            foreach (ResoluteAmmoType row in Types) fractions.Add(row.Fraction);
            fractions.Sort();
            Minimum = fractions.Count == 0 ? 1d : fractions[0];
            int middle = fractions.Count / 2;
            Median = fractions.Count == 0 ? 1d : fractions.Count % 2 != 0
                ? fractions[middle] : (fractions[middle - 1] + fractions[middle]) * .5d;
            NeedsReplenishment = HasDeliverableDeficit && (Minimum < 1d / 3d || Median < .5d);
        }

        internal static bool FiniteAmmo(Weapon weapon)
        {
            if (weapon == null || weapon is Laser || !weapon.Rearmable || !weapon.IsAttached() ||
                weapon.info == null || weapon.info.cargo || !(weapon.info.massPerRound > 0f) ||
                float.IsInfinity(weapon.info.massPerRound)) return false;
            UnitPart part = weapon.GetComponentInParent<UnitPart>();
            return part == null || !part.IsDetached() && part.hitPoints > 0f;
        }

        internal static void SynchronizeNativeCapacity(Ship ship)
        {
            if (ship == null || !Plugin.IsResolute(ship.definition)) return;
            foreach (WeaponStation station in ship.weaponStations)
            {
                long capacity = 0;
                foreach (Weapon weapon in station.Weapons)
                    if (weapon != null) capacity += Math.Max(0, weapon.GetFullAmmo());
                station.FullAmmo = (int)Math.Min(int.MaxValue, capacity);
            }
        }
    }

    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    internal static class ResoluteRearmCapacity
    {
        private static void Prefix(Unit unitToRearm)
        {
            Ship ship = unitToRearm as Ship;
            if (ship != null && ship.IsServer && Plugin.IsResolute(ship.definition))
                ResoluteAmmoInventory.SynchronizeNativeCapacity(ship);
        }
    }

    // Native rearming treats a negative FloorToInt result as unlimited stock.
    // Float exhaustion can enter that sentinel path at an ordinary finite crate.
    [HarmonyPatch(typeof(Rearmer), nameof(Rearmer.ProcessRearmRequest))]
    internal static class ResoluteRearmBudget
    {
        internal static bool PatchActive { get; private set; }
        private static bool warned;
        private static bool Scoped(Unit unit) => unit is Ship && Plugin.IsResolute(unit.definition);

        internal static int MassRounds(float capacity, float mass, Unit unit)
        {
            if (!Scoped(unit)) return Mathf.FloorToInt(capacity / mass);
            if (!(capacity > 0f) || !(mass > 0f) || float.IsInfinity(mass)) return 0;
            double rounds = Math.Floor((double)capacity / mass);
            return rounds >= int.MaxValue ? int.MaxValue : (int)rounds;
        }
        internal static float SpendMass(float capacity, int rounds, float mass, Unit unit)
        {
            // Keep native multiply/subtract together: passing their intermediate
            // product as a float would change Mono rounding for other ships.
            float remaining = capacity - (float)rounds * mass;
            return Scoped(unit) ? Math.Max(0f, remaining) : remaining;
        }
        internal static int Shortfall(int previous, int deficit, int massLimit, int warheads, int funds, Unit unit)
        {
            if (!Scoped(unit)) return unchecked(previous + (deficit - massLimit));
            int delivered = Math.Max(0, Math.Min(Math.Min(deficit, massLimit), Math.Min(warheads, funds)));
            long remaining = Math.Max(0L, (long)deficit - delivered);
            return (int)Math.Min(int.MaxValue, Math.Max(0L, previous) + remaining);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo floor = AccessTools.Method(typeof(Mathf), nameof(Mathf.FloorToInt), new[] { typeof(float) });
            MethodInfo minimum = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(int[]) });
            int floorAt = -1, floors = 0, spendAt = -1, spends = 0, shortAt = -1, shorts = 0;
            int capacity = -1, mass = -1, available = -1, deficit = -1, station = -1, given = -1;
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Call && Equals(code[i].operand, floor))
                {
                    floors++;
                    if (floorAt < 0) floorAt = i;
                }
            bool valid = floors == 2 && floorAt >= 9 && floorAt + 7 < code.Count;
            if (valid)
            {
                int i = floorAt;
                capacity = Local(code[i - 3], false); mass = Local(code[i - 2], false);
                available = Local(code[i + 1], true); deficit = Local(code[i - 4], true); station = Local(code[i - 9], false);
                valid = capacity >= 0 && mass >= 0 && available >= 0 && deficit >= 0 && station >= 0 &&
                    code[i - 1].opcode == OpCodes.Div && code[i - 5].opcode == OpCodes.Sub &&
                    Field(code[i - 8], typeof(WeaponStation), nameof(WeaponStation.FullAmmo)) && Local(code[i - 7], false) == station &&
                    Equals(code[i - 6].operand, AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.GetAmmoTotal))) &&
                    Local(code[i + 2], false) == available && code[i + 3].opcode == OpCodes.Ldc_I4_0 &&
                    (code[i + 4].opcode == OpCodes.Bge || code[i + 4].opcode == OpCodes.Bge_S) &&
                    code[i + 5].opcode == OpCodes.Ldc_I4 && Equals(code[i + 5].operand, int.MaxValue) && Local(code[i + 6], true) == available &&
                    SourceLocal(code, capacity, typeof(Rearmer), nameof(Rearmer.Capacity)) && SourceLocal(code, mass, typeof(WeaponInfo), nameof(WeaponInfo.massPerRound));
            }
            if (valid)
                for (int i = 2; i + 27 < code.Count; i++)
                    if (code[i].opcode == OpCodes.Ldarg_2 && code[i + 1].opcode == OpCodes.Ldarg_2 && code[i + 2].opcode == OpCodes.Ldind_I4 &&
                        Local(code[i + 3], false) == deficit && Local(code[i + 4], false) >= 0 && code[i + 5].opcode == OpCodes.Sub &&
                        code[i + 6].opcode == OpCodes.Add && code[i + 7].opcode == OpCodes.Stind_I4 &&
                        code[i + 8].opcode == OpCodes.Ldc_I4_4 && code[i + 9].opcode == OpCodes.Newarr && Equals(code[i + 9].operand, typeof(int)) &&
                        code[i + 26].opcode == OpCodes.Call && Equals(code[i + 26].operand, minimum))
                    {
                        int candidate = Local(code[i + 4], false);
                        if (Local(code[i - 2], false) != available || Local(code[i - 1], true) != candidate ||
                            Local(code[i + 12], false) != candidate || Local(code[i + 16], false) != deficit ||
                            Local(code[i + 20], false) < 0 || Local(code[i + 24], false) < 0 || Local(code[i + 27], true) != candidate) continue;
                        OpCode[] arrayOps = { OpCodes.Dup, OpCodes.Ldc_I4_0, OpCodes.Ldloc_S, OpCodes.Stelem_I4,
                            OpCodes.Dup, OpCodes.Ldc_I4_1, OpCodes.Ldloc_S, OpCodes.Stelem_I4,
                            OpCodes.Dup, OpCodes.Ldc_I4_2, OpCodes.Ldloc_S, OpCodes.Stelem_I4,
                            OpCodes.Dup, OpCodes.Ldc_I4_3, OpCodes.Ldloc_S, OpCodes.Stelem_I4 };
                        bool arrayMatch = true;
                        for (int j = 0; j < arrayOps.Length; j++)
                            if (j % 4 != 2 && code[i + 10 + j].opcode != arrayOps[j]) arrayMatch = false;
                        if (!arrayMatch) continue;
                        shortAt = i; given = candidate; shorts++;
                    }
            if (valid && shorts == 1)
                for (int i = 0; i + 6 < code.Count; i++)
                    if (Local(code[i], false) == capacity && Local(code[i + 1], false) == given && code[i + 2].opcode == OpCodes.Conv_R4 &&
                        Local(code[i + 3], false) == mass && code[i + 4].opcode == OpCodes.Mul && code[i + 5].opcode == OpCodes.Sub &&
                        Local(code[i + 6], true) == capacity) { spendAt = i + 5; spends++; }
            valid &= shorts == 1 && spends == 1;
            foreach (CodeInstruction instruction in code) if (instruction.blocks.Count != 0) valid = false;
            if (valid)
                foreach (int index in new[] { floorAt - 1, floorAt, shortAt + 5, shortAt + 6, spendAt - 3, spendAt - 1, spendAt })
                    if (code[index].labels.Count != 0) valid = false;
            PatchActive = valid;
            if (!valid)
            {
                if (!warned) { warned = true; Plugin.Instance?.LogStartupWarning("Resolute supply budget correction unavailable: native Rearmer layout changed. Original rearming retained."); }
                return code;
            }
            var result = new List<CodeInstruction>(code.Count + 3);
            for (int i = 0; i < code.Count; i++)
            {
                if (i == floorAt - 1) result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                else if (i == floorAt) result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResoluteRearmBudget), nameof(MassRounds))));
                else if (i == shortAt + 5)
                {
                    result.Add(new CodeInstruction(code[shortAt + 20].opcode, code[shortAt + 20].operand));
                    result.Add(new CodeInstruction(code[shortAt + 24].opcode, code[shortAt + 24].operand));
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResoluteRearmBudget), nameof(Shortfall))));
                }
                else if (i == shortAt + 6) result.Add(new CodeInstruction(OpCodes.Nop));
                else if (i == spendAt - 3) result.Add(new CodeInstruction(OpCodes.Nop));
                else if (i == spendAt - 1) result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                else if (i == spendAt) result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ResoluteRearmBudget), nameof(SpendMass))));
                else result.Add(code[i]);
            }
            return result;
        }
        private static bool SourceLocal(List<CodeInstruction> code, int local, Type owner, string field)
        {
            int count = 0;
            for (int i = 1; i < code.Count; i++)
                if (Local(code[i], true) == local && Field(code[i - 1], owner, field)) count++;
            return count == 1;
        }
        private static bool Field(CodeInstruction instruction, Type owner, string name) => instruction.opcode == OpCodes.Ldfld &&
            Equals(instruction.operand, AccessTools.Field(owner, name));
        private static int Local(CodeInstruction instruction, bool store)
        {
            OpCode op = instruction.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (op != (store ? OpCodes.Stloc : OpCodes.Ldloc) && op != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            if (instruction.operand is LocalBuilder built) return built.LocalIndex;
            if (instruction.operand is LocalVariableInfo info) return info.LocalIndex;
            if (instruction.operand is byte small) return small;
            if (instruction.operand is int index) return index;
            return -1;
        }
    }
}
