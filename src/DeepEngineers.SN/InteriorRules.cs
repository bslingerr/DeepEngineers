using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7 interiors, the Unity-free half of the Subnautica side (CONTRACT §5.4 "C4 RoomSet (S7)", "Interior chunks, sky,
 /// inside-walkable and clip proxies"): the RoomSet store (the newest set per entity, dropped with the entity on
 /// <c>EntityRemove</c> and with everything on <c>ClearAll</c>), fed by <see cref="MirrorState.Feed"/>'s RoomSet case.
 /// A new set re-classifies the entity's chunked parts (<see cref="InteriorChunks.Reclassify"/>): the class joins the
 /// chunk key, so only the cells whose class changed are dirtied (S5 rebuild rules). Keyed by entity id (v3).
 /// </summary>
    internal sealed unsafe class InteriorStore
    {
        private readonly Dictionary<ulong, CellBox[]> _sets = new Dictionary<ulong, CellBox[]>();
        private readonly List<CellBox> _tmp = new List<CellBox>();

        public long Applied, VersionIgnored, Malformed, Removed, PartsMoved, Truncated;
        /// <summary>Bumped on every change of any set (the Unity side re-applies skies and clip proxies).</summary>
        public int Serial;
        public string LastError;

        public int Count { get { return _sets.Count; } }
        public IEnumerable<KeyValuePair<ulong, CellBox[]>> Sets { get { return _sets; } }

        /// <summary>The boxes of an entity (null: none).</summary>
        public CellBox[] BoxesOf(ulong entityId)
        {
            CellBox[] b;
            return _sets.TryGetValue(entityId, out b) ? b : null;
        }

        /// <summary>
 /// One C4 RoomSet payload: decoded (Wire <see cref="RoomSetCodec"/>), stored, and the entity's grid record (when
 /// known) re-classified. Version nibble ≠ 1: ignored and counted; malformed: an error for the caller.
 /// </summary>
        public C4Outcome Feed(byte* p, int bytes, Dictionary<ulong, GridRecord> grids, out ulong entityId, out string error)
        {
            RoomSetInfo info;
            bool versionOk;
            entityId = 0;
            if (!RoomSetCodec.TryDecode(p, bytes, out info, _tmp, out versionOk, out error))
            {
                Malformed++;
                LastError = error;
                entityId = info.EntityId;
                return C4Outcome.Error;
            }
            entityId = info.EntityId;
            if (!versionOk) { VersionIgnored++; return C4Outcome.Ignored; }
            Apply(info.EntityId, _tmp.ToArray(), info.Truncated, grids);
            return C4Outcome.PartChanged;
        }

        /// <summary>Stores a set (an empty one removes the entry) and re-classifies the entity's parts.</summary>
        public int Apply(ulong entityId, CellBox[] boxes, bool truncated, Dictionary<ulong, GridRecord> grids)
        {
            if (boxes == null || boxes.Length == 0) _sets.Remove(entityId);
            else _sets[entityId] = boxes;
            Applied++;
            if (truncated) Truncated++;
            Serial++;
            GridRecord g;
            if (grids == null || !grids.TryGetValue(entityId, out g)) return 0;
            g.Rooms = boxes != null && boxes.Length > 0 ? boxes : null;
            int moved = InteriorChunks.Reclassify(g);
            PartsMoved += moved;
            return moved;
        }

        public void Remove(ulong entityId)
        {
            if (_sets.Remove(entityId)) { Removed++; Serial++; }
        }

        public void Clear()
        {
            if (_sets.Count > 0) Serial++;
            _sets.Clear();
        }
    }

    /// <summary>The chunk class of a part (<c>format interior.class</c>) and the re-keying of a grid's cells.</summary>
    internal static class InteriorChunks
    {
        /// <summary>The grid size of a mirrored entity: 2.5 m large, 0.5 m small (the cell index is round(T / g)).</summary>
        public static double GridSize(GridRecord g)
        {
            return g.TargetClass == GridClass.SmallGrid ? 0.5 : 2.5;
        }

        /// <summary>The S5 cell of a part's translation, with its interior class against the grid's room boxes.</summary>
        public static CellKey CellOf(GridRecord g, float tx, float ty, float tz)
        {
            CellKey k = CellKey.Of(tx, ty, tz, g.CellSize);
            CellBox[] rooms = g.Rooms;
            if (rooms == null || rooms.Length == 0) return k;
            byte cls = S7pRoomIndex.ClassifyLocal(tx, ty, tz, GridSize(g), rooms); // S7p-gameplay (P14): Wire's rule through a bucket index (was a scan of every box: 4.5 s stall on a 100k-part grid)
            return cls == S7Conventions.InteriorClass.Exterior ? k : k.WithClass(cls);
        }

        /// <summary>
 /// After a new room set: every chunked part whose cell key changed moves to its new cell; both cells are dirtied
 /// (the old one rebuilds without the part, the new one with it). Returns how many parts moved.
 /// </summary>
        public static int Reclassify(GridRecord g)
        {
            int moved = 0;
            foreach (PartRecord pr in g.Parts.Values)
            {
                if (!pr.Chunked) continue;
                CellKey now = CellOf(g, pr.Local[9], pr.Local[10], pr.Local[11]);
                if (now.Equals(pr.Cell)) continue;
                HashSet<ulong> set;
                if (g.CellParts.TryGetValue(pr.Cell, out set)) { set.Remove(pr.PartKey); if (set.Count == 0) g.CellParts.Remove(pr.Cell); }
                g.DirtyCells.Add(pr.Cell);
                pr.Cell = now;
                if (!g.CellParts.TryGetValue(now, out set)) { set = new HashSet<ulong>(); g.CellParts[now] = set; }
                set.Add(pr.PartKey);
                g.DirtyCells.Add(now);
                moved++;
            }
            if (moved > 0) g.Version++;
            return moved;
        }

        /// <summary>Chunk counts of a grid's cells by class (Exterior, Boundary, Inside).</summary>
        public static void CountCells(GridRecord g, out int exterior, out int boundary, out int inside)
        {
            exterior = boundary = inside = 0;
            foreach (CellKey c in g.CellParts.Keys)
            {
                if (c.C == S7Conventions.InteriorClass.Inside) inside++;
                else if (c.C == S7Conventions.InteriorClass.Boundary) boundary++;
                else exterior++;
            }
        }
    }

    /// <summary>
 /// The inside-walkable rule (CONTRACT §5.4 "Inside-walkable"): the postfix sets <c>IsInsideWalkable</c> true for the
 /// ghost <c>Player.main</c> while the newest C1 (at most <see cref="FreshMs"/> old, the bridge driving the camera) says
 /// <c>InRoom</c>; off with the A/B switch (<c>de.interior {patch: off}</c>) and under S7.M1.
 /// </summary>
    internal static class InsideRule
    {
        public const ulong FreshMs = 100;

        public static bool Inside(bool patchOn, bool mutantOff, bool driving, bool c1Seen, bool c1InRoom, ulong c1Ms, ulong nowMs)
        {
            if (!patchOn || mutantOff || !driving || !c1Seen || !c1InRoom) return false;
            return nowMs >= c1Ms && nowMs - c1Ms <= FreshMs;
        }
    }

    /// <summary>
 /// The N2 reader trace (CONTRACT §5.4 "S7 control commands": <c>de.interior {action: trace}</c>): per caller type of
 /// <c>Player.IsInsideWalkable</c>, its call count inside and outside the room, the direct readers and those through
 /// <c>Player.IsInside</c> apart. Unity-free (the postfix hands it the type names).
 /// </summary>
    internal sealed class InsideTrace
    {
        /// <summary>The 13 direct readers.</summary>
        public static readonly string[] Direct =
        {
            "AttachAndSuck", "BasePipeConnector", "ColorCorrection", "FMODGameParams", "FlyAwayWhenScared", "LargeWorldStreamer", "Player",
            "PlayerSoundTrigger", "TemperatureDamage", "UnstuckPlayer", "UseableDiveHatch", "WaterscapeVolumeOnCamera", "uGUI_AdaptiveBrightness",
        };

        /// <summary>The 5 readers through <c>Player.IsInside</c>.</summary>
        public static readonly string[] ViaIsInside = { "ArmsController", "FootstepSounds", "GasPod", "UwePostProcessingManager", "WarpBall" };

        public readonly Dictionary<string, long> InsideDirect = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> OutsideDirect = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> InsideVia = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> OutsideVia = new Dictionary<string, long>(StringComparer.Ordinal);
        public ulong UntilMs, StartMs;
        public long Calls, Unknown;

        public bool Active(ulong nowMs) { return nowMs < UntilMs; }

        public void Start(ulong nowMs, double seconds)
        {
            InsideDirect.Clear(); OutsideDirect.Clear(); InsideVia.Clear(); OutsideVia.Clear();
            Calls = Unknown = 0;
            StartMs = nowMs;
            UntilMs = nowMs + (ulong)(Math.Max(0.0, Math.Min(600.0, seconds)) * 1000.0);
        }

        /// <summary>One call: the caller's type name (null: unknown), whether it came through IsInside, and the answer given.</summary>
        public void Note(string caller, bool viaIsInside, bool inside)
        {
            Calls++;
            if (string.IsNullOrEmpty(caller)) { Unknown++; caller = "?"; }
            Dictionary<string, long> d = viaIsInside ? (inside ? InsideVia : OutsideVia) : (inside ? InsideDirect : OutsideDirect);
            long n;
            d.TryGetValue(caller, out n);
            d[caller] = n + 1;
        }

        /// <summary>The expected readers never seen in this window (INFO "not called" for the analyzer).</summary>
        public List<string> NotCalled()
        {
            var l = new List<string>();
            foreach (string t in Direct) if (!InsideDirect.ContainsKey(t) && !OutsideDirect.ContainsKey(t)) l.Add(t);
            foreach (string t in ViaIsInside) if (!InsideVia.ContainsKey(t) && !OutsideVia.ContainsKey(t)) l.Add(t + " (via IsInside)");
            return l;
        }
    }
}
