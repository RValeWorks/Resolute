using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NuclearOption.Networking;

namespace Resolute
{
    // Personal command income follows native credited damage and valuation. It
    // never assigns a native player owner to an AI ship or changes faction income.
    internal static class ResoluteCommandIncome
    {
        internal sealed class Credit
        {
            internal Player Player;
            internal FactionHQ Faction;
            internal object Mission;
            internal PersistentID Dealer;
        }
        internal struct Origin
        {
            internal bool Active;
            internal PersistentID Dealer;
            internal Credit Credit;
        }
        private sealed class Launch { internal Origin Origin; }
        private sealed class Contribution { internal Credit Credit; internal float Damage; }
        private sealed class Ledger
        {
            internal bool Reported;
            internal readonly List<Contribution> Entries = new List<Contribution>();
        }
        internal sealed class Payment { internal Credit Credit; internal float Fraction; }
        private sealed class RewardScope { internal Credit Credit; internal Unit Target; }
        private static readonly Launch Uncommanded = new Launch { Origin = new Origin { Active = true } };

        private static ConditionalWeakTable<Missile, Launch> launches = new ConditionalWeakTable<Missile, Launch>();
        private static ConditionalWeakTable<Shockwave, Launch> waves = new ConditionalWeakTable<Shockwave, Launch>();
        private static ConditionalWeakTable<Ship, Credit> currentCredits = new ConditionalWeakTable<Ship, Credit>();
        private static ConditionalWeakTable<Unit, Ledger> ledgers = new ConditionalWeakTable<Unit, Ledger>();
        private static readonly FieldInfo DamageCredit = AccessTools.Field(typeof(Unit), "damageCredit");
        [ThreadStatic] internal static Origin Current;
        [ThreadStatic] private static RewardScope reward;

        internal static void Clear()
        {
            launches = new ConditionalWeakTable<Missile, Launch>();
            waves = new ConditionalWeakTable<Shockwave, Launch>();
            currentCredits = new ConditionalWeakTable<Ship, Credit>();
            ledgers = new ConditionalWeakTable<Unit, Ledger>();
            Current = default(Origin); reward = null;
        }

        private static bool Valid(Credit credit)
        {
            Player registered;
            return credit != null && credit.Player != null && credit.Player.IsServer &&
                credit.Faction != null && credit.Faction.IsServer && credit.Player.HQ == credit.Faction &&
                MissionManager.IsRunning && ReferenceEquals(credit.Mission, MissionManager.CurrentMission) &&
                UnitRegistry.playerLookup.TryGetValue(new PlayerRef(credit.Player), out registered) &&
                registered == credit.Player && credit.Faction.factionPlayers.Contains(new PlayerRef(credit.Player));
        }

        private static Credit Commander(Unit unit)
        {
            Ship ship = unit as Ship;
            if (ship == null || !ship.IsServer || !Plugin.IsResolute(ship.definition)) return null;
            Player player = ResoluteCommandSession.GetCommander(ship);
            if (player == null || player.HQ == null || player.HQ != ship.NetworkHQ) return null;
            Credit credit;
            if (!currentCredits.TryGetValue(ship, out credit) || credit.Player != player ||
                credit.Faction != player.HQ || !ReferenceEquals(credit.Mission, MissionManager.CurrentMission))
            {
                credit = new Credit { Player = player, Faction = player.HQ,
                    Mission = MissionManager.CurrentMission, Dealer = ship.persistentID };
                currentCredits.Remove(ship); currentCredits.Add(ship, credit);
            }
            return Valid(credit) ? credit : null;
        }

        internal static void Registered(Unit owner, Missile missile)
        {
            if (owner == null || missile == null || !owner.IsServer || launches.TryGetValue(missile, out _)) return;
            Launch parent;
            if (owner is Missile previous && launches.TryGetValue(previous, out parent))
            { launches.Add(missile, parent); return; }
            if (!(owner is Ship) || !Plugin.IsResolute(owner.definition)) return;
            // Remember an uncommanded launch too: a later commander must not
            // acquire income from weapons already in the air.
            Credit credit = Commander(owner);
            launches.Add(missile, credit == null ? Uncommanded : new Launch { Origin = new Origin {
                Active = true, Dealer = owner.persistentID, Credit = credit } });
        }

        internal static Origin MissileOrigin(Missile missile)
        {
            Launch launch;
            if (missile != null && launches.TryGetValue(missile, out launch)) return launch.Origin;
            return new Origin { Active = true, Dealer = missile != null ? missile.ownerID : PersistentID.None };
        }

