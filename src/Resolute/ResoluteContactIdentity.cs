using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Resolute
{
    // Identity association only. This registry never supplies a position or a Unit lookup.
    // ESM measurements and normal faction tracks share a number without granting a lock.
    public static class ResoluteContactIdentity
    {
        private static ConditionalWeakTable<FactionHQ, ResoluteContactNumberLedger> ledgers = new ConditionalWeakTable<FactionHQ, ResoluteContactNumberLedger>();
        private static int sceneHandle = -1;
        private static object mission;
        private static float lastTime;

        internal static uint ForFaction(FactionHQ faction, PersistentID identity)
        {
            if (faction == null || identity.NotValid) return 0;
            int scene = SceneManager.GetActiveScene().handle;
            float now = Time.timeSinceLevelLoad;
            if (scene != sceneHandle || now < lastTime || !ReferenceEquals(mission, MissionManager.CurrentMission))
            {
                ledgers = new ConditionalWeakTable<FactionHQ, ResoluteContactNumberLedger>();
                sceneHandle = scene;
                mission = MissionManager.CurrentMission;
            }
            lastTime = now;
            return ledgers.GetValue(faction, _ => new ResoluteContactNumberLedger()).Get(identity.Id);
        }

        public static uint GetNumber(Ship receiver, Unit contact)
        {
            if (!ResoluteCommandApi.CanCommand(receiver, out _) || contact == null ||
                receiver.NetworkHQ == null || contact.persistentID.NotValid) return 0;
            if (contact.NetworkHQ != receiver.NetworkHQ &&
                !receiver.NetworkHQ.TryGetKnownPosition(contact, out _)) return 0;
            return ForFaction(receiver.NetworkHQ, contact.persistentID);
        }
    }
}
