using UnityEngine;

namespace Resolute
{
    internal static class NaturalSurfaceRadarVisibility
    {
        internal static bool CanSee(Transform source, Unit target)
        {
            if (source == null || target == null || target.disabled) return false;
            if (!(target is Building)) return TargetCalc.LineOfSight(source, target.transform, 10f);

            Vector3 origin = source.position;
            if (!Physics.Linecast(origin, target.transform.position, out RaycastHit obstruction,
                PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore)) return true;
            if (BelongsTo(obstruction.collider, target)) return true;

            // A building origin may lie inside a deep foundation. Look for an
            // exposed piece of its actual collider, rather than treating the
            // foundation ray or the building's own wall as intervening ground.
            foreach (Collider surface in target.GetComponentsInChildren<Collider>())
            {
                if (!Usable(surface, target)) continue;
                Bounds bounds = surface.bounds;
                for (int sample = 0; sample < 5; sample++)
                {
                    Vector3 point = bounds.center;
                    if (sample > 0)
                    {
                        point.y += bounds.extents.y * .8f;
                        point.x += bounds.extents.x * (sample % 2 == 0 ? .5f : -.5f);
                        point.z += bounds.extents.z * (sample < 3 ? -.5f : .5f);
                    }
                    Vector3 direction = point - origin;
                    float distance = direction.magnitude;
                    if (distance < .01f) continue;
                    direction /= distance;
                    if (!surface.Raycast(new Ray(origin, direction), out RaycastHit ownHit, distance + bounds.size.magnitude)) continue;
                    // Test the entire path to the genuine surface. A terrain
                    // ridge or another installation must remain an occluder.
                    if (!Physics.Raycast(origin, direction, out RaycastHit firstHit, ownHit.distance + .05f,
                        PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore) || BelongsTo(firstHit.collider, target)) return true;
                }
            }
            return false;
        }

        private static bool Usable(Collider surface, Unit target)
        {
            return surface != null && surface.enabled && !surface.isTrigger && surface.gameObject.activeInHierarchy &&
                (PhysicsLayers.StaticsMask.value & (1 << surface.gameObject.layer)) != 0 && BelongsTo(surface, target);
        }

        private static bool BelongsTo(Collider surface, Unit target)
        {
            if (surface == null) return false;
            UnitPart part = surface.GetComponentInParent<UnitPart>();
            if (part != null) return !part.IsDetached() && part.parentUnit == target;
            return surface.GetComponentInParent<Unit>() == target;
        }
    }
}
