using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Utils;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S1 test commands (CONTRACT §5.4 C11 and the <c>/de</c> chat relay; ROADMAP S1 "SE plugin"), dev builds only:
 /// <list type="bullet">
 /// <item><c>de.teleport {x, y, z, yawDeg}</c>: SE spectator to F⁻¹ of an SN point, looking along the SN heading
 /// (yaw 0 = north, 90 = east), screen-up = SN up; C1 <c>teleported</c> on the next frame (S1.WM-2);</item>
 /// <item><c>de.path {lengthM 2000, samples 100, altitude −100, headingDeg 90}</c>: the S1.WM-3 run, one sample per
 /// SE update along the great circle from the anchor; each published frame logged as a JSONL <c>wm3</c> event with
 /// the SE camera point in doubles (for <c>tests/s1/check_wm.py</c>);</item>
 /// <item><c>de.look {dir: E | N | down | yaw+30}</c>: spectator orientation at the current camera point along SN
 /// east, north, down, or the current view turned about SN up (S1.WM-4, S1.N2); the first frame showing it is logged
 /// with its tick and <c>qpcCapture</c>;</item>
 /// <item><c>de.pixel {x, y, z}</c> or <c>{ahead: m}</c>: the screen pixel of an SN point (or of the point
 /// <c>m</c> metres ahead on SE's view axis) through <c>MySector.MainCamera.ViewProjectionMatrix</c> and
 /// <c>Viewport</c> (S1.G1-E5);</item>
 /// <item><c>de.site {repair}</c>: the site record and status, or <c>/de site repair</c>; <c>{rebind}</c> validates
 /// again from scratch (<c>/de site rebind</c>);</item>
 /// <item><c>de.spawnplanet</c> and <c>de.g2e3</c>: the lab commands of <see cref="LabCommands"/>.</item>
 /// </list>
 /// The spectator is driven through <c>MySession.SetCameraController(MyCameraControllerEnum.Spectator, null,
 /// position)</c> and <c>MySpectatorCameraController.Static.SetViewMatrix(view)</c>, which sets position and
 /// orientation together
 /// <c>ControlCamera</c> then hands <c>GetViewMatrix</c> to the
 /// camera in the same tick's PrepareForDraw. Update thread only.
 /// </summary>
    internal static class S1Commands
    {
        /// <summary>C1 <c>teleported</c> after a command.</summary>
        public static readonly TeleportLatch TeleportFlag = new TeleportLatch();

        // a command waiting for its first published frame (logs: tick and qpcCapture of the frame that shows it)
        private static string _pendingLabel;
        private static long _pendingQpc;
        private static int _pendingFrames;

        // per-frame JSONL trace after a teleport or look (S1.WM-2 "still >= 2 s", S1.WM-4): 180 frames = 3 s (integration:
 // 2 s plus a margin, so the WM-2 hold is measured on SE frames too, not only on Subnautica's)
        public const int TraceFramesAfterCommand = 180;
        private static int _traceFrames;
        private static string _traceLabel;

        // repeated look steps (S1.N2: 50 scripted 30-degree yaw steps)
        private static string _lookDir;
        private static int _lookRepeatLeft, _lookEveryTicks, _lookCountdown, _lookStep;

        // the WM-3 path
        private static bool _pathActive;
        private static int _pathK, _pathN, _pathCommandedK = -1;
        private static double _pathLength, _pathAltitude, _pathHeading;
        private static double _pathMaxDy, _pathMaxDh, _pathMaxDyCommanded;
        private static ulong _pathFirstTick, _pathLastTick;
        private static int _pathLogged;
        private static Vec3 _pathCommandedSn;
        private static readonly StringBuilder Sb = new StringBuilder(512);

        public static bool PathActive { get { return _pathActive; } }

        // ============================================================================================== dispatch

 /// <summary>DevChannel.ExtraCommand: the reply, or null when <paramref name="cmd"/> is not an S1 command.</summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            switch (cmd)
            {
                case "de.site":
                    if (MiniJson.GetBool(args, "rebind") == true) return SiteBinding.Rebind("de.site rebind via " + via);
                    return MiniJson.GetBool(args, "repair") == true ? SiteBinding.Repair() : SiteBinding.Info();
                case "de.spawnplanet":
                    return LabCommands.SpawnPlanet(args, via);
                case "de.g2e3":
                    return LabCommands.G2E3Check(args, via);
                case "de.teleport":
                    return Teleport(args, via);
                case "de.path":
                    return Path(args, via);
                case "de.look":
                    return Look(args, via);
                case "de.pixel":
                    return Pixel(args, via);
                case "de.water":
                    return WaterLook.Command(args, via);
                default:
                    return null;
            }
        }

        // ============================================================================================== commands

        private static Dictionary<string, object> Teleport(IDictionary<string, object> args, string via)
        {
            string why;
            SiteMap site;
            if (!Ready(out site, out why)) return Fail("de.teleport: " + why);
            double? x = MiniJson.GetNumber(args, "x"), y = MiniJson.GetNumber(args, "y"), z = MiniJson.GetNumber(args, "z");
            if (!x.HasValue || !y.HasValue || !z.HasValue) return Fail("de.teleport needs {\"x\", \"y\", \"z\"} (Subnautica metres) and optional \"yawDeg\"");
            double yaw = MiniJson.GetNumber(args, "yawDeg") ?? 0.0;
            var q = new Vec3(x.Value, y.Value, z.Value);
            SeMatrix m = S1Math.SpectatorAt(site, q, S1Math.HeadingSn(yaw), Vec3.UnitY);
            if (!SetSpectator(m, out why)) return Fail("de.teleport: " + why);
            TeleportFlag.Arm();
            Trace("teleport");
            Vec3 back = site.SeToSn(m.Translation);
            string line = "DE.Teleport sn=" + SiteBinding.V(q) + " yawDeg=" + Num(yaw) + " -> SE P=" + SiteBinding.V(m.Translation) +
                          " F(P)=" + SiteBinding.V(back) + " |F(P)-sn|=" + Num((back - q).Length) + " m (spectator; via " + via + ")";
            Pending("teleport sn=" + SiteBinding.V(q) + " yawDeg=" + Num(yaw));
            DeLog.Info(line);
            var r = Ok(line);
            r["seP"] = Arr(m.Translation);
            r["snCheck"] = Arr(back);
            return r;
        }

        private static Dictionary<string, object> Path(IDictionary<string, object> args, string via)
        {
            string why;
            SiteMap site;
            if (!Ready(out site, out why)) return Fail("de.path: " + why);
            if (_pathActive) return Fail("de.path: a path is already running (sample " + _pathK + "/" + _pathN + ")");
            _pathLength = MiniJson.GetNumber(args, "lengthM") ?? 2000.0;
            double samples = MiniJson.GetNumber(args, "samples") ?? 100.0;
            _pathAltitude = MiniJson.GetNumber(args, "altitude") ?? -100.0;
            _pathHeading = MiniJson.GetNumber(args, "headingDeg") ?? 90.0;
            if (samples < 2 || samples > 100000 || double.IsNaN(_pathLength)) return Fail("de.path: samples must be 2..100000");
            _pathN = (int)samples;
            _pathK = 0;
            _pathCommandedK = -1;
            _pathMaxDy = 0; _pathMaxDh = 0; _pathMaxDyCommanded = 0;
            _pathLogged = 0;
            _pathFirstTick = 0; _pathLastTick = 0;
            _pathActive = true;
            string line = "DE.Path S1.WM-3 start samples=" + _pathN + " lengthM=" + Num(_pathLength) + " altitude=" + Num(_pathAltitude) +
                          " headingDeg=" + Num(_pathHeading) + " (one sample per SE update; JSONL ev=wm3 per published frame; via " + via + ")";
            DeLog.Info(line);
            return Ok(line);
        }

        private static Dictionary<string, object> Look(IDictionary<string, object> args, string via)
        {
            string dir = MiniJson.GetString(args, "dir");
            double repeat = MiniJson.GetNumber(args, "repeat") ?? 1.0;
            double every = MiniJson.GetNumber(args, "everyTicks") ?? 30.0;
            if (repeat < 1 || repeat > 10000 || every < 1 || every > 36000)
                return Fail("de.look: repeat 1..10000, everyTicks 1..36000");
            Vec3 f0, u0;
            string label0;
            if (!S1Math.LookDirections(dir, Vec3.UnitZ, Vec3.UnitY, out f0, out u0, out label0))
                return Fail("de.look needs {\"dir\": \"E\" | \"N\" | \"down\" | \"yaw+30\" | \"pitch+45\"} and optional \"repeat\", \"everyTicks\"");
            _lookRepeatLeft = 0;
            _lookStep = 0;
            string line;
            if (!DoLook(dir, via, 1, (int)repeat, out line)) return Fail(line);
            if (repeat > 1)
            {
                _lookDir = dir;
                _lookStep = 1;
                _lookRepeatLeft = (int)repeat - 1;
                _lookEveryTicks = (int)every;
                _lookCountdown = (int)every;
                line += "; " + _lookRepeatLeft + " more steps every " + _lookEveryTicks + " SE updates";
            }
            return Ok(line);
        }

        /// <summary>One look step at the current camera point; logs <c>DE.Look</c> with the command tick and QPC.</summary>
        private static bool DoLook(string dir, string via, int step, int steps, out string line)
        {
            string why;
            SiteMap site;
            if (!Ready(out site, out why)) { line = "de.look: " + why; return false; }
            MyCamera cam = MySector.MainCamera;
            SeMatrix cur = FrameWriter.ToSe(cam.WorldMatrix);
            Vec3 p = cur.Translation;
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            Vec3 fwdSn = site.DirSeToSn(e, u, n, cur.Forward), upSn = site.DirSeToSn(e, u, n, cur.Up);
            Vec3 f2, u2;
            string label;
            if (!S1Math.LookDirections(dir, fwdSn, upSn, out f2, out u2, out label)) { line = "de.look: bad dir " + dir; return false; }
            SeMatrix m = S1Math.SpectatorAtSe(site, p, f2, u2);
            if (!SetSpectator(m, out why)) { line = "de.look: " + why; return false; }
            long qpc = Win32.QpcNow();
            string tag = "look " + label + (steps > 1 ? " #" + step + "/" + steps : "");
            Pending(tag);
            Trace(tag);
            line = "DE.Look dir=" + label + (steps > 1 ? " step=" + step + "/" + steps : "") + " at sn=" + SiteBinding.V(site.SeToSn(p)) +
                   " forwardSn=" + SiteBinding.V(f2) + " cmdTick=" + FrameWriter.LastTick + " cmdQpc=" + qpc + " (spectator; via " + via + ")";
            DeLog.Info(line);
            return true;
        }

        private static Dictionary<string, object> Pixel(IDictionary<string, object> args, string via)
        {
            string why;
            SiteMap site;
            if (!Ready(out site, out why)) return Fail("de.pixel: " + why);
            MyCamera cam = MySector.MainCamera;
            SeMatrix cw = FrameWriter.ToSe(cam.WorldMatrix);
            Vec3 pSe, pSn;
            double? ahead = MiniJson.GetNumber(args, "ahead");
            if (ahead.HasValue)
            {
                pSe = cw.Translation + cw.Forward * ahead.Value;
                pSn = site.SeToSn(pSe);
            }
            else
            {
                double? x = MiniJson.GetNumber(args, "x"), y = MiniJson.GetNumber(args, "y"), z = MiniJson.GetNumber(args, "z");
                if (!x.HasValue || !y.HasValue || !z.HasValue) return Fail("de.pixel needs {\"x\", \"y\", \"z\"} (Subnautica metres) or {\"ahead\": metres}");
                pSn = new Vec3(x.Value, y.Value, z.Value);
                pSe = site.SnToSe(pSn);
            }
            MatrixD vpm = cam.ViewProjectionMatrix;
            var vp = new[]
            {
                vpm.M11, vpm.M12, vpm.M13, vpm.M14, vpm.M21, vpm.M22, vpm.M23, vpm.M24,
                vpm.M31, vpm.M32, vpm.M33, vpm.M34, vpm.M41, vpm.M42, vpm.M43, vpm.M44,
            };
            var vport = cam.Viewport;
            double px, py, ndcZ;
            bool front = S1Math.Project(vp, vport.OffsetX, vport.OffsetY, vport.Width, vport.Height, pSe, out px, out py, out ndcZ);
            FrameDiagnostics d = FrameWriter.LastDiagnostics;
            double vfov = WireMath.VerticalFovDegrees(cam.FovWithZoom);
            string line = "DE.Pixel S1.G1-E5 tick=" + FrameWriter.LastTick + " sn=" + SiteBinding.V(pSn) + " se=" + SiteBinding.V(pSe) +
                          (ahead.HasValue ? " ahead=" + Num(ahead.Value) + "m" : "") +
                          " px=(" + Num(px) + ", " + Num(py) + ") ndcZ=" + Num(ndcZ) + " inFront=" + front +
                          " viewport=(" + Num(vport.OffsetX) + ", " + Num(vport.OffsetY) + ", " + Num(vport.Width) + ", " + Num(vport.Height) + ")" +
                          " vFovDeg=" + Num(vfov) + " aspect=" + Num(cam.AspectRatio) +
                          " camSn=" + SiteBinding.V(d.CamSn) + " camRot=(" + Num(d.CamRot.X) + ", " + Num(d.CamRot.Y) + ", " + Num(d.CamRot.Z) + ", " + Num(d.CamRot.W) + ")" +
                          " s=" + Num(site.ScaleAt(cw.Translation)) + " (via " + via + ")";
            DeLog.Info(line);
            var r = Ok(line);
            r["px"] = new object[] { px, py };
            r["inFront"] = front;
            r["sn"] = Arr(pSn);
            r["vFovDeg"] = vfov;
            r["viewport"] = new object[] { (double)vport.OffsetX, (double)vport.OffsetY, (double)vport.Width, (double)vport.Height };
            return r;
        }

        // ================================================================================================ per tick

 /// <summary>Plugin Update (before this tick's PrepareForDraw): the next scheduled look step and WM-3 sample.</summary>
        public static void Tick(bool inWorld)
        {
            if (_lookRepeatLeft > 0)
            {
                if (!inWorld) { _lookRepeatLeft = 0; DeLog.Info("DE.Look repeat aborted (left the world)"); }
                else if (--_lookCountdown <= 0)
                {
                    _lookCountdown = _lookEveryTicks;
                    _lookStep++;
                    _lookRepeatLeft--;
                    string line;
                    if (!DoLook(_lookDir, "repeat", _lookStep, _lookStep + _lookRepeatLeft, out line))
                    {
                        _lookRepeatLeft = 0;
                        DeLog.Info("DE.Look repeat aborted: " + line);
                    }
                }
            }
            if (!_pathActive) return;
            SiteMap site = SiteBinding.Map;
            if (!inWorld || site == null)
            {
                _pathActive = false;
                DeLog.Info("DE.Path S1.WM-3 aborted at sample " + _pathK + "/" + _pathN + " (left the world or the site is no longer bound)");
                return;
            }
            if (_pathK >= _pathN) { FinishPath(); return; }
            Vec3 q = S1Math.PathSampleSn(_pathK, _pathN, _pathLength, _pathAltitude, _pathHeading);
            SeMatrix m = S1Math.SpectatorAt(site, q, S1Math.HeadingSn(_pathHeading), Vec3.UnitY);
            string why;
            if (!SetSpectator(m, out why))
            {
                _pathActive = false;
                DeLog.Info("DE.Path S1.WM-3 aborted at sample " + _pathK + ": " + why);
                return;
            }
            if (_pathK == 0) TeleportFlag.Arm(); // the jump to the first sample
            _pathCommandedK = _pathK;
            _pathCommandedSn = q;
            _pathK++;
        }

        /// <summary>The frame writer, after composing (and maybe publishing) a C1 frame: command and path logs.</summary>
        public static void OnFrame(ref SeFrame f, ref FrameDiagnostics d, SiteMap site, bool published)
        {
            if (_pendingLabel != null)
            {
                _pendingFrames++;
                DeLog.Info("DE.Frame first frame after " + _pendingLabel + ": tick=" + f.tick + " qpcCapture=" + f.qpcCapture +
                           " cmdToCaptureMs=" + ((f.qpcCapture - _pendingQpc) * 1000.0 / Win32.QpcFrequency()).ToString("0.000", CultureInfo.InvariantCulture) +
                           " camPos=(" + Num(f.camPos.x) + ", " + Num(f.camPos.y) + ", " + Num(f.camPos.z) + ")" +
                           " camRot=(" + Num(f.camRot.x) + ", " + Num(f.camRot.y) + ", " + Num(f.camRot.z) + ", " + Num(f.camRot.w) + ")" +
                           " teleported=" + ((f.flags & SeFrameFlags.Teleported) != 0) + " published=" + published);
                _pendingLabel = null;
            }
            if (_pathActive && _pathCommandedK >= 0) LogPathSample(ref f, ref d, site, published);
            else if (_traceFrames > 0)
            {
                _traceFrames--;
                LogFrame("c1trace", _traceLabel, -1, ref f, ref d, site, published, d.CamSn);
            }
        }

        private static void LogPathSample(ref SeFrame f, ref FrameDiagnostics d, SiteMap site, bool published)
        {
            int k = _pathCommandedK;
            _pathCommandedK = -1;
            Vec3 q = _pathCommandedSn;
            Vec3 snF = d.CamSn;
            double dy = Math.Abs(snF.Y - _pathAltitude);
            double dh = Math.Sqrt((snF.X - q.X) * (snF.X - q.X) + (snF.Z - q.Z) * (snF.Z - q.Z));
            if (dy > _pathMaxDy) _pathMaxDy = dy;
            if (dh > _pathMaxDh) _pathMaxDh = dh;
            double dyc = Math.Abs(q.Y - _pathAltitude);
            if (dyc > _pathMaxDyCommanded) _pathMaxDyCommanded = dyc;
            if (_pathLogged == 0) _pathFirstTick = f.tick;
            _pathLastTick = f.tick;
            _pathLogged++;

            LogFrame("wm3", "S1.WM-3", k, ref f, ref d, site, published, q);
        }

        /// <summary>
 /// One JSONL frame record (ev = wm3 for path samples, c1trace for the frames after a teleport or look): the SE
 /// camera point in doubles with C and Rs, so tests/s1/check_wm.py recomputes F(P) independently, the camera's
 /// unit forward and up (fwdSe, upSe), the natural gravity (gravSe) and the Water Mod depth (depthWm) at the
 /// camera, plus the published float32 C1 fields.
 /// </summary>
        private static void LogFrame(string ev, string label, int k, ref SeFrame f, ref FrameDiagnostics d, SiteMap site, bool published, Vec3 q)
        {
            Vec3 snF = d.CamSn;
            StringBuilder sb = Sb;
            sb.Length = 0;
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":");
            JsonText.AppendString(sb, ev);
            sb.Append(",\"check\":");
            JsonText.AppendString(sb, label);
            sb.Append(",\"siteVersion\":").Append(SiteV1.SiteVersion)
              .Append(",\"k\":").Append(k).Append(",\"n\":").Append(k >= 0 ? _pathN : 0)
              .Append(",\"tick\":").Append(f.tick).Append(",\"qpcCapture\":").Append(f.qpcCapture)
              .Append(",\"published\":").Append(published ? "true" : "false")
              .Append(",\"C\":"); Arr(sb, site.C);
            sb.Append(",\"Rs\":").Append(G(site.Rs));
            sb.Append(",\"seP\":"); Arr(sb, d.CamSe);
            // Integration (S1): the fields tests/s1/check_wm.py needs for its independent rows (schema s1/1 s1.frame):
 // the camera's unit forward and up (S1.WM-2/WM-4 oracle forward, S1.G1-E5 elevation), the natural gravity at
 // the camera (S1.WM-4 mapped gravity) and the Water Mod GetDepth there (S1.WM-2 "SE GetDepth -150.00").
            sb.Append(",\"fwdSe\":"); Arr(sb, d.CamFwdSe);
            sb.Append(",\"upSe\":"); Arr(sb, d.CamUpSe);
            AppendProbe(sb, d.CamSe);
            sb.Append(",\"snCommanded\":"); Arr(sb, q);
            sb.Append(",\"snF\":"); Arr(sb, snF);
            sb.Append(",\"camPos\":[").Append(G(f.camPos.x)).Append(',').Append(G(f.camPos.y)).Append(',').Append(G(f.camPos.z)).Append(']');
            sb.Append(",\"camRot\":[").Append(G(f.camRot.x)).Append(',').Append(G(f.camRot.y)).Append(',').Append(G(f.camRot.z)).Append(',').Append(G(f.camRot.w)).Append(']');
            sb.Append(",\"pelvisSn\":[").Append(G(f.pelvisSn.x)).Append(',').Append(G(f.pelvisSn.y)).Append(',').Append(G(f.pelvisSn.z)).Append(']');
            sb.Append(",\"vFovDeg\":").Append(G(f.vFovDeg)).Append(",\"cameraMode\":").Append(f.cameraMode).Append(",\"flags\":").Append(f.flags);
            sb.Append(",\"teleported\":").Append((f.flags & SeFrameFlags.Teleported) != 0 ? "true" : "false").Append('}');
            // buffered: written to the file at most once per second
            Jsonl.WriteLineBuffered(sb.ToString());
        }

        /// <summary>
 /// <c>gravSe</c> (natural gravity at the SE camera point, m/s², <c>MyGravityProviderSystem.CalculateNaturalGravityInPoint</c>)
 /// and <c>depthWm</c> (Water Mod <c>GetDepth</c> there, negative below the sea) for a test record; a value that
 /// cannot be read is written as null. Update thread, trace windows only.
 /// </summary>
        private static void AppendProbe(StringBuilder sb, Vec3 p)
        {
            var w = new Vector3D(p.X, p.Y, p.Z);
            sb.Append(",\"gravSe\":");
            try
            {
                Vector3 g = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(w);
                sb.Append('[').Append(G(g.X)).Append(',').Append(G(g.Y)).Append(',').Append(G(g.Z)).Append(']');
            }
            catch (Exception) { sb.Append("null"); }
            sb.Append(",\"depthWm\":");
            WaterModClient api = SiteBinding.Api;
            float? depth = api != null && SiteBinding.Planet != null ? api.Depth(w, SiteBinding.Planet) : null;
            sb.Append(depth.HasValue ? G(depth.Value) : "null");
        }

        private static void FinishPath()
        {
            _pathActive = false;
            DeLog.Info("DE.Path S1.WM-3 done samples=" + _pathN + " logged=" + _pathLogged + " ticks=" + _pathFirstTick + ".." + _pathLastTick +
                       " lengthM=" + Num(_pathLength) + " altitude=" + Num(_pathAltitude) +
                       " SE self-check: max|F(P).y-altitude|=" + G(_pathMaxDy) + " m, max horizontal |F(P)-commanded|=" + G(_pathMaxDh) +
                       " m (the WM-3 oracle is tests/s1/check_wm.py over the JSONL wm3 events and the Subnautica log)");
        }

        // ================================================================================================= helpers

 /// <summary>Also used by <see cref="S2Motion"/>.</summary>
        internal static bool Ready(out SiteMap site, out string why)
        {
            site = SiteBinding.Map;
            why = null;
            if (MySession.Static == null) { why = "no world loaded"; return false; }
            if (site == null) { why = "the site is not bound (" + SiteDecision.StatusName(SiteBinding.Status) + " " + (SiteBinding.Last.Reason ?? "") + ")"; return false; }
            if (MySector.MainCamera == null) { why = "no camera"; return false; }
            return true;
        }

        /// <summary>Also used by <see cref="S2Motion"/>.</summary>
        internal static bool SetSpectator(SeMatrix m, out string why)
        {
            why = null;
            MySession s = MySession.Static;
            MySpectatorCameraController spec = MySpectatorCameraController.Static;
            if (s == null || spec == null) { why = "no session or spectator"; return false; }
            var pos = new Vector3D(m.Translation.X, m.Translation.Y, m.Translation.Z);
            if (!ReferenceEquals(s.CameraController, spec))
                s.SetCameraController(MyCameraControllerEnum.Spectator, null, pos);
            MatrixD world = MatrixD.CreateWorld(pos, new Vector3D(m.Forward.X, m.Forward.Y, m.Forward.Z), new Vector3D(m.Up.X, m.Up.Y, m.Up.Z));
            spec.SetViewMatrix(MatrixD.Invert(world));
            return true;
        }

        private static void Trace(string label)
        {
            _traceFrames = TraceFramesAfterCommand;
            _traceLabel = label;
        }

        private static void Pending(string label)
        {
            _pendingLabel = label;
            _pendingQpc = Win32.QpcNow();
        }

        private static Dictionary<string, object> Ok(string line)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "line", line } };
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.Dev " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }

        private static object[] Arr(Vec3 v) { return new object[] { v.X, v.Y, v.Z }; }

        private static void Arr(StringBuilder sb, Vec3 v)
        {
            sb.Append('[').Append(G(v.X)).Append(',').Append(G(v.Y)).Append(',').Append(G(v.Z)).Append(']');
        }

        /// <summary>Round-trip double text (G17;.NET Framework's "R" is not always round-trip).</summary>
        private static string G(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static string Num(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
