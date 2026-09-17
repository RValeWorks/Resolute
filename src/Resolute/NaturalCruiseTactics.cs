using System;
using System.Linq;
using UnityEngine;

namespace Resolute
{
    // Source configuration and Pike's authored evasion/countermeasures. Native
    // cruise navigation alone owns local grouping, waypoints and motor throttle.
    internal sealed class NaturalCruiseTactics : MonoBehaviour
    {
        public float FormationSpacing;
        public float GroupLeaderAltitude, GroupWingmanAltitude;
        public string WeaponKey;
        public float JinkAmount, JinkPeriod, JinkMinRange, JinkMaxRange, JinkMinSpeed;
        private Missile missile;
        private bool terminalStarted, terminalEvasion;
        private double terminalStartedAt, previousHeadingAt = double.NaN;
        private uint terminalPhaseKey;
        private bool terminalPhaseFromCohort;
        private double terminalPhaseClock;
        private float previousHeading;
        internal float CurrentHeadingDegrees, CurrentHeadingRateDegrees, CurrentBankDemandDegrees;
        public Transform VisualRoot;
        private Quaternion originalVisualRotation;
        private Vector3 previousVisualHeading;
        private double previousVisualAt = double.NaN;
        private float observedVisualTurnRate, appliedVisualBank;
        private NaturalPikeCountermeasures pikeCountermeasures;
        private NaturalCruiseGuidance guidance;
        internal int NearbyGroupMembers, NativeGroupingUpdates, FlareCount;
        internal float LargestJink, CommandedThrottle = 1f;
        internal bool NativeFlareParticlesSeen;
        internal Vector3 CurrentJink;
        internal float CurrentTargetRange;
        internal static void Configure(GameObject prefab, NaturalWeapons.SourceWeapon source, Encyclopedia encyclopedia)
        {
            var tactics = prefab.AddComponent<NaturalCruiseTactics>();
            tactics.VisualRoot = prefab.transform.Find("OriginalWeaponGeometry");
            if (tactics.VisualRoot == null)
                throw new InvalidOperationException("Pike requires its owned visual root for banking.");
            tactics.FormationSpacing = source.Guidance("GroupSpacing", .1f) * 1852f;
            tactics.GroupLeaderAltitude = source.Guidance("GroupLeaderAlt", 50f) * .3048f;
            tactics.GroupWingmanAltitude = source.Guidance("GroupWingmanAlt", 40f) * .3048f;
            tactics.WeaponKey = source.key;
            NaturalCruiseGuidance.Configure(prefab, source);
            OpticalSeekerCruiseMissile cruiseDonor = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                string.Equals(d.jsonKey, "AShM1", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.unitPrefab.GetComponent<OpticalSeekerCruiseMissile>()).FirstOrDefault(c => c != null);
            JinkEvasion native = cruiseDonor != null ? (JinkEvasion)NaturalWeapons.Get(cruiseDonor, "jinkEvasion") : null;
            tactics.JinkAmount = source.Guidance("WobblingStrength") > 0f ? (native != null ? native.amount : .05f) : 0f;
            tactics.JinkPeriod = native != null ? (float)NaturalWeapons.Get(native, "period") : 2f;
            tactics.JinkMinRange = native != null ? (float)NaturalWeapons.Get(native, "minRange") : 1250f;
            tactics.JinkMaxRange = native != null ? (float)NaturalWeapons.Get(native, "maxRange") : 5000f;
            tactics.JinkMinSpeed = native != null ? (float)NaturalWeapons.Get(native, "minSpeed") : 300f;
            if (source.Guidance("WobblingStrength") > 0f)
            {
                // SeaPower authors the terminal phase and wobbling strength,
                // while native JinkEvasion supplies the actual steering law.
                // The source's strength 8 uses the full native AShM scale.
                tactics.JinkAmount *= Mathf.Clamp(source.Guidance("WobblingStrength") / 8f, .25f, 2f);
                tactics.JinkPeriod /= Mathf.Max(.25f, source.Guidance("WobblingSpeed", 1f));
                tactics.JinkMaxRange = Mathf.Max(tactics.JinkMaxRange, source.Guidance("TerminalApproachDist") * 1852f);
            }
        }
        private void Awake()
        {
            missile = GetComponent<Missile>();
            guidance = GetComponent<NaturalCruiseGuidance>();
            pikeCountermeasures = GetComponent<NaturalPikeCountermeasures>();
            if (VisualRoot != null) originalVisualRotation = VisualRoot.localRotation;
        }
        internal GlobalPosition ModifyAim(GlobalPosition destination)
        {
            if (missile == null || missile.disabled || !missile.LocalSim) return destination;
            NaturalCruiseController controller = NaturalCruiseController.For(missile);
            PrepareFlight(destination, controller != null && controller.TerminalReleased);
            return destination;
        }

