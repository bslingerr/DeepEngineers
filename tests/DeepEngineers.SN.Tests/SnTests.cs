using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// The Unity-free SN plugin code against real sections and processes: JSON, the creator path, C11 control, the SE peer watchdog, the window lookup. Ported from the S0
 /// scratch harness and extended for the review fixes.
 /// </summary>
    [TestClass]
    public unsafe class SnTests
    {
        // ----------------------------------------------------------------------------------------------- JSON

        [TestMethod]
        public void Json_ParseAndWrite()
        {
            var o = (Dictionary<string, object>)Json.Parse("{\"id\": 7, \"cmd\": \"de.ping\", \"args\": {\"a\": [1, 2.5, -3e2, true, null, \"x\\\"y\\u00e9\"]}}");
            Assert.IsTrue((double)o["id"] == 7 && (string)o["cmd"] == "de.ping", "flat fields");
            var a = (List<object>)((Dictionary<string, object>)o["args"])["a"];
            Assert.IsTrue(a.Count == 6 && (double)a[1] == 2.5 && (double)a[2] == -300 && (bool)a[3] && a[4] == null && (string)a[5] == "x\"yé",
                "nested array values and escapes");
            Assert.ThrowsException<FormatException>(() => Json.Parse("{\"id\": 1,"), "truncated object rejected");
            Assert.ThrowsException<FormatException>(() => Json.Parse("{\"id\": 1} x"), "trailing garbage rejected");
            string w = new JsonObjectWriter().Scalar("id", 3.0).Bool("ok", true).Str("s", "a\"b\n").Num("u", 18446744073709551615UL).ToString();
            Assert.AreEqual("{\"id\":3,\"ok\":true,\"s\":\"a\\\"b\\n\",\"u\":18446744073709551615}", w);
            Assert.AreEqual("a\"b\n", (string)((Dictionary<string, object>)Json.Parse(w))["s"], "writer/parser round trip");
        }

        // ---------------------------------------------------------------------------------------------- creator

        [TestMethod]
        public void Creator_Rules1to3_Refusals_PidReuse()
        {
            string name = SnRig.Name("creator");
            CreatorResult r1 = SectionLink.Create(SnFlags.BridgeMode | SnFlags.GhostingDisabled, 0x1234, name);
            Assert.IsTrue(r1.Outcome == CreatorOutcome.Created && r1.Generation == 1, "new section: " + r1);
            SharedSection s1 = r1.Section;
            HeaderView hv = s1.Header;
            Assert.IsTrue(hv.Magic == WireInfo.Magic && hv.SnPid == Win32.CurrentProcessId() && hv.SnGeneration == 1 &&
                          hv.SnPhase == SnPhase.Booting && hv.SnFlags == 3 && hv.SnHwnd == 0x1234 && hv.LayoutHash == LayoutHash.Compiled,
                          "header after rule 1 (magic, pid, gen 1, Booting, flags 3, hwnd)");
            ValidationResult v = SectionValidation.Validate(s1, new ValidationOptions { CheckRegionTable = true });
            Assert.IsTrue(v.Ok, "own section validates (steps 1-5 + region table): " + v);

            // An "SE" keeps the section alive; a harness produces C11c, SE produces C3/C4; then SN dies (dead snPid).
            SharedSection holder = SharedSection.OpenExisting(name, Layout.SectionSize);
            try
            {
                byte* b = holder.Base;
                var c11cProducer = new ByteRing(b, RingIndex.C11cCtlToSn);
                var c3Producer = new ByteRing(b, RingIndex.C3SeModels);
                var c4Producer = new ByteRing(b, RingIndex.C4SeParts);
                byte[] msg = Encoding.UTF8.GetBytes("{\"id\":1,\"cmd\":\"de.ping\"}");
                for (int i = 0; i < 3; i++) c11cProducer.TryWrite(C11Type.Json, msg, 0, msg.Length);
                byte[] blob = new byte[1000];
                c3Producer.TryWrite(C3Type.ModelEnd, blob, 0, 16);
                c4Producer.TryWrite(C4Type.EntityRemove, blob, 0, 8);
                SnStatus old = default(SnStatus); old.snPhase = SnPhase.Menu;
                Seqlock.WriteBytes(Layout.RegionPtr(b, RegionId.C7SnStatus), (byte*)&old, sizeof(SnStatus));
                uint c7SeqBefore = Seqlock.Current(Layout.RegionPtr(b, RegionId.C7SnStatus));
                ulong c11cHead = RingControl.ReadHead(b, RingIndex.C11cCtlToSn), c3Head = RingControl.ReadHead(b, RingIndex.C3SeModels);
                ulong c11dHead = RingControl.ReadHead(b, RingIndex.C11dCtlFromSn);
                s1.Dispose();
                HeaderView hh = holder.Header;
                hh.SnPid = SnRig.DeadPid();

                BridgeSection bs2 = SectionLink.CreateValidated(SnFlags.BridgeMode, 0x5678, name);
                CreatorResult r2 = bs2.Creator;
                Assert.IsTrue(r2.Outcome == CreatorOutcome.Reinitialised && r2.Generation == 2 && !bs2.Withdrawn && bs2.Own.Ok, "restart: " + r2 + " own=" + bs2.Own);
                HeaderView h2 = r2.Section.Header;
                Assert.IsTrue(h2.Magic == WireInfo.Magic && h2.SnPid == Win32.CurrentProcessId() && h2.SnPhase == SnPhase.Booting && h2.SnHwnd == 0x5678,
                    "rule 3 header: magic back, own pid, Booting, hwnd");
                Assert.IsTrue(RingControl.ReadTail(b, RingIndex.C11cCtlToSn) == c11cHead && RingControl.ReadTail(b, RingIndex.C3SeModels) == c3Head &&
                              RingControl.ReadTail(b, RingIndex.C4SeParts) == RingControl.ReadHead(b, RingIndex.C4SeParts), "consumer tails C3/C4/C11c := heads");
                Assert.IsTrue(RingControl.ReadHead(b, RingIndex.C11cCtlToSn) == c11cHead && RingControl.ReadHead(b, RingIndex.C3SeModels) == c3Head &&
                              RingControl.ReadHead(b, RingIndex.C11dCtlFromSn) == c11dHead, "producer heads kept");
                byte* c7 = Layout.RegionPtr(b, RegionId.C7SnStatus);
                uint c7SeqAfter = Seqlock.Current(c7);
                SnStatus st; uint seq;
                SeqlockReadStatus rs = Seqlock.TryReadBytes(c7, (byte*)&st, sizeof(SnStatus), 16, out seq);
                Assert.IsTrue(rs == SeqlockReadStatus.Fresh && c7SeqAfter == c7SeqBefore + 2 && st.snPhase == SnPhase.Booting && st.hostKind == 1 && st.snHwnd == 0x5678,
                    "own C7 rewritten in place (non-generic writer), seq continued " + c7SeqBefore + " -> " + c7SeqAfter);
                r2.Section.Dispose();

                // refusals: a live snPid (another live process), then a different layoutMajor; nothing written
                Process liveChild = SnRig.StartLiveChild(30);
                try
                {
                    HeaderView hl = holder.Header;
                    hl.SnPid = (uint)liveChild.Id;
                    var before = Resync.Snapshot(b, holder.MappedSize);
                    CreatorResult r3 = SectionLink.Create(SnFlags.BridgeMode, 0, name);
                    Assert.IsTrue(r3.Outcome == CreatorOutcome.Refused && r3.Field == "snPid", "live snPid refused: " + r3);
                    Assert.AreEqual(0, Resync.Diff(before, Resync.Snapshot(b, holder.MappedSize)).Count, "refusal wrote nothing");
                }
                finally { SnRig.Kill(liveChild); liveChild.Dispose(); }
                HeaderView h3 = holder.Header;
                h3.LayoutMajor = 2;
                var before2 = Resync.Snapshot(b, holder.MappedSize);
                CreatorResult r4 = SectionLink.Create(SnFlags.BridgeMode, 0, name);
                Assert.IsTrue(r4.Outcome == CreatorOutcome.Refused && r4.Field == "layoutMajor", "layout 2 refused: " + r4);
                Assert.AreEqual(0, Resync.Diff(before2, Resync.Snapshot(b, holder.MappedSize)).Count, "layout refusal wrote nothing");

                // PID reuse: the recorded snPid is our own PID (a dead predecessor's PID given to us) -> re-initialise
                h3.LayoutMajor = 1;
                h3.SnPid = Win32.CurrentProcessId();
                uint genBefore = h3.SnGeneration;
                CreatorResult r5 = SectionLink.Create(SnFlags.BridgeMode, 0, name);
                Assert.IsTrue(r5.Outcome == CreatorOutcome.Reinitialised && r5.Generation == genBefore + 1, "own PID recorded (PID reuse) -> " + r5);
                if (r5.Section != null) r5.Section.Dispose();
            }
            finally { holder.Dispose(); }
        }

        [TestMethod]
        public void Creator_SectionOfAnotherVersion_RefusedWithoutWriting_NotWithdrawn()
        {
            // Review finding 1 on the SN path: SE holds a section whose wire/site/damage version is not ours.
            foreach (string field in new[] { "wireVersion", "siteVersion", "damageScaleVersion" })
            {
                string name = SnRig.Name("version");
                CreatorResult first = SectionLink.Create(SnFlags.BridgeMode, 0, name);
                SharedSection holder = SharedSection.OpenExisting(name, Layout.SectionSize);
                try
                {
                    first.Section.Dispose();
                    HeaderView h = holder.Header;
                    h.SnPid = SnRig.DeadPid();
                    if (field == "wireVersion") h.WireVersion = 2;
                    if (field == "siteVersion") h.SiteVersion = 2;
                    if (field == "damageScaleVersion") h.DamageScaleVersion = 2;
                    var before = Resync.Snapshot(holder.Base, holder.MappedSize);
                    BridgeSection bs = SectionLink.CreateValidated(SnFlags.BridgeMode, 0, name);
                    Assert.AreEqual(CreatorOutcome.Refused, bs.Creator.Outcome, field + ": " + bs.Creator);
                    Assert.AreEqual(field, bs.Creator.Field);
                    Assert.AreEqual("1", bs.Creator.Expected);
                    Assert.AreEqual("2", bs.Creator.Found);
                    Assert.IsFalse(bs.Withdrawn, "refused before anything was written (nothing to withdraw)");
                    Assert.IsNull(bs.Creator.Section);
                    Assert.AreEqual(0, Resync.Diff(before, Resync.Snapshot(holder.Base, holder.MappedSize)).Count, field + ": nothing written");
                }
                finally { holder.Dispose(); }
            }
        }

        [TestMethod]
        public void OwnValidation_Classify_And_Withdraw()
        {
            string name = SnRig.Name("own");
            BridgeSection bs = SectionLink.CreateValidated(SnFlags.BridgeMode, 0, name);
            Assert.AreEqual(CreatorOutcome.Created, bs.Creator.Outcome);
            Assert.IsTrue(bs.Own.Ok && !bs.Withdrawn, "a new section validates: " + bs.Own);
            SharedSection s = bs.Creator.Section;
            SharedSection se = SharedSection.OpenExisting(name, Layout.SectionSize);
            try
            {
                HeaderView h = s.Header;
                Assert.AreEqual(OwnValidation.Ok, SectionLink.Classify(SectionLink.ValidateOwn(s, true)));
                Assert.AreEqual(OwnValidation.DisplayOff, SectionLink.Classify(SectionLink.ValidateOwn(s, false)), "step 6: display bridge off only");
                h.WireVersion = 2;
                ValidationResult v4 = SectionLink.ValidateOwn(s, true);
                Assert.AreEqual(4, v4.Step);
                Assert.AreEqual(OwnValidation.Withdraw, SectionLink.Classify(v4), "a foreign version word: withdraw");
                h.WireVersion = 1;
                *(ulong*)(s.Base + Layout.RegionTableOffset + 3 * Layout.RegionEntrySize + 16) = 0x9999; // region 3 size
                Assert.AreEqual(OwnValidation.Withdraw, SectionLink.Classify(SectionLink.ValidateOwn(s, true)), "region table: withdraw");
                SectionCreator.WriteRegionTable(s.Base);
                h.ClearMagic();
                Assert.AreEqual(OwnValidation.Withdraw, SectionLink.Classify(SectionLink.ValidateOwn(s, true)), "magic 0 on our own section: withdraw");
                h.PublishMagic();
                Assert.AreEqual(OwnValidation.Withdraw, SectionLink.Classify(null));

                SectionLink.Withdraw(s);
                Assert.IsFalse(s.IsOpen, "withdrawn: view closed");
                Assert.AreEqual(SnPhase.None, se.Header.SnPhase, "snPhase None: an attached SE reads PeerExited");
            }
            finally
            {
                if (s.IsOpen) s.Dispose();
                se.Dispose();
            }
        }

        // ---------------------------------------------------------------------------------------------- control

        private sealed class Handler : IControlHandler
        {
            public void Handle(ControlRequest req, JsonObjectWriter reply)
            {
                if (req.Cmd == "de.ping") { reply.Bool("ok", true).Str("cmd", req.Cmd); return; }
                if (req.Cmd == "boom") throw new InvalidOperationException("boom");
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "unknown");
            }
        }

        [TestMethod]
        public void Control_Replies_Counters_CorruptResync_FullRingDrops()
        {
            string name = SnRig.Name("control");
            CreatorResult r = SectionLink.Create(SnFlags.BridgeMode, 0, name);
            using (r.Section)
            {
                byte* b = r.Section.Base;
                var cc = new ControlConsumer(b);
                var harnessOut = new ByteRing(b, RingIndex.C11cCtlToSn);
                var harnessIn = new ByteRing(b, RingIndex.C11dCtlFromSn);
                string[] reqs =
                {
                    "{\"id\":1,\"cmd\":\"de.ping\"}",
                    "{\"id\":\"two\",\"cmd\":\"nope\"}",
                    "not json",
                    "{\"id\":4}",
                    "{\"id\":5,\"cmd\":\"boom\"}",
                };
                foreach (string q in reqs) { byte[] m = Encoding.UTF8.GetBytes(q); harnessOut.TryWrite(C11Type.Json, m, 0, m.Length); }
                byte[] bin = { 1, 2, 3 };
                harnessOut.TryWrite(7, bin, 0, bin.Length);
                cc.Poll(new Handler());
                var replies = new List<string>();
                byte[] buf = new byte[1 << 16];
                uint type; int n;
                while (harnessIn.TryRead(out type, buf, out n) == RingReadStatus.Ok) replies.Add(Encoding.UTF8.GetString(buf, 0, n));
                Assert.AreEqual(5, replies.Count, "5 replies for 5 JSON-type messages (type 7 ignored)");
                Assert.AreEqual("{\"id\":1,\"ok\":true,\"cmd\":\"de.ping\"}", replies[0], "ping reply echoes numeric id");
                StringAssert.StartsWith(replies[1], "{\"id\":\"two\",\"ok\":false", "unknown command echoes string id");
                StringAssert.StartsWith(replies[2], "{\"id\":null,\"ok\":false,\"error\":\"bad JSON", "bad JSON answered");
                StringAssert.Contains(replies[3], "missing \\\"cmd\\\"", "missing cmd answered");
                StringAssert.Contains(replies[4], "InvalidOperationException: boom", "handler exception answered");
                Assert.IsTrue(cc.Received == 6 && cc.BadType == 1 && cc.BadJson == 1, "counters received=" + cc.Received + " badType=" + cc.BadType + " badJson=" + cc.BadJson);

                // corrupt framing (a misaligned head written by a broken producer) -> consumer re-syncs
                RingControl.PublishHead(b, RingIndex.C11cCtlToSn, RingControl.ReadHead(b, RingIndex.C11cCtlToSn) + 3);
                cc.Poll(new Handler());
                Assert.IsTrue(cc.CorruptResyncs == 1 && RingControl.ReadTail(b, RingIndex.C11cCtlToSn) == RingControl.ReadHead(b, RingIndex.C11cCtlToSn),
                    "corrupt counters -> tail re-synced to head");

                // full C11d: replies dropped and counted, never waiting
                long dropsBefore = cc.ReplyDropped;
                for (int i = 0; i < 3; i++) cc.Reply("{\"pad\":\"" + new string('x', 90000) + "\"}");
                Assert.IsTrue(cc.ReplyDropped > dropsBefore, "full C11d drops and counts (dropped=" + cc.ReplyDropped + ")");
            }
        }

        // ---------------------------------------------------------------------------------------------- peer

        [TestMethod]
        public void Peer_NewPeer_Stall_LoadingLimit_Exit_Restart_Stale()
        {
            string name = SnRig.Name("peer");
            CreatorResult r = SectionLink.Create(SnFlags.BridgeMode, 0, name);
            using (r.Section)
            {
                HeaderView hv = r.Section.Header;
                var mon = new SePeerMonitor();
                Process child = SnRig.StartLiveChild();
                try
                {
                    uint pid = (uint)child.Id;
                    Assert.IsTrue(mon.Tick(Win32.TickCount64(), hv, SnPhase.Active) == SePeerEvent.None && mon.State == SePeerState.None, "no SE yet");
                    hv.SeHeartbeatMs = Win32.TickCount64(); hv.SePhase = SePhase.Menu; hv.WriteSeIdentity(pid, 1);
                    Assert.IsTrue(mon.Tick(Win32.TickCount64(), hv, SnPhase.Active) == SePeerEvent.NewPeer && mon.State == SePeerState.Alive, "SE attaches -> NewPeer");

                    // stall: snPhase Active -> 3 s timeout (Menu would be a loading phase: 120 s)
                    ulong t0 = Win32.TickCount64();
                    hv.SeHeartbeatMs = t0;
                    Assert.AreEqual(SePeerEvent.None, mon.Tick(t0 + 2999, hv, SnPhase.Active), "2.999 s: not stalled");
                    Assert.IsTrue(mon.Tick(t0 + 3000, hv, SnPhase.Active) == SePeerEvent.Stalled && mon.State == SePeerState.Stalled, "3 s: Stalled");
                    Assert.AreEqual(SePeerEvent.Recovered, mon.Tick(t0 + 3001, hv, SnPhase.Menu), "SN loading phase (Menu) -> 120 s timeout applies");
                    Assert.IsTrue(mon.Tick(t0 + 119999, hv, SnPhase.Menu) == SePeerEvent.None && mon.Tick(t0 + 120000, hv, SnPhase.Menu) == SePeerEvent.Stalled, "Menu: Stalled at 120 s");
                    hv.SeHeartbeatMs = t0 + 120000;
                    Assert.IsTrue(mon.Tick(t0 + 120001, hv, SnPhase.Menu) == SePeerEvent.Recovered && mon.State == SePeerState.Alive, "heartbeat fresh -> Recovered");

                    // a heartbeat that keeps changing is not liveness: kill the process; the next 1 s poll sees it
                    SnRig.Kill(child);
                    ulong tk = Win32.TickCount64();
                    SePeerEvent ev = SePeerEvent.None;
                    for (ulong dt = 0; dt <= 1100 && ev != SePeerEvent.Exited; dt += 10)
                    {
                        hv.SeHeartbeatMs = tk + dt; // the "SE" keeps beating (S0.M5c)
                        ev = mon.Tick(tk + 120001 + dt, hv, SnPhase.Menu);
                    }
                    Assert.IsTrue(ev == SePeerEvent.Exited && mon.State == SePeerState.Exited, "process exit -> Exited within the 1 s poll");
                }
                finally { SnRig.Kill(child); child.Dispose(); }

                // restart: new generation with a live pid -> NewPeer; generation bump -> NewPeer; sePhase Exiting -> Exited
                Process child2 = SnRig.StartLiveChild();
                try
                {
                    hv.SeHeartbeatMs = Win32.TickCount64(); hv.SePhase = SePhase.InWorld; hv.WriteSeIdentity((uint)child2.Id, 2);
                    Assert.IsTrue(mon.Tick(Win32.TickCount64(), hv, SnPhase.Menu) == SePeerEvent.NewPeer && mon.Generation == 2, "new generation -> NewPeer");
                    hv.WriteSeIdentity((uint)child2.Id, 3);
                    Assert.AreEqual(SePeerEvent.NewPeer, mon.Tick(Win32.TickCount64(), hv, SnPhase.Menu), "generation bump, same pid -> NewPeer");
                    hv.SePhase = SePhase.Exiting;
                    Assert.IsTrue(mon.Tick(Win32.TickCount64(), hv, SnPhase.Menu) == SePeerEvent.Exited && mon.ExitReason == "sePhase=Exiting", "sePhase Exiting -> Exited");
                    Assert.AreEqual(SePeerEvent.None, mon.MarkExited("again"), "exit reported once");
                    hv.WriteSeIdentity(0, 3);
                    Assert.IsTrue(mon.Tick(Win32.TickCount64(), hv, SnPhase.Menu) == SePeerEvent.None && mon.State == SePeerState.Detached, "pid 0 after exit -> quiet detach");
                }
                finally { SnRig.Kill(child2); child2.Dispose(); mon.Dispose(); }

                // stale SE blocks left in a re-initialised section never make Subnautica quit
                var m2 = new SePeerMonitor();
                try
                {
                    hv.SePhase = SePhase.Exiting; hv.SeHeartbeatMs = Win32.TickCount64() - 500000; hv.WriteSeIdentity(SnRig.DeadPid(), 7);
                    Assert.IsTrue(m2.Tick(Win32.TickCount64(), hv, SnPhase.Menu) == SePeerEvent.NewPeer && m2.State == SePeerState.Stale, "dead Exiting block -> Stale");
                    Assert.AreEqual(SePeerEvent.None, m2.MarkExited("C5 seExiting"), "stale block: C5 exit signal ignored");
                    bool quiet = true;
                    for (int i = 0; i < 5; i++) if (m2.Tick(Win32.TickCount64() + (ulong)i * 1500, hv, SnPhase.Menu) != SePeerEvent.None) quiet = false;
                    Assert.IsTrue(quiet && m2.State == SePeerState.Stale, "stale block stays quiet (no Exited, no quit)");
                }
                finally { m2.Dispose(); }

                var m3 = new SePeerMonitor();
                Process child3 = SnRig.StartLiveChild();
                try
                {
                    ulong t3 = Win32.TickCount64();
                    hv.SePhase = SePhase.Menu; hv.SeHeartbeatMs = t3 - 200000; hv.WriteSeIdentity((uint)child3.Id, 8); // hung SE
                    Assert.IsTrue(m3.Tick(t3, hv, SnPhase.Menu) == SePeerEvent.NewPeer && m3.State == SePeerState.Stale, "live process, old heartbeat -> Stale");
                    hv.SeHeartbeatMs = t3 + 10;
                    Assert.IsTrue(m3.Tick(t3 + 20, hv, SnPhase.Menu) == SePeerEvent.Recovered && m3.State == SePeerState.Alive, "heartbeat resumes -> Alive (Recovered)");
                    SnRig.Kill(child3);
                    SePeerEvent ev = SePeerEvent.None;
                    for (ulong dt = 20; dt <= 1200 && ev != SePeerEvent.Exited; dt += 10) { hv.SeHeartbeatMs = t3 + dt; ev = m3.Tick(t3 + dt, hv, SnPhase.Menu); }
                    Assert.AreEqual(SePeerEvent.Exited, ev, "then its exit is a real SE exit");
                }
                finally { SnRig.Kill(child3); child3.Dispose(); m3.Dispose(); }
            }
        }

        [TestMethod]
        public void Peer_AttachSe_HeartbeatBeforeIdentity_NoStaleOnAttach()
        {
            // Review finding 9: SE's attach writes the heartbeat, then PID and generation as one u64. A watcher that
 // sees the new identity always sees a fresh heartbeat (no spurious STALE, no double new-peer).
            string name = SnRig.Name("attach");
            CreatorResult r = SectionLink.Create(SnFlags.BridgeMode, 0, name);
            using (r.Section)
            {
                HeaderView hv = r.Section.Header;
                var mon = new SePeerMonitor();
                try
                {
                    hv.SeHeartbeatMs = Win32.TickCount64() - 600000; // an old SE's heartbeat left in the block
                    hv.SePhase = SePhase.Menu;
                    ulong now = Win32.TickCount64();
                    uint gen = hv.AttachSe(Win32.CurrentProcessId(), now);
                    Assert.AreEqual(SePeerEvent.NewPeer, mon.Tick(now, hv, SnPhase.Active));
                    Assert.AreEqual(SePeerState.Alive, mon.State, "fresh heartbeat with the new identity: Alive, not Stale");
                    Assert.AreEqual(gen, mon.Generation);
                    Assert.AreEqual(SePeerEvent.None, mon.Tick(now + 16, hv, SnPhase.Active), "one new peer only");
                }
                finally { mon.Dispose(); }
            }
        }

        // ---------------------------------------------------------------------------------------------- window

        [TestMethod]
        public void Window_ConsoleProcess_HasNoUnityWindow()
        {
            string how;
            IntPtr h = WindowRole.FindMainWindow(out how);
            Assert.AreEqual(IntPtr.Zero, h, "a test process has no UnityWndClass window: " + how);
        }
    }
}