        internal static Origin BlastOrigin(PersistentID missileID)
        {
            if (missileID.NotValid) return Current; // Native gun fragmentation keeps its damage-time owner.
            Unit unit;
            return UnitRegistry.TryGetUnit(missileID, out unit) && unit is Missile missile ?
                MissileOrigin(missile) : new Origin { Active = true };
        }

        internal static void WaveCreated(Shockwave wave, PersistentID ownerID)
        {
            if (wave == null) return;
            Origin origin = Current;
            if (!origin.Active)
            {
                Unit unit;
                origin = new Origin { Active = true, Dealer = ownerID,
                    Credit = UnitRegistry.TryGetUnit(ownerID, out unit) ? Commander(unit) : null };
            }
            waves.Remove(wave);
            // Unowned/client effects need no allocation: WaveOrigin already
            // suppresses retroactive credit for every uncached wave.
            if (origin.Dealer != ownerID || !Valid(origin.Credit)) return;
            waves.Add(wave, new Launch { Origin = origin });
        }

        internal static Origin WaveOrigin(Shockwave wave)
        {
            Launch launch;
            return wave != null && waves.TryGetValue(wave, out launch) ? launch.Origin : new Origin { Active = true };
        }

        internal static void Damaged(Unit target, PersistentID dealer, float amount)
        {
            if (target == null || !target.IsServer || !Positive(amount) || dealer.NotValid) return;
            Credit credit;
            if (Current.Active) credit = Current.Dealer == dealer ? Current.Credit : null;
            else
            {
                Unit unit;
                credit = UnitRegistry.TryGetUnit(dealer, out unit) ? Commander(unit) : null;
            }
            if (!Valid(credit) || credit.Dealer != dealer || target.NetworkHQ == null ||
                target.NetworkHQ == credit.Faction) return;
            Ledger ledger = ledgers.GetValue(target, _ => new Ledger());
            if (ledger.Reported) return;
            foreach (Contribution entry in ledger.Entries)
                if (entry.Credit.Player == credit.Player && entry.Credit.Dealer == dealer &&
                    entry.Credit.Faction == credit.Faction && ReferenceEquals(entry.Credit.Mission, credit.Mission))
                { if (Positive(entry.Damage + amount)) entry.Damage += amount; return; }
            // Bound a long-lived victim's bookkeeping even across repeated transfers.
            if (ledger.Entries.Count < 64) ledger.Entries.Add(new Contribution { Credit = credit, Damage = amount });
        }

        internal static List<Payment> Prepare(Unit target)
        {
            Ledger ledger;
            if (target == null || !target.IsServer || !ledgers.TryGetValue(target, out ledger) || ledger.Reported) return null;
            ledger.Reported = true; // Native ReportKilled has no own duplicate reward guard.
            var native = DamageCredit?.GetValue(target) as Dictionary<PersistentID, float>;
            PersistentUnit victim;
            if (native == null || !UnitRegistry.TryGetPersistentUnit(target.persistentID, out victim) ||
                victim.GetHQ() == null) return null;
            float total = 0f;
            foreach (float damage in native.Values)
            { if (!Finite(damage) || damage < 0f) return null; total += damage; }
            if (!Positive(total)) return null;
            var payments = new List<Payment>();
            foreach (Contribution entry in ledger.Entries)
            {
                Credit credit = entry.Credit;
                PersistentUnit dealer;
                float credited;
                if (!Valid(credit) || !native.TryGetValue(credit.Dealer, out credited) || credited / total < .01f ||
                    !UnitRegistry.TryGetPersistentUnit(credit.Dealer, out dealer) || dealer.player != null ||
                    !Plugin.IsResolute(dealer.definition) || dealer.GetHQ() != credit.Faction ||
                    victim.GetHQ() == credit.Faction) continue;
                // Threshold belongs to the native dealer's combined share. Split
                // that eligible share between its actual commanders by damage.
                float fraction = Math.Min(entry.Damage, credited) / total;
                if (Positive(fraction)) payments.Add(new Payment { Credit = credit, Fraction = fraction });
            }
            return payments;
        }

        internal static void Pay(Unit target, List<Payment> payments)
        {
            if (target == null || !target.IsServer || payments == null) return;
            foreach (Payment payment in payments)
            {
                if (!Valid(payment.Credit)) continue;
                RewardScope previous = reward;
                try
                {
                    reward = new RewardScope { Credit = payment.Credit, Target = target };
                    // Let the game compute value, ammunition/cargo value and the
                    // reward multiplier; only this scoped reward is wallet-only.
                    payment.Credit.Faction.ReportKillAction(payment.Credit.Player, target, payment.Fraction / 3f);
                }
                finally { reward = previous; }
            }
        }

