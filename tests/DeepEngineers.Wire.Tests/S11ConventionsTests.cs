using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>The S11 vector files (written by tools/s11/s11_ref.py --vectors) and their number formats.</summary>
    internal static class S11Vectors
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<string[]> Lines(string name)
        {
            string path = Path.Combine(S5Repo.Root, "tests", "s11", "vectors", name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/s11/s11_ref.py --vectors)");
            var rows = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                rows.Add(raw.Split('\t'));
            }
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        /// <summary>Python repr(float): shortest round trip, plus inf / -inf / nan.</summary>
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

        public static Vec3 V(string csv)
        {
            string[] p = csv.Split(',');
            return new Vec3(D(p[0]), D(p[1]), D(p[2]));
        }

        public static Quat Q(string csv)
        {
            string[] p = csv.Split(',');
            return new Quat(D(p[0]), D(p[1]), D(p[2]), D(p[3]));
        }

        public static void Near(double want, double got, string what, double rel = 1e-12)
        {
            if (double.IsNaN(want)) { Assert.IsTrue(double.IsNaN(got), what + ": want NaN, got " + got); return; }
            double tol = rel * Math.Max(1.0, Math.Abs(want));
            Assert.IsTrue(Math.Abs(want - got) <= tol, what + ": want " + want.ToString("R", Inv) + " got " + got.ToString("R", Inv));
        }

        public static void Near(Vec3 want, Vec3 got, string what, double rel = 1e-12)
        {
            Near(want.X, got.X, what + ".x", rel);
            Near(want.Y, got.Y, what + ".y", rel);
            Near(want.Z, got.Z, what + ".z", rel);
        }
    }

    /// <summary>
 /// S11 conventions pin (CONTRACT §5.4 "S11 conventions"): the canonical text equals the fenced block of
 /// docs/CONTRACT.md byte for byte, its FNV-1a 64 is the pinned hash, the units sit outside S6's SN_HP (1), the hazard
 /// set never overlaps the three types S6 forwards, and layout 1.0 with every earlier pin is unchanged.
 /// </summary>
    [TestClass]
    public class S11ConventionsTests
    {
        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string t = S11Conventions.CanonicalText();
            Assert.AreEqual(S11Conventions.Header + "\n", t.Substring(0, S11Conventions.Header.Length + 1));
            Assert.IsTrue(t.EndsWith("\n", StringComparison.Ordinal));
            int lines = 0;
            foreach (char c in t) if (c == '\n') lines++;
            Assert.AreEqual(S11Conventions.CanonicalLines, lines, "lines");
            Assert.AreEqual(S11Conventions.CanonicalBytes, Encoding.ASCII.GetByteCount(t), "bytes");
            foreach (char c in t) Assert.IsTrue(c < 128, "ASCII only");
            Assert.AreEqual(S11Conventions.PinnedHash, S11Conventions.Hash(), "hash 0x" + S11Conventions.Hash().ToString("x16"));
        }

        [TestMethod]
        public void CanonicalText_EqualsTheContractBlock()
        {
            string block = S8aVectors.ContractBlock(S11Conventions.Header);
            if (block == null) Assert.Inconclusive("docs/CONTRACT.md not found above the test output");
            string text = S11Conventions.CanonicalText();
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
        public void Units_OutsideS6_AndHazards_DisjointFromS6Types()
        {
            Assert.IsFalse(S11Conventions.Unit.IsS11(DamageUnit.SnHp), "S6's unit 1 stays S6's");
            for (uint u = S11Conventions.Unit.HazardHp; u <= S11Conventions.Unit.MissileHit; u++) Assert.IsTrue(S11Conventions.Unit.IsS11(u));
            Assert.AreEqual(23u, S11Conventions.Unit.MissileHit, "2026-10-24: the missile hit (format missile.hit)");
            Assert.IsFalse(S11Conventions.Unit.IsS11(24));
            foreach (S11HazardRow h in S11Conventions.HazardTable)
            {
                Assert.IsFalse(CombatConventions.IsForwarded((ushort)h.SnType), h.Name + " is one of S6's forwarded types");
                Assert.AreEqual(h.SnType, S11GrabRules.SnDamageTypeValue(h.Name), h.Name + " value (DamageType.cs)");
                Assert.IsTrue(h.SeType.StartsWith("Deep", StringComparison.Ordinal));
                Assert.IsTrue(h.CapPerS > 0 && h.KPermille > 0);
            }
            Assert.IsFalse(S11HazardRules.Forwarded(2), "Pressure is S16 (crush depth), not S11");
            Assert.AreEqual(256, S11Conventions.Flags.ViaSampler & ~0xFF, "S11 flags above S6's bits 0-4");
            Assert.AreEqual(0, (S11Conventions.Flags.ViaSampler | S11Conventions.Flags.ViaSection) & 0x1F);
        }

        [TestMethod]
        public void Release_DeS11IsDevOnly()
        {
            var args = new System.Collections.Generic.Dictionary<string, object> { { "action", "weapon" }, { "class", "handheld" } };
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s11", args), "release: de.s11 (the weapon retune, the scenes) is dev-only");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s11", null));
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s11", args), "dev builds answer it");
            Assert.IsFalse(UserCommandRules.ChatAllowed(false, new[] { "s11", "weapon" }), "release chat: /de s11 refused");
        }

        [TestMethod]
        public void Tables_Complete_AndEarlierPinsUnchanged()
        {
            for (int c = 1; c <= 4; c++)
                for (int t = 1; t <= 3; t++)
                    Assert.IsTrue(S11Conventions.WeaponRow(c, t).HasValue, "weapon row " + c + "/" + t);
            S11GrabRow reaper;
            Assert.IsTrue(S11Conventions.TryGrabRow("ReaperLeviathan", out reaper));
            Assert.AreEqual("seamothAttachPoint", reaper.AttachField);
            Assert.IsTrue(S11Conventions.TryGrabRow("seadragon", out reaper), "case-insensitive TechType name");
            S11PushRow p;
            Assert.IsTrue(S11Conventions.TryPushRow("GhostLeviathan", out p));
            Assert.IsFalse(S11Conventions.TryGrabRow("GhostLeviathan", out reaper), "the ghost never holds (no grab in Subnautica)");
            Assert.AreEqual(0xb6ee880712f8253bUL, CombatConventions.PinnedHash, "S6 pin unchanged");
            Assert.AreEqual(CombatConventions.PinnedHash, CombatConventions.Hash(), "S6 text unchanged");
            Assert.AreEqual(45, S11Conventions.Oxygen.TankS, "the S2b tank");
            Assert.AreEqual(SwimThresholds.OxygenCapacity, (double)S11Conventions.Oxygen.TankS, "SwimThresholds.OxygenCapacity (S2b)");
        }

        [TestMethod]
        public unsafe void C7Block_RoundTrip_NextToS16AndS8a()
        {
            // pass 5: SnStatus 0xC0-0xC3, after the S8a block (0x8C-0xBB) and the S16 block (0xBC-0xBF), inside pad_8c (0x8C-0xFF)
            Assert.AreEqual(S16Conventions.C7.Offset + S16Conventions.C7.Size, S11Conventions.C7.Offset, "right after the S16 block");
            Assert.IsTrue(S11Conventions.C7.Offset + S11Conventions.C7.Size <= 0x100, "inside the 256-byte record");
            var st = new SnStatus();
            st.snFrame = 7;
            S16Wire.WriteStatus(ref st, 5);
            S11Wire.WriteStatus(ref st, (byte)(S11Conventions.C7Flags.DamageFxOk | S11Conventions.C7Flags.DamageFxShown));
            byte f16, f11;
            Assert.IsTrue(S16Wire.TryReadStatus(ref st, out f16));
            Assert.AreEqual(5, f16, "the S16 block is left alone");
            Assert.IsTrue(S11Wire.TryReadStatus(ref st, out f11));
            Assert.AreEqual(3, f11);
            byte* b = (byte*)&st;
            Assert.AreEqual(1, b[0xC0]);
            Assert.AreEqual(3, b[0xC1]);
            Assert.AreEqual(0, b[0xC2] | b[0xC3]);
            Assert.AreEqual(7UL, st.snFrame);
            var empty = new SnStatus();
            Assert.IsFalse(S11Wire.TryReadStatus(ref empty, out f11), "version 0 = absent: SE keeps its own overlay");
        }
    }
}
