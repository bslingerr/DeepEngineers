using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.SE
{
    /// <summary>
 /// CONTRACT §5.4 "C2 effect
 /// events"): SE keeps the gameplay and raises event records that Subnautica draws and plays with its own prefabs and
 /// FMOD events. Sources: thrusters (one sustain per flame of every <c>MyThrust</c> of a mirrored grid, polled each
 /// tick from <c>CurrentStrength</c> and the flame dummies), ship tools (<c>MyShipWelder</c>, <c>MyShipGrinder</c>,
 /// <c>MyShipDrill</c>) and hand tools (<c>MyAngleGrinder</c>, <c>MyWelder</c>, <c>MyHandDrill</c> of every online
 /// character) polled as sustains, the splash test (a mirrored entity's lowest point crossing SN y = 0 at ≥ 2 m/s), and
 /// the five postfixes of <see cref="S7EffectPatches"/> (shots, projectile hits, explosions, grid contacts, hull
 /// breaks). Everything goes into <see cref="Window"/> (fixed per-tick buffer, per-type caps, 128 per tick, 8-tick
 /// window); <see cref="BeforeC2"/> closes the tick right before the C2 write, whose B2 <c>Link.WriteSeEntities</c>
 /// overload copies <see cref="Rows"/> to C2 0x40000 and <see cref="Extension"/> to the header's <c>pad_1c</c>.
 /// Sources run only while the S5 stream runs (hello ack) in the site (C5 zone CORE, TILE VOID or OUTER VOID), within
 /// 250 m of the camera (explosions 1,000 m). Attached (SE-local of the mirrored grid, subgrid or the C1 character)
 /// whenever the source belongs to one; else SN-world through F and mapdir. Keyed by entity id everywhere (v3: every
 /// online character's hand tool, every grid's thrusters). Update thread (the postfixes may come from another thread:
 /// they then never attach and only append under the window's lock). Seeded failures read here: S7.M12 (shots off),
 /// S7.M14 (tool sustains never refreshed), S7.M15 (attached events with entity 0), S7.M16 (no splash), S7.M17 (a
 /// 1-tick window), S7.M20 (caps off).
 /// </summary>
    internal static class S7Effects
    {
        public const string Cmd = "de.fx";

        /// <summary>The event buffer and the C2 window.</summary>
        public static readonly S7EventWindow Window = new S7EventWindow();
        private static readonly S7SustainTracker Sustains = new S7SustainTracker();
        private static readonly S7EventRow[] _rows = new S7EventRow[S7Conventions.C2Events.MaxRows];
        private static readonly S7EventRow[] _stops = new S7EventRow[256];
        private static int _rowCount;

        /// <summary>The window's rows in seq order as of the last <see cref="BeforeC2"/> (the C2 write copies them).</summary>
        public static S7EventRow[] Rows { get { return _rows; } }
        public static int RowCount { get { return _rowCount; } }

        // ---- context of the current tick (update thread; read by the postfixes)
        private static volatile bool _active;
        private static SiteMap _site;
        private static Vector3D _cam;
        private static ulong _tick;
        private static int _mainThread = -1;
        private static long _localCharacter, _localSeatRoot;
        private static string _inactiveWhy = "never ticked";

        // ---- per-grid source index (thrusters and ship tools), rebuilt when the grid's fat-block count changes
        private sealed class ThrustFx
        {
            public MyThrust T;
            public ulong FatKey;
            public ushort Kind;
            /// <summary>The model whose flame dummies are cached (re-read when the block's model changes, e.g. built up).</summary>
            public object Model;
            public ulong[] Keys = new ulong[0];
            public Vector3[] Pos = new Vector3[0], Dir = new Vector3[0];
            public float[] Radius = new float[0];
            /// <summary>S7p-effects-under (P2): the definition's FlameIdleColor / FlameFullColor (xyz) and FlameLengthScale.</summary>
            public bool HasColour;
            public Vector3 Idle, Full;
            public float LengthScale;
        }

        private sealed class ToolFx
        {
            public MyCubeBlock Block;
            public byte Type;
            public ulong Key;
            /// <summary>S7p-effects-under (P6a): the drill's contact class cache (re-read every ContactRefreshTicks).</summary>
            public DrillContactCache Contact = new DrillContactCache();
        }

        /// <summary>S7p-effects-under (P6a): what a drill bit touches, cached per drill (voxel material lookups are capped per tick).</summary>
        private sealed class DrillContactCache
        {
            public byte Class;
            public ulong Tick;
            public long Entity;
        }

        private sealed class GridFx
        {
            public MyCubeGrid Grid;
            public int FatCount = -1;
            public bool Dirty = true;
            public uint SeenEpoch;
            public readonly List<ThrustFx> Thrusters = new List<ThrustFx>();
            public readonly List<ToolFx> Tools = new List<ToolFx>();
        }

        private static readonly Dictionary<long, GridFx> _grids = new Dictionary<long, GridFx>();
        private static readonly List<long> _gridGone = new List<long>();
        private static uint _gridEpoch;
        private static readonly Dictionary<long, double> _splashPrev = new Dictionary<long, double>();
        private static readonly List<MyCharacter> _characters = new List<MyCharacter>();
        private static ulong _charactersTick;

        // ---- reflection
        private static FieldInfo _handDrillBase, _shipDrillBase, _shipToolOnSomething;
        private static bool _reflected;

        // ---- test hooks (de.fx gun) and rows
        private static readonly List<long> _gunQueue = new List<long>();
        private static int _gunShotsLeft;
        private static ulong _gunNextTick;
        private static ulong _traceUntilMs, _nextRowMs, _lastDropRowMs, _lastRowTick, _eventRowSecondMs;
        private static int _eventRowsThisSecond;
        private static double _msLast, _msMax;
        private static long _ticks, _postfixErrors, _offThread, _notMirrored, _outOfRange, _stressRaised;
        // s7.fx rows carry per-row deltas (the analyzer sums them over a window) beside the cumulative totals
        private static readonly long[] _prevRaised = new long[S7EventRules.TypeSlots], _prevSent = new long[S7EventRules.TypeSlots],
                                       _prevDropped = new long[S7EventRules.TypeSlots], _sustainRows = new long[S7EventRules.TypeSlots],
                                       _prevSustainRows = new long[S7EventRules.TypeSlots];
        private static readonly int[] _sustainKeys = new int[S7EventRules.TypeSlots];
        /// <summary>The s7.fx.event rows (one per accepted one-shot) are capped per second (log volume).</summary>
        public const int EventRowsPerSecond = 256;
        private static readonly HashSet<string> _loggedOnce = new HashSet<string>(StringComparer.Ordinal);

        // ---- S7p-effects-under:
 // the thruster's own flame colour and length scale, the shot's hit distance, the drill's contact point and class.
 // Switches (the SE keys of the S7p scales): colour, ray, contact; env DEEPENGINEERS_S7P_FX, de.fx {s7p: …}.
        internal static readonly S7pFxSettings S7p = NewS7pSettings();
        private static string _s7pEnvError;
        private static readonly List<MyPhysics.HitInfo> _rayHits = new List<MyPhysics.HitInfo>(16);
        private static ulong _rayTick, _contactTick;
        private static int _raysThisTick, _contactsThisTick;
        private static readonly Dictionary<long, DrillContactCache> _handContacts = new Dictionary<long, DrillContactCache>();
        private static long _s7pRays, _s7pRaysSkipped, _s7pRayHits, _s7pRaysOffThread, _s7pRayErrors, _s7pColoured, _s7pNoColour,
                            _s7pContactLookups, _s7pContactSkipped, _s7pContactErrors, _s7pContactRows;
        private static float _maxThrust = 1.5f;

        private static S7pFxSettings NewS7pSettings()
        {
            var s = new S7pFxSettings();
            try { _s7pEnvError = s.ApplyText(Environment.GetEnvironmentVariable(S7pFxSettings.EnvVar)); }
            catch (Exception e) { _s7pEnvError = e.GetType().Name + ": " + e.Message; }
            return s;
        }

        // ============================================================================================ per tick

 /// <summary>
 /// The S5 C2 writer's hook (update thread, right before the C2 write of <paramref name="tick"/>): sets the
 /// context, polls the sustains and the splash test, turns the ended sustains into Stop rows, closes the tick and
 /// copies the window into <see cref="Rows"/>. Returns the row count. <paramref name="streaming"/>: the S5 stream
 /// gate (hello ack); the stream id is (SE generation &lt;&lt; 16) | (stream restarts &amp; 0xFFFF).
 /// </summary>
        internal static int BeforeC2(ulong tick, Vector3D camera, bool streaming, uint seGeneration, long streamStarts)
        {
            long t0 = Stopwatch.GetTimestamp();
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            if (!S7EffectPatches.Attempted)
            {
                try { S7EffectPatches.Apply(); }
                catch (Exception e) { Once("patches", "DE.S7 fx patches FAILED " + e.GetType().Name + ": " + e.Message + " (polled sources only)"); }
            }
            _tick = tick;
            _cam = camera;
            _site = SiteBinding.Map;
            bool site = FrameWriter.HasC5 && FrameWriter.C5Zone <= 2;
            _active = streaming && site && _site != null;
            _inactiveWhy = _active ? null : !streaming ? "stream not running (no hello ack)" : _site == null ? "no site map" : "camera outside the site (zone " + FrameWriter.C5Zone + ")";
            Window.WindowTicks = S7Mutants.M17 ? 1 : S7Conventions.C2Events.WindowTicks;
            Window.CapsOff = S7Mutants.M20;
            if (Window.SetStream(S7EventRules.Stream(seGeneration, streamStarts)))
            {
                Sustains.Clear();
                _splashPrev.Clear();
                DeLog.Info("DE.S7 fx stream 0x" + Window.Stream.ToString("x8", CultureInfo.InvariantCulture) + " (seq from 1)");
            }
            Sustains.BeginTick();
            if (_active)
            {
                LocalIdentity();
                if (!_reflected) Reflect();
                try { PollGrids(tick); } catch (Exception e) { Once("grids", "DE.S7 fx grid sources error " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
                try { PollCharacters(tick); } catch (Exception e) { Once("hand", "DE.S7 fx hand tools error " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); }
                try { if (!S7Mutants.M16) PollSplash(); } catch (Exception e) { Once("splash", "DE.S7 fx splash error " + e.GetType().Name + ": " + e.Message); }
                try { GunTick(tick); } catch (Exception e) { Once("gun", "DE.S7 fx gun hook error " + e.GetType().Name + ": " + e.Message); }
            }
            try { S11Blasts.Flush(tick); } catch (Exception e) { Once("s11blast", "DE.S11 blast flush error " + e.GetType().Name + ": " + e.Message); } // S11 hook: the tick's explosion groups
            int n;
            while ((n = Sustains.CollectStops(tick, _stops)) > 0)
                for (int i = 0; i < n; i++) Raise(ref _stops[i]);
            if (_burstTicksLeft > 0) BurstRaise(tick); // review F10: the share of a stress burst for this tick
            Window.EndTick(tick);
            BurstAfterEndTick(tick);
            _rowCount = Window.CopyRows(_rows);
            _ticks++;
            ulong now = Win32.TickCount64();
            EventRows(tick, now, Window.LastAcceptedTotal, now < _traceUntilMs);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _msLast = ms;
            if (ms > _msMax) _msMax = ms;
            bool dropTick = Window.LastDroppedTotal > 0 && now >= _lastDropRowMs + 100;
            if (now >= _nextRowMs || dropTick)
            {
                if (dropTick) _lastDropRowMs = now;
                if (now >= _nextRowMs) _nextRowMs = now + 1000;
                Row(dropTick ? "drop" : "periodic", null);
            }
            return _rowCount;
        }

        /// <summary>The header extension of the C2 write (Events, plus the lights' flag and count from B2 lights).</summary>
        internal static C2Ext Extension(ulong tick, byte lightFlags, uint lightsFound)
        {
            return Window.Extension(tick, _rowCount, lightFlags, lightsFound);
        }

        /// <summary>
 /// The S5 C2 writer's one call (B2 hook in <c>GridMirrorProducer.S5</c>; never throws): <see cref="BeforeC2"/> and
 /// the header extension. On an error this tick carries no event rows (the extension without <c>Events</c>; the
 /// lights' flag and count kept) and the error is logged once.
 /// </summary>
        internal static int ForC2(ulong tick, Vector3D camera, bool streaming, uint seGeneration, long streamStarts, byte lightFlags, uint lightsFound, out C2Ext ext)
        {
            try
            {
                int n = BeforeC2(tick, camera, streaming, seGeneration, streamStarts);
                ext = Extension(tick, lightFlags, lightsFound);
                return n;
            }
            catch (Exception e)
            {
                Once("tick", "DE.S7 fx tick error " + e.GetType().Name + ": " + e.Message + " (no event rows this tick; logged once)\n" + e.StackTrace);
                _rowCount = 0;
                ext = C2Ext.For(tick, lightFlags, 0, 0, 0, 0, lightsFound);
                return 0;
            }
        }

        /// <summary>World unload: forget the grid index and the hand-tool list (the window ages out by itself).</summary>
        internal static void Forget(string why)
        {
            _grids.Clear();
            _characters.Clear();
            _handContacts.Clear(); // S7p-effects-under
            _splashPrev.Clear();
            _gunQueue.Clear();
            _gunShotsLeft = 0;
            _active = false;
            _inactiveWhy = why;
        }

        private static void LocalIdentity()
        {
            _localCharacter = CharacterExport.CurrentCharacterId;
            _localSeatRoot = 0;
            MySession s = MySession.Static;
            MyCharacter c = s != null ? s.LocalCharacter : null;
            if (c == null || !c.IsSitting) return;
            var block = c.Parent as MyCubeBlock;
            if (block != null && block.CubeGrid != null) _localSeatRoot = EntitySet.RootOf(block.CubeGrid);
        }

        private static void Reflect()
        {
            _reflected = true;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            _handDrillBase = typeof(MyHandDrill).GetField("m_drillBase", inst);
            _shipDrillBase = typeof(MyShipDrill).GetField("m_drillBase", inst);
            _shipToolOnSomething = typeof(MyShipToolBase).GetField("m_isActivatedOnSomething", inst);
            if (_handDrillBase == null || _handDrillBase.FieldType != typeof(MyDrillBase)) { _handDrillBase = null; DeLog.Info("DE.S7 fx OFF missing MyHandDrill.m_drillBase (hand drill at the tool, no sediment point)"); }
            if (_shipDrillBase == null || _shipDrillBase.FieldType != typeof(MyDrillBase)) { _shipDrillBase = null; DeLog.Info("DE.S7 fx OFF missing MyShipDrill.m_drillBase (ship drill at the block, no sensor point)"); }
            if (_shipToolOnSomething == null || _shipToolOnSomething.FieldType != typeof(bool)) { _shipToolOnSomething = null; DeLog.Info("DE.S7 fx OFF missing MyShipToolBase.m_isActivatedOnSomething (ship tools never Hit)"); }
            // S7p-effects-under (P2): SE's own MAX_THRUST
 // the divisor of the flame colour's lerp in MyThrustFlameAnimator
            try { float m = MyConstants.MAX_THRUST; if (m > 0 && !float.IsInfinity(m)) _maxThrust = m; } catch (Exception) { }
            DeLog.Info("DE.S7p fx producer: " + S7pFxConventions.Header + " 0x" + S7pFxConventions.Hash.ToString("x16", CultureInfo.InvariantCulture) +
                       (S7pFxConventions.Hash == S7pFxConventions.PinnedHash ? " pinned" : " MISMATCH") + ", MAX_THRUST " +
                       _maxThrust.ToString("0.###", CultureInfo.InvariantCulture) + ", " + S7p.Describe() +
                       (_s7pEnvError != null ? " (" + S7pFxSettings.EnvVar + ": " + _s7pEnvError + ")" : ""));
        }

        // ============================================================================================ polled sources

        private static void PollGrids(ulong tick)
        {
            _gridEpoch++;
            foreach (GridMirrorProducer.MirroredGrid g in GridMirrorProducer.Grids.Values)
            {
                MyCubeGrid grid = g.Grid;
                if (grid == null || grid.Closed || grid.MarkedForClose || g.Floating) continue;
                if (!GridMirrorProducer.SentToSn(g.Id)) continue;
                GridFx fx;
                if (!_grids.TryGetValue(g.Id, out fx)) { fx = new GridFx { Grid = grid }; _grids[g.Id] = fx; }
                fx.SeenEpoch = _gridEpoch;
                if (grid.PositionComp.WorldAABB.Distance(_cam) > S7Conventions.EventLimits.EffectRadiusM) continue;
                int fat = grid.GetFatBlocks().Count;
                if (fx.Dirty || fx.FatCount != fat || fx.Grid != grid) Index(fx, grid, fat);
                PollThrusters(fx, grid, tick);
                PollShipTools(fx, grid, tick);
            }
            if (_grids.Count == 0) return;
            _gridGone.Clear();
            foreach (KeyValuePair<long, GridFx> kv in _grids) if (kv.Value.SeenEpoch != _gridEpoch) _gridGone.Add(kv.Key);
            for (int i = 0; i < _gridGone.Count; i++) _grids.Remove(_gridGone[i]);
        }

        private static void Index(GridFx fx, MyCubeGrid grid, int fatCount)
        {
            fx.Grid = grid;
            fx.FatCount = fatCount;
            fx.Dirty = false;
            fx.Thrusters.Clear();
            fx.Tools.Clear();
            var blocks = grid.GetFatBlocks();
            for (int i = 0; i < blocks.Count; i++)
            {
                MyCubeBlock b = blocks[i];
                if (b == null || b.Closed || b.SlimBlock == null) continue;
                var t = b as MyThrust;
                if (t != null)
                {
                    string kind = null;
                    try { kind = t.BlockDefinition != null ? t.BlockDefinition.ThrusterType.String : null; } catch (Exception) { }
                    var tfx = new ThrustFx { T = t, FatKey = FatKey(b), Kind = S7EventRules.ThrusterKindOf(kind) };
                    // S7p-effects-under (P2): the block's own flame colours and length scale
                    try
                    {
                        var def = t.BlockDefinition;
                        if (def != null)
                        {
                            tfx.Idle = new Vector3(def.FlameIdleColor.X, def.FlameIdleColor.Y, def.FlameIdleColor.Z);
                            tfx.Full = new Vector3(def.FlameFullColor.X, def.FlameFullColor.Y, def.FlameFullColor.Z);
                            tfx.HasColour = true;
                            tfx.LengthScale = def.FlameLengthScale;
                        }
                    }
                    catch (Exception e) { Once("s7p.colour", "DE.S7p fx thruster colour unreadable " + e.GetType().Name + ": " + e.Message + " (the kind's vanilla colour)"); }
                    fx.Thrusters.Add(tfx);
                    continue;
                }
                byte type = b is MyShipWelder ? EventType.Welder : b is MyShipGrinder ? EventType.Grinder : b is MyShipDrill ? EventType.PrawnDrill : (byte)0;
                if (type != 0) fx.Tools.Add(new ToolFx { Block = b, Type = type, Key = S7EventKeys.ShipTool(FatKey(b)) });
            }
        }

        private static ulong FatKey(MyCubeBlock b)
        {
            Vector3I m = b.SlimBlock.Min;
            return S5Conv.FatKey(m.X, m.Y, m.Z);
        }

        private static void PollThrusters(GridFx fx, MyCubeGrid grid, ulong tick)
        {
            if (fx.Thrusters.Count == 0) return;
            MatrixD gw = grid.PositionComp.WorldMatrixRef;
            byte local = LocalFlag(grid);
            for (int k = 0; k < fx.Thrusters.Count; k++)
            {
                ThrustFx tf = fx.Thrusters[k];
                MyThrust t = tf.T;
                if (t == null || t.Closed || t.MarkedForClose) { fx.Dirty = true; continue; }
                S14Se.OnThruster(t); // S14 hook (point 4b): SE's own thruster sound under water, started on this tick
                float strength = t.CurrentStrength;
                if (!(strength > S7EventRules.ThrusterLiveStrength)) continue; // not observed: its sustains end with a Stop row
                object model = t.Model;
                if (!ReferenceEquals(model, tf.Model)) LoadFlames(tf, model);
                int nf = tf.Keys.Length;
                if (nf == 0) continue;
                // S7p-effects-under (P2): SE's own flame colour of this strength (MyThrustFlameAnimator: lerp(idle, full,
 // strength / MyConstants.MAX_THRUST)) and the length scale class ride every row of the sustain in the S7
 // pad (ext s7p fx 1, format event.flame); the subtype stays the S7 ThrusterKind
                ushort colour = 0;
                if (tf.HasColour && S7p.On(S7pFxSettings.Knob.Colour))
                {
                    float cr, cg, cb;
                    S7pFxCodec.FlameColour(tf.Idle.X, tf.Idle.Y, tf.Idle.Z, tf.Full.X, tf.Full.Y, tf.Full.Z, strength, _maxThrust, out cr, out cg, out cb);
                    colour = S7pFxCodec.EncodeFlame(cr, cg, cb, tf.LengthScale);
                }
                Matrix lm = t.PositionComp.LocalMatrixRef;
                for (int i = 0; i < nf; i++)
                {
                    Vector3 lp = Vector3.Transform(tf.Pos[i], ref lm);
                    Vector3 ld = Vector3.TransformNormal(tf.Dir[i], lm);
                    Vector3D wp = Vector3D.Transform((Vector3D)lp, gw);
                    var r = new S7EventRow
                    {
                        type = EventType.Thruster,
                        flags = (byte)(EventFlags.Attached | S7EventRules.WaterFlags(SnY(wp)) | local),
                        entityId = (ulong)grid.EntityId,
                        key = tf.Keys[i],
                        pos = F3(lp),
                        dir = F3(Unit(ld)),
                        intensity = S7EventRules.Clamp01(strength),
                        size = tf.Radius[i],
                        subtype = tf.Kind,
                        extra = colour,
                        tickLow = (uint)tick,
                    };
                    if (Sustains.Observe(ref r, tick) != SustainEmit.None)
                    {
                        if (colour != 0) _s7pColoured++; else _s7pNoColour++;
                        Raise(ref r);
                    }
                }
            }
        }

        // MyThrust.Flames is read by reflection: the
 // FlameInfo type lives in Sandbox.RenderDirect.dll, which the plugin does not reference. Its members Position,
 // Direction (Vector3) and Radius (float) are VERIFIED by the initialiser in MyThrust.LoadDummies;
 // ListReader<T> is IEnumerable. Read once per thruster and model.
        private static PropertyInfo _flamesProp;
        private static Type _flameType;
        private static Func<object, object> _flamePos, _flameDir, _flameRadius;
        private static bool _flamesBroken;

        private static void LoadFlames(ThrustFx tf, object model)
        {
            tf.Model = model;
            tf.Keys = new ulong[0];
            if (_flamesBroken) return;
            try
            {
                if (_flamesProp == null) _flamesProp = typeof(MyThrust).GetProperty("Flames", BindingFlags.Instance | BindingFlags.Public);
                if (_flamesProp == null) { _flamesBroken = true; DeLog.Info("DE.S7 fx OFF missing MyThrust.Flames (no thruster events)"); return; }
                var list = _flamesProp.GetValue(tf.T, null) as System.Collections.IEnumerable;
                if (list == null) return;
                var pos = new List<Vector3>();
                var dir = new List<Vector3>();
                var rad = new List<float>();
                foreach (object f in list)
                {
                    if (f == null) continue;
                    if (_flameType != f.GetType())
                    {
                        _flameType = f.GetType();
                        _flamePos = Member(_flameType, "Position");
                        _flameDir = Member(_flameType, "Direction");
                        _flameRadius = Member(_flameType, "Radius");
                        if (_flamePos == null || _flameDir == null || _flameRadius == null)
                        {
                            _flamesBroken = true;
                            DeLog.Info("DE.S7 fx OFF missing " + _flameType.FullName + ".Position/Direction/Radius (no thruster events)");
                            return;
                        }
                    }
                    pos.Add((Vector3)_flamePos(f));
                    dir.Add((Vector3)_flameDir(f));
                    rad.Add((float)_flameRadius(f));
                }
                int n = pos.Count;
                var keys = new ulong[n];
                for (int i = 0; i < n; i++) keys[i] = S7EventKeys.Thruster(tf.FatKey, i);
                tf.Pos = pos.ToArray();
                tf.Dir = dir.ToArray();
                tf.Radius = rad.ToArray();
                tf.Keys = keys;
            }
            catch (Exception e)
            {
                _flamesBroken = true;
                DeLog.Info("DE.S7 fx OFF thruster flames unreadable " + e.GetType().Name + ": " + e.Message + " (no thruster events)");
            }
        }

        private static Func<object, object> Member(Type t, string name)
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo f = t.GetField(name, inst);
            if (f != null) return o => f.GetValue(o);
            PropertyInfo p = t.GetProperty(name, inst);
            if (p != null && p.GetIndexParameters().Length == 0) return o => p.GetValue(o, null);
            return null;
        }

        private static void PollShipTools(GridFx fx, MyCubeGrid grid, ulong tick)
        {
            if (fx.Tools.Count == 0) return;
            MatrixD gw = grid.PositionComp.WorldMatrixRef;
            MatrixD inv = grid.PositionComp.WorldMatrixNormalizedInv;
            byte local = LocalFlag(grid);
            bool refresh = !S7Mutants.M14;
            for (int k = 0; k < fx.Tools.Count; k++)
            {
                ToolFx tf = fx.Tools[k];
                MyCubeBlock b = tf.Block;
                if (b == null || b.Closed || b.MarkedForClose) { fx.Dirty = true; continue; }
                Matrix lm = b.PositionComp.LocalMatrixRef;
                Vector3 lp, ld = lm.Forward;
                float size, intensity;
                bool hit = false;
                ushort subtype = S7Conventions.ToolSource.Ship;
                var drill = b as MyShipDrill;
                if (drill != null)
                {
                    if (!drill.IsShooting) continue;
                    MyDrillBase db = _shipDrillBase != null ? _shipDrillBase.GetValue(drill) as MyDrillBase : null;
                    if (db != null && db.Sensor != null)
                    {
                        Vector3D front = db.Sensor.FrontPoint;
                        lp = (Vector3)Vector3D.Transform(front, inv);
                        size = (float)Vector3D.Distance(db.Sensor.Center, front);
                        hit = db.DrilledEntity != null;
                        // S7p-effects-under (P6a): the contact point and what the bit touches (ext s7p fx 1, format event.drill)
                        Vector3D contact;
                        byte cls = DrillContactOf(db, tick, tf.Contact, out contact);
                        if (hit && cls != S7pFxConventions.DrillContact.None)
                        {
                            Vector3 lc = (Vector3)Vector3D.Transform(contact, inv);
                            lp = QuantiseContact(lc);
                        }
                        subtype = S7pFxCodec.ToolSubtype(S7Conventions.ToolSource.Ship, cls);
                    }
                    else { lp = lm.Translation; size = grid.GridSize; }
                    intensity = S7EventRules.ToolIntensity(hit);
                }
                else
                {
                    var tool = b as MyShipToolBase;
                    if (tool == null || !tool.IsShooting) continue;
                    BoundingSphere ds = tool.DetectorSphere; // grid-local work sphere (MyShipToolBase.ActivateCommon)
                    lp = ds.Center;
                    size = ds.Radius > 0 ? ds.Radius : grid.GridSize;
                    if (_shipToolOnSomething != null) hit = (bool)_shipToolOnSomething.GetValue(tool);
                    intensity = tf.Type == EventType.Welder ? 1f : S7EventRules.ToolIntensity(hit);
                }
                Vector3D wp = Vector3D.Transform((Vector3D)lp, gw);
                var r = new S7EventRow
                {
                    type = tf.Type,
                    flags = (byte)(EventFlags.Attached | S7EventRules.WaterFlags(SnY(wp)) | local | (hit ? EventFlags.Hit : 0)),
                    entityId = (ulong)grid.EntityId,
                    key = tf.Key,
                    pos = F3(lp),
                    dir = F3(Unit(ld)),
                    intensity = intensity,
                    size = size,
                    subtype = subtype,
                    tickLow = (uint)tick,
                };
                if (Sustains.Observe(ref r, tick, refresh) != SustainEmit.None)
                {
                    if (drill != null) _s7pContactRows++;
                    Raise(ref r);
                }
            }
        }

        /// <summary>
 /// S7p-effects-under (P6a): what a drill bit touches (<c>MyDrillBase.DrilledEntity</c>, public get): a voxel's
 /// class from <c>MyVoxelBase.GetMaterialAt(ref Vector3D)</c> → <c>MyVoxelMaterialDefinition.MaterialTypeName</c>
 /// (at most <c>ContactLookupsPerTick</c> lookups per tick, each drill re-read every <c>ContactRefreshTicks</c>; the
 /// rest keep their last class, counted), a grid Metal, anything else Other; None while it touches nothing.
 /// <paramref name="point"/> = <c>DrilledEntityPoint</c> (world). Switch <c>contact</c> off: None (the S7 rows).
 /// </summary>
        private static byte DrillContactOf(MyDrillBase db, ulong tick, DrillContactCache cache, out Vector3D point)
        {
            point = Vector3D.Zero;
            if (db == null || !S7p.On(S7pFxSettings.Knob.Contact)) return S7pFxConventions.DrillContact.None;
            MyEntity e = db.DrilledEntity;
            if (e == null || e.Closed) return S7pFxConventions.DrillContact.None;
            point = db.DrilledEntityPoint;
            if (point.LengthSquared() < 1e-6) point = db.Sensor != null ? db.Sensor.FrontPoint : point;
            if (e is MyCubeGrid) return S7pFxConventions.DrillContact.Metal;
            var vox = e as MyVoxelBase;
            if (vox == null) return S7pFxConventions.DrillContact.Other;
            if (cache.Entity == e.EntityId && cache.Class != 0 && tick < cache.Tick + (ulong)S7pFxConventions.S7pLimits.ContactRefreshTicks && tick >= cache.Tick)
                return cache.Class;
            if (_contactTick != tick) { _contactTick = tick; _contactsThisTick = 0; }
            if (_contactsThisTick >= S7pFxConventions.S7pLimits.ContactLookupsPerTick)
            {
                _s7pContactSkipped++;
                return cache.Entity == e.EntityId && cache.Class != 0 ? cache.Class : S7pFxConventions.DrillContact.Rock;
            }
            _contactsThisTick++;
            _s7pContactLookups++;
            byte cls;
            try
            {
                MyVoxelMaterialDefinition m = vox.GetMaterialAt(ref point);
                cls = S7pFxCodec.ContactOfMaterial(m != null ? m.MaterialTypeName : null);
            }
            catch (Exception ex)
            {
                _s7pContactErrors++;
                Once("s7p.contact", "DE.S7p fx drill material unreadable " + ex.GetType().Name + ": " + ex.Message + " (Rock; logged once)");
                cls = S7pFxConventions.DrillContact.Rock;
            }
            cache.Class = cls;
            cache.Tick = tick;
            cache.Entity = e.EntityId;
            return cls;
        }

        /// <summary>The contact point on the ContactQuantumCm grid of its frame (a drill held still sends no Move rows).</summary>
        private static Vector3 QuantiseContact(Vector3 p)
        {
            float q = S7pFxConventions.S7pLimits.ContactQuantumCm / 100f;
            return new Vector3(S7pFxCodec.Quantise(p.X, q), S7pFxCodec.Quantise(p.Y, q), S7pFxCodec.Quantise(p.Z, q));
        }

        private static void PollCharacters(ulong tick)
        {
            if (tick >= _charactersTick + 60 || tick < _charactersTick)
            {
                _charactersTick = tick;
                _characters.Clear();
                MySession s = MySession.Static;
                if (s != null && s.Players != null)
                {
                    foreach (MyPlayer p in s.Players.GetOnlinePlayers())
                    {
                        MyCharacter c = p != null ? p.Character : null;
                        if (c != null && !_characters.Contains(c)) _characters.Add(c);
                    }
                }
                if (s != null && s.LocalCharacter != null && !_characters.Contains(s.LocalCharacter)) _characters.Add(s.LocalCharacter);
            }
            bool refresh = !S7Mutants.M14;
            for (int k = 0; k < _characters.Count; k++)
            {
                MyCharacter c = _characters[k];
                if (c == null || c.Closed || c.MarkedForClose || c.IsDead) continue;
                var tool = c.CurrentWeapon as MyEntity;
                if (tool == null || tool.Closed) continue;
                if (Vector3D.Distance(c.PositionComp.GetPosition(), _cam) > S7Conventions.EventLimits.EffectRadiusM) continue;
                byte type;
                Vector3D p;
                float size;
                bool hit;
                ushort subtype = S7Conventions.ToolSource.Hand;
                var grinder = tool as MyAngleGrinder;
                var welder = tool as MyWelder;
                var drill = tool as MyHandDrill;
                if (grinder != null)
                {
                    if (!grinder.IsShooting) continue;
                    type = EventType.Grinder; hit = grinder.HasHitBlock; size = 0.3f;
                    p = grinder.GunBase != null ? grinder.GunBase.GetMuzzleWorldPosition() : tool.PositionComp.GetPosition();
                }
                else if (welder != null)
                {
                    if (!welder.IsShooting) continue;
                    type = EventType.Repair; hit = welder.HasHitBlock; size = 0.3f;
                    p = welder.GunBase != null ? welder.GunBase.GetMuzzleWorldPosition() : tool.PositionComp.GetPosition();
                }
                else if (drill != null)
                {
                    if (!drill.IsShooting) continue;
                    type = EventType.Drill; hit = drill.IsDrillingAnObject; size = 0.5f;
                    MyDrillBase db = _handDrillBase != null ? _handDrillBase.GetValue(drill) as MyDrillBase : null;
                    p = db != null && db.Sensor != null ? db.Sensor.FrontPoint : tool.PositionComp.GetPosition();
                    if (db != null)
                    {
                        // S7p-effects-under (P6a): the contact point (quantised in the row's frame by Place) and its class
                        DrillContactCache cache;
                        if (!_handContacts.TryGetValue(tool.EntityId, out cache))
                        {
                            if (_handContacts.Count > 256) _handContacts.Clear();
                            cache = new DrillContactCache();
                            _handContacts[tool.EntityId] = cache;
                        }
                        Vector3D contact;
                        byte cls = DrillContactOf(db, tick, cache, out contact);
                        if (hit && cls != S7pFxConventions.DrillContact.None) p = contact;
                        subtype = S7pFxCodec.ToolSubtype(S7Conventions.ToolSource.Hand, cls);
                    }
                }
                else continue;
                Vector3D d = tool.PositionComp.WorldMatrixRef.Forward;
                var r = new S7EventRow
                {
                    type = type,
                    flags = (byte)((hit ? EventFlags.Hit : 0) | (c.EntityId == _localCharacter ? EventFlags.Local : 0)),
                    key = S7EventKeys.HandTool(tool.EntityId),
                    intensity = S7EventRules.ToolIntensity(hit),
                    size = size,
                    subtype = subtype,
                    tickLow = (uint)tick,
                };
                Place(ref r, p, d, c.EntityId == _localCharacter ? c : null);
                if (type == EventType.Drill && hit && S7pFxCodec.ContactOf(subtype) != S7pFxConventions.DrillContact.None)
                {
                    // the contact point on the quantum grid of the row's frame (attached: the character; else SN-world)
                    float q = S7pFxConventions.S7pLimits.ContactQuantumCm / 100f;
                    r.pos = new F32x3(S7pFxCodec.Quantise(r.pos.x, q), S7pFxCodec.Quantise(r.pos.y, q), S7pFxCodec.Quantise(r.pos.z, q));
                }
                if (Sustains.Observe(ref r, tick, refresh) != SustainEmit.None)
                {
                    if (type == EventType.Drill) _s7pContactRows++;
                    Raise(ref r);
                }
            }
        }

        /// <summary>The splash test of the S5 set's root grids and floating objects (CONTRACT type 13).</summary>
        private static void PollSplash()
        {
            Vec3 c = _site.C;
            var cc = new Vector3D(c.X, c.Y, c.Z);
            foreach (GridMirrorProducer.MirroredGrid g in GridMirrorProducer.Grids.Values)
            {
                if (g.Subgrid) continue;
                MyEntity e = g.Grid != null ? (MyEntity)g.Grid : g.Entity;
                if (e == null || e.Closed || e.Physics == null) continue;
                BoundingBoxD box = e.PositionComp.WorldAABB;
                Vector3D centre = box.Center;
                if (Vector3D.Distance(centre, _cam) > S7Conventions.EventLimits.EffectRadiusM + box.HalfExtents.Length()) continue;
                Vector3D up = Vector3D.Normalize(centre - cc);
                Vector3D ext = box.HalfExtents;
                double h = Math.Abs(ext.X * up.X) + Math.Abs(ext.Y * up.Y) + Math.Abs(ext.Z * up.Z);
                double bottom = SnY(centre) - h;
                Vector3 lv = e.Physics.LinearVelocity;
                double vy = lv.X * up.X + lv.Y * up.Y + lv.Z * up.Z;
                double prev;
                bool had = _splashPrev.TryGetValue(g.Id, out prev);
                _splashPrev[g.Id] = bottom;
                if (!had || !S7EventRules.SplashCrossing(prev, bottom, vy)) continue;
                Vec3 sn = _site.SeToSn(new Vec3(centre.X, centre.Y, centre.Z));
                double horiz = Math.Sqrt(Math.Max(0.0, ext.LengthSquared() - h * h));
                var r = new S7EventRow
                {
                    type = EventType.Splash,
                    flags = (byte)(S7EventRules.WaterFlags(0.0) | LocalFlag(g.Grid)),
                    pos = new F32x3((float)sn.X, 0f, (float)sn.Z),
                    dir = new F32x3(0f, 1f, 0f),
                    intensity = S7EventRules.SplashIntensity(vy),
                    size = (float)horiz,
                    subtype = (ushort)(vy < 0 ? 1 : 2), // 1 entering the sea, 2 leaving it
                    lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.Splash),
                    tickLow = (uint)_tick,
                };
                Raise(ref r);
            }
            if (_splashPrev.Count > 4096) _splashPrev.Clear();
        }

        // ============================================================================================ postfix sources

        internal static void OnShoot(MyGunBase gun, Vector3D origin, Vector3 direction, MyEntity owner)
        {
            if (!_active || S7Mutants.M12 || gun == null) return;
            MyAmmoDefinitionView ammo;
            if (!AmmoOf(gun, out ammo)) return;
            if (Vector3D.Distance(origin, _cam) > S7Conventions.EventLimits.EffectRadiusM) { Interlocked.Increment(ref _outOfRange); return; }
            MyEntity weapon = gun.User != null ? gun.User.Weapon : null;
            MyEntity attach = AttachTarget(weapon) ?? AttachTarget(owner);
            byte local = LocalFlagOf(weapon ?? owner);
            Vector3D d = Vector3D.Normalize((Vector3D)direction);
            // S7p-effects-under (P5): the muzzle flash scale of the weapon (large grid 1 m, small grid 0.5 m, hand 0.3 m)
            var weaponBlock = weapon as MyCubeBlock;
            float muzzleSize = S7pFxCodec.MuzzleSize(weaponBlock != null && weaponBlock.CubeGrid != null,
                                                     weaponBlock != null && weaponBlock.CubeGrid != null && weaponBlock.CubeGrid.GridSizeEnum == MyCubeSize.Large);
            var muzzle = new S7EventRow
            {
                type = EventType.WeaponMuzzle,
                flags = local,
                intensity = 1f,
                size = muzzleSize,
                subtype = ammo.Missile ? S7Conventions.WeaponKind.Missile : S7Conventions.WeaponKind.Projectile,
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.WeaponMuzzle),
                tickLow = NowTick(),
            };
            Place(ref muzzle, origin, d, attach);
            Raise(ref muzzle);
            if (ammo.Missile) return;
            float length = S7EventRules.StreakLength(ammo.MaxTrajectory);
            // S7p-effects-under (P5): the tracer ends at the hit (muzzle -> hit): the first surface along the shot that is
 // not the shooter, by SE's own ray (the projectile's layer 15), in extra (cm) with the Hit flag; size and
 // lifetimeMs keep their S7 meaning for every other reader (ext s7p fx 1, format event.streak)
            float hitDistance;
            MyEntity shooterTop = weapon != null ? weapon.GetTopMostParent() : owner != null ? owner.GetTopMostParent() : null;
            bool hit = StreakRay(origin, d, length, shooterTop, out hitDistance);
            var streak = new S7EventRow
            {
                type = EventType.BulletStreak,
                flags = (byte)(local | (hit ? EventFlags.Hit : 0)),
                intensity = S7EventRules.StreakIntensity(ammo.ProjectileCount),
                size = length,
                subtype = S7Conventions.WeaponKind.Projectile,
                lifetimeMs = S7EventRules.StreakLifetimeMs(length, ammo.DesiredSpeed),
                extra = hit ? S7pFxCodec.EncodeHitCm(Math.Max(0.01f, hitDistance)) : (ushort)0,
                tickLow = muzzle.tickLow,
            };
            Place(ref streak, origin, d, attach);
            Raise(ref streak);
        }

        /// <summary>
 /// S7p-effects-under (P5): the shot's hit distance (m) along <paramref name="dir"/> within <paramref name="length"/>:
 /// <c>MyPhysics.CastRay(Vector3D, Vector3D, List&lt;HitInfo&gt;, int)</c> on layer 15
 /// the nearest hit whose top-most entity is not the shooter's. Only on the update thread, at
 /// most <c>StreakRaysPerTick</c> per tick (the rest keep the S7 length, counted); switch <c>ray</c> off: none.
 /// </summary>
        private static bool StreakRay(Vector3D origin, Vector3D dir, float length, MyEntity shooterTop, out float hitDistance)
        {
            hitDistance = length;
            if (!S7p.On(S7pFxSettings.Knob.Ray) || !(length > 0)) return false;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) { Interlocked.Increment(ref _s7pRaysOffThread); return false; }
            if (_rayTick != _tick) { _rayTick = _tick; _raysThisTick = 0; }
            if (_raysThisTick >= S7pFxConventions.S7pLimits.StreakRaysPerTick) { _s7pRaysSkipped++; return false; }
            _raysThisTick++;
            _s7pRays++;
            double best = double.MaxValue;
            try
            {
                _rayHits.Clear();
                MyPhysics.CastRay(origin, origin + dir * length, _rayHits, S7pFxConventions.S7pLimits.StreakRayLayer);
                for (int i = 0; i < _rayHits.Count; i++)
                {
                    IMyEntity he = null;
                    try { he = _rayHits[i].HkHitInfo.GetHitEntity(); } catch (Exception) { }
                    if (he != null && shooterTop != null && ReferenceEquals(he.GetTopMostParent(), shooterTop)) continue;
                    double dist = Vector3D.Distance(origin, _rayHits[i].Position);
                    if (dist < best) best = dist;
                }
            }
            catch (Exception e)
            {
                _s7pRayErrors++;
                Once("s7p.ray", "DE.S7p fx streak ray error " + e.GetType().Name + ": " + e.Message + " (the S7 length; logged once)");
                return false;
            }
            if (best >= length) return false;
            hitDistance = (float)best;
            _s7pRayHits++;
            return true;
        }

        internal static void OnProjectileHit(IMyEntity entity, Vector3D position, Vector3 normal)
        {
            if (!_active || entity == null) return;
            if (Vector3D.Distance(position, _cam) > S7Conventions.EventLimits.EffectRadiusM) { Interlocked.Increment(ref _outOfRange); return; }
            IMyEntity top = entity.GetTopMostParent();
            ushort surface = (ushort)(top is MyCubeGrid ? 1 : top is MyVoxelBase ? 2 : 0);
            var r = new S7EventRow
            {
                type = EventType.Impact,
                intensity = 1f,
                size = 0.5f,
                subtype = surface,
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.Impact),
                tickLow = NowTick(),
            };
            Place(ref r, position, Vector3D.Normalize((Vector3D)normal), AttachTarget(top as MyEntity));
            Raise(ref r);
        }

        internal static void OnExplosion(BoundingSphereD sphere, byte explosionType, float damage, ushort s11Extra = 0, ushort s11Class = 0) // S11 hook: the group's summed yield and its weapon class (S11Blasts)
        {
            if (!_active) return;
            if (Vector3D.Distance(sphere.Center, _cam) > S7Conventions.EventLimits.ExplosionRadiusM) { Interlocked.Increment(ref _outOfRange); return; }
            var r = new S7EventRow
            {
                type = EventType.Explosion,
                intensity = S7EventRules.ExplosionIntensity(damage),
                size = (float)sphere.Radius,
                subtype = (ushort)(explosionType | s11Class), // S11 hook: bits 8-11 = the weapon class (format event.class; 0 without S11)
                extra = s11Extra, // S11 hook: CONTRACT §5.4 format event.yield (0 without S11)
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.Explosion),
                tickLow = NowTick(),
            };
            Place(ref r, sphere.Center, Vector3D.Zero, null);
            Raise(ref r);
        }

        internal static void OnGridCollision(MyCubeGrid grid, Vector3D position, Vector3 normal, float separatingSpeed, float impulse)
        {
            if (!_active) return;
            if (Vector3D.Distance(position, _cam) > S7Conventions.EventLimits.EffectRadiusM) { Interlocked.Increment(ref _outOfRange); return; }
            var r = new S7EventRow
            {
                type = EventType.GridImpact,
                flags = LocalFlagOf(grid),
                intensity = S7EventRules.GridImpactIntensity(separatingSpeed),
                size = S7EventRules.GridImpactSize(impulse),
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.GridImpact),
                tickLow = NowTick(),
            };
            Place(ref r, position, Vector3D.Normalize((Vector3D)normal), AttachTarget(grid));
            Raise(ref r);
        }

        internal static void OnDepressurization(MyCubeGrid grid, Vector3I from, Vector3I to)
        {
            if (!_active || grid == null || from == to || grid.MarkedForClose) return;
            Vector3D wFrom = grid.GridIntegerToWorld(from);
            if (Vector3D.Distance(wFrom, _cam) > S7Conventions.EventLimits.EffectRadiusM) { Interlocked.Increment(ref _outOfRange); return; }
            Vector3D wTo = grid.GridIntegerToWorld(to);
            var r = new S7EventRow
            {
                type = EventType.HullBreak,
                flags = LocalFlagOf(grid),
                intensity = 1f,
                size = grid.GridSize,
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.HullBreak),
                tickLow = NowTick(),
            };
            Place(ref r, wFrom, Vector3D.Normalize(wTo - wFrom), AttachTarget(grid));
            Raise(ref r);
        }

        /// <summary>
 /// The cheap gate the postfixes test before any math.
 /// </summary>
        internal static bool Active { get { return _active; } }

        internal static void PostfixError(string which, Exception e)
        {
            Interlocked.Increment(ref _postfixErrors);
            Once("postfix." + which, "DE.S7 fx postfix " + which + " error " + e.GetType().Name + ": " + e.Message + " (that event dropped; logged once)\n" + e.StackTrace);
        }

        // ============================================================================================ frames

 /// <summary>
 /// Fills pos/dir and the water flags: attached (SE-local of <paramref name="attach"/>, the Attached flag and its
 /// id) or SN-world (F, mapdir) when <paramref name="attach"/> is null. <paramref name="worldDir"/> zero = none.
 /// </summary>
        private static void Place(ref S7EventRow r, Vector3D worldPos, Vector3D worldDir, MyEntity attach)
        {
            SiteMap site = _site;
            if (site == null) return;
            r.flags = (byte)(r.flags | S7EventRules.WaterFlags(SnY(worldPos)));
            bool hasDir = worldDir.LengthSquared() > 1e-12;
            if (attach != null)
            {
                MatrixD inv = attach.PositionComp.WorldMatrixNormalizedInv;
                Vector3D lp = Vector3D.Transform(worldPos, inv);
                r.entityId = (ulong)attach.EntityId;
                r.flags |= EventFlags.Attached;
                r.pos = new F32x3((float)lp.X, (float)lp.Y, (float)lp.Z);
                if (hasDir)
                {
                    Vector3D ld = Vector3D.Normalize(Vector3D.TransformNormal(worldDir, inv));
                    r.dir = new F32x3((float)ld.X, (float)ld.Y, (float)ld.Z);
                }
                return;
            }
            var p = new Vec3(worldPos.X, worldPos.Y, worldPos.Z);
            Vec3 sn = site.SeToSn(p);
            r.entityId = 0;
            r.pos = new F32x3((float)sn.X, (float)sn.Y, (float)sn.Z);
            if (hasDir)
            {
                Vec3 sd = site.DirSeToSn(p, new Vec3(worldDir.X, worldDir.Y, worldDir.Z));
                r.dir = new F32x3((float)sd.X, (float)sd.Y, (float)sd.Z);
            }
        }

        /// <summary>
 /// The entity an event attaches to: the top-most parent when it is a grid Subnautica mirrors (root or subgrid;
 /// its GridAdd is in the ring) or the C1 character; else null (world). Off the update thread: always null.
 /// </summary>
        private static MyEntity AttachTarget(MyEntity e)
        {
            if (e == null) return null;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) { Interlocked.Increment(ref _offThread); return null; }
            MyEntity top = e.GetTopMostParent();
            if (top == null || top.Closed) return null;
            if (top is MyCubeGrid && GridMirrorProducer.SentToSn(top.EntityId)) return top;
            if (top is MyCharacter && top.EntityId == _localCharacter && _localCharacter != 0) return top;
            Interlocked.Increment(ref _notMirrored); // review F23: also reached from Havok worker threads (grid contacts)
            return null;
        }

        private static byte LocalFlag(MyCubeGrid grid)
        {
            if (grid == null || _localSeatRoot == 0) return 0;
            return EntitySet.RootOf(grid) == _localSeatRoot ? EventFlags.Local : (byte)0;
        }

        private static byte LocalFlagOf(MyEntity e)
        {
            if (e == null) return 0;
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return 0;
            MyEntity top = e.GetTopMostParent();
            if (top == null) return 0;
            if (top.EntityId == _localCharacter && _localCharacter != 0) return EventFlags.Local;
            var g = top as MyCubeGrid;
            return g != null ? LocalFlag(g) : (byte)0;
        }

        /// <summary>SN y of an SE world point: |P − C| − Rs + O.y (exactly F's y).</summary>
        private static double SnY(Vector3D p)
        {
            SiteMap s = _site;
            if (s == null) return 0;
            double dx = p.X - s.C.X, dy = p.Y - s.C.Y, dz = p.Z - s.C.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) - s.Rs + s.O.Y;
        }

        private static void Raise(ref S7EventRow r)
        {
            if (S7Mutants.M15 && (r.flags & EventFlags.Attached) != 0)
            {
                // S7.M15: an attached event sent as a world event, its SE-local position read as SN-world
                r.entityId = 0;
                r.flags = (byte)(r.flags & ~EventFlags.Attached);
            }
            if (r.tickLow == 0) r.tickLow = (uint)_tick;
            if ((r.flags & EventFlags.Sustain) != 0 && EventType.IsValid(r.type)) _sustainRows[r.type]++;
            Window.TryAdd(r);
        }

        private static uint NowTick()
        {
            ulong t = GameState.GameplayTick;
            return (uint)(t != 0 ? t : _tick);
        }

        private static F32x3 F3(Vector3 v) { return new F32x3(v.X, v.Y, v.Z); }

        private static Vector3 Unit(Vector3 v)
        {
            float l = v.Length();
            return l > 1e-6f ? v / l : Vector3.Zero;
        }

        private struct MyAmmoDefinitionView
        {
            public bool Missile;
            public float DesiredSpeed, MaxTrajectory;
            public int ProjectileCount;
        }

        private static bool AmmoOf(MyGunBase gun, out MyAmmoDefinitionView v)
        {
            v = default(MyAmmoDefinitionView);
            var wp = gun.WeaponProperties;
            var ammo = wp != null ? wp.AmmoDefinition : null;
            if (ammo == null) return false;
            if (ammo.AmmoType == MyAmmoType.Missile) v.Missile = true;
            else if (ammo.AmmoType != MyAmmoType.HighSpeed) return false;
            v.DesiredSpeed = ammo.DesiredSpeed;
            v.MaxTrajectory = ammo.MaxTrajectory;
            var pa = ammo as Sandbox.Definitions.MyProjectileAmmoDefinition;
            v.ProjectileCount = pa != null ? pa.ProjectileCount : 1;
            return true;
        }

        private static void Once(string key, string line)
        {
            lock (_loggedOnce)
            {
                if (!_loggedOnce.Add(key)) return;
            }
            DeLog.Info(line);
        }

        // ============================================================================================ rows

 /// <summary>
 /// The s7.fx row (1 Hz, on a tick with drops, and the stress tick): per-type raised/sent/dropped since the previous
 /// row (the analyzer sums rows over a window) and their totals, the window, the stream, the sustain rows per type
 /// since the previous row and the live sustain keys per type, the SE ticks the row covers. <paramref name="stress"/>
 /// adds the stress tick's numbers (accepted, droppedStress, perType, nextTickPending, oldestTicks).
 /// </summary>
        private static void Row(string why, Dictionary<string, object> stress)
        {
            var sb = new StringBuilder(1536);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.Append(",\"ev\":\"s7.fx\"");
            S5Rows.Int(sb, "tick", (long)_tick);
            S5Rows.Str(sb, "why", why);
            S5Rows.Bool(sb, "active", _active);
            if (!_active) S5Rows.Str(sb, "inactive", _inactiveWhy);
            Delta(sb, "raised", Window.Raised, _prevRaised);
            Delta(sb, "sent", Window.Accepted, _prevSent);
            Delta(sb, "dropped", Window.Dropped, _prevDropped);
            Delta(sb, "sustainRows", _sustainRows, _prevSustainRows);
            Sustains.CountByType(_sustainKeys);
            PerType(sb, "sustainKeys", _sustainKeys);
            PerType(sb, "raisedTotal", Window.Raised);
            PerType(sb, "sentTotal", Window.Accepted);
            PerType(sb, "droppedTotal", Window.Dropped);
            S5Rows.Int(sb, "ticks", _lastRowTick != 0 && _tick > _lastRowTick ? (long)(_tick - _lastRowTick) : 0);
            _lastRowTick = _tick;
            // review F18c: measured by the tracker (ticks from a source's >= 0.05 strength change to the row carrying it,
 // closed and still waiting), no longer a constant
            S5Rows.Int(sb, "stepLagMaxTicks", (long)Sustains.TakeStepLagMax(_tick));
            S5Rows.Int(sb, "stepLagSamples", Sustains.StepLagSamples);
            sb.Append(",\"lastTick\":{\"raised\":").Append(Window.LastRaisedTotal.ToString(CultureInfo.InvariantCulture))
              .Append(",\"sent\":").Append(Window.LastAcceptedTotal.ToString(CultureInfo.InvariantCulture))
              .Append(",\"dropped\":").Append(Window.LastDroppedTotal.ToString(CultureInfo.InvariantCulture)).Append('}');
            if (stress != null)
            {
                S5Rows.Int(sb, "accepted", Convert.ToInt64(stress["accepted"], CultureInfo.InvariantCulture));
                S5Rows.Int(sb, "droppedStress", Convert.ToInt64(stress["dropped"], CultureInfo.InvariantCulture));
                PerType(sb, "perType", Window.LastAccepted);
                PerType(sb, "perTypeDropped", Window.LastDropped);
                S5Rows.Int(sb, "nextTickPending", Convert.ToInt64(stress["nextTickPending"], CultureInfo.InvariantCulture));
                S5Rows.Int(sb, "oldestTicks", Convert.ToInt64(stress["oldestTicks"], CultureInfo.InvariantCulture));
            }
            S5Rows.Int(sb, "window", _rowCount);
            S5Rows.Int(sb, "windowTicks", Window.WindowTicks);
            S5Rows.Int(sb, "stream", Window.Stream);
            S5Rows.Int(sb, "firstSeq", Window.FirstSeq);
            S5Rows.Int(sb, "eventsDropped", Window.EventsDropped);
            S5Rows.Int(sb, "invalid", Window.Invalid);
            S5Rows.Int(sb, "sustains", Sustains.Count);
            S5Rows.Int(sb, "grids", _grids.Count);
            S5Rows.Int(sb, "characters", _characters.Count);
            S5Rows.Int(sb, "offThread", Interlocked.Read(ref _offThread));
            S5Rows.Int(sb, "notMirrored", Interlocked.Read(ref _notMirrored));
            S5Rows.Int(sb, "outOfRange", Interlocked.Read(ref _outOfRange));
            S5Rows.Int(sb, "postfixErrors", Interlocked.Read(ref _postfixErrors));
            S5Rows.Num(sb, "ms", _msLast);
            S5Rows.Num(sb, "msMax", _msMax);
            S5Rows.Str(sb, "patches", S7EffectPatches.Describe());
            S5Rows.Str(sb, "mutants", S7Mutants.Active);
            // S7p-effects-under (CONTRACT §5.4 "S7p effects-under", rows): the producer's S7p counters (cumulative) and
 // the switches that differ from their defaults
            S5Rows.Int(sb, "s7pColoured", _s7pColoured);
            S5Rows.Int(sb, "s7pNoColour", _s7pNoColour);
            S5Rows.Int(sb, "s7pRays", _s7pRays);
            S5Rows.Int(sb, "s7pRayHits", _s7pRayHits);
            S5Rows.Int(sb, "s7pRaysSkipped", _s7pRaysSkipped);
            S5Rows.Int(sb, "s7pRaysOffThread", Interlocked.Read(ref _s7pRaysOffThread));
            S5Rows.Int(sb, "s7pRayErrors", _s7pRayErrors);
            S5Rows.Int(sb, "s7pContactRows", _s7pContactRows);
            S5Rows.Int(sb, "s7pContactLookups", _s7pContactLookups);
            S5Rows.Int(sb, "s7pContactSkipped", _s7pContactSkipped);
            S5Rows.Int(sb, "s7pContactErrors", _s7pContactErrors);
            S5Rows.Str(sb, "s7p", S7p.Changed());
            sb.Append('}');
            Jsonl.WriteLineBuffered(sb.ToString());
            _msMax = 0;
        }

        private static void Delta(StringBuilder sb, string name, long[] now, long[] prev)
        {
            sb.Append(",\"").Append(name).Append("\":{");
            bool first = true;
            for (int t = 1; t < now.Length; t++)
            {
                long d = now[t] - prev[t];
                prev[t] = now[t];
                if (d == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(EventType.Name(t)).Append("\":").Append(d.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        private static void PerType(StringBuilder sb, string name, long[] counts)
        {
            sb.Append(",\"").Append(name).Append("\":{");
            bool first = true;
            for (int t = 1; t < counts.Length; t++)
            {
                if (counts[t] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(EventType.Name(t)).Append("\":").Append(counts[t].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        private static void PerType(StringBuilder sb, string name, int[] counts)
        {
            sb.Append(",\"").Append(name).Append("\":{");
            bool first = true;
            for (int t = 1; t < counts.Length; t++)
            {
                if (counts[t] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(EventType.Name(t)).Append("\":").Append(counts[t].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        /// <summary>
 /// s7.fx.event rows for the events this tick accepted (the newest <paramref name="added"/> rows of the window): every
 /// one-shot (weapons, impacts, explosions, grid impacts, hull breaks, splashes; at most 256 rows per second), and the
 /// sustain rows too while <c>de.fx {action: trace}</c> runs. Fields: tick, seq, stream, type, flags, entity, key,
 /// pos/dir as sent, posSn (SN-world: attached ones mapped through the entity's current world matrix and F), size,
 /// intensity, subtype, lifetimeMs, tickLow.
 /// </summary>
        private static void EventRows(ulong tick, ulong nowMs, int added, bool tracing)
        {
            if (added <= 0) return;
            if (nowMs >= _eventRowSecondMs + 1000) { _eventRowSecondMs = nowMs; _eventRowsThisSecond = 0; }
            int from = Math.Max(0, _rowCount - added);
            SiteMap site = _site;
            string utc = null;
            for (int i = from; i < _rowCount; i++)
            {
                S7EventRow r = _rows[i];
                if (r.IsSustain && !tracing) continue;
                if (_eventRowsThisSecond >= EventRowsPerSecond) return;
                _eventRowsThisSecond++;
                if (utc == null) utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var sb = new StringBuilder(512);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
                sb.Append(",\"ev\":\"s7.fx.event\"");
                S5Rows.Int(sb, "tick", (long)tick);
                S5Rows.Int(sb, "seq", r.seq);
                S5Rows.Int(sb, "stream", Window.Stream);
                S5Rows.Str(sb, "type", EventType.Name(r.type));
                S5Rows.Int(sb, "flags", r.flags);
                S5Rows.Bool(sb, "attached", r.Attached);
                S5Rows.Bool(sb, "sustain", r.IsSustain);
                S5Rows.Bool(sb, "stop", r.IsStop);
                S5Rows.Str(sb, "entity", r.entityId.ToString(CultureInfo.InvariantCulture));
                S5Rows.Str(sb, "key", "0x" + r.key.ToString("x16", CultureInfo.InvariantCulture));
                sb.Append(",\"pos\":[").Append(S3Rows.G(r.pos.x)).Append(',').Append(S3Rows.G(r.pos.y)).Append(',').Append(S3Rows.G(r.pos.z)).Append(']');
                sb.Append(",\"dir\":[").Append(S3Rows.G(r.dir.x)).Append(',').Append(S3Rows.G(r.dir.y)).Append(',').Append(S3Rows.G(r.dir.z)).Append(']');
                Vec3 sn;
                if (site != null && SnWorld(site, r, out sn))
                    sb.Append(",\"posSn\":[").Append(S3Rows.G(sn.X)).Append(',').Append(S3Rows.G(sn.Y)).Append(',').Append(S3Rows.G(sn.Z)).Append(']');
                S5Rows.Num(sb, "intensity", r.intensity);
                S5Rows.Num(sb, "size", r.size);
                S5Rows.Int(sb, "subtype", r.subtype);
                S5Rows.Int(sb, "lifetimeMs", r.lifetimeMs);
                S5Rows.Int(sb, "tickLow", r.tickLow);
                sb.Append('}');
                Jsonl.WriteLineBuffered(sb.ToString());
            }
        }

        private static bool SnWorld(SiteMap site, S7EventRow r, out Vec3 sn)
        {
            sn = new Vec3(r.pos.x, r.pos.y, r.pos.z);
            if (!r.Attached) return true;
            MyEntity e = EntitySet.EntityOf((long)r.entityId);
            if (e == null || e.Closed) return false;
            Vector3D w = Vector3D.Transform(new Vector3D(r.pos.x, r.pos.y, r.pos.z), e.PositionComp.WorldMatrixRef);
            sn = site.SeToSn(new Vec3(w.X, w.Y, w.Z));
            return true;
        }

        // ============================================================================================ de.fx

 /// <summary>
 /// <c>de.fx</c> (C11 and chat; dev builds; CONTRACT §5.4 "S7 control commands"): <c>{action: status}</c> (default),
 /// <c>{action: trace, seconds}</c> (s7.fx.ev rows), <c>{action: stress, n}</c> (n synthetic one-shots in this tick's
 /// buffer: the S7.N4 rates check), <c>{inject: type, at: [x,y,z] SN-world, dir?, intensity?, size?, lifetimeMs?,
 /// entity?, key?, flags?}</c> (one synthetic event: the Subnautica path alone), <c>{thrust: entity, override: 0..1}</c>
 /// (<c>ThrustOverridePercentage</c> on every thruster of that grid), <c>{gun: entity, shots: n}</c>
 /// (<c>ShootOnce</c> n times, one every 10 ticks; a grid id shoots every gun on it), <c>{tool: block, on: bool}</c>
 /// (<c>Enabled</c>). Returns null for another command (chain it after <c>S7Commands</c>). Update thread.
 /// </summary>
        internal static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd == S7pCmd) return ExecuteS7p(args, via); // S7p-effects-under: de.s7p {fx …} (null for the other S7p keys)
            if (cmd != Cmd) return null;
            try
            {
                if (args == null) args = new Dictionary<string, object>(StringComparer.Ordinal);
                if (args.ContainsKey("inject")) return Inject(args);
                if (args.ContainsKey("thrust")) return Thrust(args);
                if (args.ContainsKey("gun")) return Gun(args);
                if (args.ContainsKey("tool")) return Tool(args);
                if (args.ContainsKey("s7p")) return S7pCommand(args);
                string action = (MiniJson.GetString(args, "action") ?? "status").Trim().ToLowerInvariant();
                switch (action)
                {
                    case "status": return Status();
                    case "trace":
                    {
                        double s = MiniJson.GetNumber(args, "seconds") ?? 10;
                        s = Math.Max(0, Math.Min(600, s));
                        _traceUntilMs = Win32.TickCount64() + (ulong)(s * 1000);
                        Dictionary<string, object> d = Status();
                        d["trace"] = s;
                        return d;
                    }
                    case "stress":
                    {
                        double n = MiniJson.GetNumber(args, "n") ?? 500;
                        double ticks = MiniJson.GetNumber(args, "ticks") ?? 1;
                        int ni = (int)Math.Max(0, Math.Min(S7EventWindow.CandidateCapacity, n));
                        // review F10: {ticks: k > 1} spreads the n events over k consecutive ticks (raised from BeforeC2)
                        if (ticks > 1) return StressBurst(ni, (int)Math.Min(StressBurstMaxTicks, Math.Max(2, ticks)));
                        return Stress(ni);
                    }
                    default:
                        return Fail("de.fx: unknown action '" + action + "' (status | trace | stress; or {inject} | {thrust} | {gun} | {tool})");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.fx " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.fx failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The status object (also for <c>de.s7 effects</c> / S7Commands' status).</summary>
        internal static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["ok"] = true;
            d["active"] = _active;
            if (!_active) d["inactive"] = _inactiveWhy;
            d["tick"] = (long)_tick;
            d["window"] = _rowCount;
            d["windowTicks"] = Window.WindowTicks;
            d["capsOff"] = Window.CapsOff;
            d["stream"] = "0x" + Window.Stream.ToString("x8", CultureInfo.InvariantCulture);
            d["firstSeq"] = (long)Window.FirstSeq;
            d["eventsDropped"] = (long)Window.EventsDropped;
            d["sustains"] = Sustains.Count;
            d["sustainStarts"] = Sustains.Starts;
            d["sustainStops"] = Sustains.Stops;
            d["sustainOverflow"] = Sustains.Overflow;
            d["raised"] = TypeDict(Window.Raised);
            d["sent"] = TypeDict(Window.Accepted);
            d["dropped"] = TypeDict(Window.Dropped);
            d["grids"] = _grids.Count;
            d["characters"] = _characters.Count;
            d["patches"] = S7EffectPatches.Describe();
            d["patchesMissing"] = string.Join("; ", S7EffectPatches.Missing.ToArray());
            d["msLast"] = _msLast;
            d["tracing"] = Win32.TickCount64() < _traceUntilMs;
            d["mutants"] = S7Mutants.Active;
            d["line"] = "DE.S7 fx " + (_active ? "active" : "inactive (" + _inactiveWhy + ")") + ": window " + _rowCount + " rows, stream 0x" +
                        Window.Stream.ToString("x8", CultureInfo.InvariantCulture) + ", sustains " + Sustains.Count + ", dropped " + Window.EventsDropped +
                        ", patches " + S7EffectPatches.Describe();
            try { TestShip(d); } catch (Exception) { } // a status reply never fails on the ship lookup
            S7pStatus(d);
            return d;
        }

        /// <summary>S7p-effects-under: the producer's S7p numbers in the <c>de.fx</c> status.</summary>
        private static void S7pStatus(Dictionary<string, object> d)
        {
            d["s7pExt"] = S7pFxConventions.Header + " 0x" + S7pFxConventions.Hash.ToString("x16", CultureInfo.InvariantCulture) +
                          (S7pFxConventions.Hash == S7pFxConventions.PinnedHash ? " pinned" : " MISMATCH");
            d["s7pSettings"] = S7p.Describe();
            if (_s7pEnvError != null) d["s7pEnvError"] = _s7pEnvError;
            d["s7pMaxThrust"] = (double)_maxThrust;
            d["s7pColoured"] = _s7pColoured;
            d["s7pNoColour"] = _s7pNoColour;
            d["s7pRays"] = _s7pRays;
            d["s7pRayHits"] = _s7pRayHits;
            d["s7pRaysSkipped"] = _s7pRaysSkipped;
            d["s7pRaysOffThread"] = Interlocked.Read(ref _s7pRaysOffThread);
            d["s7pRayErrors"] = _s7pRayErrors;
            d["s7pContactRows"] = _s7pContactRows;
            d["s7pContactLookups"] = _s7pContactLookups;
            d["s7pContactSkipped"] = _s7pContactSkipped;
            d["s7pContactErrors"] = _s7pContactErrors;
        }

        public const string S7pCmd = "de.s7p";

        /// <summary>
 /// S7p-effects-under: the shared verb <c>de.s7p</c>, its <c>fx</c> part (<see cref="S7pFxSettings.Tokens"/>; null for
 /// every other S7p key, so the chain asks the next feature): <c>/de s7p fx [status | reset | &lt;type&gt; &lt;scale&gt; |
 /// type=scale …]</c>. SE applies its switches (colour, ray, contact; the look scales are kept here too, unused), and
 /// from chat relays the same tokens to Subnautica (<c>de.s7p {fx: "…"}</c>), whose copy draws the looks; the harness
 /// sends C11 <c>de.s7p</c> to each side itself.
 /// </summary>
        private static Dictionary<string, object> ExecuteS7p(IDictionary<string, object> args, string via)
        {
            bool mine;
            List<string> tokens = S7pFxSettings.Tokens(args, out mine);
            if (!mine) return null;
            string error;
            string action = S7p.Command(tokens, out error);
            if (action == "set" || action == "reset") DeLog.Info("DE.S7p fx producer settings " + action + " (de.s7p via " + via + "): " + S7p.Describe());
            string relayed = null;
            if (error == null && via == "chat" && action != "status")
            {
                var relay = S3Commands.RelayToSn;
                var ra = new Dictionary<string, object>(StringComparer.Ordinal) { { "fx", string.Join(" ", tokens.ToArray()) } };
                try { relayed = relay != null ? (relay(S7pCmd, ra, null) ?? "relayed") : "no relay"; }
                catch (Exception e) { relayed = "relay failed: " + e.GetType().Name; }
            }
            Dictionary<string, object> d = Status();
            d["ok"] = error == null;
            d["cmd"] = S7pCmd;
            d["verb"] = "fx";
            d["action"] = action ?? "error";
            if (error != null) d["error"] = error;
            if (relayed != null) d["relayToSn"] = relayed;
            d["line"] = "DE.S7p fx " + S7p.Describe() + (error != null ? " — " + error : "") + (relayed != null ? " (Subnautica: " + relayed + ")" : "");
            return d;
        }

        /// <summary>
 /// S7p-effects-under: <c>de.fx {s7p: "status" | "reset" | "colour=0,ray=1,contact=1"}</c> or <c>{s7p: {colour: 0,
 /// …}}</c>: the producer's switches (colour = the flame colour on the wire, ray = the streak's hit ray, contact = the
 /// drill's contact point and class), 0 off; the same keys as <c>DEEPENGINEERS_S7P_FX</c>. The look scales live
 /// on Subnautica's side (<c>de.s7p fx …</c>, chat <c>/de sn s7p fx &lt;type&gt; &lt;scale&gt;</c>).
 /// </summary>
        private static Dictionary<string, object> S7pCommand(IDictionary<string, object> args)
        {
            object v = args["s7p"];
            string error = null, action = "status";
            var dict = v as IDictionary<string, object>;
            if (dict != null)
            {
                var sb = new StringBuilder();
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    object x = kv.Value;
                    string val = x is bool ? ((bool)x ? "1" : "0") : Convert.ToString(x, CultureInfo.InvariantCulture);
                    sb.Append(kv.Key).Append('=').Append(val).Append(',');
                }
                error = S7p.ApplyText(sb.ToString());
                action = "set";
            }
            else
            {
                string text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "status";
                string t = text.Trim().ToLowerInvariant();
                if (t == "reset" || t == "default") { S7p.Reset(); action = "reset"; }
                else if (t != "status" && t.Length > 0) { error = S7p.ApplyText(text); action = "set"; }
            }
            if (action != "status") DeLog.Info("DE.S7p fx producer settings " + action + ": " + S7p.Describe() + (error != null ? " (" + error + ")" : ""));
            Dictionary<string, object> d = Status();
            d["ok"] = error == null;
            d["action"] = action;
            if (error != null) d["error"] = error;
            d["line"] = "DE.S7p fx producer " + S7p.Describe() + (error != null ? " — " + error : "");
            return d;
        }

        /// <summary>
 /// S7 integrator: the test-ship ids the s7 launcher's n4 reads when no <c>--ship-ids</c> is given
 /// (<c>thrusterGrid</c>, <c>gun</c>, <c>tools</c>; decimal strings: entity ids exceed a JSON double). The ship is the
 /// grid of this index (mirrored, within the effect radius) with the most thrusters, ties to the nearest; <c>gun</c>
 /// is that grid's id when it carries a user-controllable gun (<c>de.fx {gun}</c> takes a grid and queues every
 /// gun on it); <c>tools</c> names the first ship welder, grinder and drill of that grid by kind.
 /// </summary>
        private static void TestShip(Dictionary<string, object> d)
        {
            GridFx best = null;
            double bestDist = double.MaxValue;
            foreach (GridFx fx in _grids.Values)
            {
                if (fx.Grid == null || fx.Grid.Closed || fx.Thrusters.Count == 0) continue;
                double dist = fx.Grid.PositionComp.WorldAABB.Distance(_cam);
                if (best == null || fx.Thrusters.Count > best.Thrusters.Count || (fx.Thrusters.Count == best.Thrusters.Count && dist < bestDist))
                {
                    best = fx;
                    bestDist = dist;
                }
            }
            if (best != null)
            {
                d["thrusterGrid"] = best.Grid.EntityId.ToString(CultureInfo.InvariantCulture);
                d["shipThrusters"] = best.Thrusters.Count;
            }
            // S7-1 fix: the S7-1 run's thruster grid (a lab grid, 14 thrusters) had no gun and no ship tool, so

 // first, then on every other indexed grid (nearest to the camera first); the reply names where each one is
            var order = new List<GridFx>();
            if (best != null) order.Add(best);
            var rest = new List<GridFx>();
            foreach (GridFx fx in _grids.Values) if (fx != best && fx.Grid != null && !fx.Grid.Closed) rest.Add(fx);
            rest.Sort((a, b) => a.Grid.PositionComp.WorldAABB.Distance(_cam).CompareTo(b.Grid.PositionComp.WorldAABB.Distance(_cam)));
            order.AddRange(rest);
            var tools = new Dictionary<string, object>(StringComparer.Ordinal);
            var toolGrids = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (GridFx fx in order)
            {
                if (fx.Grid == null || fx.Grid.Closed) continue;
                string gid = fx.Grid.EntityId.ToString(CultureInfo.InvariantCulture);
                if (!d.ContainsKey("gun"))
                {
                    var blocks = fx.Grid.GetFatBlocks();
                    for (int i = 0; i < blocks.Count; i++)
                        if (blocks[i] is Sandbox.ModAPI.Ingame.IMyUserControllableGun) { d["gun"] = gid; d["gunOnThrusterGrid"] = fx == best; break; }
                }
                for (int k = 0; k < fx.Tools.Count; k++)
                {
                    ToolFx t = fx.Tools[k];
                    if (t.Block == null || t.Block.Closed) continue;
                    string kind = t.Type == EventType.Welder ? "Welder" : t.Type == EventType.Grinder ? "Grinder" : "PrawnDrill";
                    if (tools.ContainsKey(kind)) continue;
                    tools[kind] = t.Block.EntityId.ToString(CultureInfo.InvariantCulture);
                    toolGrids[kind] = gid;
                }
            }
            if (tools.Count > 0) { d["tools"] = tools; d["toolGrids"] = toolGrids; }
        }

        private static Dictionary<string, object> TypeDict(long[] c)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int t = 1; t < c.Length; t++) if (c[t] != 0) d[EventType.Name(t)] = c[t];
            return d;
        }

        // ---- review F10: the stress spread over k ticks (S7.N4.loss: the 8-tick window fills while Subnautica runs at 30 fps)
        public const int StressBurstMaxTicks = 64;
        private static int _burstLeft, _burstPerTick, _burstTicksLeft, _burstTicks, _burstI;
        private static long _burstAccepted, _burstDropped, _burstRaised;
        private static ulong _burstFromTick;

        /// <summary>
 /// <c>de.fx {action: stress, n, ticks: k}</c>: n synthetic one-shots raised over k consecutive
 /// ticks (ceil(n / k) per tick, from <see cref="BeforeC2"/> before the window closes the tick), so the window holds
 /// them for its 8 ticks while Subnautica reads every other SE tick (the launcher caps it to 30 fps): S7.N4.loss
 /// and, under S7.M17 (a 1-tick window), certain loss. The reply returns at once; an <c>s7.fx</c> row with why
 /// <c>stress.burst</c> carries the totals of the burst when it ends.
 /// </summary>
        private static Dictionary<string, object> StressBurst(int n, int ticks)
        {
            if (_site == null) return Fail("de.fx stress: no site map");
            _burstLeft = n;
            _burstTicks = ticks;
            _burstTicksLeft = ticks;
            _burstPerTick = (n + ticks - 1) / ticks;
            _burstI = 0;
            _burstAccepted = _burstDropped = _burstRaised = 0;
            _burstFromTick = _tick + 1;
            DeLog.Info("DE.S7 fx stress burst: " + n + " synthetic events over " + ticks + " ticks (" + _burstPerTick + " per tick) from tick " + _burstFromTick);
            Dictionary<string, object> d = Status();
            d["scheduled"] = true;
            d["n"] = n;
            d["ticks"] = ticks;
            d["perTick"] = _burstPerTick;
            d["fromTick"] = (long)_burstFromTick;
            return d;
        }

        /// <summary>The share of a burst for this tick, raised into the window before EndTick (BeforeC2).</summary>
        private static void BurstRaise(ulong tick)
        {
            if (_burstTicksLeft <= 0) return;
            SiteMap site = _site;
            int k = Math.Min(_burstPerTick, _burstLeft);
            if (site != null && k > 0)
            {
                Vec3 cam = site.SeToSn(new Vec3(_cam.X, _cam.Y, _cam.Z));
                for (int j = 0; j < k; j++, _burstI++)
                {
                    S7EventRow r = SyntheticStressRow(_burstI, cam, (uint)tick);
                    Window.TryAdd(r);
                }
                _burstLeft -= k;
                _burstRaised += k;
                _stressRaised += k;
            }
            _burstTicksLeft--;
        }

        /// <summary>After EndTick (BeforeC2): the accepted and dropped counts of the tick added; the totals row at the end.</summary>
        private static void BurstAfterEndTick(ulong tick)
        {
            if (_burstTicks <= 0) return;
            _burstAccepted += Window.LastAcceptedTotal;
            _burstDropped += Window.LastDroppedTotal;
            if (_burstTicksLeft > 0) return;
            ulong oldest = Window.OldestTick;
            var stress = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "accepted", _burstAccepted },
                { "dropped", _burstDropped },
                { "nextTickPending", (long)Window.Pending },
                { "oldestTicks", oldest != 0 && tick >= oldest ? (long)(tick - oldest) : 0L },
            };
            Row("stress.burst", stress);
            DeLog.Info("DE.S7 fx stress burst done: " + _burstRaised + " raised over " + _burstTicks + " ticks (" + _burstFromTick + ".." + tick + "), window accepted " +
                       _burstAccepted + ", dropped " + _burstDropped);
            _burstTicks = 0;
        }

        private static S7EventRow SyntheticStressRow(int i, Vec3 cam, uint tl)
        {
            byte type = (byte)(1 + i % 13);
            float x = (float)cam.X + (i % 10) - 5f, y = (float)cam.Y - 2f, z = (float)cam.Z + 5f + i / 10 % 10;
            return new S7EventRow
            {
                type = type,
                flags = S7EventRules.WaterFlags(y),
                pos = new F32x3(x, y, z),
                dir = new F32x3(0f, 1f, 0f),
                intensity = 1f,
                size = 1f,
                lifetimeMs = S7EventRules.OneShotLifetimeMs(type),
                tickLow = tl,
            };
        }

        /// <summary>
 /// The S7.N4 rates check, synchronously on the real window: the tick's earlier raises are closed first, then
 /// <paramref name="n"/> synthetic one-shots (types round robin, at the camera's SN point) are raised and the tick is
 /// closed again, so the reply and the <c>s7.fx</c> row (why "stress") carry exactly that tick's numbers: accepted
 /// (128 by priority and caps), dropped, perType, nextTickPending (the buffer after the tick: 0) and oldestTicks.
 /// The accepted rows stay in the window and reach Subnautica with the next C2 (the S7.N4.loss burst).
 /// </summary>
        private static Dictionary<string, object> Stress(int n)
        {
            SiteMap site = _site;
            if (site == null) return Fail("de.fx stress: no site map");
            Vec3 cam = site.SeToSn(new Vec3(_cam.X, _cam.Y, _cam.Z));
            ulong tick = GameState.GameplayTick;
            if (tick < _tick) tick = _tick;
            Window.EndTick(tick); // the sources' raises of this tick so far, closed apart from the stress batch
            uint tl = (uint)tick;
            int queued = 0;
            for (int i = 0; i < n; i++)
            {
                S7EventRow r = SyntheticStressRow(i, cam, tl);
                if (Window.TryAdd(r)) queued++;
            }
            int accepted = Window.EndTick(tick);
            _stressRaised += n;
            var perType = new Dictionary<string, object>(StringComparer.Ordinal);
            var perTypeDropped = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int t = 1; t < S7EventRules.TypeSlots; t++)
            {
                if (Window.LastAccepted[t] != 0) perType[EventType.Name(t)] = (long)Window.LastAccepted[t];
                if (Window.LastDropped[t] != 0) perTypeDropped[EventType.Name(t)] = (long)Window.LastDropped[t];
            }
            ulong oldest = Window.OldestTick;
            var stress = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "accepted", (long)accepted },
                { "dropped", (long)Window.LastDroppedTotal },
                { "nextTickPending", (long)Window.Pending },
                { "oldestTicks", oldest != 0 && tick >= oldest ? (long)(tick - oldest) : 0L },
            };
            _rowCount = Window.CopyRows(_rows);
            EventRows(tick, Win32.TickCount64(), accepted, false);
            Row("stress", stress);
            DeLog.Info("DE.S7 fx stress " + n + " synthetic events at tick " + tick + ": accepted " + accepted + ", dropped " + Window.LastDroppedTotal +
                       " (priority and caps), buffer " + Window.Pending + " after the tick");
            Dictionary<string, object> d = Status();
            foreach (KeyValuePair<string, object> kv in stress) d[kv.Key] = kv.Value;
            d["perType"] = perType;
            d["perTypeDropped"] = perTypeDropped;
            d["queued"] = queued;
            d["n"] = n;
            d["atTick"] = (long)tick;
            return d;
        }

        private static Dictionary<string, object> Inject(IDictionary<string, object> args)
        {
            object tv;
            args.TryGetValue("inject", out tv);
            byte type = 0;
            var ts = tv as string;
            if (ts != null)
            {
                for (int t = 1; t < S7EventRules.TypeSlots; t++)
                    if (string.Equals(EventType.Name(t), ts.Trim(), StringComparison.OrdinalIgnoreCase)) type = (byte)t;
                int ti;
                if (type == 0 && int.TryParse(ts, NumberStyles.Integer, CultureInfo.InvariantCulture, out ti) && EventType.IsValid(ti)) type = (byte)ti;
            }
            else if (tv is double && EventType.IsValid((int)(double)tv)) type = (byte)(double)tv;
            if (type == 0) return Fail("de.fx inject: a type name or 1..13");
            double[] at = Vec(args, "at");
            if (at == null)
            {
                SiteMap site = _site;
                if (site == null) return Fail("de.fx inject: at [x, y, z] (SN-world) or a site map for the camera point");
                Vec3 cam = site.SeToSn(new Vec3(_cam.X, _cam.Y, _cam.Z));
                at = new[] { cam.X, cam.Y - 3, cam.Z + 10 };
            }
            double[] dir = Vec(args, "dir") ?? new[] { 0.0, 1.0, 0.0 };
            long entity = 0;
            object ev;
            if (args.TryGetValue("entity", out ev) && !S5Args.TryEntityId(ev, out entity)) return Fail("de.fx inject: entity is an entity id (decimal string)");
            var r = new S7EventRow
            {
                type = type,
                pos = new F32x3((float)at[0], (float)at[1], (float)at[2]),
                dir = new F32x3((float)dir[0], (float)dir[1], (float)dir[2]),
                intensity = (float)(MiniJson.GetNumber(args, "intensity") ?? 1.0),
                size = (float)(MiniJson.GetNumber(args, "size") ?? 1.0),
                subtype = (ushort)(MiniJson.GetNumber(args, "subtype") ?? 0.0),
                lifetimeMs = S7EventRules.Ms(MiniJson.GetNumber(args, "lifetimeMs") ?? S7EventRules.OneShotLifetimeMs(type)),
                key = (ulong)(long)(MiniJson.GetNumber(args, "key") ?? 0.0),
                entityId = (ulong)entity,
                tickLow = NowTick(),
            };
            double? fl = MiniJson.GetNumber(args, "flags");
            r.flags = fl.HasValue ? (byte)fl.Value : (byte)((entity != 0 ? EventFlags.Attached : 0) | (entity == 0 ? S7EventRules.WaterFlags(at[1]) : 0));
            bool ok = Window.TryAdd(r);
            Dictionary<string, object> d = Status();
            d["ok"] = ok;
            d["injected"] = r.ToString();
            if (!ok) d["error"] = "refused (malformed row or the tick's buffer is full)";
            return d;
        }

        private static Dictionary<string, object> Thrust(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("thrust", out v);
            long id;
            if (!S5Args.TryEntityId(v, out id)) return Fail("de.fx thrust: a grid entity id (decimal string)");
            double o = MiniJson.GetNumber(args, "override") ?? 0;
            o = Math.Max(0, Math.Min(1, o));
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e) || !(e is MyCubeGrid)) return Fail("de.fx thrust: grid " + id + " not found");
            int n = 0;
            var blocks = ((MyCubeGrid)e).GetFatBlocks();
            for (int i = 0; i < blocks.Count; i++)
            {
                var t = blocks[i] as Sandbox.ModAPI.Ingame.IMyThrust;
                if (t == null) continue;
                t.ThrustOverridePercentage = (float)o;
                n++;
            }
            DeLog.Info("DE.S7 fx thrust override " + o.ToString("0.00", CultureInfo.InvariantCulture) + " on " + n + " thruster(s) of grid " + id + " (tick " + _tick + ")");
            Dictionary<string, object> d = Status();
            d["thrusters"] = n;
            d["override"] = o;
            d["atTick"] = (long)_tick;
            return d;
        }

        private static Dictionary<string, object> Gun(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("gun", out v);
            long id;
            if (!S5Args.TryEntityId(v, out id)) return Fail("de.fx gun: a grid or weapon block entity id (decimal string)");
            double shots = MiniJson.GetNumber(args, "shots") ?? 1;
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e)) return Fail("de.fx gun: entity " + id + " not found");
            _gunQueue.Clear();
            var grid = e as MyCubeGrid;
            if (grid != null)
            {
                var blocks = grid.GetFatBlocks();
                for (int i = 0; i < blocks.Count; i++) if (blocks[i] is Sandbox.ModAPI.Ingame.IMyUserControllableGun) _gunQueue.Add(blocks[i].EntityId);
            }
            else if (e is Sandbox.ModAPI.Ingame.IMyUserControllableGun) _gunQueue.Add(id);
            if (_gunQueue.Count == 0) return Fail("de.fx gun: no IMyUserControllableGun at " + id);
            _gunShotsLeft = (int)Math.Max(0, Math.Min(1000, shots));
            _gunNextTick = 0;
            Dictionary<string, object> d = Status();
            d["guns"] = _gunQueue.Count;
            d["shots"] = _gunShotsLeft;
            d["note"] = "ShootOnce() once every 10 ticks per gun (HYPOTHESIS: fires without ammo in DE Lab's game mode, else an owner step)";
            return d;
        }

        private static void GunTick(ulong tick)
        {
            if (_gunShotsLeft <= 0 || _gunQueue.Count == 0) return;
            if (tick < _gunNextTick) return;
            _gunNextTick = tick + 10;
            _gunShotsLeft--;
            for (int i = 0; i < _gunQueue.Count; i++)
            {
                MyEntity e;
                if (!MyEntities.TryGetEntityById(_gunQueue[i], out e)) continue;
                var gun = e as Sandbox.ModAPI.Ingame.IMyUserControllableGun;
                if (gun != null) gun.ShootOnce();
            }
            if (_gunShotsLeft == 0) DeLog.Info("DE.S7 fx gun: the queued ShootOnce() calls are done (tick " + tick + ")");
        }

        private static Dictionary<string, object> Tool(IDictionary<string, object> args)
        {
            object v;
            args.TryGetValue("tool", out v);
            long id;
            if (!S5Args.TryEntityId(v, out id)) return Fail("de.fx tool: a tool block entity id (decimal string)");
            bool on = MiniJson.GetBool(args, "on") ?? true;
            MyEntity e;
            if (!MyEntities.TryGetEntityById(id, out e)) return Fail("de.fx tool: entity " + id + " not found");
            var fb = e as Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
            if (fb == null) return Fail("de.fx tool: " + id + " is not a functional block");
            fb.Enabled = on;
            DeLog.Info("DE.S7 fx tool " + id + " Enabled=" + on + " (tick " + _tick + ")");
            Dictionary<string, object> d = Status();
            d["tool"] = id.ToString(CultureInfo.InvariantCulture);
            d["on"] = on;
            d["atTick"] = (long)_tick;
            return d;
        }

        private static double[] Vec(IDictionary<string, object> args, string key)
        {
            object v;
            if (!args.TryGetValue(key, out v)) return null;
            var l = v as List<object>;
            if (l == null || l.Count != 3) return null;
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (l[i] is double) r[i] = (double)l[i];
                else if (!(l[i] is string) || !double.TryParse((string)l[i], NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return null;
            }
            return r;
        }

        private static Dictionary<string, object> Fail(string error)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error } };
        }
    }
}
