using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S7t T5 look match, Subnautica side: the pure rules (Unity-free; tests/DeepEngineers.SN.Tests/S7tLookMatchSnTests.cs).
 // ROADMAP "### S7t Seamless transition" T5; CONTRACT §3.4, §1.1, §5.4 "S7t look match".
 //
 // Subnautica's palette is the reference: its sky, fog, water and colour grade are not touched. Only the sun's shadow
 // strength and the ambient move a little toward SE's harder light, where SE's own softening (SE side: sun ×0.85,
 // ambient ×1.8) cannot meet it alone. The weight is 0 at and below SnLowY above the sea (the surface, the dive and
 // everything under water keep Subnautica's own values), smoothstep to 1 at SnHighY and above (the band is higher:
 // +1,000 m by default since S7t T3), and 0 whenever the picture is off (no world, SE not alive, C5 stale, not the
 // Site policy). The effective weight eases at most 1/EaseS per second. Each member is captured before the first
 // write, followed while it holds (a change by the game is taken as its new own value), and written back on leave,
 // on SE's loss and at quit; nothing is persisted (the host save is never saved).
 // =================================================================================================================

    internal enum SnLookKind { Factor, Value }

    internal sealed class SnLookKey
    {
        public readonly string Name, Member;
        public readonly SnLookKind Kind;
        public readonly double Default; // NaN = unset
        public readonly double Min, Max;
        /// <summary>The value written is clamped to this range (Light.shadowStrength is 0..1).</summary>
        public readonly double ClampMin, ClampMax;
        public int Index;

        public SnLookKey(string name, string member, SnLookKind kind, double dflt, double min, double max, double clampMin, double clampMax)
        {
            Name = name; Member = member; Kind = kind; Default = dflt; Min = min; Max = max; ClampMin = clampMin; ClampMax = clampMax;
        }
    }

    internal struct SnLookGateInputs
    {
        public bool Enabled;
        /// <summary>S1's world is up (the ghost player exists).</summary>
        public bool InWorld;
        /// <summary>The SE peer is Alive.</summary>
        public bool PeerAlive;
        /// <summary>A C5 was read within <see cref="SnLookRules.C5FreshMs"/>.</summary>
        public bool C5Fresh;
        /// <summary>C5 flag bit 9 <c>pictureSite</c>.</summary>
        public bool PictureSite;
        /// <summary>The camera's altitude above the sea (Subnautica y).</summary>
        public double Y;
        public double LowY, HighY;
        /// <summary>A forced weight 0..1, NaN = automatic.</summary>
        public double Force;
    }

    internal static class SnLookRules
    {
        public const string Cmd = "de.s7t";
        public const string Verb = "look";
        public const string EnvVar = "DEEPENGINEERS_SN_LOOK";
        public const double DefaultLowY = 20.0, DefaultHighY = 300.0, DefaultEaseS = 0.8;
        public const ulong C5FreshMs = 1500;
        public const ulong RowEveryMs = 2000;
        public const int EdgeRowsPerSecond = 4;
        public const string Usage = "/de sn s7t look [status|on|off|reset] [shadow=0..1] [ambient=f] [sun=f] [exposure=f] [key=unset|default] [force=0..1|auto] [low=m] [high=m] [ease=s]";

        public static readonly SnLookKey[] Keys = Index(new[]
        {
            // 6, ambient x1.5
            new SnLookKey("shadow", "UnityEngine.Light.shadowStrength (uSkyManager.main.SunLight)", SnLookKind.Value, 0.6, 0, 1, 0, 1),
            new SnLookKey("ambient", "uSky.uSkyLight.ambientLight", SnLookKind.Factor, 1.5, 0, 4, 0, 10),
            new SnLookKey("sun", "uSky.uSkyLight.SunIntensity", SnLookKind.Factor, double.NaN, 0, 4, 0, 4),
            new SnLookKey("exposure", "uSkyManager.Exposure", SnLookKind.Factor, double.NaN, 0.1, 4, 0.01, 10),
        });

        private static SnLookKey[] Index(SnLookKey[] k)
        {
            for (int i = 0; i < k.Length; i++) k[i].Index = i;
            return k;
        }

        public static SnLookKey Find(string name)
        {
            foreach (SnLookKey k in Keys) if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        /// <summary>0 at and below <paramref name="low"/>, smoothstep to 1 at <paramref name="high"/>, 1 above; 0 for NaN.</summary>
        public static double AltitudeWeight(double y, double low, double high)
        {
            if (double.IsNaN(y) || double.IsInfinity(y)) return 0.0;
            if (y <= low) return 0.0;
            if (!(high > low) || y >= high) return 1.0;
            double t = (y - low) / (high - low);
            return t * t * (3.0 - 2.0 * t);
        }

        public static double Target(ref SnLookGateInputs i, out string why)
        {
            if (!i.Enabled) { why = "off"; return 0.0; }
            if (!i.InWorld) { why = "noWorld"; return 0.0; }
            if (!double.IsNaN(i.Force)) { why = "force"; return i.Force < 0 ? 0.0 : i.Force > 1 ? 1.0 : i.Force; }
            if (!i.PeerAlive) { why = "peer"; return 0.0; }
            if (!i.C5Fresh) { why = "c5"; return 0.0; }
            if (!i.PictureSite) { why = "policy"; return 0.0; }
            if (double.IsNaN(i.Y) || double.IsInfinity(i.Y)) { why = "y"; return 0.0; }
            if (i.Y < 0.0) { why = "underwater"; return 0.0; }
            double w = AltitudeWeight(i.Y, i.LowY, i.HighY);
            why = w >= 1.0 ? "high" : w <= 0.0 ? "surface" : "ramp";
            return w;
        }

        public static double Ease(double current, double target, double dtS, double easeS)
        {
            if (double.IsNaN(current)) current = 0.0;
            if (!(easeS > 0.0) || double.IsNaN(dtS)) return target;
            double step = Math.Max(0.0, dtS) / easeS;
            double d = target - current;
            if (Math.Abs(d) <= step) return target;
            return current + (d > 0 ? step : -step);
        }

        /// <summary>The value at weight w from the captured own value (unset key or w ≤ 0: own), clamped to the member's range.</summary>
        public static double Blend(SnLookKey k, double target, double own, double w)
        {
            if (k == null || double.IsNaN(target) || !(w > 0.0)) return own;
            if (w > 1.0) w = 1.0;
            double v = k.Kind == SnLookKind.Factor ? own * (1.0 + (target - 1.0) * w) : own + (target - own) * w;
            if (double.IsNaN(v) || double.IsInfinity(v)) return own;
            return v < k.ClampMin ? k.ClampMin : v > k.ClampMax ? k.ClampMax : v;
        }

        /// <summary>
 /// <c>de.s7t {look: "k=v …" | {k: v} | [tokens]}</c> or the chat relay <c>{argv: ["look", …]}</c> (SE's
 /// <c>/de sn s7t look …</c>); false for the other S7t keys.
 /// </summary>
        public static bool IsMine(object args, out List<string> tokens)
        {
            tokens = null;
            var d = args as IDictionary<string, object>;
            var top = args as System.Collections.IList;
            object v;
            if (d != null && d.TryGetValue(Verb, out v))
            {
                tokens = new List<string>();
                var dv = v as IDictionary<string, object>;
                var lv = v as System.Collections.IList;
                if (dv != null) foreach (var kv in dv) tokens.Add(kv.Key + "=" + Text(kv.Value));
                else if (lv != null) foreach (object o in lv) tokens.AddRange(Split(Text(o)));
                else if (v is bool) tokens.Add((bool)v ? "on" : "off");
                else tokens.AddRange(Split(Text(v)));
                return true;
            }
            System.Collections.IList words = null;
            if (d != null && d.TryGetValue("argv", out v)) words = v as System.Collections.IList;
            else if (top != null) words = top;
            if (words == null || words.Count == 0 || !string.Equals(Text(words[0]), Verb, StringComparison.OrdinalIgnoreCase)) return false;
            tokens = new List<string>();
            for (int i = 1; i < words.Count; i++) tokens.AddRange(Split(Text(words[i])));
            return true;
        }

        public static List<string> Split(string s)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(s)) return r;
            foreach (string p in s.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)) r.Add(p.Trim());
            return r;
        }

        public static string Text(object o)
        {
            if (o == null) return "";
            if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is bool) return (bool)o ? "on" : "off";
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static string Num(double v) { return double.IsNaN(v) ? "auto" : v.ToString("0.####", CultureInfo.InvariantCulture); }
    }

    /// <summary>The live Subnautica match set (<c>de.s7t look</c>). Main thread only.</summary>
    internal sealed class SnLookTable
    {
        private readonly double[] _t = new double[SnLookRules.Keys.Length];
        public bool Enabled = true;
        public double Force = double.NaN, LowY = SnLookRules.DefaultLowY, HighY = SnLookRules.DefaultHighY, EaseS = SnLookRules.DefaultEaseS;
        public long Changes;

        public SnLookTable() { Reset(); }

        public void Reset()
        {
            foreach (SnLookKey k in SnLookRules.Keys) _t[k.Index] = k.Default;
            Enabled = true;
            Force = double.NaN;
            LowY = SnLookRules.DefaultLowY;
            HighY = SnLookRules.DefaultHighY;
            EaseS = SnLookRules.DefaultEaseS;
        }

        public double Get(SnLookKey k) { return k != null ? _t[k.Index] : double.NaN; }

        public List<string> Apply(IList<string> tokens, List<string> applied)
        {
            var problems = new List<string>();
            if (tokens == null) return problems;
            foreach (string raw in tokens)
            {
                string t = (raw ?? "").Trim();
                if (t.Length == 0) continue;
                string lower = t.ToLowerInvariant();
                if (lower == "status") continue;
                if (lower == "on" || lower == "off") { Enabled = lower == "on"; Note(applied, "enabled=" + lower); continue; }
                if (lower == "reset") { Reset(); Note(applied, "reset"); continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) { problems.Add("'" + t + "' is not key=value (" + SnLookRules.Usage + ")"); continue; }
                string key = t.Substring(0, eq).Trim(), val = t.Substring(eq + 1).Trim();
                string err = Set(key, val);
                if (err != null) problems.Add(key + ": " + err);
                else Note(applied, key + "=" + val);
            }
            if (!(HighY > LowY)) { problems.Add("high must be above low (high " + SnLookRules.Num(HighY) + ", low " + SnLookRules.Num(LowY) + "): high set to low + 1"); HighY = LowY + 1.0; }
            return problems;
        }

        private void Note(List<string> applied, string s)
        {
            Changes++;
            if (applied != null) applied.Add(s);
        }

        public string Set(string key, string val)
        {
            double d;
            string lk = key.ToLowerInvariant();
            bool num = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
            switch (lk)
            {
                case "force":
                case "w":
                    if (val.Equals("auto", StringComparison.OrdinalIgnoreCase) || val.Equals("off", StringComparison.OrdinalIgnoreCase)) { Force = double.NaN; return null; }
                    if (!num || d < 0 || d > 1) return "needs 0..1 or auto";
                    Force = d;
                    return null;
                case "low":
                    if (!num || d < 0 || d > 20000) return "needs metres 0..20000";
                    LowY = d;
                    return null;
                case "high":
                    if (!num || d < 0 || d > 20000) return "needs metres 0..20000";
                    HighY = d;
                    return null;
                case "ease":
                    if (!num || d < 0 || d > 60) return "needs seconds 0..60";
                    EaseS = d;
                    return null;
            }
            SnLookKey k = SnLookRules.Find(key);
            if (k == null) return "unknown key (shadow ambient sun exposure force low high ease)";
            if (val.Equals("unset", StringComparison.OrdinalIgnoreCase) || val.Equals("none", StringComparison.OrdinalIgnoreCase) || val == "-") { _t[k.Index] = double.NaN; return null; }
            if (val.Equals("default", StringComparison.OrdinalIgnoreCase)) { _t[k.Index] = k.Default; return null; }
            if (!num) return "needs a number";
            if (d < k.Min || d > k.Max) return "out of range " + SnLookRules.Num(k.Min) + ".." + SnLookRules.Num(k.Max);
            _t[k.Index] = d;
            return null;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("enabled=").Append(Enabled ? "on" : "off").Append(" force=").Append(SnLookRules.Num(Force)).Append(" low=").Append(SnLookRules.Num(LowY))
              .Append(" high=").Append(SnLookRules.Num(HighY)).Append(" ease=").Append(SnLookRules.Num(EaseS));
            foreach (SnLookKey k in SnLookRules.Keys) sb.Append(' ').Append(k.Name).Append('=').Append(double.IsNaN(_t[k.Index]) ? "unset" : SnLookRules.Num(_t[k.Index]));
            return sb.ToString();
        }
    }

    /// <summary>
 /// One member's capture, write and restore (pure: the caller reads the member, asks, and writes what it is told).
 /// A value differing from the last one written while applied is the game's own change: it becomes the new own value.
 /// </summary>
    internal sealed class SnLookSlot
    {
        public bool Applied;
        public double Own = double.NaN, LastWritten = double.NaN;
        public long ExternalChanges;

        /// <summary>The value to write this frame, or NaN for "write nothing".</summary>
        public double Frame(SnLookKey k, double current, double target, double w)
        {
            bool want = w > 0.0 && !double.IsNaN(target);
            if (want)
            {
                if (!Applied) { Own = current; Applied = true; }
                else if (!Same(current, LastWritten)) { Own = current; ExternalChanges++; }
                double v = SnLookRules.Blend(k, target, Own, w);
                if (Same(v, current)) { LastWritten = current; return double.NaN; }
                LastWritten = v;
                return v;
            }
            return Release(current);
        }

        /// <summary>Leave: the captured own value back unless the game changed the member meanwhile (NaN: write nothing).</summary>
        public double Release(double current)
        {
            if (!Applied) return double.NaN;
            Applied = false;
            double own = Own;
            bool ours = Same(current, LastWritten);
            Own = LastWritten = double.NaN;
            if (!ours) { ExternalChanges++; return double.NaN; }
            return Same(current, own) ? double.NaN : own;
        }

        /// <summary>The objects went away (scene change): forget without writing.</summary>
        public void Forget()
        {
            Applied = false;
            Own = LastWritten = double.NaN;
        }

        public static bool Same(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        }
    }

    /// <summary>Row cadence (periodic while applied, edges capped per second with a drop counter).</summary>
    internal sealed class SnLookRowCadence
    {
        private ulong _next, _window;
        private int _edges;
        public long Dropped;

        public bool Due(ulong nowMs, bool active, bool edge)
        {
            if (edge)
            {
                if (nowMs - _window >= 1000 || nowMs < _window) { _window = nowMs; _edges = 0; }
                if (_edges >= SnLookRules.EdgeRowsPerSecond) { Dropped++; return false; }
                _edges++;
                _next = nowMs + SnLookRules.RowEveryMs;
                return true;
            }
            if (!active) return false;
            if (nowMs >= _next || _next - nowMs > SnLookRules.RowEveryMs) { _next = nowMs + SnLookRules.RowEveryMs; return true; }
            return false;
        }
    }
}
