using UnityEngine;

namespace Resolute
{
    // Native SearchForRearm stops when a delivery fills every station. In command
    // mode, later firing must not require buying another flight merely to collect
    // supplies already delivered. This fallback transfers existing cargo only;
    // it never registers a rearm request or changes transport aircraft behavior.
    internal static class ResoluteNavalSupplyPickup
    {
        internal static bool NeedsFallback(Ship ship)
        {
            if (!ResoluteReplenishment.Live(ship) || !ResoluteReplenishment.Finite(ship.speed) || ship.speed > 25f) return false;
            // Let the existing native search own ordinary outstanding requests.
            if (ship.HasRequestedRearm && ship.radarAlt <= 1f) return false;
            // A floating hull can fail the native one-metre radar-altitude gate.
            // Keep this exception local to Resolute and physically near sea level.
            float waterline = ship.transform.position.y - Datum.LocalSeaY - ship.definition.spawnOffset.y;
            return ResoluteReplenishment.Finite(waterline) && waterline <= 3f;
        }

        internal static bool TryCollect(Ship ship)
        {
            if (!NeedsFallback(ship)) return false;
            var controller = ship.NetworkHQ.RearmMissionController;
            if (controller == null) return false;
            Rearmer best = null;
            foreach (Rearmer rearmer in controller.Rearmers)
            {
                if (!Delivered(rearmer, ship) ||
                    !FastMath.InRange(ship.GlobalPosition(), rearmer.GetPosition(), ResoluteSupplyPickup.Range(rearmer, ship))) continue;
                if (best == null || rearmer.Capacity > best.Capacity) best = rearmer;
            }
            // Preserve native finite capacity, station allocation, networking and
            // accounting. This path creates no flight or flight-payment request.
            return best != null && best.ProcessRearmRequest(ship, out _);
        }

        internal static bool Delivered(Rearmer rearmer, Ship ship)
        {
            if (rearmer == null || !rearmer.isActiveAndEnabled || !ResoluteRearmAllocation.IsNavalSupply(rearmer) ||
                !(rearmer.Capacity > 0f) || !ResoluteReplenishment.Finite(rearmer.Capacity) ||
                !(rearmer.Unit is Container cargo) || cargo.disabled || !cargo.gameObject.activeInHierarchy ||
                cargo.NetworkHQ != ship.NetworkHQ || cargo.IsSlung()) return false;
            // Mounted cargo is not a registered Container. Released parachute
            // cargo remains unavailable until the native landing cleanup occurs.
            if (cargo.GetComponentInChildren<CargoDeploymentSystem>() != null) return false;
            cargo.CheckRadarAlt();
            return ResoluteReplenishment.Finite(cargo.radarAlt) && cargo.radarAlt <= 2f;
        }
    }
}
