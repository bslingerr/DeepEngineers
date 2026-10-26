using System;
using System.Collections.Generic;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a window plan (CONTRACT §3.3 "Window", §5.4 formats window.set, window.ready, window.center). Replicates
 // Subnautica's octree ring centre (WorldStreaming.WorldStreamer.UpdateStreamingCenter: (Int3)InverseTransformPoint(wsPos +
 // (0, −8, 0)), a truncating cast; BatchOctreesStreamer.UpdateCenter: centerBounds.Clamp(Int3.FloorDiv(position, 160)); the
 // ring Int3.CenterSize(centre, 5) = centre − 2 … centre + 2; VERIFIEDInt3.cs explicit operator Int3(Vector3),
 // CenterSize, FloorDiv) with the centre clamp widened to the window universe by the constructor prefix. Pure; Python
 // twin tools/planet/s8a_ref.py (vectors tests/s8a/vectors/window.tsv).
 // =================================================================================================================

 /// <summary>An integer triple (batch id) usable as a dictionary key.</summary>
    public struct Int3Key : IEquatable<Int3Key>
    {
        public readonly int X, Y, Z;

        public Int3Key(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(Int3Key o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is Int3Key && Equals((Int3Key)obj); }
        public override int GetHashCode() { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); }
        public static bool operator ==(Int3Key a, Int3Key b) { return a.Equals(b); }
        public static bool operator !=(Int3Key a, Int3Key b) { return !a.Equals(b); }
        public override string ToString() { return X + "-" + Y + "-" + Z; }
    }

    /// <summary>The window's batch set around the camera ("window.set") and the near box ("window.ready").</summary>
    public static class S8aWindowPlan
    {
        /// <summary>
 /// The window manifest after a batch was generated: a served file gets its line (<see cref="S8aManifest.Set"/>);
 /// an all-water batch has no file and no line (the manifest lists served files only; <see cref="S8aManifest.Set"/> throws on an
 /// empty path), so a previous line of that id is removed. Returns true when the manifest now has a line for the id.
 /// </summary>
        public static bool RecordGenerated(S8aManifest m, Int3Key id, bool water, uint version, long bytes, ulong fnv, ushort flags, string relativePath)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (water)
            {
                m.Remove(id.X, id.Y, id.Z);
                return false;
            }
            m.Set(new S8aManifest.Entry { Bx = id.X, By = id.Y, Bz = id.Z, Version = version, Bytes = bytes, Fnv = fnv, Flags = flags, RelativePath = relativePath });
            return true;
        }

        /// <summary>sin 22.5°: a velocity component counts toward the look-ahead when it is at least this share of the horizontal speed.</summary>
        public const double LookAheadAxisShare = 0.38268343236508978;

        /// <summary>bx and bz in the crater box [CraterMin, CraterMax] (the install's ids; never a window id).</summary>
        public static bool InCraterBox(int bx, int bz)
        {
            return bx >= WindowC.CraterMin && bx <= WindowC.CraterMax && bz >= WindowC.CraterMin && bz <= WindowC.CraterMax;
        }

        /// <summary>A window id: bx, bz in [UniverseMin, UniverseMax] outside the crater box, by in [0, BatchesY − 1].</summary>
        public static bool IsWindowId(int bx, int by, int bz)
        {
            return bx >= WindowC.UniverseMin && bx <= WindowC.UniverseMax && bz >= WindowC.UniverseMin && bz <= WindowC.UniverseMax &&
                   by >= 0 && by < SnWorld.BatchesY && !InCraterBox(bx, bz);
        }

        /// <summary>The batch id holding an SN point (<c>FloorDiv(Floor(p − Origin), 160)</c>).</summary>
        public static Int3Key BatchOf(Vec3 sn)
        {
            int vx, vy, vz;
            S8aConvert.VoxelOf(sn, out vx, out vy, out vz);
            return new Int3Key(S8aConvert.BatchOfVoxel(vx), S8aConvert.BatchOfVoxel(vy), S8aConvert.BatchOfVoxel(vz));
        }

        /// <summary>SN world box of a batch: min = Origin + 160·id, max = min + 160.</summary>
        public static void BatchBounds(Int3Key id, out Vec3 min, out Vec3 max)
        {
            min = new Vec3(SnWorld.OriginX + SnWorld.BatchVoxels * (double)id.X, SnWorld.OriginY + SnWorld.BatchVoxels * (double)id.Y,
                           SnWorld.OriginZ + SnWorld.BatchVoxels * (double)id.Z);
            max = new Vec3(min.X + SnWorld.BatchVoxels, min.Y + SnWorld.BatchVoxels, min.Z + SnWorld.BatchVoxels);
        }

        /// <summary>
 /// The octree ring centre Subnautica computes for a camera at <paramref name="camSn"/> (float arithmetic as Unity:
 /// <c>(int)((float)x − (float)OriginX)</c>, y 8 m lower, a truncating cast, then FloorDiv 160 and the clamp to the
 /// widened centre bounds (UniverseMin, 0, UniverseMin) … (UniverseMax, 20, UniverseMax)).
 /// </summary>
        public static Int3Key StreamCentre(Vec3 camSn)
        {
            float wx = (float)camSn.X, wy = (float)camSn.Y + (float)(-WindowC.StreamCenterDownM), wz = (float)camSn.Z;
            float lx = wx - (float)SnWorld.OriginX, ly = wy - (float)SnWorld.OriginY, lz = wz - (float)SnWorld.OriginZ;
            int px = TruncF(lx), py = TruncF(ly), pz = TruncF(lz);
            int cx = S8aConvert.FloorDiv(px, SnWorld.BatchVoxels), cy = S8aConvert.FloorDiv(py, SnWorld.BatchVoxels), cz = S8aConvert.FloorDiv(pz, SnWorld.BatchVoxels);
            cx = Clamp(cx, WindowC.UniverseMin, WindowC.UniverseMax);
            cy = Clamp(cy, 0, SnWorld.BatchesY);
            cz = Clamp(cz, WindowC.UniverseMin, WindowC.UniverseMax);
            return new Int3Key(cx, cy, cz);
        }

        private static int TruncF(float v)
        {
            if (float.IsNaN(v)) return 0;
            if (v >= 2147483520f) return int.MaxValue;
            if (v <= -2147483648f) return int.MinValue;
            return (int)v;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>
 /// "window.set": <paramref name="ids"/> (cleared first) = the window ids of the RingBatches³ ring around
 /// <see cref="StreamCentre"/>, plus, when the horizontal speed is ≥ LookAheadMinSpeedCmS, the ids of the same ring moved
 /// LookAheadBatches along each horizontal axis whose velocity share is ≥ sin 22.5°; crater ids and ids outside the
 /// universe excluded; nearest first (squared distance from the camera to the batch box, then to the batch centre,
 /// then bx, by, bz).
 /// </summary>
        public static void Plan(Vec3 camSn, Vec3 velSn, List<Int3Key> ids)
        {
            if (ids == null) throw new ArgumentNullException("ids");
            ids.Clear();
            Int3Key c = StreamCentre(camSn);
            AddRing(c, ids);
            double vx = velSn.X, vz = velSn.Z;
            double speed = Math.Sqrt(vx * vx + vz * vz);
            if (speed * 100.0 >= WindowC.LookAheadMinSpeedCmS)
            {
                int sx = Math.Abs(vx) >= LookAheadAxisShare * speed ? (vx < 0 ? -1 : 1) : 0;
                int sz = Math.Abs(vz) >= LookAheadAxisShare * speed ? (vz < 0 ? -1 : 1) : 0;
                if (sx != 0 || sz != 0)
                {
                    var a = new Int3Key(Clamp(c.X + sx * WindowC.LookAheadBatches, WindowC.UniverseMin, WindowC.UniverseMax), c.Y,
                                        Clamp(c.Z + sz * WindowC.LookAheadBatches, WindowC.UniverseMin, WindowC.UniverseMax));
                    if (a != c) AddRing(a, ids);
                }
            }
            Sort(camSn, ids);
        }

        private static void AddRing(Int3Key c, List<Int3Key> ids)
        {
            int h = WindowC.RingBatches / 2;
            for (int x = c.X - h; x <= c.X - h + WindowC.RingBatches - 1; x++)
                for (int y = c.Y - h; y <= c.Y - h + WindowC.RingBatches - 1; y++)
                    for (int z = c.Z - h; z <= c.Z - h + WindowC.RingBatches - 1; z++)
                    {
                        if (!IsWindowId(x, y, z)) continue;
                        var k = new Int3Key(x, y, z);
                        if (!ids.Contains(k)) ids.Add(k);
                    }
        }

        /// <summary>Sorts ids nearest first from <paramref name="camSn"/> (box distance², centre distance², bx, by, bz).</summary>
        public static void Sort(Vec3 camSn, List<Int3Key> ids)
        {
            ids.Sort((a, b) =>
            {
                int c = BoxDist2(camSn, a).CompareTo(BoxDist2(camSn, b));
                if (c != 0) return c;
                c = CentreDist2(camSn, a).CompareTo(CentreDist2(camSn, b));
                if (c != 0) return c;
                c = a.X.CompareTo(b.X);
                if (c != 0) return c;
                c = a.Y.CompareTo(b.Y);
                return c != 0 ? c : a.Z.CompareTo(b.Z);
            });
        }

        /// <summary>Squared distance from an SN point to a batch's box (0 inside).</summary>
        public static double BoxDist2(Vec3 p, Int3Key id)
        {
            Vec3 mn, mx;
            BatchBounds(id, out mn, out mx);
            double dx = p.X < mn.X ? mn.X - p.X : p.X > mx.X ? p.X - mx.X : 0.0;
            double dy = p.Y < mn.Y ? mn.Y - p.Y : p.Y > mx.Y ? p.Y - mx.Y : 0.0;
            double dz = p.Z < mn.Z ? mn.Z - p.Z : p.Z > mx.Z ? p.Z - mx.Z : 0.0;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>Squared distance from an SN point to a batch's centre.</summary>
        public static double CentreDist2(Vec3 p, Int3Key id)
        {
            Vec3 mn, mx;
            BatchBounds(id, out mn, out mx);
            double dx = p.X - (mn.X + 80.0), dy = p.Y - (mn.Y + 80.0), dz = p.Z - (mn.Z + 80.0);
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
 /// "window.ready": the window ids meeting the camera's ±NearBoxM box (cleared first; sorted by bx, by, bz). NearPending
 /// while one of them is neither written nor known empty and SE's surface is within NearSurfaceM of the camera.
 /// </summary>
        public static void NearBox(Vec3 camSn, List<Int3Key> ids)
        {
            if (ids == null) throw new ArgumentNullException("ids");
            ids.Clear();
            double r = WindowC.NearBoxM;
            Int3Key lo = BatchOf(new Vec3(camSn.X - r, camSn.Y - r, camSn.Z - r));
            Int3Key hi = BatchOf(new Vec3(camSn.X + r, camSn.Y + r, camSn.Z + r));
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                        if (IsWindowId(x, y, z)) ids.Add(new Int3Key(x, y, z));
        }

        /// <summary>Whether an SN point lies in a window batch (outside the crater box, inside the universe).</summary>
        public static bool InWindow(Vec3 sn)
        {
            Int3Key k = BatchOf(sn);
            return IsWindowId(k.X, k.Y, k.Z);
        }
    }
}
