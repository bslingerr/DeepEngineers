using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a biomes and spawns (src/DeepEngineers.Wire/S8aBiomes.cs; CONTRACT §5.4 "S8a conventions" biome.names, biome.rule,
 /// convert.material, spawn.place, spawn.table): the pinned lines = the data rendered back = the canonical text; the
 /// converter core's material hook (S8aMaterialMap) agrees with the table; the Python twin's vectors
 /// (tests/s8a/vectors/biomes.tsv, `python -I tools/planet/s8a_biomes.py vectors`) line by line; the seabed scan over
 /// batch files written by the converter core; the plan's statistics; the spawn ledger.
 /// </summary>
    [TestClass]
    public class S8aBiomesTests
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [TestMethod]
        public void Lines_AreInTheCanonicalText_AndRenderFromTheData()
        {
            string text = S8aConventions.CanonicalText();
            var lines = new HashSet<string>(text.Split('\n'));
            foreach (string l in S8aBiomes.Lines)
            {
                Assert.IsTrue(lines.Contains(l), "not in the canonical text: " + l);
                CollectionAssert.Contains(S8aConventions.FormatLines, l.Substring("format ".Length), "FormatLines hold the lines without 'format '");
            }
            Assert.AreEqual(S8aBiomes.LineBiomeNames, S8aBiomes.RenderNamesLine());
            Assert.AreEqual(S8aBiomes.LineMaterial, S8aBiomeMaterials.RenderLine());
            Assert.AreEqual(S8aBiomes.LineSpawnTable, S8aSpawnTable.RenderLine());
            Assert.AreEqual(19, S8aSpawnTable.Rows.Length);
            Assert.AreEqual(S8aConventions.Spawn.RadiusM, 150);
            Assert.AreEqual(S8aConventions.Spawn.DespawnM, 250);
            Assert.AreEqual(S8aConventions.Window.BiomeRadiusM, 200);
        }

        [TestMethod]
        public void MaterialHook_AndBiomeRule_AreTheCoresAndConsistent()
        {
            foreach (string s in S8aMaterialMap.Subtypes) Assert.AreEqual(S8aMaterialMap.SnType(s), S8aBiomeMaterials.SnTypeOf(s));
            Assert.AreEqual(S8aBiomeMaterials.Other, S8aMaterialMap.SnType("Basalt"));
            // the expected type of every determined biome is the table type of the material the rule maps to it
            foreach (string s in S8aMaterialMap.Subtypes)
                foreach (double d in new[] { 10.0, 59.0, 61.0, 300.0, 999.0 })
                {
                    byte b = S8aBiomes.CameraBiome(s, d);
                    int want = S8aBiomes.ExpectedSnType(b, d);
                    if (want >= 0) Assert.AreEqual((int)S8aMaterialMap.SnType(s), want, s + " at " + d + " m -> biome " + b);
                    else Assert.AreEqual(S8aBiomes.Void, b, s + " at " + d);
                }
            Assert.AreEqual(S8aBiomes.Void, S8aBiomes.CameraBiome("Sand_02", 1000.5));
            Assert.AreEqual(S8aBiomes.None, S8aBiomes.CameraBiome(null, 10));
            Assert.AreEqual("kelpForest", S8aBiomes.BiomeAt(3000, -50, 0, 3000, -10, 0, S8aBiomes.KelpForest));
            Assert.AreEqual("void", S8aBiomes.BiomeAt(3000, -50, 0, 3300, -10, 0, S8aBiomes.KelpForest));
            Assert.IsNull(S8aBiomes.BiomeAt(0, -50, 0, 0, -10, 0, S8aBiomes.KelpForest), "inside the crater box: Subnautica's own answer");
            Assert.IsNull(S8aBiomes.BiomeAt(3000, -50, 0, 3000, -10, 0, S8aBiomes.None));
        }

        [TestMethod]
        public void BiomeMap_WinsOverTheMaterialRule_AndNamesTheDesignedBiomes()
        {
            // Stage C (ext s8a terrain 2): the planet's biome map G = names index + 1; 0 or out of range: the material rule
            Assert.AreEqual(13, S8aBiomes.Names.Length);
            Assert.AreEqual("grandReef", S8aBiomes.Name(S8aBiomes.GrandReef));
            Assert.AreEqual("lostRiver_Canyon", S8aBiomes.Name(S8aBiomes.LostRiverCanyon));
            Assert.AreEqual(S8aBiomes.GrandReef, S8aBiomes.CameraBiome("Sand_02", 30.0, S8aBiomes.GrandReef + 1));
            Assert.AreEqual(S8aBiomes.SafeShallows, S8aBiomes.CameraBiome("Sand_02", 30.0, 0));
            Assert.AreEqual(S8aBiomes.Mountains, S8aBiomes.CameraBiome("Grass", 5.0, 14), "G 14 is out of range: the material rule");
            Assert.AreEqual(S8aBiomes.Void, S8aBiomes.CameraBiome(null, 5.0, S8aBiomes.Void + 1));
            Assert.AreEqual(S8aBiomes.None, S8aBiomes.CameraBiome(null, 5.0, 0));
            // the ore rows: named, not surface materials, mapped to their own types
            Assert.AreEqual(16, S8aMaterialMap.Subtypes.Length);
            Assert.AreEqual(S8aMaterialMap.Subtypes.Length, S8aMaterialMap.SnTypes.Length);
            Assert.IsTrue(S8aMaterialMap.IsOre("Platinum_01") && !S8aMaterialMap.IsOre("Stone_05"));
            Assert.AreEqual(7, S8aMaterialMap.SculptValue("Grass"));
            Assert.AreEqual(0, S8aMaterialMap.SculptValue("Iron_02"));
            Assert.AreEqual(95, S8aMaterialMap.SnType("Iron_02"));
            Assert.AreEqual(122, S8aMaterialMap.SnType("Uraninite_01"));
        }

        [TestMethod]
        public void PythonTwinVectors_MatchLineByLine()
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s8a", "vectors", "biomes.tsv");
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/planet/s8a_biomes.py vectors)");
            var plans = new Dictionary<int, List<S8aSpawnPoint>>();
            int n = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                string[] f = raw.Split('\t');
                n++;
                switch (f[0])
                {
                    case "line":
                        CollectionAssert.Contains(S8aBiomes.Lines, f[2], f[1]);
                        break;
                    case "name":
                        Assert.AreEqual(f[2], S8aBiomes.Name(byte.Parse(f[1], Inv)));
                        break;
                    case "material":
                        Assert.AreEqual(int.Parse(f[2], Inv), (int)S8aBiomeMaterials.SnTypeOf(f[1]), raw);
                        break;
                    case "biome":
                        Assert.AreEqual(int.Parse(f[3], Inv), (int)S8aBiomes.CameraBiome(f[1], D(f[2])), raw);
                        break;
                    case "biomemap":
                        Assert.AreEqual(int.Parse(f[4], Inv), (int)S8aBiomes.CameraBiome(f[2] == "-" ? null : f[2], D(f[3]), int.Parse(f[1], Inv)), raw);
                        break;
                    case "expect":
                        Assert.AreEqual(int.Parse(f[3], Inv), S8aBiomes.ExpectedSnType(byte.Parse(f[1], Inv), D(f[2])), raw);
                        break;
                    case "crater":
                        Assert.AreEqual(f[3] == "1", S8aBiomes.InCraterBox(D(f[1]), D(f[2])), raw);
                        break;
                    case "window":
                        Assert.AreEqual(f[3] == "1", S8aBiomes.IsWindowColumn(int.Parse(f[1], Inv), int.Parse(f[2], Inv)), raw);
                        break;
                    case "biomeat":
                    {
                        string got = S8aBiomes.BiomeAt(D(f[1]), D(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[6]), byte.Parse(f[7], Inv));
                        Assert.AreEqual(f[8] == "-" ? null : f[8], got, raw);
                        break;
                    }
                    case "band":
                        Assert.AreEqual(int.Parse(f[2], Inv), S8aSpawnTable.BandOf(D(f[1])), raw);
                        break;
                    case "density":
                        Assert.AreEqual(D(f[2]), S8aSpawnTable.DensityOfBand(int.Parse(f[1], Inv)), 1e-12, raw);
                        break;
                    case "key":
                    {
                        ulong ck = ulong.Parse(f[1], NumberStyles.HexNumber, Inv);
                        ulong k = S8aSpawnTable.Key(ck, int.Parse(f[2], Inv), int.Parse(f[3], Inv), int.Parse(f[4], Inv), int.Parse(f[5], Inv));
                        Assert.AreEqual(ulong.Parse(f[6], NumberStyles.HexNumber, Inv), k, raw);
                        Assert.AreEqual(D(f[7]), S8aSpawnTable.Draw(k), 0.0, raw);
                        double jx, jz, yaw, hover;
                        S8aSpawnTable.Placement(k, out jx, out jz, out yaw, out hover);
                        Assert.AreEqual(D(f[8]), jx, 0.0, raw);
                        Assert.AreEqual(D(f[9]), jz, 0.0, raw);
                        Assert.AreEqual(D(f[10]), yaw, 0.0, raw);
                        Assert.AreEqual(D(f[11]), hover, 0.0, raw);
                        break;
                    }
                    case "plan":
                    {
                        int pid = int.Parse(f[1], Inv);
                        ulong ck = ulong.Parse(f[3], NumberStyles.HexNumber, Inv);
                        int bx = int.Parse(f[4], Inv), bz = int.Parse(f[5], Inv);
                        double scale = D(f[6]);
                        var disabled = f[7] == "-" ? new HashSet<int>() : new HashSet<int>(f[7].Split(',').Select(x => int.Parse(x, Inv)));
                        S8aSeabedColumns cols = Synthetic(f[2], bx, bz);
                        var pts = new List<S8aSpawnPoint>();
                        cols.Plan(ck, scale, disabled, pts);
                        Assert.AreEqual(int.Parse(f[8], Inv), pts.Count, raw);
                        ulong fold = 0;
                        foreach (S8aSpawnPoint p in pts) fold ^= p.Key;
                        Assert.AreEqual(ulong.Parse(f[9], NumberStyles.HexNumber, Inv), fold, raw);
                        var perRow = new int[S8aSpawnTable.Rows.Length];
                        foreach (S8aSpawnPoint p in pts) perRow[p.Row]++;
                        Assert.AreEqual(f[10], string.Join(",", perRow), raw);
                        plans[pid] = pts;
                        break;
                    }
                    case "planpt":
                    {
                        List<S8aSpawnPoint> pts = plans[int.Parse(f[1], Inv)];
                        S8aSpawnPoint p = pts[int.Parse(f[2], Inv)];
                        Assert.AreEqual(ulong.Parse(f[3], NumberStyles.HexNumber, Inv), p.Key, raw);
                        Assert.AreEqual(int.Parse(f[4], Inv), p.Row, raw);
                        Assert.AreEqual(int.Parse(f[5], Inv), p.Column, raw);
                        Assert.AreEqual(D(f[6]), p.X, 0.0, raw);
                        Assert.AreEqual(D(f[7]), p.Y, 0.0, raw);
                        Assert.AreEqual(D(f[8]), p.Z, 0.0, raw);
                        Assert.AreEqual(D(f[9]), p.SeabedY, 0.0, raw);
                        Assert.AreEqual(D(f[10]), p.YawDeg, 0.0, raw);
                        break;
                    }
                    default:
                        Assert.Fail("unknown vector kind: " + raw);
                        break;
                }
            }
            Assert.IsTrue(n > 150, "vector lines: " + n);
        }

        private static double D(string s) { return double.Parse(s, NumberStyles.Float, Inv); }

        /// <summary>The twin's synthetic seabeds (s8a_biomes.py _seabed_fn): float32 heights, NaN where the twin answers None.</summary>
        private static S8aSeabedColumns Synthetic(string kind, int bx, int bz)
        {
            var c = new S8aSeabedColumns(bx, bz);
            for (int lz = 0; lz < 160; lz++)
                for (int lx = 0; lx < 160; lx++)
                {
                    float v;
                    switch (kind)
                    {
                        case "slope": v = (float)(-(20.0 + 0.875 * lx + 0.375 * lz)); break;
                        case "deep": v = (float)(-(450.0 + 4.0 * lx)); break;
                        case "holes": v = (lx * 7 + lz * 3) % 5 == 0 ? float.NaN : (float)(-(30.0 + 0.25 * lz)); break;
                        default: throw new ArgumentException(kind);
                    }
                    c.TopY[S8aSeabedColumns.ColumnIndex(lx, lz)] = v;
                }
            return c;
        }

        /// <summary>The analytic seabed of the scan test (SN y), crossing octree boundaries in y.</summary>
        private static double Surface(int lx, int lz) { return -150.0 + 0.5 * lx + 0.25 * lz + 0.137; }

        /// <summary>A window batch (30, 18, −3) written by the converter core from the analytic surface (convert.sample's encoding).</summary>
        private static byte[] SurfaceBatch(int bx, int by, int bz, byte type)
        {
            S8aBatchFile f = S8aBatchFile.CreateEmpty(bx, by, bz);
            var t32 = new byte[S8aOctree.Voxels];
            var d32 = new byte[S8aOctree.Voxels];
            for (int ix = 0; ix < 5; ix++)
                for (int iy = 0; iy < 5; iy++)
                    for (int iz = 0; iz < 5; iz++)
                    {
                        for (int z = 0; z < 32; z++)
                            for (int y = 0; y < 32; y++)
                                for (int x = 0; x < 32; x++)
                                {
                                    int lx = ix * 32 + x, lz = iz * 32 + z;
                                    double centre = SnWorld.OriginY + by * 160 + iy * 32 + y + 0.5;
                                    double s = Surface(lx, lz) - centre;
                                    if (s > 1) s = 1; else if (s < -1) s = -1;
                                    int k = S8aOctree.Index(x, y, z);
                                    if (Math.Abs(s) >= 1) { d32[k] = 0; t32[k] = s > 0 ? type : (byte)0; }
                                    else { d32[k] = (byte)(Math.Floor(s * 126) + 126); t32[k] = s >= 0 ? type : (byte)0; }
                                }
                        f.SetOctree(S8aBatchFile.OctreeIndex(ix, iy, iz), S8aOctree.EncodeOctree(t32, d32));
                    }
            return f.Serialize();
        }

        [TestMethod]
        public void SeabedScan_FindsTheConvertedSurface_AcrossOctreeBoundaries()
        {
            const int bx = 30, bz = -3, by = 18;
            byte[] file = SurfaceBatch(bx, by, bz, 111);
            int asked = 0;
            S8aSeabedColumns c = S8aSeabedColumns.Scan(bx, bz, b => { asked++; return b == by ? file : null; });
            Assert.AreEqual(1, c.FilesRead);
            Assert.AreEqual(0, c.BadFiles, c.LastError);
            Assert.AreEqual(160 * 160, c.Resolved);
            Assert.AreEqual(2, asked, "stops once every column is solved (by 19 water, by 18 the surface)");
            double worst = 0;
            for (int lz = 0; lz < 160; lz++)
                for (int lx = 0; lx < 160; lx++)
                {
                    float y;
                    byte t;
                    Assert.IsTrue(c.TryTop(lx, lz, out y, out t));
                    Assert.AreEqual(111, t);
                    worst = Math.Max(worst, Math.Abs(y - Surface(lx, lz)));
                }
            Assert.IsTrue(worst < 0.02, "surface error " + worst + " m (density quantisation 1/126)");
            // TryTopAt with SN coordinates
            float yy;
            byte tt;
            double sx = SnWorld.OriginX + bx * 160 + 10.5, sz = SnWorld.OriginZ + bz * 160 + 20.5;
            Assert.IsTrue(c.TryTopAt(sx, sz, out yy, out tt));
            Assert.AreEqual(Surface(10, 20), yy, 0.02);
            Assert.IsFalse(c.TryTopAt(sx + 400, sz, out yy, out tt));
        }

        [TestMethod]
        public void SeabedScan_UniformAndBadFiles()
        {
            const int bx = -10, bz = 40;
            byte[] solid = S8aBatchFile.CreateUniform(bx, 10, bz, 84, 0).Serialize();
            S8aSeabedColumns c = S8aSeabedColumns.Scan(bx, bz, b => b == 12 ? new byte[] { 1, 2, 3 } : (b == 10 ? solid : null));
            Assert.AreEqual(1, c.BadFiles, "the short file of by 12 is refused (served as water)");
            float y;
            byte t;
            Assert.IsTrue(c.TryTop(5, 5, out y, out t));
            Assert.AreEqual(84, t);
            Assert.AreEqual(SnWorld.OriginY + 11 * 160, y, 1e-4, "a far-solid batch under far water: the surface at its top face");
            S8aSeabedColumns none = S8aSeabedColumns.Scan(bx, bz, b => null);
            Assert.AreEqual(0, none.Resolved);
            var pts = new List<S8aSpawnPoint>();
            none.Plan(1, 1, null, pts);
            Assert.AreEqual(0, pts.Count);
        }

        [TestMethod]
        public void Plan_IsDeterministic_MatchesTheTable_AndScales()
        {
            var c = new S8aSeabedColumns(40, 40);
            for (int i = 0; i < c.TopY.Length; i++) c.TopY[i] = -120f;                // band 1 everywhere
            var counts = new double[S8aSpawnTable.Rows.Length];
            int batches = 0;
            for (ulong ck = 1; ck <= 40; ck++)
            {
                var pts = new List<S8aSpawnPoint>();
                c.Plan(ck * 0x9E3779B97F4A7C15UL, 1.0, null, pts);
                var again = new List<S8aSpawnPoint>();
                c.Plan(ck * 0x9E3779B97F4A7C15UL, 1.0, null, again);
                CollectionAssert.AreEqual(pts.Select(p => p.Key).ToList(), again.Select(p => p.Key).ToList(), "deterministic");
                foreach (S8aSpawnPoint p in pts)
                {
                    counts[p.Row]++;
                    Assert.AreEqual(1, p.Band);
                    Assert.IsFalse(S8aBiomes.InCraterBox(p.X, p.Z));
                    Assert.AreEqual(S8aSpawnTable.Rows[p.Row].Flora, p.Y < p.SeabedY);
                }
                batches++;
            }
            double ha = batches * 2.56;
            foreach (S8aSpawnRow r in S8aSpawnTable.Rows)
            {
                if (r.Band != 1) { Assert.AreEqual(0, counts[r.Index], r.TechType); continue; }
                double exp = r.PerHectare * ha;
                Assert.IsTrue(Math.Abs(counts[r.Index] - exp) < 4.5 * Math.Sqrt(exp), r.TechType + " " + counts[r.Index] + " vs " + exp);
            }
            var one = new List<S8aSpawnPoint>();
            c.Plan(77, 1.0, null, one);
            var ten = new List<S8aSpawnPoint>();
            c.Plan(77, 10.0, null, ten);
            double ratio = ten.Count / (double)one.Count;
            Assert.IsTrue(ratio > 8 && ratio < 12, "S8a.M11's density x10: ratio " + ratio);
            var some = new List<S8aSpawnPoint>();
            c.Plan(77, 1.0, new HashSet<int> { 9 }, some);
            Assert.IsFalse(some.Any(p => p.Row == 9), "a disabled row (RedGrass) is skipped");
        }

        [TestMethod]
        public void SpawnBook_VisitsSpawnsDespawnsShiftsAndClears()
        {
            var book = new S8aSpawnBook();
            var c = new S8aSeabedColumns(30, 0);
            for (int i = 0; i < c.TopY.Length; i++) c.TopY[i] = -40f;
            List<ulong> gone = book.SetColumn(c, 0x5eed, 1.0, null);
            Assert.AreEqual(0, gone.Count);
            S8aSpawnBook.Column col;
            Assert.IsTrue(book.TryGet(30, 0, out col));
            Assert.IsTrue(col.Points.Count > 20, "a 2.56 ha column at 21 per ha plans about 54 points: " + col.Points.Count);
            double cx = SnWorld.OriginX + 30 * 160 + 80, cz = SnWorld.OriginZ + 80;
            var spawn = new List<S8aSpawnPoint>();
            var despawn = new List<ulong>();
            book.Pass(cx, -30, cz, 8, 600, spawn, despawn);
            Assert.AreEqual(8, spawn.Count, "the per-pass cap");
            foreach (S8aSpawnPoint p in spawn) book.MarkSpawned(p);
            Assert.AreEqual(8, book.LiveCount);
            Assert.IsTrue(book.VisitedHa[0] > 2.0 && book.VisitedHa[1] == 0, "visited " + book.VisitedHa[0]);
            for (int k = 0; k < 20; k++)
            {
                spawn.Clear();
                book.Pass(cx, -30, cz, 8, 600, spawn, despawn);
                foreach (S8aSpawnPoint p in spawn) book.MarkSpawned(p);
            }
            Assert.AreEqual(col.Points.Count, book.LiveCount, "every point of the column lies within 150 m of its centre");
            Assert.AreEqual(col.Points.Count, book.SpawnedPerBand[0]);
            Assert.AreEqual(0, book.InsideCrater);
            // the live cap counts each refused point once
            var capped = new S8aSpawnBook();
            capped.SetColumn(c, 0x5eed, 1.0, null);
            for (int k = 0; k < 3; k++)
            {
                spawn.Clear();
                capped.Pass(cx, -30, cz, 100, 10, spawn, despawn);
                foreach (S8aSpawnPoint p in spawn) capped.MarkSpawned(p);
            }
            Assert.AreEqual(10, capped.LiveCount);
            Assert.AreEqual(col.Points.Count - 10, capped.Dropped);
            // far away: everything despawns
            spawn.Clear();
            despawn.Clear();
            book.Pass(cx + 1000, -30, cz, 8, 600, spawn, despawn);
            Assert.AreEqual(col.Points.Count, despawn.Count);
            foreach (ulong k in despawn) book.MarkDespawned(k);
            Assert.AreEqual(0, book.LiveCount);
            // a whole-batch chart step: ids and points move by -T, keys stay
            ulong firstKey = col.Points[0].Key;
            double firstX = col.Points[0].X;
            book.Shift(2, -1);
            Assert.IsFalse(book.TryGet(30, 0, out col));
            Assert.IsTrue(book.TryGet(28, 1, out col));
            Assert.AreEqual(firstKey, col.Points[0].Key);
            Assert.AreEqual(firstX - 320, col.Points[0].X, 1e-9);
            // a replaced plan returns the live keys that left it
            spawn.Clear();
            book.Pass(col.Points[0].X, -30, col.Points[0].Z, 4, 600, spawn, despawn);
            foreach (S8aSpawnPoint p in spawn) book.MarkSpawned(p);
            var c2 = new S8aSeabedColumns(28, 1);
            for (int i = 0; i < c2.TopY.Length; i++) c2.TopY[i] = -40f;
            gone = book.SetColumn(c2, 0xbeef, 1.0, null);
            Assert.AreEqual(spawn.Count, gone.Count, "a new chart key re-plans: the old live points go");
            Assert.AreEqual(0, book.Clear().Count);
            Assert.AreEqual(0, book.ColumnCount);
        }
    }
}
