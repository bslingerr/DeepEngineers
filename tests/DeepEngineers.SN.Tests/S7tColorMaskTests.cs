using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7t-colour-mask (ROADMAP S7t T6), Subnautica side, Unity-free parts. SE's colour mask replayed against a literal
 /// transcription of the game's shader (Content\Shaders\Geometry\Materials\PixelUtilsMaterials.hlsli FeedOutputInternal,
 /// RemoveMetalnessFromColoring, ColorizeGray; Content\Shaders\Math\Color.hlsli rgb_to_hsv (chilliant), hsv_to_rgb,
 /// srgb_to_rgb, rgb_to_srgb) with SE's run-time ColorizeHSV (0, 0.8, -0.1) (MyTextureDebugMultipliers.Defaults); the
 /// cross-check with SE's own colour picker (MyColorPickerConstants.HSVOffsetToHSV)
 /// yellow, a UI pink: grey under the old formula, their hue now); the bake against the per-texel replay; bake sizes,
 /// budget, eviction, the metalness rule; the de.s7t mask grammar and the environment.
 /// </summary>
    [TestClass]
    public class S7tColorMaskTests
    {
        // ------------------------------------------------------------------------------------------------ HLSL, literally

        private static readonly double[] SeColorizeHsv = { 0.0, 0.8, -0.1 };

        private static double Frac(double x) { return x - Math.Floor(x); }
        private static double Saturate(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }
        private static double Step(double edge, double x) { return x >= edge ? 1 : 0; }
        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary><c>hsv_to_rgb</c>: K = (1, 2/3, 1/3, 3); p = abs(frac(hsv.xxx + K.xyz) * 6 - K.www); hsv.z * lerp(K.xxx, saturate(p - K.xxx), hsv.y).</summary>
        private static double[] HlslHsvToRgb(double[] hsv)
        {
            double[] kxyz = { 1.0, 2.0 / 3.0, 1.0 / 3.0 };
            var o = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double p = Math.Abs(Frac(hsv[0] + kxyz[i]) * 6.0 - 3.0);
                o[i] = hsv[2] * Lerp(1.0, Saturate(p - 1.0), hsv[1]);
            }
            return o;
        }

        /// <summary><c>rgb_to_hsv</c> (chilliant), every lerp/step as written.</summary>
        private static double[] HlslRgbToHsv(double[] rgb)
        {
            double r = rgb[0], g = rgb[1], b = rgb[2];
            double[] K = { 0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0 };
            double s1 = Step(b, g);
            // p = lerp(float4(rgb.bg, K.wz), float4(rgb.gb, K.xy), step(rgb.b, rgb.g))
            double[] p = { Lerp(b, g, s1), Lerp(g, b, s1), Lerp(K[3], K[0], s1), Lerp(K[2], K[1], s1) };
            double s2 = Step(p[0], r);
            // q = lerp(float4(p.xyw, rgb.r), float4(rgb.r, p.yzx), step(p.x, rgb.r))
            double[] q = { Lerp(p[0], r, s2), Lerp(p[1], p[1], s2), Lerp(p[3], p[2], s2), Lerp(r, p[0], s2) };
            double d = q[0] - Math.Min(q[3], q[1]);
            double e = 1.0e-10;
            return new[] { Math.Abs(q[2] + (q[3] - q[1]) / (6.0 * d + e)), d / (q[0] + e), q[0] };
        }

        private static double HlslSrgbToRgb(double c) { return c <= 0.04045 ? c / 12.92 : Math.Pow((Math.Abs(c) + 0.055) / 1.055, 2.4); }
        private static double HlslRgbToSrgb(double c) { return c <= 0.0031308 ? c * 12.92 : Math.Pow(Math.Abs(c), 1.0 / 2.4) * 1.055 - 0.055; }

        private static double HlslRemoveMetalnessFromColoring(double metalness, double coloring)
        {
            const double threshold = 0.4, thresholdMultiply = 0.5;
            return coloring * Saturate(1 - Saturate(metalness - threshold) / ((1 - threshold) * thresholdMultiply));
        }

        private static double[] HlslColorizeGray(double[] texel, double[] coloringHsv, double coloringFactor)
        {
            if (!(coloringFactor > 0)) return (double[])texel.Clone();
            var key = new double[3];
            for (int i = 0; i < 3; i++) key[i] = coloringHsv[i] + SeColorizeHsv[i]; // coloringHSV += frame_.TextureDebugMultipliers.ColorizeHSV.rgb
            double[] hsv = HlslRgbToHsv(new[] { HlslRgbToSrgb(texel[0]), HlslRgbToSrgb(texel[1]), HlslRgbToSrgb(texel[2]) });
            hsv[0] = 0;
            hsv[1] = 0;
            var fin = new double[3];
            for (int i = 0; i < 3; i++) fin[i] = Saturate(hsv[i] + key[i]);
            double[] rgb = HlslHsvToRgb(fin);
            var o = new double[3];
            for (int i = 0; i < 3; i++) o[i] = Lerp(texel[i], HlslSrgbToRgb(rgb[i]), coloringFactor);
            return o;
        }

        /// <summary><c>FeedOutputInternal</c>'s base colour: coloring = extras.w, metal-reduced unless METALNESS_COLORABLE.</summary>
        private static double[] HlslBaseColour(double[] cmLinear, double metal, double paint, double[] keyColor, bool metalnessColorable)
        {
            double coloring = paint;
            if (!metalnessColorable) coloring = HlslRemoveMetalnessFromColoring(metal, coloring);
            return HlslColorizeGray(cmLinear, keyColor, coloring);
        }

        // SE's own palettes: MyCharacter.m_defaultColors and MyRenderComponentBase.Old*ToHSV (the build defaults)
        private static readonly float[][] SeColours =
        {
            new[] { 0f, -1f, 0f }, new[] { 0f, -0.96f, -0.5f }, new[] { 0.575f, 0.15f, 0.2f }, new[] { 0.333f, -0.33f, -0.05f },
            new[] { 0f, 0f, 0.05f }, new[] { 0f, -0.8f, 0.6f }, new[] { 0.122f, 0.05f, 0.46f },
            new[] { 11f / 90f, -0.1f, 0.26f }, new[] { 0.575f, 0f, 0f }, new[] { 1f / 3f, -0.48f, -0.25f }, new[] { 0f, -0.95f, 0.4f },
            new[] { 0.92f, -0.2f, 0.45f }, new[] { 0.999f, 0.2f, 0.55f }, new[] { 0.5f, -1f, -1f }, new[] { 0.25f, 1f, 1f },
        };

        // ------------------------------------------------------------------------------------------------ the formula

        [TestMethod]
        public void ShaderKey_IsColorMaskHsvPlusSesColorizeHsv_InFloat()
        {
            float kh, ks, kv;
            ColorMaskRules.ShaderKey(0.92f, -0.2f, 0.45f, out kh, out ks, out kv);
            Assert.AreEqual(0.92f, kh);
            Assert.AreEqual(-0.2f + 0.8f, ks, "float add, as the shader");
            Assert.AreEqual(0.45f + -0.1f, kv);
            Assert.AreEqual(0f, ColorMaskRules.ColorizeH);
            Assert.AreEqual(0.8f, ColorMaskRules.ColorizeS);
            Assert.AreEqual(-0.1f, ColorMaskRules.ColorizeV);
        }

        [TestMethod]
        public void Texel_ReplaysSesShader_ForEveryPaletteColour_Coloring_Metal_AndColourableSwitch()
        {
            int[][] texels =
            {
                new[] { 0, 0, 0 }, new[] { 20, 20, 20 }, new[] { 90, 90, 90 }, new[] { 144, 144, 144 }, new[] { 200, 200, 200 }, new[] { 255, 255, 255 },
                new[] { 200, 100, 50 }, new[] { 30, 140, 220 }, new[] { 150, 146, 138 }, new[] { 5, 3, 1 },
            };
            int n = 0;
            foreach (float[] c in SeColours)
                foreach (int[] t in texels)
                    foreach (double paint in new[] { 0.0, 0.25, 1.0 })
                        foreach (double metal in new[] { 0.0, 0.5, 0.9 })
                            foreach (bool mc in new[] { false, true })
                            {
                                double[] lin = { HlslSrgbToRgb(t[0] / 255.0), HlslSrgbToRgb(t[1] / 255.0), HlslSrgbToRgb(t[2] / 255.0) };
                                double[] want = HlslBaseColour(lin, metal, paint, new double[] { c[0], c[1], c[2] }, mc);
                                double r, g, b;
                                ColorMaskRules.Texel(lin[0], lin[1], lin[2], paint, metal, mc, c[0], c[1], c[2], out r, out g, out b);
                                string what = "hsv(" + c[0] + "," + c[1] + "," + c[2] + ") texel(" + t[0] + "," + t[1] + "," + t[2] + ") paint " + paint + " metal " + metal + " mc " + mc;
                                Assert.AreEqual(want[0], r, 1e-6, what + " r");
                                Assert.AreEqual(want[1], g, 1e-6, what + " g");
                                Assert.AreEqual(want[2], b, 1e-6, what + " b");
                                n++;
                            }
            Assert.AreEqual(SeColours.Length * texels.Length * 3 * 3 * 2, n);
        }

        [TestMethod]
        public void GreyAt055_EqualsSesColourPicker_HsvOffsetToHsv()
        {
            // MyColorPickerConstants.HSVOffsetToHSV: (h, clamp(s + 0.8), clamp(v + 0.55 - 0.1)): the picker shows exactly what
 // the shader makes of a texel of sRGB value 0.55 (the offsets' design point), an independent check of (0, 0.8, -0.1)
            var rnd = new Random(7);
            for (int i = 0; i < 500; i++)
            {
                float h = (float)rnd.NextDouble(), s = (float)(rnd.NextDouble() * 2 - 1), v = (float)(rnd.NextDouble() * 2 - 1);
                float kh, ks, kv;
                ColorMaskRules.ShaderKey(h, s, v, out kh, out ks, out kv);
                double r, g, b;
                S7pPaint.Colourised(0.55, kh, ks, kv, out r, out g, out b);
                double[] ui = HlslHsvToRgb(new[] { (double)h, Saturate(s + 0.8), Saturate(v + 0.55 - 0.1) });
                Assert.AreEqual(HlslSrgbToRgb(ui[0]), r, 1e-6, "r " + i);
                Assert.AreEqual(HlslSrgbToRgb(ui[1]), g, 1e-6, "g " + i);
                Assert.AreEqual(HlslSrgbToRgb(ui[2]), b, 1e-6, "b " + i);
            }
        }

        private static void Hsv(byte r, byte g, byte b, out double hueDeg, out double sat, out double val)
        {
            double R = r / 255.0, G = g / 255.0, B = b / 255.0;
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
            val = max;
            sat = max > 0 ? d / max : 0;
            if (d <= 0) { hueDeg = 0; return; }
            double h;
            if (max == R) h = ((G - B) / d) % 6;
            else if (max == G) h = (B - R) / d + 2;
            else h = (R - G) / d + 4;
            hueDeg = h * 60;
            if (hueDeg < 0) hueDeg += 360;
        }

        [TestMethod]
        public void OwnerPair_YellowAndPinkKeepTheirHue_TheOldFormulaMadeThemGrey()
        {
            byte r, g, b;
            double hue, sat, val;
            // SE's build-palette yellow (MyRenderComponentBase.OldYellowToHSV): hue 44°, the UI's saturation 0.7
            ColorMaskRules.GreyPreview(11f / 90f, -0.1f, 0.26f, out r, out g, out b);
            Hsv(r, g, b, out hue, out sat, out val);
            Assert.AreEqual(44.0, hue, 2.0, "yellow hue");
            Assert.AreEqual(0.7, sat, 0.03, "yellow saturation (UI S = s + 0.8)");
            // a UI pink (H 0.92, S 0.6, V 0.9 → offsets 0.92, -0.2, 0.45 by MyColorPickerConstants.HSVToHSVOffset)
            ColorMaskRules.GreyPreview(0.92f, -0.2f, 0.45f, out r, out g, out b);
            Hsv(r, g, b, out hue, out sat, out val);
            Assert.AreEqual(331.2, hue, 2.0, "pink hue");
            Assert.AreEqual(0.6, sat, 0.03, "pink saturation");
            Assert.IsTrue(r > b && b > g, "pink: R > B > G (" + r + "," + g + "," + b + ")");

            double lr, lg, lb;
            S7pPaint.Colourised(ColorMaskRules.StandardGreySrgb, 0.92f, -0.2f, 0.45f, out lr, out lg, out lb);
            Assert.AreEqual(lr, lg, 1e-12, "old pink: grey");
            Assert.AreEqual(lr, lb, 1e-12);
            S7pPaint.Colourised(ColorMaskRules.StandardGreySrgb, 11f / 90f, -0.1f, 0.26f, out lr, out lg, out lb);
            Assert.AreEqual(lr, lb, 1e-12, "old yellow: grey");
            // SE's palette white stays neutral and light
            ColorMaskRules.GreyPreview(0f, -0.95f, 0.4f, out r, out g, out b);
            Assert.IsTrue(r == g && g == b && r > 200, "white neutral and light: " + r + "," + g + "," + b);
            Assert.AreEqual(r + "," + g + "," + b, ColorMaskRules.PreviewText(0f, -0.95f, 0.4f));
        }

        [TestMethod]
        public void S3Tint_OnTheShaderKey_KeepsTheHue()
        {
            // the approx tint (UWE_3COLOR fallback): on the raw ColorMaskHSV yellow is neutral; on the shader key it is yellow
            Rgba raw = PaintRules.MaterialColour(11f / 90f, -0.1f, 0.26f);
            Assert.AreEqual(raw.R, raw.B, 1e-6, "raw key: neutral (the beige ship)");
            float kh, ks, kv;
            ColorMaskRules.ShaderKey(11f / 90f, -0.1f, 0.26f, out kh, out ks, out kv);
            Rgba fixedTint = PaintRules.MaterialColour(kh, ks, kv);
            Assert.IsTrue(fixedTint.R > fixedTint.G && fixedTint.G > fixedTint.B, "yellow tint: R > G > B " + fixedTint);
        }

        [TestMethod]
        public void Bake_EqualsThePerTexelShaderReplay_WithinOneSrgbStep_UnpaintedCopied()
        {
            var rnd = new Random(23);
            int n = 777;
            var cm = new byte[n * 4];
            var add = new byte[n * 4];
            rnd.NextBytes(cm);
            rnd.NextBytes(add);
            for (int i = 0; i < n; i += 5) add[i * 4 + 3] = 0; // some unpainted texels
            foreach (float[] c in new[] { SeColours[7], SeColours[11], SeColours[2], SeColours[10] })
                foreach (bool mc in new[] { false, true })
                {
                    PaintBakeStats st;
                    var illum = new byte[n * 4];
                    byte[] o = ColorMaskRules.Bake(cm, add, n, c[0], c[1], c[2], mc, illum, out st);
                    int worst = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int k = i * 4;
                        double[] lin = { HlslSrgbToRgb(cm[k] / 255.0), HlslSrgbToRgb(cm[k + 1] / 255.0), HlslSrgbToRgb(cm[k + 2] / 255.0) };
                        double[] want = HlslBaseColour(lin, cm[k + 3] / 255.0, add[k + 3] / 255.0, new double[] { c[0], c[1], c[2] }, mc);
                        for (int ch = 0; ch < 3; ch++) worst = Math.Max(worst, Math.Abs(S7pPaint.SrgbByteOf(want[ch]) - o[k + ch]));
                        Assert.AreEqual(cm[k + 3], o[k + 3], "alpha (metalness) kept");
                        if (add[k + 3] == 0)
                            for (int ch = 0; ch < 3; ch++) Assert.AreEqual(cm[k + ch], o[k + ch], "unpainted texel copied byte for byte");
                    }
                    Assert.IsTrue(worst <= 1, "bake vs shader replay within one sRGB step (worst " + worst + ") for " + c[0] + "," + c[1] + "," + c[2] + " mc " + mc);
                    Assert.AreEqual(n, st.Texels);
                    Assert.IsTrue(st.Painted > 0 && st.Painted < n);
                }
        }

        // ------------------------------------------------------------------------------------------------ sizes and budget

        [TestMethod]
        public void BakeLevel_KeepsTheLargestSideWithinTheCap()
        {
            Assert.AreEqual(1, ColorMaskRules.BakeLevel(1024, 1024, 512));
            Assert.AreEqual(1, ColorMaskRules.BakeLevel(1024, 512, 512));
            Assert.AreEqual(1, ColorMaskRules.BakeLevel(512, 1024, 512));
            Assert.AreEqual(0, ColorMaskRules.BakeLevel(512, 512, 512));
            Assert.AreEqual(0, ColorMaskRules.BakeLevel(1024, 1024, 2048));
            Assert.AreEqual(4, ColorMaskRules.BakeLevel(1024, 1024, 64));
            Assert.AreEqual(2, ColorMaskRules.BakeLevel(1024, 1, 256));
            Assert.AreEqual(2, ColorMaskRules.BakeLevel(4, 4, 1));
            Assert.AreEqual(0, ColorMaskRules.BakeLevel(1, 1, 1));
            Assert.AreEqual(0, ColorMaskRules.BakeLevel(0, -3, 0), "degenerate input stays at level 0");
        }

        [TestMethod]
        public void SubChain_IsTheBuildMipsLayout_FromTheLevel()
        {
            long off, bytes;
            int w2, h2, m2;
            ColorMaskRules.SubChain(1024, 1024, 1, out off, out w2, out h2, out m2, out bytes);
            Assert.AreEqual(1024L * 1024 * 4, off);
            Assert.AreEqual(512, w2);
            Assert.AreEqual(512, h2);
            Assert.AreEqual(10, m2);
            Assert.AreEqual(TextureRepack.ChainBytes(512, 512, 10), bytes);
            // the slice of a real chain equals the chain built from that level's image
            int w = 8, h = 4;
            var level0 = new byte[w * h * 4];
            new Random(3).NextBytes(level0);
            int mips;
            byte[] chain = TextureRepack.BuildMips(level0, w, h, true, out mips);
            ColorMaskRules.SubChain(w, h, 1, out off, out w2, out h2, out m2, out bytes);
            Assert.AreEqual(mips - 1, m2);
            var level1 = new byte[w2 * h2 * 4];
            Buffer.BlockCopy(chain, (int)off, level1, 0, level1.Length);
            int mips1;
            byte[] chain1 = TextureRepack.BuildMips(level1, w2, h2, true, out mips1);
            Assert.AreEqual(m2, mips1);
            Assert.AreEqual(chain1.Length, bytes);
            for (int i = 0; i < chain1.Length; i++) Assert.AreEqual(chain1[i], chain[off + i], "byte " + i);
            Assert.AreEqual(off + bytes, chain.Length, "the slice runs to the chain's end");
            // a level past the end is clamped to the last
            ColorMaskRules.SubChain(4, 4, 9, out off, out w2, out h2, out m2, out bytes);
            Assert.AreEqual(1, w2);
            Assert.AreEqual(1, m2);
            Assert.AreEqual(4, bytes);
        }

        [TestMethod]
        public void BakeBytes_Budget_Victim_Mc_Stale_Key()
        {
            long one = ColorMaskRules.BakeBytes(1024, 1024, 1, false);
            Assert.AreEqual(TextureRepack.ChainBytes(512, 512, 10), one);
            Assert.AreEqual(2 * one, ColorMaskRules.BakeBytes(1024, 1024, 1, true), "the coloured emission doubles it");
            Assert.IsTrue(one > 1300000 && one < 1500000, "a 512 bake ≈ 1.4 MB: " + one);

            Assert.IsTrue(ColorMaskRules.Admit(0, 100, 100));
            Assert.IsFalse(ColorMaskRules.Admit(1, 100, 100));
            Assert.IsTrue(ColorMaskRules.Admit(500, 0, 100), "nothing needed always fits");

            var refs = new List<int> { 0, 1, 0, 0 };
            var settled = new List<bool> { true, true, false, true };
            var last = new List<int> { 50, 10, 5, 40 };
            Assert.AreEqual(3, ColorMaskRules.PickVictim(refs, settled, last), "least recently used, free, settled");
            Assert.AreEqual(-1, ColorMaskRules.PickVictim(new List<int> { 1 }, new List<bool> { true }, new List<int> { 0 }), "a carried bake is never freed");
            Assert.AreEqual(-1, ColorMaskRules.PickVictim(new List<int> { 0 }, new List<bool> { false }, new List<int> { 0 }), "a bake in flight is never freed");

            Assert.IsFalse(ColorMaskRules.DesiredMc(false, false));
            Assert.IsFalse(ColorMaskRules.DesiredMc(false, true), "unskinned cube parts: SE's metal reduction (VERIFIED)");
            Assert.IsTrue(ColorMaskRules.DesiredMc(true, false), "a metalness-colourable skin");
            Assert.IsFalse(ColorMaskRules.DesiredMc(true, true), "mixed users keep the reduction");

            Assert.IsFalse(ColorMaskRules.Stale(5, 6), "seen in the cycle that just ended");
            Assert.IsTrue(ColorMaskRules.Stale(5, 7), "missed a whole cycle");

            string a = ColorMaskRules.BakeKey("set", 1, false), b = ColorMaskRules.BakeKey("set", 1, true), c = ColorMaskRules.BakeKey("set", 2, false);
            Assert.AreNotEqual(a, b);
            Assert.AreNotEqual(a, c);
            Assert.IsTrue(a.StartsWith("blk#", StringComparison.Ordinal), "never the character's key (P9)");
            Assert.AreNotEqual(S7pPaint.BakeKey("set", 1, false), a);
        }

        // ------------------------------------------------------------------------------------------------ command and env

        [TestMethod]
        public void Command_IsMine_OnlyForTheMaskKeys()
        {
            Assert.IsTrue(ColorMaskRules.IsMine(new[] { "mask" }, null, null));
            Assert.IsTrue(ColorMaskRules.IsMine(new[] { "MaskSide" }, null, null));
            Assert.IsTrue(ColorMaskRules.IsMine(new[] { "maskMb" }, null, null));
            Assert.IsTrue(ColorMaskRules.IsMine(new string[0], "mask", null));
            Assert.IsTrue(ColorMaskRules.IsMine(new string[0], null, new[] { "mask", "approx" }));
            Assert.IsTrue(ColorMaskRules.IsMine(new string[0], null, new[] { "maskSide=1024" }));
            Assert.IsFalse(ColorMaskRules.IsMine(new[] { "look" }, "look", new[] { "look", "sky=1" }), "the look match's keys pass on");
            Assert.IsFalse(ColorMaskRules.IsMine(new[] { "fade" }, null, null), "the crossfade's keys pass on");
            Assert.IsFalse(ColorMaskRules.IsMine(null, null, null));
        }

        private static ColorMaskCommand Parse(Dictionary<string, object> d, params string[] words)
        {
            ColorMaskCommand c;
            string error;
            Assert.IsTrue(ColorMaskRules.TryParse(d, words.Length > 0 ? words : null, out c, out error), error);
            return c;
        }

        private static string ParseError(Dictionary<string, object> d, params string[] words)
        {
            ColorMaskCommand c;
            string error;
            Assert.IsFalse(ColorMaskRules.TryParse(d, words.Length > 0 ? words : null, out c, out error));
            Assert.IsNotNull(error);
            return error;
        }

        [TestMethod]
        public void Command_Grammar_DictAndWords()
        {
            Assert.AreEqual(false, Parse(new Dictionary<string, object> { { "mask", "approx" } }).Bake);
            Assert.AreEqual(true, Parse(new Dictionary<string, object> { { "mask", "bake" }, { "look", "sky=1" } }).Bake, "other S7t keys ignored");
            Assert.AreEqual(1024, Parse(new Dictionary<string, object> { { "maskSide", 1024.0 } }).Side);
            Assert.AreEqual(300, Parse(new Dictionary<string, object> { { "maskMb", "300" } }).Mb);
            Assert.AreEqual(false, Parse(null, "mask", "approx").Bake);
            Assert.AreEqual(true, Parse(null, "mask", "se").Bake);
            Assert.AreEqual(1024, Parse(null, "mask", "side", "1024").Side);
            Assert.AreEqual(512, Parse(null, "mask", "mb", "512").Mb);
            ColorMaskCommand two = Parse(null, "mask=bake", "maskSide=256");
            Assert.AreEqual(true, two.Bake);
            Assert.AreEqual(256, two.Side);
            Assert.AreEqual(2048, Parse(null, "maskSide", "2048").Side);
            Assert.IsFalse(Parse(null, "mask").Any, "a lone mask is the status");
            Assert.IsFalse(Parse(null, "mask", "status").Any);
            Assert.IsFalse(Parse(new Dictionary<string, object> { { "action", "mask" } }).Any);
            StringAssert.Contains(ParseError(null, "mask", "side"), "number");
            StringAssert.Contains(ParseError(null, "mask", "purple"), "bake | approx");
            StringAssert.Contains(ParseError(null, "maskSide=10"), "64..2048");
            StringAssert.Contains(ParseError(new Dictionary<string, object> { { "maskMb", 1e9 } }), "16..4096");
            StringAssert.Contains(ParseError(new Dictionary<string, object> { { "maskSide", 512.5 } }), "64..2048");
            StringAssert.Contains(ParseError(null, "mask", "approx", "foo"), "'foo'");
        }

        [TestMethod]
        public void Command_Apply_SideRebakes_ModeFlips_BudgetOnly()
        {
            var s = new ColorMaskSettings();
            bool modeChanged;
            Assert.IsFalse(ColorMaskRules.Apply(s, new ColorMaskCommand { Mb = 512 }, "t", out modeChanged));
            Assert.IsFalse(modeChanged);
            Assert.AreEqual(512, s.BudgetMb);
            Assert.AreEqual(512L * 1024 * 1024, s.BudgetBytes);
            Assert.IsTrue(ColorMaskRules.Apply(s, new ColorMaskCommand { Side = 1024 }, "t", out modeChanged), "a new side re-makes the bakes");
            Assert.AreEqual(1024, s.MaxSide);
            Assert.IsFalse(ColorMaskRules.Apply(s, new ColorMaskCommand { Side = 1024 }, "t", out modeChanged), "the same side: nothing");
            Assert.IsFalse(ColorMaskRules.Apply(s, new ColorMaskCommand { Bake = false }, "chat", out modeChanged));
            Assert.IsTrue(modeChanged);
            Assert.IsFalse(s.Bake);
            Assert.AreEqual("chat", s.Note);
            Assert.AreEqual("mask=approx side=1024 mb=512", s.Describe());
            ColorMaskSettings copy = s.Clone();
            Assert.AreEqual(s.Describe(), copy.Describe());
        }

        [TestMethod]
        public void Environment_Defaults_Overrides_BadValuesNoted()
        {
            string note;
            ColorMaskSettings d = ColorMaskRules.FromEnvironment(null, null, null, out note);
            Assert.IsTrue(d.Bake);
            Assert.AreEqual(ColorMaskRules.DefaultSide, d.MaxSide);
            Assert.AreEqual(ColorMaskRules.DefaultBudgetMb, d.BudgetMb);
            StringAssert.Contains(note, "defaults");
            ColorMaskSettings o = ColorMaskRules.FromEnvironment("approx", "1024", "768", out note);
            Assert.IsFalse(o.Bake);
            Assert.AreEqual(1024, o.MaxSide);
            Assert.AreEqual(768, o.BudgetMb);
            StringAssert.Contains(note, "DEEPENGINEERS_SN_MASK=approx");
            ColorMaskSettings bad = ColorMaskRules.FromEnvironment("maybe", "13", "abc", out note);
            Assert.IsTrue(bad.Bake);
            Assert.AreEqual(ColorMaskRules.DefaultSide, bad.MaxSide);
            Assert.AreEqual(ColorMaskRules.DefaultBudgetMb, bad.BudgetMb);
            StringAssert.Contains(note, "not understood");
            StringAssert.Contains(note, "DEEPENGINEERS_SN_MASK_SIDE='13'");
            StringAssert.Contains(note, "DEEPENGINEERS_SN_MASK_MB='abc'");
        }
    }
}
