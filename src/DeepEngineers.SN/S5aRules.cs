using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>How the arms decide to show (<c>de.arms gate=…</c>).</summary>
    internal enum ArmsGate
    {
        /// <summary>Default: C1 inWater, the camera under the sea, SE's own first-person camera, Subnautica the visible renderer (C5 Underwater/Surfacing), C1 driving.</summary>
        Full = 0,
        /// <summary>C1 inWater and the camera under the sea only (SN-alone tests with <c>de.s2c5</c>, or a third-person check).</summary>
        Camera = 1,
        /// <summary>Dev: shown whenever the ghost is ready (the renderers and the pose can be inspected out of the water).</summary>
        ForceOn = 2,
        /// <summary>Dev: never shown.</summary>
        ForceOff = 3,
    }

    /// <summary>
 /// The Unity-free rules of S5a "arms and breath" on the Subnautica side (docs/S5A-STATUS.md §6 "SN log contract",
 /// CONTRACT §5.4 "C1 swim extension"): the state and visibility mapping of the C1 swim extension, the exact log lines
 /// the oracle <c>tests/s5a/analyze_s5a.py</c> parses (<c>DE.Arms</c>, <c>DE.O2</c>, <c>DE.Breath</c>, <c>DE.PDA</c>,
 /// <c>DE.SwimWire</c>), the OxygenManager mirror arithmetic, the breath cadence by depth class and the arms' motion
 /// reconstruction. Everything the game touches is in <see cref="S5aHost"/>; this file is compiled into the SN tests.
 /// </summary>
    internal static class S5aRules
    {
        /// <summary>DE.Arms / DE.O2 cadence (the contract: every 0.5 s).</summary>
        public const ulong LogPeriodMs = 500;
        /// <summary>DE.PDA cadence (the contract: once per minute and at shutdown).</summary>
        public const ulong PdaPeriodMs = 60000;
        /// <summary>The `window=` value of the DE.PDA line (seconds).</summary>
        public const int PdaWindowS = 60;
        /// <summary>Below this difference (Subnautica oxygen units = seconds) the mirror writes nothing.</summary>
        public const double O2Epsilon = 1e-3;
        /// <summary>
 /// The oxygen floor (units) used when the death path could not be patched: Player.SuffocationUpdate starts
 /// suffocating at <c>Utils.NearlyEqual(available, 0)</c>, so one unit
 /// above zero keeps the ghost from ever reaching SuffocationDie. 0 when the patch is in (the real choke plays).
 /// </summary>
        public const double O2FloorUnpatched = 1.0;
        /// <summary>PlayerBreathBubbles.delay's serialized default; the live component's value is used when readable.</summary>
        public const double DefaultBubbleDelayS = 20.0;
        /// <summary>ArmsController.smoothSpeedUnderWater: the velocity smoothing rate, 1/s.</summary>
        public const double ArmsSmoothRate = 4.0;

        public static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------------------------------ state mapping

 /// <summary>The contract's `state`: out = inWater clear; fly = mode 3; surface = the atSurface flag; swim otherwise.</summary>
        public static string StateName(bool extValid, byte flags, byte mode)
        {
            if (!extValid || (flags & SwimWireFlags.InWater) == 0) return "out";
            if (mode == (byte)SwimMode.Fly) return "fly";
            if ((flags & SwimWireFlags.AtSurface) != 0) return "surface";
            return "swim";
        }

        /// <summary>
 /// Whether the arms renderers are enabled this frame. <paramref name="snShows"/>: C5 says Underwater or Surfacing
 /// (Subnautica is the visible renderer); <paramref name="firstPerson"/>: C1 cameraMode FirstPerson (SE's own
 /// first-person view is the reference; a third-person SE camera sits behind the body); <paramref name="driven"/>:
 /// the camera was driven this frame (not held by the drive gate, not the warpcam override). The contract: 1 only
 /// with inWater set and the camera under water, whatever the gate.
 /// </summary>
 /// <summary>Hysteresis of the "camera under the sea" input of <see cref="ArmsVisible"/>: under when the camera is below
 /// the ocean level + Enter, above again only past + Exit. Real run: every one of the 17 hides was
 /// SE's own latch keeps the
 /// engineer swimming there, so the arms now stay until the camera is clearly out.</summary>
        public const float CamUnderEnterM = -0.05f, CamUnderExitM = 0.35f;

        public static bool ArmsVisible(ArmsGate gate, bool ghostReady, bool extValid, byte flags, bool cameraUnder, bool snShows, bool firstPerson, bool driven)
        {
            if (!ghostReady) return false;
            if (gate == ArmsGate.ForceOff) return false;
            if (gate == ArmsGate.ForceOn) return true;
            bool inWater = extValid && (flags & SwimWireFlags.InWater) != 0;
            if (!inWater || !cameraUnder) return false;
            if (gate == ArmsGate.Camera) return true;
            return snShows && firstPerson && driven;
        }

        /// <summary>Why the arms are hidden (the status reply); null when visible.</summary>
        public static string HiddenReason(ArmsGate gate, bool ghostReady, bool extValid, byte flags, bool cameraUnder, bool snShows, bool firstPerson, bool driven)
        {
            if (!ghostReady) return "ghost not ready";
            if (gate == ArmsGate.ForceOff) return "gate off (de.arms show=off)";
            if (gate == ArmsGate.ForceOn) return null;
            if (!extValid) return "no C1 swim extension (version != 1)";
            if ((flags & SwimWireFlags.InWater) == 0) return "C1 inWater clear";
            if (!cameraUnder) return "camera above the sea";
            if (gate == ArmsGate.Camera) return null;
            if (!snShows) return "C5 view is not Underwater/Surfacing (Subnautica not the visible renderer)";
            if (!firstPerson) return "C1 cameraMode is not FirstPerson";
            if (!driven) return "camera not driven (drive gate or warpcam)";
            return null;
        }

        public static string GateName(ArmsGate g)
        {
            switch (g)
            {
                case ArmsGate.Full: return "full";
                case ArmsGate.Camera: return "camera";
                case ArmsGate.ForceOn: return "on";
                case ArmsGate.ForceOff: return "off";
                default: return "?";
            }
        }

        public static bool TryParseGate(string text, out ArmsGate g)
        {
            g = ArmsGate.Full;
            if (text == null) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "full": case "auto": case "c5": g = ArmsGate.Full; return true;
                case "camera": case "cam": g = ArmsGate.Camera; return true;
                case "on": case "force": case "true": g = ArmsGate.ForceOn; return true;
                case "off": case "false": case "none": g = ArmsGate.ForceOff; return true;
                default: return false;
            }
        }

        /// <summary>Subnautica is the visible renderer: C5 view Underwater, or Surfacing (the camera may still be under).</summary>
        public static bool SnShows(bool c5Valid, uint viewState)
        {
            return c5Valid && (viewState == ViewState.Underwater || viewState == ViewState.Surfacing);
        }

        // ------------------------------------------------------------------------------------------ lines

        public static string F3(double v) { return v.ToString("0.000", Ci); }
        public static string F1(double v) { return v.ToString("0.0", Ci); }

        /// <summary>A clip name as one token (the parser splits on spaces); "-" when there is none.</summary>
        public static string AnimToken(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return "-";
            var sb = new StringBuilder(clip.Length);
            foreach (char c in clip) sb.Append(char.IsWhiteSpace(c) || c == '=' ? '_' : c);
            return sb.ToString();
        }

        /// <summary>DE.Arms (contract line 1). Axes −127..127 → −1..1 (3 decimals), speeds byte/255, vy vyFrac/127.</summary>
        public static string ArmsLine(ulong tick, string state, bool visible, ref SwimWireState s, string anim)
        {
            return "DE.Arms tick=" + tick.ToString(Ci) + " state=" + state + " visible=" + (visible ? "1" : "0") +
                   " moving=" + (s.Has(SwimWireFlags.Moving) ? "1" : "0") +
                   " move=" + F3(SwimWire.FromAxis(s.MoveForward)) + "," + F3(SwimWire.FromAxis(s.MoveStrafe)) + "," + F3(SwimWire.FromAxis(s.MoveVertical)) +
                   " speedH=" + F3(SwimWire.FromFraction(s.SpeedH)) + " speedV=" + F3(SwimWire.FromFraction(s.SpeedV)) +
                   " vy=" + F3(SwimWire.FromAxis(s.VyFrac)) + " fins=" + SwimFins.Name(s.Fins) + " anim=" + AnimToken(anim);
        }

        /// <summary>DE.O2 (contract line 2).</summary>
        public static string O2Line(ulong tick, double mirrored, double snOxygen, double capacity, bool o2Override, bool shielded)
        {
            return "DE.O2 tick=" + tick.ToString(Ci) + " mirrored=" + F3(mirrored) + " snOxygen=" + F1(snOxygen) + " capacity=" + F1(capacity) +
                   " override=" + (o2Override ? "1" : "0") + " shielded=" + (shielded ? "1" : "0");
        }

        /// <summary>DE.Breath (contract line 3); <paramref name="ev"/> is breath|bubbles|lowO2|choke|refill.</summary>
        public static string BreathLine(ulong tick, string ev, double o2Fraction, double depthM)
        {
            return "DE.Breath tick=" + tick.ToString(Ci) + " event=" + ev + " o2=" + F3(o2Fraction) + " depth=" + F1(depthM);
        }

        /// <summary>DE.PDA (contract line 4).</summary>
        public static string PdaLine(ulong tick, long suppressed, long played)
        {
            return "DE.PDA tick=" + tick.ToString(Ci) + " suppressed=" + suppressed.ToString(Ci) + " played=" + played.ToString(Ci) + " window=" + PdaWindowS.ToString(Ci);
        }

        /// <summary>DE.SwimWire (contract line 5): the raw bytes, `version` included (0 = no extension).</summary>
        public static string SwimWireLine(ulong tick, ref SwimWireState s)
        {
            return "DE.SwimWire tick=" + tick.ToString(Ci) + " version=" + s.Version.ToString(Ci) + " flags=0x" + s.Flags.ToString("x2", Ci) +
                   " mode=" + s.Mode.ToString(Ci) + " fins=" + s.Fins.ToString(Ci) + " speedH=" + s.SpeedH.ToString(Ci) + " speedV=" + s.SpeedV.ToString(Ci) +
                   " move=" + s.MoveForward.ToString(Ci) + "," + s.MoveStrafe.ToString(Ci) + "," + s.MoveVertical.ToString(Ci) +
                   " o2=" + s.O2.ToString(Ci) + " dc=" + s.DepthClass.ToString(Ci) + " vy=" + s.VyFrac.ToString(Ci);
        }

        // ------------------------------------------------------------------------------------------ breathing numbers

 /// <summary>
 /// Subnautica's breath period by depth class
 /// CONTRACT §2.10 "breath periods". 0 = no breath (surface or unknown).
 /// </summary>
        public static double BreathPeriodS(byte depthClass)
        {
            switch (depthClass)
            {
                case (byte)SwimDepthClass.Safe: return 3.0;
                case (byte)SwimDepthClass.Unsafe: return 2.25;
                case (byte)SwimDepthClass.Crush: return 1.5;
                default: return 0.0;
            }
        }

        /// <summary>
 /// The m/s the arms' blend trees see for a full `speedH`/`vy` byte: the Dive forward steady speed with the fins
 /// setting, <c>(7.6 + fins) × 0.75</c> (5.70 / 7.125 / 8.1 m/s; CONTRACT §2.10 "Implied" and "Fins"). The byte's
 /// own reference is the last motor step's maximum (surface ×1.3, tool −1, vertical floor), which does not cross
 /// the wire; the animation needs the order of magnitude, not the exact value.
 /// </summary>
        public static double SpeedReferenceMs(byte fins)
        {
            return SwimWire.SpeedReference(SwimThresholds.DiveForwardMax + SwimFins.Modifier(fins));
        }
    }

    /// <summary>
 /// When to write the next DE.Arms line: every <see cref="S5aRules.LogPeriodMs"/>, plus one line at every change of
 /// `state` or `visible` (the contract). Unity-free.
 /// </summary>
    internal sealed class ArmsLogCadence
    {
        private ulong _lastMs;
        private bool _any;
        private string _state;
        private bool _visible;

        public long Lines, Changes;

        public bool Due(ulong nowMs, string state, bool visible)
        {
            bool changed = !_any || state != _state || visible != _visible;
            bool periodic = _any && nowMs - _lastMs >= S5aRules.LogPeriodMs;
            if (!changed && !periodic) return false;
            if (changed && _any) Changes++;
            _any = true;
            _state = state;
            _visible = visible;
            _lastMs = nowMs;
            Lines++;
            return true;
        }

        public void Reset() { _any = false; _lastMs = 0; }
    }

    /// <summary>A fixed period on the unscaled millisecond clock (DE.O2 every 0.5 s, DE.PDA every 60 s). Unity-free.</summary>
    internal sealed class PeriodicDue
    {
        private readonly ulong _periodMs;
        private ulong _next;
        public long Fired;

        public PeriodicDue(ulong periodMs) { _periodMs = periodMs; }

        /// <summary>True when the period elapsed (the first call fires at once); the next due time is now + period.</summary>
        public bool Due(ulong nowMs)
        {
            if (_next != 0 && nowMs < _next) return false;
            _next = nowMs + _periodMs;
            Fired++;
            return true;
        }

        public void Reset() { _next = 0; }
    }

    /// <summary>The OxygenManager mirror arithmetic (CONTRACT §5.4: available = o2/255 × capacity). Unity-free.</summary>
    internal static class O2Mirror
    {
        /// <summary>
 /// The units the ghost's OxygenManager must hold: <c>o2/255 × capacity</c>, never below <paramref name="floorUnits"/>
 /// (and never above the capacity). A byte of 255 (full, unreadable suit or idle controller) gives the capacity.
 /// </summary>
        public static double TargetUnits(byte o2, double capacity, double floorUnits)
        {
            if (!(capacity > 0.0)) return 0.0;
            double t = SwimWire.FromFraction(o2) * capacity;
            if (t < floorUnits) t = floorUnits;
            if (t > capacity) t = capacity;
            return t;
        }

        /// <summary>What to add (&gt; 0) or remove (&lt; 0); 0 within <see cref="S5aRules.O2Epsilon"/>.</summary>
        public static double Delta(double current, double target)
        {
            double d = target - current;
            return Math.Abs(d) < S5aRules.O2Epsilon ? 0.0 : d;
        }
    }

    /// <summary>
 /// A twin of LowOxygenAlert's monitor
 /// over the mirrored units, so the `lowO2` event is logged when the
 /// game's own alert sees its threshold crossed, and `refill` when the units are back above the threshold that
 /// fired. Thresholds are the alert list's `oxygenTriggerSeconds` (read from the live component). Unity-free.
 /// </summary>
    internal sealed class LowO2Monitor
    {
        private readonly float[] _thresholds;
        private float _prev, _curr;
        private bool _any;
        private float _active = float.NaN;

        public long LowCount, RefillCount;

        public LowO2Monitor(float[] thresholds)
        {
            _thresholds = thresholds ?? new float[0];
        }

        public float[] Thresholds { get { return _thresholds; } }
        /// <summary>The threshold currently in force (NaN when none fired since the last refill).</summary>
        public float Active { get { return _active; } }

        /// <summary>
 /// Feeds the current units. <paramref name="lowThreshold"/>: the smallest threshold crossed downward this update
 /// (NaN when none); <paramref name="refill"/>: the units rose back to or above the active threshold.
 /// </summary>
        public void Update(float units, out float lowThreshold, out bool refill)
        {
            lowThreshold = float.NaN;
            refill = false;
            if (!_any) { _prev = units; _curr = units; _any = true; }
            else { _prev = _curr; _curr = units; }
            for (int i = 0; i < _thresholds.Length; i++)
            {
                float t = _thresholds[i];
                if (_prev >= t && _curr < t && (float.IsNaN(lowThreshold) || t < lowThreshold)) lowThreshold = t;
            }
            if (!float.IsNaN(lowThreshold))
            {
                if (float.IsNaN(_active) || lowThreshold < _active) _active = lowThreshold;
                LowCount++;
            }
            else if (!float.IsNaN(_active) && _curr >= _active && _prev < _active)
            {
                refill = true;
                _active = float.NaN;
                RefillCount++;
            }
        }

        public void Reset() { _any = false; _active = float.NaN; }
    }

    /// <summary>
 /// The ghost's breath cadence. Subnautica's own breath path is off for the ghost
 /// and PlayerBreathBubbles
 /// starts its InvokeRepeating only on <c>Player.isUnderwater</c>, which stays false in LockedPiloting
 /// so this twin fires them from the C1 state: a breath every period of the C1 depth class while
 /// the `underwater` flag is set (the first one period after entering), a bubble burst every <c>delay</c> (the
 /// component's InvokeRepeating(delay, delay) rule). Unity-free.
 /// </summary>
    internal sealed class BreathScheduler
    {
        private bool _under;
        private ulong _nextBreathMs, _nextBubblesMs;

        public long Breaths, Bursts;

        public bool Underwater { get { return _under; } }

        /// <summary>One frame; <paramref name="breath"/> / <paramref name="bubbles"/> say what is due now.</summary>
        public void Update(ulong nowMs, bool underwater, byte depthClass, double bubbleDelayS, out bool breath, out bool bubbles)
        {
            breath = false;
            bubbles = false;
            double periodS = S5aRules.BreathPeriodS(depthClass);
            ulong periodMs = (ulong)Math.Max(1.0, periodS * 1000.0);
            ulong delayMs = (ulong)Math.Max(1.0, bubbleDelayS * 1000.0);
            if (!underwater)
            {
                _under = false;
                return;
            }
            if (!_under)
            {
                _under = true;
                _nextBreathMs = nowMs + periodMs;
                _nextBubblesMs = nowMs + delayMs;
                return;
            }
            // boundary-based like Subnautica's timeMonitor.DidChangeInterval: no drift by the frame
 // length; one breath per frame however late the frame (no catch-up burst)
            if (periodS > 0.0 && nowMs >= _nextBreathMs)
            {
                breath = true;
                Breaths++;
                do _nextBreathMs += periodMs; while (_nextBreathMs <= nowMs);
            }
            if (nowMs >= _nextBubblesMs)
            {
                bubbles = true;
                Bursts++;
                do _nextBubblesMs += delayMs; while (_nextBubblesMs <= nowMs);
            }
        }

        public void Reset() { _under = false; }
    }

    /// <summary>
 /// The body-relative velocity the arms' blend trees receive
 /// rebuilt from the C1 extension: the direction from the move axes (strafe → x right,
 /// forward → z; the last direction is kept while the input is released and the body glides), the magnitude from
 /// `speedH` × the fins' steady speed, the vertical part from `vyFrac`. Unity-free.
 /// </summary>
    internal sealed class ArmsMotion
    {
        private double _dirX = 0.0, _dirZ = 1.0;
        /// <summary>Smoothed body-relative velocity, m/s (x right, y up, z forward).</summary>
        public double Vx, Vy, Vz;

        public double Speed { get { return Math.Sqrt(Vx * Vx + Vy * Vy + Vz * Vz); } }

        /// <summary>The target (unsmoothed) velocity of one extension state.</summary>
        public static void Target(ref SwimWireState s, ref double dirX, ref double dirZ, out double vx, out double vy, out double vz)
        {
            double f = SwimWire.FromAxis(s.MoveForward), st = SwimWire.FromAxis(s.MoveStrafe);
            double n = Math.Sqrt(f * f + st * st);
            if (n > 1e-3) { dirX = st / n; dirZ = f / n; }
            double vref = S5aRules.SpeedReferenceMs(s.Fins);
            double vh = SwimWire.FromFraction(s.SpeedH) * vref;
            vx = dirX * vh;
            vz = dirZ * vh;
            vy = SwimWire.FromAxis(s.VyFrac) * vref;
        }

        public void Update(double dt, ref SwimWireState s)
        {
            double tx, ty, tz;
            Target(ref s, ref _dirX, ref _dirZ, out tx, out ty, out tz);
            double k = dt <= 0.0 ? 1.0 : Math.Min(1.0, S5aRules.ArmsSmoothRate * dt);
            Vx += (tx - Vx) * k;
            Vy += (ty - Vy) * k;
            Vz += (tz - Vz) * k;
        }

        public void Reset() { Vx = Vy = Vz = 0.0; _dirX = 0.0; _dirZ = 1.0; }

        /// <summary>
 /// S13 hook (point 5): the same smoothing toward a given body-relative target (the C1 S13 extension's look-frame
 /// velocity, Subnautica's own ArmsController rule) instead of the input axes; the input direction is kept for a later
 /// fallback to <see cref="Update"/>.
 /// </summary>
        public void UpdateTarget(double dt, double tx, double ty, double tz)
        {
            double n = Math.Sqrt(tx * tx + tz * tz);
            if (n > 1e-3) { _dirX = tx / n; _dirZ = tz / n; }
            double k = dt <= 0.0 ? 1.0 : Math.Min(1.0, S5aRules.ArmsSmoothRate * dt);
            Vx += (tx - Vx) * k;
            Vy += (ty - Vy) * k;
            Vz += (tz - Vz) * k;
        }
    }

    /// <summary>The S5a switches (environment, read once at bridge start).</summary>
    internal static class S5aSwitches
    {
        /// <summary><c>off</c>: the S5a host is not created (no arms, no oxygen mirror, no death-path patch).</summary>
        public const string Var = "DEEPENGINEERS_SN_S5A";
        /// <summary><c>on</c>: the scuba mask overlay is shown with the arms
 /// character don't have masks"; the UiHider's MaskHUD phase hides the game's own overlay anyway).</summary>
        public const string MaskVar = "DEEPENGINEERS_SN_S5A_MASK";

        public static bool Enabled { get { return !S1Switches.IsOff(S1Switches.Reader(Var)); } }
        public static bool Mask { get { return S1Switches.IsOn(S1Switches.Reader(MaskVar)); } }

        public static string Describe()
        {
            return Var + "=" + (Enabled ? "on" : "off") + " " + MaskVar + "=" + (Mask ? "on" : "off");
        }
    }
}
