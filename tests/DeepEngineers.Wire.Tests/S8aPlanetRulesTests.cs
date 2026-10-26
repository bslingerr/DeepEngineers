using System.Collections.Generic;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>Planet v2 R8: the layout's runtime rules (de-planet-rules.txt) and the crater's ore density behind them.</summary>
    [TestClass]
    public class S8aPlanetRulesTests
    {
        [TestMethod]
        public void Rules_RoundTrip_Defaults_AndTheStoreFold()
        {
            string text = S8aPlanetRules.Format(false, 4.0);
            S8aPlanetRules r;
            string err;
            Assert.IsTrue(S8aPlanetRules.TryParse(Encoding.UTF8.GetBytes(text), out r, out err), err);
            Assert.IsFalse(r.CraterCubeRule);
            Assert.AreEqual(4.0, r.CraterDepositsPerKm2);
            Assert.IsFalse(r.IsDefault);
            Assert.AreNotEqual(0UL, r.Hash);
            Assert.AreNotEqual(0x1234UL, r.FoldInto(0x1234UL), "a rules file changes the store's sculpt part");
            S8aPlanetRules d = S8aPlanetRules.Defaults();
            Assert.IsTrue(d.IsDefault && d.CraterCubeRule && d.CraterDepositsPerKm2 == 0);
            Assert.AreEqual(0x1234UL, d.FoldInto(0x1234UL), "no file (design 3): the key is today's");
            Assert.IsFalse(S8aPlanetRules.TryParse(Encoding.UTF8.GetBytes("other 1\ncrater.cubeRule=false\n"), out r, out err));
            Assert.IsTrue(r.IsDefault, "a bad file keeps the defaults");
            Assert.IsFalse(S8aPlanetRules.TryParse(Encoding.UTF8.GetBytes(S8aPlanetRules.Magic + "\ncrater.cubeRule=maybe\n"), out r, out err));
            Assert.IsTrue(S8aPlanetRules.TryParse(Encoding.UTF8.GetBytes(S8aPlanetRules.Magic + "\r\n# note\r\nfuture.key=1\r\n"), out r, out err), err);
            Assert.IsTrue(r.CraterCubeRule && r.Entries.Count == 1, "unknown keys kept, defaults stay");
        }

        [TestMethod]
        public void CubeRuleOff_OnlyThePockets()
        {
            var mat = new byte[S8aCraterOres.Rows.Length];
            for (int i = 0; i < mat.Length; i++) mat[i] = (byte)(10 + i);
            var rule = new S8aCraterOreRule(mat);
            // a cube-rule pocket somewhere in the map (scan for one)
            double hx = double.NaN, hy = 0, hz = 0;
            for (int k = 0; k < 20000 && double.IsNaN(hx); k++)
            {
                double x = -1500 + (k % 150) * 20.0, z = -1500 + (k / 150) * 20.0, y = -60.0;
                if (rule.RowAt(x, y, z) >= 0) { hx = x; hy = y; hz = z; }
            }
            Assert.IsFalse(double.IsNaN(hx), "the cube rule draws pockets at 0-200 m");
            rule.CubeRule = false;
            Assert.AreEqual(-1, rule.RowAt(hx, hy, hz), "off: no cube pocket");
            rule.SetDeposits(new List<S8aCraterDeposit> { new S8aCraterDeposit("DrillableTitanium", hx, hy + 3.0, hz) });
            Assert.AreEqual(S8aCraterOres.RowOf("Iron_02"), rule.RowAt(hx, hy, hz), "a deposit's pocket stays");
        }

        [TestMethod]
        public void DepositCap_PerKm2Cell_Deterministic_OrderFree()
        {
            var list = new List<S8aCraterDeposit>();
            for (int i = 0; i < 30; i++) list.Add(new S8aCraterDeposit("DrillableTitanium", 100.0 + 25.0 * i, -50.0, 200.0 + 13.0 * (i % 7)));   // x 100-825: one cell
            for (int i = 0; i < 3; i++) list.Add(new S8aCraterDeposit("DrillableGold", 1200.0 + 50.0 * i, -350.0, -900.0));                     // another cell: 3
            list.Add(new S8aCraterDeposit("DrillableTitanium", 9000.0, -50.0, 0.0));                                                               // outside the map
            List<S8aCraterDeposit> kept = S8aCraterOreRule.Cap(list, 4.0);
            Assert.AreEqual(4 + 3, kept.Count, "4 of the 30 in the first cell, all 3 in the second, none outside the map");
            var reversed = new List<S8aCraterDeposit>(list);
            reversed.Reverse();
            List<S8aCraterDeposit> kept2 = S8aCraterOreRule.Cap(reversed, 4.0);
            var a = new HashSet<long>();
            foreach (S8aCraterDeposit d in kept) a.Add(S8aCraterOres.PosKey(d.X, d.Y, d.Z));
            foreach (S8aCraterDeposit d in kept2) Assert.IsTrue(a.Contains(S8aCraterOres.PosKey(d.X, d.Y, d.Z)), "the same deposits whatever the file's order");
            Assert.AreEqual(33, S8aCraterOreRule.Cap(list, 0).Count, "no cap: every mapped deposit");
            var mat = new byte[S8aCraterOres.Rows.Length];
            var rule = new S8aCraterOreRule(mat) { DepositsPerKm2 = 4.0 };
            rule.SetDeposits(list);
            Assert.AreEqual(7, rule.DepositCount);
            Assert.AreEqual(26, rule.Capped);
            Assert.AreEqual(7, rule.Deposits.Count);
        }
    }
}
