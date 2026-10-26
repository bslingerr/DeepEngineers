using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using ChartC = DeepEngineers.Wire.S8aConventions.Chart;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a-2 charts and the treadmill (ROADMAP B3 "S8aChartManager: steps, re-centres, return, PicturePolicy.Set while
 /// k ≥ 1"; CONTRACT §3.3 "Chart and treadmill", formats chart.step, chart.anchor, chart.apply). Chart 0 is the site
 /// map v1 and the only chart while <see cref="TreadmillOn"/> is off (env <c>DEEPENGINEERS_S8A_TREADMILL</c>, default
 /// off). With it on, Wire's <see cref="S8aChart.Decide"/> rules a step: the camera's SN position moves by whole
 /// batches (<see cref="S8aChart.Step"/>: anchor at the camera's sea point, the carried frame, O' = F_k(camera) − T),
 /// <c>SiteBinding.SetChart</c> swaps the map at the tick the <c>ChartStep</c> notice is queued (so C1, C2 and the S8a
 /// header of that tick are under chart k + 1), the window follows the chart key, and while k ≥ 1 the picture policy
 /// is <c>Waterline</c> (<c>PicturePolicy.Set</c>, the S7a live setting; back on chart 0 the policy it had returns).
 /// <see cref="InTransition"/> (C7 <c>chartSeqApplied</c> ≠ <see cref="ChartSeq"/>) lets S7Statics drop C12 batches.
 /// S8a.M8: T = the exact overshoot (the camera's chart offset), not whole batches. Rows: <c>s8a.chart</c> {tick,
 /// chartSeq, chartKey, T, anchor, reason, camSnBefore, camSnAfter} per step.
 /// </summary>
    internal static class S8aChartManager
    {
        public const string EnvTreadmill = "DEEPENGINEERS_S8A_TREADMILL";

        public static bool TreadmillOn { get; private set; }
        /// <summary>The wire chart sequence: monotonic over the session (every step and every return increments it; C1/C2/header carry it).</summary>
        public static uint ChartSeq { get; private set; }
        /// <summary>The chart index of <see cref="S8aChart.Decide"/>: 0 = the site chart (chart 0), k >= 1 = the k-th treadmill chart since the last return.</summary>
        public static uint ChartIndex { get; private set; }
        public static ulong ChartKey { get; private set; }
        public static ulong SculptHash { get; private set; }
        public static long Steps { get; private set; }
        public static string LastReason { get; private set; }
        private static bool _policySet;
        private static uint _policyWas;
        private static bool _up;
        private static Vec3 _lastT;

        /// <summary>The current chart (SiteBinding's map: chart 0 = site v1, else the treadmill's), null before the site is bound.</summary>
        public static SiteMap Map { get { return SiteBinding.Map; } }

        /// <summary>CONTRACT "chart.apply": Subnautica still reports the previous chart (C7 chartSeqApplied lags): SE ignores Subnautica-positioned input meanwhile.</summary>
        public static bool InTransition
        {
            get
            {
                if (!_up || ChartSeq == 0 && !_policySet) return false;
                if (!S8aFeed.HasC7 || !S8aFeed.SnHasS8a) return false;
                return S8aFeed.LastC7.chartSeqApplied != ChartSeq;
            }
        }

        /// <summary>Automatic steps held back by StepGapMs (terrain 4).</summary>
        public static long GapHolds;
        private static ulong _lastStepMs;
        /// <summary>The earlier off-site policy (Waterline on charts >= 1): env DEEPENGINEERS_S8A_OFFSITE_WATERLINE=on.</summary>
        public static bool WaterlineOffSite { get; private set; }

        /// <summary>
 /// a test run (nl 20261024-033738: 16 black far captures, SE's zone FAR beyond 7.9 km parks Subnautica's picture): the treadmill
 /// switched at run time (<c>de.s8a chart {treadmill}</c>, launcher part nl): on, the charts follow the camera out; off, the
 /// next tick returns to chart 0.
 /// </summary>
        public static void SetTreadmill(bool on, string why)
        {
            if (TreadmillOn == on) return;
            TreadmillOn = on;
            DeLog.Info("DE.S8a treadmill " + (on ? "on" : "off") + " (" + why + ")");
        }

        public static void ReadEnv()
        {
            WaterlineOffSite = S8aOverlay.EnvOn("DEEPENGINEERS_S8A_OFFSITE_WATERLINE", false);
            TreadmillOn = S8aOverlay.EnvOn(EnvTreadmill, false);
        }

        /// <summary>The site bound (chart 0): the sculpt hash of the deployed world mod decides the window's chart keys.</summary>
        public static void OnWorldUp(ulong sculptHash)
        {
            SculptHash = sculptHash;
            ChartSeq = 0;
            ChartIndex = 0;
            ChartKey = SiteBinding.Map != null ? S8aChart.ChartKey(SiteBinding.Map, sculptHash) : 0;
            _up = SiteBinding.Map != null;
            Steps = 0;
            LastReason = null;
            RestorePolicy("world up");
        }

        public static void OnWorldDown()
        {
            RestorePolicy("world down");
            _up = false;
            ChartSeq = 0;
            ChartIndex = 0;
            ChartKey = 0;
        }

        /// <summary>SiteBinding re-decided the site (a rebind): chart 0 again.</summary>
        public static void OnSiteRebound()
        {
            if (!_up) return;
            ChartSeq = 0;
            ChartIndex = 0;
            ChartKey = SiteBinding.Map != null ? S8aChart.ChartKey(SiteBinding.Map, SculptHash) : 0;
            RestorePolicy("site rebound");
        }

        /// <summary>Per tick (game thread): the step rule under the current chart; the picture policy while a treadmill chart is active.</summary>
        public static void Tick(ulong nowMs, Vec3 camSe, bool haveCamera)
        {
            if (!_up || !SiteBinding.IsOk || !haveCamera) return;
            SiteMap chart = SiteBinding.Map, site = SiteBinding.SiteMapV1 ?? chart;
            if (chart == null) return;
            S8aChartDecision d = S8aChart.Decide(chart, site, ChartIndex, camSe, TreadmillOn);
            // terrain 4 ("chart.step"): an automatic step no sooner than StepGapMs after the previous one (no flip-flop at a bound);
 // a return to chart 0 is never delayed
            if (d.Any && (d.ToSite || _lastStepMs == 0 || nowMs - _lastStepMs >= (ulong)ChartC.StepGapMs)) Apply(d, chart, site, camSe, "auto: " + d.Reason);
            else if (d.Any) GapHolds++;
            // terrain 4 ("chart.
 // vertically"): the picture policy stays (Site: Subnautica's picture below the altitude band); Subnautica hides the Subnautica
 // map's above-water landmarks while the C2 flag OffSite is set. Env DEEPENGINEERS_S8A_OFFSITE_WATERLINE=on restores the
 // earlier Waterline policy on charts >= 1.
            if (ChartIndex >= 1 && !_policySet && WaterlineOffSite)
            {
                _policyWas = PicturePolicy.Current;
                _policySet = true;
                PicturePolicy.Set(ViewPicture.Waterline, "s8a.treadmill");
            }
            else if (ChartIndex == 0 && _policySet) RestorePolicy("back on chart 0");
        }

        private static void RestorePolicy(string why)
        {
            if (!_policySet) return;
            _policySet = false;
            PicturePolicy.Set(_policyWas, "s8a.treadmill " + why);
        }

        private static void Apply(S8aChartDecision d, SiteMap chart, SiteMap site, Vec3 camSe, string reason)
        {
            Vec3 before = chart.SeToSn(camSe);
            SiteMap next;
            double txM = d.Tx * S8aChart.BatchM, tzM = d.Tz * S8aChart.BatchM;
            if (d.ToSite) next = site;
            else if (S8aMutants.M8)
            {
                // S8a.M8: the exact overshoot (the camera's chart offset along the stepped axis), not whole batches
                txM = d.Tx != 0 ? before.X : 0.0;
                tzM = d.Tz != 0 ? before.Z : 0.0;
                next = S8aChart.StepMetres(chart, camSe, txM, tzM);
            }
            else next = S8aChart.Step(chart, camSe, d.Tx, d.Tz);
            ChartSeq++;
            ChartIndex = d.ToSite ? 0 : ChartIndex + 1;
            _lastStepMs = Win32.TickCount64();
            Steps++;
            LastReason = reason;
            _lastT = new Vec3(txM, 0, tzM);
            SiteBinding.SetChart(next, ChartSeq, reason);
            ChartKey = S8aChart.ChartKey(next, SculptHash);
            Vec3 after = next.SeToSn(camSe);
            uint seq = S8aFeed.Enqueue(S8aNotice.ChartStep(ChartSeq, d.ToSite ? 0 : d.Tx, d.ToSite ? 0 : d.Tz));
            S8aWindow.OnChartChanged(ChartSeq, ChartKey, next, d.ToSite ? 0 : d.Tx, d.ToSite ? 0 : d.Tz);
            string anchor = "U0=" + V(next.U0) + " O=" + V(next.O);
            Jsonl.WriteLine(S4Rows.Row("s8a.chart", "tick", (long)GameState.GameplayTick, "chartSeq", (long)ChartSeq, "chartIndex", (long)ChartIndex, "chartKey", S8aStore.Hex16(ChartKey),
                                       "T", new object[] { txM, 0.0, tzM }, "tBatches", new object[] { d.Tx, d.Tz }, "anchor", anchor, "reason", reason, "toSite", d.ToSite,
                                       "camSnBefore", S4Rows.Arr(before), "camSnAfter", S4Rows.Arr(after), "seq", (long)seq, "m8", S8aMutants.M8));
            DeLog.Info("DE.S8a chart " + ChartSeq + " index " + ChartIndex + " (" + reason + "): T=(" + F(txM) + ", " + F(tzM) + ") m " + anchor + " cam SN " + V(before) + " -> " + V(after) +
                       " key=" + S8aStore.Hex16(ChartKey) + (S8aMutants.M8 ? " (S8a.M8 ON: exact overshoot)" : ""));
        }

        /// <summary><c>de.s8a step {axis, sign}</c>: a forced step of StepBatches along the axis (or the return to chart 0 with axis "site").</summary>
        public static Dictionary<string, object> Force(string axis, int sign, Vec3 camSe, bool haveCamera)
        {
            if (!_up || !SiteBinding.IsOk || !haveCamera) return Fail("no chart (site not bound or no camera)");
            SiteMap chart = SiteBinding.Map, site = SiteBinding.SiteMapV1 ?? chart;
            S8aChartDecision d;
            if (axis == "site") d = new S8aChartDecision { ToSite = true, Reason = "forced return" };
            else
            {
                int s = sign >= 0 ? 1 : -1;
                int n = ChartIndex == 0 ? ChartC.Step0Batches : ChartC.StepBatches;
                d = new S8aChartDecision { Step = true, Tx = axis == "x" ? n * s : 0, Tz = axis == "z" ? n * s : 0, Reason = "forced " + axis + (s > 0 ? "+" : "-") };
                if (d.Tx == 0 && d.Tz == 0) return Fail("axis: x | z | site");
            }
            Apply(d, chart, site, camSe, d.Reason);
            Dictionary<string, object> r = Status();
            r["ok"] = true;
            r["line"] = "de.s8a step: chart " + ChartSeq + " (" + d.Reason + ")";
            return r;
        }

        public static Dictionary<string, object> Status()
        {
            SiteMap m = SiteBinding.Map;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "treadmill", TreadmillOn }, { "chartSeq", (long)ChartSeq }, { "chartIndex", (long)ChartIndex }, { "chartKey", S8aStore.Hex16(ChartKey) }, { "sculptHash", S8aStore.Hex16(SculptHash) },
                { "steps", Steps }, { "lastReason", LastReason }, { "inTransition", InTransition }, { "snChartSeqApplied", (long)S8aFeed.LastC7.chartSeqApplied },
                { "picturePolicySet", _policySet }, { "anchorU0", m != null ? S4Rows.Arr(m.U0) : null }, { "anchorO", m != null ? S4Rows.Arr(m.O) : null },
                { "lastT", S4Rows.Arr(_lastT) }, { "m8", S8aMutants.M8 }, { "zone", ZoneTracker.Name(FrameWriter.C5Zone) }, { "gapHolds", GapHolds },
            };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", "de.s8a step: " + error }, { "line", "de.s8a step: " + error } };
        }

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string V(Vec3 v) { return "(" + F(v.X) + ", " + F(v.Y) + ", " + F(v.Z) + ")"; }
    }
}
