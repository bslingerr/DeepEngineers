namespace DeepEngineers.Wire
{
    /// <summary>
 /// S8a window: the materials of an SE LOD-0 box of which SE computed only the shell (the voxels
 /// within one voxel of a non-full one, S8aPlanetSampler.ReadShell). SE's cut is binary, so the first solid Subnautica voxel behind
 /// a cavity can be far-solid, and its type is its stencil's fullest sample, with all 8 full the first in index order
 /// ("convert.sample"), which may lie a voxel outside the shell. A solid voxel outside the shell next to it therefore takes its
 /// shell neighbour's material (layer by layer over the 6 neighbours in the order x-, x+, y-, y+, z-, z+, first found among the
 /// voxels of the earlier layers); only solid voxels no path through solid voxels joins to the shell take the box's most common
 /// shell material. a test run (n10 session 20261023-213828): a Cobalt cavity's +x wall read sand, the box's most common, where SE's
 /// own material was the vein's (two layers then). a test run (n10 session 20261024-003902, the same cavity at (4767, -965)): the +x wall
 /// voxel (SN block 6820, 2851, 1082; octree 98 of batch 42-17-6, local 4, 3, 26) is one step from the cut and took the vein's
 /// material, but it lies in a 4 x 4 x 4 block of far-solid voxels, which Subnautica's collapse rule ("octree.write", OctNode.CanCollapse:
 /// no near child, child 0's value) writes as one leaf with its child 0's type: local (4, 0, 24), four steps from the cut, one layer
 /// past the two, so the box's most common (Sand_02). Every layer now: a far voxel takes its nearest shell voxel's material, so a
 /// collapsed block behind a wall carries the wall's. VRage-free (Wire.Tests).
 /// </summary>
    public static class S8aShellFill
    {
        /// <summary>
 /// <paramref name="shell"/>[i] != 0: a shell voxel whose material SE computed (<paramref name="m"/>[i]); <paramref name="content"/>[i] == 0:
 /// empty (material 255). Fills <paramref name="material"/> (x fastest, sx·sy·sz); <paramref name="mk"/>, <paramref name="tmp"/>: scratch of
 /// at least that length. <paramref name="near"/>: voxels filled from a neighbour, <paramref name="far"/>: from <paramref name="boxMost"/>.
 /// </summary>
        public static void Fill(byte[] content, byte[] shell, byte[] m, byte[] material, int sx, int sy, int sz, byte boxMost, byte[] mk, byte[] tmp,
                                out long near, out long far)
        {
            int n = sx * sy * sz, sxy = sx * sy;
            near = far = 0;
            for (int i = 0; i < n; i++)
            {
                if (shell[i] != 0) { material[i] = m[i]; mk[i] = 1; }
                else { material[i] = 255; mk[i] = 0; }
            }
            // layer by layer (each voxel once): a layer is the unmarked solid voxels next to the earlier layers, each takes the material
 // of its first neighbour (x-, x+, y-, y+, z-, z+) from an earlier layer (mk 1; this layer's are 2 until the layer ends), so
 // layers 1 and 2 are exactly the former two passes (tmp is no longer used). One index queue: the shell, then each layer
            int[] q = _queue;
            if (q == null || q.Length < n) _queue = q = new int[n];
            int tail = 0;
            for (int i = 0; i < n; i++) if (mk[i] == 1) q[tail++] = i;
            int ls = 0, le = tail;
            while (ls < le)
            {
                for (int k = ls; k < le; k++)
                {
                    int i = q[k];
                    int x = i % sx, y = (i / sx) % sy, z = i / sxy;
                    if (x > 0) Join(i - 1, content, mk, q, ref tail);
                    if (x + 1 < sx) Join(i + 1, content, mk, q, ref tail);
                    if (y > 0) Join(i - sx, content, mk, q, ref tail);
                    if (y + 1 < sy) Join(i + sx, content, mk, q, ref tail);
                    if (z > 0) Join(i - sxy, content, mk, q, ref tail);
                    if (z + 1 < sz) Join(i + sxy, content, mk, q, ref tail);
                }
                for (int k = le; k < tail; k++)
                {
                    int i = q[k];
                    int x = i % sx, y = (i / sx) % sy, z = i / sxy;
                    int j = x > 0 && mk[i - 1] == 1 ? i - 1 : x + 1 < sx && mk[i + 1] == 1 ? i + 1 : y > 0 && mk[i - sx] == 1 ? i - sx :
                            y + 1 < sy && mk[i + sx] == 1 ? i + sx : z > 0 && mk[i - sxy] == 1 ? i - sxy : z + 1 < sz && mk[i + sxy] == 1 ? i + sxy : -1;
                    material[i] = material[j];     // j >= 0: the voxel joined from an earlier layer's neighbour
                    near++;
                }
                for (int k = le; k < tail; k++) mk[q[k]] = 1;
                ls = le;
                le = tail;
            }
            for (int i = 0; i < n; i++)
            {
                if (mk[i] != 0 || content[i] == 0) continue;
                material[i] = boxMost;
                far++;
            }
        }

        [System.ThreadStatic] private static int[] _queue;

        /// <summary>An unmarked solid voxel joins the next layer (marked 2 until that layer ends).</summary>
        private static void Join(int j, byte[] content, byte[] mk, int[] q, ref int tail)
        {
            if (mk[j] != 0 || content[j] == 0) return;
            mk[j] = 2;
            q[tail++] = j;
        }
    }
}
