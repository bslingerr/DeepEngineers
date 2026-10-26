using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S8 minimum, Subnautica side: the reaction table (stall: mute, freeze,
 /// Idle, never quit; exit: mute, freeze, minimise, quit), the quit path and the quit step with its fallback deadline
 /// (quitting within 10 s of SE's exit), <c>de.stallSn</c> syntax, the Alt+Tab / ESC restore check and the memory text;
 /// the watchdog driven through a 60 s SE heartbeat gap never produces a quit event (S8.T-L1f, S8.M10's opposite).
 /// </summary>
    [TestClass]
    public unsafe class S8Tests
    {
        [TestMethod]
        public void ReactionTable_StallNeverQuits_ExitQuits()
        {
            S8Rules.Reaction r = S8Rules.React(SePeerState.Stalled);
            Assert.IsTrue(r.Mute && r.Freeze && r.Idle);
            Assert.IsFalse(r.Quit, "never quit on heartbeat loss");
            Assert.IsFalse(r.Minimise);
            r = S8Rules.React(SePeerState.Exited);
            Assert.IsTrue(r.Mute && r.Freeze && r.Idle && r.Minimise && r.Quit);
            foreach (SePeerState s in new[] { SePeerState.Stale, SePeerState.Detached, SePeerState.None })
            {
                r = S8Rules.React(s);
                Assert.IsTrue(r.Mute && r.Idle, s.ToString());
                Assert.IsFalse(r.Quit || r.Minimise, s + ": never quit");
            }
            r = S8Rules.React(SePeerState.Alive);
            Assert.IsFalse(r.Mute || r.Freeze || r.Idle || r.Quit || r.Minimise, "alive: C5 rules");
            Assert.IsTrue(S8Rules.QuitOn(SePeerEvent.Exited));
            foreach (SePeerEvent e in new[] { SePeerEvent.None, SePeerEvent.NewPeer, SePeerEvent.Stalled, SePeerEvent.Recovered, SePeerEvent.Detached })
                Assert.IsFalse(S8Rules.QuitOn(e), e.ToString());
        }

        [TestMethod]
        public void QuitPath_GameOwnPathNeverSaves()
        {
            Assert.AreEqual(S8Rules.QuitPath.MainMenuButton, S8Rules.ChooseQuitPath(true, false, false));
            Assert.AreEqual(S8Rules.QuitPath.IngameQuitAsync, S8Rules.ChooseQuitPath(false, true, false));
            Assert.AreEqual(S8Rules.QuitPath.ApplicationQuit, S8Rules.ChooseQuitPath(false, true, true), "permadeath would save");
            Assert.AreEqual(S8Rules.QuitPath.ApplicationQuit, S8Rules.ChooseQuitPath(false, false, false));
            Assert.AreEqual("IngameMenu.QuitGameAsync(true)", S8Rules.Name(S8Rules.QuitPath.IngameQuitAsync));
        }

        [TestMethod]
        public void QuitStep_IssueAtOnceAfterSeExit_FallbackWithinTheBudget()
        {
            // SE exited: no acknowledgement wait (issueAt = request time), fallback 5 s after the issue
            ulong t0 = 1000;
            Assert.AreEqual(S8Rules.QuitAction.Issue, S8Rules.QuitStep(t0, t0, false, false, 0, false, true));
            Assert.AreEqual(S8Rules.QuitAction.Wait, S8Rules.QuitStep(t0 + 4999, t0, false, true, t0, false, true));
            Assert.AreEqual(S8Rules.QuitAction.Fallback, S8Rules.QuitStep(t0 + 5000, t0, false, true, t0, false, true));
            Assert.AreEqual(S8Rules.QuitAction.Wait, S8Rules.QuitStep(t0 + 9000, t0, false, true, t0, true, true), "the fallback once");
            Assert.IsTrue(S8Rules.QuitFallbackAfterSeExitMs < S8Rules.QuitBudgetMs, "quitting within 10 s, gone within 15 s");
            // de.quit: wait up to 100 ms for SE's acknowledgement, then issue; fallback after 10 s
            Assert.AreEqual(S8Rules.QuitAction.Wait, S8Rules.QuitStep(t0 + 50, t0 + 100, false, false, 0, false, false));
            Assert.AreEqual(S8Rules.QuitAction.Issue, S8Rules.QuitStep(t0 + 50, t0 + 100, true, false, 0, false, false), "acknowledged early");
            Assert.AreEqual(S8Rules.QuitAction.Issue, S8Rules.QuitStep(t0 + 100, t0 + 100, false, false, 0, false, false));
            Assert.AreEqual(S8Rules.QuitAction.Wait, S8Rules.QuitStep(t0 + 9999, t0, false, true, t0, false, false));
            Assert.AreEqual(S8Rules.QuitAction.Fallback, S8Rules.QuitStep(t0 + 10000, t0, false, true, t0, false, false));
        }

        [TestMethod]
        public void StallSn_Arguments()
        {
            double s;
            string err;
            Assert.IsTrue(S8Rules.ParseStallSeconds(new Dictionary<string, object> { { "seconds", 10.0 } }, out s, out err) && s == 10.0);
            Assert.IsTrue(S8Rules.ParseStallSeconds(new List<object> { 2.5 }, out s, out err) && s == 2.5);
            Assert.IsTrue(S8Rules.ParseStallSeconds(12.0, out s, out err) && s == 12.0);
            Assert.IsTrue(S8Rules.ParseStallSeconds("3", out s, out err) && s == 3.0);
            Assert.IsTrue(S8Rules.ParseStallSeconds(Json.Parse("{\"seconds\": 60}"), out s, out err) && s == 60.0);
            Assert.IsFalse(S8Rules.ParseStallSeconds(Json.Parse("{\"seconds\": 61}"), out s, out err));
            Assert.IsFalse(S8Rules.ParseStallSeconds(0.0, out s, out err));
            Assert.IsFalse(S8Rules.ParseStallSeconds(null, out s, out err));
            Assert.IsFalse(S8Rules.ParseStallSeconds(new Dictionary<string, object>(), out s, out err));
            Assert.IsNotNull(err);
        }

        [TestMethod]
        public void RestoreAudit_AltTabAndEsc()
        {
            // under water, SE foreground, alive: audible, frozen only when wanted, styles hold
            var a = new S8Rules.AuditInputs { Valid = true, SeForeground = true, BusTarget = 1f, FreezeWanted = false, Frozen = false, Muted = false, WindowApplied = true, StylesOk = true };
            Assert.IsNull(S8Rules.AuditFailures(ref a));
            a.Muted = true;
            StringAssert.Contains(S8Rules.AuditFailures(ref a), "muted under water");
            // Alt+Tab away: must be muted
            a = new S8Rules.AuditInputs { Valid = true, SeForeground = false, BusTarget = 1f, Muted = false, WindowApplied = true, StylesOk = true };
            StringAssert.Contains(S8Rules.AuditFailures(ref a), "SE not foreground");
            a.Muted = true;
            Assert.IsNull(S8Rules.AuditFailures(ref a));
            // ESC (paused): frozen wanted; the freeze must be held
            a = new S8Rules.AuditInputs { Valid = true, SeForeground = true, BusTarget = 1f, FreezeWanted = true, Frozen = false, Muted = false, WindowApplied = true, StylesOk = true };
            StringAssert.Contains(S8Rules.AuditFailures(ref a), "not frozen");
            a.Frozen = true;
            Assert.IsNull(S8Rules.AuditFailures(ref a));
            // no live SE: muted; styles lost after a display change
            a = new S8Rules.AuditInputs { Valid = false, Muted = true, WindowApplied = true, StylesOk = false };
            StringAssert.Contains(S8Rules.AuditFailures(ref a), "window styles missing");
            // above water (bus target 0): either way is fine
            a = new S8Rules.AuditInputs { Valid = true, SeForeground = true, BusTarget = 0f, Muted = true };
            Assert.IsNull(S8Rules.AuditFailures(ref a));

            uint fgOn = S8Rules.EdgeKey(true, SeStateFlags.SeForeground, 0, SePeerState.Alive);
            uint fgOff = S8Rules.EdgeKey(true, SeStateFlags.SeForeground, Overlays.Unfocused, SePeerState.Alive);
            uint paused = S8Rules.EdgeKey(true, SeStateFlags.SeForeground | SeStateFlags.SePaused, 0, SePeerState.Alive);
            uint stalled = S8Rules.EdgeKey(false, 0, 0, SePeerState.Stalled);
            Assert.AreNotEqual(fgOn, fgOff);
            Assert.AreEqual("focus off", S8Rules.DescribeEdge(fgOn, fgOff));
            Assert.AreEqual("focus on", S8Rules.DescribeEdge(fgOff, fgOn));
            Assert.AreEqual("pause on", S8Rules.DescribeEdge(fgOn, paused));
            StringAssert.Contains(S8Rules.DescribeEdge(fgOn, stalled), "peer Alive->Stalled");
        }

        [TestMethod]
        public void MemoryText()
        {
            string t = S8Rules.MemoryText(2L << 30, 3L << 30, 512L << 20, 100, 20, 3, 55, S8Rules.CommitPct(1000, 400));
            Assert.AreEqual("mem{ws=2048.0MB priv=3072.0MB mono=512.0MB gc=100/20/3 load=55% commit=60.0%}", t);
            StringAssert.Contains(S8Rules.MemoryText(0, 0, 0, 0, 0, 0, 0, double.NaN), "commit=?");
        }

        [TestMethod]
        public void Watchdog_SixtySecondHeartbeatGap_NeverQuits_ThenExitQuits()
        {
            // S8.T-L1f in the unit rig: the SE block alive, its heartbeat stops for 60 s while its process lives, then
 // resumes; no event of the watchdog may make Subnautica quit; the process exit afterwards does
            string name = SnRig.Name("s8");
            CreatorResult r = SectionLink.Create(SnFlags.BridgeMode, 0, name);
            using (r.Section)
            {
                HeaderView hv = r.Section.Header;
                var mon = new SePeerMonitor();
                Process child = SnRig.StartLiveChild(3); // ends by itself after ~2 s (no process is killed)
                try
                {
                    uint pid = (uint)child.Id;
                    ulong t0 = Win32.TickCount64();
                    hv.SeHeartbeatMs = t0; hv.SePhase = SePhase.InWorld; hv.WriteSeIdentity(pid, 1);
                    Assert.AreEqual(SePeerEvent.NewPeer, mon.Tick(t0, hv, SnPhase.Active));
                    var events = new List<SePeerEvent>();
                    for (ulong dt = 100; dt <= 60000; dt += 100)
                    {
                        SePeerEvent e = mon.Tick(t0 + dt, hv, SnPhase.Active);
                        if (e != SePeerEvent.None) events.Add(e);
                        Assert.IsFalse(S8Rules.QuitOn(e), "no quit at +" + dt + " ms (" + e + ")");
                    }
                    CollectionAssert.AreEqual(new[] { SePeerEvent.Stalled }, events, "one stall, nothing else, over 60 s");
                    Assert.IsTrue(S8Rules.React(mon.State).Freeze && !S8Rules.React(mon.State).Quit);
                    hv.SeHeartbeatMs = t0 + 60100;
                    Assert.AreEqual(SePeerEvent.Recovered, mon.Tick(t0 + 60100, hv, SnPhase.Active), "resumes");
                    Assert.IsTrue(child.WaitForExit(15000), "the stand-in SE process ends by itself");
                    SePeerEvent ev = SePeerEvent.None;
                    for (ulong dt = 60200; dt <= 63000 && ev == SePeerEvent.None; dt += 100) { hv.SeHeartbeatMs = t0 + dt; ev = mon.Tick(t0 + dt, hv, SnPhase.Active); }
                    Assert.AreEqual(SePeerEvent.Exited, ev, "the process exit is seen within the 1 s poll");
                    Assert.IsTrue(S8Rules.QuitOn(ev));
                }
                finally
                {
                    child.WaitForExit(15000);
                    child.Dispose();
                    mon.Dispose();
                }
            }
        }
    }
}
