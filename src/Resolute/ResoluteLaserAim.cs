using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // The native turret aims at the unit origin, but Laser selects an actual
    // part and has only one degree of optical deflection. On a large/nearby
    // ground target these can be different enough to inhibit the beam.
    internal sealed class ResoluteLaserAim : MonoBehaviour
    {
        public Laser Laser;
        private AimSolver solver;
        private static readonly ConditionalWeakTable<AimSolver, ResoluteLaserAim> Bindings =
            new ConditionalWeakTable<AimSolver, ResoluteLaserAim>();
        private static readonly AccessTools.FieldRef<Laser, Transform> SelectedPart =
            AccessTools.FieldRefAccess<Laser, Transform>("currentTargetTransform");
        private static readonly AccessTools.FieldRef<Weapon, Unit> SelectedTarget =
            AccessTools.FieldRefAccess<Weapon, Unit>("currentTarget");
        private static readonly AccessTools.FieldRef<Turret, AimSolver> NativeSolver =
            AccessTools.FieldRefAccess<Turret, AimSolver>("aimSolver");

        internal static void Configure(Laser laser)
        {
            ResoluteLaserAim binding = laser.GetComponent<ResoluteLaserAim>();
            if (binding == null) binding = laser.gameObject.AddComponent<ResoluteLaserAim>();
            binding.Laser = laser;
        }

        internal void Bind(Turret turret)
        {
            AimSolver next = NativeSolver(turret);
            if (solver == next) return;
            if (solver != null) Bindings.Remove(solver);
            solver = next;
            if (solver != null) { Bindings.Remove(solver); Bindings.Add(solver, this); }
        }

        internal static bool TryAim(AimSolver source, out Vector3 vector, out float range)
        {
            vector = Vector3.zero; range = 0f;
            ResoluteLaserAim binding;
            if (!Bindings.TryGetValue(source, out binding) || binding == null || binding.Laser == null) return false;
            Laser laser = binding.Laser;
            Unit owner = laser.attachedUnit;
            Unit target = SelectedTarget(laser);
            if (owner == null || owner.disabled || !Plugin.IsResolute(owner.definition) || target == null || target.disabled) return false;
            Transform part = SelectedPart(laser);
            // Match Laser.FixedUpdate's point and native stale-track fallback.
            // No extra sensor query, part lottery, target lead or ballistic arc.
            Vector3 point = part != null ? part.position : laser.transform.position + laser.transform.forward * 20000f;
            GlobalPosition known;
            if (owner.NetworkHQ != null && !owner.NetworkHQ.IsTargetBeingTracked(target) &&
                owner.NetworkHQ.TryGetKnownPosition(target, out known)) point = known.ToLocalPosition();
            vector = point - laser.transform.position;
            range = vector.magnitude;
            return true;
        }

        private void OnDestroy()
        {
            if (solver != null) Bindings.Remove(solver);
        }
    }

    [HarmonyPatch(typeof(Turret), "SetTarget")]
    internal static class ResoluteLaserAimTarget
    {
        private static void Postfix(Turret __instance)
        {
            ResoluteLaserAim binding = __instance.GetComponentInChildren<ResoluteLaserAim>(true);
            if (binding != null) binding.Bind(__instance);
        }
    }

    [HarmonyPatch(typeof(AimSolver), "GetAimVector")]
    internal static class ResoluteLaserAimVector
    {
        private static bool Prefix(AimSolver __instance, ref Vector3 __result, ref float targetRange)
        {
            Vector3 vector; float range;
            if (!ResoluteLaserAim.TryAim(__instance, out vector, out range)) return true;
            __result = vector; targetRange = range;
            return false;
        }
    }
}
