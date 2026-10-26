using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// a test run (n10 20261023-213828): far voxels next to the shell take the shell's material, on either side of a cavity. a test run (n10
 /// 20261024-003902): every layer, so a collapsed far-solid block behind a wall (child 0 four steps from the cut) carries the wall's.
 /// </summary>
    [TestClass]
    public class S8aShellFillTests
    {
        private static byte[] Run(int cavityX, int shellX, int island, out long near, out long far)
        {
            // an 8 x 3 x 3 box along x: empty at cavityX, the shell voxel (SE: material 7, cobalt) beside it, solid beyond (outside the
 // shell); island >= 0: the plane x = island empty as well, so the solid beyond it joins no shell voxel
            int sx = 8, sy = 3, sz = 3, n = sx * sy * sz;
            var content = new byte[n];
            var shell = new byte[n];
            var m = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int x = i % sx;
                content[i] = x == cavityX || x == island ? (byte)0 : (byte)255;
                if (x == shellX) { shell[i] = 1; m[i] = 7; }
            }
            var material = new byte[n];
            S8aShellFill.Fill(content, shell, m, material, sx, sy, sz, 1, new byte[n], new byte[n], out near, out far);
            var row = new byte[sx];
            for (int x = 0; x < sx; x++) row[x] = material[x + sx * (1 + sy * 1)];
            return row;
        }

        [TestMethod]
        public void FarVoxelsBehindTheShell_TakeItsMaterial_EitherSide_EveryLayer()
        {
            long near, far;
            byte[] plus = Run(0, 1, -1, out near, out far);      // cavity at x 0, shell at 1: every solid voxel joined to it takes 7
            CollectionAssert.AreEqual(new byte[] { 255, 7, 7, 7, 7, 7, 7, 7 }, plus);
            Assert.AreEqual(0L, far);
            byte[] minus = Run(7, 6, -1, out near, out far);     // the mirror: cavity at x 7
            CollectionAssert.AreEqual(new byte[] { 7, 7, 7, 7, 7, 7, 7, 255 }, minus);
            Assert.IsTrue(near > 0);
        }

        [TestMethod]
        public void SolidJoinedToNoShell_TakesTheBoxMostCommon()
        {
            long near, far;
            byte[] row = Run(0, 1, 4, out near, out far);        // x 4 empty too: x 5-7 join no shell voxel
            CollectionAssert.AreEqual(new byte[] { 255, 7, 7, 7, 255, 1, 1, 1 }, row);
            Assert.AreEqual(3L * 9, far);
        }

        [TestMethod]
        public void LayersOneAndTwo_AsTheTwoPasses_FirstNeighbourInOrder()
        {
            // a 3 x 3 x 1 box: shell voxels at (0, 0) material 5 and (2, 0) material 6; (1, 0) is next to both: x- first wins (5);
 // (1, 1) next to (1, 0) only after layer 1: takes layer 1's (5); (0, 1) next to (0, 0): 5; (2, 1): 6
            int sx = 3, sy = 3, sz = 1, n = 9;
            var content = new byte[n];
            var shell = new byte[n];
            var m = new byte[n];
            for (int i = 0; i < n; i++) content[i] = 255;
            shell[0] = 1; m[0] = 5;
            shell[2] = 1; m[2] = 6;
            var material = new byte[n];
            long near, far;
            S8aShellFill.Fill(content, shell, m, material, sx, sy, sz, 9, new byte[n], new byte[n], out near, out far);
            CollectionAssert.AreEqual(new byte[] { 5, 5, 6, 5, 5, 6, 5, 5, 6 }, material);
            Assert.AreEqual(7L, near);
            Assert.AreEqual(0L, far);
        }

        [TestMethod]
        public void CollapsedBlockBehindAWall_ChildZeroCarriesTheWallMaterial()
        {
            // a test run's cavity: a wall plane of shell voxels at x 0 (vein, 7) with the seabed's shell (sand, 1) far above; the 4 x 4 x 4
 // block at x 1-4, y 0-3, z 0-3 is far solid; its child 0 (1, 0, 0) is joined to the wall: 7, never the box's most common
            int sx = 6, sy = 12, sz = 4, n = sx * sy * sz;
            var content = new byte[n];
            var shell = new byte[n];
            var m = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int x = i % sx, y = (i / sx) % sy;
                content[i] = y == sy - 1 ? (byte)0 : (byte)255;          // water above the seabed row
                if (x == 0 && y < 6) { shell[i] = 1; m[i] = 7; }         // the cavity wall (the cut beside it is outside the box)
                if (y == sy - 2) { shell[i] = 1; m[i] = 1; }             // the seabed's shell: sand, the box's most common
            }
            var material = new byte[n];
            long near, far;
            S8aShellFill.Fill(content, shell, m, material, sx, sy, sz, 1, new byte[n], new byte[n], out near, out far);
            Assert.AreEqual((byte)7, material[1 + sx * (0 + sy * 0)]);
            Assert.AreEqual((byte)7, material[4 + sx * (3 + sy * 3)]);
            Assert.AreEqual(0L, far);
        }
    }
}
