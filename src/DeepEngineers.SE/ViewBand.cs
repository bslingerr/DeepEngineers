using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>

 /// <c>DEEPENGINEERS_BAND=enter,exit</c> at start, <c>de.s7 {band: [enter, exit] | "enter,exit" | "default" | "status"}</c>,
 /// <c>/de s7 band &lt;enter&gt; &lt;exit&gt;|default|status</c>. Metres above the sea, <c>0 ≤ enter &lt; exit</c>; the view machine
 /// takes it on its next step (<see cref="ViewThresholds.SetBand"/>). The S7a mirror radii above water (1,500 / 1,600 m)
 /// are pinned for the default band: with a band far above 1 km a ship right below the camera pops in after the swap.
 /// S7t T3: the default is +1,000 / +1,100 m again (<see cref="ViewThresholds.DefaultApproachEnterY"/>; it was
 /// +400 / +450 m from 16:50): the crossfade (T1), Subnautica's horizon and haze (T2), the look match (T5) and the
 /// impostors above the picture camera (T3, <c>S7tImpostorRules</c>) make the 1 km swap clean.
 /// </summary>
    internal static class ViewBand
    {
        public const string EnvVar = "DEEPENGINEERS_BAND";
        public static long Changes;

        /// <summary>Once at S2Host.Init (before the first machine step).</summary>
        public static void ApplyEnv()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrEmpty(v))
                {
                    double e, x;
                    if (ViewThresholds.TryParseBand(v, out e, out x) && ViewThresholds.SetBand(e, x, "env")) Changes++;
                    else DeLog.Info("DE.View band env ignored: " + EnvVar + "='" + v + "' (needs enter,exit metres with 0 <= enter < exit)");
                }
            }
            catch (Exception) { }
            DeLog.Info("DE.View band " + Describe() + " (env " + EnvVar + "=enter,exit; /de s7 band <enter> <exit>|default|status)");
        }

        public static string Describe()
        {
            return "enter=+" + N(ViewThresholds.ApproachEnterY) + " exit=+" + N(ViewThresholds.ApproachExitY) + " m (" + ViewThresholds.BandSource + ")";
        }

        private static string N(double v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enter", ViewThresholds.ApproachEnterY }, { "exit", ViewThresholds.ApproachExitY }, { "source", ViewThresholds.BandSource },
                { "defaultEnter", ViewThresholds.DefaultApproachEnterY }, { "defaultExit", ViewThresholds.DefaultApproachExitY },
                { "changes", Changes },
            };
        }

        private static Dictionary<string, object> Fail(string err)
        {
            DeLog.Info("DE.View " + err);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", err }, { "line", err } };
        }

        /// <summary><c>de.s7 {band: [enter, exit] | "enter,exit" | "default" | "status"}</c>.</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            object v = null;
            if (args != null) args.TryGetValue("band", out v);
            double enter = double.NaN, exit = double.NaN;
            string text = v as string;
            var list = v as IList<object>;
            bool status = text != null && text.Equals("status", StringComparison.OrdinalIgnoreCase);
            if (text != null && text.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                enter = ViewThresholds.DefaultApproachEnterY;
                exit = ViewThresholds.DefaultApproachExitY;
            }
            else if (list != null && list.Count == 2 && list[0] is double && list[1] is double)
            {
                enter = (double)list[0];
                exit = (double)list[1];
            }
            else if (text != null && !status && !ViewThresholds.TryParseBand(text, out enter, out exit)) enter = double.NaN;
            if (!status && double.IsNaN(enter))
                return Fail("de.s7: band must be [enter, exit] metres (0 <= enter < exit), \"default\" or \"status\"");
            double wasE = ViewThresholds.ApproachEnterY, wasX = ViewThresholds.ApproachExitY;
            bool changed = false;
            if (!status)
            {
                if (!ViewThresholds.SetBand(enter, exit, "de.s7"))
                    return Fail("de.s7: band refused (" + N(enter) + ", " + N(exit) + "): needs 0 <= enter < exit");
                changed = wasE != ViewThresholds.ApproachEnterY || wasX != ViewThresholds.ApproachExitY;
                if (changed) Changes++;
            }
            string line = "DE.View band " + Describe() +
                          (status ? "" : changed ? " (was +" + N(wasE) + " / +" + N(wasX) + "; the machine takes it on its next step; the S7a mirror radii stay 1,500 / 1,600 m, the S7t impostor reach 5 km above the camera)" : " (unchanged)");
            DeLog.Info(line);
            Dictionary<string, object> d = Status();
            d["ok"] = true;
            d["changed"] = changed;
            d["line"] = line;
            return d;
        }
    }
}
