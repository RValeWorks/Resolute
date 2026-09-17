using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Resolute
{
    // Native launch, station accounting and replication remain in MissileLauncher.
    // The additional state describes which physical cells still contain a round.
    internal sealed class ResoluteVlsLauncher : MissileLauncher
    {
        public ShipPart[] CellOwners;
        public Transform[] PhysicalCells;
        private bool[] occupied, ruined;
        private bool supplyReloading;
        private float supplyReloadStarted;
        internal const float SupplyLoadSeconds = 15f;
        private static readonly FieldInfo Cell = AccessTools.Field(typeof(MissileLauncher), "currentCell");
        private static readonly FieldInfo Loading = AccessTools.Field(typeof(MissileLauncher), "reloadingAmmo");
        private static readonly MethodInfo EnableNative = AccessTools.Method(typeof(MissileLauncher), "OnEnable");

        private void OnEnable() { EnableNative.Invoke(this, null); }

        internal int CurrentCell => (int)Cell.GetValue(this);
        internal int RuinedCells { get { Reconcile(); return ruined.Count(v => v); } }
        internal bool IsOccupied(int index) { Reconcile(); return occupied[index]; }
        internal bool IsRuined(int index) { Reconcile(); return ruined[index]; }

        private void Reconcile()
        {
            if (PhysicalCells == null || PhysicalCells.Length == 0) return;
            if (occupied == null)
            {
                occupied = new bool[PhysicalCells.Length];
                ruined = new bool[PhysicalCells.Length];
            }
            int target = Mathf.Clamp(ammo, 0, ruined.Count(v => !v));
            int existing = occupied.Count(v => v);
            int firstCell = CurrentCell;
            // Native reloads increase ammo asynchronously. Seat those rounds in
            // intact empty cells in the same circular order as native launches.
            for (int step = 0; step < occupied.Length && existing != target; step++)
            {
                int i = (firstCell + step) % occupied.Length;
                if (existing < target && !occupied[i] && !ruined[i]) { occupied[i] = true; existing++; }
                else if (existing > target && occupied[i]) { occupied[i] = false; existing--; }
            }
            ammo = existing;
        }

        public override void Fire(Unit owner, Unit target, Vector3 inheritedVelocity, WeaponStation station, GlobalPosition aimpoint)
        {
            Reconcile();
            if (occupied == null || ammo <= 0) return;
            int selected = CurrentCell;
            for (int step = 0; step < occupied.Length; step++)
            {
                int i = (selected + step) % occupied.Length;
                if (!occupied[i]) continue;
                selected = i; break;
            }
            Cell.SetValue(this, selected);
            int before = ammo;
            base.Fire(owner, target, inheritedVelocity, station, aimpoint);
            if (ammo < before)
            {
                occupied[selected] = false;
                GetComponent<ResoluteVlsAnimation>()?.AccountFiredCell(selected);
            }
        }

        public override void Rearm(int count, WeaponStation station)
        {
            Reconcile();
            int available = ruined == null ? 0 : ruined.Count(v => !v) - ammo - (int)Loading.GetValue(this);
            count = Mathf.Clamp(count, 0, Mathf.Max(available, 0));
            if (count <= 0) return;
            weaponStation = station;
            Loading.SetValue(this, (int)Loading.GetValue(this) + count);
            if (!supplyReloading) LoadDeliveredRounds().Forget();
        }

        // Native MissileLauncher.Reload waits until lastFired + reloadTime.
        // A busy VLS can therefore leave delivered rounds queued forever. The
        // ship loads intact empty cells on a bounded delivery clock instead.
        private async UniTask LoadDeliveredRounds()
        {
            supplyReloading = true;
            supplyReloadStarted = Time.timeSinceLevelLoad;
            var cancel = destroyCancellationToken;
            try
            {
                while (Time.timeSinceLevelLoad - supplyReloadStarted < SupplyLoadSeconds)
                {
                    weaponStation?.Updated();
                    await UniTask.WaitForSeconds(1);
                    if (cancel.IsCancellationRequested) return;
                }
                Reconcile();
                int available = ruined == null ? 0 : ruined.Count(v => !v) - ammo;
                ammo += Mathf.Clamp((int)Loading.GetValue(this), 0, Mathf.Max(0, available));
                Loading.SetValue(this, 0);
                Reconcile();
                weaponStation?.AccountAmmo(); weaponStation?.Updated();
                ReportReloading(reloading: false);
            }
            finally { supplyReloading = false; }
        }

        public override float GetReloadProgress() => supplyReloading
            ? Mathf.Clamp01((Time.timeSinceLevelLoad - supplyReloadStarted) / SupplyLoadSeconds) : base.GetReloadProgress();

        // A destroyed cell cannot be replenished. Native supply uses this
        // capacity to decide when the magazine is full, including queued loads.
        public override int GetFullAmmo() => ruined == null ? base.GetFullAmmo() :
            Math.Min(base.GetFullAmmo(), ruined.Count(v => !v));

        internal int DestroyCompartment(ShipPart owner, out Vector3 positionSum)
        {
            Reconcile(); positionSum = Vector3.zero;
            int consumed = 0;
            for (int i = 0; i < CellOwners.Length; i++)
            {
                if (CellOwners[i] != owner || ruined[i]) continue;
                ruined[i] = true;
                if (!occupied[i]) continue;
                occupied[i] = false; consumed++;
                positionSum += PhysicalCells[i] != null ? PhysicalCells[i].position : owner.transform.position;
            }
            ammo = occupied.Count(v => v);
            Loading.SetValue(this, Mathf.Min((int)Loading.GetValue(this), ruined.Count(v => !v) - ammo));
            GetComponent<ResoluteVlsAnimation>()?.AccountDestroyedAmmo();
            weaponStation?.AccountAmmo(); weaponStation?.Updated();
            if (attachedUnit != null && attachedUnit.IsServer && weaponStation != null)
                attachedUnit.RpcSyncAmmoCount(weaponStation.Number, weaponStation.Ammo);
            return consumed;
        }

        internal void LoseStructuralSupport(int index)
        {
            ShipPart owner = CellOwners[index];
            // Structural damage callbacks precede the ordinary cookoff callback.
            // Commit that same compartment event first so support loss cannot
            // remove rounds from its exactly-once conventional accounting.
            ResoluteMagazineCookoff magazine = owner != null ? owner.GetComponent<ResoluteMagazineCookoff>() : null;
            if (owner != null && owner.hitPoints <= 0f && magazine != null) magazine.DestroyedSupport();
            Reconcile();
            if (ruined[index]) return;
            ruined[index] = true; occupied[index] = false;
            ammo = occupied.Count(v => v);
            Loading.SetValue(this, Mathf.Max(0, Mathf.Min((int)Loading.GetValue(this), ruined.Count(v => !v) - ammo)));
            GetComponent<ResoluteVlsAnimation>()?.AccountDestroyedAmmo();
            weaponStation?.AccountAmmo(); weaponStation?.Updated();
            if (attachedUnit != null && attachedUnit.IsServer && weaponStation != null)
                attachedUnit.RpcSyncAmmoCount(weaponStation.Number, weaponStation.Ammo);
        }
    }

    // One native physical compartment owns one exactly-once secondary event.
    // This is a Resolute feature: vanilla Dynamo's fires are not ammo-aware.
    internal sealed class ResoluteMagazineCookoff : MonoBehaviour
    {
        public Ship Ship;
        public ShipPart Part;
        public ResoluteVlsLauncher[] Launchers;
        public Missile ConventionalEffectTemplate;
        public Missile SmallConventionalEffectTemplate;
        internal bool Triggered { get; private set; }
        internal int ConsumedRounds { get; private set; }
        internal float ConventionalYieldKg { get; private set; }
        internal int ScheduledBursts { get; private set; }
        internal int EmittedBursts { get; private set; }
        internal int ServerDamageBatches { get; private set; }
        internal PersistentID DamageSource;

        internal static void Configure(Ship ship)
        {
            var launchers = ship.GetComponentsInChildren<ResoluteVlsLauncher>(true);
            MissileDefinition[] nativeDefinitions = Resources.FindObjectsOfTypeAll<MissileDefinition>();
            MissileDefinition donor = nativeDefinitions.First(d => d.jsonKey == "AShM1" && d.unitPrefab != null);
            Missile template = donor.unitPrefab.GetComponent<Missile>();
            Missile smallTemplate = nativeDefinitions.First(d => d.jsonKey == "AAM1" && d.unitPrefab != null).unitPrefab.GetComponent<Missile>();
            foreach (ResoluteVlsLauncher launcher in launchers)
            {
                ResoluteVlsAnimation animation = launcher.GetComponent<ResoluteVlsAnimation>();
                launcher.PhysicalCells = animation.Hatches.ToArray();
                // Prefabs are assembled below an inactive staging root. Include
                // inactive ancestors when resolving their physical compartment.
                launcher.CellOwners = animation.Hatches.Select(h => h.GetComponentInParent<ShipPart>(true)).ToArray();
                for (int i = 0; i < launcher.CellOwners.Length; i++)
                {
                    ShipPart owner = launcher.CellOwners[i];
                    if (owner == null || owner.parentUnit != ship)
                        throw new InvalidOperationException("A VLS cell lacks its physical Resolute compartment: launcher=" + launcher.name +
                            ", index=" + i + ", hatch=" + animation.Hatches[i].name + ", hatchParent=" + animation.Hatches[i].parent?.name +
                            ", owner=" + (owner != null ? owner.name : "null") + ", ownerUnit=" + (owner != null && owner.parentUnit != null ? owner.parentUnit.name : "null") +
                            ", expectedShip=" + ship.name + ".");
                }
            }
            foreach (ShipPart owner in launchers.SelectMany(l => l.CellOwners).Distinct())
            {
                var cookoff = owner.gameObject.AddComponent<ResoluteMagazineCookoff>();
                cookoff.Ship = ship; cookoff.Part = owner; cookoff.ConventionalEffectTemplate = template;
                cookoff.SmallConventionalEffectTemplate = smallTemplate;
                cookoff.Launchers = launchers.Where(l => l.CellOwners.Contains(owner)).ToArray();
            }
        }

        private void Awake()
        {
            if (Part == null) return;
            Part.onApplyDamage += Damaged;
            Part.onPartDetached += Detached;
            Part.onParentDetached += Detached;
            Part.onJointBroken += Detached;
        }
        private void OnDestroy()
        {
            if (Part == null) return;
            Part.onApplyDamage -= Damaged;
            Part.onPartDetached -= Detached;
            Part.onParentDetached -= Detached;
            Part.onJointBroken -= Detached;
        }

        private void Damaged(UnitPart.OnApplyDamage damage)
        {
            if (Triggered || damage.hitPoints > 0f || Ship == null) return;
            LoseCompartment(true);
        }

        private void Detached(UnitPart ignored)
        {
            // A surviving piece which separates from the ship loses its usable
            // magazine. Separation alone does not ignite otherwise intact rounds.
            if (!Triggered && Ship != null) LoseCompartment(Part.hitPoints <= 0f);
        }

        internal void DestroyedSupport()
        {
            if (!Triggered && Ship != null) LoseCompartment(true);
        }

        private void LoseCompartment(bool detonate)
        {
            Triggered = true;
            Vector3 sum = Vector3.zero;
            foreach (ResoluteVlsLauncher launcher in Launchers)
            {
                if (launcher == null) continue;
                Vector3 positions;
                int count = launcher.DestroyCompartment(Part, out positions);
                ConsumedRounds += count; sum += positions;
                // Conventional donor effects only. Cap each round and the whole
                // compartment so strategic weapon yields can never enter this path.
                float perRound = launcher.info != null ? launcher.info.blastDamage : 0f;
                ConventionalYieldKg += count * Mathf.Clamp(perRound, 0f, 500f);
            }
            ConventionalYieldKg = Mathf.Min(ConventionalYieldKg, 1500f);
            if (!detonate || ConsumedRounds == 0 || ConventionalYieldKg <= 0f) return;
            ScheduledBursts++;
            Vector3 center = sum / ConsumedRounds;
            var burst = new GameObject("ResoluteConventionalMagazineCookoff").AddComponent<ResoluteCookoffBurst>();
            Missile effect = ConventionalYieldKg > 200f ? ConventionalEffectTemplate : SmallConventionalEffectTemplate;
            burst.Prepare(this, effect, Part.transform, center, ConventionalYieldKg, DamageSource, Ship.IsServer);
        }

        internal void Emitted(bool server)
        {
            EmittedBursts++;
            if (server) ServerDamageBatches++;
        }
    }

    internal sealed class ResoluteCookoffBurst : MonoBehaviour
    {
        private ResoluteMagazineCookoff source;
        private Missile template;
        private Transform compartment;
        private Vector3 localPoint;
        private GlobalPosition fallback;
        private float yieldKg;
        private PersistentID dealer;
        private bool server;
        private static readonly string[] EffectFields = { "airEffect", "armorEffect", "terrainEffect", "waterSurfaceEffect", "underwaterEffect", "fizzleEffect" };

        internal void Prepare(ResoluteMagazineCookoff owner, Missile effect, Transform part, Vector3 point, float yield, PersistentID attacker, bool isServer)
        {
            source = owner; template = effect; compartment = part; localPoint = part.InverseTransformPoint(point);
            fallback = point.ToGlobalPosition(); yieldKg = yield; dealer = attacker; server = isServer;
            StartCoroutine(Detonate());
        }

        private IEnumerator Detonate()
        {
            // Leave the native damage callback before starting secondary damage;
            // the consumed-cell state is already committed on every observer.
            yield return new WaitForFixedUpdate();
            Vector3 position = compartment != null ? compartment.TransformPoint(localPoint) : fallback.ToLocalPosition();
            var prototype = NaturalArmament.Get<Missile.Warhead>(template, "warhead");
            var warhead = new Missile.Warhead();
            foreach (string field in EffectFields)
                NaturalArmament.Set(warhead, field, NaturalArmament.Get<GameObject>(prototype, field));
            warhead.Detonate(null, dealer, position, Vector3.up, true, yieldKg, true, false);
            // Large native effects carry their own server-gated Shockwave.
            // The native small-warhead branch performs BlastFrag separately.
            if (yieldKg <= 200f && server) DamageEffects.BlastFrag(yieldKg, position, dealer, PersistentID.None);
            source?.Emitted(server);
            Destroy(gameObject);
        }
    }

    [HarmonyPatch(typeof(UnitPart), nameof(UnitPart.TakeDamage))]
    internal static class ResoluteMagazineDamageAttribution
    {
        private static void Prefix(UnitPart __instance, PersistentID dealerID)
        {
            if (__instance.TryGetComponent<ResoluteMagazineCookoff>(out var magazine) && !magazine.Triggered)
                magazine.DamageSource = dealerID;
        }
    }
}
