using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Resolute
{
    // This class assembles a prefab only. Its inactive parent prevents native
    // Awake/OnEnable methods from registering a template as a live combat unit.
    internal static class ShipFactory
    {
        internal static GameObject Build(ShipDefinition donorDefinition, ShipDefinition definition,
            GameObject visualPrefab, Transform inactiveRoot, ManualLogSource log, string outfitKey)
            => BuildAsync(donorDefinition, definition, visualPrefab, inactiveRoot, log, outfitKey, null).GetAwaiter().GetResult();

        internal static async UniTask<GameObject> BuildAsync(ShipDefinition donorDefinition, ShipDefinition definition,
            GameObject visualPrefab, Transform inactiveRoot, ManualLogSource log, string outfitKey, StartupLoadContext loading = null)
        {
            if (donorDefinition == null || donorDefinition.unitPrefab == null)
                throw new ArgumentException("A vanilla ship prefab is required.");
            if (definition == null || visualPrefab == null)
                throw new ArgumentException("Resolute definition and visual asset are required.");
            if (inactiveRoot == null || inactiveRoot.gameObject.activeInHierarchy)
                throw new ArgumentException("The prefab staging parent must be inactive.");

            if (donorDefinition.jsonKey != "Destroyer1")
                throw new InvalidOperationException("This factory is configured for the audited Dynamo destroyer prefab.");

            string folder = Path.Combine(Path.GetDirectoryName(typeof(ShipFactory).Assembly.Location), "Assets");
            SourceAnchors anchors = JsonConvert.DeserializeObject<SourceAnchors>(File.ReadAllText(Path.Combine(folder, "weapon_anchors.json")));
            if (anchors == null || anchors.schemaVersion != 1 || anchors.banks == null || anchors.turrets == null ||
                anchors.banks.Sum(b => b.cells.Length) != 276)
                throw new InvalidDataException("The source weapon-anchor map is incomplete.");

            if (loading != null) await loading.Step("Preparing native ship systems");
            GameObject prefab = Object.Instantiate(donorDefinition.unitPrefab, inactiveRoot, false);
            try
            {
                prefab.name = "RSL_Resolute";
                prefab.SetActive(true);
                Ship ship = prefab.GetComponent<Ship>();
                if (ship == null) throw new InvalidOperationException("The donor root has no Ship component.");
                ship.definition = definition;
                definition.unitPrefab = prefab;

                Bounds targetBounds = RootBounds(visualPrefab.transform, RequireNamed(visualPrefab.transform, "rsl_hull").GetComponent<MeshFilter>());
                Bounds nativeBounds = BoundsOf(ship.GetComponentsInChildren<ShipPart>(true)
                    .Where(p => p.name != "turret_F")
                    .SelectMany(p => Corners(RootBounds(ship.transform, p.GetComponent<Collider>()))));
                Vector3 scale = new Vector3(targetBounds.size.x / nativeBounds.size.x, 1f, targetBounds.size.z / nativeBounds.size.z);
                Vector3 offset = targetBounds.center - Vector3.Scale(nativeBounds.center, scale);
                offset.y = 0f;
                if (loading != null) await loading.Step("Fitting hull compartments");
                ResizeNativeHull(ship, scale, offset);
                log.LogInfo("Resized native compartment coordinates: scale=" + scale + ", offset=" + offset + ".");
                HideNativeGeometry(prefab);

                if (loading != null) await loading.Step("Attaching ship model");
                GameObject visual = Object.Instantiate(visualPrefab, prefab.transform, false);
                visual.name = "ResoluteVisual";
                visual.SetActive(true);
                // Source Y=0 is the waterline. The vanilla editor places the ship
                // root spawnOffset.y metres above sea level.
                visual.transform.localPosition = new Vector3(0f, -donorDefinition.spawnOffset.y, 0f);
                foreach (Transform transform in visual.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = prefab.layer;
                var sourceNodes = visual.GetComponentsInChildren<Transform>(true)
                    .GroupBy(t => t.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
                var ownership = new Dictionary<UnitPart, List<Renderer>>();
                var weaponParts = new HashSet<UnitPart>();
                var gunMappings = new Dictionary<string, string>
                {
                    { "rsl_gun_yaw", "Hull_CF/Hull_CFF/turret_F" },
                    { "rsl_ciws_fp_yaw", "Hull_CF/Hull_FR/CIWS_FR" },
                    { "rsl_ciws_fs_yaw", "Hull_CF/Hull_FL/CIWS_FL" },
                    { "rsl_ciws_aft_yaw", "Hull_CR/Hull_hangarFloor/Hull_hangarR/CIWS_RR" },
                    { "rsl_ciws_al_yaw", "Hull_CR/Hull_hangarFloor/Hull_hangarL/CIWS_RL" }
                };
                foreach (var mapping in gunMappings)
                {
                    if (loading != null) await loading.Step("Fitting gun mounts");
                    Transform mount = prefab.transform.Find(mapping.Value);
                    if (mount == null || mount.GetComponent<Turret>() == null)
                        throw new InvalidOperationException("The audited weapon mount is missing: " + mapping.Value);
                    BindRig(ship, mount.GetComponent<Turret>(), anchors.turrets.Single(r => r.yaw == mapping.Key), sourceNodes, ownership, weaponParts);
                }
                if (loading != null) await loading.Step("Fitting defensive weapons");
                AddLasers(ship, anchors, sourceNodes, ownership, weaponParts, log);
                ConfigureMissiles(ship, visual.transform, anchors, sourceNodes, log);
                if (loading != null) await loading.Step("Connecting launchers and fire control");
                NaturalArmament.Configure(ship, visual.transform, ownership, weaponParts, log, outfitKey);
                ResoluteLiferafts.Prepare(ship, visual.transform);
                await AttachStaticVisualsAsync(ship, visual, ownership, weaponParts, log, loading);
                ResoluteLiferafts.Complete(ship);
                ResoluteShipEcm.Configure(ship, sourceNodes["rsl_nav_radar"]);
                ResoluteEsm.Configure(ship);
                RearMountFiringArcs.Configure(ship, visual.transform, sourceNodes, Path.Combine(folder, "weapon_anchors.json"));
                ConfigureFixedVlsControls(ship);
                ResoluteMagazineCookoff.Configure(ship);
                ship.gameObject.AddComponent<ResoluteCombatDiagnostics>();
                if (loading != null) await loading.Step("Connecting deck and ship systems");
                CarrierIntegration.Configure(ship, visual.transform, visualPrefab.transform);
                if (ship.radar != null && ship.radar.GetScanPoint() != null)
                    ship.radar.GetScanPoint().position = sourceNodes["rsl_nav_radar"].position;
                ResoluteSensors.Configure(ship);
                SetDisplacementMass(ship, 32000000f);
                ResoluteProtection.Configure(ship, donorDefinition.unitPrefab.GetComponent<Ship>(), weaponParts, 32000000f);

                ResoluteDeckAnimation animation = prefab.AddComponent<ResoluteDeckAnimation>();
                animation.Ship = ship;
                animation.Propulsion = ship.GetComponentInChildren<ShipPropulsion>(true);
                animation.Propellers = new[] { "rsl_prop_p", "rsl_prop_s", "rsl_prop_outer_p", "rsl_prop_outer_s" }.Select(n => sourceNodes[n]).ToArray();
                animation.Rudders = new[] { sourceNodes["rsl_rudder_1"], sourceNodes["rsl_rudder_2"] };
                animation.NavigationRadar = sourceNodes["rsl_nav_radar"];
                ScrewGeometry.Configure(animation);

                definition.length = targetBounds.size.z;
                definition.width = targetBounds.size.x;
                definition.height = 48f;
                definition.mass = 32000000f;
                definition.shipInfo.mass = 32000000f;
                definition.shipInfo.topSpeed = 35f * 1.852f;
                definition.manpower = 320f;
                definition.value = 3500f;
                if (loading != null) await loading.Step("Validating ship configuration");
                ValidatePrefab(prefab, ship);
                log.LogInfo("Resolute prefab assembled with source-aligned armament, fitted damage compartments, native flooding and an attached helicopter deck.");
                return prefab;
            }
            catch
            {
                VlsStructuralAttachments.DestroyFailedTemplate(prefab);
                Object.Destroy(prefab);
                throw;
            }
        }

#pragma warning disable 0649
        [Serializable] private sealed class SourceAnchors { public int schemaVersion; public SourceBank[] banks; public SourceRig[] turrets; }
        [Serializable] private sealed class SourceBank { public string id; public SourceCell[] cells; }
        [Serializable] private sealed class SourceCell
        {
            public string hatch, sourceWeapon, sourceAmmunition;
            public float[] launchPosition, rotationEuler, openEuler;
            public float openDuration;
        }
        [Serializable] private sealed class SourceRig
        {
            public string yaw, pitch, system;
            public float[] yawRange, elevationRange;
            public SourceMuzzle[] muzzles;
        }
        [Serializable] private sealed class SourceMuzzle { public string parent; public float[] localPosition; }
#pragma warning restore 0649

        private static Vector3 Vector(float[] values)
        {
            if (values == null || values.Length != 3 || values.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Invalid source vector.");
            return new Vector3(values[0], values[1], values[2]);
        }

        private static FieldInfo Field(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo result = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (result != null) return result;
            }
            throw new MissingFieldException(type.FullName, name);
        }

        private static T Read<T>(object instance, string name)
        {
            return (T)Field(instance.GetType(), name).GetValue(instance);
        }

        private static void Write(object instance, string name, object value)
        {
            Field(instance.GetType(), name).SetValue(instance, value);
        }

        private static Transform RequireNamed(Transform root, string name)
        {
            Transform[] matches = root.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name == name).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Expected one '" + name + "' below " + root.name +
                    "; found " + matches.Length + ".");
            return matches[0];
        }

        private static Transform NewAnchor(Transform parent, string name, Vector3 position, Quaternion rotation)
        {
            Transform anchor = new GameObject(name).transform;
            anchor.SetParent(parent, false);
            anchor.localPosition = position;
            anchor.localRotation = rotation;
            return anchor;
        }

        private static string RelativePath(Transform root, Transform child)
        {
            var names = new List<string>();
            for (Transform node = child; node != null && node != root; node = node.parent)
                names.Add(node.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static void HideNativeGeometry(GameObject prefab)
        {
            // Lasers use a MeshRenderer for the beam; particle and line renderers
            // also belong to the working weapon/effect system rather than hull art.
            var effectRenderers = new HashSet<Renderer>();
            foreach (Laser laser in prefab.GetComponentsInChildren<Laser>(true))
            {
                Renderer beam = Read<Renderer>(laser, "beamRenderer");
                if (beam != null) effectRenderers.Add(beam);
            }
            foreach (LODGroup lod in prefab.GetComponentsInChildren<LODGroup>(true)) lod.enabled = false;
            foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                if ((renderer is MeshRenderer || renderer is SkinnedMeshRenderer) && !effectRenderers.Contains(renderer))
                    renderer.enabled = false;
        }

        private static void SetGunMuzzle(Gun gun, Transform pitchPivot, Vector3 localMuzzle)
        {
            Transform muzzle = NewAnchor(pitchPivot, "ResoluteMuzzle", localMuzzle, Quaternion.identity);
            Write(gun, "muzzles", new[] { muzzle });
            ParticleSystem[] particles = Read<ParticleSystem[]>(gun, "muzzleParticles");
            foreach (ParticleSystem particle in particles)
            {
                particle.transform.SetParent(muzzle, false);
                particle.transform.localPosition = Vector3.zero;
                particle.transform.localRotation = Quaternion.identity;
            }
        }

        private static void ConfigureFiringCone(Turret turret, Transform parent, float heading, float angle)
        {
            Transform direction = NewAnchor(parent, "ResoluteFiringArc", Vector3.zero, Quaternion.Euler(0f, heading, 0f));
            var cone = new FiringCone();
            Write(cone, "transform", direction);
            Write(cone, "coneAngle", angle);
            Write(cone, "exclusion", false);
            Write(turret, "firingCones", new[] { cone });
        }

        private static void FitPartCollider(UnitPart part, Bounds bounds)
        {
            Collider collider = part.GetComponent<Collider>();
            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null)
            {
                var shape = new Mesh { name = "Resolute " + part.name + " damage volume", vertices = Corners(bounds) };
                shape.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 4, 2, 4, 6, 2, 1, 3, 5, 5, 3, 7, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7 };
                shape.RecalculateBounds();
                mesh.sharedMesh = shape;
                return;
            }
            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null)
            {
                sphere.center = bounds.center;
                sphere.radius = Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z));
                return;
            }
            BoxCollider box = collider as BoxCollider;
            if (box != null) { box.center = bounds.center; box.size = bounds.size; return; }
            throw new InvalidOperationException("Unsupported weapon damage volume: " + part.name);
        }

        private static void BindRig(Ship ship, Turret turret, SourceRig rig, Dictionary<string, Transform> nodes,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts)
        {
            Transform yaw = nodes[rig.yaw];
            Transform pitch = nodes[rig.pitch];
            Renderer[] art = NearRigRenderers(yaw);
            Bounds body = yaw.GetComponent<MeshFilter>().sharedMesh.bounds;
            Vector3 pitchPosition = yaw.InverseTransformPoint(pitch.position);
            Quaternion pitchRotation = Quaternion.Inverse(yaw.rotation) * pitch.rotation;
            UnitPart part = turret.GetComponent<UnitPart>();
            if (part == null) throw new InvalidOperationException("A weapon mount has no own native damage part: " + turret.name);
            Weapon firstWeapon = turret.GetWeaponStations()[0].Weapons[0];
            Transform elevation = Read<Transform>(turret, "elevationTransform") ?? firstWeapon.transform;
            if (elevation == turret.transform)
                throw new InvalidOperationException("The audited turret pitch and yaw pivots are no longer separate.");
            turret.transform.SetPositionAndRotation(yaw.position, yaw.rotation);
            float initialYaw = turret.transform.localEulerAngles.y;
            Write(turret, "traverseAngle", initialYaw > 180f ? initialYaw - 360f : initialYaw);
            elevation.SetParent(turret.transform, true);
            elevation.localPosition = pitchPosition;
            elevation.localRotation = pitchRotation;
            Write(turret, "elevationTransform", elevation);
            Write(turret, "minElevation", rig.elevationRange[0]);
            Write(turret, "maxElevation", rig.elevationRange[1]);
            if (rig.yaw == "rsl_gun_yaw") Write(turret, "traverseRange", 120f);

            yaw.SetParent(turret.transform, false);
            yaw.localPosition = Vector3.zero; yaw.localRotation = Quaternion.identity; yaw.localScale = Vector3.one;
            pitch.SetParent(elevation, false);
            pitch.localPosition = Vector3.zero; pitch.localRotation = Quaternion.identity; pitch.localScale = Vector3.one;
            FitPartCollider(part, body);

            Gun gun = firstWeapon as Gun;
            if (gun != null)
            {
                Transform muzzleParent = elevation;
                if (rig.yaw == "rsl_gun_yaw")
                {
                    Transform recoil = Read<Transform>(gun, "recoilTransform");
                    if (recoil != null)
                    {
                        recoil.localPosition = Vector3.zero;
                        recoil.localRotation = Quaternion.identity;
                        Transform barrel = nodes["rsl_gun_barrel"];
                        barrel.SetParent(recoil, false);
                        barrel.localPosition = Vector3.zero;
                        barrel.localRotation = Quaternion.identity;
                        muzzleParent = recoil;
                    }
                }
                SetGunMuzzle(gun, muzzleParent, Vector(rig.muzzles[0].localPosition));
                if (rig.yaw.StartsWith("rsl_ciws_", StringComparison.Ordinal))
                    ResoluteCiwsAim.Configure(ship, turret, gun, Read<Transform[]>(gun, "muzzles")[0]);
            }
            Laser laser = firstWeapon as Laser;
            if (laser != null)
            {
                Transform direction = Read<Transform>(laser, "directionTransform");
                direction.localPosition = Vector(rig.muzzles[0].localPosition);
                direction.localRotation = Quaternion.identity;
                Write(laser, "vehicularPowerSupply", true);
                string lensName = rig.yaw.Replace("_yaw", "_lens51");
                ResoluteHaloAperture.Configure(laser, nodes[lensName], direction);
            }
            foreach (Renderer renderer in art) AddVisual(ownership, part, renderer);
            weaponParts.Add(part);
        }

        internal static Renderer[] NearRigRenderers(Transform yaw)
        {
            return yaw.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer.GetComponent<ResoluteAnimatedLod>() == null).ToArray();
        }

        internal static void AddArticulatedDamageVisuals(LOD[] lods, Dictionary<UnitPart, List<Renderer>> ownership)
        {
            foreach (Renderer renderer in lods.Skip(1).SelectMany(lod => lod.renderers))
            {
                if (renderer.GetComponent<ResoluteAnimatedLod>() == null) continue;
                UnitPart owner = renderer.GetComponentInParent<UnitPart>(true);
                if (owner == null) throw new InvalidOperationException("No physical damage owner for " + renderer.name);
                AddVisual(ownership, owner, renderer);
            }
        }

        internal static void BindAdditionalRig(Ship ship, Turret turret, Transform visual, string yaw,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts)
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(ShipFactory).Assembly.Location), "Assets");
            SourceAnchors anchors = JsonConvert.DeserializeObject<SourceAnchors>(
                File.ReadAllText(Path.Combine(folder, "weapon_anchors.json")));
            SourceRig rig = anchors.turrets.Single(r => r.yaw == yaw);
            var nodes = visual.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            BindRig(ship, turret, rig, nodes, ownership, weaponParts);
        }

        private static void AddLasers(Ship ship, SourceAnchors anchors, Dictionary<string, Transform> nodes,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts, ManualLogSource log)
        {
            // This exact naval laser was verified in the 0.34.1 resource audit.
            ShipDefinition carrier = Resources.FindObjectsOfTypeAll<ShipDefinition>()
                .SingleOrDefault(d => d.jsonKey == "SmallCarrier1");
            if (carrier == null || carrier.unitPrefab == null)
                throw new InvalidOperationException("The audited Cursor naval laser donor is unavailable.");
            Laser prototype = carrier.unitPrefab.GetComponentsInChildren<Laser>(true).FirstOrDefault();
            Turret prototypeTurret = prototype != null ? prototype.GetComponentInParent<Turret>(true) : null;
            if (prototypeTurret == null || !Read<bool>(prototype, "vehicularPowerSupply"))
                throw new InvalidOperationException("The Cursor's native naval laser has changed.");
            List<TargetDetector> sensors = new List<TargetDetector>(Read<List<TargetDetector>>(
                RequireNamed(ship.transform, "CIWS_FR").GetComponent<Turret>(), "targetDetectors"));
            Transform parent = RequireNamed(ship.transform, "Hull_ExhaustStack");
            foreach (string side in new[] { "p", "s" })
            {
                GameObject copy = Object.Instantiate(prototypeTurret.gameObject, parent, false);
                copy.name = "ResoluteLaser_" + side;
                foreach (UnitPart part in copy.GetComponentsInChildren<UnitPart>(true))
                {
                    part.parentUnit = ship;
                    part.rb = null;
                    Write(part, "criticalPart", false);
                }
                foreach (Weapon weapon in copy.GetComponentsInChildren<Weapon>(true)) weapon.attachedUnit = ship;
                Turret turret = copy.GetComponent<Turret>();
                Write(turret, "attachedUnit", ship);
                Write(turret, "targetDetectors", new List<TargetDetector>(sensors));
                Write(turret, "fireControl", null);
                HideNativeGeometry(copy);
                BindRig(ship, turret, anchors.turrets.Single(r => r.yaw == "rsl_laser_" + side + "_yaw"), nodes, ownership, weaponParts);
                ConfigureFiringCone(turret, parent, side == "p" ? 90f : 270f, 100f);
            }
            log.LogInfo("Installed two native Cursor-class naval laser systems, including their target/beam/damage logic.");
        }

        private static void ConfigureMissiles(Ship ship, Transform visual, SourceAnchors anchors,
            Dictionary<string, Transform> nodes, ManualLogSource log)
        {
            SourceCell[] cells = anchors.banks.SelectMany(bank => bank.cells).ToArray();
            var mappings = new Dictionary<string, string[]>
            {
                { "CMLauncher_FL", new[] { "WeaponSystem1" } },
                { "CMLauncher_FR", new[] { "WeaponSystem28" } },
                { "CMLaunchOrientation", new[] { "WeaponSystem3", "WeaponSystem5" } },
                { "launchOrientation", new[] { "WeaponSystem2", "WeaponSystem4", "WeaponSystem6", "WeaponSystem27" } },
                { "launcher_F", new[] { "WeaponSystem7" } },
                { "launcher_R", new[] { "WeaponSystem8" } }
            };
            var bounds = ship.GetComponentsInChildren<ShipPart>(true).Where(p => p.name != "turret_F")
                .ToDictionary(p => (UnitPart)p, p => RootBounds(ship.transform, p.GetComponent<Collider>()));
            foreach (var mapping in mappings)
            {
                MissileLauncher launcher = ship.GetComponentsInChildren<MissileLauncher>(true)
                    .SingleOrDefault(candidate => candidate.name == mapping.Key);
                if (launcher == null) throw new InvalidOperationException("A native missile launcher is missing: " + mapping.Key);
                SourceCell[] assigned = cells.Where(cell => mapping.Value.Contains(cell.sourceWeapon)).ToArray();
                if (assigned.Length == 0) throw new InvalidDataException("No mapped launch cells for " + mapping.Key);
                Transform[] launchPoints = new Transform[assigned.Length];
                Transform[] hatches = new Transform[assigned.Length];
                Vector3[] open = new Vector3[assigned.Length];
                float[] durations = new float[assigned.Length];
                for (int i = 0; i < assigned.Length; i++)
                {
                    SourceCell cell = assigned[i];
                    Vector3 world = visual.TransformPoint(Vector(cell.launchPosition));
                    UnitPart owner = NearestPart(ship.transform.InverseTransformPoint(world), bounds);
                    Transform anchor = NewAnchor(owner.transform, "ResoluteLaunch_" + cell.hatch, Vector3.zero, Quaternion.identity);
                    anchor.SetPositionAndRotation(world, visual.rotation * Quaternion.Euler(Vector(cell.rotationEuler)));
                    launchPoints[i] = anchor;
                    hatches[i] = nodes[cell.hatch];
                    open[i] = Vector(cell.openEuler);
                    durations[i] = cell.openDuration;
                }
                Write(launcher, "launchTransforms", launchPoints);
                Write(launcher, "cellColumns", 0);
                Write(launcher, "cellRows", 0);
                launcher.ammo = assigned.Length;
                // Launch anchors are independent of the old trainable launcher
                // pivots: the grids remain fixed in the ship's deck.
                ResoluteVlsAnimation animation = launcher.gameObject.AddComponent<ResoluteVlsAnimation>();
                animation.Launcher = launcher;
                animation.Hatches = hatches;
                animation.OpenEuler = open;
                animation.OpenSeconds = durations;
                log.LogInfo(mapping.Key + ": " + assigned.Length + " native " + launcher.info.name + " rounds at source hatch positions.");
            }
            foreach (Turret turret in ship.GetComponentsInChildren<Turret>(true))
            {
                if (!turret.GetWeaponStations().SelectMany(s => s.Weapons).Any(w => w is MissileLauncher)) continue;
                Write(turret, "firesWithoutAiming", true);
                Write(turret, "minElevation", 90f);
                Write(turret, "maxElevation", 90f);
                Write(turret, "firingCones", new FiringCone[0]);
            }
        }

        private static void AlignFlightDeck(Ship ship, Transform visual)
        {
            Transform deck = RequireNamed(ship.transform, "Hull_hangarFloor");
            Transform pad = RequireNamed(ship.transform, "LandingPad");
            pad.position = visual.TransformPoint(new Vector3(0f, 5.526f, -98f));
            pad.rotation = visual.rotation * Quaternion.Euler(0f, 180f, 0f);
            RequireNamed(ship.transform, "HangarPreview").position = visual.TransformPoint(new Vector3(0f, 5.526f, -80f));
            RequireNamed(ship.transform, "HangarCamera").position = visual.TransformPoint(new Vector3(9f, 12f, -64f));
            BoxCollider landingSurface = deck.gameObject.AddComponent<BoxCollider>();
            landingSurface.center = deck.InverseTransformPoint(visual.TransformPoint(new Vector3(0f, 5.306f, -98f)));
            landingSurface.size = new Vector3(27.8f, 0.4f, 27.8f);
            if (ship.radar != null && ship.radar.GetScanPoint() != null)
                ship.radar.GetScanPoint().position = RequireNamed(visual, "rsl_nav_radar").position;
            Airbase airbase = ship.GetComponent<Airbase>();
            if (airbase != null)
            {
                object settings = Read<object>(airbase, "airbaseSettings");
                Write(settings, "UniqueName", "Resolute");
                Write(settings, "DisplayName", "Resolute");
            }
        }

        private static void SetDamageVisuals(UnitPart part, IList<Renderer> renderers)
        {
            DamageMaterial previous = Read<DamageMaterial>(part, "damageMaterial");
            var replacement = new DamageMaterial
            {
                threshold = previous != null ? previous.threshold : 100f,
                indices = new byte[0],
                renderers = new List<Renderer>(renderers)
            };
            Write(part, "damageMaterial", replacement);
            // Dynamo's hull sections are physical gibs already: ShipPart.Detach
            // gives them their own rigidbody, joints, flooding and sinking. Its
            // structural renderers are not in disintegrateObjects. Hiding ours
            // here made large sections disappear at the integrity threshold.
            Write(part, "disintegrateObjects", part is ShipPart ? new GameObject[0]
                : renderers.Select(r => r.gameObject).Distinct().ToArray());
        }

        private sealed class ColliderShape
        {
            public Collider Collider;
            public Vector3[] RootPoints;
            public Mesh Mesh;
        }

        private static Bounds LocalBounds(Collider collider)
        {
            MeshCollider mesh = collider as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null) return mesh.sharedMesh.bounds;
            BoxCollider box = collider as BoxCollider;
            if (box != null) return new Bounds(box.center, box.size);
            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null) return new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
            CapsuleCollider capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                Vector3 size = Vector3.one * (capsule.radius * 2f);
                size[capsule.direction] = capsule.height;
                return new Bounds(capsule.center, size);
            }
            throw new NotSupportedException("Unsupported donor collider " + collider.GetType().Name);
        }

        private static Vector3[] Corners(Bounds bounds)
        {
            var result = new Vector3[8];
            for (int i = 0; i < 8; i++)
                result[i] = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            return result;
        }

        private static Bounds BoundsOf(IEnumerable<Vector3> positions)
        {
            bool first = true;
            Bounds result = new Bounds();
            foreach (Vector3 position in positions)
            {
                if (first) { result = new Bounds(position, Vector3.zero); first = false; }
                else result.Encapsulate(position);
            }
            if (first) throw new ArgumentException("No positions supplied for bounds.");
            return result;
        }

        private static Bounds RootBounds(Transform root, Collider collider)
        {
            return BoundsOf(Corners(LocalBounds(collider))
                .Select(point => root.InverseTransformPoint(collider.transform.TransformPoint(point))));
        }

        private static Bounds RootBounds(Transform root, MeshFilter filter)
        {
            return BoundsOf(Corners(filter.sharedMesh.bounds)
                .Select(point => root.InverseTransformPoint(filter.transform.TransformPoint(point))));
        }

        // Move coordinates and collider shapes instead of scaling the ship's root.
        // A non-uniform root scale would shear rotating guns and their children.
        private static void ResizeNativeHull(Ship ship, Vector3 scale, Vector3 offset)
        {
            Transform root = ship.transform;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            var positions = transforms.ToDictionary(t => t, t => root.InverseTransformPoint(t.position));
            var shapes = new List<ColliderShape>();
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                MeshCollider mesh = collider as MeshCollider;
                Vector3[] local = mesh != null && mesh.sharedMesh != null
                    ? mesh.sharedMesh.vertices : Corners(LocalBounds(collider));
                shapes.Add(new ColliderShape
                {
                    Collider = collider,
                    Mesh = mesh != null ? mesh.sharedMesh : null,
                    RootPoints = local.Select(p => root.InverseTransformPoint(collider.transform.TransformPoint(p))).ToArray()
                });
            }
            foreach (Transform transform in transforms)
                if (transform != root)
                    transform.position = root.TransformPoint(Vector3.Scale(positions[transform], scale) + offset);

            foreach (ColliderShape shape in shapes)
            {
                Collider collider = shape.Collider;
                Vector3[] local = shape.RootPoints.Select(p =>
                    collider.transform.InverseTransformPoint(root.TransformPoint(Vector3.Scale(p, scale) + offset))).ToArray();
                MeshCollider mesh = collider as MeshCollider;
                if (mesh != null && shape.Mesh != null)
                {
                    Mesh copy = Object.Instantiate(shape.Mesh);
                    copy.name = "Resolute collision " + shape.Mesh.name;
                    copy.vertices = local;
                    copy.RecalculateBounds();
                    mesh.sharedMesh = copy;
                    continue;
                }
                Bounds bounds = BoundsOf(local);
                BoxCollider box = collider as BoxCollider;
                if (box != null) { box.center = bounds.center; box.size = bounds.size; continue; }
                SphereCollider sphere = collider as SphereCollider;
                if (sphere != null) { sphere.center = bounds.center; sphere.radius = Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)); continue; }
                CapsuleCollider capsule = collider as CapsuleCollider;
                if (capsule != null)
                {
                    capsule.center = bounds.center;
                    capsule.height = bounds.size[capsule.direction];
                    capsule.radius = Mathf.Max(bounds.extents[(capsule.direction + 1) % 3], bounds.extents[(capsule.direction + 2) % 3]);
                }
            }
        }

        private static void SetDisplacementMass(Ship ship, float targetMass)
        {
            UnitPart[] parts = ship.GetComponentsInChildren<UnitPart>(true);
            float originalMass = parts.Sum(p => p.mass);
            if (originalMass <= 0f) throw new InvalidOperationException("The donor has no configured structural mass.");
            float factor = targetMass / originalMass;
            foreach (UnitPart part in parts)
            {
                part.mass *= factor;
                ShipPart waterPart = part as ShipPart;
                if (waterPart == null) continue;
                foreach (string field in new[] { "displacement", "leakRateMin", "leakRateMax" })
                    Write(waterPart, field, Read<float>(waterPart, field) * factor);
            }
            ship.damageControlAvailable *= factor;
            ship.GetComponent<Rigidbody>().mass = targetMass;
            foreach (ShipPropulsion propulsion in ship.GetComponentsInChildren<ShipPropulsion>(true))
            {
                // Water drag grows with speed squared. Scale the native thrust
                // ratio to the source design speed. The fitted plating collision
                // changes the settled trim slightly; calibrate the native force
                // against the current undamaged open-water speed trial. The 0.4
                // compound hull settles near 34.25 knots with the old .981 scale;
                // 1.025 restores the 35-knot design through native thrust alone.
                float speedRatio = (35f * 1.852f) / 59f;
                Write(propulsion, "thrust", Read<float>(propulsion, "thrust") * factor * speedRatio * speedRatio * 1.025f);
                Write(propulsion, "steeringThrust", Read<float>(propulsion, "steeringThrust") * factor);
            }
        }

        private static UnitPart NearestPart(Vector3 point, Dictionary<UnitPart, Bounds> bounds)
        {
            UnitPart best = null;
            float bestScore = float.PositiveInfinity;
            foreach (var pair in bounds)
            {
                // Compartment colliders can overlap. Prefer the more local part
                // rather than assigning every overlapping triangle to the main hull.
                float score = pair.Value.SqrDistance(point)
                    + (point - pair.Value.center).sqrMagnitude * 0.0001f
                    + pair.Value.size.sqrMagnitude * 0.000001f;
                if (score < bestScore) { best = pair.Key; bestScore = score; }
            }
            if (best == null) throw new InvalidOperationException("No native compartment can own Resolute geometry.");
            return best;
        }

        private static void AddVisual(Dictionary<UnitPart, List<Renderer>> ownership, UnitPart part, Renderer renderer)
        {
            List<Renderer> renderers;
            if (!ownership.TryGetValue(part, out renderers)) ownership.Add(part, renderers = new List<Renderer>());
            renderers.Add(renderer);
        }

        private static Mesh PartitionMesh(Mesh input, IList<int> triangleIndices, Transform source, Transform parent)
        {
            Vector3[] vertices = input.vertices;
            Vector3[] normals = input.normals;
            Vector2[] uvs = input.uv;
            Vector4[] tangents = input.tangents;
            var remap = new Dictionary<int, int>();
            var newVertices = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newUvs = new List<Vector2>();
            var newTangents = new List<Vector4>();
            var newIndices = new List<int>(triangleIndices.Count);
            foreach (int old in triangleIndices)
            {
                int index;
                if (!remap.TryGetValue(old, out index))
                {
                    index = newVertices.Count;
                    remap.Add(old, index);
                    newVertices.Add(parent.InverseTransformPoint(source.TransformPoint(vertices[old])));
                    if (normals.Length == vertices.Length)
                        newNormals.Add(parent.InverseTransformDirection(source.TransformDirection(normals[old])).normalized);
                    if (uvs.Length == vertices.Length) newUvs.Add(uvs[old]);
                    if (tangents.Length == vertices.Length)
                    {
                        Vector4 tangent = tangents[old];
                        Vector3 direction = parent.InverseTransformDirection(source.TransformDirection(new Vector3(tangent.x, tangent.y, tangent.z))).normalized;
                        newTangents.Add(new Vector4(direction.x, direction.y, direction.z, tangent.w));
                    }
                }
                newIndices.Add(index);
            }
            var output = new Mesh
            {
                name = "Resolute section " + input.name + " / " + parent.name,
                indexFormat = newVertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            output.SetVertices(newVertices);
            if (newNormals.Count != 0) output.SetNormals(newNormals);
            if (newUvs.Count != 0) output.SetUVs(0, newUvs);
            if (newTangents.Count != 0) output.SetTangents(newTangents);
            output.SetTriangles(newIndices, 0);
            if (newNormals.Count == 0) output.RecalculateNormals();
            output.RecalculateBounds();
            return output;
        }

        private static async UniTask AttachStaticVisualsAsync(Ship ship, GameObject visual,
            Dictionary<UnitPart, List<Renderer>> ownership, HashSet<UnitPart> weaponParts, ManualLogSource log, StartupLoadContext loading)
        {
            var stage = System.Diagnostics.Stopwatch.StartNew();
            log.LogInfo("Structure stage: prepare source meshes and deck supports.");
            LODGroup group = visual.GetComponent<LODGroup>();
            if (group == null) throw new InvalidOperationException("The Resolute visual prefab has no LOD group.");
            LOD[] lods = group.GetLODs();
            if (lods.Length == 0) throw new InvalidOperationException("The Resolute visual prefab has no near geometry.");
            var mapped = new HashSet<Renderer>(ownership.Values.SelectMany(r => r));
            MeshRenderer hull = RequireNamed(visual.transform, "rsl_hull").GetComponent<MeshRenderer>();
            StructuralGeometry.Result structure = StructuralGeometry.Begin(ship, visual.transform, hull);
            foreach (Renderer weaponRenderer in mapped)
            {
                if (loading != null) await loading.Step("Preparing weapon surfaces");
                SurfacePaintStyle.ApplyRenderer(weaponRenderer, visual.transform, "weapon");
            }
            log.LogInfo("Structure stage: source/weapon paint ready in " + stage.ElapsedMilliseconds + " ms; preparing hull."); stage.Restart();
            await StructuralGeometry.BuildHullAsync(structure, ship, visual.transform, hull, log, loading);
            log.LogInfo("Structure stage: hull ready in " + stage.ElapsedMilliseconds + " ms; fitting " + lods[0].renderers.Length + " near renderers."); stage.Restart();
            foreach (Renderer renderer in lods[0].renderers)
            {
                if (loading != null) await loading.Step("Fitting ship details");
                if (renderer == hull) continue;
                if (mapped.Contains(renderer)) { structure.LodRenderers.Add(renderer); continue; }
                StructuralGeometry.BuildFitting(structure, visual.transform, renderer);
            }
            log.LogInfo("Structure stage: fittings ready in " + stage.ElapsedMilliseconds + " ms; assigning mass and supports."); stage.Restart();
            if (loading != null) await loading.Step("Connecting damage compartments");
            StructuralGeometry.Finish(structure, ship, weaponParts, log);
            log.LogInfo("Structure stage: supports ready in " + stage.ElapsedMilliseconds + " ms; attaching VLS cell art."); stage.Restart();
            await VlsStructuralAttachments.ConfigureAsync(ship, structure, loading);
            log.LogInfo("Structure stage: VLS art ready in " + stage.ElapsedMilliseconds + " ms; binding damage visuals."); stage.Restart();
            foreach (var pair in structure.Renderers)
                foreach (Renderer renderer in pair.Value) AddVisual(ownership, pair.Key, renderer);
            // Support fitting can move hatches and other art after their first
            // assignment. Bind native damage and color feedback to the final
            // physical parent, including articulated weapon parts below decks.
            Renderer[] ownedRenderers = ownership.Values.SelectMany(r => r).Distinct().ToArray();
            ownership.Clear();
            foreach (Renderer renderer in ownedRenderers)
            {
                if (loading != null) await loading.Step("Binding damage surfaces");
                UnitPart owner = null;
                for (Transform ancestor = renderer.transform; ancestor != null; ancestor = ancestor.parent)
                {
                    owner = ancestor.GetComponent<UnitPart>();
                    if (owner != null || ancestor == ship.transform) break;
                }
                if (owner == null)
                    throw new InvalidOperationException("No physical damage owner for Resolute renderer " + renderer.name);
                AddVisual(ownership, owner, renderer);
            }
            var diagnostics = ship.gameObject.AddComponent<ResoluteStructureDiagnostics>();
            diagnostics.Sections = structure.Cells.Select(cell => cell.Section).ToArray();
            diagnostics.NearRenderersBefore = ownership.Values.Sum(list => list.Count);
            diagnostics.NearMaterialSlotsBefore = ownership.Values.SelectMany(list => list).Sum(renderer => renderer.sharedMaterials.Length);
            foreach (StructuralGeometry.Cell cell in structure.Cells)
                ownership[cell.Part] = await ResoluteStaticBatches.BatchAsync(cell.Part, ownership[cell.Part], false, loading);
            structure.LodRenderers.Clear(); structure.LodRenderers.AddRange(ownership.Values.SelectMany(list => list));
            diagnostics.NearRenderersAfter = structure.LodRenderers.Count;
            diagnostics.NearMaterialSlotsAfter = structure.LodRenderers.Sum(renderer => renderer.sharedMaterials.Length);
            int backfaceRenderers = 0, backfaceTriangles = 0;
            foreach (var pair in ownership.ToArray())
            foreach (Renderer renderer in pair.Value.Where(SurfacePaintStyle.NeedsDamageBackfaces).ToArray())
            {
                if (loading != null) await loading.Step("Preparing damaged surfaces");
                MeshRenderer reverse = SurfacePaintStyle.AddDamageBackfaces(renderer, pair.Key);
                AddVisual(ownership, pair.Key, reverse); structure.LodRenderers.Add(reverse);
                backfaceRenderers++; backfaceTriangles += reverse.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3;
            }
            // Static mast arrays keep their true final compartment owner; a
            // healthy child is not cosmetically damaged by a parent's loss.
            log.LogInfo("Damage shell/mast validation: " + backfaceRenderers + " native-shader reverse renderers, " + backfaceTriangles +
                " triangles dormant until their owner takes damage.");
            var nearByCell = structure.Cells.ToDictionary(cell => cell.Part, cell => new List<Renderer>());
            var cellOwners = new HashSet<UnitPart>(nearByCell.Keys);
            foreach (var pair in ownership)
            {
                UnitPart support = StructuralGeometry.LodSupport(pair.Key, cellOwners);
                nearByCell[support].AddRange(pair.Value);
            }
            var levels = new List<Dictionary<UnitPart, List<Renderer>>> { nearByCell };
            for (int level = 1; level < lods.Length; level++)
            {
                var distant = await StructuralGeometry.PartitionDistantAsync(structure, visual.transform, lods[level].renderers, loading);
                foreach (StructuralGeometry.Cell cell in structure.Cells)
                    distant[cell.Part] = await ResoluteStaticBatches.BatchAsync(cell.Part, distant[cell.Part], true, loading);
                diagnostics.DistantRenderers += distant.Values.Sum(list => list.Count);
                levels.Add(distant);
            }
            var controllers = new Dictionary<UnitPart, ResoluteLocalDamageLod>();
            foreach (StructuralGeometry.Cell cell in structure.Cells)
            {
                if (loading != null) await loading.Step("Connecting near and distant models");
                var node = new GameObject("Resolute compartment LOD " + cell.Name);
                node.transform.SetParent(cell.Part.transform, false);
                LODGroup local = node.AddComponent<LODGroup>();
                local.localReferencePoint = node.transform.InverseTransformPoint(group.transform.TransformPoint(group.localReferencePoint));
                Vector3 fromScale = group.transform.lossyScale, toScale = node.transform.lossyScale;
                local.size = group.size * Mathf.Max(Mathf.Abs(fromScale.x), Mathf.Abs(fromScale.y), Mathf.Abs(fromScale.z)) /
                    Mathf.Max(Mathf.Abs(toScale.x), Mathf.Abs(toScale.y), Mathf.Abs(toScale.z));
                local.fadeMode = group.fadeMode; local.animateCrossFading = group.animateCrossFading;
                local.SetLODs(lods.Select((lod, level) => new LOD(lod.screenRelativeTransitionHeight, levels[level][cell.Part].ToArray())
                    { fadeTransitionWidth = lod.fadeTransitionWidth }).ToArray());
                var controller = node.AddComponent<ResoluteLocalDamageLod>(); controller.Group = local;
                controllers.Add(cell.Part, controller);
            }
            // Only the compartment groups now own renderer culling. Preserve
            // the original ship-sized screen thresholds in every local group.
            group.SetLODs(new LOD[0]); group.enabled = false;
            diagnostics.DetailGroups = controllers.Values.ToArray();
            log.LogInfo("Structure render batching: " + diagnostics.NearRenderersBefore + " -> " + diagnostics.NearRenderersAfter +
                " near renderers, " + diagnostics.NearMaterialSlotsBefore + " -> " + diagnostics.NearMaterialSlotsAfter +
                " material slots before dormant backfaces; " + diagnostics.DetailGroups.Length + " local damage LOD groups.");
            // Far weapon art keeps the actual turret's damage/disintegration
            // owner, but joins damage lists only after each LOD array is fixed.
            // It must never enter the near LOD through the source yaw hierarchy.
            AddArticulatedDamageVisuals(lods, ownership);
            foreach (UnitPart part in ship.GetComponentsInChildren<UnitPart>(true))
            {
                if (loading != null) await loading.Step("Connecting damage effects");
                List<Renderer> renderers;
                if (!ownership.TryGetValue(part, out renderers)) renderers = new List<Renderer>();
                SetDamageVisuals(part, renderers);
                if (renderers.Count == 0) continue;
                ResoluteDamageVisual feedback = part.gameObject.AddComponent<ResoluteDamageVisual>();
                feedback.Part = part;
                feedback.Renderers = renderers.ToArray();
                // Aggregated source weapon art can cross hull seams. A weapon
                // damage request conservatively holds every group exact; hull
                // damage holds only its own compartment. Repair withdraws it.
                feedback.DetailGroups = controllers.ContainsKey(part) ? new[] { controllers[part] } : diagnostics.DetailGroups;
                ResoluteStructuralSection section = part.GetComponent<ResoluteStructuralSection>();
                feedback.StructuralSection = section;
                feedback.StructuralGroups = section == null ? diagnostics.DetailGroups :
                    controllers.Where(pair => pair.Key == part || section.NeighborFaces.Any(face => face != null &&
                        face.GetComponentInParent<UnitPart>(true) == pair.Key)).Select(pair => pair.Value).ToArray();
            }
            log.LogInfo("Mapped coherent Resolute sections and fittings to " + ownership.Count + " native damage parts in " + stage.ElapsedMilliseconds + " ms.");
        }

        private static void ValidatePrefab(GameObject prefab, Ship ship)
        {
            if (prefab.activeInHierarchy)
                throw new InvalidOperationException("The assembled prefab was unexpectedly activated.");
            if (ship.GetComponentInChildren<Rigidbody>(true) == null)
                throw new InvalidOperationException("The assembled ship has no rigidbody.");
            ShipPart[] parts = ship.GetComponentsInChildren<ShipPart>(true);
            if (parts.Length == 0 || !parts.Any(p => Read<bool>(p, "criticalPart")))
                throw new InvalidOperationException("The native flooding/critical-part model is missing.");
            foreach (ShipPart part in parts)
            {
                if (part.GetComponent<Collider>() == null || part.parentUnit != ship)
                    throw new InvalidOperationException("Invalid native ShipPart: " + RelativePath(prefab.transform, part.transform));
            }
            foreach (Turret turret in ship.GetComponentsInChildren<Turret>(true))
            {
                if (Read<Transform>(turret, "elevationTransform") == null &&
                    (turret.GetWeaponStations().Length == 0 || turret.GetWeaponStations()[0].Weapons.Count == 0))
                    throw new InvalidOperationException("A turret has no elevation pivot: " + turret.name);
                foreach (WeaponStation station in turret.GetWeaponStations())
                    if (station.Weapons == null || station.Weapons.Count == 0 || station.Weapons[0].info == null)
                        throw new InvalidOperationException("A turret weapon station is incomplete: " + turret.name);
            }
        }

        private static void ConfigureFixedVlsControls(Ship ship)
        {
            foreach (Turret turret in ship.GetComponentsInChildren<Turret>(true))
            {
                Weapon[] weapons = turret.GetWeaponStations().SelectMany(station => station.Weapons).ToArray();
                if (weapons.Length == 0 || !weapons.All(weapon => weapon.GetComponent<ResoluteVlsAnimation>() != null)) continue;
                // One decision owner: native initialization subscribes these
                // stations to the original controller without registering an
                // autonomous sensor search. The router then assigns the actual
                // per-ammunition controller after all stations initialize.
                NaturalFireControlGroups groups = ship.GetComponent<NaturalFireControlGroups>();
                Write(turret, "targetAcquisitionMode", Enum.Parse(Field(turret.GetType(), "targetAcquisitionMode").FieldType, "fireControl"));
                Write(turret, "fireControl", groups.Original);
                Write(turret, "targetDetectors", new List<TargetDetector>());
                // A legacy Dynamo controller can coordinate cells in several
                // banks. Its old invisible SAM box / superstructure ancestry
                // must not disable healthy cells on an unrelated deck. Native
                // fire control stays intact; physical per-cell loss is handled
                // by ResoluteMagazineCookoff on the actual supporting ShipPart.
                Write(turret, "criticalParts", new UnitPart[0]);
                Write(turret, "attachedUnit", ship);
                turret.transform.SetParent(ship.transform, true);
                foreach (Weapon weapon in weapons) weapon.transform.SetParent(ship.transform, true);
            }
        }
    }

    // Native ship damage maps consume per-part hit points. Cache this renderer's
    // native material instances so all slots follow their own part's damage
    // while retaining the render pipeline's material batching path.
    internal sealed class ResoluteDamageVisual : MonoBehaviour
    {
        public UnitPart Part;
        public Renderer[] Renderers;
        public ResoluteLocalDamageLod[] DetailGroups, StructuralGroups;
        public ResoluteStructuralSection StructuralSection;
        private Material[][] damageMaterials;
        private readonly List<Material> ownedDamageMaterials = new List<Material>();
        private float appliedHealth = 100f;
        private Color[][] colors;
        private byte[][] materialResponse;

        private void Awake()
        {
            damageMaterials = new Material[Renderers.Length][];
            colors = new Color[Renderers.Length][];
            materialResponse = new byte[Renderers.Length][];
            for (int i = 0; i < Renderers.Length; i++)
            {
                Material[] materials = Renderers[i].sharedMaterials;
                colors[i] = materials.Select(material =>
                    NavalMaterials.UsesNativeDamage(material) ? Color.white :
                    material != null && material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor")
                    : material != null && material.HasProperty("_Color") ? material.color : Color.white).ToArray();
                materialResponse[i] = materials.Select(material => (byte)(NavalMaterials.UsesNativeDamage(material) ? 1 :
                    material != null && material.name.StartsWith("Resolute exposed structural steel", StringComparison.Ordinal) ? 2 : 0)).ToArray();
            }
            Part.onApplyDamage += Damaged;
            Part.onPartDetached += StructuralChange;
            Part.onParentDetached += StructuralChange;
            Part.onJointBroken += StructuralChange;
            if (StructuralSection != null) StructuralSection.SurfacePieceReleased += Torn;
        }

        private void Damaged(UnitPart.OnApplyDamage damage)
        {
            // A rounded zero-damage callback must not switch an untouched
            // renderer to a different material path. Preserve exact HP values
            // for actual changes, including negative native structural health.
            if (damage.hitPoints == appliedHealth) return;
            appliedHealth = damage.hitPoints;
            foreach (ResoluteLocalDamageLod group in DetailGroups) if (group != null) group.SetDamage(this, damage.hitPoints < 100f);
            float factor = Mathf.Lerp(0.24f, 1f, Mathf.Clamp01(damage.hitPoints / 100f));
            for (int i = 0; i < Renderers.Length; i++)
            {
                Renderer renderer = Renderers[i];
                if (renderer == null) continue;
                if (damageMaterials[i] == null)
                {
                    // Native ShipPart.ApplyDamage already obtains .material
                    // on slot zero. Reuse native per-renderer instances and
                    // instantiate the remaining slots only once, on damage.
                    // Sharing one mutable instance among multiple renderers
                    // would make the next native .material access clone again.
                    // Cached instances preserve all shader/texture properties
                    // while allowing the SRP-compatible material code path.
                    damageMaterials[i] = renderer.materials;
                    foreach (Material material in damageMaterials[i])
                        if (material != null) ownedDamageMaterials.Add(material);
                }
                for (int materialIndex = 0; materialIndex < colors[i].Length; materialIndex++)
                {
                    // Dynamo's interior steel keeps its authored material. Its
                    // exterior alone uses the native progressive damage shader.
                    if (materialResponse[i][materialIndex] == 2) continue;
                    Material material = damageMaterials[i][materialIndex];
                    if (material == null) continue;
                    if (materialResponse[i][materialIndex] == 1)
                    {
                        material.SetFloat("_HitPoints", damage.hitPoints);
                        continue;
                    }
                    Color color = colors[i][materialIndex];
                    color.r *= factor;
                    color.g *= factor;
                    color.b *= factor;
                    if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                }
            }
        }

        private void Torn(ResoluteStructuralSection section, int piece, Transform debris) { StructuralChange(Part); }

        private void StructuralChange(UnitPart part)
        {
            foreach (ResoluteLocalDamageLod group in StructuralGroups) if (group != null) group.StructuralChange();
        }

        private void OnDestroy()
        {
            if (Part != null)
            {
                Part.onApplyDamage -= Damaged;
                Part.onPartDetached -= StructuralChange;
                Part.onParentDetached -= StructuralChange;
                Part.onJointBroken -= StructuralChange;
            }
            if (StructuralSection != null) StructuralSection.SurfacePieceReleased -= Torn;
            if (DetailGroups != null) foreach (ResoluteLocalDamageLod group in DetailGroups) if (group != null) group.SetDamage(this, false);
            // Released VLS hardware can still reference the source materials
            // until its existing 75-second debris lifetime has elapsed.
            foreach (Material material in ownedDamageMaterials.Distinct())
                if (material != null) Object.Destroy(material, 76f);
        }
    }

    internal sealed class ResoluteVlsAnimation : MonoBehaviour
    {
        public MissileLauncher Launcher;
        public Transform[] Hatches;
        public Vector3[] OpenEuler;
        public float[] OpenSeconds;
        private static readonly FieldInfo Cell = typeof(MissileLauncher).GetField("currentCell", BindingFlags.Instance | BindingFlags.NonPublic);
        private Quaternion[] rest;
        private float[] firedAt;
        private float[] lastOpen;
        private int[] activeHatches;
        private bool[] hatchActive;
        private int activeCount;
        private int lastAmmo;
        private int previousCell;
        private int visualCell;
        private bool[] detachedHatches;

        internal void DetachCell(int index)
        {
            detachedHatches[index] = true;
            // Preserve the hinge's current pose when it transfers to debris.
            // Its old rest rotation belongs to the former section transform.
            AccountDestroyedAmmo();
        }

        internal void AccountFiredCell(int index)
        {
            if (!detachedHatches[index])
            {
                firedAt[index] = Time.time;
                if (!hatchActive[index]) { hatchActive[index] = true; activeHatches[activeCount++] = index; }
            }
            AccountDestroyedAmmo();
        }

        internal void AccountDestroyedAmmo()
        {
            lastAmmo = Launcher.ammo;
            previousCell = (int)Cell.GetValue(Launcher);
        }

        private void Awake()
        {
            rest = new Quaternion[Hatches.Length];
            firedAt = new float[Hatches.Length];
            lastOpen = new float[Hatches.Length];
            activeHatches = new int[Hatches.Length];
            hatchActive = new bool[Hatches.Length];
            detachedHatches = new bool[Hatches.Length];
            for (int i = 0; i < Hatches.Length; i++) rest[i] = Hatches[i].localRotation;
            lastAmmo = Launcher.ammo;
            previousCell = (int)Cell.GetValue(Launcher);
        }

        private void LateUpdate()
        {
            if (Launcher == null) return;
            int ammo = Launcher.ammo;
            if (ammo != lastAmmo)
            {
                // Native Fire advances currentCell when it consumes ammunition;
                // remote ammo updates may leave it unchanged. Preserve that
                // fallback without reflecting/boxing this field every frame.
                int nextCell = (int)Cell.GetValue(Launcher);
                if (ammo < lastAmmo)
                {
                    int count = Mathf.Min(lastAmmo - ammo, Hatches.Length);
                    if (nextCell != previousCell)
                        visualCell = (nextCell - count + Hatches.Length) % Hatches.Length;
                    float now = Time.time;
                    for (int i = 0; i < count; i++)
                    {
                        firedAt[visualCell] = now;
                        if (!detachedHatches[visualCell] && !hatchActive[visualCell])
                        {
                            hatchActive[visualCell] = true;
                            activeHatches[activeCount++] = visualCell;
                        }
                        visualCell = (visualCell + 1) % Hatches.Length;
                    }
                }
                lastAmmo = ammo;
                previousCell = nextCell;
            }
            if (activeCount == 0) return;
            float time = Time.time;
            for (int active = activeCount - 1; active >= 0; active--)
            {
                int i = activeHatches[active];
                if (detachedHatches[i])
                {
                    hatchActive[i] = false;
                    activeHatches[active] = activeHatches[--activeCount];
                    continue;
                }
                float age = time - firedAt[i];
                // The shot is already committed by native fire control. Clear the
                // hatch immediately, then use the authored hinge for closing.
                float open = age < 1.5f ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (age - 1.5f) / Mathf.Max(0.1f, OpenSeconds[i]));
                if (Hatches[i] != null && open != lastOpen[i])
                {
                    Hatches[i].localRotation = open == 0f ? rest[i] : rest[i] * Quaternion.Euler(OpenEuler[i] * open);
                    lastOpen[i] = open;
                }
                // Only moving hatches need inspection. The exact rest rotation
                // is restored even when one long frame skips past the close.
                if (open == 0f || Hatches[i] == null)
                {
                    hatchActive[i] = false;
                    activeHatches[active] = activeHatches[--activeCount];
                }
            }
        }
    }

    internal sealed class ResoluteDeckAnimation : MonoBehaviour
    {
        public Ship Ship;
        public ShipPropulsion Propulsion;
        public Transform[] Propellers;
        public float[] ForwardRotation;
        public Transform[] Rudders;
        public Transform NavigationRadar;
        private Quaternion[] rudderRest;
        private float[] propellerDirections;
        private float lastSteering = float.NaN;
        private float screwFraction;

        private void Awake()
        {
            rudderRest = new Quaternion[Rudders.Length];
            for (int i = 0; i < Rudders.Length; i++) rudderRest[i] = Rudders[i].localRotation;
            if (ForwardRotation == null || ForwardRotation.Length != Propellers.Length) ScrewGeometry.Configure(this);
            propellerDirections = ForwardRotation;
        }

        private void Update()
        {
            if (Ship == null || Ship.disabled) return;
            ShipInputs input = Ship.GetInputs();
            float deltaTime = Time.deltaTime;
            // The native throttle includes the speed controller's braking and
            // fine corrections. A speed order alone cannot describe shaft work.
            float requested = Mathf.Clamp(input.throttle, -1f, 1f);
            if (Propulsion == null || !Propulsion.enabled) requested = 0f;
            screwFraction = ResoluteScrewSpeed.Step(screwFraction, requested, deltaTime);
            if (screwFraction != 0f && deltaTime != 0f)
                for (int i = 0; i < Propellers.Length; i++)
                    if (Propellers[i] != null)
                        Propellers[i].Rotate(0f, 0f, propellerDirections[i] * screwFraction *
                            ResoluteScrewSpeed.MaximumDegreesPerSecond * deltaTime, Space.Self);
            if (input.steering != lastSteering)
            {
                Quaternion steering = Quaternion.Euler(0f, input.steering * 25f, 0f);
                for (int i = 0; i < Rudders.Length; i++)
                    if (Rudders[i] != null) Rudders[i].localRotation = rudderRest[i] * steering;
                lastSteering = input.steering;
            }
            if (NavigationRadar != null && deltaTime != 0f) NavigationRadar.Rotate(0f, 90f * deltaTime, 0f, Space.Self);
        }
    }
}
