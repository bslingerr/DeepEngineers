using System;
using System.Collections.Generic;
using System.IO;

namespace DeepEngineers.SE
{
    /// <summary>The display design in use (CONTRACT §5.4 C5 <c>displayMode</c>: 0 F, 1 A′, 2 C).</summary>
    internal enum DisplayDesign
    {
        /// <summary>Design F, "see-through SE": DWM margins, scene skipped while Subnautica's picture is requested (S7a <c>Site</c>: the
 /// picture latch, inside the site below the band; <c>Waterline</c>: underwater), SE's HUD over Subnautica.</summary>
        F = 0,
        /// <summary>Fallback A′: SE lowers itself below Subnautica while its picture is requested (S7a <c>Site</c>: the latch;
 /// <c>Waterline</c>: underwater) and stops drawing; raised for menus.</summary>
        APrime = 1,
    }

    /// <summary>
 /// S2 configuration (ARCHITECTURE §5.1 "Mode selection at start": config <c>Display = F | APrime | C</c>, default F).
 /// Read once at plugin Init from <c>%LOCALAPPDATA%\DeepEngineers\config.json</c> (bridge-owned, ARCHITECTURE §5.15);
 /// the environment variable <c>DEEPENGINEERS_DISPLAY</c> overrides the display design (<c>aprime</c> selects the
 /// fallback A′ without editing a file) and <c>DEEPENGINEERS_S2=0</c> switches the whole S2 runtime off (the S1
 /// behaviour: view state Space, the S1 perf mode). Game-independent (tests parse synthetic files).
 /// <para>Keys (all optional; unknown keys are ignored so the file can be shared with other components):</para>
 /// <list type="bullet">
 /// <item><c>"s2": true|false</c> (default true)</item>
 /// <item><c>"display": "f" | "aprime"</c> (default "f"; "c" is not implemented and falls back to F with a note)</item>
 /// <item><c>"alphaFill": true|false</c> (default true): the Prime alpha fill after the sprites (HYPOTHESIS unit)</item>
 /// <item><c>"seeThroughKey": "Apps"</c> (dev builds only; a <c>VRage.Input.MyKeys</c> name; "none" disables the hotkey;
 /// default <c>Apps</c> = the Menu key since S5a: SE binds F8 to its free spectator, <see cref="DisplayController"/>)</item>
 /// <item><c>"alphaReadback": "off" | "on"</c> (dev only; "on" = one backbuffer alpha readback every 5 s)</item>
 /// <item><c>"autoFallback": true|false</c> (default true): a composed-pixel probe that finds the calibration colour
 /// missing switches the session to A′ (ARCHITECTURE §5.1 "Not honoured → A′")</item>
 /// </list>
 /// </summary>
    internal sealed class S2Config
    {
        public const string FileName = "config.json";
        public const string DisplayEnvVar = "DEEPENGINEERS_DISPLAY";
        public const string S2EnvVar = "DEEPENGINEERS_S2";

        public bool Enabled = true;
        public DisplayDesign Display = DisplayDesign.F;
        public bool AlphaFill = true;
        public string SeeThroughKey = DefaultSeeThroughKey;
        /// <summary>S5a: <c>Apps</c> (the Menu key; SE binds no default control to it). F8 is SE's free spectator.</summary>
        public const string DefaultSeeThroughKey = "Apps";
        public bool AlphaReadbackPeriodic;
        public bool AutoFallback = true;
        /// <summary>Where the display design came from: "default", "config.json" or the environment variable.</summary>
        public string DisplaySource = "default";
        /// <summary>Parse notes (bad values, unsupported design C, unreadable file).</summary>
        public readonly List<string> Notes = new List<string>();

        public string Describe()
        {
            return "s2=" + (Enabled ? "on" : "off") + " display=" + Name(Display) + " (" + DisplaySource + ") alphaFill=" +
                   (AlphaFill ? "on" : "off") + " seeThroughKey=" + (string.IsNullOrEmpty(SeeThroughKey) ? "none" : SeeThroughKey) +
                   " alphaReadback=" + (AlphaReadbackPeriodic ? "on" : "off") + " autoFallback=" + (AutoFallback ? "on" : "off") +
                   (Notes.Count > 0 ? " notes: " + string.Join("; ", Notes.ToArray()) : "");
        }

        public static string Name(DisplayDesign d) { return d == DisplayDesign.APrime ? "A'" : "F"; }

