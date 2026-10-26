using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S15 "Gameplay and immersion polish", the SE side (ROADMAP §14 "### S15" and its addenda; docs/S15-BRIEF.md parts A-C;
 /// CONTRACT §5.4 "S15 conventions"). Game thread: <see cref="Tick"/> from Plugin.UpdateCore (S15 hook) and the C6 writer
 /// from Link.WriteSeTime inside the PrepareForDraw postfix (the same update thread). Every game member VERIFIED in the
 /// decompiled build:
 /// <list type="bullet">
 /// <item><b>A, aboard</b> (format aboard): the cockpit the local character sits in, <c>MyCharacter.UsingEntity</c>
 /// as <c>MyCockpit</c>, its
 /// <c>CubeGrid</c>; else the grid whose airtight room holds the head
 /// else (the power lines
 /// only) a station grid whose world box holds the head. A door, a ladder
 /// or a ship's open deck is none of these, so it never counts.</item>
 /// formats voice.welcome, voice.power): Welcome for a large grid or a
 /// station, SmallWelcome (the Seamoth's line) for a small dynamic grid,
 /// at the boarding (seat or sealed room); EnginePowerUp while the grid is powered: its distributor
 /// answers
 /// <c>ResourceStateByType(ElectricityId, false)</c> other than
 /// NoPower (SE's own PowerStateWorks,: the state OnPowerGenerationChanged follows), so the edge is a
 /// reactor placed or fuelled, the
 /// switch on, any source; EnginePowerDown while <c>MyCubeGrid.IsPowerSwitchOn</c> is false after it
 /// went off from a powered grid: SE's Y key
 /// Never thrust. The S15 gate (Wire <see cref="S15VoiceGate"/>, over S16's voice rule) decides; a sent event bumps its C5
 /// counter (S15SeWire); rows s15.aboard, s15.cond, s15.power, s15.voice.</item>
 /// <item><b>C, the sky</b>: the planets <c>MyPlanets.GetPlanets</c> with <c>AverageRadius</c>,
 /// <c>AtmosphereRadius</c>, <c>HasAtmosphere</c>, <c>Generator</c> every 300 ticks into the
 /// list (listSeq + 1 on a change); the frame at <c>MySector.MainCamera</c>'s <c>WorldMatrix</c>
 /// every C6 write. Rows s15.skylist, s15.sky (every 60 ticks: each body in SE's camera frame).</item>
 /// <item><b>B, the water material</b> at bind: <c>de.water texture DE_4546B_Water</c> (WaterLook, the Water Mod's own
 /// WaterSettings.Texture) when SE loaded it
 /// Row s15.water. Orbit and band captures: <c>MyRenderProxy.TakeScreenshot</c>
 /// </item>
 /// </list>
 /// A failure is caught, counted and logged at most every 10 s; it never reaches SE.
 /// </summary>
    internal static unsafe partial class S15Se
    {
        private static bool _enabledRead, _enabled, _active, _siteRow, _waterDone;
        public static string InactiveWhy = "not started";
        private static readonly RateLimit ErrorLog = new RateLimit(10000);
        public static long Errors, Scans, Events, Played, Cooldowns, SeAudioSkips;
        /// <summary>The lines played per event name (de.s15 status playedBy: the launcher's checks).</summary>
        private static readonly Dictionary<string, object> PlayedBy = new Dictionary<string, object>(StringComparer.Ordinal);

        private static readonly S15VoiceGate Gate = new S15VoiceGate();
        private static readonly S15AboardHold Hold = new S15AboardHold();
        private static readonly S15PowerSwitch PowerSw = new S15PowerSwitch();
        private static ulong _lastTick, _activeTick, _nextScanTick, _nextListTick;
        private static long _lastScanMs;
        private static bool _baselineDue, _liveLast;
        private static ulong _waterNextTick;
        private static int _waterTries;
        public const int WaterRetryTicks = 600, WaterMaxTries = 30;
        private static long _aboard;
        private static string _aboardHow = "none";
        private static MyCubeGrid _aboardGrid;
        private static readonly bool[] Cond = new bool[S15Conventions.Event.Count + 1];
        private static readonly bool[] Now = new bool[S15Conventions.Event.Count + 1];
        // the aboard grid this scan (and the powered state of the scan before: format voice.power)
        private static bool _powered, _poweredBefore, _switchOn, _large, _station;
        private static int _reactors, _reactorsWorking;
        private static double _uraniumKg;
        private static float _thrustMax;
        private static string _powerKey = "";
        private static readonly List<MyEntity> Near = new List<MyEntity>();
        private static readonly MyDefinitionId UraniumIngot = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Uranium");
        private static bool _powerReadLogged;

        // C: the body list and the frame (written inside the C6 seqlock window by WriteSky)
        private static readonly List<S15Body> Bodies = new List<S15Body>();
        private static readonly List<long> BodyIds = new List<long>();
        public static uint ListSeq;
        private static string _listKey = "";
        private static uint _listWritten = uint.MaxValue;
        private static ulong _nextListWriteTick;
        private static Vector3D _prevFwd;
        private static bool _hasPrevFwd;
        public static long SkyRows, FrameWrites;
        private static S15SkyFrame _lastFrame;

        public static string WaterState = "not applied";
        private static readonly S15SeWire.C6Writer SkyWriter = WriteSky;   // one delegate, not one per tick

 // ======================================================================================================== tick

 /// <summary>Plugin hook: once per update. <paramref name="snBusTarget"/> = this tick's C5 snBusTarget (the S2 rule).</summary>
        public static void Tick(ulong nowMs, bool inWorld, float snBusTarget)
        {
            try { TickCore(nowMs, inWorld, snBusTarget); }
            catch (Exception e)
            {
                Errors++;
                if (ErrorLog.Allow(nowMs)) DeLog.Info("DE.Error S15 tick " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        private static void TickCore(ulong nowMs, bool inWorld, float snBusTarget)
        {
            if (!inWorld)
            {
                if (_active) Reset("world gone");
                S15SeWire.Off();
                S15SeWire.C6Extra = null;
                return;
            }
            if (!_enabledRead)
            {
                _enabledRead = true;
                _enabled = S15Switch.EnabledFromEnvironment();
                DeLog.Info("DE.S15 " + (_enabled ? "on" : "OFF (" + S15Switch.EnvVar + "=off)") + ": conventions 0x" + S15Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) +
                           " (" + S15Conventions.Header + (S15Conventions.Hash() == S15Conventions.PinnedHash ? ", pinned" : ", MISMATCH") + "); voice " + S15Switch.VoiceOn +
                           ", sky " + S15Switch.SkyOn + ", water " + S15Switch.WaterOn);
            }
            if (!_enabled) { Inactive("DEEPENGINEERS_S15=off"); return; }
            if (!ActivationGate.IsOpen) { Inactive("activation gate " + ActivationGate.State); return; }
            if (!SiteBinding.IsOk || SiteBinding.Planet == null) { Inactive("no bound DE_4546B"); return; }
            ulong tick = GameState.GameplayTick;
            if (tick == _lastTick && _active) return;
            _lastTick = tick;
            if (!_active)
            {
                _active = true;
                InactiveWhy = null;
                _activeTick = tick;
                _nextScanTick = _nextListTick = 0;
                DeLog.Info("DE.S15 ACTIVE: the owner's four Cyclops/Seamoth lines on SE grids, SE's planets in Subnautica's sky, the DE_4546B water material");
            }
            S15SeWire.C6Extra = SkyWriter;
            if (!_siteRow)
            {
                _siteRow = true;
                StringBuilder sb = Head("site");
                S5Rows.Str(sb, "hash", "0x" + S15Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture));
                S5Rows.Bool(sb, "pinned", S15Conventions.Hash() == S15Conventions.PinnedHash);
                S5Rows.Str(sb, "header", S15Conventions.Header);
                S5Rows.Str(sb, "mutants", S15SeMutants.Active);
                S5Rows.Bool(sb, "voice", S15Switch.VoiceOn);
                S5Rows.Bool(sb, "sky", S15Switch.SkyOn);
                S5Rows.Bool(sb, "water", S15Switch.WaterOn);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
            // B: the water material 2 s after S15 is active (after WaterLook's own defaults at the bind, SiteBinding's
 // UpdateBeforeSimulation, whose JWater2 would else replace it), then every 10 s until it reads back as ours or is
 // refused for good
            if (!_waterDone && tick >= Math.Max(_activeTick + 120UL, _waterNextTick)) WaterStep(tick);
            if (tick >= _nextListTick) { _nextListTick = tick + (ulong)S15Conventions.Sky.ListRefreshTicks; RefreshBodies(tick); }
            if (tick >= _nextScanTick) { _nextScanTick = tick + (ulong)S15Conventions.Voice.ScanTicks; Scan(tick, snBusTarget); }
            var w = new S15WireState
            {
                Flags = (byte)Flags(),
                WorldEpoch = Phases.WorldEpoch,
                GridId = _aboard,
            };
            S15SeWire.Set(w);
        }

        private static int Flags()
        {
            int f = 0;
            if (_aboard != 0) f |= S15Conventions.C5Flags.Aboard;
            if (_aboardHow == "seat") f |= S15Conventions.C5Flags.Seated;
            if (_aboardHow == "room") f |= S15Conventions.C5Flags.Room;
            if (_aboard != 0 && _powered) f |= S15Conventions.C5Flags.Powered;
            if (_aboard != 0 && (_large || _station)) f |= S15Conventions.C5Flags.Large;
            if (_aboard != 0 && _station) f |= S15Conventions.C5Flags.Station;
            return f;
        }

        private static void Inactive(string why)
        {
            if (_active) Reset(why);
            if (InactiveWhy != why)
            {
                InactiveWhy = why;
                DeLog.Info("DE.S15 inactive: " + why);
            }
            S15SeWire.Off();
            S15SeWire.C6Extra = null;
        }

        private static void Reset(string why)
        {
            _active = false;
            _siteRow = false;
            _waterDone = false;
            _aboard = 0;
            _aboardHow = "none";
            _aboardGrid = null;
            for (int i = 0; i < Cond.Length; i++) Cond[i] = false;
            Gate.Clear();
            Hold.Reset();
            PowerSw.Reset();
            ClearSample();
            Bodies.Clear();
            BodyIds.Clear();
            _listKey = "";
            _listWritten = uint.MaxValue;
            _hasPrevFwd = false;
            _lastTick = _activeTick = _nextScanTick = _nextListTick = _nextListWriteTick = 0;
            _lastScanMs = 0;
            _baselineDue = _liveLast = false;
            _waterTries = 0;
            _waterNextTick = 0;
            WaterState = "not applied";
            PicksReset();
            DeLog.Info("DE.S15 reset (" + why + ")");
        }

        private static long Ms(ulong tick) { return (long)(tick * 1000UL / 60UL); }

        // ======================================================================================================== A: aboard and the voice

        private static void Scan(ulong tick, float snBusTarget)
        {
            Scans++;
            long ms = Ms(tick);
            string how;
            MyCubeGrid g;
            long raw = AboardRaw(out how, out g);
            long counted = Hold.Step(raw, ms, false);
            if (counted != _aboard)
            {
                long prev = _aboard;
                if (prev != 0)
                    for (int ev = 1; ev <= S15Conventions.Event.Count; ev++)
                    {
                        if (Cond[ev]) CondRow(ev, prev, false, ms, tick, snBusTarget, _lastScanMs, false);
                        Gate.Off(ev, prev, ms);
                        Cond[ev] = false;
                    }
                _aboard = counted;
                _aboardHow = counted != 0 ? how : "none";
                _aboardGrid = counted != 0 ? g : null;
                _baselineDue = counted != 0;
                _powerKey = "";
                ClearSample();
                StringBuilder sb = Head("aboard");
                S5Rows.Id(sb, "grid", counted);
                S5Rows.Id(sb, "from", prev);
                S5Rows.Str(sb, "how", _aboardHow);
                S5Rows.Str(sb, "name", _aboardGrid != null ? _aboardGrid.DisplayName : null);
                S5Rows.Bool(sb, "large", _aboardGrid != null && _aboardGrid.GridSizeEnum == MyCubeSize.Large);
                S5Rows.Bool(sb, "station", _aboardGrid != null && _aboardGrid.IsStatic);
                S5Rows.Int(sb, "tick", (long)tick);
                S5Rows.Int(sb, "ms", ms);
                S5Rows.Str(sb, "mutants", S15SeMutants.Active);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
                DeLog.Info("DE.S15 aboard " + (counted != 0 ? "grid " + counted + " (" + _aboardHow + ")" : "none") + (prev != 0 ? " (left " + prev + ")" : ""));
            }
            else if (counted != 0 && how != _aboardHow && g == _aboardGrid)
            {
                // the same grid by another way (a base's sealed room → its open hall): the welcome condition follows the way
                _aboardHow = how;
                StringBuilder sb = Head("aboard");
                S5Rows.Id(sb, "grid", counted);
                S5Rows.Id(sb, "from", counted);
                S5Rows.Str(sb, "how", _aboardHow);
                S5Rows.Int(sb, "tick", (long)tick);
                S5Rows.Int(sb, "ms", ms);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
            // the S2 rule (format voice.gate): Subnautica's audio is live, and the voice is switched on (de.s15 voice); a change is
 // one s15.live row, before the gate's samples of this scan
            bool live = S15VoiceGate.SnAudioLive(snBusTarget) && S15Switch.VoiceOn;
            if (live != _liveLast)
            {
                _liveLast = live;
                StringBuilder lb = Head("live");
                S5Rows.Bool(lb, "live", live);
                S5Rows.Num(lb, "snBus", snBusTarget);
                S5Rows.Bool(lb, "voiceOn", S15Switch.VoiceOn);
                S5Rows.Int(lb, "ms", ms);
                S5Rows.Int(lb, "tick", (long)tick);
                Jsonl.WriteLineBuffered(S5Rows.End(lb));
            }
            PicksScan(tick, ms);   // the kept picks (S15SePicks.cs): the sonar, the far sea
            if (_aboard == 0 || _aboardGrid == null || _aboardGrid.Closed) { ClearSample(); _lastScanMs = ms; return; }
            _poweredBefore = _powered;
            Sample(_aboardGrid);
            bool baseline = _baselineDue;
            _baselineDue = false;
            if (baseline) { _poweredBefore = _powered; PowerSw.Baseline(_aboard, _switchOn, _powered); }
            PowerRow(ms, tick, baseline);
            bool[] now = Now;
            bool seatOrRoom = _aboardHow == "seat" || _aboardHow == "room" || _aboardHow == "box";
            bool big = _large || _station || S15SeMutants.SizeIgnored;
            now[S15Conventions.Event.Welcome] = seatOrRoom && big;
            now[S15Conventions.Event.SmallWelcome] = seatOrRoom && !big;
            // S15.M16: the removed engine rule (any thruster's strength or override at 0.1) instead of the power state
            now[S15Conventions.Event.EnginePowerUp] = S15SeMutants.ThrustPower ? _thrustMax >= 0.1f : _powered;
            bool down = PowerSw.Step(_aboard, _switchOn, _powered);
            now[S15Conventions.Event.EnginePowerDown] = !baseline && down;
            for (int ev = 1; ev <= S15Conventions.Event.Count; ev++)
            {
                bool power = ev == S15Conventions.Event.EnginePowerUp || ev == S15Conventions.Event.EnginePowerDown;
                if (baseline && power && !S15SeMutants.NoRules)
                {
                    // a state that already holds when the grid becomes aboard is no event (a powered grid, a switch already off)
                    if (now[ev]) CondRow(ev, _aboard, true, ms, tick, snBusTarget, _lastScanMs, true);
                    Cond[ev] = now[ev];
                    Gate.Baseline(ev, _aboard, now[ev], ms);
                    continue;
                }
                if (now[ev] != Cond[ev]) CondRow(ev, _aboard, now[ev], ms, tick, snBusTarget, _lastScanMs, false);
                Cond[ev] = now[ev];
                bool welcome = ev == S15Conventions.Event.Welcome || ev == S15Conventions.Event.SmallWelcome;
                bool held = Gate.IsPending(ev, _aboard);
                S15GateResult r = Gate.Step(ev, _aboard, now[ev], ms, live, S15SeMutants.NoRules, welcome && S15SeMutants.HeldWelcome);
                if (r != S15GateResult.None) VoiceRow(ev, r, tick, ms, snBusTarget, held, _aboard);
            }
            _lastScanMs = ms;
        }

        private static void ClearSample()
        {
            _powered = _poweredBefore = _large = _station = false;
            _switchOn = true;
            _reactors = _reactorsWorking = 0;
            _uraniumKg = 0;
            _thrustMax = 0f;
        }

        /// <summary>format aboard: the raw aboard grid (0 = none) before the hold: a seat, a sealed room, a station's box.</summary>
        private static long AboardRaw(out string how, out MyCubeGrid grid)
        {
            how = "none";
            grid = null;
            MySession s = MySession.Static;
            MyCharacter ch = s != null ? s.LocalCharacter : null;
            if (ch == null || ch.IsDead) return 0;
            MyCockpit seat = ch.UsingEntity as MyCockpit;
            if (seat == null) seat = s.ControlledEntity as MyCockpit;
            if (seat != null && seat.CubeGrid != null && !seat.CubeGrid.Closed)
            {
                how = "seat";
                grid = seat.CubeGrid;
                return grid.EntityId;
            }
            Vector3D head = ch.GetHeadMatrix(false).Translation;
            var box = new BoundingBoxD(head - new Vector3D(0.5), head + new Vector3D(0.5));
            Near.Clear();
            MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, Near);
            MyCubeGrid station = null;
            try
            {
                foreach (MyEntity e in Near)
                {
                    var g = e as MyCubeGrid;
                    if (g == null || g.Closed || g.Physics == null) continue;
                    if (S15SeMutants.AnyGrid)
                    {
                        // S15.M2: any grid box around the head counts (a door, a ladder, a deck)
                        how = "box";
                        grid = g;
                        return g.EntityId;
                    }
                    MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                    if (gas != null)
                    {
                        Vector3I cell = g.WorldToGridInteger(head);
                        MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref cell);
                        if (room != null && room.IsAirtight)
                        {
                            how = "room";
                            grid = g;
                            return g.EntityId;
                        }
                    }
                    // a base or a house (format aboard, the power lines only): a station whose world box holds the head
                    if (station == null && g.IsStatic && g.PositionComp.WorldAABB.Contains(head) != ContainmentType.Disjoint) station = g;
                }
            }
            finally { Near.Clear(); }
            if (station != null)
            {
                how = "station";
                grid = station;
                return station.EntityId;
            }
            return 0;
        }

        /// <summary>The aboard grid's size, power, switch and reactors (format voice.power); the thrust only for S15.M16 and the rows.</summary>
        private static void Sample(MyCubeGrid g)
        {
            _large = g.GridSizeEnum == MyCubeSize.Large;
            _station = g.IsStatic;
            _switchOn = g.IsPowerSwitchOn;
            _powered = Powered(g);
            int n = 0, w = 0;
            double kg = 0;
            foreach (MyReactor r in g.GetFatBlocks<MyReactor>())
            {
                n++;
                if (r.IsWorking) w++;
                MyInventory inv = r.GetInventory();   // MyEntityExtensions.GetInventory
                if (inv != null) kg += (double)inv.GetItemAmount(UraniumIngot);   // MyInventory.GetItemAmount
            }
            _reactors = n;
            _reactorsWorking = w;
            _uraniumKg = kg;
            float tmax = 0f;
            foreach (MyThrust t in g.GetFatBlocks<MyThrust>())
            {
                float s = Math.Max(t.CurrentStrength, ((Sandbox.ModAPI.Ingame.IMyThrust)t).ThrustOverridePercentage);
                if (s > tmax) tmax = s;
            }
            _thrustMax = tmax;
        }

        /// <summary>
 /// The grid's electricity works (SE's own PowerStateWorks: not NoPower), read without a recompute (SE recomputes a dirty
 /// distribution in its own update; the switch recomputes at once). False when the read fails (logged once).
 /// </summary>
        private static bool Powered(MyCubeGrid g)
        {
            try
            {
                MyResourceDistributorComponent d = g.GridSystems != null ? g.GridSystems.ResourceDistributor : null;
                if (d == null) return false;
                // no grid argument: with one, GetElectricalGroupIndex adds a group for a grid it has not grouped yet
                MyResourceStateEnum st = d.ResourceStateByType(MyResourceDistributorComponent.ElectricityId, false);
                return st != MyResourceStateEnum.NoPower && st != MyResourceStateEnum.Disconnected;
            }
            catch (Exception e)
            {
                if (!_powerReadLogged) { _powerReadLogged = true; DeLog.Info("DE.S15 power read failed (" + e.GetType().Name + ": " + e.Message + "): the grid counts as unpowered"); }
                return false;
            }
        }

        /// <summary>One s15.power row per change of the aboard grid's power, switch or reactors (and at the boarding).</summary>
        private static void PowerRow(long ms, ulong tick, bool baseline)
        {
            string key = (_powered ? "P" : "p") + (_switchOn ? "S" : "s") + _reactors.ToString(CultureInfo.InvariantCulture) + "/" + _reactorsWorking.ToString(CultureInfo.InvariantCulture) +
                         "/" + Math.Round(_uraniumKg, 1).ToString(CultureInfo.InvariantCulture);
            if (key == _powerKey && !baseline) return;
            _powerKey = key;
            StringBuilder sb = Head("power");
            S5Rows.Id(sb, "grid", _aboard);
            S5Rows.Str(sb, "how", _aboardHow);
            S5Rows.Bool(sb, "powered", _powered);
            S5Rows.Bool(sb, "powerBefore", _poweredBefore);
            S5Rows.Bool(sb, "switchOn", _switchOn);
            S5Rows.Int(sb, "reactors", _reactors);
            S5Rows.Int(sb, "working", _reactorsWorking);
            S5Rows.Num(sb, "uraniumKg", _uraniumKg);
            S5Rows.Bool(sb, "large", _large);
            S5Rows.Bool(sb, "station", _station);
            if (baseline) S5Rows.Bool(sb, "baseline", true);
            S5Rows.Int(sb, "ms", ms);
            S5Rows.Int(sb, "tick", (long)tick);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        /// <summary>One change of a condition (the analyzer replays the gate over these): an off row carries the last on sample's ms.</summary>
        private static void CondRow(int ev, long grid, bool on, long ms, ulong tick, float snBus, long lastOnMs, bool baseline)
        {
            StringBuilder sb = Head("cond");
            S5Rows.Str(sb, "event", S15Conventions.VoiceOf(ev).Name);
            S5Rows.Int(sb, "evId", ev);
            S5Rows.Id(sb, "grid", grid);
            S5Rows.Bool(sb, "on", on);
            S5Rows.Int(sb, "ms", ms);
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Num(sb, "snBus", snBus);
            S5Rows.Bool(sb, "voiceOn", S15Switch.VoiceOn);
            if (!on) S5Rows.Int(sb, "lastOnMs", lastOnMs);
            if (baseline) S5Rows.Bool(sb, "baseline", true);
            S5Rows.Str(sb, "how", _aboardHow);
            S5Rows.Bool(sb, "large", _large);
            S5Rows.Bool(sb, "station", _station);
            S5Rows.Bool(sb, "powered", _powered);
            S5Rows.Bool(sb, "powerBefore", _poweredBefore);
            S5Rows.Bool(sb, "switchOn", _switchOn);
            S5Rows.Int(sb, "reactors", _reactors);
            S5Rows.Num(sb, "uraniumKg", _uraniumKg);
            S5Rows.Num(sb, "thrust", _thrustMax);
            S5Rows.Str(sb, "mutants", S15SeMutants.Active);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        private static void VoiceRow(int ev, S15GateResult r, ulong tick, long ms, float snBus, bool held, long grid)
        {
            Events++;
            uint seq = 0;
            switch (r)
            {
                case S15GateResult.Played:
                {
                    Played++;
                    seq = S15SeWire.Bump(ev);
                    string name = S15Conventions.VoiceOf(ev).Name;
                    object c;
                    PlayedBy[name] = (PlayedBy.TryGetValue(name, out c) ? (long)c : 0L) + 1L;
                    break;
                }
                case S15GateResult.Cooldown: Cooldowns++; break;
                case S15GateResult.SeAudio: SeAudioSkips++; break;
            }
            S15Conventions.VoiceRow row = S15Conventions.VoiceOf(ev);
            StringBuilder sb = Head("voice");
            S5Rows.Str(sb, "event", row.Name);
            S5Rows.Int(sb, "evId", ev);
            S5Rows.Id(sb, "grid", grid);
            S5Rows.Str(sb, "how", _aboardHow);
            S5Rows.Str(sb, "result", ResultName(r));
            S5Rows.Bool(sb, "played", r == S15GateResult.Played);
            S5Rows.Bool(sb, "held", held);
            S5Rows.Int(sb, "seq", seq);
            S5Rows.Int(sb, "ms", ms);
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Num(sb, "snBus", snBus);
            S5Rows.Str(sb, "mutants", S15SeMutants.Active);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S15 voice " + row.Name + " grid " + grid + " (" + _aboardHow + "): " + ResultName(r) + (held ? " (held from above the water: S15.M17)" : "") +
                       (r == S15GateResult.SeAudio ? " (SE's audio is live: skipped, never replayed)" : "") + (seq != 0 ? " seq=" + seq : ""));
        }

        public static string ResultName(S15GateResult r)
        {
            switch (r)
            {
                case S15GateResult.Played: return "played";
                case S15GateResult.Cooldown: return "cooldown";
                case S15GateResult.SeAudio: return "seAudio";
                default: return "none";
            }
        }

        /// <summary>de.s15 play: a line sent as if its event happened (dev; no gate).</summary>
        public static uint Force(int ev, string via)
        {
            uint seq = S15SeWire.Bump(ev);
            StringBuilder sb = Head("voice");
            S5Rows.Str(sb, "event", S15Conventions.VoiceOf(ev).Name);
            S5Rows.Int(sb, "evId", ev);
            S5Rows.Id(sb, "grid", _aboard);
            S5Rows.Str(sb, "how", "forced");
            S5Rows.Str(sb, "result", "forced");
            S5Rows.Bool(sb, "played", true);
            S5Rows.Int(sb, "seq", seq);
            S5Rows.Int(sb, "tick", (long)_lastTick);
            S5Rows.Int(sb, "ms", Ms(_lastTick));
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S15 voice " + S15Conventions.VoiceOf(ev).Name + " forced (" + via + ") seq=" + seq);
            return seq;
        }

        // ======================================================================================================== C: the sky

        private static void RefreshBodies(ulong tick)
        {
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            MyPlanet bound = SiteBinding.Planet;
            Vec3 c = SiteBinding.C;
            var all = new List<KeyValuePair<double, S15Body>>();
            if (planets != null)
                foreach (MyPlanet p in planets)
                {
                    if (p == null || p.Closed || p.MarkedForClose) continue;
                    Vector3D pos = p.PositionComp.GetPosition();
                    var rel = new Vec3(pos.X - c.X, pos.Y - c.Y, pos.Z - c.Z);
                    string name = p.Generator != null ? p.Generator.Id.SubtypeName : "?";
                    var b = new S15Body
                    {
                        Rel = rel,
                        RadiusM = p.AverageRadius,
                        AtmosphereM = p.HasAtmosphere ? p.AtmosphereRadius : 0f,
                        Palette = (ushort)S15Palette.Of(name),
                        Flags = (byte)((ReferenceEquals(p, bound) ? S15Conventions.BodyFlags.Bound : 0) | (p.HasAtmosphere ? S15Conventions.BodyFlags.Atmosphere : 0)),
                        Name = name,
                    };
                    all.Add(new KeyValuePair<double, S15Body>(ReferenceEquals(p, bound) ? -1.0 : rel.Length, b));
                }
            all.Sort((a, b) => a.Key.CompareTo(b.Key));
            var key = new StringBuilder();
            Bodies.Clear();
            for (int i = 0; i < all.Count && i < S15Conventions.Sky.MaxBodies; i++)
            {
                S15Body b = all[i].Value;
                Bodies.Add(b);
                key.Append(b.Name).Append('|').Append(Math.Round(b.Rel.X)).Append(',').Append(Math.Round(b.Rel.Y)).Append(',').Append(Math.Round(b.Rel.Z))
                   .Append('|').Append(Math.Round(b.RadiusM)).Append('|').Append(b.Flags).Append(';');
            }
            string k = key.ToString();
            if (k == _listKey) return;
            _listKey = k;
            ListSeq++;
            StringBuilder sb = Head("skylist");
            S5Rows.Int(sb, "listSeq", ListSeq);
            S5Rows.Int(sb, "count", Bodies.Count);
            S5Rows.Int(sb, "planets", planets != null ? planets.Count : 0);
            sb.Append(",\"bodies\":[");
            for (int i = 0; i < Bodies.Count; i++)
            {
                S15Body b = Bodies[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":"); JsonText.AppendString(sb, b.Name);
                sb.Append(",\"rel\":["); S3Rows.Vec(sb, b.Rel); sb.Append(']');
                sb.Append(",\"radius\":").Append(S3Rows.G(b.RadiusM)).Append(",\"atmo\":").Append(S3Rows.G(b.AtmosphereM));
                sb.Append(",\"palette\":").Append(b.Palette.ToString(CultureInfo.InvariantCulture)).Append(",\"bound\":").Append(b.Bound ? "true" : "false").Append('}');
            }
            sb.Append(']');
            S5Rows.Int(sb, "tick", (long)tick);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S15 sky list " + ListSeq + ": " + Bodies.Count + " bod" + (Bodies.Count == 1 ? "y" : "ies") + " (" + k.Replace(";", " ").Trim() + ")");
        }

        /// <summary>The C6 region-space writer (Link.WriteSeTime, inside its lock): the frame every C6, the list on a change.</summary>
        private static unsafe void WriteSky(byte* c6Region, ulong tick)
        {
            WritePickEvents(c6Region);   // the picks' events block (format c6.s15events), whatever the sky switch
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            if (map == null || cam == null) return;
            MatrixD wm = cam.WorldMatrix;
            Vector3D p = wm.Translation;
            S15SkyFrame f = S15Sky.FrameAt(map, new Vec3(p.X, p.Y, p.Z), false);
            if (!S15Switch.SkyOn) f.Flags = 0; // sky off: an invalid frame, Subnautica shows its own moons again
            f.ListSeq = ListSeq;
            f.WorldEpoch = Phases.WorldEpoch;
            f.Tick = tick;
            f.BodyCount = (ushort)Bodies.Count;
            S15SkyWire.WriteFrame(c6Region, ref f);
            FrameWrites++;
            _lastFrame = f;
            if (_listWritten != ListSeq || tick >= _nextListWriteTick)
            {
                S15SkyWire.WriteList(c6Region, ListSeq, Phases.WorldEpoch, Bodies);
                _listWritten = ListSeq;
                _nextListWriteTick = tick + (ulong)S15Conventions.Sky.ListRefreshTicks;
            }
            Vector3D fwd = wm.Forward;
            double turned = _hasPrevFwd ? Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3D.Dot(fwd, _prevFwd)))) * 180.0 / Math.PI : 0.0;
            _prevFwd = fwd;
            _hasPrevFwd = true;
            if (tick % (ulong)S15Conventions.Sky.RowTicks == 0 && Bodies.Count > 0) SkyRow(tick, wm, turned, ref f);
        }

        private static void SkyRow(ulong tick, MatrixD wm, double turnedDeg, ref S15SkyFrame f)
        {
            SkyRows++;
            Vec3 c = SiteBinding.C;
            Vector3D cam = wm.Translation;
            Vec3 camRel = new Vec3(cam.X - c.X, cam.Y - c.Y, cam.Z - c.Z);
            Vector3D right = wm.Right, up = wm.Up, fwd = wm.Forward;
            StringBuilder sb = Head("sky");
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Int(sb, "listSeq", ListSeq);
            S5Rows.Bool(sb, "still", turnedDeg * 100.0 < S15Conventions.Oracle.StillCentiDeg);
            S5Rows.Num(sb, "turnedDeg", turnedDeg);
            S5Rows.Bool(sb, "skyOn", S15Switch.SkyOn);
            S5Rows.Str(sb, "mutants", S15SeMutants.Active);
            sb.Append(",\"bodies\":[");
            bool first = true;
            int idx = -1;
            foreach (S15Body b in Bodies)
            {
                idx++;   // the list index (Subnautica's disc index): two bodies may share a name (a test Moon and the world's)
                if (b.Bound) continue;
                Vec3 dir;
                double ang;
                if (!S15Sky.ViewSe(camRel, b.Rel, b.RadiusM, out dir, out ang)) continue;
                var d = new Vector3D(dir.X, dir.Y, dir.Z);
                Vec3 dc = new Vec3(Vector3D.Dot(d, right), Vector3D.Dot(d, up), Vector3D.Dot(d, fwd));
                Vec3 sn = f.Map(dir);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":"); JsonText.AppendString(sb, b.Name);
                sb.Append(",\"idx\":").Append(idx.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"cam\":["); S3Rows.Vec(sb, dc); sb.Append(']');
                sb.Append(",\"sn\":["); S3Rows.Vec(sb, sn); sb.Append(']');
                sb.Append(",\"angDeg\":").Append(S3Rows.G(ang * 180.0 / Math.PI));
                sb.Append(",\"distKm\":").Append(S3Rows.G((b.Rel - camRel).Length / 1000.0)).Append('}');
            }
            sb.Append(']');
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ======================================================================================================== B: the water material

 /// <summary>One attempt of the bind-time water material: done when it reads back as ours or is refused for good.</summary>
        private static void WaterStep(ulong tick)
        {
            _waterTries++;
            _waterNextTick = tick + (ulong)WaterRetryTicks;
            string refused = WaterRefusal();
            if (refused != null) { _waterDone = true; ApplyWater("site bound"); return; }   // one row, one log line: the refusal
            if (WaterIsTarget())
            {
                _waterDone = true;
                if (_waterTries == 1) ApplyWater("site bound");   // already ours (a world spawned with the WaterConfig texture): one row
                return;
            }
            Dictionary<string, object> r = TryWater("site bound" + (_waterTries > 1 ? " (try " + _waterTries + ")" : ""), false);
            if (r != null && r.ContainsKey("taken") && (bool)r["taken"]) { _waterDone = true; return; }
            if (_waterTries >= WaterMaxTries)
            {
                _waterDone = true;
                TryWater("site bound (gave up after " + _waterTries + " tries)", true);
                DeLog.Info("DE.S15 water: the Water Mod never took " + S15Conventions.Water.Material + " in " + _waterTries * WaterRetryTicks / 60 + " s: the sea keeps " +
                           (WaterTexture() ?? "its texture") + " (/de s15 water apply)");
            }
            else if (_waterTries == 1) DeLog.Info("DE.S15 water: not taken yet (" + (r != null && r.ContainsKey("why") ? r["why"] : "?") + "); retried every " + WaterRetryTicks / 60 + " s");
        }

        /// <summary>A refusal for good (the switch, S15.M4, the material not loaded), or null.</summary>
        private static string WaterRefusal()
        {
            if (!S15Switch.WaterOn) return "off (de.s15 water off / " + S15Switch.EnvVar + ")";
            if (!S15SeMutants.WaterOff && !VRageRender.MyTransparentMaterials.ContainsMaterial(MyStringId.GetOrCompute(S15Conventions.Water.Material)))
                return "MISSING TransparentMaterial " + S15Conventions.Water.Material + " (deploy the world mod DeepEngineers.World with Data/TransparentMaterials.sbc)";
            return null;
        }

        public static Dictionary<string, object> ApplyWater(string via) { return TryWater(via, true); }

        /// <summary>
 /// de.water texture DE_4546B_Water through WaterLook; ok = the Water Mod's texture reads back as ours. A row when
 /// <paramref name="rowAlways"/> or on success (the retries before stay one log line).
 /// </summary>
        private static Dictionary<string, object> TryWater(string via, bool rowAlways)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            string why = WaterRefusal();
            bool applied = false;
            string before, after, transparent, material, wantTex, wantTr, wantMat;
            bool taken = false;
            WaterTarget(out wantTex, out wantTr, out wantMat);
            ReadWater(out before, out transparent, out material);
            after = before;
            if (why == null)
            {

                // Mod material DE_4546B_Sea (Reflectivity 0.5: run 20261024-063156, the near water at the switch); under S15.M4 the
 // previous look (JWater2, transparent on, the mod's own Water material) so the captures go red
                Dictionary<string, object> r = WaterLook.Command(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "texture", wantTex }, { "transparent", wantTr }, { "material", wantMat },
                }, "s15 " + via);
                object ok;
                bool cmdOk = r != null && r.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                ReadWater(out after, out transparent, out material);
                taken = cmdOk && after == wantTex && transparent == wantTr && material == wantMat;
                applied = taken && !S15SeMutants.WaterOff;
                if (!taken) why = !cmdOk && r != null && r.ContainsKey("line") ? Convert.ToString(r["line"], CultureInfo.InvariantCulture) :
                                  !cmdOk ? "de.water refused (the Water Mod not reachable yet?)" : "the Water Mod reads back texture " + (after ?? "nothing") + " transparent " + (transparent ?? "?") + " material " + (material ?? "?");
                else if (!applied) why = "S15.M4 (the previous look set: " + wantTex + ", transparent " + wantTr + ", material " + wantMat + ")";
            }
            WaterState = applied ? "texture " + after + ", transparent " + transparent + ", material " + material : "not applied: " + why;
            if (!taken && !rowAlways)
            {
                var q = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "taken", false }, { "texture", after }, { "material", material }, { "before", before }, { "why", why } };
                return q;
            }
            StringBuilder sb = Head("water");
            S5Rows.Str(sb, "material", S15Conventions.Water.Material);
            S5Rows.Bool(sb, "applied", applied);
            S5Rows.Str(sb, "before", before);
            S5Rows.Str(sb, "texture", after);
            S5Rows.Str(sb, "transparent", transparent);
            S5Rows.Str(sb, "seaMaterial", material);
            S5Rows.Str(sb, "why", why);
            S5Rows.Str(sb, "via", via);
            S5Rows.Str(sb, "mutants", S15SeMutants.Active);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S15 water " + WaterState + " (before " + before + ", " + via + ")");
            d["ok"] = applied;
            d["taken"] = taken;
            d["texture"] = after;
            d["transparent"] = transparent;
            d["material"] = material;
            d["before"] = before;
            if (why != null) d["why"] = why;
            return d;
        }

        /// <summary>The Water Mod's texture of the bound planet now, or null.</summary>
        public static string WaterTexture()
        {
            string t, tr, m;
            ReadWater(out t, out tr, out m);
            return t;
        }

        /// <summary>
 /// The look the bind applies: ours (the texture, Transparent off, the material DE_4546B_Sea), or under S15.M4 the previous one
 /// </summary>
        private static void WaterTarget(out string texture, out string transparent, out string material)
        {
            bool m4 = S15SeMutants.WaterOff;
            texture = m4 ? S15Conventions.Water.Previous : S15Conventions.Water.Material;
            transparent = (m4 || S15Conventions.Water.Transparent) ? "on" : "off";
            material = m4 ? S15Conventions.WaterFar.PlainMaterial : S15Conventions.WaterFar.SeaMaterial;
        }

        /// <summary>The bind's look reads back (its texture, transparent and material).</summary>
        private static bool WaterIsTarget()
        {
            string t, tr, m, wt, wtr, wm;
            ReadWater(out t, out tr, out m);
            WaterTarget(out wt, out wtr, out wm);
            return t == wt && tr == wtr && m == wm;
        }

        /// <summary>The Water Mod's texture, transparent and material of the bound planet now, null when unread.</summary>
        private static void ReadWater(out string texture, out string transparent, out string material)
        {
            texture = transparent = material = null;
            try
            {
                Dictionary<string, object> r = WaterLook.Command(new Dictionary<string, object>(StringComparer.Ordinal), "s15 read");
                object st;
                if (r == null || !r.TryGetValue("state", out st)) return;
                string s = Convert.ToString(st, CultureInfo.InvariantCulture);
                texture = Token(s, "texture=");
                transparent = Token(s, "transparent=");
                material = Token(s, "material=");
            }
            catch (Exception) { }
        }

        private static string Token(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int j = s.IndexOf(' ', i);
            return j < 0 ? s.Substring(i + key.Length) : s.Substring(i + key.Length, j - i - key.Length);
        }

        // ======================================================================================================== status

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "active", _active }, { "inactive", InactiveWhy }, { "aboard", _aboard.ToString(CultureInfo.InvariantCulture) }, { "how", _aboardHow },
                { "thrust", (double)_thrustMax }, { "powered", _powered }, { "switchOn", _switchOn }, { "reactors", _reactors }, { "reactorsWorking", _reactorsWorking },
                { "uraniumKg", _uraniumKg }, { "large", _large }, { "station", _station },
                { "events", Events }, { "played", Played }, { "cooldowns", Cooldowns }, { "seAudio", SeAudioSkips },
                { "playedBy", new Dictionary<string, object>(PlayedBy, StringComparer.Ordinal) }, { "live", _liveLast },
                { "seq", new List<object> { (long)S15SeWire.WelcomeSeq, (long)S15SeWire.PowerUpSeq, (long)S15SeWire.PowerDownSeq, (long)S15SeWire.SmallWelcomeSeq } },
                { "listSeq", (long)ListSeq }, { "bodies", Bodies.Count }, { "frameWrites", FrameWrites }, { "frameTick", (long)_lastFrame.Tick }, { "c6Errors", S15SeWire.C6Errors },
                { "water", WaterState }, { "voiceOn", S15Switch.VoiceOn }, { "skyOn", S15Switch.SkyOn }, { "waterOn", S15Switch.WaterOn },
                { "mutants", S15SeMutants.Active }, { "hash", "0x" + S15Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) },
                { "errors", Errors },
            };
            var names = new List<object>();
            foreach (S15Body b in Bodies) names.Add(b.Name + (b.Bound ? " (bound)" : ""));
            d["bodyNames"] = names;
            return d;
        }

        public static MyCubeGrid AboardGrid { get { return _aboardGrid; } }

        private static StringBuilder Head(string name)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s15." + name);
            return sb;
        }
    }
}
