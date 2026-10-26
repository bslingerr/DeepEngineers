using System;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S12 rules (CONTRACT §5.4 "S12 conventions"): pure functions and small state machines both plugins, the tests and the
 // Python twin (tools/s12/s12_ref.py) share. Game-free, Unity-free.
 // =================================================================================================================

 /// <summary>The room membership of a cell: 0 none (no room, or a room that is not airtight), else the room's key (&gt; 0).</summary>
    public delegate int S12CellRoom(int x, int y, int z);

    /// <summary>
 /// The block in cell (x, y, z) seals its face in direction <c>dir</c> (0 +x, 1 -x, 2 +y, 3 -y, 4 +z, 5 -z): SE's
 /// <c>MyGridGasSystem.IsAirtightBlock(block, cell, normal)</c>. False for an empty cell. Used
 /// only by S12.M9 (round 2's mid-plane: the air of a sealed block's cell ended at the cell's mid-plane, so a character
 /// pressed against a window from inside swam); format air.volume itself ends the air at the room cells' faces.
 /// </summary>
    public delegate bool S12CellSeal(int x, int y, int z, int dir);

    /// <summary>The answer of the oxygen-volume test at one point (format air.volume).</summary>
    public struct S12Depth
    {
        /// <summary>Signed depth in metres: &gt; 0 inside the room's volume (distance to its nearest boundary), &lt; 0 outside.</summary>
        public double DepthM;
        /// <summary>The room key the depth refers to (inside: the cell's room; outside: the nearest neighbouring room), 0 none.</summary>
        public int Room;
        public int CellX, CellY, CellZ;
        public bool CellInRoom;
    }

    /// <summary>format air.volume: the signed depth of a grid-local point in the airtight rooms' air.</summary>
    public static class S12AirVolume
    {
        private static readonly int[] Ax = { 1, -1, 0, 0, 0, 0 };
        private static readonly int[] Ay = { 0, 0, 1, -1, 0, 0 };
        private static readonly int[] Az = { 0, 0, 0, 0, 1, -1 };

        /// <summary>The cell index of a grid-local coordinate (metres): round(t / g), halves away from zero.</summary>
        public static int Cell(double t, double g)
        {
            return InteriorRules.CellOf(t, g);
        }

        public static int Opposite(int dir) { return dir ^ 1; }

        /// <summary>
 /// The signed depth of a grid-local point (metres) with grid size <paramref name="g"/> (format air.volume: the room's air
 /// ends at its cells' faces, where SE's airtight faces are). <paramref name="room"/> answers a cell's airtight room key.
 /// <paramref name="seal"/> is S12.M9's only (null in the rule): a room cell's own sealed face then ends its air at the
 /// cell's mid-plane.
 /// </summary>
        public static S12Depth Depth(double px, double py, double pz, double g, S12CellRoom room, S12CellSeal seal = null)
        {
            if (!(g > 0)) throw new ArgumentOutOfRangeException("g");
            if (room == null) throw new ArgumentNullException("room");
            int cx = Cell(px, g), cy = Cell(py, g), cz = Cell(pz, g);
            double fx = px / g - cx, fy = py / g - cy, fz = pz / g - cz;
            var r = new S12Depth { CellX = cx, CellY = cy, CellZ = cz };
            int own = room(cx, cy, cz);
            if (own > 0)
            {
                r.CellInRoom = true;
                r.Room = own;
                double best = 0.5;
                for (int i = 0; i < 6; i++)
                {
                    if (room(cx + Ax[i], cy + Ay[i], cz + Az[i]) == own) continue;
                    double d = FaceDistance(i, fx, fy, fz);
                    if (seal != null && seal(cx, cy, cz, i)) d -= 0.5; // the block seals this face: the air ends at the mid-plane
                    if (d < best) best = d;
                }
                r.DepthM = best * g;
                return r;
            }
            double near = 0.5;
            int nearRoom = 0;
            for (int i = 0; i < 6; i++)
            {
                int nx = cx + Ax[i], ny = cy + Ay[i], nz = cz + Az[i];
                int k = room(nx, ny, nz);
                if (k <= 0) continue;
                double d = FaceDistance(i, fx, fy, fz);
                if (seal != null && seal(nx, ny, nz, Opposite(i))) d += 0.5; // that cell's air begins at its mid-plane
                if (nearRoom == 0 || d < near) { near = d; nearRoom = k; }
            }
            r.Room = nearRoom;
            r.DepthM = -near * g;
            return r;
        }

        /// <summary>The distance (in cells) from the point to face i of its cell: 1/2 - s f_a (never negative).</summary>
        private static double FaceDistance(int i, double fx, double fy, double fz)
        {
            double f = i < 2 ? fx : i < 4 ? fy : fz;
            double s = (i & 1) == 0 ? 1.0 : -1.0;
            double d = 0.5 - s * f;
            return d < 0 ? 0 : d;
        }

        /// <summary>The hysteresis margin h of format air.hold (metres) for grid size <paramref name="g"/>.</summary>
        public static double Hysteresis(double g)
        {
            return g * S12Conventions.Air.HysteresisPermille / 1000.0;
        }

        /// <summary>format air.hold: the next answer from the previous one and the signed depth.</summary>
        public static bool Hold(bool wasIn, double depthM, double h)
        {
            return wasIn ? depthM > -h : depthM > h;
        }

        /// <summary>
 /// format air.water: the latch of a probe point in a flooding room (heights along the room's up axis above its water
 /// floor, metres): water once below the plane by WaterEnterMm, air again once above it by WaterExitMm (S2b's swim
 /// entry and grounded exit, so the picture and the swim model agree). No plane (level 0): dry.
 /// </summary>
        public static bool Wet(bool wasWet, double heightAboveFloorM, double level, double boxHeightM)
        {
            if (!(level > 0) || double.IsNaN(heightAboveFloorM)) return false;
            double plane = level * boxHeightM;
            return wasWet ? heightAboveFloorM < plane + S12Conventions.Air.WaterExitMm / 1000.0
                          : heightAboveFloorM < plane - S12Conventions.Air.WaterEnterMm / 1000.0;
        }

        /// <summary>
 /// format air.camera: the camera's answer for C1 InRoom. First person and the spectator: the camera's own; third
 /// person: the camera's and the local character's eye's (a character outside the hull never gives the camera air).
 /// </summary>
        public static bool CameraInRoom(bool cameraIn, bool thirdPerson, bool characterIn)
        {
            return cameraIn && (!thirdPerson || characterIn);
        }
    }

    /// <summary>
 /// format air.hold for one probe point over its candidate grids: the per-grid previous answer (keyed by grid id), the first
 /// candidate inside wins. Not thread-safe (SE's update thread). The S7 hold (<see cref="RoomFlagHold"/>) is what S12.M1
 /// brings back.
 /// </summary>
    public sealed class S12AirHold
    {
        private readonly Dictionary<ulong, bool> _was = new Dictionary<ulong, bool>();
        public long Flips { get; private set; }

        public bool Was(ulong grid) { bool w; return _was.TryGetValue(grid, out w) && w; }

        /// <summary>One grid's answer this tick: <paramref name="known"/> false = no room cells known at the point (no snapshot while processing): out.</summary>
        public bool Step(ulong grid, bool known, double depthM, double h)
        {
            bool was = Was(grid);
            bool now = known && S12AirVolume.Hold(was, depthM, h);
            if (now != was) Flips++;
            if (now) _was[grid] = true;
            else _was.Remove(grid);
            return now;
        }

        public void Forget(ulong grid) { _was.Remove(grid); }
        public void Clear() { _was.Clear(); }
        public int Count { get { return _was.Count; } }

        public void Retain(ICollection<ulong> alive)
        {
            if (_was.Count == 0) return;
            List<ulong> gone = null;
            foreach (ulong id in _was.Keys) if (alive == null || !alive.Contains(id)) (gone = gone ?? new List<ulong>()).Add(id);
            if (gone != null) foreach (ulong id in gone) _was.Remove(id);
        }
    }

    /// <summary>format flood.fill: the breach's fill time (Torricelli) and the rising level; format flood.drain's water from SE's oxygen.</summary>
    public static class S12Fill
    {
        /// <summary>The fill time T (ms) of a room of <paramref name="cells"/> cells of size <paramref name="g"/> through <paramref name="holes"/> hole cells at <paramref name="depthM"/>.</summary>
        public static double FillMs(int cells, double g, int holes, double depthM)
        {
            if (cells <= 0 || !(g > 0)) return S12Conventions.Flood.MinFillMs;
            double v = cells * g * g * g;
            int h = Math.Max(1, Math.Min(S12Conventions.Flood.HoleCellsMax, holes));
            double a = h * g * g;
            double d = Math.Max(S12Conventions.Flood.MinDepthMm / 1000.0, double.IsNaN(depthM) ? 0 : depthM);
            double q = S12Conventions.Flood.DischargePermille / 1000.0 * a * Math.Sqrt(2.0 * S12Conventions.Flood.GravityMmS2 / 1000.0 * d);
            double t = q > 0 ? v / q * 1000.0 : S12Conventions.Flood.MaxFillMs;
            return Math.Max(S12Conventions.Flood.MinFillMs, Math.Min(S12Conventions.Flood.MaxFillMs, t));
        }

        public static double Level(double elapsedMs, double fillMs)
        {
            if (!(fillMs > 0)) return 1.0;
            double l = elapsedMs / fillMs;
            return l <= 0 ? 0 : l >= 1 ? 1 : l;
        }

        /// <summary>
 /// format flood.drain: the water an airtight room's oxygen gives: (D - o2) / D clamped to [0, 1], D = the room's
 /// environment oxygen (MyOxygenRoom.EnvironmentOxygen: a room SE rebuilds on a breathable planet starts at it) when it is at
 /// least DryEnvMinPermille / 1000, else 1.
 /// </summary>
        public static double Water(double oxygenLevel, double envOxygen)
        {
            if (double.IsNaN(oxygenLevel)) return 0;
            double env = double.IsNaN(envOxygen) ? 0 : envOxygen;
            double dry = env * 1000.0 >= S12Conventions.Flood.DryEnvMinPermille ? Math.Min(env, 1.0) : 1.0;
            double l = (dry - oxygenLevel) / dry;
            return l <= 0 ? 0 : l >= 1 ? 1 : l;
        }
    }

    /// <summary>One tick's observation of a tracked room (SE: its watched cell on the live gas system).</summary>
    public struct S12RoomObs
    {
        /// <summary>The gas system is processing: nothing is known this tick (the track keeps its state; a rising breach keeps rising).</summary>
        public bool Processing;
        /// <summary>A room exists at the watched cell (false: a block was placed there, MyGridGasSystem.AddBlock nulls it: nothing is decided).</summary>
        public bool Known;
        /// <summary>The cell lies in an airtight room now.</summary>
        public bool Airtight;
        /// <summary>

 /// not airtight only because a door opened: no block of the grid was removed or destroyed since the track last saw the room
 /// airtight. True keeps the room dry (<see cref="S12FloodTrack.DoorOpen"/>, its last airtight cells stay air); false (the
 /// default, the twin's vectors) is the lockout: a room that is not airtight under the sea floods.
 /// </summary>
        public bool DoorOnly;
        /// <summary>That room's MyOxygenRoom.OxygenLevel (0..1; NaN unknown).</summary>
        public double Oxygen;
        /// <summary>That room's MyOxygenRoom.EnvironmentOxygen (0..1).</summary>
        public double EnvOxygen;
        /// <summary>The room's box centre is under the sea.</summary>
        public bool UnderSea;
        /// <summary>An Air Vent of the room depressurizes it (format vent.event).</summary>
        public bool VentOut;
    }

    /// <summary>
 /// formats flood.breach, flood.drain, flood.fill: one room's flood track. <see cref="Step"/> once per SE tick with the
 /// observation; <see cref="StartSeq"/> counts the starts (one splash each, format flood.plane). Game-free.
 /// </summary>
    public sealed class S12FloodTrack
    {
        public int State { get; private set; }
        public int Source { get; private set; }
        public double Level { get; private set; }
        public double RatePerS { get; private set; }
        public uint StartSeq { get; private set; }
        public double FillMs { get; private set; }
        public double DrainMs { get; private set; }
        /// <summary>The track is over (dry after draining): the caller drops it.</summary>
        public bool Ended { get; private set; }
        /// <summary> (Subnautica's rule): the room is open through a door only (<see cref="S12RoomObs.DoorOnly"/>) and stays dry;
 /// its last airtight cells answer air until it is airtight again or a block of the hull goes.</summary>
        public bool DoorOpen { get; private set; }

        private bool _seenAirtight;
        private double _riseStartMs, _riseFromLevel, _capAtRepair, _prevLevel, _prevMs, _lastStepMs;
        private double _repairSinceMs = -1, _drainStartMs = double.NegativeInfinity;

        /// <summary>A breach rises in <paramref name="fillMs"/> for a full room (the caller computes it with <see cref="S12Fill.FillMs"/>).</summary>
        public void SetFill(double fillMs) { FillMs = fillMs; }

        /// <summary>A full room drains in <paramref name="drainMs"/> at the fastest (0: at once, S12.M3).</summary>
        public void SetDrain(double drainMs) { DrainMs = drainMs; }

        /// <summary>format flood.breach's debounce: a breach drains only after its room stayed airtight RepairMs; a breach
 /// within QuietMs of the drain's start goes on as the same flood (no new start). 0 and 0: round 2's flip (S12.M12).</summary>
        public double RepairMs { get; private set; }
        public double QuietMs { get; private set; }
        public void SetDebounce(double repairMs, double quietMs) { RepairMs = repairMs; QuietMs = quietMs; }

        /// <summary>The track starts as "seen airtight" (it was taken from a snapshot of an airtight room).</summary>
        public S12FloodTrack(bool seenAirtight = true)
        {
            _seenAirtight = seenAirtight;
            DrainMs = S12Conventions.Flood.DrainMs;
            RepairMs = S12Conventions.Flood.RepairDebounceMs;
            QuietMs = S12Conventions.Flood.ReBreachQuietMs;
        }

        public void Step(double nowMs, S12RoomObs o)
        {
            double dt = _lastStepMs > 0 && nowMs > _lastStepMs ? nowMs - _lastStepMs : 0;
            _lastStepMs = nowMs;
            if (o.Processing || !o.Known) { Advance(nowMs); Rate(nowMs); return; }
            double w = S12Fill.Water(o.Oxygen, o.EnvOxygen);
            switch (State)
            {
                case S12Conventions.FloodState.None:
                    if (o.Airtight)
                    {
                        _seenAirtight = true;
                        DoorOpen = false;
                        if (o.UnderSea && o.VentOut && w * 1000.0 >= S12Conventions.Flood.OxygenStartPermille)
                        {
                            Start(nowMs, S12Conventions.FloodSource.Oxygen);
                            Level = w;
                        }
                    }
                    else if (_seenAirtight && o.UnderSea)
                    {
                        if (o.DoorOnly) { DoorOpen = true; break; }   // the room stays dry
                        DoorOpen = false;
                        Start(nowMs, S12Conventions.FloodSource.Breach);
                        _riseFromLevel = 0;
                        Level = 0;
                    }
                    break;
                case S12Conventions.FloodState.Rising:
                case S12Conventions.FloodState.Full:
                    if (Source == S12Conventions.FloodSource.Breach)
                    {
                        // format flood.breach's debounce: the room must stay airtight RepairMs (a door cycling does not drain it)
                        if (o.Airtight && _repairSinceMs < 0) _repairSinceMs = nowMs;
                        if (!o.Airtight) _repairSinceMs = -1;
                        if (o.Airtight && nowMs - _repairSinceMs >= RepairMs)
                        {
                            _repairSinceMs = -1;
                            _drainStartMs = nowMs;
                            _capAtRepair = Level; State = S12Conventions.FloodState.Draining; Level = DrainStep(w, 0); EndIfDry();
                        }
                        else Advance(nowMs);
                    }
                    else
                    {
                        if (!o.Airtight) { Rebreach(nowMs); break; }
                        if (w < Level - 1e-6) { _drainStartMs = nowMs; _capAtRepair = Level; State = S12Conventions.FloodState.Draining; Level = DrainStep(w, 0); EndIfDry(); }
                        else { Level = w; State = w * 1000.0 >= S12Conventions.Flood.FullPermille ? S12Conventions.FloodState.Full : S12Conventions.FloodState.Rising; }
                    }
                    break;
                case S12Conventions.FloodState.Draining:
                    if (!o.Airtight)
                    {
                        // a breach soon after the drain started is the same flood (no new splash): the door cycling again
                        if (Source == S12Conventions.FloodSource.Breach && QuietMs > 0 && nowMs - _drainStartMs < QuietMs) Continue(nowMs);
                        else Rebreach(nowMs);
                        break;
                    }
                    if (o.VentOut && w > Level + 1e-6)
                    {
                        Level = w;
                        _capAtRepair = Math.Max(_capAtRepair, Level);
                        Source = S12Conventions.FloodSource.Oxygen;
                        State = S12Conventions.FloodState.Rising;
                        break;
                    }
                    Level = DrainStep(w, dt);
                    EndIfDry();
                    break;
            }
            Rate(nowMs);
        }

        private void EndIfDry()
        {
            if (State == S12Conventions.FloodState.Draining && Level * 1000.0 <= S12Conventions.Flood.DryPermille) { Level = 0; Ended = true; State = S12Conventions.FloodState.None; }
        }

        /// <summary>format flood.drain: toward the oxygen's water, never faster than a full room in DrainMs, never above the level at the repair.</summary>
        private double DrainStep(double w, double dt)
        {
            double floor = DrainMs > 0 ? Level - dt / DrainMs : double.NegativeInfinity;
            double l = Math.Max(w, floor);
            l = Math.Min(_capAtRepair, l);
            return l <= 0 ? 0 : l;
        }

        private void Start(double nowMs, int source)
        {
            Source = source;
            State = S12Conventions.FloodState.Rising;
            StartSeq++;
            _riseStartMs = nowMs;
            Ended = false;
        }

        private void Continue(double nowMs)
        {
            _riseFromLevel = Level;
            State = S12Conventions.FloodState.Rising;
            _riseStartMs = nowMs;
            _repairSinceMs = -1;
        }

        private void Rebreach(double nowMs)
        {
            _riseFromLevel = Level;
            Source = S12Conventions.FloodSource.Breach;
            State = S12Conventions.FloodState.Rising;
            StartSeq++;
            _riseStartMs = nowMs;
            _repairSinceMs = -1;
        }

        private void Advance(double nowMs)
        {
            if (Source != S12Conventions.FloodSource.Breach || (State != S12Conventions.FloodState.Rising && State != S12Conventions.FloodState.Full)) return;
            double fill = FillMs > 0 ? FillMs : S12Conventions.Flood.MinFillMs;
            // from a partial level (a re-breach) the rest fills at the same rate: t0 shifted by the level already in
            double l = S12Fill.Level(nowMs - _riseStartMs + _riseFromLevel * fill, fill);
            Level = l;
            State = l * 1000.0 >= S12Conventions.Flood.FullPermille ? S12Conventions.FloodState.Full : S12Conventions.FloodState.Rising;
            if (State == S12Conventions.FloodState.Full) Level = 1.0;
        }

        private void Rate(double nowMs)
        {
            if (_prevMs > 0 && nowMs > _prevMs) RatePerS = (Level - _prevLevel) / ((nowMs - _prevMs) / 1000.0);
            else RatePerS = 0;
            if (State == S12Conventions.FloodState.Full || State == S12Conventions.FloodState.None) RatePerS = 0;
            _prevMs = nowMs;
            _prevLevel = Level;
        }
    }

    /// <summary>A vent's venting edges (format vent.event): one start per off → on.</summary>
    public sealed class S12VentEdge
    {
        public bool On { get; private set; }
        public uint StartSeq { get; private set; }
        public int State { get; private set; }

        /// <summary>One tick: venting now, and whether it depressurizes. Returns true at a start.</summary>
        public bool Step(bool venting, bool depressurizing)
        {
            bool start = venting && !On;
            On = venting;
            State = venting ? (depressurizing ? S12Conventions.VentState.Depressurizing : S12Conventions.VentState.Pressurizing) : S12Conventions.VentState.None;
            if (start) StartSeq++;
            return start;
        }
    }

    /// <summary>The interior look's decisions (formats look.when, look.preset).</summary>
    public static class S12LookRules
    {
        public static bool Applies(bool lookOn, bool inRoom, double cameraSnY)
        {
            return lookOn && inRoom && cameraSnY < 0;
        }

        public static double BounceIntensity(double brightestLamp)
        {
            if (!(brightestLamp > 0)) return 0;
            double v = S12Conventions.Look.BouncePermille / 1000.0 * brightestLamp;
            double max = S12Conventions.Look.BounceMaxIntensityPermille / 1000.0;
            return v > max ? max : v;
        }

        /// <summary>A channel of a 0xRRGGBB colour (0 red, 1 green, 2 blue) as 0..1.</summary>
        public static double Channel(int rgb, int c)
        {
            int shift = c == 0 ? 16 : c == 1 ? 8 : 0;
            return ((rgb >> shift) & 0xFF) / 255.0;
        }
    }

    /// <summary>The C6 block's header state (format c6.s12).</summary>
    public sealed class S12Header
    {
        public byte Version, Flags, Count;
        public uint WorldEpoch, FloodSeq, VentSeq;
        public ulong Tick;
    }

    /// <summary>One C6 row (format c6.s12; field table <see cref="S12Conventions.RowFields"/>).</summary>
    public struct S12Row
    {
        public long GridId;
        public uint TrackId;
        public byte Kind, State, Source, BoxCount;
        public float Level, RatePerS;
        public uint StartSeq;
        public byte GridSize, UpAxis;
        public ushort Flags;
        public float Px, Py, Pz, Dx, Dy, Dz;
        /// <summary>MaxBoxes x 6 (min x, y, z, max x, y, z) grid cells.</summary>
        public short[] Boxes;

        public CellBox Box(int i)
        {
            if (Boxes == null || i < 0 || i >= BoxCount) return new CellBox(0, 0, 0, -1, -1, -1);
            int o = i * 6;
            return new CellBox(Boxes[o], Boxes[o + 1], Boxes[o + 2], Boxes[o + 3], Boxes[o + 4], Boxes[o + 5]);
        }

        public void SetBox(int i, CellBox b)
        {
            if (Boxes == null) Boxes = new short[S12Conventions.C6.MaxBoxes * 6];
            int o = i * 6;
            Boxes[o] = Clamp16(b.MinX); Boxes[o + 1] = Clamp16(b.MinY); Boxes[o + 2] = Clamp16(b.MinZ);
            Boxes[o + 3] = Clamp16(b.MaxX); Boxes[o + 4] = Clamp16(b.MaxY); Boxes[o + 5] = Clamp16(b.MaxZ);
        }

        private static short Clamp16(int v) { return (short)(v < short.MinValue ? short.MinValue : v > short.MaxValue ? short.MaxValue : v); }
    }

    /// <summary>The C6 region-space codec of the S12 block (its own seqlock word at its offset 0).</summary>
    public static unsafe class S12Wire
    {
        public static void Encode(byte* buf, S12Header h, S12Row[] rows, int count)
        {
            for (int i = 0; i < S12Conventions.C6.Size; i++) buf[i] = 0;
            int n = Math.Max(0, Math.Min(S12Conventions.C6.MaxRows, Math.Min(count, rows == null ? 0 : rows.Length)));
            buf[0x04] = h.Version;
            buf[0x05] = h.Flags;
            buf[0x06] = (byte)n;
            *(uint*)(buf + 0x08) = h.WorldEpoch;
            *(uint*)(buf + 0x0C) = h.FloodSeq;
            *(uint*)(buf + 0x10) = h.VentSeq;
            *(ulong*)(buf + 0x14) = h.Tick;
            for (int i = 0; i < n; i++)
            {
                byte* r = buf + S12Conventions.C6.HeaderSize + i * S12Conventions.C6.RowSize;
                S12Row w = rows[i];
                *(long*)(r + 0x00) = w.GridId;
                *(uint*)(r + 0x08) = w.TrackId;
                r[0x0C] = w.Kind; r[0x0D] = w.State; r[0x0E] = w.Source;
                byte bc = (byte)Math.Min((int)w.BoxCount, S12Conventions.C6.MaxBoxes);
                r[0x0F] = bc;
                *(float*)(r + 0x10) = w.Level;
                *(float*)(r + 0x14) = w.RatePerS;
                *(uint*)(r + 0x18) = w.StartSeq;
                r[0x1C] = w.GridSize; r[0x1D] = w.UpAxis;
                *(ushort*)(r + 0x1E) = w.Flags;
                *(float*)(r + 0x20) = w.Px; *(float*)(r + 0x24) = w.Py; *(float*)(r + 0x28) = w.Pz;
                *(float*)(r + 0x2C) = w.Dx; *(float*)(r + 0x30) = w.Dy; *(float*)(r + 0x34) = w.Dz;
                for (int k = 0; k < bc * 6; k++) *(short*)(r + 0x38 + k * 2) = w.Boxes != null && k < w.Boxes.Length ? w.Boxes[k] : (short)0;
            }
        }

        /// <summary>Decodes into <paramref name="h"/> and <paramref name="rows"/> (MaxRows long); returns the row count (0 for an absent block).</summary>
        public static int Decode(byte* buf, S12Header h, S12Row[] rows)
        {
            h.Version = buf[0x04];
            h.Flags = buf[0x05];
            h.Count = buf[0x06];
            h.WorldEpoch = *(uint*)(buf + 0x08);
            h.FloodSeq = *(uint*)(buf + 0x0C);
            h.VentSeq = *(uint*)(buf + 0x10);
            h.Tick = *(ulong*)(buf + 0x14);
            if (h.Version != S12Conventions.C6.Version || rows == null) return 0;
            int n = Math.Min(Math.Min((int)h.Count, S12Conventions.C6.MaxRows), rows.Length);
            for (int i = 0; i < n; i++)
            {
                byte* r = buf + S12Conventions.C6.HeaderSize + i * S12Conventions.C6.RowSize;
                var w = new S12Row();
                w.GridId = *(long*)(r + 0x00);
                w.TrackId = *(uint*)(r + 0x08);
                w.Kind = r[0x0C]; w.State = r[0x0D]; w.Source = r[0x0E];
                w.BoxCount = (byte)Math.Min((int)r[0x0F], S12Conventions.C6.MaxBoxes);
                w.Level = *(float*)(r + 0x10);
                w.RatePerS = *(float*)(r + 0x14);
                w.StartSeq = *(uint*)(r + 0x18);
                w.GridSize = r[0x1C]; w.UpAxis = r[0x1D];
                w.Flags = *(ushort*)(r + 0x1E);
                w.Px = *(float*)(r + 0x20); w.Py = *(float*)(r + 0x24); w.Pz = *(float*)(r + 0x28);
                w.Dx = *(float*)(r + 0x2C); w.Dy = *(float*)(r + 0x30); w.Dz = *(float*)(r + 0x34);
                w.Boxes = rows[i].Boxes ?? new short[S12Conventions.C6.MaxBoxes * 6];
                Array.Clear(w.Boxes, 0, w.Boxes.Length);
                for (int k = 0; k < w.BoxCount * 6; k++) w.Boxes[k] = *(short*)(r + 0x38 + k * 2);
                rows[i] = w;
            }
            return n;
        }

        public static void Write(byte* c6Region, S12Header h, S12Row[] rows, int count)
        {
            byte* buf = stackalloc byte[S12Conventions.C6.Size];
            Encode(buf, h, rows, count);
            Seqlock.WriteBytes(c6Region + S12Conventions.C6.Offset, buf, S12Conventions.C6.Size);
        }

        /// <summary>Fresh = a coherent copy decoded (check <see cref="S12Header.Version"/>).</summary>
        public static SeqlockReadStatus TryRead(byte* c6Region, S12Header h, S12Row[] rows, out int count, out uint seq)
        {
            byte* buf = stackalloc byte[S12Conventions.C6.Size];
            count = 0;
            SeqlockReadStatus st = Seqlock.TryReadBytes(c6Region + S12Conventions.C6.Offset, buf, S12Conventions.C6.Size, Seqlock.MaxTries, out seq);
            if (st == SeqlockReadStatus.Fresh) count = Decode(buf, h, rows);
            return st;
        }
    }

    /// <summary>The box set of a flood row: at most MaxBoxes boxes; a larger room is its cell AABB (one box).</summary>
    public static class S12Boxes
    {
        /// <summary>
 /// format flood.plane: the row's boxes, the MaxBoxes largest of the room's water boxes (by cells, then by their

 /// </summary>
        public static List<CellBox> ForRow(IList<CellBox> roomBoxes)
        {
            var l = new List<CellBox>();
            if (roomBoxes == null || roomBoxes.Count == 0) return l;
            foreach (CellBox b in roomBoxes) if (b.IsValid) l.Add(b);
            l.Sort(Bigger);
            if (l.Count > S12Conventions.C6.MaxBoxes) l.RemoveRange(S12Conventions.C6.MaxBoxes, l.Count - S12Conventions.C6.MaxBoxes);
            return l;
        }

        private static int Bigger(CellBox a, CellBox b)
        {
            int c = b.CellCount.CompareTo(a.CellCount);
            if (c != 0) return c;
            if (a.MinZ != b.MinZ) return a.MinZ.CompareTo(b.MinZ);
            if (a.MinY != b.MinY) return a.MinY.CompareTo(b.MinY);
            return a.MinX.CompareTo(b.MinX);
        }

        private static long Key(int x, int y, int z)
        {
            return ((long)(x + 0x100000) << 42) | ((long)(y + 0x100000) << 21) | (long)(z + 0x100000);
        }

        /// <summary>
 /// format flood.plane: a cell set decomposed into boxes, greedily in the order z, y, x of the lowest remaining cell:
 /// the run along x, then grown along y while every row is in the set, then along z while every layer is. The Python
 /// twin does the same. Coordinates within ±2^20.
 /// </summary>
        public static List<CellBox> Decompose(IEnumerable<CellBox> unitCells)
        {
            var left = new HashSet<long>();
            var order = new List<CellBox>();
            if (unitCells != null)
                foreach (CellBox c in unitCells)
                    if (left.Add(Key(c.MinX, c.MinY, c.MinZ))) order.Add(CellBox.Cell(c.MinX, c.MinY, c.MinZ));
            order.Sort((a, b) => a.MinZ != b.MinZ ? a.MinZ.CompareTo(b.MinZ) : a.MinY != b.MinY ? a.MinY.CompareTo(b.MinY) : a.MinX.CompareTo(b.MinX));
            var boxes = new List<CellBox>();
            foreach (CellBox c in order)
            {
                int x0 = c.MinX, y0 = c.MinY, z0 = c.MinZ;
                if (!left.Contains(Key(x0, y0, z0))) continue;
                int x1 = x0;
                while (left.Contains(Key(x1 + 1, y0, z0))) x1++;
                int y1 = y0;
                while (true)
                {
                    bool all = true;
                    for (int x = x0; x <= x1 && all; x++) all = left.Contains(Key(x, y1 + 1, z0));
                    if (!all) break;
                    y1++;
                }
                int z1 = z0;
                while (true)
                {
                    bool all = true;
                    for (int y = y0; y <= y1 && all; y++)
                        for (int x = x0; x <= x1 && all; x++) all = left.Contains(Key(x, y, z1 + 1));
                    if (!all) break;
                    z1++;
                }
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++) left.Remove(Key(x, y, z));
                boxes.Add(new CellBox(x0, y0, z0, x1, y1, z1));
            }
            return boxes;
        }

        /// <summary>The union box of a row's boxes (its floor and height along the up axis give the plane).</summary>
        public static CellBox Union(IList<CellBox> boxes)
        {
            if (boxes == null || boxes.Count == 0) return new CellBox(0, 0, 0, -1, -1, -1);
            CellBox u = boxes[0];
            for (int i = 1; i < boxes.Count; i++) u = u.Union(boxes[i]);
            return u;
        }

        /// <summary>The up axis code (format c6.s12) of a grid-local up vector: the largest component's axis and sign.</summary>
        public static int UpAxisOf(double ux, double uy, double uz)
        {
            double ax = Math.Abs(ux), ay = Math.Abs(uy), az = Math.Abs(uz);
            if (ax >= ay && ax >= az) return ux >= 0 ? S12Conventions.UpAxis.PosX : S12Conventions.UpAxis.NegX;
            if (ay >= az) return uy >= 0 ? S12Conventions.UpAxis.PosY : S12Conventions.UpAxis.NegY;
            return uz >= 0 ? S12Conventions.UpAxis.PosZ : S12Conventions.UpAxis.NegZ;
        }

        /// <summary>The floor and top (metres along the up axis, signed so that larger is higher) of a box: the extent of the cells along the axis.</summary>
        public static void FloorTop(CellBox b, double g, int upAxis, out double floor, out double top)
        {
            int lo, hi;
            switch (upAxis)
            {
                case S12Conventions.UpAxis.PosX: case S12Conventions.UpAxis.NegX: lo = b.MinX; hi = b.MaxX; break;
                case S12Conventions.UpAxis.PosZ: case S12Conventions.UpAxis.NegZ: lo = b.MinZ; hi = b.MaxZ; break;
                default: lo = b.MinY; hi = b.MaxY; break;
            }
            double a = (lo - 0.5) * g, c = (hi + 0.5) * g;
            bool neg = upAxis == S12Conventions.UpAxis.NegX || upAxis == S12Conventions.UpAxis.NegY || upAxis == S12Conventions.UpAxis.NegZ;
            if (neg) { floor = -c; top = -a; }
            else { floor = a; top = c; }
        }

        /// <summary>A grid-local point's height along the up axis (same sign convention as <see cref="FloorTop"/>).</summary>
        public static double Height(double px, double py, double pz, int upAxis)
        {
            switch (upAxis)
            {
                case S12Conventions.UpAxis.PosX: return px;
                case S12Conventions.UpAxis.NegX: return -px;
                case S12Conventions.UpAxis.PosZ: return pz;
                case S12Conventions.UpAxis.NegZ: return -pz;
                case S12Conventions.UpAxis.NegY: return -py;
                default: return py;
            }
        }
    }
}
