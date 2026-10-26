using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// <see cref="RenderAck"/> with a frame-level model of SE's render thread (design F): the view state machine's
 /// requests never make the render thread present a cleared backbuffer without margins that have been on for at
 /// least one presented frame, and margins drop only after a scene-drawn frame.
 /// </summary>
    [TestClass]
    public class RenderAckTests
    {
        /// <summary>
 /// SE's render thread as ARCHITECTURE §4.4/§5.1 describe it: each frame draws with the newest request (scene or
 /// cleared backbuffer), presents, then in the Present postfix applies a margins change (margins off only after a
 /// scene-drawn frame). Records every invariant break.
 /// </summary>
        private sealed class FakeRenderThread
        {
            public bool MarginsOn;          // DWM state
            public int FramesSinceMargins;  // frames presented since margins went on
            public long SceneDrawn, Skipped;
            public readonly List<string> Violations = new List<string>();
            public long Frames;

            public bool MarginsApplied { get { return MarginsOn && FramesSinceMargins >= 1; } }

            public void Frame(uint request, ulong tick)
            {
                Frames++;
                bool skip = (request & ViewRequest.SceneSkip) != 0;
                bool margins = (request & ViewRequest.Margins) != 0;
                if (skip)
                {
                    if (!MarginsOn) Violations.Add("tick " + tick + ": cleared backbuffer presented without margins (black frame)");
                    else if (FramesSinceMargins < 1) Violations.Add("tick " + tick + ": skip on the first frame after margins went on");
                    Skipped++;
                }
                else SceneDrawn++;
                if (MarginsOn) FramesSinceMargins++;
                // Present postfix: margins changes after the frame that needs them
                if (margins && !MarginsOn) { MarginsOn = true; FramesSinceMargins = 0; }
                else if (!margins && MarginsOn && !skip) MarginsOn = false;
            }
        }

        [TestMethod]
        public void RenderAck_DefaultTreatsTheInitialRequestAsPresented()
        {
            var ack = new RenderAck();
            Assert.AreEqual(0u, ack.Update(0, 0, false, 0, 0));
            // a new margins-on request: not presented until margins applied and a frame drawn after it
            Assert.AreEqual(0u, ack.Update(1, ViewRequest.Margins | ViewRequest.AlphaFill, false, 5, 0));
            Assert.AreEqual(0u, ack.Update(1, ViewRequest.Margins | ViewRequest.AlphaFill, true, 5, 0), "no frame since");
            Assert.AreEqual(1u, ack.Update(1, ViewRequest.Margins | ViewRequest.AlphaFill, true, 6, 0));
            // skip: needs a skipped frame
            Assert.AreEqual(1u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, true, 7, 0));
            Assert.AreEqual(2u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, true, 7, 1));
            // margins off: needs margins gone and a scene frame
            Assert.AreEqual(2u, ack.Update(3, 0, true, 7, 1));
            Assert.AreEqual(2u, ack.Update(3, 0, false, 7, 1));
            Assert.AreEqual(3u, ack.Update(3, 0, false, 8, 1));
            // attaching mid-way: the current request is not taken as presented
            var late = new RenderAck();
            Assert.AreEqual(41u, late.Update(42, ViewRequest.Margins, false, 100, 0));
        }

        /// <summary>
 /// Review fix: a scene-drawn request that asks for margins is acknowledged on its frame alone (the render
 /// thread may be unable to apply the margins); a skip request still needs a skipped frame (only possible with the
 /// margins on) and the margins state; a margins-off request still needs the margins gone.
 /// </summary>
        [TestMethod]
        public void RenderAck_SceneDrawnMarginsRequest_AckedWithoutMargins_SkipStillNeedsThem()
        {
            var ack = new RenderAck();
            Assert.AreEqual(0u, ack.Update(0, 0, false, 0, 0));
            Assert.AreEqual(0u, ack.Update(1, ViewRequest.Margins | ViewRequest.AlphaFill, false, 10, 0));
            Assert.AreEqual(1u, ack.Update(1, ViewRequest.Margins | ViewRequest.AlphaFill, false, 11, 0), "scene frame presented, margins impossible");
            Assert.AreEqual(1u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, false, 11, 0));
            Assert.AreEqual(1u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, false, 12, 0), "a scene frame does not acknowledge a skip");
            Assert.AreEqual(1u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, false, 12, 1), "a skipped frame without margins state: no ack");
            Assert.AreEqual(2u, ack.Update(2, ViewRequest.Margins | ViewRequest.SceneSkip, true, 12, 2));
            Assert.AreEqual(2u, ack.Update(3, ViewRequest.AlphaFill, true, 13, 2));
            Assert.AreEqual(2u, ack.Update(3, ViewRequest.AlphaFill, true, 14, 2), "margins still on: a margins-off request is not presented");
            Assert.AreEqual(3u, ack.Update(3, ViewRequest.AlphaFill, false, 15, 2));
        }

        /// <summary>
 /// Review fix (finding 11 and the Surfacing hold found with it), with a render thread that drops the
 /// margins when it must (a lost or unlinked window, a forced margins-off) and draws the scene then:
 /// (a) margins dropped after the Prime acknowledgement: Underwater is held (the acknowledgement is sticky, the
 /// live MarginsReady input is not); (b) a peer exit underwater: Surfacing completes to Space although the margins
 /// requested in Surfacing can no longer be applied.
 /// </summary>
        [TestMethod]
        public void RenderAck_MarginsLostAfterPrime_HoldsUnderwater_AndPeerExitSurfacesToSpace()
        {
            var sim = new ViewSim { Picture = ViewPicture.Waterline };   // S2 policy, the fallback
            var render = new FakeRenderThread();
            var ack = new RenderAck();
            bool marginsPossible = true;
            sim.AckProvider = () => ack.Update(sim.State.RenderSeq, sim.State.Request, render.MarginsApplied, render.SceneDrawn, render.Skipped);
            sim.MarginsReadyProvider = () => render.MarginsApplied;
            sim.AfterStep = o =>
            {
                uint rq = o.Request;
                if (!marginsPossible) rq &= ~(ViewRequest.Margins | ViewRequest.SceneSkip); // the render thread refuses both
                render.Frame(rq, sim.Tick);
                render.Frame(rq, sim.Tick);
            };
            sim.Vy = 0.0;
            sim.StepUntil(0.5, x => x.ViewState == ViewState.Approach && x.ApproachMode == ApproachMode.Prime);
            sim.Hold(10, 0.5);
            Assert.IsTrue(render.MarginsApplied);

            // (a) the margins go away (de.display margins=off, a failed DWM call): below the waterline Underwater is held
            marginsPossible = false;
            ViewOutputs held = sim.Hold(60, -1.0);
            Assert.AreEqual(ViewState.Approach, held.ViewState, "no Underwater without the margins applied now");
            Assert.AreNotEqual(0u, held.Blocked & ViewBlock.Render);
            Assert.AreEqual(0L, render.Skipped);
            marginsPossible = true;
            sim.StepUntil(-1.0, x => x.ViewState == ViewState.Underwater, 30);

            // (b) peer exit underwater: Surfacing, then Space once a scene frame is presented, although margins are refused
            sim.Hold(30, -1.0);
            sim.Peer = PeerState.Exited;
            marginsPossible = false;
            ViewOutputs o1 = sim.Step(-1.0);
            Assert.AreEqual(ViewState.Surfacing, o1.ViewState);
            int steps = sim.StepUntil(-1.0, x => x.ViewState == ViewState.Space, 10);
            Assert.IsTrue(steps <= 2, "Space within " + steps + " steps (no Surfacing hold)");
            Assert.AreEqual(0, render.Violations.Count, string.Join("; ", render.Violations));

            // S7a Site: margins lost before the latch keep it off (its F guard needs the margins applied now), so no picture
 // and no Underwater; margins lost after the latch leave the machine in its state (only a site, peer or
 // re-streaming loss drops the latch) and the render thread draws SE's scene by itself: never a black frame.
 // A peer exit under water still reaches Space at once.
            var site = new ViewSim();
            var r2 = new FakeRenderThread();
            var ack2 = new RenderAck();
            bool possible2 = false;
            site.AckProvider = () => ack2.Update(site.State.RenderSeq, site.State.Request, r2.MarginsApplied, r2.SceneDrawn, r2.Skipped);
            site.MarginsReadyProvider = () => r2.MarginsApplied;
            site.AfterStep = o =>
            {
                uint rq = o.Request;
                if (!possible2) rq &= ~(ViewRequest.Margins | ViewRequest.SceneSkip);
                r2.Frame(rq, site.Tick);
                r2.Frame(rq, site.Tick);
            };
            site.Vy = 0.0;
            ViewOutputs before = site.Hold(60, 0.5);
            Assert.IsFalse(before.Picture, "site: no margins, no latch");
            Assert.AreNotEqual(0u, before.PictureBlocked & ViewBlock.Render);
            Assert.AreNotEqual(ViewState.Underwater, site.Hold(60, -1.0).ViewState, "site: no Underwater without the latch");
            possible2 = true;
            site.StepUntil(-1.0, x => x.ViewState == ViewState.Underwater, 30);
            Assert.IsTrue(site.Last.Picture);
            possible2 = false;
            site.Hold(30, -1.0);
            Assert.AreEqual(ViewState.Underwater, site.Last.ViewState, "site: a margins loss after the latch does not change the state (open issue)");
            site.Peer = PeerState.Exited;
            Assert.AreEqual(ViewState.Surfacing, site.Step(-1.0).ViewState);
            Assert.IsTrue(site.StepUntil(-1.0, x => x.ViewState == ViewState.Space, 10) <= 2, "site: Space at once after a peer exit");
            Assert.AreEqual(0, r2.Violations.Count, "site: " + string.Join("; ", r2.Violations));
        }

        [TestMethod]
        public void RenderAck_DrivesTheMachine_NoBlackFrame_OverRandomFrameRates()
        {
            var rnd = new Random(60000);
            for (int run = 0; run < 10; run++)
            {
                var sim = new ViewSim { SnLag = 1 + rnd.Next(3) };
                var render = new FakeRenderThread();
                var ack = new RenderAck();
                sim.AckProvider = () => ack.Update(sim.State.RenderSeq, sim.State.Request, render.MarginsApplied, render.SceneDrawn, render.Skipped);
                sim.MarginsReadyProvider = () => render.MarginsApplied;
                sim.AfterStep = o =>
                {
                    int frames = rnd.NextDouble() < 0.05 ? 0 : 1 + rnd.Next(3);   // 0 (a render hitch) to 3 frames per tick
                    for (int f = 0; f < frames; f++) render.Frame(o.Request, sim.Tick);
                };
                double y = 3.0, target = -1.0;
                for (int k = 0; k < 6000; k++)
                {
                    if (rnd.NextDouble() < 0.01) target = -1.5 + rnd.NextDouble() * 3.0;
                    if (rnd.NextDouble() < 0.002) sim.Peer = sim.Peer == PeerState.Alive ? PeerState.Stalled : PeerState.Alive;
                    if (rnd.NextDouble() < 0.001) sim.Teleport(rnd.Next(10, 90));
                    y += Math.Max(-0.05, Math.Min(0.05, target - y)) + (rnd.NextDouble() - 0.5) * 0.02;
                    sim.Step(y);
                }
                Assert.AreEqual(0, render.Violations.Count, "run " + run + ": " + string.Join("; ", render.Violations.GetRange(0, Math.Min(5, render.Violations.Count))));
                List<ViewRec> sw = ViewSim.Switches(sim.Log);
                Assert.IsTrue(sw.Count >= 4, "run " + run + " dived through the counters' acks (" + sw.Count + " switches)");
                Assert.IsTrue(render.Skipped > 0 && render.SceneDrawn > 0);
            }
        }
    }
}
