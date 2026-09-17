using System;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace Resolute
{
    // One bounded evidence event per affected round. This observes a terminal
    // rise; it does not decide that terrain avoidance or native guidance is wrong.
    internal sealed class NaturalPikeClimbDiagnostics : MonoBehaviour
    {
        internal const int MissionLimit = 24;
        private static int emitted;
        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> Aim =
            AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");
        private Missile missile;
        private NaturalCruiseGuidance guidance;
        private NaturalCruiseController controller;
        private float nextCheck, terminalSince = -1f;
        private bool seenLow, recorded;
        private struct Sample
        {
            public float gameSeconds, heightAboveSeaM, verticalSpeedMps, commandedAltitudeM, commandedVerticalSpeedMps;
            public uint targetId;
            public string phase;
            public Missile.SeekerMode seekerMode;
            public bool nativeObservationUsable;
        }
        private Sample previous;
        private bool hasPrevious;

        static NaturalPikeClimbDiagnostics() { MissionManager.onMissionLoad += _ => ResetBudget(); }
        internal static void ResetBudget() { emitted = 0; }
        private void Awake()
        {
            missile = GetComponent<Missile>();
            guidance = GetComponent<NaturalCruiseGuidance>();
        }

        private void FixedUpdate()
        {
            if (!ResoluteDiagnostics.Enabled || recorded || emitted >= MissionLimit || missile == null || missile.disabled || !missile.LocalSim ||
                missile.definition == null || missile.definition.jsonKey != "rsl_ashm" || guidance == null ||
                Time.timeSinceLevelLoad < nextCheck) return;
            nextCheck = Time.timeSinceLevelLoad + .5f;
            if (controller == null) controller = GetComponent<NaturalCruiseController>();
            if (controller == null || !controller.TerminalReleased || !guidance.LaunchReady) return;
            if (terminalSince < 0f) terminalSince = Time.timeSinceLevelLoad;
            float height = missile.transform.position.y - Datum.LocalSeaY;
            Vector3 velocity = missile.rb != null ? missile.rb.velocity : Vector3.zero;
            seenLow |= height <= 20f;
            Sample current = new Sample {
                gameSeconds = Time.timeSinceLevelLoad, heightAboveSeaM = height, verticalSpeedMps = velocity.y,
                targetId = missile.targetID.Id, phase = guidance.FlightPhase,
                commandedAltitudeM = guidance.CommandedAltitude, commandedVerticalSpeedMps = guidance.CommandedVerticalSpeed,
                nativeObservationUsable = guidance.NativeObservationUsable, seekerMode = missile.seekerMode
            };
            bool rise = ShouldRecord(seenLow, Time.timeSinceLevelLoad - terminalSince, height, velocity.y,
                guidance.CommandedAltitude, guidance.CommandedVerticalSpeed);
            if (rise)
            {
                recorded = true; emitted++;
                try
                {
                    GlobalPosition aim = Aim(missile);
                    Vector3 desired = aim - missile.GlobalPosition();
                    var snapshot = new {
                        eventNumber = emitted, eventLimit = MissionLimit, id = missile.persistentID.Id,
                        ageSeconds = missile.timeSinceSpawn, previous = hasPrevious ? (object)previous : null, current,
                        position = V(missile.GlobalPosition().AsVector3()), velocityMps = V(velocity),
                        noseDirection = V(missile.transform.forward), guidanceAim = V(aim.AsVector3()),
                        currentTargetId = missile.targetID.Id, seekerMode = missile.seekerMode.ToString(), guidance = guidance.Capture(),
                        group = missile.GetComponent<NaturalPikeGroupTargeting>()?.Capture(),
                        desiredCorridor = Probe(desired), momentumCorridor = Probe(velocity),
                        interpretation = "Terminal rise observed after sea-skimming or an eight-second terminal settling period. Phase, terrain demand, prior target and current aim are evidence; this event does not assert the cause or a miss."
                    };
                    Write("[Pike terminal rise] " + JsonConvert.SerializeObject(snapshot));
                }
                catch (Exception error)
                {
                    try { Write("[Pike terminal rise] event=" + emitted + "; snapshot unavailable: " + error.GetType().Name); }
                    catch { }
                }
            }
            previous = current;
            hasPrevious = true;
        }

        internal static bool ShouldRecord(bool lowObserved, float terminalSeconds, float height, float verticalSpeed,
            float demandedHeight, float demandedVerticalSpeed)
        {
            if (!Finite(terminalSeconds) || !Finite(height) || !Finite(verticalSpeed) ||
                !Finite(demandedHeight) || !Finite(demandedVerticalSpeed) || !lowObserved && terminalSeconds < 8f) return false;
            return height > 35f && verticalSpeed > 2f || demandedHeight > 35f && demandedVerticalSpeed > 2f;
        }

        private object Probe(Vector3 direction)
        {
            if (direction.sqrMagnitude < 1f) return null;
            float distance = Mathf.Min(3000f, Mathf.Max(100f, direction.magnitude));
            int mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask | (int)PhysicsLayers.ExclusionZonesMask;
            bool native = Physics.SphereCast(missile.transform.position, 4f, direction.normalized,
                out RaycastHit raw, distance, mask, QueryTriggerInteraction.Ignore);
            bool filtered = PikeNavigationObstacles.SphereCast(missile, missile.transform.position, 4f,
                direction.normalized, out RaycastHit retained, distance, mask, QueryTriggerInteraction.Ignore);
            return new { distanceM = distance, nativeFirstHit = native ? Hit(raw) : null,
                filteredFirstHit = filtered ? Hit(retained) : null };
        }

        private static object Hit(RaycastHit hit)
        {
            Collider collider = hit.collider;
            UnitPart part = collider != null ? collider.GetComponentInParent<UnitPart>() : null;
            Unit owner = part != null ? part.parentUnit : collider != null ? collider.GetComponentInParent<Unit>() : null;
            return new { name = collider != null ? collider.name : null, distanceM = hit.distance,
                point = V(hit.point), ownerId = owner != null ? owner.persistentID.Id : 0u,
                ownerType = owner != null ? owner.GetType().Name : null, detachedPart = part != null && part.IsDetached() };
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static void Write(string message)
        {
            if (Plugin.Instance != null) Plugin.Instance.LogStartupWarning(message);
            else Debug.LogWarning(message);
        }
    }
}
