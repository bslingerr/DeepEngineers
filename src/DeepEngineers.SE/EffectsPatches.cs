using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The five S7 effect postfixes (CONTRACT §5.4 "C2 effect events", the type table and "Game members S7 uses"; same
 /// Harmony id <c>deepengineers.se</c>). Every target is looked up with explicit parameter types
 /// and its shape checked before patching; a missing or changed target logs
 /// <c>DE.Harmony MISSING &lt;label&gt; (feature off)</c> once and leaves only that event kind off (never the display
 /// bridge, never another kind). The bodies never throw into SE and append O(1) to <see cref="S7Effects.Window"/>.
 /// <list type="bullet">
 /// <item><c>Sandbox.Game.Weapons.MyGunBase.Shoot(Vector3D initialPosition, Vector3 initialVelocity, Vector3 direction,
 /// renderId, bool userNormalizedPositionForEffects)</c>, public instance void, every other
 /// <c>Shoot</c> ends here: WeaponMuzzle and BulletStreak (S7.M12 turns it off).</item>
 /// <item><c>Sandbox.Game.Weapons.MyProjectile.OnHit(IMyEntity entity, ref MyHitInfo hitInfo, object customdata, ref
 /// LineD line)</c>, public instance void of a struct: Impact.</item>
 /// <item><c>Sandbox.Game.MyExplosions.AddExplosion(ref MyExplosionInfo explosionInfo, bool updateSync)</c>, public
 /// static bool: Explosion when it returns true.</item>
 /// <item><c>Sandbox.Game.Entities.Cube.MyGridPhysics.PlayCollisionParticlesInternal(IMyEntity otherEntity, ref
 /// Vector3D worldPosition, ref Vector3 normal, ref Vector3 separatingVelocity, float separatingSpeed, float impulse,
 /// MyEntity.ContactPointData.ContactPointDataTypes flags)</c>, public instance void:
 /// GridImpact for <c>Particle_Collision</c> or <c>Particle_GridCollision</c>.</item>
 /// <item><c>Sandbox.Game.GameSystems.MyGridGasSystem.AddDepressurizationEffects(MyCubeGrid grid, Vector3I from,
 /// Vector3I to)</c>, public static void
 /// HullBreak.</item>
 /// </list>
 /// Thrusters, tools and splashes are polled per tick by <see cref="S7Effects"/> (no patch).
 /// </summary>
    internal static class S7EffectPatches
    {
        public const string ShootTarget = "MyGunBase.Shoot(Vector3D, Vector3, Vector3, MyEntity, uint?, bool)";
        public const string HitTarget = "MyProjectile.OnHit(IMyEntity, ref MyHitInfo, object, ref LineD)";
        public const string ExplosionTarget = "MyExplosions.AddExplosion(ref MyExplosionInfo, bool)";
        public const string CollisionTarget = "MyGridPhysics.PlayCollisionParticlesInternal(IMyEntity, ref Vector3D, ref Vector3, ref Vector3, float, float, ContactPointDataTypes)";
        public const string HullTarget = "MyGridGasSystem.AddDepressurizationEffects(MyCubeGrid, Vector3I, Vector3I)";

        private static readonly object Gate = new object();
        public static readonly List<string> Applied = new List<string>();
        public static readonly List<string> Missing = new List<string>();
        public static bool Attempted { get; private set; }
        public static bool ShootOn { get; private set; }
        public static bool HitOn { get; private set; }
        public static bool ExplosionOn { get; private set; }
        public static bool CollisionOn { get; private set; }
        public static bool HullOn { get; private set; }

        /// <summary>
 /// Patches once per process (idempotent; the S7 host calls it at start, <see cref="S7Effects"/> on its first tick
 /// when nobody did). Inside its own try/catch per target.
 /// </summary>
        public static void Apply()
        {
            lock (Gate)
            {
                if (Attempted) return;
                Attempted = true;
            }
            Harmony harmony;
            try { harmony = new Harmony(HarmonyPatches.HarmonyId); }
            catch (Exception e)
            {
                DeLog.Info("DE.Harmony S7 effects FAIL " + e.GetType().Name + ": " + e.Message + " (no SE effect events; S0-S7a unaffected)");
                return;
            }

            MethodInfo shoot = AccessTools.Method(typeof(MyGunBase), "Shoot",
                new[] { typeof(Vector3D), typeof(Vector3), typeof(Vector3), typeof(MyEntity), typeof(uint?), typeof(bool) });
            ShootOn = Patch(harmony, ShootTarget, shoot, false, typeof(void), typeof(MyGunBase), nameof(ShootPostfix), "WeaponMuzzle, BulletStreak");

            MethodInfo hit = AccessTools.Method(typeof(MyProjectile), "OnHit",
                new[] { typeof(IMyEntity), typeof(MyHitInfo).MakeByRefType(), typeof(object), typeof(LineD).MakeByRefType() });
            HitOn = Patch(harmony, HitTarget, hit, false, typeof(void), typeof(MyProjectile), nameof(HitPostfix), "Impact");

            MethodInfo explosion = AccessTools.Method(typeof(MyExplosions), "AddExplosion",
                new[] { typeof(MyExplosionInfo).MakeByRefType(), typeof(bool) });
            ExplosionOn = Patch(harmony, ExplosionTarget, explosion, true, typeof(bool), typeof(MyExplosions), nameof(ExplosionPostfix), "Explosion");

            MethodInfo collision = AccessTools.Method(typeof(MyGridPhysics), "PlayCollisionParticlesInternal",
                new[]
                {
                    typeof(IMyEntity), typeof(Vector3D).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Vector3).MakeByRefType(),
                    typeof(float), typeof(float), typeof(MyEntity.ContactPointData.ContactPointDataTypes),
                });
            CollisionOn = Patch(harmony, CollisionTarget, collision, false, typeof(void), typeof(MyGridPhysics), nameof(CollisionPostfix), "GridImpact");

            MethodInfo hull = AccessTools.Method(typeof(MyGridGasSystem), "AddDepressurizationEffects",
                new[] { typeof(MyCubeGrid), typeof(Vector3I), typeof(Vector3I) });
            HullOn = Patch(harmony, HullTarget, hull, true, typeof(void), typeof(MyGridGasSystem), nameof(HullPostfix), "HullBreak");

            // S7p-scene-above: the projector's show/hide postfix (P7) rides this registration; missiles need no patch
 // (MyMissiles.GetAllMissilesInSphere is polled by the S5 producer, GridMirrorProducer.S7p.cs)
            S7pScenePatches.Apply(harmony);

            DeLog.Info("DE.Harmony S7 effects " + (Missing.Count == 0 ? "OK " : "PARTIAL ") + Applied.Count + "/5 postfixes" +
                       (Missing.Count > 0 ? " missing: " + string.Join("; ", Missing.ToArray()) : "") + " (thrusters, tools, splashes polled per tick)");
        }

        public static string Describe()
        {
            return (ShootOn ? "shoot " : "-shoot ") + (HitOn ? "hit " : "-hit ") + (ExplosionOn ? "explosion " : "-explosion ") +
                   (CollisionOn ? "collision " : "-collision ") + (HullOn ? "hull" : "-hull");
        }

        private static bool Patch(Harmony harmony, string label, MethodInfo target, bool expectStatic, Type expectReturn, Type declaring, string postfix, string kinds)
        {
            if (target == null || target.IsStatic != expectStatic || target.ReturnType != expectReturn || target.DeclaringType != declaring)
            {
                string why = target == null ? "not found" : "shape changed (static=" + target.IsStatic + " returns " + target.ReturnType.Name + " declared by " + target.DeclaringType.Name + ")";
                Missing.Add(label + " " + why);
                DeLog.Info("DE.Harmony MISSING " + label + " " + why + " (feature off: " + kinds + " events)");
                return false;
            }
            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(S7EffectPatches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
                Applied.Add(label);
                Patches info = Harmony.GetPatchInfo(target);
                int ours = info == null ? 0 : info.Postfixes.Count(p => p.owner == HarmonyPatches.HarmonyId);
                DeLog.Info("DE.Harmony S7 OK " + label + " postfix (" + kinds + "; " + ours + " deepengineers.se postfix(es))");
                return true;
            }
            catch (Exception e)
            {
                Missing.Add(label + " patch failed: " + e.GetType().Name);
                DeLog.Info("DE.Harmony MISSING " + label + " patch failed: " + e.GetType().Name + ": " + e.Message + " (feature off: " + kinds + " events)");
                return false;
            }
        }

        // ============================================================================================ postfix bodies
 // Never throw: an exception here would break SE's weapons, projectiles, explosions, contacts or gas system.

        private static void ShootPostfix(MyGunBase __instance, Vector3D initialPosition, Vector3 direction, MyEntity owner)
        {
            try { S7Effects.OnShoot(__instance, initialPosition, direction, owner); }
            catch (Exception e) { S7Effects.PostfixError("shoot", e); }
        }

        private static void HitPostfix(IMyEntity entity, ref MyHitInfo hitInfo)
        {
            try { S7Effects.OnProjectileHit(entity, hitInfo.Position, hitInfo.Normal); }
            catch (Exception e) { S7Effects.PostfixError("hit", e); }
        }

        private static void ExplosionPostfix(bool __result, ref MyExplosionInfo explosionInfo)
        {
            try
            {
                // S11 hook: the tick's explosions are grouped (stacked warheads sum their yields) and raised at the tick's flush
                if (__result && !S11Blasts.Absorb(ref explosionInfo)) S7Effects.OnExplosion(explosionInfo.ExplosionSphere, (byte)explosionInfo.ExplosionType, explosionInfo.Damage);
            }
            catch (Exception e) { S7Effects.PostfixError("explosion", e); }
        }

        private static void CollisionPostfix(MyGridPhysics __instance, ref Vector3D worldPosition, ref Vector3 normal, float separatingSpeed, float impulse,
                                             MyEntity.ContactPointData.ContactPointDataTypes flags)
        {
            // review F23: runs on Havok worker threads in single player
 // exit before any math while the effects are inactive. v3 note: on a multiplayer
 // host the contact goes through MyCubeGrid.UpdateParticleContactPoint instead and never reaches this
 // postfix, so GridImpact must also hook that path (or the relayed event) for v3.
            if (!S7Effects.Active) return;
            try
            {
                const MyEntity.ContactPointData.ContactPointDataTypes particles =
                    MyEntity.ContactPointData.ContactPointDataTypes.Particle_Collision | MyEntity.ContactPointData.ContactPointDataTypes.Particle_GridCollision;
                if ((flags & particles) == 0 || __instance == null) return;
                S7Effects.OnGridCollision(__instance.Entity as MyCubeGrid, worldPosition, normal, separatingSpeed, impulse);
            }
            catch (Exception e) { S7Effects.PostfixError("collision", e); }
        }

        private static void HullPostfix(MyCubeGrid grid, Vector3I from, Vector3I to)
        {
            try { S7Effects.OnDepressurization(grid, from, to); }
            catch (Exception e) { S7Effects.PostfixError("hull", e); }
        }
    }
}
