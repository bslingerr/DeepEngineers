using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S7t-sn-altitude: the Unity-free
 // rules of Subnautica's picture above the sea. Replay-exact and tested (tests/DeepEngineers.SN.Tests/S7tAltitudeTests.cs);
 // no game type, no Unity type.
 // - Why: the ocean surface is WaterSurface's HeightFieldMesh, a square of +-camera.farClipPlane
 // around the camera
 // and the main camera's far plane is 1,700 m ("DE.S1... near=0.03
 // far=1700", sn-20261017_224212-5380.jsonl). From altitude the sea therefore ends in a hard line 1.7 km out, and at a
 // yaw beyond 45 degrees the frustum sees past the square's side edge onto Subnautica's far terrain
 // with no water over it: the two flat olive-green
 // their inner edges converge at the square's side, their far edges at
 // the far plane). Not the world edge: the plates sit inside the far plane, where only the streamed terrain can be.
 // - HorizonRules: the far plane by altitude (the "grid extended": SNCameraRoot.SetFarPlaneDistance moves the water's
 // square out to the geometric horizon of SE's planet), the haze table by altitude band, the horizon skirt's rows.
 // - ImpostorRules: T3's coarse mirror of grids above the camera (GridAdd flags bit 6, see ImpostorGridFlag).
 // =================================================================================================================

 /// <summary>An sRGB colour, 0..py).</summary>
    internal struct HazeRgb : IEquatable<HazeRgb>
    {
        public readonly byte R, G, B;

        public HazeRgb(int r, int g, int b)
        {
            R = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
            G = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
            B = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
        }

        /// <summary>"r,g,b" (0..255), also "r g b", "r/g/b" or "#rrggbb".</summary>
        public static bool TryParse(string s, out HazeRgb c)
        {
            c = default(HazeRgb);
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length == 7 && s[0] == '#')
            {
                int v;
                if (!int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
                c = new HazeRgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
                return true;
            }
            string[] p = s.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3) return false;
            var x = new int[3];
            for (int i = 0; i < 3; i++)
            {
                double d;
                if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || d < 0 || d > 255) return false;
                x[i] = (int)Math.Round(d);
            }
            c = new HazeRgb(x[0], x[1], x[2]);
            return true;
        }

        public bool Equals(HazeRgb o) { return R == o.R && G == o.G && B == o.B; }
        public override bool Equals(object obj) { return obj is HazeRgb && Equals((HazeRgb)obj); }
        public override int GetHashCode() { return (R << 16) | (G << 8) | B; }
        public override string ToString() { return R.ToString(CultureInfo.InvariantCulture) + "," + G.ToString(CultureInfo.InvariantCulture) + "," + B.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>One row of the haze table: the look at a camera altitude above the sea (linear between rows, clamped at the ends).</summary>
    internal struct HazeRow
    {
        /// <summary>Camera altitude above the sea (m).</summary>
        public double AltM;
        /// <summary>The far plane = <c>FarK</c> x the geometric horizon distance of SE's planet (the water's square follows it).</summary>
        public double FarK;
        /// <summary>Where the skirt starts (fraction of the far plane): Subnautica's own look (its aerial perspective) in front of it.</summary>
        public double FadeStart;
        /// <summary>Where the skirt is opaque (fraction of the far plane): the sea's far edge and anything beyond it gone.</summary>
        public double FadeEnd;
        /// <summary>Multiplier on uSky's own above-water haze density (<c>_SkyFogDensity</c>); 1 = Subnautica's own (default everywhere).</summary>
        public double FogMul;
        /// <summary>The skirt's opacity at the horizon (0 = no skirt).</summary>
        public double Alpha;

        public HazeRow(double altM, double farK, double fadeStart, double fadeEnd, double fogMul, double alpha)
        {
            AltM = altM; FarK = farK; FadeStart = fadeStart; FadeEnd = fadeEnd; FogMul = fogMul; Alpha = alpha;
        }
    }

    /// <summary>What the horizon host applies this frame (one per frame, no allocation).</summary>
    internal struct AltitudeFrame
    {
        /// <summary>0 off (toggle), 1 below the minimum altitude (Subnautica's own look), 2 above (far plane, skirt, haze).</summary>
        public int State;
        public double AltM, HorizonM, FarBaseM, FarM;
        /// <summary>The skirt's horizontal radius (= the fade start distance, m) and the fade end / fade start ratio.</summary>
        public double SkirtRadiusM, FadeRatio;
        public double FogMul, Alpha, Feather;
        public bool SkirtOn, ImpostorFar;

        public const int Off = 0, Below = 1, Above = 2;
        public static string StateName(int s) { return s == Above ? "above" : (s == Below ? "below" : "off"); }
    }

    /// <summary>
 /// The live settings of the altitude picture and of the impostors: env defaults (<see cref="HorizonRules.EnvHorizon"/>,
 /// <see cref="HorizonRules.EnvHaze"/>, <see cref="ImpostorRules.EnvImpostor"/>), then <c>de.s7t horizon on|off</c>,
 /// <c>de.s7t haze key=value</c>, <c>de.s7t impostor on|off key=value</c>. NaN overrides = the table's value.
 /// </summary>
    internal sealed class AltitudeSettings
    {
        public bool Horizon = true;
        public bool Impostors = true;
        /// </summary>
        public bool Clouds = true;
        public double MinAltM = HorizonRules.DefaultMinAltM;
        public double FarMaxM = HorizonRules.DefaultFarMaxM;
        public double ImpostorFarM = ImpostorRules.DefaultFarM;
        public double Feather = HorizonRules.DefaultFeather;
        public double AlphaMul = 1.0;
        public double Gain = 1.0;
        public bool Tod = true;
        public double NightFloor = HorizonRules.DefaultNightFloor;
        public double FarK = double.NaN, FadeStart = double.NaN, FadeEnd = double.NaN, FogMul = double.NaN;
        public HazeRgb HorizonRgb = HorizonRules.DefaultHorizonRgb;
        public HazeRgb LowRgb = HorizonRules.DefaultLowRgb;
        /// </summary>
        public bool HorizonRgbSet, LowRgbSet;
        // T3 impostors
        public double MinPartFrac = ImpostorRules.DefaultMinPartFrac;
        public int CellsPerFrame = ImpostorRules.DefaultCellsPerFrame;
        public double ReachM = ImpostorRules.DefaultReachM;

        public static readonly string[] Keys =
        {
            "horizon", "impostor", "alpha", "fadeStart", "fadeEnd", "farK", "farMax", "fogMul", "feather", "horizonRgb", "lowRgb",
            "tod", "night", "gain", "minAlt", "impostorFar", "reach", "minPart", "perFrame",
        };

        public AltitudeSettings Clone() { return (AltitudeSettings)MemberwiseClone(); }

        /// <summary>Applies <c>key=value</c> pairs (keys case-insensitive); returns null or the first error (nothing after it applied).</summary>
        public string Apply(IList<KeyValuePair<string, string>> kvs)
        {
            if (kvs == null) return null;
            foreach (KeyValuePair<string, string> kv in kvs)
            {
                string e = Set(kv.Key, kv.Value);
                if (e != null) return e;
            }
            return null;
        }

        public string Set(string key, string value)
        {
            string k = (key ?? "").Trim().ToLowerInvariant();
            string v = (value ?? "").Trim();
            bool b;
            double d;
            HazeRgb c;
            switch (k)
            {
                case "horizon": if (!HorizonRules.TryOnOff(v, out b)) return "horizon: on|off"; Horizon = b; return null;
                case "impostor": case "impostors": if (!HorizonRules.TryOnOff(v, out b)) return "impostor: on|off"; Impostors = b; return null;
                case "tod": if (!HorizonRules.TryOnOff(v, out b)) return "tod: on|off"; Tod = b; return null;
                case "horizonrgb": if (!HazeRgb.TryParse(v, out c)) return "horizonRgb: r,g,b (0..255)"; HorizonRgb = c; HorizonRgbSet = true; return null;
                case "lowrgb": if (!HazeRgb.TryParse(v, out c)) return "lowRgb: r,g,b (0..255)"; LowRgb = c; LowRgbSet = true; return null;
            }
            bool table = v.Equals("table", StringComparison.OrdinalIgnoreCase) || v.Equals("default", StringComparison.OrdinalIgnoreCase);
            if (table)
            {
                switch (k)
                {
                    case "fadestart": FadeStart = double.NaN; return null;
                    case "fadeend": FadeEnd = double.NaN; return null;
                    case "fark": FarK = double.NaN; return null;
                    case "fogmul": FogMul = double.NaN; return null;
                }
            }
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d))
                return k.Length == 0 ? "empty key" : (Array.Exists(Keys, x => x.ToLowerInvariant() == k) ? k + ": a number" : "unknown key '" + key + "' (" + string.Join(" ", Keys) + ")");
            switch (k)
            {
                case "alpha": if (d < 0 || d > 1) return "alpha: 0..1"; AlphaMul = d; return null;
                case "fadestart": if (d < 0.05 || d > 0.99) return "fadeStart: 0.05..0.99 (of the far plane) or table"; FadeStart = d; return null;
                case "fadeend": if (d < 0.06 || d > 1.0) return "fadeEnd: 0.06..1 (of the far plane) or table"; FadeEnd = d; return null;
                case "fark": if (d < 0.1 || d > 4) return "farK: 0.1..4 (x the horizon distance) or table"; FarK = d; return null;
                case "farmax": if (d < 1000 || d > 100000) return "farMax: 1000..100000 m"; FarMaxM = d; return null;
                case "fogmul": if (d < 0 || d > 20) return "fogMul: 0..20 or table"; FogMul = d; return null;
                case "feather": if (d < 0 || d > 0.5) return "feather: 0..0.5 (of the band, above the horizon)"; Feather = d; return null;
                case "night": if (d < 0 || d > 1) return "night: 0..1"; NightFloor = d; return null;
                case "gain": if (d < 0 || d > 8) return "gain: 0..8"; Gain = d; return null;
                case "minalt": if (d < 0 || d > 1000) return "minAlt: 0..1000 m"; MinAltM = d; return null;
                case "impostorfar": if (d < 0 || d > 100000) return "impostorFar: 0..100000 m"; ImpostorFarM = d; return null;
                case "reach": if (d < 0 || d > 20000) return "reach: 0..20000 m"; ReachM = d; return null;
                case "minpart": if (d < 0 || d > 4) return "minPart: 0..4 (x the block size)"; MinPartFrac = d; return null;
                case "perframe": if (d < 0 || d > 256) return "perFrame: 0..256"; CellsPerFrame = (int)Math.Round(d); return null;
                default: return "unknown key '" + key + "' (" + string.Join(" ", Keys) + ")";
            }
        }

        /// <summary>One line for the status reply and the rows.</summary>
        public string Describe()
        {
            var sb = new StringBuilder(256);
            sb.Append("horizon=").Append(Horizon ? "on" : "off").Append(" impostor=").Append(Impostors ? "on" : "off")
              .Append(" alpha=").Append(N(AlphaMul)).Append(" fadeStart=").Append(T(FadeStart)).Append(" fadeEnd=").Append(T(FadeEnd))
              .Append(" farK=").Append(T(FarK)).Append(" farMax=").Append(N(FarMaxM)).Append(" fogMul=").Append(T(FogMul))
              .Append(" feather=").Append(N(Feather)).Append(" horizonRgb=").Append(HorizonRgb).Append(HorizonRgbSet ? "(set)" : "")
              .Append(" lowRgb=").Append(LowRgb).Append(LowRgbSet ? "(set)" : "").Append(" tod=").Append(Tod ? "on" : "off")
              .Append(" night=").Append(N(NightFloor)).Append(" gain=").Append(N(Gain)).Append(" minAlt=").Append(N(MinAltM))
              .Append(" impostorFar=").Append(N(ImpostorFarM)).Append(" reach=").Append(N(ReachM)).Append(" minPart=").Append(N(MinPartFrac))
              .Append(" perFrame=").Append(CellsPerFrame.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string N(double d) { return d.ToString("0.####", CultureInfo.InvariantCulture); }
        private static string T(double d) { return double.IsNaN(d) ? "table" : N(d); }
    }

    /// <summary>T2: the far plane, the haze table and the horizon skirt (Subnautica's picture up to ~1.5 km and beyond).</summary>
    internal static class HorizonRules
    {
        public const string Cmd = "de.s7t";
        /// <summary><c>on</c> (default) | <c>off</c>: the far plane by altitude, the skirt and the haze multiplier.</summary>
        public const string EnvHorizon = "DEEPENGINEERS_S7T_HORIZON";
        /// <summary><c>key=value</c> pairs separated by spaces or ';' (the keys of <c>de.s7t haze</c>); default empty = the table.</summary>
        public const string EnvHaze = "DEEPENGINEERS_S7T_HAZE";

        /// <summary>Below this altitude Subnautica's own look stays untouched (the surface, the swim, the dive).</summary>
        public const double DefaultMinAltM = 2.0;
        public const double DefaultFarMaxM = 20000.0;
        /// <summary>The skirt's feather above the horizon, as a fraction of its band (alpha 0 at the top).</summary>
        public const double DefaultFeather = 0.06;
        public const double DefaultNightFloor = 0.02;

        /// <summary>

 /// (23:05, ROADMAP S7t T5 row: "sky at the horizon... SN (197,241,255) L233"). The look-match build pass target table
 /// replaces it when it exists (<c>AltitudeHost.Targets</c>); this constant is the fallback and the named dependency.
 /// </summary>
        public static readonly HazeRgb DefaultHorizonRgb = new HazeRgb(S7tBandTargets.HazeHorizon.R, S7tBandTargets.HazeHorizon.G, S7tBandTargets.HazeHorizon.B); // S7t integrator: (197,241,255) from the one measured source (Wire S7tBandTargets), shared with SE's look-match fog colour
 /// <summary>The colour the far sea fades from: Subnautica's far water at 380 m on the same screenshot ((129,155,187) L152,
 /// the aerial perspective the brief keeps).</summary>
        public static readonly HazeRgb DefaultLowRgb = new HazeRgb(S7tBandTargets.HazeLow.R, S7tBandTargets.HazeLow.G, S7tBandTargets.HazeLow.B); // S7t integrator: (129,155,187) from Wire S7tBandTargets

 /// <summary>
 /// s7t haze</c>). The fade starts beyond 1.7 km at
 /// every altitude of the band (fadeStart x far: 3.0 km at 380 m, 4.4 km at 1 km), so the old picture's own aerial
 /// perspective (the measured far water) is kept and only what used to be the hard edge and the plates is hazed.
 /// FogMul 1 = uSky's own density everywhere (Subnautica's measured look, "do not flatten it").
 /// </summary>
        public static readonly HazeRow[] DefaultTable =
        {
            new HazeRow(0,    1.0, 0.85, 1.00, 1.0, 0.0),
            new HazeRow(50,   1.0, 0.70, 0.98, 1.0, 1.0),
            new HazeRow(150,  1.0, 0.55, 0.97, 1.0, 1.0),
            new HazeRow(400,  1.0, 0.45, 0.96, 1.0, 1.0),
            new HazeRow(1000, 1.0, 0.40, 0.95, 1.0, 1.0),
            new HazeRow(1500, 1.0, 0.38, 0.95, 1.0, 1.0),
            new HazeRow(3000, 1.0, 0.35, 0.95, 1.0, 1.0),
        };

        /// <summary>
 /// The skirt's rows, v = (fade start distance) / (sea distance) = tan(depression) / tan(depression of the fade start):
 /// v 0 is the horizon (the sea at infinity), v 1 the fade start. The mesh is built once with vertex y = -v at radius 1
 /// and is moved to the camera and scaled (R, altitude, R) every frame: a vertex at v then shows exactly the depression
 /// of the sea at distance R / v (tan = v x altitude / R). The feather row sits at v = -feather (above the horizon).
 /// </summary>
        public static readonly float[] SkirtRows = { 0f, 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.27f, 0.35f, 0.43f, 0.52f, 0.62f, 0.72f, 0.83f, 0.92f, 1f };
        /// <summary>Columns around the ring (the seam column is duplicated).</summary>
        public const int SkirtColumns = 128;

        /// <summary>Geometric horizon distance from <paramref name="altM"/> above a sphere of radius <paramref name="radiusM"/> (SE's sea, Rs 60 km): sqrt(2Rh + h^2).</summary>
        public static double HorizonDistanceM(double altM, double radiusM = SiteV1.RsExpected)
        {
            if (!(altM > 0) || !(radiusM > 0)) return 0;
            return Math.Sqrt(2.0 * radiusM * altM + altM * altM);
        }

        /// <summary>The table's row at an altitude (linear between rows, clamped at the ends; the table sorted by AltM).</summary>
        public static HazeRow Sample(HazeRow[] table, double altM)
        {
            if (table == null || table.Length == 0) return new HazeRow(altM, 1, 0.5, 0.95, 1, 0);
            if (double.IsNaN(altM) || altM <= table[0].AltM) { HazeRow r0 = table[0]; r0.AltM = altM; return r0; }
            int n = table.Length;
            if (altM >= table[n - 1].AltM) { HazeRow rn = table[n - 1]; rn.AltM = altM; return rn; }
            int i = 1;
            while (i < n && table[i].AltM < altM) i++;
            HazeRow a = table[i - 1], b = table[i];
            double span = b.AltM - a.AltM;
            double t = span > 0 ? (altM - a.AltM) / span : 1.0;
            return new HazeRow(altM, L(a.FarK, b.FarK, t), L(a.FadeStart, b.FadeStart, t), L(a.FadeEnd, b.FadeEnd, t), L(a.FogMul, b.FogMul, t), L(a.Alpha, b.Alpha, t));
        }

        /// <summary>
 /// This frame's far plane, skirt and haze for a camera at <paramref name="altM"/> above the sea. Below the minimum
 /// altitude (or with the toggle off) the far plane is Subnautica's own (<paramref name="farBaseM"/>) and nothing is
 /// drawn. Above it: far = clamp(max(farK x horizon, impostorFar when impostors exist), farBase, max(farBase, farMax)).
 /// </summary>
        public static AltitudeFrame Frame(AltitudeSettings s, HazeRow[] table, double altM, double farBaseM, bool impostorsPresent)
        {
            var f = new AltitudeFrame { AltM = altM, FarBaseM = farBaseM, FarM = farBaseM, FogMul = 1.0, FadeRatio = 1.0 };
            f.HorizonM = HorizonDistanceM(altM);
            if (s == null || !s.Horizon) { f.State = AltitudeFrame.Off; return f; }
            if (!(altM >= s.MinAltM))
            {
                f.State = AltitudeFrame.Below;
                // R2: at the surface (0 < alt < MinAltM: swimming, a low deck) a ship parked 2-5 km up must stay
 // in view: the far plane takes the impostor distance while impostors exist; Subnautica's own look otherwise
 // unchanged (no skirt, fogMul 1); at or below the sea nothing of S7t
                if (altM > 0.0 && impostorsPresent && s.Impostors && s.ImpostorFarM > farBaseM)
                {
                    f.FarM = Math.Min(Math.Max(farBaseM, s.ImpostorFarM), Math.Max(farBaseM, s.FarMaxM));
                    f.ImpostorFar = true;
                }
                return f;
            }
            f.State = AltitudeFrame.Above;
            HazeRow r = Sample(table, altM);
            if (!double.IsNaN(s.FarK)) r.FarK = s.FarK;
            if (!double.IsNaN(s.FadeStart)) r.FadeStart = s.FadeStart;
            if (!double.IsNaN(s.FadeEnd)) r.FadeEnd = s.FadeEnd;
            if (!double.IsNaN(s.FogMul)) r.FogMul = s.FogMul;
            double far = Clamp(r.FarK, 0.1, 4.0) * f.HorizonM;
            if (impostorsPresent && s.Impostors && s.ImpostorFarM > far) { far = s.ImpostorFarM; f.ImpostorFar = true; }
            double cap = Math.Max(farBaseM, s.FarMaxM);
            if (far > cap) far = cap;
            if (far < farBaseM) far = farBaseM;
            f.FarM = far;
            double fs = Clamp(r.FadeStart, 0.05, 0.99);
            double fe = Clamp(r.FadeEnd, fs + 0.005, 1.0);
            f.SkirtRadiusM = fs * far;
            f.FadeRatio = fe / fs;
            f.FogMul = Clamp(r.FogMul, 0.0, 20.0);
            f.Alpha = Clamp(r.Alpha * s.AlphaMul, 0.0, 1.0);
            f.Feather = Clamp(s.Feather, 0.0, 0.5);
            f.SkirtOn = f.Alpha > 0.001 && f.SkirtRadiusM > 1.0;
            return f;
        }

        /// <summary>The skirt's opacity at row v: 0 above the horizon (feather), the frame's alpha at the horizon, then
 /// smoothstep over the sea distance from the fade start (v 1, alpha 0) to the fade end (v = 1 / ratio, alpha 1).</summary>
        public static double RowAlpha(double v, double fadeRatio, double alpha)
        {
            if (v < 0) return 0.0;
            if (v <= 1e-9) return alpha;
            double ratio = fadeRatio > 1.0001 ? fadeRatio : 1.0001;
            return alpha * SmoothStep(1.0, ratio, 1.0 / v);
        }

        /// <summary>The weight of the horizon colour at row v (1 at and above the horizon, 0 at the fade start).</summary>
        public static double RowHorizonWeight(double v)
        {
            if (v <= 0) return 1.0;
            return v >= 1 ? 0.0 : 1.0 - v;
        }

        /// <summary>
 /// The skirt's linear RGB at row v: lerp(low, horizon, weight) in linear light, times the time-of-day factor per
 /// channel and the gain (Subnautica renders in linear colour space; mesh vertex colours reach the shader unconverted).
 /// </summary>
        public static void RowColour(HazeRgb low, HazeRgb horizon, double v, double todR, double todG, double todB, double gain,
                                     out double r, out double g, out double b)
        {
            double w = RowHorizonWeight(v);
            r = (L(SrgbToLinear(low.R / 255.0), SrgbToLinear(horizon.R / 255.0), w)) * todR * gain;
            g = (L(SrgbToLinear(low.G / 255.0), SrgbToLinear(horizon.G / 255.0), w)) * todG * gain;
            b = (L(SrgbToLinear(low.B / 255.0), SrgbToLinear(horizon.B / 255.0), w)) * todB * gain;
        }

        /// <summary>
 /// Time of day per channel: uSky's own mean sky colour now over its value at noon (Timeline01 0.5), clamped to
 /// [floor, 1.5]; 1 when the noon reference is too dark to divide by. The table's colours are the daytime look; this
 /// carries Subnautica's own dusk and night onto them (read-only: uSky's colour parameters are never written).
 /// </summary>
        public static void TodFactor(double nowR, double nowG, double nowB, double refR, double refG, double refB, double floor,
                                     out double fr, out double fg, out double fb)
        {
            fr = Ratio(nowR, refR, floor);
            fg = Ratio(nowG, refG, floor);
            fb = Ratio(nowB, refB, floor);
        }

        private static double Ratio(double now, double reference, double floor)
        {
            if (double.IsNaN(now) || double.IsNaN(reference) || reference < 0.02) return 1.0;
            return Clamp(now / reference, Clamp(floor, 0.0, 1.0), 1.5);
        }

        public static double SrgbToLinear(double c)
        {
            if (c <= 0) return 0;
            if (c <= 0.04045) return c / 12.92;
            if (c >= 1) return 1;
            return Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        public static double SmoothStep(double a, double b, double x)
        {
            if (b <= a) return x >= b ? 1.0 : 0.0;
            double t = (x - a) / (b - a);
            if (t <= 0) return 0.0;
            if (t >= 1) return 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        public static double Clamp(double x, double lo, double hi)
        {
            if (double.IsNaN(x)) return lo;
            return x < lo ? lo : (x > hi ? hi : x);
        }

        private static double L(double a, double b, double t) { return a + (b - a) * t; }

        public static bool TryOnOff(string s, out bool b)
        {
            b = false;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "on": case "1": case "true": case "yes": b = true; return true;
                case "off": case "0": case "false": case "no": b = false; return true;
                default: return false;
            }
        }

        /// <summary>"k=v k=v;k=v" -> pairs (the env form and the chat words); a word without '=' is an error.</summary>
        public static List<KeyValuePair<string, string>> ParseKv(string text, out string error)
        {
            error = null;
            var r = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return r;
            foreach (string w in text.Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = w.IndexOf('=');
                if (eq <= 0) { error = "'" + w + "' is not key=value"; return r; }
                r.Add(new KeyValuePair<string, string>(w.Substring(0, eq), w.Substring(eq + 1)));
            }
            return r;
        }

        /// <summary>The env defaults: <see cref="EnvHorizon"/> (on/off), <see cref="EnvHaze"/> (pairs) and <see cref="ImpostorRules.EnvImpostor"/>.</summary>
        public static AltitudeSettings FromEnv(string horizon, string haze, string impostor, out string error)
        {
            error = null;
            var s = new AltitudeSettings();
            bool b;
            if (!string.IsNullOrEmpty(horizon))
            {
                if (TryOnOff(horizon, out b)) s.Horizon = b;
                else error = EnvHorizon + "='" + horizon + "' is not on|off (default on kept)";
            }
            if (!string.IsNullOrEmpty(impostor))
            {
                if (TryOnOff(impostor, out b)) s.Impostors = b;
                else error = (error != null ? error + "; " : "") + ImpostorRules.EnvImpostor + "='" + impostor + "' is not on|off (default on kept)";
            }
            if (!string.IsNullOrEmpty(haze))
            {
                string e;
                List<KeyValuePair<string, string>> kv = ParseKv(haze, out e);
                if (e == null)
                {
                    AltitudeSettings trial = s.Clone();
                    e = trial.Apply(kv);
                    if (e == null) s = trial;
                }
                if (e != null) error = (error != null ? error + "; " : "") + EnvHaze + ": " + e + " (the table kept)";
            }
            return s;
        }
    }

    /// <summary>
 /// T3 (Subnautica half): a grid SE's producer flags as an impostor (far above the picture's camera, beyond the normal
 /// mirror radius) is built coarse: chunked parts of at least <c>minPart</c> x the block size only (no greebles), one
 /// flat tinted material per colour (<see cref="MaterialKey"/>: no textures), no shadows, no Child parts (subparts,
 /// doors, rotor tops), no lights, no effects; its cells are posted at most <c>perFrame</c> per frame (the rest stay
 /// dirty: <c>cellsDeferred</c>). A GridAdd that clears the bit (the grid came within the normal radius) rebuilds every
 /// cell in full, cell by cell (the coarse chunks of a cell stay until its full build replaces them).
 /// </summary>
    internal static class ImpostorRules
    {
        public const string EnvImpostor = "DEEPENGINEERS_S7T_IMPOSTOR";

        /// <summary>
 /// C4 <c>GridAdd.flags</c> bit 6 (= 64) <c>Impostor</c>, read only on a version-1 GridAdd: the first free bit after
 /// S5's 0-2, S7a's Ghost 3, S7p's Projection 4 and Missile 5 (the version nibble is 28-31); the SE producer's constant
 /// (Wire <see cref="S7tImpostorRules.GridFlagImpostor"/>, S7t-se-composite). CONTRACT 5.4 GridAdd line.
 /// </summary>
        public const uint ImpostorGridFlag = S7tImpostorRules.GridFlagImpostor;

        /// <summary>The reserved material key of an impostor chunk ("DES7tIMP"): no MaterialDef, a flat colour, never a texture.</summary>
        public const ulong MaterialKey = 0x4445537374494D50UL;

        public const double DefaultMinPartFrac = 0.75;
        public const int DefaultCellsPerFrame = 4;
        /// <summary>The vertical reach of the impostors above the camera: SE's leave height (ROADMAP T3 "out to ~5 km up";
 /// Wire <see cref="S7tImpostorRules.UpLeaveM"/> 5,500 m, admitted at 5,000 m).</summary>
        public const double DefaultReachM = S7tImpostorRules.UpLeaveM;
        /// <summary>The far plane while impostors exist above the sea (they must not be clipped at 1.7 km): above the cull
 /// distance sqrt(1,600^2 + 5,500^2) + 100 = 5,828 m.</summary>
        public const double DefaultFarM = 6000.0;

        public static bool IsImpostor(uint gridFlags) { return S7tImpostorRules.IsImpostor(gridFlags); }

        /// <summary>The edge of one block of the grid (2.5 m large, 0.5 m small).</summary>
        public static double BlockSizeM(uint targetClass) { return targetClass == GridClass.SmallGrid ? 0.5 : 2.5; }

        /// <summary>A part enters the impostor when its model's largest extent is at least <paramref name="minFrac"/> x the block size.</summary>
        public static bool KeepPart(double extentX, double extentY, double extentZ, uint targetClass, double minFrac)
        {
            double e = Math.Max(extentX, Math.Max(extentY, extentZ));
            if (double.IsNaN(e)) return false;
            return e >= minFrac * BlockSizeM(targetClass);
        }

        /// <summary>
 /// The impostors' cull distance from the camera: above the sea under the Site policy the normal leave radius and the
 /// vertical reach combined (sqrt(1,600^2 + reach^2) + 100 m), else the caller's under-water distance.
 /// </summary>
        public static double CullM(bool pictureSite, double camY, double reachM, double underwaterM)
        {
            if (!(pictureSite && camY > 0.0)) return underwaterM;
            double h = S7tImpostorRules.HorizontalLeaveM; // = S7aConventions.Limits.MirrorLeaveAboveWaterM
            return Math.Sqrt(h * h + reachM * reachM) + 100.0;
        }

        /// <summary>The per-frame cap: may another impostor cell be posted after <paramref name="posted"/> this frame?</summary>
        public static bool MayPost(int posted, int cap) { return cap > 0 && posted < cap; }
    }
}
