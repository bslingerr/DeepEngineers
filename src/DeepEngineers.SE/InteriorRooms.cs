using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 interiors, the RoomSet producer (CONTRACT §5.4 "C4 RoomSet (S7)", <c>format roomset.box</c>). For every mirrored
 /// grid (or subgrid) with a gas system: the air cells of its <b>airtight</b> rooms as boxes —
 /// <c>MyGridGasSystem.GetRooms(List&lt;IMyOxygenRoom&gt)</c>
 /// → each <c>MyOxygenRoom</c> with <c>IsAirtight</c> → its <c>Blocks</c>
 /// (<c>HashSet&lt;Vector3I&gt;</c>) → <c>MyVoxelSegmentation</c> (<c>ClearInput</c>, <c>AddInput</c>,
 /// <c>FindSegments(Simple, 0)</c>, as S6; fallback Wire <c>CollisionSkinMath.MergeCellsGreedy</c>, logged once); more
 /// than 4,096 boxes: one box per room with <c>Truncated</c> (<see cref="InteriorRules.Plan"/>).
 /// <list type="bullet">
 /// <item>When: after the grid's GridAdd is queued (lane order puts it first), again after every new GridAdd ticket
 /// (stream restart, re-parent), and when the box set changed (content hash) after the gas system finished a recompute —
 /// a handler added with <c>+=</c> to <c>OnProcessingDataComplete</c> (<c>:167</c>, invoked from
 /// <c>OnBackgroundTaskFinished</c>, <c>:826</c>; removed with <c>-=</c> when the grid leaves) — with a 10 s content-hash
 /// re-check as the safety net. A first set without any airtight room is not sent (Subnautica's default is "no
 /// room").</item>
 /// <item>Lane Edit (C4 back-pressure as S5), ≤ 0.5 ms per tick, resumable (dirty grids wait for the next tick);
 /// nothing at all without <c>EnableOxygen</c> ∧ <c>EnableOxygenPressurization</c> (S7.N1b "no RoomSet sent").</item>
 /// <item>Rows <c>s7.rooms</c> {tick, entity, boxes, truncated, hash, sent, cells, rooms, ms, why} for each evaluation
 /// that sends, and once per unchanged re-check burst at most every 10 s per grid (not logged).</item>
 /// <item>Review F1: SE builds airtight-room data only for grids with an Air Vent
 /// (<c>MyGridGasSystem.ShouldPressurize</c>, <c>:463</c>: <c>m_forcePressurize</c>, <c>m_gasBlocks.Count &gt; 0</c> or
 /// existing rooms; <c>GetRooms</c> <c>:1753</c> answers false without them). An evaluation whose <c>GetRooms</c> is false
 /// while the gas system is not processing counts as <c>noRoomData</c> (the <c>s7.rooms</c> rows and
 /// <c>de.interior</c> status) and logs once per grid id <c>DE.S7 rooms &lt;id&gt;: no room data (SE builds airtight
 /// rooms only on grids with an Air Vent: MyGridGasSystem.ShouldPressurize)</c> with one <c>s7.rooms</c> row
 /// (<c>sent</c> false, <c>noRoomData</c> true, <c>gasBlocks</c>).</item>
 /// </list>
 /// Keyed by grid entity id (v3). A failure disables the producer (logged once); the camera flag does not depend on it.
 /// </summary>
    internal static class InteriorRooms
    {
        public const ulong RecheckMs = 10000;
        public const double BudgetMs = 0.5;

        private sealed class RoomState
        {
            public long Id;
            public MyCubeGrid Grid;
            public MyGridGasSystem Gas;
            public Action Handler;
            public volatile bool Dirty = true;
            public ulong NextCheckMs;
            public bool HasSent, SentNonEmpty;
            public ulong SentHash;
            public long SentForGridAdd = long.MinValue;
            public int SentBoxes;
            public bool SentTruncated;
            public long Sends, Evaluations, Completes;
            public double LastMs;
            public string LastWhy = "";
            public bool Seen;
            /// <summary>Review F1: the last evaluation had no room data (GetRooms false, not processing); logged once per grid.</summary>
            public bool NoRoomData, NoRoomDataLogged;
        }

        private static readonly Dictionary<long, RoomState> States = new Dictionary<long, RoomState>();
        private static readonly List<long> _gone = new List<long>();
        private static readonly List<IMyOxygenRoom> _rooms = new List<IMyOxygenRoom>();
        private static readonly List<List<CellBox>> _perRoom = new List<List<CellBox>>();
        private static readonly List<CellBox> _aabbs = new List<CellBox>();
        private static readonly List<CellBox> _boxes = new List<CellBox>();
        private static readonly List<I32x3> _cells = new List<I32x3>();
        private static readonly List<CellRange> _ranges = new List<CellRange>();
        private static MyVoxelSegmentation _seg;
        private static bool _segmenterMissing, _disabled;
        private static string _disabledWhy;

        public static long Sent, SentBytes, Evaluated, Unchanged, BusySkips, BudgetStops, Errors, Hooked, Unhooked, Completes;
        /// <summary>Review F1: evaluations whose GetRooms answered false while the gas system was not processing (no Air Vent).</summary>
        public static long NoRoomData;
        public static double LastTickMs, MaxTickMs;

        /// <summary>
 /// The S5 producer's hook (<c>S5UpdateGrids</c>, after the S6 skins): <paramref name="ordered"/> = the mirrored grids
 /// nearest first; <paramref name="streaming"/> = the S5 stream is open (messages may be queued). Never throws.
 /// </summary>
        public static void Tick(List<GridMirrorProducer.MirroredGrid> ordered, ulong nowMs, ulong tick, bool streaming)
        {
            if (_disabled || ordered == null) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                TickCore(ordered, nowMs, tick, streaming, t0);
            }
            catch (Exception e)
            {
                Errors++;
                _disabled = true;
                _disabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S7 rooms OFF (" + _disabledWhy + "): no RoomSet from now on (the camera flag keeps working). " + e.StackTrace);
                UnhookAll();
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LastTickMs = ms;
            if (ms > MaxTickMs) MaxTickMs = ms;
        }

        private static void TickCore(List<GridMirrorProducer.MirroredGrid> ordered, ulong nowMs, ulong tick, bool streaming, long t0)
        {
            // grids that left the mirror: their handler removed, their state forgotten (their queued RoomSets were purged
 // with the grid's other records before its EntityRemove)
            foreach (RoomState s in States.Values) s.Seen = false;
            foreach (GridMirrorProducer.MirroredGrid mg in ordered)
            {
                RoomState s;
                if (mg.Grid != null && States.TryGetValue(mg.Id, out s)) s.Seen = true;
            }
            _gone.Clear();
            foreach (KeyValuePair<long, RoomState> kv in States) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (long id in _gone) Forget(id);

            if (!S7Interior.Pressurization) { if (States.Count > 0) UnhookAll(); return; }
            long deadline = t0 + (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            foreach (GridMirrorProducer.MirroredGrid mg in ordered)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || g.MarkedForClose) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                RoomState s;
                if (!States.TryGetValue(mg.Id, out s))
                {
                    if (gas == null) continue;
                    s = new RoomState { Id = mg.Id, Grid = g, Seen = true };
                    States[mg.Id] = s;
                }
                if (!ReferenceEquals(s.Gas, gas)) Hook(s, gas);
                if (gas == null || !streaming || mg.GridAddIndex < 0) continue;
                bool newGridAdd = s.SentForGridAdd != mg.GridAddIndex;
                bool due = newGridAdd || s.Dirty || nowMs >= s.NextCheckMs;
                if (!due) continue;
                if (Stopwatch.GetTimestamp() >= deadline) { BudgetStops++; return; } // resumable: the rest next tick
                if (((IMyGridGasSystem)gas).IsProcessingData) { BusySkips++; continue; } // its completion handler marks it dirty again
                Evaluate(s, mg, gas, nowMs, tick, newGridAdd);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Evaluate(RoomState s, GridMirrorProducer.MirroredGrid mg, MyGridGasSystem gas, ulong nowMs, ulong tick, bool newGridAdd)
        {
            long t0 = Stopwatch.GetTimestamp();
            string why = newGridAdd ? (s.SentForGridAdd == long.MinValue ? "first" : "gridAdd") : s.Dirty ? "recompute" : "recheck";
            s.Dirty = false;
            s.NextCheckMs = nowMs + RecheckMs + (ulong)(s.Id & 1023); // staggered re-checks
            s.Evaluations++;
            Evaluated++;

            int rooms = 0, cells = 0;
            bool truncated;
            bool roomData = Build(gas, out rooms, out cells, out truncated);
            ulong hash = RoomSetCodec.ContentHash(_boxes, truncated);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            s.LastMs = ms;
            s.LastWhy = why;
            s.NoRoomData = !roomData && !((IMyGridGasSystem)gas).IsProcessingData;
            if (s.NoRoomData)
            {
                NoRoomData++;
                if (!s.NoRoomDataLogged) NoRoomDataOnce(s, gas, tick, why);
            }

            bool send;
            if (newGridAdd) send = _boxes.Count > 0 || s.SentNonEmpty; // a fresh entity on Subnautica's side has no room
            else send = !s.HasSent ? _boxes.Count > 0 : hash != s.SentHash;
            s.SentForGridAdd = mg.GridAddIndex;
            if (!send)
            {
                Unchanged++;
                return;
            }
            byte[] payload = RoomSetCodec.Encode((ulong)s.Id, _boxes, truncated);
            GridMirrorProducer.EnqueueLane(C4Lanes.Edit, new OutMessage(C4Type.RoomSet, payload, 0, "RoomSet " + s.Id) { Entity = s.Id });
            s.HasSent = true;
            s.SentNonEmpty = _boxes.Count > 0;
            s.SentHash = hash;
            s.SentBoxes = _boxes.Count;
            s.SentTruncated = truncated;
            s.Sends++;
            Sent++;
            SentBytes += payload.Length;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.rooms" }, { "tick", (long)tick },
                { "entity", s.Id.ToString(CultureInfo.InvariantCulture) }, { "boxes", (long)_boxes.Count }, { "truncated", truncated },
                { "hash", "0x" + hash.ToString("x16", CultureInfo.InvariantCulture) }, { "sent", true }, { "rooms", (long)rooms }, { "cells", (long)cells },
                { "bytes", (long)payload.Length }, { "ms", Math.Round(ms, 3) }, { "why", why }, { "noRoomData", s.NoRoomData }, { "noRoomDataTotal", NoRoomData },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
            DeLog.Info("DE.S7 RoomSet " + s.Id + " (" + why + "): " + rooms + " airtight room(s), " + cells + " air cell(s) in " + _boxes.Count + " box(es)" +
                       (truncated ? " TRUNCATED to one box per room" : "") + ", " + payload.Length + " B, " + ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>Review F1: the once-per-grid log line and its s7.rooms row (sent false, noRoomData true).</summary>
        private static void NoRoomDataOnce(RoomState s, MyGridGasSystem gas, ulong tick, string why)
        {
            s.NoRoomDataLogged = true;
            int gasBlocks = S7Interior.GasBlockCount(gas);
            DeLog.Info("DE.S7 rooms " + s.Id.ToString(CultureInfo.InvariantCulture) + ": no room data (SE builds airtight rooms only on grids with an Air Vent: " +
                       "MyGridGasSystem.ShouldPressurize; gas blocks " + (gasBlocks < 0 ? "unknown" : gasBlocks.ToString(CultureInfo.InvariantCulture)) + ")");
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.rooms" }, { "tick", (long)tick },
                { "entity", s.Id.ToString(CultureInfo.InvariantCulture) }, { "boxes", 0L }, { "truncated", false }, { "sent", false },
                { "noRoomData", true }, { "noRoomDataTotal", NoRoomData }, { "gasBlocks", (long)gasBlocks }, { "why", why },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        /// <summary>
 /// The airtight rooms' air cells of the grid as boxes, into <c>_boxes</c> (sorted). Returns whether
 /// <c>GetRooms</c> had room data.
 /// </summary>
        private static bool Build(MyGridGasSystem gas, out int rooms, out int cells, out bool truncated)
        {
            rooms = 0;
            cells = 0;
            _rooms.Clear();
            foreach (List<CellBox> l in _perRoom) l.Clear();
            int used = 0;
            _aabbs.Clear();
            bool roomData = gas.GetRooms(_rooms);
            if (roomData)
            {
                foreach (IMyOxygenRoom ir in _rooms)
                {
                    var r = ir as MyOxygenRoom;
                    if (r == null || !r.IsAirtight || r.Blocks == null || r.Blocks.Count == 0) continue;
                    rooms++;
                    _cells.Clear();
                    foreach (Vector3I c in r.Blocks) _cells.Add(new I32x3(c.X, c.Y, c.Z));
                    cells += _cells.Count;
                    if (used == _perRoom.Count) _perRoom.Add(new List<CellBox>());
                    List<CellBox> boxes = _perRoom[used++];
                    Segment(_cells, boxes);
                    _aabbs.Add(InteriorRules.Aabb(_cells));
                }
            }
            S12Rooms.Capture(gas, _rooms, _perRoom, used, roomData); // S12 hook: the room cells of this completed recompute (the air rule's processing answer, the flood geometry; never throws)
            _rooms.Clear();
            _cells.Clear();
            InteriorRules.Plan(_perRoom.GetRange(0, used), _aabbs, _boxes, out truncated);
            return roomData;
        }

        private static void Segment(List<I32x3> cells, List<CellBox> output)
        {
            output.Clear();
            _ranges.Clear();
            if (!_segmenterMissing)
            {
                try
                {
                    SegmentSe(cells, _ranges);
                    foreach (CellRange r in _ranges) output.Add(CellBox.From(r));
                    return;
                }
                catch (Exception e)
                {
                    _segmenterMissing = true;
                    DeLog.Info("DE.S7 rooms MyVoxelSegmentation unavailable (" + e.GetType().Name + ": " + e.Message + "): Wire's greedy merge is used (logged once)");
                }
            }
            _ranges.Clear();
            CollisionSkinMath.MergeCellsGreedy(cells, _ranges);
            foreach (CellRange r in _ranges) output.Add(CellBox.From(r));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SegmentSe(List<I32x3> cells, List<CellRange> output)
        {
            if (_seg == null) _seg = new MyVoxelSegmentation();
            _seg.ClearInput();
            foreach (I32x3 c in cells) _seg.AddInput(new Vector3I(c.x, c.y, c.z));
            List<MyVoxelSegmentation.Segment> found = _seg.FindSegments(MyVoxelSegmentationType.Simple, 0);
            foreach (MyVoxelSegmentation.Segment g in found)
                output.Add(new CellRange(g.Min.X, g.Min.Y, g.Min.Z, g.Max.X, g.Max.Y, g.Max.Z));
            _seg.ClearInput();
        }

        // ============================================================================================ hooks

        private static void Hook(RoomState s, MyGridGasSystem gas)
        {
            Unhook(s);
            s.Gas = gas;
            if (gas == null) return;
            RoomState captured = s;
            s.Handler = () => { captured.Dirty = true; captured.Completes++; Completes++; };
            gas.OnProcessingDataComplete += s.Handler;
            s.Dirty = true;
            Hooked++;
        }

        private static void Unhook(RoomState s)
        {
            if (s.Gas != null && s.Handler != null)
            {
                try { s.Gas.OnProcessingDataComplete -= s.Handler; Unhooked++; }
                catch (Exception) { }
            }
            s.Gas = null;
            s.Handler = null;
        }

        private static void Forget(long id)
        {
            RoomState s;
            if (!States.TryGetValue(id, out s)) return;
            Unhook(s);
            States.Remove(id);
        }

        private static void UnhookAll()
        {
            foreach (RoomState s in States.Values) Unhook(s);
            States.Clear();
        }

        /// <summary>World unload (the S5 producer forgot the world): every handler removed.</summary>
        public static void OnWorldUnload(string why)
        {
            UnhookAll();
        }

        // ============================================================================================ status

        public static Dictionary<string, object> Status()
        {
            var grids = new List<object>();
            foreach (RoomState s in States.Values)
                grids.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "entity", s.Id.ToString(CultureInfo.InvariantCulture) }, { "hooked", s.Handler != null }, { "dirty", s.Dirty },
                    { "processing", s.Gas != null && ((IMyGridGasSystem)s.Gas).IsProcessingData }, { "sent", s.HasSent }, { "boxes", (long)s.SentBoxes },
                    { "truncated", s.SentTruncated }, { "hash", "0x" + s.SentHash.ToString("x16", CultureInfo.InvariantCulture) }, { "sends", s.Sends },
                    { "evaluations", s.Evaluations }, { "completes", s.Completes }, { "lastMs", Math.Round(s.LastMs, 3) }, { "lastWhy", s.LastWhy },
                    { "noRoomData", s.NoRoomData },
                });
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "grids", grids }, { "sent", Sent }, { "sentBytes", SentBytes }, { "evaluated", Evaluated }, { "unchanged", Unchanged }, { "busySkips", BusySkips },
                { "budgetStops", BudgetStops }, { "completes", Completes }, { "hooked", Hooked }, { "unhooked", Unhooked }, { "segmenterFallback", _segmenterMissing },
                { "noRoomData", NoRoomData },
                { "msLast", Math.Round(LastTickMs, 3) }, { "msMax", Math.Round(MaxTickMs, 3) }, { "disabled", _disabled }, { "disabledWhy", _disabledWhy ?? "" },
            };
        }
    }
}
