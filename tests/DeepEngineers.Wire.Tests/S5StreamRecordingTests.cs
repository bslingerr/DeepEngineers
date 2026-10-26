using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The <c>.derec</c> stream recording codec (CONTRACT §5.4 "Stream recording"): layout, round trip, truncation, C2 packing.</summary>
    [TestClass]
    public unsafe class S5StreamRecordingTests
    {
        [TestMethod]
        public void Layout_HeaderAndRecordHeader()
        {
            Assert.AreEqual(64, sizeof(DerecHeader));
            Assert.AreEqual(24, sizeof(DerecRecordHeader));
            string[] f = { "magic", "formatVersion", "headerBytes", "layoutHash", "conventionsHash", "wireVersion", "seVersion", "createdUnixMs", "flags", "worldEpoch", "reserved" };
            int[] o = { 0x00, 0x08, 0x0C, 0x10, 0x18, 0x20, 0x24, 0x28, 0x30, 0x34, 0x38 };
            for (int i = 0; i < f.Length; i++) Assert.AreEqual(o[i], (int)Marshal.OffsetOf(typeof(DerecHeader), f[i]), f[i]);
            Assert.AreEqual(0x04, (int)Marshal.OffsetOf(typeof(DerecRecordHeader), "bytes"));
            Assert.AreEqual(0x08, (int)Marshal.OffsetOf(typeof(DerecRecordHeader), "seTick"));
            Assert.AreEqual(0x10, (int)Marshal.OffsetOf(typeof(DerecRecordHeader), "qpc"));
            ulong m = Derec.Magic;
            Assert.AreEqual(Derec.MagicText, Encoding.ASCII.GetString(BitConverter.GetBytes(m)));
            DerecHeader h = Derec.CreateHeader(1210014, 5, Derec.FlagHasC1, 3);
            Assert.AreEqual(LayoutHash.PinnedLayout10, h.layoutHash);
            Assert.AreEqual(MirrorConventions.PinnedHash, h.conventionsHash);
            Assert.AreEqual(WireInfo.WireVersion, h.wireVersion);
            string error;
            Assert.IsTrue(Derec.Validate(h, out error), error);
            Assert.AreEqual(0, Derec.Padded(0));
            Assert.AreEqual(8, Derec.Padded(1));
            Assert.AreEqual(16, Derec.Padded(16));
        }

        [TestMethod]
        public void RoundTrip_AllKindsWithPadding()
        {
            var ms = new MemoryStream();
            using (var w = new DerecWriter(ms, Derec.CreateHeader(0, 1, Derec.FlagSynthetic | Derec.FlagHasC2, 9), false))
            {
                byte[] payload = { 1, 2, 3, 4, 5 };
                w.WriteMessage(Derec.KindC3, 10, 100, C3Type.MaterialDef, payload, 0, payload.Length);
                w.WriteMessage(Derec.KindC4, 11, 101, C4Type.PartAdd, new byte[100], 0, 100);
                w.Write(Derec.KindC2, 12, 102, new byte[0x40], 0, 0x40);
                w.WriteMarker(13, 103, "{\"ev\":\"edit\",\"kind\":\"grind\"}");
                w.WriteEnd(14, 104);
                w.WriteEnd(15, 105);
                Assert.AreEqual(5, w.Records);
                Assert.AreEqual(1, w.PerKind[Derec.KindC3]);
                Assert.AreEqual(1, w.PerKind[Derec.KindMarker]);
                Assert.AreEqual(ms.Length, w.Bytes);
                MirrorConventionsTests.AssertThrows<InvalidOperationException>(() => w.Write(Derec.KindC1, 1, 1, new byte[1], 0, 1));
            }
            Assert.AreEqual(0, ms.Length % 8);
            ms.Position = 0;
            using (var r = new DerecReader(ms, false))
            {
                Assert.AreEqual(9u, r.Header.worldEpoch);
                Assert.AreEqual(Derec.FlagSynthetic | Derec.FlagHasC2, r.Header.flags);
                DerecRecordHeader h;
                byte[] b;
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.AreEqual(Derec.KindC3, h.kind);
                Assert.AreEqual(10UL, h.seTick);
                Assert.AreEqual(100L, h.qpc);
                Assert.AreEqual(13, b.Length, "framed message, unpadded");
                Assert.AreEqual(C3Type.MaterialDef, BitConverter.ToUInt32(b, 0));
                Assert.AreEqual(5u, BitConverter.ToUInt32(b, 4));
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.AreEqual(108, b.Length);
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.AreEqual(Derec.KindC2, h.kind);
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.AreEqual("{\"ev\":\"edit\",\"kind\":\"grind\"}", Encoding.UTF8.GetString(b));
                Assert.IsFalse(r.TryNext(out h, out b));
                Assert.IsTrue(r.SawEnd);
                Assert.IsFalse(r.Truncated);
                Assert.AreEqual(5, r.Records);
            }
        }

        [TestMethod]
        public void Truncated_ReplaysToTheLastWholeRecord()
        {
            var ms = new MemoryStream();
            using (var w = new DerecWriter(ms, Derec.CreateHeader(0, 1, 0, 1), false))
            {
                w.Write(Derec.KindC4, 1, 1, new byte[16], 0, 16);
                w.Write(Derec.KindC4, 2, 2, new byte[16], 0, 16);
            }
            byte[] all = ms.ToArray();
            byte[] cut = new byte[all.Length - 5];
            Array.Copy(all, cut, cut.Length);
            using (var r = new DerecReader(new MemoryStream(cut)))
            {
                DerecRecordHeader h;
                byte[] b;
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.IsFalse(r.TryNext(out h, out b));
                Assert.IsTrue(r.Truncated);
                Assert.IsFalse(r.SawEnd);
            }
            using (var r = new DerecReader(new MemoryStream(all)))
            {
                DerecRecordHeader h;
                byte[] b;
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.IsTrue(r.TryNext(out h, out b));
                Assert.IsFalse(r.TryNext(out h, out b));
                Assert.IsFalse(r.Truncated, "no end record is not a truncation");
                Assert.IsFalse(r.SawEnd);
            }
            all[0] = (byte)'X';
            MirrorConventionsTests.AssertThrows<InvalidDataException>(() => new DerecReader(new MemoryStream(all)));
            MirrorConventionsTests.AssertThrows<InvalidDataException>(() => new DerecReader(new MemoryStream(new byte[10])));
        }

        [TestMethod]
        public void C2_PackAndUnpack()
        {
            using (var region = new NativeBuffer(SeEntitiesLimits.End))
            using (var back = new NativeBuffer(SeEntitiesLimits.End))
            {
                byte* p = region.Ptr;
                var h = (SeEntitiesHeader*)p;
                h->seq = 2; h->entityCount = 3; h->subpartCount = 2; h->lightCount = 1; h->tick = 77; h->worldEpoch = 4;
                for (int i = 0; i < 3; i++) ((SeEntity*)(p + SeEntitiesLimits.EntitiesOffset))[i].entityId = (ulong)(100 + i);
                for (int i = 0; i < 2; i++) ((SeSubpart*)(p + SeEntitiesLimits.SubpartsOffset))[i].partKey = (ulong)(200 + i);
                ((SeLight*)(p + SeEntitiesLimits.LightsOffset))[0].ownerId = 300;
                int n = Derec.C2SnapshotBytes(3, 2, 1);
                Assert.AreEqual(0x40 + 3 * 96 + 2 * 64 + 64, n);
                var dst = new byte[n];
                Assert.AreEqual(n, Derec.PackC2(p, dst));
                Assert.AreEqual(-1, Derec.PackC2(p, new byte[n - 1]));
                Assert.IsTrue(Derec.UnpackC2(dst, 0, n, back.Ptr));
                Assert.AreEqual(77UL, ((SeEntitiesHeader*)back.Ptr)->tick);
                Assert.AreEqual(102UL, ((SeEntity*)(back.Ptr + SeEntitiesLimits.EntitiesOffset))[2].entityId);
                Assert.AreEqual(201UL, ((SeSubpart*)(back.Ptr + SeEntitiesLimits.SubpartsOffset))[1].partKey);
                Assert.AreEqual(300UL, ((SeLight*)(back.Ptr + SeEntitiesLimits.LightsOffset))[0].ownerId);
                Assert.IsFalse(Derec.UnpackC2(dst, 0, n - 8, back.Ptr));
                h->subpartCount = 5000;
                Assert.AreEqual(-1, Derec.PackC2(p, new byte[1 << 20]));
            }
        }

        [TestMethod]
        public void Names_AndPaths()
        {
            Assert.IsTrue(Derec.IsValidName("ship_2000-a.1"));
            Assert.IsFalse(Derec.IsValidName(""));
            Assert.IsFalse(Derec.IsValidName(".hidden"));
            Assert.IsFalse(Derec.IsValidName(@"..\x"));
            Assert.IsFalse(Derec.IsValidName("a/b"));
            Assert.IsFalse(Derec.IsValidName(new string('a', 65)));
            StringAssert.EndsWith(Derec.RecordingPath(@"C:\r", "n"), @"\n.derec");
            StringAssert.EndsWith(Derec.SidecarPath(@"C:\r", "n"), @"\n.json");
            StringAssert.EndsWith(Derec.DefaultDirectory(), @"DeepEngineers\recordings");
            MirrorConventionsTests.AssertThrows<ArgumentException>(() => Derec.RecordingPath(@"C:\r", "a b"));
        }
    }
}
