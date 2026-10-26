using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7p-gameplay, the SE host and its <c>de.s7p</c> keys (ROADMAP "### S7p Polish pass" P12, P13, P14). Per plugin
 /// Update (one line in <c>Plugin.UpdateCore</c>): the camera watch every tick (<see cref="CameraAxisWatch"/>), and
 /// once a second the depth-hold rows (<see cref="DampenerHold.Second"/>) and the export backlog row
 /// (<see cref="GridMirrorProducer.S7pExportSecond"/>). <c>de.s7p</c> (C11 and chat <c>/de s7p key=value</c>) answers only
 /// its own keys — <c>hold</c> (on | off | factor 0..2), <c>yawfix</c> (on | off), <c>readAvgMs</c> (ms | off),
 /// <c>action: "gameplay"</c> (status) — and returns null otherwise, so another S7p feature's keys reach its own
 /// handler. Update thread.
 /// </summary>
    internal static class S7pGameplaySe
    {
        public const string Cmd = "de.s7p";
        private static bool _init;
        private static ulong _nextSecondTick;

        private static void Init()
        {
            _init = true;
            string env = Environment.GetEnvironmentVariable(ExportReadDebt.EnvVar);
            double avg;
            string error;
            if (ExportReadDebt.TryParse(env, out avg, out error)) GridMirrorProducer.S7pReadDebt.AvgMs = avg;
            else DeLog.Info("DE.S7p export: " + ExportReadDebt.EnvVar + "=" + env + " " + error + " (default " + ExportReadDebt.DefaultAvgMs + " ms)");
            DeLog.Info("DE.S7p gameplay (SE): depth hold " + (DampenerHold.Patched ? "factor " + DampenerHold.FactorText : "OFF (" + (DampenerHold.PatchError ?? "not patched") + ")") +
                       ", export reads averaged to " + GridMirrorProducer.S7pReadDebt.AvgMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms per tick (debt cap " +
                       GridMirrorProducer.S7pReadDebt.CapMs.ToString("0", CultureInfo.InvariantCulture) + " ms)");
        }

        /// <summary>Plugin Update (S7p-gameplay hook): never throws into the plugin.</summary>
        public static void Tick(ulong nowMs, ulong tick, bool inWorld)
        {
            try
            {
                if (!_init) Init();
                CameraAxisWatch.Tick(tick, inWorld);
                if (!inWorld) { GridMirrorProducer.S7pReadDebt.Reset(); return; }
                DampenerHold.Tick();
                if (tick < _nextSecondTick && _nextSecondTick - tick <= 60) return;
                _nextSecondTick = tick + 60;
                DampenerHold.Second(tick);
                GridMirrorProducer.S7pExportSecond(tick);
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error S7p gameplay tick " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Cmd || args == null) return null;
            object v, act;
            args.TryGetValue("action", out act);
            bool mine = args.ContainsKey("hold") || args.ContainsKey("yawfix") || args.ContainsKey("readAvgMs") || (act as string) == "gameplay";
            if (!mine) return null; // another S7p feature's keys
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            string error;
            if (args.TryGetValue("hold", out v))
            {
                string word = v as string;
                if (word != null && (word.Equals("force", StringComparison.OrdinalIgnoreCase) || word.Equals("thrust", StringComparison.OrdinalIgnoreCase)))
                {
                    DampenerHold.ForceMode = word.Equals("force", StringComparison.OrdinalIgnoreCase);
                    DampenerHold.Forget();
                    DeLog.Info("DE.S7p hold mode " + DampenerHold.ModeText + " (" + via + ")");
                    d["holdMode"] = DampenerHold.ModeText;
                    args.Remove("hold");
                    v = null;
                }
                double f = DampenerHold.Factor;
                if (v != null && !DampenerHoldRules.TryParse(v, out f, out error)) return Fail(error);
                DampenerHold.Factor = f;
                DeLog.Info("DE.S7p hold factor " + DampenerHold.FactorText + (f <= 0 ? " (OFF: ships rise with the buoyancy again)" : "") + " (" + via + ")");
            }
            if (args.TryGetValue("yawfix", out v))
            {
                bool on;
                if (!YawLockRules.TryParse(v, out on, out error)) return Fail(error);
                CameraAxisWatch.FixOn = on;
                DeLog.Info("DE.S7p camera yaw fix " + (on ? "on" : "OFF (trace only)") + " (" + via + ")");
            }
            if (args.TryGetValue("readAvgMs", out v))
            {
                double ms;
                if (!ExportReadDebt.TryParse(v, out ms, out error)) return Fail(error);
                GridMirrorProducer.S7pReadDebt.AvgMs = ms;
                GridMirrorProducer.S7pReadDebt.Reset();
                DeLog.Info("DE.S7p export reads averaged to " + (ms > 0 ? ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms per tick" : "OFF (the S5 deadline only)") + " (" + via + ")");
            }
            d["ok"] = true;
            foreach (KeyValuePair<string, object> kv in DampenerHold.Status()) d[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, object> kv in CameraAxisWatch.Status()) d[kv.Key] = kv.Value;
            ExportReadDebt rd = GridMirrorProducer.S7pReadDebt;
            d["readAvgMs"] = rd.AvgMs;
            d["readDebtMs"] = Math.Round(rd.DebtMs, 3);
            d["readSkippedTicks"] = rd.Skipped;
            d["readSpikes"] = rd.Spikes;
            d["line"] = "DE.S7p gameplay: hold " + (DampenerHold.Patched ? DampenerHold.FactorText : "unpatched") + " (" + DampenerHold.Applications + " applications), yaw fix " +
                        (CameraAxisWatch.FixOn ? "on" : "off") + " (" + CameraAxisWatch.Fixes + " fixes), export reads avg " + rd.AvgMs.ToString("0.##", CultureInfo.InvariantCulture) +
                        " ms (" + rd.Skipped + " ticks skipped)";
            return d;
        }

        private static Dictionary<string, object> Fail(string error)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error } };
        }
    }
}
