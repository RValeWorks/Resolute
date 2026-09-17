using UnityEngine;
using UnityEngine.Rendering;

namespace Resolute
{
    // Revision 09 smoke envelope on an owned NO-native material instance.
    // No source emitter, material, native pool or other missile is modified.
    internal sealed class LanceSmoke : MonoBehaviour
    {
        private ParticleSystem smoke;
        private TrailRenderer bridge;
        private Transform source;
        private Vector3 previous, datum;
        private float spacing, timeBirths, endedAt = -1f;
        private readonly Vector3[] trailPositions = new Vector3[512];
        private Material material;

        internal static LanceSmoke Create(Transform owner, Material nativeSmoke)
        {
            var item = new GameObject("Lance sustained boost smoke"); item.layer = owner.gameObject.layer;
            item.transform.SetParent(owner, false); item.transform.localPosition = new Vector3(0f, 0f, -4.651f);
            var value = item.AddComponent<LanceSmoke>(); value.source = owner;
            value.material = new Material(nativeSmoke) { name = "Lance owned native smoke" };
            value.smoke = item.AddComponent<ParticleSystem>(); value.smoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = value.smoke.main; main.playOnAwake = false; main.loop = true; main.maxParticles = 3072;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom; main.customSimulationSpace = Datum.origin;
            main.startLifetime = new ParticleSystem.MinMaxCurve(9f, 13f); main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(.36f, .44f); main.startColor = new Color(.82f, .80f, .76f, .18f);
            var emission = value.smoke.emission; emission.enabled = false;
            var shape = value.smoke.shape; shape.enabled = false;
            var size = value.smoke.sizeOverLifetime; size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1), new Keyframe(.01f, 25), new Keyframe(.35f, 32.5f), new Keyframe(1, 57.5f)));
            var color = value.smoke.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient(); gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, new[] {
                new GradientAlphaKey(.6f,0f),new GradientAlphaKey(1f,.04f),new GradientAlphaKey(.88f,.35f),new GradientAlphaKey(.58f,.65f),new GradientAlphaKey(.18f,.9f),new GradientAlphaKey(0f,1f)});
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var renderer = value.smoke.GetComponent<ParticleSystemRenderer>(); renderer.sharedMaterial = value.material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch; renderer.lengthScale = 2.6f; renderer.velocityScale = 0f; renderer.cameraVelocityScale = 0f;
            renderer.freeformStretching = true; renderer.rotateWithStretchDirection = true; renderer.allowRoll = false;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            value.bridge = item.AddComponent<TrailRenderer>(); value.bridge.sharedMaterial = value.material;
            value.bridge.time = .25f; value.bridge.minVertexDistance = 4f; value.bridge.widthMultiplier = 1f;
            value.bridge.widthCurve = new AnimationCurve(new Keyframe(0,.4f),new Keyframe(.2f,3f),new Keyframe(.55f,9f),new Keyframe(1,12f));
            var trailColor = new Gradient(); trailColor.SetKeys(new[] {new GradientColorKey(new Color(.82f,.80f,.76f),0f),new GradientColorKey(new Color(.82f,.80f,.76f),1f)},
                new[] {new GradientAlphaKey(.11f,0f),new GradientAlphaKey(.14f,.2f),new GradientAlphaKey(.09f,.55f),new GradientAlphaKey(0f,1f)});
            value.bridge.colorGradient = trailColor; value.bridge.shadowCastingMode = ShadowCastingMode.Off; value.bridge.receiveShadows = false;
            value.bridge.emitting = false; // Explicit points make origin relocation deterministic.
            value.previous = item.transform.position - Datum.originPosition; value.datum = Datum.originPosition;
            value.smoke.Play(); return value;
        }

        internal void End()
        {
            if (endedAt >= 0f) return;
            endedAt = Time.time; source = null;
            transform.SetParent(Datum.origin, true); bridge.emitting = false;
            smoke.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        private void LateUpdate()
        {
            // Particle simulation is already relative to the native datum.
            // TrailRenderer stores world positions; shift only its owned points.
            Vector3 delta = Datum.originPosition - datum;
            if (delta.sqrMagnitude > .001f)
            {
                int count = bridge.GetPositions(trailPositions);
                for (int i = 0; i < count; i++) bridge.SetPosition(i, trailPositions[i] + delta);
                datum = Datum.originPosition;
            }
            if (endedAt >= 0f)
            {
                if (!smoke.IsAlive(false) || Time.time - endedAt > 14f) Destroy(gameObject);
                return;
            }
            if (source == null || !source.gameObject.activeInHierarchy) { End(); return; }
            float dt = Time.deltaTime; if (dt <= 0f) return;
            Vector3 current = transform.position - Datum.originPosition;
            float distance = Vector3.Distance(previous, current);
            // Eight-metre spacing is sampled along the actual traversed segment,
            // not from the desired Mach setting or one particle per frame.
            float consumed = 8f - spacing;
            int countLimit = 0;
            while (consumed <= distance && countLimit++ < 256)
            {
                Emit(Vector3.Lerp(previous, current, consumed / Mathf.Max(.001f, distance)));
                consumed += 8f;
            }
            spacing = (spacing + distance) % 8f;
            timeBirths += dt * 6f;
            while (timeBirths >= 1f) { Emit(current); timeBirths -= 1f; }
            bridge.AddPosition(transform.position); previous = current;
        }
        private void Emit(Vector3 global)
        {
            var emission = new ParticleSystem.EmitParams { position = global, velocity = -source.forward * 6f };
            smoke.Emit(emission, 1);
        }
        private void OnDestroy() { if (material != null) Destroy(material); }
    }
}
