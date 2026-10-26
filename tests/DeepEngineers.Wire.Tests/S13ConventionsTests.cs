using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S13 conventions pin (CONTRACT §5.4 "S13 conventions"): the canonical text's hash, line and byte counts, the C1 S13
 /// extension inside the C1 pad after the swim, loco and interior extensions, its codec, and the light fit (SE's spot curve
 /// against Unity's built-in attenuation) the Python twin (tests/s13/analyze_s13.py) recomputes.
 /// </summary>
    [TestClass]
    public class S13ConventionsTests
    {
        [TestMethod]
        public void CanonicalText_IsPinned()
        {
            string t = S13Conventions.CanonicalText();
            Assert.AreEqual(S13Conventions.PinnedHash, S13Conventions.Hash(), "S13 pin moved: re-pin Wire, CONTRACT §5.4 and the Python twin together");
            Assert.AreEqual(S13Conventions.CanonicalLines, t.Split('\n').Length - 1);
            Assert.AreEqual(S13Conventions.CanonicalBytes, System.Text.Encoding.ASCII.GetByteCount(t));
            Assert.IsTrue(t.StartsWith(S13Conventions.Header + "\n", StringComparison.Ordinal));
            Assert.IsTrue(t.EndsWith("\n", StringComparison.Ordinal));
            foreach (char ch in t) Assert.IsTrue(ch < 128, "ASCII only");
        }

        [TestMethod]
        public void C1Extension_LiesInThePadAfterTheEarlierExtensions()
        {
            Assert.AreEqual(0xC0, S13CharWire.Offset);
            Assert.AreEqual(8, S13CharWire.Size);
            Assert.IsTrue(S13CharWire.Offset >= 0xC0, "after the swim (0x94-0x9F), loco (0xA0-0xAF) and interior (0xB0-0xBF) extensions");
            Assert.IsTrue(S13CharWire.Offset + S13CharWire.Size <= SeFrameLimits.BonesOffset, "inside the C1 pad, before the bones at 0x100");
            Assert.IsTrue(S7InteriorWire.Offset + S7InteriorWire.Size <= S13CharWire.Offset);
            Assert.AreEqual(0x4f3a35f6fc1eaf31UL, LayoutHash.Compiled, "layout 1.0 unchanged");
            Assert.AreEqual(0x832ba93ca25bc307UL, SwimWire.Hash, "the S5a swim extension pin unchanged");
        }

        [TestMethod]
        public void Codec_RoundTrips_AndLeavesTheOtherExtensionsAlone()
        {
            var f = new SeFrame();
            var sw = new SwimWireState { Flags = SwimWireFlags.InWater, Mode = 1, O2 = 200 };
            SwimWire.Write(ref f, ref sw);
            var s = new S13CharWireState { Flags = (byte)(S13Conventions.C1Flags.Valid | S13Conventions.C1Flags.BodyBasis), VRightCm = -123, VUpCm = 45, VForwardCm = 32767 };
            S13CharWire.Write(ref f, ref s);
            S13CharWireState r;
            Assert.IsTrue(S13CharWire.TryRead(ref f, out r));
            Assert.AreEqual(S13CharWire.Version, r.Version);
            Assert.AreEqual(s.Flags, r.Flags);
            Assert.AreEqual(-123, r.VRightCm);
            Assert.AreEqual(45, r.VUpCm);
            Assert.AreEqual(32767, r.VForwardCm);
            SwimWireState sw2;
            Assert.IsTrue(SwimWire.TryRead(ref f, out sw2), "the swim extension is untouched");
            Assert.AreEqual(200, sw2.O2);
            S13CharWire.Erase(ref f);
            Assert.IsFalse(S13CharWire.TryRead(ref f, out r));
            Assert.AreEqual(0, S13CharWire.ReadVersion(ref f));
        }

        [TestMethod]
        public void Compose_ProjectsOnTheLookFrame_RoundsAndClamps()
        {
            // look frame = identity axes: right x, up y, forward z
            S13CharWireState s = S13CharWire.Compose(S13Conventions.C1Flags.Valid, 0.125, -0.5, 400, 1, 0, 0, 0, 1, 0, 0, 0, 1);
            Assert.AreEqual(13, s.VRightCm, "12.5 cm/s: half away from zero");
            Assert.AreEqual(-50, s.VUpCm);
            Assert.AreEqual(32767, s.VForwardCm, "clamped");
            // forward along +x: a velocity along +x is pure forward, along -x pure backward
            s = S13CharWire.Compose(S13Conventions.C1Flags.Valid, -2, 0, 0, 0, 0, -1, 0, 1, 0, 1, 0, 0);
            Assert.AreEqual(-200, s.VForwardCm);
            Assert.AreEqual(0, s.VRightCm);
            s = S13CharWire.Compose(0, 5, 5, 5, 1, 0, 0, 0, 1, 0, 0, 0, 1);
            Assert.AreEqual(0, s.VForwardCm, "not valid: no velocity");
            Assert.AreEqual(0, S13CharWire.Cm(double.NaN));
            Assert.AreEqual(-32767, S13CharWire.Cm(-1e9));
        }

        [TestMethod]
        public void LightFit_MatchesThePythonTwinAndSeShape()
        {
            Assert.AreEqual(1.0, S13LightFit.Se(0, 1), 1e-12);
            Assert.AreEqual(0.0, S13LightFit.Se(1, 1), 1e-12);
            Assert.AreEqual(1.0, S13LightFit.Unity(0), 1e-12);
            Assert.AreEqual(0.0, S13LightFit.Unity(1), 1e-12);
            Assert.AreEqual(1.0 / (1 + 25 * 0.25), S13LightFit.Unity(0.5), 1e-12);
            // values the twin computes (analyze_s13.py --selftest checks the same numbers)
            Assert.AreEqual(2.42, S13LightFit.RangeFactor(1.0), 1e-9);
            Assert.AreEqual(2.82, S13LightFit.RangeFactor(0.0), 1e-9);
            Assert.AreEqual(2.16, S13LightFit.RangeFactor(2.0), 1e-9);
            Assert.AreEqual(0.504, S13LightFit.EnergyFactor(2.42), 0.001);
            Assert.AreEqual(1.0, S13LightFit.EnergyFactor(1.0), 1e-12);
            // the fit cuts the error at least 4x against the unfitted Unity curve, and keeps the in-range mean
            Assert.IsTrue(S13LightFit.Error(2.42, 1.0) * 4 < S13LightFit.Error(1.0, 1.0));
            // shape: at half the SE range the fitted curve is far closer to SE's 0.66 than Unity's 0.14
            double se = S13LightFit.Se(0.5, 1.0), un = S13LightFit.Unity(0.5), fit = S13LightFit.UnityScaled(0.5, 2.42);
            Assert.IsTrue(Math.Abs(fit - se) < 0.2 && Math.Abs(un - se) > 0.45, "se " + se + " unity " + un + " fit " + fit);
        }

        [TestMethod]
        public void ShadowLength_IsSimilarTriangles()
        {
            Assert.AreEqual(5.0, S13LightFit.ShadowLength(0.1, 0.2, 10), 1e-12, "a 10 cm caster at Unity's 0.2 m near plane: a 5 m shadow at 10 m");
            Assert.AreEqual(1.0, S13LightFit.ShadowLength(0.1, 1.0, 10), 1e-12, "at the S13 1 m near plane: 1 m");
            Assert.IsTrue(double.IsNaN(S13LightFit.ShadowLength(0.1, 0, 10)));
            Assert.IsTrue(double.IsNaN(S13LightFit.ShadowLength(0.1, 2, 1)));
        }

        [TestMethod]
        public void BlendRoots_AreCanonicalBones()
        {
            foreach (string r in S13Conventions.BlendRoots)
                Assert.IsTrue(MirrorSkeleton.IndexOf(r) >= 0, r + " is a canon1 bone");
        }
    }
}
