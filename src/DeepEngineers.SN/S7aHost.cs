using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S7a part of the Subnautica side that is not the ghost or the picture policy, driven by the S0 bridge after
 /// the other hosts each frame:
 /// <list type="bullet">
 /// <item>C6 (SE's sun, first written by S7a): seqlock-read whenever its <c>seq</c> changes; fresh for 3 s; handed to
 /// <see cref="DayNightApply"/> every frame while in the world (the time drive);</item>
 /// <item>C5 (from the bridge's poll): the flags the out-of-water arms gate reads (<see cref="S7aSnState"/>:
 /// <c>seeThrough</c>, bit 9 <c>pictureSite</c>);</item>
 /// <item>rows: <c>s7a.sun</c> {snFrame, c6Tick, jumpSeq, H, cTarget, sTarget, sNow, cNow, timePassed, step,
 /// advancedS, sunDirectionDeg, northPoleOffsetDeg, sunDirC6, sunDirSn, errDeg, isDay, c6ElevDeg, dayNightSpeed,
 /// lightElevDeg} every 2 s, on each hold or jump and on <c>de.s7 sun</c>;</item>
 /// <item>C11 <c>de.s7</c>: <c>{action: "status" | "sun" | "ghost" | "anim"}</c> (one row each, at once), <c>{walk:
 /// "retarget" | "sebones"}</c> (env <c>DEEPENGINEERS_SN_WALKSOURCE</c>), <c>{ghostMaterial: "auto" | "glass" |
 /// "standard" | "sprite"}</c> (GhostView's).</item>
 /// </list>
 /// Each feature has its own guard (its first exception logs once and stops that feature only).
 /// </summary>
    internal sealed unsafe class S7aHost
    {
        public const ulong SunRowMs = 2000;

        private readonly IS2Bridge _bridge;
        private readonly S1Host _s1;
        private readonly S2Host _s2;
        private readonly byte* _c6;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly S7aRowCadence _sunRow = new S7aRowCadence(SunRowMs);

        private SeTime _t;
        private uint _c6Seq;
        private ulong _c6Ms, _now;
        private bool _c6Seen, _started, _disposed;
        public long C6Reads, C6Busy, SunRows;
        public uint C5PerfMode;

        public S7aHost(IS2Bridge bridge, S1Host s1, S2Host s2, byte* sectionBase, Func<float> frameMsP99)
        {
            if (sectionBase == null) throw new ArgumentNullException("sectionBase");
            _bridge = bridge;
            _s1 = s1;
            _s2 = s2;
            _c6 = Layout.RegionPtr(sectionBase, RegionId.C6SeTime);
            // frameMsP99: kept in the signature (Bridge); the s7a.picture row that used it is S2Host's
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _c6Seq = Seqlock.Current(_c6); // a C6 left by an earlier SE is never taken for a fresh one
            DeLog.Info("DE.S7a start: time drive from C6 (DayNightApply; forward only, hold " + SunMath.HoldMaxDeg + " deg), out-of-water animator drive " +
                       "(S5aHost/S7aAnimRules) walkSource=" + S7aAnimRules.WalkSourceName(S7aSnSwitches.WalkRetarget) + " (" + S7aSnSwitches.WalkSourceVar + ")" +
                       " conventions " + S7aConventions.Header + " " + DeLog.Hex(S7aConventions.Hash) + " mutants=" + DevMutants.Describe());
        }

        public bool HoldsClock { get { return DayNightApply.HoldsClock; } }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(ulong now)
        {
            if (_disposed) return;
            _now = now;
            if (!Disabled("c6")) Guard("c6", PollC6);
            bool fresh = _c6Seen && _c6Ms != 0 && now - _c6Ms <= (ulong)DayNightRules.C6FreshMs;
            if (_s1 != null && _s1.InWorld)
            {
                if (!Disabled("time")) Guard("time", () => DayNightApply.Frame(fresh, ref _t));
                if (!Disabled("sunrow") && (_sunRow.Due("sun", now) || DayNightApply.StepEdge)) Guard("sunrow", () => SunRow("periodic"));
            }
            else if (DayNightApply.HoldsClock) DayNightApply.Release("not in the world");
            // s7a.picture rows: S2Host writes them
        }

        private void PollC6()
        {
            uint cur = Seqlock.Current(_c6);
            if (cur == 0 || cur == _c6Seq) return;
            SeTime tmp;
            uint seq;
            SeqlockReadStatus st = Seqlock.TryReadBytes(_c6, (byte*)&tmp, sizeof(SeTime), Seqlock.MaxTries, out seq);
            if (st == SeqlockReadStatus.Busy) { C6Busy++; return; }
            if (st != SeqlockReadStatus.Fresh || seq == _c6Seq) return;
            _c6Seq = seq;
            _t = tmp;
            _c6Ms = _now;
            if (!_c6Seen) DeLog.Info("DE.S7a C6 first read: tick=" + tmp.tick + " flags=0x" + tmp.flags.ToString("x", CultureInfo.InvariantCulture) + " jumpSeq=" + tmp.jumpSeq);
            _c6Seen = true;
            C6Reads++;
        }

        /// <summary>The bridge's C5 poll: the flags the arms gate and the picture rows read.</summary>
        public void OnSeState(ref SeState st)
        {
            S7aSnState.C5Valid = true;
            S7aSnState.C5Flags = st.flags;
            S7aSnState.C5View = st.viewState;
            S7aSnState.C5Approach = st.approachMode;
            S7aSnState.C5Ms = Win32.TickCount64();
            C5PerfMode = st.perfMode;
        }

        // ------------------------------------------------------------------------------------------ rows

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SunRow(string why)
        {
            DayNightCycle dnc = DayNightCycle.main;
            uSkyManager sky = uSkyManager.main;
            DayNightOutputs o = DayNightApply.Last;
            Vector3 sunSn = sky != null ? sky.SunDir : Vector3.zero;
            var c6Dir = new Vec3(_t.sunDirSn.x, _t.sunDirSn.y, _t.sunDirSn.z);
            double err = sky != null && c6Dir.LengthSquared > 0.5 ? SunMath.AngleDeg(c6Dir, new Vec3(sunSn.x, sunSn.y, sunSn.z)) : double.NaN;
            double lightElev = double.NaN, lightAz = double.NaN;
            if (sky != null && sky.SunLight != null)
            {
                Vector3 f = -sky.SunLight.transform.forward;    // the direction to Subnautica's stylised SunLight
                lightElev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, (double)f.y))) * 180.0 / Math.PI;
                lightAz = Math.Atan2(f.x, f.z) * 180.0 / Math.PI;
            }
            double diskElev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, (double)sunSn.y))) * 180.0 / Math.PI;
            double diskAz = Math.Atan2(sunSn.x, sunSn.z) * 180.0 / Math.PI;
            short az, el;
            S7aSky.ReadPole(ref _t, out az, out el);
            JsonOut j = S5Rows.Begin("s7a.sun");
            j.Str("why", why).Num("snFrame", (long)Time.frameCount).Num("c6Tick", _t.tick).Num("jumpSeq", (long)_t.jumpSeq).Num("flags", (long)_t.flags)
             .Bool("c6Fresh", _c6Seen && _now - _c6Ms <= (ulong)DayNightRules.C6FreshMs).Bool("drive", o.Drive).Bool("holdsClock", DayNightApply.HoldsClock)
             .Num("H", _t.hourAngleRad).Num("cTarget", o.CTarget).Num("sTarget", o.STarget).Num("sNow", o.SNow).Num("cNow", o.CNow)
             .Num("timePassed", dnc != null ? dnc.timePassedAsDouble : double.NaN).Str("step", SunStep.Name(o.Step)).Num("advancedS", DayNightApply.LastAdvancedS)
             .Num("sunDirectionDeg", sky != null ? (double)sky.SunDirection : double.NaN).Num("northPoleOffsetDeg", sky != null ? (double)sky.NorthPoleOffset : double.NaN)
             .Vec("sunDirC6", c6Dir.X, c6Dir.Y, c6Dir.Z).Vec("sunDirSn", sunSn.x, sunSn.y, sunSn.z).Num("errDeg", err)
             .Bool("isDay", dnc != null && dnc.IsDay()).Num("c6ElevDeg", _t.sunElevationRad * 180.0 / Math.PI)
             .Num("dayNightSpeed", dnc != null ? (double)dnc.dayNightSpeed : double.NaN).Num("lightElevDeg", lightElev).Num("lightAzDeg", lightAz)
             .Num("diskElevDeg", sky != null ? diskElev : double.NaN).Num("diskAzDeg", sky != null ? diskAz : double.NaN)
             .Num("dayScalar", dnc != null ? (double)dnc.GetDayScalar() : double.NaN).Num("cycleTime", dnc != null ? (double)dnc.GetDayNightCycleTime() : double.NaN)
             .Num("timeline", sky != null ? (double)sky.Timeline : double.NaN).Num("poleAz", az / 100.0).Num("poleEl", el / 100.0)
             .Num("advances", DayNightApply.Advances).Num("holds", DayNightApply.Holds).Num("jumps", DayNightApply.Jumps).Num("externals", DayNightApply.Externals)
             .Bool("direct", DevMutants.IsOn(DevMutants.S7aTimeDirect));
            S5Rows.End(j);
            SunRows++;
        }

        // ------------------------------------------------------------------------------------------ bridge events

        public void OnNewSePeer()
        {
            _c6Seq = Seqlock.Current(_c6);
            _c6Seen = false;
            _c6Ms = 0;
            S7aSnState.ResetC5();
            if (DayNightApply.HoldsClock) DayNightApply.Release("new SE generation");
            _sunRow.Reset();
        }

        public void OnQuitting(string why)
        {
            DayNightApply.Release("quitting (" + why + ")");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DayNightApply.Release("bridge disposal");
            S7aSnState.ResetC5();
        }

        // ------------------------------------------------------------------------------------------ C11

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd != "de.s7") return false;
            reply.Str("cmd", req.Cmd);
            // S3Args.ToDict turns that list into
 // {action: lightScale} and drops the words, so the light-scale command never reached LightMirror - the raw args first
            var raw = req.Args as Dictionary<string, object>;
            if (raw != null && LightMirror.HandlesKindCommand(raw)) { LightMirror.KindCommand(raw, reply); return true; }
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            object v;
            if (a.TryGetValue("landmarks", out v)) { S7Landmarks.Command(v, reply); return true; } // cs)
            if (LightMirror.HandlesKindCommand(a)) { LightMirror.KindCommand(a, reply); return true; } // S7p-character (P10): de.s7 lightScale <kind> <factor> (LightMirror.cs)
            if (a.TryGetValue("walk", out v))
            {
                bool r;
                if (!S7aAnimRules.TryParseWalkSource(v as string, out r)) { reply.Bool("ok", false).Str("error", "walk: retarget|sebones"); return true; }
                S7aSnSwitches.WalkRetarget = r;
                DeLog.Warn("DE.S7a walk source=" + S7aAnimRules.WalkSourceName(r) + " (de.s7)");
                Status(reply.Bool("ok", true));
                return true;
            }
            if (a.TryGetValue("ghostMaterial", out v))
            {
                GhostView g = GhostView.Current;
                if (g == null) { reply.Bool("ok", false).Str("error", "no ghost view (S5 mirror not started)"); return true; }
                try { g.MaterialSource = v as string; }
                catch (ArgumentException e) { reply.Bool("ok", false).Str("error", e.Message); return true; }
                Status(reply.Bool("ok", true));
                return true;
            }
            string action = S3Args.Str(a, "action", "status").Trim().ToLowerInvariant();
            switch (action)
            {
                case "status":
                {
                    if (_s2 != null) _s2.PictureRowNow(); // one s7a.picture row at once (S2Host's writer)
                    Status(reply.Bool("ok", true));
                    return true;
                }
                case "sun":
                    SunRow("de.s7 sun");
                    Status(reply.Bool("ok", true));
                    return true;
                case "ghost":
                {
                    GhostView g = GhostView.Current;
                    if (g != null) g.RowNow();
                    Status(reply.Bool("ok", true).Str("ghost", g != null ? g.Describe() : "no ghost view"));
                    return true;
                }
                case "anim":
                    S7aSnState.AnimRowRequested = true;
                    Status(reply.Bool("ok", true));
                    return true;
                default:
                    reply.Bool("ok", false).Str("error", "de.s7: action status|sun|ghost|anim, or {walk: retarget|sebones} | {ghostMaterial: auto|glass|standard|sprite}");
                    return true;
            }
        }

        private void Status(JsonObjectWriter w)
        {
            DayNightOutputs o = DayNightApply.Last;
            AnimParams p = S7aSnState.AnimLast;
            w.Bool("c6Seen", _c6Seen).Num("c6Tick", _t.tick).Num("c6Flags", (long)_t.flags).Num("jumpSeq", (long)_t.jumpSeq).Num("c6Reads", C6Reads)
             .Bool("holdsClock", DayNightApply.HoldsClock).Str("step", SunStep.Name(o.Step)).Num("cTarget", o.CTarget).Num("cNow", o.CNow)
             .Num("advances", DayNightApply.Advances).Num("holds", DayNightApply.Holds).Num("jumps", DayNightApply.Jumps).Num("externals", DayNightApply.Externals)
             .Bool("timeDisabled", DayNightApply.Disabled).Bool("c5Valid", S7aSnState.C5Valid).Bool("seeThrough", S7aSnState.SeeThrough)
             .Bool("pictureSite", S7aSnState.PictureSite).Str("walkSource", S7aAnimRules.WalkSourceName(S7aSnSwitches.WalkRetarget))
             .Str("charSource", S7aSnState.CharSource).Num("footFrames", S7aSnState.CharFootFrames).Num("footRetarget", S7aSnState.CharFootRetarget)
             .Str("locoState", S7aConventions.C1LocoState.Name(S7aSnState.AnimLocoState)).Bool("outOfWater", S7aSnState.AnimOut)
             .Bool("armsShown", S7aSnState.AnimArmsShown).Str("armsOutWhy", S7aSnState.ArmsOutWhy ?? "").Num("moveSpeed", (double)p.MoveSpeed)
             .Str("clip", S7aSnState.AnimClip ?? "").Str("ghostMaterial", GhostView.Current != null ? GhostView.Current.MaterialSource : "-")
             .Num("sunRows", SunRows).Num("pictureRows", _s2 != null ? _s2.PictureRows : 0L).Str("mutants", DevMutants.Describe())
             .Str("featuresDisabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
        }

        // ------------------------------------------------------------------------------------------ feature guard

        private bool Disabled(string feature) { return _disabled.Contains(feature); }

        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try { a(); }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S7a FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the bridge keeps running)\n" + e);
            }
        }
    }
}
