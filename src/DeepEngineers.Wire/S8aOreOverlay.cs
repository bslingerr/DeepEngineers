using System;
using System.Collections.Generic;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // add all ores UNDER the SN original voxels").
 // The crater's veins (S8aCraterOreRule: the cube rule's pockets and the pockets under Subnautica's large deposits) keep
 // S8aCraterOreRule.DefaultMinCoverM of the real map's rock above them, so the map would show nothing of them; this overlay
 // writes the ore's Subnautica type onto the map's own surface voxels over a vein: a solid near-surface voxel of the batch
 // (density != 0) within ShowRadiusM of a vein voxel takes the type of its nearest one (ties: the rarer row). Type only, the
 // densities are never touched (the geometry stays the map's). A vein voxel counts while the batch's base (the newest store
 // version: the drill layer, SE's edits) still holds it solid; drilled out, its paint goes: an ore-typed voxel no vein reaches
 // any more gets the install's own type back (the fallback rock where the install was not solid). VRage-free and pure: a
 // function of the rule, the base and the install (S8aDrillConverter runs it on its worker and writes the drill layer's
 // versions; tests run it on synthetic batches). S8a.M31: writes nothing.
 // =================================================================================================================

 /// <summary>One overlay pass over a batch (counts and the batch-local box of the voxels it changed).</summary>
    public sealed class S8aOreOverlayResult
    {
        public int OctreesVisited, OctreesWithOre, OctreesRewritten;
        /// <summary>Surface voxels seen, painted now (an ore type written), restored (an ore type taken back), carrying an ore type after the pass.</summary>
        public long Surface, Painted, Restored, OreSurface;
        /// <summary>Vein voxels within reach the base still holds, and the ones it lost (drilled out).</summary>
        public long VeinVoxels, Mined;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue, MaxZ = int.MinValue;
        public bool Changed { get { return OctreesRewritten > 0; } }
        public bool M31;

        internal void Grow(int x, int y, int z)
        {
            if (x < MinX) MinX = x; if (y < MinY) MinY = y; if (z < MinZ) MinZ = z;
            if (x > MaxX) MaxX = x; if (y > MaxY) MaxY = y; if (z > MaxZ) MaxZ = z;
        }
    }

    /// <summary>The overlay core (one per thread; not thread-safe).</summary>
    public sealed class S8aOreOverlay
    {
        /// <summary>A surface voxel within this distance (metres, voxel centres) of a vein voxel takes the vein's type.</summary>
        public const double ShowRadiusM = 2.5;
                                                 // deposit's footprint plus a halo; with the 2 m cover, 2.5 m paints only the crest above it: a
 // small surface hint, the deposit underneath, SE's own look
 /// <summary>The reach in voxels (ceil of <see cref="ShowRadiusM"/>).</summary>
        public const int Reach = 3;
        /// <summary>

 /// footprint): the radius in use, 0 to <see cref="ShowRadiusM"/>. The SE plugin sets it from DEEPENGINEERS_S8A_ORE_SHOW_M
 /// (default 0: no paint at all, existing paint restored as the camera comes near; the ore detector is the hint, the ore 2 m under
 /// the rock). The tests keep the full radius.
 /// </summary>
        public static double ShowRadiusSetting = ShowRadiusM;
        /// <summary>The squared reach of the radius in use, in voxels.</summary>
        private static int ShowR2()
        {
            double r = Math.Max(0.0, Math.Min(ShowRadiusSetting, ShowRadiusM));
            return (int)Math.Floor(r * r + 1e-9);
        }

        private static readonly int[] Ball = BuildBall();                    // dx, dy, dz, d2 per entry
        private readonly S8aOctreeEncoder _enc = new S8aOctreeEncoder();
        private readonly byte[] _t = new byte[S8aOctree.Voxels], _d = new byte[S8aOctree.Voxels];
        private readonly byte[] _it = new byte[S8aOctree.Voxels], _id = new byte[S8aOctree.Voxels];
        private readonly byte[] _best = new byte[S8aOctree.Voxels], _bestRow = new byte[S8aOctree.Voxels];
        private readonly bool[] _surface = new bool[S8aOctree.Voxels];
        private readonly byte[] _nodeBuf = new byte[S8aOctree.MaxBytes];
        private byte[] _rows = new byte[0];
        private readonly bool[] _isOre = new bool[256];
        private readonly byte[] _rowType;
        private readonly Dictionary<int, byte[]> _decoded = new Dictionary<int, byte[]>();

        /// <param name="rule">The crater's ore rule (its cover set: the vein voxels).</param>
 /// <param name="rowType">The Subnautica type of each rule row (S8aMaterialMap.SnType of the row's subtype); 0 = the row paints nothing.</param>
        public S8aOreOverlay(S8aCraterOreRule rule, byte[] rowType)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            if (rowType == null || rowType.Length != S8aCraterOres.Rows.Length) throw new ArgumentException("one type per row expected", "rowType");
            Rule = rule;
            _rowType = (byte[])rowType.Clone();
            for (int i = S8aMaterialMap.SurfaceMaterials; i < S8aMaterialMap.SnTypes.Length; i++) _isOre[S8aMaterialMap.SnTypes[i]] = true;
            foreach (byte t in _rowType) if (t != 0) _isOre[t] = true;
        }

        /// <summary>The Subnautica types of the rule's rows (S8aMaterialMap.SnType of each row's subtype).</summary>
        public static byte[] DefaultRowTypes()
        {
            var t = new byte[S8aCraterOres.Rows.Length];
            for (int i = 0; i < t.Length; i++) t[i] = S8aMaterialMap.SnType(S8aCraterOres.Rows[i].Subtype);
            return t;
        }

        public S8aCraterOreRule Rule { get; private set; }

        /// <summary>Added to a world voxel index to get its sample point (0.5, the converter's).</summary>
        public double SnOffset { get; set; } = S8aConvert.SampleOffset;

        /// <summary>
 /// Solid in the newest served data at a world voxel outside the batch being overlaid (the neighbours' newest store version or
 /// install file); null = solid (a vein beyond the batch's faces counts as still there).
 /// </summary>
        public Func<int, int, int, bool> SolidOutside { get; set; }

        /// <summary>True for a Subnautica type this overlay writes (the ore slots).</summary>
        public bool IsOreType(byte t) { return _isOre[t]; }

        private static int[] BuildBall()
        {
            var l = new List<int>();
            int r2 = (int)Math.Floor(ShowRadiusM * ShowRadiusM + 1e-9);
            for (int dz = -Reach; dz <= Reach; dz++)
                for (int dy = -Reach; dy <= Reach; dy++)
                    for (int dx = -Reach; dx <= Reach; dx++)
                    {
                        int d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 > r2) continue;
                        l.Add(dx); l.Add(dy); l.Add(dz); l.Add(d2);
                    }
            return l.ToArray();
        }

        /// <summary>
 /// The pass over <paramref name="batch"/> (modified in place: the octrees whose types change are re-encoded). Only the octrees
 /// <paramref name="octreeFilter"/> accepts (null: all). <paramref name="install"/> (null, or returning null: none; called at most
 /// once, when a stale ore voxel needs its type back) gives the install's own batch.
 /// </summary>
        public S8aOreOverlayResult Apply(S8aBatchFile batch, Func<S8aBatchFile> install, Func<int, bool> octreeFilter = null)
        {
            if (batch == null) throw new ArgumentNullException("batch");
            var res = new S8aOreOverlayResult();
            if (S8aWireMutants.M31OverlayOff) { res.M31 = true; return res; }
            _decoded.Clear();
            _installOf = install;
            _installRead = false;
            _install = null;
            int per = SnWorld.OctreesPerBatch;
            for (int ix = 0; ix < per; ix++)
                for (int iy = 0; iy < per; iy++)
                    for (int iz = 0; iz < per; iz++)
                    {
                        int oi = S8aBatchFile.OctreeIndex(ix, iy, iz);
                        if (!batch.Has(oi) || batch.NodeCount(oi) <= 1) continue;
                        if (octreeFilter != null && !octreeFilter(oi)) continue;
                        OverlayOctree(batch, ix, iy, iz, oi, res);
                    }
            _decoded.Clear();
            _installOf = null;
            _install = null;
            return res;
        }

        private Func<S8aBatchFile> _installOf;
        private S8aBatchFile _install;
        private bool _installRead;

        private S8aBatchFile Install()
        {
            if (!_installRead)
            {
                _installRead = true;
                _install = _installOf != null ? _installOf() : null;
            }
            return _install;
        }

        /// <summary>The base octree's raster (type then density, 2 x 32,768 bytes) of in-batch octree <paramref name="oi"/>, decoded once per pass.</summary>
        private byte[] Decoded(S8aBatchFile batch, int oi)
        {
            byte[] r;
            if (_decoded.TryGetValue(oi, out r)) return r;
            r = new byte[2 * S8aOctree.Voxels];
            if (batch.Has(oi))
            {
                var t = new byte[S8aOctree.Voxels];
                var d = new byte[S8aOctree.Voxels];
                batch.DecodeOctree(oi, t, d);
                Buffer.BlockCopy(t, 0, r, 0, S8aOctree.Voxels);
                Buffer.BlockCopy(d, 0, r, S8aOctree.Voxels, S8aOctree.Voxels);
            }
            _decoded[oi] = r;
            return r;
        }

        /// <summary>Solid in the base at world voxel (wx, wy, wz): this batch's own (decoded) octrees, else <see cref="SolidOutside"/>.</summary>
        private bool BaseSolid(S8aBatchFile batch, int wx, int wy, int wz)
        {
            int bv = SnWorld.BatchVoxels;
            int lx = wx - batch.Bx * bv, ly = wy - batch.By * bv, lz = wz - batch.Bz * bv;
            if (lx < 0 || ly < 0 || lz < 0 || lx >= bv || ly >= bv || lz >= bv)
            {
                Func<int, int, int, bool> f = SolidOutside;
                return f == null || f(wx, wy, wz);
            }
            byte[] r = Decoded(batch, S8aBatchFile.OctreeIndex(lx >> 5, ly >> 5, lz >> 5));
            int li = S8aOctree.Index(lx & 31, ly & 31, lz & 31);
            return S8aOctree.IsBelowSurface(r[li], r[S8aOctree.Voxels + li]);
        }

        private void OverlayOctree(S8aBatchFile batch, int ix, int iy, int iz, int oi, S8aOreOverlayResult res)
        {
            byte[] raster = Decoded(batch, oi);
            Buffer.BlockCopy(raster, 0, _t, 0, S8aOctree.Voxels);
            Buffer.BlockCopy(raster, S8aOctree.Voxels, _d, 0, S8aOctree.Voxels);
            // the surface voxels (solid, near) and the stale candidates (solid, an ore type): their box
            int x0 = 32, y0 = 32, z0 = 32, x1 = -1, y1 = -1, z1 = -1;
            int surface = 0, oreTyped = 0;
            for (int z = 0; z < 32; z++)
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        int i = x + (y << 5) + (z << 10);
                        byte t = _t[i], d = _d[i];
                        bool solid = S8aOctree.IsBelowSurface(t, d);
                        bool surf = solid && d != 0;
                        _surface[i] = surf;
                        _best[i] = 255;
                        if (surf) surface++;
                        bool typed = solid && _isOre[t];
                        if (typed) oreTyped++;
                        if (!surf && !typed) continue;
                        if (x < x0) x0 = x; if (y < y0) y0 = y; if (z < z0) z0 = z;
                        if (x > x1) x1 = x; if (y > y1) y1 = y; if (z > z1) z1 = z;
                    }
            if (surface == 0 && oreTyped == 0) return;
            res.OctreesVisited++;
            res.Surface += surface;
            int vbx = batch.Bx * SnWorld.BatchVoxels + ix * 32, vby = batch.By * SnWorld.BatchVoxels + iy * 32, vbz = batch.Bz * SnWorld.BatchVoxels + iz * 32;
            // the vein voxels within reach of the box (the rule), kept while the base still holds them
            int bx0 = vbx + x0 - Reach, by0 = vby + y0 - Reach, bz0 = vbz + z0 - Reach;
            int sx = x1 - x0 + 1 + 2 * Reach, sy = y1 - y0 + 1 + 2 * Reach, sz = z1 - z0 + 1 + 2 * Reach;
            int n = sx * sy * sz;
            if (_rows.Length < n) _rows = new byte[n];
            int ore = Rule.Rows(bx0, by0, bz0, sx, sy, sz, SnOffset, _rows);
            if (ore > 0)
            {
                res.OctreesWithOre++;
                for (int bz = 0; bz < sz; bz++)
                    for (int by = 0; by < sy; by++)
                        for (int bx = 0; bx < sx; bx++)
                        {
                            int bi = bx + sx * (by + sy * bz);
                            if (_rows[bi] == 255) continue;
                            if (!BaseSolid(batch, bx0 + bx, by0 + by, bz0 + bz)) { _rows[bi] = 254; res.Mined++; continue; }   // drilled out
                            res.VeinVoxels++;
                        }
                for (int bz = 0; bz < sz; bz++)
                    for (int by = 0; by < sy; by++)
                        for (int bx = 0; bx < sx; bx++)
                        {
                            int bi = bx + sx * (by + sy * bz);
                            byte row = _rows[bi];
                            if (row >= 254) continue;
                            // only the vein's boundary voxels can be a surface voxel's nearest (an inner one has a vein neighbour nearer
 // along the axis toward it), except a surface voxel that is itself an inner vein voxel: kept below
                            if (bx > 0 && bx < sx - 1 && by > 0 && by < sy - 1 && bz > 0 && bz < sz - 1 &&
                                _rows[bi - 1] < 254 && _rows[bi + 1] < 254 && _rows[bi - sx] < 254 && _rows[bi + sx] < 254 &&
                                _rows[bi - sx * sy] < 254 && _rows[bi + sx * sy] < 254)
                            {
                                int qx = x0 - Reach + bx, qy = y0 - Reach + by, qz = z0 - Reach + bz;
                                if ((uint)qx > 31u || (uint)qy > 31u || (uint)qz > 31u) continue;
                                int qi = qx + (qy << 5) + (qz << 10);
                                if (_surface[qi]) { _best[qi] = 0; _bestRow[qi] = row; }
                                continue;
                            }
                            int ox = x0 - Reach + bx, oy = y0 - Reach + by, oz = z0 - Reach + bz;   // octree-local
                            int r2s = ShowR2();
                            for (int k = 0; k < Ball.Length; k += 4)
                            {
                                if (Ball[k + 3] > r2s) continue;   // the radius in use (0: nothing painted)
                                int lx = ox + Ball[k], ly = oy + Ball[k + 1], lz = oz + Ball[k + 2];
                                if ((uint)lx > 31u || (uint)ly > 31u || (uint)lz > 31u) continue;
                                int li = lx + (ly << 5) + (lz << 10);
                                if (!_surface[li]) continue;
                                byte d2 = (byte)Ball[k + 3];
                                if (d2 < _best[li] || (d2 == _best[li] && row < _bestRow[li])) { _best[li] = d2; _bestRow[li] = row; }
                            }
                        }
            }
            // the voxels themselves a vein voxel (kept as they are), the install for the restores
            bool haveInstall = false;
            int changed = 0;
            for (int z = 0; z < 32; z++)
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        int i = x + (y << 5) + (z << 10);
                        byte t = _t[i];
                        bool solid = _surface[i] || S8aOctree.IsBelowSurface(t, _d[i]);
                        if (!solid) continue;
                        byte want = t;
                        if (_surface[i] && _best[i] != 255 && _rowType[_bestRow[i]] != 0) want = _rowType[_bestRow[i]];
                        else if (_isOre[t])
                        {
                            // stale: no vein within reach (a vein voxel itself keeps its type)
                            int bx = x - x0 + Reach, by = y - y0 + Reach, bz = z - z0 + Reach;
                            if (ore > 0 && bx >= 0 && by >= 0 && bz >= 0 && bx < sx && by < sy && bz < sz && _rows[bx + sx * (by + sy * bz)] < 254) continue;
                            if (!haveInstall)
                            {
                                haveInstall = true;
                                S8aBatchFile install = Install();
                                if (install != null && install.Has(oi)) install.DecodeOctree(oi, _it, _id);
                                else { Array.Clear(_it, 0, _it.Length); Array.Clear(_id, 0, _id.Length); }
                            }
                            byte it = _it[i];
                            want = S8aOctree.IsBelowSurface(it, _id[i]) && it != 0 && !_isOre[it] ? it : S8aMaterialMap.Fallback;
                        }
                        if (want != t)
                        {
                            if (_isOre[want]) res.Painted++;
                            else res.Restored++;
                            _t[i] = want;
                            changed++;
                            res.Grow(ix * 32 + x, iy * 32 + y, iz * 32 + z);
                        }
                        if (_surface[i] && _isOre[want]) res.OreSurface++;
                    }
            if (changed == 0) return;
            int nodes = _enc.Encode(_t, _d, _nodeBuf, 0);
            batch.SetOctree(oi, _nodeBuf, 0, nodes);
            // the raster cache follows the batch (types only; solidity never changes)
            Buffer.BlockCopy(_t, 0, raster, 0, S8aOctree.Voxels);
            res.OctreesRewritten++;
        }

        /// <summary>
 /// The vein voxels the base still holds within <paramref name="radius"/> voxels of world voxel (wx, wy, wz) (a cube; at most
 /// <paramref name="max"/> listed): [wx, wy, wz, row] each; <paramref name="solid"/> = the base's solidity at a world voxel.
 /// </summary>
        public int VeinAround(int wx, int wy, int wz, int radius, Func<int, int, int, bool> solid, List<int[]> voxels, int max)
        {
            int s = 2 * radius + 1, n = s * s * s;
            var rows = new byte[n];
            Rule.Rows(wx - radius, wy - radius, wz - radius, s, s, s, SnOffset, rows);
            int got = 0;
            for (int z = 0; z < s; z++)
                for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                    {
                        byte row = rows[x + s * (y + s * z)];
                        if (row == 255) continue;
                        int vx = wx - radius + x, vy = wy - radius + y, vz = wz - radius + z;
                        if (solid != null && !solid(vx, vy, vz)) continue;
                        got++;
                        if (voxels != null && voxels.Count < max) voxels.Add(new[] { vx, vy, vz, row });
                    }
            return got;
        }

        /// <summary>
 /// The type the overlay gives the surface voxel at world voxel (wx, wy, wz) now: the type of its nearest vein voxel the base
 /// still holds within <see cref="ShowRadiusM"/>, else 0 (no paint).
 /// </summary>
        public byte ExpectedType(int wx, int wy, int wz, Func<int, int, int, bool> solid)
        {
            int s = 2 * Reach + 1;
            var rows = new byte[s * s * s];
            if (Rule.Rows(wx - Reach, wy - Reach, wz - Reach, s, s, s, SnOffset, rows) == 0) return 0;
            int best = int.MaxValue, bestRow = 255;
            int r2s = ShowR2();
            for (int k = 0; k < Ball.Length; k += 4)
            {
                if (Ball[k + 3] > r2s) continue;   // the radius in use (0: nothing painted)
                int x = Ball[k] + Reach, y = Ball[k + 1] + Reach, z = Ball[k + 2] + Reach;
                byte row = rows[x + s * (y + s * z)];
                if (row == 255) continue;
                if (solid != null && !solid(wx + Ball[k], wy + Ball[k + 1], wz + Ball[k + 2])) continue;
                int d2 = Ball[k + 3];
                if (d2 < best || (d2 == best && row < bestRow)) { best = d2; bestRow = row; }
            }
            return bestRow == 255 ? (byte)0 : _rowType[bestRow];
        }
    }
}
