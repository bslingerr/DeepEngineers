using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7 interiors (CONTRACT §5.4 "S7 conventions"): the C1 interior codec, the RoomSet codec and content hash, the chunk
 /// class, the sky and clip-proxy decisions, the box plan with truncation, and the room flag's hold rule (the
 /// "hysteresis" of S7.N1.flicker: a processing gas system keeps its last answer; S7.M10 reads live and flickers).
 /// </summary>
    [TestClass]
    public unsafe class S7InteriorTests
    {
        private const byte InRoom = S7Conventions.C1InteriorFlags.InRoom, Press = S7Conventions.C1InteriorFlags.Pressurization,
            Proc = S7Conventions.C1InteriorFlags.Processing, Breached = S7Conventions.C1InteriorFlags.Breached,
            CharIn = S7Conventions.C1InteriorFlags.CharacterInRoom;

        // ------------------------------------------------------------------------------------------ C1 codec

        [TestMethod]
        public void C1_RoundTrip_LeavesTheOtherExtensionsAlone()
        {
            var f = new SeFrame();
            var loco = new LocoWireState { State = S7aConventions.C1LocoState.Walk, Flags = 1, VForwardCm = 123 };
            S7aLoco.Write(ref f, ref loco);
            var s = new C1Interior { Flags = (byte)(InRoom | Press | CharIn), RoomO2 = 200, RoomIndex = 7, RoomGridId = 0x0123456789abcdefUL };
            S7InteriorWire.Write(ref f, in s);
            C1Interior r;
            Assert.IsTrue(S7InteriorWire.TryRead(ref f, out r));
            Assert.AreEqual(s.Flags, r.Flags);
            Assert.AreEqual(s.RoomO2, r.RoomO2);
            Assert.AreEqual(s.RoomIndex, r.RoomIndex);
            Assert.AreEqual(s.RoomGridId, r.RoomGridId);
            Assert.IsTrue(r.InRoom && r.CharacterInRoom);
            LocoWireState l2;
            Assert.IsTrue(S7aLoco.TryRead(ref f, out l2), "the loco bytes 0xA0-0xAF are untouched");
            Assert.AreEqual(123, l2.VForwardCm);
            Assert.AreEqual(1, S7InteriorWire.ReadVersion(ref f));
            byte* b = (byte*)&f;
            Assert.AreEqual(0, b[0xB3], "pad 0");
            for (int k = 0xC0; k < 0x100; k++) Assert.AreEqual(0, b[k], "nothing past 0xBF");
            S7InteriorWire.Erase(ref f);
            Assert.IsFalse(S7InteriorWire.TryRead(ref f, out r), "version 0: no extension");
            Assert.IsTrue(S7aLoco.TryRead(ref f, out l2), "erase keeps the loco bytes");
        }

        [TestMethod]
        public void C1_InRoom_FallsBackToFlagBit3WithoutTheExtension()
        {
            var f = new SeFrame();
            Assert.IsFalse(S7InteriorWire.InRoom(ref f));
            f.flags = SeFrameFlags.CameraInAirtightRoom;
            Assert.IsTrue(S7InteriorWire.InRoom(ref f), "an SE without the extension: bit 3 alone");
            var s = C1Interior.None(true);
            S7InteriorWire.Write(ref f, in s);
            Assert.IsFalse(S7InteriorWire.InRoom(ref f), "the extension wins when written");
            Assert.AreEqual(S7InteriorWire.NoRoom, s.RoomIndex);
            Assert.AreEqual(Press, s.Flags);
            Assert.AreEqual(0, C1Interior.None(false).Flags);
        }

        [TestMethod]
        public void RoomO2_RoundsHalfAwayFromZero_Clamped()
        {
            Assert.AreEqual(0, S7InteriorWire.RoomO2Byte(0f));
            Assert.AreEqual(0, S7InteriorWire.RoomO2Byte(-1f));
            Assert.AreEqual(0, S7InteriorWire.RoomO2Byte(float.NaN));
            Assert.AreEqual(255, S7InteriorWire.RoomO2Byte(1f));
            Assert.AreEqual(255, S7InteriorWire.RoomO2Byte(3f));
            Assert.AreEqual(128, S7InteriorWire.RoomO2Byte(0.5f), "127.5 rounds away from zero");
            Assert.AreEqual(1, S7InteriorWire.RoomO2Byte(1f / 255f));
        }

        // ------------------------------------------------------------------------------------------ RoomSet

        [TestMethod]
        public void RoomSet_RoundTrip_Flags_Hash()
        {
            var boxes = new List<CellBox> { new CellBox(-1, 0, -1, 1, 1, 1), CellBox.Cell(5, 5, 5) };
            byte[] p = RoomSetCodec.Encode(42UL, boxes, false);
            Assert.AreEqual(16 + 2 * 24, p.Length);
            RoomSetInfo info;
            var outBoxes = new List<CellBox>();
            bool vok;
            string err;
            Assert.IsTrue(RoomSetCodec.TryDecode(p, out info, outBoxes, out vok, out err), err);
            Assert.IsTrue(vok);
            Assert.AreEqual(42UL, info.EntityId);
            Assert.AreEqual(2u, info.Count);
            Assert.AreEqual(1u, info.Version);
            Assert.IsFalse(info.Truncated);
            CollectionAssert.AreEqual(boxes, outBoxes);
            fixed (byte* b = p) Assert.AreEqual(S7Conventions.RoomSetFlags.Version1, *(uint*)(b + 0x0C), "pad_c = flags");

            byte[] t = RoomSetCodec.Encode(42UL, boxes, true);
            Assert.IsTrue(RoomSetCodec.TryDecode(t, out info, outBoxes, out vok, out err));
            Assert.IsTrue(info.Truncated);
            Assert.AreNotEqual(RoomSetCodec.ContentHash(boxes, false), RoomSetCodec.ContentHash(boxes, true));
            Assert.AreEqual(RoomSetCodec.ContentHash(boxes, false), RoomSetCodec.ContentHash(new List<CellBox>(boxes), false));
            Assert.AreNotEqual(RoomSetCodec.ContentHash(boxes, false), RoomSetCodec.ContentHash(new List<CellBox> { boxes[0] }, false));

            byte[] empty = RoomSetCodec.Encode(9UL, new List<CellBox>(), false);
            Assert.IsTrue(RoomSetCodec.TryDecode(empty, out info, outBoxes, out vok, out err));
            Assert.AreEqual(0, outBoxes.Count, "count 0 = no airtight room");
        }

        [TestMethod]
        public void RoomSet_Malformed_AndOtherVersions()
        {
            var boxes = new List<CellBox> { CellBox.Cell(0, 0, 0) };
            byte[] p = RoomSetCodec.Encode(1UL, boxes, false);
            RoomSetInfo info;
            var outBoxes = new List<CellBox>();
            bool vok;
            string err;
            byte[] shortP = new byte[p.Length - 1];
            Array.Copy(p, shortP, shortP.Length);
            Assert.IsFalse(RoomSetCodec.TryDecode(shortP, out info, outBoxes, out vok, out err), "size mismatch");
            byte[] v0 = (byte[])p.Clone();
            fixed (byte* b = v0) *(uint*)(b + 0x0C) = 0; // an S3/S5 producer's zero pad: version 0
            Assert.IsTrue(RoomSetCodec.TryDecode(v0, out info, outBoxes, out vok, out err));
            Assert.IsFalse(vok, "version 0: ignored by the caller");
            Assert.AreEqual(0, outBoxes.Count);
            byte[] bad = (byte[])p.Clone();
            fixed (byte* b = bad) { *(int*)(b + 16) = 3; *(int*)(b + 16 + 12) = 2; } // min.x 3 > max.x 2
            Assert.IsFalse(RoomSetCodec.TryDecode(bad, out info, outBoxes, out vok, out err), "empty box");
            Assert.AreEqual(0, outBoxes.Count);
            try { RoomSetCodec.Encode(1UL, new List<CellBox> { new CellBox(1, 0, 0, 0, 0, 0) }, false); Assert.Fail("empty box encoded"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void Plan_TruncatesToOneBoxPerRoomAboveMaxBoxes_SortedForTheHash()
        {
            var r1 = new List<CellBox> { CellBox.Cell(2, 0, 0), CellBox.Cell(0, 0, 0) };
            var r2 = new List<CellBox> { CellBox.Cell(9, 9, 9) };
            var outp = new List<CellBox>();
            bool tr;
            InteriorRules.Plan(new[] { r1, r2 }, new[] { new CellBox(0, 0, 0, 2, 0, 0), CellBox.Cell(9, 9, 9) }, outp, out tr);
            Assert.IsFalse(tr);
            Assert.AreEqual(3, outp.Count);
            Assert.AreEqual(CellBox.Cell(0, 0, 0), outp[0], "sorted");
            var big = new List<CellBox>();
            for (int i = 0; i < S7Conventions.RoomLimits.MaxBoxes + 1; i++) big.Add(CellBox.Cell(i * 2, 0, 0));
            InteriorRules.Plan(new[] { big, r2 }, new[] { new CellBox(0, 0, 0, 8192, 0, 0), CellBox.Cell(9, 9, 9) }, outp, out tr);
            Assert.IsTrue(tr, "4,097 boxes: one per room");
            Assert.AreEqual(2, outp.Count);
            CellBox a = InteriorRules.Aabb(new[] { new I32x3(1, 2, 3), new I32x3(-1, 5, 0) });
            Assert.AreEqual(new CellBox(-1, 2, 0, 1, 5, 3), a);
            Assert.IsFalse(InteriorRules.Aabb(new I32x3[0]).IsValid);
        }

        // ------------------------------------------------------------------------------------------ chunk class, sky, clip

        [TestMethod]
        public void Classify_InsideBoundaryExterior()
        {
            // the S7.N1 room: inner 3 x 2 x 3 cells around the origin, walls one cell thick
            var boxes = new List<CellBox> { new CellBox(-1, 0, -1, 1, 1, 1) };
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, InteriorRules.Classify(0, 0, 0, boxes));
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, InteriorRules.Classify(1, 1, 1, boxes));
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, InteriorRules.Classify(2, 0, 0, boxes), "wall");
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, InteriorRules.Classify(0, -1, 0, boxes), "floor");
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, InteriorRules.Classify(2, 2, 2, boxes), "corner (Chebyshev 1)");
            Assert.AreEqual(S7Conventions.InteriorClass.Exterior, InteriorRules.Classify(3, 0, 0, boxes));
            Assert.AreEqual(S7Conventions.InteriorClass.Exterior, InteriorRules.Classify(0, 0, 0, new List<CellBox>()));
            Assert.AreEqual(S7Conventions.InteriorClass.Exterior, InteriorRules.Classify(0, 0, 0, null));
            // a part's translation: large grid 2.5 m, cell round(T/g)
            Assert.AreEqual(S7Conventions.InteriorClass.Inside, InteriorRules.ClassifyLocal(2.5, 2.5, -2.5, 2.5, boxes));
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, InteriorRules.ClassifyLocal(5.0, 0, 0, 2.5, boxes));
            Assert.AreEqual(S7Conventions.InteriorClass.Boundary, InteriorRules.ClassifyLocal(3.76, 0, 0, 2.5, boxes), "1.504 rounds to 2");
            Assert.AreEqual(2, InteriorRules.CellOf(3.75, 2.5), "1.5 rounds away from zero");
            Assert.AreEqual(-2, InteriorRules.CellOf(-3.75, 2.5));
            Assert.AreEqual(0, InteriorRules.CellOf(double.NaN, 2.5));
        }

        [TestMethod]
        public void Sky_InsideAlways_BoundaryWhileInThatRoom_ExteriorNever()
        {
            byte ins = S7Conventions.InteriorClass.Inside, bd = S7Conventions.InteriorClass.Boundary, ex = S7Conventions.InteriorClass.Exterior;
            Assert.IsTrue(InteriorRules.InteriorSky(ins, false, 0, 5, 5));
            Assert.IsFalse(InteriorRules.InteriorSky(ex, true, 5, 5, 5));
            Assert.IsTrue(InteriorRules.InteriorSky(bd, true, 5, 5, 0));
            Assert.IsTrue(InteriorRules.InteriorSky(bd, true, 9, 5, 9), "the camera's room is in the chunk's root");
            Assert.IsFalse(InteriorRules.InteriorSky(bd, true, 7, 5, 9), "another grid's room");
            Assert.IsFalse(InteriorRules.InteriorSky(bd, false, 5, 5, 5), "outside: the biome sky");
            Assert.IsFalse(InteriorRules.InteriorSky(bd, true, 0, 0, 0), "no room id");
            Assert.IsTrue(InteriorRules.StraddlesSurface(-2.0, 3.0));
            Assert.IsFalse(InteriorRules.StraddlesSurface(-5.0, -0.1), "submerged");
            Assert.IsFalse(InteriorRules.StraddlesSurface(0.0, 4.0), "dry");
        }

        // ------------------------------------------------------------------------------------------ hold rule

        private static RoomProbe P(ulong grid, bool processing, bool room, bool airtight, int index = 3, float o2 = 1f)
        {
            return new RoomProbe { GridId = grid, Processing = processing, HasRoom = room, Airtight = airtight, RoomIndex = index, RoomO2 = o2 };
        }

        [TestMethod]
        public void Hold_KeepsTheLastAnswerWhileTheGasSystemProcesses()
        {
            var h = new RoomFlagHold();
            C1Interior r = h.Decide(new[] { P(10, false, true, true) }, 1, true);
            Assert.AreEqual(InRoom | Press, r.Flags);
            Assert.AreEqual(10UL, r.RoomGridId);
            Assert.AreEqual(3u, r.RoomIndex);
            Assert.AreEqual(255, r.RoomO2);
            // the recompute: the live read would say "no room" (a fresh cube); the hold keeps InRoom
            r = h.Decide(new[] { P(10, true, false, false) }, 1, true);
            Assert.AreEqual(InRoom | Press | Proc, r.Flags, "held while processing");
            Assert.AreEqual(1, h.Holds);
            // done: the breach is seen
            r = h.Decide(new[] { P(10, false, true, false) }, 1, true);
            Assert.AreEqual(Press | Breached, r.Flags, "not airtight: breached, no flag");
            Assert.AreEqual(0UL, r.RoomGridId);
            r = h.Decide(new[] { P(10, true, false, false) }, 1, true);
            Assert.AreEqual(Press | Breached | Proc, r.Flags, "the breached answer is held too");
            r = h.Decide(new[] { P(10, false, true, true) }, 1, true);
            Assert.AreEqual(InRoom | Press, r.Flags, "re-sealed");
        }

        [TestMethod]
        public void Hold_FirstAirtightCandidateWins_NoHoldBeforeAnyAnswer_PressurizationOff()
        {
            var h = new RoomFlagHold();
            C1Interior r = h.Decide(new[] { P(1, true, false, false), P(2, false, false, false), P(3, false, true, true, 9) }, 3, true);
            Assert.AreEqual(InRoom | Press | Proc, r.Flags, "grid 1 has nothing held; grid 3 wins");
            Assert.AreEqual(3UL, r.RoomGridId);
            Assert.AreEqual(9u, r.RoomIndex);
            r = h.Decide(new[] { P(3, false, true, true, 9), P(4, false, true, true, 1) }, 2, true);
            Assert.AreEqual(3UL, r.RoomGridId, "nearest first");
            Assert.AreEqual(InRoom | Press, r.Flags);
            r = h.Decide(new[] { P(3, false, true, true) }, 0, true);
            Assert.AreEqual(Press, r.Flags, "no candidate");
            r = h.Decide(new[] { P(3, false, true, true) }, 1, false);
            Assert.AreEqual(0, r.Flags, "pressurization off: all zero");
            Assert.AreEqual(S7InteriorWire.NoRoom, r.RoomIndex);
            r = h.Decide(null, 5, true);
            Assert.AreEqual(Press, r.Flags);
            Assert.AreEqual(2, h.HeldGrids, "grids 2 and 3 answered (grid 4 was never consulted: grid 3 won first)");
            h.Retain(new HashSet<ulong> { 3 });
            Assert.AreEqual(1, h.HeldGrids, "only grid 3 kept");
            h.Clear();
            Assert.AreEqual(0, h.HeldGrids);
        }

        [TestMethod]
        public void Hold_NoFlickerOverARandomRecompute_LiveReadFlickers()
        {
            // S7.N1.flicker / S7.M10: over a long run with recomputes, the held flag changes only at real edges; a reader
 // that takes "no room" while processing (GetRooms returns false then) changes during every recompute.
            var rnd = new Random(7);
            var h = new RoomFlagHold();
            bool truth = true, last = true, liveLast = true;
            int heldChanges = 0, liveChanges = 0, truthChanges = 0;
            for (int t = 0; t < 5000; t++)
            {
                if (rnd.Next(400) == 0) { truth = !truth; truthChanges++; }
                bool processing = rnd.Next(10) == 0;
                C1Interior r = h.Decide(new[] { P(1, processing, true, truth) }, 1, true);
                bool held = r.InRoom;
                bool live = !processing && truth;
                if (held != last) heldChanges++;
                if (live != liveLast) liveChanges++;
                last = held; liveLast = live;
            }
            Assert.IsTrue(heldChanges <= truthChanges, "held: " + heldChanges + " changes for " + truthChanges + " edges");
            Assert.IsTrue(liveChanges > 10 * Math.Max(1, truthChanges), "the live read flickers: " + liveChanges);
        }
    }
}
