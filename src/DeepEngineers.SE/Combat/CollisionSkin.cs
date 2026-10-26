using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.Game.Models;
using VRageMath;

namespace DeepEngineers.SE.Combat
{
    /// <summary>
 /// The S6 collision skin producer (ROADMAP S6 "SE plugin"; CONTRACT §2.6 "Sections and boxes", §5.4 "C4 type 6
 /// SkinSet (S6)"): for every grid and subgrid the S5 mirror holds, SE's own decomposition in the grid's own frame —
 /// the <c>MyCubeBlockCollector</c> logic re-implemented on public members (the collector and
 /// <c>MyBlockVerticesCache</c> are internal): box cells merged by SE's public <c>MyVoxelSegmentation</c> (<c>Simple</c>,
 /// 0 merge iterations, as <c>MyGridShape</c>), model-collision convex pieces as OBBs placed as
 /// <c>AddShapesCustom</c> places them, then Wire's <see cref="CollisionSkinMath.BuildSections"/> (fixed section lattice,
 /// ≤ 3 / 16 cells per box axis, half extents ≤ 4 m) and <see cref="SkinCodec.Encode"/>. A complete SkinSet goes on C4
 /// (lane NearEdit within 50 m, else Edit) once the grid's GridAdd is written, then again (version + 1) when a rebuild
 /// changed a section: rebuilds follow <c>OnBlockAdded</c> / <c>OnBlockRemoved</c> / <c>OnGridSplit</c> /
 /// <c>OnGridMerge</c> debounced 30 ticks, and a block-count sweep (the safety net); integrity-only changes send nothing.
 /// One grid is built at a time, nearest first, its block collection resumable within 0.5 ms per tick. A stream restart
 /// (<see cref="OnStreamStart"/>) resends every skin from version 1. Update thread only; never throws into SE.
 /// <para>Game members: <c>MyCubeGrid.GetBlocks</c>
 /// (<c>HashSet&lt;MySlimBlock&gt;</c>), <c>GridSize</c> (float), <c>GridSizeEnum</c>, <c>BlocksCount</c>, events
 /// <c>OnBlockAdded</c>/<c>OnBlockRemoved</c> (<c>Action&lt;MySlimBlock&gt;</c>), <c>OnGridSplit</c>/<c>OnGridMerge</c>
 /// (<c>Action&lt;MyCubeGrid, MyCubeGrid&gt;</c>); <c>MySlimBlock.BlockDefinition</c>, <c>Min</c>, <c>Max</c>,
 /// <c>Position</c> (fields), <c>Orientation</c> (<c>MyBlockOrientation</c>, <c>GetMatrix(out Matrix)</c>),
 /// <c>FatBlock</c>; <c>MyCubeBlockDefinition.HasPhysics</c>, <c>BlockTopology</c> (<c>MyBlockTopology.Cube</c>),
 /// <c>PhysicsOption</c> (<c>MyPhysicsOption.None</c>); <c>MyEntity.ModelCollision</c>; <c>MyModel.HavokCollisionShapes</c>
 /// (<c>HkShape[]</c>), <c>ScaleFactor</c>; <c>MyPositionComponentBase.LocalMatrixRef</c> (<c>ref readonly Matrix</c>);
 /// HavokWrapper <c>HkShape.ShapeType</c>, <c>GetLocalAABB(float, out Vector4, out Vector4)</c>,
 /// <c>HkListShape.TotalChildrenCount</c>, <c>GetChildByIndex(int)</c>, explicit <c>(HkListShape)HkShape</c>,
 /// <c>HkMoppBvTreeShape.ShapeCollection</c>, explicit <c>(HkMoppBvTreeShape)HkShape</c>,
 /// <c>HkShapeCollection.ShapeCount</c>, <c>GetShape(uint, HkShapeBuffer)</c>; <c>MyVoxelSegmentation</c>,
 /// <c>ClearInput</c>, <c>AddInput(Vector3I)</c>, <c>FindSegments(MyVoxelSegmentationType, int)</c> →
 /// <c>List&lt;Segment&gt;</c> with fields <c>Min</c>, <c>Max</c>. A missing segmentation logs once and Wire's
 /// <see cref="CollisionSkinMath.MergeCellsGreedy"/> replaces it; a failing shape read logs once per grid and that block
 /// enters as cells.</para>
 /// </summary>
    internal static class CollisionSkin
    {
        private sealed class GridSkin
        {
            public long Id;
            public MyCubeGrid Grid;
            public bool Large, Subgrid, Ready;
            public double GridSize;
            public long RootId;
            public double Distance = double.PositiveInfinity;
            public long GridAddIndex = -1;
            public Action<MySlimBlock> OnBlock;
            public Action<MyCubeGrid, MyCubeGrid> OnSplitMerge;
            public bool Hooked;
            // dirty state
            public bool Dirty = true, Immediate = true, Force;
            public string Why = "first";
            public ulong LastEventTick;
            public int BlocksAtBuild = -1;
            // what was sent
            public readonly SkinStream Stream = new SkinStream();
            public readonly SentSkin Sent = new SentSkin();
            public bool HasSent;
            public List<SkinSectionData> Last;
            public int LastCells, LastBytes, LastLane;
            public double LastBuildMs;
            public string LastCoverage, LastWhy;
            public bool TooLarge;
            // the build in progress
            public List<MySlimBlock> Snapshot;
            public int Cursor;
            public SkinBuild Build;
            public double BuildMs;
            public string BuildWhy;
            public bool BuildForce;
            public bool ShapeErrorLogged;
            public long Rebuilds, Sends, Unchanged;
        }

