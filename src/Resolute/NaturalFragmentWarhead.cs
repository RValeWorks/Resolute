using System.Collections;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    // Native large-yield explosions use a propagating pressure wave and omit
    // the normal missile fragment traces. Heavy aerial fragmentation warheads
    // also require the native fragment/armor path against fast crossing bodies.
    internal sealed class NaturalFragmentWarhead : MonoBehaviour
    {
        public float Yield;
        private bool scheduled;

        internal void Schedule(Missile missile, Unit relativeUnit, bool useUnit, Vector3 position, bool armed)
        {
            if (scheduled || !armed || Yield <= 200f || NetworkManagerNuclearOption.i == null ||
                !NetworkManagerNuclearOption.i.Server.Active) return;
            scheduled = true;
            StartCoroutine(Fragments(missile, useUnit && relativeUnit != null ? relativeUnit.transform : null, position));
        }
        private IEnumerator Fragments(Missile missile, Transform relativeTarget, Vector3 position)
        {
            // Match Missile.ExplosionForceOnPhysicsFrame, including its target
            // frame so the same collision step receives the native frag rays.
            PersistentID dealer = missile.ownerID, projectile = missile.persistentID;
            yield return new WaitForFixedUpdate();
            Vector3 origin = relativeTarget != null ? relativeTarget.TransformPoint(position) : position + Datum.origin.position;
            DamageEffects.BlastFrag(Yield, origin, dealer, projectile);
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.RpcDetonate))]
    internal static class NaturalFragmentWarheadPatch
    {
        private static void Prefix(Missile __instance, Unit relativeUnit, bool useUnit, Vector3 pos, bool armed)
        {
            NaturalFragmentWarhead fragments = __instance.GetComponent<NaturalFragmentWarhead>();
            if (fragments != null) fragments.Schedule(__instance, relativeUnit, useUnit, pos, armed);
        }
    }
}
