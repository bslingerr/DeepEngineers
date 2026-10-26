using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The S16 vector files (written by tools/s16/s16_ref.py --vectors).</summary>
    internal static class S16Vectors
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<string[]> Lines(string name)
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s16", "vectors", name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/s16/s16_ref.py --vectors)");
            var rows = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                rows.Add(raw.Split('\t'));
            }
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        public static double D(string s)
        {
            switch (s)
            {
                case "inf": return double.PositiveInfinity;
                case "-inf": return double.NegativeInfinity;
                case "nan": return double.NaN;
            }
            return double.Parse(s, NumberStyles.Float, Inv);
        }

        public static int I(string s) { return int.Parse(s, Inv); }

        public static void Near(double want, double got, string what, double rel = 1e-12)
        {
            double tol = rel * Math.Max(1.0, Math.Abs(want));
            Assert.IsTrue(Math.Abs(want - got) <= tol, what + ": want " + want.ToString("R", Inv) + " got " + got.ToString("R", Inv));
        }

        /// <summary>The synthetic grid of an exposure row: blocks "minx,y,z:maxx,y,z:class:tier:functional;..." and airtight cells.</summary>
        public static S16GridSummary Summarise(int size, string blocksText, string airText)
        {
            var blocks = new List<int[]>();
            var occ = new Dictionary<S16Cell, int>();
            foreach (string part in blocksText.Split(';'))
            {
                if (part.Length == 0) continue;
                string[] f = part.Split(':');
                string[] a = f[0].Split(','), b = f[1].Split(',');
                var v = new[] { I(a[0]), I(a[1]), I(a[2]), I(b[0]), I(b[1]), I(b[2]), I(f[2]), I(f[3]), I(f[4]) };
                blocks.Add(v);
                for (int x = v[0]; x <= v[3]; x++)
                    for (int y = v[1]; y <= v[4]; y++)
                        for (int z = v[2]; z <= v[5]; z++) occ[new S16Cell(x, y, z)] = v[6];
            }
            var air = new HashSet<S16Cell>();
            if (airText != "-")
                foreach (string c in airText.Split(';'))
                {
                    string[] p = c.Split(',');
                    air.Add(new S16Cell(I(p[0]), I(p[1]), I(p[2])));
                }
            var s = new S16GridSummary { Size = size, Blocks = blocks.Count };
            foreach (int[] v in blocks)
            {
                if (v[7] > 0 && v[8] != 0) s.Tier = Math.Max(s.Tier, v[7]);
                int ln, hn;
                int e = S16Exposure.Of(new S16Cell(v[0], v[1], v[2]), new S16Cell(v[3], v[4], v[5]),
                                       (x, y, z) => { int c; return occ.TryGetValue(new S16Cell(x, y, z), out c) ? c : 0; },
                                       (x, y, z) => air.Contains(new S16Cell(x, y, z)), v[6] == S16Conventions.BlockClass.Fitting, out ln, out hn);
                if ((e & S16Exposure.Wet) != 0) s.Wet++;
                if (!S16Exposure.Counts(v[6], e)) continue;
                if (S16Exposure.RatedClass(v[6], ln, hn) == S16Conventions.BlockClass.HeavyArmor) s.Heavy++;
                else s.Light++;
                if (v[6] == S16Conventions.BlockClass.Fitting) s.Fitting++;
            }
            return s;
        }
    }

    /// <summary>
 /// S16 conventions pin (CONTRACT §5.4 "S16 conventions"): the canonical text equals the fenced block of
 /// docs/CONTRACT.md byte for byte, its FNV-1a 64 is the pinned hash, the C5 extension lies inside layout 1.0's pad and
 /// the earlier pins are unchanged; de.s16 is dev-only.
 /// </summary>
    [TestClass]
    public class S16ConventionsTests
    {
        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string t = S16Conventions.CanonicalText();
            Assert.AreEqual(S16Conventions.Header + "\n", t.Substring(0, S16Conventions.Header.Length + 1));
            int lines = 0;
            foreach (char c in t) { if (c == '\n') lines++; Assert.IsTrue(c < 128, "ASCII only"); }
            Assert.AreEqual(S16Conventions.CanonicalLines, lines, "lines");
            Assert.AreEqual(S16Conventions.CanonicalBytes, Encoding.ASCII.GetByteCount(t), "bytes");
            Assert.AreEqual(S16Conventions.PinnedHash, S16Conventions.Hash(), "hash 0x" + S16Conventions.Hash().ToString("x16"));
        }

        [TestMethod]
        public void CanonicalText_EqualsTheContractBlock()
        {
            string block = S8aVectors.ContractBlock(S16Conventions.Header);
            if (block == null) Assert.Inconclusive("docs/CONTRACT.md has no S16 block above the test output");
            string text = S16Conventions.CanonicalText();
            if (block != text)
            {
                string[] a = block.Split('\n'), b = text.Split('\n');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    string x = i < a.Length ? a[i] : "<end>", y = i < b.Length ? b[i] : "<end>";
                    if (x != y) Assert.Fail("line " + (i + 1) + " differs:\n CONTRACT: " + x + "\n Wire:     " + y);
                }
            }
        }

        [TestMethod]
        public void C5Extension_InsideThePad_AndEarlierPinsUnchanged()
        {
            Assert.AreEqual(256, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SeState)));
            Assert.IsTrue(S16Conventions.C5.Offset >= 0x80 && S16Conventions.C5.Offset + S16Conventions.C5.Size <= 0x100, "inside SeState.pad_80 (0x80..0xFF)");
            Assert.AreEqual(0x2A, S16Conventions.C5.Size);
            Assert.AreEqual(256, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SnStatus)));
            Assert.IsTrue(S16Conventions.C7.Offset >= 0x8C + 0x30 && S16Conventions.C7.Offset + S16Conventions.C7.Size <= 0x100,
                          "the C7 block inside SnStatus.pad_8c after the S8a block 0x8C-0xBB");
            string[] c7last = S16Conventions.C7Fields[S16Conventions.C7Fields.Length - 1];
            Assert.AreEqual(S16Conventions.C7.Offset + S16Conventions.C7.Size, Convert.ToInt32(c7last[1], 16) + int.Parse(c7last[2], CultureInfo.InvariantCulture));
            string[] last = S16Conventions.C5Fields[S16Conventions.C5Fields.Length - 1];
            Assert.AreEqual(S16Conventions.C5.Offset + S16Conventions.C5.Size,
                            Convert.ToInt32(last[1], 16) + int.Parse(last[2], CultureInfo.InvariantCulture), "the field table fills the extension");
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged");
            Assert.AreEqual(0xb6ee880712f8253bUL, CombatConventions.PinnedHash, "S6 pin unchanged");
            Assert.AreEqual(S11Conventions.PinnedHash, S11Conventions.Hash(), "S11 pin unchanged");
            Assert.AreEqual(SwimThresholds.UnsafeDepth, (double)S16Conventions.Diver.UnsafeDepthM, "the diver's 100 m (S2b)");
            Assert.AreEqual(SwimThresholds.CrushDepth, (double)S16Conventions.Diver.CrushDepthM, "the diver's 200 m (S2b)");
        }

        [TestMethod]
        public void Release_DeS16IsDevOnly()
        {
            var args = new Dictionary<string, object> { { "damage", "off" } };
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s16", args), "release: de.s16 is dev-only");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s16", null));
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s16", args));
            Assert.IsFalse(UserCommandRules.ChatAllowed(false, new[] { "s16", "status" }), "release chat: /de s16 refused");
        }

        [TestMethod]
        public void Ladder_AsTheOwnerWroteIt()
        {

            Assert.AreEqual(350, S16Ladder.HullCrushM(S16Conventions.Size.Small, S16Conventions.BlockClass.LightArmor), "light small");
            Assert.AreEqual(500, S16Ladder.HullCrushM(S16Conventions.Size.Small, S16Conventions.BlockClass.HeavyArmor));
            Assert.AreEqual(800, S16Ladder.HullCrushM(S16Conventions.Size.Large, S16Conventions.BlockClass.LightArmor), "light large");
            Assert.AreEqual(1200, S16Ladder.HullCrushM(S16Conventions.Size.Large, S16Conventions.BlockClass.HeavyArmor));
            Assert.AreEqual(100.0, S16Damage.PerSecond(290.0, 200.0), 1e-12, "cap 100 hp/s from 90 m past");
            Assert.AreEqual(60.0, S16Damage.PerSecond(250.0, 200.0), 1e-12, "10 + 1.0 (d - L)");
            Assert.AreEqual(8, S16Conventions.Damage.MaxBlocksPerPeriod, "8 blocks a period");
            Assert.AreEqual(20000, S16Conventions.Voice.CooldownMs);
            Assert.AreEqual(20000, S16Conventions.Voice.RepeatMs);

            // (the planet's floor cap), no Mk3: the 3,000 m void under the Crater Edge is scenery
            Assert.AreEqual(2, S16Conventions.TierTable.Length, "two tiers");
            Assert.AreEqual(1400, S16Ladder.TierCrushM(1));
            Assert.AreEqual(2000, S16Ladder.TierCrushM(2));
            Assert.AreEqual(0, S16Ladder.TierCrushM(3), "no third tier");
            Assert.AreEqual("Cobalt,Magnesium,Silver", S16Conventions.TierTable[0].Ingots);
            Assert.AreEqual("Gold,Platinum,Uranium", S16Conventions.TierTable[1].Ingots);
        }
    }

    /// <summary>S16 rules against the Python twin's vectors (tests/s16/vectors/*.tsv) plus hand checks.</summary>
    [TestClass]
    public class S16RulesTests
    {
        [TestMethod]
        public void Classify_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("classify.tsv"))
            {
                string edge = f[2] == "-" ? null : f[2];
                Assert.AreEqual(S16Vectors.I(f[3]), S16Classify.ClassOf(f[0], f[1], edge), "class of " + string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[4]), S16Classify.TierOf(f[1]), "tier of " + f[1]);
            }
            Assert.AreEqual(S16Conventions.Size.Small, S16Classify.TierBlockSize("DE_PressureHullMk1Small"));
            Assert.AreEqual(S16Conventions.Size.Large, S16Classify.TierBlockSize("DE_PressureHullMk2Large"));
            Assert.AreEqual(0, S16Classify.TierBlockSize("DE_PressureHullMk3Large"), "no Mk3 since depth 11");
            Assert.AreEqual(0, S16Classify.TierBlockSize("LargeBlockArmorBlock"));
        }

        [TestMethod]
        public void Exposure_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("exposure.tsv"))
            {
                S16GridSummary s = S16Vectors.Summarise(S16Vectors.I(f[1]), f[2], f[3]);
                string what = f[0];
                Assert.AreEqual(S16Vectors.I(f[4]), s.Light, what + " light");
                Assert.AreEqual(S16Vectors.I(f[5]), s.Heavy, what + " heavy");
                Assert.AreEqual(S16Vectors.I(f[6]), s.Fitting, what + " fitting");
                Assert.AreEqual(S16Vectors.I(f[7]), s.Tier, what + " tier");
                Assert.AreEqual(S16Vectors.I(f[8]), s.Blocks, what + " blocks");
                int weakest;
                Assert.AreEqual(S16Vectors.I(f[9]), S16Ladder.HullOf(s, out weakest), what + " hull");
                Assert.AreEqual(S16Vectors.I(f[10]), weakest, what + " weakest");
                Assert.AreEqual(S16Vectors.I(f[11]), S16Ladder.CrushDepthOf(s), what + " crush");
                Assert.AreEqual(S16Vectors.I(f[12]), s.Wet, what + " wet");
                Assert.AreEqual(S16Vectors.I(f[13]) != 0, S16Ladder.DamageSetIsWet(s), what + " damage set");
            }
        }

        [TestMethod]
        public void Crush_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("crush.tsv"))
            {
                var s = new S16GridSummary { Size = S16Vectors.I(f[0]), Light = S16Vectors.I(f[1]), Heavy = S16Vectors.I(f[2]), Fitting = S16Vectors.I(f[3]), Tier = S16Vectors.I(f[4]) };
                int weakest;
                Assert.AreEqual(S16Vectors.I(f[5]), S16Ladder.HullOf(s, out weakest), string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[6]), weakest, string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[7]), S16Ladder.CrushDepthOf(s), string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[8]), S16Ladder.HullCode(s.Size, weakest), string.Join(" ", f));
            }
        }

        [TestMethod]
        public void Damage_Creak_Hud_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("damage.tsv"))
            {
                double d = S16Vectors.D(f[0]), limit = S16Vectors.D(f[1]);
                int size = S16Vectors.I(f[2]);
                if (double.IsInfinity(limit))
                {
                    Assert.AreEqual(S16Vectors.I(f[6]), S16Hud.State(d, limit, true), "state " + f[0]);
                    Assert.AreEqual(S16Vectors.I(f[7]), S16Hud.BreathX10(d), "breath " + f[0]);
                    continue;
                }
                S16Vectors.Near(S16Vectors.D(f[3]), S16Damage.PerSecond(d, limit), "hp/s " + string.Join(" ", f));
                S16Vectors.Near(S16Vectors.D(f[4]), S16Damage.PerPeriodSe(d, limit, size), "per period " + string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[5]), S16Creak.Level(d, limit), "level " + string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[6]), S16Hud.State(d, limit, true), "state on " + string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[7]), S16Hud.State(d, limit, false), "state off " + string.Join(" ", f));
            }
            foreach (string[] f in S16Vectors.Lines("pick.tsv"))
            {
                var depths = new List<double>();
                foreach (string x in f[0].Split(',')) depths.Add(S16Vectors.D(x));
                string got = string.Join(",", S16Damage.Pick(depths, S16Vectors.D(f[1])));
                Assert.AreEqual(f[2] == "-" ? "" : f[2], got, "pick " + f[0]);
            }
        }

        [TestMethod]
        public void Group_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("group.tsv"))
            {
                Assert.AreEqual(S16Vectors.I(f[5]), S16Ladder.EffectiveCrush(S16Vectors.I(f[0]), S16Vectors.I(f[1]), f[2] == "1", S16Vectors.I(f[3])), string.Join(" ", f));
                Assert.AreEqual(f[6] == "1", S16Ladder.DamageApplies(f[2] == "1", f[4] == "1"), string.Join(" ", f));
            }
        }

        [TestMethod]
        public void Hud_Vectors()
        {
            foreach (string[] f in S16Vectors.Lines("hud.tsv"))
            {
                switch (f[0])
                {
                    case "mode":
                        Assert.AreEqual(S16Vectors.I(f[5]), S16Hud.SnHudMode(f[1] == "1", S16Vectors.I(f[2]), S16Vectors.I(f[3]), (uint)S16Vectors.I(f[4])), string.Join(" ", f));
                        break;
                    case "suppress":
                        Assert.AreEqual(f[5] == "1", S16Hud.SeLineSuppressed(f[1] == "1", S16Vectors.I(f[2]), f[3] == "1"), string.Join(" ", f));
                        break;
                    case "px":
                    {
                        string[] res = f[2].Split(','), want = f[5].Split(',');
                        int sw = S16Vectors.I(res[0]), sh = S16Vectors.I(res[1]);
                        double scale = S16Hud.CanvasScale(sw, sh, 1.0), w = sw / scale, h = sh / scale, x0, y0, x1, y1, dx, dy, px0, py0, px1, py1;
                        int used, backdrops;
                        List<double> rects = null;
                        foreach (string[] g in S16Vectors.Lines("hud.tsv")) if (g[0] == "bounds" && g[1] == f[1]) rects = Rects(g[2]);
                        Assert.IsNotNull(rects, "px " + f[1]);
                        S16Vectors.Near(S16Vectors.D(f[3]), scale, "scale " + string.Join(" ", f));
                        Assert.IsTrue(S16Hud.ReadoutBounds(rects, w, h, out x0, out y0, out x1, out y1, out used, out backdrops));
                        S16Hud.Place(S16Hud.PlaceTopCentre, x0, y0, x1, y1, w, h, out dx, out dy);
                        S16Hud.ToScreenPx(x0 + dx, y0 + dy, x1 + dx, y1 + dy, w, h, scale, out px0, out py0, out px1, out py1);
                        S16Vectors.Near(S16Vectors.D(want[0]), px0, "x0 " + string.Join(" ", f), 1e-9);
                        S16Vectors.Near(S16Vectors.D(want[1]), py0, "y0 " + string.Join(" ", f), 1e-9);
                        S16Vectors.Near(S16Vectors.D(want[2]), px1, "x1 " + string.Join(" ", f), 1e-9);
                        S16Vectors.Near(S16Vectors.D(want[3]), py1, "y1 " + string.Join(" ", f), 1e-9);
                        break;
                    }
                    case "tone":
                        Assert.AreEqual(S16Vectors.I(f[5]), S16Hud.Tone(S16Vectors.I(f[1])), "tone " + string.Join(" ", f));
                        break;
                    case "seated":
                        Assert.AreEqual(f[5], S16Hud.SeatedText(S16Vectors.I(f[1]), S16Vectors.I(f[2]), f[3] == "-" ? "" : f[3]), "seated " + string.Join(" ", f));
                        break;
                    case "centre":
                        Assert.AreEqual(f[5] == "1", S16Hud.SeatedCentred(S16Vectors.D(f[1]), S16Vectors.D(f[2])), "centre " + string.Join(" ", f));
                        break;
                    case "font":
                        S16Vectors.Near(S16Vectors.D(f[5]), S16Hud.SeatedFontSize(S16Vectors.D(f[1]), S16Vectors.D(f[2]), S16Vectors.D(f[3])), "font " + string.Join(" ", f));
                        break;
                    case "fit":
                        Assert.AreEqual(f[5] == "1", S16Hud.SeatedTextFits(S16Vectors.I(f[1]), S16Vectors.D(f[2]), S16Vectors.D(f[3])), "fit " + string.Join(" ", f));
                        break;
                    case "seline":
                        Assert.AreEqual(S16Vectors.I(f[5]), S16Hud.SeLine(f[1] == "1", S16Vectors.I(f[2]), f[3] == "1"), "seline " + string.Join(" ", f));
                        break;
                    case "pct":
                        Assert.AreEqual(S16Vectors.I(f[3]), S16Hud.HullPct(S16Vectors.D(f[1]), S16Vectors.D(f[2])), "hull " + string.Join(" ", f));
                        Assert.AreEqual(S16Vectors.I(f[4]), S16Hud.PowerPct(S16Vectors.D(f[1]), S16Vectors.D(f[2])), "power " + string.Join(" ", f));
                        break;
                    case "scale":
                        S16Vectors.Near(S16Vectors.D(f[5]), S16Hud.CanvasScale(S16Vectors.I(f[1]), S16Vectors.I(f[2]), S16Vectors.D(f[3])), "scale " + string.Join(" ", f));
                        break;
                    case "place":
                    {
                        string[] b = f[2].Split(','), c = f[3].Split(','), want = f[5].Split(',');
                        double dx, dy;
                        S16Hud.Place(S16Vectors.I(f[1]), S16Vectors.D(b[0]), S16Vectors.D(b[1]), S16Vectors.D(b[2]), S16Vectors.D(b[3]), S16Vectors.D(c[0]), S16Vectors.D(c[1]), out dx, out dy);
                        S16Vectors.Near(S16Vectors.D(want[0]), dx, "dx " + string.Join(" ", f));
                        S16Vectors.Near(S16Vectors.D(want[1]), dy, "dy " + string.Join(" ", f));
                        break;
                    }
                    case "mix":
                        Assert.AreEqual(S16Vectors.D(f[5]), S16VoiceMix.EffectVolume((float)S16Vectors.D(f[1]), S16Vectors.D(f[2])), 1e-6, "mix " + string.Join(" ", f));
                        break;
                    case "gate":
                        Assert.AreEqual(S16Vectors.I(f[5]), S16DamageGateRule.Of(f[1] == "1", f[2] == "1", f[3] == "1"), "gate " + string.Join(" ", f));
                        break;
                    case "bounds":
                    {
                        var rects = Rects(f[2]);
                        string[] c = f[3].Split(','), want = f[5].Split(',');
                        double x0, y0, x1, y1;
                        int used, backdrops;
                        Assert.IsTrue(S16Hud.ReadoutBounds(rects, S16Vectors.D(c[0]), S16Vectors.D(c[1]), out x0, out y0, out x1, out y1, out used, out backdrops), f[1]);
                        S16Vectors.Near(S16Vectors.D(want[0]), x0, "x0 " + f[1]);
                        S16Vectors.Near(S16Vectors.D(want[1]), y0, "y0 " + f[1]);
                        S16Vectors.Near(S16Vectors.D(want[2]), x1, "x1 " + f[1]);
                        S16Vectors.Near(S16Vectors.D(want[3]), y1, "y1 " + f[1]);
                        Assert.AreEqual(S16Vectors.I(want[4]), used, "used " + f[1]);
                        Assert.AreEqual(S16Vectors.I(want[5]), backdrops, "backdrops " + f[1]);
                        break;
                    }
                    default:
                        Assert.Fail("unknown kind " + f[0]);
                        break;
                }
            }
        }

        private static List<double> Rects(string s)
        {
            var l = new List<double>();
            foreach (string q in s.Split(';'))
                foreach (string x in q.Split(',')) l.Add(S16Vectors.D(x));
            return l;
        }

        /// <summary>
 /// Both modes and the Seamoth panel (the twin's
 /// READOUTS: a 3,776 x 2,084 backdrop in each element, the vehicle readout and the panel below the screen) end on
 /// the canvas at their anchors; with the backdrop counted (the depth 3 rule) the seated readout stays off screen.
 /// </summary>
        [TestMethod]
        public void Place_BothModes_BothResolutions_NeverClipped()
        {

            // with the longer "m / crush m"; at 2560x1440 and 1920x1080 it is top-centre 6 % below the top edge, the backdrop left out
            int rows = 0;
            foreach (string[] f in S16Vectors.Lines("hud.tsv"))
            {
                if (f[0] != "bounds") continue;
                foreach (int[] res in new[] { new[] { 2560, 1440 }, new[] { 1920, 1080 } })
                {
                    rows++;
                    double scale = S16Hud.CanvasScale(res[0], res[1], 1.0), w = res[0] / scale, h = res[1] / scale;
                    var rects = Rects(f[2]);
                    double x0, y0, x1, y1, dx, dy, px0, py0, px1, py1;
                    int used, backdrops;
                    Assert.IsTrue(S16Hud.ReadoutBounds(rects, w, h, out x0, out y0, out x1, out y1, out used, out backdrops));
                    Assert.AreEqual(1, backdrops, f[1] + ": the backdrop left out");
                    S16Hud.Place(S16Hud.PlaceTopCentre, x0, y0, x1, y1, w, h, out dx, out dy);
                    S16Hud.ToScreenPx(x0 + dx, y0 + dy, x1 + dx, y1 + dy, w, h, scale, out px0, out py0, out px1, out py1);
                    string what = f[1] + " at " + res[0] + "x" + res[1] + ": (" + px0 + ", " + py0 + ")-(" + px1 + ", " + py1 + ")";
                    Assert.IsTrue(px0 >= 0 && py0 >= 0 && px1 <= res[0] && py1 <= res[1], what + ": never clipped");
                    Assert.AreEqual(res[1] * S16Conventions.Hud.TopPermille / 1000.0, py0, 1e-6, what + ": 6 % below the top edge");
                    Assert.AreEqual(res[0] / 2.0, (px0 + px1) / 2, 1e-6, what + ": centred");
                }
            }
            Assert.AreEqual(4, rows, "diver and seated, two resolutions");
            Assert.IsFalse(S16Hud.ReadoutBounds(new double[] { -1888, -1042, 1888, 1042 }, 1920, 1080, out _, out _, out _, out _, out _, out _), "a backdrop alone is no readout");
        }

        [TestMethod]
        public void Readout_SeatedLikeTheCyclops_TonesByState()
        {

            Assert.AreEqual("10 m / 120 m", S16Hud.SeatedText(10, 120, "m"), "seated: one field (CyclopsHelmHUDManager.cs:117 with spaces)");
            Assert.AreEqual("0 m / 120 m", S16Hud.SeatedText(0, 120, ""), "no language suffix yet: m");
            Assert.IsTrue(S16Hud.SeatedTextFits(1, 140, 156));
            Assert.IsFalse(S16Hud.SeatedTextFits(2, 120, 156), "a wrapped readout (the 22:47 deploy) fails");
            Assert.IsFalse(S16Hud.SeatedTextFits(1, 176.2, 156), "the 00:22 row: 176 of 156 units fails");
            Assert.IsFalse(S16Hud.SeatedTextFits(1, 155.9, 156), "the 00:22 row: 155.9 of 156 (no margin) fails");
            // depth 8: the short form and the measured font (the 00:22 session: compass 156 units, own font 56, 176.2 units for 15 characters at 22.4)
            Assert.AreEqual("1603 / 1700 m", S16Hud.SeatedText(1603, 1700, "m"));
            Assert.AreEqual("12 m", S16Hud.SeatedText(12, 0, "m"), "a station (no crush depth): the depth only");
            Assert.IsTrue(S16Hud.SeatedCentred(7.8, 156), "within 5 % of the compass width");
            Assert.IsFalse(S16Hud.SeatedCentred(-20, 156), "left of the arc (before CentreOnCompass) is red");
            Assert.AreEqual("51 m / 120 m", S16Hud.SeatedText(51, 120, "m"));
            double charW = 176.2 / 15 / 22.4, target = S16Hud.SeatedTarget(156);
            foreach (var dc in new[] { new[] { 51, 120 }, new[] { 1603, 1700 }, new[] { 1760, 1700 }, new[] { 9999, 1700 } })
            {
                string text = S16Hud.SeatedText(dc[0], dc[1], "m");
                double size = S16Hud.SeatedFontSize(56, text.Length * charW * 56, target), width = text.Length * charW * size;
                Assert.IsTrue(S16Hud.SeatedTextFits(1, width, 156), text + " at " + size + ": " + width + " of 156");
                Assert.IsTrue(size >= 56 * S16Conventions.Hud.MinFontPermille / 1000.0, text + ": above the floor");
            }
            Assert.AreEqual(S16Conventions.Tone.Normal, S16Hud.Tone(S16Conventions.HudState.Safe));
            Assert.AreEqual(S16Conventions.Tone.Caution, S16Hud.Tone(S16Conventions.HudState.Creak));
            Assert.AreEqual(S16Conventions.Tone.Danger, S16Hud.Tone(S16Conventions.HudState.Limit));
            Assert.AreEqual(S16Conventions.Tone.Danger, S16Hud.Tone(S16Conventions.HudState.Damage));
            Assert.AreEqual(0xFFC040, S16Conventions.Hud.CautionRgb);
        }

        [TestMethod]
        public void DamageGate_DestructibleBlocks_AnyGameMode()
        {
            Assert.AreEqual(S16Conventions.DamageGate.Destructible, S16DamageGateRule.Of(true, true, true), "Creative or Survival, destructible blocks: damage");
            Assert.AreEqual(S16Conventions.DamageGate.Indestructible, S16DamageGateRule.Of(true, false, true));
            Assert.AreEqual(S16Conventions.DamageGate.Off, S16DamageGateRule.Of(true, true, false), "de.s16 damage off overrides");
            Assert.AreEqual(S16Conventions.DamageGate.None, S16DamageGateRule.Of(false, true, true));
            Assert.IsTrue(S16DamageGateRule.Open(S16Conventions.DamageGate.Destructible));
            Assert.IsFalse(S16DamageGateRule.Open(S16Conventions.DamageGate.Indestructible));
            Assert.AreEqual("indestructible", S16Conventions.DamageGate.Name(2));
        }

        [TestMethod]
        public void TopK_EqualsPick()
        {
            var rnd = new Random(16);
            var k = new S16TopK();
            for (int trial = 0; trial < 500; trial++)
            {
                int n = rnd.Next(0, 40);
                var depths = new List<double>();
                for (int i = 0; i < n; i++) depths.Add(Math.Round(150 + rnd.NextDouble() * 100, rnd.Next(0, 2))); // rounding makes ties
                double limit = 200;
                k.Reset(limit);
                for (int i = 0; i < n; i++) k.Offer(depths[i], i);
                List<int> want = S16Damage.Pick(depths, limit);
                Assert.AreEqual(want.Count, k.Count, "count, trial " + trial);
                for (int i = 0; i < want.Count; i++) Assert.AreEqual(want[i], k.Index(i), "index " + i + ", trial " + trial);
            }
            foreach (string[] f in S16Vectors.Lines("pick.tsv"))
            {
                k.Reset(S16Vectors.D(f[1]));
                string[] ds = f[0].Split(',');
                for (int i = 0; i < ds.Length; i++) k.Offer(S16Vectors.D(ds[i]), i);
                var got = new List<string>();
                for (int i = 0; i < k.Count; i++) got.Add(k.Index(i).ToString(CultureInfo.InvariantCulture));
                Assert.AreEqual(f[2] == "-" ? "" : f[2], string.Join(",", got), "pick.tsv " + f[0]);
            }
        }

        [TestMethod]
        public void RuleFunctions_ByTheirBriefNames()
        {
            foreach (string[] f in S16Vectors.Lines("crush.tsv"))
            {
                var s = new S16GridSummary { Size = S16Vectors.I(f[0]), Light = S16Vectors.I(f[1]), Heavy = S16Vectors.I(f[2]), Fitting = S16Vectors.I(f[3]), Tier = S16Vectors.I(f[4]) };
                Assert.AreEqual(S16Vectors.I(f[7]), S16Rule.CrushDepthOf(s), string.Join(" ", f));
            }
            foreach (string[] f in S16Vectors.Lines("damage.tsv"))
            {
                double d = S16Vectors.D(f[0]), limit = S16Vectors.D(f[1]);
                if (double.IsInfinity(limit)) continue;
                S16Vectors.Near(S16Vectors.D(f[3]), S16Rule.DamagePerSecond(d, limit), "hp/s " + string.Join(" ", f));
                Assert.AreEqual(S16Vectors.I(f[5]), S16Rule.CreakLevel(d, limit), "level " + string.Join(" ", f));
            }
        }

        [TestMethod]
        public void CreakCadence_ReplaysTheTwin()
        {
            // the twin's three sequences, regenerated here from the same recipes; the vector file lists the creak ticks
            var want = new Dictionary<string, List<string>>();
            foreach (string[] f in S16Vectors.Lines("creak.tsv"))
            {
                List<string> l;
                if (!want.TryGetValue(f[0], out l)) want[f[0]] = l = new List<string>();
                l.Add(f[1] + "/" + f[4] + "/" + f[5]);
            }
            var seqs = new Dictionary<string, KeyValuePair<double, List<KeyValuePair<int, double>>>>();
            var a = new List<KeyValuePair<int, double>>();
            for (int t = 0; t < 60 * 30; t++) a.Add(new KeyValuePair<int, double>(t, 120.0 + t * (5.0 / 60.0)));
            seqs["descent"] = new KeyValuePair<double, List<KeyValuePair<int, double>>>(200.0, a);
            var b = new List<KeyValuePair<int, double>>();
            for (int t = 0; t < 600; t++) b.Add(new KeyValuePair<int, double>(t, 160.0 + 0.5 * t / 60.0));
            for (int t = 0; t < 900; t++) b.Add(new KeyValuePair<int, double>(600 + t, 165.0 - t / 60.0));
            for (int t = 0; t < 1800; t++) b.Add(new KeyValuePair<int, double>(1500 + t, 150.0 + t / 30.0));
            seqs["bounce"] = new KeyValuePair<double, List<KeyValuePair<int, double>>>(200.0, b);
            var c = new List<KeyValuePair<int, double>>();
            for (int t = 0; t < 60 * 50; t++) c.Add(new KeyValuePair<int, double>(t, 860.0 + t / 120.0));
            seqs["approach"] = new KeyValuePair<double, List<KeyValuePair<int, double>>>(900.0, c);
            string[] names = { "none", "enter", "rise", "period" };
            foreach (var kv in seqs)
            {
                var clock = new S16CreakClock();
                var got = new List<string>();
                foreach (var p in kv.Value.Value)
                {
                    int lv = S16Creak.Level(p.Value, kv.Value.Key);
                    S16CreakReason why = clock.Step((ulong)p.Key, lv);
                    if (why != S16CreakReason.None) got.Add(p.Key.ToString(CultureInfo.InvariantCulture) + "/" + lv + "/" + names[(int)why]);
                }
                CollectionAssert.AreEqual(want[kv.Key], got, kv.Key + ": " + string.Join(" ", got));
            }
        }

        [TestMethod]
        public void Voice_Vectors()
        {
            var want = new List<string>();
            foreach (string[] f in S16Vectors.Lines("voice.tsv")) want.Add(f[1] + "/" + f[2] + "/" + f[5]);
            var pts = new List<double[]>();
            // the twin's voice_sequences: a crossing, 60 s past (repeats), back up (re-armed), down within the cooldown, a repeat after it, another grid
            for (int i = 0; i < 100; i++) pts.Add(new double[] { i * 100, 7, 280.0 + i * 0.5 });
            for (int i = 0; i < 120; i++) pts.Add(new double[] { 10000 + i * 500, 7, 330.0 });
            for (int i = 0; i < 100; i++) pts.Add(new double[] { 70000 + i * 100, 7, 330.0 - i * 0.5 });
            for (int i = 0; i < 100; i++) pts.Add(new double[] { 80000 + i * 100, 7, 280.0 + i * 0.5 });
            for (int i = 0; i < 30; i++) pts.Add(new double[] { 90000 + i * 1000, 7, 330.0 });
            for (int i = 0; i < 100; i++) pts.Add(new double[] { 300000 + i * 100, 9, 280.0 + i * 0.5 });
            var arms = new Dictionary<long, S16VoiceArm>();
            var rule = new S16VoiceRule();
            var got = new List<string>();
            foreach (double[] p in pts)
            {
                long key = (long)p[1];
                S16VoiceArm arm;
                if (!arms.TryGetValue(key, out arm)) arms[key] = arm = new S16VoiceArm();
                if (arm.Step(p[2], 300.0, (long)p[0])) got.Add(((long)p[0]).ToString(CultureInfo.InvariantCulture) + "/" + key + "/" + (rule.TryFire(key, (long)p[0]) ? 1 : 0));
            }
            CollectionAssert.AreEqual(want, got, string.Join(" ", got));
            int repeats = 0;
            foreach (string g in got) if (g.StartsWith("24500/", StringComparison.Ordinal) || g.StartsWith("44500/", StringComparison.Ordinal) || g.StartsWith("64500/", StringComparison.Ordinal)) repeats++;
            Assert.AreEqual(3, repeats, "a repeat every 20 s while past the limit: " + string.Join(" ", got));
            Assert.AreEqual(1.6f, S16VoiceMix.VoiceVolume, 1e-6f, "the voice at 1.6");
        }

        [TestMethod]
        public unsafe void Wire_Vectors_AndRoundTrip()
        {
            foreach (string[] f in S16Vectors.Lines("wire.tsv"))
            {
                var w = new S16WireState
                {
                    Flags = (byte)S16Vectors.I(f[0]), CreakLevel = (byte)S16Vectors.I(f[1]), HullCode = (byte)S16Vectors.I(f[2]), Tier = (byte)S16Vectors.I(f[3]),
                    BreathX10 = (byte)S16Vectors.I(f[4]), HudState = (ushort)S16Vectors.I(f[5]), DepthM = (float)S16Vectors.D(f[6]), CrushM = (float)S16Vectors.D(f[7]),
                    CreakSeq = uint.Parse(f[8], CultureInfo.InvariantCulture), VoiceSeq = uint.Parse(f[9], CultureInfo.InvariantCulture),
                    DamageSeq = uint.Parse(f[10], CultureInfo.InvariantCulture), WorldEpoch = uint.Parse(f[11], CultureInfo.InvariantCulture),
                    GridId = long.Parse(f[12], CultureInfo.InvariantCulture),
                    HullPct = (byte)S16Vectors.I(f[13]), PowerPct = (byte)S16Vectors.I(f[14]),
                };
                var st = new SeState();
                st.helloEpoch = 7;
                S16Wire.Write(ref st, ref w);
                byte[] bytes = S16Wire.Bytes(ref st);
                var sb = new StringBuilder();
                foreach (byte x in bytes) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                Assert.AreEqual(f[15], sb.ToString(), "bytes of " + string.Join(" ", f));
                S16WireState r;
                Assert.IsTrue(S16Wire.TryRead(ref st, out r));
                Assert.AreEqual(w.GridId, r.GridId);
                Assert.AreEqual(w.DepthM, r.DepthM);
                Assert.AreEqual(w.CreakSeq, r.CreakSeq);
                Assert.AreEqual(w.HullPct, r.HullPct);
                Assert.AreEqual(w.PowerPct, r.PowerPct);
                Assert.AreEqual(7u, st.helloEpoch, "the extension leaves the record's fields alone");
                S16Wire.Clear(ref st);
                Assert.IsFalse(S16Wire.TryRead(ref st, out r), "cleared: version 0");
            }
        }

        [TestMethod]
        public unsafe void Status_Vectors_AndRoundTrip()
        {
            foreach (string[] f in S16Vectors.Lines("status.tsv"))
            {
                var st = new SnStatus();
                st.snFrame = 9;
                S16Wire.WriteStatus(ref st, (byte)S16Vectors.I(f[0]));
                byte* b = (byte*)&st;
                var sb = new StringBuilder();
                for (int i = 0; i < S16Conventions.C7.Size; i++) sb.Append(b[S16Conventions.C7.Offset + i].ToString("x2", CultureInfo.InvariantCulture));
                Assert.AreEqual(f[1], sb.ToString(), "C7 bytes of " + f[0]);
                byte flags;
                Assert.IsTrue(S16Wire.TryReadStatus(ref st, out flags));
                Assert.AreEqual((byte)S16Vectors.I(f[0]), flags);
                Assert.AreEqual(9UL, st.snFrame, "the block leaves the record's fields alone");
            }
            var empty = new SnStatus();
            byte none;
            Assert.IsFalse(S16Wire.TryReadStatus(ref empty, out none), "version 0 = absent");
        }

        [TestMethod]
        public void SeqReader_BaselineAndCoalesce()
        {
            var r = new S16SeqReader();
            Assert.AreEqual(0, r.Step(1, 5), "baseline");
            Assert.AreEqual(0, r.Step(1, 5));
            Assert.AreEqual(1, r.Step(1, 6));
            Assert.AreEqual(1, r.Step(1, 9));
            Assert.AreEqual(2, r.Coalesced);
            Assert.AreEqual(0, r.Step(2, 9), "new epoch rebaselines");
            Assert.AreEqual(1, r.Step(2, 10));
            Assert.AreEqual(0, r.Step(2, 3), "backwards: rebaseline, no sound");
            Assert.AreEqual(1, r.Step(2, 4));
            Assert.AreEqual(0, r.Step(2, uint.MaxValue), "wrap backwards");
            Assert.AreEqual(1, r.Step(2, 0u), "wrap forwards by one");
        }

        [TestMethod]
        public void Hud_Lines()
        {
            string g = S16Hud.GridLine(312.4, 300, S16Conventions.Size.Small, S16Conventions.BlockClass.HeavyArmor, 0, S16Conventions.HudState.Damage);
            StringAssert.Contains(g, "Depth 312 m");
            StringAssert.Contains(g, "crush depth 300 m (heavy armour, small grid)");
            StringAssert.Contains(g, "HULL INTEGRITY CRITICAL");
            StringAssert.Contains(S16Hud.GridLine(1650, 1700, 2, 2, 2, 2), "pressure hull Mk2");
            // format hud. the grid line only as the seated fallback
            Assert.AreEqual(S16Conventions.SeLine.None, S16Hud.SeLine(false, 0, false), "diver above water: nothing");
            Assert.AreEqual(S16Conventions.SeLine.None, S16Hud.SeLine(true, S16Conventions.C7Flags.DiverShown, false), "diver under water: Subnautica's compass");
            Assert.AreEqual(S16Conventions.SeLine.Grid, S16Hud.SeLine(false, 0, true), "seated, no Subnautica readouts: SE's grid line");
            Assert.AreEqual(S16Conventions.SeLine.Suppressed, S16Hud.SeLine(true, S16Conventions.C7Flags.GridShown, true), "seated, Subnautica shows them");
            Assert.AreEqual("none", S16Conventions.SeLine.Name(S16Conventions.SeLine.None));
            StringAssert.Contains(S16Hud.GridLine(1650, 1700, 1, 1, 0, 1, S16Conventions.C5Flags.Docked | S16Conventions.C5Flags.Exempt, 0), "docked inside a pressurised hull");
            StringAssert.Contains(S16Hud.GridLine(250, 200, 1, 1, 0, 4, S16Conventions.C5Flags.Grace, 42), "pressure damage starts in 42 s");
            Assert.AreEqual("Red", S16Hud.Font(S16Conventions.HudState.Creak));
            Assert.AreEqual("White", S16Hud.Font(S16Conventions.HudState.Safe));
        }
    }
}
