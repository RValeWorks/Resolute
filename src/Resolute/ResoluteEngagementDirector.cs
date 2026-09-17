using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Owns admission, not execution. Native FireControl still creates every
    // reservation, cycles physical banks, fires and cancels its own queue.
    internal sealed class ResoluteEngagementDirector : MonoBehaviour
    {
        private sealed class Battery
        {
            internal FireControl Controller;
            internal WeaponInfo Info;
            internal string Key;
            internal WeaponStation[] Stations;
            internal int InitialAmmo;
            internal bool Surface;
            internal int DetailedCursor;
            internal uint PreviousFirstCandidate;
            internal float QueueExpectedUntil;
            internal int Ammo => Stations.Sum(s => Math.Max(0, s.Ammo));
        }
        private sealed class WardWait { internal float Until; }
        private sealed class Candidate
        {
            internal object Native;
            internal TrackingInfo Track;
            internal Unit Target;
            internal int Demand;
            internal float Urgency, Score;
            internal bool Emergency;
            internal int Groups;
        }

        private static readonly ConditionalWeakTable<FireControl, ResoluteEngagementDirector> Owners =
            new ConditionalWeakTable<FireControl, ResoluteEngagementDirector>();
        private static readonly Type TargetType = AccessTools.Inner(typeof(FireControl), "FireControlTarget");
        private static readonly FieldInfo TargetTrack = AccessTools.Field(TargetType, "trackingInfo");
        private static readonly FieldInfo TargetDemand = AccessTools.Field(TargetType, "shotsRequired");
        private static readonly FieldInfo TargetOpportunity = AccessTools.Field(TargetType, "opportunityThreat");
        private static readonly FieldInfo SalvoTargets = AccessTools.Field(typeof(FireControl), "salvoTargets");
        private static readonly FieldInfo Planning = AccessTools.Field(typeof(FireControl), "planningSalvo");
        private static readonly FieldInfo FireInterval = AccessTools.Field(typeof(MissileLauncher), "fireInterval");
        private static readonly FieldInfo LastFired = AccessTools.Field(typeof(Weapon), "lastFired");
        private readonly List<Missile> missiles = new List<Missile>();
        private readonly List<Battery> batteries = new List<Battery>();
        private readonly Queue<object> history = new Queue<object>();
        // One short grace period per real track. Repeated Ward queues cannot
        // renew it indefinitely; weak keys require no periodic world scan.
        private readonly ConditionalWeakTable<TrackingInfo, WardWait> wardWaits =
            new ConditionalWeakTable<TrackingInfo, WardWait>();
        private Ship ship;
        private ResoluteStrikeAssessment assessment;
        private int candidateCursor;
        private float nextSurfaceReview;
        private bool registered;
        internal int PlansReviewed, PlansAdmitted, ShotsAdmitted;

        internal static bool Owns(Unit unit) => unit != null && unit.GetComponent<ResoluteEngagementDirector>() != null;
        internal static bool TryGet(FireControl controller, out ResoluteEngagementDirector director) =>
            Owners.TryGetValue(controller, out director) && director != null && director.registered;

        internal static void Configure(Ship ship)
        {
            if (ship.GetComponent<ResoluteEngagementDirector>() == null)
                ship.gameObject.AddComponent<ResoluteEngagementDirector>();
        }

        internal void Register(Ship owner, NaturalFireControlGroups router)
        {
            if (registered) return;
            ship = owner;
            assessment = new ResoluteStrikeAssessment(owner);
            foreach (NaturalFireControlGroups.Group group in router.Groups)
            {
                string key = NaturalMissileTargeting.Key(group.Info);
                var stations = ((List<WeaponStation>)NaturalWeapons.Get(group.Controller, "subscribedWeaponStations")).ToArray();
                var battery = new Battery { Controller = group.Controller, Info = group.Info, Key = key,
                    Stations = stations, InitialAmmo = stations.Sum(s => Math.Max(0, s.Ammo)),
                    Surface = SurfaceSalvoOrderPolicy.Applies(key) };
                batteries.Add(battery);
                Owners.Remove(group.Controller); Owners.Add(group.Controller, this);
            }
            owner.onRegisterMissile += MissileRegistered;
            owner.onDeregisterMissile += MissileDeregistered;
            registered = true;
        }

        private void MissileRegistered(Missile missile) { if (missile != null && !missiles.Contains(missile)) missiles.Add(missile); assessment?.Register(missile); }
        private void MissileDeregistered(Missile missile) { missiles.Remove(missile); assessment?.Deregister(missile); }
        private void Update() { if (registered && ship != null && ship.IsServer && !ship.disabled) assessment?.Tick(); }
        private void OnDestroy()
        {
            if (ship != null)
            {
                ship.onRegisterMissile -= MissileRegistered;
                ship.onDeregisterMissile -= MissileDeregistered;
            }
            foreach (Battery battery in batteries) Owners.Remove(battery.Controller);
            assessment?.Dispose();
            if (ship != null) ResoluteStrikeOrders.ResetOwner(ship);
            registered = false;
        }

        private bool Busy(Battery battery) => (bool)Planning.GetValue(battery.Controller);

        private WeaponStation EnsureRepresentative(Battery battery, bool readOnly = false)
        {
            if (Busy(battery) && !readOnly) return null;
            var stations = (List<WeaponStation>)NaturalWeapons.Get(battery.Controller, "subscribedWeaponStations");
            for (int i = 0; i < stations.Count; i++)
            {
                WeaponStation station = stations[i];
                if (station.Ammo <= 0 || station.Reloading) continue;
                bool usable = false;
                foreach (Weapon weapon in station.Weapons)
                {
                    Vector3 position;
                    if (NaturalMissileTargeting.TryLaunchPosition(weapon as MissileLauncher, out position))
                    { usable = true; break; }
                }
                if (!usable) continue;
                // Native HQ assessment uses index zero for every contact.
                // Preserve all banks and their relative order; change only an
                // idle list whose representative can no longer launch.
                if (i > 0 && !readOnly) { stations.RemoveAt(i); stations.Insert(0, station); }
                return station;
            }
            return null;
        }

        internal void PrepareAssessment(FireControl controller)
        {
            if (ship == null || ship.disabled || !ship.IsServer) return;
            Battery battery = batteries.FirstOrDefault(b => b.Controller == controller);
            if (battery != null) EnsureRepresentative(battery);
        }

        private bool SurfaceOutstanding(FireControl requesting)
        {
            foreach (Battery battery in batteries)
                if (battery.Surface && battery.Controller != requesting && Busy(battery)) return true;
            foreach (Missile missile in missiles)
            {
                if (missile == null || missile.disabled || !SurfaceSalvoOrderPolicy.Applies(
                    NaturalMissileTargeting.Key(missile.GetWeaponInfo()))) continue;
                // Pending formation assignment and native target retirement
                // can temporarily clear targetID. That is not a completed wave:
                // retain its commitment until the missile actually ends. This
                // also lets Pike finish a dynamic transfer before new spending.
                return true;
            }
            return false;
        }

        private float ImpactTime(Unit target, TrackingInfo track)
        {
            Missile incoming = target as Missile;
            Unit protectedUnit;
            if (incoming == null || !track.Observed() || !incoming.targetID.IsValid ||
                !UnitRegistry.TryGetUnit(incoming.targetID, out protectedUnit) || protectedUnit == null ||
                protectedUnit.disabled || protectedUnit.NetworkHQ != ship.NetworkHQ || incoming.rb == null)
                return float.PositiveInfinity;
            Vector3 offset = protectedUnit.GlobalPosition() - track.GetPosition();
            Vector3 velocity = incoming.rb.velocity - (protectedUnit.rb != null ? protectedUnit.rb.velocity : Vector3.zero);
            return ResoluteEngagementPolicy.TimeToImpact(offset.magnitude, Vector3.Dot(velocity, offset.normalized));
        }

        private bool PhysicalCandidate(Battery battery, Unit target, TrackingInfo track, out float flight, out float score, bool readOnly = false)
        {
            flight = float.PositiveInfinity; score = 0f;
            if (!ResoluteCommandApi.AllowAutomatic(ship, battery.Stations.FirstOrDefault(), target)) return false;
            if (ResoluteEngagementPolicy.Budget(battery.Key, battery.Surface, battery.Ammo, battery.InitialAmmo,
                assessment != null && assessment.SelfDefense(target), 1) <= 0 ||
                !NaturalMissileTargeting.RoleAllows(battery.Key, target)) return false;
            WeaponStation representative = EnsureRepresentative(battery, readOnly);
            if (representative == null || !(CombatAI.AnalyzeTarget(representative, ship, track).GetCombinedScore() > 0f))
                return false;
            // Offensive expenditure must be commensurate with the known target.
            if (battery.Surface && target.definition.value < battery.Info.costPerRound) return false;
            foreach (WeaponStation station in battery.Stations)
            {
                if (station.Ammo <= 0 || station.Reloading) continue;
                foreach (Weapon weapon in station.Weapons)
                {
                    MissileLauncher launcher = weapon as MissileLauncher;
                    Vector3 position; string reason;
                    if (launcher == null || !NaturalMissileTargeting.TryLaunchPosition(launcher, out position) ||
                        !NaturalMissileTargeting.Evaluate(battery.Info, ship, target, position, false, out reason)) continue;
                    OpportunityThreat native = CombatAI.AnalyzeTarget(station, ship, track);
                    if (!(native.GetCombinedScore() > 0f)) continue;
                    float wait = Mathf.Max(0f, (float)LastFired.GetValue(launcher) +
                        (float)FireInterval.GetValue(launcher) - Time.timeSinceLevelLoad);
                    float distance = (track.GetPosition() - position.ToGlobalPosition()).magnitude;
                    float estimate = wait + 3f + distance / Mathf.Max(1f, battery.Info.GetMaxSpeed()) * 1.3f;
                    if (!ResoluteEngagementPolicy.Finite(estimate)) continue;
                    flight = Mathf.Min(flight, estimate); score = Mathf.Max(score, native.GetCombinedScore());
                }
            }
            return ResoluteEngagementPolicy.Finite(flight);
        }

        private Battery ChooseBattery(Unit target, TrackingInfo track, FireControl requesting)
        {
            Battery best = null;
            float bestRank = float.PositiveInfinity;
            bool bestDedicated = false;
            bool ballistic = NaturalBallisticSelection.IsEligible(target);
            float impact = ImpactTime(target, track);
            foreach (Battery battery in batteries)
            {
                bool busy = battery.Controller != requesting && Busy(battery);
                float queueWait = busy ? battery.QueueExpectedUntil - Time.timeSinceLevelLoad : 0f;
                // Only the inexpensive medium layer may briefly defer another
                // layer. Unknown/manual queues and overdue queues never do so.
                if (busy && (battery.Key != "rsl_mrsam" || queueWait <= 0f || queueWait > 3f)) continue;
                int demand = battery.Surface && assessment != null
                    ? assessment.Demand(target, track, battery.Info.CalcAttacksNeeded(target),
                        battery.Key == "rsl_ashm" ? assessment.GroupsFor(target) : 1)
                    : ResoluteEngagementPolicy.AdditionalDemand(battery.Info.CalcAttacksNeeded(target), track.missileAttacks, track.attackers);
                if (demand <= 0) continue;
                float flight, score;
                if (!PhysicalCandidate(battery, target, track, out flight, out score, busy)) continue;
                // Conventional SAMs inherit broad altitude envelopes but do
                // not have T/X's thin-air control. Their cheaper price must not
                // reserve a midcourse ballistic away from an eligible X.
                // PhysicalCandidate still enforces each dedicated phase/range,
                // observation, ammunition, damage and native scoring gate.
                bool dedicated = ballistic && NaturalBallisticSelection.IsBastion(battery.Info);
                if (busy)
                {
                    if (!ResoluteEngagementPolicy.Finite(impact) || flight + queueWait + 4f >= impact) continue;
                    WardWait grace;
                    if (!wardWaits.TryGetValue(track, out grace))
                    { grace = new WardWait { Until = Time.timeSinceLevelLoad + 3f }; wardWaits.Add(track, grace); }
                    if (Time.timeSinceLevelLoad >= grace.Until) continue;
                    flight += queueWait;
                }
                float rank = battery.Surface ? battery.Info.costPerRound + (battery.Key == "rsl_cruise" && target is Ship ? 100f : 0f)
                    : ResoluteEngagementPolicy.InterceptorRank(battery.Info.costPerRound, flight, impact);
                if (best == null || dedicated && !bestDedicated || dedicated == bestDedicated &&
                    (rank < bestRank || rank == bestRank && string.CompareOrdinal(battery.Key, best.Key) < 0))
                { best = battery; bestRank = rank; bestDedicated = dedicated; }
            }
            return best;
        }

        internal bool PrepareSalvo(FireControl controller)
        {
            PlansReviewed++;
            Battery battery = batteries.FirstOrDefault(b => b.Controller == controller);
            if (battery == null || ship == null || ship.disabled || !ship.IsServer || ship.NetworkHQ == null) return false;
            IList pending = (IList)SalvoTargets.GetValue(controller);
            // This is the entry boundary, before native PlanSalvo starts its
            // asynchronous enumeration. Never rewrite an executing queue.
            if (Busy(battery)) return false;
            assessment.Tick();
            // Reserve eligibility belongs to the actual selected threat. This
            // upper budget only permits examining an emergency opportunity.
            int budget = ResoluteEngagementPolicy.Budget(battery.Key, battery.Surface, battery.Ammo, battery.InitialAmmo, true,
                battery.Key == "rsl_ashm" ? ResoluteEngagementPolicy.MaximumStrikeGroups : 1);
            string held = budget <= 0 ? "empty-battery" : null;
            if (battery.Surface)
            {
                foreach (Battery other in batteries)
                    if (other.Surface && other != battery && Busy(other)) { budget = 0; held = "surface-queue-executing"; break; }
                budget = Math.Min(budget, Math.Max(0, ResoluteEngagementPolicy.MaximumCommittedSurfaceShots -
                    assessment.ActiveCount - ResoluteStrikeOrders.PendingCount(ship)));
            }
            var candidates = new List<Candidate>(Math.Min(128, pending.Count));
            if (budget > 0)
                for (int scanned = 0, count = Math.Min(128, pending.Count); scanned < count; scanned++)
                {
                    object native = pending[(candidateCursor + scanned) % pending.Count];
                    TrackingInfo track = (TrackingInfo)TargetTrack.GetValue(native);
                    Unit target;
                    if (track == null || !track.TryGetUnit(out target) || target == null || target.disabled ||
                        target.NetworkHQ == null || target.NetworkHQ == ship.NetworkHQ) continue;
                    assessment.Observe(target, track);
                    bool emergency = battery.Surface && assessment.SelfDefense(target);
                    int groups = battery.Key == "rsl_ashm" ? assessment.GroupsFor(target) : 1;
                    int demand;
                    if (battery.Surface)
                    {
                        if (!assessment.TrackUsable(target, track) || !assessment.MayReassess(target)) continue;
                        demand = assessment.Demand(target, track, battery.Info.CalcAttacksNeeded(target), groups);
                        demand = Math.Min(demand, Mathf.FloorToInt(target.definition.value / Mathf.Max(.01f, battery.Info.costPerRound)));
                        if (ResoluteEngagementPolicy.Budget(battery.Key, true, battery.Ammo, battery.InitialAmmo, emergency, groups) <= 0) continue;
                    }
                    else
                    {
                        demand = ResoluteEngagementPolicy.AdditionalDemand(battery.Info.CalcAttacksNeeded(target), track.missileAttacks, track.attackers);
                        demand = Math.Min(demand, ResoluteEngagementPolicy.AdditionalDemand((float)TargetDemand.GetValue(native), 0, 0));
                    }
                    if (demand <= 0) continue;
                    candidates.Add(new Candidate { Native = native, Track = track, Target = target, Demand = demand,
                        Emergency = emergency, Groups = groups, Urgency = ImpactTime(target, track),
                        Score = ((OpportunityThreat)TargetOpportunity.GetValue(native)).GetCombinedScore() });
                }
            candidateCursor = pending.Count > 0 ? (candidateCursor + 128) % pending.Count : 0;
            candidates.Sort((a, b) => {
                int order = b.Emergency.CompareTo(a.Emergency);
                if (order == 0) order = a.Urgency.CompareTo(b.Urgency);
                if (order == 0) order = b.Score.CompareTo(a.Score);
                return order != 0 ? order : a.Target.persistentID.Id.CompareTo(b.Target.persistentID.Id);
            });
            // Rank cheaply first, then ask expensive physical/native suitability
            // questions only until the small batch has enough eligible contacts.
            var eligible = new List<Candidate>(budget);
            Candidate primary = null;
            int detailedChecks = 0;
            int detailedStart = candidates.Count > 0 ? battery.DetailedCursor % candidates.Count : 0;
            if (candidates.Count > 0)
            {
                Candidate first = candidates[0];
                if (battery.PreviousFirstCandidate != first.Target.persistentID.Id && (first.Emergency || first.Urgency < 20f)) detailedStart = 0;
                battery.PreviousFirstCandidate = first.Target.persistentID.Id;
            }
            int visited = 0;
            for (; visited < candidates.Count; visited++)
            {
                if (eligible.Count >= budget || detailedChecks >= 24) break;
                Candidate candidate = candidates[(detailedStart + visited) % candidates.Count];
                if (battery.Surface && primary != null && !assessment.SameGroup(primary.Target, candidate.Target)) continue;
                // Emergency release does not spend the reserve on unrelated
                // ships merely because they are near the active attacker.
                if (battery.Surface && primary != null && primary.Emergency && !candidate.Emergency &&
                    battery.Ammo <= ResoluteEngagementPolicy.Reserve(battery.Key, battery.InitialAmmo)) continue;
                detailedChecks++;
                if (ChooseBattery(candidate.Target, candidate.Track, controller) != battery) continue;
                if (primary == null)
                {
                    primary = candidate;
                    if (battery.Surface)
                    {
                        budget = Math.Min(budget, ResoluteEngagementPolicy.Budget(battery.Key, true, battery.Ammo,
                            battery.InitialAmmo, primary.Emergency, primary.Groups));
                        budget = Math.Min(budget, Math.Max(0, primary.Groups * ResoluteEngagementPolicy.SurfaceWaveSize -
                            assessment.GroupActiveCount(primary.Target)));
                    }
                }
                eligible.Add(candidate);
            }
            battery.DetailedCursor = eligible.Count > 0 || candidates.Count == 0 ? 0 : (detailedStart + visited) % candidates.Count;
            candidates = eligible;
            int[] grants = ResoluteEngagementPolicy.Allocate(candidates.Select(c => c.Demand).ToArray(), budget);
            // An emergency may coexist with a routine shot while there is
            // expendable stock, but only active threats may consume reserve.
            if (battery.Surface)
            {
                int routine = Math.Max(0, battery.Ammo - ResoluteEngagementPolicy.Reserve(battery.Key, battery.InitialAmmo));
                for (int i = 0; i < candidates.Count; i++)
                    if (!candidates[i].Emergency) { grants[i] = Math.Min(grants[i], routine); routine -= grants[i]; }
            }
            int total = grants.Sum();
            Unit[] targetUnits = candidates.Select(c => c.Target).ToArray();
            if (total > 0 && battery.Key == "rsl_ashm" && !ResoluteStrikeOrders.CommitStrike(ship, targetUnits, grants, Time.timeSinceLevelLoad))
            { Array.Clear(grants, 0, grants.Length); total = 0; held = "strike-order-not-ready"; }
            pending.Clear();
            var admitted = ResoluteDiagnostics.Enabled ? new List<object>() : null;
            // Native PlanSalvo starts at the last index; retain that contract.
            for (int i = candidates.Count - 1; i >= 0; i--)
                if (grants[i] > 0)
                {
                    TargetDemand.SetValue(candidates[i].Native, (float)grants[i]);
                    pending.Add(candidates[i].Native);
                    if (admitted != null) admitted.Add(new { target = candidates[i].Target.persistentID.Id, shots = grants[i],
                        estimatedImpactSeconds = ResoluteEngagementPolicy.Finite(candidates[i].Urgency) ? candidates[i].Urgency : -1f });
                }
            if (total > 0)
            {
                PlansAdmitted++; ShotsAdmitted += total;
                if (battery.Key == "rsl_mrsam")
                    battery.QueueExpectedUntil = Time.timeSinceLevelLoad + Mathf.Min(3f,
                        total * ((float)NaturalWeapons.Get(controller, "planningTimePerFire") +
                        (float)NaturalWeapons.Get(controller, "salvoInterval")));
                if (battery.Surface)
                {
                    assessment.Planned(candidates.Where((c, i) => grants[i] > 0).Select(c => c.Target).ToArray());
                    nextSurfaceReview = Time.timeSinceLevelLoad + ResoluteEngagementPolicy.SurfaceReassessmentSeconds;
                }
            }
            if (ResoluteDiagnostics.Enabled)
            {
                if (history.Count >= 32) history.Dequeue();
                history.Enqueue(new { time = Time.timeSinceLevelLoad, weapon = battery.Key, ammo = battery.Ammo,
                    reserve = battery.Surface ? ResoluteEngagementPolicy.Reserve(battery.Key, battery.InitialAmmo) : 0,
                    reason = total > 0 ? (primary != null && primary.Emergency ? "active-threat-defense" : "balanced-native-plan") :
                        held ?? "covered-reserved-or-preferred-other-battery", shots = total, targets = admitted });
            }
            return total > 0;
        }

        internal bool InboundCoversDemand(FireControl controller, Unit target, bool nativeResult)
        {
            Battery battery = batteries.FirstOrDefault(b => b.Controller == controller);
            if (battery == null || ship == null || target == null || ship.NetworkHQ == null) return nativeResult;
            TrackingInfo track = ship.NetworkHQ.GetTrackingData(target.persistentID);
            if (battery.Surface)
                return track == null || assessment == null || !assessment.TrackUsable(target, track) ||
                    assessment.Demand(target, track, battery.Info.CalcAttacksNeeded(target), battery.Key == "rsl_ashm" ? assessment.GroupsFor(target) : 1) <= 0;
            return track == null || ResoluteEngagementPolicy.AdditionalDemand(battery.Info.CalcAttacksNeeded(target),
                track.missileAttacks, track.attackers) <= 0;
        }

        internal object Capture() => new { registered, PlansReviewed, PlansAdmitted, ShotsAdmitted,
            nextSurfaceReview, surfaceWaveOutstanding = registered && SurfaceOutstanding(null),
            batteries = batteries.Select(b => new { weapon = b.Key, ammo = b.Ammo, initialAmmo = b.InitialAmmo,
                reserve = b.Surface ? ResoluteEngagementPolicy.Reserve(b.Key, b.InitialAmmo) : 0, busy = Busy(b) }).ToArray(),
            strikeAssessment = assessment?.Capture(), decisions = history.ToArray() };
    }

    [HarmonyPatch(typeof(FireControl), "HQTargetAssessment")]
    internal static class ResoluteEngagementAssessmentPatch
    {
        private static void Prefix(FireControl __instance)
        {
            ResoluteEngagementDirector director;
            if (ResoluteEngagementDirector.TryGet(__instance, out director)) director.PrepareAssessment(__instance);
        }
    }

    [HarmonyPatch(typeof(FireControl), "PlanSalvo")]
    internal static class ResoluteEngagementPlanPatch
    {
        private static bool Prefix(FireControl __instance, ref UniTask __result)
        {
            ResoluteEngagementDirector director;
            if (!ResoluteEngagementDirector.TryGet(__instance, out director) || director.PrepareSalvo(__instance)) return true;
            __result = UniTask.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(FireControl), "TargetHasInboundMissiles")]
    internal static class ResoluteEngagementCoveragePatch
    {
        private static void Postfix(FireControl __instance, Unit target, ref bool __result)
        {
            ResoluteEngagementDirector director;
            if (ResoluteEngagementDirector.TryGet(__instance, out director))
                __result = director.InboundCoversDemand(__instance, target, __result);
        }
    }
}
