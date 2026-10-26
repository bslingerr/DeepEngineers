using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7a picture policy of the view machine (ARCHITECTURE §4.2 "Picture policy (S7a)", CONTRACT §5.4 "C5/C7 semantics
 /// for S7a", ROADMAP S7a.N1/N2): under <see cref="ViewPicture.Site"/> Subnautica's picture follows a latch that turns
 /// on in Approach below +300 m once Subnautica is ready and off in Space or at once on a site, peer or re-streaming
 /// loss; the waterline no longer swaps pictures; each on-edge guard blocks alone; S7a.M1 runs Waterline.
 /// </summary>
    [TestClass]
    public class S7aPictureTests
    {
        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        private static List<ViewRec> Edges(ViewSim sim, int from = 0) { return ViewSim.PictureEdges(sim.Log, from); }

        private static void NoOrderViolation(ViewSim sim)
        {
            List<string> bad = ViewSim.OrderViolations(sim.Log);
            Assert.AreEqual(0, bad.Count, "crossing order: " + string.Join("; ", bad.Take(5)));
        }

        // ------------------------------------------------------------------------------------------- the band (S7a.N2)
        [TestMethod]
        public void Band_OneSwapPerDescentBelowPlus300_OneBackPerAscentAtSpace()
        {
            foreach (int ticks in new[] { 1200, 240 })   // 20 m/s, 100 m/s (predicted arrival)
            {
                var sim = new ViewSim();
                sim.Hold(60, 600.0);
                for (int k = 0; k < 3; k++)
                {
                    int a = sim.Log.Count;
                    sim.Ramp(ticks, 600.0, 200.0);
                    sim.Hold(120, 200.0);
                    List<ViewRec> down = Edges(sim, a);
                    Assert.AreEqual(1, down.Count, ticks + " ticks: one edge per descent");
                    Assert.AreEqual(1, down[0].O.PictureEdge);
                    Assert.AreEqual(ViewReason.PictureOn, down[0].O.PictureReason);
                    Assert.IsTrue(down[0].Y < ViewThresholds.ApproachEnterY, "the swap is at the band itself (y " + down[0].Y + ")");
                    if (ticks == 240)
                    {
                        Assert.IsTrue(down[0].Y > 150.0, "100 m/s: the latch on before +150 m (y " + down[0].Y + ")");
                        ViewRec entry = sim.Log.Skip(a).First(r => r.O.Reason == ViewReason.EnterApproach);
                        Assert.IsTrue(entry.Y > 500.0, "100 m/s: Approach by predicted arrival far above (y " + entry.Y + ")");
                        Assert.IsTrue(sim.Log.Skip(a).Where(r => r.Tick >= entry.Tick && r.Tick < down[0].Tick)
                                         .All(r => r.O.PerfMode == PerfMode.Full && r.O.MarginsOn && !r.O.Picture &&
                                                   (r.O.PictureBlocked & ViewBlock.High) != 0),
                                      "between the early entry and the band: Subnautica warm (Full, margins), SE's picture (High)");
                    }
                    int b = sim.Log.Count;
                    sim.Ramp(ticks, 200.0, 600.0);
                    sim.Hold(120, 600.0);
                    List<ViewRec> up = Edges(sim, b);
                    Assert.AreEqual(1, up.Count, ticks + " ticks: one edge per ascent");
                    Assert.AreEqual(-1, up[0].O.PictureEdge);
                    Assert.AreEqual(ViewState.Space, up[0].O.ViewState);
                    Assert.AreEqual(ViewReason.PictureOffSpace, up[0].O.PictureReason);
                    Assert.IsTrue(up[0].Y > ViewThresholds.ApproachExitY);
                }
                NoOrderViolation(sim);
                Assert.IsTrue(sim.Log.All(r => (r.O.ViewState == ViewState.Space && !r.O.MarginsOn && !r.O.SeLowered) == (r.O.PerfMode == PerfMode.Idle)),
                              "Idle exactly in Space once SE's scene is back on screen (F1: Full during the band-exit hold)");
            }
        }

        [TestMethod]
        public void Hover_BetweenPlus300AndPlus350_NoSwap_EitherWay()
        {
            var sim = new ViewSim();
            sim.Hold(60, 400.0);
            sim.Ramp(600, 400.0, 250.0);
            Assert.IsTrue(sim.Last.Picture, "below +300 m: picture on");
            sim.Ramp(120, 250.0, 327.5);
            int a = sim.Log.Count;
            sim.Run(1200, s => 327.5 + 20.0 * Math.Sin(2.0 * Math.PI * 0.05 * s));
            Assert.AreEqual(0, Edges(sim, a).Count, "hovering 307..347 m with the picture on: kept (exit at +350 m)");
            Assert.IsTrue(sim.Log.Skip(a).All(r => r.O.Picture && r.O.ViewState == ViewState.Approach));
            sim.Ramp(300, sim.Log.Last().Y, 600.0);
            sim.Hold(60, 600.0);
            sim.Ramp(1080, 600.0, 327.5);
            int b = sim.Log.Count;
            sim.Run(1200, s => 327.5 + 20.0 * Math.Sin(2.0 * Math.PI * 0.05 * s));
            Assert.AreEqual(0, Edges(sim, b).Count, "hovering from above, never below +300 m: no picture");
            Assert.IsTrue(sim.Log.Skip(b).All(r => !r.O.Picture && (r.O.PictureBlocked & ViewBlock.High) != 0 &&
                                                   r.O.ViewState == ViewState.Approach && r.O.PerfMode == PerfMode.Full));
            NoOrderViolation(sim);
        }

        // ------------------------------------------------------------------------------------------- the waterline (S7a.N1)
        [TestMethod]
        public void Waterline_NoPictureEdge_StatesAndAudioAsInS2()
        {
            var sim = new ViewSim();
            sim.Hold(120, 20.0);
            int a = sim.Log.Count;
            for (int k = 0; k < 2; k++)
            {
                sim.Ramp(480, 20.0, -20.0);
                sim.Hold(120, -20.0);
                Assert.AreEqual(ViewState.Underwater, sim.Last.ViewState);
                Assert.AreEqual(1.0, sim.Last.MixLevel, "the underwater mix (audio still follows the waterline)");
                Assert.AreEqual(1f, sim.Last.SnBusTarget);
                sim.Ramp(480, -20.0, 20.0);
                sim.Hold(120, 20.0);
                Assert.AreEqual(0.0, sim.Last.MixLevel);
            }
            Assert.AreEqual(0, Edges(sim, a).Count, "zero picture edges through two dives and surfacings");
            Assert.AreEqual(4, ViewSim.Switches(sim.Log, a).Count, "the Underwater state still switches (S2.N2 counts it)");
            Assert.IsTrue(sim.Log.Skip(a).All(r => r.O.Picture && r.O.SceneSkip), "Subnautica's picture throughout");
            Assert.IsTrue(sim.Log.Skip(a).Where(r => r.O.ViewState == ViewState.Surfacing).All(r => r.O.SceneSkip), "Surfacing keeps the picture");
            NoOrderViolation(sim);
        }

        // ------------------------------------------------------------------------------------------- the latch's guards
        [TestMethod]
        public void Latch_EachOnEdgeGuardBlocksAlone()
        {
            var cases = new List<Tuple<string, Action<ViewSim>, Action<ViewSim>, uint>>
            {
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("snPhase WarmingUp", s => s.Phase = SnPhase.WarmingUp, s => s.Phase = SnPhase.Active, ViewBlock.Phase),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("areaBuilt", s => s.AreaBuilt = false, s => s.AreaBuilt = true, ViewBlock.AreaBuilt),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("rectOk", s => s.RectOk = false, s => s.RectOk = true, ViewBlock.Rect),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("uiHidden", s => s.UiHidden = false, s => s.UiHidden = true, ViewBlock.UiHidden),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("freshness", s => s.SnLag = 6, s => s.SnLag = 1, ViewBlock.Fresh),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("zOrderOk", s => s.ZOrderOk = false, s => s.ZOrderOk = true, ViewBlock.ZOrder),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("margins (F)", s => s.MarginsReady = false, s => s.MarginsReady = true, ViewBlock.Render),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("display mode C", s => s.DisplayMode = DisplayMode.C, s => s.DisplayMode = DisplayMode.F, ViewBlock.Mode),
                Tuple.Create<string, Action<ViewSim>, Action<ViewSim>, uint>("rho 7,850 m", s => s.X = 7850.0, s => s.X = 7800.0, ViewBlock.Rho),
            };
            foreach (var c in cases)
            {
                var sim = new ViewSim { Vy = 0.0 };
                if (c.Item1 == "rho 7,850 m") sim.X = 7700.0;
                sim.Hold(10, 400.0);                         // Space: settled after the world load
                c.Item2(sim);
                ViewOutputs o = sim.Hold(60, 100.0);
                Assert.AreEqual(ViewState.Approach, o.ViewState, c.Item1);
                Assert.IsFalse(o.Picture, c.Item1 + ": no picture");
                Assert.AreEqual(c.Item4, o.PictureBlocked, c.Item1 + ": the only failing guard (0x" + o.PictureBlocked.ToString("x") + ")");
                // F16: display mode C can never show the picture, so it keeps Waterline's perf table (Track)
                Assert.AreEqual(c.Item1 == "display mode C" ? PerfMode.Track : PerfMode.Full, o.PerfMode, c.Item1 + ": Full while waiting (warm); mode C: Track");
                c.Item3(sim);
                int steps = sim.StepUntil(100.0, x => x.Picture, 10);
                Assert.IsTrue(steps <= 2, c.Item1 + " mended: the picture within 2 ticks (took " + steps + ")");
                NoOrderViolation(sim);
            }

            // NotApproach and Band: the first tick of Approach never shows the picture; with Subnautica 4 ticks late the
 // latch waits for a frame presented since the band entry
            var band = new ViewSim { Vy = 0.0, SnLag = 4 };
            band.Hold(10, 400.0);
            ViewOutputs e0 = band.Step(100.0);
            Assert.AreEqual(ViewReason.EnterApproach, e0.Reason);
            Assert.AreNotEqual(0u, e0.PictureBlocked & ViewBlock.NotApproach);
            ViewOutputs e2 = band.Hold(2, 100.0);
            Assert.AreEqual(ViewBlock.Band, e2.PictureBlocked, "entry + 2: only the band freshness (0x" + e2.PictureBlocked.ToString("x") + ")");
            Assert.IsFalse(band.Step(100.0).Picture, "entry + 3: presentedSeTick still before the band tick");
            Assert.IsTrue(band.Step(100.0).Picture, "entry + 4: Subnautica presented a frame from the band entry");

            // High: Approach by predicted arrival above +300 m waits for the band
            var high = new ViewSim { Vy = -50.0 };
            high.Hold(10, 700.0);
            ViewOutputs h = high.Hold(20, 320.0);
            Assert.AreEqual(ViewState.Approach, h.ViewState, "320 m at 50 m/s: Approach (predicted)");
            Assert.AreEqual(ViewBlock.High, h.PictureBlocked);
            Assert.IsTrue(high.Step(299.9).Picture, "below +300 m: on");

            // Dwell: a re-streaming drop, settled again after 5 ticks; the latch waits for 0.25 s since the drop
            var dw = new ViewSim { Vy = 0.0 };
            dw.Hold(60, 100.0);
            Assert.IsTrue(dw.Last.Picture);
            dw.Teleport(5);
            ViewOutputs t0 = dw.Step(100.0);
            Assert.AreEqual(ViewReason.PictureOffResettle, t0.PictureReason);
            ViewOutputs t8 = dw.Hold(8, 100.0);
            Assert.AreEqual(ViewBlock.Dwell, t8.PictureBlocked, "settled, only the dwell left (0x" + t8.PictureBlocked.ToString("x") + ")");
            int more = dw.StepUntil(100.0, x => x.Picture, 20);
            Assert.AreEqual(15, 8 + more, "on 15 ticks (0.25 s at 16,667 us) after the drop");
        }

        [TestMethod]
        public void Latch_ImmediateOff_SitePeerResettleSpace_WithTheirReasons()
        {
            // site: the zone left beyond 7,900 m in Approach
            var z = new ViewSim { Vy = 0.0, X = 7700.0 };
            z.Hold(60, 100.0);
            Assert.IsTrue(z.Last.Picture);
            z.X = 7900.5;
            ViewOutputs o = z.Step(100.0);
            Assert.IsTrue(o.ViewState == ViewState.Space && !o.Picture && o.PictureEdge == -1, "FAR: Space, picture off at once");
            Assert.AreEqual(ViewReason.PictureOffSite, o.PictureReason);
            NoOrderViolation(z);

            // peer
            var p = new ViewSim { Vy = 0.0 };
            p.Hold(60, 100.0);
            p.Peer = PeerState.Stalled;
            o = p.Step(100.0);
            Assert.AreEqual(ViewReason.PictureOffPeer, o.PictureReason);
            Assert.IsTrue(!o.SceneSkip && o.MarginsOn, "scene drawn, margins until it is presented");
            NoOrderViolation(p);

            // re-streaming without a view change (Track keeps its state; SE's own picture until settled)
            var r = new ViewSim { Vy = 0.0 };
            r.Hold(60, 100.0);
            r.Teleport(60);
            o = r.Step(100.0);
            Assert.AreEqual(ViewState.Approach, o.ViewState, "a teleport does not change Approach");
            Assert.IsTrue(!o.Picture && !o.SceneSkip && o.MarginsOn && o.PerfMode == PerfMode.Full, "SE's picture, Subnautica still Full");
            Assert.AreEqual(ViewReason.PictureOffResettle, o.PictureReason);
            NoOrderViolation(r);

            // Space by altitude
            var s = new ViewSim { Vy = 0.0 };
            s.Hold(60, 100.0);
            s.Step(351.0);
            o = s.Hold(60, 351.0);   // F15: the altitude exit holds 1 s
            Assert.AreEqual(ViewReason.PictureOffSpace, o.PictureReason);
            Assert.AreEqual(ViewReason.LeaveAltitude, o.Reason);

            // no other loss drops it: snReady parts, z-order, margins, foreground, pause
            var k = new ViewSim { Vy = 0.0 };
            k.Hold(60, 100.0);
            k.Phase = SnPhase.WarmingUp; k.ZOrderOk = false; k.MarginsReady = false; k.Foreground = false; k.Paused = true; k.SnLag = 8;
            Assert.IsTrue(k.Hold(60, 100.0).Picture, "only site, peer and re-streaming losses (and Space) drop the latch");
        }

        // ------------------------------------------------------------------------------------------- policies, S7a.M1
        [TestMethod]
        public void Policy_EffectivePicture_M1_AndWaterlineEdges()
        {
            Assert.AreEqual(ViewPicture.Site, ViewStateMachine.EffectivePicture(ViewPicture.Site, 0));
            Assert.AreEqual(ViewPicture.Waterline, ViewStateMachine.EffectivePicture(ViewPicture.Waterline, 0));
            Assert.AreEqual(ViewPicture.Waterline, ViewStateMachine.EffectivePicture(ViewPicture.Site, ViewMutants.S7aM1Waterline));
            Assert.AreEqual(ViewPicture.Site, ViewStateMachine.EffectivePicture(7u, 0), "an unknown value runs the default (Site)");
            Assert.AreEqual(ViewPicture.Site, ViewStateMachine.EffectivePicture(ViewPicture.Site, ViewMutants.M4NoHysteresis | ViewMutants.M10SettledAlways));

            // S7a.M1: Site selected, the machine runs Waterline (S7a.N1 red: SE's picture in Approach, edges at the waterline)
            var m1 = new ViewSim { Mutants = ViewMutants.S7aM1Waterline };
            m1.Hold(60, 400.0);
            m1.Ramp(900, 400.0, -50.0);
            m1.Hold(60, -50.0);
            Assert.IsTrue(m1.Log.All(r => r.O.PicturePolicy == ViewPicture.Waterline && (r.O.C5Flags & S7aConventions.C5FlagsExt.PictureSite) == 0));
            Assert.IsTrue(m1.Log.Where(r => r.O.ViewState == ViewState.Approach && r.O.ApproachMode == ApproachMode.Track)
                            .All(r => r.O.PerfMode == PerfMode.Track && !r.O.Picture && !r.O.MarginsOn), "M1: S2's Track");
            List<ViewRec> e = Edges(m1);
            Assert.AreEqual(1, e.Count);
            Assert.AreEqual(ViewState.Underwater, e[0].O.ViewState, "M1: the picture edge is the Underwater entry");
            Assert.AreEqual(ViewReason.None, e[0].O.PictureReason, "Waterline edges carry no picture reason");

            // Waterline: PictureBlocked is the Underwater guard while not Underwater, 0 while Underwater
            var wl = new ViewSim { Picture = ViewPicture.Waterline, Vy = 0.0 };
            ViewOutputs pr = wl.Hold(30, 0.5);
            Assert.AreEqual(pr.Blocked, pr.PictureBlocked);
            wl.StepUntil(-0.5, x => x.ViewState == ViewState.Underwater);
            Assert.AreEqual(0u, wl.Last.PictureBlocked);
            Assert.IsTrue(wl.Last.Picture && wl.Last.PictureEdge == 1);
        }

        [TestMethod]
        public void Policy_SwitchAtRunTime_TakesEffectOnTheNextTick()
        {
            var sim = new ViewSim { Vy = 0.0 };
            sim.Hold(60, 100.0);
            Assert.IsTrue(sim.Last.Picture && sim.Last.PerfMode == PerfMode.Full);
            sim.Picture = ViewPicture.Waterline;          // de.s7 picture waterline in Track
            ViewOutputs o = sim.Step(100.0);
            Assert.IsTrue(!o.Picture && o.PictureEdge == -1 && o.PerfMode == PerfMode.Track && o.PicturePolicy == ViewPicture.Waterline);
            sim.Hold(30, 100.0);
            Assert.IsFalse(sim.Last.MarginsOn, "S2's Track: margins off");
            sim.Picture = ViewPicture.Site;               // back
            o = sim.Step(100.0);
            Assert.IsTrue(o.MarginsOn && o.PerfMode == PerfMode.Full && !o.Picture, "Site again: margins and Full at once, the latch after its guards");
            int n = sim.StepUntil(100.0, x => x.Picture, 30);
            Assert.IsTrue(n <= 2, "picture again after the margins request is presented (" + n + ")");
            // under water a switch keeps the picture (Underwater is Subnautica's under both policies)
            sim.Ramp(300, 100.0, -10.0);
            sim.Hold(30, -10.0);
            Assert.AreEqual(ViewState.Underwater, sim.Last.ViewState);
            sim.Picture = ViewPicture.Waterline;
            o = sim.Step(-10.0);
            Assert.IsTrue(o.Picture && o.PictureEdge == 0 && o.SceneSkip, "no edge under water");
        }
    }
}
