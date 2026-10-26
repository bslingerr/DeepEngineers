using System;
using System.Diagnostics;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>The link facts the driver reads (taken from <see cref="Link"/> at each plugin Update).</summary>
    internal struct LinkFacts
    {
        /// <summary>Link up and the peer not exited (window set-up allowed).</summary>
        public bool Linked;
        public bool PeerAlive;
        /// <summary>Wire.PeerState.</summary>
        public uint Peer;
        /// <summary>C0 snPhase.</summary>
        public uint SnPhase;
        public bool HaveC7;
        public SnStatus C7;
        /// <summary>New Subnautica generations seen (Link.NewPeers) and the link epoch: their changes are machine events.</summary>
        public long NewPeers;
        public uint LinkEpoch;
        public uint HelloEpoch;
        public IntPtr SnHwnd;
        public uint SnPid;

        /// <summary>The hello is published and acknowledged (C7 helloAckEpoch = C5 helloEpoch ≠ 0).</summary>
        public bool HelloAcked { get { return HelloEpoch != 0 && HaveC7 && C7.helloAckEpoch == HelloEpoch; } }
    }

    /// <summary>One SE tick of the camera, from the C1 composition (<c>FrameWriter</c>, PrepareForDraw postfix).</summary>
    internal struct FrameFacts
    {
        public ulong Tick;
        /// <summary>F(Pcam) in SN metres (doubles).</summary>
        public Vec3 CamSn;
        /// <summary>The SE camera position (doubles), for the Water Mod depth cross-check.</summary>
        public Vec3 CamSe;
        /// <summary>d(F(Pcam))/dt in SN m/s (C5 camVelSn).</summary>
        public Vec3 CamVelSn;
        /// <summary>C1 teleported (a command) or a detected jump (&gt; 20 m or &gt; 45° in one tick).</summary>
        public bool Teleported;
        /// <summary>C5 zone (Wire.Zone, the S1 ZoneTracker with its 7,800/7,900 m hysteresis).</summary>
        public uint Zone;
    }

    /// <summary>The game facts of this update (update thread, plugin Update).</summary>
    internal struct GameFacts
    {
        public bool Paused;
        public bool Foreground;
        public bool GuiScreenOpen;
        public bool CursorOrChat;
        public bool Loading;
        /// <summary>!Config.EnableMuteWhenNotInFocus.</summary>
        public bool KeepAudibleUnfocused;
    }

    /// <summary>
 /// The update-thread driver of Wire's view state machine (ARCHITECTURE §4: "evaluated once per SE tick in the
 /// PrepareForDraw postfix"), game-independent: it fills <see cref="ViewInputs"/> from the camera (y from the map,
 /// never from the Water Mod), the link (C0/C7, peer state), the game (pause, focus, GUI, loading) and the render
 /// feedback (<see cref="RenderAck"/> over the render thread's counters), raises the machine's events (a teleport that
 /// moved the camera more than 20 m, a new Subnautica generation, a link recovery, a world load), steps it through
 /// <see cref="ViewStateAdapter"/>, writes <c>vsm</c> trace rows (<see cref="ViewTrace"/>: every step around a
 /// transition, one per second otherwise, every step with <see cref="TraceAll"/>), and cross-checks y against
 /// <c>WaterModAPI.GetDepth</c> every 60 ticks (a mismatch above 0.05 m is logged at most once per minute).
 /// <para>
 /// Band-hold diagnostic (re-dive fix
 /// "refuses to switch until I get out of the water and dive again". HYPOTHESIS: the camera sits inside the waterline
 /// hysteresis band, below the Surfacing threshold (<c>y ≤ −0.05 m</c>) but above the entry threshold
 /// (<c>y &gt; −0.20 m</c>), so Prime never enters Underwater. When the machine is in Prime with
 /// <c>−0.20 &lt; y ≤ −0.05 m</c> for more than <see cref="BandHoldLogAfterUs"/> (1 s), one line
 /// <c>DE.View band hold #N: …</c> is logged with y, vy, the blocked guards and the time held, then one every
 /// <see cref="BandHoldRepeatUs"/> (5 s) while it lasts, and one <c>DE.View band hold #N ended …</c> line; the count
 /// is <see cref="BandHolds"/> (in the <c>de.view</c> and <c>de.state</c> replies).
 /// </para>
 /// <para>
 /// S7a picture policy: <see cref="ViewInputs.Picture"/> comes from <see cref="PictureSource"/> (default
 /// <see cref="PicturePolicy.Current"/>: env <c>DEEPENGINEERS_PICTURE</c>, <c>de.s7 picture</c>); the seeded failure
 /// S7a.M1 ORs <see cref="ViewMutants.S7aM1Waterline"/> into the mutant bits. Every view or picture-latch change (and
 /// once per second outside Space, and on a policy change) writes one <c>s7a.view</c> row to <see cref="ViewRowSink"/>
 /// {tick, view, approach, policy, picture, edge, reason, pictureReason, blocked, pictureBlocked, bandTick, y, vy, rho,
 /// zone, snReady, request, renderSeq} and a latch change logs one <c>DE.View picture …</c> line.
 /// </para>
 /// </summary>
    internal sealed class ViewStateDriver
    {
        public const int DepthCheckEveryTicks = 60;
        public const double DepthMismatchM = 0.05;
        public const double DepthLogIntervalMs = 60000;
        /// <summary>A teleport flag is a machine event only when the camera moved more than this (lesson 6: the SE
 /// teleport latch carries C1 teleported on two frames, and a look command arms it without a move).</summary>
        public const double TeleportMoveM = 20.0;
        /// <summary>

 /// for Subnautica's streamingSettled) only beyond this distance — a relocation. Cockpit seats, first/third person
 /// and the ship's third-person camera jump tens of metres inside the resident area; they keep the camera-box check
 /// (areaBuilt) and nothing else.
 /// </summary>
        public const double ResettleJumpM = 150.0;
        public long LocalJumps, ResettleJumps;
        private readonly RateLimit _localJumpLog = new RateLimit(5000);
        public const int TraceRingSteps = 30;
        public const int TraceAfterTransitionSteps = 120;
        public const int TraceEverySteps = 60;
        /// <summary>Band hold: logged once Prime has held y inside (−0.20, −0.05] m for longer than this.</summary>
        public const long BandHoldLogAfterUs = 1000000;
        /// <summary>Band hold: the line repeats every 5 s while the hold lasts.</summary>
        public const long BandHoldRepeatUs = 5000000;

        private ViewMachineState _state;
        private RenderAck _ack;
        private bool _hasPrevCam;
        private Vec3 _prevCamSn;
        private bool _linkSeen;
        private long _lastNewPeers;
        private uint _lastLinkEpoch, _lastHelloEpoch;
        private bool _worldLoadPending = true;
        private int _depthCountdown = DepthCheckEveryTicks;
        private double _lastDepthLogMs = double.NegativeInfinity;
        private ulong _crossingTickSeen;
        private double _crossingAtMs;
        private uint _lastPrimeBlockLogged = uint.MaxValue;
        private bool _stepped;
        // band hold
        private bool _bandIn, _bandLogged;
        private long _bandSinceUs, _bandNextLogUs;

        // trace ring (pre-state, inputs, outputs of the last steps; formatted only when written)
        private readonly ViewMachineState[] _ringPre = new ViewMachineState[TraceRingSteps];
        private readonly ViewInputs[] _ringIn = new ViewInputs[TraceRingSteps];
        private readonly ViewOutputs[] _ringOut = new ViewOutputs[TraceRingSteps];
        private int _ringNext, _ringCount;
        private int _traceAfter;
        private readonly char[] _traceBuf = new char[ViewTrace.MaxRowChars];

        /// <summary>Unscaled clock, µs (default QPC). Tests replace it.</summary>
        public Func<long> ClockUs = () => (long)(Stopwatch.GetTimestamp() * (1000000.0 / Stopwatch.Frequency));
        /// <summary>Water Mod GetDepth at an SE point (negative below the surface), or null (game glue).</summary>
        public Func<Vec3, float?> DepthAt;
        public Action<string> Log = s => { };
        /// <summary>Where <c>vsm</c> rows go (the plugin: buffered JSONL); null = no trace.</summary>
        public Action<string> TraceSink;
        /// <summary>S7a: where <c>s7a.view</c> rows go (the plugin: buffered JSONL); null = none.</summary>
        public Action<string> ViewRowSink;
        /// <summary>S7a: the picture policy input of each step (default: <see cref="PicturePolicy.Current"/>; tests replace it).</summary>
        public Func<uint> PictureSource = () => PicturePolicy.Current;
        /// <summary>S7a: <c>s7a.view</c> rows written, picture edges seen.</summary>
        public long ViewRows { get; private set; }
        public long PictureEdges { get; private set; }
        public const long ViewRowEveryUs = 1000000;
        private long _nextViewRowUs;
        private uint _lastPolicy = uint.MaxValue;
        /// <summary>Dev: one row per step (<c>de.view trace=on</c>).</summary>
        public bool TraceAll { get; set; }

        public ViewOutputs Last { get; private set; }
        /// <summary>The machine stepped since the last <see cref="NoFrame"/> (in world, site bound).</summary>
        public bool Active { get { return _stepped; } }
        public ViewInputs LastInputs { get; private set; }
        public ViewMachineState MachineState { get { return _state; } }
        public S2State State { get { return ViewStateAdapter.StateOf(Last.ViewState, Last.ApproachMode); } }
        public bool SnReady { get { return Last.SnReady; } }
        public string SnReadyWhy { get { return ViewStateAdapter.BlockedNames(Last.Blocked & ViewBlock.SnReadyMask); } }
        public double Y { get; private set; }
        public double Vy { get; private set; }
        public double RhoInf { get; private set; }
        public ulong Tick { get; private set; }
        public long Transitions { get; private set; }
        public long DepthChecks { get; private set; }
        public double DepthWorstM { get; private set; }
        public long TraceRows { get; private set; }
        /// <summary>Band holds logged since the plugin started (Prime with −0.20 &lt; y ≤ −0.05 m for more than 1 s).</summary>
        public long BandHolds { get; private set; }
        /// <summary>The longest band hold so far (s; holds of 1 s or less are not counted).</summary>
        public double BandHoldMaxS { get; private set; }
        /// <summary>A band hold is being logged now.</summary>
        public bool InBandHold { get { return _bandLogged; } }
        /// <summary>The forced state of <c>de.view force</c> (null = automatic).</summary>
        public S2State? Forced { get; set; }

        public ViewStateDriver()
        {
            Last = SpaceOutputs();
        }

        /// <summary>Space outputs for ticks without a step (not in world, site not bound).</summary>
        public static ViewOutputs SpaceOutputs()
        {
            var o = new ViewOutputs
            {
                ViewState = ViewState.Space,
                ApproachMode = ApproachMode.None,
                PerfMode = PerfMode.Idle,
                TargetFps = ViewThresholds.IdleTargetFps,
                CrossfadeMs = AudioMix.CrossfadeMs,
                SeGameFactor = 1f,
                SeMusicFactor = 1f,
                WaterModFactor = 1f,
                Freeze = true,
                // S7a: the selected policy (C5 bit 9 follows it even before the first step)
                PicturePolicy = ViewStateMachine.EffectivePicture(PicturePolicy.Current, S7aPolicyMutants.MachineWaterline ? ViewMutants.S7aM1Waterline : 0u),
            };
            return o;
        }

        /// <summary>Plugin Update without a camera frame this tick (not in world, site not bound): Space; the next frame is a world load.</summary>
        public void NoFrame(string why)
        {
            if (_stepped && State != S2State.Space) Log("DE.View Space (" + why + "; the machine restarts with a world-load event)");
            _stepped = false;
            _worldLoadPending = true;
            _hasPrevCam = false;
            if (_bandLogged) Log("DE.View band hold #" + BandHolds + " ended (" + why + ")");
            _bandIn = false;
            _bandLogged = false;
            ViewOutputs o = SpaceOutputs();
            // keep the audio ramp and the render request moving back to rest
            o.Request = 0;
            Last = o;
        }

        /// <summary>One machine step on this tick's final camera (the PrepareForDraw postfix).</summary>
        public ViewOutputs Step(ref FrameFacts f, ref LinkFacts l, ref GameFacts g, ref DisplayFeedback fb, bool bridgeUp, DisplayDesign mode, uint mutants)
        {
            long now = ClockUs();
            Tick = f.Tick;
            double y = f.CamSn.Y;
            double rho = SiteZones.RhoInf(f.CamSn.X, f.CamSn.Z);
            Y = y; RhoInf = rho; Vy = f.CamVelSn.Y;

            uint ev = Events(ref f, ref l);
            var i = new ViewInputs
            {
                Tick = f.Tick,
                NowUs = now,
                BridgeUp = bridgeUp,
                OnSite = f.Zone != Zone.OffSite,
                Zone = f.Zone,
                RhoInfM = rho,
                Y = y,
                Vy = f.CamVelSn.Y,
                Peer = l.Peer,
                SnPhase = l.SnPhase,
                SnFlags = l.HaveC7 ? l.C7.flags : 0u,
                PresentedSeTick = l.HaveC7 ? l.C7.presentedSeTick : 0UL,
                AreaBuiltSeTick = l.HaveC7 ? l.C7.areaBuiltSeTick : 0UL,
                ZOrderOk = fb.ZOrderOk,
                Paused = g.Paused,
                Foreground = g.Foreground,
                GuiScreenOpen = g.GuiScreenOpen,
                CursorOrChat = g.CursorOrChat,
                Loading = g.Loading,
                KeepAudibleUnfocused = g.KeepAudibleUnfocused,
                Events = ev,
                DisplayMode = ViewStateAdapter.DisplayModeOf(mode),
                // S7a.M1 (seeded failure): the machine runs Waterline while Site is selected
                Mutants = mutants | (S7aPolicyMutants.MachineWaterline ? ViewMutants.S7aM1Waterline : 0u),
                Picture = PictureSource != null ? PictureSource() : ViewPicture.Site,
            };
            i.RenderAckSeq = _ack.Update(_state.RenderSeq, _state.Request, fb.MarginsReady, fb.SceneDrawnPresents, fb.SkippedPresents);
            // review fix (finding 11): the live margins state, not latched by the acknowledgement
            i.MarginsReady = fb.MarginsReady;

            ViewMachineState pre = _state;
            ViewOutputs o;
            ViewStateAdapter.Step(ref _state, ref i, out o);
            _stepped = true;
            LastInputs = i;

            Trace(ref pre, ref i, ref o);
            LogStep(ref i, ref o, now, ev);
            PictureRow(ref i, ref o, now);
            DepthCheck(ref f, y, now);

            if (Forced.HasValue)
            {
                S2State s = Forced.Value;
                ViewStateAdapter.ToC5(s, out o.ViewState, out o.ApproachMode);
                ViewStateAdapter.Perf(s, g.Foreground, out o.PerfMode, out o.TargetFps);
                o.Request = ViewStateAdapter.ForcedRequest(s, mode, g.CursorOrChat || g.GuiScreenOpen || g.Paused);
                o.MarginsOn = (o.Request & ViewRequest.Margins) != 0;
                o.SceneSkip = (o.Request & ViewRequest.SceneSkip) != 0;
                o.SeLowered = (o.Request & ViewRequest.Lowered) != 0;
            }
            Last = o;
            return o;
        }

        private uint Events(ref FrameFacts f, ref LinkFacts l)
        {
            uint ev = 0;
            if (_worldLoadPending) { ev |= ViewEvents.WorldLoad; _worldLoadPending = false; }
            if (!_linkSeen)
            {
                _linkSeen = true;
                _lastNewPeers = l.NewPeers;
                _lastLinkEpoch = l.LinkEpoch;
                _lastHelloEpoch = l.HelloEpoch;
            }
            else
            {
                if (l.NewPeers != _lastNewPeers) { _lastNewPeers = l.NewPeers; ev |= ViewEvents.NewGeneration; }
                if (l.LinkEpoch != _lastLinkEpoch) { _lastLinkEpoch = l.LinkEpoch; ev |= ViewEvents.LinkRecovery; }
                if (l.HelloEpoch != _lastHelloEpoch) { _lastHelloEpoch = l.HelloEpoch; if (l.HelloEpoch != 0) ev |= ViewEvents.WorldLoad; }
            }
            if (f.Teleported && _hasPrevCam)
            {
                double jump = (f.CamSn - _prevCamSn).Length;
                if (jump > ResettleJumpM) { ev |= ViewEvents.Teleport; ResettleJumps++; }
                else if (jump > TeleportMoveM)
                {
                    LocalJumps++;
                    if (_localJumpLog.Allow((ulong)(Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency)))
                        Log("DE.View camera jump " + jump.ToString("0.0", CultureInfo.InvariantCulture) + " m inside the resident area (camera switch / seat): no resettle, areaBuilt only (#" + LocalJumps + ")");
                }
            }
            _prevCamSn = f.CamSn;
            _hasPrevCam = true;
            return ev;
        }

        private void LogStep(ref ViewInputs i, ref ViewOutputs o, long now, uint ev)
        {
            double nowMs = now / 1000.0;
            if (o.CrossingTick != _crossingTickSeen) { _crossingTickSeen = o.CrossingTick; _crossingAtMs = nowMs; }
            if (ev != 0 && ev != ViewEvents.WorldLoad)
                Log("DE.View event " + EventNames(ev) + " tick=" + i.Tick + " (snReady needs a fresh streamingSettled + areaBuilt)");
            if (o.Changed)
            {
                Transitions++;
                S2State to = ViewStateAdapter.StateOf(o.ViewState, o.ApproachMode);
                string extra = to == S2State.Underwater
                    ? " crossing->entry " + Num(nowMs - _crossingAtMs) + " ms (presentedSeTick " + i.PresentedSeTick + " >= crossingTick " + o.CrossingTick + ")"
                    : "";
                Log("DE.View -> " + ViewStateAdapter.Name(to) + " (" + ViewReason.Name(o.Reason) + ") tick=" + i.Tick + " y=" + Num(i.Y) + " vy=" + Num(i.Vy) +
                    " rho=" + Num(i.RhoInfM) + " zone=" + ZoneTracker.Name(i.Zone) + " snReady=" + o.SnReady + " blocked=" +
                    ViewStateAdapter.BlockedNames(o.Blocked) + " perf=" + ViewStateMachine.PerfName(o.PerfMode) + "/" + o.TargetFps +
                    " request=0x" + o.Request.ToString("x", CultureInfo.InvariantCulture) + " renderSeq=" + o.RenderSeq + " ack=" + i.RenderAckSeq + extra);
            }
            // why a Prime below the waterline is not entering Underwater (logged when the blocking set changes)
            S2State st = ViewStateAdapter.StateOf(o.ViewState, o.ApproachMode);
            if (st == S2State.Prime && i.Y <= ViewThresholds.UnderwaterEnterY)
            {
                uint b = o.Blocked & ~ViewBlock.NotPrime;
                if (b != _lastPrimeBlockLogged && b != 0)
                    Log("DE.View Prime below the waterline (y " + Num(i.Y) + "), Underwater held by: " + ViewStateAdapter.BlockedNames(b));
                _lastPrimeBlockLogged = b;
            }
            else _lastPrimeBlockLogged = uint.MaxValue;
            BandHold(ref i, ref o, st, now);
        }

        /// <summary>
 /// The band-hold diagnostic (class remarks): Prime with <c>−0.20 &lt; y ≤ −0.05 m</c> for more than 1 s. Logs only;
 /// the machine's behaviour is unchanged. Allocates only when it logs.
 /// </summary>
        private void BandHold(ref ViewInputs i, ref ViewOutputs o, S2State st, long now)
        {
            bool inBand = st == S2State.Prime && i.Y > ViewThresholds.UnderwaterEnterY && i.Y <= ViewThresholds.UnderwaterExitY;
            if (!inBand)
            {
                if (_bandLogged)
                {
                    string how = st == S2State.Underwater ? "entered Underwater"
                               : st != S2State.Prime ? "state " + ViewStateAdapter.Name(st)
                               : i.Y <= ViewThresholds.UnderwaterEnterY ? "y at or below " + Num(ViewThresholds.UnderwaterEnterY) + " m (still Prime: blocked=" +
                                                                         ViewStateAdapter.BlockedNames(o.Blocked & ~ViewBlock.NotPrime) + ")"
                               : "y above " + Num(ViewThresholds.UnderwaterExitY) + " m";
                    Log("DE.View band hold #" + BandHolds + " ended after " + Num((now - _bandSinceUs) / 1e6) + " s: " + how +
                        " tick=" + i.Tick + " y=" + Num(i.Y) + " vy=" + Num(i.Vy));
                }
                _bandIn = false;
                _bandLogged = false;
                return;
            }
            if (!_bandIn)
            {
                _bandIn = true;
                _bandSinceUs = now;
                return;
            }
            long held = now - _bandSinceUs;
            bool first = !_bandLogged;
            if (first)
            {
                if (held <= BandHoldLogAfterUs) return;
                BandHolds++;
                _bandLogged = true;
            }
            else if (now < _bandNextLogUs) { NoteBandHeld(held); return; }
            _bandNextLogUs = now + BandHoldRepeatUs;
            NoteBandHeld(held);
            Log("DE.View band hold #" + BandHolds + " " + (first ? "start" : "continues") + ": Prime with y in (" +
                Num(ViewThresholds.UnderwaterEnterY) + ", " + Num(ViewThresholds.UnderwaterExitY) + "] m for " + Num(held / 1e6) +
                " s (Underwater needs y <= " + Num(ViewThresholds.UnderwaterEnterY) + " m, Surfacing happens at y > " + Num(ViewThresholds.UnderwaterExitY) +
                " m) tick=" + i.Tick + " y=" + Num(i.Y) + " vy=" + Num(i.Vy) + " snReady=" + o.SnReady + " blocked=" +
                ViewStateAdapter.BlockedNames(o.Blocked & ~ViewBlock.NotPrime) + " heldS=" + Num(held / 1e6));
        }

        private void NoteBandHeld(long heldUs)
        {
            double sec = heldUs / 1e6;
            if (sec > BandHoldMaxS) BandHoldMaxS = sec;
        }

        private void Trace(ref ViewMachineState pre, ref ViewInputs i, ref ViewOutputs o)
        {
            Action<string> sink = TraceSink;
            if (sink == null) return;
            bool transition = o.Changed || o.Reason != ViewReason.None || i.Events != 0 || o.PictureEdge != 0;
            if (transition && !TraceAll)
            {
                // the steps before the transition, oldest first
                int start = (_ringNext - _ringCount + TraceRingSteps) % TraceRingSteps;
                for (int k = 0; k < _ringCount; k++)
                {
                    int idx = (start + k) % TraceRingSteps;
                    WriteRow(ref _ringPre[idx], ref _ringIn[idx], ref _ringOut[idx], sink);
                }
                _ringCount = 0;
                _traceAfter = TraceAfterTransitionSteps;
            }
            if (TraceAll || transition || _traceAfter > 0 || pre.Steps % TraceEverySteps == 0)
            {
                WriteRow(ref pre, ref i, ref o, sink);
                if (_traceAfter > 0) _traceAfter--;
                _ringCount = 0;
                return;
            }
            _ringPre[_ringNext] = pre;
            _ringIn[_ringNext] = i;
            _ringOut[_ringNext] = o;
            _ringNext = (_ringNext + 1) % TraceRingSteps;
            if (_ringCount < TraceRingSteps) _ringCount++;
        }

        private void WriteRow(ref ViewMachineState pre, ref ViewInputs i, ref ViewOutputs o, Action<string> sink)
        {
            int n = ViewTrace.Format(ref pre, ref i, ref o, _traceBuf);
            if (n <= 0) return;
            TraceRows++;
            try { sink(new string(_traceBuf, 0, n)); } catch (Exception) { }
        }

        /// <summary>
 /// S7a: the <c>s7a.view</c> row (every view or latch change, a policy change, once per second outside Space) and
 /// the <c>DE.View picture</c> log line of a latch change. Allocates only when it writes.
 /// </summary>
        private void PictureRow(ref ViewInputs i, ref ViewOutputs o, long now)
        {
            uint prevPolicy = _lastPolicy;
            bool policyChanged = o.PicturePolicy != prevPolicy;
            _lastPolicy = o.PicturePolicy;
            if (o.PictureEdge != 0)
            {
                PictureEdges++;
                Log("DE.View picture " + (o.PictureEdge > 0 ? "ON (Subnautica's picture)" : "OFF (SE's picture)") + " policy=" + ViewPicture.Name(o.PicturePolicy) +
                    " reason=" + (o.PictureReason != ViewReason.None ? ViewReason.Name(o.PictureReason) : ViewReason.Name(o.Reason)) +
                    " view=" + ViewStateAdapter.Name(ViewStateAdapter.StateOf(o.ViewState, o.ApproachMode)) + " tick=" + i.Tick + " y=" + Num(i.Y) +
                    " vy=" + Num(i.Vy) + " bandTick=" + _state.BandTick);
            }
            else if (policyChanged && prevPolicy != uint.MaxValue)
                Log("DE.View picture policy " + ViewPicture.Name(o.PicturePolicy) + " (selected " + ViewPicture.Name(i.Picture) +
                    ((i.Mutants & ViewMutants.S7aM1Waterline) != 0 ? ", MUTANT S7a.M1" : "") + ")");
            Action<string> sink = ViewRowSink;
            if (sink == null) return;
            bool due = o.ViewState != ViewState.Space && now >= _nextViewRowUs;
            if (!(o.Changed || o.PictureEdge != 0 || policyChanged || due)) return;
            _nextViewRowUs = now + ViewRowEveryUs;
            ViewRows++;
            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(480);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", ci));
            sb.Append(",\"ev\":\"s7a.view\",\"tick\":").Append(i.Tick.ToString(ci));
            sb.Append(",\"view\":\"").Append(ViewStateMachine.ViewName(o.ViewState)).Append('"');
            sb.Append(",\"approach\":\"").Append(ViewStateMachine.ApproachName(ViewStateMachine.C5ApproachMode(o.ViewState, o.ApproachMode))).Append('"');
            sb.Append(",\"policy\":\"").Append(ViewPicture.Name(o.PicturePolicy)).Append('"');
            sb.Append(",\"picture\":").Append(o.Picture ? "true" : "false");
            sb.Append(",\"edge\":").Append(o.PictureEdge.ToString(ci));
            sb.Append(",\"reason\":").Append(o.Reason.ToString(ci));
            sb.Append(",\"pictureReason\":").Append(o.PictureReason.ToString(ci));
            sb.Append(",\"blocked\":").Append(o.Blocked.ToString(ci));
            sb.Append(",\"pictureBlocked\":").Append(o.PictureBlocked.ToString(ci));
            sb.Append(",\"pictureBlockedNames\":"); JsonText.AppendString(sb, ViewStateAdapter.BlockedNames(o.PictureBlocked));
            sb.Append(",\"bandTick\":").Append(_state.BandTick.ToString(ci));
            sb.Append(",\"y\":").Append(JsonNum(i.Y));
            sb.Append(",\"vy\":").Append(JsonNum(i.Vy));
            sb.Append(",\"rho\":").Append(JsonNum(i.RhoInfM));
            sb.Append(",\"zone\":").Append(i.Zone.ToString(ci));
            sb.Append(",\"snReady\":").Append(o.SnReady ? "true" : "false");
            sb.Append(",\"request\":").Append(o.Request.ToString(ci));
            sb.Append(",\"renderSeq\":").Append(o.RenderSeq.ToString(ci));
            sb.Append(",\"perfMode\":").Append(o.PerfMode.ToString(ci));
            sb.Append('}');
            try { sink(sb.ToString()); } catch (Exception) { }
        }

        private static string JsonNum(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void DepthCheck(ref FrameFacts f, double y, long nowUs)
        {
            if (--_depthCountdown > 0) return;
            _depthCountdown = DepthCheckEveryTicks;
            Func<Vec3, float?> depthAt = DepthAt;
            if (depthAt == null || f.Zone == Zone.OffSite) return;
            float? d;
            try { d = depthAt(f.CamSe); }
            catch (Exception) { d = null; }
            if (!d.HasValue) return;
            DepthChecks++;
            double diff = Math.Abs(d.Value - y);
            if (diff > DepthWorstM) DepthWorstM = diff;
            double nowMs = nowUs / 1000.0;
            if (diff > DepthMismatchM && nowMs - _lastDepthLogMs >= DepthLogIntervalMs)
            {
                _lastDepthLogMs = nowMs;
                Log("DE.View depth mismatch: map y=" + Num(y) + " m, Water Mod GetDepth=" + Num(d.Value) + " m, |diff|=" + Num(diff) +
                    " m > " + Num(DepthMismatchM) + " (tick " + f.Tick + "; the map's y drives the state machine; logged at most once per minute)");
            }
        }

        public static string EventNames(uint ev)
        {
            string s = "";
            if ((ev & ViewEvents.Teleport) != 0) s += "teleport ";
            if ((ev & ViewEvents.NewGeneration) != 0) s += "newGeneration ";
            if ((ev & ViewEvents.LinkRecovery) != 0) s += "linkRecovery ";
            if ((ev & ViewEvents.WorldLoad) != 0) s += "worldLoad ";
            return s.Trim();
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            return Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