        private static readonly Dictionary<long, GridSkin> Skins = new Dictionary<long, GridSkin>();
        private static readonly List<long> Drop = new List<long>();
        private static GridSkin _building;
        private static ulong _tick;
        private static int _sweep;
        private static bool _segmenterMissing;
        private static string _segmenterError;
        private static RateLimit _errorLog = new RateLimit(10000);

        // counters (s6.stats skins {sent, bytes, tooLarge, rebuilds, building}, de.s6 status)
        public static long SentCount, SentBytes, TooLargeCount, RebuildCount, UnchangedCount, Restarts, CoverageErrors, Errors;

        /// <summary>Forced resends of every skin (stream restarts, <c>de.s6 resend all</c>).</summary>
        public static long ForcedAll;

        // ============================================================================================= per tick

 /// <summary>
 /// Once per tick from <c>GridMirrorProducer.S5UpdateGrids</c> after its grid loop (<paramref name="ordered"/>: the
 /// mirrored entities, nearest first). Grids only (not floating objects), each after its GridAdd was written.
 /// </summary>
        public static void Tick(List<GridMirrorProducer.MirroredGrid> ordered, ulong nowMs, ulong tick, C4Lanes c4, uint worldEpoch)
        {
            if (!S6Settings.Enabled || ordered == null || c4 == null) return;
            try { TickCore(ordered, nowMs, tick, c4); }
            catch (Exception e)
            {
                Errors++;
                _building = null;
                if (_errorLog.Allow(nowMs)) DeLog.Info("DE.Error S6 skin #" + Errors + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        private static void TickCore(List<GridMirrorProducer.MirroredGrid> ordered, ulong nowMs, ulong tick, C4Lanes c4)
        {
            _tick = tick;
            // grids that left the mirror (distance, closed, S5 off): forgotten here (their queued SkinSets were purged with
 // the EntityRemove: OutMessage.Entity)
            if (Skins.Count > 0)
            {
                Drop.Clear();
                foreach (var kv in Skins)
                {
                    GridMirrorProducer.MirroredGrid mg;
                    if (!GridMirrorProducer.Grids.TryGetValue(kv.Key, out mg) || mg.Grid == null || mg.Grid.Closed || mg.Grid != kv.Value.Grid) Drop.Add(kv.Key);
                }
                foreach (long id in Drop) Forget(id);
            }

            GridSkin pick = null;
            foreach (GridMirrorProducer.MirroredGrid g in ordered)
            {
                if (g.Floating || g.Grid == null || g.Grid.Closed || g.Grid.MarkedForClose) continue;
                GridSkin s = Get(g);
                s.Distance = g.Distance;
                s.Subgrid = g.Subgrid;
                s.RootId = g.Subgrid && g.ParentId != 0 ? g.ParentId : g.Id;
                s.Sent.Subgrid = s.Subgrid;
                s.Sent.RootId = s.RootId;
                s.Ready = g.GridAddIndex >= 0 && c4.IsDone(g.GridAddIndex);
                if (!s.Ready) continue;
                s.GridAddIndex = g.GridAddIndex;
                if (pick == null && s.Dirty && s != _building && (s.Immediate || tick - s.LastEventTick >= (ulong)CombatConventions.Limits.SkinDebounceTicks))
                    pick = s;
            }

            // the safety net: one grid per tick, a changed block count without an event (a split or merge path we missed)
            if (ordered.Count > 0)
            {
                GridMirrorProducer.MirroredGrid sg = ordered[(int)((uint)_sweep++ % (uint)ordered.Count)];
                GridSkin ss;
                if (sg.Grid != null && Skins.TryGetValue(sg.Id, out ss) && ss.HasSent && !ss.Dirty && ss != _building && ss.Grid.BlocksCount != ss.BlocksAtBuild)
                    Mark(ss, "sweep");
            }

            if (_building == null && pick != null) Start(pick);
            if (_building != null) Step(_building, c4, nowMs);
        }

        private static GridSkin Get(GridMirrorProducer.MirroredGrid g)
        {
            GridSkin s;
            if (Skins.TryGetValue(g.Id, out s)) return s;
            s = new GridSkin
            {
                Id = g.Id,
                Grid = g.Grid,
                Large = g.Grid.GridSizeEnum == MyCubeSize.Large,
                GridSize = g.Grid.GridSize,
            };
            s.Sent.EntityId = s.Id;
            s.Sent.Large = s.Large;
            s.Sent.GridSize = s.GridSize;
            s.OnBlock = b => Mark(s, "edit");
            s.OnSplitMerge = (a, b) => Mark(s, "edit");
            try
            {
                g.Grid.OnBlockAdded += s.OnBlock;
                g.Grid.OnBlockRemoved += s.OnBlock;
                g.Grid.OnGridSplit += s.OnSplitMerge;
                g.Grid.OnGridMerge += s.OnSplitMerge;
                s.Hooked = true;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S6 skin " + s.Id + ": block event hooks failed (" + e.GetType().Name + ": " + e.Message + "); the block-count sweep is the only trigger");
            }
            Skins[s.Id] = s;
            return s;
        }

        private static void Unhook(GridSkin s)
        {
            if (!s.Hooked || s.Grid == null) return;
            try
            {
                s.Grid.OnBlockAdded -= s.OnBlock;
                s.Grid.OnBlockRemoved -= s.OnBlock;
                s.Grid.OnGridSplit -= s.OnSplitMerge;
                s.Grid.OnGridMerge -= s.OnSplitMerge;
            }
            catch (Exception) { }
            s.Hooked = false;
        }

        /// <summary>A block event (update thread): the grid is rebuilt 30 ticks after its last event.</summary>
        private static void Mark(GridSkin s, string why)
        {
            s.Dirty = true;
            s.LastEventTick = _tick;
            if (!s.Immediate) s.Why = why;
        }

        // ============================================================================================= build

        private static void Start(GridSkin s)
        {
            s.Snapshot = new List<MySlimBlock>(s.Grid.GetBlocks());
            s.Cursor = 0;
            s.Build = new SkinBuild(s.GridSize, s.Large);
            s.BuildMs = 0;
            s.BuildWhy = s.Why;
            s.BuildForce = s.Force;
            s.BlocksAtBuild = s.Snapshot.Count;
            s.Dirty = false;
            s.Immediate = false;
            s.Force = false;
            s.Why = "edit";
            _building = s;
        }

        private static void Step(GridSkin s, C4Lanes c4, ulong nowMs)
        {
            if (s.Grid == null || s.Grid.Closed) { _building = null; s.Snapshot = null; return; }
            long t0 = Stopwatch.GetTimestamp();
            long limit = (long)(S6Settings.BuildBudgetMs * Stopwatch.Frequency / 1000.0);
            List<MySlimBlock> list = s.Snapshot;
            while (s.Cursor < list.Count)
            {
                Collect(s, list[s.Cursor++]);
                if ((s.Cursor & 15) == 0 && Stopwatch.GetTimestamp() - t0 >= limit) break;
            }
            s.BuildMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (s.Cursor < list.Count) return;
            Finish(s, c4, nowMs);
        }

        private static void Collect(GridSkin s, MySlimBlock blk)
        {
            if (blk == null) return;
            MyCubeBlockDefinition def = blk.BlockDefinition;
            if (def == null) return;
            SkinBuild b = s.Build;
            b.Blocks++;
            bool cube = def.BlockTopology == MyBlockTopology.Cube;
            bool none = def.PhysicsOption == MyPhysicsOption.None;
            HkShape[] shapes = null;
            if (def.HasPhysics && !cube && !none)
            {
                try
                {
                    MyCubeBlock fat = blk.FatBlock;
                    MyModel m = fat != null ? fat.ModelCollision : null;
                    shapes = m != null ? m.HavokCollisionShapes : null;
                }
                catch (Exception) { shapes = null; }
            }
            switch (SkinRules.Classify(def.HasPhysics, cube, none, shapes != null && shapes.Length > 0))
            {
                case BlockSkinKind.None:
                    b.NoneBlocks++;
                    return;
                case BlockSkinKind.Cells:
                    b.CellBlocks++;
                    b.AddCells(blk.Min.X, blk.Min.Y, blk.Min.Z, blk.Max.X, blk.Max.Y, blk.Max.Z);
                    return;
                default:
                    b.ShapeBlocks++;
                    string err = AddShapes(b, blk, shapes);
                    if (err != null)
                    {
                        b.ShapeErrors++;
                        b.AddCells(blk.Min.X, blk.Min.Y, blk.Min.Z, blk.Max.X, blk.Max.Y, blk.Max.Z);
                        if (!s.ShapeErrorLogged)
                        {
                            s.ShapeErrorLogged = true;
                            DeLog.Info("DE.S6 skin " + s.Id + ": model collision of " + def.Id + " not read (" + err + "); the block enters as cells (logged once per grid)");
                        }
                    }
                    return;
            }
        }

        /// <summary>One OBB per convex piece, placed as <c>MyCubeBlockCollector.AddShapesCustom</c> places it. Null or why not.</summary>
        private static string AddShapes(SkinBuild b, MySlimBlock blk, HkShape[] shapes)
        {
            try
            {
                MyCubeBlock fat = blk.FatBlock;
                MyModel model = fat.ModelCollision;
                Matrix rot;
                blk.Orientation.GetMatrix(out rot);
                var ro = new SeMatrix(V(rot.Right), V(rot.Up), V(rot.Backward), Vec3.Zero);
                Vector3 tr = fat.PositionComp.LocalMatrixRef.Translation;
                Vec3 t = V(tr);
                double scale = model.ScaleFactor;
                int px = blk.Position.X, py = blk.Position.Y, pz = blk.Position.Z;
                int added = 0;
                if (shapes.Length == 1 && shapes[0].ShapeType == HkShapeType.List)
                {
                    HkListShape list = (HkListShape)shapes[0];
                    int n = list.TotalChildrenCount;
                    for (int i = 0; i < n; i++) added += Piece(b, px, py, pz, i, list.GetChildByIndex(i), ro, t, scale);
                }
                else if (shapes.Length == 1 && shapes[0].ShapeType == HkShapeType.Mopp)
                {
                    HkMoppBvTreeShape mopp = (HkMoppBvTreeShape)shapes[0];
                    HkShapeCollection coll = mopp.ShapeCollection;
                    int n = coll.ShapeCount;
                    for (int j = 0; j < n; j++) added += Piece(b, px, py, pz, j, coll.GetShape((uint)j, null), ro, t, scale);
                }
                else
                    for (int k = 0; k < shapes.Length; k++) added += Piece(b, px, py, pz, k, shapes[k], ro, t, scale);
                return added > 0 ? null : "no finite piece";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        private static int Piece(SkinBuild b, int px, int py, int pz, int index, HkShape shape, SeMatrix rot, Vec3 t, double scale)
        {
            if (!shape.IsValid) return 0;
            Vector4 mn, mx;
            shape.GetLocalAABB(0f, out mn, out mx);
            if (!Finite(mn) || !Finite(mx) || mx.X < mn.X || mx.Y < mn.Y || mx.Z < mn.Z) return 0;
            SkinBoxData box = CollisionSkinMath.ObbFromModelAabb(new Vec3(mn.X, mn.Y, mn.Z), new Vec3(mx.X, mx.Y, mx.Z), rot, t, scale);
            b.AddObb(px, py, pz, index, box);
            return 1;
        }

        private static bool Finite(Vector4 v)
        {
            return !(float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z));
        }

        private static Vec3 V(Vector3 v) { return new Vec3(v.X, v.Y, v.Z); }

        private static void Finish(GridSkin s, C4Lanes c4, ulong nowMs)
        {
            long t0 = Stopwatch.GetTimestamp();
            _building = null;
            s.Snapshot = null;
            SkinBuild b = s.Build;
            s.Build = null;
            b.SortCells();
            List<CellRange> segs = Segment(b);
            string coverage = "skipped (" + b.Cells.Count + " cells)";
            if (b.Cells.Count <= S6Settings.CoverageCheckMaxCells)
            {
                string err;
                coverage = CollisionSkinMath.CheckCoverage(b.Cells, segs, out err) ? "ok" : err;
                if (coverage != "ok")
                {
                    CoverageErrors++;
                    DeLog.Info("DE.S6 skin " + s.Id + " coverage ERROR (" + err + ") with " + (_segmenterMissing ? "the greedy stand-in" : "MyVoxelSegmentation") + "; the greedy stand-in is used for this build");
                    segs = b.GreedySegments();
                }
            }
            List<SkinSectionData> sections = b.Finish(segs);
            s.BuildMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            s.Rebuilds++;
            RebuildCount++;
            if (!s.Ready || s.Grid == null || s.Grid.Closed)
            {
                // the GridAdd is being re-sent (stream restart, re-parent): build again once it is written
                s.Dirty = true;
                s.Immediate = true;
                s.Why = s.BuildWhy;
                s.Force |= s.BuildForce;
                return;
            }
            if (!s.BuildForce && !s.Stream.Differs(sections))
            {
                s.Unchanged++;
                UnchangedCount++;
                return;
            }
            int bytes = SkinCodec.PayloadBytes(sections);
            string note = null;
            if (bytes > CombatConventions.Limits.SkinMaxBytes)
            {
                TooLargeCount++;
                if (!s.TooLarge) DeLog.Info("DE.S6 skin TOO LARGE " + s.Id + ": " + bytes + " B > " + CombatConventions.Limits.SkinMaxBytes + " B (" + Boxes(sections) + " boxes): no proxy for this grid" +
                                            (s.HasSent ? "; an empty SkinSet replaces the one sent" : ""));
                s.TooLarge = true;
                if (!s.HasSent)
                {
                    Jsonl.WriteLineBuffered(S6Rows.Skin(Utc(), "tooLarge", s.Sent, s.BuildWhy, null, b.Cells.Count, bytes, s.BuildMs, coverage, 0, false, "not sent"));
                    return;
                }
                note = "too large (" + bytes + " B): empty skin";
                sections = new List<SkinSectionData>();
                bytes = SkinCodec.PayloadBytes(sections);
            }
            else s.TooLarge = false;
            string why = s.BuildWhy;
            if (s.Stream.Version == 0 && why == "edit") why = s.HasSent ? "restart" : "first";
            uint version = s.Stream.NextVersion;
            byte[] payload = SkinCodec.Encode((ulong)s.Id, version, sections);
            int lane = Lane(s.Distance);
            c4.Enqueue(lane, new OutMessage(C4Type.SkinSet, payload, 0, "SkinSet " + s.Id + " v" + version) { Entity = s.Id });
            int changed = s.Stream.ChangedSections(sections);
            s.Stream.Commit(sections);
            s.Sent.Version = s.Stream.Version;
            s.Sent.Sections = s.Stream.Current;
            s.Sent.PreviousSections = s.Stream.Previous;
            s.Sent.Boxes = Boxes(sections);
            s.Sent.HasBox = S11GrabRules.SkinExtents(sections, out s.Sent.BoxMin, out s.Sent.BoxMax); // S11 hook: the grab size class reads the same bounds Subnautica decodes
            s.Sent.Bytes = payload.Length;
            s.HasSent = true;
            s.Last = sections;
            s.LastCells = b.Cells.Count;
            s.LastBytes = payload.Length;
            s.LastLane = lane;
            s.LastBuildMs = s.BuildMs;
            s.LastCoverage = coverage;
            s.LastWhy = why;
            s.Sends++;
            SentCount++;
            SentBytes += payload.Length;
            Jsonl.WriteLineBuffered(S6Rows.Skin(Utc(), "send", s.Sent, why, sections, b.Cells.Count, payload.Length, s.BuildMs, coverage, lane, false, note));
            DeLog.Info("DE.S6 skin " + s.Id + " v" + version + " (" + why + "): " + sections.Count + " section(s), " + s.Sent.Boxes + " box(es) [" + b.CellBlocks + " cell block(s) in " +
                       segs.Count + " segment(s), " + b.ShapeBlocks + " model-shape block(s) as " + b.ObbCount + " OBB(s)" + (b.ShapeErrors > 0 ? ", " + b.ShapeErrors + " as cells" : "") +
                       "], " + changed + " section(s) changed, " + payload.Length + " B, lane " + lane + ", built in " + s.BuildMs.ToString("0.###", CultureInfo.InvariantCulture) +
                       " ms, coverage " + coverage + (b.Dropped > 0 ? ", " + b.Dropped + " piece(s) outside the section lattice DROPPED" : "") +
                       (s.Subgrid ? ", subgrid of " + s.RootId : "") + (note != null ? ", " + note : ""));
        }

        private static int Boxes(List<SkinSectionData> sections)
        {
            int n = 0;
            foreach (SkinSectionData s in sections) n += s.Boxes.Count;
            return n;
        }

        /// <summary>CONTRACT §5.4: lane NearEdit within 50 m of the camera, else Edit (MirrorBackPressure classes SkinSet as Edit).</summary>
        private static int Lane(double distance)
        {
            C4Priority p = MirrorBackPressure.Classify(C4Type.SkinSet, false, distance);
            if (distance <= MirrorBudgets.NearEditRadiusMetres && p == C4Priority.Edit) p = C4Priority.NearEdit;
            switch (p)
            {
                case C4Priority.Control: return C4Lanes.Control;
                case C4Priority.NearEdit: return C4Lanes.NearEdit;
                case C4Priority.FirstSend: return C4Lanes.FirstSend;
                default: return C4Lanes.Edit;
            }
        }

        // ============================================================================================= segmentation

        private static List<CellRange> Segment(SkinBuild b)
        {
            if (!_segmenterMissing)
            {
                try { return SeSegmenter.Run(b.Cells); }
                catch (Exception e)
                {
                    _segmenterMissing = true;
                    _segmenterError = e.GetType().Name + ": " + e.Message;
                    DeLog.Info("DE.S6 MyVoxelSegmentation unavailable (" + _segmenterError + "): Wire's CollisionSkinMath.MergeCellsGreedy replaces it (logged once)");
                }
            }
            return b.GreedySegments();
        }

        /// <summary>SE's own segmentation, isolated so that a missing type fails only here.</summary>
        private static class SeSegmenter
        {
            private static MyVoxelSegmentation _seg;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static List<CellRange> Run(List<I32x3> cells)
            {
                if (_seg == null) _seg = new MyVoxelSegmentation();
                _seg.ClearInput();
                foreach (I32x3 c in cells) _seg.AddInput(new Vector3I(c.x, c.y, c.z));
                List<MyVoxelSegmentation.Segment> found = _seg.FindSegments(MyVoxelSegmentationType.Simple, 0);
                var l = new List<CellRange>(found.Count);
                foreach (MyVoxelSegmentation.Segment g in found)
                    l.Add(new CellRange { MinX = g.Min.X, MinY = g.Min.Y, MinZ = g.Min.Z, MaxX = g.Max.X, MaxY = g.Max.Y, MaxZ = g.Max.Z });
                _seg.ClearInput();
                return l;
            }
        }

        // ============================================================================================= hooks, queries

 /// <summary>The S5 stream restarted (ClearAll and everything; hook in GridMirrorProducer.StartStream): every skin is
 /// sent again from version 1 once its GridAdd is written.</summary>
        public static void OnStreamStart()
        {
            if (Skins.Count == 0) return;
            Restarts++;
            ForcedAll++;
            foreach (GridSkin s in Skins.Values)
            {
                s.Stream.Restart();
                s.Sent.Version = 0;
                s.Dirty = true;
                s.Immediate = true;
                s.Force = true;
                s.Why = "restart";
            }
        }

        /// <summary>The world was forgotten (hook in GridMirrorProducer.S5Forget).</summary>
        public static void Forget(string why)
        {
            if (Skins.Count == 0 && _building == null) return;
            foreach (GridSkin s in Skins.Values) Unhook(s);
            int n = Skins.Count;
            Skins.Clear();
            _building = null;
            DeLog.Info("DE.S6 skins forgotten (" + n + " grid(s)): " + why);
        }

        /// <summary>One entity left the mirror (hook in GridMirrorProducer.S5Forgotten; also found by the per-tick reconcile).</summary>
        public static void Forget(long entityId)
        {
            GridSkin s;
            if (!Skins.TryGetValue(entityId, out s)) return;
            Unhook(s);
            Skins.Remove(entityId);
            if (_building == s) _building = null;
        }

        /// <summary>The skin SE last sent for a grid or subgrid (false before the first SkinSet).</summary>
        public static bool TryGetSkin(long entityId, out SentSkin skin)
        {
            GridSkin s;
            skin = Skins.TryGetValue(entityId, out s) && s.HasSent ? s.Sent : null;
            return skin != null;
        }

        /// <summary>The grid object of a mirrored entity with a sent skin (the damage consumer).</summary>
        public static bool TryGetGrid(long entityId, out MyCubeGrid grid, out SentSkin skin)
        {
            GridSkin s;
            if (Skins.TryGetValue(entityId, out s) && s.HasSent && s.Grid != null && !s.Grid.Closed) { grid = s.Grid; skin = s.Sent; return true; }
            grid = null;
            skin = null;
            return false;
        }

        /// <summary><c>de.s6 resend</c>: rebuild and send again (version + 1, even when unchanged). Returns the count marked.</summary>
        public static int Resend(long entityId)
        {
            int n = 0;
            foreach (GridSkin s in Skins.Values)
            {
                if (entityId != 0 && s.Id != entityId) continue;
                s.Dirty = true;
                s.Immediate = true;
                s.Force = true;
                s.Why = "resend";
                n++;
            }
            if (entityId == 0) ForcedAll++;
            return n;
        }

        public static bool IsTracked(long entityId) { return Skins.ContainsKey(entityId); }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["enabled"] = S6Settings.Enabled;
            d["grids"] = Skins.Count;
            int sent = 0, dirty = 0, boxes = 0, sections = 0;
            var list = new List<object>();
            foreach (GridSkin s in Skins.Values)
            {
                if (s.HasSent) { sent++; boxes += s.Sent.Boxes; sections += s.Sent.Sections.Count; }
                if (s.Dirty) dirty++;
                if (list.Count < 32)
                    list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "entity", s.Id.ToString(CultureInfo.InvariantCulture) }, { "root", s.RootId.ToString(CultureInfo.InvariantCulture) },
                        { "subgrid", s.Subgrid }, { "large", s.Large }, { "version", s.Sent.Version }, { "sections", s.Sent.Sections.Count },
                        { "boxes", s.Sent.Boxes }, { "dirty", s.Dirty }, { "why", s.Dirty ? s.Why : s.LastWhy }, { "ready", s.Ready },
                        { "distance", Math.Round(s.Distance, 2) }, { "tooLarge", s.TooLarge }, { "rebuilds", s.Rebuilds }, { "sends", s.Sends },
                    });
            }
            d["sent"] = sent;
            d["dirty"] = dirty;
            d["sections"] = sections;
            d["boxes"] = boxes;
            d["building"] = _building != null ? (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "entity", _building.Id.ToString(CultureInfo.InvariantCulture) }, { "cursor", _building.Cursor },
                { "blocks", _building.Snapshot != null ? _building.Snapshot.Count : 0 }, { "ms", Math.Round(_building.BuildMs, 3) },
            } : null;
            d["sentCount"] = SentCount;
            d["sentBytes"] = SentBytes;
            d["tooLarge"] = TooLargeCount;
            d["rebuilds"] = RebuildCount;
            d["unchanged"] = UnchangedCount;
            d["restarts"] = Restarts;
            d["coverageErrors"] = CoverageErrors;
            d["errors"] = Errors;
            d["segmentation"] = _segmenterMissing ? "greedy stand-in (" + _segmenterError + ")" : "MyVoxelSegmentation Simple, 0 iterations";
            d["list"] = list;
            return d;
        }

