using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native propulsion combines forward thrust and rudder side-force at the
    // same submerged stern point. On the fitted Resolute, that point is roughly
    // four metres below COM; its inward rolling moment dominates the outward
    // moment from native hull side-drag. Retain the native yaw moment and remove
    // that steering contribution about the hull's longitudinal axis. At speed,
    // add a bounded outward turn moment; buoyancy still determines the actual heel.
    // Hull drag, restoring buoyancy, flooding/list, forward thrust, immersion,
    // acceleration and steering input remain native. No target heel is imposed.
    [HarmonyPatch(typeof(ShipPropulsion), "FixedUpdate")]
    internal static class ResoluteTurnHydrodynamics
    {
        internal static void Postfix(ShipPropulsion __instance, Ship ___ship,
            ShipPart ___part, Transform ___thrustTransform, bool ___underwater,
            float ___thrust, float ___steeringThrust, float ___steeringInputSmoothed)
        {
            if (___ship == null || !Plugin.IsResolute(___ship.definition) ||
                !___ship.LocalSim || ___ship.disabled || !__instance.enabled ||
                ___thrust == 0f || ___part == null || ___part.IsDetached() ||
                ___ship.rb == null || ___part.rb != ___ship.rb || ___ship.rb.isKinematic ||
                ___thrustTransform == null ||
                (___underwater && ___thrustTransform.position.y >= Datum.LocalSeaY)) return;

            // Read the already-smoothed native value, including AllowedSteerRate.
            // Recomputing from raw input would fail during turn-in and turn-out.
            float amount = ___steeringInputSmoothed * ___steeringThrust;
            if (amount == 0f || float.IsNaN(amount) || float.IsInfinity(amount)) return;
            Vector3 correction = Compensation(___thrustTransform.position - ___ship.rb.worldCenterOfMass,
                __instance.transform.right * amount, ___ship.transform.forward) *
                TurnHeelGain(Vector3.Dot(___ship.rb.velocity, ___ship.transform.forward));
            if (float.IsNaN(correction.sqrMagnitude) || float.IsInfinity(correction.sqrMagnitude)) return;
            ___ship.rb.AddTorque(correction);
        }

        internal static Vector3 Compensation(Vector3 lever, Vector3 sideForce, Vector3 bowDirection)
            => bowDirection * -Vector3.Dot(Vector3.Cross(lever, sideForce), bowDirection);

        internal static float TurnHeelGain(float forwardSpeed)
            => 1f + 2f * Mathf.Clamp01(Mathf.Abs(forwardSpeed) / 8f);
    }
}
