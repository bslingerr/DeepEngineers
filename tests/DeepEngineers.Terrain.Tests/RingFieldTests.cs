using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S8a Stage F2: the ring field (RingField) against its Python twin (tools/planet/s8a_ring3d.py vectors): the rules text,
 /// the repo world mod's face-1 maps, the seabed at columns (bit for bit), the placements (count and hash) and the content
 /// bytes of a few boxes (byte for byte), on the committed synthetic library and, when the store holds the very library the
 /// vectors were made with, on that one too.
 /// </summary>
    [TestClass]
    public class RingFieldTests
    {
        private static readonly object Gate = new object();
        private static RingBase _base;

        private static string VectorDir { get { return TestEnv.PathIn("tests", "s8a", "vectors", "ring3d"); } }

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

        private static Dictionary<string, List<string[]>> ReadVectors(string name)
        {
            string path = Path.Combine(VectorDir, name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python tools/planet/s8a_ring3d.py vectors)");
            var d = new Dictionary<string, List<string[]>>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] f = raw.Split('\t');
                if (f.Length < 2) continue;
                List<string[]> l;
                if (!d.TryGetValue(f[0], out l)) d[f[0]] = l = new List<string[]>();
                l.Add(f);
            }
            return d;
        }

        private static ulong Hex(string s) { return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture); }

        [TestMethod]
        public void RulesText_MatchesTwin()
        {
            var v = ReadVectors("ring3d-vectors.tsv");
            Assert.AreEqual(Hex(v["rules"][0][1]), RingRules.TextHash(RingRules.Seed), "the rules text differs from s8a_ring3d.rules_text:\n" + RingRules.Text(RingRules.Seed));
        }

        [TestMethod]
        public void Base_ColumnsBitForBit()
        {
            var v = ReadVectors("ring3d-vectors.tsv");
            Assert.AreEqual(Hex(v["heights"][0][1]), Base.HeightsFnv, "face-1 heights (back.png) differ from the twin's");
            Assert.AreEqual(Hex(v["biomes"][0][1]), Base.BiomesFnv, "face-1 biome map (back_mat.png G) differs from the twin's");
            foreach (string[] c in v["column"])
            {
                double x = double.Parse(c[1], CultureInfo.InvariantCulture), z = double.Parse(c[2], CultureInfo.InvariantCulture);
                double h;
                int key;
                Base.Column(x, z, out h, out key);
                Assert.AreEqual(Hex(c[3]), (ulong)BitConverter.DoubleToInt64Bits(h), "seabed at (" + c[1] + ", " + c[2] + "): " + h.ToString("R", CultureInfo.InvariantCulture) + " vs the twin's " + c[5]);
                Assert.AreEqual(int.Parse(c[4], CultureInfo.InvariantCulture), key, "rule key at (" + c[1] + ", " + c[2] + ")");
            }
        }

        [TestMethod]
        public void Synthetic_PlacementsAndBoxesByteForByte()
        {
            var v = ReadVectors("ring3d-vectors.tsv");
            RingBrushLibrary lib = RingBrushLibrary.Load(Path.Combine(VectorDir, "synthetic-library.bin"), Path.Combine(VectorDir, "synthetic-index.tsv"));
            Assert.AreEqual(Hex(v["library"][0][1]), lib.BinFnv, "synthetic library bytes");
            Assert.AreEqual(Hex(v["library"][0][2]), lib.IndexFnv, "synthetic index bytes");
            Check(v, lib, "synthetic");
        }

        [TestMethod]
        public void Install_PlacementsAndBoxesByteForByte()
        {
            string bin = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.BinName), idx = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.IndexName);
            string vp = Path.Combine(VectorDir, "ring3d-install-vectors.tsv");
            if (!File.Exists(vp) || !File.Exists(bin) || !File.Exists(idx)) Assert.Inconclusive("no store library or no install vectors (python tools/planet/s8a_brushes.py build; s8a_ring3d.py vectors)");
            var v = ReadVectors("ring3d-install-vectors.tsv");
            RingBrushLibrary lib = RingBrushLibrary.Load(bin, idx);
            if (lib.BinFnv != Hex(v["library"][0][1]) || lib.IndexFnv != Hex(v["library"][0][2]))
                Assert.Inconclusive("the store's library is not the one the install vectors were made with (rebuilt since: rerun s8a_ring3d.py vectors)");
            Assert.AreEqual(0, lib.Forbidden().Count, "S8a.F.forbidden: brushes near a landmark root or below the deep limit");
            Check(v, lib, "install");
        }

        private static void Check(Dictionary<string, List<string[]>> v, RingBrushLibrary lib, string what)
        {
            var field = new RingField(Base, lib, RingRules.Seed);
            field.Place();
            Assert.AreEqual(int.Parse(v["placements"][0][1], CultureInfo.InvariantCulture), field.Placements.Count, what + ": placements");
            Assert.AreEqual(Hex(v["placements"][0][2]), field.PlacementsHash(), what + ": placements hash");
            int brushes;
            Assert.IsTrue(field.NearestSameBrushM(out brushes) >= RingRules.RepeatM, what + ": S8a.F.repeat");
            foreach (string[] b in v["box"])
            {
                double x0 = double.Parse(b[2], CultureInfo.InvariantCulture), y0 = double.Parse(b[3], CultureInfo.InvariantCulture), z0 = double.Parse(b[4], CultureInfo.InvariantCulture);
                int nx = int.Parse(b[5], CultureInfo.InvariantCulture), ny = int.Parse(b[6], CultureInfo.InvariantCulture), nz = int.Parse(b[7], CultureInfo.InvariantCulture);
                int near;
                byte[] c = field.ContentBox(x0, y0, z0, nx, ny, nz, out near);
                int solid = 0, empty = 0;
                foreach (byte x in c) { if (x == 255) solid++; else if (x == 0) empty++; }
                string detail = what + " box " + b[1] + ": solid " + solid + " (twin " + b[9] + "), empty " + empty + " (twin " + b[10] + "), placements " + near + " (twin " + b[11] + ")";
                Assert.AreEqual(int.Parse(b[11], CultureInfo.InvariantCulture), near, detail);
                if (Hex(b[8]) != RingHash.Fnv(c))
                {
                    // the twin's probe values: the first differing f tells which step drifted
                    var probes = new List<string>();
                    foreach (string pr in b[12].Split(','))
                    {
                        string[] kv = pr.Split(':');
                        int i = int.Parse(kv[0], CultureInfo.InvariantCulture);
                        probes.Add(i + ": byte " + c[i] + ", twin f " + kv[1]);
                    }
                    Assert.Fail(detail + ": content bytes differ from the twin's; " + string.Join("; ", probes.ToArray()));
                }
            }
        }

        private static void CheckServe(Dictionary<string, List<string[]>> v, RingField field, string what)
        {
            Assert.IsTrue(v.ContainsKey("serve") && v["serve"].Count >= 3, what + ": serve vectors missing (python tools/planet/s8a_ring3d.py vectors)");
            foreach (string[] b in v["serve"])
            {
                double ax = double.Parse(b[2], CultureInfo.InvariantCulture), ay = double.Parse(b[3], CultureInfo.InvariantCulture), az = double.Parse(b[4], CultureInfo.InvariantCulture);
                int sx = int.Parse(b[5], CultureInfo.InvariantCulture), sy = int.Parse(b[6], CultureInfo.InvariantCulture), sz = int.Parse(b[7], CultureInfo.InvariantCulture);
                double lod = double.Parse(b[8], CultureInfo.InvariantCulture);
                double x0, x1, y0, y1, z0, z1;
                Assert.IsTrue(RingField.SnBounds(ax, ay, az, ax + (sx - 1) * lod, ay + (sy - 1) * lod, az + (sz - 1) * lod, out x0, out x1, out y0, out y1, out z0, out z1), b[1]);
                var near = new List<RingPlacement>();
                field.Near(x0 - 2.0, x1 + 2.0, z0 - 2.0, z1 + 2.0, near);
                var c = new byte[sx * sy * sz];
                field.ServeBox(ax, ay, az, sx, sy, sz, lod, c, 0, sx, sx * sy, near);
                int solid = 0, empty = 0;
                foreach (byte x in c) { if (x == 255) solid++; else if (x == 0) empty++; }
                Assert.AreEqual(Hex(b[9]), RingHash.Fnv(c), what + " serve " + b[1] + ": the served bytes differ from the twin (solid " + solid + " vs " + b[10] + ", empty " + empty + " vs " + b[11] + ")");
            }
        }

        [TestMethod]
        public void Serve_SyntheticBytesForBytes()
        {
            var v = ReadVectors("ring3d-vectors.tsv");
            RingBrushLibrary lib = RingBrushLibrary.Load(Path.Combine(VectorDir, "synthetic-library.bin"), Path.Combine(VectorDir, "synthetic-index.tsv"));
            var field = new RingField(Base, lib, RingRules.Seed);
            field.Place();
            CheckServe(v, field, "synthetic");
        }

        [TestMethod]
        public void Serve_InstallBytesForBytes()
        {
            string bin = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.BinName), idx = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.IndexName);
            string vp = Path.Combine(VectorDir, "ring3d-install-vectors.tsv");
            if (!File.Exists(vp) || !File.Exists(bin) || !File.Exists(idx)) Assert.Inconclusive("no store library or no install vectors");
            var v = ReadVectors("ring3d-install-vectors.tsv");
            RingBrushLibrary lib = RingBrushLibrary.Load(bin, idx);
            if (lib.BinFnv != Hex(v["library"][0][1]) || lib.IndexFnv != Hex(v["library"][0][2])) Assert.Inconclusive("the store library is not the one of the vectors");
            var field = new RingField(Base, lib, RingRules.Seed);
            field.Place();
            CheckServe(v, field, "install");
        }

        [TestMethod]
        public void AtanSmall_MatchesMathAtan()
        {
            foreach (double t in new[] { 0.0, 1e-9, 0.01, 0.1, 0.2, 0.3 })
                Assert.AreEqual(Math.Atan(t), RingField.AtanSmall(t), 2e-17, "t " + t);
            double x, y, z;
            Assert.IsFalse(RingField.SnOfPlanetLocal(0, 0, -60000.0, out x, out y, out z), "the far side of the planet is outside the site angle");
            Assert.IsTrue(RingField.SnOfPlanetLocal(-5000.0, 0, 59790.0, out x, out y, out z));
            Assert.AreEqual(0.0, z, 1e-9);
            Assert.IsTrue(x > 4990 && x < 5010, "east along -X: " + x);
        }

        [TestMethod]
        public void Png_ReadsTheWorldModMaps()
        {
            RingPng.Image im = RingPng.Read(TestEnv.PathIn("src", "DeepEngineers.WorldMod", "Data", "PlanetDataFiles", "DE_4546B", "back_mat.png"));
            Assert.AreEqual(2048, im.Width);
            Assert.AreEqual(4, im.Channels);
            byte[] g = RingPng.Channel(im, 1);
            int max = 0;
            foreach (byte x in g) max = Math.Max(max, x);
            Assert.AreEqual(13, max, "biome G values 1..13");
        }

        [TestMethod]
        public void Field_PointwiseAndDeterministic()
        {
            RingBrushLibrary lib = RingBrushLibrary.Load(Path.Combine(VectorDir, "synthetic-library.bin"), Path.Combine(VectorDir, "synthetic-index.tsv"));
            var field = new RingField(Base, lib, RingRules.Seed);
            field.Place();
            RingPlacement p = field.Placements[field.Placements.Count / 2];
            int n1, n2, n3;
            byte[] whole = field.ContentBox(Math.Floor(p.X) - 16, Math.Floor(p.H) - 20, Math.Floor(p.Z) - 16, 32, 48, 32, out n1);
            byte[] left = field.ContentBox(Math.Floor(p.X) - 16, Math.Floor(p.H) - 20, Math.Floor(p.Z) - 16, 16, 48, 32, out n2);
            byte[] right = field.ContentBox(Math.Floor(p.X), Math.Floor(p.H) - 20, Math.Floor(p.Z) - 16, 16, 48, 32, out n3);
            for (int k = 0; k < 32; k++)
                for (int j = 0; j < 48; j++)
                    for (int i = 0; i < 32; i++)
                    {
                        byte a = whole[i + 32 * (j + 48 * k)];
                        byte b = i < 16 ? left[i + 16 * (j + 48 * k)] : right[i - 16 + 16 * (j + 48 * k)];
                        if (a != b) Assert.Fail("seam: the two halves differ from the whole box at " + i + "," + j + "," + k);
                    }
            double f1 = field.Sample(p.X + 3.25, p.H + 2.5, p.Z - 1.75), f2 = field.Sample(p.X + 3.25, p.H + 2.5, p.Z - 1.75);
            Assert.AreEqual(f1, f2);
            Assert.IsTrue(field.ReachAboveM >= RingRules.ArchB3 && field.ReachBelowM >= RingRules.CaveB3);
        }
    }
}
