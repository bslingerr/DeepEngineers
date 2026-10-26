using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S2 dev commands (C11 and the <c>/de</c> chat relay; dev builds only, like every C11 command):
 /// <list type="bullet">
 /// <item><c>de.display {mode: f|aprime, toggle, seeThrough: on|off, margins: on|off|auto, skip: on|off|auto,
 /// alphaFill: on|off, autoFallback: on|off, readback: once|on|off, probe: true [expect: [r, g, b], tolerance],
 /// state}</c>: several keys may be combined; the reply carries the display state (C5 flags, walker, render
 /// counters, the probe verdict).</item>
 /// <item><c>de.view {force: space|track|prime|underwater|surfacing|auto}</c> (no argument: the state).</item>
 /// <item><c>de.audio {dump}</c>: the mix, the gate, the volumes, the Water Mod path.</item>
 /// <item><c>de.zorder {reassert | state}</c>: one z-order re-assert (verified by the walker on the next frames).</item>
 /// </list>
 /// Update thread. Window work happens later on SE's render thread; the replies say so.
 /// </summary>
    internal static class S2Commands
    {
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            switch (cmd)
            {
                case "de.display":
                case "de.view":
                case "de.audio":
                case "de.zorder":
                    break;
                default:
                    return null;
            }
            if (!S2Host.Enabled || S2Host.Controller == null) return Fail(cmd + ": S2 is off (config s2=false, DEEPENGINEERS_S2=0, or Init failed)");
            try
            {
                switch (cmd)
                {
                    case "de.display": return Display(args, via);
                    case "de.view": return View(args, via);
                    case "de.audio": return Audio();
                    default: return ZOrder(args, via);
                }
            }
            catch (Exception e) { return Fail(cmd + ": " + e.GetType().Name + ": " + e.Message); }
        }

        private static Dictionary<string, object> Display(IDictionary<string, object> args, string via)
        {
            DisplayController c = S2Host.Controller;
            var done = new List<string>();
            string mode = MiniJson.GetString(args, "mode");
            if (mode != null)
            {
                DisplayDesign d;
                string note;
                if (!S2Config.TryParseDisplay(mode, out d, out note)) return Fail("de.display mode: " + note);
                c.SetMode(d, "de.display via " + via);
                done.Add("mode " + S2Config.Name(d));
            }
            bool present, ok;
            bool? margins = S2Args.Switch(args, "margins", out present, out ok);
            if (!ok) return Fail("de.display margins: on|off|auto");
            if (present) { c.ForcedMargins = margins; done.Add("margins " + Word(margins)); DeLog.Info("DE.Display margins forced " + Word(margins) + " via " + via); }
            bool? skip = S2Args.Switch(args, "skip", out present, out ok);
            if (!ok) return Fail("de.display skip: on|off|auto");
            if (present) { c.ForcedSkip = skip; done.Add("skip " + Word(skip)); DeLog.Info("DE.Display scene skip forced " + Word(skip) + " via " + via); }
            bool? fill = S2Args.Switch(args, "alphaFill", out present, out ok);
            if (!ok || (present && !fill.HasValue)) return Fail("de.display alphaFill: on|off");
            if (present) { c.AlphaFill = fill.Value; done.Add("alphaFill " + Word(fill)); DeLog.Info("DE.Display alphaFill " + Word(fill) + " via " + via); }
            // review fix (finding 1): the scripted sessions switch the automatic A' fallback off and judge the
 // probe verdict in the oracle table instead
            bool? fallback = S2Args.Switch(args, "autoFallback", out present, out ok);
            if (!ok || (present && !fallback.HasValue)) return Fail("de.display autoFallback: on|off");
            if (present)
            {
                c.AutoFallback = fallback.Value;
                done.Add("autoFallback " + Word(fallback));
                DeLog.Info("DE.Display autoFallback " + Word(fallback) + " via " + via + " (A' after " + DisplayController.AutoFallbackAfterProbes +
                           " NOT HONOURED probes in a row when on)");
            }
            if (MiniJson.GetBool(args, "toggle") == true)
            {
                double ms = c.ToggleSeeThrough("de.display via " + via);
                done.Add("toggle #" + c.Toggles + " see-through " + (c.ForcedSeeThrough ? "on" : "off") + " (" + ms.ToString("0.000", CultureInfo.InvariantCulture) + " ms)");
            }
            bool? see = S2Args.Switch(args, "seeThrough", out present, out ok);
            if (!ok || (present && !see.HasValue)) return Fail("de.display seeThrough: on|off");
            if (present) { c.SetSeeThrough(see.Value, "de.display via " + via); done.Add("see-through " + Word(see)); }
            string rb = MiniJson.GetString(args, "readback");
            if (rb != null)
            {
                switch (rb.Trim().ToLowerInvariant())
                {
                    case "once": case "true": c.RequestReadback(); done.Add("alpha readback requested (DE.Display alpha line follows)"); break;
                    case "on": c.ReadbackPeriodic = true; done.Add("alpha readback every 5 s"); break;
                    case "off": case "false": c.ReadbackPeriodic = false; done.Add("alpha readback off"); break;
                    default: return Fail("de.display readback: once|on|off");
                }
            }
            if (MiniJson.GetBool(args, "probe") == true)
            {
                var spec = new ProbeSpec();
                object e;
                if (args != null && args.TryGetValue("expect", out e) && e is List<object>)
                {
                    var l = (List<object>)e;
                    if (l.Count != 3 || !(l[0] is double) || !(l[1] is double) || !(l[2] is double)) return Fail("de.display probe: expect [r, g, b] (0-255)");
                    spec.HasExpected = true;
                    spec.R = Byte((double)l[0]); spec.G = Byte((double)l[1]); spec.B = Byte((double)l[2]);
                }
                double? tol = MiniJson.GetNumber(args, "tolerance");
                if (tol.HasValue) spec.Tolerance = (int)tol.Value;
                object pts;
                if (args != null && args.TryGetValue("points", out pts) && pts is List<object>)
                {
                    // [[x, y], …] screen points (default: five points of SE's rectangle)
                    var list = (List<object>)pts;
                    var xs = new int[list.Count];
                    var ys = new int[list.Count];
                    for (int k = 0; k < list.Count; k++)
                    {
                        var p = list[k] as List<object>;
                        if (p == null || p.Count != 2 || !(p[0] is double) || !(p[1] is double)) return Fail("de.display probe: points [[x, y], ...]");
                        xs[k] = (int)(double)p[0];
                        ys[k] = (int)(double)p[1];
                    }
                    spec.Xs = xs;
                    spec.Ys = ys;
                }
                c.RequestProbe(spec);
                done.Add("composed-pixel probe requested" + (spec.HasExpected ? " (expect " + spec.R + "/" + spec.G + "/" + spec.B + ")" : "") + " (DE.Display probe line follows)");
            }
            Dictionary<string, object> r = Ok(done.Count > 0 ? "de.display: " + string.Join("; ", done.ToArray()) : "de.display state");
            r["state"] = c.Describe();
            r["c5"] = C5Flags();
            return r;
        }

        private static Dictionary<string, object> View(IDictionary<string, object> args, string via)
        {
            ViewStateDriver d = S2Host.Driver;
            string force = MiniJson.GetString(args, "force");
            bool present, ok;
            bool? trace = S2Args.Switch(args, "trace", out present, out ok);
            if (!ok || (present && !trace.HasValue)) return Fail("de.view trace: on|off");
            if (present) { d.TraceAll = trace.Value; DeLog.Info("DE.View trace every step " + (trace.Value ? "ON" : "OFF") + " via " + via); }
            string line;
            if (force != null)
            {
                S2State? s;
                if (!S2Args.TryParseForce(force, out s)) return Fail("de.view force: space|track|prime|underwater|surfacing|auto");
                d.Forced = s;
                line = "DE.View force " + (s.HasValue ? ViewStateAdapter.Name(s.Value) : "auto (the state machine decides again)") + " via " + via;
                DeLog.Info(line);
            }
            else line = "de.view state";
            Dictionary<string, object> r = Ok(line);
            var st = new Dictionary<string, object>(StringComparer.Ordinal);
            S2Host.AddState(st);
            st["vy"] = Math.Round(d.Vy, 3);
            st["rhoInf"] = Math.Round(d.RhoInf, 3);
            st["tick"] = d.Tick;
            st["transitions"] = d.Transitions;
            Wire.ViewMachineState ms = d.MachineState;
            st["resettlePending"] = ms.ResettlePending;
            st["resettleTick"] = ms.ResettleTick;
            st["renderSeq"] = ms.RenderSeq;
            st["request"] = ms.Request;
            st["marginsApplied"] = ms.MarginsApplied;
            st["renderAck"] = d.LastInputs.RenderAckSeq;
            st["traceRows"] = d.TraceRows;
            st["traceAll"] = d.TraceAll;
            st["depthChecks"] = d.DepthChecks;
            st["depthWorstM"] = Math.Round(d.DepthWorstM, 4);
            st["c5"] = C5Flags();
            r["state"] = st;
            return r;
        }

        private static Dictionary<string, object> Audio()
        {
            Dictionary<string, object> r = Ok("de.audio dump");
            r["audio"] = S2Host.Audio.Describe();
            r["waterMod"] = WaterModAudio.Describe();
            r["guiScreenOpen"] = S2Host.GuiScreenOpen;
            return r;
        }

        private static Dictionary<string, object> ZOrder(IDictionary<string, object> args, string via)
        {
            DisplayController c = S2Host.Controller;
            bool state = MiniJson.GetBool(args, "state") == true;
            Dictionary<string, object> r;
            if (!state)
            {
                c.RequestZOrder("de.zorder via " + via);
                r = Ok("de.zorder: re-assert requested (#" + c.ZRequests + "); the render thread runs it after its next present and verifies with the walker (DE.ZOrder lines)");
            }
            else r = Ok("de.zorder state");
            DisplayFeedback fb = c.Render.Feedback;
            r["zOrderOk"] = fb.ZOrderOk;
            r["pending"] = fb.ZPending;
            r["attempts"] = fb.ZAttempts;
            r["walker"] = fb.LastWalk ?? "not run";
            r["walks"] = fb.Walks;
            return r;
        }

        private static Dictionary<string, object> C5Flags()
        {
            DisplayFeedback fb = S2Host.Controller.Render.Feedback;
            AudioDecision a = S2Host.Audio.Last;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "seeThrough", fb.LastPresentedSkipped || fb.Lowered },
                { "marginsOn", fb.MarginsOn },
                { "zOrderOk", fb.ZOrderOk },
                { "seMixActive", a.MixActive },
                { "guiScreenOpen", S2Host.GuiScreenOpen },
            };
        }

        private static byte Byte(double v) { return (byte)Math.Max(0, Math.Min(255, Math.Round(v))); }

        private static string Word(bool? b) { return b.HasValue ? (b.Value ? "on" : "off") : "auto"; }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
