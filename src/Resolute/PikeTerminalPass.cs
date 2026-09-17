using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // MissedTarget normally reads the real interception aim. Pike replaces that
    // aim with a moving aerodynamic waypoint, so retain the last actual terminal
    // observation solely for the native missed-pass test. This is not guidance
    // or permission to reacquire a target.
    internal sealed class PikeTerminalPass
    {
        private uint target;
        private GlobalPosition position;
        private Vector3 velocity;
        private float observedAt = -1f, closest = float.PositiveInfinity, radius;
        private bool armed, passed;
        internal bool Armed => armed;
        internal bool Passed(uint currentTarget) => currentTarget != 0 && target == currentTarget && passed;

        internal void Reset()
        {
            target = 0; observedAt = -1f; closest = float.PositiveInfinity;
            armed = passed = false;
        }

        internal void Observe(uint targetId, bool independent, bool validObservation, GlobalPosition observedPosition,
            Vector3 observedVelocity, float targetRadius, GlobalPosition missilePosition, Vector3 missileVelocity,
            float speed, float now, float memorySeconds)
        {
            if (target != targetId || !independent || targetId == 0)
            {
                Reset(); target = targetId;
            }
            if (!independent || targetId == 0 || passed) return;
            if (validObservation)
            {
                position = observedPosition; velocity = observedVelocity; radius = Mathf.Max(0f, targetRadius);
                observedAt = now;
            }
            float age = now - observedAt;
            if (observedAt < 0f || age < 0f || age > Mathf.Max(0f, memorySeconds)) return;
            Vector3 remaining = position + velocity * age - missilePosition;
            float range = remaining.magnitude;
            float closing = Vector3.Dot(remaining, missileVelocity - velocity);
            // A new assignment behind the missile or a rearward formation
            // waypoint is not evidence of a terminal pass.
            if (validObservation && closing > 0f && range <= PikeFlightProfile.FinalCorrectionRange + radius)
                armed = true;
            if (!armed) return;
            closest = Mathf.Min(closest, range);
            float margin = Mathf.Max(radius + 20f, Mathf.Max(80f, speed * .2f));
            if (range > closest + margin && closing < 0f) passed = true;
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.MissedTarget))]
    internal static class PikeTerminalMissedPass
    {
        internal static void Postfix(Missile __instance, ref bool __result)
        {
            if (__result || __instance == null || !__instance.LocalSim || __instance.disabled ||
                __instance.definition == null || __instance.definition.jsonKey != "rsl_ashm") return;
            NaturalCruiseGuidance guidance = __instance.GetComponent<NaturalCruiseGuidance>();
            if (guidance != null && guidance.HasMissedTerminalPass) __result = true;
        }
    }
}
