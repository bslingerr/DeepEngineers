using System;
using System.Reflection;
using DeepEngineers.Wire.Native;
using HarmonyLib;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The fail-safe's S4 part (CONTRACT §7 "Tiles cannot be created in a DE world", ROADMAP S4 "SE plugin"): SE half.
 /// <list type="bullet">
 /// <item><see cref="Activate"/>: <c>DE.Terrain FAILSAFE ON &lt;reason&gt;</c> once, the HUD warning "DeepEngineers terrain
 /// missing: saving disabled"
 /// shown from the first simulation update on, then every 60 s; <c>seFlags.failSafeActive</c> 1 (plugin Update).</item>
 /// <item>Saving blocked: a prefix on <c>MySession.Save(out MySessionSnapshot, string, Action&lt;SaveProgress&gt)</c>
 /// returning false with
 /// <c>__result = false</c> and a null snapshot; <c>DE.Terrain SaveBlocked n=&lt;count&gt;</c>. The S0 phase prefix and
 /// finalizer on the same method still run (the finalizer restores the phase).</item>
 /// <item>The grid hold and the crush suspension are S8 (CONTRACT §7); <see cref="InFootprint"/> is their test.</item>
 /// </list>
 /// </summary>
    internal static partial class TerrainFailSafe
    {
        private static volatile bool _hudPending;
        private static ulong _nextHudMs;
        public static bool SaveBlockInstalled { get; private set; }

        /// <summary>Turns the fail-safe on for the rest of the session (idempotent).</summary>
        public static void Activate(string reason)
        {
            if (!SetActive(reason)) return;
            DeLog.Info("DE.Terrain FAILSAFE ON " + reason + " (rest of the session: saving blocked" + (SaveBlockInstalled ? "" : " — BUT the save-block patch is MISSING") +
                       ", HUD warning, seFlags.failSafeActive 1; grids in the footprint are held from S8; a reload evaluates again)");
            Jsonl.WriteLine(S4Rows.Row("s4.failsafe", "active", true, "reason", reason, "saveBlockInstalled", SaveBlockInstalled));
            _hudPending = true;                              // shown by Tick at the next simulation update (never from BeforeStart)
        }

        /// <summary>Game thread, every simulation update: shows a pending warning (until it could be shown), then every 60 s.</summary>
        public static void Tick(ulong nowMs)
        {
            if (!Active) return;
            if (_hudPending || nowMs >= _nextHudMs) ShowHud();
        }

        private static void ShowHud()
        {
            try
            {
                var u = MyAPIGateway.Utilities;
                if (u == null) return;
                u.ShowNotification(HudText, 10000, "Red");
                _hudPending = false;
                _nextHudMs = Win32.TickCount64() + 60000;
            }
            catch (Exception) { /* the HUD is not up yet (BeforeStart): retried by Tick */ }
        }

        // ================================================================================================ save block

        internal static void InstallSaveBlock(Harmony h)
        {
            const string name = "MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)";
            MethodInfo save = AccessTools.Method(typeof(MySession), "Save",
                new[] { typeof(MySessionSnapshot).MakeByRefType(), typeof(string), typeof(Action<SaveProgress>) });
            if (save == null || save.IsStatic || save.ReturnType != typeof(bool))
            {
                DeLog.Info("DE.Terrain GUARD saveblock MISSING " + name + (save == null ? " not found" : " has another shape") +
                           " (the fail-safe could not block saving; logged, terrain unaffected)");
                return;
            }
            try
            {
                var pre = new HarmonyMethod(typeof(TerrainFailSafe).GetMethod(nameof(SavePrefix), BindingFlags.Static | BindingFlags.NonPublic));
                pre.priority = Priority.High;
                h.Patch(save, prefix: pre);
                SaveBlockInstalled = true;
                DeLog.Info("DE.Terrain GUARD saveblock ok " + name + " prefix (blocks only while the terrain fail-safe is on)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Terrain GUARD saveblock MISSING " + name + " patch failed " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Positional names: __0 = the out snapshot. Blocks before anything that could throw (never fail-open).</summary>
        private static bool SavePrefix(ref MySessionSnapshot __0, ref bool __result)
        {
            if (!Active) return true;
            __0 = null;
            __result = false;
            try
            {
                long n = CountSaveBlocked();
                DeLog.Info("DE.Terrain SaveBlocked n=" + n + " (" + (Reason ?? "fail-safe") + ")");
                _hudPending = true;
                ShowHud();
            }
            catch (Exception) { }
            return false;
        }
    }
}
