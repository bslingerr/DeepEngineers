using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// <c>de.s7t</c> (C11, dev builds) and <c>/de s7t …</c> (chat), the se-composite keys only (the verb is shared with the
 /// other S7t builders: a call without these keys returns null so the next handler answers):
 /// <list type="bullet">
 /// <item><c>fade: &lt;ms&gt; | on | off | straight | premul</c> (chat <c>/de s7t fade 700</c>);</item>
 /// <item><c>flash: on | off</c>, <c>fadeAprime: on | off</c>, <c>impostors: on | off</c>;</item>
 /// <item><c>action: composite</c> (or a bare <c>de.s7t</c>): the state.</item>
 /// </list>
 /// </summary>
    internal static class S7tComposite
    {
        public const string Cmd = "de.s7t";
        public const string ChatUsage = "/de s7t fade <ms>|on|off|straight|premul | flash on|off | fadeAprime on|off | impostors on|off | composite";

        private static readonly string[] Keys = { "fade", "flash", "fadeAprime", "impostors" };

        /// <summary>Plugin.ExtraCommand chain: the reply, or null when the call carries none of these keys.</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != Cmd) return null;
            // chat (/de s7t …, DevChannel's shared verb): the words arrive as argv; mine start with one of my keys or "composite"
            object argvObj;
            if (args != null && args.TryGetValue("argv", out argvObj) && argvObj is System.Collections.IList)
            {
                var words = new List<string>();
                foreach (object o in (System.Collections.IList)argvObj) words.Add(o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture));
                if (words.Count == 0) return null;
                string first = words[0].Trim();
                int eq = first.IndexOf('=');
                string head = eq > 0 ? first.Substring(0, eq) : first;
                if (Array.IndexOf(Keys, head) < 0 && !head.Equals("composite", StringComparison.OrdinalIgnoreCase)) return null;
                args = S7pScene.ChatArgs(words);
            }
            string action = null;
            object av;
            if (args != null && args.TryGetValue("action", out av)) action = av as string;
            bool any = false, other = false;
            if (args != null)
                foreach (KeyValuePair<string, object> kv in args)
                {
                    if (kv.Key == "via" || kv.Key == "argv" || kv.Key == "action") continue;
                    if (Array.IndexOf(Keys, kv.Key) >= 0) any = true; else other = true;
                }
            bool status = action != null && action.Equals("composite", StringComparison.OrdinalIgnoreCase);
            bool bare = !any && !other && action == null;
            if (!any && !status && !bare) return null;

            var applied = new List<string>();
            var problems = new List<string>();
            if (args != null)
            {
                object v;
                if (args.TryGetValue("fade", out v)) ApplyFade(v, applied, problems);
                if (args.TryGetValue("flash", out v)) { S7tSettings.FlashWithFade = S7tSettings.OnOff(v, S7tSettings.FlashWithFade); applied.Add("flash=" + (S7tSettings.FlashWithFade ? "on" : "off")); }
                if (args.TryGetValue("fadeAprime", out v)) { S7tSettings.APrimeRamp = S7tSettings.OnOff(v, S7tSettings.APrimeRamp); applied.Add("fadeAprime=" + (S7tSettings.APrimeRamp ? "on" : "off")); }
                if (args.TryGetValue("impostors", out v)) { S7tSettings.Impostors = S7tSettings.OnOff(v, S7tSettings.Impostors); applied.Add("impostors=" + (S7tSettings.Impostors ? "on" : "off")); }
            }
            string line = "DE.S7t composite " + S7tSettings.Describe() + " band=+" + ViewThresholds.ApproachEnterY.ToString("0", CultureInfo.InvariantCulture) +
                          " / +" + ViewThresholds.ApproachExitY.ToString("0", CultureInfo.InvariantCulture) + " m" +
                          (applied.Count > 0 ? "; applied " + string.Join(", ", applied.ToArray()) : "") +
                          (problems.Count > 0 ? "; problems: " + string.Join("; ", problems.ToArray()) : "") + " (via " + via + ")";
            DeLog.Info(line);
            var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", problems.Count == 0 }, { "line", line }, { "settings", S7tSettings.Describe() } };
            try
            {
                if (S2Host.Controller != null) r["crossfade"] = S2Host.Controller.DescribeFade();
                r["impostors"] = GridMirrorProducer.S7tImpostorStatus();
            }
            catch (Exception e) { r["statusError"] = e.GetType().Name + ": " + e.Message; }
            if (problems.Count > 0) r["error"] = string.Join("; ", problems.ToArray());
            return r;
        }

        private static void ApplyFade(object v, List<string> applied, List<string> problems)
        {
            string s = v is double ? ((double)v).ToString(CultureInfo.InvariantCulture) : (v as string ?? (v is bool ? ((bool)v ? "on" : "off") : null));
            if (s == null) { problems.Add("fade needs <ms>|on|off|straight|premul"); return; }
            string t = s.Trim().ToLowerInvariant();
            if (t == "straight") { S7tSettings.Straight = true; applied.Add("fade straight (colour not premultiplied: dev comparison)"); return; }
            if (t == "premul" || t == "premultiplied") { S7tSettings.Straight = false; applied.Add("fade premultiplied"); return; }
            if (t == "on" || t == "true" || t == "default") { S7tSettings.FadeMs = CrossfadeRules.DefaultMs; S7tSettings.FadeSource = "de.s7t"; applied.Add("fade=" + CrossfadeRules.DefaultMs + " ms"); return; }
            double ms;
            if (!CrossfadeRules.TryParseMs(t, out ms)) { problems.Add("fade '" + s + "': needs " + CrossfadeRules.MinMs + ".." + CrossfadeRules.MaxMs + " ms, on or off"); return; }
            S7tSettings.FadeMs = ms;
            S7tSettings.FadeSource = "de.s7t";
            applied.Add(ms > 0 ? "fade=" + ms.ToString("0", CultureInfo.InvariantCulture) + " ms" : "fade off (S7a's cut and band tint)");
        }
    }
}
