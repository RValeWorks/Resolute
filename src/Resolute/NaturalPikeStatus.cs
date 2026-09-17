using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Resolute
{
    // Native UnitDebug writes missile target/state text every Update. Describe
    // the current formation role afterwards without changing the native target
    // used by navigation, the seeker, or faction attack accounting.
    [HarmonyPatch(typeof(UnitDebug), "Update")]
    internal static class NaturalPikeStatus
    {
        private static void Postfix(Unit ___followingUnit, TMP_Text ___target, TMP_Text ___state, GameObject ___statePanel)
        {
            Missile missile = ___followingUnit as Missile;
            if (missile == null || missile.definition == null || missile.definition.jsonKey != "rsl_ashm") return;

            NaturalPikeGroupTargeting group = missile.GetComponent<NaturalPikeGroupTargeting>();
            bool awaiting = group != null && group.AwaitingAssignment;
            uint leader = awaiting ? PendingLeader(group.FormationLeaderId) : 0u;
            if (awaiting && leader == 0) leader = PendingLeader(group.LeaderId);
            // Between leader elections an individual launch objective is still
            // not a terminal assignment. Keep it hidden while the group forms.
            bool showRole = awaiting;
            if (showRole)
            {
                bool following = leader == 0 || missile.persistentID.Id != leader;
                if (following && ___target != null) ___target.text = "None";
                if (___state != null) ___state.text = leader == 0 ? "Forming group" : following ? "Awaiting assignment" :
                    group.LeaderSearching ? "Searching targets" : "Leading formation";
            }
            if (___statePanel != null && ___statePanel.activeSelf != showRole) ___statePanel.SetActive(showRole);
            // Native UnitDebug_OnFollowingUnitSet owns panel visibility when
            // following another unit, so no cached label can leak across units.
        }

        private static uint PendingLeader(uint id)
        {
            Unit unit;
            if (id == 0 || !UnitRegistry.TryGetUnit(new PersistentID { Id = id }, out unit)) return 0u;
            Missile leader = unit as Missile;
            if (leader == null || leader.disabled) return 0u;
            NaturalPikeGroupTargeting group = leader.GetComponent<NaturalPikeGroupTargeting>();
            return group != null && group.AwaitingAssignment ? id : 0u;
        }
    }
}
