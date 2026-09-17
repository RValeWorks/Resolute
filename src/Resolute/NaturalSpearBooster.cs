using System;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    internal sealed class NaturalSurfaceLaunch : MonoBehaviour
    {
        public VLSBooster Booster;
        public string DonorKey;
        public float GuidanceDelay, FinDelay, TurnRate, GLimit, BurnTime, IgnitionDelay, Thrust, FuelMass;
        private Missile missile;
        private NaturalWeaponPhase phase;
        private Renderer[] pikeBoosterRenderers;
        private bool[] originalRendererEnabled;

        private void Awake()
        {
            missile = GetComponent<Missile>(); phase = GetComponent<NaturalWeaponPhase>();
            if (missile == null || missile.definition == null ||
                (missile.definition.jsonKey != "rsl_ashm" && missile.definition.jsonKey != "rsl_cruise") || Booster == null) return;
            pikeBoosterRenderers = Booster.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
            originalRendererEnabled = pikeBoosterRenderers.Select(r => r.enabled).ToArray();
            UpdateBoosterVisual();
        }

        private void LateUpdate() { UpdateBoosterVisual(); }

        private void UpdateBoosterVisual()
        {
            if (pikeBoosterRenderers == null || Booster == null) return;
            // The authored launch pose already contains its booster, including
            // its cleared-fin pose. Reveal the fitted native body when the
            // source pose hides it or Burnout detaches it. Never hide particles.
            bool visible = phase == null || phase.LaunchModel == null || !phase.LaunchModel.activeSelf ||
                !Booster.transform.IsChildOf(transform);
            for (int i = 0; i < pikeBoosterRenderers.Length; i++)
                if (pikeBoosterRenderers[i] != null) pikeBoosterRenderers[i].enabled = visible && originalRendererEnabled[i];
        }

        internal object Capture()
        {
            Missile owner = missile != null ? missile : GetComponent<Missile>();
            return new {
                donor = DonorKey, boosterExists = Booster != null,
                attached = owner != null && owner.boosterIsAttached,
                childOfMissile = Booster != null && Booster.transform.IsChildOf(transform),
                activated = Booster != null && (bool)NaturalWeapons.Get(Booster, "activated"),
                separated = Booster != null && (bool)NaturalWeapons.Get(Booster, "separated"),
                remainingFuelKg = Booster != null ? (float)NaturalWeapons.Get(Booster, "fuelMass") : 0f,
                configuredBurnSeconds = BurnTime,
                positionRelativeToMissile = Booster != null ? V(transform.InverseTransformPoint(Booster.transform.position)) : null,
                localScale = Booster != null ? V(Booster.transform.localScale) : null,
                worldScale = Booster != null ? V(Booster.transform.lossyScale) : null,
                modelRenderers = Booster != null ? Booster.GetComponentsInChildren<Renderer>(true)
                    .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).Select(r => new {
                        name = r.name, enabled = r.enabled, active = r.gameObject.activeInHierarchy,
                        mesh = r.GetComponent<MeshFilter>() != null && r.GetComponent<MeshFilter>().sharedMesh != null ? r.GetComponent<MeshFilter>().sharedMesh.name : null,
                        worldCenter = V(r.bounds.center), worldSize = V(r.bounds.size) }).ToArray() : null,
                particles = Booster != null ? Booster.GetComponentsInChildren<ParticleSystem>(true).Select(p => new {
                    name = p.name, playing = p.isPlaying, emitting = p.isEmitting, count = p.particleCount,
                    active = p.gameObject.activeInHierarchy, loop = p.main.loop, duration = p.main.duration,
                    positionRelativeToBooster = V(Booster.transform.InverseTransformPoint(p.transform.position)),
                    localScale = V(p.transform.localScale), worldScale = V(p.transform.lossyScale) }).ToArray() : null
            };
        }

        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

        internal void Validate()
        {
            // Native Missile.ApplyAero disables its explicit rate/g limiter
            // when both caps are zero. Zero is a real donor setting, not a
            // missing launch profile. Native delay comparisons also permit
            // immediate activation; require finite values, not positive ones.
            if (Booster == null || !Finite(GuidanceDelay) || !Finite(FinDelay) || !Finite(TurnRate) || !Finite(GLimit))
                throw new InvalidOperationException("Invalid native surface launch profile: donor=" + DonorKey +
                    ", booster=" + (Booster != null) + ", guidanceDelay=" + GuidanceDelay +
                    ", finDelay=" + FinDelay + ", turnRate=" + TurnRate + ", gLimit=" + GLimit);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }

    internal static class NaturalSpearBooster
    {
        internal static void Configure(Missile missile, NaturalWeapons.SourceWeapon source, Missile cruiseDonor, Encyclopedia encyclopedia)
        {
            VLSBooster booster = missile.GetComponentInChildren<VLSBooster>(true);
            Missile boostDonor = cruiseDonor;
            if (booster == null)
            {
                boostDonor = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                    string.Equals(d.jsonKey, "AShM1", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.unitPrefab.GetComponent<Missile>()).FirstOrDefault(m => m != null && m.GetComponentInChildren<VLSBooster>(true) != null);
                if (boostDonor == null) throw new InvalidOperationException("Spear requires the native ship-launch booster from AShM1.");
                VLSBooster original = boostDonor.GetComponentInChildren<VLSBooster>(true);
                if (original.transform == boostDonor.transform)
                    throw new InvalidOperationException("Cannot detach the native missile root as a Spear booster.");
                GameObject copy = Object.Instantiate(original.gameObject, missile.transform, false);
                copy.name = "NativeSpearVlsBooster";
                booster = copy.GetComponent<VLSBooster>();
                copy.transform.localRotation = Quaternion.Inverse(boostDonor.transform.rotation) * original.transform.rotation;
            }
            if (booster.transform == missile.transform || !booster.transform.IsChildOf(missile.transform))
                throw new InvalidOperationException("Spear booster must be an owned, detachable child.");
            float donorMass = (float)NaturalWeapons.Get(boostDonor, "mass");
            ScaleAndBind(booster, missile, source.massKg, donorMass);
            if (source.key == "rsl_ashm")
            {
                float seconds = source.Guidance("InitialFlightPhaseDuration", 3.05f);
                float nativeBurn = (float)NaturalWeapons.Get(booster, "burnTime");
                float nativeFuel = (float)NaturalWeapons.Get(booster, "fuelMass");
                NaturalWeapons.Set(booster, "fuelMass", PikeFlightProfile.BoosterFuel(nativeFuel, nativeBurn, seconds));
                NaturalWeapons.Set(booster, "burnTime", seconds);
                NaturalWeapons.Set(booster, "delayTimer", 0f);
            }
            float fuel = (float)NaturalWeapons.Get(booster, "fuelMass");
            float burn = (float)NaturalWeapons.Get(booster, "burnTime");
            float thrust = (float)NaturalWeapons.Get(booster, "thrust");
            booster.transform.localPosition = new Vector3(0f, 0f, source.stages["launch"].min[2]);
            if (source.key == "rsl_ashm" || source.key == "rsl_cruise") FitPikeBooster(booster, missile, source);
            foreach (TrailEmitter trail in booster.GetComponentsInChildren<TrailEmitter>(true))
                trail.rb = missile.GetComponent<Rigidbody>();
            OpticalSeekerCruiseMissile seeker = missile.GetComponent<OpticalSeekerCruiseMissile>();
            if (seeker != null) NaturalWeapons.Set(seeker, "booster", booster);
            OpticalSeekerCruiseMissile nativeSeeker = boostDonor.GetComponent<OpticalSeekerCruiseMissile>();
            if (nativeSeeker == null) throw new InvalidOperationException("Native surface launch donor has no cruise guidance profile.");
            var profile = missile.gameObject.AddComponent<NaturalSurfaceLaunch>();
            profile.Booster = booster; profile.DonorKey = boostDonor.definition != null ? boostDonor.definition.jsonKey : boostDonor.name;
            profile.GuidanceDelay = (float)NaturalWeapons.Get(nativeSeeker, "guidanceDelay");
            profile.FinDelay = (float)NaturalWeapons.Get(nativeSeeker, "finDelay");
            profile.TurnRate = (float)NaturalWeapons.Get(boostDonor, "maxTurnRate");
            profile.GLimit = (float)NaturalWeapons.Get(boostDonor, "gLimit");
            if (source.key == "rsl_ashm")
            {
                // The three-second stage includes the ship-clear turn. ARH
                // couples fin deployment to guidance, so deploy at the native
                // ship-clear guidance time rather than hold vertical for the
                // entire booster burn and climb hundreds of metres needlessly.
                profile.FinDelay = profile.GuidanceDelay;
                profile.TurnRate = source.Guidance("LaunchTurnRate", 50f);
                profile.GLimit = Mathf.Max((float)NaturalWeapons.Get(missile, "gLimit"),
                    profile.TurnRate * Mathf.Deg2Rad * source.speedMps / 9.81f);
            }
            profile.BurnTime = burn; profile.IgnitionDelay = (float)NaturalWeapons.Get(booster, "delayTimer");
            profile.Thrust = thrust; profile.FuelMass = fuel;
            profile.Validate();
            // Native Awake/onInitialize selects ship vs aircraft launch. Native
            // Missile.MotorThrust waits while this booster is attached; its
            // Burnout detaches itself and hands off to the native cruise motor.
        }

        internal static bool OwnsPikeBoosterVisual(Missile missile, Transform item)
        {
            if (missile == null || missile.definition == null || missile.definition.jsonKey != "rsl_ashm" || item == null) return false;
            NaturalSurfaceLaunch profile = missile.GetComponent<NaturalSurfaceLaunch>();
            return profile != null && profile.Booster != null &&
                (item == profile.Booster.transform || item.IsChildOf(profile.Booster.transform));
        }

        internal static bool OwnsSurfaceBoosterVisual(Missile missile, Transform item)
        {
            if (OwnsPikeBoosterVisual(missile, item)) return true;
            if (missile == null || missile.definition == null || missile.definition.jsonKey != "rsl_cruise" || item == null) return false;
            NaturalSurfaceLaunch profile = missile.GetComponent<NaturalSurfaceLaunch>();
            return profile != null && profile.Booster != null &&
                (item == profile.Booster.transform || item.IsChildOf(profile.Booster.transform));
        }

        private static void FitPikeBooster(VLSBooster booster, Missile missile, NaturalWeapons.SourceWeapon source)
        {
            // Fit the real donor assembly into the authored booster envelope,
            // between the retained body's nozzle and the launch model's tail.
            // Native mesh bounds are available even without CPU vertex data.
            bool found = false; Bounds bounds = new Bounds();
            foreach (MeshFilter filter in booster.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Matrix4x4 frame = booster.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                Bounds local = filter.sharedMesh.bounds;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 point = frame.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3(x, y, z)));
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                    else bounds.Encapsulate(point);
                }
            }
            float front = source.stages["flight"].min[2], tail = source.stages["launch"].min[2];
            if (!found || bounds.size.x <= .001f || bounds.size.y <= .001f || bounds.size.z <= .001f || front <= tail)
                throw new InvalidOperationException("Pike native booster has no fit-ready mesh bounds.");
            Vector3 scale = new Vector3(
                (source.stages["launch"].max[0] - source.stages["launch"].min[0]) / bounds.size.x,
                (source.stages["launch"].max[1] - source.stages["launch"].min[1]) / bounds.size.y,
                (front - tail) / bounds.size.z);
            booster.transform.localRotation = Quaternion.identity;
            booster.transform.localScale = scale;
            booster.transform.localPosition = new Vector3(-bounds.center.x * scale.x, -bounds.center.y * scale.y, front - bounds.max.z * scale.z);
        }

        internal static void ScaleAndBind(VLSBooster booster, Missile missile, float sourceMass, float donorMass)
        {
            float scale = sourceMass / donorMass;
            float fuel = (float)NaturalWeapons.Get(booster, "fuelMass") * scale;
            float burn = (float)NaturalWeapons.Get(booster, "burnTime");
            float thrust = (float)NaturalWeapons.Get(booster, "thrust") * scale;
            if (donorMass <= 0f || fuel <= 0f || fuel >= sourceMass || burn <= 0f || thrust <= 0f ||
                float.IsNaN(scale) || float.IsInfinity(scale) || float.IsNaN(fuel) || float.IsNaN(thrust))
                throw new InvalidOperationException("Spear native ship booster has invalid fuel, thrust or flight mass.");
            NaturalWeapons.Set(booster, "missile", missile);
            NaturalWeapons.Set(booster, "fuelMass", fuel);
            NaturalWeapons.Set(booster, "dryMass", (float)NaturalWeapons.Get(booster, "dryMass") * scale);
            NaturalWeapons.Set(booster, "thrust", thrust);
            NaturalWeapons.Set(booster, "burnRate", 0f); // Awake computes the scaled native fuel/burn rate.
            NaturalWeapons.Set(booster, "activated", false);
            NaturalWeapons.Set(booster, "separated", false);
            NaturalWeapons.Set(booster, "splashed", false);
        }
    }
}
