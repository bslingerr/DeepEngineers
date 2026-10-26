using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 interiors (CONTRACT §5.4 "S7 conventions": "C1 interior extension", "C4 RoomSet (S7)", "Interior chunks, sky,
 // inside-walkable and clip proxies"). Pure and allocation-light; shared by the SE producer (the camera probe's hold
 // rule, the C1 bytes, the RoomSet payloads) and the Subnautica consumer (the C1 bytes, the RoomSet store, the chunk
 // class, the sky and clip-proxy decisions). Layout 1.0 is unchanged: the interior bytes live in the C1 pad 0xB0-0xBF
 // (after the S5a swim extension 0x94-0x9F and the S7a loco extension 0xA0-0xAF), the RoomSet flags in
 // RoomSetHeader.pad_c; both are pinned by `ext s7 look 1` (S7Conventions).
 // =================================================================================================================

 /// <summary>The decoded C1 interior extension (one SE tick's answer for the viewer's camera and its character).</summary>
    public struct C1Interior
    {
        /// <summary><see cref="S7Conventions.C1InteriorFlags"/>.</summary>
        public byte Flags;
        /// <summary>The room's <c>OxygenLevel(GridSize)</c> × 255 (information; 0 without a room).</summary>
        public byte RoomO2;
        /// <summary><c>MyOxygenRoom.Index</c> of the room holding the camera, <see cref="S7InteriorWire.NoRoom"/> without one.</summary>
        public uint RoomIndex;
        /// <summary>The entity id of the grid (or subgrid) whose airtight room holds the camera; 0 = none.</summary>
        public ulong RoomGridId;

        public bool Has(byte flag) { return (Flags & flag) != 0; }
        public bool InRoom { get { return Has(S7Conventions.C1InteriorFlags.InRoom); } }
        public bool CharacterInRoom { get { return Has(S7Conventions.C1InteriorFlags.CharacterInRoom); } }

        /// <summary>No room: every field zero except <see cref="RoomIndex"/> (none) and the Pressurization bit.</summary>
        public static C1Interior None(bool pressurization)
        {
            return new C1Interior
            {
                Flags = pressurization ? S7Conventions.C1InteriorFlags.Pressurization : (byte)0,
                RoomO2 = 0,
                RoomIndex = S7InteriorWire.NoRoom,
                RoomGridId = 0,
            };
        }

        public override string ToString()
        {
            return "flags=0x" + Flags.ToString("x2", CultureInfo.InvariantCulture) + " inRoom=" + InRoom + " roomGrid=" + RoomGridId.ToString(CultureInfo.InvariantCulture) +
                   " roomIndex=" + (RoomIndex == S7InteriorWire.NoRoom ? "none" : RoomIndex.ToString(CultureInfo.InvariantCulture)) + " roomO2=" + RoomO2.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
 /// Codec of the C1 interior extension (bytes 0xB0-0xBF of <see cref="SeFrame"/>):
 /// <code>
 /// 0xB0 u8 version (1) 0xB1 u8 flags 0xB2 u8 roomO2 0xB3 u8 pad (0)
 /// 0xB4 u32 roomIndex (0xFFFFFFFF none) 0xB8 u64 roomGridId (0 none)
 /// </code>
 /// A version byte of 0 means "not written" (an SE before S7): Subnautica then reads C1 flags bit 3 alone.
 /// </summary>
    public static unsafe class S7InteriorWire
    {
        public const int Version = S7Conventions.C1Interior.Version;
        public const int Offset = S7Conventions.C1Interior.Offset;
        public const int Size = S7Conventions.C1Interior.Size;
        public const uint NoRoom = 0xFFFFFFFFu;

        public const int OffVersion = 0xB0, OffFlags = 0xB1, OffRoomO2 = 0xB2, OffPad = 0xB3, OffRoomIndex = 0xB4, OffRoomGridId = 0xB8;

        /// <summary>Writes <paramref name="s"/> into the frame's extension bytes (version 1; the pad byte 0).</summary>
        public static void Write(ref SeFrame f, in C1Interior s)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                b[OffVersion] = (byte)Version;
                b[OffFlags] = s.Flags;
                b[OffRoomO2] = s.RoomO2;
                b[OffPad] = 0;
                *(uint*)(b + OffRoomIndex) = s.RoomIndex;
                *(ulong*)(b + OffRoomGridId) = s.RoomGridId;
            }
        }

        /// <summary>Zeroes the 16 bytes (version 0: read as an SE build before S7).</summary>
        public static void Erase(ref SeFrame f)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                for (int k = 0; k < Size; k++) b[Offset + k] = 0;
            }
        }

        /// <summary>The raw version byte (0 = no extension).</summary>
        public static byte ReadVersion(ref SeFrame f)
        {
            fixed (uint* p = &f.seq) return ((byte*)p)[OffVersion];
        }

        /// <summary>Reads the extension; false (and a default value) unless the version byte is 1.</summary>
        public static bool TryRead(ref SeFrame f, out C1Interior s)
        {
            s = default(C1Interior);
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                if (b[OffVersion] != Version) return false;
                s.Flags = b[OffFlags];
                s.RoomO2 = b[OffRoomO2];
                s.RoomIndex = *(uint*)(b + OffRoomIndex);
                s.RoomGridId = *(ulong*)(b + OffRoomGridId);
            }
            return true;
        }

        /// <summary>
 /// The camera-in-room answer of a C1 record: the extension's <c>InRoom</c> when it is written (version 1), else C1
 /// flags bit 3 alone (an SE before S7 never sets it: false).
 /// </summary>
        public static bool InRoom(ref SeFrame f)
        {
            C1Interior s;
            if (TryRead(ref f, out s)) return s.InRoom;
            return (f.flags & SeFrameFlags.CameraInAirtightRoom) != 0;
        }

        /// <summary><c>roomO2</c>: the level × 255 rounded half away from zero, clamped to 0..255; NaN gives 0.</summary>
        public static byte RoomO2Byte(float level)
        {
            if (float.IsNaN(level) || level <= 0f) return 0;
            if (level >= 1f) return 255;
            return (byte)Math.Round(level * 255.0, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>An inclusive range of grid cells (a RoomSet box; the <c>MySlimBlock.Min</c> convention).</summary>
    public struct CellBox : IEquatable<CellBox>
    {
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public CellBox(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public static CellBox Cell(int x, int y, int z) { return new CellBox(x, y, z, x, y, z); }

        public bool IsValid { get { return MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ; } }

        public long CellCount { get { return IsValid ? (long)(MaxX - MinX + 1) * (MaxY - MinY + 1) * (MaxZ - MinZ + 1) : 0; } }

        public bool Contains(int x, int y, int z) { return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ; }

        /// <summary>The Chebyshev distance (in cells) from the cell to the box: 0 inside, 1 on the shell around it, …</summary>
        public long Chebyshev(int x, int y, int z)
        {
            long dx = x < MinX ? (long)MinX - x : x > MaxX ? (long)x - MaxX : 0;
            long dy = y < MinY ? (long)MinY - y : y > MaxY ? (long)y - MaxY : 0;
            long dz = z < MinZ ? (long)MinZ - z : z > MaxZ ? (long)z - MaxZ : 0;
            return Math.Max(dx, Math.Max(dy, dz));
        }

        /// <summary>The smallest box holding both.</summary>
        public CellBox Union(CellBox o)
        {
            return new CellBox(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Min(MinZ, o.MinZ), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY), Math.Max(MaxZ, o.MaxZ));
        }

        public static CellBox From(CellRange r) { return new CellBox(r.MinX, r.MinY, r.MinZ, r.MaxX, r.MaxY, r.MaxZ); }

        public static CellBox From(RoomBox b) { return new CellBox(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z); }

        public RoomBox ToRoomBox() { return new RoomBox { min = new I32x3(MinX, MinY, MinZ), max = new I32x3(MaxX, MaxY, MaxZ) }; }

        public bool Equals(CellBox o) { return MinX == o.MinX && MinY == o.MinY && MinZ == o.MinZ && MaxX == o.MaxX && MaxY == o.MaxY && MaxZ == o.MaxZ; }
        public override bool Equals(object obj) { return obj is CellBox && Equals((CellBox)obj); }
        public override int GetHashCode() { return MinX * 73856093 ^ MinY * 19349663 ^ MinZ * 83492791 ^ MaxX * 2654435 ^ MaxY * 97 ^ MaxZ * 31; }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "[" + MinX.ToString(ci) + "," + MinY.ToString(ci) + "," + MinZ.ToString(ci) + ".." + MaxX.ToString(ci) + "," + MaxY.ToString(ci) + "," + MaxZ.ToString(ci) + "]";
        }
    }

    /// <summary>The decoded RoomSet header.</summary>
    public struct RoomSetInfo
    {
        public ulong EntityId;
        public uint Count;
        public uint Flags;
        public bool Truncated { get { return (Flags & S7Conventions.RoomSetFlags.Truncated) != 0; } }
        public uint Version { get { return Flags >> 28; } }
    }

    /// <summary>
 /// C4 type 7 <c>RoomSet</c> payloads (CONTRACT §5.4 "C4 RoomSet (S7)"): header {u64 entityId, u32 count, u32 flags
 /// (pad_c: <see cref="S7Conventions.RoomSetFlags"/>, version nibble 1)} and <c>count</c> boxes {i32 min[3], i32 max[3]}.
 /// </summary>
    public static unsafe class RoomSetCodec
    {
        public const int HeaderBytes = 16, BoxBytes = 24;

        public static int PayloadBytes(int count) { return HeaderBytes + count * BoxBytes; }

        public static uint FlagsFor(bool truncated)
        {
            return S7Conventions.RoomSetFlags.Version1 | (truncated ? S7Conventions.RoomSetFlags.Truncated : 0u);
        }

        /// <summary>The payload (without the ring's 8-byte frame).</summary>
        public static byte[] Encode(ulong entityId, IList<CellBox> boxes, bool truncated)
        {
            int n = boxes != null ? boxes.Count : 0;
            if (n > S7Conventions.RoomLimits.MaxBoxes && !truncated) throw new ArgumentException("more than " + S7Conventions.RoomLimits.MaxBoxes + " boxes without Truncated");
            var payload = new byte[PayloadBytes(n)];
            fixed (byte* p = payload)
            {
                var h = (RoomSetHeader*)p;
                h->entityId = entityId;
                h->count = (uint)n;
                *(uint*)(p + 0x0C) = FlagsFor(truncated);
                var b = (RoomBox*)(p + HeaderBytes);
                for (int i = 0; i < n; i++)
                {
                    if (!boxes[i].IsValid) throw new ArgumentException("box " + i + " is empty: " + boxes[i]);
                    b[i] = boxes[i].ToRoomBox();
                }
            }
            return payload;
        }

        /// <summary>
 /// Decodes a payload: false (with <paramref name="error"/>) when the size does not match the count, the count is
 /// above <see cref="S7Conventions.RoomLimits.MaxBoxes"/> or a box is empty; <paramref name="versionOk"/> false (and
 /// no boxes) when the version nibble is not 1 (ignored and counted by the caller).
 /// </summary>
        public static bool TryDecode(byte* p, int bytes, out RoomSetInfo info, List<CellBox> boxes, out bool versionOk, out string error)
        {
            info = default(RoomSetInfo);
            versionOk = false;
            error = null;
            if (boxes != null) boxes.Clear();
            if (p == null || bytes < HeaderBytes) { error = "RoomSet of " + bytes + " B"; return false; }
            var h = (RoomSetHeader*)p;
            info.EntityId = h->entityId;
            info.Count = h->count;
            info.Flags = *(uint*)(p + 0x0C);
            if (info.Count > S7Conventions.RoomLimits.MaxBoxes) { error = "RoomSet count " + info.Count + " > " + S7Conventions.RoomLimits.MaxBoxes; return false; }
            if ((long)bytes != PayloadBytes((int)info.Count)) { error = "RoomSet of " + bytes + " B for " + info.Count + " box(es)"; return false; }
            if (info.Version != 1) return true;
            versionOk = true;
            var b = (RoomBox*)(p + HeaderBytes);
            for (int i = 0; i < (int)info.Count; i++)
            {
                CellBox c = CellBox.From(b[i]);
                if (!c.IsValid) { error = "RoomSet box " + i + " empty: " + c; if (boxes != null) boxes.Clear(); return false; }
                if (boxes != null) boxes.Add(c);
            }
            return true;
        }

        public static bool TryDecode(byte[] payload, out RoomSetInfo info, List<CellBox> boxes, out bool versionOk, out string error)
        {
            if (payload == null) { info = default(RoomSetInfo); versionOk = false; error = "null payload"; if (boxes != null) boxes.Clear(); return false; }
            fixed (byte* p = payload) return TryDecode(p, payload.Length, out info, boxes, out versionOk, out error);
        }

        /// <summary>FNV-1a 64 of the flags and the boxes (the entity id excluded): the "box set changed" test of the producer.</summary>
        public static ulong ContentHash(IList<CellBox> boxes, bool truncated)
        {
            ulong h = 0xcbf29ce484222325UL;
            h = Mix(h, FlagsFor(truncated));
            int n = boxes != null ? boxes.Count : 0;
            h = Mix(h, (uint)n);
            for (int i = 0; i < n; i++)
            {
                CellBox c = boxes[i];
                h = Mix(h, (uint)c.MinX); h = Mix(h, (uint)c.MinY); h = Mix(h, (uint)c.MinZ);
                h = Mix(h, (uint)c.MaxX); h = Mix(h, (uint)c.MaxY); h = Mix(h, (uint)c.MaxZ);
            }
            return h;
        }

        private static ulong Mix(ulong h, uint v)
        {
            for (int k = 0; k < 4; k++)
            {
                h ^= (byte)(v >> (8 * k));
                h *= 0x100000001b3UL;
            }
            return h;
        }
    }

    /// <summary>The pure interior rules shared by both plugins (CONTRACT §5.4 "S7 conventions", <c>format interior.*</c>).</summary>
    public static class InteriorRules
    {
        /// <summary>The cell index of a grid-local coordinate: <c>round(t / g)</c>, halves away from zero.</summary>
        public static int CellOf(double t, double gridSize)
        {
            if (!(gridSize > 0)) throw new ArgumentOutOfRangeException("gridSize");
            double c = Math.Round(t / gridSize, MidpointRounding.AwayFromZero);
            if (double.IsNaN(c)) return 0;
            if (c > int.MaxValue) return int.MaxValue;
            if (c < int.MinValue) return int.MinValue;
            return (int)c;
        }

        /// <summary>
 /// <c>format interior.class</c>: <see cref="S7Conventions.InteriorClass.Inside"/> when the cell is in a room box,
 /// <see cref="S7Conventions.InteriorClass.Boundary"/> at Chebyshev distance 1 from a box (walls, floors, windows),
 /// else <see cref="S7Conventions.InteriorClass.Exterior"/> (also without boxes).
 /// </summary>
        public static byte Classify(int x, int y, int z, IReadOnlyList<CellBox> boxes)
        {
            if (boxes == null || boxes.Count == 0) return S7Conventions.InteriorClass.Exterior;
            bool boundary = false;
            for (int i = 0; i < boxes.Count; i++)
            {
                long d = boxes[i].Chebyshev(x, y, z);
                if (d == 0) return S7Conventions.InteriorClass.Inside;
                if (d == 1) boundary = true;
            }
            return boundary ? S7Conventions.InteriorClass.Boundary : S7Conventions.InteriorClass.Exterior;
        }

        /// <summary>The class of a part from its grid-local translation (metres) and the grid size (2.5 m large, 0.5 m small).</summary>
        public static byte ClassifyLocal(double tx, double ty, double tz, double gridSize, IReadOnlyList<CellBox> boxes)
        {
            if (boxes == null || boxes.Count == 0) return S7Conventions.InteriorClass.Exterior;
            return Classify(CellOf(tx, gridSize), CellOf(ty, gridSize), CellOf(tz, gridSize), boxes);
        }

        /// <summary>
 /// <c>format interior.sky</c>: true when the chunk takes the base-interior sky: <c>Inside</c> always; <c>Boundary</c>
 /// while C1 says <c>InRoom</c> with <c>roomGridId</c> = the chunk's entity or its root; <c>Exterior</c> never.
 /// </summary>
        public static bool InteriorSky(byte cls, bool c1InRoom, ulong c1RoomGridId, ulong chunkEntity, ulong chunkRoot)
        {
            if (cls == S7Conventions.InteriorClass.Inside) return true;
            if (cls != S7Conventions.InteriorClass.Boundary) return false;
            if (!c1InRoom || c1RoomGridId == 0) return false;
            return c1RoomGridId == chunkEntity || (chunkRoot != 0 && c1RoomGridId == chunkRoot);
        }

        /// <summary>A room box mapped to Subnautica straddles the sea surface (min y &lt; 0 &lt; max y): it gets a clip proxy.</summary>
        public static bool StraddlesSurface(double minY, double maxY)
        {
            return minY < 0.0 && maxY > 0.0;
        }

        /// <summary>The cell AABB of a set of cells (an empty set gives an invalid box).</summary>
        public static CellBox Aabb(IEnumerable<I32x3> cells)
        {
            bool any = false;
            var b = new CellBox(int.MaxValue, int.MaxValue, int.MaxValue, int.MinValue, int.MinValue, int.MinValue);
            if (cells != null)
                foreach (I32x3 c in cells)
                {
                    any = true;
                    if (c.x < b.MinX) b.MinX = c.x; if (c.y < b.MinY) b.MinY = c.y; if (c.z < b.MinZ) b.MinZ = c.z;
                    if (c.x > b.MaxX) b.MaxX = c.x; if (c.y > b.MaxY) b.MaxY = c.y; if (c.z > b.MaxZ) b.MaxZ = c.z;
                }
            return any ? b : new CellBox(0, 0, 0, -1, -1, -1);
        }

        /// <summary>
 /// The RoomSet box list of a grid (<c>format roomset.box</c>): the segmented boxes of every airtight room, or, when
 /// they number more than <see cref="S7Conventions.RoomLimits.MaxBoxes"/>, one box per room (its cell AABB) with
 /// <paramref name="truncated"/> set. <paramref name="perRoomBoxes"/>[i] and <paramref name="perRoomAabb"/>[i]
 /// describe room i. The output is sorted (min z, y, x, then max) so equal sets hash equal.
 /// </summary>
        public static void Plan(IList<List<CellBox>> perRoomBoxes, IList<CellBox> perRoomAabb, List<CellBox> output, out bool truncated)
        {
            if (output == null) throw new ArgumentNullException("output");
            output.Clear();
            truncated = false;
            int total = 0;
            if (perRoomBoxes != null) foreach (List<CellBox> l in perRoomBoxes) total += l != null ? l.Count : 0;
            if (total > S7Conventions.RoomLimits.MaxBoxes)
            {
                truncated = true;
                if (perRoomAabb != null) foreach (CellBox b in perRoomAabb) if (b.IsValid) output.Add(b);
            }
            else if (perRoomBoxes != null)
                foreach (List<CellBox> l in perRoomBoxes) if (l != null) foreach (CellBox b in l) if (b.IsValid) output.Add(b);
            output.Sort(Compare);
        }

        public static int Compare(CellBox a, CellBox b)
        {
            int c = a.MinZ.CompareTo(b.MinZ); if (c != 0) return c;
            c = a.MinY.CompareTo(b.MinY); if (c != 0) return c;
            c = a.MinX.CompareTo(b.MinX); if (c != 0) return c;
            c = a.MaxZ.CompareTo(b.MaxZ); if (c != 0) return c;
            c = a.MaxY.CompareTo(b.MaxY); if (c != 0) return c;
            return a.MaxX.CompareTo(b.MaxX);
        }
    }

    /// <summary>One candidate grid's raw answer at a probe point (SE: from its gas system this tick).</summary>
    public struct RoomProbe
    {
        public ulong GridId;
        /// <summary><c>IMyGridGasSystem.IsProcessingData</c>: the answer below is not valid this tick.</summary>
        public bool Processing;
        /// <summary>A room was found at the point (<c>GetOxygenRoomForCubeGridPosition</c>, else <c>GetSafeOxygenBlock(..).Room</c>).</summary>
        public bool HasRoom;
        public bool Airtight;
        public int RoomIndex;
        public float RoomO2;
    }

    /// <summary>
 /// The interior flag's hold rule (<c>format interior.flag</c>, "last value held while IsProcessingData"): the answer of
 /// a probe point (the camera, or one character's eye) over its candidate grids, nearest first. A grid whose gas system
 /// is processing keeps its last answer (its cell map is being rebuilt: <c>m_cubeRoom</c> is a fresh cube during the
 /// recompute, so a live read would flicker; S7.M10); the first candidate in an airtight room wins. One instance per
 /// probe point (the camera; each character id: v3 keying). Not thread-safe (SE's update thread).
 /// </summary>
    public sealed class RoomFlagHold
    {
        private struct Held
        {
            public bool HasRoom, Airtight;
            public int RoomIndex;
            public float RoomO2;
        }

        private readonly Dictionary<ulong, Held> _held = new Dictionary<ulong, Held>();

        /// <summary>Grids with a held answer.</summary>
        public int HeldGrids { get { return _held.Count; } }
        /// <summary>Answers taken from the hold (a processing grid) since the construction.</summary>
        public long Holds { get; private set; }

        /// <summary>
 /// The C1 interior value for this tick. Without <paramref name="pressurization"/> (the world lacks
 /// <c>EnableOxygen</c> ∧ <c>EnableOxygenPressurization</c>) the answer is "no room" with every flag clear.
 /// <c>Processing</c> is set when a candidate consulted before (or as) the winner was processing; <c>Breached</c>
 /// when no candidate is in an airtight room and one is in a room that is not airtight.
 /// </summary>
        public C1Interior Decide(IList<RoomProbe> probes, int count, bool pressurization)
        {
            if (!pressurization) return C1Interior.None(false);
            C1Interior r = C1Interior.None(true);
            bool processing = false, breached = false;
            int n = probes == null ? 0 : Math.Min(count, probes.Count);
            for (int i = 0; i < n; i++)
            {
                RoomProbe p = probes[i];
                Held h;
                if (p.Processing)
                {
                    processing = true;
                    if (!_held.TryGetValue(p.GridId, out h)) continue;
                    Holds++;
                }
                else
                {
                    h = new Held { HasRoom = p.HasRoom, Airtight = p.Airtight, RoomIndex = p.RoomIndex, RoomO2 = p.RoomO2 };
                    _held[p.GridId] = h;
                }
                if (h.HasRoom && h.Airtight)
                {
                    r.Flags |= S7Conventions.C1InteriorFlags.InRoom;
                    r.RoomGridId = p.GridId;
                    r.RoomIndex = h.RoomIndex >= 0 ? (uint)h.RoomIndex : S7InteriorWire.NoRoom;
                    r.RoomO2 = S7InteriorWire.RoomO2Byte(h.RoomO2);
                    if (processing) r.Flags |= S7Conventions.C1InteriorFlags.Processing;
                    return r;
                }
                if (h.HasRoom) breached = true;
            }
            if (breached) r.Flags |= S7Conventions.C1InteriorFlags.Breached;
            if (processing) r.Flags |= S7Conventions.C1InteriorFlags.Processing;
            return r;
        }

        /// <summary>Drops the held answers of grids not in <paramref name="alive"/> (grids that left the set or closed).</summary>
        public void Retain(ICollection<ulong> alive)
        {
            if (_held.Count == 0) return;
            List<ulong> gone = null;
            foreach (ulong id in _held.Keys) if (alive == null || !alive.Contains(id)) (gone = gone ?? new List<ulong>()).Add(id);
            if (gone != null) foreach (ulong id in gone) _held.Remove(id);
        }

        public void Forget(ulong gridId) { _held.Remove(gridId); }

        public void Clear() { _held.Clear(); }
    }
}
