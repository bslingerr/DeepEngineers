using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>One tick of swim input: forward/backward, strafe, vertical in [−1, 1], sprint.</summary>
    internal struct SwimInputs
    {
        public double Forward, Strafe, Vertical;
        public bool Sprint;

        public bool Any { get { return Forward != 0 || Strafe != 0 || Vertical != 0 || Sprint; } }

        public override string ToString()
        {
            return "(" + SwimText.N(Forward) + "," + SwimText.N(Strafe) + "," + SwimText.N(Vertical) + "," + (Sprint ? "1" : "0") + ")";
        }
    }

    /// <summary>
 /// SE input ↔ swim input (game-independent). SE builds the character's move indicator in
 /// <c>VRage.Input.MyInputExtensions.GetPositionDelta</c>:
 /// <c>X = STRAFE_RIGHT − STRAFE_LEFT</c>, <c>Y = JUMP − CROUCH</c>, <c>Z = BACKWARD − FORWARD</c>
 /// the gameplay screen
 /// hands it to <c>MyCharacter.MoveAndRotate</c>, which stores it in the public
 /// <c>MoveIndicator</c>; sprint is <c>WantsSprint</c>
 /// Subnautica's move
 /// direction is (x strafe, y vertical, z forward).
 /// </summary>
    internal static class SwimInputMap
    {
        /// <summary>SE move indicator (X strafe right, Y up, Z backward) and sprint → swim input (clamped to [−1, 1]).</summary>
        public static SwimInputs FromMoveIndicator(double x, double y, double z, bool sprint)
        {
            return new SwimInputs { Forward = Clamp1(-z), Strafe = Clamp1(x), Vertical = Clamp1(y), Sprint = sprint };
        }

        /// <summary>Swim input → SE move indicator (the scripted source driving SE's own walk or jetpack out of the water).</summary>
        public static void ToMoveIndicator(SwimInputs i, out double x, out double y, out double z)
        {
            x = Clamp1(i.Strafe);
            y = Clamp1(i.Vertical);
            z = -Clamp1(i.Forward);
            if (z == 0) z = 0; // no negative zero in the logs
        }

        /// <summary>
 /// A scripted look in SN degrees: yaw 0 = SN north (+Z), 90 = east (+X), clockwise seen from above (as
 /// <c>de.teleport</c>); pitch + = up. Forward and right are SN unit vectors; right stays horizontal (no roll).
 /// </summary>
        public static void LookFromYawPitch(double yawDeg, double pitchDeg, out Vec3 forward, out Vec3 right)
        {
            double y = yawDeg * (Math.PI / 180.0);
            double p = Math.Max(-90.0, Math.Min(90.0, pitchDeg)) * (Math.PI / 180.0);
            double cp = Math.Cos(p);
            forward = new Vec3(Math.Sin(y) * cp, Math.Sin(p), Math.Cos(y) * cp);
            right = new Vec3(Math.Cos(y), 0.0, -Math.Sin(y));
        }

        public static double Clamp1(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
            return v < -1.0 ? -1.0 : v > 1.0 ? 1.0 : v;
        }
    }

    /// <summary>One step of a scripted input source.</summary>
    internal struct SwimScriptStep
    {
        public double Seconds;
        public int Ticks;
        public SwimInputs Input;
        public double LookYawDeg, LookPitchDeg;
        /// <summary>−1 no request, 0 switch the jetpack off, 1 switch it on (as a press of SE's jetpack key would).</summary>
        public int Jetpack;
        public string Label;
    }

    /// <summary>
 /// The scripted input source of <c>de.swim {script: …}</c> (ROADMAP S2b.N1–N4 in SE alone: no human at the keyboard).
 /// One tick per SE simulation step (1/60 s); step k lasts <c>max(1, round(seconds × 60))</c> ticks. Forms accepted:
 /// <list type="bullet">
 /// <item>a JSON list of objects <c>{seconds, forward, strafe, vertical, sprint, lookYawDeg, lookPitchDeg, jetpack,
 /// label}</c> (missing inputs 0, a missing look keeps the previous step's, the first defaults to yaw 0 pitch 0;
 /// <c>jetpack</c> true/false is a one-shot request at the step start);</item>
 /// <item>one such object;</item>
 /// <item>a compact string for <c>key=value</c> tools and the chat: steps separated by <c>;</c> or <c>|</c>, each
 /// <c>seconds,forward,strafe,vertical,sprint,yaw,pitch[,jetpack]</c> (missing fields 0, jetpack −1/0/1);</item>
 /// <item><c>"stop"</c> (or <c>off</c>, <c>cancel</c>): stops a running script.</item>
 /// </list>
 /// Game-independent.
 /// </summary>
    internal sealed class SwimScript
    {
        public const int TicksPerSecond = 60;
        public const int MaxSteps = 1000;
        public const double MaxStepSeconds = 3600.0;
        public const double MaxTotalSeconds = 3600.0;

        public readonly SwimScriptStep[] Steps;
        public readonly int TotalTicks;

        public int StepIndex { get; private set; }
        public int TickInStep { get; private set; }
        public int Elapsed { get; private set; }

        private SwimScript(SwimScriptStep[] steps)
        {
            Steps = steps;
            int total = 0;
            foreach (SwimScriptStep s in steps) total += s.Ticks;
            TotalTicks = total;
        }

        public bool Done { get { return StepIndex >= Steps.Length; } }

        /// <summary>The step of the current tick (the last step once done).</summary>
        public SwimScriptStep Current { get { return Steps[Math.Min(StepIndex, Steps.Length - 1)]; } }

        /// <summary>True at the first tick of a step.</summary>
        public bool AtStepStart { get { return !Done && TickInStep == 0; } }

        /// <summary>Consumes the current tick.</summary>
        public void Advance()
        {
            if (Done) return;
            Elapsed++;
            TickInStep++;
            if (TickInStep >= Steps[StepIndex].Ticks)
            {
                StepIndex++;
                TickInStep = 0;
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Steps.Length).Append(" steps, ").Append(TotalTicks).Append(" ticks (")
              .Append(SwimText.N(TotalTicks / (double)TicksPerSecond)).Append(" s)");
            return sb.ToString();
        }

        public static string StepText(SwimScriptStep s)
        {
            return SwimText.N(s.Seconds) + " s in=" + s.Input + " look=(" + SwimText.N(s.LookYawDeg) + "," + SwimText.N(s.LookPitchDeg) + ")" +
                   (s.Jetpack >= 0 ? " jetpack=" + (s.Jetpack == 1 ? "on" : "off") : "") +
                   (string.IsNullOrEmpty(s.Label) ? "" : " label=" + s.Label);
        }

        /// <summary>Parses a script; null with <paramref name="error"/> set (or <paramref name="stop"/> true) otherwise.</summary>
        public static SwimScript Parse(object value, out bool stop, out string error)
        {
            stop = false;
            error = null;
            var steps = new List<SwimScriptStep>();
            double yaw = 0, pitch = 0;
            string text = value as string;
            if (text != null)
            {
                string t = text.Trim();
                if (t.Equals("stop", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    stop = true;
                    return null;
                }
                string[] parts = t.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < parts.Length; k++)
                {
                    SwimScriptStep s;
                    if (!ParseCompact(parts[k], k, ref yaw, ref pitch, out s, out error)) return null;
                    steps.Add(s);
                }
            }
            else if (value is IDictionary<string, object>)
            {
                SwimScriptStep s;
                if (!ParseObject((IDictionary<string, object>)value, 0, ref yaw, ref pitch, out s, out error)) return null;
                steps.Add(s);
            }
            else if (value is IList<object>)
            {
                var list = (IList<object>)value;
                bool allNumbers = list.Count > 0;
                foreach (object x in list) if (!(x is double)) { allNumbers = false; break; }
                if (allNumbers)
                {
                    // one compact step that a key=value tool turned into a number list (se_cmd: "script=10,1,0,0")
                    var parts = new string[list.Count];
                    for (int i = 0; i < list.Count; i++) parts[i] = ((double)list[i]).ToString("R", CultureInfo.InvariantCulture);
                    SwimScriptStep one;
                    if (!ParseCompact(string.Join(",", parts), 0, ref yaw, ref pitch, out one, out error)) return null;
                    return new SwimScript(new[] { one });
                }
                for (int k = 0; k < list.Count; k++)
                {
                    var o = list[k] as IDictionary<string, object>;
                    if (o == null) { error = "script step " + (k + 1) + " is not an object"; return null; }
                    SwimScriptStep s;
                    if (!ParseObject(o, k, ref yaw, ref pitch, out s, out error)) return null;
                    steps.Add(s);
                }
            }
            else
            {
                error = "script must be a list of steps {seconds, forward, strafe, vertical, sprint, lookYawDeg, lookPitchDeg, jetpack}, " +
                        "a compact string 'seconds,forward,strafe,vertical,sprint,yaw,pitch[,jetpack];...', or \"stop\"";
                return null;
            }
            if (steps.Count == 0) { error = "script has no steps"; return null; }
            if (steps.Count > MaxSteps) { error = "script has " + steps.Count + " steps (max " + MaxSteps + ")"; return null; }
            double total = 0;
            foreach (SwimScriptStep s in steps) total += s.Ticks / (double)TicksPerSecond;
            if (total > MaxTotalSeconds) { error = "script lasts " + SwimText.N(total) + " s (max " + MaxTotalSeconds + ")"; return null; }
            return new SwimScript(steps.ToArray());
        }

        private static bool ParseObject(IDictionary<string, object> o, int k, ref double yaw, ref double pitch, out SwimScriptStep s, out string error)
        {
            s = default(SwimScriptStep);
            error = null;
            double? seconds = MiniJson.GetNumber(o, "seconds");
            if (!seconds.HasValue) { error = "script step " + (k + 1) + ": \"seconds\" is required"; return false; }
            double f = MiniJson.GetNumber(o, "forward") ?? 0, st = MiniJson.GetNumber(o, "strafe") ?? 0, v = MiniJson.GetNumber(o, "vertical") ?? 0;
            bool sprint = MiniJson.GetBool(o, "sprint") ?? false;
            double? y = MiniJson.GetNumber(o, "lookYawDeg"), p = MiniJson.GetNumber(o, "lookPitchDeg");
            if (y.HasValue) yaw = y.Value;
            if (p.HasValue) pitch = p.Value;
            int jet = -1;
            if (o.ContainsKey("jetpack"))
            {
                bool? j = MiniJson.GetBool(o, "jetpack");
                if (!j.HasValue) { error = "script step " + (k + 1) + ": \"jetpack\" must be true or false"; return false; }
                jet = j.Value ? 1 : 0;
            }
            return Make(k, seconds.Value, f, st, v, sprint, yaw, pitch, jet, MiniJson.GetString(o, "label"), out s, out error);
        }

        private static bool ParseCompact(string text, int k, ref double yaw, ref double pitch, out SwimScriptStep s, out string error)
        {
            s = default(SwimScriptStep);
            error = null;
            string[] f = text.Split(new[] { ',', ':' }, StringSplitOptions.None);
            var n = new double[8];
            var present = new bool[8];
            n[7] = -1;
            if (f.Length < 1 || f.Length > 8) { error = "script step " + (k + 1) + " '" + text + "': 1 to 8 numbers expected"; return false; }
            for (int i = 0; i < f.Length; i++)
            {
                string t = f[i].Trim();
                if (t.Length == 0) continue; // empty field: 0, the previous look (yaw, pitch), or no jetpack request
                double d;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                {
                    error = "script step " + (k + 1) + " '" + text + "': '" + t + "' is not a number";
                    return false;
                }
                n[i] = d;
                present[i] = true;
            }
            if (!present[0]) { error = "script step " + (k + 1) + " '" + text + "': seconds missing"; return false; }
            if (present[5]) yaw = n[5];
            if (present[6]) pitch = n[6];
            int jet = n[7] > 0.5 ? 1 : n[7] > -0.5 ? 0 : -1;
            return Make(k, n[0], n[1], n[2], n[3], n[4] > 0.5, yaw, pitch, jet, null, out s, out error);
        }

        private static bool Make(int k, double seconds, double f, double st, double v, bool sprint, double yaw, double pitch, int jet, string label,
                                 out SwimScriptStep s, out string error)
        {
            s = default(SwimScriptStep);
            error = null;
            string at = "script step " + (k + 1) + ": ";
            if (double.IsNaN(seconds) || seconds <= 0 || seconds > MaxStepSeconds) { error = at + "seconds must be in (0, " + MaxStepSeconds + "]"; return false; }
            if (!InUnit(f) || !InUnit(st) || !InUnit(v)) { error = at + "forward, strafe and vertical must be in [-1, 1]"; return false; }
            if (double.IsNaN(yaw) || double.IsInfinity(yaw)) { error = at + "lookYawDeg must be a number"; return false; }
            if (double.IsNaN(pitch) || pitch < -90 || pitch > 90) { error = at + "lookPitchDeg must be in [-90, 90]"; return false; }
            s.Seconds = seconds;
            s.Ticks = Math.Max(1, (int)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero));
            s.Input = new SwimInputs { Forward = f, Strafe = st, Vertical = v, Sprint = sprint };
            s.LookYawDeg = yaw;
            s.LookPitchDeg = pitch;
            s.Jetpack = jet;
            s.Label = label;
            return true;
        }

        private static bool InUnit(double x) { return !double.IsNaN(x) && x >= -1.0 && x <= 1.0; }
    }

    /// <summary>Number text for the S2b logs.</summary>
    internal static class SwimText
    {
        public static string N(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "nan";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string V(Vec3 v)
        {
            return "(" + N(v.X) + "," + N(v.Y) + "," + N(v.Z) + ")";
        }

        /// <summary>Round-trip double text for the replayed fields of the swim rows.</summary>
        public static string G(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("G17", CultureInfo.InvariantCulture);
        }

        /// <summary>Three fixed-point numbers (6 decimals) as a JSON array, without allocating.</summary>
        public static void Arr(StringBuilder sb, Vec3 v)
        {
            sb.Append('[');
            AppendFixed(sb, v.X, 6);
            sb.Append(',');
            AppendFixed(sb, v.Y, 6);
            sb.Append(',');
            AppendFixed(sb, v.Z, 6);
            sb.Append(']');
        }

        private static readonly long[] Pow10 =
        {
            1L, 10L, 100L, 1000L, 10000L, 100000L, 1000000L, 10000000L, 100000000L, 1000000000L,
        };

        /// <summary>
 /// A fixed-point decimal (half away from zero) appended digit by digit (no string; <c>StringBuilder.Append(long)</c>
 /// allocates on.NET Framework); <c>null</c> for NaN, infinities and values too large for the decimals.
 /// </summary>
        public static void AppendFixed(StringBuilder sb, double v, int decimals)
        {
            if (decimals < 0) decimals = 0;
            if (decimals >= Pow10.Length) decimals = Pow10.Length - 1;
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            double scaled = Math.Round(Math.Abs(v) * Pow10[decimals], MidpointRounding.AwayFromZero);
            if (scaled >= 9.0e18) { sb.Append("null"); return; }
            long q = (long)scaled;
            if (v < 0 && q != 0) sb.Append('-');
            AppendDigits(sb, q / Pow10[decimals], 1);
            if (decimals == 0) return;
            sb.Append('.');
            AppendDigits(sb, q % Pow10[decimals], decimals);
        }

        /// <summary>An integer appended digit by digit.</summary>
        public static void AppendInt(StringBuilder sb, long v)
        {
            if (v < 0)
            {
                sb.Append('-');
                if (v == long.MinValue) { sb.Append("9223372036854775808"); return; }
                v = -v;
            }
            AppendDigits(sb, v, 1);
        }

        /// <summary>A UTC <c>DateTime.Ticks</c> value as the round-trip "o" text (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>), no allocation.</summary>
        public static void AppendUtc(StringBuilder sb, long utcTicks)
        {
            var t = new DateTime(utcTicks, DateTimeKind.Utc);
            AppendDigits(sb, t.Year, 4);
            sb.Append('-');
            AppendDigits(sb, t.Month, 2);
            sb.Append('-');
            AppendDigits(sb, t.Day, 2);
            sb.Append('T');
            AppendDigits(sb, t.Hour, 2);
            sb.Append(':');
            AppendDigits(sb, t.Minute, 2);
            sb.Append(':');
            AppendDigits(sb, t.Second, 2);
            sb.Append('.');
            AppendDigits(sb, utcTicks % TimeSpan.TicksPerSecond, 7);
            sb.Append('Z');
        }

        /// <summary>A non-negative integer with at least <paramref name="minDigits"/> digits (zero-padded).</summary>
        private static void AppendDigits(StringBuilder sb, long v, int minDigits)
        {
            int len = 1;
            for (long t = v / 10; t != 0; t /= 10) len++;
            for (int k = len; k < minDigits; k++) sb.Append('0');
            long div = 1;
            for (int k = 1; k < len; k++) div *= 10;
            for (; div > 0; div /= 10) sb.Append((char)('0' + (int)(v / div % 10)));
        }
    }
}
