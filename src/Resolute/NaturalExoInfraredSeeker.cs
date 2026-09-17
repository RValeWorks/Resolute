using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Only Bastion-X uses this adapter. The native IR terminal lead and flare
    // response remain authoritative; no launch-time long-range IR lock exists.
    internal sealed class NaturalExoInfraredSeeker : IRSeeker
    {
        public float AcquisitionRange = 10f * 1852f, TrackingHalfAngle = 55f;
        private Unit assignedTarget;
        private Unit subscribedTarget;
        private float nextSensorCheck, nextDatalinkCheck;
        private bool terminalTracking;
        internal bool OwnSeekerAcquired => terminalTracking && Source(this) != null && !Source(this).flare;
        internal string GuidancePhase = "uninitialized";
        private static readonly AccessTools.FieldRef<IRSeeker, IRSource> Source = AccessTools.FieldRefAccess<IRSeeker, IRSource>("IRTarget");
        private static readonly AccessTools.FieldRef<IRSeeker, GlobalPosition> Position = AccessTools.FieldRefAccess<IRSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<IRSeeker, Vector3> Velocity = AccessTools.FieldRefAccess<IRSeeker, Vector3>("knownVel");
        private static readonly AccessTools.FieldRef<IRSeeker, Vector3> PreviousVelocity = AccessTools.FieldRefAccess<IRSeeker, Vector3>("knownVelPrev");
        private static readonly AccessTools.FieldRef<IRSeeker, Vector3> Acceleration = AccessTools.FieldRefAccess<IRSeeker, Vector3>("knownAccel");
        private static readonly AccessTools.FieldRef<IRSeeker, bool> Guiding = AccessTools.FieldRefAccess<IRSeeker, bool>("guidance");
        private static readonly MethodInfo NativeFlare = AccessTools.Method(typeof(IRSeeker), "IRSeeker_OnTargetFlare");

        public override void Initialize(Unit target, GlobalPosition aimpoint)
        {
            Unsubscribe();
            assignedTarget = targetUnit = target;
            Source(this) = null; terminalTracking = false;
            Guiding(this) = false;
            Position(this) = aimpoint;
            Velocity(this) = PreviousVelocity(this) = Acceleration(this) = Vector3.zero;
            NaturalWeapons.Set(this, "topSpeed", missile.GetWeaponInfo().GetMaxSpeed());
            NaturalWeapons.Set(this, "errorOffset", Random.insideUnitSphere);
            NaturalWeapons.Set(this, "driftError", Vector3.zero);
            NaturalWeapons.Set(this, "dazzleAmount", 0f);
            NaturalWeapons.Set(this, "lastEvaluated", Time.timeSinceLevelLoad);
            missile.NetworkseekerMode = Missile.SeekerMode.passive;
            TrackingInfo track = target != null && missile.NetworkHQ != null ? missile.NetworkHQ.GetTrackingData(target.persistentID) : null;
            if (track != null) Position(this) = track.lastKnownPosition;
            RefreshObservedTrack();
            missile.SetAimpoint(Position(this), Velocity(this));
            nextSensorCheck = nextDatalinkCheck = Time.timeSinceLevelLoad;
        }

        public override string GetSeekerType() { return "Datalink / IR"; }

        public override void Seek()
        {
            if (missile == null || missile.disabled || !missile.LocalSim) return;
            if (missile.timeSinceSpawn > .4f)
            { Guiding(this) = true; missile.DeployFins(); }
            float now = Time.timeSinceLevelLoad;
            if (now >= nextSensorCheck)
            {
                nextSensorCheck = now + .2f;
                if (terminalTracking && !VisibleSource(Source(this)))
                { terminalTracking = false; Source(this) = null; Unsubscribe(); }
                if (!terminalTracking) TryAcquire();
            }
            if (terminalTracking && Source(this) != null)
            {
                // Native IR retains its positional noise, acceleration lead,
                // physical LOS checks and susceptibility to source/flare loss.
                base.Seek();
                GuidancePhase = OwnSeekerAcquired ? "infrared-terminal" : "infrared-countermeasure";
                return;
            }
            if (now >= nextDatalinkCheck)
            { nextDatalinkCheck = now + .2f; RefreshObservedTrack(); }
            Position(this) += Velocity(this) * Time.fixedDeltaTime;
            Vector3 delta = Position(this) - missile.GlobalPosition();
            Vector3 platform = delta.normalized * Mathf.Max(1f, missile.timeSinceSpawn < 3f ? missile.GetWeaponInfo().GetMaxSpeed() : missile.speed);
            Vector3 lead = TargetCalc.GetLeadVectorWithAccel(Position(this), missile.GlobalPosition(), Velocity(this), platform, Vector3.zero, 5f);
            GlobalPosition aim = Position(this) + lead;
            aim.y = Mathf.Max(0f, aim.y);
            missile.SetAimpoint(aim, Velocity(this));
            GetComponent<NaturalLoftGuidance>()?.Apply();
        }

        private void RefreshObservedTrack()
        {
            TrackingInfo track = assignedTarget != null && missile.NetworkHQ != null ? missile.NetworkHQ.GetTrackingData(assignedTarget.persistentID) : null;
            if (track == null || !track.Observed())
            { GuidancePhase = "inertial-track-memory"; return; }
            // Live position/velocity may be sampled ONLY while native HQ calls
            // this track observed. Once lost, the saved position is advanced
            // using the last saved velocity; no hidden target transform reads.
            Position(this) = track.GetPosition();
            Velocity(this) = assignedTarget.rb != null ? assignedTarget.rb.velocity : Vector3.zero;
            PreviousVelocity(this) = Velocity(this);
            Acceleration(this) = Vector3.zero;
            GuidancePhase = "observed-datalink";
        }

        private void TryAcquire()
        {
            if (assignedTarget == null || assignedTarget.disabled || !assignedTarget.HasIRSignature()) return;
            // The retained cue first brings the search to the terminal region;
            // actual source range, gimbal and LOS then decide own acquisition.
            Vector3 cue = Position(this) - missile.GlobalPosition();
            if (cue.sqrMagnitude > AcquisitionRange * AcquisitionRange * 1.21f) return;
            Vector3 actual = assignedTarget.GlobalPosition() - missile.GlobalPosition();
            if (!InterceptorFlightPolicy.InfraredEnvelope(actual.sqrMagnitude, AcquisitionRange, Vector3.Angle(transform.forward, actual), TrackingHalfAngle) ||
                !assignedTarget.LineOfSight(transform.position, 1000f)) return;
            IRSource source = assignedTarget.GetIRSource();
            if (source == null || source.flare || !VisibleSource(source)) return;
            targetUnit = assignedTarget;
            Source(this) = source;
            terminalTracking = true;
            NaturalWeapons.Set(this, "achievedLock", false);
            NaturalWeapons.Set(this, "lastEvaluated", Time.timeSinceLevelLoad - .3f);
            subscribedTarget = assignedTarget;
            subscribedTarget.onAddIRSource += OnFlare;
            missile.SetTarget(assignedTarget);
            if (proximityFuse) missile.SetProxyFuse(assignedTarget.GetRandomPart().transform, assignedTarget.rb);
        }

        private bool VisibleSource(IRSource source)
        {
            if (source == null || source.transform == null) return false;
            Vector3 delta = source.transform.position - transform.position;
            return InterceptorFlightPolicy.InfraredEnvelope(delta.sqrMagnitude, AcquisitionRange, Vector3.Angle(transform.forward, delta), TrackingHalfAngle) &&
                !Physics.Linecast(transform.position, source.transform.position, PhysicsLayers.StaticsMask);
        }
        private void OnFlare(IRSource source)
        {
            if (terminalTracking && Source(this) != null && Source(this).transform != null)
                NativeFlare.Invoke(this, new object[] { source });
        }
        private void Unsubscribe()
        {
            if (subscribedTarget != null) subscribedTarget.onAddIRSource -= OnFlare;
            subscribedTarget = null;
        }
        private void OnDisable() { Unsubscribe(); terminalTracking = false; }
        private void OnDestroy() { Unsubscribe(); }
    }

    [HarmonyPatch(typeof(IRSeeker), "RangeCoef")]
    internal static class NaturalExoInfraredCountermeasureRange
    {
        private static readonly AccessTools.FieldRef<IRSeeker, AnimationCurve> RangeFactor =
            AccessTools.FieldRefAccess<IRSeeker, AnimationCurve>("rangeFactor");
        private static bool Prefix(IRSeeker __instance, float targetDistance, ref float __result)
        {
            NaturalExoInfraredSeeker exo = __instance as NaturalExoInfraredSeeker;
            if (exo == null) return true;
            // Native IR normally uses its weapon's launch range. X's 1500nm
            // datalink envelope must not masquerade as its 10nm IR detector
            // range in the native flare strength comparison.
            __result = RangeFactor(__instance).Evaluate(targetDistance / Mathf.Max(1f, exo.AcquisitionRange));
            return false;
        }
    }
}
