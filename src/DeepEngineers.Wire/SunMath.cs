using System;

namespace DeepEngineers.Wire
{
    /// <summary>SE's sun at the camera as C6 carries it (CONTRACT §5.4 "S7a conventions", "C6 written by SE").</summary>
    public struct SunSky
    {
        /// <summary>H = atan2(s·W, s·M): 0 at local noon, + towards sunset (radians, (−π, π]).</summary>
        public double HourAngleRad;
        /// <summary>asin(s·U): the sun's elevation above the camera's horizon (radians).</summary>
        public double ElevationRad;
        /// <summary>acos(clamp(−tan δ·tan λ, −1, 1)) (π/2 when δ = 0).</summary>
        public double SunriseHourAngleRad;
        /// <summary>δ = asin(s·a).</summary>
        public double DeclinationRad;
        /// <summary>λ = asin(U·a): the camera's latitude relative to the sun's rotation axis.</summary>
        public double LatitudeRad;
        /// <summary>The direction to the sun mapped to SN axes at the camera (<see cref="SiteMap.DirSeToSn(Vec3, Vec3)"/>).</summary>
        public Vec3 SunDirSn;
        /// <summary>q = M_sn × W_sn (component cross product of the mapped meridian and west): the sun's rotation pole in SN axes.</summary>
        public Vec3 PoleSn;
        /// <summary>The C6 pad bytes: az = atan2(q.x, q.z), el = asin(q.y) in centidegrees (rounded half away from zero).</summary>
        public short PoleAzCdeg, PoleElCdeg;
        /// <summary>|U·a| ≤ <see cref="SunMath.PoleValidMaxDot"/>: the meridian is defined (C6 flag bit 2 skyPoleValid).</summary>
        public bool PoleValid;
    }

    /// <summary>What Subnautica's time drive does this frame (<see cref="SunMath.Step"/>).</summary>
    public static class SunStep
    {
        /// <summary>In sync (within <see cref="SunMath.SyncEpsDeg"/>): nothing written.</summary>
        public const int None = 0;
        /// <summary>Forward by less than a quarter day.</summary>
        public const int Advance = 1;
        /// <summary>Subnautica slightly ahead (≤ <c>SunHoldMaxCdeg</c>/100 = 2° of cycle angle): nothing written.</summary>
        public const int Hold = 2;
        /// <summary>A single forward jump of less than one day (first apply, a new jumpSeq, a hold beyond 2°, > ¼ day).</summary>
        public const int Jump = 3;

        public static string Name(int s)
        {
            switch (s)
            {
                case None: return "none";
                case Advance: return "advance";
                case Hold: return "hold";
                case Jump: return "jump";
                default: return "?" + s;
            }
        }
    }

    /// <summary>
 /// The S7a sun mapping (CONTRACT §5.4 "S7a conventions": the <c>format pole</c>, <c>format sky</c> and <c>format
 /// time</c> lines; ARCHITECTURE §5.12): SE's sun at the camera (<see cref="SeSky"/>), its cycle time
 /// <c>c = frac(H/2π + 0.5)</c> on Subnautica's <c>GetDayNightCycleTime</c> scale, Subnautica's day scalar and its
 /// inverse, the sky angles
 /// <c>uSkyManager.SunDirection</c>/<c>NorthPoleOffset</c> from the pole, Subnautica's sun disk
 /// (<c>uSkyManager.InitSunAndMoon</c>: <c>SunDir = −(Euler(0, SD, NPO)·Euler(15°·Timeline − 90°, 0, 0)·forward)</c>)
 /// and the forward-only step. Doubles throughout; host-neutral (used by both plugins). The Python twin is
 /// <c>tools/worldmap/worldmap_ref.py s7asun</c> (vectors read by S7aSunVectorTests).
 /// </summary>
    public static class SunMath
    {
        /// <summary>|U·a| above this: the meridian is undefined (camera on the sun's axis), the pole is not written.</summary>
        public const double PoleValidMaxDot = 0.999;
        /// <summary>Subnautica's day (<c>DayNightCycle.kDayLengthSeconds</c>, VERIFIED).</summary>
        public const double SnDayLengthSec = 1200.0;
        /// <summary><c>DayNightCycle.sunRiseTime</c> / <c>sunSetTime</c> serialized defaults (VERIFIED); the live fields are used at run time.</summary>
        public const double SnSunRiseDefault = 0.125, SnSunSetDefault = 0.875;
        /// <summary>Subnautica within this many degrees of cycle angle ahead of the target counts as in sync (float noise).</summary>
        public const double SyncEpsDeg = 1e-6;
        /// <summary>A forward step above a quarter day is a jump (ARCHITECTURE §5.12).</summary>
        public const double JumpMinDays = 0.25;
        /// <summary>The hold band: <c>SunHoldMaxCdeg</c>/100 degrees of cycle angle.</summary>
        public static double HoldMaxDeg { get { return S7aConventions.Limits.SunHoldMaxCdeg / 100.0; } }

