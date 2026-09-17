using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    public enum ResoluteSpeedPreset
    {
        AheadFlank, AheadFull, AheadStandard, AheadTwoThirds, AheadOneThird, AllStop, BackFull
    }

    public sealed class ResoluteSensorSnapshot
    {
        public int Id;
        public string Name;
        public bool Operational, Enabled;
        public float RangeMetres;
    }

    public sealed class ResoluteNavigationSnapshot
    {
        public float ActualSpeedKnots, OrderedSpeedKnots, MinimumSpeedKnots, MaximumSpeedKnots;
        public bool HasSpeedOrder, RoutePaused, RadarAvailable, RadarEnabled;
        public GlobalPosition[] Waypoints;
        public ResoluteSensorSnapshot[] Sensors;
        public string NavigationStatus;
    }

    // Operating hooks only. The optional command addon owns every input and UI.
    // Native ShipAI retains pathfinding, obstacle avoidance, steering and physics.
    public static class ResoluteNavigationCommands
    {
        public const int MaximumWaypoints = 64;
        public const float MetresPerSecondPerKnot = 1852f / 3600f;

        public static bool IsResolute(Unit unit) => unit is Ship && Plugin.IsResolute(unit.definition);

        internal static bool HasPermission(Ship ship, Player player)
        {
            if (ship == null || player == null || !IsResolute(ship)) return false;
            // Joining a faction restricts even a host/admin to that faction.
            return player.HQ != null ? player.HQ == ship.NetworkHQ : player.HasAuthority;
        }

        public static bool CanCommand(Unit unit, out string reason)
        {
            reason = null;
            Ship ship = unit as Ship;
            if (!IsResolute(ship)) { reason = "Only Resolute ships can be commanded."; return false; }
            if (!MissionManager.IsRunning || !ship.gameObject.activeInHierarchy || ship.disabled)
            { reason = "This ship is not available in a running mission."; return false; }
            Player player;
            if (!GameManager.GetLocalPlayer<Player>(out player) || player == null)
            { reason = "A local player is required."; return false; }
            if (!HasPermission(ship, player))
            { reason = player.HQ != null ? "You can command only your faction's Resolutes." : "Spectator command authority is required."; return false; }
            if (!ship.IsServer || !ship.LocalSim)
            { reason = "Ship commands currently require the local mission host."; return false; }
            return true;
        }

        private static bool State(Ship ship, out ResoluteNavigationOrderState state, out string reason)
        {
            state = null;
            if (!CanCommand(ship, out reason)) return false;
            if (ship.UnitCommand == null || ship.GetComponent<ShipAI>() == null || !ResoluteNavigationOrderState.NativeBindingsAvailable)
            { reason = "The native ship navigation controller is unavailable."; return false; }
            state = ship.GetComponent<ResoluteNavigationOrderState>();
            if (state == null)
            {
                state = ship.gameObject.AddComponent<ResoluteNavigationOrderState>();
                state.Initialize(ship);
            }
            Player player;
            GameManager.GetLocalPlayer<Player>(out player);
            state.SetIssuer(player);
            return true;
        }

        private static bool ValidWaypoint(GlobalPosition waypoint) =>
            Finite(waypoint.x) && Finite(waypoint.y) && Finite(waypoint.z);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool ReplaceWaypoint(Ship ship, GlobalPosition waypoint, out string reason)
        {
            if (!ValidWaypoint(waypoint)) { reason = "The waypoint coordinates are invalid."; return false; }
            ResoluteNavigationOrderState state;
            if (!State(ship, out state, out reason)) return false;
            state.Replace(waypoint);
            return true;
        }

        public static bool AppendWaypoint(Ship ship, GlobalPosition waypoint, out string reason)
        {
            if (!ValidWaypoint(waypoint)) { reason = "The waypoint coordinates are invalid."; return false; }
            ResoluteNavigationOrderState state;
            if (!State(ship, out state, out reason)) return false;
            if (state.WaypointCount >= MaximumWaypoints)
            { reason = "The route already contains 64 waypoints."; return false; }
            state.Append(waypoint);
            return true;
        }

        public static bool ClearWaypoints(Ship ship, out string reason)
        {
            ResoluteNavigationOrderState state;
            if (!State(ship, out state, out reason)) return false;
            state.ClearRoute();
            return true;
        }

        public static bool ResumeCourse(Ship ship, out string reason)
        {
            ResoluteNavigationOrderState state;
            if (!State(ship, out state, out reason)) return false;
            if (!state.Resume()) { reason = "There is no suspended route to resume."; return false; }
            return true;
        }

        internal static float MaximumSpeed(Ship ship)
        {
            float top = (ship.definition as ShipDefinition)?.shipInfo?.topSpeed ?? 0f;
            return Finite(top) && top > 0f ? top / 1.852f : 35f;
        }

        public static bool SetOrderedSpeedKnots(Ship ship, float knots, out string reason)
        {
            if (!Finite(knots)) { reason = "The ordered speed is invalid."; return false; }
            ResoluteNavigationOrderState state;
            if (!State(ship, out state, out reason)) return false;
            float maximum = MaximumSpeed(ship);
            state.OrderSpeed(Mathf.Clamp(knots, -maximum / 3f, maximum));
            return true;
        }

        public static bool SetSpeedPreset(Ship ship, ResoluteSpeedPreset preset, out string reason)
        {
            float fraction;
            switch (preset)
            {
                case ResoluteSpeedPreset.AheadFlank: fraction = 1f; break;
                case ResoluteSpeedPreset.AheadFull: fraction = .9f; break;
                case ResoluteSpeedPreset.AheadStandard: fraction = .75f; break;
                case ResoluteSpeedPreset.AheadTwoThirds: fraction = 2f / 3f; break;
                case ResoluteSpeedPreset.AheadOneThird: fraction = 1f / 3f; break;
                case ResoluteSpeedPreset.AllStop: fraction = 0f; break;
                case ResoluteSpeedPreset.BackFull: fraction = -1f / 3f; break;
                default: reason = "The speed preset is invalid."; return false;
            }
            if (!CanCommand(ship, out reason)) return false;
            return SetOrderedSpeedKnots(ship, MaximumSpeed(ship) * fraction, out reason);
        }

        private static bool SetRadars(Ship ship, int? sensorId, bool enabled, out string reason)
        {
            if (!CanCommand(ship, out reason)) return false;
            bool found = false;
            foreach (Radar radar in ship.GetComponentsInChildren<Radar>(true))
            {
                if (radar == null || radar.GetAttachedUnit() != ship || !radar.IsOperational() ||
                    (sensorId.HasValue && sensorId.Value != radar.GetInstanceID())) continue;
                // Same activation field as the native aircraft radar command.
                // Search loops, damage ownership, jamming and SARH checks stay native.
                radar.activated = enabled;
                found = true;
            }
            if (!found) reason = "No operational radar matches this command.";
            return found;
        }

        public static bool SetRadarEnabled(Ship ship, bool enabled, out string reason) => SetRadars(ship, null, enabled, out reason);
        public static bool SetRadarEnabled(Ship ship, int sensorId, bool enabled, out string reason) => SetRadars(ship, sensorId, enabled, out reason);

        public static ResoluteNavigationSnapshot GetSnapshot(Ship ship)
        {
            if (!IsResolute(ship)) return null;
            var state = ship.GetComponent<ResoluteNavigationOrderState>();
            var sensors = new List<ResoluteSensorSnapshot>();
            bool anyOperational = false, anyEnabled = false;
            foreach (Radar radar in ship.GetComponentsInChildren<Radar>(true))
            {
                if (radar == null || radar.GetAttachedUnit() != ship) continue;
                bool operational = radar.IsOperational();
                sensors.Add(new ResoluteSensorSnapshot { Id = radar.GetInstanceID(), Name = radar.name,
                    Operational = operational, Enabled = operational && radar.activated, RangeMetres = radar.GetRadarRange() });
                anyOperational |= operational;
                anyEnabled |= operational && radar.activated;
            }
            float actual = ship.rb != null ? Vector3.Dot(ship.rb.velocity, ship.transform.forward) / MetresPerSecondPerKnot : 0f;
            float maximum = MaximumSpeed(ship);
            return new ResoluteNavigationSnapshot { ActualSpeedKnots = actual,
                OrderedSpeedKnots = state != null && state.HasSpeedOrder ? state.OrderedSpeedKnots : actual,
                MinimumSpeedKnots = -maximum / 3f, MaximumSpeedKnots = maximum,
                HasSpeedOrder = state != null && state.HasSpeedOrder,
                RoutePaused = state != null && state.RoutePaused,
                RadarAvailable = anyOperational, RadarEnabled = anyEnabled, Sensors = sensors.ToArray(),
                Waypoints = state != null ? state.CopyWaypoints() : new GlobalPosition[0],
                NavigationStatus = state != null ? state.Status : "Autonomous navigation" };
        }
    }

    internal sealed class ResoluteNavigationOrderState : MonoBehaviour
    {
        private static readonly FieldInfo Commanded = AccessTools.Field(typeof(ShipAI), "commandedDestination");
        private static readonly FieldInfo LastSteered = AccessTools.Field(typeof(ShipAI), "lastSteeringUpdate");
        internal static bool NativeBindingsAvailable => Commanded != null && LastSteered != null;
        private readonly List<GlobalPosition> route = new List<GlobalPosition>();
        private readonly List<GlobalPosition> suspendedRoute = new List<GlobalPosition>();
        private Ship ship;
        private ShipAI ai;
        private Player issuer;
        private bool sendingNativeOrder, ownsRoute, ownsHeading;
        private Vector3 orderedHeading;
        private GlobalPosition headingDestination;
        private float nextAuthorityCheck;
        private float speedIntegral, lastGovernorUpdate;
        internal bool HasSpeedOrder { get; private set; }
        internal float OrderedSpeedKnots { get; private set; }
        internal bool RoutePaused { get; private set; }
        internal int WaypointCount => route.Count;
        internal bool OwnsNavigation => ownsRoute || ownsHeading;
        internal bool NeedsNativeSteer => !(ownsRoute && route.Count == 0);
        internal string Status => RoutePaused ? "Route paused" : ownsRoute && route.Count == 0 ? "Holding position" :
            HasSpeedOrder && Mathf.Abs(OrderedSpeedKnots) < .01f ? "All stop" : route.Count > 0 ? "Following ordered route" :
            ownsHeading ? "Following ordered course" : "Autonomous navigation";

        internal void Initialize(Ship owner)
        {
            ship = owner; ai = ship.GetComponent<ShipAI>();
            ship.UnitCommand.ProcessSetDestination += NativeDestination;
        }
        internal void SetIssuer(Player player) { issuer = player; }
        internal GlobalPosition[] CopyWaypoints() => route.ToArray();
        internal void OrderSpeed(float knots)
        {
            if (!HasSpeedOrder || Mathf.Abs(knots - OrderedSpeedKnots) > .01f) speedIntegral = 0f;
            OrderedSpeedKnots = knots; HasSpeedOrder = true;
            if (Mathf.Abs(knots) > .01f && route.Count == 0 && !ownsHeading)
            {
                // Telegraph orders work without a plotted route. Give native
                // pathfinding a rolling point on the current bow heading;
                // astern uses native reverse thrust while retaining that bow.
                ownsRoute = false; ownsHeading = true; RoutePaused = false;
                suspendedRoute.Clear();
                orderedHeading = ship.transform.forward; orderedHeading.y = 0f;
                orderedHeading.Normalize();
                ContinueHeading();
            }
        }
        private void ContinueHeading()
        {
            headingDestination = ship.GlobalPosition() + orderedHeading * 10000f;
            SendDestination(headingDestination);
        }

        internal void Replace(GlobalPosition waypoint)
        {
            route.Clear(); suspendedRoute.Clear(); route.Add(waypoint);
            RoutePaused = false; ownsRoute = true; ownsHeading = false; SendDestination(waypoint);
        }
        internal void Append(GlobalPosition waypoint)
        {
            suspendedRoute.Clear(); RoutePaused = false; ownsRoute = true; ownsHeading = false;
            route.Add(waypoint);
            if (route.Count == 1) SendDestination(waypoint);
        }
        internal void ClearRoute()
        {
            if (route.Count > 0) { suspendedRoute.Clear(); suspendedRoute.AddRange(route); }
            route.Clear(); RoutePaused = true; ownsRoute = true; ownsHeading = false; HoldNative();
        }
        internal bool Resume()
        {
            if (suspendedRoute.Count == 0) return false;
            route.Clear(); route.AddRange(suspendedRoute); suspendedRoute.Clear();
            RoutePaused = false; ownsRoute = true; ownsHeading = false; SendDestination(route[0]);
            return true;
        }
        private void SendDestination(GlobalPosition position)
        {
            sendingNativeOrder = true;
            try { ship.SetHoldPosition(false); ship.UnitCommand.SetDestination(position, playerCommand: true); }
            finally { sendingNativeOrder = false; }
        }
        private void HoldNative()
        {
            ship.SetHoldPosition(true);
            ai.state = ShipAI.ShipAIState.holding;
            Commanded.SetValue(ai, true);
            ShipInputs inputs = ship.GetInputs();
            inputs.steering = 0f;
            inputs.throttle = Mathf.Clamp(-Vector3.Dot(ship.rb.velocity, ship.transform.forward) * .1f, -1f, 1f);
        }
        private void NativeDestination(ref UnitCommand.Command command)
        {
            if (sendingNativeOrder) return;
            // A newer native player order supersedes our queued route too.
            route.Clear(); suspendedRoute.Clear(); RoutePaused = false; ownsHeading = false;
            ownsRoute = command.player != null && ResoluteNavigationCommands.HasPermission(ship, command.player);
            if (ownsRoute) { issuer = command.player; route.Add(command.position); }
        }

        internal bool BeforeSteer()
        {
            if (ship == null || ai == null || !ship.IsServer || !ship.LocalSim || ship.disabled) return false;
            float now = Time.timeSinceLevelLoad;
            if (now >= nextAuthorityCheck)
            {
                nextAuthorityCheck = now + .5f;
                if (!MissionManager.IsRunning || !ResoluteNavigationCommands.HasPermission(ship, issuer))
                {
                    if (ownsRoute || ownsHeading || HasSpeedOrder)
                    {
                        route.Clear(); suspendedRoute.Clear(); HasSpeedOrder = false; RoutePaused = false;
                        HoldNative(); ownsRoute = false; ownsHeading = false;
                    }
                    return false;
                }
            }
            if (ownsHeading)
            {
                Commanded.SetValue(ai, true);
                if (FastMath.Distance(ship.GlobalPosition(), headingDestination) < 2000f) ContinueHeading();
            }
            if (ownsRoute)
            {
                Commanded.SetValue(ai, true);
                // The same arrival radius as native ShipAI.Steer. Advancing
                // here avoids its two-minute hold between player route legs.
                if (route.Count > 0 && FastMath.Distance(ship.GlobalPosition(), route[0]) < ship.maxRadius + 100f)
                {
                    route.RemoveAt(0);
                    if (route.Count > 0) SendDestination(route[0]);
                    else HoldNative();
                }
                if (route.Count == 0) { HoldNative(); return false; }
            }
            return now - (float)LastSteered.GetValue(ai) >= .2f;
        }

        internal void AfterSteer(bool nativeUpdated)
        {
            if (!nativeUpdated || !HasSpeedOrder || ship == null || ship.disabled || !ship.IsServer || !ship.LocalSim) return;
            if (ownsRoute && route.Count == 0) { HoldNative(); return; }
            ShipInputs inputs = ship.GetInputs();
            float nativeThrottle = inputs.throttle;
            float forwardSpeed = Vector3.Dot(ship.rb.velocity, ship.transform.forward);
            float desired = OrderedSpeedKnots * ResoluteNavigationCommands.MetresPerSecondPerKnot;
            float maximum = ResoluteNavigationCommands.MaximumSpeed(ship) * ResoluteNavigationCommands.MetresPerSecondPerKnot;
            // A speed governor uses native propulsion inputs. Feed-forward
            // offsets drag, while feedback trims it; no velocity is assigned.
            float fraction = desired / Mathf.Max(1f, maximum);
            float error = desired - forwardSpeed;
            float upper = desired > 0f ? Mathf.Clamp(nativeThrottle, -1f, 1f) : 1f;
            float raw = fraction * Mathf.Abs(fraction) + error * .18f + speedIntegral;
            float elapsed = Mathf.Clamp(Time.timeSinceLevelLoad - lastGovernorUpdate, 0f, .25f);
            lastGovernorUpdate = Time.timeSinceLevelLoad;
            // Integral trim removes steady speed error without winding up while
            // native avoidance, a turn, or maximum propulsion limits the order.
            if ((raw > -1f && raw < upper) || (raw >= upper && error < 0f) || (raw <= -1f && error > 0f))
                speedIntegral = Mathf.Clamp(speedIntegral + error * elapsed * .04f, -1f, 1f);
            float throttle = Mathf.Clamp(raw, -1f, upper);
            inputs.throttle = throttle;
        }
        private void OnDestroy()
        {
            if (ship != null && ship.UnitCommand != null) ship.UnitCommand.ProcessSetDestination -= NativeDestination;
        }
    }

    [HarmonyPatch(typeof(ShipAI), "Steer")]
    internal static class ResoluteOrderedNavigationPatch
    {
        private struct SteerCall
        {
            internal ResoluteNavigationOrderState State;
            internal bool NativeUpdated;
        }
        private static bool Prefix(ShipAI __instance, out SteerCall __state)
        {
            var state = __instance.GetComponent<ResoluteNavigationOrderState>();
            __state = new SteerCall { State = state, NativeUpdated = state != null && state.BeforeSteer() };
            // Native Steer starts a delayed hold task on every arrival check.
            // Our explicit final/paused hold already supplies native brake input;
            // do not repeatedly enqueue native two-minute arrival tasks.
            return state == null || state.NeedsNativeSteer;
        }
        private static void Postfix(SteerCall __state) => __state.State?.AfterSteer(__state.NativeUpdated);
    }

    [HarmonyPatch(typeof(ShipAI), "ChooseTarget")]
    internal static class ResoluteOrderedDestinationPatch
    {
        private static bool Prefix(ShipAI __instance)
        {
            var state = __instance.GetComponent<ResoluteNavigationOrderState>();
            // An old native delayed hold completion can clear commandedDestination
            // before Update calls ChooseTarget. It must not replace a newer order.
            return state == null || !state.OwnsNavigation;
        }
    }
}
