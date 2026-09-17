using System;
using UnityEngine;

namespace Resolute
{
    // Scoped ship adapter for the native Unit.Jam RPC/event pathway. Native
    // JammingPod requires Aircraft.PowerSupply; QoL 1.1.8.1 removes that aircraft
    // assumption for ground emitters. No global JammingPod patch is needed here.
    internal sealed class ResoluteShipEcm : MonoBehaviour
    {
        // Defensive-only tuning: four channels and bounded registry work are
        // Resolute policy. The 25 km linear falloff follows QoL's EW vehicle
        // pod configuration, not its global constant-strength FixedUpdate patch.
        private const int Channels = 4, ScanBudget = 64;
        private const float Range = 25000f, ScanInterval = .5f, PulseInterval = .25f;
        [SerializeField] private Transform emitter;
        [SerializeField] private UnitPart support;
        private Ship ship;
        private readonly Missile[] targets = new Missile[Channels];
        private readonly float[] distances = new float[Channels];
        private float nextScan, nextPulse;
        private int cursor;
        internal int NativeJamCalls { get; private set; }
        internal int ActiveChannels { get; private set; }

        internal static void Configure(Ship ship, Transform authoredEmitter)
        {
            if (ship == null || authoredEmitter == null || !authoredEmitter.IsChildOf(ship.transform))
                throw new InvalidOperationException("Resolute ECM requires its fitted radar/mast support.");
            var component = ship.gameObject.AddComponent<ResoluteShipEcm>();
            // The native scan point was repositioned but can retain its old
            // parent. Use the authored mast after structural ownership instead.
            component.emitter = authoredEmitter;
            // Construction runs below an inactive template root.
            component.support = component.emitter.GetComponentInParent<UnitPart>(true);
            if (component.support == null)
                throw new InvalidOperationException("Resolute ECM emitter has no damageable supporting part.");
        }

        private void Awake() { ship = GetComponent<Ship>(); }

        private bool Operational()
        {
            return ship != null && ship.IsServer && ship.LocalSim && !ship.disabled &&
                ship.NetworkHQ != null && MissionManager.IsRunning && emitter != null &&
                support != null && support.hitPoints > 0f && !support.IsDetached() &&
                support.parentUnit == ship && emitter.gameObject.activeInHierarchy;
        }

        // The optional command emitter uses this same damageable mast. Reading
        // its availability does not change the automatic defensive channels.
        internal bool TryGetEmitter(out Transform point)
        { point = emitter; return Operational(); }

        private void Update()
        {
            if (!Operational()) { ActiveChannels = 0; Array.Clear(targets, 0, targets.Length); return; }
            float now = Time.timeSinceLevelLoad;
            if (now >= nextScan)
            {
                nextScan = now + ScanInterval;
                // Keep existing qualified channels while incrementally examining
                // a bounded slice of the native registry. Unknown tracks cannot jam.
                for (int i = 0; i < Channels; i++)
                {
                    float distance;
                    if (!Eligible(targets[i], out distance)) { targets[i] = null; distances[i] = float.PositiveInfinity; }
                    else distances[i] = distance;
                }
                int count = UnitRegistry.allUnits.Count;
                int limit = Math.Min(ScanBudget, count);
                for (int i = 0; i < limit; i++)
                {
                    if (cursor >= count) cursor = 0;
                    Missile candidate = UnitRegistry.allUnits[cursor++] as Missile;
                    float distance;
                    if (!Eligible(candidate, out distance) || Array.IndexOf(targets, candidate) >= 0) continue;
                    int worst = 0;
                    for (int slot = 1; slot < Channels; slot++) if (distances[slot] > distances[worst]) worst = slot;
                    if (distance < distances[worst]) { targets[worst] = candidate; distances[worst] = distance; }
                }
            }
            if (now < nextPulse) return;
            nextPulse = now + PulseInterval;
            ActiveChannels = 0;
            for (int i = 0; i < Channels; i++)
            {
                float distance;
                Missile target = targets[i];
                if (!Eligible(target, out distance)) { targets[i] = null; continue; }
                // Native static terrain/building occlusion. Ships do not acquire
                // a target through this check; the live HQ track gate comes first.
                if (Physics.Linecast(emitter.position, target.transform.position, PhysicsLayers.StaticsMask)) continue;
                float amount = Mathf.Clamp01(1f - distance / Range);
                if (amount <= 0f) continue;
                target.Jam(new Unit.JamEventArgs { jammingUnit = ship, jamAmount = amount });
                NativeJamCalls++; ActiveChannels++;
            }
        }

        private bool Eligible(Missile target, out float distance)
        {
            distance = float.PositiveInfinity;
            if (target == null || target.disabled || target.NetworkHQ == null || target.NetworkHQ == ship.NetworkHQ ||
                !ResoluteSupplyDefense.IsTargetOrObservedIncoming(target, ship) || !ship.NetworkHQ.IsTargetBeingTracked(target) ||
                !ship.NetworkHQ.IsTargetPositionAccurate(target, 500f)) return false;
            // Optical and IR guidance is not susceptible to this radio-frequency
            // countermeasure. Native ARH/SARH handle interference and home-on-jam.
            if (target.GetComponent<ARHSeeker>() == null && target.GetComponent<SARHSeeker>() == null) return false;
            distance = (target.GlobalPosition() - ship.GlobalPosition()).magnitude;
            return !float.IsNaN(distance) && !float.IsInfinity(distance) && distance >= 0f && distance < Range;
        }

        private void OnDisable() { ActiveChannels = 0; Array.Clear(targets, 0, targets.Length); }
    }
}
