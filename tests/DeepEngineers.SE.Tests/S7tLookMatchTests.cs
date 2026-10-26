using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7t T5 look match, SE side (ROADMAP "### S7t Seamless transition" T5; CONTRACT §3.4): the target table, the weight
 /// across the band (SE's own look in Space → the match set at the band and below), the gate that restores SE's own
 /// look when the picture is off, on peer loss and under water, the blend, the de.s7t look syntax and the row cadence.
 /// Replay-exact: no game type.
 /// </summary>
    [TestClass]
    public class S7tLookMatchTests
    {
        private static LookGateInputs Live(double y)
        {
            return new LookGateInputs
            {
                Enabled = true, InWorld = true, BridgeUp = true, DiveZone = true, PeerAlive = true, SitePolicy = true,
                Y = y, BandEnterY = 1000, RampM = 1500, Force = double.NaN,
            };
        }

        // ------------------------------------------------------------------------------------------ the table

        [TestMethod]
        public void Keys_AreUniqueIndexedAndTheirDefaultsInRange()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < LookMatchRules.Keys.Length; i++)
            {
                LookKey k = LookMatchRules.Keys[i];
                Assert.IsTrue(names.Add(k.Name), "duplicate key " + k.Name);
                Assert.AreEqual(i, k.Index, k.Name);
                Assert.IsFalse(string.IsNullOrEmpty(k.Member), k.Name);
                if (k.Default == null) continue;
                Assert.AreEqual(k.Channels, k.Default.Length, k.Name);
                foreach (double v in k.Default) Assert.IsTrue(v >= k.Min && v <= k.Max, k.Name + " default " + v + " outside " + k.Min + ".." + k.Max);
            }
            Assert.IsNull(LookMatchRules.Find("nope"));
            Assert.AreSame(LookMatchRules.Find("contrast"), LookMatchRules.Find("CONTRAST"));
        }

        [TestMethod]
        public void Defaults_MoveTowardSubnauticasMeasuredLook()
        {

            // aerial perspective toward Subnautica's white horizon haze (197,241,255), no daylight stars
            Assert.IsTrue(LookMatchRules.Find("contrast").Default[0] < 1.0);
            Assert.IsTrue(LookMatchRules.Find("saturation").Default[0] < 1.0);
            Assert.IsTrue(LookMatchRules.Find("sun").Default[0] < 1.0);
            Assert.IsTrue(LookMatchRules.Find("ambient").Default[0] > 1.0);
            Assert.IsTrue(LookMatchRules.Find("atmoAmbient").Default[0] > 1.0);
            Assert.IsTrue(LookMatchRules.Find("skybox").Default[0] < 1.0);
            double[] fc = LookMatchRules.Find("fogColor").Default;
            Assert.AreEqual(197, Math.Round(fc[0] * 255)); Assert.AreEqual(241, Math.Round(fc[1] * 255)); Assert.AreEqual(255, Math.Round(fc[2] * 255));
            for (int ch = 0; ch < 3; ch++) Assert.AreEqual(DeepEngineers.Wire.S7tBandTargets.HazeHorizon.Unit(ch), fc[ch], 1e-12, "S7t integrator: fogColor = the one measured source (Wire S7tBandTargets)");
            // fog: ~4 % at 550 m, ~19 % at 5 km (the pair asked for ~7 % at 550 m and ~37 % far, halved for EarthLike's own FogIntensity)
            double m = LookMatchRules.Find("fog").Default[0], d = LookMatchRules.Find("fogDensity").Default[0];
            Assert.AreEqual(0.25, m, 1e-12);
            Assert.AreEqual(0.0003, d, 1e-12);
            Assert.IsTrue(m * (1 - Math.Exp(-d * 5000)) > 0.15 && m * (1 - Math.Exp(-d * 5000)) < 0.2);

            foreach (LookKey k in LookMatchRules.Keys) if (k.Group == LookGroup.Atmo) Assert.IsNull(k.Default, k.Name);
        }

        // ------------------------------------------------------------------------------------------ weight

        [TestMethod]
        public void BandWeight_IsOneAtAndBelowTheBand_ZeroAboveTheRamp_SmoothBetween()
        {
            Assert.AreEqual(1.0, LookMatchRules.BandWeight(1000, 1000, 1500));
            Assert.AreEqual(1.0, LookMatchRules.BandWeight(0, 1000, 1500));
            Assert.AreEqual(1.0, LookMatchRules.BandWeight(-50, 1000, 1500));
            Assert.AreEqual(0.0, LookMatchRules.BandWeight(2500, 1000, 1500));
            Assert.AreEqual(0.0, LookMatchRules.BandWeight(100000, 1000, 1500));
            Assert.AreEqual(0.5, LookMatchRules.BandWeight(1750, 1000, 1500), 1e-12);
            Assert.AreEqual(0.0, LookMatchRules.BandWeight(double.NaN, 1000, 1500));
            Assert.AreEqual(0.0, LookMatchRules.BandWeight(1001, 1000, 0), "no ramp: a step at the band");
            double last = 1.0;
            for (double y = 1000; y <= 2600; y += 25)
            {
                double w = LookMatchRules.BandWeight(y, 1000, 1500);
                Assert.IsTrue(w <= last + 1e-15, "monotonic at " + y);
                last = w;
            }
            // smoothstep: flat at both ends (no visible kink at the band or at the top)
            Assert.IsTrue(1.0 - LookMatchRules.BandWeight(1010, 1000, 1500) < 1e-3);
            Assert.IsTrue(LookMatchRules.BandWeight(2490, 1000, 1500) < 1e-3);
        }

        [TestMethod]
        public void Target_RestoresSeOwnLook_WheneverThePictureIsOff()
        {
            string why;
            LookGateInputs i = Live(500);
            Assert.AreEqual(1.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("band", why);
            i = Live(1750); Assert.AreEqual(0.5, LookMatchRules.Target(ref i, out why), 1e-12); Assert.AreEqual("ramp", why);
            i = Live(5000); Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("high", why);
            i = Live(500); i.Enabled = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("off", why);
            i = Live(500); i.InWorld = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("noWorld", why);
            i = Live(500); i.BridgeUp = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("bridge", why);
            i = Live(500); i.PeerAlive = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("peer", why);
            i = Live(500); i.SitePolicy = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("policy", why);
            i = Live(500); i.DiveZone = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("site", why);
            i = Live(double.NaN); Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("y", why);
            i = Live(-0.5); Assert.AreEqual(1.0, LookMatchRules.Target(ref i, out why), "the surface itself is still the band's look");
            i = Live(-1.5); Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why)); Assert.AreEqual("underwater", why);
        }

        [TestMethod]
        public void Target_ForceShowsTheMatchSetAnywhereInTheWorld_ButNotWhenOffOrNoWorld()
        {
            string why;
            LookGateInputs i = Live(50000); i.PeerAlive = false; i.Force = 0.7;
            Assert.AreEqual(0.7, LookMatchRules.Target(ref i, out why), 1e-12); Assert.AreEqual("force", why);
            i.Force = 3; Assert.AreEqual(1.0, LookMatchRules.Target(ref i, out why));
            i.Enabled = false; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why));
            i = Live(500); i.InWorld = false; i.Force = 1; Assert.AreEqual(0.0, LookMatchRules.Target(ref i, out why));
        }

        [TestMethod]
        public void Ease_PeerLossRestoresWithinTheEaseTime()
        {
            double w = 1.0;
            int frames = 0;
            while (w > 0 && frames < 1000) { w = LookMatchRules.Ease(w, 0.0, 1.0 / 60.0, 0.8); frames++; }
            Assert.AreEqual(0.0, w);
            Assert.IsTrue(frames >= 47 && frames <= 49, "0.8 s at 60 fps: " + frames);
            Assert.AreEqual(0.3, LookMatchRules.Ease(0.0, 0.3, 1.0, 0.8), 1e-12, "within one step: exact");
            Assert.AreEqual(1.0, LookMatchRules.Ease(0.0, 1.0, 0.01, 0.0), "ease 0: at once");
            Assert.AreEqual(0.5, LookMatchRules.Ease(double.NaN, 0.5, 1, 0.8));
        }

        [TestMethod]
        public void Replay_DiveFromSpaceAndBack_EndsExactlyOnSeOwnValues()
        {
            // a 150 m/s descent from 4 km to the water and back up at 60 fps; the band's look is held below the band and
 // SE's own values come back bit for bit in Space (a factor key and a value key)
            LookKey contrast = LookMatchRules.Find("contrast"), fog = LookMatchRules.Find("fog");
            double[] tc = { 0.9 }, tf = { 0.25 };
            const float ownC = 1.0f, ownF = 0.13f;
            double w = 0, y = 4000, dt = 1.0 / 60.0;
            double maxBelowBand = 0;
            for (int f = 0; f < 4000 / 150 * 60; f++)
            {
                y -= 150 * dt;
                LookGateInputs i = Live(y);
                string why;
                w = LookMatchRules.Ease(w, LookMatchRules.Target(ref i, out why), dt, 0.8);
                if (y < 900) maxBelowBand = Math.Max(maxBelowBand, Math.Abs(1.0 - w));
            }
            Assert.IsTrue(maxBelowBand < 1e-9, "the full match set below the band");
            Assert.AreEqual(0.9f, LookMatchRules.Blend(contrast, tc, 0, ownC, w), 1e-6f);
            for (int f = 0; f < 4000 / 150 * 60; f++)
            {
                y += 150 * dt;
                LookGateInputs i = Live(y);
                string why;
                w = LookMatchRules.Ease(w, LookMatchRules.Target(ref i, out why), dt, 0.8);
            }
            Assert.AreEqual(0.0, w);
            Assert.AreEqual(ownC, LookMatchRules.Blend(contrast, tc, 0, ownC, w));
            Assert.AreEqual(ownF, LookMatchRules.Blend(fog, tf, 0, ownF, w));
        }

        // ------------------------------------------------------------------------------------------ blend

        [TestMethod]
        public void Blend_FactorValueAndUnset()
        {
            LookKey contrast = LookMatchRules.Find("contrast"), fog = LookMatchRules.Find("fog"), tint = LookMatchRules.Find("tint");
            Assert.AreEqual(2.0f, LookMatchRules.Blend(contrast, new[] { 0.9 }, 0, 2.0f, 0.0));
            Assert.AreEqual(1.8f, LookMatchRules.Blend(contrast, new[] { 0.9 }, 0, 2.0f, 1.0), 1e-6f);
            Assert.AreEqual(1.9f, LookMatchRules.Blend(contrast, new[] { 0.9 }, 0, 2.0f, 0.5), 1e-6f);
            Assert.AreEqual(0.25f, LookMatchRules.Blend(fog, new[] { 0.25 }, 0, 0.13f, 1.0), 1e-6f);
            Assert.AreEqual(0.19f, LookMatchRules.Blend(fog, new[] { 0.25 }, 0, 0.13f, 0.5), 1e-6f);
            Assert.AreEqual(0.13f, LookMatchRules.Blend(fog, null, 0, 0.13f, 1.0), "unset: SE's own");
            Assert.AreEqual(0.13f, LookMatchRules.Blend(fog, new[] { double.NaN }, 0, 0.13f, 1.0));
            Assert.AreEqual(0.5f, LookMatchRules.Blend(tint, new[] { 1.0, 0.5, 1.0 }, 1, 1.0f, 1.0), 1e-6f);
            Assert.AreEqual(1.8f, LookMatchRules.Blend(contrast, new[] { 0.9 }, 0, 2.0f, 7.0), 1e-6f, "w clamps to 1");
            LookKey fc = LookMatchRules.Find("fogColor");
            double[] haze = fc.Default;
            Assert.AreEqual((byte)197, LookMatchRules.BlendByte(fc, haze, 0, 0, 1.0));
            Assert.AreEqual((byte)255, LookMatchRules.BlendByte(fc, haze, 2, 0, 1.0));
            Assert.AreEqual((byte)0, LookMatchRules.BlendByte(fc, haze, 0, 0, 0.0));
            Assert.AreEqual((byte)49, LookMatchRules.BlendByte(fc, haze, 0, 0, 0.25), "197 x 0.25 = 49.25");
        }

        [TestMethod]
        public void Differs_RelativeEpsilon()
        {
            Assert.IsTrue(LookMatchRules.Differs(new double[] { 1, 2 }, null));
            Assert.IsFalse(LookMatchRules.Differs(new double[] { 100, 0.5 }, new double[] { 100.4, 0.5 }));
            Assert.IsTrue(LookMatchRules.Differs(new double[] { 100, 0.5 }, new double[] { 101, 0.5 }));
            Assert.IsFalse(LookMatchRules.Differs(new double[] { 0, 0 }, new double[] { 0, 0 }));
        }

        // ------------------------------------------------------------------------------------------ de.s7t look

        [TestMethod]
        public void Table_KeyValueSyntax()
        {
            var t = new LookTable();
            var applied = new List<string>();
            List<string> p = t.Apply(new[] { "contrast=0.8", "fogColor=128,128,255", "tint=0.9", "force=0.5", "ramp=2000", "ease=0" }, applied);
            Assert.AreEqual(0, p.Count, string.Join("; ", p.ToArray()));
            Assert.AreEqual(0.8, t.Get(LookMatchRules.Find("contrast"))[0], 1e-12);
            Assert.AreEqual(128 / 255.0, t.Get(LookMatchRules.Find("fogColor"))[0], 1e-12, "0..255 accepted");
            CollectionAssert.AreEqual(new[] { 0.9, 0.9, 0.9 }, t.Get(LookMatchRules.Find("tint")), "one factor for the three channels");
            Assert.AreEqual(0.5, t.Force); Assert.AreEqual(2000, t.RampM); Assert.AreEqual(0, t.EaseS);
            p = t.Apply(new[] { "fogColor=0.5,0.6,0.7", "contrast=unset", "force=auto", "skybox=default" }, applied);
            Assert.AreEqual(0, p.Count);
            Assert.AreEqual(0.5, t.Get(LookMatchRules.Find("fogColor"))[0], 1e-12, "0..1 kept");
            Assert.IsNull(t.Get(LookMatchRules.Find("contrast")));
            Assert.IsTrue(double.IsNaN(t.Force));
            Assert.AreEqual(0.5, t.Get(LookMatchRules.Find("skybox"))[0]);
            p = t.Apply(new[] { "contrast=9", "nope=1", "fogColor=1,2", "rayleigh=abc", "loose" }, applied);
            Assert.AreEqual(5, p.Count, string.Join("; ", p.ToArray()));
            Assert.IsNull(t.Get(LookMatchRules.Find("contrast")), "a refused value changes nothing");
            t.Apply(new[] { "off" }, applied); Assert.IsFalse(t.Enabled);
            t.Apply(new[] { "on", "rayleigh=118,84,63" }, applied); Assert.IsTrue(t.Enabled);
            Assert.IsTrue(t.AnySet(LookGroup.Atmo));
            t.Apply(new[] { "reset" }, applied);
            Assert.IsFalse(t.AnySet(LookGroup.Atmo));
            Assert.AreEqual(0.9, t.Get(LookMatchRules.Find("contrast"))[0], 1e-12);
            StringAssert.Contains(t.Describe(), "contrast=0.9");
            StringAssert.Contains(t.Describe(), "fogColor=197,241,255");
            StringAssert.Contains(t.Describe(), "unset:");
        }

        [TestMethod]
        public void Env_DefaultsUseBlanksOrSemicolons()
        {
            var t = new LookTable();
            List<string> p = t.ApplyEnv("contrast=0.8;saturation=0.95 fogColor=200,240,255", null);
            Assert.AreEqual(0, p.Count);
            Assert.AreEqual(0.95, t.Get(LookMatchRules.Find("saturation"))[0], 1e-12);
            Assert.AreEqual(200 / 255.0, t.Get(LookMatchRules.Find("fogColor"))[0], 1e-12);
            t = new LookTable();
            t.ApplyEnv("off", null);
            Assert.IsFalse(t.Enabled);
            Assert.AreEqual(0, new LookTable().ApplyEnv(null, null).Count);
        }

        [TestMethod]
        public void IsMine_OnlyTheLookKeysOfTheSharedVerb()
        {
            List<string> tok;
            Assert.IsTrue(LookMatchRules.IsMine(new Dictionary<string, object> { { "look", "contrast=0.9 fog=0.2" } }, out tok));
            CollectionAssert.AreEqual(new[] { "contrast=0.9", "fog=0.2" }, tok);
            Assert.IsTrue(LookMatchRules.IsMine(new Dictionary<string, object> { { "look", new Dictionary<string, object> { { "contrast", 0.9 } } } }, out tok));
            CollectionAssert.AreEqual(new[] { "contrast=0.9" }, tok);
            Assert.IsTrue(LookMatchRules.IsMine(new Dictionary<string, object> { { "argv", new List<object> { "look", "fogColor=197,241,255", "status" } } }, out tok));
            CollectionAssert.AreEqual(new[] { "fogColor=197,241,255", "status" }, tok);
            Assert.IsTrue(LookMatchRules.IsMine(new Dictionary<string, object> { { "argv", new List<object> { "LOOK" } } }, out tok));
            Assert.AreEqual(0, tok.Count);
            Assert.IsFalse(LookMatchRules.IsMine(new Dictionary<string, object> { { "argv", new List<object> { "fade", "700" } } }, out tok));
            Assert.IsFalse(LookMatchRules.IsMine(new Dictionary<string, object> { { "fade", 700.0 } }, out tok));
            Assert.IsFalse(LookMatchRules.IsMine(new Dictionary<string, object> { { "mask", "on" } }, out tok));
            Assert.IsFalse(LookMatchRules.IsMine(null, out tok));
        }

        // ------------------------------------------------------------------------------------------ rows

        [TestMethod]
        public void RowCadence_PeriodicWhileActive_EdgesCappedWithADropCounter()
        {
            var c = new LookRowCadence();
            Assert.IsTrue(c.Due(10000, true, false));
            Assert.IsFalse(c.Due(10500, true, false));
            Assert.IsTrue(c.Due(11000, true, false));
            Assert.IsFalse(c.Due(13000, false, false), "inactive: no periodic row");
            int edges = 0;
            for (int i = 0; i < 10; i++) if (c.Due(20000UL + (ulong)i, true, true)) edges++;
            Assert.AreEqual(LookMatchRules.EdgeRowsPerSecond, edges);
            Assert.AreEqual(10 - LookMatchRules.EdgeRowsPerSecond, c.Dropped);
            Assert.IsTrue(c.Due(21001, true, true), "a new second");
        }
    }
}
