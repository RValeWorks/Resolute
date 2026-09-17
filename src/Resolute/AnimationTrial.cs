using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Resolute
{
    // A copied-game observer of actual native shots. No ammo, cell index, hatch
    // transform or animation method is changed by this diagnostic.
    internal static class AnimationTrial
    {
        private static readonly FieldInfo Cell = typeof(MissileLauncher).GetField("currentCell", BindingFlags.Instance | BindingFlags.NonPublic);

        internal sealed class Probe
        {
            internal MissileLauncher Launcher;
            internal ResoluteVlsAnimation Animation;
            internal ResoluteAnimationObserver Observer;
            internal Quaternion[] Rest, Open;
            internal bool[] FiredHatches;
            internal int AmmoBefore, CellBefore, AmmoConsumed, NextCell;
            internal float FiredAt, LastSample, MaxFrameGap, CloseDuration;
            internal float MaxOpenError, MaxUnrelatedDrift, MaxFinalRestError;
            internal float LastFullOpenAge, FirstClosingAge = -1f, FirstClosedAge = -1f;
            internal int Samples, ClosingSamples;
            internal bool Fired, Complete, OpenedImmediately, HeldOpen;
            internal string Failure;

            internal void Sample()
            {
                if (Complete) return;
                if (Launcher == null || Animation == null)
                {
                    Failure = "Launcher or animation disappeared while observing the shot.";
                    Complete = true;
                    return;
                }
                float time = Time.time;
                if (!Fired)
                {
                    if (Launcher.ammo >= AmmoBefore) return;
                    Fired = true;
                    FiredAt = LastSample = time;
                    AmmoConsumed = AmmoBefore - Launcher.ammo;
                    NextCell = (int)Cell.GetValue(Launcher);
                    for (int shot = 0; shot < Mathf.Min(AmmoConsumed, FiredHatches.Length); shot++)
                    {
                        int index = (CellBefore + shot) % FiredHatches.Length;
                        FiredHatches[index] = true;
                        CloseDuration = Mathf.Max(CloseDuration, Mathf.Max(.1f, Animation.OpenSeconds[index]));
                    }
                }
                MaxFrameGap = Mathf.Max(MaxFrameGap, time - LastSample);
                LastSample = time;
                float age = time - FiredAt;
                float openError = 0f, restError = 0f;
                bool intermediate = false;
                for (int i = 0; i < Animation.Hatches.Length; i++)
                {
                    Transform hatch = Animation.Hatches[i];
                    if (hatch == null)
                    {
                        Failure = "An observed hatch disappeared.";
                        Complete = true;
                        return;
                    }
                    float rest = Quaternion.Angle(Rest[i], hatch.localRotation);
                    if (!FiredHatches[i])
                    {
                        MaxUnrelatedDrift = Mathf.Max(MaxUnrelatedDrift, rest);
                        continue;
                    }
                    float open = Quaternion.Angle(Open[i], hatch.localRotation);
                    openError = Mathf.Max(openError, open);
                    restError = Mathf.Max(restError, rest);
                    intermediate |= age > 1.5f && open > 1f && rest > 1f;
                }
                if (Samples == 0) OpenedImmediately = openError < .15f && restError > 1f;
                if (age <= 1.45f) MaxOpenError = Mathf.Max(MaxOpenError, openError);
                if (age >= 1f && age <= 1.45f && openError < .15f) HeldOpen = true;
                if (openError < .15f) LastFullOpenAge = age;
                if (intermediate)
                {
                    ClosingSamples++;
                    if (FirstClosingAge < 0f) FirstClosingAge = age;
                }
                if (age > 1.5f && restError < .15f && FirstClosedAge < 0f) FirstClosedAge = age;
                Samples++;
                if (age >= 1.5f + CloseDuration + .15f)
                {
                    MaxFinalRestError = restError;
                    Complete = true;
                }
            }
        }

        internal static Probe Begin(MissileLauncher launcher)
        {
            ResoluteVlsAnimation animation = launcher != null ? launcher.GetComponent<ResoluteVlsAnimation>() : null;
            if (animation == null) return null;
            if (Path.GetFileName(Directory.GetParent(Application.dataPath).FullName) != "test-game")
                throw new InvalidOperationException("Animation trial requires the isolated test-game copy.");
            var probe = new Probe
            {
                Launcher = launcher, Animation = animation,
                AmmoBefore = launcher.ammo, CellBefore = (int)Cell.GetValue(launcher),
                Rest = animation.Hatches.Select(h => h.localRotation).ToArray(),
                FiredHatches = new bool[animation.Hatches.Length]
            };
            probe.Open = probe.Rest.Select((rest, i) => rest * Quaternion.Euler(animation.OpenEuler[i])).ToArray();
            probe.Observer = launcher.gameObject.AddComponent<ResoluteAnimationObserver>();
            probe.Observer.Probe = probe;
            return probe;
        }

        internal static IEnumerator Observe(IEnumerable<Probe> pending, Dictionary<string, object> report, List<object> checks, string output)
        {
            Probe[] probes = pending.Where(p => p != null).ToArray();
            float deadline = Time.realtimeSinceStartup + 8f;
            while (probes.Any(p => !p.Complete) && Time.realtimeSinceStartup < deadline) yield return null;
            var banks = new List<object>();
            report["vlsAnimations"] = new Dictionary<string, object>
            {
                ["scope"] = "Observers run after production LateUpdate for every VLS bank fired by the native WeaponStation loop. They inspect real hatch rotations through opening, the 1.5 second hold and the authored close; no synthetic ammo/cell changes or direct animation calls. Full magazine wrap and remote synchronization are outside this test.",
                ["banks"] = banks, ["observedBanks"] = probes.Length,
                ["representedPhysicalHatches"] = probes.Sum(p => p.Rest.Length),
                ["actuallyFiredHatches"] = probes.Sum(p => p.AmmoConsumed)
            };
            bool allPassed = probes.Length > 0;
            foreach (Probe probe in probes)
            {
                string name = probe.Launcher != null ? probe.Launcher.name : "destroyed";
                bool cellAdvanced = probe.AmmoConsumed > 0 && probe.NextCell == (probe.CellBefore + probe.AmmoConsumed) % probe.Rest.Length;
                bool timing = probe.HeldOpen && probe.MaxOpenError < .15f && probe.ClosingSamples > 0 &&
                    probe.FirstClosingAge >= 1.5f && probe.FirstClosedAge >= 1.5f + probe.CloseDuration - .08f &&
                    probe.FirstClosedAge <= 1.5f + probe.CloseDuration + probe.MaxFrameGap + .05f;
                bool passed = probe.Failure == null && probe.Complete && probe.Fired && cellAdvanced && probe.OpenedImmediately &&
                    timing && probe.MaxUnrelatedDrift < .15f && probe.MaxFinalRestError < .15f;
                banks.Add(new Dictionary<string, object>
                {
                    ["launcher"] = name, ["hatches"] = probe.Rest.Length, ["ammoBefore"] = probe.AmmoBefore,
                    ["consumedAmmo"] = probe.AmmoConsumed, ["nativeCellBefore"] = probe.CellBefore, ["nativeCellAfter"] = probe.NextCell,
                    ["openedImmediately"] = probe.OpenedImmediately, ["heldOpen"] = probe.HeldOpen,
                    ["authoredCloseSeconds"] = probe.CloseDuration, ["lastFullOpenAge"] = probe.LastFullOpenAge,
                    ["firstClosingAge"] = probe.FirstClosingAge, ["firstClosedAge"] = probe.FirstClosedAge,
                    ["samples"] = probe.Samples, ["intermediateClosingSamples"] = probe.ClosingSamples, ["maxFrameGapSeconds"] = probe.MaxFrameGap,
                    ["maxHoldErrorDegrees"] = probe.MaxOpenError, ["maxUnfiredHatchDriftDegrees"] = probe.MaxUnrelatedDrift,
                    ["finalRestErrorDegrees"] = probe.MaxFinalRestError, ["complete"] = probe.Complete,
                    ["error"] = probe.Failure, ["passed"] = passed
                });
                checks.Add(new Dictionary<string, object> { ["name"] = "vls-native-hatch-animation-" + name, ["passed"] = passed });
                allPassed &= passed;
                if (probe.Observer != null) UnityEngine.Object.Destroy(probe.Observer);
            }
            checks.Add(new Dictionary<string, object> { ["name"] = "vls-all-observed-bank-animations", ["passed"] = allPassed });
            File.WriteAllText(output, Audit.Json(report));
            if (!allPassed) throw new InvalidOperationException("One or more native VLS hatch animation observations failed.");
        }
    }

    [DefaultExecutionOrder(10000)]
    internal sealed class ResoluteAnimationObserver : MonoBehaviour
    {
        internal AnimationTrial.Probe Probe;
        private void LateUpdate()
        {
            if (Probe == null) return;
            Probe.Sample();
            if (Probe.Complete) enabled = false;
        }
    }
}
