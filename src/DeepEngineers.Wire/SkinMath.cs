using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 skinned-mesh codec support (CONTRACT §5.4 "Character"): the pure array work of the character export (SE side)
 // and of the bone tree (Subnautica side), with no game type:
 // - SkeletonMap: a model's MyModelBone[] (names, parent indices) mapped to the canonical rows of canon1 by name;
 // every canonical bone and its parent link must be present (else the export falls back to the default Engineer);
 // - RemapInfluences: a vertex's weights moved to canonical indices (a non-canonical bone gives its weight to its
 // nearest canonical ancestor), duplicates summed in slot order, the 4 largest kept (ties: lower index), renormalised;
 // - AbsoluteFromLocal / SkinnedBind: abs = local × parentAbs (row order); SkinnedBind_i = abs_i⁻¹ (SE's SkinTransform);
 // - SplitByMaterial: the |char=body / |char=head1st models, each holding only the vertices its submeshes use;
 // - BoneLocals: C1 absolute bones to parent-relative locals (abs_i × abs_parent⁻¹) for the Unity bone tree;
 // - SkinPointSe: the SE skinning reference (Σ w·(v × bind_i × abs_i)).
 // The ModelCodec payload (76 B skinned vertices) and EncodeSkinnedBind are unchanged (layout 1.0). Python twin:
 // tools/worldmap/wire_ref.py (s5_bone_map, s5_remap_weights, s5_split, s5_skin_point), compared by S5VectorTests.
 // =================================================================================================================

 /// <summary>A model skeleton mapped onto the canonical skeleton <c>canon1</c>.</summary>
    public sealed class SkeletonMap
    {
        /// <summary>Bones of the source model.</summary>
        public int ModelBoneCount { get; private set; }
        /// <summary>Model bone index of each canonical row.</summary>
        public readonly int[] CanonicalToModel = new int[MirrorSkeleton.BoneCount];
        /// <summary>Canonical row of each model bone, −1 when the bone is not canonical.</summary>
        public int[] ModelToCanonical { get; private set; }
        /// <summary>Canonical row receiving each model bone's weights: itself, else its nearest canonical ancestor, else 0.</summary>
        public int[] ModelToCanonicalAncestor { get; private set; }
        /// <summary>Model bones that are not canonical (their weights move up).</summary>
        public int NonCanonicalBones { get; private set; }

        private SkeletonMap() { }

        /// <summary>
 /// Maps a model's bones (<c>MyModelBone.Name</c>, <c>MyModelBone.Parent</c>; −1 = root) to <c>canon1</c> by name
 /// (ordinal). Fails (false, a reason for <c>DE.S5 char FALLBACK</c>) when a name repeats, a parent index is out of
 /// range or cyclic, a canonical bone is missing, or a canonical bone's model parent is not its canonical parent.
 /// </summary>
        public static bool TryBuild(IList<string> names, IList<int> parents, out SkeletonMap map, out string error)
        {
            map = null;
            error = null;
            if (names == null || parents == null || names.Count != parents.Count) { error = "bone names and parents differ in count"; return false; }
            int n = names.Count;
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                if (names[i] == null) { error = "bone " + i + " has no name"; return false; }
                if (byName.ContainsKey(names[i])) { error = "bone name repeated: " + names[i]; return false; }
                byName[names[i]] = i;
                if (parents[i] < -1 || parents[i] >= n || parents[i] == i) { error = "bone " + names[i] + " parent " + parents[i] + " out of range"; return false; }
            }
            var m = new SkeletonMap { ModelBoneCount = n, ModelToCanonical = new int[n], ModelToCanonicalAncestor = new int[n] };
            for (int i = 0; i < n; i++) m.ModelToCanonical[i] = -1;
            for (int c = 0; c < MirrorSkeleton.BoneCount; c++)
            {
                int mi;
                if (!byName.TryGetValue(MirrorSkeleton.BoneName(c), out mi)) { error = "canonical bone missing: " + MirrorSkeleton.BoneName(c); return false; }
                string want = MirrorSkeleton.ParentName(c);
                int mp = parents[mi];
                string got = mp < 0 ? null : names[mp];
                if (!string.Equals(want, got, StringComparison.Ordinal))
                {
                    error = "canonical bone " + MirrorSkeleton.BoneName(c) + " has parent " + (got ?? "(root)") + ", expected " + (want ?? "(root)");
                    return false;
                }
                m.CanonicalToModel[c] = mi;
                m.ModelToCanonical[mi] = c;
            }
            int nonCanonical = 0;
            for (int i = 0; i < n; i++)
            {
                if (m.ModelToCanonical[i] >= 0) { m.ModelToCanonicalAncestor[i] = m.ModelToCanonical[i]; continue; }
                nonCanonical++;
                int target = 0, p = parents[i], steps = 0;
                while (p >= 0)
                {
                    if (++steps > n) { error = "bone parent chain of " + names[i] + " is cyclic"; return false; }
                    if (m.ModelToCanonical[p] >= 0) { target = m.ModelToCanonical[p]; break; }
                    p = parents[p];
                }
                m.ModelToCanonicalAncestor[i] = target;
            }
            m.NonCanonicalBones = nonCanonical;
            map = m;
            return true;
        }
    }

    /// <summary>Skinning helpers (CONTRACT §5.4 "Character").</summary>
    public static class SkinMath
    {
        // ------------------------------------------------------------------------------------------------ weights

 /// <summary>
 /// One vertex's influences (model bone indices, weights; <paramref name="count"/> slots, usually 4) to 4 canonical
 /// slots at <paramref name="dstOffset"/>: weights ≤ 0 or NaN are dropped; each bone's weight goes to
 /// <see cref="SkeletonMap.ModelToCanonicalAncestor"/>; equal targets are summed in slot order (doubles); the 4
 /// largest are kept (ties: lower canonical index first), sorted by weight descending, renormalised to sum 1
 /// (float32 of w / Σ); unused slots get index 0, weight 0. A vertex with no positive weight gets (0, 1).
 /// Returns false when a bone index is outside the model (nothing written). No allocation.
 /// </summary>
        public static bool RemapInfluences(SkeletonMap map, byte[] srcBones, float[] srcWeights, int srcOffset, int count,
                                           byte[] dstBones, float[] dstWeights, int dstOffset)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (count < 0 || count > 8) throw new ArgumentOutOfRangeException("count", "0..8 influences");
            // Up to 8 distinct targets, kept on the stack.
            int t0 = 0, t1 = 0, t2 = 0, t3 = 0, t4 = 0, t5 = 0, t6 = 0, t7 = 0;
            double w0 = 0, w1 = 0, w2 = 0, w3 = 0, w4 = 0, w5 = 0, w6 = 0, w7 = 0;
            int nt = 0;
            for (int k = 0; k < count; k++)
            {
                float w = srcWeights[srcOffset + k];
                if (!(w > 0f)) continue;
                int b = srcBones[srcOffset + k];
                if (b >= map.ModelBoneCount) return false;
                int c = map.ModelToCanonicalAncestor[b];
                int j = 0;
                for (; j < nt; j++) if (Get(j, t0, t1, t2, t3, t4, t5, t6, t7) == c) break;
                if (j == nt) { Set(j, c, ref t0, ref t1, ref t2, ref t3, ref t4, ref t5, ref t6, ref t7); nt++; }
                AddW(j, w, ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7);
            }
            // Selection sort by (weight desc, index asc): at most 4 picks over at most 8 entries.
            int keep = Math.Min(nt, MirrorLimits.MaxSkinWeights);
            int used = 0; // bit mask of picked entries
            int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
            double v0 = 0, v1 = 0, v2 = 0, v3 = 0;
            for (int s = 0; s < keep; s++)
            {
                int best = -1;
                double bw = 0; int bi = 0;
                for (int j = 0; j < nt; j++)
                {
                    if ((used & (1 << j)) != 0) continue;
                    double wj = Get(j, w0, w1, w2, w3, w4, w5, w6, w7);
                    int ij = Get(j, t0, t1, t2, t3, t4, t5, t6, t7);
                    if (best < 0 || wj > bw || (wj == bw && ij < bi)) { best = j; bw = wj; bi = ij; }
                }
                used |= 1 << best;
                switch (s)
                {
                    case 0: i0 = bi; v0 = bw; break;
                    case 1: i1 = bi; v1 = bw; break;
                    case 2: i2 = bi; v2 = bw; break;
                    default: i3 = bi; v3 = bw; break;
                }
            }
            int o = dstOffset;
            if (keep == 0)
            {
                dstBones[o] = 0; dstBones[o + 1] = 0; dstBones[o + 2] = 0; dstBones[o + 3] = 0;
                dstWeights[o] = 1f; dstWeights[o + 1] = 0f; dstWeights[o + 2] = 0f; dstWeights[o + 3] = 0f;
                return true;
            }
            double total = v0;
            if (keep > 1) total += v1;
            if (keep > 2) total += v2;
            if (keep > 3) total += v3;
            dstBones[o] = (byte)i0; dstWeights[o] = (float)(v0 / total);
            dstBones[o + 1] = keep > 1 ? (byte)i1 : (byte)0; dstWeights[o + 1] = keep > 1 ? (float)(v1 / total) : 0f;
            dstBones[o + 2] = keep > 2 ? (byte)i2 : (byte)0; dstWeights[o + 2] = keep > 2 ? (float)(v2 / total) : 0f;
            dstBones[o + 3] = keep > 3 ? (byte)i3 : (byte)0; dstWeights[o + 3] = keep > 3 ? (float)(v3 / total) : 0f;
            return true;
        }

        private static T Get<T>(int j, T a0, T a1, T a2, T a3, T a4, T a5, T a6, T a7)
        {
            switch (j)
            {
                case 0: return a0; case 1: return a1; case 2: return a2; case 3: return a3;
                case 4: return a4; case 5: return a5; case 6: return a6; default: return a7;
            }
        }

        private static void Set(int j, int v, ref int a0, ref int a1, ref int a2, ref int a3, ref int a4, ref int a5, ref int a6, ref int a7)
        {
            switch (j)
            {
                case 0: a0 = v; break; case 1: a1 = v; break; case 2: a2 = v; break; case 3: a3 = v; break;
                case 4: a4 = v; break; case 5: a5 = v; break; case 6: a6 = v; break; default: a7 = v; break;
            }
        }

        private static void AddW(int j, double v, ref double a0, ref double a1, ref double a2, ref double a3, ref double a4, ref double a5, ref double a6, ref double a7)
        {
            switch (j)
            {
                case 0: a0 += v; break; case 1: a1 += v; break; case 2: a2 += v; break; case 3: a3 += v; break;
                case 4: a4 += v; break; case 5: a5 += v; break; case 6: a6 += v; break; default: a7 += v; break;
            }
        }

        /// <summary>
 /// <see cref="RemapInfluences"/> over every vertex of a skinned model, in place (its <c>BoneIndices</c> become
 /// canonical). False with an error when the model is not skinned or a bone index is outside the model.
 /// </summary>
        public static bool RemapModelWeights(ModelGeometry g, SkeletonMap map, out string error)
        {
            error = null;
            if (g == null) throw new ArgumentNullException("g");
            if (!g.Skinned || g.BoneIndices == null || g.BoneWeights == null) { error = "model is not skinned"; return false; }
            var bones = new byte[4];
            var weights = new float[4];
            for (int v = 0; v < g.VertexCount; v++)
            {
                if (!RemapInfluences(map, g.BoneIndices, g.BoneWeights, 4 * v, 4, bones, weights, 0))
                {
                    error = "vertex " + v + " weights a bone outside the model's " + map.ModelBoneCount + " bones";
                    return false;
                }
                for (int k = 0; k < 4; k++) { g.BoneIndices[4 * v + k] = bones[k]; g.BoneWeights[4 * v + k] = weights[k]; }
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ binds and bones

 /// <summary>
 /// Absolute matrices from parent-relative ones (<c>MyModelBone.Transform</c>): abs_i = local_i × abs_parent (row
 /// order), root abs = local. Works for any parent order (chains are resolved recursively; a cycle throws).
 /// </summary>
        public static void AbsoluteFromLocal(IList<SeMatrix> local, IList<int> parents, SeMatrix[] abs)
        {
            if (local == null || parents == null || abs == null) throw new ArgumentNullException("local");
            int n = local.Count;
            if (parents.Count != n || abs.Length < n) throw new ArgumentException("sizes differ");
            var state = new byte[n]; // 0 todo, 1 in progress, 2 done
            for (int i = 0; i < n; i++) Resolve(i, local, parents, abs, state);
        }

        private static void Resolve(int i, IList<SeMatrix> local, IList<int> parents, SeMatrix[] abs, byte[] state)
        {
            if (state[i] == 2) return;
            if (state[i] == 1) throw new ArgumentException("bone parent chain is cyclic at " + i);
            state[i] = 1;
            int p = parents[i];
            if (p < 0) abs[i] = local[i];
            else
            {
                Resolve(p, local, parents, abs, state);
                abs[i] = InstanceMath.Multiply(local[i], abs[p]);
            }
            state[i] = 2;
        }

        /// <summary>
 /// The C3 SkinnedBind rows (78 × 12 floats, canonical order): matrix i = the inverse of canonical bone i's absolute
 /// bind matrix (<paramref name="modelAbsBind"/> by model bone index), which equals SE's <c>SkinTransform</c>.
 /// </summary>
        public static float[] SkinnedBindRows(SkeletonMap map, IList<SeMatrix> modelAbsBind)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (modelAbsBind == null || modelAbsBind.Count != map.ModelBoneCount) throw new ArgumentException("one absolute bind per model bone");
            var rows = new float[MirrorSkeleton.BoneCount * 12];
            for (int c = 0; c < MirrorSkeleton.BoneCount; c++)
                InstanceMath.Invert(modelAbsBind[map.CanonicalToModel[c]]).CopyRowsTo(rows, 12 * c);
            return rows;
        }

        /// <summary>The C3 SkinnedBind of a character model (<see cref="SkinnedBindRows"/>).</summary>
        public static SkinnedBindDefinition MakeSkinnedBind(ulong modelKey, SkeletonMap map, IList<SeMatrix> modelAbsBind)
        {
            return new SkinnedBindDefinition { ModelKey = modelKey, BoneCount = MirrorSkeleton.BoneCount, Rows = SkinnedBindRows(map, modelAbsBind) };
        }

        /// <summary>
 /// The C1 bones in canonical order: <paramref name="modelRows"/> holds 12 floats per model bone
 /// (<c>MyCharacter.BoneAbsoluteTransforms</c> rows, model space); 78 × 12 floats are written to
 /// <paramref name="dst"/> at <paramref name="dstOffset"/>. No allocation.
 /// </summary>
        public static void PickCanonical(SkeletonMap map, float[] modelRows, float[] dst, int dstOffset)
        {
            if (map == null) throw new ArgumentNullException("map");
            if (modelRows == null || modelRows.Length < 12 * map.ModelBoneCount) throw new ArgumentException("12 floats per model bone");
            if (dst == null || dst.Length - dstOffset < 12 * MirrorSkeleton.BoneCount) throw new ArgumentException("room for 78 bones");
            for (int c = 0; c < MirrorSkeleton.BoneCount; c++)
                Array.Copy(modelRows, 12 * map.CanonicalToModel[c], dst, dstOffset + 12 * c, 12);
        }

        /// <summary>
 /// Parent-relative bone matrices from canonical absolute ones (C1 bones or abs binds): local_i = abs_i × abs_parent⁻¹
 /// (general inverse: bones may carry scale), root local = abs. <paramref name="absRows"/>: 78 × 12 floats at
 /// <paramref name="offset"/>. No allocation.
 /// </summary>
        public static void BoneLocals(float[] absRows, int offset, SeMatrix[] locals)
        {
            if (absRows == null || absRows.Length - offset < 12 * MirrorSkeleton.BoneCount) throw new ArgumentException("78 bones of 12 floats");
            if (locals == null || locals.Length < MirrorSkeleton.BoneCount) throw new ArgumentException("room for 78 locals");
            for (int i = 0; i < MirrorSkeleton.BoneCount; i++)
            {
                SeMatrix abs = SeMatrix.FromRows(absRows, offset + 12 * i);
                int p = MirrorSkeleton.Parent(i);
                locals[i] = p < 0 ? abs : InstanceMath.RelativeGeneral(abs, SeMatrix.FromRows(absRows, offset + 12 * p));
            }
        }

        /// <summary><see cref="BoneLocals(float[], int, SeMatrix[])"/> for absolute matrices already in doubles.</summary>
        public static void BoneLocals(IList<SeMatrix> abs, SeMatrix[] locals)
        {
            if (abs == null || abs.Count < MirrorSkeleton.BoneCount) throw new ArgumentException("78 absolute bones");
            if (locals == null || locals.Length < MirrorSkeleton.BoneCount) throw new ArgumentException("room for 78 locals");
            for (int i = 0; i < MirrorSkeleton.BoneCount; i++)
            {
                int p = MirrorSkeleton.Parent(i);
                locals[i] = p < 0 ? abs[i] : InstanceMath.RelativeGeneral(abs[i], abs[p]);
            }
        }

        /// <summary>
 /// The SE skinning reference of one vertex: Σ w_k · (v × bind[b_k] × boneAbs[b_k]) over the 4 slots (row order:
 /// model space → bone space → posed model space). The Unity side must equal its mirror:
 /// Σ w_k · (bone_k.localToWorld × bindpose_k) × Mirror(v) with the conjugated matrices.
 /// </summary>
        public static Vec3 SkinPointSe(Vec3 v, byte[] bones, float[] weights, int offset, IList<SeMatrix> bind, IList<SeMatrix> boneAbs)
        {
            Vec3 acc = Vec3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = weights[offset + k];
                if (w == 0f) continue;
                int b = bones[offset + k];
                acc = acc + boneAbs[b].Transform(bind[b].Transform(v)) * w;
            }
            return acc;
        }

        // ------------------------------------------------------------------------------------------------ split by material

 /// <summary>
 /// Splits a character model by submesh: <paramref name="headSubmesh"/>[s] true puts submesh s (the materials of
 /// <c>MyCharacterDefinition.MaterialsDisabledIn1st</c>) into the head model, the rest into the body. Each triangle
 /// belongs to the first submesh whose range covers it (S3's coverage rule); a part's triangles are grouped by
 /// submesh in submesh order, each part keeping only the vertices it uses (renumbered in first-use order), bounds
 /// recomputed; triangles outside every range go to the body after its submeshes (still outside every range).
 /// Submeshes that own no triangle are dropped. A part without triangles is null. Model keys are set by the caller.
 /// </summary>
        public static void SplitByMaterial(ModelGeometry src, bool[] headSubmesh, out ModelGeometry body, out ModelGeometry head)
        {
            if (src == null) throw new ArgumentNullException("src");
            int sc = src.SubmeshCount;
            if (headSubmesh == null || headSubmesh.Length != sc) throw new ArgumentException("one flag per submesh", "headSubmesh");
            int tris = src.TriangleCount;
            var owner = new int[tris];
            for (int t = 0; t < tris; t++)
            {
                owner[t] = -1;
                for (int s = 0; s < sc; s++)
                {
                    SubmeshRange r = src.Submeshes[s];
                    if ((uint)t >= r.TriStart && (ulong)t < (ulong)r.TriStart + r.TriCount) { owner[t] = s; break; }
                }
            }
            body = BuildPart(src, owner, headSubmesh, false);
            head = BuildPart(src, owner, headSubmesh, true);
        }

        private static ModelGeometry BuildPart(ModelGeometry src, int[] owner, bool[] headSubmesh, bool wantHead)
        {
            int sc = src.SubmeshCount, tris = owner.Length;
            var triList = new List<int>(tris);
            var ranges = new List<SubmeshRange>(sc);
            for (int s = 0; s < sc; s++)
            {
                if (headSubmesh[s] != wantHead) continue;
                int start = triList.Count;
                for (int t = 0; t < tris; t++) if (owner[t] == s) triList.Add(t);
                int count = triList.Count - start;
                if (count == 0) continue;
                SubmeshRange r = src.Submeshes[s];
                ranges.Add(new SubmeshRange(r.MaterialKey, (uint)start, (uint)count, r.DrawTechnique));
            }
            if (!wantHead) for (int t = 0; t < tris; t++) if (owner[t] < 0) triList.Add(t);
            if (triList.Count == 0) return null;

            var remap = new int[src.VertexCount];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var order = new List<int>();
            var indices = new uint[3 * triList.Count];
            for (int k = 0; k < triList.Count; k++)
            {
                int t = triList[k];
                for (int e = 0; e < 3; e++)
                {
                    int v = (int)src.Indices[3 * t + e];
                    if (remap[v] < 0) { remap[v] = order.Count; order.Add(v); }
                    indices[3 * k + e] = (uint)remap[v];
                }
            }
            ModelGeometry g = ModelGeometry.Allocate(order.Count, indices.Length, ranges.Count, src.Flags);
            g.ModelKey = src.ModelKey;
            g.Lod = src.Lod;
            g.PatternScale = src.PatternScale;
            for (int nv = 0; nv < order.Count; nv++)
            {
                int ov = order[nv];
                Array.Copy(src.Positions, 3 * ov, g.Positions, 3 * nv, 3);
                Array.Copy(src.Normals, 3 * ov, g.Normals, 3 * nv, 3);
                Array.Copy(src.Tangents, 3 * ov, g.Tangents, 3 * nv, 3);
                Array.Copy(src.Binormals, 3 * ov, g.Binormals, 3 * nv, 3);
                Array.Copy(src.Uvs, 2 * ov, g.Uvs, 2 * nv, 2);
                if (g.Skinned)
                {
                    Array.Copy(src.BoneIndices, 4 * ov, g.BoneIndices, 4 * nv, 4);
                    Array.Copy(src.BoneWeights, 4 * ov, g.BoneWeights, 4 * nv, 4);
                }
            }
            Array.Copy(indices, g.Indices, indices.Length);
            for (int s = 0; s < ranges.Count; s++) g.Submeshes[s] = ranges[s];
            g.ComputeBounds();
            return g;
        }

        /// <summary>A one-line description of a skeleton map for <c>DE.S5 char</c> logs.</summary>
        public static string Describe(SkeletonMap map)
        {
            if (map == null) return "map=none";
            return "bones=" + map.ModelBoneCount.ToString(CultureInfo.InvariantCulture) + " canonical=" + MirrorSkeleton.BoneCount.ToString(CultureInfo.InvariantCulture)
                + " nonCanonical=" + map.NonCanonicalBones.ToString(CultureInfo.InvariantCulture);
        }
    }
}
