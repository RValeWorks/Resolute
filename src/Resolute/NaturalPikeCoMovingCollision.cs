using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    [HarmonyPatch(typeof(Missile), "DetectCollisions")]
    internal static class NaturalPikeCoMovingCollision
    {
        private static bool warnedUnsupportedShape;
        internal static bool PatchActive { get; private set; }

        internal static bool ShouldSkipCoMovingAdvance(Missile missile, ref RaycastHit hit)
        {
            // The native far sweep already ignores this dynamic co-moving body,
            // but only after relocating onto it. Preserve its existing return
            // before those two pose writes, for allied local Pikes only. Actual
            // colliders, Unity contacts, close sweeps and impact fuses stay native.
            if (!ValidPike(missile) || missile.NetworkHQ == null || missile.rb == null || hit.collider == null)
                return false;
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body == null || body.isKinematic) return false;
            Missile peer = hit.collider.GetComponentInParent<Missile>();
            return peer != missile && ValidPike(peer) && peer.NetworkHQ == missile.NetworkHQ && peer.rb == body &&
                FastMath.InRange(body.velocity, missile.rb.velocity, 100f);
        }

        private static bool ValidPike(Missile missile) => missile != null && missile.LocalSim && !missile.disabled &&
            missile.isActiveAndEnabled && missile.definition != null && missile.definition.jsonKey == "rsl_ashm";

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var native = new List<CodeInstruction>(instructions);
            MethodInfo linecast = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
                new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(int) });
            MethodInfo predicate = AccessTools.Method(typeof(NaturalPikeCoMovingCollision), nameof(ShouldSkipCoMovingAdvance));
            int call = -1, count = 0;
            bool protectedBody = false;
            for (int i = 0; i < native.Count; i++)
            {
                if (native[i].opcode == OpCodes.Call && Equals(native[i].operand, linecast)) { call = i; count++; }
                protectedBody |= native[i].blocks.Count != 0;
            }
            PatchActive = generator != null && linecast != null && predicate != null && count == 2 && !protectedBody &&
                MatchFarBody(native, call);
            if (!PatchActive)
            {
                if (!warnedUnsupportedShape)
                {
                    warnedUnsupportedShape = true;
                    Plugin.Instance?.LogStartupWarning("Pike co-moving sweep correction is unavailable: Missile.DetectCollisions layout changed. Keeping the original native method.");
                }
                return native;
            }

            int start = call + 2;
            Label originalBody = generator.DefineLabel();
            var instance = new CodeInstruction(OpCodes.Ldarg_0);
            instance.labels.AddRange(native[start].labels);
            native[start].labels.Clear();
            native[start].labels.Add(originalBody);
            native.InsertRange(start, new[] {
                instance,
                new CodeInstruction(native[call - 2].opcode, native[call - 2].operand),
                new CodeInstruction(OpCodes.Call, predicate),
                new CodeInstruction(OpCodes.Brfalse, originalBody),
                new CodeInstruction(OpCodes.Ret)
            });
            return native;
        }

        private static bool MatchFarBody(List<CodeInstruction> code, int call)
        {
            int s = call + 2;
            if (call < 2 || s + 48 >= code.Count || !Address(code[call - 2]) || !Load(code[call - 1]) ||
                !Branch(code[call + 1], false)) return false;
            // Exact current native straight-line relocation and existing
            // co-moving return. Validate before mutating any instruction.
            OpCode[] expected = {
                OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Ldloca_S, OpCodes.Call,
                OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Callvirt, OpCodes.Stloc_S,
                OpCodes.Ldloca_S, OpCodes.Call, OpCodes.Ldc_R4, OpCodes.Call, OpCodes.Call, OpCodes.Callvirt,
                OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Ldloca_S, OpCodes.Call,
                OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Callvirt, OpCodes.Stloc_S,
                OpCodes.Ldloca_S, OpCodes.Call, OpCodes.Ldc_R4, OpCodes.Call, OpCodes.Call, OpCodes.Callvirt,
                OpCodes.Ldloca_S, OpCodes.Call, OpCodes.Callvirt, OpCodes.Stloc_S,
                OpCodes.Ldloc_S, OpCodes.Ldnull, OpCodes.Call, OpCodes.Brfalse_S,
                OpCodes.Ldloc_S, OpCodes.Callvirt, OpCodes.Brtrue_S,
                OpCodes.Ldloc_S, OpCodes.Callvirt, OpCodes.Ldarg_0, OpCodes.Call, OpCodes.Callvirt,
                OpCodes.Ldc_R4, OpCodes.Call, OpCodes.Brfalse_S, OpCodes.Ret
            };
            for (int i = 0; i < expected.Length; i++)
            {
                // Harmony normalizes short conditional branches to long ones.
                // Only these three branch encodings may differ from native IL.
                bool matches = i == 35 || i == 46 ? Branch(code[s + i], false) :
                    i == 38 ? Branch(code[s + i], true) : code[s + i].opcode == expected[i];
                if (!matches || (i > 0 && code[s + i].labels.Count != 0)) return false;
            }
            if (!Equals(code[s + 10].operand, .2f) || !Equals(code[s + 24].operand, .2f) ||
                !Equals(code[s + 44].operand, 100f)) return false;
            foreach (int i in new[] { 2, 16, 28 })
                if (!Equals(code[s + i].operand, code[call - 2].operand)) return false;
            foreach (int i in new[] { 8, 21, 22 })
                if (!Equals(code[s + i].operand, code[s + 7].operand)) return false;
            foreach (int i in new[] { 32, 36, 39 })
                if (!Equals(code[s + i].operand, code[s + 31].operand)) return false;
            // Harmony can assign separate labels to branches with the same
            // destination. Compare destination membership, not label identity.
            foreach (int i in new[] { 35, 38, 46 })
                if (!(code[s + i].operand is Label after) || !code[s + 48].labels.Contains(after)) return false;

            return Method(code[s + 1], typeof(Component), "get_transform") &&
                Method(code[s + 3], typeof(RaycastHit), "get_point") && Same(code, s, 3, 17) &&
                Method(code[s + 5], typeof(Unit), "get_rb") && Same(code, s, 5, 15, 19, 42) &&
                Method(code[s + 6], typeof(Rigidbody), "get_velocity") && Same(code, s, 6, 20, 40, 43) &&
                Method(code[s + 9], typeof(Vector3), "get_normalized") && Same(code, s, 9, 23) &&
                Method(code[s + 11], typeof(Vector3), "op_Multiply", typeof(Vector3), typeof(float)) && Same(code, s, 11, 25) &&
                Method(code[s + 12], typeof(Vector3), "op_Subtraction", typeof(Vector3), typeof(Vector3)) && Same(code, s, 12, 26) &&
                Method(code[s + 13], typeof(Transform), "set_position", typeof(Vector3)) &&
                Method(code[s + 27], typeof(Rigidbody), nameof(Rigidbody.MovePosition), typeof(Vector3)) &&
                Method(code[s + 29], typeof(RaycastHit), "get_collider") &&
                Method(code[s + 30], typeof(Collider), "get_attachedRigidbody") &&
                Method(code[s + 34], typeof(UnityEngine.Object), "op_Inequality", typeof(UnityEngine.Object), typeof(UnityEngine.Object)) &&
                Method(code[s + 37], typeof(Rigidbody), "get_isKinematic") &&
                Method(code[s + 45], typeof(FastMath), nameof(FastMath.InRange), typeof(Vector3), typeof(Vector3), typeof(float));
        }

        private static bool Same(List<CodeInstruction> code, int start, int first, params int[] rest)
        {
            foreach (int i in rest) if (!Equals(code[start + first].operand, code[start + i].operand)) return false;
            return true;
        }
        private static bool Method(CodeInstruction instruction, Type type, string name, params Type[] parameters)
        {
            MethodInfo method = AccessTools.Method(type, name, parameters);
            return method != null && Equals(instruction.operand, method);
        }
        private static bool Address(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S;
        private static bool Load(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
            instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 || instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;
        private static bool Branch(CodeInstruction instruction, bool whenTrue) => instruction.operand is Label &&
            (whenTrue ? instruction.opcode == OpCodes.Brtrue || instruction.opcode == OpCodes.Brtrue_S :
                instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S);
    }
}
