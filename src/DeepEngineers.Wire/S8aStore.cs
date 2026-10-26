using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using OverlayC = DeepEngineers.Wire.S8aConventions.Overlay;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a store (CONTRACT §3.3 "The store", §5.4 formats store.path, store.key, store.manifest, overlay.file). The store is
 // derived data under %LOCALAPPDATA%\DeepEngineers\terrain\ (never inside either game's install folder or SNAppData): one
 // writer (SE), one reader (Subnautica). Pure helpers: keys, paths, the manifest text, the overlay file codec. Python twin:
 // tools/planet/s8a_ref.py (vectors tests/s8a/vectors/store.tsv).
 // =================================================================================================================

 /// <summary>Keys and paths of the store ("store.key", "store.path").</summary>
    public static class S8aStore
    {
        public const string ManifestName = "manifest.v1.txt";
        public const string ManifestMagic = "de-s8a-store";
        public const int ManifestVersion = 1;
        public const string DrillLayer = "drill";
        public const string WindowLayer = "window";

        /// <summary>FNV-1a 64 of the UTF-8 bytes of <paramref name="text"/>.</summary>
        public static ulong Fnv1a64Utf8(string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text ?? "");
            return LayoutHash.Fnv1a64(b);
        }

        /// <summary>16 lower-case hex digits.</summary>
        public static string Hex16(ulong v) { return v.ToString("x16", CultureInfo.InvariantCulture); }

        /// <summary>storeKey = fnv1a64(utf8(lower(world folder path at load) + "|" + siteVersion)); lower = ToLowerInvariant.</summary>
        public static ulong StoreKey(string worldPath, int siteVersion)
        {
            return Fnv1a64Utf8(StoreKeyText(worldPath, siteVersion));
        }

        public static string StoreKeyText(string worldPath, int siteVersion)
        {
            return (worldPath ?? "").ToLowerInvariant() + "|" + siteVersion.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The default store root: %LOCALAPPDATA%\DeepEngineers\terrain.</summary>
        public static string DefaultRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", "terrain");
        }

        /// <summary>&lt;root&gt;\&lt;storeKey x16&gt;</summary>
        public static string StoreFolder(string root, ulong storeKey) { return Path.Combine(root, Hex16(storeKey)); }

        /// <summary>&lt;root&gt;\&lt;storeKey x16&gt;\drill</summary>
        public static string DrillFolder(string root, ulong storeKey) { return Path.Combine(root, Hex16(storeKey), DrillLayer); }

        /// <summary>&lt;root&gt;\&lt;storeKey x16&gt;\window\&lt;chartKey x16&gt;</summary>
        public static string WindowFolder(string root, ulong storeKey, ulong chartKey)
        {
            return Path.Combine(root, Hex16(storeKey), WindowLayer, Hex16(chartKey));
        }

        /// <summary><c>compiled-batch-&lt;bx&gt;-&lt;by&gt;-&lt;bz&gt;.v&lt;version&gt;.optoctrees</c> (a version is never overwritten).</summary>
        public static string BatchFileName(int bx, int by, int bz, uint version)
        {
            return "compiled-batch-" + bx.ToString(CultureInfo.InvariantCulture) + "-" + by.ToString(CultureInfo.InvariantCulture) + "-" +
                   bz.ToString(CultureInfo.InvariantCulture) + ".v" + version.ToString(CultureInfo.InvariantCulture) + ".optoctrees";
        }

        /// <summary>Subnautica's own name (<c>compiled-batch-x-y-z.optoctrees</c>; the S4 reader reads a folder of those).</summary>
        public static string PlainBatchFileName(int bx, int by, int bz)
        {
            return "compiled-batch-" + bx.ToString(CultureInfo.InvariantCulture) + "-" + by.ToString(CultureInfo.InvariantCulture) + "-" +
                   bz.ToString(CultureInfo.InvariantCulture) + ".optoctrees";
        }

        /// <summary>Parses a versioned store file name.</summary>
        public static bool TryParseBatchFileName(string name, out int bx, out int by, out int bz, out uint version)
        {
            bx = by = bz = 0;
            version = 0;
            const string pre = "compiled-batch-", suf = ".optoctrees";
            if (name == null || !name.StartsWith(pre, StringComparison.Ordinal) || !name.EndsWith(suf, StringComparison.Ordinal)) return false;
            string mid = name.Substring(pre.Length, name.Length - pre.Length - suf.Length);   // bx-by-bz.vN
            int dot = mid.LastIndexOf(".v", StringComparison.Ordinal);
            if (dot < 0) return false;
            if (!uint.TryParse(mid.Substring(dot + 2), NumberStyles.None, CultureInfo.InvariantCulture, out version)) return false;
            string ids = mid.Substring(0, dot);
            // ids may be negative: split on '-' that follow a digit
            var parts = new List<string>();
            int start = 0;
            for (int i = 1; i < ids.Length; i++)
                if (ids[i] == '-' && char.IsDigit(ids[i - 1])) { parts.Add(ids.Substring(start, i - start)); start = i + 1; }
            parts.Add(ids.Substring(start));
            if (parts.Count != 3) return false;
            return int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bx) &&
                   int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out by) &&
                   int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bz);
        }

        /// <summary>A store file's path relative to the store root, as the manifest lists it (backslashes).</summary>
        public static string RelativeDrillPath(ulong storeKey, int bx, int by, int bz, uint version)
        {
            return Hex16(storeKey) + "\\" + DrillLayer + "\\" + BatchFileName(bx, by, bz, version);
        }

        public static string RelativeWindowPath(ulong storeKey, ulong chartKey, int bx, int by, int bz, uint version)
        {
            return Hex16(storeKey) + "\\" + WindowLayer + "\\" + Hex16(chartKey) + "\\" + BatchFileName(bx, by, bz, version);
        }

        /// <summary>
 /// sculptHash = fnv1a64 over <c>name TAB bytes TAB fnv1a64 x16 LF</c> of the planet data files in ordinal name order
 /// (the 12 PNGs of DE_4546B).
 /// </summary>
        public static ulong SculptHash(IList<string> names, IList<long> sizes, IList<ulong> fnvs)
        {
            if (names == null || sizes == null || fnvs == null || names.Count != sizes.Count || names.Count != fnvs.Count)
                throw new ArgumentException("names, sizes and fnvs must have the same length");
            var idx = new int[names.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => string.CompareOrdinal(names[a], names[b]));
            var sb = new StringBuilder();
            foreach (int i in idx)
                sb.Append(names[i]).Append('\t').Append(sizes[i].ToString(CultureInfo.InvariantCulture)).Append('\t').Append(Hex16(fnvs[i])).Append('\n');
            return Fnv1a64Utf8(sb.ToString());
        }

        /// <summary><see cref="SculptHash(IList{string}, IList{long}, IList{ulong})"/> of the <c>*.png</c> files of a planet data folder (read only).</summary>
        public static ulong SculptHashOfFolder(string planetDataDir)
        {
            var names = new List<string>();
            var sizes = new List<long>();
            var fnvs = new List<ulong>();
            foreach (string f in Directory.GetFiles(planetDataDir, "*.png"))
            {
                byte[] b = File.ReadAllBytes(f);
                names.Add(Path.GetFileName(f));
                sizes.Add(b.LongLength);
                fnvs.Add(LayoutHash.Fnv1a64(b));
            }
            return SculptHash(names, sizes, fnvs);
        }

        /// <summary>Whether <paramref name="path"/> lies inside <paramref name="root"/> (full paths, case-insensitive, Windows).</summary>
        public static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
 /// The store root rule (S8a.N6): refuses a root inside (or containing) any of <paramref name="forbidden"/> (both game
 /// installs, SNAppData, the SE world folder); null when acceptable, else the reason.
 /// </summary>
        public static string CheckRoot(string root, IEnumerable<string> forbidden)
        {
            if (string.IsNullOrEmpty(root)) return "no store root";
            try
            {
                if (forbidden != null)
                    foreach (string f in forbidden)
                    {
                        if (string.IsNullOrEmpty(f)) continue;
                        if (IsInside(root, f)) return "store root " + root + " is inside " + f;
                        if (IsInside(f, root)) return "store root " + root + " contains " + f;
                    }
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
            return null;
        }
    }

    /// <summary>
 /// <c>manifest.v1.txt</c> of a layer folder ("store.manifest"): line 1 <c>de-s8a-store 1 &lt;drill|window&gt; &lt;key x16&gt;
 /// &lt;overlayHash|sculptHash x16&gt;</c> (drill: storeKey and overlayHash; window: chartKey and sculptHash); then one line
 /// per served batch <c>&lt;bx&gt; &lt;by&gt; &lt;bz&gt; &lt;version&gt; &lt;bytes&gt; &lt;fnv1a64 x16&gt; &lt;flags&gt; &lt;path relative to
 /// the store root&gt;</c>, sorted by bx, by, bz; LF line ends (CRLF accepted when reading); written to a temp file then
 /// moved over the old one.
 /// </summary>
    public sealed class S8aManifest
    {
        public sealed class Entry
        {
            public int Bx, By, Bz;
            public uint Version;
            public long Bytes;
            public ulong Fnv;
            public ushort Flags;
            public string RelativePath;

            public Int3Key Id { get { return new Int3Key(Bx, By, Bz); } }

            public string ToLine()
            {
                return Bx.ToString(CultureInfo.InvariantCulture) + " " + By.ToString(CultureInfo.InvariantCulture) + " " +
                       Bz.ToString(CultureInfo.InvariantCulture) + " " + Version.ToString(CultureInfo.InvariantCulture) + " " +
                       Bytes.ToString(CultureInfo.InvariantCulture) + " " + S8aStore.Hex16(Fnv) + " " +
                       Flags.ToString(CultureInfo.InvariantCulture) + " " + RelativePath;
            }
        }

        public string Layer = S8aStore.DrillLayer;
        /// <summary>drill: storeKey; window: chartKey.</summary>
        public ulong Key;
        /// <summary>drill: overlayHash; window: sculptHash.</summary>
        public ulong Hash;
        private readonly Dictionary<Int3Key, Entry> _entries = new Dictionary<Int3Key, Entry>();

        public int Count { get { return _entries.Count; } }

        public IEnumerable<Entry> Entries { get { return _entries.Values; } }

        /// <summary>Adds or replaces the entry of its batch id.</summary>
        public void Set(Entry e)
        {
            if (e == null) throw new ArgumentNullException("e");
            if (string.IsNullOrEmpty(e.RelativePath) || e.RelativePath.IndexOf(' ') >= 0 || e.RelativePath.IndexOf('\n') >= 0)
                throw new ArgumentException("relative path must be non-empty without spaces or line breaks");
            _entries[e.Id] = e;
        }

        public bool TryGet(int bx, int by, int bz, out Entry e) { return _entries.TryGetValue(new Int3Key(bx, by, bz), out e); }

        public bool Remove(int bx, int by, int bz) { return _entries.Remove(new Int3Key(bx, by, bz)); }

        public void Clear() { _entries.Clear(); }

        public string HeaderLine()
        {
            return S8aStore.ManifestMagic + " " + S8aStore.ManifestVersion.ToString(CultureInfo.InvariantCulture) + " " + Layer + " " +
                   S8aStore.Hex16(Key) + " " + S8aStore.Hex16(Hash);
        }

        public string ToText()
        {
            var list = new List<Entry>(_entries.Values);
            list.Sort((a, b) =>
            {
                int c = a.Bx.CompareTo(b.Bx);
                if (c != 0) return c;
                c = a.By.CompareTo(b.By);
                return c != 0 ? c : a.Bz.CompareTo(b.Bz);
            });
            var sb = new StringBuilder(64 + 96 * list.Count);
            sb.Append(HeaderLine()).Append('\n');
            foreach (Entry e in list) sb.Append(e.ToLine()).Append('\n');
            return sb.ToString();
        }

        public static bool TryParse(string text, out S8aManifest m, out string error)
        {
            m = null;
            error = null;
            if (text == null) { error = "no text"; return false; }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) { error = "empty"; return false; }
            string[] h = lines[0].Split(' ');
            if (h.Length != 5 || h[0] != S8aStore.ManifestMagic || h[1] != "1" || (h[2] != S8aStore.DrillLayer && h[2] != S8aStore.WindowLayer))
            { error = "bad header: " + lines[0]; return false; }
            var r = new S8aManifest { Layer = h[2] };
            if (!ulong.TryParse(h[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out r.Key) || h[3].Length != 16 ||
                !ulong.TryParse(h[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out r.Hash) || h[4].Length != 16)
            { error = "bad header keys: " + lines[0]; return false; }
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.Length == 0) continue;
                string[] p = l.Split(' ');
                var e = new Entry();
                if (p.Length != 8 ||
                    !int.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out e.Bx) ||
                    !int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out e.By) ||
                    !int.TryParse(p[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out e.Bz) ||
                    !uint.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out e.Version) ||
                    !long.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out e.Bytes) ||
                    p[5].Length != 16 || !ulong.TryParse(p[5], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out e.Fnv) ||
                    !ushort.TryParse(p[6], NumberStyles.None, CultureInfo.InvariantCulture, out e.Flags) || p[7].Length == 0)
                { error = "bad line " + (i + 1) + ": " + l; return false; }
                e.RelativePath = p[7];
                r._entries[e.Id] = e;
            }
            m = r;
            return true;
        }

        /// <summary>Writes <c>manifest.v1.txt</c> into <paramref name="folder"/>: a temp file, then a replace (atomic on NTFS).</summary>
        public void WriteAtomic(string folder)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, S8aStore.ManifestName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToText(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch (PlatformNotSupportedException) { }
                catch (IOException) { }
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        /// <summary>Reads <c>manifest.v1.txt</c> of a folder; null + error when missing or malformed.</summary>
        public static S8aManifest ReadFrom(string folder, out string error)
        {
            error = null;
            try
            {
                string path = Path.Combine(folder, S8aStore.ManifestName);
                if (!File.Exists(path)) { error = "no manifest in " + folder; return null; }
                S8aManifest m;
                if (!TryParse(File.ReadAllText(path, Encoding.UTF8), out m, out error)) return null;
                return m;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }

    /// <summary>One dirty 16³ overlay cell of a tile: key (tile i, j, k; cell cx, cy, cz) and its content and material (x fastest).</summary>
    public sealed class S8aOverlayCell
    {
        public const int Voxels = OverlayC.CellVoxels * OverlayC.CellVoxels * OverlayC.CellVoxels;

        public byte I, J, K, Cx, Cy, Cz;
        public byte[] Content = new byte[Voxels];
        public byte[] Material = new byte[Voxels];

        /// <summary>The sort key: bytes (i, j, k, 0, cx, cy, cz, 0) read big-endian.</summary>
        public ulong Key { get { return KeyOf(I, J, K, Cx, Cy, Cz); } }

        public static ulong KeyOf(int i, int j, int k, int cx, int cy, int cz)
        {
            return ((ulong)(byte)i << 56) | ((ulong)(byte)j << 48) | ((ulong)(byte)k << 40) |
                   ((ulong)(byte)cx << 24) | ((ulong)(byte)cy << 16) | ((ulong)(byte)cz << 8);
        }
    }

    /// <summary>
 /// <c>DE_S8a_overlay.vx2</c> ("overlay.file"): raw deflate of [magic "DES8AOV1", u32 version 1, u32 siteVersion, u32
 /// cellCount, then the cells sorted by key, each u8 i, j, k, pad, u8 cx, cy, cz, pad, content[4096], material[4096]], then
 /// the u64 FNV-1a 64 of those uncompressed bytes (little-endian; the manifest's <c>overlayHash</c>). Saved by SE as the voxel
 /// snapshot <c>DE_S8a_overlay</c> (MySessionSnapshot.CompressedVoxelSnapshots, written raw as <c>&lt;name&gt;.vx2</c>).
 /// </summary>
    public static class S8aOverlayFile
    {
        public const string Magic = "DES8AOV1";
        public const string SnapshotName = "DE_S8a_overlay";
        public const string FileName = "DE_S8a_overlay.vx2";
        private const int HeaderBytes = 8 + 4 + 4 + 4;
        private const int CellBytes = 8 + 2 * S8aOverlayCell.Voxels;

        /// <summary>The uncompressed payload (cells sorted by key here; duplicate keys refused).</summary>
        public static byte[] Payload(int siteVersion, IList<S8aOverlayCell> cells)
        {
            if (cells == null) cells = new S8aOverlayCell[0];
            if (cells.Count > OverlayC.MaxCells) throw new ArgumentException("more than " + OverlayC.MaxCells + " overlay cells");
            var sorted = new List<S8aOverlayCell>(cells);
            sorted.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i].Key == sorted[i - 1].Key) throw new ArgumentException("duplicate overlay cell key " + sorted[i].Key.ToString("x16", CultureInfo.InvariantCulture));
            var buf = new byte[HeaderBytes + (long)CellBytes * sorted.Count];
            int p = 0;
            foreach (char c in Magic) buf[p++] = (byte)c;
            p = PutU32(buf, p, (uint)OverlayC.Version);
            p = PutU32(buf, p, (uint)siteVersion);
            p = PutU32(buf, p, (uint)sorted.Count);
            foreach (S8aOverlayCell c in sorted)
            {
                if (c.Content == null || c.Content.Length != S8aOverlayCell.Voxels || c.Material == null || c.Material.Length != S8aOverlayCell.Voxels)
                    throw new ArgumentException("an overlay cell needs 4,096 content and 4,096 material bytes");
                buf[p++] = c.I; buf[p++] = c.J; buf[p++] = c.K; buf[p++] = 0;
                buf[p++] = c.Cx; buf[p++] = c.Cy; buf[p++] = c.Cz; buf[p++] = 0;
                Buffer.BlockCopy(c.Content, 0, buf, p, S8aOverlayCell.Voxels);
                p += S8aOverlayCell.Voxels;
                Buffer.BlockCopy(c.Material, 0, buf, p, S8aOverlayCell.Voxels);
                p += S8aOverlayCell.Voxels;
            }
            return buf;
        }

        /// <summary>The overlay hash (FNV-1a 64 of the payload) of a cell set.</summary>
        public static ulong Hash(int siteVersion, IList<S8aOverlayCell> cells) { return LayoutHash.Fnv1a64(Payload(siteVersion, cells)); }

        /// <summary>The file bytes; <paramref name="hash"/> = the payload's FNV-1a 64.</summary>
 /// <summary>The save postfix's encoding: the fastest deflate; the hash covers the payload, so any level decodes alike (the vectors pin <see cref="Encode(int, IList{S8aOverlayCell}, out ulong)"/>'s Optimal bytes).</summary>
        public static byte[] EncodeFast(int siteVersion, IList<S8aOverlayCell> cells, out ulong hash)
        {
            return Encode(siteVersion, cells, out hash, CompressionLevel.Fastest);
        }

        public static byte[] Encode(int siteVersion, IList<S8aOverlayCell> cells, out ulong hash)
        {
            return Encode(siteVersion, cells, out hash, CompressionLevel.Optimal);
        }

        private static byte[] Encode(int siteVersion, IList<S8aOverlayCell> cells, out ulong hash, CompressionLevel level)
        {
            byte[] payload = Payload(siteVersion, cells);
            hash = LayoutHash.Fnv1a64(payload);
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, level, true)) z.Write(payload, 0, payload.Length);
                var tail = new byte[8];
                for (int i = 0; i < 8; i++) tail[i] = (byte)(hash >> (8 * i));
                ms.Write(tail, 0, 8);
                return ms.ToArray();
            }
        }

        public static byte[] Encode(int siteVersion, IList<S8aOverlayCell> cells)
        {
            ulong h;
            return Encode(siteVersion, cells, out h);
        }

        /// <summary>Decodes a file; false + error on a short file, a bad deflate stream, a hash mismatch, a bad magic or version, or a bad length.</summary>
        public static bool TryDecode(byte[] file, out int siteVersion, out List<S8aOverlayCell> cells, out ulong hash, out string error)
        {
            siteVersion = 0;
            cells = null;
            hash = 0;
            error = null;
            if (file == null || file.Length < 9) { error = "short file"; return false; }
            ulong stored = 0;
            for (int i = 0; i < 8; i++) stored |= (ulong)file[file.Length - 8 + i] << (8 * i);
            byte[] payload;
            try
            {
                using (var src = new MemoryStream(file, 0, file.Length - 8, false))
                using (var z = new DeflateStream(src, CompressionMode.Decompress))
                using (var dst = new MemoryStream())
                {
                    z.CopyTo(dst);
                    payload = dst.ToArray();
                }
            }
            catch (Exception e)
            {
                error = "inflate: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            hash = LayoutHash.Fnv1a64(payload);
            if (hash != stored) { error = "hash " + S8aStore.Hex16(hash) + " != stored " + S8aStore.Hex16(stored); return false; }
            if (payload.Length < HeaderBytes) { error = "short payload"; return false; }
            for (int i = 0; i < 8; i++)
                if (payload[i] != (byte)Magic[i]) { error = "bad magic"; return false; }
            uint version = GetU32(payload, 8);
            if (version != OverlayC.Version) { error = "version " + version; return false; }
            siteVersion = (int)GetU32(payload, 12);
            uint count = GetU32(payload, 16);
            if (count > OverlayC.MaxCells || payload.Length != HeaderBytes + (long)CellBytes * count) { error = "bad cell count " + count; return false; }
            var list = new List<S8aOverlayCell>((int)count);
            int p = HeaderBytes;
            ulong prev = 0;
            for (int n = 0; n < count; n++)
            {
                var c = new S8aOverlayCell { I = payload[p], J = payload[p + 1], K = payload[p + 2], Cx = payload[p + 4], Cy = payload[p + 5], Cz = payload[p + 6] };
                p += 8;
                Buffer.BlockCopy(payload, p, c.Content, 0, S8aOverlayCell.Voxels);
                p += S8aOverlayCell.Voxels;
                Buffer.BlockCopy(payload, p, c.Material, 0, S8aOverlayCell.Voxels);
                p += S8aOverlayCell.Voxels;
                if (n > 0 && c.Key <= prev) { error = "cells not sorted by key"; return false; }
                prev = c.Key;
                list.Add(c);
            }
            cells = list;
            return true;
        }

        private static int PutU32(byte[] b, int p, uint v)
        {
            b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); b[p + 2] = (byte)(v >> 16); b[p + 3] = (byte)(v >> 24);
            return p + 4;
        }

        private static uint GetU32(byte[] b, int p) { return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)); }
    }
}
