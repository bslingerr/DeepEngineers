using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S7p-character (ROADMAP "S7p Polish pass" P8, P9, P10
 // character polish, Unity-free and compiled into DeepEngineers.SN.Tests (replay-exact, no game types).
 // - P8 "first person it's still SN hands and shadow": the first-person part rule (SE's HiddenInFirstPerson parts are
 // shadow-only, as SE's own first person draws them), fpbody=se by default, S5a's diver arms forced off under it.
 // - P9 "in third person it's another character from my original": SE's own paint (ColorizeGray of the material pixel
 // path) evaluated per texel on the CPU, METALNESS_COLORABLE as SE sets it for every character.
 // - P10 "the light of my suit is absolutely blinding specially at night": a per-kind intensity/range scale on the C2
 // light rows (character spot, character point, block spot, block point), defaults from SE's own intensities.
 // =================================================================================================================

 /// <summary>How one character part is drawn this frame (P8).</summary>
    internal enum PartShow { Hidden = 0, Drawn = 1, ShadowOnly = 2 }

    /// <summary>
 /// P8 first person (S7p-character). VERIFIED in first person SE calls
 /// <c>MyCharacter.EnableHead(false)</c>, which sends
 /// <c>UpdateModelProperties(id, material, add 0, remove Visible)</c> for every <c>MaterialsDisabledIn1st</c> material
 /// (<c>UpdateHeadModelProperties</c>); the renderer maps "not Visible" to <c>SkipInMainView | SkipInForward</c>
 /// (<c>MyProxiesFactory.GetRenderableProxyFlags</c>) and its shadow views skip only <c>SkipInDepth</c> proxies
 /// (<c>MyGeometryRendererOld</c> view filter): the head is not seen but still casts its shadow. The body keeps drawing
 /// (and casting) as in third person. Unity: Drawn = enabled + <c>ShadowCastingMode.On</c>, ShadowOnly = enabled +
 /// <c>ShadowsOnly</c>, Hidden = disabled.
 /// </summary>
    internal static class S7pFirstPerson
    {
        /// </summary>
        public const FpBody FpBodyDefault = FpBody.Se;

        /// <summary><c>DEEPENGINEERS_SN_FPBODY</c> (se|diver) or the default <see cref="FpBodyDefault"/>; <paramref name="note"/> says why.</summary>
        public static FpBody FromEnvironmentValue(string value, out string note)
        {
            if (string.IsNullOrEmpty(value)) { note = "default " + S5Switches.Name(FpBodyDefault) + " (S7p)"; return FpBodyDefault; }
            FpBody b;
            if (S5Switches.TryParseFpBody(value, out b)) { note = S5Switches.FpBodyVar + "=" + value; return b; }
            note = S5Switches.FpBodyVar + "='" + value + "' not understood (se|diver): " + S5Switches.Name(FpBodyDefault);
            return FpBodyDefault;
        }

        /// <summary>
 /// The part rule: third person (any camera but SE's first person) = Drawn; first person with fpbody=diver = Hidden (S5a's
 /// diver arms are the body then); first person with fpbody=se = Drawn, and ShadowOnly for HiddenInFirstPerson parts.
 /// </summary>
        public static PartShow Show(uint partFlags, uint cameraMode, FpBody fp)
        {
            if (cameraMode != CameraMode.FirstPerson) return PartShow.Drawn;
            if (fp == FpBody.Diver) return PartShow.Hidden;
            return (partFlags & S5Conv.HiddenInFirstPerson) != 0 ? PartShow.ShadowOnly : PartShow.Drawn;
        }

        /// <summary>
 /// S5a's diver arms (view model, mask, their shadow) are allowed with fpbody=diver, and under fpbody=se only while SE
 /// has sent no character at all (<paramref name="seCharacterKnown"/> false: S5 off on SE, an SE before S5, the export
 /// failed with no fallback): then they are the only first-person body there is (the S5a regression sessions run so).
 /// Whenever SE's astronaut exists they stay off, also in the frames its body is not drawn (materials loading, a C1
 /// frame without the character): the old rule brought the diver back in exactly those frames.
 /// </summary>
        public static bool DiverArmsAllowed(FpBody fp, bool seCharacterKnown) { return fp == FpBody.Diver || !seCharacterKnown; }

        /// <summary>The S5a arms gate the S5 host keeps: ForceOff under fpbody=se once SE's astronaut is known, the user's gate otherwise.</summary>
        public static ArmsGate GateFor(FpBody fp, ArmsGate userGate, bool seCharacterKnown)
        {
            return DiverArmsAllowed(fp, seCharacterKnown) ? userGate : ArmsGate.ForceOff;
        }

        public static string Name(PartShow s)
        {
            switch (s)
            {
                case PartShow.Drawn: return "drawn";
                case PartShow.ShadowOnly: return "shadowOnly";
                default: return "hidden";
            }
        }

        /// <summary>
 /// The P8 symptom the <c>s7p.char</c> row carries: under fpbody=se in SE's first person, with SE's astronaut known,
 /// nothing of Subnautica's own player may be drawn (no diver arms, no enabled Player renderer); true = the frame is wrong.
 /// </summary>
        public static bool SnBodyLeak(FpBody fp, bool seCharacterKnown, bool firstPerson, bool diverArmsShown, int snPlayerRenderersOn)
        {
            return !DiverArmsAllowed(fp, seCharacterKnown) && firstPerson && (diverArmsShown || snPlayerRenderersOn > 0);
        }
    }

    /// <summary>One texel bake's counters (P9).</summary>
    internal struct PaintBakeStats
    {
        public int Texels, Painted, IllumTexels;
        public double MeanColoring;
    }

    /// <summary>
 /// P9 (S7p-character): SE's own paint of a character material, per texel. VERIFIED in SE's shader sources
 /// (<c>Content\Shaders\Geometry\Materials\PixelUtilsMaterials.hlsli</c>, read in place, never copied):
 /// <c>FeedOutputInternal</c>: coloring = extras.w (the <c>_add</c> alpha), reduced on metal by
 /// <c>RemoveMetalnessFromColoring</c> only <c>#ifndef METALNESS_COLORABLE</c>; base_color =
 /// <c>ColorizeGray(cm.rgb, key_color, coloring)</c>; <c>ColorizeGray</c>: hsv = rgb_to_hsv(rgb_to_srgb(texel)),
 /// hsv.xy = 0, finalHsv = saturate(hsv + keyHSV), texel = lerp(texel, srgb_to_rgb(hsv_to_rgb(finalHsv)), coloring)
 /// (<c>Math\Color.hlsli</c> for the four helpers). The key colour is the character's <c>ColorMaskHsv</c> unchanged
 /// (<c>MyRender11.ColorFromMask</c> returns its argument) and the character is METALNESS_COLORABLE
 /// (<c>MyCharacter</c> constructor: <c>Render.MetalnessColorable = true</c>; shader combos
 /// <c>USE_SKINNING|METALNESS_COLORABLE</c>). The debug offset <c>frame_.TextureDebugMultipliers.ColorizeHSV</c> is taken
 /// as 0 (HYPOTHESIS: a debug-only multiplier). The previous Subnautica mapping (one <c>_Color</c> tint under UWE_3COLOR
 /// computed on SE's 144 grey, metal reduction always on) is S3.C13's ΔE ≈ 17.
 /// S7t-colour-mask: that HYPOTHESIS is refuted: the run-time <c>ColorizeHSV</c> is
 /// <c>MyTextureDebugMultipliers.Defaults</c> (0, 0.8, -0.1) (Environment.sbc sets only GlossMultiplier). The hue/sat/val
 /// arguments of these helpers are the shader key; callers pass <see cref="ColorMaskRules.ShaderKey"/> (or call
 /// <see cref="ColorMaskRules.Bake"/>), as S7pCharacterPaint and the block paint now do.
 /// </summary>
    internal static class S7pPaint
    {
        private static readonly double[] SrgbByteToLinear = BuildByteLut();
        /// <summary>Linear 0..1 → sRGB byte, 16,384 steps (a step is below half an sRGB byte above linear 0.0005).</summary>
        private const int LinLutSize = 16384;
        private static readonly byte[] LinearToSrgbByteLut = BuildLinLut();

        private static double[] BuildByteLut()
        {
            var t = new double[256];
            for (int i = 0; i < 256; i++) t[i] = SrgbToRgb(i / 255.0);
            return t;
        }

        private static byte[] BuildLinLut()
        {
            var t = new byte[LinLutSize + 1];
            for (int i = 0; i <= LinLutSize; i++) t[i] = TextureRepack.ToByte(RgbToSrgb((double)i / LinLutSize));
            return t;
        }

        public static double Saturate(double x) { return x < 0 ? 0 : (x > 1 ? 1 : (double.IsNaN(x) ? 0 : x)); }

        private static double Frac(double x) { return x - Math.Floor(x); }

        /// <summary>SE's <c>hsv_to_rgb</c> (Math\Color.hlsli): p = |frac(h + K.xyz)·6 − 3|; rgb = v · lerp(1, saturate(p − 1), s).</summary>
        public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            double pr = Math.Abs(Frac(h + 1.0) * 6.0 - 3.0);
            double pg = Math.Abs(Frac(h + 2.0 / 3.0) * 6.0 - 3.0);
            double pb = Math.Abs(Frac(h + 1.0 / 3.0) * 6.0 - 3.0);
            r = v * (1.0 + (Saturate(pr - 1.0) - 1.0) * s);
            g = v * (1.0 + (Saturate(pg - 1.0) - 1.0) * s);
            b = v * (1.0 + (Saturate(pb - 1.0) - 1.0) * s);
        }

        /// <summary>SE's <c>srgb_to_rgb</c>.</summary>
        public static double SrgbToRgb(double c) { return c <= 0.04045 ? c / 12.92 : Math.Pow((Math.Abs(c) + 0.055) / 1.055, 2.4); }

        /// <summary>SE's <c>rgb_to_srgb</c>.</summary>
        public static double RgbToSrgb(double c) { return c <= 0.0031308 ? c * 12.92 : Math.Pow(Math.Abs(c), 1.0 / 2.4) * 1.055 - 0.055; }

        /// <summary>The coloring factor of a texel: the paint mask, metal-reduced unless the part is metalness-colourable.</summary>
        public static double Coloring(double paint, double metal, bool metalnessColorable)
        {
            paint = Saturate(paint);
            return metalnessColorable ? paint : TextureRepack.PaintOnMetal((float)paint, (float)metal);
        }

        /// <summary>
 /// The colourised colour (linear RGB) of a texel whose sRGB value (max channel) is <paramref name="valueSrgb"/>:
 /// srgb_to_rgb(hsv_to_rgb(saturate(H), saturate(S), saturate(V + valueSrgb))).
 /// </summary>
        public static void Colourised(double valueSrgb, float hue, float sat, float val, out double r, out double g, out double b)
        {
            double sr, sg, sb;
            HsvToRgb(Saturate(hue), Saturate(sat), Saturate(valueSrgb + val), out sr, out sg, out sb);
            r = SrgbToRgb(sr);
            g = SrgbToRgb(sg);
            b = SrgbToRgb(sb);
        }

        /// <summary>
 /// SE's <c>ColorizeGray</c> for one texel given in linear RGB (as SE samples an sRGB texture): the result in linear
 /// RGB. <paramref name="coloring"/> ≤ 0 returns the texel unchanged (SE's own early-out).
 /// </summary>
        public static void ColorizeGray(double r, double g, double b, float hue, float sat, float val, double coloring, out double or, out double og, out double ob)
        {
            or = r; og = g; ob = b;
            if (!(coloring > 0)) return;
            double v = Math.Max(RgbToSrgb(r), Math.Max(RgbToSrgb(g), RgbToSrgb(b)));
            double cr, cg, cb;
            Colourised(v, hue, sat, val, out cr, out cg, out cb);
            or = r + (cr - r) * coloring;
            og = g + (cg - g) * coloring;
            ob = b + (cb - b) * coloring;
        }

        /// <summary>sRGB byte → linear (exact, table).</summary>
        public static double LinearOf(byte srgb) { return SrgbByteToLinear[srgb]; }

        /// <summary>Linear 0..1 → sRGB byte (table, clamped).</summary>
        public static byte SrgbByteOf(double linear)
        {
            if (!(linear > 0)) return 0;
            if (linear >= 1) return 255;
            return LinearToSrgbByteLut[(int)(linear * LinLutSize + 0.5)];
        }

        /// <summary>
 /// Bakes SE's paint into a level-0 RGBA8 sRGB image (worker thread). <paramref name="cmSrgb"/>: the <c>_cm</c> texels
 /// as sRGB bytes (RGB) with the metalness in A; <paramref name="add"/>: the <c>_add</c> texels (A = the paint mask,
 /// G = the emissive mask); both <paramref name="texels"/> × 4 bytes. The output keeps the <c>_cm</c> alpha; texels
 /// with no coloring are copied byte for byte. <paramref name="illum"/>, when non-null on entry, receives SE's
 /// emissive colour of the painted albedo (RGB = sRGB(base × (add.g − 1/255)), A 0; the same rule as the S3 repack's
 /// <c>_Illum</c>, now on the coloured base), else is left alone.
 /// </summary>
        public static byte[] Bake(byte[] cmSrgb, byte[] add, int texels, float hue, float sat, float val, bool metalnessColorable, byte[] illum, out PaintBakeStats stats)
        {
            if (cmSrgb == null || cmSrgb.Length < texels * 4) throw new ArgumentException("colour-metal texels missing or short");
            if (add == null || add.Length < texels * 4) throw new ArgumentException("add-map texels missing or short");
            if (illum != null && illum.Length < texels * 4) throw new ArgumentException("illum buffer short");
            // the colourised colour depends on the texel's sRGB value only (H and S are replaced): one entry per byte value
            var tr = new double[256];
            var tg = new double[256];
            var tb = new double[256];
            for (int i = 0; i < 256; i++) Colourised(i / 255.0, hue, sat, val, out tr[i], out tg[i], out tb[i]);
            var output = new byte[texels * 4];
            stats = new PaintBakeStats { Texels = texels };
            double sum = 0;
            for (int t = 0; t < texels; t++)
            {
                int o = t * 4;
                byte sr = cmSrgb[o], sg = cmSrgb[o + 1], sb = cmSrgb[o + 2], metal = cmSrgb[o + 3];
                double coloring = Coloring(add[o + 3] / 255.0, metal / 255.0, metalnessColorable);
                double lr = SrgbByteToLinear[sr], lg = SrgbByteToLinear[sg], lb = SrgbByteToLinear[sb];
                if (coloring > 0)
                {
                    int vmax = sr > sg ? (sr > sb ? sr : sb) : (sg > sb ? sg : sb);
                    lr += (tr[vmax] - lr) * coloring;
                    lg += (tg[vmax] - lg) * coloring;
                    lb += (tb[vmax] - lb) * coloring;
                    output[o] = SrgbByteOf(lr);
                    output[o + 1] = SrgbByteOf(lg);
                    output[o + 2] = SrgbByteOf(lb);
                    stats.Painted++;
                    sum += coloring;
                }
                else
                {
                    output[o] = sr;
                    output[o + 1] = sg;
                    output[o + 2] = sb;
                }
                output[o + 3] = metal;
                if (illum != null)
                {
                    double em = Math.Max(0.0, add[o + 1] / 255.0 - TextureRepack.EmissiveNoise);
                    illum[o] = SrgbByteOf(lr * em);
                    illum[o + 1] = SrgbByteOf(lg * em);
                    illum[o + 2] = SrgbByteOf(lb * em);
                    illum[o + 3] = 0;
                    if (em > 0) stats.IllumTexels++;
                }
            }
            stats.MeanColoring = stats.Painted > 0 ? sum / stats.Painted : 0;
            return output;
        }

        /// <summary>The bake's cache key: one bake per (texture set, colour, metalness-colourable).</summary>
        public static string BakeKey(string setKey, long colourKey, bool metalnessColorable)
        {
            return (setKey ?? "") + "#" + colourKey.ToString("x", CultureInfo.InvariantCulture) + (metalnessColorable ? "#mc" : "#m");
        }

        public const string ModeVar = "DEEPENGINEERS_SN_CHAR_PAINT";

        /// <summary><c>bake</c> (default: SE's exact paint baked into the character's albedo) or <c>approx</c> (the S3 UWE_3COLOR tint).</summary>
        public static bool TryParseMode(string s, out bool bake)
        {
            bake = true;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "bake": case "se": case "on": bake = true; return true;
                case "approx": case "tint": case "off": bake = false; return true;
                default: return false;
            }
        }

        public static bool ModeFromEnvironmentValue(string value, out string note)
        {
            bool b;
            if (string.IsNullOrEmpty(value)) { note = "default bake (S7p)"; return true; }
            if (TryParseMode(value, out b)) { note = ModeVar + "=" + value; return b; }
            note = ModeVar + "='" + value + "' not understood (bake|approx): bake";
            return true;
        }
    }

    /// <summary>The kinds a C2 light row is scaled by (P10): derived from the row's own flags (Character bit 3, Spot bit 0).</summary>
    internal enum LightKind { BlockPoint = 0, BlockSpot = 1, CharacterPoint = 2, CharacterSpot = 3 }

    /// <summary>
 /// P10 (S7p-character): Unity intensity = SE intensity × the global <c>de.lights</c> scale × the kind's scale; range the
 /// same with the range scales. SE's own values: the suit lamp <c>MyRenderComponentCharacter.InitLight</c>
 /// / <c>UpdateLightPower</c>: reflector <c>MyCharacter.REFLECTOR_INTENSITY</c> 25 × light power, range 35 m, cone cos
 /// 0.373 (102° full cone), point <c>POINT_LIGHT_INTENSITY</c> 0.5, range <c>POINT_LIGHT_RANGE</c> 1.08 m; spotlights
 /// <c>MyReflectorLight.UpdateIntensity</c> / <c>MySearchlight</c>: reflector = intensity × 8 (default intensity 1 → 8,
 /// range 120 m), point = intensity × 0.3; interior lights <c>MyInteriorLight.UpdateIntensity</c>: intensity × 2 (default
 /// 5 → 10, range 3.6 m). SE shows them through its eye adaptation; Subnautica has none, so a Unity intensity of 25 at
 /// 35 m whites the whole cone out at night. Defaults: the two long-range spots (the suit lamp and the spotlights) land
 /// at Unity intensity 1.0 at SE's default setting (HYPOTHESIS: Subnautica's own hand and vehicle lamps sit near 1; the
 /// s7 lightScale</c>); the short-range point lights keep 1.0 (Unity's attenuation at 1–4 m is
 /// already low, and S7.N3 calibrates the block points through the global scale against the Seamoth cabin light).
 /// </summary>
    internal static class S7pLightScale
    {
        public const string EnvVar = "DEEPENGINEERS_SN_LIGHT_SCALE";
        public const int Kinds = 4;
        public const float MinScale = 0.001f, MaxScale = 100f;

        /// <summary>SE's default intensity per kind (above), for the docs, the status and the tests.</summary>
        public static readonly float[] SeDefaultIntensity = { 10f, 8f, 0.5f, 25f };
        /// <summary>
 /// Default intensity scale per kind: block point 1/10, block spot 1/8, character point 1, character spot 1/25. The block
 /// point 1.0 was the HYPOTHESIS above
 /// out the whole hull at night: "the lighting is broken in SN, I'm blinded") settled it: SE's interior light row carries 10
 /// (5 × 2) and Unity shows 10 at 1–3 m as pure white with Subnautica's bloom; 0.1 lands it at 1.0 like the other kinds.
 /// </summary>
        public static readonly float[] DefaultIntensity = { 0.1f, 0.125f, 1f, 0.04f };
        /// <summary>Default range scale per kind: block spots at half SE's 120 m (Subnautica's own headlights reach ~60 m); the rest SE's.</summary>
        public static readonly float[] DefaultRange = { 1f, 0.5f, 1f, 1f };
        /// <summary>

 /// high in SE (intensity up to 10 → reflector 80) lands in Subnautica, which has no eye adaptation, as a blinding cone up close.
 /// Unity intensity ceiling per kind, applied after the scales (the live scales still tune below it).
 /// </summary>
        public static readonly float[] MaxIntensity = { 1.0f, 1.2f, 1.0f, 1.2f };

        public static LightKind KindOf(uint flags)
        {
            bool character = S7LightRow.IsCharacter(flags), spot = S7LightRow.IsSpot(flags);
            return character ? (spot ? LightKind.CharacterSpot : LightKind.CharacterPoint) : (spot ? LightKind.BlockSpot : LightKind.BlockPoint);
        }

        public static string Name(LightKind k)
        {
            switch (k)
            {
                case LightKind.BlockSpot: return "blockSpot";
                case LightKind.CharacterPoint: return "characterPoint";
                case LightKind.CharacterSpot: return "characterSpot";
                default: return "blockPoint";
            }
        }

        /// </summary>
        public static bool TryParseKind(string s, out LightKind k)
        {
            k = LightKind.BlockPoint;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "blockpoint": case "interior": case "point": k = LightKind.BlockPoint; return true;
                case "blockspot": case "spotlight": case "spotlights": case "spot": k = LightKind.BlockSpot; return true;
                case "characterpoint": case "suitpoint": k = LightKind.CharacterPoint; return true;
                case "characterspot": case "character": case "suit": case "helmet": k = LightKind.CharacterSpot; return true;
                default: return false;
            }
        }

        public static bool ValidScale(double v) { return v >= MinScale && v <= MaxScale && !double.IsNaN(v); }

        /// <summary>Unity intensity of a row (never negative; the Wire rule clamps NaN/∞).</summary>
        public static float Intensity(float seIntensity, float globalScale, float kindScale) { return LightPoolRules.Intensity(seIntensity, globalScale * kindScale); }

        /// <summary>The Unity intensity of a row of a kind: the scales, then the kind's ceiling (<see cref="MaxIntensity"/>).</summary>
        public static float Intensity(LightKind kind, float seIntensity, float globalScale, float kindScale)
        {
            return Math.Min(MaxIntensity[(int)kind], Intensity(seIntensity, globalScale, kindScale));
        }

        /// <summary>Unity range of a row (≥ 0.01 m).</summary>
        public static float Range(float seRange, float globalScale, float kindScale) { return LightPoolRules.Range(seRange, globalScale * kindScale); }

        public static void Reset(float[] intensity, float[] range)
        {
            Array.Copy(DefaultIntensity, intensity, Kinds);
            Array.Copy(DefaultRange, range, Kinds);
        }

        /// <summary>
 /// The environment form: <c>kind=intensity[:range][,kind=…]</c> (e.g. <c>characterSpot=0.04,blockSpot=0.125:0.8</c>);
 /// applied in order onto the arrays; false with the first error (entries before it are kept).
 /// </summary>
        public static bool TryParseList(string text, float[] intensity, float[] range, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text)) return true;
            foreach (string raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim();
                if (item.Length == 0) continue;
                int eq = item.IndexOf('=');
                if (eq <= 0) { error = "'" + item + "': kind=intensity[:range]"; return false; }
                LightKind k;
                if (!TryParseKind(item.Substring(0, eq), out k)) { error = "'" + item.Substring(0, eq) + "': kind blockPoint|blockSpot|characterPoint|characterSpot"; return false; }
                string val = item.Substring(eq + 1);
                string rv = null;
                int colon = val.IndexOf(':');
                if (colon >= 0) { rv = val.Substring(colon + 1); val = val.Substring(0, colon); }
                double iv, rr = double.NaN;
                if (!double.TryParse(val.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out iv) || !ValidScale(iv)) { error = "'" + item + "': intensity scale in [" + MinScale + ", " + MaxScale + "]"; return false; }
                if (rv != null && (!double.TryParse(rv.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rr) || !ValidScale(rr))) { error = "'" + item + "': range scale in [" + MinScale + ", " + MaxScale + "]"; return false; }
                intensity[(int)k] = (float)iv;
                if (rv != null) range[(int)k] = (float)rr;
            }
            return true;
        }

        /// <summary>
 /// The live command's tokens (<c>de.s7 lightScale &lt;kind&gt; &lt;factor&gt; [range]</c>, <c>status</c>, <c>reset</c>), as the chat
 /// relay (<c>/de sn s7 lightScale suit 0.04</c> → argv) or a C11 string gives them. <paramref name="action"/> = set,
 /// status or reset; <paramref name="range"/> NaN when not given.
 /// </summary>
        public static bool TryParseCommand(IList<string> tokens, out string action, out LightKind kind, out double factor, out double range, out string error)
        {
            action = "status";
            kind = LightKind.BlockPoint;
            factor = double.NaN;
            range = double.NaN;
            error = null;
            if (tokens == null || tokens.Count == 0) return true;
            string first = (tokens[0] ?? "").Trim().ToLowerInvariant();
            if (first == "status" || first == "") return true;
            if (first == "reset" || first == "default" || first == "defaults") { action = "reset"; return true; }
            if (!TryParseKind(tokens[0], out kind)) { error = "lightScale: kind blockPoint|blockSpot|characterPoint|characterSpot (or suit|spotlight|interior), then a factor"; return false; }
            if (tokens.Count < 2 || !double.TryParse((tokens[1] ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out factor) || !ValidScale(factor))
            {
                error = "lightScale " + Name(kind) + ": a factor in [" + MinScale + ", " + MaxScale + "]";
                return false;
            }
            if (tokens.Count > 2)
            {
                if (!double.TryParse((tokens[2] ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out range) || !ValidScale(range))
                {
                    error = "lightScale " + Name(kind) + ": the range scale in [" + MinScale + ", " + MaxScale + "]";
                    return false;
                }
            }
            action = "set";
            return true;
        }

        public static string Describe(float[] intensity, float[] range)
        {
            var sb = new StringBuilder(128);
            for (int k = 0; k < Kinds; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append(Name((LightKind)k)).Append('=').Append(intensity[k].ToString("0.####", CultureInfo.InvariantCulture));
                if (range[k] != 1f) sb.Append(':').Append(range[k].ToString("0.####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}

namespace DeepEngineers.SN
{
    /// <summary>

 /// character and its held tool never cast from the character's own lamps, SE's own rule
 /// The S13 near plane (1 m) clipped the helmet and
 /// the arms only: standing or swimming above the seabed and looking down, the trunk and the legs (1 to 1.7 m from the
 /// lamp at the helmet) still cast, and the whole lit disc under the player was the character's own black silhouette.
 /// Unity's only per-light caster filter is the light's culling mask, so the character's renderers live on a layer of
 /// their own (the highest unnamed user layer the main camera renders) that the character-kind lights exclude; block
 /// lights and the sun still light and shadow the character. The price: the suit lamp no longer lights the body either
 /// (the built-in deferred path has no shadow-only exclusion); the body sits behind the lamp except the hands and,
 /// looking down, the legs. Deferred honours at most 4 excluded layers across the scene (Unity manual, rendering
 /// paths): one here. The main camera renders the layer because the runtime keeps its bit set in the camera's mask.
 /// <c>DEEPENGINEERS_S7P_SELFSHADOW=off</c> restores layer 0 (the lamp lights and shadows the body).
 /// </summary>
    internal static class S7pSelfShadowRules
    {
        /// <summary>
 /// The character layer: the highest unnamed user layer (31 down to 8); -1 when none. The main camera's mask is not a
 /// filter: Subnautica's leaves the unnamed layers out, so the runtime sets the layer's bit on it (22:5x, the character
 /// had vanished with the layer picked unchecked before the camera existed).
 /// </summary>
        public static int PickLayer(Func<int, string> layerName)
        {
            for (int i = 31; i >= 8; i--)
            {
                string n;
                try { n = layerName(i); } catch (Exception) { continue; }
                if (string.IsNullOrEmpty(n)) return i;
            }
            return -1;
        }

        /// <summary>The culling mask of a character-kind light: everything but the character layer (everything when there is none).</summary>
        public static int LampMask(int layer) { return layer < 8 || layer > 31 ? -1 : ~(1 << layer); }

        /// <summary>The Unity layer a character renderer gets: the picked layer, or Default (0) when there is none.</summary>
        public static int RendererLayer(int layer) { return layer < 8 || layer > 31 ? 0 : layer; }

        public static bool IsCharacterKind(LightKind k) { return k == LightKind.CharacterPoint || k == LightKind.CharacterSpot; }
    }
}
