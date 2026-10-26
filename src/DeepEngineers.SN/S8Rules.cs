using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SN
{
    /// <summary>

 /// an SE that stalls or exits, and for the Alt+Tab / ESC restore check (CONTRACT §7 "SE exits normally", "SE crashes",
 /// "SE hangs"; ARCHITECTURE §5.14 "Subnautica's side"). Unity-free (SN.Tests S8Tests); the glue is
 /// <see cref="BridgeBehaviour"/> (quit sequencing, <c>de.stallSn</c>) and <see cref="S8Host"/> (rows, the audit, memory).
 /// <para>The reaction table (SE peer state → what Subnautica does):</para>
 /// <list type="table">
 /// <item><term>Alive</term><description>follows C5 (recipe, freeze causes, mix): no S8 reaction.</description></item>
 /// <item><term>Stalled (heartbeat ≥ 3 s; 120 s while either side loads)</term><description>mute, freeze, Idle; never
 /// quit, never minimise; resume when the heartbeat is fresh again and answer SE's next link epoch.</description></item>
 /// <item><term>Exited (process exit, <c>sePhase = Exiting</c>, C5 <c>seExiting</c>) of an SE seen alive</term><description>
 /// mute, freeze, minimise without activation (<c>SW_SHOWMINNOACTIVE</c>), quit without saving through the game's own
 /// path at once, <c>Application.Quit</c> as the fallback after <see cref="QuitFallbackAfterSeExitMs"/>.</description></item>
 /// <item><term>Stale, Detached, None (no live SE, or a dead block left in the section)</term><description>Idle and muted
 /// (no live C5); never quit: only an SE first seen alive can make Subnautica quit.</description></item>
 /// </list>
 /// </summary>
    internal static class S8Rules
    {
        /// <summary>CONTRACT §7: Subnautica quits within 10 s of SE's exit; ROADMAP S8.T-L1c/e: its process gone within 15 s.</summary>
        public const ulong QuitBudgetMs = 10000;
        /// <summary>
 /// SE exited: the game's own quit (<c>IngameMenu.QuitGameAsync(true)</c>) first waits for the world to settle
 /// so
 /// <c>Application.Quit</c> follows after 5 s if the process still runs: quitting within 10 s, gone within 15 s.
 /// </summary>
        public const ulong QuitFallbackAfterSeExitMs = 5000;
        /// <summary>Other quits (<c>de.quit</c>, Subnautica's own): the S0 retry after 10 s.</summary>
        public const ulong QuitFallbackMs = 10000;
        /// <summary><c>de.stallSn</c> blocks the main thread at most this long (dev flavour only).</summary>
        public const double StallMaxSeconds = 60.0;
        /// <summary>The Alt+Tab / ESC restore check runs this long after an edge (≥ the 300 ms crossfade and one per-second window check).</summary>
        public const ulong AuditDelayMs = 1500;

        /// <summary>What Subnautica does for one SE peer state.</summary>
        internal struct Reaction
        {
            public bool Mute, Freeze, Idle, Minimise, Quit;
            public string Why;
        }

        /// <summary>The reaction table above.</summary>
        public static Reaction React(SePeerState s)
        {
            switch (s)
            {
                case SePeerState.Alive:
                    return new Reaction { Why = "SE alive: C5 rules" };
                case SePeerState.Stalled:
                    return new Reaction { Mute = true, Freeze = true, Idle = true, Why = "SE stalled: mute, freeze, Idle; never quit on heartbeat loss" };
                case SePeerState.Exited:
                    return new Reaction { Mute = true, Freeze = true, Idle = true, Minimise = true, Quit = true, Why = "SE exited: mute, freeze, minimise without activation, quit without saving" };
                case SePeerState.Stale:
                    return new Reaction { Mute = true, Idle = true, Why = "SE block left by an SE that is gone: Idle, muted; never quit" };
                case SePeerState.Detached:
                    return new Reaction { Mute = true, Idle = true, Why = "SE detached without an exit: Idle, muted; never quit" };
                default:
                    return new Reaction { Mute = true, Idle = true, Why = "no SE yet: Idle, muted" };
            }
        }

        /// <summary>Only an exit of an SE seen alive quits Subnautica (never a stall, a stale block or a detach).</summary>
        public static bool QuitOn(SePeerEvent e)
        {
            return e == SePeerEvent.Exited;
        }

        internal enum QuitPath : byte { None = 0, MainMenuButton = 1, IngameQuitAsync = 2, ApplicationQuit = 3 }

        /// <summary>
 /// The game's own quit path, never saving: at the main menu <c>uGUI_MainMenu.OnButtonQuit</c>; in
 /// game <c>IngameMenu.QuitGameAsync(quitToDesktop: true)</c>, which saves only in permadeath, so a
 /// permadeath game (never the Freedom host save) and any other case take <c>Application.Quit</c>.
 /// </summary>
        public static QuitPath ChooseQuitPath(bool mainMenu, bool ingameMenu, bool permadeath)
        {
            if (mainMenu) return QuitPath.MainMenuButton;
            if (ingameMenu && !permadeath) return QuitPath.IngameQuitAsync;
            return QuitPath.ApplicationQuit;
        }

        public static string Name(QuitPath p)
        {
            switch (p)
            {
                case QuitPath.MainMenuButton: return "uGUI_MainMenu.OnButtonQuit";
                case QuitPath.IngameQuitAsync: return "IngameMenu.QuitGameAsync(true)";
                case QuitPath.ApplicationQuit: return "Application.Quit";
                default: return "none";
            }
        }

        /// <summary>The <c>Application.Quit</c> fallback after the quit was issued: 5 s when SE exited, 10 s otherwise.</summary>
        public static ulong FallbackAfterMs(bool seExited)
        {
            return seExited ? QuitFallbackAfterSeExitMs : QuitFallbackMs;
        }

        internal enum QuitAction : byte { Wait = 0, Issue = 1, Fallback = 2 }

        /// <summary>
 /// One frame of the quit sequence (CONTRACT §7): the game's quit is issued once SE acknowledged (C5 PeerExited) or the
 /// ≤ 100 ms wait ended (no wait when SE is gone); the fallback once, <see cref="FallbackAfterMs"/> after the issue.
 /// </summary>
        public static QuitAction QuitStep(ulong nowMs, ulong issueAtMs, bool acked, bool issued, ulong issuedAtMs, bool fallbackDone, bool seExited)
        {
            if (!issued) return acked || nowMs >= issueAtMs ? QuitAction.Issue : QuitAction.Wait;
            if (!fallbackDone && nowMs >= issuedAtMs + FallbackAfterMs(seExited)) return QuitAction.Fallback;
            return QuitAction.Wait;
        }

        // ================================================================================================ de.stallSn

 /// <summary>
 /// <c>de.stallSn</c> arguments (ROADMAP S8 SN items; S8.T-L1b): <c>{"seconds": s}</c>, <c>[s]</c> or a bare number,
 /// 0 &lt; s ≤ <see cref="StallMaxSeconds"/>.
 /// </summary>
        public static bool ParseStallSeconds(object args, out double seconds, out string error)
        {
            seconds = 0; error = null;
            object v = args;
            var d = args as Dictionary<string, object>;
            var l = args as List<object>;
            if (d != null && !d.TryGetValue("seconds", out v)) v = null;
            else if (l != null) v = l.Count == 1 ? l[0] : null;
            double s;
            if (v is double) s = (double)v;
            else if (v is long) s = (long)v;
            else if (v is int) s = (int)v;
            else if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out s)) { }
            else { error = "args need {\"seconds\": s} with 0 < s <= " + StallMaxSeconds.ToString(CultureInfo.InvariantCulture); return false; }
            if (double.IsNaN(s) || s <= 0 || s > StallMaxSeconds) { error = "seconds must be in (0, " + StallMaxSeconds.ToString(CultureInfo.InvariantCulture) + "]"; return false; }
            seconds = s;
            return true;
        }

        // ======================================================================================= Alt+Tab / ESC audit

 /// <summary>The facts the restore check compares (S8.N4: "state restored each time; audio rules hold").</summary>
        internal struct AuditInputs
        {
            /// <summary>C5 of a live SE is in force (the S2 host's Valid).</summary>
            public bool Valid;
            public bool SeForeground;
            public bool Quitting;
            /// <summary>C5 snBusTarget (&gt; 0 under water).</summary>
            public float BusTarget;
            public bool FreezeWanted, Frozen;
            public bool Muted;
            /// <summary>The window role is on and was applied at ReadyIdle (styles then must hold).</summary>
            public bool WindowApplied;
            public bool StylesOk;
        }

        /// <summary>

 /// state equals its wanted state; muted whenever SE is not alive, not foreground or Subnautica quits; audible when SE
 /// is alive, foreground and the bus target is above 0 (under water); the window styles hold once applied.
 /// </summary>
        public static string AuditFailures(ref AuditInputs a)
        {
            List<string> f = null;
            if (a.Frozen != a.FreezeWanted) Add(ref f, a.FreezeWanted ? "not frozen (freeze wanted)" : "frozen (no freeze wanted)");
            bool mustMute = a.Quitting || !a.Valid || !a.SeForeground;
            if (mustMute && !a.Muted) Add(ref f, "audible (" + (a.Quitting ? "quitting" : !a.Valid ? "no live SE" : "SE not foreground") + ")");
            if (!mustMute && a.BusTarget > 0f && a.Muted) Add(ref f, "muted under water with SE foreground");
            if (a.WindowApplied && !a.StylesOk) Add(ref f, "window styles missing");
            return f == null ? null : string.Join(",", f.ToArray());
        }

        private static void Add(ref List<string> f, string s)
        {
            if (f == null) f = new List<string>();
            f.Add(s);
        }

        /// <summary>
 /// The edge key the audit watches: SE's foreground and pause (C5 flags and overlays) and the peer state. A change of
 /// the key starts an audit <see cref="AuditDelayMs"/> later.
 /// </summary>
        public static uint EdgeKey(bool valid, uint flags, uint overlays, SePeerState peer)
        {
            uint k = (uint)peer << 8;
            if (!valid) return k | 0x80u;
            bool fg = (flags & Wire.SeStateFlags.SeForeground) != 0 && (overlays & Wire.Overlays.Unfocused) == 0;
            bool paused = (flags & Wire.SeStateFlags.SePaused) != 0 || (overlays & Wire.Overlays.Paused) != 0;
            return k | (fg ? 1u : 0u) | (paused ? 2u : 0u);
        }

        /// <summary>A readable edge: "focus off", "pause on", "peer Alive->Stalled" … (the s8.restore row).</summary>
        public static string DescribeEdge(uint from, uint to)
        {
            var parts = new List<string>();
            if (((from ^ to) & 0x80u) != 0) parts.Add((to & 0x80u) != 0 ? "C5 lost" : "C5 back");
            if (((from ^ to) & 1u) != 0) parts.Add((to & 1u) != 0 ? "focus on" : "focus off");
            if (((from ^ to) & 2u) != 0) parts.Add((to & 2u) != 0 ? "pause on" : "pause off");
            if ((from >> 8) != (to >> 8)) parts.Add("peer " + SePeerMonitor.Name((SePeerState)(from >> 8)) + "->" + SePeerMonitor.Name((SePeerState)(to >> 8)));
            return parts.Count == 0 ? "none" : string.Join(",", parts.ToArray());
        }

        // =================================================================================================== memory

 /// <summary>The S8.N3 memory part of the per-second <c>DE.S2 sec</c> line (MB rounded to 0.1).</summary>
        public static string MemoryText(long workingSetBytes, long privateBytes, long monoHeapBytes, int gc0, int gc1, int gc2, uint memoryLoadPct, double commitPct)
        {
            var ci = CultureInfo.InvariantCulture;
            return "mem{ws=" + Mb(workingSetBytes).ToString("0.0", ci) + "MB priv=" + Mb(privateBytes).ToString("0.0", ci) + "MB mono=" +
                   Mb(monoHeapBytes).ToString("0.0", ci) + "MB gc=" + gc0.ToString(ci) + "/" + gc1.ToString(ci) + "/" + gc2.ToString(ci) +
                   " load=" + memoryLoadPct.ToString(ci) + "% commit=" + (double.IsNaN(commitPct) ? "?" : commitPct.ToString("0.0", ci) + "%") + "}";
        }

        public static double Mb(long bytes)
        {
            return bytes <= 0 ? 0.0 : Math.Round(bytes / (1024.0 * 1024.0), 1);
        }

        /// <summary>Commit charge in percent of the commit limit (NaN when unknown).</summary>
        public static double CommitPct(ulong totalPageFile, ulong availPageFile)
        {
            if (totalPageFile == 0 || availPageFile > totalPageFile) return double.NaN;
            return Math.Round(100.0 * (totalPageFile - availPageFile) / totalPageFile, 1);
        }
    }
}
