using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S7t T5 look match, SE side: the pure rules (no game types; tests/DeepEngineers.SE.Tests/S7tLookMatchTests.cs).
 // ROADMAP "### S7t Seamless transition" T5; CONTRACT §3.4 (the target table and its derivation), §1.1 (settings
 // touched: none persisted), §5.4 "S7t look match" (de.s7t look, the s7t.look rows).
 //
 // SE's look moves from its own values (in Space and far from the site) to the match set at the Approach band and
 // below, so that SE's picture and Subnautica's look alike where the picture swaps. The weight w is 1 at and below
 // the band's enter altitude, eases to 0 over RampM metres above it (smoothstep), is 0 below the waterline (nothing
 // under water), and is 0 whenever the picture is off: no peer, not the Site policy, off the dive site, no world.
 // The effective weight moves towards that target at most 1/EaseS per second, so a gate flip (peer loss) restores
 // SE's own look within EaseS. Every key maps one SE render member: Factor keys multiply SE's own value
 // (own · (1 + (f − 1)·w)), Value keys replace it (own + (t − own)·w), colour keys do so per channel. A key that is
 // unset leaves SE's own value untouched. Nothing here is persisted: the game hooks change only the copies SE
 // sends to its renderer each frame (LookMatch.cs).
 // =================================================================================================================

    internal enum LookGroup { Post, Env, Fog, Planet, Atmo }

    internal enum LookKind
    {
        /// <summary>One number, multiplies SE's own value at w = 1.</summary>
        Factor,
        /// <summary>One number, replaces SE's own value at w = 1.</summary>
        Value,
        /// <summary>Three factors (r, g, b), per channel.</summary>
        RgbFactor,
        /// <summary>A colour 0..1 (0..255 accepted: any channel above 1 divides all by 255), replaces SE's own.</summary>
        RgbValue,
        /// <summary>Three raw numbers (x, y, z), replace SE's own (atmosphere vectors).</summary>
        Vec3Value,
    }

    /// <summary>One <c>de.s7t look</c> key.</summary>
    internal sealed class LookKey
    {
        public readonly string Name;
        public readonly LookGroup Group;
        public readonly LookKind Kind;
        /// <summary>The SE member it writes (documentation and the trace).</summary>
        public readonly string Member;
        /// <summary>The match-set value at the band (null: unset, SE's own value stays).</summary>
        public readonly double[] Default;
        public readonly double Min, Max;
        public int Index;

        public LookKey(string name, LookGroup group, LookKind kind, string member, double[] dflt, double min, double max)
        {
            Name = name; Group = group; Kind = kind; Member = member; Default = dflt; Min = min; Max = max;
        }

        public int Channels { get { return Kind == LookKind.Factor || Kind == LookKind.Value ? 1 : 3; } }
        public bool IsFactor { get { return Kind == LookKind.Factor || Kind == LookKind.RgbFactor; } }
    }

    /// <summary>The gate's inputs, one frame (filled by LookMatch from the view driver; replayable).</summary>
    internal struct LookGateInputs
    {
        public bool Enabled;
        /// <summary>A world is loaded and the view driver stepped within the last 30 ticks.</summary>
        public bool InWorld;
        public bool BridgeUp;
        /// <summary>On the bound site in a dive zone (CORE, TILE VOID, OUTER VOID).</summary>
        public bool DiveZone;
        /// <summary>The Subnautica peer is Alive (PeerState.Alive).</summary>
        public bool PeerAlive;
        /// <summary>The effective picture policy is Site.</summary>
        public bool SitePolicy;
        /// <summary>The camera altitude above the sea, SN metres (negative under water).</summary>
        public double Y;
        public double BandEnterY;
        public double RampM;
        /// <summary>A forced weight 0..1 (de.s7t look force=…), NaN = automatic.</summary>
        public double Force;
    }

    internal static class LookMatchRules
    {
        public const string Cmd = "de.s7t";
        public const string Verb = "look";
        public const string EnvVar = "DEEPENGINEERS_LOOK";
        public const double DefaultRampM = 1500.0;
        public const double DefaultEaseS = 0.8;
        /// <summary>Below this camera altitude the match set is off (S7t T5: nothing under water).</summary>
        public const double WaterlineCutY = -1.0;
        /// <summary>The view driver must have stepped within this many SE ticks (else the world is gone or paused out).</summary>
        public const ulong DriverStaleTicks = 30;
        public const ulong RowEveryMs = 1000;
        public const int EdgeRowsPerSecond = 4;
        /// <summary>The atmosphere is re-sent at most once per this many frames, and refreshed this often while it holds.</summary>
        public const int AtmoMinFrames = 10, AtmoRefreshFrames = 120;
        /// <summary>Relative change below which the atmosphere is not re-sent.</summary>
        public const double AtmoEpsilon = 0.005;

        public const string Usage = "/de s7t look [status|on|off|reset] [key=value ...] [key=unset|default] [force=0..1|auto] [ramp=m] [ease=s]";

        private static double[] V(params double[] v) { return v; }

        /// <summary>
 /// The keys, their SE members and the match set at the band. Derivation (CONTRACT §3.
 /// band (SE +500 m, Subnautica +380 m) — SE's shadows hard and dark, Subnautica's soft; SE's near water darker and
 /// more saturated (L62 vs L92); SE no aerial perspective (far water L67 vs L152); stars in SE's daylight sky. The
 /// pair predates EarthLike's AtmosphereSettings on DE_4546B (deployed 23:17, FogIntensity 1 adds aerial
 /// perspective), so the fog keys carry half of what the pair alone asks for.
 /// </summary>
        public static readonly LookKey[] Keys = Index(new[]
        {
            // post-process (MyPostprocessSettings.Data, the copy SE sends each frame through MyRenderProxy.SwitchPostprocessSettings)
            new LookKey("contrast", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.Contrast", V(0.90), 0.2, 3),
            new LookKey("saturation", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.Saturation", V(0.88), 0, 3),
            new LookKey("brightness", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.Brightness", null, 0.2, 4),
            new LookKey("tint", LookGroup.Post, LookKind.RgbFactor, "MyPostprocessSettings.Data.BrightnessFactorR/G/B", null, 0, 3),
            new LookKey("vibrance", LookGroup.Post, LookKind.Value, "MyPostprocessSettings.Data.Vibrance", null, -1, 1),
            new LookKey("bloom", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.BloomMult", null, 0, 8),
            new LookKey("exposure", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.LuminanceExposure", null, 0.1, 8),
            new LookKey("eyeTau", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.EyeAdaptationTau", null, 0.05, 20),
            new LookKey("whitePoint", LookGroup.Post, LookKind.Factor, "MyPostprocessSettings.Data.WhitePoint", null, 0.1, 8),
            new LookKey("vignette", LookGroup.Post, LookKind.Value, "MyPostprocessSettings.Data.VignetteStart", null, 0, 10),
            new LookKey("vignetteLength", LookGroup.Post, LookKind.Value, "MyPostprocessSettings.Data.VignetteLength", null, 0.01, 10),
            // environment light (MyEnvironmentData.EnvironmentLight, the copy sent through MyRenderProxy.UpdateRenderEnvironment)
            new LookKey("sun", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.SunDiffuseFactor", V(0.85), 0, 4),
            new LookKey("ambient", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.AmbientDiffuseFactor", V(1.8), 0, 10),
            new LookKey("ambientSpec", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.AmbientSpecularFactor", null, 0, 10),
            new LookKey("aoDir", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.AODirLight", null, 0, 4),
            new LookKey("aoIndirect", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.AOIndirectLight", null, 0, 4),
            new LookKey("skybox", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.SkyboxBrightness", V(0.5), 0, 4),
            new LookKey("envSkybox", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.EnvSkyboxBrightness", null, 0, 4),
            new LookKey("envAtmo", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.EnvAtmosphereBrightness", null, 0, 8),
            new LookKey("shadowFade", LookGroup.Env, LookKind.Factor, "MyEnvironmentLightData.ShadowFadeoutMultiplier", null, 0, 10),
            new LookKey("sunColor", LookGroup.Env, LookKind.RgbFactor, "MyEnvironmentLightData.SunColorRaw", null, 0, 4),
            // environment fog (MyRenderFogSettings, the copy sent through MyRenderProxy.UpdateFogSettings)
            new LookKey("fog", LookGroup.Fog, LookKind.Value, "MyRenderFogSettings.FogMultiplier", V(0.25), 0, 1),
            new LookKey("fogDensity", LookGroup.Fog, LookKind.Value, "MyRenderFogSettings.FogDensity", V(0.0003), 0, 0.05),
            new LookKey("fogColor", LookGroup.Fog, LookKind.RgbValue, "MyRenderFogSettings.FogColor", V(Wire.S7tBandTargets.HazeHorizon.Unit(0), Wire.S7tBandTargets.HazeHorizon.Unit(1), Wire.S7tBandTargets.HazeHorizon.Unit(2)), 0, 1), // S7t integrator: the one measured source (197,241,255), shared with Subnautica's skirt
            new LookKey("fogAtmo", LookGroup.Fog, LookKind.Value, "MyRenderFogSettings.FogAtmo", null, 0, 1),
            new LookKey("fogSky", LookGroup.Fog, LookKind.Value, "MyRenderFogSettings.FogSkybox", null, 0, 1),
            // planet render settings (MyRenderPlanetSettings, the copy sent through MyRenderProxy.UpdatePlanetSettings)
            new LookKey("atmoInt", LookGroup.Planet, LookKind.Factor, "MyRenderPlanetSettings.AtmosphereIntensityMultiplier", null, 0, 4),
            new LookKey("atmoAmbient", LookGroup.Planet, LookKind.Factor, "MyRenderPlanetSettings.AtmosphereIntensityAmbientMultiplier", V(1.25), 0, 4),
            new LookKey("atmoDesat", LookGroup.Planet, LookKind.Factor, "MyRenderPlanetSettings.AtmosphereDesaturationFactorForward", null, 0, 4),
            new LookKey("clouds", LookGroup.Planet, LookKind.Factor, "MyRenderPlanetSettings.CloudsIntensityMultiplier", null, 0, 4),
            // the bound planet's atmosphere (MyAtmosphereSettings sent through its render component; the planet's own
 // AtmosphereSettings, read from the definition at load and saved with the world, are never written)
            new LookKey("rayleigh", LookGroup.Atmo, LookKind.Vec3Value, "MyAtmosphereSettings.RayleighScattering", null, 1, 2000),
            new LookKey("mieColor", LookGroup.Atmo, LookKind.Vec3Value, "MyAtmosphereSettings.MieColorScattering", null, 1, 2000),
            new LookKey("mieG", LookGroup.Atmo, LookKind.Value, "MyAtmosphereSettings.MieG", null, 0, 0.99999),
            new LookKey("mieHeight", LookGroup.Atmo, LookKind.Value, "MyAtmosphereSettings.MieHeight", null, 0.1, 2000),
            new LookKey("rayleighHeight", LookGroup.Atmo, LookKind.Value, "MyAtmosphereSettings.RayleighHeight", null, 0.1, 2000),
            new LookKey("atmoIntensity", LookGroup.Atmo, LookKind.Value, "MyAtmosphereSettings.Intensity", null, 0.01, 100),
            new LookKey("atmoFog", LookGroup.Atmo, LookKind.Value, "MyAtmosphereSettings.FogIntensity", null, 0, 10),
        });

        private static LookKey[] Index(LookKey[] keys)
        {
            for (int i = 0; i < keys.Length; i++) keys[i].Index = i;
            return keys;
        }

        public static LookKey Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (LookKey k in Keys) if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        // ------------------------------------------------------------------------------------------------ weight

 /// <summary>1 at and below <paramref name="bandEnterY"/>, smoothstep to 0 at <c>bandEnterY + rampM</c>, 0 above; 0 for NaN.</summary>
        public static double BandWeight(double y, double bandEnterY, double rampM)
        {
            if (double.IsNaN(y) || double.IsNaN(bandEnterY)) return 0.0;
            if (y <= bandEnterY) return 1.0;
            if (!(rampM > 0.0)) return 0.0;
            double t = (bandEnterY + rampM - y) / rampM;
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>The target weight this frame and why (the row's <c>gate</c>).</summary>
        public static double Target(ref LookGateInputs i, out string why)
        {
            if (!i.Enabled) { why = "off"; return 0.0; }
            if (!i.InWorld) { why = "noWorld"; return 0.0; }
            if (!double.IsNaN(i.Force)) { why = "force"; return Clamp01(i.Force); }
            if (!i.BridgeUp) { why = "bridge"; return 0.0; }
            if (!i.PeerAlive) { why = "peer"; return 0.0; }
            if (!i.SitePolicy) { why = "policy"; return 0.0; }
            if (!i.DiveZone) { why = "site"; return 0.0; }
            if (double.IsNaN(i.Y) || double.IsInfinity(i.Y)) { why = "y"; return 0.0; }
            if (i.Y < WaterlineCutY) { why = "underwater"; return 0.0; }
            double w = BandWeight(i.Y, i.BandEnterY, i.RampM);
            why = w >= 1.0 ? "band" : w <= 0.0 ? "high" : "ramp";
            return w;
        }

        /// <summary>Moves <paramref name="current"/> towards <paramref name="target"/> by at most dt/easeS (easeS ≤ 0: at once).</summary>
        public static double Ease(double current, double target, double dtS, double easeS)
        {
            if (double.IsNaN(current)) current = 0.0;
            if (!(easeS > 0.0) || double.IsNaN(dtS)) return target;
            double step = Math.Max(0.0, dtS) / easeS;
            double d = target - current;
            if (Math.Abs(d) <= step) return target;
            return current + (d > 0 ? step : -step);
        }

        public static double Clamp01(double v) { return v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : double.IsNaN(v) ? 0.0 : v; }

        // ------------------------------------------------------------------------------------------------ blend

 /// <summary>One channel of a key at weight <paramref name="w"/> from SE's own value; own when the key is unset.</summary>
        public static float Blend(LookKey k, double[] target, int channel, float own, double w)
        {
            if (k == null || target == null || w <= 0.0 || channel < 0 || channel >= target.Length) return own;
            if (w > 1.0) w = 1.0;
            double t = target[channel];
            if (double.IsNaN(t)) return own;
            double v = k.IsFactor ? own * (1.0 + (t - 1.0) * w) : own + (t - own) * w;
            if (double.IsNaN(v) || double.IsInfinity(v)) return own;
            return (float)v;
        }

        /// <summary>A byte colour channel (fog colour): own byte → target 0..1 at w, rounded and clamped.</summary>
        public static byte BlendByte(LookKey k, double[] target, int channel, byte own, double w)
        {
            float v = Blend(k, target, channel, own / 255f, w);
            double b = Math.Round(v * 255.0);
            return (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }

        /// <summary>True when two atmosphere snapshots differ by more than <see cref="AtmoEpsilon"/> (relative) in any component.</summary>
        public static bool Differs(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return true;
            for (int i = 0; i < a.Length; i++)
            {
                double s = Math.Max(1e-6, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])));
                if (Math.Abs(a[i] - b[i]) / s > AtmoEpsilon) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------------ args

 /// <summary>
 /// The <c>de.s7t</c> args this handler answers: <c>{look: "k=v k=v" | "status" | {k: v} | [tokens]}</c> or a chat
 /// relay <c>{argv: ["look", tokens…]}</c>. False for the other S7t keys (they reach their own handler).
 /// </summary>
        public static bool IsMine(IDictionary<string, object> args, out List<string> tokens)
        {
            tokens = null;
            if (args == null) return false;
            object v;
            if (args.TryGetValue(Verb, out v))
            {
                tokens = new List<string>();
                var d = v as IDictionary<string, object>;
                var list = v as System.Collections.IList;
                string s = v as string;
                if (d != null) foreach (var kv in d) tokens.Add(kv.Key + "=" + ValueText(kv.Value));
                else if (list != null) foreach (object o in list) tokens.Add(ValueText(o));
                else if (s != null) tokens.AddRange(SplitTokens(s));
                else if (v is bool) tokens.Add((bool)v ? "on" : "off");
                return true;
            }
            if (args.TryGetValue("argv", out v) && v is System.Collections.IList)
            {
                var words = (System.Collections.IList)v;
                if (words.Count == 0 || !string.Equals(ValueText(words[0]), Verb, StringComparison.OrdinalIgnoreCase)) return false;
                tokens = new List<string>();
                for (int i = 1; i < words.Count; i++) tokens.AddRange(SplitTokens(ValueText(words[i])));
                return true;
            }
            return false;
        }

        /// <summary>Splits on blanks and ';' (commas stay inside colour values).</summary>
        public static List<string> SplitTokens(string s)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(s)) return r;
            foreach (string p in s.Split(new[] { ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)) r.Add(p.Trim());
            return r;
        }

        public static string ValueText(object o)
        {
            if (o == null) return "";
            if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
            if (o is bool) return (bool)o ? "on" : "off";
            var list = o as System.Collections.IList;
            if (list != null)
            {
                var sb = new StringBuilder();
                foreach (object e in list) { if (sb.Length > 0) sb.Append(','); sb.Append(ValueText(e)); }
                return sb.ToString();
            }
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static string Num(double v)
        {
            return double.IsNaN(v) ? "auto" : v.ToString("0.#####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The live match set (<c>de.s7t look</c>) and its controls. Update thread only.</summary>
    internal sealed class LookTable
    {
        private readonly double[][] _target = new double[LookMatchRules.Keys.Length][];
        public bool Enabled = true;
        public double Force = double.NaN;
        public double RampM = LookMatchRules.DefaultRampM;
        public double EaseS = LookMatchRules.DefaultEaseS;
        public long Changes;

        public LookTable() { Reset(); }

        public void Reset()
        {
            foreach (LookKey k in LookMatchRules.Keys) _target[k.Index] = k.Default != null ? (double[])k.Default.Clone() : null;
            Enabled = true;
            Force = double.NaN;
            RampM = LookMatchRules.DefaultRampM;
            EaseS = LookMatchRules.DefaultEaseS;
        }

        public double[] Get(LookKey k) { return k != null ? _target[k.Index] : null; }

        public bool AnySet(LookGroup g)
        {
            foreach (LookKey k in LookMatchRules.Keys) if (k.Group == g && _target[k.Index] != null) return true;
            return false;
        }

        /// <summary>
 /// Applies the tokens in order: <c>on</c>, <c>off</c>, <c>reset</c>, <c>status</c>, <c>key=value</c>,
 /// <c>key=unset</c>, <c>key=default</c>, <c>force=0..1|auto</c>, <c>ramp=m</c>, <c>ease=s</c>. Returns the
 /// problems; <paramref name="applied"/> lists what changed.
 /// </summary>
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
                if (lower == "reset") { Reset(); Note(applied, "reset (the match set's defaults)"); continue; }
                int eq = t.IndexOf('=');
                if (eq <= 0) { problems.Add("'" + t + "' is not key=value (" + LookMatchRules.Usage + ")"); continue; }
                string key = t.Substring(0, eq).Trim(), val = t.Substring(eq + 1).Trim();
                string err = Set(key, val);
                if (err != null) problems.Add(key + ": " + err);
                else Note(applied, key + "=" + val);
            }
            return problems;
        }

        private void Note(List<string> applied, string s)
        {
            Changes++;
            if (applied != null) applied.Add(s);
        }

        /// <summary>One key; null when applied, else why not.</summary>
        public string Set(string key, string val)
        {
            double d;
            string lk = key.ToLowerInvariant();
            if (lk == "enabled" || lk == "look")
            {
                string v = val.ToLowerInvariant();
                if (v == "on" || v == "1" || v == "true") { Enabled = true; return null; }
                if (v == "off" || v == "0" || v == "false") { Enabled = false; return null; }
                return "needs on|off";
            }
            if (lk == "force" || lk == "w")
            {
                if (val.Equals("auto", StringComparison.OrdinalIgnoreCase) || val.Equals("off", StringComparison.OrdinalIgnoreCase)) { Force = double.NaN; return null; }
                if (!TryNum(val, out d) || d < 0 || d > 1) return "needs 0..1 or auto";
                Force = d;
                return null;
            }
            if (lk == "ramp")
            {
                if (!TryNum(val, out d) || d < 0 || d > 100000) return "needs metres 0..100000";
                RampM = d;
                return null;
            }
            if (lk == "ease")
            {
                if (!TryNum(val, out d) || d < 0 || d > 60) return "needs seconds 0..60";
                EaseS = d;
                return null;
            }
            LookKey k = LookMatchRules.Find(key);
            if (k == null) return "unknown key (" + KeyNames() + ")";
            if (val.Equals("unset", StringComparison.OrdinalIgnoreCase) || val.Equals("none", StringComparison.OrdinalIgnoreCase) || val == "-")
            {
                _target[k.Index] = null;
                return null;
            }
            if (val.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                _target[k.Index] = k.Default != null ? (double[])k.Default.Clone() : null;
                return null;
            }
            double[] parsed;
            string perr = Parse(k, val, out parsed);
            if (perr != null) return perr;
            _target[k.Index] = parsed;
            return null;
        }

        public static string Parse(LookKey k, string val, out double[] parsed)
        {
            parsed = null;
            string[] parts = (val ?? "").Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
            int n = k.Channels;
            if (n == 3 && parts.Length == 1 && k.Kind == LookKind.RgbFactor) parts = new[] { parts[0], parts[0], parts[0] };
            if (parts.Length != n) return n == 1 ? "needs a number" : "needs three numbers a,b,c";
            var v = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (!TryNum(parts[i], out v[i])) return "'" + parts[i] + "' is not a number";
            }
            if (k.Kind == LookKind.RgbValue)
            {
                bool bytes = false;
                foreach (double c in v) if (c > 1.0) bytes = true;
                if (bytes) for (int i = 0; i < n; i++) v[i] /= 255.0;
            }
            for (int i = 0; i < n; i++)
            {
                if (v[i] < k.Min || v[i] > k.Max)
                    return "out of range " + LookMatchRules.Num(k.Min) + ".." + LookMatchRules.Num(k.Max) + (k.Kind == LookKind.RgbValue ? " (or 0..255)" : "");
            }
            parsed = v;
            return null;
        }

        private static bool TryNum(string s, out double d)
        {
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
        }

        public static string KeyNames()
        {
            var sb = new StringBuilder();
            foreach (LookKey k in LookMatchRules.Keys) { if (sb.Length > 0) sb.Append(' '); sb.Append(k.Name); }
            return sb.ToString();
        }

        /// <summary>"enabled=on force=auto ramp=1500 ease=0.8 contrast=0.9 … (unset: …)".</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("enabled=").Append(Enabled ? "on" : "off").Append(" force=").Append(LookMatchRules.Num(Force))
              .Append(" ramp=").Append(LookMatchRules.Num(RampM)).Append(" ease=").Append(LookMatchRules.Num(EaseS));
            var unset = new StringBuilder();
            foreach (LookKey k in LookMatchRules.Keys)
            {
                double[] t = _target[k.Index];
                if (t == null) { if (unset.Length > 0) unset.Append(','); unset.Append(k.Name); continue; }
                sb.Append(' ').Append(k.Name).Append('=').Append(ValueString(k, t));
            }
            if (unset.Length > 0) sb.Append(" (unset: ").Append(unset).Append(')');
            return sb.ToString();
        }

        public static string ValueString(LookKey k, double[] t)
        {
            if (t == null) return "unset";
            var sb = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(k.Kind == LookKind.RgbValue ? Math.Round(t[i] * 255.0).ToString("0", CultureInfo.InvariantCulture) : LookMatchRules.Num(t[i]));
            }
            return sb.ToString();
        }

        /// <summary>Env defaults: <c>DEEPENGINEERS_LOOK</c> = "off" | "on" | tokens (blank- or ';'-separated). Returns the problems.</summary>
        public List<string> ApplyEnv(string env, List<string> applied)
        {
            if (string.IsNullOrEmpty(env)) return new List<string>();
            return Apply(LookMatchRules.SplitTokens(env), applied);
        }
    }

    /// <summary>Row cadence: one periodic row per <see cref="LookMatchRules.RowEveryMs"/> while active, edge rows capped per second (drops counted).</summary>
    internal sealed class LookRowCadence
    {
        private ulong _nextPeriodicMs, _edgeWindowMs;
        private int _edgesInWindow;
        public long Dropped;

        /// <summary>True when a row is due now; <paramref name="edge"/> rows are capped at <see cref="LookMatchRules.EdgeRowsPerSecond"/> per second.</summary>
        public bool Due(ulong nowMs, bool active, bool edge)
        {
            if (edge)
            {
                if (nowMs - _edgeWindowMs >= 1000 || nowMs < _edgeWindowMs) { _edgeWindowMs = nowMs; _edgesInWindow = 0; }
                if (_edgesInWindow >= LookMatchRules.EdgeRowsPerSecond) { Dropped++; return false; }
                _edgesInWindow++;
                _nextPeriodicMs = nowMs + LookMatchRules.RowEveryMs;
                return true;
            }
            if (!active) return false;
            if (nowMs >= _nextPeriodicMs || _nextPeriodicMs - nowMs > LookMatchRules.RowEveryMs)
            {
                _nextPeriodicMs = nowMs + LookMatchRules.RowEveryMs;
                return true;
            }
            return false;
        }
    }
}
