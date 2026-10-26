using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The Unity-free half of the S7 skinned astronaut on the Subnautica side (CONTRACT §5.4 "S7 conventions",
 /// <c>format bones.order</c>, <c>bones.unity</c>; ROADMAP S7.N6, S7.M4), compiled into tests/DeepEngineers.SN.Tests:
 /// the canonical slice of an ExtraBones SkinnedBind (the S5 skin builds on rows 0..77), the flat pose of rows 78..
 /// (Wire <see cref="S7Bones.UnityLocal"/>, interpolated like the SeBones locals), S7.M4's component-wise SeBones locals
 /// (Wire <see cref="S7Bones.ComponentWiseLocal"/> on each bone's SE relative matrix), the count agreement of the three
 /// bone records and the tearing summary of a built part.
 /// </summary>
    internal static class SkinnedAstronautRules
    {
        /// <summary>The <c>s7.bones</c> rows a <c>de.bones {action: rows}</c> asks for at most.</summary>
        public const int MaxRowFrames = 600;

        /// <summary>
 /// The first <paramref name="canonical"/> rows of a SkinnedBind (12 floats each): the array itself when it has
 /// exactly that many, a copy of the slice when it has more (ExtraBones), null when it has fewer.
 /// </summary>
        public static float[] CanonBind(float[] bind, int canonical)
        {
            if (bind == null || canonical <= 0) return null;
            int want = 12 * canonical;
            if (bind.Length == want) return bind;
            if (bind.Length < want || bind.Length % 12 != 0) return null;
            var slice = new float[want];
            Array.Copy(bind, slice, want);
            return slice;
        }

        /// <summary>The extra rows of a SkinnedBind beyond the canonical ones (null when none).</summary>
        public static float[] ExtraBind(float[] bind, int canonical)
        {
            int want = 12 * canonical;
            if (bind == null || bind.Length <= want || bind.Length % 12 != 0) return null;
            var extra = new float[bind.Length - want];
            Array.Copy(bind, want, extra, 0, extra.Length);
            return extra;
        }

        /// <summary>One bone posed flat under the Entity (<c>format bones.unity</c>), or S7.M4's component-wise mirror.</summary>
        public static Trs FlatLocal(float[] absRows, int off, bool componentWise)
        {
            Vec3 p, s;
            Quat q;
            if (componentWise) S7Bones.ComponentWiseLocal(absRows, off, out p, out q, out s);
            else S7Bones.UnityLocal(absRows, off, out p, out q, out s);
            return new Trs { P = p, Q = q, S = s };
        }

        /// <summary>The flat pose from a SkinnedBind row (the inverse absolute bind): the rest pose of an extra bone.</summary>
        public static Trs RestFromBind(float[] bindRows, int off)
        {
            Aff abs = Aff.FromSeRows(bindRows, off).Inverse();
            var tmp = new float[12];
            abs.ToSeRows(tmp, 0);
            return FlatLocal(tmp, 0, false);
        }

        /// <summary>
 /// The flat extra poses of one camera sample: rows 78.. of the newer C1 frame, blended with the older one by
 /// <paramref name="alpha"/> when both carry the same extra rows (<see cref="Trs.Blend"/>, as the SeBones locals);
 /// false when the newer frame has none.
 /// </summary>
        public static bool ExtraLocals(float[] newer, int newerCount, float[] older, int olderCount, double alpha, bool componentWise, Trs[] dst)
        {
            if (newer == null || newerCount <= 0 || dst == null) return false;
            int n = Math.Min(newerCount, dst.Length);
            bool blend = older != null && olderCount == newerCount && alpha < 1.0;
            for (int k = 0; k < n; k++)
            {
                Trs b = FlatLocal(newer, 12 * k, componentWise);
                dst[k] = blend ? Trs.Blend(FlatLocal(older, 12 * k, componentWise), b, Math.Max(0.0, alpha)) : b;
            }
            return true;
        }

        /// <summary>
 /// S7.M4 (seeded failure): the SeBones locals with each bone's SE relative matrix (abs_i × abs_parent⁻¹) mirrored
 /// component-wise instead of conjugated; the S5 path's <see cref="CharacterMath.SeBoneLocals"/> otherwise.
 /// </summary>
        public static void ComponentWiseLocals(float[] absRows, int[] parent, Trs[] dst)
        {
            var tmp = new float[12];
            for (int i = 0; i < parent.Length; i++)
            {
                Aff a = Aff.FromSeRows(absRows, i * 12);
                Aff rel = parent[i] < 0 ? a : Aff.FromSeRows(absRows, parent[i] * 12).Inverse() * a;
                rel.ToSeRows(tmp, 0);
                dst[i] = FlatLocal(tmp, 0, true);
            }
        }

        /// <summary>
 /// S7.N6.count on one sample: CharacterAdd <c>boneCount</c>, the SkinnedBind rows and the C1 <c>boneCount</c> equal
 /// (and, with ExtraBones, above the canonical 78).
 /// </summary>
        public static bool CountsAgree(uint addBoneCount, int bindRows, uint c1BoneCount, bool extraBones)
        {
            if (addBoneCount != c1BoneCount || bindRows != (int)c1BoneCount) return false;
            return extraBones ? c1BoneCount > S7Bones.CanonicalRows && c1BoneCount <= S7Bones.MaxBones : c1BoneCount == S7Bones.CanonicalRows;
        }

        /// <summary>The tearing summary of a built part (S7.N6.tear): its vertices, bad sums, out-of-range weights, the largest index.</summary>
        public static TearInfo Tear(byte[] boneIdx, float[] weights, int vertexCount, int boneCount)
        {
            var t = new TearInfo { Vertices = vertexCount, BoneCount = boneCount };
            if (boneIdx == null || weights == null || vertexCount <= 0) { t.Valid = false; return t; }
            int badSum, badIndex, maxIndex;
            S7Bones.CheckWeights(boneIdx, weights, vertexCount, boneCount, S7Bones.WeightSumTolerance, out badSum, out badIndex, out maxIndex);
            t.BadSum = badSum;
            t.BadIndex = badIndex;
            t.MaxIndex = maxIndex;
            t.Valid = true;
            return t;
        }
    }

    /// <summary>The weights of one built character part (S7.N6.tear).</summary>
    internal struct TearInfo
    {
        public bool Valid;
        public int Vertices, BoneCount, BadSum, BadIndex, MaxIndex;
        public bool Ok { get { return Valid && BadSum == 0 && BadIndex == 0; } }
    }

    /// <summary>The S7 rows of the skinned astronaut that the bridge writes per request (<c>de.bones</c>), cross-instance (v3: every mirror is keyed by its character id).</summary>
    internal static class SkinnedAstronautRequests
    {
        private static int _frames;
        private static readonly object Lock = new object();

        /// <summary><c>de.bones {action: rows, frames}</c>: one <c>s7.bones</c> row per counted camera sample for that many frames.</summary>
        public static int Request(int frames)
        {
            lock (Lock) { _frames = Math.Max(0, Math.Min(SkinnedAstronautRules.MaxRowFrames, frames)); return _frames; }
        }

        public static int Left { get { lock (Lock) return _frames; } }

        /// <summary>Takes one row of the request (one per counted frame, shared by every character mirror of that frame).</summary>
        public static bool Take(int frame, ref int lastFrame)
        {
            lock (Lock)
            {
                if (_frames <= 0) return false;
                if (frame != _takenFrame) { _takenFrame = frame; _frames--; }
                lastFrame = frame;
                return true;
            }
        }

        private static int _takenFrame = int.MinValue;
    }
}
