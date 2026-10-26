using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7t T5 look match, Subnautica side (ROADMAP "### S7t Seamless transition" T5; CONTRACT §3.4): Subnautica keeps its
 /// palette; only the sun's shadow strength and the ambient move toward SE's harder light above the surface. The
 /// altitude weight (nothing at or below the surface), the gate (SE alive, C5 fresh, the Site policy), the member
 /// capture/write/restore and the de.s7t look syntax. Unity-free.
 /// </summary>
    [TestClass]
    public class S7tLookMatchSnTests
    {
        private static SnLookGateInputs Live(double y)
        {
            return new SnLookGateInputs
            {
                Enabled = true, InWorld = true, PeerAlive = true, C5Fresh = true, PictureSite = true, Y = y,
                LowY = SnLookRules.DefaultLowY, HighY = SnLookRules.DefaultHighY, Force = double.NaN,
            };
        }

        [TestMethod]
        public void Keys_DefaultsHardenSubnauticaOnlySlightly()
        {
            SnLookKey shadow = SnLookRules.Find("shadow"), ambient = SnLookRules.Find("ambient");

            // sun shadows and more ambient above the surface
            Assert.AreEqual(0.6, shadow.Default, "softer sun shadows at the band");
            Assert.AreEqual(1.5, ambient.Default, "more ambient at the band");
            Assert.IsTrue(double.IsNaN(SnLookRules.Find("sun").Default));
            Assert.IsTrue(double.IsNaN(SnLookRules.Find("exposure").Default));
            for (int i = 0; i < SnLookRules.Keys.Length; i++) Assert.AreEqual(i, SnLookRules.Keys[i].Index);
            Assert.IsNull(SnLookRules.Find("fogColor"), "Subnautica's sky, fog and grade are the reference: no such key");
        }

        [TestMethod]
        public void AltitudeWeight_NothingAtTheSurface_FullHigh()
        {
            Assert.AreEqual(0.0, SnLookRules.AltitudeWeight(0, 20, 300));
            Assert.AreEqual(0.0, SnLookRules.AltitudeWeight(20, 20, 300));
            Assert.AreEqual(0.5, SnLookRules.AltitudeWeight(160, 20, 300), 1e-12);
            Assert.AreEqual(1.0, SnLookRules.AltitudeWeight(300, 20, 300));
            Assert.AreEqual(1.0, SnLookRules.AltitudeWeight(1000, 20, 300), "the band (+1,000 m) is in the full range");
            Assert.AreEqual(0.0, SnLookRules.AltitudeWeight(double.NaN, 20, 300));
        }

        [TestMethod]
        public void Target_Gate()
        {
            string why;
            SnLookGateInputs i = Live(1000); Assert.AreEqual(1.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("high", why);
            i = Live(10); Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("surface", why);
            i = Live(-5); Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("underwater", why);
            i = Live(1000); i.PeerAlive = false; Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("peer", why);
            i = Live(1000); i.C5Fresh = false; Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("c5", why);
            i = Live(1000); i.PictureSite = false; Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("policy", why);
            i = Live(1000); i.InWorld = false; Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("noWorld", why);
            i = Live(1000); i.Enabled = false; Assert.AreEqual(0.0, SnLookRules.Target(ref i, out why)); Assert.AreEqual("off", why);
            i = Live(-50); i.PeerAlive = false; i.Force = 0.4; Assert.AreEqual(0.4, SnLookRules.Target(ref i, out why), 1e-12); Assert.AreEqual("force", why);
        }

        [TestMethod]
        public void Blend_ClampsShadowStrengthToZeroOne()
        {
            SnLookKey shadow = SnLookRules.Find("shadow"), ambient = SnLookRules.Find("ambient");
            Assert.AreEqual(0.9, SnLookRules.Blend(shadow, 1.0, 0.8, 0.5), 1e-12);
            Assert.AreEqual(1.0, SnLookRules.Blend(shadow, 1.0, 0.8, 1.0), 1e-12);
            Assert.AreEqual(0.8, SnLookRules.Blend(shadow, 1.0, 0.8, 0.0));
            Assert.AreEqual(0.238, SnLookRules.Blend(ambient, 0.85, 0.28, 1.0), 1e-12);
            Assert.AreEqual(0.28, SnLookRules.Blend(ambient, double.NaN, 0.28, 1.0), "unset");
        }

        [TestMethod]
        public void Slot_CapturesWritesAndRestoresTheOwnValue()
        {
            SnLookKey ambient = SnLookRules.Find("ambient");
            var s = new SnLookSlot();
            Assert.IsTrue(double.IsNaN(s.Frame(ambient, 0.28, 0.85, 0.0)), "w 0: nothing written");
            Assert.IsFalse(s.Applied);
            double v = s.Frame(ambient, 0.28, 0.85, 1.0);
            Assert.AreEqual(0.238, v, 1e-12);
            Assert.IsTrue(s.Applied);
            Assert.AreEqual(0.28, s.Own, 1e-12);
            Assert.IsTrue(double.IsNaN(s.Frame(ambient, 0.238, 0.85, 1.0)), "unchanged: no write");
            Assert.AreEqual(0.259, s.Frame(ambient, 0.238, 0.85, 0.5), 1e-12);
            Assert.AreEqual(0.28, s.Frame(ambient, 0.259, 0.85, 0.0), 1e-12, "leave: the own value back");
            Assert.IsFalse(s.Applied);
            Assert.IsTrue(double.IsNaN(s.Frame(ambient, 0.28, 0.85, 0.0)));
        }

        [TestMethod]
        public void Slot_AdoptsTheGamesOwnChange_AndNeverOverwritesItOnRelease()
        {
            SnLookKey ambient = SnLookRules.Find("ambient");
            var s = new SnLookSlot();
            s.Frame(ambient, 0.28, 0.85, 1.0);                      // wrote 0.238
            double v = s.Frame(ambient, 0.5, 0.85, 1.0);            // the game set 0.5 meanwhile
            Assert.AreEqual(0.5, s.Own, 1e-12);
            Assert.AreEqual(0.425, v, 1e-12);
            Assert.AreEqual(1, s.ExternalChanges);
            Assert.IsTrue(double.IsNaN(s.Release(0.7)), "changed by the game again: left as it is");
            Assert.IsFalse(s.Applied);
            s.Frame(ambient, 0.28, 0.85, 1.0);
            s.Forget();
            Assert.IsFalse(s.Applied);
            Assert.IsTrue(double.IsNaN(s.Release(0.238)), "forgotten (scene change): nothing written");
        }

        [TestMethod]
        public void Ease_RestoresWithinTheEaseTime()
        {
            double w = 1.0;
            int n = 0;
            while (w > 0 && n < 1000) { w = SnLookRules.Ease(w, 0.0, 1.0 / 60.0, 0.8); n++; }
            Assert.AreEqual(0.0, w);
            Assert.IsTrue(n >= 47 && n <= 49, n.ToString());
        }

        [TestMethod]
        public void Table_AndIsMine()
        {
            List<string> tok;
            Assert.IsTrue(SnLookRules.IsMine(new Dictionary<string, object> { { "argv", new List<object> { "look", "shadow=0.9", "ambient=0.8" } } }, out tok));
            CollectionAssert.AreEqual(new[] { "shadow=0.9", "ambient=0.8" }, tok);
            Assert.IsTrue(SnLookRules.IsMine(new Dictionary<string, object> { { "look", "status" } }, out tok));
            Assert.IsTrue(SnLookRules.IsMine(new Dictionary<string, object> { { "look", new Dictionary<string, object> { { "ambient", 0.8 } } } }, out tok));
            CollectionAssert.AreEqual(new[] { "ambient=0.8" }, tok);
            Assert.IsTrue(SnLookRules.IsMine(new List<object> { "look", "off" }, out tok));
            Assert.IsFalse(SnLookRules.IsMine(new Dictionary<string, object> { { "argv", new List<object> { "mask", "on" } } }, out tok));
            Assert.IsFalse(SnLookRules.IsMine(new Dictionary<string, object> { { "fade", 700.0 } }, out tok));
            Assert.IsFalse(SnLookRules.IsMine(null, out tok));

            var t = new SnLookTable();
            var applied = new List<string>();
            List<string> p = t.Apply(new[] { "shadow=0.9", "ambient=unset", "low=50", "high=400", "force=0.3", "ease=1" }, applied);
            Assert.AreEqual(0, p.Count, string.Join("; ", p.ToArray()));
            Assert.AreEqual(0.9, t.Get(SnLookRules.Find("shadow")));
            Assert.IsTrue(double.IsNaN(t.Get(SnLookRules.Find("ambient"))));
            Assert.AreEqual(50, t.LowY); Assert.AreEqual(400, t.HighY); Assert.AreEqual(0.3, t.Force); Assert.AreEqual(1, t.EaseS);
            p = t.Apply(new[] { "shadow=2", "fogColor=1", "high=10" }, applied);
            Assert.AreEqual(3, p.Count, "out of range, unknown, high below low (fixed to low + 1)");
            Assert.AreEqual(51, t.HighY);
            t.Apply(new[] { "reset" }, applied);
            Assert.AreEqual(1.5, t.Get(SnLookRules.Find("ambient")));   // the default since
            StringAssert.Contains(t.Describe(), "shadow=0.6");
            t.Apply(SnLookRules.Split("off"), applied);
            Assert.IsFalse(t.Enabled);
        }

        [TestMethod]
        public void RowCadence_EdgesCapped()
        {
            var c = new SnLookRowCadence();
            Assert.IsTrue(c.Due(5000, true, false));
            Assert.IsFalse(c.Due(6000, true, false));
            Assert.IsTrue(c.Due(7000, true, false));
            int edges = 0;
            for (int i = 0; i < 9; i++) if (c.Due(9000UL + (ulong)i, false, true)) edges++;
            Assert.AreEqual(SnLookRules.EdgeRowsPerSecond, edges);
            Assert.AreEqual(9 - SnLookRules.EdgeRowsPerSecond, c.Dropped);
        }
    }
}
