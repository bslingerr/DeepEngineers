using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// In-game captures (ROADMAP §3 rule 5; CONTRACT §5.4 C10, §5.1 C10 "one request in flight; a new request id replaces
 /// an unanswered one; result failed after 5 s"): <c>ScreenCapture.CaptureScreenshot(path)</c> (Unity writes the PNG at
 /// the end of the frame in which it is called) into <c>%LOCALAPPDATA%\DeepEngineers\captures\</c>, plus a JSON sidecar
 /// (slice, check id, frames, SE ticks, camera pose, lens, settings, build ids). Requests come from C10 (SE relays the
 /// harness) or from the C11 command <c>de.capture</c>. Kind 1 captures the next presented frame; kind 2 the first frame
 /// whose presented SE tick is ≥ <c>atSeTick</c>. The capture is triggered in the main camera's onPreCull (after the
 /// re-sample), so the PNG shows exactly the frame whose tick and pose the sidecar records; outside the world (no
 /// driven camera) it is triggered from Update. The result is written when the file exists with a stable, non-zero size.
 /// </summary>
    internal sealed unsafe class CaptureService
    {
        public const ulong TimeoutMs = 5000;
        public const int MaxPathBytes = CaptureResult.PathCapacity;

        private readonly byte* _req;
        private readonly byte* _res;
        private uint _reqSeqSeen;
        private bool _reqSeqInit;

        private bool _active, _triggered;
        private bool _fromC10;
        private uint _reqId, _kind;
        private ulong _atSeTick;
        private string _rel, _full, _checkId;
        private ulong _startMs, _snFrame;
        private int _unityFrame;
        private CamSample _sample;
        private ulong _presented;
        private long _lastSize = -1;
        private int _stableTicks;

        // Unity state of the captured frame, taken when the capture is triggered (the sidecar is written later)
        private bool _uValid, _uUnderwater, _uEscapePod, _uHasPlayer;
        private Vector3 _uPos;
        private Quaternion _uRot;
        private float _uFov, _uNear, _uAspect, _uDepth;
        private string _uBiome;
        private int _uScreenW, _uScreenH, _uVSync, _uFps;

        public long Requests, Ok, Failed, Replaced;
        public string LastPath { get; private set; }
        public string LastOutcome { get; private set; }

        public CaptureService(byte* sectionBase)
        {
            byte* region = Layout.RegionPtr(sectionBase, RegionId.C10Capture);
            _req = region + CaptureLayout.RequestOffset;
            _res = region + CaptureLayout.ResultOffset;
        }

        public static string CaptureDir() { return CapturePaths.Dir(); }

        public static bool TryNormalise(string rel, out string normalised, out string error)
        {
            return CapturePaths.TryNormalise(rel, MaxPathBytes, out normalised, out error);
        }

        // -------------------------------------------------------------------------------- requests

 /// <summary>C10 request poll (every frame; a changed seq with a new reqId starts a capture).</summary>
        public void PollC10(ulong nowMs, ulong snFrame)
        {
            uint cur = Seqlock.Current(_req);
            if (!_reqSeqInit)
            {
                // a request left in the section before this bridge started is never executed
                _reqSeqInit = true;
                _reqSeqSeen = cur;
                return;
            }
            if (cur == 0 || cur == _reqSeqSeen) return;
            CaptureRequest r;
            uint seq;
            if (Seqlock.TryReadBytes(_req, (byte*)&r, sizeof(CaptureRequest), Seqlock.MaxTries, out seq) != SeqlockReadStatus.Fresh) return;
            _reqSeqSeen = seq;
            string path = r.GetPath();
            string norm, error;
            if (r.kind != CaptureKind.NextFramePng && r.kind != CaptureKind.AtSeTick)
            {
                WriteResult(r.reqId, CaptureStatus.Failed, 0, snFrame, 0, path);
                DeLog.Warn("DE.Capture C10 reqId=" + r.reqId + " refused: kind " + r.kind);
                return;
            }
            if (!TryNormalise(path, out norm, out error))
            {
                WriteResult(r.reqId, CaptureStatus.Failed, 0, snFrame, 0, path);
                DeLog.Warn("DE.Capture C10 reqId=" + r.reqId + " refused: " + error + " (path '" + path + "')");
                return;
            }
            Begin(nowMs, true, r.reqId, r.kind, r.atSeTick, norm, null);
            WriteResult(r.reqId, CaptureStatus.Pending, 0, snFrame, 0, norm);
        }

        /// <summary>de.capture: kind 1 (next presented frame); returns the full path the PNG will have.</summary>
        public bool StartManual(ulong nowMs, string rel, string checkId, ulong snFrame, out string full, out string error)
        {
            full = null;
            if (string.IsNullOrEmpty(rel)) rel = "sn-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "-f" + snFrame + ".png";
            string norm;
            if (!TryNormalise(rel, out norm, out error)) return false;
            Begin(nowMs, false, 0, CaptureKind.NextFramePng, 0, norm, checkId);
            full = _full;
            return true;
        }

        private void Begin(ulong nowMs, bool fromC10, uint reqId, uint kind, ulong atSeTick, string rel, string checkId)
        {
            if (_active && !_triggered)
            {
                Replaced++;
                if (_fromC10) WriteResult(_reqId, CaptureStatus.Failed, 0, 0, 0, _rel);
                DeLog.Info("DE.Capture request " + Describe() + " replaced before it was taken");
            }
            else if (_active)
            {
                // the previous PNG is being written; its result is no longer awaited
                Replaced++;
            }
            Requests++;
            _active = true;
            _triggered = false;
            _fromC10 = fromC10;
            _reqId = reqId;
            _kind = kind;
            _atSeTick = atSeTick;
            _rel = rel;
            _full = Path.Combine(CaptureDir(), rel);
            _checkId = checkId ?? "";
            _startMs = nowMs;
            _lastSize = -1;
            _stableTicks = 0;
            DeLog.Info("DE.Capture request " + Describe());
        }

        private string Describe()
        {
            return (_fromC10 ? "C10 reqId=" + _reqId : "de.capture") + " kind=" + (_kind == CaptureKind.AtSeTick ? "atSeTick>=" + _atSeTick : "nextFrame") +
                   " path=" + _rel + (_checkId.Length > 0 ? " checkId=" + _checkId : "");
        }

        // -------------------------------------------------------------------------------- trigger and completion

 /// <summary>Main camera onPreCull, after the re-sample: trigger when the request's condition holds.</summary>
        public void OnPreCull(CamSample s, ulong snFrame, ulong presentedTickNow)
        {
            if (!_active || _triggered) return;
            if (_kind == CaptureKind.AtSeTick && (s.Outcome == SampleOutcome.None || s.PresentedTick < _atSeTick)) return;
            Trigger(s, snFrame, s.Outcome != SampleOutcome.None ? s.PresentedTick : presentedTickNow);
        }

        /// <summary>Update: completion, timeout, and kind-1 triggers when no driven camera renders.</summary>
        public void Tick(ulong nowMs, ulong snFrame, bool cameraDriven, ulong presentedSeTick)
        {
            if (!_active) return;
            if (!_triggered)
            {
                if (_kind == CaptureKind.NextFramePng && !cameraDriven) Trigger(default(CamSample), snFrame, presentedSeTick);
                else if (nowMs - _startMs >= TimeoutMs) Finish(false, "not triggered within 5 s (no frame reached atSeTick " + _atSeTick + ")");
                return;
            }
            long size = FileSize(_full);
            if (size > 0)
            {
                if (size == _lastSize) _stableTicks++;
                else _stableTicks = 0;
                _lastSize = size;
                if (_stableTicks >= 1) { Finish(true, size + " bytes"); return; }
            }
            if (nowMs - _startMs >= TimeoutMs) Finish(false, "file not written within 5 s");
        }

        private void Trigger(CamSample s, ulong snFrame, ulong presented)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_full));
                if (File.Exists(_full)) File.Delete(_full); // our own file from an earlier capture of the same name
                CaptureNow(_full);
                SnapshotUnity();
                _triggered = true;
                _sample = s;
                _snFrame = snFrame;
                _presented = presented;
                _unityFrame = FrameCount();
            }
            catch (Exception e)
            {
                Finish(false, "ScreenCapture failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CaptureNow(string full)
        {
            ScreenCapture.CaptureScreenshot(full);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FrameCount() { return Time.frameCount; }

        private static long FileSize(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private void Finish(bool ok, string detail)
        {
            _active = false;
            LastPath = _full;
            LastOutcome = (ok ? "ok " : "failed ") + detail;
            if (ok)
            {
                Ok++;
                WriteSidecar();
            }
            else
            {
                Failed++;
            }
            if (_fromC10) WriteResult(_reqId, ok ? CaptureStatus.Ok : CaptureStatus.Failed, (float)_sample.Alpha, _snFrame, _presented, _rel);
            string line = "DE.Capture " + (ok ? "OK " : "FAILED ") + Describe() + " full=" + _full + " snFrame=" + _snFrame + " unityFrame=" + _unityFrame +
                          " seTickUsed=" + _presented + " outcome=" + _sample.Outcome + " alpha=" + _sample.Alpha.ToString("0.###", CultureInfo.InvariantCulture) +
                          " (" + detail + ")";
            if (ok) DeLog.Info(line);
            else DeLog.Warn(line);
        }

        private void WriteResult(uint reqId, uint status, float alpha, ulong snFrame, ulong seTickUsed, string rel)
        {
            CaptureResult r = default(CaptureResult);
            r.reqId = reqId;
            r.status = status;
            r.alpha = alpha;
            r.snFrame = snFrame;
            r.seTickUsed = seTickUsed;
            if (!r.SetPath(rel ?? "")) r.SetPath("");
            Seqlock.WriteBytes(_res, (byte*)&r, sizeof(CaptureResult));
        }

        private void WriteSidecar()
        {
            try
            {
                var w = new JsonObjectWriter();
                w.Str("slice", "S1").Str("checkId", _checkId).Str("source", _fromC10 ? "C10" : "C11 de.capture").Num("reqId", (long)_reqId)
                 .Str("png", _full).Num("snFrame", _snFrame).Num("unityFrame", (long)_unityFrame).Num("seTickUsed", _presented)
                 .Str("outcome", _sample.Outcome.ToString()).Num("alpha", _sample.Alpha).Num("newestTick", _sample.NewestTick)
                 .Num("olderTick", _sample.OlderTick).Num("newestQpc", _sample.NewestQpc).Num("renderQpc", _sample.RenderQpc)
                 .Num("camX", _sample.Px).Num("camY", _sample.Py).Num("camZ", _sample.Pz)
                 .Num("qx", (double)_sample.Qx).Num("qy", (double)_sample.Qy).Num("qz", (double)_sample.Qz).Num("qw", (double)_sample.Qw)
                 .Num("vFovDeg", (double)_sample.VFovDeg).Num("near", (double)_sample.Near).Num("seAspect", (double)_sample.Aspect)
                 .Num("seFlags", (long)_sample.Flags).Num("cameraMode", (long)_sample.CameraMode);
                AppendUnity(w);
                w.Str("plugin", Plugin.PluginName + " " + Plugin.PluginVersion).Str("build", BuildFlavor.Name)
                 .Num("wireVersion", (long)WireInfo.WireVersion).Num("siteVersion", (long)WireInfo.SiteVersion)
                 .Str("utc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
                 .Num("pid", (long)Win32.CurrentProcessId());
                File.WriteAllText(_full + ".json", w.ToString() + "\n");
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.Capture sidecar not written: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The Unity camera and settings of the captured frame (called right after CaptureScreenshot).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SnapshotUnity()
        {
            _uValid = false;
            try
            {
                _uScreenW = Screen.width;
                _uScreenH = Screen.height;
                _uVSync = QualitySettings.vSyncCount;
                _uFps = Application.targetFrameRate;
                Camera c = MainCamera.camera;
                if (c != null)
                {
                    _uPos = c.transform.position;
                    _uRot = c.transform.rotation;
                    _uFov = c.fieldOfView;
                    _uNear = c.nearClipPlane;
                    _uAspect = c.aspect;
                    _uDepth = Ocean.GetDepthOf(_uPos);
                    LargeWorld lw = LargeWorld.main;
                    _uBiome = lw != null ? lw.GetBiome(_uPos) : null;
                    _uValid = true;
                }
                Player pl = Player.main;
                _uHasPlayer = pl != null;
                if (_uHasPlayer)
                {
                    _uUnderwater = pl.IsUnderwater();
                    _uEscapePod = pl.escapePod.value;
                }
            }
            catch (Exception)
            {
                // the sidecar then carries the SE-side values only
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendUnity(JsonObjectWriter w)
        {
            if (_uValid)
            {
                w.Num("unityCamX", (double)_uPos.x).Num("unityCamY", (double)_uPos.y).Num("unityCamZ", (double)_uPos.z)
                 .Num("unityQx", (double)_uRot.x).Num("unityQy", (double)_uRot.y).Num("unityQz", (double)_uRot.z).Num("unityQw", (double)_uRot.w)
                 .Num("unityFov", (double)_uFov).Num("unityNear", (double)_uNear).Num("unityAspect", (double)_uAspect)
                 .Str("biome", _uBiome).Num("depth", (double)_uDepth);
            }
            w.Num("screenW", (long)_uScreenW).Num("screenH", (long)_uScreenH).Num("vSyncCount", (long)_uVSync)
             .Num("targetFrameRate", (long)_uFps).Str("game", Application.version).Str("unity", Application.unityVersion);
            if (_uHasPlayer) w.Bool("playerUnderwater", _uUnderwater).Bool("escapePod", _uEscapePod);
        }

        public string DescribeState()
        {
            return "requests=" + Requests + " ok=" + Ok + " failed=" + Failed + " replaced=" + Replaced +
                   (_active ? " active(" + Describe() + (_triggered ? ", triggered" : "") + ")" : "") +
                   (LastOutcome != null ? " last=" + LastOutcome + " " + LastPath : "");
        }
    }
}
