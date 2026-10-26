using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeepEngineers.SN
{
    /// <summary>What the S1 host needs from the bridge behaviour (implemented by <see cref="BridgeBehaviour"/>).</summary>
    internal interface IS1Bridge
    {
        uint Phase { get; }
        ulong SnFrame { get; }
        bool Quitting { get; }
        void SetPhase(uint phase, string why);
        /// <summary>Writes C7 now (end of frame, after presentedSeTick was stamped).</summary>
        void PublishStatus();
    }

    /// <summary>
 /// The S1 "pose bridge" of the Subnautica side (ROADMAP S1 SN plugin), driven by the S0 bridge behaviour:
 /// <list type="number">
 /// <item>Menu: "Run in background" enforced (<see cref="UiHider"/>), then the host save is chosen and loaded
 /// (<see cref="HostSave"/>): snPhase Menu → LoadingSave.</item>
 /// <item>World loaded: Freedom and not invisible asserted (else `DE.HostSave REFUSE`, nothing else happens), then the
 /// camera rig is taken over (<see cref="CameraDriver"/>), the Player becomes the ghost (<see cref="GhostPlayer"/>),
 /// the UI is hidden; the frame after, escapePod/currentSub are asserted and snPhase becomes ReadyIdle.</item>
 /// <item>ReadyIdle: C1 drives the camera (LateUpdateLast and the main camera's onPreCull), the Player follows its rule,
 /// input is cleared every frame, telemetry (<see cref="S1Telemetry"/>) and captures (<see cref="CaptureService"/>)
 /// run; C7 carries worldLoaded, streamingSettled, combatEnabled, areaBuilt, uiHidden, runInBackgroundForced,
 /// presentedSeTick, areaBuiltSeTick/Pos and resamplesLastFrame. Outside the dive zone (C5 zone FAR or off-site, or
 /// the snapshot beyond the zone, above +350 m or below −7,900 m) nothing is driven (<see cref="DriveGate"/>).</item>
 /// </list>
 /// WarmingUp/Active (S2) are not entered in S1: Subnautica idles in ReadyIdle with its camera driven. Each feature runs
 /// in its own try/catch; its first exception is logged once (`DE.S1 FEATURE … DISABLED`) and only that feature stops.
 /// </summary>
    internal sealed unsafe class S1Host
    {
        public const int StatsEveryFrames = 300;
        public const ulong AudioSummaryMs = 60000;
        public const ulong C5VelocityFreshMs = 500;

        private readonly IS1Bridge _bridge;
        private readonly byte* _base;
        private readonly Transform _root;

        public readonly HostSave Save = new HostSave();
        public readonly UiHider Ui = new UiHider();
        public readonly GhostPlayer Ghost = new GhostPlayer();
        public readonly S1Telemetry Tel = new S1Telemetry();
        public readonly CameraDriver Cam;
        public readonly CaptureService Capture;

        /// <summary>
 /// S3: called right after the camera was placed for a sample (false: LateUpdateLast, true: the main camera's
 /// onPreCull), so the mirrored entities follow the same SE ticks and alpha. Must not throw.
 /// </summary>
        public Action<CamSample, bool> CameraSampled;

        /// <summary>
 /// S5a: called right after the ghost step of LateUpdateLast (the Player moved, its renderers re-hidden) with the
 /// sample, the camera position and whether the camera was driven; and after the main camera's onPreCull
 /// re-sample. Each is guarded here: an exception logs once and drops the hook (the camera driver never sees it).
 /// </summary>
        public Action<CamSample, Vector3, bool> AfterGhostFrame;
        public Action<CamSample> AfterPreCullFrame;

        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private bool _worldSetup, _assertPending, _ready, _sceneHooked;
        private int _setupFrame;
        private ulong _nextSecondMs, _nextAudioMs, _readyAtMs;
        private long _lastSnaps, _lastResets;
        private bool _combatEnabled;

        // C5 camera velocity for the forward box
        private Vector3 _c5Vel;
        private ulong _c5VelMs;

        // per-frame steps as cached delegates over fields (no closure allocated per frame)
        private ulong _now, _frame;
        private CamSample _lateSample, _preCullSample;
        private Vector3 _lateCamPos;
        private bool _lateDriven;

        // de.pixel: requests answered at the first presented frame with SE tick >= minSeTick
        private readonly List<PixelRequest> _pixels = new List<PixelRequest>();
        public const ulong PixelTimeoutMs = 2000;

        private sealed class PixelRequest
        {
            public string Label;
            public Vector3 Point;
            public ulong MinSeTick;
            public ulong StartMs;
        }
        private readonly Action _stepClearInput, _stepUiFrame, _stepTelemetryFrame, _stepGhostFrame, _stepCapture, _stepCapturePreCull, _stepPixels;

        public S1Host(IS1Bridge bridge, byte* sectionBase, Transform root)
        {
            _bridge = bridge;
            _base = sectionBase;
            _root = root;
            Cam = new CameraDriver(Layout.RegionPtr(sectionBase, RegionId.C1SeFrame), Win32.QpcFrequency());
            Capture = new CaptureService(sectionBase);
            _stepClearInput = ClearInput;
            _stepUiFrame = UiFrame;
            _stepTelemetryFrame = TelemetryFrame;
            _stepGhostFrame = GhostFrame;
            _stepCapture = CaptureFrame;
            _stepCapturePreCull = CapturePreCull;
            _stepPixels = ServePixels;
        }

        public bool InWorld { get { return _ready; } }

        public void Start()
        {
            DeLog.Info("DE.S1 start: " + S1Switches.Describe() + " mutants=" + DevMutants.Describe());
            Guard("patches", () => S1Patches.InstallCore());
            Guard("hostsave", () => Save.Attach());
            Guard("scenes", HookScenes);
            Cam.Trace = S1Switches.Trace;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HookScenes()
        {
            if (_sceneHooked) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHooked = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try { if (_worldSetup) Ui.MarkSceneLoaded(); } catch (Exception) { }
        }

        public void Dispose()
        {
            try { Cam.Uninstall(); } catch (Exception) { }
            try { Save.Detach(); } catch (Exception) { }
            try { if (_sceneHooked) SceneManager.sceneLoaded -= OnSceneLoaded; } catch (Exception) { }
            _sceneHooked = false;
        }

        // ------------------------------------------------------------------------------------------ per frame (Update)

        public void Tick(ulong now)
        {
            uint phase = _bridge.Phase;
            ulong frame = _bridge.SnFrame;

            if (!_bridge.Quitting)
            {
                if (phase >= SnPhase.Menu && phase <= SnPhase.Active && now >= _nextSecondMs && !_ready)
                    Guard("runInBackground", () => Ui.NoteRunInBackground()); // read only before the world (see UiHider)

                if (phase == SnPhase.Menu && !Disabled("hostsave"))
                {
                    bool started = false;
                    Guard("hostsave", () => started = Save.TickMenu(now));
                    if (started) _bridge.SetPhase(SnPhase.LoadingSave, "host save " + Save.Choice.Slot.Slot + " via uGUI_MainMenu.LoadGameAsync");
                }
                else if (phase == SnPhase.LoadingSave)
                {
                    TickLoading(now, frame);
                }
                else if (_ready)
                {
                    TickWorld(now, frame);
                }
            }

            _now = now;
            _frame = frame;
            if (!Disabled("capture")) Guard("capture", _stepCapture);

            if (now >= _nextSecondMs) _nextSecondMs = now + 1000;
        }

        private void TickLoading(ulong now, ulong frame)
        {
            if (!_worldSetup)
            {
                if (Save.State != HostSaveState.Loading || Disabled("hostsave")) return;
                bool loaded = false;
                Guard("hostsave", () => loaded = Save.TickLoading(now));
                if (!loaded) return;
                bool modeOk = false;
                Guard("hostsave", () => modeOk = Save.CheckMode());
                if (!modeOk) return;
                SetUpWorld(now);
                return;
            }
            if (_assertPending && Time.frameCount > _setupFrame)
            {
                _assertPending = false;
                Guard("ghost", () => Ghost.AssertAfterFirstFrame());
                _ready = true;
                _readyAtMs = now;
                _nextAudioMs = now + AudioSummaryMs;
                Guard("combat", () => _combatEnabled = !GameModeUtils.IsInvisible());
                _bridge.SetPhase(SnPhase.ReadyIdle, "host world loaded in " + Save.LoadMs + " ms, ghost Player set, camera rig driven by C1" +
                                                    (Cam.Installed ? "" : " (camera driver NOT installed)"));
            }
        }

        /// <summary>The world is up and the mode is Freedom: camera rig, ghost Player, hidden UI.</summary>
        private void SetUpWorld(ulong now)
        {
            _worldSetup = true;
            string error;
            if (!Cam.Install(out error)) DeLog.Error("DE.S1 FEATURE camera DISABLED: " + error + " (the camera is not driven)");
            Cam.AfterLateUpdate = OnAfterLateUpdate;
            Cam.AfterPreCull = OnAfterPreCull;
            Cam.EndOfFrame = _bridge.PublishStatus;
            Guard("runInBackground", () => Ui.EnforceRunInBackground()); // before ReadyIdle: before SE can take the focus
            Vector3 camPos = Vector3.zero;
            Guard("ghost", () =>
            {
                camPos = CameraPosition();
                Ghost.Setup(_root, camPos);
            });
            Guard("ui", () => Ui.ApplyHide(now, "world loaded"));
            _setupFrame = Time.frameCount;
            _assertPending = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector3 CameraPosition()
        {
            Camera c = MainCamera.camera;
            return c != null ? c.transform.position : (Player.main != null ? Player.main.transform.position : Vector3.zero);
        }

        private void UiFrame() { Ui.PerFrame(_now); }

        private void CaptureFrame()
        {
            Capture.PollC10(_now, _frame);
            bool driven = _ready && Cam.Installed && !Cam.Failed && Cam.LastLateDriven;
            Capture.Tick(_now, _frame, driven, Cam.PresentedSeTick);
        }

        private void CapturePreCull() { Capture.OnPreCull(_preCullSample, _bridge.SnFrame, Cam.PresentedSeTick); }

        private void TelemetryFrame()
        {
            bool snapped = Cam.Reader.Snaps != _lastSnaps;
            string why = snapped ? "C1 snap: " + Cam.Reader.LastPushWhy : null;
            if (Cam.Reader.Resets != _lastResets && !snapped) { snapped = true; why = "C1 history reset: " + Cam.Reader.LastPushWhy; }
            _lastSnaps = Cam.Reader.Snaps;
            _lastResets = Cam.Reader.Resets;
            Tel.VelocitySn = Velocity(_now);
            Camera c = MainCam();
            if (c != null) Tel.PerFrame(_now, c.transform.position, c.transform.forward, Cam.PresentedSeTick, snapped, why);
        }

        private void GhostFrame() { Ghost.PerFrame(_lateSample, _lateCamPos, _lateDriven, Cam.OverrideActive); }

        private void TickWorld(ulong now, ulong frame)
        {
            _now = now;
            if (Ghost.Ready && !Disabled("input")) Guard("input", _stepClearInput);
            if (!Disabled("ui")) Guard("ui", _stepUiFrame);
            if (!Disabled("telemetry")) Guard("telemetry", _stepTelemetryFrame);

            if (now >= _nextSecondMs)
            {
                if (!Disabled("ghost")) Guard("ghost", () => Ghost.PerSecond());
                Guard("runInBackground", () => Ui.EnforceRunInBackground());
                bool focusFreezer = false;
                Guard("runInBackground", () => focusFreezer = Ui.FocusFreezerPresent());
                Guard("combat", () => _combatEnabled = !GameModeUtils.IsInvisible());
                Cam.PerSecond();
                Cam.FlushTrace(); // the buffered DE.CamTrace records go to the writer thread
                if (!Disabled("telemetry"))
                {
                    Guard("telemetry", () =>
                    {
                        Camera c = MainCam();
                        if (c == null) return;
                        Vector3 p = c.transform.position;
                        S1Patches.UnderwaterOceanLevel = Ocean.GetOceanLevel();
                        DeLog.Info(Tel.SecondLine(p, c.transform.rotation, Cam, Ghost, Ui, focusFreezer));
                    });
                }
                if (!Disabled("water")) Guard("water", () =>
                {
                    Camera c = MainCam();
                    if (c != null) Tel.RequestWaterSample(c.transform.position);
                });
            }

            if (frame % StatsEveryFrames == 0 && Cam.Installed) DeLog.Info(Cam.TakeWindowStats());

            if (now >= _nextAudioMs)
            {
                _nextAudioMs = now + AudioSummaryMs;
                DeLog.Info("DE.Audio 60s " + S1Patches.TakeAudioSummary() + " suppressed{" + S1Patches.DescribeUi() + "} saveBlocked=" + S1Patches.SavesBlocked +
                           " underwaterVariant=" + (S1Patches.UnderwaterMode == 1 ? "global" : "off"));
            }
        }

        /// <summary>The driven camera, else MainCamera.camera (Unity's == null, never ??: a destroyed camera is "null").</summary>
        private Camera MainCam()
        {
            Camera c = Cam.Camera;
            return c != null ? c : MainCamera.camera;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClearInput()
        {
            GameInput.ClearInput(1);
        }

        private Vector3 Velocity(ulong now)
        {
            if (_c5VelMs != 0 && now - _c5VelMs <= C5VelocityFreshMs) return _c5Vel;
            CamHistory h = Cam.Reader.History;
            if (h.Count >= 2)
            {
                CamSnapshot a = h[h.Count - 2], b = h.Newest;
                double dt = (b.Qpc - a.Qpc) / (double)Win32.QpcFrequency();
                if (dt > 1e-4) return new Vector3((float)((b.Px - a.Px) / dt), (float)((b.Py - a.Py) / dt), (float)((b.Pz - a.Pz) / dt));
            }
            return Vector3.zero;
        }

        // ------------------------------------------------------------------------------------------ camera callbacks

        private void OnAfterLateUpdate(CamSample s, Vector3 camPos, bool driven)
        {
            S1Patches.UnderwaterCameraY = camPos.y; // the S1.N6 "global" IsUnderwater variant reads it
            Action<CamSample, bool> mirror = CameraSampled;
            if (mirror != null) mirror(s, false); // S3: mirrors posed with this sample
            if (!Disabled("ghost"))
            {
                _lateSample = s;
                _lateCamPos = camPos;
                _lateDriven = driven;
                Guard("ghost", _stepGhostFrame);
            }
            Action<CamSample, Vector3, bool> s5a = AfterGhostFrame;
            if (s5a != null)
            {
                try { s5a(s, camPos, driven); }
                catch (Exception e)
                {
                    AfterGhostFrame = null;
                    DeLog.Error("DE.S5a FEATURE lateFrame DISABLED after " + e.GetType().Name + ": " + e.Message + " (hook dropped; the camera keeps running)\n" + e);
                }
            }
        }

        private void OnAfterPreCull(CamSample s)
        {
            Action<CamSample, bool> mirror = CameraSampled;
            if (mirror != null) mirror(s, true); // S3: mirrors re-posed with the main camera's re-sample
            if (_pixels.Count > 0 && !Disabled("pixel")) Guard("pixel", _stepPixels);
            Action<CamSample> s5a = AfterPreCullFrame;
            if (s5a != null)
            {
                try { s5a(s); }
                catch (Exception e)
                {
                    AfterPreCullFrame = null;
                    DeLog.Error("DE.S5a FEATURE preCull DISABLED after " + e.GetType().Name + ": " + e.Message + " (hook dropped; the camera keeps running)\n" + e);
                }
            }
            if (Disabled("capture")) return;
            _preCullSample = s;
            Guard("capture", _stepCapturePreCull);
        }

        /// <summary>
 /// The pending de.pixel requests (S1.G1-E5): at the first presented frame whose position and orientation come
 /// from SE ticks ≥ minSeTick (and not held by the gate), <c>Camera.WorldToScreenPoint</c> of the SN point after
 /// this frame's placement, logged as `DE.SnPixel` (pixels from the top-left corner, y down, as SE's de.pixel).
 /// Requests older than 2 s are logged as failed.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ServePixels()
        {
            CamSample s = Cam.LastPresented;
            Camera c = Cam.Camera;
            ulong now = Win32.TickCount64();
            var ci = CultureInfo.InvariantCulture;
            for (int i = _pixels.Count - 1; i >= 0; i--)
            {
                PixelRequest r = _pixels[i];
                bool ready = c != null && Cam.Gate.Open && !Cam.OverrideActive && s.Outcome != SampleOutcome.None &&
                             s.PresentedTick >= r.MinSeTick && s.NewestTick >= r.MinSeTick;
                if (!ready)
                {
                    if (now - r.StartMs > PixelTimeoutMs)
                    {
                        _pixels.RemoveAt(i);
                        DeLog.Warn("DE.SnPixel FAILED label=" + r.Label + " no presented frame with SE tick >= " + r.MinSeTick + " within " + PixelTimeoutMs +
                                   " ms (presented " + s.PresentedTick + ", newest " + s.NewestTick + ", gate " + (Cam.Gate.Open ? "open" : "HOLD") + ")");
                    }
                    continue;
                }
                _pixels.RemoveAt(i);
                Vector3 sp = c.WorldToScreenPoint(r.Point);
                float h = c.pixelHeight;
                Vector3 cp = c.transform.position;
                Quaternion q = c.transform.rotation;
                DeLog.Info("DE.SnPixel label=" + r.Label + " seTick=" + s.PresentedTick.ToString(ci) + " newestTick=" + s.NewestTick.ToString(ci) +
                           " frame=" + Time.frameCount.ToString(ci) + " px=(" + sp.x.ToString("R", ci) + ", " + (h - sp.y).ToString("R", ci) + ")" +
                           " depth=" + sp.z.ToString("R", ci) + " screen=(" + c.pixelWidth.ToString(ci) + ", " + c.pixelHeight.ToString(ci) + ")" +
                           " fov=" + c.fieldOfView.ToString("R", ci) + " pointSn=(" + r.Point.x.ToString("R", ci) + ", " + r.Point.y.ToString("R", ci) + ", " +
                           r.Point.z.ToString("R", ci) + ") camPos=(" + cp.x.ToString("R", ci) + ", " + cp.y.ToString("R", ci) + ", " + cp.z.ToString("R", ci) +
                           ") camRot=(" + q.x.ToString("R", ci) + ", " + q.y.ToString("R", ci) + ", " + q.z.ToString("R", ci) + ", " + q.w.ToString("R", ci) + ")" +
                           " (Camera.WorldToScreenPoint; y from the top)");
            }
        }

        // ------------------------------------------------------------------------------------------ bridge events

 /// <summary>New SE generation: the C1 history restarts (CONTRACT §6 "New SE generation").</summary>
        public void OnNewSePeer()
        {
            Cam.ResetHistory();
            Cam.Gate.Reset();
            _c5VelMs = 0;
        }

        /// <summary>A fresh C5: forward-box velocity, and the zone for the drive gate.</summary>
        public void OnSeState(ref SeState st)
        {
            _c5Vel = new Vector3(st.camVelSn.x, st.camVelSn.y, st.camVelSn.z);
            _c5VelMs = Win32.TickCount64();
            Cam.Gate.NoteC5(st.zone, (uint)st.viewState, _c5VelMs);   // S7a-1: the view state carries the band
        }

        /// <summary>C7 fields owned by S1.</summary>
        public void FillStatus(ref SnStatus s)
        {
            uint f = 0;
            if (_ready) f |= SnStatusFlags.WorldLoaded;
            if (_ready && Tel.StreamingSettled) f |= SnStatusFlags.StreamingSettled;
            if (_ready && _combatEnabled) f |= SnStatusFlags.CombatEnabled;
            if (_ready && Tel.AreaBuilt) f |= SnStatusFlags.AreaBuilt;
            if (_ready && Ui.UiHidden) f |= SnStatusFlags.UiHidden;
            if (Ui.RunInBackgroundForced) f |= SnStatusFlags.RunInBackgroundForced;
            s.flags |= f;
            s.presentedSeTick = Cam.PresentedSeTick;
            s.areaBuiltSeTick = Tel.AreaBuiltSeTick;
            Vector3 ap = Tel.AreaBuiltPos;
            s.areaBuiltPos = new F32x3(ap.x, ap.y, ap.z);
            s.resamplesLastFrame = Cam.ResamplesLastFrame;
        }

        /// <summary>C7 capability bits of S1: in-game captures (C10).</summary>
        public uint Capabilities() { return DeepEngineers.Wire.Capabilities.Capture; }

        // ------------------------------------------------------------------------------------------ C11

 /// <summary>The S1 commands; false when the command is not one of them.</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            object args = S1Args.Unwrap(req.Args);
            switch (req.Cmd)
            {
                case "de.status":
                    Status(reply.Bool("ok", true).Str("cmd", req.Cmd), now);
                    return true;

                case "de.warpcam":
                    WarpCam(req, args, reply);
                    return true;

                case "de.capture":
                {
                    object pv = S1Args.Get(args, "path", 0);
                    object cv = S1Args.Get(args, "checkId", 1);
                    string full, error;
                    if (Capture.StartManual(now, pv as string, cv as string, _bridge.SnFrame, out full, out error))
                        reply.Bool("ok", true).Str("cmd", req.Cmd).Str("path", full).Num("snFrame", _bridge.SnFrame)
                             .Str("note", "written at the end of the next presented frame; DE.Capture OK|FAILED in the log, sidecar <png>.json");
                    else reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error);
                    return true;
                }

                case "de.testSave":
                {
                    if (!_ready)
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "not in the host world (snPhase " + SnPhase.Name(_bridge.Phase) + ")");
                        return true;
                    }
                    long before = S1Patches.SavesBlocked, allowed = S1Patches.SavesAllowedByMutant;
                    string err = null;
                    Guard("testSave", () => { err = TestSave(); });
                    bool blocked = S1Patches.SavesBlocked > before;
                    reply.Bool("ok", err == null).Str("cmd", req.Cmd).Bool("blocked", blocked)
                         .Bool("mutantAllowed", S1Patches.SavesAllowedByMutant > allowed).Num("savesBlocked", S1Patches.SavesBlocked);
                    if (err != null) reply.Str("error", err);
                    return true;
                }

                case "de.trace":
                {
                    bool on;
                    if (!S1Args.TryBool(S1Args.Get(args, "on", 0), out on))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "args {\"on\": true|false}");
                        return true;
                    }
                    Cam.Trace = on;
                    DeLog.Info("DE.CamTrace " + (on ? "ON" : "OFF") + " (de.trace)");
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("trace", on);
                    return true;
                }

                case "de.pixel":
                {
                    // S1.G1-E5: {pointSn: [x, y, z], minSeTick, label}; answered at once (pending), the pixel is logged as
 // DE.SnPixel at the first presented frame with SE tick >= minSeTick
                    object pv = S1Args.Get(args, "pointSn", 0);
                    var list = pv as System.Collections.IList;
                    double px, py, pz, mt = 0;
                    if (list == null || list.Count != 3 || !S1Args.TryDouble(list[0], out px) || !S1Args.TryDouble(list[1], out py) ||
                        !S1Args.TryDouble(list[2], out pz))
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "args {\"pointSn\": [x, y, z], \"minSeTick\": n, \"label\": \"...\"} (SN-world m)");
                        return true;
                    }
                    S1Args.TryDouble(S1Args.Get(args, "minSeTick", 1), out mt);
                    string label = S1Args.Get(args, "label", 2) as string;
                    label = string.IsNullOrEmpty(label) ? "pixel" : label.Replace(' ', '_');
                    if (!_ready || !Cam.Installed)
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "camera not driven yet (snPhase " + SnPhase.Name(_bridge.Phase) + ")");
                        return true;
                    }
                    if (_pixels.Count >= 16) _pixels.RemoveAt(0);
                    _pixels.Add(new PixelRequest { Label = label, Point = new Vector3((float)px, (float)py, (float)pz), MinSeTick = mt > 0 ? (ulong)mt : 0UL, StartMs = now });
                    reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("pending", true).Str("label", label).Num("minSeTick", mt)
                         .Str("note", "logged as DE.SnPixel at the first presented frame with SE tick >= minSeTick (Camera.WorldToScreenPoint)");
                    return true;
                }

                case "de.underwater":
                {
                    string mode = S1Args.Get(args, "mode", 0) as string;
                    if (mode != "off" && mode != "global")
                    {
                        reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "args {\"mode\": \"off\"|\"global\"}");
                        return true;
                    }
                    bool installed = true;
                    if (mode == "global") Guard("underwater", () => installed = S1Patches.EnsureUnderwaterPatch());
                    S1Patches.UnderwaterMode = mode == "global" && installed ? 1 : 0;
                    DeLog.Info("DE.S1 IsUnderwater variant " + (S1Patches.UnderwaterMode == 1 ? "global (camera below the ocean level)" : "off (vanilla)") + " (S1.N6)");
                    reply.Bool("ok", installed).Str("cmd", req.Cmd).Str("mode", S1Patches.UnderwaterMode == 1 ? "global" : "off");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
 /// S1.N4 trigger: the in-game menu's own SaveGame (the prefix must block it and log DE.SaveBlocked). IngameMenu.main
 /// is set in Awake; when the menu object has never been active, the scene instance is found with
 /// Resources.FindObjectsOfTypeAll (inactive objects included).
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string TestSave()
        {
            IngameMenu m = IngameMenu.main;
            if (m == null)
            {
                foreach (IngameMenu x in Resources.FindObjectsOfTypeAll<IngameMenu>())
                    if (x != null && x.gameObject.scene.IsValid()) { m = x; break; }
            }
            if (m == null) return "no IngameMenu instance in the loaded scenes";
            m.SaveGame();
            return null;
        }

        private void WarpCam(ControlRequest req, object args, JsonObjectWriter reply)
        {
            bool clear;
            if (S1Args.TryBool(S1Args.Get(args, "clear", -1), out clear) && clear)
            {
                Cam.ClearOverride();
                DeLog.Info("DE.Cam warpcam cleared (C1 drives the camera again)");
                reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("override", false);
                return;
            }
            double x, y, z;
            if (!S1Args.TryDouble(S1Args.Get(args, "x", 0), out x) || !S1Args.TryDouble(S1Args.Get(args, "y", 1), out y) ||
                !S1Args.TryDouble(S1Args.Get(args, "z", 2), out z))
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "args {\"x\",\"y\",\"z\"[,\"yaw\",\"pitch\"]} (SN-world m, degrees) or {\"clear\": true}");
                return;
            }
            if (!_ready || !Cam.Installed)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", "camera not driven yet (snPhase " + SnPhase.Name(_bridge.Phase) + ")");
                return;
            }
            double yaw, pitch;
            bool hasYaw = S1Args.TryDouble(S1Args.Get(args, "yaw", 3), out yaw);
            bool hasPitch = S1Args.TryDouble(S1Args.Get(args, "pitch", 4), out pitch);
            Quaternion? rot = null;
            if (hasYaw || hasPitch) rot = Quaternion.Euler(hasPitch ? -(float)pitch : 0f, hasYaw ? (float)yaw : 0f, 0f);
            var p = new Vector3((float)x, (float)y, (float)z);
            Vector3 from = Cam.Camera != null ? Cam.Camera.transform.position : p;
            Cam.SetOverride(p, rot);
            Guard("telemetry", () => Tel.StartProbe((p - from).magnitude, "de.warpcam", p, true));
            DeLog.Info("DE.Cam warpcam to " + CameraDriver.V(p) + (rot.HasValue ? " yaw=" + yaw + " pitch=" + pitch : "") +
                       " from " + CameraDriver.V(from) + " (test override; C1 ignored until de.warpcam {\"clear\": true})");
            reply.Bool("ok", true).Str("cmd", req.Cmd).Bool("override", true).Num("x", x).Num("y", y).Num("z", z)
                 .Num("jumpM", (double)(p - from).magnitude);
        }

        private void Status(JsonObjectWriter w, ulong now)
        {
            CamSample s = Cam.LastPresented;
            w.Str("snPhase", SnPhase.Name(_bridge.Phase)).Num("snFrame", _bridge.SnFrame).Bool("inWorld", _ready)
             .Str("hostSave", Save.Describe()).Str("ghost", Ghost.Describe()).Str("ui", Ui.Describe())
             .Bool("cameraInstalled", Cam.Installed).Bool("cameraFailed", Cam.Failed).Bool("override", Cam.OverrideActive)
             .Num("presentedSeTick", Cam.PresentedSeTick).Num("newestTick", s.NewestTick).Str("outcome", s.Outcome.ToString())
             .Num("alpha", s.Alpha).Num("camX", s.Px).Num("camY", s.Py).Num("camZ", s.Pz)
             .Num("fovApplied", (double)Cam.AppliedFov).Num("nearApplied", (double)Cam.AppliedNear)
             .Num("c1Fresh", Cam.Reader.Fresh).Num("c1Torn", Cam.Reader.Torn).Num("c1Snaps", Cam.Reader.Snaps).Num("c1Resets", Cam.Reader.Resets)
             .Num("c1Invalid", Cam.Reader.Invalid).Num("c1NotInWorld", Cam.Reader.NotInWorld)
             .Num("framesTotal", Cam.Total.Frames).Num("bracketedTotal", Cam.Total.Bracketed).Num("extrapolatedTotal", Cam.Total.Extrapolated)
             .Num("heldTotal", Cam.Total.Held).Num("fovDriftTotal", Cam.Total.FovDrift).Num("gatedTotal", Cam.Total.Gated)
             .Str("gate", Cam.Gate.Open ? "open" : "HOLD " + Cam.Gate.Reason).Str("latencyMs", Cam.TotalLatency.Describe())
             .Num("steps", Cam.Steps).Bool("trace", Cam.Trace)
             .Bool("streamingSettled", Tel.StreamingSettled).Bool("areaBuilt", Tel.AreaBuilt).Bool("areaBuiltFwd", Tel.AreaBuiltForward).Num("probes", Tel.Probes)
             .Bool("combatEnabled", _combatEnabled).Num("savesBlocked", S1Patches.SavesBlocked)
             .Bool("saveGuardMenu", S1Patches.SaveGuardMenu).Bool("saveGuardDeepStorage", S1Patches.SaveGuardDeepStorage)
             .Num("uiSinks", (long)S1Patches.UiSinksInstalled).Str("capture", Capture.DescribeState())
             .Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()))
             .Str("mutants", DevMutants.Describe()).Num("readySinceMs", _ready ? (long)(now - _readyAtMs) : 0L).Num("tickMs", now);
        }

        // ------------------------------------------------------------------------------------------ feature guard

        private bool Disabled(string feature) { return _disabled.Contains(feature); }

        /// <summary>Runs one feature step; its first exception disables that feature only (logged once).</summary>
        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try
            {
                a();
            }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S1 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message +
                            " (the rest of the bridge keeps running)\n" + e);
            }
        }
    }
}
