using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>One recorded step of a <see cref="ViewSim"/> run.</summary>
    internal struct ViewRec
    {
        public ulong Tick;
        public long Us;
        public double Y;
        public double Rho;
        public uint Zone;
        public uint AckIn;
        public uint EventsIn;
        public uint PreSeq;
        public uint PreRequest;
        public ViewOutputs O;
    }

    /// <summary>
 /// A scripted world around <see cref="ViewStateMachine.Step"/> (no game): the camera's y and x per tick, the zone
 /// from <see cref="SiteZones.Classify"/>, a Subnautica that presents <see cref="SnLag"/> ticks late and reports
 /// settled/built once <see cref="SettleAt"/> is reached, and a render thread that acknowledges the request of
 /// <see cref="AckLag"/> steps ago. 60 Hz ticks of 16,667 µs on the unscaled clock.
 /// </summary>
    internal sealed class ViewSim
    {
        public const long TickUs = 16667;

        public ViewMachineState State;
        public readonly List<ViewRec> Log = new List<ViewRec>();
        public readonly List<string> Rows = new List<string>();
        public bool TraceRows;

        public ulong Tick = 1000;
        public double X = 100.0, Z = 100.0;
        public bool OnSite = true, BridgeUp = true;
        public uint Peer = PeerState.Alive;
        public uint Phase = SnPhase.Active;
        public bool AreaBuilt = true, Settled = true, RectOk = true, UiHidden = true;
        /// <summary>IsWorldSettled rarely holds while the camera moves: no streamingSettled while |vy| >= 0.5 m/s.</summary>
        public bool SettledOnlyWhenStill;
        public bool ZOrderOk = true, Foreground = true, Paused, Gui, CursorOrChat, Loading, KeepAudibleUnfocused;
        public uint DisplayMode = Wire.DisplayMode.F;
        public uint Mutants;
        /// <summary>
 /// S7a picture policy input (<see cref="ViewInputs.Picture"/>): Site by default, as the machine; the S2 pins set
 /// <see cref="ViewPicture.Waterline"/> explicitly ("S2 policy, the fallback").
 /// </summary>
        public uint Picture = ViewPicture.Site;
        public int SnLag = 1;
        public int AckLag = 1;
        /// <summary>Subnautica reports settled + built (with a fresh areaBuiltSeTick) from this tick; null = always.</summary>
        public ulong? SettleAt;
        /// <summary>Fixed vy for the next steps; null = from consecutive y (× 60).</summary>
        public double? Vy;
        /// <summary>Override of the C7 areaBuiltSeTick (null = derived from <see cref="SnLag"/> and <see cref="SettleAt"/>).</summary>
        public ulong? AreaBuiltOverride;
        /// <summary>A render thread model that acknowledges by itself (replaces <see cref="AckLag"/>), and a hook after each step.</summary>
        public Func<uint> AckProvider;
        /// <summary>The render thread's live margins state (ViewInputs.MarginsReady): <see cref="MarginsReady"/> unless the provider is set.</summary>
        public bool MarginsReady = true;
        public Func<bool> MarginsReadyProvider;
        public Action<ViewOutputs> AfterStep;

        private bool _inZone;
        private uint _events;
        private double _lastY = double.NaN;
        private readonly List<uint> _seqHistory = new List<uint>();
        private readonly char[] _buf = new char[ViewTrace.MaxRowChars];

        public ViewOutputs Last { get { return Log.Count > 0 ? Log[Log.Count - 1].O : default(ViewOutputs); } }

        public void Event(uint ev) { _events |= ev; }

        /// <summary>A teleport this tick; Subnautica settles <paramref name="settleTicks"/> later.</summary>
        public void Teleport(int settleTicks)
        {
            _events |= ViewEvents.Teleport;
            SettleAt = Tick + (ulong)settleTicks;
        }

        public ViewOutputs Step(double y)
        {
            var i = new ViewInputs();
            i.Tick = Tick;
            i.NowUs = (long)Tick * TickUs;
            i.BridgeUp = BridgeUp;
            i.OnSite = OnSite;
            double rho = SiteZones.RhoInf(X, Z);
            i.Zone = SiteZones.Classify(OnSite, rho, ref _inZone);
            i.RhoInfM = rho;
            i.Y = y;
            i.Vy = Vy ?? (double.IsNaN(_lastY) ? 0.0 : (y - _lastY) * 60.0);
            _lastY = y;
            i.Peer = Peer;
            i.SnPhase = Phase;
            long lagged = (long)Tick - SnLag;   // a negative lag stamps ahead of SE (a stale stamp from an earlier world)
            ulong presented = lagged > 0 ? (ulong)lagged : 0;
            bool ready = !SettleAt.HasValue || Tick >= SettleAt.Value;
            uint f = 0;
            if (RectOk) f |= SnStatusFlags.RectOk;
            if (UiHidden) f |= SnStatusFlags.UiHidden;
            if (AreaBuilt && ready) f |= SnStatusFlags.AreaBuilt;
            if (Settled && ready && !(SettledOnlyWhenStill && Math.Abs(i.Vy) >= 0.5)) f |= SnStatusFlags.StreamingSettled;
            i.SnFlags = f;
            i.PresentedSeTick = presented;
            i.AreaBuiltSeTick = AreaBuiltOverride ?? (ready ? presented : (SettleAt.HasValue && SettleAt.Value > 400 ? SettleAt.Value - 400 : 0));
            i.ZOrderOk = ZOrderOk;
            i.Paused = Paused;
            i.Foreground = Foreground;
            i.GuiScreenOpen = Gui;
            i.CursorOrChat = CursorOrChat;
            i.Loading = Loading;
            i.KeepAudibleUnfocused = KeepAudibleUnfocused;
            i.Events = _events;
            _events = 0;
            // the render thread presents the request of AckLag (>= 1) steps ago; 1 = the next step sees it presented
            int lag = Math.Max(1, AckLag);
            i.RenderAckSeq = _seqHistory.Count >= lag ? _seqHistory[_seqHistory.Count - lag] : 0u;
            if (AckProvider != null) i.RenderAckSeq = AckProvider();
            // review fix: the live margins state (ViewInputs.MarginsReady); true unless a test models the render thread
            i.MarginsReady = MarginsReadyProvider != null ? MarginsReadyProvider() : MarginsReady;
            i.DisplayMode = DisplayMode;
            i.Mutants = Mutants;
            i.Picture = Picture;

            ViewMachineState pre = State;
            ViewOutputs o;
            ViewStateMachine.Step(ref State, ref i, out o);
            if (TraceRows)
            {
                int n = ViewTrace.Format(ref pre, ref i, ref o, _buf);
                if (n < 0) throw new InvalidOperationException("trace row overflow");
                Rows.Add(new string(_buf, 0, n));
            }
            _seqHistory.Add(o.RenderSeq);
            if (AfterStep != null) AfterStep(o);
            Log.Add(new ViewRec
            {
                Tick = Tick, Us = i.NowUs, Y = y, Rho = rho, Zone = i.Zone, AckIn = i.RenderAckSeq, EventsIn = i.Events,
                PreSeq = pre.RenderSeq, PreRequest = pre.Request, O = o,
            });
            Tick++;
            return o;
        }

        public ViewOutputs Hold(int ticks, double y)
        {
            ViewOutputs o = default(ViewOutputs);
            for (int k = 0; k < ticks; k++) o = Step(y);
            return o;
        }

        /// <summary>Linear y from <paramref name="from"/> to <paramref name="to"/> over <paramref name="ticks"/> steps (last step at <paramref name="to"/>).</summary>
        public ViewOutputs Ramp(int ticks, double from, double to)
        {
            ViewOutputs o = default(ViewOutputs);
            for (int k = 1; k <= ticks; k++) o = Step(from + (to - from) * k / ticks);
            return o;
        }

        public ViewOutputs Run(int ticks, Func<double, double> yOfSeconds)
        {
            ViewOutputs o = default(ViewOutputs);
            for (int k = 0; k < ticks; k++) o = Step(yOfSeconds(Tick / 60.0));
            return o;
        }

        /// <summary>Steps at <paramref name="y"/> until <paramref name="until"/> holds (at most <paramref name="max"/> steps); returns the steps taken.</summary>
        public int StepUntil(double y, Func<ViewOutputs, bool> until, int max = 600)
        {
            for (int k = 1; k <= max; k++)
                if (until(Step(y))) return k;
            throw new InvalidOperationException("condition not reached in " + max + " steps at tick " + Tick + "; last view " +
                                                ViewStateMachine.ViewName(Last.ViewState) + " blocked 0x" + Last.Blocked.ToString("x"));
        }

        /// <summary>A sim already Underwater at y = −20 m (ready, design F, policy <paramref name="picture"/>), with the log cleared.</summary>
        public static ViewSim Underwater(double y = -20.0, uint picture = ViewPicture.Site)
        {
            var sim = new ViewSim { Picture = picture };
            sim.Vy = 0.0;
            sim.StepUntil(y, o => o.ViewState == ViewState.Underwater);
            sim.Hold(30, y);
            sim.Vy = null;
            sim.Step(y);
            sim.Log.Clear();
            return sim;
        }

        /// <summary>
 /// A sim in Approach/Prime at y = +0.5 m with its margins request acknowledged (policy <paramref name="picture"/>;
 /// under Site the picture latch is on as well), log cleared.
 /// </summary>
        public static ViewSim Primed(double y = 0.5, uint picture = ViewPicture.Site)
        {
            var sim = new ViewSim { Picture = picture };
            sim.Vy = 0.0;
            sim.StepUntil(y, o => o.ApproachMode == ApproachMode.Prime && o.ViewState == ViewState.Approach);
            sim.Hold(5, y);
            sim.Vy = null;
            sim.Step(y);
            sim.Log.Clear();
            return sim;
        }

        // ------------------------------------------------------------------------------------------- analysis
 /// <summary>S7a: the steps whose picture changed (<see cref="ViewOutputs.PictureEdge"/> ≠ 0), from <paramref name="from"/> on.</summary>
        public static List<ViewRec> PictureEdges(List<ViewRec> log, int from = 0)
        {
            var e = new List<ViewRec>();
            for (int k = Math.Max(0, from); k < log.Count; k++) if (log[k].O.PictureEdge != 0) e.Add(log[k]);
            return e;
        }

        /// <summary>Ticks at which the view went into or out of Underwater (S2.N2 "switches").</summary>
        public static List<ViewRec> Switches(List<ViewRec> log, int from = 0)
        {
            var sw = new List<ViewRec>();
            for (int k = Math.Max(1, from); k < log.Count; k++)
            {
                bool was = log[k - 1].O.ViewState == ViewState.Underwater;
                bool now = log[k].O.ViewState == ViewState.Underwater;
                if (was != now) sw.Add(log[k]);
            }
            return sw;
        }

        /// <summary>The smallest spacing between switches (µs; long.MaxValue with fewer than two) and the most in any half-open 1 s window.</summary>
        public static void N2Metrics(List<ViewRec> sw, out long minSpacingUs, out int maxPerSecond)
        {
            minSpacingUs = long.MaxValue;
            for (int k = 1; k < sw.Count; k++) minSpacingUs = Math.Min(minSpacingUs, sw[k].Us - sw[k - 1].Us);
            maxPerSecond = 0;
            int j = 0;
            for (int k = 0; k < sw.Count; k++)
            {
                while (sw[k].Us - sw[j].Us >= 1000000) j++;
                maxPerSecond = Math.Max(maxPerSecond, k - j + 1);
            }
        }

        /// <summary>
 /// Crossing-order violations in design F: a skip without margins; a skip started before the margins-on request
 /// was acknowledged; margins dropped before a scene-drawn request (after the last skip) was acknowledged.
 /// </summary>
        public static List<string> OrderViolations(List<ViewRec> log)
        {
            var bad = new List<string>();
            uint? lastSkipSeq = null, clearedSeq = null;
            for (int k = 0; k < log.Count; k++)
            {
                ViewRec r = log[k];
                uint rq = r.O.Request;
                if ((rq & ViewRequest.SceneSkip) != 0 && (rq & ViewRequest.Margins) == 0)
                    bad.Add("tick " + r.Tick + ": skip without margins");
                if (k > 0)
                {
                    uint prevRq = log[k - 1].O.Request;
                    if ((rq & ViewRequest.SceneSkip) != 0 && (prevRq & ViewRequest.SceneSkip) == 0)
                    {
                        bool ok = (r.PreRequest & ViewRequest.Margins) != 0 && (r.PreRequest & ViewRequest.SceneSkip) == 0 && r.AckIn == r.PreSeq;
                        if (!ok) bad.Add("tick " + r.Tick + ": skip before the margins-on request was presented");
                    }
                    if ((prevRq & ViewRequest.Margins) != 0 && (rq & ViewRequest.Margins) == 0)
                    {
                        bool ok = (r.PreRequest & ViewRequest.SceneSkip) == 0 && (rq & ViewRequest.SceneSkip) == 0;
                        if (ok && lastSkipSeq.HasValue) ok = clearedSeq.HasValue && r.AckIn >= clearedSeq.Value;
                        if (!ok) bad.Add("tick " + r.Tick + ": margins dropped before a scene-drawn frame was presented");
                        lastSkipSeq = null;
                        clearedSeq = null;
                    }
                }
                if ((rq & ViewRequest.SceneSkip) != 0) { lastSkipSeq = r.O.RenderSeq; clearedSeq = null; }
                else if (lastSkipSeq.HasValue && !clearedSeq.HasValue) clearedSeq = r.O.RenderSeq;
            }
            return bad;
        }
    }
}
