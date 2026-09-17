using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace Resolute
{
    internal sealed class NaturalReactionControl : MonoBehaviour
    {
        public float MaxAcceleration, MaxTurnRate, ControlSeconds;
        public MeshRenderer[] Jets;
        public Vector3[] JetOffsets, JetDirections;
        public Vector3 FlightCentre;
        public float FlightLength, SeparationSeconds, NearSeekerRange;
        private Missile missile;
        private MissileSeeker seeker;
        private NaturalLoftGuidance loft;
        private float usedControlSeconds;
        private bool separationApplied;
        internal float ActiveSeconds, AppliedDeltaV, PeakAcceleration;
        internal Vector3 LastAcceleration;
        internal Vector3 LastAngularAcceleration;
        internal bool JetsSeen;
        internal float UsedControlSeconds => usedControlSeconds;
        internal float RemainingControlSeconds => Mathf.Max(0f, ControlSeconds - usedControlSeconds);
        internal string ControlState = "not-started";
        internal float AerodynamicAuthority
        {
            get
            {
                Vector3 acceleration, angular; float blend, charge; string reason;
                // Dense-air terminal RCS supplements the fins. Only the
                // thin-air blend fades native aerodynamic attitude authority.
                return TryCommands(out acceleration, out angular, out blend, out charge, out reason) ?
                    1f - Mathf.InverseLerp(.1f, .015f, missile.airDensity) : 1f;
            }
        }

        internal static void Configure(GameObject prefab, NaturalWeapons.SourceWeapon source)
        {
            var control = prefab.AddComponent<NaturalReactionControl>();
            control.MaxAcceleration = source.Guidance("MaxTurnG", 60f) * 9.81f;
            control.MaxTurnRate = source.Guidance("MaxTurnRate", 50f) * Mathf.Deg2Rad;
            control.ControlSeconds = Mathf.Max(12f, source.Guidance("AccelerationTime", 6f) + source.Guidance("SustainerAccelerationTime", 12f));
            float center = (source.stages["flight"].min[2] + source.stages["flight"].max[2]) * .5f;
            control.FlightCentre = Vector3.forward * center;
            control.FlightLength = source.stages["flight"].max[2] - source.stages["flight"].min[2];
            control.SeparationSeconds = source.switchSeconds;
            control.NearSeekerRange = source.Guidance(source.key == "rsl_bmd_exo" ? "SeekerPassiveRange" : "SeekerActiveRange", 10f) * 1852f;
            var jets = new MeshRenderer[12];
            control.JetOffsets = new Vector3[jets.Length]; control.JetDirections = new Vector3[jets.Length];
            float offset = control.FlightLength * .22f;
            GameObject flight = prefab.GetComponent<NaturalWeaponPhase>().FlightModel;
            for (int n = 0; n < jets.Length; n++)
            {
                Vector3 direction, point;
                float z = n < 8 ? center + (n < 4 ? offset : -offset) : center;
                float radius = BodyRadius(flight, prefab.transform, z,
                    Mathf.Max(Mathf.Abs(source.stages["flight"].min[0]), source.stages["flight"].max[0])) + .004f;
                RcsJetLayout.Get(n, center, offset, radius, out point, out direction);
                control.JetOffsets[n] = point - control.FlightCentre; control.JetDirections[n] = direction;
                NaturalRcsSurfaceDetails.Add(flight.transform, prefab.transform, point,
                    new Vector3(point.x, point.y, 0f).normalized, n < 4 ? "FWD" : n < 8 ? "AFT" : n < 10 ? "L" : "R", n >= 8);
                jets[n] = NaturalRcsPlume.Add(prefab.transform, point, direction, n);
            }
            control.Jets = jets;
        }
        private static float BodyRadius(GameObject model, Transform root, float z, float fallback)
        {
            var radii = new List<float>();
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Vector3[] vertices = filter.sharedMesh.vertices;
                int[] indices = filter.sharedMesh.triangles;
                Matrix4x4 matrix = root.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                for (int n = 0; n < indices.Length; n += 3)
                for (int side = 0; side < 3; side++)
                {
                    Vector3 a = matrix.MultiplyPoint3x4(vertices[indices[n + side]]);
                    Vector3 b = matrix.MultiplyPoint3x4(vertices[indices[n + (side + 1) % 3]]);
                    // Include endpoints on an exact casing section ring.
                    if ((a.z - z) * (b.z - z) > 0f || Mathf.Abs(a.z - b.z) < .0001f) continue;
                    Vector3 point = Vector3.Lerp(a, b, (z - a.z) / (b.z - a.z));
                    radii.Add(new Vector2(point.x, point.y).magnitude);
                }
            }
            radii.Sort();
            return radii.Count > 0 ? radii[radii.Count / 2] : fallback;
        }
        private void Awake() { missile = GetComponent<Missile>(); seeker = GetComponent<MissileSeeker>(); loft = GetComponent<NaturalLoftGuidance>(); }
        private void FixedUpdate()
        {
            LastAcceleration = LastAngularAcceleration = Vector3.zero;
            if (!separationApplied && missile != null && missile.timeSinceSpawn >= SeparationSeconds && !missile.boosterIsAttached)
            {
                // The detached stage has its own capsule/COM. Native mass and
                // motor consumption continue unchanged; banks are symmetric
                // about the remaining body's actual physics centre.
                CapsuleCollider capsule = GetComponent<CapsuleCollider>();
                if (capsule != null) { capsule.center = FlightCentre; capsule.height = FlightLength; }
                if (missile.rb != null) missile.rb.ResetCenterOfMass();
                separationApplied = true;
            }
            Vector3 acceleration, angularAcceleration; float blend, charge; string reason;
            if (!TryCommands(out acceleration, out angularAcceleration, out blend, out charge, out reason))
            { ControlState = reason; ShowJets(Vector3.zero); return; }
            missile.rb.AddForce(acceleration, ForceMode.Acceleration);
            missile.rb.AddTorque(angularAcceleration, ForceMode.Acceleration);
            usedControlSeconds = Mathf.Min(ControlSeconds, usedControlSeconds + charge);
            LastAcceleration = acceleration;
            LastAngularAcceleration = angularAcceleration;
            ControlState = charge > 0f ? "active" : "coasting";
            if (acceleration.sqrMagnitude > .01f)
            {
                ActiveSeconds += Time.fixedDeltaTime;
                AppliedDeltaV += acceleration.magnitude * Time.fixedDeltaTime;
                PeakAcceleration = Mathf.Max(PeakAcceleration, acceleration.magnitude);
            }
            ShowJets(transform.InverseTransformDirection(-acceleration));
        }

        private bool TryCommands(out Vector3 acceleration, out Vector3 angularAcceleration, out float blend, out float charge, out string reason)
        {
            acceleration = angularAcceleration = Vector3.zero; blend = charge = 0f; reason = "ready";
            if (!enabled || missile == null || missile.disabled || missile.rb == null) return Stop("inactive-missile", out reason);
            if (!missile.LocalSim) return Stop("remote-simulation", out reason);
            if (!Finite(missile.timeSinceSpawn) || missile.timeSinceSpawn < SeparationSeconds || missile.boosterIsAttached) return Stop("launch-delay", out reason);
            if (!Finite(MaxAcceleration) || MaxAcceleration <= 0f || !Finite(MaxTurnRate) || MaxTurnRate < 0f ||
                !Finite(ControlSeconds) || ControlSeconds <= 0f || !Finite(usedControlSeconds) || usedControlSeconds < 0f ||
                !Finite(Time.fixedDeltaTime) || Time.fixedDeltaTime <= 0f) return Stop("invalid-control-configuration", out reason);
            if (usedControlSeconds >= ControlSeconds) return Stop("control-budget-exhausted", out reason);
            if (!Finite(missile.airDensity)) return Stop("invalid-air-density", out reason);
            // Native BallisticMissileGuidance enables RCS below density 0.1.
            blend = Mathf.InverseLerp(.1f, .015f, missile.airDensity);
            if (missile.targetID.NotValid || seeker == null || !(seeker is ARHSeeker || seeker is IRSeeker) ||
                !(bool)NaturalWeapons.Get(seeker, "guidance")) return Stop("no-native-guidance", out reason);
            Unit target = (Unit)NaturalWeapons.Get(seeker, "targetUnit");
            if (target == null || target.disabled || target.NetworkHQ == null || missile.NetworkHQ == null)
                return Stop("no-live-hostile-native-target", out reason);
            // Do not demand active radar lock: a retained native datalink target
            // remains valid. Never replace the native track with live coordinates.
            Vector3 velocity = missile.rb.velocity;
            Vector3 angularVelocity = missile.rb.angularVelocity;
            Vector3 aim = (GlobalPosition)NaturalWeapons.Get(missile, "aimPoint") - missile.GlobalPosition();
            Vector3 range = (GlobalPosition)NaturalWeapons.Get(seeker, "knownPos") - missile.GlobalPosition();
            Vector3 trackVelocity = (Vector3)NaturalWeapons.Get(seeker, "knownVel");
            if (!Finite(velocity) || !Finite(angularVelocity) || !Finite(aim) || !Finite(range) || !Finite(trackVelocity) ||
                !Finite(velocity.magnitude) || !Finite(range.magnitude) || !Finite(transform.forward)) return Stop("invalid-flight-or-track-state", out reason);
            if (velocity.sqrMagnitude < 100f) return Stop("insufficient-speed", out reason);
            bool ownLock = seeker is NaturalExoInfraredSeeker exo ? exo.OwnSeekerAcquired :
                seeker is ARHSeeker && missile.seekerMode == Missile.SeekerMode.activeLock;
            blend = InterceptorFlightPolicy.RcsBlend(missile.airDensity, ownLock && range.sqrMagnitude <= NearSeekerRange * NearSeekerRange);
            if (blend <= 0f) return Stop("aerodynamic-flight", out reason);
            Vector3 destination = aim.normalized;
            Vector3 requested = Vector3.ProjectOnPlane(destination * velocity.magnitude - velocity, velocity.normalized) * 2f;
            if (loft == null || !loft.Active)
            {
                // Use only the native seeker's retained track, including its
                // errors, lock loss and countermeasure behavior. A predicted
                // miss correction removes trajectory lag in thin atmosphere.
                Vector3 relativeVelocity = trackVelocity - velocity;
                float closing = -Vector3.Dot(range.normalized, relativeVelocity);
                if (closing > 1f && range.sqrMagnitude > 1f)
                {
                    float timeToGo = Mathf.Max(.08f, range.magnitude / closing);
                    Vector3 predictedMiss = range + relativeVelocity * timeToGo;
                    requested = Vector3.ProjectOnPlane(predictedMiss, velocity.normalized) * (3f / (timeToGo * timeToGo));
                }
            }
            acceleration = Vector3.ClampMagnitude(requested, MaxAcceleration) * blend;
            // Physical transverse thrust changes the trajectory even where
            // aerodynamic lift vanishes. No position/velocity is overwritten.
            // Keep body thrust aligned with the physical trajectory while the
            // lateral jets remove miss distance. Native fin authority fades
            // with density, so two attitude controllers cannot fight in vacuum.
            Vector3 wantedRate = Vector3.ClampMagnitude(Vector3.Cross(transform.forward, velocity.normalized) * 4f, MaxTurnRate);
            angularAcceleration = Vector3.ClampMagnitude((wantedRate - angularVelocity) * 3f, 6f) * blend;
            if (!Finite(acceleration) || !Finite(angularAcceleration) || !Finite(acceleration.magnitude) || !Finite(angularAcceleration.magnitude))
                return Stop("invalid-control-command", out reason);
            // Both actuators draw from the same normalized command budget. This
            // closes the unlimited pure-attitude loophole without changing gains,
            // acceleration caps or the configured number of control seconds.
            charge = Time.fixedDeltaTime * Mathf.Clamp01(Mathf.Max(acceleration.magnitude / MaxAcceleration, angularAcceleration.magnitude / 6f));
            float remaining = ControlSeconds - usedControlSeconds;
            if (charge > remaining)
            {
                float scale = remaining / charge;
                acceleration *= scale; angularAcceleration *= scale; blend *= scale; charge = remaining;
            }
            return true;
        }
        private void OnDisable() { LastAcceleration = LastAngularAcceleration = Vector3.zero; ControlState = "component-disabled"; ShowJets(Vector3.zero); }
        private static bool Stop(string value, out string reason) { reason = value; return false; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private void ShowJets(Vector3 exhaust)
        {
            if (Jets == null) return;
            for (int n = 0; n < Jets.Length; n++)
            {
                if (Jets[n] == null) continue;
                Vector3 direction = JetDirections != null && n < JetDirections.Length ? JetDirections[n] : Vector3.zero;
                Vector3 offset = JetOffsets != null && n < JetOffsets.Length ? JetOffsets[n] : Vector3.zero;
                Vector3 localAngular = transform.InverseTransformDirection(LastAngularAcceleration);
                // The two axial banks provide translation and pitch/yaw. Only
                // the compact flank pairs supply roll; their radial components
                // cancel when the diagonal pair fires together.
                float translation = n < 8 ? Mathf.Max(0f, Vector3.Dot(exhaust, direction) / Mathf.Max(1f, MaxAcceleration)) : 0f;
                Vector3 torqueAxis = Vector3.Cross(offset, -direction);
                float attitude = n < 8 ? Vector3.Dot(new Vector3(localAngular.x, localAngular.y, 0f), torqueAxis.normalized) / 6f :
                    localAngular.z * Mathf.Sign(torqueAxis.z) / 6f;
                bool fire = translation + attitude > .001f;
                if (Jets[n].enabled != fire) Jets[n].enabled = fire;
                JetsSeen |= fire && Jets[n].enabled;
            }
        }
    }

    [HarmonyPatch(typeof(Missile), "ApplyAero")]
    internal static class NaturalReactionFinAuthorityPatch
    {
        private static void Prefix(Missile __instance, out Vector3? __state)
        {
            __state = null;
            NaturalReactionControl control = __instance.GetComponent<NaturalReactionControl>();
            float authority = control != null ? control.AerodynamicAuthority : 1f;
            if (authority >= 1f) return;
            Vector3 inputs = (Vector3)NaturalWeapons.Get(__instance, "inputs");
            __state = inputs;
            NaturalWeapons.Set(__instance, "inputs", inputs * authority);
        }
        private static void Postfix(Missile __instance, Vector3? __state)
        {
            if (__state.HasValue) NaturalWeapons.Set(__instance, "inputs", __state.Value);
        }
        private static Exception Finalizer(Missile __instance, Vector3? __state, Exception __exception)
        {
            if (__state.HasValue && __instance != null) NaturalWeapons.Set(__instance, "inputs", __state.Value);
            return __exception;
        }
    }
}
