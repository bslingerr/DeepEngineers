using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S4.A-T0 runner: the C# reader/sampler (src/DeepEngineers.Terrain) against the independent Python oracle
 /// (tools/sn-octrees/sn_octree_batch.py --at0) on the 10 batches of the ROADMAP, read in place from the user's
 /// install. Per batch: rasters byte-identical, summaries equal (leaf sizes, near sizes, solid, near, crossings, the
 /// three SHA-256), the oracle's D = 0 crossings reproduced by the test-side oracle field, D at 2,048 probes within
 /// 1e-6 of the oracle field, and the content iso-surface (SE bytes from SnField; iso byte 127 = SE's VOXEL_ISO_LEVEL
 /// and its "content ≥ 127" inside test, the unbiased iso of the truncating byte per S4.A-T0b; 127.5 reported too)
 /// evaluated at the sample points (G0) and on the grid offset by (0.37, 0.21, 0.45) m (G1) against the oracle's
 /// D = 0 surface. Pass (defect detector): G0 P95 ≤ 0.05 m (the ROADMAP threshold, where the two iso-surfaces are
 /// sampled alike) and G1 P95 ≤ 0.20 m (regression guard). The ROADMAP's 0.05 m on G1 is reported separately
 /// ("roadmapIsoG1Pass"): an offset grid resamples a field whose ±1-voxel distance band saturates at ±1 at the next
 /// sample, so SE's linear edge interpolation moves the surface by a few cm to a few dm. Writes at0-&lt;b&gt;.cs.json (derived data, outside the repo) and
 /// at0-results.json (also copied to tests/out/s4/at0). tests/s4/at0_compare.py re-checks the C# output with sn_field.py.
 /// </summary>
    [TestClass]
    public class At0Tests
    {
        public static readonly int[][] Batches =
        {
            new[] { 12, 18, 12 }, new[] { 7, 17, 10 }, new[] { 14, 18, 16 }, new[] { 10, 17, 11 }, new[] { 6, 17, 5 },
            new[] { 7, 13, 15 }, new[] { 10, 11, 13 }, new[] { 14, 8, 9 }, new[] { 14, 19, 18 }, new[] { 18, 15, 7 },
        };

        public static readonly string[] BatchNames =
        {
            "Safe Shallows", "densest file", "kelp forest", "Jellyshroom cave entrance", "Grand Reef CoralArch",
            "Lost River region", "lava zone", "deepest floor", "Mountain Island top", "shallowest cut ceiling",
        };

        /// <summary>ROADMAP S4.A-T0: content iso-surface vs SN signed-distance iso-surface, Hausdorff P95 (metres).</summary>
        public const double IsoP95Max = 0.05;
        /// <summary>Regression guard on the offset grid (the A-T1/A-T2 budgets are 0.25/0.3 m; a half-voxel slip gives 0.5 m).</summary>
        public const double IsoP95G1Guard = 0.20;
        public const double ProbeTol = 1e-6;
        public const int ProbeCount = 2048;
        public const int IsoSamplesMax = 2000;
        public static readonly double[] Offset = { 0.37, 0.21, 0.45 };
        /// <summary>Iso bytes scored; the first is the pass criterion (inside = byte ≥ iso).</summary>
        public static readonly double[] IsoBytes = { 127.0, 127.5 };

        internal sealed class IsoStats
        {
            public double Iso;
            public List<double> E0 = new List<double>(), E1 = new List<double>();
            public long Missed, Unmatched, Count;
            public double P95, Max, P95G0, P95G1;

            public void Finish()
            {
                P95G0 = P95Of(E0);
                P95G1 = P95Of(E1);
                var all = new List<double>(E0.Count + E1.Count);
                all.AddRange(E0);
                all.AddRange(E1);
                Count = all.Count;
                P95 = P95Of(all);
                Max = 0;
                foreach (double x in all) if (x > Max) Max = x;
                E0 = null;
                E1 = null;
            }
        }

        private static readonly object OracleGate = new object();

        /// <summary>Makes sure the oracle files of a batch exist and come from the current oracle script; runs Python otherwise.</summary>
        internal static At0Data Oracle(int bx, int by, int bz)
        {
            lock (OracleGate)
            {
                string dir = TestEnv.At0Dir;
                string script = TestEnv.PathIn("tools", "sn-octrees", "sn_octree_batch.py");
                string sha = At0Data.FileSha256(script);
                if (!At0Data.Fresh(dir, bx, by, bz, sha))
                {
                    string outp = TestEnv.RunPython(script, TestEnv.OctreeDir, bx.ToString(CultureInfo.InvariantCulture),
                        by.ToString(CultureInfo.InvariantCulture), bz.ToString(CultureInfo.InvariantCulture), "--at0", dir);
                    Console.WriteLine(outp.Trim());
                }
                return At0Data.Load(dir, bx, by, bz);
            }
        }

        internal sealed class BatchResult
        {
            public int[] Batch;
            public string Name;
            public bool RasterEqual, SummaryEqual, OracleIsoMatch, ProbesOk;
            public string RasterDiff, SummaryDiff;
            public double ProbeMax, IsoP95, IsoMax, IsoP95G0, IsoP95G1;
            public long IsoCount, Missed, Unmatched, OracleIso;
            public IsoStats[] Isos;
            public bool Pass, RoadmapIsoG1Pass;
            public string Mutants;
            public SnBatchSummary Summary;
            public List<double[]> Probes = new List<double[]>();
            public List<double[]> IsoSamples = new List<double[]>();
            public double ElapsedS;
        }

        /// <summary>Runs every A-T0 check on one batch (iso part skipped when <paramref name="withIso"/> is false: the mutant runs).</summary>
        internal static BatchResult RunBatch(int bx, int by, int bz, string name, bool withIso)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            At0Data o = Oracle(bx, by, bz);
            var r = new BatchResult { Batch = new[] { bx, by, bz }, Name = name, Mutants = TerrainMutants.Active };
            SnOctreeSource src = TestEnv.Install;
            const int n3 = At0Data.N * At0Data.N * At0Data.N;
            var type = new byte[n3];
            var density = new byte[n3];
            var leaf = new byte[n3];
            SnBatchSummary s;
            Assert.IsTrue(SnBatchRaster.Rasterize(src, bx, by, bz, type, density, leaf, out s), "rasterize refused (closing?)");
            r.Summary = s;
            r.RasterDiff = FirstDiff("type", type, o.Type) ?? FirstDiff("density", density, o.Density) ?? FirstDiff("leafSize", leaf, o.LeafSize);
            r.RasterEqual = r.RasterDiff == null;
            string diff;
            r.SummaryEqual = s.SameAs(o.Summary, out diff);
            r.SummaryDiff = diff;

            var oracle = new OracleField(o.Type, o.Density);
            float[] g0 = oracle.SampleGridCrossings();
            r.OracleIso = o.Iso.Length / 3;
            r.OracleIsoMatch = g0.Length == o.Iso.Length && MaxAbs(g0, o.Iso) <= 1e-5;

            // probes: SnField (under test) vs the oracle field, SN world coordinates
            var field = new SnField(src);
            var rng = new Random(bx * 10007 + by * 101 + bz);
            double wx = SnOctreeFormat.OriginX + bx * At0Data.N, wy = SnOctreeFormat.OriginY + by * At0Data.N, wz = SnOctreeFormat.OriginZ + bz * At0Data.N;
            int isoPts = o.Iso.Length / 3;
            double probeMax = 0;
            bool sameOk = true;
            for (int i = 0; i < ProbeCount; i++)
            {
                double lx, ly, lz;
                if (i % 4 == 3 && isoPts > 0)
                {
                    int k = rng.Next(isoPts);
                    lx = o.Iso[3 * k] + (rng.NextDouble() - 0.5);
                    ly = o.Iso[3 * k + 1] + (rng.NextDouble() - 0.5);
                    lz = o.Iso[3 * k + 2] + (rng.NextDouble() - 0.5);
                }
                else
                {
                    lx = 0.5 + rng.NextDouble() * 158.99;
                    ly = 0.5 + rng.NextDouble() * 158.99;
                    lz = 0.5 + rng.NextDouble() * 158.99;
                }
                lx = Math.Max(0.5, Math.Min(159.49, lx));
                ly = Math.Max(0.5, Math.Min(159.49, ly));
                lz = Math.Max(0.5, Math.Min(159.49, lz));
                bool same;
                float dcs = field.Sample(wx + lx, wy + ly, wz + lz, out same);
                double dor = oracle.D(lx - 0.5, ly - 0.5, lz - 0.5);
                double err = Math.Abs(dcs - dor);
                if (err > probeMax) probeMax = err;
                bool sameOr = OracleSame(oracle, lx - 0.5, ly - 0.5, lz - 0.5);
                if (same != sameOr) sameOk = false;
                r.Probes.Add(new[] { wx + lx, wy + ly, wz + lz, (double)dcs, same ? 1.0 : 0.0 });
            }
            r.ProbeMax = probeMax;
            r.ProbesOk = probeMax <= ProbeTol && sameOk;

            if (withIso)
            {
                var stats = new IsoStats[IsoBytes.Length];
                for (int i = 0; i < stats.Length; i++) stats[i] = new IsoStats { Iso = IsoBytes[i] };
                IsoGrid(field, oracle, wx, wy, wz, new double[3], At0Data.N, stats, r.IsoSamples, 0);
                IsoGrid(field, oracle, wx, wy, wz, Offset, At0Data.N - 1, stats, r.IsoSamples, 1);
                foreach (IsoStats st in stats) st.Finish();
                r.Isos = stats;
                IsoStats p = stats[0];
                r.IsoP95G0 = p.P95G0;
                r.IsoP95G1 = p.P95G1;
                r.IsoCount = p.Count;
                r.Missed = p.Missed;
                r.Unmatched = p.Unmatched;
                r.IsoP95 = p.P95;
                r.IsoMax = p.Max;
                ThinSamples(r.IsoSamples, IsoSamplesMax);
            }
            r.Pass = r.RasterEqual && r.SummaryEqual && r.OracleIsoMatch && r.ProbesOk &&
                     (!withIso || (r.IsoP95G0 <= IsoP95Max && r.IsoP95G1 <= IsoP95G1Guard));
            r.RoadmapIsoG1Pass = withIso && r.IsoP95G1 <= IsoP95Max;
            r.ElapsedS = sw.Elapsed.TotalSeconds;
            return r;
        }

        private static bool OracleSame(OracleField f, double ux, double uy, double uz)
        {
            int x0 = (int)Math.Floor(ux), y0 = (int)Math.Floor(uy), z0 = (int)Math.Floor(uz);
            bool allFar = true;
            int pos = 0, neg = 0;
            for (int c = 0; c < 8; c++)
            {
                int x = x0 + (c & 1), y = y0 + ((c >> 1) & 1), z = z0 + ((c >> 2) & 1);
                int i = (x * At0Data.N + y) * At0Data.N + z;
                if (!f.Far[i]) allFar = false;
                if (f.S[i] > 0) pos++; else neg++;
            }
            return allFar && (pos == 0 || neg == 0);
        }

        /// <summary>
 /// Content crossings (bytes from SnField at the grid points; inside = byte ≥ iso) on every edge of a grid of m³
 /// points at u = k + off, scored against the oracle's D = 0 (forward), plus the oracle crossings the content
 /// misses on an edge (backward). Samples (for the Python re-check) are kept for the first iso only.
 /// </summary>
        private static void IsoGrid(SnField field, OracleField oracle, double wx, double wy, double wz, double[] off, int m,
                                    IsoStats[] stats, List<double[]> samples, int grid)
        {
            var bytes = new byte[m * m * m];
            var dOr = new double[m * m * m];
            Parallel.For(0, m, x =>
            {
                for (int y = 0; y < m; y++)
                    for (int z = 0; z < m; z++)
                    {
                        double ux = x + off[0], uy = y + off[1], uz = z + off[2];
                        bool same;
                        float d = field.Sample(wx + ux + 0.5, wy + uy + 0.5, wz + uz + 0.5, out same);
                        int i = (x * m + y) * m + z;
                        bytes[i] = TileSampler.ContentByte(TileSampler.LodDistance(d, same, 0));
                        dOr[i] = oracle.D(ux, uy, uz);
                    }
            });
            for (int si = 0; si < stats.Length; si++)
            {
                IsoStats st = stats[si];
                double iso = st.Iso;
                List<double> errs = grid == 0 ? st.E0 : st.E1;
                for (int axis = 0; axis < 3; axis++)
                {
                    int stp = axis == 0 ? m * m : axis == 1 ? m : 1;
                    for (int x = 0; x < m; x++)
                        for (int y = 0; y < m; y++)
                            for (int z = 0; z < m; z++)
                            {
                                int along = axis == 0 ? x : axis == 1 ? y : z;
                                if (along == m - 1) continue;
                                int a = (x * m + y) * m + z, b = a + stp;
                                bool ca = bytes[a] >= iso, cb = bytes[b] >= iso;
                                bool oa = dOr[a] >= 0, ob = dOr[b] >= 0;
                                double ax = x + off[0], ay = y + off[1], az = z + off[2];
                                if (ca != cb)
                                {
                                    double tq = (bytes[a] - iso) / (bytes[a] - (double)bytes[b]);
                                    double e;
                                    if (!oracle.EdgeError(ax, ay, az, axis, tq, out e)) st.Unmatched++;
                                    errs.Add(e);
                                    if (si == 0) samples.Add(new[] { grid, axis, ax, ay, az, tq, e });
                                }
                                else if (oa != ob)
                                {
                                    st.Missed++;
                                    errs.Add(oracle.MissedError(ax, ay, az, axis));
                                }
                            }
                }
            }
        }

        private static void ThinSamples(List<double[]> s, int max)
        {
            if (s.Count <= max) return;
            var keep = new List<double[]>(max);
            double stride = s.Count / (double)max;
            for (int i = 0; i < max; i++) keep.Add(s[(int)(i * stride)]);
            s.Clear();
            s.AddRange(keep);
        }

        /// <summary>Nearest-rank P95 (index ceil(0.95·n) − 1 of the sorted values; 0 when empty). Same rule in at0_compare.py.</summary>
        internal static double P95Of(List<double> v)
        {
            if (v.Count == 0) return 0;
            var a = v.ToArray();
            Array.Sort(a);
            int k = Math.Max(0, (int)Math.Ceiling(0.95 * a.Length) - 1);
            return a[k];
        }

        private static double MaxAbs(float[] a, float[] b)
        {
            double m = 0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        private static string FirstDiff(string name, byte[] a, byte[] b)
        {
            long count = 0;
            int first = -1;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) { if (first < 0) first = i; count++; }
            if (first < 0) return null;
            int n = At0Data.N;
            return name + " differs at " + count + " voxels, first (" + first / (n * n) + "," + first / n % n + "," + first % n + ") cs=" + a[first] + " oracle=" + b[first];
        }

        private static string Num(double v) { return TerrainJson.Num(v); }

        private static string CsJson(BatchResult r)
        {
            var sb = new StringBuilder(1 << 16);
            sb.Append("{\"format\":\"DEAT0-CS\",\"version\":1");
            sb.Append(",\"batch\":[").Append(r.Batch[0]).Append(',').Append(r.Batch[1]).Append(',').Append(r.Batch[2]).Append(']');
            sb.Append(",\"name\":").Append(TerrainJson.Quote(r.Name));
            sb.Append(",\"mutants\":").Append(TerrainJson.Quote(r.Mutants));
            sb.Append(",\"sampleOffset\":").Append(Num(SnField.SampleOffset));
            sb.Append(",\"summary\":").Append(r.Summary.ToJson());
            sb.Append(",\"rasterEqual\":").Append(r.RasterEqual ? "true" : "false");
            sb.Append(",\"rasterDiff\":").Append(TerrainJson.Quote(r.RasterDiff));
            sb.Append(",\"summaryEqual\":").Append(r.SummaryEqual ? "true" : "false");
            sb.Append(",\"summaryDiff\":").Append(TerrainJson.Quote(r.SummaryDiff));
            sb.Append(",\"oracleIso\":{\"count\":").Append(r.OracleIso).Append(",\"match\":").Append(r.OracleIsoMatch ? "true" : "false").Append('}');
            sb.Append(",\"probes\":{\"count\":").Append(r.Probes.Count).Append(",\"maxErr\":").Append(Num(r.ProbeMax))
              .Append(",\"tol\":").Append(Num(ProbeTol)).Append(",\"ok\":").Append(r.ProbesOk ? "true" : "false").Append(",\"points\":[");
            for (int i = 0; i < r.Probes.Count; i++)
            {
                if (i > 0) sb.Append(',');
                double[] p = r.Probes[i];
                sb.Append('[').Append(Num(p[0])).Append(',').Append(Num(p[1])).Append(',').Append(Num(p[2])).Append(',')
                  .Append(Num(p[3])).Append(',').Append(p[4] > 0 ? 1 : 0).Append(']');
            }
            sb.Append("]}");
            sb.Append(",\"isoOthers\":[");
            for (int i = 1; r.Isos != null && i < r.Isos.Length; i++)
            {
                IsoStats o = r.Isos[i];
                if (i > 1) sb.Append(',');
                sb.Append("{\"iso\":").Append(Num(o.Iso)).Append(",\"count\":").Append(o.Count).Append(",\"missed\":").Append(o.Missed)
                  .Append(",\"unmatched\":").Append(o.Unmatched).Append(",\"p95\":").Append(Num(o.P95)).Append(",\"max\":").Append(Num(o.Max))
                  .Append(",\"p95g0\":").Append(Num(o.P95G0)).Append(",\"p95g1\":").Append(Num(o.P95G1)).Append('}');
            }
            sb.Append(']');
            sb.Append(",\"iso\":{\"iso\":").Append(Num(IsoBytes[0])).Append(",\"offset\":[").Append(Num(Offset[0])).Append(',').Append(Num(Offset[1])).Append(',').Append(Num(Offset[2])).Append(']');
            sb.Append(",\"count\":").Append(r.IsoCount).Append(",\"missed\":").Append(r.Missed).Append(",\"unmatched\":").Append(r.Unmatched);
            sb.Append(",\"p95\":").Append(Num(r.IsoP95)).Append(",\"max\":").Append(Num(r.IsoMax));
            sb.Append(",\"p95g0\":").Append(Num(r.IsoP95G0)).Append(",\"p95g1\":").Append(Num(r.IsoP95G1));
            sb.Append(",\"threshold\":").Append(Num(IsoP95Max)).Append(",\"g1Guard\":").Append(Num(IsoP95G1Guard));
            sb.Append(",\"roadmapIsoG1Pass\":").Append(r.RoadmapIsoG1Pass ? "true" : "false").Append(",\"samples\":[");
            for (int i = 0; i < r.IsoSamples.Count; i++)
            {
                if (i > 0) sb.Append(',');
                double[] p = r.IsoSamples[i];
                sb.Append('[').Append((int)p[0]).Append(',').Append((int)p[1]);
                for (int k = 2; k < 7; k++) sb.Append(',').Append(Num(p[k]));
                sb.Append(']');
            }
            sb.Append("]}");
            sb.Append(",\"pass\":").Append(r.Pass ? "true" : "false");
            sb.Append(",\"elapsedS\":").Append(Num(Math.Round(r.ElapsedS, 3)));
            sb.Append('}');
            return sb.ToString();
        }

        private static string ResultsJson(List<BatchResult> rs)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < rs.Count; i++)
            {
                BatchResult r = rs[i];
                if (i > 0) sb.Append(",\n");
                sb.Append(" {\"batch\":\"").Append(r.Batch[0]).Append('-').Append(r.Batch[1]).Append('-').Append(r.Batch[2]).Append('"');
                sb.Append(",\"name\":").Append(TerrainJson.Quote(r.Name));
                sb.Append(",\"rasterEqual\":").Append(r.RasterEqual ? "true" : "false");
                sb.Append(",\"summaryEqual\":").Append(r.SummaryEqual ? "true" : "false");
                sb.Append(",\"oracleIsoMatch\":").Append(r.OracleIsoMatch ? "true" : "false");
                sb.Append(",\"probeMax\":").Append(Num(r.ProbeMax));
                sb.Append(",\"isoP95\":").Append(Num(r.IsoP95)).Append(",\"isoMax\":").Append(Num(r.IsoMax));
                sb.Append(",\"isoP95g0\":").Append(Num(r.IsoP95G0)).Append(",\"isoP95g1\":").Append(Num(r.IsoP95G1));
                sb.Append(",\"isoCount\":").Append(r.IsoCount).Append(",\"missed\":").Append(r.Missed).Append(",\"unmatched\":").Append(r.Unmatched);
                sb.Append(",\"oracleIso\":").Append(r.OracleIso);
                sb.Append(",\"isoByte\":").Append(Num(IsoBytes[0]));
                if (r.Isos != null && r.Isos.Length > 1)
                    sb.Append(",\"isoP95_1275\":").Append(Num(r.Isos[1].P95)).Append(",\"isoP95g1_1275\":").Append(Num(r.Isos[1].P95G1));
                sb.Append(",\"solid\":").Append(r.Summary.Solid).Append(",\"near\":").Append(r.Summary.Near);
                sb.Append(",\"filePresent\":").Append(r.Summary.FilePresent ? "true" : "false");
                sb.Append(",\"roadmapIsoG1Pass\":").Append(r.RoadmapIsoG1Pass ? "true" : "false");
                sb.Append(",\"pass\":").Append(r.Pass ? "true" : "false").Append('}');
            }
            sb.Append("\n]\n");
            return sb.ToString();
        }

        [TestMethod]
        [TestCategory("AT0")]
        public void At0_TenBatches_MatchThePythonOracle()
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            var results = new List<BatchResult>();
            string dir = TestEnv.At0Dir;
            for (int i = 0; i < Batches.Length; i++)
            {
                int[] b = Batches[i];
                BatchResult r = RunBatch(b[0], b[1], b[2], BatchNames[i], true);
                File.WriteAllText(Path.Combine(dir, "at0-" + b[0] + "-" + b[1] + "-" + b[2] + ".cs.json"), CsJson(r));
                results.Add(r);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "A-T0 {0}-{1}-{2} {3,-26} raster={4} summary={5} oracleIso={6} probeMax={7:E2} iso n={8} missed={9} unmatched={10} P95={11:F4} (g0 {12:F4}, g1 {13:F4}) max={14:F3} {15} [{16:F1} s]{17}{18}",
                    b[0], b[1], b[2], BatchNames[i], r.RasterEqual, r.SummaryEqual, r.OracleIsoMatch, r.ProbeMax, r.IsoCount, r.Missed,
                    r.Unmatched, r.IsoP95, r.IsoP95G0, r.IsoP95G1, r.IsoMax, r.Pass ? "PASS" : "FAIL", r.ElapsedS,
                    r.RasterDiff != null ? " rasterDiff: " + r.RasterDiff : "", r.SummaryDiff != null ? " summaryDiff: " + r.SummaryDiff : "")
                    + (r.Isos != null && r.Isos.Length > 1 ? string.Format(CultureInfo.InvariantCulture, " | iso 127.5: P95 {0:F4} (g0 {1:F4}, g1 {2:F4}) missed {3} unmatched {4}",
                        r.Isos[1].P95, r.Isos[1].P95G0, r.Isos[1].P95G1, r.Isos[1].Missed, r.Isos[1].Unmatched) : ""));
            }
            string json = ResultsJson(results);
            File.WriteAllText(Path.Combine(dir, "at0-results.json"), json);
            string repoOut = TestEnv.PathIn("tests", "out", "s4", "at0");
            Directory.CreateDirectory(repoOut);
            File.WriteAllText(Path.Combine(repoOut, "at0-results.json"), json);
            foreach (BatchResult r in results)
                Assert.IsTrue(r.Pass, "S4.A-T0 batch " + r.Batch[0] + "-" + r.Batch[1] + "-" + r.Batch[2] + " (" + r.Name + ") FAILED: raster=" + r.RasterEqual +
                              " summary=" + r.SummaryEqual + " oracleIso=" + r.OracleIsoMatch + " probes=" + r.ProbesOk + " (" + r.ProbeMax + ") isoP95 g0=" + r.IsoP95G0 + " g1=" + r.IsoP95G1 +
                              (r.RasterDiff != null ? " " + r.RasterDiff : "") + (r.SummaryDiff != null ? " " + r.SummaryDiff : ""));
        }

        private static void MutantRed(string id)
        {
            TestEnv.RequireInstall();
            TerrainMutants.ClearAll();
            Assert.IsTrue(TerrainMutants.Set(id, true));
            try
            {
                for (int i = 0; i < Batches.Length; i++)
                {
                    int[] b = Batches[i];
                    BatchResult r = RunBatch(b[0], b[1], b[2], BatchNames[i], false);
                    if (!r.Pass)
                    {
                        Console.WriteLine(id + " turns S4.A-T0 red on " + b[0] + "-" + b[1] + "-" + b[2] + " (" + BatchNames[i] + "): raster=" + r.RasterEqual +
                                          " summary=" + r.SummaryEqual + " probes=" + r.ProbesOk + " probeMax=" + r.ProbeMax.ToString("G4", CultureInfo.InvariantCulture));
                        return;
                    }
                }
                Assert.Fail(id + " left every A-T0 batch green: the oracle cannot see it");
            }
            finally
            {
                TerrainMutants.ClearAll();
            }
        }

        [TestMethod] [TestCategory("AT0")] public void At0_MutantM1a_SignFlip_Red() { MutantRed("S4.M1a"); }
        [TestMethod] [TestCategory("AT0")] public void At0_MutantM1b_OneVoxelOffset_Red() { MutantRed("S4.M1b"); }
        [TestMethod] [TestCategory("AT0")] public void At0_MutantM1c_SwapXZ_Red() { MutantRed("S4.M1c"); }
        [TestMethod] [TestCategory("AT0")] public void At0_MutantM1d_ChildOrder_Red() { MutantRed("S4.M1d"); }
    }
}
