using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>One model known to the mirror: the received payload (kept to re-prepare under a mutant change) and its prepared form.</summary>
    internal sealed class ModelEntry
    {
        public ulong Key;
        public CompletedModel Raw;
        public PreparedModel Prepared;
        public bool Synthetic, Failed, Preparing;
        public string Error;
        public int Generation;
        public ulong FirstWaitMs;
        /// <summary>The submesh material keys read from the payload's submesh table at ModelEnd (C7 pendingParts before the worker parsed it).</summary>
        public ulong[] MaterialKeys;
    }

    /// <summary>Worker → main: a prepared model.</summary>
    internal sealed class PrepareResult
    {
        public ulong Key;
        public int Generation;
        public PreparedModel Model;
        public string Error;
        /// <summary>The worker's payloadHash check failed (CONTRACT §5.4: checked on a worker).</summary>
        public bool HashMismatch;
    }

    /// <summary>
 /// The C3/C4/C2 consumer and the mirrored entities (ROADMAP S3 SN plugin; CONTRACT §5.1 C2-C4, §5.4; ARCHITECTURE
 /// §3.2, §5.8, §5.13). Main thread, from the bridge's Update:
 /// <list type="number">
 /// <item>Drain C4 then C3 within 4 ms or 48 MiB per frame (CONTRACT §5.1); C3 payload bytes are copied out of the ring
 /// by <see cref="ModelAssembler"/>; corrupt framing re-syncs the tail and marks the mirror incomplete
 ///.</item>
 /// <item>Hand complete models to the worker (payloadHash check, parse, mirror, (I0, I2, I1), winding measure with the
 /// automatic flip below 1 %), and dirty cells whose models are all prepared (cell builds: part matrices composed in
 /// SE space, then mirrored; chunks per cell × material × colour; every triangle exactly once, Wire's coverage
 /// rule).</item>
 /// <item>While the host world is up: MarmosetUBER from Addressables, materials and textures, the Frame/Entity
 /// GameObjects, chunk uploads within 2 ms per frame (<see cref="UploadScheduler"/>). When the world goes (quit to
 /// menu), every Unity object of the mirror is destroyed; the wire state stays and is rebuilt in the next world.</item>
 /// <item>Poses: C2 read and sampled with the camera's bracket (ticks, alpha) in the same LateUpdateLast / onPreCull
 /// callbacks that place the camera (S1 CameraDriver), so the block and the camera come from the same SE ticks
 /// (S3.M4 lags one snapshot). Without a camera sample the newest snapshot is used.</item>
 /// <item>C7: mirroredEntityCount (grids known), pendingParts (parts whose model or materials are incomplete),
 /// c3BytesDrained (payload + 8-B header per drained C3 message), as the S0 stand-ins defined them.</item>
 /// </list>
 /// </summary>
    internal sealed unsafe class MirrorConsumer : IDisposable
    {
        public const int StatsEveryFrames = 300;
        public const ulong ModelWaitLogMs = 10000;
        public const ulong NoPoseLogMs = 5000;

        private readonly ByteRing _c3, _c4;
        public readonly EntityTableReader C2;
        public readonly ModelAssembler Assembler = new ModelAssembler();
        public readonly MirrorState State = new MirrorState();
        public readonly MirrorWorker Worker = new MirrorWorker();
        public readonly UploadScheduler Uploads;
        public readonly MarmosetMaterials Marmoset = new MarmosetMaterials();
        public readonly TextureImport Textures;
        public readonly MirrorObjects Owned = new MirrorObjects();
        public readonly S3Options Options = new S3Options();

        private readonly Dictionary<ulong, ModelEntry> _models = new Dictionary<ulong, ModelEntry>();
        private readonly Dictionary<ulong, MaterialDefData> _materials = new Dictionary<ulong, MaterialDefData>();
        private readonly Dictionary<ulong, MirrorGridView> _views = new Dictionary<ulong, MirrorGridView>();
        private readonly Dictionary<string, MaterialSlot> _slots = new Dictionary<string, MaterialSlot>(StringComparer.Ordinal);
        private readonly Dictionary<GridCell, int> _cellVersion = new Dictionary<GridCell, int>();
        private readonly HashSet<ulong> _builtLogged = new HashSet<ulong>();
        private readonly Stopwatch _sw = new Stopwatch();
        private int _generation;
        /// <summary>Cell build versions come from one serial that is never reset, so a late result of a dropped build can never match a new one.</summary>
        private int _buildSerial;
        private bool _worldReady, _suppressed, _hidden;
        private S3MutantSet _mutants;

        // S5 (CONTRACT §5.4 "C1–C4 conventions for S5"): Child parts, subgrids and rows (S5Mirror), the player's character
 // (CharacterMirror), SkinnedBind by model key, the chunk build gate and queue, S5.M1/M2 (set by S5Host)
        public readonly S5Mirror S5;
        /// <summary>S5 review F1: the root grid whose group holds the camera this frame (C2 flag bit 1), 0 when none; its subgrid
 /// rows and dynamic children are sampled at the camera orientation's tick too (S5Mirror.PoseRowsCore).</summary>
        public ulong GroupRoot;
        public long GroupLocked;
        public readonly CharacterMirror Character;
        /// <summary>S7a: the placement ghost's translucent look (a mirrored grid with the Ghost marker; GhostView.cs).</summary>
        public readonly GhostView Ghost;
        /// <summary>S7p-scene-above: above-water event variants, missile trails, projector holograms (S7pScene.cs).</summary>
        public readonly S7pSceneSn S7p;
        /// <summary>S6: called after the character at every camera sample (the proxies follow the mirror's applied poses).</summary>
        public Action<CamBracket, bool> S6CameraSample;
        private readonly Dictionary<ulong, float[]> _binds = new Dictionary<ulong, float[]>();
        public readonly DrainGate Gate = new DrainGate();
        public readonly CellBuildQueue BuildQueue = new CellBuildQueue();
        private readonly Dictionary<GridCell, long> _postedFrame = new Dictionary<GridCell, long>();
        private bool _buildAllowed = true;
        private long _frame;
        private bool _s5m1, _s5m2;
        private long _pendingSerial = -1, _modelSerial;
        private int _pendingCached;

        internal Dictionary<ulong, MirrorGridView> ViewMap { get { return _views; } }
        internal Dictionary<ulong, ModelEntry> ModelMap { get { return _models; } }
        internal Dictionary<ulong, float[]> Binds { get { return _binds; } }
        public bool S5M1 { get { return _s5m1; } }
        public bool S5M2 { get { return _s5m2; } }

        // counters
        public long C3Messages, C4Messages, C3Corrupt, C4Corrupt, DrainFramesOverBudget, CellBuilds, ChunkUploads, Rebuilds;
        public long PoseFrames, PoseInterpolated, PoseHeld, PoseExtrapolated, PoseNewest, PosePrevious, PoseMissing, WorldTeardowns, RootsDestroyedByScene;
        public double DrainMsMax;
        public string LastC3Error, LastC4Error;
        private ulong _lastErrorLogMs;
        private int _lastCameraFrame = -1;
        private long _uploadFailuresLogged;

        public MirrorConsumer(byte* sectionBase, ByteRing c3, ByteRing c4)
        {
            _c3 = c3;
            _c4 = c4;
            C2 = new EntityTableReader(Layout.RegionPtr(sectionBase, RegionId.C2SeEntities));
            Uploads = new UploadScheduler(Win32.QpcNow, Win32.QpcFrequency());
            Textures = new TextureImport(Worker, Uploads, Owned, Options);
            Textures.ColorMask = Marmoset.ColorMask = new ColorMaskPaint(this); // S7t-colour-mask (T6): SE's colour per texel on the block slots
            S5 = new S5Mirror(this);
            Character = new CharacterMirror(this, sectionBase);
            Ghost = new GhostView(this); // S7a
            S7p = new S7pSceneSn(this); // S7p-scene-above
        }

        /// <summary>S5Host: the S5 seeded failures of this side (S5.M1 rows/roots one snapshot behind, S5.M2 per-model flip skipped).</summary>
        public void SetS5Mutants(bool m1, bool m2)
        {
            _s5m1 = m1;
            if (m2 != _s5m2)
            {
                _s5m2 = m2;
                RePrepareAll("mutant S5.M2 " + (m2 ? "on (per-model flip skipped)" : "off"));
            }
        }

        public bool WorldReady { get { return _worldReady; } }
        public bool Suppressed { get { return _suppressed; } }
        public S3MutantSet Mutants { get { return _mutants; } }
        public IEnumerable<MirrorGridView> Views { get { return _views.Values; } }
        public int ModelCount { get { return _models.Count; } }
        public int MaterialDefCount { get { return _materials.Count; } }

        // ------------------------------------------------------------------------------------------ lifecycle events

 /// <summary>
 /// New SE generation (CONTRACT §6): every mirror, part, model and material of the old SE is dropped. The worker's
 /// queued jobs go, so the texture sets and slots that waited for them are dropped too; the generation bump makes late results of jobs in progress harmless.
 /// </summary>
        public void OnNewSePeer()
        {
            Assembler.OnResync();
            State.Clear();
            _models.Clear();
            _materials.Clear();
            _binds.Clear();
            ForgetBuilds();
            _pendingCells.Clear();
            C2.Reset();
            Worker.Clear();
            _generation++;
            ResetTextures("new SE generation (worker jobs dropped)");
            DestroyViews("new SE generation");
            S5.TearDown("new SE generation");
            Character.OnNewSePeer();
            DeLog.Info("DE.Mirror reset (new SE generation): mirrors, parts, models, materials and texture sets dropped; C2 history restarted");
        }

        /// <summary>The bridge re-synced the C3/C4 tails (new peer or link epoch): the model in flight is dropped.</summary>
        public void OnRingsResynced(string why)
        {
            Assembler.OnResync();
        }

        /// <summary>A hello was acknowledged: records of other world epochs are dropped from now on (a new epoch clears the mirror).</summary>
        public void OnHello(uint epoch)
        {
            if (State.AcceptEpoch != 0 && State.AcceptEpoch != epoch)
            {
                State.Clear();
                ForgetBuilds();
                DestroyViews("world epoch " + State.AcceptEpoch + " -> " + epoch);
            }
            State.AcceptEpoch = epoch;
        }

        public void OnMutantsChanged(S3MutantSet m)
        {
            S3MutantSet old = _mutants;
            _mutants = m;
            Textures.M8 = m.M8;
            if (old.M1 != m.M1 || old.M2 != m.M2) RePrepareAll("mutants " + m.Describe());
            if (old.M3 != m.M3 || old.M6 != m.M6) DestroyViews("mutants " + m.Describe() + " (renderers re-made)");
            if (old.M8 != m.M8) ResetTextures("mutants " + m.Describe());
        }

        public void ApplyOptions(bool rebuildMeshes, bool reloadTextures)
        {
            Uploads.BudgetMs = Options.UploadBudgetMs;
            Uploads.CompressEveryFrames = Math.Max(1, Options.CompressEveryFrames);
            if (rebuildMeshes) RebuildAllCells("options " + Options.Describe());
            if (reloadTextures) ResetTextures("options " + Options.Describe());
        }

        public void ResetTextures(string why)
        {
            foreach (MaterialSlot s in _slots.Values) Owned.Destroy(s.Mat);
            _slots.Clear();
            _s10SlotFast.Clear(); // S10 hook
            Textures.DestroyAll();
            foreach (MirrorGridView v in _views.Values) foreach (ChunkView c in v.Chunks.Values) { c.Slot = null; c.C4Logged = c.C4FinalLogged = false; if (c.Renderer != null) c.Renderer.enabled = false; }
            S5.ResetChildSlots();
            Character.ResetSlots();
            DeLog.Info("DE.Mirror textures and materials re-made (" + why + ")");
        }

        /// <summary>Re-prepare every model (mesh mutants S3.M1/M2, S5.M2 changed): the cells rebuild when they are prepared again.</summary>
        public void RePrepareAll(string why)
        {
            _generation++;
            foreach (ModelEntry e in _models.Values)
            {
                if (e.Synthetic || e.Raw == null) continue;
                e.Prepared = null;
                e.Failed = false;
                PostPrepare(e);
            }
            Character.TearDown("models re-prepared (" + why + ")");
            RebuildAllCells(why);
        }

        public void RebuildAllCells(string why)
        {
            foreach (GridRecord g in State.Grids.Values) g.DirtyAll();
            foreach (MirrorGridView v in _views.Values) v.PlacementLogged = false;
            _builtLogged.Clear();
            Rebuilds++;
            DeLog.Info("DE.Mirror rebuild all cells (" + why + ")");
        }

        /// <summary>
 /// de.s3 teardown (S3.C12): the world-loss path of a quit to menu, run now, and nothing rebuilt until
 /// <see cref="Resume"/>. The same <see cref="OnWorldLost"/> as the real scene unload, so S3.M6 (roots given
 /// SceneCleanerPreserve + DontDestroyOnLoad, which a scene unload does not take and the mutant's world-loss path
 /// does not clean) leaves its roots alive here too and de.s3 count turns S3.C12 red.
 /// </summary>
        public void Suppress(bool clearData)
        {
            _suppressed = true;
            if (_worldReady)
            {
                OnWorldLost("de.s3 teardown (the world-loss path of a quit to menu)");
                _worldReady = false;
            }
            else if (!_mutants.M6) TearDownUnity("de.s3 teardown (world not up)");
            if (clearData)
            {
                State.Clear();
                _models.Clear();
                _materials.Clear();
                _binds.Clear();
                ForgetBuilds();
                _pendingCells.Clear();
            }
        }

        /// <summary>The host world went away (quit to menu, reload) or de.s3 teardown emulates it: every Unity object of the mirror goes, except S3.M6's preserved roots.</summary>
        private void OnWorldLost(string why)
        {
            if (_mutants.M6) LeakPreservedRoots(why);
            else TearDownUnity(why);
        }

        public void Resume()
        {
            _suppressed = false;
            RebuildAllCells("de.s3 rebuild");
        }

        public void SetHidden(bool hidden)
        {
            _hidden = hidden;
            foreach (MirrorGridView v in _views.Values) if (v.Alive) v.SetVisible(!hidden);
        }

        // ------------------------------------------------------------------------------------------ per frame

 /// <summary>Main thread, from the bridge's Update.</summary>
        public void Tick(ulong nowMs, bool worldReady, long frame)
        {
            S10Sn.Mirror = this; // S10 hook (round 2 A2): the s10.mem row counts this mirror's meshes and kit
            _frame = frame;
            DirtyCellSet.CurrentFrame = frame;
            long s10 = S10Sn.T(); // S10 hook: the kit stage (C3 drain, worker results, prepared models), timed
            Drain(nowMs);
            TakeWorkerResults(nowMs);
            S7pFlushPrepared(); // S7p-gameplay (P14): one part pass for every model prepared this frame (was one pass per model)
            S10Sn.Add(S10PerfRules.StageKit, s10); s10 = S10Sn.T(); // S10 hook: the placement stage (cells, views, uploads, materials, S5) from here
            ScheduleCellBuilds(nowMs);

            bool ready = worldReady && !_suppressed;
            if (_worldReady && !ready) OnWorldLost(worldReady ? "suppressed" : "the host world went away (quit to menu or reload)");
            if (!_worldReady && ready) DeLog.Info("DE.Mirror world up: views, materials and uploads enabled");
            _worldReady = ready;
            if (!ready) { S10Sn.Add(S10PerfRules.StagePlace, s10); return; } // S10 hook

            Marmoset.Tick(nowMs);
            SyncViews(nowMs);
            Textures.Tick(nowMs);
            Uploads.RunFrame(frame);
            if (Uploads.Failures != _uploadFailuresLogged)
            {
                _uploadFailuresLogged = Uploads.Failures;
                DeLog.Warn("DE.Mirror upload job failed (" + Uploads.Failures + " so far): " + Uploads.LastError);
            }
            UpdateMaterials();
            // S5: hierarchy, children, rows, culling and statistics; the character's records, builds and materials
            S5.Tick(nowMs, Time.unscaledDeltaTime * 1000.0);
            Character.Tick(nowMs);
            Character.UpdateMaterials();
            Ghost.ApplyFrame(); // S7a: the placement ghost's material, colour and visibility after every material step
            S7p.Tick(nowMs); // S7p-scene-above: above-water effects, missile trails, holograms (after the materials); never throws
 // poses: the newest snapshot now; the camera callbacks re-sample with their bracket later in this frame
            int f = Time.frameCount;
            if (_lastCameraFrame < f - 1)
            {
                ApplyPoses(default(CamBracket), false);
                Character.OnCameraSample(default(CamBracket), false);
            }
            if (frame % StatsEveryFrames == 0 && (C3Messages + C4Messages > 0 || _views.Count > 0)) DeLog.Info(StatsLine());
            S10Sn.Add(S10PerfRules.StagePlace, s10); // S10 hook
        }

        private void Drain(ulong nowMs)
        {
            _sw.Reset();
            _sw.Start();
            long bytes = 0;
            double budget = Options.DrainBudgetMs;
            long byteBudget = Options.DrainBudgetBytes;
            // S5: the C4 head at the start of the frame's drain; cells are built only from a state that reached it (no
 // half-applied edit is drawn) or after 10 frames regardless (CONTRACT §5.4 "Chunk key and consumer rules")
            ulong c4Head = _c4.Head;
            // C4 (small records) first, then C3
            for (int guard = 0; guard < 100000; guard++)
            {
                if (_sw.Elapsed.TotalMilliseconds >= budget || bytes >= byteBudget) break;
                uint type;
                byte* p;
                int n;
                RingReadStatus st = _c4.TryPeek(out type, out p, out n);
                if (st == RingReadStatus.Empty) break;
                if (st != RingReadStatus.Ok)
                {
                    C4Corrupt++;
                    _c4.ResyncTail();
                    State.NoteLoss();
                    DeLog.Warn("DE.Mirror C4 " + st + ": consumer tail re-synchronised to the head (records lost: C7 pendingParts reports the loss until " +
                               "SE's next ClearAll; SE's consumer audit resends everything when its rings are drained and the counters stay short)");
                    break;
                }
                // S5 s5.edit: the cell a part leaves (read before the record is applied)
                bool partRecord = (type == C4Type.PartAdd || type == C4Type.PartRemove || type == C4Type.PartRecolor) && n >= 16;
                ulong editEntity = 0, editPart = 0;
                CellKey? oldCell = null;
                if (partRecord)
                {
                    editEntity = *(ulong*)p;
                    editPart = *(ulong*)(p + 8);
                    GridRecord eg;
                    PartRecord ep;
                    if (State.Grids.TryGetValue(editEntity, out eg) && eg.Parts.TryGetValue(editPart, out ep) && ep.Chunked) oldCell = ep.Cell;
                }
                ulong id;
                C4Outcome o = State.Feed(type, p, n, out id);
                _c4.Advance();
                C4Messages++;
                bytes += n + 8;
                if (o == C4Outcome.Error) ReportError(nowMs, "C4", State.LastError, ref LastC4Error);
                else if (o == C4Outcome.GridRemoved) RemoveView(id, "EntityRemove");
                else if (o == C4Outcome.ClearAll)
                {
                    ForgetBuilds();
                    DestroyViews("ClearAll(worldEpoch " + State.LastClearAll.worldEpoch + ", linkEpoch " + State.LastClearAll.linkEpoch + ")");
                }
                else if (o == C4Outcome.PartChanged && partRecord && _worldReady) S5.NoteEdit(type, editEntity, editPart, oldCell, _frame);
            }
            bool reachedHead = _c4.ConsumerTail >= c4Head;
            _buildAllowed = Gate.Allow(reachedHead);
            for (int guard = 0; guard < 100000; guard++)
            {
                if (_sw.Elapsed.TotalMilliseconds >= budget || bytes >= byteBudget) break;
                uint type;
                byte* p;
                int n;
                RingReadStatus st = _c3.TryPeek(out type, out p, out n);
                if (st == RingReadStatus.Empty) break;
                if (st != RingReadStatus.Ok)
                {
                    C3Corrupt++;
                    _c3.ResyncTail();
                    Assembler.OnResync();
                    State.NoteLoss();
                    DeLog.Warn("DE.Mirror C3 " + st + ": consumer tail re-synchronised to the head (the model in flight and the records after it are " +
                               "lost: C7 pendingParts and c3BytesDrained show it until SE's consumer audit resends everything after a ClearAll)");
                    break;
                }
                C3Outcome o = Assembler.Feed(type, p, n);
                _c3.Advance();
                C3Messages++;
                bytes += n + 8;
                switch (o)
                {
                    case C3Outcome.ModelComplete: OnModelComplete(Assembler.Completed, nowMs); break;
                    case C3Outcome.MaterialDefined: OnMaterialDefined(Assembler.CompletedMaterial); break;
                    case C3Outcome.SkinnedBind:
                        // S5: the character models' binds (C7 pendingParts counts skinned parts until theirs arrived)
                        _binds[Assembler.CompletedBind.ModelKey] = Assembler.CompletedBind.Rows;
                        _modelSerial++;
                        break;
                    case C3Outcome.Error: ReportError(nowMs, "C3", Assembler.LastError, ref LastC3Error); break;
                }
            }
            _sw.Stop();
            double ms = _sw.Elapsed.TotalMilliseconds;
            if (ms > DrainMsMax) DrainMsMax = ms;
            if (ms > budget * 1.5) DrainFramesOverBudget++;
        }

        private void ReportError(ulong nowMs, string ring, string what, ref string last)
        {
            last = what;
            if (nowMs - _lastErrorLogMs < 1000) return;
            _lastErrorLogMs = nowMs;
            DeLog.Warn("DE.Mirror " + ring + " record refused: " + what + " (counted; at most one line per second)");
        }

        private void OnModelComplete(CompletedModel c, ulong nowMs)
        {
            ulong key = c.Begin.modelKey;
            ModelEntry e;
            if (_models.TryGetValue(key, out e) && !c.Synthetic && e.Raw != null && e.Raw.Hash == c.Hash && !e.Failed) return; // same payload again
            e = new ModelEntry { Key = key, Raw = c.Synthetic ? null : c, Synthetic = c.Synthetic, FirstWaitMs = nowMs };
            if (!c.Synthetic) e.MaterialKeys = ModelPayload.SubmeshMaterialKeys(c.Begin, c.Payload, c.Length);
            _models[key] = e;
            _modelSerial++;
            if (c.Synthetic) return; // S0 stand-in traffic: complete, never built
            PostPrepare(e);
        }

        private void PostPrepare(ModelEntry e)
        {
            e.Preparing = true;
            e.Generation = _generation;
            CompletedModel c = e.Raw;
            int gen = _generation;
            bool mirror = !_mutants.M1, swap = !_mutants.M2, allowFlip = !_s5m2;
            Worker.Post("prepare " + DeLog.Hex(e.Key), () =>
            {
                var r = new PrepareResult { Key = c.Begin.modelKey, Generation = gen };
                try
                {
                    // the payloadHash check runs here, on the worker
                    ulong actual;
                    if (!ModelPayload.HashMatches(c, out actual))
                    {
                        r.HashMismatch = true;
                        r.Error = "ModelEnd " + DeLog.Hex(c.Begin.modelKey) + " payloadHash " + DeLog.Hex(c.Hash) + " != computed " + DeLog.Hex(actual) +
                                  " (FNV-1a 64 over the concatenated ModelChunk payloads, checked on the worker)";
                        return r;
                    }
                    ModelData m;
                    string error;
                    if (!ModelPayload.TryParse(c.Begin, c.Payload, c.Length, out m, out error)) { r.Error = error; return r; }
                    m.PayloadHash = c.Hash;
                    r.Model = MeshBuilder.Prepare(m, mirror, swap, allowFlip);
                }
                catch (Exception ex)
                {
                    r.Error = "prepare " + ex.GetType().Name + ": " + ex.Message;
                }
                return r;
            });
        }

        private void OnMaterialDefined(MaterialDefData d)
        {
            MaterialDefData old;
            if (_materials.TryGetValue(d.Key, out old) && old.ColorMetal == d.ColorMetal && old.NormalGloss == d.NormalGloss && old.AddMaps == d.AddMaps && old.AlphaMask == d.AlphaMask)
                return;
            _materials[d.Key] = d;
            _modelSerial++;
            DeLog.Info("DE.Mirror MaterialDef " + d.Describe());
            // slots of this material are re-made with the new paths
            var stale = new List<string>();
            foreach (KeyValuePair<string, MaterialSlot> kv in _slots) if (kv.Value.MaterialKey == d.Key) stale.Add(kv.Key);
            foreach (string k in stale)
            {
                MaterialSlot s = _slots[k];
                _slots.Remove(k);
                _s10SlotFast.Clear(); // S10 hook
                foreach (MirrorGridView v in _views.Values)
                    foreach (ChunkView c in v.Chunks.Values)
                        if (c.Slot == s) { c.Slot = null; if (c.Renderer != null) c.Renderer.enabled = false; }
                Owned.Destroy(s.Mat);
            }
            if (stale.Count > 0) { S5.ResetChildSlots(); Character.ResetSlots(); }
        }

        private void TakeWorkerResults(ulong nowMs)
        {
            long s7pStart = System.Diagnostics.Stopwatch.GetTimestamp(); // S7p-gameplay (P14): results within a frame budget
            for (int i = 0; i < 64; i++)
            {
                if (!S7pMirrorBudget.Within(s7pStart, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency, S7pMirrorBudget.ResultsBudgetMs, i)) { S7pResultsDeferred++; return; }
                object o;
                if (!Worker.TryTake(out o)) return;
                var pr = o as PrepareResult;
                if (pr != null) { OnPrepared(pr); continue; }
                var cb = o as CellBuildResult;
                if (cb != null) { OnCellBuilt(cb); continue; }
                var sr = o as SharedMeshResult;
                if (sr != null) { S5.OnSharedBuilt(sr); continue; }
                var kr = o as SkinnedBuildResult;
                if (kr != null) { Character.OnBuilt(kr); continue; }
                if (Textures.OnWorkerResult(o)) continue;
                var f = o as WorkerFailure;
                if (f != null) DeLog.Warn("DE.Mirror worker job '" + f.Name + "' failed: " + f.Error.GetType().Name + ": " + f.Error.Message);
            }
        }

        private void OnPrepared(PrepareResult r)
        {
            ModelEntry e;
            if (!_models.TryGetValue(r.Key, out e) || r.Generation != e.Generation) return;
            e.Preparing = false;
            _modelSerial++;
            if (r.Error != null)
            {
                e.Failed = true;
                e.Error = r.Error;
                if (r.HashMismatch) Assembler.NoteHashMismatch(r.Error);
                DeLog.Warn("DE.Mirror model " + DeLog.Hex(r.Key) + " REFUSED: " + r.Error);
                return;
            }
            e.Prepared = r.Model;
            _modelSerial++;
            PreparedModel m = r.Model;
            string line = "DE.Mirror model " + m.Describe();
            if (m.Flipped) DeLog.Warn(line + " (FLIPPED automatically: agreement " + (m.RawAgreement * 100).ToString("0.###", CultureInfo.InvariantCulture) + " % < 1 %; CONTRACT §2.5)");
            else if (m.FlipSkipped) DeLog.Warn(line + " (MUTANT S5.M2: the census asked for the flip, skipped)");
            else if (m.RawAgreement < 0.99) DeLog.Warn(line + " (winding agreement below 99 %: S3.C3 FAIL for this model)");
            else if (S10Sn.TakeMirrorLine()) DeLog.Info(line); // S10 hook (round 2 A): the per-model line under the budget (warnings always)
            ModelRow(m);
            // S7p-gameplay (P14): the parts of this model are marked by S7pFlushPrepared, once per frame for every model
 // prepared in it (a 100k-part grid with 222 models cost one full part scan per model before)
            _s7pPrepared.Add(r.Key);
            S7pPreparedBatched++;
            S5.OnModelPrepared(r.Key);
        }

        // S7p-gameplay (P14): batched part marking and the deferral counters of the per-frame budgets (s7p.mirror row)
        private readonly HashSet<ulong> _s7pPrepared = new HashSet<ulong>();
        public long S7pPreparedBatched, S7pPostsDeferred, S7pResultsDeferred;

        private void S7pFlushPrepared()
        {
            if (_s7pPrepared.Count == 0) return;
            foreach (GridRecord g in State.Grids.Values)
                foreach (PartRecord p in g.Parts.Values)
                    if (_s7pPrepared.Contains(p.ModelKey))
                    {
                        if (p.Chunked) g.DirtyCells.Add(p.Cell);
                        else g.ChildrenDirty = true;
                    }
            _s7pPrepared.Clear();
        }

        // S7p-gameplay (P14): the S3.C1 grid model totals of the "built" line, computed once per grid version and model
 // serial (they were summed over every part of the grid for every cell uploaded: 100k parts × ~400 cells)
        private readonly Dictionary<ulong, long[]> _s7pTotals = new Dictionary<ulong, long[]>();

        private void S7pGridModelTotals(GridRecord g, out long verts, out long tris)
        {
            long[] t;
            if (_s7pTotals.TryGetValue(g.Id, out t) && t[0] == g.Version && t[1] == _modelSerial) { verts = t[2]; tris = t[3]; return; }
            verts = tris = 0;
            foreach (PartRecord pr in g.Parts.Values)
            {
                ModelEntry me;
                if (_models.TryGetValue(pr.ModelKey, out me) && me.Prepared != null) { verts += me.Prepared.VertexCount; tris += me.Prepared.TriangleCount; }
            }
            if (_s7pTotals.Count > 4096) _s7pTotals.Clear();
            _s7pTotals[g.Id] = new long[] { g.Version, _modelSerial, verts, tris };
        }

        /// <summary>s5.model (S5.G1-E4): the winding census per model, the automatic flip and S5.M2's skip.</summary>
        private void ModelRow(PreparedModel m)
        {
            try
            {
                JsonOut j = S5Rows.Begin("s5.model");
                j.Str("model", S5Rows.Hex(m.Key)).Num("verts", (long)m.VertexCount).Num("tris", (long)m.TriangleCount).Num("submeshes", (long)(m.Submeshes != null ? m.Submeshes.Length : 0))
                 .Num("winding", m.RawAgreement).Num("degenerate", (long)m.Degenerate).Bool("flipped", m.Flipped).Bool("flipSkipped", m.FlipSkipped)
                 .Bool("skinned", m.Skinned).Bool("mirrorZ", m.Mirrored).Bool("triSwap", m.Swapped).Str("hash", S5Rows.Hex(m.Hash));
                S5Rows.End(j);
            }
            catch (Exception e) { DeLog.WarnOnce("s5.modelrow", "DE.S5 s5.model row not written: " + e.GetType().Name + ": " + e.Message); }
        }

        private static string CellId(ulong grid, CellKey cell) { return grid.ToString("x", CultureInfo.InvariantCulture) + "/" + cell; }

        /// <summary>True while a cell build of this grid is in flight (s5.grid waits for a settled grid).</summary>
        internal bool CellsInFlight(ulong grid)
        {
            foreach (GridCell k in _postedFrame.Keys) if (k.Grid == grid) return true;
            foreach (GridCell k in _pendingCells.Keys) if (k.Grid == grid) return true;
            foreach (GridCell k in _uploading) if (k.Grid == grid) return true;
            return false;
        }

        /// <summary>Every in-flight build forgotten (its late result is discarded by the version check): with every _cellVersion reset.</summary>
        private void ForgetBuilds()
        {
            _cellVersion.Clear();
            _postedFrame.Clear();
            _pendingPosted.Clear();
            _uploading.Clear();
            BuildQueue.Clear();
        }

        /// <summary>
 /// Posts the builds of dirty cells whose models are prepared (or refused/synthetic: those parts are skipped).
 /// S5 (CONTRACT §5.4 "Chunk key and consumer rules", "Budgets"): only from a state where this frame's C4 drain
 /// reached its starting head (or after 10 frames regardless), one build in flight per cell (a cell dirtied again
 /// stays dirty and is rebuilt once more after), at most 256 in flight, dirty-newest then nearest; a cell reads its
 /// own parts from the grid's cell index.
 /// </summary>
        private void ScheduleCellBuilds(ulong nowMs)
        {
            if (!_buildAllowed) { LogModelWaits(nowMs); return; }
            BuildOptions opts = null;
            BuildQueue.BeginFrame();
            BuildQueue.DistanceOrder = S10Sn.OrderOn; // S10 hook (part 3): near cells first (newest dirty), then the rest nearest first
            BuildQueue.NearM = S10Sn.NearM;
            if (_s7tCoarse.Count > 0) S7tPrune(); // S7t-sn-altitude (T3): removed grids leave the coarse set
            bool s10Sweep = _frame % S10Sn.LodSweepFrames == 0; // S10 hook (part 3): per-cell LOD sweep
            if (s10Sweep) S10PruneCoarse();
            foreach (GridRecord g in State.Grids.Values)
            {
                S7tSyncMode(g); // S7t-sn-altitude (T3): an impostor bit (or the switch) changed: every cell rebuilt in the new mode
                if (s10Sweep) S10LodSweep(g); // S10 hook (part 3): cells crossing the LOD edge are dirtied and rebuilt in their new mode
                if (g.DirtyCells.Count == 0) continue;
                foreach (CellKey cell in g.DirtyCells)
                {
                    var key = new GridCell(g.Id, cell);
                    if (BuildQueue.IsInFlight(key)) { BuildQueue.SkippedInFlight++; continue; }
                    if (!CellReady(g, cell)) continue;
                    BuildQueue.Offer(new CellCandidate { Key = key, DirtyFrame = g.DirtyCells.FrameOf(cell), Distance = CellDistance(g, cell) });
                }
            }
            List<CellCandidate> picks = BuildQueue.Pick();
            int s7tPosted = 0; // S7t-sn-altitude (T3): impostor cells posted this frame
            long s7pStart = System.Diagnostics.Stopwatch.GetTimestamp(); // S7p-gameplay (P14): posts (part clones) within a frame budget
            for (int i = 0; i < picks.Count; i++)
            {
                if (!S7pMirrorBudget.Within(s7pStart, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency, S7pMirrorBudget.PostBudgetMs, i))
                {
                    S7pPostsDeferred += picks.Count - i; // the rest stay dirty and are offered again next frame
                    break;
                }
                GridCell key = picks[i].Key;
                GridRecord g;
                if (!State.Grids.TryGetValue(key.Grid, out g)) continue;
                // S7t-sn-altitude (T3): an impostor's cells are capped per frame; the rest stay dirty for the next frame (counted)
                bool s7tCoarse = _s7tCoarse.Contains(g.Id);
                bool coarse = s7tCoarse || _s10Coarse.Contains(key); // S10 hook (part 3): a cell beyond the LOD edge builds coarse too
                if (s7tCoarse && !ImpostorRules.MayPost(s7tPosted, S7tImpostors.Settings.CellsPerFrame)) { S7tImpostors.CellsDeferred++; continue; }
                if (opts == null) opts = Options.Build(_mutants);
                var input = new CellBuildInput { GridId = g.Id, Cell = key.Cell, Options = opts, Impostor = coarse };
                HashSet<ulong> keys;
                if (g.CellParts.TryGetValue(key.Cell, out keys))
                {
                    foreach (ulong pk in keys)
                    {
                        PartRecord p;
                        ModelEntry e;
                        if (!g.Parts.TryGetValue(pk, out p) || !_models.TryGetValue(p.ModelKey, out e) || e.Synthetic || e.Failed || e.Prepared == null) continue;
                        if (coarse) // S7t-sn-altitude (T3): large blocks only (the model's largest extent against the grid's block size)
                        {
                            PreparedModel pm = e.Prepared;
                            if (!ImpostorRules.KeepPart(pm.MaxX - pm.MinX, pm.MaxY - pm.MinY, pm.MaxZ - pm.MinZ, g.TargetClass, S7tImpostors.Settings.MinPartFrac)) { if (s7tCoarse) S7tImpostors.PartsSkipped++; continue; }
                            if (s7tCoarse) S7tImpostors.PartsKept++;
                        }
                        input.Parts.Add(new PartBuild { Part = p.Clone(), Model = e.Prepared });
                    }
                }
                if (s7tCoarse) { s7tPosted++; S7tImpostors.CellsPosted++; }
                else if (coarse) S10Sn.CoarseCellsPosted++; // S10 hook
                int v = ++_buildSerial;
                _cellVersion[key] = v;
                _postedFrame[key] = _frame;
                input.Version = v;
                CellBuilds++;
                BuildQueue.MarkPosted(key);
                g.DirtyCells.Remove(key.Cell);
                Func<object> buildJob = () =>
                {
                    try { return MeshBuilder.BuildCell(input); }
                    catch (Exception ex) { return new CellBuildResult { GridId = input.GridId, Cell = input.Cell, Version = input.Version, Error = ex.GetType().Name + ": " + ex.Message }; }
                };
                //: a cell holding a pending edit (a block placed, ground or blown up) jumps the streaming backlog
                if (S5.HasPendingEdit(g.Id, key.Cell)) Worker.PostUrgent("build " + CellId(g.Id, key.Cell), buildJob);
                else Worker.Post("build " + CellId(g.Id, key.Cell), buildJob);
            }
            LogModelWaits(nowMs);
        }

        // ------------------------------------------------------------------------------------------ S10 per-cell LOD (part 3)

 // the cells built coarse by the S10 LOD (Wire S10Lod: beyond 150 m, or beyond 40 m on a grid of 40,000+ parts)
        private readonly HashSet<GridCell> _s10Coarse = new HashSet<GridCell>();
        private readonly List<GridCell> _s10Gone = new List<GridCell>();

        /// <summary>S10 (part 3): every LodSweepFrames frames, each cell of the grid against the LOD edge (hysteresis); a cell
 /// that changes side is dirtied (its chunks stay until the new build replaces them, as the S7t swap). With the LOD off
 /// every coarse cell goes back to full.</summary>
        private void S10LodSweep(GridRecord g)
        {
            int parts = g.Parts.Count;
            foreach (CellKey cell in g.CellParts.Keys)
            {
                var key = new GridCell(g.Id, cell);
                bool was = _s10Coarse.Contains(key);
                bool now = S10Sn.LodOn && S10Lod.Coarse(CellDistance(g, cell), was, parts, S10Sn.LodM, S10Sn.HystM, S10Sn.HullParts, S10Sn.HullNearM);
                if (now == was) continue;
                if (now) { _s10Coarse.Add(key); S10Sn.LodSwapsToCoarse++; }
                else { _s10Coarse.Remove(key); S10Sn.LodSwapsToFull++; }
                g.DirtyCells.Add(cell);
            }
            S10Sn.CoarseCells = _s10Coarse.Count;
        }

        /// <summary>S10 (part 3): coarse marks of grids or cells that are gone.</summary>
        private void S10PruneCoarse()
        {
            if (_s10Coarse.Count == 0) return;
            _s10Gone.Clear();
            foreach (GridCell k in _s10Coarse)
            {
                GridRecord g;
                if (!State.Grids.TryGetValue(k.Grid, out g) || !g.CellParts.ContainsKey(k.Cell)) _s10Gone.Add(k);
            }
            for (int i = 0; i < _s10Gone.Count; i++) _s10Coarse.Remove(_s10Gone[i]);
            S10Sn.CoarseCells = _s10Coarse.Count;
        }

        // ------------------------------------------------------------------------------------------ S7t impostors

 /// <summary>S7t-sn-altitude (T3): the grids built coarse right now (impostor bit set and the switch on).</summary>
        private readonly HashSet<ulong> _s7tCoarse = new HashSet<ulong>();

        /// <summary>S7t-sn-altitude (T3): the grid's mode follows its GridAdd bit and the switch; a change rebuilds every cell and the children.</summary>
        private void S7tSyncMode(GridRecord g)
        {
            bool coarse = S7tImpostors.Active(g.Flags);
            if (coarse == _s7tCoarse.Contains(g.Id)) return;
            if (coarse) { _s7tCoarse.Add(g.Id); S7tImpostors.SwapsToCoarse++; }
            else { _s7tCoarse.Remove(g.Id); S7tImpostors.SwapsToFull++; }
            S7tImpostors.Count = _s7tCoarse.Count;
            g.DirtyAll(); // the coarse chunks of a cell stay until its full build replaces them (ApplyCell), and the other way round
            DeLog.Info("DE.S7t impostor grid=" + DeLog.Hex(g.Id) + (coarse ? " coarse (large blocks, flat colour, no children)" : " full (within the normal mirror radius)") +
                       " cells=" + g.CellParts.Count + " impostors=" + _s7tCoarse.Count);
        }

        private readonly List<ulong> _s7tGone = new List<ulong>();

        private void S7tPrune()
        {
            _s7tGone.Clear();
            foreach (ulong id in _s7tCoarse) if (!State.Grids.ContainsKey(id)) _s7tGone.Add(id);
            for (int i = 0; i < _s7tGone.Count; i++) _s7tCoarse.Remove(_s7tGone[i]);
            S7tImpostors.Count = _s7tCoarse.Count;
        }

        /// <summary>S7t-sn-altitude (T3): is the entity (or its root) an impostor now? (lights and effects skip it)</summary>
        internal bool S7tIsImpostor(ulong id)
        {
            if (id == 0 || _s7tCoarse.Count == 0) return false;
            if (_s7tCoarse.Contains(id)) return true;
            GridRecord g;
            return State.Grids.TryGetValue(id, out g) && g.IsSubgrid && _s7tCoarse.Contains(g.ParentId);
        }

        /// <summary>S7t-sn-altitude (T3): the coarse set (S5Mirror's culling and children read it).</summary>
        internal HashSet<ulong> S7tCoarse { get { return _s7tCoarse; } }

        /// <summary>S7t-sn-altitude (T3): an impostor chunk's material: Subnautica's MarmosetUBER, no texture, the part's paint as a flat colour, no style.</summary>
        private MaterialSlot S7tImpostorSlot(long colourKey, float h, float sat, float val)
        {
            string id = SlotId(ImpostorRules.MaterialKey, colourKey);
            MaterialSlot s;
            if (_slots.TryGetValue(id, out s)) return s;
            if (!Marmoset.Ready) return null;
            s = new MaterialSlot { MaterialKey = ImpostorRules.MaterialKey, ColourKey = colourKey, H = h, S = sat, V = val, Label = "impostor/" + id };
            s.Mat = Marmoset.Create(s, Owned);
            float kh, ks, kv;
            ColorMaskRules.ShaderKey(h, sat, val, out kh, out ks, out kv); // SE's shader key, as the block slots
            Rgba c = PaintRules.MaterialColour(kh, ks, kv);
            if (s.Mat.HasProperty("_Color")) s.Mat.SetColor("_Color", new Color(c.R, c.G, c.B, 1f));
            Rgba sc = PaintRules.SpecularFor(c);
            if (s.Mat.HasProperty("_SpecColor")) s.Mat.SetColor("_SpecColor", new Color(sc.R, sc.G, sc.B, 1f));
            s.Mat.name = "DE.S7t impostor " + id;
            _slots[id] = s;
            S7tImpostors.SlotsCreated++;
            return s;
        }

        /// <summary>Every part of the cell has its model prepared (or refused/synthetic, then skipped).</summary>
        private bool CellReady(GridRecord g, CellKey cell)
        {
            HashSet<ulong> keys;
            if (!g.CellParts.TryGetValue(cell, out keys)) return true; // emptied: the build removes its chunks
            foreach (ulong pk in keys)
            {
                PartRecord p;
                ModelEntry e;
                if (!g.Parts.TryGetValue(pk, out p)) continue;
                if (!_models.TryGetValue(p.ModelKey, out e)) return false;
                if (e.Synthetic || e.Failed) continue;
                if (e.Prepared == null) return false;
            }
            return true;
        }

        /// <summary>Distance from the main camera to the cell centre through the grid's (or its root's) last pose; 0 when unknown.</summary>
        private double CellDistance(GridRecord g, CellKey cell)
        {
            if (!S5.CameraKnown) return 0;
            MirrorGridView v;
            ulong id = g.IsSubgrid ? g.ParentId : g.Id;
            if (!_views.TryGetValue(id, out v) || !v.HasPose) return 0;
            double cs = g.CellSize;
            var local = new Vec3((cell.X + 0.5) * cs, (cell.Y + 0.5) * cs, (cell.Z + 0.5) * cs);
            if (g.IsSubgrid)   // review F18: a subgrid cell sits where its relative row puts it, not at the root's origin
            {
                MirrorGridView sub;
                if (_views.TryGetValue(g.Id, out sub) && sub.HasPose) local = Aff.FromSeRows(sub.LastRow, 0).Apply(local);
            }
            Vec3 w = MirrorPoseMath.ApplySeLocal(v.LastPose, local);
            Vector3 c = S5.CameraPos;
            return (w - new Vec3(c.x, c.y, c.z)).Length;
        }

        private ulong _nextWaitScanMs;

        private void LogModelWaits(ulong nowMs)
        {
            if (nowMs < _nextWaitScanMs) return;   // review F6: a full part scan once a second, not per frame
            _nextWaitScanMs = nowMs + 1000;
            // CONTRACT §5.1 C3: parts referring to a missing model wait (logged after 10 s)
            foreach (GridRecord g in State.Grids.Values)
            {
                foreach (PartRecord p in g.Parts.Values)
                {
                    if (_models.ContainsKey(p.ModelKey)) { _waiting.Remove(p.ModelKey); continue; }
                    _waiting.TryGetValue(p.ModelKey, out ulong since);
                    if (since == 0) { _waiting[p.ModelKey] = nowMs; continue; }
                    if (nowMs - since >= ModelWaitLogMs && _waitLogged.Add(p.ModelKey))
                        DeLog.Warn("DE.Mirror part " + DeLog.Hex(p.PartKey) + " of grid " + DeLog.Hex(g.Id) + " waits for model " + DeLog.Hex(p.ModelKey) + " for " + (nowMs - since) + " ms");
                }
            }
        }

        private readonly Dictionary<ulong, ulong> _waiting = new Dictionary<ulong, ulong>();
        private readonly HashSet<ulong> _waitLogged = new HashSet<ulong>();

        private readonly Dictionary<GridCell, CellBuildResult> _pendingCells = new Dictionary<GridCell, CellBuildResult>();

        private void OnCellBuilt(CellBuildResult r)
        {
            var id = new GridCell(r.GridId, r.Cell);
            int v;
            if (!_cellVersion.TryGetValue(id, out v) || v != r.Version) return; // superseded
            BuildQueue.MarkDone(id); // S5: the next build of this cell may be posted
            long posted;
            if (!_postedFrame.TryGetValue(id, out posted)) posted = _frame;
            _postedFrame.Remove(id);
            if (!State.Grids.ContainsKey(r.GridId)) return;
            if (r.Error != null) { DeLog.Warn("DE.Mirror cell build FAILED grid=" + DeLog.Hex(r.GridId) + " cell=" + r.Cell + ": " + r.Error); return; }
            _pendingCells[id] = r; // applied (uploaded) while the world is up
            _pendingPosted[id] = posted;
        }

        private readonly Dictionary<GridCell, long> _pendingPosted = new Dictionary<GridCell, long>();
        private readonly HashSet<GridCell> _uploading = new HashSet<GridCell>();

        // ------------------------------------------------------------------------------------------ views

        private MirrorGridView EnsureView(ulong id, ulong nowMs)
        {
            MirrorGridView v;
            if (_views.TryGetValue(id, out v) && v.Alive) return v;
            v = MirrorGridView.Create(id, _mutants.M6, OnRootDestroyed);
            v.NoPoseSinceMs = nowMs;
            if (_hidden) v.SetVisible(false);
            _views[id] = v;
            if (_mutants.M6) DeLog.Warn("DE.Mirror root " + v.Frame.name + " given SceneCleanerPreserve + DontDestroyOnLoad (MUTANT S3.M6: it survives quit to menu)");
            return v;
        }

        /// <summary>
 /// Views follow the built cells: a grid gets its Frame/Entity when its first non-empty cell is built (S0 synthetic
 /// grids, whose models are never meshes, get none); views of grids that are gone are removed.
 /// </summary>
        private void SyncViews(ulong nowMs)
        {
            List<ulong> gone = null;
            foreach (ulong id in _views.Keys) if (!State.Grids.ContainsKey(id)) (gone = gone ?? new List<ulong>()).Add(id);
            if (gone != null) foreach (ulong id in gone) RemoveView(id, "grid gone");

            if (_pendingCells.Count > 0)
            {
                foreach (KeyValuePair<GridCell, CellBuildResult> kv in _pendingCells)
                {
                    CellBuildResult r = kv.Value;
                    if (!State.Grids.ContainsKey(r.GridId)) continue;
                    MirrorGridView existing;
                    bool hasView = _views.TryGetValue(r.GridId, out existing) && existing.Alive;
                    if (r.Chunks.Count == 0 && !hasView) continue;
                    long posted;
                    if (!_pendingPosted.TryGetValue(kv.Key, out posted)) posted = _frame;
                    QueueCellUpload(EnsureView(r.GridId, nowMs), r, posted);
                }
                _pendingCells.Clear();
                _pendingPosted.Clear();
            }

            foreach (MirrorGridView v in _views.Values)
            {
                if (!v.HasPose && !v.NoPoseLogged && v.Chunks.Count > 0 && nowMs - v.NoPoseSinceMs > NoPoseLogMs)
                {
                    v.NoPoseLogged = true;
                    DeLog.Warn("DE.Mirror entity " + DeLog.Hex(v.Id) + " has no C2 pose after " + NoPoseLogMs + " ms (hidden until one arrives; C2 " +
                               (C2.EverWritten ? "written, entity absent" : "never written") + ")");
                }
            }
        }

        private void QueueCellUpload(MirrorGridView v, CellBuildResult r, long postedFrame)
        {
            var id = new GridCell(r.GridId, r.Cell);
            _uploading.Add(id);
            bool urgent = S5.HasPendingEdit(r.GridId, r.Cell);   //: an edited cell is uploaded before the backlog
            var next = new int[1];   //: the next chunk of this cell (the upload runs over several frames)
            var job = new UploadJob
            {
                Kind = UploadKind.Mesh, Name = "cell " + CellId(r.GridId, r.Cell),
                Run = () =>
                {
                    int cur;
                    if (!_cellVersion.TryGetValue(id, out cur) || cur != r.Version) { _uploading.Remove(id); return true; } // superseded meanwhile
                    if (!v.Alive) { _uploading.Remove(id); return true; }
                    if (!ApplyCellChunks(v, r, next)) return false;   // more chunks next frame
                    ApplyCell(v, r);
                    _uploading.Remove(id);
                    S5.OnCellUploaded(r.GridId, r.Cell, postedFrame, _frame);
                    return true;
                },
            };
            if (urgent) Uploads.EnqueueUrgent(job);
            else Uploads.Enqueue(job);
        }

        /// <summary>The view of an entity (created hidden when missing): S5 children and subgrids need one before any chunk.</summary>
        internal MirrorGridView EnsureViewInternal(ulong id, ulong nowMs) { return EnsureView(id, nowMs); }

        /// <summary>S10 (round 2 A2): the mirror's memory share for the s10.mem row (once per 10 s, main thread).</summary>
        internal struct S10MemoryStats
        {
            public int Views, Chunks, ReadableChunks, Models, TextureSets, RawTextures;
            public long MeshBytes, MeshCpuBytes, KitBytes;
        }

        internal S10MemoryStats S10Memory()
        {
            var s = new S10MemoryStats { Views = _views.Count, Models = _models.Count, TextureSets = Textures.SetCount, RawTextures = Textures.RawCount };
            foreach (MirrorGridView v in _views.Values)
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (!c.HasMesh) continue;
                    s.Chunks++;
                    long b = (long)c.Vertices * 48 + c.IndexBytes;
                    s.MeshBytes += b;
                    if (c.Mesh != null && c.Mesh.isReadable) { s.ReadableChunks++; s.MeshCpuBytes += b; }
                }
            foreach (ModelEntry e in _models.Values)
                if (e.Prepared != null) s.KitBytes += (long)e.Prepared.VertexCount * 48 + (long)e.Prepared.TriangleCount * 12;
            return s;
        }

        // S10 hook (round 2 A): ApplyCell's scratch sets reused (main thread only; one ApplyCell at a time), no allocation per edit
        private readonly HashSet<ChunkKey> _s10Keep = new HashSet<ChunkKey>();
        private readonly List<ChunkKey> _s10Remove = new List<ChunkKey>();

        /// <summary>
 /// the 21:07 session: a 336-block hologram, single cell uploads of 24-82 ms against the
 /// 2 ms budget, frames of 110-180 ms for 4 s): a cell's chunks are uploaded ChunkStepBudgetMs at a time, at least one
 /// per frame; true once every chunk is in (then <see cref="ApplyCell"/> removes the stale ones and runs the hooks).
 /// </summary>
        public const double ChunkStepBudgetMs = 2.0;
        public long ChunkSteps;

        private bool ApplyCellChunks(MirrorGridView v, CellBuildResult r, int[] next)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double perMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            while (next[0] < r.Chunks.Count)
            {
                if (next[0] > 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / perMs >= ChunkStepBudgetMs && (System.Diagnostics.Stopwatch.GetTimestamp() - t0) > 0)
                {
                    ChunkSteps++;
                    return false;
                }
                UploadChunk(v, r.Chunks[next[0]]);
                next[0]++;
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) / perMs >= ChunkStepBudgetMs && next[0] < r.Chunks.Count) { ChunkSteps++; return false; }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ApplyCell(MirrorGridView v, CellBuildResult r)
        {
            HashSet<ChunkKey> keep = S10Sn.EditsOn ? _s10Keep : new HashSet<ChunkKey>();
            keep.Clear();
            foreach (ChunkMeshData c in r.Chunks) keep.Add(c.Key);   // uploaded by ApplyCellChunks
            FinishCell(v, r, keep);
        }

        private void UploadChunk(MirrorGridView v, ChunkMeshData c)
        {
            {
                // S5: NoShadows parts (SE's edges) form their own chunks without shadows; the Mesh object is reused
                ChunkView cv = v.EnsureChunk(c.Key, Options.Shadows && !c.Key.NoShadows, !_mutants.M3);
                if (cv.Mesh != null && !cv.Mesh.isReadable) { Owned.Destroy(cv.Mesh); cv.Mesh = null; } // S10 hook (round 2 A2): a freed chunk mesh is replaced, never rewritten
                if (cv.Mesh == null) cv.Mesh = Owned.NewMesh("DE.Mirror " + DeLog.Hex(v.Id) + " " + c.Key);
                MeshUpload.Upload(cv.Mesh, c);
                if (S10Sn.FreeMeshCpu) { cv.Mesh.UploadMeshData(true); S10Sn.MeshesFreed++; } // S10 hook (round 2 A2): no CPU copy kept (Mesh.UploadMeshData(markNoLongerReadable))
                cv.Filter.sharedMesh = cv.Mesh;
                cv.HasMesh = true;
                cv.Vertices = c.VertexCount;
                cv.Triangles = c.TriangleCount;
                cv.IndexBytes = c.IndexCount * (c.Use32 ? 4 : 2);
                cv.Agreement = c.Agreement;
                cv.MinX = c.MinX; cv.MinY = c.MinY; cv.MinZ = c.MinZ; cv.MaxX = c.MaxX; cv.MaxY = c.MaxY; cv.MaxZ = c.MaxZ;
                cv.Probes = S10Sn.EditsOn ? c.Probes : (PlacementProbe[])c.Probes.Clone(); // S10 hook: a build result is applied exactly once, its array is ours
                ChunkUploads++;
            }
        }

        private void FinishCell(MirrorGridView v, CellBuildResult r, HashSet<ChunkKey> keep)
        {
            List<ChunkKey> remove = S10Sn.EditsOn ? _s10Remove : new List<ChunkKey>();
            remove.Clear();
            foreach (KeyValuePair<ChunkKey, ChunkView> kv in v.Chunks) if (kv.Key.Cell.Equals(r.Cell) && !keep.Contains(kv.Key)) remove.Add(kv.Key);
            foreach (ChunkKey k in remove) v.RemoveChunk(k, Owned);
            v.PlacementLogged = false;
            Action<MirrorGridView, CellKey> s7 = InteriorHooks.CellApplied;
            if (s7 != null && r.Cell.C != 0) s7(v, r.Cell); // S7: interior/boundary chunks get their sky after every rebuild (InteriorView.cs)
            GridRecord g;
            int parts = State.Grids.TryGetValue(v.Id, out g) ? g.Parts.Count : 0;
            if (g != null && GhostRules.IsGhost(v.Id, g.Flags)) Ghost.OnEntityBuilt(v.Id, g.Flags, Ghost.RenderersOf(v)); // S7a: ghost material at once
            if (g != null && S7pScene.IsProjection(g.Flags)) S7p.OnEntityBuilt(v); // S7p-scene-above (P7): hologram material at once
 // S3.C1: the grid's model totals, the sum over its parts of the prepared model's counts
 // (what SE sums from MyModel); a chunk copies only the vertices its submesh references, so the chunk totals
 // (gridVerts) may differ for shared or unreferenced vertices without anything being lost. Drawn triangles are
 // exact (each model triangle once: Wire's coverage rule), so gridTris must equal gridModelTris
            long gridModelVerts = 0, gridModelTris = 0;
            if (g != null) S7pGridModelTotals(g, out gridModelVerts, out gridModelTris); // S7p-gameplay (P14): cached per grid version
            if (S10Sn.TakeMirrorLine()) DeLog.Info("DE.Mirror built grid=" + DeLog.Hex(v.Id) + " cell=" + r.Cell + " parts=" + r.Parts + " verts=" + r.Vertices + " tris=" + r.Triangles +
                       " modelVerts=" + r.ModelVertices + " modelTris=" + r.ModelTriangles + " chunks=" + r.Chunks.Count +
                       " winding=" + (r.MinAgreement * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%" +
                       " gridParts=" + parts + " gridVerts=" + v.Vertices + " gridTris=" + v.Triangles +
                       " gridModelVerts=" + gridModelVerts + " gridModelTris=" + gridModelTris + " uncoveredTris=" + r.UncoveredTriangles +
                       " overlapTris=" + r.OverlapTriangles + " (S3.C1: the model totals equal the producer's counts; drawn gridTris = gridModelTris)");
        }

        private void OnRootDestroyed(MirrorRootMarker m)
        {
            MirrorGridView v;
            if (m == null || !_views.TryGetValue(m.EntityId, out v)) return;
            _views.Remove(m.EntityId);
            v.OnRootDestroyed(Owned);
            RootsDestroyedByScene++;
            GridRecord g;
            if (State.Grids.TryGetValue(m.EntityId, out g)) g.DirtyAll(); // rebuilt in the next world
            DeLog.Info("DE.Mirror root of " + DeLog.Hex(m.EntityId) + " destroyed by the scene (quit to menu or scene unload): its meshes destroyed");
        }

        private void RemoveView(ulong id, string why)
        {
            MirrorGridView v;
            if (!_views.TryGetValue(id, out v)) return;
            _views.Remove(id);
            if (v.Alive) S5.OnViewRemoving(v); // its subgrid Frames are detached first (they live on)
            v.Destroy(Owned);
            DeLog.Info("DE.Mirror view " + DeLog.Hex(id) + " removed (" + why + ")");
        }

        /// <summary>Every view destroyed; built-but-not-uploaded cells dropped and every cell marked for a rebuild.</summary>
        private void DestroyViews(string why)
        {
            int n = _views.Count;
            foreach (MirrorGridView v in _views.Values) v.Destroy(Owned);
            _views.Clear();
            _pendingCells.Clear();
            _pendingPosted.Clear();
            _uploading.Clear();
            foreach (GridRecord g in State.Grids.Values) g.DirtyAll();
            Character.TearDown(why);
            if (n > 0) DeLog.Info("DE.Mirror " + n + " view(s) destroyed (" + why + ")");
        }

        /// <summary>
 /// S3.M6: the mistake S3.C12 must catch. The roots were given SceneCleanerPreserve + DontDestroyOnLoad, so the quit
 /// to menu (or de.s3 teardown, which runs the same world-loss path) did not take them, and nothing else cleans them
 /// up either: they and their meshes stay (counted by de.s3 count). Materials and textures still go.
 /// </summary>
        private void LeakPreservedRoots(string why)
        {
            int kept = 0;
            foreach (MirrorGridView v in _views.Values) if (v.Alive) kept++;
            foreach (MaterialSlot s in _slots.Values) Owned.Destroy(s.Mat);
            _slots.Clear();
            _s10SlotFast.Clear(); // S10 hook
            Textures.DestroyAll();
            Uploads.Clear();
            _pendingCells.Clear();
            _pendingPosted.Clear();
            _uploading.Clear();
            S5.TearDown(why);
            Character.TearDown(why);
            try { Marmoset.Release(); } catch (Exception) { }
            DeLog.Warn("DE.Mirror MUTANT S3.M6: " + why + ": " + kept + " preserved root(s) stay alive with their meshes (meshesAlive=" + Owned.MeshesAlive + ")");
        }

        /// <summary>Every Unity object of the mirror destroyed (world gone, teardown); the wire state is kept for the next world.</summary>
        private void TearDownUnity(string why)
        {
            WorldTeardowns++;
            S7p.TearDown(why); // S7p-scene-above: particles, hologram material, holds
            DestroyViews(why);
            S5.TearDown(why);
            foreach (MaterialSlot s in _slots.Values) Owned.Destroy(s.Mat);
            _slots.Clear();
            _s10SlotFast.Clear(); // S10 hook
            Textures.DestroyAll();
            Uploads.Clear();
            _uploading.Clear();
            Owned.DestroyAllMeshes();
            Owned.DestroyAllOthers();
            try { Marmoset.Release(); } catch (Exception) { }
            DeLog.Info("DE.Mirror Unity objects torn down (" + why + "): meshesAlive=" + Owned.MeshesAlive + " othersAlive=" + Owned.OthersAlive);
        }

        // ------------------------------------------------------------------------------------------ materials

        private void UpdateMaterials()
        {
            bool shaderReady = Marmoset.Ready;
            int frame = Time.frameCount;
            foreach (MirrorGridView v in _views.Values)
            {
                if (!v.Alive) continue;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (c.Go == null || !c.HasMesh) continue;
                    if (c.Slot == null && shaderReady)
                    {
                        c.Slot = Slot(c.Key);
                        if (c.Slot != null)
                        {
                            c.Renderer.sharedMaterial = c.Slot.Mat;
                            c.Renderer.enabled = !c.Culled; // S5: culled chunks stay off until they come within range
                            c.MaterialFrame = frame;
                        }
                    }
                    if (c.Slot != null && c.Slot.Set != null && c.Slot.AppliedVersion != c.Slot.Set.Version) Marmoset.ApplyMaps(c.Slot);
                    if (_mutants.M3 && !c.FreshBlockSet && c.Slot != null && frame - c.CreatedFrame >= 2)
                    {
                        // S3.M3: the mistake the plugin must never make: a fresh property block after the sky was applied
                        c.Renderer.SetPropertyBlock(new MaterialPropertyBlock());
                        c.FreshBlockSet = true;
                        DeLog.Warn("DE.Mirror MUTANT S3.M3: no SkyApplier and a fresh MaterialPropertyBlock on " + c.Go.name);
                    }
                    // S3.C4 "SkyApplier present and its sky non-null after 2 frames": frames counted once the Frame is active
 // (a SkyApplier on an inactive object has not started)
                    if (c.Slot != null && !c.C4Logged && v.HasPose && frame - Math.Max(c.CreatedFrame, v.ActiveFrame) >= 2 && frame - c.MaterialFrame >= 2)
                    {
                        c.C4Logged = true;
                        LogC4(v, c, "assigned");
                    }
                    if (c.Slot != null && c.C4Logged && !c.C4FinalLogged && c.Slot.Set != null &&
                        (c.Slot.Set.ConvertState == TextureSet.Conv.Ready || c.Slot.Set.ConvertState == TextureSet.Conv.Failed || c.Slot.Set.ConvertState == TextureSet.Conv.Off) &&
                        c.Slot.AppliedVersion == c.Slot.Set.Version)
                    {
                        c.C4FinalLogged = true;
                        LogC4(v, c, "final");
                    }
                }
            }
        }

        // S10 hook (round 2 A): the slot of a (material, colour) pair without building its string id; emptied wherever _slots
 // loses an entry (the three clears and the MaterialDef swap), filled by Slot
        private readonly Dictionary<KeyValuePair<ulong, long>, MaterialSlot> _s10SlotFast = new Dictionary<KeyValuePair<ulong, long>, MaterialSlot>();

        private MaterialSlot Slot(ChunkKey key)
        {
            MaterialSlot s;
            var fast = new KeyValuePair<ulong, long>(key.MaterialKey, key.ColourKey);
            if (S10Sn.EditsOn && _s10SlotFast.TryGetValue(fast, out s)) return s;
            s = SlotSlow(key);
            if (S10Sn.EditsOn && s != null) _s10SlotFast[fast] = s;
            return s;
        }

        private MaterialSlot SlotSlow(ChunkKey key)
        {
            MaterialSlot s;
            if (_slots.TryGetValue(SlotId(key.MaterialKey, key.ColourKey), out s)) return s;
            float h = 0, sat = 0, val = 0;
            foreach (MirrorGridView v in _views.Values)
                foreach (ChunkView c in v.Chunks.Values)
                    if (c.Key.ColourKey == key.ColourKey) { GetColour(v.Id, c.Key, out h, out sat, out val); goto Found; }
            Found:
            return SlotFor(key.MaterialKey, key.ColourKey, h, sat, val);
        }

        private static string SlotId(ulong materialKey, long colourKey)
        {
            return materialKey.ToString("x", CultureInfo.InvariantCulture) + "/" + colourKey.ToString("x", CultureInfo.InvariantCulture);
        }

        /// <summary>
 /// The material of an SE material in one colour (shared by chunks, S5 children and the character); null while its
 /// MaterialDef is missing. S5: glass and alpha-masked materials get their style (<see cref="S5Mirror.StyleFor"/>).
 /// </summary>
        internal MaterialSlot SlotFor(ulong materialKey, long colourKey, float h, float sat, float val)
        {
            if (materialKey == ImpostorRules.MaterialKey) return S7tImpostorSlot(colourKey, h, sat, val); // S7t-sn-altitude (T3): flat, no textures
            string id = SlotId(materialKey, colourKey);
            MaterialSlot s;
            if (_slots.TryGetValue(id, out s)) return s;
            if (!Marmoset.Ready) return null;
            MaterialDefData def = null;
            if (materialKey != 0 && !_materials.TryGetValue(materialKey, out def)) return null; // waits for its MaterialDef
            s = new MaterialSlot { MaterialKey = materialKey, ColourKey = colourKey, H = h, S = sat, V = val, Label = id };
            s.Mat = Marmoset.Create(s, Owned);
            bool glass = false;
            try { s.Mat = S5.StyleFor(s.Mat, def, Owned, out glass); }
            catch (Exception e) { DeLog.WarnOnce("s5.style", "DE.S5 material style not applied (" + e.GetType().Name + ": " + e.Message + "): opaque from now on"); }
            if (def != null && !glass) s.Set = Textures.Request(def);
            Marmoset.ApplyMaps(s);
            _slots[id] = s;
            return s;
        }

        private void GetColour(ulong gridId, ChunkKey key, out float h, out float s, out float v)
        {
            h = s = v = 0;
            GridRecord g;
            if (!State.Grids.TryGetValue(gridId, out g)) return;
            foreach (PartRecord p in g.Parts.Values)
                if (p.Cell.Equals(key.Cell) && p.ColourKey == key.ColourKey) { h = p.H; s = p.S; v = p.V; return; }
        }

        /// <summary>The S3.C4 line; a failure to describe (a missing game member) is logged once and never stops the mirror.</summary>
        private void LogC4(MirrorGridView v, ChunkView c, string when)
        {
            if (!S10Sn.TakeMirrorLine()) return; // S10 hook (round 2 A): per-chunk material lines under a per-second budget
            try { DeLog.Info(C4Line(v, c, when)); }
            catch (Exception e) { DeLog.WarnOnce("s3.c4line", "DE.Mirror material line not written: " + e.GetType().Name + ": " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string C4Line(MirrorGridView v, ChunkView c, string when)
        {
            string sky = "none", env = "none";
            bool hasSky = c.Sky != null;
            if (hasSky)
            {
                try
                {
                    sky = c.Sky.InspectorApplySky != null ? c.Sky.InspectorApplySky.name : "null";
                    env = c.Sky.InspectorEnvironmentSky != null ? c.Sky.InspectorEnvironmentSky.name : "null";
                }
                catch (Exception e) { sky = "error " + e.GetType().Name; }
            }
            return "DE.Mirror material " + when + " grid=" + DeLog.Hex(v.Id) + " " + c.Key + " " + MarmosetMaterials.Describe(c.Slot.Mat) +
                   " planned=[" + c.Slot.PlannedKeywords + "] layer=" + c.Go.layer + " shadows=" + c.Renderer.shadowCastingMode +
                   " skyApplier=" + hasSky + " applySky=" + sky + " environmentSky=" + env + " framesSinceCreate=" + (Time.frameCount - c.CreatedFrame) +
                   " maps{" + (c.Slot.Set != null ? c.Slot.Set.Describe() : "no MaterialDef (material 0)") + "}" +
                   (_mutants.M3 ? " (MUTANT S3.M3)" : "") + " (S3.C4)";
        }

        // ------------------------------------------------------------------------------------------ poses

 /// <summary>The camera driver placed the camera for this sample (LateUpdateLast, then onPreCull): pose the mirrors the same way.</summary>
        public void OnCameraSample(CamSample s, bool preCull)
        {
            _lastCameraFrame = Time.frameCount;
            if (!_worldReady) return;
            CamBracket br = CamBracket.From(s);
            if (br.Valid && !S8aMutants.M7 && S8aTerrain.ChartChangedBetween(br.Older, br.Newer)) S8aTerrain.ChartSnapRow(s, br, preCull);   //  the C1 camera frames across the change
            ApplyPoses(br, preCull);
            Ghost.OnCameraSample(br, preCull); // S7a: the ghost's newest C2 flags before the render, its presented ticks
 // S5: the character follows the same sample (C1 polled, bones posed once per sample), then the edits that
 // became visible are logged with this frame's presented tick, and the coherence probe after the final placement
            Character.OnCameraSample(br, preCull);
            Action<CamBracket, bool> s6 = S6CameraSample;
            if (s6 != null) s6(br, preCull);
            if (preCull)
            {
                S5.FlushEdits(br);
                S5.CohRow(br);
            }
        }

        public void ApplyPoses(CamBracket br, bool countFrame)
        {
            // CONTRACT §5.1 C2 pair rule: C2's tick must equal the camera's C1 tick, re-read up to 4 times; SE writes C2 before C1, so the first read normally has it
            if (br.Valid && _views.Count > 0) C2.PollPair(br.NewestTick > br.Newer ? br.NewestTick : br.Newer);
            else C2.Poll();
            // S8a (B4 hook, CONTRACT "chart.apply"): a chart change inside the bracket: every pose snaps to the newer tick
 // (no interpolation between two charts); S8a.M7 keeps interpolating
            if (br.Valid && !S8aMutants.M7 && S8aTerrain.ChartChangedBetween(br.Older, br.Newer)) { br = br.Locked(); S8aTerrain.SnapFrames++; }
            GroupRoot = 0;
            // S5 review F1 (camera-group lock): the root whose group holds the camera is posed at the camera orientation's tick
 // (the newest snapshot; with S5.M1 the one before it) and shifted by the camera's interpolation offset, so the
 // camera and its ride share one tick (ship-render 6.7 #2); every other root keeps the bracket interpolation
            EntitySnapshot groupSnap = null;
            if (br.Valid && br.NewestTick != 0) groupSnap = (_mutants.M4 || _s5m1) ? C2.History.Before(br.NewestTick) : C2.History.Exactly(br.NewestTick);
            // S3.M4 and S5.M1: roots (and, S5, the C2 rows) from the snapshot before the bracket's newer tick
            bool prev = _mutants.M4 || _s5m1;
            try { Camera cam = MainCamera.camera; if (cam != null) S5.NoteCamera(cam.transform.position); } catch (Exception) { }
            foreach (MirrorGridView v in _views.Values)
            {
                if (!v.Alive || v.IsSubgrid) continue; // S5: subgrids are posed from their C2 row below
                EntityPose p;
                ulong tick;
                PoseHow how;
                EntityPose gp;
                if (groupSnap != null && GroupRoot == 0 && groupSnap.TryGet(v.Id, out gp) && (gp.Flags & 2u) != 0 && gp.Finite)
                {
                    gp.Ox += br.DPx; gp.Oy += br.DPy; gp.Oz += br.DPz;
                    p = gp; tick = groupSnap.Tick; how = PoseHow.Exact;
                    GroupRoot = v.Id;
                    if (countFrame) GroupLocked++;
                }
                else if (!PoseSampler.Sample(C2.History, v.Id, br, prev, out p, out tick, out how))
                {
                    if (countFrame) PoseMissing++;
                    continue;
                }
                v.ApplyPose(p, tick, how, false);
                if (countFrame)
                {
                    PoseFrames++;
                    switch (how)
                    {
                        case PoseHow.Interpolated: PoseInterpolated++; break;
                        case PoseHow.Held: case PoseHow.Exact: PoseHeld++; break;
                        case PoseHow.Extrapolated: PoseExtrapolated++; break;
                        case PoseHow.Newest: PoseNewest++; break;
                        case PoseHow.Previous: PosePrevious++; break;
                    }
                }
                if (!v.PlacementLogged && v.LocalBounds(out Vec3 mn, out Vec3 mx))
                {
                    v.PlacementLogged = true;
                    if (S10Sn.TakeMirrorLine()) DeLog.Info(PlacementLine(v)); // S10 hook (round 2 A): under the per-second budget
                }
            }
            // S5: subgrid Frames and Dynamic children from C2's subpart table with the same bracket (and S5.M1)
            S5.PoseRows(br, prev, countFrame);
        }

        /// <summary>
 /// S3.C2 evidence: 8 drawn vertices, the most extreme along each corner direction
 /// (corner i: bit 0 +x, bit 1 +y, bit 2 +z), with their SE-local positions computed independently of the mesh
 /// build pass (seLocalCorners) and their uploaded positions mapped to SN world by Unity (the chunk renderer's
 /// localToWorldMatrix: cornersSn), the pose and SE tick used, and the largest difference against the emulated
 /// Apply(o, q, s, k·l) of the SE-local positions. For a box the 8 vertices are its corners (seLocalMin/Max).
 /// </summary>
        public string PlacementLine(MirrorGridView v)
        {
            Vec3[] seLocal, world;
            double dev;
            if (!Placement(v, out seLocal, out world, out dev)) return "DE.Mirror placed entity=" + DeLog.Hex(v.Id) + " (nothing built yet)";
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024);
            Vec3 mn = seLocal[0], mx = seLocal[0];
            foreach (Vec3 l in seLocal)
            {
                mn = new Vec3(Math.Min(mn.X, l.X), Math.Min(mn.Y, l.Y), Math.Min(mn.Z, l.Z));
                mx = new Vec3(Math.Max(mx.X, l.X), Math.Max(mx.Y, l.Y), Math.Max(mx.Z, l.Z));
            }
            sb.Append("DE.Mirror placed entity=").Append(DeLog.Hex(v.Id)).Append(" tick=").Append(v.LastTick.ToString(ci)).Append(" how=").Append(v.LastHow)
              .Append(' ').Append(v.LastPose.Describe())
              .Append(" seLocalMin=").Append(V3(mn)).Append(" seLocalMax=").Append(V3(mx)).Append(" seLocalCorners=");
            for (int i = 0; i < 8; i++) { if (i > 0) sb.Append(';'); sb.Append(V3(seLocal[i])); }
            sb.Append(" cornersSn=");
            for (int i = 0; i < 8; i++) { if (i > 0) sb.Append(';'); sb.Append(V3(world[i])); }
            sb.Append(" maxDevVsApplyM=").Append(dev.ToString("0.######", ci))
              .Append(" (S3.C2: drawn vertices; Python recomputes Apply from SE's matrix and P*)");
            return sb.ToString();
        }

        private static string V3(Vec3 v)
        {
            var ci = CultureInfo.InvariantCulture;
            return v.X.ToString("R", ci) + "," + v.Y.ToString("R", ci) + "," + v.Z.ToString("R", ci);
        }

        /// <summary>
 /// The 8 drawn probe vertices: their independent SE-local positions and their SN world positions as Unity draws
 /// them (false when nothing is built); <paramref name="maxDev"/> = the largest |drawn − Apply(o, q, s, k·l)|.
 /// </summary>
        public bool Placement(MirrorGridView v, out Vec3[] seLocal, out Vec3[] world, out double maxDev)
        {
            seLocal = new Vec3[8];
            world = new Vec3[8];
            maxDev = double.NaN;
            if (!v.Alive || !v.HasPose || !v.DrawnCorners(seLocal, world)) { seLocal = world = null; return false; }
            double worst = 0;
            for (int i = 0; i < 8; i++)
            {
                double d = (world[i] - MirrorPoseMath.ApplySeLocal(v.LastPose, seLocal[i])).Length;
                if (d > worst) worst = d;
            }
            maxDev = worst;
            return true;
        }

        /// <summary>World AABB centre and corners of an entity (the lab's block centre B).</summary>
        public bool Centre(MirrorGridView v, out Vec3 centre, out Vec3[] corners)
        {
            Vec3[] seLocal;
            double dev;
            centre = Vec3.Zero;
            if (!Placement(v, out seLocal, out corners, out dev)) return false;
            Vec3 sum = Vec3.Zero;
            foreach (Vec3 c in corners) sum = sum + c;
            centre = sum / 8.0;
            return true;
        }

        // ------------------------------------------------------------------------------------------ C7 and status

 /// <summary>C7 counters (CONTRACT §5.4; S0 stand-in semantics).</summary>
        public void FillStatus(ref SnStatus s)
        {
            // S5 (CONTRACT §5.4 "C7 counters (S5)"): entities known by GridAdd plus the character known by CharacterAdd
            s.mirroredEntityCount = (uint)(State.Grids.Count + State.Characters.Count);
            s.pendingParts = (uint)PendingPartsCached();
            s.c3BytesDrained = (ulong)Assembler.Bytes;
        }

        /// <summary>
 /// <see cref="PendingParts"/> recomputed only after a C4 change, a model, material or bind event (S5 at scale: the
 /// status is written every frame and a 5,000-block ship has tens of thousands of parts).
 /// </summary>
        private int PendingPartsCached()
        {
            long serial = State.ChangeSerial * 1000003L + _modelSerial * 31L + (State.LostSinceClearAll ? 1 : 0);
            if (serial == _pendingSerial) return _pendingCached;
            _pendingSerial = serial;
            _pendingCached = PendingParts();
            return _pendingCached;
        }

        /// <summary>
 /// Parts whose model has not arrived (ModelEnd) or whose submesh materials are not all defined (the keys come from
 /// the payload's submesh table at ModelEnd, so a model not yet prepared is judged too); orphans included; plus 1
 /// while records lost to a corrupt-framing re-sync have not been replaced by SE's ClearAll and resend.
 /// </summary>
        public int PendingParts()
        {
            int n = 0;
            foreach (GridRecord g in State.Grids.Values)
                foreach (PartRecord p in g.Parts.Values)
                    if (PartPending(p)) n++;
            foreach (Dictionary<ulong, PartRecord> o in State.Orphans.Values) n += o.Count;
            if (State.LostSinceClearAll) n++;
            // S5: character parts (model or materials incomplete, or a skinned model still without its SkinnedBind)
            n += Character.Pending(_pendingPredicate ?? (_pendingPredicate = PartPending));
            return n;
        }

        private Func<PartRecord, bool> _pendingPredicate;

        private bool PartPending(PartRecord p)
        {
            ModelEntry e;
            if (!_models.TryGetValue(p.ModelKey, out e)) return true;
            if (e.Synthetic || e.Failed) return false;
            ulong[] keys = e.MaterialKeys;
            if (keys == null && e.Prepared != null && e.Prepared.Submeshes != null)
            {
                foreach (SubmeshInfo sm in e.Prepared.Submeshes)
                    if (sm.MaterialKey != 0 && !_materials.ContainsKey(sm.MaterialKey)) return true;
                return false;
            }
            if (keys != null)
                foreach (ulong k in keys)
                    if (k != 0 && !_materials.ContainsKey(k)) return true;
            return false;
        }

        public string StatsLine()
        {
            var ci = CultureInfo.InvariantCulture;
            return "DE.Mirror stats grids=" + State.Grids.Count + " parts=" + State.PartCount + " pending=" + PendingParts() + " models=" + _models.Count +
                   " materials=" + _materials.Count + " views=" + _views.Count + " c3=msgs:" + C3Messages + ",bytes:" + Assembler.Bytes + ",misparse:" + Assembler.Misparse +
                   ",orphans:" + Assembler.Orphans + ",synthetic:" + Assembler.Synthetic + ",hashMismatch:" + Assembler.HashMismatch + ",corrupt:" + C3Corrupt +
                   " c4=msgs:" + C4Messages + ",misparse:" + State.Misparse +
                   ",ignored:" + State.Ignored + ",epochDropped:" + State.EpochDropped + ",corrupt:" + C4Corrupt + " lost=" + (State.LostSinceClearAll ? "yes" : "no") +
                   "(" + State.Losses + ") drainMsMax=" + DrainMsMax.ToString("0.###", ci) +
                   " c2=fresh:" + C2.Fresh + ",busy:" + C2.Busy + ",invalid:" + C2.Invalid + ",history:" + C2.History.Count + ",incoherent:" + C2.Incoherent +
                   ",pairRereads:" + C2.PairRereads +
                   " poses=frames:" + PoseFrames + ",interp:" + PoseInterpolated + ",held:" + PoseHeld + ",extrap:" + PoseExtrapolated + ",newest:" + PoseNewest +
                   ",previous:" + PosePrevious + ",missing:" + PoseMissing + " builds=" + CellBuilds + " chunkUploads=" + ChunkUploads + " uploads{" + Uploads.Describe() + "}" +
                   " worker=queued:" + Worker.Queued + ",done:" + Worker.Done + ",failed:" + Worker.Failed + " textures{" + Textures.Describe() + "}" +
                   " shader=" + Marmoset.State + " meshesAlive=" + Owned.MeshesAlive + " world=" + (_worldReady ? "up" : "down") + (_suppressed ? " SUPPRESSED" : "") +
                   " mutants=" + _mutants.Describe() + (_s5m1 ? ",S5.M1" : "") + (_s5m2 ? ",S5.M2" : "") +
                   " s5{characters=" + State.Characters.Count + " binds=" + _binds.Count + " rejected=" + State.ConventionsRejected + " builds=inFlight:" + BuildQueue.InFlight +
                   ",capped:" + BuildQueue.Capped + ",skippedInFlight:" + BuildQueue.SkippedInFlight + " drain=deferred:" + Gate.DeferredFrames + ",forced:" + Gate.ForcedFrames +
                   " c2rows=" + (C2.History.Newest != null ? C2.History.Newest.RowCount : 0) + ",invalid:" + C2.RowsInvalid + " " + S5.Describe() +
                   " char=" + CharSourceRules.Name(Character.Source) + "/" + Character.LastWhy + "}";
        }

        public void Status(JsonObjectWriter w)
        {
            w.Num("grids", (long)State.Grids.Count).Num("parts", (long)State.PartCount).Num("pendingParts", (long)PendingParts()).Num("models", (long)_models.Count)
             .Num("materialDefs", (long)_materials.Count).Num("views", (long)_views.Count).Num("c3Bytes", Assembler.Bytes).Num("c3Messages", C3Messages)
             .Num("c3Misparse", Assembler.Misparse).Num("c3HashMismatch", Assembler.HashMismatch).Num("c3Synthetic", Assembler.Synthetic)
             .Num("c4Messages", C4Messages).Num("c4Misparse", State.Misparse).Num("c2Fresh", C2.Fresh).Num("c2History", (long)C2.History.Count)
             .Num("c2Incoherent", C2.Incoherent).Num("c3Corrupt", C3Corrupt).Num("c4Corrupt", C4Corrupt).Bool("recordsLost", State.LostSinceClearAll)
             .Num("poseFrames", PoseFrames).Num("posePrevious", PosePrevious).Num("poseMissing", PoseMissing)
             .Num("cellBuilds", CellBuilds).Num("chunkUploads", ChunkUploads).Num("uploadOverBudget", Uploads.OverBudgetFrames).Num("uploadMaxMs", Uploads.MaxFrameMs)
             .Str("shader", Marmoset.State).Str("textures", Textures.Describe()).Num("meshesAlive", (long)Owned.MeshesAlive).Num("othersAlive", (long)Owned.OthersAlive)
             .Bool("worldUp", _worldReady).Bool("suppressed", _suppressed).Bool("hidden", _hidden).Str("options", Options.Describe()).Str("mutants", _mutants.Describe())
             .Str("lastC3Error", LastC3Error ?? "").Str("lastC4Error", LastC4Error ?? "");
            // S5
            w.Num("characters", (long)State.Characters.Count).Num("skinnedBinds", (long)_binds.Count).Num("conventionsRejected", State.ConventionsRejected)
             .Num("buildsInFlight", (long)BuildQueue.InFlight).Num("buildsCapped", BuildQueue.Capped).Num("drainDeferred", Gate.DeferredFrames).Num("drainForced", Gate.ForcedFrames)
             .Num("c2Rows", (long)(C2.History.Newest != null ? C2.History.Newest.RowCount : 0)).Num("c2RowsInvalid", C2.RowsInvalid).Str("s5", S5.Describe())
             .Bool("s5M1", _s5m1).Bool("s5M2", _s5m2);
            Character.Status(w);
            var sb = new StringBuilder();
            foreach (ModelEntry e in _models.Values)
            {
                if (sb.Length > 0) sb.Append(" | ");
                if (e.Synthetic) sb.Append(DeLog.Hex(e.Key)).Append(" synthetic");
                else if (e.Failed) sb.Append(DeLog.Hex(e.Key)).Append(" refused: ").Append(e.Error);
                else if (e.Prepared != null) sb.Append(e.Prepared.Describe());
                else sb.Append(DeLog.Hex(e.Key)).Append(" preparing");
                if (sb.Length > 3000) { sb.Append(" ..."); break; }
            }
            w.Str("modelList", sb.ToString());
        }

        public void Dispose()
        {
            try { Worker.Dispose(); } catch (Exception) { }
            try { TearDownUnity("bridge stopped"); } catch (Exception) { }
            try { Character.TearDown("bridge stopped"); } catch (Exception) { }
            try { S7p.Dispose(); } catch (Exception) { } // S7p-scene-above: its C2 reader off the extension list
        }
    }
}
