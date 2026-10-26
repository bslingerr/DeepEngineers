using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.World;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony bootstrap (ROADMAP §5.4): id `deepengineers.se`, Lib.Harmony 2.4.2 from Pulsar (referenced, never
 /// shipped). Every target was read and is looked up again here with
 /// explicit parameter types; its shape (static or instance, parameters) is verified before patching and logged.
 /// A missing or changed target is logged as `DE.Harmony MISSING`, the start-up self-check fails (display bridge
 /// refused) and that patch stays off; nothing throws into the game.
 /// <list type="bullet">
 /// <item>`Sandbox.Game.Gui.MyGuiScreenLoading.RunLoad`: private instance, no parameters, called synchronously from
 /// `MyGuiScreenLoading.Update` on the update thread; wraps `m_screenToLoad.RunLoadingAction` (the whole world load).
 /// Prefix → LoadingWorld (+ worldEpoch for a gameplay screen); finalizer → load ended.</item>
 /// <item>`Sandbox.Game.World.MySession.Save(out MySessionSnapshot, string, Action&lt;SaveProgress&gt)`: public instance;
 /// the other overload `Save(string, Action&lt;SaveProgress&gt)` calls it. Prefix → Saving; finalizer → back.</item>
 /// <item>`Sandbox.MySandboxGame.ExitThreadSafe`: public static, no parameters. Pulsar v2.4.3 prefixes it (default
 /// priority, `Patch_ExitThreadSafe`) with `Process.GetCurrentProcess.Kill` and skips the original, so our
 /// prefix runs at `Priority.First` and writes `Exiting` before the kill.</item>
 /// </list>
 /// </summary>
    internal static class HarmonyPatches
    {
        public const string HarmonyId = "deepengineers.se";

        public static readonly List<string> Missing = new List<string>();
        public static readonly List<string> Applied = new List<string>();
        public static bool Attempted;
        public static bool AllApplied { get { return Attempted && Missing.Count == 0; } }

        private static FieldInfo _screenToLoadField;

        /// <summary>Called by the plugin's Init inside a try/catch (a missing 0Harmony fails here, not in Init).</summary>
        public static void Apply()
        {
            Attempted = true;
            var harmony = new Harmony(HarmonyId);
            DeLog.Info("DE.Harmony id=" + HarmonyId + " assembly=" + typeof(Harmony).Assembly.GetName().FullName);

            // 1. MyGuiScreenLoading.RunLoad — private instance, no parameters
            MethodInfo runLoad = AccessTools.Method(typeof(MyGuiScreenLoading), "RunLoad", Type.EmptyTypes);
            _screenToLoadField = AccessTools.Field(typeof(MyGuiScreenLoading), "m_screenToLoad");
            if (Check("MyGuiScreenLoading.RunLoad()", runLoad, false, typeof(void)))
                Patch(harmony, "MyGuiScreenLoading.RunLoad()", runLoad,
                    prefix: Hm(nameof(RunLoadPrefix)), finalizer: Hm(nameof(RunLoadFinalizer)));
            if (_screenToLoadField == null)
                DeLog.Info("DE.Harmony note: MyGuiScreenLoading.m_screenToLoad not found; every RunLoad counts as a world load");

            // 2. MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>) — public instance
            MethodInfo save = AccessTools.Method(typeof(MySession), "Save",
                new[] { typeof(MySessionSnapshot).MakeByRefType(), typeof(string), typeof(Action<SaveProgress>) });
            if (Check("MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)", save, false, typeof(bool)))
                Patch(harmony, "MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)", save,
                    prefix: Hm(nameof(SavePrefix)), finalizer: Hm(nameof(SaveFinalizer)));

            // 3. MySandboxGame.ExitThreadSafe — public static, no parameters; Priority.First (before Pulsar's kill)
            MethodInfo exit = AccessTools.Method(typeof(MySandboxGame), "ExitThreadSafe", Type.EmptyTypes);
            if (Check("MySandboxGame.ExitThreadSafe()", exit, true, typeof(void)))
            {
                HarmonyMethod pre = Hm(nameof(ExitPrefix));
                pre.priority = Priority.First;
                Patch(harmony, "MySandboxGame.ExitThreadSafe()", exit, prefix: pre, finalizer: null);
                LogPrefixOrder(exit);
            }

            DeLog.Info(Missing.Count == 0
                ? "DE.Harmony OK " + Applied.Count + " targets patched: " + string.Join(", ", Applied.ToArray())
                : "DE.Harmony FAIL missing: " + string.Join("; ", Missing.ToArray()) + " (start-up self-check failed: display bridge refused)");
        }

        private static HarmonyMethod Hm(string name)
        {
            MethodInfo m = typeof(HarmonyPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return new HarmonyMethod(m);
        }

        private static bool Check(string name, MethodInfo m, bool expectStatic, Type expectReturn)
        {
            if (m == null)
            {
                Missing.Add(name + " not found");
                DeLog.Info("DE.Harmony MISSING " + name + " (not found in this game build; patch off)");
                return false;
            }
            if (m.IsStatic != expectStatic || m.ReturnType != expectReturn)
            {
                Missing.Add(name + " changed (static=" + m.IsStatic + " returns " + m.ReturnType.Name + ")");
                DeLog.Info("DE.Harmony MISSING " + name + " has a different shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (patch off)");
                return false;
            }
            string access = m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsFamily ? "protected" : "internal";
            DeLog.Info("DE.Harmony target " + m.DeclaringType.FullName + "." + m.Name + "(" +
                       string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ") " + access +
                       (m.IsStatic ? " static" : " instance") + " returns " + m.ReturnType.Name);
            return true;
        }

        private static void Patch(Harmony harmony, string name, MethodInfo target, HarmonyMethod prefix, HarmonyMethod finalizer)
        {
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: null, transpiler: null, finalizer: finalizer);
                Applied.Add(name);
                DeLog.Info("DE.Harmony patched " + name + (prefix != null ? " prefix" + (prefix.priority >= 0 ? "(priority " + prefix.priority + ")" : "") : "") +
                           (finalizer != null ? " finalizer" : ""));
            }
            catch (Exception e)
            {
                Missing.Add(name + " patch failed: " + e.GetType().Name);
                DeLog.Info("DE.Harmony MISSING " + name + " patch failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void LogPrefixOrder(MethodBase target)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null) return;
                var order = info.Prefixes.OrderByDescending(p => p.priority).ThenBy(p => p.index)
                    .Select(p => p.owner + "(priority " + p.priority + ")").ToArray();
                DeLog.Info("DE.Harmony ExitThreadSafe prefixes in run order: " + string.Join(" > ", order));
            }
            catch (Exception e) { DeLog.Info("DE.Harmony note: prefix order unavailable (" + e.GetType().Name + ")"); }
        }

        // ============================================================================================ patch bodies
 // Never throw: an exception in a prefix would break the game's own load, save or exit.

        private static void RunLoadPrefix(object __instance)
        {
            try
            {
                object target = null;
                if (_screenToLoadField != null && __instance != null) target = _screenToLoadField.GetValue(__instance);
                bool gameplay = _screenToLoadField == null || target is MyGuiScreenGamePlay;
                string name = target != null ? target.GetType().Name : "null";
                Phases.OnRunLoadStart(gameplay, name, Win32.TickCount64());
                Plugin.OnRunLoadStarted(gameplay, name);
            }
            catch (Exception) { }
        }

        private static void RunLoadFinalizer()
        {
            try
            {
                Phases.OnRunLoadEnd();
                Plugin.OnRunLoadEnded();
            }
            catch (Exception) { }
        }

        private static void SavePrefix()
        {
            try { Phases.OnSaveEnter(); }
            catch (Exception) { }
        }

        private static void SaveFinalizer()
        {
            try { Phases.OnSaveExit(GameState.PhaseOutsideSave()); }
            catch (Exception) { }
        }

        [HarmonyPriority(Priority.First)]
        private static void ExitPrefix()
        {
            try
            {
                Phases.OnExit();
                Plugin.OnExiting();
            }
            catch (Exception) { }
        }
    }
}
