using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7p-character (ROADMAP S7p P8, P9, P10), Subnautica side, Unity-free parts: the first-person part rule (SE's
 /// HiddenInFirstPerson parts shadow-only, the body drawn, nothing under fpbody=diver), the fpbody default se and the
 /// diver gate, the leak verdict of the s7p.char row; SE's ColorizeGray per texel (hand-computed vectors from the shader
 /// formula, the metalness-colourable switch, the bake's byte-exact copy of unpainted texels, the coloured _Illum); the
 /// light kinds from the row flags, the default scales (the suit lamp 25 → Unity 1.0), the environment list and the
 /// live command grammar.
 /// </summary>
    [TestClass]
    public class S7pCharacterTests
    {
        private const uint Head = S5Conv.HiddenInFirstPerson;
        private const uint Body = 0u;

        // ------------------------------------------------------------------------------------------------ P8

        [TestMethod]
        public void P8_FirstPerson_BodyDrawn_HeadShadowOnly_UnderSe()
        {
            Assert.AreEqual(PartShow.Drawn, S7pFirstPerson.Show(Body, CameraMode.FirstPerson, FpBody.Se), "SE's arms and body in first person");
            Assert.AreEqual(PartShow.ShadowOnly, S7pFirstPerson.Show(Head, CameraMode.FirstPerson, FpBody.Se), "the head is not seen but casts its shadow (SE: SkipInMainView only)");
            Assert.AreEqual(PartShow.Hidden, S7pFirstPerson.Show(Body, CameraMode.FirstPerson, FpBody.Diver), "fpbody=diver: S5a's diver arms are the body");
            Assert.AreEqual(PartShow.Hidden, S7pFirstPerson.Show(Head, CameraMode.FirstPerson, FpBody.Diver));
            foreach (uint mode in new[] { CameraMode.ThirdPerson, CameraMode.Cockpit, CameraMode.Spectator, CameraMode.Other })
                foreach (FpBody fp in new[] { FpBody.Se, FpBody.Diver })
                {
                    Assert.AreEqual(PartShow.Drawn, S7pFirstPerson.Show(Body, mode, fp), "third person draws everything (mode " + mode + ")");
                    Assert.AreEqual(PartShow.Drawn, S7pFirstPerson.Show(Head, mode, fp));
                }
        }

        [TestMethod]
        public void P8_ShowAgreesWithS5PartDrawn_ForTheVisiblePart()
        {
            // S5's two-state rule is the "seen in the main view" half of the S7p rule (S5.N7b stays true)
            foreach (uint flags in new[] { Body, Head, Head | S5Conv.Dynamic })
                foreach (uint mode in new[] { CameraMode.FirstPerson, CameraMode.ThirdPerson })
                    foreach (FpBody fp in new[] { FpBody.Se, FpBody.Diver })
                        Assert.AreEqual(CharSourceRules.PartDrawn(flags, mode, fp), S7pFirstPerson.Show(flags, mode, fp) == PartShow.Drawn, flags + "/" + mode + "/" + fp);
        }

        [TestMethod]
        public void P8_FpBodyDefaultIsSe_EnvironmentOverrides()
        {
            string note;
            Assert.AreEqual(FpBody.Se, S7pFirstPerson.FpBodyDefault);
            Assert.AreEqual(FpBody.Se, S7pFirstPerson.FromEnvironmentValue(null, out note));
            StringAssert.Contains(note, "default se");
            Assert.AreEqual(FpBody.Se, S7pFirstPerson.FromEnvironmentValue("", out note));
            Assert.AreEqual(FpBody.Diver, S7pFirstPerson.FromEnvironmentValue("diver", out note));
            StringAssert.Contains(note, "DEEPENGINEERS_SN_FPBODY=diver");
            Assert.AreEqual(FpBody.Se, S7pFirstPerson.FromEnvironmentValue(" SE ", out note));
            Assert.AreEqual(FpBody.Se, S7pFirstPerson.FromEnvironmentValue("arms", out note), "not understood: the default");
            StringAssert.Contains(note, "not understood");
        }

        [TestMethod]
        public void P8_DiverGateForcedOffUnderSe_UserGateUnderDiver()
        {
            foreach (ArmsGate g in new[] { ArmsGate.Full, ArmsGate.Camera, ArmsGate.ForceOn, ArmsGate.ForceOff })
            {
                Assert.AreEqual(ArmsGate.ForceOff, S7pFirstPerson.GateFor(FpBody.Se, g, true), "se with SE's astronaut: never the diver arms (" + g + ")");
                Assert.AreEqual(g, S7pFirstPerson.GateFor(FpBody.Se, g, false), "se without any SE character (S5 off on SE): the diver is the only body");
                Assert.AreEqual(g, S7pFirstPerson.GateFor(FpBody.Diver, g, true), "diver: the user's gate");
                Assert.AreEqual(g, S7pFirstPerson.GateFor(FpBody.Diver, g, false));
            }
            Assert.IsFalse(S7pFirstPerson.DiverArmsAllowed(FpBody.Se, true));
            Assert.IsTrue(S7pFirstPerson.DiverArmsAllowed(FpBody.Se, false));
            Assert.IsTrue(S7pFirstPerson.DiverArmsAllowed(FpBody.Diver, true));
            // the gate the host keeps hides S5a's arms whatever the swim state (S5aRules is the S5a rule itself)
            Assert.IsFalse(S5aRules.ArmsVisible(S7pFirstPerson.GateFor(FpBody.Se, ArmsGate.Full, true), true, true, SwimWireFlags.InWater, true, true, true, true));
            Assert.IsTrue(S5aRules.ArmsVisible(S7pFirstPerson.GateFor(FpBody.Diver, ArmsGate.Full, true), true, true, SwimWireFlags.InWater, true, true, true, true));
        }

        [TestMethod]
        public void P8_LeakVerdict()
        {
            Assert.IsTrue(S7pFirstPerson.SnBodyLeak(FpBody.Se, true, true, true, 0), "diver arms under se");
            Assert.IsTrue(S7pFirstPerson.SnBodyLeak(FpBody.Se, true, true, false, 3), "Player renderers on under se");
            Assert.IsFalse(S7pFirstPerson.SnBodyLeak(FpBody.Se, true, true, false, 0));
            Assert.IsFalse(S7pFirstPerson.SnBodyLeak(FpBody.Se, true, false, true, 3), "third person: not a first-person frame");
            Assert.IsFalse(S7pFirstPerson.SnBodyLeak(FpBody.Diver, true, true, true, 3), "diver: allowed");
            Assert.IsFalse(S7pFirstPerson.SnBodyLeak(FpBody.Se, false, true, true, 3), "no SE character: the diver is allowed");
            Assert.AreEqual("shadowOnly", S7pFirstPerson.Name(PartShow.ShadowOnly));
        }

        // ------------------------------------------------------------------------------------------------ P9

        private static void AssertRgb(double er, double eg, double eb, double r, double g, double b, double tol, string what)
        {
            Assert.AreEqual(er, r, tol, what + " r");
            Assert.AreEqual(eg, g, tol, what + " g");
            Assert.AreEqual(eb, b, tol, what + " b");
        }

        [TestMethod]
        public void P9_HsvToRgb_IsSeFormula_EqualsTheTextbookHsv()
        {
            // SE's branch-free hsv_to_rgb (Math/Color.hlsli) equals the textbook six-sector HSV for every hue
            for (int i = 0; i <= 360; i += 5)
                foreach (double s in new[] { 0.0, 0.3, 1.0 })
                    foreach (double v in new[] { 0.0, 0.5, 1.0 })
                    {
                        double r, g, b, r2, g2, b2;
                        S7pPaint.HsvToRgb(i / 360.0, s, v, out r, out g, out b);
                        PaintRules.HsvToRgb(i / 360.0, s, v, out r2, out g2, out b2);
                        AssertRgb(r2, g2, b2, r, g, b, 1e-9, "h=" + i + " s=" + s + " v=" + v);
                    }
        }

        [TestMethod]
        public void P9_ColorizeGray_HandVectors()
        {
            double r, g, b;
            // SE's grey default (0, -1, 0): saturation 0, value kept: a grey texel unchanged, a coloured one turned grey at its max channel
            double t = S7pPaint.SrgbToRgb(128 / 255.0);
            S7pPaint.ColorizeGray(t, t, t, 0f, -1f, 0f, 1.0, out r, out g, out b);
            AssertRgb(t, t, t, r, g, b, 1e-12, "grey kept");
            S7pPaint.ColorizeGray(S7pPaint.SrgbToRgb(200 / 255.0), S7pPaint.SrgbToRgb(100 / 255.0), S7pPaint.SrgbToRgb(50 / 255.0), 0f, -1f, 0f, 1.0, out r, out g, out b);
            double v200 = S7pPaint.SrgbToRgb(200 / 255.0);
            AssertRgb(v200, v200, v200, r, g, b, 1e-12, "coloured → grey at V");
            // red, full saturation, no value offset, on sRGB 128: (128, 0, 0)
            S7pPaint.ColorizeGray(t, t, t, 0f, 1f, 0f, 1.0, out r, out g, out b);
            AssertRgb(t, 0, 0, r, g, b, 1e-12, "red");
            // green (h 1/3), saturation 0.5, value +0.2 on sRGB 100: sRGB (0.5 V, V, 0.5 V) with V = 100/255 + 0.2
            double tv = S7pPaint.SrgbToRgb(100 / 255.0);
            S7pPaint.ColorizeGray(tv, tv, tv, 1f / 3f, 0.5f, 0.2f, 1.0, out r, out g, out b);
            double V = 100 / 255.0 + 0.2;
            AssertRgb(S7pPaint.SrgbToRgb(0.5 * V), S7pPaint.SrgbToRgb(V), S7pPaint.SrgbToRgb(0.5 * V), r, g, b, 1e-6, "green");
            // saturate: value over 1 clamps, saturation offset below 0 clamps, hue 1 = hue 0
            S7pPaint.ColorizeGray(t, t, t, 1f, 2f, 0.9f, 1.0, out r, out g, out b);
            AssertRgb(1, 0, 0, r, g, b, 1e-12, "clamped red");
            // coloring 0.5 lerps in linear; coloring 0 is SE's early-out
            S7pPaint.ColorizeGray(t, t, t, 0f, 1f, 0f, 0.5, out r, out g, out b);
            AssertRgb(t, t * 0.5, t * 0.5, r, g, b, 1e-12, "half");
            S7pPaint.ColorizeGray(0.3, 0.2, 0.1, 0.6f, 1f, 0.5f, 0.0, out r, out g, out b);
            AssertRgb(0.3, 0.2, 0.1, r, g, b, 0, "no paint");
        }

        [TestMethod]
        public void P9_Coloring_MetalnessColourable_SkipsTheMetalReduction()
        {
            Assert.AreEqual(1.0, S7pPaint.Coloring(1.0, 0.9, true), 1e-12, "character: METALNESS_COLORABLE");
            Assert.AreEqual(0.0, S7pPaint.Coloring(1.0, 0.9, false), 1e-6, "block: metal 0.9 loses its paint (SE RemoveMetalnessFromColoring)");
            Assert.AreEqual(1.0, S7pPaint.Coloring(1.0, 0.3, false), 1e-6, "below 0.4: full");
            Assert.AreEqual(0.5, S7pPaint.Coloring(1.0, 0.55, false), 1e-6, "0.55: half (falls to 0 at 0.7)");
            Assert.AreEqual(0.0, S7pPaint.Coloring(-0.2, 0, true), 0, "clamped");
        }

        [TestMethod]
        public void P9_Bake_ByteExactCopy_PaintedTexelsFollowTheFormula_AlphaKept()
        {
            // four texels: unpainted, painted grey, painted coloured on metal (colourable), painted metal (not colourable)
            byte[] cm = { 10, 20, 30, 40,   128, 128, 128, 0,   200, 100, 50, 230,   90, 90, 90, 230 };
            byte[] add = { 0, 0, 0, 0,       0, 0, 0, 255,      0, 128, 0, 255,      0, 0, 0, 255 };
            PaintBakeStats st;
            var illum = new byte[16];
            byte[] o = S7pPaint.Bake(cm, add, 4, 0f, 1f, 0f, true, illum, out st);
            CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40 }, new[] { o[0], o[1], o[2], o[3] }, "unpainted: copied");
            Assert.AreEqual(4, st.Texels);
            Assert.AreEqual(3, st.Painted, "metalness-colourable: the metal texels are painted too");
            // texel 1: grey 128 painted red → sRGB (128, 0, 0)
            Assert.AreEqual(128, o[4]);
            Assert.AreEqual(0, o[5]);
            Assert.AreEqual(0, o[6]);
            Assert.AreEqual(0, o[7], "alpha = metalness kept");
            // texel 2: V = 200 → red (200, 0, 0), alpha 230 kept
            Assert.AreEqual(200, o[8]);
            Assert.AreEqual(0, o[9]);
            Assert.AreEqual(230, o[11]);
            // illum of texel 2: the painted base × (128/255 − 1/255) in linear
            double em = 128 / 255.0 - 1 / 255.0;
            Assert.AreEqual(S7pPaint.SrgbByteOf(S7pPaint.LinearOf(200) * em), illum[8]);
            Assert.AreEqual(0, illum[9]);
            Assert.AreEqual(1, st.IllumTexels);
            // not colourable: the two metal texels keep their colour
            byte[] o2 = S7pPaint.Bake(cm, add, 4, 0f, 1f, 0f, false, null, out st);
            Assert.AreEqual(1, st.Painted);
            CollectionAssert.AreEqual(new byte[] { 200, 100, 50, 230, 90, 90, 90, 230 }, new[] { o2[8], o2[9], o2[10], o2[11], o2[12], o2[13], o2[14], o2[15] });
        }

        [TestMethod]
        public void P9_Bake_AgreesWithPerTexelColorizeGray()
        {
            var rnd = new Random(17);
            int n = 512;
            var cm = new byte[n * 4];
            var add = new byte[n * 4];
            rnd.NextBytes(cm);
            rnd.NextBytes(add);
            float h = 0.575f, s = 0.15f, v = 0.2f; // one of SE's default character colours (MyCharacter.m_defaultColors)
            PaintBakeStats st;
            byte[] o = S7pPaint.Bake(cm, add, n, h, s, v, true, null, out st);
            int worst = 0;
            for (int i = 0; i < n; i++)
            {
                double r, g, b;
                S7pPaint.ColorizeGray(S7pPaint.LinearOf(cm[i * 4]), S7pPaint.LinearOf(cm[i * 4 + 1]), S7pPaint.LinearOf(cm[i * 4 + 2]), h, s, v, add[i * 4 + 3] / 255.0, out r, out g, out b);
                worst = Math.Max(worst, Math.Abs(S7pPaint.SrgbByteOf(r) - o[i * 4]));
                worst = Math.Max(worst, Math.Abs(S7pPaint.SrgbByteOf(g) - o[i * 4 + 1]));
                worst = Math.Max(worst, Math.Abs(S7pPaint.SrgbByteOf(b) - o[i * 4 + 2]));
                Assert.AreEqual(cm[i * 4 + 3], o[i * 4 + 3]);
            }
            Assert.IsTrue(worst <= 1, "bake vs per-texel formula within one sRGB step (worst " + worst + ")");
        }

        [TestMethod]
        public void P9_SrgbLuts_RoundTrip()
        {
            for (int i = 0; i < 256; i++) Assert.AreEqual((byte)i, S7pPaint.SrgbByteOf(S7pPaint.LinearOf((byte)i)), "byte " + i);
            Assert.AreEqual(0, S7pPaint.SrgbByteOf(-1));
            Assert.AreEqual(255, S7pPaint.SrgbByteOf(2));
            Assert.AreEqual(0, S7pPaint.SrgbByteOf(double.NaN));
        }

        [TestMethod]
        public void P9_Bake_RefusesShortInputs_ModeGrammar()
        {
            PaintBakeStats st;
            Assert.ThrowsException<ArgumentException>(() => S7pPaint.Bake(new byte[4], new byte[8], 2, 0, 0, 0, true, null, out st));
            Assert.ThrowsException<ArgumentException>(() => S7pPaint.Bake(new byte[8], new byte[4], 2, 0, 0, 0, true, null, out st));
            Assert.ThrowsException<ArgumentException>(() => S7pPaint.Bake(new byte[8], new byte[8], 2, 0, 0, 0, true, new byte[4], out st));
            bool bake;
            Assert.IsTrue(S7pPaint.TryParseMode("approx", out bake) && !bake);
            Assert.IsTrue(S7pPaint.TryParseMode(" BAKE ", out bake) && bake);
            Assert.IsFalse(S7pPaint.TryParseMode("maybe", out bake));
            string note;
            Assert.IsTrue(S7pPaint.ModeFromEnvironmentValue(null, out note));
            Assert.IsFalse(S7pPaint.ModeFromEnvironmentValue("approx", out note));
            Assert.IsTrue(S7pPaint.ModeFromEnvironmentValue("x", out note));
            StringAssert.Contains(note, "not understood");
            Assert.AreNotEqual(S7pPaint.BakeKey("a", 1, true), S7pPaint.BakeKey("a", 1, false));
            Assert.AreNotEqual(S7pPaint.BakeKey("a", 1, true), S7pPaint.BakeKey("a", 2, true));
        }

        // ------------------------------------------------------------------------------------------------ P10

        [TestMethod]
        public void P10_KindsFromRowFlags()
        {
            Assert.AreEqual(LightKind.CharacterSpot, S7pLightScale.KindOf(S7LightRow.Flags(true, true, true, true)));
            Assert.AreEqual(LightKind.CharacterPoint, S7pLightScale.KindOf(S7LightRow.Flags(false, false, true, true)));
            Assert.AreEqual(LightKind.BlockSpot, S7pLightScale.KindOf(S7LightRow.Flags(true, true, true, false)));
            Assert.AreEqual(LightKind.BlockPoint, S7pLightScale.KindOf(S7LightRow.Flags(false, false, true, false)));
        }

        [TestMethod]
        public void P10_Defaults_SuitLampAndSpotlightsAtUnityOne()
        {
            float[] i = S7pLightScale.DefaultIntensity;
            float[] se = S7pLightScale.SeDefaultIntensity;
            Assert.AreEqual(1f, S7pLightScale.Intensity(se[(int)LightKind.CharacterSpot], 1f, i[(int)LightKind.CharacterSpot]), 1e-6f, "the suit lamp (SE 25) → Unity 1");
            Assert.AreEqual(1f, S7pLightScale.Intensity(se[(int)LightKind.BlockSpot], 1f, i[(int)LightKind.BlockSpot]), 1e-6f, "a spotlight at its default (SE 8) → Unity 1");
            Assert.AreEqual(0.5f, S7pLightScale.Intensity(se[(int)LightKind.CharacterPoint], 1f, i[(int)LightKind.CharacterPoint]), 1e-6f, "the helmet glow keeps SE's 0.5");
            Assert.AreEqual(1f, S7pLightScale.Intensity(se[(int)LightKind.BlockPoint], 1f, i[(int)LightKind.BlockPoint]), 1e-6f, "interior lights (SE 10) → Unity 1 (owner 2026-10-17 23:5x: three of them whited out the hull at night)");
            Assert.AreEqual(2f, S7pLightScale.Intensity(25f, 2f, 0.04f), 1e-6f, "the global de.lights scale multiplies");
            Assert.AreEqual(0.5f, S7pLightScale.DefaultRange[(int)LightKind.BlockSpot], "spotlights at half SE's range (2026-10-18)");
            Assert.AreEqual(1f, S7pLightScale.DefaultRange[(int)LightKind.BlockPoint]);
            // the ceiling: a spotlight at SE intensity 10 (reflector 80) lands at 1.2, not 10
            Assert.AreEqual(1.2f, S7pLightScale.Intensity(LightKind.BlockSpot, 80f, 1f, 0.125f), 1e-6f);
            Assert.AreEqual(1f, S7pLightScale.Intensity(LightKind.BlockSpot, 8f, 1f, 0.125f), 1e-6f, "the default setting stays 1.0");
            Assert.AreEqual(35f, S7pLightScale.Range(35f, 1f, 1f), 1e-6f);
            Assert.AreEqual(0.01f, S7pLightScale.Range(35f, 1f, 0f), 1e-6f, "never below 1 cm");
            Assert.AreEqual(0f, S7pLightScale.Intensity(float.NaN, 1f, 1f));
        }

        [TestMethod]
        public void P10_EnvironmentList()
        {
            var i = new float[4];
            var r = new float[4];
            S7pLightScale.Reset(i, r);
            string e;
            Assert.IsTrue(S7pLightScale.TryParseList("characterSpot=0.08, blockSpot=0.2:0.5;suit=0.05", i, r, out e), e);
            Assert.AreEqual(0.05f, i[(int)LightKind.CharacterSpot], 1e-6f, "applied in order (suit = characterSpot)");
            Assert.AreEqual(0.2f, i[(int)LightKind.BlockSpot], 1e-6f);
            Assert.AreEqual(0.5f, r[(int)LightKind.BlockSpot], 1e-6f);
            Assert.AreEqual(1f, r[(int)LightKind.CharacterSpot], 1e-6f, "range untouched without ':'");
            Assert.IsTrue(S7pLightScale.TryParseList("", i, r, out e));
            Assert.IsFalse(S7pLightScale.TryParseList("characterSpot=0", i, r, out e), "0 refused");
            Assert.IsFalse(S7pLightScale.TryParseList("lamp=0.5", i, r, out e), "unknown kind");
            Assert.IsFalse(S7pLightScale.TryParseList("blockPoint", i, r, out e), "no '='");
            Assert.IsFalse(S7pLightScale.TryParseList("blockPoint=1:x", i, r, out e), "bad range");
            Assert.AreEqual("blockPoint=0.1,blockSpot=0.2:0.5,characterPoint=1,characterSpot=0.05", S7pLightScale.Describe(i, r));
            S7pLightScale.Reset(i, r);
            Assert.AreEqual("blockPoint=0.1,blockSpot=0.125:0.5,characterPoint=1,characterSpot=0.04", S7pLightScale.Describe(i, r));
        }

        [TestMethod]
        public void P10_CommandGrammar()
        {
            string action, error;
            LightKind k;
            double f, rr;
            Assert.IsTrue(S7pLightScale.TryParseCommand(new List<string>(), out action, out k, out f, out rr, out error));
            Assert.AreEqual("status", action);
            Assert.IsTrue(S7pLightScale.TryParseCommand(new List<string> { "reset" }, out action, out k, out f, out rr, out error));
            Assert.AreEqual("reset", action);
            Assert.IsTrue(S7pLightScale.TryParseCommand(new List<string> { "suit", "0.02" }, out action, out k, out f, out rr, out error), error);
            Assert.AreEqual("set", action);
            Assert.AreEqual(LightKind.CharacterSpot, k);
            Assert.AreEqual(0.02, f, 1e-12);
            Assert.IsTrue(double.IsNaN(rr));
            Assert.IsTrue(S7pLightScale.TryParseCommand(new List<string> { "spotlight", "0.5", "0.75" }, out action, out k, out f, out rr, out error), error);
            Assert.AreEqual(LightKind.BlockSpot, k);
            Assert.AreEqual(0.75, rr, 1e-12);
            Assert.IsFalse(S7pLightScale.TryParseCommand(new List<string> { "suit" }, out action, out k, out f, out rr, out error), "no factor");
            Assert.IsFalse(S7pLightScale.TryParseCommand(new List<string> { "suit", "500" }, out action, out k, out f, out rr, out error), "over 100");
            Assert.IsFalse(S7pLightScale.TryParseCommand(new List<string> { "lamp", "1" }, out action, out k, out f, out rr, out error), "unknown kind");
            Assert.IsFalse(S7pLightScale.TryParseCommand(new List<string> { "suit", "1", "-1" }, out action, out k, out f, out rr, out error), "bad range");
            LightKind kk;
            Assert.IsTrue(S7pLightScale.TryParseKind("CharacterPoint", out kk) && kk == LightKind.CharacterPoint);
            Assert.IsTrue(S7pLightScale.TryParseKind("interior", out kk) && kk == LightKind.BlockPoint);
            Assert.AreEqual("characterSpot", S7pLightScale.Name(LightKind.CharacterSpot));
        }
    }
}
