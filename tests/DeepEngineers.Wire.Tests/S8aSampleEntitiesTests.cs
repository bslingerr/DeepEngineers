using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>S8a planet v2, the sample (route B's first slice): the entity file SE writes and Subnautica reads.</summary>
    [TestClass]
    public class S8aSampleEntitiesTests
    {
        [TestMethod]
        public void File_RoundTrip()
        {
            var recs = new List<S8aSampleRecord>
            {
                new S8aSampleRecord { Key = 7, ClassId = "99cdec62-302b-4999-ba49-f50c73575a4d", Family = "flora", Level = 0, X = 4155.12345, Y = -120.5, Z = -19560.25,
                                      Qx = 0.0, Qy = 0.70710678, Qz = 0.0, Qw = 0.70710678, Sx = 1.0, Sy = 1.25, Sz = 1.0 },
                new S8aSampleRecord { Key = 0x1234, Slot = true, Family = "slot", Level = 0, X = 1.0, Y = 2.0, Z = 3.0, Qw = 1.0, Sx = 1, Sy = 1, Sz = 1,
                                      SlotBiome = 1501, SlotAllowed = 16, SlotDensity = 4.603158 },
            };
            string text = S8aSampleEntities.Format(5, 9, 0xabcdefUL, 0x1122334455667788UL, -640.5, -96.25, -192.0, recs);
            long seq;
            uint cs;
            ulong ck, sid;
            double[] cn;
            List<S8aSampleRecord> back;
            Assert.IsTrue(S8aSampleEntities.TryParse(text, out seq, out cs, out ck, out sid, out cn, out back), text);
            Assert.AreEqual(5L, seq);
            Assert.AreEqual(9u, cs);
            Assert.AreEqual(0xabcdefUL, ck);
            Assert.AreEqual(0x1122334455667788UL, sid);
            Assert.AreEqual(-640.5, cn[0], 1e-9);
            Assert.AreEqual(2, back.Count);
            Assert.AreEqual("99cdec62-302b-4999-ba49-f50c73575a4d", back[0].ClassId);
            Assert.IsFalse(back[0].Slot);
            Assert.AreEqual(4155.1235, back[0].X, 1e-9);           // 0.1 mm
            Assert.AreEqual(1.25, back[0].Sy, 1e-9);
            Assert.IsTrue(back[1].Slot);
            Assert.IsNull(back[1].ClassId);
            Assert.AreEqual(1501, back[1].SlotBiome);
            Assert.AreEqual(16, back[1].SlotAllowed);
            Assert.AreEqual(4.603158, back[1].SlotDensity, 1e-9);
            Assert.AreEqual(0x1234UL, back[1].Key);
        }

        [TestMethod]
        public void File_RejectsTruncatedAndForeign()
        {
            var recs = new List<S8aSampleRecord> { new S8aSampleRecord { Key = 1, ClassId = "a", Family = "prop", Qw = 1, Sx = 1, Sy = 1, Sz = 1 } };
            string text = S8aSampleEntities.Format(1, 1, 1, 1, 0, 0, 0, recs);
            long seq;
            uint cs;
            ulong ck, sid;
            double[] cn;
            List<S8aSampleRecord> back;
            Assert.IsFalse(S8aSampleEntities.TryParse(text.Substring(0, text.IndexOf('\n') + 1), out seq, out cs, out ck, out sid, out cn, out back), "the count names a record that is missing");
            Assert.IsFalse(S8aSampleEntities.TryParse("de-s8a-outcrops 1 1 1 0\n", out seq, out cs, out ck, out sid, out cn, out back));
        }

        [TestMethod]
        public void Radius_ByCellLevel_AndJitterBounded()
        {
            Assert.AreEqual(100.0, S8aSampleEntities.RadiusOf(0));
            Assert.AreEqual(400.0, S8aSampleEntities.RadiusOf(3));
            for (int j = 1; j < 6; j++)
            {
                double dx, dy, dz;
                S8aSampleEntities.SlotJitter(0xfeedUL, j, out dx, out dy, out dz);
                Assert.IsTrue(dx * dx + dy * dy + dz * dz <= S8aSampleEntities.SlotJitterM * S8aSampleEntities.SlotJitterM + 1e-9);
                double ex, ey, ez;
                S8aSampleEntities.SlotJitter(0xfeedUL, j, out ex, out ey, out ez);
                Assert.AreEqual(dx, ex);
            }
        }
    }
}
