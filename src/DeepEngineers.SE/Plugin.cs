using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Plugins;
using VRage.Utils;

namespace DeepEngineers.SE
{
    /// <summary>

 /// 20:54). `Init` runs once on SE's update thread during `MySandboxGame` start-up, before any world (inside Pulsar's
 /// own plugin's Init, from the `MyPlugins.Plugins` loop of `MySandboxGame.Run`); `Update` runs every game update,
 /// also at the main menu and while paused, but not during the synchronous world load (`MyGuiScreenLoading.RunLoad`);
 /// `Dispose` is not reliable under Pulsar (it kills the process in `ExitThreadSafe`), so nothing depends on it.
 /// <para>
 /// S0 ("Hello bridge", ROADMAP §4 S0): identity (`DE.Hello`), the SE WM-1 subset (`DE.WireSelfTest`), the version
 /// pin (`DE.Version`), power-throttling opt-out, Harmony phase patches, the activation gate, and the open-only
 /// link to Subnautica's section (`DE.Link UP|WAIT|REJECT|REFUSE`). Without Subnautica it does nothing else.
 /// </para>
 /// </summary>
    public class Plugin : IPlugin
    {
        public static Plugin Instance { get; private set; }
        public static readonly string Version = typeof(Plugin).Assembly.GetName().Version.ToString();

        private Link _link;
        private DevChannel _dev;
        private PerfLog _perf;
        private SelfTestResult _selfTest;
        private volatile bool _selfTestPass;
        private bool _inert;
        private bool _measureOnly;
        private long _initTimestamp;
        private bool _onLoadingSeen;
        private long _updates;
        private uint _lastPhase = uint.MaxValue;
        private ulong _nextHwndMs;
        private ulong _hwnd;
        private bool _harmonyOk;
        private RateLimit _errorLog = new RateLimit(10000);
        private long _errors;
        private uint _helloEpoch;
        private volatile bool _s1Active;

        // ---- S1 (pose bridge): read by the session component, the C1 writer and the test commands (update thread)
 /// <summary>Init completed in bridge-capable mode (not inert, not measure-only): the S1 parts may run.</summary>
        internal bool S1Active { get { return _s1Active; } }
        internal Link LinkForS1 { get { return _link; } }
        /// <summary>The sePhase computed at the last plugin Update.</summary>
        internal uint CurrentPhase { get { return _lastPhase; } }
        /// <summary>The published hello epoch (0 while no hello is published).</summary>
        internal uint HelloEpoch { get { return _helloEpoch; } }

        // ======================================================================================================= Init

        public void Init(object gameInstance)
        {
            Instance = this;
            _initTimestamp = Stopwatch.GetTimestamp();
            DeLog.GameSink = WriteGameLog;
            try { Jsonl.Open(DateTime.Now); } catch (Exception) { }
            try { InitCore(gameInstance); }
            catch (Exception e) { DeLog.Info("DE.Error Init " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e)); }
        }