        internal void PrepareFlight(GlobalPosition nativeAim, bool terminalReleased)
        {
            CurrentJink = Vector3.zero;
            if (missile == null || missile.disabled || !missile.LocalSim) return;
            if (missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile))
            {
                NearbyGroupMembers = 0; CurrentJink = Vector3.zero; CommandedThrottle = 1f;
                terminalEvasion = false; CurrentHeadingDegrees = CurrentHeadingRateDegrees = CurrentBankDemandDegrees = 0f;
                previousHeadingAt = double.NaN;
                if (guidance != null) guidance.LostTarget();
                return;
            }
            NaturalCruiseController controller = NaturalCruiseController.For(missile);
            NearbyGroupMembers = controller.NearbyMissiles;
            NativeGroupingUpdates = controller.UpdatesWithNeighbors;
            CommandedThrottle = controller.Throttle;
            // Observe the native seeker's aim before any local route waypoint.
            // The phase owner, not this adapter, decides assignment/release.
            CurrentTargetRange = (nativeAim - missile.GlobalPosition()).magnitude;
            double now = Time.timeSinceLevelLoad;
            terminalEvasion = terminalReleased;
            if (terminalEvasion && !terminalStarted)
            {
                terminalStarted = true; terminalStartedAt = now;
                NaturalPikeFormation formation = missile.GetComponent<NaturalPikeFormation>();
                terminalPhaseFromCohort = formation != null && formation.LastCohortId != 0;
                terminalPhaseKey = terminalPhaseFromCohort ? formation.LastCohortId : missile.persistentID.Id;
            }
            // Independent release times and leader loss must not put members
            // into opposing turns. Keep the salvo key and use one mission clock;
            // each missile retains its own range-dependent amplitude and aim.
            terminalPhaseClock = now;
            CurrentHeadingDegrees = terminalEvasion ? (float)PikeEvasionPolicy.HeadingDegrees(CurrentTargetRange, now, terminalPhaseKey) : 0f;
            if (terminalEvasion)
            {
                double dt = now - previousHeadingAt;
                if (!double.IsNaN(dt) && dt > .00001 && dt <= .25)
                    CurrentHeadingRateDegrees = (float)((CurrentHeadingDegrees - previousHeading) / dt);
                else if (double.IsNaN(dt) || dt > .25)
                    CurrentHeadingRateDegrees = (float)PikeEvasionPolicy.NominalHeadingRateDegrees(CurrentTargetRange, now, terminalPhaseKey);
            }
            else CurrentHeadingRateDegrees = 0f;
            if (PikeEvasionPolicy.AmplitudeDegrees(CurrentTargetRange) == 0) CurrentHeadingRateDegrees = 0f;
            CurrentBankDemandDegrees = (float)PikeEvasionPolicy.BankDegrees(missile.speed, CurrentHeadingRateDegrees);
            previousHeading = CurrentHeadingDegrees; previousHeadingAt = now;
            if (pikeCountermeasures != null)
            {
                pikeCountermeasures.ObserveTargetRange(CurrentTargetRange);
                FlareCount = pikeCountermeasures.FlaresEmitted;
                NativeFlareParticlesSeen |= pikeCountermeasures.NativeFlareParticlesSeen;
            }
        }

        internal GlobalPosition PreviewTerminalHeading(GlobalPosition guidedAim)
        {
            if (!terminalEvasion || missile == null || missile.disabled || !missile.LocalSim || missile.targetID.NotValid ||
                Mathf.Abs(CurrentHeadingDegrees) < .000001f) return guidedAim;
            GlobalPosition current = missile.GlobalPosition();
            Vector3 vector = guidedAim - current;
            float horizontalRange = new Vector2(vector.x, vector.z).magnitude;
            if (horizontalRange < 1f) return guidedAim;
            // Native Steering normalizes this vector. Limit its preview length
            // instead of inventing kilometres of target-position displacement.
            // Preserve the guided elevation exactly; this is horizontal swerve.
            float length = Mathf.Min(horizontalRange, Mathf.Clamp(missile.speed * .75f, 250f, 1500f));
            Vector3 direction = new Vector3(vector.x, 0f, vector.z) / horizontalRange;
            direction = Quaternion.AngleAxis(CurrentHeadingDegrees, Vector3.up) * direction;
            Vector3 demand = direction * length; demand.y = vector.y * length / horizontalRange;
            return current + demand;
        }

        internal GlobalPosition ApplyTerminalHeading(GlobalPosition guidedAim)
        {
            GlobalPosition demand = PreviewTerminalHeading(guidedAim);
            CurrentJink = Vector3.zero;
            if (missile != null && demand != guidedAim)
            {
                Vector3 before = guidedAim - missile.GlobalPosition(), after = demand - missile.GlobalPosition();
                CurrentJink = after - before.normalized * after.magnitude;
                LargestJink = Mathf.Max(LargestJink, CurrentJink.magnitude);
            }
            return demand;
        }

