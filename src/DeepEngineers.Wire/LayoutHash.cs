using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The layout hash of CONTRACT §6: FNV-1a 64 (offset basis 0xcbf29ce484222325, prime 0x100000001b3) over a
 /// canonical ASCII text: one line per region `region &lt;id&gt; &lt;kind&gt; 0x&lt;offset&gt; 0x&lt;size&gt; &lt;recordSize&gt; &lt;version&gt;` in
 /// id order; one `ring`, `table`, `message` and `const` line per ring-control entry, placed record, byte-ring message
 /// type and wire constant (see the six-block overload); then one line per field `field &lt;record&gt; &lt;name&gt;
 /// 0x&lt;offset&gt; &lt;size&gt; &lt;type&gt;` sorted by record name (ordinal) then offset; lower-case hex without leading zeros,
 /// decimal integers, single spaces, `\n` after every line (the last included), no trailing spaces. The text is
 /// generated from Layout's tables only.
 /// </summary>
    public static unsafe class LayoutHash
    {
        public const ulong FnvOffsetBasis = 0xcbf29ce484222325UL;
        public const ulong FnvPrime = 0x100000001b3UL;

        /// <summary>The layout-1.0 hash pinned in docs/CONTRACT.md §6 (S0.N1). Layout.cs edits must change it.</summary>
        public const ulong PinnedLayout10 = 0x4f3a35f6fc1eaf31UL;

        private static string _text;
        private static ulong _hash;
        private static bool _computed;
        private static readonly object Gate = new object();

        public static ulong Fnv1a64(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            ulong h = FnvOffsetBasis;
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= FnvPrime; }
            return h;
        }

        public static ulong Fnv1a64(byte* data, long length)
        {
            return Fnv1a64Continue(FnvOffsetBasis, data, length);
        }

        /// <summary>Continues an FNV-1a 64 over more bytes (start with FnvOffsetBasis).</summary>
        public static ulong Fnv1a64Continue(ulong h, byte* data, long length)
        {
            for (long i = 0; i < length; i++) { h ^= data[i]; h *= FnvPrime; }
            return h;
        }

        public static ulong Fnv1a64Ascii(string text)
        {
            return Fnv1a64(Encoding.ASCII.GetBytes(text));
        }

        /// <summary>The canonical text of the compiled layout (cached).</summary>
        public static string CanonicalText()
        {
            EnsureComputed();
            return _text;
        }

        /// <summary>The layout hash of the compiled layout (cached); this is the value written in C0 `layoutHash`.</summary>
        public static ulong Compiled
        {
            get { EnsureComputed(); return _hash; }
        }

        private static void EnsureComputed()
        {
            if (_computed) return;
            lock (Gate)
            {
                if (_computed) return;
                string text = BuildCanonicalText(Layout.Regions, Layout.Records);
                _hash = Fnv1a64Ascii(text);
                _text = text;
                _computed = true;
            }
        }

        /// <summary>
 /// Builds the canonical text from the given region and record tables and the compiled ring order, placement
 /// tables, messages and constants (exposed for tests and tools).
 /// </summary>
        public static string BuildCanonicalText(IReadOnlyList<RegionDef> regions, IReadOnlyList<RecordDef> records)
        {
            return BuildCanonicalText(regions, Layout.RingOrder, Layout.Tables, Layout.Messages, Layout.Constants, records);
        }

        /// <summary>
 /// The canonical text, in six blocks:
 /// <list type="number">
 /// <item>`region &lt;id&gt; &lt;kind&gt; 0x&lt;offset&gt; 0x&lt;size&gt; &lt;recordSize&gt; &lt;version&gt;`, in id order;</item>
 /// <item>`ring &lt;index&gt; &lt;region id&gt; 0x&lt;head offset&gt; 0x&lt;tail offset&gt;` (absolute offsets), in index order;</item>
 /// <item>`table &lt;region id&gt; &lt;name&gt; 0x&lt;offset&gt; &lt;count&gt; &lt;stride&gt; &lt;record&gt;` (offset from the region start),
 /// by region id then offset;</item>
 /// <item>`message &lt;region id&gt; &lt;type&gt; &lt;record&gt;`, by region id then type;</item>
 /// <item>`const &lt;group&gt; &lt;name&gt; &lt;value&gt;` (decimal), by group (ordinal), then value, then name (ordinal);</item>
 /// <item>`field &lt;record&gt; &lt;name&gt; 0x&lt;offset&gt; &lt;size&gt; &lt;type&gt;`, by record name (ordinal) then offset.</item>
 /// </list>
 /// </summary>
        public static string BuildCanonicalText(IReadOnlyList<RegionDef> regions, IReadOnlyList<RegionId> rings,
            IReadOnlyList<TableDef> tables, IReadOnlyList<MessageDef> messages, IReadOnlyList<ConstDef> constants,
            IReadOnlyList<RecordDef> records)
        {
            var sb = new StringBuilder(32768);
            var regionList = new List<RegionDef>(regions);
            regionList.Sort((a, b) => ((uint)a.Id).CompareTo((uint)b.Id));
            foreach (RegionDef g in regionList)
            {
                sb.Append("region ").Append(Dec((uint)g.Id)).Append(' ').Append(Dec((uint)g.Kind)).Append(' ')
                  .Append(Hex((ulong)g.Offset)).Append(' ').Append(Hex((ulong)g.Size)).Append(' ')
                  .Append(Dec((uint)g.RecordSize)).Append(' ').Append(Dec(g.Version)).Append('\n');
            }

            for (int r = 0; r < rings.Count; r++)
            {
                long head = Layout.RingControlOffset + (long)r * Layout.RingControlStride;
                sb.Append("ring ").Append(Dec((uint)r)).Append(' ').Append(Dec((uint)rings[r])).Append(' ')
                  .Append(Hex((ulong)head)).Append(' ').Append(Hex((ulong)(head + Layout.RingTailOffset))).Append('\n');
            }

            var tableList = new List<TableDef>(tables);
            tableList.Sort((a, b) =>
            {
                int c = ((uint)a.Region).CompareTo((uint)b.Region);
                return c != 0 ? c : a.Offset.CompareTo(b.Offset);
            });
            foreach (TableDef t in tableList)
            {
                sb.Append("table ").Append(Dec((uint)t.Region)).Append(' ').Append(t.Name).Append(' ').Append(Hex((ulong)t.Offset))
                  .Append(' ').Append(Dec((uint)t.Count)).Append(' ').Append(Dec((uint)t.Stride)).Append(' ').Append(t.Record).Append('\n');
            }

            var messageList = new List<MessageDef>(messages);
            messageList.Sort((a, b) =>
            {
                int c = ((uint)a.Region).CompareTo((uint)b.Region);
                return c != 0 ? c : a.Type.CompareTo(b.Type);
            });
            foreach (MessageDef m in messageList)
                sb.Append("message ").Append(Dec((uint)m.Region)).Append(' ').Append(Dec(m.Type)).Append(' ').Append(m.Record).Append('\n');

            var constList = new List<ConstDef>(constants);
            constList.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Group, b.Group);
                if (c != 0) return c;
                c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            foreach (ConstDef k in constList)
                sb.Append("const ").Append(k.Group).Append(' ').Append(k.Name).Append(' ')
                  .Append(k.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');

            var lines = new List<KeyValuePair<string, FieldDef>>();
            foreach (RecordDef r in records)
                foreach (FieldDef f in r.Fields)
                    lines.Add(new KeyValuePair<string, FieldDef>(r.Name, f));
            lines.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : a.Value.Offset.CompareTo(b.Value.Offset);
            });
            foreach (var kv in lines)
            {
                FieldDef f = kv.Value;
                sb.Append("field ").Append(kv.Key).Append(' ').Append(f.Name).Append(' ').Append(Hex((ulong)f.Offset))
                  .Append(' ').Append(Dec((uint)f.Size)).Append(' ').Append(f.Type).Append('\n');
            }
            string text = sb.ToString();
            for (int i = 0; i < text.Length; i++)
                if (text[i] > 0x7E || (text[i] < 0x20 && text[i] != '\n'))
                    throw new InvalidOperationException("LayoutHash: canonical text is not printable ASCII at " + i);
            return text;
        }

        public static string Hex(ulong v) { return "0x" + v.ToString("x", CultureInfo.InvariantCulture); }
        private static string Dec(uint v) { return v.ToString(CultureInfo.InvariantCulture); }

        /// <summary>"0x" + 16 lower-case hex digits, for logs.</summary>
        public static string Format(ulong hash) { return "0x" + hash.ToString("x16", CultureInfo.InvariantCulture); }
    }
}
