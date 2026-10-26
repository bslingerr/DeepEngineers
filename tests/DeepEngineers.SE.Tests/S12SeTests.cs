using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S12, SE side, the game-free part (src/DeepEngineers.SE/S12/S12SeLogic.cs): the seeded failures routed by BuildFlavor's
 /// registry (S12 hook), the packed cells of the room snapshots, the snapshot's cell map as the air rule's processing
 /// answer, and the C6 block Link.WriteSeTime writes (S12 hook): rows while active, version 0 (absent) when not.
 /// </summary>
    [TestClass]
    public class S12SeTests
    {
        [TestCleanup]
        public void Clean()
        {
            S12SeMutants.ClearAll();
            S12SeWire.Deactivate();
        }

        [TestMethod]
        public void Mutants_SetActiveClear_AndRegistry()
        {
            Assert.IsNull(S12SeMutants.Set("S16.M1", true), "not an S12 id");
            StringAssert.StartsWith(S12SeMutants.Set("S12.M6", true), "REFUSE", "S12.M6 is Subnautica's");
            StringAssert.StartsWith(S12SeMutants.Set("S12.M7", true), "REFUSE", "S12.M7 is Subnautica's");
            StringAssert.StartsWith(S12SeMutants.Set("s12.m1", true), "S12.M1 ON");
            Assert.IsTrue(S12SeMutants.CameraBox);
            Assert.AreEqual("S12.M1", S12SeMutants.Active);
            Assert.IsTrue(Mutants.Active.Contains("S12.M1"), "BuildFlavor's registry lists the S12 mutants (S12 hook)");
            StringAssert.StartsWith(Mutants.Set("S12.M4", true), "S12.M4 ON", "BuildFlavor routes the S12 ids (S12 hook)");
            StringAssert.StartsWith(Mutants.Set("S12.M2", true), "S12.M2 ON");
            StringAssert.StartsWith(Mutants.Set("S12.M3", true), "S12.M3 ON");
            StringAssert.StartsWith(Mutants.Set("S12.M5", true), "S12.M5 ON");
            StringAssert.StartsWith(Mutants.Set("S12.M8", true), "S12.M8 ON", "the air pocket (the owner's open house)");
            Assert.IsTrue(S12SeMutants.AirPocket);
            StringAssert.StartsWith(Mutants.Set("S12.M9", true), "S12.M9 ON", "the mid-plane (the owner's window)");
            StringAssert.StartsWith(Mutants.Set("S12.M10", true), "S12.M10 ON", "the box plane (the owner's attached blocks)");
            StringAssert.StartsWith(Mutants.Set("S12.M11", true), "S12.M11 ON", "no plane surface (the owner stuck in the flood)");
            StringAssert.StartsWith(Mutants.Set("S12.M12", true), "S12.M12 ON", "round 2's door flip (the owner's airlock)");
            Assert.IsTrue(S12SeMutants.SealMidPlane && S12SeMutants.BoxPlane && S12SeMutants.NoSurface && S12SeMutants.DoorFlip);
            Assert.AreEqual("S12.M1,S12.M2,S12.M3,S12.M4,S12.M5,S12.M8,S12.M9,S12.M10,S12.M11,S12.M12", S12SeMutants.Active);
            Mutants.ClearAll();
            Assert.AreEqual("", S12SeMutants.Active, "BuildFlavor's ClearAll clears them (S12 hook)");
        }

        [TestMethod]
        public void Water_SkinCellsLeftOut_NeverOutside()
        {
            // the test house: a 3^3 interior (cells 1..3) and a window cell (4, 1, -2) in the east wall that SE counts as room
            var snap = new S12Snapshot();
            var sr = new S12SnapRoom { Index = 1 };
            for (int x = 1; x <= 3; x++) for (int y = 1; y <= 3; y++) for (int z = -3; z <= -1; z++) snap.Cells[S12Cell.Pack(x, y, z)] = 1;
            snap.Cells[S12Cell.Pack(4, 1, -2)] = 1;
            snap.Rooms.Add(sr);
            sr.Aabb = new CellBox(1, 1, -3, 4, 3, -1);
            // blocks: the shell (x 0..4, y 0..4, z -4..0 outside the interior), the window, and an interior seat at (2, 1, -2)
            Func<int, int, int, bool> block = (x, y, z) =>
            {
                bool inShell = x >= 0 && x <= 4 && y >= 0 && y <= 4 && z >= -4 && z <= 0;
                bool interior = x >= 1 && x <= 3 && y >= 1 && y <= 3 && z >= -3 && z <= -1;
                return (inShell && !interior) || (x == 2 && y == 1 && z == -2);
            };
            HashSet<long> water = S12Water.Cells(snap, sr, block);
            Assert.AreEqual(27, water.Count, "the window's cell is skin (its outer neighbour is empty); the seat's cell stays water");
            Assert.IsFalse(water.Contains(S12Cell.Pack(4, 1, -2)));
            Assert.IsTrue(water.Contains(S12Cell.Pack(2, 1, -2)));
            List<CellBox> boxes = S12Water.Boxes(water);
            Assert.AreEqual(1, boxes.Count, "the 3^3 interior is one box");
            Assert.AreEqual(0L, S12Water.Outside(boxes, water), "format flood.plane: nothing outside the water cells");
            Assert.AreEqual(9L, S12Water.Outside(new List<CellBox> { sr.Aabb }, water), "S12.M10's bounding box reaches the wall layer");
        }

        [TestMethod]
        public void Cells_PackUnpack()
        {
            int[][] cases = { new[] { 0, 0, 0 }, new[] { -1, 2, -3 }, new[] { 1048575, -1048576, 7 }, new[] { -500, 500, 0 } };
            var seen = new HashSet<long>();
            foreach (int[] c in cases)
            {
                long k = S12Cell.Pack(c[0], c[1], c[2]);
                int x, y, z;
                S12Cell.Unpack(k, out x, out y, out z);
                Assert.AreEqual(c[0], x); Assert.AreEqual(c[1], y); Assert.AreEqual(c[2], z);
                Assert.IsTrue(seen.Add(k), "distinct keys");
            }
        }

        [TestMethod]
        public void Snapshot_IsTheProcessingAnswer()
        {
            var s = new S12Snapshot();
            var room = new S12SnapRoom { Index = 4, Cells = 27, OxygenAmount = 210.9375f, BlockCount = 27 };
            for (int x = 1; x <= 3; x++) for (int y = 1; y <= 3; y++) for (int z = 1; z <= 3; z++) s.Cells[S12Cell.Pack(x, y, z)] = 4;
            s.Rooms.Add(room);
            Assert.AreEqual(4, s.RoomAt(2, 2, 2));
            Assert.AreEqual(0, s.RoomAt(4, 2, 2), "the wall cell has no room");
            Assert.AreEqual(0.5f, room.Oxygen(2.5f), 1e-6f, "MyOxygenRoom.OxygenLevel's rule: amount / (BlockCount g^3)");
            Assert.AreSame(room, s.RoomByIndex(4));
            // the air rule on the snapshot: inside 0.25 m from the east face (g 2.5), outside by 0.25 m
            S12Depth inside = S12AirVolume.Depth(3 * 2.5 + 1.0, 5, 5, 2.5, s.RoomAt);
            S12Depth outside = S12AirVolume.Depth(3 * 2.5 + 1.5, 5, 5, 2.5, s.RoomAt);
            Assert.AreEqual(0.25, inside.DepthM, 1e-12);
            Assert.AreEqual(-0.25, outside.DepthM, 1e-12);
            Assert.AreEqual(4, inside.Room);
        }

        [TestMethod]
        public unsafe void C6Block_WrittenWhileActive_AbsentWhenNot()
        {
            byte[] region = new byte[0x1000];
            var rows = new List<S12Row>
            {
                new S12Row { GridId = 77, TrackId = 3, Kind = (byte)S12Conventions.RowKind.Flood, State = (byte)S12Conventions.FloodState.Rising, Level = 0.5f, StartSeq = 2 },
                new S12Row { GridId = 77, TrackId = 9, Kind = (byte)S12Conventions.RowKind.Vent, State = (byte)S12Conventions.VentState.Pressurizing, StartSeq = 5 },
            };
            S12SeWire.Publish(6, 1234, (byte)S12Conventions.C6Flags.Active, rows);
            fixed (byte* p = region) S12SeWire.WriteC6(p, 1234);
            var h = new S12Header();
            var back = new S12Row[S12Conventions.C6.MaxRows];
            int n;
            uint seq;
            fixed (byte* p = region) Assert.AreEqual(SeqlockReadStatus.Fresh, S12Wire.TryRead(p, h, back, out n, out seq));
            Assert.AreEqual(2, n);
            Assert.AreEqual(1, h.Version);
            Assert.AreEqual(6u, h.WorldEpoch);
            Assert.AreEqual(2u, h.FloodSeq, "the header sums the flood starts");
            Assert.AreEqual(5u, h.VentSeq, "the header sums the vent starts");
            Assert.AreEqual(0.5f, back[0].Level);
            for (int i = 0; i < S12Conventions.C6.Offset; i++) Assert.AreEqual(0, region[i], "nothing written before +0x800 (S15's blocks untouched)");
            S12SeWire.Deactivate();
            fixed (byte* p = region) S12SeWire.WriteC6(p, 1235);
            fixed (byte* p = region) Assert.AreEqual(SeqlockReadStatus.Fresh, S12Wire.TryRead(p, h, back, out n, out seq));
            Assert.AreEqual(0, h.Version, "inactive: version 0 (absent)");
            Assert.AreEqual(0, n);
        }
    }
}
