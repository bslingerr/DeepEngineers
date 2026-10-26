using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S2 runtime view state machine (ARCHITECTURE §4, ROADMAP S2 "State machine", CONTRACT §5.4 C5).
 //
 // A pure, deterministic, allocation-free step function: (state, inputs) -> (state', outputs). The SE plugin owns one
 // ViewMachineState for the plugin's lifetime, fills a ViewInputs once per SE tick in the PrepareForDraw postfix
 // (InWorld only) and calls ViewStateMachine.Step; the outputs are published in C5 (ViewStateMachine.ApplyTo) and
 // drive the render-thread display unit (margins, scene skip, alpha fill, A' lowering), the z-order unit and the
 // UpdateSound postfix (audio). Every threshold is a named constant of ViewThresholds / SiteZones / AudioMix.
 // The independent Python oracle is tests/s2/state_traces.py (same constants, its own code); rows are written by
 // ViewTrace.
 //
 // ARCHITECTURE §4.2 "Picture policy (S7a)", CONTRACT §5.4 "C5/C7 semantics for S7a"): the
 // states, transitions and thresholds are S2's; the picture policy is a machine input (ViewInputs.Picture). Site, the
 // default, shows Subnautica's picture inside the site below the Approach band (a picture latch that turns on in
 // Approach once Subnautica is ready and holds through Underwater and Surfacing); Waterline is S2's policy, the
 // fallback and the replay policy of vsm rows of version 3 or lower.
 // =================================================================================================================

 /// <summary>
 /// Every threshold of the view state machine (ARCHITECTURE §4.2 "Hysteresis summary", ROADMAP S2 state table).
 /// Altitudes are SN metres, <c>y</c> negative underwater. Times are microseconds of the unscaled clock.
 /// </summary>
    public static class ViewThresholds
    {
        /// <summary>

 /// <c>y &lt; ApproachEnterY</c> (strict), Approach → Space when <c>y &gt; ApproachExitY</c> (strict, held 1 s: F15). Under
 /// the Site policy (S7a) this is where the picture swaps. Default +1,000 / +1,100 m again since S7t, live: env
 /// <c>DEEPENGINEERS_BAND=enter,exit</c>, <c>de.s7 {band: [enter, exit]}</c>, <c>/de s7 band &lt;enter&gt; &lt;exit&gt;</c>
 /// (<see cref="SetBand"/>). The tests pin the mechanics at the original +300 / +350 m; the Python twin takes
 /// <c>--band</c>. The S7a mirror radii above water (1,500 / 1,600 m) are pinned for the default band.
 /// </summary>
        public static double ApproachEnterY { get { return _bandEnter; } }
        public static double ApproachExitY { get { return _bandExit; } }
        /// <summary>
 /// S7t T3: back to +1,000 / +1,100 m (ROADMAP "### S7t Seamless transition": "the band goes back up to 1 km so a
 /// ship is well inside Subnautica's picture before the sea fills the screen").
 /// the 1 km band ("the skybox of SN isn't made to be seen that high") took it to +400 / +450 m; S7t T2 (horizon, haze)
 /// and T5 (the sky) answer that. A live setting (env, de.s7 band); the tests pin their own band.
 /// </summary>
        public const double DefaultApproachEnterY = 1000.0, DefaultApproachExitY = 1100.0;
        private static double _bandEnter = DefaultApproachEnterY, _bandExit = DefaultApproachExitY;
        /// <summary>Where the band came from: "default", "env", "test" or a command's via.</summary>
        public static string BandSource = "default";
        /// <summary>F15: Approach → Space on altitude only after the exit condition held this long.</summary>
        public const long ApproachLeaveDwellUs = 1000000;

        /// <summary>Sets the band; false (unchanged) unless both are finite and <c>0 ≤ enter &lt; exit</c>.</summary>
        public static bool SetBand(double enter, double exit, string source)
        {
            if (double.IsNaN(enter) || double.IsNaN(exit) || double.IsInfinity(enter) || double.IsInfinity(exit)) return false;
            if (!(enter >= 0.0) || !(exit > enter)) return false;
            _bandEnter = enter;
            _bandExit = exit;
            BandSource = source ?? "set";
            return true;
        }

        /// <summary>"enter,exit" (metres, invariant culture; blanks and ';' accepted); false for anything else.</summary>
        public static bool TryParseBand(string text, out double enter, out double exit)
        {
            enter = exit = double.NaN;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out enter) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out exit);
        }
        /// <summary>
 /// Space → Approach also when the camera will be below <see cref="ApproachEnterY"/> within this many seconds
 /// (<c>y ≤ 300 + 8·(−vy)</c>, <c>vy &lt
 /// 109 m/s return from space gave Subnautica 2.7 s to warm up and stream (9 s swap); 8 s of lead at that speed.
 /// </summary>
        public const double ApproachPredictS = 8.0;

        /// <summary>Track → Prime when <c>y ≤ +1 m</c>; Prime → Track needs <c>y &gt; +1 m</c>.</summary>
        public const double PrimeY = 1.0;
        /// <summary>Track → Prime when <c>y ≤ +5 m</c> and <c>vy ≤ −0.2 m/s</c>.</summary>
        public const double PrimeDescentY = 5.0;
        public const double PrimeDescentVy = -0.2;
        /// <summary>Track → Prime when the predicted crossing <c>y / −vy</c> is within 4 s (evaluated as <c>y ≤ 4·(−vy)</c>, <c>vy &lt; 0</c>; 2 s until).</summary>
        public const double PrimePredictS = 4.0;   // Subnautica primes earlier at speed
 /// <summary>

 /// will be when the request is presented (about one frame of each game plus the render acknowledgement), so a
 /// 20 m/s dive switches about 1 m earlier instead of 1 m late; at swimming speeds the lead is centimetres.
 /// </summary>
        public const double UnderwaterLeadS = 0.0;   // parked at 0: S2.N2 (bobbing band), S2.N7b (crossing tick) and the Python twin pin the raw-y rule; a lead needs the contract and twin updated first
 /// <summary>Prime → Track after 3 s without any Prime condition (and <c>y &gt; +1 m</c>) while <c>y ≥ +5 m</c>.</summary>
        public const long PrimeQuietUs = 3000000;
        /// <summary>
 /// Re-dive fix: below this altitude (<c>y &lt; +5 m</c>, strict) the Prime quiet timeout is
 /// <see cref="PrimeQuietNearUs"/> instead of <see cref="PrimeQuietUs"/>.
 /// after lingering just above the water for more than 3 s, the next dive showed SE's view for about 0.5–1 s; the
 /// 13/10 SE log shows a Prime → Track drop ("Prime quiet") before each slow dive, Subnautica back at 10 fps with
 /// its cameras off, and 533 ms from the crossing to the Underwater entry (quick re-dives without the drop: 33 ms).
 /// The cost is Subnautica at full rate (Full recipe) for up to 10 s near the surface.
 /// </summary>
        public const double PrimeQuietNearY = 5.0;
        /// <summary>Prime → Track after 10 s without any Prime condition while <c>y &lt; +5 m</c> (and <c>y &gt; +1 m</c>); re-dive fix.</summary>
        public const long PrimeQuietNearUs = 10000000;

        /// <summary>Prime → Underwater needs <c>y ≤ −0.20 m</c>.</summary>
        public const double UnderwaterEnterY = -0.20;
        /// <summary>Underwater → Surfacing when <c>y &gt; −0.05 m</c> (after the dwell).</summary>
        public const double UnderwaterExitY = -0.05;
        /// <summary>Minimum time between two Approach ↔ Underwater switches (0.25 s).</summary>
        public const long MinDwellUs = 250000;

        /// <summary>Prime → Underwater needs <c>ρ∞ ≤ 7,800 m</c> (ARCHITECTURE §4.2), on top of the zone (hysteretic, 7,800/7,900).</summary>
        public const double UnderwaterMaxRhoM = SiteZones.ZoneInM;

        /// <summary>CONTRACT §5.4 C7 freshness for <c>snReady</c>: <c>presentedSeTick ≥ tick − 4</c>.</summary>
        public const ulong FreshnessTicks = 4;
        /// <summary>
 /// Sanity bound (ours, not in CONTRACT): a C7 tick stamp more than one tick ahead of SE's tick is stale (from an
 /// earlier world, whose tick counter was larger) and is not taken as fresh.
 /// </summary>
        public const ulong AheadTicks = 1;
        /// <summary>
 /// (laptop logs: after a respawn the area was built for about 45 s of a 110 s curtain while the streamer was
 /// still busy with far cells): once the area around the camera is reported built after a jump, the wait for the streamer
 /// to go idle (StreamingSettled) lasts at most this many ticks from the jump; the far cells pop in afterwards.
 /// </summary>
        public const ulong ResettleSettleCapTicks = 180;

        /// <summary>
 /// Lowest and highest <c>snPhase</c> counted as ready: Active only (CONTRACT §5.4 "snReady … snPhase Active",
 /// ARCHITECTURE §4.1). The SN plugin publishes Active once the Full recipe has rendered (Prime, Underwater,
 /// Surfacing); ReadyIdle (Idle) and WarmingUp (Track, Full before its first frames) are not ready. Integration
 /// change: wire-s2 had ReadyIdle..Active while the SN side did not publish WarmingUp/Active yet.
 /// </summary>
        public const uint ReadyPhaseMin = SnPhase.Active;
        public const uint ReadyPhaseMax = SnPhase.Active;

        /// <summary>C5 <c>targetFps</c> per Subnautica mode (ARCHITECTURE §5.13): Idle and Track (0, 10); Full 0 = no cap
 /// (Subnautica picks (1, any) or (0, 120)); (0, 30) while Subnautica's picture is shown and SE is not foreground
 /// (S2: Underwater; S7a <c>Site</c>: the picture latch on, Approach included).</summary>
        public const uint IdleTargetFps = 10;
        public const uint TrackTargetFps = 10;
        public const uint FullTargetFps = 0;
        public const uint UnfocusedUnderwaterFps = 30;

        /// <summary>
 /// S7a: minimum time between two changes of the picture latch (0.25 s, as the waterline dwell); it gates the latch's
 /// on edge only, the off edges are immediate (ARCHITECTURE §4.2 thresholds "Picture dwell (S7a)"; S7a conventions
 /// <c>Limits.PictureDwellMs</c>).
 /// </summary>
        public const long PictureDwellUs = 250000L;

        /// <summary>
 /// S2.M4 seeded failure (no hysteresis, no dwell): one waterline threshold for both directions, the centre of
 /// the band ((−0.20 + −0.05)/2 = −0.125 m), so that the S2.N2 trace (a) bobbing inside the band switches.
 /// </summary>
        public const double MutantM4WaterlineY = -0.125;
    }

    /// <summary>
 /// Zones of the camera in SN coordinates (ARCHITECTURE §4.2): CORE inside about ±1,950 m, TILE VOID to ±2,048 m,
 /// OUTER VOID to ±7,800 m (left at 7,900 m: hysteresis), FAR beyond, off-site elsewhere. Same constants and logic
 /// as the SE plugin's <c>ZoneTracker</c> (S1); this pure version serves the state-machine tests and may replace it.
 /// </summary>
    public static class SiteZones
    {
        public const double CoreHalfM = 1950.0;
        public const double TileHalfM = 2048.0;
        public const double ZoneInM = 7800.0;
        public const double ZoneOutM = 7900.0;

        /// <summary><c>ρ∞ = max(|x|, |z|)</c> of the camera in SN metres.</summary>
        public static double RhoInf(double snX, double snZ)
        {
            return Math.Max(Math.Abs(snX), Math.Abs(snZ));
        }

        /// <summary>The C5 zone with hysteresis; <paramref name="inZone"/> carries the hysteresis state between calls.</summary>
        public static uint Classify(bool onSite, double rhoInf, ref bool inZone)
        {
            if (!onSite || double.IsNaN(rhoInf))
            {
                inZone = false;
                return Zone.OffSite;
            }
            inZone = inZone ? rhoInf <= ZoneOutM : rhoInf <= ZoneInM;
            if (!inZone) return Zone.Far;
            if (rhoInf <= CoreHalfM) return Zone.Core;
            if (rhoInf <= TileHalfM) return Zone.TileVoid;
            return Zone.OuterVoid;
        }

        /// <summary>CORE, TILE VOID or OUTER VOID: Subnautica draws the underwater picture there.</summary>
        public static bool IsDive(uint zone)
        {
            return zone == Zone.Core || zone == Zone.TileVoid || zone == Zone.OuterVoid;
        }
    }

    /// <summary>Peer state as SE judges it (ARCHITECTURE §4.1, §4.3). Same values as the SE plugin's <c>PeerStatus</c>.</summary>
    public static class PeerState
    {
        /// <summary>A new peer generation was seen and not judged yet: not alive.</summary>
        public const uint Unknown = 0;
        public const uint Alive = 1;
        /// <summary>Heartbeat ≥ 3 s old (120 s while loading), process alive.</summary>
        public const uint Stalled = 2;
        /// <summary>Process exit, <c>snPhase ∈ {Quitting, None}</c>, changed PID/generation, lost window.</summary>
        public const uint Exited = 3;
    }

    /// <summary>Events of this tick (<see cref="ViewInputs.Events"/>). Each makes <c>snReady</c> need a fresh <c>streamingSettled</c>.</summary>
    public static class ViewEvents
    {
        /// <summary>C1 <c>teleported</c> (a plugin teleport or a > 20 m / > 45° camera jump; CONTRACT §2.8).</summary>
        public const uint Teleport = 1u << 0;
        /// <summary>A new Subnautica generation was seen.</summary>
        public const uint NewGeneration = 1u << 1;
        /// <summary>Link recovery after PeerStalled (linkEpoch bumped).</summary>
        public const uint LinkRecovery = 1u << 2;
        /// <summary>A world load (new world epoch): the machine restarts in Space. The first Step ever counts as one.</summary>
        public const uint WorldLoad = 1u << 3;
    }

    /// <summary>Seeded failures the plugin can switch on through <see cref="ViewInputs.Mutants"/>.</summary>
    public static class ViewMutants
    {
        /// <summary>S2.M4: no waterline hysteresis (one threshold, <see cref="ViewThresholds.MutantM4WaterlineY"/>) and no dwell.</summary>
        public const uint M4NoHysteresis = 1u << 0;
        /// <summary>S2.M11: SE volumes written every tick without the focus/pause/GUI/loading gate.</summary>
        public const uint M11NoAudioGate = 1u << 1;
        /// <summary>
 /// S2.M10: snReady gated on C7 streamingSettled (IsWorldSettled) at all times, not only after an event; a
 /// moving dive then enters late or never (S2.N7 (b)).
 /// </summary>
        public const uint M10SettledAlways = 1u << 2;
        /// <summary>
 /// S7a.M1: the machine runs the <see cref="ViewPicture.Waterline"/> policy while <see cref="ViewPicture.Site"/> is
 /// selected (S7a.N1 red: Approach rows show SE's picture, picture edges at the waterline).
 /// </summary>
        public const uint S7aM1Waterline = 1u << 3;
    }

    /// <summary>
 /// S7a picture policy (<see cref="ViewInputs.Picture"/>; ARCHITECTURE §4.2 "Picture policy (S7a)"). These two values
 /// enter the S7a canonical text (<see cref="S7aConventions"/>).
 /// </summary>
    public static class ViewPicture
    {
        /// <summary>Subnautica's picture inside the site below the Approach band, above and under water (default since S7a).</summary>
        public const uint Site = 0;
        /// <summary>S2's policy: Subnautica's picture Underwater only (the fallback; the replay policy of vsm rows v1-v3).</summary>
        public const uint Waterline = 1;

        public static string Name(uint picture)
        {
            switch (picture)
            {
                case Site: return "site";
                case Waterline: return "waterline";
                default: return "unknown(" + picture + ")";
            }
        }
    }

    /// <summary>Why the last transition happened (<see cref="ViewOutputs.Reason"/>; logs and the trace).</summary>
    public static class ViewReason
    {
        public const uint None = 0, WorldLoad = 1, EnterApproach = 2, LeaveSite = 3, LeavePeer = 4, LeaveAltitude = 5,
            EnterPrime = 6, PrimeQuiet = 7, EnterUnderwater = 8, SurfaceWaterline = 9, SurfaceSite = 10, SurfacePeer = 11,
            SurfaceResettle = 12, SurfacedToPrime = 13, SurfacedToSpace = 14;

        /// <summary>
 /// S7a: why the picture latch changed (<see cref="ViewOutputs.PictureReason"/>; <c>Site</c> policy only, None
 /// under <c>Waterline</c>, whose picture edges are the Underwater transitions and their <see cref="ViewOutputs.Reason"/>).
 /// </summary>
        public const uint PictureOn = 15, PictureOffSpace = 16, PictureOffSite = 17, PictureOffPeer = 18, PictureOffResettle = 19;

        public static string Name(uint reason)
        {
            switch (reason)
            {
                case None: return "none";
                case WorldLoad: return "world load";
                case EnterApproach: return "enter Approach";
                case LeaveSite: return "left the site/zone/bridge";
                case LeavePeer: return "peer not alive";
                case LeaveAltitude: return "y > +" + ((long)ViewThresholds.ApproachExitY).ToString() + " m (band exit, held 1 s)";
                case EnterPrime: return "enter Prime";
                case PrimeQuiet: return "Prime quiet: 10 s below +5 m, 3 s above";
                case EnterUnderwater: return "enter Underwater";
                case SurfaceWaterline: return "surface: waterline";
                case SurfaceSite: return "surface: site/zone/bridge";
                case SurfacePeer: return "surface: peer";
                case SurfaceResettle: return "surface: not ready after teleport";
                case SurfacedToPrime: return "surfaced to Prime";
                case SurfacedToSpace: return "surfaced to Space";
                case PictureOn: return "picture on: Subnautica ready in Approach";
                case PictureOffSpace: return "picture off: Space";
                case PictureOffSite: return "picture off: site/zone/bridge";
                case PictureOffPeer: return "picture off: peer";
                case PictureOffResettle: return "picture off: re-streaming after a teleport/load";
                default: return "unknown";
            }
        }
    }

    /// <summary>
 /// Bits of <see cref="ViewOutputs.Blocked"/>: every Prime → Underwater guard that fails this tick (diagnostic;
 /// computed in every state). The <c>snReady</c> parts are <see cref="SnReadyMask"/>. S7a: the picture latch's on-edge
 /// guards (<see cref="ViewOutputs.PictureBlocked"/>) use the same bits (Dwell = the picture dwell, Render = F's
 /// margins-ready) plus <see cref="Band"/>, <see cref="NotApproach"/> and <see cref="High"/>.
 /// </summary>
    public static class ViewBlock
    {
        public const uint NotPrime = 1u << 0, Y = 1u << 1, Rho = 1u << 2, Peer = 1u << 3, Phase = 1u << 4,
            AreaBuilt = 1u << 5, Settle = 1u << 6, Fresh = 1u << 7, Rect = 1u << 8, UiHidden = 1u << 9,
            ZOrder = 1u << 10, Dwell = 1u << 11, Crossing = 1u << 12, Render = 1u << 13, Mode = 1u << 14,
            Site = 1u << 15;

        /// <summary>S7a picture latch: no frame presented since the Approach entry (<c>presentedSeTick &lt; bandTick</c>).</summary>
        public const uint Band = 1u << 16;
        /// <summary>S7a picture latch: the view is Space now or was on the previous tick (the margins request not issued yet).</summary>
        public const uint NotApproach = 1u << 17;
        /// <summary>S7a picture latch: <c>y ≥ +300 m</c> (an Approach entered early by predicted arrival swaps at the band itself).</summary>
        public const uint High = 1u << 18;

        /// <summary>The parts of <c>snReady</c>.</summary>
        public const uint SnReadyMask = Peer | Phase | AreaBuilt | Settle | Fresh | Rect | UiHidden;
    }

    /// <summary>The render request bits (<see cref="ViewOutputs.Request"/>, the trace's <c>rq</c>).</summary>
    public static class ViewRequest
    {
        public const uint Margins = 1u << 0, SceneSkip = 1u << 1, AlphaFill = 1u << 2, Lowered = 1u << 3;
    }

    /// <summary>
 /// What the SE plugin knows this tick (filled on the update thread, PrepareForDraw postfix, InWorld only).
 /// </summary>
    public struct ViewInputs
    {
        /// <summary>SE tick (<c>MySession.GameplayFrameCounter</c>, the C1 tick).</summary>
        public ulong Tick;
        /// <summary>Unscaled monotonic clock in microseconds (QPC); every timer of the machine runs on it.</summary>
        public long NowUs;

        /// <summary>
 /// Display bridge allowed: link Up, hello published and acknowledged (<c>helloAckEpoch == helloEpoch ≠ 0</c>),
 /// display not refused (version pins, self-tests, site validation; CONTRACT §1.2). False keeps Space.
 /// </summary>
        public bool BridgeUp;
        /// <summary>The camera's closest Water Mod planet is the bound <c>DE_4546B</c> (ARCHITECTURE §4.1 <c>onSite</c>).</summary>
        public bool OnSite;
        /// <summary>C5 zone (Wire.Zone) from the map, with the 7,800/7,900 m hysteresis (<see cref="SiteZones.Classify"/>).</summary>
        public uint Zone;
        /// <summary><c>ρ∞</c> of the camera, SN metres.</summary>
        public double RhoInfM;
        /// <summary>SN altitude of the camera (<c>|P − C| − Rs</c>), negative underwater.</summary>
        public double Y;
        /// <summary>Rate of change of <see cref="Y"/>, m per simulation second (C5 <c>camVelSn.y</c>).</summary>
        public double Vy;

        /// <summary><see cref="PeerState"/>.</summary>
        public uint Peer;
        /// <summary>C0 <c>snPhase</c>.</summary>
        public uint SnPhase;
        /// <summary>C7 flags (<c>SnStatusFlags</c>: streamingSettled, areaBuilt, rectOk, uiHidden are read).</summary>
        public uint SnFlags;
        /// <summary>C7 <c>presentedSeTick</c>.</summary>
        public ulong PresentedSeTick;
        /// <summary>C7 <c>areaBuiltSeTick</c>: the SE tick of the camera pose for which areaBuilt was last true.</summary>
        public ulong AreaBuiltSeTick;

        /// <summary>The z-order walker found <c>snHwnd</c> directly under <c>seHwnd</c>.</summary>
        public bool ZOrderOk;
        /// <summary><c>MySandboxGame.IsPaused</c>.</summary>
        public bool Paused;
        /// <summary><c>MyGameWindow.IsActive</c>.</summary>
        public bool Foreground;
        /// <summary>A GUI screen other than gameplay is open.</summary>
        public bool GuiScreenOpen;
        /// <summary>A′ menu rule only: <c>IMyGui.IsCursorVisible ∨ ChatEntryVisible</c>.</summary>
        public bool CursorOrChat;
        /// <summary>SE in a loading phase (LoadingWorld, Saving) or its loading screen: no audio writes.</summary>
        public bool Loading;
        /// <summary>
 /// <c>!MySandboxGame.Config.EnableMuteWhenNotInFocus</c>: SE stays audible unfocused, so the focus condition of
 /// the audio gate does not apply (ARCHITECTURE §5.3 "(or EnableMuteWhenNotInFocus is false)"). Subnautica still
 /// mutes.
 /// </summary>
        public bool KeepAudibleUnfocused;
        /// <summary><see cref="ViewEvents"/> of this tick.</summary>
        public uint Events;

        /// <summary>
 /// The render thread's acknowledgement: the <see cref="ViewOutputs.RenderSeq"/> of the newest request whose
 /// effects have been presented (render ack contract, <see cref="ViewStateMachine"/> remarks).
 /// </summary>
        public uint RenderAckSeq;
        /// <summary>
 /// The render thread's live margins state: the DWM margins −1 are applied
 /// (S_OK) on SE's window and at least one frame was presented since, now. Not latched: false again as soon as the
 /// margins are dropped (a failed DWM call, a lost window, <c>de.display margins=off</c>). Prime → Underwater needs
 /// it on top of the acknowledged margins request (the acknowledgement itself is sticky and, for a request without
 /// scene skip, only says that a scene-drawn frame was presented under it). Trace key <c>mrd</c>.
 /// </summary>
        public bool MarginsReady;
        /// <summary>Wire.DisplayMode: F (design F), APrime (fallback A′), C (contingency: never see-through here).</summary>
        public uint DisplayMode;
        /// <summary><see cref="ViewMutants"/> (seeded failures; 0 in normal play).</summary>
        public uint Mutants;
        /// <summary>
 /// S7a picture policy (<see cref="ViewPicture"/>; default 0 = <see cref="ViewPicture.Site"/>). SE: env
 /// <c>DEEPENGINEERS_PICTURE</c>, <c>de.s7 picture</c>. Any value but Waterline runs Site. Trace key <c>pp</c>.
 /// </summary>
        public uint Picture;
    }

    /// <summary>What the machine decided this tick.</summary>
    public struct ViewOutputs
    {
        /// <summary>C5 viewState (Wire.ViewState).</summary>
        public uint ViewState;
        /// <summary>
 /// The machine's approach sub-state (Wire.ApproachMode): Prime also while Underwater/Surfacing (its margins stay
 /// on), None in Space. C5 <c>approachMode</c> is this value only while <see cref="ViewState"/> is Approach and 0
 /// otherwise (CONTRACT §5.4; <see cref="ViewStateMachine.C5ApproachMode"/>, used by <see cref="ViewStateMachine.ApplyTo"/>).
 /// </summary>
        public uint ApproachMode;
        /// <summary>C5 perfMode (Wire.PerfMode) and targetFps.</summary>
        public uint PerfMode;
        public uint TargetFps;

        /// <summary>Render request (to the render thread): DWM margins −1 (F only).</summary>
        public bool MarginsOn;
        /// <summary>Render request: skip SE's 3D scene (F: backbuffer cleared to (0,0,0,0); A′: <c>DrawEnabled</c> false).</summary>
        public bool SceneSkip;
        /// <summary>Render request: alpha-only fill to 1 after the sprites (F, while margins may be on and the scene is drawn).</summary>
        public bool AlphaFill;
        /// <summary>Render request, A′ only: SE lowered below Subnautica.</summary>
        public bool SeLowered;
        /// <summary>The request bits (<see cref="ViewRequest"/>) and their sequence number (bumped on every change).</summary>
        public uint Request;
        public uint RenderSeq;

        /// <summary>One-tick pulse: (re)start the z-order re-assert sequence (Prime entry, re-activation, z-order lost).</summary>
        public bool ZOrderReassert;
        /// </summary>
        public bool Freeze;
        /// <summary>C5 crossingTick: SE tick of the latest downward waterline crossing.</summary>
        public ulong CrossingTick;
        /// <summary>ARCHITECTURE §4.1 snReady (diagnostic).</summary>
        public bool SnReady;
        /// <summary><see cref="ViewBlock"/> bits failing this tick.</summary>
        public uint Blocked;
        /// <summary>C5 overlays (Wire.Overlays).</summary>
        public uint Overlays;
        /// <summary>The C5 flag bits the machine owns (<see cref="ViewStateMachine.OwnedC5Flags"/>).</summary>
        public uint C5Flags;

        /// <summary>C5 snBusTarget (Subnautica fades to it over <see cref="CrossfadeMs"/>) and crossfadeMs.</summary>
        public float SnBusTarget;
        public float CrossfadeMs;
        /// <summary>Current SE factors (multiply the config volumes; already crossfaded on the unscaled clock).</summary>
        public float SeGameFactor;
        public float SeMusicFactor;
        /// <summary>Water Mod underwater sound factor (0 underwater: emitters scaled; settings never written).</summary>
        public float WaterModFactor;
        /// <summary>The UpdateSound postfix may write SE volumes this tick (focused, unpaused, no GUI screen, not loading).</summary>
        public bool SeAudioWrite;
        /// <summary>C5 seMixActive: the plugin's mix differs from the config volumes.</summary>
        public bool SeMixActive;
        /// <summary>The crossfade level 0 (above-water mix).. 1 (underwater mix).</summary>
        public double MixLevel;

        /// <summary><see cref="ViewReason"/> of the last transition this tick (None if none; Space → Approach → Prime on one tick reports EnterPrime).</summary>
        public uint Reason;
        /// <summary>viewState or approachMode changed this tick.</summary>
        public bool Changed;

        /// <summary>
 /// S7a: Subnautica's picture is requested this tick (<c>Site</c>: the picture latch; <c>Waterline</c>: view =
 /// Underwater). Trace key <c>o_sp</c>.
 /// </summary>
        public bool Picture;
        /// <summary>
 /// S7a: +1 when <see cref="Picture"/> turned on this tick (SE → Subnautica), −1 when it turned off, else 0: the
 /// crossing fade's trigger under both policies (<c>Waterline</c>: the Underwater entry and exit). Trace key <c>o_pe</c>.
 /// </summary>
        public int PictureEdge;
        /// <summary>
 /// S7a: why the picture is not on (diagnostic; 0 while it is). <c>Site</c>: the latch's failing on-edge guards
 /// (<see cref="ViewBlock"/> bits incl. Band/NotApproach/High); <c>Waterline</c>: <see cref="Blocked"/> (the
 /// Underwater guard). Trace key <c>o_pb</c>.
 /// </summary>
        public uint PictureBlocked;
        /// <summary>S7a: <see cref="ViewReason"/> PictureOn … PictureOffResettle when the latch changed this tick (<c>Site</c> only). Trace key <c>o_prs</c>.</summary>
        public uint PictureReason;
        /// <summary>S7a: the effective picture policy (<see cref="ViewStateMachine.EffectivePicture"/>; C5 flag bit 9). Trace key <c>o_pp</c>.</summary>
        public uint PicturePolicy;
    }

    /// <summary>
 /// Everything the machine remembers between ticks (a plain struct: copy it to snapshot, log it to reseed the oracle).
 /// <c>default(ViewMachineState)</c> is the initial state; the first Step counts as a world load.
 /// </summary>
    public struct ViewMachineState
    {
        /// <summary>Number of Step calls before this one.</summary>
        public ulong Steps;
        public uint View;
        public uint Approach;
        /// <summary>An Approach ↔ Underwater switch happened since the world load; <see cref="LastSwitchUs"/> is its time.</summary>
        public bool HasSwitched;
        public long LastSwitchUs;
        /// <summary>Time a Prime condition last held (Prime only).</summary>
        public long PrimeCondUs;
        /// <summary>Waterline latch (below once <c>y ≤ −0.20</c>, above again once <c>y &gt; −0.05</c>).</summary>
        public bool Below;
        public ulong CrossingTick;
        /// <summary>A teleport, new generation, link recovery or world load at <see cref="ResettleTick"/> awaits a fresh streamingSettled.</summary>
        public bool ResettlePending;
        public ulong ResettleTick;
        /// <summary>The current render request (<see cref="ViewRequest"/> bits) and its sequence number.</summary>
        public uint RenderSeq;
        public uint Request;
        /// <summary>The render thread acknowledged a request with margins on (and none with margins off since).</summary>
        public bool MarginsApplied;
        public bool PrevForeground;
        public bool PrevZOrderOk;
        /// <summary>Audio crossfade: level at <see cref="MixStartUs"/>, and its direction.</summary>
        public double MixFrom;
        public long MixStartUs;
        public bool MixToUnderwater;

        /// <summary>
 /// S7a: the picture latch (<c>Site</c>); under <c>Waterline</c> it follows view = Underwater, so a policy switch
 /// and the edge output stay consistent. Trace key <c>s_sp</c>.
 /// </summary>
        public bool Picture;
        /// <summary>S7a: the SE tick of the last Space → Approach transition (the latch needs <c>presentedSeTick ≥ BandTick</c>). Trace key <c>s_bt</c>.</summary>
        public ulong BandTick;
        /// <summary>S7a: the latch changed since the plugin started; <see cref="PicSwitchUs"/> is its time (the picture dwell). Trace keys <c>s_ps</c>, <c>s_pt</c>.</summary>
        public bool PicSwitched;
        public long PicSwitchUs;
        /// <summary>F15: the altitude exit condition has held since <see cref="LeftSinceUs"/> (Approach → Space after 1 s).</summary>
        public bool LeftArmed;
        public long LeftSinceUs;
    }

    /// <summary>
 /// The step function. Transition rules (ARCHITECTURE §4.2, ROADMAP S2 state table):
 /// <list type="bullet">
 /// <item>Space → Approach/Track: bridge up, on site, zone CORE/TILE VOID/OUTER VOID, peer Alive, <c>y &lt; +300</c>;
 /// Prime is checked on the same tick.</item>
 /// <item>Approach → Space: any of those lost, or <c>y &gt; +350</c> (margins off with it).</item>
 /// <item>Track → Prime: <c>y ≤ +1</c>, or <c>y ≤ +5 ∧ vy ≤ −0.2</c>, or <c>y ≤ 4·(−vy)</c> with <c>vy &lt; 0</c>.
 /// Prime → Track: no Prime condition for 3 s and <c>y &gt; +1</c>.</item>
 /// <item>Prime (held since an earlier tick) → Underwater: <c>y ≤ −0.20</c> ∧ <c>ρ∞ ≤ 7,800</c> ∧ snReady ∧ zOrderOk ∧
 /// dwell ≥ 0.25 s ∧ <c>presentedSeTick ≥ crossingTick</c> ∧ (F) the margins-on request presented (render ack) ∧ the
 /// margins still applied now (<see cref="ViewInputs.MarginsReady"/>).</item>
 /// <item>Underwater → Surfacing: at once on site/zone/bridge loss, peer not Alive, or not snReady while a teleport
 /// (or new generation, link recovery) awaits its settle; after the dwell on <c>y &gt; −0.05</c>.</item>
 /// <item>Surfacing → Prime (Approach rules hold) or Space: once the scene-drawn request has been presented.</item>
 /// </list>
 /// snReady = peer Alive ∧ snPhase Active ∧ areaBuilt ∧ rectOk ∧ uiHidden ∧ <c>tick − 4 ≤ presentedSeTick ≤
 /// tick + 1</c> ∧ no pending settle; a pending settle (set by the events, cleared once observed) needs C7
 /// streamingSettled ∧ areaBuilt with <c>ResettleTick ≤ areaBuiltSeTick ≤ tick + 1</c> while the peer is Alive.
 /// <para>
 /// Crossing order through the render ack contract. The machine publishes a request (margins, scene skip, alpha fill,
 /// A′ lowering) and bumps <see cref="ViewOutputs.RenderSeq"/> on every change. The display unit on SE's render
 /// thread reads it each frame and reports in <see cref="ViewInputs.RenderAckSeq"/> the sequence number of a request
 /// once (1) scene skip: a frame with the scene skipped was presented under it (F: only possible with the margins on
 /// for at least one presented frame); (2) scene drawn: a frame with the scene drawn was presented under it; (3) margins off: applied after presenting a scene-drawn
 /// frame. The machine skips the scene only once a margins-on request is acknowledged and the margins are applied
 /// now (<see cref="ViewInputs.MarginsReady"/>), and drops margins only after the scene-drawn request of Surfacing is
 /// acknowledged, so no frame presents a cleared backbuffer without margins.
 /// </para>
 /// Overlays (Paused, Unfocused) never change the state; they gate the audio writes, the freeze and the frame rate.
 /// Seeded failures switch through <see cref="ViewInputs.Mutants"/>: S2.M4 (one waterline, no dwell), S2.M10
 /// (snReady needs streamingSettled at all times), S2.M11 (SE volumes written without the gate), S7a.M1 (the
 /// <c>Waterline</c> policy while <c>Site</c> is selected).
 /// <para>
 /// Picture policy (S7a; ARCHITECTURE §4.2 "Picture policy (S7a)"). The rules above are the policy <c>Waterline</c>
 /// (S2) and stay as they are under it. Under <c>Site</c> (the default) the states and transitions are the same and
 /// Underwater still gates the audio mix (and, outside the machine, the S5a oxygen and arms, the swim model and the S6
 /// proxies); what changes is the picture, through a latch (<see cref="ViewMachineState.Picture"/>), in this order:
 /// (1) events, render ack, waterline latch, snReady as above; (2) the latch drops at once on a site loss, a peer not
 /// Alive, or a pending re-settle with ¬snReady; (3) transitions, where the Prime → Underwater render guard is "latch
 /// off" (it replaces the margins-ready bit, which needs the scene drawn) and Surfacing completes on the
 /// acknowledgement of a request that either keeps the latch's picture or draws the scene; Space → Approach stamps
 /// <see cref="ViewMachineState.BandTick"/>; (4) the latch: off in Space; on when the view was not Space on the previous
 /// tick either, <c>y &lt; +300 m</c>, site ok, <c>ρ∞ ≤ 7,800 m</c>, snReady, zOrderOk, 0.25 s since the last latch
 /// change, <c>presentedSeTick ≥ BandTick</c>, (F) the margins-on request with the scene drawn presented and the margins
 /// applied now, and design F or A′; (5) the request: F margins in every non-Space state (and in Space until a
 /// scene-drawn request was presented after a skip: the S2 crossing order), skip = latch, alpha fill while the scene is
 /// drawn under margins; A′ lowered = latch ∧ no menu, skip = lowered, kept lowered without skip until a drawn frame is
 /// presented when the latch drops. Perf: Idle in Space, Full elsewhere (<see cref="PerfFor"/>); 30 fps while the
 /// picture is shown and SE is unfocused. The crossing fade follows <see cref="ViewOutputs.PictureEdge"/>.
 /// </para>
 /// </summary>
    public static class ViewStateMachine
    {
        /// <summary>
 /// C5 flag bits written from <see cref="ViewOutputs.C5Flags"/>; the caller keeps seExiting and cameraInAirtightRoom.
 /// S7a adds bit 9 <c>pictureSite</c> (<see cref="S7aConventions.C5FlagsExt.PictureSite"/>).
 /// </summary>
        public const uint OwnedC5Flags = SeStateFlags.SeForeground | SeStateFlags.SePaused | SeStateFlags.SeeThrough |
                                         SeStateFlags.MarginsOn | SeStateFlags.ZOrderOk | SeStateFlags.SeMixActive |
                                         SeStateFlags.GuiScreenOpen | S7aConventions.C5FlagsExt.PictureSite;

        /// <summary>One SE tick. Pure: the result depends only on <paramref name="s"/> and <paramref name="i"/>. Allocates nothing.</summary>
        public static void Step(ref ViewMachineState s, ref ViewInputs i, out ViewOutputs o)
        {
            o = default(ViewOutputs);
            long now = i.NowUs;
            bool mutM4 = (i.Mutants & ViewMutants.M4NoHysteresis) != 0;
            uint policy = EffectivePicture(i.Picture, i.Mutants);
            bool site = policy == ViewPicture.Site;
            bool prevPicture = s.Picture;
            uint picReason = ViewReason.None;
            uint ev = i.Events;
            if (s.Steps == 0) ev |= ViewEvents.WorldLoad;
            uint reason = ViewReason.None;

            // ---- events
            if ((ev & ViewEvents.WorldLoad) != 0)
            {
                // the render request, its acknowledgement and the audio ramp are physical state and carry on
                s.View = ViewState.Space;
                s.Approach = ApproachMode.None;
                s.HasSwitched = false;
                s.LastSwitchUs = 0;
                s.PrimeCondUs = 0;
                s.Below = false;
                s.CrossingTick = 0;
                s.PrevZOrderOk = false;
                reason = ViewReason.WorldLoad;
            }
            if ((ev & (ViewEvents.WorldLoad | ViewEvents.Teleport | ViewEvents.NewGeneration | ViewEvents.LinkRecovery)) != 0)
            {
                s.ResettlePending = true;
                s.ResettleTick = i.Tick;
            }

            // ---- render acknowledgement of the previous request
            bool renderAcked = i.RenderAckSeq == s.RenderSeq;
            if (renderAcked) s.MarginsApplied = (s.Request & ViewRequest.Margins) != 0;

            // ---- waterline latch and crossingTick
            double y = i.Y;
            double vy = double.IsNaN(i.Vy) || double.IsInfinity(i.Vy) ? 0.0 : i.Vy;
            bool yFinite = !double.IsNaN(y) && !double.IsInfinity(y);

            // the request is presented); the mutant M4 waterline keeps the raw y so S2.M4 stays what its test says
            double yLead = yFinite ? y + vy * ViewThresholds.UnderwaterLeadS : y;
            bool belowEnter = mutM4 ? y <= ViewThresholds.MutantM4WaterlineY : yLead <= ViewThresholds.UnderwaterEnterY;
            bool aboveExit = mutM4 ? y > ViewThresholds.MutantM4WaterlineY : yLead > ViewThresholds.UnderwaterExitY;
            if (!s.Below && belowEnter)
            {
                s.Below = true;
                s.CrossingTick = i.Tick;
            }
            else if (s.Below && aboveExit)
            {
                s.Below = false;
            }
            if (s.Below && (ev & ViewEvents.Teleport) != 0) s.CrossingTick = i.Tick;

            // ---- site, peer and snReady
            bool siteOk = i.BridgeUp && i.OnSite && SiteZones.IsDive(i.Zone) && yFinite;
            bool peerOk = i.Peer == PeerState.Alive;
            bool area = (i.SnFlags & SnStatusFlags.AreaBuilt) != 0;
            bool settled = (i.SnFlags & SnStatusFlags.StreamingSettled) != 0;
            ulong aheadLimit = i.Tick + ViewThresholds.AheadTicks;
            if (s.ResettlePending && peerOk && area && i.AreaBuiltSeTick >= s.ResettleTick && i.AreaBuiltSeTick <= aheadLimit &&
                (settled || i.Tick >= s.ResettleTick + ViewThresholds.ResettleSettleCapTicks))
                s.ResettlePending = false;

            uint blocked = 0;
            if (!peerOk) blocked |= ViewBlock.Peer;
            if (i.SnPhase < ViewThresholds.ReadyPhaseMin || i.SnPhase > ViewThresholds.ReadyPhaseMax) blocked |= ViewBlock.Phase;
            if (!area) blocked |= ViewBlock.AreaBuilt;
            if (s.ResettlePending || ((i.Mutants & ViewMutants.M10SettledAlways) != 0 && !settled)) blocked |= ViewBlock.Settle;
            if (i.PresentedSeTick + ViewThresholds.FreshnessTicks < i.Tick || i.PresentedSeTick > aheadLimit) blocked |= ViewBlock.Fresh;
            if ((i.SnFlags & SnStatusFlags.RectOk) == 0) blocked |= ViewBlock.Rect;
            if ((i.SnFlags & SnStatusFlags.UiHidden) == 0) blocked |= ViewBlock.UiHidden;
            bool snReady = (blocked & ViewBlock.SnReadyMask) == 0;

            bool dwellOk = mutM4 || !s.HasSwitched || now - s.LastSwitchUs >= ViewThresholds.MinDwellUs;
            bool primeCond = PrimeCondition(y, vy);
            uint mode = i.DisplayMode;
            bool modeF = mode == DisplayMode.F;
            bool modeA = mode == DisplayMode.APrime;
            // review fix (finding 11): the acknowledgement is sticky, so the live margins state is required too
            bool marginsReady = (s.Request & ViewRequest.Margins) != 0 && (s.Request & ViewRequest.SceneSkip) == 0 && renderAcked && i.MarginsReady;

            // ---- S7a (Site) step 2: the picture latch drops at once (no dwell) on a site, peer or re-streaming loss
            if (site && s.Picture)
            {
                uint off = !siteOk ? ViewReason.PictureOffSite
                         : !peerOk ? ViewReason.PictureOffPeer
                         : s.ResettlePending && !snReady ? ViewReason.PictureOffResettle
                         : ViewReason.None;
                if (off != ViewReason.None)
                {
                    s.Picture = false;
                    s.PicSwitched = true;
                    s.PicSwitchUs = now;
                    picReason = off;
                }
            }

            // the rest of the Prime → Underwater guard (diagnostic in every state)
            if (!siteOk) blocked |= ViewBlock.Site;
            if (!belowEnter) blocked |= ViewBlock.Y;
            if (!(i.RhoInfM <= ViewThresholds.UnderwaterMaxRhoM)) blocked |= ViewBlock.Rho;
            if (!i.ZOrderOk) blocked |= ViewBlock.ZOrder;
            if (!dwellOk) blocked |= ViewBlock.Dwell;
            if (i.PresentedSeTick < s.CrossingTick) blocked |= ViewBlock.Crossing;
            if (!modeF && !modeA) blocked |= ViewBlock.Mode;
            else if (site) { if (!s.Picture) blocked |= ViewBlock.Render; }   // S7a: Underwater only under the picture latch
            else if (modeF && !marginsReady) blocked |= ViewBlock.Render;

            uint prevView = s.View, prevApproach = s.Approach;
            if (!(prevView == ViewState.Approach && prevApproach == ApproachMode.Prime)) blocked |= ViewBlock.NotPrime;

            // ---- transitions
            if (s.View == ViewState.Space)
            {
                if (siteOk && peerOk && ApproachCondition(y, vy))
                {
                    s.View = ViewState.Approach;
                    s.Approach = ApproachMode.Track;
                    s.BandTick = i.Tick;   // S7a: the picture latch needs a frame presented since this tick
                    reason = ViewReason.EnterApproach;
                }
            }

            if (s.View == ViewState.Approach)
            {
                // F15: the altitude exit holds for 1 s (a craft hovering at the band with vy swinging
 // toggled Space/Approach every few ticks: park/unpark, Idle/Full churn, bandTick re-stamped)
                bool leftAlt = ApproachLeft(y, vy);
                if (!leftAlt) s.LeftArmed = false;
                else if (!s.LeftArmed) { s.LeftArmed = true; s.LeftSinceUs = now; }
                bool leftHeld = leftAlt && now - s.LeftSinceUs >= ViewThresholds.ApproachLeaveDwellUs;
                if (!siteOk || !peerOk || leftHeld)
                {
                    s.View = ViewState.Space;
                    s.Approach = ApproachMode.None;
                    s.LeftArmed = false;
                    reason = !siteOk ? ViewReason.LeaveSite : !peerOk ? ViewReason.LeavePeer : ViewReason.LeaveAltitude;
                }
                else if (s.Approach == ApproachMode.Track)
                {
                    if (primeCond)
                    {
                        s.Approach = ApproachMode.Prime;
                        s.PrimeCondUs = now;
                        reason = ViewReason.EnterPrime;
                    }
                }
                else
                {
                    if (primeCond) s.PrimeCondUs = now;
                    else if (y > ViewThresholds.PrimeY && now - s.PrimeCondUs >= PrimeQuietTimeoutUs(y))
                    {
                        s.Approach = ApproachMode.Track;
                        reason = ViewReason.PrimeQuiet;
                    }
                    // Underwater only from a Prime held since an earlier tick (its margins request issued before):
 // the NotPrime bit covers that, every other bit is a failing guard
                    if (s.Approach == ApproachMode.Prime && blocked == 0)
                    {
                        s.View = ViewState.Underwater;
                        s.HasSwitched = true;
                        s.LastSwitchUs = now;
                        reason = ViewReason.EnterUnderwater;
                    }
                }
            }
            else if (prevView == ViewState.Underwater)
            {
                uint why = ViewReason.None;
                if (!siteOk) why = ViewReason.SurfaceSite;
                else if (!peerOk) why = ViewReason.SurfacePeer;
                else if (s.ResettlePending && !snReady) why = ViewReason.SurfaceResettle;
                else if (aboveExit && dwellOk) why = ViewReason.SurfaceWaterline;
                if (why != ViewReason.None)
                {
                    s.View = ViewState.Surfacing;
                    s.HasSwitched = true;
                    s.LastSwitchUs = now;
                    reason = why;
                }
            }
            else if (prevView == ViewState.Surfacing)
            {
                // S7a (Site): under the latch the unchanged request (Subnautica's picture) completes Surfacing once it is
 // acknowledged (one tick); after an immediate exit the latch is off and this is S2's scene-drawn rule
                bool scenePresented = renderAcked && ((site && s.Picture) || (s.Request & ViewRequest.SceneSkip) == 0);
                if (scenePresented)
                {
                    if (siteOk && peerOk && !(y > ViewThresholds.ApproachExitY))
                    {
                        s.View = ViewState.Approach;
                        s.Approach = ApproachMode.Prime;
                        s.PrimeCondUs = now;
                        reason = ViewReason.SurfacedToPrime;
                    }
                    else
                    {
                        s.View = ViewState.Space;
                        s.Approach = ApproachMode.None;
                        reason = ViewReason.SurfacedToSpace;
                    }
                }
            }

            // ---- S7a step 4: the picture latch (Site), or the picture = Underwater (Waterline)
            uint picBlocked;
            if (site)
            {
                if (s.View == ViewState.Space && s.Picture)
                {
                    s.Picture = false;
                    s.PicSwitched = true;
                    s.PicSwitchUs = now;
                    picReason = ViewReason.PictureOffSpace;
                }
                uint pb = 0;
                if (s.View == ViewState.Space || prevView == ViewState.Space) pb |= ViewBlock.NotApproach;
                if (!(y < ViewThresholds.ApproachEnterY)) pb |= ViewBlock.High;
                if (!siteOk) pb |= ViewBlock.Site;
                if (!(i.RhoInfM <= ViewThresholds.UnderwaterMaxRhoM)) pb |= ViewBlock.Rho;
                pb |= blocked & ViewBlock.SnReadyMask;
                if (!i.ZOrderOk) pb |= ViewBlock.ZOrder;
                if (s.PicSwitched && now - s.PicSwitchUs < ViewThresholds.PictureDwellUs) pb |= ViewBlock.Dwell;
                if (i.PresentedSeTick < s.BandTick) pb |= ViewBlock.Band;
                if (!modeF && !modeA) pb |= ViewBlock.Mode;
                else if (modeF && !marginsReady) pb |= ViewBlock.Render;
                if (!s.Picture && pb == 0)
                {
                    s.Picture = true;
                    s.PicSwitched = true;
                    s.PicSwitchUs = now;
                    picReason = ViewReason.PictureOn;
                }
                picBlocked = s.Picture ? 0u : pb;
            }
            else
            {
                bool under = s.View == ViewState.Underwater;
                if (under != s.Picture)
                {
                    s.Picture = under;
                    s.PicSwitched = true;
                    s.PicSwitchUs = now;
                }
                picBlocked = s.Picture ? 0u : blocked;
            }

            // ---- render request (crossing order: see the class remarks)
            bool margins = false, skip = false, alpha = false, lowered = false;
            if (site)
            {
                if (modeF)
                {
                    // margins in every non-Space state; after a skip they stay until a scene-drawn request is presented
 // (Space entered with the latch on: the scene is drawn first, then the margins go)
                    bool hold = (s.Request & ViewRequest.Margins) != 0 && ((s.Request & ViewRequest.SceneSkip) != 0 || !renderAcked);
                    margins = s.View != ViewState.Space || hold;
                    skip = s.Picture;
                    alpha = !skip && (margins || s.MarginsApplied);
                }
                else if (modeA)
                {
                    bool menu = i.CursorOrChat || i.GuiScreenOpen || i.Paused;
                    if (s.Picture && !menu)
                    {
                        lowered = true;
                        skip = true;
                    }
                    else
                    {
                        // the latch dropped: stay lowered (drawing) until a drawn frame was presented, then raise; a menu
 // raises at once as in S2
                        bool drawnPresented = renderAcked && (s.Request & ViewRequest.SceneSkip) == 0;
                        lowered = (s.Request & ViewRequest.Lowered) != 0 && !menu && !drawnPresented;
                    }
                }
            }
            else if (modeF)
            {
                margins = IsPrimeLike(s.View, s.Approach);
                skip = s.View == ViewState.Underwater;
                alpha = !skip && (margins || s.MarginsApplied);
            }
            else if (modeA)
            {
                bool menu = i.CursorOrChat || i.GuiScreenOpen || i.Paused;
                if (s.View == ViewState.Underwater) lowered = !menu;
                else if (s.View == ViewState.Surfacing) lowered = (s.Request & ViewRequest.Lowered) != 0;
                skip = s.View == ViewState.Underwater && lowered;
            }
            uint request = (margins ? ViewRequest.Margins : 0u) | (skip ? ViewRequest.SceneSkip : 0u) |
                           (alpha ? ViewRequest.AlphaFill : 0u) | (lowered ? ViewRequest.Lowered : 0u);
            if (request != s.Request)
            {
                s.Request = request;
                s.RenderSeq = unchecked(s.RenderSeq + 1);
            }

            // ---- z-order re-assert pulse (when the margins become wanted, on re-activation, on a lost z-order)
            bool wanted = MarginsWanted(s.View, s.Approach, policy);
            bool prevWanted = MarginsWanted(prevView, prevApproach, policy);
            bool zPulse = wanted && (!prevWanted || (i.Foreground && !s.PrevForeground) || (!i.ZOrderOk && s.PrevZOrderOk));
            s.PrevForeground = i.Foreground;
            s.PrevZOrderOk = i.ZOrderOk;

            // ---- Subnautica mode
 // F16: display mode C can never show Subnautica's picture: Waterline's perf table there
            uint perf = PerfFor(s.View, s.Approach, (!modeF && !modeA) ? ViewPicture.Waterline : policy);
            // F1: on the band exit SE's scene is not back on screen yet (F: the skip-off request
 // unacknowledged; A′: still lowered). Subnautica keeps rendering Full (no freeze, no parking) until then.
            if (site && s.View == ViewState.Space && (margins || lowered)) perf = PerfMode.Full;
            uint fps = perf == PerfMode.Idle ? ViewThresholds.IdleTargetFps
                     : perf == PerfMode.Track ? ViewThresholds.TrackTargetFps
                     : ViewThresholds.FullTargetFps;
            if (s.Picture && !i.Foreground) fps = ViewThresholds.UnfocusedUnderwaterFps;   // S2: Underwater; S7a Site: the latch

 // ---- audio (ARCHITECTURE §5.3): the mix follows Underwater (the waterline hysteresis), 300 ms linear ramps
            bool mixUw = s.View == ViewState.Underwater;
            if (mixUw != s.MixToUnderwater)
            {
                s.MixFrom = AudioMix.Level(s.MixFrom, s.MixToUnderwater, s.MixStartUs, now);
                s.MixStartUs = now;
                s.MixToUnderwater = mixUw;
            }
            double m = AudioMix.Level(s.MixFrom, s.MixToUnderwater, s.MixStartUs, now);

            uint overlays = 0;
            if (i.Paused) overlays |= Overlays.Paused;
            if (!i.Foreground) overlays |= Overlays.Unfocused;
            if (i.Peer == PeerState.Stalled) overlays |= Overlays.PeerStalled;
            if (i.Peer == PeerState.Exited) overlays |= Overlays.PeerExited;

            bool mixActive = m > 0.0;
            uint flags = 0;
            if (i.Foreground) flags |= SeStateFlags.SeForeground;
            if (i.Paused) flags |= SeStateFlags.SePaused;
            if (skip) flags |= SeStateFlags.SeeThrough;
            if (margins) flags |= SeStateFlags.MarginsOn;
            if (i.ZOrderOk) flags |= SeStateFlags.ZOrderOk;
            if (mixActive) flags |= SeStateFlags.SeMixActive;
            if (i.GuiScreenOpen) flags |= SeStateFlags.GuiScreenOpen;
            if (site) flags |= S7aConventions.C5FlagsExt.PictureSite;

            // ---- outputs
            o.ViewState = s.View;
            o.ApproachMode = s.Approach;
            o.PerfMode = perf;
            o.TargetFps = fps;
            o.MarginsOn = margins;
            o.SceneSkip = skip;
            o.AlphaFill = alpha;
            o.SeLowered = lowered;
            o.Request = request;
            o.RenderSeq = s.RenderSeq;
            o.ZOrderReassert = zPulse;
            o.Freeze = perf == PerfMode.Idle || i.Paused;
            o.CrossingTick = s.CrossingTick;
            o.SnReady = snReady;
            o.Blocked = blocked;
            o.Overlays = overlays;
            o.C5Flags = flags;
            o.SnBusTarget = mixUw && i.Foreground ? (float)AudioMix.SnBusUnderwater : 0f;
            o.CrossfadeMs = AudioMix.CrossfadeMs;
            o.SeGameFactor = (float)AudioMix.Lerp(1.0, AudioMix.SeGameUnderwater, m);
            o.SeMusicFactor = (float)AudioMix.Lerp(1.0, AudioMix.SeMusicUnderwater, m);
            o.WaterModFactor = (float)AudioMix.Lerp(1.0, AudioMix.WaterModUnderwater, m);
            o.SeAudioWrite = AudioMix.WriteGate(i.Foreground || i.KeepAudibleUnfocused, i.Paused, i.GuiScreenOpen, i.Loading) ||
                             (i.Mutants & ViewMutants.M11NoAudioGate) != 0;
            o.SeMixActive = mixActive;
            o.MixLevel = m;
            o.Reason = reason;
            o.Changed = s.View != prevView || s.Approach != prevApproach;
            o.Picture = s.Picture;
            o.PictureEdge = s.Picture == prevPicture ? 0 : s.Picture ? 1 : -1;
            o.PictureBlocked = picBlocked;
            o.PictureReason = site ? picReason : ViewReason.None;
            o.PicturePolicy = policy;

            s.Steps++;
        }

        /// <summary>
 /// The picture policy the machine runs (S7a): <see cref="ViewPicture.Waterline"/> when selected or under the seeded
 /// failure S7a.M1 (<see cref="ViewMutants.S7aM1Waterline"/>), else <see cref="ViewPicture.Site"/> (any other value).
 /// </summary>
        public static uint EffectivePicture(uint picture, uint mutants)
        {
            if (picture == ViewPicture.Waterline || (mutants & ViewMutants.S7aM1Waterline) != 0) return ViewPicture.Waterline;
            return ViewPicture.Site;
        }

        /// <summary>
 /// The view states whose design F margins are wanted (the z-order re-assert pulse fires when this rises): under
 /// <c>Waterline</c> Prime, Underwater and Surfacing (<see cref="IsPrimeLike"/>, S2); under <c>Site</c> every state but
 /// Space (S7a: Subnautica must be composed under SE from the Approach entry so the latch can swap at the band).
 /// <paramref name="picture"/> is the effective policy.
 /// </summary>
        public static bool MarginsWanted(uint view, uint approach, uint picture)
        {
            if (picture == ViewPicture.Waterline) return IsPrimeLike(view, approach);
            return view != ViewState.Space;
        }

        /// <summary>
 /// C5 <c>perfMode</c> of a view state: Idle in Space under both policies; <c>Waterline</c> (S2) Track in
 /// Approach/Track and Full in Prime, Underwater and Surfacing; <c>Site</c> (S7a) Full in every state but Space
 /// (Subnautica renders whenever its picture can be shown; the Track recipe is never requested).
 /// </summary>
        public static uint PerfFor(uint view, uint approach, uint picture)
        {
            if (view == ViewState.Space) return PerfMode.Idle;
            if (picture == ViewPicture.Waterline && view == ViewState.Approach && approach == ApproachMode.Track) return PerfMode.Track;
            return PerfMode.Full;
        }

        /// <summary>Space → Approach: <c>y &lt; +300</c>, or the camera will be below +300 m within <see cref="ViewThresholds.ApproachPredictS"/> (descending).</summary>
        public static bool ApproachCondition(double y, double vy)
        {
            if (y < ViewThresholds.ApproachEnterY) return true;
            return vy < 0.0 && y <= ViewThresholds.ApproachEnterY + ViewThresholds.ApproachPredictS * -vy;
        }

        /// <summary>Approach → Space on altitude: <c>y &gt; +350</c> and not descending onto +350 m within <see cref="ViewThresholds.ApproachPredictS"/>.</summary>
        public static bool ApproachLeft(double y, double vy)
        {
            if (!(y > ViewThresholds.ApproachExitY)) return false;
            return !(vy < 0.0 && y <= ViewThresholds.ApproachExitY + ViewThresholds.ApproachPredictS * -vy);
        }

        /// <summary>Track → Prime conditions: <c>y ≤ +1</c>, or <c>y ≤ +5 ∧ vy ≤ −0.2</c>, or a crossing predicted within 4 s.</summary>
        public static bool PrimeCondition(double y, double vy)
        {
            if (y <= ViewThresholds.PrimeY) return true;
            if (y <= ViewThresholds.PrimeDescentY && vy <= ViewThresholds.PrimeDescentVy) return true;
            return vy < 0.0 && y <= ViewThresholds.PrimePredictS * -vy;
        }

        /// <summary>
 /// Prime → Track quiet timeout at altitude <paramref name="y"/>: <see cref="ViewThresholds.PrimeQuietNearUs"/>
 /// (10 s) while <c>y &lt; +5 m</c>, else <see cref="ViewThresholds.PrimeQuietUs"/> (3 s). The timeout is read at
 /// the current <c>y</c>: rising above +5 m after more than 3 s of quiet drops to Track on that tick.
 /// </summary>
        public static long PrimeQuietTimeoutUs(double y)
        {
            return y < ViewThresholds.PrimeQuietNearY ? ViewThresholds.PrimeQuietNearUs : ViewThresholds.PrimeQuietUs;
        }

        /// <summary>
 /// Seconds until the camera reaches the waterline at its current vertical speed (<c>y / −vy</c>): 0 at or below
 /// it, +∞ when not descending. For logs; <see cref="PrimeCondition"/> compares <c>y ≤ 2·(−vy)</c> instead.
 /// </summary>
        public static double PredictedCrossingS(double y, double vy)
        {
            if (y <= 0.0) return 0.0;
            if (!(vy < 0.0)) return double.PositiveInfinity;
            return y / -vy;
        }

        /// <summary>
 /// C5 <c>approachMode</c> of a (viewState, machine approach sub-state) pair: Track or Prime only while viewState is
 /// Approach, else None (CONTRACT §5.4 "C5 approachMode is Track or Prime only while viewState = Approach, else 0").
 /// </summary>
        public static uint C5ApproachMode(uint viewState, uint approach)
        {
            return viewState == ViewState.Approach ? approach : ApproachMode.None;
        }

        /// <summary>
 /// Approach/Prime, Underwater or Surfacing: the states whose margins are on in design F under the <c>Waterline</c>
 /// policy (S2). Under <c>Site</c> (S7a) the margins are on in every non-Space state (<see cref="MarginsWanted"/>);
 /// Prime keeps its S2 role there (the waterline approach: the Underwater entry is only checked from Prime).
 /// </summary>
        public static bool IsPrimeLike(uint view, uint approach)
        {
            return (view == ViewState.Approach && approach == ApproachMode.Prime) || view == ViewState.Underwater ||
                   view == ViewState.Surfacing;
        }

        /// <summary>
 /// Writes the machine's C5 fields into <paramref name="st"/>: viewState, approachMode, displayMode, overlays,
 /// perfMode, targetFps, snBusTarget, crossfadeMs, crossingTick and the owned flag bits (others kept).
 /// </summary>
        public static void ApplyTo(ref ViewOutputs o, uint displayMode, ref SeState st)
        {
            st.viewState = o.ViewState;
            st.approachMode = C5ApproachMode(o.ViewState, o.ApproachMode);
            st.displayMode = displayMode;
            st.overlays = o.Overlays;
            st.perfMode = o.PerfMode;
            st.targetFps = o.TargetFps;
            st.snBusTarget = o.SnBusTarget;
            st.crossfadeMs = o.CrossfadeMs;
            st.crossingTick = o.CrossingTick;
            st.flags = (st.flags & ~OwnedC5Flags) | (o.C5Flags & OwnedC5Flags);
        }

        public static string ViewName(uint view)
        {
            switch (view)
            {
                case ViewState.Space: return "Space";
                case ViewState.Approach: return "Approach";
                case ViewState.Underwater: return "Underwater";
                case ViewState.Surfacing: return "Surfacing";
                default: return "Unknown";
            }
        }

        public static string ApproachName(uint approach)
        {
            switch (approach)
            {
                case ApproachMode.None: return "none";
                case ApproachMode.Track: return "Track";
                case ApproachMode.Prime: return "Prime";
                default: return "unknown";
            }
        }

        public static string PerfName(uint perf)
        {
            switch (perf)
            {
                case PerfMode.Idle: return "Idle";
                case PerfMode.Track: return "Track";
                case PerfMode.Full: return "Full";
                default: return "unknown";
            }
        }
    }
    /// <summary>
 /// What the Subnautica camera parking reads each frame: C5 validity and <c>perfMode</c> (S7a rule: parked while C5 is
 /// valid and <c>perfMode ≠ Full</c>), and the view state and approach mode for the S2 rule that the seeded failure
 /// S7a.M2 (<see cref="ParkByView"/>) restores.
 /// </summary>
    public struct PerfInputsForPark
    {
        public bool C5Valid;
        public uint View, Approach;
        /// <summary>C5 <c>perfMode</c> (Wire.PerfMode).</summary>
        public uint PerfMode;
        /// <summary>Seeded failure S7a.M2: park by view (Space or Approach/Track) as S2 did.</summary>
        public bool ParkByView;
    }

}
