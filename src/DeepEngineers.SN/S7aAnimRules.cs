using System;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>The Player animator parameters of one out-of-water frame (CONTRACT §5.4 "C1 locomotion extension").</summary>
    internal struct AnimParams
    {
        public bool IsUnderwater, OnSurface, Jump, Diving, DivingLand;
        /// <summary><c>move_speed</c> = |(x, y, z)|; <c>move_speed_x</c> (strafe, + right), <c>_y</c> (0 while grounded), <c>_z</c> (forward).</summary>
        public float MoveSpeed, X, Y, Z;
    }

    /// <summary>
 /// The Unity-free rules of the S7a astronaut out of the water: the foot states, the character source rule
 /// (Retarget out of the water in a foot state while the walk source is <c>retarget</c>), the animator parameters
 /// (Subnautica's own above-water rule: <c>move_speed_y</c> 0 while grounded, VERIFIED
 /// <c>ArmsController.GetRelativeVelocity</c>; <c>jump</c> after <c>FallAnimMs</c> of Jump/Fall, VERIFIED
 /// <c>Player.GetPlayFallingAnimation</c>'s 0.45 s; <c>diving</c> falling towards the sea) and the first-person arms
 /// gate above water. Seeded failure S7a.M5: <c>move_speed*</c> forced to 0. Compiled into the SN tests.
 /// </summary>
    internal static class S7aAnimRules
    {
        /// <summary>k = <c>GroundAnimScalePermille</c>/1000 (1.0; HYPOTHESIS for the look).</summary>
        public static float GroundScale { get { return S7aConventions.Limits.GroundAnimScalePermille / 1000f; } }
        /// <summary><c>FallAnimMs</c> in seconds (0.45).</summary>
        public static double FallAnimSeconds { get { return S7aConventions.Limits.FallAnimMs / 1000.0; } }
        /// <summary><c>diving</c>: falling faster than 1 m/s (vUpCm &lt; −100) …</summary>
        public const short DivingVUpCm = -100;
        /// <summary>… with the pelvis above the sea by more than 0.5 m (C1 <c>pelvisSn.y</c>).</summary>
        public const float DivingPelvisMinY = 0.5f;

        /// <summary>Idle, Walk, Run, Sprint, Jump, Fall (<c>S7aConventions.C1LocoState.IsFoot</c>).</summary>
        public static bool FootState(byte state) { return S7aConventions.C1LocoState.IsFoot(state); }

        /// <summary>
 /// The S5 "Retarget" source rule extended (CONTRACT): extension version 1, the swim latch off, a foot state, the
 /// walk source <c>retarget</c>, not seated or dead → Retarget (Subnautica's clips through path G); every other
 /// dry state keeps SeBones (the jetpack, crouch, ladder and seat poses exist only in SE).
 /// </summary>
        public static bool RetargetOutOfWater(byte locoVersion, byte state, bool inWater, bool walkRetarget, bool seated, bool dead)
        {
            return locoVersion == S7aLoco.Version && !inWater && walkRetarget && !seated && !dead && FootState(state);
        }

        /// <summary>
 /// The animator parameters out of the water: <c>is_underwater</c>/<c>on_surface</c> false, <c>move_speed_x</c> =
 /// vStrafe × k, <c>_z</c> = vForward × k, <c>_y</c> = 0 while grounded else vUp × k (m/s), <c>move_speed</c> the
 /// magnitude, <c>jump</c> = Jump or Fall held ≥ 0.45 s (<paramref name="airSeconds"/>), <c>diving</c> = Fall ∧
 /// vUp &lt; −1 m/s ∧ pelvis &gt; +0.5 m, <c>diving_land</c> false (Subnautica's obstacle raycast is not reproduced).
 /// </summary>
        public static AnimParams OutOfWater(ref LocoWireState s, float pelvisSnY, double airSeconds, float k)
        {
            var p = new AnimParams();
            p.IsUnderwater = false;   // out of the water by definition (the swim latch is off)
            p.OnSurface = false;
            bool grounded = s.Has(S7aConventions.C1LocoFlags.Grounded);
            p.X = s.VStrafeCm / 100f * k;
            p.Z = s.VForwardCm / 100f * k;
            p.Y = grounded ? 0f : s.VUpCm / 100f * k;
            bool air = s.State == S7aConventions.C1LocoState.Jump || s.State == S7aConventions.C1LocoState.Fall;
            p.Jump = air && airSeconds >= FallAnimSeconds;
            p.Diving = s.State == S7aConventions.C1LocoState.Fall && s.VUpCm < DivingVUpCm && pelvisSnY > DivingPelvisMinY;
            p.DivingLand = false;
            if (DevMutants.IsOn(DevMutants.S7aMoveSpeedZero)) { p.X = p.Y = p.Z = 0f; }
            p.MoveSpeed = (float)Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            return p;
        }

        /// <summary>
 /// The S5a view-model arms above water (CONTRACT): C1 first person, C1 drives the camera, the camera above the
 /// sea, C5 <c>seeThrough</c> (Subnautica's picture shown), C5 bit 9 <c>pictureSite</c>, a foot state and
 /// <c>de.s5 fpbody=diver</c>. The in-water S5a gate is unchanged (S5aRules.ArmsVisible).
 /// </summary>
        public static bool ArmsOutOfWater(bool firstPerson, bool cameraDrives, bool cameraAboveSea, bool seeThrough, bool pictureSite, byte state, bool fpbodyDiver)
        {
            return firstPerson && cameraDrives && cameraAboveSea && seeThrough && pictureSite && FootState(state) && fpbodyDiver;
        }

        /// <summary>Why the out-of-water arms are hidden (status); null when shown.</summary>
        public static string ArmsOutHiddenReason(bool firstPerson, bool cameraDrives, bool cameraAboveSea, bool seeThrough, bool pictureSite, byte state, bool fpbodyDiver)
        {
            if (!fpbodyDiver) return "fpbody se";
            if (!FootState(state)) return "C1 loco state " + S7aConventions.C1LocoState.Name(state) + " is not a foot state";
            if (!cameraAboveSea) return "camera under the sea";
            if (!pictureSite) return "C5 picture policy is not Site";
            if (!seeThrough) return "C5 seeThrough clear (SE's picture)";
            if (!firstPerson) return "C1 cameraMode is not FirstPerson";
            if (!cameraDrives) return "camera not driven";
            return null;
        }

        public static string WalkSourceName(bool retarget) { return retarget ? "retarget" : "sebones"; }

        public static bool TryParseWalkSource(string text, out bool retarget)
        {
            retarget = true;
            if (text == null) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "retarget": case "g": case "snclips": retarget = true; return true;
                case "sebones": case "se": case "bones": retarget = false; return true;
                default: return false;
            }
        }
    }

    /// <summary>Seconds the C1 loco state has been Jump or Fall (the <c>jump</c> parameter's 0.45 s delay).</summary>
    internal sealed class AirClock
    {
        private bool _inAir;
        private double _since;

        public double Update(byte state, double nowS)
        {
            bool air = state == S7aConventions.C1LocoState.Jump || state == S7aConventions.C1LocoState.Fall;
            if (air && !_inAir) _since = nowS;
            _inAir = air;
            return air ? Math.Max(0.0, nowS - _since) : 0.0;
        }

        public void Reset() { _inAir = false; }
    }

    /// <summary>The S7a switches of the Subnautica side (environment, read once; <c>de.s7 {walk}</c> later).</summary>
    internal static class S7aSnSwitches
    {
        public const string WalkSourceVar = "DEEPENGINEERS_SN_WALKSOURCE";

        /// <summary>
 /// The walk source: true = <c>retarget</c> (Subnautica's clips on the SE body), false = <c>sebones</c> (SE's own walk, run
 /// and jump animation).
 /// SE, more like Subnautica; the animation is weird when running and jumping"); env or <c>de.s7 {walk}</c> switch it.
 /// </summary>
        public static bool WalkRetarget = ReadWalk();

        private static bool ReadWalk()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(WalkSourceVar); } catch (Exception) { }
            bool r;
            return !string.IsNullOrEmpty(v) && S7aAnimRules.TryParseWalkSource(v, out r) && r;
        }
    }

    /// <summary>
 /// The latest S7a facts one SN part publishes for another (main thread only): the C5 flags (S7aHost, from the
 /// bridge's C5 poll), the character source (CharacterMirror), the animator snapshot (S5aHost) and the row requests
 /// of <c>de.s7 {action}</c>. Unity-free.
 /// </summary>
    internal static class S7aSnState
    {
        public static bool C5Valid;
        public static uint C5Flags, C5View, C5Approach;
        public static ulong C5Ms;

        public static bool SeeThrough { get { return C5Valid && (C5Flags & SeStateFlags.SeeThrough) != 0; } }
        public static bool PictureSite { get { return C5Valid && (C5Flags & S7aConventions.C5FlagsExt.PictureSite) != 0; } }

        // CharacterMirror
        public static string CharSource = "None";
        public static long CharFootFrames, CharFootRetarget;

        // S5aHost animator snapshot
        public static ulong AnimSeTick;
        public static byte AnimLocoState;
        public static bool AnimLocoValid, AnimInWater, AnimOut, AnimArmsShown, AnimDriving;
        public static AnimParams AnimLast;
        public static string AnimClip;
        public static string ArmsOutWhy;
        public static bool AnimRowRequested;

        public static void ResetC5() { C5Valid = false; C5Flags = 0; C5Ms = 0; }
    }

    /// <summary>One frame's inputs of the time drive (<see cref="DayNightRules"/>).</summary>
    internal struct DayNightInputs
    {
        /// <summary>C6 read within the last 3 s.</summary>
        public bool C6Fresh;
        public uint Flags, JumpSeq;
        public double HourAngleRad;
        public short PoleAzCdeg, PoleElCdeg;
        /// <summary><c>DayNightCycle.main.timePassedAsDouble</c>.</summary>
        public double TimePassed;
        /// <summary>The live <c>sunRiseTime</c> / <c>sunSetTime</c>.</summary>
        public double Rise, Set;
        /// <summary><c>DayNightCycle.main.dayNightSpeed</c>.</summary>
        public float DayNightSpeed;
        /// <summary>Seeded failure S7a.M6: <c>timePassedAsDouble</c> set straight from the target (backward allowed).</summary>
        public bool Direct;
    }

    /// <summary>What the time drive does this frame.</summary>
    internal struct DayNightOutputs
    {
        /// <summary>C6 fresh and valid: the clock is ours this frame.</summary>
        public bool Drive;
        /// <summary>First frame of a hold of the clock (log the scene's sky angles to restore them later).</summary>
        public bool FirstApply;
        /// <summary>Hand the clock back (Resume, the logged sky angles).</summary>
        public bool Release;
        public string ReleaseWhy;
        /// <summary><c>dayNightSpeed</c> ≠ 0: call <c>Pause</c>; <see cref="External"/> when something else set it back while we held it.</summary>
        public bool Pause, External;
        /// <summary>Write <see cref="NewTimePassed"/> to <c>timePassedAsDouble</c>.</summary>
        public bool WriteTime;
        public double NewTimePassed;
        /// <summary><see cref="SunStep"/>.</summary>
        public int Step;
        /// <summary>The step in days (negative only under S7a.M6).</summary>
        public double AdvanceDays;
        public double CTarget, STarget, SNow, CNow;
        public double SunDirectionDeg, NorthPoleOffsetDeg;
        /// <summary>The raw NPO was beyond the scene's ±60° (<c>DE.Time WARN npo</c>).</summary>
        public bool NpoClamped;
        public double NpoRaw;
        public bool NewJumpSeq;
    }

    /// <summary>
 /// The Unity-free core of Subnautica's time drive (<see cref="DayNightApply"/>; CONTRACT §5.4 "Time drive"): drive
 /// while C6 is fresh (≤ 3 s), <c>skyPoleValid</c> and bit 0 or bit 1 are set; <c>Pause</c> whenever the speed is not
 /// 0; target <c>c = frac(H/2π + 0.5)</c> and its day scalar through the live sunrise/sunset; the forward-only
 /// <see cref="SunMath.Step"/> (first apply and a new <c>jumpSeq</c> logged as a jump); the sky angles from the C6
 /// pole, NPO clamped to ±60°. Compiled into the SN tests.
 /// </summary>
    internal sealed class DayNightRules
    {
        public const double C6FreshMs = 3000.0;
        public const double NpoLimitDeg = 60.0;

        private bool _holding, _haveSeq;
        private uint _lastSeq;

        public bool Holding { get { return _holding; } }

        public void Reset() { _holding = false; _haveSeq = false; }

        public DayNightOutputs Frame(ref DayNightInputs i)
        {
            var o = new DayNightOutputs();
            bool flagsOk = (i.Flags & (SeTimeFlags.TimeSyncValid | SeTimeFlags.SkyOnly)) != 0 && (i.Flags & S7aConventions.C6FlagsExt.SkyPoleValid) != 0;
            if (!i.C6Fresh || !flagsOk)
            {
                if (_holding)
                {
                    o.Release = true;
                    o.ReleaseWhy = !i.C6Fresh ? "C6 stale (SE stalled, exited or not writing)" :
                        ((i.Flags & (SeTimeFlags.TimeSyncValid | SeTimeFlags.SkyOnly)) == 0 ? "C6 sync off" : "C6 sky pole not valid");
                    _holding = false;
                }
                return o;
            }
            o.Drive = true;
            o.FirstApply = !_holding;
            _holding = true;
            o.Pause = i.DayNightSpeed != 0f;
            o.External = o.Pause && !o.FirstApply;
            o.NewJumpSeq = !_haveSeq || i.JumpSeq != _lastSeq;
            _lastSeq = i.JumpSeq;
            _haveSeq = true;
            bool force = o.FirstApply || o.NewJumpSeq;

            double rise = i.Rise, set = i.Set;
            if (!(set > rise) || rise < 0.0 || set > 1.0) { rise = SunMath.SnSunRiseDefault; set = SunMath.SnSunSetDefault; }
            o.CTarget = SunMath.CycleTime(i.HourAngleRad);
            o.STarget = SunMath.DayScalarFromCycle(o.CTarget, rise, set);
            o.SNow = SunMath.Frac(i.TimePassed / SunMath.SnDayLengthSec);
            o.CNow = SunMath.CycleFromDayScalar(o.SNow, rise, set);
            if (i.Direct)
            {
                double target = (Math.Floor(i.TimePassed / SunMath.SnDayLengthSec) + o.STarget) * SunMath.SnDayLengthSec;
                o.AdvanceDays = (target - i.TimePassed) / SunMath.SnDayLengthSec;
                o.Step = o.AdvanceDays == 0.0 ? SunStep.None : (force || Math.Abs(o.AdvanceDays) > SunMath.JumpMinDays ? SunStep.Jump : SunStep.Advance);
                o.WriteTime = o.AdvanceDays != 0.0;
                o.NewTimePassed = target;
            }
            else
            {
                double adv;
                o.Step = SunMath.Step(o.SNow, o.CNow, o.STarget, o.CTarget, force, out adv);
                o.AdvanceDays = adv;
                if (o.Step == SunStep.Advance || o.Step == SunStep.Jump)
                {
                    o.WriteTime = true;
                    o.NewTimePassed = i.TimePassed + adv * SunMath.SnDayLengthSec;
                }
            }

            double sd, npo;
            SunMath.SkyAngles(SunMath.PoleFromCdeg(i.PoleAzCdeg, i.PoleElCdeg), out sd, out npo);
            o.NpoRaw = npo;
            if (npo > NpoLimitDeg) { npo = NpoLimitDeg; o.NpoClamped = true; }
            else if (npo < -NpoLimitDeg) { npo = -NpoLimitDeg; o.NpoClamped = true; }
            o.SunDirectionDeg = sd;
            o.NorthPoleOffsetDeg = npo;
            return o;
        }
    }

    /// <summary>The cadence of the s7a.anim (2 Hz and every change) and s7a.sun (every 2 s) rows.</summary>
    internal sealed class S7aRowCadence
    {
        private readonly ulong _periodMs;
        private ulong _lastMs;
        private string _key;
        private bool _have;

        public S7aRowCadence(ulong periodMs) { _periodMs = periodMs; }

        public bool Due(string key, ulong nowMs)
        {
            bool changed = !_have || !string.Equals(key, _key, StringComparison.Ordinal);
            if (!changed && nowMs - _lastMs < _periodMs) return false;
            _have = true;
            _key = key;
            _lastMs = nowMs;
            return true;
        }

        public void Reset() { _have = false; }
    }

    internal static class S7aFmt
    {
        public static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    }
}
