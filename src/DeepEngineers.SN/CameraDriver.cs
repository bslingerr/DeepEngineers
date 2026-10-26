using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The SE-driven camera (ROADMAP S1 SN plugin; CONTRACT §2.8, §5.4 C1/C7; ARCHITECTURE §5.8, §5.11):
 /// <list type="bullet">
 /// <item>Install: <c>MainCameraControl.main.ResetCamera</c>, the rig (MainCameraControl's transform, the object
 /// <c>FreecamController</c> unparents) unparented with its world pose kept, <c>MainCameraControl</c> disabled
 /// Then the
 /// callbacks: <c>ManagedUpdate.Subscribe(Queue.LateUpdateLast)</c> (after the game's camera queues),
 /// <c>Camera.onPreCull</c> (re-sample once per frame, main camera only) and <c>ManagedUpdate.Queue.EndOfFrameLast</c>
 /// (presentedSeTick stamped after the frame was rendered; ManagedUpdate runs that queue after WaitForEndOfFrame).</item>
 /// <item>Pose: position interpolated on QPC stamps with D = 1.25 ticks, orientation = newest snapshot
 /// (<see cref="FrameSampler"/>). The rig is placed so that <c>MainCamera.camera</c> lands exactly on the pose,
 /// whatever fixed offsets the rig's children carry (the offset is measured each time). The camera is never scaled.</item>
 /// <item>FOV: <c>SNCameraRoot.main.SetFov(vFovDeg)</c> on change, and <c>fieldOfView</c> checked in every main-camera
 /// onPreCull before the render: a drift beyond 0.01° is counted (S1.N7) and corrected. The value found before the
 /// correction is what the trace and S1.N7 report. Near:
 /// <c>minNearClip = near</c>, then <c>SetNearClip(near)</c> on change (SNCameraRoot clamps to minNearClip).</item>
 /// <item>Drive gate: outside the dive zone (ρ∞ &gt; 7,900 m, y &gt; +350 m,
 /// y &lt; −7,900 m, or C5 zone FAR / off-site) the camera and the Player are held where they are.</item>
 /// <item>Trace (<c>de.trace on</c>): one <see cref="CamTraceEntry"/> per presented frame with the Unity camera transform
 /// after placement and the commanded pose, written in batches by <see cref="CamTraceWriter"/>
 /// on a background thread once per second.</item>
 /// <item>A test override (<c>de.warpcam</c>) holds a fixed pose instead of C1 until cleared.</item>
 /// </list>
 /// Every callback catches everything; the first exception of a callback disables the driver (the game keeps running).
 /// </summary>
    internal sealed unsafe class CameraDriver
    {
        public const double FovToleranceDeg = 0.01;
        public const double StepDetectDeg = 15.0;

        public readonly FrameReader Reader;
        private readonly long _qpcFreq;

        private Transform _rig;
        private Camera _cam;
        private bool _driveCameraDirectly;
        private ManagedUpdate.OnUpdate _onLate, _onEnd;
        private Camera.CameraCallback _onPreCull;

        public bool Installed { get; private set; }
        public bool Failed { get; private set; }
        public string FailReason { get; private set; }

        /// <summary>
 /// Called after each LateUpdateLast application with the sample, the camera position and whether the camera was
 /// driven this frame (false: no C1, or held by the drive gate) (ghost Player).
 /// </summary>
        public Action<CamSample, Vector3, bool> AfterLateUpdate;
        /// <summary>Called after the main camera's re-sample in onPreCull (capture triggers).</summary>
        public Action<CamSample> AfterPreCull;
        /// <summary>Called at EndOfFrameLast after presentedSeTick was stamped (the bridge publishes C7).</summary>
        public Action EndOfFrame;

        // de.warpcam override
        public bool OverrideActive { get; private set; }
        private Vector3 _ovPos;
        private Quaternion _ovRot;
        private bool _ovHasRot;

        // per-frame state
        public CamSample LastLate;
        public CamSample LastPresented;
        public ulong PresentedSeTick { get; private set; }
        public uint ResamplesLastFrame { get; private set; }
        private int _resampleFrame = -1;
        private uint _resamplesThisFrame;
        private ulong _framePresentedTick;
        private bool _framePresented;
        public long FramesPresented;

        private float _appliedFov = -1f, _appliedNear = -1f;
        public float AppliedFov { get { return _appliedFov; } }
        public float AppliedNear { get { return _appliedNear; } }

        public readonly CamCounters Window = new CamCounters();
        public readonly CamCounters Total = new CamCounters();
        public readonly LatencyHistogram WindowLatency = new LatencyHistogram();
        public readonly LatencyHistogram TotalLatency = new LatencyHistogram();
        public long Snaps, OverrideFrames, PlacementChecks;
        public double WorstPlacementM, WorstPlacementDeg;

        public bool Trace;
        private readonly CamTraceWriter _traceWriter;
        public CamTraceWriter TraceWriter { get { return _traceWriter; } }
        private bool _hasPrevYaw;
        private double _prevYaw;
        public long Steps;

        // one-time logs
        private bool _fovLogged, _nearLogged;
        private int _fovDriftLogs;

        /// <summary>Where C1 may drive Subnautica.</summary>
        public readonly DriveGate Gate = new DriveGate();
        /// <summary>S2 sets it: (c5Valid, view, approach, perfMode, the S7a.M2 switch) of this frame for <see cref="CameraParkRules"/>; null = never park.</summary>
        public static Func<Wire.PerfInputsForPark> ParkSource;
        public long ParkedSamples;
        private bool _parked;
        /// <summary>The camera was driven by C1 (or the override) in the last LateUpdateLast.</summary>
        public bool LastLateDriven { get; private set; }

        // onPreCull FOV observations of this frame (S1.N7): fieldOfView found before the assertion, and the FOV the plugin
 // had applied before it (C1 vFovDeg of an earlier application; -1 before the first)
        private float _fovSeen = float.NaN, _fovApplied = -1f;

        public CameraDriver(byte* c1, long qpcFrequency)
        {
            _qpcFreq = qpcFrequency;
            Reader = new FrameReader(c1, qpcFrequency);
            _traceWriter = new CamTraceWriter(qpcFrequency, DeLog.AppendJsonlBatch);
        }

        public Camera Camera { get { return _cam; } }
        public Transform Rig { get { return _rig; } }
        /// <summary>S5a: the main camera is a child of the rig, so the rig's world pose is the vanilla MainCameraControl transform's (the view model's reference).</summary>
        public bool CameraUnderRig { get { return Installed && !_driveCameraDirectly; } }

        // ------------------------------------------------------------------------------------------ install

        public bool Install(out string error)
        {
            error = null;
            if (Installed) return true;
            try
            {
                InstallCore();
                Installed = true;
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                Fail("install", e);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void InstallCore()
        {
            MainCameraControl mcc = MainCameraControl.main;
            _cam = MainCamera.camera;
            if (mcc == null || _cam == null) throw new InvalidOperationException("MainCameraControl.main or MainCamera.camera is null");
            mcc.ResetCamera();
            _rig = mcc.transform;
            Transform oldParent = _rig.parent;
            _rig.SetParent(null, true);
            mcc.SetEnabled(false);
            mcc.enabled = false;
            Transform camT = _cam.transform;
            _driveCameraDirectly = !(camT == _rig || camT.IsChildOf(_rig));
            Vector3 relPos = Quaternion.Inverse(_rig.rotation) * (camT.position - _rig.position);
            Quaternion relRot = Quaternion.Inverse(_rig.rotation) * camT.rotation;
            DeLog.Info("DE.Cam rig '" + _rig.name + "' unparented from '" + (oldParent != null ? oldParent.name : "(none)") +
                       "', MainCameraControl disabled; main camera '" + _cam.name + "' " +
                       (_driveCameraDirectly ? "is NOT under the rig: its own transform is driven" : "under the rig, offset pos=" + V(relPos) +
                       " rot=" + Q(relRot)) + " rigScale=" + V(_rig.lossyScale) + " fov=" + F(_cam.fieldOfView) + " optionsFov=" + F(MiscSettings.fieldOfView) +
                       " near=" + F(_cam.nearClipPlane) + " far=" + F(_cam.farClipPlane) + " minNearClip=" + F(SNCameraRoot.main.minNearClip) +
                       " (S1.N7 and S1.M6 discriminate only when C1 vFovDeg differs from optionsFov)");

            _onLate = OnLateUpdateLast;
            _onEnd = OnEndOfFrame;
            _onPreCull = OnPreCull;
            ManagedUpdate.Subscribe(ManagedUpdate.Queue.LateUpdateLast, _onLate);
            ManagedUpdate.Subscribe(ManagedUpdate.Queue.EndOfFrameLast, _onEnd);
            Camera.onPreCull += _onPreCull;
        }

        public void Uninstall()
        {
            if (!Installed) return;
            Installed = false;
            try { UninstallCore(); } catch (Exception) { }
            try { _traceWriter.Stop(); } catch (Exception) { } // the last trace batch, before the JSONL closes
        }

        /// <summary>Per second (main thread): the trace buffer goes to the writer thread.</summary>
        public void FlushTrace()
        {
            try { _traceWriter.Flush(); } catch (Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UninstallCore()
        {
            if (_onPreCull != null) Camera.onPreCull -= _onPreCull;
            if (_onLate != null) ManagedUpdate.Unsubscribe(ManagedUpdate.Queue.LateUpdateLast, _onLate);
            if (_onEnd != null) ManagedUpdate.Unsubscribe(ManagedUpdate.Queue.EndOfFrameLast, _onEnd);
        }

        private void Fail(string where, Exception e)
        {
            if (Failed) return;
            Failed = true;
            FailReason = where + ": " + e.GetType().Name + ": " + e.Message;
            DeLog.Error("DE.Cam DISABLED after an exception in " + where + " (camera no longer driven; Subnautica keeps running): " + e);
            Uninstall();
        }

        // ------------------------------------------------------------------------------------------ override (de.warpcam)

        public void SetOverride(Vector3 pos, Quaternion? rot)
        {
            OverrideActive = true;
            _ovPos = pos;
            _ovHasRot = rot.HasValue;
            _ovRot = rot.HasValue ? rot.Value : Quaternion.identity;
            Snaps++;
        }

        public void ClearOverride()
        {
            OverrideActive = false;
        }

        /// <summary>A new SE generation (or world epoch handled by the history): restart the history.</summary>
        public void ResetHistory()
        {
            Reader.Reset();
            _hasPrevYaw = false;
        }

        // ------------------------------------------------------------------------------------------ callbacks

        private void OnLateUpdateLast()
        {
            if (!Installed || Failed) return;
            try
            {
                Reader.Poll();
                CamSample s = Reader.Sample(Win32.QpcNow());
                LastLate = s;
                Vector3 camPos = _cam.transform.position;
                bool driven = !Gated(s) && Apply(s, false, out camPos);
                LastLateDriven = driven;
                Action<CamSample, Vector3, bool> after = AfterLateUpdate;
                if (after != null) after(s, camPos, driven);
            }
            catch (Exception e)
            {
                Fail("LateUpdateLast", e);
            }
        }

        private void OnPreCull(Camera c)
        {
            if (!Installed || Failed) return;
            try
            {
                if (c != _cam || c == null) return;
                int frame = Time.frameCount;
                if (frame == _resampleFrame)
                {
                    // the main camera renders more than once in this frame: keep the first re-sample (counted)
                    Window.ExtraPreCull++;
                    Total.ExtraPreCull++;
                    return;
                }
                _resampleFrame = frame;
                _resamplesThisFrame++;
                long qpcUsed = Win32.QpcNow();
                Reader.Poll();
                CamSample s = Reader.Sample(qpcUsed);
                Vector3 camPos = _cam.transform.position;
                _fovSeen = _cam.fieldOfView;   // before this re-sample's lens application (S1.N7)
                _fovApplied = _appliedFov;
                bool gated = Gated(s);
                bool driven = !gated && Apply(s, true, out camPos);
                LastPresented = s;
                if (driven && !OverrideActive)
                {
                    // S7p-gameplay (P13): the presented camera's motion per axis (s7p.cam row: a stuck axis shows here)
                    Vector3 af = _placedRot * Vector3.forward, au = _placedRot * Vector3.up;
                    double ay, ap, ar;
                    S7pAxisFreedom.Angles(af.x, af.y, af.z, au.x, au.y, au.z, out ay, out ap, out ar);
                    Axis.Add(ay, ap, ar);
                    _framePresentedTick = s.PresentedTick;
                    _framePresented = true;
                    double latMs = (qpcUsed - s.NewestQpc) * 1000.0 / _qpcFreq;
                    WindowLatency.Add(latMs);
                    TotalLatency.Add(latMs);
                    Window.Count(s.Outcome, s.Stale);
                    Total.Count(s.Outcome, s.Stale);
                    DetectStep(s, qpcUsed);
                    if (Trace) TraceFrame(s, qpcUsed);
                }
                else if (OverrideActive)
                {
                    OverrideFrames++;
                }
                else if (gated)
                {
                    Window.Gated++;
                    Total.Gated++;
                }
                else
                {
                    Window.Count(SampleOutcome.None, false);
                    Total.Count(SampleOutcome.None, false);
                }
                Action<CamSample> after = AfterPreCull;
                if (after != null) after(s);
            }
            catch (Exception e)
            {
                Fail("Camera.onPreCull", e);
            }
        }

        private void OnEndOfFrame()
        {
            if (!Installed || Failed) return;
            try
            {
                ResamplesLastFrame = _resamplesThisFrame;
                _resamplesThisFrame = 0;
                if (_framePresented)
                {
                    PresentedSeTick = _framePresentedTick; // CONTRACT §5.4 C7: stamped at the end of the presented frame
                    FramesPresented++;
                }
                _framePresented = false;
                Action end = EndOfFrame;
                if (end != null) end();
            }
            catch (Exception e)
            {
                Fail("EndOfFrame", e);
            }
        }

        // ------------------------------------------------------------------------------------------ application

 /// <summary>
 /// The drive gate for a C1 sample (never for the de.warpcam override or an empty history): true when the sample
 /// must not drive Subnautica. Transitions are logged.
 /// </summary>
        private bool Gated(CamSample s)
        {
            if (OverrideActive || s.Outcome == SampleOutcome.None) return false;
            bool changed;
            bool open = Gate.Decide(s.Px, s.Py, s.Pz, Win32.TickCount64(), out changed);
            if (changed)
            {
                if (open)
                    DeLog.Info("DE.Cam gate OPEN at SN (" + F(s.Px) + ", " + F(s.Py) + ", " + F(s.Pz) + "): C1 drives the camera and the Player again (#" + Gate.Openings + ")");
                else
                    DeLog.Warn("DE.Cam gate HOLD: " + Gate.Reason + " at SN (" + F(s.Px) + ", " + F(s.Py) + ", " + F(s.Pz) +
                               "); the camera and the Player stay at the last driven pose (CONTRACT §7 leaving the site; OutOfBoundsWarp ±8,192 m) (#" + Gate.Closings + ")");
            }
            if (!open && SpawnParkOn && Gate.Reason != null && Gate.Reason.StartsWith("C5 zone", StringComparison.Ordinal)) ParkAtSpawn("far from 4546B");
            return !open;
        }

        /// <summary>

 /// never see the loading screen when spawning or respawning"): while SE is far from 4546B (C5 zone FAR or off-site) or the
 /// character is dead (the respawn screen), the camera and the Player wait at the spawn, the lifepod point, so the area
 /// a capsule spawn lands in is already built. DEEPENGINEERS_SN_SPAWNPARK=off: the camera stays at the last driven pose.
 /// </summary>
        public static bool SpawnParkOn = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_SN_SPAWNPARK"), "off", StringComparison.OrdinalIgnoreCase);
        public static readonly Vector3 SpawnParkPos = new Vector3(0f, 1.6f, 0f);   // the lifepod point (0, 0.6, 0), at eye height
        public long SpawnParkedFrames;
        private bool _spawnParked;
        private string _spawnParkWhy;

        private void ParkAtSpawn(string why)
        {
            Place(SpawnParkPos, _placed ? _placedRot : Quaternion.identity);
            SpawnParkedFrames++;
            if (!_spawnParked || _spawnParkWhy != why)
            {
                _spawnParked = true;
                _spawnParkWhy = why;
                DeLog.Info("DE.Cam spawn park (" + why + "): the camera and the Player wait at the lifepod point " + SpawnParkPos + " so a spawn there finds its area built");
            }
        }

        private void SpawnParkRelease()
        {
            if (!_spawnParked) return;
            _spawnParked = false;
            _spawnParkWhy = null;
            DeLog.Info("DE.Cam spawn park released: C1 drives the camera again");
        }

        /// <summary>Places the camera for the sample (or the override). False when nothing drives the camera.</summary>
        private bool Apply(CamSample s, bool preCull, out Vector3 camPos)
        {
            camPos = _cam != null ? _cam.transform.position : Vector3.zero;
            if (OverrideActive)
            {
                Quaternion r = _ovHasRot ? _ovRot : (s.Outcome != SampleOutcome.None ? new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw) : _cam.transform.rotation);
                Place(_ovPos, r);
                camPos = _ovPos;
                if (s.Outcome != SampleOutcome.None) ApplyLens(s, preCull);
                return true;
            }
            if (s.Outcome == SampleOutcome.None)
            {

                // its menu or loading its world; Subnautica is ready about 30 s before SE's world) or none for 2 s: the camera and
 // the Player wait at the spawn, so its area is built before the first capsule lands there
                bool noSe = s.NewestQpc == 0 || (Win32.QpcNow() - s.NewestQpc) > 2 * _qpcFreq;
                if (SpawnParkOn && noSe && Player.main != null) { ParkAtSpawn("no SE camera yet"); camPos = SpawnParkPos; return false; }
                return false;
            }
            var p = new Vector3((float)s.Px, (float)s.Py, (float)s.Pz);
            Quaternion q;
            // S7p-gameplay (P13): a non-finite C1 pose never reaches the transform (a NaN rotation freezes or tears the
 // camera): the last good pose is held and the sample counted (s7p.cam nonFinite)
            if (!S7pAxisFreedom.FiniteQuat(s.Qx, s.Qy, s.Qz, s.Qw) || double.IsNaN(s.Px + s.Py + s.Pz) || double.IsInfinity(s.Px + s.Py + s.Pz))
            {
                NonFiniteSamples++;
                if (!_hasGood) return false;
                p = _goodPos;
                q = _goodRot;
            }
            else
            {
                q = Normalize(new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw));
                _goodPos = p;
                _goodRot = q;
                _hasGood = true;
            }

            // S7a: C5 perfMode != Full, i.e. Space under the Site policy, Space/Track under Waterline; S7a.M2: S2's view rule)
            bool park = false;
            Func<Wire.PerfInputsForPark> src = ParkSource;
            if (src != null)
            {
                Wire.PerfInputsForPark pi = src();
                park = CameraParkRules.Decide(pi);
            }
            if (park != _parked)
            {
                _parked = park;
                DeLog.Info(park ? "DE.Cam parked: altitude clamped to +" + CameraParkRules.ParkY + " m under the SE camera while C5 asks for less than Full (streams the arrival column)"
                                : "DE.Cam park released: the true pose drives the camera again");
            }
            if (park) { p.y = CameraParkRules.DriveY(true, p.y); ParkedSamples++; }
            bool dead = (s.Flags & Wire.SeFrameFlags.CharacterDead) != 0;
            bool noCharacter = (s.Flags & Wire.SeFrameFlags.CharacterPresent) == 0;   // SE's respawn screen, before the first spawn
            if (SpawnParkOn && (dead || noCharacter))
            {
                ParkAtSpawn(dead ? "the character is dead" : "no character yet (SE's respawn screen)");
                camPos = SpawnParkPos;
                ApplyLens(s, preCull);
                return true;
            }
            SpawnParkRelease();
            Place(p, q);
            camPos = p;
            ApplyLens(s, preCull);
            return true;
        }

        private Vector3 _placedPos;
        private Quaternion _placedRot;
        private bool _placed;

        // S7p-gameplay (P13): the last finite C1 pose, the non-finite count and the per-axis motion of the presented camera
        private Vector3 _goodPos;
        private Quaternion _goodRot = Quaternion.identity;
        private bool _hasGood;
        public long NonFiniteSamples;
        public readonly S7pAxisFreedom Axis = new S7pAxisFreedom();

        private void Place(Vector3 p, Quaternion r)
        {
            _placedPos = p;
            _placedRot = r;
            _placed = true;
            Transform camT = _cam.transform;
            if (_driveCameraDirectly)
            {
                camT.SetPositionAndRotation(p, r);
                return;
            }
            Quaternion rigRot = _rig.rotation;
            Quaternion inv = Quaternion.Inverse(rigRot);
            Vector3 relPos = inv * (camT.position - _rig.position);
            Quaternion relRot = inv * camT.rotation;
            Quaternion newRigRot = r * Quaternion.Inverse(relRot);
            _rig.SetPositionAndRotation(p - newRigRot * relPos, newRigRot);
        }

        public long ControlReDisabled, RigReUnparented;

        /// <summary>
 /// Per second: MainCameraControl stays disabled and the rig stays unparented (a cinematic or the console's
 /// freecam toggle may undo either); each undo is reverted, counted and logged.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PerSecond()
        {
            if (!Installed || Failed) return;
            try
            {
                MainCameraControl mcc = MainCameraControl.main;
                if (mcc != null && mcc.enabled)
                {
                    mcc.enabled = false;
                    ControlReDisabled++;
                    DeLog.Warn("DE.Cam MainCameraControl was enabled again: disabled (#" + ControlReDisabled + ")");
                }
                if (_rig != null && _rig.parent != null)
                {
                    string parent = _rig.parent.name;
                    _rig.SetParent(null, true);
                    RigReUnparented++;
                    DeLog.Warn("DE.Cam rig was re-parented under '" + parent + "': unparented again (#" + RigReUnparented + ")");
                }
                CheckPlacement();
            }
            catch (Exception e)
            {
                Fail("PerSecond", e);
            }
        }

        /// <summary>Checks that the main camera sits on the target pose (per second; worst values kept for the log).</summary>
        public void CheckPlacement()
        {
            if (!Installed || Failed || _cam == null || !_placed) return;
            // the pose the last placement (LateUpdateLast or onPreCull) asked for
            Transform camT = _cam.transform;
            double dm = (camT.position - _placedPos).magnitude;
            double dd = Quaternion.Angle(camT.rotation, _placedRot);
            PlacementChecks++;
            if (dm > WorstPlacementM) WorstPlacementM = dm;
            if (dd > WorstPlacementDeg) WorstPlacementDeg = dd;
        }

        private void ApplyLens(CamSample s, bool preCull)
        {
            SNCameraRoot root = SNCameraRoot.main;
            if (root == null) return;
            float fov = s.VFovDeg;
            if (Math.Abs(fov - _appliedFov) > 1e-4f)
            {
                root.SetFov(fov);
                if (!_fovLogged || Math.Abs(fov - _appliedFov) > 0.5f)
                    DeLog.Info("DE.Cam FOV " + F(_appliedFov) + " -> " + F(fov) + " deg (SNCameraRoot.SetFov; MiscSettings.fieldOfView untouched)");
                _fovLogged = true;
                _appliedFov = fov;
            }
            else if (preCull)
            {
                float actual = _cam.fieldOfView;
                if (Math.Abs(actual - fov) > FovToleranceDeg)
                {
                    Window.FovDrift++;
                    Total.FovDrift++;
                    if (_fovDriftLogs < 5)
                    {
                        _fovDriftLogs++;
                        DeLog.Warn("DE.Cam FOV drift: fieldOfView " + F(actual) + " != C1 vFovDeg " + F(fov) + " (corrected; counted" +
                                   (_fovDriftLogs == 5 ? ", further drifts only counted" : "") + ")");
                    }
                    root.SetFov(fov);
                }
            }
            float near = s.Near;
            if (near > 0f && Math.Abs(near - _appliedNear) > 1e-5f)
            {
                root.minNearClip = near;
                root.SetNearClip(near);
                if (!_nearLogged) DeLog.Info("DE.Cam near " + F(_appliedNear) + " -> " + F(near) + " m (minNearClip = near, then SetNearClip); camera near=" + F(_cam.nearClipPlane));
                _nearLogged = true;
                _appliedNear = near;
            }
        }

        // ------------------------------------------------------------------------------------------ S1.N2 and traces

        private void DetectStep(CamSample s, long qpcUsed)
        {
            double yaw = CamMath.YawDeg(s.Qx, s.Qy, s.Qz, s.Qw);
            if (_hasPrevYaw)
            {
                double d = CamMath.YawDelta(_prevYaw, yaw);
                if (d > StepDetectDeg)
                {
                    Steps++;
                    // S1.N2: latency = this qpcUsed − qpc(T) of SE's scripted step
                    DeLog.Info("DE.CamStep qpcUsed=" + qpcUsed.ToString(CultureInfo.InvariantCulture) + " deltaYawDeg=" + F(d) +
                               " yawDeg=" + F(yaw) + " newestTick=" + s.NewestTick + " newestQpc=" + s.NewestQpc.ToString(CultureInfo.InvariantCulture) +
                               " frame=" + Time.frameCount);
                }
            }
            _prevYaw = yaw;
            _hasPrevYaw = true;
        }

        /// <summary>
 /// One trace entry per presented frame when tracing (S1.N1/N2/N7, WM-2/3/4 tools): the Unity camera transform after
 /// placement, the commanded pose, and the FOV found before the per-frame assertion together with
 /// the FOV applied before it, the rendered one and C1's. Buffered, no allocation (finding 6).
 /// </summary>
        private void TraceFrame(CamSample s, long qpcUsed)
        {
            Transform t = _cam.transform;
            Vector3 p = t.position;
            Quaternion r = t.rotation;
            var e = new CamTraceEntry
            {
                Frame = Time.frameCount,
                TickMs = Win32.TickCount64(),
                SnFrame = DeLog.Frame,
                QpcUsed = qpcUsed,
                NewestQpc = s.NewestQpc,
                NewestTick = s.NewestTick,
                PresentedTick = s.PresentedTick,
                OlderTick = s.OlderTick,
                Outcome = s.Outcome,
                Alpha = s.Alpha,
                X = p.x, Y = p.y, Z = p.z, Qx = r.x, Qy = r.y, Qz = r.z, Qw = r.w,
                Tx = _placedPos.x, Ty = _placedPos.y, Tz = _placedPos.z,
                Tqx = _placedRot.x, Tqy = _placedRot.y, Tqz = _placedRot.z, Tqw = _placedRot.w,
                FovSeen = _fovSeen,
                FovApplied = _fovApplied,
                Fov = _cam.fieldOfView,
                C1Fov = s.VFovDeg,
            };
            _traceWriter.Add(ref e);
        }

        /// <summary>The 300-frame outcome line; the window counters restart.</summary>
        public string TakeWindowStats()
        {
            CamCounters w = Window;
            string line = "DE.Cam stats frames=" + w.Frames + " bracketed=" + w.Bracketed + " extrapolated=" + w.Extrapolated + " held=" + w.Held +
                          " none=" + w.None + " stale=" + w.Stale + " tornTotal=" + Reader.Torn + " snapsTotal=" + Reader.Snaps +
                          " resetsTotal=" + Reader.Resets + " invalidTotal=" + Reader.Invalid + " notInWorldTotal=" + Reader.NotInWorld +
                          " c1FreshTotal=" + Reader.Fresh +
                          " fovDrift=" + w.FovDrift + " extraPreCull=" + w.ExtraPreCull + " gated=" + w.Gated + " resamplesLastFrame=" + ResamplesLastFrame +
                          " presentedSeTick=" + PresentedSeTick + " newestTick=" + LastPresented.NewestTick +
                          " latencyMs " + WindowLatency.Describe() + " total{" + Total.Frames + " frames, ext " + Total.Extrapolated +
                          ", held " + Total.Held + ", fovDrift " + Total.FovDrift + ", gated " + Total.Gated + ", latencyMs " + TotalLatency.Describe() + "}" +
                          " gate=" + (Gate.Open ? "open" : "HOLD(" + Gate.Reason + ")") +
                          (OverrideActive ? " OVERRIDE(de.warpcam)" : "") +
                          " placementWorst=" + DeLog.F(WorstPlacementM) + "m," + DeLog.F(WorstPlacementDeg) + "deg" +
                          (Trace || _traceWriter.Written > 0 ? " trace{written " + _traceWriter.Written + ", buffered " + _traceWriter.Buffered + ", dropped " + _traceWriter.Dropped + "}" : "");
            Window.Reset();
            WindowLatency.Clear();
            return line;
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (n < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        }

        internal static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        internal static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        internal static string V(Vector3 v)
        {
            return "(" + v.x.ToString("0.###", CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", CultureInfo.InvariantCulture) + "," +
                   v.z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }
        internal static string Q(Quaternion q)
        {
            return "(" + q.x.ToString("0.####", CultureInfo.InvariantCulture) + "," + q.y.ToString("0.####", CultureInfo.InvariantCulture) + "," +
                   q.z.ToString("0.####", CultureInfo.InvariantCulture) + "," + q.w.ToString("0.####", CultureInfo.InvariantCulture) + ")";
        }
    }
}
