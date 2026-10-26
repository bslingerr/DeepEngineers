using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S2 view state machine (ARCHITECTURE §4, ROADMAP S2 state table, S2.N2, S2.M4): every transition with scripted
 /// y traces, the guards one by one, crossing order, zone exit at 7,900 m, peer stalled within one tick, the
 /// ESC/foreground gating of the audio writes, crossfades, A′ and the two S2.N2 traces (green, and red under S2.M4).
 /// <para>
 /// S7a: the machine's default picture policy is <see cref="ViewPicture.Site"/>. The states, transitions
 /// and thresholds are unchanged, so most tests here hold under both policies and run on the default. The tests that
 /// pin S2's picture (Track recipe, margins only in Prime/Underwater/Surfacing, the skip exactly Underwater, Surfacing's
 /// scene-drawn frame, margins off with Prime, the z-order pulse at the Prime entry, C5 flags without bit 9) keep that
 /// expectation under an explicit <see cref="ViewPicture.Waterline"/> input ("S2 policy, the fallback") and state what
 /// S7a wants under Site next to it (ROADMAP S7a "Re-stated pins"); the S7a-only behaviour is in S7aPictureTests.
 /// </para>
 /// </summary>
    [TestClass]
    public class ViewStateMachineTests
    {
        private static void AssertView(ViewOutputs o, uint view, uint approach, string what)
        {
            Assert.AreEqual(ViewStateMachine.ViewName(view), ViewStateMachine.ViewName(o.ViewState), what + ": view state (blocked 0x" + o.Blocked.ToString("x") + ")");
            Assert.AreEqual(ViewStateMachine.ApproachName(approach), ViewStateMachine.ApproachName(o.ApproachMode), what + ": approach mode");
        }

        private static void AssertNoOrderViolation(ViewSim sim)
        {
            List<string> bad = ViewSim.OrderViolations(sim.Log);
            Assert.AreEqual(0, bad.Count, "crossing order: " + string.Join("; ", bad.Take(5)));
        }

        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        [TestMethod]
        public void Band_DefaultIs1000And1100_SetBandValidates()
        {
            // S7t T3: the band back at 1 km (it was +400 / +450 m from 16:50 until S7t)
            Assert.AreEqual(1000.0, ViewThresholds.DefaultApproachEnterY, "S7t T3: the band back to 1 km");
            Assert.AreEqual(1100.0, ViewThresholds.DefaultApproachExitY);
            Assert.IsTrue(ViewThresholds.SetBand(ViewThresholds.DefaultApproachEnterY, ViewThresholds.DefaultApproachExitY, "test-default"));
            try
            {
                var sim = new ViewSim { Vy = 0.0 };
                AssertView(sim.Hold(5, 1200.0), ViewState.Space, ApproachMode.None, "+1,200 m: Space under the default band");
                AssertView(sim.Step(999.0), ViewState.Approach, ApproachMode.Track, "+999 m: Approach");
                sim.Step(1100.001);
                AssertView(sim.Hold(60, 1100.001), ViewState.Space, ApproachMode.None, "+1,100.001 m held 1 s: Space");
                Assert.IsFalse(ViewThresholds.SetBand(1100.0, 1000.0, "bad"), "exit must be above enter");
                Assert.IsFalse(ViewThresholds.SetBand(-1.0, 100.0, "bad"), "enter must be >= 0");
                Assert.IsFalse(ViewThresholds.SetBand(double.NaN, 100.0, "bad"));
                Assert.AreEqual(1000.0, ViewThresholds.ApproachEnterY, "a refused band leaves the current one");
                double e, x;
                Assert.IsTrue(ViewThresholds.TryParseBand("1000,1100", out e, out x) && e == 1000.0 && x == 1100.0);
                Assert.IsTrue(ViewThresholds.TryParseBand(" 300 ; 350 ", out e, out x) && e == 300.0 && x == 350.0);
                Assert.IsFalse(ViewThresholds.TryParseBand("1000", out e, out x));
                Assert.IsFalse(ViewThresholds.TryParseBand("a,b", out e, out x));
            }
            finally { ViewThresholds.SetBand(300.0, 350.0, "test"); }
        }

        // ------------------------------------------------------------------------------------------- Space / Approach
        [TestMethod]
        public void SpaceToApproach_BelowPlus300_BackToSpaceAbovePlus350()
        {
            var sim = new ViewSim { Vy = 0.0, Picture = ViewPicture.Waterline };   // S2 policy, the fallback
            ViewOutputs o = sim.Hold(5, 400.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "+400 m");
            Assert.AreEqual(PerfMode.Idle, o.PerfMode);
            Assert.AreEqual(10u, o.TargetFps);
            Assert.IsTrue(o.Freeze, "Space: Subnautica frozen (Idle)");
            Assert.IsFalse(o.MarginsOn);

            AssertView(sim.Step(300.0), ViewState.Space, ApproachMode.None, "exactly +300 m (entry is strict)");
            o = sim.Step(299.999);
            AssertView(o, ViewState.Approach, ApproachMode.Track, "+299.999 m");
            Assert.AreEqual(ViewReason.EnterApproach, o.Reason);
            Assert.IsTrue(o.Changed);
            Assert.AreEqual(PerfMode.Track, o.PerfMode);
            Assert.AreEqual(10u, o.TargetFps);
            Assert.IsFalse(o.Freeze, "Track: time running");
            Assert.IsFalse(o.MarginsOn, "Track: margins 0");

            AssertView(sim.Step(320.0), ViewState.Approach, ApproachMode.Track, "+320 m inside the band");
            AssertView(sim.Step(350.0), ViewState.Approach, ApproachMode.Track, "exactly +350 m (exit is strict)");
            AssertView(sim.Step(350.001), ViewState.Approach, ApproachMode.Track, "+350.001 m: the exit holds 1 s first (F15)");
            AssertView(sim.Hold(59, 350.001), ViewState.Approach, ApproachMode.Track, "+350.001 m for 59 ticks: still Approach");
            o = sim.Step(350.001);
            AssertView(o, ViewState.Space, ApproachMode.None, "+350.001 m held 1 s");
            Assert.AreEqual(ViewReason.LeaveAltitude, o.Reason);
            AssertView(sim.Step(320.0), ViewState.Space, ApproachMode.None, "+320 m from above: re-entry needs < +300 m");
            AssertView(sim.Step(299.0), ViewState.Approach, ApproachMode.Track, "+299 m");

            // S7a Site: the same transitions; Subnautica renders Full from the Approach entry with the margins on (it must
 // be composed under SE before the swap), the picture latch turns on one tick later (the margins request
 // presented) and off on the Space tick; SE's scene is drawn first, the margins go after that frame
            var site = new ViewSim { Vy = 0.0 };
            o = site.Hold(5, 400.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "site +400 m");
            Assert.IsTrue(o.PerfMode == PerfMode.Idle && o.Freeze && !o.MarginsOn && !o.Picture, "site Space: Idle, frozen, SE's picture");
            o = site.Step(299.999);
            AssertView(o, ViewState.Approach, ApproachMode.Track, "site +299.999 m");
            Assert.AreEqual(PerfMode.Full, o.PerfMode, "site Track: Full (Subnautica warm and streaming at the true pose)");
            Assert.AreEqual(ViewThresholds.FullTargetFps, o.TargetFps);
            Assert.IsFalse(o.Freeze);
            Assert.IsTrue(o.MarginsOn && o.AlphaFill && !o.SceneSkip, "site Track: margins from the Approach entry, SE's scene alpha-filled");
            Assert.IsFalse(o.Picture, "the latch waits for the margins request to be presented");
            Assert.AreNotEqual(0u, o.PictureBlocked & ViewBlock.NotApproach);
            o = site.Step(299.0);
            Assert.IsTrue(o.Picture && o.SceneSkip && o.MarginsOn && o.PictureEdge == 1, "site: Subnautica's picture on the next tick");
            Assert.AreEqual(ViewReason.PictureOn, o.PictureReason);
            site.Step(350.001);
            o = site.Hold(60, 350.001);
            AssertView(o, ViewState.Space, ApproachMode.None, "site +350.001 m held 1 s");
            Assert.IsTrue(!o.Picture && o.PictureEdge == -1 && !o.SceneSkip && o.MarginsOn, "site Space: scene drawn first, margins kept");
            Assert.AreEqual(ViewReason.PictureOffSpace, o.PictureReason);
            Assert.AreEqual(PerfMode.Full, o.PerfMode, "F1: Subnautica renders Full until the scene-drawn frame is presented (no freeze, no parking)");
            o = site.Step(360.0);
            Assert.IsFalse(o.MarginsOn, "margins off once the scene-drawn frame was presented");
            Assert.AreEqual(PerfMode.Idle, o.PerfMode, "Idle once SE's own scene is on screen");
        }

        [TestMethod]
        public void Approach_NeedsSiteZoneBridgeAndLivePeer()
        {
            var sim = new ViewSim { Vy = 0.0 };
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "start");

            sim.OnSite = false;
            ViewOutputs o = sim.Step(100.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "off site");
            Assert.AreEqual(ViewReason.LeaveSite, o.Reason);
            Assert.AreEqual(Zone.OffSite, sim.Log.Last().Zone);
            sim.OnSite = true;
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "back on site");

            sim.BridgeUp = false;
            AssertView(sim.Step(100.0), ViewState.Space, ApproachMode.None, "bridge down (no hello ack / refused)");
            sim.BridgeUp = true;
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "bridge up");

            sim.Peer = PeerState.Stalled;
            o = sim.Step(100.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "peer stalled");
            Assert.AreEqual(ViewReason.LeavePeer, o.Reason);
            Assert.AreEqual(Overlays.PeerStalled, o.Overlays & Overlays.PeerStalled);
            sim.Peer = PeerState.Unknown;
            AssertView(sim.Step(100.0), ViewState.Space, ApproachMode.None, "new generation not judged yet");
            sim.Peer = PeerState.Alive;
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "peer alive");

            sim.X = 7850.0;
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "7,850 m: still in zone (hysteresis)");
            Assert.AreEqual(Zone.OuterVoid, sim.Log.Last().Zone);
            sim.X = 7900.5;
            o = sim.Step(100.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "7,900.5 m: FAR");
            Assert.AreEqual(Zone.Far, sim.Log.Last().Zone);
            sim.X = 7850.0;
            AssertView(sim.Step(100.0), ViewState.Space, ApproachMode.None, "7,850 m from outside: still FAR");
            sim.X = 7800.0;
            AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, "7,800 m: OUTER VOID again");
        }

        [TestMethod]
        public void SpaceToPrime_OnTheSameTick_WhenArrivingAtTheSurface()
        {
            var sim = new ViewSim { Vy = 0.0 };
            sim.Hold(3, 1000.0);
            ViewOutputs o = sim.Step(0.5);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "+1,000 m → +0.5 m in one tick");
            Assert.AreEqual(PerfMode.Full, o.PerfMode);
            Assert.IsTrue(o.MarginsOn && o.AlphaFill && !o.SceneSkip, "Prime: margins on, alpha filled, scene drawn");
            Assert.IsTrue(o.ZOrderReassert, "Prime entry re-asserts the z-order");
        }

        // ------------------------------------------------------------------------------------------- Track ↔ Prime
        [TestMethod]
        public void TrackToPrime_EachCondition()
        {
            var cases = new[]
            {
                new { y = 1.0, vy = 0.0, prime = true, why = "y <= +1" },
                new { y = 1.0001, vy = 0.0, prime = false, why = "y just above +1, still" },
                new { y = -3.0, vy = 2.0, prime = true, why = "below the water, rising" },
                new { y = 5.0, vy = -0.2, prime = true, why = "y <= +5 and vy <= -0.2" },
                new { y = 5.0, vy = -0.19, prime = false, why = "vy -0.19 at +5" },
                new { y = 5.0001, vy = -0.2, prime = false, why = "+5.0001 at -0.2 m/s (crossing in 25 s)" },
                new { y = 10.0, vy = -2.5, prime = true, why = "crossing predicted in 4.0 s" },
                new { y = 10.0, vy = -2.49, prime = false, why = "crossing predicted in 4.016 s" },
                new { y = 40.0, vy = -10.0, prime = true, why = "crossing in 4 s from +40 m" },
                new { y = 40.0, vy = -5.0, prime = false, why = "crossing in 8 s from +40 m" },
                new { y = 3.0, vy = 0.5, prime = false, why = "rising at +3 m" },
            };
            foreach (var c in cases)
            {
                var sim = new ViewSim { Vy = 0.0 };
                AssertView(sim.Step(100.0), ViewState.Approach, ApproachMode.Track, c.why + " (setup)");
                sim.Vy = c.vy;
                ViewOutputs o = sim.Step(c.y);
                Assert.AreEqual(c.prime ? ApproachMode.Prime : ApproachMode.Track, o.ApproachMode, c.why);
                Assert.AreEqual(c.prime, ViewStateMachine.PrimeCondition(c.y, c.vy), c.why + " (PrimeCondition)");
                if (c.prime) Assert.AreEqual(ViewReason.EnterPrime, o.Reason, c.why);
                double tc = ViewStateMachine.PredictedCrossingS(c.y, c.vy);
                if (c.y > 5.0) Assert.AreEqual(c.prime, tc <= ViewThresholds.PrimePredictS, c.why + " (predicted crossing " + tc + " s)");
            }
        }

        /// <summary>
 /// Prime → Track after 3 s without a Prime condition at <c>y ≥ +5 m</c>; never at <c>y ≤ +1 m</c>.
 /// </summary>
 /// <summary> (109 m/s return from space): Approach by predicted arrival within 8 s, kept while diving fast.</summary>
        [TestMethod]
        public void ApproachByPredictedArrival_Within8s_AndKeptWhileDescendingFast()
        {
            Assert.IsTrue(ViewStateMachine.ApproachCondition(870.0, -109.0), "870 m at 109 m/s: +300 m in 5.2 s");
            Assert.IsTrue(ViewStateMachine.ApproachCondition(1172.0, -109.0), "1,172 m at 109 m/s: exactly 8 s");
            Assert.IsFalse(ViewStateMachine.ApproachCondition(1173.0, -109.0), "1,173 m at 109 m/s: 8.01 s");
            Assert.IsFalse(ViewStateMachine.ApproachCondition(600.0, -10.0), "600 m at 10 m/s: 30 s away");
            Assert.IsFalse(ViewStateMachine.ApproachCondition(600.0, 20.0), "rising");
            Assert.IsTrue(ViewStateMachine.ApproachCondition(299.9, 50.0), "below +300 m: in, whatever vy");
            Assert.IsFalse(ViewStateMachine.ApproachLeft(400.0, -20.0), "400 m descending at 20 m/s: +350 m in 2.5 s, kept");
            Assert.IsTrue(ViewStateMachine.ApproachLeft(400.0, 0.0), "400 m still: left");
            Assert.IsTrue(ViewStateMachine.ApproachLeft(400.0, -5.0), "400 m at 5 m/s: 10 s away, left");
            Assert.IsFalse(ViewStateMachine.ApproachLeft(350.0, 0.0), "exactly +350 m: not left (exit is strict)");
            var sim = new ViewSim { Vy = -109.0 };
            AssertView(sim.Step(2000.0), ViewState.Space, ApproachMode.None, "2 km at 109 m/s: 15.6 s away");
            ViewOutputs o = sim.Step(1000.0);
            AssertView(o, ViewState.Approach, ApproachMode.Track, "1 km at 109 m/s: 6.4 s away");
            Assert.AreEqual(ViewReason.EnterApproach, o.Reason);
            AssertView(sim.Step(900.0), ViewState.Approach, ApproachMode.Track, "still descending fast: kept above +350 m");
            sim.Vy = 0.0;
            sim.Step(900.0);
            AssertView(sim.Hold(60, 900.0), ViewState.Space, ApproachMode.None, "stopped at 900 m: back to Space after the 1 s hold");
        }

        [TestMethod]
        public void PrimeToTrack_After3sAbovePlus5m_AndOnlyAbovePlus1()
        {
            ViewSim sim = ViewSim.Primed(0.5, ViewPicture.Waterline);   // S2 policy, the fallback
            sim.Vy = 0.0;
            AssertView(sim.Hold(600, 0.5), ViewState.Approach, ApproachMode.Prime, "10 s at +0.5 m (sailing): Prime holds");

            sim.Step(0.9);                       // the last tick a condition holds
            ulong t0 = sim.Log.Last().Tick;
            sim.Hold(150, 6.0);
            sim.Step(0.9);                       // a condition again: the 3 s restart
            t0 = sim.Log.Last().Tick;
            for (int k = 1; k <= 179; k++)
            {
                ViewOutputs h = sim.Step(6.0);
                Assert.AreEqual(ApproachMode.Prime, h.ApproachMode, "tick t0+" + k + " (< 3 s)");
                Assert.IsTrue(h.MarginsOn, "margins stay on while Prime");
            }
            ViewOutputs o = sim.Step(6.0);
            Assert.AreEqual(t0 + 180, sim.Log.Last().Tick);
            AssertView(o, ViewState.Approach, ApproachMode.Track, "3.00006 s quiet at +6 m");
            Assert.AreEqual(ViewReason.PrimeQuiet, o.Reason);
            Assert.IsFalse(o.MarginsOn, "margins off when leaving Prime");
            Assert.AreEqual(PerfMode.Track, o.PerfMode);

            // S7a Site: the same Prime -> Track timing; Track keeps Subnautica's picture (the latch holds to Space), the
 // margins and the Full recipe: Prime only decides the waterline approach now
            ViewSim site = ViewSim.Primed(0.5);
            site.Vy = 0.0;
            site.Step(0.9);
            ulong s0 = site.Log.Last().Tick;
            for (int k = 1; k <= 179; k++) Assert.AreEqual(ApproachMode.Prime, site.Step(6.0).ApproachMode, "site tick t0+" + k);
            ViewOutputs so = site.Step(6.0);
            Assert.AreEqual(s0 + 180, site.Log.Last().Tick);
            AssertView(so, ViewState.Approach, ApproachMode.Track, "site 3.00006 s quiet at +6 m");
            Assert.IsTrue(so.Picture && so.SceneSkip && so.MarginsOn && so.PictureEdge == 0, "site Track: the picture stays, no swap");
            Assert.AreEqual(PerfMode.Full, so.PerfMode);
        }

        /// <summary>

 /// showed SE for about 0.5–1 s; measured 533 ms from the crossing to the entry after a Prime → Track drop): the
 /// quiet timeout is 10 s while <c>y &lt; +5 m</c> (strict) and 3 s from +5 m up; the timeout is read at the
 /// current <c>y</c>, so rising above +5 m after 3 s of quiet drops to Track on that tick.
 /// </summary>
        [TestMethod]
        public void PrimeToTrack_After10sBelowPlus5m_3sFromPlus5m()
        {
            Assert.AreEqual(10000000L, ViewThresholds.PrimeQuietNearUs);
            Assert.AreEqual(3000000L, ViewThresholds.PrimeQuietUs);
            Assert.AreEqual(5.0, ViewThresholds.PrimeQuietNearY);
            Assert.AreEqual(ViewThresholds.PrimeQuietNearUs, ViewStateMachine.PrimeQuietTimeoutUs(4.999));
            Assert.AreEqual(ViewThresholds.PrimeQuietNearUs, ViewStateMachine.PrimeQuietTimeoutUs(-3.0));
            Assert.AreEqual(ViewThresholds.PrimeQuietUs, ViewStateMachine.PrimeQuietTimeoutUs(5.0), "+5 m exactly: 3 s (y < +5 m is strict)");
            Assert.AreEqual(ViewThresholds.PrimeQuietUs, ViewStateMachine.PrimeQuietTimeoutUs(250.0));
            Assert.AreEqual(ViewThresholds.PrimeQuietUs, ViewStateMachine.PrimeQuietTimeoutUs(double.NaN));

            foreach (double y in new[] { 1.0001, 2.0, 4.999 })
            {
                ViewSim sim = ViewSim.Primed(0.5, ViewPicture.Waterline);   // S2 policy, the fallback (margins off with Prime)
                sim.Vy = 0.0;
                sim.Step(0.9);                   // the last tick a condition holds
                ulong t0 = sim.Log.Last().Tick;
                for (int k = 1; k <= 599; k++)
                {
                    ViewOutputs h = sim.Step(y);
                    Assert.AreEqual(ApproachMode.Prime, h.ApproachMode, "y " + y + ": tick t0+" + k + " (< 10 s)");
                    Assert.IsTrue(h.MarginsOn && h.PerfMode == PerfMode.Full, "Prime keeps the margins and Subnautica at full rate");
                }
                ViewOutputs o = sim.Step(y);
                Assert.AreEqual(t0 + 600, sim.Log.Last().Tick);
                AssertView(o, ViewState.Approach, ApproachMode.Track, "10.0002 s quiet at y = " + y);
                Assert.AreEqual(ViewReason.PrimeQuiet, o.Reason);
                Assert.IsFalse(o.MarginsOn, "margins off when leaving Prime");
                AssertNoOrderViolation(sim);
            }

            // S7a Site: the same 10 s rule below +5 m; Track then keeps the margins, the picture and Full
            ViewSim siteNear = ViewSim.Primed(0.5);
            siteNear.Vy = 0.0;
            siteNear.Step(0.9);
            ulong tn = siteNear.Log.Last().Tick;
            for (int k = 1; k <= 599; k++) Assert.AreEqual(ApproachMode.Prime, siteNear.Step(2.0).ApproachMode, "site +2 m, tick t0+" + k);
            ViewOutputs sn = siteNear.Step(2.0);
            Assert.AreEqual(tn + 600, siteNear.Log.Last().Tick);
            AssertView(sn, ViewState.Approach, ApproachMode.Track, "site 10.0002 s quiet at +2 m");
            Assert.IsTrue(sn.MarginsOn && sn.Picture && sn.PerfMode == PerfMode.Full, "site Track keeps margins, picture and Full");
            AssertNoOrderViolation(siteNear);

            // exactly +5 m: the 3 s timeout
            ViewSim at5 = ViewSim.Primed(0.5);
            at5.Vy = 0.0;
            at5.Step(0.9);
            ulong t5 = at5.Log.Last().Tick;
            for (int k = 1; k <= 179; k++) Assert.AreEqual(ApproachMode.Prime, at5.Step(5.0).ApproachMode, "+5 m, tick t0+" + k);
            AssertView(at5.Step(5.0), ViewState.Approach, ApproachMode.Track, "3.00006 s quiet at exactly +5 m");
            Assert.AreEqual(t5 + 180, at5.Log.Last().Tick);

            // 5 s of quiet at +2 m, then up to +6 m: Track on the first tick above +5 m (the 3 s are long over)
            ViewSim up = ViewSim.Primed(0.5);
            up.Vy = 0.0;
            up.Step(0.9);
            AssertView(up.Hold(300, 2.0), ViewState.Approach, ApproachMode.Prime, "5 s quiet at +2 m");
            ViewOutputs u = up.Step(6.0);
            AssertView(u, ViewState.Approach, ApproachMode.Track, "first tick at +6 m after 5 s of quiet");
            Assert.AreEqual(ViewReason.PrimeQuiet, u.Reason);

            // 2 s of quiet at +2 m, then +6 m: Track once 3 s have passed since the last condition
            ViewSim up2 = ViewSim.Primed(0.5);
            up2.Vy = 0.0;
            up2.Step(0.9);
            ulong t2 = up2.Log.Last().Tick;
            up2.Hold(120, 2.0);
            for (int k = 121; k <= 179; k++) Assert.AreEqual(ApproachMode.Prime, up2.Step(6.0).ApproachMode, "+6 m, tick t0+" + k);
            AssertView(up2.Step(6.0), ViewState.Approach, ApproachMode.Track, "3 s after the last condition, at +6 m");
            Assert.AreEqual(t2 + 180, up2.Log.Last().Tick);
        }

        /// <summary>
 /// 5 m/s, linger 8 s at +2 m, dive again at
 /// 1.5 m/s. Prime is kept through the linger (10 s rule), so the dive enters Underwater as soon as Subnautica
 /// presents the crossing; the same linger at +8 m (above +5 m) drops to Track after 3 s.
 /// </summary>
        [TestMethod]
        public void ReDive_After8sNearTheSurface_KeepsPrime_EntersAtOnce_AndTrackAbovePlus5m()
        {
            ViewSim sim = ViewSim.Underwater(-3.0);
            sim.Ramp(200, -3.0, 2.0);                            // up at 1.5 m/s
            sim.Hold(480, 2.0);                                  // 8 s at +2 m
            AssertView(sim.Last, ViewState.Approach, ApproachMode.Prime, "8 s at +2 m after surfacing");
            int diveStart = sim.Log.Count;
            sim.Ramp(200, 2.0, -3.0);                            // down at 1.5 m/s
            sim.Hold(30, -3.0);
            Assert.IsFalse(sim.Log.Any(r => r.O.ViewState == ViewState.Approach && r.O.ApproachMode == ApproachMode.Track),
                           "no Prime -> Track drop near the surface");
            int entry = sim.Log.FindIndex(diveStart, r => r.O.ViewState == ViewState.Underwater);
            Assert.IsTrue(entry > diveStart, "entered");
            ViewRec e = sim.Log[entry];
            ViewRec c = sim.Log.First(r => r.Tick == e.O.CrossingTick);
            long ms = (e.Us - c.Us) / 1000;
            Assert.IsTrue(ms <= 300, "re-dive: Underwater " + ms + " ms after the crossing (n7b threshold 300 ms)");
            AssertNoOrderViolation(sim);

            // the same linger at +8 m: Track (3 s rule above +5 m, on the way up or during the 12 s), then the dive
 // passes Prime again
            int lingerStart = sim.Log.Count;
            sim.Ramp(330, -3.0, 8.0);
            sim.Hold(720, 8.0);                                  // 12 s at +8 m
            Assert.IsTrue(sim.Log.Skip(lingerStart).Any(r => r.O.ApproachMode == ApproachMode.Track && r.O.Reason == ViewReason.PrimeQuiet &&
                                                             r.Y >= ViewThresholds.PrimeQuietNearY), "Prime -> Track above +5 m");
            AssertView(sim.Last, ViewState.Approach, ApproachMode.Track, "after 12 s at +8 m");
            int dive2 = sim.Log.Count;
            sim.Ramp(440, 8.0, -3.0);
            sim.Hold(30, -3.0);
            int entry2 = sim.Log.FindIndex(dive2, r => r.O.ViewState == ViewState.Underwater);
            Assert.IsTrue(entry2 > dive2 && sim.Log.Skip(dive2).Take(entry2 - dive2).Any(r => r.O.ApproachMode == ApproachMode.Prime &&
                          r.O.ViewState == ViewState.Approach), "Track -> Prime -> Underwater");
            AssertNoOrderViolation(sim);
        }

        // ------------------------------------------------------------------------------------------- Prime → Underwater
        [TestMethod]
        public void PrimeToUnderwater_WhenEveryGuardHolds_SnLagTicksAfterTheCrossing()
        {
            foreach (int lag in new[] { 1, 3, 4 })
            {
                ViewSim sim = ViewSim.Primed();
                sim.SnLag = lag;
                sim.Vy = 0.0;
                ViewOutputs c = sim.Step(-0.5);
                ulong crossing = sim.Log.Last().Tick;
                Assert.AreEqual(crossing, c.CrossingTick, "crossingTick stamped at the first tick y <= -0.20");
                AssertView(c, ViewState.Approach, ApproachMode.Prime, "crossing tick (Subnautica has not presented it)");
                Assert.AreNotEqual(0u, c.Blocked & ViewBlock.Crossing);
                int steps = sim.StepUntil(-0.5, o => o.ViewState == ViewState.Underwater);
                Assert.AreEqual(lag, steps, "Underwater as soon as presentedSeTick >= crossingTick (SN lag " + lag + ")");
                ViewOutputs u = sim.Last;
                Assert.AreEqual(ViewReason.EnterUnderwater, u.Reason);
                Assert.IsTrue(u.SceneSkip && u.MarginsOn && !u.AlphaFill, "Underwater: scene skipped, margins on, no alpha fill");
                Assert.AreEqual(ApproachMode.Prime, u.ApproachMode, "approach mode stays Prime underwater (margins on)");
                Assert.AreEqual(PerfMode.Full, u.PerfMode);
                Assert.AreEqual(SeStateFlags.SeeThrough | SeStateFlags.MarginsOn, u.C5Flags & (SeStateFlags.SeeThrough | SeStateFlags.MarginsOn));
                AssertNoOrderViolation(sim);
            }
        }

        [TestMethod]
        public void PrimeToUnderwater_EachGuardBlocksAlone()
        {
            // name, break the guard, mend it, expected block bit
            var guards = new List<Tuple<string, Action<ViewSim>, Action<ViewSim>, uint>>
            {
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("snPhase LoadingSave", s => s.Phase = SnPhase.LoadingSave, s => s.Phase = SnPhase.Active, ViewBlock.Phase),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("snPhase Quitting", s => s.Phase = SnPhase.Quitting, s => s.Phase = SnPhase.Active, ViewBlock.Phase),
                // CONTRACT §5.4: snReady needs Active; ReadyIdle (Idle, e.g. Subnautica's own reaction to a stalled SE)
 // and WarmingUp (Track, or Full before its first frames) are not ready
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("snPhase ReadyIdle", s => s.Phase = SnPhase.ReadyIdle, s => s.Phase = SnPhase.Active, ViewBlock.Phase),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("snPhase WarmingUp", s => s.Phase = SnPhase.WarmingUp, s => s.Phase = SnPhase.Active, ViewBlock.Phase),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("areaBuilt", s => s.AreaBuilt = false, s => s.AreaBuilt = true, ViewBlock.AreaBuilt),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("rectOk", s => s.RectOk = false, s => s.RectOk = true, ViewBlock.Rect),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("uiHidden", s => s.UiHidden = false, s => s.UiHidden = true, ViewBlock.UiHidden),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("freshness (5 ticks old)", s => s.SnLag = 5, s => s.SnLag = 4, ViewBlock.Fresh),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("stamp 2 ticks ahead (stale world)", s => s.SnLag = -2, s => s.SnLag = -1, ViewBlock.Fresh),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("zOrderOk", s => s.ZOrderOk = false, s => s.ZOrderOk = true, ViewBlock.ZOrder),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("rho 7,850 m (in zone)", s => s.X = 7850.0, s => s.X = 7800.0, ViewBlock.Rho),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("render ack", s => s.AckLag = 1000000, s => s.AckLag = 1, ViewBlock.Render),
            };
            foreach (var g in guards)
            {
                ViewSim sim = ViewSim.Primed(0.5, ViewPicture.Waterline);   // S2 policy, the fallback
                sim.Vy = 0.0;
                sim.Step(0.5);
                if (g.Item1 == "render ack")
                {
                    // a fresh margins-on request that the render thread never presents
                    sim.Hold(200, 6.0);                         // back to Track (3 s quiet above +5 m): margins off
                    Assert.AreEqual(ApproachMode.Track, sim.Last.ApproachMode, "setup Track");
                    g.Item2(sim);
                    sim.Step(0.5);                              // Prime again: a new request, not acknowledged
                }
                else g.Item2(sim);
                ViewOutputs o = sim.Hold(30, -0.5);
                AssertView(o, ViewState.Approach, ApproachMode.Prime, g.Item1 + " broken");
                Assert.AreEqual(g.Item4, o.Blocked, g.Item1 + ": the only failing guard (0x" + o.Blocked.ToString("x") + ")");
                g.Item3(sim);
                int steps = sim.StepUntil(-0.5, x => x.ViewState == ViewState.Underwater, 10);
                Assert.IsTrue(steps <= 2, g.Item1 + " mended: Underwater within 2 ticks (took " + steps + ")");
                AssertNoOrderViolation(sim);
            }

            // S7a Site: the same guards block alone once the picture latch is on (none of them drops it: only a site, peer
 // or re-streaming loss does); the render bit is "latch off" instead of the margins-ready bit (the scene is
 // already skipped), so its case is a latch that never turned on because the margins were never presented
            foreach (var g in guards)
            {
                if (g.Item1 == "render ack") continue;
                ViewSim sim = ViewSim.Primed();
                sim.Vy = 0.0;
                sim.Step(0.5);
                Assert.IsTrue(sim.Last.Picture, "site setup: latch on");
                g.Item2(sim);
                ViewOutputs o = sim.Hold(30, -0.5);
                AssertView(o, ViewState.Approach, ApproachMode.Prime, "site " + g.Item1 + " broken");
                Assert.AreEqual(g.Item4, o.Blocked, "site " + g.Item1 + ": the only failing guard (0x" + o.Blocked.ToString("x") + ")");
                Assert.IsTrue(o.Picture, "site " + g.Item1 + ": the picture stays (no swap at the waterline)");
                g.Item3(sim);
                int steps = sim.StepUntil(-0.5, x => x.ViewState == ViewState.Underwater, 10);
                Assert.IsTrue(steps <= 2, "site " + g.Item1 + " mended: Underwater within 2 ticks (took " + steps + ")");
                AssertNoOrderViolation(sim);
            }
            var latchOff = new ViewSim { Vy = 0.0, MarginsReady = false };
            ViewOutputs lo = latchOff.Hold(60, 0.5);
            Assert.IsFalse(lo.Picture, "site: margins never presented, no latch");
            Assert.AreEqual(ViewBlock.Render, lo.PictureBlocked, "site: the margins are the only failing latch guard");
            lo = latchOff.Hold(30, -0.5);
            AssertView(lo, ViewState.Approach, ApproachMode.Prime, "site render (latch off)");
            Assert.AreEqual(ViewBlock.Render, lo.Blocked, "site: latch off is the only failing Underwater guard");
            latchOff.MarginsReady = true;
            int ls = latchOff.StepUntil(-0.5, x => x.ViewState == ViewState.Underwater, 10);
            Assert.AreEqual(2, ls, "site: latch on the first tick (no earlier latch change: no dwell), Underwater the next");
            AssertNoOrderViolation(latchOff);
        }

        [TestMethod]
        public void PrimeToUnderwater_NeedsYAtMostMinus020()
        {
            ViewSim sim = ViewSim.Primed();
            sim.Vy = 0.0;
            ViewOutputs o = sim.Hold(60, -0.19);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "-0.19 m");
            Assert.AreEqual(ViewBlock.Y, o.Blocked);
            Assert.AreEqual(0ul, o.CrossingTick, "no crossing above -0.20 m");
            o = sim.Step(-0.20);
            Assert.AreEqual(sim.Log.Last().Tick, o.CrossingTick, "exactly -0.20 m is a crossing");
            AssertView(sim.Step(-0.20), ViewState.Underwater, ApproachMode.Prime, "-0.20 m, presented the next tick");
        }

        [TestMethod]
        public void PrimeToUnderwater_NeverInDisplayModeC_AndNotFromTrack()
        {
            var sim = new ViewSim { DisplayMode = DisplayMode.C, Vy = 0.0 };
            ViewOutputs o = sim.Hold(120, -20.0);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "mode C");
            Assert.AreNotEqual(0u, o.Blocked & ViewBlock.Mode);
            Assert.IsFalse(o.MarginsOn || o.SceneSkip || o.SeLowered || o.AlphaFill, "mode C requests nothing");

            // the first Prime tick never goes Underwater (the margins-on request was only just issued)
            sim = new ViewSim { Vy = 0.0 };
            o = sim.Step(-20.0);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "first tick");
            Assert.AreNotEqual(0u, o.Blocked & ViewBlock.NotPrime);
        }

        // ------------------------------------------------------------------------------------------- Underwater → Surfacing → Prime/Track
        [TestMethod]
        public void UnderwaterToSurfacing_AboveMinus005_OnlyAfterTheDwell()
        {
            ViewSim sim = ViewSim.Primed(0.5, ViewPicture.Waterline);   // S2 policy, the fallback (Surfacing draws SE's scene)
            sim.Vy = 0.0;
            sim.StepUntil(-0.5, o => o.ViewState == ViewState.Underwater);
            ulong entry = sim.Log.Last().Tick;
            int steps = sim.StepUntil(0.0, o => o.ViewState == ViewState.Surfacing);
            Assert.AreEqual(15, steps, "Surfacing at the first tick 0.25 s after the switch (15 x 16,667 us)");
            Assert.AreEqual(entry + 15, sim.Log.Last().Tick);
            ViewOutputs s = sim.Last;
            Assert.AreEqual(ViewReason.SurfaceWaterline, s.Reason);
            Assert.IsFalse(s.SceneSkip, "scene drawn first");
            Assert.IsTrue(s.MarginsOn && s.AlphaFill, "margins stay, alpha filled");
            Assert.AreEqual(PerfMode.Full, s.PerfMode, "Full until the next state");
            ViewOutputs p = sim.Step(0.0);
            AssertView(p, ViewState.Approach, ApproachMode.Prime, "after the scene-drawn frame was presented");
            Assert.AreEqual(ViewReason.SurfacedToPrime, p.Reason);
            Assert.IsTrue(p.MarginsOn, "margins stay on in Prime");
            AssertNoOrderViolation(sim);

            // exactly -0.05 m is not above the exit threshold
            sim = ViewSim.Underwater();
            AssertView(sim.Hold(120, -0.05), ViewState.Underwater, ApproachMode.Prime, "-0.05 m for 2 s");

            // S7a Site: the same thresholds and dwell; Surfacing keeps Subnautica's picture (no swap at the waterline) and
 // ends as soon as its unchanged request is acknowledged: one tick, then Prime with the picture still on
            ViewSim site = ViewSim.Primed();
            site.Vy = 0.0;
            site.StepUntil(-0.5, o => o.ViewState == ViewState.Underwater);
            Assert.AreEqual(15, site.StepUntil(0.0, o => o.ViewState == ViewState.Surfacing), "site: Surfacing after the same 0.25 s dwell");
            ViewOutputs ss = site.Last;
            Assert.AreEqual(ViewReason.SurfaceWaterline, ss.Reason);
            Assert.IsTrue(ss.SceneSkip && ss.MarginsOn && ss.Picture && ss.PictureEdge == 0, "site Surfacing: still Subnautica's picture");
            Assert.AreEqual(0f, ss.SnBusTarget, "the audio leaves the underwater mix as in S2");
            ViewOutputs sp = site.Step(0.0);
            AssertView(sp, ViewState.Approach, ApproachMode.Prime, "site: Surfacing lasts one tick");
            Assert.AreEqual(ViewReason.SurfacedToPrime, sp.Reason);
            Assert.IsTrue(sp.Picture && sp.SceneSkip, "site Prime after surfacing: the picture stays");
            Assert.AreEqual(0, ViewSim.PictureEdges(site.Log).Count, "no picture edge through the dive and the surfacing");
            AssertNoOrderViolation(site);
        }

        [TestMethod]
        public void Surfacing_MarginsOffOnlyWhenLeavingPrime_SceneFirst()
        {
            ViewSim sim = ViewSim.Primed(0.5, ViewPicture.Waterline);   // S2 policy, the fallback
            sim.Vy = 0.0;
            sim.Hold(60, -2.0);
            Assert.AreEqual(ViewState.Underwater, sim.Last.ViewState);
            sim.Hold(30, 6.0);                                   // surface, Prime again
            sim.Hold(200, 6.0);                                  // 3 s quiet above +5 m → Track
            Assert.AreEqual(ApproachMode.Track, sim.Last.ApproachMode);
            int firstSkip = sim.Log.FindIndex(r => r.O.SceneSkip);
            int lastSkip = sim.Log.FindLastIndex(r => r.O.SceneSkip);
            int marginsOff = sim.Log.FindIndex(r => !r.O.MarginsOn);
            int primeEnd = sim.Log.FindIndex(r => r.O.ApproachMode == ApproachMode.Track);
            Assert.IsTrue(firstSkip > 0 && lastSkip >= firstSkip);
            Assert.IsTrue(sim.Log.Take(marginsOff).All(r => r.O.MarginsOn), "margins on from Prime through Underwater, Surfacing and Prime");
            Assert.AreEqual(primeEnd, marginsOff, "margins go off on the tick Prime ends, not before");
            Assert.IsTrue(marginsOff > lastSkip + 1, "scene drawn and presented before margins off");
            Assert.IsTrue(sim.Log[marginsOff].O.AlphaFill, "alpha fill until the margins-off request is presented");
            Assert.IsFalse(sim.Log[marginsOff + 1].O.AlphaFill, "then off");
            AssertNoOrderViolation(sim);

            // S7a Site: the margins stay through Underwater, Surfacing, Prime and Track (every non-Space state) and the
 // scene stays skipped (the latch); they go only after the climb to Space, once a scene-drawn frame is presented
            ViewSim site = ViewSim.Primed();
            site.Vy = 0.0;
            site.Hold(60, -2.0);
            Assert.AreEqual(ViewState.Underwater, site.Last.ViewState);
            site.Hold(30, 6.0);
            site.Hold(200, 6.0);
            Assert.AreEqual(ApproachMode.Track, site.Last.ApproachMode);
            Assert.IsTrue(site.Log.All(r => r.O.MarginsOn && r.O.SceneSkip && r.O.Picture), "site: margins and skip from Prime to Track");
            site.Ramp(120, 6.0, 400.0);
            site.Hold(90, 400.0);   // F15: the altitude exit holds 1 s
            int spaceAt = site.Log.FindIndex(r => r.O.ViewState == ViewState.Space);
            int mOff = site.Log.FindIndex(r => !r.O.MarginsOn);
            Assert.IsTrue(spaceAt > 0 && !site.Log[spaceAt].O.SceneSkip && site.Log[spaceAt].O.MarginsOn, "site: Space draws the scene first");
            Assert.IsTrue(mOff > spaceAt, "site: margins off after the scene-drawn frame (" + spaceAt + " -> " + mOff + ")");
            AssertNoOrderViolation(site);
        }

        [TestMethod]
        public void CrossingOrder_HoldsOverRandomDivesAndRenderLatencies()
        {
            var rnd = new Random(4546);
            for (int run = 0; run < 12; run++)
            {
                // S2 policy, the fallback (the per-row pins below are S2's picture)
                var sim = new ViewSim { AckLag = 1 + rnd.Next(4), SnLag = 1 + rnd.Next(3), Picture = ViewPicture.Waterline };
                double y = 5.0, target = -1.0;
                for (int k = 0; k < 6000; k++)
                {
                    if (rnd.NextDouble() < 0.01) target = -1.0 + rnd.NextDouble() * 2.0;
                    if (rnd.NextDouble() < 0.002) sim.Peer = sim.Peer == PeerState.Alive ? PeerState.Stalled : PeerState.Alive;
                    if (rnd.NextDouble() < 0.002) sim.ZOrderOk = !sim.ZOrderOk;
                    if (rnd.NextDouble() < 0.001) sim.Teleport(rnd.Next(10, 120));
                    if (rnd.NextDouble() < 0.003) sim.AckLag = 1 + rnd.Next(4);
                    y += Math.Max(-0.05, Math.Min(0.05, target - y)) + (rnd.NextDouble() - 0.5) * 0.02;
                    sim.Step(y);
                }
                AssertNoOrderViolation(sim);
                foreach (ViewRec r in sim.Log)
                {
                    Assert.AreEqual(r.O.ViewState == ViewState.Underwater, r.O.SceneSkip, "F: skip exactly while Underwater (tick " + r.Tick + ")");
                    Assert.AreEqual(ViewStateMachine.IsPrimeLike(r.O.ViewState, r.O.ApproachMode), r.O.MarginsOn, "F: margins exactly in Prime/Underwater/Surfacing");
                }
                List<ViewRec> sw = ViewSim.Switches(sim.Log);
                long minUs; int perS;
                ViewSim.N2Metrics(sw, out minUs, out perS);
                Assert.IsTrue(sw.Count > 4, "run " + run + " dived (" + sw.Count + " switches)");
            }

            // S7a Site: the same random dives keep the crossing order; the skip is exactly the picture latch, the margins
 // are on in every non-Space state (and in Space only until the drawn frame after a skip is presented)
            rnd = new Random(4547);
            for (int run = 0; run < 12; run++)
            {
                var sim = new ViewSim { AckLag = 1 + rnd.Next(4), SnLag = 1 + rnd.Next(3) };
                double y = 5.0, target = -1.0;
                for (int k = 0; k < 6000; k++)
                {
                    if (rnd.NextDouble() < 0.01) target = -1.0 + rnd.NextDouble() * 2.0;
                    if (rnd.NextDouble() < 0.002) sim.Peer = sim.Peer == PeerState.Alive ? PeerState.Stalled : PeerState.Alive;
                    if (rnd.NextDouble() < 0.002) sim.ZOrderOk = !sim.ZOrderOk;
                    if (rnd.NextDouble() < 0.001) sim.Teleport(rnd.Next(10, 120));
                    if (rnd.NextDouble() < 0.003) sim.AckLag = 1 + rnd.Next(4);
                    y += Math.Max(-0.05, Math.Min(0.05, target - y)) + (rnd.NextDouble() - 0.5) * 0.02;
                    sim.Step(y);
                }
                AssertNoOrderViolation(sim);
                foreach (ViewRec r in sim.Log)
                {
                    Assert.AreEqual(r.O.Picture, r.O.SceneSkip, "site F: skip exactly with the picture (tick " + r.Tick + ")");
                    if (r.O.ViewState != ViewState.Space) Assert.IsTrue(r.O.MarginsOn, "site F: margins in every non-Space state (tick " + r.Tick + ")");
                    if (r.O.ViewState == ViewState.Underwater) Assert.IsTrue(r.O.Picture, "site: Underwater only under the latch (tick " + r.Tick + ")");
                }
                Assert.IsTrue(ViewSim.Switches(sim.Log).Count > 4, "site run " + run + " dived");
            }
        }

        // ------------------------------------------------------------------------------------------- zone, peer, teleport
        [TestMethod]
        public void ZoneExitAt7900_SurfacesAtOnce_SeViewBeyond_ReentryAt7800()
        {
            ViewSim sim = ViewSim.Underwater();
            sim.X = 7700.0;
            AssertView(sim.Hold(30, -20.0), ViewState.Underwater, ApproachMode.Prime, "7,700 m");
            sim.X = 7900.0;
            AssertView(sim.Hold(30, -20.0), ViewState.Underwater, ApproachMode.Prime, "7,900 m (zone left only beyond)");
            sim.X = 7900.001;
            ViewOutputs o = sim.Step(-20.0);
            AssertView(o, ViewState.Surfacing, ApproachMode.Prime, "7,900.001 m");
            Assert.AreEqual(ViewReason.SurfaceSite, o.Reason);
            Assert.IsFalse(o.SceneSkip, "SE draws its own view at once");
            o = sim.Step(-20.0);
            AssertView(o, ViewState.Space, ApproachMode.None, "beyond 7,900 m: SE's Water Mod view");
            Assert.IsFalse(o.MarginsOn);
            Assert.AreEqual(PerfMode.Idle, o.PerfMode);
            sim.X = 7850.0;
            AssertView(sim.Hold(60, -20.0), ViewState.Space, ApproachMode.None, "7,850 m from outside (FAR, hysteresis)");
            Assert.IsTrue(sim.Log.Where(r => r.Zone == Zone.Far).All(r => !r.O.SceneSkip && r.O.ViewState != ViewState.Underwater));
            sim.X = 7800.0;
            o = sim.Step(-20.0);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "7,800 m: OUTER VOID, Prime at once (y <= +1)");
            sim.StepUntil(-20.0, x => x.ViewState == ViewState.Underwater, 5);
            AssertNoOrderViolation(sim);

            // no dwell for a zone exit: right after entering
            sim = ViewSim.Primed();
            sim.Vy = 0.0;
            sim.StepUntil(-0.5, x => x.ViewState == ViewState.Underwater);
            sim.X = 8000.0;
            o = sim.Step(-0.5);
            AssertView(o, ViewState.Surfacing, ApproachMode.Prime, "zone exit one tick after entering");
        }

        [TestMethod]
        public void PeerStalledOrExited_UnderwaterSurfacesWithinOneTick()
        {
            foreach (uint peer in new[] { PeerState.Stalled, PeerState.Exited })
            {
                ViewSim sim = ViewSim.Underwater();
                sim.Hold(30, -20.0);
                sim.Peer = peer;
                ViewOutputs o = sim.Step(-20.0);
                AssertView(o, ViewState.Surfacing, ApproachMode.Prime, "peer " + peer + ", same tick");
                Assert.AreEqual(ViewReason.SurfacePeer, o.Reason);
                Assert.IsFalse(o.SceneSkip, "scene drawn on the same tick");
                Assert.AreEqual(0f, o.SnBusTarget, "Subnautica's bus target drops at once");
                Assert.AreEqual(1.0, o.MixLevel, 1e-12, "the SE crossfade back starts on this tick");
                Assert.AreNotEqual(0u, o.Overlays & (Overlays.PeerStalled | Overlays.PeerExited));
                o = sim.Step(-20.0);
                AssertView(o, ViewState.Space, ApproachMode.None, "next tick: SE's view, margins off");
                Assert.IsFalse(o.MarginsOn);
                Assert.IsTrue(o.MixLevel < 1.0, "audio restoring");
                Assert.AreEqual(PerfMode.Idle, o.PerfMode);
                AssertNoOrderViolation(sim);
            }

            // in Prime a peer loss goes to Space at once, margins off on the same tick (S2 policy, the fallback: no skip in Prime)
            ViewSim p = ViewSim.Primed(0.5, ViewPicture.Waterline);
            p.Peer = PeerState.Exited;
            ViewOutputs q = p.Step(0.5);
            AssertView(q, ViewState.Space, ApproachMode.None, "Prime, peer exited");
            Assert.IsFalse(q.MarginsOn);

            // S7a Site: in Prime the picture is Subnautica's (latch on, scene skipped): a peer loss drops the latch and goes
 // to Space on the same tick, SE's scene is drawn on that tick, the margins go on the next (after that frame)
            ViewSim ps = ViewSim.Primed();
            Assert.IsTrue(ps.State.Picture && (ps.State.Request & ViewRequest.SceneSkip) != 0, "site setup: Prime under the latch");
            ps.Peer = PeerState.Exited;
            ViewOutputs qs = ps.Step(0.5);
            AssertView(qs, ViewState.Space, ApproachMode.None, "site Prime, peer exited");
            Assert.IsTrue(!qs.Picture && qs.PictureEdge == -1 && !qs.SceneSkip && qs.MarginsOn, "site: scene drawn first, margins kept one frame");
            Assert.AreEqual(ViewReason.PictureOffPeer, qs.PictureReason);
            Assert.IsFalse(ps.Step(0.5).MarginsOn, "site: margins off after the scene-drawn frame");
            AssertNoOrderViolation(ps);
            // and under water: Surfacing at once with the latch off (S2's Surfacing), Space on the next tick
            ViewSim us = ViewSim.Underwater();
            us.Peer = PeerState.Stalled;
            ViewOutputs u1 = us.Step(-20.0);
            AssertView(u1, ViewState.Surfacing, ApproachMode.Prime, "site underwater, peer stalled");
            Assert.IsTrue(!u1.Picture && !u1.SceneSkip && u1.MarginsOn, "site: latch off, scene drawn on the same tick");
            AssertView(us.Step(-20.0), ViewState.Space, ApproachMode.None, "site: Space on the next tick");
            AssertNoOrderViolation(us);

            // recovery: link epoch + fresh settle before the next dive
            ViewSim r = ViewSim.Underwater();
            r.Peer = PeerState.Stalled;
            r.Hold(60, -20.0);
            r.Peer = PeerState.Alive;
            r.Event(ViewEvents.LinkRecovery);
            r.SettleAt = r.Tick + 45;
            ulong recovery = r.Tick;
            r.StepUntil(-20.0, x => x.ViewState == ViewState.Underwater, 200);
            Assert.IsTrue(r.Log.Last().Tick >= recovery + 45, "not before Subnautica settled after the recovery");
        }

        [TestMethod]
        public void Teleport_Underwater_SurfacesUntilSettledAfterTheTeleport()
        {
            ViewSim sim = ViewSim.Underwater(-20.0, ViewPicture.Waterline);   // S2 policy, the fallback (exact entry tick)
            ulong e = sim.Tick;
            sim.Teleport(90);
            ViewOutputs o = sim.Step(-40.0);
            AssertView(o, ViewState.Surfacing, ApproachMode.Prime, "teleport tick");
            Assert.AreEqual(ViewReason.SurfaceResettle, o.Reason);
            Assert.AreEqual(e, o.CrossingTick, "crossingTick re-stamped by a teleport below the waterline");
            Assert.IsFalse(o.SnReady);
            for (int k = 1; k < 90; k++)
            {
                ViewOutputs h = sim.Step(-40.0);
                Assert.AreNotEqual(ViewState.Underwater, h.ViewState, "tick e+" + k + ": not settled yet");
            }
            o = sim.Step(-40.0);
            Assert.AreEqual(e + 90, sim.Log.Last().Tick);
            AssertView(o, ViewState.Underwater, ApproachMode.Prime, "settled with areaBuiltSeTick >= the teleport tick");
            AssertNoOrderViolation(sim);

            // S7a Site: the teleport drops the picture latch at once (re-streaming: SE's own view, as S2), the latch is on
 // again on the settled tick and Underwater follows one tick later (Underwater only under the latch)
            ViewSim site = ViewSim.Underwater();
            ulong es = site.Tick;
            site.Teleport(90);
            ViewOutputs t0 = site.Step(-40.0);
            AssertView(t0, ViewState.Surfacing, ApproachMode.Prime, "site teleport tick");
            Assert.IsTrue(!t0.Picture && t0.PictureEdge == -1 && !t0.SceneSkip, "site: the picture off at once");
            Assert.AreEqual(ViewReason.PictureOffResettle, t0.PictureReason);
            for (int k = 1; k < 90; k++) Assert.IsFalse(site.Step(-40.0).Picture, "site tick e+" + k + ": not settled, no picture");
            ViewOutputs t90 = site.Step(-40.0);
            Assert.AreEqual(es + 90, site.Log.Last().Tick);
            Assert.IsTrue(t90.Picture && t90.PictureEdge == 1 && t90.ViewState == ViewState.Approach, "site: latch on when settled");
            AssertView(site.Step(-40.0), ViewState.Underwater, ApproachMode.Prime, "site: Underwater one tick after the latch");
            AssertNoOrderViolation(site);

            // a stale areaBuiltSeTick (before the teleport) with the flags set does not count
            sim = ViewSim.Underwater();
            ulong e2 = sim.Tick;
            sim.Event(ViewEvents.Teleport);
            sim.AreaBuiltOverride = e2 - 10;
            o = sim.Hold(120, -40.0);
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "stale stamp");
            Assert.AreNotEqual(0u, o.Blocked & ViewBlock.Settle);
            sim.AreaBuiltOverride = null;
            sim.StepUntil(-40.0, x => x.ViewState == ViewState.Underwater, 3);
        }

        [TestMethod]
        public void Underwater_OtherSnReadyLapses_WithoutAnEvent_DoNotSurface()
        {
            // ARCHITECTURE §4.2 lists "¬snReady after a teleport" only; a hitch or a rect change alone keeps the view
            ViewSim sim = ViewSim.Underwater();
            sim.SnLag = 12;
            ViewOutputs o = sim.Hold(60, -20.0);
            AssertView(o, ViewState.Underwater, ApproachMode.Prime, "Subnautica 12 ticks late");
            Assert.IsFalse(o.SnReady);
            sim.SnLag = 1;
            sim.RectOk = false;
            AssertView(sim.Hold(60, -20.0), ViewState.Underwater, ApproachMode.Prime, "rect mismatch");
        }

        [TestMethod]
        public void WorldLoad_RestartsInSpace_AndNeedsAFreshSettle()
        {
            ViewSim sim = ViewSim.Underwater(-20.0, ViewPicture.Waterline);   // S2 policy, the fallback (entry within 5 ticks)
            ulong w = sim.Tick;
            sim.Event(ViewEvents.WorldLoad);
            ViewOutputs o = sim.Step(-20.0);
            Assert.AreEqual(ViewReason.EnterPrime, o.Reason, "reset to Space, then Approach/Prime at y -20 on the same tick");
            AssertView(o, ViewState.Approach, ApproachMode.Prime, "after the world load");
            Assert.AreEqual(w, o.CrossingTick, "the latch restarts: the first sample below is a crossing");
            Assert.IsFalse(o.SnReady, "a world load needs a fresh streamingSettled");
            sim.StepUntil(-20.0, x => x.ViewState == ViewState.Underwater, 5);

            // S7a Site: the world load drops the picture latch (re-streaming) with the same reset; the latch waits for the
 // fresh settle and the 0.25 s picture dwell since that drop, then Underwater one tick later
            ViewSim site = ViewSim.Underwater();
            site.Event(ViewEvents.WorldLoad);
            ViewOutputs so = site.Step(-20.0);
            AssertView(so, ViewState.Approach, ApproachMode.Prime, "site after the world load");
            Assert.IsTrue(!so.Picture && so.PictureEdge == -1, "site: the world load drops the picture");
            Assert.AreEqual(ViewReason.PictureOffResettle, so.PictureReason);
            int n = site.StepUntil(-20.0, x => x.ViewState == ViewState.Underwater, 30);
            Assert.AreEqual(16, n, "site: latch on after the 0.25 s dwell (15 ticks), Underwater on the next");
        }

        // ------------------------------------------------------------------------------------------- crossingTick, z-order, perf
        [TestMethod]
        public void CrossingTick_StampedOnDownwardCrossingsOnly()
        {
            ViewSim sim = ViewSim.Primed();
            sim.Vy = 0.0;
            sim.Ramp(10, 0.5, -0.1);
            Assert.AreEqual(0ul, sim.Last.CrossingTick, "inside the band from above: no crossing");
            sim.Step(-0.2);
            ulong c1 = sim.Log.Last().Tick;
            Assert.AreEqual(c1, sim.Last.CrossingTick);
            sim.Hold(30, -0.1);
            sim.Step(-0.3);
            Assert.AreEqual(c1, sim.Last.CrossingTick, "bobbing inside the band does not re-stamp");
            sim.Hold(30, -0.04);
            sim.Step(-0.25);
            ulong c2 = sim.Log.Last().Tick;
            Assert.AreEqual(c2, sim.Last.CrossingTick, "a new crossing after rising above -0.05 m");
            sim.Hold(10, -0.25);
            sim.Teleport(1);
            sim.Step(-0.3);
            Assert.AreEqual(sim.Log.Last().Tick, sim.Last.CrossingTick, "a teleport below the waterline re-stamps");
        }

        [TestMethod]
        public void ZOrderReassert_PulsesAtPrimeEntryRefocusAndLoss()
        {
            var sim = new ViewSim { Vy = 0.0, Picture = ViewPicture.Waterline };   // S2 policy, the fallback
            Assert.IsFalse(sim.Step(100.0).ZOrderReassert, "Track");
            Assert.IsTrue(sim.Step(0.5).ZOrderReassert, "Prime entry");
            Assert.IsFalse(sim.Step(0.5).ZOrderReassert, "Prime held");
            sim.Foreground = false;
            Assert.IsFalse(sim.Step(0.5).ZOrderReassert, "focus lost");
            sim.Foreground = true;
            Assert.IsTrue(sim.Step(0.5).ZOrderReassert, "SE re-activated");
            sim.ZOrderOk = false;
            Assert.IsTrue(sim.Step(0.5).ZOrderReassert, "walker lost the order");
            Assert.IsFalse(sim.Step(0.5).ZOrderReassert, "no repeat while still lost (the unit retries itself)");
            sim.ZOrderOk = true;
            Assert.IsFalse(sim.Step(0.5).ZOrderReassert, "restored");
            sim.Hold(200, 6.0);                                  // 3 s quiet above +5 m → Track
            Assert.AreEqual(ApproachMode.Track, sim.Last.ApproachMode);
            sim.Foreground = false; sim.Step(6.0);
            sim.Foreground = true;
            Assert.IsFalse(sim.Step(6.0).ZOrderReassert, "re-activation in Track: nothing to place");

            // S7a Site: the margins are wanted from the Approach entry (MarginsWanted), so the pulse fires there, not at
 // the Prime entry; a re-activation in Track re-places the windows (Subnautica is composed under SE there)
            var site = new ViewSim { Vy = 0.0 };
            Assert.IsTrue(site.Step(100.0).ZOrderReassert, "site: Approach entry");
            Assert.IsFalse(site.Step(100.0).ZOrderReassert, "site: Track held");
            Assert.IsFalse(site.Step(0.5).ZOrderReassert, "site: Prime entry (margins already wanted)");
            site.Hold(200, 6.0);
            Assert.AreEqual(ApproachMode.Track, site.Last.ApproachMode);
            site.Foreground = false; site.Step(6.0);
            site.Foreground = true;
            Assert.IsTrue(site.Step(6.0).ZOrderReassert, "site: re-activation in Track");
            site.ZOrderOk = false;
            Assert.IsTrue(site.Step(6.0).ZOrderReassert, "site: walker lost the order in Track");
            Assert.IsTrue(ViewStateMachine.MarginsWanted(ViewState.Approach, ApproachMode.Track, ViewPicture.Site));
            Assert.IsFalse(ViewStateMachine.MarginsWanted(ViewState.Approach, ApproachMode.Track, ViewPicture.Waterline));
            Assert.IsFalse(ViewStateMachine.MarginsWanted(ViewState.Space, ApproachMode.None, ViewPicture.Site));
            foreach (uint v in new[] { ViewState.Underwater, ViewState.Surfacing })
                Assert.IsTrue(ViewStateMachine.MarginsWanted(v, ApproachMode.Prime, ViewPicture.Waterline) &&
                              ViewStateMachine.MarginsWanted(v, ApproachMode.Prime, ViewPicture.Site));
        }

        [TestMethod]
        public void PerfModeAndTargetFps_PerState()
        {
            var sim = new ViewSim { Vy = 0.0, Picture = ViewPicture.Waterline };   // S2 policy, the fallback
            ViewOutputs o = sim.Step(400.0);
            Assert.AreEqual(PerfMode.Idle, o.PerfMode); Assert.AreEqual(ViewThresholds.IdleTargetFps, o.TargetFps);
            o = sim.Step(100.0);
            Assert.AreEqual(PerfMode.Track, o.PerfMode); Assert.AreEqual(ViewThresholds.TrackTargetFps, o.TargetFps);
            o = sim.Step(0.5);
            Assert.AreEqual(PerfMode.Full, o.PerfMode); Assert.AreEqual(ViewThresholds.FullTargetFps, o.TargetFps);
            sim.StepUntil(-1.0, x => x.ViewState == ViewState.Underwater);
            o = sim.Last;
            Assert.AreEqual(PerfMode.Full, o.PerfMode); Assert.AreEqual(0u, o.TargetFps);
            sim.Foreground = false;
            o = sim.Step(-1.0);
            Assert.AreEqual(PerfMode.Full, o.PerfMode); Assert.AreEqual(30u, o.TargetFps, "unfocused underwater (0, 30)");
            sim.Foreground = true;
            sim.Peer = PeerState.Stalled;
            o = sim.Step(-1.0);
            Assert.AreEqual(ViewState.Surfacing, o.ViewState); Assert.AreEqual(PerfMode.Full, o.PerfMode, "Surfacing: Full until the next state");

            // S7a Site: Idle in Space, Full in every other state (Track included: Subnautica renders whenever its picture
 // can be shown, so the swap at the band never waits for a recipe change); 30 fps while the picture is shown
 // and SE is unfocused, in Approach too
            var site = new ViewSim { Vy = 0.0 };
            o = site.Step(400.0);
            Assert.AreEqual(PerfMode.Idle, o.PerfMode); Assert.AreEqual(ViewThresholds.IdleTargetFps, o.TargetFps);
            o = site.Step(100.0);
            Assert.AreEqual(ApproachMode.Track, o.ApproachMode);
            Assert.AreEqual(PerfMode.Full, o.PerfMode, "site Track: Full"); Assert.AreEqual(ViewThresholds.FullTargetFps, o.TargetFps);
            site.Foreground = false;
            o = site.Step(100.0);
            Assert.IsTrue(o.Picture, "site: the latch is on in Track");
            Assert.AreEqual(ViewThresholds.UnfocusedUnderwaterFps, o.TargetFps, "site: unfocused with the picture shown (Track) -> 30");
            site.Foreground = true;
            o = site.Step(0.5);
            Assert.AreEqual(PerfMode.Full, o.PerfMode); Assert.AreEqual(0u, o.TargetFps);
            site.StepUntil(-1.0, x => x.ViewState == ViewState.Underwater);
            Assert.AreEqual(PerfMode.Full, site.Last.PerfMode);
            for (uint v = ViewState.Space; v <= ViewState.Surfacing; v++)
                for (uint a = ApproachMode.None; a <= ApproachMode.Prime; a++)
                {
                    Assert.AreEqual(v == ViewState.Space ? PerfMode.Idle : PerfMode.Full, ViewStateMachine.PerfFor(v, a, ViewPicture.Site), "site " + v + "/" + a);
                    uint s2 = v == ViewState.Space ? PerfMode.Idle : v == ViewState.Approach && a == ApproachMode.Track ? PerfMode.Track : PerfMode.Full;
                    Assert.AreEqual(s2, ViewStateMachine.PerfFor(v, a, ViewPicture.Waterline), "waterline " + v + "/" + a);
                }
        }

        // ------------------------------------------------------------------------------------------- overlays and audio
        [TestMethod]
        public void AudioWrites_GatedByEscForegroundGuiAndLoading()
        {
            ViewSim sim = ViewSim.Underwater();
            ViewOutputs o = sim.Hold(30, -20.0);
            Assert.IsTrue(o.SeAudioWrite, "focused, unpaused, gameplay: the plugin writes its mix");
            Assert.AreEqual(1f, o.SnBusTarget);
            Assert.AreEqual(0.7f, o.SeGameFactor, 1e-6f);
            Assert.AreEqual(0f, o.SeMusicFactor);
            Assert.AreEqual(0f, o.WaterModFactor);
            Assert.IsTrue(o.SeMixActive);
            Assert.AreEqual(SeStateFlags.SeMixActive, o.C5Flags & SeStateFlags.SeMixActive);

            sim.Foreground = false;                                     // Alt+Tab
            o = sim.Step(-20.0);
            Assert.IsFalse(o.SeAudioWrite, "unfocused: SE mutes itself, the plugin writes nothing");
            Assert.AreEqual(0f, o.SnBusTarget, "Subnautica muted while SE is not foreground");
            Assert.AreEqual(Overlays.Unfocused, o.Overlays);
            Assert.AreEqual(0u, o.C5Flags & SeStateFlags.SeForeground);
            Assert.AreEqual(ViewState.Underwater, o.ViewState, "no state change");
            sim.Foreground = true;
            o = sim.Step(-20.0);
            Assert.IsTrue(o.SeAudioWrite && o.ZOrderReassert, "refocus: mix re-applied, z-order re-asserted");
            Assert.AreEqual(1f, o.SnBusTarget);

            sim.Paused = true;                                          // ESC
            o = sim.Step(-20.0);
            Assert.IsFalse(o.SeAudioWrite, "paused: no SE volume write");
            Assert.IsTrue(o.Freeze, "paused: Subnautica frozen");
            Assert.AreEqual(Overlays.Paused, o.Overlays);
            Assert.AreEqual(SeStateFlags.SePaused, o.C5Flags & SeStateFlags.SePaused);
            o = sim.Hold(600, -20.0);
            AssertView(o, ViewState.Underwater, ApproachMode.Prime, "10 s paused underwater: no state change");
            sim.Paused = false;
            Assert.IsFalse(sim.Step(-20.0).Freeze);

            sim.Gui = true;                                             // inventory, terminal
            o = sim.Step(-20.0);
            Assert.IsFalse(o.SeAudioWrite, "GUI screen open: no SE volume write");
            Assert.IsFalse(o.Freeze, "a GUI screen does not freeze Subnautica (only Idle or Paused)");
            Assert.AreEqual(SeStateFlags.GuiScreenOpen, o.C5Flags & SeStateFlags.GuiScreenOpen);
            sim.Gui = false;
            sim.Loading = true;
            Assert.IsFalse(sim.Step(-20.0).SeAudioWrite, "loading: no SE volume write");
            sim.Loading = false;
            Assert.IsTrue(sim.Step(-20.0).SeAudioWrite);

            // does not apply; Subnautica still mutes (ARCHITECTURE §5.3)
            var keep = ViewSim.Underwater();
            keep.Foreground = false;
            Assert.IsFalse(keep.Step(-20.0).SeAudioWrite);
            keep.KeepAudibleUnfocused = true;
            o = keep.Step(-20.0);
            Assert.IsTrue(o.SeAudioWrite, "EnableMuteWhenNotInFocus off: the plugin keeps writing its mix unfocused");
            Assert.AreEqual(0f, o.SnBusTarget, "Subnautica still mutes");
            keep.Paused = true;
            Assert.IsFalse(keep.Step(-20.0).SeAudioWrite, "paused still closes the gate");

            // S2.M11 seeded failure: written without the gate (SE audible while away)
            sim.Mutants = ViewMutants.M11NoAudioGate;
            sim.Foreground = false;
            Assert.IsTrue(sim.Step(-20.0).SeAudioWrite, "S2.M11: written although unfocused");
            sim.Mutants = 0;
            Assert.IsFalse(sim.Step(-20.0).SeAudioWrite);

            // above water: config volumes, Subnautica muted, the gate still applies
            var space = new ViewSim { Vy = 0.0 };
            o = space.Step(400.0);
            Assert.IsTrue(o.SeAudioWrite && o.Freeze && !o.SeMixActive);
            Assert.AreEqual(0f, o.SnBusTarget);
            Assert.AreEqual(1f, o.SeGameFactor);
            Assert.AreEqual(1f, o.SeMusicFactor);
            Assert.AreEqual(1f, o.WaterModFactor);
        }

        [TestMethod]
        public void Crossfade_Linear300ms_BothDirections_NoJump()
        {
            ViewSim sim = ViewSim.Primed();
            sim.Vy = 0.0;
            sim.StepUntil(-0.5, o => o.ViewState == ViewState.Underwater);
            ViewOutputs e = sim.Last;
            Assert.AreEqual(0.0, e.MixLevel, "the ramp starts at the switch");
            Assert.AreEqual(1f, e.SnBusTarget, "Subnautica's target flips at once (it fades itself over crossfadeMs)");
            Assert.AreEqual(300f, e.CrossfadeMs);
            ViewOutputs half = sim.Hold(9, -0.5);                      // 150,003 us
            Assert.AreEqual(150003.0 / 300000.0, half.MixLevel, 1e-12);
            Assert.AreEqual(1.0 - 0.3 * half.MixLevel, half.SeGameFactor, 1e-6);
            Assert.AreEqual(1.0 - half.MixLevel, half.SeMusicFactor, 1e-6);
            Assert.AreEqual(1.0 - half.MixLevel, half.WaterModFactor, 1e-6);
            Assert.IsTrue(half.SeMixActive);
            ViewOutputs full = sim.Hold(9, -0.5);                      // 300,006 us
            Assert.AreEqual(1.0, full.MixLevel);
            Assert.AreEqual(0.7f, full.SeGameFactor, 1e-6f);

            // reversal in mid-ramp: from where it is, no jump
            sim = ViewSim.Primed();
            sim.Vy = 0.0;
            sim.StepUntil(-0.5, o => o.ViewState == ViewState.Underwater);
            sim.Hold(9, -0.5);
            double before = sim.Last.MixLevel;
            sim.Peer = PeerState.Stalled;
            ViewOutputs r = sim.Step(-0.5);
            Assert.AreEqual(ViewState.Surfacing, r.ViewState);
            Assert.AreEqual(before + 16667.0 / 300000.0, r.MixLevel, 1e-9, "the reversal tick still holds the level reached");
            double prev = r.MixLevel;
            for (int k = 0; k < 30; k++)
            {
                double m = sim.Step(-0.5).MixLevel;
                Assert.IsTrue(m <= prev && prev - m <= 16667.0 / 300000.0 + 1e-12, "monotonic, at most one tick's slope");
                prev = m;
            }
            Assert.AreEqual(0.0, prev, "back at the config mix");
            Assert.IsFalse(sim.Last.SeMixActive);
        }

        // ------------------------------------------------------------------------------------------- S2.N2 traces and S2.M4
        private static List<ViewRec> BobFromAbove(uint mutants, double hz, out ViewSim sim)
        {
            sim = new ViewSim { Mutants = mutants };
            sim.Hold(60, 2.0);
            sim.Ramp(120, 2.0, -0.125);
            int start = sim.Log.Count;
            double phase0 = sim.Tick / 60.0;
            sim.Run(1800, s => -0.125 + 0.05 * Math.Sin(2.0 * Math.PI * hz * (s - phase0)));
            return ViewSim.Switches(sim.Log, start);
        }

        private static List<ViewRec> BobFromBelow(uint mutants, double hz, out ViewSim sim)
        {
            sim = new ViewSim { Mutants = mutants };
            sim.Hold(60, -1.0);
            Assert.AreEqual(ViewState.Underwater, sim.Last.ViewState, "setup below");
            sim.Ramp(60, -1.0, -0.125);
            int start = sim.Log.Count;
            double phase0 = sim.Tick / 60.0;
            sim.Run(1800, s => -0.125 + 0.05 * Math.Sin(2.0 * Math.PI * hz * (s - phase0)));
            return ViewSim.Switches(sim.Log, start);
        }

        private static List<ViewRec> Bob5Hz(uint mutants, out ViewSim sim)
        {
            sim = new ViewSim { Mutants = mutants };
            sim.Hold(60, -1.0);
            int start = sim.Log.Count;
            double phase0 = sim.Tick / 60.0;
            sim.Run(1800, s => -0.125 + 0.1 * Math.Sin(2.0 * Math.PI * 5.0 * (s - phase0)));
            return ViewSim.Switches(sim.Log, start);
        }

        [TestMethod]
        public void S2N2_TraceA_BobbingInsideTheBand_ZeroSwitches()
        {
            foreach (double hz in new[] { 0.25, 0.5, 2.0, 5.0 })
            {
                ViewSim sim;
                List<ViewRec> sw = BobFromAbove(0, hz, out sim);
                Assert.AreEqual(0, sw.Count, "from above at " + hz + " Hz");
                Assert.AreEqual(ViewState.Approach, sim.Last.ViewState, "stays on SE's view (Prime)");
                AssertNoOrderViolation(sim);
                sw = BobFromBelow(0, hz, out sim);
                Assert.AreEqual(0, sw.Count, "from below at " + hz + " Hz");
                Assert.AreEqual(ViewState.Underwater, sim.Last.ViewState, "stays underwater");
                AssertNoOrderViolation(sim);
            }
        }

        [TestMethod]
        public void S2N2_TraceB_5Hz_SpacingAtLeast250ms_AtMost4PerSecond()
        {
            ViewSim sim;
            List<ViewRec> sw = Bob5Hz(0, out sim);
            long minUs; int perS;
            ViewSim.N2Metrics(sw, out minUs, out perS);
            Assert.IsTrue(sw.Count > 20, "the trace does cross the band (" + sw.Count + " switches)");
            Assert.IsTrue(minUs >= ViewThresholds.MinDwellUs, "min spacing " + minUs + " us");
            Assert.IsTrue(perS <= 4, "max per second " + perS);
            AssertNoOrderViolation(sim);
        }

        [TestMethod]
        public void S2M4_NoHysteresisNoDwell_TurnsBothTracesRed()
        {
            ViewSim sim;
            List<ViewRec> a = BobFromAbove(ViewMutants.M4NoHysteresis, 0.5, out sim);
            Assert.IsTrue(a.Count > 0, "S2.M4 trace (a): switches inside the band (" + a.Count + ")");
            AssertNoOrderViolation(sim);   // the mutant breaks the hysteresis, not the crossing order
            List<ViewRec> b = Bob5Hz(ViewMutants.M4NoHysteresis, out sim);
            long minUs; int perS;
            ViewSim.N2Metrics(b, out minUs, out perS);
            Assert.IsTrue(minUs < ViewThresholds.MinDwellUs || perS > 4,
                          "S2.M4 trace (b) red: min spacing " + minUs + " us, " + perS + " per second");
        }

        /// <summary>A 5 m/s vertical dive from +20 m to −20 m with a Subnautica that is never "settled" while moving.</summary>
        private static ViewSim Dive5(uint mutants)
        {
            var sim = new ViewSim { Mutants = mutants, SettledOnlyWhenStill = true };
            sim.Hold(120, 20.0);
            sim.Ramp(480, 20.0, -20.0);                      // 5 m/s
            sim.Hold(120, -20.0);
            return sim;
        }

        [TestMethod]
        public void S2N7b_MovingDive5ms_EntersWithinHalfASecond_AndS2M10DelaysIt()
        {
            ViewSim sim = Dive5(0);
            int entry = sim.Log.FindIndex(r => r.O.ViewState == ViewState.Underwater);
            Assert.IsTrue(entry > 0, "entered");
            ViewRec e = sim.Log[entry];
            ViewRec c = sim.Log.First(r => r.Tick == e.O.CrossingTick);
            Assert.IsTrue(c.Y <= ViewThresholds.UnderwaterEnterY && sim.Log[sim.Log.IndexOf(c) - 1].Y > ViewThresholds.UnderwaterEnterY, "crossing tick = first sample <= -0.20 m");
            long ms = (e.Us - c.Us) / 1000;
            Assert.IsTrue(ms <= 500, "S2.N7 (b): Underwater " + ms + " ms after the crossing (Subnautica moving, not settled)");
            Assert.IsTrue(sim.Log.Take(entry).Any(r => r.O.ApproachMode == ApproachMode.Prime), "Prime before the crossing");
            AssertNoOrderViolation(sim);

            ViewSim m10 = Dive5(ViewMutants.M10SettledAlways);
            int entryM = m10.Log.FindIndex(r => r.O.ViewState == ViewState.Underwater);
            ViewRec cm = m10.Log.First(r => r.Y <= ViewThresholds.UnderwaterEnterY);
            Assert.IsTrue(entryM < 0 || (m10.Log[entryM].Us - cm.Us) / 1000 > 500,
                          "S2.M10 (snReady gated on IsWorldSettled) must delay the entry beyond 0.5 s or prevent it");
            if (entryM >= 0) Assert.IsTrue(m10.Log[entryM].Y == -20.0, "S2.M10 enters only once the camera stops");
        }

        // ------------------------------------------------------------------------------------------- A′, C5, zones, ramp
        [TestMethod]
        public void APrime_LowersInsteadOfMargins_MenuRaises_DrawBeforeRaise()
        {
            // S2 policy, the fallback: lowered Underwater only
            var sim = new ViewSim { DisplayMode = DisplayMode.APrime, Vy = 0.0, Picture = ViewPicture.Waterline };
            sim.StepUntil(-20.0, o => o.ViewState == ViewState.Underwater, 10);
            ViewOutputs u = sim.Last;
            Assert.IsTrue(u.SeLowered && u.SceneSkip, "A′ underwater: lowered, not drawing");
            sim.Hold(30, -20.0);
            sim.Gui = true;
            ViewOutputs m = sim.Step(-20.0);
            AssertView(m, ViewState.Underwater, ApproachMode.Prime, "menu open underwater");
            Assert.IsFalse(m.SeLowered || m.SceneSkip, "menu rule: SE raised and drawing");
            sim.Gui = false;
            Assert.IsTrue(sim.Step(-20.0).SeLowered);
            sim.Peer = PeerState.Stalled;
            ViewOutputs s = sim.Step(-20.0);
            AssertView(s, ViewState.Surfacing, ApproachMode.Prime, "A′ surfacing");
            Assert.IsTrue(s.SeLowered && !s.SceneSkip, "draw first while still lowered");
            ViewOutputs p = sim.Step(-20.0);
            AssertView(p, ViewState.Space, ApproachMode.None, "after the drawn frame was presented");
            Assert.IsFalse(p.SeLowered, "then raised");
            Assert.IsTrue(sim.Log.All(r => !r.O.MarginsOn && !r.O.AlphaFill), "A′ never uses DWM margins");
            Assert.IsTrue(sim.Log.Where(r => r.O.ViewState == ViewState.Approach).All(r => !r.O.SeLowered), "S2 A′: raised in Approach");

            // S7a Site: A′ follows the same latch with its Lowered semantics: SE lowered below Subnautica and its scene
 // skipped from the latch's on edge in Approach (no menu open), raised while a menu is open, and when the latch
 // drops (here: the climb into Space) kept lowered while it draws a frame, raised once that frame is presented
            var site = new ViewSim { DisplayMode = DisplayMode.APrime, Vy = 0.0 };
            site.Hold(5, 100.0);
            ViewOutputs t = site.Last;
            AssertView(t, ViewState.Approach, ApproachMode.Track, "site A′ +100 m");
            Assert.IsTrue(t.Picture && t.SeLowered && t.SceneSkip && !t.MarginsOn, "site A′ Track: lowered and not drawing under the latch");
            site.Gui = true;
            ViewOutputs menu = site.Step(100.0);
            Assert.IsTrue(menu.Picture && !menu.SeLowered && !menu.SceneSkip, "site A′: a menu raises SE (the latch stays)");
            site.Gui = false;
            Assert.IsTrue(site.Step(100.0).SeLowered, "site A′: lowered again");
            site.Step(400.0);
            ViewOutputs up = site.Hold(60, 400.0);
            AssertView(up, ViewState.Space, ApproachMode.None, "site A′ Space (after the 1 s hold)");
            Assert.IsTrue(!up.Picture && up.SeLowered && !up.SceneSkip, "site A′: the latch dropped, still lowered, drawing first");
            Assert.IsFalse(site.Step(400.0).SeLowered, "site A′: raised after the drawn frame");
            Assert.IsTrue(site.Log.All(r => !r.O.MarginsOn && !r.O.AlphaFill), "site A′ never uses DWM margins");
        }

        [TestMethod]
        public void ApplyTo_WritesTheC5Fields_KeepsCallerFlags()
        {
            ViewSim sim = ViewSim.Underwater(-20.0, ViewPicture.Waterline);   // S2 policy, the fallback (C5 bit 9 clear)
            ViewOutputs o = sim.Hold(30, -20.0);
            var st = new SeState { flags = SeStateFlags.SeExiting | SeStateFlags.CameraInAirtightRoom | SeStateFlags.SePaused };
            ViewStateMachine.ApplyTo(ref o, DisplayMode.F, ref st);
            Assert.AreEqual(ViewState.Underwater, st.viewState);
            Assert.AreEqual(ApproachMode.Prime, o.ApproachMode, "the machine's sub-state stays Prime underwater (margins on)");
            Assert.AreEqual(ApproachMode.None, st.approachMode, "CONTRACT §5.4: C5 approachMode is 0 outside Approach");
            Assert.AreEqual(ApproachMode.None, ViewStateMachine.C5ApproachMode(ViewState.Surfacing, ApproachMode.Prime));
            Assert.AreEqual(ApproachMode.Prime, ViewStateMachine.C5ApproachMode(ViewState.Approach, ApproachMode.Prime));
            Assert.AreEqual(ApproachMode.Track, ViewStateMachine.C5ApproachMode(ViewState.Approach, ApproachMode.Track));
            Assert.AreEqual(ApproachMode.None, ViewStateMachine.C5ApproachMode(ViewState.Space, ApproachMode.None));
            Assert.AreEqual(DisplayMode.F, st.displayMode);
            Assert.AreEqual(PerfMode.Full, st.perfMode);
            Assert.AreEqual(0u, st.targetFps);
            Assert.AreEqual(1f, st.snBusTarget);
            Assert.AreEqual(300f, st.crossfadeMs);
            Assert.AreEqual(o.CrossingTick, st.crossingTick);
            Assert.AreEqual(0u, st.overlays);
            uint want = SeStateFlags.SeExiting | SeStateFlags.CameraInAirtightRoom | SeStateFlags.SeForeground |
                        SeStateFlags.SeeThrough | SeStateFlags.MarginsOn | SeStateFlags.ZOrderOk | SeStateFlags.SeMixActive;
            Assert.AreEqual(want, st.flags, "owned bits replaced (SePaused cleared), caller bits kept");

            // S7a Site: the same fields plus C5 flag bit 9 pictureSite (owned: written from the effective policy, so a
 // stale bit 9 from the caller is replaced too)
            ViewSim site = ViewSim.Underwater();
            ViewOutputs so = site.Hold(30, -20.0);
            Assert.AreEqual(ViewPicture.Site, so.PicturePolicy);
            var ss = new SeState { flags = SeStateFlags.SeExiting | SeStateFlags.CameraInAirtightRoom | SeStateFlags.SePaused };
            ViewStateMachine.ApplyTo(ref so, DisplayMode.F, ref ss);
            Assert.AreEqual(want | S7aConventions.C5FlagsExt.PictureSite, ss.flags, "site: bit 9 set");
            Assert.AreNotEqual(0u, ViewStateMachine.OwnedC5Flags & S7aConventions.C5FlagsExt.PictureSite, "bit 9 is owned by the machine");
            st.flags |= S7aConventions.C5FlagsExt.PictureSite;
            ViewStateMachine.ApplyTo(ref o, DisplayMode.F, ref st);
            Assert.AreEqual(want, st.flags, "waterline: a stale bit 9 is cleared");
        }

        [TestMethod]
        public void SiteZones_ClassifyWithHysteresis()
        {
            bool inZone = false;
            Assert.AreEqual(Zone.OffSite, SiteZones.Classify(false, 10.0, ref inZone));
            Assert.AreEqual(Zone.Core, SiteZones.Classify(true, 1950.0, ref inZone));
            Assert.AreEqual(Zone.TileVoid, SiteZones.Classify(true, 1950.01, ref inZone));
            Assert.AreEqual(Zone.TileVoid, SiteZones.Classify(true, 2048.0, ref inZone));
            Assert.AreEqual(Zone.OuterVoid, SiteZones.Classify(true, 2048.01, ref inZone));
            Assert.AreEqual(Zone.OuterVoid, SiteZones.Classify(true, 7900.0, ref inZone), "inside: kept to 7,900 m");
            Assert.AreEqual(Zone.Far, SiteZones.Classify(true, 7900.01, ref inZone));
            Assert.AreEqual(Zone.Far, SiteZones.Classify(true, 7800.01, ref inZone), "outside: needs <= 7,800 m");
            Assert.AreEqual(Zone.OuterVoid, SiteZones.Classify(true, 7800.0, ref inZone));
            Assert.AreEqual(Zone.OffSite, SiteZones.Classify(true, double.NaN, ref inZone));
            Assert.IsFalse(inZone);
            Assert.AreEqual(7000.0, SiteZones.RhoInf(-7000.0, 12.0));
            Assert.IsTrue(SiteZones.IsDive(Zone.Core) && SiteZones.IsDive(Zone.TileVoid) && SiteZones.IsDive(Zone.OuterVoid));
            Assert.IsFalse(SiteZones.IsDive(Zone.Far) || SiteZones.IsDive(Zone.OffSite));
        }

        [TestMethod]
        public void LinearRamp_RetargetsFromTheCurrentValue()
        {
            var r = new LinearRamp();
            Assert.AreEqual(0.0, r.Value(0));
            r.Retarget(1.0, 1000, 300000);
            Assert.AreEqual(0.0, r.Value(1000));
            Assert.AreEqual(0.5, r.Value(151000), 1e-12);
            Assert.AreEqual(1.0, r.Value(301000));
            r.Retarget(0.0, 151000, 300000);
            Assert.AreEqual(0.5, r.Value(151000), 1e-12, "no jump");
            Assert.AreEqual(0.25, r.Value(301000), 1e-12);
            r.Set(0.3, 5);
            Assert.AreEqual(0.3, r.Value(10));
        }

        [TestMethod]
        public void Step_IsDeterministic()
        {
            Func<List<string>> run = () =>
            {
                var sim = new ViewSim { TraceRows = true, AckLag = 2, SnLag = 2 };
                sim.Hold(30, 50.0);
                sim.Ramp(300, 50.0, -3.0);
                sim.Hold(60, -3.0);
                sim.Foreground = false; sim.Hold(10, -3.0); sim.Foreground = true;
                sim.Teleport(40); sim.Hold(80, -6.0);
                sim.Ramp(300, -6.0, 20.0);
                return sim.Rows;
            };
            CollectionAssert.AreEqual(run(), run());
        }
    }
}
