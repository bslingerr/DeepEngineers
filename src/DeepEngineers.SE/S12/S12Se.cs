using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Definitions;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S12 Interiors v2, the SE side (CONTRACT §5.4 "S12 conventions"; docs/S12-BRIEF.md). Game members, each VERIFIED in the
 // decompiled build (se/Sandbox.Game, se/SpaceEngineers.Game):
 // MyGridGasSystem.GetOxygenRoomForCubeGridPosition(ref Vector3I): MyOxygenRoom
 // IMyGridGasSystem.IsProcessingData (explicit)
 // MyGridGasSystem.OnProcessingDataComplete (Action {get; set})
 // MyGridGasSystem.GetSafeOxygenBlock(Vector3D): MyOxygenBlock
 // MyGridGasSystem.IsAirtightBlock(IMySlimBlock, Vector3I, Vector3) (static; normal = toward the neighbour),
 // its use by MyCubeGridGasContext.IsAirtightBetweenPositions (endPos - startPos)
 // MyGridGasSystem.RemoveBlock (the breach: IsAirtight false, OxygenAmount 0, cells merged)
 // MyGridGasSystem.AddBlock (an airtight block nulls its cell: m_cubeRoom = null)
 // MyGridGasSystem.CreateAirtightRoom (a rebuilt room starts at its environment oxygen)
 // MyOxygenRoom.Index / IsAirtight / EnvironmentOxygen / OxygenAmount / BlockCount / Blocks
 // MyOxygenRoom.OxygenLevel(float) = min(1, OxygenAmount / MaxOxygen); MaxOxygen(float) = BlockCount g^3
 // MyCubeGrid.WorldToGridInteger = Round(Transform(p, WorldMatrixNormalizedInv) * GridSizeR)
 // MyCubeGrid.GetFatBlocks: ListReader<MyCubeBlock>
 // MyCubeGrid.GetCubeBlock(Vector3I): MySlimBlock
 // MyCubeGrid.RemoveBlock(MySlimBlock, bool updatePhysics)
 // IMyCubeGrid.AddBlock(MyObjectBuilder_CubeBlock, bool testMerge) (explicit)
 // MySlimBlock.GetObjectBuilder(bool copy)
 // MyAirVent.IsDepressurizing (public get/set) / CanVent
 // MyAirVent.m_isPlayingVentEffect (private bool; CheckForVentEffect from VentToRoom/DrainFromRoom)
 // MyAirVent.GetOxygenBlock (private: the room at its vent dummy "vent_001")
 // MyGasTank's build pass FilledRatio (read at Init)
 // MyCubeBlock.BlockDefinition / Position / CubeGrid
 // MySession.CameraController, GetCameraControllerEnum (FrameWriter.CameraModeOf's use)
 // MyCharacter.OxygenComponent / Physics / MoveIndicator
 // MyCharacterOxygenComponent.EnvironmentOxygenLevel
 // Threads: everything here runs on SE's update thread (the C1 writer's PrepareForDraw postfix calls the air hook, the
 // plugin's tick calls Tick, InteriorRooms' evaluation calls Capture, Link.WriteSeTime writes the C6 block).
 // =================================================================================================================

 /// <summary>
 /// formats air.volume / air.hold / air.camera / air.flood / air.water: the S12 air rule, called by <c>InteriorProbe.Decide</c>
 /// (the S12 hook) for the camera and every character's swim gate. False = the S7 answer stays (S12 off, the air switch off,
 /// S12.M1, S7.M10).
 /// </summary>
    internal static class S12Air
    {
        private sealed class HoldBox
        {
            public readonly S12AirHold Hold = new S12AirHold();
            /// <summary>format air.water's latch per grid (true: under the plane).</summary>
            public readonly Dictionary<ulong, bool> Wet = new Dictionary<ulong, bool>();
            public uint Epoch;
        }

        private static readonly ConditionalWeakTable<RoomFlagHold, HoldBox> Holds = new ConditionalWeakTable<RoomFlagHold, HoldBox>();
        private static readonly S12CellRoom LiveFn = LiveRoom;
        private static readonly S12CellRoom SnapFn = SnapRoom;
        private static readonly S12CellRoom FrozenFn = FrozenRoom;
        private static readonly S12CellSeal SealFn = Seal;
        private static MyGridGasSystem _ctxGas;
        private static MyCubeGrid _ctxGrid;
        private static S12Snapshot _ctxSnap;
        private static S12Floods.Track _ctxTrack;

        public static long Decisions, SnapshotAnswers, KeptAnswers, WaterOuts, SeatIns, CameraFlips, Rows, Disagreements, FrozenAnswers, ThirdOuts, SealQueries;
        private static bool _lastS7In;
        public static bool HasCamera;
        public static Vector3D LastCamera;
        public static double LastDepth;
        public static bool LastIn, LastThird;
        public static int LastRoom;
        public static long LastGrid;
        public static bool LastProcessing, LastSnapshot, LastKept, LastFrozen;
        public static int[] LastCell = new int[3];
        private static ulong _nextRowMs;
        private static bool _rowHas;

        /// <summary>The S12 hook of <c>InteriorProbe.Decide</c>. Never throws (false on any error: the S7 answer stays).</summary>
        public static bool TryDecide(RoomFlagHold s7hold, bool isCamera, Vector3D p, MyCubeGrid[] cands, int n, bool seat, bool pressurization, C1Interior s7, out C1Interior result)
        {
            result = C1Interior.None(pressurization);
            // S7.M10 (the GetRooms flicker) is the S7 rule's own seeded failure: it needs the S7 path
            if (!S12Se.Active || !S12Switch.AirOn || S12SeMutants.CameraBox || S7Mutants.M10 || s7hold == null) return false;
            try
            {
                if (isCamera) { LastCamera = p; HasCamera = true; }
                if (!pressurization) { result = C1Interior.None(false); return true; }
                HoldBox box = Holds.GetOrCreateValue(s7hold);
                uint epoch = Phases.WorldEpoch;
                if (box.Epoch != epoch) { box.Hold.Clear(); box.Wet.Clear(); box.Epoch = epoch; }
                S12CellSeal seal = S12SeMutants.SealMidPlane ? SealFn : null; // S12.M9: round 2's mid-plane
                C1Interior r = C1Interior.None(true);
                bool processing = false, breached = false, won = false;
                double camDepth = double.NaN;
                int camRoom = 0;
                long camGrid = 0;
                bool camProc = false, camSnap = false, camKept = false, camFrozen = false;
                int cx = 0, cy = 0, cz = 0;
                for (int i = 0; i < n; i++)
                {
                    MyCubeGrid g = cands[i];
                    if (g == null) continue;
                    MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                    if (gas == null) continue;
                    ulong gid = (ulong)g.EntityId;
                    bool proc = ((IMyGridGasSystem)gas).IsProcessingData;
                    if (proc) processing = true;
                    Vector3D local = Vector3D.Transform(p, g.PositionComp.WorldMatrixNormalizedInv);
                    double gs = g.GridSize;
                    var d = new S12Depth { DepthM = -gs / 2 };
                    bool keep = false, snapUsed = false, frozenUsed = false;
                    S12Floods.Track ft = null;
                    _ctxGas = gas;
                    _ctxGrid = g;
                    if (!proc) d = S12AirVolume.Depth(local.X, local.Y, local.Z, gs, LiveFn, seal);
                    else
                    {
                        // format air.hold: the snapshot answers only when it is as new as the last completed recompute
                        S12Snapshot snap = S12Rooms.Of(gas);
                        if (snap != null && S12Rooms.Fresh(gas, snap))
                        {
                            _ctxSnap = snap;
                            d = S12AirVolume.Depth(local.X, local.Y, local.Z, gs, SnapFn, seal);
                            snapUsed = true;
                            SnapshotAnswers++;
                        }
                        else { keep = true; KeptAnswers++; }
                    }
                    // format air.flood: a room that is not airtight is water; S12.M8 brings back round 2's air pocket (a rising

                    if (!keep)
                    {
                        ft = S12Floods.RisingBreach(g.EntityId) ?? S12Floods.DoorDry(g.EntityId);   //: a room open through a door only stays air (Subnautica's rule)
                        if (ft != null)
                        {
                            _ctxTrack = ft;
                            S12Depth df = S12AirVolume.Depth(local.X, local.Y, local.Z, gs, FrozenFn, seal);
                            if (df.DepthM > d.DepthM) { d = df; frozenUsed = true; FrozenAnswers++; }
                        }
                    }
                    _ctxGas = null; _ctxGrid = null; _ctxSnap = null; _ctxTrack = null;
                    bool inNow = keep ? box.Hold.Was(gid) : box.Hold.Step(gid, true, d.DepthM, S12AirVolume.Hysteresis(gs));
                    int roomIndex = inNow ? d.Room : 0;
                    float o2 = 0f;
                    if (inNow && !keep)
                    {
                        // format air.water: the latch per grid (water below the plane - WaterEnterMm, air above it + WaterExitMm)
                        bool was;
                        box.Wet.TryGetValue(gid, out was);
                        bool water = frozenUsed ? S12Floods.WaterIn(ft, local, gs, was) : S12Floods.WaterAt(g.EntityId, roomIndex, local, gs, was);
                        box.Wet[gid] = water;
                        if (water) { inNow = false; WaterOuts++; }
                    }
                    else if (!keep && box.Wet.Count > 0) box.Wet.Remove(gid);
                    if (!inNow && seat && !proc)
                    {
                        // review F14 (S7): inside a pressurized cockpit the seat's own room counts (SE's SelectEffect test)
                        MyOxygenBlock ob = gas.GetSafeOxygenBlock(p);
                        if (ob != null && ob.Room != null && ob.Room.IsAirtight) { inNow = true; roomIndex = ob.Room.Index; SeatIns++; }
                    }
                    if (inNow && !proc && !frozenUsed)
                    {
                        Vector3I c = new Vector3I(d.CellX, d.CellY, d.CellZ);
                        MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref c);
                        if (room != null && room.IsAirtight) o2 = room.OxygenLevel(g.GridSize);
                    }
                    else if (!inNow && !proc)
                    {
                        Vector3I c = new Vector3I(S12AirVolume.Cell(local.X, gs), S12AirVolume.Cell(local.Y, gs), S12AirVolume.Cell(local.Z, gs));
                        MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref c);
                        if (room != null && !room.IsAirtight) breached = true; // S7's Breached: the cell lies in a room that is not airtight
                    }
                    if (inNow && !won)
                    {
                        won = true;
                        r.Flags |= S7Conventions.C1InteriorFlags.InRoom;
                        r.RoomGridId = gid;
                        r.RoomIndex = roomIndex >= 0 ? (uint)roomIndex : S7InteriorWire.NoRoom;
                        r.RoomO2 = S7InteriorWire.RoomO2Byte(o2);
                    }
                    if (isCamera && (camGrid == 0 || (inNow && won && r.RoomGridId == gid)))
                    {
                        camGrid = g.EntityId; camDepth = keep ? double.NaN : d.DepthM; camRoom = d.Room; camProc = proc; camSnap = snapUsed; camKept = keep; camFrozen = frozenUsed;
                        cx = d.CellX; cy = d.CellY; cz = d.CellZ;
                    }
                }
                bool third = false;
                if (isCamera && won)
                {
                    // format air.
                    third = ThirdPerson();
                    if (third)
                    {
                        MySession ses = MySession.Static;
                        MyCharacter ch = ses != null ? ses.LocalCharacter : null;
                        bool chrIn = ch != null && !ch.Closed && S7Interior.CharacterInRoom(ch.EntityId);
                        if (!S12AirVolume.CameraInRoom(true, true, chrIn))
                        {
                            r = C1Interior.None(true);
                            won = false;
                            ThirdOuts++;
                        }
                    }
                }
                if (processing) r.Flags |= S7Conventions.C1InteriorFlags.Processing;
                if (!won && breached) r.Flags |= S7Conventions.C1InteriorFlags.Breached;
                Decisions++;
                result = r;
                if (isCamera)
                {
                    LastThird = third;
                    CameraRow(r.InRoom, s7.InRoom, camGrid, camDepth, camRoom, camProc, camSnap, camKept, camFrozen, third, cx, cy, cz, box.Hold.Flips, n);
                }
                return true;
            }
            catch (Exception e)
            {
                _ctxGas = null; _ctxGrid = null; _ctxSnap = null; _ctxTrack = null;
                S12Se.Fail("air", e);
                return false;
            }
        }

        /// <summary>format air.camera's third person: SE's third-person camera (not the spectator, not first person), or the dev override.</summary>
        internal static bool ThirdPerson()
        {
            if (S12Switch.ForceThirdPerson) return true;
            MySession s = MySession.Static;
            if (s == null) return false;
            IMyCameraController cc = s.CameraController;
            if (cc == null || ReferenceEquals(cc, MySpectatorCameraController.Static)) return false;
            return s.GetCameraControllerEnum() == MyCameraControllerEnum.ThirdPersonSpectator;
        }

        private static int LiveRoom(int x, int y, int z)
        {
            var c = new Vector3I(x, y, z);
            MyOxygenRoom room = _ctxGas.GetOxygenRoomForCubeGridPosition(ref c);
            if (room == null || !room.IsAirtight) return 0;
            return room.Index > 0 ? room.Index : int.MaxValue;
        }

        private static int SnapRoom(int x, int y, int z)
        {
            return _ctxSnap != null ? _ctxSnap.RoomAt(x, y, z) : 0;
        }

        private static int FrozenRoom(int x, int y, int z)
        {
            S12Floods.Track t = _ctxTrack;
            return t != null && t.Frozen != null && t.Frozen.Contains(S12Cell.Pack(x, y, z)) ? S12Floods.FrozenKey(t) : 0;
        }

        /// <summary>format air.volume's seal: the cell's own block seals its face toward <paramref name="dir"/> (MyGridGasSystem.IsAirtightBlock).</summary>
        private static bool Seal(int x, int y, int z, int dir)
        {
            MyCubeGrid g = _ctxGrid;
            if (g == null) return false;
            var c = new Vector3I(x, y, z);
            MySlimBlock b = g.GetCubeBlock(c);
            if (b == null) return false;
            SealQueries++;
            var normal = new Vector3(dir == 0 ? 1 : dir == 1 ? -1 : 0, dir == 2 ? 1 : dir == 3 ? -1 : 0, dir == 4 ? 1 : dir == 5 ? -1 : 0);
            return MyGridGasSystem.IsAirtightBlock(b, c, normal);
        }

        private static void CameraRow(bool inRoom, bool s7In, long grid, double depth, int room, bool proc, bool snap, bool kept, bool frozen, bool third,
                                      int cx, int cy, int cz, long flips, int candidates)
        {
            if (s7In != inRoom) Disagreements++;
            if (_rowHas && inRoom != LastIn) CameraFlips++;
            bool changed = !_rowHas || inRoom != LastIn || s7In != _lastS7In;
            _lastS7In = s7In;
            LastIn = inRoom; LastDepth = depth; LastRoom = room; LastGrid = grid; LastProcessing = proc; LastSnapshot = snap; LastKept = kept; LastFrozen = frozen;
            LastCell[0] = cx; LastCell[1] = cy; LastCell[2] = cz;
            ulong now = Win32.TickCount64();
            bool periodic = candidates > 0 && now >= _nextRowMs;
            if (!changed && !periodic) return;
            _rowHas = true;
            _nextRowMs = now + 500;
            Rows++;
            SiteMap map = SiteBinding.Map;
            object camSn = null;
            if (map != null)
            {
                Vec3 q = map.SeToSn(new Vec3(LastCamera.X, LastCamera.Y, LastCamera.Z));
                camSn = new List<object> { Math.Round(q.X, 3), Math.Round(q.Y, 3), Math.Round(q.Z, 3) };
            }
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.air" }, { "why", changed ? "change" : "periodic" },
                { "tick", (long)FrameWriter.LastTick }, { "inRoom", inRoom }, { "grid", grid.ToString(CultureInfo.InvariantCulture) }, { "room", (long)room },
                { "depth", double.IsNaN(depth) ? null : (object)Math.Round(depth, 4) }, { "cell", new List<object> { (long)cx, (long)cy, (long)cz } },
                { "processing", proc }, { "snapshot", snap }, { "kept", kept }, { "frozen", frozen }, { "third", third }, { "flips", flips },
                { "cameraFlips", CameraFlips }, { "candidates", (long)candidates }, { "camSn", camSn }, { "rule", "s12" }, { "s7InRoom", s7In },
                { "disagreeTicks", Disagreements }, { "mutants", S12SeMutants.Active },
            };
            CharacterFields(row);
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        /// <summary>
 /// The local character's swim-gate answer (S7Interior.CharacterInRoom), SE's own environment oxygen at it
 /// and S2b's swim latch (SwimController.IsSwimming).
 /// </summary>
        internal static void CharacterFields(Dictionary<string, object> row)
        {
            MySession s = MySession.Static;
            MyCharacter ch = s != null ? s.LocalCharacter : null;
            if (ch == null || ch.Closed) { row["chrIn"] = null; row["chrEnvO2"] = null; row["chrSwim"] = null; return; }
            row["chrIn"] = S7Interior.CharacterInRoom(ch.EntityId);
            double env = double.NaN;
            try { MyCharacterOxygenComponent oc = ch.OxygenComponent; if (oc != null) env = oc.EnvironmentOxygenLevel; } catch (Exception) { }
            row["chrEnvO2"] = double.IsNaN(env) ? null : (object)Math.Round(env, 4);
            row["chrSwim"] = SwimController.IsSwimming;
        }

        public static void OnWorldUnload()
        {
            HasCamera = false;
            _rowHas = false;
        }
    }

    /// <summary>
 /// The per-grid room snapshots (format air.hold's processing answer; flood geometry): taken by the S12 hook in
 /// <c>InteriorRooms.Build</c> from the very <c>GetRooms</c> list it already read (no second call), keyed by the gas system,
 /// and each gas system's count of completed recomputes (its own handler on <c>OnProcessingDataComplete</c>).
 /// </summary>
    internal static class S12Rooms
    {
        private sealed class Counter { public long Completions; }

        private static readonly ConditionalWeakTable<MyGridGasSystem, S12Snapshot> Snaps = new ConditionalWeakTable<MyGridGasSystem, S12Snapshot>();
        private static readonly ConditionalWeakTable<MyGridGasSystem, Counter> Counters = new ConditionalWeakTable<MyGridGasSystem, Counter>();
        public static long Captures, Truncated, Errors, Reused, Watched;

        private static Counter Watch(MyGridGasSystem gas)
        {
            Counter c;
            if (Counters.TryGetValue(gas, out c)) return c;
            c = new Counter();
            Counters.Add(gas, c);
            Counter cap = c;
            gas.OnProcessingDataComplete += () => Interlocked.Increment(ref cap.Completions); // (InteriorRooms' own handler stays)
            Watched++;
            return c;
        }

        /// <summary>The completed recomputes counted since S12 first saw the gas system.</summary>
        public static long CompletionsOf(MyGridGasSystem gas) { return gas == null ? 0 : Interlocked.Read(ref Watch(gas).Completions); }

        /// <summary>format air.hold: the snapshot was taken after the gas system's last completed recompute.</summary>
        public static bool Fresh(MyGridGasSystem gas, S12Snapshot s) { return s != null && s.Completions == CompletionsOf(gas); }

        private static bool SameRooms(S12Snapshot s, List<IMyOxygenRoom> rooms)
        {
            int n = 0;
            foreach (IMyOxygenRoom ir in rooms)
            {
                var r = ir as MyOxygenRoom;
                if (r == null || !r.IsAirtight || r.Blocks == null || r.Blocks.Count == 0) continue;
                S12SnapRoom sr = s.RoomByIndex(r.Index > 0 ? r.Index : int.MaxValue);
                if (sr == null || sr.Cells != r.Blocks.Count) return false;
                foreach (Vector3I c in r.Blocks) { int k; if (!s.Cells.TryGetValue(S12Cell.Pack(c.X, c.Y, c.Z), out k) || k != sr.Index) return false; break; }
                n++;
            }
            return n == s.Rooms.Count;
        }

        public static S12Snapshot Of(MyGridGasSystem gas)
        {
            S12Snapshot s;
            return gas != null && Snaps.TryGetValue(gas, out s) ? s : null;
        }

        /// <summary>
 /// The S12 hook of <c>InteriorRooms.Build</c>, right after its loop: <paramref name="rooms"/> = its GetRooms list (each
 /// room's Blocks filled), <paramref name="perRoom"/>[k] = the boxes of the k-th airtight room with cells. Never throws.
 /// </summary>
        public static void Capture(MyGridGasSystem gas, List<IMyOxygenRoom> rooms, List<List<CellBox>> perRoom, int used, bool roomData)
        {
            if (gas == null || !roomData || !S12Se.Active) return;
            try
            {
                long completions = CompletionsOf(gas);
                // an unchanged room set (the 10 s re-check, a recompute that changed nothing) keeps its cell map: only the
 // oxygen and the completion count are refreshed (no per-cell pass, no allocation)
                S12Snapshot old0 = Of(gas);
                if (old0 != null && SameRooms(old0, rooms))
                {
                    foreach (IMyOxygenRoom ir in rooms)
                    {
                        var r = ir as MyOxygenRoom;
                        if (r == null || !r.IsAirtight || r.Blocks == null || r.Blocks.Count == 0) continue;
                        S12SnapRoom sr = old0.RoomByIndex(r.Index > 0 ? r.Index : int.MaxValue);
                        if (sr != null) { sr.OxygenAmount = r.OxygenAmount; sr.BlockCount = r.BlockCount; }
                    }
                    old0.TakenMs = Win32.TickCount64();
                    old0.Completions = completions;
                    Reused++;
                    return;
                }
                var s = new S12Snapshot { TakenMs = Win32.TickCount64(), Completions = completions };
                int k = 0, total = 0;
                foreach (IMyOxygenRoom ir in rooms)
                {
                    var r = ir as MyOxygenRoom;
                    if (r == null || !r.IsAirtight || r.Blocks == null || r.Blocks.Count == 0) continue; // the same filter as Build's loop
                    List<CellBox> boxes = k < used && k < perRoom.Count ? perRoom[k] : null;
                    k++;
                    total += r.Blocks.Count;
                    if (total > S12Conventions.Air.SnapshotMaxCells) { s.Truncated = true; break; }
                    var sr = new S12SnapRoom { Index = r.Index > 0 ? r.Index : int.MaxValue, Cells = r.Blocks.Count, OxygenAmount = r.OxygenAmount, BlockCount = r.BlockCount, RepCell = long.MaxValue };
                    int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
                    foreach (Vector3I c in r.Blocks)
                    {
                        long key = S12Cell.Pack(c.X, c.Y, c.Z);
                        s.Cells[key] = sr.Index;
                        if (key < sr.RepCell) sr.RepCell = key;
                        if (c.X < x0) x0 = c.X; if (c.Y < y0) y0 = c.Y; if (c.Z < z0) z0 = c.Z;
                        if (c.X > x1) x1 = c.X; if (c.Y > y1) y1 = c.Y; if (c.Z > z1) z1 = c.Z;
                    }
                    sr.Aabb = new CellBox(x0, y0, z0, x1, y1, z1);
                    if (boxes != null) sr.Boxes.AddRange(boxes);
                    else sr.Boxes.Add(sr.Aabb);
                    s.Rooms.Add(sr);
                }
                if (s.Truncated) { Truncated++; s.Cells.Clear(); s.Rooms.Clear(); }
                s.Serial = old0 != null ? old0.Serial + 1 : 1;
                Snaps.Remove(gas);
                if (!s.Truncated) Snaps.Add(gas, s);
                Captures++;
            }
            catch (Exception e)
            {
                Errors++;
                S12Se.Fail("rooms", e);
            }
        }
    }

    /// <summary>formats flood.breach, flood.drain, flood.fill, air.flood: the flood tracks of the airtight rooms near the camera.</summary>
    internal static class S12Floods
    {
        public const int FrozenMaxCells = 65536;

        internal sealed class Track
        {
            public long GridId;
            /// <summary> (Subnautica's door rule): the grid's removed-block serial when this track last saw its room airtight.</summary>
            public long AirtightSerial;
            public MyCubeGrid Grid;
            public long RepCell;
            public int RepX, RepY, RepZ;
            public uint Id;
            public readonly S12FloodTrack T = new S12FloodTrack(true);
            public int Cells;
            public readonly List<CellBox> Boxes = new List<CellBox>();
            /// <summary>The room's cells of its last airtight snapshot (format air.flood); null for a room over FrozenMaxCells.</summary>
            public HashSet<long> Frozen;
            public long FrozenSerial = -1;
            public CellBox Union;
            public int Up = S12Conventions.UpAxis.PosY;
            public int LiveRoom;
            public double Oxygen, EnvOxygen, DepthM, FillMs, Distance;
            public bool UnderSea, Seen, Known = true, NeedsRepick;
            public int LastState = -1;
            public uint LastSeq;
            public double LastRowLevel = -1;
            public ulong LastRowTick;
            public Vector3D PointLocal;
            public bool Processing;
            public long Repicks;
            /// <summary>Refreshes this track spent unseen while its room's fate was pending (a recompute, a breach not yet decided).</summary>
            public int Pending;
            /// <summary>format flood.plane: the room's water cells, the cells of the sent boxes outside them (0), the boxes left out.</summary>
            public int WaterCells;
            public long Outside;
            public int BoxesDropped;
            /// <summary>format flood.breach's one plane per cell: the water cells of its geometry, the geometry's age, the cells
 /// another active track with newer geometry draws (left out; S12.M12: kept, so counted as overlap).</summary>
            public HashSet<long> WaterSet;
            public long GeomStamp;
            public long Overlap;
        }

        private static readonly Dictionary<long, List<Track>> ByGrid = new Dictionary<long, List<Track>>();
        private static readonly List<Track> _all = new List<Track>();
        private static uint _nextId = 1;
        public static long Starts, Ended, Rows, Refreshes, Repicks, Unknowns;
        public const int MaxWatched = 32;
        public const int RefreshTicks = 30;
        /// <summary>Refreshes an unseen track is kept while its room's fate is pending (8 x 30 ticks: about 4 s).</summary>
        public const int PendingMaxRefreshes = 8;
        public static long KeptPending;

        public static int Count { get { return _all.Count; } }

        /// <summary>format air.flood's room key of a frozen cell set (above every SE room index in practice).</summary>
        public static int FrozenKey(Track t) { return 0x40000000 + (int)(t.Id & 0x0FFFFFFF); }

        /// <summary>format air.flood: the grid's breach flood that rises or is full and keeps its frozen cells, or null.</summary>
        public static Track RisingBreach(long gridId)
        {
            if (!S12SeMutants.AirPocket) return null; // format air.flood: an open room is water; S12.M8 only
            List<Track> l;
            if (!ByGrid.TryGetValue(gridId, out l)) return null;
            foreach (Track t in l)
                if (t.Frozen != null && t.T.Source == S12Conventions.FloodSource.Breach &&
                    (t.T.State == S12Conventions.FloodState.Rising || t.T.State == S12Conventions.FloodState.Full)) return t;
            return null;
        }

        /// <summary>format air.water's latch for a track's plane (grid-local metres; <paramref name="wasWet"/> the point's previous answer).</summary>
        public static bool WaterIn(Track t, Vector3D local, double gs, bool wasWet)
        {
            if (t == null || !(t.T.Level > 0)) return false;
            double floor, top;
            S12Boxes.FloorTop(t.Union, gs, t.Up, out floor, out top);
            double h = S12Boxes.Height(local.X, local.Y, local.Z, t.Up);
            return S12AirVolume.Wet(wasWet, h - floor, t.T.Level, top - floor);
        }

        /// <summary>The active flood track of an airtight room (its plane), or null.</summary>
        public static Track OfRoom(long gridId, int roomIndex)
        {
            if (roomIndex <= 0) return null;
            List<Track> l;
            if (!ByGrid.TryGetValue(gridId, out l)) return null;
            foreach (Track t in l)
                if (t.T.State != S12Conventions.FloodState.None && t.LiveRoom == roomIndex && t.T.Level > 0) return t;
            return null;
        }

        /// <summary>format air.water: the latch of a point in an airtight room with an active flood (grid-local metres).</summary>
        public static bool WaterAt(long gridId, int roomIndex, Vector3D local, double gs, bool wasWet)
        {
            Track t = OfRoom(gridId, roomIndex);
            return t != null && WaterIn(t, local, gs, wasWet);
        }

        public static long SurfaceQueries, SurfaceHits;
        public static bool LastSurface;
        public static double LastSurfaceSnY = double.NaN, LastEyeSnY = double.NaN;
        public static long LastSurfaceGrid;

        /// <summary>
 /// format flood.surface (S2b's hook in SwimController.Tick): the eye (SE world) lies in an airtight room with an
 /// active flood; <paramref name="surfaceSnY"/> = the plane's SN y at the eye. False with S12 off, the flood switch
 /// off, or S12.M11. Never throws.
 /// </summary>
        public static bool SurfaceAt(Vector3D eye, double eyeSnY, out double surfaceSnY)
        {
            surfaceSnY = double.NaN;
            LastEyeSnY = eyeSnY;
            LastSurface = false;
            try
            {
                if (!S12Se.Active || !S12Switch.FloodOn || S12SeMutants.NoSurface || _all.Count == 0) return false;
                SiteMap map = SiteBinding.Map;
                if (map == null) return false;
                SurfaceQueries++;
                foreach (Track t in _all)
                {
                    if (t.T.State == S12Conventions.FloodState.None || t.LiveRoom <= 0 || !(t.T.Level > 0)) continue;
                    MyCubeGrid g = t.Grid;
                    if (g == null || g.Closed) continue;
                    MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                    if (gas == null || ((IMyGridGasSystem)gas).IsProcessingData) continue;
                    Vector3D local = Vector3D.Transform(eye, g.PositionComp.WorldMatrixNormalizedInv);
                    double gs = g.GridSize;
                    var c = new Vector3I(S12AirVolume.Cell(local.X, gs), S12AirVolume.Cell(local.Y, gs), S12AirVolume.Cell(local.Z, gs));
                    MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref c);
                    if (room == null || !room.IsAirtight || (room.Index > 0 ? room.Index : int.MaxValue) != t.LiveRoom) continue;
                    double floor, top;
                    S12Boxes.FloorTop(t.Union, gs, t.Up, out floor, out top);
                    double h = floor + Math.Max(0, Math.Min(1, t.T.Level)) * (top - floor);
                    double a = (t.Up == S12Conventions.UpAxis.NegX || t.Up == S12Conventions.UpAxis.NegY || t.Up == S12Conventions.UpAxis.NegZ) ? -h : h;
                    Vector3D onPlane = local;
                    if (t.Up == S12Conventions.UpAxis.PosX || t.Up == S12Conventions.UpAxis.NegX) onPlane.X = a;
                    else if (t.Up == S12Conventions.UpAxis.PosZ || t.Up == S12Conventions.UpAxis.NegZ) onPlane.Z = a;
                    else onPlane.Y = a;
                    Vector3D w = Vector3D.Transform(onPlane, g.PositionComp.WorldMatrixRef);
                    surfaceSnY = map.SeToSn(new Vec3(w.X, w.Y, w.Z)).Y;
                    SurfaceHits++;
                    LastSurface = true;
                    LastSurfaceSnY = surfaceSnY;
                    LastSurfaceGrid = g.EntityId;
                    return true;
                }
            }
            catch (Exception e) { S12Se.Fail("surface", e); }
            return false;
        }

        public static void Tick(ulong nowMs, ulong tick, Vector3D cam, List<S12Row> rows)
        {
            // the tracks step first: a breach seen this tick starts before the refresh could drop a room the new snapshot lost
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                Track t = _all[i];
                MyCubeGrid g = t.Grid;
                if (g == null || g.Closed || g.MarkedForClose) { Drop(t, "grid gone"); continue; }
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                var o = new S12RoomObs { UnderSea = t.UnderSea, Known = true };
                if (gas == null) { o.Known = false; }
                else
                {
                    o.Processing = ((IMyGridGasSystem)gas).IsProcessingData;
                    if (!o.Processing)
                    {
                        var c = new Vector3I(t.RepX, t.RepY, t.RepZ);
                        MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref c);
                        // format flood.breach: no room at the watched cell (a block placed there) decides nothing; pick it again
                        o.Known = room != null;
                        if (!o.Known) { if (!t.NeedsRepick) Unknowns++; t.NeedsRepick = true; }
                        o.Airtight = room != null && room.IsAirtight;

                        // last seen airtight = a door opened; the room stays dry (DEEPENGINEERS_S12_DOORS=lockout brings the flood back)
                        long serial = RemovedSerial(t.GridId);
                        if (o.Airtight) t.AirtightSerial = serial;
                        o.DoorOnly = DoorsSubnautica && !o.Airtight && t.AirtightSerial == serial;
                        o.Oxygen = o.Airtight ? room.OxygenLevel(g.GridSize) : 0.0;
                        o.EnvOxygen = room != null ? room.EnvironmentOxygen : 0.0;
                        t.LiveRoom = o.Airtight ? (room.Index > 0 ? room.Index : int.MaxValue) : 0;
                        o.VentOut = o.Airtight && S12Vents.Depressurizing(t.GridId, t.LiveRoom);
                    }
                }
                t.Processing = o.Processing;
                t.Known = o.Known;
                t.Oxygen = o.Oxygen;        // SE's raw room oxygen (logged as is: S12.FLOOD.DRAIN judges against it)
                t.EnvOxygen = o.EnvOxygen;
                int before = t.T.State;
                if (before == S12Conventions.FloodState.None) t.T.SetFill(S12SeMutants.FillInstant ? 1.0 : t.FillMs);
                t.T.SetDrain(S12SeMutants.DrainInstant ? 0.0 : S12Conventions.Flood.DrainMs); // S12.M3: no drain time
                if (S12SeMutants.DoorFlip) t.T.SetDebounce(0, 0); // S12.M12: round 2's door flip
                else t.T.SetDebounce(S12Conventions.Flood.RepairDebounceMs, S12Conventions.Flood.ReBreachQuietMs);
                t.T.Step(nowMs, o);
                if (before == S12Conventions.FloodState.None && t.T.State != S12Conventions.FloodState.None) { Starts++; Started(t); }
                else if (t.T.StartSeq != t.LastSeq && t.LastSeq != 0) Starts++;
                RowIfDue(t, tick);
                if (t.T.Ended) { Ended++; Drop(t, "dry"); continue; }
            }
            if (tick % RefreshTicks == 0) Refresh(cam);
            // the C6 rows: the active floods, nearest first (the refresh sorted the list)
            foreach (Track t in _all)
                if (t.T.State != S12Conventions.FloodState.None && rows.Count < S12Conventions.C6.MaxRows) rows.Add(RowOf(t));
        }

        private static void Started(Track t)
        {
            DeLog.Info("DE.S12 flood start grid " + t.GridId + " track " + t.Id + " (" + S12Conventions.FloodSource.Name(t.T.Source) + "): " + t.Cells + " cells, depth " +
                       t.DepthM.ToString("0.0", CultureInfo.InvariantCulture) + " m, fill " + (t.FillMs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s, frozen " +
                       (t.Frozen != null ? t.Frozen.Count.ToString(CultureInfo.InvariantCulture) : "none"));
        }

        private static S12Row RowOf(Track t)
        {
            var r = new S12Row
            {
                GridId = t.GridId, TrackId = t.Id, Kind = (byte)S12Conventions.RowKind.Flood, State = (byte)t.T.State, Source = (byte)t.T.Source,
                Level = (float)t.T.Level, RatePerS = (float)t.T.RatePerS, StartSeq = S12SeMutants.DoubleStart ? t.T.StartSeq * 2 : t.T.StartSeq,
                GridSize = (byte)(t.Grid.GridSizeEnum == MyCubeSize.Large ? 2 : 1), UpAxis = (byte)t.Up,
                Px = (float)t.PointLocal.X, Py = (float)t.PointLocal.Y, Pz = (float)t.PointLocal.Z,
            };
            int n = Math.Min(t.Boxes.Count, S12Conventions.C6.MaxBoxes);
            r.BoxCount = (byte)n;
            for (int i = 0; i < n; i++) r.SetBox(i, t.Boxes[i]);
            return r;
        }

        private static void RowIfDue(Track t, ulong tick)
        {
            bool stateChanged = t.T.State != t.LastState || t.T.StartSeq != t.LastSeq || t.T.Ended;
            bool levelDue = t.T.State != S12Conventions.FloodState.None && Math.Abs(t.T.Level - t.LastRowLevel) > 0.001 && tick >= t.LastRowTick + (ulong)S12Conventions.Flood.RowTicks;
            if (!stateChanged && !levelDue) return;
            t.LastState = t.T.State;
            t.LastSeq = t.T.StartSeq;
            t.LastRowLevel = t.T.Level;
            t.LastRowTick = tick;
            if (t.T.State == S12Conventions.FloodState.None && !t.T.Ended && t.T.StartSeq == 0) return; // a watched room that never flooded
            if (!stateChanged && !S10Se.TakeRow(S10Se.RowFlood)) return; // S10 hook (round 3 E): level rows under the per-second budget (state changes always); telemetry only
            Rows++;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.flood" }, { "tick", (long)tick },
                { "grid", t.GridId.ToString(CultureInfo.InvariantCulture) }, { "track", (long)t.Id }, { "state", S12Conventions.FloodState.Name(t.T.State) },
                { "source", S12Conventions.FloodSource.Name(t.T.Source) }, { "level", Math.Round(t.T.Level, 5) }, { "rate", Math.Round(t.T.RatePerS, 5) },
                { "startSeq", (long)(S12SeMutants.DoubleStart ? t.T.StartSeq * 2 : t.T.StartSeq) }, { "fillMs", Math.Round(t.FillMs, 1) },
                { "drainMs", Math.Round(t.T.DrainMs, 1) }, { "cells", (long)t.Cells }, { "gridSize", Math.Round((double)t.Grid.GridSize, 3) }, { "holes", 1L },
                { "depth", Math.Round(t.DepthM, 3) }, { "oxygen", Math.Round(t.Oxygen, 5) }, { "envOxygen", Math.Round(t.EnvOxygen, 5) }, { "known", t.Known },
                { "processing", t.Processing }, { "room", (long)t.LiveRoom }, { "ended", t.T.Ended }, { "boxes", (long)t.Boxes.Count }, { "up", (long)t.Up },
                { "frozen", t.Frozen != null ? (long)t.Frozen.Count : 0L }, { "repicks", t.Repicks }, { "waterCells", (long)t.WaterCells },
                { "outside", t.Outside }, { "boxesDropped", (long)t.BoxesDropped }, { "overlap", t.Overlap }, { "mutants", S12SeMutants.Active },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        /// <summary>The watch list: every airtight room of the snapshots of mirrored grids within RangeM, nearest first, at most MaxWatched.</summary>
        private static void Refresh(Vector3D cam)
        {
            Refreshes++;
            foreach (Track t in _all) t.Seen = false;
            SiteMap map = SiteBinding.Map;
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || g.MarkedForClose || mg.Projection) continue;
                if (g.PositionComp.WorldAABB.Distance(cam) > S12Conventions.Flood.RangeM) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                S12Snapshot snap = S12Rooms.Of(gas);
                if (snap == null) continue;
                List<Track> list;
                if (!ByGrid.TryGetValue(g.EntityId, out list)) { list = new List<Track>(); ByGrid[g.EntityId] = list; }
                // format flood.breach: a track whose watched cell lost its room picks it again from this snapshot's room over its cells
                foreach (Track x in list) if (x.NeedsRepick) Repick(x, snap);
                foreach (S12SnapRoom sr in snap.Rooms)
                {
                    Track t = null;
                    foreach (Track x in list) { int k; if (snap.Cells.TryGetValue(x.RepCell, out k) && k == sr.Index) { t = x; break; } }
                    if (t == null)
                    {
                        if (_all.Count >= MaxWatched) continue;
                        int rx, ry, rz;
                        S12Cell.Unpack(sr.RepCell, out rx, out ry, out rz);
                        // a snapshot older than a breach must not start a flood the camera never saw coming: watch only rooms airtight now
                        if (gas == null || ((IMyGridGasSystem)gas).IsProcessingData) continue;
                        var rc = new Vector3I(rx, ry, rz);
                        MyOxygenRoom live = gas.GetOxygenRoomForCubeGridPosition(ref rc);
                        if (live == null || !live.IsAirtight) continue;
                        t = new Track { GridId = g.EntityId, Grid = g, RepCell = sr.RepCell, Id = _nextId++, RepX = rx, RepY = ry, RepZ = rz };
                        Subscribe(g);
                        t.AirtightSerial = RemovedSerial(g.EntityId);
                        list.Add(t);
                        _all.Add(t);
                    }
                    t.Seen = true;
                    // a draining room's boxes follow its airtight room (an airlock's inner door closed: the chamber is not its own)
                    bool draining = t.T.State == S12Conventions.FloodState.Draining && !S12SeMutants.DoorFlip;
                    if (t.T.State == S12Conventions.FloodState.None || draining)
                    {
                        // keep the airtight geometry fresh while nothing floods (a breach keeps the last airtight shape);
 // format flood.plane: the room's water boxes, never their bounding box (S12.M10 brings it back)
                        t.Cells = sr.Cells;
                        if (sr.Water == null)
                        {
                            MyCubeGrid gb = g;
                            sr.Water = S12Water.Cells(snap, sr, (x, y, z) => gb.GetCubeBlock(new Vector3I(x, y, z)) != null);
                            sr.WaterBoxes = S12Water.Boxes(sr.Water);
                        }
                        t.Boxes.Clear();
                        if (S12SeMutants.BoxPlane) t.Boxes.Add(sr.Aabb);
                        else t.Boxes.AddRange(S12Boxes.ForRow(sr.WaterBoxes));
                        t.WaterCells = sr.Water.Count;
                        t.Outside = S12Water.Outside(t.Boxes, sr.Water);
                        t.BoxesDropped = S12SeMutants.BoxPlane ? 0 : Math.Max(0, sr.WaterBoxes.Count - t.Boxes.Count);
                        t.Union = S12Boxes.Union(t.Boxes);
                        if (t.WaterSet != sr.Water) { t.WaterSet = sr.Water; t.GeomStamp = ++_geomStamp; }
                        if (t.T.State == S12Conventions.FloodState.None && t.FrozenSerial != snap.Serial) Freeze(t, snap, sr);
                    }
                    Geometry(t, g, map, cam);
                }
                OnePlanePerCell(list);
            }
            // watched rooms that left the snapshot while nothing flooded (rebuilt with another shape, or out of range) are dropped
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                Track t = _all[i];
                if (t.Seen || t.T.State != S12Conventions.FloodState.None) { t.Pending = 0; if (!t.Seen && t.Grid != null) { PruneToOpen(t); Geometry(t, t.Grid, map, cam); } continue; }
                // format flood.breach: RemoveBlock marks the room non-airtight at once and starts a recompute,
 // so a snapshot taken meanwhile has lost the room before a completed gas system let the track decide:
 // keep it while SE recomputes or its watched cell has no airtight room yet, for at most PendingMaxRefreshes
                if (PendingFate(t) && ++t.Pending <= PendingMaxRefreshes) { KeptPending++; continue; }
                Drop(t, "unwatched");
            }
            _all.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        /// <summary>The track's room is being recomputed, or its watched cell lies in no room or a non-airtight one (a breach the next
 /// completed tick decides): not a room rebuilt with another shape.</summary>
        private static bool PendingFate(Track t)
        {
            MyCubeGrid g = t.Grid;
            if (g == null || g.Closed || g.MarkedForClose) return false;
            MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
            if (gas == null) return false;
            if (((IMyGridGasSystem)gas).IsProcessingData) return true;
            var c = new Vector3I(t.RepX, t.RepY, t.RepZ);
            MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref c);
            return room == null || !room.IsAirtight;
        }

        private static long _geomStamp;
        public static long OverlapCells;

        /// <summary>
 /// format flood.breach's one plane per cell: of the grid's active tracks over the same cells only the one with the newer

 /// S12.M12 leaves the overlap in place and counts it.
 /// </summary>
        private static void OnePlanePerCell(List<Track> list)
        {
            var active = new List<Track>();
            foreach (Track t in list) if (t.T.State != S12Conventions.FloodState.None && t.WaterSet != null) active.Add(t);
            foreach (Track t in active) t.Overlap = 0;
            if (active.Count < 2) return;
            active.Sort((a, b) => b.GeomStamp.CompareTo(a.GeomStamp));
            var claimed = new HashSet<long>();
            foreach (Track t in active)
            {
                HashSet<long> own = null;
                long shared = 0;
                foreach (long c in t.WaterSet) if (claimed.Contains(c)) shared++;
                if (shared > 0)
                {
                    OverlapCells += shared;
                    if (S12SeMutants.DoorFlip) t.Overlap = shared;
                    else
                    {
                        own = new HashSet<long>(t.WaterSet);
                        own.ExceptWith(claimed);
                        t.Boxes.Clear();
                        t.Boxes.AddRange(S12Boxes.ForRow(S12Water.Boxes(own)));
                        if (t.Boxes.Count > 0) t.Union = S12Boxes.Union(t.Boxes);
                        t.Outside = S12Water.Outside(t.Boxes, t.WaterSet);
                    }
                }
                claimed.UnionWith(own ?? t.WaterSet);
            }
        }

        /// <summary>format air.flood: the room's cells as of this airtight snapshot.</summary>
        private static void Freeze(Track t, S12Snapshot snap, S12SnapRoom sr)
        {
            t.FrozenSerial = snap.Serial;
            if (sr.Cells > FrozenMaxCells) { t.Frozen = null; return; }
            var set = new HashSet<long>();
            foreach (KeyValuePair<long, int> kv in snap.Cells) if (kv.Value == sr.Index) set.Add(kv.Key);
            t.Frozen = set;
        }

        /// <summary>The watched cell again: a frozen cell that this snapshot puts in an airtight room (the room with the most of them).</summary>
        private static void Repick(Track t, S12Snapshot snap)
        {
            if (t.Frozen == null || t.Frozen.Count == 0) return;
            var votes = new Dictionary<int, int>();
            var first = new Dictionary<int, long>();
            foreach (long c in t.Frozen)
            {
                int k;
                if (!snap.Cells.TryGetValue(c, out k)) continue;
                int v;
                votes.TryGetValue(k, out v);
                votes[k] = v + 1;
                if (!first.ContainsKey(k)) first[k] = c;
            }
            int best = 0, bestVotes = 0;
            foreach (KeyValuePair<int, int> kv in votes) if (kv.Value > bestVotes) { best = kv.Key; bestVotes = kv.Value; }
            if (bestVotes == 0) return;
            t.RepCell = first[best];
            S12Cell.Unpack(t.RepCell, out t.RepX, out t.RepY, out t.RepZ);
            t.NeedsRepick = false;
            t.Repicks++;
            Repicks++;
            DeLog.Info("DE.S12 flood track " + t.Id + " watches cell " + t.RepX + "," + t.RepY + "," + t.RepZ + " again (its cell lost its room: a block placed there)");
        }

        private static void Geometry(Track t, MyCubeGrid g, SiteMap map, Vector3D cam)
        {
            double gs = g.GridSize;
            CellBox u = t.Union;
            var cLocal = new Vector3D((u.MinX + u.MaxX) * 0.5 * gs, (u.MinY + u.MaxY) * 0.5 * gs, (u.MinZ + u.MaxZ) * 0.5 * gs);
            MatrixD wm = g.PositionComp.WorldMatrixRef;
            Vector3D cWorld = Vector3D.Transform(cLocal, wm);
            t.Distance = Vector3D.Distance(cWorld, cam);
            if (map != null)
            {
                Vec3 sn = map.SeToSn(new Vec3(cWorld.X, cWorld.Y, cWorld.Z));
                t.DepthM = -sn.Y;
                t.UnderSea = sn.Y < 0;
                Vec3 up0 = map.SnToSe(sn), up1 = map.SnToSe(new Vec3(sn.X, sn.Y + 1.0, sn.Z));
                var upSe = new Vector3D(up1.X - up0.X, up1.Y - up0.Y, up1.Z - up0.Z);
                Vector3D upLocal = Vector3D.TransformNormal(upSe, g.PositionComp.WorldMatrixNormalizedInv);
                t.Up = S12Boxes.UpAxisOf(upLocal.X, upLocal.Y, upLocal.Z);
            }
            t.PointLocal = cLocal; // the row point: the room's centre (Subnautica puts the splash on the room floor under it)
            if (t.T.State == S12Conventions.FloodState.None) t.FillMs = S12Fill.FillMs(t.Cells, gs, 1, t.DepthM);
        }

        private static void Drop(Track t, string why)
        {
            _all.Remove(t);
            List<Track> l;
            if (ByGrid.TryGetValue(t.GridId, out l)) { l.Remove(t); if (l.Count == 0) ByGrid.Remove(t.GridId); }
            if (t.LastSeq != 0 || t.T.StartSeq != 0) DeLog.Info("DE.S12 flood track " + t.Id + " on grid " + t.GridId + " dropped (" + why + ")");
        }

        public static void Clear() { _all.Clear(); ByGrid.Clear(); _removedSerial.Clear(); _subscribed.Clear(); }

        // A room that is not airtight floods only when a block of its grid was removed or destroyed since the room was last airtight
 // (MyCubeGrid.OnBlockRemoved fires for both: a destroyed block is removed); a door opening removes nothing, so the room stays
 // dry and its last airtight cells keep answering air (DoorDry). DEEPENGINEERS_S12_DOORS=lockout restores the flood on any loss.
        private static readonly Dictionary<long, long> _removedSerial = new Dictionary<long, long>();
        private static readonly HashSet<long> _subscribed = new HashSet<long>();
        private static int _doorsMode = -1;
        public static long BlockRemovals;

        /// <summary>Subnautica's rule (default) or the lockout (DEEPENGINEERS_S12_DOORS=lockout).</summary>
        public static bool DoorsSubnautica
        {
            get
            {
                if (_doorsMode < 0)
                {
                    string v = null;
                    try { v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S12_DOORS"); } catch (Exception) { }

                    // the lockout is the default again (the chamber fills and drains), the leak into the house fixed by PruneToOpen
                    _doorsMode = v != null && v.Trim().ToLowerInvariant() == "subnautica" ? 1 : 0;
                    DeLog.Info("DE.S12 doors: " + (_doorsMode == 1 ? "Subnautica's rule (a door never floods a room)" : "lockout (a room open through a door floods, its plane only in the open part; DEEPENGINEERS_S12_DOORS=subnautica for the dry rule)"));
                }
                return _doorsMode == 1;
            }
        }

        private static long RemovedSerial(long gridId)
        {
            long s;
            return _removedSerial.TryGetValue(gridId, out s) ? s : 0;
        }

        private static void Subscribe(MyCubeGrid g)
        {
            if (g == null || !_subscribed.Add(g.EntityId)) return;
            long id = g.EntityId;
            try
            {
                g.OnBlockRemoved += _ =>
                {
                    long s;
                    _removedSerial[id] = (_removedSerial.TryGetValue(id, out s) ? s : 0) + 1;
                    BlockRemovals++;
                };
            }
            catch (Exception e) { S12Se.Fail("doors", e); }
        }

        public static long PrunedCells, Prunes;

        /// <summary>

 /// a breach keeps the last airtight shape, which may span a room that is airtight now or still (an airlock chamber tracked
 /// together with the house behind its inner door, the inner door closed since). The cells of that shape that lie in an
 /// airtight room of the current snapshot are not water: the plane stays in the open part. A completed gas system only.
 /// </summary>
        private static void PruneToOpen(Track t)
        {
            try
            {
                if (t == null || t.Grid == null || t.WaterSet == null || t.WaterSet.Count == 0 || S12SeMutants.DoorFlip) return;
                if (t.T.Source != S12Conventions.FloodSource.Breach || (t.T.State != S12Conventions.FloodState.Rising && t.T.State != S12Conventions.FloodState.Full)) return;
                MyGridGasSystem gas = t.Grid.GridSystems != null ? t.Grid.GridSystems.GasSystem : null;
                if (gas == null || ((IMyGridGasSystem)gas).IsProcessingData) return;
                S12Snapshot snap = S12Rooms.Of(gas);
                if (snap == null || !S12Rooms.Fresh(gas, snap)) return;
                HashSet<long> pruned = null;
                foreach (long k in t.WaterSet)
                {
                    int x, y, z;
                    S12Cell.Unpack(k, out x, out y, out z);
                    if (snap.RoomAt(x, y, z) == 0) continue;            // not in an airtight room: still water
                    if (pruned == null) pruned = new HashSet<long>(t.WaterSet);
                    pruned.Remove(k);
                }
                if (pruned == null || pruned.Count == 0 || pruned.Count == t.WaterSet.Count) return;
                List<CellBox> wb = S12Water.Boxes(pruned);
                t.Boxes.Clear();
                t.Boxes.AddRange(S12Boxes.ForRow(wb));
                t.WaterCells = pruned.Count;
                t.Outside = S12Water.Outside(t.Boxes, pruned);
                t.Union = S12Boxes.Union(t.Boxes);
                PrunedCells += t.WaterSet.Count - pruned.Count;
                Prunes++;
                t.WaterSet = pruned;
                t.GeomStamp = ++_geomStamp;
                DeLog.Info("DE.S12 flood track " + t.Id + " on grid " + t.GridId + " pruned to its open part: " + pruned.Count + " water cell(s), " + (t.WaterSet.Count) + " kept of the frozen shape (the rest lies in an airtight room now)");
            }
            catch (Exception e) { S12Se.Fail("prune", e); }
        }

        /// <summary>The grid's room open through a door only, dry, with its last airtight cells (Subnautica's rule), or null.</summary>
        public static Track DoorDry(long gridId)
        {
            List<Track> l;
            if (!ByGrid.TryGetValue(gridId, out l)) return null;
            foreach (Track t in l)
                if (t.Frozen != null && t.T.DoorOpen) return t;
            return null;
        }

        public static List<object> Status()
        {
            var l = new List<object>();
            foreach (Track t in _all)
                l.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "grid", t.GridId.ToString(CultureInfo.InvariantCulture) }, { "track", (long)t.Id }, { "state", S12Conventions.FloodState.Name(t.T.State) },
                    { "source", S12Conventions.FloodSource.Name(t.T.Source) }, { "level", Math.Round(t.T.Level, 4) }, { "startSeq", (long)t.T.StartSeq },
                    { "cells", (long)t.Cells }, { "rep", new List<object> { (long)t.RepX, (long)t.RepY, (long)t.RepZ } }, { "room", (long)t.LiveRoom },
                    { "oxygen", Math.Round(t.Oxygen, 4) }, { "envOxygen", Math.Round(t.EnvOxygen, 4) }, { "known", t.Known }, { "underSea", t.UnderSea },
                    { "depth", Math.Round(t.DepthM, 2) }, { "fillMs", Math.Round(t.FillMs, 1) }, { "distance", Math.Round(t.Distance, 1) }, { "up", (long)t.Up },
                    { "frozen", t.Frozen != null ? (long)t.Frozen.Count : 0L },
                });
            return l;
        }
    }

    /// <summary>format vent.event: the Air Vents of the mirrored grids near the camera that SE vents through (or de.s12 drives).</summary>
    internal static class S12Vents
    {
        internal sealed class VentState
        {
            public MyAirVent Vent;
            public long GridId, Id;
            public readonly S12VentEdge Edge = new S12VentEdge();
            public int Room;
            public bool Seen, Dev, DevOut, DummyRoom;
            public ulong DevUntilMs;
            public double DevRate;
            public double Distance;
            public bool LastOn;
        }

        private static readonly Dictionary<long, VentState> Vents = new Dictionary<long, VentState>();
        private static readonly List<VentState> _order = new List<VentState>();
        private static FieldInfo _playing;
        private static MethodInfo _oxygenBlock;
        private static bool _reflected, _missing, _blockReflected, _blockMissing;
        public static long Starts, Rows, DevTicks;

        public static int Count { get { return Vents.Count; } }

        private static bool Playing(MyAirVent v)
        {
            if (_missing || v == null) return false;
            if (!_reflected)
            {
                _reflected = true;
                _playing = typeof(MyAirVent).GetField("m_isPlayingVentEffect", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_playing == null || _playing.FieldType != typeof(bool))
                {
                    _missing = true;
                    _playing = null;
                    DeLog.Info("DE.S12 MISSING MyAirVent.m_isPlayingVentEffect: SE's own venting is not seen (de.s12 vent still drives the effect)");
                    return false;
                }
            }
            try { return (bool)_playing.GetValue(v); }
            catch (Exception) { _missing = true; return false; }
        }

        /// <summary>The vent's room as SE's own vent finds it (MyAirVent.GetOxygenBlock: the room at its vent dummy); null = none or unknown.</summary>
        private static MyOxygenRoom RoomOf(MyAirVent v, MyGridGasSystem gas, out bool dummy)
        {
            dummy = false;
            if (!_blockMissing)
            {
                if (!_blockReflected)
                {
                    _blockReflected = true;
                    _oxygenBlock = typeof(MyAirVent).GetMethod("GetOxygenBlock", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (_oxygenBlock == null || _oxygenBlock.ReturnType != typeof(MyOxygenBlock))
                    {
                        _blockMissing = true;
                        _oxygenBlock = null;
                        DeLog.Info("DE.S12 MISSING MyAirVent.GetOxygenBlock(): the vent's room is taken at its own cell");
                    }
                }
                if (_oxygenBlock != null)
                {
                    try
                    {
                        var ob = _oxygenBlock.Invoke(v, null) as MyOxygenBlock;
                        dummy = true;
                        return ob != null ? ob.Room : null;
                    }
                    catch (Exception) { _blockMissing = true; }
                }
            }
            Vector3I c = v.Position;
            return gas.GetOxygenRoomForCubeGridPosition(ref c);
        }

        /// <summary>An Air Vent of the room depressurizes it now (format flood.drain's oxygen source).</summary>
        public static bool Depressurizing(long gridId, int room)
        {
            if (room <= 0) return false;
            foreach (VentState s in _order)
                if (s.GridId == gridId && s.Room == room && s.Edge.On && s.Edge.State == S12Conventions.VentState.Depressurizing) return true;
            return false;
        }

        public static void Tick(ulong nowMs, ulong tick, Vector3D cam, List<S12Row> rows)
        {
            if (tick % S12Floods.RefreshTicks == 0) Refresh(cam);
            foreach (VentState s in _order)
            {
                MyAirVent v = s.Vent;
                if (v == null || v.Closed || v.MarkedForClose) continue;
                MyCubeGrid g = v.CubeGrid;
                if (g == null || g.Closed) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                if (gas != null && !((IMyGridGasSystem)gas).IsProcessingData)
                {
                    bool dummy;
                    MyOxygenRoom room = RoomOf(v, gas, out dummy);
                    s.DummyRoom = dummy;
                    s.Room = room != null && room.IsAirtight ? (room.Index > 0 ? room.Index : int.MaxValue) : 0;
                    if (s.Dev && nowMs < s.DevUntilMs && room != null && room.IsAirtight) DevDrive(s, room, g.GridSize);
                }
                if (s.Dev && nowMs >= s.DevUntilMs) { s.Dev = false; DeLog.Info("DE.S12 vent " + s.Id + " dev drive ended"); }
                bool venting = s.Dev || Playing(v);
                bool depress = s.Dev ? s.DevOut : v.IsDepressurizing;
                bool start = s.Edge.Step(venting, depress);
                if (start) Starts++;
                if (start || venting != s.LastOn) Row(s, tick, start);
                s.LastOn = venting;
                if (venting && !S12SeMutants.VentOff && rows.Count < S12Conventions.C6.MaxRows) rows.Add(RowOf(s, g));
            }
        }

        /// <summary>de.s12 vent (dev): SE's room oxygen raised or lowered at rate x MaxOxygen per second (VentToRoom's effect on the room).</summary>
        private static void DevDrive(VentState s, MyOxygenRoom room, float gridSize)
        {
            DevTicks++;
            float max = room.MaxOxygen(gridSize);
            float step = (float)(s.DevRate * max / 60.0);
            float v = room.OxygenAmount + (s.DevOut ? -step : step);
            room.OxygenAmount = v < 0 ? 0 : v > max ? max : v;
        }

        private static S12Row RowOf(VentState s, MyCubeGrid g)
        {
            MatrixD inv = g.PositionComp.WorldMatrixNormalizedInv;
            Vector3D p = Vector3D.Transform(s.Vent.PositionComp.GetPosition(), inv);
            Vector3D f = Vector3D.TransformNormal(s.Vent.WorldMatrix.Forward, inv);
            return new S12Row
            {
                GridId = s.GridId, TrackId = (uint)(s.Id & 0xFFFFFFFF), Kind = (byte)S12Conventions.RowKind.Vent, State = (byte)s.Edge.State,
                StartSeq = s.Edge.StartSeq, GridSize = (byte)(g.GridSizeEnum == MyCubeSize.Large ? 2 : 1),
                Flags = (ushort)(s.Dev ? 1 : 0), Px = (float)p.X, Py = (float)p.Y, Pz = (float)p.Z, Dx = (float)f.X, Dy = (float)f.Y, Dz = (float)f.Z,
            };
        }

        private static void Row(VentState s, ulong tick, bool start)
        {
            if (S12SeMutants.VentOff) return;
            Rows++;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.vent" }, { "tick", (long)tick },
                { "grid", s.GridId.ToString(CultureInfo.InvariantCulture) }, { "vent", s.Id.ToString(CultureInfo.InvariantCulture) }, { "on", s.Edge.On },
                { "start", start }, { "state", S12Conventions.VentState.Name(s.Edge.State) }, { "startSeq", (long)s.Edge.StartSeq }, { "dev", s.Dev },
                { "room", (long)s.Room }, { "dummyRoom", s.DummyRoom }, { "seEffect", Playing(s.Vent) }, { "depressurizing", s.Vent.IsDepressurizing },
                { "canVent", s.Vent.CanVent }, { "distance", Math.Round(s.Distance, 1) },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        private static void Refresh(Vector3D cam)
        {
            foreach (VentState s in Vents.Values) s.Seen = false;
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || g.MarkedForClose || mg.Projection) continue;
                if (g.PositionComp.WorldAABB.Distance(cam) > S12Conventions.Flood.RangeM) continue;
                foreach (MyCubeBlock b in g.GetFatBlocks())
                {
                    var v = b as MyAirVent;
                    if (v == null) continue;
                    VentState s;
                    if (!Vents.TryGetValue(v.EntityId, out s)) { s = new VentState { Vent = v, GridId = g.EntityId, Id = v.EntityId }; Vents[v.EntityId] = s; }
                    s.Seen = true;
                    s.Distance = Vector3D.Distance(v.PositionComp.GetPosition(), cam);
                }
            }
            var gone = new List<long>();
            foreach (KeyValuePair<long, VentState> kv in Vents) if (!kv.Value.Seen && !kv.Value.Dev) gone.Add(kv.Key);
            foreach (long id in gone) Vents.Remove(id);
            _order.Clear();
            _order.AddRange(Vents.Values);
            _order.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        private static VentState Find(long ventOrGrid)
        {
            VentState s;
            if (Vents.TryGetValue(ventOrGrid, out s)) return s;
            foreach (VentState x in _order) if (x.GridId == ventOrGrid) return x;
            MyEntity e = MyEntities.GetEntityById(ventOrGrid);
            var g = e as MyCubeGrid;
            var v0 = e as MyAirVent;
            if (v0 != null) g = v0.CubeGrid;
            if (g == null) return null;
            foreach (MyCubeBlock b in g.GetFatBlocks())
            {
                var v = b as MyAirVent;
                if (v == null || (v0 != null && !ReferenceEquals(v, v0))) continue;
                s = new VentState { Vent = v, GridId = g.EntityId, Id = v.EntityId };
                Vents[v.EntityId] = s;
                _order.Add(s);
                return s;
            }
            return null;
        }

        /// <summary>de.s12 vent: drive a vent (by its id, or the nearest vent of a grid) for <paramref name="seconds"/>. Null = done, else the refusal.</summary>
        public static string Drive(long ventOrGrid, string mode, double seconds, double rate, out long ventId)
        {
            ventId = 0;
            VentState s = Find(ventOrGrid);
            if (s == null) return "no Air Vent " + ventOrGrid + " (a vent id, or a grid id with a vent)";
            ventId = s.Id;
            string m = (mode ?? "").ToLowerInvariant();
            if (m == "off") { s.Dev = false; return null; }
            if (m != "pressurize" && m != "depressurize") return "mode: pressurize | depressurize | off";
            s.Dev = true;
            s.DevOut = m == "depressurize";
            s.DevRate = rate > 0 ? Math.Min(rate, 1.0) : 0.1;
            s.DevUntilMs = Win32.TickCount64() + (ulong)(Math.Max(0.1, Math.Min(600.0, seconds)) * 1000.0);
            DeLog.Info("DE.S12 vent " + s.Id + " dev drive " + m + " for " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s at " + s.DevRate.ToString("0.###", CultureInfo.InvariantCulture) + " of the room per s");
            return null;
        }

        /// <summary>de.s12 ventmode: SE's own vent set to depressurize or pressurize (MyAirVent.IsDepressurizing, its terminal switch).</summary>
        public static string Mode(long ventOrGrid, bool depressurize, out long ventId)
        {
            ventId = 0;
            VentState s = Find(ventOrGrid);
            if (s == null || s.Vent == null) return "no Air Vent " + ventOrGrid;
            ventId = s.Id;
            s.Dev = false;
            s.Vent.IsDepressurizing = depressurize;
            DeLog.Info("DE.S12 vent " + s.Id + " set to " + (depressurize ? "depressurize" : "pressurize") + " (SE's own venting)");
            return null;
        }

        public static void Clear() { Vents.Clear(); _order.Clear(); }

        public static List<object> Status()
        {
            var l = new List<object>();
            foreach (VentState s in _order)
                l.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "vent", s.Id.ToString(CultureInfo.InvariantCulture) }, { "grid", s.GridId.ToString(CultureInfo.InvariantCulture) }, { "on", s.Edge.On },
                    { "state", S12Conventions.VentState.Name(s.Edge.State) }, { "startSeq", (long)s.Edge.StartSeq }, { "room", (long)s.Room }, { "dummyRoom", s.DummyRoom },
                    { "dev", s.Dev }, { "seEffect", Playing(s.Vent) }, { "depressurizing", s.Vent != null && s.Vent.IsDepressurizing },
                    { "canVent", s.Vent != null && s.Vent.CanVent }, { "distance", Math.Round(s.Distance, 1) },
                });
            return l;
        }
    }

    /// <summary>The S12 host: activation, the per-tick flood and vent pass, the C6 block, the site row, the dev builds.</summary>
    internal static class S12Se
    {
        private static bool _enabled, _envRead, _active, _siteLogged, _failed;
        private static uint _epoch = uint.MaxValue;
        private static readonly List<S12Row> _rows = new List<S12Row>(S12Conventions.C6.MaxRows);
        public static long Ticks, Errors;
        public static string LastError;
        public static bool Active { get { return _active && !_failed; } }

        /// <summary>The plugin's per-tick hook (update thread). Never throws.</summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            try
            {
                if (!_envRead) { _envRead = true; _enabled = S12Switch.EnabledFromEnvironment(); DeLog.Info("DE.S12 " + (_enabled ? "on" : "off (" + S12Switch.EnvVar + ")") + " " + S12Conventions.Header + " 0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture)); }
                uint epoch = Phases.WorldEpoch;
                if (epoch != _epoch) { _epoch = epoch; OnWorldChange(); }
                _active = _enabled && inWorld && !_failed;
                if (!_active) { S12SeWire.Deactivate(); return; }
                if (!_siteLogged) SiteRow();
                Ticks++;
                ulong tick = FrameWriter.LastTick;
                _rows.Clear();
                if (S12Air.HasCamera && S7Interior.Pressurization)
                {
                    Vector3D cam = S12Air.LastCamera;
                    if (S12Switch.FloodOn) S12Floods.Tick(nowMs, tick, cam, _rows);
                    if (S12Switch.VentOn) S12Vents.Tick(nowMs, tick, cam, _rows);
                }
                CharRow(nowMs, tick);
                byte flags = (byte)(S12Conventions.C6Flags.Active | (S12Switch.AirOn && !S12SeMutants.CameraBox ? S12Conventions.C6Flags.AirRule : 0));
                S12SeWire.Publish(epoch, tick, flags, _rows);
            }
            catch (Exception e) { Fail("tick", e); }
        }

        private static ulong _charActiveMs, _charNextTick;
        public static long CharRows;

        /// <summary>
 /// The s12.char row (every 6 ticks while a flood row is active and 5 s after the last; always after de.s12 charrows
 /// on): the local character's speed (MyCharacter.Physics.LinearVelocity), S2b's swim latch and mode, the room surface
 /// (format flood.surface) and the eye's height above it, its swim-gate answer and SE's environment oxygen.
 /// </summary>
        private static void CharRow(ulong nowMs, ulong tick)
        {
            bool active = false;
            foreach (S12Row r in _rows) if (r.Kind == (byte)S12Conventions.RowKind.Flood) { active = true; break; }
            if (active) _charActiveMs = nowMs;
            bool due = S12Switch.CharRows || (_charActiveMs != 0 && nowMs < _charActiveMs + 5000);
            if (!due || tick < _charNextTick) return;
            _charNextTick = tick + 6;
            MySession ses = MySession.Static;
            MyCharacter ch = ses != null ? ses.LocalCharacter : null;
            if (ch == null || ch.Closed) return;
            double speed = double.NaN;
            try { if (ch.Physics != null) speed = ch.Physics.LinearVelocity.Length(); } catch (Exception) { }
            CharRows++;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.char" }, { "tick", (long)tick },
                { "speed", double.IsNaN(speed) ? null : (object)Math.Round(speed, 3) }, { "swimMode", SwimController.ModeName }, { "script", SwimController.ScriptRunning },
                { "surface", S12Floods.LastSurface ? "room" : "none" },
                { "eyeAbove", S12Floods.LastSurface && !double.IsNaN(S12Floods.LastEyeSnY) ? (object)Math.Round(S12Floods.LastEyeSnY - S12Floods.LastSurfaceSnY, 3) : null },
                { "eyeSnY", double.IsNaN(S12Floods.LastEyeSnY) ? null : (object)Math.Round(S12Floods.LastEyeSnY, 3) }, { "floodActive", active },
                { "mutants", S12SeMutants.Active },
            };
            S12Air.CharacterFields(row);
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        private static void OnWorldChange()
        {
            S12Floods.Clear();
            S12Vents.Clear();
            S12Air.OnWorldUnload();
            _breaches.Clear();
            _siteLogged = false;
        }

        private static void SiteRow()
        {
            _siteLogged = true;
            var row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s12.site" }, { "header", S12Conventions.Header },
                { "hash", "0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) }, { "pressurization", S7Interior.Pressurization },
                { "air", S12Switch.AirOn }, { "flood", S12Switch.FloodOn }, { "vent", S12Switch.VentOn }, { "mutants", S12SeMutants.Active }, { "worldEpoch", (long)_epoch },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(row));
        }

        internal static void Fail(string what, Exception e)
        {
            Errors++;
            LastError = what + ": " + e.GetType().Name + ": " + e.Message;
            if (Errors <= 3) DeLog.Info("DE.S12 error (" + LastError + ")" + (Errors == 3 ? " (further errors counted, not logged)" : "") + " " + e.StackTrace);
            if (Errors >= 50 && !_failed) { _failed = true; DeLog.Info("DE.S12 OFF after 50 errors: the S7 rule decides the air, no flood or vent rows"); }
        }

        public static Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", _enabled }, { "active", Active }, { "failed", _failed }, { "conventions", S12Conventions.Header + " 0x" + S12Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) },
                { "air", S12Switch.AirOn }, { "flood", S12Switch.FloodOn }, { "vent", S12Switch.VentOn }, { "third", S12Switch.ForceThirdPerson }, { "charrows", S12Switch.CharRows }, { "mutants", S12SeMutants.Active },
                { "camera", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "inRoom", S12Air.LastIn }, { "grid", S12Air.LastGrid.ToString(CultureInfo.InvariantCulture) }, { "room", (long)S12Air.LastRoom },
                        { "depth", double.IsNaN(S12Air.LastDepth) ? null : (object)Math.Round(S12Air.LastDepth, 4) }, { "processing", S12Air.LastProcessing },
                        { "snapshot", S12Air.LastSnapshot }, { "kept", S12Air.LastKept }, { "frozen", S12Air.LastFrozen }, { "thirdPerson", S12Air.LastThird },
                        { "cell", new List<object> { (long)S12Air.LastCell[0], (long)S12Air.LastCell[1], (long)S12Air.LastCell[2] } }, { "flips", S12Air.CameraFlips },
                    } },
                { "decisions", S12Air.Decisions }, { "snapshotAnswers", S12Air.SnapshotAnswers }, { "keptAnswers", S12Air.KeptAnswers }, { "frozenAnswers", S12Air.FrozenAnswers },
                { "waterOuts", S12Air.WaterOuts }, { "thirdOuts", S12Air.ThirdOuts }, { "sealQueries", S12Air.SealQueries }, { "seatIns", S12Air.SeatIns },
                { "s7DisagreeTicks", S12Air.Disagreements }, { "snapshots", S12Rooms.Captures }, { "snapshotsReused", S12Rooms.Reused }, { "snapshotsTruncated", S12Rooms.Truncated },
                { "gasWatched", S12Rooms.Watched }, { "floods", S12Floods.Status() }, { "floodStarts", S12Floods.Starts }, { "floodKeptPending", S12Floods.KeptPending }, { "surfaceHits", S12Floods.SurfaceHits }, { "charRows", CharRows }, { "floodRows", S12Floods.Rows },
                { "floodRepicks", S12Floods.Repicks }, { "vents", S12Vents.Status() }, { "ventStarts", S12Vents.Starts }, { "ventRows", S12Vents.Rows },
                { "c6", S12SeWire.IsActive ? "rows " + S12SeWire.Count + ", writes " + S12SeWire.Writes : "absent" },
                { "ticks", Ticks }, { "errors", Errors }, { "lastError", LastError },
            };
        }

        // ================================================================================================= dev commands

        private sealed class BreachRecord
        {
            public long GridId;
            public Vector3I Cell;
            public MyObjectBuilder_CubeBlock Builder;
        }

        private static readonly List<BreachRecord> _breaches = new List<BreachRecord>();

        /// <summary>de.s12 breach: removes the block at a grid cell. Null = done, else the refusal.</summary>
        public static string Breach(long gridId, int x, int y, int z)
        {
            MyCubeGrid g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null || g.Closed) return "no grid " + gridId;
            var cell = new Vector3I(x, y, z);
            MySlimBlock slim = g.GetCubeBlock(cell);
            if (slim == null) return "no block at " + cell;
            if (slim.FatBlock != null) return "the block at " + cell + " is a functional block (" + slim.BlockDefinition.Id + "): breach an armour block";
            MyObjectBuilder_CubeBlock ob = slim.GetObjectBuilder(true);
            _breaches.Add(new BreachRecord { GridId = gridId, Cell = cell, Builder = ob });
            g.RemoveBlock(slim, true);
            DeLog.Info("DE.S12 breach: removed " + ob.SubtypeName + " at " + cell + " of grid " + gridId);
            return null;
        }

        /// <summary>de.s12 repair: puts back every block this session's breaches removed from the grid. Null = done.</summary>
        public static string Repair(long gridId, out int count)
        {
            count = 0;
            MyCubeGrid g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null || g.Closed) return "no grid " + gridId;
            for (int i = _breaches.Count - 1; i >= 0; i--)
            {
                BreachRecord b = _breaches[i];
                if (b.GridId != gridId) continue;
                if (g.GetCubeBlock(b.Cell) == null) { ((IMyCubeGrid)g).AddBlock(b.Builder, false); count++; }
                _breaches.RemoveAt(i);
            }
            DeLog.Info("DE.S12 repair: " + count + " block(s) put back on grid " + gridId);
            return count > 0 ? null : "nothing to repair on grid " + gridId;
        }

        private static MyCubeBlockDefinition Def(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            MyDefinitionId d;
            if (id.IndexOf('/') < 0 || !MyDefinitionId.TryParse(id, out d)) d = new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), id);
            MyCubeBlockDefinition def;
            return MyDefinitionManager.Static.TryGetCubeBlockDefinition(d, out def) ? def : null;
        }

        private static MyObjectBuilder_CubeBlock Builder(string defId, int x, int y, int z, string forward, string up, out string why)
        {
            why = null;
            MyCubeBlockDefinition def = Def(defId);
            if (def == null) { why = "definition missing: " + defId; return null; }
            var b = MyObjectBuilderSerializerKeen.CreateNewObject(def.Id) as MyObjectBuilder_CubeBlock;
            if (b == null) { why = def.Id + " has no MyObjectBuilder_CubeBlock builder"; return null; }
            b.Min = new SerializableVector3I(x, y, z);
            b.EntityId = MyEntityIdentifier.AllocateId();
            b.IntegrityPercent = 1f;
            b.BuildPercent = 1f;
            Base6Directions.Direction fwd = Base6Directions.Direction.Forward, upd = Base6Directions.Direction.Up;
            if ((forward != null && !Enum.TryParse(forward, out fwd)) || (up != null && !Enum.TryParse(up, out upd))) { why = "bad orientation " + forward + "/" + up; return null; }
            b.BlockOrientation = new SerializableBlockOrientation(fwd, upd);
            return b;
        }

        /// <summary>de.s12 swap: the block at a cell replaced by another definition and orientation (the test house's slope). Null = done.</summary>
        public static string Swap(long gridId, int x, int y, int z, string defId, string forward, string up, out long blockId)
        {
            blockId = 0;
            MyCubeGrid g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null || g.Closed) return "no grid " + gridId;
            string why;
            MyObjectBuilder_CubeBlock ob = Builder(defId, x, y, z, forward, up, out why);
            if (ob == null) return why;
            var cell = new Vector3I(x, y, z);
            MySlimBlock slim = g.GetCubeBlock(cell);
            if (slim != null)
            {
                if (slim.FatBlock != null) return "the block at " + cell + " is a functional block";
                g.RemoveBlock(slim, true);
            }
            ((IMyCubeGrid)g).AddBlock(ob, false);
            MySlimBlock added = g.GetCubeBlock(cell);
            if (added != null && added.FatBlock != null) blockId = added.FatBlock.EntityId;
            DeLog.Info("DE.S12 swap: " + defId + " (" + forward + "/" + up + ") at " + cell + " of grid " + gridId + (blockId != 0 ? " (block " + blockId + ")" : ""));
            return null;
        }

        /// <summary>
 /// de.s12 rig: a static test grid for SE's own venting at an SN point (block (0, 0, 0)): a light-armour 5^3 shell, an Air Vent
 /// in its west wall (cell 0, 2, 2: front into the room, its back outward) with a large Oxygen Tank half full lying behind it
 /// (its port toward the vent's back), a charged battery in the floor. The ids in the reply (decimal strings).
 /// </summary>
        public static Dictionary<string, object> Rig(double x, double y, double z, out string why)
        {
            why = null;
            SiteMap site = SiteBinding.Map;
            if (site == null) { why = "no site map"; return null; }
            var plan = new List<MyObjectBuilder_CubeBlock>();
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                    for (int k = 0; k < 5; k++)
                    {
                        bool shell = i == 0 || j == 0 || k == 0 || i == 4 || j == 4 || k == 4;
                        if (!shell) continue;
                        if (i == 0 && j == 2 && k == 2) continue;   // the vent
                        if (i == 2 && j == 0 && k == 2) continue;   // the battery
                        MyObjectBuilder_CubeBlock b = Builder("MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", i, j, -k, null, null, out why);
                        if (b == null) return null;
                        plan.Add(b);
                    }
            MyObjectBuilder_CubeBlock vent = Builder("MyObjectBuilder_AirVent/", 0, 2, -2, "Right", "Up", out why);
            MyObjectBuilder_CubeBlock tank = Builder("MyObjectBuilder_OxygenTank/", -2, 2, -2, "Forward", "Left", out why);
            MyObjectBuilder_CubeBlock bat = Builder("MyObjectBuilder_BatteryBlock/LargeBlockBatteryBlock", 2, 0, -2, null, null, out why);
            if (vent == null || tank == null || bat == null) return null;
            try { FieldInfo fr = tank.GetType().GetField("FilledRatio"); if (fr != null) fr.SetValue(tank, 0.5f); } catch (Exception) { }
            plan.Add(vent); plan.Add(tank); plan.Add(bat);
            SeMatrix gm = S3Placement.GridMatrix(site, x, y, z, 0.0);
            var gridOb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            gridOb.EntityId = MyEntityIdentifier.AllocateId();
            gridOb.GridSizeEnum = MyCubeSize.Large;
            gridOb.IsStatic = true;
            gridOb.CreatePhysics = true;
            gridOb.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
            gridOb.DisplayName = "DE.S12 vent rig";
            gridOb.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(gm.Translation.X, gm.Translation.Y, gm.Translation.Z),
                new Vector3((float)gm.Forward.X, (float)gm.Forward.Y, (float)gm.Forward.Z), new Vector3((float)gm.Up.X, (float)gm.Up.Y, (float)gm.Up.Z));
            foreach (MyObjectBuilder_CubeBlock b in plan) gridOb.CubeBlocks.Add(b);
            var grid = MyEntities.CreateFromObjectBuilderAndAdd(gridOb, false) as MyCubeGrid;
            if (grid == null || grid.Closed) { why = "MyEntities.CreateFromObjectBuilderAndAdd returned no grid (see the SE log)"; return null; }
            grid.Save = false;
            GridMirrorProducer.Register(grid, "de.s12 rig", null);
            var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "grid", grid.EntityId.ToString(CultureInfo.InvariantCulture) } };
            foreach (MyCubeBlock fb in grid.GetFatBlocks())
            {
                if (fb is MyAirVent) d["vent"] = fb.EntityId.ToString(CultureInfo.InvariantCulture);
                else if (fb is MyGasTank) { d["tank"] = fb.EntityId.ToString(CultureInfo.InvariantCulture); d["tankFill"] = Math.Round(((MyGasTank)fb).FilledRatio, 3); }
                else if (fb is MyBatteryBlock)
                {
                    var bb = (MyBatteryBlock)fb;
                    try { if (bb.CurrentStoredPower < 0.5f * bb.MaxStoredPower) bb.CurrentStoredPower = bb.MaxStoredPower; } catch (Exception) { }
                    d["battery"] = fb.EntityId.ToString(CultureInfo.InvariantCulture);
                }
            }
            DeLog.Info("DE.S12 vent rig grid " + grid.EntityId + " at SN (" + x.ToString("0.#", CultureInfo.InvariantCulture) + ", " + y.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                       z.ToString("0.#", CultureInfo.InvariantCulture) + "): vent " + (d.ContainsKey("vent") ? d["vent"] : "MISSING") + ", tank " + (d.ContainsKey("tank") ? d["tank"] : "MISSING"));
            return d;
        }

        /// <summary>de.s12 remove: closes a grid S12 built (the rig).</summary>
        public static bool Remove(long gridId)
        {
            MyCubeGrid g = MyEntities.GetEntityById(gridId) as MyCubeGrid;
            if (g == null || g.Closed || g.DisplayName != "DE.S12 vent rig") return false;
            g.Close();
            return true;
        }
    }
}
