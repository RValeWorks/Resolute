using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Resolute
{
    // Isolated native diagnostic, invoked while the exo trial owns a real local
    // player. Hull motions are explicit fixtures; rounds and impacts are native.
    internal static class NaturalRailgunAimTrial
    {
        private static Ship owner, target;
        private static Gun gun;
        private static Transform muzzle;
        private static int rounds, hits;
        private static float closest, originError, velocityError;
        private static readonly List<object> shots = new List<object>();

        internal static IEnumerator Run(Ship ship, FactionHQ hostile, Dictionary<string, object> report,
            Dictionary<string, object> result, List<object> checks, string output)
        {
            if (!string.Equals(Path.GetFileName(Directory.GetParent(Application.dataPath).FullName), "test-game", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Railgun trial requires isolated test-game.");
            owner = ship; rounds = hits = 0; closest = float.PositiveInfinity; originError = velocityError = 0f; shots.Clear();
            WeaponStation station = ship.weaponStations.Single(s => s.WeaponInfo.name == "rsl_155mm");
            gun = station.Weapons.OfType<Gun>().Single();
            Turret turret = station.GetTurret();
            muzzle = NaturalArmament.Get<Transform[]>(gun, "muzzles")[0];
            var row = new Dictionary<string, object> { ["scope"] = "Native command order, native turret AimSolver, native Gun/BulletSim and registered hull hits. Both hulls have explicitly maintained crossing velocity/height; no aim, projectile, spread, hitbox or damage mutations.",
                ["requestedRangeMetres"] = 16000f, ["ownerSpeedMps"] = 8f, ["targetSpeedMps"] = 15f, ["shots"] = shots };
            result["railgunMovingTarget"] = row;
            var corridor = new Dictionary<string, object>(); row["oceanCorridor"] = corridor;
            object[] args = { 16000f, corridor, default(GlobalPosition), default(GlobalPosition), Vector3.zero, 1500f };
            AccessTools.Method(typeof(NaturalWeaponsTrial), "FindLongWaterLeg").Invoke(null, args);
            GlobalPosition start = (GlobalPosition)args[2], end = (GlobalPosition)args[3];
            Vector3 forward = (Vector3)args[4], lateral = Vector3.Cross(Vector3.up, forward);
            Vector3 oldPosition = ship.rb.position, oldVelocity = ship.rb.velocity;
            Quaternion oldRotation = ship.rb.rotation;
            bool oldGravity = ship.rb.useGravity;
            RigidbodyConstraints oldConstraints = ship.rb.constraints;
            var hooks = new Harmony(Plugin.Id + ".railgun-aim-trial");
            try
            {
                var hull = Plugin.FindDonor(Encyclopedia.i);
                end.y = Datum.SeaLevel.y + hull.spawnOffset.y;
                target = NetworkSceneSingleton<Spawner>.i.SpawnShip(hull.unitPrefab, end,
                    Quaternion.LookRotation(lateral), hostile, "railgun_crossing_target", 1f, true);
                MissionTrial.HoldControllers(target);
                start.y = oldPosition.ToGlobalPosition().y;
                ship.rb.position = start.ToLocalPosition(); ship.rb.rotation = Quaternion.LookRotation(forward);
                float ownerY = ship.rb.position.y, targetY = target.rb.position.y;
                hooks.Patch(AccessTools.Method(typeof(BulletSim), nameof(BulletSim.AddBullet)),
                    prefix: new HarmonyMethod(typeof(NaturalRailgunAimTrial), nameof(Shot)));
                hooks.Patch(AccessTools.Method(typeof(BulletSim.Bullet), nameof(BulletSim.Bullet.TrajectoryTrace)),
                    postfix: new HarmonyMethod(typeof(NaturalRailgunAimTrial), nameof(Trace)));
                hooks.Patch(AccessTools.Method(typeof(Unit), nameof(Unit.RegisterHit)),
                    prefix: new HarmonyMethod(typeof(NaturalRailgunAimTrial), nameof(Hit)));
                float warm = Time.time + 2f;
                while (Time.time < warm)
                { Maintain(ship, lateral * 8f, ownerY); Maintain(target, lateral * 15f, targetY); yield return new WaitForFixedUpdate(); }
                NaturalWeaponsTrial.Know(ship.NetworkHQ, target);
                float health = target.parts.Sum(p => Mathf.Max(0f, p.hitPoints));
                string reason;
                bool ordered = ResoluteCommandApi.Attack(ship, ResoluteCommandApi.WeaponKey(station.WeaponInfo), target, 1, out reason);
                row["orderAccepted"] = ordered; row["orderReason"] = reason;
                Require(checks, "railgun-native-moving-target-order-accepted", ordered);
                float until = Time.time + 75f;
                while (Time.time < until && !target.disabled && (hits == 0 || rounds < 3))
                {
                    Maintain(ship, lateral * 8f, ownerY); Maintain(target, lateral * 15f, targetY);
                    NaturalWeaponsTrial.Know(ship.NetworkHQ, target);
                    yield return new WaitForFixedUpdate();
                }
                AimSolver solver = NaturalArmament.Get<AimSolver>(turret, "aimSolver");
                bool binding = NaturalArmament.Get<Transform>(solver, "firingTransform") == muzzle &&
                    gun.velocityInherit == ship.rb && !NaturalArmament.Get<bool>(solver, "artillery") &&
                    NaturalArmament.Get<float>(solver, "rakeAmount") == 0f;
                row["solverUsesFittedMuzzleAndMovingTargetMode"] = binding;
                row["rounds"] = rounds; row["nativeRegisteredHits"] = hits;
                row["closestSampleMetres"] = float.IsInfinity(closest) ? (object)null : closest;
                row["maximumShotOriginErrorMetres"] = originError;
                row["maximumInheritedVelocityErrorMps"] = velocityError;
                row["targetHealthBefore"] = health;
                row["targetHealthAfter"] = target.parts.Sum(p => Mathf.Max(0f, p.hitPoints));
                row["commandStatus"] = ResoluteCommandApi.GetStatus(ship);
                File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));
                Require(checks, "railgun-native-fitted-muzzle-and-motion-solver", binding);
                Require(checks, "railgun-native-rounds-preserve-origin-and-inherited-motion", rounds >= 3 && originError < .01f && velocityError < .01f);
                Require(checks, "railgun-native-crossing-hull-hit-from-moving-platform", hits > 0);
            }
            finally
            {
                ResoluteCommandApi.CeaseFire(ship, out _);
                hooks.UnpatchSelf();
                if (target != null) NaturalWeaponsTrial.RetireFixtureObject(target.gameObject, true);
                ship.rb.position = oldPosition; ship.rb.rotation = oldRotation; ship.rb.velocity = oldVelocity;
                ship.rb.useGravity = oldGravity; ship.rb.constraints = oldConstraints;
                owner = target = null; gun = null; muzzle = null;
            }
        }

        private static void Maintain(Ship ship, Vector3 velocity, float y)
        {
            ship.rb.useGravity = false; ship.rb.constraints = RigidbodyConstraints.FreezeRotation;
            ship.rb.velocity = velocity; ship.rb.angularVelocity = Vector3.zero;
            Vector3 point = ship.rb.position; point.y = y; ship.rb.position = point;
        }
        private static void Shot(BulletSim __instance, Transform muzzle, Vector3 inheritedVelocity, Unit ___owner, WeaponInfo ___weaponInfo)
        {
            if (owner == null || ___owner != owner || ___weaponInfo != gun.info) return;
            rounds++;
            originError = Mathf.Max(originError, Vector3.Distance(muzzle.position, NaturalRailgunAimTrial.muzzle.position));
            Vector3 expected = gun.velocityInherit.velocity + muzzle.forward * NaturalArmament.Get<float>(gun, "muzzleVelocity");
            velocityError = Mathf.Max(velocityError, (inheritedVelocity - expected).magnitude);
            if (shots.Count < 32) shots.Add(new Dictionary<string, object> { ["time"] = Time.time,
                ["muzzleGlobal"] = V(muzzle.GlobalPosition().AsVector3()), ["initialVelocity"] = V(inheritedVelocity),
                ["targetGlobal"] = V(target.GlobalPosition().AsVector3()), ["targetVelocity"] = V(target.rb.velocity),
                ["ownerVelocity"] = V(owner.rb.velocity) });
        }
        private static void Trace(BulletSim.Bullet __instance, WeaponInfo info, Unit owner)
        {
            if (NaturalRailgunAimTrial.owner == null || owner != NaturalRailgunAimTrial.owner || info != gun.info || target == null) return;
            closest = Mathf.Min(closest, (__instance.position - target.GlobalPosition()).magnitude);
        }
        private static void Hit(Unit __instance, Unit hitUnit, WeaponInfo weaponInfo)
        { if (owner != null && __instance == owner && hitUnit == target && weaponInfo == gun.info) hits++; }
        private static float[] V(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
        private static void Require(List<object> checks, string name, bool passed)
        {
            checks.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed });
            if (!passed) throw new InvalidOperationException("Railgun aim trial failed: " + name);
        }
    }
}
