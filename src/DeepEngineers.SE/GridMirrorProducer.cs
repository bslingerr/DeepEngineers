using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>What de.s3 knows about a grid it placed (s3.place, the pixel action, the colour export).</summary>
    internal sealed class S3GridInfo
    {
        public Vec3 LabSn;
        public double HeadingDeg;
        public string HSource;
        /// <summary>The grid-local box of the placed cells (SE-local metres).</summary>
        public Vec3 LocalMin, LocalMax;
        /// <summary>Where SE's coloured texture of the parts' material goes (null: no export requested).</summary>
        public string ExportPng;
        public bool ExportDone;
        public string Label;
        /// <summary>True for a grid the lab command spawned (de.s3 place: closed by remove / the next place); false for a grid the
 /// player built and registered with de.s3 mirror (never closed by the lab, only unregistered).</summary>
        public bool Spawned;
    }

    /// <summary>
 /// The S3 grid mirror producer (ROADMAP S3 "SE plugin (S3b)"; CONTRACT §5.1 C2/C3/C4, §5.4, §6): for every grid the
 /// lab command registered, it streams
 /// <list type="bullet">
 /// <item>C4 (update thread): <c>ClearAll</c> at every stream (re)start, <c>GridAdd</c>, then <c>PartAdd</c> /
 /// <c>PartRemove</c> / <c>PartRecolor</c> from the debounced diff of the armor parts (<see cref="ArmorParts"/>),
 /// re-read on block add/remove (<c>MyCubeGrid.OnBlockAdded</c>/<c>OnBlockRemoved</c>, public
 /// <c>event Action&lt;MySlimBlock&gt;</c>, VERIFIED) and every <see cref="S3Defaults.PartPollTicks"/> ticks;
 /// <c>EntityRemove</c> when the grid closes (<c>MyEntity.OnClose</c>, <c>Closed</c>, <c>MarkedForClose</c>);</item>
 /// <item>C3: each model once per stream (MaterialDefs first, then ModelBegin/Chunks/End back to back), loaded and
 /// serialised by the <see cref="ModelLoader"/> thread; the update thread only copies the bytes;</item>
 /// <item>C2 (in the PrepareForDraw postfix right before C1, same tick, so a reader that saw C1(N) finds C2(N); <see cref="OnSeFrame"/>): one entity record per
 /// mirrored grid with the pose <c>(o, q, s, k)</c> = <c>PoseMath.FromSe(site, M, P*, k)</c> (WireMath.Pose plus k), P* = the point of the grid
 /// group's oriented boxes nearest to the SE camera (CONTRACT §2.4; <c>WireMath.PivotNearest</c> over the physical
 /// group's <c>PositionComp.LocalAABB</c>), <c>k</c> = <c>PositionComp.Scale ?? 1</c>, v and ω mapped at P*.</item>
 /// </list>
 /// Writes go through <see cref="OutboundQueue"/> with a per-tick budget (C3 ≈ 2 ms, C4 1 ms): a full ring leaves the
 /// message queued for the next tick (back-pressure), the update thread never waits. Streaming waits for the hello ack
 /// and restarts with a full resend on a new Subnautica generation, a re-attach, a link epoch or a new world
 /// (<see cref="StreamGate"/>); after a ClearAll, C3 is held until Subnautica's C4 tail passed the ClearAll, so
 /// re-sent models can never be wiped by a ClearAll consumed after them (at most 5 s). Logs: <c>DE.S3 …</c> lines and
 /// se-*.jsonl rows <c>{"ev": "s3.pose" | "s3.place" | "s3.gps" | "s3.model" | "s3.part" | "s3.stream" | "s3.gridAdd" |
 /// "s3.gridRemove" | "s3.stats"}</c> in the "s3/1" log contract of tests/s3/analyze_s3.py and check_placement.py (formats:
 /// <see cref="S3Rows"/>). Update thread only. Never throws into SE.
 /// <para>S5 (GridMirrorProducer.S5.cs): with S5 on, the registry is fed by the entity set (every grid and floating object
 /// near the camera, whole physical hierarchies), every part class is read by <see cref="GridExporter"/>, C4 goes through
 /// the prioritised <see cref="C4Lanes"/> with back-pressure, C2 carries the subgrid / dynamic subpart / tool rows, and the
 /// player's character is exported (<see cref="CharacterExport"/>). With S5 off the S3 path below is unchanged.</para>
 /// </summary>
    internal static partial class GridMirrorProducer
    {
        public const ulong C3HoldTimeoutMs = 5000;
        public const ulong StatsIntervalMs = 1000;

        private sealed class ModelEntry
        {
            public ulong Key;
            public string Asset;
            public float Scale;
            /// <summary>0 requested, 1 ready, 2 failed.</summary>
            public int State;
            public ExportedModel Model;
            public List<OutMessage> Messages;
            public long PayloadBytes;
            public bool SentThisStream;
            public ulong RequestedMs;
            /// <summary>S5: ModelLoadRequest.Kind; the base model of a skin variant; the skin; forced by de.s5 models.</summary>
            public int Kind;
            public ulong BaseKey;
            public Dictionary<string, SkinTextureChange> Skin;
            public string SkinText;
            public bool Forced;
            public string Error;
            /// <summary>S5: a part asked for this model (InUse without scanning every part; a later removal keeps it sent, harmless).</summary>
            public bool Used;
        }

        internal sealed class MirroredGrid
        {
            public MyCubeGrid Grid;
            public long Id;
            public string Why;
            public long GridAddIndex = -1;
            public readonly PartSetTracker Parts = new PartSetTracker();
            public volatile bool BlockEvent = true;
            public long LastPollTick = long.MinValue;
            /// <summary>Poll every tick until this tick after a block event (SE rebuilds the parts a few ticks later).</summary>
            public long FastPollUntil;
            public uint PartsVersion;
            public int FatBlocks;
            public bool Closing;
            /// <summary>The de.s3 request (s3.place, pixel, export).</summary>
            public S3GridInfo Info;
            /// <summary>An s3.place row is due once the parts are committed and their models loaded.</summary>
            public bool PlacePending;
            public bool PlaceAwaitingModels;
            public Action<MySlimBlock> OnBlock;
            public Action<MyEntity> OnClose;
            // C2 pose
            public bool HasPrev;
            public SeEntity Prev;
            public SeMatrix PrevM;
            public Vec3 PrevPivot, PrevCam;
            public bool HasLogged;
            public F32x3 LoggedO;
            public F32x4 LoggedQ;
            public float LoggedS;
            public ulong LoggedMs;
            public int RowsThisSecond;
            public ulong RowsSecondMs;
            // S5 (GridMirrorProducer.S5.cs)
            public S5GridState S5;
            public MyEntity Entity;
            public bool Floating;
            public bool Subgrid;
            public long ParentId;
            public uint SentGridFlags;
            public long SentParentId;
            public bool Pinned;
            public double Distance = double.PositiveInfinity;
            public bool FloatingSent;
            public int MotionCursor;
            public bool Large;
            // S7p-scene-above (GridMirrorProducer.S7p.cs): a projector's blueprint (P7) / an SE missile in flight (P1)
            public bool Projection, Missile;
            // S7t-se-composite (GridMirrorProducer.S7t.cs, T3): a coarse impostor above the picture camera (GridAdd bit 6); the
 // parts it does not carry are held back here and sent after an upgrade (the grid came within the S7a radius)
            public bool Impostor;
            public Dictionary<ulong, PartSnapshot> ImpostorHeld;
        }

        private sealed class LinkSink : IMessageSink
        {
            public Link Link;
            public bool C3;
            public RingWriteStatus TryWrite(uint type, byte[] payload, int count)
            {
                // S5 back-pressure: no new model starts while C3 has less than 8 MiB free
                if (C3 && type == C3Type.ModelBegin && C3ModelStartBlocked()) return RingWriteStatus.Full;
                RingWriteStatus st = C3 ? Link.WriteC3(type, payload, count) : Link.WriteC4(type, payload, count);
                if (st == RingWriteStatus.Ok) OnRingWrite(C3, type, payload, count);
                return st;
            }
        }

        private static readonly StreamGate Gate = new StreamGate();
        private static readonly ConsumerAudit Audit = new ConsumerAudit();
        private static readonly OutboundQueue C3 = new OutboundQueue("C3");
        /// <summary>C4: S3 uses the first-send lane only (FIFO, as before) plus lane 1 for ClearAll/GridAdd/EntityRemove; S5 all four lanes.</summary>
        private static readonly C4Lanes C4 = new C4Lanes("C4");
        private static readonly ModelLoader Loader = new ModelLoader();
        internal static readonly Dictionary<long, MirroredGrid> Grids = new Dictionary<long, MirroredGrid>();
        private static readonly Dictionary<ulong, ModelEntry> Models = new Dictionary<ulong, ModelEntry>();
        private static readonly HashSet<ulong> MaterialsSent = new HashSet<ulong>();
        private static readonly LinkSink C3Sink = new LinkSink { C3 = true };
        private static readonly LinkSink C4Sink = new LinkSink { C3 = false };
        private static readonly Dictionary<ulong, PartSnapshot> Read = new Dictionary<ulong, PartSnapshot>();
        private static readonly List<PartSnapshot> Adds = new List<PartSnapshot>(), Removes = new List<PartSnapshot>(), Recolors = new List<PartSnapshot>();
        private static readonly Func<long> Clock = Stopwatch.GetTimestamp;

        // C2 scratch (no allocation per frame)
        private static readonly double[] _gpsVp = new double[16];
        private static SeEntity[] _entities = new SeEntity[16];
        private static SeMatrix[] _boxM = new SeMatrix[16];
        private static Vec3[] _boxMin = new Vec3[16], _boxMax = new Vec3[16];

        private static uint _worldEpoch;
        private static long _ticks;
        private static bool _holdC3;
        private static long _clearIndex = -1;
        private static ulong _clearWrittenMs, _c4HeadAfterClear;
        private static bool _clearWritten;
        private static string _streamReason;
        private static ulong _streamStartMs;
        private static bool _resendOpen;
        private static long _resendSnFrame = -1;
        private static ulong _resendC3Head, _resendC4Head;
        private static bool _resendFlushed;
        private static ulong _nextStatsMs;
        private static ulong _traceUntilMs;
        private static long _c2Writes, _c2Entities, _modelsQueued, _partAdds, _partRemoves, _partRecolors, _corrupt;
        private static long _poseRows, _poseRowsSkipped;
        private static PumpStop _lastC3Stop, _lastC4Stop;
        private static bool _loggedC3Full, _loggedC4Full;
        private static RateLimit _errorLog = new RateLimit(10000);
        private static long _errors;
        private static Link _link;
        private static MirroredGrid _latest;

        public static bool Streaming { get { return Gate.Streaming; } }
        public static int GridCount { get { return Grids.Count; } }

        // ============================================================================================ registry

 /// <summary>Starts mirroring a grid (update thread). Subscribes to its block and close events.</summary>
        public static void Register(MyCubeGrid grid, string why, S3GridInfo info)
        {
            if (grid == null || grid.Closed) return;
            if (Grids.ContainsKey(grid.EntityId)) return;
            var g = new MirroredGrid { Grid = grid, Id = grid.EntityId, Why = why, Info = info ?? new S3GridInfo() };
            _latest = g;
            g.OnBlock = b => { g.BlockEvent = true; };
            g.OnClose = e => { g.Closing = true; };
            try
            {
                grid.OnBlockAdded += g.OnBlock;
                grid.OnBlockRemoved += g.OnBlock;
                grid.OnClose += g.OnClose;
            }
            catch (Exception e) { DeLog.Info("DE.S3 grid " + grid.EntityId + ": event hooks failed (" + e.GetType().Name + ": " + e.Message + "); parts are polled only"); }
            Grids[g.Id] = g;
            S5AttachRegistered(g); // S5 on: the grid gets the S5 exporter and stays mirrored whatever its distance
            DeLog.Info("DE.S3 mirror grid " + g.Id + " registered (" + why + "): static=" + grid.IsStatic + " gridSize=" + grid.GridSize +
                       " blocks=" + grid.GetBlocks().Count + (Gate.Streaming ? "" : " (stream waits for the hello ack: " + (Gate.Waiting ?? "?") + ")"));
        }

        /// <summary>The last grid de.s3 placed, while it is open.</summary>
        public static bool Latest(out MyCubeGrid grid, out S3GridInfo info)
        {
            MirroredGrid g = _latest;
            grid = g != null ? g.Grid : null;
            info = g != null ? g.Info : null;
            return g != null && grid != null && !grid.Closed && !grid.MarkedForClose && Grids.ContainsKey(g.Id);
        }

        /// <summary>s3.gps rows every frame (de.s3 gps on/off, S3.N2).</summary>
        public static bool GpsRows;

        /// <summary>The registered grids that are still open (update thread).</summary>
        public static List<MyCubeGrid> Registered() { return Registered(false); }

        /// <summary>The registered open grids; <paramref name="spawnedOnly"/> keeps the lab-spawned ones (never a player's grid).</summary>
        public static List<MyCubeGrid> Registered(bool spawnedOnly)
        {
            var l = new List<MyCubeGrid>(Grids.Count);
            foreach (MirroredGrid g in Grids.Values)
                if (g.Grid != null && !g.Grid.Closed && (!spawnedOnly || (g.Info != null && g.Info.Spawned))) l.Add(g.Grid);
            return l;
        }

        private static void Unhook(MirroredGrid g)
        {
            try
            {
                if (g.Grid != null)
                {
                    g.Grid.OnBlockAdded -= g.OnBlock;
                    g.Grid.OnBlockRemoved -= g.OnBlock;
                    g.Grid.OnClose -= g.OnClose;
                }
                else if (g.Entity != null) g.Entity.OnClose -= g.OnClose;
            }
            catch (Exception) { }
        }

        private static void ForgetWorld(string why)
        {
            if (Grids.Count > 0 || C3.Count > 0 || C4.Count > 0)
                DeLog.Info("DE.S3 forget " + Grids.Count + " mirrored grid(s), " + C3.Count + " C3 and " + C4.Count + " C4 queued messages (" + why + ")");
            foreach (MirroredGrid g in Grids.Values) { Unhook(g); if (g.S5 != null) GridExporter.Detach(g.S5); }
            Grids.Clear();
            C3.Clear();
            C4.Clear();
            MaterialsSent.Clear();
            S5Forget(why);
            foreach (ModelEntry m in Models.Values) m.SentThisStream = false;
            _holdC3 = false;
            _resendOpen = false;
        }

        // ============================================================================================ per tick

 /// <summary>Plugin Update (update thread), after the link's tick.</summary>
        public static void Tick(ulong nowMs, Link link, uint helloEpoch, uint worldEpoch, bool inWorld)
        {
            try { TickCore(nowMs, link, helloEpoch, worldEpoch, inWorld); }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(nowMs)) DeLog.Info("DE.Error S3 producer #" + _errors + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
            }
        }

        private static void TickCore(ulong nowMs, Link link, uint helloEpoch, uint worldEpoch, bool inWorld)
        {
            _ticks++;
            _link = link;
            C3Sink.Link = link;
            C4Sink.Link = link;
            MaterialExport.Poll(nowMs);
            if (!inWorld)
            {
                if (Grids.Count > 0) ForgetWorld("left the world");
                Gate.Reset();
                return;
            }
            if (worldEpoch != _worldEpoch)
            {
                if (_worldEpoch != 0) ForgetWorld("world epoch " + _worldEpoch + " -> " + worldEpoch);
                _worldEpoch = worldEpoch;
            }

            S3Commands.Tick(nowMs); // a pending de.s3 h=raycast asks Subnautica again
            DrainLoader(nowMs);
            PruneClosed();
            S5PreTick(nowMs, link, worldEpoch); // S5: postfix marks, the entity set (registers and removes), settings

            bool linked = link != null && link.State == LinkState.Up && link.HasSnStatus;
            SnStatus st = linked ? link.LastSnStatus : default(SnStatus);
            Gate.Tick(linked, linked ? link.Attaches : 0, linked ? link.NewPeers : 0, linked ? link.LinkEpoch : 0, worldEpoch, helloEpoch,
                      st.helloAckEpoch, st.linkAckEpoch, st.snFrame);
            if (!Gate.Streaming)
            {
                Stats(nowMs, link, st);
                return;
            }
            string resend = Gate.TakeResend();
            if (resend != null) StartStream(nowMs, resend, worldEpoch, link.LinkEpoch);

            if (S5Active) { S5UpdateGrids(nowMs, worldEpoch); PrewarmToolbar(nowMs); }
            else
                foreach (MirroredGrid g in Grids.Values)
                {
                    UpdateGrid(nowMs, g, worldEpoch);
                    if (g.PlaceAwaitingModels && ModelsSettled(g)) { g.PlaceAwaitingModels = false; g.PlacePending = true; }
                }

            // C4 first: a ClearAll and the GridAdds go before anything else of the stream
            ulong c3Head, c3Tail, c4Head, c4Tail;
            link.RingCounters(out c3Head, out c3Tail, out c4Head, out c4Tail);
            PumpResult r4 = S5Active
                ? C4.Pump(C4Sink, new PumpBudget(S5Defaults.C4BudgetMs, S5Defaults.C4MaxMessagesPerTick, S5Defaults.C4BytesPerTick), Clock, RingFree(RegionId.C4SeParts, c4Head, c4Tail))
                : C4.Pump(C4Sink, new PumpBudget(S3Defaults.C4BudgetMs, S3Defaults.C4MaxMessagesPerTick, 0), Clock, -1);
            _lastC4Stop = r4.Stop;
            _lastC4Ms = r4.Ms;
            Pressure(r4, C4.Name, C4.Count, C4.PendingBytes, ref _loggedC4Full);
            link.RingCounters(out c3Head, out c3Tail, out c4Head, out c4Tail);
            if (_holdC3)
            {
                if (!_clearWritten && C4.IsDone(_clearIndex))
                {
                    _clearWritten = true;
                    _clearWrittenMs = nowMs;
                    _c4HeadAfterClear = c4Head;
                }
                if (_clearWritten && c4Tail >= _c4HeadAfterClear)
                {
                    _holdC3 = false;
                    DeLog.Info("DE.S3 C3 released: Subnautica consumed C4 past the ClearAll (C4 tail " + c4Tail + " >= " + _c4HeadAfterClear + ") after " + (nowMs - _clearWrittenMs) + " ms");
                }
                else if (_clearWritten && nowMs - _clearWrittenMs > C3HoldTimeoutMs)
                {
                    _holdC3 = false;
                    DeLog.Info("DE.S3 C3 released after " + C3HoldTimeoutMs / 1000 + " s without Subnautica consuming C4 past the ClearAll (C4 tail " + c4Tail +
                               " < " + _c4HeadAfterClear + "): is the Subnautica C4 consumer running?");
                }
            }
            if (!_holdC3)
            {
                _c3FreeBytes = RingFree(RegionId.C3SeModels, c3Head, c3Tail);
                PumpResult r3 = C3.Pump(C3Sink, S5Active ? new PumpBudget(S5Defaults.C3BudgetMs, S5Defaults.C3MaxMessagesPerTick, S5Defaults.C3BytesPerTick)
                                                         : new PumpBudget(S3Defaults.C3BudgetMs, S3Defaults.C3MaxMessagesPerTick, 0), Clock);
                _lastC3Stop = r3.Stop;
                _lastC3Ms = r3.Ms;
                Pressure(r3, C3.Name, C3.Count, C3.PendingBytes, ref _loggedC3Full);
            }
            else _lastC3Stop = PumpStop.Held;
            ResendProgress(nowMs, st, c3Head, c3Tail, c4Head, c4Tail);
            AuditConsumer(nowMs, st, c3Head, c3Tail, c4Head, c4Tail, worldEpoch, link.LinkEpoch);
            Stats(nowMs, link, st);
            S5Budget(nowMs, link, c3Head, c3Tail, c4Head, c4Tail);
        }

        /// <summary>Free bytes of a producer ring (its region size minus head − tail); -1 when unknown.</summary>
        private static long RingFree(RegionId id, ulong head, ulong tail)
        {
            try
            {
                long cap = (long)Layout.Region(id).Size;
                long used = head >= tail ? (long)(head - tail) : 0;
                return Math.Max(0, cap - used);
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
 /// The consumer audit: with the stream idle and both rings
 /// drained, Subnautica's C7 counters must match what SE sent; a lasting mismatch (records lost to a corrupt-framing
 /// re-sync on the Subnautica side) restarts the stream: ClearAll and everything again.
 /// </summary>
        private static ulong _nextAuditMs;

        private static void AuditConsumer(ulong nowMs, SnStatus st, ulong c3Head, ulong c3Tail, ulong c4Head, ulong c4Tail, uint worldEpoch, uint linkEpoch)
        {
            if (nowMs < _nextAuditMs) return;   // review F5: 4 Hz is enough for a 2 s mismatch rule; the walk is ~0.5 ms at 20k parts
            _nextAuditMs = nowMs + 250;
            bool idle = !_resendOpen && !_holdC3 && C3.Count == 0 && C4.Count == 0;
            uint grids = 0, expectPending = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.GridAddIndex < 0 || !C4.IsDone(g.GridAddIndex) || Unsettled(g)) { idle = false; continue; }
                grids++;
                foreach (PartSnapshot p in HeldOf(g).Values)
                {
                    ModelEntry e;
                    if (!Models.TryGetValue(p.ModelKey, out e) || e.State == 2) expectPending++;
                    else if (e.State == 0 || !e.SentThisStream) idle = false;
                }
            }
            if (S5Active && !CharacterExport.AuditView(ref grids, ref expectPending)) idle = false;
            if (S5Active && !PlacementGhost.AuditView(ref grids, ref expectPending)) idle = false; // S7a: the ghost is one sent entity while alive
            bool drained = c3Tail == c3Head && c4Tail == c4Head;
            long seC3Bytes = C3.TotalBytes + (long)ByteRing.HeaderBytes * C3.TotalWritten;
            string why = Audit.Tick(nowMs, idle, drained, seC3Bytes, grids, expectPending, st.snFrame, st.c3BytesDrained, st.mirroredEntityCount, st.pendingParts);
            if (why == null) return;
            DeLog.Info("DE.S3 consumer audit: " + why + " for " + ConsumerAudit.MismatchMs + " ms with both rings drained: resending everything (resend #" + Audit.Resends + ")");
            JsonRow("audit", "\"reason\":" + Str(why) + ",\"resends\":" + Audit.Resends + ",\"c3BytesDrained\":" + st.c3BytesDrained + ",\"seC3Bytes\":" + seC3Bytes +
                    ",\"mirroredEntityCount\":" + st.mirroredEntityCount + ",\"grids\":" + grids + ",\"pendingParts\":" + st.pendingParts + ",\"expectPending\":" + expectPending);
            StartStream(nowMs, "consumer audit: " + why, worldEpoch, linkEpoch);
        }

        private static void Pressure(PumpResult r, string name, int count, long pendingBytes, ref bool logged)
        {
            if (r.Stop == PumpStop.Full && !logged)
            {
                logged = true;
                DeLog.Info("DE.S3 " + name + " ring full: " + count + " message(s) (" + pendingBytes + " B) stay queued for the next tick (back-pressure; logged once per stream)");
            }
            if (r.Stop == PumpStop.Corrupt)
            {
                _corrupt++;
                if (_errorLog.Allow(Win32.TickCount64())) DeLog.Info("DE.S3 " + name + " producer: impossible ring counters (Corrupt); nothing written until the link re-attaches");
            }
            if (r.Dropped > 0) DeLog.Info("DE.S3 " + name + " dropped " + r.Dropped + " message(s) that can never fit (TooLarge)");
        }

        private static void StartStream(ulong nowMs, string reason, uint worldEpoch, uint linkEpoch)
        {
            _nextAuditMs = 0;
            Audit.Reset();
            C3.Clear();
            C4.Clear();
            MaterialsSent.Clear();
            foreach (ModelEntry m in Models.Values) m.SentThisStream = false;
            _clearIndex = C4.Enqueue(C4Lanes.Control, S3Codec.ClearAllMsg(worldEpoch, linkEpoch));
            _clearWritten = false;
            _holdC3 = true;
            _loggedC3Full = _loggedC4Full = false;
            foreach (MirroredGrid g in Grids.Values)
            {
                g.GridAddIndex = -1;
                g.Parts.ForgetHeld();
                g.BlockEvent = true;
                g.HasPrev = false;
                if (g.S5 != null) GridExporter.ForgetHeld(g.S5);
                g.FloatingSent = false;
                g.SentGridFlags = 0;
                g.SentParentId = 0;
                if (g.ImpostorHeld != null) g.ImpostorHeld.Clear(); // S7t-se-composite: the exporter re-emits everything; the filter holds again
            }
            CharacterExport.OnStreamStart();
            Combat.CollisionSkin.OnStreamStart(); // S6: every skin again from version 1
            PlacementGhost.OnStreamRestart(); // S7a: the placement ghost again from its GridAdd
            _streamReason = reason;
            _streamStartMs = nowMs;
            _resendOpen = true;
            _resendFlushed = false;
            DeLog.Info("DE.S3 stream START (" + reason + "): ClearAll(worldEpoch=" + worldEpoch + ", linkEpoch=" + linkEpoch + "), then " + Grids.Count +
                       " grid(s), their parts, models and materials (C3 held until Subnautica consumed the ClearAll)");
            JsonRow("stream", "\"reason\":" + Str(reason) + ",\"worldEpoch\":" + worldEpoch + ",\"linkEpoch\":" + linkEpoch + ",\"grids\":" + Grids.Count);
        }

        /// <summary>
 /// "A resend is complete when the consumer's tails have passed the heads at which the resend ended and a C7 newer
 /// than the resend has been read" (CONTRACT §6, D41): logged with Subnautica's counters.
 /// </summary>
        private static void ResendProgress(ulong nowMs, SnStatus st, ulong c3Head, ulong c3Tail, ulong c4Head, ulong c4Tail)
        {
            if (!_resendOpen) return;
            if (!_resendFlushed)
            {
                if (C3.Count > 0 || C4.Count > 0 || _holdC3) return;
                foreach (MirroredGrid g in Grids.Values) if (g.GridAddIndex < 0 || Unsettled(g)) return;
                foreach (ModelEntry m in Models.Values) if (m.State == 0) return;
                _resendFlushed = true;
                _resendC3Head = c3Head;
                _resendC4Head = c4Head;
                _resendSnFrame = (long)st.snFrame;
                return;
            }
            if (c3Tail < _resendC3Head || c4Tail < _resendC4Head || (long)st.snFrame <= _resendSnFrame) return;
            _resendOpen = false;
            DeLog.Info("DE.S3 stream complete (" + _streamReason + ") in " + (nowMs - _streamStartMs) + " ms: Subnautica consumed C3 to " + c3Tail + " and C4 to " + c4Tail +
                       "; C7 mirroredEntityCount=" + st.mirroredEntityCount + " pendingParts=" + st.pendingParts + " c3BytesDrained=" + st.c3BytesDrained +
                       " (SE sent " + Grids.Count + " grid(s), " + HeldParts() + " parts)");
        }

        private static void DrainLoader(ulong nowMs)
        {
            ModelLoadResult r;
            int n = 0;
            while (n++ < 64 && Loader.TryTakeResult(out r))
            {
                if (r.Request.Kind == ModelLoadRequest.KindCharacter) { CharacterExport.OnLoaded(nowMs, r); continue; }
                ModelEntry e;
                if (!Models.TryGetValue(r.Request.ModelKey, out e)) continue;
                if (r.Error != null)
                {
                    e.State = 2;
                    e.Error = r.Error;
                    S5ModelRow(r, e);
                    DeLog.Info("DE.S3 FAIL loading model " + r.Request.AssetName + " key=" + S3Rows.Hex(r.Request.ModelKey) + ": " + r.Error +
                               " (its parts stay pending in Subnautica)");
                    continue;
                }
                e.State = 1;
                e.Model = r.Model;
                e.Messages = r.Messages;
                e.PayloadBytes = r.PayloadBytes;
                ExportedModel m = r.Model;
                float[] b = m.Bounds();
                DeLog.Info("DE.S3 loaded model " + m.AssetName + " key=" + S3Rows.Hex(m.ModelKey) + " verts=" + m.VertexCount + " tris=" + m.TriangleCount +
                           " (MyModel.GetVerticesCount=" + m.SeVertexCount + " GetTrianglesCount=" + m.SeTriangleCount + "; S3.C1 reference) submeshes=" + m.Submeshes.Length +
                           " patternScale=" + G(m.PatternScale) + " scale=" + G(m.Scale) + " tangents=" + m.TangentSource +
                           " bounds=(" + G(b[0]) + "," + G(b[1]) + "," + G(b[2]) + ")..(" + G(b[3]) + "," + G(b[4]) + "," + G(b[5]) + ")" +
                           " payload=" + r.PayloadBytes + " B in " + (r.Messages.Count - 2) + " chunk(s) loadMs=" + G(Math.Round(m.LoadMs, 2)) + " (worker thread)");
                foreach (MaterialInfo mi in m.Materials)
                    DeLog.Info("DE.S3 material " + mi.Name + " key=" + S3Rows.Hex(mi.Key) + " technique=" + mi.DrawTechnique + " flags=" + mi.Flags +
                               " cm=" + Q(mi.ColorMetal) + " ng=" + Q(mi.NormalGloss) + " add=" + Q(mi.AddMaps) + " alpha=" + Q(mi.AlphaMask) +
                               (mi.Missing.Length > 0 ? " MISSING=" + mi.Missing : ""));
                Jsonl.WriteLine(S3Rows.Model(Utc(), m, r.Messages.Count - 2, r.PayloadBytes));
                S5ModelRow(r, e);
                if (r.Notes != null) DeLog.Info("DE.S5 model " + m.AssetName + " key=" + S3Rows.Hex(m.ModelKey) + ": " + r.Notes);
                if (Gate.Streaming && (e.Forced || (S5Active ? e.Used : InUse(e.Key)))) QueueModel(e);
                S5OnModelReady(e);
            }
        }

        private static bool InUse(ulong modelKey)
        {
            foreach (MirroredGrid g in Grids.Values)
                foreach (PartSnapshot p in HeldOf(g).Values)
                    if (p.ModelKey == modelKey) return true;
            return false;
        }

        /// <summary>The parts Subnautica holds for a grid (S3: the whole-grid tracker; S5: the exporter's aggregate).</summary>
        private static Dictionary<ulong, PartSnapshot> HeldOf(MirroredGrid g)
        {
            return g.S5 != null ? g.S5.Held : g.Parts.Held;
        }

        /// <summary>True while the grid's last reads have not settled.</summary>
        private static bool Unsettled(MirroredGrid g)
        {
            if (g.Floating) return !g.FloatingSent;
            return g.S5 != null ? GridExporter.Unsettled(g.S5) : g.Parts.Unsettled(S3Defaults.PartStablePolls);
        }

        private static void EnsureModel(ulong nowMs, PartSnapshot p)
        {
            ModelEntry e;
            if (S5Active && S5EnsureVariant(nowMs, p))
            {
                if (Models.TryGetValue(p.ModelKey, out e)) e.Used = true;
                return;
            }
            if (!Models.TryGetValue(p.ModelKey, out e))
            {
                e = new ModelEntry { Key = p.ModelKey, Asset = p.AssetName, Scale = p.ModelScale, State = 0, RequestedMs = nowMs, Used = true };
                Models[p.ModelKey] = e;
                Loader.Request(new ModelLoadRequest { ModelKey = p.ModelKey, AssetName = p.AssetName, Scale = p.ModelScale, WorldEpoch = _worldEpoch });
                DeLog.Info("DE.S3 requested model " + p.AssetName + " key=" + S3Rows.Hex(p.ModelKey) + " scale=" + G(p.ModelScale) + " (loader queue " + Loader.Pending + ")");
                return;
            }
            e.Used = true;
            if (e.State == 1 && !e.SentThisStream) QueueModel(e);
        }

        private static void QueueModel(ModelEntry e)
        {
            if (e.SentThisStream || e.Model == null || e.Messages == null) return;
            foreach (MaterialInfo mi in e.Model.Materials)
            {
                if (MaterialsSent.Contains(mi.Key)) continue;
                C3.Enqueue(S3Codec.MaterialDef(mi));
                MaterialsSent.Add(mi.Key);
            }
            C3.EnqueueRange(e.Messages);
            e.SentThisStream = true;
            _modelsQueued++;
        }

        private static void PruneClosed()
        {
            List<long> gone = null;
            foreach (MirroredGrid g in Grids.Values)
            {
                MyEntity e = g.Grid != null ? (MyEntity)g.Grid : g.Entity;
                if (e == null || e.Closed || e.MarkedForClose || g.Closing)
                    (gone ?? (gone = new List<long>())).Add(g.Id);
            }
            if (gone == null) return;
            foreach (long id in gone)
            {
                MirroredGrid g = Grids[id];
                Unhook(g);
                if (g.S5 != null) GridExporter.Detach(g.S5);
                Grids.Remove(id);
                S5Forgotten(id, "closed by SE");
                C4.Purge(id);
                if (Gate.Streaming && g.GridAddIndex >= 0) C4.Enqueue(C4Lanes.Control, S3Codec.EntityRemoveMsg(id));
                DeLog.Info("DE.S3 mirror grid " + id + " closed: EntityRemove " + (Gate.Streaming && g.GridAddIndex >= 0 ? "queued" : "not needed (never sent)"));
                JsonRow("gridRemove", "\"entityId\":" + id);
            }
        }

        private static void UpdateGrid(ulong nowMs, MirroredGrid g, uint worldEpoch)
        {
            MyCubeGrid grid = g.Grid;
            if (g.GridAddIndex < 0)
            {
                long parent = 0; // S3 mirrors root grids only (subgrids are C2 subparts of their root, D8: not in S3)
                g.GridAddIndex = C4.Enqueue(C4Lanes.Control, S3Codec.GridAddMsg(g.Id, grid.GridSizeEnum == VRage.Game.MyCubeSize.Large, parent, worldEpoch));
                JsonRow("gridAdd", "\"entityId\":" + g.Id + ",\"large\":" + (grid.GridSizeEnum == VRage.Game.MyCubeSize.Large ? "true" : "false") +
                        ",\"static\":" + (grid.IsStatic ? "true" : "false") + ",\"gridSize\":" + G(grid.GridSize));
            }
            if (g.BlockEvent) g.FastPollUntil = _ticks + S3Defaults.FastPollTicks;
            bool due = g.BlockEvent || _ticks < g.FastPollUntil || g.Parts.Unsettled(S3Defaults.PartStablePolls) || _ticks - g.LastPollTick >= S3Defaults.PartPollTicks;
            if (!due) return;
            g.BlockEvent = false;
            g.LastPollTick = _ticks;
            int fat;
            ArmorParts.Read(grid, Read, out fat);
            if (fat != g.FatBlocks)
            {
                g.FatBlocks = fat;
                if (fat > 0) DeLog.Info("DE.S3 grid " + g.Id + ": " + fat + " functional block(s) not mirrored in S3 (armor parts only)");
            }
            if (!g.Parts.Offer(Read, S3Defaults.PartStablePolls)) return;
            PartDiff.Compute(g.Parts.Held, Read, Adds, Removes, Recolors);
            string utc = Utc();
            foreach (PartSnapshot p in Removes)
            {
                C4.Enqueue(S3Codec.PartRemoveMsg(g.Id, p.PartKey));
                _partRemoves++;
                Jsonl.WriteLineBuffered(S3Rows.Part(utc, "remove", g.Id, p));
            }
            foreach (PartSnapshot p in Adds)
            {
                EnsureModel(nowMs, p);
                C4.Enqueue(S3Codec.PartAddMsg(g.Id, p));
                _partAdds++;
                Jsonl.WriteLineBuffered(S3Rows.Part(utc, "add", g.Id, p));
            }
            foreach (PartSnapshot p in Recolors)
            {
                C4.Enqueue(S3Codec.PartRecolorMsg(g.Id, p));
                _partRecolors++;
                Jsonl.WriteLineBuffered(S3Rows.Part(utc, "recolor", g.Id, p));
            }
            g.Parts.Commit(Read);
            g.PartsVersion++;
            g.PlaceAwaitingModels = true; // s3.place with the new totals once every model of the parts is loaded
            DeLog.Info("DE.S3 grid " + g.Id + " parts v" + g.PartsVersion + ": +" + Adds.Count + " -" + Removes.Count + " ~" + Recolors.Count + " (now " + g.Parts.Held.Count +
                       " part(s), " + DistinctModels(g) + " model(s)); C4 queue " + C4.Count);
        }

        /// <summary>Every model the grid's parts use is loaded or failed.</summary>
        private static bool ModelsSettled(MirroredGrid g)
        {
            foreach (PartSnapshot p in HeldOf(g).Values)
            {
                ModelEntry e;
                if (!Models.TryGetValue(p.ModelKey, out e) || e.State == 0) return false;
            }
            return true;
        }

        /// <summary>s3.place: totals of the grid's parts (S3.C1) and its pose now (written from the C2 hook, same tick).</summary>
        private static void PlaceRow(ulong tick, MirroredGrid g, MyCubeGrid grid, SeMatrix m, Vec3 pivot, WireEntityPose pose)
        {
            g.PlacePending = false;
            long verts = 0, tris = 0;
            var models = new HashSet<ulong>();
            foreach (PartSnapshot p in HeldOf(g).Values)
            {
                ModelEntry e;
                if (!Models.TryGetValue(p.ModelKey, out e) || e.Model == null) continue;
                verts += e.Model.VertexCount;
                tris += e.Model.TriangleCount;
                models.Add(p.ModelKey);
            }
            BoundingBox b = grid.PositionComp.LocalAABB;
            S3GridInfo info = g.Info;
            Jsonl.WriteLine(S3Rows.Place(Utc(), tick, g.Id, info.LabSn, info.HeadingDeg, info.HSource, m, pivot, pose, new Vec3(b.Min.X, b.Min.Y, b.Min.Z),
                                         new Vec3(b.Max.X, b.Max.Y, b.Max.Z), HeldOf(g).Count, verts, tris, models.Count, SiteBinding.Map));
            DeLog.Info("DE.S3 grid " + g.Id + " totals: " + HeldOf(g).Count + " part(s), " + models.Count + " model(s), verts=" + verts + " tris=" + tris +
                       " (sums over the parts: S3.C1 compares them with Subnautica's DE.Mirror built; se-*.jsonl s3.place)");
            if (info.ExportPng != null && !info.ExportDone)
            {
                // de.s3 place {export: true}: SE's coloured texture of the parts' material(s), at the path the reply named
                info.ExportDone = true;
                List<ColourExportItem> items = ExportItemsFor(g);
                if (items.Count == 0) DeLog.Info("DE.S3 colour export for grid " + g.Id + ": no part material with a _cm texture");
                else
                {
                    string first = info.ExportPng;
                    string stem = first.EndsWith("-0_ca.png", StringComparison.OrdinalIgnoreCase) ? first.Substring(0, first.Length - "-0_ca.png".Length) : first;
                    MaterialExport.Request(items, Win32.TickCount64(), k => k == 0 ? first : stem + "-" + k.ToString(CultureInfo.InvariantCulture) + "_ca.png");
                    DeLog.Info("DE.S3 colour export requested for grid " + g.Id + ": " + items.Count + " PNG(s), the first at " + first + " (S3.C13; written by SE's render thread)");
                }
            }
        }

        /// <summary>s3.gps (S3.N2): the pixel of the grid centre (the GPS point) through SE's camera of this frame.</summary>
        private static void GpsRow(MyCamera camera, ulong tick, long qpc, MirroredGrid g, Vec3 pointSe)
        {
            MatrixD vpm = camera.ViewProjectionMatrix;
            double[] vp = _gpsVp; // reused
            vp[0] = vpm.M11; vp[1] = vpm.M12; vp[2] = vpm.M13; vp[3] = vpm.M14; vp[4] = vpm.M21; vp[5] = vpm.M22; vp[6] = vpm.M23; vp[7] = vpm.M24;
            vp[8] = vpm.M31; vp[9] = vpm.M32; vp[10] = vpm.M33; vp[11] = vpm.M34; vp[12] = vpm.M41; vp[13] = vpm.M42; vp[14] = vpm.M43; vp[15] = vpm.M44;
            var vport = camera.Viewport;
            double px, py, ndcZ;
            bool front = S1Math.Project(vp, vport.OffsetX, vport.OffsetY, vport.Width, vport.Height, pointSe, out px, out py, out ndcZ);
            Jsonl.WriteLineBuffered(S3Rows.Gps(Utc(), tick, qpc, g.Id, px, py, front, pointSe));
        }

        private static int DistinctModels(MirroredGrid g)
        {
            var set = new HashSet<ulong>();
            foreach (PartSnapshot p in HeldOf(g).Values) set.Add(p.ModelKey);
            return set.Count;
        }

        private static int HeldParts()
        {
            int n = 0;
            foreach (MirroredGrid g in Grids.Values) n += HeldOf(g).Count;
            return n;
        }

        // ============================================================================================ C2 (before C1)

 /// <summary>
 /// S3Patches' PrepareForDraw postfix (update thread, after the C1 writer; SE's camera of this frame is final): the
 /// s3.gps rows of S3.N2 while tracing or <c>de.s3 gps on</c> (tick = this frame's GameplayFrameCounter, qpc now),
 /// always from the current camera, also under S3.M4 (C1/C2 one tick late).
 /// </summary>
        public static void OnPrepareForDraw()
        {
            try
            {
                if (!Gate.Streaming || Grids.Count == 0) return;
                S5CohRows();
                if (!GpsRows && Win32.TickCount64() >= _traceUntilMs) return;
                MySession s = MySession.Static;
                MyCamera camera = Sandbox.Game.World.MySector.MainCamera;
                if (s == null || camera == null) return;
                ulong tick = (ulong)(uint)s.GameplayFrameCounter;
                long qpc = Win32.QpcNow();
                foreach (MirroredGrid g in Grids.Values)
                {
                    if (g.GridAddIndex < 0 || !C4.IsDone(g.GridAddIndex)) continue;
                    MyCubeGrid grid = g.Grid;
                    if (grid == null || grid.Closed) continue;
                    Vector3D t = grid.WorldMatrix.Translation;
                    GpsRow(camera, tick, qpc, g, new Vec3(t.X, t.Y, t.Z));
                }
            }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(Win32.TickCount64())) DeLog.Info("DE.Error S3 gps rows #" + _errors + " " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>The link's before-C1 hook (PrepareForDraw postfix, update thread; the plugin Update under S1.M2/S3.M4): C2 for the same tick, published before C1.</summary>
        public static void OnSeFrame(ulong tick, long qpcCapture)
        {
            try { OnSeFrameCore(tick, qpcCapture); }
            catch (Exception e)
            {
                _errors++;
                if (_errorLog.Allow(Win32.TickCount64())) DeLog.Info("DE.Error S3 C2 writer #" + _errors + " " + e.GetType().Name + ": " + e.Message + " " + FirstFrame(e));
            }
        }

        private static void OnSeFrameCore(ulong tick, long qpcCapture)
        {
            Link link = _link;
            if (link == null || !Gate.Streaming) return;
            if (S5Active) { S5OnSeFrame(link, tick, qpcCapture); return; }
            SiteMap site = SiteBinding.Map;
            MyCamera camera = Sandbox.Game.World.MySector.MainCamera;
            if (site == null || camera == null) return;
            Vector3D ct = camera.WorldMatrix.Translation;
            var cam = new Vec3(ct.X, ct.Y, ct.Z);
            ulong now = Win32.TickCount64();
            bool tracing = now < _traceUntilMs;
            int n = 0;
            foreach (MirroredGrid g in Grids.Values)
            {
                if (g.GridAddIndex < 0 || !C4.IsDone(g.GridAddIndex)) continue; // Subnautica learns the grid from C4 first
                MyCubeGrid grid = g.Grid;
                if (grid == null || grid.Closed) continue;
                if (n >= SeEntitiesLimits.MaxEntities) break;
                SeMatrix m = FrameWriter.ToSe(grid.WorldMatrix);
                Vec3 pivot = Pivot(grid, m, cam);
                float k = grid.PositionComp.Scale ?? 1f;
                WireEntityPose pose = PoseMath.FromSe(site, m, pivot, k > 0 ? k : 1f);
                Vec3 v = Vec3.Zero, w = Vec3.Zero;
                if (grid.Physics != null)
                {
                    Vector3 lv = grid.Physics.LinearVelocity, av = grid.Physics.AngularVelocity;
                    v = WireMath.Vel(site, pivot, new Vec3(lv.X, lv.Y, lv.Z));
                    w = WireMath.AngVel(site, pivot, new Vec3(av.X, av.Y, av.Z));
                }
                var e = new SeEntity
                {
                    entityId = (ulong)g.Id,
                    kind = SeEntityKind.RootGrid,
                    flags = grid.IsStatic ? SeEntityFlags.Static : 0u,
                    groupId = (ulong)g.Id,
                    v = new F32x3((float)v.X, (float)v.Y, (float)v.Z),
                    omega = new F32x3((float)w.X, (float)w.Y, (float)w.Z),
                    partsVersion = g.PartsVersion,
                    skinVersion = 0,
                };
                PoseMath.WriteWire(ref e, pose); // o, q (w >= 0), s, k as float32
                SeEntity written = e;
                SeMatrix rowM = m;
                Vec3 rowPivot = pivot, rowCam = cam;
                bool lagged = false;
                if (S3Mutants.PoseOneTickLag && g.HasPrev)
                {
                    // S3.M4: the pose of the previous tick under this tick's stamp
                    written = g.Prev;
                    written.partsVersion = g.PartsVersion;
                    rowM = g.PrevM; rowPivot = g.PrevPivot; rowCam = g.PrevCam;
                    lagged = true;
                }
                g.Prev = e; g.PrevM = m; g.PrevPivot = pivot; g.PrevCam = cam; g.HasPrev = true;
                if (n >= _entities.Length) Array.Resize(ref _entities, _entities.Length * 2);
                _entities[n++] = written;
                PoseRow(now, tick, g, grid, rowM, rowPivot, rowCam, written, lagged, tracing);
                if (g.PlacePending) PlaceRow(tick, g, grid, m, pivot, PoseMath.FromSe(site, m, pivot, k > 0 ? k : 1f));
            }
            if (link.WriteSeEntities(tick, _worldEpoch, _entities, n))
            {
                _c2Writes++;
                _c2Entities += n;
            }
        }

        /// <summary>P* over the grid's physical group boxes (CONTRACT §2.4), the grid's own box when it has no group.</summary>
        private static Vec3 Pivot(MyCubeGrid grid, SeMatrix m, Vec3 cam)
        {
            int count = 0;
            var groups = MyCubeGridGroups.Static;
            var group = groups != null ? groups.Physical.GetGroup(grid) : null;
            if (group != null)
            {
                foreach (var node in group.Nodes)
                {
                    MyCubeGrid gg = node.NodeData;
                    if (gg == null || gg.Closed) continue;
                    if (count >= _boxM.Length)
                    {
                        Array.Resize(ref _boxM, count * 2);
                        Array.Resize(ref _boxMin, count * 2);
                        Array.Resize(ref _boxMax, count * 2);
                    }
                    BoundingBox b = gg.PositionComp.LocalAABB;
                    _boxM[count] = ReferenceEquals(gg, grid) ? m : FrameWriter.ToSe(gg.WorldMatrix);
                    _boxMin[count] = new Vec3(b.Min.X, b.Min.Y, b.Min.Z);
                    _boxMax[count] = new Vec3(b.Max.X, b.Max.Y, b.Max.Z);
                    count++;
                }
            }
            if (count == 0)
            {
                BoundingBox b = grid.PositionComp.LocalAABB;
                _boxM[0] = m;
                _boxMin[0] = new Vec3(b.Min.X, b.Min.Y, b.Min.Z);
                _boxMax[0] = new Vec3(b.Max.X, b.Max.Y, b.Max.Z);
                count = 1;
            }
            return WireMath.PivotNearest(_boxM, _boxMin, _boxMax, count, cam);
        }

        private static void PoseRow(ulong now, ulong tick, MirroredGrid g, MyCubeGrid grid, SeMatrix m, Vec3 pivot, Vec3 cam, SeEntity e, bool lagged, bool tracing)
        {
            bool changed = !g.HasLogged || e.o.x != g.LoggedO.x || e.o.y != g.LoggedO.y || e.o.z != g.LoggedO.z || e.q.x != g.LoggedQ.x || e.q.y != g.LoggedQ.y ||
                           e.q.z != g.LoggedQ.z || e.q.w != g.LoggedQ.w || e.s != g.LoggedS;
            string reason;
            if (!g.HasLogged) reason = "first";
            else if (tracing) reason = "trace";
            else if (changed) reason = "changed";
            else if (now - g.LoggedMs >= S3Defaults.PoseRowIntervalMs) reason = "periodic";
            else return;
            if (reason == "changed")
            {
                if (now - g.RowsSecondMs >= 1000) { g.RowsSecondMs = now; g.RowsThisSecond = 0; }
                if (g.RowsThisSecond >= S3Defaults.PoseRowsPerSecond) { _poseRowsSkipped++; return; }
                g.RowsThisSecond++;
            }
            BoundingBox b = grid.PositionComp.LocalAABB;
            string row = S3Rows.Pose(Utc(), tick, g.Id, m, pivot, cam, e.o, e.q, e.s, e.k, new Vec3(b.Min.X, b.Min.Y, b.Min.Z), new Vec3(b.Max.X, b.Max.Y, b.Max.Z), lagged, reason, SiteBinding.Map);
            if (reason == "first")
            {
                Jsonl.WriteLine(row);
                DeLog.Info("DE.S3 first pose of grid " + g.Id + " tick=" + tick + " seMatrix T=" + V(m.Translation) + " R=" + V(m.Right) + " U=" + V(m.Up) + " B=" + V(m.Backward) +
                           " P*=" + V(pivot) + " camSe=" + V(cam) + " -> o=(" + G(e.o.x) + "," + G(e.o.y) + "," + G(e.o.z) + ") q=(" + G(e.q.x) + "," + G(e.q.y) + "," + G(e.q.z) + "," + G(e.q.w) +
                           ") s=" + G(e.s) + " k=" + G(e.k) + (lagged ? " LAGGED (S3.M4)" : "") + " (S3.C2 input; every pose row is in se-*.jsonl as ev s3.pose)");
            }
            else Jsonl.WriteLineBuffered(row);
            _poseRows++;
            g.HasLogged = true;
            g.LoggedO = e.o; g.LoggedQ = e.q; g.LoggedS = e.s;
            g.LoggedMs = now;
        }

        // ============================================================================================ commands

 /// <summary>A pose row every tick for <paramref name="seconds"/> (S3.C2 / S3.N2 windows).</summary>
        public static void Trace(double seconds)
        {
            _traceUntilMs = Win32.TickCount64() + (ulong)Math.Max(0, Math.Min(600, seconds) * 1000);
        }

        /// <summary>One coloured-export item per distinct (material with a _cm, HSV) of the mirrored parts (S3.C13).</summary>
        public static List<ColourExportItem> ExportItems()
        {
            var items = new List<ColourExportItem>();
            var seen = new HashSet<string>();
            foreach (MirroredGrid g in Grids.Values) AddExportItems(g, items, seen);
            return items;
        }

        private static List<ColourExportItem> ExportItemsFor(MirroredGrid g)
        {
            var items = new List<ColourExportItem>();
            AddExportItems(g, items, new HashSet<string>());
            return items;
        }

        private static void AddExportItems(MirroredGrid g, List<ColourExportItem> items, HashSet<string> seen)
        {
            foreach (PartSnapshot p in HeldOf(g).Values)
            {
                ModelEntry e;
                if (!Models.TryGetValue(p.ModelKey, out e) || e.Model == null) continue;
                foreach (MaterialInfo mi in e.Model.Materials)
                {
                    if (string.IsNullOrEmpty(mi.ColorMetalRaw)) continue;
                    string key = mi.Key + "|" + G(p.H) + "|" + G(p.S) + "|" + G(p.V);
                    if (!seen.Add(key)) continue;
                    items.Add(new ColourExportItem { Material = mi, H = p.H, S = p.S, V = p.V });
                }
            }
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["streaming"] = Gate.Streaming;
            d["waiting"] = Gate.Waiting;
            d["streamStarts"] = Gate.Starts;
            d["streamReason"] = _streamReason;
            d["c3Held"] = _holdC3;
            d["c3Queue"] = C3.Count;
            d["c3QueueBytes"] = C3.PendingBytes;
            d["c3Written"] = C3.TotalWritten;
            d["c3Bytes"] = C3.TotalBytes;
            d["c3Full"] = C3.FullEvents;
            d["c4Queue"] = C4.Count;
            d["c4Written"] = C4.TotalWritten;
            d["c4Bytes"] = C4.TotalBytes;
            d["c4Full"] = C4.FullEvents;
            d["c2Writes"] = _c2Writes;
            d["modelsLoaded"] = Loader.Loaded;
            d["modelsFailed"] = Loader.Failed;
            d["modelsPending"] = Loader.Pending;
            d["modelsQueued"] = _modelsQueued;
            d["partAdds"] = _partAdds;
            d["partRemoves"] = _partRemoves;
            d["partRecolors"] = _partRecolors;
            d["auditChecks"] = Audit.Checks;
            d["auditResends"] = Audit.Resends;
            d["auditPending"] = Audit.Pending;
            d["auditLastReason"] = Audit.LastReason;
            var grids = new List<object>();
            foreach (MirroredGrid g in Grids.Values)
            {
                var gd = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "entityId", g.Id },
                    { "parts", HeldOf(g).Count },
                    { "partsVersion", g.PartsVersion },
                    { "gridAddSent", g.GridAddIndex >= 0 && C4.IsDone(g.GridAddIndex) },
                    { "why", g.Why },
                };
                if (g.S5 != null || g.Floating)
                {
                    gd["s5"] = true;
                    gd["floating"] = g.Floating;
                    gd["subgrid"] = g.Subgrid;
                    gd["parentId"] = g.ParentId;
                    gd["distance"] = g.Distance;
                    if (g.S5 != null)
                    {
                        gd["cells"] = g.S5.Cells.Count;
                        gd["blocks"] = g.S5.Blocks.Count;
                        gd["dynamic"] = g.S5.Dynamic.Count;
                        gd["firstPassDone"] = g.S5.FirstPassDone;
                        gd["deferred"] = GridExporter.DeferredCount(g.S5);
                    }
                }
                if (g.HasPrev)
                {
                    gd["o"] = new object[] { (double)g.Prev.o.x, (double)g.Prev.o.y, (double)g.Prev.o.z };
                    gd["q"] = new object[] { (double)g.Prev.q.x, (double)g.Prev.q.y, (double)g.Prev.q.z, (double)g.Prev.q.w };
                    gd["s"] = (double)g.Prev.s;
                    gd["pivot"] = new object[] { g.PrevPivot.X, g.PrevPivot.Y, g.PrevPivot.Z };
                    gd["seT"] = new object[] { g.PrevM.Translation.X, g.PrevM.Translation.Y, g.PrevM.Translation.Z };
                }
                grids.Add(gd);
            }
            d["grids"] = grids;
            var models = new List<object>();
            foreach (ModelEntry m in Models.Values)
            {
                var md = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "asset", m.Asset },
                    { "key", S3Rows.Hex(m.Key) },
                    { "state", m.State == 1 ? "ready" : m.State == 2 ? "failed" : "loading" },
                    { "sent", m.SentThisStream },
                };
                if (m.Model != null)
                {
                    md["verts"] = m.Model.VertexCount;
                    md["tris"] = m.Model.TriangleCount;
                    md["seVerts"] = m.Model.SeVertexCount;
                    md["seTris"] = m.Model.SeTriangleCount;
                    md["bytes"] = m.PayloadBytes;
                }
                models.Add(md);
            }
            d["models"] = models;
            Link link = _link;
            if (link != null && link.HasSnStatus)
            {
                SnStatus st = link.LastSnStatus;
                d["snMirroredEntityCount"] = st.mirroredEntityCount;
                d["snPendingParts"] = st.pendingParts;
                d["snC3BytesDrained"] = st.c3BytesDrained;
                d["snHelloAckEpoch"] = st.helloAckEpoch;
            }
            return d;
        }

        // ============================================================================================ logs

        private static void Stats(ulong nowMs, Link link, SnStatus st)
        {
            if (nowMs < _nextStatsMs) return;
            // every second while something is mirrored or queued, once a minute otherwise (no log spam in plain play)
            bool busy = Grids.Count > 0 || C3.Count > 0 || C4.Count > 0;
            _nextStatsMs = nowMs + (busy ? StatsIntervalMs : 60000UL);
            if (!busy && C3.TotalWritten == 0 && C4.TotalWritten == 0 && !Gate.Streaming) return;
            var sb = new StringBuilder(512);
            sb.Append("\"streaming\":").Append(Gate.Streaming ? "true" : "false");
            sb.Append(",\"waiting\":").Append(Str(Gate.Waiting));
            sb.Append(",\"grids\":").Append(Grids.Count).Append(",\"parts\":").Append(HeldParts());
            sb.Append(",\"modelsLoaded\":").Append(Loader.Loaded).Append(",\"modelsFailed\":").Append(Loader.Failed).Append(",\"modelsPending\":").Append(Loader.Pending);
            sb.Append(",\"modelsQueued\":").Append(_modelsQueued);
            sb.Append(",\"c3\":{\"queue\":").Append(C3.Count).Append(",\"queueBytes\":").Append(C3.PendingBytes).Append(",\"written\":").Append(C3.TotalWritten)
              .Append(",\"bytes\":").Append(C3.TotalBytes).Append(",\"full\":").Append(C3.FullEvents).Append(",\"budgetStops\":").Append(C3.BudgetStops)
              .Append(",\"held\":").Append(_holdC3 ? "true" : "false").Append(",\"last\":").Append(Str(_lastC3Stop.ToString())).Append('}');
            sb.Append(",\"c4\":{\"queue\":").Append(C4.Count).Append(",\"written\":").Append(C4.TotalWritten).Append(",\"bytes\":").Append(C4.TotalBytes)
              .Append(",\"full\":").Append(C4.FullEvents).Append(",\"last\":").Append(Str(_lastC4Stop.ToString()))
              .Append(",\"lanes\":[").Append(C4.LaneCount(1)).Append(',').Append(C4.LaneCount(2)).Append(',').Append(C4.LaneCount(3)).Append(',').Append(C4.LaneCount(4))
              .Append("],\"backPressureTicks\":").Append(C4.BackPressureTicks).Append(",\"purged\":").Append(C4.Purged).Append('}');
            sb.Append(",\"partAdds\":").Append(_partAdds).Append(",\"partRemoves\":").Append(_partRemoves).Append(",\"partRecolors\":").Append(_partRecolors);
            sb.Append(",\"c2Writes\":").Append(_c2Writes).Append(",\"c2Entities\":").Append(_c2Entities).Append(",\"corrupt\":").Append(_corrupt);
            sb.Append(",\"poseRows\":").Append(_poseRows).Append(",\"poseRowsSkipped\":").Append(_poseRowsSkipped).Append(",\"tracing\":").Append(Win32.TickCount64() < _traceUntilMs ? "true" : "false");
            sb.Append(",\"sn\":{\"mirroredEntityCount\":").Append(st.mirroredEntityCount).Append(",\"pendingParts\":").Append(st.pendingParts)
              .Append(",\"c3BytesDrained\":").Append(st.c3BytesDrained).Append(",\"helloAckEpoch\":").Append(st.helloAckEpoch).Append(",\"snFrame\":").Append(st.snFrame).Append('}');
            sb.Append(",\"audit\":{\"checks\":").Append(Audit.Checks).Append(",\"resends\":").Append(Audit.Resends).Append(",\"pending\":").Append(Str(Audit.Pending)).Append('}');
            JsonRow("stats", sb.ToString());
        }

        private static void JsonRow(string kind, string fields)
        {
            var sb = new StringBuilder(128 + fields.Length);
            S3Rows.Head(sb, Utc(), kind);
            if (fields.Length > 0) sb.Append(',').Append(fields);
            sb.Append('}');
            Jsonl.WriteLine(sb.ToString());
        }

        private static string Utc() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

        private static string Str(string s)
        {
            var sb = new StringBuilder();
            if (s == null) sb.Append("null"); else JsonText.AppendString(sb, s);
            return sb.ToString();
        }

        private static string Q(string s) { return string.IsNullOrEmpty(s) ? "-" : "\"" + s + "\""; }
        private static string G(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static string V(Vec3 v) { return "(" + v.X.ToString("G17", CultureInfo.InvariantCulture) + "," + v.Y.ToString("G17", CultureInfo.InvariantCulture) + "," + v.Z.ToString("G17", CultureInfo.InvariantCulture) + ")"; }

        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }
    }
}
