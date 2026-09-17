using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NuclearOption.SavedMission;
using UnityEngine;

namespace Resolute
{
    // Configure the Dynamo's real attached-airbase components before native Awake.
    // Helicopter inventory, spawning, servicing and AI remain the game's systems.
    internal static class CarrierIntegration
    {
        internal const string DeckCollisionName = "Resolute source helideck collision";
        internal static readonly Vector2 LandingCircle = new Vector2(0f, -102f);

        internal static void Configure(Ship ship, Transform visual, Transform sourceReference)
        {
            if (ship == null || visual == null || sourceReference == null)
                throw new ArgumentNullException("Carrier configuration requires the ship and both visual references.");
            Airbase airbase = ship.GetComponent<Airbase>();
            ShipPart deckPart = Named(ship.transform, "Hull_hangarFloor").GetComponent<ShipPart>();
            Hangar hangar = deckPart != null ? deckPart.GetComponent<Hangar>() : null;
            if (airbase == null || deckPart == null || hangar == null)
                throw new InvalidOperationException("The native Dynamo attached airbase or helicopter hangar is missing.");

            MeshFilter deckSource = Named(sourceReference, "rsl_flight_deck_surface48").GetComponent<MeshFilter>();
            if (deckSource == null || deckSource.sharedMesh == null)
                throw new InvalidOperationException("The source helicopter-deck surface is unavailable.");
            Vector3[] points = deckSource.sharedMesh.vertices.Select(v =>
                sourceReference.InverseTransformPoint(deckSource.transform.TransformPoint(v))).ToArray();
            float deckY = points.Average(p => p.y);
            if (points.Length < 3 || points.Max(p => p.y) - points.Min(p => p.y) > .01f)
                throw new InvalidOperationException("The audited source helicopter deck is no longer a flat surface.");
            List<List<Vector2>> profiles = DeckProfiles(points, deckSource.sharedMesh.triangles);
            if (!profiles.Any(p => Inside(p, LandingCircle)))
                throw new InvalidOperationException("The source deck perimeter does not contain its authored landing circle.");

            // The visible source surface is the contact plane. A thin closed prism
            // follows its outer perimeter rather than an oversized donor rectangle.
            // Match the structural damage sections so a detached stern section
            // takes its landing surface with it instead of leaving hidden support.
            foreach (List<Vector2> perimeter in profiles)
            {
                AddDeckCollision(deckPart, visual, Clip(perimeter, false, -98f, true), deckY);
                List<Vector2> aft = Clip(perimeter, false, -98f, false);
                AddDeckCollision(Named(ship.transform, "Hull_RearL").GetComponent<ShipPart>(), visual, Clip(aft, true, 0f, false), deckY);
                AddDeckCollision(Named(ship.transform, "Hull_RearR").GetComponent<ShipPart>(), visual, Clip(aft, true, 0f, true), deckY);
            }
            Vector3 sourceCenter = new Vector3(LandingCircle.x, deckY, LandingCircle.y);
            Transform landing = Named(ship.transform, "LandingPad");
            Transform takeoff = hangar.GetSpawnTransform();
            if (takeoff == null) throw new InvalidOperationException("Native helicopter spawn transform is missing.");
            Place(landing, deckPart.transform, visual, sourceCenter, 180f);
            // Native Dynamo distinguishes the aft-facing approach marker from
            // the forward-facing aircraft spawn and preview. Hangar uses this
            // rotation directly, including the aircraft's authored rest pitch.
            Place(takeoff, deckPart.transform, visual, sourceCenter, 0f);
            Place(airbase.aircraftSelectionTransform, deckPart.transform, visual, sourceCenter, 0f);

            Transform camera = airbase.fixedCameraTransform;
            if (camera == null) throw new InvalidOperationException("Native aircraft-selection camera is missing.");
            camera.SetParent(deckPart.transform, true);
            camera.position = visual.TransformPoint(sourceCenter + new Vector3(15f, 10f, -17f));
            camera.rotation = Quaternion.LookRotation(visual.TransformPoint(sourceCenter + Vector3.up * 2f) - camera.position, visual.up);

            airbase.verticalLandingPoints = new[]
            {
                new Airbase.VerticalLandingPoint
                { point = landing, unitPart = deckPart, approachAngleRange = 90f, size = 20f }
            };
            Write(airbase, "servicePoints", new[] { landing });
            Write(airbase, "attachedUnit", ship);
            hangar.attachedUnit = ship;
            Write(hangar, "criticalPart", deckPart);
            // Keep this existing NetworkBehaviour and the native helicopter types.
            // Do not register the hangar here: Airbase.OnStartServer does that once.
            AircraftDefinition[] helicopters = Read<AircraftDefinition[]>(hangar, "availableAircraft");
            if (helicopters == null || helicopters.Length != 2 ||
                !helicopters.Any(a => a != null && a.jsonKey == "AttackHelo1") ||
                !helicopters.Any(a => a != null && a.jsonKey == "UtilityHelo1"))
                throw new InvalidOperationException("The native Dynamo helicopter inventory has changed.");

            SavedAirbase settings = Read<SavedAirbase>(airbase, "airbaseSettings");
            settings.UniqueName = ship.definition.unitName;
            settings.DisplayName = ship.definition.unitName;
            settings.Capturable = false;
            settings.Disabled = false;
            // Dynamo's 90 m service radius does not reach Resolute's aft pad.
            // Native abandonment must find this attached airbase at every deck edge.
            settings.CaptureRange = Mathf.Max(settings.CaptureRange, points.Max(p => p.magnitude) + 20f);
            if (ship.radar != null && ship.radar.GetScanPoint() != null)
                ship.radar.GetScanPoint().position = Named(ship.transform, "rsl_nav_radar").position;
        }

