using UnityEngine;

namespace Resolute
{
    // Terminal presentation is already on when Begin is called. This owns only
    // a short native motor force cutoff, never effect state or rigidbody speed.
    internal sealed class NaturalPikeTerminalAcceleration : MonoBehaviour
    {
        private Missile missile;
        private NaturalWeaponPhase phase;
        private uint targetId;
        private int rank, count;
        private float referenceTime, delaySeconds, beganAt = -1f, startsAt = -1f, deadline = -1f;
        private float initialSpeed, lastCeiling, cutoffRestoredAt = -1f, fullSpeedObservedAt = -1f;
        private bool begun, cutoffRestored;
        private string completionReason;

        internal bool Begun => begun;

        internal static void Arm(Missile missile, uint assignedTarget, int rank, int count,
            float referenceTime, float delaySeconds)
        {
            if (!Eligible(missile) || assignedTarget == 0 || missile.targetID.Id != assignedTarget ||
                count < 1 || count > PikeTerminalAccelerationPolicy.MaximumMembers || rank < 0 || rank >= count ||
                !PikeTerminalAccelerationPolicy.Finite(referenceTime) || !PikeTerminalAccelerationPolicy.Finite(delaySeconds) ||
                delaySeconds < 0f || delaySeconds > rank *
                    PikeTerminalAccelerationPolicy.DesiredGapMetres /
                    (PikeTerminalAccelerationPolicy.TerminalSpeed - PikeTerminalAccelerationPolicy.CruiseSpeed) + .00001f) return;
            NaturalPikeTerminalAcceleration state = missile.GetComponent<NaturalPikeTerminalAcceleration>();
            if (state == null)
            {
                state = missile.gameObject.AddComponent<NaturalPikeTerminalAcceleration>();
                state.missile = missile; state.phase = missile.GetComponent<NaturalWeaponPhase>();
            }
            // Once begun, losses/retargets must not compress, reorder or restart
            // the existing acceleration schedule. Repeated unchanged plans also
            // preserve their original clock while awaiting actual release.
            if (state.begun || state.targetId == assignedTarget) return;
            state.targetId = assignedTarget; state.rank = rank; state.count = count;
            state.referenceTime = referenceTime; state.delaySeconds = delaySeconds;
        }

        internal void Begin(float actualSpeed, float now)
        {
            if (begun || !Eligible(missile) || phase == null || missile.targetID.Id != targetId ||
                !PikeTerminalAccelerationPolicy.Finite(actualSpeed) || !PikeTerminalAccelerationPolicy.Finite(now)) return;
            begun = true; beganAt = now; initialSpeed = Mathf.Max(0f, actualSpeed);
            // A late receiver consumes only the remainder of the shared delay;
            // even an invalid future reference cannot stretch this brief hold.
            startsAt = now + Mathf.Clamp(referenceTime + delaySeconds - now, 0f, delaySeconds);
            deadline = startsAt + PikeTerminalAccelerationPolicy.RampSeconds;
            lastCeiling = PikeTerminalAccelerationPolicy.SpeedCeiling(initialSpeed, startsAt, now, phase.TerminalSpeed);
            if (count <= 1) Restore(now, "single-target-recipient");
            else if (initialSpeed >= phase.TerminalSpeed) Restore(now, "already-at-terminal-speed");
        }

        internal float SpeedCeiling(float now, float terminalSpeed)
        {
            if (!begun || cutoffRestored) return terminalSpeed;
            if (!PikeTerminalAccelerationPolicy.Finite(now) || missile == null || missile.targetID.NotValid)
            { Restore(now, "native-target-cleared"); return terminalSpeed; }
            if (now >= deadline)
            { Restore(now, "acceleration-ramp-complete"); return terminalSpeed; }
            lastCeiling = PikeTerminalAccelerationPolicy.SpeedCeiling(initialSpeed, startsAt, now, terminalSpeed);
            return lastCeiling;
        }

        private static bool Eligible(Missile missile) => missile != null && missile.LocalSim && !missile.disabled &&
            missile.definition != null && missile.definition.jsonKey == "rsl_ashm";

        private void FixedUpdate()
        {
            if (!begun || !Eligible(missile) || phase == null) return;
            float now = Time.timeSinceLevelLoad;
            // Native target-clear handling can return before ApplySeaSkim's
            // normal motor update. This independent deadline prevents a stale
            // temporary cutoff from surviving that early return.
            if (missile.targetID.NotValid) Restore(now, "native-target-cleared");
            else if (now >= deadline) Restore(now, "acceleration-ramp-complete");
            if (phase.TerminalBoostActive || cutoffRestored)
                phase.SetMotorSpeedLimit(SpeedCeiling(now, phase.TerminalSpeed));
            if (cutoffRestored && fullSpeedObservedAt < 0f && missile.speed >= phase.TerminalSpeed)
            { fullSpeedObservedAt = now; enabled = false; }
        }

        private void Restore(float now, string reason)
        {
            if (cutoffRestored || phase == null) return;
            cutoffRestored = true; cutoffRestoredAt = now; completionReason = reason;
            lastCeiling = phase.TerminalSpeed;
            phase.SetMotorSpeedLimit(phase.TerminalSpeed);
        }

        internal object Capture() => new {
            armedTargetId = targetId, rank = rank, count = count, delaySeconds = delaySeconds,
            referenceGameSeconds = referenceTime, begun = begun, beganGameSeconds = beganAt,
            initialSpeedMps = initialSpeed, accelerationStartsGameSeconds = startsAt, deadlineGameSeconds = deadline,
            motorSpeedCeilingMps = lastCeiling, fullTerminalCutoffRestored = cutoffRestored,
            cutoffRestoredGameSeconds = cutoffRestoredAt, completionReason = completionReason,
            fullTerminalSpeedObserved = fullSpeedObservedAt >= 0f, fullTerminalSpeedObservedGameSeconds = fullSpeedObservedAt,
            finalSpeedMps = phase != null ? phase.TerminalSpeed : PikeTerminalAccelerationPolicy.TerminalSpeed,
            authority = "temporary native motor topSpeed cutoff; common final speed; effects/fuel/evasion unchanged"
        };
    }
}
