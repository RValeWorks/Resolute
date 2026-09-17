using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Resolute
{
    // Native parts always begin at 100 HP; their tolerances, rather than their
    // physical mass, determine how much penetrating damage that represents.
    internal static class ResoluteProtection
    {
        private static readonly FieldInfo Armor = typeof(UnitPart).GetField("armorProperties", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Configure(Ship ship, Ship donor, HashSet<UnitPart> weapons, float targetMass)
        {
            if (ship == null || donor == null || ship == donor || ship.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Resolute protection requires a separate inactive prefab.");
            float originalMass = donor.GetComponentsInChildren<UnitPart>(true).Sum(p => p.mass);
            if (!(originalMass > 0f) || !(targetMass > 0f)) throw new InvalidOperationException("Missing structural protection mass reference.");
            float factor = targetMass / originalMass;
            foreach (ShipPart part in ship.GetComponentsInChildren<ShipPart>(true))
            {
                // Exposed mounts and the native radar-bearing tower keep the
                // donor vulnerability. Structural strength is not sensor armor.
                if (weapons.Contains(part) || part.GetComponent<Radar>() != null) continue;
                Armor.SetValue(part, Scale(part.GetArmorProperties(), factor));
            }
        }

        internal static ArmorProperties Scale(ArmorProperties original, float factor)
        {
            if (original == null || float.IsNaN(factor) || float.IsInfinity(factor) || factor <= 0f)
                throw new InvalidOperationException("Invalid structural protection reference.");
            // Clone even when native objects share armor configuration. Keep
            // penetration, blast shielding and all break/flood thresholds exact.
            return new ArmorProperties {
                pierceArmor = original.pierceArmor, blastArmor = original.blastArmor,
                fireArmor = original.fireArmor, overpressureLimit = original.overpressureLimit,
                pierceTolerance = original.pierceTolerance * factor,
                blastTolerance = original.blastTolerance * factor,
                fireTolerance = original.fireTolerance * factor
            };
        }
    }
}
