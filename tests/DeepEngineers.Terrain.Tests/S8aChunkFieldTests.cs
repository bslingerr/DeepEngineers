using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S8a planet v2, the sample (route B's first slice): the chunk field (S8aChunkField) against its Python twin
 /// (tools/planet/s8a_chunks.py vectors): pointwise values bit for bit and the served bytes of four boxes byte for byte on the
 /// committed synthetic chunk, and on the store's chunk when the store holds the very chunk the install vectors were made with.
 /// </summary>
    [TestClass]
    public class S8aChunkFieldTests
    {
        private static readonly object Gate = new object();
        private static RingBase _base;

        private static string VectorDir { get { return TestEnv.PathIn("tests", "s8a", "vectors", "sample"); } }

        private static RingBase Base
        {
            get
            {
                lock (Gate)
                {
                    if (_base == null) _base = RingBase.FromPlanetDir(TestEnv.PathIn("src", "DeepEngineers.WorldMod", "Data", "PlanetDataFiles", "DE_4546B"));
                    return _base;
                }
            }
        }

        private static List<string[]> Read(string path)
        {
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python tools/planet/s8a_chunks.py vectors)");
            var l = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path)) l.Add(raw.Split('\t'));
            return l;
        }

        private static double D(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static ulong Hex(string s) { return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }

        private static void Check(S8aChunkField f, List<string[]> v, string what)
        {
            int points = 0, boxes = 0;
            foreach (string[] r in v)
            {
                if (r[0] == "point")
                {
                    double h, w;
                    double got = f.Sample(D(r[1]), D(r[2]), D(r[3]), out h, out w);
                    Assert.AreEqual(BitConverter.DoubleToInt64Bits(D(r[4])), BitConverter.DoubleToInt64Bits(got), what + " point " + points + ": twin " + r[4] + ", C# " + got.ToString("R", CultureInfo.InvariantCulture));
                    Assert.AreEqual(BitConverter.DoubleToInt64Bits(D(r[5])), BitConverter.DoubleToInt64Bits(h), what + " point " + points + " heightmap: twin " + r[5] + ", C# " + h.ToString("R", CultureInfo.InvariantCulture));
                    points++;
                }
                else if (r[0] == "serve")
                {
                    int sx = int.Parse(r[5], CultureInfo.InvariantCulture), sy = int.Parse(r[6], CultureInfo.InvariantCulture), sz = int.Parse(r[7], CultureInfo.InvariantCulture);
                    int lod = int.Parse(r[8], CultureInfo.InvariantCulture);
                    var c = new byte[sx * sy * sz];
                    f.ServeBox(D(r[2]), D(r[3]), D(r[4]), sx, sy, sz, lod, c, 0, sx, sx * sy);
                    int full = 0, empty = 0;
                    foreach (byte b in c) { if (b == 255) full++; if (b == 0) empty++; }
                    Assert.AreEqual(Hex(r[9]), RingHash.Fnv(c), what + " box " + r[1] + ": full " + full + " (twin " + r[10] + "), empty " + empty + " (twin " + r[11] + ")");
                    boxes++;
                }
            }
            Assert.IsTrue(points >= 8 && boxes >= 4, what + ": " + points + " points, " + boxes + " boxes in the vectors");
        }

        [TestMethod]
        public void Synthetic_PointsAndBoxesByteForByte()
        {
            S8aChunk ch = S8aChunk.Load(Path.Combine(VectorDir, "chunk-synth-v1.bin"), Path.Combine(VectorDir, "chunk-synth-v1.tsv"));
            S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(Path.Combine(VectorDir, "synthetic-sample.txt")));
            List<string[]> v = Read(Path.Combine(VectorDir, "sample-vectors.tsv"));
            Assert.AreEqual(Hex(v[1][1]), ch.BinFnv, "the synthetic chunk differs from the one the vectors were made with");
            Assert.AreEqual(Hex(v[2][1]), p.TextFnv, "the synthetic placement differs from the one the vectors were made with");
            Assert.AreEqual(13, ch.Entities.Count, "entity records of the synthetic chunk");
            Check(new S8aChunkField(ch, p, Base), v, "synthetic");
        }

        [TestMethod]
        public void Install_PointsAndBoxesByteForByte()
        {
            string vp = Path.Combine(VectorDir, "sample-install-vectors.tsv");
            if (!File.Exists(vp)) { Assert.Inconclusive("no install vectors (the store had no chunk when they were written)"); return; }
            List<string[]> v = Read(vp);
            string bin = Path.Combine(S8aChunk.DefaultFolder(), S8aChunk.BinName("gp1")), idx = Path.Combine(S8aChunk.DefaultFolder(), S8aChunk.IndexName("gp1"));
            string sp = S8aSamplePlacement.DefaultPath();
            if (!File.Exists(bin) || !File.Exists(idx) || !File.Exists(sp)) { Assert.Inconclusive("no chunk in the store (python tools/planet/s8a_chunks.py build)"); return; }
            S8aChunk ch = S8aChunk.Load(bin, idx);
            S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(sp));
            if (ch.BinFnv != Hex(v[1][1]) || p.TextFnv != Hex(v[2][1])) { Assert.Inconclusive("the store's chunk or placement is not the one the install vectors were made with"); return; }
            Check(new S8aChunkField(ch, p, Base), v, "install");
        }

        /// <summary>The fast ServeBox (decoded-octree cache, the saturated shortcuts, the box fills, no heightmap at weight 1) = the per-voxel reference.</summary>
        private static void FastEqualsReference(S8aChunkField f, int boxes, int seed, string what, out double fastNs, out double refNs)
        {
            var rng = new Random(seed);
            long fastTicks = 0, refTicks = 0, voxels = 0;
            var lf = new long[4];
            var lr = new long[4];
            var lv = new long[4];
            int checkedBoxes = 0, mixed = 0;
            double half = f.Place.HalfM;
            for (int b = 0; b < boxes; b++)
            {
                int lodIndex = b % 4;
                double lod = 1 << lodIndex;
                int n = lodIndex == 0 ? 24 : 16;
                double u = (rng.NextDouble() * 2.0 - 1.0) * (half + 40.0), v = (rng.NextDouble() * 2.0 - 1.0) * (half + 40.0);
                double y = f.YLo + rng.NextDouble() * (f.YHi - f.YLo);
                double px, py, pz;
                f.LocalOfTangent(u, v, y, out px, out py, out pz);
                double g = 8.0 * lod;
                double ax = g * Math.Floor(px / g), ay = g * Math.Floor(py / g), az = g * Math.Floor(pz / g);
                var a = new byte[n * n * n];
                var r = new byte[n * n * n];
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int fa = f.ServeBox(ax, ay, az, n, n, n, lod, a, 0, n, n * n);
                fastTicks += sw.ElapsedTicks;
                lf[lodIndex] += sw.ElapsedTicks;
                sw.Restart();
                int fr = f.ServeBoxReference(ax, ay, az, n, n, n, lod, r, 0, n, n * n);
                refTicks += sw.ElapsedTicks;
                lr[lodIndex] += sw.ElapsedTicks;
                voxels += n * n * n;
                lv[lodIndex] += n * n * n;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != r[i]) Assert.Fail(what + " box " + b + " (lod " + lod + " at " + ax + ", " + ay + ", " + az + "): voxel " + i + " fast " + a[i] + " reference " + r[i]);
                Assert.AreEqual(fr, fa, what + " box " + b + ": the all-empty / all-full flags");
                checkedBoxes++;
                if (fr == 0) mixed++;
            }
            Assert.IsTrue(mixed >= boxes / 8, what + ": only " + mixed + " of " + checkedBoxes + " boxes held rock and water");
            fastNs = fastTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / voxels;
            refNs = refTicks * 1e9 / System.Diagnostics.Stopwatch.Frequency / voxels;
            Console.WriteLine(what + ": " + checkedBoxes + " boxes (" + mixed + " mixed), fast " + fastNs.ToString("0", CultureInfo.InvariantCulture) + " ns/voxel, reference " +
                              refNs.ToString("0", CultureInfo.InvariantCulture) + " ns/voxel, filled boxes " + S8aChunkField.BoxesFilled + ", decodes " + S8aChunk.Decodes);
            for (int l = 0; l < 4; l++)
                if (lv[l] > 0)
                    Console.WriteLine(what + " LOD " + l + ": fast " + (lf[l] * 1e9 / System.Diagnostics.Stopwatch.Frequency / lv[l]).ToString("0", CultureInfo.InvariantCulture) +
                                      " ns/voxel, reference " + (lr[l] * 1e9 / System.Diagnostics.Stopwatch.Frequency / lv[l]).ToString("0", CultureInfo.InvariantCulture) + " ns/voxel");
        }

        [TestMethod]
        public void Fast_EqualsReference_Synthetic()
        {
            S8aChunk ch = S8aChunk.Load(Path.Combine(VectorDir, "chunk-synth-v1.bin"), Path.Combine(VectorDir, "chunk-synth-v1.tsv"));
            S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(Path.Combine(VectorDir, "synthetic-sample.txt")));
            double a, b;
            FastEqualsReference(new S8aChunkField(ch, p, Base), 160, 4546, "synthetic", out a, out b);
        }

        [TestMethod]
        public void Fast_EqualsReference_Install()
        {
            string bin = Path.Combine(S8aChunk.DefaultFolder(), S8aChunk.BinName("gp1")), idx = Path.Combine(S8aChunk.DefaultFolder(), S8aChunk.IndexName("gp1"));
            string sp = S8aSamplePlacement.DefaultPath();
            if (!File.Exists(bin) || !File.Exists(idx) || !File.Exists(sp)) { Assert.Inconclusive("no chunk in the store (python tools/planet/s8a_chunks.py build)"); return; }
            S8aChunk ch = S8aChunk.Load(bin, idx);
            S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(sp));
            double a, b;
            FastEqualsReference(new S8aChunkField(ch, p, Base), 400, 2610, "install", out a, out b);
            Assert.IsTrue(a < b, "the fast path is not faster (" + a + " vs " + b + " ns per voxel)");
        }

        [TestMethod]
        public void Field_TurnsAndFrameAreInverse()
        {
            S8aChunk ch = S8aChunk.Load(Path.Combine(VectorDir, "chunk-synth-v1.bin"), Path.Combine(VectorDir, "chunk-synth-v1.tsv"));
            S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(Path.Combine(VectorDir, "synthetic-sample.txt")));
            var f = new S8aChunkField(ch, p, Base);
            foreach (S8aChunkEntity e in ch.Entities)
            {
                double px, py, pz, x, y, z;
                f.NativeToLocal(e.X, e.Y, e.Z, out px, out py, out pz);
                f.LocalToNative(px, py, pz, out x, out y, out z);
                Assert.AreEqual(e.X, x, 1e-6, "x of entity " + e.Index);
                Assert.AreEqual(e.Y, y, 1e-6, "y of entity " + e.Index);
                Assert.AreEqual(e.Z, z, 1e-6, "z of entity " + e.Index);
                Assert.IsTrue(f.Touches(px - 1, py - 1, pz - 1, px + 1, py + 1, pz + 1, 1.0), "the zone holds entity " + e.Index);
            }
            // far from the footprint the field is the heightmap's
            double qx, qy, qz, h, w;
            f.LocalOfTangent(p.HalfM + 200.0, 0.0, -100.0, out qx, out qy, out qz);
            double s = f.Sample(qx, qy, qz, out h, out w);
            Assert.AreEqual(0.0, w, "no chunk weight outside the square");
            Assert.AreEqual(h - (Math.Sqrt(qx * qx + qy * qy + qz * qz) - RingRules.Rs), s, 1e-9, "the heightmap outside the square");
            Assert.IsFalse(f.Touches(qx - 4, qy - 4, qz - 4, qx + 4, qy + 4, qz + 4, 1.0), "a box 200 m outside is not in the zone");
        }
    }
}
