using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeepEngineers.Terrain
{
    /// <summary>One ring biome's stamping rule: density per mille of a stamp per cell, the classes it takes, its 3D rules.</summary>
    public sealed class RingRule
    {
        public string Key;
        public int DensityPermille;
        public string[] Classes;
        public bool Caves, Arches;
    }

    /// <summary>
 /// S8a Stage F2: the ring field's rules and constants, the same table as tools/planet/s8a_ring3d.py (RULES and the
 /// constants above it); <see cref="Text"/> is the canonical text whose FNV the vectors carry (rules_text), so a change on
 /// one side only fails RingFieldTests.
 /// </summary>
    public static class RingRules
    {
        public const int FieldVersion = 2;
        public const long Seed = 4546;
        public const int Res = 2048;
        public const double Inner = 57000.0, HeightRatio = 10200.0, Rs = 60000.00390625;
        public const double CellM = 96.0, RepeatM = 300.0, FalloffM = 6.0, SinkM = 1.0;
        public const int Scale32Lo = 900, Scale32Hi = 1150, Scale64Lo = 900, Scale64Hi = 2000;
        public const double RhoMin = 3800.0, RhoMax = 12000.0, HalfM = 12600.0, StampAboveY = -6.0, GridM = 128.0;
        public const int Disc = 550;
        /// <summary>The 3D rules fade in over SN radius FadeIn0-FadeIn1 and out over FadeOut0-FadeOut1 (outside, the field is the heightmap).</summary>
        public const double FadeIn0 = 3600.0, FadeIn1 = 3800.0, FadeOut0 = 12000.0, FadeOut1 = 12200.0;
        // cave / arch: wavelength, octaves, threshold per mille, gain, band in / full / out / end (m under / over the seabed)
        public const double CaveWavelength = 48.0, CaveGain = 40.0, CaveB0 = 4.0, CaveB1 = 10.0, CaveB2 = 90.0, CaveB3 = 130.0;
        public const int CaveOctaves = 2, CaveThreshold = 130;
        public const double ArchWavelength = 90.0, ArchGain = 60.0, ArchB0 = 6.0, ArchB1 = 12.0, ArchB2 = 32.0, ArchB3 = 42.0;
        public const int ArchOctaves = 2, ArchThreshold = 860;

        public static readonly RingRule[] Rules =
        {
            R("safeShallows", 300, "boulder,floor", ""),
            R("kelpForest", 400, "wall,boulder,overhang", ""),
            R("grassyPlateaus", 300, "wall,floor,overhang", ""),
            R("mushroomForest", 600, "boulder,pillar", ""),
            R("sparseReef", 450, "boulder,pillar,overhang", ""),
            R("grandReef", 500, "overhang,pillar,wall,cave", "arches"),
            R("dunes", 40, "boulder", ""),
            R("mountains", 400, "wall,boulder,overhang", ""),
            R("underwaterIslands", 500, "pillar,overhang,wall", ""),
            R("bloodKelp", 450, "cave,overhang,wall", "caves"),
            R("crashZone", 200, "boulder,floor", ""),
            R("lostRiver", 500, "cave,wall", "caves"),
            R("craterEdge", 0, "", ""),
        };

        /// <summary>The planet's biome map names (G - 1; sculpt_de4546b.BIOME_NAMES) and the rule key each stands for.</summary>
        public static readonly string[] BiomeNames = { "safeShallows", "grassyPlateaus", "kelpForest", "dunes", "bloodKelp", "sparseReef", "mountains", "void",
                                                       "mushroomForest", "grandReef", "underwaterIslands", "crashZone", "lostRiver_Canyon" };
        public static readonly string[] Keys = BuildKeys();
        /// <summary>The rule of each key index (null without one).</summary>
        public static readonly RingRule[] RuleOfKey = BuildRuleOfKey();

        private static RingRule R(string key, int d, string classes, string features)
        {
            return new RingRule
            {
                Key = key, DensityPermille = d, Classes = classes.Length == 0 ? new string[0] : classes.Split(','),
                Caves = features.Contains("caves"), Arches = features.Contains("arches")
            };
        }

        private static string[] BuildKeys()
        {
            var k = new string[BiomeNames.Length];
            for (int i = 0; i < k.Length; i++) k[i] = BiomeNames[i] == "lostRiver_Canyon" ? "lostRiver" : BiomeNames[i] == "void" ? "craterEdge" : BiomeNames[i];
            return k;
        }

        private static RingRule[] BuildRuleOfKey()
        {
            var r = new RingRule[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
                foreach (RingRule rule in Rules)
                    if (rule.Key == Keys[i]) r[i] = rule;
            return r;
        }

        private static string I(double v) { return ((long)v).ToString(CultureInfo.InvariantCulture); }

        /// <summary>The canonical rules text (s8a_ring3d.rules_text).</summary>
        public static string Text(long seed)
        {
            var sb = new StringBuilder();
            sb.Append("de-ring-rules ").Append(FieldVersion).Append('\n');
            sb.Append("seed ").Append(seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("cellM ").Append(I(CellM)).Append('\n');
            sb.Append("repeatM ").Append(I(RepeatM)).Append('\n');
            sb.Append("falloffM ").Append(I(FalloffM)).Append('\n');
            sb.Append("sinkM ").Append(I(SinkM)).Append('\n');
            sb.Append("scale32 ").Append(Scale32Lo).Append(' ').Append(Scale32Hi).Append('\n');
            sb.Append("scale64 ").Append(Scale64Lo).Append(' ').Append(Scale64Hi).Append('\n');
            sb.Append("rho ").Append(I(RhoMin)).Append(' ').Append(I(RhoMax)).Append('\n');
            sb.Append("halfM ").Append(I(HalfM)).Append('\n');
            sb.Append("stampAboveY ").Append(I(StampAboveY)).Append('\n');
            sb.Append("disc ").Append(Disc).Append('\n');
            sb.Append("gridM ").Append(I(GridM)).Append('\n');
            sb.Append("fade ").Append(I(FadeIn0)).Append(' ').Append(I(FadeIn1)).Append(' ').Append(I(FadeOut0)).Append(' ').Append(I(FadeOut1)).Append('\n');
            sb.Append("cave ").Append(I(CaveWavelength)).Append(' ').Append(CaveOctaves).Append(' ').Append(CaveThreshold).Append(' ').Append(I(CaveGain)).Append(' ')
              .Append(I(CaveB0)).Append(' ').Append(I(CaveB1)).Append(' ').Append(I(CaveB2)).Append(' ').Append(I(CaveB3)).Append('\n');
            sb.Append("arch ").Append(I(ArchWavelength)).Append(' ').Append(ArchOctaves).Append(' ').Append(ArchThreshold).Append(' ').Append(I(ArchGain)).Append(' ')
              .Append(I(ArchB0)).Append(' ').Append(I(ArchB1)).Append(' ').Append(I(ArchB2)).Append(' ').Append(I(ArchB3)).Append('\n');
            foreach (RingRule r in Rules)
            {
                var f = new List<string>();
                if (r.Caves) f.Add("caves");
                if (r.Arches) f.Add("arches");
                sb.Append("rule ").Append(r.Key).Append(' ').Append(r.DensityPermille).Append(' ').Append(r.Classes.Length == 0 ? "-" : string.Join(",", r.Classes))
                  .Append(' ').Append(f.Count == 0 ? "-" : string.Join(",", f.ToArray())).Append('\n');
            }
            for (int g = 0; g < BiomeNames.Length; g++) sb.Append("biome ").Append(g + 1).Append(' ').Append(BiomeNames[g]).Append(' ').Append(Keys[g]).Append('\n');
            return sb.ToString();
        }

        public static ulong TextHash(long seed) { return RingHash.FnvUtf8(Text(seed)); }
    }

    /// <summary>
 /// The ring's base: SE's face-1 heightmap (16-bit, row-major, 2048²) and biome map (the _mat G channel) of the deployed world
 /// mod, sampled per Subnautica column (s8a_ring3d.Base). Read-only.
 /// </summary>
    public sealed class RingBase
    {
        private const double S3 = -1.0 / 6.0, S5 = 1.0 / 120.0, S7 = -1.0 / 5040.0, S9 = 1.0 / 362880.0, S11 = -1.0 / 39916800.0, S13 = 1.0 / 6227020800.0, S15 = -1.0 / 1307674368000.0;
        private const double C2 = -0.5, C4 = 1.0 / 24.0, C6 = -1.0 / 720.0, C8 = 1.0 / 40320.0, C10 = -1.0 / 3628800.0, C12 = 1.0 / 479001600.0, C14 = -1.0 / 87178291200.0, C16 = 1.0 / 20922789888000.0;

        private readonly double[] _hv;
        private readonly byte[] _g;
        private readonly int _res;
        public readonly ulong HeightsFnv, BiomesFnv;

        public RingBase(ushort[] heights, byte[] biomeG, int res = RingRules.Res)
        {
            if (heights == null || heights.Length != res * res || biomeG == null || biomeG.Length != res * res) throw new ArgumentException("face 1 maps of " + res + "² expected");
            _res = res;
            _hv = new double[heights.Length];
            for (int i = 0; i < heights.Length; i++) _hv[i] = heights[i] / 65535.0;
            _g = (byte[])biomeG.Clone();
            var hb = new byte[heights.Length * 2];
            Buffer.BlockCopy(heights, 0, hb, 0, hb.Length);
            HeightsFnv = RingHash.Fnv(hb);
            BiomesFnv = RingHash.Fnv(_g);
        }

        /// <summary>The deployed world mod's face 1 (back.png, back_mat.png) of a planet data folder.</summary>
        public static RingBase FromPlanetDir(string planetDataDir)
        {
            ushort[] h = RingPng.Gray16(RingPng.Read(Path.Combine(planetDataDir, "back.png")));
            byte[] g = RingPng.Channel(RingPng.Read(Path.Combine(planetDataDir, "back_mat.png")), 1);
            return new RingBase(h, g);
        }

        public static void SinCos(double t, out double s, out double c)
        {
            double t2 = t * t;
            s = t * (1.0 + t2 * (S3 + t2 * (S5 + t2 * (S7 + t2 * (S9 + t2 * (S11 + t2 * (S13 + t2 * S15)))))));
            c = 1.0 + t2 * (C2 + t2 * (C4 + t2 * (C6 + t2 * (C8 + t2 * (C10 + t2 * (C12 + t2 * (C14 + t2 * C16)))))));
        }

        /// <summary>Face-1 pixel coordinates (column, row) of an SN column (s8a_ring3d.texel).</summary>
        public static void Texel(double x, double z, out double px, out double py)
        {
            double rho = Math.Sqrt(x * x + z * z);
            double s, c;
            SinCos(rho / RingRules.Rs, out s, out c);
            double ex = rho > 0 ? -x / rho : 0.0;
            double ez = rho > 0 ? -z / rho : 0.0;
            double dx = ex * s, dy = ez * s, dz = c;
            double tx = dx / dz;
            double ty = -dy / dz;
            px = (tx + 1.0) * 0.5 * (RingRules.Res - 1.0);
            py = (ty + 1.0) * 0.5 * (RingRules.Res - 1.0);
        }

        /// <summary>The seabed (SN y) and the rule key index (or -1) of an SN column (NaN, -1 off the face's interior).</summary>
        public void Column(double x, double z, out double h, out int key)
        {
            double px, py;
            Texel(x, z, out px, out py);
            ColumnPx(px, py, out h, out key);
        }

        /// <summary>
 /// The seabed (SN y) and the rule key index at face-1 pixel coordinates (column, row): the Catmull-Rom of <see cref="Column"/>
 /// after its texel (S8a sample: s8a_chunks.column_px, the pixel coordinates from a planet-local direction).
 /// </summary>
        public void ColumnPx(double px, double py, out double h, out int key)
        {
            double fi = Math.Floor(px), fj = Math.Floor(py);
            if (!(fi >= 1 && fj >= 1 && fi <= _res - 3 && fj <= _res - 3)) { h = double.NaN; key = -1; return; }
            int i0 = (int)fi, j0 = (int)fj;
            double ux = px - fi, uy = py - fj;
            double ux2 = ux * ux, ux3 = ux * ux * ux, uy2 = uy * uy, uy3 = uy * uy * uy;
            double wx0 = -0.5 * ux3 + ux2 - 0.5 * ux, wx1 = 1.5 * ux3 - 2.5 * ux2 + 1.0, wx2 = -1.5 * ux3 + 2.0 * ux2 + 0.5 * ux, wx3 = 0.5 * ux3 - 0.5 * ux2;
            double wy0 = -0.5 * uy3 + uy2 - 0.5 * uy, wy1 = 1.5 * uy3 - 2.5 * uy2 + 1.0, wy2 = -1.5 * uy3 + 2.0 * uy2 + 0.5 * uy, wy3 = 0.5 * uy3 - 0.5 * uy2;
            int b = (j0 - 1) * _res + i0 - 1;
            double r0 = wx0 * _hv[b] + wx1 * _hv[b + 1] + wx2 * _hv[b + 2] + wx3 * _hv[b + 3];
            b += _res;
            double r1 = wx0 * _hv[b] + wx1 * _hv[b + 1] + wx2 * _hv[b + 2] + wx3 * _hv[b + 3];
            b += _res;
            double r2 = wx0 * _hv[b] + wx1 * _hv[b + 1] + wx2 * _hv[b + 2] + wx3 * _hv[b + 3];
            b += _res;
            double r3 = wx0 * _hv[b] + wx1 * _hv[b + 1] + wx2 * _hv[b + 2] + wx3 * _hv[b + 3];
            double hh = wy0 * r0 + wy1 * r1 + wy2 * r2 + wy3 * r3;
            h = RingRules.Inner + hh * RingRules.HeightRatio - RingRules.Rs;
            int g = _g[(int)(py + 0.5) * _res + (int)(px + 0.5)];
            key = g >= 1 && g <= RingRules.Keys.Length ? g - 1 : -1;
        }
    }

    /// <summary>One stamp of the ring: a brush placed at an SN column (s8a_ring3d placement dict).</summary>
    public sealed class RingPlacement
    {
        public int Index;
        public double X, Z, H, Y0, Cos, Sin, Scale, Ground;
        public bool Mirror;
        public RingBrush Brush;
        public string Biome;
        public double RadiusM { get { return Brush.Size * 0.5 * Scale; } }
    }

    /// <summary>
 /// S8a Stage F2: the ring's 3D field, f(x, y, z) in metres positive in rock over Subnautica coordinates (site v1): the
 /// twin of tools/planet/s8a_ring3d.py (Ring3D: the base, the 3D rules, the stamping), a deterministic function of the
 /// face-1 maps, the brush library and the seed; <see cref="Place"/> once, then <see cref="Sample(double, double, double)"/>
 /// from any thread. Plain IEEE double arithmetic in the twin's order (vectors: tests/s8a/vectors/ring3d/).
 /// </summary>
    public sealed class RingField
    {
        public readonly RingBase Base;
        public readonly RingBrushLibrary Library;
        public readonly long Seed;
        public readonly List<RingPlacement> Placements = new List<RingPlacement>();
        private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
        private readonly RingNoise _cave1, _cave2, _arch;
        /// <summary>How far above / below the seabed anything of the field reaches (stamps at their scale, the 3D rules).</summary>
        public double ReachAboveM { get; private set; }
        public double ReachBelowM { get; private set; }
        public double PlaceMs { get; private set; }

        public RingField(RingBase b, RingBrushLibrary lib, long seed = RingRules.Seed)
        {
            Base = b;
            Library = lib;
            Seed = seed;
            _cave1 = new RingNoise(seed, "ring3d/cave1", RingRules.CaveOctaves);
            _cave2 = new RingNoise(seed, "ring3d/cave2", RingRules.CaveOctaves);
            _arch = new RingNoise(seed, "ring3d/arch", RingRules.ArchOctaves);
            ReachAboveM = RingRules.ArchB3 + 1.0;
            ReachBelowM = RingRules.CaveB3 + 1.0;
        }

        private static long GridKey(int gx, int gz) { return ((long)gx << 32) ^ (uint)gz; }

        // ------------------------------------------------------------------------------------------ placements

 /// <summary>The placements over the ring: cells in row order (x outer, z inner) (s8a_ring3d.Ring3D.place).</summary>
        public void Place()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Placements.Clear();
            _grid.Clear();
            int n = (int)Math.Ceiling(RingRules.HalfM / RingRules.CellM);
            int cellR = (int)Math.Ceiling(RingRules.RepeatM / RingRules.CellM);
            var cells = new Dictionary<long, List<int>>();
            var near = new HashSet<int>();
            var pools = new Dictionary<int, List<RingBrush>>();
            for (int a = -n; a < n; a++)
                for (int b = -n; b < n; b++)
                {
                    double jx = RingHash.U(Seed, a, b, 1), jz = RingHash.U(Seed, a, b, 2);
                    double x = (a + 0.2 + 0.6 * jx) * RingRules.CellM;
                    double z = (b + 0.2 + 0.6 * jz) * RingRules.CellM;
                    double r = Math.Sqrt(x * x + z * z);
                    if (!(r >= RingRules.RhoMin && r <= RingRules.RhoMax)) continue;
                    double h;
                    int key;
                    Base.Column(x, z, out h, out key);
                    RingRule rule = key >= 0 ? RingRules.RuleOfKey[key] : null;
                    if (rule == null || rule.Classes.Length == 0 || RingHash.U(Seed, a, b, 3) >= rule.DensityPermille / 1000.0 || h > RingRules.StampAboveY) continue;
                    string biome = RingRules.Keys[key];
                    List<RingBrush> pool;
                    if (!pools.TryGetValue(key, out pool)) pools[key] = pool = PoolOf(rule, biome);
                    if (pool.Count == 0) continue;
                    int off = (int)(RingHash.U(Seed, a, b, 7) * pool.Count);
                    near.Clear();
                    for (int dx = -cellR; dx <= cellR; dx++)
                        for (int dz = -cellR; dz <= cellR; dz++)
                        {
                            List<int> l;
                            if (!cells.TryGetValue(GridKey(a + dx, b + dz), out l)) continue;
                            foreach (int j in l)
                            {
                                RingPlacement p = Placements[j];
                                double ddx = p.X - x, ddz = p.Z - z;
                                if (Math.Sqrt(ddx * ddx + ddz * ddz) < RingRules.RepeatM) near.Add(p.Brush.Id);
                            }
                        }
                    RingBrush choice = null;
                    for (int k = 0; k < pool.Count && choice == null; k++)
                    {
                        RingBrush c = pool[(off + k) % pool.Count];
                        if (!near.Contains(c.Id)) choice = c;
                    }
                    if (choice == null) continue;
                    int lo = choice.Size >= 64 ? RingRules.Scale64Lo : RingRules.Scale32Lo, hi = choice.Size >= 64 ? RingRules.Scale64Hi : RingRules.Scale32Hi;
                    double s = lo / 1000.0 + (hi / 1000.0 - lo / 1000.0) * RingHash.U(Seed, a, b, 4);
                    double ya = 2.0 * RingHash.U(Seed, a, b, 5) - 1.0, yb = 2.0 * RingHash.U(Seed, a, b, 8) - 1.0;
                    double yl = Math.Sqrt(ya * ya + yb * yb);
                    double cy = 1.0, sy = 0.0;
                    if (yl >= 1e-3) { cy = ya / yl; sy = yb / yl; }
                    var pl = new RingPlacement
                    {
                        Index = Placements.Count, X = x, Z = z, H = h, Y0 = h - choice.Ground * s - RingRules.SinkM, Cos = cy, Sin = sy,
                        Mirror = RingHash.U(Seed, a, b, 6) < 0.5, Scale = s, Ground = choice.Ground, Brush = choice, Biome = biome
                    };
                    List<int> cl;
                    if (!cells.TryGetValue(GridKey(a, b), out cl)) cells[GridKey(a, b)] = cl = new List<int>(1);
                    cl.Add(pl.Index);
                    Placements.Add(pl);
                }
            double above = RingRules.ArchB3 + 1.0, below = RingRules.CaveB3 + 1.0;
            foreach (RingPlacement p in Placements)
            {
                List<int> l;
                long gk = GridKey((int)Math.Floor(p.X / RingRules.GridM), (int)Math.Floor(p.Z / RingRules.GridM));
                if (!_grid.TryGetValue(gk, out l)) _grid[gk] = l = new List<int>(2);
                l.Add(p.Index);
                above = Math.Max(above, (p.Brush.Size - 1 - p.Ground) * p.Scale + 1.0);
                below = Math.Max(below, p.Ground * p.Scale + 1.0);
            }
            ReachAboveM = above;
            ReachBelowM = below;
            PlaceMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>A key's brush pool, ascending ids: its own biome's brushes of the rule's classes, or with fewer than 3 those of every biome.</summary>
        private List<RingBrush> PoolOf(RingRule rule, string biome)
        {
            int own = 0;
            foreach (RingBrush br in Library.Brushes)
                if (br.Biome == biome && Array.IndexOf(rule.Classes, br.Class) >= 0) own++;
            var pool = new List<RingBrush>();
            foreach (RingBrush br in Library.Brushes)
                if (Array.IndexOf(rule.Classes, br.Class) >= 0 && (own < 3 || br.Biome == biome)) pool.Add(br);
            return pool;
        }

        /// <summary>FNV-1a 64 over every placement's x, z, H, scale, cos, sin (doubles) and brush, mirror, size (int32), in order (placements_hash).</summary>
        public ulong PlacementsHash()
        {
            ulong h = RingHash.FnvOffset;
            var buf = new byte[60];
            foreach (RingPlacement p in Placements)
            {
                int o = 0;
                foreach (double d in new[] { p.X, p.Z, p.H, p.Scale, p.Cos, p.Sin })
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(d), 0, buf, o, 8);
                    o += 8;
                }
                Buffer.BlockCopy(BitConverter.GetBytes(p.Brush.Id), 0, buf, o, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(p.Mirror ? 1 : 0), 0, buf, o + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(p.Brush.Size), 0, buf, o + 8, 4);
                h = RingHash.Fnv(buf, 0, 60, h);
            }
            return h;
        }

        /// <summary>The placements whose footprint may reach the SN box [x0, x1] x [z0, z1], ascending (placements_near).</summary>
        public void Near(double x0, double x1, double z0, double z1, List<RingPlacement> into)
        {
            into.Clear();
            int gx0 = (int)Math.Floor((x0 - RingRules.GridM) / RingRules.GridM), gx1 = (int)Math.Floor((x1 + RingRules.GridM) / RingRules.GridM);
            int gz0 = (int)Math.Floor((z0 - RingRules.GridM) / RingRules.GridM), gz1 = (int)Math.Floor((z1 + RingRules.GridM) / RingRules.GridM);
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    List<int> l;
                    if (!_grid.TryGetValue(GridKey(gx, gz), out l)) continue;
                    foreach (int i in l)
                    {
                        RingPlacement p = Placements[i];
                        double r = p.Brush.Size * 0.5 * p.Scale + 1.0;
                        if (p.X + r >= x0 && p.X - r <= x1 && p.Z + r >= z0 && p.Z - r <= z1) into.Add(p);
                    }
                }
            if (into.Count > 1) into.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        /// <summary>The nearest distance between two placements of one brush (S8a.F.repeat), +inf with none.</summary>
        public double NearestSameBrushM(out int brushesUsed)
        {
            var by = new Dictionary<int, List<RingPlacement>>();
            foreach (RingPlacement p in Placements)
            {
                List<RingPlacement> l;
                if (!by.TryGetValue(p.Brush.Id, out l)) by[p.Brush.Id] = l = new List<RingPlacement>();
                l.Add(p);
            }
            brushesUsed = by.Count;
            double worst = double.PositiveInfinity;
            foreach (List<RingPlacement> l in by.Values)
                for (int i = 0; i < l.Count; i++)
                    for (int j = i + 1; j < l.Count; j++)
                    {
                        double dx = l[i].X - l[j].X, dz = l[i].Z - l[j].Z;
                        worst = Math.Min(worst, Math.Sqrt(dx * dx + dz * dz));
                    }
            return worst;
        }

        // ------------------------------------------------------------------------------------------ the field

        private static double Smooth(double e0, double e1, double x)
        {
            double t = Math.Min(Math.Max((x - e0) / (e1 - e0), 0.0), 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>The field at an SN point (its column and the placements near it looked up).</summary>
        public double Sample(double x, double y, double z)
        {
            double h;
            int key;
            Base.Column(x, z, out h, out key);
            var near = new List<RingPlacement>(4);
            Near(x, x, z, z, near);
            return Sample(x, y, z, h, key, near);
        }

        /// <summary>The field at an SN point of a column whose seabed and key are known, with a superset of the placements reaching it (ascending).</summary>
        public double Sample(double x, double y, double z, double h, int key, List<RingPlacement> near)
        {
            double f = h - y;
            if (key >= 0)
            {
                RingRule rule = RingRules.RuleOfKey[key];
                double rho = Math.Sqrt(x * x + z * z);
                double edge = Smooth(RingRules.FadeIn0, RingRules.FadeIn1, rho) * (1.0 - Smooth(RingRules.FadeOut0, RingRules.FadeOut1, rho));
                if (rule != null && rule.Caves)
                {
                    double depth = h - y;
                    double band = Smooth(RingRules.CaveB0, RingRules.CaveB1, depth) * (1.0 - Smooth(RingRules.CaveB2, RingRules.CaveB3, depth)) * edge;
                    if (band > 0)
                    {
                        double n1 = Math.Min(Math.Max(1.7 * _cave1.Fbm(x, y, z, RingRules.CaveWavelength), -1.0), 1.0);
                        double n2 = Math.Min(Math.Max(1.7 * _cave2.Fbm(x, y, z, RingRules.CaveWavelength), -1.0), 1.0);
                        double w = Math.Sqrt(n1 * n1 + n2 * n2);
                        double carve = (w - RingRules.CaveThreshold / 1000.0) * RingRules.CaveGain;
                        f = Math.Min(f, f * (1.0 - band) + carve * band);
                    }
                }
                if (rule != null && rule.Arches)
                {
                    double hh = y - h;
                    double band = Smooth(RingRules.ArchB0, RingRules.ArchB1, hh) * (1.0 - Smooth(RingRules.ArchB2, RingRules.ArchB3, hh)) * edge;
                    if (band > 0)
                    {
                        double a = _arch.Ridged(x, y, z, RingRules.ArchWavelength);
                        double arch = (a - RingRules.ArchThreshold / 1000.0) * RingRules.ArchGain;
                        if (arch > f) f = Math.Max(f, arch * band + f * (1.0 - band));
                    }
                }
            }
            if (near != null)
                for (int i = 0; i < near.Count; i++) f = Stamp(near[i], f, x, y, z, h);
            return f;
        }

        private static double Stamp(RingPlacement p, double f, double x, double y, double z, double h)
        {
            int n = p.Brush.Size;
            double half = n / 2.0;
            double s = p.Scale;
            double dx = x - p.X, dz = z - p.Z;
            double lx = (p.Cos * dx + p.Sin * dz) / s + half;
            double lz = (-p.Sin * dx + p.Cos * dz) / s + half;
            double ly = p.Ground + (y - h) / s;
            if (p.Mirror) lx = n - lx;
            double ex = lx - half, ez = lz - half;
            double rr = Math.Sqrt(ex * ex + ez * ez) / half;
            if (!(rr < 1.0 && ly >= 0.0 && ly <= n - 1.0)) return f;
            double m = RingRules.FalloffM / s;
            double wy = Math.Min(Math.Max(Math.Min(ly, (n - 1.0) - ly) / m, 0.0), 1.0);
            double wh = 1.0 - Smooth(RingRules.Disc / 1000.0, 1.0, rr);
            double w = wh * (wy * wy * (3.0 - 2.0 * wy));
            double b = Trilinear(p.Brush.Sdf, n, lz, ly, lx) * s;
            double clamp = 7.9 * s;
            double hy = h - y;
            double d = b - Math.Min(Math.Max(hy, -clamp), clamp);
            return f + w * d;
        }

        /// <summary>The weight of a placement's stamp at a point (0 outside it): the S8a.F.blend probe's border test.</summary>
        public static double Weight(RingPlacement p, double x, double y, double z, double h)
        {
            int n = p.Brush.Size;
            double half = n / 2.0, s = p.Scale;
            double dx = x - p.X, dz = z - p.Z;
            double lx = (p.Cos * dx + p.Sin * dz) / s + half;
            double lz = (-p.Sin * dx + p.Cos * dz) / s + half;
            double ly = p.Ground + (y - h) / s;
            if (p.Mirror) lx = n - lx;
            double ex = lx - half, ez = lz - half;
            double rr = Math.Sqrt(ex * ex + ez * ez) / half;
            if (!(rr < 1.0 && ly >= 0.0 && ly <= n - 1.0)) return 0.0;
            double wy = Math.Min(Math.Max(Math.Min(ly, (n - 1.0) - ly) / (RingRules.FalloffM / s), 0.0), 1.0);
            return (1.0 - Smooth(RingRules.Disc / 1000.0, 1.0, rr)) * (wy * wy * (3.0 - 2.0 * wy));
        }

        private static double Trilinear(sbyte[] q, int n, double z, double y, double x)
        {
            x = Math.Min(Math.Max(x, 0.0), n - 1.0);
            y = Math.Min(Math.Max(y, 0.0), n - 1.0);
            z = Math.Min(Math.Max(z, 0.0), n - 1.0);
            int x0 = Math.Min((int)Math.Floor(x), n - 2), y0 = Math.Min((int)Math.Floor(y), n - 2), z0 = Math.Min((int)Math.Floor(z), n - 2);
            double fx = x - x0, fy = y - y0, fz = z - z0;
            int nn = n * n;
            int b000 = z0 * nn + y0 * n + x0;
            int b010 = b000 + n, b001 = b000 + nn, b011 = b001 + n;
            double v000 = q[b000] / 16.0, v100 = q[b000 + 1] / 16.0, v010 = q[b010] / 16.0, v110 = q[b010 + 1] / 16.0;
            double v001 = q[b001] / 16.0, v101 = q[b001 + 1] / 16.0, v011 = q[b011] / 16.0, v111 = q[b011 + 1] / 16.0;
            double c00 = v000 + fx * (v100 - v000);
            double c10 = v010 + fx * (v110 - v010);
            double c01 = v001 + fx * (v101 - v001);
            double c11 = v011 + fx * (v111 - v011);
            double c0 = c00 + fy * (c10 - c00);
            double c1 = c01 + fy * (c11 - c01);
            return c0 + fz * (c1 - c0);
        }

        /// <summary>SE's content byte of a signed distance at a LOD voxel size (MyPlanetShapeProvider.CalculateDistanceFieldInternal's rule, in double).</summary>
        public static byte Content(double f, double lodM)
        {
            return (byte)(int)((Math.Min(Math.Max(f / lodM, -1.0), 1.0) * 0.5 + 0.5) * 255.0);
        }

        /// <summary>Content bytes of an SN box on the 1 m grid (points x0 + i + 0.5...), x fastest, then y, then z (the vectors' order).</summary>
        public byte[] ContentBox(double x0, double y0, double z0, int nx, int ny, int nz, out int placementsNear)
        {
            var near = new List<RingPlacement>();
            Near(x0, x0 + nx, z0, z0 + nz, near);
            placementsNear = near.Count;
            var c = new byte[nx * ny * nz];
            var hs = new double[nx * nz];
            var ks = new int[nx * nz];
            for (int k = 0; k < nz; k++)
                for (int i = 0; i < nx; i++)
                    Base.Column(x0 + i + 0.5, z0 + k + 0.5, out hs[k * nx + i], out ks[k * nx + i]);
            int o = 0;
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                        c[o++] = Content(Sample(x0 + i + 0.5, y0 + j + 0.5, z0 + k + 0.5, hs[k * nx + i], ks[k * nx + i], near), 1.0);
            return c;
        }

        // ------------------------------------------------------------------------------------------ the served bytes (S8aRing)

 /// <summary>arctan Taylor coefficients: -1/3, 1/5,..., 1/29 (|t| &lt;= tan 0.3; error &lt; 1e-19).</summary>
        private static readonly double[] AtanCoef = { -1.0 / 3.0, 1.0 / 5.0, -1.0 / 7.0, 1.0 / 9.0, -1.0 / 11.0, 1.0 / 13.0, -1.0 / 15.0, 1.0 / 17.0, -1.0 / 19.0, 1.0 / 21.0, -1.0 / 23.0, 1.0 / 25.0, -1.0 / 27.0, 1.0 / 29.0 };
        /// <summary>tan(0.3): beyond it a point is outside the served zone (the zone's site angle is at most 0.21 rad).</summary>
        public const double MaxSiteT = 0.30933624960962325;

        /// <summary>arctan of a small t as a Horner polynomial (s8a_ring3d.atan_small): no transcendental call, so the same bits on every machine.</summary>
        public static double AtanSmall(double t)
        {
            double t2 = t * t;
            double acc = AtanCoef[AtanCoef.Length - 1];
            for (int k = AtanCoef.Length - 2; k >= 0; k--) acc = AtanCoef[k] + t2 * acc;
            return t * (1.0 + t2 * acc);
        }

        /// <summary>
 /// Site v1 (C = 0) SN coordinates of a planet-local point in the vanilla generator's frame
 /// E0 = -X, U0 = +Z, N0 = -Y, psi 0, O 0 (SiteMap.SeToSn), the arctan as
 /// <see cref="AtanSmall"/> (s8a_ring3d.sn_of_planet_local). False outside the site angle atan(<see cref="MaxSiteT"/>).
 /// </summary>
        public static bool SnOfPlanetLocal(double px, double py, double pz, out double x, out double y, out double z)
        {
            double le = -px, lu = pz, ln = -py;
            double h = Math.Sqrt(le * le + ln * ln);
            double r = Math.Sqrt(le * le + lu * lu + ln * ln);
            y = r - RingRules.Rs;
            if (!(lu > 0) || !(h <= MaxSiteT * lu)) { x = z = double.NaN; return false; }
            double rho = RingRules.Rs * AtanSmall(h / lu);
            x = h > 0 ? rho * le / h : 0.0;
            z = h > 0 ? rho * ln / h : 0.0;
            return true;
        }

        /// <summary>
 /// The content bytes served for a planet-local box (voxel (i, j, k) at (ax + i lod, ay + j lod, az + k lod), integer-valued
 /// coordinates) into <paramref name="dst"/> at origin + i + j stepY + k stepZ: the ReadRange prefix's own loop and the vectors'
 /// (s8a_ring3d.serve_box). <paramref name="near"/>: a superset of the placements reaching the box, ascending. Returns bit 0 =
 /// every byte 0, bit 1 = every byte 255.
 /// </summary>
        public int ServeBox(double ax, double ay, double az, int sx, int sy, int sz, double lod, byte[] dst, int origin, int stepY, int stepZ, List<RingPlacement> near)
        {
            bool allEmpty = true, allFull = true;
            for (int k = 0; k < sz; k++)
                for (int j = 0; j < sy; j++)
                {
                    int idx = origin + j * stepY + k * stepZ;
                    for (int i = 0; i < sx; i++, idx++)
                    {
                        double x, y, z, h;
                        int key;
                        byte c = 0;
                        if (SnOfPlanetLocal(ax + i * lod, ay + j * lod, az + k * lod, out x, out y, out z))
                        {
                            Base.Column(x, z, out h, out key);
                            if (!double.IsNaN(h)) c = Content(Sample(x, y, z, h, key, near), lod);
                        }
                        dst[idx] = c;
                        if (c != 0) allEmpty = false;
                        if (c != 255) allFull = false;
                    }
                }
            return (allEmpty ? 1 : 0) | (allFull ? 2 : 0);
        }

        /// <summary>The SN bounds of a planet-local box's 8 corners (false when a corner lies outside the site angle).</summary>
        public static bool SnBounds(double ax, double ay, double az, double bx, double by, double bz, out double x0, out double x1, out double y0, out double y1, out double z0, out double z1)
        {
            x0 = y0 = z0 = double.PositiveInfinity;
            x1 = y1 = z1 = double.NegativeInfinity;
            for (int c = 0; c < 8; c++)
            {
                double x, y, z;
                if (!SnOfPlanetLocal((c & 1) == 0 ? ax : bx, (c & 2) == 0 ? ay : by, (c & 4) == 0 ? az : bz, out x, out y, out z)) return false;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                z0 = Math.Min(z0, z); z1 = Math.Max(z1, z);
            }
            return true;
        }

        /// <summary>The ring zone (SN radius), with a margin.</summary>
        public static bool InRing(double x, double z, double marginM)
        {
            double r = Math.Sqrt(x * x + z * z);
            return r >= RingRules.RhoMin - marginM && r <= RingRules.RhoMax + marginM;
        }
    }
}
