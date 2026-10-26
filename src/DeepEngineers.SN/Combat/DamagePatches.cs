using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>The innermost damage context of the current call stack (CONTRACT §2.7 "The hit point w").</summary>
    internal struct DamageContext
    {
        /// <summary>0 none, 1 mouth stash (MeleeAttack.OnTouch), 2 ampeel (ShockerMeleeAttack.OnTouch), 3 blast (DamageSystem.RadiusDamage).</summary>
        public int Kind;
        public Collider Col;
        public GameObject Creature, Target, Source;
        public Vector3 Point;
        public float Radius;

        public const int None = 0, Mouth = 1, Shocker = 2, Blast = 3;
    }

    /// <summary>
 /// The S6 Harmony patches of the Subnautica side (id <c>deepengineers.sn.s6</c>; installed once at the S6 host's start,
 /// never removed: every body checks <see cref="Host"/> and returns when the host is gone). Targets
 /// <list type="number">
 /// <item><c>public bool LiveMixin.TakeDamage(float originalDamage, Vector3 position, DamageType type, GameObject dealer)</c>
 /// the prefix records the game's gate (health &gt; 0, not invincible, not invisible-mode with
 /// invincibleInCreative or a BaseCell) in <c>__state</c>; the postfix emits one C8 record per call for our targets.</item>
 /// <item><c>protected bool MeleeAttack.CanDealDamageTo(GameObject target)</c>: the
 /// postfix turns false into true for an enabled proxy marker whose combat gate is open, with the game's own
 /// line-of-sight rule counting every section of the same root.</item>
 /// <item><c>public virtual void MeleeAttack.OnTouch(Collider collider)</c>: prefix + finalizer, the
 /// mouth stash (ReaperMeleeAttack reaches it through base.OnTouch).</item>
 /// <item><c>public override void ShockerMeleeAttack.OnTouch(Collider collider)</c> (ShockerMeleeAttack.cs; it never calls
 /// the base and passes no dealer): prefix + finalizer, the ampeel context.</item>
 /// <item><c>public static void DamageSystem.RadiusDamage(float maxDamage, Vector3 position, float detonateRadius,
 /// DamageType type, GameObject sourceObject)</c>: prefix +
 /// finalizer, the blast context (radius flag).</item>
 /// <item>Optional: <c>public static void CrafterLogic.NotifyCraftEnd(GameObject target, TechType techType)</c>
 /// postfix, s6.spawn rows.</item>
 /// </list>
 /// The contexts form a small stack (prefix pushes, finalizer restores the depth the prefix saw), so nested calls
 /// (ShockerMeleeAttack.OnMouthTouch → OnTouch, then base.OnTouch) keep the innermost one on top. Patch bodies never
 /// throw into the game.
 /// </summary>
    internal static class DamagePatches
    {
        public const string HarmonyId = "deepengineers.sn.s6";
        private static Harmony _harmony;
        private static bool _installed;

        public static bool InstalledTakeDamage, InstalledCanDeal, InstalledOnTouch, InstalledShocker, InstalledRadius, InstalledSpawnLog;
        public static string Missing = "";
        /// <summary>The running S6 host (null: every patch body returns at once).</summary>
        public static volatile S6Host Host;

        private const int MaxDepth = 16;
        private static readonly DamageContext[] Stack = new DamageContext[MaxDepth];
        private static int _depth;
        public static long ContextOverflow, CanDealAccepted, PatchErrors;

        public static DamageContext Top { get { return _depth > 0 && _depth <= MaxDepth ? Stack[_depth - 1] : default(DamageContext); } }

        public static string Describe()
        {
            return "takeDamage=" + Flag(InstalledTakeDamage) + " canDeal=" + Flag(InstalledCanDeal) + " onTouch=" + Flag(InstalledOnTouch) +
                   " shocker=" + Flag(InstalledShocker) + " radius=" + Flag(InstalledRadius) + " spawnLog=" + Flag(InstalledSpawnLog);
        }

        private static string Flag(bool b) { return b ? "on" : "MISSING"; }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try { _harmony = new Harmony(HarmonyId); }
            catch (Exception e)
            {
                Missing = "Harmony instance";
                DeLog.Error("DE.Harmony S6 FAILED creating the Harmony instance: " + e.GetType().Name + ": " + e.Message + " (no damage forwarding, no bites on proxies)");
                return;
            }
            InstalledTakeDamage = Patch(typeof(LiveMixin), "TakeDamage", new[] { typeof(float), typeof(Vector3), typeof(DamageType), typeof(GameObject) }, typeof(bool), false,
                "Prefix_TakeDamage", "Postfix_TakeDamage", null, "LiveMixin.TakeDamage(float, Vector3, DamageType, GameObject)");
            InstalledCanDeal = Patch(typeof(MeleeAttack), "CanDealDamageTo", new[] { typeof(GameObject) }, typeof(bool), false,
                null, "Postfix_CanDeal", null, "MeleeAttack.CanDealDamageTo(GameObject)");
            InstalledOnTouch = Patch(typeof(MeleeAttack), "OnTouch", new[] { typeof(Collider) }, typeof(void), false,
                "Prefix_OnTouch", null, "Finalizer_Pop", "MeleeAttack.OnTouch(Collider)");
            InstalledShocker = Patch(typeof(ShockerMeleeAttack), "OnTouch", new[] { typeof(Collider) }, typeof(void), false,
                "Prefix_Shocker", null, "Finalizer_Pop", "ShockerMeleeAttack.OnTouch(Collider)");
            InstalledRadius = Patch(typeof(DamageSystem), "RadiusDamage", new[] { typeof(float), typeof(Vector3), typeof(float), typeof(DamageType), typeof(GameObject) }, typeof(void), true,
                "Prefix_Radius", null, "Finalizer_Pop", "DamageSystem.RadiusDamage(float, Vector3, float, DamageType, GameObject)");
            InstalledSpawnLog = Patch(typeof(CrafterLogic), "NotifyCraftEnd", new[] { typeof(GameObject), typeof(TechType) }, typeof(void), true,
                null, "Postfix_Spawned", null, "CrafterLogic.NotifyCraftEnd(GameObject, TechType)");
            DeLog.Info("DE.Harmony SN " + HarmonyId + " " + Describe());
        }

        private static bool Patch(Type type, string name, Type[] args, Type returnType, bool isStatic, string prefix, string postfix, string finalizer, string label)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, args);
                if (m == null || m.DeclaringType != type)
                {
                    Missing += (Missing.Length > 0 ? "," : "") + label;
                    DeLog.Warn("DE.Harmony MISSING " + label + (m != null ? " (declared on " + m.DeclaringType + ")" : "") + " (feature off)");
                    return false;
                }
                if (m.IsStatic != isStatic || m.ReturnType != returnType)
                {
                    Missing += (Missing.Length > 0 ? "," : "") + label + " shape";
                    DeLog.Warn("DE.Harmony MISSING " + label + " shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (feature off)");
                    return false;
                }
                _harmony.Patch(m, Hm(prefix), Hm(postfix), null, Hm(finalizer), null); // HarmonyX 2.9: (original, prefix, postfix, transpiler, finalizer, ilmanipulator)
                return true;
            }
            catch (Exception e)
            {
                Missing += (Missing.Length > 0 ? "," : "") + label;
                DeLog.Warn("DE.Harmony MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        private static HarmonyMethod Hm(string name)
        {
            if (name == null) return null;
            MethodInfo mi = typeof(DamagePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (mi == null) throw new MissingMethodException(typeof(DamagePatches).Name, name);
            return new HarmonyMethod(mi);
        }

        // ------------------------------------------------------------------------------------------ context stack

        private static int Push(DamageContext c)
        {
            int before = _depth;
            if (_depth < 0 || _depth > MaxDepth) _depth = 0;
            if (_depth == MaxDepth) { ContextOverflow++; return before; }
            Stack[_depth++] = c;
            return before;
        }

        // ------------------------------------------------------------------------------------------ 1. TakeDamage

        private static void Prefix_TakeDamage(LiveMixin __instance, out bool __state)
        {
            __state = false;
            try
            {
                S6Host h = Host;
                if (h == null || __instance == null || !h.IsTarget(__instance)) return;
                __state = GameGate(__instance);
            }
            catch (Exception) { PatchErrors++; }
        }

        /// <summary>The game's own gate of TakeDamage, evaluated before the call.</summary>
        private static bool GameGate(LiveMixin lm)
        {
            if (!(lm.health > 0f) || lm.invincible) return false;
            if (GameModeUtils.IsInvisible())
            {
                bool inc = lm.data != null && lm.data.invincibleInCreative;
                if (inc || lm.GetComponent<BaseCell>() != null) return false;
            }
            return true;
        }

        private static void Postfix_TakeDamage(LiveMixin __instance, float originalDamage, Vector3 position, DamageType type, GameObject dealer, bool __state)
        {
            try
            {
                S6Host h = Host;
                if (h == null || __instance == null) return;
                if (S6Mutants.IsOn(S6Mutants.LateForwarder)) return; // S6.M12: the patch is "off", the forwarder speaks instead
                h.OnTakeDamage(__instance, originalDamage, position, (int)type, dealer, __state, Top);
            }
            catch (Exception e)
            {
                PatchErrors++;
                DeLog.WarnOnce("s6.postfix", "DE.S6 TakeDamage postfix failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>S6.M12: the late forwarder's path (its receivers are notified only inside the game's gate).</summary>
        public static void Forwarded(LiveMixin lm, float amount, Vector3 position, DamageType type, GameObject dealer)
        {
            S6Host h = Host;
            if (h == null || lm == null || !S6Mutants.IsOn(S6Mutants.LateForwarder)) return;
            h.OnTakeDamage(lm, amount, position, (int)type, dealer, true, Top);
        }

        // ------------------------------------------------------------------------------------------ 2. CanDealDamageTo

        private static void Postfix_CanDeal(MeleeAttack __instance, GameObject target, ref bool __result)
        {
            if (__result || target == null) return;
            try
            {
                S6Host h = Host;
                if (h == null || __instance == null || S6Mutants.IsOn(S6Mutants.NoCanDeal)) return;
                if (h.AcceptBite(__instance, target)) { __result = true; CanDealAccepted++; }
            }
            catch (Exception) { PatchErrors++; }
        }

        // ------------------------------------------------------------------------------------------ 3-5. contexts

        private static void Prefix_OnTouch(MeleeAttack __instance, Collider collider, out int __state)
        {
            var c = new DamageContext { Kind = DamageContext.Mouth };
            try
            {
                if (__instance != null && collider != null && Host != null)
                {
                    c.Col = collider;
                    c.Creature = __instance.gameObject;
                    GameObject mouth = __instance.mouth;
                    c.Point = mouth != null ? mouth.transform.position : __instance.transform.position;
                    c.Target = __instance.GetTarget(collider);
                }
            }
            catch (Exception) { PatchErrors++; }
            __state = Push(c);
        }

        private static void Prefix_Shocker(ShockerMeleeAttack __instance, Collider collider, out int __state)
        {
            var c = new DamageContext { Kind = DamageContext.Shocker };
            try
            {
                if (__instance != null && collider != null && Host != null)
                {
                    c.Col = collider;
                    c.Creature = __instance.gameObject;
                    c.Point = __instance.transform.position;
                    c.Target = __instance.GetTarget(collider);
                }
            }
            catch (Exception) { PatchErrors++; }
            __state = Push(c);
        }

        private static void Prefix_Radius(Vector3 position, float detonateRadius, GameObject sourceObject, out int __state)
        {
            var c = new DamageContext { Kind = DamageContext.Blast, Point = position, Radius = detonateRadius, Source = sourceObject };
            __state = Push(c);
        }

        /// <summary>Restores the depth the prefix saw (a skipped prefix leaves 0: the stack is simply emptied).</summary>
        private static void Finalizer_Pop(int __state)
        {
            _depth = __state >= 0 && __state <= MaxDepth ? __state : 0;
        }

        // ------------------------------------------------------------------------------------------ 6. spawn rows

        private static void Postfix_Spawned(GameObject target, TechType techType)
        {
            try
            {
                S6Host h = Host;
                if (h != null && target != null) h.OnSpawned(target, techType);
            }
            catch (Exception) { PatchErrors++; }
        }
    }
}
