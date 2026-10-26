using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S11 "Creatures and hazards", Subnautica side: the Unity-free rules (compiled into tests/DeepEngineers.SN.Tests).
 // The shared numbers are Wire's S11Conventions/S11Rules; this file holds the switches, the mutants and the small state
 // machines the host drives each frame (the grab clock, the oxygen accumulator, the once-per-blast set, the radiation
 // level the S1 UI hider zeroes every frame).
 // =================================================================================================================

 /// <summary>The S11 switch: env <c>DEEPENGINEERS_S11=on|off</c> (default on; the launcher sets it per session).</summary>
    internal static class S11Switches
    {
        public const string Var = "DEEPENGINEERS_S11";

        public static bool Parse(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            v = v.Trim().ToLowerInvariant();
            return !(v == "off" || v == "0" || v == "false" || v == "no");
        }

        public static bool Enabled
        {
            get
            {
                try { return Parse(Environment.GetEnvironmentVariable(Var)); }
                catch (Exception) { return true; }
            }
        }
    }

    /// <summary>The S11 seeded failures of the Subnautica side (dev builds; <c>de.mutant S11.Mx on|off</c> or the env lists).</summary>
    internal static class S11Mutants
    {
        /// <summary>S11.M1: the weapon floors ignored (a handheld weapon kills a leviathan: S11.A1 red).</summary>
        public const string NoFloor = "S11.M1";
        /// <summary>S11.M2: Explosion rows read without their yield and never clustered (S7's capped intensity: S11.A3 red).</summary>
        public const string NoStack = "S11.M2";
        /// <summary>S11.M3: the proxies left out of the creature avoidance (S11.B1 red).</summary>
        public const string NoAvoid = "S11.M3";
        /// <summary>S11.M4: the grab size class not checked (a too-large grid is held: S11.C1 red; SE has the same name).</summary>
        public const string NoSizeClass = "S11.M4";
        /// <summary>S11.M5: no release record (the grid stays held past the creature's release: S11.C3 red; SE has the same name).</summary>
        public const string NoRelease = "S11.M5";
        /// <summary>S11.M6: no hazard record (S11.D1 red).</summary>
        public const string NoHazard = "S11.M6";
        /// <summary>S11.M7: every hazard record written twice under two ids (S11.D1 once red).</summary>
        public const string DoubleHazard = "S11.M7";
        /// <summary>S11.M8: no oxygen record (S11.E1 red).</summary>
        public const string NoOxygen = "S11.M8";
        /// <summary>S11.M9: no EMP record (S11.D2 red).</summary>
        public const string NoEmp = "S11.M9";
        /// <summary>S11.M10: no heal record (S11.E2 red).</summary>
        public const string NoHeal = "S11.M10";
        /// <summary>S11.M11 (pass 6): grid remnants treated as complete grids (hunted, bitten, grabbed, pushed: S11.C4 red; SE has the same name).</summary>
        public const string NoDebrisFilter = "S11.M11";
        /// <summary>S11.M14: no surface rule (no release at the surface, no follow clamp: S11.C7 red; SE has the same name).</summary>
        public const string NoSurface = "S11.M14";
        /// <summary>S11.M15: the old hit test (no missile sweep, blasts to the creature's transform, no shot from inside: S11.R1-R3 red).</summary>
        public const string OldHitTest = "S11.M15";
        /// <summary>S11.M16: no rage rule (hits on a holder neither anger it nor make it let go: S11.R4, R5 red).</summary>
        public const string NoRage = "S11.M16";

        public static readonly string[] All = { NoFloor, NoStack, NoAvoid, NoSizeClass, NoRelease, NoHazard, DoubleHazard, NoOxygen, NoEmp, NoHeal, NoDebrisFilter, NoSurface, OldHitTest, NoRage };

        public static bool IsOn(string name) { return DevMutants.IsOn(name); }

        public static string Describe()
        {
            var on = new List<string>();
            foreach (string n in All) if (DevMutants.IsOn(n)) on.Add(n);
            return on.Count == 0 ? "none" : string.Join(",", on.ToArray());
        }
    }

    /// <summary>
 /// One grab's clock (C): the hold lasts the planned time (the creature's own Invoke("ReleaseVehicle", 8 + Random·5));
 /// a hold record every RecordIntervalMs; the hold damage every IntervalMs; after the release no new grab by that
 /// creature for CooldownMs (ReaperLeviathan.GetCanGrabVehicle).
 /// </summary>
    internal sealed class S11GrabClock
    {
        public double StartS, EndS, NextHoldS, NextDamageS;
        public double IntervalS, HoldEveryS;

        public S11GrabClock(double nowS, double holdS, S11GrabRow row)
        {
            StartS = nowS;
            EndS = nowS + Math.Max(0.1, holdS);
            IntervalS = Math.Max(0.1, row.IntervalMs / 1000.0);
            HoldEveryS = S11Conventions.Hold.RecordIntervalMs / 1000.0;
            NextHoldS = nowS + HoldEveryS;
            NextDamageS = nowS + IntervalS;
        }

        public bool Expired(double nowS) { return nowS >= EndS; }

        /// <summary>True when a hold record is due now (then schedules the next one).</summary>
        public bool HoldDue(double nowS)
        {
            if (nowS < NextHoldS) return false;
            NextHoldS += HoldEveryS;
            if (NextHoldS <= nowS) NextHoldS = nowS + HoldEveryS; // a long frame never stacks hold records
            return true;
        }

        /// <summary>True when a hold damage tick is due now (then schedules the next one).</summary>
        public bool DamageDue(double nowS)
        {
            if (nowS < NextDamageS) return false;
            NextDamageS += IntervalS;
            if (NextDamageS < nowS) NextDamageS = nowS + IntervalS;
            return true;
        }

        public static bool CooldownOver(double releasedAtS, double nowS, S11GrabRow row)
        {
            return double.IsNegativeInfinity(releasedAtS) || nowS >= releasedAtS + row.CooldownMs / 1000.0;
        }
    }

    /// <summary>
 /// Review 4: Subnautica commits a grab on its start record, but SE may refuse it (Static, TooLarge by its own box,
 /// AlreadyHeld) or let go after a hitch (the 1 s timeout), with no answer back. The truthful check is the effect: after
 /// GraceS the held grid's proxy must stay within MaxGapM of the creature's mouth; a gap held for SustainS lets go.
 /// </summary>
    internal sealed class S11FollowWatch
    {
        public const double GraceS = 1.5, MaxGapM = 15.0, SustainS = 1.0;
        private double _badSince = double.NaN;

        /// <summary>True when the grid stopped following: past the grace, the gap over MaxGapM for SustainS.</summary>
        public bool Lost(double nowS, double startS, double gapM)
        {
            if (nowS - startS < GraceS || double.IsNaN(gapM) || gapM <= MaxGapM) { _badSince = double.NaN; return false; }
            if (double.IsNaN(_badSince)) _badSince = nowS;
            return nowS - _badSince >= SustainS;
        }
    }

    /// <summary>Sums small amounts (OxygenArea's per-step oxygen) and releases them every period as one record.</summary>
    internal sealed class S11Accumulator
    {
        public double Pending, Released;
        public double PeriodS = 0.25, NextS = double.NegativeInfinity;

        public void Add(double v) { if (v > 0 && !double.IsInfinity(v)) Pending += v; }

        /// <summary>The amount to send now (0 = nothing yet), capped at <paramref name="max"/> per record (the rest stays).</summary>
        public double Take(double nowS, double max)
        {
            if (Pending <= 0) return 0.0;
            if (nowS < NextS) return 0.0;
            NextS = nowS + PeriodS;
            double g = Math.Min(Pending, max);
            Pending -= g;
            Released += g;
            return g;
        }
    }

    /// <summary>A bounded set of (blast, target) keys seen once (EMPBlast.OnTouch fires for every collider of a grid, every frame of the blast).</summary>
    internal sealed class S11OnceSet
    {
        private readonly HashSet<long> _keys = new HashSet<long>();
        private readonly Queue<long> _order = new Queue<long>();
        public int Capacity = 1024;

        public bool Add(int blast, ulong entity)
        {
            long k = unchecked(((long)blast << 32) ^ (long)entity ^ (long)(entity >> 32) * 0x9E3779B1L);
            if (!_keys.Add(k)) return false;
            _order.Enqueue(k);
            while (_order.Count > Capacity) _keys.Remove(_order.Dequeue());
            return true;
        }

        public int Count { get { return _keys.Count; } }
        public void Clear() { _keys.Clear(); _order.Clear(); }
    }

    /// <summary>
 /// The radiation level Subnautica computed for the ghost Player (RadiatePlayerInRange.Radiate every 0.2 s) before the S1
 /// UI hider zeroes it each frame: fresh for 0.3 s.
 /// </summary>
    internal sealed class S11Fresh
    {
        public double Value, AtS = double.NegativeInfinity;
        public double MaxAgeS = 0.3;

        public void Seen(double v, double nowS) { if (v > 0 && !double.IsNaN(v)) { Value = v; AtS = nowS; } }
        public double Get(double nowS) { return nowS - AtS <= MaxAgeS ? Value : 0.0; }
    }

    /// <summary>The live <c>de.s11 weapon</c> arguments: class, tier, mult (×), floor (fraction).</summary>
    internal static class S11Args
    {
        public static bool TryWeapon(string cls, string tier, string mult, string floor, out int c, out int t, out int? multPermille, out int? floorPermille, out string error)
        {
            c = S11Conventions.WeaponClass.Parse(cls);
            t = S11Conventions.Tier.Parse(tier);
            multPermille = null;
            floorPermille = null;
            error = null;
            if (c == 0 || t == 0) { error = "weapon: class Handheld|SmallGrid|LargeGrid|Explosion, tier Small|Large|Leviathan"; return false; }
            double d;
            if (!string.IsNullOrEmpty(mult))
            {
                if (!double.TryParse(mult, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || d < 0 || d > 100) { error = "mult 0..100"; return false; }
                multPermille = (int)Math.Round(d * 1000.0, MidpointRounding.AwayFromZero);
            }
            if (!string.IsNullOrEmpty(floor))
            {
                if (!double.TryParse(floor, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || d < 0 || d > 1) { error = "floor 0..1"; return false; }
                floorPermille = (int)Math.Round(d * 1000.0, MidpointRounding.AwayFromZero);
            }
            if (!multPermille.HasValue && !floorPermille.HasValue) { error = "weapon: mult and/or floor"; return false; }
            return true;
        }
    }
}
