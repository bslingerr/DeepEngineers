using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// The vector files of the S8a Python twin (<c>tests/s8a/vectors/*.tsv</c>, written by
 /// <c>python -I tools/planet/s8a_ref.py --vectors</c> and <c>tools/sn-octrees/sn_octree_write.py --vectors</c>): tab
 /// separated, <c>#</c> comments, doubles as 16 hex digits of their IEEE bits, lists comma-joined.
 /// </summary>
    internal static class S8aVectors
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string PathOf(string name) { return Path.Combine(S5Repo.Root, "tests", "s8a", "vectors", name); }

        public static List<string[]> Lines(string name)
        {
            string path = PathOf(name);
            Assert.IsTrue(File.Exists(path), "vector file missing: " + path + " (python -I tools/planet/s8a_ref.py --vectors)");
            var rows = new List<string[]>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                rows.Add(raw.Split('\t'));
            }
            Assert.IsTrue(rows.Count > 0, name + " has no rows");
            return rows;
        }

        /// <summary>16 hex digits of IEEE bits → double.</summary>
        public static double D(string bits) { return BitConverter.Int64BitsToDouble((long)ulong.Parse(bits, NumberStyles.AllowHexSpecifier, Inv)); }

        public static string Bits(double v) { return ((ulong)BitConverter.DoubleToInt64Bits(v)).ToString("x16", Inv); }

        public static Vec3 V(string csv)
        {
            string[] p = csv.Split(',');
            return new Vec3(D(p[0]), D(p[1]), D(p[2]));
        }

        public static int I(string s) { return int.Parse(s, Inv); }
        public static uint U(string s) { return uint.Parse(s, Inv); }
        public static ulong X16(string s) { return ulong.Parse(s, NumberStyles.AllowHexSpecifier, Inv); }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", Inv));
            return sb.ToString();
        }

        public static byte[] Unhex(string h)
        {
            var b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(2 * i, 2), NumberStyles.AllowHexSpecifier, Inv);
            return b;
        }

        public static string Ids(List<Int3Key> ids)
        {
            if (ids.Count == 0) return "-";
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(ids[i].X).Append(',').Append(ids[i].Y).Append(',').Append(ids[i].Z);
            }
            return sb.ToString();
        }

        /// <summary>The fenced canonical block of docs/CONTRACT.md that starts with <paramref name="header"/> (null when the repository is not above the test output).</summary>
        public static string ContractBlock(string header)
        {
            string path = Path.Combine(S5Repo.Root, "docs", "CONTRACT.md");
            if (!File.Exists(path)) return null;
            string doc = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
            int i = doc.IndexOf("```\n" + header + "\n", StringComparison.Ordinal);
            if (i < 0) return null;
            i += 4;
            int j = doc.IndexOf("```", i, StringComparison.Ordinal);
            return j < 0 ? null : doc.Substring(i, j - i);
        }
    }

    /// <summary>
 /// S8a conventions pin (CONTRACT §5.4 "S8a conventions", ROADMAP S8a.C3): the canonical text equals the fenced block
 /// of docs/CONTRACT.md byte for byte, its FNV-1a 64 is <c>0x3188251ab11e2fec</c> with 155 lines and 14,696 bytes (ext s8a terrain 6),
 /// every <c>const</c> line names a constant of the code, the header and the rows lie inside the C2 region past S7's
 /// event window, the C7 block inside <c>pad_8c</c>, and layout 1.0 with the earlier extension pins is unchanged. The
 /// Python twin (<c>tools/planet/s8a_ref.py --selftest</c>) pins the same block, so the three agree.
 /// </summary>
    [TestClass]
    public class S8aConventionsTests
    {
        [TestMethod]
        public void CanonicalText_PinnedHashLinesAndBytes()
        {
            string text = S8aConventions.CanonicalText();
            foreach (char c in text) Assert.IsTrue(c < 128, "ASCII only");
            Assert.AreEqual(S8aConventions.Header + "\n", text.Substring(0, S8aConventions.Header.Length + 1));
            Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal));
            int lines = 0;
            foreach (char c in text) if (c == '\n') lines++;
            Assert.AreEqual(S8aConventions.CanonicalLines, lines, "lines");
            Assert.AreEqual(155, lines);
            Assert.AreEqual(S8aConventions.CanonicalBytes, Encoding.ASCII.GetByteCount(text), "bytes");
            Assert.AreEqual(14696, Encoding.ASCII.GetByteCount(text));
            Assert.AreEqual(0x3188251ab11e2fecUL, S8aConventions.PinnedHash);
            Assert.AreEqual(S8aConventions.PinnedHash, S8aConventions.Hash, "ext s8a terrain 5 drifted: 0x" + S8aConventions.Hash.ToString("x16"));
            Assert.AreEqual(LayoutHash.Fnv1a64(Encoding.ASCII.GetBytes(text)), S8aConventions.Hash);
        }

        [TestMethod]
        public void CanonicalText_EqualsTheContractBlock()
        {
            string block = S8aVectors.ContractBlock(S8aConventions.Header);
            if (block == null) Assert.Inconclusive("docs/CONTRACT.md not found above the test output");
            string text = S8aConventions.CanonicalText();
            if (block != text)
            {
                string[] a = block.Split('\n'), b = text.Split('\n');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    string x = i < a.Length ? a[i] : "<end>", y = i < b.Length ? b[i] : "<end>";
                    if (x != y) Assert.Fail("line " + (i + 1) + " differs:\n CONTRACT: " + x + "\n Wire:     " + y);
                }
            }
            Assert.AreEqual(block, text);
        }

        [TestMethod]
        public void ConstLines_SortedByGroupValueName_AndEveryGroupPresent()
        {
            string[] lines = S8aConventions.CanonicalText().Split('\n');
            string prevGroup = null;
            long prevValue = long.MinValue;
            string prevName = null;
            int consts = 0, fields = 0, formats = 0;
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (string l in lines)
            {
                if (l.StartsWith("const ", StringComparison.Ordinal))
                {
                    consts++;
                    string[] p = l.Split(' ');
                    Assert.AreEqual(4, p.Length, l);
                    long v = long.Parse(p[3], CultureInfo.InvariantCulture);
                    groups.Add(p[1]);
                    if (prevGroup != null)
                    {
                        int c = string.CompareOrdinal(prevGroup, p[1]);
                        Assert.IsTrue(c <= 0, "group order at " + l);
                        if (c == 0)
                        {
                            Assert.IsTrue(prevValue <= v, "value order at " + l);
                            if (prevValue == v) Assert.IsTrue(string.CompareOrdinal(prevName, p[2]) < 0, "name order at " + l);
                        }
                    }
                    prevGroup = p[1]; prevValue = v; prevName = p[2];
                }
                else if (l.StartsWith("field ", StringComparison.Ordinal)) fields++;
                else if (l.StartsWith("format ", StringComparison.Ordinal)) formats++;
            }
            Assert.AreEqual(80, consts, "const lines");
            Assert.AreEqual(S8aConventions.FieldLines().Length, fields);
            Assert.AreEqual(45, fields, "field lines");
            Assert.AreEqual(S8aConventions.FormatLines.Length, formats);
            Assert.AreEqual(29, formats, "format lines");
            Assert.AreEqual(1 + consts + fields + formats, S8aConventions.CanonicalLines);
            foreach (string g in new[] { "C2S8a", "C2S8aFlags", "C7S8a", "C7S8aFlags", "Chart", "NoticeFlags", "NoticeKind", "NoticeLayer", "Overlay", "Queue", "SnWorld", "Spawn", "Store", "Window" })
                Assert.IsTrue(groups.Contains(g), "group " + g);
            // format lines sorted by key
            string prevF = null;
            foreach (string l in lines)
            {
                if (!l.StartsWith("format ", StringComparison.Ordinal)) continue;
                if (prevF != null) Assert.IsTrue(string.CompareOrdinal(prevF, l) < 0, "format order at " + l);
                prevF = l;
            }
        }

        [TestMethod]
        public void Places_InsideTheC2RegionPastS7_AndInsidePad8c()
        {
            Assert.AreEqual(0x50000, S8aConventions.C2S8a.Offset);
            Assert.AreEqual(S8aConventions.C2S8a.Offset + S8aConventions.C2S8a.HeaderSize, S8aConventions.C2S8a.RowsOffset);
            Assert.AreEqual(0x58040, S8aConventions.C2S8a.End);
            Assert.IsTrue(S8aConventions.C2S8a.Offset >= S7Conventions.C2Events.Offset + S7Conventions.C2Events.MaxRows * S7Conventions.C2Events.RowSize, "past S7's event window");
            Assert.IsTrue(S8aConventions.C2S8a.End <= Layout.Region(RegionId.C2SeEntities).Size, "inside the C2 region");
            Assert.AreEqual(0x8C, S8aConventions.C7S8a.Offset);
            Assert.AreEqual(48, S8aConventions.C7S8a.Size);
            Assert.IsTrue(S8aConventions.C7S8a.Offset + S8aConventions.C7S8a.Size <= 0x8C + 0x74, "inside SnStatus.pad_8c");
            unsafe
            {
                Assert.AreEqual(32, sizeof(S8aNotice));
                Assert.AreEqual(64, sizeof(S8aHeader));
                Assert.AreEqual(48, sizeof(S8aC7));
                Assert.AreEqual(256, sizeof(SnStatus));
            }
        }

        [TestMethod]
        public void EarlierPins_Unchanged()
        {
            Assert.AreEqual(0x4f3a35f6fc1eaf31UL, LayoutHash.PinnedLayout10, "layout 1.0");
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 compiled");
            Assert.AreEqual(0x1f9b9b4a876a97f9UL, S7Conventions.Hash, "ext s7 look 1");
            Assert.AreEqual(0x4257c11c17ff772aUL, S7aConventions.Hash, "ext s7a picture 1");
        }

        [TestMethod]
        public void FieldLines_MatchTheStructs()
        {
            // every field line's offset and size agree with the explicit struct layout
            string[] f = S8aConventions.FieldLines();
            var notice = new Dictionary<string, int> { { "seq", 0 }, { "kind", 4 }, { "layer", 5 }, { "flags", 6 }, { "bx", 8 }, { "by", 10 }, { "bz", 12 }, { "pad", 14 }, { "version", 16 }, { "min", 20 }, { "max", 23 }, { "octrees", 26 }, { "cells", 28 } };
            var hdr = new Dictionary<string, int> { { "version", 0 }, { "flags", 1 }, { "rowCount", 2 }, { "firstSeq", 4 }, { "stream", 8 }, { "dropped", 12 }, { "tickLow", 16 }, { "resyncEpoch", 20 }, { "storeKey", 24 }, { "overlayGen", 32 }, { "chartSeq", 36 }, { "cameraBiome", 40 }, { "pad", 41 }, { "nearPendingCount", 42 }, { "seabedBelowCamM", 44 }, { "windowGen", 48 }, { "drillGen", 52 }, { "chartKey", 56 } };
            var c7 = new Dictionary<string, int> { { "version", 0x8C }, { "flags", 0x8D }, { "reloadsPending", 0x8E }, { "ackSeq", 0x90 }, { "appliedSeq", 0x94 }, { "stream", 0x98 }, { "resyncEpoch", 0x9C }, { "chartSeqApplied", 0xA0 }, { "batchesServed", 0xA4 }, { "reloadFailures", 0xA8 }, { "lastLatencyMs", 0xAC }, { "nearPendingSeen", 0xAE }, { "storeKey", 0xB0 }, { "pad", 0xB8 } };
            int seen = 0;
            foreach (string l in f)
            {
                string[] p = l.Split(' ');
                int off = Convert.ToInt32(p[2].Substring(2), 16);
                Dictionary<string, int> want = p[0] == "s8a" ? hdr : p[0] == "notice" ? notice : c7;
                Assert.IsTrue(want.ContainsKey(p[1]), l);
                Assert.AreEqual(want[p[1]], off, l);
                seen++;
            }
            Assert.AreEqual(hdr.Count + notice.Count + c7.Count, seen);
        }
    }
}
