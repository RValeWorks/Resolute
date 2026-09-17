using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Mission-wide simulation time, not time spent loading or viewing a ship.
    // Late spawns use the current mission default rather than restarting it.
    internal static class ResoluteStartupEngagement
    {
        private static bool started;
        private static float beganAt;
        internal static ResoluteEngagementMode DefaultMode => started && MissionManager.IsRunning &&
            Time.timeSinceLevelLoad - beganAt >= 10f ? ResoluteEngagementMode.WeaponsFree : ResoluteEngagementMode.WeaponsTight;
        internal static void Reset() { started = false; }
        internal static void Begin()
        {
            if (!MissionManager.IsRunning) return;
            beganAt = Time.timeSinceLevelLoad;
            started = true;
        }
    }

    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
    internal static class ResoluteStartupEngagementClock
    {
        private static void Prefix() => ResoluteStartupEngagement.Reset();
        private static void Postfix() => ResoluteStartupEngagement.Begin();
    }
}
