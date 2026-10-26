using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The SE link (src/DeepEngineers.SE/Link.cs) against real sections: searching, attach, heartbeat, C5, stall and
 /// recovery, restart and re-validation, exit, stuck re-initialisation, REJECT (S0.M1 in process), second instance,
 /// the exit prefix, C11 harness commands and the /de relay, phase computation. Ported from the S0 scratch harness
 ///.
 /// </summary>
    [TestClass]
    public unsafe class LinkTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public void Searching_NoSection_WaitAtMostEvery10s_SlowCadenceAfterAMinute()
        {
            using (var rig = new Rig(TestContext))
            {
                var link = new Link(() => true, Rig.Name(), Rig.MutexName());
                var input = Rig.Input();
                rig.Run(link, ref input, 30000);
                Assert.AreEqual(LinkState.Searching, link.State, "state Searching without a section");
                Assert.AreEqual(3, rig.Count("DE.Link WAIT"), "3 WAIT lines in 30 s");
                rig.Run(link, ref input, 60000);
                Assert.AreEqual(9, rig.Count("DE.Link WAIT"), "9 WAIT lines in 90 s");
                Assert.IsTrue(rig.Has("next try in 5 s"), "slow cadence (5 s) logged after the first minute");
            }
        }

        [TestMethod]
        public void Attach_Heartbeat_C5_Stall_LoadingLimits_Recovery()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                Assert.AreEqual(CreatorOutcome.Created, cr.Outcome, cr.ToString());
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.Menu;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State, "state Up");
                    Assert.AreEqual(Rig.SelfPid, hv.SePid, "sePid written");
                    Assert.AreEqual(1u, hv.SeGeneration, "seGeneration 1 (found 0 + 1)");
                    Assert.IsTrue(rig.Now - hv.SeHeartbeatMs <= 16, "SE heartbeat fresh");
                    Assert.AreEqual(SePhase.Menu, hv.SePhase, "sePhase Menu");
                    Assert.AreEqual(0x1234UL, hv.SeHwnd, "seHwnd");
                    SeState st = Rig.ReadC5(b);
                    Assert.IsTrue(st.seq != 0 && (st.seq & 1) == 0, "C5 seq even, non-zero");
                    Assert.IsTrue(st.zone == Zone.OffSite && st.viewState == ViewState.Space && st.perfMode == PerfMode.Idle && st.targetFps == 10, "C5 S0 constants");
                    Assert.IsTrue(st.siteHash == SiteV1.SiteHash && st.siteVersion == 1, "C5 siteHash/siteVersion");
                    Assert.IsTrue((st.flags & SeStateFlags.SeForeground) != 0 && (st.overlays & Overlays.Unfocused) == 0, "C5 foreground flag, no Unfocused overlay");
                    Assert.IsTrue(rig.Has("DE.Link UP seGen=1") && rig.Has("DE.Link PEER snPid=" + Rig.SelfPid), "UP and PEER logged");

                    // data in flight on the rings SE consumes
                    var c8 = new EntryRing(b, RingIndex.C8SnDamage);
                    for (int i = 0; i < 5; i++) c8.TryWrite(new DamageRecord { dedupSeq = (uint)i });
                    var c11aProd = new ByteRing(b, RingIndex.C11aCtlToSe);
                    c11aProd.TryWrite(C11Type.Json, Encoding.UTF8.GetBytes("{\"id\":9}"), 0, 8);
                    var c12Prod = new ByteRing(b, RingIndex.C12SnStatics);
                    c12Prod.TryWrite(C12Type.BatchClear, new BatchClear());

                    // stall: SN heartbeat stops (ReadyIdle: not a loading phase)
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 100, hv);
                    rig.Run(link, ref input, 2900, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "alive at 2.9 s without heartbeat");
                    rig.Run(link, ref input, 300, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Stalled, link.Peer, "Stalled after > 3 s");
                    Assert.IsTrue((Rig.ReadC5(b).overlays & Overlays.PeerStalled) != 0, "C5 overlay PeerStalled");
                    Assert.AreNotEqual(RingControl.ReadHead(b, RingIndex.C8SnDamage), RingControl.ReadTail(b, RingIndex.C8SnDamage), "C8 data in flight");

                    // recovery: same PID and generation → link epoch + 1, consumer tails re-synced
                    rig.Run(link, ref input, 100, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "Alive after recovery");
                    Assert.AreEqual(1u, link.LinkEpoch, "linkEpoch 1");
                    st = Rig.ReadC5(b);
                    Assert.IsTrue(st.linkEpoch == 1 && (st.overlays & Overlays.PeerStalled) == 0, "C5 linkEpoch 1, no PeerStalled");
                    foreach (RingIndex r in new[] { RingIndex.C8SnDamage, RingIndex.C12SnStatics, RingIndex.C11aCtlToSe })
                        Assert.AreEqual(RingControl.ReadHead(b, r), RingControl.ReadTail(b, r), "tail == head after recovery for " + r);
                    Assert.AreEqual(5UL, RingControl.ReadHead(b, RingIndex.C8SnDamage), "C8 head untouched");
                    Assert.IsTrue(rig.Has("DE.Link RECOVER linkEpoch=1"), "RECOVER logged");

                    // loading phases: 120 s limit
                    hv.SnPhase = SnPhase.LoadingSave;
                    rig.Run(link, ref input, 10000, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "no stall after 10 s while Subnautica is loading");
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 16, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Stalled, link.Peer, "stall as soon as the loading phase ends (age 10 s > 3 s)");
                    hv.SnPhase = SnPhase.Menu;
                    rig.Run(link, ref input, 100, hv);
                    rig.Run(link, ref input, 10000, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "Menu counts as a loading phase (CONTRACT §5.4 snPhase 1-3)");
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 100, hv);
                    Assert.AreEqual(2u, link.LinkEpoch, "linkEpoch 2 after the second recovery");
                    input.Phase = SePhase.Saving;
                    rig.Run(link, ref input, 10000, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer, "no stall after 10 s while SE is Saving");
                    input.Phase = SePhase.Menu;
                    rig.Run(link, ref input, 100, hv);

                    // immediate writes from other threads
                    link.WritePhaseNow(SePhase.LoadingWorld, rig.Now + 5);
                    Assert.IsTrue(hv.SePhase == SePhase.LoadingWorld && hv.SeHeartbeatMs == rig.Now + 5, "WritePhaseNow");
                    link.Beat(rig.Now + 7, 12);
                    Assert.IsTrue(hv.SeHeartbeatMs == rig.Now + 7 && hv.SeLoadStep == 12, "Beat writes heartbeat and seLoadStep");
                }
            }
        }

        [TestMethod]
        public void Restart_Revalidate_Exit_StuckReinitialisation()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                SharedSection sn = cr.Section;
                byte* b = sn.Base;
                HeaderView hv = sn.Header;
                hv.SnPhase = SnPhase.Menu;
                rig.Run(link, ref input, 1100, hv);
                Assert.AreEqual(LinkState.Up, link.State);

                // re-initialisation by a restarted Subnautica, observed mid-way (magic 0)
                hv.ClearMagic();
                rig.Run(link, ref input, 500, hv);
                Assert.IsTrue(rig.Has("DE.Link WAIT peer re-initialising"), "WAIT re-initialising logged");
                Assert.IsTrue(rig.Now - hv.SeHeartbeatMs <= 16, "SE keeps its heartbeat while magic is 0");
                var c8 = new EntryRing(b, RingIndex.C8SnDamage);
                for (int i = 0; i < 3; i++) c8.TryWrite(new DamageRecord { dedupSeq = (uint)i });
                hv.SnGeneration = hv.SnGeneration + 1;
                hv.PublishMagic();
                rig.Run(link, ref input, 200, hv);
                Assert.IsTrue(rig.Has("DE.Link UP re-validated"), "re-validated after the magic came back");
                Assert.AreEqual(2u, link.LastSnGeneration, "sees snGeneration 2");
                Assert.AreEqual(RingControl.ReadHead(b, RingIndex.C8SnDamage), RingControl.ReadTail(b, RingIndex.C8SnDamage), "C8 tail re-synced");
                Assert.IsTrue(rig.Has("DE.Link PEER snPid=" + Rig.SelfPid + " snGen=2"), "PEER logged for generation 2");
                Assert.IsTrue(link.SeGeneration == 1 && hv.SeGeneration == 1, "SE generation unchanged (SE did not re-attach)");

                // the full creator path (rule 2/3) while SE is attached
                CreatorResult cr2 = rig.CreateSn(name, Rig.SelfPid, _ => false);
                Assert.IsTrue(cr2.Outcome == CreatorOutcome.Reinitialised && cr2.Generation == 3, "creator re-initialised: " + cr2);
                rig.Run(link, ref input, 200, hv);
                Assert.IsTrue(link.LastSnGeneration == 3 && link.Peer == PeerStatus.Alive, "sees generation 3, alive");
                cr2.Section.Dispose();

                // exit: Quitting
                hv.SnPhase = SnPhase.Quitting;
                rig.Run(link, ref input, 50, hv);
                Assert.AreEqual(PeerStatus.Exited, link.Peer, "Exited on snPhase Quitting");
                Assert.IsTrue((Rig.ReadC5(b).overlays & Overlays.PeerExited) != 0, "C5 PeerExited (the Quitting acknowledgement)");

                // a new peer whose process is gone: exited after the 1 s process poll
                hv.SnPhase = SnPhase.Menu;
                hv.SnPid = Rig.DeadPid;
                hv.SnGeneration = 4;
                rig.Run(link, ref input, 1100, hv);
                Assert.IsTrue(link.Peer == PeerStatus.Exited && rig.Has("process " + Rig.DeadPid + " exited"), "Exited after the process poll (dead PID)");

                // stuck re-initialisation: magic 0 and a dead snPid for 10 s → detach
                hv.ClearMagic();
                rig.Run(link, ref input, 9000, hv);
                Assert.AreEqual(LinkState.Up, link.State, "still Up after 9 s of magic 0");
                rig.Run(link, ref input, 1500, hv);
                Assert.IsTrue(link.State == LinkState.Searching && rig.Has("re-initialisation stuck"), "detached after 10 s of magic 0 with a dead snPid");
                rig.Run(link, ref input, 11000, hv);
                Assert.IsTrue(rig.Has("section not ready (magic 0") && rig.Count("DE.Link REJECT") == 0 && link.State == LinkState.Searching,
                    "re-open sees magic 0 → WAIT (not REJECT), rate-limited");
                sn.Dispose();
                rig.Run(link, ref input, 11000);
                Assert.AreEqual(LinkState.Searching, link.State, "searching after the creator closed");
                for (int i = 1; i < rig.WaitTimes.Count; i++)
                    Assert.IsTrue(rig.WaitTimes[i] - rig.WaitTimes[i - 1] >= 10000, "every DE.Link WAIT at least 10 s after the previous one");
            }
        }

        [TestMethod]
        public void S0M1_Reject_WritesNothing_RepeatedAtMostOncePerMinute()
        {
            foreach (string mutant in new[] { "magic", "wireVersion", "layoutHash", "selftest" })
            {
                using (var rig = new Rig(TestContext))
                {
                    string name = Rig.Name();
                    CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                    using (SharedSection sn = cr.Section)
                    {
                        byte* b = sn.Base;
                        HeaderView hv = sn.Header;
                        bool selfTest = true;
                        if (mutant == "magic") *(uint*)b = 0x51454544; // "DEEQ"
                        if (mutant == "wireVersion") hv.WireVersion = 2;
                        if (mutant == "layoutHash") hv.LayoutHash = 0x1234;
                        if (mutant == "selftest") selfTest = false;
                        Dictionary<uint, ulong> before = Resync.Snapshot(b, sn.MappedSize);
                        var link = new Link(() => selfTest, name, Rig.MutexName());
                        var input = Rig.Input();
                        rig.Run(link, ref input, 1100, hv, snBeat: false);
                        List<string> diff = Resync.Diff(before, Resync.Snapshot(b, sn.MappedSize));
                        Assert.AreEqual(LinkState.Searching, link.State, mutant + ": not linked");
                        Assert.AreEqual(1, rig.Count("DE.Link REJECT"), mutant + ": one REJECT line");
                        Assert.IsTrue(hv.SePid == 0 && diff.Count == 0, mutant + ": sePid 0, region checksums unchanged (" + string.Join(",", diff.ToArray()) + ")");
                        rig.Run(link, ref input, 30000, hv, snBeat: false);
                        Assert.AreEqual(1, rig.Count("DE.Link REJECT"), mutant + ": same REJECT not repeated within a minute");
                        rig.Run(link, ref input, 31000, hv, snBeat: false);
                        Assert.AreEqual(2, rig.Count("DE.Link REJECT"), mutant + ": repeated after a minute");
                        Assert.AreEqual(0, Resync.Diff(before, Resync.Snapshot(b, sn.MappedSize)).Count, mutant + ": still nothing written after 62 s");
                    }
                }
            }
        }

        [TestMethod]
        public void SecondInstance_MutexHeldElsewhere_Dormant_NothingWritten()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name(), mx = Rig.MutexName();
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    HeaderView hv = sn.Header;
                    var held = new ManualResetEvent(false);
                    var release = new ManualResetEvent(false);
                    var holder = new Thread(() =>
                    {
                        NamedMutex m; int err;
                        NamedMutex.TryAcquire(mx, out m, out err);
                        held.Set();
                        release.WaitOne();
                        m.Dispose();
                    });
                    holder.Start();
                    held.WaitOne();
                    try
                    {
                        Dictionary<uint, ulong> before = Resync.Snapshot(sn.Base, sn.MappedSize);
                        var link = new Link(() => true, name, mx);
                        var input = Rig.Input();
                        rig.Run(link, ref input, 5000, hv, snBeat: false);
                        Assert.AreEqual(LinkState.Dormant, link.State, "Dormant");
                        Assert.AreEqual(1, rig.Count("DE.Link REFUSE second instance"), "REFUSE second instance logged once");
                        Assert.IsTrue(hv.SePid == 0 && Resync.Diff(before, Resync.Snapshot(sn.Base, sn.MappedSize)).Count == 0, "nothing written");
                    }
                    finally
                    {
                        release.Set();
                        holder.Join();
                    }
                }
            }
        }

        [TestMethod]
        public void ExitPrefix_FromAnotherThread_WritesExitingAndSeExiting()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    HeaderView hv = sn.Header;
                    rig.Run(link, ref input, 1100, hv);
                    ulong at = rig.Now + 3;
                    var t = new Thread(() => link.OnExiting(at));
                    t.Start(); t.Join();
                    Assert.AreEqual(SePhase.Exiting, hv.SePhase, "C0 sePhase Exiting");
                    Assert.IsTrue((Rig.ReadC5(sn.Base).flags & SeStateFlags.SeExiting) != 0, "C5 seExiting");
                }
            }
        }

        [TestMethod]
        public void Dev_HarnessCommands_And_ChatRelay()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var dev = new DevChannel(link)
                {
                    VersionInfo = () => new Dictionary<string, object> { { "plugin", "test" } },
                    StateInfo = () => new Dictionary<string, object> { { "phase", "Menu" } },
                    SelfTestLine = () => "DE.WireSelfTest PASS n=50 (stub)",
                    VersionTest = v => "DE.Version " + (v.HasValue && v.Value != 1210014 ? "REFUSE" : "OK") + " (stub " + v + ")",
                    ShowChat = (s, t) => { rig.Chat.Add(t); TestContext.WriteLine("    chat> " + t); },
                };
                var input = Rig.Input();
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.DevControl = DevControl.SeChatRelay; // left by an SE that died while relaying
                    rig.Run(link, ref input, 1100, hv, dev: dev);
                    Assert.IsTrue(hv.DevControl == 0 && rig.Has("cleared a stale devControl bit 1"), "stale bit 1 cleared at attach");

                    // devControl 0: C11 inactive
                    var toSe = new ByteRing(b, RingIndex.C11aCtlToSe);
                    var fromSe = new ByteRing(b, RingIndex.C11bCtlFromSe);
                    byte[] req = Encoding.UTF8.GetBytes("{\"id\": 7, \"cmd\": \"de.ping\", \"args\": {}}");
                    toSe.TryWrite(C11Type.Json, req, 0, req.Length);
                    rig.Run(link, ref input, 100, hv, dev: dev);
                    uint type; int n; var buf = new byte[1 << 17];
                    Assert.AreEqual(RingReadStatus.Empty, fromSe.TryRead(out type, buf, out n), "no reply while devControl is 0");

                    // harness attaches (bit 0)
                    hv.DevControl = DevControl.HarnessAttached;
                    rig.Run(link, ref input, 50, hv, dev: dev);
                    Assert.AreEqual(RingReadStatus.Ok, fromSe.TryRead(out type, buf, out n), "reply on CtlFromSe");
                    string reply = Encoding.UTF8.GetString(buf, 0, n);
                    var o = (IDictionary<string, object>)MiniJson.Parse(reply);
                    Assert.IsTrue(MiniJson.GetNumber(o, "id") == 7 && MiniJson.GetBool(o, "ok") == true && MiniJson.GetBool(o, "pong") == true, "de.ping reply: " + reply);
                    foreach (string r in new[] { "{\"id\":8,\"cmd\":\"de.version\"}", "{\"id\":9,\"cmd\":\"de.nope\"}", "not json",
                                                 "{\"id\":10,\"cmd\":\"de.versionTest\",\"args\":{\"gameVersion\":1210015}}",
                                                 "{\"id\":11,\"cmd\":\"de.mutant\",\"args\":{\"name\":\"S0.M8\",\"on\":true}}",
                                                 "{\"id\":12,\"cmd\":\"de.mutant\",\"args\":{\"name\":\"S0.M8\",\"on\":false}}" })
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(r);
                        toSe.TryWrite(C11Type.Json, bytes, 0, bytes.Length);
                    }
                    rig.Run(link, ref input, 50, hv, dev: dev);
                    var replies = new List<string>();
                    while (fromSe.TryRead(out type, buf, out n) == RingReadStatus.Ok) replies.Add(Encoding.UTF8.GetString(buf, 0, n));
                    replies.ForEach(r => TestContext.WriteLine("    reply> " + r));
                    Assert.AreEqual(6, replies.Count, "6 replies");
                    Assert.IsTrue(replies[0].Contains("\"plugin\":\"test\"") && replies[1].Contains("\"ok\":false") && replies[2].Contains("\"id\":null") &&
                                  replies[3].Contains("REFUSE") && replies[4].Contains("S0.M8 ON") && replies[5].Contains("S0.M8 OFF"),
                                  "version/unknown/malformed/versionTest/mutant replies");
                    Assert.IsFalse(Mutants.VersionFromCompiledConst, "mutant switched back off");

                    // relay refused while the harness is attached
                    dev.HandleChat("/de ping", rig.Now);
                    Assert.IsTrue(rig.Chat.Any(c => c.Contains("not relayed: a test harness is attached")), "relay refused while bit 0 is set");
                    Assert.AreEqual(DevControl.HarnessAttached, hv.DevControl, "devControl untouched");

                    // harness leaves: relay takes bit 1
                    hv.DevControl = 0;
                    var toSn = new ByteRing(b, RingIndex.C11cCtlToSn);   // SN consumer side
                    var fromSn = new ByteRing(b, RingIndex.C11dCtlFromSn); // SN producer side
                    Assert.IsTrue(dev.HandleChat("/de ping", rig.Now), "/de ping consumed");
                    Assert.IsTrue(!dev.HandleChat("/dex", rig.Now) && !dev.HandleChat("hello", rig.Now), "other chat lines not consumed");
                    Assert.IsTrue(hv.DevControl == DevControl.SeChatRelay && link.RelayHeld, "devControl bit 1 set by the relay");
                    Assert.AreEqual(RingReadStatus.Ok, toSn.TryRead(out type, buf, out n), "request on CtlToSn");
                    string relayed = Encoding.UTF8.GetString(buf, 0, n);
                    var ro = (IDictionary<string, object>)MiniJson.Parse(relayed);
                    Assert.IsTrue(MiniJson.GetString(ro, "cmd") == "de.ping" && MiniJson.GetObject(ro, "args") != null, "relayed request: " + relayed);
                    byte[] snReply = Encoding.UTF8.GetBytes("{\"id\":" + MiniJson.GetNumber(ro, "id") + ",\"ok\":true,\"side\":\"sn\"}");
                    fromSn.TryWrite(C11Type.Json, snReply, 0, snReply.Length);
                    rig.Run(link, ref input, 50, hv, dev: dev);
                    Assert.IsTrue(rig.Chat.Any(c => c.StartsWith("SN de.ping (") && c.Contains("\"side\":\"sn\"")), "SN reply shown in chat");
                    dev.HandleChat("/de sn quit", rig.Now);
                    Assert.IsTrue(toSn.TryRead(out type, buf, out n) == RingReadStatus.Ok && Encoding.UTF8.GetString(buf, 0, n).Contains("\"cmd\":\"de.quit\""), "/de sn quit relayed as de.quit");
                    rig.Run(link, ref input, 31000, hv, dev: dev);
                    Assert.IsTrue(rig.Chat.Any(c => c.Contains("no reply after 30 s")), "pending request expires after 30 s");
                    rig.Run(link, ref input, 31000, hv, dev: dev);
                    Assert.IsTrue(hv.DevControl == 0 && !link.RelayHeld, "relay bit released after 60 s idle");

                    // a new peer generation re-syncs C11a (harness commands in flight are dropped)
                    hv.DevControl = DevControl.HarnessAttached;
                    toSe.TryWrite(C11Type.Json, req, 0, req.Length);
                    hv.SnGeneration = hv.SnGeneration + 1;
                    rig.Run(link, ref input, 50, hv, dev: dev);
                    Assert.AreEqual(RingReadStatus.Empty, fromSe.TryRead(out type, buf, out n), "in-flight harness command dropped by the generation re-sync");
                }
            }
        }

        [TestMethod]
        public void Phases_Compute()
        {
            Assert.AreEqual(SePhase.Menu, Phases.Compute(false, false, false, 0), "Menu at start");
            Phases.OnRunLoadStart(true, "MyGuiScreenGamePlay", 0);
            Assert.AreEqual(1u, Phases.WorldEpoch, "worldEpoch 1 after a gameplay RunLoad");
            Assert.AreEqual(SePhase.LoadingWorld, Phases.Compute(false, false, false, 1), "LoadingWorld during RunLoad");
            Phases.OnRunLoadEnd();
            Assert.AreEqual(SePhase.LoadingWorld, Phases.Compute(true, true, false, 2), "LoadingWorld until the gameplay screen reports done");
            Assert.AreEqual(SePhase.InWorld, Phases.Compute(true, true, true, 3), "InWorld when done");
            Phases.OnSaveEnter();
            Assert.AreEqual(SePhase.Saving, Phases.Compute(true, true, true, 4), "Saving");
            Phases.OnSaveExit(SePhase.InWorld);
            Assert.AreEqual(SePhase.InWorld, Phases.Compute(true, true, true, 5), "InWorld after save");
            Assert.AreEqual(SePhase.Menu, Phases.Compute(false, false, false, 6), "Menu after unload");
            Phases.OnRunLoadStart(false, "MyGuiScreenLoadInventory", 7);
            Assert.AreEqual(1u, Phases.WorldEpoch, "a non-gameplay load does not bump worldEpoch");
            Phases.OnRunLoadEnd();
            Assert.AreEqual(SePhase.Menu, Phases.Compute(true, false, false, 8), "Menu after a non-gameplay load");
            Phases.OnRunLoadStart(true, "MyGuiScreenGamePlay", 9);
            Phases.OnRunLoadEnd();
            Assert.AreEqual(SePhase.Menu, Phases.Compute(false, false, false, 10), "failed load (no session) → Menu");
            Phases.OnExit();
            Assert.AreEqual(SePhase.Exiting, Phases.Compute(true, true, true, 11), "Exiting latched");
        }
    }
}