        /// <summary>
 /// Parses a display design word: f, design-f, see-through → F; aprime, a', a′, a-prime, a_prime, fallback → A′.
 /// "c" (contingency C) is recognised but not implemented: false with <paramref name="note"/> set.
 /// </summary>
        public static bool TryParseDisplay(string s, out DisplayDesign design, out string note)
        {
            design = DisplayDesign.F;
            note = null;
            if (s == null) return false;
            string k = s.Trim().ToLowerInvariant().Replace("′", "'").Replace("’", "'");
            switch (k)
            {
                case "f":
                case "design-f":
                case "designf":
                case "see-through":
                case "seethrough":
                    design = DisplayDesign.F;
                    return true;
                case "aprime":
                case "a'":
                case "a-prime":
                case "a_prime":
                case "a prime":
                case "fallback":
                    design = DisplayDesign.APrime;
                    return true;
                case "c":
                    note = "display 'c' (contingency C, shared texture) is not implemented in S2: design F kept";
                    return false;
                default:
                    note = "display '" + s + "' not recognised (f | aprime)";
                    return false;
            }
        }

        /// <summary>
 /// Builds the configuration from the file text (null when absent) and the two environment values (null when
 /// unset). Never throws.
 /// </summary>
        public static S2Config Parse(string json, string envDisplay, string envS2)
        {
            var c = new S2Config();
            if (!string.IsNullOrEmpty(json))
            {
                IDictionary<string, object> o = null;
                try { o = MiniJson.Parse(json) as IDictionary<string, object>; }
                catch (Exception e) { c.Notes.Add(FileName + " not parsed (" + e.Message + "): defaults used"); }
                if (o == null && c.Notes.Count == 0) c.Notes.Add(FileName + " is not a JSON object: defaults used");
                if (o != null) c.ApplyObject(o);
            }
            if (!string.IsNullOrEmpty(envDisplay))
            {
                DisplayDesign d;
                string note;
                if (TryParseDisplay(envDisplay, out d, out note)) { c.Display = d; c.DisplaySource = DisplayEnvVar; }
                else c.Notes.Add(DisplayEnvVar + ": " + note);
            }
            if (!string.IsNullOrEmpty(envS2))
            {
                bool? b = ParseBool(envS2);
                if (b.HasValue) c.Enabled = b.Value;
                else c.Notes.Add(S2EnvVar + "='" + envS2 + "' not recognised (0|1|on|off)");
            }
            return c;
        }

        private void ApplyObject(IDictionary<string, object> o)
        {
            object v;
            if (o.TryGetValue("s2", out v))
            {
                bool? b = ParseBool(v);
                if (b.HasValue) Enabled = b.Value; else Notes.Add("s2: not a boolean");
            }
            string display = MiniJson.GetString(o, "display");
            if (display != null)
            {
                DisplayDesign d;
                string note;
                if (TryParseDisplay(display, out d, out note)) { Display = d; DisplaySource = FileName; }
                else Notes.Add("display: " + note);
            }
            if (o.TryGetValue("alphaFill", out v))
            {
                bool? b = ParseBool(v);
                if (b.HasValue) AlphaFill = b.Value; else Notes.Add("alphaFill: not a boolean");
            }
            if (o.TryGetValue("seeThroughKey", out v))
            {
                string k = v as string;
                if (k == null) Notes.Add("seeThroughKey: not a string");
                else SeeThroughKey = k.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : k.Trim();
            }
            if (o.TryGetValue("alphaReadback", out v))
            {
                bool? b = ParseBool(v);
                if (b.HasValue) AlphaReadbackPeriodic = b.Value; else Notes.Add("alphaReadback: on|off");
            }
            if (o.TryGetValue("autoFallback", out v))
            {
                bool? b = ParseBool(v);
                if (b.HasValue) AutoFallback = b.Value; else Notes.Add("autoFallback: not a boolean");
            }
        }

        /// <summary>true/false, on/off, 1/0, yes/no (strings, case-insensitive), JSON booleans and numbers.</summary>
        public static bool? ParseBool(object v)
        {
            if (v == null) return null;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            string s = (v as string ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "1": case "on": case "true": case "yes": return true;
                case "0": case "off": case "false": case "no": return false;
                default: return null;
            }
        }

        /// <summary>The configuration of this process: the file under %LOCALAPPDATA%\DeepEngineers and the environment.</summary>
        public static S2Config Load()
        {
            string json = null;
            string fileNote = null;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", FileName);
                if (File.Exists(path)) json = File.ReadAllText(path);
            }
            catch (Exception e) { fileNote = FileName + " unreadable (" + e.GetType().Name + "): defaults used"; }
            string env = null, envS2 = null;
            try { env = Environment.GetEnvironmentVariable(DisplayEnvVar); envS2 = Environment.GetEnvironmentVariable(S2EnvVar); }
            catch (Exception) { }
            S2Config c = Parse(json, env, envS2);
            if (fileNote != null) c.Notes.Add(fileNote);
            return c;
        }
    }
}
