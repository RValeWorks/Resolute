using System;
using System.Collections.Generic;
using UnityEngine;

namespace Resolute
{
    internal static class ScrewGeometry
    {
        internal sealed class Measurement
        {
            internal float ForwardRotation, ForwardReaction, SelectedArea, AgreeingArea, AxisForwardDot;
            internal int BladeTriangles;
            internal Vector3 Axis;
            internal object Json(string name) => new Dictionary<string, object>
            {
                ["propeller"] = name, ["meshDerivedForwardRotationSign"] = ForwardRotation,
                ["forwardReactionForPositiveLocalRotation"] = ForwardReaction,
                ["selectedBladeArea"] = SelectedArea, ["agreeingBladeAreaFraction"] = AgreeingArea / SelectedArea,
                ["bladeTriangles"] = BladeTriangles, ["shaftAxisWorld"] = new[] { Axis.x, Axis.y, Axis.z },
                ["shaftAxisDotShipForward"] = AxisForwardDot
            };
        }

        internal static Measurement Measure(Transform propeller, Transform ship)
        {
            Mesh mesh = propeller.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("A screw has no inspectable blade mesh.");
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Matrix4x4 world = propeller.localToWorldMatrix;
            Vector3 axis = world.MultiplyVector(Vector3.forward).normalized;
            Vector3 forward = ship.forward;
            float maxRadius = 0f;
            foreach (Vector3 v in vertices) maxRadius = Mathf.Max(maxRadius, new Vector2(v.x, v.y).magnitude);
            var result = new Measurement { Axis = axis, AxisForwardDot = Vector3.Dot(axis, forward) };
            float positiveArea = 0f, negativeArea = 0f;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Vector3 center = (a + b + c) / 3f;
                if (new Vector2(center.x, center.y).magnitude <= maxRadius * .38f) continue;
                Vector3 normalArea = Vector3.Cross(world.MultiplyVector(b - a), world.MultiplyVector(c - a));
                float twiceArea = normalArea.magnitude;
                if (twiceArea < .000001f) continue;
                Vector3 normal = normalArea / twiceArea;
                if (Mathf.Abs(Vector3.Dot(normal, axis)) <= .2f) continue;
                // Differentiate the actual transformed vertex under a positive
                // local-Z rotation. This includes mirrored/nonuniform parents.
                Vector3 velocity = world.MultiplyVector(Vector3.Cross(Vector3.forward, center));
                // A moving blade pushes water along its surface normal; reaction
                // on the ship is opposite. Reversing triangle winding cancels out.
                float reaction = -Vector3.Dot(normal, velocity) * Vector3.Dot(normal, forward);
                float area = twiceArea * .5f;
                result.ForwardReaction += area * reaction;
                result.SelectedArea += area; result.BladeTriangles++;
                if (reaction > 0f) positiveArea += area;
                else if (reaction < 0f) negativeArea += area;
            }
            if (result.SelectedArea <= 0f || Mathf.Abs(result.ForwardReaction) < .001f || Mathf.Abs(result.AxisForwardDot) < .95f)
                throw new InvalidOperationException("The authored screw pitch or shaft axis is ambiguous: " + propeller.name);
            result.ForwardRotation = Mathf.Sign(result.ForwardReaction);
            result.AgreeingArea = result.ForwardRotation > 0f ? positiveArea : negativeArea;
            if (result.AgreeingArea / result.SelectedArea < .9f)
                throw new InvalidOperationException("The screw's blade surfaces disagree about handedness: " + propeller.name);
            return result;
        }

        internal static void Configure(ResoluteDeckAnimation animation)
        {
            animation.ForwardRotation = new float[animation.Propellers.Length];
            for (int i = 0; i < animation.Propellers.Length; i++)
                animation.ForwardRotation[i] = Measure(animation.Propellers[i], animation.Ship.transform).ForwardRotation;
        }
    }
}
