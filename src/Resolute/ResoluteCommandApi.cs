using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace Resolute
{
    public enum ResoluteEngagementMode { WeaponsFree, WeaponsTight, WeaponsHold }

    public sealed class ResoluteWeaponCommandInfo
    {
        public string Key, Name, Readiness;
        public int Ammo;
        public float MinRange, MaxRange;
        public bool IsGun, PointDefense, IsBeam, Continuous;
    }

    // Operating API only. Selection, input, map drawing and HUD belong to the
    // optional addon. Automatic startup discipline also uses this scoped state.
    public static class ResoluteCommandApi
    {
        public const int ApiVersion = 2;
        public static bool CanCommand(Unit unit, out string reason) => ResoluteNavigationCommands.CanCommand(unit, out reason);
        public static bool Begin(Ship ship, out string reason)
        {
            if (!CanCommand(ship, out reason)) return false;
            ResoluteCommandState.Ensure(ship);
            return ResoluteCommandSession.Begin(ship, out reason);
        }
        public static void End(Ship ship) { ResoluteCommandSession.End(ship); } // Issued orders survive leaving the interface.
        public static bool TryGetMissilePositionOrder(Ship ship, Missile missile, out GlobalPosition position)
        {
            position = default(GlobalPosition);
            if (!CanCommand(ship, out _) || missile == null || missile.disabled || missile.owner != ship) return false;
            if (missile.definition?.jsonKey == NaturalLanceFlight.Key) return ResoluteLancePosition.TryGetPoint(missile, out position);
            if (missile.definition?.jsonKey != "rsl_cruise") return false;
            var seeker = missile.GetComponent<OpticalSeekerCruiseMissile>();
            if (!ResoluteSpearPosition.TryGet(seeker, out var order)) return false;
            position = order.Point;
            return true;
        }
        public static ResoluteWeaponCommandInfo[] GetWeapons(Ship ship)
        {
            if (ship == null || !Plugin.IsResolute(ship.definition)) return new ResoluteWeaponCommandInfo[0];
            return ship.weaponStations.Where(s => s.WeaponInfo != null && s.Weapons.Any(SupportedWeapon))
                .GroupBy(s => s.WeaponInfo).Select(g => new ResoluteWeaponCommandInfo {
                    Key = WeaponKey(g.Key), Name = g.Key.weaponName,
                    Ammo = g.Sum(s => Math.Max(0, s.Ammo)), IsGun = g.Any(s => s.Weapons.Any(w => w is Gun)),
                    IsBeam = g.Any(s => s.Weapons.Any(w => w is Laser)),
                    Continuous = g.Any(s => s.Weapons.Any(w => w is Gun || w is Laser)),
                    PointDefense = g.Any(s => IsPointDefense(s)),
                    MinRange = g.Key.targetRequirements.minRange, MaxRange = g.Key.targetRequirements.maxRange,
                    Readiness = g.All(s => s.Reloading) ? "Reloading" : g.All(s => s.Ammo <= 0) && !g.Any(s => s.Weapons.Any(w => w is Laser)) ? "Empty" : "Ready"
                }).ToArray();
        }
        public static bool CanAttackTarget(Ship ship, string key, Unit target, out string reason)
        {
            if (!CanCommand(ship, out reason)) return false;
            if (target == null || target == ship || target.disabled || target.definition == null || target.persistentID.NotValid)
            { reason = "Choose another live unit."; return false; }
            WeaponStation fitted = ship.weaponStations.FirstOrDefault(s => WeaponKey(s.WeaponInfo) == key && s.Weapons.Any(SupportedWeapon));
            if (fitted == null) { reason = "That weapon is not fitted."; return false; }
            string registered = NaturalMissileTargeting.Key(fitted.WeaponInfo);
            if (registered != null && !NaturalMissileTargeting.ManualRoleAllows(registered, target))
            {
                reason = key == "rsl_ashm" ? "Pike can only be ordered against surface ships." :
                    key == NaturalLanceFlight.Key ? "Lance can engage ships and stationary ground targets." :
                    fitted.WeaponInfo.weaponName + " cannot engage that target type.";
                return false;
            }
            // Friendly targets are permitted. Unseen hostile units are not
            // revealed by this hook; the native map/track supplies the contact.
            GlobalPosition known;
            if (ship.NetworkHQ == null || target.NetworkHQ != ship.NetworkHQ && !ship.NetworkHQ.TryGetKnownPosition(target, out known))
            { reason = "No known position for that contact."; return false; }
            TrackingInfo track = ship.NetworkHQ.GetTrackingData(target.persistentID);
            reason = target.NetworkHQ != ship.NetworkHQ && track != null && !track.Observed()
                ? "Stale track: acquisition and interception are uncertain." : null;
            return true;
        }
        public static bool Attack(Ship ship, string key, Unit target, int count, out string reason, bool append = false)
        {
            if (!CanAttackTarget(ship, key, target, out reason)) return false;
            string warning = reason;
            WeaponStation[] stations = ship.weaponStations.Where(s => WeaponKey(s.WeaponInfo) == key && s.Weapons.Any(SupportedWeapon)).ToArray();
            if (stations.Length == 0) { reason = "That weapon is not fitted."; return false; }
            bool continuous = stations.Any(s => s.Weapons.Any(w => w is Gun || w is Laser));
            if (continuous) count = 1; // Counts are a missile-only command concept.
            else if (!ResoluteCommandRules.ValidQuantity(count)) { reason = "Choose 1, 2, 4, 8, 16 or 20."; return false; }
            bool beam = stations.Any(s => s.Weapons.Any(w => w is Laser));
            if (!beam && stations.Sum(s => Math.Max(0, s.Ammo)) < count)
            { reason = "Not enough loaded ammunition for that quantity."; return false; }
            bool accepted = ResoluteCommandState.Ensure(ship).AddOrder(key, target, stations, count, out reason, null, append);
            if (accepted && warning != null) reason += " · " + warning;
            return accepted;
        }
        public static bool CanAttackPosition(Ship ship, string key, GlobalPosition position, out GlobalPosition resolved, out string reason)
        {
            resolved = position;
            if (!CanCommand(ship, out reason)) return false;
            if (key != "rsl_ashm" && key != "rsl_cruise" && key != NaturalLanceFlight.Key) { reason = "This weapon needs a target. Right-click a compatible contact."; return false; }
            if (!ResoluteNavigationCommands.Finite(position.x) || !ResoluteNavigationCommands.Finite(position.y) || !ResoluteNavigationCommands.Finite(position.z))
            { reason = "The selected position is invalid."; return false; }
            if ((key == "rsl_cruise" || key == NaturalLanceFlight.Key) && !ResoluteSpearPosition.TryResolveLand(position, out resolved))
            { reason = (key == NaturalLanceFlight.Key ? "Lance" : "Spear") + " needs a land position. Select a point on land."; return false; }
            reason = null;
            return true;
        }
        public static bool AttackPosition(Ship ship, string key, GlobalPosition position, int count, out string reason, bool append = false)
        {
            if (!CanAttackPosition(ship, key, position, out position, out reason)) return false;
            if (!ResoluteCommandRules.ValidQuantity(count)) { reason = "Choose 1, 2, 4, 8, 16 or 20."; return false; }
            WeaponStation[] stations = ship.weaponStations.Where(s => WeaponKey(s.WeaponInfo) == key && s.Weapons.Any(w => w is ResoluteVlsLauncher)).ToArray();
            if (stations.Length == 0) { reason = "No matching VLS launcher is fitted."; return false; }
            if (stations.Sum(s => Math.Max(0, s.Ammo)) < count) { reason = "Not enough loaded ammunition for that quantity."; return false; }
            return ResoluteCommandState.Ensure(ship).AddOrder(key, null, stations, count, out reason, position, append);
        }
        public static bool SetROE(Ship ship, ResoluteEngagementMode mode, out string reason)
        {
            if (!CanCommand(ship, out reason)) return false;
            if (!Enum.IsDefined(typeof(ResoluteEngagementMode), mode)) { reason = "Unknown engagement mode."; return false; }
            ResoluteCommandState state = ResoluteCommandState.Ensure(ship);
            state.StartupModePending = false;
            state.Mode = mode; state.FirePaused = false; state.InvalidateAutomatic(null);
            reason = "Automatic engagement mode updated.";
            return true;
        }
        public static ResoluteEngagementMode GetROE(Ship ship)
        {
            ResoluteCommandState state = ResoluteCommandState.Find(ship);
            if (state != null) { state.UpdateStartupMode(); return state.Mode; }
            return ship != null && Plugin.IsResolute(ship.definition) ? ResoluteStartupEngagement.DefaultMode : ResoluteEngagementMode.WeaponsFree;
        }
        public static bool CeaseFire(Ship ship, out string reason)
        {
            if (!CanCommand(ship, out reason)) return false;
            ResoluteCommandState.Ensure(ship).Cease();
            reason = "Cease fire. Select an engagement mode to resume automatic fire.";
            return true;
        }
        public static string GetStatus(Ship ship) => ResoluteCommandState.Find(ship)?.Status ?? "Automatic engagement";
        internal static string WeaponKey(WeaponInfo info) => info == null ? null : NaturalMissileTargeting.Key(info) ?? "native:" + info.name;
        internal static bool SupportedWeapon(Weapon weapon) => weapon is MissileLauncher || weapon is Gun || weapon is Laser;
        internal static bool IsPointDefense(WeaponStation station) => station != null && (NaturalMissileTargeting.Key(station.WeaponInfo) == "rsl_pd" ||
            station.Weapons.Any(w => w is Laser || w is Gun && w.GetComponentInParent<ResoluteCiwsAim>() != null));

        internal static bool AllowAutomatic(Unit owner, WeaponStation station, Unit target)
        {
            if (ResoluteCommandState.ClearingTurret != null && ResoluteCommandState.ClearingTurret.GetAttachedUnit() == owner) return false;
            Ship ship = owner as Ship;
            ResoluteCommandState state = ship != null && Plugin.IsResolute(ship.definition)
                ? ResoluteCommandState.Ensure(ship) : null;
            return state == null || state.AllowAutomatic(station, target);
        }
        internal static bool IsManualFire(Unit owner, WeaponStation station, Unit target) => ResoluteCommandState.IsManualFire(owner, station, target);
    }

    internal static class ResoluteCommandRules
    {
        internal static bool ValidQuantity(int value) => value == 1 || value == 2 || value == 4 || value == 8 || value == 16 || value == 20;
        internal static bool Allows(ResoluteEngagementMode mode, bool paused, bool attackingWeapon, bool attackingUnit, bool pointDefense)
        {
            if (paused) return false;
            if (mode == ResoluteEngagementMode.WeaponsHold) return attackingWeapon && pointDefense;
            if (mode == ResoluteEngagementMode.WeaponsTight) return attackingWeapon || attackingUnit;
            return true;
        }
    }

    internal sealed class ResoluteCommandState : MonoBehaviour
    {
        internal sealed class Order
        {
            internal int Id, Remaining, Requested;
            internal string Key;
            internal Unit Target;
            internal GlobalPosition? Position;
            internal Player Issuer;
            internal FactionHQ IssuingFaction;
            internal WeaponStation[] Stations;
            internal Weapon Selected;
            internal WeaponStation Station;
            internal Turret Turret;
            internal float Created, NextShot, NextMountAssessment, BlockedSince = -1f;
            internal bool Continuous;
        }
        private static readonly ConditionalWeakTable<Ship, ResoluteCommandState> States = new ConditionalWeakTable<Ship, ResoluteCommandState>();
        private static readonly FieldInfo GunTrigger = AccessTools.Field(typeof(Gun), "ticksSinceTriggerPull");
        private static readonly FieldInfo GunQueue = AccessTools.Field(typeof(Gun), "queuedBullets");
        private static readonly FieldInfo LaserTrigger = AccessTools.Field(typeof(Laser), "fireCommanded");
        private static readonly FieldInfo LaserDirection = AccessTools.Field(typeof(Laser), "directionTransform");
        private static readonly MethodInfo ChooseTarget = AccessTools.Method(typeof(Turret), "ChooseTarget");
        private static readonly FieldInfo LastFired = AccessTools.Field(typeof(Weapon), "lastFired");
        private static readonly FieldInfo LaunchInterval = AccessTools.Field(typeof(MissileLauncher), "fireInterval");
        private static readonly FieldInfo NativePlanning = AccessTools.Field(typeof(FireControl), "planningSalvo");
        private static readonly FieldInfo ControllerStations = AccessTools.Field(typeof(FireControl), "subscribedWeaponStations");
        private readonly List<Order> orders = new List<Order>(16);
        private readonly Dictionary<uint, float> attackers = new Dictionary<uint, float>();
        private readonly List<uint> expired = new List<uint>();
        private readonly List<FireControl> draining = new List<FireControl>();
        private Ship ship;
        private float nextThreatSweep;
        private int threatCursor, nextOrderId;
        [ThreadStatic] private static Order firing;
        [ThreadStatic] private static Ship firingShip;
        [ThreadStatic] internal static Turret ClearingTurret;
        internal ResoluteEngagementMode Mode;
        internal bool StartupModePending;
        internal bool FirePaused;
        private string lastStatus = "Automatic engagement";
        internal string Status => (FirePaused ? "Automatic fire paused. " : "") +
            (orders.Count > 0 ? (orders[0].Continuous ? "Continuous" : orders[0].Remaining + "/" + orders[0].Requested + " remaining") +
                " · " + orders.Count + " order(s) · " + lastStatus : lastStatus);
        internal static ResoluteCommandState Find(Ship ship)
        { ResoluteCommandState value; return ship != null && States.TryGetValue(ship, out value) && value != null ? value : null; }
        internal static ResoluteCommandState Ensure(Ship ship)
        {
            ResoluteCommandState state = Find(ship);
            if (state != null) return state;
            state = ship.gameObject.AddComponent<ResoluteCommandState>(); state.ship = ship;
            state.Mode = ResoluteStartupEngagement.DefaultMode;
            state.StartupModePending = state.Mode == ResoluteEngagementMode.WeaponsTight;
            States.Add(ship, state); ship.onRegisterMissile += state.RegisterMissile;
            return state;
        }
        internal static bool IsManualFire(Unit owner, WeaponStation station, Unit target) => firing != null &&
            firingShip == owner && firing.Station == station && firing.Target == target && firing.Remaining > 0;
        internal static void ReportAttack(Unit attacker, Unit victim)
        {
            ResoluteSupplyDefense.ReportAttack(attacker, victim);
            ResoluteCommandState state = Find(victim as Ship);
            if (state == null || attacker == null || attacker.NetworkHQ == null || attacker.NetworkHQ == state.ship.NetworkHQ) return;
            TrackingInfo track = state.ship.NetworkHQ.GetTrackingData(attacker.persistentID);
            if (track == null || !track.Observed()) return;
            if (state.attackers.Count < 128 || state.attackers.ContainsKey(attacker.persistentID.Id))
                state.attackers[attacker.persistentID.Id] = Time.timeSinceLevelLoad + 30f;
        }
        private bool AttackingWeapon(Unit target)
        {
            Missile missile = target as Missile;
            if (missile == null || missile.disabled || ship.NetworkHQ == null) return false;
            TrackingInfo track = ship.NetworkHQ.GetTrackingData(missile.persistentID);
            if (track == null || !track.Observed()) return false;
            // The same detected launch-area threat used by native defenses
            // must also satisfy Resolute's Tight/Hold self-defense gate.
            // An unassigned Pike can still retain another ship's launch ID.
            bool incoming = missile.targetID == ship.persistentID ||
                missile.definition?.jsonKey == "rsl_ashm" &&
                NaturalPikeObservedThreat.IsTargetOrObservedIncoming(missile, ship);
            if (!incoming && missile.targetID.NotValid && missile.rb != null)
            {
                Vector3 offset = ship.GlobalPosition() - track.GetPosition();
                Vector3 relative = missile.rb.velocity - (ship.rb != null ? ship.rb.velocity : Vector3.zero);
                float time = relative.sqrMagnitude > 1f ? Vector3.Dot(offset, relative) / relative.sqrMagnitude : -1f;
                incoming = time >= 0f && time <= 20f && (offset - relative * time).sqrMagnitude < (ship.maxRadius + 20f) * (ship.maxRadius + 20f);
            }
            if (incoming) ReportAttack(missile.owner, ship);
            return incoming || ResoluteSupplyDefense.IsIncoming(missile, ship);
        }
        internal bool AllowAutomatic(WeaponStation station, Unit target)
        {
            UpdateStartupMode();
            if (station == null || target == null || target.disabled || target.NetworkHQ == null || target.NetworkHQ == ship.NetworkHQ) return false;
            if (FirePaused) return false;
            if (orders.Count == 0 && draining.Count == 0 && Mode == ResoluteEngagementMode.WeaponsFree) return true;
            string key = ResoluteCommandApi.WeaponKey(station.WeaponInfo);
            if (orders.Count > 0 && orders.Any(o => o.Key == key) || draining.Count > 0 && draining.Any(c => c != null &&
                ((List<WeaponStation>)ControllerStations.GetValue(c)).Contains(station))) return false;
            if (Mode == ResoluteEngagementMode.WeaponsFree) return true;
            bool incoming = AttackingWeapon(target);
            float until;
            bool attacker = attackers.TryGetValue(target.persistentID.Id, out until) && Time.timeSinceLevelLoad < until ||
                ResoluteSupplyDefense.IsAttacker(ship, target);
            return ResoluteCommandRules.Allows(Mode, false, incoming, attacker, ResoluteCommandApi.IsPointDefense(station));
        }
        internal bool AddOrder(string key, Unit target, WeaponStation[] stations, int count, out string reason,
            GlobalPosition? position = null, bool append = false)
        {
            bool continuous = stations.Any(s => s.Weapons.Any(w => w is Gun || w is Laser));
            if (orders.Count - (append ? 0 : orders.Count(o => o.Key == key)) >= 16)
            { reason = "The manual order queue is full."; return false; }
            int reserved = append && !continuous ? orders.Where(o => o.Key == key).Sum(o => o.Remaining) : 0;
            if (!continuous && stations.Sum(s => Math.Max(0, s.Ammo)) - reserved < count)
            { reason = "Those rounds are already assigned to another manual order."; return false; }
            Player player;
            if (!GameManager.GetLocalPlayer<Player>(out player)) { reason = "No commanding player."; return false; }
            int id = ++nextOrderId;
            InvalidateAutomatic(key);
            if (key == "rsl_ashm")
            {
                ResoluteStrikeOrders.CancelAutomaticPending(ship);
                int[] replaced = append ? null : orders.Where(o => o.Key == key).Select(o => o.Id).ToArray();
                bool prepared = position.HasValue ? ResoluteManualPikeOrders.PreparePosition(ship, position.Value, count, id, replaced)
                    : ResoluteManualPikeOrders.Prepare(ship, target, count, id, replaced);
                if (!prepared)
                { reason = "Pike group order capacity is currently full."; return false; }
            }
            // Replacement cancels unlaunched work only. Existing missiles keep
            // their own objective and seeker; continuous weapons release the
            // previous trigger/mount before acquiring the replacement target.
            if (!append)
                for (int i = orders.Count - 1; i >= 0; i--)
                    if (orders[i].Key == key) { Release(orders[i]); orders.RemoveAt(i); }
            orders.Add(new Order { Id = id, Key = key, Target = target, Stations = stations, Remaining = count, Requested = count,
                Created = Time.timeSinceLevelLoad, Issuer = player, IssuingFaction = player.HQ, Position = position, Continuous = continuous });
            reason = lastStatus = (append ? "Queued " : "Ordered ") + (continuous ? "continuous " : count + " × ") + stations[0].WeaponInfo.weaponName +
                (position.HasValue ? key == "rsl_ashm" ? " to search the selected area" : " to strike the selected land position" : " against " + target.definition.unitName);
            return true;
        }
        internal void InvalidateAutomatic(string key)
        {
            foreach (FireControl controller in ship.GetComponentsInChildren<FireControl>(true))
            {
                var stations = (List<WeaponStation>)ControllerStations.GetValue(controller);
                if ((key == null || stations.Any(s => ResoluteCommandApi.WeaponKey(s.WeaponInfo) == key)) &&
                    (bool)NativePlanning.GetValue(controller) && !draining.Contains(controller)) draining.Add(controller);
            }
            // Native QueuedAttack.StillValid sees the block, calls its own
            // Cancel, and releases its own reservations. Never mutate a list
            // while the native async launch loop is enumerating it.
        }
        internal void Cease()
        {
            StartupModePending = false;
            FirePaused = true; InvalidateAutomatic(null);
            foreach (Order order in orders) Release(order);
            orders.Clear(); ResoluteManualPikeOrders.CancelPending(ship, 0); ResoluteStrikeOrders.CancelPending(ship, 0);
            foreach (WeaponStation station in ship.weaponStations)
                foreach (Weapon weapon in station.Weapons) StopTrigger(weapon);
            lastStatus = "Cease fire · airborne weapons continue their own flight";
        }
        private void RegisterMissile(Missile missile)
        {
            if (firing != null && firingShip == ship && missile != null)
            {
                if (firing.Key == "rsl_cruise" && firing.Position.HasValue)
                    ResoluteSpearPosition.Register(missile, firing.Position.Value);
                else if (firing.Key == NaturalLanceFlight.Key && firing.Position.HasValue)
                    ResoluteLancePosition.Register(missile, firing.Position.Value);
                else if (firing.Key == "rsl_ashm")
                {
                    if (firing.Position.HasValue) ResoluteManualPikeOrders.RegisterLaunchedPosition(ship, missile, firing.Position.Value, firing.Id);
                    else ResoluteManualPikeOrders.RegisterLaunched(ship, missile, firing.Target, firing.Id);
                }
            }
        }
        private void Update()
        {
            if (ship == null || ship.disabled || !ship.IsServer || !ship.LocalSim) return;
            UpdateStartupMode();
            draining.RemoveAll(c => c == null || !(bool)NativePlanning.GetValue(c));
            float now = Time.timeSinceLevelLoad;
            if (now >= nextThreatSweep && Mode != ResoluteEngagementMode.WeaponsFree)
            {
                nextThreatSweep = now + .5f;
                int count = UnitRegistry.allUnits.Count;
                for (int i = 0; i < Math.Min(count, 64); i++)
                {
                    if (threatCursor >= count) threatCursor = 0;
                    Unit unit = UnitRegistry.allUnits[threatCursor++];
                    if (unit is Missile && unit.NetworkHQ != ship.NetworkHQ) AttackingWeapon(unit);
                }
                expired.Clear(); foreach (var entry in attackers) if (now >= entry.Value) expired.Add(entry.Key);
                foreach (uint id in expired) attackers.Remove(id);
            }
        }
        internal void UpdateStartupMode()
        {
            if (!StartupModePending || ResoluteStartupEngagement.DefaultMode != ResoluteEngagementMode.WeaponsFree) return;
            StartupModePending = false;
            Mode = ResoluteEngagementMode.WeaponsFree;
        }
        private void FixedUpdate()
        {
            if (ship == null || ship.disabled || !ship.IsServer || !ship.LocalSim) { CancelAll(); return; }
            for (int i = 0; i < orders.Count; i++)
            {
                Order order = orders[i];
                if (order.Remaining <= 0 || !order.Position.HasValue && (order.Target == null || order.Target.disabled) ||
                    order.Issuer == null || order.Issuer.HQ != order.IssuingFaction ||
                    !ResoluteNavigationCommands.HasPermission(ship, order.Issuer) || !order.Continuous && Time.timeSinceLevelLoad - order.Created > 180f)
                { Finish(i--, order.Remaining <= 0 ? "Ordered salvo completed" : "Order ended: target, authority or timeout changed"); continue; }
                if (orders.Take(i).Any(o => o.Key == order.Key)) continue;
                Execute(order);
            }
        }
        private void Execute(Order order)
        {
            float now = Time.timeSinceLevelLoad;
            if (now < order.NextShot) return;
            if (order.Selected != null && now >= order.NextMountAssessment)
            {
                order.NextMountAssessment = now + .2f;
                bool sector = ResoluteCommandMounts.CanServe(order.Selected, order.Target);
                bool blocked = sector && order.Turret != null && order.Turret.IsOnTarget() && !ClearMuzzle(order.Selected, order.Target);
                if (blocked && order.BlockedSince < 0f) order.BlockedSince = now;
                if (!blocked) order.BlockedSince = -1f;
                if (!sector || blocked && now - order.BlockedSince >= .6f)
                {
                    ReleaseMount(order);
                    RotateStations(order);
                    order.BlockedSince = -1f;
                }
            }
            if (order.Selected == null || order.Selected.ammo <= 0 && !(order.Selected is Laser) || !Usable(order.Selected))
            {
                ReleaseMount(order);
                foreach (WeaponStation station in order.Stations)
                {
                    if (station.Reloading) continue;
                    Weapon candidate = station.Weapons.FirstOrDefault(w => ResoluteCommandApi.SupportedWeapon(w) && Usable(w) &&
                        (w.ammo > 0 || w is Laser) && MountAvailable(w, order) && ResoluteCommandMounts.CanServe(w, order.Target));
                    if (candidate == null) continue;
                    order.Selected = candidate; order.Station = station;
                    order.Turret = candidate.GetComponentInParent<Turret>();
                    if (order.Turret != null)
                    {
                        Turret previousClearing = ClearingTurret;
                        ClearingTurret = order.Turret;
                        try { if (order.Turret.GetTarget() != null) ChooseTarget.Invoke(order.Turret, new object[] { true }); }
                        finally { ClearingTurret = previousClearing; }
                        if (order.Position.HasValue)
                        {
                            // Native targetless manual turret mode assumes an
                            // aircraft cockpit. Fixed VLS needs no aim target.
                            previousClearing = ClearingTurret; ClearingTurret = order.Turret;
                            try { order.Turret.SetManual(false); order.Turret.SetTarget(PersistentID.None, station.Number); }
                            finally { ClearingTurret = previousClearing; }
                        }
                        else
                        {
                            order.Turret.SetTarget(order.Target.persistentID, station.Number);
                            order.Turret.SetManual(true);
                        }
                    }
                    candidate.SetTarget(order.Target);
                    order.NextShot = now + Time.fixedDeltaTime;
                    order.NextMountAssessment = now + .2f;
                    order.BlockedSince = -1f;
                    break;
                }
                if (order.Selected == null) { lastStatus = "Waiting for a usable mount with a clear firing sector"; order.NextShot = now + .2f; }
                return; // Let the native turret aim before trusting IsOnTarget.
            }
            if (order.Selected == null) { lastStatus = "Waiting for a usable mount"; return; }
            // Fixed VLS has no trainable mount. Its native firesWithoutAiming
            // turret never updates IsOnTarget; AimTurret returns a range test
            // instead. Explicit orders retain physical cell and Safety checks.
            bool fixedVls = order.Selected is ResoluteVlsLauncher;
            if (!fixedVls && order.Turret != null && !order.Turret.IsOnTarget()) { StopTrigger(order.Selected); lastStatus = "Mount turning toward ordered target"; return; }
            if (order.Selected.Safety) { StopTrigger(order.Selected); lastStatus = "Waiting for mount firing readiness"; return; }
            if (order.Selected is MissileLauncher && now - (float)LastFired.GetValue(order.Selected) < (float)LaunchInterval.GetValue(order.Selected)) return;
            // Range and intercept prediction are advisory for explicit orders.
            // Actual mount damage, traverse, reload, cooldown and obstruction
            // still belong to the native weapon and turret.
            if (!ClearMuzzle(order.Selected, order.Target)) { StopTrigger(order.Selected); lastStatus = "Mount obstructed"; return; }
            int before = order.Selected.ammo;
            Order previous = firing; Ship previousShip = firingShip;
            firing = order; firingShip = ship;
            try { order.Selected.Fire(ship, order.Target, ship.rb != null ? ship.rb.velocity : Vector3.zero, order.Station, order.Position ?? default(GlobalPosition)); }
            finally { firing = previous; firingShip = previousShip; }
            if (order.Selected is Gun) { lastStatus = "Engaging " + order.Target.definition.unitName + " continuously"; return; }
            if (order.Selected is Laser)
            {
                lastStatus = "Engaging " + order.Target.definition.unitName + " continuously";
            }
            else if (order.Selected.ammo < before)
            {
                order.Remaining -= before - order.Selected.ammo;
                order.NextShot = now + Mathf.Max(.1f, order.Station.WeaponInfo.fireInterval);
                // Cycle physical stations while their native local cooldowns
                // remain authoritative. Each native call launches one round.
                ReleaseMount(order);
                RotateStations(order);
                lastStatus = "Launching ordered salvo";
            }
        }
        private static void RotateStations(Order order)
        {
            WeaponStation first = order.Stations[0];
            Array.Copy(order.Stations, 1, order.Stations, 0, order.Stations.Length - 1); order.Stations[order.Stations.Length - 1] = first;
        }
        private bool MountAvailable(Weapon weapon, Order requesting)
        {
            Turret turret = weapon.GetComponentInParent<Turret>();
            return turret == null || !orders.Any(o => o != requesting && o.Turret == turret);
        }
        private static bool Usable(Weapon weapon)
        {
            if (weapon == null || !weapon.gameObject.activeInHierarchy || !weapon.IsAttached()) return false;
            UnitPart part = weapon.GetComponentInParent<UnitPart>();
            return part == null || !part.IsDetached() && part.hitPoints > 0f;
        }
        private static bool ClearMuzzle(Weapon weapon, Unit target)
        {
            if (weapon is ResoluteVlsLauncher) return true;
            if (weapon is MissileLauncher launcher) return ResoluteCommandMounts.ClearCell(launcher, target);
            Transform from = weapon.transform;
            if (weapon is Laser laser) from = (Transform)LaserDirection.GetValue(laser) ?? from;
            Gun gun = weapon as Gun;
            if (gun != null)
            {
                Transform[] muzzles = (Transform[])NaturalWeapons.Get(gun, "muzzles");
                if (muzzles.Length > 0 && muzzles[0] != null) from = muzzles[0];
            }
            RaycastHit hit;
            float distance = Mathf.Min(200f, (target.transform.position - from.position).magnitude);
            if (!Physics.Raycast(from.position, from.forward, out hit, distance, ~(int)PhysicsLayers.ExclusionZonesMask)) return true;
            UnitPart part = hit.collider.GetComponentInParent<UnitPart>();
            Unit unit = part != null ? part.parentUnit : hit.collider.GetComponentInParent<Unit>();
            return unit == target;
        }
        private static void StopTrigger(Weapon weapon)
        {
            if (weapon is Gun gun) { GunTrigger.SetValue(gun, 3); GunQueue.SetValue(gun, 0f); }
            if (weapon is Laser laser) LaserTrigger.SetValue(laser, false);
        }
        private static void ReleaseMount(Order order)
        {
            StopTrigger(order.Selected);
            if (order.Turret != null)
            {
                // Never leave a ship turret manual with no target: native
                // targetless manual mode assumes an aircraft cockpit.
                Turret previousClearing = ClearingTurret;
                ClearingTurret = order.Turret;
                try { order.Turret.SetManual(false); order.Turret.SetTarget(PersistentID.None, order.Station.Number); }
                finally { ClearingTurret = previousClearing; }
            }
            order.Selected = null; order.Station = null; order.Turret = null;
        }
        private void Release(Order order) { ReleaseMount(order); ResoluteManualPikeOrders.CancelPending(ship, order.Id); }
        private void Finish(int index, string text) { Release(orders[index]); orders.RemoveAt(index); lastStatus = text; }
        private void CancelAll() { foreach (Order order in orders) Release(order); orders.Clear(); }
        private void OnDestroy()
        {
            CancelAll();
            if (ship != null) { ship.onRegisterMissile -= RegisterMissile; States.Remove(ship); }
        }
        internal static float ClampGunBudget(float queued, Gun gun)
        {
            ResoluteCommandState state = Find(gun.attachedUnit as Ship);
            if (state == null) return queued;
            Order order = state.orders.FirstOrDefault(o => o.Selected == gun);
            if (order != null) return order.Continuous ? queued : Mathf.Min(queued, Math.Max(0, order.Remaining));
            return state.FirePaused ? 0f : queued;
        }
        internal static void GunFired(Gun gun, int previousAmmo)
        {
            ResoluteCommandState state = Find(gun.attachedUnit as Ship);
            if (state == null) return;
            Order order = state.orders.FirstOrDefault(o => o.Selected == gun);
            if (order == null || order.Continuous) return;
            order.Remaining -= Math.Max(0, previousAmmo - gun.ammo);
            if (order.Remaining <= 0) StopTrigger(gun);
        }
        internal static bool OwnsManualWeapon(Weapon weapon)
        {
            ResoluteCommandState state = Find(weapon.attachedUnit as Ship);
            return state != null && state.orders.Any(o => o.Selected == weapon && o.Remaining > 0);
        }
        internal static bool OwnsManualTurret(Turret turret)
        {
            ResoluteCommandState state = Find(turret != null ? turret.GetAttachedUnit() as Ship : null);
            return state != null && state.orders.Any(o => o.Turret == turret);
        }
        internal static bool AllowsTurretTarget(Turret turret, PersistentID target)
        {
            if (ClearingTurret == turret) return true;
            ResoluteCommandState state = Find(turret != null ? turret.GetAttachedUnit() as Ship : null);
            Order order = state?.orders.FirstOrDefault(o => o.Turret == turret);
            return order == null || order.Target != null && order.Target.persistentID == target;
        }
    }
}