        private void LateUpdate()
        {
            if (VisualRoot == null || missile == null || missile.disabled) return;
            double now = Time.timeSinceLevelLoad;
            double delta = now - previousVisualAt;
            float dt = !double.IsNaN(delta) && delta > 0 && delta <= .25 ? (float)delta : 0f;
            Vector3 horizontal = transform.forward; horizontal.y = 0f;
            if (dt > 0f && horizontal.sqrMagnitude > .01f && previousVisualHeading.sqrMagnitude > .01f)
            {
                float turn = Mathf.Atan2(Vector3.Cross(previousVisualHeading, horizontal).y,
                    Vector3.Dot(previousVisualHeading, horizontal)) * Mathf.Rad2Deg / dt;
                observedVisualTurnRate = Mathf.Lerp(observedVisualTurnRate, turn, 1f - Mathf.Exp(-dt / .12f));
            }
            else observedVisualTurnRate = 0f;
            previousVisualHeading = horizontal; previousVisualAt = now;
            // Remote copies have no authoritative seeker/phase calculation.
            // Their already-replicated heading supplies the same cosmetic cue.
            float demand = missile.LocalSim ? terminalEvasion ? CurrentBankDemandDegrees : 0f :
                missile.timeSinceSpawn >= 3.05f ? (float)PikeEvasionPolicy.BankDegrees(missile.speed, observedVisualTurnRate) : 0f;
            if (dt > 0f) appliedVisualBank = Mathf.Lerp(appliedVisualBank, demand, 1f - Mathf.Exp(-dt / .12f));
            appliedVisualBank = Mathf.Clamp(appliedVisualBank, -(float)PikeEvasionPolicy.MaximumBankDegrees,
                (float)PikeEvasionPolicy.MaximumBankDegrees);
            Vector3 forward = transform.forward;
            Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, forward);
            if (levelUp.sqrMagnitude < .01f) return;
            float physicalBank = TargetCalc.GetAngleOnAxis(levelUp, transform.up, forward);
            // Native axisymmetric missile forces do not need physical roll to
            // turn. Banking the visual model avoids rotating the native PID's
            // pitch/yaw history and subtracts root roll to keep the net cap.
            // Flare ports are children of the flight model; nozzle/afterburner
            // effects are separate and retain their original transforms.
            VisualRoot.localRotation = Quaternion.AngleAxis(-appliedVisualBank - physicalBank, Vector3.forward) * originalVisualRotation;
        }

        private void OnDisable()
        {
            if (VisualRoot != null) VisualRoot.localRotation = originalVisualRotation;
            appliedVisualBank = observedVisualTurnRate = 0f; previousVisualAt = double.NaN;
        }

        internal object Capture() => new {
            terminalStarted, terminalEvasion, periodSeconds = PikeEvasionPolicy.PeriodSeconds,
            synchronizedPhaseKey = terminalStarted ? (uint?)terminalPhaseKey : null,
            phaseKeySource = terminalPhaseFromCohort ? "retained-salvo-cohort" : "solo-missile",
            phaseClock = "Time.timeSinceLevelLoad", phaseClockSeconds = terminalStarted ? (double?)terminalPhaseClock : null,
            terminalStartGameSeconds = terminalStarted ? (double?)terminalStartedAt : null,
            headingDemandDegrees = CurrentHeadingDegrees, headingRateDegreesPerSecond = CurrentHeadingRateDegrees,
            bankDemandDegrees = CurrentBankDemandDegrees, bankLimitDegrees = PikeEvasionPolicy.MaximumBankDegrees,
            visualBankDegrees = appliedVisualBank, bankAuthority = "owned-visual-root; native rigidbody remains unmodified",
            currentOffsetM = new[] { CurrentJink.x, CurrentJink.y, CurrentJink.z },
            amplitudeDegrees = PikeEvasionPolicy.AmplitudeDegrees(CurrentTargetRange),
            cutoffAuthority = "authored-heading-profile-zero-at-0.6-nmi", motionAuthority = "native-Steering-PID-and-ApplyAero"
        };
    }

    internal sealed class NaturalFlareOwner : MonoBehaviour
    {
        internal Missile Owner;
        internal IRSource Source;
        private void Update()
        {
            if (Owner != null && (Owner.disabled || Source.intensity <= 0f || Vector3.Distance(transform.position, Owner.transform.position) > 100f)) Release();
        }
        private void OnDestroy() { Release(); }
        private void Release() { if (Owner != null) Owner.RemoveIRSource(Source); Owner = null; }
    }
}

