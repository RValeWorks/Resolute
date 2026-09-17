using HarmonyLib;

namespace Resolute
{
    // MissedTarget and DetectCollisions also consume aimPoint. Restrict the
    // terrain correction to Steering itself so native loss/retirement and
    // collision geometry continue to see the native extrapolated destination.
    [HarmonyPatch(typeof(Missile), "Steering")]
    internal static class NaturalLostTargetTerrain
    {
        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> AimPoint =
            AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");

        private struct AimState
        {
            internal bool Restore;
            internal GlobalPosition NativeAim;
        }

        private static void Prefix(Missile __instance, out AimState __state)
        {
            __state = default(AimState);
            if (__instance == null || __instance.disabled || !__instance.LocalSim ||
                __instance.targetID.IsValid || __instance.definition == null ||
                __instance.definition.jsonKey != "rsl_ashm") return;
            NaturalCruiseGuidance guidance = __instance.GetComponent<NaturalCruiseGuidance>();
            if (guidance == null) return;
            GlobalPosition nativeAim = AimPoint(__instance);
            if (!guidance.TryLostTargetTerrainAim(nativeAim, out GlobalPosition terrainAim)) return;
            __state.NativeAim = nativeAim;
            __state.Restore = true;
            AimPoint(__instance) = terrainAim;
        }

        private static void Finalizer(Missile __instance, AimState __state)
        {
            if (__state.Restore) AimPoint(__instance) = __state.NativeAim;
        }
    }
}
