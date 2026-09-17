using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Supplies aimpoints to the native steering/aerodynamic model. Terrain is
    // observed along the intended corridor AND the current momentum corridor;
    // the controller never translates a missile or replaces its velocity.
    internal sealed class NaturalCruiseGuidance : MonoBehaviour
    {
        public float CruiseAltitude, TerminalAltitude, TerminalRange, NominalSpeed;
        public float InitialFlightSeconds, MaximumDescentDegrees;
        public float CeilingAltitude, SeaSkimStartRange, FinalApproachRange, MaximumLoftDegrees;
        public float LaunchTurnRate, LaunchGLimit;
        public bool NativePikeLaunch;
        public float NativeLaunchFinDelay, LaunchClearanceM;
        public bool LandAttack;
        private Missile missile;
        private ARHSeeker seeker;
        private NaturalWeaponPhase weaponPhase;
        private NaturalCruiseTactics tactics;
        private Unit terminalTarget;
        private bool initialized, verticalLaunch, clearedLauncher;
        private float launchAltitude, nextProbe, terrainFloor, currentGround, routeHoldUntil, approachGroundPeak;
        private bool terrainObstacleActive;
        private float terrainObstacleDeadline, terrainObstacleClearance;
        private bool lostTargetTerrainMode;
        private float nextLostTargetTerrainProbe;
        private int lostTargetTerrainFrames;
        private Vector3 routeDirection;
        private int routeSide;
        private float previousGuideTime = -1f;
        private PersistentID observedTarget;
        private float closestTerminalRange = float.MaxValue;
        private bool closePassObserved;
        private readonly PikeTerminalPass terminalPass = new PikeTerminalPass();
        private readonly PikeFinalCorrection finalCorrection = new PikeFinalCorrection();
        private float nextTerminalRadarUpdate;
        private bool launchRateApplied, directTerminalClear, terrainApproachObserved;
        private bool launchSettled, launchFinsDeployed;
        private bool seaSkimSelected;
        private bool cruiseCorridorObservation;
        private float nextTerminalSteeringEnvelope, terminalSteeringEnvelope;
        private int terminalSteeringEnvelopeUpdates, terminalSteeringLimitedFrames;
        private float normalTurnRate, normalGLimit, nextTerminalProbe;
        private Collider terminalCollider;
        private UnitPart terminalNativePart;
        private ShipPart terminalNativeHull;
        private int nativePartSelectionAttempts, nativePartSelections, nativePartInvalidations, nativeSurfaceHeightInvalidations;
        private float nativePartSelectedAt = -1f;
        private int surfaceReservationChecks, surfaceReservationRejections, selectedSurfacePeers;
        private float selectedSurfaceMargin = float.PositiveInfinity, selectedCapsuleRadius;
        private float lastRejectedSurfaceMargin;
        private PersistentID lastRejectedSurfacePeer;
        private static readonly AccessTools.FieldRef<Missile, Unit> NativeCollisionTarget = AccessTools.FieldRefAccess<Missile, Unit>("target");
        private static readonly AccessTools.FieldRef<Missile, Vector3> NativeCollisionTargetVelocity = AccessTools.FieldRefAccess<Missile, Vector3>("targetVel");
        private bool terminalSurfaceSelected;
        private Vector3 terminalSurfaceLocal;
        private GlobalPosition terminalAim, terminalPhysicalSurface, terminalBaseAim;
        private static readonly float[] Fractions = { 0f, .025f, .06f, .11f, .18f, .27f, .38f, .51f, .66f, .82f, 1f };
        private static readonly int[] CandidateDirections = { -2, -1, 1, 2 };
        private static readonly AccessTools.FieldRef<Missile, float> CurrentFinArea = AccessTools.FieldRefAccess<Missile, float>("currentFinArea");
        private static readonly AccessTools.FieldRef<Missile, float> CurrentThrust = AccessTools.FieldRefAccess<Missile, float>("engineCurrentThrust");
        private static readonly AccessTools.FieldRef<Missile, float> Throttle = AccessTools.FieldRefAccess<Missile, float>("throttle");
        private static readonly AccessTools.FieldRef<Missile, bool> ReachedOnTarget = AccessTools.FieldRefAccess<Missile, bool>("reachedOnTarget");
        private static readonly AccessTools.FieldRef<Missile, float> MaximumTurnRate = AccessTools.FieldRefAccess<Missile, float>("maxTurnRate");
        private static readonly AccessTools.FieldRef<Missile, float> MaximumGLimit = AccessTools.FieldRefAccess<Missile, float>("gLimit");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> RadarGuidance = AccessTools.FieldRefAccess<ARHSeeker, bool>("guidance");
        private static readonly AccessTools.FieldRef<ARHSeeker, bool> RadarLocked = AccessTools.FieldRefAccess<ARHSeeker, bool>("radarLockEstablished");
        private static readonly AccessTools.FieldRef<ARHSeeker, GlobalPosition> RadarPosition = AccessTools.FieldRefAccess<ARHSeeker, GlobalPosition>("knownPos");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> RadarVelocity = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownVel");
        private static readonly AccessTools.FieldRef<ARHSeeker, Vector3> RadarAcceleration = AccessTools.FieldRefAccess<ARHSeeker, Vector3>("knownAccel");
        private static readonly AccessTools.FieldRef<ARHSeeker, float> RadarMaximumLead = AccessTools.FieldRefAccess<ARHSeeker, float>("maxLead");

        internal string FlightPhase = "launch-clearance";
        internal float CommandedAltitude, CommandedVerticalSpeed, MinimumObservedClearance = float.MaxValue;
        internal float MaximumDescentSpeed, MaximumRouteAngle, LowCruiseSeconds, FirstLowCruiseTime = -1f;
        internal int TerrainSamples, CorridorChecks, TerrainAvoidanceEvents;
        internal bool TerrainAvoidanceActive;
        internal bool TerminalMissSelfDestruct;
        internal bool HasMissedTerminalPass => missile != null && terminalPass.Passed(missile.targetID.Id);
        internal float CurrentTerrainAltitude => currentGround;
        internal float CurrentTerrainFloor => terrainFloor;
        internal float ApproachGroundPeak => approachGroundPeak;
        internal bool DirectTerminalActive;
        internal float FirstDirectTerminalTime = -1f;
        internal float DirectTerminalTerrainFloorAtHandoff;
        internal int DirectTerminalChecks, DirectTerminalBlockedChecks;
        internal string TerminalPathBlockReason, TerminalBlockingCollider;
        internal float TerminalAimGround;
        internal GlobalPosition TerminalAim => terminalAim;
        internal GlobalPosition TerminalPhysicalSurface => terminalPhysicalSurface;
        internal GlobalPosition TerminalBaseAim => terminalBaseAim;
        internal GlobalPosition NativeRadarPosition => seeker != null ? RadarPosition(seeker) : default(GlobalPosition);
        internal string TerminalColliderName => terminalCollider != null ? terminalCollider.name : null;
        internal float ActiveTurnRate => missile != null ? MaximumTurnRate(missile) : 0f;
        internal bool LaunchReady => clearedLauncher && launchSettled && missile != null && !missile.boosterIsAttached;
        internal bool NativeObservationUsable => missile != null && seeker != null && missile.targetID.IsValid &&
            RadarGuidance(seeker) && (!RadarLocked(seeker) || missile.seekerMode == Missile.SeekerMode.activeLock);
        internal float ObservedRange
        {
            get
            {
                if (missile == null || seeker == null) return float.PositiveInfinity;
                GlobalPosition point;
                if (!TryMidcourseGroupPoint(out point)) point = RadarPosition(seeker);
                Vector3 observed = point - missile.GlobalPosition(); observed.y = 0f;
                return observed.magnitude;
            }
        }

        private bool TryMidcourseGroupPoint(out GlobalPosition point)
        {
            point = default(GlobalPosition);
            if (!IsPike || !NativeObservationUsable && !ResoluteStrikeOrders.HasAreaObjective(missile)) return false;
            NaturalCruiseController controller = GetComponent<NaturalCruiseController>();
            if (controller != null && controller.TerminalReleased) return false;
            NaturalPikeGroupTargeting targeting = GetComponent<NaturalPikeGroupTargeting>();
            Missile leader;
            if (targeting != null && (!targeting.AwaitingAssignment ||
                !targeting.TryGetFormationLeader(out leader) || leader != missile)) return false;
            // Before the first formation Bind, the ship-issued order is still
            // the launch's common destination. Later only its actual leader
            // uses this point; wingmen explicitly follow their leader.
            // The order API enforces native observation/datalink constraints.
            return ResoluteStrikeOrders.TryGetGuidancePoint(missile, out point, out Vector3 course);
        }
        internal void PrepareProfile(float range)
        {
            // Once descending, a retarget or noisy observation cannot send a
            // sea-skimming round back into the high-altitude cruise segment.
            if ((NativeObservationUsable || ResoluteStrikeOrders.HasAreaObjective(missile)) && PikeFlightProfile.Finite(range) && range <= SeaSkimStartRange)
                seaSkimSelected = true;
        }

        private struct Path
        {
            internal float Floor, Peak, GroundPeak, Penalty;
            internal Vector3 Direction;
        }

        internal static void Configure(GameObject prefab, NaturalWeapons.SourceWeapon source)
        {
            var profile = prefab.AddComponent<NaturalCruiseGuidance>();
            profile.CruiseAltitude = source.Guidance("SeaSkimmingAlt", 40f) * .3048f;
            profile.TerminalAltitude = source.Guidance("TerminalAlt", 30f) * .3048f;
            profile.TerminalRange = source.Guidance("TerminalApproachDist", 12f) * 1852f;
            profile.NominalSpeed = source.speedMps;
            profile.InitialFlightSeconds = Mathf.Clamp(source.Guidance("InitialFlightPhaseDuration", 3.05f), 1f, 6f);
            profile.MaximumDescentDegrees = Mathf.Clamp(source.Guidance("SeaSkimmingMaxDescentAngle", 25f), 5f, 30f);
            profile.CeilingAltitude = source.Guidance("MaxLoftAlt", 15000f) * .3048f;
            profile.SeaSkimStartRange = source.Guidance("SeaSkimmingStartDistToTarget", 200f) * 1852f;
            profile.FinalApproachRange = source.Guidance("FinalFlightPhaseDistToTarget", 35f) * 1852f;
            profile.MaximumLoftDegrees = source.Guidance("MaxLoftAngle", 25f);
            profile.LandAttack = source.key == "rsl_cruise";
            profile.LaunchTurnRate = profile.LandAttack ? source.Guidance("LaunchTurnRate", 50f) : 0f;
            if (source.key == "rsl_ashm")
            {
                if (ResoluteDiagnostics.Enabled) prefab.AddComponent<NaturalPikeClimbDiagnostics>();
                NaturalSurfaceLaunch native = prefab.GetComponent<NaturalSurfaceLaunch>();
                if (native == null)
                    throw new System.InvalidOperationException("Pike requires the native AShM surface launch profile.");
                native.Validate();
                profile.NativePikeLaunch = true;
                profile.InitialFlightSeconds = native.GuidanceDelay;
                profile.NativeLaunchFinDelay = native.FinDelay;
                profile.LaunchTurnRate = native.TurnRate;
                profile.LaunchGLimit = native.GLimit;
                // Full authored launch length above the spawn centre gives the
                // longer Pike room to turn; there is no invented 55m apex.
                profile.LaunchClearanceM = source.stages["launch"].max[2] - source.stages["launch"].min[2];
            }
        }

        private void Awake()
        {
            missile = GetComponent<Missile>(); seeker = GetComponent<ARHSeeker>(); weaponPhase = GetComponent<NaturalWeaponPhase>();
            tactics = GetComponent<NaturalCruiseTactics>();
            // Native Spawner.Instantiate supplies the launch pose before Awake.
            // Recording it on the first seek would add an extra ascent margin.
            launchAltitude = transform.GlobalPosition().y;
            verticalLaunch = transform.forward.y > .55f;
        }

        internal GlobalPosition Guide(GlobalPosition destination)
        {
            // Accepted terminal navigation or direct pursuit explicitly renews
            // its steering authority. Any earlier return restores authored caps.
            RestoreFinalCorrectionAuthority();
            if (missile == null || missile.disabled || !missile.LocalSim || seeker == null || !RadarGuidance(seeker))
            {
                RestoreLaunchTurnRate();
                return destination;
            }
            if (missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile))
            {
                LostTarget();
                // Native ARH's invalid-ID branch supplies its extrapolated
                // aimpoint. Keep it intact so native MissedTarget can retire
                // the missile instead of following our moving local waypoint.
                return destination;
            }
            if (ReturnNativeAfterEmptySearch()) return destination;
            lostTargetTerrainMode = false;
            GlobalPosition current = missile.GlobalPosition();
            missile.targetID.TryGetUnit(out terminalTarget);
            if (observedTarget != missile.targetID)
            {
                observedTarget = missile.targetID; closestTerminalRange = float.MaxValue; closePassObserved = false;
                ClearTerminalSurface(); nextTerminalProbe = 0f;
                terrainApproachObserved = false;
            }
            GlobalPosition trackedPosition = RadarPosition(seeker), hqPosition = default(GlobalPosition);
            bool accurateHqTrack = terminalTarget != null && missile.NetworkHQ != null &&
                missile.NetworkHQ.IsTargetBeingTracked(terminalTarget) && missile.NetworkHQ.IsTargetPositionAccurate(terminalTarget, 500f) &&
                missile.NetworkHQ.TryGetKnownPosition(terminalTarget, out hqPosition);
            if (accurateHqTrack && missile.seekerMode != Missile.SeekerMode.activeLock) trackedPosition = hqPosition;
            // After an off-gimbal pass native ARH replaces knownPos with a
            // forward navigation point. A current accurate HQ observation is
            // still a real target reference, so use it for pass retirement.
            // A live radar lock takes precedence over the optional HQ track,
            // whose accepted position accuracy can be substantially coarser.
            Vector3 trackedVector = trackedPosition - current;
            float trackedRange = trackedVector.magnitude;
            float targetRadius = terminalTarget != null ? Mathf.Max(0f, terminalTarget.maxRadius) : 0f;
            bool independentAttack = !IsPike || GetComponent<NaturalCruiseController>()?.TerminalReleased == true;
            bool validTerminalTrack = independentAttack && observedTarget.IsValid && terminalTarget != null && !terminalTarget.disabled &&
                (missile.seekerMode == Missile.SeekerMode.activeLock || accurateHqTrack);
            if (IsPike)
                // Pre-lock native datalink also supplies a legitimate goal.
                // A weak terminal return does not: TerminalMode can replace
                // knownPos with a moving 1 km forward fallback. The existing
                // NativeObservationUsable distinction excludes that fallback.
                terminalPass.Observe(missile.targetID.Id, independentAttack,
                    terminalTarget != null && !terminalTarget.disabled && (NativeObservationUsable || accurateHqTrack), trackedPosition,
                    RadarVelocity(seeker), targetRadius, current, missile.rb.velocity, missile.speed,
                    missile.timeSinceSpawn, NaturalCruiseDatalink.LockPerseverance(seeker));
            if (!IsPike && validTerminalTrack && trackedRange < Mathf.Max(targetRadius + 50f, Mathf.Max(300f, missile.speed * .4f))) closePassObserved = true;
            if (!IsPike && closePassObserved && validTerminalTrack)
            {
                closestTerminalRange = Mathf.Min(closestTerminalRange, trackedRange);
                float passedMargin = Mathf.Max(targetRadius + 20f, Mathf.Max(80f, missile.speed * .2f));
                if (trackedRange > closestTerminalRange + passedMargin &&
                    Vector3.Dot(trackedVector, missile.rb.velocity) < 0f)
                {
                    TerminalMissSelfDestruct = true;
                    missile.Detonate(missile.rb.velocity, false, false);
                    return destination;
                }
            }
            // This is a navigation destination, never a fabricated seeker
            // target or radar lock. Use the same point for profile distance
            // above and the leader's route until independent assignment.
            if (TryMidcourseGroupPoint(out GlobalPosition groupPoint)) destination = groupPoint;
            Vector3 targetVector = destination - current; targetVector.y = 0f;
            float range = targetVector.magnitude;
            if (range < 1f && ResoluteStrikeOrders.TryGetAreaPoint(missile, out GlobalPosition area) &&
                ResoluteStrikeOrders.TryGetOrder(missile, out uint areaGroup, out GlobalPosition areaPoint, out Vector3 areaCourse))
            {
                destination = NaturalPikeAreaSearch.NavigationPoint(missile, area, areaCourse);
                targetVector = destination - current; targetVector.y = 0f; range = targetVector.magnitude;
            }
            if (range < 1f) return destination;
            Vector3 targetDirection = targetVector / range;
            float delta = previousGuideTime >= 0f ? Mathf.Clamp(missile.timeSinceSpawn - previousGuideTime, 0f, .1f) : Time.fixedDeltaTime;
            previousGuideTime = missile.timeSinceSpawn;
            if (!initialized)
            {
                initialized = true;
                routeDirection = targetDirection;
            }
            bool nativeCruiseObservation =
                !NaturalCruiseController.For(missile).TerminalReleased;
            if (missile.timeSinceSpawn >= nextProbe || cruiseCorridorObservation != nativeCruiseObservation)
            {
                float interval = .28f + (missile.persistentID.Id % 5) * .015f;
                nextProbe = missile.timeSinceSpawn + interval;
                cruiseCorridorObservation = nativeCruiseObservation;
                if (nativeCruiseObservation)
                {
                    // Native TerrainWaypoint already samples the cruise route.
                    // The dense parallel corridor was telemetry only and cost
                    // dozens of extra raycasts per missile every 0.3 s. Retain
                    // that evidence only in the explicitly enabled live trial.
                    if (NaturalCruiseNativeExperiment.Enabled) ObserveCruiseCorridor(targetDirection, range, current);
                    else
                    {
                        currentGround = Mathf.Max(0f, current.y - (IsPike
                            ? PikeNavigationObstacles.RadarAltitude(missile) : missile.radarAlt));
                        terrainFloor = currentGround + CruiseAltitude;
                        approachGroundPeak = currentGround;
                        routeDirection = targetDirection;
                    }
                }
                else ProbeCorridor(targetDirection, range, current, interval);
            }
            MinimumObservedClearance = Mathf.Min(MinimumObservedClearance, current.y - currentGround);
            float speed = Mathf.Max(1f, missile.speed);
            float timeToGo = range / speed;
            float lowAltitude = weaponPhase != null && weaponPhase.TerminalBoostActive ? TerminalAltitude : CruiseAltitude;
            float altitude = Mathf.Max(lowAltitude, terrainFloor);
            Vector3 heading = routeDirection.sqrMagnitude > .5f ? routeDirection : targetDirection;
            if (!clearedLauncher)
            {
                // The native booster performs the actual ejection/ascent. A
                // vertical shot clears its launch point before turning; a
                // horizontal diagnostic/air launch does not invent an ascent.
                clearedLauncher = NativePikeLaunch ? PikeLaunchPolicy.ClearToGuide(verticalLaunch, missile.timeSinceSpawn,
                    InitialFlightSeconds, current.y - launchAltitude, LaunchClearanceM) :
                    !verticalLaunch || current.y - launchAltitude >= 55f || missile.timeSinceSpawn >= 2.4f;
                if (!clearedLauncher)
                {
                    if (NativePikeLaunch) ApplyLaunchTurnRate();
                    FlightPhase = "launch-clearance";
                    return NativePikeLaunch ? current + Vector3.up * 100000f :
                        current + (Vector3.up + targetDirection * .22f) * 1500f;
                }
            }
            if (!NativePikeLaunch && verticalLaunch && missile.timeSinceSpawn < InitialFlightSeconds)
            {
                ApplyLaunchTurnRate();
                FlightPhase = "launch-turn";
                // Tilt the powered boost towards the target while retaining a
                // positive flight path until the folding surfaces are clear.
                return current + (targetDirection + Vector3.up * .45f) * 1500f;
            }
            if (NativePikeLaunch && verticalLaunch && !launchSettled)
            {
                float exitPitch = seaSkimSelected ? 0f : MaximumLoftDegrees;
                Vector3 exitDirection = targetDirection * Mathf.Cos(exitPitch * Mathf.Deg2Rad) + Vector3.up * Mathf.Sin(exitPitch * Mathf.Deg2Rad);
                Vector3 angular = transform.InverseTransformVector(missile.rb.angularVelocity);
                float restoreRate = launchRateApplied ? normalTurnRate : MaximumTurnRate(missile);
                float restoreG = launchRateApplied ? normalGLimit : MaximumGLimit(missile);
                launchSettled = !missile.boosterIsAttached && Vector3.Angle(transform.forward, exitDirection) <= 10f &&
                    Vector3.Angle(missile.rb.velocity, exitDirection) <= 10f &&
                    PikeFlightProfile.CanRestoreRates(angular.x, angular.y, restoreRate, restoreG, missile.speed);
                if (!launchSettled)
                {
                    ApplyLaunchTurnRate(); FlightPhase = "launch-turn";
                    CommandedAltitude = seaSkimSelected ? CruiseAltitude : CeilingAltitude;
                    return current + exitDirection * Mathf.Max(1000f, speed * 6f);
                }
                RestoreLaunchTurnRate();
            }
            else { if (!verticalLaunch) launchSettled = true; RestoreLaunchTurnRate(); }

            if (approachGroundPeak > 20f && terrainFloor > lowAltitude + 20f) terrainApproachObserved = true;
            DirectTerminalActive = false;
            bool pikeApproach = IsPike && terminalTarget is Ship && weaponPhase != null && weaponPhase.TerminalBoostActive;
            bool directSurfaceApproach = terminalTarget is Building || terminalTarget is Ship && (terrainApproachObserved || pikeApproach);
            if ((LandAttack || pikeApproach) && directSurfaceApproach && missile.seekerMode == Missile.SeekerMode.activeLock &&
                (IsPike ? ObservedRange <= PikeFlightProfile.FinalCorrectionRange : timeToGo < 8f))
            {
                if (pikeApproach && terminalSurfaceSelected && !PikeTerminalPartValid())
                {
                    nativePartInvalidations++; ClearTerminalSurface(); nextTerminalProbe = 0f;
                }
                if (missile.timeSinceSpawn >= nextTerminalProbe)
                {
                    nextTerminalProbe = missile.timeSinceSpawn + .12f;
                    directTerminalClear = ProbeTerminalPath(current);
                }
                if (directTerminalClear && terminalCollider != null && terminalCollider.enabled && IsTargetCollider(terminalCollider) &&
                    (!pikeApproach || RefreshPikeTerminalAim(current)))
                {
                    // Stop regulating height when a locked surface target's
                    // physical impact volume is directly reachable. Native
                    // pursuit carries the descent through the impact instead
                    // of braking vertical speed to zero above the target.
                    // Spear ship use retains its observed land-approach gate.
                    // Pike's final pursuit retains live jink and verifies its
                    // resulting corridor separately from the real hull ray.
                    // The probe also retains ARH's native moving-target lead.
                    DirectTerminalActive = true;
                    if (FirstDirectTerminalTime < 0f)
                    {
                        FirstDirectTerminalTime = missile.timeSinceSpawn;
                        DirectTerminalTerrainFloorAtHandoff = terrainFloor;
                    }
                    FlightPhase = "terminal-direct"; TerrainAvoidanceActive = false;
                    CommandedAltitude = terminalAim.y;
                    CommandedVerticalSpeed = (terminalAim.y - current.y) / Mathf.Max(.1f, timeToGo);
                    NaturalCruiseController.For(missile).ReleaseTerminal();
                    ApplyFinalCorrectionAuthority(independentAttack, ObservedRange);
                    return terminalAim;
                }
            }
            else directTerminalClear = false;

            if (missile != null && missile.LocalSim)
            {
                NaturalCruiseController controller = NaturalCruiseController.For(missile);
                if (controller.TryCruise(destination, lowAltitude, NativeObservationUsable ? ObservedRange : range,
                    seaSkimSelected, out GlobalPosition nativeWaypoint))
                {
                    // Keep native terrain/neighbor navigation for the route.
                    // Its low-altitude clearance is separate from Pike's
                    // long-range ceiling and braking-aware descent schedule.
                    Vector3 nativeDirection = nativeWaypoint - current;
                    float nativePreviewTime = nativeDirection.magnitude / speed;
                    CommandedAltitude = nativeWaypoint.y;
                    CommandedVerticalSpeed = (nativeWaypoint.y - current.y) / Mathf.Max(.1f, nativePreviewTime);
                    nativeDirection.y = 0f;
                    float nativeAngle = Vector3.Angle(nativeDirection, targetDirection);
                    MaximumRouteAngle = Mathf.Max(MaximumRouteAngle, nativeAngle);
                    bool avoiding = nativeWaypoint.y > lowAltitude + 20f || nativeAngle > 3f;
                    if (avoiding && !TerrainAvoidanceActive) TerrainAvoidanceEvents++;
                    TerrainAvoidanceActive = avoiding;
                    float profileAltitude = PikeFlightProfile.GoalAltitude(seaSkimSelected, false, CeilingAltitude, CruiseAltitude, TerminalAltitude);
                    bool profileHeightControl = !seaSkimSelected || current.y > Mathf.Max(40f, nativeWaypoint.y + 20f);
                    if (profileHeightControl)
                    {
                        float requested = Mathf.Max(profileAltitude, nativeWaypoint.y);
                        // Terrain may require a higher clearance than the
                        // nominal ceiling; open-water cruise never does.
                        CommandedAltitude = requested;
                        FlightPhase = !seaSkimSelected ? current.y < CeilingAltitude - 20f ? "long-range-climb" : "high-cruise" : "descent-to-sea-skim";
                    }
                    else
                    {
                        NaturalPikeGroupTargeting groupTargeting = GetComponent<NaturalPikeGroupTargeting>();
                        FlightPhase = groupTargeting != null && groupTargeting.LeaderSearching ? "group-leader-search" :
                            ObservedRange <= FinalApproachRange ? "final-approach" : "grouped-sea-skim";
                    }
                    if (current.y - currentGround < 35f && Mathf.Abs(missile.rb.velocity.y) < 10f)
                    {
                        if (FirstLowCruiseTime < 0f) FirstLowCruiseTime = missile.timeSinceSpawn;
                        LowCruiseSeconds += delta;
                    }
                    MaximumDescentSpeed = Mathf.Max(MaximumDescentSpeed, -missile.rb.velocity.y);
                    return profileHeightControl ? LiftAim(current, nativeDirection.normalized, range, CommandedAltitude) : nativeWaypoint;
                }
                if (cruiseCorridorObservation)
                {
                    // The native controller released this frame. Restore the
                    // complete existing terminal safety controller before its
                    // height loop runs, including its momentum sweep.
                    float interval = .28f + (missile.persistentID.Id % 5) * .015f;
                    ProbeCorridor(targetDirection, range, current, interval);
                    nextProbe = missile.timeSinceSpawn + interval;
                    cruiseCorridorObservation = false;
                    altitude = Mathf.Max(lowAltitude, terrainFloor);
                    heading = routeDirection.sqrMagnitude > .5f ? routeDirection : targetDirection;
                }
            }

            float terminalBlend = IsPike ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(6f, .8f, timeToGo));
            float aimHeight = Mathf.Max(3f, RadarPosition(seeker).y);
            altitude = Mathf.Lerp(altitude, aimHeight, terminalBlend);
            // Terrain clearance is applied AFTER the terminal blend. The old
            // order could blend a safe ridge crossing back into the ground.
            float terminalClearance = timeToGo < 1.1f ? 2f : currentGround > 1f ? 12f : lowAltitude;
            float terrainSafety = Mathf.Max(currentGround + terminalClearance, terrainFloor);
            if (timeToGo < 3f && approachGroundPeak <= aimHeight + 1f)
            {
                // Release only the cruise clearance margin, and only when all
                // sampled intervening ground is below the native aimpoint.
                // A ridge higher than the target retains its clearance floor.
                terrainSafety = currentGround > 1f ? currentGround + terminalClearance : 2f;
                heading = targetDirection;
            }
            altitude = Mathf.Max(altitude, terrainSafety);
            TerrainAvoidanceActive = terrainFloor > lowAltitude + 8f || Vector3.Angle(heading, targetDirection) > 3f;
            FlightPhase = timeToGo < 6f ? "terminal" : TerrainAvoidanceActive ? "terrain-clearance" :
                current.y > altitude + 20f ? "descent" : "low-cruise";
            CommandedAltitude = altitude;
            if (FlightPhase == "low-cruise" && current.y - currentGround < 35f && Mathf.Abs(missile.rb.velocity.y) < 10f)
            {
                if (FirstLowCruiseTime < 0f) FirstLowCruiseTime = missile.timeSinceSpawn;
                LowCruiseSeconds += delta;
            }
            MaximumDescentSpeed = Mathf.Max(MaximumDescentSpeed, -missile.rb.velocity.y);
            ApplyFinalCorrectionAuthority(independentAttack, ObservedRange);
            return LiftAim(current, heading, range, altitude);
        }

        private bool ProbeTerminalPath(GlobalPosition current)
        {
            DirectTerminalChecks++;
            TerminalPathBlockReason = null; TerminalBlockingCollider = null;
            // A cached hull-local point can rise when an attached, live ship
            // pitches or rolls. Re-select actual low hull geometry only at the
            // existing terminal probe cadence, retaining all native part and
            // collision checks instead of clamping an aimpoint into empty air.
            if (IsPike && terminalTarget is Ship && terminalSurfaceSelected && terminalCollider != null &&
                !PikeTerminalSurfaceHeightValid(terminalCollider.transform.TransformPoint(terminalSurfaceLocal)))
            {
                nativeSurfaceHeightInvalidations++;
                ClearTerminalSurface();
            }
            if (terminalCollider == null || !terminalCollider.enabled || !terminalCollider.gameObject.activeInHierarchy || !IsTargetCollider(terminalCollider))
            {
                ClearTerminalSurface();
                float largestVolume = 0f;
                foreach (Collider candidate in terminalTarget.GetComponentsInChildren<Collider>())
                {
                    if (candidate == null || !candidate.enabled || candidate.isTrigger || !candidate.gameObject.activeInHierarchy ||
                        candidate.gameObject.layer == PhysicsLayers.ExclusionZones || !IsTargetCollider(candidate)) continue;
                    Vector3 size = candidate.bounds.size;
                    float volume = size.x * size.y * size.z;
                    if (volume <= largestVolume) continue;
                    largestVolume = volume; terminalCollider = candidate;
                }
            }
            if (terminalCollider == null) return TerminalBlocked("no-enabled-target-collider", null);
            if (!terminalSurfaceSelected)
            {
                if (IsPike && terminalTarget is Ship)
                {
                    if (!SelectPikeTerminalSurface())
                        return TerminalBlocked("native-part-has-no-visible-attached-hull-surface", terminalCollider);
                }
                else
                {
                    // Native buildings can contain deep foundations: an aggregate
                    // collider center may be underground. Find a real exposed
                    // roof surface of the locked target instead. Every candidate
                    // is an actual target-collider hit, checked against terrain.
                    Bounds bounds = terminalCollider.bounds;
                    float highest = float.NegativeInfinity;
                    for (int i = 0; i < 5; i++)
                    {
                        Vector3 above = bounds.center;
                        if (i > 0)
                        {
                            above.x += bounds.size.x * (i % 2 == 0 ? .25f : -.25f);
                            above.z += bounds.size.z * (i < 3 ? -.25f : .25f);
                        }
                        above.y = bounds.max.y + 2f;
                        CorridorChecks++;
                        if (!terminalCollider.Raycast(new Ray(above, Vector3.down), out RaycastHit roof, bounds.size.y + 4f)) continue;
                        float ground = GroundAt(roof.point);
                        if (roof.point.y - Datum.LocalSeaY <= ground + 3f || roof.point.y <= highest) continue;
                        highest = roof.point.y;
                        terminalSurfaceLocal = terminalCollider.transform.InverseTransformPoint(roof.point - Vector3.up * .5f);
                        terminalSurfaceSelected = true;
                    }
                    if (!terminalSurfaceSelected) return TerminalBlocked("no-exposed-target-surface", terminalCollider);
                }
            }
            Vector3 aim = terminalCollider.transform.TransformPoint(terminalSurfaceLocal);
            terminalAim = current + (aim - transform.position);
            terminalPhysicalSurface = terminalAim;
            TerminalAimGround = GroundAt(aim);
            if (terminalAim.y <= TerminalAimGround + 2f) return TerminalBlocked("target-point-below-terrain", terminalCollider);
            Vector3 direction = aim - transform.position;
            float distance = direction.magnitude;
            if (distance < 1f) return TerminalBlocked("already-at-target-surface", terminalCollider);
            direction /= distance;
            // Require the approach centerline to hit actual target geometry,
            // including concave mesh geometry around the selected surface.
            if (!terminalCollider.Raycast(new Ray(transform.position, direction), out RaycastHit targetHit, distance + 1f))
                return TerminalBlocked("approach-ray-misses-target-geometry", terminalCollider);
            CorridorChecks++;
            if (NavigationSphereCast(transform.position, 4f, direction, out RaycastHit obstacle,
                targetHit.distance, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                return TerminalBlocked("desired-corridor-obstructed", obstacle.collider);
            if (terminalTarget is Ship)
            {
                // ARH computes lead before SetAimpoint; its target-velocity
                // argument alone does not lead native steering. Preserve that
                // same calculation using the real locked radar observations.
                Vector3 lead = TargetCalc.GetLeadVectorWithAccel(terminalPhysicalSurface, current,
                    RadarVelocity(seeker), missile.rb.velocity, RadarAcceleration(seeker), RadarMaximumLead(seeker));
                terminalAim += lead;
                Vector3 ledVector = terminalAim - current;
                if (lead.sqrMagnitude > .01f)
                {
                    if (terminalAim.y <= GroundAt(terminalAim.ToLocalPosition()) + 2f)
                        return TerminalBlocked("led-target-point-below-terrain", null);
                    CorridorChecks++;
                    if (NavigationSphereCast(transform.position, 4f, ledVector.normalized, out RaycastHit ledHit,
                        ledVector.magnitude, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                        return TerminalBlocked("led-corridor-obstructed", ledHit.collider);
                }
            }
            // A clear desired line alone does not cancel vertical momentum.
            // Preserve avoidance if the existing velocity is about to strike
            // intervening terrain while native steering turns onto that line.
            float reactionDistance = Mathf.Min(targetHit.distance, missile.speed * .8f);
            CorridorChecks++;
            if (reactionDistance > 1f && NavigationSphereCast(transform.position, 4f, missile.rb.velocity.normalized,
                out RaycastHit momentumHit, reactionDistance, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                return TerminalBlocked("momentum-corridor-obstructed", momentumHit.collider);
            return true;
        }

        private bool IsPike => tactics != null && tactics.WeaponKey == "rsl_ashm";

        private void ClearTerminalSurface()
        {
            terminalCollider = null; terminalNativePart = null; terminalNativeHull = null;
            terminalSurfaceSelected = false; directTerminalClear = false;
            selectedSurfaceMargin = float.PositiveInfinity; selectedSurfacePeers = 0; selectedCapsuleRadius = 0f;
        }

        private bool LiveTargetPart(UnitPart part)
        {
            return terminalTarget != null && !terminalTarget.disabled && part != null && part.parentUnit == terminalTarget && !part.IsDetached() &&
                part.hitPoints > 0f && part.gameObject.activeInHierarchy;
        }

        private bool PikeTerminalPartValid()
        {
            return LiveTargetPart(terminalNativePart) && LiveTargetPart(terminalNativeHull) && TargetHullCollider(terminalCollider);
        }

        private bool PikeTerminalSurfaceHeightValid(Vector3 surface)
        {
            return surface.y <= Datum.LocalSeaY + CruiseAltitude;
        }

        private bool TargetHullCollider(Collider candidate)
        {
            return candidate != null && candidate.enabled && !candidate.isTrigger && candidate.gameObject.activeInHierarchy &&
                candidate.gameObject.layer != PhysicsLayers.ExclusionZones && IsTargetCollider(candidate) &&
                LiveTargetPart(candidate.GetComponentInParent<UnitPart>() as ShipPart);
        }

        private bool SelectPikeTerminalSurface()
        {
            // Native optical cruise selects GetRandomPart once on terminal
            // entry. Retain that distribution here under actual ARH lock,
            // validating the Ship override's potentially detached result.
            // Resolve superstructure to its physical attached hull, rather
            // than inventing a high aimpoint or an internal collision point.
            if (terminalTarget.GetAllParts().Count == 0) return false;
            Collider[] colliders = terminalTarget.GetComponentsInChildren<Collider>();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                nativePartSelectionAttempts++;
                Transform nativeTransform = terminalTarget.GetRandomPart();
                UnitPart nativePart = nativeTransform != null ? nativeTransform.GetComponent<UnitPart>() : null;
                if (!LiveTargetPart(nativePart)) continue;
                ShipPart selectedHull = nativePart.GetComponentInParent<ShipPart>();
                if (!LiveTargetPart(selectedHull)) continue;
                foreach (Collider candidate in colliders)
                {
                    if (!TargetHullCollider(candidate) || candidate.GetComponentInParent<UnitPart>() != selectedHull) continue;
                    Bounds bounds = candidate.bounds;
                    float strikeHeight = Datum.LocalSeaY + TerminalAltitude;
                    float maximumStrikeHeight = Datum.LocalSeaY + CruiseAltitude;
                    if (bounds.min.y > maximumStrikeHeight) continue;
                    // Bounds provide finite ray seeds only. A ray must strike
                    // actual mesh geometry before any point can be accepted.
                    Vector3[] seeds = {
                        Vector3.Lerp(bounds.ClosestPoint(nativeTransform.position), bounds.center, .1f),
                        bounds.center, bounds.ClosestPoint(transform.position) };
                    foreach (Vector3 seed in seeds)
                    {
                        Vector3 lowSeed = seed;
                        lowSeed.y = Mathf.Clamp(strikeHeight, bounds.min.y + .1f, Mathf.Min(bounds.max.y, maximumStrikeHeight));
                        Vector3 direction = lowSeed - transform.position;
                        if (direction.sqrMagnitude < 1f) continue;
                        float rayLength = direction.magnitude + bounds.size.magnitude + 1f;
                        direction.Normalize(); CorridorChecks++;
                        Ray ray = new Ray(transform.position, direction);
                        if (!candidate.Raycast(ray, out RaycastHit selectedHit, rayLength)) continue;
                        Collider struckHull = candidate;
                        RaycastHit surface = selectedHit;
                        // An earlier attached hull surface is the real impact
                        // surface when it occludes the selected compartment.
                        foreach (Collider other in colliders)
                        {
                            if (other == candidate || !TargetHullCollider(other)) continue;
                            CorridorChecks++;
                            if (other.Raycast(ray, out RaycastHit hit, surface.distance) && hit.distance < surface.distance)
                            { surface = hit; struckHull = other; }
                        }
                        Vector3 point = surface.point + direction * .5f;
                        if (point.y > maximumStrikeHeight) continue;
                        if (point.y - Datum.LocalSeaY <= GroundAt(point) + 2f) continue;
                        if (!PikeSurfaceAvailable(point, out float margin, out int peers, out float capsuleRadius)) continue;
                        terminalNativePart = nativePart; terminalNativeHull = selectedHull;
                        terminalCollider = struckHull;
                        terminalSurfaceLocal = struckHull.transform.InverseTransformPoint(point);
                        terminalSurfaceSelected = true;
                        selectedSurfaceMargin = margin; selectedSurfacePeers = peers; selectedCapsuleRadius = capsuleRadius;
                        nativePartSelections++; nativePartSelectedAt = missile.timeSinceSpawn;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool PikeSurfaceAvailable(Vector3 point, out float minimumMargin, out int peers, out float capsuleRadius)
        {
            minimumMargin = float.PositiveInfinity; peers = 0; capsuleRadius = 0f;
            CapsuleCollider capsule = missile.GetComponent<CapsuleCollider>();
            if (capsule == null || !capsule.enabled || !capsule.gameObject.activeInHierarchy || capsule.isTrigger) return false;
            capsuleRadius = capsule.bounds.extents.magnitude;
            if (missile.NetworkHQ == null) return true;
            // Reuse the native local cruise registry. This only rejects an
            // overlapping candidate surface or crossing final approach; it never moves an existing
            // reservation or adds a steering, throttle or formation controller.
            foreach (Missile peer in missile.NetworkHQ.GetCruiseMissiles())
            {
                if (peer == null || peer == missile || peer.disabled || !peer.isActiveAndEnabled || !peer.LocalSim ||
                    peer.NetworkHQ != missile.NetworkHQ || peer.targetID != missile.targetID ||
                    peer.seekerMode != Missile.SeekerMode.activeLock ||
                    !FastMath.InRange(peer.GlobalPosition(), missile.GlobalPosition(), 5000f)) continue;
                NaturalCruiseGuidance other = peer.GetComponent<NaturalCruiseGuidance>();
                if (other == null || !other.isActiveAndEnabled || other.observedTarget != peer.targetID || other.terminalTarget != terminalTarget || !other.terminalSurfaceSelected ||
                    !other.PikeTerminalPartValid()) continue;
                CapsuleCollider peerCapsule = peer.GetComponent<CapsuleCollider>();
                if (peerCapsule == null || !peerCapsule.enabled || !peerCapsule.gameObject.activeInHierarchy || peerCapsule.isTrigger) continue;
                Vector3 reservedPoint = other.terminalCollider.transform.TransformPoint(other.terminalSurfaceLocal);
                float requiredSeparation = capsuleRadius + peerCapsule.bounds.extents.magnitude + 1f;
                float margin = Vector3.Distance(point, reservedPoint) - requiredSeparation;
                peers++; surfaceReservationChecks++; minimumMargin = Mathf.Min(minimumMargin, margin);
                if (margin >= 0f)
                {
                    // Separate impact points can still cross on the way to the
                    // hull. Check only this candidate against an already reserved
                    // approach, ending when either round reaches its surface.
                    bool crosses = PikeApproachReservation.Conflicts(peer.transform.position - transform.position,
                        point - transform.position, reservedPoint - peer.transform.position,
                        missile.rb != null ? missile.rb.velocity.magnitude : missile.speed,
                        peer.rb != null ? peer.rb.velocity.magnitude : peer.speed, requiredSeparation,
                        out _, out float approachMargin);
                    minimumMargin = Mathf.Min(minimumMargin, approachMargin);
                    if (!crosses && missile.rb != null && peer.rb != null)
                    {
                        crosses = PikeApproachReservation.NativeSweepConflicts(peer.transform.position - transform.position,
                            point - transform.position, reservedPoint - peer.transform.position,
                            missile.rb.velocity, peer.rb.velocity, PikeCollisionVelocity(missile), PikeCollisionVelocity(peer),
                            Time.fixedDeltaTime, requiredSeparation, out _, out float sweepMargin);
                        minimumMargin = Mathf.Min(minimumMargin, sweepMargin);
                        if (crosses) approachMargin = sweepMargin;
                    }
                    if (!crosses) continue;
                    margin = approachMargin;
                }
                surfaceReservationRejections++; lastRejectedSurfaceMargin = margin;
                lastRejectedSurfacePeer = peer.persistentID;
                return false;
            }
            return true;
        }

        private static Vector3 PikeCollisionVelocity(Missile round)
        {
            // Match the velocity used by native DetectCollisions, including
            // its known-velocity fallback when no physical target is bound.
            Unit target = NativeCollisionTarget(round);
            return target != null && target.rb != null ? target.rb.velocity : NativeCollisionTargetVelocity(round);
        }

        private bool RefreshPikeTerminalAim(GlobalPosition current)
        {
            if (!PikeTerminalPartValid()) return TerminalBlocked("native-terminal-part-no-longer-attached", terminalCollider);
            // The expensive physical surface selection is cached; moving-hull
            // position, native radar lead and the existing jink are not. This
            // runs after Tactics has applied the current native jink tick.
            Vector3 surface = terminalCollider.transform.TransformPoint(terminalSurfaceLocal);
            // Between expensive probe ticks, fall back to the existing height
            // controller if the physical point leaves its selection band.
            // Radar lead is applied afterwards and keeps native compensation.
            if (!PikeTerminalSurfaceHeightValid(surface))
                return TerminalBlocked("pike-cached-surface-above-strike-band", terminalCollider);
            terminalPhysicalSurface = current + (surface - transform.position);
            terminalBaseAim = terminalPhysicalSurface + TargetCalc.GetLeadVectorWithAccel(terminalPhysicalSurface, current,
                RadarVelocity(seeker), missile.rb.velocity, RadarAcceleration(seeker), RadarMaximumLead(seeker));
            GlobalPosition finalAim = tactics.PreviewTerminalHeading(terminalBaseAim);
            Vector3 desired = finalAim - current;
            if (desired.sqrMagnitude < 1f) return TerminalBlocked("pike-already-at-terminal-point", null);
            if (finalAim.y <= GroundAt(finalAim.ToLocalPosition()) + 2f)
                return TerminalBlocked("pike-led-jink-point-below-terrain", null);
            CorridorChecks++;
            if (NavigationSphereCast(transform.position, 4f, desired.normalized, out RaycastHit obstacle,
                desired.magnitude, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                return TerminalBlocked("pike-live-jink-corridor-obstructed", obstacle.collider);
            float reactionDistance = Mathf.Min((terminalPhysicalSurface - current).magnitude, missile.speed * .8f);
            CorridorChecks++;
            if (reactionDistance > 1f && NavigationSphereCast(transform.position, 4f, missile.rb.velocity.normalized,
                out RaycastHit momentumHit, reactionDistance, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                return TerminalBlocked("pike-live-momentum-corridor-obstructed", momentumHit.collider);
            // The caller applies the same checked heading once after guidance.
            terminalAim = terminalBaseAim;
            TerminalPathBlockReason = null; TerminalBlockingCollider = null;
            return true;
        }

        private bool TerminalBlocked(string reason, Collider obstruction)
        {
            DirectTerminalBlockedChecks++; TerminalPathBlockReason = reason;
            TerminalBlockingCollider = obstruction != null ? obstruction.name : null;
            return false;
        }

        private void ApplyLaunchTurnRate()
        {
            if ((!LandAttack && !NativePikeLaunch) || (!NativePikeLaunch && LaunchTurnRate <= 0f) || launchRateApplied) return;
            normalTurnRate = MaximumTurnRate(missile);
            normalGLimit = MaximumGLimit(missile);
            MaximumTurnRate(missile) = LaunchTurnRate;
            // Native combines both caps in one minimum. Applying its zero
            // rate with Pike's positive authored g cap would forbid turning.
            if (NativePikeLaunch) MaximumGLimit(missile) = LaunchGLimit;
            launchRateApplied = true;
        }

        private void RestoreLaunchTurnRate()
        {
            if (!launchRateApplied) return;
            if (NativePikeLaunch && missile != null && !missile.disabled && missile.LocalSim && missile.rb != null)
            {
                Vector3 angular = transform.InverseTransformVector(missile.rb.angularVelocity);
                if (!PikeFlightProfile.CanRestoreRates(angular.x, angular.y, normalTurnRate, normalGLimit, missile.speed)) return;
            }
            launchRateApplied = false;
            if (missile == null) return;
            MaximumTurnRate(missile) = normalTurnRate;
            MaximumGLimit(missile) = normalGLimit;
        }

        private void FixedUpdate()
        {
            // Native invalid-target handling can skip Guide. Do not leave the
            // short final-pass cap on a round whose observation was cleared.
            if (finalCorrection.Active && (missile == null || missile.disabled || !missile.LocalSim ||
                !finalCorrection.ForTarget(missile.targetID.Id) || missile.targetID.NotValid || seeker == null ||
                !RadarGuidance(seeker) || missile.seekerMode != Missile.SeekerMode.activeLock ||
                !LaunchReady || weaponPhase == null || !weaponPhase.TerminalBoostActive ||
                (!DirectTerminalActive && !WeaveCorrectionEligible(ObservedRange)) || HasMissedTerminalPass ||
                GetComponent<NaturalCruiseController>()?.TerminalReleased != true ||
                !PikeFlightProfile.Finite(ObservedRange) || ObservedRange <= 0f || ObservedRange >= PikeFinalCorrection.WeaveRange))
                RestoreFinalCorrectionAuthority();
            // The cruise helper used to be the only caller. ARH does not
            // refresh radarAlt itself, leaving the HUD frozen after release.
            // Continue native (unfiltered) altitude updates at the same 2 Hz.
            if (IsPike && missile != null && missile.LocalSim && !missile.disabled &&
                GetComponent<NaturalCruiseController>()?.TerminalReleased == true &&
                missile.timeSinceSpawn >= nextTerminalRadarUpdate)
            {
                nextTerminalRadarUpdate = missile.timeSinceSpawn + .5f;
                missile.UpdateRadarAlt();
            }
            if (NativePikeLaunch && missile != null && missile.LocalSim && !missile.disabled &&
                !launchFinsDeployed && missile.timeSinceSpawn > NativeLaunchFinDelay)
            { missile.DeployFins(); launchFinsDeployed = true; }
            if (launchRateApplied && (missile == null || missile.disabled || missile.targetID.NotValid ||
                (NativePikeLaunch ? launchSettled : missile.timeSinceSpawn >= InitialFlightSeconds))) RestoreLaunchTurnRate();
        }

        private void OnDisable() { RestoreFinalCorrectionAuthority(); RestoreLaunchTurnRate(); }

        private void ApplyFinalCorrectionAuthority(bool independentAttack, float range)
        {
            if (missile == null) return;
            bool weaving = WeaveCorrectionEligible(range);
            bool eligible = IsPike && missile.LocalSim && !missile.disabled && missile.rb != null &&
                missile.targetID.IsValid && independentAttack && LaunchReady && !launchRateApplied &&
                seeker != null && RadarGuidance(seeker) && missile.seekerMode == Missile.SeekerMode.activeLock &&
                weaponPhase != null && weaponPhase.TerminalBoostActive && (DirectTerminalActive || weaving) &&
                !HasMissedTerminalPass;
            finalCorrection.Update(eligible, missile.targetID.Id, range,
                ref MaximumTurnRate(missile), ref MaximumGLimit(missile), weaving);
        }

        private bool WeaveCorrectionEligible(float range)
        {
            return range > PikeFinalCorrection.Range && range < PikeFinalCorrection.WeaveRange &&
                !TerrainSteeringRequired(TerrainAvoidanceActive, terrainFloor, TerminalAltitude, routeSide) &&
                FlightPhase != "native-target-retirement";
        }

        private void RestoreFinalCorrectionAuthority()
        {
            if (missile != null && finalCorrection.Active)
                finalCorrection.Restore(ref MaximumTurnRate(missile), ref MaximumGLimit(missile));
        }

        private void ObserveCruiseCorridor(Vector3 direction, float range, GlobalPosition current)
        {
            currentGround = GroundAt(transform.position);
            float preview = Mathf.Min(Mathf.Max(0f, range - 60f),
                Mathf.Clamp(Mathf.Max(missile.speed, NominalSpeed * .75f) * 10f, 900f, 15000f));
            float baseAltitude = range < TerminalRange ? TerminalAltitude : CruiseAltitude;
            Path observed = SamplePath(direction, preview, baseAltitude, true);
            // These values feed telemetry and the retained locked-terminal
            // safety gate. They never modify the native cruise waypoint.
            terrainFloor = observed.Floor; approachGroundPeak = observed.GroundPeak;
            routeDirection = direction;
        }

        private void ProbeCorridor(Vector3 destinationDirection, float range, GlobalPosition current, float interval)
        {
            terrainObstacleActive = false;
            float preview = Mathf.Min(Mathf.Max(0f, range - 60f), Mathf.Clamp(Mathf.Max(missile.speed, NominalSpeed * .75f) * 10f, 900f, 15000f));
            currentGround = GroundAt(transform.position);
            if (preview < 30f) { terrainFloor = currentGround + 2f; approachGroundPeak = currentGround; routeDirection = destinationDirection; return; }
            float baseAltitude = range < TerminalRange ? TerminalAltitude : CruiseAltitude;
            Path direct = SamplePath(destinationDirection, preview, baseAltitude, true);
            Path chosen = direct;
            float directCost = direct.Penalty;
            bool blocked = direct.Peak > current.y + 25f && range > 1500f;
            if (blocked || routeSide != 0)
            {
                // Every candidate advances towards the target (at most 42
                // degrees off course). There are no orbits or hidden waypoints
                // that can trap a cohort when its first missile is destroyed.
                foreach (int candidate in CandidateDirections)
                {
                    int side = candidate < 0 ? -1 : 1;
                    if (routeSide != 0 && Time.time < routeHoldUntil && side != routeSide) continue;
                    float angle = candidate * 21f;
                    Vector3 direction = Quaternion.AngleAxis(angle, Vector3.up) * destinationDirection;
                    Path alternative = SamplePath(direction, preview, baseAltitude, false);
                    float cost = alternative.Penalty + Mathf.Abs(angle) * 3f;
                    if (routeSide == side) cost -= 30f;
                    if (cost + 20f < directCost)
                    {
                        directCost = cost; chosen = alternative;
                    }
                }
            }
            float sideOfPath = Vector3.Dot(Vector3.Cross(destinationDirection, chosen.Direction), Vector3.up);
            int newSide = Mathf.Abs(sideOfPath) < .01f ? 0 : sideOfPath > 0f ? 1 : -1;
            if (newSide != 0 && newSide != routeSide) { routeHoldUntil = Time.time + 2f; TerrainAvoidanceEvents++; }
            routeSide = newSide;
            routeDirection = Vector3.RotateTowards(routeDirection, chosen.Direction, 12f * Mathf.Deg2Rad * interval, 0f).normalized;
            MaximumRouteAngle = Mathf.Max(MaximumRouteAngle, Vector3.Angle(routeDirection, destinationDirection));

            // A new route does not cancel existing momentum. Check its actual
            // near-term corridor until the missile has physically turned.
            Vector3 velocityDirection = missile.rb.velocity; velocityDirection.y = 0f;
            float required = chosen.Floor;
            approachGroundPeak = chosen.GroundPeak;
            if (velocityDirection.sqrMagnitude > 100f)
            {
                velocityDirection.Normalize();
                if (Vector3.Angle(velocityDirection, chosen.Direction) > 5f)
                {
                    Path momentum = SamplePath(velocityDirection, Mathf.Min(preview, Mathf.Max(300f, missile.speed * 3f)), baseAltitude, true);
                    required = Mathf.Max(required, momentum.Floor);
                    approachGroundPeak = Mathf.Max(approachGroundPeak, momentum.GroundPeak);
                }
            }
            // Predict the swept centreline with current vertical momentum.
            // Sphere casts cover narrow obstructions between height samples.
            Vector3 projected = missile.rb.velocity * Mathf.Min(3f, preview / Mathf.Max(1f, missile.speed));
            if (projected.sqrMagnitude > 1f)
            {
                CorridorChecks++;
                if (NavigationSphereCast(transform.position, 4f, projected.normalized, out RaycastHit obstruction,
                    projected.magnitude, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 forward = projected; forward.y = 0f; forward.Normalize();
                    float obstructionTop = Mathf.Max(obstruction.point.y - Datum.LocalSeaY,
                        Mathf.Max(GroundAt(obstruction.point + forward * .5f), GroundAt(obstruction.point + forward * 3f)));
                    required = Mathf.Max(required, obstructionTop + 35f);
                    approachGroundPeak = Mathf.Max(approachGroundPeak, obstructionTop);
                    // Reuse this physical momentum hit to time the climb. A
                    // comfortable altitude capture can otherwise reach a low
                    // coastal ridge before it reaches the requested clearance.
                    terrainObstacleActive = true;
                    terrainObstacleDeadline = missile.timeSinceSpawn + obstruction.distance / Mathf.Max(1f, missile.speed);
                    terrainObstacleClearance = obstructionTop + 35f;
                }
            }
            // Raise immediately; lower slowly enough to cross a crest before
            // commencing the next descent. Altitude remains an aimpoint only.
            terrainFloor = required > terrainFloor ? required : Mathf.MoveTowards(terrainFloor, required,
                Mathf.Max(15f, Mathf.Min(45f, missile.speed * .07f)) * interval);
        }

        private Path SamplePath(Vector3 direction, float distance, float baseAltitude, bool wide)
        {
            float slope = NominalSpeed > 500f ? .035f : .065f;
            var result = new Path { Direction = direction, Floor = Mathf.Max(baseAltitude, currentGround > 1f ? currentGround + 25f : baseAltitude),
                Peak = currentGround, GroundPeak = currentGround };
            Vector3 side = Vector3.Cross(Vector3.up, direction);
            Vector3 previous = transform.position; previous.y = Datum.LocalSeaY + Mathf.Max(baseAltitude, currentGround + 18f);
            for (int i = 1; i < Fractions.Length; i++)
            {
                float ahead = distance * Fractions[i];
                Vector3 point = transform.position + direction * ahead;
                float ground = GroundAt(point);
                if (wide && i % 3 == 0)
                {
                    float width = Mathf.Clamp(12f + ahead * .01f, 12f, 65f);
                    ground = Mathf.Max(ground, Mathf.Max(GroundAt(point + side * width), GroundAt(point - side * width)));
                }
                float clearance = ground > 1f ? 25f : baseAltitude;
                float floor = ground + clearance;
                result.Floor = Mathf.Max(result.Floor, floor - ahead * slope);
                result.Peak = Mathf.Max(result.Peak, floor);
                result.GroundPeak = Mathf.Max(result.GroundPeak, ground);
                // A segment at the sampled endpoint heights catches ridges
                // whose crest lies between two otherwise clear samples.
                point.y = Datum.LocalSeaY + floor;
                CorridorChecks++;
                if (NavigationLinecast(previous, point, out RaycastHit ridge, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                {
                    float ridgeDistance = Vector3.Distance(transform.position, ridge.point);
                    float ridgeHeight = Mathf.Max(ridge.point.y - Datum.LocalSeaY,
                        Mathf.Max(GroundAt(ridge.point + direction * .5f), GroundAt(ridge.point + direction * 3f))) + 35f;
                    result.Floor = Mathf.Max(result.Floor, ridgeHeight - ridgeDistance * slope);
                    result.Peak = Mathf.Max(result.Peak, ridgeHeight);
                    result.GroundPeak = Mathf.Max(result.GroundPeak, ridgeHeight - 35f);
                }
                previous = point;
            }
            result.Penalty = result.Floor + result.Peak * .4f;
            return result;
        }

        private bool NavigationSphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit,
            float distance, int mask, QueryTriggerInteraction triggers)
        {
            if (IsPike) return PikeNavigationObstacles.SphereCast(missile, origin, radius, direction, out hit, distance, mask, triggers);
            return Physics.SphereCast(origin, radius, direction, out hit, distance, mask, triggers) && !IsTargetCollider(hit.collider);
        }

        private bool NavigationLinecast(Vector3 from, Vector3 to, out RaycastHit hit, int mask, QueryTriggerInteraction triggers)
        {
            if (IsPike) return PikeNavigationObstacles.Linecast(missile, from, to, out hit, mask, triggers);
            return Physics.Linecast(from, to, out hit, mask, triggers) && !IsTargetCollider(hit.collider);
        }

        private float GroundAt(Vector3 point)
        {
            point.y = Datum.LocalSeaY + 12000f;
            if (IsPike)
            {
                TerrainSamples++;
                return PikeNavigationObstacles.Raycast(missile, point, Vector3.down, out RaycastHit ground,
                    24000f, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)
                    ? Mathf.Max(0f, ground.point.y - Datum.LocalSeaY) : 0f;
            }
            for (int layer = 0; layer < 8; layer++)
            {
                TerrainSamples++;
                if (!Physics.Raycast(point, Vector3.down, out RaycastHit hit, 24000f,
                    PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)) return 0f;
                if (!IsTargetCollider(hit.collider)) return Mathf.Max(0f, hit.point.y - Datum.LocalSeaY);
                // Native installations use the statics layer. Their own roof
                // is an intended impact surface, not a hill to fly over.
                point = hit.point + Vector3.down * .1f;
            }
            return 0f;
        }

        private bool IsTargetCollider(Collider collider)
        {
            if (terminalTarget == null || collider == null) return false;
            UnitPart part = collider.GetComponentInParent<UnitPart>();
            if (part != null && part.IsDetached()) return false;
            return part != null && part.parentUnit == terminalTarget || collider.transform.IsChildOf(terminalTarget.transform);
        }

        private GlobalPosition LiftAim(GlobalPosition current, Vector3 heading, float range, float altitude)
        {
            Vector3 velocity = missile.rb.velocity;
            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float altitudeError = altitude - current.y;
            float density = GameAssets.i.airDensityAltitude.Evaluate(current.y * .001f);
            float pressure = Mathf.Max(1f, .5f * density * velocity.sqrMagnitude * CurrentFinArea(missile));
            float mass = Mathf.Max(1f, missile.rb.mass);
            float liftAtTenDegrees = pressure * Mathf.Abs(missile.GetLiftCoeff(10f * Mathf.Deg2Rad)) / mass;
            float brakingAcceleration = Mathf.Clamp(liftAtTenDegrees - 9.81f, 4f, 18f);
            float descentAngleLimit = horizontalSpeed * Mathf.Tan(MaximumDescentDegrees * Mathf.Deg2Rad);
            float maximumSink = SafeSinkRate(Mathf.Max(0f, -altitudeError), brakingAcceleration, 1.5f);
            float wantedVertical = altitudeError < 0f ? -Mathf.Min(descentAngleLimit, Mathf.Min(maximumSink, -altitudeError * .65f)) :
                Mathf.Min(altitudeError * .8f, Mathf.Max(35f, horizontalSpeed * .36f));
            bool urgentTerrainClimb = false;
            if (IsPike)
            {
                wantedVertical = PikeFlightProfile.VerticalDemand(altitudeError, horizontalSpeed, brakingAcceleration,
                    altitudeError >= 0f ? MaximumLoftDegrees : MaximumDescentDegrees);
                if (!LandAttack && terrainObstacleActive)
                {
                    float normalVertical = wantedVertical;
                    wantedVertical = PikeTerrainClearancePolicy.VerticalDemand(wantedVertical,
                        Mathf.Min(altitude, terrainObstacleClearance) - current.y, horizontalSpeed,
                        Mathf.Max(0f, terrainObstacleDeadline - missile.timeSinceSpawn), MaximumLoftDegrees, brakingAcceleration);
                    urgentTerrainClimb = wantedVertical > normalVertical;
                }
            }
            CommandedVerticalSpeed = wantedVertical;
            float speedLimit = NominalSpeed;
            if (weaponPhase != null && weaponPhase.TerminalBoostActive) speedLimit = Mathf.Max(NominalSpeed, weaponPhase.TerminalSpeed);
            if (weaponPhase != null) speedLimit = Mathf.Min(speedLimit, weaponPhase.MotorSpeedLimit);
            float thrust = missile.speed < speedLimit ? CurrentThrust(missile) * Throttle(missile) : 0f;
            float thrustVertical = thrust / mass * transform.forward.y;
            float positiveAccelerationLimit = Mathf.Max(30f, brakingAcceleration);
            if (urgentTerrainClimb)
                positiveAccelerationLimit = PikeTerrainClearancePolicy.PositiveAccelerationLimit(positiveAccelerationLimit,
                    true, liftAtTenDegrees, missile.speed, MaximumTurnRate(missile), MaximumGLimit(missile));
            float wantedAcceleration = Mathf.Clamp((wantedVertical - velocity.y) * 1.6f, -20f, positiveAccelerationLimit);
            float wantedLift = mass * (9.81f - thrustVertical + wantedAcceleration);
            float coefficient = Mathf.Abs(wantedLift) / pressure;
            float incidence = 25f * Mathf.Deg2Rad;
            for (int degree = 0; degree <= 25; degree++)
            {
                if (Mathf.Abs(missile.GetLiftCoeff(degree * Mathf.Deg2Rad)) < coefficient) continue;
                float low = Mathf.Max(0, degree - 1) * Mathf.Deg2Rad, high = degree * Mathf.Deg2Rad;
                for (int refine = 0; refine < 7; refine++)
                {
                    float middle = (low + high) * .5f;
                    if (Mathf.Abs(missile.GetLiftCoeff(middle)) >= coefficient) high = middle; else low = middle;
                }
                incidence = (low + high) * .5f; break;
            }
            incidence *= Mathf.Sign(wantedLift);
            float pitch = Mathf.Atan2(velocity.y, Mathf.Max(1f, horizontalSpeed)) + incidence * (ReachedOnTarget(missile) ? .5f : 1f);
            pitch = Mathf.Clamp(pitch, -(IsPike ? MaximumDescentDegrees : 35f) * Mathf.Deg2Rad,
                (IsPike ? MaximumLoftDegrees : 45f) * Mathf.Deg2Rad);
            float lead = Mathf.Min(range, Mathf.Max(250f, missile.speed * 3f));
            return current + heading * lead + Vector3.up * (Mathf.Tan(pitch) * lead);
        }

        internal GlobalPosition StabilizeTerminalSteering(GlobalPosition aim)
        {
            if (!IsPike || missile == null || missile.disabled || !missile.LocalSim || missile.rb == null ||
                missile.targetID.NotValid || seeker == null || !RadarGuidance(seeker) || !LaunchReady ||
                weaponPhase == null || !weaponPhase.TerminalBoostActive ||
                FlightPhase == "native-target-retirement") return aim;
            GlobalPosition current = missile.GlobalPosition();
            Vector3 demand = aim - current, horizontal = demand; horizontal.y = 0f;
            Vector3 prograde = missile.rb.velocity; prograde.y = 0f;
            if (horizontal.sqrMagnitude < 1f || prograde.sqrMagnitude < 1f) return aim;
            if (missile.timeSinceSpawn >= nextTerminalSteeringEnvelope)
            {
                nextTerminalSteeringEnvelope = missile.timeSinceSpawn + .1f;
                terminalSteeringEnvelope = 0f;
                float rate = Mathf.Min(MaximumTurnRate(missile) * Mathf.Deg2Rad,
                    9.81f * MaximumGLimit(missile) / Mathf.Max(missile.speed, 1f));
                Vector3 airVelocity = missile.rb.velocity - NetworkSceneSingleton<LevelInfo>.i.GetWind(current);
                float density = GameAssets.i.airDensityAltitude.Evaluate(current.y * .001f);
                float pressure = .5f * density * airVelocity.sqrMagnitude * CurrentFinArea(missile);
                float coefficient = missile.rb.mass * missile.speed * rate / Mathf.Max(pressure, 1f);
                if (!PikeFlightProfile.Finite(coefficient) || coefficient <= 0f || pressure <= 0f)
                    return aim;
                float incidence = 25f * Mathf.Deg2Rad;
                for (int degree = 0; degree <= 25; degree++)
                {
                    if (Mathf.Abs(missile.GetLiftCoeff(degree * Mathf.Deg2Rad)) < coefficient) continue;
                    float low = Mathf.Max(0, degree - 1) * Mathf.Deg2Rad, high = degree * Mathf.Deg2Rad;
                    for (int refine = 0; refine < 7; refine++)
                    {
                        float middle = (low + high) * .5f;
                        if (Mathf.Abs(missile.GetLiftCoeff(middle)) >= coefficient) high = middle; else low = middle;
                    }
                    incidence = (low + high) * .5f; break;
                }
                // Native Steering tracks the average of nose and prograde
                // after initial alignment. Its aim deflection therefore needs
                // half the physical incidence at the authored rate/g cap.
                terminalSteeringEnvelope = incidence * (ReachedOnTarget(missile) ? .5f : 1f);
                terminalSteeringEnvelopeUpdates++;
            }
            if (terminalSteeringEnvelope <= 0f || !PikeFlightProfile.Finite(terminalSteeringEnvelope)) return aim;
            Vector3 requested = horizontal.normalized, forward = prograde.normalized;
            if (Vector3.Angle(forward, requested) * Mathf.Deg2Rad <= terminalSteeringEnvelope) return aim;
            // Keep a reverse-course correction in the horizontal plane too.
            // A general 3-D rotation has no unique plane for opposite vectors.
            float turn = Mathf.Atan2(Vector3.Cross(forward, requested).y, Vector3.Dot(forward, requested));
            Vector3 controlled = Quaternion.AngleAxis(Mathf.Clamp(turn, -terminalSteeringEnvelope, terminalSteeringEnvelope) * Mathf.Rad2Deg, Vector3.up) * forward;
            terminalSteeringLimitedFrames++;
            // Preserve the altitude controller's elevation and the requested
            // attack/jink bearing. Native steering closes that bearing using
            // available lift, rather than a large instantaneous yaw incidence
            // which rotates its turn-bank/pitch response into an upward surge.
            // A raised obstacle floor or direct hull pursuit must not remove
            // this yaw limit: either can coincide with a large retarget turn.
            // Vertical clearance and the real impact point's elevation remain
            // unchanged. No velocity, force, seeker or bank/jink state is written.
            return current + controlled * horizontal.magnitude + Vector3.up * demand.y;
        }

        internal static bool TerrainSteeringRequired(bool avoidance, float floor, float lowAltitude, int selectedRouteSide)
        {
            // routeDirection slews after target assignment. Its angular lag
            // alone sets the broad telemetry flag even over unobstructed sea.
            // Only an actual raised floor or chosen detour needs this bypass.
            return avoidance && (floor > lowAltitude + 8f || selectedRouteSide != 0);
        }

        internal static float SafeSinkRate(float heightRemaining, float brakingAcceleration, float responseSeconds)
        {
            // v*t + v^2/(2*a) <= available height, including control response.
            float response = brakingAcceleration * responseSeconds;
            return Mathf.Max(0f, Mathf.Sqrt(response * response + 2f * brakingAcceleration * heightRemaining) - response);
        }

        private bool ReturnNativeAfterEmptySearch()
        {
            NaturalPikeGroupTargeting targeting = GetComponent<NaturalPikeGroupTargeting>();
            if (targeting == null || !targeting.YieldToNativeGuidance) return false;
            // Native Seek has already supplied destination. Stop replacing it
            // with a moving formation waypoint so native MissedTarget/SlowChecks
            // can retire this failed objective without any new seeker authority.
            LostTarget(); FlightPhase = "native-target-retirement";
            return true;
        }

        internal void LostTarget()
        {
            RestoreFinalCorrectionAuthority();
            terminalPass.Reset();
            RestoreLaunchTurnRate();
            if (missile != null && missile.LocalSim)
            {
                NaturalCruiseController controller = GetComponent<NaturalCruiseController>();
                if (controller != null) controller.Suspend();
            }
            observedTarget = PersistentID.None; closestTerminalRange = float.MaxValue; closePassObserved = false;
            terminalTarget = null; FlightPhase = "lost-track"; TerrainAvoidanceActive = false;
            // Target ownership and terrain observation have different lives.
            // A native radar loss must not cancel a ridge already ahead of the
            // missile. The steering-only fallback refreshes that observation
            // immediately on transition, then at a bounded cadence.
            if (!lostTargetTerrainMode) nextLostTargetTerrainProbe = 0f;
            lostTargetTerrainMode = true;
            DirectTerminalActive = false; ClearTerminalSurface();
            terrainApproachObserved = false;
        }

        internal bool TryLostTargetTerrainAim(GlobalPosition nativeAim, out GlobalPosition terrainAim)
        {
            terrainAim = nativeAim;
            if (!IsPike || LandAttack || missile == null || missile.disabled || !missile.LocalSim ||
                missile.rb == null || missile.targetID.IsValid || seeker == null || !RadarGuidance(seeker) ||
                !LaunchReady || ResoluteStrikeOrders.HasAreaObjective(missile)) return false;
            GlobalPosition current = missile.GlobalPosition();
            Vector3 nativeDirection = nativeAim - current;
            nativeDirection.y = 0f;
            float range = nativeDirection.magnitude;
            // Do not turn a passed native destination into a new moving goal.
            float nativeProgress = Vector3.Dot(nativeAim - current, missile.rb.velocity);
            if (!(range > 1f) || !PikeFlightProfile.Finite(range) ||
                !PikeFlightProfile.Finite(nativeAim.y) || !PikeFlightProfile.Finite(current.y) ||
                !PikeFlightProfile.Finite(missile.timeSinceSpawn) || !PikeFlightProfile.Finite(missile.speed) ||
                !(nativeProgress > 0f) || !PikeFlightProfile.Finite(nativeProgress)) return false;
            if (missile.timeSinceSpawn >= nextLostTargetTerrainProbe)
            {
                nextLostTargetTerrainProbe = missile.timeSinceSpawn + .28f;
                terrainObstacleActive = false;
                Vector3 projected = missile.rb.velocity * Mathf.Min(3f, range / Mathf.Max(1f, missile.speed));
                if (projected.sqrMagnitude > 1f)
                {
                    CorridorChecks++;
                    if (NavigationSphereCast(transform.position, 4f, projected.normalized, out RaycastHit hit,
                        projected.magnitude, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore))
                    {
                        Vector3 forward = projected; forward.y = 0f; forward.Normalize();
                        float top = Mathf.Max(hit.point.y - Datum.LocalSeaY,
                            Mathf.Max(GroundAt(hit.point + forward * .5f), GroundAt(hit.point + forward * 3f)));
                        terrainObstacleActive = true;
                        terrainObstacleDeadline = missile.timeSinceSpawn + hit.distance / Mathf.Max(1f, missile.speed);
                        terrainObstacleClearance = top + 35f;
                    }
                }
            }
            if (!terrainObstacleActive) return false;
            CommandedAltitude = terrainObstacleClearance;
            TerrainAvoidanceActive = true;
            terrainAim = LiftAim(current, nativeDirection / range, range, CommandedAltitude);
            // Only raise the native pitch. Its bearing and all seeker state
            // remain native, and the hook restores this aim before collision
            // and lifetime checks can observe it.
            terrainAim.x = nativeAim.x; terrainAim.z = nativeAim.z;
            // LiftAim uses a shorter steering lever; preserve its pitch when
            // expressing it at the original native horizontal distance.
            float lever = Mathf.Min(range, Mathf.Max(250f, missile.speed * 3f));
            terrainAim.y = current.y + (terrainAim.y - current.y) * range / lever;
            if (!(terrainAim.y > nativeAim.y) || !PikeFlightProfile.Finite(terrainAim.y))
            {
                terrainAim = nativeAim;
                return false;
            }
            lostTargetTerrainFrames++;
            return true;
        }

        internal object Capture()
        {
            return new Dictionary<string, object> {
                ["phase"] = FlightPhase, ["commandedAltitudeM"] = CommandedAltitude,
                ["lostTargetTerrainFrames"] = lostTargetTerrainFrames,
                ["ceilingAltitudeM"] = CeilingAltitude, ["seaSkimStartRangeM"] = SeaSkimStartRange,
                ["seaSkimSelected"] = seaSkimSelected, ["maximumLoftDegrees"] = MaximumLoftDegrees,
                ["profileObservedRangeM"] = ObservedRange, ["nativeObservationUsable"] = NativeObservationUsable,
                ["nativeRadarPosition"] = seeker != null ? (object)new[] { RadarPosition(seeker).x, RadarPosition(seeker).y, RadarPosition(seeker).z } : null,
                ["nativeRadarVelocity"] = seeker != null ? (object)new[] { RadarVelocity(seeker).x, RadarVelocity(seeker).y, RadarVelocity(seeker).z } : null,
                ["nativeRadarAcceleration"] = seeker != null ? (object)new[] { RadarAcceleration(seeker).x, RadarAcceleration(seeker).y, RadarAcceleration(seeker).z } : null,
                ["terminalSteeringEnvelopeDegrees"] = terminalSteeringEnvelope * Mathf.Rad2Deg,
                ["terminalSteeringEnvelopeUpdates"] = terminalSteeringEnvelopeUpdates,
                ["terminalSteeringLimitedFrames"] = terminalSteeringLimitedFrames,
                ["nativePikeLaunch"] = NativePikeLaunch, ["nativeGuidanceDelaySeconds"] = InitialFlightSeconds,
                ["launchClearanceM"] = LaunchClearanceM, ["launchOriginAltitudeM"] = launchAltitude,
                ["launcherCleared"] = clearedLauncher, ["launchSettled"] = launchSettled,
                ["nativeLaunchTurnRate"] = LaunchTurnRate, ["activeTurnRate"] = ActiveTurnRate,
                ["nativeLaunchGLimit"] = LaunchGLimit,
                ["commandedVerticalSpeedMps"] = CommandedVerticalSpeed, ["minimumObservedClearanceM"] = MinimumObservedClearance,
                ["maximumDescentSpeedMps"] = MaximumDescentSpeed, ["firstLowCruiseTimeSeconds"] = FirstLowCruiseTime,
                ["lowCruiseSeconds"] = LowCruiseSeconds, ["maximumRouteAngleDegrees"] = MaximumRouteAngle,
                ["terrainSamples"] = TerrainSamples, ["continuousCorridorChecks"] = CorridorChecks,
                ["terrainAvoidanceEvents"] = TerrainAvoidanceEvents, ["terrainAvoidanceActive"] = TerrainAvoidanceActive,
                ["terminalMissSelfDestruct"] = TerminalMissSelfDestruct,
                ["terminalPassArmed"] = terminalPass.Armed, ["terminalPassMissed"] = HasMissedTerminalPass,
                ["directTerminalActive"] = DirectTerminalActive, ["firstDirectTerminalSeconds"] = FirstDirectTerminalTime,
                ["terrainFloorAtDirectHandoffM"] = DirectTerminalTerrainFloorAtHandoff,
                ["directTerminalChecks"] = DirectTerminalChecks, ["directTerminalBlockedChecks"] = DirectTerminalBlockedChecks,
                ["terminalCollider"] = TerminalColliderName, ["terminalAim"] = new[] { terminalAim.x, terminalAim.y, terminalAim.z },
                ["nativeTerminalPartSelection"] = CaptureTerminalPart(),
                ["terminalPhysicalSurface"] = new[] { terminalPhysicalSurface.x, terminalPhysicalSurface.y, terminalPhysicalSurface.z },
                ["terminalBaseAim"] = new[] { terminalBaseAim.x, terminalBaseAim.y, terminalBaseAim.z },
                ["terminalAimGroundM"] = TerminalAimGround, ["terminalPathBlockReason"] = TerminalPathBlockReason,
                ["terminalBlockingCollider"] = TerminalBlockingCollider,
                ["currentTerrainM"] = currentGround, ["terrainFloorM"] = terrainFloor, ["approachGroundPeakM"] = approachGroundPeak,
                ["terrainObstacleActive"] = terrainObstacleActive,
                ["terrainObstacleSecondsRemaining"] = terrainObstacleActive ? (object)Mathf.Max(0f, terrainObstacleDeadline - missile.timeSinceSpawn) : null,
                ["terrainObstacleClearanceM"] = terrainObstacleActive ? (object)terrainObstacleClearance : null,
                ["nativeMaximumTurnRateDegreesPerSecond"] = ActiveTurnRate,
                ["finalCorrectionAuthorityActive"] = finalCorrection.Active,
                ["nativeCruiseController"] = GetComponent<NaturalCruiseController>() != null ?
                    GetComponent<NaturalCruiseController>().Capture() : null
            };
        }

        internal object CaptureTerminalPart()
        {
            if (!IsPike) return null;
            GlobalPosition point = terminalSurfaceSelected && terminalCollider != null ?
                terminalCollider.transform.TransformPoint(terminalSurfaceLocal).ToGlobalPosition() : default(GlobalPosition);
            return new Dictionary<string, object> {
                ["nativeMethod"] = "Ship.GetRandomPart", ["selectionAttempts"] = nativePartSelectionAttempts,
                ["selections"] = nativePartSelections, ["invalidations"] = nativePartInvalidations,
                ["heightInvalidations"] = nativeSurfaceHeightInvalidations,
                ["selectedAtSeconds"] = nativePartSelectedAt,
                ["selectedNativePart"] = terminalNativePart != null ? terminalNativePart.name : null,
                ["selectedNativePartId"] = terminalNativePart != null ? (object)terminalNativePart.id : null,
                ["associatedAttachedHull"] = terminalNativeHull != null ? terminalNativeHull.name : null,
                ["selectedPartsStillLiveAttached"] = PikeTerminalPartValid(),
                ["struckCollider"] = TerminalColliderName,
                ["cachedLocalSurface"] = terminalSurfaceSelected ?
                    (object)new[] { terminalSurfaceLocal.x, terminalSurfaceLocal.y, terminalSurfaceLocal.z } : null,
                ["cachedGlobalSurface"] = terminalSurfaceSelected ? (object)new[] { point.x, point.y, point.z } : null,
                ["selectedSurfaceStillAttached"] = terminalSurfaceSelected && IsTargetCollider(terminalCollider),
                ["surfaceReservationNeighborRadiusM"] = 5000f, ["surfaceReservationCollisionMarginM"] = 1f,
                ["surfaceReservationPeerChecks"] = surfaceReservationChecks, ["surfaceReservationRejections"] = surfaceReservationRejections,
                ["selectedSurfacePeerCount"] = selectedSurfacePeers,
                ["selectedRootCapsuleEnclosingRadiusM"] = selectedCapsuleRadius,
                ["minimumSurfaceMarginAtSelectionM"] = float.IsPositiveInfinity(selectedSurfaceMargin) ? null : (object)selectedSurfaceMargin,
                ["lastRejectedSurfacePeerId"] = lastRejectedSurfacePeer.IsValid ? lastRejectedSurfacePeer.ToString() : null,
                ["lastRejectedSurfaceMarginM"] = surfaceReservationRejections > 0 ? (object)lastRejectedSurfaceMargin : null
            };
        }
    }
}

