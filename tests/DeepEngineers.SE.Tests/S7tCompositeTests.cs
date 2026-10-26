using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>A GPU that records the crossfade passes (alpha per frame) besides S2's fill and readback.</summary>
    internal sealed class FadeGpu : IRenderGpu, ICrossfadeGpu
    {
        public int Fills;
        public readonly List<double> Alphas = new List<double>();
        public readonly List<bool> Premultiplied = new List<bool>();
        public bool FailFade;
        public bool Fill(out string error) { error = null; Fills++; return true; }
        public bool BeginReadback(out string error) { error = null; return true; }
        public bool ReadbackPending { get { return false; } }
        public bool TryFinishReadback(out AlphaStats stats, out string error) { stats = default(AlphaStats); error = null; return false; }
        public bool Fade(double alpha, bool premultiply, out string error)
        {
            error = FailFade ? "test failure" : null;
            if (FailFade) return false;
            Alphas.Add(alpha);
            Premultiplied.Add(premultiply);
            return true;
        }
    }

    /// <summary>FakeWindowOps plus SE's own window's constant alpha (the A′ ramp).</summary>
    internal sealed class LayeredOps : IWindowOps, ILayeredWindowOps
    {
        public readonly FakeWindowOps Inner;
        public readonly List<int> Alphas = new List<int>();
        public bool Layered;
        public int Clears;
        public LayeredOps(FakeWindowOps inner) { Inner = inner; }
        public bool SetLayeredAlpha(IntPtr h, byte alpha) { Layered = true; Alphas.Add(alpha); return true; }
        public bool ClearLayered(IntPtr h) { Layered = false; Clears++; return true; }
        public bool IsWindow(IntPtr h) { return Inner.IsWindow(h); }
        public bool IsWindowVisible(IntPtr h) { return Inner.IsWindowVisible(h); }
        public int ExtendFrame(IntPtr h, bool sheetOfGlass) { return Inner.ExtendFrame(h, sheetOfGlass); }
        public bool SetPos(IntPtr h, IntPtr insertAfter, uint flags) { return Inner.SetPos(h, insertAfter, flags); }
        public bool AllowSetForeground(uint pid, out int error) { return Inner.AllowSetForeground(pid, out error); }
        public bool SetForeground(IntPtr h) { return Inner.SetForeground(h); }
        public IntPtr Foreground() { return Inner.Foreground(); }
        public bool IsTopmost(IntPtr h) { return Inner.IsTopmost(h); }
        public bool SetTopmost(IntPtr h, bool topmost) { return Inner.SetTopmost(h, topmost); }
        public WalkResult Walk(IntPtr se, IntPtr sn) { return Inner.Walk(se, sn); }
        public bool GetRect(IntPtr h, out I32x4 rect) { return Inner.GetRect(h, out rect); }
        public uint[] ReadScreenPixels(int[] xs, int[] ys) { return Inner.ReadScreenPixels(xs, ys); }
        public long Qpc() { return Inner.Qpc(); }
    }

    /// <summary>
 /// S7t se-composite (T1 the crossfade, T3's band): the pure rules (<see cref="CrossfadeRules"/>), the render thread's
 /// ramp over a fake Windows and GPU (F: the skip held until the ramp reaches Subnautica, the pass before the sprites on
 /// drawn frames only, no fill on a faded frame, the margins held while the ramp still shows Subnautica, a snap; A′: the
 /// window alpha ramp around the lowering), the update thread's decision through Wire's machine (the fade in at the
 /// latch, the fade back at the leave dwell, no snap on a normal climb, s7t.fade rows), and the crossfade pass itself
 /// on a WARP device (premultiplied colour on an sRGB target, alpha, the caller's state restored).
 /// </summary>
    [TestClass]
    public unsafe class S7tCompositeTests
    {
        private static readonly IntPtr Se = new IntPtr(0x1000), Sn = new IntPtr(0x2000);
        private readonly List<string> _log = new List<string>();
        private readonly List<string> _violations = new List<string>();
        private double _savedMs;
        private bool _savedAprime, _savedStraight, _savedFlash;

        [TestInitialize]
        public void Init()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));   // the tests pin their own band (the live default is +1,000 / +1,100 m)
            _savedMs = S7tSettings.FadeMs;
            _savedAprime = S7tSettings.APrimeRamp;
            _savedStraight = S7tSettings.Straight;
            _savedFlash = S7tSettings.FlashWithFade;
            S7tSettings.FadeMs = 700.0;
            S7tSettings.APrimeRamp = false;
            S7tSettings.Straight = false;
            S7tSettings.FlashWithFade = false;
        }

        [TestCleanup]
        public void Cleanup()
        {
            S7tSettings.FadeMs = _savedMs;
            S7tSettings.APrimeRamp = _savedAprime;
            S7tSettings.Straight = _savedStraight;
            S7tSettings.FlashWithFade = _savedFlash;
            Mutants.ClearAll();
            FallbackAPrime.DrawDisabled = false;
            DisplayController.CrossingHook = null;
        }

        // =================================================================================================== rules

        [TestMethod]
        public void Rules_ParseEaseStepAlphaAndTheColourFactor()
        {
            double ms;
            Assert.IsTrue(CrossfadeRules.TryParseMs(null, out ms) && ms == 700.0, "default 700 ms");
            Assert.IsTrue(CrossfadeRules.TryParseMs(" 450 ", out ms) && ms == 450.0);
            Assert.IsTrue(CrossfadeRules.TryParseMs("900ms", out ms) && ms == 900.0);
            Assert.IsTrue(CrossfadeRules.TryParseMs("off", out ms) && ms == 0.0);
            Assert.IsTrue(CrossfadeRules.TryParseMs("0", out ms) && ms == 0.0);
            Assert.IsFalse(CrossfadeRules.TryParseMs("10", out ms), "below 50 ms");
            Assert.IsFalse(CrossfadeRules.TryParseMs("9000", out ms), "above 5 s");
            Assert.IsFalse(CrossfadeRules.TryParseMs("soon", out ms));

            Assert.AreEqual(0.0, CrossfadeRules.Ease(0.0));
            Assert.AreEqual(1.0, CrossfadeRules.Ease(1.0));
            Assert.AreEqual(0.5, CrossfadeRules.Ease(0.5), 1e-12, "symmetric");
            Assert.AreEqual(0.0, CrossfadeRules.Ease(-1.0));
            Assert.AreEqual(1.0, CrossfadeRules.Ease(2.0));
            for (int i = 1; i <= 100; i++) Assert.IsTrue(CrossfadeRules.Ease(i / 100.0) > CrossfadeRules.Ease((i - 1) / 100.0), "monotonic");
            Assert.IsTrue(CrossfadeRules.Ease(0.01) < 0.001, "flat at the start");

            Assert.AreEqual(0.5, CrossfadeRules.Step(0.0, true, 350.0, 700.0), 1e-12, "linear at 1/ms");
            Assert.AreEqual(1.0, CrossfadeRules.Step(0.9, true, 350.0, 700.0), "clamped");
            Assert.AreEqual(0.4, CrossfadeRules.Step(0.5, false, 70.0, 700.0), 1e-12, "a reversed fade turns back from where it is");
            Assert.AreEqual(1.0, CrossfadeRules.Step(0.2, true, 5.0, 0.0), "no duration: jump");
            Assert.AreEqual(0.2, CrossfadeRules.Step(0.2, true, -5.0, 700.0), "a negative step does nothing");
            Assert.AreEqual(1.0, CrossfadeRules.Step(double.NaN, true, 1.0, 700.0), "non-finite progress: the target");

            Assert.AreEqual(1.0, CrossfadeRules.Alpha(0.0));
            Assert.AreEqual(0.0, CrossfadeRules.Alpha(1.0));
            Assert.AreEqual(0.5, CrossfadeRules.Alpha(0.5), 1e-12);
            Assert.AreEqual(Math.Pow(0.5, 2.2), CrossfadeRules.ColourFactor(0.5, true, true), 1e-6, "sRGB target: the linear blend scales by alpha^2.2");
            Assert.AreEqual(0.5f, CrossfadeRules.ColourFactor(0.5, false, true), "linear target: alpha");
            Assert.AreEqual(1f, CrossfadeRules.ColourFactor(0.5, true, false), "straight: the colour left alone");
            Assert.AreEqual(0f, CrossfadeRules.ColourFactor(-1.0, true, true));
            Assert.AreEqual(350.0, CrossfadeRules.RemainingMs(0.5, true, 700.0), 1e-9);

            Assert.IsTrue(CrossfadeRules.SkipAllowed(false, 0.3), "no ramp: the S7a skip");
            Assert.IsFalse(CrossfadeRules.SkipAllowed(true, 0.999));
            Assert.IsTrue(CrossfadeRules.SkipAllowed(true, 1.0));
            Assert.IsTrue(CrossfadeRules.PassWanted(true, 0.2, true, true));
            Assert.IsFalse(CrossfadeRules.PassWanted(true, 0.0, true, true), "SE's picture: no pass (the fill)");
            Assert.IsFalse(CrossfadeRules.PassWanted(true, 0.5, false, true), "a skipped frame: nothing to fade");
            Assert.IsFalse(CrossfadeRules.PassWanted(true, 0.5, true, false), "no margins: DWM ignores the alpha");
            Assert.IsTrue(CrossfadeRules.HoldMargins(true, 0.01));
            Assert.IsFalse(CrossfadeRules.HoldMargins(true, 0.0));
            Assert.IsFalse(CrossfadeRules.HoldMargins(false, 0.5));
        }

        private static CrossfadeRules.Inputs In(bool latch, bool leave = false, bool site = true, bool f = true, bool forced = false, double ms = 700.0)
        {
            return new CrossfadeRules.Inputs
            {
                Enabled = ms > 0, Ms = ms, PictureSite = site, InWorld = true, DesignF = f, DesignAPrime = !f, Latch = latch, LeaveArmed = leave, Forced = forced,
            };
        }

        [TestMethod]
        public void Rules_Decide_FadeInAtTheLatch_BackAtTheLeaveDwell_SnapOnALoss()
        {
            var m = new CrossfadeRules.Memory();
            CrossfadeRules.Inputs i = In(false);
            CrossfadeRules.Decision d = CrossfadeRules.Decide(ref i, ref m, false, false);
            Assert.IsTrue(d.On && !d.ToSn && !d.Started, "Space/Track before the latch: SE's picture, the ramp armed");

            i = In(true);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsTrue(d.Started && d.ToSn && d.Skip && !d.Snap, "the latch: fade to Subnautica, the request keeps the skip (the render thread holds it)");
            Assert.AreEqual(CrossfadeRules.Reason.Latch, d.Why);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsFalse(d.Started, "one start per change");

            i = In(true, leave: true);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsTrue(d.Started && !d.ToSn && !d.Skip && !d.Snap, "the leave dwell: fade back, the scene drawn, the latch still on");
            Assert.AreEqual(CrossfadeRules.Reason.LeaveDwell, d.Why);

            i = In(true);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsTrue(d.Started && d.ToSn, "the dwell cancelled (descending again): back to Subnautica");
            Assert.AreEqual(CrossfadeRules.Reason.Back, d.Why);

            int seq = d.SnapSeq;
            i = In(false);
            d = CrossfadeRules.Decide(ref i, ref m, false, false);
            Assert.IsTrue(d.Started && !d.ToSn && d.Snap, "the latch dropped without the dwell (peer, site, re-streaming): SE at once");
            Assert.AreEqual(CrossfadeRules.Reason.Loss, d.Why);
            Assert.AreEqual(seq + 1, d.SnapSeq);

            // the normal climb: dwell first (fade back), then the latch drops in Space: no snap
            i = In(true); CrossfadeRules.Decide(ref i, ref m, true, false);
            i = In(true, leave: true); CrossfadeRules.Decide(ref i, ref m, true, false);
            i = In(false, leave: true);
            d = CrossfadeRules.Decide(ref i, ref m, false, false);
            Assert.IsFalse(d.Started || d.Snap, "the latch off edge after the fade back changes nothing");
        }

        [TestMethod]
        public void Rules_Decide_OffUnderWaterlineForcedOrWithoutTheRamp_TheS7aRequestUnchanged()
        {
            var m = new CrossfadeRules.Memory();
            CrossfadeRules.Inputs i = In(true, site: false);
            CrossfadeRules.Decision d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsFalse(d.On, "Waterline: S2's cut and flash");
            Assert.IsTrue(d.Skip && d.ToSn);
            i = In(true, forced: true);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsFalse(d.On, "the dev see-through toggle is instant");
            i = In(true, ms: 0.0);
            d = CrossfadeRules.Decide(ref i, ref m, true, false);
            Assert.IsFalse(d.On, "de.s7t fade off");
            i = In(true, f: false);
            d = CrossfadeRules.Decide(ref i, ref m, false, true);
            Assert.IsFalse(d.On, "A′ without its ramp switch: S7a's lowering");
            Assert.IsTrue(d.Lowered);
            i = In(true, f: false);
            i.APrimeRamp = true;
            d = CrossfadeRules.Decide(ref i, ref m, false, true);
            Assert.IsTrue(d.On && d.Lowered, "A′ with its ramp");
            i.MenuOpen = true;
            d = CrossfadeRules.Decide(ref i, ref m, false, false);
            Assert.IsFalse(d.On, "A′ menu rule: raised at once");
        }

        // ========================================================================================== render thread

        private static FakeWindowOps Ops() { return new FakeWindowOps { Se = Se, Sn = Sn, Fg = Se }; }

        private static DisplayRequest Req(bool margins, bool skip, bool fadeOn, bool toSn, int snap = 0, DisplayDesign mode = DisplayDesign.F, bool lowered = false)
        {
            return new DisplayRequest
            {
                Linked = true, Se = Se, Sn = Sn, SnPid = 4242, Mode = mode, Margins = margins, Skip = skip, AlphaFill = true, Lowered = lowered,
                FadeOn = fadeOn, FadeToSn = toSn, FadeMs = 700.0, FadeSnapSeq = snap,
            };
        }

        /// <summary>One render frame at 120 Hz: FullDraw (scene decision, the sprites prefix, the postfix), then Present.</summary>
        private void Frame(RenderDisplay rd, FakeWindowOps ops, bool fullDraw = true)
        {
            ops.Now += Stopwatch.Frequency / 120;
            ops.Pump();
            bool skipped = false;
            if (fullDraw)
            {
                rd.OnFullDrawStart();
                skipped = rd.DecideSkip(true);
                rd.OnBeforeSprites();
                rd.OnBeforeSprites(); // a screenshot's second sprite call in the same FullDraw changes nothing
                rd.OnFullDrawEnd(true);
            }
            if (skipped && !ops.Margins) _violations.Add("a cleared backbuffer presented without margins");
            rd.OnPresented();
            rd.DrainLog(_log.Add);
        }

        [TestMethod]
        public void Render_F_SkipHeldUntilTheRampEnds_PassBeforeTheSprites_NoFillOnAFadedFrame()
        {
            FakeWindowOps ops = Ops();
            var gpu = new FadeGpu();
            var rd = new RenderDisplay(ops) { Gpu = gpu };
            DisplayRequest r = Req(margins: true, skip: false, fadeOn: true, toSn: false);
            rd.SetRequest(ref r);
            for (int i = 0; i < 5; i++) Frame(rd, ops);
            Assert.IsTrue(ops.Margins);
            int fillsBefore = gpu.Fills;
            Assert.IsTrue(fillsBefore > 0, "SE's picture under margins: the S2 fill");
            Assert.AreEqual(0, gpu.Alphas.Count, "p = 0: no crossfade pass");

            // the latch: skip requested, the ramp to Subnautica
            r = Req(margins: true, skip: true, fadeOn: true, toSn: true);
            rd.SetRequest(ref r);
            int frames = 0;
            while (!rd.Feedback.LastPresentedSkipped && frames < 400) { Frame(rd, ops); frames++; }
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped, "the skip engages once the ramp ends");
            Assert.IsTrue(frames >= 82 && frames <= 88, "700 ms at 120 Hz ≈ 84 frames, was " + frames);
            Assert.AreEqual(fillsBefore, gpu.Fills, "no alpha fill on a faded frame");
            Assert.IsTrue(gpu.Alphas.Count >= 80, "one pass per drawn frame of the ramp");
            for (int i = 1; i < gpu.Alphas.Count; i++) Assert.IsTrue(gpu.Alphas[i] <= gpu.Alphas[i - 1], "alpha falls monotonically");
            Assert.IsTrue(gpu.Alphas.First() > 0.99 && gpu.Alphas.Last() < 0.01, "from SE's picture to Subnautica's");
            Assert.IsTrue(gpu.Premultiplied.All(x => x), "premultiplied by default");
            Assert.IsTrue(rd.Feedback.FadeHeldSkips >= 80);
            Assert.AreEqual(1.0, rd.Feedback.FadeP);

            // the fade back (the leave dwell): the scene drawn at once, alpha rising; the margins held after the request drops them
            gpu.Alphas.Clear();
            r = Req(margins: true, skip: false, fadeOn: true, toSn: false);
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.LastPresentedSceneDrawn, "the skip disengages when the fade back starts");
            r = Req(margins: false, skip: false, fadeOn: true, toSn: false);   // Space: the machine drops the margins
            rd.SetRequest(ref r);
            for (int i = 0; i < 40; i++) Frame(rd, ops);
            Assert.IsTrue(ops.Margins, "held: the ramp still shows Subnautica");
            Assert.IsTrue(rd.Feedback.FadeMarginHolds > 0);
            for (int i = 0; i < 80; i++) Frame(rd, ops);
            Assert.AreEqual(0.0, rd.Feedback.FadeP);
            Assert.IsFalse(ops.Margins, "off after SE's scene frames once the ramp rests");
            for (int i = 1; i < gpu.Alphas.Count; i++) Assert.IsTrue(gpu.Alphas[i] >= gpu.Alphas[i - 1], "alpha rises monotonically");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        [TestMethod]
        public void Render_F_SnapAndRampOff_AreInstant_AndAFailedPassKeepsSeOpaque()
        {
            FakeWindowOps ops = Ops();
            var gpu = new FadeGpu();
            var rd = new RenderDisplay(ops) { Gpu = gpu };
            DisplayRequest r = Req(margins: true, skip: true, fadeOn: true, toSn: true);
            rd.SetRequest(ref r);
            for (int i = 0; i < 120; i++) Frame(rd, ops);
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped);
            // a loss: snap to SE (no ramp back)
            gpu.Alphas.Clear();
            r = Req(margins: true, skip: false, fadeOn: true, toSn: false, snap: 1);
            rd.SetRequest(ref r);
            Frame(rd, ops);
            Assert.AreEqual(0.0, rd.Feedback.FadeP, "snapped");
            Assert.AreEqual(0, gpu.Alphas.Count, "SE's picture at once: no pass");
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.Display crossfade snap to SE")), string.Join("\n", _log));

            // without the ramp (S7a): the skip engages on the first margins frame, no pass
            FakeWindowOps ops2 = Ops();
            var gpu2 = new FadeGpu();
            var rd2 = new RenderDisplay(ops2) { Gpu = gpu2 };
            DisplayRequest r2 = Req(margins: true, skip: true, fadeOn: false, toSn: true);
            rd2.SetRequest(ref r2);
            for (int i = 0; i < 4; i++) Frame(rd2, ops2);
            Assert.IsTrue(rd2.Feedback.LastPresentedSkipped, "S7a's instant swap");
            Assert.AreEqual(0, gpu2.Alphas.Count);

            // a failing pass: counted, logged once, the S2 fill keeps SE's frame opaque
            FakeWindowOps ops3 = Ops();
            var gpu3 = new FadeGpu { FailFade = true };
            var rd3 = new RenderDisplay(ops3) { Gpu = gpu3 };
            DisplayRequest r3 = Req(margins: true, skip: true, fadeOn: true, toSn: true);
            rd3.SetRequest(ref r3);
            for (int i = 0; i < 20; i++) Frame(rd3, ops3);
            Assert.IsTrue(rd3.Feedback.FadeErrors > 0);
            Assert.IsTrue(gpu3.Fills > 0, "the fill still runs on an unfaded frame");
            Assert.AreEqual(1, _log.Count(l => l.StartsWith("DE.Display crossfade pass FAILED")), "logged once per distinct error");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
        }

        [TestMethod]
        public void Render_F_SpritesHookNeverRuns_RampStopsHoldingTheSkip()
        {
            FakeWindowOps ops = Ops();
            var rd = new RenderDisplay(ops) { Gpu = new FadeGpu() };
            DisplayRequest r = Req(margins: true, skip: true, fadeOn: true, toSn: true);
            rd.SetRequest(ref r);
            int frames = 0;
            while (!rd.Feedback.LastPresentedSkipped && frames < 100)
            {
                // FullDraw without the ConsumeMainSprites prefix (the patch missing or inlined away)
                ops.Now += Stopwatch.Frequency / 120;
                ops.Pump();
                rd.OnFullDrawStart();
                rd.DecideSkip(true);
                rd.OnFullDrawEnd(true);
                rd.OnPresented();
                rd.DrainLog(_log.Add);
                frames++;
            }
            Assert.IsTrue(rd.Feedback.LastPresentedSkipped, "S7a's instant swap after the hook is declared dead");
            Assert.IsTrue(frames <= 6, "3 drawn frames, then the skip (was " + frames + ")");
            Assert.IsTrue(rd.Feedback.FadeHookDead);
            Assert.IsTrue(_log.Any(l => l.StartsWith("DE.Display crossfade OFF: the ConsumeMainSprites prefix never ran")), string.Join("\n", _log));
        }

        [TestMethod]
        public void Render_APrime_WindowAlphaRamp_AroundTheLowering()
        {
            FakeWindowOps inner = Ops();
            var ops = new LayeredOps(inner);
            var rd = new RenderDisplay(ops) { Gpu = new FadeGpu() };
            DisplayRequest r = Req(margins: false, skip: true, fadeOn: true, toSn: true, mode: DisplayDesign.APrime, lowered: true);
            rd.SetRequest(ref r);
            Frame(rd, inner);
            Assert.IsFalse(rd.Feedback.Lowered, "SE stays on top while its window fades");
            Assert.IsTrue(ops.Layered);
            int frames = 1;
            while (!rd.Feedback.Lowered && frames < 400) { Frame(rd, inner, !FallbackAPrime.DrawDisabled); frames++; }
            Assert.IsTrue(rd.Feedback.Lowered, "lowered at the end of the ramp");
            Assert.IsTrue(frames >= 80 && frames <= 90, "≈ 700 ms, was " + frames);
            Assert.IsFalse(ops.Layered, "the layered style removed below Subnautica");
            for (int i = 1; i < ops.Alphas.Count; i++) Assert.IsTrue(ops.Alphas[i] <= ops.Alphas[i - 1], "window alpha falls");
            Assert.IsTrue(inner.SeUnderSn);

            // back: drawn frames first (S2), raised at alpha 0, then faded in, then opaque (style removed)
            ops.Alphas.Clear();
            r = Req(margins: false, skip: false, fadeOn: true, toSn: false, mode: DisplayDesign.APrime, lowered: false);
            rd.SetRequest(ref r);
            for (int i = 0; i < 200; i++) Frame(rd, inner, !FallbackAPrime.DrawDisabled);
            Assert.IsFalse(rd.Feedback.Lowered);
            Assert.IsTrue(inner.IndexOf(Se) < inner.IndexOf(Sn), "raised");
            Assert.IsTrue(ops.Alphas.Count > 10 && ops.Alphas[0] == 0, "raised at alpha 0");
            for (int i = 1; i < ops.Alphas.Count; i++) Assert.IsTrue(ops.Alphas[i] >= ops.Alphas[i - 1], "window alpha rises");
            Assert.IsFalse(ops.Layered, "opaque again at rest");
            Assert.AreEqual(0.0, rd.Feedback.FadeP);
        }

        // ============================================================================ update thread + Wire's machine

        private static LinkFacts SnFacts(ulong tick)
        {
            var c7 = new SnStatus
            {
                snPhase = SnPhase.Active,
                flags = SnStatusFlags.AreaBuilt | SnStatusFlags.RectOk | SnStatusFlags.UiHidden | SnStatusFlags.StreamingSettled,
                presentedSeTick = tick - 1,
                areaBuiltSeTick = tick - 1,
                helloAckEpoch = 3,
                snHwnd = (ulong)Sn.ToInt64(),
            };
            return new LinkFacts
            {
                Linked = true, PeerAlive = true, Peer = PeerState.Alive, SnPhase = SnPhase.Active, HaveC7 = true, C7 = c7,
                HelloEpoch = 3, SnHwnd = Sn, SnPid = 4242,
            };
        }

        /// <summary>The S2Host chain of one SE tick: step, the machine's facts (the leave dwell, the tick), post, two render frames.</summary>
        private sealed class World
        {
            public readonly FakeWindowOps Ops;
            public readonly RenderDisplay Rd;
            public readonly DisplayController Dc;
            public readonly ViewStateDriver Driver;
            public readonly FadeGpu Gpu = new FadeGpu();
            public readonly List<string> Rows = new List<string>();
            public readonly List<string> ViewRows = new List<string>();
            public readonly List<string> Hooks = new List<string>();
            public ulong Tick = 5000;
            public long Us = 1000000;
            public double Y = 400;
            private readonly S7tCompositeTests _t;

            public World(S7tCompositeTests t)
            {
                _t = t;
                Ops = Ops2();
                Ops.ZGood = false;
                Ops.SnPump = true;
                Rd = new RenderDisplay(Ops) { Gpu = Gpu };
                Dc = new DisplayController(Rd, S2Config.Parse(null, null, null)) { Log = t._log.Add, ForegroundWindow = () => Se, RowSink = Rows.Add };
                Driver = new ViewStateDriver { Log = t._log.Add, ClockUs = () => Us, PictureSource = () => ViewPicture.Site, ViewRowSink = ViewRows.Add };
                DisplayController.CrossingHook = (toSn, band, why) => Hooks.Add((toSn ? "in" : "out") + (band ? " band" : " waterline"));
            }

            private static FakeWindowOps Ops2() { return new FakeWindowOps { Se = Se, Sn = Sn, Fg = Se }; }

            public void Step(double vy)
            {
                Tick++;
                Us += 16667;
                var f = new FrameFacts { Tick = Tick, CamSn = new Vec3(100, Y, 50), CamVelSn = new Vec3(0, vy, 0), Zone = Zone.Core };
                LinkFacts l = SnFacts(Tick);
                l.C7.snFrame = Ops.SnFrameNow;
                var g = new GameFacts { Foreground = true };
                DisplayFeedback fb = Rd.Feedback;
                ViewOutputs o = Driver.Step(ref f, ref l, ref g, ref fb, true, Dc.Mode, ViewStateAdapter.MutantBits());
                Dc.SetMachineFacts(Driver.MachineState.LeftArmed, Driver.Tick);
                Dc.Post(ref l, Se, Driver.State, o.Request, o.ZOrderReassert, true, false, true, ref fb, o.PictureEdge, o.PicturePolicy == ViewPicture.Site);
                _t.Frame(Rd, Ops);
                _t.Frame(Rd, Ops);
            }
        }

        [TestMethod]
        public void Driver_Site_FadeInAtTheLatch_FadeBackOverTheLeaveDwell_RowsBothWays()
        {
            var w = new World(this);
            for (int i = 0; i < 30; i++) w.Step(0);
            Assert.AreEqual(S2State.Space, w.Driver.State);
            // descend through the band (300 m pinned) at 20 m/s
            for (int i = 0; i < 900 && w.Y > 250; i++) { w.Y -= 20.0 / 60; w.Step(-20.0); }
            for (int i = 0; i < 60; i++) w.Step(0);
            Assert.IsTrue(w.Driver.Last.Picture, "the latch on");
            Assert.IsTrue(w.Rd.Feedback.LastPresentedSkipped, "Subnautica's picture after the ramp");
            Assert.IsTrue(w.Gpu.Alphas.Count > 60, "the crossfade pass ran");
            Assert.IsTrue(w.Rows.Any(r => r.Contains("\"ev\":\"s7t.fade\"") && r.Contains("\"phase\":\"start\"") && r.Contains("\"dir\":\"toSn\"") && r.Contains("\"reason\":\"latch\"") && r.Contains("\"path\":\"F\"")), string.Join("\n", w.Rows));
            Assert.IsTrue(w.Rows.Any(r => r.Contains("\"phase\":\"end\"") && r.Contains("\"dir\":\"toSn\"") && r.Contains("\"actualMs\":")), string.Join("\n", w.Rows));
            Assert.IsTrue(S7tSettings.CrossfadeCovers, "the band flash is off while the crossfade covers the swap");

            // climb out: the fade back starts at the leave dwell (+350 m held 1 s), before the latch drops in Space
            int rowsBefore = w.Rows.Count;
            for (int i = 0; i < 1200 && w.Y < 420; i++) { w.Y += 20.0 / 60; w.Step(20.0); }
            for (int i = 0; i < 120; i++) w.Step(0);
            Assert.AreEqual(S2State.Space, w.Driver.State);
            List<string> back = w.Rows.Skip(rowsBefore).ToList();
            string start = back.FirstOrDefault(r => r.Contains("\"phase\":\"start\"") && r.Contains("\"dir\":\"toSe\""));
            Assert.IsNotNull(start, string.Join("\n", back));
            StringAssert.Contains(start, "\"reason\":\"leaveDwell\"");
            StringAssert.Contains(start, "\"view\":\"Track\"");
            Assert.IsTrue(back.Any(r => r.Contains("\"phase\":\"end\"") && r.Contains("\"dir\":\"toSe\"")), string.Join("\n", back));
            Assert.IsFalse(back.Any(r => r.Contains("\"path\":\"snap\"")), "no snap on a normal climb");
            Assert.AreEqual(0.0, w.Rd.Feedback.FadeP);
            Assert.IsFalse(w.Ops.Margins, "margins off after SE's scene frames");
            CollectionAssert.AreEqual(new[] { "in band", "out band" }, w.Hooks, "the S7a hook still sees both edges (the flash decides)");
            Assert.AreEqual(0, _violations.Count, string.Join("\n", _violations));
            Assert.AreEqual(0L, w.Dc.FadeSnaps);
        }

        // ================================================================================= the pass on a WARP device

        private const int DriverTypeWarp = 5;

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        private static byte[] Read(IntPtr device, IntPtr ctx, IntPtr tex, uint w, uint h, int format)
        {
            var s = new D3D.Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = D3D.UsageStaging, CPUAccessFlags = D3D.CpuAccessRead };
            IntPtr staging;
            Assert.IsTrue(D3D.CreateTexture2D(device, ref s, out staging) >= 0);
            try
            {
                D3D.CopyResource(ctx, staging, tex);
                D3D.MappedSubresource m;
                Assert.IsTrue(D3D.Map(ctx, staging, D3D.MapRead, 0u, out m) >= 0);
                var bytes = new byte[w * h * 4];
                for (uint y = 0; y < h; y++) Marshal.Copy(m.Data + (int)(y * m.RowPitch), bytes, (int)(y * w * 4), (int)(w * 4));
                D3D.Unmap(ctx, staging);
                return bytes;
            }
            finally { D3D.Release(staging); }
        }

        [TestMethod]
        public void Pass_OnWarp_PremultipliesTheStoredColour_SetsAlpha_RestoresTheCallersState()
        {
            foreach (int format in new[] { D3D.FormatR8G8B8A8UnormSrgb, D3D.FormatR8G8B8A8Unorm })
            {
                IntPtr device = IntPtr.Zero, ctx = IntPtr.Zero, tex = IntPtr.Zero, rtv = IntPtr.Zero;
                try
                {
                    int level;
                    int hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, 0, IntPtr.Zero, 0, (uint)D3D.SdkVersion, out device, out level, out ctx);
                    if (hr < 0 || device == IntPtr.Zero) Assert.Inconclusive("no WARP device here (hr " + NativeWin.Hr(hr) + ")");
                    const uint W = 32, H = 16;
                    var d = new D3D.Texture2DDesc { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = D3D.UsageDefault, BindFlags = D3D.BindRenderTarget };
                    Assert.IsTrue(D3D.CreateTexture2D(device, ref d, out tex) >= 0);
                    Assert.IsTrue(D3D.CreateRenderTargetView(device, tex, out rtv) >= 0);
                    D3D.ClearRenderTargetView(ctx, rtv, 0.8f, 0.5f, 0.2f, 1f);   // SE's opaque scene
                    byte[] before = Read(device, ctx, tex, W, H, format);
                    D3D.RSSetViewport(ctx, new D3D.Viewport { TopLeftX = 1, TopLeftY = 2, Width = 7, Height = 9, MaxDepth = 1 });
                    D3D.OMSetRenderTarget(ctx, rtv);

                    const double alpha = 0.5;
                    using (var pass = new CrossfadePass())
                    {
                        float f = CrossfadeRules.ColourFactor(alpha, D3D.IsSrgb(format), true);
                        Assert.IsTrue(pass.Execute(device, ctx, rtv, W, H, f, (float)alpha), pass.Error);
                        Assert.AreEqual(1, pass.Executions);
                    }
                    D3D.Viewport vp;
                    Assert.AreEqual(1u, D3D.RSGetViewport(ctx, out vp));
                    Assert.IsTrue(vp.TopLeftX == 1 && vp.TopLeftY == 2 && vp.Width == 7 && vp.Height == 9, "viewport restored");
                    IntPtr bound = D3D.OMGetRenderTarget(ctx);
                    Assert.AreEqual(rtv, bound, "render target restored");
                    D3D.Release(bound);

                    byte[] after = Read(device, ctx, tex, W, H, format);
                    int tol = D3D.IsSrgb(format) ? 4 : 1;   // the power law vs the sRGB curve
                    for (int i = 0; i < after.Length; i += 4)
                    {
                        Assert.AreEqual(128, after[i + 3], 1, "alpha 0.5 (linear in every format)");
                        for (int c = 0; c < 3; c++)
                            Assert.AreEqual(before[i + c] * alpha, after[i + c], tol, "stored colour × alpha (premultiplied for DWM), format " + format + " channel " + c);
                    }
                }
                finally
                {
                    D3D.SafeRelease(ref rtv);
                    D3D.SafeRelease(ref tex);
                    D3D.SafeRelease(ref ctx);
                    D3D.SafeRelease(ref device);
                }
            }
        }
    }
}
