using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>One tick's inputs of the C6 writer besides the sun geometry (<see cref="SunSky"/>).</summary>
    internal struct SunTickInputs
    {
        public ulong Tick;
        /// <summary>Simulation seconds since the previous C6 tick (0 while paused).</summary>
        public double SimDt;
        /// <summary><c>MySectorWeatherComponent.RotationInterval</c> (= 60 × <c>SunRotationIntervalMinutes</c>; 1,200 on DE worlds).</summary>
        public double DayLengthSec;
        public float SimSpeed;
        public bool Paused;
        /// <summary>The sun could be read this tick (else C6 says nothing: flags 0).</summary>
        public bool Readable;
        /// <summary>The bind checks (S1's: |axis·U0| &lt; 1e-3, |base·axis| &lt; 1e-3, interval 20, rotation on) passed.</summary>
        public bool ChecksOk;
        /// <summary><c>MySession.Static.Settings.EnableSunRotation</c>.</summary>
        public bool RotationOn;
        /// <summary>The sync switch (<c>de.s7 time sync</c>, env <c>DEEPENGINEERS_TIMESYNC</c>).</summary>
        public bool SyncOn;
    }

    /// <summary>
 /// The game-independent part of SE's C6 writer (CONTRACT §5.4 "C6 written by SE"): the flags (bit 0
 /// <c>timeSyncValid</c>, bit 1 <c>skyOnly</c>, bit 2 <c>skyPoleValid</c>), the jump counter (+1 when H moves by more
 /// than 1° beyond the expected 2π·Δt/<c>dayLengthSec</c> in one tick, and once at every bind), the sky pole kept from
 /// the last valid tick, and the record fields. Tested in tests/DeepEngineers.SE.Tests (S7aSeTests).
 /// </summary>
    internal sealed class SunSyncCore
    {
        public const double JumpThresholdDeg = 1.0;

        private bool _hasH;
        private double _lastH;
        private short _poleAz, _poleEl;
        private bool _hasPole;

        public uint JumpSeq { get; private set; }
        public long Jumps { get; private set; }
        /// <summary>The H deviation of the last jump (degrees; diagnostic).</summary>
        public double LastJumpDeg { get; private set; }

        /// <summary>A new bind (world load, re-bind): the next tick is a jump by definition (Subnautica's first apply).</summary>
        public void OnBind()
        {
            _hasH = false;
            _hasPole = false;
            JumpSeq++;
            Jumps++;
            LastJumpDeg = double.NaN;
        }

        /// <summary>The dev phase or anything else that moves H on purpose: counted as a jump at once.</summary>
        public void ForceJump()
        {
            _hasH = false;
            JumpSeq++;
            Jumps++;
            LastJumpDeg = double.NaN;
        }

        /// <summary>
 /// Fills the C6 record (all fields but <c>seq</c>) from this tick's sky; true when it is a jump tick. With the sun
 /// unreadable the record keeps the tick and zero flags (Subnautica's drive stops on its own: no valid flag).
 /// </summary>
        public bool Compose(ref SunSky sky, ref SunTickInputs i, ref SeTime t)
        {
            bool jumped = false;
            t.tick = i.Tick;
            t.simSpeed = i.SimSpeed;
            t.paused = i.Paused ? 1u : 0u;
            t.dayLengthSec = (float)i.DayLengthSec;
            if (!i.Readable)
            {
                t.flags = 0;
                t.jumpSeq = JumpSeq;
                _hasH = false;
                return false;
            }
            double expected = i.RotationOn && i.DayLengthSec > 0.0 && i.SimDt > 0.0 ? 2.0 * Math.PI * i.SimDt / i.DayLengthSec : 0.0;
            if (_hasH)
            {
                double dev = SunMath.WrapRad(sky.HourAngleRad - _lastH - expected) * (180.0 / Math.PI);
                if (Math.Abs(dev) > JumpThresholdDeg)
                {
                    JumpSeq++;
                    Jumps++;
                    LastJumpDeg = dev;
                    jumped = true;
                }
            }
            _lastH = sky.HourAngleRad;
            _hasH = true;

            uint flags = 0;
            bool valid = i.ChecksOk && i.RotationOn && i.DayLengthSec > 0.0 && i.SyncOn;
            if (valid) flags |= SeTimeFlags.TimeSyncValid;
            else if (i.SyncOn) flags |= SeTimeFlags.SkyOnly;
            if (sky.PoleValid)
            {
                _poleAz = sky.PoleAzCdeg;
                _poleEl = sky.PoleElCdeg;
                _hasPole = true;
                flags |= S7aConventions.C6FlagsExt.SkyPoleValid;
            }
            t.flags = flags;
            t.jumpSeq = JumpSeq;
            t.hourAngleRad = sky.HourAngleRad;
            t.sunElevationRad = (float)sky.ElevationRad;
            t.sunDirSn = new F32x3((float)sky.SunDirSn.X, (float)sky.SunDirSn.Y, (float)sky.SunDirSn.Z);
            t.sunriseHourAngleRad = (float)sky.SunriseHourAngleRad;
            if (_hasPole) S7aSky.WritePole(ref t, _poleAz, _poleEl);
            else S7aSky.ClearPole(ref t);
            return jumped;
        }

        /// <summary>
 /// The SE sun direction whose hour angle at the camera is <paramref name="hourAngleRad"/>, keeping the declination
 /// (the dev phase override): s′ = cos δ·(cos H·M + sin H·W) + sin δ·a with the camera's meridian M and west W.
 /// </summary>
        public static Vec3 SunForHourAngle(SiteMap map, Vec3 camSe, Vec3 sunSe, Vec3 axisSe, double hourAngleRad)
        {
            Vec3 a = axisSe.Normalized();
            Vec3 s = sunSe.Normalized();
            Vec3 u = (camSe - map.C).Normalized();
            Vec3 m = (u - a * Vec3.Dot(a, u)).Normalized();
            Vec3 w = Vec3.Cross(a, u).Normalized();
            double sd = Math.Max(-1.0, Math.Min(1.0, Vec3.Dot(s, a)));
            double cd = Math.Sqrt(1.0 - sd * sd);
            return (m * Math.Cos(hourAngleRad) + w * Math.Sin(hourAngleRad)) * cd + a * sd;
        }

        /// <summary>The hour angle of a cycle time: H = 2π(c − 0.5) (the inverse of <see cref="SunMath.CycleTime"/>).</summary>
        public static double HourAngleOfCycle(double c)
        {
            return SunMath.WrapRad(2.0 * Math.PI * (SunMath.Frac(c) - 0.5));
        }
    }
}
