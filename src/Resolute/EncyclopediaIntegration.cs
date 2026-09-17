using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Keep the game's own aggregation, sorting, styling and text formatting. The
    // serialized browser only provides a fixed number of weapon rows, whereas a
    // Resolute outfit can contain more distinct weapon types than native ships.
    [HarmonyPatch(typeof(EncyclopediaBrowser), "UpdateWeaponDisplay")]
    internal static class EncyclopediaIntegration
    {
        private static readonly FieldInfo Rows = AccessTools.Field(typeof(EncyclopediaBrowser), "weaponStationDisplays");

        private static void Prefix(EncyclopediaBrowser __instance, Unit unit)
        {
            if (unit == null || !Plugin.IsResolute(unit.definition)) return;
            int required = unit.weaponStations.Select(s => s.WeaponInfo).Distinct().Count();
            Array rows = (Array)Rows.GetValue(__instance);
            if (required <= rows.Length) return;
            if (rows.Length == 0) throw new InvalidOperationException("Native encyclopedia has no weapon-row template.");

            Type rowType = rows.GetType().GetElementType();
            FieldInfo panelField = AccessTools.Field(rowType, "panel");
            FieldInfo nameField = AccessTools.Field(rowType, "nameText");
            FieldInfo ammoField = AccessTools.Field(rowType, "ammoText");
            object prototype = rows.GetValue(rows.Length - 1);
            GameObject sourcePanel = (GameObject)panelField.GetValue(prototype);
            TMP_Text sourceName = (TMP_Text)nameField.GetValue(prototype);
            TMP_Text sourceAmmo = (TMP_Text)ammoField.GetValue(prototype);
            Array expanded = Array.CreateInstance(rowType, required);
            Array.Copy(rows, expanded, rows.Length);
            for (int index = rows.Length; index < required; index++)
            {
                GameObject panel = Object.Instantiate(sourcePanel, sourcePanel.transform.parent, false);
                panel.name = "ResoluteWeaponRow" + index;
                panel.transform.SetSiblingIndex(sourcePanel.transform.GetSiblingIndex() + index - rows.Length + 1);
                panel.SetActive(false);
                object row = Activator.CreateInstance(rowType, true);
                panelField.SetValue(row, panel);
                nameField.SetValue(row, CloneText(sourcePanel.transform, panel.transform, sourceName));
                ammoField.SetValue(row, CloneText(sourcePanel.transform, panel.transform, sourceAmmo));
                expanded.SetValue(row, index);
            }
            Rows.SetValue(__instance, expanded);
        }

        private static void Postfix(EncyclopediaBrowser __instance, Unit unit)
        {
            if (unit == null || !Plugin.IsResolute(unit.definition)) return;
            foreach (object row in (Array)Rows.GetValue(__instance))
            {
                Type type = row.GetType();
                WeaponInfo info = (WeaponInfo)AccessTools.Field(type, "weaponInfo").GetValue(row);
                if (!IsHalo(info)) continue;
                // Native Laser.ammo is a readiness sentinel, not stored rounds.
                ((TMP_Text)AccessTools.Field(type, "ammoText").GetValue(row)).text = string.Empty;
            }
        }

        internal static bool IsHalo(WeaponInfo info) => info != null && info.name == "rsl_laser_pulse" && info.energy;

        private static TMP_Text CloneText(Transform sourceRoot, Transform cloneRoot, TMP_Text source)
        {
            var indices = new Stack<int>();
            Transform at = source.transform;
            while (at != sourceRoot)
            {
                if (at == null) throw new InvalidOperationException("Native weapon-row text is outside its panel.");
                indices.Push(at.GetSiblingIndex());
                at = at.parent;
            }
            at = cloneRoot;
            while (indices.Count != 0) at = at.GetChild(indices.Pop());
            TMP_Text result = at.GetComponent<TMP_Text>();
            if (result == null) throw new InvalidOperationException("Cloned native weapon row lost a text component.");
            return result;
        }
    }

    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.GetAmmoReadout))]
    internal static class HaloAmmoReadout
    {
        private static void Postfix(WeaponStation __instance, ref string __result)
        {
            if (EncyclopediaIntegration.IsHalo(__instance.WeaponInfo)) __result = string.Empty;
        }
    }

    // The native followed-unit HUD aggregates Ammo/FullAmmo directly instead
    // of using GetAmmoReadout. Its update method also handles station events.
    [HarmonyPatch(typeof(UnitDebug.WeaponStationDebug), nameof(UnitDebug.WeaponStationDebug.UpdateText))]
    internal static class HaloFollowedUnitReadout
    {
        private static void Postfix(WeaponInfo ___weaponInfo, TMP_Text ___text)
        {
            if (EncyclopediaIntegration.IsHalo(___weaponInfo)) ___text.text = ___weaponInfo.weaponName;
        }
    }
}
