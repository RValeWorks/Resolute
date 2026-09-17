using HarmonyLib;

namespace Resolute
{
    // Native scoring assumes a sub-500 blast missile with no unit target is
    // spent. An authorized Pike area search has a live navigation objective
    // before its leader assigns ships, so that assumption does not apply.
    [HarmonyPatch(typeof(Missile), nameof(Missile.InterceptPriority))]
    internal static class NaturalPikeThreat
    {
        private static void Postfix(Missile __instance, Unit toUnit, Unit ___target,
            WeaponInfo ___info, ref float __result)
        {
            if (__result != 0f || __instance == null || __instance.disabled ||
                !__instance.isActiveAndEnabled || __instance.definition == null ||
                __instance.definition.jsonKey != "rsl_ashm" || ___target != null ||
                __instance.targetID.IsValid || ___info == null || !(___info.blastDamage < 500f) ||
                toUnit == null || toUnit.disabled || toUnit.NetworkHQ == null ||
                __instance.NetworkHQ == null || toUnit.NetworkHQ == __instance.NetworkHQ ||
                !ResoluteStrikeOrders.HasAreaObjective(__instance)) return;

            // This is the native neutral score for an unassigned large-warhead
            // missile. Detection, opportunity, interception geometry, weapon
            // suitability, reservations and firing remain native. Do not set a
            // target or grant the self-defense multiplier to every fleet ship.
            // No LocalSim gate: assessment authority belongs to the defender.
            // A peer without this order's membership receives no invented state.
            __result = 1f;
        }
    }
}
