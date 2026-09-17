using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    [HarmonyPatch(typeof(CombatAI), nameof(CombatAI.AnalyzeTarget))]
    internal static class ResoluteCommandOpportunity
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(WeaponStation weaponStation, Unit analyzer, TrackingInfo trackingInfo, ref OpportunityThreat __result)
        {
            Unit target;
            if (trackingInfo != null && trackingInfo.TryGetUnit(out target) &&
                !ResoluteCommandApi.AllowAutomatic(analyzer, weaponStation, target)) __result = new OpportunityThreat(0f, 0f);
        }
    }
    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.Fire))]
    internal static class ResoluteCommandStationGate
    {
        private static bool Prefix(WeaponStation __instance, Unit owner, Unit target) =>
            ResoluteCommandApi.IsManualFire(owner, __instance, target) || ResoluteCommandApi.AllowAutomatic(owner, __instance, target);
    }
    [HarmonyPatch]
    internal static class ResoluteCommandQueueGate
    {
        private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.Inner(typeof(FireControl), "QueuedAttack"), "StillValid");
        private static void Postfix(WeaponStation ___weaponStation, Unit ___target, ref bool __result)
        {
            if (!__result || ___weaponStation == null) return;
            Unit owner = ___weaponStation.Weapons.FirstOrDefault(w => w != null)?.attachedUnit;
            if (!ResoluteCommandApi.AllowAutomatic(owner, ___weaponStation, ___target)) __result = false;
        }
    }
    [HarmonyPatch(typeof(Gun), nameof(Gun.Fire))]
    internal static class ResoluteCommandGunGate
    {
        private static bool Prefix(Gun __instance, Unit firingUnit, Unit target, WeaponStation weaponStation)
        {
            if (!ResoluteCommandApi.IsManualFire(firingUnit, weaponStation, target) &&
                !ResoluteCommandApi.AllowAutomatic(firingUnit, weaponStation, target)) return false;
            if (__instance.ammo > 0) ResoluteCommandState.ReportAttack(firingUnit, target);
            return true;
        }
    }
    [HarmonyPatch(typeof(Laser), nameof(Laser.Fire))]
    internal static class ResoluteCommandLaserGate
    {
        private static bool Prefix(Unit owner, Unit target, WeaponStation weaponStation)
        {
            if (!ResoluteCommandApi.IsManualFire(owner, weaponStation, target) &&
                !ResoluteCommandApi.AllowAutomatic(owner, weaponStation, target)) return false;
            ResoluteCommandState.ReportAttack(owner, target);
            return true;
        }
    }
    [HarmonyPatch(typeof(Laser), "FixedUpdate")]
    internal static class ResoluteCommandLaserCeaseGate
    {
        private static readonly FieldInfo Station = AccessTools.Field(typeof(Weapon), "weaponStation");
        private static void Prefix(Laser __instance, ref bool ___fireCommanded)
        {
            if (ResoluteCommandState.Find(__instance.attachedUnit as Ship) == null) return;
            if (ResoluteCommandState.OwnsManualWeapon(__instance)) return;
            Turret turret = __instance.GetComponentInParent<Turret>();
            WeaponStation station = turret != null ? turret.GetWeaponStation() : (WeaponStation)Station.GetValue(__instance);
            if (!ResoluteCommandApi.AllowAutomatic(__instance.attachedUnit, station, __instance.GetTarget())) ___fireCommanded = false;
        }
    }
    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class ResoluteCommandLauncherEvidence
    {
        private static bool Prefix(MissileLauncher __instance, Unit owner, Unit target, WeaponStation weaponStation, out int __state)
        {
            __state = __instance.ammo;
            return ResoluteCommandApi.IsManualFire(owner, weaponStation, target) || ResoluteCommandApi.AllowAutomatic(owner, weaponStation, target);
        }
        private static void Postfix(MissileLauncher __instance, Unit owner, Unit target, int __state)
        { if (__instance.ammo < __state) ResoluteCommandState.ReportAttack(owner, target); }
    }
    [HarmonyPatch(typeof(Unit), nameof(Unit.RecordDamage))]
    internal static class ResoluteCommandDamageEvidence
    {
        private static void Postfix(Unit __instance, PersistentID lastDamagedBy, float damageAmount)
        {
            Unit attacker;
            if (damageAmount > .1f && UnitRegistry.TryGetUnit(lastDamagedBy, out attacker)) ResoluteCommandState.ReportAttack(attacker, __instance);
        }
    }
    [HarmonyPatch(typeof(Gun), "FixedUpdate")]
    internal static class ResoluteCommandGunBudget
    {
        internal static bool Available;
        private static readonly FieldInfo Queue = AccessTools.Field(typeof(Gun), "queuedBullets");
        private static readonly FieldInfo Station = AccessTools.Field(typeof(Weapon), "weaponStation");
        private static readonly MethodInfo Minimum = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(float), typeof(float) });
        private static readonly MethodInfo Clamp = AccessTools.Method(typeof(ResoluteCommandState), nameof(ResoluteCommandState.ClampGunBudget));
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var sites = new List<int>();
            for (int i = 1; i < codes.Count; i++)
                if (codes[i].opcode == OpCodes.Stfld && Equals(codes[i].operand, Queue) && codes[i - 1].Calls(Minimum)) sites.Add(i);
            if (sites.Count != 1 || codes[sites[0]].blocks.Count > 0)
            {
                Available = false;
                Plugin.Instance?.LogStartupWarning("Native gun queue layout differs; exact manual gun orders disabled, original gun code retained.");
                return codes;
            }
            int site = sites[0];
            var self = new CodeInstruction(OpCodes.Ldarg_0);
            self.labels.AddRange(codes[site].labels); codes[site].labels.Clear();
            codes.InsertRange(site, new[] { self, new CodeInstruction(OpCodes.Call, Clamp) });
            Available = true; return codes;
        }
        private static void Prefix(Gun __instance, out int __state, ref int ___ticksSinceTriggerPull, ref float ___queuedBullets)
        {
            __state = __instance.ammo;
            if (ResoluteCommandState.Find(__instance.attachedUnit as Ship) == null) return;
            if (ResoluteCommandState.OwnsManualWeapon(__instance)) return;
            Turret turret = __instance.GetComponentInParent<Turret>();
            WeaponStation station = turret != null ? turret.GetWeaponStation() : (WeaponStation)Station.GetValue(__instance);
            Unit target = turret != null ? turret.GetTarget() : __instance.GetTarget();
            if (!ResoluteCommandApi.AllowAutomatic(__instance.attachedUnit, station, target))
            { ___ticksSinceTriggerPull = 3; ___queuedBullets = 0f; }
        }
        private static void Postfix(Gun __instance, int __state) => ResoluteCommandState.GunFired(__instance, __state);
    }
    [HarmonyPatch(typeof(Turret), "ChooseTarget")]
    internal static class ResoluteCommandTurretSearchGate
    {
        private static bool Prefix(Turret __instance) => ResoluteCommandState.ClearingTurret == __instance || !ResoluteCommandState.OwnsManualTurret(__instance);
    }
    [HarmonyPatch(typeof(Turret), nameof(Turret.SetTarget))]
    internal static class ResoluteCommandTurretTargetGate
    {
        private static bool Prefix(Turret __instance, PersistentID id) => ResoluteCommandState.AllowsTurretTarget(__instance, id);
    }
    [HarmonyPatch(typeof(Turret), nameof(Turret.DestroyTurret))]
    internal static class ResoluteCommandTurretDestroyed
    {
        private static void Prefix(Turret __instance, ref Unit ___target)
        {
            // The manual target has no native AI reservation. Native turret
            // destruction must not subtract an unrelated attacker's counter.
            if (ResoluteCommandState.OwnsManualTurret(__instance)) ___target = null;
        }
    }
}
