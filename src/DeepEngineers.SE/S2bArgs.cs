using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Chat syntax of <c>/de swim</c> (game-independent; <see cref="DevChannel"/> turns it into the C11 <c>de.swim</c>
 /// argument object of <see cref="S2bCommands"/>):
 /// <list type="bullet">
 /// <item><c>/de swim</c> or <c>/de swim dump</c>: state, numbers, script, saved jetpack/dampeners, oxygen;</item>
 /// <item><c>/de swim on|off</c>, <c>/de swim model on|off</c>: the swim model (off = stock SE movement);</item>
 /// <item><c>/de swim script &lt;s,f,st,v,sp,yaw,pitch[,jet];…&gt;</c>, <c>/de swim stop</c>: the scripted input source;</item>
 /// <item><c>/de swim tp &lt;x&gt; &lt;y&gt; &lt;z&gt; [yawDeg]</c>: the character to an SN point (feet), upright;</item>
 /// <item><c>/de swim o2</c>: the oxygen rule's numbers; <c>/de swim o2 &lt;0..1&gt;|auto</c> (S5a, dev): the override of the
 /// O2 fraction mirrored to Subnautica (test only);</item>
 /// <item><c>/de swim fins off|basic|ultra</c> (S5a): the fins modifier (+0 / +1.9 / +3.2 m/s; default ultra).</item>
 /// </list>
 /// <c>key=value</c> tokens are accepted too (<c>/de swim model=off</c>).
 /// </summary>
    internal static class S2bArgs
    {
        public const string SwimUsage = "/de swim [dump] | on|off | model on|off | script <s,f,st,v,sp,yaw,pitch[,jet];...> | stop | tp <x> <y> <z> [yawDeg] | o2 [<0..1>|auto] | fins off|basic|ultra";

        /// <summary>argv[0] is "swim"; returns the C11 args, or null with <paramref name="usage"/> set.</summary>
        public static Dictionary<string, object> ParseChat(string[] argv, out string usage)
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
            if (w0 == null)
            {
                if (a.Count == 0) a["dump"] = true;
                return a;
            }
            switch (w0)
            {
                case "dump":
                case "state":
                    a["dump"] = true;
                    return a;
                case "on":
                case "off":
                    a["model"] = w0;
                    return a;
                case "model":
                    if (words.Count < 2 || !(words[1].Equals("on", StringComparison.OrdinalIgnoreCase) || words[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
                    {
                        usage = SwimUsage;
                        return null;
                    }
                    a["model"] = words[1].ToLowerInvariant();
                    return a;
                case "stop":
                    a["script"] = "stop";
                    return a;
                case "script":
                    if (words.Count < 2) { usage = SwimUsage; return null; }
                    a["script"] = string.Join(";", words.GetRange(1, words.Count - 1).ToArray());
                    return a;
                case "tp":
                case "teleport":
                case "teleportchar":
                {
                    double x, y, z;
                    if (words.Count < 4 || !Num(words[1], out x) || !Num(words[2], out y) || !Num(words[3], out z)) { usage = SwimUsage; return null; }
                    a["teleportChar"] = new List<object> { x, y, z };
                    double yaw;
                    if (words.Count > 4)
                    {
                        if (!Num(words[4], out yaw)) { usage = SwimUsage; return null; }
                        a["yawDeg"] = yaw;
                    }
                    return a;
                }
                case "o2":
                case "oxygen":
                {
                    if (words.Count < 2) { a["o2"] = "dump"; return a; }
                    double frac;
                    if (words[1].Equals("auto", StringComparison.OrdinalIgnoreCase) || words[1].Equals("off", StringComparison.OrdinalIgnoreCase)) a["o2"] = "auto";
                    else if (words[1].Equals("dump", StringComparison.OrdinalIgnoreCase)) a["o2"] = "dump";
                    else if (Num(words[1], out frac) && frac >= 0.0 && frac <= 1.0) a["o2"] = frac;
                    else { usage = SwimUsage; return null; }
                    return a;
                }
                case "fins":
                {
                    uint f;
                    if (words.Count < 2 || !SwimFins.TryParse(words[1], out f)) { usage = SwimUsage; return null; }
                    a["fins"] = SwimFins.Name(f);
                    return a;
                }
                default:
                    usage = SwimUsage;
                    return null;
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

        private static bool Num(string s, out double d)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
    }
}
