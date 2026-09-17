using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Resolute
{
    internal static class RearMountFiringArcs
    {
        // Keep the historical type name for existing diagnostic readers. Apply
        // one authored-sector rule to every CIWS and trainable PD mount.
        private static readonly string[] MountKeys = {
            "rsl_ciws_fp_yaw", "rsl_ciws_fs_yaw", "rsl_ciws_aft_yaw", "rsl_ciws_al_yaw",
            "rsl_pd_fwd_yaw", "rsl_pd_fl_yaw", "rsl_pd_ap_yaw", "rsl_pd_as_yaw" };

        internal static void Configure(Ship ship, Transform source,
            IDictionary<string, Transform> sourceNodes, string anchorPath)
        {
            JObject anchors = JObject.Parse(File.ReadAllText(anchorPath));
            foreach (string key in MountKeys)
            {
                JObject rig = anchors["turrets"].OfType<JObject>().Single(r => (string)r["yaw"] == key);
                float[] yaw = rig["yawRange"].ToObject<float[]>();
                float[] elevation = rig["elevationRange"].ToObject<float[]>();
                if (yaw.Length != 2 || elevation.Length != 2 || yaw.Concat(elevation).Any(v => float.IsNaN(v) || float.IsInfinity(v)) ||
                    yaw[0] < -180f || yaw[1] > 180f || yaw[1] <= yaw[0] || yaw[1] - yaw[0] >= 180f)
                    throw new InvalidDataException("Invalid authored firing sector: " + key);
                ExtendDartSternCoverage(key, yaw);
                Turret turret = sourceNodes[key].GetComponentInParent<Turret>(true);
                Transform support = turret != null ? turret.transform.parent : null;
                UnitPart supportPart = support != null ? support.GetComponent<UnitPart>() : null;
                if (turret == null || supportPart == null || supportPart.parentUnit != ship)
                    throw new InvalidOperationException("Turret has no direct native structural support: " + key);

                // Native traverseRange is a symmetric clamp around the parent
                // transform's forward axis. A +/-90 clamp truncates these
                // outward sectors. Native firing cones provide their yaw limits
                // without inserting a parent that would break UnitPart.Awake's
                // direct-parent damage attachment.
                NaturalArmament.Set(turret, "traverseRange", 360f);
                NaturalArmament.Set(turret, "minElevation", elevation[0]);
                NaturalArmament.Set(turret, "maxElevation", elevation[1]);
                NaturalArmament.Set(turret, "firesWithoutAiming", false);

                // Two horizontal half-planes preserve the same azimuth limits
                // at every elevation. A single spherical cone would incorrectly
                // narrow or widen the authored yaw sector as targets climb.
                var cones = new FiringCone[2];
                float[] headings = { yaw[0] + 90f, yaw[1] - 90f };
                for (int i = 0; i < cones.Length; i++)
                {
                    var boundary = new GameObject("ResoluteRearArcBoundary" + i + "_" + key).transform;
                    boundary.SetParent(support, false);
                    boundary.SetPositionAndRotation(turret.transform.position, source.rotation * Quaternion.Euler(0f, headings[i], 0f));
                    cones[i] = new FiringCone();
                    NaturalArmament.Set(cones[i], "transform", boundary);
                    NaturalArmament.Set(cones[i], "coneAngle", 90f);
                    NaturalArmament.Set(cones[i], "exclusion", false);
                }
                NaturalArmament.Set(turret, "firingCones", cones);
                ResoluteRearMountArc arc = turret.gameObject.AddComponent<ResoluteRearMountArc>();
                arc.Turret = turret; arc.SourceFrame = source; arc.SourceYaw = key;
                arc.MinimumAzimuth = yaw[0]; arc.MaximumAzimuth = yaw[1];
                arc.MinimumElevation = elevation[0]; arc.MaximumElevation = elevation[1];
            }
        }

        internal static void ExtendDartSternCoverage(string key, float[] yaw)
        {
            // The aft launchers sit 11.38m off the centerline. A stem-on
            // contact crosses each mount's +/-180 seam even at long range.
            // Two degrees of overlap cover that parallax at Dart's 500m
            // minimum range. Native pitch and the actual-cell clearance gate
            // still prevent firing into the nearby aft CIWS. Keep customized
            // authored sectors, all CIWS and both forward launchers unchanged.
            if (key == "rsl_pd_ap_yaw" && yaw[0] == 8f && yaw[1] == 178f) yaw[1] = 182f;
            else if (key == "rsl_pd_as_yaw" && yaw[0] == -178f && yaw[1] == -8f) yaw[0] = -182f;
        }
    }

    // Native Turret does not serialize traverseAngle or initialize it from the
    // authored yaw pose. Seed that field once per clone, preserving the existing
    // outward rest pose. Native aiming, readiness and occlusion remain in charge.
    [DefaultExecutionOrder(-50)]
    internal sealed class ResoluteRearMountArc : MonoBehaviour
    {
        public Turret Turret;
        public Transform SourceFrame;
        public string SourceYaw;
        public float MinimumAzimuth, MaximumAzimuth, MinimumElevation, MaximumElevation;
        [NonSerialized] public bool NativeAngleInitialized;

        private void Awake()
        {
            if (Turret == null || Turret.gameObject != gameObject)
                throw new InvalidOperationException("Rear turret arc lost its native turret reference.");
            NaturalArmament.Set(Turret, "traverseAngle", Mathf.DeltaAngle(0f, Turret.transform.localEulerAngles.y));
            NativeAngleInitialized = true;
        }
    }
}
