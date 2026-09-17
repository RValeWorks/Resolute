using System.Collections;

namespace Resolute
{
    internal static class SurfaceSalvoOrderPolicy
    {
        internal static bool Applies(string key) => key == "rsl_ashm" || key == "rsl_cruise";

        // CombatAI.DistributeTargets visits each still-needed target once per
        // pass. Native ship PlanSalvo instead stays on its highest pending index
        // until demand is exhausted. Rotate that active prefix after a shot;
        // completed targets stay where PlanSalvo expects them and its indices,
        // 100-iteration bound, station cycling and demand decrement still own it.
        internal static void RotatePending(IList targets, object justQueued, bool finished)
        {
            if (finished || targets == null) return;
            int index = targets.IndexOf(justQueued);
            if (index <= 0) return;
            for (int i = index; i > 0; i--) targets[i] = targets[i - 1];
            targets[0] = justQueued;
        }
    }
}
