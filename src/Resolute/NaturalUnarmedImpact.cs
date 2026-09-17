using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native impact handling stops an unarmed round without retiring it. End
    // an actual solid/water collision through its unarmed native fizzle path.
    // Armed rounds retain the game's impact/proximity fuses and penetration.
    [HarmonyPatch(typeof(Missile), "DetectCollisions")]
    internal static class NaturalUnarmedImpact
    {
        private static bool Prefix(Missile __instance)
        {
            NaturalWeaponPhase phase = __instance.GetComponent<NaturalWeaponPhase>();
            if (phase == null || !phase.ContactFuse || !__instance.LocalSim || __instance.disabled || __instance.IsArmed()) return true;
            Vector3 position = __instance.transform.position;
            if (position.y < Datum.LocalSeaY)
            {
                __instance.Detonate(Vector3.up, false, false);
                return false;
            }
            Vector3 displacement = __instance.rb.velocity * (1.1f * Time.fixedDeltaTime);
            if (displacement.sqrMagnitude < .000001f || !Physics.Linecast(position, position + displacement, out var hit,
                PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)) return true;
            Unit victim = hit.collider.GetComponentInParent<Unit>();
            if (victim == __instance || victim == __instance.owner) return true;
            __instance.transform.position = hit.point - displacement.normalized * .2f;
            __instance.rb.MovePosition(__instance.transform.position);
            __instance.Detonate(hit.normal, false, hit.collider.sharedMaterial == GameAssets.i.terrainMaterial);
            return false;
        }
    }
}
