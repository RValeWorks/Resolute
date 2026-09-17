using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native Disappear keeps the spent round alive for sound/effects, but its
    // IgnoreCollisions layer is still included in other missiles' linecasts.
    // Remove only this invisible body's collider when native detonation hides it.
    [HarmonyPatch(typeof(Missile), "Disappear")]
    internal static class NaturalSpentMissileCollision
    {
        private static void Postfix(Missile __instance)
        {
            if (__instance.GetComponent<NaturalWeaponPhase>() == null) return;
            CapsuleCollider body = __instance.GetComponent<CapsuleCollider>();
            if (body != null) body.enabled = false;
        }
    }
}
