using System;
using System.Collections.Generic;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Bridge-mode entry (ROADMAP S0 SN plugin; CONTRACT §1.2, §6, §7): bridge token → mutex → window role → section
 /// creation by the creator rules → the bridge GameObject. Any closed gate leaves Subnautica dormant (vanilla: no
 /// section, no window, option or save change, no file written). Runs once, from the plugin's first player-loop Update
 /// (never Awake: S0 real run), on the main thread.
 /// </summary>
    internal static class BridgeStartup
    {
        public const string BridgeObjectName = "DeepEngineers.Bridge";

        private static bool _startAttempted;

        public static BridgeBehaviour TryStart()
        {
            // One start per process. The SN mutex is re-entrant for its owning thread, and the creator treats a
 // recorded snPid equal to our own PID as a dead predecessor (PID reuse), so a second start in the same
 // process (a duplicate plugin load) must be refused here, before either check.
            if (_startAttempted)
            {
                DeLog.Warn("DE.Link REFUSE second start in this process (the bridge start already ran once)");
                return null;
            }
            _startAttempted = true;

            // 1. Bridge token: DEEPENGINEERS_BRIDGE=1, or the launcher's named object Local\DeepEngineers_v1_launcher
 // (the launcher creates a named MUTEX; Wire's check opens it with OpenMutexW and also accepts the name
 // existing as another object type).
            string which;
            if (!BridgeToken.IsPresent(out which))
            {
                DeLog.DisableJsonl();
                DeLog.Info("DE.Bridge DORMANT (no bridge token: " + which + "; needs " + WireInfo.BridgeEnvVar + "=1 or " +
                           WireInfo.LauncherObject + ")");
                return null;
            }

            // 2. Second instance: Local\DeepEngineers_v1_sn is held by another Subnautica in bridge mode.
            NamedMutex mutex;
            int err;
            MutexAcquire acq = NamedMutex.TryAcquire(WireInfo.MutexSn, out mutex, out err);
            if (acq == MutexAcquire.HeldElsewhere)
            {
                DeLog.DisableJsonl();
                DeLog.Warn("DE.Link REFUSE second instance (" + WireInfo.MutexSn + " is held by another process)");
                DeLog.Info("DE.Bridge DORMANT (second instance)");
                return null;
            }
            if (acq == MutexAcquire.Error)
            {
                DeLog.DisableJsonl();
                DeLog.Warn("DE.Link REFUSE mutex " + WireInfo.MutexSn + " error " + err + " (name used by another object type, or access denied)");
                DeLog.Info("DE.Bridge DORMANT (mutex error)");
                return null;
            }

            DeLog.EnableJsonl();
            DeLog.Info("DE.Bridge BRIDGE MODE token=" + which + " mutex=" + WireInfo.MutexSn +
                       (acq == MutexAcquire.AcquiredAbandoned ? " (abandoned by a previous holder that died)" : "") +
                       (DeLog.JsonlPath != null ? " jsonl=" + DeLog.JsonlPath : ""));

            bool keepMutex = false;
            SharedSection section = null;
            GameObject go = null;
            try
            {
                // 3. Window role (S0): ghosting off, power-throttling opt-out, our HWND (may not exist yet).
                bool ghostingOff = WindowRole.DisableGhosting();
                WindowRole.OptOutOfPowerThrottling();
                string hwndHow;
                IntPtr hwnd = WindowRole.FindMainWindow(out hwndHow);
                DeLog.Info("DE.Window snHwnd=" + DeLog.Hex((ulong)hwnd.ToInt64()) + " (" + hwndHow + ")");

                // 4. Section by the creator rules (magic last), then validated (steps 1-5 + region table) before use.
                uint flags = SnFlags.BridgeMode | (ghostingOff ? SnFlags.GhostingDisabled : 0u);
                BridgeSection bs = SectionLink.CreateValidated(flags, (ulong)hwnd.ToInt64());
                CreatorResult cr = bs.Creator;
                if (bs.Withdrawn)
                {
                    DeLog.Warn("DE.Link REJECT own section " + WireInfo.SectionName + " after " + cr + ": " + bs.Own +
                               " (withdrawn: snPhase None, section closed)");
                    DeLog.Info("DE.Bridge DORMANT (own section failed validation)");
                    return null;
                }
                if (cr.Outcome == CreatorOutcome.Refused)
                {
                    DeLog.Warn("DE.Link REFUSE section " + WireInfo.SectionName + ": field=" + cr.Field + " expected=" + cr.Expected +
                               " found=" + cr.Found + (cr.Message != null ? " (" + cr.Message + ")" : "") + "; nothing written");
                    DeLog.Info("DE.Bridge DORMANT (creator refused)");
                    return null;
                }
                if (cr.Outcome == CreatorOutcome.Failed || cr.Section == null)
                {
                    DeLog.Error("DE.Link FAILED creating " + WireInfo.SectionName + ": " + cr.Message);
                    DeLog.Info("DE.Bridge DORMANT (section creation failed)");
                    return null;
                }
                section = cr.Section;
                DeLog.Info("DE.Link CREATE " + WireInfo.SectionName + " " + cr + " size=" + DeLog.Hex((ulong)section.MappedSize) +
                           " pid=" + Win32.CurrentProcessId() + " snFlags=" + DeLog.Hex(flags) + " (magic published last)");

                // 5. The bridge GameObject: survives scene loads and the game's SceneCleaner.
                go = new GameObject(BridgeObjectName);
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<SceneCleanerPreserve>();
                BridgeBehaviour b = go.AddComponent<BridgeBehaviour>();
                b.Attach(section, mutex, cr.Generation, flags, hwnd, bs.Own);
                keepMutex = true;
                DeLog.Info("DE.Bridge object '" + BridgeObjectName + "' scene=" + go.scene.name + " frame=" + Time.frameCount +
                           " activeScene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                           (go.scene.name == "DontDestroyOnLoad" ? "" : " (WARNING: not in DontDestroyOnLoad)"));
                return b;
            }
            catch (Exception e)
            {
                DeLog.Error("DE.Bridge start failed, staying dormant: " + e);
                // Withdraw a half-started bridge: snPhase None (an SE that already opened the section reads PeerExited).
                try
                {
                    if (section != null && section.IsOpen)
                    {
                        HeaderView hv = section.Header;
                        hv.SnPhase = SnPhase.None;
                        section.Dispose();
                    }
                }
                catch (Exception) { }
                try { if (go != null) UnityEngine.Object.Destroy(go); } catch (Exception) { }
                return null;
            }
            finally
            {
                if (!keepMutex) mutex.Dispose(); // same thread as the acquire (main thread)
            }
        }
    }

    /// <summary>
 /// The per-frame bridge driver on the "DeepEngineers.Bridge" GameObject (SceneCleanerPreserve, DontDestroyOnLoad).
 /// Main thread only; every timer runs on GetTickCount64 / unscaled time (FreezeTime sets Time.timeScale = 0).
 /// Each frame: SN heartbeat (C0), WM-1 self-check on the first frame, Booting → Menu, HWND retry, SE peer watchdog
 /// (PID/generation per frame, process handle once per second, stall/exit), C5 epochs (link, hello, seExiting, the
 /// Quitting acknowledgement), C11 control (dev builds while devControl ≠ 0), the S1 pose bridge (<see cref="S1Host"/>:
 /// host save Menu → LoadingSave → ReadyIdle, ghost Player, C1-driven camera, telemetry, captures), the S2 display, input

 /// ReadyIdle → WarmingUp → Active), the S3 mirror (<see cref="S3Host"/>: C3/C4/C2 consumer, mirrored meshes, lab
 /// commands de.s3/de.time), quit sequencing and the C7 SnStatus record. Counters every 300 frames. Update
 /// never throws: the first exception is logged, snPhase is set to None (SE reads PeerExited) and the bridge stops.
 /// </summary>
    internal sealed unsafe class BridgeBehaviour : MonoBehaviour, IControlHandler, IS1Bridge, IS2Bridge
    {
        private const int StatsEveryFrames = 300;
        private const int SettingsEveryFrames = 60;
        private const ulong HwndRetryMs = 1000;
        private const ulong QuitAckWaitMs = 100;

        private SharedSection _section;
        private NamedMutex _mutex;
        private uint _generation;
        private uint _snFlags;
        private ulong _hwnd;
        private IntPtr _c5, _c7;
        private ByteRing _c3, _c4;
        private ControlConsumer _control;
        private readonly SePeerMonitor _peer = new SePeerMonitor();
        private readonly MenuDetector _menu = new MenuDetector();

        private uint _phase = SnPhase.Booting;
        private ulong _snFrame;
        private ulong _startMs;
        private ulong _nextHwndTryMs;
        private bool _attached, _disabled, _destroyed;
        /// <summary>Set by Revalidate when our own section fails steps 1-5; the bridge is withdrawn after the frame.</summary>
        private string _withdrawReason;

        // WM-1 in-game self-check (CONTRACT §2.9, §6 validation step 6)
        private bool _selfTestDone, _selfTestPass;
        private string _selfTestLine = "";

        // C5 SeState, read by seqlock; only values written after the current SE attached are used.
        private SeState _seState;
        private uint _seStateSeq, _c5SeqAtPeer;
        private bool _seStateValid;
        private readonly EpochWatch _linkWatch = new EpochWatch();
        private readonly EpochWatch _helloWatch = new EpochWatch();
        private uint _helloAck, _linkAck;
        private long _c5Fresh, _c5Busy, _resyncs;

        // quit sequencing (CONTRACT §7: Quitting, ≤ 100 ms for SE's acknowledgement, then the game's own quit)
        private bool _quitting, _quitIssued, _quitRetried, _quitWaitAck;
        private ulong _quitAtMs, _quitIssuedMs;
        private uint _c5SeqAtQuit;
        private string _quitReason;
        // S8: the quit follows an SE exit (Application.Quit fallback after 5 s instead of 10 s; S8Rules.FallbackAfterMs)
        private bool _quitSeExited;
        private S8Rules.QuitPath _quitPath;
        // S8: de.stallSn (dev flavour, C11): the main thread blocks this long at the end of the next frame (S8.T-L1b)
        private ulong _stallSnMs;

        // frame time (unscaled) and applied settings, for C7
        private readonly float[] _frameMs = new float[StatsEveryFrames];
        private readonly float[] _sortScratch = new float[StatsEveryFrames];
        private int _frameMsCount;
        private float _p50, _p99;
        private int _vSync, _targetFps;
        private float _fixedDt;

        public uint Phase { get { return _phase; } }

        // S1 pose bridge (S1Host.cs): host save, ghost Player, driven camera, telemetry, captures. Null if its start failed.
        private S1Host _s1;

        public ulong SnFrame { get { return _snFrame; } }
        public bool Quitting { get { return _quitting; } }
        void IS1Bridge.SetPhase(uint phase, string why) { SetPhaseWithS2(phase, why); }

        // S2 display, input and audio (S2Host.cs). Null if its start failed (S0/S1 keep running).
        private S2Host _s2;

        void IS2Bridge.SetPhase(uint phase, string why) { SetPhaseWithS2(phase, why); }
        public SePeerState PeerState { get { return _peer.State; } }

        // S3 mirror and lab (S3Host.cs). Null if its start failed (S0-S2 keep running).
        private S3Host _s3;
        // S5a arms and breath (S5aHost.cs). Null if its start failed or DEEPENGINEERS_SN_S5A=off (S0-S3 keep running).
        private S5aHost _s5a;
        // S4 raycast service de.s4 (S4Host.cs; dev C11 only). Null if its start failed or S1 did not start (S0-S3 keep running).
        private S4Host _s4;
        // S5 whole grids + character (S5Host.cs; the mirror work runs in S3's consumer). Null if S3 did not start or S5's start failed.
        private S5Host _s5;
        // S6 proxies and damage forwarding (the proxies follow S3's mirror). Null if S3 did not start, DEEPENGINEERS_S6=off or S6's start failed.
        private S6Host _s6;
        // S7a time drive from C6, picture/sun rows, de.s7 (S7aHost.cs). Null if S1 did not start or its start failed (S0-S6 keep running).
        private S7aHost _s7a;
        // S7 interiors + lights (InteriorHost.cs: the IsInsideWalkable rule, interior skies, clip proxies, the light pool,
 // de.interior, de.lights). Null if S1 or S3 did not start or its start failed (S0-S7a keep running).
        private InteriorHost _s7i;
        // S7 effects (EffectsPlayer.cs: the C2 event window drawn and played with Subnautica's own prefabs and FMOD events,
 // de.fx). Null if S3 did not start or its start failed (S0-S7a and the interiors keep running).
        private S7Effects _s7fx;
        // S7 host (S7Host.cs: the C12 statics reporter, C7 capabilities bit 2, de.statics, de.bones). Needs only the
 // mapped section; disposed before it is unmapped.
        private S7Host _s7;
        // S8a (S8aTerrain.cs: the store view, the notice reader on S3's C2 reader, the reload scheduler, the C7 S8a block, de.s8a). Null if its start failed (S0-S7t keep running).
        private S8aTerrain _s8a;
        // S7p-gameplay (SE weapons/tools damage creatures, the s7p.cam row, de.s7p). Null if S3 did not start or its start failed.
        private S7pGameplay _s7p;
        // S11 (weapon classes, explosion chains, avoidance, grabs, hazards, air, EMP, de.s11). Null if S3 did not start,
 // DEEPENGINEERS_S11=off or its start failed; it uses S6 (proxies, the C8 sink) when S6 runs.
        private S11Host _s11;
        // S7t-sn-altitude (AltitudeHost.cs: the far plane by altitude, the horizon skirt, the haze, de.s7t horizon|haze|impostor|alt).
 // Null if its start failed; the impostors' mirror hooks keep their defaults without it.
        private AltitudeHost _s7t;
        // C8 SnDamage producer ring (S6), built after the creator/attach step like C3/C4
        private EntryRing _c8;
        public IntPtr SnHwnd { get { return new IntPtr((long)_hwnd); } }
        public ulong SeHwnd { get { return _section != null && _section.IsOpen ? _section.Header.SeHwnd : 0UL; } }

        /// <summary>S2: a re-found window handle goes to C0 snHwnd (and C7 through WriteStatus).</summary>
        public void PublishHwnd(IntPtr hwnd, string how)
        {
            _hwnd = (ulong)hwnd.ToInt64();
            if (_section != null && _section.IsOpen) { HeaderView hv = _section.Header; hv.SnHwnd = _hwnd; }
            DeLog.Info("DE.Window snHwnd=" + DeLog.Hex(_hwnd) + " (" + how + ")");
        }

        /// <summary>S2: C0 snFlags bit 2 stylesApplied.</summary>
        public void SetStylesApplied(bool on)
        {
            uint f = on ? (_snFlags | SnFlags.StylesApplied) : (_snFlags & ~SnFlags.StylesApplied);
            if (f == _snFlags) return;
            _snFlags = f;
            if (_section != null && _section.IsOpen) { HeaderView hv = _section.Header; hv.SnFlags = f; }
        }

        /// <summary>
 /// The first ReadyIdle is preceded by S2's set-up (CONTRACT §7 "Start": Idle, muted, window restyled and lowered,
 /// AllowSetForegroundWindow, then ReadyIdle), so the launcher never sees ReadyIdle before it.
 /// </summary>
        private void SetPhaseWithS2(uint phase, string why)
        {
            if (phase == SnPhase.ReadyIdle && _s2 != null && !_s2.Ready)
            {
                try { _s2.BeforeReadyIdle(Win32.TickCount64()); }
                catch (Exception e) { DeLog.Error("DE.S2 ReadyIdle set-up failed (S2 disabled; S1 continues): " + e); DisposeS2(); }
            }
            SetPhase(phase, why);
        }

        /// <summary>C7 written now: S1's end-of-frame stamp of presentedSeTick (CONTRACT §5.4 C7).</summary>
        public void PublishStatus()
        {
            if (!_attached || _disabled || _section == null || !_section.IsOpen) return;
            WriteStatus();
        }

        internal void Attach(SharedSection section, NamedMutex mutex, uint generation, uint snFlags, IntPtr hwnd, ValidationResult own)
        {
            _section = section;
            _mutex = mutex;
            _generation = generation;
            _snFlags = snFlags;
            _hwnd = (ulong)hwnd.ToInt64();
            _startMs = Win32.TickCount64();
            _nextHwndTryMs = _startMs + HwndRetryMs;

            byte* b = section.Base;
            _c5 = (IntPtr)Layout.RegionPtr(b, RegionId.C5SeState);
            _c7 = (IntPtr)Layout.RegionPtr(b, RegionId.C7SnStatus);
            // Ring objects are built after the creator step: they read the head/tail words found in the section.
            _c3 = new ByteRing(b, RingIndex.C3SeModels);
            _c4 = new ByteRing(b, RingIndex.C4SeParts);
            _control = new ControlConsumer(b);
            _c8 = new EntryRing(b, RingIndex.C8SnDamage);

            _menu.Attach();
            DevMutants.LoadFromEnvironment();
            try
            {
                _s1 = new S1Host(this, b, transform);
                _s1.Start();
            }
            catch (Exception e)
            {
                _s1 = null;
                DeLog.Error("DE.S1 start failed (S0 link only, no host save, no camera driving): " + e);
            }
            try
            {
                _s2 = new S2Host(this, _s1, transform);
                _s2.Start();
            }
            catch (Exception e)
            {
                _s2 = null;
                DeLog.Error("DE.S2 start failed (S0/S1 keep running; no S2 recipes, freeze owner, mix or window role): " + e);
            }
            try
            {
                // after S1: the mirror consumes C3/C4 through the ring objects above (one consumer tail per ring)
                _s3 = new S3Host(this, this, b, _c3, _c4, _s1);
                _s3.Start();
            }
            catch (Exception e)
            {
                _s3 = null;
                DeLog.Error("DE.S3 start failed (S0-S2 keep running; no mirror, no lab commands): " + e);
            }
            try
            {
                // after S1 and S2: the arms follow the camera driver's samples, the gate reads S2's C5 view state
                if (_s1 != null && S5aSwitches.Enabled)
                {
                    _s5a = new S5aHost(this, _s1, _s2);
                    _s5a.Start();
                }
                else DeLog.Info("DE.S5a not started (" + (_s1 == null ? "S1 failed" : S5aSwitches.Var + "=off") + "): no arms, no oxygen mirror, no death-path patch");
            }
            catch (Exception e)
            {
                _s5a = null;
                DeLog.Error("DE.S5a start failed (S0-S3 keep running; no arms, no oxygen mirror): " + e);
            }
            try
            {
                // after S1: the layer-30 probe holds the camera through S1's override (S4 terrain oracles A-T2/WM-6)
                if (_s1 != null)
                {
                    _s4 = new S4Host(_s1);
                    _s4.Start();
                }
                else DeLog.Info("DE.S4 not started (S1 failed): no de.s4 raycast service");
            }
            catch (Exception e)
            {
                _s4 = null;
                DeLog.Error("DE.S4 start failed (S0-S3 keep running; no de.s4 raycast service): " + e);
            }
            try
            {
                // after S3 and S5a: the character and the S5 grid extensions live in S3's mirror; S5a's arms gate is switched by fpbody
                if (_s3 != null)
                {
                    _s5 = new S5Host(this, _s3, _s5a);
                    _s5.Start();
                }
                else DeLog.Info("DE.S5 not started (S3 failed): no whole-grid extensions, no character mirror");
            }
            catch (Exception e)
            {
                _s5 = null;
                DeLog.Error("DE.S5 start failed (S0-S5a keep running; S3's mirror unchanged): " + e);
            }
            try
            {
                // after S3 (and S5): the proxies follow S3's mirror (views, SkinSet store, character); the ghost rule is S1's
                if (_s3 != null && S6Switches.Enabled)
                {
                    _s6 = new S6Host(this, _s1, _s3, _c8, generation);
                    _s6.Start();
                }
                else DeLog.Info("DE.S6 not started (" + (_s3 == null ? "S3 failed" : S6Switches.Var + "=off") + "): no proxies, no damage forwarding");
            }
            catch (Exception e)
            {
                _s6 = null;
                DeLog.Error("DE.S6 start failed (S0-S5 keep running; no proxies, no damage forwarding): " + e);
            }
            try
            {
                // after S1, S2 and S5a: C6 drives Subnautica's day; the arms gate reads the C5 flags it publishes (S7aSnState)
                if (_s1 != null)
                {
                    _s7a = new S7aHost(this, _s1, _s2, b, () => _p99);
                    _s7a.Start();
                }
                else DeLog.Info("DE.S7a not started (S1 failed): no time drive, no de.s7");
            }
            catch (Exception e)
            {
                _s7a = null;
                DeLog.Error("DE.S7a start failed (S0-S6 keep running; no time drive): " + e);
            }
            try
            {
                // after S1 (C1's interior bytes, the camera gate) and S3 (the mirror's room store, views and C2 reader)
                if (_s1 != null && _s3 != null)
                {
                    _s7i = new InteriorHost(_s1, _s3.Mirror);
                    _s7i.Start();
                }
                else DeLog.Info("DE.S7 interiors+lights not started (" + (_s1 == null ? "S1" : "S3") + " failed): no interiors, no lights");
            }
            catch (Exception e)
            {
                _s7i = null;
                DeLog.Error("DE.S7 interiors+lights start failed (S0-S7a keep running): " + e);
            }
            try
            {
                // after S3 (the mirror's views and C2 reader); the coroutine host is this bridge
                if (_s3 != null)
                {
                    _s7fx = new S7Effects(this, _s3.Mirror);
                    _s7fx.Start();
                }
                else DeLog.Info("DE.S7 fx not started (S3 failed): no Subnautica effects");
            }
            catch (Exception e)
            {
                _s7fx = null;
                DeLog.Error("DE.S7 fx start failed (S0-S7a and the interiors keep running): " + e);
            }
            try
            {
                // S7 statics + de.bones (S7Host.cs): the C12 producer writes into the mapped section (b)
                _s7 = new S7Host(b, generation, () => _s6 != null ? _s6.Proxies.AllSections() : null); // S7 review F17: proxy boxes for insideProxy
            }
            catch (Exception e)
            {
                _s7 = null;
                DeLog.Error("DE.S7 host start failed (S0-S7a, the interiors and the effects keep running): " + e);
            }
            try
            {
                // S7p-gameplay: after S3 (its event reader sits on S3's C2 reader) and S1 (the camera driver for s7p.cam)
                if (_s3 != null)
                {
                    _s7p = new S7pGameplay(_s3.Mirror, () => _s1 != null ? _s1.Cam : null);
                    _s7p.Start();
                }
            }
            catch (Exception e)
            {
                _s7p = null;
                DeLog.Error("DE.S7p gameplay start failed (everything else keeps running; no creature damage): " + e);
            }
            try
            {
                // S11 hook: after S6 (the sink, the proxies) and S7p (the creature damage it classes)
                if (_s3 != null && S11Switches.Enabled)
                {
                    _s11 = new S11Host(this, _s6, _s3.Mirror);
                    _s11.Start();
                }
                else DeLog.Info("DE.S11 not started (" + (_s3 == null ? "S3 failed" : S11Switches.Var + "=off") + ")");
            }
            catch (Exception e)
            {
                _s11 = null;
                DeLog.Error("DE.S11 start failed (everything else keeps running): " + e);
            }
            try
            {
                // S7t-sn-altitude: the picture above the sea (far plane, skirt, haze) and the impostor switches (S7tImpostors)
                _s7t = new AltitudeHost();
                _s7t.Start();
            }
            catch (Exception e)
            {
                _s7t = null;
                DeLog.Error("DE.S7t altitude start failed (everything else keeps running; Subnautica's own far plane): " + e);
            }
            try
            {
                // S8a (B4 hook): the terrain converter's Subnautica side; its notice reader sits on S3's C2 reader, de.s8a rays forwards to S4
                _s8a = new S8aTerrain(b, generation);
                S4Host s4 = _s4;
                _s8a.Attach(_s3 != null ? _s3.Mirror.C2 : null, s4 != null ? (Func<ControlRequest, JsonObjectWriter, ulong, bool>)s4.Handle : null);
                _s8a.AttachHost(this); // S8a-2: the spawns' coroutine host (prefabs through CraftData, as the effects library)
                _s8a.MirrorPoseOf = id =>
                {
                    //  a mirrored grid's drawn frame position (S8a.N8.jump reads the plate on this side)
                    MirrorGridView v;
                    if (_s3 != null && _s3.Mirror.ViewMap.TryGetValue(id, out v) && v.Alive && v.HasPose && v.FrameT != null)
                    {
                        Vector3 p = v.FrameT.position;
                        return new S8aTerrain.MirrorPose { Ok = true, X = p.x, Y = p.y, Z = p.z, Tick = v.LastTick };
                    }
                    return default(S8aTerrain.MirrorPose);
                };
                S1Telemetry.AreaBuiltGate = _s8a.AreaBuiltGate; // CONTRACT "window.ready": the C7 areaBuilt gate
            }
            catch (Exception e)
            {
                _s8a = null;
                DeLog.Error("DE.S8a host start failed (everything else keeps running; the store is never served): " + e);
            }
            SampleSettings();
            WriteStatus();
            _attached = true;

            // validated by BridgeStartup before this point (SectionLink.CreateValidated); a failure never gets here
            DeLog.Info("DE.Link VALID steps 1-5 + region table (" + own + ") generation=" + generation);
            DeLog.Info("DE.Link WAIT se (section ready; SE opens it, never creates it) phase=" + SnPhase.Name(_phase) +
                       " devControl=" + (BuildFlavor.Dev ? "honoured" : "SE chat relay only, user tuning commands (release build)")); // S9-release-flavor
        }

        private void Update()
        {
            if (!_attached || _disabled) return;
            try
            {
                long s10 = S10Sn.T(); // S10 hook (round 2 A): the whole bridge tick timed (s10.perf stage "plugin")
                Tick();
                S10Sn.Add(S10PerfRules.StagePlugin, s10);
                if (_withdrawReason != null) WithdrawBridge();
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }

        // the 21:10 session: 152 ms plugin frames with every timed stage under 6 ms): the untimed
 // calls of the tick each report a single call over SlowCallMs (one line per name per 10 s), naming the culprit
        public const double SlowCallMs = 40.0;
        private readonly Dictionary<string, ulong> _slowNext = new Dictionary<string, ulong>(StringComparer.Ordinal);

        private void SlowCheck(string name, long t0, ulong now)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms < SlowCallMs) return;
            ulong next;
            if (_slowNext.TryGetValue(name, out next) && now < next) return;
            _slowNext[name] = now + 10000;
            DeLog.Warn("DE.S10 slow call: " + name + " took " + ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms in one frame (untimed in s10.perf)");
        }

        private void Tick()
        {
            ulong now = Win32.TickCount64();
            _snFrame++;
            DeLog.Frame = _snFrame;
            HeaderView hv = _section.Header;
            hv.SnHeartbeatMs = now; // C0 heartbeat, every frame, GetTickCount64

            RecordFrameTime();
            S10Sn.Frame(now); // S10 hook: the s10.perf row (frame times, the stage clock below, memory); measurement only, never throws
            if (_snFrame % SettingsEveryFrames == 0) SampleSettings();

            if (!_selfTestDone) RunSelfTest("load");

            if (_phase == SnPhase.Booting && _menu.MenuReady()) SetPhase(SnPhase.Menu, _menu.Describe());

            if (_hwnd == 0 && now >= _nextHwndTryMs) TryFindWindow(now);

            long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SePeerEvent pev = _peer.Tick(now, hv, _phase);
            SlowCheck("peer", q0, now);
            switch (pev)
            {
                case SePeerEvent.NewPeer: OnNewSePeer(); break;
                case SePeerEvent.Exited: OnSeExited(now); break;
            }
            if (pev != SePeerEvent.None) S8Host.PeerEvent(pev, _peer, _snFrame, _linkAck); // S8: the s8.peer row (reaction table)

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); PollSeState(now); SlowCheck("se state", q, now); }

            if (UserCommandRules.ControlReadAllowed(BuildFlavor.Dev, hv.DevControl)) _control.Poll(this); // S9-release-flavor: release reads C11c only from SE's chat relay

            long s10v = S10Sn.T(); // S10 hook (round 3 E): the view stage (S1, S2, the S8 restore check) timed, its allocations named
            if (_s1 != null) TickS1(now); // S1: Menu -> LoadingSave -> ReadyIdle, ghost, camera, telemetry, captures

            if (_s2 != null) TickS2(now);

            if (_s2 != null) S8Host.Frame(now, _peer.State, _seStateValid, ref _seState, _s2, _quitting, _phase); // S8: the Alt+Tab / ESC restore check (guarded inside)
            S10Sn.Add(S10PerfRules.StageView, s10v); // S10 hook (round 3 E)

            if (_s3 != null) { long s10 = S10Sn.T(); TickS3(now); S10Sn.Add(S10PerfRules.StageMirror, s10); } // S3: C3/C4/C2 drain, mirrored meshes, uploads, lab scene (S10 hook: timed)

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); if (_s5a != null) TickS5a(now); SlowCheck("s5a", q, now); } // S5a: the clock of the arms/oxygen steps (they run in S1's camera callbacks), DE.PDA

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); if (_s4 != null) TickS4(now); SlowCheck("s4", q, now); } // S4: de.s4 camera holds and layer-30 rays (only while a batch runs)

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); if (_s5 != null) TickS5(now); SlowCheck("s5 fpbody", q, now); } // S5: fpbody and the S5a arms gate, S5.M1/M2, the glass material source

            if (_s6 != null) { long s10 = S10Sn.T(); TickS6(now); S10Sn.Add(S10PerfRules.StageColliders, s10); } // S6: proxies, physics rate, Freedom gate, self-check, C8 flush, scenes (S10 hook: timed)

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); if (_s7a != null) TickS7a(now); SlowCheck("s7a sky", q, now); } // S7a: C6 -> DayNightCycle/uSkyManager, s7a.sun / s7a.picture rows

            if (_s7i != null) { long s10 = S10Sn.T(); TickS7i(now); S10Sn.Add(S10PerfRules.StageLights, s10); } // S7: inside-walkable, interior skies, clip proxies, the light pool (S10 hook: timed)

            if (_s7fx != null) { long s10 = S10Sn.T(); TickS7fx(now); S10Sn.Add(S10PerfRules.StageFx, s10); } // S7: effect events (thrusters, weapons, explosions, tools, impacts, hull breaks, splashes) (S10 hook: timed)

            { long q = System.Diagnostics.Stopwatch.GetTimestamp(); if (_s7 != null) _s7.Tick(now); SlowCheck("s7 statics", q, now); } // S7: the statics reporter (C12; guarded inside, never throws)
            if (_s8a != null) { long s10 = S10Sn.T(); _s8a.Update(now); S10Sn.Add(S10PerfRules.StageWindow, s10); } // S8a: notices, file checks, reloads, the s8a.* rows (guarded inside, never throws) (S10 hook: timed)

            long s10h = S10Sn.T(); // S10 hook (round 3 E): the hosts stage (S7p.. S12 below) timed, its allocations named
            if (_s7p != null) TickS7p(now); // S7p-gameplay: creature damage, s7p.cam

            if (_s11 != null) TickS11(now); // S11 hook: grabs, samplers, scenes, s11.status

            if (_s7t != null) _s7t.Tick(now); // S7t-sn-altitude: far plane, horizon skirt, haze, s7t.alt / s7t.imp rows (guarded inside, never throws)

            LookMatchSn.Frame(now, _s1 != null && _s1.InWorld, _peer.State == SePeerState.Alive); // S7t-look-match (T5): the sun's shadow strength and the ambient toward SE's light above the surface (guarded, never throws)
            S16Sn.Frame(now, _s1 != null && _s1.InWorld, _peer.State == SePeerState.Alive, _seStateValid, ref _seState); // S16 hook: hull creaks, the hull-critical voice, the crush groan from the C5 extension (guarded, never throws)
            S15Sn.Frame(now, _s1 != null && _s1.InWorld, _peer.State == SePeerState.Alive, _seStateValid, ref _seState, _section != null && _section.IsOpen ? _section.Base : null, _s1 != null ? _s1.Cam.PresentedSeTick : 0UL); // S15 hook: the Cyclops lines from the C5 block, SE's planets in the sky from C6's region space (guarded, never throws)
            S12Sn.Frame(now, _s1 != null && _s1.InWorld, _peer.State == SePeerState.Alive, _section != null && _section.IsOpen ? _section.Base : null, _s3 != null ? _s3.Mirror : null, _s7fx, _s1 != null && _s1.Cam != null ? _s1.Cam.Reader : null); // S12 hook: flood planes and vent bubbles from C6 region +0x800, the interior look (guarded, never throws)
            S17Sn.Frame(now, _s1 != null && _s1.InWorld, _peer.State == SePeerState.Alive, _seStateValid, ref _seState, _section != null && _section.IsOpen ? _section.Base : null); // S17 hook: the fish in C9, SE's catch request from the C5 block (guarded, never throws)
            S10Sn.Add(S10PerfRules.StageHosts, s10h); // S10 hook (round 3 E)

            if (_quitting) TickQuit(now);

            WriteStatus();

            if (_snFrame % StatsEveryFrames == 0) LogStats(now, hv);

            if (_stallSnMs > 0)
            {
                // S8 de.stallSn (dev flavour only; ROADMAP S8.T-L1b): the main thread blocks, so the heartbeat stops (SE sees
 // PeerStalled at 3 s, then a link epoch when this frame ends)
                ulong ms = _stallSnMs;
                _stallSnMs = 0;
                DeLog.Info("DE.S8 de.stallSn: blocking the main thread for " + ms + " ms (heartbeat stops)");
                Thread.Sleep((int)ms);
                DeLog.Info("DE.S8 de.stallSn: main thread back after " + (Win32.TickCount64() - now) + " ms");
            }
        }

        /// <summary>S1 never takes the S0 link down: an exception escaping it stops S1 only (logged once).</summary>
        private void TickS1(ulong now)
        {
            try
            {
                _s1.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S1 DISABLED after an exception (the S0 link keeps running; no camera driving): " + e);
                try { _s1.Dispose(); } catch (Exception) { }
                _s1 = null;
            }
        }

        /// <summary>S2 never takes S0/S1 down: an exception escaping it stops S2 only (logged once, Subnautica restored).</summary>
        private void TickS2(ulong now)
        {
            try
            {
                _s2.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S2 DISABLED after an exception (S0/S1 keep running): " + e);
                DisposeS2();
            }
        }

        /// <summary>S3 never takes S0-S2 down: an exception escaping it stops S3 only (logged once, its objects destroyed).</summary>
        private void TickS3(ulong now)
        {
            try
            {
                _s3.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S3 DISABLED after an exception (S0-S2 keep running): " + e);
                DisposeS3();
            }
        }

        /// <summary>S5a never takes S0-S3 down: an exception escaping it stops S5a only (logged once, the arms hidden, the mirror released).</summary>
        private void TickS5a(ulong now)
        {
            try
            {
                _s5a.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S5a DISABLED after an exception (S0-S3 keep running): " + e);
                DisposeS5a();
            }
        }

        /// <summary>S4 never takes S0-S3 down: an exception escaping it stops S4 only (logged once, the camera released).</summary>
        private void TickS4(ulong now)
        {
            try
            {
                _s4.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S4 DISABLED after an exception (S0-S3 keep running): " + e);
                DisposeS4();
            }
        }

        /// <summary>S5 never takes S0-S5a down: an exception escaping it stops S5's host only (logged once, the arms gate restored).</summary>
        private void TickS5(ulong now)
        {
            try
            {
                _s5.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S5 DISABLED after an exception (S0-S5a keep running): " + e);
                DisposeS5();
            }
        }

        /// <summary>S6 never takes S0-S5 down: an exception escaping it stops S6's host only (proxies destroyed, physics rate released).</summary>
        private void TickS6(ulong now)
        {
            try
            {
                _s6.Tick(now, _helloAck);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S6 DISABLED after an exception (S0-S5 keep running): " + e);
                DisposeS11(); // S11 hook: it uses S6's sink and proxies
                DisposeS6();
            }
        }

        /// <summary>S7a never takes S0-S6 down: an exception escaping it stops S7a only (Subnautica's clock handed back).</summary>
        private void TickS7a(ulong now)
        {
            try
            {
                _s7a.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S7a DISABLED after an exception (S0-S6 keep running): " + e);
                DisposeS7a();
            }
        }

        /// <summary>S7 interiors + lights never take S0-S7a down: an exception escaping the host stops it only.</summary>
        private void TickS7i(ulong now)
        {
            try
            {
                _s7i.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S7 interiors+lights DISABLED after an exception (S0-S7a keep running): " + e);
                DisposeS7i();
            }
        }

        /// <summary>S7 effects never take S0-S7a or the interiors down: an exception escaping the player stops it only.</summary>
        private void TickS7fx(ulong now)
        {
            try
            {
                _s7fx.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S7 fx DISABLED after an exception (S0-S7a and the interiors keep running): " + e);
                DisposeS7fx();
            }
        }

        /// <summary>S7p-gameplay never takes anything else down: an exception escaping it stops it only.</summary>
        private void TickS7p(ulong now)
        {
            try
            {
                _s7p.Tick(now);
            }
            catch (Exception e)
            {
                DeLog.Error("DE.S7p gameplay DISABLED after an exception (everything else keeps running): " + e);
                DisposeS7p();
            }
        }

        /// <summary>S11 never takes anything else down: an exception escaping it stops it only.</summary>
        private void TickS11(ulong now)
        {
            try { _s11.Tick(now); }
            catch (Exception e)
            {
                DeLog.Error("DE.S11 DISABLED after an exception (everything else keeps running): " + e);
                DisposeS11();
            }
        }

        /// <summary>S11: grabs released, hooks off (before S6 and S7p go).</summary>
        private void DisposeS11()
        {
            S11Host h = _s11;
            _s11 = null;
            try { if (h != null) h.Dispose(); } catch (Exception) { }
        }

        /// <summary>S7p-gameplay: its C2 event reader removed (before S3's mirror goes).</summary>
        private void DisposeS7p()
        {
            S7pGameplay h = _s7p;
            _s7p = null;
            try { if (h != null) h.Dispose(); } catch (Exception) { }
        }

        /// <summary>The effect pools, lights and loops destroyed, the C2 event reader removed (before S3's mirror goes).</summary>
 /// <summary>The statics reporter stopped (it writes C12 into the view): before the section is unmapped.</summary>
        private void DisposeS7()
        {
            S7Host h = _s7;
            _s7 = null;
            try { if (h != null) h.Dispose(); } catch (Exception) { }
        }

        /// <summary>S8a: the C7 gate hook released, the host disposed (its reader removed from S3's C2 reader).</summary>
        private void DisposeS8a()
        {
            S8aTerrain t = _s8a;
            _s8a = null;
            if (t != null) S1Telemetry.AreaBuiltGate = null;
            try { if (t != null) t.Dispose(); } catch (Exception) { }
        }

        private void DisposeS7fx()
        {
            S7Effects fx = _s7fx;
            _s7fx = null;
            try { if (fx != null) fx.Dispose(); } catch (Exception) { }
        }

        /// <summary>The inside override off, the light pool and clip proxies destroyed, the C2 light reader removed (before S3's mirror goes).</summary>
        private void DisposeS7i()
        {
            InteriorHost h = _s7i;
            _s7i = null;
            try { if (h != null) h.Dispose(); } catch (Exception) { }
        }

        /// <summary>The time drive released (DayNightCycle resumed, the sky angles back) before the section goes.</summary>
        private void DisposeS7a()
        {
            S7aHost s7a = _s7a;
            _s7a = null;
            try { if (s7a != null) s7a.Dispose(); } catch (Exception) { }
        }

        /// <summary>Proxies destroyed, the stepper deregistered, the store callbacks dropped (before S3's mirror goes).</summary>
        private void DisposeS6()
        {
            S6Host s6 = _s6;
            _s6 = null;
            try { if (s6 != null) s6.Dispose(); } catch (Exception) { }
        }

        /// <summary>S5a's arms gate restored, the character callback dropped (before S5a or S3 goes).</summary>
        private void DisposeS5()
        {
            S5Host s5 = _s5;
            _s5 = null;
            try { if (s5 != null) s5.Dispose(); } catch (Exception) { }
        }

        /// <summary>Fails a running de.s4 batch and releases the camera override it holds (before S1's camera goes).</summary>
        private void DisposeS4()
        {
            S4Host s4 = _s4;
            _s4 = null;
            try { if (s4 != null) s4.Dispose(); } catch (Exception) { }
        }

        private void DisposeS5a()
        {
            DisposeS5(); // it switches S5a's gate
            S5aHost s5a = _s5a;
            _s5a = null;
            try { if (s5a != null) s5a.Dispose(); } catch (Exception) { }
        }

        /// <summary>S7t-sn-altitude: the far plane restored, the skirt and its camera callbacks gone.</summary>
        private void DisposeS7t()
        {
            AltitudeHost h = _s7t;
            _s7t = null;
            try { if (h != null) h.Dispose(); } catch (Exception) { }
        }

        /// <summary>Before the section is unmapped: the mirror reads C2/C3/C4 through pointers into the view.</summary>
        private void DisposeS3()
        {
            DisposeS7i(); // S7: its lights hang under S3's views; its reader sits on S3's C2 reader
            DisposeS7fx(); // S7: its effects hang under S3's views; its reader sits on S3's C2 reader
            DisposeS11(); // S11 hook: it uses S6's sink and proxies and S7p's damage
            DisposeS7p(); // S7p-gameplay: its reader sits on S3's C2 reader
            DisposeS6(); // its proxies follow S3's mirror
            DisposeS5(); // its character and grid extensions live in S3's mirror
            S3Host s3 = _s3;
            _s3 = null;
            try { if (s3 != null) s3.Dispose(); } catch (Exception) { }
        }

        private void DisposeS2()
        {
            S2Host s2 = _s2;
            _s2 = null;
            try { if (s2 != null) s2.Dispose(); } catch (Exception) { }
        }

        // ----------------------------------------------------------------------------------------- phases

        private void SetPhase(uint phase, string why)
        {
            if (phase == _phase) return;
            uint old = _phase;
            _phase = phase;
            HeaderView hv = _section.Header;
            hv.SnPhase = phase;
            DeLog.Info("DE.Phase " + SnPhase.Name(old) + " -> " + SnPhase.Name(phase) + " (" + why + ") frame=" + _snFrame +
                       " sinceStartMs=" + (Win32.TickCount64() - _startMs));
        }

        // ----------------------------------------------------------------------------------------- self-test

 /// <summary>WM-1 math subset (Wire SelfTest, n = 70) + the Unity half; sets C7 bit 3 wireSelfTestPass.</summary>
        private string RunSelfTest(string why)
        {
            _selfTestDone = true;
            SelfTestResult math = SelfTest.Run(SelfTestKind.Sn);
            UnitySelfTestResult unity = UnitySelfTest.Run();
            bool pass = math.Pass && unity.Pass;
            if (math.Pass && !unity.Pass)
            {
                math.Pass = false;
                math.Failures += unity.Failures;
                if (math.Message.Length == 0) math.Message = "unity: " + unity.Message;
            }
            _selfTestPass = pass;
            _selfTestLine = math.LogLine + " " + unity.Summary + " run=" + why;
            if (pass) DeLog.Info(_selfTestLine);
            else DeLog.Error(_selfTestLine + " (display bridge must stay off: C7 wireSelfTestPass = 0)");
            Revalidate("self-test " + why);
            return _selfTestLine;
        }

        /// <summary>
 /// Validation of both sides on our own section (steps 1-6 + region table). Step 6 (self-check FAIL): the
 /// display bridge stays off through C7 wireSelfTestPass = 0. Any other failure: the section
 /// is no longer the one this creator wrote, so the bridge is withdrawn at the end of this frame (never in the
 /// middle of a frame: the rest of Tick still dereferences the mapped view).
 /// </summary>
        private void Revalidate(string why)
        {
            ValidationResult v = SectionLink.ValidateOwn(_section, _selfTestPass);
            switch (SectionLink.Classify(v))
            {
                case OwnValidation.Ok:
                    DeLog.Info("DE.Link VALID steps 1-6 (" + why + ")" + (v.Notes.Count > 0 ? " " + v : ""));
                    break;
                case OwnValidation.DisplayOff:
                    DeLog.Warn("DE.Link REJECT " + v + " (" + why + "; display bridge off: C7 wireSelfTestPass = 0)");
                    break;
                default:
                    if (_withdrawReason == null) _withdrawReason = v + " (" + why + ")";
                    break;
            }
        }

        /// <summary>Releases the bridge after its own section failed validation: dormant until the process ends.</summary>
        private void WithdrawBridge()
        {
            string reason = _withdrawReason;
            _disabled = true;
            DeLog.Warn("DE.Link REJECT own section: " + reason + " (bridge withdrawn: snPhase None, section closed, " + WireInfo.MutexSn + " released)");
            try { _menu.Detach(); } catch (Exception) { }
            DisposeS7a(); // it reads C6 through the view
            DisposeS7t(); // S7t-sn-altitude: Subnautica's own far plane back, the skirt and its camera callbacks gone
            DisposeS8a(); // S8a: the notice reader leaves S3's C2 reader, the GetPath postfix goes inert
            DisposeS7(); // S7: the statics reporter writes C12 into the view
            DisposeS4(); // the camera override it holds is released before S1's camera goes
            try { if (_s1 != null) _s1.Dispose(); } catch (Exception) { } // camera callbacks read C1: stop them before unmapping
            DisposeS2();
            DisposeS3();
            try { _peer.Dispose(); } catch (Exception) { }
            try { SectionLink.Withdraw(_section); } catch (Exception) { }
            try { if (_mutex != null) _mutex.Dispose(); } catch (Exception) { } // main thread, as the acquire
            _section = null;
            _mutex = null;
            DeLog.Info("DE.Bridge DORMANT (own section failed validation)");
        }

        // ----------------------------------------------------------------------------------------- window

        private void TryFindWindow(ulong now)
        {
            _nextHwndTryMs = now + HwndRetryMs;
            string how;
            IntPtr h = WindowRole.FindMainWindow(out how);
            if (h == IntPtr.Zero) return;
            _hwnd = (ulong)h.ToInt64();
            HeaderView hv = _section.Header;
            hv.SnHwnd = _hwnd;
            DeLog.Info("DE.Window snHwnd=" + DeLog.Hex(_hwnd) + " (" + how + ") after " + (now - _startMs) + " ms");
        }

        // ----------------------------------------------------------------------------------------- SE peer

        private void OnNewSePeer()
        {
            // CONTRACT §5.2/§6: a new SE generation → consumer tails:= heads (C3, C4, C11c), wait for a new hello.
            ResyncConsumers("new SE pid=" + _peer.Pid + " gen=" + _peer.Generation);
            _seStateValid = false;
            _c5SeqAtPeer = Seqlock.Current((byte*)_c5);
            _seStateSeq = _c5SeqAtPeer;
            _linkWatch.Forget();
            _helloWatch.Forget();
            _helloAck = 0;
            _linkAck = 0;
            if (_s1 != null) _s1.OnNewSePeer(); // C1 history restarts (a C1 left by an earlier SE is never used)
            if (_s3 != null) _s3.OnNewSePeer(); // CONTRACT §6: every mirror, part, model and material of the old SE dropped
            if (_s2 != null) _s2.OnNewSePeer(); // its C5 is used once it changes; the mix restarts from silence
            if (_s5a != null) { try { _s5a.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S5a new peer: " + e.GetType().Name + ": " + e.Message); } } // arms hidden, extension unknown again
            if (_s7a != null) { try { _s7a.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S7a new peer: " + e.GetType().Name + ": " + e.Message); } } // clock handed back until the new SE's C6
            if (_s7i != null) _s7i.OnNewSePeer(); // S7: inside off, light rows dropped, chunk skies re-applied to the new mirror
            if (_s7fx != null) { try { _s7fx.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S7 fx new peer: " + e.GetType().Name + ": " + e.Message); } } // S7: event stream learnt again, sustains end
            if (_s7p != null) { try { _s7p.OnNewSePeer(); } catch (Exception e) { DeLog.Warn("DE.S7p new peer: " + e.GetType().Name + ": " + e.Message); } } // S7p-gameplay: event stream learnt again, tools end
            if (_s11 != null) _s11.OnNewSePeer(); // S11 hook: every grab released (the new SE knows none)
            Revalidate("new SE peer");
        }

        private void OnSeExited(ulong now)
        {
            // CONTRACT §7 / ARCHITECTURE §5.14: SE process exit or seExiting → Subnautica quits without saving within
 // 10 s (S0: at the main menu, nothing to mute or freeze). Never on heartbeat loss (S8Rules reaction table).
            if (!_quitting && _s2 != null)
            {
                try { _s2.OnQuitting("SE exited (" + _peer.ExitReason + ")", true); } // muted, frozen, minimised without activation
                catch (Exception e) { DeLog.Warn("DE.S2 quit set-up: " + e.GetType().Name + ": " + e.Message); }
            }
            else if (!_quitting && _hwnd != 0)
            {
                // S8: without the S2 host (its start failed) the window is still minimised without activation
                try { SnNative.ShowWindow(new IntPtr((long)_hwnd), SnNative.SW_SHOWMINNOACTIVE); DeLog.Info("DE.Window ShowWindow(SW_SHOWMINNOACTIVE) (SE exited, no S2 host)"); }
                catch (Exception) { }
            }
            if (!_quitting)
            {
                _quitSeExited = true;
                BeginQuit(now, "SE exited (" + _peer.ExitReason + ")", false);
            }
        }

        private void ResyncConsumers(string why)
        {
            _c3.ResyncTail();
            _c4.ResyncTail();
            _control.Resync();
            _resyncs++;
            if (_s3 != null) _s3.OnRingsResynced(why); // the model in flight is dropped
            DeLog.Info("DE.Link RESYNC consumer tails C3,C4,C11c := heads (" + why + ") heads=" + _c3.Head + "," + _c4.Head + "," + _control.InHead);
        }

        private void PollSeState(ulong now)
        {
            if (!_peer.Known) return;
            byte* rec = (byte*)_c5;
            uint cur = Seqlock.Current(rec);
            if (cur == _seStateSeq || cur == 0) return; // unchanged, or never written
            SeState tmp;
            uint seq;
            SeqlockReadStatus st = Seqlock.TryReadBytes(rec, (byte*)&tmp, sizeof(SeState), Seqlock.MaxTries, out seq);
            if (st == SeqlockReadStatus.Busy) { _c5Busy++; return; }
            if (st != SeqlockReadStatus.Fresh || seq == _seStateSeq) return;
            _seState = tmp;
            _seStateSeq = seq;
            _seStateValid = true;
            _c5Fresh++;
            if (_s1 != null) _s1.OnSeState(ref tmp);
            if (_s2 != null) _s2.OnSeState(ref tmp);
            if (_s7a != null) _s7a.OnSeState(ref tmp); // S7a: the C5 flags of the out-of-water arms gate and the picture rows

            if ((tmp.flags & SeStateFlags.SeExiting) != 0 && _peer.State != SePeerState.Exited)
            {
                if (_peer.MarkExited("C5 seExiting") == SePeerEvent.Exited)
                {
                    OnSeExited(now);
                    S8Host.PeerEvent(SePeerEvent.Exited, _peer, _snFrame, _linkAck); // S8: the s8.peer row
                }
            }

            // Link epoch: a bump after a stall → re-sync consumed rings, re-validate, acknowledge (CONTRACT §6).
            bool firstLink = !_linkWatch.Known;
            if (_linkWatch.Observe(tmp.linkEpoch))
            {
                if (!firstLink)
                {
                    ResyncConsumers("link epoch " + tmp.linkEpoch);
                    Revalidate("link epoch " + tmp.linkEpoch);
                }
                _linkAck = tmp.linkEpoch;
                DeLog.Info("DE.Link ACK linkEpoch=" + tmp.linkEpoch + (firstLink ? " (first seen for this SE)" : ""));
            }

            // Hello (CONTRACT §6): validate the site identity, acknowledge with hostKind and capabilities in C7.
            if (_helloWatch.Observe(tmp.helloEpoch) && tmp.helloEpoch != 0)
            {
                if (tmp.siteVersion != WireInfo.SiteVersion)
                    DeLog.Warn("DE.Hello REJECT helloEpoch=" + tmp.helloEpoch + " field=siteVersion expected=" + WireInfo.SiteVersion + " found=" + tmp.siteVersion);
                else if (tmp.siteHash != SiteV1.SiteHash)
                    DeLog.Warn("DE.Hello REJECT helloEpoch=" + tmp.helloEpoch + " field=siteHash expected=" + SiteV1.HashHex(SiteV1.SiteHash) +
                               " found=" + SiteV1.HashHex(tmp.siteHash));
                else
                {
                    _helloAck = tmp.helloEpoch;
                    if (_s3 != null) _s3.OnHello(tmp.helloEpoch); // C4 records of other world epochs dropped from now on
                    DeLog.Info("DE.Hello ACK helloEpoch=" + tmp.helloEpoch + " Rs=" + DeLog.F(tmp.Rs, "R") + " siteHash=" +
                               SiteV1.HashHex(tmp.siteHash) + " hostKind=" + HostKind.Subnautica1 + " capabilities=" + DeLog.Hex(Capabilities()));
                }
            }
        }

        /// <summary>C7 capability bits (D13 boundary): S0 had none; S1 adds in-game captures (bit 4).</summary>
        private uint Capabilities() { return _s1 != null ? _s1.Capabilities() : 0u; }

        // ----------------------------------------------------------------------------------------- quit

        private void BeginQuit(ulong now, string reason, bool waitForAck)
        {
            if (_quitting) return;
            _quitting = true;
            _quitReason = reason;
            _quitWaitAck = waitForAck && _peer.State == SePeerState.Alive;
            _c5SeqAtQuit = _seStateSeq;
            _quitAtMs = now + (_quitWaitAck ? QuitAckWaitMs : 0);
            if (_s2 != null)
            {
                try { _s2.OnQuitting(reason, false); } // muted and frozen at once (no-op when OnSeExited did it)
                catch (Exception e) { DeLog.Warn("DE.S2 quit set-up: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s5a != null)
            {
                try { _s5a.OnQuitting(reason); } // arms hidden, OxygenManager released, the final DE.PDA line
                catch (Exception e) { DeLog.Warn("DE.S5a quit set-up: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s7a != null)
            {
                try { _s7a.OnQuitting(reason); } // DayNightCycle resumed, the sky angles back
                catch (Exception e) { DeLog.Warn("DE.S7a quit set-up: " + e.GetType().Name + ": " + e.Message); }
            }
            LookMatchSn.Restore("quit: " + reason); // S7t-look-match: the sun's shadow strength and the ambient back (guarded)
            SetPhase(SnPhase.Quitting, reason);
            WriteStatus();
            S8Host.QuitRow("request", reason, S8Rules.QuitPath.None, _quitSeExited); // S8: the s8.quit row
            DeLog.Info("DE.Lifecycle QUIT requested (" + reason + ")" +
                       (_quitWaitAck ? "; waiting up to " + QuitAckWaitMs + " ms for SE's acknowledgement" : ""));
        }

        private bool QuitAcknowledged()
        {
            return _seStateValid && _seStateSeq != _c5SeqAtQuit && (_seState.overlays & Overlays.PeerExited) != 0;
        }

        private void TickQuit(ulong now)
        {
            // S8 (S8Rules.QuitStep): issue once acknowledged or after the <= 100 ms wait; Application.Quit as the fallback
 // 5 s after the issue when SE exited (the game's quit waits for the world to settle, without a timeout), else 10 s
            bool acked = !_quitIssued && _quitWaitAck && QuitAcknowledged();
            switch (S8Rules.QuitStep(now, _quitAtMs, acked, _quitIssued, _quitIssuedMs, _quitRetried, _quitSeExited))
            {
                case S8Rules.QuitAction.Issue:
                    _quitIssued = true;
                    _quitIssuedMs = now;
                    IssueQuit(acked);
                    return;
                case S8Rules.QuitAction.Fallback:
                    _quitRetried = true;
                    DeLog.Warn("DE.Lifecycle still running " + (now - _quitIssuedMs) + " ms after the quit (" + S8Rules.Name(_quitPath) + "); calling Application.Quit()" +
                               (_quitSeExited ? " (S8: SE exited, quitting within " + S8Rules.QuitBudgetMs / 1000 + " s)" : ""));
                    S8Host.QuitRow("fallback", _quitReason, S8Rules.QuitPath.ApplicationQuit, _quitSeExited);
                    Application.Quit();
                    return;
            }
        }

        /// <summary>The game's own quit path (no save): main menu Quit button, else the in-game menu's quit to desktop.</summary>
        private void IssueQuit(bool acked)
        {
            string ack = _quitWaitAck ? (acked ? " (SE acknowledged)" : " (no SE acknowledgement within " + QuitAckWaitMs + " ms)") : "";
            bool mainMenu = uGUI_MainMenu.main != null, ingame = IngameMenu.main != null;
            _quitPath = S8Rules.ChooseQuitPath(mainMenu, ingame, !mainMenu && ingame && GameModeUtils.IsPermadeath());
            S8Host.QuitRow("issue", _quitReason, _quitPath, _quitSeExited); // S8: the s8.quit row
            if (_quitPath == S8Rules.QuitPath.MainMenuButton)
            {
                DeLog.Info("DE.Lifecycle quitting via uGUI_MainMenu.OnButtonQuit" + ack);
                uGUI_MainMenu.main.OnButtonQuit();
            }
            else if (_quitPath == S8Rules.QuitPath.IngameQuitAsync)
            {
                DeLog.Info("DE.Lifecycle quitting via IngameMenu.QuitGameAsync(quitToDesktop: true), no save" + ack);
                StartCoroutine(IngameMenu.main.QuitGameAsync(true));
            }
            else
            {
                DeLog.Info("DE.Lifecycle quitting via Application.Quit" + ack);
                Application.Quit();
            }
        }

        // ----------------------------------------------------------------------------------------- C11 control

        public void Handle(ControlRequest req, JsonObjectWriter reply)
        {
            ulong now = Win32.TickCount64();
            // S9-release-flavor: a release build runs only the user tuning commands (UserCommandRules)
            if (!UserCommandRules.RequestAllowed(BuildFlavor.Dev, req.Cmd, req.Args))
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText);
                return;
            }
            switch (req.Cmd)
            {
                case "de.ping":
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Str("side", "sn").Num("pid", (long)Win32.CurrentProcessId())
                        .Num("snGeneration", (long)_generation).Num("snFrame", _snFrame).Str("snPhase", SnPhase.Name(_phase))
                        .Num("tickMs", now);
                    return;

                case "de.state":
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Str("side", "sn").Num("pid", (long)Win32.CurrentProcessId())
                        .Num("snGeneration", (long)_generation).Num("snFrame", _snFrame).Str("snPhase", SnPhase.Name(_phase))
                        .Num("snFlags", (long)_snFlags).Num("snHwnd", _hwnd).Bool("wireSelfTestPass", _selfTestPass)
                        .Str("se", SePeerMonitor.Name(_peer.State)).Num("sePid", (long)_peer.Pid).Num("seGeneration", (long)_peer.Generation)
                        .Num("seHeartbeatAgeMs", _peer.HeartbeatAgeMs).Num("helloAckEpoch", (long)_helloAck).Num("linkAckEpoch", (long)_linkAck)
                        .Num("resyncs", _resyncs).Num("c11Received", _control.Received).Num("c11Replied", _control.Replied)
                        .Num("c11ReplyDropped", _control.ReplyDropped).Str("mutants", DevMutants.Describe())
                        .Num("frameMsP50", (double)_p50).Num("frameMsP99", (double)_p99).Num("tickMs", now);
                    return;

                case "de.selftest":
                {
                    string line = RunSelfTest("de.selftest");
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("pass", _selfTestPass).Str("line", line);
                    return;
                }

                case "de.mutant":
                {
                    string name, error;
                    bool on;
                    if (!ParseMutantArgs(req.Args, out name, out on, out error) || !DevMutants.Set(name, on, out error))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                        return;
                    }
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Str("name", name).Bool("on", on).Str("mutants", DevMutants.Describe())
                        .Str("note", "run de.selftest to apply");
                    return;
                }

                case "de.quit":
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("quitting", true).Str("snPhase", "Quitting");
                    BeginQuit(now, "de.quit control command", true);
                    return;

                case "de.stallSn":
                {
                    // S8 (ROADMAP S8 SN items, S8.T-L1b): block the main thread N s at the end of the next frame; C11 is
 // served only in the dev flavour (BuildFlavor.Dev), so a release build never has it
                    double seconds;
                    string error;
                    if (!S8Rules.ParseStallSeconds(req.Args, out seconds, out error))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                        return;
                    }
                    _stallSnMs = (ulong)(seconds * 1000.0);
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Num("stallMs", (long)_stallSnMs).Str("note", "the main thread blocks at the end of the next frame");
                    return;
                }

                default:
                    if (_s1 != null && _s1.Handle(req, reply, now)) return;
                    if (_s2 != null && _s2.Handle(req, reply, now)) return;
                    if (_s3 != null && _s3.Handle(req, reply, now)) return;
                    if (_s5a != null && _s5a.Handle(req, reply, now)) return;
                    if (_s4 != null && _s4.Handle(req, reply, now)) return;
                    if (_s5 != null && _s5.Handle(req, reply, now)) return;
                    if (_s6 != null && _s6.Handle(req, reply, now)) return;
                    if (_s7a != null && _s7a.Handle(req, reply, now)) return;
                    if (_s7i != null && _s7i.Handle(req, reply, now)) return; // S7: de.interior, de.lights
                    if (_s7fx != null && _s7fx.Handle(req, reply, now)) return; // S7: de.fx
                    if (_s7 != null && _s7.Handle(req, reply, now)) return; // S7: de.statics, de.bones
                    if (_s8a != null && _s8a.Handle(req, reply, now)) return; // S8a: de.s8a {status|probe|verify|reload|rays|spawns}
                    if (_s3 != null && _s3.Mirror.S7p.Handle(req, reply, now)) return; // S7p-scene-above: de.s7p {above}
                    if (_s7p != null && _s7p.Handle(req, reply, now)) return; // S7p-gameplay: de.s7p {damage | mirrorMs | action: gameplay}
                    if (_s11 != null && _s11.Handle(req, reply, now)) return; // S11 hook: de.s11 {status | weapon | scene | teardown}
                    if (_s7t != null && _s7t.Handle(req, reply, now)) return; // S7t-sn-altitude: de.s7t {horizon | haze | impostor | alt}
                    if (LookMatchSn.Handle(req, reply, now)) return; // S7t-look-match: de.s7t {look}, other keys pass on
                    if (S15Sn.Handle(req, reply, now)) return; // S15 hook: de.s15 {status | voice | sky}
                    if (S10Sn.Handle(req, reply, now)) return; // S10 hook: de.s10 {lod | order | lodM | hullParts | hullNearM | nearM}
                    if (S12Sn.Handle(req, reply, now)) return; // S12 hook: de.s12 {status | look: on|off}
                    if (S17Sn.Handle(req, reply, now)) return; // S17 hook: de.s17 {status}
                    if (_s3 != null && _s3.Mirror.Textures.ColorMask != null && _s3.Mirror.Textures.ColorMask.Handle(req, reply, now)) return; // S7t-colour-mask (T6): de.s7t {mask | maskSide | maskMb}, other keys pass on
                    reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "unknown or not implemented (sn has de.ping, de.state, de.selftest, de.mutant, de.quit, de.stallSn, " +
                                                                            "de.status, de.warpcam, de.capture, de.testSave, de.trace, de.underwater, de.pixel, " +
                                                                            "de.calib, de.s2, de.s2c5, de.s3, de.time, de.s5a, de.arms, de.o2, de.s4, de.s5, de.s6, de.spawn, de.s7, " +
                                                                            "de.interior, de.lights, de.fx, de.statics, de.bones)");
                    return;
            }
        }

        /// <summary>Accepts {"name": n, "state": "on"|"off"}, {"name": n, "on": true|false} or ["name", "on"|"off"].</summary>
        private static bool ParseMutantArgs(object args, out string name, out bool on, out string error)
        {
            name = null; on = false; error = null;
            object state = null;
            var dict = args as Dictionary<string, object>;
            var list = args as List<object>;
            if (dict != null)
            {
                object v;
                if (dict.TryGetValue("name", out v)) name = v as string;
                if (dict.TryGetValue("state", out v)) state = v;
                else if (dict.TryGetValue("on", out v)) state = v;
            }
            else if (list != null && list.Count == 2)
            {
                name = list[0] as string;
                state = list[1];
            }
            if (string.IsNullOrEmpty(name)) { error = "args need a mutant name"; return false; }
            if (state is bool) { on = (bool)state; return true; }
            var s = state as string;
            if (s == "on") { on = true; return true; }
            if (s == "off") { on = false; return true; }
            error = "args need state on|off";
            return false;
        }

        // ----------------------------------------------------------------------------------------- C7 SnStatus

        private void WriteStatus()
        {
            SnStatus s = default(SnStatus);
            s.flags = _selfTestPass ? SnStatusFlags.WireSelfTestPass : 0u; // uiHidden (bit 6) stays 0 in S0
            s.snPhase = _phase;
            s.snFrame = _snFrame;
            s.helloAckEpoch = _helloAck;
            s.linkAckEpoch = _linkAck;
            s.frameMsP50 = _p50;
            s.frameMsP99 = _p99;
            s.hostKind = HostKind.Subnautica1;
            s.snHwnd = _hwnd;
            s.capabilities = Capabilities();
            s.vSyncCountApplied = (uint)Math.Max(0, _vSync);
            s.targetFrameRateApplied = _targetFps;
            s.fixedDeltaTime = _fixedDt;
            if (_s1 != null) _s1.FillStatus(ref s); // S1 flags, presentedSeTick, areaBuilt*, resamplesLastFrame
            if (_s2 != null)
            {
                // S2: snHwnd, snRect, rectOk, frozen, perfModeApplied, vSyncCountApplied, targetFrameRateApplied
                try { _s2.FillStatus(ref s); }
                catch (Exception e) { DeLog.WarnOnce("s2.fillstatus", "DE.S2 C7 fields not written: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s3 != null)
            {
                // S3: mirroredEntityCount, pendingParts, c3BytesDrained (CONTRACT §5.4 C7; S0 stand-in semantics)
                try { _s3.FillStatus(ref s); }
                catch (Exception e) { DeLog.WarnOnce("s3.fillstatus", "DE.S3 C7 counters not written: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s6 != null)
            {
                // S6: flags bit 10 damageSelfTestPass, capabilities bit 1, damageDropped, damageOverflowDepth, fixedDeltaTime every frame
                try { _s6.FillStatus(ref s); }
                catch (Exception e) { DeLog.WarnOnce("s6.fillstatus", "DE.S6 C7 fields not written: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s7 != null)
            {
                // S7: capabilities bit 2 (statics) while the C12 reporter runs
                try { _s7.FillStatus(ref s); }
                catch (Exception e) { DeLog.WarnOnce("s7.fillstatus", "DE.S7 C7 capability not written: " + e.GetType().Name + ": " + e.Message); }
            }
            if (_s8a != null)
            {
                // S8a: the C7 S8a block at 0x8C (version, flags, ackSeq, appliedSeq, stream, counters, storeKey)
                try { _s8a.FillStatus(ref s); }
                catch (Exception e) { DeLog.WarnOnce("s8a.fillstatus", "DE.S8a C7 block not written: " + e.GetType().Name + ": " + e.Message); }
            }
            S16Sn.FillStatus(ref s); // S16 hook: the C7 S16 block at 0xBC (Subnautica's HUD flags; guarded inside)
            S11Hooks.FillStatus(ref s); // S11 hook: the C7 S11 block at 0xC0 (Subnautica's damage FX; guarded inside)
            S14Sn.FillStatus(ref s); // S14 hook: the C7 S14 block at 0xC4 (which thruster types Subnautica's engine loop plays; guarded inside)
            S17Sn.FillStatus(ref s); // S17 hook: the C7 S17 answer at 0xC8 (the last catch request handled; nothing while S17 is off)
            Seqlock.WriteBytes((byte*)_c7, (byte*)&s, sizeof(SnStatus));
        }

        private void SampleSettings()
        {
            _vSync = QualitySettings.vSyncCount;
            _targetFps = Application.targetFrameRate;
            _fixedDt = Time.fixedDeltaTime;
        }

        private void RecordFrameTime()
        {
            _frameMs[_frameMsCount % _frameMs.Length] = Time.unscaledDeltaTime * 1000f;
            _frameMsCount++;
            if (_frameMsCount % _frameMs.Length != 0) return;
            Array.Copy(_frameMs, _sortScratch, _frameMs.Length);
            Array.Sort(_sortScratch);
            _p50 = _sortScratch[_sortScratch.Length / 2];
            _p99 = _sortScratch[(int)(_sortScratch.Length * 0.99)];
        }

        private void LogStats(ulong now, HeaderView hv)
        {
            DeLog.Info("DE.Bridge stats frame=" + _snFrame + " upMs=" + (now - _startMs) + " phase=" + SnPhase.Name(_phase) +
                       " gen=" + _generation + " selfTest=" + (_selfTestPass ? "PASS" : "FAIL") +
                       " se=" + SePeerMonitor.Name(_peer.State) + " sePid=" + _peer.Pid + " seGen=" + _peer.Generation +
                       " sePhase=" + SePhase.Name(_peer.LastSePhase) + " seHbAgeMs=" + _peer.HeartbeatAgeMs +
                       " stalls=" + _peer.Stalls + " recoveries=" + _peer.Recoveries + " processPolls=" + _peer.ProcessPolls +
                       " resyncs=" + _resyncs + " c5=fresh:" + _c5Fresh + ",busy:" + _c5Busy +
                       " helloAck=" + _helloAck + " linkAck=" + _linkAck + " devControl=" + DeLog.Hex(hv.DevControl) +
                       " c11=in:" + _control.Received + ",out:" + _control.Replied + ",dropped:" + _control.ReplyDropped +
                       ",corrupt:" + _control.CorruptResyncs + ",badType:" + _control.BadType + ",badJson:" + _control.BadJson +
                       " frameMs=p50:" + DeLog.F(_p50, "0.00") + ",p99:" + DeLog.F(_p99, "0.00") +
                       " vSync=" + _vSync + " targetFps=" + _targetFps + " snHwnd=" + DeLog.Hex(_hwnd));
        }

        // ----------------------------------------------------------------------------------------- failure and exit

        private void Disable(Exception e)
        {
            _disabled = true;
            DeLog.Error("DE.Bridge DISABLED after an exception in Update (snPhase -> None, SE sees PeerExited): " + e);
            DisposeS7a(); // the clock handed back
            DisposeS7t(); // S7t-sn-altitude: Subnautica's own far plane back, the skirt and its camera callbacks gone
            DisposeS8a(); // S8a: the notice reader leaves S3's C2 reader, the GetPath postfix goes inert
            DisposeS7(); // S7: the statics reporter writes C12 into the view
            DisposeS4(); // a running de.s4 batch fails, its camera override is released
            try { if (_s1 != null) _s1.Dispose(); } catch (Exception) { } // no camera driving without a live bridge
            DisposeS2(); // Subnautica restored: unfrozen, unmuted, cameras on, the user's frame rate and priority
            DisposeS3(); // mirror objects destroyed, worker stopped
            try
            {
                if (_section != null && _section.IsOpen) { HeaderView hv = _section.Header; hv.SnPhase = SnPhase.None; }
            }
            catch (Exception)
            {
            }
        }

        private bool _appQuitting;

        private void OnApplicationQuit()
        {
            _appQuitting = true;
            if (!_attached || _destroyed || _section == null || !_section.IsOpen) return;
            try
            {
                if (_disabled) return;
                if (_s2 != null)
                {
                    _s2.MarkAppQuitting();
                    try { _s2.OnQuitting("application quit", false); } // muted and frozen before the handshake
                    catch (Exception e) { DeLog.Warn("DE.S2 quit set-up: " + e.GetType().Name + ": " + e.Message); }
                }
                if (_s5a != null)
                {
                    try { _s5a.OnQuitting("application quit"); } // no-op when BeginQuit did it
                    catch (Exception e) { DeLog.Warn("DE.S5a quit set-up: " + e.GetType().Name + ": " + e.Message); }
                }
                if (_s7a != null)
                {
                    try { _s7a.OnQuitting("application quit"); } // the clock handed back (in memory; the host save is never saved)
                    catch (Exception e) { DeLog.Warn("DE.S7a quit set-up: " + e.GetType().Name + ": " + e.Message); }
                }
                LookMatchSn.Restore("application quit"); // S7t-look-match (no-op when BeginQuit did it)
                if (_phase != SnPhase.Quitting)
                {
                    ulong now = Win32.TickCount64();
                    _quitting = true;   // the quit is already under way: no second quit request from PollSeState
                    _quitIssued = true;
                    _quitReason = "application quit";
                    _c5SeqAtQuit = _seStateSeq;
                    SetPhase(SnPhase.Quitting, "application quit");
                    WriteStatus();
                    if (_peer.State == SePeerState.Alive)
                    {
                        // CONTRACT §7: wait ≤ 100 ms for SE's acknowledgement (C5 overlay PeerExited); bounded, at quit only.
                        _quitWaitAck = true;
                        while (Win32.TickCount64() - now < QuitAckWaitMs)
                        {
                            PollSeState(Win32.TickCount64());
                            if (QuitAcknowledged()) break;
                            Thread.Sleep(1);
                        }
                        DeLog.Info("DE.Lifecycle application quit: SE " + (QuitAcknowledged() ? "acknowledged" : "did not acknowledge") +
                                   " Quitting within " + (Win32.TickCount64() - now) + " ms");
                    }
                }
                if (_s2 != null)
                {
                    try { _s2.OnApplicationQuit(); } // the user's frame-rate pair, priority and Input System setting back (in memory)
                    catch (Exception e) { DeLog.Warn("DE.S2 application quit: " + e.GetType().Name + ": " + e.Message); }
                }
                DeLog.Info("DE.Lifecycle application quit after " + _snFrame + " frames" + (_quitReason != null ? " (" + _quitReason + ")" : ""));
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Lifecycle OnApplicationQuit: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (_destroyed) return;
            _destroyed = true;
            try
            {
                string where = "frame=" + Time.frameCount + " scene=" + gameObject.scene.name +
                               " activeScene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                               " appQuitting=" + _appQuitting;
                _menu.Detach();
                DisposeS7a(); // it reads C6 through the view: stopped before it is unmapped
                DisposeS7t(); // S7t-sn-altitude: Subnautica's own far plane back, the skirt and its camera callbacks gone
                DisposeS8a(); // S8a: the notice reader leaves S3's C2 reader before the section is unmapped
                DisposeS7(); // S7: the statics reporter writes C12 into the view: stopped before it is unmapped
                DisposeS5a(); // its hooks hang off S1's camera callbacks: dropped before S1 goes
                DisposeS4(); // a running de.s4 batch fails, its camera override is released before S1 goes
                if (_s1 != null) _s1.Dispose(); // camera callbacks read C1: stop them before the view is unmapped
                DisposeS2(); // after an application quit only hooks and the calibration objects (nothing is unfrozen)
                DisposeS3(); // the mirror reads C2/C3/C4 through the view: stopped before it is unmapped
                _peer.Dispose();
                if (_section != null)
                {
                    if (_section.IsOpen && !_disabled && _phase != SnPhase.Quitting)
                    {
                        HeaderView hv = _section.Header;
                        if (_appQuitting)
                        {
                            hv.SnPhase = SnPhase.Quitting;
                            _phase = SnPhase.Quitting;
                        }
                        else
                        {
                            // Destroyed without an application quit (a scene load took the object): this is a bridge
 // failure, not a quit. SE must read PeerExited, never "Subnautica is quitting".
                            DeLog.Warn("DE.Bridge object destroyed without an application quit (" + where + "): snPhase -> None");
                            hv.SnPhase = SnPhase.None;
                            _phase = SnPhase.None;
                        }
                        WriteStatus();
                    }
                    _section.Dispose();
                }
                DeLog.Info("DE.Bridge OnDestroy " + where);
                if (_mutex != null) _mutex.Dispose(); // main thread, as the acquire
                DeLog.Info("DE.Bridge stopped after " + _snFrame + " frames (section unmapped, " + WireInfo.MutexSn + " released)");
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Bridge OnDestroy: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _section = null;
                _mutex = null;
                DeLog.CloseJsonl();
            }
        }
    }
}
