using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    [NetworkMessage]
    internal struct LancePresentationMessage
    {
        internal byte Version;
        internal uint MissileId, Sequence;
        internal float HostAgeSeconds;
        internal byte Flags;
        internal Vector3 WorldUp;
    }

    internal struct LancePresentationState
    {
        internal uint Sequence;
        internal float HostAgeSeconds, ReceivedAgoSeconds;
        internal bool Separated, Failed, BoostActive, CruiseActive;
        internal Vector3 WorldUp;
    }

    // Separate observer-only presentation protocol. Native missile snapshots
    // still own position and velocity; this file never writes either, nor does
    // it detach geometry, consume fuel, choose a target or touch a Rigidbody.
    internal static class LancePresentationNetworking
    {
        internal const byte ProtocolVersion = 1;
        internal const int PayloadBytes = 26;
        internal const float SendInterval = .1f;
        internal const float MaximumHostAgeSeconds = 3600f;
        private const byte SeparatedFlag = 1, FailedFlag = 2, BoostFlag = 4, CruiseFlag = 8;
        private const string WeaponKey = "rsl_scramjet";
        private sealed class Sent
        {
            internal uint Sequence;
            internal float LastSentAt = float.NegativeInfinity, LastAge;
        }
        private sealed class Observed
        {
            internal LancePresentationMessage Message;
            internal float ReceivedAt;
            internal Vector3 VisualUp;
            internal bool VisualInitialized;
        }
        private static readonly ConditionalWeakTable<Missile, Sent> SentStates = new ConditionalWeakTable<Missile, Sent>();
        private static readonly ConditionalWeakTable<Missile, Observed> ObservedStates = new ConditionalWeakTable<Missile, Observed>();
        private static bool serializationReady;

        internal static void Initialize()
        {
            if (serializationReady) return;
            Writer<LancePresentationMessage>.Write = Write;
            Reader<LancePresentationMessage>.Read = Read;
            MessagePacker.RegisterMessage<LancePresentationMessage>();
            serializationReady = true;
        }

        internal static void RegisterClient(NetworkManagerNuclearOption manager)
        {
            Initialize();
            if (manager != null && manager.Client != null && manager.Client.MessageHandler != null)
                ((IMessageReceiver)manager.Client.MessageHandler).RegisterHandler<LancePresentationMessage>(Receive, false);
        }

        internal static bool Send(Missile missile, bool separated, bool failed, bool boostActive, bool cruiseActive, Vector3 worldUp)
        {
            if (!LiveLance(missile) || !missile.LocalSim || !missile.IsServer || missile.Identity == null) return false;
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            if (manager == null || manager.Server == null || !manager.Server.Active) return false;
            bool hasRemoteObserver = false;
            foreach (INetworkPlayer observer in missile.Identity.observers)
                if (observer != null && !ReferenceEquals(observer, manager.Server.LocalPlayer) &&
                    observer.IsConnected && observer.IsAuthenticated && observer.SceneIsReady)
                { hasRemoteObserver = true; break; }
            if (!hasRemoteObserver) return false;
            float now = Time.unscaledTime;
            if (!Finite(now)) return false;
            Sent state = SentStates.GetValue(missile, _ => new Sent());
            if (now < state.LastSentAt || now - state.LastSentAt < SendInterval ||
                missile.timeSinceSpawn < state.LastAge || state.Sequence == uint.MaxValue) return false;
            var message = new LancePresentationMessage {
                Version = ProtocolVersion, MissileId = missile.persistentID.Id, Sequence = state.Sequence + 1,
                HostAgeSeconds = missile.timeSinceSpawn, WorldUp = worldUp.normalized,
                Flags = (byte)((separated ? SeparatedFlag : 0) | (failed ? FailedFlag : 0) |
                    (boostActive ? BoostFlag : 0) | (cruiseActive ? CruiseFlag : 0))
            };
            if (!ValidMessage(message)) return false;
            Initialize();
            // Every sample is complete. Newly interested/late observers obtain
            // the current stage on the next tick, with no replay queue. An
            // unreliable channel avoids queuing obsolete bank samples.
            manager.Server.SendToObservers(missile.Identity, message, excludeLocalPlayer: true, excludeOwner: false, channelId: Channel.Unreliable);
            state.Sequence = message.Sequence; state.LastSentAt = now; state.LastAge = message.HostAgeSeconds;
            return true;
        }

        private static void Receive(INetworkPlayer sender, LancePresentationMessage message)
        {
            NetworkManagerNuclearOption manager = NetworkManagerNuclearOption.i;
            if (manager == null || manager.Server == null || manager.Server.Active || manager.Client == null ||
                !manager.Client.Active || !manager.Client.IsConnected || sender == null ||
                !ReferenceEquals(sender, manager.Client.Player) || !sender.IsConnected || !sender.IsAuthenticated ||
                !ValidMessage(message)) return;
            if (!UnitRegistry.TryGetUnit(new PersistentID { Id = message.MissileId }, out Unit unit)) return;
            Accept(unit as Missile, message);
        }

        // Validation of an authenticated server sample is separate so the
        // fixed protocol and ordering rules can be exercised without a game.
        internal static bool Accept(Missile missile, LancePresentationMessage message)
        {
            if (!LiveLance(missile) || missile.LocalSim || missile.IsServer ||
                missile.persistentID.Id != message.MissileId || !ValidMessage(message) || !Finite(Time.unscaledTime)) return false;
            if (ObservedStates.TryGetValue(missile, out Observed previous))
            {
                if (!Follows(previous.Message, message)) return false;
                previous.Message = message; previous.ReceivedAt = Time.unscaledTime;
            }
            else ObservedStates.Add(missile, new Observed { Message = message, ReceivedAt = Time.unscaledTime });
            return true;
        }

        internal static bool Follows(LancePresentationMessage previous, LancePresentationMessage next) =>
            ValidMessage(next) && next.Sequence > previous.Sequence && next.HostAgeSeconds >= previous.HostAgeSeconds &&
            !((previous.Flags & SeparatedFlag) != 0 && (next.Flags & SeparatedFlag) == 0) &&
            !((previous.Flags & FailedFlag) != 0 && (next.Flags & FailedFlag) == 0) &&
            // A successful separation begins cruise immediately. Once that
            // finite motor is spent, later samples cannot visually relight it.
            !((previous.Flags & SeparatedFlag) != 0 && (previous.Flags & CruiseFlag) == 0 &&
                (next.Flags & CruiseFlag) != 0);

        internal static bool TryGet(Missile missile, out LancePresentationState state)
        {
            state = default(LancePresentationState);
            if (!LiveLance(missile) || missile.LocalSim || missile.IsServer ||
                !ObservedStates.TryGetValue(missile, out Observed value)) return false;
            LancePresentationMessage message = value.Message;
            state = new LancePresentationState {
                Sequence = message.Sequence, HostAgeSeconds = message.HostAgeSeconds,
                ReceivedAgoSeconds = Mathf.Max(0, Time.unscaledTime - value.ReceivedAt),
                Separated = (message.Flags & SeparatedFlag) != 0, Failed = (message.Flags & FailedFlag) != 0,
                BoostActive = (message.Flags & BoostFlag) != 0, CruiseActive = (message.Flags & CruiseFlag) != 0,
                WorldUp = message.WorldUp
            };
            return true;
        }

        internal static bool TryGetVisualUp(Missile missile, Vector3 currentForward, float deltaTime, out Vector3 up)
        {
            up = Vector3.zero;
            if (!TryGet(missile, out LancePresentationState state) || !Finite(currentForward) ||
                currentForward.sqrMagnitude < .0001f || !Finite(deltaTime) || deltaTime < 0f ||
                !ObservedStates.TryGetValue(missile, out Observed value)) return false;
            Vector3 forward = currentForward.normalized;
            Vector3 target = Vector3.ProjectOnPlane(state.WorldUp, forward);
            // Near a pole, retain a valid previous bank instead of inventing a
            // discontinuity. Native position/velocity continue independently.
            if (target.sqrMagnitude < .0001f) target = Vector3.ProjectOnPlane(value.VisualUp, forward);
            if (target.sqrMagnitude < .0001f) target = Vector3.ProjectOnPlane(Vector3.up, forward);
            if (target.sqrMagnitude < .0001f) target = Vector3.ProjectOnPlane(Vector3.right, forward);
            target.Normalize();
            if (!value.VisualInitialized)
            { value.VisualUp = target; value.VisualInitialized = true; }
            else
            {
                Vector3 prior = Vector3.ProjectOnPlane(value.VisualUp, forward);
                if (prior.sqrMagnitude < .0001f) prior = target;
                prior.Normalize();
                float dt = Mathf.Min(deltaTime, .1f);
                float angle = Vector3.SignedAngle(prior, target, forward);
                float step = angle * (1f - Mathf.Exp(-dt / .08f));
                step = Mathf.Clamp(step, -720f * dt, 720f * dt);
                value.VisualUp = (Quaternion.AngleAxis(step, forward) * prior).normalized;
            }
            up = value.VisualUp;
            return true;
        }

        internal static void Remove(Missile missile)
        {
            if (ReferenceEquals(missile, null)) return;
            SentStates.Remove(missile); ObservedStates.Remove(missile);
        }

        internal static bool ValidMessage(LancePresentationMessage message)
        {
            if (message.Version != ProtocolVersion || message.MissileId == 0 || message.Sequence == 0 ||
                !Finite(message.HostAgeSeconds) || message.HostAgeSeconds < 0f || message.HostAgeSeconds > MaximumHostAgeSeconds ||
                (message.Flags & ~15) != 0 || !Finite(message.WorldUp) ||
                message.WorldUp.sqrMagnitude < .99f || message.WorldUp.sqrMagnitude > 1.01f) return false;
            bool separated = (message.Flags & SeparatedFlag) != 0, failed = (message.Flags & FailedFlag) != 0;
            bool boost = (message.Flags & BoostFlag) != 0, cruise = (message.Flags & CruiseFlag) != 0;
            return !(boost && (separated || failed || cruise)) && !(cruise && (!separated || failed)) && !(failed && !separated);
        }

        // Exactly 26 bytes, no network-selected array lengths or strings.
        internal static void Write(NetworkWriter writer, LancePresentationMessage message)
        {
            writer.WriteByte(message.Version); writer.WriteUInt32(message.MissileId); writer.WriteUInt32(message.Sequence);
            writer.WriteSingle(message.HostAgeSeconds); writer.WriteByte(message.Flags);
            writer.WriteSingle(message.WorldUp.x); writer.WriteSingle(message.WorldUp.y); writer.WriteSingle(message.WorldUp.z);
        }
        internal static LancePresentationMessage Read(NetworkReader reader) => new LancePresentationMessage {
            Version = reader.ReadByte(), MissileId = reader.ReadUInt32(), Sequence = reader.ReadUInt32(),
            HostAgeSeconds = reader.ReadSingle(), Flags = reader.ReadByte(),
            WorldUp = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
        };
        private static bool LiveLance(Missile missile) => missile != null && !missile.disabled && missile.isActiveAndEnabled &&
            missile.definition != null && missile.definition.jsonKey == WeaponKey;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    [HarmonyPatch(typeof(NetworkManagerNuclearOption), "ClientStarted")]
    internal static class LancePresentationClientStartedPatch
    {
        private static void Postfix(NetworkManagerNuclearOption __instance) => LancePresentationNetworking.RegisterClient(__instance);
    }
}
