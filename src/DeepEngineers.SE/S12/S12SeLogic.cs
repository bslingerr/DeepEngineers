using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S12 seeded failures of the SE side (dev builds; tests/s12/analyze_s12.py turns exactly their rows red): M1 the camera
 /// box comes back (the S7 rule: the raw cell at the camera with the per-grid held answer while SE recomputes:
 /// S12.AIR.FLIP, S12.AIR.HOLD), M2 a breach fills at once (S12.FLOOD.FILL), M3 the drain ignores SE's oxygen (the level
 /// drops to zero at the repair: S12.FLOOD.DRAIN), M4 every flood start counts twice (S12.FLOOD.ONCE), M5 no vent rows
 /// (S12.VENT)
 /// (a breached room's frozen cells keep the air above the rising water: S12.AIR.OPEN), M9 the mid-plane (a sealed
 /// block's cell is air only up to its mid-plane: a character against a window from inside swims, S12.AIR.WINDOW.IN),
 /// M10 the box plane (a flood row's boxes are the bounding box of the room's cells, block cells included:
 /// S12.FLOOD.CLIP), M11 no plane surface (the swim model ignores a flooding room's plane: S12.FLOOD.MOVE), M12 round 2's
 /// FLOOD.DEBOUNCE
 /// S12.FLOOD.ONEPLANE). S12.M6 (the
 /// look preset off) and S12.M7 (no water plane) are Subnautica's.
 /// </summary>
    internal static class S12SeMutants
    {
        public static volatile bool CameraBox, FillInstant, DrainInstant, DoubleStart, VentOff, AirPocket, SealMidPlane, BoxPlane, NoSurface, DoorFlip;
        public static readonly string[] All = { "S12.M1", "S12.M2", "S12.M3", "S12.M4", "S12.M5", "S12.M8", "S12.M9", "S12.M10", "S12.M11", "S12.M12" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(5);
                if (CameraBox) parts.Add("S12.M1");
                if (FillInstant) parts.Add("S12.M2");
                if (DrainInstant) parts.Add("S12.M3");
                if (DoubleStart) parts.Add("S12.M4");
                if (VentOff) parts.Add("S12.M5");
                if (AirPocket) parts.Add("S12.M8");
                if (SealMidPlane) parts.Add("S12.M9");
                if (BoxPlane) parts.Add("S12.M10");
                if (NoSurface) parts.Add("S12.M11");
                if (DoorFlip) parts.Add("S12.M12");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll() { CameraBox = FillInstant = DrainInstant = DoubleStart = VentOff = AirPocket = SealMidPlane = BoxPlane = NoSurface = DoorFlip = false; }

        /// <summary>Switches an S12 SE mutant; the log text, or null when the id is not an S12 id this side owns.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S12.M1": CameraBox = on; return "S12.M1 " + state + " (the camera box: the raw cell and the held per-grid answer of S7 decide the air)";
                case "S12.M2": FillInstant = on; return "S12.M2 " + state + " (a breach fills the room at once)";
                case "S12.M3": DrainInstant = on; return "S12.M3 " + state + " (no drain time: the level drops to the oxygen's water at the weld)";
                case "S12.M4": DoubleStart = on; return "S12.M4 " + state + " (every flood start counts twice)";
                case "S12.M5": VentOff = on; return "S12.M5 " + state + " (no vent rows)";
                case "S12.M8": AirPocket = on; return "S12.M8 " + state + " (the air pocket: a breached room's frozen cells keep the air above the rising water)";
                case "S12.M9": SealMidPlane = on; return "S12.M9 " + state + " (the mid-plane: a sealed block's cell is air only up to its mid-plane)";
                case "S12.M10": BoxPlane = on; return "S12.M10 " + state + " (the box plane: a flood row's box is the room's bounding box, block cells included)";
                case "S12.M11": NoSurface = on; return "S12.M11 " + state + " (no plane surface: the swim model ignores a flooding room's plane)";
                case "S12.M12": DoorFlip = on; return "S12.M12 " + state + " (round 2's door flip: no breach debounce, two planes over one cell)";
                case "S12.M6":
                case "S12.M7": return "REFUSE " + id.Trim() + ": a Subnautica-side S12 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default: return null;
            }
        }
    }

    /// <summary>The S12 switches read once per session (env) and changed live by de.s12 (dev).</summary>
    internal static class S12Switch
    {
        public const string EnvVar = "DEEPENGINEERS_S12";
        public static volatile bool AirOn = true, FloodOn = true, VentOn = true;
        /// <summary>de.s12 third on (dev): the camera is treated as SE's third-person camera (format air.camera with the spectator).</summary>
        public static volatile bool ForceThirdPerson;
        /// <summary>de.s12 charrows on (dev): the s12.char rows every 6 ticks, not only while a flood row is active.</summary>
        public static volatile bool CharRows;

        /// <summary>Off with DEEPENGINEERS_S12=off (or 0, false); "noair", "noflood", "novent" in a comma list switch those off only.</summary>
        public static bool EnabledFromEnvironment()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return true;
            v = v.Trim().ToLowerInvariant();
            if (v == "off" || v == "0" || v == "false") return false;
            foreach (string part in v.Split(','))
            {
                string p = part.Trim();
                if (p == "noair") AirOn = false;
                else if (p == "noflood") FloodOn = false;
                else if (p == "novent") VentOn = false;
            }
            return true;
        }
    }

    /// <summary>A grid cell packed into a long (21 bits per axis, two's complement): the snapshot's key.</summary>
    internal static class S12Cell
    {
        private const long Mask = (1L << 21) - 1;

        public static long Pack(int x, int y, int z)
        {
            return ((x & Mask) << 42) | ((y & Mask) << 21) | (z & Mask);
        }

        public static void Unpack(long k, out int x, out int y, out int z)
        {
            x = Sign((int)((k >> 42) & Mask));
            y = Sign((int)((k >> 21) & Mask));
            z = Sign((int)(k & Mask));
        }

        private static int Sign(int v) { return v >= (1 << 20) ? v - (1 << 21) : v; }
    }

    /// <summary>One airtight room of a snapshot (InteriorRooms' last completed GetRooms).</summary>
    internal sealed class S12SnapRoom
    {
        public int Index;
        public int Cells;
        /// <summary>MyOxygenRoom.OxygenAmount and BlockCount at the snapshot (the level needs the grid size: <see cref="Oxygen"/>).</summary>
        public float OxygenAmount;
        public int BlockCount;
        public CellBox Aabb;
        public readonly List<CellBox> Boxes = new List<CellBox>();
        /// <summary>format flood.plane: the room's water cells and their boxes (computed once per snapshot by the flood watch).</summary>
        public HashSet<long> Water;
        public List<CellBox> WaterBoxes;
        /// <summary>The room's smallest packed cell: its identity across recomputes (a flood track's watch cell).</summary>
        public long RepCell;

        /// <summary>MyOxygenRoom.OxygenLevel's rule: min(1, OxygenAmount / (BlockCount g^3)).</summary>
        public float Oxygen(float gridSize)
        {
            float max = BlockCount * gridSize * gridSize * gridSize;
            return max > 0 ? Math.Min(1f, OxygenAmount / max) : 0f;
        }
    }

    /// <summary>
 /// format flood.plane: a snapshot room's water cells, its cells less the skin cells (a cell holding a block with a face
 /// neighbour that is neither a cell of the room nor holds a block: the window, door or slope between the room and the
 /// outside), so a plane is never drawn in a wall's layer, behind a window glass or in a doorway. Game-free.
 /// </summary>
    internal static class S12Water
    {
        private static readonly int[] Dx = { 1, -1, 0, 0, 0, 0 }, Dy = { 0, 0, 1, -1, 0, 0 }, Dz = { 0, 0, 0, 0, 1, -1 };

        public static HashSet<long> Cells(S12Snapshot snap, S12SnapRoom sr, Func<int, int, int, bool> hasBlock)
        {
            var water = new HashSet<long>();
            if (snap == null || sr == null) return water;
            foreach (KeyValuePair<long, int> kv in snap.Cells)
            {
                if (kv.Value != sr.Index) continue;
                int x, y, z;
                S12Cell.Unpack(kv.Key, out x, out y, out z);
                bool skin = false;
                if (hasBlock != null && hasBlock(x, y, z))
                    for (int d = 0; d < 6 && !skin; d++)
                    {
                        int nx = x + Dx[d], ny = y + Dy[d], nz = z + Dz[d];
                        skin = snap.RoomAt(nx, ny, nz) != sr.Index && !hasBlock(nx, ny, nz);
                    }
                if (!skin) water.Add(kv.Key);
            }
            return water;
        }

        public static List<CellBox> Boxes(HashSet<long> water)
        {
            var cells = new List<CellBox>(water != null ? water.Count : 0);
            if (water != null)
                foreach (long k in water)
                {
                    int x, y, z;
                    S12Cell.Unpack(k, out x, out y, out z);
                    cells.Add(CellBox.Cell(x, y, z));
                }
            return S12Boxes.Decompose(cells);
        }

        /// <summary>The cells of <paramref name="boxes"/> that are not water cells (format flood.plane: 0; S12.M10's bounding box: the wall layer).</summary>
        public static long Outside(IList<CellBox> boxes, HashSet<long> water, long cap = 4000000)
        {
            long n = 0, seen = 0;
            foreach (CellBox b in boxes)
                for (int z = b.MinZ; z <= b.MaxZ; z++)
                    for (int y = b.MinY; y <= b.MaxY; y++)
                        for (int x = b.MinX; x <= b.MaxX; x++)
                        {
                            if (++seen > cap) return n;
                            if (water == null || !water.Contains(S12Cell.Pack(x, y, z))) n++;
                        }
            return n;
        }
    }

    /// <summary>A grid's airtight room cells as of its last completed recompute (format air.volume: the processing answer).</summary>
    internal sealed class S12Snapshot
    {
        public readonly Dictionary<long, int> Cells = new Dictionary<long, int>();
        public readonly List<S12SnapRoom> Rooms = new List<S12SnapRoom>();
        public long Serial;
        public ulong TakenMs;
        /// <summary>The gas system's completed recomputes when it was taken (format air.hold: fresh while equal).</summary>
        public long Completions;
        public bool Truncated;

        public int RoomAt(int x, int y, int z)
        {
            int k;
            return Cells.TryGetValue(S12Cell.Pack(x, y, z), out k) ? k : 0;
        }

        public S12SnapRoom RoomByIndex(int index)
        {
            foreach (S12SnapRoom r in Rooms) if (r.Index == index) return r;
            return null;
        }
    }

    /// <summary>
 /// The C6 block's current content (format c6.s12): set by S12Se on the update thread, written by <c>Link.WriteSeTime</c>
 /// (the S12 hook, inside its lock, the same thread). Inactive: version 0 (absent).
 /// </summary>
    internal static unsafe class S12SeWire
    {
        private static readonly S12Header _h = new S12Header();
        private static readonly S12Row[] _rows = new S12Row[S12Conventions.C6.MaxRows];
        private static int _count;
        private static bool _active;
        public static long Writes, Errors;

        public static bool IsActive { get { return _active; } }
        public static int Count { get { return _count; } }

        /// <summary>The rows of this tick (nearest first, at most MaxRows), with the header's sums.</summary>
        public static void Publish(uint worldEpoch, ulong tick, byte flags, List<S12Row> rows)
        {
            int n = rows == null ? 0 : Math.Min(rows.Count, S12Conventions.C6.MaxRows);
            uint flood = 0, vent = 0;
            for (int i = 0; i < n; i++)
            {
                _rows[i] = rows[i];
                if (rows[i].Kind == S12Conventions.RowKind.Flood) flood += rows[i].StartSeq;
                else if (rows[i].Kind == S12Conventions.RowKind.Vent) vent += rows[i].StartSeq;
            }
            _count = n;
            _h.Version = (byte)S12Conventions.C6.Version;
            _h.Flags = flags;
            _h.Count = (byte)n;
            _h.WorldEpoch = worldEpoch;
            _h.FloodSeq = flood;
            _h.VentSeq = vent;
            _h.Tick = tick;
            _active = true;
        }

        public static void Deactivate() { _active = false; _count = 0; }

        /// <summary>The S12 hook of <c>Link.WriteSeTime</c> (inside its lock): never throws into the C6 writer.</summary>
        public static void WriteC6(byte* c6Region, ulong tick)
        {
            try
            {
                if (_active) S12Wire.Write(c6Region, _h, _rows, _count);
                else
                {
                    var off = new S12Header { Version = 0 };
                    S12Wire.Write(c6Region, off, _rows, 0);
                }
                Writes++;
            }
            catch (Exception) { Errors++; }
        }
    }
}
