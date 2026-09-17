using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    [HarmonyPatch(typeof(Missile), "ApplyAero")]
    internal static class NaturalPikeAero
    {
        private static readonly AccessTools.FieldRef<Missile, Vector3> LocalRate = AccessTools.FieldRefAccess<Missile, Vector3>("localAngularVel");
        private static readonly AccessTools.FieldRef<Missile, Vector3> Inputs = AccessTools.FieldRefAccess<Missile, Vector3>("inputs");
        private static readonly AccessTools.FieldRef<Missile, float> MaximumRate = AccessTools.FieldRefAccess<Missile, float>("maxTurnRate");
        private static readonly AccessTools.FieldRef<Missile, float> GLimit = AccessTools.FieldRefAccess<Missile, float>("gLimit");
        private static readonly MethodInfo NativeTorque = AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddRelativeTorque), new[] { typeof(Vector3), typeof(ForceMode) });
        private static readonly MethodInfo GuardedTorque = AccessTools.Method(typeof(NaturalPikeAero), nameof(ApplyTorque));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var result = new List<CodeInstruction>(instructions);
            int match = -1, count = 0;
            for (int index = 0; index < result.Count; index++)
                if ((result[index].opcode == OpCodes.Call || result[index].opcode == OpCodes.Callvirt) &&
                    Equals(result[index].operand, NativeTorque)) { match = index; count++; }
            if (NativeTorque == null || GuardedTorque == null || count != 1 || result[match].blocks.Count != 0)
                throw new InvalidOperationException("Resolute Pike aero guard expected exactly one unprotected native AddRelativeTorque(Vector3, ForceMode) call in Missile.ApplyAero.");
            // Keep the native body, Vector3 and ForceMode already on the stack;
            // append its actual Missile instance for the narrowly scoped guard.
            CodeInstruction call = result[match];
            var instance = new CodeInstruction(OpCodes.Ldarg_0);
            instance.labels.AddRange(call.labels); call.labels.Clear();
            call.opcode = OpCodes.Call; call.operand = GuardedTorque;
            result.Insert(match, instance);
            return result;
        }

        internal static void ApplyTorque(Rigidbody body, Vector3 nativeAcceleration, ForceMode mode, Missile missile)
        {
            Vector3 acceleration = nativeAcceleration;
            if (body != null && missile != null && missile.rb == body && missile.LocalSim && !missile.disabled &&
                missile.definition != null && missile.definition.jsonKey == "rsl_ashm" && mode == ForceMode.Acceleration &&
                Finite(nativeAcceleration))
            {
                NaturalWeaponPhase phase = missile.GetComponent<NaturalWeaponPhase>();
                if (phase != null && phase.TerminalBoostActive)
                {
                    float dt = Time.fixedDeltaTime, maximum = MaximumRate(missile), g = GLimit(missile);
                    float torque = missile.GetTorque();
                    Vector3 current = LocalRate(missile), input = Inputs(missile);
                    if (Finite(dt) && dt > 0f && Finite(maximum) && Finite(g) && Finite(torque) &&
                        Finite(missile.speed) && Finite(current) && Finite(input) && (maximum > 0f || g > 0f))
                    {
                        // Use the same cached local rate, input, cap and dt as
                        // the native method which supplied this acceleration.
                        float cap = Mathf.Min(maximum * (Mathf.PI / 180f), 9.81f * g / Mathf.Max(missile.speed, 1f));
                        if (Finite(cap) && cap >= 0f)
                        {
                            acceleration.x = CorrectAxis(current.x, input.x * torque, cap, dt, nativeAcceleration.x);
                            acceleration.y = CorrectAxis(current.y, input.y * torque, cap, dt, nativeAcceleration.y);
                        }
                    }
                }
            }
            body.AddRelativeTorque(acceleration, mode);
        }

        private static float CorrectAxis(float current, float requested, float cap, float dt, float native)
        {
            float predicted = current + requested * dt;
            if (!Finite(requested) || !Finite(predicted) || Mathf.Abs(current) <= cap || Mathf.Abs(predicted) <= cap ||
                Mathf.Sign(requested) == Mathf.Sign(predicted)) return native;
            // Native subtracts Sign(requested) * excess. After an external
            // over-cap rate, opposing input can therefore amplify the spin.
            // Correct only that branch through the native acceleration force.
            float corrected = (Mathf.Clamp(predicted, -cap, cap) - current) / dt;
            return Finite(corrected) ? corrected : native;
        }

        private static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