        private static void AddDeckCollision(ShipPart deck, Transform visual, List<Vector2> perimeter, float topY)
        {
            if (deck == null) throw new InvalidOperationException("A native helicopter-deck damage section is missing.");
            if (perimeter.Count < 3 || Area(perimeter) < .001f) return;
            int count = perimeter.Count;
            var vertices = new Vector3[count * 2];
            for (int i = 0; i < count; i++)
            {
                Vector2 p = perimeter[i];
                vertices[i] = deck.transform.InverseTransformPoint(visual.TransformPoint(new Vector3(p.x, topY, p.y)));
                vertices[count + i] = deck.transform.InverseTransformPoint(visual.TransformPoint(new Vector3(p.x, topY - .25f, p.y)));
            }
            var triangles = new List<int>();
            for (int i = 1; i < count - 1; i++)
            {
                triangles.AddRange(new[] { 0, i + 1, i, count, count + i, count + i + 1 });
            }
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                triangles.AddRange(new[] { i, j, count + j, i, count + j, count + i });
            }
            var mesh = new Mesh { name = DeckCollisionName + " " + deck.name, vertices = vertices, triangles = triangles.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            Collider nativeSurface = deck.GetComponent<Collider>();
            MeshCollider collision = deck.gameObject.AddComponent<MeshCollider>();
            collision.sharedMesh = mesh;
            collision.convex = true;
            collision.isTrigger = false;
            collision.contactOffset = .01f;
            if (nativeSurface != null) collision.sharedMaterial = nativeSurface.sharedMaterial;
        }

