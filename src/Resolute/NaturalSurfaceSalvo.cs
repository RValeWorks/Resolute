using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    internal static class NaturalSurfaceSalvo
    {
        private static readonly ConditionalWeakTable<IList, FireControl> Owners = new ConditionalWeakTable<IList, FireControl>();
        internal static readonly MethodInfo NativeQueueAttack = typeof(FireControl)
            .GetNestedType("FireControlTarget", BindingFlags.NonPublic)?
            .GetMethod("QueueAttack", BindingFlags.Instance | BindingFlags.Public);

        internal static void Register(FireControl controller, WeaponInfo info)
        {
            if (!SurfaceSalvoOrderPolicy.Applies(NaturalMissileTargeting.Key(info))) return;
            var attacks = (IList)NaturalWeapons.Get(controller, "queuedAttacks");
            Owners.Remove(attacks);
            Owners.Add(attacks, controller);
        }

        internal static double LaunchInterval(Unit owner, UnitDefinition definition)
        {
            NaturalFireControlGroups groups = owner != null ? owner.GetComponent<NaturalFireControlGroups>() : null;
            if (groups != null && groups.Groups != null && definition != null)
                foreach (NaturalFireControlGroups.Group group in groups.Groups)
                    if (group != null && group.Controller != null && NaturalMissileTargeting.Key(group.Info) == definition.jsonKey)
                        return (float)NaturalWeapons.Get(group.Controller, "salvoInterval");
            return PikeCohortPolicy.DefaultSalvoIntervalSeconds;
        }

        // This substitutes only the call site inside native PlanSalvo. The
        // original QueueAttack still constructs every native reservation and
        // decrements its own demand. LaunchSalvo, Fire/Cancel and sensors are
        // untouched. Weak ownership excludes every unrelated fire controller.
        internal static void QueueNativeAttack(object target, IList attacks, WeaponStation station, out bool finished)
        {
            object[] args = { attacks, station, false };
            NativeQueueAttack.Invoke(target, args);
            finished = (bool)args[2];
            FireControl controller;
            if (Owners.TryGetValue(attacks, out controller) && controller != null)
                SurfaceSalvoOrderPolicy.RotatePending((IList)NaturalWeapons.Get(controller, "salvoTargets"), target, finished);
        }
    }

    [HarmonyPatch]
    internal static class NaturalSurfaceSalvoPlanPatch
    {
        private static MethodInfo moveNext;
        internal static bool Prepare()
        {
            MethodInfo plan = typeof(FireControl).GetMethod("PlanSalvo", BindingFlags.Instance | BindingFlags.NonPublic);
            var attribute = plan?.GetCustomAttribute<AsyncStateMachineAttribute>();
            moveNext = attribute?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            bool available = moveNext != null && NaturalSurfaceSalvo.NativeQueueAttack != null;
            if (!available) Debug.LogWarning("Resolute surface salvo ordering: native planning method unavailable; retaining native ordering.");
            return available;
        }
        private static MethodBase TargetMethod() => moveNext;

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            int matches = body.Count(i => (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) &&
                Equals(i.operand, NaturalSurfaceSalvo.NativeQueueAttack));
            if (matches != 1)
            {
                Debug.LogWarning("Resolute surface salvo ordering: expected one native reservation call, found " + matches + "; retaining native ordering.");
                return body;
            }
            MethodInfo replacement = typeof(NaturalSurfaceSalvo).GetMethod(nameof(NaturalSurfaceSalvo.QueueNativeAttack),
                BindingFlags.Static | BindingFlags.NonPublic);
            foreach (CodeInstruction instruction in body)
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    Equals(instruction.operand, NaturalSurfaceSalvo.NativeQueueAttack))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
            return body;
        }
    }
}
