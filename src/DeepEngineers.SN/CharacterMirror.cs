using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>One character part on the bone tree (Body or Head1st): its skinned mesh and renderer.</summary>
    internal sealed class CharPartView
    {
        public ulong PartKey, ModelKey;
        public uint Flags;
        public long ColourKey;
        public float H, S, V;
        public SkinnedMeshData Data;
        public Mesh Mesh;
        public GameObject Go;
        public SkinnedMeshRenderer Renderer;
        public SkyApplier Sky;
        public MaterialSlot[] Slots;
        public Material[] Mats;
        public bool MaterialsReady, Drawn;
        /// <summary>S7p-character (P8): how the part was drawn last frame (drawn, shadow-only in SE's first person, hidden).</summary>
        public PartShow Show;
        /// <summary>The build this view waits for (a result of an older build is dropped).</summary>
        public int Serial;
    }

    /// <summary>Worker → main: a skinned part built.</summary>
    internal sealed class SkinnedBuildResult
    {
        public int Serial;
        public ulong PartKey;
        public SkinnedMeshData Data;
    }

    /// <summary>
 /// CONTRACT §5.4 "Character"; ROADMAP S5.N7)
 /// <list type="bullet">
 /// <item>Records: the version-1 CharacterAdd and its PartAdds (class 5 Body / Head1st, optional tool) with the skinned
 /// models (76-B vertices, canonical bone indices) and their SkinnedBind. The parts are built on the mirror worker
 /// (mirror, (I0, I2, I1), canonical weights renormalised, bind poses = the mirrored SkinnedBind) and uploaded once.</item>
 /// <item>Unity: Frame (o, (s, 1, s)) → Entity (q) from C1 charO/charQ/charS at the camera bracket; under the Entity the
 /// canonical bone tree (78 Transforms named as seBone, rest locals from SkinnedBind) and one SkinnedMeshRenderer per
 /// part (bones = the tree, rootBone = row 0, updateWhenOffscreen), materials as for the grids, a SkyApplier.</item>
 /// <item>Animation source per camera sample: SeBones (C1's bones relative to their parents, mirrored, interpolated
 /// with the camera's alpha), Retarget while swimming (<see cref="CharacterRetarget"/>: the ghost Player's animator,
 /// humanoid path H or generic path G), Bind otherwise; 0.25 s per-bone blend between sources.</item>
 /// <item>Display: third person = C1's pose; first person + Retarget = the Entity moved so HeadDummy sits at the
 /// camera; HiddenInFirstPerson parts not drawn in first person (S5.N7b); fpbody=diver draws no SE body in first person
 /// (S5a's diver arms do), fpbody=se draws it and S5Host turns S5a's arms off. S7p-character (P8): fpbody=se is the
 /// default and the HiddenInFirstPerson parts are shadow-only in first person as in SE (<see cref="S7pFirstPerson"/>);
 /// (P9) SE's paint is baked into the character's materials (<see cref="S7pCharacterPaint"/>).</item>
 /// </list>
 /// Fallback: SE exports the default Engineer (CharacterAdd Fallback flag, logged); never a Subnautica diver.
 /// Every Unity call is guarded: an exception disables only the failing step (logged once).
 /// </summary>
    internal sealed unsafe class CharacterMirror
    {
        public const int HeadDummy = MirrorSkeleton.HeadDummy, Pelvis = MirrorSkeleton.Pelvis, Head = MirrorSkeleton.Head, RightPalm = MirrorSkeleton.RightPalm;
        public const double RetargetBudgetMs = 0.3;
        public const int RetargetOverBudgetFrames = 60;

        private readonly MirrorConsumer _m;
        public readonly CharFrameReader C1;
        private CanonSkeleton _sk;
        private string _skError;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);

        // record being drawn
        private ulong _id;
        private int _recordVersion = -1;
        private ulong _bodyModel;
        private bool _fallbackLogged;
        private readonly Dictionary<ulong, CharPartView> _parts = new Dictionary<ulong, CharPartView>();
        private readonly Dictionary<ulong, ChildPartView> _children = new Dictionary<ulong, ChildPartView>();
        private int _selfShadowGen; //: the character layer generation applied to the parts (S7pSelfShadow)
        private int _serial;
        private int _pendingBuilds;

        // Unity objects
        private GameObject _frame;
        private Transform _frameT, _entityT, _rigT;
        private Transform[] _bones;
        private int _createdFrame;

        // pose buffers (allocated once per rig)
        private Trs[] _rest, _target, _final, _seLocals, _retLocals;
        private SourceBlend _blend;
        public readonly CharacterRetarget Retarget = new CharacterRetarget();
        /// <summary>S7 (SkinnedAstronaut.cs): ExtraBones rows 78.. posed flat, the canonical slice of the SkinnedBind, S7.M4, s7.bones rows.</summary>
        public readonly SkinnedAstronaut Skinned = new SkinnedAstronaut();
        private float _lastPoseTime;
        private int _overBudget;
        private bool _retargetOff;
        private readonly Stopwatch _sw = new Stopwatch();

        /// </summary>
        public FpBody Fp = S7pFirstPerson.FpBodyDefault;
        /// <summary>S7p-character (P9): SE's own paint baked into the character's materials (<see cref="S7pCharacterPaint"/>).</summary>
        public readonly S7pCharacterPaint Paint;
        public bool FirstPersonBodyDrawn { get; private set; }
        /// <summary>S7p-character (P8): the head (HiddenInFirstPerson parts) drawn shadow-only in SE's first person last frame.</summary>
        public bool HeadShadowOnly { get; private set; }
        /// <summary>S7p-character (P8): the C1 camera mode of the last pose (CameraMode.*).</summary>
        public uint LastCameraMode { get { return _lastCameraMode; } }
        /// <summary>S5Host: called when <see cref="FirstPersonBodyDrawn"/> changes (in the camera callback, before S5a's step).</summary>
        public Action<bool> OnFirstPersonBody;

        private void SetFirstPersonBody(bool v)
        {
            if (v == FirstPersonBodyDrawn) return;
            FirstPersonBodyDrawn = v;
            Action<bool> cb = OnFirstPersonBody;
            if (cb != null) cb(v);
        }
        public CharSource Source { get { return _blend != null ? _blend.Source : CharSource.None; } }
        public bool Built { get { return _frame != null && _bones != null; } }
        /// <summary>S7 lights (LightMirror): the character's Entity transform (null until built) and its id: light rows attached to the character hang under it.</summary>
        internal Transform EntityTransform { get { return _entityT; } }
        internal ulong CharacterId { get { return _id; } }
        /// <summary>The held tool (Child parts of the character), for the shared-mesh bookkeeping.</summary>
        public Dictionary<ulong, ChildPartView>.ValueCollection Children { get { return _children.Values; } }

        // counters (per second window and totals)
        public long Frames, FramesBind, FramesSeBones, FramesRetarget, FramesSwimming, FramesSwimRetarget, NonFinite, Builds, BuildFailures, OffsetFrames;
        private long _wFrames, _wBind, _wSe, _wRet, _wSwim, _wSwimRet, _wNonFinite;
        private ulong _nextSecondMs;
        private CamBracket _lastBr;
        private int _retFrame = -1;
        private bool _retGood;
        /// <summary>Frames posed at the camera orientation's tick.</summary>
        public long Locked;
        private double _lastAlpha;
        private CharFrame _lastNewer;
        private CharFrame _lastOlder;   // S5-2d: the older C1 frame of the last sample (c1Oo on s5.char, interpolated rows)
        private EntityPose _lastPose;
        private uint _lastCameraMode;
        private bool _swimNow;
        public string LastWhy = "";
        /// <summary>S6 (read-only for the character section): the pose and C1 frame of the last Pose of this camera sample.</summary>
        public bool LastPoseValid { get; private set; }
        public EntityPose LastPose { get { return _lastPose; } }
        public CharFrame LastFrame { get { return _lastNewer; } }

        public CharacterMirror(MirrorConsumer m, byte* sectionBase)
        {
            _m = m;
            C1 = new CharFrameReader(Layout.RegionPtr(sectionBase, RegionId.C1SeFrame));
            Paint = new S7pCharacterPaint(m); // S7p-character (P9)
        }

        private bool Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return false;
            try { a(); return true; }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S5 FEATURE char." + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the grids keep running)\n" + e);
                return false;
            }
        }

        public string DisabledList { get { return _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()); } }

        private CanonSkeleton Skeleton()
        {
            if (_sk != null || _skError != null) return _sk;
            _sk = CanonSkeleton.Embedded(out _skError);
            if (_sk == null) DeLog.Error("DE.S5 FEATURE character DISABLED: canonical skeleton canon1 unavailable (" + _skError + ")");
            else
            {
                List<string> miss = _sk.MissingRequiredHuman();
                DeLog.Info("DE.S5 skeleton canon1 bones=" + _sk.Count + " hash=" + DeLog.Hex(_sk.Hash) + " requiredHumanMissing=" + (miss.Count == 0 ? "none" : string.Join(",", miss.ToArray())) +
                           " snBoneMissing=" + _sk.MissingRequiredSnBones().Count + "/15 (path G needs all; filled from the real run's DE.S5 rig line)");
            }
            return _sk;
        }

        // ------------------------------------------------------------------------------------------ records and builds (Update)

 /// <summary>Main thread, from the consumer's Tick while the world is up.</summary>
        public void Tick(ulong nowMs)
        {
            if (Skeleton() == null) return;
            Guard("records", _stepRecord ?? (_stepRecord = SyncRecord));
            if (nowMs >= _nextSecondMs)
            {
                _nextSecondMs = nowMs + 1000;
                Guard("row", _stepRow ?? (_stepRow = CharRow));
            }
        }

        /// <summary>The record to draw: the one whose id is C1's character id (the newest C1), else the only one.</summary>
        private CharacterRecord Current()
        {
            Dictionary<ulong, CharacterRecord> all = _m.State.Characters;
            if (all.Count == 0) return null;
            CharFrame n = C1.Newest;
            CharacterRecord r;
            if (n != null && all.TryGetValue(n.CharacterId, out r)) return r;
            if (all.Count == 1) foreach (CharacterRecord only in all.Values) return only;
            return null;
        }

        private void SyncRecord()
        {
            CharacterRecord r = Current();
            if (r == null)
            {
                if (_frame != null || _parts.Count > 0) TearDown("no character record");
                _id = 0;
                _recordVersion = -1;
                return;
            }
            if (r.Id != _id || r.ModelKey != _bodyModel)
            {
                TearDown(_id == 0 ? "first character" : "character " + DeLog.Hex(_id) + " -> " + DeLog.Hex(r.Id));
                _id = r.Id;
                _bodyModel = r.ModelKey;
                _recordVersion = -1;
                _fallbackLogged = false;
            }
            if (r.Fallback && !_fallbackLogged)
            {
                _fallbackLogged = true;
                DeLog.Warn("DE.S5 char FALLBACK model in use: SE exported the default Engineer for character " + DeLog.Hex(r.Id) + " (CharacterAdd Fallback flag; SE logs the reason)");
            }
            if (r.Version == _recordVersion && _pendingBuilds == 0)
            {
                if (_frame != null) Guard("tool", _stepTool ?? (_stepTool = SyncTool));
                return;
            }
            // parts: build what is new or changed (model or bind), recolour in place
            bool waiting = false;
            foreach (PartRecord p in r.Parts.Values)
            {
                if (p.Role != PartRole.Skinned) continue;
                CharPartView v;
                if (_parts.TryGetValue(p.PartKey, out v) && v.ModelKey == p.ModelKey && v.Data != null)
                {
                    if (v.ColourKey != p.ColourKey)
                    {
                        PaintRelease(v.Slots, "colour changed"); // S7p-character (P9): the old colour's slots back on the S3 maps
                        v.ColourKey = p.ColourKey; v.H = p.H; v.S = p.S; v.V = p.V; v.MaterialsReady = false; v.Slots = null;
                    }
                    v.Flags = p.Flags;
                    continue;
                }
                ModelEntry e;
                float[] bind;
                if (_failedModels.Contains(p.ModelKey)) continue; // logged once; a new CharacterAdd (another model) tries again
                if (!_m.ModelMap.TryGetValue(p.ModelKey, out e) || e.Prepared == null || !_m.Binds.TryGetValue(p.ModelKey, out bind)) { waiting = true; continue; }
                if (v != null && v.Data == null && v.ModelKey == p.ModelKey) { waiting = true; continue; } // build in flight
                if (v != null) DestroyPart(v);
                v = new CharPartView { PartKey = p.PartKey, ModelKey = p.ModelKey, Flags = p.Flags, ColourKey = p.ColourKey, H = p.H, S = p.S, V = p.V };
                _parts[p.PartKey] = v;
                PostBuild(v, e.Prepared, bind);
                waiting = true;
            }
            List<ulong> gone = null;
            foreach (ulong k in _parts.Keys) if (!r.Parts.ContainsKey(k)) (gone = gone ?? new List<ulong>()).Add(k);
            if (gone != null) foreach (ulong k in gone) { DestroyPart(_parts[k]); _parts.Remove(k); }
            if (!waiting) _recordVersion = r.Version;
            if (_frame != null) Guard("tool", _stepTool ?? (_stepTool = SyncTool));
        }

        private readonly HashSet<ulong> _failedModels = new HashSet<ulong>();

        private void PostBuild(CharPartView v, PreparedModel model, float[] bind)
        {
            int serial = ++_serial;
            v.Serial = serial;
            bind = Skinned.OnBind(v.ModelKey, bind); // S7: an ExtraBones SkinnedBind (81 rows) builds the S5 skin on its 78 canonical rows
            int[] parents = _sk.Parents;
            ulong key = v.PartKey;
            _pendingBuilds++;
            _m.Worker.Post("skinned " + DeLog.Hex(v.ModelKey), () =>
            {
                var res = new SkinnedBuildResult { Serial = serial, PartKey = key };
                try { res.Data = SkinnedBuilder.Build(model, model.Key, bind, parents); }
                catch (Exception ex) { res.Data = new SkinnedMeshData { ModelKey = model.Key, Error = ex.GetType().Name + ": " + ex.Message }; }
                return res;
            });
        }

        /// <summary>Worker result (main thread): the part's mesh and renderer are made in an upload job.</summary>
        public void OnBuilt(SkinnedBuildResult r)
        {
            Guard("build", () => OnBuiltCore(r));
        }

        private void OnBuiltCore(SkinnedBuildResult r)
        {
            if (_pendingBuilds > 0) _pendingBuilds--;
            CharPartView v;
            if (!_parts.TryGetValue(r.PartKey, out v) || v.ModelKey != r.Data.ModelKey || v.Serial != r.Serial) return;
            if (r.Data.Error != null)
            {
                BuildFailures++;
                _failedModels.Add(r.Data.ModelKey);
                DeLog.Warn("DE.S5 char part " + r.PartKey + " model " + DeLog.Hex(r.Data.ModelKey) + " NOT BUILT: " + r.Data.Error + " (not retried for this model)");
                _parts.Remove(r.PartKey);
                return;
            }
            _m.Uploads.Enqueue(new UploadJob
            {
                Kind = UploadKind.Mesh, Name = "character part " + r.PartKey,
                Run = () =>
                {
                    CharPartView cur;
                    if (!_parts.TryGetValue(r.PartKey, out cur) || cur != v) return true;
                    Guard("build", () => Upload(v, r.Data));
                    return true;
                },
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Upload(CharPartView v, SkinnedMeshData d)
        {
            EnsureRig(d);
            int n = d.VertexCount;
            var pos = new Vector3[n];
            var nrm = new Vector3[n];
            var tan = new Vector4[n];
            var uv = new Vector2[n];
            var bw = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = new Vector3(d.Pos[i * 3], d.Pos[i * 3 + 1], d.Pos[i * 3 + 2]);
                nrm[i] = new Vector3(d.Nrm[i * 3], d.Nrm[i * 3 + 1], d.Nrm[i * 3 + 2]);
                tan[i] = new Vector4(d.Tan[i * 4], d.Tan[i * 4 + 1], d.Tan[i * 4 + 2], d.Tan[i * 4 + 3]);
                uv[i] = new Vector2(d.Uv[i * 2], d.Uv[i * 2 + 1]);
                bw[i] = Weight(d.BoneIdx, d.Weights, i);
            }
            var bp = new Matrix4x4[_bones.Length];
            for (int b = 0; b < bp.Length; b++)
            {
                int o = b * 12;
                var m = new Matrix4x4();
                m.m00 = (float)d.Bindposes[o]; m.m10 = (float)d.Bindposes[o + 1]; m.m20 = (float)d.Bindposes[o + 2];
                m.m01 = (float)d.Bindposes[o + 3]; m.m11 = (float)d.Bindposes[o + 4]; m.m21 = (float)d.Bindposes[o + 5];
                m.m02 = (float)d.Bindposes[o + 6]; m.m12 = (float)d.Bindposes[o + 7]; m.m22 = (float)d.Bindposes[o + 8];
                m.m03 = (float)d.Bindposes[o + 9]; m.m13 = (float)d.Bindposes[o + 10]; m.m23 = (float)d.Bindposes[o + 11];
                m.m33 = 1f;
                bp[b] = m;
            }
            Mesh mesh = _m.Owned.NewMesh("DE.S5 character " + v.PartKey + " " + DeLog.Hex(d.ModelKey));
            mesh.indexFormat = d.Use32 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = pos;
            mesh.normals = nrm;
            mesh.tangents = tan;
            mesh.uv = uv;
            mesh.boneWeights = bw;
            mesh.bindposes = bp;
            mesh.subMeshCount = Math.Max(1, d.Submeshes.Count);
            for (int s = 0; s < d.Submeshes.Count; s++) mesh.SetTriangles(d.Submeshes[s].Value, s);
            var c = new Vector3((d.MinX + d.MaxX) * 0.5f, (d.MinY + d.MaxY) * 0.5f, (d.MinZ + d.MaxZ) * 0.5f);
            mesh.bounds = new Bounds(c, new Vector3(d.MaxX - d.MinX, d.MaxY - d.MinY, d.MaxZ - d.MinZ));
            v.Mesh = mesh;
            v.Data = d;
            v.Go = new GameObject(v.PartKey == S5Conv.CharPartHead1st ? "Head1st" : v.PartKey == S5Conv.CharPartBody ? "Body" : "Part" + v.PartKey);
            S7pSelfShadow.Apply(v.Go); //: the character layer, excluded from its own lamps (S7pSelfShadowRules)
            v.Go.transform.SetParent(_entityT, false);
            v.Renderer = v.Go.AddComponent<SkinnedMeshRenderer>();
            v.Renderer.sharedMesh = mesh;
            v.Renderer.bones = _bones;
            v.Renderer.rootBone = _bones[0];
            v.Renderer.updateWhenOffscreen = true;
            v.Renderer.shadowCastingMode = ShadowCastingMode.On;
            v.Renderer.receiveShadows = true;
            v.Renderer.enabled = false;
            if (!_m.Mutants.M3)
            {
                v.Sky = v.Go.AddComponent<SkyApplier>();
                v.Sky.renderers = new Renderer[] { v.Renderer };
                v.Sky.dynamic = true;
            }
            Builds++;
            DeLog.Info("DE.S5 char part " + v.Go.name + " built: model " + DeLog.Hex(d.ModelKey) + " verts=" + n + " tris=" + d.TriangleCount + " submeshes=" + d.Submeshes.Count +
                       " index=" + (d.Use32 ? "UInt32" : "UInt16") + " droppedWeights=" + d.DroppedWeights + " emptyWeights=" + d.EmptyWeights + " uncovered=" + d.Uncovered +
                       " (skinned on canon1, bind poses = mirrored SkinnedBind)");
        }

        /// <summary>The 4 (index, weight) pairs sorted by weight, highest first (Unity's quality settings keep the first ones).</summary>
        private static BoneWeight Weight(byte[] idx, float[] w, int v)
        {
            int o = v * 4;
            int i0 = 0, i1 = 1, i2 = 2, i3 = 3;
            // a 4-element sort network on the weights
            Sw(w, o, ref i0, ref i1); Sw(w, o, ref i2, ref i3); Sw(w, o, ref i0, ref i2); Sw(w, o, ref i1, ref i3); Sw(w, o, ref i1, ref i2);
            return new BoneWeight
            {
                boneIndex0 = idx[o + i0], weight0 = w[o + i0], boneIndex1 = idx[o + i1], weight1 = w[o + i1],
                boneIndex2 = idx[o + i2], weight2 = w[o + i2], boneIndex3 = idx[o + i3], weight3 = w[o + i3],
            };
        }

        private static void Sw(float[] w, int o, ref int a, ref int b)
        {
            if (w[o + b] > w[o + a]) { int t = a; a = b; b = t; }
        }

        /// <summary>Frame/Entity and the canonical bone tree at the rest pose of the first built part's bind.</summary>
        private void EnsureRig(SkinnedMeshData d)
        {
            if (_frame != null) return;
            int n = _sk.Count;
            _frame = new GameObject("DE.Mirror.Character." + _id.ToString("x", CultureInfo.InvariantCulture));
            _frameT = _frame.transform;
            var entity = new GameObject("Entity");
            _entityT = entity.transform;
            _entityT.SetParent(_frameT, false);
            var rig = new GameObject("DE.S5.Rig");
            _rigT = rig.transform;
            _rigT.SetParent(_entityT, false);
            _bones = new Transform[n];
            _rest = (Trs[])d.RestLocals.Clone();
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject(_sk.Bones[i].SeBone);
                _bones[i] = go.transform;
                _bones[i].SetParent(_sk.Parents[i] < 0 ? _rigT : _bones[_sk.Parents[i]], false);
                Write(_bones[i], _rest[i]);
            }
            _target = new Trs[n];
            _final = new Trs[n];
            _seLocals = new Trs[n];
            _retLocals = new Trs[n];
            _blend = new SourceBlend(n);
            _createdFrame = Time.frameCount;
            _frame.SetActive(false); // until a C1 pose
            Skinned.OnRig(_entityT, _id); // S7: the extra bones hang under the Entity
            DeLog.Info("DE.S5 char rig built for " + DeLog.Hex(_id) + ": " + n + " bones (canon1) under " + _frame.name + " (rest locals from SkinnedBind of " + DeLog.Hex(d.ModelKey) + ")");
        }

        private static void Write(Transform t, Trs v)
        {
            t.localPosition = new Vector3((float)v.P.X, (float)v.P.Y, (float)v.P.Z);
            t.localRotation = new Quaternion((float)v.Q.X, (float)v.Q.Y, (float)v.Q.Z, (float)v.Q.W);
            t.localScale = new Vector3((float)v.S.X, (float)v.S.Y, (float)v.S.Z);
        }

        /// <summary>The held tool (class 6, key 3) as a Child of the character's Entity, posed from its C2 row.</summary>
        private void SyncTool()
        {
            CharacterRecord r;
            if (!_m.State.Characters.TryGetValue(_id, out r)) return;
            _m.S5.Reconcile(_entityT, _children, _id, r.Parts);
            foreach (ChildPartView c in _children.Values) //: the held tool on the character layer too (SE: the weapon never casts from the suit lamp)
            {
                S7pSelfShadow.Apply(c.Go);
                foreach (ChildRenderer cr in c.Renderers) S7pSelfShadow.Apply(cr.Go);
            }
        }

        // ------------------------------------------------------------------------------------------ materials (Update)

        public void UpdateMaterials()
        {
            Guard("materials", _stepMaterials ?? (_stepMaterials = UpdateMaterialsCore));
        }

        private Action _stepMaterials;

        private void UpdateMaterialsCore()
        {
            if (!_m.Marmoset.Ready) return;
            PaintStep(null, 0); // S7p-character (P9): bakes finished, uploads, timeouts
            foreach (CharPartView v in _parts.Values)
            {
                if (v.Renderer == null || v.Data == null) continue;
                if (v.Slots == null) { v.Slots = new MaterialSlot[Math.Max(1, v.Data.Submeshes.Count)]; v.Mats = new Material[v.Slots.Length]; v.MaterialsReady = false; }
                if (!v.MaterialsReady)
                {
                    bool all = true;
                    for (int s = 0; s < v.Slots.Length; s++)
                    {
                        if (v.Slots[s] == null)
                        {
                            ulong mk = s < v.Data.Submeshes.Count ? v.Data.Submeshes[s].Key : 0UL;
                            v.Slots[s] = _m.SlotFor(mk, v.ColourKey, v.H, v.S, v.V);
                        }
                        if (v.Slots[s] == null) { all = false; continue; }
                        v.Mats[s] = v.Slots[s].Mat;
                    }
                    if (all) { v.Renderer.sharedMaterials = v.Mats; v.MaterialsReady = true; }
                }
                else
                {
                    foreach (MaterialSlot s in v.Slots)
                    {
                        if (s != null && s.Set != null && s.AppliedVersion != s.Set.Version) _m.Marmoset.ApplyMaps(s);
                        if (s != null) PaintStep(s, v.Flags); // S7p-character (P9): SE's paint over the S3 tint, again after every ApplyMaps
                    }
                }
            }
            foreach (ChildPartView c in _children.Values) _m.S5.AssignMaterials(c);
        }

        private bool _paintOff;

        private void PaintRelease(MaterialSlot[] slots, string why)
        {
            if (_paintOff || slots == null) return;
            try { Paint.Release(slots, why); } catch (Exception) { }
        }

        /// <summary>S7p-character (P9): the paint bake in its own guard (a failure leaves the S3 tint, never the character).</summary>
        private void PaintStep(MaterialSlot s, uint flags)
        {
            if (_paintOff) return;
            try
            {
                if (s == null) Paint.Tick();
                else Paint.Apply(s, flags);
            }
            catch (Exception e)
            {
                _paintOff = true;
                DeLog.Error("DE.S7p FEATURE char.paint DISABLED after " + e.GetType().Name + ": " + e.Message + " (the S3 tint stays; the character keeps running)\n" + e);
            }
        }

        /// <summary>Slots were re-made: every part takes new ones.</summary>
        public void ResetSlots()
        {
            try { Paint.Clear("material slots re-made"); } catch (Exception) { } // S7p-character (P9)
            foreach (CharPartView v in _parts.Values) { v.Slots = null; v.MaterialsReady = false; if (v.Renderer != null) v.Renderer.enabled = false; }
            foreach (ChildPartView c in _children.Values) foreach (ChildRenderer r in c.Renderers) { r.Slot = null; if (r.Renderer != null) r.Renderer.enabled = false; }
        }

        // ------------------------------------------------------------------------------------------ pose (camera callbacks)

 /// <summary>
 /// The camera driver placed the camera for this sample (LateUpdateLast, then onPreCull): C1 polled, the entity and
 /// the bone tree posed once per sample with the camera's bracket; the source counted on the LateUpdateLast call.
 /// </summary>
        public void OnCameraSample(CamBracket br, bool countFrame)
        {
            LastPoseValid = false;
            Guard("c1", _stepPoll ?? (_stepPoll = PollC1));
            if (!Built || _sk == null) { SetFirstPersonBody(false); return; }
            _lastBr = br;
            _countFrame = countFrame;
            Guard("pose", _stepPose ?? (_stepPose = Pose));
        }

        private bool _countFrame;
        private int _lastPoseFrame = -1;
        private CharSource _whySource = CharSource.None;
        private bool _whyReady;
        private string _whyText;
        private Action _stepPose, _stepPoll, _stepRecord, _stepRow, _stepTool;

        private void PollC1()
        {
            C1.Poll();
        }

        public void PollOnly() { Guard("c1", _stepPoll ?? (_stepPoll = PollC1)); }

        private void Pose()
        {
            CamBracket br = _lastBr;
            CharFrame newer, older;
            double alpha;
            EntityPose pose;
            bool bonesOk;
            bool ok = CharSampler.Sample(C1, br, false, _sk.Parents, out newer, out older, out alpha, out pose, _seLocals, out bonesOk);
            bool present = ok && newer != null && newer.CharacterId == _id && newer.Has(SeFrameFlags.CharacterPresent);
            // S5 review F1: seated or first person = the character moves with the camera: pose it at the camera orientation's
 // tick plus the camera's interpolation offset (the same lock as the camera's grid), bones from that tick too
            if (present && br.Valid && br.NewestTick != 0 && (newer.Has(SeFrameFlags.CharacterSeated) || newer.CameraMode == CameraMode.FirstPerson))
            {
                CharFrame n2, o2; double a2; EntityPose p2; bool b2;
                if (CharSampler.Sample(C1, br.Locked(), false, _sk.Parents, out n2, out o2, out a2, out p2, _seLocals, out b2) && n2 != null && n2.CharacterId == _id)
                {
                    p2.Ox += br.DPx; p2.Oy += br.DPy; p2.Oz += br.DPz;
                    newer = n2; older = o2; alpha = a2; pose = p2; bonesOk = b2;
                    Locked++;
                }
            }
            // S7.M4 (seeded failure, SkinnedAstronaut): the SeBones locals mirrored component-wise instead of conjugated
            if (present && bonesOk && S7Mutants.M4) Skinned.ComponentWise(newer, _sk.Parents, _seLocals);
            if (!present)
            {
                if (_frame.activeSelf) _frame.SetActive(false);
                SetFirstPersonBody(false);
                return;
            }
            CharacterRecord rec;
            uint bonesVersion = _m.State.Characters.TryGetValue(_id, out rec) ? rec.BonesVersion : 0u;
            bool bonesValid = bonesOk && newer.BonesVersion == bonesVersion;
            var si = new CharSourceInputs
            {
                BonesValid = bonesValid, SwimValid = newer.SwimValid, SwimFlags = newer.Swim.Flags, SwimMode = newer.Swim.Mode,
                Seated = newer.Has(SeFrameFlags.CharacterSeated), Dead = newer.Has(SeFrameFlags.CharacterDead),
                RetargetReady = !_retargetOff && Retarget.EnsureReady(_rigT, _bones, _rest, _sk, _m.Owned),
                ToolHeld = S5aHost.ToolHeldNow,
                Ladder = S13Source.OnLadder(newer.LocoVersion, newer.Loco.State, newer.Loco.Flags), // S13 hook: a ladder is SE's pose
            };
            _swimNow = CharSourceRules.Swimming(ref si);
            CharSource src = CharSourceRules.Select(ref si);
            // S7a hook: Retarget also out of the water in a foot state of the C1 locomotion extension while the walk source is
 // `retarget` (CONTRACT §5.4 "C1 locomotion extension"; S7aAnimRules); the jetpack/crouch/ladder/seat poses stay SeBones
            bool footOut = S7aAnimRules.RetargetOutOfWater(newer.LocoVersion, newer.Loco.State,
                (newer.SwimValid && (newer.Swim.Flags & SwimWireFlags.InWater) != 0) || newer.Loco.Has(S7aConventions.C1LocoFlags.InWater),
                S7aSnSwitches.WalkRetarget, si.Seated, si.Dead);
            // review F11: only while S5aHost drives the animator out of the water (else the ghost Player's own pose would be retargeted)
            if (footOut && si.RetargetReady && S7aSnState.AnimDriving && S7aSnState.AnimOut) src = CharSource.Retarget;
            if (footOut) S7aSnState.CharFootFrames++;
            if (src == CharSource.Retarget || src == CharSource.Blend) // S13 hook (point 6): Blend evaluates the retarget for the legs
            {
                bool good;
                if (_retFrame == Time.frameCount) good = _retGood;   // review F13: one evaluation per frame (LateUpdateLast + onPreCull)
                else
                {
                _sw.Reset();
                _sw.Start();
                try { good = Retarget.Evaluate(_bones, _rest, _retLocals); }
                catch (Exception e)
                {
                    // a failing retarget turns off only the retarget (SeBones from now on), never the character
                    good = false;
                    TurnRetargetOff("Evaluate " + e.GetType().Name + ": " + e.Message);
                }
                _sw.Stop();
                if (_sw.Elapsed.TotalMilliseconds > RetargetBudgetMs) { if (++_overBudget >= RetargetOverBudgetFrames) TurnRetargetOff("over its " + RetargetBudgetMs + " ms budget for " + RetargetOverBudgetFrames + " frames"); }
                else _overBudget = 0;
                _retFrame = Time.frameCount; _retGood = good;
                }
                if (good && src == CharSource.Blend) S13Source.ComposeBlend(S13Sn.BlendMaskFor(_sk), _retLocals, _seLocals, _target); // S13 hook (point 6)
                else if (good) Array.Copy(_retLocals, _target, _target.Length);
                else src = bonesValid ? CharSource.SeBones : CharSource.Bind;
            }
            S13Sn.NoteSource(ref si, newer.SwimValid, newer.Swim.Flags, newer.Swim.Mode, src, _countFrame); // S13 hook (points 2, 6): the A2/A6 counters
            if (footOut && src == CharSource.Retarget) S7aSnState.CharFootRetarget++;
            S7aSnState.CharSource = CharSourceRules.Name(src);
            if (src == CharSource.SeBones) Array.Copy(_seLocals, _target, _target.Length);
            else if (src == CharSource.Bind) Array.Copy(_rest, _target, _target.Length);
            // S5-2a (N7a 80 cm): Pose runs twice per frame (LateUpdateLast, then onPreCull); the first call used to stamp
 // _lastPoseTime so the counted second call always saw dt 0 and the blend's clock never advanced: weight stayed 0
 // and the rig kept the pose of the last source switch for the whole session. The blend now advances once per
 // Unity frame, on the first call of the frame, by the wall-clock delta between frames.
            float now = Time.unscaledTime;
            double dt = 0;
            int frameNow = Time.frameCount;
            if (_lastPoseFrame != frameNow)
            {
                dt = _lastPoseTime > 0 ? now - _lastPoseTime : 0;
                _lastPoseTime = now;
                _lastPoseFrame = frameNow;
            }
            _blend.Step(src, dt, _target, _final);
            int bad = 0;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (!_final[i].Finite) { bad++; _final[i] = _rest[i]; }
                Write(_bones[i], _final[i]);
            }
            // the entity: Frame (o, (s, 1, s)), Entity (q) as every mirrored entity (CONTRACT §2.5)
            float s = (float)pose.S;
            _frameT.localPosition = new Vector3((float)pose.Ox, (float)pose.Oy, (float)pose.Oz);
            _frameT.localRotation = Quaternion.identity;
            _frameT.localScale = new Vector3(s, 1f, s);
            _entityT.localPosition = Vector3.zero;
            _entityT.localRotation = new Quaternion((float)pose.Qx, (float)pose.Qy, (float)pose.Qz, (float)pose.Qw);
            _entityT.localScale = Vector3.one;
            if (!_frame.activeSelf) _frame.SetActive(true);
            uint camMode = newer.CameraMode;
            bool first = camMode == CameraMode.FirstPerson;
            // first person + Retarget: HeadDummy at the camera (S5a's view-model rule); SeBones: SE's own head is the camera
            if (first && src == CharSource.Retarget && Fp == FpBody.Se)
            {
                Camera cam = MainCamera.camera;
                if (cam != null)
                {
                    Vector3 off = cam.transform.position - _bones[HeadDummy].position;
                    _frameT.position += off;
                    if (_countFrame) OffsetFrames++;
                }
            }
            // S7: rows 78.. (ExtraBones) posed flat under the Entity, the count agreement, the requested s7.bones row
            Skinned.AfterPose(rec, newer, older, alpha, src, br, _bones, _countFrame, _parts.Values);
            //: the character layer once the camera exists, the camera's mask keeps its bit (the character had vanished)
            S7pSelfShadow.KeepCamera();
            S7pSelfShadow.KeepLights(); // 23:0x: Subnautica's lights (the sun) keep lighting and shadowing the character layer
            if (_selfShadowGen != S7pSelfShadow.Generation)
            {
                _selfShadowGen = S7pSelfShadow.Generation;
                foreach (CharPartView p in _parts.Values) S7pSelfShadow.Apply(p.Go);
                foreach (ChildPartView c in _children.Values) { S7pSelfShadow.Apply(c.Go); foreach (ChildRenderer cr in c.Renderers) S7pSelfShadow.Apply(cr.Go); }
            }
            bool anyBodyDrawn = false, headShadow = false;
            foreach (CharPartView v in _parts.Values)
            {
                if (v.Renderer == null) continue;
                // S7p-character (P8): SE's first person: the body drawn and casting, the HiddenInFirstPerson head shadow-only
 // (VERIFIED SE: EnableHead(false) = SkipInMainView | SkipInForward, its shadow views still draw it)
                PartShow show = v.MaterialsReady ? S7pFirstPerson.Show(v.Flags, camMode, Fp) : PartShow.Hidden;
                bool on = show != PartShow.Hidden;
                if (v.Renderer.enabled != on) v.Renderer.enabled = on;
                if (on)
                {
                    ShadowCastingMode scm = show == PartShow.ShadowOnly ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
                    if (v.Renderer.shadowCastingMode != scm) v.Renderer.shadowCastingMode = scm;
                }
                v.Show = show;
                v.Drawn = show == PartShow.Drawn;
                if (show == PartShow.ShadowOnly) headShadow = true;
                if (v.Drawn && v.PartKey == S5Conv.CharPartBody) anyBodyDrawn = true;
            }
            HeadShadowOnly = headShadow;
            foreach (ChildPartView c in _children.Values) { PoseTool(c, br); S13Sn.GateToolChild(c, _countFrame); } // S13 hook (point 3): a held tool is hidden until its first C2 row (no frame at the feet on equip)
            SetFirstPersonBody(first && Fp == FpBody.Se && anyBodyDrawn);
            _lastAlpha = alpha;
            _lastNewer = newer;
            _lastOlder = older;
            _lastPose = pose;
            _lastCameraMode = camMode;
            LastPoseValid = true;
            if (_countFrame)
            {
                Frames++; _wFrames++;
                if (bad > 0) { NonFinite++; _wNonFinite++; }
                switch (src)
                {
                    case CharSource.Bind: FramesBind++; _wBind++; break;
                    case CharSource.SeBones: FramesSeBones++; _wSe++; break;
                    case CharSource.Retarget: FramesRetarget++; _wRet++; break;
                }
                if (_swimNow) { FramesSwimming++; _wSwim++; if (src == CharSource.Retarget) { FramesSwimRetarget++; _wSwimRet++; } }
                if (src != _whySource || si.RetargetReady != _whyReady || !ReferenceEquals(Retarget.Why, _whyText))
                {
                    // rebuilt only when it changes (no allocation per frame)
                    _whySource = src;
                    _whyReady = si.RetargetReady;
                    _whyText = Retarget.Why;
                    LastWhy = CharSourceRules.Name(src) + (si.RetargetReady ? "" : " (retarget " + Retarget.Why + ")");
                }
            }
        }

        /// <summary>The blend clock advances on the LateUpdateLast sample only (the onPreCull re-sample of the same frame adds no time).</summary>

        private void PoseTool(ChildPartView c, CamBracket br)
        {
            if (c.T == null) return;
            Aff se;
            ulong tick;
            double alpha;
            PoseHow how;
            if (!_m.S5.SampleRow(_id, c.PartKey, br, false, out se, out tick, out alpha, out how)) return;
            Trs t = MirrorXform.UnityLocal(se);
            if (!t.Finite) return;
            c.T.localPosition = new Vector3((float)t.P.X, (float)t.P.Y, (float)t.P.Z);
            c.T.localRotation = new Quaternion((float)t.Q.X, (float)t.Q.Y, (float)t.Q.Z, (float)t.Q.W);
            c.T.localScale = new Vector3((float)t.S.X, (float)t.S.Y, (float)t.S.Z);
            c.RowSeen = true;
        }

        private void TurnRetargetOff(string why)
        {
            _retargetOff = true;
            DeLog.Warn("DE.S5 char Retarget OFF (" + why + "): SeBones from now on (CONTRACT §5.4 budget 0.3 ms per frame, HYPOTHESIS)");
        }

        /// <summary>de.s5 retarget on|off (dev): switches the swim retarget source (off = SeBones while swimming).</summary>
        public void SetRetarget(bool on)
        {
            _retargetOff = !on;
            _overBudget = 0;
        }

        // ------------------------------------------------------------------------------------------ rows

 /// <summary>s5.char per second (S5.N7a/b/c): source coverage, swimming frames on Retarget, hidden parts, the pose and three bones for the recomputation.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CharRow()
        {
            if (_id == 0 && _wFrames == 0) return;
            JsonOut j = S5Rows.Begin("s5.char");
            j.Str("character", S5Rows.Hex(_id)).Str("bodyModel", S5Rows.Hex(_bodyModel)).Bool("built", Built).Str("fpbody", S5Switches.Name(Fp))
             .Str("source", CharSourceRules.Name(Source)).Str("why", LastWhy).Num("frames", _wFrames).Num("bind", _wBind).Num("seBones", _wSe).Num("retarget", _wRet)
             .Num("swimming", _wSwim).Num("swimmingOnRetarget", _wSwimRet).Num("nonFinite", _wNonFinite).Num("cameraMode", (long)_lastCameraMode)
             .Bool("firstPersonBodyDrawn", FirstPersonBodyDrawn).Str("retargetPath", Retarget.Path).Num("requiredMapped", (long)Retarget.RequiredMapped).Str("retargetWhy", Retarget.Why ?? "").Bool("retargetOff", _retargetOff);
            j.Arr("parts");
            foreach (CharPartView v in _parts.Values)
                j.Obj(null).Num("key", v.PartKey).Str("model", S5Rows.Hex(v.ModelKey)).Bool("hiddenInFirstPerson", (v.Flags & S5Conv.HiddenInFirstPerson) != 0)
                 .Bool("drawn", v.Drawn).Bool("materials", v.MaterialsReady).Str("show", S7pFirstPerson.Name(v.Show)).EndObj(); // S7p-character (P8)
            j.EndArr();
            CharFrame f = _lastNewer;
            if (f != null && Built)
            {
                j.Num("tick", f.Tick).Num("alpha", _lastAlpha).Vec("o", _lastPose.Ox, _lastPose.Oy, _lastPose.Oz).Quat4("q", _lastPose.Qx, _lastPose.Qy, _lastPose.Qz, _lastPose.Qw)
                 .Num("s", _lastPose.S).Vec("c1O", f.Ox, f.Oy, f.Oz).Quat4("c1Q", f.Qx, f.Qy, f.Qz, f.Qw).Num("c1S", f.S).Num("bonesVersion", (long)f.BonesVersion)
                 .Vec("entityWorld", _frameT.position.x, _frameT.position.y, _frameT.position.z);
                CharFrame fo = _lastOlder;
                if (fo != null && _lastAlpha < 0.999) j.Vec("c1Oo", fo.Ox, fo.Oy, fo.Oz);   // S5-2d: N7a judges interpolated samples
                j.Arr("bones");
                foreach (int b in new[] { Pelvis, Head, RightPalm })
                {
                    Vector3 w = _bones[b].position;
                    j.Obj(null).Num("index", (long)b).Str("name", _sk.Bones[b].SeBone).Vec("snWorld", w.x, w.y, w.z);
                    if (f.BonesValid) S5Rows.Rows12(j, "seAbs", f.Bones, b * 12);
                    j.EndObj();
                }
                j.EndArr();
            }
            S5Rows.End(j);
            _wFrames = _wBind = _wSe = _wRet = _wSwim = _wSwimRet = _wNonFinite = 0;
        }

        // ------------------------------------------------------------------------------------------ teardown

        private void DestroyPart(CharPartView v)
        {
            PaintRelease(v.Slots, "part rebuilt"); // S7p-character (P9)
            _m.Owned.DestroyMesh(v.Mesh);
            v.Mesh = null;
            try { if (v.Go != null) UnityEngine.Object.Destroy(v.Go); } catch (Exception) { }
            v.Go = null;
            v.Renderer = null;
        }

        /// <summary>Everything Unity of the character destroyed explicitly (record change, EntityRemove, ClearAll, world loss, quit).</summary>
        public void TearDown(string why)
        {
            bool any = _frame != null || _parts.Count > 0;
            foreach (CharPartView v in _parts.Values) DestroyPart(v);
            try { Paint.Clear(why); } catch (Exception) { } // S7p-character (P9)
            HeadShadowOnly = false;
            _parts.Clear();
            _children.Clear();
            Retarget.Release(_m.Owned);
            try { if (_frame != null) UnityEngine.Object.Destroy(_frame); } catch (Exception) { }
            _frame = null;
            _frameT = _entityT = _rigT = null;
            _bones = null;
            Skinned.OnTearDown(); // S7
            _recordVersion = -1;
            _pendingBuilds = 0;
            _serial++;
            if (_blend != null) _blend.Reset();
            SetFirstPersonBody(false);
            if (any) DeLog.Info("DE.S5 char torn down (" + why + ")");
        }

        /// <summary>A new SE generation: the C1 history restarts.</summary>
        public void OnNewSePeer()
        {
            C1.Reset();
            TearDown("new SE generation");
            _failedModels.Clear();
            _id = 0;
        }

        /// <summary>C7 pendingParts: character parts whose model, materials or SkinnedBind are missing.</summary>
        public int Pending(Func<PartRecord, bool> modelPending)
        {
            int n = 0;
            foreach (CharacterRecord r in _m.State.Characters.Values)
                foreach (PartRecord p in r.Parts.Values)
                    if (modelPending(p) || (p.Role == PartRole.Skinned && !_m.Binds.ContainsKey(p.ModelKey))) n++;
            return n;
        }

        public void Status(JsonObjectWriter w)
        {
            w.Str("character", DeLog.Hex(_id)).Bool("characterBuilt", Built).Str("characterSource", CharSourceRules.Name(Source)).Str("characterWhy", LastWhy)
             .Str("fpbody", S5Switches.Name(Fp)).Bool("firstPersonBodyDrawn", FirstPersonBodyDrawn).Num("characterFrames", Frames).Num("framesSeBones", FramesSeBones)
             .Num("framesRetarget", FramesRetarget).Num("framesBind", FramesBind).Num("framesSwimming", FramesSwimming).Num("framesSwimRetarget", FramesSwimRetarget)
             .Num("nonFinite", NonFinite).Num("c1Fresh", C1.Fresh).Num("c1Busy", C1.Busy).Str("retargetPath", Retarget.Path).Num("requiredMapped", (long)Retarget.RequiredMapped).Str("retargetWhy", Retarget.Why ?? "")
             .Bool("retargetOff", _retargetOff).Str("characterDisabled", DisabledList)
             .Bool("headShadowOnly", HeadShadowOnly).Str("paint", Paint.Describe()) // S7p-character (P8, P9)
             .Num("selfShadowLayer", (long)S7pSelfShadow.Layer).Str("selfShadow", S7pSelfShadow.Note).Num("selfShadowCamBits", S7pSelfShadow.CameraBitSets).Num("selfShadowLightBits", S7pSelfShadow.LightBitSets).Num("lightsSwept", S7pSelfShadow.LightsSwept);
        }
    }

    /// <summary>
 /// The swim retarget (CONTRACT §5.4 "Character", HYPOTHESIS O22): the ghost Player's animator
 /// (<c>Player.main.playerAnimator</c>, driven by S5a's parameters with AlwaysAnimate) is the source. Path H when the
 /// animator is humanoid: an Avatar for the SE rig from <c>AvatarBuilder.BuildHumanAvatar</c> (human column of canon1,
 /// a T-pose description with the upper arms raised), two <c>HumanPoseHandler</c>s made once, per frame GetHumanPose →
 /// SetHumanPose with bodyPosition reset (no root motion; one reused HumanPose). Path G otherwise: per row with a
 /// Subnautica bone, <c>R_se = (R_sn · R_sn,rest⁻¹) · R_se,rest</c>. Either path needs all 15 required human bones. The
 /// first use logs <c>DE.S5 rig …</c> (and s5.rig) so the real run fills canon1's snBone column.
 /// </summary>
    internal sealed class CharacterRetarget
    {
        public string Path = "none";
        public string Why = "not set up";
        /// <summary>Review F2: of the 15 required canonical bones, how many the active path maps (H: human bones, G: snBones).</summary>
 /// <summary>How path G's rests were taken.</summary>
        public string RestNote = "";
        public int RequiredMapped;
        private bool _tried, _ready, _rigLogged;
        private Animator _src;
        private Avatar _avatar;
        private HumanPoseHandler _srcH, _dstH;
        private HumanPose _pose, _restPose;
        private Vector3 _restBody;
        // path G
        private Transform[] _snBones;
        private bool[] _mapped;
        private Quat[] _snRest, _snNow, _seRestWorld, _scratch;
        private Transform _snRoot;

        public bool Ready { get { return _ready; } }

        /// <summary>Sets up once (or again after <see cref="Release"/>); false while the source or the rig is missing.</summary>
        public bool EnsureReady(Transform rig, Transform[] bones, Trs[] rest, CanonSkeleton sk, MirrorObjects owned)
        {
            if (_ready)
            {
                if (_src == null) { Release(owned); Why = "source animator gone"; return false; }
                return true;
            }
            if (_tried) return false;
            _tried = true;
            try
            {
                Player p = Player.main;
                _src = p != null ? p.playerAnimator : null;
                if (_src == null) { Why = "Player.main.playerAnimator missing"; _tried = false; return false; }
                LogRig(sk);
                if (_src.isHuman && _src.avatar != null && _src.avatar.isHuman && TrySetupH(rig, bones, rest, sk, owned)) { Path = "H"; _ready = true; Why = "ok"; }
                else if (TrySetupG(bones, rest, sk)) { Path = "G"; _ready = true; Why = "ok"; }
                RequiredMapped = Path == "H" ? 15 - sk.MissingRequiredHuman().Count : Path == "G" ? 15 - sk.MissingRequiredSnBones().Count : 0;
                if (_ready) DeLog.Info("DE.S5 char retarget ready: path " + Path + " (source " + _src.name + ", required bones mapped " + RequiredMapped + "/15)");
                else DeLog.Warn("DE.S5 char retarget UNAVAILABLE: " + Why + " (swimming falls back to SeBones)");
            }
            catch (Exception e)
            {
                Why = "setup " + e.GetType().Name + ": " + e.Message;
                DeLog.Warn("DE.S5 char retarget setup FAILED: " + Why + " (SeBones while swimming)");
            }
            return _ready;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogRig(CanonSkeleton sk)
        {
            if (_rigLogged) return;
            _rigLogged = true;
            Transform[] all = _src.GetComponentsInChildren<Transform>(true);
            var names = new StringBuilder();
            for (int i = 0; i < all.Length && i < 256; i++) { if (i > 0) names.Append(','); names.Append(all[i].name); }
            var clips = new StringBuilder();
            try
            {
                RuntimeAnimatorController rc = _src.runtimeAnimatorController;
                if (rc != null) foreach (AnimationClip c in rc.animationClips) { if (clips.Length > 0) clips.Append(','); clips.Append(c != null ? c.name : "null"); if (clips.Length > 4000) break; }
            }
            catch (Exception e) { clips.Append("error " + e.GetType().Name); }
            Avatar av = _src.avatar;
            var humanMap = new StringBuilder();
            if (_src.isHuman)
            {
                foreach (string h in CanonSkeleton.RequiredHuman)
                {
                    try
                    {
                        Transform t = _src.GetBoneTransform((HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), h));
                        if (humanMap.Length > 0) humanMap.Append(',');
                        humanMap.Append(h).Append('=').Append(t != null ? t.name : "-");
                    }
                    catch (Exception) { }
                }
            }
            string line = "DE.S5 rig animator=" + _src.name + " isHuman=" + _src.isHuman + " avatar=" + (av != null ? av.name : "null") + " avatarHuman=" + (av != null && av.isHuman) +
                          " avatarValid=" + (av != null && av.isValid) + " bones=" + all.Length + " names=[" + names + "] clips=[" + clips + "] human=[" + humanMap + "]";
            DeLog.Info(line);
            JsonOut j = S5Rows.Begin("s5.rig");
            j.Str("animator", _src.name).Bool("isHuman", _src.isHuman).Str("avatar", av != null ? av.name : "").Bool("avatarHuman", av != null && av.isHuman)
             .Num("bones", (long)all.Length).Str("names", names.ToString()).Str("clips", clips.ToString()).Str("human", humanMap.ToString())
             .Num("snBoneMapped", (long)(15 - sk.MissingRequiredSnBones().Count)).Num("humanMapped", (long)(15 - sk.MissingRequiredHuman().Count));
            S5Rows.End(j);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TrySetupH(Transform rig, Transform[] bones, Trs[] rest, CanonSkeleton sk, MirrorObjects owned)
        {
            try
            {
                List<string> miss = sk.MissingRequiredHuman();
                if (miss.Count > 0) { Why = "canon1 human column misses " + string.Join(",", miss.ToArray()); return false; }
                string[] humanNames = HumanTrait.BoneName;
                var human = new List<HumanBone>();
                foreach (CanonBone b in sk.Bones)
                {
                    if (!b.HasHuman) continue;
                    var hb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), b.Human);
                    int hi = (int)hb;
                    if (hi < 0 || hi >= humanNames.Length) continue;
                    var bone = new HumanBone { boneName = b.SeBone, humanName = humanNames[hi] };
                    bone.limit.useDefaultValues = true;
                    human.Add(bone);
                }
                Trs[] tpose = CharacterMath.TPose(rest, sk.Parents, sk.IndexOf("SE_RigLUpperarm"), sk.IndexOf("SE_RigLForearm1"), sk.IndexOf("SE_RigRUpperarm"), sk.IndexOf("SE_RigRForearm1"));
                var skel = new SkeletonBone[bones.Length + 1];
                skel[0] = new SkeletonBone { name = rig.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one };
                for (int i = 0; i < bones.Length; i++)
                {
                    Trs t = tpose[i];
                    skel[i + 1] = new SkeletonBone
                    {
                        name = bones[i].name,
                        position = new Vector3((float)t.P.X, (float)t.P.Y, (float)t.P.Z),
                        rotation = new Quaternion((float)t.Q.X, (float)t.Q.Y, (float)t.Q.Z, (float)t.Q.W),
                        scale = new Vector3((float)t.S.X, (float)t.S.Y, (float)t.S.Z),
                    };
                }
                var desc = new HumanDescription
                {
                    human = human.ToArray(), skeleton = skel, upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                    armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
                };
                // the rest pose on the transforms while the avatar is built and the rest body position is read
                for (int i = 0; i < bones.Length; i++) WriteT(bones[i], rest[i]);
                _avatar = AvatarBuilder.BuildHumanAvatar(rig.gameObject, desc);
                if (_avatar == null || !_avatar.isValid || !_avatar.isHuman) { Why = "BuildHumanAvatar gave " + (_avatar == null ? "null" : "valid=" + _avatar.isValid + " human=" + _avatar.isHuman); Destroy(owned); return false; }
                _avatar.name = "DE.S5 canon1 avatar";
                owned.Track(_avatar);
                _srcH = new HumanPoseHandler(_src.avatar, _src.transform);
                _dstH = new HumanPoseHandler(_avatar, rig);
                _dstH.GetHumanPose(ref _restPose);
                _restBody = _restPose.bodyPosition;
                _srcH.GetHumanPose(ref _pose);
                DeLog.Info("DE.S5 char avatar built (path H): " + human.Count + " human bones, rest body " + _restBody + ", T-pose description (upper arms raised), muscles=" +
                           (_pose.muscles != null ? _pose.muscles.Length : 0));
                return true;
            }
            catch (Exception e)
            {
                Why = "path H " + e.GetType().Name + ": " + e.Message;
                Destroy(owned);
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TrySetupG(Transform[] bones, Trs[] rest, CanonSkeleton sk)
        {
            List<string> miss = sk.MissingRequiredSnBones();
            if (miss.Count > 0) { Why = (Why == "not set up" ? "" : Why + "; ") + "path G: snBone unmapped for " + miss.Count + "/15 required bones (HYPOTHESIS column, filled after the real run)"; return false; }
            int n = bones.Length;
            _snRoot = _src.transform;
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in _src.GetComponentsInChildren<Transform>(true)) if (!byName.ContainsKey(t.name)) byName[t.name] = t;
            _snBones = new Transform[n];
            _mapped = new bool[n];
            _snRest = new Quat[n];
            _snNow = new Quat[n];
            _scratch = new Quat[n];
            _seRestWorld = new Quat[n];
            CharacterMath.WorldRotations(rest, sk.Parents, _seRestWorld);
            Quaternion rootInv = Quaternion.Inverse(_snRoot.rotation);
            Vector3 rootPos = _snRoot.position;

            // rigs' bind poses (the source's from its skinned meshes' bindposes, SE's from SkinnedBind), each SE rest rotation
 // aligned by bone direction (CharacterMath.AlignRest); the live pose is the fallback for bones without a bindpose
            var bindWorld = new Dictionary<Transform, Matrix4x4>();
            foreach (SkinnedMeshRenderer smr in _src.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                Transform[] sb = smr.bones;
                if (mesh == null || sb == null) continue;
                Matrix4x4[] bp = mesh.bindposes;
                if (bp == null || bp.Length != sb.Length) continue;
                Matrix4x4 m2w = smr.transform.localToWorldMatrix;   // HYPOTHESIS: the mesh space is the renderer's transform today
                for (int k = 0; k < sb.Length; k++) if (sb[k] != null && !bindWorld.ContainsKey(sb[k])) bindWorld[sb[k]] = m2w * bp[k].inverse;
            }
            var snPos = new Vec3[n];
            var snValid = new bool[n];
            int fromBind = 0, fromLive = 0;
            for (int i = 0; i < n; i++)
            {
                CanonBone b = sk.Bones[i];
                Transform t;
                if (!b.HasSnBone || !byName.TryGetValue(b.SnBone, out t)) continue;
                _snBones[i] = t;
                _mapped[i] = true;
                Matrix4x4 bw;
                Quaternion rot;
                Vector3 pos;
                if (bindWorld.TryGetValue(t, out bw)) { rot = bw.rotation; pos = bw.GetColumn(3); fromBind++; }
                else { rot = t.rotation; pos = t.position; fromLive++; }
                _snRest[i] = Q(rootInv * rot);
                Vector3 pr = rootInv * (pos - rootPos);
                snPos[i] = new Vec3(pr.x, pr.y, pr.z);
                snValid[i] = true;
            }
            var abs = new Aff[n];
            CharacterMath.Absolute(rest, sk.Parents, abs);
            var sePos = new Vec3[n];
            var humans = new string[n];
            for (int i = 0; i < n; i++) { sePos[i] = abs[i].T; humans[i] = sk.Bones[i].Human; }
            int[] dirChild = CharacterMath.DirectionChildren(sk.Parents, _mapped, humans);
            var aligned = new Quat[n];
            CharacterMath.AlignRest(_seRestWorld, sePos, snPos, snValid, _mapped, dirChild, sk.Parents, aligned, _scratch);
            _seRestWorld = aligned;
            RestNote = "bind poses for " + fromBind + " mapped bone(s)" + (fromLive > 0 ? ", live pose for " + fromLive : "") + ", rests direction-aligned";
            DeLog.Info("DE.S5 char retarget rest: " + RestNote);
            return true;
        }

        /// <summary>Retargets this frame's source pose onto the SE rig; <paramref name="dst"/> receives the resulting locals.</summary>
        public bool Evaluate(Transform[] bones, Trs[] rest, Trs[] dst)
        {
            if (!_ready || _src == null) return false;
            if (Path == "H")
            {
                for (int i = 0; i < bones.Length; i++) WriteT(bones[i], rest[i]);
                _srcH.GetHumanPose(ref _pose);
                _pose.bodyPosition = _restBody; // no root motion
                _dstH.SetHumanPose(ref _pose);
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform t = bones[i];
                    Vector3 p = t.localPosition, s = t.localScale;
                    Quaternion q = t.localRotation;
                    dst[i] = new Trs { P = new Vec3(p.x, p.y, p.z), Q = new Quat(q.x, q.y, q.z, q.w), S = new Vec3(s.x, s.y, s.z) };
                }
                return true;
            }
            if (Path == "G")
            {
                Quaternion rootInv = Quaternion.Inverse(_snRoot.rotation);
                for (int i = 0; i < bones.Length; i++) _snNow[i] = _mapped[i] && _snBones[i] != null ? Q(rootInv * _snBones[i].rotation) : Quat.Identity;
                CharacterMath.RetargetGeneric(_snNow, _snRest, _mapped, rest, _seRestWorld, ParentsOf(bones), dst, _scratch);
                return true;
            }
            return false;
        }

        private int[] _parents;
        private int[] ParentsOf(Transform[] bones)
        {
            if (_parents != null && _parents.Length == bones.Length) return _parents;
            _parents = new int[bones.Length];
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++) index[bones[i]] = i;
            for (int i = 0; i < bones.Length; i++) { int pi; _parents[i] = bones[i].parent != null && index.TryGetValue(bones[i].parent, out pi) ? pi : -1; }
            return _parents;
        }

        private static Quat Q(Quaternion q) { return new Quat(q.x, q.y, q.z, q.w); }

        private static void WriteT(Transform t, Trs v)
        {
            t.localPosition = new Vector3((float)v.P.X, (float)v.P.Y, (float)v.P.Z);
            t.localRotation = new Quaternion((float)v.Q.X, (float)v.Q.Y, (float)v.Q.Z, (float)v.Q.W);
            t.localScale = new Vector3((float)v.S.X, (float)v.S.Y, (float)v.S.Z);
        }

        private void Destroy(MirrorObjects owned)
        {
            try { if (_srcH != null) _srcH.Dispose(); } catch (Exception) { }
            try { if (_dstH != null) _dstH.Dispose(); } catch (Exception) { }
            _srcH = _dstH = null;
            if (_avatar != null) owned.Destroy(_avatar);
            _avatar = null;
        }

        /// <summary>Handlers disposed, the avatar destroyed; set up again on the next use (a new rig).</summary>
        public void Release(MirrorObjects owned)
        {
            Destroy(owned);
            _ready = false;
            _tried = false;
            _src = null;
            _snBones = null;
            _parents = null;
            Path = "none";
            Why = "not set up";
        }
    }
}
