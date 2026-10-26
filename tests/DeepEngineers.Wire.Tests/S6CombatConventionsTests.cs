using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// CONTRACT §5.4 "S6 combat conventions": the canonical text byte for byte (against CONTRACT.md itself), the pinned
 /// hash, every constant against the code, DamageScale.Version = the header's damageScaleVersion, and the layout's own
 /// pinned classes and record sizes untouched (layout 1.0 is not re-pinned for S6).
 /// </summary>
    [TestClass]
    public class CombatConventionsTests
    {
        /// <summary>The fenced block of CONTRACT.md that starts with "ext s6 combat 1".</summary>
        private static string ContractText()
        {
            string doc = File.ReadAllText(Path.Combine(S5Repo.Root, "docs", "CONTRACT.md"), Encoding.UTF8).Replace("\r\n", "\n");
            int i = doc.IndexOf("```\next s6 combat 1\n", StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, "CONTRACT.md has no S6 conventions block");
            i += 4;
            int j = doc.IndexOf("```", i, StringComparison.Ordinal);
            return doc.Substring(i, j - i);
        }

        [TestMethod]
        public void S6Conventions_TextIsTheContractTextByteForByte()
        {
            string text = CombatConventions.CanonicalText();
            Assert.AreEqual(ContractText(), text);
            Assert.AreEqual(CombatConventions.CanonicalLines, text.Split('\n').Length - 1, "lines");
            Assert.AreEqual(CombatConventions.CanonicalBytes, Encoding.ASCII.GetByteCount(text), "bytes");
            Assert.IsTrue(text.StartsWith(CombatConventions.Header + "\n", StringComparison.Ordinal));
            Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal));
            foreach (char c in text) Assert.IsTrue(c < 128, "ASCII only");
        }

        [TestMethod]
        public void S6Conventions_HashIsPinned()
        {
            Assert.AreEqual(0xb6ee880712f8253bUL, CombatConventions.PinnedHash);
            Assert.AreEqual(CombatConventions.PinnedHash, CombatConventions.Hash());
            Assert.AreEqual(LayoutHash.Fnv1a64Ascii(CombatConventions.CanonicalText()), CombatConventions.Hash());
        }

        [TestMethod]
        public void S6Conventions_EveryConstLineEqualsTheCode()
        {
            var code = new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                { "C8FlagsExt GhostPlayer", CombatConventions.C8FlagsExt.GhostPlayer },
                { "CombatLimits RefreshMm", CombatConventions.Limits.RefreshMm },
                { "CombatLimits SkinDebounceTicks", CombatConventions.Limits.SkinDebounceTicks },
                { "CombatLimits CapsuleRadiusMm", CombatConventions.Limits.CapsuleRadiusMm },
                { "CombatLimits MaxShapesPerStep", CombatConventions.Limits.MaxShapesPerStep },
                { "CombatLimits MaxAmountSnHp", CombatConventions.Limits.MaxAmountSnHp },
                { "CombatLimits CapsuleHeightMm", CombatConventions.Limits.CapsuleHeightMm },
                { "CombatLimits MaxHalfExtentMm", CombatConventions.Limits.MaxHalfExtentMm },
                { "CombatLimits OverflowRecords", CombatConventions.Limits.OverflowRecords },
                { "CombatLimits DedupWindow", CombatConventions.Limits.DedupWindow },
                { "CombatLimits SkinMaxBytes", CombatConventions.Limits.SkinMaxBytes },
                { "DamageScale KChar", (ulong)CombatConventions.DamageScale.KChar },
                { "DamageScale KSmall", (ulong)CombatConventions.DamageScale.KSmall },
                { "DamageScale KLarge", (ulong)CombatConventions.DamageScale.KLarge },
                { "DamageScale Version", CombatConventions.DamageScale.Version },
                { "SkinBoxFlags Rotated", CombatConventions.SkinBoxFlags.Rotated },
                { "SkinBoxFlags SourceShift", CombatConventions.SkinBoxFlags.SourceShift },
                { "SkinBoxFlags SourceMask", CombatConventions.SkinBoxFlags.SourceMask },
                { "SkinBoxSource Segment", CombatConventions.SkinBoxSource.Segment },
                { "SkinBoxSource ModelShapeObb", CombatConventions.SkinBoxSource.ModelShapeObb },
                { "SkinBoxSource Synthetic", CombatConventions.SkinBoxSource.Synthetic },
                { "SkinLattice MaxBoxCellsLarge", CombatConventions.SkinLattice.MaxBoxCellsLarge },
                { "SkinLattice SectionCellsLarge", CombatConventions.SkinLattice.SectionCellsLarge },
                { "SkinLattice SectionIdBits", CombatConventions.SkinLattice.SectionIdBits },
                { "SkinLattice MaxBoxCellsSmall", CombatConventions.SkinLattice.MaxBoxCellsSmall },
                { "SkinLattice SectionCellsSmall", CombatConventions.SkinLattice.SectionCellsSmall },
                { "SkinLattice SectionIdBias", CombatConventions.SkinLattice.SectionIdBias },
                { "SnDamageForward Normal", CombatConventions.SnDamageForward.Normal },
                { "SnDamageForward Explosive", CombatConventions.SnDamageForward.Explosive },
                { "SnDamageForward Electrical", CombatConventions.SnDamageForward.Electrical },
            };
            foreach (DamageReject r in (DamageReject[])Enum.GetValues(typeof(DamageReject)))
                if (r != DamageReject.None) code.Add("DamageReject " + r, (ulong)(int)r);

            int consts = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in ContractText().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("const ", StringComparison.Ordinal)) continue;
                string[] t = line.Split(' ');
                Assert.AreEqual(4, t.Length, line);
                string key = t[1] + " " + t[2];
                Assert.IsTrue(code.ContainsKey(key), "no code constant for " + key);
                Assert.AreEqual(ulong.Parse(t[3]), code[key], key);
                Assert.IsTrue(seen.Add(key), "duplicate " + key);
                consts++;
            }
            Assert.AreEqual(code.Count, consts, "every code constant appears in the text");
            Assert.AreEqual(47, consts);
            Assert.AreEqual(CombatConventions.Limits.MaxHalfExtentMm / 1000.0, CollisionSkinMath.MaxHalfExtent);
            Assert.AreEqual(0.01, CollisionSkinMath.RefreshMetres);
        }

        [TestMethod]
        public void S6Conventions_DamageScaleVersionIsTheHeaderValue()
        {
            Assert.AreEqual(WireInfo.DamageScaleVersion, CombatConventions.DamageScale.Version);
            Assert.AreEqual(1.0, CombatConventions.DamageScale.KChar);
            Assert.AreEqual(1.0, CombatConventions.DamageScale.KSmall);
            Assert.AreEqual(25.0, CombatConventions.DamageScale.KLarge);
        }

        [TestMethod]
        public void S6Conventions_LayoutClassesUntouched()
        {
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 hash unchanged");
            Assert.AreEqual(1, DamageFlags.MouthContact);
            Assert.AreEqual(2, DamageFlags.Radius);
            Assert.AreEqual(4, DamageFlags.FallbackHitPoint);
            Assert.AreEqual(8, DamageFlags.FrameSubgrid);
            Assert.AreEqual(0, CombatConventions.C8FlagsExt.GhostPlayer & (DamageFlags.MouthContact | DamageFlags.Radius | DamageFlags.FallbackHitPoint | DamageFlags.FrameSubgrid),
                "GhostPlayer is a new bit");
            Assert.AreEqual(1u, SkinSectionKind.Grid);
            Assert.AreEqual(2u, SkinSectionKind.Character);
            Assert.AreEqual(1u, DamageTargetKind.GridSection);
            Assert.AreEqual(2u, DamageTargetKind.Character);
            Assert.AreEqual(1u, DamageUnit.SnHp);
            Assert.AreEqual(6u, C4Type.SkinSet);
            Assert.AreEqual(1u, GridClass.LargeGrid);
            Assert.AreEqual(2u, GridClass.SmallGrid);
            Assert.AreEqual(1u << 10, SnStatusFlags.DamageSelfTestPass);
            Assert.AreEqual(1u << 1, Capabilities.CreaturesAndDamage);
            Assert.AreEqual(Layout.C8OverflowCapacity, CombatConventions.Limits.OverflowRecords);
            Assert.AreEqual(128, Marshal.SizeOf(typeof(DamageRecord)));
            Assert.AreEqual(16, Marshal.SizeOf(typeof(SkinSetHeader)));
            Assert.AreEqual(24, Marshal.SizeOf(typeof(SkinSection)));
            Assert.AreEqual(64, Marshal.SizeOf(typeof(SkinBox)));
            Assert.AreEqual(SkinCodec.HeaderBytes, Marshal.SizeOf(typeof(SkinSetHeader)));
            Assert.AreEqual(SkinCodec.SectionBytesFixed, Marshal.SizeOf(typeof(SkinSection)));
            Assert.AreEqual(SkinCodec.BoxBytes, Marshal.SizeOf(typeof(SkinBox)));
            Assert.AreEqual(0x3C, (int)Marshal.OffsetOf(typeof(SkinBox), "flags"));
        }

        [TestMethod]
        public void S6Conventions_DamageRejectValuesAreContiguous()
        {
            var values = (DamageReject[])Enum.GetValues(typeof(DamageReject));
            Assert.AreEqual(18, values.Length);
            for (int i = 0; i < values.Length; i++) Assert.AreEqual(i, (int)values[i]);
            Assert.AreEqual(13, (int)DamageReject.HitPoint);
            Assert.AreEqual(17, (int)DamageReject.NotApplied);
        }

        [TestMethod]
        public void S6Conventions_SeDamageTypeNames()
        {
            Assert.AreEqual("DeepBite", CombatConventions.SeDamageTypeName(0));
            Assert.AreEqual("DeepBlast", CombatConventions.SeDamageTypeName(7));
            Assert.AreEqual("DeepShock", CombatConventions.SeDamageTypeName(8));
            foreach (ushort t in new ushort[] { 1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14, 15, 16, 65535 })
            {
                Assert.IsNull(CombatConventions.SeDamageTypeName(t), "type " + t);
                Assert.IsFalse(CombatConventions.IsForwarded(t));
            }
            StringAssert.Contains(CombatConventions.CanonicalText(), "format seDamageType 0=DeepBite 7=DeepBlast 8=DeepShock\n");
        }
    }
}
