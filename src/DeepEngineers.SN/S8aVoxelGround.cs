using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Review HIGH 8 (props over holes): the ground under an object read from Subnautica's own voxels, not its colliders
 /// (colliders exist only near the camera, clipmap level 0, so an object waking further away could never be judged): the batch
 /// file Subnautica serves for that batch now (the store's carved version, else the install's), or the install's original (the
 /// seabed before any carve). A small cache of parsed files and decoded octrees; main thread.
 /// </summary>
    internal static class S8aVoxelGround
    {
        /// <summary>The install's path per batch, recorded by the GetPath postfix before the store's path replaces it.</summary>
        public static readonly ConcurrentDictionary<long, string> InstallPaths = new ConcurrentDictionary<long, string>();
        private const int MaxFiles = 6, MaxOctrees = 24;
        private static readonly Dictionary<string, S8aBatchFile> Files = new Dictionary<string, S8aBatchFile>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> FileOrder = new List<string>();
        private static readonly Dictionary<string, KeyValuePair<byte[], byte[]>> Octrees = new Dictionary<string, KeyValuePair<byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> OctreeOrder = new List<string>();
        public static long Reads, Unknown;

        public static long Key(int bx, int by, int bz) { unchecked { return ((long)(bx & 0xFFFFF) << 40) | ((long)(by & 0xFFFFF) << 20) | (long)(bz & 0xFFFFF); } }

        /// <summary>1 solid, 0 empty, -1 unknown (no file) at SN point p: the served version, or the install's original.</summary>
        public static int Solid(Vector3 p, bool original)
        {
            int vx, vy, vz;
            S8aConvert.VoxelOf(new Vec3(p.x, p.y, p.z), out vx, out vy, out vz);
            int bx = S8aConvert.BatchOfVoxel(vx), by = S8aConvert.BatchOfVoxel(vy), bz = S8aConvert.BatchOfVoxel(vz);
            string path = null;
            if (!original) path = S8aTerrain.ServedPath(bx, by, bz);
            if (path == null) InstallPaths.TryGetValue(Key(bx, by, bz), out path);
            if (path == null) { Unknown++; return -1; }
            S8aBatchFile b = File(path, bx, by, bz);
            if (b == null) { Unknown++; return -1; }
            int ox = (vx - bx * 160) >> 5, oy = (vy - by * 160) >> 5, oz = (vz - bz * 160) >> 5;
            int oi = S8aBatchFile.OctreeIndex(ox, oy, oz);
            string ok = path + ":" + oi;
            KeyValuePair<byte[], byte[]> td;
            if (!Octrees.TryGetValue(ok, out td))
            {
                var t = new byte[S8aOctree.Voxels];
                var d = new byte[S8aOctree.Voxels];
                b.DecodeOctree(oi, t, d);
                td = new KeyValuePair<byte[], byte[]>(t, d);
                Octrees[ok] = td;
                OctreeOrder.Add(ok);
                if (OctreeOrder.Count > MaxOctrees) { Octrees.Remove(OctreeOrder[0]); OctreeOrder.RemoveAt(0); }
            }
            int li = S8aOctree.Index((vx - bx * 160) & 31, (vy - by * 160) & 31, (vz - bz * 160) & 31);
            Reads++;
            return S8aOctree.IsBelowSurface(td.Key[li], td.Value[li]) ? 1 : 0;
        }

        /// <summary>1 when a solid voxel lies between above over the root and depth under it (0.5 m steps), 0 when none, -1 unknown.</summary>
        public static int GroundWithin(Vector3 root, float above, float depth, bool original)
        {
            bool unknown = false;
            for (float dy = above; dy >= -depth - 1e-3f; dy -= 0.5f)
            {
                int s = Solid(new Vector3(root.x, root.y + dy, root.z), original);
                if (s == 1) return 1;
                if (s < 0) unknown = true;
            }
            return unknown ? -1 : 0;
        }

        private static S8aBatchFile File(string path, int bx, int by, int bz)
        {
            S8aBatchFile b;
            if (Files.TryGetValue(path, out b)) return b;
            try
            {
                if (!System.IO.File.Exists(path)) return null;
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                string err;
                if (!S8aBatchFile.TryParse(bytes, bytes.Length, bx, by, bz, out b, out err)) return null;
            }
            catch (Exception) { return null; }
            Files[path] = b;
            FileOrder.Add(path);
            if (FileOrder.Count > MaxFiles)
            {
                string old = FileOrder[0];
                FileOrder.RemoveAt(0);
                Files.Remove(old);
                OctreeOrder.RemoveAll(k => k.StartsWith(old + ":", StringComparison.OrdinalIgnoreCase));
                var keep = new Dictionary<string, KeyValuePair<byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in OctreeOrder) keep[k] = Octrees[k];
                Octrees.Clear();
                foreach (var kv in keep) Octrees[kv.Key] = kv.Value;
            }
            return b;
        }

        public static void Clear()
        {
            Files.Clear(); FileOrder.Clear(); Octrees.Clear(); OctreeOrder.Clear();
        }
    }
}
