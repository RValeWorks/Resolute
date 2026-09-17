using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class ResoluteCommandMounts
    {
        private static readonly AccessTools.FieldRef<Turret, FiringCone[]> Cones = AccessTools.FieldRefAccess<Turret, FiringCone[]>("firingCones");
        private static readonly AccessTools.FieldRef<MissileLauncher, Transform[]> Cells = AccessTools.FieldRefAccess<MissileLauncher, Transform[]>("launchTransforms");
        private static readonly AccessTools.FieldRef<MissileLauncher, int> Cell = AccessTools.FieldRefAccess<MissileLauncher, int>("currentCell");
        private static readonly AccessTools.FieldRef<MissileLauncher, Transform> Origin = AccessTools.FieldRefAccess<MissileLauncher, Transform>("launchTransform");

        internal static Transform LaunchOrigin(MissileLauncher launcher)
        {
            Transform[] cells = Cells(launcher);
            int cell = Cell(launcher);
            if (cells != null && cells.Length > 0)
                return cell >= 0 && cell < cells.Length ? cells[cell] : null;
            return Origin(launcher);
        }

        internal static bool CanServe(Weapon weapon, Unit target)
        {
            if (weapon is ResoluteVlsLauncher) return true;
            Turret turret = weapon.GetComponentInParent<Turret>();
            if (turret == null) return true;
            Unit owner = turret.GetAttachedUnit();
            GlobalPosition known;
            if (target == null || owner == null || owner.NetworkHQ == null ||
                !owner.NetworkHQ.TryGetKnownPosition(target, out known)) return false;
            Vector3 direction = known - turret.transform.GlobalPosition();
            if (direction.sqrMagnitude < .0001f || !Finite(direction)) return false;
            FiringCone[] cones = Cones(turret);
            return cones == null || FiringConeChecker.VectorWithinFiringCones(cones, direction, out _);
        }

        internal static bool ClearRay(Vector3 position, Vector3 direction, Unit target)
        {
            if (!Finite(position) || !Finite(direction) || direction.sqrMagnitude < .0001f) return false;
            float distance = target != null ? Mathf.Min(200f, (target.transform.position - position).magnitude) : 200f;
            if (!Physics.Raycast(position, direction, out RaycastHit hit, distance, ~(int)PhysicsLayers.ExclusionZonesMask)) return true;
            UnitPart part = hit.collider.GetComponentInParent<UnitPart>();
            Unit struck = part != null ? part.parentUnit : hit.collider.GetComponentInParent<Unit>();
            return target != null && struck == target;
        }

        internal static bool ClearCell(MissileLauncher launcher, Unit target)
        {
            Transform origin = LaunchOrigin(launcher);
            return origin != null && ClearRay(origin.position, origin.forward, target);
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
