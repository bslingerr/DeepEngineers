using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>One renderer of a Child part (one shared mesh per material group).</summary>
    internal sealed class ChildRenderer
    {
        public GameObject Go;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public SkyApplier Sky;
        public ulong MaterialKey;
        public MaterialSlot Slot;
    }

    /// <summary>
 /// S5: a Child part (CONTRACT §5.4 "Chunk key and consumer rules": Dynamic subparts, tools and floating objects are

 /// PartAdd matrix, or every frame by its C2 row once Dynamic.
 /// </summary>
    internal sealed class ChildPartView
    {
        public ulong OwnerId, PartKey, ModelKey;
        public uint Flags, Class;
        public long ColourKey;
        public float H, S, V;
        public ChildMeshKey MeshKey;
        public GameObject Go;
        public Transform T;
        public readonly List<ChildRenderer> Renderers = new List<ChildRenderer>();
        public readonly float[] StaticLocal = new float[12];
        public readonly float[] LastRow = new float[12];
        public bool RowSeen;
        public ulong LastTick;
        public double LastAlpha = 1;

        public bool Dynamic { get { return (Flags & S5Conv.Dynamic) != 0 || Class == S5Conv.ClassTool; } }
        /// <summary>: Unity's frame a preview was made (a placed block drawn alone until its cell is rebuilt); -1 for a child.</summary>
        public long PreviewFrame = -1;
    }

    /// <summary>The key of a shared Child mesh: the model and its pattern offset (the UVs carry it).</summary>
    internal struct ChildMeshKey : IEquatable<ChildMeshKey>
    {
        public readonly ulong ModelKey;
        public readonly long Uv;
        public ChildMeshKey(ulong model, float offU, float offV)
        {
            ModelKey = model;
            Uv = ((long)Math.Round(offU * 4096.0) << 32) ^ (long)(uint)Math.Round(offV * 4096.0);
        }
        public bool Equals(ChildMeshKey o) { return ModelKey == o.ModelKey && Uv == o.Uv; }
        public override bool Equals(object obj) { return obj is ChildMeshKey && Equals((ChildMeshKey)obj); }
        public override int GetHashCode() { return ModelKey.GetHashCode() ^ Uv.GetHashCode() * 31; }
    }

    internal sealed class SharedModelMesh
    {
        public ChildMeshKey Key;
        public bool Building, Ready, Failed;
        public int Version;
        public readonly List<KeyValuePair<ulong, Mesh>> Meshes = new List<KeyValuePair<ulong, Mesh>>();
        public int Vertices, Triangles, Users;
        public long Bytes;
        /// <summary>Seconds in a row without a user (destroyed after <see cref="S5Mirror.SharedIdleSeconds"/>).</summary>
        public int IdleSeconds;
    }

    internal sealed class SharedMeshResult
    {
        public ChildMeshKey Key;
        public int Version;
        public CellBuildResult Build;
    }

    /// <summary>An s5.edit waiting until its cells are rebuilt and drawn (live edits on drawn grids only).</summary>
    internal sealed class PendingEdit
    {
        public ulong Entity, PartKey;
        public uint Type;
        public long Frame;
        public readonly List<CellKey> Cells = new List<CellKey>(2);
        public bool Child;
        public long VisibleFrame = -1;
        /// <summary>Unity's frame when the record was drained (the age limit of pending edits).</summary>
        public long UnityFrame;
    }

    /// <summary>
 /// The S5 extensions of the grid mirror (ROADMAP S5 SN plugin; CONTRACT §5.4 "C1–C4 conventions for S5", §2.8;
 /// ARCHITECTURE §5.8 "S5 refinements"), owned by <see cref="MirrorConsumer"/>:
 /// <list type="bullet">
 /// <item>Child parts (Dynamic subparts, floating objects, the held tool): their own GameObjects with meshes shared per
 /// model, built on the mirror worker, uploaded within the upload budget.</item>
 /// <item>Subgrids (D8): views parented under their physical-hierarchy root's Entity, posed from C2's subpart table at
 /// the camera's bracket (T lerp + slerp in SE space, mirrored once), hidden until their first row; a dropped row holds
 /// the last pose. S5.M1 samples rows one snapshot behind.</item>
 /// <item>Culling at the water visibility fallback (200 m), glass and alpha-masked material styles, the s5.* rows
 /// (grid, placed, edit, sub, coh, scale) and the per-second scale statistics.</item>
 /// </list>
 /// Every step runs in its own guard: an exception logs <c>DE.S5 FEATURE … DISABLED</c> once and stops that step only.
 /// </summary>
    internal sealed class S5Mirror
    {
        public const double CullDistanceM = 200.0;
        public const int SampleParts = 20;
        public const int MaxSubRowsPerFrame = 64;
        /// <summary>A shared Child mesh without users for this long is destroyed (its model may come back: built again).</summary>
        public const int SharedIdleSeconds = 60;

        private readonly MirrorConsumer _m;
        private readonly Dictionary<ChildMeshKey, SharedModelMesh> _shared = new Dictionary<ChildMeshKey, SharedModelMesh>();
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<PendingEdit> _edits = new List<PendingEdit>();
        private readonly List<PendingEdit> _visible = new List<PendingEdit>();
        private readonly Dictionary<ulong, int> _gridLoggedVersion = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> _placedLogged = new HashSet<ulong>();
        private readonly FrameStats _frames = new FrameStats();
        private int _sharedSerial;
        private ulong _nextSecondMs;
        private double _uploadMsMax;
        private Vector3 _camPos;
        private bool _camKnown;

        // sessions (de.s5 sub / coh)
        private ulong _subUntilMs, _cohUntilMs;
        private ulong _cohEntity;
        private Vec3 _cohLocal;
        private ulong _now;

        public Material GlassSource;
        public string GlassSourceName;
        private bool _glassMissingLogged;

        public long ChildrenCreated, ChildrenDestroyed, SharedBuilt, SharedFailed, RowsApplied, RowsHeld, RowsMissing, SubgridsShown, EditsLogged,
                    SubRows, CohRows, Culled, ScaleRows, PlacedRows, GridRowsWritten;

        // per-frame steps as cached delegates (no closure allocated per frame)
        private readonly Action _stepHierarchy, _stepChildren, _stepChildMaterials, _stepCull, _stepSecond, _stepPreview;

        public S5Mirror(MirrorConsumer m)
        {
            _m = m;
            _stepHierarchy = SyncHierarchy;
            _stepChildren = SyncChildren;
            _stepPreview = PreviewStep;
            _stepChildMaterials = UpdateChildMaterials;
            _stepCull = Cull;
            _stepSecond = Second;
        }

        public bool Disabled(string feature) { return _disabled.Contains(feature); }
        public string DisabledList { get { return _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()); } }

        private bool Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return false;
            try { a(); return true; }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S5 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the mirror keeps running)\n" + e);
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------ per frame (Update)

 /// <summary>Main thread, from the consumer's Tick while the world is up: hierarchy, children, materials, statistics.</summary>
        public void Tick(ulong nowMs, double frameMs)
        {
            _now = nowMs;
            _frames.Add(frameMs);
            if (_m.Uploads.LastFrameMs > _uploadMsMax) _uploadMsMax = _m.Uploads.LastFrameMs;
            Guard("hierarchy", _stepHierarchy);
            Guard("children", _stepChildren);
            Guard("preview", _stepPreview);
            Guard("childMaterials", _stepChildMaterials);
            if (nowMs >= _nextSecondMs)
            {
                _nextSecondMs = nowMs + 1000;
                Guard("cull", _stepCull);
                Guard("rows", _stepSecond);
            }
        }

        /// <summary>Subgrid views follow their root's Entity (created on demand); a re-parent moves the view and hides it until its next row.</summary>
        private void SyncHierarchy()
        {
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                if (!g.IsSubgrid && !g.ParentChanged) continue;
                MirrorGridView v;
                if (!_m.ViewMap.TryGetValue(g.Id, out v) || !v.Alive)
                {
                    if (!g.IsSubgrid) { g.ParentChanged = false; continue; }
                    if (g.Parts.Count == 0) continue;
                    v = _m.EnsureViewInternal(g.Id, _now);
                }
                if (g.ParentChanged)
                {
                    g.ParentChanged = false;
                    if (v.HasPose && g.IsSubgrid) { v.HasPose = false; v.Frame.SetActive(false); } // hidden until its next row
                    if (!g.IsSubgrid && v.IsSubgrid)
                    {
                        v.IsSubgrid = false;
                        v.ParentId = 0;
                        v.AttachTo(null);
                        v.HasPose = false;
                        v.Frame.SetActive(false);
                    }
                    DeLog.Info("DE.S5 grid " + DeLog.Hex(g.Id) + " re-parented to " + DeLog.Hex(g.ParentId) + " (GridAdd again; meshes kept)");
                }
                if (!g.IsSubgrid) continue;
                v.IsSubgrid = true;
                v.ParentId = g.ParentId;
                MirrorGridView root;
                if (!_m.ViewMap.TryGetValue(g.ParentId, out root) || !root.Alive) root = _m.EnsureViewInternal(g.ParentId, _now);
                v.AttachTo(root.EntityT);
            }
        }

        /// <summary>A root view is about to be destroyed: its subgrid Frames are detached (hidden) so they survive and re-attach later.</summary>
        public void OnViewRemoving(MirrorGridView root)
        {
            try { OnViewRemovingCore(root); }
            catch (Exception e) { DeLog.WarnOnce("s5.viewremoving", "DE.S5 subgrid detach failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private void OnViewRemovingCore(MirrorGridView root)
        {
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive || !v.IsSubgrid || v.ParentId != root.Id) continue;
                v.AttachTo(null);
                v.HasPose = false;
                v.Frame.SetActive(false);
            }
            foreach (ChildPartView c in root.Children.Values) Release(c);
            RemovePreviews(root.Id);
        }

        // ------------------------------------------------------------------------------------------ the instant preview

 /// <summary>
 /// laptop: an edit took 6 frames at the median, up
 /// to 87, to show through its cell's rebuild and upload): a block added to a drawn grid is drawn at once on its own, with
 /// the shared mesh of its model (the Child machinery), under the grid's entity at its part matrix, until the rebuilt
 /// cell holding it is uploaded (the edit visible), the part goes, or PreviewMaxFrames pass.
 /// DEEPENGINEERS_SN_PREVIEW=off: none.
 /// </summary>
        public static bool PreviewOn = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_SN_PREVIEW"), "off", StringComparison.OrdinalIgnoreCase);
        public const int PreviewMaxFrames = 600, PreviewWaitFrames = 120, PreviewMax = 256;
        private readonly Dictionary<ulong, Dictionary<ulong, ChildPartView>> _previews = new Dictionary<ulong, Dictionary<ulong, ChildPartView>>();
        private readonly List<KeyValuePair<ulong, ulong>> _previewWant = new List<KeyValuePair<ulong, ulong>>();
        private readonly List<long> _previewWantFrame = new List<long>();
        private int _previewCount;
        public long PreviewsMade, PreviewsReplaced, PreviewsExpired, PreviewsMissed;

        /// <summary>A pending edit covers this cell (its rebuild and upload jump the queues).</summary>
        public bool HasPendingEdit(ulong grid, CellKey cell)
        {
            for (int i = 0; i < _edits.Count; i++)
            {
                PendingEdit e = _edits[i];
                if (e.Entity == grid && e.Cells.Contains(cell)) return true;
            }
            return false;
        }

        private void WantPreview(ulong grid, ulong part)
        {
            if (!PreviewOn || Disabled("preview")) return;
            if (TryPreview(grid, part)) return;
            if (_previewWant.Count >= PreviewMax) return;
            _previewWant.Add(new KeyValuePair<ulong, ulong>(grid, part));
            _previewWantFrame.Add(Time.frameCount);
        }

        /// <summary>True when made (or nothing to make); false while its shared mesh is building.</summary>
        private bool TryPreview(ulong grid, ulong part)
        {
            GridRecord g;
            MirrorGridView v;
            PartRecord p;
            if (!_m.State.Grids.TryGetValue(grid, out g) || !_m.ViewMap.TryGetValue(grid, out v) || !v.Alive || !v.HasPose) return true;
            if (_m.S7tCoarse.Contains(grid) || !g.Parts.TryGetValue(part, out p) || !p.Chunked) return true;
            // (log: 1,412 previews made, 1,907 missed in one session): a grid streaming in adds every part too; only a
 // part added to a cell already drawn is an edit worth bridging
            if (!v.HasCellChunks(p.Cell)) return true;
            if (!HasPendingEditPart(grid, part)) return true;   // its cell was uploaded meanwhile: nothing to bridge
            if (_previewCount >= PreviewMax) { PreviewsMissed++; return true; }
            ModelEntry e;
            if (_m.ModelMap.TryGetValue(p.ModelKey, out e) && (e.Failed || e.Synthetic)) return true;
            SharedModelMesh sm = Shared(p, true);
            if (sm != null && sm.Failed) return true;
            if (sm == null || !sm.Ready) return false;
            Dictionary<ulong, ChildPartView> d;
            if (!_previews.TryGetValue(grid, out d)) { d = new Dictionary<ulong, ChildPartView>(); _previews[grid] = d; }
            ChildPartView old;
            if (d.TryGetValue(part, out old)) { DestroyChild(old); _previewCount--; PreviewsReplaced++; }
            ChildPartView c = CreateChild(v.EntityT, grid, p, sm);
            c.PreviewFrame = Time.frameCount;
            c.Go.name = "DE.S5.Preview." + part.ToString("x", CultureInfo.InvariantCulture);
            if (_m.Marmoset.Ready) AssignMaterials(c);
            d[part] = c;
            _previewCount++;
            PreviewsMade++;
            return true;
        }

        private bool HasPendingEditPart(ulong grid, ulong part)
        {
            for (int i = 0; i < _edits.Count; i++) if (_edits[i].Entity == grid && _edits[i].PartKey == part && _edits[i].Cells.Count > 0) return true;
            return false;
        }

        private void RemovePreview(ulong grid, ulong part)
        {
            Dictionary<ulong, ChildPartView> d;
            ChildPartView c;
            if (!_previews.TryGetValue(grid, out d) || !d.TryGetValue(part, out c)) return;
            d.Remove(part);
            if (d.Count == 0) _previews.Remove(grid);
            DestroyChild(c);
            _previewCount--;
        }

        private void RemovePreviews(ulong grid)
        {
            Dictionary<ulong, ChildPartView> d;
            if (!_previews.TryGetValue(grid, out d)) return;
            foreach (ChildPartView c in d.Values) { DestroyChild(c); _previewCount--; }
            _previews.Remove(grid);
        }

        /// <summary>Every frame: the previews waiting for their shared mesh, the materials of new ones, the age limit.</summary>
        private void PreviewStep()
        {
            for (int i = _previewWant.Count - 1; i >= 0; i--)
            {
                bool done = TryPreview(_previewWant[i].Key, _previewWant[i].Value);
                if (!done && Time.frameCount - _previewWantFrame[i] > PreviewWaitFrames) { done = true; PreviewsMissed++; }
                if (done) { _previewWant.RemoveAt(i); _previewWantFrame.RemoveAt(i); }
            }
            if (_previewCount == 0) return;
            List<KeyValuePair<ulong, ulong>> old = null;
            foreach (KeyValuePair<ulong, Dictionary<ulong, ChildPartView>> kv in _previews)
            {
                MirrorGridView v;
                bool alive = _m.ViewMap.TryGetValue(kv.Key, out v) && v.Alive;
                foreach (KeyValuePair<ulong, ChildPartView> c in kv.Value)
                {
                    if (!alive || Time.frameCount - c.Value.PreviewFrame > PreviewMaxFrames) (old = old ?? new List<KeyValuePair<ulong, ulong>>()).Add(new KeyValuePair<ulong, ulong>(kv.Key, c.Key));
                    else if (_m.Marmoset.Ready) AssignMaterials(c.Value);
                }
            }
            if (old != null) foreach (KeyValuePair<ulong, ulong> k in old) { RemovePreview(k.Key, k.Value); PreviewsExpired++; }
        }

        // ------------------------------------------------------------------------------------------ children

        private void SyncChildren()
        {
            // S7p-gameplay (P14): the children created this frame share one budget (S7pMirrorBudget: 2 ms, 48 children);
 // the rest wait for the next frame (the grid stays ChildrenDirty), counted as childrenDeferred
            _childStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _childCreated = 0;
            _childBudgetHit = false;
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                if (!g.ChildrenDirty) continue;
                MirrorGridView v;
                // S7t-sn-altitude (T3): an impostor has no children (subparts, doors, rotor tops): reconciled against no parts, they go
                Dictionary<ulong, PartRecord> parts = _m.S7tCoarse.Contains(g.Id) ? S7tImpostors.NoParts : g.Parts;
                bool hasChildParts = false;
                foreach (PartRecord p in parts.Values) if (p.Role == PartRole.Child) { hasChildParts = true; break; }
                if (!_m.ViewMap.TryGetValue(g.Id, out v) || !v.Alive)
                {
                    if (!hasChildParts) { g.ChildrenDirty = false; continue; }
                    v = _m.EnsureViewInternal(g.Id, _now);
                }
                if (parts != g.Parts) S7tImpostors.ChildrenRemoved += v.Children.Count; // S7t-sn-altitude (T3)
                g.ChildrenDirty = !ReconcileCore(v.EntityT, v.Children, g.Id, parts, true);
                if (_childBudgetHit) break; // S7p-gameplay (P14): the frame's child budget is spent: the other grids next frame
            }
            if (_childBudgetHit) ChildFramesDeferred++;
        }

        // S7p-gameplay (P14): the per-frame child creation budget and its counters (s7p.mirror row)
        private long _childStart;
        private int _childCreated;
        private bool _childBudgetHit;
        public long ChildFramesDeferred, ChildrenDeferred;
        /// <summary>Child parts waiting for creation at the last SyncChildren (the backlog the s7p.mirror row reports).</summary>
        public int ChildBacklog;

        /// <summary>
 /// Brings the children under <paramref name="parent"/> in line with the Child parts; false while some wait for
 /// their model or shared mesh (tried again next frame). Allocation-free unless children go.
 /// </summary>
        internal bool Reconcile(Transform parent, Dictionary<ulong, ChildPartView> children, ulong ownerId, Dictionary<ulong, PartRecord> parts)
        {
            return ReconcileCore(parent, children, ownerId, parts, false); // the character's held tool: never budgeted (one part)
        }

        private bool ReconcileCore(Transform parent, Dictionary<ulong, ChildPartView> children, ulong ownerId, Dictionary<ulong, PartRecord> parts, bool budgeted)
        {
            bool complete = true;
            List<ulong> gone = null;
            foreach (KeyValuePair<ulong, ChildPartView> kv in children)
            {
                PartRecord p;
                if (!parts.TryGetValue(kv.Key, out p) || p.Role != PartRole.Child || p.ModelKey != kv.Value.ModelKey ||
                    !new ChildMeshKey(p.ModelKey, p.OffU, p.OffV).Equals(kv.Value.MeshKey))
                    (gone = gone ?? new List<ulong>()).Add(kv.Key);
            }
            if (gone != null)
                foreach (ulong k in gone)
                {
                    ChildPartView c = children[k];
                    children.Remove(k);
                    DestroyChild(c);
                }
            foreach (PartRecord p in parts.Values)
            {
                if (p.Role != PartRole.Child) continue;
                ChildPartView c;
                if (children.TryGetValue(p.PartKey, out c))
                {
                    // replace in place: colour, flags and the static matrix may change
                    bool recolour = c.ColourKey != p.ColourKey;
                    c.Flags = p.Flags;
                    c.Class = p.Class;
                    c.ColourKey = p.ColourKey; c.H = p.H; c.S = p.S; c.V = p.V;
                    Array.Copy(p.Local, c.StaticLocal, 12);
                    if (recolour) foreach (ChildRenderer r in c.Renderers) { r.Slot = null; if (r.Renderer != null) r.Renderer.enabled = false; }
                    if (!c.Dynamic || !c.RowSeen) ApplyChildLocal(c, MirrorXform.UnityLocal(c.StaticLocal, 0));
                    NoteChildEditVisible(ownerId, p.PartKey);
                    continue;
                }
                ModelEntry e;
                if (_m.ModelMap.TryGetValue(p.ModelKey, out e) && (e.Failed || e.Synthetic)) continue; // refused model: not drawn, not waited for
                SharedModelMesh sm = Shared(p);
                if (sm != null && sm.Failed) continue; // logged once by OnSharedBuilt
                if (sm == null || !sm.Ready) { complete = false; continue; }
                if (budgeted && !S7pMirrorBudget.ChildAllowed(_childStart, System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency, _childCreated))
                {
                    // S7p-gameplay (P14): over this frame's child budget: created next frame (never a multi-second burst)
                    complete = false;
                    _childBudgetHit = true;
                    ChildrenDeferred++;
                    break; // the rest of this grid's parts next frame (no full part scan per frame once the budget is spent)
                }
                if (budgeted) _childCreated++;
                c = CreateChild(parent, ownerId, p, sm);
                children[p.PartKey] = c;
                NoteChildEditVisible(ownerId, p.PartKey);
            }
            return complete;
        }

        /// <summary>The shared mesh of a part's model (requested on the worker when missing; null while the model is not prepared).</summary>
        private SharedModelMesh Shared(PartRecord p, bool urgent = false)
        {
            var key = new ChildMeshKey(p.ModelKey, p.OffU, p.OffV);
            SharedModelMesh sm;
            if (_shared.TryGetValue(key, out sm) && (sm.Ready || sm.Building || sm.Failed)) return sm;
            ModelEntry e;
            if (!_m.ModelMap.TryGetValue(p.ModelKey, out e) || e.Prepared == null || e.Synthetic || e.Failed) return null;
            if (sm == null) { sm = new SharedModelMesh { Key = key }; _shared[key] = sm; }
            sm.Building = true;
            sm.Version = ++_sharedSerial;
            var part = new PartRecord { EntityId = 0, PartKey = 0, ModelKey = p.ModelKey, OffU = p.OffU, OffV = p.OffV, Role = PartRole.Chunk };
            part.Local[0] = 1; part.Local[4] = 1; part.Local[8] = 1;
            var input = new CellBuildInput { GridId = 0, Cell = default(CellKey), Version = sm.Version, Options = _m.Options.Build(_m.Mutants) };
            input.Parts.Add(new PartBuild { Part = part, Model = e.Prepared });
            int version = sm.Version;
            Func<object> sharedJob = () =>
            {
                var r = new SharedMeshResult { Key = key, Version = version };
                try { r.Build = MeshBuilder.BuildCell(input); }
                catch (Exception ex) { r.Build = new CellBuildResult { Error = ex.GetType().Name + ": " + ex.Message }; }
                return r;
            };
            if (urgent) _m.Worker.PostUrgent("shared " + DeLog.Hex(p.ModelKey), sharedJob);   // a preview waits on it
            else _m.Worker.Post("shared " + DeLog.Hex(p.ModelKey), sharedJob);
            return sm;
        }

        /// <summary>Worker result: the shared meshes are uploaded within the frame budget.</summary>
        public void OnSharedBuilt(SharedMeshResult r)
        {
            if (Disabled("children")) return;
            try { OnSharedBuiltCore(r); }
            catch (Exception e) { _disabled.Add("children"); DeLog.Error("DE.S5 FEATURE children DISABLED after " + e.GetType().Name + ": " + e.Message); }
        }

        private void OnSharedBuiltCore(SharedMeshResult r)
        {
            SharedModelMesh sm;
            if (!_shared.TryGetValue(r.Key, out sm) || sm.Version != r.Version) return;
            if (r.Build == null || r.Build.Error != null)
            {
                sm.Building = false;
                sm.Failed = true;
                SharedFailed++;
                DeLog.Warn("DE.S5 shared mesh " + DeLog.Hex(r.Key.ModelKey) + " FAILED: " + (r.Build != null ? r.Build.Error : "no result"));
                return;
            }
            _m.Uploads.Enqueue(new UploadJob
            {
                Kind = UploadKind.Mesh, Name = "shared " + DeLog.Hex(r.Key.ModelKey),
                Run = () =>
                {
                    if (sm.Version != r.Version) return true;
                    foreach (KeyValuePair<ulong, Mesh> kv in sm.Meshes) _m.Owned.DestroyMesh(kv.Value);
                    sm.Meshes.Clear();
                    sm.Vertices = sm.Triangles = 0;
                    sm.Bytes = 0;
                    foreach (ChunkMeshData c in r.Build.Chunks)
                    {
                        Mesh mesh = _m.Owned.NewMesh("DE.S5 shared " + DeLog.Hex(r.Key.ModelKey) + " m" + c.Key.MaterialKey.ToString("x", CultureInfo.InvariantCulture));
                        MeshUpload.Upload(mesh, c);
                        sm.Meshes.Add(new KeyValuePair<ulong, Mesh>(c.Key.MaterialKey, mesh));
                        sm.Vertices += c.VertexCount;
                        sm.Triangles += c.TriangleCount;
                        sm.Bytes += (long)c.VertexCount * MirrorVertex.Size + (long)c.IndexCount * (c.Use32 ? 4 : 2);
                    }
                    sm.Building = false;
                    sm.Ready = true;
                    SharedBuilt++;
                    return true;
                },
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ChildPartView CreateChild(Transform parent, ulong ownerId, PartRecord p, SharedModelMesh sm)
        {
            var c = new ChildPartView
            {
                OwnerId = ownerId, PartKey = p.PartKey, ModelKey = p.ModelKey, Flags = p.Flags, Class = p.Class, ColourKey = p.ColourKey,
                H = p.H, S = p.S, V = p.V, MeshKey = sm.Key,
            };
            Array.Copy(p.Local, c.StaticLocal, 12);
            c.Go = new GameObject("DE.S5.Part." + S5PartRules.ClassName(c.Class) + "." + p.PartKey.ToString("x", CultureInfo.InvariantCulture));
            c.Go.layer = 0;
            c.T = c.Go.transform;
            c.T.SetParent(parent, false);
            bool shadows = _m.Options.Shadows && (p.Flags & S5Conv.NoShadows) == 0;
            foreach (KeyValuePair<ulong, Mesh> kv in sm.Meshes)
            {
                var r = new ChildRenderer { MaterialKey = kv.Key };
                r.Go = new GameObject("m" + kv.Key.ToString("x", CultureInfo.InvariantCulture));
                r.Go.layer = 0;
                r.Go.transform.SetParent(c.T, false);
                r.Filter = r.Go.AddComponent<MeshFilter>();
                r.Filter.sharedMesh = kv.Value;
                r.Renderer = r.Go.AddComponent<MeshRenderer>();
                r.Renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                r.Renderer.receiveShadows = true;
                r.Renderer.enabled = false;
                if (!_m.Mutants.M3)
                {
                    r.Sky = r.Go.AddComponent<SkyApplier>();
                    r.Sky.renderers = new Renderer[] { r.Renderer };
                    r.Sky.dynamic = true;
                }
                c.Renderers.Add(r);
            }
            sm.Users++;
            ApplyChildLocal(c, MirrorXform.UnityLocal(c.StaticLocal, 0));
            ChildrenCreated++;
            return c;
        }

        private static void ApplyChildLocal(ChildPartView c, Trs t)
        {
            if (c.T == null || !t.Finite) return;
            c.T.localPosition = new Vector3((float)t.P.X, (float)t.P.Y, (float)t.P.Z);
            c.T.localRotation = new Quaternion((float)t.Q.X, (float)t.Q.Y, (float)t.Q.Z, (float)t.Q.W);
            c.T.localScale = new Vector3((float)t.S.X, (float)t.S.Y, (float)t.S.Z);
        }

        private void DestroyChild(ChildPartView c)
        {
            Release(c);
            try { if (c.Go != null) UnityEngine.Object.Destroy(c.Go); } catch (Exception) { }
            c.Go = null;
            ChildrenDestroyed++;
        }

        private void Release(ChildPartView c)
        {
            SharedModelMesh sm;
            if (_shared.TryGetValue(c.MeshKey, out sm) && sm.Users > 0) sm.Users--;
        }

        private void UpdateChildMaterials()
        {
            if (!_m.Marmoset.Ready) return;
            foreach (MirrorGridView v in _m.ViewMap.Values)
                if (v.Alive) foreach (ChildPartView c in v.Children.Values) AssignMaterials(c);
        }

        internal void AssignMaterials(ChildPartView c)
        {
            foreach (ChildRenderer r in c.Renderers)
            {
                if (r.Renderer == null) continue;
                if (r.Slot == null)
                {
                    r.Slot = _m.SlotFor(r.MaterialKey, c.ColourKey, c.H, c.S, c.V);
                    if (r.Slot != null) { r.Renderer.sharedMaterial = r.Slot.Mat; r.Renderer.enabled = true; }
                }
                else if (r.Slot.Set != null && r.Slot.AppliedVersion != r.Slot.Set.Version) _m.Marmoset.ApplyMaps(r.Slot);
            }
        }

        /// <summary>Slots were re-made (textures, MaterialDef changes): every child renderer takes a new one.</summary>
        public void ResetChildSlots()
        {
            foreach (MirrorGridView v in _m.ViewMap.Values)
                foreach (ChildPartView c in v.Children.Values)
                    foreach (ChildRenderer r in c.Renderers) { r.Slot = null; if (r.Renderer != null) r.Renderer.enabled = false; }
        }

        /// <summary>
 /// S5 material styles (CONTRACT §5.4 / ARCHITECTURE §5.8 "Glass", HYPOTHESIS for the look): glass takes a clone of
 /// a vanilla glass material when one was found (S5Host reads it from the Seamoth prefab); alpha-masked materials
 /// (and glass without a source) are drawn two-sided when the shader has <c>_Cull</c>. The cut-out itself needs
 /// SE's alpha-mask texture, which the texture import does not convert yet: <c>MARMO_ALPHA_CLIP</c> is NOT enabled
 /// (it would cut by <c>_cm</c>'s alpha, which is SE's metalness), logged once.
 /// </summary>
        public Material StyleFor(Material m, MaterialDefData def, MirrorObjects owned, out bool glass)
        {
            glass = false;
            if (def == null || m == null) return m;
            bool alpha = (def.Flags & MaterialFlags.AlphaMasked) != 0;
            bool isGlass = (def.Flags & (MaterialFlags.GlassCw | MaterialFlags.GlassCcw)) != 0;
            if (isGlass && GlassSource != null)
            {
                Material g = owned.Track(new Material(GlassSource) { name = "DE.S5 glass " + DeLog.Hex(def.Key) });
                S10Sn.TintGlass(g); // S10 hook (S12 batch): a visible blue-green tint under water, so a sealed window never reads as a hole
                owned.Destroy(m);
                glass = true;
                GlassMaterials++;
                return g;
            }
            if (isGlass && !_glassMissingLogged)
            {
                _glassMissingLogged = true;
                DeLog.Warn("DE.S5 glass material " + DeLog.Hex(def.Key) + " drawn opaque and two-sided: no vanilla glass material found yet (" + (GlassSourceName ?? "Seamoth prefab not read") + ")");
            }
            if (alpha && !_alphaLogged)
            {
                _alphaLogged = true;
                DeLog.Warn("DE.S5 alpha-masked material " + DeLog.Hex(def.Key) + " drawn two-sided without its cut-out (the alpha-mask texture '" + def.AlphaMask +
                           "' is not converted yet; open item)");
            }
            if (alpha || isGlass)
            {
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f); // two-sided (gap-1 open question 4)
                TwoSidedMaterials++;
            }
            return m;
        }

        private bool _alphaLogged;
        public long GlassMaterials, TwoSidedMaterials;

        // ------------------------------------------------------------------------------------------ poses (camera callbacks)

 /// <summary>
 /// Subgrid Frames and Dynamic children from C2's subpart table with the camera's bracket (S5.M1: one snapshot
 /// behind). Rows past the table's end, or dropped by SE, hold the last pose (sticky).
 /// </summary>
        public void PoseRows(CamBracket br, bool previous, bool countFrame)
        {
            _prBr = br;
            _prPrevious = previous;
            _prCount = countFrame;
            Guard("poseRows", _stepPoseRows ?? (_stepPoseRows = PoseRowsStep));
        }

        private CamBracket _prBr;
        private bool _prPrevious, _prCount;
        private Action _stepPoseRows;

        private void PoseRowsStep() { PoseRowsCore(_prBr, _prPrevious, _prCount); }

        private void PoseRowsCore(CamBracket br, bool previous, bool countFrame)
        {
            int subLogged = 0;
            bool subSession = countFrame && _subUntilMs > _now;
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive) continue;
                // S5 review F1: rows of the camera's group ride the camera orientation's tick (MirrorConsumer.GroupRoot)
                CamBracket bv = (_m.GroupRoot != 0 && br.Valid && (v.IsSubgrid ? v.ParentId : v.Id) == _m.GroupRoot) ? br.Locked() : br;
                if (v.IsSubgrid)
                {
                    Aff se;
                    ulong tick;
                    double alpha;
                    PoseHow how;
                    if (SampleRow(v.ParentId, v.Id, bv, previous, out se, out tick, out alpha, out how))
                    {
                        bool first = !v.HasPose;
                        v.ApplyLocal(MirrorXform.UnityLocal(se), tick, how);
                        se.ToSeRows(v.LastRow, 0);
                        v.LastAlpha = alpha;
                        if (first) { SubgridsShown++; DeLog.Info("DE.S5 subgrid " + DeLog.Hex(v.Id) + " shown under root " + DeLog.Hex(v.ParentId) + " (first C2 row, tick " + tick + ")"); }
                        if (countFrame) RowsApplied++;
                        if (subSession && subLogged < MaxSubRowsPerFrame) { SubRow("subgrid", v.ParentId, v.Id, tick, alpha, how, v.LastRow, v.FrameT); subLogged++; }
                    }
                    else if (countFrame) { if (v.HasPose) RowsHeld++; else RowsMissing++; }
                }
                if (v.Children.Count == 0) continue;
                foreach (ChildPartView c in v.Children.Values)
                {
                    if (!c.Dynamic || c.T == null) continue;
                    Aff se;
                    ulong tick;
                    double alpha;
                    PoseHow how;
                    if (!SampleRow(v.Id, c.PartKey, bv, previous, out se, out tick, out alpha, out how)) { if (countFrame) RowsHeld++; continue; }
                    ApplyChildLocal(c, MirrorXform.UnityLocal(se));
                    se.ToSeRows(c.LastRow, 0);
                    c.RowSeen = true;
                    c.LastTick = tick;
                    c.LastAlpha = alpha;
                    if (countFrame) RowsApplied++;
                    if (subSession && subLogged < MaxSubRowsPerFrame) { SubRow("subpart", v.Id, c.PartKey, tick, alpha, how, c.LastRow, c.T); subLogged++; }
                }
            }
        }

        /// <summary>One C2 row at the bracket (<see cref="RowSampler.Sample"/>, Unity-free and tested).</summary>
        internal bool SampleRow(ulong owner, ulong key, CamBracket br, bool previous, out Aff se, out ulong tick, out double alpha, out PoseHow how)
        {
            return RowSampler.Sample(_m.C2.History, owner, key, br, previous, out se, out tick, out alpha, out how);
        }

        /// <summary>The main camera's position of this frame (culling and nearest-first ordering).</summary>
        public void NoteCamera(Vector3 p) { _camPos = p; _camKnown = true; }
        public bool CameraKnown { get { return _camKnown; } }
        public Vector3 CameraPos { get { return _camPos; } }

        // ------------------------------------------------------------------------------------------ edits (s5.edit)

 /// <summary>A C4 part record for a drawn grid: logged as s5.edit once the rebuilt cell (or child) is uploaded and presented.</summary>
        public void NoteEdit(uint type, ulong entity, ulong partKey, CellKey? oldCell, long frame)
        {
            if (Disabled("edits")) return;
            try { NoteEditCore(type, entity, partKey, oldCell, frame); }
            catch (Exception e) { _disabled.Add("edits"); DeLog.Error("DE.S5 FEATURE edits DISABLED after " + e.GetType().Name + ": " + e.Message); }
        }

        private void NoteEditCore(uint type, ulong entity, ulong partKey, CellKey? oldCell, long frame)
        {
            GridRecord g;
            MirrorGridView v;
            if (!_m.State.Grids.TryGetValue(entity, out g) || !_m.ViewMap.TryGetValue(entity, out v) || !v.Alive || !v.HasPose) return;
            if (GhostRules.IsGhost(g.Id, g.Flags)) return; // S7a: the placement ghost is never an S5 edit
            if (_edits.Count >= 4096) return;
            var e = new PendingEdit { Entity = entity, PartKey = partKey, Type = type, Frame = frame, UnityFrame = Time.frameCount };
            PartRecord p;
            if (g.Parts.TryGetValue(partKey, out p))
            {
                if (p.Chunked) e.Cells.Add(p.Cell);
                else e.Child = true;
            }
            if (oldCell.HasValue && !e.Cells.Contains(oldCell.Value)) e.Cells.Add(oldCell.Value);
            if (type == C4Type.PartRemove) RemovePreview(entity, partKey);
            if (e.Cells.Count == 0 && !e.Child) return;
            _edits.Add(e);
            if (type == C4Type.PartAdd && e.Cells.Count > 0 && !e.Child) WantPreview(entity, partKey);
        }

        /// <summary>A cell build posted at <paramref name="postedFrame"/> was uploaded: edits it covers become visible.</summary>
        public void OnCellUploaded(ulong grid, CellKey cell, long postedFrame, long frame)
        {
            for (int i = _edits.Count - 1; i >= 0; i--)
            {
                PendingEdit e = _edits[i];
                if (e.Entity != grid || e.Frame > postedFrame) continue;
                e.Cells.Remove(cell);
                if (e.Cells.Count == 0 && !e.Child) { e.VisibleFrame = frame; _visible.Add(e); _edits.RemoveAt(i); RemovePreview(e.Entity, e.PartKey); }
            }
        }

        private void NoteChildEditVisible(ulong entity, ulong partKey)
        {
            for (int i = _edits.Count - 1; i >= 0; i--)
            {
                PendingEdit e = _edits[i];
                if (e.Entity != entity || e.PartKey != partKey || !e.Child) continue;
                e.Child = false;
                if (e.Cells.Count == 0) { e.VisibleFrame = Time.frameCount; _visible.Add(e); _edits.RemoveAt(i); }
            }
        }

        /// <summary>The first camera sample after the upload presents the edit (presentedSeTick = the bracket's newer tick).</summary>
        public void FlushEdits(CamBracket br)
        {
            if (_visible.Count == 0 || Disabled("edits")) return;
            try { FlushEditsCore(br); }
            catch (Exception e) { _disabled.Add("edits"); DeLog.Error("DE.S5 FEATURE edits DISABLED after " + e.GetType().Name + ": " + e.Message); }
        }

        private void FlushEditsCore(CamBracket br)
        {
            foreach (PendingEdit e in _visible)
            {
                if (Disabled("rows")) break;
                if (!S10Sn.TakeEditRow()) continue; // S10 hook (round 2 A): s5.edit rows under a per-second budget, the rest counted
                JsonOut j = S5Rows.Begin("s5.edit");
                j.Str("entity", S5Rows.Hex(e.Entity)).Str("part", S5Rows.Hex(e.PartKey)).Str("kind", EditKind(e.Type)).Num("recordFrame", e.Frame)
                 .Num("visibleFrame", e.VisibleFrame).Num("presentedSeTick", br.Valid ? br.Newer : 0UL).Num("alpha", br.Alpha);
                S5Rows.End(j);
                EditsLogged++;
            }
            _visible.Clear();
        }

        private static string EditKind(uint type)
        {
            switch (type)
            {
                case C4Type.PartAdd: return "add";
                case C4Type.PartRemove: return "remove";
                case C4Type.PartRecolor: return "recolor";
                default: return "type" + type.ToString(CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------------------------------------ sessions and rows

        public void StartSub(double seconds) { _subUntilMs = _now + (ulong)(Math.Max(0.1, Math.Min(600, seconds)) * 1000); }

        public void StartCoh(ulong entity, Vec3 seLocal, double seconds)
        {
            _cohEntity = entity;
            _cohLocal = seLocal;
            _cohUntilMs = _now + (ulong)(Math.Max(0.1, Math.Min(600, seconds)) * 1000);
        }

        public bool SubActive { get { return _subUntilMs > _now; } }
        public bool CohActive { get { return _cohUntilMs > _now; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SubRow(string kind, ulong owner, ulong key, ulong tick, double alpha, PoseHow how, float[] rows, Transform t)
        {
            if (Disabled("rows")) return;
            Matrix4x4 w = t.localToWorldMatrix;
            JsonOut j = S5Rows.Begin("s5.sub");
            j.Str("kind", kind).Str("owner", S5Rows.Hex(owner)).Str("key", S5Rows.Hex(key)).Num("tick", tick).Num("alpha", alpha).Str("how", how.ToString());
            S5Rows.Rows12(j, "seLocal", rows, 0);
            j.Arr("unityWorld");
            for (int c = 0; c < 4; c++) for (int r = 0; r < 3; r++) j.Num(null, (double)w[r, c]);
            j.EndArr();
            S5Rows.End(j);
            SubRows++;
        }

        /// <summary>s5.coh (S5.N4): the screen position of a fixed point of an entity, after the main camera's final placement.</summary>
        public void CohRow(CamBracket br)
        {
            if (!CohActive || Disabled("coh")) return;
            try { CohRowCore(br); }
            catch (Exception e) { _disabled.Add("coh"); DeLog.Error("DE.S5 FEATURE coh DISABLED after " + e.GetType().Name + ": " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CohRowCore(CamBracket br)
        {
            MirrorGridView v;
            Camera cam = MainCamera.camera;
            if (cam == null || !_m.ViewMap.TryGetValue(_cohEntity, out v) || !v.Alive || !v.HasPose) return;
            Vector3 world = v.EntityT.TransformPoint(new Vector3((float)_cohLocal.X, (float)_cohLocal.Y, (float)-_cohLocal.Z));
            Vector3 sp = cam.WorldToScreenPoint(world);
            JsonOut j = S5Rows.Begin("s5.coh");
            j.Str("entity", S5Rows.Hex(_cohEntity)).Vec("seLocal", _cohLocal).Vec("world", world.x, world.y, world.z).Num("px", (double)sp.x).Num("py", (double)sp.y)
             .Num("depth", (double)sp.z).Num("presentedSeTick", br.Valid ? br.Newer : 0UL).Num("olderTick", br.Valid ? br.Older : 0UL).Num("alpha", br.Alpha)
             .Num("unityFrame", (long)Time.frameCount);
            S5Rows.End(j);
            CohRows++;
        }

        /// <summary>
 /// s5.grid and s5.placed (S5.N1.C1/C2): once a grid is settled (no dirty cell, no build in flight, posed), its
 /// counts; once per grid, 20 sample parts (smallest splitmix64 of the part key) with their drawn origin.
 /// </summary>
        private void GridRows()
        {
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                MirrorGridView v;
                if (!_m.ViewMap.TryGetValue(g.Id, out v) || !v.Alive || !v.HasPose) continue;
                if (GhostRules.IsGhost(g.Id, g.Flags)) continue; // S7a: no s5.grid / s5.placed row for the placement ghost
                if (g.DirtyCells.Count > 0 || g.ChildrenDirty || _m.CellsInFlight(g.Id)) continue;
                int logged;
                if (_gridLoggedVersion.TryGetValue(g.Id, out logged) && logged == g.Version) continue;
                _gridLoggedVersion[g.Id] = g.Version;
                GridRow(g, v);
                if (_placedLogged.Add(g.Id)) PlacedRows += PlacedRowsFor(g, v, PartSample.Pick(g.Parts.Keys, SampleParts));
            }
        }

        private void GridRow(GridRecord g, MirrorGridView v)
        {
            int chunkParts = 0, childParts = 0, edges = 0, fat = 0, subparts = 0, dynamic = 0, stage = 0, deformed = 0;
            long modelVerts = 0, modelTris = 0;
            foreach (PartRecord p in g.Parts.Values)
            {
                if (p.Chunked) chunkParts++; else childParts++;
                if (p.Class == S5Conv.ClassEdge) edges++;
                if (p.Class == S5Conv.ClassFatBlock) fat++;
                if (p.Class == S5Conv.ClassSubpart) subparts++;
                if ((p.Flags & S5Conv.Dynamic) != 0) dynamic++;
                if (S5Conv.Version(p.Flags) == 1 && (p.Flags & S5Conv.BuildStage) != 0) stage++;
                if (S5Conv.Version(p.Flags) == 1 && (p.Flags & S5Conv.Deformed) != 0) deformed++;
                ModelEntry me;
                if (_m.ModelMap.TryGetValue(p.ModelKey, out me) && me.Prepared != null) { modelVerts += me.Prepared.VertexCount; modelTris += me.Prepared.TriangleCount; }
            }
            JsonOut j = S5Rows.Begin("s5.grid");
            j.Str("entity", S5Rows.Hex(g.Id)).Str("parent", S5Rows.Hex(g.ParentId)).Num("flags", (long)g.Flags).Num("targetClass", (long)g.TargetClass)
             .Num("parts", (long)g.Parts.Count).Num("chunkParts", (long)chunkParts).Num("childParts", (long)childParts).Num("edges", (long)edges).Num("fatBlocks", (long)fat)
             .Num("subparts", (long)subparts).Num("dynamic", (long)dynamic).Num("buildStage", (long)stage).Num("deformedNotDrawn", (long)deformed)
             .Num("cells", (long)g.CellParts.Count).Num("chunks", (long)v.Chunks.Count).Num("children", (long)v.Children.Count)
             .Num("drawnVerts", (long)v.Vertices).Num("drawnTris", (long)v.Triangles).Num("modelVerts", modelVerts).Num("modelTris", modelTris)
             .Bool("subgrid", v.IsSubgrid).Num("version", (long)g.Version);
            S5Rows.End(j);
            GridRowsWritten++;
        }

        /// <summary>
 /// de.s5 placed: s5.placed rows now for the given parts of an entity (the launcher passes the keys of SE's s5.part
 /// rows, so the two sides join on (entity, part)); every grid's automatic sample when <paramref name="keys"/> is null.
 /// Returns the rows written.
 /// </summary>
        public int LogPlaced(ulong entity, List<ulong> keys)
        {
            int n = 0;
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                if (entity != 0 && g.Id != entity) continue;
                if (GhostRules.IsGhost(g.Id, g.Flags)) continue; // S7a
                MirrorGridView v;
                if (!_m.ViewMap.TryGetValue(g.Id, out v) || !v.Alive || !v.HasPose) continue;
                n += PlacedRowsFor(g, v, keys ?? PartSample.Pick(g.Parts.Keys, SampleParts));
            }
            PlacedRows += n;
            return n;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int PlacedRowsFor(GridRecord g, MirrorGridView v, List<ulong> keys)
        {
            int n = 0;
            foreach (ulong key in keys)
            {
                PartRecord p;
                if (!g.Parts.TryGetValue(key, out p)) continue;
                Vector3 drawn;
                string via;
                ChildPartView c;
                if (!p.Chunked && v.Children.TryGetValue(key, out c) && c.T != null) { drawn = c.T.position; via = "child"; }
                else
                {
                    // the part origin through a renderer of its cell (the chunk GameObject sits at the Entity's identity)
                    Transform t = v.EntityT;
                    foreach (ChunkView cv in v.Chunks.Values) if (cv.Key.Cell.Equals(p.Cell) && cv.Renderer != null) { t = cv.Go.transform; break; }
                    drawn = t.TransformPoint(new Vector3(p.Local[9], p.Local[10], -p.Local[11]));
                    via = "chunk";
                }
                JsonOut j = S5Rows.Begin("s5.placed");
                j.Str("entity", S5Rows.Hex(g.Id)).Str("part", S5Rows.Hex(key)).Str("model", S5Rows.Hex(p.ModelKey)).Str("class", S5PartRules.ClassName(p.Class)).Str("via", via);
                S5Rows.Rows12(j, "seLocal", p.Local, 0);
                j.Vec("drawn", drawn.x, drawn.y, drawn.z).Num("tick", v.LastTick).Str("how", v.LastHow.ToString()).Bool("subgrid", v.IsSubgrid);
                if (v.IsSubgrid)
                {
                    S5Rows.Rows12(j, "subgridRow", v.LastRow, 0);
                    MirrorGridView root;
                    if (_m.ViewMap.TryGetValue(v.ParentId, out root)) Pose(j, "rootPose", root.LastPose);
                }
                else Pose(j, "pose", v.LastPose);
                S5Rows.End(j);
                n++;
            }
            return n;
        }

        private static void Pose(JsonOut j, string name, EntityPose p)
        {
            j.Obj(name).Vec("o", p.Ox, p.Oy, p.Oz).Quat4("q", p.Qx, p.Qy, p.Qz, p.Qw).Num("s", p.S).Num("k", p.K).EndObj();
        }

        private long _previewLogged = -1;
        private int _previewLogSec;

        /// <summary>Per second: grid rows, the s5.scale row (chunks, renderers, draw estimate, vertices, bytes, frame p50/p99, upload ms).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Second()
        {
            GridRows();
            CollectShared();
            //: the response counters, every 30 s while they move (previews, the urgent lanes)
            if (++_previewLogSec >= 30)
            {
                _previewLogSec = 0;
                long sig = PreviewsMade + PreviewsExpired + PreviewsMissed + _m.Worker.UrgentPosted + _m.Uploads.UrgentJobs;
                if (sig != _previewLogged)
                {
                    _previewLogged = sig;
                    DeLog.Info("DE.S5 response: previews made " + PreviewsMade + ", replaced " + PreviewsReplaced + ", expired " + PreviewsExpired + ", missed " + PreviewsMissed +
                               ", live " + _previewCount + "; urgent builds " + _m.Worker.UrgentPosted + ", urgent uploads " + _m.Uploads.UrgentJobs);
                }
            }
            // edits never presented (their grid left, or a cell that never rebuilt) are forgotten after about 30 s
            long frame = Time.frameCount;
            for (int i = _edits.Count - 1; i >= 0; i--)
                if (!_m.State.Grids.ContainsKey(_edits[i].Entity) || frame - _edits[i].UnityFrame > 1800) _edits.RemoveAt(i);
            long chunks = 0, renderers = 0, enabled = 0, verts = 0, tris = 0, meshBytes = 0, culled = 0, children = 0, subgrids = 0;
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive) continue;
                if (v.IsSubgrid) subgrids++;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (!c.HasMesh) continue;
                    chunks++;
                    renderers++;
                    if (c.Renderer != null && c.Renderer.enabled) enabled++;
                    if (c.Culled) culled++;
                    verts += c.Vertices;
                    tris += c.Triangles;
                    meshBytes += (long)c.Vertices * MirrorVertex.Size + c.IndexBytes;
                }
                foreach (ChildPartView ch in v.Children.Values)
                {
                    children++;
                    foreach (ChildRenderer r in ch.Renderers)
                    {
                        renderers++;
                        if (r.Renderer != null && r.Renderer.enabled) enabled++;
                    }
                    SharedModelMesh sm;
                    if (_shared.TryGetValue(ch.MeshKey, out sm)) { verts += sm.Vertices; tris += sm.Triangles; }
                }
            }
            foreach (SharedModelMesh sm in _shared.Values) meshBytes += sm.Bytes;
            double p50, p99, max;
            int frames;
            int over;
            _frames.Take(out p50, out p99, out max, out frames, out over);
            if (Disabled("rows")) return;
            JsonOut j = S5Rows.Begin("s5.scale");
            j.Num("grids", (long)_m.State.Grids.Count).Num("views", (long)_m.ViewMap.Count).Num("subgrids", subgrids).Num("chunks", chunks).Num("children", children)
             .Num("renderers", renderers).Num("drawEstimate", enabled).Num("culled", culled).Num("vertices", verts).Num("triangles", tris).Num("meshBytes", meshBytes)
             .Num("sharedMeshes", (long)_shared.Count).Num("meshesAlive", (long)_m.Owned.MeshesAlive).Str("textures", _m.Textures.Describe())
             .Num("frames", (long)frames).Num("framesOver16_7", (long)over).Num("frameP50Ms", p50).Num("frameP99Ms", p99).Num("frameMaxMs", max).Num("uploadMaxMs", _uploadMsMax)
             .Num("uploadOverBudget", _m.Uploads.OverBudgetFrames).Num("uploadsPending", (long)_m.Uploads.Pending).Num("buildsInFlight", (long)_m.BuildQueue.InFlight)
             .Num("buildsCapped", _m.BuildQueue.Capped).Num("drainDeferred", _m.Gate.DeferredFrames).Num("drainForced", _m.Gate.ForcedFrames)
             .Num("drainMsMax", _m.DrainMsMax).Num("rowsApplied", RowsApplied).Num("rowsHeld", RowsHeld).Num("rowsMissing", RowsMissing)
             .Num("c2Rows", (long)(_m.C2.History.Newest != null ? _m.C2.History.Newest.RowCount : 0)).Num("conventionsRejected", _m.State.ConventionsRejected)
             .Str("disabled", DisabledList);
            S5Rows.End(j);
            ScaleRows++;
            _uploadMsMax = 0;
            try { S7pMirrorRow(max, p99, frames, over); } // S7p-gameplay (P14): the backlog row
            catch (Exception e) { DeLog.WarnOnce("s7p.mirror", "DE.S7p s7p.mirror row not written: " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>
 /// S7p-gameplay (P14; CONTRACT §5.4 "S7p trace rows"): <c>s7p.mirror</c>, once a second while anything is mirrored
 /// or pending: the work a big grid leaves behind (parts, dirty cells, builds in flight, uploads and children waiting,
 /// the worker queue), what each per-frame budget deferred, the room index, the drain's worst record and the

 /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void S7pMirrorRow(double frameMax, double frameP99, int frames, int over)
        {
            long parts = 0, dirty = 0, childDirtyGrids = 0, childBacklog = 0;
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                parts += g.Parts.Count;
                dirty += g.DirtyCells.Count;
                if (!g.ChildrenDirty) continue;
                childDirtyGrids++;
                int want = 0;
                foreach (PartRecord p in g.Parts.Values) if (p.Role == PartRole.Child) want++;
                MirrorGridView v;
                int have = _m.ViewMap.TryGetValue(g.Id, out v) && v.Alive ? v.Children.Count : 0;
                if (want > have) childBacklog += want - have;
            }
            ChildBacklog = (int)Math.Min(int.MaxValue, childBacklog);
            bool busy = dirty > 0 || _m.BuildQueue.InFlight > 0 || _m.Uploads.Pending > 0 || childBacklog > 0 || _m.Worker.Queued > 0;
            if (_m.State.Grids.Count == 0 && !busy) return;
            if (!busy && _now < _s7pQuietUntilMs) return; // settled: one row every 10 s
            _s7pQuietUntilMs = _now + 10000;
            if (Disabled("rows")) return;
            JsonOut j = S5Rows.Begin("s7p.mirror");
            j.Num("grids", (long)_m.State.Grids.Count).Num("parts", parts).Num("dirtyCells", dirty).Num("buildsInFlight", (long)_m.BuildQueue.InFlight)
             .Num("buildsCapped", _m.BuildQueue.Capped).Num("workerQueued", (long)_m.Worker.Queued).Num("uploadsPending", (long)_m.Uploads.Pending)
             .Num("uploadOverBudget", _m.Uploads.OverBudgetFrames).Num("childDirtyGrids", childDirtyGrids).Num("childBacklog", childBacklog)
             .Num("childFramesDeferred", ChildFramesDeferred).Num("childrenDeferred", ChildrenDeferred)
             .Num("postsDeferred", _m.S7pPostsDeferred).Num("resultsDeferred", _m.S7pResultsDeferred).Num("preparedBatched", _m.S7pPreparedBatched)
             .Num("drainMsMax", _m.DrainMsMax).Num("drainFramesOverBudget", _m.DrainFramesOverBudget)
             .Num("roomIndexBuilds", S7pRoomIndex.Builds).Num("roomIndexBuildMsMax", S7pRoomIndex.BuildMsMax).Num("roomLookups", S7pRoomIndex.Lookups)
             .Num("roomLinear", S7pRoomIndex.LinearLookups).Num("roomBoxTests", S7pRoomIndex.BoxTests).Num("roomPartsMoved", _m.State.Rooms.PartsMoved)
             .Num("frames", (long)frames).Num("framesOver16_7", (long)over).Num("frameP99Ms", frameP99).Num("frameMaxMs", frameMax)
             .Num("budgetScale", S7pMirrorBudget.Scale).Bool("busy", busy);
            S5Rows.End(j);
        }

        private ulong _s7pQuietUntilMs;

        /// <summary>
 /// Users of the shared Child meshes counted again (views destroyed wholesale do not release theirs); a mesh unused
 /// for <see cref="SharedIdleSeconds"/> is destroyed explicitly.
 /// </summary>
        private void CollectShared()
        {
            if (_shared.Count == 0) return;
            foreach (SharedModelMesh sm in _shared.Values) sm.Users = 0;
            SharedModelMesh u;
            foreach (MirrorGridView v in _m.ViewMap.Values)
                if (v.Alive) foreach (ChildPartView c in v.Children.Values) if (_shared.TryGetValue(c.MeshKey, out u)) u.Users++;
            foreach (ChildPartView c in _m.Character.Children) if (_shared.TryGetValue(c.MeshKey, out u)) u.Users++;
            foreach (Dictionary<ulong, ChildPartView> d in _previews.Values) foreach (ChildPartView c in d.Values) if (_shared.TryGetValue(c.MeshKey, out u)) u.Users++;   // the previews hold theirs too
            List<ChildMeshKey> idle = null;
            foreach (SharedModelMesh sm in _shared.Values)
            {
                if (sm.Users > 0 || sm.Building) { sm.IdleSeconds = 0; continue; }
                if (++sm.IdleSeconds >= SharedIdleSeconds) (idle = idle ?? new List<ChildMeshKey>()).Add(sm.Key);
            }
            if (idle == null) return;
            foreach (ChildMeshKey k in idle)
            {
                foreach (KeyValuePair<ulong, Mesh> kv in _shared[k].Meshes) _m.Owned.DestroyMesh(kv.Value);
                _shared.Remove(k);
            }
        }

        /// <summary>Chunks whose world bounds are farther than the cull distance are not drawn (fallback 200 m; HYPOTHESIS until the biome visibility is read).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Cull()
        {
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            // S7a hook: above the sea under the Site picture policy the entity set's leave radius (1,050 m), else 200 m
            double cullM = MirrorCullRules.DistanceM(S2Host.PictureSite, p.y, CullDistanceM);
            float d2 = (float)(cullM * cullM);
            // S7t-sn-altitude (T3): impostors are drawn out to the leave radius and the vertical reach combined (above the sea)
            double impM = ImpostorRules.CullM(S2Host.PictureSite, p.y, S7tImpostors.Settings.ReachM, CullDistanceM);
            float i2 = (float)(impM * impM);
            HashSet<ulong> coarse = _m.S7tCoarse;
            long n = 0, nImp = 0;
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                if (!v.Alive || !v.HasPose) continue;
                bool imp = coarse.Count > 0 && _m.S7tIsImpostor(v.Id); // S7t-sn-altitude (T3): the grid or its impostor root
                float vd2 = imp ? i2 : d2;
                foreach (ChunkView c in v.Chunks.Values)
                {
                    if (c.Renderer == null || c.Slot == null) continue;
                    bool far = c.Renderer.bounds.SqrDistance(p) > vd2;
                    if (far && imp) nImp++;
                    if (far) n++;
                    if (far != c.Culled) { c.Culled = far; c.Renderer.enabled = !far; }
                }
            }
            Culled = n;
            S7tImpostors.Culled = nImp; // S7t-sn-altitude (T3)
        }

        // ------------------------------------------------------------------------------------------ teardown

 /// <summary>The world went away or the mirror was reset: shared meshes destroyed explicitly, pending rows dropped.</summary>
        public void TearDown(string why)
        {
            foreach (SharedModelMesh sm in _shared.Values) foreach (KeyValuePair<ulong, Mesh> kv in sm.Meshes) _m.Owned.DestroyMesh(kv.Value);
            _shared.Clear();
            _edits.Clear();
            _visible.Clear();
            _gridLoggedVersion.Clear();
            _placedLogged.Clear();
        }

        /// <summary>A model was prepared again (mutants): shared meshes of that model are rebuilt and their children re-made.</summary>
        public void OnModelPrepared(ulong modelKey)
        {
            if (_shared.Count == 0) return;
            try { OnModelPreparedCore(modelKey); }
            catch (Exception e) { DeLog.WarnOnce("s5.reprepared", "DE.S5 shared meshes not re-made: " + e.GetType().Name + ": " + e.Message); }
        }

        private void OnModelPreparedCore(ulong modelKey)
        {
            var stale = new List<ChildMeshKey>();
            foreach (SharedModelMesh sm in _shared.Values) if (sm.Key.ModelKey == modelKey) stale.Add(sm.Key);
            if (stale.Count == 0) return;
            foreach (ChildMeshKey k in stale)
            {
                SharedModelMesh sm = _shared[k];
                foreach (KeyValuePair<ulong, Mesh> kv in sm.Meshes) _m.Owned.DestroyMesh(kv.Value);
                _shared.Remove(k);
            }
            foreach (MirrorGridView v in _m.ViewMap.Values)
            {
                List<ulong> gone = null;
                foreach (ChildPartView c in v.Children.Values) if (c.ModelKey == modelKey) (gone = gone ?? new List<ulong>()).Add(c.PartKey);
                if (gone == null) continue;
                foreach (ulong k in gone) { DestroyChild(v.Children[k]); v.Children.Remove(k); }
                GridRecord g;
                if (_m.State.Grids.TryGetValue(v.Id, out g)) g.ChildrenDirty = true;
            }
        }

        public string Describe()
        {
            return "children=" + ChildrenCreated + "/-" + ChildrenDestroyed + " shared=" + _shared.Count + "(built " + SharedBuilt + ", failed " + SharedFailed + ")" +
                   " rows=applied:" + RowsApplied + ",held:" + RowsHeld + ",missing:" + RowsMissing + " subgridsShown=" + SubgridsShown + " culled=" + Culled +
                   " edits=pending:" + _edits.Count + ",logged:" + EditsLogged + " rowsWritten{grid:" + GridRowsWritten + ",placed:" + PlacedRows + ",scale:" + ScaleRows +
                   ",sub:" + SubRows + ",coh:" + CohRows + "} disabled=" + DisabledList;
        }
    }
}