        private void InitCore(object gameInstance)
        {
            bool sessionAtInit = GameState.SessionExists;
            WriteGameLog("[DeepEngineers.SE] Init: plugin v" + Version + " (" + BuildFlavor.Name + "), " + WireInfo.Describe() +
                         ", game=" + (gameInstance == null ? "null" : gameInstance.GetType().FullName) +
                         ", clr=" + Environment.Version + ", x64=" + Environment.Is64BitProcess);
            if (Jsonl.Path != null) DeLog.Info("DE.Log jsonl=" + Jsonl.Path);
            else DeLog.Info("DE.Log jsonl unavailable: " + (Jsonl.Error ?? "unknown"));

            if (Sandbox.Engine.Platform.Game.IsDedicated)
            {
                _inert = true;
                DeLog.Info("DE.Gate REFUSE dedicated server (plugin inert: no link, no patches)");
                return;
            }

            if (IsMeasureOnlyMode())
            {
                // S0.N7 baseline: SE's own STATISTICS line comes only every 60 s, so the baseline run uses this mode
 // (same 1 s sampler, nothing else: no link, no Harmony, no hooks, no power-throttling change).
                _measureOnly = true;
                _perf = new PerfLog(_initTimestamp);
                DeLog.Info(HelloLine() + " mode=measure-only");
                DeLog.Info("DE.Mode measure-only (" + ModeEnvVar + "=measure): only the 1 s JSONL sampler runs; no link, patches or hooks");
                return;
            }

            Mutants.LoadFromEnvironment();
            DeLog.Info(HelloLine());

            // load order (S0.N8): Init must be logged before MySession.OnLoading
            DeLog.Info("DE.LoadOrder Init (session at Init: " + (sessionAtInit ? "PRESENT, Init came after a session was created" : "none") + ")");

            string layoutError;
            DeLog.Info(Layout.TryValidate(out layoutError)
                ? "DE.Layout OK layout " + Layout.Major + "." + Layout.Minor + " hash " + LayoutHash.Format(LayoutHash.Compiled) +
                  (LayoutHash.Compiled == LayoutHash.PinnedLayout10 ? " (pinned)" : " (DIFFERS from pinned " + LayoutHash.Format(LayoutHash.PinnedLayout10) + ")")
                : "DE.Layout FAIL " + layoutError);

            // WM-1 subset (CONTRACT §2.9): a FAIL disables the display bridge (validation step 6)
            _selfTest = SelfTest.Run(SelfTestKind.Se);
            _selfTestPass = _selfTest.Pass;
            DeLog.Info(_selfTest.LogLine);

            PowerThrottlingResult pt = Win32.OptOutOfPowerThrottling();
            DeLog.Info("DE.Perf power-throttling opt-out " + pt);

            VersionGate.CheckAtInit();

            // S4 terrain: provider id 45460 registered once per process, before any world loads (CONTRACT §3.1)
            InitTerrain();
            try { S8aHost.Init(); } // S8a (B2 hook): DEEPENGINEERS_S8A switches, the feed's state, Link.C2Extra (the S8a header and rows inside the C2 seqlock)
            catch (Exception e) { DeLog.Info("DE.S8a INIT FAIL " + e.GetType().Name + ": " + e.Message + " (S8a off; S4's guards block the tiles)"); }

            S2Host.LoadConfig(); // S2: config.json / DEEPENGINEERS_DISPLAY / DEEPENGINEERS_S2, before the patches
            DisplayController.CrossingHook = CrossingFlash.Trigger;
            ApplyHarmony();
            S2Host.Init();       // S2: display, view state, audio (inert while not linked)

            HookSessionEvents();
            HookChat();

            _link = new Link(() => _selfTestPass);
            _dev = new DevChannel(_link)
            {
                VersionInfo = VersionInfo,
                StateInfo = StateInfo,
                SelfTestLine = () => SelfTest.Run(SelfTestKind.Se).LogLine,
                VersionTest = v => v.HasValue ? VersionGate.RunTestHook(v.Value, "dev command") : VersionGate.Check("re-check"),
                ShowChat = ShowChat,
                ExtraCommand = (cmd, args, via) => S10Se.Execute(cmd, args, via) /* S10 hook: de.s10 {paste|clear|status}, null for other verbs */ ?? LookMatch.Execute(cmd, args, via) /* S7t-look-match: de.s7t {look}, null for other keys */ ?? S7tComposite.Execute(cmd, args, via) /* S7t-se-composite: de.s7t {fade|flash|fadeAprime|impostors|action: composite}, null for other keys */ ?? S7pGameplaySe.Execute(cmd, args, via) /* S7p-gameplay: de.s7p {hold|yawfix|readAvgMs|action: gameplay}, null for other keys */ ?? S8aCommands.Execute(cmd, args, via) /* S8a (B2 hook): de.s8a {status|drill|overlay|save|rays}, null for other keys */ ?? S11Commands.Execute(cmd, args, via) /* S11 hook: de.s11, null for other verbs */ ?? S16Commands.Execute(cmd, args, via) /* S16 hook: de.s16, null for other verbs */ ?? S15Commands.Execute(cmd, args, via) /* S15 hook: de.s15, null for other verbs */ ?? S13Se.Execute(cmd, args, via) /* S13 hook: de.s13, null for other verbs */ ?? S12Commands.Execute(cmd, args, via) /* S12 hook: de.s12, null for other verbs */ ?? S17Se.Execute(cmd, args, via) /* S17 hook: de.s17, null for other verbs */ ?? S7Commands.Execute(cmd, args, via) ?? S7aCommands.Execute(cmd, args, via) ?? Combat.S6Commands.Execute(cmd, args, via) ?? S5Commands.Execute(cmd, args, via) ?? Terrain.S4Commands.Execute(cmd, args, via) ?? S3Commands.Execute(cmd, args, via) ?? S2bCommands.Execute(cmd, args, via) ?? S2Commands.Execute(cmd, args, via) ?? S2Motion.Execute(cmd, args, via) ?? S1Commands.Execute(cmd, args, via),
            };
            // S3: C2 right before C1 in the PrepareForDraw postfix; de.s3 h=raycast asks Subnautica over the relay
            _link.BeforeSeFrame = GridMirrorProducer.OnSeFrame;
            S3Commands.RelayToSn = (cmd, args, onReply) =>
            {
                DevChannel d = _dev;
                if (d == null) return "no dev channel";
                string why;
                return d.RelayForReply(cmd, args, Win32.TickCount64(), onReply, out why) ? null : why;
            };
            _perf = new PerfLog(_initTimestamp);
            Phases.PhaseForced = phase => { Link l = _link; if (l != null) l.WritePhaseNow(phase, Win32.TickCount64()); };
            _s1Active = true; // S1: session component (site binding), C1 writer, test commands

            DeLog.Info("DE.Init done in " + ElapsedMs(_initTimestamp).ToString("0.0", CultureInfo.InvariantCulture) + " ms" +
                       (_harmonyOk ? "" : " (start-up self-check FAILED: display bridge refused)") +
                       "; link opens " + WireInfo.SectionName + " once per second (open only, never create)");
        }

