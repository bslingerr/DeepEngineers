using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The S0 review fixes on the SE link (findings 1, 2, 6) and the C# seeded failures S0.M5a-c of the peer detector
 /// (finding 4: before this the C# stall and exit detector had never been red). Each mutant test first runs the
 /// check green, then with the mutant switched on, and requires it to go red (ROADMAP §3 rule 1).
 /// </summary>
    [TestClass]
    public unsafe class ReviewFixTests
    {
        public TestContext TestContext { get; set; }

        // ------------------------------------------------------------------------------------------- finding 1

        [TestMethod]
        public void NewGeneration_IsRevalidated_EvenWhenMagicZeroWasNeverSampled()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                using (SharedSection sn = rig.CreateSn(name, Rig.SelfPid).Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);

                    // a valid restart that SE never saw at magic 0: accepted
                    hv.SnGeneration = 2;
                    rig.Run(link, ref input, 100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    Assert.AreEqual(2u, link.LastSnGeneration);
                    Assert.AreEqual(0, rig.Count("DE.Link REJECT"));

                    // a creator of another wire version re-initialised the section entirely between two SE ticks
                    hv.ClearMagic();
                    hv.WireVersion = 2;
                    hv.SnGeneration = 3;
                    hv.PublishMagic();
                    rig.Run(link, ref input, 16, hv);
                    Assert.AreEqual(LinkState.Searching, link.State, "detached: the new generation failed re-validation");
                    Assert.IsTrue(rig.Has("DE.Link REJECT step 4 field=wireVersion expected=1 found=2"), "REJECT names the field and both values");
                    Assert.IsTrue(rig.Has("re-validation of a new Subnautica generation"));

                    // nothing written by SE afterwards (it keeps rejecting the section at every re-open)
                    byte[] seBlock = new byte[0x80];
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)(sn.Base + 0x80), seBlock, 0, seBlock.Length);
                    ulong c5 = Resync.RegionChecksum(sn.Base, RegionId.C5SeState);
                    rig.Run(link, ref input, 5000, hv);
                    byte[] after = new byte[0x80];
                    System.Runtime.InteropServices.Marshal.Copy((IntPtr)(sn.Base + 0x80), after, 0, after.Length);
                    CollectionAssert.AreEqual(seBlock, after, "SE block unchanged after the REJECT");
                    Assert.AreEqual(c5, Resync.RegionChecksum(sn.Base, RegionId.C5SeState), "C5 unchanged after the REJECT");
                    Assert.AreEqual(LinkState.Searching, link.State);
                }
            }
        }

        // ------------------------------------------------------------------------------------------- finding 2

        [TestMethod]
        public void PeerExit_KeepsTheProcessHandle_DeadPidStaysPinned_CreatorRule2SeesItDead()
        {
            using (var rig = new Rig(TestContext))
            {
                Process child = Rig.StartLiveChild();
                uint cpid = (uint)child.Id;
                try
                {
                    string name = Rig.Name();
                    var link = new Link(() => true, name, Rig.MutexName());
                    var input = Rig.Input();
                    using (SharedSection sn = rig.CreateSn(name, cpid).Section)
                    {
                        HeaderView hv = sn.Header;
                        hv.SnPhase = SnPhase.ReadyIdle;
                        rig.Run(link, ref input, 1100, hv);
                        Assert.AreEqual(PeerStatus.Alive, link.Peer);

                        Rig.Kill(child);
                        child.Dispose(); // this process's own handle is gone: only the link's watch remains
                        child = null;
                        ulong t = rig.RunUntil(link, ref input, 1500, () => link.Peer == PeerStatus.Exited, hv, snBeat: false);
                        Assert.IsTrue(t <= Link.PeerProcessPollMs + 16, "Exited within the 1 s poll (" + t + " ms)");

                        // SE still holds a handle on the exited process: its PID cannot be reused, and OpenProcess
 // returns the signalled process object (what creator rule 2 checks).
                        Assert.IsTrue(link.HoldsPeerProcessHandle(cpid), "the handle is kept after PeerExited");
                        rig.Run(link, ref input, 5000, hv, snBeat: false);
                        Assert.IsTrue(link.HoldsPeerProcessHandle(cpid), "... for as long as no new generation appears");
                        Assert.AreEqual(ProcessState.Exited, Win32.QueryProcess(cpid), "dead PID pinned by SE's handle");
                        Assert.IsFalse(Win32.IsProcessAlive(cpid));

                        // the restarted Subnautica: creator rule 2 with the real liveness check re-initialises
                        var o = new CreatorOptions { Name = name, Sddl = WireInfo.SectionSddlForCurrentUser(), Pid = Rig.SelfPid, NowMs = () => rig.Now };
                        CreatorResult r2 = SectionCreator.CreateOrAttach(o);
                        Assert.AreEqual(CreatorOutcome.Reinitialised, r2.Outcome, r2.ToString());
                        using (r2.Section)
                        {
                            rig.Run(link, ref input, 200, hv);
                            Assert.AreEqual(2u, link.LastSnGeneration);
                            Assert.AreEqual(PeerStatus.Alive, link.Peer);
                            Assert.IsFalse(link.HoldsPeerProcessHandle(cpid), "the old handle is released at the new generation");
                            Assert.IsTrue(link.HoldsPeerProcessHandle(Rig.SelfPid), "and the new peer is watched");
                            TestContext.WriteLine("old snPid " + cpid + " after the new generation: " + Win32.QueryProcess(cpid) +
                                                  " (SE released its handle; another holder, if any, may keep the object)");
                        }
                    }
                }
                finally
                {
                    if (child != null) { Rig.Kill(child); child.Dispose(); }
                }
            }
        }

        // ------------------------------------------------------------------------------------------- finding 6

        [TestMethod]
        public void IdentityChange_IsPeerExitedForThePreviousPeer()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                using (SharedSection sn = rig.CreateSn(name, Rig.SelfPid).Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(0L, link.PeerExits);
                    hv.SnGeneration = hv.SnGeneration + 1;
                    rig.Run(link, ref input, 50, hv);
                    Assert.AreEqual(1L, link.PeerExits, "PeerExited counted for the previous generation");
                    Assert.IsTrue(rig.Has("DE.Link DOWN peer exited (snGeneration changed)"));
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "then the new peer is judged on its own");
                    hv.WriteSnIdentity(Rig.DeadPid, hv.SnGeneration);
                    rig.Run(link, ref input, 50, hv);
                    Assert.IsTrue(rig.Has("DE.Link DOWN peer exited (snPid changed)"));
                    Assert.IsTrue(link.PeerExits >= 2, "a second PeerExited (then the dead PID's own exit)");
                }
            }
        }

        [TestMethod]
        public void CreatorDiesDuringReinitialisation_ExitSeenWhileMagicIsZero()
        {
            using (var rig = new Rig(TestContext))
            {
                Process child = Rig.StartLiveChild();
                try
                {
                    string name = Rig.Name();
                    var link = new Link(() => true, name, Rig.MutexName());
                    var input = Rig.Input();
                    using (SharedSection sn = rig.CreateSn(name, (uint)child.Id).Section)
                    {
                        HeaderView hv = sn.Header;
                        hv.SnPhase = SnPhase.ReadyIdle;
                        rig.Run(link, ref input, 1100, hv);
                        Assert.AreEqual(PeerStatus.Alive, link.Peer);
                        hv.ClearMagic(); // a re-initialisation starts...
                        Rig.Kill(child);  //... and the process dies before publishing the magic again
                        ulong t = rig.RunUntil(link, ref input, 5000, () => link.Peer == PeerStatus.Exited, hv, snBeat: false);
                        Assert.IsTrue(t <= Link.PeerProcessPollMs + 16, "Exited within the 1 s poll while magic is 0 (" + t + " ms)");
                        StringAssert.Contains(link.PeerExitReason, "magic 0");
                        Assert.AreEqual(LinkState.Up, link.State, "still attached (the stuck-magic detach comes after 10 s)");
                    }
                }
                finally { Rig.Kill(child); child.Dispose(); }
            }
        }

        // ------------------------------------------------------------------------------------ S0.M5a / b / c

 /// <summary>S0.T-L1s "Exited ≤ 1 s after process exit" (1 s poll + one tick), against a live child process.</summary>
        private bool ExitedWithin1s(Rig rig)
        {
            Process child = Rig.StartLiveChild();
            try
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                using (SharedSection sn = rig.CreateSn(name, (uint)child.Id).Section)
                {
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    if (link.State != LinkState.Up || link.Peer != PeerStatus.Alive) return false;
                    Rig.Kill(child); // a crash: the heartbeat stops with it
                    ulong t = rig.RunUntil(link, ref input, 2500, () => link.Peer == PeerStatus.Exited, hv, snBeat: false);
                    TestContext.WriteLine("Exited after " + (t == ulong.MaxValue ? "never (2.5 s)" : t + " ms") + " mutants=" + Mutants.Active);
                    return t <= Link.PeerProcessPollMs + 16;
                }
            }
            finally { Rig.Kill(child); child.Dispose(); }
        }

        /// <summary>S0.T-L1s "Stalled ≤ 3 s after the heartbeats stop" (3.1 s: one tick and the clock granularity).</summary>
        private bool StalledWithin3s(Rig rig)
        {
            string name = Rig.Name();
            var link = new Link(() => true, name, Rig.MutexName());
            var input = Rig.Input();
            using (SharedSection sn = rig.CreateSn(name, Rig.SelfPid).Section)
            {
                HeaderView hv = sn.Header;
                hv.SnPhase = SnPhase.ReadyIdle;
                rig.Run(link, ref input, 1100, hv);
                if (link.Peer != PeerStatus.Alive) return false;
                ulong t = rig.RunUntil(link, ref input, 6000, () => link.Peer == PeerStatus.Stalled, hv, snBeat: false);
                TestContext.WriteLine("Stalled after " + (t == ulong.MaxValue ? "never (6 s)" : t + " ms") + " mutants=" + Mutants.Active);
                return t <= 3100;
            }
        }

        /// <summary>
 /// S0.T-L1s restart detection with a reused PID: the same snPid comes back with generation + 1 while the
 /// heartbeat keeps changing. Detected = PEER logged for the new generation and C8 re-synced.
 /// </summary>
        private bool RestartWithReusedPidDetected(Rig rig)
        {
            string name = Rig.Name();
            var link = new Link(() => true, name, Rig.MutexName());
            var input = Rig.Input();
            using (SharedSection sn = rig.CreateSn(name, Rig.SelfPid).Section)
            {
                byte* b = sn.Base;
                HeaderView hv = sn.Header;
                hv.SnPhase = SnPhase.ReadyIdle;
                rig.Run(link, ref input, 1100, hv);
                if (link.Peer != PeerStatus.Alive) return false;
                long peersBefore = link.NewPeers;
                var c8 = new EntryRing(b, RingIndex.C8SnDamage);
                for (int i = 0; i < 4; i++) c8.TryWrite(new DamageRecord { dedupSeq = (uint)i });
                hv.WriteSnIdentity(Rig.SelfPid, hv.SnGeneration + 1); // same PID, new generation
                rig.Run(link, ref input, 300, hv);
                bool detected = link.NewPeers == peersBefore + 1 && link.LastSnGeneration == 2 &&
                                RingControl.ReadTail(b, RingIndex.C8SnDamage) == RingControl.ReadHead(b, RingIndex.C8SnDamage);
                TestContext.WriteLine("restart with reused PID " + (detected ? "detected" : "NOT detected") + " mutants=" + Mutants.Active);
                return detected;
            }
        }

        [TestMethod]
        public void S0M5a_DetectorIgnoringProcessExit_TurnsExitedWithin1sRed()
        {
            using (var rig = new Rig(TestContext))
            {
                Assert.IsTrue(ExitedWithin1s(rig), "green without the mutant");
                Mutants.IgnoreProcessExit = true;
                Assert.IsFalse(ExitedWithin1s(rig), "S0.M5a must turn the check red");
            }
        }

        [TestMethod]
        public void S0M5b_StallTimeout30s_TurnsStalledWithin3sRed()
        {
            using (var rig = new Rig(TestContext))
            {
                Assert.IsTrue(StalledWithin3s(rig), "green without the mutant");
                Mutants.StallTimeout30s = true;
                Assert.IsFalse(StalledWithin3s(rig), "S0.M5b must turn the check red");
            }
        }

        [TestMethod]
        public void S0M5c_HeartbeatLivenessWithReusedPid_TurnsRestartDetectionRed()
        {
            using (var rig = new Rig(TestContext))
            {
                Assert.IsTrue(RestartWithReusedPidDetected(rig), "green without the mutant");
                Mutants.HeartbeatLiveness = true;
                Assert.IsFalse(RestartWithReusedPidDetected(rig), "S0.M5c must turn the check red");
            }
        }

        [TestMethod]
        public void Mutants_SwitchesAndNames()
        {
            using (new Rig(TestContext))
            {
                StringAssert.StartsWith(Mutants.Set("S0.M5a", true), "S0.M5a ON");
                StringAssert.StartsWith(Mutants.Set("s0.m5b", true), "S0.M5b ON");
                StringAssert.StartsWith(Mutants.Set("S0.M5c", true), "S0.M5c ON");
                Assert.AreEqual("S0.M5a,S0.M5b,S0.M5c", Mutants.Active);
                Mutants.ClearAll();
                Assert.AreEqual("", Mutants.Active);
                StringAssert.StartsWith(Mutants.Set("S0.M9", true), "UNKNOWN");
            }
        }
    }
}
