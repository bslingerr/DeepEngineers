using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 skinned astronaut, Subnautica side (CONTRACT §5.4 "S7 conventions", "Character bones (S7; S7Bones)"; ROADMAP
 /// S7.N6, S7.M4). S5 already draws SE's own astronaut mesh skinned on the canonical bone tree
 /// (<see cref="CharacterMirror"/>: SeBones from C1 by default, path G Retarget while swimming and as the fallback, S5a's
 /// diver arms as the first-person diver; all of that stays). With <c>ExtraBones</c> on the CharacterAdd, the records
 /// carry every model bone (81): this class, one per character mirror (keyed by its character id, v3), adds
 /// <list type="bullet">
 /// <item>the canonical slice of the 81-row SkinnedBind for the S5 skin build (<see cref="SkinnedAstronautRules.CanonBind"/>;
 /// no vertex weights a bone past 77) and the extra rows' rest pose;</item>
 /// <item>rows 78.. posed flat under the character's Entity every camera sample from C1's absolute matrices
 /// (<c>format bones.unity</c>, Wire <see cref="S7Bones.UnityLocal"/>), blended with the camera's alpha like the
 /// SeBones locals; Transforms <c>DE.S7.Bone.&lt;row&gt;</c> (SE's attachment points: they carry no skin);</item>
 /// <item>S7.M4: the SeBones locals and the extra rows mirrored component-wise (Wire
 /// <see cref="S7Bones.ComponentWiseLocal"/>) instead of conjugated;</item>
 /// <item>rows <c>s7.bones</c> {snFrame, presentedSeTick, seTick, alpha, characterId, model, boneCount (C1),
 /// addBoneCount (CharacterAdd), bindBones (SkinnedBind rows), extraBones, extraPosed, source, countsAgree, world
 /// [[x,y,z]…] (Unity world positions of rows 0..boneCount−1: the canonical Transforms, then the extra ones), tear
 /// [{part, verts, badSum, badIndex, maxIndex}]} once per counted frame while a <c>de.bones {action: rows, frames}</c>
 /// request lasts, and the C11 handler <see cref="Handle"/> (<c>de.bones {action: status | rows, frames}</c>; routed by
 /// the S7 host).</item>
 /// </list>

 /// </summary>
    internal sealed class SkinnedAstronaut
    {
        public const string Cmd = "de.bones";
        private const int MaxExtra = S7Bones.MaxBones - S7Bones.CanonicalRows;

        /// <summary>Every live instance (one per character mirror), weakly held: the C11 status lists them.</summary>
        private static readonly List<WeakReference> Live = new List<WeakReference>();

        private Transform _entityT;
        private ulong _id;
        private readonly Transform[] _extra = new Transform[MaxExtra];
        private int _extraMade;
        private readonly Trs[] _extraLocals = new Trs[MaxExtra];
        private float[] _extraBind;
        private int _bindRows;
        private ulong _bindModel;
        private readonly Dictionary<ulong, TearInfo> _tear = new Dictionary<ulong, TearInfo>();
        private int _rowFrame = int.MinValue;
        private bool _loggedExtra, _loggedMismatch;

        public long ExtraFrames, NoExtraFrames, CountMismatchFrames, CountAgreeFrames, Rows, M4Frames;
        public uint LastC1Count, LastAddCount;
        public int ExtraPosed { get; private set; }

        public SkinnedAstronaut()
        {
            lock (Live)
            {
                Live.RemoveAll(w => !w.IsAlive);
                Live.Add(new WeakReference(this));
            }
        }

        // ------------------------------------------------------------------------------------------ hooks from CharacterMirror

 /// <summary>
 /// CharacterMirror.PostBuild (main thread): the bind the S5 skin is built from (its 78 canonical rows; an ExtraBones
 /// SkinnedBind is sliced), and the extra rows kept for the rest pose. Null when the bind has fewer than 78 rows.
 /// </summary>
        public float[] OnBind(ulong modelKey, float[] bind)
        {
            if (_off) return SkinnedAstronautRules.CanonBind(bind, S7Bones.CanonicalRows) ?? bind;
            if (bind != null && bind.Length >= 12 * S7Bones.CanonicalRows && bind.Length % 12 == 0)
            {
                _bindRows = bind.Length / 12;
                _bindModel = modelKey;
                _extraBind = SkinnedAstronautRules.ExtraBind(bind, S7Bones.CanonicalRows);
            }
            return SkinnedAstronautRules.CanonBind(bind, S7Bones.CanonicalRows) ?? bind;
        }

        /// <summary>CharacterMirror.EnsureRig: the Entity the extra bones hang under and the character they belong to.</summary>
        public void OnRig(Transform entity, ulong characterId)
        {
            _off = false;   // a new rig retries
            _entityT = entity;
            _id = characterId;
            _extraMade = 0;
            Array.Clear(_extra, 0, _extra.Length);
        }

        /// <summary>CharacterMirror.TearDown: the extra Transforms went with the rig.</summary>
        public void OnTearDown()
        {
            _entityT = null;
            _extraMade = 0;
            Array.Clear(_extra, 0, _extra.Length);
            _tear.Clear();
            ExtraPosed = 0;
        }

        /// <summary>
 /// CharacterMirror.Pose right after the C1 sample (S7.M4 only): the SeBones locals recomputed from the newer frame
 /// with the component-wise mirror (no alpha blend: the seeded failure need not be smooth).
 /// </summary>
        public void ComponentWise(CharFrame newer, int[] parents, Trs[] seLocals)
        {
            if (_off || newer == null || !newer.BonesValid || parents == null || seLocals == null) return;
            try
            {
                SkinnedAstronautRules.ComponentWiseLocals(newer.Bones, parents, seLocals);
                M4Frames++;
            }
            catch (Exception e) { TurnOff("ComponentWise", e); }
        }

        /// <summary>
 /// CharacterMirror.Pose after the bones, the Entity and the first-person offset are set: the extra rows posed flat,
 /// the count agreement counted, and the requested <c>s7.bones</c> row on the counted sample.
 /// </summary>
        public void AfterPose(CharacterRecord rec, CharFrame newer, CharFrame older, double alpha, CharSource src, CamBracket br,
                              Transform[] bones, bool countFrame, IEnumerable<CharPartView> parts)
        {
            if (_off || newer == null) return;
            try { AfterPoseCore(rec, newer, older, alpha, src, br, bones, countFrame, parts); }
            catch (Exception e) { TurnOff("AfterPose", e); }
        }

        /// <summary>
 /// A failure here turns the S7 extras off (logged once) and never the S5 character: CharacterMirror's own guard
 /// would otherwise disable the whole pose step.
 /// </summary>
        private void TurnOff(string where, Exception e)
        {
            _off = true;
            DeLog.Error("DE.S7 FEATURE bones." + where + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the S5 character keeps running) " + e);
        }

        private bool _off;

        private void AfterPoseCore(CharacterRecord rec, CharFrame newer, CharFrame older, double alpha, CharSource src, CamBracket br,
                                   Transform[] bones, bool countFrame, IEnumerable<CharPartView> parts)
        {
            bool extraFlag = rec != null && S7Bones.HasExtraBones(rec.Flags);
            int n = newer.BonesValid ? newer.ExtraCount : 0;
            ExtraPosed = 0;
            if (extraFlag && n > 0 && _entityT != null)
            {
                bool cw = S7Mutants.M4;
                CharFrame o = older != null && older.BonesValid && older.BonesVersion == newer.BonesVersion ? older : null;
                if (SkinnedAstronautRules.ExtraLocals(newer.Extra, n, o != null ? o.Extra : null, o != null ? o.ExtraCount : 0, alpha, cw, _extraLocals))
                {
                    EnsureExtra(n);
                    for (int k = 0; k < n && k < _extraMade; k++)
                    {
                        Trs t = _extraLocals[k];
                        if (!t.Finite) continue;
                        Transform x = _extra[k];
                        x.localPosition = new Vector3((float)t.P.X, (float)t.P.Y, (float)t.P.Z);
                        x.localRotation = new Quaternion((float)t.Q.X, (float)t.Q.Y, (float)t.Q.Z, (float)t.Q.W);
                        x.localScale = new Vector3((float)t.S.X, (float)t.S.Y, (float)t.S.Z);
                        ExtraPosed++;
                    }
                }
            }
            if (countFrame)
            {
                uint add = rec != null ? rec.BoneCount : 0u;
                LastC1Count = newer.BoneCount;
                LastAddCount = add;
                if (ExtraPosed > 0) ExtraFrames++; else NoExtraFrames++;
                if (SkinnedAstronautRules.CountsAgree(add, _bindRows, newer.BoneCount, extraFlag)) CountAgreeFrames++;
                else
                {
                    CountMismatchFrames++;
                    if (!_loggedMismatch)
                    {
                        _loggedMismatch = true;
                        DeLog.Warn("DE.S7 bones count mismatch for character " + DeLog.Hex(_id) + ": CharacterAdd " + add + ", SkinnedBind " + _bindRows +
                                   ", C1 " + newer.BoneCount + (extraFlag ? " (ExtraBones)" : " (canon1)") + " (logged once)");
                    }
                }
                if (ExtraPosed > 0 && !_loggedExtra)
                {
                    _loggedExtra = true;
                    DeLog.Info("DE.S7 bones character " + DeLog.Hex(_id) + ": " + newer.BoneCount + " rows, " + ExtraPosed + " extra bones posed flat under the Entity" +
                               " (format bones.unity); CharacterAdd " + add + ", SkinnedBind " + _bindRows + (S7Mutants.M4 ? " MUTANT S7.M4 component-wise" : ""));
                }
                int frame = Time.frameCount;
                if (frame != _rowFrame && SkinnedAstronautRequests.Take(frame, ref _rowFrame))
                    Row(rec, newer, alpha, src, br, bones, parts);
            }
        }

        private void EnsureExtra(int n)
        {
            if (_entityT == null) return;
            while (_extraMade < n && _extraMade < MaxExtra)
            {
                var go = new GameObject("DE.S7.Bone." + (S7Bones.CanonicalRows + _extraMade).ToString(CultureInfo.InvariantCulture));
                go.layer = 0;
                go.transform.SetParent(_entityT, false);
                Trs rest = _extraBind != null && 12 * (_extraMade + 1) <= _extraBind.Length
                    ? SkinnedAstronautRules.RestFromBind(_extraBind, 12 * _extraMade) : Trs.Identity;
                if (rest.Finite)
                {
                    go.transform.localPosition = new Vector3((float)rest.P.X, (float)rest.P.Y, (float)rest.P.Z);
                    go.transform.localRotation = new Quaternion((float)rest.Q.X, (float)rest.Q.Y, (float)rest.Q.Z, (float)rest.Q.W);
                }
                _extra[_extraMade++] = go.transform;
            }
        }

        // ------------------------------------------------------------------------------------------ rows

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Row(CharacterRecord rec, CharFrame newer, double alpha, CharSource src, CamBracket br, Transform[] bones, IEnumerable<CharPartView> parts)
        {
            bool extraFlag = rec != null && S7Bones.HasExtraBones(rec.Flags);
            int c1 = (int)newer.BoneCount;
            JsonOut j = S5Rows.Begin("s7.bones");
            j.Num("snFrame", (long)Time.frameCount).Num("presentedSeTick", br.Valid ? br.Newer : 0UL).Num("seTick", newer.Tick).Num("alpha", alpha)
             .Num("characterId", _id).Str("characterIdStr", _id.ToString(CultureInfo.InvariantCulture))
             .Str("model", rec != null ? S5Rows.Hex(rec.ModelKey) : null).Num("boneCount", (long)newer.BoneCount)
             .Num("addBoneCount", rec != null ? (long)rec.BoneCount : -1L).Num("bindBones", (long)_bindRows).Str("bindModel", S5Rows.Hex(_bindModel))
             .Bool("extraBones", extraFlag).Num("extraPosed", (long)ExtraPosed).Str("source", CharSourceRules.Name(src))
             .Bool("bonesValid", newer.BonesValid).Bool("countsAgree", SkinnedAstronautRules.CountsAgree(rec != null ? rec.BoneCount : 0u, _bindRows, newer.BoneCount, extraFlag))
             .Bool("mutantS7M4", S7Mutants.M4);
            j.Arr("world");
            if (newer.BonesValid && bones != null)
            {
                int canon = Math.Min(bones.Length, Math.Min(c1, S7Bones.CanonicalRows));
                for (int i = 0; i < canon; i++)
                {
                    Vector3 p = bones[i] != null ? bones[i].position : new Vector3(float.NaN, float.NaN, float.NaN);
                    j.Vec(null, p.x, p.y, p.z);
                }
                for (int k = 0; k < c1 - S7Bones.CanonicalRows; k++)
                {
                    Vector3 p = k < _extraMade && k < ExtraPosed && _extra[k] != null ? _extra[k].position : new Vector3(float.NaN, float.NaN, float.NaN);
                    j.Vec(null, p.x, p.y, p.z);
                }
            }
            j.EndArr();
            j.Arr("tear");
            if (parts != null)
            {
                foreach (CharPartView v in parts)
                {
                    if (v == null || v.Data == null) continue;
                    TearInfo t;
                    if (!_tear.TryGetValue(v.ModelKey, out t))
                    {
                        t = SkinnedAstronautRules.Tear(v.Data.BoneIdx, v.Data.Weights, v.Data.VertexCount, bones != null ? bones.Length : S7Bones.CanonicalRows);
                        _tear[v.ModelKey] = t;
                    }
                    j.Obj(null).Num("part", v.PartKey).Str("model", S5Rows.Hex(v.ModelKey)).Bool("valid", t.Valid).Num("verts", (long)t.Vertices)
                     .Num("badSum", (long)t.BadSum).Num("badIndex", (long)t.BadIndex).Num("maxIndex", (long)t.MaxIndex).Num("boneCount", (long)t.BoneCount).EndObj();
                }
            }
            j.EndArr();
            S5Rows.End(j);
            Rows++;
        }

        public string Describe()
        {
            return "character " + DeLog.Hex(_id) + " C1 " + LastC1Count + " CharacterAdd " + LastAddCount + " SkinnedBind " + _bindRows + " extraPosed " + ExtraPosed +
                   " frames extra/none " + ExtraFrames + "/" + NoExtraFrames + " counts agree/mismatch " + CountAgreeFrames + "/" + CountMismatchFrames +
                   " rows " + Rows + (S7Mutants.M4 ? " MUTANT S7.M4 (" + M4Frames + " frames)" : "");
        }

        // ------------------------------------------------------------------------------------------ C11

 /// <summary><c>de.bones {action: status | rows, frames}</c> (Subnautica half of S7.N6): rows = one <c>s7.bones</c> row per counted frame for <c>frames</c> frames (default 60).</summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            string action = S3Args.Str(a, "action", "status").Trim().ToLowerInvariant();
            reply.Str("cmd", req.Cmd);
            if (action == "rows")
            {
                int frames = (int)Math.Round(S3Args.Num(a, "frames", 60));
                int got = SkinnedAstronautRequests.Request(frames);
                reply.Bool("ok", true).Num("frames", (long)got);
            }
            else if (action == "status") reply.Bool("ok", true);
            else { reply.Bool("ok", false).Str("error", "de.bones: action status | rows (frames)"); return true; }
            var lines = new List<string>();
            lock (Live)
                foreach (WeakReference w in Live)
                {
                    var s = w.Target as SkinnedAstronaut;
                    if (s != null) lines.Add(s.Describe());
                }
            reply.Num("mirrors", (long)lines.Count).Str("line", "DE.S7 bones " + (lines.Count == 0 ? "no character mirror" : string.Join(" | ", lines.ToArray())))
                 .Num("rowFramesLeft", (long)SkinnedAstronautRequests.Left).Str("mutants", DevMutants.Describe());
            return true;
        }
    }
}