        private string HelloLine()
        {
            int? game = null;
            try { game = Sandbox.Game.MyPerGameSettings.BasicGameInfo.GameVersion; } catch (Exception) { }
            return "DE.Hello side=se plugin=" + Version + " build=" + BuildFlavor.Name + " flavor=" + BuildFlavor.Name + /* S9-release-flavor */
                   " wire=" + WireInfo.WireVersion + " layout=" + WireInfo.LayoutMajor + "." + WireInfo.LayoutMinor +
                   " layoutHash=" + LayoutHash.Format(LayoutHash.Compiled) +
                   " site=" + SiteV1.SiteVersion + " siteHash=" + LayoutHash.Format(SiteV1.SiteHash) +
                   " Rs=" + SiteV1.RsExpected.ToString("R", CultureInfo.InvariantCulture) +
                   " damageScale=" + WireInfo.DamageScaleVersion +
                   " game=" + (game.HasValue ? game.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                   " pulsar=" + AssemblyVersion("Legacy") + " harmony=" + AssemblyVersion("0Harmony") +
                   " clr=" + Environment.Version + " x64=" + Environment.Is64BitProcess + " pid=" + Win32.CurrentProcessId() +
                   " section=" + WireInfo.SectionName + (Mutants.Active.Length > 0 ? " MUTANTS=" + Mutants.Active : "");
        }

        /// <summary>
 /// S4: the core's log sink and the provider registration (<c>MyOctreeStorage.RegisterTypes</c>, guarded, once per
 /// process). Isolated (NoInlining) so that a missing SE member fails here and not in Init; a failure only makes the
 /// terrain gate refuse (condition 4).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitTerrain()
        {
            try
            {
                DeepEngineers.Terrain.TerrainLog.Sink = DeLog.Info;
                Terrain.TerrainGate.RegisterProviderOnce();
            }
            catch (Exception e) { DeLog.Info("DE.Terrain PROVIDER FAIL " + e.GetType().Name + ": " + e.Message + " (terrain gate refuses)"); }
        }

        /// <summary>Isolated so that a missing 0Harmony fails here (JIT of this method) and not in Init.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyHarmony()
        {
            try
            {
                HarmonyPatches.Apply();
                S1Patches.Apply(); // S1: MySandboxGame.PrepareForDraw postfix (C1 writer)
                _harmonyOk = HarmonyPatches.AllApplied && S1Patches.Applied;
            }
            catch (Exception e)
            {
                _harmonyOk = false;
                DeLog.Info("DE.Harmony FAIL " + e.GetType().Name + ": " + e.Message + " (no phase patches; display bridge refused)");
            }
            // S3: a second PrepareForDraw postfix for the s3.gps rows (a failure disables only those rows)
            try { S3Patches.Apply(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S3 FAIL " + e.GetType().Name + ": " + e.Message + " (s3.gps rows off; S0-S2b and the S3 streams unaffected)"); }
            // S5: the render-cell and paint marks of the grid exporter (a failure leaves only the sweep; nothing else depends on them)
            try { S5Patches.Apply(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S5 FAIL " + e.GetType().Name + ": " + e.Message + " (S5 edit marks off; the exporter's sweep still finds edits)"); }
            // S4: the terrain fail-safe's save block and the edit guards (each target logged DE.Terrain GUARD; a missing one
 // leaves only that path unguarded, never the terrain or the display bridge)
            try { Terrain.TerrainPatches.Apply(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S4 FAIL " + e.GetType().Name + ": " + e.Message + " (terrain edit guards and save block off)"); }
            try { S8aHost.ApplyPatches(); } // S8a (B2 hook): the MySession.Save postfix of the drill overlay (inert unless DEEPENGINEERS_S8A=on)
            catch (Exception e) { DeLog.Info("DE.Harmony S8a FAIL " + e.GetType().Name + ": " + e.Message + " (the drill overlay is not saved)"); }
            // S2b swimming patches on their own, independent of S2 (testable in SE alone): a failure disables only the swim
 // controller (stock SE movement), never the display bridge
            try { GhostGizmoPatch.Apply(); }   // S7a review F8: UpdateGizmo postfix
            catch (Exception e) { DeLog.Info("DE.Harmony S7a ghost patch failed: " + e.GetType().Name + ": " + e.Message); }
            try { S7EffectPatches.Apply(); }   // S7 effects: the five event postfixes (each missing target turns only its kind off)
            catch (Exception e) { DeLog.Info("DE.Harmony S7 effects FAIL " + e.GetType().Name + ": " + e.Message + " (no SE effect events)"); }
            try { SwimPatches.Apply(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S2b FAIL " + e.GetType().Name + ": " + e.Message + " (swim controller off; S0/S1/S2 unaffected)"); }
            try { LookMatch.Apply(); } // S7t-look-match (T5): the render-copy prefixes of the look match (a failure turns only that off)
            catch (Exception e) { DeLog.Info("DE.Harmony S7t look FAIL " + e.GetType().Name + ": " + e.Message + " (SE keeps its own look)"); }
            try { DampenerHold.Apply(); } // S7p-gameplay (P12): dampeners hold the depth against the Water Mod's buoyancy (a failure turns only that off)
            catch (Exception e) { DeLog.Info("DE.Harmony S7p hold FAIL " + e.GetType().Name + ": " + e.Message + " (the depth hold is off)"); }
            try { S9StarSystem.Install(); } // a failure keeps SE's own
            catch (Exception e) { DeLog.Info("DE.Harmony S9 card swap FAIL " + e.GetType().Name + ": " + e.Message + " (SE's own Star System)"); }
            try { S12Oxygen.Install(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S12 oxygen FAIL " + e.GetType().Name + ": " + e.Message + " (SE's own oxygen lookup stays)"); }
            // S2 patches on their own: a missing S2 target refuses only the S2 feature that needs it (S2Patches)
            if (!S2Host.Enabled) return;
            try { S2Patches.Apply(); }
            catch (Exception e) { DeLog.Info("DE.Harmony S2 FAIL " + e.GetType().Name + ": " + e.Message + " (S2 display/audio refused; S0/S1 unaffected)"); }
        }

        private void HookSessionEvents()
        {
            try
            {
                MySession.OnLoading += OnSessionLoading;
                MySession.OnUnloading += OnSessionUnloading;
                MySession.LoadingStep = (Action<LoadingProgress>)Delegate.Combine(MySession.LoadingStep, new Action<LoadingProgress>(OnLoadingStep));
                MySession.LoadingLocalAdd = (Action<float>)Delegate.Combine(MySession.LoadingLocalAdd, new Action<float>(OnLoadingLocal));
                MySession.LoadingLocalTotalSet = (Action<float>)Delegate.Combine(MySession.LoadingLocalTotalSet, new Action<float>(OnLoadingLocal));
                MySession.LoadingLocalClear = (Action)Delegate.Combine(MySession.LoadingLocalClear, new Action(OnLoadingLocalClear));
                DeLog.Info("DE.Session hooks: MySession.OnLoading, OnUnloading, LoadingStep, LoadingLocalAdd/TotalSet/Clear (heartbeat during world loads)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Session hooks FAILED " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void HookChat()
        {
            try
            {
                MyAPIUtilities.Static.MessageEntered -= OnMessageEntered;
                MyAPIUtilities.Static.MessageEntered += OnMessageEntered;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Dev chat hook FAILED " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ===================================================================================================== Update

        public void Update()
        {
            if (_inert) return;
            if (_measureOnly) { MeasureOnlyTick(); return; }
            if (_link == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                UpdateCore();
            }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(Win32.TickCount64()))
                    DeLog.Info("DE.Error Update #" + _errors + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
            }
            // Null when InitCore threw between creating the link and the sampler: Update must never throw into SE.
            PerfLog perf = _perf;
            if (perf != null) perf.AddUpdate(Stopwatch.GetTimestamp() - t0);
        }

        private void UpdateCore()
        {
            ulong now = Win32.TickCount64();
            _updates++;

            bool session = GameState.SessionExists;
            uint phase = Phases.Compute(session, GameState.GameplayScreen, GameState.GameplayLoadingDone, now);
            if (phase != _lastPhase)
            {
                OnPhaseChanged(_lastPhase, phase);
                _lastPhase = phase;
            }

            if (phase == SePhase.InWorld) ActivationGate.EvaluateOnce(Phases.WorldEpoch);
            else if (!session && ActivationGate.State != GateState.NoSession) ActivationGate.Clear();

            // Hello (CONTRACT §6): in a world, gate open, version accepted, start-up self-checks passed and, from S1, the
 // site bound through the Water Mod API (CONTRACT §3): Rs is the validated runtime value.
            bool inWorld = phase == SePhase.InWorld;
            SiteBinding.Tick(now, inWorld); // 10 s re-check of Rs and crush damage
            uint hello = inWorld && ActivationGate.IsOpen && !DisplayRefused && SiteBinding.IsOk ? Phases.WorldEpoch : 0u;
            _helloEpoch = hello;
            S1Commands.Tick(inWorld); // de.path: this tick's WM-3 sample (before PrepareForDraw)
            S2Motion.Tick(inWorld);   // de.motion (S2.N2 bob, S2.N7 dive): this tick's sample (before PrepareForDraw)
            SwimGuard.PluginUpdate(inWorld); // S2b: forget the swim state when the world is gone
            Jsonl.FlushIfDue(now);    // buffered per-tick test records: at most one flush per second

            Terrain.S4Commands.Tick(now, inWorld); // S4: the asynchronous de.terrain harness parts (at1, bake, drop, walk, perf)
            S10Se.Tick(now, inWorld); // S10 hook: the s10.se row (engine frame time, update interval) and the de.s10 paste job; measurement only, never throws

            uint seFlags = 0;
            if (ActivationGate.IsOpen) seFlags |= SeFlags.GateOpen;
            if (DisplayRefused) seFlags |= SeFlags.DisplayRefused;
            if (Terrain.TerrainTiles.TerrainUp) seFlags |= SeFlags.TerrainUp;            // S4 (CONTRACT §1.2)
            if (Terrain.TerrainFailSafe.Active) seFlags |= SeFlags.FailSafeActive;       // S4 (CONTRACT §7)

            bool foreground = GameState.Foreground;
            bool paused = GameState.Paused;
            uint stateFlags = 0;
            if (foreground) stateFlags |= SeStateFlags.SeForeground;
            if (paused) stateFlags |= SeStateFlags.SePaused;
            if (Phases.Exiting) stateFlags |= SeStateFlags.SeExiting;

            // seHwnd is only published while linked. After an attach the first refresh is due at once, so a fresh handle follows within one tick.
            if (_link.State == LinkState.Up && now >= _nextHwndMs)
            {
                // S2: MyWindowsWindows.WindowHandle (the render window), Process.MainWindowHandle as the fallback (S0 D35)
                _hwnd = (ulong)SeWindow.Handle.ToInt64();
                _nextHwndMs = now + 10000;
            }

            ulong tick = GameState.GameplayTick;
            var input = new SeTickInput
            {
                Phase = phase,
                SeFlags = seFlags,
                StateFlags = stateFlags,
                Tick = tick,
                WorldEpoch = Phases.WorldEpoch,
                HelloEpoch = hello,
                Rs = hello != 0 ? SiteBinding.Rs : 0.0,
                SeHwnd = _hwnd,
                // S1 C5 fields from the last C1 frame (zone from the map, perf mode); S0 values while not bound
                SiteActive = hello != 0 && FrameWriter.HasC5,
                Zone = FrameWriter.C5Zone,
                PerfMode = FrameWriter.C5PerfMode,
                TargetFps = FrameWriter.C5TargetFps,
                CamSnY = FrameWriter.C5CamSnY,
                CamVelSn = FrameWriter.C5CamVelSn,
            };
            // S2: view state, display design, perf mode from the state machine, S2 flags (the S1 values when S2 is off)
            S2C5 s2 = S2Host.Tick(now, phase, _link, hello);
            if (s2.Active)
            {
                input.S2Active = true;
                input.ViewState = s2.ViewState;
                input.ApproachMode = s2.ApproachMode;
                input.DisplayMode = s2.DisplayMode;
                input.PerfMode = s2.PerfMode;
                input.TargetFps = s2.TargetFps;
                input.S2Flags = s2.Flags;
                input.SnBusTarget = s2.SnBusTarget;
                input.CrossfadeMs = s2.CrossfadeMs;
                input.SeRect = s2.SeRect;
                input.CrossingTick = s2.CrossingTick;
            }
            _link.Tick(now, ref input);
            _dev.Tick(now);
            S10Se.LapStart(); // S10 hook: the lap marks below time the producer, the gameplay hooks and the S7/S8a hosts
            GridMirrorProducer.Tick(now, _link, hello, Phases.WorldEpoch, inWorld); // S3: C3/C4 streams of the mirrored grids (budgeted)
            S10Se.Lap(S10PerfRules.SeStageProducer); // S10 hook
            Combat.DamageConsumer.Tick(now, _link, Phases.WorldEpoch, inWorld); // S6: C8 damage records (validated, deduplicated, applied)
            S11Consumer.Tick(now, inWorld); // S11 hook: held grids' forces, grab timeouts, EMP restores (guarded, never throws)
            S16Host.Tick(now, inWorld); // S16 hook: crush depths, crush damage through SE's block damage, the depth HUD, the C5 extension (guarded, never throws)
            S16ThrustProbe.Tick(now, inWorld); // thrust every 5 s
            S15Se.Tick(now, inWorld, input.S2Active ? input.SnBusTarget : 0f); // S15 hook: the Cyclops voice events (C5 0xAC), SE's planets for Subnautica's sky (C6 region space), the water material (guarded, never throws)
            S12Se.Tick(now, inWorld); // S12 hook: flood tracks and venting Air Vents near the camera, the C6 block at +0x800 (guarded, never throws)
            S17Se.Tick(now, inWorld); // S17 hook: the fish target from C9, F, the C5 catch request, the item on Subnautica's answer (guarded, never throws)
            S10Se.Lap(S10PerfRules.SeStageHooks); // S10 hook: S6 damage, S11, S16, S15, S12 above
            S7Host.Tick(now, _link, Phases.WorldEpoch, inWorld); // S7: C12 statics hosts (S7Statics; guarded, never throws)
            S8aHost.Tick(now, _link, inWorld); // S8a (B2 hook): the drill overlay's queue, the converter, the C7 acks, the s8a.* rows (guarded, never throws)
            S10Se.Lap(S10PerfRules.SeStageS8a); // S10 hook: S7 statics and S8a above
            S8aBeacon.Tick(now, inWorld); // S8a Stage E: the Aurora distress beacon on the first arrival in DE_4546B's gravity (S8a on or off; never throws)
            S7pGameplaySe.Tick(now, tick, inWorld); // S7p-gameplay: camera yaw watch (P13), s7p.hold (P12) and s7p.export (P14) rows; guarded

            if (_updates == 600) DeLog.Info("DE.Alive 600 updates");
            if (_perf.Due(now)) _perf.Write(now, phase, _link, Phases.WorldEpoch, hello, paused, foreground, tick);

            // S1.M2 seeded failure only: C1 from here (the previous tick's camera) instead of the PrepareForDraw postfix
            FrameWriter.Capture(true);
        }

        private void MeasureOnlyTick()
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                ulong now = Win32.TickCount64();
                _updates++;
                if (_perf.Due(now))
                {
                    uint phase = GameState.SessionExists && GameState.GameplayScreen ? SePhase.InWorld : SePhase.Menu;
                    _perf.Write(now, phase, null, 0, 0, GameState.Paused, GameState.Foreground, GameState.GameplayTick);
                }
            }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(Win32.TickCount64())) DeLog.Info("DE.Error measure #" + _errors + " " + e.GetType().Name + ": " + e.Message);
            }
            PerfLog perf = _perf;
            if (perf != null) perf.AddUpdate(Stopwatch.GetTimestamp() - t0);
        }

        /// <summary>`DEEPENGINEERS_SE_MODE=measure`: the S0.N7 baseline mode.</summary>
        public const string ModeEnvVar = "DEEPENGINEERS_SE_MODE";

        private static bool IsMeasureOnlyMode()
        {
            if (!BuildFlavor.IsDev) return false; // S9-release-flavor: the S0.N7 baseline mode is a dev/stand-in hook
            try { return string.Equals(Environment.GetEnvironmentVariable(ModeEnvVar), "measure", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        /// <summary>
 /// Display bridge refused: version pin, WM-1 self-test, Harmony self-check (whole process), or the site
 /// validation refused (this world session; CONTRACT §3).
 /// </summary>
        private bool DisplayRefused
        {
            get { return VersionGate.Refused || !_selfTestPass || !_harmonyOk || SiteBinding.IsRefused; }
        }

        private void OnPhaseChanged(uint from, uint to)
        {
            DeLog.Info("DE.Phase " + (from == uint.MaxValue ? "start" : SePhase.Name(from)) + " -> " + SePhase.Name(to) +
                       " worldEpoch=" + Phases.WorldEpoch + (to == SePhase.LoadingWorld ? " target=" + Phases.LoadTarget : ""));
            if (from == SePhase.LoadingWorld && to == SePhase.InWorld && Phases.LoadIsGameplay) LogWorldLoadTiming();
        }

        /// <summary>
 /// S1.N12 world-load time: from the <c>MyGuiScreenLoading.RunLoad</c> prefix to the first update in world (this
 /// plugin Update), with the RunLoad duration and the first session <c>UpdateBeforeSimulation</c> on the way.
 /// </summary>
        private static void LogWorldLoadTiming()
        {
            long start = Phases.LoadStartTimestamp;
            if (start == 0) return;
            long now = Stopwatch.GetTimestamp(), end = Phases.RunLoadEndTimestamp, sim = SiteBinding.FirstSimUpdateTimestamp;
            Func<long, string> ms = t => t > 0 ? ((t - start) * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
            DeLog.Info("DE.WorldLoad S1.N12 worldEpoch=" + Phases.WorldEpoch + " target=" + Phases.LoadTarget +
                       " runLoadToFirstUpdateMs=" + ms(now) + " runLoadMs=" + ms(end) + " runLoadToFirstSimUpdateMs=" + ms(sim) +
                       " (RunLoad prefix -> first plugin Update in world; heartbeat kept by LoadingStep/LoadingLocal* during the load)");
        }

        // ============================================================================================ game callbacks

        private void OnSessionLoading()
        {
            try
            {
                Link l = _link;
                if (l != null) l.Beat(Win32.TickCount64(), -1);
                double s = ElapsedMs(_initTimestamp) / 1000.0;
                if (!_onLoadingSeen)
                {
                    _onLoadingSeen = true;
                    DeLog.Info("DE.LoadOrder MySession.OnLoading +" + s.ToString("0.000", CultureInfo.InvariantCulture) +
                               " s after plugin Init (Init first: S0.N8 order holds) worldEpoch=" + Phases.WorldEpoch);
                }
                else
                {
                    DeLog.Info("DE.Session OnLoading +" + s.ToString("0.000", CultureInfo.InvariantCulture) + " s after Init worldEpoch=" + Phases.WorldEpoch);
                }
            }
            catch (Exception) { }
        }

        private void OnSessionUnloading()
        {
            try { DeLog.Info("DE.Session OnUnloading worldEpoch=" + Phases.WorldEpoch); }
            catch (Exception) { }
            S11Consumer.OnUnload(); // S11 hook: the EMP's switched-off producers on again before the world is saved or closed
        }

        private void OnLoadingStep(LoadingProgress step)
        {
            try { Link l = _link; if (l != null) l.Beat(Win32.TickCount64(), (int)step); }
            catch (Exception) { }
        }

        private void OnLoadingLocal(float _)
        {
            try { Link l = _link; if (l != null) l.Beat(Win32.TickCount64(), -1); }
            catch (Exception) { }
        }

        private void OnLoadingLocalClear()
        {
            OnLoadingLocal(0f);
        }

        private void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            try
            {
                DevChannel d = _dev;
                if (d != null && d.HandleChat(messageText, Win32.TickCount64())) sendToOthers = false;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error chat " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------------------------- from the Harmony patches

        internal static void OnRunLoadStarted(bool gameplay, string target)
        {
            DeLog.Info("DE.Phase LoadingWorld (MyGuiScreenLoading.RunLoad prefix) target=" + target +
                       (gameplay ? " worldEpoch=" + Phases.WorldEpoch : " (not a world load: worldEpoch unchanged " + Phases.WorldEpoch + ")"));
        }

        internal static void OnRunLoadEnded()
        {
            Plugin p = Instance;
            if (p == null) return;
            DeLog.Info("DE.Phase RunLoad returned +" + (ElapsedMs(p._initTimestamp) / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " s after Init");
        }

        internal static void OnExiting()
        {
            // S8-exit (ROADMAP S8.T-L1c): seExiting first, so Subnautica starts quitting even if the flush below fails or
 // Pulsar's kill comes first; then the buffered per-tick test records (c1trace, wm3) before Pulsar's kill
            Plugin p = Instance;
            Link l = p != null ? p._link : null;
            if (l != null) l.OnExiting(Win32.TickCount64());
            try { Jsonl.Flush(); } catch (Exception) { }
            if (p == null) return;
            DeLog.Info("DE.Exit MySandboxGame.ExitThreadSafe prefix: sePhase=Exiting and C5 seExiting written before Pulsar's kill");
        }

        // ================================================================================================ dev info

        private Dictionary<string, object> VersionInfo()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "side", "se" },
                { "plugin", Version },
                { "build", BuildFlavor.Name },
                { "wireVersion", WireInfo.WireVersion },
                { "layout", WireInfo.LayoutMajor + "." + WireInfo.LayoutMinor },
                { "layoutHash", LayoutHash.Format(LayoutHash.Compiled) },
                { "siteVersion", SiteV1.SiteVersion },
                { "siteHash", LayoutHash.Format(SiteV1.SiteHash) },
                { "damageScaleVersion", WireInfo.DamageScaleVersion },
                { "gameVersion", VersionGate.Found.HasValue ? (object)VersionGate.Found.Value : null },
                { "gameVersionExpected", VersionGate.Expected },
                { "versionGate", VersionGate.Status.ToString() },
                { "gate", ActivationGate.State.ToString() },
                { "harmony", _harmonyOk },
                { "selfTest", _selfTest != null ? _selfTest.LogLine : "not run" },
                { "mutants", Mutants.Active },
            };
        }

        private Dictionary<string, object> StateInfo()
        {
            Link l = _link;
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "side", "se" },
                { "phase", SePhase.Name(_lastPhase) },
                { "worldEpoch", Phases.WorldEpoch },
                { "helloEpoch", _helloEpoch },
                { "gate", ActivationGate.State.ToString() },
                { "gateReason", ActivationGate.Reason },
                { "displayRefused", DisplayRefused },
                { "link", l != null ? l.State.ToString() : "None" },
                { "site", SiteDecision.StatusName(SiteBinding.Status) },
                { "siteReason", SiteBinding.Last.Reason },
                { "c1Written", FrameWriter.Written },
                { "zone", ZoneTracker.Name(FrameWriter.C5Zone) },
                { "perfMode", FrameWriter.C5PerfMode },
            };
            if (l != null)
            {
                d["peer"] = l.Peer.ToString();
                d["peerExitReason"] = l.PeerExitReason;
                d["seGeneration"] = l.SeGeneration;
                d["snPid"] = l.LastSnPid;
                d["snGeneration"] = l.LastSnGeneration;
                d["snPhase"] = SnPhase.Name(l.LastSnPhase);
                d["snHeartbeatAgeMs"] = l.LastSnHeartbeatAgeMs;
                d["linkEpoch"] = l.LinkEpoch;
                d["devControl"] = l.DevControlWord;
                d["relay"] = l.RelayHeld;
                d["attaches"] = l.Attaches;
                d["newPeers"] = l.NewPeers;
                d["resyncs"] = l.Resyncs;
                d["stalls"] = l.Stalls;
                d["peerExits"] = l.PeerExits;
            }
            d["frameTimeAvgMs"] = (double)GameState.FrameTimeAvgMs;
            d["swim"] = SwimGuard.StateName; // S2b
            try { S2Host.AddState(d); } catch (Exception) { }
            return d;
        }

        // ================================================================================================= helpers

        private static void WriteGameLog(string line)
        {
            MyLog log = MyLog.Default;
            if (log != null) log.WriteLine(line);
        }

        private static void ShowChat(string sender, string text)
        {
            DeLog.Info("DE.Dev chat: " + text);
            try { MyHud.Chat.ShowMessage(sender, text); }
            catch (Exception) { /* no HUD at the main menu */ }
        }

        private static string AssemblyVersion(string simpleName)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    AssemblyName n = a.GetName();
                    if (string.Equals(n.Name, simpleName, StringComparison.OrdinalIgnoreCase)) return n.Version.ToString();
                }
            }
            catch (Exception) { }
            return "not-loaded";
        }

        private static double ElapsedMs(long since)
        {
            return (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        }

        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }

        public void Dispose()
        {
            // Not called reliably under Pulsar (process killed in ExitThreadSafe); nothing depends on it.
            try { DeLog.Info("DE.Dispose after " + _updates + " updates"); } catch (Exception) { }
            Instance = null;
        }
    }
}
