using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Chat syntax of the S2 dev commands (game-independent; <see cref="DevChannel"/> turns <c>/de display …</c>,
 /// <c>/de view …</c>, <c>/de audio</c>, <c>/de zorder</c> into the C11 argument objects of <c>S2Commands</c>):
 /// <list type="bullet">
 /// <item><c>/de display [state]</c>, <c>toggle</c>, <c>mode f|aprime</c>, <c>margins on|off|auto</c>,
 /// <c>skip on|off|auto</c>, <c>alphafill on|off</c>, <c>readback [once|on|off]</c>, <c>probe [r g b [tolerance]]</c>,
 /// <c>seethrough on|off</c></item>
 /// <item><c>/de view [force space|track|prime|underwater|auto]</c></item>
 /// <item><c>/de audio [dump]</c>, <c>/de zorder [reassert]</c></item>
 /// </list>
 /// <c>key=value</c> tokens are accepted too (<c>/de display margins=on</c>).
 /// </summary>
    internal static class S2Args
    {
        public const string DisplayUsage = "/de display [state] | toggle | seethrough on|off | mode f|aprime | margins on|off|auto | skip on|off|auto | alphafill on|off | readback [once|on|off] | probe [r g b [tolerance]]";
        public const string ViewUsage = "/de view [force space|track|prime|underwater|auto] | trace on|off";

        public static bool IsS2Verb(string verb)
        {
            return verb == "display" || verb == "view" || verb == "audio" || verb == "zorder";
        }

        /// <summary>argv[0] is the verb; returns the C11 args, or null with <paramref name="usage"/> set.</summary>
        public static Dictionary<string, object> ParseChat(string verb, string[] argv, out string usage)
        {
            usage = null;
            var a = new Dictionary<string, object>(StringComparer.Ordinal);
            var words = new List<string>();
            for (int i = 1; i < argv.Length; i++)
            {
                string t = argv[i];
                int eq = t.IndexOf('=');
                if (eq > 0) a[t.Substring(0, eq)] = Value(t.Substring(eq + 1));
                else words.Add(t);
            }
            string w0 = words.Count > 0 ? words[0].ToLowerInvariant() : null;
            switch (verb)
            {
                case "display":
                    if (w0 == null) { if (a.Count == 0) a["state"] = true; return a; }
                    switch (w0)
                    {
                        case "state": a["state"] = true; return a;
                        case "toggle": a["toggle"] = true; return a;
                        case "seethrough":
                        case "mode":
                        case "margins":
                        case "skip":
                        case "alphafill":
                            if (words.Count < 2) { usage = DisplayUsage; return null; }
                            a[w0 == "alphafill" ? "alphaFill" : w0 == "seethrough" ? "seeThrough" : w0] = words[1].ToLowerInvariant();
                            return a;
                        case "readback":
                            a["readback"] = words.Count > 1 ? words[1].ToLowerInvariant() : "once";
                            return a;
                        case "probe":
                        {
                            a["probe"] = true;
                            if (words.Count >= 4)
                            {
                                double r, g, b;
                                if (!Num(words[1], out r) || !Num(words[2], out g) || !Num(words[3], out b)) { usage = DisplayUsage; return null; }
                                a["expect"] = new List<object> { r, g, b };
                                double tol;
                                if (words.Count >= 5 && Num(words[4], out tol)) a["tolerance"] = tol;
                            }
                            else if (words.Count > 1) { usage = DisplayUsage; return null; }
                            return a;
                        }
                        default:
                            usage = DisplayUsage;
                            return null;
                    }
                case "view":
                    if (w0 == null) { if (a.Count == 0) a["state"] = true; return a; }
                    if (w0 == "force" && words.Count >= 2) { a["force"] = words[1].ToLowerInvariant(); return a; }
                    if (w0 == "trace" && words.Count >= 2) { a["trace"] = words[1].ToLowerInvariant(); return a; }
                    if (w0 == "state") { a["state"] = true; return a; }
                    usage = ViewUsage;
                    return null;
                case "audio":
                    a["dump"] = true;
                    return a;
                case "zorder":
                    if (w0 == null || w0 == "reassert") { a["reassert"] = true; return a; }
                    if (w0 == "state") { a["state"] = true; return a; }
                    usage = "/de zorder [reassert|state]";
                    return null;
                default:
                    usage = "unknown S2 verb " + verb;
                    return null;
            }
        }

        /// <summary>A switch value: on/off/true/false/1/0/auto; null for auto, absent or unrecognised (<paramref name="ok"/> false then).</summary>
        public static bool? Switch(IDictionary<string, object> args, string key, out bool present, out bool ok)
        {
            object v;
            present = args != null && args.TryGetValue(key, out v) && v != null;
            ok = true;
            if (!present) return null;
            args.TryGetValue(key, out v);
            string s = v as string;
            if (s != null && s.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
            bool? b = S2Config.ParseBool(v);
            if (!b.HasValue) ok = false;
            return b;
        }

        /// <summary>Parses a <c>de.view force</c> word; null for auto.</summary>
        public static bool TryParseForce(string s, out S2State? state)
        {
            state = null;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "auto": case "off": case "none": return true;
                case "space": state = S2State.Space; return true;
                case "track": case "approach": state = S2State.Track; return true;
                case "prime": state = S2State.Prime; return true;
                case "underwater": state = S2State.Underwater; return true;
                case "surfacing": state = S2State.Surfacing; return true;
                default: return false;
            }
        }

        private static object Value(string s)
        {
            double d;
            if (Num(s, out d)) return d;
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return s;
        }

        private static bool Num(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
