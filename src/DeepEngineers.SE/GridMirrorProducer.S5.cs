using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRage.Game.Utils;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S5 half of the grid mirror producer (ROADMAP S5 "SE plugin"; CONTRACT §5.4 "C1–C4 conventions for S5"): the
 /// registry fed by the <see cref="EntitySet"/>, the per-grid exporter (<see cref="GridExporter"/>) and its diffs into the
 /// prioritised C4 lanes, skin variants (<see cref="IS5ModelResolver"/>), floating objects, the C2 entity records and
 /// subpart rows (<see cref="SubgridPoses"/>), the character (<see cref="CharacterExport"/>), the stream recorder, the
 /// s5.* JSONL rows (s5.grid, s5.part, s5.edit, s5.sub, s5.model, s5.coh, s5.budget) and the S5.M3 mutant. Update thread.
 /// </summary>
    internal static partial class GridMirrorProducer
    {
        public static readonly S5Settings S5Config = LoadS5Config();
        public static readonly StreamRecorder Recorder = new StreamRecorder();
        private static bool _s5Applied = true;

        /// <summary>S5 mirrors the whole entity set; off = the S3 path (de.s3 registrations only).</summary>
        public static bool S5Active { get { return S5Config.Enabled; } }

        private static double _lastC3Ms, _lastC4Ms;
        private static long _c3FreeBytes = -1;
        private static readonly ConcurrentQueue<MyCubeGridRenderCell> CellMarks = new ConcurrentQueue<MyCubeGridRenderCell>();
        private static readonly ConcurrentQueue<MySlimBlock> BlockMarks = new ConcurrentQueue<MySlimBlock>();
        private static readonly ModelResolver Resolver = new ModelResolver();
        private static readonly List<MirroredGrid> _ordered = new List<MirroredGrid>();
        private static readonly List<long> _added = new List<long>();
        private static readonly List<KeyValuePair<long, string>> _removed = new List<KeyValuePair<long, string>>();
        private static readonly HashSet<long> _loggedSkips = new HashSet<long>();
        private static SeSubpart[] _rows = new SeSubpart[SeEntitiesLimits.MaxSubparts];
        private static ulong _tick;
        private static ulong _s5TraceUntilMs;
        private static long _cohEntity;
        private static Vec3 _cohLocal;
        private static bool _cohHasPoint;
        private static ulong _cohUntilMs;
        private static ulong _sweepGrid, _motionGrid;

        // counters (s5.budget, de.s5 stats)
        private static long _s5Edits, _s5EditParts, _s5Reparents, _s5Enters, _s5Leaves, _s5Dropped3, _c2Rows, _c2Dropped, _c2DroppedTotal, _floatSent;
        private static double _readMs, _setMs, _c2Ms, _c2MsMax, _readMsMax;
        private static ulong _nextBudgetMs;
        private static long _budgetC3Bytes, _budgetC4Bytes;

        private static S5Settings LoadS5Config()
        {
            var c = new S5Settings();
            try
            {
                string v = Environment.GetEnvironmentVariable(S5Settings.EnvVar);
                if (!string.IsNullOrEmpty(v))
                {
                    bool? b = S5Args.Bool(v);
                    if (b.HasValue) c.Enabled = b.Value;
                }
            }
            catch (Exception) { }
            return c;
        }

        // ============================================================================================= postfix marks

 /// <summary>S10 hook (round 2 A2): the mirror's memory share for the s10.mem row: the model payloads kept for re-streams,
 /// the model entries, the C3 queue's pending bytes, the mirrored grids. Update thread.</summary>
        internal static void S10Memory(out long payloadBytes, out long models, out long c3PendingBytes, out long grids)
        {
            payloadBytes = 0;
            foreach (ModelEntry e in Models.Values) payloadBytes += e.PayloadBytes;
            models = Models.Count;
            c3PendingBytes = C3 != null ? C3.PendingBytes : 0;
            grids = Grids.Count;
        }

        /// <summary>The <c>RebuildInstanceParts</c> postfix (any thread): the cell is read again on the update thread.</summary>
        public static void MarkCellFromHook(MyCubeGridRenderCell cell)
        {
            if (cell != null && S5Config.Enabled && Gate.Streaming) CellMarks.Enqueue(cell);
        }

        /// <summary>The <c>ChangeColorAndSkin</c> postfix (paint and skin): the block is read again on the update thread.</summary>
        public static void MarkBlockFromHook(MySlimBlock block)
        {
            if (block != null && S5Config.Enabled && Gate.Streaming) BlockMarks.Enqueue(block);
        }

        // ============================================================================================= per tick

        private static void S5PreTick(ulong nowMs, Link link, uint worldEpoch)
        {
            MySession s = MySession.Static;
            _tick = s != null ? (ulong)(uint)s.GameplayFrameCounter : 0;
            S5Log.Tick = _tick;
            if (S5Config.Enabled != _s5Applied) S5ApplyMode(nowMs, worldEpoch);
            if (!S5Active) return;
            GridExporter.Emit = S5Emit;
            GridExporter.Models = Resolver;
            DrainMarks();
            long t0 = Stopwatch.GetTimestamp();
            MyCamera camera = MySector.MainCamera;
            if (camera != null)
            {
                bool allowAdd = Gate.Streaming && FrameWriter.HasC5 && FrameWriter.C5Zone <= ZoneOuterVoid;
                double enterM = S5Config.EnterM, leaveM = S5Config.LeaveM;
                PictureHooks.EntityRadii(ref enterM, ref leaveM); // S7a hook: 1,000/1,050 m above water under the Site picture policy
                EntitySet.Tracker.EnterM = enterM;
                EntitySet.Tracker.LeaveM = leaveM;
                EntitySet.Tracker.KeepMs = (ulong)(S5Config.KeepS * 1000);
                EntitySet.Tracker.MaxEntities = S5Config.MaxEntities;
                EntitySet.Tick(nowMs, camera.WorldMatrix.Translation, allowAdd, _added, _removed);
                foreach (var kv in _removed) if (!S7tDemote(kv.Key, kv.Value)) UnregisterS5(kv.Key, kv.Value); // S7t-se-composite (T3): a root still in the vertical reach stays as an impostor
                foreach (long id in _added)
                {
                    MyEntity e = EntitySet.EntityOf(id);
                    if (e != null) RegisterS5(e, "entity set", false, null);
                }
                S7pPreTick(nowMs, camera.WorldMatrix.Translation, allowAdd); // S7p-scene-above: projections pruned, missiles in/out (GridMirrorProducer.S7p.cs)
                S7tPreTick(nowMs, camera.WorldMatrix.Translation, allowAdd); // S7t-se-composite (T3): impostors above the picture camera (GridMirrorProducer.S7t.cs)
            }
            _setMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>C5 zones that allow new entities: CORE 0, TILE VOID 1, OUTER VOID 2.</summary>
        private const uint ZoneOuterVoid = 2;

        private static void DrainMarks()
        {
            MyCubeGridRenderCell cell;
            int n = 0;
            while (n++ < 4096 && CellMarks.TryDequeue(out cell))
            {
                MyCubeGrid grid = null;
                try { grid = cell.m_gridRenderComponent != null ? cell.m_gridRenderComponent.CubeGrid : null; } catch (Exception) { }
                MirroredGrid g;
                if (grid != null && Grids.TryGetValue(grid.EntityId, out g) && g.S5 != null) GridExporter.MarkCell(g.S5, cell, _tick);
            }
            MySlimBlock b;
            n = 0;
            while (n++ < 4096 && BlockMarks.TryDequeue(out b))
            {
                MyCubeGrid grid = b.CubeGrid;
                MirroredGrid g;
                if (grid != null && Grids.TryGetValue(grid.EntityId, out g) && g.S5 != null) GridExporter.MarkBlock(g.S5, b, false);
            }
        }

        /// <summary>S5 switched on or off at run time: registrations converted and, while streaming, everything resent.</summary>
        private static void S5ApplyMode(ulong nowMs, uint worldEpoch)
        {
            bool on = S5Config.Enabled;
            _s5Applied = on;
            var drop = new List<long>();
            foreach (MirroredGrid g in Grids.Values)
            {
                if (on)
                {
                    if (g.Grid != null && g.S5 == null) { g.S5 = GridExporter.Attach(g.Grid); g.Pinned = true; EntitySet.Tracker.Pin(g.Id, g.Id, S5EntityKind.Grid); }
                }
                else
                {
                    if (g.S5 != null) { GridExporter.Detach(g.S5); g.S5 = null; }
                    if (!g.Pinned || g.Floating) drop.Add(g.Id);
                }
            }
            foreach (long id in drop) { MirroredGrid g = Grids[id]; Unhook(g); Grids.Remove(id); }
            if (!on) { EntitySet.Tracker.Clear(); CharacterExport.Forget("S5 off"); }
            DeLog.Info("DE.S5 mode " + (on ? "ON (every grid near the camera, the character)" : "OFF (S3 path: de.s3 registrations only)") +
                       (Gate.Streaming ? "; resending everything" : ""));
            if (Gate.Streaming && _link != null) StartStream(nowMs, "S5 mode " + (on ? "on" : "off"), worldEpoch, _link.LinkEpoch);
        }

        private static void S5Forget(string why)
        {
            EntitySet.Tracker.Clear();
            CharacterExport.Forget(why);
            Combat.CollisionSkin.Forget(why); // S6
            PlacementGhost.OnWorldUnload(why); // S7a
            S7Host.OnWorldForgotten(why); // S7: interior holds, room handlers, the effects' grid index
            S7pForget(why); // S7p-scene-above: spin state, projection/missile counts
            S7tForget(why); // S7t-se-composite (T3): the impostor query state
            _ordered.Clear();
            Resolver.Clear();
            MyCubeGridRenderCell c;
            while (CellMarks.TryDequeue(out c)) { }
            MySlimBlock b;
            while (BlockMarks.TryDequeue(out b)) { }
            if (Recorder.Active) Recorder.Stop("world forgotten: " + why);
        }

        private static void S5Forgotten(long id, string why)
        {
            Combat.CollisionSkin.Forget(id); // S6
            if (EntitySet.Tracker.Remove(id))
            {
                _s5Leaves++;
                GridRow("remove", id, 0, why, null);
            }
        }

        // ============================================================================================= registry

 /// <summary>de.s3 registered a grid while S5 is on: it gets the S5 state and stays mirrored (pinned) whatever its distance.</summary>
        private static void S5AttachRegistered(MirroredGrid g)
        {
            if (!S5Active || g.Grid == null) return;
            if (g.S5 == null) g.S5 = GridExporter.Attach(g.Grid);
            g.Pinned = true;
            g.Large = g.Grid.GridSizeEnum == VRage.Game.MyCubeSize.Large;
            EntitySet.Tracker.Pin(g.Id, g.Id, S5EntityKind.Grid);
        }

        private static void RegisterS5(MyEntity e, string why, bool pinned, S3GridInfo info)
        {
            MirroredGrid g;
            if (Grids.TryGetValue(e.EntityId, out g)) { if (pinned) g.Pinned = true; return; }
            g = new MirroredGrid { Id = e.EntityId, Why = why, Info = info ?? new S3GridInfo(), Pinned = pinned };
            g.OnClose = x => { g.Closing = true; };
            var grid = e as MyCubeGrid;
            if (grid != null)
            {
                g.Grid = grid;
                g.Large = grid.GridSizeEnum == VRage.Game.MyCubeSize.Large;
                g.S5 = GridExporter.Attach(grid);
                g.OnBlock = b => { g.BlockEvent = true; };
                try { grid.OnClose += g.OnClose; } catch (Exception) { }
            }
            else
            {
                g.Entity = e;
                g.Floating = true;
                try { e.OnClose += g.OnClose; } catch (Exception) { }
            }
            Grids[g.Id] = g;
            S7pOnRegistered(g); // S7p-scene-above: an admitted preview grid is a projection (P7)
            _s5Enters++;
            EntitySetTracker.Member m;
            long root = EntitySet.Tracker.TryGet(g.Id, out m) ? m.RootId : g.Id;
            GridRow("enter", g.Id, root, why, g);
        }

        private static void UnregisterS5(long id, string why)
        {
            MirroredGrid g;
            if (!Grids.TryGetValue(id, out g)) return;
            Unhook(g);
            if (g.S5 != null) GridExporter.Detach(g.S5);
            Grids.Remove(id);
            int purged = C4.Purge(id);
            if (Gate.Streaming && g.GridAddIndex >= 0) C4.Enqueue(C4Lanes.Control, S3Codec.EntityRemoveMsg(id));
            _s5Leaves++;
            GridRow("leave", id, g.ParentId, why + (purged > 0 ? " (" + purged + " queued record(s) dropped)" : ""), g);
        }

        // ============================================================================================= grids

        private static void S5UpdateGrids(ulong nowMs, uint worldEpoch)
        {
            MyCamera camera = MySector.MainCamera;
            Vector3D cam = camera != null ? camera.WorldMatrix.Translation : Vector3D.Zero;
            _ordered.Clear();
            foreach (MirroredGrid g in Grids.Values)
            {
                MyEntity e = g.Grid != null ? (MyEntity)g.Grid : g.Entity;
                if (e == null || e.Closed) continue;
                BoundingBoxD box = e.PositionComp.WorldAABB;
                g.Distance = EntitySetTracker.BoxDistance(cam.X, cam.Y, cam.Z, box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
                if (g.S5 != null) g.S5.Distance = g.Distance;
                _ordered.Add(g);
            }
            _ordered.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));

            long t0 = Stopwatch.GetTimestamp();
            long deadline = t0 + (long)(S5Config.ReadBudgetMs * Stopwatch.Frequency / 1000.0);
            // S7p-gameplay (P14): a read that overran its deadline (one render cell of a workshop ship: 25-83 ms) is paid back by
 // skipping the reads of the next ticks (ExportReadDebt: average <= readAvgMs; the stream is late, never a stutter train)
            bool s7pRead = S7pReadDebt.Allow();
            if (!s7pRead) deadline = t0;
            foreach (MirroredGrid g in _ordered)
            {
                if (g.Floating) { UpdateFloating(nowMs, g, worldEpoch); continue; }
                if (g.S5 == null) continue;
                SendGridAdd(g, worldEpoch);
                if (!g.S5.FirstPassStarted) GridExporter.StartFirstPass(g.S5, _tick);
                if (Stopwatch.GetTimestamp() < deadline) GridExporter.Process(g.S5, _tick, deadline, true);
                if (g.PlaceAwaitingModels && ModelsSettled(g)) { g.PlaceAwaitingModels = false; g.PlacePending = true; }
            }
            if (_ordered.Count > 0 && s7pRead) // S7p-gameplay (P14): no sweep or motion read on a paid-back tick
            {
                // the sweep is a safety net (the hooks report edits): one grid per tick in rotation, a few of its blocks, and
 // one of its render cells every CellSweepTicks ticks
                MirroredGrid sg = _ordered[(int)(_sweepGrid++ % (ulong)_ordered.Count)];
                if (sg.S5 != null) GridExporter.Sweep(sg.S5, _tick, S5Defaults.BlockSweepPerTick, _tick % S5Defaults.CellSweepTicks == 0 ? 1 : 0);
                // motion detection within its per-tick budget, the starting grid rotating (no grid starves)
                int budget = S5Defaults.MotionChecksPerTick;
                int start = (int)(_motionGrid++ % (ulong)_ordered.Count);
                for (int i = 0; i < _ordered.Count && budget > 0; i++)
                {
                    MirroredGrid g = _ordered[(start + i) % _ordered.Count];
                    if (g.S5 == null) continue;
                    budget -= GridExporter.DetectMotion(g.S5, ref g.MotionCursor, budget, _tick);
                }
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _readMs = ms;
            if (ms > _readMsMax) _readMsMax = ms;
            if (s7pRead) S7pReadDebt.Spent(ms); // S7p-gameplay (P14)
            if (ms > _s7pReadMsMax) _s7pReadMsMax = ms;
            S10Se.Add(S10PerfRules.SeStageExport, t0); // S10 hook: the export reads, sweep and motion checks of this tick
            List<MirroredGrid> solid = S7pSolid(_ordered); // S7p-scene-above: no skin, no room set for a projection or a missile
            long s10 = S10Se.T(); // S10 hook (part 3): skins timed, and built only for grids near the camera (S10Se.NearOnly)
            Combat.CollisionSkin.Tick(S10Se.NearOnly(solid), nowMs, _tick, C4, worldEpoch); // S6: collision skins (C4 SkinSet), one grid built at a time
            S10Se.Add(S10PerfRules.SeStageSkin, s10);
            s10 = S10Se.T();
            InteriorRooms.Tick(solid, nowMs, _tick, Gate.Streaming); // S7: airtight rooms as C4 RoomSet on lane Edit (InteriorRooms.cs)
            S10Se.Add(S10PerfRules.SeStageRooms, s10); // S10 hook: timed
            if (S5Config.Character) CharacterExport.Tick(nowMs, worldEpoch);
            else CharacterExport.Forget("character export off");
            // S7a: the placement ghost (never an entry of Grids: no s5.* row, no S6 skin); active only while the C5 zone allows mirrored entities
            PlacementGhost.Tick(_tick, nowMs, worldEpoch, Gate.Streaming && FrameWriter.HasC5 && FrameWriter.C5Zone <= ZoneOuterVoid);
        }

        /// <summary>GridAdd (lane 1): the first one, and again on a re-parent or a flag change (hierarchy, static).</summary>
        private static void SendGridAdd(MirroredGrid g, uint worldEpoch)
        {
            MyCubeGrid grid = g.Grid;
            long parent = EntitySet.RootOf(grid);
            if (parent == g.Id) parent = 0;
            bool subgrid = parent != 0;
            uint flags = S7pScene.GridFlags(S5Conv.Grid(grid.IsStatic, subgrid, false), g.Projection, false); // S7p-scene-above: Projection bit 4 (P7)
            flags = S7tImpostorRules.GridFlags(flags, g.Impostor || S10Se.PreviewImpostor(grid)); // S7t-se-composite (T3): Impostor bit 6; a change re-sends the GridAdd (upgrade / demotion); S10 hook: a paste preview over the cap
            g.Subgrid = subgrid;
            g.ParentId = parent;
            if (g.GridAddIndex >= 0 && flags == g.SentGridFlags && parent == g.SentParentId) return;
            // the hierarchy root's GridAdd goes first (a dock or an attach can make a newly mirrored grid the root)
            MirroredGrid pg;
            if (parent != 0 && Grids.TryGetValue(parent, out pg) && pg.Grid != null && pg.GridAddIndex < 0 && EntitySet.RootOf(pg.Grid) == parent) SendGridAdd(pg, worldEpoch);
            bool first = g.GridAddIndex < 0;
            g.GridAddIndex = C4.Enqueue(C4Lanes.Control, S3Codec.GridAddMsg(g.Id, g.Large, parent, worldEpoch, flags));
            g.SentGridFlags = flags;
            g.SentParentId = parent;
            if (!first)
            {
                _s5Reparents++;
                S5Log.Edit("reparent", g.Id, parent);
            }
            GridRow(first ? "gridAdd" : "reparent", g.Id, parent, null, g);
        }

        /// <summary>A floating object: GridAdd {targetClass 2, FloatingObject} and one PartAdd class 7, key 1, identity.</summary>
        private static void UpdateFloating(ulong nowMs, MirroredGrid g, uint worldEpoch)
        {
            if (g.GridAddIndex < 0)
            {
                uint ff = S7pScene.GridFlags(S5Conv.Grid(false, false, true), false, g.Missile); // S7p-scene-above: Missile bit 5 (P1)
                g.GridAddIndex = C4.Enqueue(C4Lanes.Control, S3Codec.GridAddMsg(g.Id, false, 0, worldEpoch, ff));
                g.SentGridFlags = ff;
                GridRow("gridAdd", g.Id, 0, null, g);
            }
            if (g.FloatingSent) return;
            MyModel model = g.Entity != null ? g.Entity.Model : null;
            if (model == null) return;
            ulong key;
            string asset;
            float scale;
            uint skinId;
            bool metal;
            if (!Resolver.Resolve(model, MyStringHash.NullOrEmpty, out key, out asset, out scale, out skinId, out metal)) return;
            var p = new PartSnapshot
            {
                PartKey = S5Conv.FloatingPartKey,
                ModelKey = key,
                AssetName = asset,
                ModelScale = scale,
                Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
                Flags = S5Conv.Part(S5Conv.PartClass.FloatingObject, 0),
            };
            EnsureModel(nowMs, p);
            C4.Enqueue(C4Lanes.FirstSend, S3Codec.PartAddMsg(g.Id, p));
            g.Parts.Held[p.PartKey] = p;
            g.FloatingSent = true;
            g.PartsVersion++;
            _partAdds++;
            _floatSent++;
        }

        /// <summary>GridExporter's diff → C4 records in the entity's lane (CONTRACT §5.4 order).</summary>
        private static void S5Emit(S5GridState st, List<PartSnapshot> adds, List<PartSnapshot> replaces, List<PartSnapshot> removes, List<PartSnapshot> recolors,
                                   string source, ulong eventTick, bool firstPass)
        {
            MirroredGrid g;
            if (!Grids.TryGetValue(st.Id, out g)) return;
            if (S7tFilter(g, adds, replaces, removes, recolors)) return; // S7t-se-composite (T3): an impostor's edges and subparts are held back
            int lane = firstPass ? C4Lanes.FirstSend : st.Lanes.Choose(C4, g.Distance <= S5Defaults.NearEditM);
            ulong now = Win32.TickCount64();
            foreach (PartSnapshot p in removes)
            {
                if (S5Mutants.DropPartRemove) { _s5Dropped3++; _partRemoves++; continue; } // S5.M3: counted as sent, never written
                st.Lanes.Note(C4.Enqueue(lane, S3Codec.PartRemoveMsg(g.Id, p.PartKey)));
                _partRemoves++;
            }
            foreach (PartSnapshot p in adds) { EnsureModel(now, p); st.Lanes.Note(C4.Enqueue(lane, S3Codec.PartAddMsg(g.Id, p))); _partAdds++; }
            foreach (PartSnapshot p in replaces) { EnsureModel(now, p); st.Lanes.Note(C4.Enqueue(lane, S3Codec.PartAddMsg(g.Id, p))); _partAdds++; }
            foreach (PartSnapshot p in recolors) { st.Lanes.Note(C4.Enqueue(lane, S3Codec.PartRecolorMsg(g.Id, p))); _partRecolors++; }
            g.PartsVersion++;
            if (g.Pinned && g.Info != null && g.Info.Spawned) g.PlaceAwaitingModels = true; // s3.place rows of de.s3 lab grids only
            if (g.Pinned)
            {
                // de.s3 grids keep their s3.part rows (tests/s3/analyze_s3.py reads them in S3 sessions run with S5 on)
                string utc = Utc();
                foreach (PartSnapshot p in removes) Jsonl.WriteLineBuffered(S3Rows.Part(utc, "remove", g.Id, p));
                foreach (PartSnapshot p in adds) Jsonl.WriteLineBuffered(S3Rows.Part(utc, "add", g.Id, p));
                foreach (PartSnapshot p in replaces) Jsonl.WriteLineBuffered(S3Rows.Part(utc, "add", g.Id, p));
                foreach (PartSnapshot p in recolors) Jsonl.WriteLineBuffered(S3Rows.Part(utc, "recolor", g.Id, p));
            }
            if (firstPass) return;
            _s5Edits++;
            _s5EditParts += adds.Count + replaces.Count + removes.Count + recolors.Count;
            var sb = S5Rows.Head(Utc(), "edit");
            S5Rows.Str(sb, "kind", source);
            S5Rows.Int(sb, "tick", (long)_tick);
            S5Rows.Int(sb, "eventTick", (long)eventTick);
            S5Rows.Id(sb, "entityId", g.Id);
            S5Rows.Int(sb, "lane", lane);
            S5Rows.Int(sb, "adds", adds.Count);
            S5Rows.Int(sb, "replaces", replaces.Count);
            S5Rows.Int(sb, "removes", removes.Count);
            S5Rows.Int(sb, "recolors", recolors.Count);
            S5Rows.Int(sb, "parts", st.Held.Count);
            S5Rows.Bool(sb, "removeDropped", S5Mutants.DropPartRemove && removes.Count > 0);
            sb.Append(",\"keys\":[");
            int k = 0;
            foreach (List<PartSnapshot> l in new[] { adds, replaces, removes, recolors })
                foreach (PartSnapshot p in l)
                {
                    if (k >= 8) break;
                    if (k++ > 0) sb.Append(',');
                    sb.Append("\"0x").Append(p.PartKey.ToString("x16", CultureInfo.InvariantCulture)).Append('"');
                }
            sb.Append(']');
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ============================================================================================= models

 /// <summary>A skin variant part: its entry is created and built from the loaded base model on the worker. True = handled.</summary>
        private static bool S5EnsureVariant(ulong nowMs, PartSnapshot p)
        {
            ModelResolver.Variant v;
            if (!Resolver.Variants.TryGetValue(p.ModelKey, out v)) return false;
            ModelEntry e;
            if (Models.TryGetValue(p.ModelKey, out e))
            {
                if (e.State == 1 && !e.SentThisStream) QueueModel(e);
                return true;
            }
            ModelEntry b;
            if (!Models.TryGetValue(v.BaseKey, out b) || b.State != 1 || b.Model == null) return false; // not decidable: the normal path loads it plain
            e = new ModelEntry { Key = p.ModelKey, Asset = v.Asset, Scale = v.Scale, State = 0, RequestedMs = nowMs, Kind = ModelLoadRequest.KindSkinVariant, BaseKey = v.BaseKey, Skin = v.Changes, SkinText = v.SkinText };
            Models[p.ModelKey] = e;
            Loader.Request(new ModelLoadRequest
            {
                ModelKey = p.ModelKey, AssetName = v.Asset, Scale = v.Scale, WorldEpoch = _worldEpoch, Kind = ModelLoadRequest.KindSkinVariant,
                BaseModel = b.Model, Skin = v.Changes, SkinText = v.SkinText,
            });
            DeLog.Info("DE.S5 requested skin variant " + v.Asset + " |skin=" + v.SkinText + " key=" + S3Rows.Hex(p.ModelKey) + " (base " + S3Rows.Hex(v.BaseKey) + ")");
            return true;
        }

        // laptop: the first block of a type waited 0.4 s
 // for its model): every 5 s the models of the blocks on the player's toolbar (the finished block and its construction
 // stages, the model a Survival block shows first) are requested and sent to Subnautica before any is placed; each once
 // per world. DEEPENGINEERS_S5_PREWARM=off: none.
        public static bool PrewarmOn = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_S5_PREWARM"), "off", StringComparison.OrdinalIgnoreCase);
        private static readonly HashSet<string> _prewarmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static ulong _nextPrewarmMs;
        private static uint _prewarmEpoch;
        public static long PrewarmRequested;

        private static void PrewarmToolbar(ulong nowMs)
        {
            if (!PrewarmOn || nowMs < _nextPrewarmMs) return;
            _nextPrewarmMs = nowMs + 5000;
            try
            {
                if (_prewarmEpoch != _worldEpoch) { _prewarmed.Clear(); _prewarmEpoch = _worldEpoch; }
                var tb = Sandbox.Game.Screens.Helpers.MyToolbarComponent.CurrentToolbar;
                if (tb == null) return;
                int n = 0;
                for (int i = 0; i < tb.ItemCount; i++)
                {
                    var it = tb.GetItemAtIndex(i) as Sandbox.Game.Screens.Helpers.MyToolbarItemCubeBlock;
                    MyCubeBlockDefinition def = it != null ? it.BlockDefinition : null;
                    if (def == null) continue;
                    n += PrewarmAsset(nowMs, def.Model);
                    if (def.BuildProgressModels != null)
                        foreach (MyCubeBlockDefinition.BuildProgressModel bp in def.BuildProgressModels)
                            if (bp != null) n += PrewarmAsset(nowMs, bp.File);
                }
                if (n > 0) DeLog.Info("DE.S5 prewarm: " + n + " toolbar block model(s) requested and sent ahead of placement (" + _prewarmed.Count + " this world)");
            }
            catch (Exception e) { PrewarmOn = false; DeLog.Info("DE.S5 prewarm off after " + e.GetType().Name + ": " + e.Message); }
        }

        private static int PrewarmAsset(ulong nowMs, string asset)
        {
            if (string.IsNullOrEmpty(asset) || !_prewarmed.Add(asset)) return 0;
            ulong key;
            uint skinId;
            bool metal;
            if (!Resolver.ResolveAsset(asset, 1f, MyStringHash.NullOrEmpty, out key, out skinId, out metal)) return 0;
            EnsureModelFor(nowMs, new PartSnapshot { ModelKey = key, AssetName = asset, ModelScale = 1f });
            PrewarmRequested++;
            return 1;
        }

        /// <summary>A plain model entry requested (the resolver's base model of a skinned part, de.s5 models).</summary>
        private static ModelEntry RequestModel(ulong nowMs, ulong key, string asset, float scale, bool forced)
        {
            ModelEntry e;
            if (Models.TryGetValue(key, out e)) { if (forced) e.Forced = true; return e; }
            e = new ModelEntry { Key = key, Asset = asset, Scale = scale, State = 0, RequestedMs = nowMs, Forced = forced };
            Models[key] = e;
            Loader.Request(new ModelLoadRequest { ModelKey = key, AssetName = asset, Scale = scale, WorldEpoch = _worldEpoch });
            return e;
        }

        /// <summary>A model finished: grids with parts deferred on it are read again.</summary>
        private static void S5OnModelReady(ModelEntry e)
        {
            if (!S5Active) return;
            if (Resolver.WaitingBase.Remove(e.Key))
                foreach (MirroredGrid g in Grids.Values)
                    if (g.S5 != null) GridExporter.OnModelReady(g.S5, _tick);
        }

        private static void S5ModelRow(ModelLoadResult r, ModelEntry e)
        {
            if (!S5Active) return;
            var sb = S5Rows.Head(Utc(), "model");
            S5Rows.Hex(sb, "modelKey", r.Request.ModelKey);
            S5Rows.Str(sb, "asset", r.Request.AssetName);
            S5Rows.Int(sb, "kind", r.Request.Kind);
            if (r.Request.SkinText != null) S5Rows.Str(sb, "skin", r.Request.SkinText);
            if (r.Error != null) S5Rows.Str(sb, "error", r.Error);
            if (r.Model != null)
            {
                S5Rows.Int(sb, "verts", r.Model.VertexCount);
                S5Rows.Int(sb, "tris", r.Model.TriangleCount);
                S5Rows.Int(sb, "submeshes", r.Model.Submeshes != null ? r.Model.Submeshes.Length : 0);
                S5Rows.Bool(sb, "skinned", r.Model.BoneIndices != null);
                // review F3: G1-E4 excludes glass and alpha-masked models (MyMeshDrawTechnique GLASS 15, ALPHA_MASKED 3, _SINGLE_SIDED 4, FOLIAGE 5)
                bool glass = false, alphaM = false;
                if (r.Model.Submeshes != null)
                    foreach (var sm in r.Model.Submeshes) { int t = (int)sm.DrawTechnique; if (t == 15) glass = true; if (t == 3 || t == 4 || t == 5) alphaM = true; }
                S5Rows.Bool(sb, "glass", glass);
                S5Rows.Bool(sb, "alphaMasked", alphaM);
                S5Rows.Num(sb, "loadMs", Math.Round(r.Model.LoadMs, 3));
                S5Rows.Int(sb, "bytes", r.PayloadBytes);
            }
            S5Rows.Bool(sb, "forced", e != null && e.Forced);
            Jsonl.WriteLine(S5Rows.End(sb));
        }

        /// <summary>
 /// The skin and model-key resolver (CONTRACT §5.4 "Model keys and skins"): base keys cached per <c>MyModel</c> instance
 /// (SE shares one per asset) and per asset text (edges); a skin's texture changes from
 /// <c>MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(MyStringHash)</c>
 /// the |skin= variant only
 /// when the skin changes a material of that model, decided once the base model is loaded (until then the part waits).
 /// </summary>
        internal sealed class ModelResolver : IS5ModelResolver
        {
            internal struct BaseInfo { public ulong Key; public string Asset; public float Scale; }
            internal sealed class SkinInfo { public string Name; public uint Id; public bool Metal; public Dictionary<string, SkinTextureChange> Changes; }
            internal sealed class Variant { public ulong BaseKey; public string Asset; public float Scale; public string SkinText; public Dictionary<string, SkinTextureChange> Changes; }

            private readonly Dictionary<MyModel, BaseInfo> _byModel = new Dictionary<MyModel, BaseInfo>();
            private readonly Dictionary<string, BaseInfo> _byAsset = new Dictionary<string, BaseInfo>(StringComparer.Ordinal);
            private readonly Dictionary<MyStringHash, SkinInfo> _skins = new Dictionary<MyStringHash, SkinInfo>();
            private readonly Dictionary<KeyValuePair<ulong, MyStringHash>, ulong> _decided = new Dictionary<KeyValuePair<ulong, MyStringHash>, ulong>();
            public readonly Dictionary<ulong, Variant> Variants = new Dictionary<ulong, Variant>();
            public readonly HashSet<ulong> WaitingBase = new HashSet<ulong>();

            public bool Resolve(MyModel model, MyStringHash skin, out ulong modelKey, out string asset, out float scale, out uint skinId, out bool skinMetalness)
            {
                BaseInfo b;
                if (!_byModel.TryGetValue(model, out b))
                {
                    b.Asset = model.AssetName;
                    b.Scale = model.ScaleFactor;
                    b.Key = S3Keys.ModelKey(b.Asset, 0, b.Scale);
                    _byModel[model] = b;
                }
                asset = b.Asset;
                scale = b.Scale;
                return Core(b, skin, out modelKey, out skinId, out skinMetalness);
            }

            public bool ResolveAsset(string asset, float scale, MyStringHash skin, out ulong modelKey, out uint skinId, out bool skinMetalness)
            {
                BaseInfo b;
                if (!_byAsset.TryGetValue(asset, out b))
                {
                    b.Asset = asset;
                    b.Scale = scale;
                    b.Key = S3Keys.ModelKey(asset, 0, scale);
                    _byAsset[asset] = b;
                }
                return Core(b, skin, out modelKey, out skinId, out skinMetalness);
            }

            private bool Core(BaseInfo b, MyStringHash skin, out ulong modelKey, out uint skinId, out bool metal)
            {
                modelKey = b.Key;
                skinId = 0;
                metal = false;
                if (skin == MyStringHash.NullOrEmpty) return true;
                SkinInfo si = Skin(skin);
                skinId = si.Id;
                metal = si.Metal;
                if (si.Changes == null || si.Changes.Count == 0) return true;
                ulong decided;
                var dk = new KeyValuePair<ulong, MyStringHash>(b.Key, skin);
                if (_decided.TryGetValue(dk, out decided)) { modelKey = decided; return true; }
                ModelEntry e;
                if (!Models.TryGetValue(b.Key, out e))
                {
                    RequestModel(Win32.TickCount64(), b.Key, b.Asset, b.Scale, false);
                    WaitingBase.Add(b.Key);
                    return false;
                }
                if (e.State == 0) { WaitingBase.Add(b.Key); return false; }
                if (e.State == 2 || e.Model == null) { _decided[dk] = b.Key; return true; }
                if (!SkinResolve.Overrides(e.Model, si.Changes)) { _decided[dk] = b.Key; return true; }
                ulong vk = S5Conv.ModelKey(b.Asset, 0, b.Scale, si.Name, null);
                Variants[vk] = new Variant { BaseKey = b.Key, Asset = b.Asset, Scale = b.Scale, SkinText = si.Name.ToLowerInvariant(), Changes = si.Changes };
                _decided[dk] = vk;
                modelKey = vk;
                return true;
            }

            public SkinInfo Skin(MyStringHash skin)
            {
                SkinInfo si;
                if (_skins.TryGetValue(skin, out si)) return si;
                si = new SkinInfo { Name = skin.String ?? "", Changes = new Dictionary<string, SkinTextureChange>(StringComparer.Ordinal) };
                si.Id = S5Conv.SkinId(si.Name);
                try
                {
                    MyDefinitionManager.MyAssetModifiers am = MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(skin);
                    si.Metal = am.MetalnessColorable;
                    if (am.SkinTextureChanges != null)
                        foreach (var kv in am.SkinTextureChanges)
                            si.Changes[kv.Key.String] = new SkinTextureChange
                            {
                                ColorMetal = kv.Value.ColorMetalFileName, NormalGloss = kv.Value.NormalGlossFileName,
                                Extensions = kv.Value.ExtensionsFileName, AlphaMask = kv.Value.AlphamaskFileName,
                            };
                }
                catch (Exception ex) { DeLog.Info("DE.S5 skin " + si.Name + ": asset modifiers not read (" + ex.GetType().Name + ": " + ex.Message + "); drawn unskinned"); }
                _skins[skin] = si;
                DeLog.Info("DE.S5 skin " + si.Name + " skinId=0x" + si.Id.ToString("x8", CultureInfo.InvariantCulture) + " materials=" + si.Changes.Count + " metalnessColorable=" + si.Metal);
                return si;
            }

            public void Clear()
            {
                _decided.Clear();
                WaitingBase.Clear();
            }
        }

        /// <summary>The skin changes of several asset modifiers merged in list order (the character), by subtype.</summary>
        internal static Dictionary<string, SkinTextureChange> SkinChanges(IList<MyStringHash> skins, out string text, out bool metal)
        {
            var list = new List<IDictionary<string, SkinTextureChange>>();
            var names = new List<string>();
            metal = false;
            foreach (MyStringHash h in skins)
            {
                ModelResolver.SkinInfo si = Resolver.Skin(h);
                if (si.Changes == null || si.Changes.Count == 0) continue;
                list.Add(si.Changes);
                names.Add(si.Name.ToLowerInvariant());
                metal |= si.Metal;
            }
            text = names.Count > 0 ? string.Join("+", names.ToArray()) : null;
            return SkinResolve.Merge(list);
        }

        // ============================================================================================= character hooks

        internal static long EnqueueControl(OutMessage m) { return C4.Enqueue(C4Lanes.Control, m); }
        /// <summary>S7a placement ghost: its part records in their lane (lane 2, NearEdit).</summary>
        internal static long EnqueueLane(int lane, OutMessage m) { return C4.Enqueue(lane, m); }
        /// <summary>S7a: C7 presentedSeTick (0 without a Subnautica status).</summary>
        internal static ulong SnPresentedTick { get { Link l = _link; return l != null && l.HasSnStatus ? l.LastSnStatus.presentedSeTick : 0UL; } }
        /// <summary>S7a: a model's state (-1 unknown, 0 requested, 1 ready, 2 failed) and whether it went over C3 in this stream.</summary>
        internal static int ModelStateOf(ulong key, out bool sentThisStream)
        {
            ModelEntry e;
            sentThisStream = false;
            if (!Models.TryGetValue(key, out e)) return -1;
            sentThisStream = e.SentThisStream;
            return e.State;
        }
        internal static bool IsC4Done(long ticket) { return C4.IsDone(ticket); }
        internal static void PurgeC4(long entity) { C4.Purge(entity); }
        internal static bool StreamingNow { get { return Gate.Streaming; } }
        internal static uint WorldEpochNow { get { return _worldEpoch; } }

        /// <summary>The character's C3 messages: its materials (once per stream) then each part's model messages.</summary>
        internal static void QueueCharacterModels(ExportedModel m, List<OutMessage> messages)
        {
            if (m == null || messages == null) return;
            foreach (MaterialInfo mi in m.Materials)
            {
                if (MaterialsSent.Contains(mi.Key)) continue;
                C3.Enqueue(S3Codec.MaterialDef(mi));
                MaterialsSent.Add(mi.Key);
            }
            C3.EnqueueRange(messages);
            _modelsQueued++;
        }

        internal static void RequestLoad(ModelLoadRequest r) { Loader.Request(r); }

        /// <summary>The model of a part outside the grids (the held tool): requested, and queued on C3 once loaded.</summary>
        internal static void EnsureModelFor(ulong nowMs, PartSnapshot p)
        {
            EnsureModel(nowMs, p);
            ModelEntry e;
            if (Models.TryGetValue(p.ModelKey, out e)) e.Forced = true;
        }

        /// <summary>The C1 record for the recorder (0x100 head + boneCount × 48 B), when recording with c1.</summary>
        internal static unsafe void RecordC1(ref SeFrame f, float[] bones, int boneCount)
        {
            if (!Recorder.Active || !Recorder.WantC1) return;
            int len = Derec.C1RecordBytes((uint)boneCount);
            if (_c1Pack.Length < len) _c1Pack = new byte[SeFrameLimits.BonesOffset + SeFrameLimits.MaxBones * SeFrameLimits.BoneSize];
            fixed (byte* b = _c1Pack)
            {
                *(SeFrame*)b = f;
                float* dst = (float*)(b + SeFrameLimits.BonesOffset);
                for (int i = 0; i < 12 * boneCount; i++) dst[i] = bones[i];
            }
            Recorder.RecordRaw(Derec.KindC1, _c1Pack, len, f.tick, f.qpcCapture);
        }

        private static byte[] _c1Pack = new byte[0];

        internal static ModelResolver.SkinInfo SkinOf(MyStringHash h) { return Resolver.Skin(h); }

        // ============================================================================================= ring writes

        private static bool C3ModelStartBlocked()
        {
            if (!S5Active) return false;
            Link l = _link;
            if (l == null) return false;
            ulong h3, t3, h4, t4;
            l.RingCounters(out h3, out t3, out h4, out t4);
            long free = RingFree(RegionId.C3SeModels, h3, t3);
            _c3FreeBytes = free;
            return free >= 0 && free < S5Defaults.C3LowFreeBytes;
        }

        private static void OnRingWrite(bool c3, uint type, byte[] payload, int count)
        {
            if (c3) _budgetC3Bytes += count + 8; else _budgetC4Bytes += count + 8;
            if (!Recorder.Active) return;
            Recorder.RecordMessage(c3 ? Derec.KindC3 : Derec.KindC4, type, payload, count, _tick, Win32.QpcNow());
        }

        // ============================================================================================= C2 (before C1)

        private static void S5OnSeFrame(Link link, ulong tick, long qpcCapture)
        {
            long t0 = Stopwatch.GetTimestamp();
            SiteMap site = SiteBinding.Map;
            MyCamera camera = MySector.MainCamera;
            if (site == null || camera == null) return;
            Vector3D ct = camera.WorldMatrix.Translation;
            var cam = new Vec3(ct.X, ct.Y, ct.Z);
            long cameraRoot = CameraGroupRoot();
            ulong now = Win32.TickCount64();
            bool tracing = now < _traceUntilMs;
            int n = 0;
            SeEntity ghost = default(SeEntity);
            if (PlacementGhost.TryGetC2Row(ref ghost, tick)) _entities[n++] = ghost; // S7a: the placement ghost's row first, never dropped
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.GridAddIndex < 0 || !C4.IsDone(g.GridAddIndex)) continue;
                if (g.Subgrid) continue; // a subgrid is a C2 row of its root (D8)
                MyEntity e = g.Grid != null ? (MyEntity)g.Grid : g.Entity;
                if (e == null || e.Closed) continue;
                if (n >= SeEntitiesLimits.MaxEntities) break;
                SeMatrix m = FrameWriter.ToSe(e.WorldMatrix);
                Vec3 pivot = g.Grid != null ? Pivot(g.Grid, m, cam) : OwnPivot(e, m, cam);
                float k = e.PositionComp.Scale ?? 1f;
                WireEntityPose pose = PoseMath.FromSe(site, m, pivot, k > 0 ? k : 1f);
                Vec3 v = Vec3.Zero, w = Vec3.Zero;
                if (e.Physics != null)
                {
                    Vector3 lv = e.Physics.LinearVelocity, av = e.Physics.AngularVelocity;
                    v = WireMath.Vel(site, pivot, new Vec3(lv.X, lv.Y, lv.Z));
                    w = WireMath.AngVel(site, pivot, new Vec3(av.X, av.Y, av.Z));
                }
                uint flags = 0;
                if (g.Grid != null && g.Grid.IsStatic) flags |= SeEntityFlags.Static;
                if (cameraRoot != 0 && cameraRoot == g.Id) flags |= 2u; // bit 1: in the camera's group
                var se = new SeEntity
                {
                    entityId = (ulong)g.Id,
                    kind = g.Floating ? SeEntityKind.FloatingObject : SeEntityKind.RootGrid,
                    flags = flags,
                    groupId = (ulong)g.Id,
                    v = new F32x3((float)v.X, (float)v.Y, (float)v.Z),
                    omega = new F32x3((float)w.X, (float)w.Y, (float)w.Z),
                    partsVersion = g.PartsVersion,
                    skinVersion = 0,
                };
                PoseMath.WriteWire(ref se, pose);
                if (n >= _entities.Length) Array.Resize(ref _entities, _entities.Length * 2);
                _entities[n++] = se;
                g.Prev = se; g.PrevM = m; g.PrevPivot = pivot; g.PrevCam = cam; g.HasPrev = true;
                if (g.Pinned && g.Grid != null) PoseRow(now, tick, g, g.Grid, m, pivot, cam, se, false, tracing);
                if (g.PlacePending && g.Grid != null) PlaceRow(tick, g, g.Grid, m, pivot, pose);
            }
            int dropped;
            int rows = SubgridPoses.Build(Grids, C4, cam, _rows, out dropped, now < _s5TraceUntilMs, tick);
            _c2Rows = rows;
            _c2Dropped = dropped;
            _c2DroppedTotal += dropped;
            // S7 (B2 hooks, CONTRACT §5.4 "C2 lights (S7)", "C2 effect events"): the light rows (S7Lights.Fill) and this
 // tick's effect window (S7Effects.ForC2: sources polled, tick closed), written with C2 in the same seqlock window.
            int s7LightCount = 0;
            uint s7LightsFound = 0;
            S7Lights.Fill(_s7Lights, out s7LightCount, out s7LightsFound); // S7 lights (LightTable.cs): the nearest 64 within 60 m; never throws
            C2Ext s7Ext;
            int s7Events = S7Effects.ForC2(tick, ct, Gate.Streaming, link.SeGeneration, Gate.Starts, S7Conventions.C2ExtFlags.Lights, s7LightsFound, out s7Ext);
            if (link.WriteSeEntities(tick, _worldEpoch, _entities, n, _rows, rows, _s7Lights, s7LightCount, S7Effects.Rows, s7Events, s7Ext))
            {
                _c2Writes++;
                _c2Entities += n;
                if (Recorder.Active && Recorder.WantC2) Recorder.RecordRaw(Derec.KindC2, PackC2(tick, n, rows), _c2PackLen, tick, qpcCapture);
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _c2Ms = ms;
            if (ms > _c2MsMax) _c2MsMax = ms;
        }

        private static byte[] _c2Pack = new byte[0];
        private static int _c2PackLen;
        /// <summary>S7 (B2 hook): the C2 light rows of this tick (filled by B2 lights).</summary>
        private static readonly SeLight[] _s7Lights = new SeLight[SeEntitiesLimits.MaxLights];

        /// <summary>S7 (B2 hook): the grid's GridAdd is in Subnautica's ring (its view exists there): effects and lights attach to it.</summary>
        internal static bool SentToSn(long id)
        {
            MirroredGrid g;
            return Grids.TryGetValue(id, out g) && g.GridAddIndex >= 0 && C4.IsDone(g.GridAddIndex);
        }

        /// <summary>The packed C2 snapshot of the recorder (0x40 header, entities, subparts, no lights).</summary>
        private static unsafe byte[] PackC2(ulong tick, int n, int rows)
        {
            int len = Derec.C2SnapshotBytes((uint)n, (uint)rows, 0);
            if (_c2Pack.Length < len) _c2Pack = new byte[Math.Max(len, _c2Pack.Length * 2)];
            _c2PackLen = len;
            fixed (byte* b = _c2Pack)
            {
                var h = (SeEntitiesHeader*)b;
                *h = default(SeEntitiesHeader);
                h->entityCount = (uint)n;
                h->tick = tick;
                h->worldEpoch = _worldEpoch;
                h->subpartCount = (uint)rows;
                h->lightCount = 0;
                var e = (SeEntity*)(b + SeEntitiesLimits.EntitiesOffset);
                for (int i = 0; i < n; i++) e[i] = _entities[i];
                var s = (SeSubpart*)(b + SeEntitiesLimits.EntitiesOffset + n * SeEntitiesLimits.EntitySize);
                for (int i = 0; i < rows; i++) s[i] = _rows[i];
            }
            return _c2Pack;
        }

        /// <summary>P* of a floating object: the point of its own oriented box nearest to the camera.</summary>
        private static Vec3 OwnPivot(MyEntity e, SeMatrix m, Vec3 cam)
        {
            BoundingBox b = e.PositionComp.LocalAABB;
            _boxM[0] = m;
            _boxMin[0] = new Vec3(b.Min.X, b.Min.Y, b.Min.Z);
            _boxMax[0] = new Vec3(b.Max.X, b.Max.Y, b.Max.Z);
            return WireMath.PivotNearest(_boxM, _boxMin, _boxMax, 1, cam);
        }

        /// <summary>The physical root of the grid the local character sits in (0 when not seated).</summary>
        private static long CameraGroupRoot()
        {
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || !c.IsSitting) return 0;
            var block = c.Parent as MyCubeBlock;
            MyCubeGrid grid = block != null ? block.CubeGrid : null;
            if (grid == null) return 0;
            return EntitySet.RootOf(grid);
        }

        // ============================================================================================= coherence (S5.N4)

 /// <summary>de.s5 coh: s5.coh rows (SE's pixel of a grid-fixed point through this frame's camera) for <paramref name="seconds"/>.</summary>
        internal static string StartCoh(long entityId, Vec3? local, double seconds)
        {
            MirroredGrid g;
            if (!Grids.TryGetValue(entityId, out g) || g.Grid == null) return "entity " + entityId + " is not a mirrored grid";
            _cohEntity = entityId;
            _cohHasPoint = local.HasValue;
            _cohLocal = local ?? Vec3.Zero;
            _cohUntilMs = Win32.TickCount64() + (ulong)(Math.Max(0, Math.Min(600, seconds)) * 1000);
            return null;
        }

        private static void S5CohRows()
        {
            if (_cohEntity == 0 || Win32.TickCount64() >= _cohUntilMs) return;
            MirroredGrid g;
            MyCamera camera = MySector.MainCamera;
            MySession s = MySession.Static;
            if (camera == null || s == null || !Grids.TryGetValue(_cohEntity, out g) || g.Grid == null) return;
            Vec3 local = _cohLocal;
            if (!_cohHasPoint)
            {
                // default: the seat the local character sits in on that grid, else the grid origin
                MyCharacter c = s.LocalCharacter;
                var seat = c != null && c.IsSitting ? c.Parent as MyCubeBlock : null;
                if (seat != null && seat.CubeGrid == g.Grid)
                {
                    // S5 review F11: 3 m in front of the seat, never at or behind the camera (a point within 0.5 m of the
 // camera moves about 1.5 px per mm and says nothing about the ship)
                    Matrix lm = seat.PositionComp.LocalMatrixRef;
                    Vector3 t = lm.Translation + lm.Forward * 3f;
                    local = new Vec3(t.X, t.Y, t.Z);
                }
            }
            MatrixD wm = g.Grid.WorldMatrix;
            Vector3D p = Vector3D.Transform(new Vector3D(local.X, local.Y, local.Z), ref wm);
            MatrixD vpm = camera.ViewProjectionMatrix;
            double[] vp = _gpsVp;
            vp[0] = vpm.M11; vp[1] = vpm.M12; vp[2] = vpm.M13; vp[3] = vpm.M14; vp[4] = vpm.M21; vp[5] = vpm.M22; vp[6] = vpm.M23; vp[7] = vpm.M24;
            vp[8] = vpm.M31; vp[9] = vpm.M32; vp[10] = vpm.M33; vp[11] = vpm.M34; vp[12] = vpm.M41; vp[13] = vpm.M42; vp[14] = vpm.M43; vp[15] = vpm.M44;
            var vport = camera.Viewport;
            double px, py, ndcZ;
            bool front = S1Math.Project(vp, vport.OffsetX, vport.OffsetY, vport.Width, vport.Height, new Vec3(p.X, p.Y, p.Z), out px, out py, out ndcZ);
            var sb = S5Rows.Head(Utc(), "coh");
            S5Rows.Int(sb, "tick", (long)(uint)s.GameplayFrameCounter);
            S5Rows.Int(sb, "qpc", Win32.QpcNow());
            S5Rows.Id(sb, "entityId", g.Id);
            sb.Append(",\"px\":[").Append(S3Rows.G(px)).Append(',').Append(S3Rows.G(py)).Append(']');
            S5Rows.Bool(sb, "inFront", front);
            S5Rows.Vec(sb, "local", local);
            S5Rows.Vec(sb, "pointSe", new Vec3(p.X, p.Y, p.Z));
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ============================================================================================= commands

 /// <summary>A recording starts: everything is sent again (ClearAll, models, parts) so the.derec file is complete.</summary>
        internal static string RestartForRecording(string name)
        {
            if (!Gate.Streaming || _link == null) return "not streaming yet (the recording starts with the stream's ClearAll)";
            StartStream(Win32.TickCount64(), "de.s5 record " + name, _worldEpoch, _link.LinkEpoch);
            return null;
        }

        internal static void S5Trace(double seconds)
        {
            _s5TraceUntilMs = Win32.TickCount64() + (ulong)(Math.Max(0, Math.Min(600, seconds)) * 1000);
            CharacterExport.TraceUntilMs = _s5TraceUntilMs;
        }

        /// <summary>de.s5 export: s5.part rows of up to <paramref name="sample"/> random parts of an entity (S5.N1). Returns null or why not.</summary>
        internal static string ExportParts(long entityId, int sample, int seed, out int written)
        {
            written = 0;
            MirroredGrid g;
            if (!Grids.TryGetValue(entityId, out g)) return "entity " + entityId + " is not mirrored";
            SiteMap site = SiteBinding.Map;
            MyCamera camera = MySector.MainCamera;
            MySession s = MySession.Static;
            if (site == null || camera == null || s == null) return "no site binding or camera";
            MyEntity owner = g.Grid != null ? (MyEntity)g.Grid : g.Entity;
            if (owner == null || owner.Closed) return "entity closed";
            // the root's matrices and pose (a subgrid's parts are placed through its root and the relative matrix)
            MirroredGrid root = g;
            if (g.Subgrid && !Grids.TryGetValue(g.ParentId, out root)) return "the subgrid's root " + g.ParentId + " is not mirrored";
            MyEntity rootE = root.Grid != null ? (MyEntity)root.Grid : root.Entity;
            Vector3D ct = camera.WorldMatrix.Translation;
            var cam = new Vec3(ct.X, ct.Y, ct.Z);
            SeMatrix rootM = FrameWriter.ToSe(rootE.WorldMatrix);
            Vec3 pivot = root.Grid != null ? Pivot(root.Grid, rootM, cam) : OwnPivot(rootE, rootM, cam);
            float k = rootE.PositionComp.Scale ?? 1f;
            WireEntityPose pose = PoseMath.FromSe(site, rootM, pivot, k > 0 ? k : 1f);
            SeMatrix ownerM = FrameWriter.ToSe(owner.WorldMatrix);
            SeMatrix? rel = null;
            if (g.Subgrid) rel = SubgridPoses.Relative(owner, rootE);
            var parts = new List<PartSnapshot>(HeldOf(g).Values);
            parts.Sort((a, b) => a.PartKey.CompareTo(b.PartKey));
            var rnd = new Random(seed);
            for (int i = parts.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); PartSnapshot t = parts[i]; parts[i] = parts[j]; parts[j] = t; }
            ulong tick = (ulong)(uint)s.GameplayFrameCounter;
            string utc = Utc();
            int n = Math.Min(sample <= 0 ? parts.Count : sample, parts.Count);
            for (int i = 0; i < n; i++)
            {
                Jsonl.WriteLineBuffered(S5Rows.Part(utc, tick, g.Id, root.Id, parts[i], ownerM, rootM, rel, pivot, pose, site, "sample"));
                written++;
            }
            // S5.N1.C1: SE's per-grid count at the time of the export, the held part set the
 // consumer audit compares (tests/s5/analyze_s5.py reads s5.grid rows that carry "parts")
            var cb = S5Rows.Head(utc, "grid");
            S5Rows.Str(cb, "op", "count");
            S5Rows.Str(cb, "label", "count");
            S5Rows.Int(cb, "tick", (long)tick);
            S5Rows.Id(cb, "entityId", g.Id);
            S5Rows.Id(cb, "parentId", g.Subgrid ? g.ParentId : 0);
            S5Rows.Int(cb, "parts", parts.Count);
            Jsonl.WriteLineBuffered(S5Rows.End(cb));
            Jsonl.Flush();
            return null;
        }

        /// <summary>de.s5 models: the first <paramref name="count"/> distinct cube block models (sorted by definition id, the census models first), loaded and, when asked, sent.</summary>
        internal static string RequestModelSet(int count, bool send, string[] mustInclude, out int requested)
        {
            requested = 0;
            var defs = MyDefinitionManager.Static;
            if (defs == null) return "no definitions";
            var assets = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var first = new List<string>();
            foreach (MyDefinitionBase d in defs.GetAllDefinitions())
            {
                var cb = d as MyCubeBlockDefinition;
                if (cb == null) continue;
                string key = cb.Id.ToString();
                // S5-2c: deformable armor has no block Model (its sides are below); the early `continue` on an empty
 // Model used to skip those definitions before the side walk, so the census armor model never entered the set
                if (!string.IsNullOrEmpty(cb.Model))
                {
                    bool must = false;
                    if (mustInclude != null)
                        foreach (string m in mustInclude)
                            if (cb.Id.SubtypeName != null && cb.Id.SubtypeName.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                cb.Model.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) must = true;
                    if (must && !first.Contains(cb.Model)) first.Add(cb.Model);
                    if (!assets.ContainsKey(key)) assets[key] = cb.Model;
                }
                // S5-2a: deformable armor (CubeBlocks_Armor.sbc) has no block Model; its side models (the census model
 // RoundCornerTriangleRightPlate among them) sit in CubeDefinition.Model, so the set walks those too
                if (cb.CubeDefinition != null && cb.CubeDefinition.Model != null)
                    for (int si = 0; si < cb.CubeDefinition.Model.Length; si++)
                    {
                        string side = cb.CubeDefinition.Model[si];
                        if (string.IsNullOrEmpty(side)) continue;
                        string sk = key + "#side" + si.ToString(CultureInfo.InvariantCulture);
                        bool ms = false;
                        if (mustInclude != null) foreach (string m in mustInclude) if (side.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) ms = true;
                        if (ms && !first.Contains(side)) first.Add(side);
                        if (!assets.ContainsKey(sk)) assets[sk] = side;
                    }
                if (cb.BuildProgressModels != null)
                    foreach (var bp in cb.BuildProgressModels)
                        if (bp != null && !string.IsNullOrEmpty(bp.File))
                        {
                            string bk = key + "#stage" + bp.BuildRatioUpperBound.ToString("R", CultureInfo.InvariantCulture);
                            bool mb = false;
                            if (mustInclude != null) foreach (string m in mustInclude) if (bp.File.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) mb = true;
                            if (mb && !first.Contains(bp.File)) first.Add(bp.File);
                            if (!assets.ContainsKey(bk)) assets[bk] = bp.File;
                        }
            }
            var picked = new List<string>(first);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in first) seen.Add(S3Keys.NormalisePath(f));
            foreach (var kv in assets)
            {
                if (picked.Count >= count) break;
                if (seen.Add(S3Keys.NormalisePath(kv.Value))) picked.Add(kv.Value);
            }
            ulong now = Win32.TickCount64();
            foreach (string a in picked)
            {
                ulong key = S3Keys.ModelKey(a, 0, 1f);
                ModelEntry e = RequestModel(now, key, a, 1f, send);
                if (send && e.State == 1 && Gate.Streaming && !e.SentThisStream) QueueModel(e);
                requested++;
            }
            DeLog.Info("DE.S5 model set: " + requested + " distinct block model(s) requested" + (send ? " and sent over C3 once loaded" : " (load only)") +
                       (first.Count > 0 ? "; first: " + string.Join(", ", first.ToArray()) : "") + " (S5.G1-E4 / S5.N5)");
            return null;
        }

        internal static Dictionary<string, object> S5Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["settings"] = S5Config.ToDict();
            d["active"] = S5Active;
            d["streaming"] = Gate.Streaming;
            d["entities"] = EntitySet.Tracker.Count;
            d["waiting"] = EntitySet.Tracker.Waiting;
            int grids = 0, subgrids = 0, floating = 0, parts = 0, dynamic = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.Floating) floating++;
                else if (g.Subgrid) subgrids++;
                else grids++;
                parts += HeldOf(g).Count;
                if (g.S5 != null) dynamic += g.S5.Dynamic.Count;
            }
            d["grids"] = grids;
            d["subgrids"] = subgrids;
            d["floating"] = floating;
            d["parts"] = parts;
            d["dynamicSubparts"] = dynamic;
            d["c4Lanes"] = new object[] { C4.LaneCount(1), C4.LaneCount(2), C4.LaneCount(3), C4.LaneCount(4) };
            d["c4BackPressure"] = C4.LastBackPressure;
            d["c3Queue"] = C3.Count;
            d["c3FreeBytes"] = _c3FreeBytes;
            d["c2Rows"] = _c2Rows;
            d["c2DroppedTotal"] = _c2DroppedTotal;
            d["edits"] = _s5Edits;
            d["reparents"] = _s5Reparents;
            d["enters"] = _s5Enters;
            d["leaves"] = _s5Leaves;
            d["edgeError"] = GridExporter.EdgeError;
            d["deformedParts"] = GridExporter.DeformedParts;
            d["partRemovesDroppedS5M3"] = _s5Dropped3;
            d["character"] = CharacterExport.Status();
            d["ghost"] = PlacementGhost.Status(); // S7a
            d["recorder"] = Recorder.Status();
            d["mutants"] = S5Mutants.Active;
            return d;
        }

        // ============================================================================================= rows

        private static void GridRow(string op, long id, long parent, string reason, MirroredGrid g)
        {
            var sb = S5Rows.Head(Utc(), "grid");
            S5Rows.Str(sb, "op", op);
            S5Rows.Int(sb, "tick", (long)_tick);
            S5Rows.Id(sb, "entityId", id);
            S5Rows.Id(sb, "parentId", parent);
            if (g != null)
            {
                S5Rows.Str(sb, "kind", g.Floating ? "floating" : g.Subgrid ? "subgrid" : "grid");
                S5Rows.Bool(sb, "large", g.Large);
                if (g.Grid != null) S5Rows.Bool(sb, "static", g.Grid.IsStatic);
                S5Rows.Int(sb, "flags", g.SentGridFlags);
                S5Rows.Num(sb, "distance", g.Distance);
                S5Rows.Bool(sb, "pinned", g.Pinned);
                if (g.Grid != null) S5Rows.Int(sb, "blocks", g.Grid.BlocksCount);
            }
            if (reason != null) S5Rows.Str(sb, "reason", reason);
            S5Rows.Int(sb, "entities", EntitySet.Tracker.Count);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        /// <summary>s5.budget once per second while S5 streams: C3/C4 ms and bytes, queues, back-pressure, C2 rows and drops, reads, entities.</summary>
        private static void S5Budget(ulong nowMs, Link link, ulong c3Head, ulong c3Tail, ulong c4Head, ulong c4Tail)
        {
            if (!S5Active || nowMs < _nextBudgetMs) return;
            bool first = _nextBudgetMs == 0;
            _nextBudgetMs = nowMs + S5Defaults.BudgetRowMs;
            if (first) { _budgetC3Bytes = _budgetC4Bytes = 0; return; }
            if (Grids.Count == 0 && C3.Count == 0 && C4.Count == 0 && !Gate.Streaming) return;
            int grids = 0, subgrids = 0, floating = 0, parts = 0, cells = 0, blocks = 0, dynamic = 0, deferred = 0, unsettled = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.Floating) floating++; else if (g.Subgrid) subgrids++; else grids++;
                parts += HeldOf(g).Count;
                if (g.S5 != null)
                {
                    cells += g.S5.Cells.Count;
                    blocks += g.S5.Blocks.Count;
                    dynamic += g.S5.Dynamic.Count;
                    deferred += GridExporter.DeferredCount(g.S5);
                    if (GridExporter.Unsettled(g.S5)) unsettled++;
                }
            }
            var sb = S5Rows.Head(Utc(), "budget");
            S5Rows.Int(sb, "tick", (long)_tick);
            sb.Append(",\"c3\":{\"ms\":").Append(S3Rows.G(Math.Round(_lastC3Ms, 4))).Append(",\"bytesPerSec\":").Append(_budgetC3Bytes)
              .Append(",\"queue\":").Append(C3.Count).Append(",\"queueBytes\":").Append(C3.PendingBytes)
              .Append(",\"free\":").Append(RingFree(RegionId.C3SeModels, c3Head, c3Tail)).Append(",\"held\":").Append(_holdC3 ? "true" : "false")
              .Append(",\"full\":").Append(C3.FullEvents).Append(",\"last\":"); JsonText.AppendString(sb, _lastC3Stop.ToString()); sb.Append('}');
            sb.Append(",\"c4\":{\"ms\":").Append(S3Rows.G(Math.Round(_lastC4Ms, 4))).Append(",\"bytesPerSec\":").Append(_budgetC4Bytes)
              .Append(",\"lanes\":[").Append(C4.LaneCount(1)).Append(',').Append(C4.LaneCount(2)).Append(',').Append(C4.LaneCount(3)).Append(',').Append(C4.LaneCount(4))
              .Append("],\"queueBytes\":").Append(C4.PendingBytes).Append(",\"free\":").Append(RingFree(RegionId.C4SeParts, c4Head, c4Tail))
              .Append(",\"backPressure\":").Append(C4.LastBackPressure ? "true" : "false").Append(",\"backPressureTicks\":").Append(C4.BackPressureTicks)
              .Append(",\"full\":").Append(C4.FullEvents).Append(",\"purged\":").Append(C4.Purged).Append('}');
            sb.Append(",\"c2\":{\"entities\":").Append(_c2Entities).Append(",\"rows\":").Append(_c2Rows).Append(",\"c2Dropped\":").Append(_c2Dropped)
              .Append(",\"c2DroppedTotal\":").Append(_c2DroppedTotal).Append(",\"ms\":").Append(S3Rows.G(Math.Round(_c2Ms, 4)))
              .Append(",\"msMax\":").Append(S3Rows.G(Math.Round(_c2MsMax, 4))).Append('}');
            sb.Append(",\"read\":{\"ms\":").Append(S3Rows.G(Math.Round(_readMs, 4))).Append(",\"msMax\":").Append(S3Rows.G(Math.Round(_readMsMax, 4)))
              .Append(",\"setMs\":").Append(S3Rows.G(Math.Round(_setMs, 4))).Append(",\"cells\":").Append(cells).Append(",\"blocks\":").Append(blocks)
              .Append(",\"deferred\":").Append(deferred).Append(",\"unsettled\":").Append(unsettled).Append('}');
            sb.Append(",\"entities\":").Append(EntitySet.Tracker.Count).Append(",\"waiting\":").Append(EntitySet.Tracker.Waiting)
              .Append(",\"grids\":").Append(grids).Append(",\"subgrids\":").Append(subgrids).Append(",\"floating\":").Append(floating)
              .Append(",\"parts\":").Append(parts).Append(",\"dynamic\":").Append(dynamic)
              .Append(",\"edits\":").Append(_s5Edits).Append(",\"editParts\":").Append(_s5EditParts)
              .Append(",\"models\":{\"loaded\":").Append(Loader.Loaded).Append(",\"failed\":").Append(Loader.Failed).Append(",\"pending\":").Append(Loader.Pending).Append('}');
            sb.Append(",\"character\":"); JsonText.AppendString(sb, CharacterExport.StateName);
            sb.Append(",\"recorder\":").Append(Recorder.Active ? "true" : "false");
            sb.Append(",\"sn\":{\"mirroredEntityCount\":").Append(link != null && link.HasSnStatus ? link.LastSnStatus.mirroredEntityCount : 0)
              .Append(",\"pendingParts\":").Append(link != null && link.HasSnStatus ? link.LastSnStatus.pendingParts : 0).Append('}');
            Jsonl.WriteLine(S5Rows.End(sb));
            _budgetC3Bytes = _budgetC4Bytes = 0;
            _c2MsMax = 0;
            _readMsMax = 0;
        }

        // ============================================================================================= S7p-gameplay (P14)

 /// <summary>S7p-gameplay (P14): the export read debt (<see cref="ExportReadDebt"/>; <c>de.s7p {readAvgMs}</c>, env DEEPENGINEERS_S7P_READAVG).</summary>
        internal static readonly ExportReadDebt S7pReadDebt = new ExportReadDebt();
        private static double _s7pReadMsMax;
        private static long _s7pSkippedReported, _s7pSpikesReported;
        private static ulong _s7pQuietUntilTick;

        /// <summary>
 /// s7p.export (P14; once a second while the export has work, every 10 s otherwise): the read step's worst tick and the
 /// debt it left, the ticks skipped to pay it back, the cells and blocks still queued for a read, the grids whose first
 /// pass is not done, the C3/C4 queues and the back-pressure, and Subnautica's pending parts (C7): a slow mirror reads
 /// here as a backlog on both sides.
 /// </summary>
        internal static void S7pExportSecond(ulong tick)
        {
            long cells = 0, blocks = 0, firstPending = 0, firstNotDone = 0, parts = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                parts += HeldOf(g).Count; // the parts SE holds as sent (S5 and S3 grids)
                if (g.S5 == null) continue;
                cells += g.S5.CellQueue.Count;
                blocks += g.S5.BlockQueue.Count;
                firstPending += Math.Max(0, g.S5.FirstPending);
                if (!g.S5.FirstPassDone) firstNotDone++;
            }
            long skipped = S7pReadDebt.Skipped - _s7pSkippedReported, spikes = S7pReadDebt.Spikes - _s7pSpikesReported;
            bool busy = cells + blocks + firstPending > 0 || skipped > 0 || C4.Count > 0 || C3.Count > 0;
            if (!busy && tick < _s7pQuietUntilTick) return;
            _s7pQuietUntilTick = tick + 600;
            if (Grids.Count == 0 && !busy) return;
            SnStatus st = _link != null && _link.HasSnStatus ? _link.LastSnStatus : default(SnStatus);
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7p.export" }, { "tick", (long)tick },
                { "grids", Grids.Count }, { "parts", parts }, { "cellsQueued", cells }, { "blocksQueued", blocks }, { "firstPending", firstPending },
                { "firstPassNotDone", firstNotDone }, { "readMsMax", Math.Round(_s7pReadMsMax, 3) }, { "readAvgMs", S7pReadDebt.AvgMs },
                { "debtMs", Math.Round(S7pReadDebt.DebtMs, 3) }, { "skippedTicks", skipped }, { "spikes", spikes },
                { "c3Queue", C3.Count }, { "c3QueueBytes", C3.PendingBytes }, { "c4Queue", C4.Count }, { "c4QueueBytes", C4.PendingBytes },
                { "snPendingParts", (long)st.pendingParts }, { "snMirrored", (long)st.mirroredEntityCount }, { "busy", busy },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
            _s7pSkippedReported = S7pReadDebt.Skipped;
            _s7pSpikesReported = S7pReadDebt.Spikes;
            _s7pReadMsMax = 0;
        }
    }
}
