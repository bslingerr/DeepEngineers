using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UWE;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S1 telemetry of the Subnautica side (ROADMAP S1 SN plugin, oracles S1.N3, S1.N6, S1.N8, S1.N10, S1.N11; C7 flags):
 /// <list type="bullet">
 /// <item>Once per second (unscaled): `DE.S1 sec` with <c>Player.main.IsUnderwater</c>, the fog's camera-inside flag
 /// (the expression of <c>WaterscapeVolumeOnCamera.OnRenderImage</c>: <c>Player.IsInsideWalkable</c> and no active
 /// FreecamController), the biome at the camera and at the Player (<c>LargeWorld.main.GetBiome(Vector3)</c>), depth
 /// (<c>Ocean.GetDepthOf</c> = −y) and the camera y, the Player-to-camera distance, the ghost rule, whether the Player
 /// carries <c>OutOfBoundsWarp</c> (G7), escapePod/currentSub, frozen stats, FOV and near, run in background and the
 /// FreezeTime state, streaming flags, the water-surface sample.</item>
 /// <item>Area flags for C7 (every 100 ms; every frame while a jump probe runs): <c>LargeWorldStreamer.main.IsWorldSettled</c>
 /// (C7 streamingSettled) and <c>IsRangeActiveAndBuilt(Bounds)</c> for the ±56 m camera box and the forward box (C7
 /// areaBuilt, ARCHITECTURE §4.1). Forward box (our reading of "a forward box along the C5 camera velocity, length
 /// max(20 m, |v|·1 s)"): the same ±56 m box centred max(20 m, |v|·1 s) ahead of the camera along the velocity (C5
 /// `camVelSn` when fresh, else the C1 snapshots), along the camera's forward when |v| &lt; 0.1 m/s.</item>
 /// <item>Streaming probe after every camera jump (> 20 m in one frame, a C1 snap, or de.warpcam; S1.N3): time to
 /// IsWorldSettled and to both boxes built, logged as `DE.Stream`.</item>
 /// <item>Water surface at the camera (S1.N10, CONTRACT O15): once per second an <c>AsyncGPUReadback</c> of the one
 /// texel of <c>WaterSurface.Get.GetDisplacementTexture</c>
 /// under the camera's (x, z); the G channel is taken as the vertical
 /// displacement (HYPOTHESIS: the shader is compiled, the texel mapping uv = xz / patchLength is inferred from
 /// <c>_WaterPatchLength</c>). Max, min and the last value are logged per second and per 60 s window.</item>
 /// </list>
 /// </summary>
    internal sealed class S1Telemetry
    {
        /// <summary>
 /// Half-size of the camera box and the forward box asked of <c>IsRangeActiveAndBuilt</c>. ARCHITECTURE §4.1 said
 /// ±56 m; the real runs showed that the collision clipmap level answers true for ±16 m
 /// always and ±32 m sometimes, never ±56 m (its loaded window is a few cells around the viewer's cell, with
 /// margins): with ±56 m C7 areaBuilt was never set in any session. ±16 m is the near field that is always
 /// answerable; <c>IsWorldSettled</c> (C7 streamingSettled) covers the rest of the view. The ladder
 /// <c>camBuiltHalf</c> keeps measuring what is built (D-item for S2's entry criterion).
 /// </summary>
        public const float BoxHalf = 16f;
        /// <summary>S7a-1b: areaBuilt follows IsWorldSettled while the camera is above the near field under Site.</summary>
        public bool AirRule { get; private set; }
        public const ulong AreaEveryMs = 100;
        public const ulong ProbeTimeoutMs = 120000;
        public const double JumpMetres = 20.0;
        /// <summary>A probe whose streamer never went busy within this window reports settledMs=0 busy=no (nothing to stream).</summary>
        public const double SettleIdleWindowMs = 1000.0;
        private static readonly int[] BuiltLadder = { 56, 32, 16, 8, 4 };

        // C7 inputs
        public bool StreamingSettled { get; private set; }
        public bool AreaBuilt { get; private set; }
        /// <summary>S8a (B4 hook): set by the bridge while the S8a host runs: (Subnautica's own areaBuilt, camera) → the gated value (CONTRACT "window.ready").</summary>
        public static Func<bool, Vector3, bool> AreaBuiltGate;
        /// <summary>The forward box.</summary>
        public bool AreaBuiltForward { get; private set; }
        /// <summary>
 /// S7a: C5 flag bit 9 pictureSite (set each frame by S2Host). Under the Site picture policy the ±16 m camera box
 /// is centred at <c>min(y, +140 m)</c> under the camera (<see cref="S7aConventions.AreaBoxCentreY"/>: a camera at
 /// +300 m asks for the world's top layer under it, not for the empty air above the octree, whose answer is
 /// HYPOTHESIS); <see cref="AreaBuiltRaw"/> keeps the box at the camera itself (logged on the DE.S1 sec line).
 /// </summary>
        public bool AreaBoxSite;
        /// <summary>S7a: the ±16 m box at the camera's own position (equal to <see cref="AreaBuilt"/> unless the Site box was lowered).</summary>
        public bool AreaBuiltRaw { get; private set; }
        public ulong AreaBuiltSeTick { get; private set; }
        public Vector3 AreaBuiltPos { get; private set; }
        private ulong _nextAreaMs;

        // jump probe
        private bool _probe;
        private long _probeQpc;
        private double _probeDist;
        private string _probeWhy;
        private Vector3 _probeTo;
        private double _settledMs = -1, _camBoxMs = -1, _fwdBoxMs = -1;
        private bool _probeSawBusy;
        public long Probes, ProbeTimeouts;
        private Vector3 _lastCamPos;
        private bool _hasLastCam;
        /// <summary>Frames left in which the move of a commanded probe (de.warpcam) is expected and not taken for a new jump.</summary>
        private int _skipJumpFrames;

        // water surface
        private bool _waterRequestInFlight, _waterUnsupportedLogged;
        private float _waterDy = float.NaN, _waterSecMax = float.NegativeInfinity, _waterSecMin = float.PositiveInfinity;
        private float _waterWinMax = float.NegativeInfinity, _waterWinMin = float.PositiveInfinity;
        private float _waterDxzMax;
        private long _waterSamples;
        private int _waterSecond;

        // velocity for the forward box
        public Vector3 VelocitySn;

        /// <summary>Per frame: jump detection and the area flags (in world).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void PerFrame(ulong nowMs, Vector3 cam, Vector3 camForward, ulong presentedSeTick, bool snapped, string snapWhy)
        {
            if (_hasLastCam)
            {
                double d = (cam - _lastCamPos).magnitude;
                // Only a real move is a jump. A snap without a move (the teleport latch's second frame, a C1 history
 // reset, a teleport to the same point) measures nothing: in the session B real run such
 // 0 m "jumps" superseded every real probe after 0 ms.
                bool jump = d > JumpMetres;
                if (_skipJumpFrames > 0) _skipJumpFrames = jump ? 0 : _skipJumpFrames - 1; // the commanded move itself
                else if (jump)
                    StartProbe(d, snapped ? snapWhy : "camera moved " + d.ToString("0.0", CultureInfo.InvariantCulture) + " m in one frame", cam, false);
            }
            _lastCamPos = cam;
            _hasLastCam = true;

            if (!_probe && nowMs < _nextAreaMs) return;
            _nextAreaMs = nowMs + AreaEveryMs;
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            if (lws == null) { StreamingSettled = false; AreaBuilt = false; return; }
            bool settled = lws.IsWorldSettled();
            // S7a hook: under the Site picture policy the camera box is centred at min(y, +140 m) under the camera
            var boxCentre = new Vector3(cam.x, (float)S7aConventions.AreaBoxCentreY(AreaBoxSite, cam.y), cam.z);
            bool camBox = lws.IsRangeActiveAndBuilt(new Bounds(boxCentre, Vector3.one * (2f * BoxHalf)));
            AreaBuiltRaw = boxCentre.y == cam.y ? camBox : lws.IsRangeActiveAndBuilt(new Bounds(cam, Vector3.one * (2f * BoxHalf)));
            Vector3 dir;
            float len;
            ForwardDir(camForward, out dir, out len);
            bool fwdBox = lws.IsRangeActiveAndBuilt(new Bounds(cam + dir * len, Vector3.one * (2f * BoxHalf)));
            // 5-7 s, hovering 8 m over the seabed looking down held 7 s): the forward
 // box lies below the seabed or inside rock in those cases and the clipmap never reports it built; the entry gate
 // is the camera box alone (the near field S1 found always answerable), the forward box is telemetry
 // S7a-1b: above the near field under Site the camera has only air around it; the clamped box lies far
 // below the camera where the clip level is never built (HYPOTHESIS settled): built = the streamer idle
            bool airAbove = S7aConventions.AirAboveTheNearField(AreaBoxSite, cam.y, BoxHalf);
            if (airAbove != AirRule)
            {
                AirRule = airAbove;
                DeLog.Info("DE.S1 areaBuilt air rule " + (airAbove ? "ON: camera above the near field (y " + cam.y.ToString("0", CultureInfo.InvariantCulture) + " m > +" + (S7aConventions.AreaBoxMaxCentreY + BoxHalf).ToString("0", CultureInfo.InvariantCulture) + " m): built = true, nothing to build around the camera" : "OFF: the camera box decides again"));
            }

            // fast approach, and above the near field there is nothing to build around the camera - built is simply true there
            bool built = airAbove || camBox;
            // S8a (B4 hook; CONTRACT "window.ready"): the terrain host's gate (NearPending, unapplied window notices near the camera); null = Subnautica's own value
            Func<bool, Vector3, bool> gate = AreaBuiltGate;
            if (gate != null) { try { built = gate(built, cam); } catch (Exception) { } }
            AreaBuilt = built;
            AreaBuiltForward = fwdBox;
            if (AreaBuilt)
            {
                AreaBuiltSeTick = presentedSeTick;
                AreaBuiltPos = cam;
            }
            if (_probe)
            {
                double ms = (Win32.QpcNow() - _probeQpc) * 1000.0 / Win32.QpcFrequency();
                // settled is measured on the busy -> idle edge: the streamer is still idle in the first frames after a
 // jump
                if (!settled) _probeSawBusy = true;
                if (settled && _settledMs < 0 && (_probeSawBusy || ms >= SettleIdleWindowMs)) _settledMs = _probeSawBusy ? ms : 0;
                if (camBox && _camBoxMs < 0) _camBoxMs = ms;
                if (fwdBox && _fwdBoxMs < 0) _fwdBoxMs = ms;
                if (_settledMs >= 0 && _camBoxMs >= 0 && _fwdBoxMs >= 0) EndProbe(false);
                else if (ms >= ProbeTimeoutMs) EndProbe(true);
            }
            // C7 streamingSettled (CONTRACT §5.4 bit 1): IsWorldSettled seen on a busy -> idle edge (or idle for 1 s) since
 // the last jump. While a jump probe has not seen that yet, the stale idle of the first frames after the jump
 // is not reported
            StreamingSettled = SettleRules.Report(settled, _probe, _settledMs >= 0);
        }

        private void ForwardDir(Vector3 camForward, out Vector3 dir, out float len)
        {
            float speed = VelocitySn.magnitude;
            if (speed >= 0.1f) dir = VelocitySn / speed;
            else dir = camForward.sqrMagnitude > 1e-6f ? camForward.normalized : Vector3.forward;
            len = Mathf.Max(20f, speed * 1f);
        }

        public void StartProbe(double dist, string why, Vector3 to, bool commanded)
        {
            _skipJumpFrames = commanded ? 3 : 0;
            if (_probe) EndProbe(false, "superseded by a new jump");
            _probe = true;
            Probes++;
            _probeQpc = Win32.QpcNow();
            _probeDist = dist;
            _probeWhy = why;
            _probeTo = to;
            _settledMs = _camBoxMs = _fwdBoxMs = -1;
            _probeSawBusy = false;
            _nextAreaMs = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EndProbe(bool timeout, string note = null)
        {
            _probe = false;
            if (timeout) ProbeTimeouts++;
            var ci = CultureInfo.InvariantCulture;
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            string detail = lws != null ? " camBuiltHalf=" + BuiltHalf(lws, _lastCamPos) + " cam" + ((int)BoxHalf).ToString(ci) + "=" +
                                          AreaDetail(lws, new Bounds(_lastCamPos, Vector3.one * (2f * BoxHalf))) : "";
            DeLog.Info("DE.Stream jump dist=" + _probeDist.ToString("0.0", ci) + "m to=" + CameraDriver.V(_probeTo) + " (" + _probeWhy + ")" +
                       " settledMs=" + Ms(_settledMs) + " busy=" + (_probeSawBusy ? "yes" : "no") + " camBoxMs=" + Ms(_camBoxMs) + " fwdBoxMs=" + Ms(_fwdBoxMs) +
                       detail + (timeout ? " TIMEOUT " + ProbeTimeoutMs + " ms" : "") + (note != null ? " (" + note + ")" : ""));
        }

        private static string Ms(double v) { return v < 0 ? "never" : v.ToString("0", CultureInfo.InvariantCulture); }

        /// <summary>The largest half-size of {56, 32, 16, 8, 4} m whose camera box the streamer reports active and built (0: none).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int BuiltHalf(LargeWorldStreamer lws, Vector3 cam)
        {
            try
            {
                foreach (int h in BuiltLadder)
                    if (lws.IsRangeActiveAndBuilt(new Bounds(cam, Vector3.one * (2f * h)))) return h;
            }
            catch (Exception) { return -1; }
            return 0;
        }

        /// <summary>
 /// The conditions of <c>LargeWorldStreamer.IsRangeActiveAndBuilt(Bounds)</c> one by one: the
 /// collision clipmap level's cells loaded over the block range (<c>streamerV2.clipmapStreamer</c>), then the entity
 /// cells awake at Medium, Far and VeryFar (<c>cellManager.AreCellsLoaded</c>). Diagnostic for S1.N3: in the session B
 /// real run the ±56 m boxes were never "built" while the world was settled.
 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string AreaDetail(LargeWorldStreamer lws, Bounds bb)
        {
            try
            {
                string clip = "?";
                global::WorldStreaming.WorldStreamer ws = lws.streamerV2;
                if (ws != null && ws.clipmapStreamer != null)
                    clip = ws.clipmapStreamer.IsRangeActiveAndBuilt(Int3.MinMax(lws.GetBlock(bb.min), lws.GetBlock(bb.max))) ? "1" : "0";
                CellManager cm = lws.cellManager;
                string med = cm != null ? (cm.AreCellsLoaded(bb, LargeWorldEntity.CellLevel.Medium) ? "1" : "0") : "?";
                string far = cm != null ? (cm.AreCellsLoaded(bb, LargeWorldEntity.CellLevel.Far) ? "1" : "0") : "?";
                string vfar = cm != null ? (cm.AreCellsLoaded(bb, LargeWorldEntity.CellLevel.VeryFar) ? "1" : "0") : "?";
                return "clip" + clip + "/med" + med + "/far" + far + "/vfar" + vfar;
            }
            catch (Exception e)
            {
                return "err:" + e.GetType().Name;
            }
        }

        // ------------------------------------------------------------------------------------------ water surface

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RequestWaterSample(Vector3 cam)
        {
            if (_waterRequestInFlight) return;
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                if (!_waterUnsupportedLogged) DeLog.Warn("DE.Water AsyncGPUReadback unsupported: no surface sample (S1.N10 needs another method)");
                _waterUnsupportedLogged = true;
                return;
            }
            WaterSurface ws = WaterSurface.Get();
            if (ws == null) return;
            Texture tex = ws.GetDisplacementTexture();
            float patch = ws.GetPatchLength();
            if (tex == null || patch <= 0f) return;
            int w = tex.width, h = tex.height;
            float u = cam.x / patch, v = cam.z / patch;
            u -= Mathf.Floor(u);
            v -= Mathf.Floor(v);
            int x = Mathf.Clamp((int)(u * w), 0, w - 1);
            int y = Mathf.Clamp((int)(v * h), 0, h - 1);
            _waterRequestInFlight = true;
            AsyncGPUReadback.Request(tex, 0, x, 1, y, 1, 0, 1, TextureFormat.RGBAFloat, OnWaterReadback);
        }

        private void OnWaterReadback(AsyncGPUReadbackRequest req)
        {
            _waterRequestInFlight = false;
            try
            {
                if (req.hasError) return;
                NativeArray<float> d = req.GetData<float>();
                if (d.Length < 3) return;
                float dx = d[0], dy = d[1], dz = d[2];
                _waterDy = dy;
                _waterSamples++;
                if (dy > _waterSecMax) _waterSecMax = dy;
                if (dy < _waterSecMin) _waterSecMin = dy;
                if (dy > _waterWinMax) _waterWinMax = dy;
                if (dy < _waterWinMin) _waterWinMin = dy;
                float dxz = Mathf.Sqrt(dx * dx + dz * dz);
                if (dxz > _waterDxzMax) _waterDxzMax = dxz;
            }
            catch (Exception)
            {
            }
        }

        private string WaterLine()
        {
            var ci = CultureInfo.InvariantCulture;
            string s = "waterDy=" + (float.IsNaN(_waterDy) ? "n/a" : _waterDy.ToString("0.###", ci)) +
                       (_waterSamples > 0 ? " waterDy60=[" + _waterWinMin.ToString("0.###", ci) + "," + _waterWinMax.ToString("0.###", ci) + "]" +
                                            " waterDxzMax=" + _waterDxzMax.ToString("0.###", ci) : "");
            _waterSecond++;
            if (_waterSecond >= 60)
            {
                if (_waterSamples > 0)
                    DeLog.Info("DE.Water 60s surface displacement at the camera: dy min=" + _waterWinMin.ToString("0.###", ci) + " max=" +
                               _waterWinMax.ToString("0.###", ci) + " horizontal max=" + _waterDxzMax.ToString("0.###", ci) + " samples=" + _waterSamples +
                               " (S1.N10; texel mapping HYPOTHESIS) oceanLevel=" + Ocean.GetOceanLevel().ToString("0.###", ci));
                _waterSecond = 0;
                _waterWinMax = float.NegativeInfinity;
                _waterWinMin = float.PositiveInfinity;
                _waterDxzMax = 0;
                _waterSamples = 0;
            }
            _waterSecMax = float.NegativeInfinity;
            _waterSecMin = float.PositiveInfinity;
            return s;
        }

        // ------------------------------------------------------------------------------------------ per second

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string SecondLine(Vector3 cam, Quaternion camRot, CameraDriver driver, GhostPlayer ghost, UiHider ui, bool focusFreezer)
        {
            var ci = CultureInfo.InvariantCulture;
            Player p = Player.main;
            var sb = new StringBuilder(640);
            sb.Append("DE.S1 sec cam=").Append(CameraDriver.V(cam));
            sb.Append(" camY=").Append(cam.y.ToString("0.0000", ci));
            sb.Append(" depth=").Append(Ocean.GetDepthOf(cam).ToString("0.0000", ci));
            sb.Append(" oceanLevel=").Append(Ocean.GetOceanLevel().ToString("0.###", ci));
            sb.Append(" yawDeg=").Append(CamMath.YawDeg(camRot.x, camRot.y, camRot.z, camRot.w).ToString("0.000", ci));
            sb.Append(" pitchDeg=").Append(CamMath.PitchDeg(camRot.x, camRot.y, camRot.z, camRot.w).ToString("0.000", ci));
            Camera c = driver.Camera;
            if (c != null)
            {
                sb.Append(" fov=").Append(c.fieldOfView.ToString("0.####", ci));
                sb.Append(" near=").Append(c.nearClipPlane.ToString("0.####", ci));
                sb.Append(" aspect=").Append(c.aspect.ToString("0.####", ci));
            }
            LargeWorld lw = LargeWorld.main;
            sb.Append(" biome=").Append(lw != null ? (lw.GetBiome(cam) ?? "null") : "n/a");
            if (p != null)
            {
                Vector3 pp = p.transform.position;
                sb.Append(" playerBiome=").Append(lw != null ? (lw.GetBiome(pp) ?? "null") : "n/a");
                sb.Append(" playerCamDist=").Append((pp - cam).magnitude.ToString("0.###", ci));
                sb.Append(" rule=").Append(ghost.AtPelvis ? "pelvis" : "camera");
                sb.Append(" isUnderwater=").Append(p.IsUnderwater() ? "true" : "false");
                bool freecam = false;
                MainCameraControl mcc = MainCameraControl.main;
                if (mcc != null)
                {
                    FreecamController fc = mcc.GetComponent<FreecamController>();
                    freecam = fc != null && fc.GetActive();
                }
                sb.Append(" fogCameraInside=").Append(p.IsInsideWalkable() && !freecam ? "true" : "false");
                sb.Append(" insideWalkable=").Append(p.IsInsideWalkable() ? "true" : "false");
                sb.Append(" canBeAttacked=").Append(p.CanBeAttacked() ? "true" : "false");
                sb.Append(" escapePod=").Append(p.escapePod.value ? "true" : "false");
                sb.Append(" currentSub=").Append(p.currentSub != null ? p.currentSub.name : "null");
                sb.Append(" outOfBoundsWarp=").Append(p.GetComponent<OutOfBoundsWarp>() != null ? "true" : "false");
                sb.Append(" frozenStats=").Append(p.IsFrozenStats() ? "true" : "false");
                sb.Append(" mode=").Append(p.GetMode());
            }
            sb.Append(" runInBg=").Append(MiscSettings.runInBackground ? "true" : "false");
            sb.Append(" focusFreezer=").Append(focusFreezer ? "true" : "false");
            sb.Append(" freezeTop=").Append(FreezeTime.GetTopmostId());
            sb.Append(" timeScale=").Append(Time.timeScale.ToString("0.##", ci));
            sb.Append(" settled=").Append(StreamingSettled ? "true" : "false");
            sb.Append(" areaBuilt=").Append(AreaBuilt ? "true" : "false");
            sb.Append(" areaBuiltRaw=").Append(AreaBuiltRaw ? "true" : "false").Append(" airRule=" + (AirRule ? 1 : 0) + " areaBoxSite=").Append(AreaBoxSite ? "true" : "false");
            LargeWorldStreamer lws = LargeWorldStreamer.main;
            if (lws != null)
            {
                sb.Append(" builtHalf=").Append(BuiltHalf(lws, cam).ToString(ci));
                sb.Append(" area").Append(((int)BoxHalf).ToString(ci)).Append('=').Append(AreaDetail(lws, new Bounds(cam, Vector3.one * (2f * BoxHalf))));
            }
            sb.Append(" uiHidden=").Append(ui.UiHidden ? "true" : "false");
            sb.Append(" presentedSeTick=").Append(driver.PresentedSeTick.ToString(ci));
            sb.Append(" newestTick=").Append(driver.LastLate.NewestTick.ToString(ci));
            if (driver.LastLate.Outcome != SampleOutcome.None)
                sb.Append(" newestAgeMs=").Append(((Win32.QpcNow() - driver.LastLate.NewestQpc) * 1000.0 / Win32.QpcFrequency()).ToString("0.0", ci));
            sb.Append(" gate=").Append(driver.Gate.Open ? "open" : "HOLD");
            sb.Append(" gatedTotal=").Append(driver.Total.Gated.ToString(ci));
            sb.Append(" fovDriftTotal=").Append(driver.Total.FovDrift.ToString(ci));
            sb.Append(driver.OverrideActive ? " override=warpcam" : "");
            sb.Append(' ').Append(WaterLine());
            return sb.ToString();
        }
    }
}
