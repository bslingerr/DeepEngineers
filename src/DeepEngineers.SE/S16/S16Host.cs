using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S16 depth progression, the SE side (ROADMAP §14 "### S16"; CONTRACT §5.4 "S16 conventions"). Game thread, every
 /// plugin update in a world (Plugin.UpdateCore, S16 hook), only while the activation gate is open and the site's planet
 /// and the Water Mod API are bound (SiteBinding):
 /// <list type="bullet">
 /// <item><b>Grids.</b> Every 120 ticks the grids within 1 km above the sea
 /// are tracked. Each one's lowest point is the deepest of its local box's eight corners, its depth
 /// <c>−WaterModAPI.GetDepth</c> there (SiteBinding.Api; the geometric Rs − |P − C| when the API answers nothing): every
 /// tick for the HUD grid, every 30 ticks for the others; a grid 2 km above the sea is dropped until it comes back.</item>
 /// <item><b>Crush depth.</b> A grid in the water is scanned (budget 1,500 blocks per tick over all grids) when its block
 /// count changed, when its gas system finished a recompute, or every 600 ticks: per block the class
 /// the tier of a functional upgrade block, the
 /// exposure over the box <c>Min..Max</c> with the neighbours' classes
 /// and the S7 air rule
 /// a counted Fitting is rated as the armour it touches.
 /// The scan also keeps the blocks with a wet face and the batteries
 /// A scan never runs while the
 /// gas system is recomputing; the last summary stays.</item>
 /// <item><b>Groups.</b> Every 60 ticks: a grid's limit is raised by the best tier of its mechanical group
 /// a grid linked by a connector or landing gear (<c>Physical</c>, <c>NoContactDamage</c>) to a
 /// larger grid outside its mechanical group is docked: its limit is at least the carrier's, and it takes no damage while
 /// every block centre lies in the carrier's airtight room (<c>MyCubeGrid.WorldToGridInteger</c>).</item>
 /// <item><b>Damage.</b> Every 60 ticks per grid (the next tick set first; the first period 60 + id mod 60 ticks after the
 /// grid is tracked, so a split-off grid never repeats its parent's period and the grids are out of phase), none during the
 /// 60 s grace after S16 becomes active (a HUD notice names the grids past their limit): the eight deepest damage-set
 /// blocks whose centre is past the limit (allocation-free <c>S16TopK</c>)
 /// take <c>MySlimBlock.DoDamage(amount, DeepPressure, true, null, 0)</c>, SE's own block damage path;
 /// never a health write. Only with destructible blocks, whatever the game mode
 /// the S16 damage switch on, outside the terrain fail-safe's footprint. The Water Mod's own
 /// CrushDamage stays 0 on this planet.</item>
 /// <item><b>HUD.</b> The HUD grid is the seated player's grid
 /// else the grid whose box holds the character; no grid and the eye under water = the diver. One SE
 /// HUD line every 30 ticks, hidden while Subnautica shows the
 /// same values in its own HUD (the C7 S16 flags of a fresh SnStatus: HUD A/B); the creak cadence, the voice arming and
 /// rule run on it, and the C5 extension (S16SeWire) carries the values and the event counters to Subnautica.</item>
 /// <item><b>Rows</b> s16.site, s16.grace, s16.grid (summary changes), s16.group (group context changes), s16.depth, s16.damage,
 /// s16.creak, s16.voice, s16.hud (every 30 ticks while the HUD is on), s16.descend.</item>
 /// </list>
 /// A failure is caught, counted and logged at most every 10 s; it never reaches SE.
 /// </summary>
    internal static class S16Host
    {
        private sealed class GridState
        {
            public MyCubeGrid Grid;
            public long Id;
            public int Size;
            public string Label;
            // summary
            public bool HasSummary;
            public S16GridSummary Summary;
            public int Crush, Hull, Weakest;
            public List<MySlimBlock> Counted = new List<MySlimBlock>();
            public List<MySlimBlock> Wet = new List<MySlimBlock>();
            public List<MyBatteryBlock> Batteries = new List<MyBatteryBlock>();
            public double HullMax;
            // group context
            public int GroupTier, Effective, CarrierCrush;
            public long CarrierId;
            public bool Docked, Exempt, GroupKnown;
            public string GroupKey;
            // scan in progress
            public List<MySlimBlock> ScanList;
            public int ScanIndex;
            public S16GridSummary ScanSum;
            public List<MySlimBlock> ScanCounted, ScanWet;
            public List<MyBatteryBlock> ScanBatteries;
            public long ScanStartTs;
            public bool Dirty = true;
            public int LastBlockCount = -1;
            public bool GasBusyLast;
            public ulong NextRescanTick;
            public Func<int, int, int, int> BlockAt;
            public Func<int, int, int, bool> Airtight;
            // depth
            public ulong NextDepthTick, NextDepthRowTick, NextDamageTick;
            public double LowestDepth = double.NaN, CentreDepth = double.NaN;
            public Vector3D Lowest;
            public readonly Vector3D[] Corners = new Vector3D[8];
            public bool DepthFromApi;
            // counters
            public long DamageTicks, DamagedBlocks, DestroyedBlocks;
            public double DamageSe;
        }

        private sealed class DescendJob
        {
            public long Id;
            public double Rate, To, StartDepth = double.NaN;
            public bool Holding;
            public string Label;
            public ulong LastTick, StartTick;
            public Vector3D Hold;
            public long Steps;
        }

        private static readonly Dictionary<long, GridState> Grids = new Dictionary<long, GridState>();
        private static readonly List<GridState> Order = new List<GridState>();
        private static readonly Dictionary<long, DescendJob> Jobs = new Dictionary<long, DescendJob>();
        private static readonly Dictionary<MyCubeBlockDefinition, int> ClassCache = new Dictionary<MyCubeBlockDefinition, int>();
        private static readonly List<long> Gone = new List<long>();
        private static readonly List<MyCubeGrid> GroupScratch = new List<MyCubeGrid>();
        private static readonly HashSet<long> MechScratch = new HashSet<long>();
        private static readonly S16TopK TopK = new S16TopK();
        private static readonly MyStringHash PressureType = MyStringHash.GetOrCompute(S16Conventions.Damage.SeType);
        private static readonly RateLimit ErrorLog = new RateLimit(10000);
        private static readonly HashSet<string> Once = new HashSet<string>(StringComparer.Ordinal);

        private static bool _enabledRead, _enabled = true, _active, _siteRow;
        /// <summary>de.s16 damage on|off (dev); default on.</summary>
        public static volatile bool DamageSwitch = true;
        /// <summary>de.s16 watch (dev): this grid is the HUD grid (the scripted descents have no pilot).</summary>
        public static long WatchId;
        private static ulong _lastTick, _nextEnumTick, _nextGroupTick, _activeTick, _graceEnd;
        private static bool _graceNoticeDone, _graceEndDone;
        private static Vector3D _c;
        private static double _rs;

        // HUD grid / diver state (creaks and the voice follow the HUD grid; a change of grid resets them)
        private static long _hudId;
        private static readonly S16CreakClock Creak = new S16CreakClock();
        private static readonly S16VoiceArm Arm = new S16VoiceArm();
        private static readonly S16VoiceRule Voice = new S16VoiceRule();
        private static IMyHudNotification _note;
        private static bool _noteHidden;
        private static ulong _nextHudTick;

        public static long Scans, ScannedBlocks, DamageTicks, DamagedBlocks, Destroyed, Creaks, VoiceEvents, VoicePlays, HudRows, Errors, ApiMisses, SuppressedLines;
        public static double DamageSe, LastScanMs, MaxScanMs, LastDamageMs, MaxDamageMs;
        public static string InactiveWhy = "not started";
        public static bool DamageGateOpen { get; private set; }
        public static string DamageGateWhy { get; private set; }
        /// <summary><c>format damage.gate</c>: S16Conventions.DamageGate (destructible, indestructible, off, none).</summary>
        public static int DamageGate { get; private set; } = -1;

        public const int ScanBudgetBlocks = 1500;
        public const int EnumerateEveryTicks = 120;
        public const int RescanEveryTicks = 600;
        public const int GraceNoticeTicks = 300;
        public const int DockSampleBlocks = 512;
        public const double TrackAboveSeaM = 1000.0;

        public static bool Enabled { get { return _enabled; } }
        public static int Tracked { get { return Grids.Count; } }
        public static bool InGrace { get { return _active && _lastTick < _graceEnd; } }
        public static double GraceLeftS { get { return InGrace ? (_graceEnd - _lastTick) / 60.0 : 0.0; } }

        // ======================================================================================================== tick

        public static void Tick(ulong nowMs, bool inWorld)
        {
            try { TickCore(nowMs, inWorld); }
            catch (Exception e)
            {
                Errors++;
                if (ErrorLog.Allow(nowMs)) DeLog.Info("DE.Error S16 tick " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        private static void TickCore(ulong nowMs, bool inWorld)
        {
            if (!inWorld)
            {
                if (_active || Grids.Count > 0) Reset("world gone");
                S16SeWire.Off();
                return;
            }
            if (!_enabledRead)
            {
                _enabledRead = true;
                _enabled = S16Switch.EnabledFromEnvironment();
                DeLog.Info("DE.S16 " + (_enabled ? "on" : "OFF (" + S16Switch.EnvVar + "=off)") + ": conventions 0x" + S16Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) +
                           " (" + S16Conventions.Header + "), the Water Mod's CrushDamage stays 0 (the plugin applies the ladder)");
            }
            if (!_enabled) { Inactive("DEEPENGINEERS_S16=off"); return; }
            if (!ActivationGate.IsOpen) { Inactive("activation gate " + ActivationGate.State); return; }
            MyPlanet planet = SiteBinding.Planet;
            WaterModClient api = SiteBinding.Api;
            if (planet == null || api == null || !api.Usable || !(SiteBinding.Rs > 0)) { Inactive("no bound DE_4546B / Water Mod API"); return; }
            ulong tick = GameState.GameplayTick;
            if (tick == _lastTick && _active) return; // paused or the same simulation tick: nothing new
            _lastTick = tick;
            Vec3 c = SiteBinding.C;
            _c = new Vector3D(c.X, c.Y, c.Z);
            _rs = SiteBinding.Rs;
            if (!_active)
            {
                _active = true;
                InactiveWhy = null;
                _activeTick = tick;
                _graceEnd = tick + (ulong)S16Conventions.Damage.GraceTicks;
                _graceNoticeDone = _graceEndDone = false;
                DeLog.Info("DE.S16 ACTIVE planet " + planet.EntityId + " Rs " + SiteBinding.Rs.ToString("R", CultureInfo.InvariantCulture) +
                           "; no crush damage for " + S16Conventions.Damage.GraceTicks / 60 + " s (load grace)");
                GraceRow("start", 0);
            }
            if (!_siteRow)
            {
                _siteRow = true;
                var sb = Head("site");
                S5Rows.Doubles(sb, "c", new[] { _c.X, _c.Y, _c.Z }, 0, 3);
                S5Rows.Num(sb, "rs", _rs);
                S5Rows.Id(sb, "planet", planet.EntityId);
                S5Rows.Str(sb, "hash", "0x" + S16Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture));
                S5Rows.Str(sb, "mutants", S16SeMutants.Active);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }

            UpdateDamageGate();
            if (tick >= _nextEnumTick) { Enumerate(); _nextEnumTick = tick + EnumerateEveryTicks; }

            GridState hud = HudGrid();
            StepJobs(tick);

            Gone.Clear();
            foreach (GridState gs in Order)
            {
                MyCubeGrid g = gs.Grid;
                if (g == null || g.Closed || g.MarkedForClose) { Gone.Add(gs.Id); continue; }
                if (g.IsStatic && gs.Id != WatchId) { Gone.Add(gs.Id); continue; } // converted to a station: no crush depth any more
                if (gs == hud || tick >= gs.NextDepthTick || Jobs.ContainsKey(gs.Id)) // a descending grid's depth every tick (its stop at To)
                {
                    UpdateDepth(gs, api, planet);
                    gs.NextDepthTick = tick + 30;
                    if (gs.LowestDepth < -2 * TrackAboveSeaM && gs != hud && !Jobs.ContainsKey(gs.Id)) { Gone.Add(gs.Id); continue; } // left the sea: tracked again when it comes back
                }
                bool wet = gs.LowestDepth > 0 || gs == hud;
                if (wet) MarkDirtyIfChanged(gs, tick);
                if (tick >= gs.NextDepthRowTick && gs.HasSummary && wet && (gs == hud || Jobs.ContainsKey(gs.Id) || gs.LowestDepth >= gs.Effective - 60))
                {
                    gs.NextDepthRowTick = tick + 60;
                    DepthRow(gs, tick);
                }
            }
            foreach (long id in Gone) Forget(id);

            ScanStep();
            if (tick >= _nextGroupTick) { GroupStep(tick); _nextGroupTick = tick + (ulong)S16Conventions.Damage.PeriodTicks; }

            long t0 = Stopwatch.GetTimestamp();
            foreach (GridState gs in Order)
                if (tick >= gs.NextDamageTick) { gs.NextDamageTick = tick + (ulong)S16Conventions.Damage.PeriodTicks; DamageStep(gs, tick, gs == hud); }
            double dms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            LastDamageMs = dms;
            if (dms > MaxDamageMs) MaxDamageMs = dms;

            GraceStep(tick);
            HudStep(hud, tick);
        }

        private static void Inactive(string why)
        {
            if (_active) Reset(why);
            if (InactiveWhy != why)
            {
                InactiveWhy = why;
                DeLog.Info("DE.S16 inactive: " + why);
            }
            S16SeWire.Off();
        }

        private static void Reset(string why)
        {
            Grids.Clear();
            Order.Clear();
            Jobs.Clear();
            ClassCache.Clear();
            _active = false;
            _siteRow = false;
            _hudId = 0;
            WatchId = 0;
            _lastTick = _nextEnumTick = _nextHudTick = _nextGroupTick = _activeTick = _graceEnd = 0; // a new world's ticks start low again
            Creak.Reset();
            Arm.Reset();
            Voice.Clear();
            Once.Clear();
            try { if (_note != null) _note.Hide(); } catch (Exception) { }
            _note = null;
            DeLog.Info("DE.S16 reset (" + why + ")");
        }

        private static void Forget(long id)
        {
            GridState gs;
            if (!Grids.TryGetValue(id, out gs)) return;
            Grids.Remove(id);
            Order.Remove(gs);
            Jobs.Remove(id);
            if (WatchId == id) WatchId = 0;
        }

        // =================================================================================================== grids

        private static void Enumerate()
        {
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var g = e as MyCubeGrid;
                if (g == null || g.Closed || g.MarkedForClose || g.IsPreview || g.Physics == null) continue;
                if (g.IsStatic) continue;
                if (Grids.ContainsKey(g.EntityId)) continue;
                double r = (g.PositionComp.WorldAABB.Center - _c).Length();
                if (r > _rs + TrackAboveSeaM) continue;
                Track(g, null);
            }
        }

        private static GridState Track(MyCubeGrid g, string label)
        {
            GridState gs;
            if (Grids.TryGetValue(g.EntityId, out gs)) { if (label != null) gs.Label = label; return gs; }
            gs = new GridState { Grid = g, Id = g.EntityId, Size = g.GridSizeEnum == MyCubeSize.Large ? S16Conventions.Size.Large : S16Conventions.Size.Small, Label = label };
            // /7: the first damage period starts a full period (plus a per-grid phase) after tracking: a grid split off
 // a damaged one never repeats the parent's period, and the grids' periods do not all fall on one tick
            gs.NextDamageTick = _lastTick + (ulong)S16Conventions.Damage.PeriodTicks + (ulong)((g.EntityId & 0x7FFFFFFF) % S16Conventions.Damage.PeriodTicks);
            MyCubeGrid gg = g;
            gs.BlockAt = (x, y, z) =>
            {
                MySlimBlock b = gg.GetCubeBlock(new Vector3I(x, y, z));
                return b == null ? 0 : ClassOfDef(b.BlockDefinition);
            };
            gs.Airtight = (x, y, z) =>
            {
                MyGridGasSystem gas = gg.GridSystems != null ? gg.GridSystems.GasSystem : null;
                if (gas == null) return false;
                var v = new Vector3I(x, y, z);
                MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref v);
                return room != null && room.IsAirtight;
            };
            Grids[g.EntityId] = gs;
            Order.Add(gs);
            return gs;
        }

        private static int ClassOfDef(MyCubeBlockDefinition def)
        {
            if (def == null) return S16Conventions.BlockClass.Fitting;
            int c;
            if (ClassCache.TryGetValue(def, out c)) return c;
            c = S16Classify.ClassOf(def.Id.TypeId.ToString(), def.Id.SubtypeName, def.EdgeType);
            ClassCache[def] = c;
            return c;
        }

        private static void UpdateDepth(GridState gs, WaterModClient api, MyPlanet planet)
        {
            MyCubeGrid g = gs.Grid;
            BoundingBox lb = g.PositionComp.LocalAABB;
            MatrixD wm = g.WorldMatrix;
            double best = double.MaxValue;
            int bi = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3D((i & 1) != 0 ? lb.Max.X : lb.Min.X, (i & 2) != 0 ? lb.Max.Y : lb.Min.Y, (i & 4) != 0 ? lb.Max.Z : lb.Min.Z);
                Vector3D w = Vector3D.Transform(corner, wm);
                gs.Corners[i] = w;
                double r = (w - _c).Length();
                if (r < best) { best = r; bi = i; }
            }
            gs.Lowest = gs.Corners[bi];
            Vector3D centre = g.PositionComp.WorldAABB.Center;
            gs.CentreDepth = _rs - (centre - _c).Length();
            Vector3D at = S16SeMutants.CentreDepth ? centre : gs.Lowest;
            float? y = api.Depth(at, planet);
            if (y.HasValue && !float.IsNaN(y.Value)) { gs.LowestDepth = -y.Value; gs.DepthFromApi = true; }
            else { gs.LowestDepth = _rs - (at - _c).Length(); gs.DepthFromApi = false; ApiMisses++; }
        }

        private static void MarkDirtyIfChanged(GridState gs, ulong tick)
        {
            MyCubeGrid g = gs.Grid;
            int n = g.BlocksCount;
            if (n != gs.LastBlockCount) { gs.LastBlockCount = n; gs.Dirty = true; }
            MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
            bool busy = gas != null && ((IMyGridGasSystem)gas).IsProcessingData;
            if (gs.GasBusyLast && !busy) gs.Dirty = true; // the rooms were just recomputed
            gs.GasBusyLast = busy;
            if (tick >= gs.NextRescanTick) { gs.NextRescanTick = tick + RescanEveryTicks; gs.Dirty = true; }
        }

        private static void ScanStep()
        {
            int budget = ScanBudgetBlocks;
            foreach (GridState gs in Order)
            {
                if (budget <= 0) break;
                MyCubeGrid g = gs.Grid;
                if (g == null || g.Closed) continue;
                MyGridGasSystem gas = g.GridSystems != null ? g.GridSystems.GasSystem : null;
                bool busy = gas != null && ((IMyGridGasSystem)gas).IsProcessingData;
                if (gs.ScanList == null)
                {
                    if (!gs.Dirty || busy) continue;
                    if (!(gs.LowestDepth > 0) && gs.Id != _hudId && !Jobs.ContainsKey(gs.Id)) continue; // dry grids keep their last summary
                    gs.Dirty = false;
                    gs.ScanList = new List<MySlimBlock>(g.GetBlocks());
                    gs.ScanIndex = 0;
                    gs.ScanSum = new S16GridSummary { Size = gs.Size };
                    gs.ScanCounted = new List<MySlimBlock>();
                    gs.ScanWet = new List<MySlimBlock>();
                    gs.ScanBatteries = new List<MyBatteryBlock>();
                    gs.ScanStartTs = Stopwatch.GetTimestamp();
                }
                if (busy) { gs.ScanList = null; gs.Dirty = true; continue; } // rooms are being rebuilt: start again later
                while (gs.ScanIndex < gs.ScanList.Count && budget > 0)
                {
                    MySlimBlock b = gs.ScanList[gs.ScanIndex++];
                    budget--;
                    ScanBlock(gs, b);
                }
                if (gs.ScanIndex >= gs.ScanList.Count) FinishScan(gs);
            }
        }

        private static void ScanBlock(GridState gs, MySlimBlock b)
        {
            if (b == null || b.CubeGrid != gs.Grid || b.IsDestroyed) return;
            MyCubeBlockDefinition def = b.BlockDefinition;
            if (def == null) return;
            ScannedBlocks++;
            gs.ScanSum.Blocks++;
            var bat = b.FatBlock as MyBatteryBlock;
            if (bat != null) gs.ScanBatteries.Add(bat);
            int cls = ClassOfDef(def);
            int tier = S16Classify.TierOf(def.Id.SubtypeName);
            if (tier > 0 && !S16SeMutants.NoTiers && b.ComponentStack != null && b.ComponentStack.IsFunctional && tier > gs.ScanSum.Tier) gs.ScanSum.Tier = tier;
            int ln, hn;
            int e = S16Exposure.Of(new S16Cell(b.Min.X, b.Min.Y, b.Min.Z), new S16Cell(b.Max.X, b.Max.Y, b.Max.Z), gs.BlockAt, gs.Airtight,
                                   cls == S16Conventions.BlockClass.Fitting, out ln, out hn);
            if ((e & S16Exposure.Wet) != 0) { gs.ScanSum.Wet++; gs.ScanWet.Add(b); }
            if (S16SeMutants.CountsNothing || !S16Exposure.Counts(cls, e)) return; // S16.M8: the exposure counts nothing
            if (S16Exposure.RatedClass(cls, ln, hn) == S16Conventions.BlockClass.HeavyArmor) gs.ScanSum.Heavy++;
            else gs.ScanSum.Light++;
            if (cls == S16Conventions.BlockClass.Fitting) gs.ScanSum.Fitting++;
            gs.ScanCounted.Add(b);
        }

        private static void FinishScan(GridState gs)
        {
            double ms = (Stopwatch.GetTimestamp() - gs.ScanStartTs) * 1000.0 / Stopwatch.Frequency;
            LastScanMs = ms;
            if (ms > MaxScanMs) MaxScanMs = ms;
            Scans++;
            S16GridSummary s = gs.ScanSum;
            int weakest;
            int hull = S16Ladder.HullOf(s, out weakest);
            int crush = S16Ladder.CrushDepthOf(s);
            bool changed = !gs.HasSummary || crush != gs.Crush || s.Light != gs.Summary.Light || s.Heavy != gs.Summary.Heavy || s.Fitting != gs.Summary.Fitting ||
                           s.Tier != gs.Summary.Tier || s.Blocks != gs.Summary.Blocks || s.Wet != gs.Summary.Wet;
            gs.Summary = s;
            gs.Hull = hull;
            gs.Weakest = weakest;
            gs.Crush = crush;
            if (!gs.GroupKnown) gs.Effective = crush;
            gs.Counted = gs.ScanCounted;
            gs.Wet = gs.ScanWet;
            gs.Batteries = gs.ScanBatteries;
            double max = 0;
            foreach (MySlimBlock b in DamageSet(gs)) max += b.MaxIntegrity;
            if (max > gs.HullMax) gs.HullMax = max;
            gs.HasSummary = true;
            gs.ScanList = null;
            gs.ScanCounted = gs.ScanWet = null;
            gs.ScanBatteries = null;
            if (!changed) return;
            var sb = Head("grid");
            S5Rows.Id(sb, "grid", gs.Id);
            S5Rows.Str(sb, "label", gs.Label);
            S5Rows.Str(sb, "name", gs.Grid.DisplayName);
            S5Rows.Int(sb, "size", s.Size);
            S5Rows.Int(sb, "light", s.Light);
            S5Rows.Int(sb, "heavy", s.Heavy);
            S5Rows.Int(sb, "fitting", s.Fitting);
            S5Rows.Int(sb, "wet", s.Wet);
            S5Rows.Int(sb, "tier", s.Tier);
            S5Rows.Int(sb, "blocks", s.Blocks);
            S5Rows.Int(sb, "hull", hull);
            S5Rows.Str(sb, "weakest", S16Conventions.BlockClass.Name(weakest));
            S5Rows.Int(sb, "crush", crush);
            S5Rows.Bool(sb, "wetSet", S16Ladder.DamageSetIsWet(s));
            S5Rows.Num(sb, "scanMs", ms);
            S5Rows.Int(sb, "tick", (long)_lastTick);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S16 grid " + gs.Id + (gs.Label != null ? " (" + gs.Label + ")" : "") + " crush depth " + crush + " m: " + s +
                       " -> hull " + hull + " m (" + S16Conventions.BlockClass.Name(weakest) + ")" + (s.Tier > 0 ? ", tier " + s.Tier : "") +
                       (S16Ladder.DamageSetIsWet(s) ? "; nothing counts: its " + s.Wet + " wet-faced block(s) take the damage" : ""));
        }

        /// <summary>The blocks that take crush damage (<c>format damage.block</c>): the counted ones, or the wet-faced ones when none counts.</summary>
        private static List<MySlimBlock> DamageSet(GridState gs)
        {
            if (gs.Counted.Count > 0 || S16SeMutants.NoWetFallback) return gs.Counted; // S16.M10: no fallback set
            return gs.Wet;
        }

        private static void DepthRow(GridState gs, ulong tick)
        {
            var sb = Head("depth");
            S5Rows.Id(sb, "grid", gs.Id);
            S5Rows.Str(sb, "label", gs.Label);
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Num(sb, "depth", gs.LowestDepth);
            S5Rows.Num(sb, "centreDepth", gs.CentreDepth);
            S5Rows.Int(sb, "crush", gs.Effective);
            S5Rows.Int(sb, "own", gs.Crush);
            S5Rows.Int(sb, "tier", gs.Summary.Tier);
            S5Rows.Int(sb, "blocks", gs.Grid.BlocksCount);
            S5Rows.Bool(sb, "api", gs.DepthFromApi);
            DescendJob j;
            if (Jobs.TryGetValue(gs.Id, out j)) { S5Rows.Str(sb, "job", j.Holding ? "hold" : "descend"); S5Rows.Num(sb, "to", j.To); }
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // =================================================================================================== groups

        private static void GroupStep(ulong tick)
        {
            MyCubeGridGroups groups = MyCubeGridGroups.Static;
            if (groups == null) return;
            // pass 1: the best tier of each grid's mechanical group (rotors, hinges, pistons)
            foreach (GridState gs in Order)
            {
                if (!gs.HasSummary || gs.Grid == null || gs.Grid.Closed) continue;
                int best = gs.Summary.Tier;
                GroupScratch.Clear();
                groups.Mechanical.GetGroupNodes(gs.Grid, GroupScratch);
                foreach (MyCubeGrid n in GroupScratch)
                {
                    GridState o;
                    if (n != null && Grids.TryGetValue(n.EntityId, out o) && o.HasSummary && o.Summary.Tier > best) best = o.Summary.Tier;
                }
                gs.GroupTier = best;
            }
            // pass 2: docking (connector, landing gear) to a larger grid outside the mechanical group
            foreach (GridState gs in Order)
            {
                if (!gs.HasSummary || gs.Grid == null || gs.Grid.Closed) continue;
                MyCubeGrid g = gs.Grid;
                MechScratch.Clear();
                GroupScratch.Clear();
                groups.Mechanical.GetGroupNodes(g, GroupScratch);
                foreach (MyCubeGrid n in GroupScratch) if (n != null) MechScratch.Add(n.EntityId);
                MechScratch.Add(g.EntityId);
                GridState carrier = null;
                GroupScratch.Clear();
                groups.Physical.GetGroupNodes(g, GroupScratch);
                int nPhys = GroupScratch.Count;
                groups.NoContactDamage.GetGroupNodes(g, GroupScratch);
                for (int i = 0; i < GroupScratch.Count; i++)
                {
                    MyCubeGrid n = GroupScratch[i];
                    if (n == null || MechScratch.Contains(n.EntityId) || n.Closed) continue;
                    GridState o;
                    if (!Grids.TryGetValue(n.EntityId, out o) || !o.HasSummary) continue;
                    if (n.BlocksCount <= g.BlocksCount) continue;
                    if (carrier == null || n.BlocksCount > carrier.Grid.BlocksCount) carrier = o;
                }
                bool docked = carrier != null;
                int carrierCrush = 0;
                bool inside = false;
                if (docked)
                {
                    carrierCrush = Math.Max(carrier.Crush, S16Ladder.TierCrushM(carrier.GroupTier));
                    inside = InsideCarrierRoom(g, carrier.Grid, gs.Exempt);
                }
                int own = gs.Crush;
                int eff;
                bool exempt;
                if (S16SeMutants.NoGroups) { eff = own; exempt = false; } // S16.M9: group and dock ignored
                else { eff = S16Ladder.EffectiveCrush(own, gs.GroupTier, docked, carrierCrush); exempt = !S16Ladder.DamageApplies(docked, inside); }
                gs.Docked = docked;
                gs.CarrierId = docked ? carrier.Id : 0;
                gs.CarrierCrush = carrierCrush;
                gs.Exempt = exempt;
                gs.Effective = eff;
                gs.GroupKnown = true;
                string key = own + "|" + gs.GroupTier + "|" + docked + "|" + gs.CarrierId + "|" + carrierCrush + "|" + exempt + "|" + eff;
                if (key == gs.GroupKey) continue;
                bool first = gs.GroupKey == null;
                gs.GroupKey = key;
                if (first && gs.GroupTier == gs.Summary.Tier && !docked) continue; // a lone grid: nothing to say
                var sb = Head("group");
                S5Rows.Id(sb, "grid", gs.Id);
                S5Rows.Str(sb, "label", gs.Label);
                S5Rows.Int(sb, "tick", (long)tick);
                S5Rows.Int(sb, "own", own);
                S5Rows.Int(sb, "tier", gs.Summary.Tier);
                S5Rows.Int(sb, "groupTier", gs.GroupTier);
                S5Rows.Bool(sb, "docked", docked);
                S5Rows.Id(sb, "carrier", gs.CarrierId);
                S5Rows.Int(sb, "carrierCrush", carrierCrush);
                S5Rows.Bool(sb, "inside", inside);
                S5Rows.Bool(sb, "exempt", exempt);
                S5Rows.Int(sb, "effective", eff);
                S5Rows.Int(sb, "physicalNodes", nPhys);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
                DeLog.Info("DE.S16 group grid " + gs.Id + ": own " + own + " m, mechanical group tier " + gs.GroupTier + (docked ? ", docked to " + gs.CarrierId + " (" + carrierCrush + " m)" +
                           (exempt ? " inside its airtight room: no crush damage" : "") : "") + " -> limit " + eff + " m");
            }
        }

        /// <summary>Every block centre of the docked grid in an airtight room of the carrier (sampled to 512 blocks); while the carrier recomputes its rooms the last answer stays.</summary>
        private static bool InsideCarrierRoom(MyCubeGrid g, MyCubeGrid carrier, bool last)
        {
            MyGridGasSystem gas = carrier.GridSystems != null ? carrier.GridSystems.GasSystem : null;
            if (gas == null) return false;
            if (((IMyGridGasSystem)gas).IsProcessingData) return last;
            HashSet<MySlimBlock> blocks = g.GetBlocks();
            int n = blocks.Count;
            if (n == 0) return false;
            int stride = Math.Max(1, n / DockSampleBlocks), i = 0;
            foreach (MySlimBlock b in blocks)
            {
                if (i++ % stride != 0) continue;
                Vector3D wc;
                b.ComputeWorldCenter(out wc);
                Vector3I cell = carrier.WorldToGridInteger(wc);
                MyOxygenRoom room = gas.GetOxygenRoomForCubeGridPosition(ref cell);
                if (room == null || !room.IsAirtight) return false;
            }
            return true;
        }

        // ================================================================================================== damage

 /// <summary>
 /// <c>format damage.
 /// metres past the alert"): the world's DestructibleBlocks
 /// whatever the game mode; de.s16 damage off overrides.
 /// </summary>
        private static void UpdateDamageGate()
        {
            MySession s = MySession.Static;
            int gate = S16DamageGateRule.Of(s != null, s != null && s.Settings.DestructibleBlocks, DamageSwitch);
            DamageGateOpen = S16DamageGateRule.Open(gate);
            DamageGateWhy = gate == S16Conventions.DamageGate.Destructible ? null :
                            gate == S16Conventions.DamageGate.Off ? "de.s16 damage off" :
                            gate == S16Conventions.DamageGate.Indestructible ? "DestructibleBlocks off" : "no session";
            if (gate != DamageGate)
            {
                DamageGate = gate;
                string mode = s != null ? s.Settings.GameMode.ToString() : "-";
                DeLog.Info("DE.S16 DAMAGE-" + (DamageGateOpen ? "ACTIVE (destructible blocks, " + mode + ")" : "INACTIVE " + DamageGateWhy + " (" + mode + ")") +
                           ": gate " + S16Conventions.DamageGate.Name(gate));
            }
        }

        private static void DamageStep(GridState gs, ulong tick, bool isHud)
        {
            if (!gs.HasSummary || !DamageGateOpen) return;
            if (tick < _graceEnd) return; //  the load grace
            int limit = gs.Effective;
            double threshold = S16SeMutants.DamageThreshold(limit);
            if (!(gs.LowestDepth > threshold)) return;
            if (gs.Exempt) { OnceLog("exempt" + gs.Id, "DE.S16 grid " + gs.Id + " docked inside the airtight room of " + gs.CarrierId + ": no crush damage"); return; }
            MyCubeGrid g = gs.Grid;
            if (!g.BlocksDestructionEnabled) { OnceLog("immune" + gs.Id, "DE.S16 DAMAGE-INACTIVE grid " + gs.Id + ": not destructible (immune or DestructibleBlocks off)"); return; }
            if (Terrain.TerrainFailSafe.Active)
            {
                Vec3 sn = SiteBinding.Map != null ? SiteBinding.Map.SeToSn(new Vec3(gs.Lowest.X, gs.Lowest.Y, gs.Lowest.Z)) : default(Vec3);
                if (SiteBinding.Map != null && Terrain.TerrainFailSafe.InFootprint(sn)) { OnceLog("failsafe" + gs.Id, "DE.S16 DAMAGE-INACTIVE grid " + gs.Id + ": terrain fail-safe footprint (CONTRACT §7)"); return; }
            }
            List<MySlimBlock> set = DamageSet(gs);
            bool wetSet = !ReferenceEquals(set, gs.Counted);
            //  one pass, no allocation, no sort: the K deepest past the threshold (S16TopK = S16Damage.Pick)
            TopK.Reset(threshold);
            for (int i = 0; i < set.Count; i++)
            {
                MySlimBlock b = set[i];
                if (b == null || b.CubeGrid != g || b.IsDestroyed) continue;
                Vector3D wc;
                b.ComputeWorldCenter(out wc);
                TopK.Offer(_rs - (wc - _c).Length(), i);
            }
            int picked = TopK.Count;
            if (picked == 0) return;
            gs.DamageTicks++;
            DamageTicks++;
            double k = S16Damage.K(gs.Size);
            bool any = false;
            for (int p = 0; p < picked; p++)
            {
                MySlimBlock b = set[TopK.Index(p)];
                if (g.GetCubeBlock(b.Position) != b) continue; // ground down or split off since the scan
                double d = TopK.Depth(p);
                double hps = S16SeMutants.PerSecond(d, limit);
                double amount = hps * (S16Conventions.Damage.PeriodTicks / 60.0) * k;
                double before = b.Integrity - b.AccumulatedDamage;
                b.DoDamage((float)amount, PressureType, true, null, 0L);
                double after = b.IsDestroyed ? 0.0 : b.Integrity - b.AccumulatedDamage;
                double applied = Math.Max(0.0, before - after);
                bool destroyed = b.IsDestroyed || b.CubeGrid != g || g.GetCubeBlock(b.Position) != b;
                if (applied > 0) any = true;
                gs.DamagedBlocks++;
                DamagedBlocks++;
                gs.DamageSe += applied;
                DamageSe += applied;
                if (destroyed) { gs.DestroyedBlocks++; Destroyed++; }
                var sb = Head("damage");
                S5Rows.Id(sb, "grid", gs.Id);
                S5Rows.Str(sb, "label", gs.Label);
                S5Rows.Int(sb, "tick", (long)tick);
                S5Rows.Str(sb, "block", b.Position.X.ToString(CultureInfo.InvariantCulture) + "," + b.Position.Y.ToString(CultureInfo.InvariantCulture) + "," + b.Position.Z.ToString(CultureInfo.InvariantCulture));
                S5Rows.Str(sb, "subtype", b.BlockDefinition != null ? b.BlockDefinition.Id.SubtypeName : null);
                S5Rows.Int(sb, "size", gs.Size);
                S5Rows.Num(sb, "depth", d);
                S5Rows.Int(sb, "crush", limit);
                S5Rows.Num(sb, "hps", hps);
                S5Rows.Num(sb, "k", k);
                S5Rows.Num(sb, "amount", amount);
                S5Rows.Num(sb, "applied", applied);
                S5Rows.Bool(sb, "destroyed", destroyed);
                S5Rows.Bool(sb, "wetSet", wetSet);
                S5Rows.Num(sb, "gridDepth", gs.LowestDepth);
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
            }
            if (any && gs.DamageTicks == 1)
                DeLog.Info("DE.S16 crush damage on grid " + gs.Id + (gs.Label != null ? " (" + gs.Label + ")" : "") + " at " + gs.LowestDepth.ToString("0.0", CultureInfo.InvariantCulture) + " m (limit " + limit + " m)" +
                           (wetSet ? " to its wet-faced blocks (nothing counts)" : ""));
            if (any && isHud) S16SeWire.DamageSeq++;
        }

        // ================================================================================================== grace

        private static void GraceStep(ulong tick)
        {
            if (!_graceNoticeDone && tick >= _activeTick + GraceNoticeTicks)
            {
                _graceNoticeDone = true;
                int past = 0;
                foreach (GridState gs in Order) if (gs.HasSummary && gs.LowestDepth > gs.Effective && !gs.Exempt) past++;
                GraceRow("notice", past);
                if (past > 0 && tick < _graceEnd && DamageGateOpen)
                {
                    string text = "DeepEngineers: pressure damage starts in " + ((_graceEnd - tick) / 60).ToString(CultureInfo.InvariantCulture) + " s: " + past +
                                  " grid(s) past their crush depth";
                    DeLog.Info("DE.S16 " + text);
                    try
                    {
                        IMyUtilities u = (IMyUtilities)MyAPIUtilities.Static;
                        if (u != null) u.ShowNotification(text, 10000, "Red");
                    }
                    catch (Exception e) { OnceLog("gracehud", "DE.S16 MISSING the grace notice (IMyUtilities.ShowNotification): " + e.GetType().Name); }
                }
            }
            if (!_graceEndDone && tick >= _graceEnd)
            {
                _graceEndDone = true;
                GraceRow("end", 0);
                DeLog.Info("DE.S16 load grace over: crush damage applies");
            }
        }

        private static void GraceRow(string phase, int past)
        {
            var sb = Head("grace");
            S5Rows.Str(sb, "phase", phase);
            S5Rows.Int(sb, "tick", (long)_lastTick);
            S5Rows.Int(sb, "endTick", (long)_graceEnd);
            S5Rows.Int(sb, "gridsPast", past);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ===================================================================================================== HUD

        private static GridState HudGrid()
        {
            GridState gs;
            if (WatchId != 0)
            {
                if (Grids.TryGetValue(WatchId, out gs)) return gs;
                MyEntity e;
                if (MyEntities.TryGetEntityById(WatchId, out e) && e is MyCubeGrid) return Track((MyCubeGrid)e, null);
                return null;
            }
            MySession s = MySession.Static;
            if (s == null) return null;
            var sc = s.ControlledEntity as MyShipController;
            if (sc != null && sc.CubeGrid != null) return sc.CubeGrid.IsStatic ? null : Track(sc.CubeGrid, null); // seated in a station: the diver's readout, no crush
            MyCharacter ch = s.LocalCharacter;
            if (ch == null || ch.IsDead) return null;
            Vector3D p = ch.PositionComp.WorldAABB.Center;
            GridState best = null;
            double bestD = double.MaxValue;
            foreach (GridState o in Order)
            {
                MyCubeGrid g = o.Grid;
                if (g == null || g.Closed || g.IsStatic) continue;
                if (g.PositionComp.WorldAABB.Contains(p) == ContainmentType.Disjoint) continue;
                Vector3D local = Vector3D.Transform(p, g.PositionComp.WorldMatrixNormalizedInv);
                if (g.PositionComp.LocalAABB.Contains((Vector3)local) == ContainmentType.Disjoint) continue;
                double d = (g.PositionComp.WorldAABB.Center - p).LengthSquared();
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        /// <summary>The C7 S16 flags of a fresh SnStatus (Subnautica's HUD state, HUD A/B), or -1 without one.</summary>
        private static int SnHudFlags()
        {
            Plugin pl = Plugin.Instance;
            Link l = pl != null ? pl.LinkForS1 : null;
            if (l == null || l.State != LinkState.Up || !l.HasSnStatus) return -1;
            SnStatus st = l.LastSnStatus;
            byte f;
            return S16Wire.TryReadStatus(ref st, out f) ? f : -1;
        }

        private static void HudStep(GridState hud, ulong tick)
        {
            uint epoch = Phases.WorldEpoch;
            long id = hud != null ? hud.Id : 0;
            if (id != _hudId)
            {
                _hudId = id;
                Creak.Reset();
                Arm.Reset();
            }
            bool grace = tick < _graceEnd;
            if (hud != null && hud.HasSummary && !double.IsNaN(hud.LowestDepth))
            {
                double depth = hud.LowestDepth;
                int crush = hud.Effective;
                int band = S16SeMutants.CreakBand;
                int level = S16Creak.Level(depth, crush, band);
                ulong prevTick = Creak.LastTick;
                int prevLevel = Creak.Active ? Creak.LastLevel : 0;
                S16CreakReason why = Creak.Step(tick, level);
                if (why != S16CreakReason.None)
                {
                    S16SeWire.CreakSeq++;
                    Creaks++;
                    var sb = Head("creak");
                    S5Rows.Id(sb, "grid", id);
                    S5Rows.Str(sb, "label", hud.Label);
                    S5Rows.Int(sb, "tick", (long)tick);
                    S5Rows.Int(sb, "level", level);
                    S5Rows.Str(sb, "reason", why.ToString().ToLowerInvariant());
                    S5Rows.Int(sb, "prevTick", (long)prevTick);
                    S5Rows.Int(sb, "prevLevel", prevLevel);
                    S5Rows.Num(sb, "depth", depth);
                    S5Rows.Int(sb, "crush", crush);
                    S5Rows.Int(sb, "seq", S16SeWire.CreakSeq);
                    Jsonl.WriteLineBuffered(S5Rows.End(sb));
                }
                long ms = (long)(tick * 1000UL / 60UL);
                bool crossing = Arm.Armed;
                bool ev = Arm.Step(depth, crush, ms);   // format voice.rule: the crossing, then every RepeatMs while past the limit
                if (S16SeMutants.VoiceRepeats && depth > crush && !ev && tick % (ulong)S16Conventions.Damage.PeriodTicks == 0) ev = true; // S16.M5: every second, no cooldown
                if (ev)
                {
                    VoiceEvents++;
                    bool play = S16SeMutants.VoiceRepeats || Voice.TryFire(id, ms);
                    if (play) { VoicePlays++; S16SeWire.VoiceSeq++; }
                    var sb = Head("voice");
                    S5Rows.Id(sb, "grid", id);
                    S5Rows.Str(sb, "label", hud.Label);
                    S5Rows.Int(sb, "tick", (long)tick);
                    S5Rows.Int(sb, "ms", ms);
                    S5Rows.Num(sb, "depth", depth);
                    S5Rows.Int(sb, "crush", crush);
                    S5Rows.Bool(sb, "played", play);
                    S5Rows.Str(sb, "reason", crossing ? "cross" : "repeat");
                    S5Rows.Int(sb, "seq", S16SeWire.VoiceSeq);
                    Jsonl.WriteLineBuffered(S5Rows.End(sb));
                    DeLog.Info("DE.S16 hull integrity critical: grid " + id + " at " + depth.ToString("0.0", CultureInfo.InvariantCulture) + " m past " + crush + " m" +
                               (crossing ? "" : " (repeat)") + (play ? "" : " (voice cooldown)"));
                }
                bool damageOn = DamageGateOpen && !hud.Exempt;
                int state = S16Hud.State(depth, crush, damageOn);
                int flags = S16Conventions.C5Flags.Grid | (damageOn ? S16Conventions.C5Flags.DamageOn : 0) | (hud.Docked ? S16Conventions.C5Flags.Docked : 0) |
                            (hud.Exempt ? S16Conventions.C5Flags.Exempt : 0) | (grace ? S16Conventions.C5Flags.Grace : 0);
                int hullPct = HullPct(hud), powerPct = PowerPct(hud);
                var w = new S16WireState
                {
                    Flags = (byte)flags,
                    CreakLevel = (byte)level,
                    HullCode = (byte)S16Ladder.HullCode(hud.Size, hud.Weakest),
                    Tier = (byte)Math.Max(hud.Summary.Tier, hud.GroupTier),
                    BreathX10 = 0,
                    HudState = (ushort)state,
                    DepthM = (float)depth,
                    CrushM = crush,
                    WorldEpoch = epoch,
                    GridId = id,
                    HullPct = (byte)hullPct,
                    PowerPct = (byte)powerPct,
                };
                S16SeWire.Set(w);
                if (state != S16Conventions.HudState.Off && tick >= _nextHudTick)
                {
                    _nextHudTick = tick + (ulong)S16Conventions.Hud.RefreshTicks;
                    int sn = SnHudFlags();
                    int line = S16Hud.SeLine(sn >= 0, sn < 0 ? 0 : sn, true);   // format hud.se: the grid line is the fallback
                    if (line == S16Conventions.SeLine.Suppressed) HideHud(true);
                    else ShowHud(S16Hud.GridLine(depth, crush, hud.Size, hud.Weakest, w.Tier, state, flags, (int)Math.Ceiling(GraceLeftS)), S16Hud.Font(state));
                    HudRow(tick, "grid", hud, depth, crush, state, level, 0, flags, hullPct, powerPct, line, line == S16Conventions.SeLine.Suppressed, sn);
                }
                return;
            }
            // the diver (no grid): depth = the eye, Subnautica's breathing rule, no crush depth
            MySession s = MySession.Static;
            MyCharacter ch = s != null ? s.LocalCharacter : null;
            // review of the orchestrator's 16:4x edit: seated in a station's cockpit HudGrid gives null (no crush depth), and the
 // controlled entity is the cockpit, not the character: the diver readout then too (format crush.depth, depth 9)
            var seat = s != null ? s.ControlledEntity as MyShipController : null;
            bool stationSeat = seat != null && seat.CubeGrid != null && seat.CubeGrid.IsStatic;
            if (hud == null && ch != null && !ch.IsDead && (s.ControlledEntity == ch || stationSeat))
            {
                Vector3D eye = ch.GetHeadMatrix(false).Translation;
                double depth = _rs - (eye - _c).Length();
                int state = depth < S16Conventions.Hud.MinDepthDm / 10.0 ? S16Conventions.HudState.Off : S16Conventions.HudState.Safe;
                var w = new S16WireState
                {
                    Flags = (byte)S16Conventions.C5Flags.Diver,
                    BreathX10 = (byte)S16Hud.BreathX10(Math.Max(0.0, depth)),
                    HudState = (ushort)state,
                    DepthM = (float)depth,
                    WorldEpoch = epoch,
                    HullPct = 0,
                    PowerPct = 255,
                };
                S16SeWire.Set(w);
                if (state != S16Conventions.HudState.Off && tick >= _nextHudTick)
                {
                    _nextHudTick = tick + (ulong)S16Conventions.Hud.RefreshTicks;
                    int sn = SnHudFlags();
                    // format hud.
 // under water Subnautica's compass shows the depth (the C5 values above still drive it), above water nothing
                    int line = S16Hud.SeLine(sn >= 0, sn < 0 ? 0 : sn, false);
                    HideHud(false);
                    HudRow(tick, "diver", null, depth, 0, state, 0, w.BreathX10, S16Conventions.C5Flags.Diver, 0, 255, line,
                           sn >= 0 && (sn & S16Conventions.C7Flags.DiverShown) != 0, sn);
                }
                return;
            }
            S16SeWire.SetIdle(epoch);
        }

        /// <summary><c>format hud.hull</c> over the damage set, against the largest total seen for this grid.</summary>
        private static int HullPct(GridState gs)
        {
            double cur = 0;
            foreach (MySlimBlock b in DamageSet(gs))
                if (b != null && !b.IsDestroyed && b.CubeGrid == gs.Grid) cur += Math.Max(0.0, b.Integrity - b.AccumulatedDamage);
            return S16Hud.HullPct(cur, gs.HullMax);
        }

        private static int PowerPct(GridState gs)
        {
            double stored = 0, cap = 0;
            foreach (MyBatteryBlock b in gs.Batteries)
            {
                if (b == null || b.Closed || b.CubeGrid != gs.Grid) continue;
                stored += b.CurrentStoredPower;
                cap += b.MaxStoredPower;
            }
            return S16Hud.PowerPct(stored, cap);
        }

        private static void ShowHud(string text, string font)
        {
            try
            {
                IMyUtilities u = (IMyUtilities)MyAPIUtilities.Static;
                if (u == null) return;
                if (_note == null) _note = u.CreateNotification(text, S16Conventions.Hud.AliveMs, font);
                _note.Text = text;
                _note.Font = font;
                _note.AliveTime = S16Conventions.Hud.AliveMs;
                _note.Show();
                _noteHidden = false;
            }
            catch (Exception e)
            {
                OnceLog("hud", "DE.S16 MISSING HUD notification (IMyUtilities.CreateNotification): " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
 /// The SE line goes away: Subnautica shows the values (HUD A/B; <paramref name="replaced"/>, counted) and the fallback
 /// comes back the moment it stops, or the diver, who has no SE line (format hud.se).
 /// </summary>
        private static void HideHud(bool replaced)
        {
            if (replaced) SuppressedLines++;
            if (_noteHidden) return;
            _noteHidden = true;
            try { if (_note != null) _note.Hide(); } catch (Exception) { }
        }

        private static void HudRow(ulong tick, string mode, GridState gs, double depth, int crush, int state, int level, int breath, int flags, int hullPct, int powerPct,
                                   int line, bool snShown, int snFlags)
        {
            HudRows++;
            var sb = Head("hud");
            S5Rows.Str(sb, "mode", mode);
            S5Rows.Int(sb, "tick", (long)tick);
            S5Rows.Num(sb, "depth", depth);
            S5Rows.Int(sb, "crush", crush);
            S5Rows.Str(sb, "state", S16Conventions.HudState.Name(state));
            S5Rows.Int(sb, "level", level);
            S5Rows.Int(sb, "breathX10", breath);
            S5Rows.Bool(sb, "damageOn", (flags & S16Conventions.C5Flags.DamageOn) != 0);
            S5Rows.Str(sb, "gate", S16Conventions.DamageGate.Name(DamageGate < 0 ? S16Conventions.DamageGate.None : DamageGate));
            S5Rows.Int(sb, "flags", flags);
            S5Rows.Int(sb, "hullPct", hullPct);
            S5Rows.Int(sb, "powerPct", powerPct);
            S5Rows.Str(sb, "line", S16Conventions.SeLine.Name(line));
            S5Rows.Bool(sb, "snHud", snShown);
            S5Rows.Int(sb, "snFlags", snFlags);
            if (gs != null)
            {
                S5Rows.Id(sb, "grid", gs.Id);
                S5Rows.Str(sb, "label", gs.Label);
                S5Rows.Int(sb, "tier", gs.Summary.Tier);
                S5Rows.Int(sb, "hullCode", S16Ladder.HullCode(gs.Size, gs.Weakest));
                var cs = new double[24];
                for (int i = 0; i < 8; i++) { cs[i * 3] = gs.Corners[i].X; cs[i * 3 + 1] = gs.Corners[i].Y; cs[i * 3 + 2] = gs.Corners[i].Z; }
                S5Rows.Doubles(sb, "corners", cs, 0, 24);
                S5Rows.Bool(sb, "api", gs.DepthFromApi);
            }
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ============================================================================================ dev harness

 /// <summary>
 /// de.s16 descend, kinematic: each simulation tick the grid is moved <paramref name="rate"/>/60 m toward the planet centre
 /// (its velocities zeroed, so neither gravity, the Water Mod's buoyancy nor SE's physics range changes the path) until its
 /// lowest point is at <paramref name="to"/> m, then pinned there. The depth rule and the damage path read the grid where it is.
 /// </summary>
        public static string Descend(long id, double rate, double to, string label)
        {
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e) || !(e is MyCubeGrid)) return "no grid " + id;
            var g = (MyCubeGrid)e;
            if (g.IsStatic) return "grid " + id + " is static";
            GridState gs = Track(g, label);
            Jobs[id] = new DescendJob { Id = id, Rate = Math.Max(0.1, rate), To = to, Label = label, StartTick = _lastTick, StartDepth = gs != null ? gs.LowestDepth : double.NaN };
            DeLog.Info("DE.S16 descend: grid " + id + " (" + (label ?? "-") + ") kinematic at " + rate.ToString("0.##", CultureInfo.InvariantCulture) + " m/s to " +
                       to.ToString("0.#", CultureInfo.InvariantCulture) + " m" + (gs != null && !double.IsNaN(gs.LowestDepth) ? " from " + gs.LowestDepth.ToString("0.0", CultureInfo.InvariantCulture) + " m" : ""));
            return null;
        }

        /// <summary>The tracked grid whose id a JSON number (a double: 53 bits) rounds from, 0 when none (C11 ids should travel as strings).</summary>
        public static long TrackedIdNear(double v)
        {
            foreach (long id in Grids.Keys)
                if ((double)id == v) return id;
            return 0;
        }

        public static bool StopDescend(long id) { return Jobs.Remove(id); }

        private static void StepJobs(ulong tick)
        {
            if (Jobs.Count == 0) return;
            foreach (DescendJob j in Jobs.Values)
            {
                GridState gs;
                if (!Grids.TryGetValue(j.Id, out gs) || gs.Grid == null || gs.Grid.Closed || gs.Grid.MarkedForClose) continue;
                MyCubeGrid g = gs.Grid;
                ulong dt = j.LastTick == 0 || tick <= j.LastTick ? 1UL : Math.Min(10UL, tick - j.LastTick); // a hitch moves at most 10 ticks' worth
                j.LastTick = tick;
                Vector3D pos = g.PositionComp.GetPosition();
                double depth = gs.LowestDepth;
                if (!j.Holding && !double.IsNaN(depth) && depth >= j.To)
                {
                    j.Holding = true;
                    j.Hold = pos;
                    DeLog.Info("DE.S16 descend: grid " + j.Id + " reached " + depth.ToString("0.0", CultureInfo.InvariantCulture) + " m after " +
                               ((tick - j.StartTick) / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + " s, holding");
                    var sb = Head("descend");
                    S5Rows.Id(sb, "grid", j.Id);
                    S5Rows.Str(sb, "label", j.Label);
                    S5Rows.Int(sb, "tick", (long)tick);
                    S5Rows.Str(sb, "phase", "hold");
                    S5Rows.Num(sb, "depth", depth);
                    S5Rows.Num(sb, "to", j.To);
                    S5Rows.Num(sb, "from", j.StartDepth);
                    S5Rows.Num(sb, "seconds", (tick - j.StartTick) / 60.0);
                    Jsonl.WriteLineBuffered(S5Rows.End(sb));
                }
                Vector3D target = pos;
                if (j.Holding) target = j.Hold;
                else
                {
                    Vector3D down = _c - pos;
                    down.Normalize();
                    target = pos + down * (j.Rate * dt / 60.0);
                }
                if (g.Physics != null)
                {
                    g.Physics.LinearVelocity = Vector3.Zero;   // (RigidBody.LinearVelocity): gravity and buoyancy restart from rest each tick
                    g.Physics.AngularVelocity = Vector3.Zero;
                }
                // MyPositionComponentBase.SetPosition -> SetWorldMatrix (a step far below the teleport distance) ->
 // MyPositionComponent.OnWorldPositionChanged -> MyPhysicsBody.OnWorldPositionChanged (the rigid body and its cluster follow)
                g.PositionComp.SetPosition(target);
                j.Steps++;
            }
        }

        public static Dictionary<string, object> JobStatus(long id)
        {
            DescendJob j;
            GridState gs;
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["descending"] = Jobs.TryGetValue(id, out j) && !j.Holding;
            d["holding"] = j != null && j.Holding;
            d["watched"] = WatchId == id;
            d["hudGrid"] = _hudId == id;
            if (j != null) { d["to"] = j.To; d["steps"] = j.Steps; d["seconds"] = Math.Round((_lastTick - j.StartTick) / 60.0, 2); }
            if (Grids.TryGetValue(id, out gs))
            {
                d["depth"] = gs.LowestDepth;
                d["crush"] = gs.HasSummary ? (object)gs.Effective : null;
                d["scanned"] = gs.HasSummary;
                d["blocks"] = gs.Grid != null && !gs.Grid.Closed ? (object)gs.Grid.BlocksCount : 0;
                d["destroyed"] = gs.DestroyedBlocks;
                d["damageTicks"] = gs.DamageTicks;
                d["summary"] = gs.HasSummary ? gs.Summary.ToString() : null;
            }
            d["exists"] = gs != null && gs.Grid != null && !gs.Grid.Closed;
            d["tracked"] = gs != null;
            MyEntity se;
            if (MyEntities.TryGetEntityById(id, out se) && se is MyCubeGrid)
            {
                d["static"] = ((MyCubeGrid)se).IsStatic;
                if (gs == null) d["exists"] = !se.Closed;
            }
            return d;
        }

        /// <summary>The planet floor's depth under SN (x, z), NaN when unknown.</summary>
        public static double FloorDepth(double x, double z)
        {
            MyPlanet planet = SiteBinding.Planet;
            SiteMap map = SiteBinding.Map;
            if (planet == null || map == null || !(SiteBinding.Rs > 0)) return double.NaN;
            Vec3 c = SiteBinding.C;
            Vector3D cc = new Vector3D(c.X, c.Y, c.Z);
            Vec3 p = map.SnToSe(new Vec3(x, 0, z));
            Vector3D pd = new Vector3D(p.X, p.Y, p.Z);
            Vector3D s = planet.GetClosestSurfacePointGlobal(ref pd);
            return SiteBinding.Rs - (s - cc).Length();
        }

        private static readonly double[] ColumnRings = { 60, 120, 250, 500, 1000, 1500, 2000, 3000, 4500, 6000, 7500, 9000, 10500, 12000, 13500, 15000 };
        /// <summary>A column keeps this clearance (m) from every obstacle's box, along its whole depth plus this much.</summary>
        private const double ColumnClearM = 20.0, ColumnStepM = 10.0;

        /// <summary>
 /// The first SN (x, z) on rings of 60 m to 15 km around (<paramref name="nearX"/>, <paramref name="nearZ"/>) (default: the
 /// local character, else the origin) whose planet floor is at least <paramref name="needM"/> deep and whose column, from
 /// the surface to needM + 20 m, is clear: {x, z, floor, nearX, nearZ, ring, skipped} (null when none). Clear = outside
 /// the boxes (inflated 20 m) of every voxel map but the planet and of every grid. Night queue: the planet
 /// floor (FloorDepth) does not see the DE terrain tiles (Subnautica's seabed mirrored as SE voxel maps, 1,024 m tiles
 /// around the anchor; the Safe Shallows floor is ~17-56 m deep), so the shapes pinned at 50 m near the character were
 /// spawned into the seabed: MyGridPhysics collision "explosions"
 /// broke the heavy cube to 33 of 64 blocks and the room to 2 of 100
 /// (S16.LADDER FAIL). The nearest clear column is outside the tiles (the 3 km column of the first sessions was).
 /// </summary>
        public static double[] DeepColumn(double needM, double? nearX, double? nearZ)
        {
            var obstacles = new List<BoundingBoxD>();
            MySession ses = MySession.Static;
            if (ses != null && ses.VoxelMaps != null)
                foreach (MyVoxelBase v in ses.VoxelMaps.Instances)
                    if (v != null && !(v is MyPlanet) && !v.Closed) obstacles.Add(v.PositionComp.WorldAABB.Inflate(ColumnClearM));
            foreach (MyEntity e in MyEntities.GetEntities())
            {
                var g = e as MyCubeGrid;
                if (g != null && !g.Closed && !g.MarkedForClose) obstacles.Add(g.PositionComp.WorldAABB.Inflate(ColumnClearM));
            }
            int skipped = 0;
            double cx = 0, cz = 0;
            if (nearX.HasValue && nearZ.HasValue) { cx = nearX.Value; cz = nearZ.Value; }
            else
            {
                MySession s = MySession.Static;
                MyCharacter ch = s != null ? s.LocalCharacter : null;
                SiteMap map = SiteBinding.Map;
                if (ch != null && map != null)
                {
                    Vector3D p = ch.PositionComp.GetPosition();
                    Vec3 q = map.SeToSn(new Vec3(p.X, p.Y, p.Z));
                    cx = Math.Round(q.X, 1);
                    cz = Math.Round(q.Z, 1);
                }
            }
            foreach (double r in ColumnRings)
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4.0;
                    double x = Math.Round(cx + r * Math.Cos(a)), z = Math.Round(cz + r * Math.Sin(a));
                    double f = FloorDepth(x, z);
                    if (!(f >= needM)) continue;
                    if (!ColumnClear(x, z, needM, obstacles)) { skipped++; continue; }
                    if (skipped > 0)
                        DeLog.Info("DE.S16 deep column SN (" + x + ", " + z + ") floor " + f.ToString("0", CultureInfo.InvariantCulture) + " m, " + r + " m from (" + cx + ", " + cz +
                                   "): " + skipped + " nearer column(s) skipped (terrain tiles or grids in the way, " + obstacles.Count + " obstacle box(es))");
                    return new[] { x, z, f, cx, cz, r, skipped };
                }
            return null;
        }

        /// <summary>True when SN column (x, z) from the surface to <paramref name="needM"/> + 20 m is outside every obstacle box.</summary>
        private static bool ColumnClear(double x, double z, double needM, List<BoundingBoxD> obstacles)
        {
            SiteMap map = SiteBinding.Map;
            if (map == null || obstacles.Count == 0) return true;
            for (double d = 0; d <= needM + ColumnClearM; d += ColumnStepM)
            {
                Vec3 p = map.SnToSe(new Vec3(x, -d, z));
                var pd = new Vector3D(p.X, p.Y, p.Z);
                foreach (BoundingBoxD b in obstacles)
                    if (b.Contains(pd) != ContainmentType.Disjoint) return false;
            }
            return true;
        }

        /// <summary>One block of a spawn plan: a definition and its Min cell (default orientation).</summary>
        public struct Placement
        {
            public string Subtype;
            public int X, Y, Z;
            public Placement(string subtype, int x, int y, int z) { Subtype = subtype; X = x; Y = y; Z = z; }
        }

        /// <summary>
 /// A dynamic test grid (Save = false) from a plan, its box centred at SN (x, y, z). Multi-cell blocks are placed by
 /// their Min cell in the default orientation (the plan keeps them apart).
 /// </summary>
        public static long Spawn(List<Placement> plan, double x, double y, double z, string label, out string why) { return Spawn(plan, x, y, z, label, false, out why); }

        /// <summary>As <see cref="Spawn(List{Placement}, double, double, double, string, out string)"/>, static when <paramref name="isStatic"/> (a station: never tracked nor rated).</summary>
        public static long Spawn(List<Placement> plan, double x, double y, double z, string label, bool isStatic, out string why)
        {
            why = null;
            SiteMap map = SiteBinding.Map;
            if (map == null) { why = "no site map"; return 0; }
            if (plan == null || plan.Count == 0) { why = "empty plan"; return 0; }
            var defs = new List<MyCubeBlockDefinition>(plan.Count);
            MyCubeSize? size = null;
            Vector3I max = Vector3I.Zero;
            foreach (Placement p in plan)
            {
                MyCubeBlockDefinition d = Def(p.Subtype);
                if (d == null)
                {
                    why = "definition missing: " + p.Subtype + (p.Subtype.StartsWith("DE_", StringComparison.Ordinal) ? " (deploy the world mod DeepEngineers.World with Data/CubeBlocks_S16.sbc)" : "");
                    return 0;
                }
                if (size.HasValue && size.Value != d.CubeSize) { why = p.Subtype + " is not of the plan's grid size"; return 0; }
                size = d.CubeSize;
                defs.Add(d);
                max = Vector3I.Max(max, new Vector3I(p.X, p.Y, p.Z) + d.Size - Vector3I.One);
            }
            double gsz = size.Value == MyCubeSize.Large ? 2.5 : 0.5;
            SeMatrix gm = S1Math.SpectatorAt(map, new Vec3(x, y, z), S1Math.HeadingSn(0), Vec3.UnitY);
            // the origin cell centre sits at the translation: shift so the plan's box centre is at the point
            Vec3 t = gm.Translation - gm.Right * (max.X * gsz / 2.0) - gm.Up * (max.Y * gsz / 2.0) - gm.Backward * (max.Z * gsz / 2.0);
            var gridOb = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
            gridOb.EntityId = MyEntityIdentifier.AllocateId();
            gridOb.GridSizeEnum = size.Value;
            gridOb.IsStatic = isStatic;
            gridOb.CreatePhysics = true;
            gridOb.PersistentFlags |= MyPersistentEntityFlags2.Enabled | MyPersistentEntityFlags2.InScene;
            gridOb.DisplayName = "DE.S16 " + (label ?? "test");
            gridOb.PositionAndOrientation = new MyPositionAndOrientation(new Vector3D(t.X, t.Y, t.Z),
                new Vector3((float)gm.Forward.X, (float)gm.Forward.Y, (float)gm.Forward.Z), new Vector3((float)gm.Up.X, (float)gm.Up.Y, (float)gm.Up.Z));
            for (int i = 0; i < plan.Count; i++)
            {
                var b = MyObjectBuilderSerializerKeen.CreateNewObject(defs[i].Id) as MyObjectBuilder_CubeBlock;
                if (b == null) { why = defs[i].Id + " has no MyObjectBuilder_CubeBlock builder"; return 0; }
                b.Min = new SerializableVector3I(plan[i].X, plan[i].Y, plan[i].Z);
                b.EntityId = MyEntityIdentifier.AllocateId();
                b.IntegrityPercent = 1f;
                b.BuildPercent = 1f;
                gridOb.CubeBlocks.Add(b);
            }
            var grid = MyEntities.CreateFromObjectBuilderAndAdd(gridOb, false) as MyCubeGrid;
            if (grid == null || grid.Closed) { why = "MyEntities.CreateFromObjectBuilderAndAdd returned no grid (see the SE log)"; return 0; }
            grid.Save = false;
            if (!isStatic) Track(grid, label);   // a station is never tracked (format crush.depth)
            DeLog.Info("DE.S16 spawned " + (isStatic ? "static " : "") + (label ?? "test") + " grid " + grid.EntityId + ": " + plan.Count + " block(s), box " + (max + Vector3I.One) +
                       " at SN (" + x.ToString("0.#", CultureInfo.InvariantCulture) + ", " + y.ToString("0.#", CultureInfo.InvariantCulture) + ", " + z.ToString("0.#", CultureInfo.InvariantCulture) + ")");
            return grid.EntityId;
        }

        private static MyCubeBlockDefinition Def(string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return null;
            MyDefinitionId id;
            if (subtype.IndexOf('/') < 0 || !MyDefinitionId.TryParse(subtype, out id)) id = new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), subtype);
            MyCubeBlockDefinition def;
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out def) || def == null) return null;
            return def;
        }

        public static bool Remove(long id)
        {
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e) || !(e is MyCubeGrid)) return false;
            Jobs.Remove(id);
            if (WatchId == id) WatchId = 0;
            e.Close();
            return true;
        }

        // ================================================================================================= status

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["enabled"] = _enabled;
            d["active"] = _active;
            d["inactive"] = InactiveWhy;
            d["damageGate"] = DamageGateOpen ? "active" : DamageGateWhy;
            d["gate"] = S16Conventions.DamageGate.Name(DamageGate < 0 ? S16Conventions.DamageGate.None : DamageGate);
            d["damageSwitch"] = DamageSwitch;
            d["graceLeftS"] = Math.Round(GraceLeftS, 1);
            d["tracked"] = Grids.Count;
            d["hudGrid"] = _hudId;
            d["watch"] = WatchId;
            d["scans"] = Scans;
            d["scannedBlocks"] = ScannedBlocks;
            d["lastScanMs"] = LastScanMs;
            d["maxScanMs"] = MaxScanMs;
            d["lastDamageMs"] = LastDamageMs;
            d["maxDamageMs"] = MaxDamageMs;
            d["damageTicks"] = DamageTicks;
            d["damagedBlocks"] = DamagedBlocks;
            d["destroyed"] = Destroyed;
            d["damageSe"] = DamageSe;
            d["creaks"] = Creaks;
            d["voiceEvents"] = VoiceEvents;
            d["voicePlays"] = VoicePlays;
            d["hudRows"] = HudRows;
            d["suppressedLines"] = SuppressedLines;
            d["snHudFlags"] = SnHudFlags();
            d["apiMisses"] = ApiMisses;
            d["errors"] = Errors;
            d["c5"] = S16SeWire.IsActive ? "seq creak " + S16SeWire.CreakSeq + " voice " + S16SeWire.VoiceSeq + " damage " + S16SeWire.DamageSeq : "off";
            d["mutants"] = S16SeMutants.Active;
            var grids = new List<object>();
            foreach (GridState gs in Order)
            {
                if (grids.Count >= 16) break;
                grids.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "grid", gs.Id }, { "label", gs.Label }, { "name", gs.Grid != null ? gs.Grid.DisplayName : null }, { "size", S16Conventions.Size.Name(gs.Size) },
                    { "depth", double.IsNaN(gs.LowestDepth) ? null : (object)Math.Round(gs.LowestDepth, 2) }, { "crush", gs.HasSummary ? (object)gs.Crush : null },
                    { "limit", gs.HasSummary ? (object)gs.Effective : null }, { "docked", gs.Docked }, { "exempt", gs.Exempt }, { "groupTier", gs.GroupTier },
                    { "summary", gs.HasSummary ? gs.Summary.ToString() : "not scanned" }, { "destroyed", gs.DestroyedBlocks },
                });
            }
            d["grids"] = grids;
            return d;
        }

        private static StringBuilder Head(string name)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s16." + name);
            return sb;
        }

        private static void OnceLog(string key, string line)
        {
            if (Once.Add(key)) DeLog.Info(line);
        }
    }
}
