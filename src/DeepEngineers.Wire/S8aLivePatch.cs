using System;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// A new version of
 /// a served batch is applied to the octrees Subnautica holds in memory instead of reloading the batch: only the octrees
 /// whose node bytes changed are replaced, each exactly as <c>BatchOctrees.LoadOctrees</c> would load it from the new file
 /// so the patched memory equals a reload of the file byte for byte (<see cref="LoadedBytes"/>; the round trip
 /// of S8a.ND.roundtrip compares Subnautica's loaded octrees with it).
 /// </summary>
    public static class S8aLivePatch
    {
        /// <summary>The file order indices of the octrees whose stored node bytes differ between two versions of a batch.</summary>
        public static List<int> ChangedOctrees(S8aBatchFile before, S8aBatchFile after)
        {
            if (before == null || after == null) throw new ArgumentNullException(before == null ? "before" : "after");
            if (before.Bx != after.Bx || before.By != after.By || before.Bz != after.Bz) throw new ArgumentException("two versions of one batch expected");
            var changed = new List<int>();
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++)
            {
                if (!after.Has(i)) continue;
                byte[] a, b;
                int ao, bo, an, bn;
                before.GetOctree(i, out a, out ao, out an);
                after.GetOctree(i, out b, out bo, out bn);
                if (!before.Has(i) || an != bn || !Same(a, ao, b, bo, bn * S8aOctree.BytesPerNode)) changed.Add(i);
            }
            return changed;
        }

        private static bool Same(byte[] a, int ao, byte[] b, int bo, int n)
        {
            for (int k = 0; k < n; k++) if (a[ao + k] != b[bo + k]) return false;
            return true;
        }

        /// <summary>The record <c>Octree.Read</c> consumes for octree i of a file: u16 node count (little-endian), then the nodes.</summary>
        public static byte[] ReadRecord(S8aBatchFile file, int i)
        {
            byte[] a;
            int off, n;
            file.GetOctree(i, out a, out off, out n);
            var r = new byte[2 + n * S8aOctree.BytesPerNode];
            r[0] = (byte)(n & 0xFF);
            r[1] = (byte)(n >> 8);
            if (n > 0) Buffer.BlockCopy(a, off, r, 2, n * S8aOctree.BytesPerNode);
            return r;
        }

        /// <summary>
 /// The bytes Subnautica holds for octree i of a file after <c>Read</c> and <c>UnloadChildren(minLod)</c>: the nodes, or for
 /// minLod ≥ 1 a root that is not a leaf the first 36 bytes (root + 8 children) with the children made leaves (first child
 /// id 0); an octree with no nodes holds nothing (the empty array).
 /// </summary>
        public static byte[] LoadedBytes(S8aBatchFile file, int i, int minLod)
        {
            byte[] a;
            int off, n;
            file.GetOctree(i, out a, out off, out n);
            return LoadedBytes(a, off, n, minLod);
        }

        public static byte[] LoadedBytes(byte[] nodes, int offset, int nodeCount, int minLod)
        {
            int len = nodeCount * S8aOctree.BytesPerNode;
            if (len == 0) return new byte[0];
            bool rootLeaf = nodes[offset + 2] == 0 && nodes[offset + 3] == 0;
            if (minLod >= 1 && !rootLeaf && len != 36)
            {
                var dst = new byte[36];
                if (len >= 36) Buffer.BlockCopy(nodes, offset, dst, 0, 36);
                for (int c = 1; c <= 8; c++) { dst[c * 4 + 2] = 0; dst[c * 4 + 3] = 0; }
                return dst;
            }
            var r = new byte[len];
            Buffer.BlockCopy(nodes, offset, r, 0, len);
            return r;
        }

        /// <summary>
 /// The in-memory model of a batch as a streamer holds it (index → loaded bytes, the octrees it reads: all 125 for a window
 /// id, the stored ones for a crater id), from a file.
 /// </summary>
        public static Dictionary<int, byte[]> Load(S8aBatchFile file, int minLod)
        {
            var m = new Dictionary<int, byte[]>();
            for (int i = 0; i < S8aBatchFile.OctreesPerBatch; i++) if (file.Has(i)) m[i] = LoadedBytes(file, i, minLod);
            return m;
        }

        /// <summary>The live edit on the model: the changed octrees of <paramref name="after"/> replace their entries (in place).</summary>
        public static int Apply(Dictionary<int, byte[]> model, S8aBatchFile before, S8aBatchFile after, int minLod)
        {
            List<int> changed = ChangedOctrees(before, after);
            foreach (int i in changed) model[i] = LoadedBytes(after, i, minLod);
            return changed.Count;
        }

        /// <summary>Whether two models hold the same octrees with byte-identical nodes.</summary>
        public static bool SameModel(Dictionary<int, byte[]> a, Dictionary<int, byte[]> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<int, byte[]> kv in a)
            {
                byte[] o;
                if (!b.TryGetValue(kv.Key, out o) || o.Length != kv.Value.Length) return false;
                if (!Same(kv.Value, 0, o, 0, o.Length)) return false;
            }
            return true;
        }

        /// <summary>
 /// Stage D (2): the clipmap levels a live edit of a batch rebuilds now: level 0 always; levels 1–3 when the batch's last
 /// higher-level rebuild is at least <see cref="HigherLevelsEveryMs"/> old (<paramref name="lastHigherMs"/> 0 = never).
 /// </summary>
        public const ulong HigherLevelsEveryMs = 1000;

        public static bool HigherLevelsDue(ulong nowMs, ulong lastHigherMs)
        {
            return lastHigherMs == 0 || nowMs - lastHigherMs >= HigherLevelsEveryMs;
        }

        /// <summary>
 /// The per-frame visibility of a clipmap cell (Stage D oracle): covered when some level shows its area
 /// Loaded (5) waits for the
 /// visibility updater and every loading or unloading state shows nothing.
 /// </summary>
        public static bool Covered(int cellState) { return cellState >= 6 && cellState <= 8; }

        /// <summary>A loaded cell the live edit can rebuild in place (Loaded, Visible, HiddenByParent, HiddenByChildren).</summary>
        public static bool Rebuildable(int cellState) { return cellState >= 5 && cellState <= 8; }

        /// <summary>A cell building its mesh (BuildingMesh 3, BuildingLayers 4): it may have read the old octrees, so it is rebuilt once loaded.</summary>
        public static bool Building(int cellState) { return cellState == 3 || cellState == 4; }
    }
}
