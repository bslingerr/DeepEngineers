using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a Stage E: the Subnautica map's ores (src/DeepEngineers.Wire/S8aCraterOres.cs): the table = the layout's rows, the cube
 /// rule (deterministic, bands, shares, the pocket inside its cube), the deposit file codec, the TechType mapping and the
 /// per-session rule (deposit pockets first, rows off when their material is unresolved).
 /// </summary>
    [TestClass]
    public class S8aCraterOresTests
    {
        private static byte[] Materials() { return new byte[] { 31, 32, 33, 34, 35, 36, 37, 38, 39 }; }

        [TestMethod]
        public void Table_IsTheLayoutsOreRows()
        {
            string root = RepoRoot();
            Assert.IsNotNull(root, "repository root");
            string text = File.ReadAllText(Path.Combine(root, "tools", "planet", "layout_4546b.json"));
            var rows = new List<string>();
            foreach (Match m in Regex.Matches(text, "\\{\"ore\": \"\\w+\"[^\\n]*?\"type\": \"(\\w+)\"[^\\n]*?\"depthM\": \\[(\\d+), (\\d+)\\][^\\n]*?\"share\": ([0-9.]+)"))
                rows.Add(m.Groups[1].Value + " " + m.Groups[2].Value + " " + m.Groups[3].Value + " " + double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture));
            var mine = new List<string>();
            foreach (S8aCraterOreRow r in S8aCraterOres.Rows)
                mine.Add(r.Subtype + " " + r.MinDepthM.ToString(CultureInfo.InvariantCulture) + " " + r.MaxDepthM.ToString(CultureInfo.InvariantCulture) + " " + r.Share.ToString("0.###", CultureInfo.InvariantCulture));
            CollectionAssert.AreEqual(rows, mine, "the crater table is layout_4546b.json ores.rows (ring bands and shares, rarest first)");
        }

        [TestMethod]
        public void CubeRule_DeterministicBandsAndShares()
        {
            var count = new int[S8aCraterOres.Rows.Length];
            int cubes = 0;
            for (long cx = -60; cx < 60; cx++)
                for (long cz = -60; cz < 60; cz++)
                {
                    long cy = -3;                         // centre depth 20 m: the common band only
                    double px, py, pz, r, px2, py2, pz2, r2;
                    int row = S8aCraterOres.Pocket(cx, cy, cz, out px, out py, out pz, out r);
                    int again = S8aCraterOres.Pocket(cx, cy, cz, out px2, out py2, out pz2, out r2);
                    Assert.AreEqual(row, again);
                    Assert.AreEqual(px, px2);
                    cubes++;
                    if (row < 0) continue;
                    count[row]++;
                    Assert.IsTrue(r >= S8aCraterOres.PocketMinM && r <= S8aCraterOres.PocketMaxM);
                    Assert.IsTrue(px - r >= cx * S8aCraterOres.CellM - 1e-9 && px + r <= (cx + 1) * S8aCraterOres.CellM + 1e-9, "the pocket lies inside its cube");
                    Assert.IsTrue(py - r >= cy * S8aCraterOres.CellM - 1e-9 && py + r <= (cy + 1) * S8aCraterOres.CellM + 1e-9);
                    Assert.AreEqual(row, S8aCraterOres.RowAt(px, py, pz), "the pocket's centre is ore");
                }
            for (int i = 0; i < count.Length; i++)
            {
                S8aCraterOreRow o = S8aCraterOres.Rows[i];
                if (20.0 < o.MinDepthM || 20.0 > o.MaxDepthM) { Assert.AreEqual(0, count[i], o.Subtype + " outside its band"); continue; }
                double share = count[i] / (double)cubes;
                Assert.AreEqual(o.Share, share, Math.Max(0.004, o.Share * 0.25), o.Subtype + " share at 20 m");
            }
            Assert.AreEqual(-1, S8aCraterOres.RowAt(3000.0, -20.0, 0.0), "outside the Subnautica map");
        }

        [TestMethod]
        public void Deposits_FileCodecAndTechTypes()
        {
            Assert.AreEqual("Iron_02", S8aCraterOres.OreOfTechType("DrillableTitanium"));
            Assert.AreEqual("Silver_01", S8aCraterOres.OreOfTechType("DrillableSilver"));
            Assert.AreEqual("Platinum_01", S8aCraterOres.OreOfTechType("DrillableKyanite_Large"));
            Assert.AreEqual("Uraninite_01", S8aCraterOres.OreOfTechType("DrillableUraninite"));
            Assert.AreEqual("Uraninite_01", S8aCraterOres.OreOfTechType("DrillableUranium"));   // the real TechType name
            Assert.IsNull(S8aCraterOres.OreOfTechType("Peeper"));
            string line = S8aCraterOres.FormatLine("DrillableGold", 12.345, -101.5, -7.0);
            Assert.AreEqual("DrillableGold\t12.35\t-101.50\t-7.00", line);
            string text = S8aCraterOres.DepositsMagic + "\n" + line + "\n" + line + "\n# note\nbad line\nDrillableQuartz\t-5\t-20\t40\n";
            List<S8aCraterDeposit> list = S8aCraterOres.Parse(text);
            Assert.AreEqual(2, list.Count, "duplicates within 1 m and junk dropped");
            Assert.AreEqual(S8aCraterOres.RowOf("Gold_01"), list[0].Row);
            Assert.AreEqual(S8aCraterOres.RowOf("Silicon_01"), list[1].Row);
            Assert.AreEqual(Path.Combine(S8aStore.StoreFolder("R", 0x1234UL), "crater-deposits.tsv"), S8aCraterOres.DepositsPath("R", 0x1234UL));
        }

        [TestMethod]
        public void Rule_DepositPocketFirstAndUnresolvedRowsOff()
        {
            var rule = new S8aCraterOreRule(Materials());
            var d = new S8aCraterDeposit("DrillableGold", 100.0, -50.0, 200.0);
            rule.SetDeposits(new[] { d, new S8aCraterDeposit("DrillableGold", 5000.0, -50.0, 0.0), new S8aCraterDeposit("Peeper", 0, 0, 0) });
            Assert.AreEqual(1, rule.DepositCount, "outside the map and unmapped TechTypes skipped");
            Assert.AreEqual(1, rule.Deposits.Count);
            byte gold = Materials()[S8aCraterOres.RowOf("Gold_01")];
            Assert.AreEqual(gold, rule.MaterialAt(100.0, -50.0 - S8aCraterOres.DepositSinkM, 200.0), "the pocket under the deposit");
            Assert.AreEqual(gold, rule.MaterialAt(100.0 + S8aCraterOres.DepositRadiusM - 0.2, -50.0 - S8aCraterOres.DepositSinkM, 200.0));
            // a deposit near a bucket edge is found from the neighbouring bucket
            rule.SetDeposits(new[] { new S8aCraterDeposit("DrillableGold", 31.0, -50.0, 31.0) });
            Assert.AreEqual(gold, rule.MaterialAt(35.0, -53.0, 33.0));
            // the cube rule through the session rule; a row whose material is unresolved (255) is off
            long cx = 0, cz = 0, cy = -3;
            double px = 0, py = 0, pz = 0, r;
            int row = -1;
            for (cx = 0; cx < 400 && row < 0; cx++) row = S8aCraterOres.Pocket(cx, cy, cz, out px, out py, out pz, out r);
            Assert.IsTrue(row >= 0, "some cube holds a pocket");
            Assert.AreEqual(Materials()[row], new S8aCraterOreRule(Materials()).MaterialAt(px, py, pz));
            byte[] off = Materials();
            off[row] = 255;
            Assert.AreEqual((byte)255, new S8aCraterOreRule(off).MaterialAt(px, py, pz));
        }

        private static string RepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }
    }
}
