using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Native cruise terrain routing advances its heading only ten degrees
    // from current horizontal velocity. A vertical ejection has no useful
    // horizontal course yet: inherit the authorized destination's bearing
    // for the initial turn, then hand back to that same native terrain path.
    internal sealed class NaturalSpearLaunchBearing : MonoBehaviour
    {
        private Missile missile;
        private NaturalSpearFlight profile;
        private bool validCue, finished;
        internal const float MaximumTurnSeconds = 8f;

        private void Awake()
        {
            missile = GetComponent<Missile>();
            profile = GetComponent<NaturalSpearFlight>();
        }

        internal void InitializeCue(OpticalSeekerCruiseMissile seeker, Unit nativeTarget)
        {
            if (!Applies()) return;
            // Validate the native memory, without replacing it with a live
            // target transform or inventing a target for coordinate orders.
            validCue = ResoluteSpearPosition.TryGet(seeker, out _) ||
                nativeTarget != null && !nativeTarget.disabled && missile.NetworkHQ != null &&
                missile.NetworkHQ.TryGetKnownPosition(nativeTarget, out _);
        }

        private bool Applies()
        {
            return !finished && enabled && missile != null && !missile.disabled && missile.LocalSim &&
                missile.rb != null && missile.owner is Ship && profile != null &&
                missile.definition != null && missile.definition.jsonKey == "rsl_cruise" &&
                (missile.startRotation * Vector3.forward).y > .55f;
        }

        internal void Apply(OpticalSeekerCruiseMissile seeker, GlobalPosition nativePosition,
            bool guidance, bool terminalMode, ref Vector3 terrainClearVector)
        {
            if (!Applies()) return;
            float age = missile.timeSinceSpawn;
            if (!Finite(age) || age < 0f || age >= MaximumTurnSeconds || terminalMode)
            { finished = true; enabled = false; return; }
            if (!validCue || !Finite(nativePosition.x) || !Finite(nativePosition.y) || !Finite(nativePosition.z)) return;

            GlobalPosition current = missile.GlobalPosition();
            Vector3 axis = missile.startRotation * Vector3.forward;
            // Preserve the full authored launch-length clearance, measured
            // from the true native spawn point through floating-origin shifts.
            if (!guidance || Vector3.Dot(current - missile.startPosition, axis) < profile.LaunchClearance)
            {
                missile.SetAimpoint(current + axis * 10000f, Vector3.zero);
                return;
            }
            if (!TryDirection(nativePosition - current, out Vector3 direction)) return;

            Vector3 prograde = missile.rb.velocity;
            Vector3 horizontal = prograde; horizontal.y = 0f;
            Vector3 bearing = direction; bearing.y = 0f;
            bool aligned = horizontal.sqrMagnitude > 25f &&
                Vector3.Angle(horizontal, bearing) <= 8f &&
                Vector3.Angle(missile.transform.forward, direction) <= 10f;
            if (aligned)
            {
                // The velocity now has a meaningful course. Seed only the
                // previous-path smoothing state and immediately run the real
                // native terrain query before relinquishing launch guidance.
                terrainClearVector = prograde.normalized * Mathf.Max(100f, missile.speed * 6f);
                missile.SetAimpoint(seeker.TerrainWaypoint(nativePosition), Vector3.zero);
                finished = true; enabled = false;
                return;
            }
            missile.SetAimpoint(current + direction * 1500f, Vector3.zero);
        }

        internal static bool TryDirection(Vector3 delta, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (!Finite(delta.x) || !Finite(delta.y) || !Finite(delta.z)) return false;
            Vector3 horizontal = delta; horizontal.y = 0f;
            if (horizontal.sqrMagnitude < 1f) return false;
            // Positive ship-clear climb; an elevated known destination may
            // demand a steeper climb. The target bearing never uses ship yaw.
            float climb = Mathf.Clamp(delta.y / horizontal.magnitude, .45f, 2f);
            direction = (horizontal.normalized + Vector3.up * climb).normalized;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.Initialize))]
    internal static class NaturalSpearLaunchBearingInitialize
    {
        private static void Postfix(OpticalSeekerCruiseMissile __instance, Unit ___targetUnit)
            => __instance.GetComponent<NaturalSpearLaunchBearing>()?.InitializeCue(__instance, ___targetUnit);
    }

    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), nameof(OpticalSeekerCruiseMissile.Seek))]
    internal static class NaturalSpearLaunchBearingSeek
    {
        private static void Postfix(OpticalSeekerCruiseMissile __instance, GlobalPosition ___knownPos,
            bool ___guidance, bool ___terminalMode, ref Vector3 ___terrainClearVector)
        {
            __instance.GetComponent<NaturalSpearLaunchBearing>()?.Apply(__instance, ___knownPos,
                ___guidance, ___terminalMode, ref ___terrainClearVector);
        }
    }
}
