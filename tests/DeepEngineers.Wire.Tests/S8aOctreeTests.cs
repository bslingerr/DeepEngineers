using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// The synthetic 32³ rasters both twins generate from a seed (tools/sn-octrees/sn_octree_write.py
 /// <c>synthetic_raster</c>; the C# copy here, no code shared): splitmix64 draws, kinds random, blocky4, blocky8,
 /// terrain, uniform. Raster index x + 32·y + 1024·z.
 /// </summary>
    internal static class S8aSyntheticRaster
    {
        private static readonly byte[] Types = { 0, 1, 84, 150 };

        public sealed class SplitMix64
        {
            private ulong _s;
            public SplitMix64(ulong seed) { _s = seed; }

            public ulong Next()
            {
                _s += 0x9E3779B97F4A7C15UL;
                ulong z = _s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static void Rule(ulong r, out byte t, out byte d)
        {
            t = Types[(int)(r & 3)];
            ulong m = (r >> 2) & 3;
            if (m == 1) d = (byte)(126 + (int)((r >> 8) % 126UL));
            else if (m == 2) d = (byte)(1 + (int)((r >> 8) % 125UL));
            else d = 0;
        }

        public static void Make(string kind, ulong seed, byte[] type, byte[] density)
        {
            Array.Clear(type, 0, S8aOctree.Voxels);
            Array.Clear(density, 0, S8aOctree.Voxels);
            var rng = new SplitMix64(seed);
            byte t, d;
            switch (kind)
            {
                case "random":
                    for (int z = 0; z < 32; z++)
                        for (int y = 0; y < 32; y++)
                            for (int x = 0; x < 32; x++)
                            {
                                Rule(rng.Next(), out t, out d);
                                int i = S8aOctree.Index(x, y, z);
                                type[i] = t;
                                density[i] = d;
                            }
                    break;
                case "blocky4":
                case "blocky8":
                {
                    int k = kind == "blocky4" ? 4 : 8;
                    for (int bz = 0; bz < 32 / k; bz++)
                        for (int by = 0; by < 32 / k; by++)
                            for (int bx = 0; bx < 32 / k; bx++)
                            {
                                Rule(rng.Next(), out t, out d);
                                for (int z = bz * k; z < (bz + 1) * k; z++)
                                    for (int y = by * k; y < (by + 1) * k; y++)
                                        for (int x = bx * k; x < (bx + 1) * k; x++)
                                        {
                                            int i = S8aOctree.Index(x, y, z);
                                            type[i] = t;
                                            density[i] = d;
                                        }
                            }
                    break;
                }
                case "terrain":
                {
                    long sd = (long)(seed % (1UL << 31));
                    for (int z = 0; z < 32; z++)
                        for (int x = 0; x < 32; x++)
                        {
                            long h = 8 + (x * 7 + z * 13 + sd) % 11;
                            for (int y = 0; y < 32; y++)
                            {
                                long dist = h - y;
                                int i = S8aOctree.Index(x, y, z);
                                if (dist >= 2) { type[i] = (byte)(y < 6 ? 84 : 150); density[i] = 0; }
                                else if (dist <= -2) { type[i] = 0; density[i] = 0; }
                                else
                                {
                                    long dd = Math.Max(1, Math.Min(251, 126 + 50 * dist));
                                    type[i] = (byte)(dd >= 126 ? 150 : 0);
                                    density[i] = (byte)dd;
                                }
                            }
                        }
                    break;
                }
                case "uniform":
                    Rule(rng.Next(), out t, out d);
                    for (int i = 0; i < S8aOctree.Voxels; i++) { type[i] = t; density[i] = d; }
                    break;
                default:
                    throw new ArgumentException(kind);
            }
        }
    }

    /// <summary>
 /// S8a converter core, part 1 (CONTRACT §3.3 "Conversion", §5.4 formats octree.write / octree.batch; ROADMAP S8a.C1
 /// offline half): the C# octree writer against the Python twin's vectors (tests/s8a/vectors/octree.tsv: node count,
 /// bytes and FNV-1a 64 of the encoded synthetic rasters), the decode/encode idempotence, the writer-rule census, the
 /// S8a.M13 mutant (interior nodes = child 0: bytes change, census red), the hand cases of CacheDownsampledType and the
 /// ComputeMostCommonChildType tie rule, and the batch file layout (crater edge ids store 45 octrees, window ids 125).
 /// </summary>
    [TestClass]
    public class S8aOctreeTests
    {
        private static S8aRuleStats Census(byte[] nodes)
        {
            var st = new S8aRuleStats();
            S8aOctree.CheckRules(nodes, 0, nodes.Length / 4, st);
            return st;
        }

        [TestCleanup]
        public void Cleanup() { S8aWireMutants.ClearAll(); }

        [TestMethod]
        public void Vectors_NodesBytesAndFnv_MatchThePythonWriter()
        {
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            int n = 0;
            foreach (string[] f in S8aVectors.Lines("octree.tsv"))
            {
                S8aSyntheticRaster.Make(f[0], ulong.Parse(f[1]), t, d);
                byte[] nodes = S8aOctree.EncodeOctree(t, d);
                Assert.AreEqual(S8aVectors.I(f[2]), nodes.Length / 4, f[0] + "/" + f[1] + " nodes");
                Assert.AreEqual(S8aVectors.I(f[3]), nodes.Length, f[0] + "/" + f[1] + " bytes");
                Assert.AreEqual(S8aVectors.X16(f[4]), LayoutHash.Fnv1a64(nodes), f[0] + "/" + f[1] + " fnv");
                // decode → encode again: identical; census clean
                var t2 = new byte[S8aOctree.Voxels];
                var d2 = new byte[S8aOctree.Voxels];
                S8aOctree.DecodeOctree(nodes, 0, nodes.Length / 4, t2, d2);
                CollectionAssert.AreEqual(nodes, S8aOctree.EncodeOctree(t2, d2), f[0] + "/" + f[1] + " idempotent");
                S8aRuleStats st = Census(nodes);
                Assert.IsTrue(st.Clean, f[0] + "/" + f[1] + " census: " + st);
                if (f[0] == "random") Assert.IsTrue(nodes.Length / 4 > 30000, "random raster nearly fully subdivided");
                n++;
            }
            Assert.AreEqual(12, n);
        }

        [TestMethod]
        public void Decode_DoesNotRoundTripTheRaster_ButTheEncodingOfTheDecodeDoes()
        {
            // a collapsed leaf loses the far children's density values on purpose (the engine's rule): the raster is not the
 // same, the node bytes are (the install round trip, S8a.C1, is on the install's files: S8aRoundTripTests)
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            S8aSyntheticRaster.Make("blocky8", 3, t, d);
            byte[] nodes = S8aOctree.EncodeOctree(t, d);
            Assert.AreEqual(73, nodes.Length / 4);
            var t2 = new byte[S8aOctree.Voxels];
            var d2 = new byte[S8aOctree.Voxels];
            S8aOctree.DecodeOctree(nodes, 0, nodes.Length / 4, t2, d2);
            CollectionAssert.AreEqual(nodes, S8aOctree.EncodeOctree(t2, d2));
        }

        [TestMethod]
        public void Uniform_IsOneNode_AndOneDifferentVoxelIs41Nodes()
        {
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, S8aOctree.EncodeOctree(t, d), "empty = one far-empty node");
            for (int i = 0; i < t.Length; i++) t[i] = 84;
            CollectionAssert.AreEqual(new byte[] { 84, 0, 0, 0 }, S8aOctree.EncodeOctree(t, d), "solid = one far-solid node");
            t[S8aOctree.Index(0, 0, 0)] = 0;
            byte[] nodes = S8aOctree.EncodeOctree(t, d);
            Assert.AreEqual(41, nodes.Length / 4, "one empty voxel in rock: 5 interior levels");
            S8aRuleStats st = Census(nodes);
            Assert.IsTrue(st.Clean && st.Interior == 5, st.ToString());
            // layout: pre-order, 8-node blocks from 1, child 0 first: node 0's children at 1, node 1's at 9,...
            Assert.AreEqual(1, nodes[2] | (nodes[3] << 8));
            Assert.AreEqual(9, nodes[4 * 1 + 2] | (nodes[4 * 1 + 3] << 8));
            Assert.AreEqual(17, nodes[4 * 9 + 2] | (nodes[4 * 9 + 3] << 8));
            Assert.AreEqual(0, nodes[4 * 2 + 2] | (nodes[4 * 2 + 3] << 8), "child 1 of the root is a leaf");
        }

        [TestMethod]
        public void EncoderBuffer_AllocationFreeOverload_SameBytes()
        {
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            S8aSyntheticRaster.Make("terrain", 17, t, d);
            var enc = new S8aOctreeEncoder();
            var buf = new byte[S8aOctree.MaxBytes];
            int n = enc.Encode(t, d, buf, 0);
            byte[] a = S8aOctree.EncodeOctree(t, d);
            Assert.AreEqual(a.Length / 4, n);
            Assert.AreEqual(n, enc.LastNodes);
            Assert.AreEqual((n - 1) / 8, enc.LastInterior);
            for (int i = 0; i < a.Length; i++) Assert.AreEqual(a[i], buf[i]);
            var small = new byte[8];
            try { enc.Encode(t, d, small, 0); Assert.Fail("dest too small accepted"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void M13_ChangesInteriorNodes_AndTheCensusGoesRed()
        {
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            S8aSyntheticRaster.Make("terrain", 17, t, d);
            byte[] good = S8aOctree.EncodeOctree(t, d);
            Assert.IsTrue(S8aWireMutants.Set("S8a.M13", true));
            Assert.AreEqual("S8a.M13", S8aWireMutants.Active);
            byte[] bad = S8aOctree.EncodeOctree(t, d);
            S8aWireMutants.ClearAll();
            Assert.AreEqual(good.Length, bad.Length, "the layout is the same; the values differ");
            CollectionAssert.AreNotEqual(good, bad);
            S8aRuleStats st = Census(bad);
            Assert.IsTrue(st.DownsampleBad > 0 && !st.Clean, st.ToString());
            Assert.IsTrue(Census(good).Clean);
            Assert.IsFalse(S8aWireMutants.Set("S8a.M99", true));
        }

        [TestMethod]
        public void HandCases_CanCollapse_Downsample_MostCommonType()
        {
            var ct = new byte[] { 5, 7, 7, 5, 0, 0, 0, 0 };
            var cd = new byte[] { 200, 200, 200, 200, 0, 0, 0, 0 };
            Assert.AreEqual(7, S8aOctreeEncoder.MostCommonChildType(ct, cd), "type 5's count sits at child 0 (never compared): 7 wins with 2");
            Assert.AreEqual(3, S8aOctreeEncoder.MostCommonChildType(new byte[] { 9, 3, 0, 0, 0, 0, 0, 0 }, new byte[] { 130, 130, 0, 0, 0, 0, 0, 0 }), "child 1's type (count 1 > 0) wins over child 0's");
            Assert.AreEqual(9, S8aOctreeEncoder.MostCommonChildType(new byte[] { 9, 3, 4, 0, 0, 0, 0, 0 }, new byte[8]), "no near-and-below child: counts 0, a larger type replaces on ties");
            byte ty, de;
            S8aOctreeEncoder.Downsample(new byte[] { 1, 2, 0, 0, 0, 0, 0, 0 }, new byte[] { 10, 21, 0, 0, 0, 0, 0, 0 }, out ty, out de);
            Assert.AreEqual(15, de, "integer mean of the near children");
            Assert.AreEqual(2, ty, "no near-and-below child: child 1's larger type replaces child 0's on the 0 = 0 tie");
            S8aOctreeEncoder.Downsample(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }, new byte[8], out ty, out de);
            Assert.AreEqual(0, de);
            Assert.AreEqual(7, ty, "no near child: density 0, child 0's type");
            var leaves = new[] { true, true, true, true, true, true, true, true };
            int child;
            Assert.IsTrue(S8aOctreeEncoder.CanCollapse(new byte[] { 84, 84, 84, 84, 84, 84, 84, 84 }, new byte[8], leaves, out child), "all far solid");
            Assert.AreEqual(0, child);
            Assert.IsFalse(S8aOctreeEncoder.CanCollapse(new byte[] { 84, 0, 84, 84, 84, 84, 84, 84 }, new byte[8], leaves, out child), "mixed below/above");
            // far (0, 0) is above the surface and near (5, 130) is below: mixed, never collapsed
            Assert.IsFalse(S8aOctreeEncoder.CanCollapse(new byte[] { 0, 5, 5, 0, 0, 0, 0, 0 }, new byte[] { 0, 130, 130, 0, 0, 0, 0, 0 }, leaves, out child));
            Assert.IsTrue(S8aOctreeEncoder.CanCollapse(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 100, 100, 0, 0, 0, 0, 0 }, leaves, out child), "near empties equal, far empties: collapse to the first near child");
            Assert.AreEqual(1, child);
            Assert.IsFalse(S8aOctreeEncoder.CanCollapse(new byte[8], new byte[] { 0, 100, 101, 0, 0, 0, 0, 0 }, leaves, out child), "near children differ");
            leaves[3] = false;
            Assert.IsFalse(S8aOctreeEncoder.CanCollapse(new byte[8], new byte[8], leaves, out child), "an interior child");
        }

        [TestMethod]
        public void BatchFile_CraterEdgeStores45_WindowIdStores125_ParseSerializeRoundTrip()
        {
            Assert.IsTrue(S8aBatchFile.IsCraterId(25, 18, 25));
            Assert.IsFalse(S8aBatchFile.IsCraterId(26, 18, 12));
            Assert.IsFalse(S8aBatchFile.IsCraterId(-3, 18, 4));
            Assert.AreEqual(45, S8aBatchFile.StoredCount(25, 18, 25), "octrees 125-127 lie outside 128 x 100 x 128");
            Assert.AreEqual(75, S8aBatchFile.StoredCount(25, 18, 12));
            Assert.AreEqual(125, S8aBatchFile.StoredCount(12, 18, 12));
            Assert.AreEqual(125, S8aBatchFile.StoredCount(-3, 18, 4), "a window id stores all 125");
            Assert.AreEqual(125, S8aBatchFile.StoredCount(0, 19, 0), "by 19 holds octrees 95-99 inside OctreesY 100");
            S8aBatchFile f = S8aBatchFile.CreateEmpty(25, 18, 25);
            Assert.AreEqual(4 + 45 * 6, f.SerializedLength);
            Assert.IsTrue(f.AllEmpty && !f.AllSolid);
            byte[] bytes = f.Serialize();
            S8aBatchFile g = S8aBatchFile.Parse(bytes, 25, 18, 25);
            Assert.AreEqual(4, g.Version);
            CollectionAssert.AreEqual(bytes, g.Serialize());
            Assert.AreEqual(LayoutHash.Fnv1a64(bytes), g.Fnv());
            // a window batch with a subdivided octree in the middle, parsed and re-serialised
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            S8aSyntheticRaster.Make("terrain", 2025, t, d);
            S8aBatchFile w = S8aBatchFile.CreateUniform(-3, 18, 4, 84, 0);
            Assert.IsTrue(w.AllSolid);
            int mid = S8aBatchFile.OctreeIndex(2, 2, 2);
            w.SetOctree(mid, S8aOctree.EncodeOctree(t, d));
            Assert.IsFalse(w.AllSolid);
            byte[] wb = w.Serialize();
            Assert.AreEqual(4 + 124 * 6 + 2 + 11729 * 4, wb.Length);
            S8aBatchFile w2 = S8aBatchFile.Parse(wb, -3, 18, 4);
            Assert.AreEqual(11729, w2.NodeCount(mid));
            var t2 = new byte[S8aOctree.Voxels];
            var d2 = new byte[S8aOctree.Voxels];
            w2.DecodeOctree(mid, t2, d2);
            CollectionAssert.AreEqual(w.Octree(mid), S8aOctree.EncodeOctree(t2, d2));
            Assert.AreEqual(w.Fnv(), w2.Fnv());
            int ix, iy, iz;
            S8aBatchFile.OctreeCoords(mid, out ix, out iy, out iz);
            Assert.AreEqual(2, ix); Assert.AreEqual(2, iy); Assert.AreEqual(2, iz);
            Assert.AreEqual(25, S8aBatchFile.OctreeIndex(1, 0, 0), "x-outer");
            Assert.AreEqual(5, S8aBatchFile.OctreeIndex(0, 1, 0), "y");
            Assert.AreEqual(1, S8aBatchFile.OctreeIndex(0, 0, 1), "z-inner");
            // bad files
            S8aBatchFile bad;
            string err;
            Assert.IsFalse(S8aBatchFile.TryParse(new byte[] { 3, 0, 0, 0 }, 4, 12, 18, 12, out bad, out err), "version 3");
            Assert.IsFalse(S8aBatchFile.TryParse(bytes, bytes.Length - 1, 25, 18, 25, out bad, out err), "truncated");
            var extra = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, extra, 0, bytes.Length);
            Assert.IsFalse(S8aBatchFile.TryParse(extra, extra.Length, 25, 18, 25, out bad, out err), "bytes left over");
            Assert.IsFalse(S8aBatchFile.TryParse(bytes, bytes.Length, 25, 18, 12, out bad, out err), "another id's layout does not consume the file exactly");
        }
    }
}
