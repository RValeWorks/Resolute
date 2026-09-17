using UnityEngine;

namespace Resolute
{
    // Operating information only: the optional addon owns all rendering.
    public static class ResolutePikeMap
    {
        public const float RadarRangeMetres = 10f * 1852f;
        public const float SearchHalfAngleDegrees = 45f;

        public static ResolutePikeSearchZone CreateSearchZone(GlobalPosition origin, GlobalPosition selected, float maximumRange)
            => new ResolutePikeSearchZone(origin.x, origin.z, selected.x, selected.z,
                RadarRangeMetres, SearchHalfAngleDegrees, maximumRange);

        public static bool TryGetLine(Missile missile, out GlobalPosition point, out bool awaitingAssignment)
        {
            point = default(GlobalPosition); awaitingAssignment = false;
            if (missile == null || missile.disabled || missile.definition == null ||
                missile.definition.jsonKey != "rsl_ashm" || missile.NetworkHQ == null) return false;
            NaturalPikeGroupTargeting group = missile.GetComponent<NaturalPikeGroupTargeting>();
            awaitingAssignment = group != null ? group.AwaitingAssignment : ResoluteStrikeOrders.HasAreaObjective(missile);
            if (awaitingAssignment)
            {
                if (group == null) return false;
                uint leader = PendingLeader(group.LeaderId);
                if (leader == 0) leader = PendingLeader(group.FormationLeaderId);
                return leader == missile.persistentID.Id && ResoluteStrikeOrders.TryGetMapObjective(missile, out point);
            }
            return missile.targetID.IsValid && UnitRegistry.TryGetUnit(missile.targetID, out Unit target) &&
                target != null && !target.disabled && missile.NetworkHQ.TryGetKnownPosition(target, out point);
        }

        private static uint PendingLeader(uint id)
        {
            if (id == 0 || !UnitRegistry.TryGetUnit(new PersistentID { Id = id }, out Unit unit) ||
                !(unit is Missile missile) || missile.disabled) return 0;
            return missile.GetComponent<NaturalPikeGroupTargeting>()?.AwaitingAssignment == true ? id : 0;
        }
    }
}
