using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Mirage;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Resolute
{
    // This diagnostic only runs when explicitly requested on the command line.
    // It reads resource prefabs without instantiating ships or starting a match.
    internal static class Audit
    {
        private static string[] pendingArgs;
        private static ManualLogSource pendingLog;
        private static bool synchronousStarted;
        private static Action startupCallback;

        internal static void ScheduleCallback(Action callback) { startupCallback = callback; }

        internal static string AssemblySha256()
        {
            using (var stream = File.OpenRead(typeof(Plugin).Assembly.Location))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static Dictionary<string, string> AssetSha256()
        {
            string folder = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Assets");
            var result = new Dictionary<string, string>();
            foreach (string path in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                using (var stream = File.OpenRead(path))
                using (var hash = SHA256.Create())
                    result.Add(path.Substring(folder.Length + 1).Replace('\\', '/'),
                        BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant());
            return result;
        }

        internal static void ScheduleSynchronous(string[] args, ManualLogSource log)
        {
            pendingArgs = args;
            pendingLog = log;
        }

        internal static void IsolateStartup(ManualLogSource log)
        {
            // The ordinary menu startup migrates saves and initializes Steam. Neither
            // operation belongs in an explicit resource audit of a copied installation.
            var harmony = new Harmony(Plugin.Id + ".audit");
            var prefix = new HarmonyMethod(typeof(Audit), nameof(SkipMenuStartup));
            harmony.Patch(AccessTools.Method(typeof(MainMenu), "Awake"), prefix: prefix);
            harmony.Patch(AccessTools.Method(typeof(MainMenu), "Start"), prefix: prefix);
            log.LogInfo("Resolute audit: normal menu initialization is suspended for this process.");
        }

        private static bool SkipMenuStartup()
        {
            if (pendingArgs != null && !synchronousStarted)
                RunSynchronously(pendingArgs, pendingLog);
            Action callback = startupCallback;
            startupCallback = null;
            callback?.Invoke();
            return false;
        }

        internal static void RunSynchronously(string[] args, ManualLogSource log)
        {
            if (synchronousStarted) return;
            synchronousStarted = true;
            string output = Argument(args, "--resolute-audit-output");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output))
            {
                log.LogError("--resolute-audit requires --resolute-audit-output followed by an absolute output file path.");
                Application.Quit(2);
                return;
            }

            int exitCode = 0;
            var report = new Dictionary<string, object>
            {
                ["auditVersion"] = 1,
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["unityVersion"] = Application.unityVersion,
                ["gameVersion"] = Application.version,
                ["gameDataPath"] = Application.dataPath,
                ["mode"] = "resource-inspection-only"
            };
            try
            {
                log.LogInfo("Resolute audit synchronously loading Encyclopedia; output: " + output);
                Encyclopedia encyclopedia = Resources.Load<Encyclopedia>("Encyclopedia");
                log.LogInfo("Resolute audit Encyclopedia resource loaded.");
                if (encyclopedia == null)
                    throw new InvalidOperationException("Encyclopedia resource was not found.");

                ShipDefinition donor = Plugin.FindDonor(encyclopedia);
                report["donor"] = donor == null ? null : Reference(donor);
                report["availableShips"] = encyclopedia.ships.Where(ship => ship != null).Select(ship =>
                    new Dictionary<string, object> { ["key"] = ship.jsonKey, ["name"] = ship.unitName, ["prefab"] = Reference(ship.unitPrefab) }).ToArray();
                var ships = new List<object>();
                bool allShips = args.Any(arg => arg == "--resolute-audit-all-ships");
                var selectedShips = allShips ? encyclopedia.ships : new List<ShipDefinition> { donor };
                foreach (ShipDefinition ship in selectedShips.OrderBy(ship => ship == donor ? 0 : 1))
                {
                    if (ship == null)
                        continue;
                    log.LogInfo("Resolute audit inspecting ship " + ship.jsonKey + ".");
                    var entry = new Dictionary<string, object>
                    {
                        ["definition"] = Fields(ship, null, 0),
                        ["prefab"] = Reference(ship.unitPrefab)
                    };
                    if (ship.unitPrefab != null)
                    {
                        entry["hierarchy"] = Hierarchy(ship.unitPrefab);
                        entry["partMassKg"] = ship.unitPrefab.GetComponentsInChildren<UnitPart>(true).Sum(part => part.GetMass());
                        entry["rendererBounds"] = BoundsOf(ship.unitPrefab);
                    }
                    ships.Add(entry);
                }
                report["ships"] = ships;
                report["shipTypes"] = Value(encyclopedia.shipTypes, null, 0);
                report["laserCandidates"] = CaptureLasers(log);
                report["success"] = donor != null;
                if (donor == null)
                    throw new InvalidOperationException("The expected vanilla Destroyer1 donor was not found.");
                log.LogInfo("Resolute audit captured " + ships.Count + " ship prefabs.");
            }
            catch (Exception exception)
            {
                report["success"] = false;
                report["error"] = exception.ToString();
                log.LogError("Resolute audit: " + exception);
                exitCode = 2;
            }

            try
            {
                string fullPath = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, Json(report), new UTF8Encoding(false));
                log.LogInfo("Resolute audit written: " + fullPath);
            }
            catch (Exception exception)
            {
                log.LogError("Could not write Resolute audit: " + exception);
                exitCode = 2;
            }
            Application.Quit(exitCode);
        }

        private static object CaptureLasers(ManualLogSource log)
        {
            var candidates = new List<object>();
            foreach (Laser laser in Resources.FindObjectsOfTypeAll<Laser>())
            {
                Turret turret = laser.GetComponentInParent<Turret>(true);
                Unit unit = laser.GetComponentInParent<Unit>(true);
                Transform root = unit != null ? unit.transform : laser.transform.root;
                candidates.Add(new Dictionary<string, object>
                {
                    ["name"] = laser.name,
                    ["unit"] = Reference(unit),
                    ["unitDefinition"] = unit == null ? null : Reference(unit.definition),
                    ["path"] = PathOf(laser.transform, root),
                    ["laserFields"] = Fields(laser, root, 0),
                    ["turretFields"] = turret == null ? null : Fields(turret, root, 0),
                    ["turretHierarchy"] = turret == null ? null : Hierarchy(turret.gameObject),
                    ["unitParts"] = turret == null ? null : turret.GetComponentsInChildren<UnitPart>(true)
                        .Select(part => Fields(part, root, 0)).ToArray()
                });
            }
            log.LogInfo("Resolute audit captured " + candidates.Count + " native laser candidates.");
            return candidates;
        }

        internal static string Argument(string[] args, string name)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return index + 1 < args.Length ? args[index + 1] : null;
                if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                    return args[index].Substring(name.Length + 1);
            }
            return null;
        }

        internal static object Hierarchy(GameObject prefab)
        {
            var nodes = new List<object>();
            foreach (Transform transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                var components = new List<object>();
                foreach (Component component in transform.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        components.Add(new Dictionary<string, object> { ["missingScript"] = true });
                        continue;
                    }
                    var details = new Dictionary<string, object>
                    {
                        ["type"] = component.GetType().FullName,
                        ["fields"] = Fields(component, prefab.transform, 0)
                    };
                    Behaviour behaviour = component as Behaviour;
                    if (behaviour != null)
                        details["enabled"] = behaviour.enabled;
                    NetworkIdentity identity = component as NetworkIdentity;
                    if (identity != null)
                    {
                        details["prefabHash"] = identity.PrefabHash;
                        details["sceneId"] = identity.SceneId;
                    }
                    Collider collider = component as Collider;
                    if (collider != null)
                    {
                        details["enabled"] = collider.enabled;
                        details["isTrigger"] = collider.isTrigger;
                        details["bounds"] = Bound(collider.bounds);
                        BoxCollider box = collider as BoxCollider;
                        if (box != null)
                        {
                            details["center"] = Vector(box.center);
                            details["size"] = Vector(box.size);
                        }
                        MeshCollider meshCollider = collider as MeshCollider;
                        if (meshCollider != null)
                        {
                            details["convex"] = meshCollider.convex;
                            details["mesh"] = MeshInfo(meshCollider.sharedMesh);
                        }
                    }
                    Renderer renderer = component as Renderer;
                    if (renderer != null)
                    {
                        details["enabled"] = renderer.enabled;
                        details["bounds"] = Bound(renderer.bounds);
                        details["materials"] = renderer.sharedMaterials.Select(MaterialInfo).ToArray();
                    }
                    MeshFilter filter = component as MeshFilter;
                    if (filter != null)
                        details["mesh"] = MeshInfo(filter.sharedMesh);
                    Rigidbody body = component as Rigidbody;
                    if (body != null)
                    {
                        details["mass"] = body.mass;
                        details["drag"] = body.drag;
                        details["angularDrag"] = body.angularDrag;
                        details["isKinematic"] = body.isKinematic;
                        details["useGravity"] = body.useGravity;
                        details["centerOfMass"] = Vector(body.centerOfMass);
                    }
                    components.Add(details);
                }
                nodes.Add(new Dictionary<string, object>
                {
                    ["path"] = PathOf(transform, prefab.transform),
                    ["name"] = transform.name,
                    ["activeSelf"] = transform.gameObject.activeSelf,
                    ["layer"] = transform.gameObject.layer,
                    ["localPosition"] = Vector(transform.localPosition),
                    ["localEulerAngles"] = Vector(transform.localEulerAngles),
                    ["localScale"] = Vector(transform.localScale),
                    ["components"] = components
                });
            }
            return nodes;
        }

        private static object BoundsOf(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return null;
            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);
            return Bound(bounds);
        }

        private static object MeshInfo(Mesh mesh)
        {
            if (mesh == null)
                return null;
            return new Dictionary<string, object>
            {
                ["name"] = mesh.name,
                ["vertices"] = mesh.vertexCount,
                ["subMeshes"] = mesh.subMeshCount,
                ["bounds"] = Bound(mesh.bounds)
            };
        }

        private static object MaterialInfo(Material material)
        {
            if (material == null)
                return null;
            return new Dictionary<string, object>
            {
                ["name"] = material.name,
                ["shader"] = material.shader == null ? null : material.shader.name,
                ["mainTexture"] = Reference(material.mainTexture)
            };
        }

        internal static Dictionary<string, object> Fields(object target, Transform root, int depth)
        {
            var result = new Dictionary<string, object>();
            Type type = target.GetType();
            while (type != null && type != typeof(Object) && type != typeof(Component) && type != typeof(Behaviour) && type != typeof(MonoBehaviour))
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsNotSerialized || (!field.IsPublic && !Attribute.IsDefined(field, typeof(SerializeField))))
                        continue;
                    string key = result.ContainsKey(field.Name) ? type.Name + "." + field.Name : field.Name;
                    try { result[key] = Value(field.GetValue(target), root, depth + 1); }
                    catch (Exception exception) { result[key] = "<error: " + exception.GetType().Name + ">"; }
                }
                type = type.BaseType;
            }
            return result;
        }

        private static object Value(object value, Transform root, int depth)
        {
            if (value == null)
                return null;
            if (value is Object)
            {
                Object unity = (Object)value;
                if (unity == null)
                    return null;
                var result = Reference(unity);
                Component component = unity as Component;
                GameObject gameObject = unity as GameObject;
                Transform transform = component != null ? component.transform : gameObject != null ? gameObject.transform : null;
                if (transform != null && root != null && (transform == root || transform.IsChildOf(root)))
                    result["path"] = PathOf(transform, root);
                return result;
            }
            Type type = value.GetType();
            if (type.IsEnum)
                return value.ToString();
            if (value is string || value is bool || type.IsPrimitive || value is decimal)
                return value;
            if (value is Vector3)
                return Vector((Vector3)value);
            if (value is Vector2)
            {
                Vector2 vector = (Vector2)value;
                return new[] { vector.x, vector.y };
            }
            if (value is Quaternion)
            {
                Quaternion quaternion = (Quaternion)value;
                return new[] { quaternion.x, quaternion.y, quaternion.z, quaternion.w };
            }
            if (depth > 6)
                return "<" + type.FullName + ">";
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                var list = new List<object>();
                foreach (object item in sequence)
                {
                    if (list.Count >= 1024) { list.Add("<truncated>"); break; }
                    list.Add(Value(item, root, depth + 1));
                }
                return list;
            }
            return Fields(value, root, depth);
        }

        private static Dictionary<string, object> Reference(Object value)
        {
            if (value == null)
                return null;
            return new Dictionary<string, object> { ["name"] = value.name, ["type"] = value.GetType().FullName };
        }

        private static object Bound(Bounds bounds)
        {
            return new Dictionary<string, object> { ["center"] = Vector(bounds.center), ["size"] = Vector(bounds.size) };
        }

        private static float[] Vector(Vector3 vector) { return new[] { vector.x, vector.y, vector.z }; }

        private static string PathOf(Transform transform, Transform root)
        {
            var segments = new List<string>();
            while (transform != null)
            {
                segments.Add(transform.name);
                if (transform == root)
                    break;
                transform = transform.parent;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        internal static string Json(object value)
        {
            if (value == null) return "null";
            string text = value as string;
            if (text != null) return Quote(text);
            if (value is bool) return (bool)value ? "true" : "false";
            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                var fields = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                    fields.Add(Quote(Convert.ToString(entry.Key, CultureInfo.InvariantCulture)) + ":" + Json(entry.Value));
                return "{" + string.Join(",", fields) + "}";
            }
            IEnumerable sequence = value as IEnumerable;
            if (sequence != null)
            {
                var elements = new List<string>();
                foreach (object item in sequence) elements.Add(Json(item));
                return "[" + string.Join(",", elements) + "]";
            }
            if (value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value))) return "null";
            if (value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value))) return "null";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Quote(string text)
        {
            var result = new StringBuilder("\"");
            foreach (char character in text ?? "")
            {
                switch (character)
                {
                    case '\"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 32) result.Append("\\u" + ((int)character).ToString("x4"));
                        else result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }
    }
}
