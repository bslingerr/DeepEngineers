using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S8 minimum, SE side: the peer-loss fallback decision table of
 /// <see cref="S8Rules"/> (peer × view × time: fallback, notice now / after 10 s / never, one per loss, recovery), the
 /// display guard in <see cref="DisplayController"/> (no skip, no A′ lowering, the crossfade snaps), the audio restore,
 /// the exit-poll and resend rules, the rows, and the link's stall bookkeeping.
 /// </summary>
    [TestClass]
    public class S8Tests
    {
        private static S8Rules.Inputs In(uint peer, uint view = ViewState.Space, bool picture = false, ulong now = 1000, bool linked = true,
                                         bool inWorld = true, bool site = true)
        {
            return new S8Rules.Inputs { Linked = linked, Peer = peer, ViewState = view, PictureOn = picture, InWorld = inWorld, SiteActive = site, NowMs = now };
        }

        private static S8Rules.Decision Step(ref S8Rules.Memory m, S8Rules.Inputs i)
        {
            return S8Rules.Decide(ref i, ref m);
        }

        [TestMethod]
        public void Table_AliveAndStart_NoFallbackNoNotice()
        {
            var m = new S8Rules.Memory();
            // the start: a linked peer not judged yet, never alive → plain SE, nothing
            S8Rules.Decision d = Step(ref m, In(PeerState.Unknown));
            Assert.IsFalse(d.Active); Assert.IsFalse(d.NoticeNow);
            d = Step(ref m, In(PeerState.Unknown, linked: false, now: 50000));
            Assert.IsFalse(d.Active, "renderer offline from the start: no fallback, no notice");
            d = Step(ref m, In(PeerState.Alive, ViewState.Underwater, now: 60000));
            Assert.IsFalse(d.Active); Assert.IsFalse(d.LostEdge); Assert.IsTrue(m.EverAlive);
        }

        [TestMethod]
        public void Table_LossUnderwater_NoticeAtOnce_OncePerLoss()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, ViewState.Underwater, now: 1000));
            S8Rules.Decision d = Step(ref m, In(PeerState.Exited, ViewState.Underwater, now: 2000));
            Assert.IsTrue(d.Active); Assert.IsTrue(d.LostEdge); Assert.IsTrue(d.Visible);
            Assert.IsTrue(d.NoticeNow, "underwater: at once");
            Assert.AreEqual(2000UL, d.NoticeAtMs);
            Assert.AreEqual(PeerState.Exited, m.LostPeer);
            d = Step(ref m, In(PeerState.Exited, ViewState.Surfacing, now: 2016));
            Assert.IsTrue(d.Active); Assert.IsFalse(d.LostEdge); Assert.IsFalse(d.NoticeNow, "once per loss");
            d = Step(ref m, In(PeerState.Exited, ViewState.Space, now: 30000));
            Assert.IsFalse(d.NoticeNow);
            Assert.AreEqual(1L, m.Notices);
            Assert.AreEqual(S8Rules.NoticeExited, S8Rules.NoticeText(m.LostPeer));
        }

        [TestMethod]
        public void Table_LossSurfacingOrPictureOn_NoticeAtOnce()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, ViewState.Surfacing, now: 1000));
            Assert.IsTrue(Step(ref m, In(PeerState.Stalled, ViewState.Surfacing, now: 4100)).NoticeNow);
            var m2 = new S8Rules.Memory();
            Step(ref m2, In(PeerState.Alive, ViewState.Approach, picture: true, now: 1000));
            S8Rules.Decision d = Step(ref m2, In(PeerState.Stalled, ViewState.Approach, picture: true, now: 4100));
            Assert.IsTrue(d.NoticeNow, "S7a Site: the latch was on above water");
            Assert.AreEqual(S8Rules.NoticeStalled, S8Rules.NoticeText(m2.LostPeer));
        }

        [TestMethod]
        public void Table_LossElsewhere_NoticeAfter10s()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, ViewState.Space, now: 1000));
            S8Rules.Decision d = Step(ref m, In(PeerState.Stalled, ViewState.Space, now: 5000));
            Assert.IsTrue(d.Active); Assert.IsFalse(d.Visible); Assert.IsFalse(d.NoticeNow);
            Assert.AreEqual(5000UL + S8Rules.NoticeDelayMs, d.NoticeAtMs);
            Assert.IsFalse(Step(ref m, In(PeerState.Stalled, ViewState.Approach, now: 14999)).NoticeNow, "Track/Prime under Waterline: still waiting");
            Assert.IsTrue(Step(ref m, In(PeerState.Stalled, ViewState.Approach, now: 15000)).NoticeNow, "10 s");
            Assert.IsFalse(Step(ref m, In(PeerState.Stalled, ViewState.Approach, now: 15016)).NoticeNow);
        }

        [TestMethod]
        public void Table_NoticeOnlyInADeWorld_ShownWhenEntered()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, now: 1000));
            S8Rules.Decision d = Step(ref m, In(PeerState.Exited, ViewState.Underwater, now: 2000, inWorld: false));
            Assert.IsTrue(d.Active); Assert.IsFalse(d.NoticeNow, "menu / loading: no HUD");
            Assert.IsFalse(Step(ref m, In(PeerState.Exited, now: 20000, site: false)).NoticeNow, "a world without DE_4546B: no notice");
            Assert.IsTrue(Step(ref m, In(PeerState.Exited, now: 30000)).NoticeNow, "a DE world entered while the renderer is still offline");
        }

        [TestMethod]
        public void Table_Recovery_RearmsAndReportsDuration_StallThenExitUsesExitText()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, ViewState.Underwater, now: 1000));
            Step(ref m, In(PeerState.Stalled, ViewState.Underwater, now: 4000));
            S8Rules.Decision d = Step(ref m, In(PeerState.Alive, ViewState.Surfacing, now: 15000));
            Assert.IsTrue(d.RecoveredEdge); Assert.AreEqual(11000UL, d.LostForMs); Assert.IsFalse(d.Active);
            Assert.AreEqual(1L, m.Recoveries);
            Assert.IsTrue(S8Rules.ResendOnRecovery(d.LostForMs, true), ">= 10 s: resend");
            // the next loss notifies again; a stall that ends in an exit takes the exit text
            Step(ref m, In(PeerState.Stalled, ViewState.Space, now: 20000));
            Step(ref m, In(PeerState.Exited, ViewState.Space, now: 22000));
            d = Step(ref m, In(PeerState.Exited, ViewState.Space, now: 30000));
            Assert.IsTrue(d.NoticeNow);
            Assert.AreEqual(PeerState.Exited, m.LostPeer);
            Assert.AreEqual(2L, m.Losses);
            // a detached link is a loss too
            var m2 = new S8Rules.Memory();
            Step(ref m2, In(PeerState.Alive, ViewState.Underwater, now: 1000));
            d = Step(ref m2, In(PeerState.Alive, ViewState.Underwater, now: 1500, linked: false));
            Assert.IsTrue(d.Active); Assert.IsTrue(d.NoticeNow); Assert.AreEqual(PeerState.Exited, m2.LostPeer);
        }

        [TestMethod]
        public void Guard_AudioRestore_ExitPoll_Resend()
        {
            Assert.IsFalse(S8Rules.PeerLost(false, false), "unlinked: the render thread's own path");
            Assert.IsFalse(S8Rules.PeerLost(true, true));
            Assert.IsTrue(S8Rules.PeerLost(true, false));

            var o = new ViewOutputs { SeGameFactor = 0.7f, SeMusicFactor = 0f, WaterModFactor = 0f, MixLevel = 0.6, SeMixActive = true, SnBusTarget = 1f };
            S8Rules.RestoreAudio(ref o);
            Assert.AreEqual(1f, o.SeGameFactor); Assert.AreEqual(1f, o.SeMusicFactor); Assert.AreEqual(1f, o.WaterModFactor);
            Assert.AreEqual(0.0, o.MixLevel); Assert.IsFalse(o.SeMixActive); Assert.AreEqual(0f, o.SnBusTarget);
            var cfgMix = new AudioController();
            AudioDecision ad = cfgMix.Tick(ref o, true, false, 0.8f, 0.5f);
            Assert.AreEqual(0.8f, ad.Game, 1e-6); Assert.AreEqual(0.5f, ad.Music, 1e-6); Assert.AreEqual(1f, ad.WaterScale); Assert.IsFalse(ad.MixActive);

            Assert.IsFalse(S8Rules.ExitPollEveryTick(false, false, false, false));
            Assert.IsTrue(S8Rules.ExitPollEveryTick(true, false, false, false));
            Assert.IsTrue(S8Rules.ExitPollEveryTick(false, true, false, false));
            Assert.IsTrue(S8Rules.ExitPollEveryTick(false, false, true, false));
            Assert.IsTrue(S8Rules.ExitPollEveryTick(false, false, false, true));

            Assert.IsFalse(S8Rules.ResendOnRecovery(9999, true));
            Assert.IsTrue(S8Rules.ResendOnRecovery(10000, true));
            Assert.IsTrue(S8Rules.ResendOnRecovery(100, false));
        }

        [TestMethod]
        public void DisplayController_PeerLost_DrawsSceneAndSnapsTheCrossfade()
        {
            var ops = new FakeWindowOps();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            var dc = new DisplayController(rd, new S2Config());
            IntPtr se = new IntPtr(0x1000), sn = new IntPtr(0x2000);
            var l = new LinkFacts { Linked = true, PeerAlive = true, Peer = PeerState.Alive, SnPhase = SnPhase.Active, HelloEpoch = 1, SnHwnd = sn };
            var fb = new DisplayFeedback();
            uint req = ViewRequest.Margins | ViewRequest.SceneSkip;
            dc.Post(ref l, se, S2State.Underwater, req, false, true, false, true, ref fb, 0, true);
            Assert.IsTrue(dc.LastRequest.Skip, "alive: the machine's skip passes");
            Assert.IsFalse(dc.PeerLostNow);
            l.PeerAlive = false; l.Peer = PeerState.Stalled;
            dc.Post(ref l, se, S2State.Underwater, req, false, true, false, true, ref fb, 0, true);
            Assert.IsFalse(dc.LastRequest.Skip, "stalled: SE's scene on this frame");
            Assert.IsTrue(dc.LastRequest.Margins, "margins go off through the render thread's drawn-present acknowledgement");
            Assert.IsTrue(dc.PeerLostNow);
            Assert.AreEqual(CrossfadeRules.Reason.Loss, dc.LastFade.Why, "the crossfade snaps to SE's picture");
            Assert.IsTrue(dc.LastFade.Snap);
            // a dev override never skips over a dead peer
            dc.ForcedSkip = true;
            dc.Post(ref l, se, S2State.Underwater, req, false, true, false, true, ref fb, 0, true);
            Assert.IsFalse(dc.LastRequest.Skip);
            Assert.AreEqual(1L, dc.PeerLostGuards);
            // A′: never lowered under a dead peer
            dc.ForcedSkip = null;
            dc.SetMode(DisplayDesign.APrime, "test");
            dc.Post(ref l, se, S2State.Underwater, ViewRequest.Lowered | ViewRequest.SceneSkip, false, true, false, true, ref fb, 0, true);
            Assert.IsFalse(dc.LastRequest.Lowered);
            Assert.IsFalse(dc.LastRequest.Skip);
            l.PeerAlive = true; l.Peer = PeerState.Alive;
            dc.Post(ref l, se, S2State.Underwater, ViewRequest.Lowered | ViewRequest.SceneSkip, false, true, false, true, ref fb, 0, false);
            Assert.IsTrue(dc.LastRequest.Lowered, "alive again: the machine's request passes (Waterline, no ramp)");
        }

        [TestMethod]
        public void Rows_AreJsonWithTheTraceKeys()
        {
            var m = new S8Rules.Memory();
            Step(ref m, In(PeerState.Alive, ViewState.Underwater, now: 1000));
            S8Rules.Decision d = Step(ref m, In(PeerState.Exited, ViewState.Underwater, now: 2000));
            string row = S8Rules.PeerRow("2026-10-18T10:00:00Z", 1234, PeerState.Alive, true, PeerState.Exited, true, 3, 4242, 7, "process 4242 exited",
                                         ViewState.Underwater, true, ref d);
            var o = (Dictionary<string, object>)MiniJson.Parse(row);
            Assert.AreEqual("s8.peer", o["ev"]);
            foreach (string k in new[] { "tick", "from", "to", "linked", "linkEpoch", "snPid", "snGen", "reason", "view", "picture", "fallback", "visible", "noticeAtMs", "lostForMs", "resendRule" })
                Assert.IsTrue(o.ContainsKey(k), k);
            Assert.AreEqual("Alive", o["from"]); Assert.AreEqual("Exited", o["to"]); Assert.AreEqual(true, o["fallback"]);
            string n = S8Rules.NoticeRow("2026-10-18T10:00:00Z", 1234, PeerState.Stalled, 10000, false, ViewState.Space);
            var no = (Dictionary<string, object>)MiniJson.Parse(n);
            Assert.AreEqual("s8.notice", no["ev"]); Assert.AreEqual("stalled", no["kind"]); Assert.AreEqual(S8Rules.NoticeStalled, no["text"]);

            var sb = new System.Text.StringBuilder("{\"a\":1");
            S8Rules.AppendMemory(sb, 5L << 30, 6L << 30, 300L << 20, 10, 5, 2, 41, S8Rules.CommitPct(100, 30));
            sb.Append('}');
            var mo = (Dictionary<string, object>)MiniJson.Parse(sb.ToString());
            Assert.AreEqual(5120.0, Convert.ToDouble(mo["workingSetMB"]));
            Assert.AreEqual(300.0, Convert.ToDouble(mo["gcHeapMB"]));
            Assert.AreEqual(70.0, Convert.ToDouble(mo["commitPct"]));
            Assert.IsTrue(double.IsNaN(S8Rules.CommitPct(0, 0)));
        }

        public TestContext TestContext { get; set; }

        [TestMethod]
        public unsafe void Link_StallRecovery_BumpsTheEpochAndRecordsTheStall()
        {
            // the S0 link rig: a real section created by the Wire creator rules in process, SE's link on a virtual clock
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                Assert.AreEqual(CreatorOutcome.Created, cr.Outcome, cr.ToString());
                using (SharedSection sn = cr.Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer);
                    uint epoch = link.LinkEpoch;
                    rig.Run(link, ref input, 12000, hv, snBeat: false);   // a 12 s stall (de.stallSn 12)
                    Assert.AreEqual(PeerStatus.Stalled, link.Peer);
                    Assert.AreEqual(0L, link.Recoveries);
                    rig.Run(link, ref input, 32, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer);
                    Assert.AreEqual(epoch + 1, link.LinkEpoch, "link epoch + 1 after the stall (same PID and generation)");
                    Assert.AreEqual(1L, link.Recoveries);
                    Assert.IsTrue(link.LastStallForMs >= S8Rules.ResendAfterStallMs, "stalled " + link.LastStallForMs + " ms");
                    Assert.IsTrue(S8Rules.ResendOnRecovery(link.LastStallForMs, true));
                    Assert.IsTrue(rig.Has("as CONTRACT §6 requires after >= 10 s"), "the RECOVER line names the resend");
                }
            }
        }
    }
}
