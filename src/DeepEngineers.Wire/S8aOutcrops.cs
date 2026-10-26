using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // I want to SEE
 // the ore, the Subnautica way"). SE's veins are voxel materials (the planet's OreMappings, the crater's pockets); the
 // window and the drill layer give them distinct Subnautica block types (S8aMaterialMap, the second cue), which read as
 // rock. Subnautica shows ore as large resource deposits (the Drillable* prefabs). So:
 // 1. SE scans its own voxels around the camera (S8aOutcropsSe, a worker): per ore detector cell
 // the LOD 1 voxels (2 m) of each ore: its volume, its exposed voxels (solid ore with an empty face
 // neighbour: where the vein meets the water, the seabed outcrop or a drilled wall) and SE's detector centroid of the
 // cell (the same formula: the centre of the LOD 2 voxels with content > 127 of that material).
 // 2. An outcrop (exposed area >= MinExposedM2) gets a deposit: anchored at the exposed voxel horizontally nearest the
 // detector centroid (so the detector's marker and the deposit agree), sized by the exposed area of its first sighting
 // and shrunk by the cube root of the volume left (the drill mines the vein: SE's storage loses the voxels, the cell is
 // scanned again on RangeChanged); gone when the vein's volume falls under RemoveFraction of its first sighting.
 // 3. SE writes the records (SN coordinates of the current chart) into the store folder (FileName, rewritten whole, at
 // most WriteHz); Subnautica reads it and keeps one Drillable per record within SpawnRadiusM of its camera
 // (S8aOreDeposits): transient, never in the save, never reported as its own deposit, never damaged by its drills.
 // The ore -> Subnautica deposit mapping is the inverse of S8aCraterOres.OreOfTechType (one member of each preimage).
 // =================================================================================================================

 /// <summary>One outcrop record (SN coordinates of the file's chart).</summary>
    public struct S8aOutcrop
    {
        public ulong Key;
        /// <summary>The SE ore subtype (S8aOutcrops.Ores).</summary>
        public string Ore;
        /// <summary>The anchor: the exposed voxel's centre (SN).</summary>
        public double X, Y, Z;
        /// <summary>The exposed surface's outward normal at the anchor (SN, unit).</summary>
        public double NX, NY, NZ;
        public double ExposedM2, VolumeM3, FirstExposedM2, FirstVolumeM3;
        /// <summary>
 /// a test run (no 20261024-033017, S8a.NO.shrink "6984 -> 6920 m3, scale 2.06 -> 2.54"): the ore within <see cref="S8aOutcrops.LocalRadiusVoxels"/>
 /// of the first anchor, now and at the first sighting; the deposit's size follows it (not written to the file).
 /// </summary>
        public double LocalM3, FirstLocalM3;
        /// <summary>SE's detector centroid of the cell for this ore (SN).</summary>
        public double DetX, DetY, DetZ;
        /// <summary>The vein was mined since its first sighting (volume below the first).</summary>
        public bool Drilled;
        /// <summary>"planet" or "tile".</summary>
        public string Zone;
        public double Scale;

        public string TechType { get { return S8aOutcrops.TechTypeOfOre(Ore); } }
    }

    /// <summary>One ore's numbers in one scanned cell (<see cref="S8aOutcrops.ScanCell"/>).</summary>
    public sealed class S8aOutcropCellOre
    {
        public int Ore;
        public int OreVoxels, ExposedVoxels;
        /// <summary>Exposed voxels packed: x | y &lt;&lt; 5 | z &lt;&lt; 10 | (empty-neighbour mask) &lt;&lt; 15 (inner coordinates 0..n-1).</summary>
        public readonly List<int> Exposed = new List<int>();
    }

    public static class S8aOutcrops
    {
        /// <summary>SE's ore detector cell: LOD 2 blocks of 8^3 voxels (32 m), the grid of the storage's own coordinates.</summary>
        public const int CellM = 32;
        /// <summary>The scan reads LOD 1 (2 m voxels): 16^3 per cell plus a one-voxel margin.</summary>
        public const int ScanLod = 1, ScanN = 16;
        public const double VoxelM = 2.0;
        /// <summary>The SE scan covers the cells within this of the camera; Subnautica keeps deposits within SpawnRadiusM, drops them beyond DespawnM.</summary>
        public const double ScanRadiusM = 112.0, SpawnRadiusM = 100.0, DespawnM = 130.0, DropCellsM = 220.0;
        /// <summary>An outcrop: at least this much exposed ore (two LOD 1 faces).</summary>
        public const double MinExposedM2 = 8.0;
        /// <summary>A deposit goes when its vein keeps less than this share of its first-sighting volume.</summary>
        public const double RemoveFraction = 0.1;
        /// <summary>Scale = clamp(sqrt(first exposed m^2) / BaseSizeM, MinScale, MaxScale) x cbrt(volume / first volume).</summary>
        public const double BaseSizeM = 4.0, MinScale = 0.6, MaxScale = 3.0;
        /// <summary>
 /// a test run (S8a.NO.shrink): the deposit stands for the ore within this many LOD 1 voxels (6 m) of its first anchor, not for the
 /// whole 32 m cell's vein (a 3 m drill hole there took 64 of 6,984 m3, 1 %, and a deposit would go only when 90 % of the cell
 /// was mined); its scale follows the cube root of that local volume left, and it goes below <see cref="RemoveFraction"/> of it.
 /// </summary>
        public const int LocalRadiusVoxels = 3;
        /// <summary>Records of one ore closer than this are one (tiles overlap: the same face seen in two tiles).</summary>
        public const double MergeM = 4.0;
        /// <summary>The deposit sits this far into the seabed along its normal.</summary>
        public const double SinkM = 0.4;
        /// <summary>The detector and the deposit agree when the horizontal (SN x, z) distance is at most this (undrilled outcrops).</summary>
        public const double AgreeM = 5.0;
        public const double WriteHz = 2.0;
        public const string FileName = "ore-outcrops.tsv";
        public const string Magic = "de-s8a-outcrops 1";

        /// <summary>The SE ore subtypes (the planet's OreMappings Types; S8aMaterialMap's ore rows).</summary>
        public static readonly string[] Ores = { "Iron_02", "Nickel_01", "Silicon_01", "Cobalt_01", "Magnesium_01", "Silver_01", "Gold_01", "Platinum_01", "Uraninite_01" };
        /// <summary>
 /// Subnautica's large deposit per ore, the inverse of
 /// S8aCraterOres.OreOfTechType: Iron titanium (the grey metal), Nickel nickel, Silicon quartz, Cobalt lead, Magnesium
 /// lithium, Silver silver, Gold gold, Platinum diamond, Uranium uraninite (DrillableUranium).
 /// </summary>
        public static readonly string[] TechTypes = { "DrillableTitanium", "DrillableNickel", "DrillableQuartz", "DrillableLead", "DrillableLithium", "DrillableSilver",
                                                      "DrillableGold", "DrillableDiamond", "DrillableUranium" };

        public static int OreIndex(string subtype)
        {
            for (int i = 0; i < Ores.Length; i++) if (string.Equals(Ores[i], subtype, StringComparison.Ordinal)) return i;
            return -1;
        }

        public static string TechTypeOfOre(string subtype)
        {
            int i = OreIndex(subtype);
            return i < 0 ? null : TechTypes[i];
        }

        /// <summary>&lt;root&gt;\&lt;storeKey x16&gt;\ore-outcrops.tsv.</summary>
        public static string PathOf(string root, ulong storeKey) { return Path.Combine(S8aStore.StoreFolder(root, storeKey), FileName); }

        /// <summary>The record key: fnv1a64 of the voxel body, the cell and the ore.</summary>
        public static ulong Key(long body, int cx, int cy, int cz, int ore)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (long v in new[] { body, cx, cy, cz, (long)ore })
                    for (int b = 0; b < 8; b++) { h ^= (byte)(v >> (8 * b)); h *= 1099511628211UL; }
                return h;
            }
        }

        /// <summary>The deposit's scale (see <see cref="BaseSizeM"/>); 0 when it is not shown.</summary>
        public static double ScaleOf(double firstExposedM2, double volumeM3, double firstVolumeM3)
        {
            if (!Shown(firstExposedM2, volumeM3, firstVolumeM3)) return 0.0;
            double a = Math.Max(MinScale, Math.Min(MaxScale, Math.Sqrt(firstExposedM2) / BaseSizeM));
            double f = firstVolumeM3 > 0 ? Math.Max(0.0, Math.Min(1.0, volumeM3 / firstVolumeM3)) : 1.0;
            return a * Math.Pow(f, 1.0 / 3.0);
        }

        public static bool Shown(double exposedM2, double volumeM3, double firstVolumeM3)
        {
            if (exposedM2 < MinExposedM2 || volumeM3 <= 0) return false;
            return firstVolumeM3 <= 0 || volumeM3 >= RemoveFraction * firstVolumeM3;
        }

        /// <summary>
 /// One cell of LOD 1 voxels with a one-voxel margin: arrays of (n + 2)^3, index x + (n + 2)(y + (n + 2) z), content
 /// (> 127 solid) and SE material index; <paramref name="oreOf"/> maps a material index to the ore (−1 none). Per ore:
 /// its solid voxels in the inner n^3 and the exposed ones (a face neighbour, margin included, with content &lt;= 127).
 /// A margin voxel outside the body must be given solid by the caller (an edge of a tile is no exposure).
 /// </summary>
        public static List<S8aOutcropCellOre> ScanCell(byte[] content, byte[] material, int n, sbyte[] oreOf)
        {
            var res = new List<S8aOutcropCellOre>();
            if (content == null || material == null || oreOf == null) return res;
            int s = n + 2;
            if (content.Length < s * s * s || material.Length < s * s * s) throw new ArgumentException("arrays of (n + 2)^3 expected");
            var byOre = new S8aOutcropCellOre[Ores.Length];
            int[] step = { 1, -1, s, -s, s * s, -s * s };
            for (int z = 1; z <= n; z++)
                for (int y = 1; y <= n; y++)
                    for (int x = 1; x <= n; x++)
                    {
                        int i = x + s * (y + s * z);
                        if (content[i] <= 127) continue;
                        int ore = oreOf[material[i]];
                        if (ore < 0 || ore >= Ores.Length) continue;
                        S8aOutcropCellOre o = byOre[ore];
                        if (o == null) { o = new S8aOutcropCellOre { Ore = ore }; byOre[ore] = o; res.Add(o); }
                        o.OreVoxels++;
                        int mask = 0;
                        for (int d = 0; d < 6; d++) if (content[i + step[d]] <= 127) mask |= 1 << d;
                        if (mask == 0) continue;
                        o.ExposedVoxels++;
                        o.Exposed.Add((x - 1) | ((y - 1) << 5) | ((z - 1) << 10) | (mask << 15));
                    }
            return res;
        }

        /// <summary>
 /// The ore voxels of <paramref name="ore"/> within <paramref name="r"/> voxels (a sphere) of inner voxel (px, py, pz) in
 /// <see cref="ScanCell"/>'s arrays ((n + 2)^3 with the one-voxel margin; the margin counts too).
 /// </summary>
        public static int LocalOreVoxels(byte[] content, byte[] material, int n, sbyte[] oreOf, int ore, int px, int py, int pz, int r)
        {
            int s = n + 2, count = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx * dx + dy * dy + dz * dz > r * r) continue;
                        int x = px + 1 + dx, y = py + 1 + dy, z = pz + 1 + dz;
                        if (x < 0 || y < 0 || z < 0 || x >= s || y >= s || z >= s) continue;
                        int i = x + s * (y + s * z);
                        if (content[i] <= 127) continue;
                        int o = oreOf[material[i]];
                        if (o == ore) count++;
                    }
            return count;
        }

        /// <summary>The vein was mined since its first sighting: at least one LOD 1 voxel of ore less, in the cell or near the first anchor.</summary>
        public static bool MinedSince(double volumeM3, double firstVolumeM3, double localM3, double firstLocalM3)
        {
            double voxel = VoxelM * VoxelM * VoxelM;
            return volumeM3 <= firstVolumeM3 - voxel + 1e-6 || localM3 <= firstLocalM3 - voxel + 1e-6;
        }

        /// <summary>The empty-neighbour mask's outward direction (+x, −x, +y, −y, +z, −z bits), unnormalised.</summary>
        public static void MaskDirection(int mask, out int dx, out int dy, out int dz)
        {
            dx = ((mask & 1) != 0 ? 1 : 0) - ((mask & 2) != 0 ? 1 : 0);
            dy = ((mask & 4) != 0 ? 1 : 0) - ((mask & 8) != 0 ? 1 : 0);
            dz = ((mask & 16) != 0 ? 1 : 0) - ((mask & 32) != 0 ? 1 : 0);
        }

        /// <summary>Horizontal (SN x, z) distance.</summary>
        public static double HorizontalM(double x0, double z0, double x1, double z1)
        {
            double dx = x1 - x0, dz = z1 - z0;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Records of one ore closer than <see cref="MergeM"/> are one: the larger exposure stays (input order otherwise).</summary>
        public static List<S8aOutcrop> Merge(IList<S8aOutcrop> recs)
        {
            var order = new List<S8aOutcrop>(recs);
            order.Sort((a, b) => b.ExposedM2.CompareTo(a.ExposedM2) != 0 ? b.ExposedM2.CompareTo(a.ExposedM2) : a.Key.CompareTo(b.Key));
            var kept = new List<S8aOutcrop>(order.Count);
            foreach (S8aOutcrop r in order)
            {
                bool dup = false;
                foreach (S8aOutcrop k in kept)
                {
                    if (!string.Equals(k.Ore, r.Ore, StringComparison.Ordinal)) continue;
                    double dx = k.X - r.X, dy = k.Y - r.Y, dz = k.Z - r.Z;
                    if (dx * dx + dy * dy + dz * dz < MergeM * MergeM) { dup = true; break; }
                }
                if (!dup) kept.Add(r);
            }
            return kept;
        }

        private static string F(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }

        /// <summary>
 /// The file: line 1 "de-s8a-outcrops 1 &lt;seq&gt; &lt;chartSeq&gt; &lt;chartKey x16&gt;", then one record per line,
 /// tab-separated: key x16, ore, x, y, z, nx, ny, nz, exposedM2, volumeM3, firstExposedM2, firstVolumeM3, detX, detY, detZ,
 /// drilled (0/1), zone, scale (invariant, 2 decimals; the normal 3).
 /// </summary>
        public static string Format(long seq, uint chartSeq, ulong chartKey, IList<S8aOutcrop> recs)
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append(' ').Append(seq.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(chartSeq.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(chartKey.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            foreach (S8aOutcrop r in recs)
            {
                sb.Append(r.Key.ToString("x16", CultureInfo.InvariantCulture)).Append('\t').Append(r.Ore).Append('\t').Append(F(r.X)).Append('\t').Append(F(r.Y)).Append('\t').Append(F(r.Z))
                  .Append('\t').Append(r.NX.ToString("0.000", CultureInfo.InvariantCulture)).Append('\t').Append(r.NY.ToString("0.000", CultureInfo.InvariantCulture))
                  .Append('\t').Append(r.NZ.ToString("0.000", CultureInfo.InvariantCulture)).Append('\t').Append(F(r.ExposedM2)).Append('\t').Append(F(r.VolumeM3))
                  .Append('\t').Append(F(r.FirstExposedM2)).Append('\t').Append(F(r.FirstVolumeM3)).Append('\t').Append(F(r.DetX)).Append('\t').Append(F(r.DetY))
                  .Append('\t').Append(F(r.DetZ)).Append('\t').Append(r.Drilled ? '1' : '0').Append('\t').Append(r.Zone ?? "planet").Append('\t').Append(F(r.Scale)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Parses <see cref="Format"/>'s text; false when the first line is not this format (records with bad fields are skipped).</summary>
        public static bool TryParse(string text, out long seq, out uint chartSeq, out ulong chartKey, out List<S8aOutcrop> recs)
        {
            seq = 0;
            chartSeq = 0;
            chartKey = 0;
            recs = new List<S8aOutcrop>();
            if (string.IsNullOrEmpty(text)) return false;
            string[] lines = text.Split('\n');
            string[] h = lines[0].TrimEnd('\r').Split(' ');
            if (h.Length < 5 || h[0] + " " + h[1] != Magic) return false;
            if (!long.TryParse(h[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq) ||
                !uint.TryParse(h[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out chartSeq) ||
                !ulong.TryParse(h[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out chartKey)) return false;
            for (int li = 1; li < lines.Length; li++)
            {
                string[] f = lines[li].TrimEnd('\r').Split('\t');
                if (f.Length < 18) continue;
                var r = new S8aOutcrop();
                double[] v = new double[13];
                bool ok = ulong.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r.Key) && OreIndex(f[1]) >= 0;
                for (int k = 0; k < 13 && ok; k++) ok = double.TryParse(f[2 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
                double scale = 0;
                ok = ok && double.TryParse(f[17], NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
                if (!ok) continue;
                r.Ore = f[1];
                r.X = v[0]; r.Y = v[1]; r.Z = v[2]; r.NX = v[3]; r.NY = v[4]; r.NZ = v[5];
                r.ExposedM2 = v[6]; r.VolumeM3 = v[7]; r.FirstExposedM2 = v[8]; r.FirstVolumeM3 = v[9];
                r.DetX = v[10]; r.DetY = v[11]; r.DetZ = v[12];
                r.Drilled = f[15] == "1";
                r.Zone = f[16];
                r.Scale = scale;
                recs.Add(r);
            }
            return true;
        }
    }
}
