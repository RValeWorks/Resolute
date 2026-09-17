using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    // Live interface ownership is separate from persistent navigation/fire orders.
    // Commands currently support only the local mission host, hence one live session.
    public static class ResoluteCommandSession
    {
        public const float HeartbeatTimeoutSeconds = 5f;
        private static Ship commandedShip;
        private static Player commander;
        private static FactionHQ faction;
        private static object mission;
        private static float heartbeat, lastGameSeconds;

        internal static bool Begin(Ship ship, out string reason)
        {
            if (!ResoluteNavigationCommands.CanCommand(ship, out reason)) return false;
            Player player;
            if (!GameManager.GetLocalPlayer<Player>(out player) || player == null || !player.IsServer)
            { reason = "Ship commands currently require the local mission host."; return false; }
            float now = Time.unscaledTime, game = Time.timeSinceLevelLoad;
            if (!Finite(now) || !Finite(game))
            { reason = "Command session timing is unavailable."; return false; }
            commandedShip = ship;
            commander = player;
            faction = player.HQ;
            mission = MissionManager.CurrentMission;
            heartbeat = now;
            lastGameSeconds = game;
            return true;
        }

        internal static void End(Ship ship)
        {
            // A delayed exit for the previous ship must not release its replacement.
            if (ReferenceEquals(commandedShip, ship)) Clear();
        }

        public static Player GetCommander(Ship ship)
        {
            return Valid() && commandedShip == ship ? commander : null;
        }

        public static bool IsCommanded(Ship ship) => GetCommander(ship) != null;

        // The optional addon calls this only while its real command interface is active.
        // Expired or invalid sessions require Begin again; reads cannot revive ownership.
        public static bool KeepAlive(Ship ship)
        {
            if (GetCommander(ship) == null) return false;
            heartbeat = Time.unscaledTime;
            return true;
        }

        private static bool Valid()
        {
            if (commandedShip == null || commander == null) { Clear(); return false; }
            float now = Time.unscaledTime, game = Time.timeSinceLevelLoad;
            Player local;
            if (!Finite(now) || !Finite(game) || now < heartbeat || now - heartbeat >= HeartbeatTimeoutSeconds ||
                game < lastGameSeconds || !ReferenceEquals(mission, MissionManager.CurrentMission) ||
                !commander.IsServer || commander.HQ != faction ||
                !GameManager.GetLocalPlayer<Player>(out local) || local != commander ||
                !ResoluteNavigationCommands.CanCommand(commandedShip, out _))
            { Clear(); return false; }
            lastGameSeconds = game;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static void Clear()
        {
            commandedShip = null;
            commander = null;
            faction = null;
            mission = null;
            heartbeat = lastGameSeconds = 0f;
        }
    }
}
