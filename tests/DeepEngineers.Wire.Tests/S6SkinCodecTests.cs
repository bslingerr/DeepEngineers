using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The C4 type 6 SkinSet payload (CONTRACT §5.4): encode, decode, content hashes and every reason a skin is dropped (skinBad).</summary>
    [TestClass]
    public unsafe class SkinCodecTests
    {
        private static List<SkinSectionData> SampleSkin()
        {
            var cells = new List<I32x3>();
            for (int x = -6; x <= 6; x++) for (int y = -2; y <= 2; y++) for (int z = -15; z <= 15; z++) cells.Add(new I32x3(x, y, z));
            var segs = new List<CellRange>();
            CollisionSkinMath.MergeCellsGreedy(cells, segs);
            var rot = new SeMatrix(new Vec3(0.6, 0.8, 0), new Vec3(-0.8, 0.6, 0), new Vec3(0, 0, 1), Vec3.Zero);
            var obbs = new List<SkinBoxData> { CollisionSkinMath.ObbFromModelAabb(new Vec3(-1, -1, -6), new Vec3(1, 1, 6), rot, new Vec3(2, 1, 3), 1.0) };
            return CollisionSkinMath.BuildSections(segs, obbs, 2.5, true);
        }

        private static bool Decode(byte[] payload, List<SkinSectionData> sections, out SkinSetHeader h, out string error)
        {
            return SkinCodec.TryDecode(payload, 0, payload.Length, out h, sections, out error);
        }

        [TestMethod]
        public void S6Codec_RoundTrip()
        {
            List<SkinSectionData> skin = SampleSkin();
            byte[] p = SkinCodec.Encode(0x1122334455667788UL, 3, skin);
            Assert.AreEqual(SkinCodec.PayloadBytes(skin), p.Length);
            Assert.AreEqual(16 + skin.Sum(s => 24 + 64 * s.Boxes.Count), p.Length);
            string err;
            Assert.IsTrue(SkinCodec.Validate(skin, out err), err);
            var got = new List<SkinSectionData>();
            SkinSetHeader h;
            Assert.IsTrue(Decode(p, got, out h, out err), err);
            Assert.AreEqual(0x1122334455667788UL, h.entityId);
            Assert.AreEqual(3u, h.skinVersion);
            Assert.AreEqual((uint)skin.Count, h.sectionCount);
            Assert.AreEqual(skin.Count, got.Count);
            for (int i = 0; i < skin.Count; i++)
            {
                Assert.AreEqual(skin[i].SectionId, got[i].SectionId);
                Assert.AreEqual(skin[i].ContentHash, got[i].ContentHash, "the decoder hashes the same bytes");
                Assert.AreEqual(skin[i].Boxes.Count, got[i].Boxes.Count);
                Assert.AreEqual(skin[i].Kind, got[i].Kind);
                Assert.IsTrue((skin[i].Centre - got[i].Centre).Length < 1e-5);
                for (int k = 0; k < skin[i].Boxes.Count; k++)
                {
                    SkinBoxData a = skin[i].Boxes[k], b = got[i].Boxes[k];
                    Assert.AreEqual(a.Flags, b.Flags);
                    Assert.IsTrue((a.Centre - b.Centre).Length < 1e-5 && (a.Half - b.Half).Length < 1e-6 && (a.E0 - b.E0).Length < 1e-7);
                }
                Assert.AreEqual(CollisionSkinMath.SectionContentHash(got[i]), got[i].ContentHash, "re-encoding decoded floats gives the same bytes");
            }
            // re-encoding the decoded skin reproduces the payload byte for byte
            CollectionAssert.AreEqual(p, SkinCodec.Encode(h.entityId, h.skinVersion, got));
            // the unsafe overload and an offset
            var padded = new byte[p.Length + 10];
            Array.Copy(p, 0, padded, 7, p.Length);
            Assert.IsTrue(SkinCodec.TryDecode(padded, 7, p.Length, out h, got, out err), err);
            fixed (byte* bp = p) Assert.IsTrue(SkinCodec.TryDecode(bp, p.Length, out h, got, out err), err);
        }

        [TestMethod]
        public void S6Codec_EmptySkinRemovesEverything()
        {
            byte[] p = SkinCodec.Encode(5, 2, new List<SkinSectionData>());
            Assert.AreEqual(16, p.Length);
            var got = new List<SkinSectionData> { new SkinSectionData() };
            SkinSetHeader h;
            string err;
            Assert.IsTrue(Decode(p, got, out h, out err), err);
            Assert.AreEqual(0, got.Count, "cleared");
            Assert.AreEqual(0u, h.sectionCount);
        }

        [TestMethod]
        public void S6Codec_CharacterSectionsAreSkippedAndCounted()
        {
            List<SkinSectionData> skin = SampleSkin();
            var ch = new SkinSectionData { SectionId = 0, Centre = new Vec3(0, 0.9, 0), Kind = SkinSectionKind.Character };
            ch.Boxes.Add(SkinBoxData.Aligned(new Vec3(0, 0.9, 0), new Vec3(0.25, 0.9, 0.25), 0));
            skin.Insert(0, ch);
            byte[] p = SkinCodec.Encode(1, 1, skin);
            var got = new List<SkinSectionData>();
            SkinSetHeader h;
            int ignored;
            string err;
            Assert.IsTrue(SkinCodec.TryDecode(p, 0, p.Length, out h, got, out ignored, out err), err);
            Assert.AreEqual(1, ignored);
            Assert.AreEqual(skin.Count - 1, got.Count);
            Assert.IsTrue(got.All(s => s.Kind == SkinSectionKind.Grid));
        }

        [TestMethod]
        public void S6Codec_BadPayloadsAreDroppedWhole()
        {
            List<SkinSectionData> skin = SampleSkin().Take(2).ToList();
            byte[] good = SkinCodec.Encode(9, 4, skin);
            int sec0 = 24 + 64 * skin[0].Boxes.Count;
            var cases = new List<Tuple<string, byte[]>>
            {
                Tuple.Create("short", good.Take(15).ToArray()),
                Tuple.Create("one byte less", good.Take(good.Length - 1).ToArray()),
                Tuple.Create("one byte more", good.Concat(new byte[1]).ToArray()),
                Tuple.Create("version 0", Patch(good, 8, BitConverter.GetBytes(0u))),
                Tuple.Create("count 3", Patch(good, 12, BitConverter.GetBytes(3u))),
                Tuple.Create("count huge", Patch(good, 12, BitConverter.GetBytes(0xFFFFFFFFu))),
                Tuple.Create("kind 3", Patch(good, 16 + 20, BitConverter.GetBytes(3u))),
                Tuple.Create("kind 0", Patch(good, 16 + 20, BitConverter.GetBytes(0u))),
                Tuple.Create("box count 0", Patch(good, 16 + 16, BitConverter.GetBytes(0u))),
                Tuple.Create("box count big", Patch(good, 16 + 16, BitConverter.GetBytes(1000000u))),
                Tuple.Create("ids not ascending", Patch(good, 16 + sec0, BitConverter.GetBytes(skin[0].SectionId))),
                Tuple.Create("section centre NaN", Patch(good, 16 + 4, BitConverter.GetBytes(float.NaN))),
                Tuple.Create("box centre inf", Patch(good, 16 + 24, BitConverter.GetBytes(float.PositiveInfinity))),
                Tuple.Create("half 0", Patch(good, 16 + 24 + 12, BitConverter.GetBytes(0f))),
                Tuple.Create("half -1", Patch(good, 16 + 24 + 16, BitConverter.GetBytes(-1f))),
                Tuple.Create("half 4.01", Patch(good, 16 + 24 + 20, BitConverter.GetBytes(4.01f))),
                Tuple.Create("axes not unit", Patch(good, 16 + 24 + 24, BitConverter.GetBytes(0.9f))),
                Tuple.Create("unknown flag", Patch(good, 16 + 24 + 60, BitConverter.GetBytes(0x10000u))),
                Tuple.Create("rotated flag missing", Patch(Patch(good, 16 + 24 + 24, Floats(0, 1, 0)), 16 + 24 + 36, Floats(-1, 0, 0))),
                Tuple.Create("left-handed", Patch(Patch(good, 16 + 24 + 48, Floats(0, 0, -1)), 16 + 24 + 60, BitConverter.GetBytes(1u))),
            };
            foreach (Tuple<string, byte[]> c in cases)
            {
                var got = new List<SkinSectionData> { new SkinSectionData() };
                SkinSetHeader h;
                string err;
                Assert.IsFalse(Decode(c.Item2, got, out h, out err), c.Item1);
                Assert.IsNotNull(err, c.Item1);
                Assert.AreEqual(0, got.Count, c.Item1 + ": nothing kept");
            }
            // 4.0 m + rounding (up to 1e-4 m) is still accepted; a rotated box with a permutation frame is fine when flagged
            var ok = new List<SkinSectionData>();
            SkinSetHeader hh;
            string e2;
            Assert.IsTrue(Decode(Patch(good, 16 + 24 + 12, BitConverter.GetBytes(4.00005f)), ok, out hh, out e2), e2);
            Assert.IsFalse(Decode(Patch(good, 16 + 24 + 12, BitConverter.GetBytes(4.0002f)), ok, out hh, out e2));
            byte[] flagged = Patch(Patch(Patch(good, 16 + 24 + 24, Floats(0, 1, 0)), 16 + 24 + 36, Floats(-1, 0, 0)), 16 + 24 + 60, BitConverter.GetBytes(1u));
            Assert.IsTrue(Decode(flagged, ok, out hh, out e2), e2);
            // larger than 2 MiB
            var big = new byte[CombatConventions.Limits.SkinMaxBytes + 16];
            Array.Copy(good, big, 16);
            Assert.IsFalse(Decode(big, ok, out hh, out e2));
            StringAssert.Contains(e2, "larger");
        }

        private static byte[] Patch(byte[] src, int offset, byte[] value)
        {
            var b = (byte[])src.Clone();
            Array.Copy(value, 0, b, offset, value.Length);
            return b;
        }

        private static byte[] Floats(params float[] f)
        {
            return f.SelectMany(BitConverter.GetBytes).ToArray();
        }

        [TestMethod]
        public void S6Codec_EncodeRejectsBadStructure()
        {
            List<SkinSectionData> skin = SampleSkin();
            var swapped = new List<SkinSectionData> { skin[1], skin[0] };
            Assert.ThrowsException<ArgumentException>(() => SkinCodec.Encode(1, 1, swapped));
            var empty = new List<SkinSectionData> { new SkinSectionData { SectionId = 1 } };
            Assert.ThrowsException<ArgumentException>(() => SkinCodec.Encode(1, 1, empty));
            Assert.ThrowsException<ArgumentException>(() => SkinCodec.Encode(1, 1, new List<SkinSectionData> { null }));
            string err;
            Assert.IsFalse(SkinCodec.Validate(swapped, out err));
            var badBox = new SkinSectionData { SectionId = 2 };
            badBox.Boxes.Add(SkinBoxData.Aligned(Vec3.Zero, new Vec3(5, 1, 1), 0));
            Assert.IsFalse(SkinCodec.Validate(new List<SkinSectionData> { badBox }, out err));
            StringAssert.Contains(err, "half extent");
            Assert.AreEqual(16, SkinCodec.PayloadBytes(null));
        }

        [TestMethod]
        public void S6Codec_SectionBytesAreTheHashedBytes()
        {
            foreach (SkinSectionData s in SampleSkin())
            {
                byte[] b = SkinCodec.SectionBytes(s);
                Assert.AreEqual(24 + 64 * s.Boxes.Count, b.Length);
                Assert.AreEqual(LayoutHash.Fnv1a64(b), s.ContentHash);
                Assert.AreEqual(s.SectionId, BitConverter.ToUInt32(b, 0));
                Assert.AreEqual((uint)s.Boxes.Count, BitConverter.ToUInt32(b, 16));
                Assert.AreEqual(s.Kind, BitConverter.ToUInt32(b, 20));
            }
            // one moved box changes only its section's hash
            List<SkinSectionData> a = SampleSkin(), c = SampleSkin();
            SkinBoxData moved = c[1].Boxes[0];
            moved.Centre = moved.Centre + new Vec3(0.001, 0, 0);
            c[1].Boxes[0] = moved;
            c[1].ContentHash = CollisionSkinMath.SectionContentHash(c[1]);
            for (int i = 0; i < a.Count; i++)
                Assert.AreEqual(i != 1, a[i].ContentHash == c[i].ContentHash, "section " + i);
        }
    }
}
