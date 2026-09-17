using System;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    [NetworkMessage]
    internal struct PikeFlarePairMessage
    {
        internal uint MissileId;
        internal byte BurstIndex;
        internal float TargetRange;
        internal Vector3[] Positions, Velocities;
    }

    // The server owns the finite sequence. Observers receive one bounded
    // message per opposed flare pair; they never estimate or schedule a burst.
    internal static class PikeCountermeasureNetworking
    {
        private static bool serializationReady;

        internal static void Initialize()
        {
            if (!serializationReady)
            {
                Writer<PikeFlarePairMessage>.Write = Write;
                Reader<PikeFlarePairMessage>.Read = Read;
                MessagePacker.RegisterMessage<PikeFlarePairMessage>();
                serializationReady = true;
            }
        }

        internal static void RegisterClient(NetworkManagerNuclearOption manager)
        {
            // Encyclopedia assets may finish before the native network-manager
            // preload. Install the handler from its actual client lifecycle,
            // without accessing the logging singleton getter during asset setup.
            Initialize();
            if (manager != null && manager.Client != null && manager.Client.MessageHandler != null)
                ((IMessageReceiver)manager.Client.MessageHandler).RegisterHandler<PikeFlarePairMessage>(Receive, false);
        }

        internal static void Send(Missile missile, PikeFlarePairMessage message)
        {
            if (missile == null || !missile.LocalSim || !missile.IsServer || !ValidMessage(message)) return;
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            if (manager == null || !manager.Server.Active) return;
            manager.Server.SendToObservers(missile.Identity, message, excludeLocalPlayer: true, excludeOwner: false);
        }

        private static void Receive(INetworkPlayer sender, PikeFlarePairMessage message)
        {
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            if (manager == null || manager.Server.Active || !ValidMessage(message)) return;
            Unit unit;
            if (!UnitRegistry.TryGetUnit(new PersistentID { Id = message.MissileId }, out unit)) return;
            Missile missile = unit as Missile;
            if (missile == null || missile.definition == null || missile.definition.jsonKey != NaturalPikeCountermeasures.WeaponKey) return;
            NaturalPikeCountermeasures.Find(missile)?.ReceiveBurst(message);
        }

        internal static bool ValidMessage(PikeFlarePairMessage message)
        {
            if (message.BurstIndex >= PikeCountermeasureSchedule.BurstCapacity || message.MissileId == 0 ||
                !Finite(message.TargetRange) || message.TargetRange < 0f ||
                message.Positions == null || message.Velocities == null ||
                message.Positions.Length != PikeCountermeasureSchedule.FlaresPerBurst ||
                message.Velocities.Length != PikeCountermeasureSchedule.FlaresPerBurst) return false;
            for (int i = 0; i < PikeCountermeasureSchedule.FlaresPerBurst; i++)
                if (!Finite(message.Positions[i]) || !Finite(message.Velocities[i])) return false;
            return true;
        }

        private static void Write(NetworkWriter writer, PikeFlarePairMessage message)
        {
            writer.WriteUInt32(message.MissileId);
            writer.WriteByte(message.BurstIndex);
            writer.WriteSingle(message.TargetRange);
            // No array length comes from the network: the protocol is always
            // exactly two positions and velocities, in global coordinates.
            for (int i = 0; i < PikeCountermeasureSchedule.FlaresPerBurst; i++)
            {
                WriteVector(writer, message.Positions[i]);
                WriteVector(writer, message.Velocities[i]);
            }
        }

        private static PikeFlarePairMessage Read(NetworkReader reader)
        {
            var message = new PikeFlarePairMessage {
                MissileId = reader.ReadUInt32(), BurstIndex = reader.ReadByte(), TargetRange = reader.ReadSingle(),
                Positions = new Vector3[PikeCountermeasureSchedule.FlaresPerBurst],
                Velocities = new Vector3[PikeCountermeasureSchedule.FlaresPerBurst]
            };
            for (int i = 0; i < PikeCountermeasureSchedule.FlaresPerBurst; i++)
            {
                message.Positions[i] = ReadVector(reader);
                message.Velocities[i] = ReadVector(reader);
            }
            return message;
        }

        private static void WriteVector(NetworkWriter writer, Vector3 value)
        {
            writer.WriteSingle(value.x); writer.WriteSingle(value.y); writer.WriteSingle(value.z);
        }
        private static Vector3 ReadVector(NetworkReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static bool Finite(Vector3 value) { return Finite(value.x) && Finite(value.y) && Finite(value.z); }
    }

    [HarmonyPatch(typeof(NetworkManagerNuclearOption), "ClientStarted")]
    internal static class PikeCountermeasureClientStartedPatch
    {
        private static void Postfix(NetworkManagerNuclearOption __instance) { PikeCountermeasureNetworking.RegisterClient(__instance); }
    }
}
