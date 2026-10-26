using System;
using System.Collections.Generic;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The SE voxel source the drill converter reads: the site's tiles with their voxel-0 position in SE world and a dense LOD-0 read of
 /// a voxel box. The SE plugin implements it over <c>TerrainTiles</c> and <c>MyStorageBase.ReadRange</c>; the tests
 /// over an analytic field.
 /// </summary>
    public interface IS8aTileSource
    {
        /// <summary>Tiles indexed 0..Count−1 (a closed tile answers false from <see cref="TryGetTile"/>).</summary>
        int Count { get; }
        /// <summary>Voxels per tile edge (site v1: 1024).</summary>
        int TileSize { get; }
        /// <summary>The tile's voxel-0 position (SE world; LOD-0 sample i sits at placeMin + i), false when closed.</summary>
        bool TryGetTile(int index, out Vec3 placeMin);
        /// <summary>The tile holding an SE world point (its interior first), or −1.</summary>
        int TileAt(Vec3 se);
        /// <summary>
 /// LOD-0 content and material of the tile's voxel box [min, max] (inclusive, tile voxel coordinates, inside the tile)
 /// into <paramref name="content"/> / <paramref name="material"/>, x fastest, (max − min + 1)³ entries.
 /// </summary>
        void ReadRange(int index, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte[] content, byte[] material);
    }

    /// <summary>An edited voxel range of one tile (inclusive, tile voxel coordinates).</summary>
    public struct S8aEditBox
    {
        public int Tile;
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public S8aEditBox(int tile, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            Tile = tile;
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }
    }

    /// <summary>What one batch conversion did.</summary>
    public sealed class S8aConvertResult
    {
        public int OctreesVisited, OctreesRewritten, Skipped;
        public uint Cells;
        /// <summary>Batch-local voxel box (0–159, inclusive) of the voxels that changed; valid when <see cref="OctreesRewritten"/> &gt; 0.</summary>
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        /// <summary>a test run: of those, the voxels the level-0 mesh can show (<see cref="S8aFieldSampler.LastMeshChanged"/>); the box is valid when <see cref="MeshCells"/> &gt; 0.</summary>
        public uint MeshCells, DensityCells;
        public int MeshMinX = int.MaxValue, MeshMinY = int.MaxValue, MeshMinZ = int.MaxValue, MeshMaxX = int.MinValue, MeshMaxY = int.MinValue, MeshMaxZ = int.MinValue;
        public string Note;
    }

    /// <summary>
 /// The one conversion core ("convert.sample", "convert.copyonwrite"; CONTRACT §3.3 "Conversion"): the edited SE ranges
 /// of the tiles → the Subnautica octrees of one batch, spliced copy-on-write into its base. Per octree whose SN box
 /// meets an edit: the SE buffer over the octree's preimage box is stitched from every tile whose box meets it (the
 /// tiles' voxel grids align: pitch 1,020 m is a whole number of metres, so another tile's voxel is the reference tile's
 /// voxel plus an integer offset), only the voxels whose 8-sample stencil meets an edited range are recomputed, a voxel
 /// that was solid keeps its type. The notice box is the min/max of the voxels that actually changed.
 /// VRage-free: the SE plugin wraps its storages in an <see cref="IS8aTileSource"/>; tests use an analytic field.
 /// </summary>
    public sealed class S8aConvertCore
    {
        public const int MaxPreimageEdge = 96;

        private readonly S8aFieldSampler _sampler = new S8aFieldSampler();
        private readonly S8aOctreeEncoder _enc = new S8aOctreeEncoder();
        private readonly byte[] _t32 = new byte[S8aOctree.Voxels], _d32 = new byte[S8aOctree.Voxels], _nodeBuf = new byte[S8aOctree.MaxBytes];
        private byte[] _content, _material, _rangeC, _rangeM;
        private int[] _boxes = new int[6 * 16];

        /// <summary>Added to a voxel index to get its sample point (0.5; S8a.M2 sets 0).</summary>
        public double SnOffset
        {
            get { return _sampler.SnOffset; }
            set { _sampler.SnOffset = value; }
        }

        /// <summary>The SE material index → Subnautica block type table (256 entries).</summary>
 /// <summary>Planet v2 route B: a placed chunk's native types for its solid voxels (<see cref="S8aFieldSampler.NativeType"/>).</summary>
        public Func<Vec3, byte> NativeType
        {
            get { return _sampler.NativeType; }
            set { _sampler.NativeType = value; }
        }

        public byte[] MaterialTable
        {
            get { return _sampler.MaterialTable; }
            set { _sampler.MaterialTable = value ?? S8aMaterialMap.UniformTable(S8aMaterialMap.Fallback); }
        }

        /// <summary>The SN voxel box (inclusive, expanded by one voxel) of a tile range's world box through F.</summary>
        public static void SnBoxOf(SiteMap map, Vec3 placeMin, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                   out int vx0, out int vy0, out int vz0, out int vx1, out int vy1, out int vz1)
        {
            double ax = double.MaxValue, ay = double.MaxValue, az = double.MaxValue, bx = double.MinValue, by = double.MinValue, bz = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                var w = new Vec3(placeMin.X + ((c & 1) != 0 ? maxX + 1 : minX), placeMin.Y + ((c & 2) != 0 ? maxY + 1 : minY), placeMin.Z + ((c & 4) != 0 ? maxZ + 1 : minZ));
                Vec3 p = map.SeToSn(w);
                if (p.X < ax) ax = p.X; if (p.X > bx) bx = p.X;
                if (p.Y < ay) ay = p.Y; if (p.Y > by) by = p.Y;
                if (p.Z < az) az = p.Z; if (p.Z > bz) bz = p.Z;
            }
            S8aConvert.VoxelOf(new Vec3(ax, ay, az), out vx0, out vy0, out vz0);
            S8aConvert.VoxelOf(new Vec3(bx, by, bz), out vx1, out vy1, out vz1);
            vx0 -= 1; vy0 -= 1; vz0 -= 1;
            vx1 += 1; vy1 += 1; vz1 += 1;
        }

        /// <summary>The integer voxel offset of tile <paramref name="of"/> relative to tile <paramref name="relativeTo"/>: of's voxel v is relativeTo's voxel v + offset.</summary>
        public static void Offset(Vec3 ofPlaceMin, Vec3 relativeToPlaceMin, out int ox, out int oy, out int oz)
        {
            Vec3 d = ofPlaceMin - relativeToPlaceMin;
            ox = (int)Math.Round(d.X); oy = (int)Math.Round(d.Y); oz = (int)Math.Round(d.Z);
        }

        /// <summary>
 /// Converts the edits into <paramref name="batch"/> (its octrees rewritten in place). Octrees whose SN box meets no
 /// edit, and octrees whose recomputation changes no voxel, are left as they are.
 /// </summary>
        public S8aConvertResult ConvertBatch(SiteMap map, IS8aTileSource tiles, S8aBatchFile batch, IList<S8aEditBox> edits, bool keepBaseType = true, Func<int, bool> octreeFilter = null)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (tiles == null) throw new ArgumentNullException("tiles");
            if (batch == null) throw new ArgumentNullException("batch");
            var res = new S8aConvertResult { MinX = int.MaxValue, MinY = int.MaxValue, MinZ = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue, MaxZ = int.MinValue };
            if (edits == null || edits.Count == 0) { res.Note = "no edits"; return res; }
            int bx0 = batch.Bx * SnWorld.BatchVoxels, by0 = batch.By * SnWorld.BatchVoxels, bz0 = batch.Bz * SnWorld.BatchVoxels;
            int ex0 = int.MaxValue, ey0 = int.MaxValue, ez0 = int.MaxValue, ex1 = int.MinValue, ey1 = int.MinValue, ez1 = int.MinValue;
            var placeMins = new Vec3[tiles.Count];
            var open = new bool[tiles.Count];
            for (int t = 0; t < tiles.Count; t++) open[t] = tiles.TryGetTile(t, out placeMins[t]);
            foreach (S8aEditBox e in edits)
            {
                if (e.Tile < 0 || e.Tile >= tiles.Count || !open[e.Tile]) continue;
                int vx0, vy0, vz0, vx1, vy1, vz1;
                SnBoxOf(map, placeMins[e.Tile], e.MinX, e.MinY, e.MinZ, e.MaxX, e.MaxY, e.MaxZ, out vx0, out vy0, out vz0, out vx1, out vy1, out vz1);
                ex0 = Math.Min(ex0, vx0); ey0 = Math.Min(ey0, vy0); ez0 = Math.Min(ez0, vz0);
                ex1 = Math.Max(ex1, vx1); ey1 = Math.Max(ey1, vy1); ez1 = Math.Max(ez1, vz1);
            }
            ex0 = Math.Max(ex0, bx0); ey0 = Math.Max(ey0, by0); ez0 = Math.Max(ez0, bz0);
            ex1 = Math.Min(ex1, bx0 + SnWorld.BatchVoxels - 1); ey1 = Math.Min(ey1, by0 + SnWorld.BatchVoxels - 1); ez1 = Math.Min(ez1, bz0 + SnWorld.BatchVoxels - 1);
            if (ex1 < ex0 || ey1 < ey0 || ez1 < ez0) { res.Note = "no edit meets the batch"; return res; }
            _sampler.Map = map;
            if (_boxes.Length < edits.Count * 6) _boxes = new int[edits.Count * 6];
            int first = -1;
            foreach (S8aEditBox e in edits) if (e.Tile >= 0 && e.Tile < tiles.Count && open[e.Tile]) { first = e.Tile; break; }
            if (first < 0) { res.Note = "no open tile"; return res; }
            for (int ix = (ex0 - bx0) >> 5; ix <= (ex1 - bx0) >> 5; ix++)
                for (int iy = (ey0 - by0) >> 5; iy <= (ey1 - by0) >> 5; iy++)
                    for (int iz = (ez0 - bz0) >> 5; iz <= (ez1 - bz0) >> 5; iz++)
                    {
                        int oi = S8aBatchFile.OctreeIndex(ix, iy, iz);
                        if (!batch.Has(oi)) continue;
                        if (octreeFilter != null && !octreeFilter(oi)) continue;   // the window: LOD-0 reads only for octrees that meet the surface
                        res.OctreesVisited++;
                        int ox = batch.Bx * SnWorld.OctreesPerBatch + ix, oy = batch.By * SnWorld.OctreesPerBatch + iy, oz = batch.Bz * SnWorld.OctreesPerBatch + iz;
                        // the reference tile: the one holding the octree's preimage centre, else the first edit's
                        Vec3 centreSe = map.SnToSe(S8aConvert.SampleCentreSn(32 * ox + 15, 32 * oy + 15, 32 * oz + 15));
                        int refTile = tiles.TileAt(centreSe);
                        if (refTile < 0 || !open[refTile]) refTile = first;
                        Vec3 refMin = placeMins[refTile];
                        int minX, minY, minZ, maxX, maxY, maxZ;
                        S8aFieldSampler.PreimageBox(map, 32 * ox, 32 * oy, 32 * oz, 32, refMin, 1, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
                        int sx = maxX - minX + 1, sy = maxY - minY + 1, sz = maxZ - minZ + 1;
                        if (sx > MaxPreimageEdge || sy > MaxPreimageEdge || sz > MaxPreimageEdge) { res.Skipped++; res.Note = "preimage box too large " + sx + "x" + sy + "x" + sz; continue; }
                        int n = sx * sy * sz;
                        if (_content == null || _content.Length < n) { _content = new byte[n]; _material = new byte[n]; }
                        Array.Clear(_content, 0, n);
                        Array.Clear(_material, 0, n);
                        if (!FillBuffer(tiles, placeMins, open, refMin, minX, minY, minZ, sx, sy, sz)) { res.Skipped++; continue; }
                        int nBoxes = 0;
                        foreach (S8aEditBox e in edits)
                        {
                            if (e.Tile < 0 || e.Tile >= tiles.Count || !open[e.Tile]) continue;
                            int dx, dy, dz;
                            Offset(placeMins[e.Tile], refMin, out dx, out dy, out dz);
                            int k = nBoxes * 6;
                            _boxes[k] = e.MinX + dx; _boxes[k + 1] = e.MinY + dy; _boxes[k + 2] = e.MinZ + dz;
                            _boxes[k + 3] = e.MaxX + dx; _boxes[k + 4] = e.MaxY + dy; _boxes[k + 5] = e.MaxZ + dz;
                            nBoxes++;
                        }
                        _sampler.SetBuffer(_content, _material, sx, sy, sz, minX, minY, minZ, refMin);
                        batch.DecodeOctree(oi, _t32, _d32);
                        if (keepBaseType) _sampler.SampleOctreeCopyOnWrite(ox, oy, oz, _t32, _d32, _boxes, nBoxes);
                        else _sampler.SampleOctree(ox, oy, oz, _t32, _d32, false);
                        if (_sampler.LastChanged == 0) continue;
                        int nodes = _enc.Encode(_t32, _d32, _nodeBuf, 0);
                        batch.SetOctree(oi, _nodeBuf, 0, nodes);
                        res.OctreesRewritten++;
                        res.Cells += (uint)_sampler.LastChanged;
                        int lx0 = ix * 32 + _sampler.LastMinX, ly0 = iy * 32 + _sampler.LastMinY, lz0 = iz * 32 + _sampler.LastMinZ;
                        int lx1 = ix * 32 + _sampler.LastMaxX, ly1 = iy * 32 + _sampler.LastMaxY, lz1 = iz * 32 + _sampler.LastMaxZ;
                        res.MinX = Math.Min(res.MinX, lx0); res.MinY = Math.Min(res.MinY, ly0); res.MinZ = Math.Min(res.MinZ, lz0);
                        res.MaxX = Math.Max(res.MaxX, lx1); res.MaxY = Math.Max(res.MaxY, ly1); res.MaxZ = Math.Max(res.MaxZ, lz1);
                        res.DensityCells += (uint)_sampler.LastDensityChanged;
                        if (_sampler.LastMeshChanged > 0)
                        {
                            res.MeshCells += (uint)_sampler.LastMeshChanged;
                            res.MeshMinX = Math.Min(res.MeshMinX, ix * 32 + _sampler.LastMeshMinX); res.MeshMinY = Math.Min(res.MeshMinY, iy * 32 + _sampler.LastMeshMinY);
                            res.MeshMinZ = Math.Min(res.MeshMinZ, iz * 32 + _sampler.LastMeshMinZ);
                            res.MeshMaxX = Math.Max(res.MeshMaxX, ix * 32 + _sampler.LastMeshMaxX); res.MeshMaxY = Math.Max(res.MeshMaxY, iy * 32 + _sampler.LastMeshMaxY);
                            res.MeshMaxZ = Math.Max(res.MeshMaxZ, iz * 32 + _sampler.LastMeshMaxZ);
                        }
                    }
            return res;
        }

        /// <summary>
 /// The ore cover scan (S8a.NV.
 /// included; each SN voxel sampled as the converter samples it) and, per solid sample of an ore material (S8aMaterialMap.OreCanon),
 /// the real map at that voxel (<paramref name="mapSolid"/>, world voxel indices): not solid = above the map's surface, an empty
 /// face neighbour = on it, an empty voxel in the 3 x 3 x 3 neighbourhood = within the cover. Accumulates into <paramref name="into"/>.
 /// </summary>
        public void ScanOreCover(SiteMap map, IS8aTileSource tiles, int ox, int oy, int oz, Func<int, int, int, bool> mapSolid, S8aOreCoverStats into)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (tiles == null) throw new ArgumentNullException("tiles");
            if (mapSolid == null) throw new ArgumentNullException("mapSolid");
            if (into == null) throw new ArgumentNullException("into");
            byte[] canon = S8aMaterialMap.OreCanon;
            if (canon == null) { into.Skipped++; return; }
            var placeMins = new Vec3[tiles.Count];
            var open = new bool[tiles.Count];
            for (int t = 0; t < tiles.Count; t++) open[t] = tiles.TryGetTile(t, out placeMins[t]);
            Vec3 centreSe = map.SnToSe(S8aConvert.SampleCentreSn(32 * ox + 15, 32 * oy + 15, 32 * oz + 15));
            int refTile = tiles.TileAt(centreSe);
            if (refTile < 0 || !open[refTile]) { into.Skipped++; return; }
            Vec3 refMin = placeMins[refTile];
            int minX, minY, minZ, maxX, maxY, maxZ;
            S8aFieldSampler.PreimageBox(map, 32 * ox, 32 * oy, 32 * oz, 32, refMin, 1, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
            int sx = maxX - minX + 1, sy = maxY - minY + 1, sz = maxZ - minZ + 1;
            if (sx > MaxPreimageEdge || sy > MaxPreimageEdge || sz > MaxPreimageEdge) { into.Skipped++; return; }
            int n = sx * sy * sz;
            if (_content == null || _content.Length < n) { _content = new byte[n]; _material = new byte[n]; }
            Array.Clear(_content, 0, n);
            Array.Clear(_material, 0, n);
            if (!FillBuffer(tiles, placeMins, open, refMin, minX, minY, minZ, sx, sy, sz)) { into.Skipped++; return; }
            _sampler.Map = map;
            _sampler.SetBuffer(_content, _material, sx, sy, sz, minX, minY, minZ, refMin);
            into.Octrees++;
            double off = S8aConvert.SampleOffset;
            for (int z = 0; z < 32; z++)
            {
                int wz = 32 * oz + z;
                double snZ = SnWorld.OriginZ + wz + off;
                for (int x = 0; x < 32; x++)
                {
                    int wx = 32 * ox + x;
                    double snX = SnWorld.OriginX + wx + off;
                    Vec3 u = S8aConvert.ColumnDir(map, snX, snZ);
                    for (int y = 0; y < 32; y++)
                    {
                        int wy = 32 * oy + y;
                        double snY = SnWorld.OriginY + wy + off;
                        double d;
                        byte m;
                        if (!_sampler.TrySample(S8aConvert.SnToSeOnColumn(map, u, snY), out d, out m)) continue;
                        into.Sampled++;
                        if (d < 0.0) continue;
                        byte type = canon[m];
                        if (type == 0) continue;
                        into.Ore++;
                        long c;
                        into.BySnType[type] = (into.BySnType.TryGetValue(type, out c) ? c : 0L) + 1L;
                        int kind = -1;
                        if (!mapSolid(wx, wy, wz)) kind = 0;
                        else if (!mapSolid(wx + 1, wy, wz) || !mapSolid(wx - 1, wy, wz) || !mapSolid(wx, wy + 1, wz) || !mapSolid(wx, wy - 1, wz) ||
                                 !mapSolid(wx, wy, wz + 1) || !mapSolid(wx, wy, wz - 1)) kind = 1;
                        else
                        {
                            for (int k = 0; k < 27 && kind < 0; k++)
                            {
                                int dx = k % 3 - 1, dy = (k / 3) % 3 - 1, dz = k / 9 - 1;
                                if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) < 2) continue;
                                if (!mapSolid(wx + dx, wy + dy, wz + dz)) kind = 2;
                            }
                        }
                        if (kind < 0) continue;
                        if (kind == 0) into.Above++;
                        else if (kind == 1) into.Face++;
                        else into.Cube++;
                        if (into.Examples.Count < 16) into.Examples.Add(new double[] { snX, snY, snZ, type, kind });
                    }
                }
            }
        }

        /// <summary>The SE buffer over the preimage box (reference tile voxel coordinates): every open tile whose box meets it contributes its part.</summary>
        private bool FillBuffer(IS8aTileSource tiles, Vec3[] placeMins, bool[] open, Vec3 refMin, int minX, int minY, int minZ, int sx, int sy, int sz)
        {
            bool any = false;
            int maxX = minX + sx - 1, maxY = minY + sy - 1, maxZ = minZ + sz - 1;
            int tile = tiles.TileSize;
            for (int t = 0; t < tiles.Count; t++)
            {
                if (!open[t]) continue;
                int ox, oy, oz;
                Offset(placeMins[t], refMin, out ox, out oy, out oz);     // t's voxel v ↔ reference voxel v + offset
                int lx0 = Math.Max(minX, ox), ly0 = Math.Max(minY, oy), lz0 = Math.Max(minZ, oz);
                int lx1 = Math.Min(maxX, ox + tile - 1), ly1 = Math.Min(maxY, oy + tile - 1), lz1 = Math.Min(maxZ, oz + tile - 1);
                if (lx1 < lx0 || ly1 < ly0 || lz1 < lz0) continue;
                int rx = lx1 - lx0 + 1, ry = ly1 - ly0 + 1, rz = lz1 - lz0 + 1;
                int n = rx * ry * rz;
                if (_rangeC == null || _rangeC.Length < n) { _rangeC = new byte[n]; _rangeM = new byte[n]; }
                tiles.ReadRange(t, lx0 - ox, ly0 - oy, lz0 - oz, lx1 - ox, ly1 - oy, lz1 - oz, _rangeC, _rangeM);
                for (int z = 0; z < rz; z++)
                    for (int y = 0; y < ry; y++)
                    {
                        int src = rx * (y + ry * z);
                        int dst = (lx0 - minX) + sx * ((ly0 - minY + y) + sy * (lz0 - minZ + z));
                        Buffer.BlockCopy(_rangeC, src, _content, dst, rx);
                        Buffer.BlockCopy(_rangeM, src, _material, dst, rx);
                    }
                any = true;
            }
            return any;
        }
    }
    /// <summary>The ore cover scan's counts (<see cref="S8aConvertCore.ScanOreCover"/>).</summary>
    public sealed class S8aOreCoverStats
    {
        public int Octrees, Skipped;
        /// <summary>SN voxels sampled, SE ore among them, and those above the map, on its surface (an empty face neighbour), within the cover (an empty voxel of the 3 x 3 x 3).</summary>
        public long Sampled, Ore, Above, Face, Cube;
        public readonly System.Collections.Generic.Dictionary<byte, long> BySnType = new System.Collections.Generic.Dictionary<byte, long>();
        /// <summary>The first offenders: SN x, y, z, the Subnautica ore type, the kind (0 above, 1 on the surface, 2 within the cover).</summary>
        public readonly System.Collections.Generic.List<double[]> Examples = new System.Collections.Generic.List<double[]>();
    }

}
