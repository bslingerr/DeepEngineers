using System;
using System.Collections;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// S9: which <c>/de …</c> chat lines and which C11 requests a release build still
 /// executes. Dev builds run everything (CONTRACT §5.1 C11, ROADMAP §5.10). A release build (both plugins compiled with
 /// <c>DEEPENG_RELEASE</c>) keeps only the user-facing tuning commands and the two information verbs:
 /// <list type="bullet">
 /// <item><c>de.water</c> (any arguments): the Water Mod's look (SE).</item>
 /// <item><c>de.s7</c> with <c>band</c> (SE: the altitude band of the Subnautica picture), <c>lightScale</c> and
 /// <c>landmarks</c> (Subnautica: mirrored light strength, the Aurora).</item>
 /// <item><c>de.s7t</c> with <c>look</c> (both sides), <c>horizon</c> and <c>clouds</c> (Subnautica).</item>
 /// <item><c>de.s7p</c> with <c>fx</c> (both sides: effect strength).</item>
 /// <item>chat <c>/de help</c> and <c>/de version</c> (SE only, read-only).</item>
 /// </list>
 /// Everything else (mutants, the lab and test commands, teleports, captures, the self-test, the S0.N12 version hook,
 /// stand-in hooks) is refused in a release build. Pure and game-free: SE's DevChannel and Subnautica's bridge pass the
 /// flavour in.
 /// <para>Request form (C11 JSON <c>{"cmd": …, "args": …}</c>): the selector words are <c>args.argv[0]</c> when an
 /// <c>argv</c> list is present (the chat relay <c>/de sn s7 lightScale suit 0.04</c> sends
 /// <c>{argv: ["lightScale", "suit", "0.04"]}</c>) and every other key of <c>args</c> except <c>argv</c> and
 /// <c>action</c> (a modifier several handlers read, never a feature on its own here). A command with selectors in the
 /// table needs at least one selector, and every selector must be allowed (case-insensitive).</para>
 /// </summary>
    public static class UserCommandRules
    {
        /// <summary>The release table: command → allowed selector words (an empty list = any arguments).</summary>
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "de.water", new string[0] },
            { "de.s7", new[] { "band", "lightScale", "landmarks" } },
            { "de.s7t", new[] { "look", "horizon", "clouds" } },
            { "de.s7p", new[] { "fx" } },

            { "de.display", new[] { "mode", "state" } },
        };

        /// <summary>Chat verbs that only read state on SE (never relayed in a release build).</summary>
        private static readonly string[] InfoVerbs = { "help", "version" };

        /// <summary>The refusal shown in chat and in a C11 reply of a release build.</summary>
        public const string RefusalText = "developer command, not in the release build (/de help lists the commands it keeps)";

        /// <summary>The help lines of a release build (SE chat).</summary>
        public static readonly string[] ReleaseHelp =
        {
            "/de water [key=value …] | reset | textures | help  - the Water Mod's look (waveHeight, fog=r,g,b, …)",
            "/de s7 band <enter> <exit> | default | status  - the altitude band of the Subnautica picture (default 1000 1100 m)",
            "/de s7p fx [status | reset | <type> <scale> | type=scale …]  - effect strength (light, glow, muzzle, …), both games",
            "/de s7t look [key=value …] | off | status  - Space Engineers' look at the band",
            "/de sn s7t look shadow=… ambient=… | horizon on|off | clouds on|off  - Subnautica's look",
            "/de sn s7 lightScale <kind> <factor> | status | reset  - mirrored light strength; /de sn s7 landmarks on|off  - the Aurora",
            "/de display mode aprime | f | state  - the fallback picture mode when Subnautica never shows through under water",
            "/de version  - versions and build flavour",
        };

        /// <summary>
 /// Subnautica: whether to read C11c this frame. Dev: whenever <c>devControl</c> is non-zero (harness bit 0 or SE's
 /// chat relay bit 1). Release: only while SE's chat relay holds the ring (bit 1) and no harness is attached (bit 0
 /// clear); a release build never serves a test harness.
 /// </summary>
        public static bool ControlReadAllowed(bool dev, uint devControl)
        {
            if (devControl == 0) return false;
            if (dev) return true;
            return (devControl & DevControl.SeChatRelay) != 0 && (devControl & DevControl.HarnessAttached) == 0;
        }

        /// <summary>SE: whether to drain the harness ring C11a (dev builds only, bit 0 set).</summary>
        public static bool HarnessReadAllowed(bool dev, uint devControl)
        {
            return dev && (devControl & DevControl.HarnessAttached) != 0;
        }

        /// <summary>
 /// True when a C11 request may run: always in a dev build; in a release build only a command of the table whose
 /// selectors are allowed (see the class summary).
 /// </summary>
        public static bool RequestAllowed(bool dev, string cmd, object args)
        {
            if (dev) return true;
            if (string.IsNullOrEmpty(cmd)) return false;
            string[] allowed;
            if (!Table.TryGetValue(cmd.Trim(), out allowed)) return false;
            if (allowed.Length == 0) return true;
            List<string> selectors = Selectors(args);
            if (selectors == null || selectors.Count == 0) return false;
            foreach (string s in selectors)
                if (!Contains(allowed, s)) return false;
            return true;
        }

        /// <summary>
 /// True when a <c>/de …</c> chat line may run on SE (<paramref name="argv"/> = the words after <c>/de</c>):
 /// always in a dev build; in a release build <c>help</c> (also the empty line), <c>version</c>, a table command
 /// whose first argument word is allowed (<c>/de s7 band …</c>), and <c>/de sn &lt;cmd&gt; &lt;word&gt; …</c> for the
 /// same table (<c>/de sn s7t horizon off</c>).
 /// </summary>
        public static bool ChatAllowed(bool dev, IList<string> argv)
        {
            if (dev) return true;
            if (argv == null || argv.Count == 0) return true; // "/de" alone shows the help
            string verb = (argv[0] ?? "").Trim();
            if (Contains(InfoVerbs, verb)) return true;
            if (verb.Equals("sn", StringComparison.OrdinalIgnoreCase))
            {
                if (argv.Count < 2) return false;
                string cmd = (argv[1] ?? "").Trim();
                if (!cmd.StartsWith("de.", StringComparison.OrdinalIgnoreCase)) cmd = "de." + cmd;
                return WordAllowed(cmd, argv.Count > 2 ? argv[2] : null);
            }
            return WordAllowed("de." + verb, argv.Count > 1 ? argv[1] : null);
        }

        /// <summary>The words a request selects (null when the arguments are neither an object nor absent).</summary>
        public static List<string> Selectors(object args)
        {
            var list = new List<string>();
            if (args == null) return list;
            var dict = args as IDictionary<string, object>;
            if (dict == null) return null;
            object argv;
            if (dict.TryGetValue("argv", out argv))
            {
                var words = argv as IList;
                if (words == null || words.Count == 0) return null;
                string first = words[0] as string;
                if (string.IsNullOrEmpty(first)) return null;
                list.Add(first.Trim());
            }
            foreach (KeyValuePair<string, object> kv in dict)
            {
                if (kv.Key == null) continue;
                if (kv.Key.Equals("argv", StringComparison.Ordinal) || kv.Key.Equals("action", StringComparison.Ordinal)) continue;
                list.Add(kv.Key.Trim());
            }
            return list;
        }

        private static bool WordAllowed(string cmd, string word)
        {
            string[] allowed;
            if (!Table.TryGetValue(cmd, out allowed)) return false;
            if (allowed.Length == 0) return true;
            if (string.IsNullOrEmpty(word)) return false;
            // "/de s7t look=…" style: the key before '=' is the selector
            string w = word.Trim();
            int eq = w.IndexOf('=');
            if (eq > 0) w = w.Substring(0, eq);
            return Contains(allowed, w);
        }

        private static bool Contains(string[] set, string word)
        {
            if (word == null) return false;
            foreach (string s in set)
                if (s.Equals(word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
