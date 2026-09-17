using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Runs after the native Laser's LateUpdate has stopped an expired beam.
    // Its beam state is the firing indication; an order alone never lights the lens.
    [DefaultExecutionOrder(10000)]
    internal sealed class ResoluteHaloAperture : MonoBehaviour
    {
        private static readonly FieldInfo BeamField = typeof(Laser).GetField("beamRenderer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MuzzleField = typeof(Laser).GetField("muzzleParticles", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<Mesh, Mesh> SplitMeshes = new Dictionary<Mesh, Mesh>();
        private static readonly Dictionary<Material, Material> FrontMaterials = new Dictionary<Material, Material>();
        private static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        // This exact opaque emissive variant is present in the game's URP Lit
        // shader. Adding emission to the glass's three map keywords is not:
        // that combination was stripped from the player build.
        private static readonly string[] FiringKeywords = { "_EMISSION" };
        internal static readonly Color FiringEmission = new Color(.02f, .3f, 8f, 1f);
        public Laser Laser;
        public Renderer Beam;
        public Renderer[] Lenses;
        private Material[] ownedFronts;
        private string[][] idleKeywords;
        private bool applied;

        internal static void Configure(Laser laser, Transform lens, Transform direction)
        {
            // The Cursor muzzle billboard has its own red emissive material.
            // Its GameObject also owns the native aim transform and blue beam:
            // stop only the particles, never disable or move that whole object.
            ParticleSystem[] particles = (ParticleSystem[])MuzzleField.GetValue(laser);
            foreach (ParticleSystem particle in particles ?? new ParticleSystem[0])
            {
                if (particle == null) continue;
                particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var emission = particle.emission; emission.enabled = false;
                ParticleSystemRenderer renderer = particle.GetComponent<ParticleSystemRenderer>();
                if (renderer != null) renderer.enabled = false;
            }
            MuzzleField.SetValue(laser, new ParticleSystem[0]);
            Renderer[] lenses = lens.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in lenses)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || filter.sharedMesh.subMeshCount != 1 ||
                    renderer.sharedMaterials.Length != 1 || !renderer.sharedMaterial.HasProperty(Emission))
                    throw new InvalidOperationException("Unexpected Halo aperture surface: " + renderer.name);
                Mesh split;
                if (!SplitMeshes.TryGetValue(filter.sharedMesh, out split))
                {
                    split = SplitFront(filter.sharedMesh);
                    Object.DontDestroyOnLoad(split); SplitMeshes.Add(filter.sharedMesh, split);
                }
                Material idle = renderer.sharedMaterial, front;
                if (!FrontMaterials.TryGetValue(idle, out front))
                {
                    front = new Material(idle) { name = "Resolute Halo aperture / prepared front" };
                    front.SetTexture("_EmissionMap", Texture2D.whiteTexture);
                    front.SetColor(Emission, Color.black);
                    Object.DontDestroyOnLoad(front); FrontMaterials.Add(idle, front);
                }
                filter.sharedMesh = split;
                renderer.sharedMaterials = new[] { idle, front };
            }
            // The authored muzzle anchor sat 20 cm behind the glass. Derive
            // the beam origin from the real lens face rather than a magic offset.
            Bounds bounds = lens.GetComponent<MeshFilter>().sharedMesh.bounds;
            direction.position = lens.TransformPoint(new Vector3(bounds.center.x, bounds.center.y, bounds.max.z + .02f));
            var effects = laser.gameObject.AddComponent<ResoluteHaloAperture>();
            effects.Laser = laser; effects.Beam = (Renderer)BeamField.GetValue(laser); effects.Lenses = lenses;
        }

        internal static Mesh SplitFront(Mesh original)
        {
            Vector3[] vertices = original.vertices;
            int[] triangles = original.triangles;
            var front = new List<int>(); var rest = new List<int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a);
                // The authored glass has several overlapping forward faces.
                // A depth cut leaves dark triangles over the lit disc. Front
                // winding keeps every outward face and excludes its back/rim.
                List<int> destination = normal.z > .01f * normal.magnitude ? front : rest;
                destination.Add(triangles[i]); destination.Add(triangles[i + 1]); destination.Add(triangles[i + 2]);
            }
            if (front.Count == 0 || rest.Count == 0)
                throw new InvalidOperationException("Halo lens has no separate front surface: " + original.name);
            Mesh result = Object.Instantiate(original);
            result.name = original.name + " / Halo front and housing";
            result.subMeshCount = 2; result.SetTriangles(rest, 0); result.SetTriangles(front, 1);
            result.bounds = original.bounds;
            return result;
        }

        private void Awake()
        {
            ownedFronts = new Material[Lenses.Length];
            idleKeywords = new string[Lenses.Length][];
            for (int i = 0; i < Lenses.Length; i++)
            {
                if (Lenses[i] == null) continue;
                Material[] materials = Lenses[i].sharedMaterials;
                Material front = new Material(materials[1]) { name = "Resolute Halo aperture / live front" };
                ownedFronts[i] = front; idleKeywords[i] = front.shaderKeywords;
                materials[1] = front; Lenses[i].sharedMaterials = materials;
            }
        }

        private void LateUpdate()
        {
            ApplyState(Laser != null && Laser.isActiveAndEnabled && Beam != null &&
                Beam.enabled && Beam.gameObject.activeInHierarchy);
        }

        internal void ApplyState(bool firing)
        {
            if (applied == firing) return;
            applied = firing;
            for (int i = 0; i < Lenses.Length; i++)
            {
                if (Lenses[i] == null) continue;
                // Damage feedback can lazily instantiate the renderer's
                // materials. Read the current front only on transitions so
                // its damage tint survives and an obsolete clone is never lit.
                Material front = Lenses[i].sharedMaterials[1];
                front.shaderKeywords = firing ? FiringKeywords : idleKeywords[i];
                front.SetColor(Emission, firing ? FiringEmission : Color.black);
            }
        }

        private void OnDisable() { if (ownedFronts != null) ApplyState(false); }

        private void OnDestroy()
        {
            // Detached turret visuals can survive with the ship's existing
            // damage debris. Match their material cleanup grace period.
            foreach (Material front in ownedFronts ?? new Material[0])
                if (front != null) Object.Destroy(front, 76f);
        }
    }
}
