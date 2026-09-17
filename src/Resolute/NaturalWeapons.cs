using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // Separate definitions and prefabs retain source weapon identities. Native
    // effects, networking and seeker code are supplied by the installed game.
    internal static class NaturalWeapons
    {
        internal static readonly Dictionary<string, MissileDefinition> Definitions = new Dictionary<string, MissileDefinition>();
        internal static readonly Dictionary<string, WeaponInfo> Infos = new Dictionary<string, WeaponInfo>();
        internal static readonly Dictionary<string, string> NativeLoftDonors = new Dictionary<string, string>();
        private static GameObject visualBag;
        private static List<Object> constructing;
        private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> FieldCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();

#pragma warning disable 0649
        internal sealed class WeaponFile { public int schemaVersion; public SourceWeapon[] weapons; }
        internal sealed class SourceStage { public string node; public float[] min, max; }
        internal sealed class SourceWeapon
        {
            public string key, name, sourceRole, sourceDescription;
            public float massKg, speedMps, minRangeM, maxRangeM, maxAltitudeM, maxFlightTime, switchSeconds;
            public Dictionary<string, Dictionary<string, string>> source;
            public Dictionary<string, SourceStage> stages;
            internal string Text(string section, string key, string fallback = "")
            {
                Dictionary<string, string> group; string value;
                return source != null && source.TryGetValue(section, out group) && group.TryGetValue(key, out value) ? value : fallback;
            }
            internal float Number(string section, string key, float fallback = 0f)
            {
                float number;
                return float.TryParse(Text(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && !float.IsNaN(number) ? number : fallback;
            }
            internal float Guidance(string key, float fallback = 0f) { return Number("Guidance", key, fallback); }
            internal bool Underwater { get { return key == "rsl_torpedo" || key == "rsl_asw_payload"; } }
            internal bool Carrier { get { return key == "rsl_asw" || key == "rsl_fulmar"; } }
            internal bool Surface { get { return sourceRole.Contains("ASuW") || Underwater || Carrier; } }
        }
#pragma warning restore 0649

        internal static void EnsureRegistered(Encyclopedia encyclopedia, Transform inactiveRoot, ManualLogSource log)
        {
            // The null-context path deliberately completes inline for existing
            // diagnostic callers; production startup supplies the yielding context.
            EnsureRegisteredAsync(encyclopedia, inactiveRoot, log, null).GetAwaiter().GetResult();
        }

        internal static async UniTask EnsureRegisteredAsync(Encyclopedia encyclopedia, Transform inactiveRoot, ManualLogSource log,
            StartupLoadContext loading = null)
        {
            if (encyclopedia == null || inactiveRoot == null || inactiveRoot.gameObject.activeInHierarchy)
                throw new ArgumentException("Source weapons require a loaded encyclopedia and inactive template root.");
            if (Definitions.Count == 0)
            {
                if (loading != null) await loading.Step("Reading missile catalog");
                string folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets", "Weapons");
                var file = JsonConvert.DeserializeObject<WeaponFile>(File.ReadAllText(Path.Combine(folder, "weapons.json")));
                if (file == null || file.schemaVersion != 1 || file.weapons == null || file.weapons.Length != 13 ||
                    file.weapons.Select(w => w.key).Distinct().Count() != 13 || !file.weapons.Any(w => w.key == NaturalLanceFlight.Key))
                    throw new InvalidDataException("The Resolute catalog must contain its twelve original entries and Lance.");
                var missilePaint = new MissileVisuals(folder, encyclopedia);
                visualBag = await VisualLoader.LoadFromFolderAsync(folder, "ResoluteWeapons", inactiveRoot, log, missilePaint.CreateMaterial, loading);
                var pendingDefinitions = new Dictionary<string, MissileDefinition>();
                var pendingInfos = new Dictionary<string, WeaponInfo>();
                constructing = new List<Object>();
                try
                {
                    foreach (SourceWeapon source in file.weapons)
                    {
                        if (!NaturalArmament.IsEnabledWeapon(source.key)) continue;
                        if (loading != null) await loading.Step("Preparing " + source.name);
                        MissileDefinition donor = FindDonor(encyclopedia, source);
                        MissileDefinition definition; WeaponInfo info;
                        Build(source, donor, encyclopedia, inactiveRoot, out definition, out info);
                        NaturalSurfaceLaunch surfaceLaunch = definition.unitPrefab.GetComponent<NaturalSurfaceLaunch>();
                        if (surfaceLaunch != null)
                            log.LogInfo(source.name + " native VLS donor=" + surfaceLaunch.DonorKey + "; guidance delay=" + surfaceLaunch.GuidanceDelay.ToString(CultureInfo.InvariantCulture) +
                                ", fin delay=" + surfaceLaunch.FinDelay.ToString(CultureInfo.InvariantCulture) + ", turn=" + surfaceLaunch.TurnRate.ToString(CultureInfo.InvariantCulture) +
                                ", g limit=" + surfaceLaunch.GLimit.ToString(CultureInfo.InvariantCulture) +
                                ", burn=" + surfaceLaunch.BurnTime.ToString(CultureInfo.InvariantCulture) + ", ignition delay=" + surfaceLaunch.IgnitionDelay.ToString(CultureInfo.InvariantCulture) +
                                ", thrust=" + surfaceLaunch.Thrust.ToString(CultureInfo.InvariantCulture) + ", fuel=" + surfaceLaunch.FuelMass.ToString(CultureInfo.InvariantCulture) + ".");
                        if (source.key == "rsl_cruise")
                        {
                            NaturalSpearFlight flight = definition.unitPrefab.GetComponent<NaturalSpearFlight>();
                            log.LogInfo("Spear complete native cruise flight donor=" + donor.jsonKey + ", name=" + donor.unitName + ", code=" + donor.code +
                                "; native stages=" + flight.NativeMotorStages + ", turn=" + flight.NativeTurnRate.ToString(CultureInfo.InvariantCulture) +
                                ", fin area=" + flight.NativeFinArea.ToString(CultureInfo.InvariantCulture) + ", sustainer thrust=" + flight.NativeThrust.ToString(CultureInfo.InvariantCulture) +
                                ", mass scale=" + flight.MassScale.ToString(CultureInfo.InvariantCulture) + ". Authored art, mass, payload, range and speed ceiling retained.");
                        }
                        NaturalLoftGuidance loft = definition.unitPrefab.GetComponent<NaturalLoftGuidance>();
                        if (loft != null && loft.NativeAirDefense)
                        {
                            if (loft.NativeLoftAmount <= 0f)
                                log.LogWarning(source.key + ": native AAM2/SAM_Radar2 donors supplied no positive loft coefficient; long-range loft remains unresolved. See missile diagnostics.");
                            else log.LogInfo(source.key + ": native loft donor=" + loft.NativeLoftDonor + ", coefficient=" +
                                loft.NativeLoftAmount.ToString(CultureInfo.InvariantCulture) + "; radar activation distance=" +
                                Get(definition.unitPrefab.GetComponent<ARHSeeker>(), "terminalRange") + " m.");
                        }
                        pendingDefinitions.Add(source.key, definition);
                        pendingInfos.Add(source.key, info);
                    }
                    foreach (var pair in pendingDefinitions) Definitions.Add(pair.Key, pair.Value);
                    foreach (var pair in pendingInfos) Infos.Add(pair.Key, pair.Value);
                }
                catch
                {
                    foreach (Object item in constructing) if (item != null) Object.Destroy(item);
                    Definitions.Clear(); Infos.Clear(); NativeLoftDonors.Clear();
                    VisualLoader.DestroyFailedTemplate(visualBag);
                    visualBag = null;
                    throw;
                }
                finally { constructing = null; }
                log.LogInfo("Registered " + Definitions.Count + " enabled original Resolute missile templates.");
            }
            foreach (var pair in Definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                UnitDefinition existing;
                if (Encyclopedia.Lookup.TryGetValue(pair.Key, out existing) && existing != pair.Value)
                    throw new InvalidOperationException("Weapon definition key is already occupied: " + pair.Key);
                if (!encyclopedia.missiles.Contains(pair.Value)) encyclopedia.missiles.Add(pair.Value);
                Encyclopedia.Lookup[pair.Key] = pair.Value;
                int index = encyclopedia.IndexLookup.IndexOf(pair.Value);
                if (index < 0) { index = encyclopedia.IndexLookup.Count; encyclopedia.IndexLookup.Add(pair.Value); }
                ((INetworkDefinition)pair.Value).LookupIndex = index;
            }
        }

        private static MissileDefinition FindDonor(Encyclopedia encyclopedia, SourceWeapon source)
        {
            bool infrared = source.key == "rsl_pd" || source.key == "rsl_bmd_exo";
            var candidates = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                !d.jsonKey.StartsWith("rsl_", StringComparison.OrdinalIgnoreCase) && d.unitPrefab.GetComponent<Missile>() != null).ToArray();
            if (source.key == "rsl_cruise")
            {
                // Resolve the user's named native cruise missile from its
                // actual installed definition. Older captures identify the
                // native cruise prefab as CruiseMissile1 but omit its name.
                // AShM1 is a separate anti-ship donor, never a silent fallback.
                var cruise = candidates.Where(d => d.unitPrefab.GetComponent<OpticalSeekerCruiseMissile>() != null)
                    .OrderBy(d => d.jsonKey == "CruiseMissile1" ? 0 : 1).ToArray();
                MissileDefinition named = cruise.FirstOrDefault(d => IsAlcm450Name(d.unitName) || IsAlcm450Name(d.code));
                MissileDefinition selected = named ?? cruise.FirstOrDefault(d => string.Equals(d.jsonKey, "CruiseMissile1", StringComparison.OrdinalIgnoreCase));
                if (selected == null) throw new InvalidOperationException("Spear requires the native ALCM450/CruiseMissile1 optical cruise prefab.");
                return selected;
            }
            string wanted = source.Surface ? "AShM1" : infrared ? "AAM1" : "AAM2";
            var exact = candidates.FirstOrDefault(d => string.Equals(d.jsonKey, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.unitPrefab.name, wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var fallback = candidates.FirstOrDefault(d => source.Surface ? d.unitPrefab.GetComponent<OpticalSeekerCruiseMissile>() != null :
                infrared ? d.unitPrefab.GetComponent<IRSeeker>() != null : d.unitPrefab.GetComponent<ARHSeeker>() != null);
            if (fallback == null) throw new InvalidOperationException("No native missile systems donor for " + source.key);
            return fallback;
        }

        private static bool IsAlcm450Name(string name)
        {
            if (name == null) return false;
            string normalized = name.Replace("-", "").Replace(" ", "");
            return string.Equals(normalized, "ALCM450", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "ALMC450", StringComparison.OrdinalIgnoreCase);
        }

        private static void Build(SourceWeapon source, MissileDefinition donor, Encyclopedia encyclopedia, Transform inactiveRoot,
            out MissileDefinition definition, out WeaponInfo info)
        {
            definition = Object.Instantiate(donor);
            constructing.Add(definition);
            definition.name = source.key;
            definition.jsonKey = source.key;
            definition.unitName = source.name;
            definition.code = source.name;
            definition.bogeyName = source.Underwater ? "Torpedo" : "Missile";
            definition.description = Description(source);
            definition.dontAutomaticallyAddToEncyclopedia = false;
            definition.IsObstacle = false;
            definition.mass = source.massKg;
            definition.radarSize = Mathf.Max(0.00001f, source.Number("SensorData", "RCS", donor.radarSize));
            Set(definition, "mass", null, typeof(MissileDefinition)); // discard the donor's private nullable mass cache
            definition.value = Mathf.Max(1f, source.Number("General", "AmmoPoints", 200f) * 0.01f);
            var stage = source.stages["launch"];
            definition.width = Mathf.Max(stage.max[0] - stage.min[0], stage.max[1] - stage.min[1]);
            definition.height = stage.max[1] - stage.min[1];
            definition.length = stage.max[2] - stage.min[2];
            definition.roleIdentity = Role(source);
            definition.typeIdentity = new TypeIdentity(0f, 0f, 1f, 0f, 0f);
            Set(definition, "disabled", false, typeof(UnitDefinition));
            Set(definition, "isEventContent", false, typeof(UnitDefinition));
            GameObject prefab = Object.Instantiate(donor.unitPrefab, inactiveRoot, false);
            constructing.Add(prefab);
            prefab.name = source.key;
            prefab.SetActive(true);
            prefab.transform.localPosition = Vector3.zero;
            prefab.transform.localRotation = Quaternion.identity;
            prefab.transform.localScale = Vector3.one;
            definition.unitPrefab = prefab;
            Missile missile = prefab.GetComponent<Missile>();
            missile.definition = definition;
            missile.boosterIsAttached = false;
            info = Object.Instantiate(donor.unitPrefab.GetComponent<Missile>().GetWeaponInfo());
            constructing.Add(info);
            info.name = source.key + "_info";
            info.weaponName = source.name;
            info.shortName = source.name;
            info.description = definition.description;
            info.weaponPrefab = prefab;
            info.massPerRound = source.massKg;
            info.costPerRound = definition.value;
            info.maxSpeed = Mathf.Max(source.speedMps, source.Guidance("TerminalVelocity", 0f) * 0.514444444f);
            info.muzzleVelocity = source.Underwater ? 12f : 60f;
            info.missile = true;
            info.gun = info.bomb = info.nuclear = info.energy = info.hideInDisplay = false;
            info.effectiveness = definition.roleIdentity;
            info.pK = Mathf.Clamp01(source.Number("WarheadData", "KillProbability", source.Surface ? 0.8f : 0.9f));
            info.overHorizon = source.Surface;
            info.fireInterval = source.key == "rsl_pd" ? 0.3f : source.key == "rsl_mrsam" ? .6f : 1f;
            info.targetRequirements = Requirements(source);
            Set(missile, "info", info);
            Set(missile, "mass", source.massKg);
            if (source.key != "rsl_cruise")
            {
                Set(missile, "maxTurnRate", source.Guidance("MaxTurnRate", 30f));
                float inferredTurnG = source.Guidance("MaxTurnRate", 30f) * Mathf.Deg2Rad * info.maxSpeed / 9.81f;
                Set(missile, "gLimit", source.Guidance("MaxTurnG", Mathf.Max(12f, inferredTurnG)));
                Set(missile, "foldingFins", new Missile.FoldingFin[0]);
            }
            foreach (string field in new[] { "armorProperties", "warhead", "PIDFactors" }) Set(missile, field, CopyManaged(Get(missile, field)));
            Set(Get(missile, "warhead"), "Armed", false);
            float blast = BlastYield(source);
            Set(missile, "blastYield", blast);
            if (blast > 200f)
            {
                // Native large-yield damage is carried by the explosion's
                // shockwave component. A small IR donor's 10-kg effect is not
                // the correct effect family for these larger warheads.
                Missile heavy = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                    string.Equals(d.jsonKey, "AShM1", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.unitPrefab.GetComponent<Missile>()).FirstOrDefault(m => m != null);
                if (heavy != null)
                    foreach (string effect in new[] { "airEffect", "armorEffect", "terrainEffect", "waterSurfaceEffect", "underwaterEffect" })
                        Set(Get(missile, "warhead"), effect, Get(Get(heavy, "warhead"), effect));
                if (!source.Surface) prefab.AddComponent<NaturalFragmentWarhead>().Yield = blast;
            }
            // Proximity and impact fusing are independent. Native unfused
            // missiles deliberately ricochet from terrain after a missed shot.
            Set(missile, "impactFuse", true);
            float pierce = source.key == NaturalLanceFlight.Key
                ? source.Number("WarheadData", "PierceDamage", source.Surface ? 500f : 120f)
                : source.Surface ? 500f : 120f;
            float impactFuseDelay = source.key == NaturalLanceFlight.Key
                ? Mathf.Max(0f, source.Number("WarheadData", "ImpactFuseDelay", 0f)) : 0f;
            Set(missile, "impactFuseDelay", impactFuseDelay);
            Set(missile, "pierceDamage", pierce);
            info.blastDamage = blast;
            info.pierceDamage = pierce;
            if (source.key == "rsl_cruise" || source.key == "rsl_ashm")
                NaturalSpearBooster.Configure(missile, source, donor.unitPrefab.GetComponent<Missile>(), encyclopedia);
            if (source.key == NaturalLanceFlight.Key)
                NaturalLanceFlight.ConfigurePropulsion(missile, source, encyclopedia);
            else if (source.key == "rsl_cruise")
                NaturalSpearFlight.Configure(missile, source, donor.unitPrefab.GetComponent<Missile>());
            else ConfigureMotors(missile, source, donor.unitPrefab.GetComponent<Missile>());
            if (source.key != NaturalLanceFlight.Key) NaturalWeaponEffects.Configure(missile, source);
            foreach (LODGroup lod in prefab.GetComponentsInChildren<LODGroup>(true)) Object.DestroyImmediate(lod);
            foreach (MeshRenderer renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                if (!NaturalSpearBooster.OwnsSurfaceBoosterVisual(missile, renderer.transform)) renderer.enabled = false;
            foreach (SkinnedMeshRenderer renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (!NaturalSpearBooster.OwnsSurfaceBoosterVisual(missile, renderer.transform)) renderer.enabled = false;
            foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                if (!NaturalSpearBooster.OwnsSurfaceBoosterVisual(missile, filter.transform)) filter.sharedMesh = null;
            foreach (Light light in prefab.GetComponentsInChildren<Light>(true)) light.enabled = false;
            foreach (ParticleSystem system in prefab.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main; main.playOnAwake = false;
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            foreach (AudioSource sound in prefab.GetComponentsInChildren<AudioSource>(true)) sound.playOnAwake = false;
            foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            var capsule = prefab.AddComponent<CapsuleCollider>();
            capsule.direction = 2;
            capsule.center = new Vector3((stage.min[0] + stage.max[0]) * 0.5f, (stage.min[1] + stage.max[1]) * 0.5f,
                (stage.min[2] + stage.max[2]) * 0.5f);
            capsule.radius = Mathf.Max(0.04f, definition.width * 0.5f);
            capsule.height = Mathf.Max(capsule.radius * 2f, definition.length);
            Rigidbody rigidbody = prefab.GetComponent<Rigidbody>();
            rigidbody.mass = source.massKg;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var body = new GameObject("OriginalWeaponGeometry"); body.transform.SetParent(prefab.transform, false);
            GameObject launch, flight;
            if (source.key == NaturalLanceFlight.Key)
            {
                var presentation = LanceVisuals.Attach(prefab, encyclopedia,
                    Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets", "Lance"));
                presentation.BindBooster(prefab.GetComponent<NaturalLanceFlight>().Booster);
                launch = new GameObject("LancePhaseLaunch"); launch.transform.SetParent(body.transform, false);
                flight = new GameObject("LancePhaseCruise"); flight.transform.SetParent(body.transform, false);
            }
            else
            {
                launch = CopyVisual(source.stages["launch"].node, body.transform);
                flight = CopyVisual(source.stages["flight"].node, body.transform);
            }
            launch.SetActive(true); flight.SetActive(false);
            var phase = prefab.AddComponent<NaturalWeaponPhase>();
            phase.LaunchModel = launch; phase.FlightModel = flight; phase.SwitchSeconds = source.switchSeconds;
            if (source.key != NaturalLanceFlight.Key) NaturalFinDeployment.Configure(phase, source.key, visualBag.transform);
            phase.Lifetime = source.maxFlightTime > 0 ? source.maxFlightTime : Mathf.Max(60f, source.maxRangeM / Mathf.Max(1f, source.speedMps) * 1.3f);
            if (source.key == "rsl_ashm")
            {
                phase.SwitchSeconds = prefab.GetComponent<NaturalSurfaceLaunch>().FinDelay;
                phase.Lifetime = Mathf.Max(phase.Lifetime, PikeFlightProfile.PoweredSeconds(source.maxRangeM, source.speedMps) + 30f);
                phase.PassiveHomeOnJamRange = source.Guidance("SeekerPassiveRange", 100f) * 1852f;
            }
            phase.CruiseSpeed = source.speedMps;
            phase.TerminalSpeed = source.Guidance("TerminalVelocity", 0f) * 0.514444444f;
            phase.TerminalRange = source.Guidance("TerminalApproachDist", 0f) * 1852f;
            phase.SeaSkim = source.key == "rsl_ashm" || source.key == "rsl_cruise";
            phase.SurfaceCapable = phase.SeaSkim || source.key == NaturalLanceFlight.Key;
            phase.NativeCruisePipeline = source.key == "rsl_cruise";
            phase.NativePrefabDonor = donor.jsonKey;
            // Every tangible custom missile has a physical capsule. Its edge
            // can contact terrain before the native centre-line sweep does.
            // Spear keeps native optical guidance, but needs the same contact
            // fallback to honour its immediate impact fuse without bouncing.
            phase.ContactFuse = !source.Underwater && !source.Carrier;
            phase.CruiseAltitude = source.Guidance("SeaSkimmingAlt", 40f) * 0.3048f;
            phase.TerminalAltitude = source.Guidance("TerminalAlt", 30f) * 0.3048f;
            if (source.key == NaturalLanceFlight.Key)
            {
                phase.SwitchSeconds = 0f;
                phase.NativeCruisePipeline = true;
                phase.Lifetime = 900f;
            }
            else NaturalWeaponEffects.FitNozzles(missile);
            ConfigureSeeker(prefab, missile, source, encyclopedia);
            if (source.key == "rsl_pd" || source.key == "rsl_mrsam") NaturalDartLaunch.Configure(missile, encyclopedia);
            NaturalLoftGuidance.Configure(prefab, source);
            // Spear keeps the complete cloned native cruise seeker. Pike alone
            // needs the authored ARH/terminal-boost/evasion adapter below.
            if (source.key == "rsl_ashm") NaturalCruiseTactics.Configure(prefab, source, encyclopedia);
            if (source.key == NaturalPikeCountermeasures.WeaponKey) NaturalPikeCountermeasures.Configure(prefab, source, encyclopedia);
            if (source.key != NaturalLanceFlight.Key) NaturalWeaponEffects.ConfigureAfterburner(missile, source, encyclopedia);
            if (source.key == "rsl_bmd" || source.key == "rsl_bmd_exo") NaturalReactionControl.Configure(prefab, source);
            ConfigureIdentities(prefab, source.key);
            Object.DontDestroyOnLoad(definition); Object.DontDestroyOnLoad(info);
        }

        private static GameObject CopyVisual(string node, Transform parent)
        {
            Transform source = visualBag.transform.Find("LOD0/" + node);
            if (source == null) throw new InvalidDataException("Missing source weapon geometry: " + node);
            var result = Object.Instantiate(source.gameObject, parent, false);
            result.name = node;
            return result;
        }

        private static RoleIdentity Role(SourceWeapon source)
        {
            if (source.Surface) return new RoleIdentity { antiSurface = 1f };
            if (source.sourceRole == "BMD") return new RoleIdentity { antiMissile = 1f };
            return new RoleIdentity { antiAir = 1f, antiMissile = 1f };
        }

        private static TargetRequirements Requirements(SourceWeapon source)
        {
            // Ballistic-only classification is enforced through the native
            // opportunity and launch paths. Retain the broad altitude envelope
            // so genuine ballistic contacts remain eligible through their arc.
            float maxAltitude = source.sourceRole == "BMD" ? 1000000f : source.maxAltitudeM;
            if (float.IsInfinity(maxAltitude)) maxAltitude = 1000000f;
            float minAltitude = source.sourceRole == "BMD" ? 0f : source.Guidance("MinAttackAltitude", 0f) * 0.3048f;
            if (float.IsInfinity(minAltitude)) minAltitude = 0f;
            return new TargetRequirements {
                minRange = source.minRangeM, maxRange = source.key == "rsl_bmd" ? NaturalBallisticSelection.TerminalSelectionRange : source.maxRangeM,
                minAltitude = source.Surface ? -25f : Mathf.Max(0f, minAltitude),
                maxAltitude = source.key == NaturalLanceFlight.Key ? 10000f : source.Surface ? 250f : maxAltitude,
                maxSpeed = source.Surface ? 60f : source.Guidance("MaxAttackVelocity", 12000f) * 0.514444444f,
                lineOfSight = source.key == "rsl_pd", minAlignment = -1f,
                minValue = source.sourceRole == "BMD" ? NaturalBallisticSelection.MinimumValue : 0f,
                minIR = source.key == "rsl_pd" ? 0.001f : 0f
            };
        }

        private static string Description(SourceWeapon source)
        {
            switch (source.key)
            {
                case "rsl_ashm": return "A heavy, supersonic anti-ship missile designed to overwhelm fleet defenses through coordinated attacks. Pike cruises at low altitude before accelerating for a sea-skimming terminal approach. An active radar seeker, shared targeting information and electronic countermeasures allow groups of missiles to search for ships and distribute their attacks across a formation.";
                case "rsl_cruise": return "A long-range cruise missile designed for precision strikes against ground installations, vehicles and ships. Spear combines inertial navigation with terrain-following flight and optical terminal guidance, approaching at low altitude before striking its target with a conventional explosive warhead.";
                case "rsl_scramjet": return "A hypersonic strike missile designed to engage moving ships and stationary ground targets. Lance climbs under rocket power before separating its booster and accelerating through a high-altitude approach. Datalink updates and an active radar seeker guide attacks on tracked targets, while inertial guidance supports strikes against fixed land coordinates. Paired evasive maneuvers diminish as the missile converges on its final approach.";
                case "rsl_lrsam": return "An active radar-guided, long-range surface-to-air missile designed to protect fleets against aircraft and incoming missiles. Sentinel uses a two-stage rocket motor and a lofted flight path to reach distant targets, receiving datalink updates before transitioning to its own radar seeker for terminal guidance.";
                case "rsl_mrsam": return "An active radar-guided, medium-range surface-to-air missile designed for rapid interception of aircraft and incoming missiles. Ward is ejected vertically before turning toward the target and igniting its motor. Datalink guidance supports the approach, while its onboard radar guides the final interception.";
                case "rsl_bastion": return "An active radar-guided, long-range surface-to-air missile designed to defend fleets against aircraft and incoming missiles. Bastion combines high speed with a lofted flight path to extend its reach, using datalink updates during the approach before its onboard radar takes over for the final interception.";
                case "rsl_bmd": return "A high-speed interceptor designed to destroy ballistic missiles during their terminal approach. Bastion-T combines datalink guidance with an active radar seeker to track descending threats. Reaction-control thrusters supplement aerodynamic steering, providing additional maneuvering authority during the final interception.";
                case "rsl_bmd_exo": return "A long-range interceptor designed to destroy ballistic missiles outside the atmosphere. Bastion-X uses inertial guidance and datalink updates during its approach before acquiring the target with an infrared seeker. Reaction-control thrusters provide precise maneuvering in the thin upper atmosphere and vacuum.";
                case "rsl_pd": return "A compact, infrared-guided surface-to-air missile designed for close-range defense against aircraft and incoming missiles. Dart is ejected from its launcher before turning toward the threat and accelerating into the interception. Its small size allows a substantial ready magazine, while infrared homing provides terminal guidance without radar illumination.";
                case "rsl_asw": return "Vertically launched torpedo carrier. Delivers a Minnow lightweight homing torpedo near a designated ship, extending underwater attack reach to 54 nautical miles. Cruise speed: 750 knots.";
                case "rsl_asw_payload": return "Lightweight homing torpedo carried by Gannet and Fulmar. Enters the water before pursuing a surface ship and striking its hull. Maximum running speed: 50 knots; nominal range: 8 nautical miles.";
                case "rsl_torpedo": return "Heavyweight homing torpedo for attacking surface ships. Launches from Resolute's trainable torpedo mounts and runs beneath the surface at up to 45 knots. Nominal range: 17.5 nautical miles.";
                case "rsl_fulmar": return "Tube-launched torpedo delivery vehicle. Flies toward a designated ship and releases a Minnow lightweight homing torpedo. Nominal reach: 54 nautical miles; maximum flight speed: 510 knots.";
                default: throw new InvalidDataException("Unknown original weapon description: " + source.key);
            }
        }

        private static float BlastYield(SourceWeapon source)
        {
            if (source.Carrier) return 0f;
            // Sea Power's Power is a game-specific damage rating, not kg TNT.
            // Map its proportion to a conventional-warhead mass budget; never
            // interpret WarheadType=1 in a BMD entry as a nuclear yield.
            float rating = source.Number("WarheadData", "Power", 10f);
            return Mathf.Clamp(rating * 4f, 2f, source.massKg * 0.35f);
        }

        private static void ConfigureMotors(Missile missile, SourceWeapon source, Missile nativeDonor)
        {
            Array originals = (Array)Get(missile, "motors");
            if (originals == null || originals.Length == 0) throw new InvalidOperationException("Native missile donor has no motor.");
            Type motorType = originals.GetType().GetElementType();
            bool cruise = source.Surface && !source.Underwater;
            bool nativeSurfaceLaunch = source.key == "rsl_ashm";
            bool staged = !nativeSurfaceLaunch && (cruise || !source.Surface && source.Guidance("SustainerAccelerationTime") > 0f);
            Array motors = Array.CreateInstance(motorType, staged ? 2 : 1);
            float burn = source.Guidance("AccelerationTime", 0f);
            if (source.Surface) burn = Mathf.Max(30f, source.maxRangeM / Mathf.Max(1f, source.speedMps) * 1.2f);
            if (nativeSurfaceLaunch) burn = PikeFlightProfile.PoweredSeconds(source.maxRangeM, source.speedMps);
            if (burn <= 0f) burn = 8f;
            // Preserve the native missile's aerodynamic area per unit mass.
            // The previous small constant left Ward with only 0.0147 m² and
            // could rotate its nose without turning its actual flight path.
            float nativeMass = Mathf.Max(1f, (float)Get(nativeDonor, "mass"));
            float finArea = Mathf.Max(.005f, source.massKg * (float)Get(nativeDonor, "finArea") / nativeMass);
            Set(missile, "finArea", finArea);
            float accelerationScale = source.Text("Guidance", "ApplyKinematics") == "True" ? 9.81f : 0.514444444f;
            float sustainingAcceleration = cruise ? source.Guidance("Acceleration", 6f) : staged ? source.Guidance("SustainerAcceleration", 4f) : source.Guidance("Acceleration", 10f);
            float sustainerAccelerationMps = source.key == "rsl_ashm" ? 35f : Mathf.Max(.5f, sustainingAcceleration) * accelerationScale;
            float authoredThrust = source.massKg * sustainerAccelerationMps;
            float nominalDrag = missile.GetDragCoef(.01f) * .5f * 1.225f * finArea * source.speedMps * source.speedMps;
            if (source.speedMps > 340f) nominalDrag *= 1f + (float)Get(missile, "supersonicDrag");
            float dragScale = Mathf.Min(1f, authoredThrust * .75f / Mathf.Max(1f, nominalDrag));
            AnimationCurve originalDrag = (AnimationCurve)Get(nativeDonor, "dragCurve");
            Keyframe[] dragKeys = originalDrag.keys;
            for (int n = 0; n < dragKeys.Length; n++)
            {
                dragKeys[n].value *= dragScale; dragKeys[n].inTangent *= dragScale; dragKeys[n].outTangent *= dragScale;
            }
            Set(missile, "dragCurve", new AnimationCurve(dragKeys) { preWrapMode = originalDrag.preWrapMode, postWrapMode = originalDrag.postWrapMode });
            for (int index = 0; index < motors.Length; index++)
            {
                object motor = CopyManaged(originals.GetValue(Mathf.Min(index, originals.Length - 1)));
                float duration = nativeSurfaceLaunch ? burn : cruise ? index == 0 ? Mathf.Clamp(source.Guidance("InitialFlightPhaseDuration", 3.05f), 3f, 6f) : burn :
                    index == 0 ? burn : source.Guidance("SustainerAccelerationTime", 10f);
                float acceleration = index == 0 ? source.Guidance("Acceleration", 10f) : source.Guidance("SustainerAcceleration", 4f);
                float accelerationMps = nativeSurfaceLaunch ? sustainerAccelerationMps : cruise ? index == 0 ? 45f : sustainerAccelerationMps :
                    Mathf.Max(.5f, acceleration) * accelerationScale;
                Set(motor, "thrust", source.massKg * accelerationMps);
                Set(motor, "burnTime", duration);
                Set(motor, "fuelMass", source.massKg * (nativeSurfaceLaunch ? .22f : cruise ? index == 0 ? .08f : .22f : staged ? index == 0 ? 0.22f : 0.18f : 0.3f));
                Set(motor, "topSpeed", source.speedMps);
                // Native MotorThrust already waits for booster separation.
                // Pike's optical donor delay would add a second unpowered
                // coast and expose it to ARH's early low-speed retirement.
                Set(motor, "delayTimer", source.Underwater ? 100000f : nativeSurfaceLaunch ? 0f : index == 0 ? 0.05f : 0f);
                Set(motor, "activated", false);
                Set(motor, "burnRate", 0f);
                if (cruise && !nativeSurfaceLaunch && index == 0)
                {
                    VLSBooster booster = missile.GetComponentInChildren<VLSBooster>(true);
                    if (booster != null)
                    {
                        foreach (string effectField in new[] { "particleSystems", "trailEmitters", "audioSources", "lights" })
                            Set(motor, effectField, Get(booster, effectField));
                        Set(motor, "startupSource", null);
                    }
                }
                motors.SetValue(motor, index);
            }
            if (nativeSurfaceLaunch)
            {
                float boosterFuel = (float)Get(missile.GetComponentInChildren<VLSBooster>(true), "fuelMass");
                float motorFuel = 0f;
                foreach (object motor in motors) motorFuel += (float)Get(motor, "fuelMass");
                if (boosterFuel + motorFuel >= source.massKg)
                    throw new InvalidOperationException("Pike native booster and sustainer leave no flight mass.");
            }
            Set(missile, "motors", motors);
        }

        private static void ConfigureSeeker(GameObject prefab, Missile missile, SourceWeapon source, Encyclopedia encyclopedia)
        {
            if (source.key == "rsl_cruise")
            {
                // Keep the selected native cruise prefab's actual seeker
                // and cloned object references intact: native Initialize/Seek
                // own registration, terrain waypoints, part selection and the
                // terminal transition. A disabled helper running only one of
                // those methods is not the native cruise-missile lifecycle.
                OpticalSeekerCruiseMissile cruise = prefab.GetComponent<OpticalSeekerCruiseMissile>();
                if (cruise == null) throw new InvalidOperationException("Spear requires the complete native optical cruise seeker.");
                foreach (MissileSeeker other in prefab.GetComponents<MissileSeeker>())
                    if (other != cruise) Object.DestroyImmediate(other);
                Set(cruise, "missile", missile, typeof(MissileSeeker));
                cruise.proximityFuse = false;
                cruise.triggerMissileWarning = true;
                return;
            }
            foreach (MissileSeeker old in prefab.GetComponents<MissileSeeker>()) Object.DestroyImmediate(old);
            MissileSeeker seeker;
            if (source.Underwater || source.Carrier)
            {
                var maritime = prefab.AddComponent<NaturalMaritimeSeeker>();
                maritime.Underwater = source.Underwater;
                maritime.DeliversTorpedo = source.Carrier;
                maritime.RunningSpeed = source.speedMps;
                maritime.TurnRate = source.Guidance("MaxTurnRate", 20f);
                maritime.SeekerRange = source.Guidance("SeekerActiveRange", 2.5f) * 1852f;
                maritime.FlightAltitude = source.key == "rsl_fulmar" ? 80f : 61f;
                maritime.ReleaseRange = source.key == "rsl_fulmar" ? 1300f : 1800f;
                seeker = maritime;
            }
            else
            {
                bool infrared = source.key == "rsl_pd" || source.key == "rsl_bmd_exo";
                Type seekerType = infrared ? typeof(IRSeeker) : typeof(ARHSeeker);
                string seekerKey = infrared ? "AAM1" : "AAM2";
                Component donor = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null && !d.jsonKey.StartsWith("rsl_"))
                    .OrderBy(d => string.Equals(d.jsonKey, seekerKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .Select(d => d.unitPrefab.GetComponent(seekerType)).FirstOrDefault(c => c != null);
                if (donor == null) throw new InvalidOperationException("No native seeker donor: " + seekerType.Name);
                seeker = (MissileSeeker)prefab.AddComponent(source.key == "rsl_bmd_exo" ? typeof(NaturalExoInfraredSeeker) : seekerType);
                CopySerialized(donor, seeker);
                if (infrared)
                {
                    Set(seeker, "guidanceDelay", source.key == "rsl_pd" ? 0.25f : 0.4f);
                    Set(seeker, "tangibleDelay", 0.4f);
                    Set(seeker, "selfDestructAtSpeed", 60f);
                    Set(seeker, "flareRejection", source.Guidance("AntiCountermeasuresBonus", 0.65f));
                    if (seeker is NaturalExoInfraredSeeker exo)
                    {
                        exo.AcquisitionRange = source.Guidance("SeekerPassiveRange", 10f) * 1852f;
                        exo.TrackingHalfAngle = source.Guidance("SeekerGimbalFOV", 110f) * .5f;
                    }
                }
                else
                {
                    Set(seeker, "guidanceDelay", source.key == "rsl_ashm" ? prefab.GetComponent<NaturalSurfaceLaunch>().GuidanceDelay : 0.35f);
                    Set(seeker, "armDelay", 0.6f);
                    Set(seeker, "homeOnJam", source.Text("Guidance", "SecondaryPassiveRadarGuidanceType").Length > 0);
                    Set(seeker, "maxTrackingAngle", source.Guidance("SeekerGimbalFOV", 120f) * 0.5f);
                    Set(seeker, "maxDatalinkAngle", 180f);
                    Set(seeker, "minReacquireRange", 100f);
                    // These three conventional SAMs use their actual native
                    // donor's activation distance copied above. Detection range
                    // remains source-authored; T/X retain their existing paths.
                    if (!UsesNativeAirDefenseProfile(source.key))
                        Set(seeker, "terminalRange", source.key == "rsl_ashm" ? ResolutePikeMap.RadarRangeMetres :
                            Mathf.Max(2000f, source.Guidance("SeekerActiveRange", 14f) * 1852f));
                    Set(seeker, "jamTolerance", source.Guidance("AntiJammerBonus", 0.6f));
                    Set(seeker, "selfDestructAtSpeed", 60f);
                    if (source.key == "rsl_ashm" || source.key == NaturalLanceFlight.Key)
                    {
                        // ARH has an independent pre-terminal jink. Pike's
                        // prescribed swerve begins only after group release;
                        // clone before changing so the donor remains intact.
                        object jink = CopyManaged(Get(seeker, "jinkEvasion"));
                        Set(jink, "amount", 0f); Set(seeker, "jinkEvasion", jink);
                    }
                    // Conventional SAMs retain the donor's native loft law;
                    // the profile only tapers it near terminal interception.
                    // Specialized T/X still use their authored loft adapter.
                    if (!UsesNativeAirDefenseProfile(source.key)) Set(seeker, "loftAmount", 0f);
                    else
                    {
                        NativeLoftDonors[source.key] = donor.name;
                        if ((float)Get(seeker, "loftAmount") <= 0f)
                        {
                            // A non-lofting AAM prefab must not silently mask
                            // the long-range SAM issue. Reuse the installed
                            // native fleet SAM coefficient if it supplies one.
                            // Never invent a value when neither donor lofts.
                            ARHSeeker sam = encyclopedia.missiles.Where(d => d != null && d.unitPrefab != null &&
                                string.Equals(d.jsonKey, "SAM_Radar2", StringComparison.OrdinalIgnoreCase))
                                .Select(d => d.unitPrefab.GetComponent<ARHSeeker>()).FirstOrDefault(s => s != null && (float)Get(s, "loftAmount") > 0f);
                            if (sam != null)
                            {
                                Set(seeker, "loftAmount", (float)Get(sam, "loftAmount"));
                                NativeLoftDonors[source.key] = sam.name;
                            }
                        }
                        // Player-requested coefficient increase for Sentinel
                        // and standard Bastion only; Ward and T/X are unchanged.
                        if (source.key == "rsl_lrsam" || source.key == "rsl_bastion")
                            Set(seeker, "loftAmount", (float)Get(seeker, "loftAmount") + .2f);
                    }
                    if (source.Surface) Set(seeker, "datalinkPositionalError", source.Guidance("CircularErrorRadiusInstallation", 6f));
                    RadarParams radar = (RadarParams)Get(seeker, "radarParameters");
                    radar.maxRange = source.key == "rsl_ashm" ? ResolutePikeMap.RadarRangeMetres :
                        Mathf.Max(2000f, source.Guidance("SeekerActiveRange", 14f) * 1852f);
                    // A modest sensitivity improvement for weak ship returns.
                    // Range, horizon, LOS, jamming and the acquisition cone
                    // still apply; no contact or receiver lock is guaranteed.
                    if (source.key == "rsl_ashm") radar.minSignal *= .85f;
                    radar.clutterFactor *= source.Surface ? 0.35f : 1f;
                    Set(seeker, "radarParameters", radar);
                }
            }
            Set(seeker, "missile", missile, typeof(MissileSeeker));
            seeker.proximityFuse = !source.Surface;
            seeker.triggerMissileWarning = !source.Underwater;
        }

        internal static bool UsesNativeAirDefenseProfile(string key)
        {
            return key == "rsl_lrsam" || key == "rsl_mrsam" || key == "rsl_bastion";
        }

        private static void ConfigureIdentities(GameObject prefab, string key)
        {
            NetworkIdentity[] identities = prefab.GetComponentsInChildren<NetworkIdentity>(true);
            var own = new HashSet<NetworkIdentity>(identities);
            var occupied = new HashSet<int>(Resources.FindObjectsOfTypeAll<NetworkIdentity>().Where(n => n != null && !own.Contains(n)).Select(n => n.PrefabHash));
            foreach (NetworkIdentity identity in identities)
            {
                string path = identity.transform == prefab.transform ? "root" : AnimationUtilityPath(identity.transform, prefab.transform);
                int hash = StableHash(Plugin.Id + ":" + key + ":" + path);
                if (hash == 0 || !occupied.Add(hash)) throw new InvalidOperationException("Source weapon identity collision: " + key);
                identity.ClearSceneId(); identity.PrefabHash = hash;
                Set(identity, "_hasSpawned", false); identity.ClearNetworkBehaviourCache();
            }
        }

        private static string AnimationUtilityPath(Transform transform, Transform root)
        {
            var path = new List<string>();
            for (Transform t = transform; t != root; t = t.parent) path.Add(t.name + "[" + t.GetSiblingIndex() + "]");
            path.Reverse(); return string.Join("/", path);
        }
        private static int StableHash(string text)
        {
            unchecked { uint hash = 2166136261; foreach (char c in text) { hash ^= c; hash *= 16777619; } return (int)hash; }
        }
        internal static object Get(object owner, string name, Type declaring = null)
        {
            return CachedField(declaring ?? owner.GetType(), name).GetValue(owner);
        }
        internal static void Set(object owner, string name, object value, Type declaring = null)
        {
            CachedField(declaring ?? owner.GetType(), name).SetValue(owner, value);
        }
        private static FieldInfo CachedField(Type type, string name)
        {
            Dictionary<string, FieldInfo> fields;
            if (!FieldCache.TryGetValue(type, out fields)) FieldCache.Add(type, fields = new Dictionary<string, FieldInfo>());
            FieldInfo field;
            if (!fields.TryGetValue(name, out field))
            {
                field = AccessTools.Field(type, name);
                if (field == null) throw new MissingFieldException(type.FullName, name);
                fields.Add(name, field);
            }
            return field;
        }
        internal static object CopyManaged(object value)
        {
            if (value == null) return null;
            Type type = value.GetType();
            if (type.IsValueType || type == typeof(string) || value is Object) return value;
            AnimationCurve curve = value as AnimationCurve;
            if (curve != null) return new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
            Array array = value as Array;
            if (array != null) { Array copy = Array.CreateInstance(type.GetElementType(), array.Length); for (int i = 0; i < array.Length; i++) copy.SetValue(CopyManaged(array.GetValue(i)), i); return copy; }
            object clone = typeof(object).GetMethod("MemberwiseClone", Fields).Invoke(value, null);
            foreach (FieldInfo field in type.GetFields(Fields))
                if (!field.IsStatic && !field.IsInitOnly && !typeof(Delegate).IsAssignableFrom(field.FieldType)) field.SetValue(clone, CopyManaged(field.GetValue(value)));
            return clone;
        }
        private static void CopySerialized(Component source, Component target)
        {
            for (Type type = source.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
                        field.SetValue(target, CopyManaged(field.GetValue(source)));
        }
    }

    internal sealed class NaturalWeaponPhase : MonoBehaviour
    {
        public GameObject LaunchModel, FlightModel;
        public MeshFilter LaunchMesh;
        public Mesh ClearedLaunchMesh;
        public float SwitchSeconds, Lifetime, CruiseSpeed, TerminalSpeed, TerminalRange, CruiseAltitude, TerminalAltitude;
        public float PassiveHomeOnJamRange;
        public bool SeaSkim, SurfaceCapable, ContactFuse, NativeCruisePipeline;
        public string NativePrefabDonor;
        internal bool TerminalBoostActive;
        private Missile missile;
        private IRSeeker infrared;
        private NaturalCruiseTactics tactics;
        private NaturalCruiseGuidance cruiseGuidance;
        private NaturalSurfaceLaunch surfaceLaunch;
        private bool switched;
        internal bool FlightModelVisible => switched;
        private bool visualFinsDeployed;
        private float[] originalThrust;
        private Array motors;
        private bool speedProfileApplied;
        private float currentSpeedLimit;
        internal float MotorSpeedLimit => speedProfileApplied ? currentSpeedLimit : CruiseSpeed;
        internal void SetMotorSpeedLimit(float ceiling)
        {
            if (motors == null || !PikeFlightProfile.Finite(ceiling) || ceiling <= 0f || ceiling == currentSpeedLimit) return;
            currentSpeedLimit = ceiling;
            foreach (object motor in motors) NaturalWeapons.Set(motor, "topSpeed", ceiling);
        }
        private static readonly AccessTools.FieldRef<Missile, GlobalPosition> AimPoint = AccessTools.FieldRefAccess<Missile, GlobalPosition>("aimPoint");
        private static readonly AccessTools.FieldRef<Missile, Vector3> TargetVelocity = AccessTools.FieldRefAccess<Missile, Vector3>("targetVel");
        private static readonly AccessTools.FieldRef<Missile, float> CurrentFinArea = AccessTools.FieldRefAccess<Missile, float>("currentFinArea");
        private static readonly AccessTools.FieldRef<Missile, float> CurrentThrust = AccessTools.FieldRefAccess<Missile, float>("engineCurrentThrust");
        private static readonly AccessTools.FieldRef<Missile, float> Throttle = AccessTools.FieldRefAccess<Missile, float>("throttle");
        private static readonly AccessTools.FieldRef<Missile, bool> ReachedOnTarget = AccessTools.FieldRefAccess<Missile, bool>("reachedOnTarget");
        private static readonly AccessTools.FieldRef<Missile, bool> ImpactFuse = AccessTools.FieldRefAccess<Missile, bool>("impactFuse");
        private static readonly AccessTools.FieldRef<Missile, Unit> CollisionTarget = AccessTools.FieldRefAccess<Missile, Unit>("target");
        private static readonly System.Reflection.MethodInfo PenetrateObject = AccessTools.Method(typeof(Missile), "PenetrateObject");
        internal Unit ContactedUnit;
        internal float ContactTime;
        private void Awake()
        {
            missile = GetComponent<Missile>();
            infrared = GetComponent<IRSeeker>(); tactics = GetComponent<NaturalCruiseTactics>();
            cruiseGuidance = GetComponent<NaturalCruiseGuidance>();
            surfaceLaunch = GetComponent<NaturalSurfaceLaunch>();
            motors = (Array)NaturalWeapons.Get(missile, "motors");
            originalThrust = motors.Cast<object>().Select(m => (float)NaturalWeapons.Get(m, "thrust")).ToArray();
            NaturalMissileDiagnostics.Register(missile, this);
        }
        private void OnDestroy() { NaturalMissileDiagnostics.Unregister(missile); }
        private void FixedUpdate()
        {
            if (missile == null || missile.disabled) return;
            if (!switched && !visualFinsDeployed && ClearedLaunchMesh != null)
                visualFinsDeployed = NaturalFinDeployment.TryDeploy(missile, LaunchMesh, ClearedLaunchMesh);
            if (missile.LocalSim && missile.timeSinceSpawn > .6f && infrared != null &&
                (missile.owner == null || Vector3.Distance(transform.position, missile.owner.transform.position) > 60f))
            {
                // Native IR donors begin armed; these safe cloned templates do
                // not. Arm after separation just as the radar variants do.
                missile.SetTangible(true); missile.Arm();
            }
            if (!switched && missile.timeSinceSpawn >= SwitchSeconds &&
                (!NativeCruisePipeline || !missile.boosterIsAttached))
            {
                switched = true; LaunchModel.SetActive(false); FlightModel.SetActive(true);
                // Native optical Seek owns Spear's fin delay and arming;
                // its source pose must also retain the booster until native
                // Burnout detaches it, on both local and remote simulation.
                if (!NativeCruisePipeline) missile.DeployFins();
            }
            if (missile.LocalSim && missile.timeSinceSpawn > Lifetime && !ResoluteStrikeOrders.IsAreaMissile(missile))
                missile.Detonate(transform.forward, false, false);
        }
        private void OnCollisionEnter(Collision collision) { Contact(collision); }
        private void OnCollisionStay(Collision collision) { Contact(collision); }
        private void Contact(Collision collision)
        {
            if (!ContactFuse || missile == null || !missile.LocalSim || missile.disabled || !missile.IsTangible() || collision.contactCount == 0) return;
            UnitPart part = collision.collider.GetComponent<UnitPart>();
            Unit victim = part != null ? part.parentUnit : collision.collider.GetComponentInParent<Unit>();
            if (victim == missile || victim == missile.owner) return;
            // Match Missile.DetectCollisions: away from its close-target sweep,
            // a dynamic body travelling within 100 m/s does not trigger impact.
            // Unity contact callbacks must not override that native decision.
            Unit target = CollisionTarget(missile);
            Vector3 collisionAim = target != null && target.maxRadius < 20f ? target.transform.position : AimPoint(missile).ToLocalPosition();
            bool closeTarget = FastMath.InRange(transform.position, collisionAim, missile.speed * .25f);
            Rigidbody otherBody = collision.collider.attachedRigidbody;
            if (!closeTarget && otherBody != null && !otherBody.isKinematic &&
                FastMath.InRange(otherBody.velocity, missile.rb.velocity, 100f)) return;
            // A successful native penetration disables this fuse while its
            // delayed detonation is pending. The fallback must leave it alone.
            if (!ImpactFuse(missile)) return;
            ContactPoint contact = collision.GetContact(0);
            ContactedUnit = victim; ContactTime = Time.time;
            transform.position = contact.point + contact.normal * .1f;
            missile.rb.MovePosition(transform.position);
            IDamageable damageable = collision.collider.GetComponent<IDamageable>();
            if (damageable != null && missile.IsArmed() &&
                (bool)PenetrateObject.Invoke(missile, new object[] { damageable, contact.point, contact.normal }))
            {
                if (missile.definition?.jsonKey == NaturalLanceFlight.Key && !missile.rb.isKinematic)
                    missile.rb.velocity = Vector3.zero;
                return;
            }
            bool water = contact.point.y < Datum.LocalSeaY;
            bool terrain = !water && collision.collider.sharedMaterial == GameAssets.i.terrainMaterial;
            missile.Detonate(contact.normal, !water && !terrain, terrain);
        }
        internal void ApplySeaSkim()
        {
            if (!SeaSkim || missile == null || missile.disabled || !missile.LocalSim || missile.timeSinceSpawn < .35f) return;
            // A surviving group can supply a legitimate newly detected target
            // after a native clear. Do this before reading this frame's aim.
            missile.GetComponent<NaturalPikeGroupTargeting>()?.BeforeGuidance();
            GlobalPosition aim = AimPoint(missile);
            // Use the native seeker's observation for phase distances. Its
            // final steering aim may include lead or an off-gimbal waypoint.
            float range = cruiseGuidance != null ? cruiseGuidance.ObservedRange : (aim - missile.GlobalPosition()).magnitude;
            if (missile.targetID.NotValid && !ResoluteStrikeOrders.HasAreaObjective(missile))
            {
                // Preserve Pike's existing lost-target cleanup and release of
                // its cruise controller while avoiding a motor phase change.
                if (tactics != null) tactics.ModifyAim(aim);
                return;
            }
            NaturalCruiseController controller = tactics != null ? NaturalCruiseController.For(missile) : null;
            if (cruiseGuidance != null) cruiseGuidance.PrepareProfile(range);
            if (controller != null)
                controller.PrepareTerminal(range, cruiseGuidance != null && cruiseGuidance.NativeObservationUsable && PikeFlightProfile.Finite(range) && range > 0f,
                    !missile.boosterIsAttached && cruiseGuidance != null && cruiseGuidance.LaunchReady);
            bool terminalBoost = controller != null ? controller.TerminalReleased : !missile.boosterIsAttached &&
                (surfaceLaunch == null || missile.timeSinceSpawn >= surfaceLaunch.GuidanceDelay) &&
                TerminalSpeed > CruiseSpeed && range < TerminalRange;
            if (!speedProfileApplied || TerminalBoostActive != terminalBoost)
            {
                // All phase consumers, including Pike's native cruise release,
                // see the same transition as its actual motor this seek.
                TerminalBoostActive = terminalBoost; speedProfileApplied = true;
                float speed = terminalBoost ? TerminalSpeed : CruiseSpeed;
                currentSpeedLimit = speed;
                for (int index = 0; index < motors.Length; index++)
                {
                    object motor = motors.GetValue(index);
                    NaturalWeapons.Set(motor, "topSpeed", speed);
                    if (index > 0 || motors.Length == 1) NaturalWeapons.Set(motor, "thrust", originalThrust[index] * Mathf.Pow(speed / CruiseSpeed, 2f));
                }
            }
            if (TerminalBoostActive)
            {
                NaturalPikeTerminalAcceleration acceleration = missile.GetComponent<NaturalPikeTerminalAcceleration>();
                if (acceleration != null)
                    SetMotorSpeedLimit(acceleration.SpeedCeiling(Time.timeSinceLevelLoad, TerminalSpeed));
            }
            if (tactics != null) tactics.PrepareFlight(cruiseGuidance.NativeRadarPosition, TerminalBoostActive);
            if ((range > 1f || ResoluteStrikeOrders.HasAreaObjective(missile)) && cruiseGuidance != null)
            {
                aim = cruiseGuidance.Guide(aim);
                if (missile.disabled) return;
                if (tactics != null) aim = tactics.ApplyTerminalHeading(aim);
                aim = cruiseGuidance.StabilizeTerminalSteering(aim);
                missile.SetAimpoint(aim, TargetVelocity(missile));
            }
            if (controller != null && !TerminalBoostActive)
            {
                // The native motor uses topSpeed as a force cutoff. A genuine
                // rendezvous speed ceiling lets leading rounds slow down under
                // native drag while later rounds close the along-track gap.
                float ceiling = Mathf.Min(CruiseSpeed, controller.FormationSpeedCeiling);
                if (ceiling != currentSpeedLimit)
                {
                    SetMotorSpeedLimit(ceiling);
                }
            }
        }
        internal GlobalPosition CruiseAim(GlobalPosition destination, float altitude)
        {
            Vector3 horizontal = destination - missile.GlobalPosition(); horizontal.y = 0f;
            float lookAhead = Mathf.Min(horizontal.magnitude, Mathf.Max(250f, missile.speed * 3f));
            horizontal = horizontal.normalized;
            Vector3 velocity = missile.rb.velocity;
            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float wantedVerticalSpeed = Mathf.Clamp((altitude - missile.GlobalPosition().y) * .45f, -12f, 30f);
            float speedLimit = TerminalBoostActive ? TerminalSpeed : CruiseSpeed;
            float thrust = missile.speed < speedLimit ? CurrentThrust(missile) * Throttle(missile) : 0f;
            float thrustVerticalAcceleration = thrust / Mathf.Max(1f, missile.rb.mass) * transform.forward.y;
            float wantedLift = missile.rb.mass * Mathf.Clamp(9.81f - thrustVerticalAcceleration + (wantedVerticalSpeed - velocity.y) * 1.2f, -15f, 45f);
            float density = GameAssets.i.airDensityAltitude.Evaluate(missile.GlobalPosition().y * .001f);
            float pressure = Mathf.Max(1f, .5f * density * velocity.sqrMagnitude * CurrentFinArea(missile));
            float coefficient = Mathf.Abs(wantedLift) / pressure;
            float incidence = 30f * Mathf.Deg2Rad;
            for (int degree = 0; degree <= 30; degree++)
                if (Mathf.Abs(missile.GetLiftCoeff(degree * Mathf.Deg2Rad)) >= coefficient)
                {
                    float low = Mathf.Max(0, degree - 1) * Mathf.Deg2Rad, high = degree * Mathf.Deg2Rad;
                    for (int refine = 0; refine < 8; refine++)
                    {
                        float middle = (low + high) * .5f;
                        if (Mathf.Abs(missile.GetLiftCoeff(middle)) >= coefficient) high = middle; else low = middle;
                    }
                    incidence = (low + high) * .5f; break;
                }
            incidence *= Mathf.Sign(wantedLift);
            float velocityPitch = Mathf.Atan2(velocity.y, Mathf.Max(1f, horizontalSpeed));
            bool blendedSteering = ReachedOnTarget(missile);
            float pitch = Mathf.Clamp(velocityPitch + incidence * (blendedSteering ? .5f : 1f), -35f * Mathf.Deg2Rad, 45f * Mathf.Deg2Rad);
            return missile.GlobalPosition() + horizontal * lookAhead + Vector3.up * (Mathf.Tan(pitch) * lookAhead);
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), nameof(ARHSeeker.Seek))]
    internal static class NaturalSeaSkimPatch
    {
        private static void Postfix(ARHSeeker __instance)
        {
            NaturalWeaponPhase phase = __instance.GetComponent<NaturalWeaponPhase>();
            if (phase != null) phase.ApplySeaSkim();
        }
    }

    [HarmonyPatch(typeof(ARHSeeker), "GetRadarReturn")]
    internal static class NaturalSurfaceRadarPatch
    {
        private static bool Prefix(ARHSeeker __instance, ref float __result)
        {
            NaturalWeaponPhase phase = __instance.GetComponent<NaturalWeaponPhase>();
            Unit target = (Unit)NaturalWeapons.Get(__instance, "targetUnit", typeof(MissileSeeker));
            if (phase == null || !phase.SurfaceCapable || target == null || target is IRadarReturn) return true;
            // Native ships and installations do not implement IRadarReturn.
            // Supply their existing RCS to the same native radar equation so a
            // maritime ARH seeker can establish a real terminal track.
            Missile missile = __instance.GetComponent<Missile>();
            float previousTime = (float)NaturalWeapons.Get(__instance, "lastActiveTrackAttempt");
            if (Time.timeSinceLevelLoad - previousTime < .5f)
            { __result = (float)NaturalWeapons.Get(__instance, "returnStrength"); return false; }
            NaturalWeapons.Set(__instance, "lastActiveTrackAttempt", Time.timeSinceLevelLoad);
            __result = 0f;
            if (target.disabled || (bool)NaturalWeapons.Get(__instance, "isJammed") && !(bool)NaturalWeapons.Get(__instance, "homeOnJam")) return false;
            Vector3 delta = target.GlobalPosition() - missile.GlobalPosition();
            float distance = delta.magnitude;
            NaturalWeapons.Set(__instance, "targetDist", distance);
            // Share exactly the same native surface-return calculation with
            // the group leader's scan. This root seeker still owns its cache.
            __result = PikeGroupSeeker.SurfaceReturn(__instance, missile, target,
                (float)NaturalWeapons.Get(__instance, "returnStrength"));
            return false;
        }
    }

    [HarmonyPatch(typeof(Missile), nameof(Missile.GetTopSpeed))]
    internal static class NaturalWeaponSpeedPatch
    {
        private static bool Prefix(Missile __instance, ref float __result)
        {
            NaturalWeaponPhase phase = __instance.GetComponent<NaturalWeaponPhase>();
            if (phase == null) return true;
            __result = Mathf.Max(phase.CruiseSpeed, phase.TerminalSpeed);
            return false;
        }
    }

    [HarmonyPatch(typeof(EncyclopediaBrowser), "DisplayUnitInfo")]
    internal static class NaturalWeaponBrowserStatsPatch
    {
        private static void Postfix(EncyclopediaBrowser __instance, UnitDefinition definition)
        {
            if (!(definition is MissileDefinition) || definition.unitPrefab == null) return;
            NaturalWeaponPhase phase = definition.unitPrefab.GetComponent<NaturalWeaponPhase>();
            if (phase == null) return;
            // The vanilla browser substitutes delta-V when its inferred rocket
            // speed is lower. These weapons have an explicit propulsion cap.
            ((GameObject)NaturalWeapons.Get(__instance, "deltaVPanel")).SetActive(false);
            ((GameObject)NaturalWeapons.Get(__instance, "topSpeedPanel")).SetActive(true);
            ((TMP_Text)NaturalWeapons.Get(__instance, "topSpeed")).text = UnitConverter.SpeedReading(Mathf.Max(phase.CruiseSpeed, phase.TerminalSpeed));
            NaturalMaritimeSeeker maritime = definition.unitPrefab.GetComponent<NaturalMaritimeSeeker>();
            if (maritime != null && maritime.Underwater)
            {
                ((GameObject)NaturalWeapons.Get(__instance, "burnTimePanel")).SetActive(false);
                ((GameObject)NaturalWeapons.Get(__instance, "rcsPanel")).SetActive(false);
            }
            if (maritime != null && maritime.DeliversTorpedo)
                ((GameObject)NaturalWeapons.Get(__instance, "yieldPanel")).SetActive(false);
        }
    }

    // Surface-ship homing is explicit: this does not pretend the game has a
    // submarine database, acoustic propagation, or an underwater sensor model.
    internal sealed class NaturalMaritimeSeeker : MissileSeeker
    {
        public bool Underwater, DeliversTorpedo;
        public float RunningSpeed, TurnRate, SeekerRange, FlightAltitude, ReleaseRange;
        private GlobalPosition knownPosition;
        private bool enteredWater, delivered;
        private float searchTimer;
        public override string GetSeekerType() { return Underwater ? "Ship homing / underwater" : "INS / torpedo delivery"; }
        public override float GetMinSpeed() { return Underwater ? 2f : 40f; }
        public override void Initialize(Unit target, GlobalPosition aimpoint)
        {
            targetUnit = target; knownPosition = aimpoint;
            if (target != null && missile.NetworkHQ != null) missile.NetworkHQ.TryGetKnownPosition(target, out knownPosition);
            missile.SetAimpoint(knownPosition, Vector3.zero);
        }
        public override void Seek()
        {
            if (missile.disabled) return;
            if (targetUnit == null && missile.targetID.IsValid) missile.targetID.TryGetUnit(out targetUnit);
            if (targetUnit != null && missile.NetworkHQ != null && missile.NetworkHQ.TryGetKnownPosition(targetUnit, out var known)) knownPosition = known;
            if (missile.timeSinceSpawn > 0.75f && (missile.owner == null || Vector3.Distance(transform.position, missile.owner.transform.position) > 60f))
            { missile.SetTangible(true); missile.Arm(); }
            if (Underwater) return;
            if (missile.timeSinceSpawn > .35f) missile.DeployFins();
            GlobalPosition aim = knownPosition;
            Vector3 toTarget = aim - missile.GlobalPosition();
            float flatRange = new Vector2(toTarget.x, toTarget.z).magnitude;
            aim = GetComponent<NaturalWeaponPhase>().CruiseAim(aim, Mathf.Max(FlightAltitude, knownPosition.y + 20f));
            missile.SetAimpoint(aim, Vector3.zero);
            if (DeliversTorpedo && !delivered && missile.timeSinceSpawn > 4f && flatRange < ReleaseRange)
            {
                delivered = true;
                MissileDefinition payload;
                if (NaturalWeapons.Definitions.TryGetValue("rsl_asw_payload", out payload))
                {
                    Vector3 horizontal = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
                    Quaternion rotation = Quaternion.LookRotation((horizontal - Vector3.up * 0.6f).normalized);
                    NetworkSceneSingleton<Spawner>.i.SpawnMissile(payload, transform.position - Vector3.up * 2f,
                        rotation, horizontal * 35f - Vector3.up * 15f, targetUnit, missile);
                    NaturalWeapons.Set(NaturalWeapons.Get(missile, "warhead"), "Armed", false);
                    missile.Detonate(-transform.forward, false, false);
                }
            }
        }
        internal void WaterPhysics()
        {
            Seek();
            Rigidbody rb = missile.rb;
            if (transform.position.y > Datum.LocalSeaY - 0.5f && !enteredWater)
            {
                rb.useGravity = true;
                CheckImpact(rb.velocity * Time.fixedDeltaTime);
                return;
            }
            if (!enteredWater)
            {
                enteredWater = true;
                missile.RCS = 0f;
                rb.velocity = Vector3.Scale(rb.velocity, new Vector3(1f, 0f, 1f)).normalized * Mathf.Min(RunningSpeed, 12f);
                foreach (ParticleSystem system in GetComponentsInChildren<ParticleSystem>()) system.Stop();
                foreach (AudioSource source in GetComponentsInChildren<AudioSource>()) source.Stop();
            }
            rb.useGravity = false;
            rb.angularVelocity = Vector3.zero;
            Vector3 destination = knownPosition.ToLocalPosition();
            destination.y = Datum.LocalSeaY - 3f;
            if (targetUnit != null && !targetUnit.disabled && Vector3.Distance(targetUnit.transform.position, transform.position) < SeekerRange)
            {
                Vector3 targetVelocity = targetUnit.rb != null ? targetUnit.rb.velocity : Vector3.zero;
                float time = Vector3.Distance(targetUnit.transform.position, transform.position) / Mathf.Max(1f, RunningSpeed);
                destination = targetUnit.transform.position + targetVelocity * Mathf.Min(30f, time);
                destination.y = Datum.LocalSeaY - 3f;
            }
            else if (Vector3.Distance(destination, transform.position) < 100f)
            {
                searchTimer += Time.fixedDeltaTime;
                destination = transform.position + Quaternion.AngleAxis(Mathf.Sin(searchTimer * 0.2f) * 35f, Vector3.up) * transform.forward * 200f;
                destination.y = Datum.LocalSeaY - 3f;
            }
            Vector3 heading = (destination - transform.position).normalized;
            Vector3 current = rb.velocity.sqrMagnitude > 1f ? rb.velocity.normalized : transform.forward;
            heading = Vector3.RotateTowards(current, heading, TurnRate * Mathf.Deg2Rad * Time.fixedDeltaTime, 1f);
            float speed = Mathf.MoveTowards(rb.velocity.magnitude, RunningSpeed, 4f * Time.fixedDeltaTime);
            Vector3 velocity = heading * speed;
            CheckImpact(velocity * Time.fixedDeltaTime * 1.2f);
            if (!missile.disabled)
            {
                rb.velocity = velocity;
                Quaternion rotation = Quaternion.LookRotation(heading, Vector3.up);
                rb.MoveRotation(rotation);
                missile.SetAimpoint(destination.ToGlobalPosition(), Vector3.zero);
            }
        }
        private void CheckImpact(Vector3 displacement)
        {
            if (!missile.IsTangible() || displacement.sqrMagnitude < 0.000001f) return;
            CapsuleCollider capsule = GetComponent<CapsuleCollider>();
            Vector3 center = transform.TransformPoint(capsule.center);
            float halfSegment = Mathf.Max(0f, capsule.height * .5f - capsule.radius);
            RaycastHit[] hits = Physics.CapsuleCastAll(center - transform.forward * halfSegment, center + transform.forward * halfSegment,
                capsule.radius, displacement.normalized,
                displacement.magnitude + 0.5f, ~PhysicsLayers.ExclusionZonesMask.value, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(h => h.distance))
            {
                if (hit.collider.transform.IsChildOf(transform)) continue;
                Unit victim = hit.collider.GetComponentInParent<Unit>();
                if (victim == missile || victim == missile.owner) continue;
                Impact(hit.point, hit.normal, victim);
                break;
            }
        }
        private void OnCollisionEnter(Collision collision) { Contact(collision); }
        private void OnCollisionStay(Collision collision) { Contact(collision); }
        private void Contact(Collision collision)
        {
            if (!Underwater || missile == null || !missile.LocalSim || missile.disabled || !missile.IsTangible() || collision.contactCount == 0) return;
            Unit victim = collision.collider.GetComponentInParent<Unit>();
            if (victim == missile || victim == missile.owner) return;
            ContactPoint contact = collision.GetContact(0);
            Impact(contact.point, contact.normal, victim);
        }
        private void Impact(Vector3 point, Vector3 normal, Unit victim)
        {
            transform.position = point + normal * .1f;
            missile.rb.MovePosition(transform.position);
            missile.Arm();
            missile.Detonate(normal, victim != null, victim == null && point.y > Datum.LocalSeaY);
        }
    }

    [HarmonyPatch(typeof(Missile), "ServerFixedUpdate")]
    internal static class NaturalUnderwaterPhysicsPatch
    {
        private static bool Prefix(Missile __instance)
        {
            NaturalMaritimeSeeker seeker = __instance.GetComponent<NaturalMaritimeSeeker>();
            if (seeker == null || !seeker.Underwater) return true;
            seeker.WaterPhysics(); return false;
        }
    }

    [HarmonyPatch(typeof(Missile), "MotorThrust")]
    internal static class NaturalUnderwaterMotorPatch
    {
        private static bool Prefix(Missile __instance)
        {
            NaturalMaritimeSeeker seeker = __instance.GetComponent<NaturalMaritimeSeeker>();
            return seeker == null || !seeker.Underwater;
        }
    }
}
