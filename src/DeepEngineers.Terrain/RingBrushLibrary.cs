using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace DeepEngineers.Terrain
{
    /// <summary>One brush: its signed distance field [z, y, x] in 1/16 m (int8, clamped at ±7.9 m), its index row.</summary>
    public sealed class RingBrush
    {
        public int Id, Size;
        public string Biome, Class;
        /// <summary>The brush's ground, voxels from its bottom (the index's 0.01 m value).</summary>
        public double Ground;
        /// <summary>Its origin in the Subnautica map (0.1 m; where it was taken from).</summary>
        public double OriginX, OriginY, OriginZ;
        public sbyte[] Sdf;
    }

    /// <summary>
 /// S8a Stage F2: the brush library as derived files (tools/planet/s8a_brushes.py build; never the install's data): the
 /// library (<c>library-v1.bin</c>: "DEBRUSH1", u16 version, u32 count, per brush u32 id, u16 size, u32 length,
 /// zlib(int8 sdf + uint8 types)) and the index for C# (<c>index-v1.tsv</c>, s8a_ring3d.write_index_tsv: the landmark
 /// roots and per brush id, size, biome, class, ground in 0.01 m, origin in 0.1 m). Read-only after <see cref="Load"/>.
 /// </summary>
    public sealed class RingBrushLibrary
    {
        public sealed class Landmark { public double X, Y, Z; public string Pattern; }

        public readonly List<RingBrush> Brushes = new List<RingBrush>();
        public readonly Dictionary<int, RingBrush> ById = new Dictionary<int, RingBrush>();
        public readonly List<Landmark> Landmarks = new List<Landmark>();
        public double LandmarkRadiusM = 48.0, DeepLimitY = -600.0;
        public ulong BinFnv, IndexFnv;
        public long DecodedBytes;

        public const string BinName = "library-v1.bin", IndexName = "index-v1.tsv";

        /// <summary>%LOCALAPPDATA%\DeepEngineers\terrain\brushes (s8a_brushes.store_dir).</summary>
        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", "terrain", "brushes");
        }

        public static RingBrushLibrary Load(string binPath, string indexPath)
        {
            var lib = new RingBrushLibrary();
            byte[] idx = File.ReadAllBytes(indexPath);
            lib.IndexFnv = RingHash.Fnv(idx);
            var rows = new Dictionary<int, RingBrush>();
            string[] lines = System.Text.Encoding.UTF8.GetString(idx).Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith("de-brush-index ", StringComparison.Ordinal)) throw new InvalidDataException("not a brush index: " + indexPath);
            foreach (string raw in lines)
            {
                string ln = raw.TrimEnd('\r');
                if (ln.StartsWith("landmarkRadiusM ", StringComparison.Ordinal)) lib.LandmarkRadiusM = int.Parse(ln.Substring(16), CultureInfo.InvariantCulture);
                else if (ln.StartsWith("deepLimitY ", StringComparison.Ordinal)) lib.DeepLimitY = int.Parse(ln.Substring(11), CultureInfo.InvariantCulture);
                else if (ln.StartsWith("landmark\t", StringComparison.Ordinal))
                {
                    string[] f = ln.Split('\t');
                    lib.Landmarks.Add(new Landmark { X = I(f[1]) / 10.0, Y = I(f[2]) / 10.0, Z = I(f[3]) / 10.0, Pattern = f.Length > 4 ? f[4] : "" });
                }
                else if (ln.StartsWith("brush\t", StringComparison.Ordinal))
                {
                    string[] f = ln.Split('\t');
                    var b = new RingBrush
                    {
                        Id = I(f[1]), Size = I(f[2]), Biome = f[3], Class = f[4], Ground = I(f[5]) / 100.0,
                        OriginX = I(f[6]) / 10.0, OriginY = I(f[7]) / 10.0, OriginZ = I(f[8]) / 10.0
                    };
                    rows[b.Id] = b;
                }
            }
            byte[] bin = File.ReadAllBytes(binPath);
            lib.BinFnv = RingHash.Fnv(bin);
            if (bin.Length < 14 || System.Text.Encoding.ASCII.GetString(bin, 0, 8) != "DEBRUSH1") throw new InvalidDataException("not a brush library: " + binPath);
            int count = BitConverter.ToInt32(bin, 10), off = 14;
            for (int k = 0; k < count; k++)
            {
                int id = BitConverter.ToInt32(bin, off);
                int size = BitConverter.ToUInt16(bin, off + 4);
                int len = BitConverter.ToInt32(bin, off + 6);
                off += 10;
                RingBrush b;
                if (!rows.TryGetValue(id, out b)) throw new InvalidDataException("brush " + id + " is not in the index " + indexPath);
                if (b.Size != size) throw new InvalidDataException("brush " + id + ": size " + size + " in the library, " + b.Size + " in the index");
                int m = size * size * size;
                var sdf = new sbyte[m];
                using (var ms = new MemoryStream(bin, off + 2, len - 2))
                using (var inflate = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    byte[] tmp = new byte[m];
                    int got = 0;
                    while (got < m)
                    {
                        int n = inflate.Read(tmp, got, m - got);
                        if (n <= 0) throw new InvalidDataException("brush " + id + ": its field ends early");
                        got += n;
                    }
                    Buffer.BlockCopy(tmp, 0, sdf, 0, m);
                }
                b.Sdf = sdf;
                lib.DecodedBytes += m;
                off += len;
            }
            foreach (RingBrush b in rows.Values)
                if (b.Sdf == null) throw new InvalidDataException("brush " + b.Id + " is in the index but not in the library");
            lib.Brushes.AddRange(rows.Values);
            lib.Brushes.Sort((a, c) => a.Id.CompareTo(c.Id));
            foreach (RingBrush b in lib.Brushes) lib.ById[b.Id] = b;
            return lib;
        }

        private static int I(string s) { return int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }

        /// <summary>S8a.F.forbidden (s8a_brushes.verify): brushes whose box lies within LandmarkRadiusM of a landmark root or below DeepLimitY.</summary>
        public List<int> Forbidden()
        {
            var bad = new List<int>();
            double r2 = LandmarkRadiusM * LandmarkRadiusM;
            foreach (RingBrush b in Brushes)
            {
                bool near = b.OriginY < DeepLimitY;
                foreach (Landmark l in Landmarks)
                {
                    if (near) break;
                    double dx = Math.Max(Math.Max(b.OriginX - l.X, 0.0), l.X - (b.OriginX + b.Size));
                    double dy = Math.Max(Math.Max(b.OriginY - l.Y, 0.0), l.Y - (b.OriginY + b.Size));
                    double dz = Math.Max(Math.Max(b.OriginZ - l.Z, 0.0), l.Z - (b.OriginZ + b.Size));
                    near = dx * dx + dy * dy + dz * dz <= r2;
                }
                if (near) bad.Add(b.Id);
            }
            return bad;
        }
    }
}