        public static bool Building { get { return _building != null; } }

        /// <summary>One log line on the skin SE sent for an entity (and an s6.skin census row with the sections when <paramref name="rows"/>).</summary>
        public static string Census(long entityId, bool rows)
        {
            GridSkin s;
            if (!Skins.TryGetValue(entityId, out s)) return "DE.S6 skin " + entityId + ": not a mirrored grid";
            if (!s.HasSent) return "DE.S6 skin " + entityId + ": no SkinSet sent yet (" + (s.Ready ? "building" : "GridAdd not written") + ")";
            if (rows) Jsonl.WriteLineBuffered(S6Rows.Skin(Utc(), "census", s.Sent, s.LastWhy, s.Last, s.LastCells, s.LastBytes, s.LastBuildMs, s.LastCoverage, s.LastLane, true, null));
            return "DE.S6 skin " + entityId + " v" + s.Sent.Version + ": " + s.Sent.Sections.Count + " section(s), " + s.Sent.Boxes + " box(es), " + s.LastBytes + " B" +
                   (s.Subgrid ? ", subgrid of " + s.RootId : "") + (s.Large ? ", large" : ", small") + " grid " + s.GridSize.ToString("0.###", CultureInfo.InvariantCulture) + " m";
        }

        /// <summary>The <c>de.s6 skin</c> reply: counts and, per section, its id, centre, box count, hash and up to 8 box centres.</summary>
        public static Dictionary<string, object> CensusReply(long entityId, bool rows)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            GridSkin s;
            d["entity"] = entityId.ToString(CultureInfo.InvariantCulture);
            d["line"] = Census(entityId, rows);
            if (!Skins.TryGetValue(entityId, out s)) { d["ok"] = false; d["error"] = "not a mirrored grid"; return d; }
            d["ok"] = true;
            d["root"] = s.RootId.ToString(CultureInfo.InvariantCulture);
            d["subgrid"] = s.Subgrid;
            d["large"] = s.Large;
            d["gridSize"] = s.GridSize;
            d["sentVersion"] = s.Sent.Version;
            d["hasSent"] = s.HasSent;
            if (!s.HasSent || s.Last == null) return d;
            d["sections"] = s.Last.Count;
            d["boxes"] = s.Sent.Boxes;
            d["bytes"] = s.LastBytes;
            d["cells"] = s.LastCells;
            d["coverage"] = s.LastCoverage;
            var list = new List<object>();
            foreach (SkinSectionData sec in s.Last)
            {
                if (list.Count >= S6Settings.CensusMaxSections) { d["truncated"] = true; break; }
                var bc = new List<object>();
                foreach (SkinBoxData box in sec.Boxes)
                {
                    if (bc.Count >= S6Settings.CensusBoxesPerSection) break;
                    bc.Add(new object[] { Math.Round(box.Centre.X, 4), Math.Round(box.Centre.Y, 4), Math.Round(box.Centre.Z, 4) });
                }
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", sec.SectionId }, { "c", new object[] { sec.Centre.X, sec.Centre.Y, sec.Centre.Z } }, { "n", sec.Boxes.Count },
                    { "hash", "0x" + sec.ContentHash.ToString("x16", CultureInfo.InvariantCulture) }, { "b", bc },
                });
            }
            d["list"] = list;
            return d;
        }

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    }
}
