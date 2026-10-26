using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7a: SE's sun drives Subnautica's day (CONTRACT §5.4 "Time drive"; ARCHITECTURE §5.12; S7.WM-5 pulled forward).
 /// Every frame while C6 is fresh (≤ 3 s), its <c>skyPoleValid</c> bit and bit 0 (<c>timeSyncValid</c>) or bit 1
 /// (<c>skyOnly</c>) are set: <c>DayNightCycle.main.Pause</c> whenever <c>dayNightSpeed</c> ≠ 0 (re-asserted every
 /// frame; a speed set back by the console's day/night or the end of skip-time is logged as external),
 /// <c>timePassedAsDouble</c> advanced forward only to the day scalar of <c>c = frac(H/2π + 0.5)</c> (holds within 2°,
 /// a single forward jump on the first apply, a new <c>jumpSeq</c> or a hold beyond 2°), and
 /// <c>uSkyManager.main.SunDirection</c>/<c>NorthPoleOffset</c> written from the C6 sky pole (NPO clamped to the scene's
 /// ±60°). <c>SetDayNightTime</c> is never used (it always adds a whole day). Off (C6 stale, sync off, the pole not
 /// valid), quit or disposal: <c>Resume</c> and the sky angles logged at the first apply back. The decisions are
 /// <see cref="DayNightRules"/> (Unity-free, tested). Members VERIFIED:
 /// <c>DayNightCycle.main</c> (public static), <c>Pause</c>, <c>Resume</c>, <c>dayNightSpeed</c> (float property),
 /// <c>timePassedAsDouble</c> (public double field), <c>GetDayScalar</c>, <c>GetDayNightCycleTime</c>,
 /// <c>IsDay</c>, <c>sunRiseTime</c>/<c>sunSetTime</c> (public float fields); <c>uSkyManager.main</c> (public
 /// static), <c>SunDirection</c>, <c>NorthPoleOffset</c>, <c>Timeline</c>, <c>sunMaxAngle</c> (public float fields),
 /// <c>SunDir</c> (public Vector3 property), <c>SunLight</c> (public GameObject). Seeded failure S7a.M6: the time set
 /// straight from the target (backward allowed). A failure disables the drive only (logged once, the clock handed back).
 /// </summary>
    internal static class DayNightApply
    {
        private static readonly DayNightRules Rules = new DayNightRules();
        private static bool _disabled, _haveLogged;
        private static float _loggedSd, _loggedNpo;
        private static int _prevStep = -1;

        public static long Frames, Advances, Holds, Jumps, Externals, NpoWarnings, Releases;
        public static DayNightOutputs Last;
        public static DayNightInputs LastIn;
        /// <summary>Seconds the last writing step added to <c>timePassedAsDouble</c> (negative only under S7a.M6).</summary>
        public static double LastAdvancedS;
        /// <summary>The step kind changed to hold or jump this frame (the s7a.sun row's "each hold or jump").</summary>
        public static bool StepEdge;
        public static bool Disabled { get { return _disabled; } }

        /// <summary>The drive holds Subnautica's clock (LabScene's <c>de.time</c> is refused meanwhile).</summary>
        public static bool HoldsClock { get { return Rules.Holding && !_disabled; } }

        public static void Frame(bool c6Fresh, ref SeTime t)
        {
            StepEdge = false;
            if (_disabled) return;
            try
            {
                FrameCore(c6Fresh, ref t);
            }
            catch (Exception e)
            {
                _disabled = true;
                DeLog.Error("DE.Time drive DISABLED after " + e.GetType().Name + ": " + e.Message + " (Subnautica's clock handed back; the rest of the bridge keeps running)\n" + e);
                try { ReleaseCore("drive disabled", true); } catch (Exception) { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FrameCore(bool c6Fresh, ref SeTime t)
        {
            DayNightCycle dnc = DayNightCycle.main;
            if (dnc == null)
            {
                if (Rules.Holding) { Rules.Reset(); DeLog.Info("DE.Time drive idle: DayNightCycle.main gone (world unloaded)"); }
                return;
            }
            var i = new DayNightInputs
            {
                C6Fresh = c6Fresh, Flags = t.flags, JumpSeq = t.jumpSeq, HourAngleRad = t.hourAngleRad, TimePassed = dnc.timePassedAsDouble,
                Rise = dnc.sunRiseTime, Set = dnc.sunSetTime, DayNightSpeed = dnc.dayNightSpeed, Direct = DevMutants.IsOn(DevMutants.S7aTimeDirect),
            };
            S7aSky.ReadPole(ref t, out i.PoleAzCdeg, out i.PoleElCdeg);
            DayNightOutputs o = Rules.Frame(ref i);
            LastIn = i;
            Last = o;
            if (o.Release) ReleaseCore(o.ReleaseWhy, true);
            if (!o.Drive) { _prevStep = -1; return; }
            Frames++;
            uSkyManager sky = uSkyManager.main;
            if (o.FirstApply)
            {
                if (!_haveLogged && sky != null)
                {
                    _loggedSd = sky.SunDirection;
                    _loggedNpo = sky.NorthPoleOffset;
                    _haveLogged = true;
                }
                DeLog.Info("DE.Time drive ON: DayNightCycle paused and driven forward from C6 (flags 0x" + t.flags.ToString("x", CultureInfo.InvariantCulture) +
                           (((t.flags & SeTimeFlags.SkyOnly) != 0) ? " skyOnly" : "") + ") timePassed=" + F(i.TimePassed) + " speed=" + F(i.DayNightSpeed) +
                           " sky angles logged SunDirection=" + F(_loggedSd) + " NorthPoleOffset=" + F(_loggedNpo));
            }
            if (o.Pause)
            {
                dnc.Pause();
                if (o.External)
                {
                    Externals++;
                    DeLog.Info("DE.Time external speed change: dayNightSpeed was " + F(i.DayNightSpeed) + " (console day/night/daynight or the end of skip-time); paused again");
                }
            }
            if (o.WriteTime)
            {
                LastAdvancedS = o.NewTimePassed - i.TimePassed;
                dnc.timePassedAsDouble = o.NewTimePassed;
            }
            switch (o.Step)
            {
                case SunStep.Advance: Advances++; break;
                case SunStep.Hold: Holds++; break;
                case SunStep.Jump:
                    Jumps++;
                    DeLog.Info("DE.Time jump " + (o.NewJumpSeq ? "(new jumpSeq " + t.jumpSeq + ") " : "") + "timePassed " + F(i.TimePassed) + " -> " + F(o.NewTimePassed) +
                               " (+" + F(o.NewTimePassed - i.TimePassed) + " s) c " + F(o.CNow) + " -> " + F(o.CTarget) + (i.Direct ? " [S7a.M6 direct]" : ""));
                    break;
            }
            StepEdge = (o.Step == SunStep.Hold || o.Step == SunStep.Jump) && o.Step != _prevStep;
            _prevStep = o.Step;
            if (sky != null)
            {
                sky.SunDirection = (float)o.SunDirectionDeg;
                sky.NorthPoleOffset = (float)o.NorthPoleOffsetDeg;
            }
            if (o.NpoClamped && NpoWarnings++ % 600 == 0)
                DeLog.Warn("DE.Time WARN npo " + F(o.NpoRaw) + " deg clamped to ±" + F(DayNightRules.NpoLimitDeg) + " (the scene's OnValidate range)");
        }

        /// <summary>Hands the clock back (sync off, peer stalled, quit, disposal): Resume and the logged sky angles.</summary>
        public static void Release(string why)
        {
            try { ReleaseCore(why, Rules.Holding); }
            catch (Exception e) { DeLog.Warn("DE.Time release: " + e.GetType().Name + ": " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReleaseCore(string why, bool held)
        {
            Rules.Reset();
            Last = default(DayNightOutputs);
            _prevStep = -1;
            if (!held) return;
            DayNightCycle dnc = DayNightCycle.main;
            if (dnc != null) dnc.Resume();
            uSkyManager sky = uSkyManager.main;
            if (_haveLogged && sky != null)
            {
                sky.SunDirection = _loggedSd;
                sky.NorthPoleOffset = _loggedNpo;
            }
            Releases++;
            DeLog.Info("DE.Time drive OFF (" + why + "): DayNightCycle.Resume(), sky angles back to SunDirection=" + F(_loggedSd) + " NorthPoleOffset=" + F(_loggedNpo));
        }

        /// <summary>A new world: the logged angles belong to the old scene.</summary>
        public static void OnWorldLost()
        {
            Rules.Reset();
            _haveLogged = false;
            _prevStep = -1;
        }

        private static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    }
}
