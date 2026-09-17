using System;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    // Bounded, event-owned missile registry. Each update visits only its own
    // eight-member cohort, never the world's missiles or ships.
    internal sealed class NaturalPikeFormation : MonoBehaviour
    {
        private static readonly PikeCohortPolicy Policy = new PikeCohortPolicy();
        private static readonly Dictionary<uint, NaturalPikeFormation> Live = new Dictionary<uint, NaturalPikeFormation>();
        private readonly List<Missile> neighbors = new List<Missile>(8);
        private Missile missile;
        private NaturalCruiseTactics tactics;
        private NaturalCruiseGuidance guidance;
        private PikeCohortPolicy.Cohort cohort;
        private uint id, targetId, ownerId, lastCohortId;
        private float aheadM, nativeDragDeceleration, responseSeconds;
        private float alongSpan, maximumLateralError, maximumHeadingError;
        private float lateralTrimM, lastCoordinateTime = -1f;
        private int members, slot;
        private uint leaderId;
        private bool finished, disabledCallback;
        private float firstCoordinationAge = -1f;
        private GlobalPosition referenceLeaderPosition;
        private Vector3 referenceLeaderHeading;
        private float desiredLeaderRelativeLane, ownLateralError, ownAlongError;
        private int referenceLeaderSlot;
        private bool followingLeader;
        private float nativeRetirementSpeed, nativeLiftAtTenDegrees;
        private float assemblyMinimumSpeed = PikeFormationFlightPolicy.CruiseSpeed;
        private float assemblyDensity, assemblyFinArea, assemblyMass, assemblyWindSpeed;
        private bool assemblyLaunchReady;
        internal List<Missile> Neighbors => neighbors;
        internal int Members => members;
        // Terminal release removes live membership, but the salvo's identity
        // must survive so its independent missiles can keep a shared turn phase.
        internal uint LastCohortId => lastCohortId;
        internal float SpeedCeiling { get; private set; } = PikeFormationFlightPolicy.CruiseSpeed;

        internal static NaturalPikeFormation For(Missile missile)
        {
            NaturalPikeFormation value = missile.GetComponent<NaturalPikeFormation>();
            if (value != null) return value;
            value = missile.gameObject.AddComponent<NaturalPikeFormation>();
            value.missile = missile; value.id = missile.persistentID.Id;
            value.tactics = missile.GetComponent<NaturalCruiseTactics>();
            value.guidance = missile.GetComponent<NaturalCruiseGuidance>();
            ARHSeeker seeker = missile.GetComponent<ARHSeeker>();
            value.nativeRetirementSpeed = seeker != null ? (float)NaturalWeapons.Get(seeker, "selfDestructAtSpeed") : 0f;
            value.nativeLiftAtTenDegrees = Mathf.Abs(missile.GetLiftCoeff(10f * Mathf.Deg2Rad));
            if (Live.Count < PikeCohortPolicy.MaximumLiveMembers && value.id != 0)
                Live[value.id] = value;
            missile.onDisableUnit += value.Disabled; value.disabledCallback = true;
            return value;
        }

        internal GlobalPosition Coordinate(GlobalPosition destination, ref float altitude, out float throttle)
            => Coordinate(destination, ref altitude, true, out throttle);

        internal GlobalPosition Coordinate(GlobalPosition destination, ref float altitude, bool lowCruise, out float throttle)
            => Coordinate(destination, ref altitude, lowCruise, false, out throttle);

        internal GlobalPosition Coordinate(GlobalPosition destination, ref float altitude, bool lowCruise, bool finalApproach, out float throttle)
        {
            float now = Time.timeSinceLevelLoad;
            float delta = lastCoordinateTime >= 0f ? Mathf.Clamp(now - lastCoordinateTime, 0f, 1f) : 0f;
            lastCoordinateTime = now;
            throttle = 1f; SpeedCeiling = PikeFormationFlightPolicy.CruiseSpeed;
            neighbors.Clear(); members = 0; aheadM = 0f;
            alongSpan = 0f; maximumLateralError = 0f; maximumHeadingError = 0f;
            followingLeader = false; ownLateralError = 0f; ownAlongError = 0f;
            if (finished || missile == null || missile.disabled || missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile) || missile.owner == null ||
                tactics == null || !Live.ContainsKey(id)) return destination;
            uint target = missile.targetID.Id, owner = missile.owner.persistentID.Id;
            uint orderedGroup;
            GlobalPosition orderedPoint;
            Vector3 orderedCourse;
            bool ordered = ResoluteStrikeOrders.TryGetOrder(missile, out orderedGroup, out orderedPoint, out orderedCourse);
            Vector3 direction = destination - missile.GlobalPosition(); direction.y = 0f;
            if (ordered && orderedCourse.sqrMagnitude > .5f) direction = orderedCourse;
            else if (direction.sqrMagnitude < 1f)
            {
                if (cohort == null) return destination;
                direction = new Vector3(cohort.HeadingX, 0f, cohort.HeadingZ);
            }
            else direction.Normalize();
            // Ship-issued plans reserve exact membership. Only unmanaged
            // diagnostic/legacy launches use the initial native corridor.
            // Once joined, a
            // wingman's stale or replaced individual objective cannot tear it
            // out of the formation or change the leader's navigation frame.
            if (ownerId != owner)
            {
                Policy.Leave(id); cohort = null; ownerId = owner;
                lateralTrimM = 0f;
            }
            targetId = target;
            if (cohort == null)
                cohort = Policy.Join(id, owner, target, now - missile.timeSinceSpawn, now,
                    direction.x, direction.z, NaturalSurfaceSalvo.LaunchInterval(missile.owner, missile.definition), orderedGroup);
            if (cohort == null) return destination;
            if (ordered && orderedCourse.sqrMagnitude > .5f)
            { cohort.HeadingX = orderedCourse.x; cohort.HeadingZ = orderedCourse.z; }
            lastCohortId = cohort.Id;
            NaturalPikeGroupTargeting.Bind(missile, cohort.Id, cohort.HeadingX, cohort.HeadingZ);
            slot = 0;
            leaderId = 0;
            NaturalPikeFormation leader = null;
            for (int i = 0; i < cohort.Members.Length; i++)
            {
                NaturalPikeFormation member;
                if (!TryLiveMember(cohort.Members[i], out member)) continue;
                if (leader == null) { leader = member; referenceLeaderSlot = i; }
                if (member.id == id) slot = i;
                else neighbors.Add(member.missile);
                members++;
            }
            NaturalPikeGroupTargeting targeting = missile.GetComponent<NaturalPikeGroupTargeting>();
            Missile chosen;
            NaturalPikeFormation selected;
            if (targeting != null && targeting.TryGetFormationLeader(out chosen) && chosen != null &&
                TryLiveMember(chosen.persistentID.Id, out selected))
            {
                int index = Array.IndexOf(cohort.Members, chosen.persistentID.Id);
                if (index >= 0) { leader = selected; referenceLeaderSlot = index; }
            }
            if (leader == null) return destination;
            leaderId = leader.id;
            if (targeting != null && targeting.LeaderSearching)
                altitude = Mathf.Max(altitude, PikeGroupTargetingPolicy.LeaderSearchAltitude);
            referenceLeaderPosition = leader.missile.GlobalPosition();
            direction = HorizontalHeading(leader.missile, cohort);
            referenceLeaderHeading = direction;
            Vector3 right = Vector3.Cross(Vector3.up, direction).normalized;
            float leaderLane = PikeFormationFlightPolicy.SlotOffset(referenceLeaderSlot, tactics.FormationSpacing);
            desiredLeaderRelativeLane = PikeFormationFlightPolicy.SlotOffset(slot, tactics.FormationSpacing) - leaderLane;
            Vector3 fromLeader = missile.GlobalPosition() - referenceLeaderPosition;
            float ownAlong = Vector3.Dot(fromLeader, direction);
            ownAlongError = -ownAlong;
            ownLateralError = desiredLeaderRelativeLane - Vector3.Dot(fromLeader, right);
            float rearAlong = ownAlong, frontAlong = ownAlong;
            float rearSpeed = Vector3.Dot(missile.rb.velocity, direction);
            float ownSpeed = rearSpeed;
            for (int i = 0; i < cohort.Members.Length; i++)
            {
                NaturalPikeFormation member;
                if (!TryLiveMember(cohort.Members[i], out member)) continue;
                Vector3 memberOffset = member.missile.GlobalPosition() - referenceLeaderPosition;
                float along = Vector3.Dot(memberOffset, direction);
                if (along < rearAlong) { rearAlong = along; rearSpeed = Vector3.Dot(member.missile.rb.velocity, direction); }
                frontAlong = Mathf.Max(frontAlong, along);
                float lateral = Vector3.Dot(memberOffset, right);
                maximumLateralError = Mathf.Max(maximumLateralError,
                    Mathf.Abs(lateral - (PikeFormationFlightPolicy.SlotOffset(i, tactics.FormationSpacing) - leaderLane)));
                Vector3 velocity = member.missile.rb.velocity; velocity.y = 0f;
                if (velocity.sqrMagnitude > 1f) maximumHeadingError = Mathf.Max(maximumHeadingError, Vector3.Angle(velocity, direction));
            }
            if (firstCoordinationAge < 0f) firstCoordinationAge = missile.timeSinceSpawn;
            aheadM = Mathf.Max(0f, ownAlong - rearAlong);
            alongSpan = frontAlong - rearAlong;
            nativeDragDeceleration = EstimateNativeDragDeceleration();
            responseSeconds = PikeFormationFlightPolicy.ResponseSeconds(aheadM, nativeDragDeceleration);
            SpeedCeiling = PikeFormationFlightPolicy.SpeedCeiling(aheadM, rearSpeed, ownSpeed,
                tactics.FormationSpacing, nativeDragDeceleration, members, assemblyMinimumSpeed);
            // The high flight profile supplies its own climb/descent altitude.
            // Group altitude offsets apply only after low cruise is selected.
            if (lowCruise) altitude = Mathf.Max(altitude, !finalApproach && id == leaderId ? tactics.GroupLeaderAltitude : tactics.GroupWingmanAltitude);
            // The leader follows its authorized search corridor while awaiting
            // allocation. Wingmen follow its
            // actual position and heading, never a mean of separate targets.
            if (id == leaderId || members < 2)
            {
                lateralTrimM = 0f;
                if (targeting != null && targeting.TryGetSearchCourse(out Vector3 searchCourse))
                {
                    // The group's approach course freezes when search begins
                    // and survives leader replacement. Do not chase the search
                    // point behind us or feed body-heading drift back into it.
                    GlobalPosition searchAhead = missile.GlobalPosition() + searchCourse * PikeAreaSearchPolicy.LookAhead(missile.speed);
                    searchAhead.y = altitude; return searchAhead;
                }
                if (ordered && (targeting == null || targeting.AwaitingAssignment) &&
                    ResoluteStrikeOrders.TryGetGuidancePoint(missile, out orderedPoint, out orderedCourse))
                {
                    if (ResoluteStrikeOrders.TryGetAreaPoint(missile, out GlobalPosition area))
                        orderedPoint = NaturalPikeAreaSearch.NavigationPoint(missile, area, orderedCourse);
                    orderedPoint.y = altitude; return orderedPoint;
                }
                return destination;
            }
            followingLeader = true;
            float lookAhead = Mathf.Max(1200f, missile.speed * 6f);
            // Native local separation creates a steady outward bias even with
            // its correct donor gain. Bounded lane trim rejects that bias while
            // retaining native avoidance/terrain constraints. The actual AShM1
            // gain 100 needs about 7.6 degrees at the outermost eight-member lane;
            // bound trim below the native ten-degree heading-step authority.
            float trimLimit = lookAhead * .15838444f; // tan(9 degrees)
            lateralTrimM = Mathf.Clamp(lateralTrimM + ownLateralError * delta * .3f, -trimLimit, trimLimit);
            GlobalPosition aim = referenceLeaderPosition + direction * lookAhead +
                right * (desiredLeaderRelativeLane + lateralTrimM);
            // A leader's scan pop-up is not copied into wingmen's altitude.
            // Their authored profile and native terrain clearance still own y.
            aim.y = altitude;
            return aim;
        }

        private bool TryLiveMember(uint memberId, out NaturalPikeFormation member)
        {
            if (!Live.TryGetValue(memberId, out member) || member == null || member.finished ||
                member.missile == null || member.missile.disabled || member.missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(member.missile) ||
                member.missile.owner != missile.owner) return false;
            NaturalCruiseController controller = member.missile.GetComponent<NaturalCruiseController>();
            return controller == null || !controller.TerminalReleased;
        }

        private static Vector3 HorizontalHeading(Missile leader, PikeCohortPolicy.Cohort group)
        {
            Vector3 heading = leader.transform.forward; heading.y = 0f;
            if (heading.sqrMagnitude < .0001f) { heading = leader.rb.velocity; heading.y = 0f; }
            if (heading.sqrMagnitude < .0001f) heading = new Vector3(group.HeadingX, 0f, group.HeadingZ);
            return heading.normalized;
        }

        private float EstimateNativeDragDeceleration()
        {
            // Same aerodynamic inputs as native ApplyAero. This estimates a
            // response time only; native forces and fuel/mass remain untouched.
            Vector3 wind = NetworkSceneSingleton<LevelInfo>.i.GetWind(missile.GlobalPosition());
            Vector3 airVelocity = missile.rb.velocity - wind;
            float speed = airVelocity.magnitude;
            float angle = Vector3.Angle(transform.forward, airVelocity) * Mathf.Deg2Rad;
            float density = GameAssets.i.airDensityAltitude.Evaluate(missile.GlobalPosition().y * .001f);
            float area = (float)NaturalWeapons.Get(missile, "currentFinArea");
            assemblyDensity = density; assemblyFinArea = area;
            assemblyMass = missile.rb.mass; assemblyWindSpeed = wind.magnitude;
            assemblyLaunchReady = guidance != null && guidance.LaunchReady;
            assemblyMinimumSpeed = assemblyLaunchReady
                ? PikeFormationFlightPolicy.AssemblyMinimumSpeed(density, area, nativeLiftAtTenDegrees,
                    assemblyMass, assemblyWindSpeed, nativeRetirementSpeed)
                : PikeFormationFlightPolicy.CruiseSpeed * PikeFormationFlightPolicy.MinimumSpeedFraction;
            float drag = missile.GetDragCoef(angle) * density * speed * speed * .5f * area;
            float supersonic = (float)NaturalWeapons.Get(missile, "supersonicDrag");
            float sound = LevelInfo.GetSpeedOfSound(missile.GlobalPosition().y);
            if (supersonic > 0f && speed > 1.1f * sound) drag *= 1f + supersonic;
            else if (supersonic > 0f && speed > .9f * sound)
            {
                float blend = (.1f - Mathf.Min(Mathf.Abs((sound - speed) / sound), .1f)) / .1f;
                drag *= 1f + blend * blend * blend * (supersonic + .15f);
            }
            return Mathf.Max(0f, drag / Mathf.Max(1f, missile.rb.mass));
        }

        internal void Release(bool terminal)
        {
            Policy.Leave(id); cohort = null; neighbors.Clear(); members = 0;
            SpeedCeiling = PikeFormationFlightPolicy.CruiseSpeed;
            lateralTrimM = 0f; lastCoordinateTime = -1f;
            followingLeader = false;
            finished = terminal;
            if (!terminal) { targetId = 0; ownerId = 0; }
        }
        private void Disabled(Unit unit) { Release(true); Remove(); }
        private void OnDestroy() { Release(true); Remove(); }
        private void Remove()
        {
            NaturalPikeFormation current;
            if (Live.TryGetValue(id, out current) && ReferenceEquals(current, this)) Live.Remove(id);
            if (disabledCallback && missile != null) missile.onDisableUnit -= Disabled;
            disabledCallback = false;
        }
        internal object Capture() => new {
            cohortId = cohort != null ? cohort.Id : 0u, orderId = cohort != null ? cohort.OrderId : 0u, lastCohortId, targetId, ownerId,
            members, slot, leaderId, firstCoordinationAgeSeconds = firstCoordinationAge,
            aheadOfLastMemberM = aheadM, nativeDragDecelerationMps2 = nativeDragDeceleration,
            responseSeconds, lateralTrimM, formationSpeedCeilingMps = SpeedCeiling,
            followingLeader, referenceLeaderSlot,
            referenceLeaderPositionM = new[] { referenceLeaderPosition.x, referenceLeaderPosition.y, referenceLeaderPosition.z },
            referenceLeaderHorizontalHeading = new[] { referenceLeaderHeading.x, referenceLeaderHeading.y, referenceLeaderHeading.z },
            desiredLeaderRelativeLaneM = desiredLeaderRelativeLane, ownLateralErrorM = ownLateralError, ownAlongErrorM = ownAlongError,
            assemblyMinimumSpeedMps = assemblyMinimumSpeed,
            assemblyFloorInputs = new { launchReady = assemblyLaunchReady, airDensityKgM3 = assemblyDensity,
                finAreaM2 = assemblyFinArea, liftCoefficientAtTenDegrees = nativeLiftAtTenDegrees,
                massKg = assemblyMass, windSpeedMps = assemblyWindSpeed, nativeRetirementSpeedMps = nativeRetirementSpeed },
            longitudinalSpanM = alongSpan, maximumLateralErrorM = maximumLateralError, maximumHeadingErrorDegrees = maximumHeadingError,
            physicallyAligned = members > 1 && alongSpan <= tactics.FormationSpacing * .1f &&
                maximumLateralError <= tactics.FormationSpacing * .1f && maximumHeadingError <= 2f,
            terminalReleased = finished,
            groupTargeting = GetComponent<NaturalPikeGroupTargeting>()?.Capture(),
            maximumCohortSize = PikeCohortPolicy.MaximumMembers,
            joinWindowSeconds = cohort != null ? cohort.JoinWindowSeconds : PikeCohortPolicy.JoinWindow(PikeCohortPolicy.DefaultSalvoIntervalSeconds)
        };
    }

}
