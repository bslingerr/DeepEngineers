using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>

 /// numbers of the ROADMAP S7t T5 row, the haze colours both sides use, and the Python twin in tests/s7t/band_compare.py
 /// (its <c>TARGETS</c> table must equal this class, so the oracle and the plugins never drift apart).
 /// </summary>
    [TestClass]
    public class S7tBandTargetsTests
    {
        [TestMethod]
        public void Values_AreTheOwnersPair()
        {
            Assert.AreEqual(new BandRgb(135, 221, 235), S7tBandTargets.SeSkyTop);
            Assert.AreEqual(new BandRgb(123, 176, 234), S7tBandTargets.SnSkyTop);
            Assert.AreEqual(new BandRgb(153, 234, 246), S7tBandTargets.SeSkyHorizon);
            Assert.AreEqual(new BandRgb(197, 241, 255), S7tBandTargets.SnSkyHorizon);
            Assert.AreEqual(new BandRgb(38, 72, 96), S7tBandTargets.SeFarWater);
            Assert.AreEqual(new BandRgb(129, 155, 187), S7tBandTargets.SnFarWater);
            Assert.AreEqual(new BandRgb(27, 68, 104), S7tBandTargets.SeNearWater);
            Assert.AreEqual(new BandRgb(65, 97, 122), S7tBandTargets.SnNearWater);
            Assert.AreEqual(S7tBandTargets.SnSkyHorizon, S7tBandTargets.HazeHorizon, "the haze at the horizon is Subnautica's sky at the horizon");
            Assert.AreEqual(S7tBandTargets.SnFarWater, S7tBandTargets.HazeLow, "the skirt's low colour is Subnautica's far water");
            Assert.AreEqual(1.0, S7tBandTargets.HazeHorizon.Unit(2), 1e-12);
            Assert.AreEqual(197 / 255.0, S7tBandTargets.HazeHorizon.Unit(0), 1e-12);
            Assert.AreEqual("197,241,255", S7tBandTargets.HazeHorizon.ToString());
            BandRgb c;
            Assert.IsFalse(S7tBandTargets.TryGet("xx", "sky_top", out c));
            Assert.IsFalse(S7tBandTargets.TryGet("sn", "nope", out c));
        }

        [TestMethod]
        public void PythonTwin_BandCompareTargets_EqualThisClassWhenPresent()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string py = null;
            for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); up++)
            {
                string candidate = System.IO.Path.Combine(dir, "tests", "s7t", "band_compare.py");
                if (System.IO.File.Exists(candidate)) { py = candidate; break; }
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            if (py == null) Assert.Inconclusive("tests/s7t/band_compare.py not found above " + AppDomain.CurrentDomain.BaseDirectory);
            string text = System.IO.File.ReadAllText(py, Encoding.UTF8).Replace("\r\n", "\n");
            int seen = 0;
            foreach (string side in new[] { "se", "sn" })
            {
                Match line = Regex.Match(text, "^\\s*\"" + side + "\": \\{(?<body>[^}]*)\\}", RegexOptions.Multiline);
                Assert.IsTrue(line.Success, "TARGETS[\"" + side + "\"] in band_compare.py");
                foreach (string band in S7tBandTargets.BandNames)
                {
                    Match m = Regex.Match(line.Groups["body"].Value, "\"" + band + "\": \\((?<r>\\d+), (?<g>\\d+), (?<b>\\d+)\\)");
                    Assert.IsTrue(m.Success, side + "." + band + " in band_compare.py TARGETS");
                    var py3 = new BandRgb(byte.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture), byte.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture),
                                          byte.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture));
                    BandRgb cs;
                    Assert.IsTrue(S7tBandTargets.TryGet(side, band, out cs));
                    Assert.AreEqual(cs, py3, side + "." + band + ": band_compare.py TARGETS = S7tBandTargets");
                    seen++;
                }
            }
            Assert.AreEqual(8, seen);
        }
    }
}