        private static List<List<Vector2>> DeckProfiles(Vector3[] points, int[] indices)
        {
            // The outer beam reaches its maximum where the main deck meets the
            // hangar. Two separate forward extensions flank the source's central
            // gap. A single convex hull would fill this gap and the outer steps.
            float seam = points.OrderByDescending(p => Mathf.Abs(p.x)).First().z;
            var port = new List<Vector2>();
            var starboard = new List<Vector2>();
            float surfaceArea = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3[] triangle = { points[indices[i]], points[indices[i + 1]], points[indices[i + 2]] };
                Vector2[] xz = triangle.Select(p => new Vector2(p.x, p.z)).ToArray();
                surfaceArea += Mathf.Abs(Cross(xz[0], xz[1], xz[2])) * .5f;
                if (triangle.All(p => p.z <= seam + .001f)) continue;
                if (triangle.Any(p => p.z < seam - .001f) || (triangle.Any(p => p.x < 0) && triangle.Any(p => p.x > 0)))
                    throw new InvalidOperationException("The source helicopter deck no longer has the audited split hangar extensions.");
                (triangle.Average(p => p.x) < 0 ? port : starboard).AddRange(xz);
            }
            var profiles = new List<List<Vector2>>
            {
                ConvexPerimeter(points.Where(p => p.z <= seam + .001f).Select(p => new Vector2(p.x, p.z))),
                ConvexPerimeter(port), ConvexPerimeter(starboard)
            };
            if (profiles.Any(p => p.Count < 3 || p.Count > 60) || Mathf.Abs(profiles.Sum(Area) - surfaceArea) > .1f)
                throw new InvalidOperationException("Helicopter-deck collision does not match the original surface area.");
            return profiles;
        }

        private static float Area(List<Vector2> polygon)
        {
            float twice = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                twice += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(twice) * .5f;
        }

        private static List<Vector2> ConvexPerimeter(IEnumerable<Vector2> points)
        {
            List<Vector2> sorted = points.Select(p => new Vector2(Mathf.Round(p.x * 10000f) / 10000f, Mathf.Round(p.y * 10000f) / 10000f))
                .Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToList();
            var hull = new List<Vector2>();
            foreach (Vector2 point in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= .0001f) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            int lower = hull.Count;
            for (int i = sorted.Count - 2; i >= 0; i--)
            {
                Vector2 point = sorted[i];
                while (hull.Count > lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= .0001f) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }

        private static List<Vector2> Clip(List<Vector2> polygon, bool xAxis, float boundary, bool greater)
        {
            var output = new List<Vector2>();
            if (polygon.Count == 0) return output;
            Vector2 previous = polygon[polygon.Count - 1];
            float previousDistance = ((xAxis ? previous.x : previous.y) - boundary) * (greater ? 1f : -1f);
            foreach (Vector2 current in polygon)
            {
                float currentDistance = ((xAxis ? current.x : current.y) - boundary) * (greater ? 1f : -1f);
                if ((previousDistance >= 0) != (currentDistance >= 0))
                    output.Add(Vector2.Lerp(previous, current, previousDistance / (previousDistance - currentDistance)));
                if (currentDistance >= 0) output.Add(current);
                previous = current; previousDistance = currentDistance;
            }
            return output;
        }

        private static bool Inside(List<Vector2> polygon, Vector2 point)
        {
            for (int i = 0; i < polygon.Count; i++)
                if (Cross(polygon[i], polygon[(i + 1) % polygon.Count], point) < -.001f) return false;
            return true;
        }

        private static void Place(Transform point, Transform parent, Transform visual, Vector3 source, float yaw)
        {
            if (point == null) throw new InvalidOperationException("A native helicopter-deck anchor is missing.");
            point.SetParent(parent, true);
            point.SetPositionAndRotation(visual.TransformPoint(source), visual.rotation * Quaternion.Euler(0, yaw, 0));
        }

        internal static Transform Named(Transform root, string name)
        {
            Transform[] found = root.GetComponentsInChildren<Transform>(true).Where(t => t.name == name).ToArray();
            if (found.Length != 1) throw new InvalidOperationException("Expected one carrier component named " + name + ".");
            return found[0];
        }

        internal static T Read<T>(object instance, string name)
        { return (T)AccessTools.Field(instance.GetType(), name).GetValue(instance); }

        private static void Write(object instance, string name, object value)
        {
            FieldInfo field = AccessTools.Field(instance.GetType(), name);
            if (field == null) throw new MissingFieldException(instance.GetType().Name, name);
            field.SetValue(instance, value);
        }
    }
}
