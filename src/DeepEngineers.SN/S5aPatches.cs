using System;
using System.Reflection;
using HarmonyLib;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S5a Harmony patch of the Subnautica side (bridge mode only; installed once at bridge start, never removed):
 /// <c>Player.SuffocationDie</c>
 /// The prefix skips the original, so
 /// the ghost can choke (the suffocation state, overlay and sounds are the game's) but never dies: CONTRACT §4.2 "SE is
 /// authoritative for character health; Subnautica health never changes on the ghost Player" and §1 "nothing in
 /// Subnautica may hurt, kill, move or save the ghost". Death itself is SE's: an empty SE suit suffocates the
 /// engineer in SE (CONTRACT §2.10 "Oxygen rule"). A missing target logs <c>DE.Harmony MISSING</c> once and the S5a host
 /// falls back to the oxygen floor (<see cref="S5aRules.O2FloorUnpatched"/>: the mirrored oxygen never reaches 0, so
 /// the suffocation sequence never starts).
 /// </summary>
    internal static class S5aPatches
    {
        public const string HarmonyId = "deepengineers.sn.s5a";

        private static Harmony _harmony;
        private static bool _installed;

        public static bool SuffocationDiePatched { get; private set; }
        public static string Missing { get; private set; }
        /// <summary>Player.SuffocationDie calls skipped (each logged).</summary>
        public static long DeathsSkipped;

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
                DeLog.Error("DE.Harmony S5a FAILED creating the Harmony instance: " + e.GetType().Name + ": " + e.Message + " (no SuffocationDie patch: the oxygen floor applies)");
                return;
            }
            SuffocationDiePatched = Patch(typeof(Player), "SuffocationDie", Type.EmptyTypes, typeof(void), "Prefix_SuffocationDie", "Player.SuffocationDie()");
            DeLog.Info("DE.Harmony SN " + HarmonyId + " suffocationDie=" + (SuffocationDiePatched ? "installed (the ghost chokes but never dies; SE owns death)" : "MISSING (oxygen floor " +
                       S5aRules.O2FloorUnpatched.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " unit applies)"));
        }

        private static bool Patch(Type type, string name, Type[] args, Type returnType, string prefix, string label)
        {
            try
            {
                MethodInfo m = AccessTools.Method(type, name, args);
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
                _harmony.Patch(m, new HarmonyMethod(typeof(S5aPatches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                Missing = label + ": " + e.GetType().Name;
                DeLog.Warn("DE.Harmony MISSING " + label + ": " + e.GetType().Name + ": " + e.Message + " (feature off)");
                return false;
            }
        }

        /// <summary>The death path of suffocation is skipped (the only caller is the suffocation Sequence callback).</summary>
        private static bool Prefix_SuffocationDie(Player __instance)
        {
            try
            {
                DeathsSkipped++;
                bool alive = __instance != null && __instance.liveMixin != null && __instance.liveMixin.IsAlive();
                DeLog.Warn("DE.S5a suffocation death SKIPPED #" + DeathsSkipped + " (Player.SuffocationDie; the ghost " + (alive ? "stays alive" : "liveMixin not alive?!") +
                           "; CONTRACT §4.2: SE owns death, the SE suit's own suffocation applies there)");
            }
            catch (Exception)
            {
                // a patch body never throws into the game
            }
            return false;
        }
    }
}
