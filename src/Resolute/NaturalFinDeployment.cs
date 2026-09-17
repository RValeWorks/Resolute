using System;
using UnityEngine;

namespace Resolute
{
    // The saved art has a deployed launch pose as well as the folded launch
    // and booster-free flight poses. Switching only the launch mesh opens the
    // fins without removing its booster or touching native aerodynamic timing.
    internal static class NaturalFinDeployment
    {
        internal static void Configure(NaturalWeaponPhase phase, string key, Transform visualBag)
        {
            if (key != "rsl_ashm" && key != "rsl_cruise" && key != "rsl_lrsam" && key != "rsl_mrsam" &&
                key != "rsl_bastion" && key != "rsl_bmd" && key != "rsl_bmd_exo" && key != "rsl_pd") return;
            Transform node = visualBag.Find("LOD0/" + key + "_cleared");
            MeshFilter opened = node != null ? node.GetComponent<MeshFilter>() : null;
            phase.LaunchMesh = phase.LaunchModel.GetComponent<MeshFilter>();
            if (opened == null || opened.sharedMesh == null || phase.LaunchMesh == null || phase.LaunchMesh.sharedMesh == null)
                throw new InvalidOperationException("Missing deployed launch-fin geometry: " + key);
            phase.ClearedLaunchMesh = opened.sharedMesh;
        }

        internal static bool TryDeploy(Missile missile, MeshFilter launch, Mesh opened)
        {
            if (missile == null || missile.disabled || launch == null || opened == null || missile.timeSinceSpawn < .05f) return false;
            Bounds bounds = launch.sharedMesh.bounds;
            Transform body = missile.transform;
            Vector3 launchAxis = missile.startRotation * Vector3.forward;
            // Use the native global launch point: floating-origin shifts do
            // not count as movement out of the tube. The complete stored
            // envelope must pass the launch plane, including during a turn.
            Vector3 travel = missile.GlobalPosition() - missile.startPosition;
            if (!ClearedPlane(travel, launchAxis, body.right, body.up, body.forward, bounds)) return false;
            launch.sharedMesh = opened;
            return true;
        }

        internal static bool ClearedPlane(Vector3 travel, Vector3 axis, Vector3 right, Vector3 up, Vector3 forward, Bounds bounds)
        {
            float x = Vector3.Dot(right, axis), y = Vector3.Dot(up, axis), z = Vector3.Dot(forward, axis);
            float centre = Vector3.Dot(travel, axis) + bounds.center.x * x + bounds.center.y * y + bounds.center.z * z;
            float radius = bounds.extents.x * Mathf.Abs(x) + bounds.extents.y * Mathf.Abs(y) + bounds.extents.z * Mathf.Abs(z);
            return centre - radius >= .25f;
        }
    }
}
