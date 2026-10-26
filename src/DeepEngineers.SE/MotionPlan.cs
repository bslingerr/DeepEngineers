using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The camera motion of the S2 dev command <c>de.motion</c>: one SN point per SE update (60 Hz), like S1's <c>de.path</c>, so the S2.N2 hysteresis traces and the
 /// S2.N7 (b) dive are continuous camera moves rather than teleports (a teleport re-arms <c>streamingSettled</c> and
 /// forces Surfacing, which makes those checks non-discriminating; tools-s2 proposal, the stand-in SE implements the
 /// same command).
 /// <list type="bullet">
 /// <item><c>{kind: "bob", x, z, centreY, amplitudeM, hz, seconds}</c>: <c>y(t) = centreY + amplitudeM·sin(2π·hz·t)</c>
 /// at the fixed SN point (x, z), t = k / 60 s, for <c>seconds</c> (S2.N2 traces (a) and (b)).</item>
 /// <item><c>{kind: "line", fromSn: [x, y, z], toSn: [x, y, z], speedMps}</c>: a straight line at a constant speed,
 /// k / n of the way at update k, n = round(|to − from| / speed · 60) (S2.N7 (b) 5 m/s dive, S2.N11 sailing).</item>
 /// <item><c>{kind: "stop"}</c>: ends a running motion.</item>
 /// </list>
 /// The heading and pitch of the camera when the command arrives are kept (SN directions). The plugin arms no teleport
 /// flag: a first sample more than 20 m from the camera is flagged by the frame composer's own jump detection (D63).
 /// </summary>
    internal sealed class MotionPlan
    {
        public const double TicksPerSecond = 60.0;
        public const int MaxTicks = 60 * 60 * 30;   // 30 minutes
        public const double MaxSpeedMps = 1000.0;

        public string Kind { get; private set; }
        public string Label { get; private set; }
        /// <summary>Number of updates after the first sample (the last sample is at k = Ticks).</summary>
        public int Ticks { get; private set; }

        private Vec3 _from, _to;
        private double _x, _z, _centreY, _amp, _hz;

        /// <summary>The SN point of update <paramref name="k"/> (clamped to 0..Ticks).</summary>
        public Vec3 PointAt(int k)
        {
            if (k < 0) k = 0;
            if (k > Ticks) k = Ticks;
            if (Kind == "line")
            {
                double a = Ticks == 0 ? 1.0 : k / (double)Ticks;
                return _from + (_to - _from) * a;
            }
            double t = k / TicksPerSecond;
            return new Vec3(_x, _centreY + _amp * Math.Sin(2.0 * Math.PI * _hz * t), _z);
        }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            if (Kind == "line")
                return "line from " + V(_from) + " to " + V(_to) + " over " + Ticks + " updates (" +
                       ((_to - _from).Length / (Ticks / TicksPerSecond)).ToString("0.###", ci) + " m/s)";
            return "bob at SN (" + _x.ToString("0.###", ci) + ", " + _z.ToString("0.###", ci) + ") y = " + _centreY.ToString("0.###", ci) +
                   " +- " + _amp.ToString("0.###", ci) + " m at " + _hz.ToString("0.###", ci) + " Hz for " + Ticks + " updates";
        }

        /// <summary>
 /// Parses the command arguments. <paramref name="current"/> is the camera's SN point (the default <c>fromSn</c>).
 /// Returns null with <paramref name="error"/> set when the arguments are refused; <paramref name="stop"/> for
 /// <c>kind: "stop"</c>.
 /// </summary>
        public static MotionPlan Parse(IDictionary<string, object> args, Vec3 current, out bool stop, out string error)
        {
            stop = false;
            error = null;
            string kind = MiniJson.GetString(args, "kind");
            string label = MiniJson.GetString(args, "label") ?? kind;
            if (kind == "stop") { stop = true; return null; }
            if (kind == "bob")
            {
                double x = MiniJson.GetNumber(args, "x") ?? 0.0, z = MiniJson.GetNumber(args, "z") ?? 0.0;
                double c = MiniJson.GetNumber(args, "centreY") ?? -0.125;
                double amp = MiniJson.GetNumber(args, "amplitudeM") ?? 0.05;
                double hz = MiniJson.GetNumber(args, "hz") ?? 1.0;
                double secs = MiniJson.GetNumber(args, "seconds") ?? 30.0;
                if (!Finite(x, z, c, amp, hz, secs) || amp < 0 || amp > 1000 || hz < 0 || hz > 30 || secs <= 0 || secs * TicksPerSecond > MaxTicks)
                {
                    error = "de.motion bob: x, z, centreY finite; amplitudeM 0..1000; hz 0..30; seconds > 0 and at most " + (MaxTicks / TicksPerSecond) + " s";
                    return null;
                }
                return new MotionPlan
                {
                    Kind = "bob", Label = label, Ticks = (int)Math.Round(secs * TicksPerSecond), _x = x, _z = z, _centreY = c, _amp = amp, _hz = hz,
                };
            }
            if (kind == "line")
            {
                Vec3 from, to;
                bool hasFrom = TryVec(args, "fromSn", out from);
                if (!hasFrom) from = current;
                if (!TryVec(args, "toSn", out to))
                {
                    error = "de.motion line needs \"toSn\": [x, y, z] (Subnautica metres), optional \"fromSn\" (default: the camera) and \"speedMps\"";
                    return null;
                }
                double speed = MiniJson.GetNumber(args, "speedMps") ?? 5.0;
                if (!Finite(speed) || speed <= 0 || speed > MaxSpeedMps)
                {
                    error = "de.motion line: speedMps must be in (0, " + MaxSpeedMps + "]";
                    return null;
                }
                double ticks = Math.Round((to - from).Length / speed * TicksPerSecond);
                if (ticks > MaxTicks) { error = "de.motion line: longer than " + (MaxTicks / TicksPerSecond) + " s"; return null; }
                return new MotionPlan { Kind = "line", Label = label, Ticks = Math.Max(1, (int)ticks), _from = from, _to = to };
            }
            error = "de.motion needs \"kind\": \"bob\" {x, z, centreY, amplitudeM, hz, seconds} | \"line\" {fromSn, toSn, speedMps} | \"stop\"";
            return null;
        }

        private static bool TryVec(IDictionary<string, object> args, string key, out Vec3 v)
        {
            v = default(Vec3);
            object o;
            if (args == null || !args.TryGetValue(key, out o) || o == null) return false;
            var list = o as IList<object>;
            double[] d = null;
            if (list != null && list.Count == 3)
            {
                d = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (list[i] is double) d[i] = (double)list[i];
                    else if (!(list[i] is string) || !double.TryParse((string)list[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d[i])) return false;
                }
            }
            else if (o is string)
            {
                string[] p = ((string)o).Trim('[', ']', ' ').Split(',');
                if (p.Length != 3) return false;
                d = new double[3];
                for (int i = 0; i < 3; i++)
                    if (!double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d[i])) return false;
            }
            if (d == null || !Finite(d[0], d[1], d[2])) return false;
            v = new Vec3(d[0], d[1], d[2]);
            return true;
        }

        private static bool Finite(params double[] v)
        {
            foreach (double x in v) if (double.IsNaN(x) || double.IsInfinity(x)) return false;
            return true;
        }

        private static string V(Vec3 v)
        {
            var ci = CultureInfo.InvariantCulture;
            return "(" + v.X.ToString("0.###", ci) + ", " + v.Y.ToString("0.###", ci) + ", " + v.Z.ToString("0.###", ci) + ")";
        }
    }
}
