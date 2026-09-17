using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class CarrierRecovery
    {
        private static readonly FieldInfo Contact = AccessTools.Field(typeof(LandingGear), "contactCollider");
        private static readonly FieldInfo Force = AccessTools.Field(typeof(LandingGear), "compressionForce");
        private static readonly FieldInfo Owner = AccessTools.Field(typeof(LandingGear), "aircraft");

        internal static bool DeckContact(Aircraft aircraft, out Ship ship, out float speed)
        {
            ship = null; speed = float.PositiveInfinity;
            if (aircraft == null || aircraft.rb == null || aircraft.NetworkHQ == null) return false;
            int contacts = 0;
            var seen = new HashSet<LandingGear>();
            foreach (UnitPart part in aircraft.GetAllParts())
            {
                if (part == null) continue;
                foreach (LandingGear gear in part.GetComponentsInChildren<LandingGear>(true))
                {
                    if (!seen.Add(gear) || !gear.gameObject.activeInHierarchy || (Owner.GetValue(gear) as Aircraft) != aircraft || (float)Force.GetValue(gear) <= 0f) continue;
                    Collider contact = Contact.GetValue(gear) as Collider;
                    Ship candidate = contact != null && contact.attachedRigidbody != null ? contact.attachedRigidbody.GetComponent<Ship>() : null;
                    if (candidate == null || !Plugin.IsResolute(candidate.definition) || candidate.disabled || candidate.NetworkHQ != aircraft.NetworkHQ) continue;
                    Airbase airbase = candidate.GetComponent<Airbase>();
                    if (airbase == null || airbase.disabled || !airbase.hangars.Any(h => h != null && h.IsFunctional())) continue;
                    // Require two distinct loaded legs on the same live vessel.
                    if (ship != null && ship != candidate) continue;
                    ship = candidate; contacts++;
                }
            }
            if (contacts < 2 || ship == null) return false;
            speed = (aircraft.rb.velocity - ship.rb.GetPointVelocity(aircraft.transform.position)).magnitude;
            return true;
        }

    }

    [HarmonyPatch(typeof(Airbase), nameof(Airbase.SetupAttachedAirbase))]
    internal static class CarrierSavedRadiusPatch
    {
        private static void Postfix(Airbase __instance, Unit unit)
        {
            if (unit == null || !Plugin.IsResolute(unit.definition) || __instance.SavedAirbase == null) return;
            // SetupAttachedAirbase can replace prefab settings with a mission's
            // saved 0.2.0 entry. Upgrade that live record after the native link.
            float radius = unit.definition.length * .5f + 25f;
            foreach (Hangar hangar in unit.GetComponentsInChildren<Hangar>(true))
                if (hangar.GetSpawnTransform() != null)
                    radius = Mathf.Max(radius, Vector3.Distance(__instance.center.position, hangar.GetSpawnTransform().position) + 25f);
            __instance.SavedAirbase.CaptureRange = Mathf.Max(__instance.SavedAirbase.CaptureRange, radius);
        }
    }

}