        internal static bool NativeReward(FactionHQ faction, Player player, Unit target, float allocation)
        {
            if (reward == null || reward.Credit.Faction != faction || reward.Credit.Player != player || reward.Target != target)
                return true;
            float net = allocation * (1f - faction.playerTaxRate);
            if (Valid(reward.Credit) && Positive(net) && Finite(player.Allocation + net)) player.AddAllocation(net);
            return false; // No extra player score, sortie score, kill message or faction credit.
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Positive(float value) => Finite(value) && value > 0f;
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.RegisterMissile))]
    internal static class ResoluteCommandIncomeLaunch
    { private static void Postfix(Unit __instance, Missile missile) => ResoluteCommandIncome.Registered(__instance, missile); }

    [HarmonyPatch]
    internal static class ResoluteCommandIncomeMissileDamage
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Missile), "DetectCollisions");
            yield return AccessTools.Method(typeof(Missile), nameof(Missile.RpcDetonate));
        }
        private static void Prefix(Missile __instance, out ResoluteCommandIncome.Origin __state)
        { __state = ResoluteCommandIncome.Current; ResoluteCommandIncome.Current = ResoluteCommandIncome.MissileOrigin(__instance); }
        private static Exception Finalizer(Exception __exception, ResoluteCommandIncome.Origin __state)
        { ResoluteCommandIncome.Current = __state; return __exception; }
    }
    [HarmonyPatch(typeof(DamageEffects), nameof(DamageEffects.BlastFrag))]
    internal static class ResoluteCommandIncomeBlast
    {
        private static void Prefix(PersistentID missileID, out ResoluteCommandIncome.Origin __state)
        { __state = ResoluteCommandIncome.Current; ResoluteCommandIncome.Current = ResoluteCommandIncome.BlastOrigin(missileID); }
        private static Exception Finalizer(Exception __exception, ResoluteCommandIncome.Origin __state)
        { ResoluteCommandIncome.Current = __state; return __exception; }
    }
    [HarmonyPatch(typeof(Shockwave), nameof(Shockwave.SetOwner))]
    internal static class ResoluteCommandIncomeWaveCreated
    { private static void Postfix(Shockwave __instance, PersistentID ownerID) => ResoluteCommandIncome.WaveCreated(__instance, ownerID); }

    [HarmonyPatch(typeof(Shockwave), "Update")]
    internal static class ResoluteCommandIncomeWaveDamage
    {
        private static void Prefix(Shockwave __instance, out ResoluteCommandIncome.Origin __state)
        { __state = ResoluteCommandIncome.Current; ResoluteCommandIncome.Current = ResoluteCommandIncome.WaveOrigin(__instance); }
        private static Exception Finalizer(Exception __exception, ResoluteCommandIncome.Origin __state)
        { ResoluteCommandIncome.Current = __state; return __exception; }
    }
    [HarmonyPatch(typeof(Unit), nameof(Unit.RecordDamage))]
    internal static class ResoluteCommandIncomeDamage
    { private static void Postfix(Unit __instance, PersistentID lastDamagedBy, float damageAmount) => ResoluteCommandIncome.Damaged(__instance, lastDamagedBy, damageAmount); }

    [HarmonyPatch(typeof(Unit), nameof(Unit.ReportKilled))]
    internal static class ResoluteCommandIncomeKilled
    {
        private static void Prefix(Unit __instance, out List<ResoluteCommandIncome.Payment> __state) => __state = ResoluteCommandIncome.Prepare(__instance);
        private static void Postfix(Unit __instance, bool __runOriginal, List<ResoluteCommandIncome.Payment> __state)
        { if (__runOriginal) ResoluteCommandIncome.Pay(__instance, __state); }
    }
    [HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.RewardPlayer))]
    internal static class ResoluteCommandIncomeReward
    { private static bool Prefix(FactionHQ __instance, Player player, Unit target, float rewardAllocation) => ResoluteCommandIncome.NativeReward(__instance, player, target, rewardAllocation); }

    [HarmonyPatch(typeof(UnitRegistry), nameof(UnitRegistry.Clear))]
    internal static class ResoluteCommandIncomeReset
    { private static void Postfix() => ResoluteCommandIncome.Clear(); }
}
