using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>The window calls of the S2 display (real: <see cref="NativeWindowOps"/>; tests: a fake).</summary>
    internal interface IWindowOps
    {
        bool IsWindow(IntPtr h);
        bool IsWindowVisible(IntPtr h);
        /// <summary>DwmExtendFrameIntoClientArea(h, −1 margins) when <paramref name="sheetOfGlass"/>, else 0 margins; the HRESULT.</summary>
        int ExtendFrame(IntPtr h, bool sheetOfGlass);
        bool SetPos(IntPtr h, IntPtr insertAfter, uint flags);
        bool AllowSetForeground(uint pid, out int error);
        bool SetForeground(IntPtr h);
        IntPtr Foreground();
        bool IsTopmost(IntPtr h);
        bool SetTopmost(IntPtr h, bool topmost);
        WalkResult Walk(IntPtr se, IntPtr sn);
        bool GetRect(IntPtr h, out I32x4 rect);
        /// <summary>Composed-desktop pixels (GDI <c>GetPixel</c> on the screen DC) as 0x00BBGGRR; any thread.</summary>
        uint[] ReadScreenPixels(int[] xs, int[] ys);
        long Qpc();
    }

    /// <summary>The GPU side of a frame (real: <c>RenderHooks</c> on SE's device; tests: a fake).</summary>
    internal interface IRenderGpu
    {
        /// <summary>The alpha fill on the finished backbuffer (after the sprites).</summary>
        bool Fill(out string error);
        /// <summary>Copies the finished backbuffer for an alpha count.</summary>
        bool BeginReadback(out string error);
        bool ReadbackPending { get; }
        bool TryFinishReadback(out AlphaStats stats, out string error);
    }

    /// <summary>
 /// S7t T1: the crossfade pass on the drawn scene before the HUD sprites (real: <c>RenderHooks</c> → <see cref="CrossfadePass"/>;
 /// tests: a fake). Optional: a GPU without it never fades (the S7a cut).
 /// </summary>
    internal interface ICrossfadeGpu
    {
        /// <summary>SE's backbuffer to (colour premultiplied by <paramref name="alpha"/> unless <paramref name="premultiply"/> is false, alpha).</summary>
        bool Fade(double alpha, bool premultiply, out string error);
    }

    /// <summary>
 /// S7t T1, design A′ (no per-pixel alpha there): SE's own window's constant alpha (<c>WS_EX_LAYERED</c> +
 /// <c>SetLayeredWindowAttributes(LWA_ALPHA)</c>, render thread = the window's thread). Optional (real: <see cref="NativeWindowOps"/>).
 /// </summary>
    internal interface ILayeredWindowOps
    {
        bool SetLayeredAlpha(IntPtr h, byte alpha);
        bool ClearLayered(IntPtr h);
    }

    /// <summary>A composed-pixel measurement request (ARCHITECTURE §5.1 "Mode selection at start").</summary>
    internal struct ProbeSpec
    {
        /// <summary>Screen points; null = five points of the SE rectangle (centre and the four quarter points).</summary>
        public int[] Xs, Ys;
        public bool HasExpected;
        public byte R, G, B;
        public int Tolerance;
    }

    /// <summary>What the update thread asks the render thread for (copied under a lock once per render frame).</summary>
    internal struct DisplayRequest
    {
        /// <summary>Link up and peer not exited: SE's window set-up (NOTOPMOST, z-order) is allowed; restored when false.</summary>
        public bool Linked;
        public IntPtr Se, Sn;
        public uint SnPid;
        /// <summary>C7 <c>snFrame</c> as the update thread last read it. 0 = unknown.</summary>
        public ulong SnFrame;
        public DisplayDesign Mode;
        /// <summary>F: DWM margins wanted (the machine's request: S7a <c>Site</c> every non-Space state; <c>Waterline</c> Prime,
 /// Underwater, Surfacing; forced see-through).</summary>
        public bool Margins;
        /// <summary>F: 3D scene skip wanted (S7a <c>Site</c>: the picture latch; <c>Waterline</c>: Underwater; forced see-through);
 /// applied only after the margins have been
 /// on for one presented frame.</summary>
        public bool Skip;
        /// <summary>F: the alpha fill while the margins are wanted or on and the scene is drawn (config / de.display).</summary>
        public bool AlphaFill;
        /// <summary>A′: SE below Subnautica.</summary>
        public bool Lowered;
        /// <summary>Walk the z-order every frame (Prime/Underwater/Surfacing/forced); once per second otherwise.</summary>
        public bool WalkEveryFrame;
        /// <summary>Prime: re-assert the z-order when it is lost, at most every 2 s (the Prime → Underwater guard needs it).</summary>
        public bool AutoReassert;
        /// <summary>Counters: a change asks for one action.</summary>
        public int ZSeq, ForegroundSeq, ReadbackSeq, ProbeSeq;
        public string ZWhy;
        public bool ReadbackPeriodic;
        public ProbeSpec Probe;
        /// <summary>QPC of the latest skip request (T-D1 latency: request → first skipped present).</summary>
        public long SkipRequestQpc;
        /// <summary>Label of the latest toggle/state for the logs (e.g. "toggle #3", "Underwater").</summary>
        public string Label;
        /// <summary>S7t T1: the crossfade ramp runs (Site, the crossfade on, design F, or A′ with its ramp switch); false = S7a's instant swap.</summary>
        public bool FadeOn;
        /// <summary>S7t T1: the ramp's target is Subnautica's picture (F: the skip engages when it is reached; A′: the lowering).</summary>
        public bool FadeToSn;
        /// <summary>S7t T1: the ramp's duration (ms).</summary>
        public double FadeMs;
        /// <summary>S7t T1: a new value jumps the ramp to its target at once (a picture loss without the leave dwell).</summary>
        public int FadeSnapSeq;
        /// <summary>S7t T1 dev comparison (<c>de.s7t fade straight</c>): the colour is not premultiplied by the alpha.</summary>
        public bool FadeStraight;
    }

    /// <summary>What the render thread reports back (copied under a lock).</summary>
    internal struct DisplayFeedback
    {
        public bool HooksSeen;
        public long Presents, SceneDrawnPresents, SkippedPresents, FullDraws;
        public bool MarginsOn;
        /// <summary>Frames presented since the margins went on.</summary>
        public long MarginsPresents;
        public bool LastPresentedSceneDrawn, LastPresentedSkipped;
        public int LastDwmHr;
        public bool ZOrderOk, ZPending;
        public int ZAttempts;
        public long Walks;
        public string LastWalk;
        public bool Foreground, ForegroundFailed;
        /// <summary>Subnautica's window is no longer a visible window: destroyed, or hidden for at least
 /// <see cref="RenderDisplay.SnHiddenDebounceMs"/> over <see cref="RenderDisplay.SnHiddenDebouncePresents"/> presents.</summary>
        public bool SnWindowLost;
        /// <summary>The lost window was destroyed (<c>IsWindow</c> false): not recoverable for this handle.</summary>
        public bool SnWindowGone;
        /// <summary>A hidden window that became visible again (the loss withdrawn).</summary>
        public long SnWindowRecoveries;
        /// <summary>Presents in a row with the window hidden, below the debounce (the scene is drawn meanwhile).</summary>
        public int SnHiddenPresents;
        public bool Lowered;
        /// <summary>The z-order the walker checks: false = Subnautica directly under SE; true = A′ lowered, SE directly
 /// under Subnautica.</summary>
        public bool ZInverted;
        public bool SeRectValid;
        public I32x4 SeRect;
        public long Fills;
        public string FillError;
        public int ProbeDone;
        public string ProbeResult;
        /// <summary>1 honoured, 0 not honoured, −1 undecided (no expected colour, a mixed result, reads that disagree, a
 /// timeout). Both reads of a probe must agree for 1 or 0.</summary>
        public int ProbeVerdict;
        /// <summary>The probe of <see cref="ProbeDone"/> had an expected colour.</summary>
        public bool ProbeHadExpected;
        public bool ProbeActive;
        /// <summary>S7t T1: the ramp's progress (0 = SE's picture, 1 = Subnautica's) after the last render frame.</summary>
        public double FadeP;
        /// <summary>S7t T1: frames the crossfade pass ran; frames the ramp held the skip back (the scene drawn meanwhile); pass failures.</summary>
        public long FadePasses, FadeHeldSkips, FadeErrors;
        public string FadeError;
        /// <summary>S7t T1: frames the ramp held the margins on beyond the request (a fade back that outlasts the dwell).</summary>
        public long FadeMarginHolds;
        /// <summary>S7t T1: the ConsumeMainSprites prefix never ran under an F ramp (3 drawn frames): the ramp holds nothing back any more.</summary>
        public bool FadeHookDead;
        /// <summary>S7t T1, A′: SE's window is layered (its constant alpha ramps); calls made.</summary>
        public bool Layered;
        public long LayeredCalls;

        public bool MarginsReady { get { return MarginsOn && MarginsPresents >= 1; } }
    }

    /// <summary>
 /// SE's render-thread side of the S2 display (ARCHITECTURE §5.1, §4.4). Every call on <c>seHwnd</c> is made here, by

 /// thread); the update thread only posts a <see cref="DisplayRequest"/>. Game-independent: window calls go through
 /// <see cref="IWindowOps"/>, GPU work through <see cref="IRenderGpu"/>, so the ordering rules are unit-tested:
 /// <list type="bullet">
 /// <item>Entering: the margins go on after a present, and the scene is skipped only once at least one frame has been
 /// presented with the margins on (and only while the margins are wanted). Leaving: the scene is drawn first, and the
 /// margins go off only after <see cref="MarginsOffAfterDrawnPresents"/> frames with the scene drawn have been
 /// presented (review fix finding 5: SE sets no maximum frame latency, so DXGI's default of 3 frames may
 /// still be queued when <c>Present</c> returns; 10-present timeout when no scene frame comes, e.g. a world unload).
 /// No frame ever presents a cleared backbuffer without margins.</item>
 /// <item>z-order re-assert: <c>AllowSetForegroundWindow(snPid)</c>, then <c>SetWindowPos(snHwnd, seHwnd, NOMOVE |
 /// NOSIZE | NOACTIVATE | ASYNCWINDOWPOS)</c>, verified by the walker once at least 2 presents passed and Subnautica
 /// ran its message pump (C7 <c>snFrame</c> advanced by 2) or <see cref="ZVerifyMaxWaitMs"/> passed, at most 3 retries → <c>zOrderOk</c>. While A′ has SE lowered the wanted order is the inverse: SE
 /// directly under Subnautica, asserted by SE on its own window (<c>SetWindowPos(seHwnd, snHwnd)</c>, synchronous) and
 /// walked from <c>snHwnd</c>.</item>
 /// <item>TopMost: removed (<c>HWND_NOTOPMOST</c>) while linked, re-applied on unlink when SE had it and is still the
 /// foreground window; <see cref="OnModeChanged"/> undoes SE's own <c>TopMost = true</c> while linked.</item>
 /// <item>Foreground: <c>SetForegroundWindow(seHwnd)</c> on request, checked on later frames, up to 3 retries.</item>
 /// <item>Window watch: while the margins are on (or SE is lowered) <c>IsWindow</c> ∧ <c>IsWindowVisible(snHwnd)</c>
 /// every frame. A destroyed window is lost at once; a hidden one draws SE's scene at once but counts as lost only
 /// after <see cref="SnHiddenDebounceMs"/> and <see cref="SnHiddenDebouncePresents"/> presents, and the loss is
 /// withdrawn when the same window is visible again. A lost window drops the margins after the
 /// scene frames.</item>
 /// <item>A′: lower below Subnautica, then stop drawing; to come back, draw first, then raise after
 /// <see cref="RaiseAfterDrawnPresents"/> drawn presents.</item>
 /// <item>Dev measurements: backbuffer alpha readback, composed-pixel probe.</item>
 /// <item>S7t T1, the crossfade (<see cref="CrossfadeRules"/>; CONTRACT §5.4 "S7t crossfade"): a ramp stepped once per
 /// render frame on the QPC clock toward the request's target. F: the scene skip engages only once the ramp reached
 /// Subnautica (the scene is drawn meanwhile) and the crossfade pass (<see cref="OnBeforeSprites"/>, before the HUD
 /// sprites) scales SE's drawn frame to the ramp's alpha while the margins are presented; the alpha fill skips a faded
 /// frame; the margins stay on while the ramp still shows Subnautica. A′ (only with its switch): SE stays on top and
 /// drawing while its window's constant alpha fades (<see cref="ILayeredWindowOps"/>), is lowered at the end, and on the
 /// way back is raised at alpha 0 and fades in; the layered style is removed when the ramp rests.</item>
 /// </list>
 /// Log lines are queued (<see cref="DrainLog"/> on the update thread): the render thread never does file I/O.
 /// </summary>
    internal sealed class RenderDisplay
    {
        public const int ZMaxAttempts = 4;            // 1 + 3 retries (ROADMAP S2 "≤ 3 retries")
        public const int ZVerifyAfterPresents = 2;
        /// <summary>An asynchronous re-assert is handled by Subnautica's message pump: verify once its C7 snFrame advanced
 /// by this many frames since the assert (one stale C7 read tolerated) …</summary>
        public const int ZVerifyAfterSnFrames = 2;
        /// <summary>… or after this long (Subnautica at 10 fps and BelowNormal in Idle/Track: 100 ms a frame).</summary>
        public const double ZVerifyMaxWaitMs = 400;
        public const int FgMaxAttempts = 4;
        /// <summary>DXGI's default maximum frame latency (SE sets none): frames still queued when <c>Present</c> returns.</summary>
        public const int DxgiMaxFrameLatency = 3;
        /// <summary>Scene-drawn presents before the margins go off (the queued skipped frames are composed with margins).</summary>
        public const int MarginsOffAfterDrawnPresents = DxgiMaxFrameLatency;
        public const int MarginsOffTimeoutPresents = 10;
        /// <summary>A′: drawn presents before SE raises itself again (no stale frame on top).</summary>
        public const int RaiseAfterDrawnPresents = DxgiMaxFrameLatency;
        public const int RaiseTimeoutPresents = 10;
        /// <summary>Skipped presents before the first probe read: 3 for the picture plus the frames DXGI may still queue.</summary>
        public const int ProbeSkippedPresentsBeforeRead = 3 + DxgiMaxFrameLatency;
        public const double ProbeMinWaitMs = 100;
        public const double ProbeSecondReadAfterMs = 100;
        public const int ProbeSecondReadAfterSkipped = 3;
        public const double ProbeTimeoutMs = 3000;
        /// <summary>A hidden Subnautica window counts as lost only after this long and this many presents (finding 3).</summary>
        public const double SnHiddenDebounceMs = 250;
        public const int SnHiddenDebouncePresents = 3;
        public const double AutoReassertIntervalMs = 2000;
        public const double ReadbackPeriodMs = 5000;
        public const int SlowWalkEveryPresents = 60;
        public const int RectEveryPresents = 60;
        public const int ForegroundEveryPresents = 10;
        public const int MaxQueuedLogLines = 512;

        private readonly IWindowOps _ops;
        private readonly object _lock = new object();
        private readonly ConcurrentQueue<string> _log = new ConcurrentQueue<string>();
        private int _queued;
        private readonly double _qpcToMs = 1000.0 / Stopwatch.Frequency;

        private DisplayRequest _request;      // written by the update thread under _lock
        private DisplayRequest _frame;        // this render frame's copy
        private bool _frameHasRequest;
        private DisplayFeedback _fb;          // render-thread state, published under _lock
        private DisplayFeedback _published;

        // frame
        private bool _inFullDraw, _frameFullDraw, _frameDecided, _frameSceneDrawn, _frameSkipped, _frameFilled;
        // margins
        private int _marginsOffWait, _marginsOffDrawn;
        private int _marginsFailures;
        private long _marginsRetryAt;
        private bool _skipLatencyArmed;
        private long _skipLatencyRequestQpc;
        // z-order
        private int _zSeqDone, _zAttempts;
        private long _zVerifyAt;
        private bool _zPending, _zSyncAssert;
        private long _zAssertQpc;
        private ulong _zSnFrameAtAssert;
        private long _lastAutoZQpc;
        private string _zWhy;
        private readonly IntPtr[] _walkSkipped = new IntPtr[ZOrderWalker.MaxLoggedSkipped];
        private readonly string[] _walkSkippedWhy = new string[ZOrderWalker.MaxLoggedSkipped];
        private bool _walkLoggedOk;
        private bool _walkEverLogged;
        // foreground
        private int _fgSeqDone, _fgAttempts;
        private long _fgCheckAt;
        private bool _fgPending;
        // topmost
        private bool _topHandled, _wasTopmost;
        private IntPtr _topHwnd;
        // window watch
        private IntPtr _watchedSn;
        private long _snHiddenSinceQpc;
        // A'
        private bool _raisePending;
        private int _raiseWait, _raiseDrawn;
        // readback
        private int _rbSeqDone;
        private bool _rbWanted;
        private long _rbLastQpc;
        private string _rbContext;
        // probe
        private int _probeSeqDone;
        private bool _probeActive;
        private long _probeStartQpc, _probeSkippedAtStart, _probeRead1Qpc, _probeSkippedAtRead1;
        private int _probeReads;           // reads started (0, 1, 2)
        private ProbeSpec _probe;
        private int[] _probeXs, _probeYs;
        private uint[] _probeRead1;
        private volatile uint[] _probeColours;
        private volatile bool _probeWorkerDone;
        private bool _seenLink;
        // S7t T1: the crossfade ramp (render thread)
        private double _fadeP;
        private long _fadeLastQpc;
        private bool _fadeHasClock;
        private int _fadeSnapSeqDone;
        private bool _frameFaded, _frameFadeDecided;
        private bool _fadeHookDead;
        private int _fadeHookMisses;
        private bool _layeredOn, _layeredFailed;
        private byte _layeredAlpha = 255;
        private IntPtr _layeredHwnd;

        public IRenderGpu Gpu;
        public long Errors { get; private set; }

        public RenderDisplay(IWindowOps ops)
        {
            _ops = ops;
            _fb.ProbeVerdict = -1;
        }

        // =========================================================================================== update thread

        public void SetRequest(ref DisplayRequest r)
        {
            lock (_lock) _request = r;
        }

        public DisplayFeedback Feedback
        {
            get { lock (_lock) return _published; }
        }

        /// <summary>Update thread: hands queued render-thread log lines to <paramref name="sink"/>.</summary>
        public void DrainLog(Action<string> sink)
        {
            string line;
            while (_log.TryDequeue(out line))
            {
                Interlocked.Decrement(ref _queued);
                try { sink(line); } catch (Exception) { }
            }
        }

        // ============================================================================================ render thread

 /// <summary><c>MyRender11.FullDraw</c> prefix: a new drawn frame starts.</summary>
        public void OnFullDrawStart()
        {
            try
            {
                if (!_frameHasRequest) TakeRequest();
                _inFullDraw = true;
                _frameFullDraw = true;
                _frameDecided = false;
                _frameSceneDrawn = false;
                _frameSkipped = false;
                _frameFilled = false;
                _frameFaded = false;
                _frameFadeDecided = false;
                FadeTick(_ops.Qpc()); // S7t T1: the ramp of this frame (the skip decision and the pass read it)
            }
            catch (Exception e) { Fail("FullDraw prefix", e); }
        }

        /// <summary>
 /// S7t T1: one step of the crossfade ramp per render frame (FullDraw start, or the present of a frame without one)
 /// on the QPC clock. Without the ramp, or on a new snap sequence, the progress jumps to the target (S7a's instant
 /// swap). A′: the way back starts only once SE is raised again (drawn frames first, S2's order).
 /// </summary>
        private void FadeTick(long now)
        {
            DisplayRequest r = _frame;
            double dt = _fadeHasClock ? Ms(now - _fadeLastQpc) : 0.0;
            _fadeLastQpc = now;
            _fadeHasClock = true;
            double target = r.FadeToSn ? 1.0 : 0.0;
            if (!r.FadeOn || !(r.FadeMs > 0.0) || r.FadeSnapSeq != _fadeSnapSeqDone)
            {
                if (r.FadeOn && r.FadeSnapSeq != _fadeSnapSeqDone && _fadeP != target)
                    Log("DE.Display crossfade snap to " + (r.FadeToSn ? "Subnautica" : "SE") + " (picture lost without the leave dwell) at p=" + _fadeP.ToString("0.###", CultureInfo.InvariantCulture));
                _fadeSnapSeqDone = r.FadeSnapSeq;
                _fadeP = target;
            }
            else
            {
                bool holdBack = r.Mode == DisplayDesign.APrime && !r.FadeToSn && _fb.Lowered;
                if (!holdBack) _fadeP = CrossfadeRules.Step(_fadeP, r.FadeToSn, dt, r.FadeMs);
            }
            _fb.FadeP = _fadeP;
        }

        /// <summary>
 /// S7t T1: the <c>ConsumeMainSprites</c> prefix (SE's scene or its clear is in the backbuffer, the HUD sprites not
 /// yet): the crossfade pass, once per FullDraw (a screenshot's second sprite call is ignored), on a drawn scene with
 /// the margins presented while the ramp shows any of Subnautica's picture.
 /// </summary>
        public void OnBeforeSprites()
        {
            try
            {
                if (!_inFullDraw || _frameFadeDecided) return;
                _frameFadeDecided = true;
                DisplayRequest r = _frame;
                if (r.Mode != DisplayDesign.F) return;
                if (!CrossfadeRules.PassWanted(r.FadeOn, _fadeP, _frameSceneDrawn, _fb.MarginsOn && _fb.MarginsPresents >= 1)) return;
                var gpu = Gpu as ICrossfadeGpu;
                if (gpu == null) return;
                string err;
                if (gpu.Fade(CrossfadeRules.Alpha(_fadeP), !r.FadeStraight, out err)) { _frameFaded = true; _fb.FadePasses++; }
                else
                {
                    _fb.FadeErrors++;
                    if (err != _fb.FadeError)
                    {
                        _fb.FadeError = err;
                        Log("DE.Display crossfade pass FAILED " + err + " (the frame keeps SE's opaque scene; reported once per distinct error)");
                    }
                }
            }
            catch (Exception e) { Fail("ConsumeMainSprites prefix", e); }
        }

        /// <summary>
 /// <c>RenderMainSprites</c> prefix (first call inside FullDraw, after the render messages set <c>m_drawScene</c>):
 /// true when the 3D scene must be skipped this frame (the caller clears <c>m_drawScene</c>, so SE's own else-branch
 /// clears the backbuffer to (0,0,0,0) and the sprites still draw).
 /// </summary>
        public bool DecideSkip(bool sceneWanted)
        {
            try
            {
                if (!_inFullDraw || _frameDecided) return false;
                _frameDecided = true;
                bool skip = sceneWanted && SkipAllowed();
                _frameSkipped = skip;
                _frameSceneDrawn = sceneWanted && !skip;
                // S7t T1: a skip the request asks for but the ramp holds back (SE's scene drawn and faded meanwhile)
                if (sceneWanted && !skip && _frame.Skip && _frame.FadeOn && !_fadeHookDead && !CrossfadeRules.SkipAllowed(true, _fadeP)) _fb.FadeHeldSkips++;
                return skip;
            }
            catch (Exception e) { Fail("skip decision", e); return false; }
        }

        private bool SkipAllowed()
        {
            DisplayRequest r = _frame;
            // a hidden Subnautica window (even below the debounce) gets SE's own scene at once
            if (r.Mode != DisplayDesign.F || !r.Linked || r.Sn == IntPtr.Zero || _fb.SnWindowLost || _fb.SnHiddenPresents > 0) return false;
            // the skip only while the margins are wanted too: once they are not, every frame draws the scene, so the
 // margins can never go off right after a cleared frame (a forced skip with forced margins off draws)
 // S7t T1: and only once the crossfade ramp reached Subnautica (SE's scene is drawn and faded until then)
            if (!((r.Skip && r.Margins && CrossfadeRules.SkipAllowed(r.FadeOn && !_fadeHookDead, _fadeP)) || _probeActive)) return false;
            return _fb.MarginsOn && _fb.MarginsPresents >= 1;
        }

        /// <summary><c>MyRender11.FullDraw</c> postfix: the backbuffer is complete (scene or clear, then sprites).</summary>
        public void OnFullDrawEnd(bool draw)
        {
            try
            {
                _inFullDraw = false;
                if (!draw) { _frameSceneDrawn = false; return; }
                DisplayRequest r = _frame;
                // S7t T1: a FullDraw that drew the scene under a running F ramp without reaching the ConsumeMainSprites prefix
 // (the patch missing, or the call inlined away): after 3 such frames the ramp no longer holds the skip back
                if (r.Mode == DisplayDesign.F && r.FadeOn && _frameSceneDrawn && !_frameFadeDecided && !_fadeHookDead && ++_fadeHookMisses >= 3)
                {
                    _fadeHookDead = true;
                    _fb.FadeHookDead = true;
                    Log("DE.Display crossfade OFF: the ConsumeMainSprites prefix never ran in " + _fadeHookMisses + " drawn frames (patch missing or inlined); S7a's instant swap");
                }
                if (_frameFadeDecided) _fadeHookMisses = 0;
                IRenderGpu gpu = Gpu;
                if (gpu == null) return;
                // S7t T1: a frame the crossfade pass faded keeps its alpha (the pass set it on the scene; the HUD blended over it)
                if (r.Mode == DisplayDesign.F && r.AlphaFill && _frameSceneDrawn && !_frameFaded && (r.Margins || _fb.MarginsOn || _probeActive))
                {
                    string err;
                    if (gpu.Fill(out err)) { _frameFilled = true; _fb.Fills++; }
                    else if (err != _fb.FillError)
                    {
                        _fb.FillError = err;
                        Log("DE.Display alphaFill FAILED " + err + " (Prime frames keep the sprites' alpha; reported once per distinct error)");
                    }
                }
                if (_rbWanted && !gpu.ReadbackPending)
                {
                    string err;
                    _rbWanted = false;
                    _rbLastQpc = _ops.Qpc();
                    _rbContext = "mode=" + S2Config.Name(r.Mode) + " label=" + (r.Label ?? "-") + " margins=" + (_fb.MarginsOn ? "on" : "off") +
                                 " scene=" + (_frameSkipped ? "skipped" : _frameSceneDrawn ? "drawn" : "none") + " alphaFill=" + (_frameFilled ? "done" : "no") +
                                 " crossfade=" + (_frameFaded ? "alpha " + CrossfadeRules.Alpha(_fadeP).ToString("0.###", CultureInfo.InvariantCulture) : "no");
                    if (!gpu.BeginReadback(out err)) Log("DE.Display alpha readback FAILED " + err);
                }
            }
            catch (Exception e) { Fail("FullDraw postfix", e); }
        }

        /// <summary>
 /// <c>MyGameWindow.OnModeChanged</c> postfix (render thread: SE applies render settings there, VERIFIED): SE has just
 /// set <c>TopMost = true</c> for its fullscreen-window mode; undone while linked.
 /// </summary>
        public void OnModeChanged()
        {
            try
            {
                DisplayRequest r;
                lock (_lock) r = _request;
                if (!r.Linked || r.Se == IntPtr.Zero || !_topHandled) return;
                bool top = _ops.IsTopmost(r.Se);
                if (!top) return;
                bool ok = _ops.SetTopmost(r.Se, false);
                _wasTopmost = true;
                Log("DE.Display TopMost set again by SE's mode change: undone (HWND_NOTOPMOST " + (ok ? "ok" : "FAILED") + ")");
            }
            catch (Exception e) { Fail("OnModeChanged postfix", e); }
        }

        /// <summary><c>MyRender11.Present</c> postfix: the frame is on its way to DWM; window work for the next one.</summary>
        public void OnPresented()
        {
            try { PresentedCore(); }
            catch (Exception e) { Fail("Present postfix", e); }
            _frameHasRequest = false;
            _frameFullDraw = false;
            _inFullDraw = false;
        }

        private void TakeRequest()
        {
            lock (_lock) _frame = _request;
            _frameHasRequest = true;
        }

        private void PresentedCore()
        {
            if (!_frameHasRequest) TakeRequest();
            DisplayRequest r = _frame;
            long now = _ops.Qpc();
            _fb.HooksSeen = true;
            if (!_frameFullDraw) FadeTick(now); // S7t T1: a frame without FullDraw (A′ lowered, not drawing) still steps the ramp

 // ---- accounting of the frame just presented
            _fb.Presents++;
            if (_frameFullDraw) _fb.FullDraws++;
            bool drawn = _frameFullDraw && _frameSceneDrawn;
            // F: the scene was skipped in FullDraw; A': SE was lowered and drew nothing (DrawEnabled false)
            bool skipped = (_frameFullDraw && _frameSkipped) || (!_frameFullDraw && _fb.Lowered && FallbackAPrime.DrawDisabled);
            if (drawn) _fb.SceneDrawnPresents++;
            if (skipped) _fb.SkippedPresents++;
            if (_fb.MarginsOn) _fb.MarginsPresents++;
            _fb.LastPresentedSceneDrawn = drawn;
            _fb.LastPresentedSkipped = skipped;
            if (r.SkipRequestQpc != 0 && r.SkipRequestQpc != _skipLatencyRequestQpc) { _skipLatencyRequestQpc = r.SkipRequestQpc; _skipLatencyArmed = true; }
            if (skipped && _skipLatencyArmed)
            {
                _skipLatencyArmed = false;
                Log("DE.Display see-through first skipped frame presented +" + Ms(now - _skipLatencyRequestQpc) + " ms after the request (" +
                    (r.Label ?? "-") + ", margins on for " + _fb.MarginsPresents + " presents)");
            }

            // ---- the Subnautica window (watched while it matters, and while lost to see it come back)
            WatchSnWindow(r, now);

            // ---- probe (forces see-through while it runs)
            ProbeStep(r, now);

            // ---- DWM margins (F)
 // S7t T1: the margins also stay on while the crossfade ramp still shows part of Subnautica's picture
            bool fadeHold = r.Mode == DisplayDesign.F && CrossfadeRules.HoldMargins(r.FadeOn, _fadeP);
            bool wantMargins = r.Mode == DisplayDesign.F && r.Linked && r.Se != IntPtr.Zero && !_fb.SnWindowLost && (r.Margins || _probeActive || fadeHold);
            if (wantMargins && !r.Margins && !_probeActive) _fb.FadeMarginHolds++;
            if (wantMargins && !_fb.MarginsOn)
            {
                if (_marginsFailures == 0 || _fb.Presents >= _marginsRetryAt) ApplyMargins(r, true);
            }
            else if (!wantMargins && _fb.MarginsOn)
            {
                // review fix (finding 5): off only after DxgiMaxFrameLatency scene-drawn presents, so the
 // skipped frames DXGI may still have queued are composed while the margins are on
                _marginsOffWait++;
                if (drawn) _marginsOffDrawn++;
                bool enough = _marginsOffDrawn >= MarginsOffAfterDrawnPresents;
                if (enough || _marginsOffWait > MarginsOffTimeoutPresents || r.Se == IntPtr.Zero)
                {
                    if (!enough && r.Se != IntPtr.Zero)
                        Log("DE.Display margins off after " + _marginsOffWait + " presents with " + _marginsOffDrawn + " scene frame(s) (timeout; " +
                            MarginsOffAfterDrawnPresents + " wanted)");
                    ApplyMargins(r, false);
                }
            }
            if (wantMargins || !_fb.MarginsOn) { _marginsOffWait = 0; _marginsOffDrawn = 0; }

            // ---- A' lower / raise
            APrimeStep(r, drawn);

            // ---- TopMost while linked
            TopmostStep(r);

            // ---- foreground
            ForegroundStep(r);

            // ---- z-order
            ZOrderStep(r, now);

            // ---- rectangle
            if (r.Se != IntPtr.Zero && (_fb.Presents % RectEveryPresents == 1 || !_fb.SeRectValid))
            {
                I32x4 rect;
                if (_ops.GetRect(r.Se, out rect)) { _fb.SeRect = rect; _fb.SeRectValid = true; }
            }

            // ---- readback
            if (r.ReadbackSeq != _rbSeqDone) { _rbSeqDone = r.ReadbackSeq; _rbWanted = true; }
            if (r.ReadbackPeriodic && !_rbWanted && Ms(now - _rbLastQpc) >= ReadbackPeriodMs) _rbWanted = true;
            IRenderGpu gpu = Gpu;
            if (gpu != null && gpu.ReadbackPending)
            {
                AlphaStats s;
                string err;
                if (gpu.TryFinishReadback(out s, out err))
                    Log("DE.Display alpha " + _rbContext + " " + s.Width + "x" + s.Height + " format=" + s.Format + " pixels=" + s.Pixels +
                        " alphaGt0=" + s.AlphaGt0 + " (" + Pct(s.FractionGt0) + " %) alphaLt1=" + s.AlphaLt1 + " (" + Pct(s.FractionLt1) + " %) alpha0=" + s.Alpha0 +
                        " [T-D1: no menu underwater < 2 % alphaGt0; scene drawn < 0.1 % alphaLt1]");
                else if (err != null) Log("DE.Display alpha readback FAILED " + err);
            }

            Publish();
        }

        // ------------------------------------------------------------------------------------------------ margins

        private void ApplyMargins(DisplayRequest r, bool on)
        {
            int hr;
            bool mutant = S2Mutants.DwmCallDisabled;
            if (mutant) hr = 0;
            else hr = _ops.ExtendFrame(r.Se, on);
            _fb.LastDwmHr = hr;
            if (hr < 0)
            {
                _marginsFailures++;
                _marginsRetryAt = _fb.Presents + 60;
                if (_marginsFailures <= 3)
                    Log("DE.Display DwmExtendFrameIntoClientArea(" + (on ? "-1" : "0") + ") FAILED hr=" + NativeWin.Hr(hr) + " (attempt " + _marginsFailures + "; retry in 60 frames)");
                if (!on) { _fb.MarginsOn = false; _fb.MarginsPresents = 0; } // a failed "off" is not retried as on
                return;
            }
            _marginsFailures = 0;
            _fb.MarginsOn = on;
            _fb.MarginsPresents = 0;
            Log("DE.Display margins " + (on ? "ON (-1)" : "OFF (0)") + " hr=" + NativeWin.Hr(hr) + (mutant ? " (MUTANT S2.M1: DwmExtendFrameIntoClientArea NOT called)" : " S_OK") +
                " label=" + (r.Label ?? "-") + " presents=" + _fb.Presents);
        }

        // ------------------------------------------------------------------------------------------- window watch

 /// <summary>
 /// Review fix (finding 3): a destroyed window is lost at once (PeerExited, latched for this handle); a
 /// hidden one makes SE draw its scene at once (<see cref="DisplayFeedback.SnHiddenPresents"/> &gt; 0 refuses the
 /// skip) but counts as lost only once hidden for <see cref="SnHiddenDebounceMs"/> over
 /// <see cref="SnHiddenDebouncePresents"/> presents (Subnautica's own restyle hides it for a moment); a hidden loss
 /// is withdrawn when the same window is visible again (the update thread then withdraws the PeerExited report).
 /// </summary>
        private void WatchSnWindow(DisplayRequest r, long now)
        {
            if (r.Sn != _watchedSn)
            {
                _watchedSn = r.Sn;
                _fb.SnWindowLost = false;
                _fb.SnWindowGone = false;
                _fb.SnHiddenPresents = 0;
            }
            bool watch = r.Sn != IntPtr.Zero && (_fb.MarginsOn || _fb.Lowered || r.Margins || r.Lowered || _fb.SnWindowLost || _fb.SnHiddenPresents > 0);
            if (!watch) { _fb.SnHiddenPresents = 0; return; }
            if (_fb.SnWindowGone) return;
            bool exists = _ops.IsWindow(r.Sn);
            bool visible = exists && _ops.IsWindowVisible(r.Sn);
            if (!exists)
            {
                _fb.SnWindowGone = true;
                _fb.SnHiddenPresents = 0;
                if (!_fb.SnWindowLost)
                {
                    _fb.SnWindowLost = true;
                    Log("DE.Display snHwnd " + NativeWin.Hex(r.Sn) + " destroyed (IsWindow false): scene drawn now, margins off after it (PeerExited)");
                }
                else Log("DE.Display snHwnd " + NativeWin.Hex(r.Sn) + " destroyed while lost (no recovery for this handle)");
                return;
            }
            if (_fb.SnWindowLost)
            {
                if (visible)
                {
                    _fb.SnWindowLost = false;
                    _fb.SnHiddenPresents = 0;
                    _fb.SnWindowRecoveries++;
                    Log("DE.Display snHwnd " + NativeWin.Hex(r.Sn) + " visible again after " + Ms(now - _snHiddenSinceQpc).ToString("0", CultureInfo.InvariantCulture) +
                        " ms: window loss withdrawn (recovery #" + _fb.SnWindowRecoveries + ")");
                }
                return;
            }
            if (visible) { _fb.SnHiddenPresents = 0; return; }
            if (_fb.SnHiddenPresents++ == 0)
            {
                _snHiddenSinceQpc = now;
                Log("DE.Display snHwnd " + NativeWin.Hex(r.Sn) + " hidden: SE's scene drawn meanwhile; lost after " +
                    SnHiddenDebounceMs.ToString("0", CultureInfo.InvariantCulture) + " ms / " + SnHiddenDebouncePresents + " presents");
            }
            if (_fb.SnHiddenPresents >= SnHiddenDebouncePresents && Ms(now - _snHiddenSinceQpc) >= SnHiddenDebounceMs)
            {
                _fb.SnWindowLost = true;
                Log("DE.Display snHwnd " + NativeWin.Hex(r.Sn) + " hidden for " + Ms(now - _snHiddenSinceQpc).ToString("0", CultureInfo.InvariantCulture) +
                    " ms over " + _fb.SnHiddenPresents + " presents: no longer a visible window, margins off after the scene frames (PeerExited, withdrawn if it shows again)");
            }
        }

        // --------------------------------------------------------------------------------------------------- A'

 /// <summary>
 /// A′: <see cref="DisplayRequest.Lowered"/> puts SE below Subnautica; <see cref="DisplayRequest.Skip"/> while lowered
 /// stops SE drawing (the machine's SceneSkip = <c>DrawEnabled</c> false: the latch under S7a <c>Site</c>, Underwater under
 /// <c>Waterline</c>; during a <c>Waterline</c> Surfacing SE stays
 /// lowered but draws again, so a fresh frame is ready before it is raised).
 /// </summary>
        private void APrimeStep(DisplayRequest r, bool drawn)
        {
            bool wantLower = r.Mode == DisplayDesign.APrime && r.Linked && r.Lowered && !_fb.SnWindowLost && r.Sn != IntPtr.Zero && r.Se != IntPtr.Zero;
            // a hidden Subnautica window (below the debounce): SE stays lowered but draws, so it is what shows
            bool wantNoDraw = wantLower && r.Skip && _fb.SnHiddenPresents == 0;
            // S7t T1 (A′ ramp, its own switch): SE's window alpha instead of the per-pixel alpha A′ does not have
            var layered = _ops as ILayeredWindowOps;
            if (!r.FadeOn) _layeredFailed = false; // a new try with the next ramp
            bool fadeA = r.Mode == DisplayDesign.APrime && r.FadeOn && layered != null && r.Se != IntPtr.Zero && !_fb.SnWindowLost && !_layeredFailed;
            if (wantLower && !_fb.Lowered && fadeA && _fadeP < 1.0)
            {
                // the fade to Subnautica: SE stays on top and drawing while its window fades out; lowered at the end
                _raisePending = false;
                FallbackAPrime.DrawDisabled = false;
                SetLayered(layered, r.Se, CrossfadeRules.Alpha(_fadeP));
                return;
            }
            if (wantLower && !_fb.Lowered)
            {
                _raisePending = false;
                bool ok = _ops.SetPos(r.Se, r.Sn, NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE);
                _fb.Lowered = true;
                FallbackAPrime.DrawDisabled = wantNoDraw;
                Log("DE.Display A' lowered SE below Subnautica (SetWindowPos(seHwnd, snHwnd) " + (ok ? "ok" : "FAILED") + "); DrawEnabled " +
                    (wantNoDraw ? "false" : "true") + "; label=" + (r.Label ?? "-"));
                if (_layeredOn) ClearLayered(layered, "lowered (S7t ramp done)"); // below Subnautica: opaque again
            }
            else if (wantLower && _fb.Lowered)
            {
                _raisePending = false;
                if (FallbackAPrime.DrawDisabled != wantNoDraw)
                {
                    FallbackAPrime.DrawDisabled = wantNoDraw;
                    Log("DE.Display A' lowered, DrawEnabled " + (wantNoDraw ? "false" : "true (drawing again before the raise)") + "; label=" + (r.Label ?? "-"));
                }
            }
            else if (!wantLower && _fb.Lowered)
            {
                // draw first; raise after RaiseAfterDrawnPresents drawn presents
                bool wasOff = FallbackAPrime.DrawDisabled;
                if (!_raisePending) { _raisePending = true; _raiseWait = 0; _raiseDrawn = 0; }
                FallbackAPrime.DrawDisabled = false;
                if (wasOff && r.Se != IntPtr.Zero) return; // the frame just presented was not drawn
                _raiseWait++;
                if (drawn) _raiseDrawn++;
                bool enough = _raiseDrawn >= RaiseAfterDrawnPresents;
                if (enough || _raiseWait > RaiseTimeoutPresents || r.Se == IntPtr.Zero)
                {
                    // S7t T1 (A′ ramp): raised at the ramp's alpha (0 at its start), then faded in by the next frames
                    if (fadeA) SetLayered(layered, r.Se, CrossfadeRules.Alpha(_fadeP));
                    bool ok = r.Se != IntPtr.Zero && _ops.SetPos(r.Se, NativeWin.HWND_TOP, NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE);
                    _fb.Lowered = false;
                    _raisePending = false;
                    Log("DE.Display A' raised SE (HWND_TOP " + (ok ? "ok" : "FAILED") + (enough ? " after " + _raiseDrawn + " drawn presents" : ", " + _raiseDrawn + " drawn frame(s) within " + RaiseTimeoutPresents + " presents") +
                        "); label=" + (r.Label ?? "-") + (fadeA ? "; S7t ramp from alpha " + _layeredAlpha : ""));
                }
            }
            else if (!_fb.Lowered)
            {
                FallbackAPrime.DrawDisabled = false;
                // S7t T1 (A′ ramp): on top, SE's window follows the ramp until it rests at SE's picture
                if (fadeA && _fadeP > 0.0) SetLayered(layered, r.Se, CrossfadeRules.Alpha(_fadeP));
                else if (_layeredOn) ClearLayered(layered, fadeA ? "the ramp rests at SE's picture" : "the ramp is off");
            }
            if (_layeredOn && r.Mode != DisplayDesign.APrime) ClearLayered(layered, "design " + S2Config.Name(r.Mode));
        }

        /// <summary>S7t T1 (A′ ramp): SE's window constant alpha (layered on the first call); a failed call clears the style.</summary>
        private void SetLayered(ILayeredWindowOps layered, IntPtr se, double alpha)
        {
            if (layered == null || se == IntPtr.Zero) return;
            double a = alpha < 0.0 ? 0.0 : (alpha > 1.0 ? 1.0 : alpha);
            byte b = (byte)Math.Round(a * 255.0);
            if (_layeredOn && b == _layeredAlpha && se == _layeredHwnd) return;
            bool first = !_layeredOn;
            _fb.LayeredCalls++;
            if (!layered.SetLayeredAlpha(se, b))
            {
                Log("DE.Display A' S7t ramp: SetLayeredWindowAttributes FAILED (alpha " + b + "); the window stays opaque (the cut) until the ramp is off again");
                layered.ClearLayered(se);
                _layeredFailed = true;
                _layeredOn = false;
                _fb.Layered = false;
                return;
            }
            _layeredOn = true;
            _layeredHwnd = se;
            _layeredAlpha = b;
            _fb.Layered = true;
            if (first) Log("DE.Display A' S7t ramp: SE's window layered (WS_EX_LAYERED, LWA_ALPHA " + b + ")");
        }

        private void ClearLayered(ILayeredWindowOps layered, string why)
        {
            if (!_layeredOn) return;
            _layeredOn = false;
            _layeredAlpha = 255;
            _fb.Layered = false;
            _fb.LayeredCalls++;
            bool ok = layered != null && _layeredHwnd != IntPtr.Zero && layered.ClearLayered(_layeredHwnd);
            Log("DE.Display A' S7t ramp: layered style removed (" + why + ")" + (ok ? "" : " (call FAILED)"));
        }

        // ---------------------------------------------------------------------------------------------- TopMost

        private void TopmostStep(DisplayRequest r)
        {
            if (r.Linked && r.Se != IntPtr.Zero && !_topHandled)
            {
                _seenLink = true;
                _topHandled = true;
                _topHwnd = r.Se;
                _wasTopmost = _ops.IsTopmost(r.Se);
                if (_wasTopmost)
                {
                    bool ok = _ops.SetTopmost(r.Se, false);
                    Log("DE.Display linked: SE was TopMost (fullscreen-window mode) -> HWND_NOTOPMOST " + (ok ? "ok" : "FAILED") + " (restored on unlink)");
                }
                else Log("DE.Display linked: SE not TopMost (nothing to undo)");
            }
            else if ((!r.Linked || r.Se != _topHwnd) && _topHandled)
            {
                _topHandled = false;
                IntPtr h = _topHwnd;
                if (_wasTopmost && h != IntPtr.Zero && _ops.IsWindow(h) && _ops.Foreground() == h)
                {
                    bool ok = _ops.SetTopmost(h, true);
                    Log("DE.Display unlinked: TopMost restored (HWND_TOPMOST " + (ok ? "ok" : "FAILED") + ")");
                }
                else if (_wasTopmost)
                    Log("DE.Display unlinked: TopMost not restored (SE is not the foreground window; SE itself drops TopMost on deactivation)");
                _wasTopmost = false;
                _fb.ZOrderOk = false;
            }
        }

        // ------------------------------------------------------------------------------------------- foreground

        private void ForegroundStep(DisplayRequest r)
        {
            if (r.Se == IntPtr.Zero) return;
            if (r.ForegroundSeq != _fgSeqDone)
            {
                _fgSeqDone = r.ForegroundSeq;
                _fgAttempts = 0;
                _fb.ForegroundFailed = false;
                ForegroundAttempt(r);
            }
            else if (_fgPending && _fb.Presents >= _fgCheckAt)
            {
                bool fg = _ops.Foreground() == r.Se;
                if (fg)
                {
                    _fgPending = false;
                    Log("DE.Display foreground OK: GetForegroundWindow() == seHwnd after attempt " + _fgAttempts);
                }
                else if (_fgAttempts < FgMaxAttempts) ForegroundAttempt(r);
                else
                {
                    _fgPending = false;
                    _fb.ForegroundFailed = true;
                    Log("DE.Display foreground FAILED after " + _fgAttempts + " attempts: foreground is " + NativeWin.Hex(_ops.Foreground()) + " (HUD hint)");
                }
            }
            if (_fb.Presents % ForegroundEveryPresents == 0 || _fgPending) _fb.Foreground = _ops.Foreground() == r.Se;
        }

        private void ForegroundAttempt(DisplayRequest r)
        {
            _fgAttempts++;
            bool ok = _ops.SetForeground(r.Se);
            _fgPending = true;
            _fgCheckAt = _fb.Presents + (_fgAttempts == 1 ? 6 : 30);
            Log("DE.Display SetForegroundWindow(seHwnd " + NativeWin.Hex(r.Se) + ") attempt " + _fgAttempts + " returned " + ok);
        }

        // ---------------------------------------------------------------------------------------------- z-order

        private void ZOrderStep(DisplayRequest r, long now)
        {
            bool canAssert = r.Linked && r.Se != IntPtr.Zero && r.Sn != IntPtr.Zero && !_fb.SnWindowLost;
            if (!canAssert)
            {
                _zPending = false;
                _fb.ZPending = false;
                if (_fb.ZOrderOk) { _fb.ZOrderOk = false; }
                if (r.ZSeq != _zSeqDone) _zSeqDone = r.ZSeq; // nothing to do now; a later trigger asks again
                return;
            }
            // A′ lowered: the wanted order is SE directly under Subnautica
            bool inverted = _fb.Lowered;
            if (inverted != _fb.ZInverted)
            {
                _fb.ZInverted = inverted;
                _walkEverLogged = false; // the next walk logs the order it now checks
            }
            IntPtr top = inverted ? r.Sn : r.Se, below = inverted ? r.Se : r.Sn;
            string order = inverted ? " [A' lowered: SE directly under Subnautica]" : "";
            if (r.ZSeq != _zSeqDone)
            {
                _zSeqDone = r.ZSeq;
                _zAttempts = 0;
                _zWhy = r.ZWhy;
                ZAssert(r, now);
            }
            else if (_zPending && ZVerifyDue(r, now))
            {
                WalkResult w = _ops.Walk(top, below);
                _fb.Walks++;
                string waited = " (verified " + Ms(now - _zAssertQpc).ToString("0", CultureInfo.InvariantCulture) + " ms after the assert, snFrame +" +
                                (r.SnFrame >= _zSnFrameAtAssert && _zSnFrameAtAssert != 0 ? (r.SnFrame - _zSnFrameAtAssert).ToString(CultureInfo.InvariantCulture) : "?") + ")";
                if (w.Ok)
                {
                    _zPending = false;
                    _fb.ZOrderOk = true;
                    _walkLoggedOk = true;
                    _walkEverLogged = true;
                    _fb.LastWalk = w.Describe(below, true) + order;
                    Log("DE.ZOrder OK after attempt " + _zAttempts + " (" + (_zWhy ?? "-") + "): " + _fb.LastWalk + waited);
                }
                else if (_zAttempts < ZMaxAttempts)
                {
                    _fb.LastWalk = w.Describe(below, true) + order;
                    Log("DE.ZOrder retry: attempt " + _zAttempts + " not verified: " + _fb.LastWalk + waited);
                    ZAssert(r, now);
                }
                else
                {
                    _zPending = false;
                    _fb.ZOrderOk = false;
                    _walkLoggedOk = false;
                    _walkEverLogged = true;
                    _fb.LastWalk = w.Describe(below, true) + order;
                    Log("DE.ZOrder FAIL after " + _zAttempts + " attempts (" + (_zWhy ?? "-") + "): " + _fb.LastWalk + waited + " (zOrderOk 0: no Underwater)");
                }
            }
            else if (!_zPending && (r.WalkEveryFrame || _fb.Presents % SlowWalkEveryPresents == 0))
            {
                WalkResult w = _ops.Walk(top, below, _walkSkipped, _walkSkippedWhy);
                _fb.Walks++;
                if (w.Ok != _fb.ZOrderOk || !_walkEverLogged || w.Ok != _walkLoggedOk)
                {
                    _fb.LastWalk = w.Describe(below, true) + order;
                    Log("DE.ZOrder " + (w.Ok ? "OK" : "LOST") + " (walker, " + (r.Label ?? "-") + "): " + _fb.LastWalk);
                    _walkLoggedOk = w.Ok;
                    _walkEverLogged = true;
                }
                _fb.ZOrderOk = w.Ok;
                // Prime: the Prime → Underwater guard needs zOrderOk; A′ lowered: SE re-lowers itself under Subnautica
                if (!w.Ok && (r.AutoReassert || inverted) && Ms(now - _lastAutoZQpc) >= AutoReassertIntervalMs)
                {
                    _lastAutoZQpc = now;
                    _zAttempts = 0;
                    _zWhy = inverted ? "A' lowered: order lost, SE re-lowers itself" : "Prime: zOrderOk lost, automatic re-assert";
                    ZAssert(r, now);
                }
            }
            _fb.ZPending = _zPending;
            _fb.ZAttempts = _zAttempts;
        }

        /// <summary>
 /// Review fix (finding 6): an asynchronous re-assert is executed by Subnautica's message pump at its
 /// own frame rate (10 fps in Idle/Track), so the walker waits for at least <see cref="ZVerifyAfterPresents"/> SE
 /// presents and either <see cref="ZVerifyAfterSnFrames"/> new Subnautica frames (C7 snFrame) or
 /// <see cref="ZVerifyMaxWaitMs"/>. SE's own synchronous re-lowering (A′) is verified after the presents alone.
 /// </summary>
        private bool ZVerifyDue(DisplayRequest r, long now)
        {
            if (_fb.Presents < _zVerifyAt) return false;
            if (_zSyncAssert) return true;
            if (_zSnFrameAtAssert != 0 && r.SnFrame >= _zSnFrameAtAssert + ZVerifyAfterSnFrames) return true;
            return Ms(now - _zAssertQpc) >= ZVerifyMaxWaitMs;
        }

        private void ZAssert(DisplayRequest r, long now)
        {
            _zAttempts++;
            _zPending = true;
            _zAssertQpc = now;
            _zSnFrameAtAssert = r.SnFrame;
            _zVerifyAt = _fb.Presents + ZVerifyAfterPresents;
            if (_fb.Lowered)
            {
                // A′ lowered: SE moves its own window directly under Subnautica's (synchronous; no foreground right needed)
                _zSyncAssert = true;
                bool low = _ops.SetPos(r.Se, r.Sn, NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE);
                Log("DE.ZOrder assert attempt " + _zAttempts + " (" + (_zWhy ?? "-") + "): A' lowered: SetWindowPos(seHwnd " + NativeWin.Hex(r.Se) +
                    ", snHwnd " + NativeWin.Hex(r.Sn) + ")=" + low + " (SE directly under Subnautica)");
                return;
            }
            _zSyncAssert = false;
            int err = 0;
            bool allow = r.SnPid != 0 && _ops.AllowSetForeground(r.SnPid, out err);
            bool pos = _ops.SetPos(r.Sn, r.Se, NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE | NativeWin.SWP_ASYNCWINDOWPOS);
            Log("DE.ZOrder assert attempt " + _zAttempts + " (" + (_zWhy ?? "-") + "): AllowSetForegroundWindow(snPid " + r.SnPid + ")=" + allow +
                (allow ? "" : " err=" + err) + " SetWindowPos(snHwnd " + NativeWin.Hex(r.Sn) + ", seHwnd " + NativeWin.Hex(r.Se) + ", ASYNCWINDOWPOS)=" + pos +
                " snFrame=" + r.SnFrame);
        }

        // ------------------------------------------------------------------------------------------------ probe

        private void ProbeStep(DisplayRequest r, long now)
        {
            if (r.ProbeSeq != _probeSeqDone && !_probeActive)
            {
                _probeSeqDone = r.ProbeSeq;
                _probe = r.Probe;
                if (r.Mode != DisplayDesign.F || !r.Linked || r.Se == IntPtr.Zero)
                {
                    PublishProbe(-1, "probe refused: needs design F, the link and seHwnd");
                    return;
                }
                _probeActive = true;
                _probeStartQpc = now;
                _probeSkippedAtStart = _fb.SkippedPresents;
                _probeReads = 0;
                _probeRead1 = null;
                _probeWorkerDone = false;
                _probeColours = null;
                _fb.ProbeActive = true;
                Log("DE.Display probe START (composed-pixel measurement: margins on, one see-through moment, two GDI GetPixel reads: the first after " +
                    ProbeSkippedPresentsBeforeRead + " skipped presents and " + ProbeMinWaitMs.ToString("0", CultureInfo.InvariantCulture) + " ms, the second " +
                    ProbeSecondReadAfterMs.ToString("0", CultureInfo.InvariantCulture) + " ms later)");
            }
            if (!_probeActive) return;
            // review fix (finding 4): Present returning does not mean composed, so
 // the first read waits for 3 + 3 skipped presents and 100 ms; a second read 100 ms and 3 skipped presents later
            if (_probeReads == 0 && _fb.SkippedPresents - _probeSkippedAtStart >= ProbeSkippedPresentsBeforeRead && Ms(now - _probeStartQpc) >= ProbeMinWaitMs)
            {
                BuildProbePoints(r);
                _probeRead1Qpc = now;
                _probeSkippedAtRead1 = _fb.SkippedPresents;
                StartProbeRead();
            }
            else if (_probeReads == 1 && _probeWorkerDone && _fb.SkippedPresents - _probeSkippedAtRead1 >= ProbeSecondReadAfterSkipped &&
                     Ms(now - _probeRead1Qpc) >= ProbeSecondReadAfterMs)
            {
                _probeRead1 = _probeColours;
                StartProbeRead();
            }
            bool timeout = Ms(now - _probeStartQpc) > ProbeTimeoutMs;
            if ((_probeReads == 2 && _probeWorkerDone) || timeout)
            {
                _probeActive = false;
                _fb.ProbeActive = false;
                uint[] c2 = _probeReads == 2 && _probeWorkerDone ? _probeColours : null;
                uint[] c1 = _probeReads == 2 ? _probeRead1 : (_probeReads == 1 && _probeWorkerDone ? _probeColours : null);
                if (c1 == null && c2 == null)
                {
                    PublishProbe(-1, timeout ? "probe TIMEOUT (" + (_probeReads > 0 ? "GDI read did not finish" : "no see-through frame presented") + ")" : "probe GDI read failed");
                    return;
                }
                if (c1 == null || c2 == null)
                {
                    int v1;
                    string t1 = ProbeVerdict(_probe, _probeXs, _probeYs, c1 ?? c2, out v1);
                    PublishProbe(-1, "UNDECIDED (one read only" + (timeout ? ", timeout" : "") + "): read: " + t1);
                    return;
                }
                int verdict;
                string text = CombinedVerdict(_probe, _probeXs, _probeYs, c1, c2, out verdict);
                PublishProbe(verdict, text);
            }
        }

        private void StartProbeRead()
        {
            _probeReads++;
            _probeWorkerDone = false;
            _probeColours = null;
            int[] xs = _probeXs, ys = _probeYs;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _probeColours = _ops.ReadScreenPixels(xs, ys); }
                catch (Exception) { _probeColours = null; }
                _probeWorkerDone = true;
            });
        }

        /// <summary>
 /// Two reads of one probe: HONOURED or NOT HONOURED only when both reads say so; otherwise UNDECIDED (−1), which
 /// never switches the design.
 /// </summary>
        public static string CombinedVerdict(ProbeSpec p, int[] xs, int[] ys, uint[] read1, uint[] read2, out int verdict)
        {
            int v1, v2;
            string t1 = ProbeVerdict(p, xs, ys, read1 ?? new uint[0], out v1);
            string t2 = ProbeVerdict(p, xs, ys, read2 ?? new uint[0], out v2);
            if (read1 != null && read2 != null && v1 == v2 && v1 != -1)
            {
                verdict = v1;
                return t1 + " | read 2: " + t2;
            }
            verdict = -1;
            if (!p.HasExpected) return t1 + " | read 2: " + t2;
            return "UNDECIDED (the two reads disagree or are mixed) read 1: " + t1 + " | read 2: " + t2;
        }

        private void BuildProbePoints(DisplayRequest r)
        {
            if (_probe.Xs != null && _probe.Ys != null && _probe.Xs.Length == _probe.Ys.Length && _probe.Xs.Length > 0)
            {
                _probeXs = _probe.Xs;
                _probeYs = _probe.Ys;
                return;
            }
            I32x4 rc = _fb.SeRect;
            if (!_fb.SeRectValid) _ops.GetRect(r.Se, out rc);
            int w = rc.z - rc.x, h = rc.w - rc.y;
            _probeXs = new[] { rc.x + w / 2, rc.x + w / 4, rc.x + 3 * w / 4, rc.x + w / 4, rc.x + 3 * w / 4 };
            _probeYs = new[] { rc.y + h / 2, rc.y + h / 4, rc.y + h / 4, rc.y + 3 * h / 4, rc.y + 3 * h / 4 };
        }

        /// <summary>The probe verdict: honoured when at least 3 of 5 points (a majority) show the expected colour within the tolerance.</summary>
        public static string ProbeVerdict(ProbeSpec p, int[] xs, int[] ys, uint[] colours, out int verdict)
        {
            var sb = new System.Text.StringBuilder(200);
            int match = 0, black = 0, n = Math.Min(colours.Length, Math.Min(xs.Length, ys.Length));
            int tol = p.Tolerance > 0 ? p.Tolerance : 24;
            sb.Append("points:");
            for (int i = 0; i < n; i++)
            {
                uint c = colours[i];
                int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
                bool invalid = c == NativeWin.CLR_INVALID;
                sb.Append(" (").Append(xs[i]).Append(',').Append(ys[i]).Append(")=");
                if (invalid) { sb.Append("invalid"); continue; }
                sb.Append(r).Append('/').Append(g).Append('/').Append(b);
                if (r == 0 && g == 0 && b == 0) black++;
                if (p.HasExpected && Math.Abs(r - p.R) <= tol && Math.Abs(g - p.G) <= tol && Math.Abs(b - p.B) <= tol) match++;
            }
            if (p.HasExpected)
            {
                int need = n / 2 + 1;
                verdict = match >= need ? 1 : match == 0 ? 0 : -1;
                sb.Insert(0, (verdict == 1 ? "HONOURED" : verdict == 0 ? "NOT HONOURED" : "MIXED") + " expected " + p.R + "/" + p.G + "/" + p.B +
                             " +-" + tol + ": " + match + "/" + n + " match; ");
            }
            else
            {
                verdict = -1;
                sb.Insert(0, "MEASURED (no expected colour)" + (black == n && n > 0 ? " all black: DWM alpha probably NOT honoured" : "") + "; ");
            }
            return sb.ToString();
        }

        private void PublishProbe(int verdict, string text)
        {
            _fb.ProbeDone++;
            _fb.ProbeVerdict = verdict;
            _fb.ProbeHadExpected = _probe.HasExpected;
            _fb.ProbeResult = text;
            Log("DE.Display probe " + text);
        }

        // -------------------------------------------------------------------------------------------- utilities

        private void Publish()
        {
            lock (_lock) _published = _fb;
        }

        private void Log(string line)
        {
            if (Interlocked.Increment(ref _queued) > MaxQueuedLogLines) { Interlocked.Decrement(ref _queued); return; }
            _log.Enqueue(line);
        }

        private void Fail(string where, Exception e)
        {
            Errors++;
            if (Errors <= 5) Log("DE.Error render " + where + " #" + Errors + " " + e.GetType().Name + ": " + e.Message);
        }

        private double Ms(long qpcDelta) { return qpcDelta * _qpcToMs; }

        private static string Pct(double f) { return (f * 100.0).ToString("0.###", CultureInfo.InvariantCulture); }

        /// <summary>Tests and the de.display state dump.</summary>
        public bool SeenLink { get { return _seenLink; } }
    }

    /// <summary>The real window calls (user32, dwmapi, gdi32). Render thread, except <see cref="ReadScreenPixels"/>.</summary>
    internal class NativeWindowOps : IWindowOps, ILayeredWindowOps
    {
        // ---- S7t T1 (A′ ramp): SE's own window's constant alpha (user32 SetWindowLongPtrW / SetLayeredWindowAttributes)
        private const long WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;

        [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colourKey, byte alpha, uint flags);

        public bool SetLayeredAlpha(IntPtr h, byte alpha)
        {
            long ex = NativeWin.GetWindowLongPtr(h, NativeWin.GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtr(h, NativeWin.GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            return SetLayeredWindowAttributes(h, 0u, alpha, LWA_ALPHA);
        }

        public bool ClearLayered(IntPtr h)
        {
            long ex = NativeWin.GetWindowLongPtr(h, NativeWin.GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_LAYERED) == 0) return true;
            SetLayeredWindowAttributes(h, 0u, 255, LWA_ALPHA);
            SetWindowLongPtr(h, NativeWin.GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
            return true;
        }

        public bool IsWindow(IntPtr h) { return NativeWin.IsWindow(h); }
        public bool IsWindowVisible(IntPtr h) { return NativeWin.IsWindowVisible(h); }

        public int ExtendFrame(IntPtr h, bool sheetOfGlass)
        {
            int v = sheetOfGlass ? -1 : 0;
            var m = new NativeWin.MARGINS { Left = v, Right = v, Top = v, Bottom = v };
            return NativeWin.DwmExtendFrameIntoClientArea(h, ref m);
        }

        public bool SetPos(IntPtr h, IntPtr insertAfter, uint flags) { return NativeWin.SetWindowPos(h, insertAfter, 0, 0, 0, 0, flags); }

        public bool AllowSetForeground(uint pid, out int error) { return DeepEngineers.Wire.Native.Win32.AllowSetForegroundWindow(pid, out error); }

        public bool SetForeground(IntPtr h) { return NativeWin.SetForegroundWindow(h); }
        public IntPtr Foreground() { return NativeWin.GetForegroundWindow(); }

        public bool IsTopmost(IntPtr h) { return (NativeWin.GetWindowLongPtr(h, NativeWin.GWL_EXSTYLE).ToInt64() & NativeWin.WS_EX_TOPMOST) != 0; }

        public virtual bool SetTopmost(IntPtr h, bool topmost)
        {
            return NativeWin.SetWindowPos(h, topmost ? NativeWin.HWND_TOPMOST : NativeWin.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeWin.SWP_NOMOVE | NativeWin.SWP_NOSIZE | NativeWin.SWP_NOACTIVATE);
        }

        public WalkResult Walk(IntPtr se, IntPtr sn) { return ZOrderWalker.WalkLive(se, sn); }

        public WalkResult Walk(IntPtr se, IntPtr sn, IntPtr[] skipped, string[] why) { return ZOrderWalker.WalkLive(se, sn, skipped, why); }

        public bool GetRect(IntPtr h, out I32x4 rect)
        {
            NativeWin.RECT r;
            bool ok = NativeWin.GetWindowRect(h, out r);
            rect = new I32x4(r.Left, r.Top, r.Right, r.Bottom);
            return ok;
        }

        public uint[] ReadScreenPixels(int[] xs, int[] ys)
        {
            IntPtr dc = NativeWin.GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                var c = new uint[Math.Min(xs.Length, ys.Length)];
                for (int i = 0; i < c.Length; i++) c[i] = NativeWin.GetPixel(dc, xs[i], ys[i]);
                return c;
            }
            finally { NativeWin.ReleaseDC(IntPtr.Zero, dc); }
        }

        public long Qpc() { return Stopwatch.GetTimestamp(); }
    }

    internal static class WindowOpsExtensions
    {
        /// <summary>The allocation-free walk when the implementation offers it.</summary>
        public static WalkResult Walk(this IWindowOps ops, IntPtr se, IntPtr sn, IntPtr[] skipped, string[] why)
        {
            var n = ops as NativeWindowOps;
            return n != null ? n.Walk(se, sn, skipped, why) : ops.Walk(se, sn);
        }
    }
}
