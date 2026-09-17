using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace Resolute
{
    public sealed class ResoluteReplenishmentSnapshot
    {
        public string Status, CarrierName, PayloadName, SelectedOptionId;
        public bool CanRequest;
        public float Price, AircraftPrice, PayloadPrice, DispatchCooldown, ReservedPrice;
        public int Outstanding, QueueLimit = 5;
        public ResoluteReplenishmentOption[] Options = Array.Empty<ResoluteReplenishmentOption>();
        public ResoluteReplenishmentOrder[] Queue = Array.Empty<ResoluteReplenishmentOrder>();
    }
    public sealed class ResoluteReplenishmentOption
    {
        public string Id, CarrierName, PayloadName, Status;
        public int Stock, NavalPallets;
        public float CapacityKg, Price, AircraftPrice, PayloadPrice;
        public bool CanRequest, PurchasesAircraft;
    }
    public sealed class ResoluteReplenishmentOrder
    {
        public int Id;
        public string CarrierName, Status;
        public float Price;
        public bool CanCancel;
    }

    // Native supply traffic owns flight, cargo delivery and ammunition transfer.
    // Protection observes both native and paid deliveries; destination overrides
    // and payment apply only to an explicitly requested paid carrier.
    public static class ResoluteReplenishment
    {
        internal sealed class Quote
        {
            internal string Id;
            internal Airbase Source;
            internal AircraftDefinition Definition;
            internal Loadout Loadout;
            internal float Fuel, AircraftPrice, PayloadPrice, CapacityKg;
            internal int Stock, NavalPallets;
            internal bool PurchasesAircraft, Ready;
            internal string PayloadName;
            internal float Price => AircraftPrice + PayloadPrice;
        }
        internal sealed class Contract
        {
            internal int Id;
            internal float MaximumPrice;
            internal ResoluteReplenishmentState State;
            internal Ship Ship;
            internal Player Payer;
            internal FactionHQ HQ;
            internal Quote Quote;
            internal object Mission;
            internal Hangar Hangar;
            internal Aircraft Carrier;
            internal readonly List<Unit> Cargo = new List<Unit>();
            internal bool Accepted, Debited, Spawned, FundsRefunded, AirframeRefunded, Cancelled, Finished, SawLoadedCargo, StockConsumed;
            internal bool EarlyAssociationCreated, NativeTransportObserved;
            internal string Status = "Queued; payment is taken at dispatch";
        }

        private sealed class PendingTicket
        {
            internal WeakReference<Contract> Contract;
            internal bool Cancelled;
        }
        // A late callback retains its own loadout identity even if another order
        // uses the same hangar. Tombstones never root an old ship or mission.
        private static readonly ConditionalWeakTable<Loadout, PendingTicket> Pending = new ConditionalWeakTable<Loadout, PendingTicket>();
        private static readonly ConditionalWeakTable<Aircraft, Contract> Dedicated = new ConditionalWeakTable<Aircraft, Contract>();
        private static readonly List<WeakReference<Contract>> Reservations = new List<WeakReference<Contract>>();
        private sealed class SupplyAssociation { internal Ship Ship; internal object Mission; }
        private static readonly ConditionalWeakTable<Aircraft, SupplyAssociation> SupplyAssignments = new ConditionalWeakTable<Aircraft, SupplyAssociation>();
        [ThreadStatic] private static Contract dispatch;
        [ThreadStatic] private static Contract choosing;
        [ThreadStatic] private static Aircraft choosingAircraft;
        [ThreadStatic] private static Ship explicitRequest;
        internal const int MaximumOutstanding = 5;
        internal const float DispatchInterval = 15f;

        public static bool IsPendingSupplyAircraft(Ship ship, Unit unit)
        {
            Ship destination;
            return TryGetSupplyDestination(unit, out destination) && destination == ship;
        }
        internal static bool TryGetSupplyDestination(Unit unit, out Ship ship)
        {
            ship = null;
            Aircraft aircraft = unit as Aircraft;
            SupplyAssociation association;
            if (aircraft == null || aircraft.disabled || !aircraft.gameObject.activeInHierarchy ||
                !SupplyAssignments.TryGetValue(aircraft, out association) || !Live(association.Ship) ||
                aircraft.NetworkHQ != association.Ship.NetworkHQ || !ReferenceEquals(association.Mission, MissionManager.CurrentMission) || !HasNavalCargo(aircraft)) return false;
            ship = association.Ship; return true;
        }
        public static void GetPendingSupplyAircraft(Ship ship, List<Aircraft> result)
        {
            if (result == null) return;
            result.Clear();
            ResoluteReplenishmentState state = ship != null ? ship.GetComponent<ResoluteReplenishmentState>() : null;
            if (state == null) return;
            for (int i = state.SupplyAircraft.Count - 1; i >= 0; i--)
            {
                Aircraft aircraft;
                if (!state.SupplyAircraft[i].TryGetTarget(out aircraft) || !IsPendingSupplyAircraft(ship, aircraft))
                    state.SupplyAircraft.RemoveAt(i);
                else result.Add(aircraft);
            }
        }
        private static bool HasNavalCargo(Aircraft aircraft)
        {
            if (aircraft.weaponStations != null)
                foreach (WeaponStation station in aircraft.weaponStations)
                    if (station != null && station.WeaponInfo != null && station.WeaponInfo.cargo && station.WeaponInfo.rearmShip)
                    {
                        bool physicalCargo = false;
                        foreach (Weapon weapon in station.Weapons)
                            if (weapon is MountedCargo cargo)
                            {
                                physicalCargo = true;
                                // Fire removes the displayed round before its ramp/rail
                                // animation. The native mount stays active until spawn.
                                if (cargo.gameObject.activeInHierarchy && cargo.IsAttached() && !ResoluteNavalCargoState.Detached(cargo)) return true;
                            }
                        if (!physicalCargo && station.Ammo > 0) return true;
                    }
            return false;
        }
        internal static void ObserveDestination(bool selected, Unit destination)
        {
            Aircraft aircraft = choosingAircraft;
            if (aircraft == null) return;
            Ship ship = selected ? destination as Ship : null;
            AssignSupply(aircraft, ship);
        }
        private static void AssignSupply(Aircraft aircraft, Ship ship)
        {
            RetireAssociation(aircraft);
            if (!Live(ship) || aircraft.NetworkHQ != ship.NetworkHQ || !HasNavalCargo(aircraft)) return;
            SupplyAssignments.Add(aircraft, new SupplyAssociation { Ship = ship, Mission = MissionManager.CurrentMission });
            var entries = State(ship).SupplyAircraft;
            bool exists = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Aircraft existing;
                if (!entries[i].TryGetTarget(out existing) || !IsPendingSupplyAircraft(ship, existing)) entries.RemoveAt(i);
                else if (existing == aircraft) exists = true;
            }
            if (!exists) entries.Add(new WeakReference<Aircraft>(aircraft));
        }
        internal static void RetireAssociation(Aircraft aircraft)
        {
            if (ReferenceEquals(aircraft, null)) return;
            if (SupplyAssignments.TryGetValue(aircraft, out SupplyAssociation previous) && previous.Ship != null)
            {
                var state = previous.Ship.GetComponent<ResoluteReplenishmentState>();
                if (state != null)
                    for (int i = state.SupplyAircraft.Count - 1; i >= 0; i--)
                        if (!state.SupplyAircraft[i].TryGetTarget(out Aircraft entry) || entry == null || entry == aircraft)
                            state.SupplyAircraft.RemoveAt(i);
            }
            SupplyAssignments.Remove(aircraft);
        }
        internal static void TransportExited(Aircraft aircraft)
        {
            if (aircraft != null && Dedicated.TryGetValue(aircraft, out Contract c)) c.NativeTransportObserved = true;
            RetireAssociation(aircraft);
        }
        private static void AssociatePaidTakeoff(Contract c)
        {
            // An explicit paid destination is known before native takeoff. Wait
            // for real loaded cargo, and never revive it after a native choice/exit.
            if (c.EarlyAssociationCreated || c.NativeTransportObserved || c.Finished || c.Carrier == null || !HasNavalCargo(c.Carrier)) return;
            AssignSupply(c.Carrier, c.Ship); c.EarlyAssociationCreated = true;
        }

        public static void Ensure(Ship ship)
        {
            if (ship == null || !Plugin.IsResolute(ship.definition)) return;
            if (ship.GetComponent<ResoluteReplenishmentState>() == null)
                ship.gameObject.AddComponent<ResoluteReplenishmentState>().Initialize(ship);
        }

        internal static ResoluteReplenishmentState State(Ship ship)
        {
            Ensure(ship);
            return ship != null ? ship.GetComponent<ResoluteReplenishmentState>() : null;
        }

        internal static bool Live(Ship ship) => MissionManager.IsRunning && ship != null && ship.gameObject.activeInHierarchy && !ship.disabled && ship.IsServer && ship.LocalSim &&
            ship.NetworkHQ != null && Plugin.IsResolute(ship.definition);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static ResoluteReplenishmentSnapshot Capture(Ship ship, string optionId = null)
        {
            ResoluteReplenishmentState state = State(ship);
            if (state == null || !Live(ship)) return new ResoluteReplenishmentSnapshot { Status = "Replenishment unavailable" };
            return state.Snapshot(optionId);
        }

        public static bool TryRequestManual(Ship ship, out string reason, float? expectedPrice = null, string optionId = null)
        {
            if (!Live(ship) || !ResoluteCommandApi.CanCommand(ship, out reason))
            { reason = "Replenishment requires a live ship controlled by the mission host."; return false; }
            Player payer = ResoluteCommandSession.GetCommander(ship);
            Player registered;
            if (payer == null || !payer.IsServer || payer.HQ != ship.NetworkHQ ||
                !UnitRegistry.playerLookup.TryGetValue(new PlayerRef(payer), out registered) || registered != payer ||
                !ship.NetworkHQ.factionPlayers.Contains(new PlayerRef(payer)))
            { reason = "Open this ship's command interface to request replenishment."; return false; }
            ResoluteReplenishmentState state = State(ship);
            if (state.Outstanding >= MaximumOutstanding)
            { reason = "Five replenishment runs are already outstanding."; return false; }
            state.RefreshInventoryCached(true);
            if (!state.Inventory.HasDeliverableDeficit)
            { reason = "No ammunition that naval supplies can replenish is missing."; return false; }
            Quote quote;
            if (!TryQuote(ship, out quote, out reason, optionId)) return false;
            if (expectedPrice.HasValue && (!Finite(expectedPrice.Value) ||
                Math.Abs(expectedPrice.Value - quote.Price) > 0.000001f))
            { reason = "The replenishment price changed. Review the updated price and request again."; return false; }
            if (!Finite(payer.Allocation) || payer.Allocation - ReservedPrice(payer) < quote.Price)
            { reason = "Insufficient uncommitted allocation for this replenishment run."; return false; }
            var contract = new Contract { Id = state.NextId++, MaximumPrice = quote.Price, State = state, Ship = ship, Payer = payer,
                HQ = ship.NetworkHQ, Quote = quote, Mission = MissionManager.CurrentMission };
            PruneReservations(); Reservations.Add(new WeakReference<Contract>(contract));
            state.Orders.Add(contract);
            ProcessQueue(state);
            state.InvalidateQuote();
            reason = contract.Status;
            return !contract.Cancelled;
        }

        // Queued runs reserve a spending ceiling in this service, not money or
        // faction airframes. Dispatch revalidates everything. A stock loss may
        // delay a discounted request; it can never silently buy a full airframe.
        internal static float ReservedPrice(Player payer, Contract except = null)
        {
            float total = 0f;
            foreach (var weak in Reservations)
                if (weak.TryGetTarget(out Contract c) && c != except && !c.Finished && !c.Debited && c.Payer == payer &&
                    ReferenceEquals(c.Mission, MissionManager.CurrentMission)) total += c.MaximumPrice;
            return total;
        }
        private static int ReservedStock(FactionHQ hq, AircraftDefinition definition, Contract queued = null)
        {
            int count = 0;
            foreach (var weak in Reservations)
            {
                if (!weak.TryGetTarget(out Contract c)) continue;
                // Reservations are appended in global request order. An existing
                // request yields stock only to earlier reservations, even across
                // ships. Later requests cannot strand the head after native use
                // reduces stock. A new quote counts every existing reservation.
                if (ReferenceEquals(c, queued)) break;
                if (!c.Finished && !c.Accepted && !c.StockConsumed &&
                    !c.Quote.PurchasesAircraft && c.HQ == hq && c.Quote.Definition == definition &&
                    ReferenceEquals(c.Mission, MissionManager.CurrentMission)) count++;
            }
            return count;
        }
        public static bool CancelManual(Ship ship, int orderId, out string reason)
        {
            if (!Live(ship) || !ResoluteCommandApi.CanCommand(ship, out reason))
            { reason = "Open a live, host-controlled ship to cancel a queued run."; return false; }
            Player payer = ResoluteCommandSession.GetCommander(ship);
            foreach (Contract c in State(ship).Orders)
                if (c.Id == orderId && !c.Finished && c.Payer == payer)
                {
                    if (c.Spawned) { reason = "This aircraft has already launched."; return false; }
                    FailBeforeSpawn(c, "Replenishment cancelled before takeoff; any payment returned.");
                    reason = c.Status; return true;
                }
            reason = "This queued run is no longer available."; return false;
        }
        internal static void ProcessQueue(ResoluteReplenishmentState state)
        {
            if (Time.timeSinceLevelLoad < state.NextDispatch || !Live(state.Ship)) return;
            Contract c = null;
            foreach (Contract candidate in state.Orders)
                if (!candidate.Finished && !candidate.Accepted && !candidate.Spawned) { c = candidate; break; }
            if (c == null) return;
            if (!ValidDestination(c)) { FailBeforeSpawn(c, "Queued replenishment cancelled after the ship or faction changed."); return; }
            state.RefreshInventoryCached(true);
            if (!state.Inventory.HasDeliverableDeficit) { FailBeforeSpawn(c, "Queued run cancelled: ammunition is already replenished."); return; }
            Quote quote;
            string reason;
            if (!TryQuote(c.Ship, out quote, out reason, c.Quote.Id, c))
            { c.Status = "Waiting for an eligible source airbase and naval supply loadout"; return; }
            if (quote.Price > c.MaximumPrice + .000001f)
            { c.Status = "Waiting for the quoted stock price; cancel and reorder to purchase an aircraft"; return; }
            if (!quote.Ready) { c.Status = "Waiting for a free native hangar"; return; }
            if (!Finite(c.Payer.Allocation) || c.Payer.Allocation - ReservedPrice(c.Payer, c) < quote.Price)
            { c.Status = "Waiting for sufficient uncommitted player funds"; return; }
            c.Quote = quote;
            Dispatch(c);
            if (c.Accepted || c.Spawned) state.NextDispatch = Time.timeSinceLevelLoad + DispatchInterval;
        }
        private static void Dispatch(Contract contract)
        {
            Quote quote = contract.Quote;
            Contract previous = dispatch;
            try
            {
                dispatch = contract;
                Airbase.TrySpawnResult result = quote.Source.TrySpawnAircraft(null, quote.Definition,
                    new LiveryKey(quote.Definition.aircraftParameters.GetRandomLiveryForFaction(contract.HQ.faction)),
                    quote.Loadout, quote.Fuel);
                if (!result.Allowed)
                {
                    FailBeforeSpawn(contract, "No available native hangar could dispatch the aircraft.");
                    return;
                }
                NativeAccepted(contract, result);
                if (contract.Cancelled) return;
                RequestNative(contract.Ship);
                contract.Status = contract.Spawned ? "Supply aircraft dispatched" : "Supply aircraft preparing to launch";
            }
            catch (Exception error)
            {
                if (!contract.Spawned) FailBeforeSpawn(contract, "Supply dispatch failed before launch.");
                else contract.Status = "Supply aircraft dispatched; native confirmation reported an error.";
                Plugin.Instance?.LogStartupWarning("Resolute replenishment dispatch: " + error.Message);
            }
            finally { dispatch = previous; contract.State.InvalidateQuote(); }
        }

        internal static void RequestNative(Ship ship)
        {
            Ship previous = explicitRequest;
            try
            {
                explicitRequest = ship;
                ResoluteAmmoInventory.SynchronizeNativeCapacity(ship);
                ship.RequestRearm();
            }
            finally { explicitRequest = previous; }
        }

        internal static bool AllowNativeRequest(Unit unit)
        {
            Ship ship = unit as Ship;
            if (ship == null || !Plugin.IsResolute(ship.definition)) return true;
            if (!Live(ship)) return false;
            if (ship.HasRequestedRearm) return true;
            if (explicitRequest == ship) return true;
            if (ResoluteCommandSession.IsCommanded(ship)) return false;
            ResoluteReplenishmentState state = State(ship);
            state.RefreshInventoryCached(false);
            if (!state.Inventory.NeedsReplenishment) return false;
            ResoluteAmmoInventory.SynchronizeNativeCapacity(ship);
            return true;
        }

        internal static bool TryQuote(Ship ship, out Quote best, out string reason, string optionId = null, Contract queued = null)
        {
            best = null;
            reason = "No available native naval supply aircraft and source airbase.";
            foreach (Quote quote in DiscoverQuotes(ship, queued))
                if (optionId == null || optionId == quote.Id) { best = quote; break; }
            if (best == null) return false;
            reason = null; return true;
        }
        internal static List<Quote> DiscoverQuotes(Ship ship, Contract queued = null)
        {
            var results = new List<Quote>();
            if (!Live(ship)) return results;
            var candidates = new Dictionary<string, Quote>();
            foreach (Airbase source in ship.NetworkHQ.GetAirbases())
            {
                if (source == null || source.disabled || source.AttachedAirbase || source.CurrentHQ != ship.NetworkHQ) continue;
                // Native hangar compatibility, including pad size and disabled
                // facilities, determines the possible airframes. Busy hangars
                // remain visible as waiting options; we never bypass their gate.
                foreach (AircraftDefinition definition in source.GetAvailableAircraft())
                {
                    if (definition == null || definition.unitPrefab == null || definition.aircraftParameters == null ||
                        definition.aircraftParameters.StandardLoadouts == null) continue;
                    Aircraft prefab = definition.unitPrefab.GetComponent<Aircraft>();
                    WeaponManager manager = prefab != null ? prefab.weaponManager : null;
                    if (manager == null || manager.hardpointSets == null) continue;
                    int loadoutIndex = -1;
                    foreach (StandardLoadout standard in definition.aircraftParameters.StandardLoadouts)
                    {
                        loadoutIndex++;
                        if (standard == null || standard.disabled || standard.loadout == null || !standard.AllowedByHQ(manager, ship.NetworkHQ)) continue;
                        float payload;
                        if (!NavalLoadoutPrice(standard.loadout, manager, source, ship.NetworkHQ, out payload)) continue;
                        int stock = Math.Max(0, ship.NetworkHQ.GetUnitSupply(definition) - ReservedStock(ship.NetworkHQ, definition, queued));
                        bool purchase = stock == 0;
                        float airframe = definition.value * (purchase ? 1f : .2f), total = airframe + payload;
                        if (!Finite(airframe) || airframe < 0f || !Finite(total) || total <= 0f) continue;
                        var quote = new Quote { Id = definition.jsonKey + ":" + loadoutIndex, Source = source, Definition = definition,
                            Loadout = new Loadout { weapons = new List<WeaponMount>(standard.loadout.weapons) },
                            Fuel = Mathf.Clamp01(standard.FuelRatio), AircraftPrice = airframe, PayloadPrice = payload, PayloadName = standard.Name,
                            Stock = stock, PurchasesAircraft = purchase, Ready = source.CanSpawnAircraft(definition) };
                        LoadoutCapacity(quote, manager);
                        if (candidates.TryGetValue(quote.Id, out Quote earlier))
                        {
                            if (earlier.Ready && !quote.Ready) continue;
                            if (earlier.Ready == quote.Ready && FastMath.SquareDistance(ship.GlobalPosition(), earlier.Source.center.GlobalPosition()) <=
                                FastMath.SquareDistance(ship.GlobalPosition(), source.center.GlobalPosition())) continue;
                        }
                        candidates[quote.Id] = quote;
                    }
                }
            }
            results.AddRange(candidates.Values);
            results.Sort((a, b) => { int price = a.Price.CompareTo(b.Price); return price != 0 ? price : string.CompareOrdinal(a.Id, b.Id); });
            return results;
        }
        private static void LoadoutCapacity(Quote quote, WeaponManager manager)
        {
            for (int i = 0; i < quote.Loadout.weapons.Count; i++)
            {
                WeaponMount mount = quote.Loadout.weapons[i];
                if (mount?.info == null || !mount.info.cargo || !mount.info.rearmShip) continue;
                int hardpoints = manager.hardpointSets[i].hardpoints.Count;
                int count = mount.ammo * hardpoints;
                quote.NavalPallets += count;
                if (count <= 0) continue;
                // Hardpoint.SpawnMount instantiates this assembly, whose cargo
                // can sit below a rail/pylon root. Each MountedCargo is one
                // physical pallet already represented in mount.ammo: multiply
                // the assembly total by hardpoints, never by ammo again.
                MountedCargo[] mounted = mount.prefab != null ? mount.prefab.GetComponentsInChildren<MountedCargo>(true) : Array.Empty<MountedCargo>();
                if (mounted.Length > 0)
                {
                    foreach (MountedCargo cargo in mounted)
                        quote.CapacityKg += hardpoints * PayloadCapacity(cargo?.cargo?.unitPrefab);
                    continue;
                }
                // Some loadouts expose only a per-round weapon/payload prefab.
                // That can be either MountedCargo or the deployed supply unit.
                GameObject round = mount.info.weaponPrefab;
                MountedCargo prototype = round != null ? round.GetComponentInChildren<MountedCargo>(true) : null;
                quote.CapacityKg += count * PayloadCapacity(prototype != null ? prototype.cargo?.unitPrefab : round);
            }
        }
        private static float PayloadCapacity(GameObject prefab)
        {
            Rearmer rearmer = prefab != null ? prefab.GetComponent<Rearmer>() : null;
            return rearmer != null && Finite(rearmer.Capacity) && rearmer.Capacity > 0f ? rearmer.Capacity : 0f;
        }

        internal static bool NavalLoadoutPrice(Loadout loadout, WeaponManager manager, Airbase source, FactionHQ hq, out float price)
        {
            price = 0f; bool naval = false;
            if (loadout.weapons == null || loadout.weapons.Count != manager.hardpointSets.Length) return false;
            for (int i = 0; i < manager.hardpointSets.Length; i++)
            {
                WeaponMount mount = loadout.weapons[i]; HardpointSet hardpoints = manager.hardpointSets[i];
                if (mount == null) continue;
                if (hardpoints == null || !WeaponChecker.MountAllowedHQ(mount, hq) ||
                    !WeaponChecker.MountAllowedAirbase(mount, source) || !WeaponChecker.MountAllowedHardpoint(mount, hardpoints) ||
                    !WeaponChecker.MountAllowedConflict(hardpoints, loadout) || mount.info != null && mount.info.nuclear) return false;
                if (mount.info != null && mount.info.cargo && mount.info.rearmShip && mount.ammo > 0 && hardpoints.hardpoints.Count > 0) naval = true;
                float cost = mount.emptyCost + (mount.info != null ? mount.ammo * mount.info.costPerRound : 0f);
                if (!Finite(cost) || cost < 0f) return false;
                price += hardpoints.hardpoints.Count * cost;
            }
            return naval && Finite(price) && price >= 0f;
        }

        internal static Contract BindHangar(Hangar hangar, Player player, AircraftDefinition definition, Loadout loadout)
        {
            Contract c = dispatch;
            if (c == null || player != null || definition != c.Quote.Definition || !ReferenceEquals(loadout, c.Quote.Loadout) || !hangar.CanSpawnAircraft(definition)) return null;
            c.Hangar = hangar;
            Pending.Remove(loadout); Pending.Add(loadout, new PendingTicket { Contract = new WeakReference<Contract>(c) });
            // Reserve only once a real, available native hangar accepts the call.
            // Its later cancellation refunds money and consumed stock separately.
            if (!c.Debited) { c.Payer.AddAllocation(-c.Quote.Price); c.Debited = true; }
            return c;
        }
        internal static bool StockChangeAllowed(FactionHQ hq, UnitDefinition definition, int amount, out Contract tracked)
        {
            tracked = null;
            Contract c = dispatch;
            if (c == null || amount != -1 || c.HQ != hq || c.Quote.Definition != definition || c.Hangar == null || !c.Debited) return true;
            // Native AI spawning always consumes faction stock. A paid new
            // airframe has no faction stock to consume: leave all inventory and
            // player reservation lists untouched for this exact dispatch only.
            if (c.Quote.PurchasesAircraft) return false;
            tracked = c; return true;
        }
        internal static void StockChanged(Contract c)
        {
            if (c == null) return;
            c.StockConsumed = true;
            if (c.Cancelled) RefundBeforeSpawn(c);
        }
        internal static void NativeAccepted(Contract c, Airbase.TrySpawnResult result)
        {
            if (c == null || !result.Allowed) return;
            c.Accepted = true; c.Hangar = result.Hangar;
            if (c.Cancelled) RefundBeforeSpawn(c);
        }
        internal static UniTask ObserveQueue(Hangar hangar, UniTask task)
        {
            Contract c = dispatch;
            return c != null && c.Hangar == hangar ? ObserveQueueCompletion(task, c) : task;
        }
        private static async UniTask ObserveQueueCompletion(UniTask task, Contract c)
        {
            try { await task; }
            finally { if (!c.Spawned) FailBeforeSpawn(c, "Native hangar launch ended before spawning; reservation returned."); }
        }
        internal static bool AllowQueuedSpawn(Hangar hangar, Player player, AircraftDefinition definition, Loadout loadout)
        {
            PendingTicket ticket;
            if (player != null || loadout == null || !Pending.TryGetValue(loadout, out ticket)) return true;
            Contract c;
            if (ticket.Cancelled || !ticket.Contract.TryGetTarget(out c)) return false;
            if (c.Hangar == hangar && definition == c.Quote.Definition && !c.Cancelled && ValidDestination(c) && hangar.IsFunctional() && c.Quote.Source != null && c.Quote.Source.CurrentHQ == c.HQ) return true;
            FailBeforeSpawn(c, "Supply launch cancelled before takeoff.");
            return false;
        }
        internal static void BindAircraft(Aircraft aircraft, Player player, Hangar hangar, Loadout loadout)
        {
            Contract c; PendingTicket ticket;
            if (aircraft == null || player != null || hangar == null || loadout == null || !Pending.TryGetValue(loadout, out ticket) || ticket.Cancelled || !ticket.Contract.TryGetTarget(out c) || c.Hangar != hangar ||
                !ReferenceEquals(loadout, c.Quote.Loadout) || aircraft.definition != c.Quote.Definition || aircraft.NetworkHQ != c.HQ) return;
            c.Carrier = aircraft; c.Spawned = true; c.Status = "Supply aircraft dispatched";
            c.SawLoadedCargo = HasNavalCargo(aircraft);
            Dedicated.Remove(aircraft); Dedicated.Add(aircraft, c);
            Pending.Remove(loadout);
            AssociatePaidTakeoff(c);
        }
        internal static void BindCargo(Unit cargo, Unit owner)
        {
            Contract c;
            Aircraft aircraft = owner as Aircraft;
            if (cargo == null || aircraft == null || !Dedicated.TryGetValue(aircraft, out c) || c.Finished ||
                cargo.GetComponent<Rearmer>() == null || c.Cargo.Contains(cargo)) return;
            c.Cargo.Add(cargo);
        }
        internal static bool ValidDestination(Contract c)
        {
            Player registered;
            return Live(c.Ship) && c.Ship.NetworkHQ == c.HQ && ReferenceEquals(c.Mission, MissionManager.CurrentMission) &&
                c.Payer != null && c.Payer.IsServer && c.Payer.HQ == c.HQ &&
                UnitRegistry.playerLookup.TryGetValue(new PlayerRef(c.Payer), out registered) && registered == c.Payer &&
                c.HQ.factionPlayers.Contains(new PlayerRef(c.Payer));
        }

        internal static void Tick(Contract c)
        {
            if (c == null || c.Finished) return;
            if (!c.Spawned)
            {
                if (!ValidDestination(c) || c.Accepted && (c.Hangar == null || !c.Hangar.IsFunctional() || c.Quote.Source == null || c.Quote.Source.CurrentHQ != c.HQ))
                    FailBeforeSpawn(c, "Supply launch failed; allocation and reserved airframe returned.");
                return;
            }
            if (!ValidDestination(c)) { Finish(c, "Supply assignment ended after the ship or faction changed."); return; }
            AssociatePaidTakeoff(c);
            // Other deliveries can fill the ship while this carrier is inbound.
            // Keep its accounting and escort association until its own payload
            // is deployed, or native transport selects another destination.
            if (c.Carrier == null || c.Carrier.disabled)
            {
                Finish(c, c.Cargo.Count > 0 ? "Supply flight ended; dropped supplies remain available to the native system" :
                    "Supply aircraft lost; existing native request remains active");
                return;
            }
            bool loaded = HasNavalCargo(c.Carrier), salvo = false;
            foreach (WeaponStation station in c.Carrier.weaponStations)
                if (station.Cargo) salvo |= station.SalvoInProgress;
            c.SawLoadedCargo |= loaded;
            // The paid contract covers a flight and its physical cargo delivery.
            // Crates may remain useful, out of reach, or too light for the next
            // missing round; their native lifetime must not lock another purchase.
            if ((c.SawLoadedCargo || c.Cargo.Count > 0) && !loaded && !salvo)
            {
                RetireAssociation(c.Carrier);
                Finish(c, c.Cargo.Count > 0 ? "Supplies delivered; native ammunition transfer remains active" :
                    "Naval payload no longer aboard; existing native request remains active");
            }
            else if (c.Cargo.Count > 0) c.Status = "Deploying remaining naval supplies";
        }

        internal static void FailBeforeSpawn(Contract c, string status)
        {
            c.Cancelled = true;
            PendingTicket ticket;
            if (Pending.TryGetValue(c.Quote.Loadout, out ticket)) ticket.Cancelled = true;
            RefundBeforeSpawn(c);
            Finish(c, status);
            // Keep a cancelled queued identity until its native spawn callback
            // arrives; otherwise that callback could create a refunded aircraft.
        }
        private static void RefundBeforeSpawn(Contract c)
        {
            if (c.Spawned) return;
            Player registered;
            bool sameMission = ReferenceEquals(c.Mission, MissionManager.CurrentMission);
            if (c.Debited && !c.FundsRefunded && sameMission && c.Payer != null && c.Payer.IsServer && c.Payer.HQ == c.HQ &&
                UnitRegistry.playerLookup.TryGetValue(new PlayerRef(c.Payer), out registered) && registered == c.Payer && c.HQ.factionPlayers.Contains(new PlayerRef(c.Payer)))
            { c.FundsRefunded = true; c.Payer.AddAllocation(c.Quote.Price); }
            if (c.StockConsumed && !c.AirframeRefunded && sameMission && c.HQ != null && c.HQ.IsServer)
            { c.AirframeRefunded = true; c.HQ.AddSupplyUnit(c.Quote.Definition, 1); }
        }
        private static void PruneReservations()
        {
            for (int i = Reservations.Count - 1; i >= 0; i--)
                if (!Reservations[i].TryGetTarget(out Contract c) || c.Finished) Reservations.RemoveAt(i);
        }
        internal static void BeforePlayerLeaves(FactionHQ hq, Player player)
        {
            if (hq == null || !hq.IsServer || player == null) return;
            foreach (var weak in Reservations)
                if (weak.TryGetTarget(out Contract c) && !c.Finished && c.HQ == hq && c.Payer == player)
                {
                    if (!c.Spawned) FailBeforeSpawn(c, "Supply launch cancelled before faction departure; reservation returned.");
                    else Finish(c, "Supply assignment ended after faction departure.");
                }
            PruneReservations();
        }
        internal static void BeforeRegistryClear()
        {
            foreach (var weak in Reservations)
                if (weak.TryGetTarget(out Contract c) && !c.Finished)
                {
                    if (!c.Spawned) FailBeforeSpawn(c, "Supply launch cancelled before mission reset.");
                    else Finish(c, "Supply assignment ended at mission reset.");
                }
            Reservations.Clear();
        }
        internal static void Finish(Contract c, string status)
        {
            c.Status = status; c.Finished = true;
            if (!ReferenceEquals(c.Carrier, null)) Dedicated.Remove(c.Carrier);
            c.State.LastStatus = status;
            c.State?.InvalidateQuote();
        }
        internal struct ChooserScope { internal Contract Contract; internal Aircraft Aircraft; }
        internal static ChooserScope EnterChooser(Aircraft aircraft)
        {
            var previous = new ChooserScope { Contract = choosing, Aircraft = choosingAircraft };
            choosing = null; choosingAircraft = aircraft;
            if (aircraft != null && Dedicated.TryGetValue(aircraft, out Contract c) && !c.Finished)
            { choosing = c; c.NativeTransportObserved = true; }
            return previous;
        }
        internal static void LeaveChooser(ChooserScope previous) { choosing = previous.Contract; choosingAircraft = previous.Aircraft; }
        internal static bool ChooseDedicated(RearmMissionController controller, bool ships, out Unit destination)
        {
            destination = null;
            Contract c = choosing;
            if (c == null || c.Finished) return false;
            if (ships && ValidDestination(c) && c.HQ.RearmMissionController == controller && c.Ship.HasRequestedRearm && c.Ship.radarAlt <= 10f)
                destination = c.Ship;
            return true;
        }
    }

    internal static class ResoluteNavalCargoState
    {
        internal static readonly AccessTools.FieldRef<MountedCargo, bool> Detached = AccessTools.FieldRefAccess<MountedCargo, bool>("detached");
    }

    internal sealed class ResoluteReplenishmentState : MonoBehaviour
    {
        internal Ship Ship;
        internal readonly ResoluteAmmoInventory Inventory = new ResoluteAmmoInventory();
        internal readonly List<WeakReference<Aircraft>> SupplyAircraft = new List<WeakReference<Aircraft>>();
        internal readonly List<ResoluteReplenishment.Contract> Orders = new List<ResoluteReplenishment.Contract>();
        internal float NextDispatch;
        internal int NextId = 1;
        internal string LastStatus;
        internal int Outstanding { get { int count = 0; foreach (var order in Orders) if (!order.Finished) count++; return count; } }
        private FactionHQ registeredHQ;
        private float nextAutomatic, nextContract, nextQuote, nextInventory, nextPickup;
        internal void RefreshInventoryCached(bool force)
        {
            float now = Time.timeSinceLevelLoad;
            if (!force && now < nextInventory) return;
            Inventory.Refresh(Ship); nextInventory = now + 5f;
        }
        private List<ResoluteReplenishment.Quote> quotes = new List<ResoluteReplenishment.Quote>();
        internal void Initialize(Ship ship) { Ship = ship; registeredHQ = ship.NetworkHQ; }
        internal void InvalidateQuote() { nextQuote = 0f; }
        private void Update()
        {
            if (Ship == null) return;
            float now = Time.timeSinceLevelLoad;
            if (now >= nextContract)
            {
                nextContract = now + 1f;
                foreach (var order in Orders) ResoluteReplenishment.Tick(order);
                Orders.RemoveAll(order => order.Finished);
                ResoluteReplenishment.ProcessQueue(this);
            }
            if (!ResoluteReplenishment.Live(Ship)) return;
            if (now >= nextPickup)
            {
                nextPickup = now + 5f;
                if (ResoluteNavalSupplyPickup.NeedsFallback(Ship))
                {
                    RefreshInventoryCached(true);
                    if (Inventory.HasDeliverableDeficit && ResoluteNavalSupplyPickup.TryCollect(Ship))
                        RefreshInventoryCached(true);
                }
            }
            if (now < nextAutomatic) return;
            nextAutomatic = now + 5f;
            if (registeredHQ != Ship.NetworkHQ)
            {
                if (Ship.HasRequestedRearm)
                {
                    registeredHQ?.RearmMissionController.DeregisterNeedsRearm(Ship);
                    Ship.NetworkHQ.RearmMissionController.RegisterNeedsRearm(Ship);
                }
                registeredHQ = Ship.NetworkHQ;
            }
            if (Ship.HasRequestedRearm || ResoluteCommandSession.IsCommanded(Ship)) return;
            RefreshInventoryCached(false);
            if (Inventory.NeedsReplenishment) ResoluteReplenishment.RequestNative(Ship);
        }
        internal ResoluteReplenishmentSnapshot Snapshot(string optionId)
        {
            if (Time.timeSinceLevelLoad >= nextQuote)
            {
                nextQuote = Time.timeSinceLevelLoad + 1f;
                RefreshInventoryCached(true);
                quotes = ResoluteReplenishment.DiscoverQuotes(Ship);
            }
            Player payer = ResoluteCommandSession.GetCommander(Ship);
            float reserved = ResoluteReplenishment.ReservedPrice(payer);
            bool available = Outstanding < ResoluteReplenishment.MaximumOutstanding && payer != null && Inventory.HasDeliverableDeficit;
            var options = new List<ResoluteReplenishmentOption>();
            ResoluteReplenishment.Quote quote = null;
            foreach (var candidate in quotes)
            {
                if (optionId == candidate.Id || optionId == null && quote == null) quote = candidate;
                bool afford = payer != null && ResoluteReplenishment.Finite(payer.Allocation) && payer.Allocation - reserved >= candidate.Price;
                options.Add(new ResoluteReplenishmentOption {
                    Id = candidate.Id, CarrierName = candidate.Definition.unitName, PayloadName = candidate.PayloadName,
                    Stock = candidate.Stock, NavalPallets = candidate.NavalPallets, CapacityKg = candidate.CapacityKg,
                    PurchasesAircraft = candidate.PurchasesAircraft, Price = candidate.Price, AircraftPrice = candidate.AircraftPrice,
                    PayloadPrice = candidate.PayloadPrice, CanRequest = available && afford,
                    Status = !afford ? "Insufficient uncommitted allocation" : !candidate.Ready ? "Queues until a native hangar is free" :
                        candidate.PurchasesAircraft ? "Purchase aircraft plus loadout" : "20% aircraft operating cost plus loadout"
                });
            }
            var queue = new List<ResoluteReplenishmentOrder>();
            foreach (var order in Orders)
                if (!order.Finished) queue.Add(new ResoluteReplenishmentOrder { Id = order.Id, CarrierName = order.Quote.Definition.unitName,
                    Status = order.Status, Price = order.Debited ? order.Quote.Price : order.MaximumPrice,
                    CanCancel = !order.Spawned && order.Payer == payer });
            bool affordable = payer != null && quote != null && ResoluteReplenishment.Finite(payer.Allocation) && payer.Allocation - reserved >= quote.Price;
            return new ResoluteReplenishmentSnapshot {
                Status = Outstanding >= ResoluteReplenishment.MaximumOutstanding ? "Five replenishment runs are outstanding" :
                    !Inventory.HasDeliverableDeficit ? "Ammunition ready" : quote == null ? "No eligible naval supply aircraft and source airbase" :
                    !affordable ? "Insufficient uncommitted allocation" : Outstanding > 0 ? "Replenishment runs outstanding: " + Outstanding :
                    LastStatus ?? (Ship.HasRequestedRearm ? "Awaiting native supplies" : "Replenishment available"),
                CanRequest = available && quote != null && affordable,
                Options = options.ToArray(), Queue = queue.ToArray(), Outstanding = Outstanding, QueueLimit = ResoluteReplenishment.MaximumOutstanding,
                DispatchCooldown = Math.Max(0f, NextDispatch - Time.timeSinceLevelLoad), ReservedPrice = reserved, SelectedOptionId = quote?.Id,
                Price = quote?.Price ?? 0f, AircraftPrice = quote?.AircraftPrice ?? 0f, PayloadPrice = quote?.PayloadPrice ?? 0f,
                CarrierName = quote?.Definition.unitName ?? "Supply aircraft", PayloadName = quote?.PayloadName ?? "Naval supplies"
            };
        }
        private void OnDestroy()
        {
            foreach (var order in Orders)
            {
                if (order.Finished) continue;
                if (!order.Spawned) ResoluteReplenishment.FailBeforeSpawn(order, "Supply launch cancelled before takeoff");
                else ResoluteReplenishment.Finish(order, "Supply assignment ended");
            }
        }
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.RequestRearm))]
    internal static class ResoluteNativeRearmGate
    {
        private static bool Prefix(Unit __instance) => ResoluteReplenishment.AllowNativeRequest(__instance);
    }
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RemovePlayer))]
    internal static class ResoluteSupplyPlayerDeparture
    { private static void Prefix(FactionHQ __instance, Player player) => ResoluteReplenishment.BeforePlayerLeaves(__instance, player); }
    [HarmonyPatch(typeof(UnitRegistry), nameof(UnitRegistry.Clear))]
    internal static class ResoluteSupplyRegistryReset
    { private static void Prefix() => ResoluteReplenishment.BeforeRegistryClear(); }
    [HarmonyPatch(typeof(Unit), "InitializeUnit")]
    internal static class ResoluteReplenishmentInitialize
    {
        private static void Postfix(Unit __instance) { if (__instance is Ship ship && ship.IsServer) ResoluteReplenishment.Ensure(ship); }
    }
    [HarmonyPatch(typeof(Hangar), nameof(Hangar.TrySpawnAircraft))]
    internal static class ResoluteSupplyHangarSelection
    {
        private static void Prefix(Hangar __instance, Player player, AircraftDefinition definition, Loadout loadout, out ResoluteReplenishment.Contract __state) => __state = ResoluteReplenishment.BindHangar(__instance, player, definition, loadout);
        private static void Postfix(Airbase.TrySpawnResult __result, ResoluteReplenishment.Contract __state) => ResoluteReplenishment.NativeAccepted(__state, __result);
    }
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AddSupplyUnit))]
    internal static class ResoluteSupplyPurchasedAirframe
    {
        private static bool Prefix(FactionHQ __instance, UnitDefinition unitDefinition, int amount, out ResoluteReplenishment.Contract __state) =>
            ResoluteReplenishment.StockChangeAllowed(__instance, unitDefinition, amount, out __state);
        private static void Postfix(ResoluteReplenishment.Contract __state) => ResoluteReplenishment.StockChanged(__state);
    }
    [HarmonyPatch(typeof(Hangar), "DoorSequenceCarrier")]
    internal static class ResoluteSupplyQueueCompletion
    {
        private static void Postfix(Hangar __instance, ref UniTask __result) => __result = ResoluteReplenishment.ObserveQueue(__instance, __result);
    }
    [HarmonyPatch(typeof(Hangar), "SpawnAircraft")]
    internal static class ResoluteSupplyQueuedSpawn
    {
        private static bool Prefix(Hangar __instance, Player player, AircraftDefinition definition, Loadout loadout) => ResoluteReplenishment.AllowQueuedSpawn(__instance, player, definition, loadout);
    }
    [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnAircraft))]
    internal static class ResoluteSupplyAircraftSpawn
    {
        private static void Postfix(Aircraft __result, Player player, Hangar spawningHangar, Loadout loadout) => ResoluteReplenishment.BindAircraft(__result, player, spawningHangar, loadout);
    }
    [HarmonyPatch(typeof(Spawner), nameof(Spawner.SpawnUnit))]
    internal static class ResoluteSupplyCargoSpawn
    {
        private static void Postfix(Unit __result, Unit owner) => ResoluteReplenishment.BindCargo(__result, owner);
    }
    [HarmonyPatch(typeof(AIHeloTransportState), "SearchForLandingSpot")]
    internal static class ResoluteSupplyTransportScope
    {
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> Aircraft = AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static void Prefix(AIHeloTransportState __instance, out ResoluteReplenishment.ChooserScope __state) => __state = ResoluteReplenishment.EnterChooser(Aircraft(__instance));
        private static Exception Finalizer(Exception __exception, ResoluteReplenishment.ChooserScope __state) { ResoluteReplenishment.LeaveChooser(__state); return __exception; }
    }
    [HarmonyPatch(typeof(AIHeloTransportState), nameof(AIHeloTransportState.LeaveState))]
    internal static class ResoluteSupplyTransportExit
    {
        private static readonly AccessTools.FieldRef<PilotBaseState, Aircraft> Aircraft = AccessTools.FieldRefAccess<PilotBaseState, Aircraft>("aircraft");
        private static void Prefix(AIHeloTransportState __instance) => ResoluteReplenishment.TransportExited(Aircraft(__instance));
    }
    [HarmonyPatch(typeof(RearmMissionController), nameof(RearmMissionController.TryGetUnitNeedingRearm))]
    internal static class ResoluteSupplyDestination
    {
        private static bool Prefix(RearmMissionController __instance, bool ships, ref Unit lowestAmmoUnit, ref bool __result)
        {
            Unit selected;
            if (!ResoluteReplenishment.ChooseDedicated(__instance, ships, out selected)) return true;
            lowestAmmoUnit = selected; __result = selected != null; return false;
        }
        private static void Postfix(bool __result, Unit lowestAmmoUnit) => ResoluteReplenishment.ObserveDestination(__result, lowestAmmoUnit);
    }
}
