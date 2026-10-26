using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7a: SE's sun to Subnautica through C6 (CONTRACT §5.4 "C6 written by SE", "S7a conventions"; ARCHITECTURE §5.12),
 /// written every tick by <see cref="FrameWriter"/> (the PrepareForDraw postfix, camera final) as a seqlock record.
 /// Members: <c>MySector.SunProperties</c> (public static <c>MySunProperties</c>
 /// struct field) with <c>SunDirectionNormalized</c> (public <c>Vector3</c> field, written by
 /// <c>MySectorWeatherComponent.UpdateBeforeSimulation</c> while <c>EnableSunRotation</c>),
 /// <c>BaseSunDirectionNormalized</c> (field) and <c>SunRotationAxis</c> (property); <c>MySession.Static.Settings</c>
 /// <c>EnableSunRotation</c> (bool) and <c>SunRotationIntervalMinutes</c> (float); <c>MySession.Static.ElapsedGameTime</c>
 /// (via the caller's simDt); <c>MySession.Static.GetComponent&lt;MySectorWeatherComponent&gt;</c> (public generic)
 /// with <c>RotationInterval</c> (public float property = <c>m_speed</c>); <c>MySandboxGame.IsPaused</c>;
 /// <c>MyPhysics.SimulationRatio</c>. The geometry is Wire <see cref="SunMath.SeSky"/>; flags, jumps and the pole
 /// are <see cref="SunSyncCore"/>. Bind: at the first tick of a new site map, S1's sun checks (|axis·U0| &lt; 1e-3,
 /// |base·axis| &lt; 1e-3, interval 20 min, rotation on); a failure logs <c>DE.Time REFUSE …</c> and C6 says sky-only.
 /// Switch: <c>de.s7 time sync on|off</c> (env <c>DEEPENGINEERS_TIMESYNC</c>, default on). Dev phase (dev builds,
 /// HYPOTHESIS path): <c>de.s7 time &lt;c&gt;</c> turns <c>SunDirectionNormalized</c> about the axis every tick so the
 /// camera's cycle time equals c (a runtime field, never saved); <c>de.s7 time off</c> clears it. Rows <c>s7a.sun</c>
 /// {tick, checks, H, c, elevDeg, sunDirSn, pole {az, el}, flags, jumpSeq, devPhase, why} at bind, jumps,
 /// <c>de.s7 status</c> and every 10 s. Any failure disables the sun part only (C6 then stays unwritten, flags 0).
 /// </summary>
    internal static class SunSync
    {
        public const string EnvVar = "DEEPENGINEERS_TIMESYNC";
        public const ulong RowPeriodMs = 10000;

        private static readonly SunSyncCore Core = new SunSyncCore();
        private static SiteMap _map;
        private static bool _bound, _disabled, _readFailedLogged;
        private static double? _devPhase;
        private static SunSky _sky;
        private static SeTime _last;
        private static ulong _lastTick, _nextRowMs;
        private static double _dayLengthSec;
        private static bool _rotationOn;
        private static string _bindLine = "not bound";
        private static Vec3 _axis, _sun;

        /// <summary>The sync switch (bit 0 and bit 1 of C6 need it).</summary>
        public static bool SyncOn = ReadEnvOn();
        /// <summary>The bind checks passed (time sync allowed).</summary>
        public static bool ChecksOk { get; private set; }
        public static long Ticks, Errors;
        public static bool Disabled { get { return _disabled; } }
        public static double? DevPhase { get { return _devPhase; } }

        private static bool ReadEnvOn()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return true;
            v = v.Trim().ToLowerInvariant();
            return !(v == "off" || v == "0" || v == "false" || v == "no");
        }

        /// <summary>The bind checks on the current world (S1's, SiteBinding.AppendSun), logged; <see cref="ChecksOk"/> set.</summary>
        public static void Bind()
        {
            try { BindCore(); }
            catch (Exception e)
            {
                ChecksOk = false;
                _bound = true;
                _bindLine = "bind error " + e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.Time REFUSE bind error " + e.GetType().Name + ": " + e.Message + " (C6 sky-only or unwritten)");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BindCore()
        {
            _map = SiteBinding.Map;
            _bound = true;
            // a test run (nl 20261024-051730: the far captures black at "noon"): S8a's treadmill gives every chart step a new site map,
 // and this re-bind cleared the dev phase, so SE's natural clock came back (c 0.96, the sun 65 deg under the horizon,
 // Subnautica's _UweLightScalar 0.015 against 1.0 at the spawn). A dev phase now lasts until the world unloads (OnWorldUnload)
 // or de.s7 time off; a re-bind keeps it
            if (_devPhase.HasValue) DeLog.Info("DE.Time devPhase " + F(_devPhase.Value) + " kept across the re-bind (a new site map: a chart step)");
            Vector3 axis = MySector.SunProperties.SunRotationAxis;
            Vector3 b = MySector.SunProperties.BaseSunDirectionNormalized;
            var settings = MySession.Static != null ? MySession.Static.Settings : null;
            _rotationOn = settings != null && settings.EnableSunRotation;
            float minutes = settings != null ? settings.SunRotationIntervalMinutes : float.NaN;
            double comp = ReadRotationInterval();
            _dayLengthSec = comp > 0.0 ? comp : (minutes > 0f ? minutes * 60.0 : 0.0);
            double axisU = Math.Abs(axis.X * SiteV1.AnchorU0.X + axis.Y * SiteV1.AnchorU0.Y + axis.Z * SiteV1.AnchorU0.Z);
            double baseAxis = Math.Abs(axis.X * b.X + axis.Y * b.Y + axis.Z * b.Z);
            var why = new List<string>();
            if (!(axisU < SiteBinding.SunTolerance)) why.Add("|axis.U0|=" + F(axisU));
            if (!(baseAxis < SiteBinding.SunTolerance)) why.Add("|base.axis|=" + F(baseAxis));
            if (!_rotationOn) why.Add("rotation off");
            if (minutes != SiteV1.SunRotationIntervalMinutes) why.Add("intervalMin=" + F(minutes) + " (want " + F(SiteV1.SunRotationIntervalMinutes) + ")");
            if (!(_dayLengthSec > 0.0)) why.Add("RotationInterval=" + F(comp));
            ChecksOk = why.Count == 0;
            Core.OnBind();
            _bindLine = "axis=(" + F(axis.X) + "," + F(axis.Y) + "," + F(axis.Z) + ") base=(" + F(b.X) + "," + F(b.Y) + "," + F(b.Z) + ") |axis.U0|=" +
                        F(axisU) + " |base.axis|=" + F(baseAxis) + " rotation=" + _rotationOn + " intervalMin=" + F(minutes) +
                        " RotationInterval=" + F(comp) + " dayLengthSec=" + F(_dayLengthSec) + " sync=" + (SyncOn ? "on" : "off");
            if (ChecksOk) DeLog.Info("DE.Time BIND OK " + _bindLine + " (C6 timeSyncValid while the sync switch is on)");
            else DeLog.Info("DE.Time REFUSE " + string.Join(", ", why.ToArray()) + " — " + _bindLine + " (C6 skyOnly: Subnautica drives the sky without the S7a.N5 claim)");
            _nextRowMs = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadRotationInterval()
        {
            try
            {
                MySectorWeatherComponent w = MySession.Static != null ? MySession.Static.GetComponent<MySectorWeatherComponent>() : null;
                return w != null ? w.RotationInterval : double.NaN;
            }
            catch (Exception) { return double.NaN; }
        }

        /// <summary>
 /// One tick (FrameWriter, camera final): binds on a new site map, applies the dev phase, fills
 /// <paramref name="t"/> (every field but seq). False when nothing could be computed (no site, disabled).
 /// </summary>
        public static bool Tick(ulong tick, Vector3D camWorld, double simDt, ref SeTime t)
        {
            if (_disabled) return false;
            try
            {
                return TickCore(tick, camWorld, simDt, ref t);
            }
            catch (Exception e)
            {
                Errors++;
                _disabled = true;
                DeLog.Info("DE.Time OFF after " + e.GetType().Name + ": " + e.Message + " (C6 no longer written; Subnautica's clock runs free)");
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TickCore(ulong tick, Vector3D camWorld, double simDt, ref SeTime t)
        {
            SiteMap map = SiteBinding.Map;
            if (map == null) { _bound = false; return false; }
            if (!_bound || !ReferenceEquals(map, _map)) Bind();
            Ticks++;
            var cam = new Vec3(camWorld.X, camWorld.Y, camWorld.Z);
            var inp = new SunTickInputs
            {
                Tick = tick, SimDt = simDt, DayLengthSec = _dayLengthSec, SimSpeed = MyPhysics.SimulationRatio, Paused = MySandboxGame.IsPaused,
                ChecksOk = ChecksOk, RotationOn = _rotationOn, SyncOn = SyncOn, Readable = true,
            };
            Vec3 s, a;
            try
            {
                Vector3 sv = MySector.SunProperties.SunDirectionNormalized;
                Vector3 av = MySector.SunProperties.SunRotationAxis;
                s = new Vec3(sv.X, sv.Y, sv.Z);
                a = new Vec3(av.X, av.Y, av.Z);
                if (!(s.LengthSquared > 0.5) || !(a.LengthSquared > 0.5)) throw new InvalidOperationException("zero sun direction or axis");
                if (_devPhase.HasValue)
                {
                    // dev phase: turn SE's runtime sun about the axis so the camera's cycle time is the request (never saved)
                    s = SunSyncCore.SunForHourAngle(map, cam, s, a, SunSyncCore.HourAngleOfCycle(_devPhase.Value));
                    MySector.SunProperties.SunDirectionNormalized = new Vector3((float)s.X, (float)s.Y, (float)s.Z);
                }
            }
            catch (Exception e)
            {
                inp.Readable = false;
                s = a = Vec3.Zero;
                if (!_readFailedLogged) { _readFailedLogged = true; DeLog.Info("DE.Time sun unreadable: " + e.GetType().Name + ": " + e.Message + " (C6 flags 0)"); }
            }
            if (inp.Readable) SunMath.SeSky(map, cam, s, a, out _sky);
            _sun = s;
            _axis = a;
            bool jumped = Core.Compose(ref _sky, ref inp, ref t);
            _last = t;
            _lastTick = tick;
            ulong now = Win32.TickCount64();
            if (jumped)
            {
                DeLog.Info("DE.Time jump jumpSeq=" + Core.JumpSeq + " dH=" + F(Core.LastJumpDeg) + " deg H=" + F(_sky.HourAngleRad) + " c=" + F(SunMath.CycleTime(_sky.HourAngleRad)) +
                           " tick=" + tick + (_devPhase.HasValue ? " devPhase=" + F(_devPhase.Value) : ""));
                Row("jump");
            }
            else if (_nextRowMs == 0 || now >= _nextRowMs) Row(_nextRowMs == 0 ? "bind" : "periodic");
            return true;
        }

        /// <summary><c>de.s7 time &lt;c&gt;</c> / <c>de.s7 time off</c> (dev builds).</summary>
        public static void SetDevPhase(double? c)
        {
            if (c.HasValue && (double.IsNaN(c.Value) || double.IsInfinity(c.Value))) c = null;
            _devPhase = c.HasValue ? SunMath.Frac(c.Value) : (double?)null;
            Core.ForceJump();
            DeLog.Info("DE.Time devPhase " + (_devPhase.HasValue ? F(_devPhase.Value) + " (MySector.SunProperties.SunDirectionNormalized turned about the axis every tick; HYPOTHESIS that SE's renderer follows the field)" : "off (SE's own sun)"));
        }

        /// <summary>A new world or an unload: bind again at the next tick, the dev phase cleared.</summary>
        public static void OnWorldUnload()
        {
            _bound = false;
            _map = null;
            _devPhase = null;
        }

        /// <summary>The <c>time</c> key of <c>de.s7</c>: {sync: on|off} | &lt;c 0..1&gt; | "off".</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            object v;
            if (args == null || !args.TryGetValue("time", out v)) return Reply(true, StatusLine());
            var obj = v as IDictionary<string, object>;
            if (obj != null)
            {
                object sv;
                if (!obj.TryGetValue("sync", out sv)) return Reply(false, "de.s7 time: {sync: on|off} | <c 0..1> | off");
                bool on;
                if (!TryOnOff(sv, out on)) return Reply(false, "de.s7 time sync: on|off");
                SyncOn = on;
                DeLog.Info("DE.Time sync " + (on ? "ON" : "OFF") + " (de.s7)" + (on ? "" : ": C6 bits 0 and 1 cleared, Subnautica resumes its own clock"));
                Row("sync");
                return Reply(true, "DE.Time sync " + (on ? "on" : "off"));
            }
            var text = v as string;
            if (text != null && (text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase)))
            {
                SetDevPhase(null);
                return Reply(true, "DE.Time dev phase off");
            }
            double c;
            if (text != null ? double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out c) : TryNumber(v, out c))
            {
                if (!BuildFlavor.IsDev) return Reply(false, "de.s7 time <c>: dev builds only");
                if (c < 0.0 || c > 1.0) return Reply(false, "de.s7 time <c>: 0..1 (0.25 sunrise, 0.5 noon, 0.75 sunset)");
                SetDevPhase(c);
                return Reply(true, "DE.Time dev phase " + F(c) + " (HYPOTHESIS: if SE's sun does not move in a capture, S7a.N5 runs on the natural clock)");
            }
            return Reply(false, "de.s7 time: {sync: on|off} | <c 0..1> | off");
        }

        private static bool TryNumber(object v, out double d)
        {
            d = double.NaN;
            if (v is double) { d = (double)v; return true; }
            if (v is long) { d = (long)v; return true; }
            if (v is int) { d = (int)v; return true; }
            if (v is float) { d = (float)v; return true; }
            if (v is decimal) { d = (double)(decimal)v; return true; }
            return false;
        }

        private static bool TryOnOff(object v, out bool on)
        {
            on = false;
            if (v is bool) { on = (bool)v; return true; }
            var s = v as string;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": on = true; return true;
                case "off": case "false": case "0": on = false; return true;
                default: return false;
            }
        }

        private static Dictionary<string, object> Reply(bool ok, string line)
        {
            var d = Status();
            d["ok"] = ok;
            d["line"] = line;
            if (!ok) d["error"] = line;
            return d;
        }

        /// <summary>The sun part of <c>de.s7 status</c> (and an s7a.sun row).</summary>
        public static Dictionary<string, object> Status()
        {
            SunSky k = _sky;
            SeTime t = _last;
            short az, el;
            S7aSky.ReadPole(ref t, out az, out el);
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "sync", SyncOn }, { "checks", ChecksOk }, { "bound", _bound }, { "disabled", _disabled }, { "bind", _bindLine },
                { "tick", (long)_lastTick }, { "H", k.HourAngleRad }, { "c", SunMath.CycleTime(k.HourAngleRad) },
                { "elevDeg", k.ElevationRad * 180.0 / Math.PI }, { "flags", (long)t.flags }, { "jumpSeq", (long)t.jumpSeq },
                { "poleAz", az / 100.0 }, { "poleEl", el / 100.0 }, { "poleValid", k.PoleValid }, { "dayLengthSec", _dayLengthSec },
                { "devPhase", _devPhase.HasValue ? (object)_devPhase.Value : null }, { "ticks", Ticks }, { "jumps", Core.Jumps },
            };
            return d;
        }

        public static string StatusLine()
        {
            SunSky k = _sky;
            return "DE.Time sync=" + (SyncOn ? "on" : "off") + " checks=" + (ChecksOk ? "ok" : "REFUSE") + " H=" + F(k.HourAngleRad) + " c=" +
                   F(SunMath.CycleTime(k.HourAngleRad)) + " elev=" + F(k.ElevationRad * 180.0 / Math.PI) + " deg flags=" + _last.flags + " jumpSeq=" + _last.jumpSeq +
                   (_devPhase.HasValue ? " devPhase=" + F(_devPhase.Value) : "") + (_disabled ? " DISABLED" : "");
        }

        /// <summary>One s7a.sun row (bind, jumps, sync changes, <c>de.s7 status</c>, every 10 s).</summary>
        public static void Row(string why)
        {
            _nextRowMs = Win32.TickCount64() + RowPeriodMs;
            SunSky k = _sky;
            SeTime t = _last;
            short az, el;
            S7aSky.ReadPole(ref t, out az, out el);
            var sb = new StringBuilder(512);
            sb.Append("{\"t\":");
            JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7a.sun\",\"why\":");
            JsonText.AppendString(sb, why);
            sb.Append(",\"tick\":").Append(_lastTick).Append(",\"checks\":").Append(ChecksOk ? "true" : "false")
              .Append(",\"sync\":").Append(SyncOn ? "true" : "false").Append(",\"H\":");
            JsonText.AppendNumber(sb, k.HourAngleRad);
            sb.Append(",\"c\":");
            JsonText.AppendNumber(sb, SunMath.CycleTime(k.HourAngleRad));
            sb.Append(",\"elevDeg\":");
            JsonText.AppendNumber(sb, k.ElevationRad * 180.0 / Math.PI);
            sb.Append(",\"sunDirSn\":[");
            JsonText.AppendNumber(sb, t.sunDirSn.x); sb.Append(',');
            JsonText.AppendNumber(sb, t.sunDirSn.y); sb.Append(',');
            JsonText.AppendNumber(sb, t.sunDirSn.z);
            sb.Append("],\"pole\":{\"az\":");
            JsonText.AppendNumber(sb, az / 100.0);
            sb.Append(",\"el\":");
            JsonText.AppendNumber(sb, el / 100.0);
            sb.Append("},\"flags\":").Append(t.flags).Append(",\"jumpSeq\":").Append(t.jumpSeq).Append(",\"dayLengthSec\":");
            JsonText.AppendNumber(sb, _dayLengthSec);
            sb.Append(",\"devPhase\":");
            if (_devPhase.HasValue) JsonText.AppendNumber(sb, _devPhase.Value); else sb.Append("null");
            sb.Append(",\"sunSe\":[");
            JsonText.AppendNumber(sb, _sun.X); sb.Append(',');
            JsonText.AppendNumber(sb, _sun.Y); sb.Append(',');
            JsonText.AppendNumber(sb, _sun.Z);
            sb.Append("],\"axisSe\":[");
            JsonText.AppendNumber(sb, _axis.X); sb.Append(',');
            JsonText.AppendNumber(sb, _axis.Y); sb.Append(',');
            JsonText.AppendNumber(sb, _axis.Z);
            sb.Append("]}");
            Jsonl.WriteLine(sb.ToString());
        }

        private static string F(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
    }
}
