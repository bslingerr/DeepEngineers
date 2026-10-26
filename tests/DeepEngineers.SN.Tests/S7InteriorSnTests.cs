using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7 interiors, the Subnautica side's Unity-free parts (CONTRACT §5.4 "C4 RoomSet (S7)", "Interior chunks"): the RoomSet
 /// store behind <see cref="MirrorState.Feed"/> (decode, version, malformed, before/after GridAdd, EntityRemove,
 /// ClearAll), the chunk class in the S5 cell key and the re-keying that dirties only changed cells, the C1 interior
 /// bytes in <see cref="FrameReader"/>, the inside-walkable rule (fresh C1, driving, patch A/B, S7.M1) and the N2 trace.
 /// </summary>
    [TestClass]
    public unsafe class S7InteriorSnTests
    {
        private const ulong Model = 0x1234UL;

        private static byte[] Bytes<T>(T v) where T : unmanaged
        {
            var a = new byte[sizeof(T)];
            fixed (byte* p = a) *(T*)p = v;
            return a;
        }

        private static C4Outcome Feed(MirrorState s, uint type, byte[] payload)
        {
            ulong id;
            fixed (byte* p = payload) return s.Feed(type, p, payload.Length, out id);
        }

        private static PartAdd Part(ulong entity, ulong key, float tx, float ty, float tz)
        {
            var p = new PartAdd { entityId = entity, partKey = key, modelKey = Model, colorMaskHSV = new F32x3(0f, 0.5f, 0.1f), flags = MirrorConventions.PartFlags(1, 0) };
            float[] rows = { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz };
            for (int i = 0; i < 12; i++) p.local[i] = rows[i];
            return p;
        }

        private static GridAdd Grid(ulong id, uint targetClass = 1)
        {
            return new GridAdd { entityId = id, targetClass = targetClass, flags = S5Conv.GridVersion1, parentEntityId = 0, worldEpoch = 0 };
        }

        // the S7.N1 room: inner 3 x 2 x 3 cells around the origin of a large grid (2.5 m)
        private static readonly CellBox[] Room = { new CellBox(-1, 0, -1, 1, 1, 1) };

        private static MirrorState GridWithParts()
        {
            var s = new MirrorState();
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(Grid(1))));
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 10, 0f, 0f, 0f)));      // cell (0,0,0): inside
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 11, 5f, 0f, 0f)));      // cell (2,0,0): the wall (boundary)
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 12, 10f, 0f, 0f)));     // cell (4,0,0): outside (exterior)
            return s;
        }

        [TestMethod]
        public void RoomSet_ClassifiesTheParts_DirtiesOnlyTheChangedCells()
        {
            MirrorState s = GridWithParts();
            GridRecord g = s.Grids[1];
            foreach (PartRecord p in g.Parts.Values) Assert.AreEqual(0, p.Cell.C, "no room set yet: Exterior");
            Assert.AreEqual(1, g.CellParts.Count, "one 20 m S5 cell before");
            g.DirtyCells.Clear();
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(1UL, Room, false)));
            Assert.AreEqual(1, s.Rooms.Count);
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, g.Parts[10].Cell.C);
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, g.Parts[11].Cell.C);
            Assert.AreEqual(S7Conventions.InteriorClass.Exterior, g.Parts[12].Cell.C);
            Assert.AreEqual(3, g.CellParts.Count, "the S5 cell split into three chunk keys");
            Assert.AreEqual(3, g.DirtyCells.Count, "the old cell and the two new ones are rebuilt");
            Assert.AreEqual(2, s.Rooms.PartsMoved);
            Assert.AreEqual(g.Parts[10].Cell.X, g.Parts[12].Cell.X, "same S5 cell coordinates, other class");
            // the same set again: nothing moves
            g.DirtyCells.Clear();
            Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(1UL, Room, false));
            Assert.AreEqual(0, g.DirtyCells.Count);
            // a part added later gets its class at once
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 13, 2.5f, 2.5f, 2.5f)));
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, g.Parts[13].Cell.C);
            // count 0: no airtight room any more (breach): everything back to Exterior
            g.DirtyCells.Clear();
            Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(1UL, new CellBox[0], false));
            Assert.AreEqual(0, s.Rooms.Count);
            foreach (PartRecord p in g.Parts.Values) Assert.AreEqual(0, p.Cell.C);
            Assert.AreEqual(1, g.CellParts.Count);
            Assert.IsNull(g.Rooms);
        }

        [TestMethod]
        public void RoomSet_VersionMalformedBeforeGridAddRemoveClear()
        {
            var s = new MirrorState();
            byte[] v0 = RoomSetCodec.Encode(5UL, Room, false);
            fixed (byte* b = v0) *(uint*)(b + 12) = 0;
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.RoomSet, v0));
            Assert.AreEqual(0, s.Rooms.Count, "version 0 ignored");
            Assert.AreEqual(1, s.Rooms.VersionIgnored);
            Assert.AreEqual(1, s.ConventionsRejected);
            Assert.AreEqual(C4Outcome.Error, Feed(s, C4Type.RoomSet, new byte[10]));
            Assert.AreEqual(1, s.Rooms.Malformed);
            // a set before its GridAdd (a re-sent stream): the record takes it when it is created
            Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(5UL, Room, false));
            Feed(s, C4Type.GridAdd, Bytes(Grid(5)));
            Assert.IsNotNull(s.Grids[5].Rooms);
            Feed(s, C4Type.PartAdd, Bytes(Part(5, 1, 0f, 2.5f, 0f)));
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, s.Grids[5].Parts[1].Cell.C);
            // EntityRemove drops it; ClearAll drops everything
            Feed(s, C4Type.EntityRemove, Bytes(new EntityRemove { entityId = 5 }));
            Assert.AreEqual(0, s.Rooms.Count);
            Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(6UL, Room, false));
            Assert.AreEqual(1, s.Rooms.Count);
            Feed(s, C4Type.ClearAll, Bytes(new ClearAll { worldEpoch = 0, linkEpoch = 0 }));
            Assert.AreEqual(0, s.Rooms.Count);
        }

        [TestMethod]
        public void SmallGrid_UsesHalfMetreCells()
        {
            var s = new MirrorState();
            Feed(s, C4Type.GridAdd, Bytes(Grid(2, targetClass: GridClass.SmallGrid)));
            Feed(s, C4Type.RoomSet, RoomSetCodec.Encode(2UL, new[] { new CellBox(0, 0, 0, 3, 3, 3) }, false));
            Feed(s, C4Type.PartAdd, Bytes(Part(2, 1, 1.5f, 1.5f, 1.5f)));   // cell 3: inside
            Feed(s, C4Type.PartAdd, Bytes(Part(2, 2, 2.0f, 0.0f, 0.0f)));   // cell 4: boundary
            Feed(s, C4Type.PartAdd, Bytes(Part(2, 3, 2.5f, 0.0f, 0.0f)));   // cell 5: exterior
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, s.Grids[2].Parts[1].Cell.C);
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, s.Grids[2].Parts[2].Cell.C);
            Assert.AreEqual(S7Conventions.InteriorClass.Exterior, s.Grids[2].Parts[3].Cell.C);
        }

        [TestMethod]
        public void CellKey_ClassIsPartOfTheKey_TextUnchangedForExterior()
        {
            var a = new CellKey(1, 2, 3);
            var b = a.WithClass(S7Conventions.InteriorClass.Inside);
            Assert.AreNotEqual(a, b);
            Assert.AreEqual("1.2.3", a.ToString(), "pre-S7 text kept");
            Assert.AreEqual("1.2.3.in", b.ToString());
            Assert.AreEqual("1.2.3.bd", a.WithClass(S7Conventions.InteriorClass.Boundary).ToString());
            var set = new HashSet<CellKey> { a, b, a.WithClass(1) };
            Assert.AreEqual(3, set.Count);
        }

        [TestMethod]
        public void FrameReader_DecodesTheInteriorExtension_FallsBackToBit3()
        {
            byte* mem = stackalloc byte[256 + 64];
            for (int i = 0; i < 320; i++) mem[i] = 0;
            var f = new SeFrame { tick = 99 };
            var ex = new C1Interior { Flags = (byte)(S7Conventions.C1InteriorFlags.InRoom | S7Conventions.C1InteriorFlags.Pressurization), RoomIndex = 2, RoomGridId = 77 };
            S7InteriorWire.Write(ref f, in ex);
            Seqlock.Write(mem, f);
            var r = new FrameReader(mem, 10000000);
            Assert.AreEqual(C1Poll.Fresh, r.Poll());
            Assert.IsTrue(r.InteriorValid && r.InteriorInRoom && r.InteriorSeen);
            Assert.AreEqual(77UL, r.Interior.RoomGridId);
            Assert.AreEqual(99UL, r.InteriorTick);
            Assert.IsTrue(r.InteriorMs > 0);
            S7InteriorWire.Erase(ref f);
            f.flags = SeFrameFlags.CameraInAirtightRoom;
            f.tick = 100;
            Seqlock.Write(mem, f);
            Assert.AreEqual(C1Poll.Fresh, r.Poll());
            Assert.IsFalse(r.InteriorValid, "an SE before S7");
            Assert.IsTrue(r.InteriorInRoom, "bit 3 alone");
            r.Reset();
            Assert.IsFalse(r.InteriorSeen || r.InteriorInRoom);
        }

        [TestMethod]
        public void InsideRule_FreshC1_Driving_PatchAB_M1()
        {
            Assert.IsTrue(InsideRule.Inside(true, false, true, true, true, 1000, 1050));
            Assert.IsTrue(InsideRule.Inside(true, false, true, true, true, 1000, 1100), "100 ms is still fresh");
            Assert.IsFalse(InsideRule.Inside(true, false, true, true, true, 1000, 1101), "stale C1");
            Assert.IsFalse(InsideRule.Inside(false, false, true, true, true, 1000, 1050), "patch off (A/B)");
            Assert.IsFalse(InsideRule.Inside(true, true, true, true, true, 1000, 1050), "S7.M1");
            Assert.IsFalse(InsideRule.Inside(true, false, false, true, true, 1000, 1050), "the bridge not driving the camera");
            Assert.IsFalse(InsideRule.Inside(true, false, true, false, true, 1000, 1050), "no C1 yet");
            Assert.IsFalse(InsideRule.Inside(true, false, true, true, false, 1000, 1050), "outside");
            Assert.IsFalse(InsideRule.Inside(true, false, true, true, true, 2000, 1050), "a clock that went back");
        }

        [TestMethod]
        public void Trace_CountsCallersInsideAndOutside_ListsTheSilentReaders()
        {
            var t = new InsideTrace();
            Assert.AreEqual(13, InsideTrace.Direct.Length);
            Assert.AreEqual(5, InsideTrace.ViaIsInside.Length);
            Assert.IsFalse(t.Active(0));
            t.Start(1000, 10);
            Assert.IsTrue(t.Active(10999));
            Assert.IsFalse(t.Active(11000));
            t.Note("WaterscapeVolumeOnCamera", false, true);
            t.Note("WaterscapeVolumeOnCamera", false, true);
            t.Note("Player", false, false);
            t.Note("ArmsController", true, true);
            t.Note(null, false, true);
            Assert.AreEqual(2, t.InsideDirect["WaterscapeVolumeOnCamera"]);
            Assert.AreEqual(1, t.OutsideDirect["Player"]);
            Assert.AreEqual(1, t.InsideVia["ArmsController"]);
            Assert.AreEqual(1, t.Unknown);
            List<string> silent = t.NotCalled();
            Assert.IsFalse(silent.Contains("WaterscapeVolumeOnCamera"));
            Assert.IsFalse(silent.Contains("Player"));
            Assert.IsTrue(silent.Contains("TemperatureDamage"));
            Assert.IsFalse(silent.Contains("ArmsController (via IsInside)"));
            Assert.IsTrue(silent.Contains("WarpBall (via IsInside)"));
            Assert.AreEqual(13 - 2 + 5 - 1, silent.Count);
            t.Start(20000, 1);
            Assert.AreEqual(0, t.Calls, "a new window starts empty");
        }
    }
}
