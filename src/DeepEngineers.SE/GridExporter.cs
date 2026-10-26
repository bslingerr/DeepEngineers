using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// How a part's model key is decided (implemented by the producer, which owns the model entries): the base key of
 /// (asset, scale) and, for a skinned part, the skin variant key once the base model's materials are known (CONTRACT
 /// §5.4 "Model keys and skins"). False = deferred (the base model is still loading; the part is read again later).
 /// </summary>
    internal interface IS5ModelResolver
    {
        bool Resolve(MyModel model, MyStringHash skin, out ulong modelKey, out string asset, out float scale, out uint skinId, out bool skinMetalness);
        bool ResolveAsset(string asset, float scale, MyStringHash skin, out ulong modelKey, out uint skinId, out bool skinMetalness);
    }

    /// <summary>One SE render cell as mirrored: its held parts (cube parts and edges) and its debounce.</summary>
    internal sealed class CellHeld
    {
        public MyCubeGridRenderCell Cell;
        public Vector3I Key;
        public bool HasKey;
        public Dictionary<ulong, PartSnapshot> Held = new Dictionary<ulong, PartSnapshot>();
        public readonly SignatureDebounce Deb = new SignatureDebounce();
        public ulong DirtySinceTick;
        public int Deferred;
        public bool FirstPassPending;
    }

    /// <summary>A subpart as mirrored (motion detection and the C2 row of a dynamic one).</summary>
    internal sealed class SubRef
    {
        public MyEntitySubpart Sub;
        public MyCubeBlock Block;
        public ulong Key;
        public bool Dynamic;
        public float[] SentLocal;
        public string Path;
        /// <summary>: recorded but not exported because SE hides it (the parachute canopy before deployment); watched by DetectMotion.</summary>
        public bool Hidden;
    }

    /// <summary>One block with a fat block as mirrored: the fat part and its subparts.</summary>
    internal sealed class BlockHeld
    {
        public MySlimBlock Slim;
        public ulong FatKey;
        public Dictionary<ulong, PartSnapshot> Held = new Dictionary<ulong, PartSnapshot>();
        public ulong HeldSig;
        public bool HasHeld;
        public readonly List<SubRef> Subs = new List<SubRef>();
        public readonly HashSet<ulong> DynamicKeys = new HashSet<ulong>();
        public bool Removed;
        public ulong DirtySinceTick;
        public int Deferred;
        public bool FirstPassPending;
        /// <summary>: the subpart count at the last read (the parachute canopy is created at deployment: a count change re-reads the block).</summary>
        public int SubpartsSeen;
    }

    /// <summary>The S5 state of one mirrored grid (GridExporter; owned by the producer's registry entry).</summary>
    internal sealed class S5GridState
    {
        public long Id;
        public MyCubeGrid Grid;
        public bool Large;
        public readonly Dictionary<MyCubeGridRenderCell, CellHeld> Cells = new Dictionary<MyCubeGridRenderCell, CellHeld>();
        public readonly Dictionary<MySlimBlock, BlockHeld> Blocks = new Dictionary<MySlimBlock, BlockHeld>();
        public readonly Queue<CellHeld> CellQueue = new Queue<CellHeld>();
        public readonly HashSet<CellHeld> CellQueued = new HashSet<CellHeld>();
        public readonly Queue<BlockHeld> BlockQueue = new Queue<BlockHeld>();
        public readonly HashSet<BlockHeld> BlockQueued = new HashSet<BlockHeld>();
        public readonly List<BlockHeld> SweepBlocks = new List<BlockHeld>();
        public readonly List<CellHeld> SweepCells = new List<CellHeld>();
        public int SweepBlockCursor, SweepCellCursor;
        public bool SweepListsStale = true;
        public bool FirstPassStarted, FirstPassDone;
        public int FirstPending;
        /// <summary>The aggregate of every held cell and block part (the producer's view of what Subnautica holds).</summary>
        public readonly Dictionary<ulong, PartSnapshot> Held = new Dictionary<ulong, PartSnapshot>();
        public readonly LaneChooser Lanes = new LaneChooser();
        public readonly List<SubRef> Dynamic = new List<SubRef>();
        public double Distance = double.PositiveInfinity;
        public long Reads, FullReads, Commits;
        public Action<MySlimBlock> OnAdded, OnRemoved, OnIntegrity;
        public Action<MyCubeGrid, MyCubeGrid> OnSplit, OnMerge;
        public bool Hooked;
        public bool FullRescan;
        public bool DynamicStale;
        /// <summary>S7p-scene-above (P7): a projector's blueprint: blocks SE hides are not exported, no edges.</summary>
        public bool Projection;
    }

    /// <summary>
 /// The S5 grid exporter (CONTRACT §5.4 "PartAdd (S5)", "Live edits (deltas)"): reads every part class of one grid on
 /// the update thread and diffs it against what Subnautica holds, per SE render cell (cube parts and edges, debounced:
 /// a cheap allocation-free signature first, a full read only once two reads agree and differ from the held set) and
 /// per fat block (the fat part and its nested subparts). Cells are marked dirty by the
 /// <c>MyCubeGridRenderCell.RebuildInstanceParts(RenderFlags)</c> postfix (S5Patches), blocks by the grid's
 /// <c>OnBlockAdded</c>/<c>OnBlockRemoved</c>/<c>OnBlockIntegrityChanged</c> events
 /// and the <c>ChangeColorAndSkin</c> postfix;
 /// a slow round-robin sweep re-signs every cell and block for whatever no hook reports. Members:
 /// <c>MyCubeGrid.RenderData.Cells</c> (<c>ConcurrentDictionary&lt;Vector3I, MyCubeGridRenderCell&gt;</c>),
 /// <c>MyCubeGridRenderCell.CubeParts</c>, its private <c>m_edgesToRender</c> (<c>ConcurrentDictionary&lt;long,
 /// MyEdgeRenderData&gt;</c>, private struct with public readonly <c>ModelId</c> and <c>EdgeInfo</c>; reflection, a missing
 /// member turns edges off with one log line), <c>MyFourEdgeInfo.FirstAvailable</c>, <c>LocalOrthoMatrix</c>,
 /// <c>GetNormalInfo(int, out Color, out MyStringHash, out MyStringHash, out Base27Directions.Direction, out
 /// Base27Directions.Direction)</c>, <c>Color.ColorToHSVDX11</c>, <c>Vector4.UnpackOrthoMatrix(ref Vector4, out Matrix)</c>,
 /// <c>MyModel.GetById(int)</c> (asset name), <c>MyCubePart.Model/InstanceData/SkinSubtypeId</c>,
 /// <c>MyCubeInstanceData.EnableSkinning</c>/<c>GetPackedBone(int)</c> (neutral bones 128: ResetBones), <c>MySlimBlock.FatBlock</c>,
 /// <c>Min</c>, <c>ColorMaskHSV</c>, <c>SkinSubtypeId</c>, <c>CalculateCurrentModelID</c>, <c>MyEntity.Subparts</c>
 /// (<c>Dictionary&lt;string, MyEntitySubpart&gt;</c>), <c>PositionComp.LocalMatrixRef</c>, <c>Parent</c>, <c>Render.Visible</c>,
 /// <c>Render.MetalnessColorable</c>. Update thread only.
 /// </summary>
    internal static class GridExporter
    {
        public const int StablePolls = 2;

        /// <summary>Called with each committed diff (the producer queues the records).</summary>
        internal delegate void EmitDiff(S5GridState st, List<PartSnapshot> adds, List<PartSnapshot> replaces, List<PartSnapshot> removes,
                                        List<PartSnapshot> recolors, string source, ulong eventTick, bool firstPass);

        public static EmitDiff Emit;
        public static IS5ModelResolver Models;

        // scratch (update thread)
        private static readonly Dictionary<ulong, PartSnapshot> Read = new Dictionary<ulong, PartSnapshot>();
        private static readonly List<PartSnapshot> Adds = new List<PartSnapshot>(), Replaces = new List<PartSnapshot>(), Removes = new List<PartSnapshot>(), Recolors = new List<PartSnapshot>();
        private static readonly List<SubRef> _subsScratch = new List<SubRef>();

        public static long EdgeReads { get; private set; }
        public static long DeformedParts { get; private set; }
        public static string EdgeError { get { return Edges.Error; } }

        // ================================================================================================= attach

 /// <summary>Creates the state of a newly mirrored grid and subscribes its events (update thread).</summary>
        public static S5GridState Attach(MyCubeGrid grid)
        {
            LogColorizeOnce(); // S7t-colour-mask (T6)
            var st = new S5GridState { Id = grid.EntityId, Grid = grid, Large = grid.GridSizeEnum == VRage.Game.MyCubeSize.Large };
            st.OnAdded = b => MarkBlock(st, b, false);
            st.OnRemoved = b => MarkBlock(st, b, true);
            st.OnIntegrity = b => MarkBlock(st, b, false);
            st.OnSplit = (a, b) => { st.FullRescan = true; S5Log.Edit("split", st.Id, b != null ? b.EntityId : 0); };
            st.OnMerge = (a, b) => { st.FullRescan = true; S5Log.Edit("merge", st.Id, b != null ? b.EntityId : 0); };
            try
            {
                grid.OnBlockAdded += st.OnAdded;
                grid.OnBlockRemoved += st.OnRemoved;
                grid.OnBlockIntegrityChanged += st.OnIntegrity;
                grid.OnGridSplit += st.OnSplit;
                grid.OnGridMerge += st.OnMerge;
                st.Hooked = true;
            }
            catch (Exception e) { DeLog.Info("DE.S5 grid " + grid.EntityId + ": event hooks failed (" + e.GetType().Name + ": " + e.Message + "); the sweep still finds edits"); }
            return st;
        }

        private static bool _colorizeLogged;

        /// <summary>
 /// S7t-colour-mask (ROADMAP S7t T6): Subnautica paints the mirrored parts with SE's <c>ColorizeGray</c> on the key
 /// <c>colorMaskHSV + ColorizeHSV</c>, assuming SE's run-time <c>ColorizeHSV</c> is (0, 0.8, -0.1)
 /// (<c>MyTextureDebugMultipliers.Defaults</c>; the game's Environment.sbc sets only GlossMultiplier). The value SE
 /// renders with is <c>MySector.SunProperties.TextureMultipliers.ColorizeHSV</c>
 /// logged once per process at the first mirrored grid so a
 /// world or mod with another environment shows up in the log. The part colours themselves already ride every PartAdd
 /// (<c>colorMaskHSV</c>: cube parts <c>MyCubeInstanceData.ColorMaskHSV</c>, fat blocks and subparts
 /// <c>MySlimBlock.ColorMaskHSV</c>, edges <c>Color.ColorToHSVDX11</c>), and a repaint re-sends them (the part hash).
 /// </summary>
        private static void LogColorizeOnce()
        {
            if (_colorizeLogged) return;
            _colorizeLogged = true;
            try
            {
                Vector4 c = Sandbox.Game.World.MySector.SunProperties.TextureMultipliers.ColorizeHSV;
                bool same = Math.Abs(c.X) < 1e-4f && Math.Abs(c.Y - 0.8f) < 1e-4f && Math.Abs(c.Z + 0.1f) < 1e-4f;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                DeLog.Info("DE.S7t mask SE ColorizeHSV=(" + c.X.ToString("0.####", ci) + "," + c.Y.ToString("0.####", ci) + "," + c.Z.ToString("0.####", ci) + ") " +
                           (same ? "= the key offset Subnautica's paint uses (0, 0.8, -0.1) (S7t T6)"
                                 : "DIFFERS from the key offset Subnautica's paint uses (0, 0.8, -0.1): mirrored paint colours will be off (S7t T6 HYPOTHESIS refuted for this world)"));
            }
            catch (Exception e) { DeLog.Info("DE.S7t mask SE ColorizeHSV not read (" + e.GetType().Name + ": " + e.Message + "); Subnautica assumes (0, 0.8, -0.1)"); }
        }

        public static void Detach(S5GridState st)
        {
            if (st == null || !st.Hooked || st.Grid == null) return;
            try
            {
                st.Grid.OnBlockAdded -= st.OnAdded;
                st.Grid.OnBlockRemoved -= st.OnRemoved;
                st.Grid.OnBlockIntegrityChanged -= st.OnIntegrity;
                st.Grid.OnGridSplit -= st.OnSplit;
                st.Grid.OnGridMerge -= st.OnMerge;
            }
            catch (Exception) { }
            st.Hooked = false;
        }

        /// <summary>Forgets everything Subnautica held (stream restart): the next pass sends the grid again.</summary>
        public static void ForgetHeld(S5GridState st)
        {
            st.Cells.Clear();
            st.Blocks.Clear();
            st.CellQueue.Clear();
            st.CellQueued.Clear();
            st.BlockQueue.Clear();
            st.BlockQueued.Clear();
            st.SweepBlocks.Clear();
            st.SweepCells.Clear();
            st.SweepListsStale = true;
            st.Held.Clear();
            st.Dynamic.Clear();
            st.Lanes.Reset();
            st.FirstPassStarted = st.FirstPassDone = false;
            st.FirstPending = 0;
            st.FullRescan = false;
        }

        /// <summary>
 /// The first pass (or a full rescan): every render cell and every block with a fat block is queued; each is
 /// committed once stable (a grid's first-send records go to C4 lane 4). Update thread.
 /// </summary>
        public static void StartFirstPass(S5GridState st, ulong tick)
        {
            st.FirstPassStarted = true;
            st.FirstPassDone = false;
            st.FirstPending = 0;
            MyCubeGrid grid = st.Grid;
            MyCubeGridRenderData rd = grid.RenderData;
            if (rd != null)
                foreach (KeyValuePair<Vector3I, MyCubeGridRenderCell> kv in rd.Cells)
                {
                    if (kv.Value == null) continue;
                    CellHeld c = GetCell(st, kv.Value, kv.Key, true);
                    c.FirstPassPending = true;
                    st.FirstPending++;
                    Queue(st, c, tick);
                }
            foreach (MySlimBlock b in grid.GetBlocks())
            {
                if (b == null || b.FatBlock == null) continue;
                BlockHeld bh = GetBlock(st, b, true);
                bh.FirstPassPending = true;
                st.FirstPending++;
                Queue(st, bh, tick);
            }
            // cells that disappeared since a previous pass read as empty (their parts are removed)
            foreach (CellHeld c in st.Cells.Values)
                if (!c.FirstPassPending && c.Held.Count > 0) Queue(st, c, tick);
            st.SweepListsStale = true;
            if (st.FirstPending == 0) st.FirstPassDone = true;
        }

        private static CellHeld GetCell(S5GridState st, MyCubeGridRenderCell cell, Vector3I key, bool hasKey)
        {
            CellHeld c;
            if (!st.Cells.TryGetValue(cell, out c))
            {
                c = new CellHeld { Cell = cell, Key = key, HasKey = hasKey };
                st.Cells[cell] = c;
                st.SweepListsStale = true;
            }
            else if (hasKey && !c.HasKey) { c.Key = key; c.HasKey = true; }
            return c;
        }

        private static BlockHeld GetBlock(S5GridState st, MySlimBlock slim, bool create)
        {
            BlockHeld b;
            if (!st.Blocks.TryGetValue(slim, out b) && create)
            {
                b = new BlockHeld { Slim = slim, FatKey = S5Conv.FatKey(slim.Min.X, slim.Min.Y, slim.Min.Z) };
                st.Blocks[slim] = b;
                st.SweepListsStale = true;
            }
            return b;
        }

        private static void Queue(S5GridState st, CellHeld c, ulong tick)
        {
            if (c.DirtySinceTick == 0) c.DirtySinceTick = tick == 0 ? 1 : tick;
            if (st.CellQueued.Add(c)) st.CellQueue.Enqueue(c);
        }

        private static void Queue(S5GridState st, BlockHeld b, ulong tick)
        {
            if (b.DirtySinceTick == 0) b.DirtySinceTick = tick == 0 ? 1 : tick;
            if (st.BlockQueued.Add(b)) st.BlockQueue.Enqueue(b);
        }

        // ================================================================================================= marks

 /// <summary>The render-cell postfix (drained on the update thread): the cell's parts changed.</summary>
        public static void MarkCell(S5GridState st, MyCubeGridRenderCell cell, ulong tick)
        {
            if (st == null || cell == null || !st.FirstPassStarted) return;
            CellHeld c;
            if (!st.Cells.TryGetValue(cell, out c))
            {
                // a new cell object: find its key once (rare: a block placed in an empty region)
                Vector3I key = default(Vector3I);
                bool found = false;
                MyCubeGridRenderData rd = st.Grid.RenderData;
                if (rd != null)
                    foreach (KeyValuePair<Vector3I, MyCubeGridRenderCell> kv in rd.Cells)
                        if (ReferenceEquals(kv.Value, cell)) { key = kv.Key; found = true; break; }
                c = GetCell(st, cell, key, found);
            }
            Queue(st, c, tick);
        }

        /// <summary>A block event (added, removed, integrity, paint): the block's fat part and subparts are read again.</summary>
        public static void MarkBlock(S5GridState st, MySlimBlock slim, bool removed)
        {
            if (st == null || slim == null || !st.FirstPassStarted) return;
            ulong tick = S5Log.Tick;
            BlockHeld b = GetBlock(st, slim, !removed && slim.FatBlock != null);
            if (b == null) return;
            if (removed) b.Removed = true;
            Queue(st, b, tick);
        }

        // ================================================================================================= process

 /// <summary>
 /// Reads queued cells and blocks until <paramref name="deadline"/> (Stopwatch ticks). A cell read this tick that is
 /// not yet stable stays queued for the next tick (two reads one tick apart).
 /// </summary>
        public static void Process(S5GridState st, ulong tick, long deadline, bool cellsFirst)
        {
            if (st.FullRescan)
            {
                st.FullRescan = false;
                StartFirstPass(st, tick);
            }
            int cells = st.CellQueue.Count;
            for (int i = 0; i < cells && Stopwatch.GetTimestamp() < deadline; i++)
            {
                CellHeld c = st.CellQueue.Dequeue();
                st.CellQueued.Remove(c);
                if (!ProcessCell(st, c, tick)) { st.CellQueued.Add(c); st.CellQueue.Enqueue(c); }
            }
            int blocks = st.BlockQueue.Count;
            for (int i = 0; i < blocks && Stopwatch.GetTimestamp() < deadline; i++)
            {
                BlockHeld b = st.BlockQueue.Dequeue();
                st.BlockQueued.Remove(b);
                ProcessBlock(st, b, tick);
            }
            if (!st.FirstPassDone && st.FirstPending <= 0) st.FirstPassDone = true;
            if (st.DynamicStale) RefreshDynamic(st);
        }

        /// <summary>True when the cell is done (committed or unchanged); false = read again next tick.</summary>
        private static bool ProcessCell(S5GridState st, CellHeld c, ulong tick)
        {
            st.Reads++;
            bool exists = CellExists(st, c);
            int deferred;
            ulong sig = exists ? CellSignature(st, c.Cell, out deferred) : EmptySignature(out deferred);
            c.Deferred = deferred;
            if (!c.Deb.Offer(sig, StablePolls))
            {
                if (c.Deb.Unsettled(StablePolls)) return false;
                Done(st, c);
                return true;
            }
            st.FullReads++;
            Read.Clear();
            if (exists) ReadCell(st, c.Cell, Read);
            Commit(st, c.Held, Read, "cell", c.DirtySinceTick, c.FirstPassPending);
            c.Deb.Commit(sig);
            Done(st, c);
            if (!exists && c.Held.Count == 0) { st.Cells.Remove(c.Cell); st.SweepListsStale = true; }
            return true;
        }

        private static void Done(S5GridState st, CellHeld c)
        {
            c.DirtySinceTick = 0;
            if (c.FirstPassPending) { c.FirstPassPending = false; st.FirstPending--; }
            // deferred (skinned) parts are read again when their base model loads (OnModelReady)
        }

        private static bool CellExists(S5GridState st, CellHeld c)
        {
            MyCubeGridRenderData rd = st.Grid.RenderData;
            if (rd == null) return false;
            if (!c.HasKey) return true; // key unknown: trust the postfix that reported it
            MyCubeGridRenderCell now;
            return rd.Cells.TryGetValue(c.Key, out now) && ReferenceEquals(now, c.Cell);
        }

        private static void ProcessBlock(S5GridState st, BlockHeld b, ulong tick)
        {
            st.Reads++;
            MySlimBlock slim = b.Slim;
            Read.Clear();
            int deferred = 0;
            if (!b.Removed && slim != null) ReadBlock(st, b, Read, out deferred);
            else b.Subs.Clear();
            b.Deferred = deferred;
            ulong sig = S5PartDiff.Signature(SignatureView(b, Read));
            if (!b.HasHeld || sig != b.HeldSig || b.Removed)
            {
                st.FullReads++;
                Commit(st, b.Held, Read, "block", b.DirtySinceTick, b.FirstPassPending);
                b.HeldSig = sig;
                b.HasHeld = true;
            }
            // the motion detector compares with what Subnautica holds, not with this read
            foreach (SubRef r in b.Subs)
            {
                PartSnapshot h;
                if (b.Held.TryGetValue(r.Key, out h) && h.Local != null) r.SentLocal = h.Local;
            }
            b.DirtySinceTick = 0;
            if (b.FirstPassPending) { b.FirstPassPending = false; st.FirstPending--; }
            if ((b.Removed || slim == null || slim.FatBlock == null) && b.Held.Count == 0)
            {
                st.Blocks.Remove(b.Slim);
                st.SweepListsStale = true;
            }
            st.DynamicStale = true;
        }

        /// <summary>The block signature excludes subpart matrices (the motion detector owns subpart motion).</summary>
        private static Dictionary<ulong, PartSnapshot> SignatureView(BlockHeld b, Dictionary<ulong, PartSnapshot> read)
        {
            _sigView.Clear();
            foreach (var kv in read)
            {
                PartSnapshot p = kv.Value;
                if (S5Conv.ClassOf(p.Flags) == S5Conv.PartClass.Subpart) p.Local = null;
                _sigView[kv.Key] = p;
            }
            return _sigView;
        }

        private static readonly Dictionary<ulong, PartSnapshot> _sigView = new Dictionary<ulong, PartSnapshot>();

        /// <summary>Diffs a read against a held set, emits, and updates the held set and the grid aggregate.</summary>
        private static void Commit(S5GridState st, Dictionary<ulong, PartSnapshot> held, Dictionary<ulong, PartSnapshot> now, string source, ulong eventTick, bool firstPass)
        {
            long s10 = Stopwatch.GetTimestamp(); // S10 hook (round 3 G): the commit (diff, record serialization, C4 enqueue, rows) timed apart from the reads
            try { CommitCore(st, held, now, source, eventTick, firstPass); }
            finally { S10Se.ExportCommit(s10, Adds.Count + Replaces.Count + Removes.Count + Recolors.Count); }
        }

        private static void CommitCore(S5GridState st, Dictionary<ulong, PartSnapshot> held, Dictionary<ulong, PartSnapshot> now, string source, ulong eventTick, bool firstPass)
        {
            S5PartDiff.Compute(held, now, Adds, Replaces, Removes, Recolors);
            if (Adds.Count + Replaces.Count + Removes.Count + Recolors.Count == 0) return;
            st.Commits++;
            foreach (PartSnapshot p in Removes) { held.Remove(p.PartKey); st.Held.Remove(p.PartKey); }
            foreach (PartSnapshot p in Adds) { held[p.PartKey] = p; st.Held[p.PartKey] = p; }
            foreach (PartSnapshot p in Replaces) { held[p.PartKey] = p; st.Held[p.PartKey] = p; }
            foreach (PartSnapshot p in Recolors)
            {
                PartSnapshot h;
                if (held.TryGetValue(p.PartKey, out h)) { h.H = p.H; h.S = p.S; h.V = p.V; held[p.PartKey] = h; st.Held[p.PartKey] = h; }
            }
            EmitDiff e = Emit;
            if (e != null) e(st, Adds, Replaces, Removes, Recolors, source, eventTick, firstPass);
        }

        // ================================================================================================= sweep

 /// <summary>Round-robin re-signing of a few blocks and cells per tick (edits no hook reported).</summary>
        public static void Sweep(S5GridState st, ulong tick, int blocks, int cells)
        {
            if (!st.FirstPassDone) return;
            if (st.SweepListsStale)
            {
                st.SweepBlocks.Clear();
                st.SweepBlocks.AddRange(st.Blocks.Values);
                st.SweepCells.Clear();
                st.SweepCells.AddRange(st.Cells.Values);
                st.SweepListsStale = false;
                st.SweepBlockCursor = 0;
                st.SweepCellCursor = 0;
            }
            // checked inline, never queued while unchanged: the queues hold only real pending changes (the consumer audit and
 // the resend-complete rule wait for empty queues)
            for (int i = 0; i < blocks && st.SweepBlocks.Count > 0; i++)
            {
                if (st.SweepBlockCursor >= st.SweepBlocks.Count) st.SweepBlockCursor = 0;
                BlockHeld b = st.SweepBlocks[st.SweepBlockCursor++];
                if (st.BlockQueued.Contains(b) || !st.Blocks.ContainsKey(b.Slim)) continue;
                ProcessBlock(st, b, tick); // a signature compare; commits only a real change
            }
            for (int i = 0; i < cells && st.SweepCells.Count > 0; i++)
            {
                if (st.SweepCellCursor >= st.SweepCells.Count) st.SweepCellCursor = 0;
                CellHeld c = st.SweepCells[st.SweepCellCursor++];
                if (st.CellQueued.Contains(c) || !st.Cells.ContainsKey(c.Cell)) continue;
                if (!ProcessCell(st, c, tick)) Queue(st, c, tick); // changed: read again next tick (debounce)
            }
            if (st.DynamicStale) RefreshDynamic(st);
        }

        /// <summary>A model finished loading: cells and blocks with deferred (skinned) parts are read again.</summary>
        public static void OnModelReady(S5GridState st, ulong tick)
        {
            foreach (CellHeld c in st.Cells.Values) if (c.Deferred > 0) Queue(st, c, tick);
            foreach (BlockHeld b in st.Blocks.Values) if (b.Deferred > 0) Queue(st, b, tick);
        }

        public static bool Unsettled(S5GridState st)
        {
            return !st.FirstPassDone || st.CellQueue.Count > 0 || st.BlockQueue.Count > 0;
        }

        public static int DeferredCount(S5GridState st)
        {
            int n = 0;
            foreach (CellHeld c in st.Cells.Values) n += c.Deferred;
            foreach (BlockHeld b in st.Blocks.Values) n += b.Deferred;
            return n;
        }

        // ================================================================================================= cell reads

        private static ulong EmptySignature(out int deferred)
        {
            deferred = 0;
            return new S5PartDiff.SigAcc().Value;
        }

        /// <summary>Allocation-light signature of a cell's cube parts and edges (no snapshot built).</summary>
        private static ulong CellSignature(S5GridState st, MyCubeGridRenderCell cell, out int deferred)
        {
            deferred = 0;
            var acc = new S5PartDiff.SigAcc();
            foreach (KeyValuePair<MyCubePart, ConcurrentDictionary<uint, bool>> kv in cell.CubeParts)
            {
                MyCubePart part = kv.Key;
                if (part == null || part.Model == null) continue;
                if (st.Projection && S7pProjectionRules.Hidden(part.InstanceData.ColorMaskHSV.W)) continue; // S7p-scene-above: a block SE hides in the projection (Dithering in W)
                ulong modelKey;
                string asset;
                float scale;
                uint skinId;
                bool metal;
                if (!Models.Resolve(part.Model, part.SkinSubtypeId, out modelKey, out asset, out scale, out skinId, out metal))
                {
                    deferred++;
                    acc.Add(0x5DEFE44EDUL ^ (ulong)part.SkinSubtypeId.GetHashCode());
                    continue;
                }
                Vector4 packed = part.InstanceData.PackedOrthoMatrix;
                ulong key = S5Conv.CubeKey(modelKey, packed.X, packed.Y, packed.Z, packed.W);
                Vector4 hsv = part.InstanceData.ColorMaskHSV;
                float u, v;
                Uv(ref part.InstanceData, out u, out v);
                uint flags = CubeFlags(ref part.InstanceData, metal);
                ulong h = S5PartDiff.PartHash(key, modelKey, flags, hsv.X, hsv.Y, hsv.Z, u, v, skinId);
                h = Fnv.AddF32(h, packed.X); h = Fnv.AddF32(h, packed.Y); h = Fnv.AddF32(h, packed.Z); h = Fnv.AddF32(h, packed.W);
                acc.Add(h);
            }
            IDictionaryEnumerator en = st.Projection ? null : Edges.Begin(cell); // S7p-scene-above: a hologram has no armor edges
            int modelId;
            MyFourEdgeInfo info;
            while (Edges.Next(en, out modelId, out info))
            {
                PartSnapshot p;
                if (!EdgeSnapshot(modelId, info, false, out p)) { if (p.ModelKey == 1) deferred++; continue; }
                acc.Add(S5PartDiff.PartHash(p.PartKey, p.ModelKey, p.Flags, p.H, p.S, p.V, 0, 0, p.SkinId));
            }
            return acc.Value;
        }

        private static uint CubeFlags(ref MyCubeInstanceData d, bool metal)
        {
            uint extra = 0;
            if (Deformed(ref d)) extra |= S5Conv.PartFlags.Deformed;
            if (metal) extra |= S5Conv.PartFlags.MetalnessColorable;
            return S5Conv.Part(S5Conv.PartClass.CubePart, extra);
        }

        /// <summary>SE's armor deformation: skinning on with a non-neutral bone (ResetBones writes 128 into every bone byte).</summary>
        private static bool Deformed(ref MyCubeInstanceData d)
        {
            if (!d.EnableSkinning) return false;
            for (int i = 0; i <= 8; i++)
            {
                Vector4UByte b = d.GetPackedBone(i);
                if (b.X != 128 || b.Y != 128 || b.Z != 128) return true;
            }
            return false;
        }

        private static void Uv(ref MyCubeInstanceData d, out float u, out float v)
        {
            Vector4UByte b5 = d.GetPackedBone(5), b6 = d.GetPackedBone(6), b7 = d.GetPackedBone(7);
            PartUv.Offset(b5.W, b6.W, b7.W, out u, out v);
        }

        /// <summary>The cell's cube parts and edges as snapshots (class 1 and 2; deferred parts left out).</summary>
        private static void ReadCell(S5GridState st, MyCubeGridRenderCell cell, Dictionary<ulong, PartSnapshot> into)
        {
            foreach (KeyValuePair<MyCubePart, ConcurrentDictionary<uint, bool>> kv in cell.CubeParts)
            {
                MyCubePart part = kv.Key;
                if (part == null || part.Model == null) continue;
                if (st.Projection && S7pProjectionRules.Hidden(part.InstanceData.ColorMaskHSV.W)) { S7pSceneSe.HiddenParts++; continue; } // S7p-scene-above (P7)
                ulong modelKey;
                string asset;
                float scale;
                uint skinId;
                bool metal;
                if (!Models.Resolve(part.Model, part.SkinSubtypeId, out modelKey, out asset, out scale, out skinId, out metal)) continue;
                Matrix m = part.InstanceData.LocalMatrix;
                Vector4 packed = part.InstanceData.PackedOrthoMatrix;
                Vector4 hsv = part.InstanceData.ColorMaskHSV;
                float u, v;
                Uv(ref part.InstanceData, out u, out v);
                uint flags = CubeFlags(ref part.InstanceData, metal);
                if ((flags & S5Conv.PartFlags.Deformed) != 0) DeformedParts++;
                var p = new PartSnapshot
                {
                    PartKey = S5Conv.CubeKey(modelKey, packed.X, packed.Y, packed.Z, packed.W),
                    ModelKey = modelKey,
                    AssetName = asset,
                    ModelScale = scale,
                    Local = Rows(ref m),
                    H = hsv.X, S = hsv.Y, V = hsv.Z,
                    UvU = u, UvV = v,
                    SkinId = skinId,
                    Flags = flags,
                };
                into[p.PartKey] = p;
            }
            IDictionaryEnumerator en = st.Projection ? null : Edges.Begin(cell); // S7p-scene-above: a hologram has no armor edges
            int modelId;
            MyFourEdgeInfo info;
            while (Edges.Next(en, out modelId, out info))
            {
                PartSnapshot p;
                if (EdgeSnapshot(modelId, info, true, out p)) { into[p.PartKey] = p; EdgeReads++; }
            }
        }

        /// <summary>
 /// One armor edge as SE draws it (<c>MyCubeGridRenderCell.AddEdgeParts</c>): model by id, the unpacked
 /// <c>LocalOrthoMatrix</c>, colour and skin of <c>GetNormalInfo(FirstAvailable, …)</c>, HSV = <c>ColorToHSVDX11</c>,
 /// pattern offset (0, 0) (SE sets <c>SetTextureOffset(0, 0, 1, 1)</c>), NoShadows (SE's instance flags 0). False when
 /// the edge has no info or its model is deferred (then <c>p.ModelKey</c> = 1).
 /// </summary>
        private static bool EdgeSnapshot(int modelId, MyFourEdgeInfo info, bool full, out PartSnapshot p)
        {
            p = default(PartSnapshot);
            if (info == null) return false;
            int idx = info.FirstAvailable;
            if (idx < 0) return false;
            Color color;
            MyStringHash skin, edgeModel;
            Base27Directions.Direction n0, n1;
            if (!info.GetNormalInfo(idx, out color, out skin, out edgeModel, out n0, out n1)) return false;
            string asset;
            try { asset = MyModel.GetById(modelId); }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(asset)) return false;
            ulong modelKey;
            uint skinId;
            bool metal;
            if (!Models.ResolveAsset(asset, 1f, skin, out modelKey, out skinId, out metal)) { p.ModelKey = 1; return false; }
            Vector4 packed = info.LocalOrthoMatrix;
            Vector3 hsv = color.ColorToHSVDX11();
            uint extra = S5Conv.PartFlags.NoShadows | (metal ? S5Conv.PartFlags.MetalnessColorable : 0u);
            p.PartKey = S5Conv.EdgeKey(modelKey, packed.X, packed.Y, packed.Z, packed.W);
            p.ModelKey = modelKey;
            p.H = hsv.X; p.S = hsv.Y; p.V = hsv.Z;
            p.SkinId = skinId;
            p.Flags = S5Conv.Part(S5Conv.PartClass.Edge, extra);
            if (full)
            {
                Matrix m;
                Vector4.UnpackOrthoMatrix(ref packed, out m);
                p.AssetName = asset;
                p.ModelScale = 1f;
                p.Local = Rows(ref m);
            }
            return true;
        }

        // ================================================================================================= block reads

 /// <summary>The block's fat part (class 3) and its subparts (class 4, nested), composed grid-local.</summary>
        private static void ReadBlock(S5GridState st, BlockHeld b, Dictionary<ulong, PartSnapshot> into, out int deferred)
        {
            deferred = 0;
            MySlimBlock slim = b.Slim;
            MyCubeBlock fat = slim.FatBlock;
            b.Subs.Clear();
            if (fat == null || fat.Closed || fat.MarkedForClose) return;
            if (st.Projection && S7pProjectionRules.Hidden(slim.Dithering)) { S7pSceneSe.HiddenBlocks++; return; } // S7p-scene-above: SE hides this projected block (built, or not buildable)
            Vector3 hsv = slim.ColorMaskHSV;
            MyStringHash skin = slim.SkinSubtypeId;
            Matrix fl = fat.PositionComp.LocalMatrixRef;
            MyModel model = fat.Model;
            if (model != null && fat.Render != null && fat.Render.Visible)
            {
                ulong modelKey;
                string asset;
                float scale;
                uint skinId;
                bool metal;
                if (Models.Resolve(model, skin, out modelKey, out asset, out scale, out skinId, out metal))
                {
                    uint extra = 0;
                    int stage = -1;
                    try { stage = slim.CalculateCurrentModelID(); } catch (Exception) { }
                    if (stage >= 0) extra |= S5Conv.PartFlags.BuildStage;
                    if (metal || fat.Render.MetalnessColorable) extra |= S5Conv.PartFlags.MetalnessColorable;
                    var p = new PartSnapshot
                    {
                        PartKey = b.FatKey,
                        ModelKey = modelKey,
                        AssetName = asset,
                        ModelScale = scale,
                        Local = Rows(ref fl),
                        H = hsv.X, S = hsv.Y, V = hsv.Z,
                        SkinId = skinId,
                        Flags = S5Conv.Part(S5Conv.PartClass.FatBlock, extra),
                    };
                    into[p.PartKey] = p;
                }
                else deferred++;
            }
            int subCount = fat.Subparts != null ? fat.Subparts.Count : 0;
            if (subCount > 0)
            {
                bool animates = SubpartMotion.AnimatesByType(fat.GetType().Name);
                Walk(st, b, fat, fat, fat.Subparts, "", Se(ref fl), hsv, skin, animates, into, ref deferred, 0);
            }

            // created by LoadSubpartFromName at deployment); when it is not among Subparts it is walked here as "Parachute"
            MyEntitySubpart chute = ParachuteCanopy(fat);
            if (chute != null && !SubpartListed(fat, chute))
            {
                _oneSub.Clear();
                _oneSub["Parachute"] = chute;
                Walk(st, b, fat, fat, _oneSub, "", Se(ref fl), hsv, skin, true, into, ref deferred, 0);
                subCount++;
            }
            b.SubpartsSeen = subCount;
        }

        private static readonly Dictionary<string, MyEntitySubpart> _oneSub = new Dictionary<string, MyEntitySubpart>();
        private static System.Reflection.FieldInfo _chuteField;
        private static bool _chuteFieldLooked;
        public static long CanopyWalks, SubpartCountReads;

        /// <summary>The parachute block's canopy subpart (null for any other block, or before deployment).</summary>
        private static MyEntitySubpart ParachuteCanopy(MyCubeBlock fat)
        {
            if (fat == null || fat.GetType().Name != "MyParachute") return null;
            try
            {
                if (!_chuteFieldLooked)
                {
                    _chuteFieldLooked = true;
                    _chuteField = fat.GetType().GetField("m_parachuteSubpart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    DeLog.Info("DE.S5 parachute canopy field " + (_chuteField != null ? "found (m_parachuteSubpart)" : "MISSING: the canopy is mirrored only when SE lists it among Subparts"));
                }
                if (_chuteField == null) return null;
                var sub = _chuteField.GetValue(fat) as MyEntitySubpart;
                if (sub != null && CanopyWalks++ == 0) DeLog.Info("DE.S5 parachute canopy present on block " + fat.EntityId + " (model " + (sub.Model != null ? sub.Model.AssetName : "none") + ", visible " + (sub.Render != null && sub.Render.Visible) + ")");
                return sub;
            }
            catch (Exception) { return null; }
        }

        private static bool SubpartListed(MyCubeBlock fat, MyEntitySubpart sub)
        {
            if (fat.Subparts == null) return false;
            foreach (KeyValuePair<string, MyEntitySubpart> kv in fat.Subparts) if (ReferenceEquals(kv.Value, sub)) return true;
            return false;
        }

        /// <summary>The block's subpart count as the exporter sees it now (Subparts plus an unlisted canopy).</summary>
        private static int SubpartCountNow(MyCubeBlock fat)
        {
            int n = fat.Subparts != null ? fat.Subparts.Count : 0;
            MyEntitySubpart chute = ParachuteCanopy(fat);
            if (chute != null && !SubpartListed(fat, chute)) n++;
            return n;
        }

        private static void Walk(S5GridState st, BlockHeld b, MyCubeBlock fat, MyEntity parent, Dictionary<string, MyEntitySubpart> subs, string prefix,
                                 SeMatrix parentComposed, Vector3 hsv, MyStringHash skin, bool animates, Dictionary<ulong, PartSnapshot> into, ref int deferred, int depth)
        {
            if (depth > 8) return;
            foreach (KeyValuePair<string, MyEntitySubpart> kv in subs)
            {
                MyEntitySubpart sub = kv.Value;
                if (sub == null || sub.Closed) continue;
                string path = prefix.Length == 0 ? kv.Key : prefix + "/" + kv.Key;
                Matrix sl = sub.PositionComp.LocalMatrixRef;
                // composed in SE space, VRage row order: the subpart's local × its parent's composed grid-local matrix
                SeMatrix composed = InstanceMath.Multiply(Se(ref sl), parentComposed);
                ulong key = S5Conv.SubpartKey(b.FatKey, path);
                MyModel model = sub.Model;
                if (model != null && sub.Render != null && sub.Render.Visible)
                {
                    ulong modelKey;
                    string asset;
                    float scale;
                    uint skinId;
                    bool metal;
                    if (Models.Resolve(model, skin, out modelKey, out asset, out scale, out skinId, out metal))
                    {
                        bool spins = !animates && SubpartSpin.Spins(sub, fat); // S7p-scene-above (P6b): a render-thread spinner (propeller, fan, turbine, H2 rotor) posed by C2
                        if (spins) S7pSceneSe.SpinnersMarked++;
                        bool dynamic = animates || spins || b.DynamicKeys.Contains(key);
                        if (dynamic) b.DynamicKeys.Add(key);
                        uint extra = (dynamic ? S5Conv.PartFlags.Dynamic : 0u) | (metal || sub.Render.MetalnessColorable ? S5Conv.PartFlags.MetalnessColorable : 0u);
                        var p = new PartSnapshot
                        {
                            PartKey = key,
                            ModelKey = modelKey,
                            AssetName = asset,
                            ModelScale = scale,
                            Local = SubpartMotion.Floats(composed),
                            H = hsv.X, S = hsv.Y, V = hsv.Z,
                            SkinId = skinId,
                            Flags = S5Conv.Part(S5Conv.PartClass.Subpart, extra),
                        };
                        into[p.PartKey] = p;
                        b.Subs.Add(new SubRef { Sub = sub, Block = fat, Key = key, Dynamic = dynamic, SentLocal = p.Local, Path = path });
                    }
                    else deferred++;
                }
                else if (model != null && sub.Render != null) // the block is read again when it shows
                    b.Subs.Add(new SubRef { Sub = sub, Block = fat, Key = key, Path = path, Hidden = true });
                if (sub.Subparts != null && sub.Subparts.Count > 0)
                    Walk(st, b, fat, sub, sub.Subparts, path, composed, hsv, skin, animates, into, ref deferred, depth + 1);
            }
        }

        /// <summary>
 /// The subpart's current grid-local matrix (allocation-free): its local, then each parent subpart's local, then the
 /// fat block's local (child × parent × …, VRage row order). False when the chain is broken (closed, re-parented).
 /// </summary>
        public static bool Compose(SubRef r, out SeMatrix composed)
        {
            composed = InstanceMath.Identity;
            MyEntitySubpart sub = r.Sub;
            if (sub == null || sub.Closed || r.Block == null || r.Block.Closed) return false;
            Matrix l = sub.PositionComp.LocalMatrixRef;
            SeMatrix m = Se(ref l);
            MyEntity p = sub.Parent;
            int guard = 0;
            while (p != null && !ReferenceEquals(p, r.Block) && guard++ < 8)
            {
                if (!(p is MyEntitySubpart)) return false;
                Matrix pl = p.PositionComp.LocalMatrixRef;
                m = InstanceMath.Multiply(m, Se(ref pl));
                p = p.Parent;
            }
            if (!ReferenceEquals(p, r.Block)) return false;
            Matrix bl = r.Block.PositionComp.LocalMatrixRef;
            composed = InstanceMath.Multiply(m, Se(ref bl));
            return true;
        }

        /// <summary>
 /// The motion detector (CONTRACT §5.4 "Dynamic subparts"): up to <paramref name="budget"/> static subparts of the
 /// grid are composed and compared with the matrix Subnautica holds; the first change above 1e-4 m or 1e-4 rad
 /// re-sends the part with <c>Dynamic</c> (same key: a replace) and from then on it has a C2 row every tick.
 /// Returns the number checked.
 /// </summary>
        public static int DetectMotion(S5GridState st, ref int cursor, int budget, ulong tick)
        {
            if (!st.FirstPassDone || budget <= 0) return 0;
            //: a block whose subpart count changed since its read (a canopy created at deployment) is read again
            _blocksScratch.Clear();
            foreach (BlockHeld b in st.Blocks.Values) _blocksScratch.Add(b);
            for (int i = 0; i < budget && i < _blocksScratch.Count; i++)
            {
                if (_blockCursor >= _blocksScratch.Count) _blockCursor = 0;
                BlockHeld b = _blocksScratch[_blockCursor++];
                MyCubeBlock fat = b.Slim != null ? b.Slim.FatBlock : null;
                if (fat == null || fat.Closed || b.Removed) continue;
                int now = SubpartCountNow(fat);
                if (now == b.SubpartsSeen) continue;
                b.SubpartsSeen = now;
                MarkBlock(st, b.Slim, false);
                SubpartCountReads++;
                if (SubpartCountReads <= 20) DeLog.Info("DE.S5 grid " + st.Id + " block " + fat.EntityId + " (" + fat.GetType().Name + ") subparts " + now + ": the block is read again (" + SubpartCountReads + ")");
            }
            _subsScratch.Clear();
            foreach (BlockHeld b in st.Blocks.Values)
                foreach (SubRef r in b.Subs)
                    _subsScratch.Add(r); // every subpart: the visibility watch covers the hidden and the dynamic ones too
            if (_subsScratch.Count == 0) return 0;
            int n = 0;
            for (; n < budget && n < _subsScratch.Count; n++)
            {
                if (cursor >= _subsScratch.Count) cursor = 0;
                SubRef r = _subsScratch[cursor++];
                //: a subpart SE shows or hides after the read (MyParachute.DoDeployChute makes the canopy visible and scales it;
 // a cut hides it again): the block is read again, so the canopy enters the mirror and leaves it with SE
                bool vis = SubVisible(r.Sub);
                if (vis == r.Hidden) { Reappear(st, r, vis); continue; }
                if (r.Hidden || r.Dynamic) continue;
                SeMatrix now;
                if (!Compose(r, out now)) continue;
                if (!SubpartMotion.Moved(r.SentLocal, now)) continue;
                MakeDynamic(st, r, now, tick);
            }
            return n;
        }

        public static long VisibilityReads;
        private static readonly List<BlockHeld> _blocksScratch = new List<BlockHeld>();
        private static int _blockCursor;

        private static bool SubVisible(MyEntitySubpart sub)
        {
            return sub != null && !sub.Closed && sub.Render != null && sub.Render.Visible && sub.Model != null;
        }

        /// <summary>A watched subpart shown or hidden since the block's read: the block is queued for a new read (once per change).</summary>
        private static void Reappear(S5GridState st, SubRef r, bool vis)
        {
            if (r.Block == null || r.Block.Closed) return;
            MySlimBlock slim = r.Block.SlimBlock;
            if (slim == null) return;
            r.Hidden = !vis;
            MarkBlock(st, slim, false);
            VisibilityReads++;
            if (VisibilityReads <= 20) DeLog.Info("DE.S5 grid " + st.Id + " subpart " + r.Path + (vis ? " shown" : " hidden") + " by SE: the block is read again (" + VisibilityReads + ")");
        }

        private static void MakeDynamic(S5GridState st, SubRef r, SeMatrix now, ulong tick)
        {
            BlockHeld b = null;
            foreach (BlockHeld x in st.Blocks.Values) if (x.Subs.Contains(r)) { b = x; break; }
            if (b == null) return;
            PartSnapshot held;
            if (!b.Held.TryGetValue(r.Key, out held)) return;
            r.Dynamic = true;
            b.DynamicKeys.Add(r.Key);
            held.Flags |= S5Conv.PartFlags.Dynamic;
            held.Local = SubpartMotion.Floats(now);
            r.SentLocal = held.Local;
            Read.Clear();
            foreach (var kv in b.Held) Read[kv.Key] = kv.Value;
            Read[r.Key] = held;
            Commit(st, b.Held, Read, "dynamic", tick, false);
            b.HeldSig = S5PartDiff.Signature(SignatureView(b, b.Held));
            if (!st.Dynamic.Contains(r)) st.Dynamic.Add(r);
            DeLog.Info("DE.S5 grid " + st.Id + " subpart " + r.Path + " key=" + S3Rows.Hex(r.Key) + " moved: now Dynamic (its own child, posed by C2 every tick)");
        }

        /// <summary>The grid's dynamic subparts after a (re)read: every held subpart flagged Dynamic.</summary>
        public static void RefreshDynamic(S5GridState st)
        {
            st.DynamicStale = false;
            st.Dynamic.Clear();
            foreach (BlockHeld b in st.Blocks.Values)
                foreach (SubRef r in b.Subs)
                    if (r.Dynamic) st.Dynamic.Add(r);
        }

        // ================================================================================================= helpers

        public static float[] Rows(ref Matrix m)
        {
            return new[] { m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43 };
        }

        /// <summary>A VRage float matrix as Wire's SE matrix (rows Right, Up, Backward, Translation; doubles).</summary>
        public static SeMatrix Se(ref Matrix m)
        {
            return new SeMatrix(new Vec3(m.M11, m.M12, m.M13), new Vec3(m.M21, m.M22, m.M23), new Vec3(m.M31, m.M32, m.M33), new Vec3(m.M41, m.M42, m.M43));
        }

        // ================================================================================================= edges (reflection)

 /// <summary>
 /// The render cell's private edge set (<c>m_edgesToRender</c>, values of the private struct <c>MyEdgeRenderData</c>):
 /// looked up once with explicit names and types, read through compiled accessors (reflection fallback). Any
 /// mismatch logs once and turns edges off (cube parts and fat blocks are unaffected).
 /// </summary>
        private static class Edges
        {
            private static bool _init, _ok;
            private static FieldInfo _field;
            private static Func<object, int> _modelId;
            private static Func<object, MyFourEdgeInfo> _info;
            public static string Error;

            private static void Init()
            {
                _init = true;
                try
                {
                    _field = typeof(MyCubeGridRenderCell).GetField("m_edgesToRender", BindingFlags.Instance | BindingFlags.NonPublic);
                    Type t = typeof(MyCubeGridRenderCell).GetNestedType("MyEdgeRenderData", BindingFlags.NonPublic);
                    FieldInfo fm = t != null ? t.GetField("ModelId", BindingFlags.Instance | BindingFlags.Public) : null;
                    FieldInfo fi = t != null ? t.GetField("EdgeInfo", BindingFlags.Instance | BindingFlags.Public) : null;
                    if (_field == null || t == null || fm == null || fi == null || fm.FieldType != typeof(int) || fi.FieldType != typeof(MyFourEdgeInfo) ||
                        !typeof(IDictionary).IsAssignableFrom(_field.FieldType))
                    {
                        Error = "MyCubeGridRenderCell.m_edgesToRender / MyEdgeRenderData.ModelId / EdgeInfo not found or changed";
                        DeLog.Info("DE.S5 MISSING " + Error + ": armor edges are not mirrored");
                        return;
                    }
                    try
                    {
                        ParameterExpression p = Expression.Parameter(typeof(object), "o");
                        _modelId = Expression.Lambda<Func<object, int>>(Expression.Field(Expression.Convert(p, t), fm), p).Compile();
                        _info = Expression.Lambda<Func<object, MyFourEdgeInfo>>(Expression.Field(Expression.Convert(p, t), fi), p).Compile();
                    }
                    catch (Exception e)
                    {
                        DeLog.Info("DE.S5 edge accessors not compiled (" + e.GetType().Name + "): reflection reads instead");
                        _modelId = o => (int)fm.GetValue(o);
                        _info = o => (MyFourEdgeInfo)fi.GetValue(o);
                    }
                    _ok = true;
                    DeLog.Info("DE.S5 edges: MyCubeGridRenderCell.m_edgesToRender (" + _field.FieldType.Name + ") read by reflection");
                }
                catch (Exception e)
                {
                    Error = e.GetType().Name + ": " + e.Message;
                    DeLog.Info("DE.S5 MISSING edge reflection failed (" + Error + "): armor edges are not mirrored");
                }
            }

            /// <summary>An enumerator over the cell's edges, or null when edges are off.</summary>
            public static IDictionaryEnumerator Begin(MyCubeGridRenderCell cell)
            {
                if (!_init) Init();
                if (!_ok || cell == null) return null;
                var dict = _field.GetValue(cell) as IDictionary;
                return dict != null ? dict.GetEnumerator() : null;
            }

            public static bool Next(IDictionaryEnumerator e, out int modelId, out MyFourEdgeInfo info)
            {
                modelId = 0;
                info = null;
                if (e == null) return false;
                while (e.MoveNext())
                {
                    object v = e.Value;
                    if (v == null) continue;
                    modelId = _modelId(v);
                    info = _info(v);
                    return true;
                }
                return false;
            }
        }
    }

    /// <summary>Small shared log helpers of S5 (the tick for edit rows, s5.edit rows from the exporter's hooks).</summary>
    internal static class S5Log
    {
        /// <summary>The SE gameplay tick of the current update (set by the producer each tick).</summary>
        public static ulong Tick;

        public static void Edit(string kind, long entityId, long otherId)
        {
            try
            {
                var sb = S5Rows.Head(DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), "edit");
                S5Rows.Str(sb, "kind", kind);
                S5Rows.Int(sb, "tick", (long)Tick);
                S5Rows.Id(sb, "entityId", entityId);
                if (otherId != 0) S5Rows.Id(sb, "otherId", otherId);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
            catch (Exception) { }
        }
    }
}
