using System;
using System.Diagnostics;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The S2 additions to <see cref="Link"/> against a real named section (no game): the C5 S2 fields (view state,
 /// approach, display design, flags, seRect, crossingTick, snBusTarget, crossfadeMs) and the S1 values when S2 is
 /// off; C7 read every tick (snReady, snHwnd); the per-tick process poll while the margins are on; the lost-window
 /// PeerExited report.
 /// </summary>
    [TestClass]
    public unsafe class S2LinkTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void C5_S2Fields_C7EveryTick_LostWindowExit()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                SeTickInput input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                Assert.AreEqual(CreatorOutcome.Created, cr.Outcome, cr.ToString());
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    hv.SnHwnd = 0xABCD;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    Assert.AreEqual(0xABCDUL, link.LastSnHwnd, "C0 snHwnd read");

                    // S2 off: the S1 values
                    SeState st = Rig.ReadC5(b);
                    Assert.IsTrue(st.viewState == ViewState.Space && st.approachMode == ApproachMode.None && st.displayMode == DisplayMode.F &&
                                  st.crossingTick == 0 && st.snBusTarget == 0f && st.crossfadeMs == 300f, "S1 C5 values while S2 is off");

                    // S2 on
                    input.S2Active = true;
                    input.ViewState = ViewState.Underwater;
                    input.ApproachMode = ApproachMode.Prime;
                    input.DisplayMode = DisplayMode.APrime;
                    input.S2Flags = SeStateFlags.SeeThrough | SeStateFlags.MarginsOn | SeStateFlags.ZOrderOk | SeStateFlags.SeMixActive | SeStateFlags.GuiScreenOpen;
                    input.SnBusTarget = 1f;
                    input.CrossfadeMs = 300f;
                    input.SeRect = new I32x4(0, 0, 2560, 1440);
                    input.CrossingTick = 777;
                    input.SiteActive = true;
                    input.Zone = Zone.Core;
                    input.PerfMode = PerfMode.Full;
                    input.TargetFps = 30;
                    rig.Run(link, ref input, 50, hv);
                    st = Rig.ReadC5(b);
                    Assert.AreEqual(ViewState.Underwater, st.viewState);
                    Assert.AreEqual(ApproachMode.Prime, st.approachMode);
                    Assert.AreEqual(DisplayMode.APrime, st.displayMode);
                    Assert.AreEqual(input.S2Flags | SeStateFlags.SeForeground, st.flags, "S2 flags OR'ed with the S0 ones");
                    Assert.AreEqual(1f, st.snBusTarget);
                    Assert.AreEqual(777UL, st.crossingTick);
                    Assert.IsTrue(st.seRect.z == 2560 && st.seRect.w == 1440);
                    Assert.IsTrue(st.perfMode == PerfMode.Full && st.targetFps == 30);

                    // C7 every tick (S1 polled it once a second)
                    var c7 = new SnStatus { snPhase = SnPhase.Active, presentedSeTick = 41, snHwnd = 0xBEEF, flags = SnStatusFlags.AreaBuilt };
                    Seqlock.Write(Layout.RegionPtr(b, RegionId.C7SnStatus), c7);
                    rig.Run(link, ref input, 16, hv);
                    Assert.IsTrue(link.HasSnStatus && link.LastSnStatus.presentedSeTick == 41 && link.LastSnStatus.snHwnd == 0xBEEF);
                    c7.presentedSeTick = 42;
                    Seqlock.Write(Layout.RegionPtr(b, RegionId.C7SnStatus), c7);
                    rig.Run(link, ref input, 16, hv);
                    Assert.AreEqual(42UL, link.LastSnStatus.presentedSeTick, "the next tick sees the next C7");

                    // the render thread's lost-window report: PeerExited at once (C5 overlay = Quitting acknowledgement too)
                    link.ReportPeerExited("snHwnd 0xbeef no longer a visible window");
                    Assert.AreEqual(PeerStatus.Exited, link.Peer);
                    rig.Run(link, ref input, 16, hv);
                    Assert.IsTrue((Rig.ReadC5(b).overlays & Overlays.PeerExited) != 0);
                    Assert.IsTrue(rig.Has("no longer a visible window"));
                }
            }
        }

        /// <summary>
 /// Review fix (finding 3): a hidden-window exit of the same peer is withdrawn when the window shows
 /// again; a destroyed-window exit is not (CONTRACT: PeerExited lasts until a new generation).
 /// </summary>
        [TestMethod]
        public void HiddenWindowExit_IsWithdrawn_DestroyedWindowExitIsNot()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                SeTickInput input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                Assert.AreEqual(CreatorOutcome.Created, cr.Outcome, cr.ToString());
                using (SharedSection sn = cr.Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer);

                    link.ReportPeerExited("snHwnd 0xbeef hidden for 250 ms or more", true);
                    Assert.AreEqual(PeerStatus.Exited, link.Peer);
                    rig.Run(link, ref input, 32, hv);
                    Assert.AreEqual(PeerStatus.Exited, link.Peer, "latched until withdrawn");
                    Assert.IsTrue(link.RecoverWindowExit("snHwnd 0xbeef visible again"));
                    rig.Run(link, ref input, 32, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "judged again on its heartbeat");
                    Assert.AreEqual(1L, link.WindowExitRecoveries);
                    Assert.IsTrue(rig.Has("PeerExited withdrawn"));
                    Assert.IsFalse(link.RecoverWindowExit("again"), "nothing to withdraw");

                    link.ReportPeerExited("snHwnd 0xbeef destroyed", false);
                    Assert.IsFalse(link.RecoverWindowExit("visible"), "a destroyed window is not recoverable");
                    rig.Run(link, ref input, 32, hv);
                    Assert.AreEqual(PeerStatus.Exited, link.Peer);
                }
            }
        }

        [TestMethod]
        public void FastExitPoll_SeesTheExitWithinATick()
        {
            using (var rig = new Rig(TestContext))
            {
                Process child = Rig.StartLiveChild();
                try
                {
                    string name = Rig.Name();
                    var link = new Link(() => true, name, Rig.MutexName());
                    SeTickInput input = Rig.Input(SePhase.InWorld);
                    CreatorResult cr = rig.CreateSn(name, (uint)child.Id);
                    Assert.AreEqual(CreatorOutcome.Created, cr.Outcome, cr.ToString());
                    using (SharedSection sn = cr.Section)
                    {
                        HeaderView hv = sn.Header;
                        hv.SnPhase = SnPhase.ReadyIdle;
                        rig.Run(link, ref input, 1500, hv);
                        Assert.AreEqual(PeerStatus.Alive, link.Peer);
                        link.FastExitPoll = true;
                        rig.Run(link, ref input, 32, hv);
                        Rig.Kill(child);
                        rig.Run(link, ref input, 16, hv);
                        Assert.AreEqual(PeerStatus.Exited, link.Peer, "every tick while the margins are on (ARCHITECTURE §5.14)");
                    }
                }
                finally { Rig.Kill(child); }
            }
        }
    }
}
