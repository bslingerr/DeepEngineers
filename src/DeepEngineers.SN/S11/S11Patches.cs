using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S11 Harmony patches of the Subnautica side (id <c>deepengineers.sn.s11</c>; installed once at the S11 host's start,
 /// never removed: every body checks <see cref="S11Hooks.Host"/> and returns when the host is gone). Targets
 /// <list type="number">
 /// <item>C: <c>public override void ReaperMeleeAttack.OnTouch(Collider)</c> prefix;
 /// <c>public void ReaperLeviathan.Update</c>, <c>public void ReaperLeviathan.OnTakeDamage(DamageInfo)</c>,
 /// <c>public override void ReaperLeviathan.OnDisable</c> postfixes; <c>public void SeaDragonMeleeAttack.OnTouchFront(Collider)</c>
 /// prefix; <c>public override void SeaDragon.OnDisable</c> postfix. The
 /// protected <c>MeleeAttack.timeLastBite</c> through a field reference.</item>
 /// <item>B: <c>public override float AvoidObstacles.Evaluate(Creature, float)</c>
 /// prefix/postfix (a proxy-only cast beside the scan; private <c>timeNextScan</c>, <c>swimDirectionFound</c>),
 /// <c>private void AvoidObstacles.FindSwimDirection</c> postfix (<c>avoidancePosition</c>, <c>timeStartAvoidance</c>,
 /// CreatureAction's protected <c>creature</c>), <c>protected virtual bool AvoidObstacles.IsObstacle(Collider)</c> postfix
 /// (mask −5 creatures: the attacked ship is no obstacle); <c>private static bool AvoidTerrain.OctreeRaycast(Vector3, Vector3)</c>
 /// postfix, with <c>public override float AvoidTerrain.Evaluate(Creature, float)</c> and
 /// <c>public override void AvoidTerrain.StartPerform(Creature, float)</c> prefix/finalizer pairs for the creature context.</item>
 /// <item>D: <c>private void TemperatureDamage.UpdateDamage</c> and <c>private void TemperatureDamage.ApplyLavaDamage</c>
 /// prefixes (the ghost Player's own heat is replaced by S11's sampler at the SE character); <c>private LiveMixin
 /// AcidicBrineDamageTrigger.GetLiveMixin(GameObject)</c> postfix; <c>private void Geyser.OnTriggerStay(Collider)</c>
 /// postfix (<c>private bool erupting</c>); <c>private void GasPod.Update</c> prefix/postfix
 /// (<c>detonated</c>, <c>timeLastDamageTick</c>, <c>tracker</c>); <c>private void DamagePlayerInRadius.DoDamage</c>
 /// prefix/finalizer (the radiation level the S1 UI hider zeroes each frame, restored for the call);
 /// <c>public void EMPBlast.OnTouch(Collider)</c> prefix.</item>
 /// <item>E: <c>private void Bubble.OnCollisionEnter(Collision)</c> prefix (<c>private bool hasPopped</c>);
 /// <c>private void OxygenArea.OnTriggerStay(Collider)</c> prefix; <c>public float LiveMixin.AddHealth(float)</c>
 /// prefix.</item>
 /// </list>
 /// Patch bodies never throw into the game.
 /// </summary>
    internal static class S11Patches
    {
        public const string HarmonyId = "deepengineers.sn.s11";
        private static Harmony _harmony;
        private static bool _installed;
        public static string Missing = "";
        public static long Errors;
        private static readonly Dictionary<string, bool> Installed = new Dictionary<string, bool>(StringComparer.Ordinal);

        private static AccessTools.FieldRef<MeleeAttack, float> _timeLastBite;
        private static AccessTools.FieldRef<Bubble, bool> _hasPopped;
        private static AccessTools.FieldRef<Geyser, bool> _erupting;
        private static AccessTools.FieldRef<GasPod, bool> _detonated;
        private static AccessTools.FieldRef<GasPod, float> _gasLastTick;
        private static AccessTools.FieldRef<GasPod, UWE.TriggerStayTracker> _gasTracker;
        private static AccessTools.FieldRef<AvoidObstacles, float> _timeNextScan, _avoidStart;
        private static AccessTools.FieldRef<AvoidObstacles, bool> _swimFound;
        private static AccessTools.FieldRef<AvoidObstacles, Vector3> _avoidPos;
        private static AccessTools.FieldRef<CreatureAction, Creature> _actionCreature;

        [ThreadStatic] private static GameObject _avoidTarget;
        [ThreadStatic] private static bool _avoidCtx;

        public static bool Has(string label) { bool b; return Installed.TryGetValue(label, out b) && b; }

        public static string Describe()
        {
            if (!_installed) return "not installed";
            var on = new List<string>();
            var off = new List<string>();
            foreach (KeyValuePair<string, bool> kv in Installed) (kv.Value ? on : off).Add(kv.Key);
            return on.Count + " on" + (off.Count > 0 ? ", MISSING " + string.Join(",", off.ToArray()) : "");
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try { _harmony = new Harmony(HarmonyId); }
            catch (Exception e)
            {
                Missing = "Harmony instance";
                DeLog.Error("DE.S11 MISSING Harmony instance: " + e.GetType().Name + ": " + e.Message + " (no grabs, avoidance, hazards, air or EMP)");
                return;
            }
            _timeLastBite = Ref<MeleeAttack, float>("timeLastBite");
            _hasPopped = Ref<Bubble, bool>("hasPopped");
            _erupting = Ref<Geyser, bool>("erupting");
            _detonated = Ref<GasPod, bool>("detonated");
            _gasLastTick = Ref<GasPod, float>("timeLastDamageTick");
            _gasTracker = Ref<GasPod, UWE.TriggerStayTracker>("tracker");
            _timeNextScan = Ref<AvoidObstacles, float>("timeNextScan");
            _swimFound = Ref<AvoidObstacles, bool>("swimDirectionFound");
            _avoidPos = Ref<AvoidObstacles, Vector3>("avoidancePosition");
            _avoidStart = Ref<AvoidObstacles, float>("timeStartAvoidance");
            _actionCreature = Ref<CreatureAction, Creature>("creature");

            // C
            Patch("reaperTouch", typeof(ReaperMeleeAttack), "OnTouch", new[] { typeof(Collider) }, typeof(void), false, "Prefix_ReaperTouch", null, null, _timeLastBite != null);
            Patch("reaperUpdate", typeof(ReaperLeviathan), "Update", Type.EmptyTypes, typeof(void), false, null, "Postfix_ReaperUpdate", null);
            Patch("reaperDamaged", typeof(ReaperLeviathan), "OnTakeDamage", new[] { typeof(DamageInfo) }, typeof(void), false, null, "Postfix_ReaperDamaged", null);
            Patch("reaperDisable", typeof(ReaperLeviathan), "OnDisable", Type.EmptyTypes, typeof(void), false, null, "Postfix_CreatureDisable", null);
            Patch("dragonTouch", typeof(SeaDragonMeleeAttack), "OnTouchFront", new[] { typeof(Collider) }, typeof(void), false, "Prefix_DragonTouch", null, null);
            Patch("dragonDisable", typeof(SeaDragon), "OnDisable", Type.EmptyTypes, typeof(void), false, null, "Postfix_CreatureDisable", null);
            // B
            Patch("avoidScan", typeof(AvoidObstacles), "Evaluate", new[] { typeof(Creature), typeof(float) }, typeof(float), false, "Prefix_AvoidScan", "Postfix_AvoidScan", null,
                  _timeNextScan != null && _swimFound != null);
            Patch("avoidDirection", typeof(AvoidObstacles), "FindSwimDirection", Type.EmptyTypes, typeof(void), false, null, "Postfix_AvoidDirection", null,
                  _swimFound != null && _avoidPos != null && _avoidStart != null && _actionCreature != null);
            Patch("avoidObstacle", typeof(AvoidObstacles), "IsObstacle", new[] { typeof(Collider) }, typeof(bool), false, null, "Postfix_IsObstacle", null);
            Patch("avoidOctree", typeof(AvoidTerrain), "OctreeRaycast", new[] { typeof(Vector3), typeof(Vector3) }, typeof(bool), true, null, "Postfix_Octree", null);
            Patch("avoidEvaluate", typeof(AvoidTerrain), "Evaluate", new[] { typeof(Creature), typeof(float) }, typeof(float), false, "Prefix_AvoidCtx", null, "Finalizer_AvoidCtx");
            Patch("avoidStart", typeof(AvoidTerrain), "StartPerform", new[] { typeof(Creature), typeof(float) }, typeof(void), false, "Prefix_AvoidCtx", null, "Finalizer_AvoidCtx");
            // D
            Patch("tempUpdate", typeof(TemperatureDamage), "UpdateDamage", Type.EmptyTypes, typeof(void), false, "Prefix_GhostTemperature", null, null);
            Patch("tempLava", typeof(TemperatureDamage), "ApplyLavaDamage", Type.EmptyTypes, typeof(void), false, "Prefix_GhostTemperature", null, null);
            Patch("acid", typeof(AcidicBrineDamageTrigger), "GetLiveMixin", new[] { typeof(GameObject) }, typeof(LiveMixin), false, null, "Postfix_AcidTarget", null);
            Patch("geyser", typeof(Geyser), "OnTriggerStay", new[] { typeof(Collider) }, typeof(void), false, null, "Postfix_Geyser", null, _erupting != null);
            Patch("gasPod", typeof(GasPod), "Update", Type.EmptyTypes, typeof(void), false, "Prefix_GasPod", "Postfix_GasPod", null, _detonated != null && _gasLastTick != null && _gasTracker != null);
            Patch("radiation", typeof(DamagePlayerInRadius), "DoDamage", Type.EmptyTypes, typeof(void), false, "Prefix_Radiation", null, "Finalizer_Radiation");
            Patch("emp", typeof(EMPBlast), "OnTouch", new[] { typeof(Collider) }, typeof(void), false, "Prefix_Emp", null, null);
            // E
            Patch("bubble", typeof(Bubble), "OnCollisionEnter", new[] { typeof(Collision) }, typeof(void), false, "Prefix_Bubble", null, null, _hasPopped != null);
            Patch("oxygenArea", typeof(OxygenArea), "OnTriggerStay", new[] { typeof(Collider) }, typeof(void), false, "Prefix_OxygenArea", null, null);
            Patch("heal", typeof(LiveMixin), "AddHealth", new[] { typeof(float) }, typeof(float), false, "Prefix_Heal", null, null);
            // (format surface.release),  public void SplineFollowing.GoTo(Vector3 targetPos, Vector3 targetDir, float
 // velocity), the last door of SwimTo, Attack and Overshoot
            Patch("followClamp", typeof(SplineFollowing), "GoTo", new[] { typeof(Vector3), typeof(Vector3), typeof(float) }, typeof(void), false,
                  "Prefix_FollowClamp", null, null);
            // in-game 20261024-144924 (C7: a following reaper 13-20 m up): the backstop at every physics step, after the creature's
 // own push: public void Locomotion.ManagedFixedUpdate
            Patch("riseGuard", typeof(Locomotion), "ManagedFixedUpdate", Type.EmptyTypes, typeof(void), false, null, "Postfix_RiseGuard", null);
            // pass 6: no bite on a grid remnant; after S6's own postfix (which admits proxies), so Priority.Last
            Installed["debrisBite"] = false;
            try
            {
                MethodInfo cd = AccessTools.Method(typeof(MeleeAttack), "CanDealDamageTo", new[] { typeof(GameObject) });
                if (cd != null && cd.DeclaringType == typeof(MeleeAttack) && !cd.IsStatic && cd.ReturnType == typeof(bool))
                {
                    HarmonyMethod hm = Hm("Postfix_DebrisBite");
                    hm.priority = Priority.Last;
                    _harmony.Patch(cd, null, hm);
                    Installed["debrisBite"] = true;
                }
                else
                {
                    Missing += (Missing.Length > 0 ? "," : "") + "debrisBite";
                    DeLog.Warn("DE.S11 MISSING MeleeAttack.CanDealDamageTo(GameObject) (debrisBite off: remnants can still be bitten)");
                }
            }
            catch (Exception e)
            {
                Missing += (Missing.Length > 0 ? "," : "") + "debrisBite";
                DeLog.Warn("DE.S11 MISSING MeleeAttack.CanDealDamageTo: " + e.GetType().Name + ": " + e.Message + " (debrisBite off)");
            }
            DeLog.Info("DE.Harmony SN " + HarmonyId + " " + Describe());
        }

        public static long DebrisBitesRefused;

        private static void Postfix_RiseGuard(Locomotion __instance)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return;
            try { h.FixedGuard(__instance); }
            catch (Exception e) { Err(e, "riseGuard"); }
        }

        private static void Prefix_FollowClamp(SplineFollowing __instance, ref Vector3 targetPos)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return;
            try { h.FollowClamp(__instance, ref targetPos); }
            catch (Exception e) { Err(e, "followClamp"); }
        }

        private static void Postfix_DebrisBite(GameObject target, ref bool __result)
        {
            if (!__result || target == null) return;
            try
            {
                S11Host h = S11Hooks.Host;
                if (h == null) return;
                ProxyMarker pm = S11Hooks.MarkerOf(target);
                if (pm != null && h.IsDebrisMarker(pm)) { __result = false; DebrisBitesRefused++; }
            }
            catch (Exception e) { Err(e, "debrisBite"); }
        }

        private static AccessTools.FieldRef<T, F> Ref<T, F>(string field)
        {
            try
            {
                FieldInfo fi = AccessTools.Field(typeof(T), field);
                if (fi == null || fi.FieldType != typeof(F)) { DeLog.Warn("DE.S11 MISSING " + typeof(T).Name + "." + field + " (" + typeof(F).Name + ") (its feature off)"); return null; }
                return AccessTools.FieldRefAccess<T, F>(fi);
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.S11 MISSING " + typeof(T).Name + "." + field + ": " + e.GetType().Name + " (its feature off)");
                return null;
            }
        }

        private static void Patch(string label, Type type, string name, Type[] args, Type returnType, bool isStatic, string prefix, string postfix, string finalizer, bool fieldsOk = true)
        {
            Installed[label] = false;
            try
            {
                if (!fieldsOk) { Missing += (Missing.Length > 0 ? "," : "") + label; return; }
                MethodInfo m = AccessTools.Method(type, name, args);
                if (m == null || m.DeclaringType != type || m.IsStatic != isStatic || m.ReturnType != returnType)
                {
                    Missing += (Missing.Length > 0 ? "," : "") + label;
                    DeLog.Warn("DE.S11 MISSING " + type.Name + "." + name + "(" + ArgText(args) + ")" + (m != null ? " shape: declared on " + m.DeclaringType + ", static=" + m.IsStatic + ", returns " + m.ReturnType.Name : "") + " (" + label + " off)");
                    return;
                }
                _harmony.Patch(m, Hm(prefix), Hm(postfix), null, Hm(finalizer), null);
                Installed[label] = true;
            }
            catch (Exception e)
            {
                Missing += (Missing.Length > 0 ? "," : "") + label;
                DeLog.Warn("DE.S11 MISSING " + type.Name + "." + name + ": " + e.GetType().Name + ": " + e.Message + " (" + label + " off)");
            }
        }

        private static string ArgText(Type[] args)
        {
            var s = new List<string>();
            foreach (Type t in args) s.Add(t.Name);
            return string.Join(", ", s.ToArray());
        }

        private static HarmonyMethod Hm(string name)
        {
            if (name == null) return null;
            return new HarmonyMethod(typeof(S11Patches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void Err(Exception e, string where)
        {
            Errors++;
            S11Host h = S11Hooks.Host;
            if (h != null) h.Fault("patch." + where, e);
        }

        // ============================================================================================ C

        private static bool Prefix_ReaperTouch(ReaperMeleeAttack __instance, Collider collider)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return true;
            try { return h.Grabs.OnReaperTouch(__instance, collider, _timeLastBite(__instance)); }
            catch (Exception e) { Err(e, "reaperTouch"); return true; }
        }

        private static void Postfix_ReaperUpdate(ReaperLeviathan __instance)
        {
            S11Host h = S11Hooks.Host;
            if (h == null) return;
            try { h.Grabs.OnReaperUpdate(__instance); }
            catch (Exception e) { Err(e, "reaperUpdate"); }
        }

        private static void Postfix_ReaperDamaged(ReaperLeviathan __instance, DamageInfo damageInfo)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || damageInfo == null) return;
            try { h.Grabs.OnCreatureDamaged(__instance, (int)damageInfo.type); }
            catch (Exception e) { Err(e, "reaperDamaged"); }
        }

        private static void Postfix_CreatureDisable(Creature __instance)
        {
            S11Host h = S11Hooks.Host;
            if (h == null) return;
            try { h.Grabs.OnCreatureDisabled(__instance); }
            catch (Exception e) { Err(e, "creatureDisable"); }
        }

        private static void Prefix_DragonTouch(SeaDragonMeleeAttack __instance, Collider collider)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return;
            try { h.Grabs.OnDragonTouch(__instance, collider); }
            catch (Exception e) { Err(e, "dragonTouch"); }
        }

        // ============================================================================================ B

 /// <summary>
 /// Review 3: the game's own scan is left as it is (its mask, its SphereCast or Raycast); beside it, at the same moment
 /// (the scan ran when timeNextScan moved), a proxy-only cast with the action's own radius and distance. A section of a
 /// ship the creature is not attacking starts the action's avoidance exactly as a terrain hit does
 /// </summary>
        private static void Prefix_AvoidScan(AvoidObstacles __instance, out float __state)
        {
            __state = float.NaN;
            try { if (S11Hooks.Host != null && __instance != null) __state = _timeNextScan(__instance); }
            catch (Exception) { }
        }

        private static void Postfix_AvoidScan(AvoidObstacles __instance, Creature creature, float __state, ref float __result)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || !h.AvoidOn || __instance == null || creature == null || float.IsNaN(__state) || __result != 0f) return;
            try
            {
                if (_timeNextScan(__instance) == __state) return; // no scan this call
                h.AvoidScans++; // run 1: the evidence that the hook sees the game's scans (B1)
                GameObject target = __instance.lastTarget != null ? __instance.lastTarget.target : null;
                if (!h.ProxyAhead(creature.transform.position, creature.transform.forward, __instance.scanRadius, __instance.scanDistance, target)) return;
                _swimFound(__instance) = false;
                __result = __instance.GetEvaluatePriority();
            }
            catch (Exception e) { Err(e, "avoidScan"); }
        }

        /// <summary>
 /// Review 3: FindSwimDirection picks a random direction clear of its own mask; when that
 /// direction runs into a proxy (not the target), the same search runs again with both tests (the game's mask and the
 /// proxies), as many tries as the action's avoidanceIterations; no clear direction ends the avoidance as the game does.
 /// </summary>
        private static void Postfix_AvoidDirection(AvoidObstacles __instance)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || !h.AvoidOn || __instance == null || !_swimFound(__instance)) return;
            try
            {
                Creature c = _actionCreature(__instance);
                if (c == null) return;
                GameObject target = __instance.lastTarget != null ? __instance.lastTarget.target : null;
                Vector3 p = c.transform.position;
                Vector3 to = _avoidPos(__instance) - p;
                float dist = __instance.avoidanceDistance;
                if (!h.ProxyAhead(p, to, 0f, dist, target)) return;
                int mask = __instance.avoidTerrainOnly ? Voxeland.GetTerrainLayerMask() : -5;
                for (int i = 0; (float)i < __instance.avoidanceIterations; i++)
                {
                    Vector3 d = UnityEngine.Random.onUnitSphere;
                    RaycastHit hit;
                    bool blocked = Physics.Raycast(p, d, out hit, dist, mask, QueryTriggerInteraction.Ignore) && !IsTarget(hit.collider, target, __instance.avoidTerrainOnly);
                    if (blocked || h.ProxyAhead(p, d, 0f, dist, target)) continue;
                    _avoidPos(__instance) = p + d * dist;
                    return;
                }
                _swimFound(__instance) = false;
                _avoidStart(__instance) = 0f;
            }
            catch (Exception e) { Err(e, "avoidDirection"); }
        }

        /// <summary>
 /// Pass 4: one scan of the creature's own <c>AvoidObstacles.Evaluate(Creature, float)</c>
 /// forced now: <c>timeNextScan</c> and <c>timeStartAvoidance</c> zeroed, then the
 /// game's own call, so the patches above run exactly as they do for the AI. <paramref name="hookHits"/> = the proxy
 /// obstacles the S11 hook counted during the call (0 under S11.M3). False without the field refs (B1 MISSING).
 /// </summary>
        internal static bool ProbeAvoid(AvoidObstacles a, Creature c, out float result, out long hookHits)
        {
            result = 0f;
            hookHits = 0;
            S11Host h = S11Hooks.Host;
            if (a == null || c == null || h == null || _timeNextScan == null || _avoidStart == null) return false;
            long before = h.AvoidHits;
            _timeNextScan(a) = 0f;
            _avoidStart(a) = 0f;
            result = a.Evaluate(c, Time.time);
            hookHits = h.AvoidHits - before;
            return true;
        }

        /// <summary>Pass 4: the action's avoidance window is running.</summary>
        internal static bool Avoiding(AvoidObstacles a)
        {
            if (a == null || _avoidStart == null) return false;
            float t0 = _avoidStart(a);
            return t0 > 0f && Time.time < t0 + a.avoidanceDuration;
        }

        /// <summary>AvoidObstacles.IsObstacle's exception: with avoidTerrainOnly false the creature's own target is no obstacle.</summary>
        private static bool IsTarget(Collider c, GameObject target, bool terrainOnly)
        {
            if (terrainOnly || target == null || c == null) return false;
            Rigidbody rb = c.attachedRigidbody;
            return (rb != null ? rb.gameObject : c.gameObject) == target;
        }

        private static void Postfix_IsObstacle(AvoidObstacles __instance, Collider collider, ref bool __result)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || !h.AvoidOn || collider == null) return;
            try
            {
                //  the mask is the game's; only a creature whose mask already sees proxies (avoidTerrainOnly false, -5)
 // gets the one exception: a section of the ship it attacks is no obstacle
                if (__instance.avoidTerrainOnly || !__result || S11Hooks.MarkerOf(collider) == null) return;
                GameObject target = __instance.lastTarget != null ? __instance.lastTarget.target : null;
                __result = h.IsProxyObstacle(collider, target);
            }
            catch (Exception e) { Err(e, "isObstacle"); }
        }

        private static void Prefix_AvoidCtx(Creature creature)
        {
            _avoidCtx = true;
            try
            {
                LastTarget lt = creature != null ? creature.GetComponent<LastTarget>() : null;
                _avoidTarget = lt != null ? lt.target : null;
            }
            catch (Exception) { _avoidTarget = null; }
        }

        private static Exception Finalizer_AvoidCtx(Exception __exception)
        {
            _avoidCtx = false;
            _avoidTarget = null;
            return __exception;
        }

        private static void Postfix_Octree(Vector3 startPoint, Vector3 endPoint, ref bool __result)
        {
            if (__result || !_avoidCtx) return;
            S11Host h = S11Hooks.Host;
            if (h == null || !h.AvoidOn) return;
            try { __result = h.ProxyOnSegment(startPoint, endPoint, _avoidTarget); }
            catch (Exception e) { Err(e, "octree"); }
        }

        // ============================================================================================ D

 /// <summary>The ghost Player's own TemperatureDamage is replaced by S11's sampler at the SE character (exactly once).</summary>
        private static bool Prefix_GhostTemperature(TemperatureDamage __instance)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || !h.HasS6 || __instance == null) return true;
            try { return !(Player.main != null && ReferenceEquals(__instance.gameObject, Player.main.gameObject)); }
            catch (Exception e) { Err(e, "temperature"); return true; }
        }

        private static void Postfix_AcidTarget(GameObject go, ref LiveMixin __result)
        {
            if (__result != null) return;
            S11Host h = S11Hooks.Host;
            if (h == null) return;
            try { __result = h.AcidTarget(go); }
            catch (Exception e) { Err(e, "acid"); }
        }

        private static void Postfix_Geyser(Geyser __instance, Collider other)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null || other == null) return;
            try
            {
                if (!_erupting(__instance)) return;
                h.OnGeyser(__instance, other, __instance.damage);
            }
            catch (Exception e) { Err(e, "geyser"); }
        }

        private static void Prefix_GasPod(GasPod __instance, out float __state)
        {
            __state = float.NaN;
            try { if (S11Hooks.Host != null && __instance != null) __state = _gasLastTick(__instance); }
            catch (Exception) { }
        }

        private static void Postfix_GasPod(GasPod __instance, float __state)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null || float.IsNaN(__state)) return;
            try
            {
                if (!_detonated(__instance)) return;
                if (_gasLastTick(__instance) == __state) return; // no damage tick this frame
                UWE.TriggerStayTracker t = _gasTracker(__instance);
                if (t == null) return;
                h.OnGasPodTick(__instance, t.Get(), __instance.damagePerSecond * __instance.damageInterval);
            }
            catch (Exception e) { Err(e, "gasPod"); }
        }

        private static void Prefix_Radiation(DamagePlayerInRadius __instance, out bool __state)
        {
            __state = false;
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null || __instance.damageType != DamageType.Radiation) return;
            if (!DeepEngineers.Wire.S11HazardRules.Forwarded((int)DamageType.Radiation)) return; // the mechanism stays dormant
            try
            {
                Player p = Player.main;
                if (p == null || p.radiationAmount != 0f) return;
                float level = h.RadiationLevel();
                if (!(level > 0f)) return;
                p.SetRadiationAmount(level); // what RadiatePlayerInRange set before the S1 UI hider zeroed it
                __state = true;
            }
            catch (Exception e) { Err(e, "radiation"); }
        }

        private static Exception Finalizer_Radiation(bool __state, Exception __exception)
        {
            if (__state)
            {
                try { if (Player.main != null) Player.main.SetRadiationAmount(0f); } catch (Exception) { }
            }
            return __exception;
        }

        private static void Prefix_Emp(EMPBlast __instance, Collider collider)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return;
            try { h.OnEmp(__instance, collider); }
            catch (Exception e) { Err(e, "emp"); }
        }

        // ============================================================================================ E

        private static bool Prefix_Bubble(Bubble __instance, Collision collisionInfo)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return true;
            try
            {
                if (_hasPopped(__instance)) return true;
                if (Time.time < __instance.dontPopTime) return true; // the game's own rule for non-Player layers: no pop yet
                if (!h.OnBubble(__instance, collisionInfo)) return true;
                __instance.Pop(true);
                return false;
            }
            catch (Exception e) { Err(e, "bubble"); return true; }
        }

        private static void Prefix_OxygenArea(OxygenArea __instance, Collider other)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null) return;
            try { h.OnOxygenArea(__instance, other); }
            catch (Exception e) { Err(e, "oxygenArea"); }
        }

        private static void Prefix_Heal(LiveMixin __instance, float healthBack)
        {
            S11Host h = S11Hooks.Host;
            if (h == null || __instance == null || !(healthBack > 0f)) return;
            try
            {
                Player p = Player.main;
                if (p == null || !ReferenceEquals(__instance, p.liveMixin)) return;
                h.OnHeal(healthBack);
            }
            catch (Exception e) { Err(e, "heal"); }
        }
    }
}
