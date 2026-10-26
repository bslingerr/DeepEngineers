using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// The "vsm" trace rows (<see cref="ViewTrace"/>), the allocation-free claim of <see cref="ViewStateMachine.Step"/>
 /// and the cross-check against the independent Python oracle <c>tests/s2/state_traces.py</c>.
 /// </summary>
    [TestClass]
    public class ViewTraceTests
    {
        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        // ------------------------------------------------------------------------------------------- a flat JSON reader
        private static Dictionary<string, string> ParseFlat(string json)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            int p = 0;
            Func<char, bool> skip = c => { while (p < json.Length && char.IsWhiteSpace(json[p])) p++; if (p < json.Length && json[p] == c) { p++; return true; } return false; };
            Func<string> str = () =>
            {
                if (!skip('"')) throw new FormatException("string expected at " + p);
                int s = p;
                while (json[p] != '"') { if (json[p] == '\\') throw new FormatException("escape"); p++; }
                return json.Substring(s, p++ - s);
            };
            if (!skip('{')) throw new FormatException("{ expected");
            while (true)
            {
                string key = str();
                if (!skip(':')) throw new FormatException(": expected after " + key);
                while (char.IsWhiteSpace(json[p])) p++;
                string val;
                if (json[p] == '"') val = "\"" + str() + "\"";
                else
                {
                    int s = p;
                    while (p < json.Length && json[p] != ',' && json[p] != '}') p++;
                    val = json.Substring(s, p - s).Trim();
                }
                if (d.ContainsKey(key)) throw new FormatException("duplicate key " + key);
                d[key] = val;
                if (skip(',')) continue;
                if (skip('}')) break;
                throw new FormatException(", or } expected at " + p);
            }
            if (p != json.Length) throw new FormatException("trailing text");
            return d;
        }

        private static double FromBits(string quotedHex)
        {
            ulong bits = ulong.Parse(quotedHex.Trim('"'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        [TestMethod]
        public void TraceRow_HasEveryFieldAndExactBits()
        {
            var sim = new ViewSim { TraceRows = true };
            sim.Hold(5, 0.123456789012345);
            sim.Step(-0.2000000000000001);
            Dictionary<string, string> r = ParseFlat(sim.Rows.Last());
            string[] keys =
            {
                "ev", "v", "n", "tick", "us", "bu", "site", "zone", "rho", "rhob", "y", "yb", "vy", "vyb", "peer", "ph", "snf",
                "pst", "abt", "zok", "pau", "fg", "gui", "cur", "ld", "kau", "evt", "ack", "mrd", "dm", "mut",
                "s_v", "s_a", "s_sw", "s_ls", "s_pc", "s_bw", "s_xt", "s_rp", "s_rt", "s_rs", "s_rq", "s_ma", "s_pf", "s_pz",
                "s_mf", "s_ms", "s_mu",
                "o_vs", "o_am", "o_pm", "o_fps", "o_rq", "o_rs", "o_zr", "o_fz", "o_xt", "o_rdy", "o_blk", "o_ov", "o_c5f",
                "o_bus", "o_xf", "o_sg", "o_sm", "o_wm", "o_aw", "o_ma", "o_mix", "o_rsn",
                // vsm/4 (S7a picture policy)
                "pp", "s_sp", "s_bt", "s_ps", "s_pt", "o_sp", "o_pe", "o_pb", "o_prs", "o_pp",
                "be", "bx", "s_la", "s_lt",   // vsm/5: the band and the altitude-exit dwell
            };
            CollectionAssert.AreEquivalent(keys, r.Keys.ToArray(), "the documented vsm/4 keys (the vsm/3 keys plus the picture), nothing else");
            Assert.AreEqual("\"vsm\"", r["ev"]);
            Assert.AreEqual("5", r["v"], "vsm/5: the band and the exit dwell (2026-10-17; vsm/4 was the S7a picture policy)");
            Assert.AreEqual(5, ViewTrace.Version);   // 5: s_la/s_lt
            Assert.AreEqual("0", r["pp"], "the default input: Site");
            Assert.AreEqual("0", r["o_pp"]);
            Assert.AreEqual(sim.Log.Last().O.PictureEdge.ToString(CultureInfo.InvariantCulture), r["o_pe"]);
            Assert.AreEqual("1", r["mrd"], "ViewSim reports the margins applied by default");
            Assert.AreEqual("5", r["n"], "step index before the step");
            Assert.AreEqual("1005", r["tick"]);
            Assert.AreEqual((1005L * ViewSim.TickUs).ToString(CultureInfo.InvariantCulture), r["us"]);
            Assert.AreEqual(-0.2000000000000001, FromBits(r["yb"]), "y bits exact");
            Assert.AreEqual("-0.2000", r["y"]);
            Assert.AreEqual(SiteZones.RhoInf(100.0, 100.0), FromBits(r["rhob"]));
            Assert.AreEqual("100.00", r["rho"]);
            Assert.AreEqual(sim.Log.Last().O.CrossingTick.ToString(CultureInfo.InvariantCulture), r["o_xt"]);
            Assert.AreEqual("300.0", r["o_xf"]);
            Assert.AreEqual("1.000000", r["o_sg"]);
            Assert.AreEqual(0.0, FromBits(r["s_mf"]));
        }

        [TestMethod]
        public void TraceRow_ExtremeValues_FitAndOverflowIsReported()
        {
            var pre = new ViewMachineState
            {
                Steps = ulong.MaxValue, LastSwitchUs = long.MinValue, PrimeCondUs = long.MaxValue, CrossingTick = ulong.MaxValue,
                ResettleTick = ulong.MaxValue, RenderSeq = uint.MaxValue, Request = uint.MaxValue, MixFrom = double.NaN, MixStartUs = long.MinValue,
            };
            var i = new ViewInputs
            {
                Tick = ulong.MaxValue, NowUs = long.MinValue, Zone = uint.MaxValue, RhoInfM = double.PositiveInfinity, Y = double.NaN,
                Vy = -1e300, Peer = uint.MaxValue, SnPhase = uint.MaxValue, SnFlags = uint.MaxValue, PresentedSeTick = ulong.MaxValue,
                AreaBuiltSeTick = ulong.MaxValue, Events = uint.MaxValue, RenderAckSeq = uint.MaxValue, DisplayMode = uint.MaxValue,
                Mutants = uint.MaxValue,
            };
            var o = new ViewOutputs
            {
                ViewState = uint.MaxValue, Blocked = uint.MaxValue, SnBusTarget = float.NaN, SeGameFactor = float.MaxValue,
                SeMusicFactor = -float.Epsilon, MixLevel = double.NegativeInfinity, CrossingTick = ulong.MaxValue,
            };
            string row = ViewTrace.FormatString(ref pre, ref i, ref o);
            Assert.IsNotNull(row);
            Assert.IsTrue(row.Length < ViewTrace.MaxRowChars, "row length " + row.Length);
            Dictionary<string, string> r = ParseFlat(row);
            Assert.AreEqual("null", r["y"]);
            Assert.IsTrue(double.IsNaN(FromBits(r["yb"])));
            Assert.AreEqual("null", r["rho"]);
            Assert.AreEqual("null", r["vy"], "too large for fixed point");
            Assert.AreEqual(-1e300, FromBits(r["vyb"]));
            Assert.AreEqual("-9223372036854775808", r["us"]);
            Assert.AreEqual("18446744073709551615", r["tick"]);
            Assert.AreEqual("0.000000", r["o_sm"], "a negative zero rounds to 0 without a sign");
            var pre2 = pre; pre2.BandTick = ulong.MaxValue; pre2.PicSwitchUs = long.MinValue;
            var o2 = o; o2.PictureEdge = int.MinValue; o2.PictureBlocked = uint.MaxValue;
            string row2 = ViewTrace.FormatString(ref pre2, ref i, ref o2);
            Assert.IsNotNull(row2);
            Assert.IsTrue(row2.Length < ViewTrace.MaxRowChars, "v4 extremes fit: " + row2.Length);
            Dictionary<string, string> r2 = ParseFlat(row2);
            Assert.AreEqual("-2147483648", r2["o_pe"]);
            Assert.AreEqual("-9223372036854775808", r2["s_pt"]);

            var small = new char[100];
            Assert.AreEqual(-1, ViewTrace.Format(ref pre, ref i, ref o, small), "a short buffer reports -1");
        }

        // ------------------------------------------------------------------------------------------- allocation
        [TestMethod]
        public void Step_AndTraceFormat_AllocateNothing()
        {
            try { AppDomain.MonitoringIsEnabled = true; }
            catch (Exception e) { Assert.Inconclusive("AppDomain monitoring unavailable: " + e.Message); }

            var s = default(ViewMachineState);
            var i = new ViewInputs
            {
                BridgeUp = true, OnSite = true, Zone = Zone.Core, RhoInfM = 100.0, Peer = PeerState.Alive, SnPhase = SnPhase.Active,
                SnFlags = SnStatusFlags.AreaBuilt | SnStatusFlags.StreamingSettled | SnStatusFlags.RectOk | SnStatusFlags.UiHidden,
                ZOrderOk = true, Foreground = true, DisplayMode = DisplayMode.F, MarginsReady = true,
            };
            char[] buf = new char[ViewTrace.MaxRowChars];
            int switches = 0;
            Action<int> loop = n =>
            {
                for (int k = 0; k < n; k++)
                {
                    i.Tick++;
                    i.NowUs += 16667;
                    i.Y = 3.0 * Math.Sin(i.Tick * 0.01) - 0.5;
                    i.Vy = 0.03 * Math.Cos(i.Tick * 0.01) * 60.0;
                    i.PresentedSeTick = i.Tick - 1;
                    i.AreaBuiltSeTick = i.Tick - 1;
                    i.RenderAckSeq = s.RenderSeq;
                    i.Paused = (i.Tick / 997) % 7 == 0;
                    i.Foreground = (i.Tick / 1999) % 5 != 0;
                    ViewMachineState pre = s;
                    ViewOutputs o;
                    ViewStateMachine.Step(ref s, ref i, out o);
                    if (o.ViewState != pre.View) switches++;
                    if ((k & 3) == 0 && ViewTrace.Format(ref pre, ref i, ref o, buf) < 0) throw new InvalidOperationException("overflow");
                }
            };
            loop(5000); // JIT and static initialisers
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            loop(200000);
            long after = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            Assert.IsTrue(switches > 100, "the loop exercised the transitions (" + switches + ")");
            Console.WriteLine("200,000 steps + 50,000 rows allocated " + (after - before) + " bytes (whole AppDomain)");
            Assert.IsTrue(after - before < 512 * 1024, "allocated " + (after - before) + " bytes; one object per step would be >= 4.8 MB");
        }

        // ------------------------------------------------------------------------------------------- Python oracle
        private static string RepoRoot()
        {
            string dir = Path.GetDirectoryName(typeof(ViewTraceTests).Assembly.Location);
            while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PythonExe()
        {
            string env = Environment.GetEnvironmentVariable("DEEPENG_PYTHON");
            if (!string.IsNullOrEmpty(env)) return env;
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                string p = Path.Combine(local, "Programs", "Python", "Python310", "python.exe");
                if (File.Exists(p)) return p;
            }
            return "python";
        }

        /// <summary>Runs <c>python -I tests/s2/state_traces.py args</c>; returns the exit code (stdout in <paramref name="output"/>), or −1 when Python cannot start.</summary>
        private static int RunOracle(string root, out string output, params string[] args)
        {
            var sb = new StringBuilder("-I ");
            sb.Append(Quote(Path.Combine(root, "tests", "s2", "state_traces.py")));
            foreach (string a in args) sb.Append(' ').Append(Quote(a));
            var psi = new ProcessStartInfo(PythonExe(), sb.ToString())
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception e) { output = e.Message; return -1; }
            using (p)
            {
                var err = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (InvalidOperationException) { } output = "timeout"; return -1; }
                output = stdout + err.Result;
                return p.ExitCode;
            }
        }

        private static string Quote(string a)
        {
            return a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;
        }

        private static string WriteRows(string root, string name, IEnumerable<string> rows)
        {
            string dir = Path.Combine(root, "tests", "out", "s2");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "cs-vsm-" + name + "-" + Process.GetCurrentProcess().Id + ".jsonl");
            File.WriteAllText(path, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
            return path;
        }

        private static string Keep(List<string> written, string path)
        {
            written.Add(path);
            return path;
        }

        private static ViewSim Traced(Action<ViewSim> script, uint mutants = 0, uint display = DisplayMode.F, uint picture = ViewPicture.Site)
        {
            var sim = new ViewSim { TraceRows = true, Mutants = mutants, DisplayMode = display, Picture = picture };
            script(sim);
            return sim;
        }

        /// <summary>S7a band legs: descents +600 → +200 m and ascents back, <paramref name="ticks"/> per leg.</summary>
        private static void Band(ViewSim s, int cycles, int ticks)
        {
            s.Hold(60, 600.0);
            for (int k = 0; k < cycles; k++) { s.Ramp(ticks, 600.0, 200.0); s.Hold(120, 200.0); s.Ramp(ticks, 200.0, 600.0); s.Hold(120, 600.0); }
        }

        private static IEnumerable<KeyValuePair<string, ViewSim>> Scenarios()
        {
            yield return new KeyValuePair<string, ViewSim>("dive", Traced(s =>
            {
                s.Hold(60, 400.0); s.Ramp(900, 400.0, -50.0); s.Hold(120, -50.0); s.Ramp(900, -50.0, 400.0);
            }));
            yield return new KeyValuePair<string, ViewSim>("zone", Traced(s =>
            {
                s.Hold(120, -20.0);
                for (int k = 0; k < 600; k++) { s.X = 7700.0 + k * 0.5; s.Step(-20.0); }
                for (int k = 0; k < 600; k++) { s.X = 8000.0 - k * 0.5; s.Step(-20.0); }
            }));
            yield return new KeyValuePair<string, ViewSim>("peer", Traced(s =>
            {
                s.Hold(120, -20.0); s.Peer = PeerState.Stalled; s.Hold(60, -20.0); s.Peer = PeerState.Alive;
                s.Event(ViewEvents.LinkRecovery); s.SettleAt = s.Tick + 60; s.Hold(180, -20.0);
                s.Peer = PeerState.Exited; s.Hold(60, -20.0); s.Peer = PeerState.Unknown; s.Hold(30, -20.0);
                s.Peer = PeerState.Alive; s.Event(ViewEvents.NewGeneration); s.SettleAt = s.Tick + 30; s.Hold(90, -20.0);
            }));
            yield return new KeyValuePair<string, ViewSim>("teleport", Traced(s =>
            {
                s.Hold(120, -20.0); s.Teleport(90); s.Hold(240, -40.0); s.Teleport(20); s.Hold(60, 30.0); s.Hold(60, -10.0);
            }));
            yield return new KeyValuePair<string, ViewSim>("focus", Traced(s =>
            {
                s.Hold(120, -20.0); s.Foreground = false; s.Hold(60, -20.0); s.Foreground = true; s.Hold(30, -20.0);
                s.Paused = true; s.Hold(60, -20.0); s.Paused = false; s.Gui = true; s.Hold(30, -20.0); s.Gui = false;
                s.Loading = true; s.Hold(10, -20.0); s.Loading = false; s.Foreground = false; s.KeepAudibleUnfocused = true; s.Hold(10, -20.0);
                s.KeepAudibleUnfocused = false; s.Foreground = true; s.ZOrderOk = false; s.Hold(10, -20.0); s.ZOrderOk = true;
                s.Event(ViewEvents.WorldLoad); s.Hold(60, -20.0);
            }));
            yield return new KeyValuePair<string, ViewSim>("aprime", Traced(s =>
            {
                s.Hold(120, -20.0); s.Gui = true; s.Hold(20, -20.0); s.Gui = false; s.CursorOrChat = true; s.Hold(20, -20.0);
                s.CursorOrChat = false; s.Hold(20, -20.0); s.Ramp(120, -20.0, 2.0); s.Hold(240, 2.0);
            }, 0, DisplayMode.APrime));
            yield return new KeyValuePair<string, ViewSim>("modec", Traced(s => s.Hold(120, -20.0), 0, DisplayMode.C));
            // re-dive fix: the Prime quiet timeout is 10 s below +5 m and 3 s from +5 m up (vsm/3)
            yield return new KeyValuePair<string, ViewSim>("redive", Traced(s =>
            {
                s.Hold(120, -3.0); s.Ramp(200, -3.0, 2.0); s.Hold(480, 2.0); s.Ramp(200, 2.0, -3.0); s.Hold(60, -3.0);
                s.Ramp(200, -3.0, 2.0); s.Hold(660, 2.0); s.Ramp(200, 2.0, -3.0); s.Hold(60, -3.0);
                s.Ramp(330, -3.0, 8.0); s.Hold(720, 8.0); s.Ramp(440, 8.0, -3.0); s.Hold(60, -3.0);
                s.Ramp(200, -3.0, 2.0); s.Hold(120, 2.0); s.Ramp(40, 2.0, 6.0); s.Hold(240, 6.0); s.Hold(60, 5.0);
            }));
            // S7a: the S2 policy (the fallback) through the oracle too, and the band swaps of the site policy
            yield return new KeyValuePair<string, ViewSim>("dive-waterline", Traced(s =>
            {
                s.Hold(60, 400.0); s.Ramp(900, 400.0, -50.0); s.Hold(120, -50.0); s.Ramp(900, -50.0, 400.0);
            }, 0, DisplayMode.F, ViewPicture.Waterline));
            yield return new KeyValuePair<string, ViewSim>("aprime-waterline", Traced(s =>
            {
                s.Hold(120, -20.0); s.Gui = true; s.Hold(20, -20.0); s.Gui = false; s.Ramp(120, -20.0, 2.0); s.Hold(240, 2.0);
            }, 0, DisplayMode.APrime, ViewPicture.Waterline));
            yield return new KeyValuePair<string, ViewSim>("band", Traced(s => Band(s, 3, 1200)));
            yield return new KeyValuePair<string, ViewSim>("band-fast", Traced(s => Band(s, 2, 240)));
            yield return new KeyValuePair<string, ViewSim>("band-aprime", Traced(s => Band(s, 2, 600), 0, DisplayMode.APrime));
            yield return new KeyValuePair<string, ViewSim>("band-events", Traced(s =>
            {
                s.Hold(120, 50.0); s.Peer = PeerState.Stalled; s.Hold(30, 50.0); s.Peer = PeerState.Alive; s.Event(ViewEvents.LinkRecovery);
                s.SettleAt = s.Tick + 40; s.Hold(90, 50.0); s.Teleport(60); s.Hold(120, 50.0); s.Ramp(300, 50.0, -10.0); s.Hold(60, -10.0);
                s.Picture = ViewPicture.Waterline; s.Hold(30, -10.0); s.Ramp(120, -10.0, 20.0); s.Picture = ViewPicture.Site; s.Hold(60, 20.0);
                s.Ramp(300, 20.0, 400.0); s.Hold(30, 400.0);
            }));
            yield return new KeyValuePair<string, ViewSim>("random", Traced(s =>
            {
                var rnd = new Random(1657);
                double y = 5.0, target = -1.0;
                for (int k = 0; k < 8000; k++)
                {
                    if (rnd.NextDouble() < 0.01) target = -1.5 + rnd.NextDouble() * 3.0;
                    if (rnd.NextDouble() < 0.002) s.Peer = s.Peer == PeerState.Alive ? PeerState.Stalled : PeerState.Alive;
                    if (rnd.NextDouble() < 0.002) s.ZOrderOk = !s.ZOrderOk;
                    if (rnd.NextDouble() < 0.002) s.Foreground = !s.Foreground;
                    if (rnd.NextDouble() < 0.001) s.Teleport(rnd.Next(10, 120));
                    if (rnd.NextDouble() < 0.003) { s.AckLag = 1 + rnd.Next(4); s.SnLag = 1 + rnd.Next(4); }
                    if (rnd.NextDouble() < (s.X > 7000 ? 0.01 : 0.0005)) s.X = s.X > 7000 ? 100.0 : 7950.0;
                    y += Math.Max(-0.05, Math.Min(0.05, target - y)) + (rnd.NextDouble() - 0.5) * 0.02;
                    s.Step(y);
                }
            }));
        }

        [TestMethod]
        public void PythonOracle_ReplaysTheCSharpMachine_AndCatchesS2M4()
        {
            string root = RepoRoot();
            Assert.IsNotNull(root, "repository root not found");
            string probe;
            int ver = RunOracle(root, out probe, "--help");
            if (ver != 0) Assert.Inconclusive("Python oracle not runnable (" + ver + "): " + probe);
            var written = new List<string>();
            try
            {
                foreach (KeyValuePair<string, ViewSim> sc in Scenarios())
                {
                    string path = Keep(written, WriteRows(root, sc.Key, sc.Value.Rows));
                    string output;
                    int code = RunOracle(root, out output, "replay", path);
                    Console.WriteLine(output);
                    Assert.AreEqual(0, code, "oracle replay of C# scenario '" + sc.Key + "':\n" + output);
                    StringAssert.Contains(output, "STATE TRACES PASS");
                    StringAssert.Contains(output, "localMismatch=0 linkMismatch=0");
                }

                // sparse rows (every third step): the oracle reseeds from the logged state after each gap
                ViewSim dive = Scenarios().First().Value;
                string sparse = Keep(written, WriteRows(root, "dive-sparse", dive.Rows.Where((r, k) => k % 3 == 0)));
                string so;
                Assert.AreEqual(0, RunOracle(root, out so, "replay", sparse), so);

                // re-dive fix: the oracle replays vsm/1 and vsm/2 rows with the 3 s Prime quiet timeout at every
 // altitude (the machine that wrote them), so the vsm/4 re-dive rows relabelled as version 2 must be red
                ViewSim redive = Scenarios().First(k => k.Key == "redive").Value;
                string asV2 = Keep(written, WriteRows(root, "redive-as-v2", redive.Rows.Select(r => r.Replace("\"v\":" + ViewTrace.Version + ",", "\"v\":2,"))));
                string ro;
                Assert.AreEqual(1, RunOracle(root, out ro, "replay", asV2), "vsm/4 re-dive rows read as vsm/2 must be red:\n" + ro);
                StringAssert.Contains(ro, "STATE TRACES FAIL");

                // S7a: vsm/3 rows replay as the Waterline policy: the Waterline machine's rows relabelled vsm/3 stay green,
 // the site machine's rows relabelled vsm/3 are red
                ViewSim wl = Scenarios().First(k => k.Key == "dive-waterline").Value;
                string wlV3 = Keep(written, WriteRows(root, "dive-waterline-as-v3", wl.Rows.Select(r => r.Replace("\"v\":" + ViewTrace.Version + ",", "\"v\":3,"))));
                string wo;
                Assert.AreEqual(0, RunOracle(root, out wo, "replay", wlV3), "Waterline rows as vsm/3:\n" + wo);
                ViewSim st = Scenarios().First(k => k.Key == "dive").Value;
                string stV3 = Keep(written, WriteRows(root, "dive-site-as-v3", st.Rows.Select(r => r.Replace("\"v\":" + ViewTrace.Version + ",", "\"v\":3,"))));
                Assert.AreEqual(1, RunOracle(root, out wo, "replay", stV3), "site rows as vsm/3 must be red:\n" + wo);

                // S7a.M1 judged by the oracle: the mutant machine's rows are red, and replay with the logged mutant flag
                var m1 = Traced(s => Band(s, 2, 600), ViewMutants.S7aM1Waterline);
                Assert.IsTrue(m1.Log.All(x => x.O.PicturePolicy == ViewPicture.Waterline), "S7a.M1: the machine ran Waterline");
                string m1p = Keep(written, WriteRows(root, "band-s7a-m1", m1.Rows));
                string mo;
                Assert.AreEqual(1, RunOracle(root, out mo, "replay", m1p), "S7a.M1 rows must be red:\n" + mo);
                Assert.AreEqual(0, RunOracle(root, out mo, "replay", m1p, "--honour-mutant"), "S7a.M1 with the logged mutant:\n" + mo);

                // S2.N2 traces judged by the oracle: green, then S2.M4 red
                var traces = new[]
                {
                    new { name = "n2a", c = "a", mut = 0u },
                    new { name = "n2b", c = "b", mut = 0u },
                    new { name = "n2a-m4", c = "a", mut = ViewMutants.M4NoHysteresis },
                    new { name = "n2b-m4", c = "b", mut = ViewMutants.M4NoHysteresis },
                };
                foreach (var t in traces)
                {
                    var sim = new ViewSim { TraceRows = true, Mutants = t.mut };
                    ulong from;
                    if (t.c == "a")
                    {
                        sim.Hold(60, 2.0); sim.Ramp(120, 2.0, -0.125); from = sim.Tick;
                        double p0 = sim.Tick / 60.0;
                        sim.Run(1800, x => -0.125 + 0.05 * Math.Sin(2.0 * Math.PI * 0.5 * (x - p0)));
                    }
                    else
                    {
                        sim.Hold(60, -1.0); from = sim.Tick;
                        double p0 = sim.Tick / 60.0;
                        sim.Run(1800, x => -0.125 + 0.1 * Math.Sin(2.0 * Math.PI * 5.0 * (x - p0)));
                    }
                    string path = Keep(written, WriteRows(root, t.name, sim.Rows));
                    string output;
                    int code = RunOracle(root, out output, "replay", path, "--case", t.c, "--from-tick", from.ToString(CultureInfo.InvariantCulture));
                    Console.WriteLine(output);
                    if (t.mut == 0)
                    {
                        Assert.AreEqual(0, code, t.name + ":\n" + output);
                    }
                    else
                    {
                        Assert.AreEqual(1, code, t.name + " must be red:\n" + output);
                        StringAssert.Matches(output, new System.Text.RegularExpressions.Regex(@"\(documented machine\)\s+FAIL"));
                        StringAssert.Contains(output, "STATE TRACES FAIL");
                        StringAssert.Contains(output, "FAIL  switches=");
                        string honour;
                        int hc = RunOracle(root, out honour, "replay", path, "--honour-mutant");
                        Assert.AreEqual(0, hc, t.name + " replayed with the logged mutant flag (the Python mutant equals the C# one):\n" + honour);
                    }
                }

                // S2.N7 (b) judged by the oracle on a 5 m/s dive: green, then S2.M10 red
                foreach (uint mut in new[] { 0u, ViewMutants.M10SettledAlways })
                {
                    var sim = new ViewSim { TraceRows = true, Mutants = mut, SettledOnlyWhenStill = true };
                    sim.Hold(120, 20.0); sim.Ramp(480, 20.0, -20.0); sim.Hold(120, -20.0);
                    string path = Keep(written, WriteRows(root, mut == 0 ? "dive5" : "dive5-m10", sim.Rows));
                    string output;
                    int code = RunOracle(root, out output, "replay", path, "--max-entry-ms", "500");
                    Console.WriteLine(output);
                    Assert.AreEqual(mut == 0 ? 0 : 1, code, "dive5 " + (mut == 0 ? "green" : "S2.M10 red") + ":\n" + output);
                    if (mut != 0)
                    {
                        StringAssert.Matches(output, new System.Text.RegularExpressions.Regex(@"crossing -> Underwater\s+FAIL"));
                        string honour;
                        Assert.AreEqual(1, RunOracle(root, out honour, "replay", path, "--max-entry-ms", "500", "--honour-mutant"),
                                        "the replay agrees under the mutant, the late entry stays red:\n" + honour);
                        StringAssert.Contains(honour, "localMismatch=0 linkMismatch=0");
                    }
                }
            }
            finally
            {
                foreach (string f in written)
                    try { File.Delete(f); } catch (IOException) { }
            }
        }
    }
}
