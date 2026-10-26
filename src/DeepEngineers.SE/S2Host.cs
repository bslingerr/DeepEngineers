using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using VRage.Audio;
using VRage.Game.ModAPI;
using VRage.Input;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>The S2 fields of C5 for one plugin Update (copied into <see cref="SeTickInput"/>).</summary>
    internal struct S2C5
    {
        public bool Active;
        public uint ViewState, ApproachMode, DisplayMode, PerfMode, TargetFps;
        public uint Flags;
        public float SnBusTarget, CrossfadeMs;
        public I32x4 SeRect;
        public ulong CrossingTick;
    }

    /// <summary>
 /// The SE plugin's S2 runtime ("Display + input + audio", ROADMAP §4 S2; ARCHITECTURE §4, §5.1–5.3), game glue:
 /// <list type="bullet">
 /// <item><see cref="Tick"/> (plugin Update, update thread): link facts, GUI/pause/focus state, the dev hotkey (Apps),
 /// the display request (<see cref="DisplayController"/>), the audio decision (<see cref="AudioController"/>), the
 /// Water Mod sound (<see cref="WaterModAudio"/>), render log lines, and the C5 S2 fields.</item>
 /// <item><see cref="OnFrame"/> (the <c>PrepareForDraw</c> postfix through <c>FrameWriter</c>): the view state machine
 /// (<see cref="ViewStateDriver"/>) on this tick's final camera, then the display request again (lowest latency).</item>
 /// <item>The render hooks (<see cref="RenderHooks"/>, render thread) execute every window call.</item>
 /// </list>
 /// Input never leaves SE (ARCHITECTURE §5.2): nothing is forwarded; SE keeps the foreground. Config switch
 /// <c>s2=false</c> (or <c>DEEPENGINEERS_S2=0</c>) keeps the S1 behaviour (view state Space, the S1 perf mode).
 /// Every part has its own guard; an exception disables S2 (logged once), never S0/S1.
 /// </summary>
    internal static class S2Host
    {
        public static S2Config Config { get; private set; }
        public static bool Enabled { get; private set; }
        public static RenderDisplay Display { get; private set; }
        public static DisplayController Controller { get; private set; }
        public static ViewStateDriver Driver { get; private set; }
        // S8 (ROADMAP S8 minimum, S8Rules): the peer-loss fallback, its HUD notice and rows. Replaces review F25's hint
 // (which showed only on a picture edge): the notice now follows ARCHITECTURE §4.3 (at once with Subnautica's
 // picture on screen, after 10 s elsewhere, in a DE world).
        private static S8Rules.Memory _s8;
        private static S8Rules.Decision _s8Last;
        private static Link _linkObj;           // S8: OnFrame re-reads the link facts (this tick's peer state, same frame)
        private static uint _helloLast;
        private static bool _s8PrevLinked;
        private static uint _s8PrevPeer = PeerState.Unknown;
        public static AudioController Audio { get; private set; }

        private static LinkFacts _link;
        private static IntPtr _seHwnd;
        private static bool _inWorld, _cursor, _chat, _gameplayScreen, _terminal, _paused, _focused;
        private static bool _hello;
        private static MyKeys _seeThroughKey;
        private static bool _hasSeeThroughKey;
        private static bool _frameThisTick;
        private static ulong _nextHandleMs;
        private static IntPtr _lostReportedFor;
        private static bool _lostReportedRecoverable;
        /// <summary>Review fix (finding 19): one delegate for the per-tick render log drain.</summary>
        private static readonly Action<string> DrainSink = DeLog.Info;
        private static long _errors;
        private static bool _disabledByError;
        private static string _lastAllowedWhy;
        private static bool _lastAllowed;
        private static bool _bridgeUp;
        private static MyGuiModHelpers _gui;

        /// <summary>
 /// The GUI screen state of C5 bit 8 (cursor, the chat entry, a gameplay screen, the terminal). Review fix
 /// (finding 14): CONTRACT §5.4's overlay table lists chat among the GUI screens that stop SE's volume
 /// writes, so the chat entry counts too.
 /// </summary>
        public static bool GuiScreenOpen { get { return _cursor || _chat || _gameplayScreen || _terminal; } }

        // ======================================================================================================= Init

 /// <summary>Plugin Init, before the Harmony patches: the configuration (S2 off means no S2 patch at all).</summary>
        public static void LoadConfig()
        {
            try
            {
                Config = S2Config.Load();
                Enabled = Config.Enabled;
                DeLog.Info("DE.S2 config " + Config.Describe() + " (state machine: " + ViewStateAdapter.Implementation + ")" +
                           (S2Mutants.Active.Length > 0 ? " MUTANTS=" + S2Mutants.Active : ""));
                if (!Enabled) DeLog.Info("DE.S2 OFF (s2=false): the S1 behaviour (view state Space, the S1 perf mode); no S2 patch, no window, render or audio change");
            }
            catch (Exception e)
            {
                Enabled = false;
                DeLog.Info("DE.Error S2 config " + e.GetType().Name + ": " + e.Message + " (S2 off; S0/S1 unaffected)");
            }
        }

        /// <summary>Plugin Init, after the Harmony patches: instances and start-up facts.</summary>
        public static void Init()
        {
            if (!Enabled) return;
            try
            {
                Display = new RenderDisplay(new SeWindowOps());
                RenderHooks.Gpu = new RenderHooks();
                Display.Gpu = RenderHooks.Gpu;
                RenderHooks.Display = Display;
                ViewBand.ApplyEnv();   //: the picture band is a live setting (env, de.s7 band); before the first step
                PicturePolicy.CurrentViewState = () => Driver != null && Driver.Active ? Driver.Last.ViewState : ViewState.Space;   // review F17
                Controller = new DisplayController(Display, Config) { Log = DeLog.Info, HudHint = ShowHint };
                Driver = new ViewStateDriver { Log = DeLog.Info, DepthAt = DepthAt, TraceSink = Jsonl.WriteLineBuffered, ViewRowSink = Jsonl.WriteLineBuffered };
                DeLog.Info("DE.S7a picture policy " + PicturePolicy.Describe() + " (env " + PicturePolicy.EnvVar + ", de.s7 {picture})");
                Audio = new AudioController { Log = DeLog.Info };
                WaterModAudio.CheckRestoreAtInit();

                if (BuildFlavor.IsDev && !string.IsNullOrEmpty(Config.SeeThroughKey))
                {
                    MyKeys k;
                    if (Enum.TryParse(Config.SeeThroughKey, true, out k)) { _seeThroughKey = k; _hasSeeThroughKey = true; }
                    else DeLog.Info("DE.S2 seeThroughKey '" + Config.SeeThroughKey + "' is not a VRage.Input.MyKeys name: hotkey off");
                }
                DeLog.Info(SeWindow.GpuPreferences());
                DeLog.Info("DE.S2 ready: design " + S2Config.Name(Controller.Mode) + " (hooks: designF=" + S2Patches.DesignFHooksOk +
                           " aPrime=" + S2Patches.APrimeHooksOk + " audio=" + S2Patches.UpdateSoundOk + " optionsSave=" + S2Patches.OptionsSaveOk +
                           ")" + (_hasSeeThroughKey ? "; dev hotkey " + _seeThroughKey + " toggles see-through" +
                                                       (_seeThroughKey == MyKeys.F8 ? " (note: SE also binds F8 to its free spectator; the default is Apps since S5a)" : "") : ""));
            }
            catch (Exception e)
            {
                Enabled = false;
                DeLog.Info("DE.Error S2 Init " + e.GetType().Name + ": " + e.Message + " (S2 off; S0/S1 unaffected)");
            }
        }

        // ===================================================================================================== Update

 /// <summary>Plugin Update (update thread): returns this update's C5 S2 fields.</summary>
        public static S2C5 Tick(ulong nowMs, uint phase, Link link, uint helloEpoch)
        {
            var c5 = new S2C5();
            if (!Enabled || _disabledByError) return c5;
            try
            {
                if (Display != null) Display.DrainLog(DrainSink);
                _inWorld = phase == SePhase.InWorld && !Phases.Loading;
                _hello = helloEpoch != 0;
                ReadGameState();
                if (nowMs >= _nextHandleMs || _seHwnd == IntPtr.Zero)
                {
                    _nextHandleMs = nowMs + 10000;
                    _seHwnd = SeWindow.Handle;
                    string cmp = SeWindow.CompareOnce();
                    if (cmp != null) DeLog.Info(cmp);
                }
                ReadLink(link, helloEpoch);
                _linkObj = link;
                _helloLast = helloEpoch;

                if (!_frameThisTick && (!_inWorld || !FrameWriter.HasC5))
                {
                    Driver.NoFrame(_inWorld ? "site not bound" : "not in world");
                    _bridgeUp = false;
                }
                _frameThisTick = false;

                // dev hotkey (Apps since S5a, config seeThroughKey): S2.T-D1 see-through toggle
                if (_hasSeeThroughKey && _inWorld && !_chat)
                {
                    IMyInput input = MyInput.Static;
                    if (input != null && input.IsNewKeyPressed(_seeThroughKey)) Controller.ToggleSeeThrough(_seeThroughKey.ToString());
                }

                DisplayFeedback fb = Display.Feedback;
                ViewOutputs o = Driver.Last;
                S2State state = Driver.State;
                bool stepped = Driver.Active;
                Controller.Post(ref _link, _seHwnd, state, stepped ? o.Request : 0u, false, _inWorld, MenuOpen, _focused, ref fb, 0,
                                o.PicturePolicy == ViewPicture.Site);
                // S8: the fallback decision of this update (S8Rules table: peer × view × time), the HUD notice "underwater
 // renderer offline" (at once with Subnautica's picture on screen, after 10 s elsewhere; once per loss) and
 // the s8.peer / s8.notice rows. The display guard runs in DisplayController.Post on the same link facts.
                S8Step(nowMs, link, stepped, ref o);

                // PeerExited when Subnautica's window is gone while it was on screen (ARCHITECTURE §4.3). Review fix
 // (finding 3): the render thread debounces a hidden window (250 ms); a destroyed window stays
 // exited (CONTRACT: until a new generation), a hidden one is withdrawn when the same window shows again
                ReportWindowLoss(link, ref fb);
                if (link != null) link.FastExitPoll = S8Rules.ExitPollEveryTick(fb.MarginsOn, fb.Lowered, Controller.LastRequest.Margins, Controller.LastRequest.Lowered);

                // audio: the machine's factors and gate; SE writes them in the UpdateSound postfix. Review fix
 // (finding 10): out of the world, a mix still in SE's volumes is written back once SE may sound
                float cfgGame, cfgMusic;
                ConfigVolumes(out cfgGame, out cfgMusic);
                bool restoreAllowed = (_focused || !ConfigMuteWhenUnfocused()) && !Phases.Loading && !Phases.Saving;
                // S8: on a peer loss the mix goes back at once (no 300 ms ramp): SE's config volumes, the Water Mod's sound
                ViewOutputs ao = o;
                if (_s8Last.Active) S8Rules.RestoreAudio(ref ao);
                AudioDecision ad = Audio.Tick(ref ao, stepped && _inWorld, _bridgeUp, cfgGame, cfgMusic, restoreAllowed);
                AudioPolish.Tick(nowMs, ref ad); // S7 audio polish A1 (AudioPolish.cs): seMixActive of this update for the SelectEffect muffle, s7.audio rows
                WaterModAudio.Tick(nowMs, ad.WaterScale, _inWorld, SiteBinding.Api != null && SiteBinding.Api.Usable && SiteBinding.Api.VersionPinned);

                // C5 (CONTRACT §5.4): the machine's view, perf and audio fields; marginsOn/seeThrough as the render thread applied them
                c5.Active = true;
                c5.ViewState = o.ViewState;
                // CONTRACT §5.4: approachMode is Track/Prime only while viewState = Approach, else 0 (the machine keeps
 // Prime as its sub-state underwater; that stays internal)
                c5.ApproachMode = ViewStateMachine.C5ApproachMode(o.ViewState, o.ApproachMode);
                c5.DisplayMode = ViewStateAdapter.DisplayModeOf(Controller.Mode);
                c5.PerfMode = o.PerfMode;
                c5.TargetFps = o.TargetFps;
                if (fb.LastPresentedSkipped || fb.Lowered) c5.Flags |= SeStateFlags.SeeThrough;
                if (fb.MarginsOn) c5.Flags |= SeStateFlags.MarginsOn;
                if (fb.ZOrderOk) c5.Flags |= SeStateFlags.ZOrderOk;
                if (ad.MixActive) c5.Flags |= SeStateFlags.SeMixActive;
                if (GuiScreenOpen) c5.Flags |= SeStateFlags.GuiScreenOpen;
                if (S7Interior.CameraInRoom) c5.Flags |= SeStateFlags.CameraInAirtightRoom; // S7: C5 bit 4 = C1 flags bit 3 (InteriorProbe.cs)
 // S7a: C5 flag bit 9 pictureSite = the machine's effective picture policy is Site (CONTRACT §5.4 "C5/C7
 // semantics for S7a"); Subnautica reads it for its expected perf mode, the areaBuilt box and the mirror radius
                if (o.PicturePolicy == ViewPicture.Site) c5.Flags |= S7aConventions.C5FlagsExt.PictureSite;
                c5.SnBusTarget = stepped ? ao.SnBusTarget : 0f;
                c5.CrossfadeMs = o.CrossfadeMs > 0f ? o.CrossfadeMs : AudioMix.CrossfadeMs;
                c5.SeRect = fb.SeRectValid ? fb.SeRect : default(I32x4);
                c5.CrossingTick = o.CrossingTick;
            }
            catch (Exception e) { Fail("Tick", e); c5.Active = false; }
            return c5;
        }

        /// <summary>The PrepareForDraw postfix (through FrameWriter): one state-machine step on this tick's final camera.</summary>
        public static void OnFrame(ref FrameDiagnostics d, uint zone, ulong tick, bool teleported)
        {
            if (!Enabled || _disabledByError || Driver == null) return;
            try
            {
                _frameThisTick = true;
                // S8: this tick's peer state (the link ticked after S2Host.Tick in the plugin Update), so a stall or exit
 // found in this update reaches the machine and the display request in the same SE frame
                if (_linkObj != null) ReadLink(_linkObj, _helloLast);
                var f = new FrameFacts { Tick = tick, CamSn = d.CamSn, CamSe = d.CamSe, CamVelSn = d.CamVelSn, Teleported = teleported, Zone = zone };
                DisplayFeedback fb = Display.Feedback;
                string why;
                _bridgeUp = DisplayAllowed(ref fb, out why);
                if (_bridgeUp != _lastAllowed || (!_bridgeUp && why != _lastAllowedWhy))
                {
                    _lastAllowed = _bridgeUp;
                    _lastAllowedWhy = why;
                    DeLog.Info("DE.View bridge " + (_bridgeUp ? "UP (display allowed)" : "not up (" + why + ")"));
                }
                var g = new GameFacts
                {
                    Paused = _paused,
                    Foreground = _focused,
                    GuiScreenOpen = GuiScreenOpen,
                    CursorOrChat = _cursor || _chat,
                    Loading = !_inWorld || Phases.Loading || Phases.Saving,
                    KeepAudibleUnfocused = !ConfigMuteWhenUnfocused(),
                };
                ViewOutputs o = Driver.Step(ref f, ref _link, ref g, ref fb, _bridgeUp, Controller.Mode, ViewStateAdapter.MutantBits());
                Controller.SetMachineFacts(Driver.MachineState.LeftArmed, Driver.Tick); // S7t-se-composite (T1): the crossfade's fade back starts at the leave dwell
 // S7a: the picture edge of this step drives the fade (Site: at the band; Waterline: at the waterline)
                Controller.Post(ref _link, _seHwnd, Driver.State, o.Request, o.ZOrderReassert, _inWorld, MenuOpen, _focused, ref fb,
                                o.PictureEdge, o.PicturePolicy == ViewPicture.Site);
            }
            catch (Exception e) { Fail("OnFrame", e); }
        }

        /// <summary>
 /// S8 (S8Rules): one fallback decision per plugin Update from the link facts and the last machine step; shows the
 /// HUD notice when due, writes <c>s8.peer</c> on every change of the link's peer state and <c>s8.notice</c> when shown.
 /// </summary>
        private static void S8Step(ulong nowMs, Link link, bool stepped, ref ViewOutputs o)
        {
            bool linked = link != null && link.State == LinkState.Up;
            uint peer = linked ? _link.Peer : PeerState.Unknown;
            var si = new S8Rules.Inputs
            {
                Linked = linked,
                Peer = peer,
                ViewState = stepped ? o.ViewState : ViewState.Space,
                PictureOn = stepped && o.Picture,
                InWorld = _inWorld,
                SiteActive = _hello,
                NowMs = nowMs,
            };
            S8Rules.Decision d = S8Rules.Decide(ref si, ref _s8);
            _s8Last = d;
            if (linked != _s8PrevLinked || peer != _s8PrevPeer)
            {
                string reason = !linked ? (link != null ? "link " + link.State : "no link") : peer == PeerState.Exited ? link.PeerExitReason :
                                peer == PeerState.Stalled ? "heartbeat " + link.LastSnHeartbeatAgeMs + " ms old" : peer == PeerState.Alive ? "heartbeat fresh" : "new generation, not judged yet";
                try
                {
                    Jsonl.WriteLineBuffered(S8Rules.PeerRow(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), FrameWriter.LastTick, _s8PrevPeer == uint.MaxValue ? PeerState.Unknown : _s8PrevPeer,
                                                            _s8PrevLinked, peer, linked, linked ? link.LinkEpoch : 0u, linked ? link.LastSnPid : 0u, linked ? link.LastSnGeneration : 0u,
                                                            reason, si.ViewState, si.PictureOn, ref d));
                }
                catch (Exception) { }
                if (d.LostEdge)
                    DeLog.Info("DE.S8 peer lost (" + d.Why + "): scene drawn and margins off on the next frame, audio restored now, picture latch dropped; " +
                               "HUD notice " + (d.NoticeAtMs <= nowMs ? "now" : "in " + (d.NoticeAtMs - nowMs) / 1000 + " s if still lost") + (si.InWorld && si.SiteActive ? "" : " (shown only in a DE world)"));
                else if (d.RecoveredEdge)
                    DeLog.Info("DE.S8 peer back after " + d.LostForMs + " ms (linkEpoch " + (linked ? link.LinkEpoch : 0u) + ", snPid " + (linked ? link.LastSnPid : 0u) +
                               "): the stream gate resends everything after linkAckEpoch/helloAckEpoch; snReady gates the next dive");
                _s8PrevLinked = linked;
                _s8PrevPeer = peer;
            }
            if (d.NoticeNow)
            {
                ShowHint(S8Rules.NoticeText(_s8.LostPeer));
                try
                {
                    Jsonl.WriteLineBuffered(S8Rules.NoticeRow(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), FrameWriter.LastTick, _s8.LostPeer, d.LostForMs, _s8.Urgent, si.ViewState));
                }
                catch (Exception) { }
            }
        }

        /// <summary>The window watch's verdict to the link: report a loss once, withdraw a hidden-window loss on recovery.</summary>
        private static void ReportWindowLoss(Link link, ref DisplayFeedback fb)
        {
            if (link == null) return;
            IntPtr sn = _link.SnHwnd;
            if (fb.SnWindowLost && sn != IntPtr.Zero && _lostReportedFor != sn)
            {
                _lostReportedFor = sn;
                _lostReportedRecoverable = !fb.SnWindowGone;
                link.ReportPeerExited("snHwnd " + NativeWin.Hex(sn) + (fb.SnWindowGone ? " destroyed" : " hidden for " +
                                      RenderDisplay.SnHiddenDebounceMs.ToString("0", CultureInfo.InvariantCulture) + " ms or more"), _lostReportedRecoverable);
            }
            else if (_lostReportedFor != IntPtr.Zero && (!fb.SnWindowLost || sn != _lostReportedFor))
            {
                IntPtr was = _lostReportedFor;
                bool recoverable = _lostReportedRecoverable && !fb.SnWindowGone && sn == was;
                _lostReportedFor = IntPtr.Zero;
                _lostReportedRecoverable = false;
                if (recoverable) link.RecoverWindowExit("snHwnd " + NativeWin.Hex(was) + " visible again");
            }
        }

        private static bool DisplayAllowed(ref DisplayFeedback fb, out string why)
        {
            if (!_inWorld) { why = "not in world"; return false; }
            if (fb.SnWindowLost) { why = "snHwnd no longer a visible window (" + (fb.SnWindowGone ? "destroyed" : "hidden") + ")"; return false; }
            if (!_hello) { why = "no hello (site not bound, gate closed or display refused)"; return false; }
            if (!_link.Linked) { why = "not linked"; return false; }
            if (!_link.HelloAcked) { why = "hello not acknowledged by Subnautica (C7 helloAckEpoch)"; return false; }
            if (_seHwnd == IntPtr.Zero) { why = "seHwnd unknown"; return false; }
            if (_link.SnHwnd == IntPtr.Zero) { why = "snHwnd unknown"; return false; }
            bool hooks = Controller.Mode == DisplayDesign.APrime ? S2Patches.APrimeHooksOk : S2Patches.DesignFHooksOk;
            if (!hooks) { why = "render hooks missing for design " + S2Config.Name(Controller.Mode); return false; }
            why = "";
            return true;
        }

        private static bool MenuOpen
        {
            get { return FallbackAPrime.MenuOpen(_cursor, _chat, _gameplayScreen || _terminal, _paused); }
        }

        private static void ReadLink(Link link, uint helloEpoch)
        {
            var l = new LinkFacts();
            if (link != null && link.State == LinkState.Up)
            {
                PeerStatus p = link.Peer;
                // CONTRACT §1.2: a refused global gate (e.g. multiplayer) means no window change, even with the link open (O17)
                l.Linked = p != PeerStatus.Exited && ActivationGate.State != GateState.Refused;
                l.PeerAlive = p == PeerStatus.Alive;
                l.Peer = ViewStateAdapter.PeerOf(p);
                l.SnPhase = link.LastSnPhase;
                l.HaveC7 = link.HasSnStatus;
                l.C7 = link.LastSnStatus;
                l.NewPeers = link.NewPeers;
                l.LinkEpoch = link.LinkEpoch;
                l.SnPid = link.LastSnPid;
                ulong h = l.HaveC7 && l.C7.snHwnd != 0 ? l.C7.snHwnd : link.LastSnHwnd;
                l.SnHwnd = new IntPtr((long)h);
            }
            l.HelloEpoch = helloEpoch;
            _link = l;
        }

        private static void ReadGameState()
        {
            _focused = GameState.Foreground;
            _paused = GameState.Paused;
            try { MySandboxGame g = MySandboxGame.Static; _cursor = g != null && g.IsCursorVisible; } catch (Exception) { _cursor = false; }
            try { _gameplayScreen = MyGuiScreenGamePlay.ActiveGameplayScreen != null; } catch (Exception) { _gameplayScreen = false; }
            try { _terminal = _inWorld && MyGuiScreenTerminal.GetCurrentScreen() != MyTerminalPageEnum.None; } catch (Exception) { _terminal = false; }
            // MyGuiScreenChat is internal: the ModAPI's public MyGuiModHelpers.ChatEntryVisible reads the same textbox
            try { if (_gui == null) _gui = new MyGuiModHelpers(); _chat = _gui.ChatEntryVisible; }
            catch (Exception) { _chat = false; }
        }

        private static bool ConfigMuteWhenUnfocused()
        {
            try { return MySandboxGame.Config == null || MySandboxGame.Config.EnableMuteWhenNotInFocus; }
            catch (Exception) { return true; }
        }

        private static void ConfigVolumes(out float game, out float music)
        {
            game = 1f; music = 1f;
            try
            {
                var c = MySandboxGame.Config;
                if (c != null) { game = c.GameVolume; music = c.MusicVolume; }
            }
            catch (Exception) { }
        }

        private static float? DepthAt(Vec3 se)
        {
            WaterModClient api = SiteBinding.Api;
            if (api == null || SiteBinding.Planet == null) return null;
            return api.Depth(new Vector3D(se.X, se.Y, se.Z), SiteBinding.Planet);
        }

        // ====================================================================================================== audio

 /// <summary>The MySandboxGame.UpdateSound postfix (any thread): this update's SE volumes.</summary>
        public static void OnUpdateSound()
        {
            AudioController a = Audio;
            if (!Enabled || a == null) return;
            AudioDecision d;
            if (!a.TakeForUpdateSound(out d)) return;
            IMyAudio audio = MyAudio.Static;
            if (audio == null) return;
            audio.VolumeGame = d.Game;
            audio.VolumeMusic = d.Music;
            audio.VolumeHud = d.Hud;
            a.OnWritten(ref d);
        }

        /// <summary>
 /// The MyGuiScreenOptionsAudio.Save prefix: the dialog copies <c>MyAudio.Static.VolumeGame/VolumeMusic</c> into the
 /// config and saves it; they are reset first to the dialog's own sliders (which it initialised from the config and
 /// the player may have moved), so a plugin mix can never be persisted. Fallback when the sliders are unreadable:
 /// a value equal to the plugin's last write is replaced by the config value.
 /// </summary>
        public static void OnOptionsAudioSave(object screen)
        {
            AudioController a = Audio;
            if (!Enabled || a == null) return;
            IMyAudio audio = MyAudio.Static;
            if (audio == null) return;
            float? g = Slider(screen, "m_gameVolumeSlider"), m = Slider(screen, "m_musicVolumeSlider");
            float beforeGame = audio.VolumeGame, beforeMusic = audio.VolumeMusic;
            float cfgGame, cfgMusic;
            ConfigVolumes(out cfgGame, out cfgMusic);
            if (g.HasValue) { audio.VolumeGame = g.Value; audio.VolumeHud = g.Value; }
            else if (a.HasWritten && beforeGame == a.WrittenGame) { audio.VolumeGame = cfgGame; audio.VolumeHud = cfgGame; }
            if (m.HasValue) audio.VolumeMusic = m.Value;
            else if (a.HasWritten && beforeMusic == a.WrittenMusic) audio.VolumeMusic = cfgMusic;
            DeLog.Info("DE.Audio options Save prefix: game " + F(beforeGame) + " -> " + F(audio.VolumeGame) + ", music " + F(beforeMusic) + " -> " +
                       F(audio.VolumeMusic) + " (" + (g.HasValue && m.HasValue ? "the dialog's sliders" : "config where the plugin's value was found") +
                       ") before SE persists them");
        }

        private static float? Slider(object screen, string field)
        {
            try
            {
                FieldInfo f = screen.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                object slider = f != null ? f.GetValue(screen) : null;
                PropertyInfo v = slider != null ? slider.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public) : null;
                if (v == null || v.PropertyType != typeof(float)) return null;
                return (float)v.GetValue(slider, null);
            }
            catch (Exception) { return null; }
        }

        // ===================================================================================================== utils

        public static void ShowHint(string text)
        {
            DeLog.Info("DE.Display HUD hint: " + text);
            try { ((IMyUtilities)MyAPIUtilities.Static).ShowNotification(text, 8000, "Red"); }
            catch (Exception) { /* no session: log only */ }
        }

        /// <summary>The de.state additions.</summary>
        public static void AddState(Dictionary<string, object> d)
        {
            d["s2"] = Enabled && !_disabledByError;
            if (!Enabled || Driver == null) return;
            ViewOutputs o = Driver.Last;
            d["view"] = ViewStateAdapter.Name(Driver.State);
            d["viewMachine"] = ViewStateAdapter.Implementation;
            d["bridgeUp"] = _bridgeUp;
            d["blocked"] = ViewStateAdapter.BlockedNames(o.Blocked);
            d["viewForced"] = Driver.Forced.HasValue ? ViewStateAdapter.Name(Driver.Forced.Value) : "auto";
            d["snReady"] = Driver.SnReady;
            d["snReadyWhy"] = Driver.SnReadyWhy;
            d["display"] = S2Config.Name(Controller.Mode);
            d["y"] = Math.Round(Driver.Y, 3);
            d["crossingTick"] = o.CrossingTick;
            // re-dive fix: the band-hold diagnostic (ViewStateDriver; DE.View band hold lines)
            d["bandHolds"] = Driver.BandHolds;
            d["bandHoldMaxS"] = Math.Round(Driver.BandHoldMaxS, 3);
            d["inBandHold"] = Driver.InBandHold;
            d["guiScreenOpen"] = GuiScreenOpen;
            d["audioMix"] = Math.Round(Audio.Mix, 3);
            // S7a picture policy and latch
            d["picturePolicy"] = PicturePolicy.Describe();
            d["picture"] = o.Picture;
            d["pictureBlocked"] = ViewStateAdapter.BlockedNames(o.PictureBlocked);
            d["pictureEdges"] = Driver.PictureEdges;
            // S8: the peer-loss fallback (S8Rules)
            d["s8Lost"] = _s8.Lost;
            d["s8LostForMs"] = _s8Last.LostForMs;
            d["s8Losses"] = _s8.Losses;
            d["s8Recoveries"] = _s8.Recoveries;
            d["s8Notices"] = _s8.Notices;
            d["s8PeerGuards"] = Controller.PeerLostGuards;
        }

        private static void Fail(string where, Exception e)
        {
            _errors++;
            if (_errors <= 3) DeLog.Info("DE.Error S2 " + where + " #" + _errors + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
            if (_errors >= 20 && !_disabledByError)
            {
                _disabledByError = true;
                DeLog.Info("DE.S2 FEATURE DISABLED after " + _errors + " errors (S0/S1 continue; the display request is withdrawn)");
                try
                {
                    var off = new DisplayRequest();
                    if (Display != null) Display.SetRequest(ref off);
                }
                catch (Exception) { }
            }
        }

        private static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }
    }
}
