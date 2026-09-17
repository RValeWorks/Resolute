using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Group observations are datalink inputs. Every recipient must acquire its
    // own native terminal radar lock; a leader's return is never copied as one.
    internal static class PikeGroupSeeker
    {
        private static readonly AccessTools.FieldRef<MissileSeeker, Unit> Target = AccessTools.FieldRefAccess<MissileSeeker, Unit>("targetUnit");
        private static readonly AccessTools.FieldRef<ARHSeeker, GlobalPosition> Position = AccessTools.FieldRefAccess<ARHSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> Velocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVel");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> PreviousVelocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVelPrev");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> Acceleration = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownAccel");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> PositionError = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("positionalErrorVector");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> Return = AccessTools.FieldRefAccess<ARHSeeker, float>("returnStrength");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> Locked = AccessTools.FieldRefAccess<ARHSeeker, bool>("radarLockEstablished");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> AchievedLock = AccessTools.FieldRefAccess<ARHSeeker, bool>("achievedLock");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> HomingTime = AccessTools.FieldRefAccess<ARHSeeker, float>("homingLockTime");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> MissingTime = AccessTools.FieldRefAccess<ARHSeeker, float>("timeWithoutReturn");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> Distance = AccessTools.FieldRefAccess<ARHSeeker, float>("targetDist");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> TimeToTarget = AccessTools.FieldRefAccess<ARHSeeker, float>("timeToTarget");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> MultipleInbound = AccessTools.FieldRefAccess<ARHSeeker, bool>("multipleInbound");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> Jammed = AccessTools.FieldRefAccess<ARHSeeker, bool>("isJammed");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> HomeOnJam = AccessTools.FieldRefAccess<ARHSeeker, bool>("homeOnJam");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> TrackingAngle = AccessTools.FieldRefAccess<ARHSeeker, float>("maxTrackingAngle");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> DatalinkAngle = AccessTools.FieldRefAccess<ARHSeeker, float>("maxDatalinkAngle");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> ReacquireRange = AccessTools.FieldRefAccess<ARHSeeker, float>("minReacquireRange");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> ActiveRange = AccessTools.FieldRefAccess<ARHSeeker, float>("terminalRange");

        // Read the actual native activation threshold rather than duplicating
        // the configured range in the group coordinator. Radar detection
        // range remains independent, exactly as in native DatalinkMode.
        internal static bool CanActivate(ARHSeeker seeker, float objectiveRange)
            => seeker != null && Finite(objectiveRange) && objectiveRange >= 0f && objectiveRange < ActiveRange(seeker);

        // The same surface return used by the 0.7.8 ARH adapter, without its
        // single-target cache or clock writes. Callers own scan scheduling.
        // Buildings remain supported for the existing adapter's other users.
        internal static float SurfaceReturn(ARHSeeker seeker, Missile missile, Unit target, float previousReturn, bool searchOnly = false)
        {
            if (seeker == null || missile == null || target == null || target.definition == null ||
                target.disabled || Jammed(seeker) && !HomeOnJam(seeker)) return 0f;
            RadarParams radar = seeker.GetRadarParams();
            GlobalPosition source = missile.GlobalPosition(), observed = target.GlobalPosition();
            Vector3 delta = observed - source;
            float distance = delta.magnitude;
            if (!Finite(source) || !Finite(observed) || !Finite(distance)) return 0f;
            float radarHeight = Mathf.Max(1f, observed.y + target.definition.height * .5f);
            float horizon = Mathf.Sqrt(12742000f * Mathf.Max(0f, source.y)) + Mathf.Sqrt(12742000f * radarHeight);
            float halfAngle = TrackingAngle(seeker);
            // Acquisition uses the same cone as the planner/threat corridor.
            // Once locked, native terminal tracking retains its wider gimbal;
            // the group's independent search is narrow even if its leader has
            // already established a lock on the originally ordered ship.
            if (missile.definition != null && missile.definition.jsonKey == "rsl_ashm" &&
                (searchOnly || missile.seekerMode != Missile.SeekerMode.activeLock))
                halfAngle = Mathf.Min(halfAngle, ResolutePikeMap.SearchHalfAngleDegrees);
            if (distance > radar.maxRange || distance > horizon ||
                Vector3.Angle(missile.transform.forward, delta) > halfAngle) return 0f;
            if (!NaturalSurfaceRadarVisibility.CanSee(missile.transform, target)) return -1f;
            if (previousReturn < radar.minSignal && distance < ReacquireRange(seeker)) return 0f;
            return radar.GetSignalStrength(delta.normalized, Mathf.Max(1f, distance), target.rb,
                Mathf.Max(.00001f, target.RCS), target is Ship ? .05f : 1f, 0f);
        }

        internal static bool CanReceive(ARHSeeker seeker, Missile missile, Unit target, GlobalPosition observedPosition)
        {
            if (seeker == null || missile == null || !missile.LocalSim || missile.disabled ||
                !(target is Ship) || target.disabled || target.definition == null || target.persistentID.NotValid ||
                missile.NetworkHQ == null || target.NetworkHQ == null || !ResoluteManualPikeOrders.AllowsTarget(missile, target) ||
                !Finite(observedPosition) || !Finite(missile.GlobalPosition()) || Target(seeker) is IRadarReturn) return false;
            GlobalPosition known = observedPosition + PositionError(seeker);
            if (!Finite(known) || !Finite((known - missile.GlobalPosition()).magnitude)) return false;
            Vector3 relative = observedPosition - missile.GlobalPosition();
            float angle = DatalinkAngle(seeker);
            // Native ARH only restricts this angle below 180 degrees. It does
            // not reject all datalink updates merely because isJammed is true.
            return Finite(relative) && (angle >= 180f || Vector3.Angle(missile.transform.forward, relative) <= angle);
        }

        internal static bool Assign(ARHSeeker seeker, Missile missile, Unit target,
            GlobalPosition observedPosition, Vector3 observedVelocity)
        {
            if (!CanReceive(seeker, missile, target, observedPosition) || !Finite(observedVelocity) ||
                missile.targetID == target.persistentID) return false;
            // Normal Pike targets and role-filtered home-on-jam emitters are
            // ships. Refuse an unexpected IRadarReturn target rather than leave
            // its private native chaff callback subscribed after reassignment;
            // CanReceive includes that guard for atomic whole-group planning.
            GlobalPosition known = observedPosition + PositionError(seeker);
            float range = (known - missile.GlobalPosition()).magnitude;
            if (!Finite(known) || !Finite(range)) return false;

            // All checks precede mutation. This runs synchronously on the local
            // simulation thread: native SetTarget observes the complete seeker
            // state and keeps its own network ID/attack accounting authoritative.
            Target(seeker) = target;
            Position(seeker) = known;
            Velocity(seeker) = PreviousVelocity(seeker) = observedVelocity;
            Acceleration(seeker) = Vector3.zero;
            Return(seeker) = 0f;
            Locked(seeker) = AchievedLock(seeker) = false;
            HomingTime(seeker) = MissingTime(seeker) = 0f;
            MultipleInbound(seeker) = false;
            Distance(seeker) = range;
            TimeToTarget(seeker) = range / Mathf.Max(10f, missile.speed);
            // Deliberately retain armed/guidance, jam accumulation/state,
            // positional error and both native sampling timestamps. Initialize
            // would duplicate callbacks and reset those lifetime controls.
            missile.SetTarget(target);
            // The group coordinator may run after this step's native Seek.
            // Keep the immediate navigation input on the same observation;
            // native Seek supplies its normal lead on the following step.
            missile.SetAimpoint(known, observedVelocity);
            missile.NetworkseekerMode = range < ActiveRange(seeker)
                ? Missile.SeekerMode.activeSearch : Missile.SeekerMode.passive;
            return true;
        }

        private static bool Finite(GlobalPosition value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
