using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a Stage D: the live edit's pure rules (src/DeepEngineers.Wire/S8aLivePatch.cs). The round trip: a batch held in memory
 /// (the loader's bytes per octree), patched with the changed octrees of a new version, equals the new version loaded from its
 /// file (serialized and parsed back), byte for byte, at minLod 0 (the full-detail streamer) and 4 (the low-detail one).
 /// </summary>
    [TestClass]
    public class S8aLivePatchTests
    {
        private static byte[] Raster(int seed, int holeX, int holeY, int holeZ, int r, out byte[] dens)
        {
            var rng = new Random(seed);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            for (int z = 0; z < 32; z++)
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        int i = S8aOctree.Index(x, y, z);
                        bool solid = y < 14 + (x + z) % 3;
                        if (r > 0 && (x - holeX) * (x - holeX) + (y - holeY) * (y - holeY) + (z - holeZ) * (z - holeZ) <= r * r) solid = false;
                        t[i] = solid ? (byte)(1 + rng.Next(3)) : (byte)0;
                        d[i] = solid && y >= 12 ? (byte)(140 + rng.Next(60)) : (byte)0;
                    }
            dens = d;
            return t;
        }

        private static S8aBatchFile Batch(int bx, int by, int bz, int holeR)
        {
            S8aBatchFile f = S8aBatchFile.CreateEmpty(bx, by, bz);
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!f.Has(i)) continue;
                byte[] d;
                byte[] t = Raster(1000 + i, 16, 13, 16, i == 62 || i == 63 ? holeR : 0, out d);
                f.SetOctree(i, S8aOctree.EncodeOctree(t, d));
            }
            return S8aBatchFile.Parse(f.Serialize(), bx, by, bz);    // as the files are read
        }

        [TestMethod]
        public void RoundTrip_PatchedMemoryEqualsTheReloadedFile()
        {
            foreach (int[] id in new[] { new[] { 30, 18, 4 }, new[] { 12, 18, 12 } })      // a window id and a crater id
            {
                S8aBatchFile before = Batch(id[0], id[1], id[2], 0);
                S8aBatchFile after = Batch(id[0], id[1], id[2], 5);
                List<int> changed = S8aLivePatch.ChangedOctrees(before, after);
                CollectionAssert.AreEquivalent(new[] { 62, 63 }, changed.ToArray(), "only the drilled octrees changed");
                foreach (int minLod in new[] { 0, 4 })
                {
                    Dictionary<int, byte[]> memory = S8aLivePatch.Load(before, minLod);
                    int n = S8aLivePatch.Apply(memory, before, after, minLod);
                    Assert.AreEqual(2, n);
                    Dictionary<int, byte[]> reloaded = S8aLivePatch.Load(S8aBatchFile.Parse(after.Serialize(), id[0], id[1], id[2]), minLod);
                    Assert.IsTrue(S8aLivePatch.SameModel(memory, reloaded), "byte-identical nodes after the live edit, minLod " + minLod);
                    Assert.IsFalse(S8aLivePatch.SameModel(S8aLivePatch.Load(before, minLod), reloaded) && minLod == 0, "the edit changed something");
                }
            }
        }

        [TestMethod]
        public void ReadRecordAndUnloadChildren_AsTheLoader()
        {
            S8aBatchFile f = Batch(30, 18, 4, 4);
            byte[] rec = S8aLivePatch.ReadRecord(f, 62);
            Assert.AreEqual(f.NodeCount(62), rec[0] | (rec[1] << 8), "u16 node count first");
            Assert.AreEqual(2 + 4 * f.NodeCount(62), rec.Length);
            byte[] full = S8aLivePatch.LoadedBytes(f, 62, 0);
            Assert.AreEqual(4 * f.NodeCount(62), full.Length, "minLod 0 keeps every node");
            byte[] low = S8aLivePatch.LoadedBytes(f, 62, 4);
            Assert.AreEqual(36, low.Length, "minLod >= 1: root + 8 children");
            for (int c = 1; c <= 8; c++) Assert.IsTrue(low[c * 4 + 2] == 0 && low[c * 4 + 3] == 0, "child " + c + " is a leaf");
            for (int k = 0; k < 4; k++) Assert.AreEqual(full[k], low[k], "the root as stored");
            byte[] leaf = S8aLivePatch.LoadedBytes(new byte[] { 3, 0, 0, 0 }, 0, 1, 4);
            CollectionAssert.AreEqual(new byte[] { 3, 0, 0, 0 }, leaf, "a leaf root stays one node");
            Assert.AreEqual(0, S8aLivePatch.LoadedBytes(new byte[0], 0, 0, 0).Length, "no nodes: nothing held");
        }

        [TestMethod]
        public void LevelsAndCellStates()
        {
            Assert.IsTrue(S8aLivePatch.HigherLevelsDue(5000, 0));
            Assert.IsFalse(S8aLivePatch.HigherLevelsDue(5000, 4500));
            Assert.IsTrue(S8aLivePatch.HigherLevelsDue(5500, 4500));
            for (int s = 0; s <= 12; s++)
            {
                Assert.AreEqual(s >= 6 && s <= 8, S8aLivePatch.Covered(s), "covered " + s);
                Assert.AreEqual(s >= 5 && s <= 8, S8aLivePatch.Rebuildable(s), "rebuildable " + s);
                Assert.AreEqual(s == 3 || s == 4, S8aLivePatch.Building(s), "building " + s);
            }
        }
    }
}
