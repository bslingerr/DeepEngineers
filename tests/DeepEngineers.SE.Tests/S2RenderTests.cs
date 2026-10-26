using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// A scripted Windows for <see cref="RenderDisplay"/>: records every call and models DWM margins, the top-level
 /// z-order as a list, window visibility, the foreground and the
 /// composed pixels of the probe (one array per read).
 /// </summary>
    internal sealed class FakeWindowOps : IWindowOps
    {
        public static readonly IntPtr Other = new IntPtr(0x5000);
        public readonly List<string> Calls = new List<string>();
        public bool Margins;              // DWM state of seHwnd
        public bool Topmost = true;
        public IntPtr Fg;
        public bool SnExists = true, SnVisible = true;
        public int ZFailuresLeft;         // asynchronous SetWindowPos(sn, …) requests Subnautica drops
        public bool ZNeverWorks;
        public int ExtendHr;
        public int ForegroundAttempts;
        public bool ForegroundWorks = true;
        public uint[] Pixels;
        /// <summary>Composed pixels per probe read (dequeued; <see cref="Pixels"/> when empty).</summary>
        public readonly Queue<uint[]> PixelReads = new Queue<uint[]>();
        public readonly List<long> PixelReadAt = new List<long>();
        public long Now = 1000;
        public IntPtr Se, Sn;
        /// <summary>Subnautica's frame rate: its message pump runs once per frame (asynchronous SetWindowPos), C7 snFrame.</summary>
        public double SnFps = 60;
        /// <summary>false: asynchronous requests take effect at once (the old fake).</summary>
        public bool SnPump;

        /// <summary>Top-level windows, top first.</summary>
        public readonly List<IntPtr> Order = new List<IntPtr>();
        private readonly List<KeyValuePair<long, IntPtr>> _async = new List<KeyValuePair<long, IntPtr>>();

        private void EnsureOrder()
        {
            if (Order.Count == 0) { Order.Add(Se); Order.Add(Other); Order.Add(Sn); }
        }

        /// <summary>Subnautica directly under SE (the walker's normal verdict); set: rebuild the order.</summary>
        public bool ZGood
        {
            get { Pump(); return Below(Se) == Sn; }
            set
            {
                Order.Clear();
                Order.Add(Se);
                if (!value) Order.Add(Other);
                Order.Add(Sn);
                if (value) Order.Add(Other);
            }
        }

        /// <summary>SE directly under Subnautica (A′ lowered).</summary>
        public bool SeUnderSn { get { Pump(); return Below(Sn) == Se; } }

        public int IndexOf(IntPtr h) { Pump(); return Order.IndexOf(h); }

        private IntPtr Below(IntPtr h)
        {
            EnsureOrder();
            int i = Order.IndexOf(h);
            return i >= 0 && i + 1 < Order.Count ? Order[i + 1] : IntPtr.Zero;
        }

        public long SnFramePeriodQpc { get { return (long)(Stopwatch.Frequency / SnFps); } }
        public ulong SnFrameNow { get { return 1UL + (ulong)(Now / SnFramePeriodQpc); } }

        /// <summary>Runs Subnautica's message pump up to <see cref="Now"/>.</summary>
        public void Pump()
        {
            for (int k = 0; k < _async.Count; k++)
                if (_async[k].Key <= Now) { Move(Sn, _async[k].Value); _async.RemoveAt(k); k--; }
        }

        private void Move(IntPtr h, IntPtr after)
        {
            EnsureOrder();
            if (after == NativeWin.HWND_TOPMOST || after == NativeWin.HWND_NOTOPMOST) return;
            Order.Remove(h);
            if (after == NativeWin.HWND_TOP) Order.Insert(0, h);
            else if (after == NativeWin.HWND_BOTTOM) Order.Add(h);
            else
            {
                int i = Order.IndexOf(after);
                Order.Insert(i < 0 ? Order.Count : i + 1, h);
            }
        }

        public bool IsWindow(IntPtr h) { return h != Sn || SnExists; }
        public bool IsWindowVisible(IntPtr h) { return h != Sn || (SnExists && SnVisible); }

        public int ExtendFrame(IntPtr h, bool on)
        {
            Calls.Add("ExtendFrame " + (on ? "-1" : "0"));
            if (ExtendHr >= 0) Margins = on;
            return ExtendHr;
        }

        public bool SetPos(IntPtr h, IntPtr after, uint flags)
        {
            Calls.Add("SetPos " + h.ToInt64().ToString("x") + " after " + after.ToInt64().ToString("x") + " flags 0x" + flags.ToString("x"));
            EnsureOrder();
            if (h == Sn && (flags & NativeWin.SWP_ASYNCWINDOWPOS) != 0)
            {
                if (ZNeverWorks) return true;
                if (ZFailuresLeft > 0) { ZFailuresLeft--; return true; }
                if (!SnPump) { Move(h, after); return true; }
                long period = SnFramePeriodQpc;
                long due = (Now / period + 1) * period; // the next Subnautica frame
                _async.Add(new KeyValuePair<long, IntPtr>(due, after));
                return true;
            }
            Move(h, after);
            return true;
        }

        public bool AllowSetForeground(uint pid, out int error) { error = 0; Calls.Add("AllowSetForeground " + pid); return true; }

        public bool SetForeground(IntPtr h)
        {
            ForegroundAttempts++;
            Calls.Add("SetForeground");
            if (ForegroundWorks) Fg = h;
            return ForegroundWorks;
        }

        public IntPtr Foreground() { return Fg; }
        public bool IsTopmost(IntPtr h) { return Topmost; }
        public bool SetTopmost(IntPtr h, bool on) { Calls.Add("SetTopmost " + on); Topmost = on; return true; }

        public WalkResult Walk(IntPtr top, IntPtr below)
        {
            Calls.Add("Walk");
            Pump();
            EnsureOrder();
            var list = new List<WindowInfo>();
            int i = Order.IndexOf(top);
            for (int k = i + 1; i >= 0 && k < Order.Count; k++)
            {
                IntPtr h = Order[k];
                bool sn = h == Sn;
                list.Add(new WindowInfo { Hwnd = h, Visible = !sn || (SnExists && SnVisible), Width = sn || h == Se ? 2560 : 100, Height = sn || h == Se ? 1440 : 100 });
            }
            return ZOrderWalker.Decide(list, top, below);
        }

        public bool GetRect(IntPtr h, out I32x4 r) { r = new I32x4(0, 0, 2560, 1440); return true; }

        public uint[] ReadScreenPixels(int[] xs, int[] ys)
        {
            lock (PixelReads)
            {
                PixelReadAt.Add(Now);
                if (PixelReads.Count > 0) return PixelReads.Dequeue();
            }
            return Pixels ?? new uint[xs.Length];
        }

        public long Qpc() { return Now; }
    }

    internal sealed class FakeGpu : IRenderGpu
    {
        public int Fills;
        public bool Pending;
        public int Readbacks;
        public bool Fill(out string error) { error = null; Fills++; return true; }
        public bool BeginReadback(out string error) { error = null; Pending = true; Readbacks++; return true; }
        public bool ReadbackPending { get { return Pending; } }
        public bool TryFinishReadback(out AlphaStats stats, out string error)
        {
            error = null;
            stats = new AlphaStats { Width = 10, Height = 10, Pixels = 100, AlphaGt0 = 1, AlphaLt1 = 99 };
            Pending = false;
            return true;
        }
    }

    /// <summary>
 /// S2 SE side: the render-thread display executor (<see cref="RenderDisplay"/>) with a fake Windows (crossing order,
 /// S2.M1, z-order re-assert with retries, TopMost, foreground, the lost window, A′, the probe), and the whole
 /// update-thread path (<see cref="ViewStateDriver"/> over Wire's machine + <see cref="DisplayController"/>) driving it
 /// through dives.
 /// </summary>
    [TestClass]
    public class S2RenderTests
    {
        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        private static readonly IntPtr Se = new IntPtr(0x1000), Sn = new IntPtr(0x2000);
        private readonly List<string> _violations = new List<string>();
        private readonly List<string> _log = new List<string>();
        private int _frames, _skippedFrames;
        private int _presentsSinceSkip = int.MaxValue;

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); FallbackAPrime.DrawDisabled = false; DisplayController.CrossingHook = null; }

        private FakeWindowOps Ops() { return new FakeWindowOps { Se = Se, Sn = Sn, Fg = Se }; }

        /// <summary>
 /// One render frame (FullDraw with or without SE's scene, then Present) with the invariants of ARCHITECTURE §4.4:
 /// no cleared backbuffer presented without margins, no skip on the first margins frame, and the margins never dropped while fewer than DXGI's 3 queued frames followed a cleared one.
 /// </summary>
        private void Frame(RenderDisplay rd, FakeWindowOps ops, bool sceneWanted = true, bool fullDraw = true)
        {
            _frames++;
            ops.Now += Stopwatch.Frequency / 120;
            ops.Pump();
            bool skipped = false;
            if (fullDraw)
            {
                rd.OnFullDrawStart();
                skipped = rd.DecideSkip(sceneWanted);
                rd.DecideSkip(sceneWanted); // a second sprite call in the same frame (screenshot path) changes nothing
                rd.OnFullDrawEnd(true);
            }
            bool marginsAtPresent = ops.Margins;
            DisplayFeedback before = rd.Feedback;
            if (skipped)
            {
                _skippedFrames++;
                if (!marginsAtPresent) _violations.Add("frame " + _frames + ": cleared backbuffer presented without margins");
                else if (before.MarginsPresents < 1) _violations.Add("frame " + _frames + ": skipped on the first frame after margins went on");
            }
            _presentsSinceSkip = skipped ? 0 : (_presentsSinceSkip == int.MaxValue ? int.MaxValue : _presentsSinceSkip + 1);
            rd.OnPresented();
            rd.DrainLog(_log.Add);
            if (!ops.Margins && marginsAtPresent && _presentsSinceSkip < RenderDisplay.DxgiMaxFrameLatency)
                _violations.Add("frame " + _frames + ": margins dropped " + _presentsSinceSkip + " presents after a cleared frame (DXGI may still queue it)");
        }

        /// <summary>Frames with the request's C7 snFrame following Subnautica's clock (the update thread posts it every tick).</summary>
        private void Run(RenderDisplay rd, FakeWindowOps ops, ref DisplayRequest r, int frames, bool sceneWanted = true, bool fullDraw = true)
        {
            for (int k = 0; k < frames; k++)
            {
                r.SnFrame = ops.SnFrameNow;
                rd.SetRequest(ref r);
                Frame(rd, ops, sceneWanted, fullDraw);
            }
        }

        private static DisplayRequest Req(bool margins = false, bool skip = false, bool linked = true, DisplayDesign mode = DisplayDesign.F)
        {
            return new DisplayRequest { Linked = linked, Se = Se, Sn = Sn, SnPid = 4242, Mode = mode, Margins = margins, Skip = skip, AlphaFill = true };
        }

        // ================================================================================================ ordering

        [TestMethod]
        public void Render_CrossingOrder_MarginsBeforeSkip_SceneBeforeMarginsOff()
        {
            FakeWindowOps ops = Ops();
            var gpu = new FakeGpu();
            var rd = new RenderDisplay(ops) { Gpu = gpu };
            DisplayRequest r = Req();
            rd.SetRequest(ref r);
            for (int i = 0; i < 5; i++) Frame(rd, ops);
            Assert.IsFalse(ops.Margins);
            Assert.AreEqual(0, gpu.Fills, "no alpha fill while no margins are wanted");

            // Prime: margins wanted, scene drawn, alpha filled
            r = Req(margins: true);
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.IsTrue(ops.Margins, "margins applied in the Present postfix");
            Assert.AreEqual(1, gpu.Fills, "the frame before the margins is already alpha-filled");
            Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.MarginsReady);

            // Underwater requested at once with the margins: the skip still waits one presented margins frame
            FakeWindowOps ops2 = Ops();
            var rd2 = new RenderDisplay(ops2) { Gpu = new FakeGpu() };
            DisplayRequest both = Req(margins: true, skip: true);
            rd2.SetRequest(ref both);
            Frame(rd2, ops2);
            Assert.AreEqual(0, rd2.Feedback.SkippedPresents, "first frame: margins go on after it, scene drawn");
            Frame(rd2, ops2);
            Assert.AreEqual(0, rd2.Feedback.SkippedPresents, "second frame: margins on but not yet presented once");
            Frame(rd2, ops2);
            Assert.AreEqual(1, rd2.Feedback.SkippedPresents);
            for (int i = 0; i < 10; i++) Frame(rd2, ops2);

            // surfacing: skip off and margins off together -> scene first, margins off after DXGI's 3 queued frames
            DisplayRequest off = Req();
            rd2.SetRequest(ref off);
            for (int i = 1; i < RenderDisplay.MarginsOffAfterDrawnPresents; i++)
            {
                Frame(rd2, ops2);
                Assert.IsTrue(ops2.Margins, "scene frame " + i + ": the margins stay on (finding 5)");
                Assert.IsTrue(rd2.Feedback.LastPresentedSceneDrawn);
            }
            Frame(rd2, ops2);
            Assert.IsFalse(ops2.Margins, "margins off after " + RenderDisplay.MarginsOffAfterDrawnPresents + " scene-drawn frames");
            CollectionAssert.AreEqual(new[] { "ExtendFrame -1", "ExtendFrame 0" }, ops2.Calls.Where(c => c.StartsWith("ExtendFrame")).ToArray());

            // a forced skip without the margins wanted never skips (the margins could drop right after it)
            DisplayRequest skipOnly = Req(skip: true);
            rd2.SetRequest(ref skipOnly);
            for (int i = 0; i < 5; i++) Frame(rd2, ops2);
            Assert.IsFalse(rd2.Feedback.LastPresentedSkipped);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.Display margins ON (-1) hr=0x00000000 S_OK")), string.Join("\n", _log));
        }

        [TestMethod]
        public void Render_MarginsOff_TimesOutWithoutASceneFrame_AndFailedDwmCallRetries()
        {
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req(margins: true);
            rd.SetRequest(ref r);
            Frame(rd, ops); Frame(rd, ops);
            Assert.IsTrue(ops.Margins);
            r = Req();
            rd.SetRequest(ref r);
            for (int i = 0; i < RenderDisplay.MarginsOffTimeoutPresents; i++) { Frame(rd, ops, sceneWanted: false); Assert.IsTrue(ops.Margins, "waiting for a scene frame"); }
            Frame(rd, ops, sceneWanted: false);
            Assert.IsFalse(ops.Margins, "timeout (e.g. a world unload)");
            Assert.IsTrue(_log.Any(l => l.Contains("timeout")));

            // a failing DWM call: logged, retried 60 frames later, the skip never happens without margins
            FakeWindowOps bad = Ops();
            bad.ExtendHr = unchecked((int)0x80004005);
            var rd2 = new RenderDisplay(bad) { Gpu = new FakeGpu() };
            r = Req(margins: true, skip: true);
            rd2.SetRequest(ref r);
            for (int i = 0; i < 70; i++) Frame(rd2, bad);
            Assert.AreEqual(2, bad.Calls.Count(c => c == "ExtendFrame -1"));
            Assert.AreEqual(0, rd2.Feedback.SkippedPresents);
            Assert.IsTrue(_log.Any(l => l.Contains("FAILED hr=0x80004005")));
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        [TestMethod]
        public void Render_S2M1_DwmCallNeverMade_MarginsReportedApplied()
        {
            Mutants.Set("S2.M1", true);
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req(margins: true, skip: true);
            rd.SetRequest(ref r);
            for (int i = 0; i < 6; i++) Frame(rd, ops);
            Assert.AreEqual(0, ops.Calls.Count(c => c.StartsWith("ExtendFrame")), "S2.M1: no DWM call");
            Assert.IsTrue(rd.Feedback.MarginsOn, "reported as applied, so the pipeline proceeds");
            Assert.IsTrue(rd.Feedback.SkippedPresents > 0, "the scene is skipped: the composed picture is opaque black (T-D1 'opaque', S2.N0 red)");
            Assert.IsTrue(_log.Any(l => l.Contains("MUTANT S2.M1")));
        }

        // ================================================================================================ z-order

        [TestMethod]
        public void Render_ZOrder_ReassertVerifiedByTheWalker_AtMostThreeRetries()
        {
            FakeWindowOps ops = Ops();
            ops.ZFailuresLeft = 2;
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req();
            r.ZSeq = 1;
            r.ZWhy = "test";
            Run(rd, ops, ref r, 40);
            Assert.IsTrue(rd.Feedback.ZOrderOk, string.Join("\n", _log));
            Assert.AreEqual(3, rd.Feedback.ZAttempts, "two failures, then the third attempt verified");
            Assert.AreEqual(3, ops.Calls.Count(c => c.StartsWith("AllowSetForeground 4242")));
            string expectFlags = "flags 0x" + (NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE | NativeWin.SWP_ASYNCWINDOWPOS).ToString("x");
            Assert.AreEqual(3, ops.Calls.Count(c => c.StartsWith("SetPos 2000 after 1000 " + expectFlags)));
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.ZOrder OK after attempt 3")));

            // never works: 4 attempts (1 + 3 retries), then zOrderOk 0
            FakeWindowOps never = Ops();
            never.ZNeverWorks = true;
            var rd2 = new RenderDisplay(never) { Gpu = new FakeGpu() };
            DisplayRequest r2 = r;
            Run(rd2, never, ref r2, 60);
            Assert.IsFalse(rd2.Feedback.ZOrderOk);
            Assert.AreEqual(RenderDisplay.ZMaxAttempts, never.Calls.Count(c => c.StartsWith("SetPos 2000 after 1000")));
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.ZOrder FAIL after 4 attempts")));
            StringAssert.Contains(rd2.Feedback.LastWalk, "first=0x5000");

            // walker every frame afterwards: a window slipping in between is seen (S2.M9 oracle)
            ops.ZGood = false;
            r.WalkEveryFrame = true;
            Run(rd, ops, ref r, 1);
            Assert.IsFalse(rd.Feedback.ZOrderOk);
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.ZOrder LOST")));
        }

        /// <summary>
 /// Review fix (finding 6): Subnautica at 10 fps (Idle/Track) handles the asynchronous SetWindowPos at
 /// its next frame; at 165 Hz the old 2-present verification ran out of its 4 attempts first. The walker now waits
 /// for 2 new Subnautica frames (C7 snFrame) or 400 ms: one attempt, no retry, no FAIL.
 /// </summary>
        [TestMethod]
        public void Render_ZOrder_SlowSubnautica_VerifiedAfterItsFrames_NoFalseRetry()
        {
            FakeWindowOps ops = Ops();
            ops.SnPump = true;
            ops.SnFps = 10;
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req();
            r.ZSeq = 1;
            r.ZWhy = "link up";
            Run(rd, ops, ref r, 6); // 50 ms: the old verification (2 presents) would have walked and retried by now
            Assert.IsFalse(_log.Any(l => l.StartsWith("DE.ZOrder retry")), string.Join("\n", _log));
            Assert.IsTrue(rd.Feedback.ZPending, "still waiting for Subnautica's frames");
            Run(rd, ops, ref r, 60);
            Assert.IsTrue(rd.Feedback.ZOrderOk, string.Join("\n", _log));
            Assert.AreEqual(1, rd.Feedback.ZAttempts);
            Assert.IsFalse(_log.Any(l => l.StartsWith("DE.ZOrder retry") || l.StartsWith("DE.ZOrder FAIL")), string.Join("\n", _log));
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.ZOrder OK after attempt 1") && l.Contains("snFrame +")), string.Join("\n", _log));

            // no C7 snFrame (0): the 400 ms bound decides
            FakeWindowOps ops2 = Ops();
            ops2.SnPump = true;
            ops2.SnFps = 10;
            var rd2 = new RenderDisplay(ops2) { Gpu = new FakeGpu() };
            DisplayRequest r2 = Req();
            r2.ZSeq = 1;
            rd2.SetRequest(ref r2);
            for (int i = 0; i < 40; i++) Frame(rd2, ops2);
            Assert.IsTrue(rd2.Feedback.ZPending, "333 ms: not verified yet without snFrame");
            for (int i = 0; i < 20; i++) Frame(rd2, ops2);
            Assert.IsTrue(rd2.Feedback.ZOrderOk && rd2.Feedback.ZAttempts == 1, string.Join("\n", _log));
        }

        /// <summary>
 /// Review fix (finding 2): in A′, SE lowered under Subnautica is the wanted order. Re-asserts (Underwater
 /// entry, a toggle, the machine's pulse) keep it: SE lowers itself again instead of pushing Subnautica under SE,
 /// and the walker checks the inverted order, so zOrderOk stays true and no LOST pulse follows the lowering.
 /// </summary>
        [TestMethod]
        public void Render_APrime_LoweredOrder_IsKeptByEveryReassert()
        {
            FakeWindowOps ops = Ops();
            ops.SnPump = true;
            ops.ZGood = true;
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req(mode: DisplayDesign.APrime);
            r.WalkEveryFrame = true;
            Run(rd, ops, ref r, 10);
            Assert.IsTrue(rd.Feedback.ZOrderOk);

            // Underwater: lowered, drawing off, and the "Underwater entry" re-assert on the same request
            r.Lowered = true;
            r.Skip = true;
            r.ZSeq++;
            r.ZWhy = "Underwater entry";
            Run(rd, ops, ref r, 1, fullDraw: true);
            Assert.IsTrue(rd.Feedback.Lowered && FallbackAPrime.DrawDisabled);
            Assert.IsTrue(ops.SeUnderSn, "SE directly under Subnautica");
            Run(rd, ops, ref r, 60, fullDraw: false);
            Assert.IsTrue(ops.SeUnderSn, "still lowered: no re-assert raised SE again");
            Assert.IsTrue(rd.Feedback.ZOrderOk && rd.Feedback.ZInverted, string.Join("\n", _log));
            Assert.IsFalse(ops.Calls.Any(c => c.StartsWith("SetPos 2000 after 1000")), "no SetWindowPos(snHwnd, seHwnd) while lowered");
            Assert.IsFalse(_log.Any(l => l.StartsWith("DE.ZOrder LOST")), string.Join("\n", _log));

            // a stale asynchronous request of Subnautica's lands after the lowering: SE ends up on top while not drawing;
 // the walker sees it and SE lowers itself again (the machine's pulse is one more re-assert)
            ops.SetPos(Sn, Se, NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE | NativeWin.SWP_ASYNCWINDOWPOS);
            Run(rd, ops, ref r, 20, fullDraw: false);
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.ZOrder LOST")), "seen by the walker");
            r.ZSeq++;
            r.ZWhy = "view state machine pulse";
            Run(rd, ops, ref r, 5, fullDraw: false);
            Assert.IsTrue(ops.SeUnderSn && rd.Feedback.ZOrderOk, string.Join("\n", _log));
            Assert.IsTrue(_log.Any(l => l.Contains("A' lowered: SetWindowPos(seHwnd")));

            // back up: drawn first, raised after 3 drawn presents, the normal order checked again
            r.Lowered = false;
            r.Skip = false;
            Run(rd, ops, ref r, 1, fullDraw: false);
            Assert.IsTrue(rd.Feedback.Lowered && !FallbackAPrime.DrawDisabled, "drawing first");
            Run(rd, ops, ref r, RenderDisplay.RaiseAfterDrawnPresents - 1);
            Assert.IsTrue(rd.Feedback.Lowered, "not before " + RenderDisplay.RaiseAfterDrawnPresents + " drawn presents");
            Run(rd, ops, ref r, 1);
            Assert.IsFalse(rd.Feedback.Lowered);
            Run(rd, ops, ref r, 5);
            Assert.IsTrue(ops.ZGood && rd.Feedback.ZOrderOk && !rd.Feedback.ZInverted, string.Join("\n", _log));
        }

        [TestMethod]
        public void Render_TopMost_Foreground_LostWindow()
        {
            FakeWindowOps ops = Ops();
            ops.Fg = new IntPtr(0x9999);
            ops.ForegroundWorks = false;
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req();
            r.ForegroundSeq = 1;
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.IsFalse(ops.Topmost, "linked: HWND_NOTOPMOST");
            for (int i = 0; i < 120; i++) Frame(rd, ops);
            Assert.AreEqual(RenderDisplay.FgMaxAttempts, ops.ForegroundAttempts);
            Assert.IsTrue(rd.Feedback.ForegroundFailed, "HUD hint follows on the update thread");

            // SE's own mode change sets TopMost again: undone while linked
            ops.Topmost = true;
            rd.OnModeChanged();
            Assert.IsFalse(ops.Topmost);

            // unlink while SE is the foreground window: TopMost restored
            ops.Fg = Se;
            r = Req(linked: false);
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.IsTrue(ops.Topmost, string.Join("\n", _log));

            // foreground works on the first attempt
            FakeWindowOps ok = Ops();
            ok.Fg = new IntPtr(0x9999);
            var rd2 = new RenderDisplay(ok) { Gpu = new FakeGpu() };
            DisplayRequest r2 = Req();
            r2.ForegroundSeq = 7;
            rd2.SetRequest(ref r2);
            for (int i = 0; i < 10; i++) Frame(rd2, ok);
            Assert.AreEqual(1, ok.ForegroundAttempts);
            Assert.IsTrue(rd2.Feedback.Foreground && !rd2.Feedback.ForegroundFailed);

            // the Subnautica window is destroyed while see-through: scene at once, margins off after the scene frames
            DisplayRequest uw = Req(margins: true, skip: true);
            rd2.SetRequest(ref uw);
            for (int i = 0; i < 5; i++) Frame(rd2, ok);
            Assert.IsTrue(rd2.Feedback.LastPresentedSkipped);
            ok.SnExists = false;
            Frame(rd2, ok); // the watch sees it after this present
            Assert.IsTrue(rd2.Feedback.SnWindowLost && rd2.Feedback.SnWindowGone);
            Frame(rd2, ok);
            Assert.IsFalse(rd2.Feedback.LastPresentedSkipped, "the scene is drawn on the next frame");
            for (int i = 0; i < RenderDisplay.MarginsOffAfterDrawnPresents; i++) Frame(rd2, ok);
            Assert.IsFalse(ok.Margins, "margins off after the scene frames");
            ok.SnExists = true;
            for (int i = 0; i < 5; i++) Frame(rd2, ok);
            Assert.IsTrue(rd2.Feedback.SnWindowLost, "a destroyed handle never comes back");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        /// <summary>
 /// Review fix (finding 3): Subnautica's own restyle hides its window for a moment. A short hide draws
 /// SE's scene at once but is not a lost window; only a hide of 250 ms and 3 presents is, and that loss is
 /// withdrawn when the same window shows again.
 /// </summary>
        [TestMethod]
        public void Render_HiddenWindow_Debounced_AndWithdrawnWhenVisibleAgain()
        {
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest uw = Req(margins: true, skip: true);
            rd.SetRequest(ref uw);
            for (int i = 0; i < 6; i++) Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped);

            // a 5-frame hide (42 ms): SE's scene meanwhile, not lost, the skip resumes afterwards
            ops.SnVisible = false;
            for (int i = 0; i < 5; i++) Frame(rd, ops);
            Assert.IsFalse(rd.Feedback.SnWindowLost, "below the debounce");
            Assert.IsTrue(rd.Feedback.SnHiddenPresents > 0);
            Assert.IsFalse(rd.Feedback.LastPresentedSkipped, "SE's own scene while Subnautica is hidden");
            Assert.IsTrue(ops.Margins, "the margins stay on through a short hide");
            ops.SnVisible = true;
            for (int i = 0; i < 3; i++) Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped && rd.Feedback.SnHiddenPresents == 0);

            // hidden for 300 ms: lost, margins off after the scene frames; then visible again: withdrawn
            ops.SnVisible = false;
            int frames = 0;
            while (!rd.Feedback.SnWindowLost && frames < 100) { Frame(rd, ops); frames++; }
            Assert.IsTrue(rd.Feedback.SnWindowLost && !rd.Feedback.SnWindowGone);
            Assert.IsTrue(frames >= (int)(RenderDisplay.SnHiddenDebounceMs / (1000.0 / 120)), "lost after " + frames + " frames");
            for (int i = 0; i < 5; i++) Frame(rd, ops);
            Assert.IsFalse(ops.Margins);
            ops.SnVisible = true;
            Frame(rd, ops);
            Assert.IsFalse(rd.Feedback.SnWindowLost, "withdrawn");
            Assert.AreEqual(1L, rd.Feedback.SnWindowRecoveries);
            for (int i = 0; i < 4; i++) Frame(rd, ops);
            Assert.IsTrue(ops.Margins && rd.Feedback.LastPresentedSkipped, "see-through again");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.IsTrue(_log.Any(l => l.Contains("visible again")), string.Join("\n", _log));
        }

        [TestMethod]
        public void Render_APrime_LowerThenStopDrawing_DrawFirstThenRaise()
        {
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req(mode: DisplayDesign.APrime);
            r.Lowered = true;
            r.Margins = true; // ignored in A'
            r.Skip = true;
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.Lowered && FallbackAPrime.DrawDisabled);
            Assert.AreEqual(1, ops.Calls.Count(c => c == "SetPos 1000 after 2000 flags 0x" + (NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE).ToString("x")));
            Assert.AreEqual(0, ops.Calls.Count(c => c.StartsWith("ExtendFrame")), "no DWM margins in A'");
            // lowered frames: no FullDraw (DrawEnabled false) count as skipped for the render ack
            Frame(rd, ops, fullDraw: false);
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped);
            long skippedBefore = rd.Feedback.SkippedPresents;

            r.Lowered = false;
            rd.SetRequest(ref r);
            Frame(rd, ops, fullDraw: false);
            Assert.IsFalse(FallbackAPrime.DrawDisabled, "drawing re-enabled first");
            Assert.IsTrue(rd.Feedback.Lowered, "still below until drawn frames were presented");
            for (int i = 1; i < RenderDisplay.RaiseAfterDrawnPresents; i++) { Frame(rd, ops); Assert.IsTrue(rd.Feedback.Lowered, "drawn present " + i); }
            Frame(rd, ops);
            Assert.IsFalse(rd.Feedback.Lowered);
            Assert.IsTrue(ops.Calls.Last(c => c.StartsWith("SetPos")).StartsWith("SetPos 1000 after 0 "), "HWND_TOP");
            Assert.IsTrue(rd.Feedback.SkippedPresents >= skippedBefore);
        }

        // ================================================================================================ probe

        private static uint Rgb(int r, int g, int b) { return (uint)(r | (g << 8) | (b << 16)); }

        private static uint[] Five(uint c) { return new[] { c, c, c, c, c }; }

        /// <summary>Runs frames (letting the probe's worker thread finish) until a probe result is published.</summary>
        private void RunProbe(RenderDisplay rd, FakeWindowOps ops, int maxFrames = 600)
        {
            int done = rd.Feedback.ProbeDone;
            for (int i = 0; i < maxFrames && rd.Feedback.ProbeDone == done; i++)
            {
                Frame(rd, ops);
                Thread.Sleep(1);
            }
        }

        /// <summary>
 /// Review fix (finding 4): the probe reads the screen only after 3 + 3 skipped presents and 100 ms, reads
 /// twice at least 100 ms apart, and gives a verdict only when both reads agree.
 /// </summary>
        [TestMethod]
        public void Probe_WaitsForComposition_ReadsTwice_VerdictOnlyWhenBothAgree()
        {
            var magenta = new ProbeSpec { HasExpected = true, R = 255, G = 0, B = 255, Tolerance = 24 };
            uint mag = Rgb(255, 0, 255), sea = Rgb(30, 60, 90);

            // both reads magenta: HONOURED
            FakeWindowOps ops = Ops();
            ops.PixelReads.Enqueue(Five(mag));
            ops.PixelReads.Enqueue(Five(mag));
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            DisplayRequest r = Req();
            rd.SetRequest(ref r);
            for (int i = 0; i < 3; i++) Frame(rd, ops);
            long start = ops.Now;
            long skippedAtStart = rd.Feedback.SkippedPresents;
            r.ProbeSeq = 1;
            r.Probe = magenta;
            rd.SetRequest(ref r);
            RunProbe(rd, ops);
            Assert.AreEqual(1, rd.Feedback.ProbeVerdict, rd.Feedback.ProbeResult);
            Assert.IsTrue(rd.Feedback.ProbeHadExpected);
            StringAssert.StartsWith(rd.Feedback.ProbeResult, "HONOURED");
            Assert.AreEqual(2, ops.PixelReadAt.Count, "two reads");
            double ms1 = (ops.PixelReadAt[0] - start) * 1000.0 / Stopwatch.Frequency;
            double gap = (ops.PixelReadAt[1] - ops.PixelReadAt[0]) * 1000.0 / Stopwatch.Frequency;
            Assert.IsTrue(ms1 >= RenderDisplay.ProbeMinWaitMs, "first read after " + ms1 + " ms");
            Assert.IsTrue(gap >= RenderDisplay.ProbeSecondReadAfterMs, "second read " + gap + " ms later");
            Assert.IsTrue(rd.Feedback.SkippedPresents - skippedAtStart >= RenderDisplay.ProbeSkippedPresentsBeforeRead + RenderDisplay.ProbeSecondReadAfterSkipped);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));

            // the first read still shows SE's scene, the second the calibration colour: UNDECIDED (never NOT HONOURED)
            FakeWindowOps ops2 = Ops();
            ops2.PixelReads.Enqueue(Five(sea));
            ops2.PixelReads.Enqueue(Five(mag));
            var rd2 = new RenderDisplay(ops2) { Gpu = new FakeGpu() };
            DisplayRequest r2 = Req();
            r2.ProbeSeq = 1;
            r2.Probe = magenta;
            rd2.SetRequest(ref r2);
            RunProbe(rd2, ops2);
            Assert.AreEqual(-1, rd2.Feedback.ProbeVerdict, rd2.Feedback.ProbeResult);
            StringAssert.StartsWith(rd2.Feedback.ProbeResult, "UNDECIDED");

            // both reads without the colour: NOT HONOURED
            FakeWindowOps ops3 = Ops();
            ops3.PixelReads.Enqueue(Five(sea));
            ops3.PixelReads.Enqueue(Five(sea));
            var rd3 = new RenderDisplay(ops3) { Gpu = new FakeGpu() };
            DisplayRequest r3 = Req();
            r3.ProbeSeq = 1;
            r3.Probe = magenta;
            rd3.SetRequest(ref r3);
            RunProbe(rd3, ops3);
            Assert.AreEqual(0, rd3.Feedback.ProbeVerdict, rd3.Feedback.ProbeResult);
            StringAssert.StartsWith(rd3.Feedback.ProbeResult, "NOT HONOURED");
            Assert.IsFalse(rd3.Feedback.ProbeActive);
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.Display probe NOT HONOURED")), string.Join("\n", _log));
        }

        /// <summary>
 /// Review fix (finding 1): one NOT HONOURED probe never switches the design; two in a row (each two
 /// agreeing reads, with an expected colour) do when autoFallback is on; a probe without an expected colour or any
 /// other verdict resets the run; autoFallback off never switches.
 /// </summary>
        [TestMethod]
        public void AutoFallback_NeedsTwoNotHonouredProbesInARow()
        {
            var rd = new RenderDisplay(Ops());
            var dc = new DisplayController(rd, S2Config.Parse(null, null, null)) { Log = _log.Add, ForegroundWindow = () => Se };
            Assert.IsTrue(dc.AutoFallback);
            LinkFacts l = SnFacts(100);
            var fb = new DisplayFeedback();
            Action<int, bool> probe = (verdict, expected) =>
            {
                fb.ProbeDone++;
                fb.ProbeVerdict = verdict;
                fb.ProbeHadExpected = expected;
                dc.Post(ref l, Se, S2State.Prime, 0, false, true, false, true, ref fb);
            };
            probe(0, true);
            Assert.AreEqual(DisplayDesign.F, dc.Mode, "one NOT HONOURED probe");
            probe(-1, true);
            probe(0, true);
            Assert.AreEqual(DisplayDesign.F, dc.Mode, "an undecided probe reset the run");
            probe(0, false);
            Assert.AreEqual(DisplayDesign.F, dc.Mode, "no expected colour: does not count");
            probe(0, true);
            probe(0, true);
            Assert.AreEqual(DisplayDesign.APrime, dc.Mode, "two in a row");
            Assert.IsTrue(_log.Any(l2 => l2.StartsWith("DE.Display mode F -> A'")));

            var dc2 = new DisplayController(new RenderDisplay(Ops()), S2Config.Parse(null, null, null)) { Log = _log.Add, ForegroundWindow = () => Se };
            dc2.AutoFallback = false;
            var fb2 = new DisplayFeedback();
            for (int i = 0; i < 3; i++)
            {
                fb2.ProbeDone++;
                fb2.ProbeVerdict = 0;
                fb2.ProbeHadExpected = true;
                dc2.Post(ref l, Se, S2State.Prime, 0, false, true, false, true, ref fb2);
            }
            Assert.AreEqual(DisplayDesign.F, dc2.Mode, "autoFallback off");
            Assert.AreEqual(3, dc2.NotHonouredInARow);
        }

        /// <summary>
 /// Review fix (finding 17): a link whose peer has no window (the SE-alone baseline's passive host) does
 /// not set SE's window up: no foreground request, no HWND_NOTOPMOST; with Subnautica's window it does.
 /// </summary>
        [TestMethod]
        public void Controller_NoSubnauticaWindow_LeavesSeVanilla()
        {
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FakeGpu() };
            var dc = new DisplayController(rd, S2Config.Parse(null, null, null)) { Log = _log.Add, ForegroundWindow = () => Se };
            LinkFacts l = SnFacts(100);
            l.SnHwnd = IntPtr.Zero;
            l.C7.snHwnd = 0;
            var fb = new DisplayFeedback();
            for (int i = 0; i < 5; i++) { dc.Post(ref l, Se, S2State.Space, 0, false, false, false, true, ref fb); Frame(rd, ops); }
            Assert.IsFalse(dc.LastRequest.Linked);
            Assert.IsTrue(ops.Topmost, "TopMost untouched");
            Assert.IsFalse(_log.Any(x => x.StartsWith("DE.Display foreground request")));
            l = SnFacts(100);
            for (int i = 0; i < 5; i++) { dc.Post(ref l, Se, S2State.Space, 0, false, false, false, true, ref fb); Frame(rd, ops); }
            Assert.IsTrue(dc.LastRequest.Linked);
            Assert.IsFalse(ops.Topmost, "linked with Subnautica's window: HWND_NOTOPMOST");
            Assert.IsTrue(_log.Any(x => x.StartsWith("DE.Display foreground request (link up")));
        }

        // ============================================================================== update thread + Wire's machine

 /// <summary>A Subnautica that presents every SE tick one tick late, built and settled, and acknowledges the hello.</summary>
        private static LinkFacts SnFacts(ulong tick, bool settled = true, uint phase = SnPhase.Active)
        {
            var c7 = new SnStatus
            {
                snPhase = phase,
                flags = SnStatusFlags.AreaBuilt | SnStatusFlags.RectOk | SnStatusFlags.UiHidden | (settled ? SnStatusFlags.StreamingSettled : 0u),
                presentedSeTick = tick - 1,
                areaBuiltSeTick = tick - 1,
                helloAckEpoch = 3,
                snHwnd = (ulong)Sn.ToInt64(),
            };
            return new LinkFacts
            {
                Linked = true, PeerAlive = true, Peer = PeerState.Alive, SnPhase = phase, HaveC7 = true, C7 = c7,
                HelloEpoch = 3, SnHwnd = Sn, SnPid = 4242,
            };
        }

        private sealed class World
        {
            public readonly FakeWindowOps Ops;
            public readonly RenderDisplay Rd;
            public readonly DisplayController Dc;
            public readonly ViewStateDriver Driver;
            public readonly List<string> States = new List<string>();
            public readonly List<string> Rows = new List<string>();
            public ulong Tick = 5000;
            public long Us = 1000000;
            public double Y = 20, X = 100;
            public bool Settled = true;
            public bool Teleport;
            public uint Peer = PeerState.Alive;
            public S2RenderTests T;

            public readonly List<string> Fades = new List<string>();
            public readonly List<string> ViewRows = new List<string>();

            /// <summary>
 /// S7a: <paramref name="picture"/> is the view machine's picture policy input (the S2 pins pass
 /// <see cref="ViewPicture.Waterline"/>, "S2 policy, the fallback"); the fade hook is recorded in <see cref="Fades"/>.
 /// </summary>
            public World(S2RenderTests t, DisplayDesign mode = DisplayDesign.F, bool snPump = false, double snFps = 60, uint picture = ViewPicture.Site)
            {
                T = t;
                Ops = t.Ops();
                Ops.ZGood = false;
                Ops.SnPump = snPump;
                Ops.SnFps = snFps;
                Rd = new RenderDisplay(Ops) { Gpu = new FakeGpu() };
                var cfg = S2Config.Parse(null, mode == DisplayDesign.APrime ? "aprime" : null, null);
                Dc = new DisplayController(Rd, cfg) { Log = t._log.Add, ForegroundWindow = () => Se };
                Driver = new ViewStateDriver { Log = t._log.Add, ClockUs = () => Us, TraceSink = Rows.Add, PictureSource = () => picture, ViewRowSink = ViewRows.Add };
                DisplayController.CrossingHook = (toSn, band, why) => Fades.Add((toSn ? "in" : "out") + (band ? " band" : " waterline"));
            }

            public ViewOutputs Step(double vy = 0)
            {
                Tick++;
                Us += 16667;
                var f = new FrameFacts { Tick = Tick, CamSn = new Vec3(X, Y, 50), CamVelSn = new Vec3(0, vy, 0), Zone = Zone.Core, Teleported = Teleport };
                Teleport = false;
                LinkFacts l = SnFacts(Tick, Settled);
                l.C7.snFrame = Ops.SnFrameNow;
                l.Peer = Peer;
                l.PeerAlive = Peer == PeerState.Alive;
                l.Linked = Peer != PeerState.Exited;
                var g = new GameFacts { Foreground = true };
                DisplayFeedback fb = Rd.Feedback;
                ViewOutputs o = Driver.Step(ref f, ref l, ref g, ref fb, l.Linked, Dc.Mode, ViewStateAdapter.MutantBits());
                Dc.Post(ref l, Se, Driver.State, o.Request, o.ZOrderReassert, true, false, true, ref fb, o.PictureEdge, o.PicturePolicy == ViewPicture.Site);
                // 120 fps render thread, 60 Hz ticks; while A' has SE lowered, SE runs no FullDraw (DrawEnabled false)
                T.Frame(Rd, Ops, fullDraw: !FallbackAPrime.DrawDisabled);
                T.Frame(Rd, Ops, fullDraw: !FallbackAPrime.DrawDisabled);
                string s = ViewStateAdapter.Name(Driver.State);
                if (States.Count == 0 || States[States.Count - 1] != s) States.Add(s);
                return o;
            }
        }

        [TestMethod]
        public void Driver_DiveAndSurface_ThroughWiresMachine_KeepsTheCrossingOrder()
        {
            var w = new World(this, snPump: true, snFps: 60, picture: ViewPicture.Waterline);   // S2 policy, the fallback
            for (int i = 0; i < 30; i++) w.Step();                      // +20 m: Track
            Assert.AreEqual(S2State.Track, w.Driver.State);
            Assert.AreEqual(PerfMode.Track, w.Driver.Last.PerfMode);
            Assert.IsTrue(w.Rd.Feedback.ZOrderOk, "link-up z-order assert verified by the walker");
            Assert.IsFalse(w.Ops.Margins);

            // descend at 2 m/s through the waterline
            for (int i = 0; i < 1200 && w.Y > -3; i++) { w.Y -= 2.0 / 60; w.Step(-2.0); }
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join(" > ", w.States) + "\n" + string.Join("\n", _log));
            CollectionAssert.IsSubsetOf(new[] { "Track", "Prime", "Underwater" }, w.States, string.Join(" > ", w.States));
            Assert.IsTrue(w.Ops.Margins && w.Rd.Feedback.LastPresentedSkipped, "see-through: margins on, scene skipped");
            Assert.AreEqual(ViewState.Underwater, w.Driver.Last.ViewState);
            Assert.AreEqual(PerfMode.Full, w.Driver.Last.PerfMode);
            Assert.AreEqual(1f, w.Driver.Last.SnBusTarget);
            Assert.IsTrue(w.Driver.Last.CrossingTick > 0);
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.View -> Underwater") && l.Contains("crossing->entry")), string.Join("\n", _log));
            Assert.IsTrue(w.Rows.Count > 0 && w.Rows[0].Contains("\"mrd\":"), "vsm/2 rows carry the live margins state");

            // a look command (teleport flag without a move) is not a resettle event: still Underwater
            w.Teleport = true;
            for (int i = 0; i < 10; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State);

            // a > 20 m teleport while Subnautica is not settled: surfacing at once, back to Underwater once settled
            w.Settled = false;
            w.X += 500;
            w.Teleport = true;
            w.Step();
            w.Step();
            Assert.AreNotEqual(S2State.Underwater, w.Driver.State, string.Join(" > ", w.States));
            for (int i = 0; i < 20; i++) w.Step();
            Assert.AreNotEqual(S2State.Underwater, w.Driver.State, "held while not settled");
            w.Settled = true;
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join(" > ", w.States) + "\n" + string.Join("\n", _log.Skip(_log.Count - 15)));

            // ascend to +3 m: Surfacing, Prime (margins kept); below +5 m Prime stays 10 s without a Prime condition
 //, then Track: margins off after the scene frames
            for (int i = 0; i < 600 && w.Y < 3; i++) { w.Y += 2.0 / 60; w.Step(2.0); }
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State);
            Assert.IsTrue(w.Ops.Margins, "Prime keeps the margins");
            for (int i = 0; i < 240; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State, "about 5.5 s quiet at +3 m: Prime kept (10 s below +5 m)");
            Assert.IsTrue(w.Ops.Margins);
            for (int i = 0; i < 420; i++) w.Step();
            Assert.AreEqual(S2State.Track, w.Driver.State, "about 12.5 s quiet at +3 m: Track");
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.View -> Track (Prime quiet: 10 s below +5 m, 3 s above)")), string.Join("\n", _log));
            Assert.IsFalse(w.Ops.Margins);
            CollectionAssert.IsSubsetOf(new[] { "Surfacing" }, w.States);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.IsTrue(_skippedFrames > 0);
            Assert.IsFalse(_log.Any(l => l.StartsWith("DE.ZOrder FAIL") || l.StartsWith("DE.ZOrder retry")), "no false z-order failure: " +
                string.Join("\n", _log.Where(l => l.StartsWith("DE.ZOrder"))));
            Assert.IsTrue(w.Rows.Count > 0 && w.Rows[0].StartsWith("{\"ev\":\"vsm\""), "vsm trace rows written around transitions");
            // a quiet stretch (no transition, no event): one row per second, after the rows that follow the last transition
            int before = w.Rows.Count;
            for (int i = 0; i < 600; i++) w.Step();
            int added = w.Rows.Count - before;
            Assert.IsTrue(added >= 600 / ViewStateDriver.TraceEverySteps - 1 && added <= ViewStateDriver.TraceAfterTransitionSteps + 600 / ViewStateDriver.TraceEverySteps + 1,
                "rows in 600 quiet steps: " + added);
        }

        /// <summary>
 /// Re-dive fix: the band-hold diagnostic. Prime with the camera inside the waterline band
 /// (−0.20 &lt; y ≤ −0.05 m) for more than 1 s logs one <c>DE.View band hold #N</c> line, one more every 5 s while it
 /// lasts and an end line; the counter is published; the machine's behaviour is unchanged (still Prime, scene drawn).
 /// A dip shorter than 1 s logs nothing.
 /// </summary>
        [TestMethod]
        public void Driver_BandHold_LoggedOncePerHoldAndEvery5s_BehaviourUnchanged()
        {
            var w = new World(this, picture: ViewPicture.Waterline);   // S2 policy, the fallback (SE's scene drawn in Prime)
            for (int i = 0; i < 30; i++) w.Step();
            w.Y = -2;
            for (int i = 0; i < 120; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join("\n", _log));
            w.Y = 0.5;
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State, "surfaced to Prime");

            Func<string, int> count = prefix => _log.Count(l => l.StartsWith(prefix));
            w.Y = -0.125;                                        // inside the band: below the waterline, above the entry
            for (int i = 0; i < 50; i++) w.Step();
            Assert.AreEqual(0, count("DE.View band hold"), "0.83 s: not yet");
            for (int i = 0; i < 20; i++) w.Step();
            Assert.AreEqual(1, count("DE.View band hold #1 start: Prime with y in (-0.2, -0.05] m for "), string.Join("\n", _log));
            string line = _log.First(l => l.StartsWith("DE.View band hold #1 start"));
            StringAssert.Contains(line, " y=-0.125 vy=0 snReady=True blocked=y heldS=");
            Assert.AreEqual(1L, w.Driver.BandHolds);
            Assert.IsTrue(w.Driver.InBandHold);
            Assert.AreEqual(S2State.Prime, w.Driver.State, "behaviour unchanged: Prime, SE's scene drawn");
            Assert.IsFalse(w.Rd.Feedback.LastPresentedSkipped);
            for (int i = 0; i < 250; i++) w.Step();
            Assert.AreEqual(0, count("DE.View band hold #1 continues"), "no repeat before 5 s");
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(1, count("DE.View band hold #1 continues: "), "one repeat after 5 s more");
            Assert.AreEqual(1, count("DE.View band hold #1 start"), "logged once per hold");
            Assert.IsTrue(w.Driver.BandHoldMaxS > 6.0 && w.Driver.BandHoldMaxS < 6.5, "max " + w.Driver.BandHoldMaxS);

            w.Y = -0.5;                                          // below the entry threshold: Underwater ends the hold
            for (int i = 0; i < 10; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State);
            Assert.AreEqual(1, count("DE.View band hold #1 ended after "), string.Join("\n", _log));
            // the hold ends on the crossing tick (still Prime until Subnautica presents it), Underwater follows
            StringAssert.Contains(_log.First(l => l.StartsWith("DE.View band hold #1 ended")), ": y at or below -0.2 m (still Prime: blocked=crossing) tick=");
            Assert.IsFalse(w.Driver.InBandHold);

            // a dip into the band shorter than 1 s: nothing logged, nothing counted
            w.Y = 0.5;
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State);
            w.Y = -0.1;
            for (int i = 0; i < 55; i++) w.Step();
            w.Y = 0.5;
            for (int i = 0; i < 10; i++) w.Step();
            Assert.AreEqual(1L, w.Driver.BandHolds);
            Assert.AreEqual(0, count("DE.View band hold #2"));
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        /// <summary>
 /// Review fix (findings 11, 15 and the Surfacing hold): a peer exit underwater goes Surfacing → Space
 /// although the render thread can no longer apply the margins Surfacing asks for, and the margins drop after the
 /// scene frames; the margins forced off in Prime hold the Underwater entry (the acknowledgement is sticky, the
 /// live margins state is not).
 /// </summary>
        [TestMethod]
        public void Driver_PeerExitUnderwater_ReachesSpace_AndMarginsOffHoldUnderwater()
        {
            var w = new World(this, picture: ViewPicture.Waterline);   // S2 policy, the fallback
            for (int i = 0; i < 30; i++) w.Step();
            w.Y = -2;
            for (int i = 0; i < 120; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join("\n", _log));
            w.Peer = PeerState.Exited;
            int steps = 0;
            while (w.Driver.State != S2State.Space && steps < 30) { w.Step(); steps++; }
            Assert.AreEqual(S2State.Space, w.Driver.State, "no Surfacing hold: " + string.Join(" > ", w.States));
            Assert.IsTrue(steps <= 3, "Space after " + steps + " ticks");
            for (int i = 0; i < 3; i++) w.Step();
            Assert.IsFalse(w.Ops.Margins);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));

            // margins forced off after Prime was acknowledged: Underwater held below the waterline (S2 policy, the fallback;
 // under Site the latch, once on, does not drop on a margins loss: see Driver_Site_…)
            var w2 = new World(this, picture: ViewPicture.Waterline);
            for (int i = 0; i < 30; i++) w2.Step();
            w2.Y = 0.5;
            for (int i = 0; i < 30; i++) w2.Step();
            Assert.AreEqual(S2State.Prime, w2.Driver.State);
            Assert.IsTrue(w2.Ops.Margins);
            w2.Dc.ForcedMargins = false;
            for (int i = 0; i < 5; i++) w2.Step();
            Assert.IsFalse(w2.Ops.Margins);
            w2.Y = -1;
            for (int i = 0; i < 60; i++) w2.Step();
            Assert.AreEqual(S2State.Prime, w2.Driver.State, "no Underwater while the margins are off (finding 11)");
            Assert.AreNotEqual(0u, w2.Driver.Last.Blocked & ViewBlock.Render);
            Assert.AreEqual(0f, w2.Driver.Last.SnBusTarget, "C5 and the audio stay above water");
            w2.Dc.ForcedMargins = null;
            for (int i = 0; i < 30; i++) w2.Step();
            Assert.AreEqual(S2State.Underwater, w2.Driver.State, string.Join("\n", _log.Skip(_log.Count - 10)));
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        [TestMethod]
        public void Driver_S2M7_NoMarginsInPrime_HoldsUnderwaterEntry_AndToggle()
        {
            Mutants.Set("S2.M7", true);
            var w = new World(this, picture: ViewPicture.Waterline);   // S2 policy, the fallback (S2.M7 is a Prime mutant)
            for (int i = 0; i < 30; i++) w.Step();
            w.Y = 0.5;
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State);
            Assert.IsFalse(w.Ops.Margins, "S2.M7: Subnautica fully covered during Prime");
            w.Y = -1;
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State, "the render ack never comes: Underwater held");
            Mutants.ClearAll();
            for (int i = 0; i < 10; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State);

            // the T-D1 toggle on top of the machine: forced see-through and back (S2 policy: Track draws SE's scene)
            var w2 = new World(this, picture: ViewPicture.Waterline);
            for (int i = 0; i < 30; i++) w2.Step();
            double ms = w2.Dc.ToggleSeeThrough("test");
            Assert.IsTrue(ms < 1.0, "update-thread cost per toggle < 1 ms (S2.T-D1)");
            for (int i = 0; i < 5; i++) w2.Step();
            Assert.IsTrue(w2.Ops.Margins && w2.Rd.Feedback.LastPresentedSkipped, "toggle: see-through in Track");
            w2.Dc.ToggleSeeThrough("test");
            for (int i = 0; i < 3; i++) w2.Step();
            Assert.IsFalse(w2.Ops.Margins);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.Display toggle #1 see-through ON") && l.Contains("foregroundIsSe=True")));
        }

        /// <summary>
 /// The A′ dive through the whole chain (driver, controller, render executor, a z-order model with Subnautica's
 /// asynchronous message pump): SE stays lowered directly under Subnautica for the whole Underwater stretch,
 /// although every re-assert source fires.
 /// </summary>
        [TestMethod]
        public void Driver_APrime_DiveLowersSe_StaysLowered_NoMargins()
        {
            var w = new World(this, DisplayDesign.APrime, snPump: true, snFps: 30, picture: ViewPicture.Waterline);   // S2 policy, the fallback
            for (int i = 0; i < 30; i++) w.Step();
            w.Y = -2;
            for (int i = 0; i < 120; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join("\n", _log));
            Assert.IsTrue(w.Rd.Feedback.Lowered && FallbackAPrime.DrawDisabled);
            Assert.AreEqual(0, w.Ops.Calls.Count(c => c.StartsWith("ExtendFrame")));
            for (int i = 0; i < 240; i++)
            {
                w.Step();
                Assert.IsTrue(w.Ops.SeUnderSn, "step " + i + ": SE directly under Subnautica while lowered\n" + string.Join("\n", _log.Where(l => l.StartsWith("DE.ZOrder"))));
            }
            Assert.IsTrue(w.Rd.Feedback.ZOrderOk, "the inverted order verified");
            Assert.AreEqual(S2State.Underwater, w.Driver.State);
            w.Y = 3;
            for (int i = 0; i < 60; i++) w.Step();
            Assert.IsFalse(w.Rd.Feedback.Lowered);
            Assert.IsFalse(FallbackAPrime.DrawDisabled);
            Assert.IsTrue(w.Ops.IndexOf(Se) < w.Ops.IndexOf(Sn), "raised above Subnautica again");
        }

        // ============================================================================== S7a: the Site picture policy

 /// <summary>
 /// S7a Site through the whole chain (driver over Wire's machine, controller, render executor, a z-order model with
 /// Subnautica's message pump): Subnautica's picture from the band at +300 m (one fade, kind band), none at the
 /// waterline, back to SE's at +350 m (one fade), margins on from the Approach entry and off only after SE's scene
 /// frames, never a cleared frame without margins; the dev see-through toggle clears itself on the picture's off
 /// edge; s7a.view rows on every change.
 /// </summary>
        [TestMethod]
        public void Driver_Site_PictureAtTheBand_NoSwapAtTheWaterline_NoBlackFrame()
        {
            var w = new World(this, snPump: true, snFps: 60) { Y = 400 };
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Space, w.Driver.State);
            Assert.AreEqual(PerfMode.Idle, w.Driver.Last.PerfMode);
            Assert.IsFalse(w.Ops.Margins);
            for (int i = 0; i < 900 && w.Y > 250; i++) { w.Y -= 20.0 / 60; w.Step(-20.0); }
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Track, w.Driver.State, string.Join(" > ", w.States));
            Assert.IsTrue(w.Driver.Last.Picture, "the latch on in Track");
            Assert.AreEqual(PerfMode.Full, w.Driver.Last.PerfMode, "Subnautica Full in Track");
            Assert.IsTrue(w.Ops.Margins && w.Rd.Feedback.LastPresentedSkipped, "Subnautica's picture: margins on, SE's scene skipped");
            CollectionAssert.AreEqual(new[] { "in band" }, w.Fades, "one fade at the band");
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.View picture ON")), string.Join("\n", _log));
            Assert.AreNotEqual(0u, w.Driver.Last.C5Flags & S7aConventions.C5FlagsExt.PictureSite);

            // the dive and back to +3 m: the states change, the picture does not
            for (int i = 0; i < 2000 && w.Y > -3; i++) { w.Y -= 10.0 / 60; w.Step(-10.0); }
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State, string.Join(" > ", w.States));
            for (int i = 0; i < 600 && w.Y < 3; i++) { w.Y += 2.0 / 60; w.Step(2.0); }
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Prime, w.Driver.State);
            CollectionAssert.IsSubsetOf(new[] { "Track", "Prime", "Underwater", "Surfacing" }, w.States, string.Join(" > ", w.States));
            CollectionAssert.AreEqual(new[] { "in band" }, w.Fades, "no fade at the waterline");
            Assert.IsTrue(w.Rd.Feedback.LastPresentedSkipped, "still Subnautica's picture");

            // the dev toggle on in Prime, then up through the band: the picture's off edge fades and clears the toggle
            w.Dc.ToggleSeeThrough("test");
            for (int i = 0; i < 1200 && w.Y < 400; i++) { w.Y += 20.0 / 60; w.Step(20.0); }
            for (int i = 0; i < 30; i++) w.Step();
            Assert.AreEqual(S2State.Space, w.Driver.State);
            CollectionAssert.AreEqual(new[] { "in band", "out band" }, w.Fades);
            Assert.IsFalse(w.Dc.ForcedSeeThrough, "the dev toggle cleared itself on the picture's off edge");
            Assert.IsFalse(w.Ops.Margins, "margins off after SE's scene frames");
            Assert.IsFalse(w.Rd.Feedback.LastPresentedSkipped);
            Assert.AreEqual(2L, w.Dc.PictureEdges);
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.IsTrue(w.ViewRows.Count >= 4 && w.ViewRows.All(r => r.StartsWith("{\"t\":") && r.Contains("\"ev\":\"s7a.view\"")), "s7a.view rows");
            Assert.IsTrue(w.ViewRows.Any(r => r.Contains("\"edge\":1") && r.Contains("\"picture\":true") && r.Contains("\"policy\":\"site\"")));
            Assert.IsTrue(w.ViewRows.Any(r => r.Contains("\"edge\":-1")));
        }

        /// <summary>S7a.M1 through the chain: Site selected, the machine runs Waterline (SE's picture in Track, the swap at the waterline).</summary>
        [TestMethod]
        public void Driver_S7aM1_RunsWaterline_SeesSePictureInTrack()
        {
            StringAssert.StartsWith(Mutants.Set("S7a.M1", true), "S7a.M1 ON");
            var w = new World(this) { Y = 100 };
            for (int i = 0; i < 60; i++) w.Step();
            Assert.AreEqual(S2State.Track, w.Driver.State);
            Assert.IsFalse(w.Driver.Last.Picture);
            Assert.AreEqual(PerfMode.Track, w.Driver.Last.PerfMode);
            Assert.AreEqual(ViewPicture.Waterline, w.Driver.Last.PicturePolicy);
            Assert.IsFalse(w.Ops.Margins);
            w.Y = -2;
            for (int i = 0; i < 120; i++) w.Step();
            Assert.AreEqual(S2State.Underwater, w.Driver.State);
            CollectionAssert.AreEqual(new[] { "in waterline" }, w.Fades, "the S2 fade at the waterline");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        /// <summary>
 /// S7a Site with design A′: SE lowered below Subnautica (and not drawing) from the latch in Track, raised after a
 /// drawn frame when the picture goes at the band exit; never DWM margins.
 /// </summary>
        [TestMethod]
        public void Driver_Site_APrime_LoweredFromTheBand_RaisedAfterADrawnFrame()
        {
            var w = new World(this, DisplayDesign.APrime, snPump: true, snFps: 30) { Y = 100 };
            for (int i = 0; i < 120; i++) w.Step();
            Assert.AreEqual(S2State.Track, w.Driver.State);
            Assert.IsTrue(w.Driver.Last.Picture);
            Assert.IsTrue(w.Rd.Feedback.Lowered && FallbackAPrime.DrawDisabled, "A' Track under the latch: lowered, not drawing");
            Assert.IsTrue(w.Ops.SeUnderSn);
            w.Y = 400;
            for (int i = 0; i < 120; i++) w.Step();   // F15: the altitude exit holds 1 s, then the drawn frame
            Assert.AreEqual(S2State.Space, w.Driver.State);
            Assert.IsFalse(w.Rd.Feedback.Lowered);
            Assert.IsFalse(FallbackAPrime.DrawDisabled);
            Assert.IsTrue(w.Ops.IndexOf(Se) < w.Ops.IndexOf(Sn), "raised above Subnautica again");
            Assert.AreEqual(0, w.Ops.Calls.Count(c => c.StartsWith("ExtendFrame")), "A' never uses DWM margins");
            CollectionAssert.AreEqual(new[] { "in band", "out band" }, w.Fades);
        }
    }
}
