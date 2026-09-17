using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Editor manipulation moves every child collider. Unity otherwise rebuilds
    // the compound body's mass properties at every transform flush, although
    // rigid placement has not changed any part's local shape or mass.
    [DefaultExecutionOrder(-32000)]
    internal sealed class ResoluteEditorMassProperties : MonoBehaviour
    {
        private static readonly List<ResoluteEditorMassProperties> Instances = new List<ResoluteEditorMassProperties>();
        private Ship ship;
        private Rigidbody body;
        private bool automaticCenter, automaticInertia;
        internal bool Frozen { get; private set; }
        internal static bool DiagnosticSuspended { get; private set; }

        private static bool PausedEditor => !DiagnosticSuspended && GameManager.gameState == GameState.Editor && Time.timeScale == 0f;

        private void Awake() { enabled = false; }

        internal static void Register(Ship value)
        {
            if (value == null || !Plugin.IsResolute(value.definition) || value.rb == null) return;
            var component = value.GetComponent<ResoluteEditorMassProperties>();
            if (component == null) component = value.gameObject.AddComponent<ResoluteEditorMassProperties>();
            component.ship = value; component.body = value.rb;
            if (!Instances.Contains(component)) Instances.Add(component);
            component.FreezeIfPaused();
        }

        private void FreezeIfPaused()
        {
            if (Frozen || !PausedEditor || ship == null || body == null || !ship.gameObject.activeInHierarchy) return;
            automaticCenter = body.automaticCenterOfMass;
            automaticInertia = body.automaticInertiaTensor;
            Vector3 center = body.centerOfMass, tensor = body.inertiaTensor;
            Quaternion tensorRotation = body.inertiaTensorRotation;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.centerOfMass = center;
            body.inertiaTensor = tensor;
            body.inertiaTensorRotation = tensorRotation;
            Frozen = true;
            enabled = true;
        }

        private void Restore()
        {
            if (!Frozen) return;
            Frozen = false;
            if (body != null)
            {
                // Restore only ownership of recalculation. Never put an old
                // mass/tensor back over native changes made during the session.
                body.automaticCenterOfMass = automaticCenter;
                body.automaticInertiaTensor = automaticInertia;
            }
            enabled = false;
        }

        private void LateUpdate() { if (!PausedEditor) Restore(); }

        // Native transitions restore synchronously below. This runs before the
        // game's normal FixedUpdates if a third party writes Time.timeScale
        // directly, bypassing TimeScaleManager.
        private void FixedUpdate() { if (!PausedEditor) Restore(); }

        private void OnDisable() { Restore(); }
        private void OnDestroy() { Restore(); Instances.Remove(this); }

        private static void RestoreAll()
        {
            for (int i = 0; i < Instances.Count; i++)
                if (Instances[i] != null) Instances[i].Restore();
        }

        private static void FreezeAll()
        {
            if (!PausedEditor) return;
            for (int i = 0; i < Instances.Count; i++)
                if (Instances[i] != null) Instances[i].FreezeIfPaused();
        }

        // Only the explicit isolated A/B experiment calls this; production has
        // no configuration or command-line path that disables restoration.
        internal static void SuspendForDiagnostic(bool value)
        {
            DiagnosticSuspended = value;
            if (value) RestoreAll(); else FreezeAll();
        }

        [HarmonyPatch(typeof(Ship), "OnStartClient")]
        private static class ShipInitialized
        {
            private static void Prefix(Ship __instance)
            {
                var component = __instance.GetComponent<ResoluteEditorMassProperties>();
                if (component != null) component.Restore();
            }
            private static void Postfix(Ship __instance) { Register(__instance); }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.SetGameState))]
        private static class StateChanged
        {
            private static void Prefix(GameState gameState) { if (gameState != GameState.Editor) RestoreAll(); }
            private static void Postfix() { FreezeAll(); }
        }

        [HarmonyPatch(typeof(TimeScaleManager), nameof(TimeScaleManager.Scale), MethodType.Setter)]
        private static class TimeChanged
        {
            private static void Prefix(float value) { if (value != 0f) RestoreAll(); }
            private static void Postfix() { FreezeAll(); }
        }
    }
}