        private const double Deg = 180.0 / Math.PI;
        private const double Rad = Math.PI / 180.0;

        public static double Frac(double x) { return x - Math.Floor(x); }

        private static double Clamp1(double x) { return x < -1.0 ? -1.0 : (x > 1.0 ? 1.0 : x); }

        /// <summary>
 /// SE's sun at the camera P: U = normalize(P − C), M = normalize(U − a(a·U)), W = normalize(a × U), H =
 /// atan2(s·W, s·M), elevation asin(s·U), δ = asin(s·a), λ = asin(U·a), the sunrise hour angle, sunDirSn =
 /// DirSeToSn(P, s), q = M_sn × W_sn and its centidegrees. <paramref name="sunDirSe"/> and <paramref name="axisSe"/>
 /// are renormalised here. With |U·a| &gt; <see cref="PoleValidMaxDot"/> the pole is not valid (H then uses an
 /// arbitrary meridian).
 /// </summary>
        public static void SeSky(SiteMap map, Vec3 camSe, Vec3 sunDirSe, Vec3 axisSe, out SunSky sky)
        {
            if (map == null) throw new ArgumentNullException("map");
            sky = default(SunSky);
            Vec3 s = sunDirSe.Normalized();
            Vec3 a = axisSe.Normalized();
            Vec3 u = (camSe - map.C).Normalized();
            double au = Vec3.Dot(a, u);
            sky.PoleValid = Math.Abs(au) <= PoleValidMaxDot;
            Vec3 m;
            if (sky.PoleValid) m = (u - a * au).Normalized();
            else
            {
                // any direction perpendicular to the axis (the pole is not sent; H stays finite for the row)
                Vec3 t = Math.Abs(a.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
                m = (t - a * Vec3.Dot(a, t)).Normalized();
            }
            Vec3 w = sky.PoleValid ? Vec3.Cross(a, u).Normalized() : Vec3.Cross(a, m).Normalized();
            sky.HourAngleRad = Math.Atan2(Vec3.Dot(s, w), Vec3.Dot(s, m));
            sky.ElevationRad = Math.Asin(Clamp1(Vec3.Dot(s, u)));
            sky.DeclinationRad = Math.Asin(Clamp1(Vec3.Dot(s, a)));
            sky.LatitudeRad = Math.Asin(Clamp1(au));
            sky.SunriseHourAngleRad = SunriseHourAngle(sky.DeclinationRad, sky.LatitudeRad);
            Vec3 e, uu, n;
            map.FrameAt(camSe, out e, out uu, out n);
            sky.SunDirSn = map.DirSeToSn(e, uu, n, s);
            Vec3 mSn = map.DirSeToSn(e, uu, n, m);
            Vec3 wSn = map.DirSeToSn(e, uu, n, w);
            sky.PoleSn = Vec3.Cross(mSn, wSn);
            double az, el;
            PoleAzEl(sky.PoleSn, out az, out el);
            sky.PoleAzCdeg = PackCdeg(az);
            sky.PoleElCdeg = PackCdeg(el);
        }

        /// <summary>acos(clamp(−tan δ·tan λ, −1, 1)); exactly π/2 when δ = 0 (SE's own construction on DE worlds).</summary>
        public static double SunriseHourAngle(double declinationRad, double latitudeRad)
        {
            if (declinationRad == 0.0) return Math.PI / 2.0;
            return Math.Acos(Clamp1(-Math.Tan(declinationRad) * Math.Tan(latitudeRad)));
        }

        /// <summary>Degrees as i16 centidegrees: rounded half away from zero, clamped to ±32,767 (NaN → 0).</summary>
        public static short PackCdeg(double deg)
        {
            if (double.IsNaN(deg)) return 0;
            double x = Math.Round(deg * 100.0, MidpointRounding.AwayFromZero);
            if (x >= 32767.0) return 32767;
            if (x <= -32767.0) return -32767;
            return (short)x;
        }

        /// <summary>az = atan2(q.x, q.z), el = asin(q.y), degrees (q normalised first).</summary>
        public static void PoleAzEl(Vec3 q, out double azDeg, out double elDeg)
        {
            Vec3 n = q.Normalized();
            azDeg = Math.Atan2(n.X, n.Z) * Deg;
            elDeg = Math.Asin(Clamp1(n.Y)) * Deg;
        }

        /// <summary>The unit pole of the C6 pad bytes: (cos el·sin az, sin el, cos el·cos az).</summary>
        public static Vec3 PoleFromCdeg(short azCdeg, short elCdeg)
        {
            double az = azCdeg / 100.0 * Rad, el = elCdeg / 100.0 * Rad;
            double ce = Math.Cos(el);
            return new Vec3(ce * Math.Sin(az), Math.Sin(el), ce * Math.Cos(az));
        }

        /// <summary>Subnautica's cycle time of SE's hour angle: c = frac(H/2π + 0.5) (0.25 sunrise, 0.5 noon, 0.75 sunset).</summary>
        public static double CycleTime(double hourAngleRad)
        {
            return Frac(hourAngleRad / (2.0 * Math.PI) + 0.5);
        }

        /// <summary><c>DayNightCycle.GetDayNightCycleTime</c> of a day scalar, in doubles.</summary>
        public static double CycleFromDayScalar(double s, double rise, double set)
        {
            double span = set - rise;
            if (s > rise && s < set) return (s - rise) / span * 0.5 + 0.25;
            double night = 1.0 - span;
            if (s < set) s += 1.0;
            double c = (s - set) / night * 0.5 + 0.75;
            if (c > 1.0) c -= 1.0;
            return c;
        }

        /// <summary>The inverse: day c ∈ [0.25, 0.75] → rise + (c − 0.25)·2·(set − rise); night → frac(set + (c′ − 0.75)·2·(1 − (set − rise))).</summary>
        public static double DayScalarFromCycle(double c, double rise, double set)
        {
            c = Frac(c);
            double span = set - rise;
            if (c >= 0.25 && c <= 0.75) return rise + (c - 0.25) * 2.0 * span;
            double cp = c < 0.25 ? c + 1.0 : c;
            return Frac(set + (cp - 0.75) * 2.0 * (1.0 - span));
        }

        /// <summary>
 /// <c>uSkyManager.SunDirection</c> and <c>NorthPoleOffset</c> (degrees) of a pole q: n = normalize(up − q(up·q))
 /// (the noon point), r = −(q × n) (the sunrise point), SD = atan2(−r.x, −r.z), NPO = atan2(−n.x·cos SD + n.z·sin SD,
 /// n.y). Unclamped (the caller clamps NPO to the scene's ±60°).
 /// </summary>
        public static void SkyAngles(Vec3 pole, out double sunDirectionDeg, out double northPoleOffsetDeg)
        {
            Vec3 q = pole.Normalized();
            Vec3 n = (Vec3.UnitY - q * q.Y).Normalized();
            Vec3 r = -Vec3.Cross(q, n);
            double sd = Math.Atan2(-r.X, -r.Z);
            sunDirectionDeg = sd * Deg;
            northPoleOffsetDeg = Math.Atan2(-n.X * Math.Cos(sd) + n.Z * Math.Sin(sd), n.Y) * Deg;
        }

        /// <summary>
 /// Subnautica's sun disk direction (<c>uSkyManager.SunDir</c>) for cycle time c and the two sky angles:
 /// −(R_y(SD)·R_z(NPO)·R_x(360°·c − 90°)·forward) with Unity's <c>Quaternion.Euler</c> order (z, x, y).
 /// </summary>
        public static Vec3 SnSunDir(double c, double sdDeg, double npoDeg)
        {
            double al = (360.0 * c - 90.0) * Rad, sd = sdDeg * Rad, npo = npoDeg * Rad;
            double vy = -Math.Sin(al), vz = Math.Cos(al);            // R_x(α)·(0, 0, 1)
            double x1 = -vy * Math.Sin(npo), y1 = vy * Math.Cos(npo), z1 = vz;   // R_z(NPO)
            double x2 = x1 * Math.Cos(sd) + z1 * Math.Sin(sd), z2 = -x1 * Math.Sin(sd) + z1 * Math.Cos(sd);   // R_y(SD)
            return new Vec3(-x2, -y1, -z2);
        }

        /// <summary>
 /// The forward-only drive's step (CONTRACT <c>format time</c>): with Subnautica ahead of the target by
 /// <c>frac(cNow − cTarget)</c> cycles, within <see cref="SyncEpsDeg"/> either side → None; at most <see cref="HoldMaxDeg"/>
 /// → Hold (nothing written); else <paramref name="advanceDays"/> = frac(sTarget − sNow) (multiply by
 /// <see cref="SnDayLengthSec"/> for <c>timePassedAsDouble</c>), a Jump when forced (first apply, a new jumpSeq) or
 /// above a quarter day (a hold beyond 2° gives nearly a whole day), an Advance otherwise.
 /// </summary>
        public static int Step(double sNow, double cNow, double sTarget, double cTarget, bool forceJump, out double advanceDays)
        {
            advanceDays = 0.0;
            double aheadDeg = Frac(cNow - cTarget) * 360.0;
            if (aheadDeg <= SyncEpsDeg || aheadDeg >= 360.0 - SyncEpsDeg) return SunStep.None;   // in sync either side (float noise)
            if (aheadDeg <= HoldMaxDeg) return SunStep.Hold;
            advanceDays = Frac(sTarget - sNow);
            if (advanceDays <= 0.0) { advanceDays = 0.0; return SunStep.None; }
            return forceJump || advanceDays > JumpMinDays ? SunStep.Jump : SunStep.Advance;
        }

        /// <summary>
 /// The elevation of Subnautica's stylised <c>SunLight</c> (<c>uSkyManager.InitSunAndMoon</c>, NPO 0): 90° −
 /// |angle|, angle = lerp(0, −max, 4c) before 06:00, lerp(max, 0, 4(c − 0.75)) after 18:00, else lerp(−max, max,
 /// clamp01(2c − 0.5)) (S3.C9's formula; <paramref name="sunMaxAngleDeg"/> 65 in the scene).
 /// </summary>
        public static double SunLightElevationDeg(double c, double sunMaxAngleDeg)
        {
            double t = Frac(c) * 24.0, x = Frac(c), ang;
            if (t < 6.0) ang = Lerp(0.0, -sunMaxAngleDeg, Clamp01(x * 4.0));
            else if (t > 18.0) ang = Lerp(sunMaxAngleDeg, 0.0, Clamp01((x - 0.75) * 4.0));
            else ang = Lerp(-sunMaxAngleDeg, sunMaxAngleDeg, Clamp01(x * 2.0 - 0.5));
            return 90.0 - Math.Abs(ang);
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        private static double Clamp01(double x) { return x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x); }

        /// <summary>The angle between two directions, degrees (atan2 form: exact near 0).</summary>
        public static double AngleDeg(Vec3 a, Vec3 b)
        {
            return Math.Atan2(Vec3.Cross(a, b).Length, Vec3.Dot(a, b)) * Deg;
        }

        /// <summary>A wrapped angle difference a − b in (−π, π].</summary>
        public static double WrapRad(double x)
        {
            x = Math.IEEERemainder(x, 2.0 * Math.PI);
            if (x <= -Math.PI) x += 2.0 * Math.PI;
            return x;
        }
    }
}
