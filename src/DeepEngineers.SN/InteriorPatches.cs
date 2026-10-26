using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S7 interior Harmony patches of the Subnautica side (bridge mode only; installed once at the interior host's start,
 /// never removed; CONTRACT §5.4 "Inside-walkable", S7.N2):
 /// <list type="bullet">
 /// <item>Postfix on <c>Player.IsInsideWalkable</c>
 /// the result becomes true for
 /// <c>Player.main</c> (the ghost) while <see cref="S7Interior.InsideNow"/>. While a <c>de.interior {action: trace}</c>
 /// window runs, it also counts its callers by type (the first frame past the patched method; through
 /// <c>Player.IsInside</c> counted apart: the 13 direct and 5 transitive readers of S7.N2).</item>
 /// <item>Prefix on <c>UnstuckPlayer.TrySavePosition</c>
 /// skipped while the override holds, so the inside branch never stores
 /// <c>basePosition</c> (<c>Vector3.zero</c>) as the ghost's last safe point (an "unstuck" would warp it to the world
 /// origin). The other harmful readers need nothing for the ghost: <c>TemperatureDamage</c> stops damaging inside (and
 /// the ghost is shielded), oxygen does not read it (<c>Player.CanBreathe</c> reads <c>currentSub</c>; S5a drives the
 /// mirrored O₂), <c>AttachAndSuck</c> lets go.</item>
 /// </list>
 /// A missing target logs <c>DE.Harmony MISSING</c> once and turns that feature off (the interior then stays wet and
 /// fogged: S7.N1.fog would read as S7.M1).
 /// </summary>
    internal static class InteriorPatches
    {
        public const string HarmonyId = "deepengineers.sn.s7.interior";

        private static Harmony _harmony;
        private static bool _installed;
        private static readonly Type PlayerType = typeof(Player);

        public static bool InsideWalkablePatched { get; private set; }
        public static bool UnstuckPatched { get; private set; }
        public static string Missing { get; private set; }
        /// <summary>Calls answered true by the postfix (the game alone said false).</summary>
        public static long Overrides;
        public static long UnstuckSkipped;
        public static long TraceErrors;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new Harmony(HarmonyId);
            }
            catch (Exception e)
            {
                Missing = "Harmony instance: " + e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.Harmony S7 interior FAILED creating the Harmony instance: " + Missing + " (interiors stay wet)");
                return;
            }
            InsideWalkablePatched = Patch(typeof(Player), "IsInsideWalkable", typeof(bool), null, "Postfix_IsInsideWalkable", "Player.IsInsideWalkable()");
            UnstuckPatched = Patch(typeof(UnstuckPlayer), "TrySavePosition", typeof(void), "Prefix_TrySavePosition", null, "UnstuckPlayer.TrySavePosition()");
            DeLog.Info("DE.Harmony SN " + HarmonyId + " isInsideWalkable=" + (InsideWalkablePatched ? "postfix" : "MISSING") + " unstuck=" +
                       (UnstuckPatched ? "prefix (no base position stored for the ghost while inside)" : "MISSING"));
        }

        private static bool Patch(Type type, string name, Type returnType, string prefix, string postfix, string label)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, Type.EmptyTypes);
                if (m == null)
                {
                    Missing = label;
                    DeLog.Warn("DE.Harmony MISSING " + label + " (feature off)");
                    return false;
                }
                if (m.IsStatic || m.ReturnType != returnType)
                {
                    Missing = label + " shape";
                    DeLog.Warn("DE.Harmony MISSING " + label + " shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (feature off)");
                    return false;
                }
                HarmonyMethod pre = prefix != null ? new HarmonyMethod(typeof(InteriorPatches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
                HarmonyMethod post = postfix != null ? new HarmonyMethod(typeof(InteriorPatches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)) : null;
                _harmony.Patch(m, pre, post);
                return true;
            }
            catch (Exception e)
            {
                Missing = label + ": " + e.GetType().Name;
                DeLog.Warn("DE.Harmony MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        private static void Postfix_IsInsideWalkable(Player __instance, ref bool __result)
        {
            try
            {
                if (!__result && S7Interior.InsideNow && ReferenceEquals(__instance, Player.main))
                {
                    __result = true;
                    Overrides++;
                }
                if (S7Interior.Trace.Active(S7Interior.NowMs)) TraceCaller(__result);
            }
            catch (Exception)
            {
                TraceErrors++;
            }
        }

        /// <summary>
 /// The caller type of this IsInsideWalkable call (frames past the postfix and the patched method). Review F24
 ///: one <c>StackFrame</c> at a time from depth 2 (the patched method's frame; usually the caller is the
 /// next one, or the one after <c>Player.IsInside</c>) instead of a full <c>StackTrace</c> per call: FMODGameParams
 /// alone asks once per instance per frame while a trace window runs.
 /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TraceCaller(bool answer)
        {
            bool via = false;
            string caller = null;
            for (int i = 1; i < 13; i++)
            {
                MethodBase m = new StackFrame(i, false).GetMethod();
                if (m == null) continue;
                string n = m.Name ?? "";
                Type t = m.DeclaringType;
                // S7-1 fix: a nested type is named by its outermost declaring type. The S7-1 trace filed
 // LargeWorldStreamer's every-frame call under "Settings"
 // and so S7.N2.readers read it as missing; coroutine iterator classes
 // (<Name>d__N) and lambda closures are nested the same way
                while (t != null && t.DeclaringType != null) t = t.DeclaringType;
                if (n.IndexOf("IsInsideWalkable", StringComparison.Ordinal) >= 0) continue; // the patched replacement
                if (t == typeof(InteriorPatches)) continue;
                if (t == PlayerType && n == "IsInside") { via = true; continue; }
                if (t == null) continue;
                caller = t.Name;
                break;
            }
            S7Interior.Trace.Note(caller, via, S7Interior.InsideNow);
        }

        private static bool Prefix_TrySavePosition()
        {
            if (!S7Interior.InsideNow) return true;
            UnstuckSkipped++;
            return false;
        }
    }
}
