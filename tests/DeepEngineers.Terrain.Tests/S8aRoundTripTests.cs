using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// ROADMAP S8a.C1 (the C# half; the Python half is <c>tools/sn-octrees/sn_octree_write.py --roundtrip N</c>): the

 /// read in place and read-only: at least 100 batch files (12-18-12, 7-17-10 and 14-8-9 always, the rest a seeded
 /// sample) parse → raster → write → bytes identical per octree and per file; the writer-rule census on every parsed
 /// install octree and on every octree written (0 interior nodes off CacheDownsampledType, 0 collapsible nodes,
 /// layout pre-order, counts consumed exactly); the S4 reader (<see cref="SnOctreeSource"/>) reads the written files with
 /// the same samples; S8a.M13 turns both rows red. Writes <c>tests/out/s8a/c1/s8a-c1-cs.json</c> (the launcher's c1 part
 /// and <c>tests/s8a/analyze_s8a.py</c> read it). Inconclusive without the install (DEEPENG_SUBNAUTICA).
 /// Overrides: <c>DEEPENG_S8A_FILES</c> (how many files, default 100), <c>DEEPENG_S8A_SEED</c> (default 4546).
 /// </summary>
    [TestClass]
    public class S8aRoundTripTests
    {
        private static readonly string[] Required = { "12-18-12", "7-17-10", "14-8-9" };

        private static int EnvInt(string name, int def)
        {
            string v = Environment.GetEnvironmentVariable(name);
            int n;
            return !string.IsNullOrEmpty(v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def;
        }

        private static bool BatchId(string name, out int bx, out int by, out int bz)
        {
            bx = by = bz = 0;
            const string pre = "compiled-batch-", suf = ".optoctrees";
            if (!name.StartsWith(pre, StringComparison.Ordinal) || !name.EndsWith(suf, StringComparison.Ordinal)) return false;
            string[] p = name.Substring(pre.Length, name.Length - pre.Length - suf.Length).Split('-');
            return p.Length == 3 && int.TryParse(p[0], out bx) && int.TryParse(p[1], out by) && int.TryParse(p[2], out bz);
        }

        /// <summary>The Python tool's pick: the required three, then a seeded sample of the rest (another generator here: both halves hold on ≥ 100 files, not the same 100).</summary>
        internal static List<string> PickFiles(string dir, int n, int seed)
        {
            var all = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "compiled-batch-*.optoctrees")) all.Add(Path.GetFileName(f));
            all.Sort(StringComparer.Ordinal);
            var req = new List<string>();
            foreach (string k in Required) if (all.Contains("compiled-batch-" + k + ".optoctrees")) req.Add("compiled-batch-" + k + ".optoctrees");
            var rest = new List<string>();
            foreach (string f in all) if (!req.Contains(f)) rest.Add(f);
            var rng = new Random(seed);
            int k2 = Math.Max(0, Math.Min(rest.Count, n - req.Count));
            // partial Fisher-Yates
            for (int i = 0; i < k2; i++)
            {
                int j = i + rng.Next(rest.Count - i);
                string t = rest[i]; rest[i] = rest[j]; rest[j] = t;
            }
            var pick = rest.GetRange(0, k2);
            pick.Sort(StringComparer.Ordinal);
            req.AddRange(pick);
            return req;
        }

        [TestMethod]
        [TestCategory("S8aC1")]
        public void Install_RoundTripOnAtLeast100Files_RulesClean_S4ReaderAgrees_M13Red()
        {
            TestEnv.RequireInstall();
            S8aWireMutants.ClearAll();
            string dir = TestEnv.OctreeDir;
            int n = Math.Max(3, EnvInt("DEEPENG_S8A_FILES", 100));
            int seed = EnvInt("DEEPENG_S8A_SEED", 4546);
            List<string> files = PickFiles(dir, n, seed);
            Assert.IsTrue(files.Count >= Math.Min(n, 100), "only " + files.Count + " batch files found in " + dir);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var install = new S8aRuleStats();
            var written = new S8aRuleStats();
            var enc = new S8aOctreeEncoder();
            var buf = new byte[S8aOctree.MaxBytes];
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            long octrees = 0, identical = 0, different = 0, subdivided = 0, nodes = 0, bytes = 0;
            int filesIdentical = 0, filesDifferent = 0;
            var examples = new List<string>();
            var reserialised = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in files)
            {
                int bx, by, bz;
                Assert.IsTrue(BatchId(name, out bx, out by, out bz), name);
                byte[] file;
                using (var fs = new FileStream(Path.Combine(dir, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    file = new byte[fs.Length];
                    int off = 0;
                    while (off < file.Length) { int r = fs.Read(file, off, file.Length - off); if (r <= 0) break; off += r; }
                }
                bytes += file.Length;
                S8aBatchFile b;
                string err;
                Assert.IsTrue(S8aBatchFile.TryParse(file, file.Length, bx, by, bz, out b, out err), name + ": " + err);
                bool fileOk = true;
                for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
                {
                    if (!b.Has(i)) continue;
                    byte[] a;
                    int off, cnt;
                    b.GetOctree(i, out a, out off, out cnt);
                    S8aOctree.CheckRules(a, off, cnt, install);
                    S8aOctree.DecodeOctree(a, off, cnt, t, d);
                    int wn = enc.Encode(t, d, buf, 0);
                    S8aOctree.CheckRules(buf, 0, wn, written);
                    octrees++;
                    nodes += cnt;
                    if (cnt > 1) subdivided++;
                    bool same = wn == cnt;
                    if (same) for (int k = 0; k < cnt * 4; k++) if (buf[k] != a[off + k]) { same = false; break; }
                    if (same) identical++;
                    else
                    {
                        different++;
                        fileOk = false;
                        if (examples.Count < 5) examples.Add(name + " octree " + i + ": " + cnt + " nodes -> " + wn);
                    }
                }
                byte[] again = b.Serialize();
                bool sameFile = fileOk && again.Length == file.Length;
                if (sameFile) for (int k = 0; k < file.Length; k++) if (again[k] != file[k]) { sameFile = false; break; }
                if (sameFile) filesIdentical++; else filesDifferent++;
                Assert.AreEqual(LayoutHash.Fnv1a64(file), b.Fnv(), name + " Fnv()");
                if (Array.IndexOf(Required, bx + "-" + by + "-" + bz) >= 0) reserialised[name] = again;
            }
            double roundTripS = sw.Elapsed.TotalSeconds;

            // the S4 reader on the written files: the same samples as the install
            string tmp = TestEnv.NewTempDir("s8a-rt");
            long readerSamples = 0, readerDiffs = 0;
            try
            {
                foreach (var kv in reserialised) File.WriteAllBytes(Path.Combine(tmp, kv.Key), kv.Value);
                string e2;
                SnOctreeSource ours = SnOctreeSource.Open(tmp, out e2);
                Assert.IsNotNull(ours, e2);
                try
                {
                    SnOctreeSource theirs = TestEnv.Install;
                    var rng = new Random(seed);
                    foreach (string k in Required)
                    {
                        if (!reserialised.ContainsKey("compiled-batch-" + k + ".optoctrees")) continue;
                        string[] p = k.Split('-');
                        int bx = int.Parse(p[0]), by = int.Parse(p[1]), bz = int.Parse(p[2]);
                        for (int i = 0; i < 5000; i++)
                        {
                            int vx = bx * 160 + rng.Next(160), vy = by * 160 + rng.Next(160), vz = bz * 160 + rng.Next(160);
                            byte t1, d1, t2, d2;
                            int l1, l2;
                            bool g1 = theirs.TrySampleVoxel(vx, vy, vz, out t1, out d1, out l1);
                            bool g2 = ours.TrySampleVoxel(vx, vy, vz, out t2, out d2, out l2);
                            readerSamples++;
                            if (g1 != g2 || t1 != t2 || d1 != d2 || l1 != l2) readerDiffs++;
                        }
                    }
                }
                finally { ours.Release(); }
            }
            finally { TestEnv.DeleteDir(tmp); }

            // S8a.M13: interior nodes = child 0's value; the bytes differ and the census goes red
            int m13Different = 0, m13Checked = 0;
            long m13DownsampleBad = 0;
            {
                string name = "compiled-batch-12-18-12.optoctrees";
                byte[] file = File.ReadAllBytes(Path.Combine(dir, name));
                S8aBatchFile b;
                string err;
                Assert.IsTrue(S8aBatchFile.TryParse(file, file.Length, 12, 18, 12, out b, out err), err);
                S8aWireMutants.M13Child0Interior = true;
                try
                {
                    var bad = new S8aRuleStats();
                    for (int i = 0; i < S8aBatchFile.OctreesPerBatch && m13Checked < 20; i++)
                    {
                        if (!b.Has(i) || b.NodeCount(i) <= 1) continue;
                        byte[] a;
                        int off, cnt;
                        b.GetOctree(i, out a, out off, out cnt);
                        S8aOctree.DecodeOctree(a, off, cnt, t, d);
                        int wn = enc.Encode(t, d, buf, 0);
                        S8aOctree.CheckRules(buf, 0, wn, bad);
                        m13Checked++;
                        bool same = wn == cnt;
                        if (same) for (int k = 0; k < cnt * 4; k++) if (buf[k] != a[off + k]) { same = false; break; }
                        if (!same) m13Different++;
                    }
                    m13DownsampleBad = bad.DownsampleBad;
                }
                finally { S8aWireMutants.ClearAll(); }
            }

            bool rtPass = files.Count >= 100 && filesDifferent == 0 && different == 0;
            bool rulesPass = install.Clean && written.Clean && install.Octrees > 0;
            bool readerPass = readerSamples > 0 && readerDiffs == 0;
            bool m13Pass = m13Checked > 0 && m13Different == m13Checked && m13DownsampleBad > 0;
            string outDir = TestEnv.PathIn("tests", "out", "s8a", "c1");
            Directory.CreateDirectory(outDir);
            var sb = new StringBuilder();
            sb.Append("{\n \"tool\": \"S8aRoundTripTests (C#)\",\n \"dir\": ").Append(Json(dir)).Append(",\n");
            sb.Append(" \"files\": ").Append(files.Count).Append(", \"filesIdentical\": ").Append(filesIdentical).Append(", \"filesDifferent\": ").Append(filesDifferent).Append(",\n");
            sb.Append(" \"octrees\": ").Append(octrees).Append(", \"octreesIdentical\": ").Append(identical).Append(", \"octreesDifferent\": ").Append(different)
              .Append(", \"subdivided\": ").Append(subdivided).Append(", \"nodes\": ").Append(nodes).Append(", \"bytes\": ").Append(bytes).Append(",\n");
            sb.Append(" \"rules\": ").Append(StatsJson(install)).Append(",\n \"rulesWritten\": ").Append(StatsJson(written)).Append(",\n");
            sb.Append(" \"reader\": {\"samples\": ").Append(readerSamples).Append(", \"diffs\": ").Append(readerDiffs).Append("},\n");
            sb.Append(" \"m13\": {\"checked\": ").Append(m13Checked).Append(", \"different\": ").Append(m13Different).Append(", \"downsampleBad\": ").Append(m13DownsampleBad).Append(", \"red\": ").Append(m13Pass ? "true" : "false").Append("},\n");
            sb.Append(" \"seconds\": ").Append(roundTripS.ToString("0.0", CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append(" \"rows\": {\"S8a.C1.roundtrip\": ").Append(Json(rtPass ? "PASS" : "FAIL")).Append(", \"S8a.C1.rules\": ").Append(Json(rulesPass ? "PASS" : "FAIL"))
              .Append(", \"S8a.C1.reader\": ").Append(Json(readerPass ? "PASS" : "FAIL")).Append(", \"S8a.M13.red\": ").Append(Json(m13Pass ? "PASS" : "FAIL")).Append("},\n");
            sb.Append(" \"examples\": [");
            for (int i = 0; i < examples.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(Json(examples[i])); }
            sb.Append("]\n}\n");
            File.WriteAllText(Path.Combine(outDir, "s8a-c1-cs.json"), sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("S8a.C1 C#: files " + files.Count + " identical " + filesIdentical + " different " + filesDifferent + " | octrees " + octrees +
                              " identical " + identical + " different " + different + " (subdivided " + subdivided + ", nodes " + nodes + ") | " +
                              roundTripS.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            Console.WriteLine("S8a.C1 rules (install): " + install);
            Console.WriteLine("S8a.C1 rules (written): " + written);
            Console.WriteLine("S8a.C1 reader: samples " + readerSamples + " diffs " + readerDiffs + "; M13 checked " + m13Checked + " different " + m13Different + " downsampleBad " + m13DownsampleBad);
            foreach (string ex in examples) Console.WriteLine("  example " + ex);
            Assert.IsTrue(rtPass, "S8a.C1.roundtrip: files " + filesDifferent + " different, octrees " + different + " different");
            Assert.IsTrue(rulesPass, "S8a.C1.rules: install " + install + "; written " + written);
            Assert.IsTrue(readerPass, "the S4 reader disagrees on " + readerDiffs + " of " + readerSamples + " samples");
            Assert.IsTrue(m13Pass, "S8a.M13 did not turn the rows red");
        }

        private static string StatsJson(S8aRuleStats s)
        {
            return "{\"octrees\": " + s.Octrees + ", \"empty\": " + s.EmptyOctrees + ", \"interior\": " + s.Interior + ", \"downsampleOk\": " + s.DownsampleOk +
                   ", \"downsampleBad\": " + s.DownsampleBad + ", \"collapsible\": " + s.Collapsible + ", \"layoutBad\": " + s.LayoutBad + ", \"countBad\": " + s.CountBad + "}";
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        [TestMethod]
        public void PickFiles_RequiredFirstThenASeededSample()
        {
            string tmp = TestEnv.NewTempDir("s8a-pick");
            try
            {
                for (int i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(tmp, "compiled-batch-" + i + "-18-" + (i % 3) + ".optoctrees"), new byte[4]);
                File.WriteAllBytes(Path.Combine(tmp, "compiled-batch-12-18-12.optoctrees"), new byte[4]);
                File.WriteAllBytes(Path.Combine(tmp, "compiled-batch-7-17-10.optoctrees"), new byte[4]);
                List<string> a = PickFiles(tmp, 10, 1), b = PickFiles(tmp, 10, 1), c = PickFiles(tmp, 10, 2);
                Assert.AreEqual(10, a.Count);
                Assert.AreEqual("compiled-batch-12-18-12.optoctrees", a[0]);
                Assert.AreEqual("compiled-batch-7-17-10.optoctrees", a[1]);
                CollectionAssert.AreEqual(a, b, "seeded");
                CollectionAssert.AreNotEqual(a, c);
                Assert.AreEqual(32, PickFiles(tmp, 1000, 1).Count, "capped at the folder");
            }
            finally { TestEnv.DeleteDir(tmp); }
        }
    }
}
