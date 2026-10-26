using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 interiors, the SE probe (CONTRACT §5.4 "S7 conventions": "C1 interior extension", <c>format interior.flag</c>,
 /// "Swim gate"; ROADMAP S7 work items "SE plugin: Interiors"). Every SE tick on the update thread (the C1 writer in the
 /// <c>PrepareForDraw</c> postfix calls <see cref="ProbeCamera"/> before composing C1 and <see cref="FillWire"/> after):
 /// <list type="bullet">
 /// <item>World settings: only when the world has <c>EnableOxygen</c> ∧ <c>EnableOxygenPressurization</c>
 /// otherwise every answer is "no room" and
 /// <c>DE.Interior OFF pressurization disabled</c> is logged once per world. The settings are logged once per world
 /// either way (fact B: read, never written).</item>
 /// <item>Candidates: the mirrored grids (S5 set and de.s3 registrations, subgrids included) whose world AABB contains
 /// the point, nearest centre first. Per candidate: <c>cell = WorldToGridInteger(point)</c>; while
 /// <c>((IMyGridGasSystem)gas).IsProcessingData</c> the grid
 /// keeps its last answer (the game rebuilds <c>m_cubeRoom</c> from a fresh cube during the recompute,
 /// <c>StartGenerateAirtightData</c>); else <c>GetOxygenRoomForCubeGridPosition(ref cell)</c> (<c>:1487</c>) and, when
 /// null (inside a block, e.g. a cockpit), <c>GetSafeOxygenBlock(point).Room</c>
 /// <c>IsAirtight</c>, <c>Index</c>, <c>OxygenLevel(GridSize)</c> (<c>MyOxygenRoom.cs</c>).
 /// Review F14: the <c>GetSafeOxygenBlock</c> fallback is used only while the probed character (the
 /// local character for the camera, the character itself for its swim gate) sits in a cockpit whose
 /// <c>BlockDefinition.IsPressurized</c> is true
 /// otherwise a null room is no room (an exterior cockpit on a submarine's hull
 /// no longer reads as inside).
 /// The hold and the winner are Wire's <see cref="RoomFlagHold"/>. Never <c>GetRooms</c> per tick, except under
 /// S7.M10 (<see cref="S7Mutants.M10"/>: the flag from <c>GetRooms</c>, which answers false while processing: the
 /// flicker S7.N1 must catch).</item>
 /// <item>The camera's answer goes to C1 flags bit 3 (<see cref="ProbeCamera"/>'s result through the composer), the C1
 /// interior extension (<see cref="FillWire"/>) and C5 bit 4 (<see cref="CameraInRoom"/>). Each character id has its
 /// own hold (<see cref="SwimGate"/>, called by the S2b swim controller at the character's eye; v3: keyed by id);
 /// <c>CharacterInRoom</c> in C1 is the frame's own character.</item>
 /// <item>Rows <c>s7.interior</c> on every change and at 2 Hz while a candidate exists; <c>de.interior</c>
 /// (<see cref="InteriorCommands"/>).</item>
 /// <item>Review F1: a candidate grid whose camera cell has no room is checked (at most once a second per grid) for
 /// gas blocks: SE builds airtight rooms only for grids with an Air Vent (<c>MyGridGasSystem.ShouldPressurize</c>
 /// <c>:463</c>, <c>m_gasBlocks</c> <c>:83</c>, filled with every <c>IMyGasBlock</c> whose <c>CanPressurizeRoom</c>
 /// is true, i.e. <c>MyAirVent</c>); a grid with blocks and none logs once <c>DE.Interior: no airtight room possible on
 /// &lt;grid&gt;: it has no Air Vent (MyGridGasSystem pressurizes only with a gas block)</c>. The private list is read
 /// through one cached <c>FieldInfo</c>; a missing field logs once and turns only this diagnostic off.</item>
 /// </list>
 /// A failure disables the probe (logged once; the answer is then "no room"): C1 is never broken by it. Budget ≤ 0.05 ms
 /// per tick (a handful of candidates; no allocation per tick).
 /// </summary>
    internal static class S7Interior
    {
        public const int MaxCandidates = 16;
        /// <summary>A character answer older than this many ticks is not used for C1 (the probe then runs at the fallback eye).</summary>
        public const ulong CharacterFreshTicks = 2;
        public const double FallbackEyeAboveFeetM = 1.7;
        public const ulong RowIntervalMs = 500;

        private sealed class CharacterProbe
        {
            public readonly RoomFlagHold Hold = new RoomFlagHold();
            public C1Interior Last;
            public ulong Tick;
            public long Probes;
        }

        private static readonly RoomFlagHold CameraHold = new RoomFlagHold();
        private static readonly Dictionary<long, CharacterProbe> Characters = new Dictionary<long, CharacterProbe>();
        private static readonly RoomProbe[] _probes = new RoomProbe[MaxCandidates];
        private static readonly MyCubeGrid[] _cands = new MyCubeGrid[MaxCandidates];
        private static readonly double[] _candD = new double[MaxCandidates];
        private static readonly HashSet<ulong> _alive = new HashSet<ulong>();
        private static readonly List<IMyOxygenRoom> _m10Rooms = new List<IMyOxygenRoom>();

        // review F1: the Air Vent diagnostic
        private sealed class VentCheck
        {
            public ulong NextMs;
            public int GasBlocks = -1;
            public bool Logged;
        }
        private static readonly Dictionary<long, VentCheck> _vents = new Dictionary<long, VentCheck>();
        private static FieldInfo _gasBlocksField;
        private static bool _gasBlocksReflected, _gasBlocksMissing;
        public static long NoAirVentLogged;
        public const ulong VentCheckMs = 1000;

        private static C1Interior _camera = C1Interior.None(false);
        private static ulong _cameraTick;
        private static int _cameraCandidates;
        private static bool _disabled, _audioHooked;
        private static string _disabledWhy;

        // world settings, read once per world (and re-read every tick: two field reads)
        private static uint _settingsEpoch = uint.MaxValue;
        private static bool _oxygen, _pressurization, _offLogged;

        // rows
        private static C1Interior _rowLast;
        private static bool _rowLastCharacter, _rowHas;
        private static ulong _nextRowMs;

        public static long Probes, CharacterProbes, Holds, NoGasSystem, M10Reads, Rows, Errors;
        public static double LastUs, MaxUs;

        /// <summary>The camera's answer of the last C1 tick.</summary>
        public static C1Interior Camera { get { return _camera; } }
        public static ulong CameraTick { get { return _cameraTick; } }
        /// <summary>C1 flags bit 3 / C5 bit 4 of the last tick (the audio rule's input: <see cref="AudioPolish.CameraInRoomSource"/>).</summary>
        public static bool CameraInRoom { get { return !_disabled && _camera.InRoom; } }
        /// <summary>The world has <c>EnableOxygen</c> ∧ <c>EnableOxygenPressurization</c>.</summary>
        public static bool Pressurization { get { return _oxygen && _pressurization; } }
        public static bool Disabled { get { return _disabled; } }

        // ============================================================================================ per tick

 /// <summary>
 /// The C1 writer's hook (before the composer): the camera's answer for this tick (C1 flags bit 3), and the local
 /// character's when the swim controller did not probe it this tick (its fallback eye). Never throws.
 /// </summary>
        public static bool ProbeCamera(Vector3D camera, ulong tick, MyCharacter localCharacter)
        {
            if (_disabled) return false;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!_audioHooked)
                {
                    // the audio polish (A1/A3) reads the camera-in-room answer from here (AudioPolish.CameraInRoomSource)
                    _audioHooked = true;
                    AudioPolish.CameraInRoomSource = () => CameraInRoom;
                }
                ReadSettings();
                _cameraCandidates = Candidates(camera);
                // review F14: the camera's fallback inside a block only when the local character sits in a pressurized cockpit
                _camera = Decide(CameraHold, camera, _cameraCandidates, SeatedInPressurizedCockpit(localCharacter));
                _cameraTick = tick;
                Probes++;
                RetainAlive();
                if (localCharacter != null && !localCharacter.Closed)
                {
                    CharacterProbe cp;
                    if (!Characters.TryGetValue(localCharacter.EntityId, out cp) || tick > cp.Tick + CharacterFreshTicks)
                    {
                        MatrixD wm = localCharacter.WorldMatrix;
                        ProbeCharacterCore(localCharacter, wm.Translation + wm.Up * FallbackEyeAboveFeetM, tick);
                    }
                }
                Row(tick, localCharacter != null ? localCharacter.EntityId : 0);
            }
            catch (Exception e)
            {
                Disable("camera probe", e);
                return false;
            }
            double us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
            LastUs = us;
            if (us > MaxUs) MaxUs = us;
            return _camera.InRoom;
        }

        /// <summary>
 /// The S2b swim gate (CONTRACT §5.4 "Swim gate"): the character's eye cell is in an airtight room of a mirrored grid
 /// (its own hold, keyed by the character's entity id). The swim controller turns its latch off (exit reason
 /// <c>room</c>) and keeps it off while this holds. Never throws (false when the probe is disabled).
 /// </summary>
        public static bool SwimGate(MyCharacter c, Vector3D eye, ulong tick)
        {
            if (_disabled || c == null) return false;
            try
            {
                ReadSettings();
                return ProbeCharacterCore(c, eye, tick).InRoom;
            }
            catch (Exception e)
            {
                Disable("swim gate", e);
                return false;
            }
        }

        /// <summary>The newest answer of a character id (false when never probed or older than the camera tick by 2 ticks).</summary>
        public static bool CharacterInRoom(long characterId)
        {
            CharacterProbe cp;
            if (_disabled || !Characters.TryGetValue(characterId, out cp)) return false;
            if (_cameraTick > cp.Tick + CharacterFreshTicks) return false;
            return cp.Last.InRoom;
        }

        /// <summary>
 /// The C1 writer's hook (after the composer): the interior extension 0xB0-0xBF of this record — the camera's answer,
 /// plus <c>CharacterInRoom</c> of the frame's <c>characterId</c>. Version 1 always (the extension exists; a disabled
 /// probe writes "no room"). Never throws.
 /// </summary>
        public static void FillWire(ref SeFrame f)
        {
            try
            {
                C1Interior v = _disabled ? C1Interior.None(Pressurization) : _camera;
                if (f.characterId != 0 && CharacterInRoom((long)f.characterId)) v.Flags |= S7Conventions.C1InteriorFlags.CharacterInRoom;
                else v.Flags = (byte)(v.Flags & ~S7Conventions.C1InteriorFlags.CharacterInRoom);
                S7InteriorWire.Write(ref f, in v);
            }
            catch (Exception e)
            {
                Disable("C1 fill", e);
            }
        }

        private static C1Interior ProbeCharacterCore(MyCharacter c, Vector3D eye, ulong tick)
        {
            long id = c.EntityId;
            CharacterProbe cp;
            if (!Characters.TryGetValue(id, out cp))
            {
                if (Characters.Count > 64) Characters.Clear(); // a long session with many respawns: keep the table bounded
                cp = new CharacterProbe();
                Characters[id] = cp;
            }
            if (cp.Tick == tick && cp.Probes > 0) return cp.Last; // once per tick per character
            int n = Candidates(eye);
            cp.Last = Decide(cp.Hold, eye, n, SeatedInPressurizedCockpit(c));
            cp.Tick = tick;
            cp.Probes++;
            CharacterProbes++;
            return cp.Last;
        }

        // ============================================================================================ probe

        private static void ReadSettings()
        {
            MySession s = MySession.Static;
            bool oxy = false, press = false;
            if (s != null && s.Settings != null)
            {
                oxy = s.Settings.EnableOxygen;
                press = s.Settings.EnableOxygenPressurization;
            }
            uint epoch = Phases.WorldEpoch;
            bool changed = epoch != _settingsEpoch || oxy != _oxygen || press != _pressurization;
            _oxygen = oxy;
            _pressurization = press;
            if (!changed) return;
            if (epoch != _settingsEpoch)
            {
                _settingsEpoch = epoch;
                _offLogged = false;
                CameraHold.Clear();
                Characters.Clear();
            }
            DeLog.Info("DE.Interior settings EnableOxygen=" + (oxy ? "on" : "off") + " EnableOxygenPressurization=" + (press ? "on" : "off") +
                       " (world epoch " + epoch + "; read only, the world is the owner's)");
            if (!(oxy && press) && !_offLogged)
            {
                _offLogged = true;
                DeLog.Info("DE.Interior OFF pressurization disabled (EnableOxygen=" + (oxy ? "on" : "off") + ", EnableOxygenPressurization=" + (press ? "on" : "off") +
                           "): no airtight room, no RoomSet; interiors stay wet in Subnautica");
            }
        }

        /// <summary>The mirrored grids whose world AABB contains <paramref name="p"/>, nearest centre first, into <c>_cands</c>.</summary>
        private static int Candidates(Vector3D p)
        {
            int n = 0;
            if (!Pressurization) return 0;
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed || g.MarkedForClose) continue;
                if (mg.Projection) continue; // S7p-scene-above (P7): a projector's hologram is never a room (the swim gate)
                BoundingBoxD box = g.PositionComp.WorldAABB;
                if (box.Contains(p) == ContainmentType.Disjoint) continue;
                double d = Vector3D.DistanceSquared(box.Center, p);
                // insertion by distance (few candidates)
                if (n == MaxCandidates && d >= _candD[n - 1]) continue;
                int k = n < MaxCandidates ? n : n - 1;
                while (k > 0 && _candD[k - 1] > d) { _cands[k] = _cands[k - 1]; _candD[k] = _candD[k - 1]; k--; }
                _cands[k] = g;
                _candD[k] = d;
                if (n < MaxCandidates) n++;
            }
            return n;
        }

        /// <summary>
 /// Review F14: the probed character sits in a cockpit whose definition is pressurized
 /// Keyed by the character passed in, never
 /// "the player".
 /// </summary>
        internal static bool SeatedInPressurizedCockpit(MyCharacter c)
        {
            if (c == null || c.Closed) return false;
            var cockpit = c.Parent as MyCockpit;
            return cockpit != null && cockpit.BlockDefinition != null && cockpit.BlockDefinition.IsPressurized;
        }

        private static C1Interior Decide(RoomFlagHold hold, Vector3D p, int n, bool pressurizedSeat)
        {
            for (int i = 0; i < n; i++) _probes[i] = ProbeGrid(_cands[i], p, pressurizedSeat);
            long holdsBefore = hold.Holds;
            C1Interior r = hold.Decide(_probes, n, Pressurization);
            Holds += hold.Holds - holdsBefore;
            C1Interior s12; // S12 hook: the oxygen-volume air rule with hysteresis decides (format air.volume/air.hold); false = this S7 answer stays (S12 off, its air switch off, S12.M1, S7.M10)
            if (S12Air.TryDecide(hold, ReferenceEquals(hold, CameraHold), p, _cands, n, pressurizedSeat, Pressurization, r, out s12)) r = s12;
            for (int i = 0; i < n; i++) _cands[i] = null;
            return r;
        }

        private static RoomProbe ProbeGrid(MyCubeGrid g, Vector3D p, bool pressurizedSeat)
        {
            var r = new RoomProbe { GridId = (ulong)g.EntityId, RoomIndex = -1 };
            MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
            if (gas == null) { NoGasSystem++; return r; }
            Vector3I cell = g.WorldToGridInteger(p);
            MyOxygenRoom room = null;
            if (S7Mutants.M10)
            {
                // S7.M10: the flag from GetRooms every tick (no hold; GetRooms answers false while processing)
                M10Reads++;
                _m10Rooms.Clear();
                if (gas.GetRooms(_m10Rooms))
                {
                    foreach (IMyOxygenRoom ir in _m10Rooms)
                    {
                        var mr = ir as MyOxygenRoom;
                        if (mr != null && mr.Blocks != null && mr.Blocks.Contains(cell)) { room = mr; break; }
                    }
                }
                _m10Rooms.Clear();
            }
            else
            {
                if (((IMyGridGasSystem)gas).IsProcessingData) { r.Processing = true; return r; }
                room = gas.GetOxygenRoomForCubeGridPosition(ref cell);
                if (room == null && pressurizedSeat)
                {
                    // review F14: inside a block (a pressurized cockpit) only; any other null room is no room
                    MyOxygenBlock ob = gas.GetSafeOxygenBlock(p);
                    room = ob != null ? ob.Room : null;
                }
                if (room == null) CheckAirVent(g, gas);
            }
            if (room == null) return r;
            r.HasRoom = true;
            r.Airtight = room.IsAirtight;
            r.RoomIndex = room.Index;
            r.RoomO2 = room.OxygenLevel(g.GridSize);
            return r;
        }

        /// <summary>
 /// Review F1: the number of gas blocks of a grid's gas system (the private <c>m_gasBlocks</c>), or -1 when unknown
 /// (the field is missing in this build: logged once, the diagnostic off). Never throws.
 /// </summary>
        internal static int GasBlockCount(MyGridGasSystem gas)
        {
            if (gas == null || _gasBlocksMissing) return -1;
            try
            {
                if (!_gasBlocksReflected)
                {
                    _gasBlocksReflected = true;
                    _gasBlocksField = typeof(MyGridGasSystem).GetField("m_gasBlocks", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_gasBlocksField == null || !typeof(ICollection).IsAssignableFrom(_gasBlocksField.FieldType))
                    {
                        _gasBlocksMissing = true;
                        _gasBlocksField = null;
                        DeLog.Info("DE.S7 interior OFF missing MyGridGasSystem.m_gasBlocks (the Air Vent diagnostic only; the probe keeps working)");
                        return -1;
                    }
                }
                var list = _gasBlocksField.GetValue(gas) as ICollection;
                return list != null ? list.Count : -1;
            }
            catch (Exception e)
            {
                _gasBlocksMissing = true;
                DeLog.Info("DE.S7 interior Air Vent diagnostic OFF (" + e.GetType().Name + ": " + e.Message + ")");
                return -1;
            }
        }

        /// <summary>Review F1: at most once a second per grid; the log line once per grid id while it has no gas block.</summary>
        private static void CheckAirVent(MyCubeGrid g, MyGridGasSystem gas)
        {
            ulong now = Win32.TickCount64();
            VentCheck v;
            if (!_vents.TryGetValue(g.EntityId, out v))
            {
                if (_vents.Count > 256) _vents.Clear();
                v = new VentCheck();
                _vents[g.EntityId] = v;
            }
            if (now < v.NextMs) return;
            v.NextMs = now + VentCheckMs;
            v.GasBlocks = GasBlockCount(gas);
            if (v.GasBlocks != 0 || g.BlocksCount <= 0 || v.Logged) return;
            v.Logged = true;
            NoAirVentLogged++;
            DeLog.Info("DE.Interior: no airtight room possible on " + g.EntityId.ToString(CultureInfo.InvariantCulture) + " (" + (g.DisplayName ?? "?") +
                       ", " + g.BlocksCount + " blocks): it has no Air Vent (MyGridGasSystem pressurizes only with a gas block)");
        }

        /// <summary>Review F1: the candidate grids last seen without a gas block (status and rows).</summary>
        private static List<object> NoAirVentGrids()
        {
            var l = new List<object>();
            foreach (KeyValuePair<long, VentCheck> kv in _vents)
                if (kv.Value.GasBlocks == 0) l.Add(kv.Key.ToString(CultureInfo.InvariantCulture));
            return l;
        }

        /// <summary>Held answers of grids that left the mirror are dropped (no stale hold when a grid comes back).</summary>
        private static void RetainAlive()
        {
            if (CameraHold.HeldGrids == 0 && Characters.Count == 0) return;
            if (CameraHold.HeldGrids <= GridMirrorProducer.Grids.Count && (_cameraTickCounter++ & 63) != 0) return; // every 64 ticks
            _alive.Clear();
            foreach (long id in GridMirrorProducer.Grids.Keys) _alive.Add((ulong)id);
            CameraHold.Retain(_alive);
            foreach (CharacterProbe cp in Characters.Values) cp.Hold.Retain(_alive);
        }

        private static long _cameraTickCounter;

        /// <summary>World unload (the plugin's phase leaves InWorld): every hold and character answer forgotten.</summary>
        public static void OnWorldUnload(string why)
        {
            CameraHold.Clear();
            Characters.Clear();
            _camera = C1Interior.None(false);
            _settingsEpoch = uint.MaxValue;
            _rowHas = false;
            _vents.Clear();
        }

        // ============================================================================================ rows and status

        private static void Row(ulong tick, long localCharacterId)
        {
            ulong now = Win32.TickCount64();
            bool chr = localCharacterId != 0 && CharacterInRoom(localCharacterId);
            bool changed = !_rowHas || _rowLast.Flags != _camera.Flags || _rowLast.RoomGridId != _camera.RoomGridId || _rowLast.RoomIndex != _camera.RoomIndex ||
                           _rowLastCharacter != chr;
            bool periodic = _cameraCandidates > 0 && now >= _nextRowMs;
            if (!changed && !periodic) return;
            _rowHas = true;
            _rowLast = _camera;
            _rowLastCharacter = chr;
            _nextRowMs = now + RowIntervalMs;
            Rows++;
            Jsonl.WriteLineBuffered(JsonText.Serialize(RowData(tick, chr, changed ? "change" : "periodic")));
        }

        private static Dictionary<string, object> RowData(ulong tick, bool characterInRoom, string why)
        {
            C1Interior c = _camera;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.interior" }, { "tick", (long)tick }, { "why", why },
                { "inRoom", c.InRoom }, { "processing", c.Has(S7Conventions.C1InteriorFlags.Processing) }, { "breached", c.Has(S7Conventions.C1InteriorFlags.Breached) },
                { "roomGridId", c.RoomGridId.ToString(CultureInfo.InvariantCulture) }, { "roomIndex", c.RoomIndex == S7InteriorWire.NoRoom ? -1L : (long)c.RoomIndex },
                { "roomO2", (long)c.RoomO2 }, { "characterInRoom", characterInRoom }, { "candidates", (long)_cameraCandidates },
                { "settings", new Dictionary<string, object>(StringComparer.Ordinal) { { "oxygen", _oxygen }, { "pressurization", _pressurization } } },
                { "disabled", _disabled }, { "m10", S7Mutants.M10 },
                // review F1 / F14
                { "noAirVent", NoAirVentGrids() }, { "noRoomData", InteriorRooms.NoRoomData }, { "pressurizedSeat", SeatedInPressurizedCockpitOfLocal() },
            };
        }

        /// <summary><c>de.interior {action: status}</c>: the camera's answer, the candidates and their gas systems, the RoomSets.</summary>
        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            C1Interior c = _camera;
            d["tick"] = (long)_cameraTick;
            d["inRoom"] = c.InRoom;
            d["flags"] = (long)c.Flags;
            d["processing"] = c.Has(S7Conventions.C1InteriorFlags.Processing);
            d["breached"] = c.Has(S7Conventions.C1InteriorFlags.Breached);
            d["roomGridId"] = c.RoomGridId.ToString(CultureInfo.InvariantCulture);
            d["roomIndex"] = c.RoomIndex == S7InteriorWire.NoRoom ? -1L : (long)c.RoomIndex;
            d["roomO2"] = (long)c.RoomO2;
            d["candidates"] = (long)_cameraCandidates;
            d["settings"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "oxygen", _oxygen }, { "pressurization", _pressurization } };
            var chars = new List<object>();
            foreach (KeyValuePair<long, CharacterProbe> kv in Characters)
                chars.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", kv.Key.ToString(CultureInfo.InvariantCulture) }, { "inRoom", kv.Value.Last.InRoom }, { "tick", (long)kv.Value.Tick },
                    { "roomGridId", kv.Value.Last.RoomGridId.ToString(CultureInfo.InvariantCulture) }, { "fresh", _cameraTick <= kv.Value.Tick + CharacterFreshTicks },
                });
            d["characters"] = chars;
            var grids = new List<object>();
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid g = mg.Grid;
                if (g == null || g.Closed) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                if (gas == null) continue;
                grids.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", g.EntityId.ToString(CultureInfo.InvariantCulture) }, { "processing", ((IMyGridGasSystem)gas).IsProcessingData },
                    { "large", g.GridSizeEnum == VRage.Game.MyCubeSize.Large },
                });
            }
            d["gasSystems"] = grids;
            d["rooms"] = InteriorRooms.Status();
            d["noAirVent"] = NoAirVentGrids();
            d["noAirVentLogged"] = NoAirVentLogged;
            d["noRoomData"] = InteriorRooms.NoRoomData;
            d["pressurizedSeat"] = SeatedInPressurizedCockpitOfLocal();
            d["probes"] = Probes;
            d["characterProbes"] = CharacterProbes;
            d["holds"] = Holds;
            d["noGasSystem"] = NoGasSystem;
            d["m10Reads"] = M10Reads;
            d["usLast"] = Math.Round(LastUs, 2);
            d["usMax"] = Math.Round(MaxUs, 2);
            d["disabled"] = _disabled;
            if (_disabledWhy != null) d["disabledWhy"] = _disabledWhy;
            d["mutantM10"] = S7Mutants.M10;
            d["conventions"] = S7Conventions.Header + " 0x" + S7Conventions.Hash.ToString("x16", CultureInfo.InvariantCulture);
            Jsonl.WriteLineBuffered(JsonText.Serialize(RowData(_cameraTick, CharacterInRoomOfLocal(), "status")));
            return d;
        }

        /// <summary><c>de.interior {action: probe, point: [x, y, z]}</c> (SE world; default the camera): the per-grid raw answers, no hold.</summary>
        public static Dictionary<string, object> ProbeAt(Vector3D p)
        {
            ReadSettings();
            int n = Candidates(p);
            var list = new List<object>();
            bool seat = SeatedInPressurizedCockpitOfLocal();
            for (int i = 0; i < n; i++)
            {
                MyCubeGrid g = _cands[i];
                RoomProbe r = ProbeGrid(g, p, seat);
                MyGridGasSystem gs = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                Vector3I cell = g.WorldToGridInteger(p);
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "grid", g.EntityId.ToString(CultureInfo.InvariantCulture) }, { "cell", new List<object> { (long)cell.X, (long)cell.Y, (long)cell.Z } },
                    { "processing", r.Processing }, { "hasRoom", r.HasRoom }, { "airtight", r.Airtight }, { "roomIndex", (long)r.RoomIndex },
                    { "roomO2", Math.Round(r.RoomO2, 4) }, { "gasBlocks", (long)GasBlockCount(gs) }, { "blocks", (long)g.BlocksCount },
                });
                _cands[i] = null;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "point", new List<object> { p.X, p.Y, p.Z } }, { "candidates", list }, { "pressurization", Pressurization },
            };
        }

        private static bool CharacterInRoomOfLocal()
        {
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            return c != null && CharacterInRoom(c.EntityId);
        }

        private static bool SeatedInPressurizedCockpitOfLocal()
        {
            MySession s = MySession.Static;
            return SeatedInPressurizedCockpit(s != null ? s.LocalCharacter : null);
        }

        private static void Disable(string what, Exception e)
        {
            Errors++;
            if (_disabled) return;
            _disabled = true;
            _disabledWhy = what + ": " + e.GetType().Name + ": " + e.Message;
            DeLog.Info("DE.S7 interior OFF (" + _disabledWhy + "): the camera reads as outside every room from now on; C1 keeps flowing. " + e.StackTrace);
        }
    }
}
