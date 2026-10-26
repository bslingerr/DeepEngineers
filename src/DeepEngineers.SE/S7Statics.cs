using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using DeepEngineers.Wire;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7 statics into SE, the consumer (ROADMAP S7 "statics into SE"; CONTRACT §5.4 "C12 static primitives (S7;
 /// S7Statics)", ownership row "Static structure collision"): Subnautica's static primitives per 160 m batch become
 /// static Havok collision in SE on non-saved host entities (<see cref="S7StaticsHost"/>).
 /// <list type="bullet">
 /// <item><b>Drain.</b> C12 every tick within the statics budget (≤ 1 ms with the shape building): <c>BatchStatics</c>
 /// is checked whole (<see cref="S7StaticsCodec"/>; a malformed message is dropped and counted); per batch the newest
 /// epoch wins, a live message replaces a census set (<see cref="S7StaticsMath.Supersedes"/>); <c>BatchClear</c> closes
 /// the batch. A new Subnautica generation closes every live batch (epochs restart); a world change closes all.</item>
 /// <item><b>Life cycle.</b> Every 15 ticks the anchors (every S5 mirrored grid or subgrid, and every online player's
 /// character: never "the player") are measured against each batch's primitive bounds (in SN metres, through F of the
 /// anchor; s ≈ 1 within 0.6 m at 300 m): a host is raised while an anchor is within <c>CreateRadiusM</c> (300 m) and
 /// closed beyond <c>CloseRadiusM</c> (350 m) (<see cref="S7StaticsMath.HostWanted"/>). Batches over
 /// <c>MaxPerHost</c> are split into hosts <c>.p&lt;k&gt;</c>.</item>
 /// <item><b>Census cache.</b> Without a live message, a batch's census file
 /// (<c>%LOCALAPPDATA%\DeepEngineers\census\batches\&lt;bx&gt;.&lt;by&gt;.&lt;bz&gt;.c12</c>, written by Subnautica's census
 /// session) is read when an anchor comes near, so a grid parked against a wreck far from the camera collides; a census
 /// batch whose hosts closed is forgotten (re-read when needed).</item>
 /// <item><b>S7.N5 / S7.N10 hooks.</b> <c>de.statics {action: rays, rays: [{o, d, len, dSn}]}</c> casts Subnautica's rays
 /// (SN world) mapped through F⁻¹ with <c>MyPhysics.CastRay</c> and reports the SE hit distance, its distance mapped
 /// back to SN, and whether a statics host was hit; <c>{action: probe, entity, down}</c> casts down from a grid (the
 /// far-grid check); <c>{action: hosts}</c> lists the hosts (their entity ids, for the save check).</item>
 /// </list>
 /// Rows <c>s7.statics</c> {tick, batch, epoch, primitives, host, state created|closed|census|malformed|replaced, ms}
 /// on each change. Seeded failure S7.M8: hosts raised with <c>Save = true</c> (throwaway copy only). Update thread;
 /// never throws into SE. Keyed by batch and host entity id (v3: no single-player assumption).
 /// <para>Members: <c>MyPhysics.CastRay(Vector3D, Vector3D, int)</c> →
 /// <c>MyPhysics.HitInfo?</c>,
 /// <c>MyPhysicsExtensions.GetHitEntity(this HkHitInfo)</c>;
 /// <c>MySession.Static.Players.GetOnlinePlayers</c>, <c>MyPlayer.Character</c>
 /// <c>MyEntity.PositionComp.WorldAABB</c>, <c>Closed</c>.</para>
 /// </summary>
    internal static class S7Statics
    {
        public const string Cmd = "de.statics";
        /// <summary>C12 drain and shape building per tick (CONTRACT: statics ≤ 1 ms).</summary>
        public const double TickBudgetMs = 1.0;
        /// <summary>Anchors are measured every this many ticks (4 Hz).</summary>
        public const int AnchorEveryTicks = 15;
        /// <summary>Census candidates are looked up every this many ticks; at most one file is read per tick.</summary>
        public const int CensusEveryTicks = 30;
        /// <summary>Margin added to the census look-up radius (the SN/SE metre difference and the anchor's motion).</summary>
        public const double CensusMarginM = 40.0;

        private sealed class Batch
        {
            public int Bx, By, Bz;
            public long Key;
            public bool Census;
            public uint Epoch;
            public uint Generation;
            public StaticPrimitive[] Prims;
            public int Count;
            public Vec3 SnMin, SnMax;
            public List<S7StaticsHost> Hosts;
            /// <summary>The previous epoch's raised hosts, kept until this epoch's hosts are up (no gap in the collision).</summary>
            public List<S7StaticsHost> Retiring;
            public uint RetiringEpoch;
            public double Distance = double.PositiveInfinity;
            public string LastAnchor;
            public ulong ReceivedTick;
            public int Kinds0, Kinds1, Kinds2, Kinds3;
        }

        private struct Anchor
        {
            public Vec3 Sn;
            public double Radius;
            public string Label;
        }

        private static readonly Dictionary<long, Batch> Batches = new Dictionary<long, Batch>();
        private static readonly List<S7StaticsHost> BuildQueue = new List<S7StaticsHost>();
        private static readonly List<Anchor> Anchors = new List<Anchor>();
        private static readonly byte[] Buf = new byte[S7StaticsCodec.MaxPayloadBytes + 64];
        private static readonly StaticPrimitive[] Scratch = new StaticPrimitive[S7Conventions.StaticsLimits.MaxPerBatch];
        private static readonly Stopwatch Sw = new Stopwatch();
        private static readonly HashSet<long> CensusIndex = new HashSet<long>();
        private static readonly List<long> CensusPending = new List<long>();
        private static readonly HashSet<long> CensusPendingSet = new HashSet<long>();
        private static readonly long[] Malformed = new long[16];
        private static readonly RateLimit ErrorLog = new RateLimit(10000);

        private static uint _worldEpoch;
        private static bool _worldKnown;
        private static uint _snGeneration;
        private static bool _genKnown;
        private static SiteMap _map;
        private static bool _censusIndexed;
        private static string _censusDir;
        private static ulong _tick;
        private static double _maxTickMs;
        private static bool _disabled;
        private static string _disabledWhy;

        public static long DroppedInTransition;   // S8a (B3 hook): C12 batches dropped while C7 chartSeqApplied lags the chart
        public static long Messages, Clears, Stale, UnknownTypes, HostsCreated, HostsClosed, HostFailures, PrimitivesDropped, CensusLoaded,
                           CensusErrors, GenerationResets, Errors, RaysCast;

        // ============================================================================================= per tick

 /// <summary>Per tick (update thread), from the S7 host after <c>GridMirrorProducer.Tick</c>. Never throws.</summary>
        public static void Tick(ulong nowMs, Link link, uint worldEpoch, bool inWorld)
        {
            if (_disabled) return;
            try { TickCore(nowMs, link, worldEpoch, inWorld); }
            catch (Exception e) when (e is MissingMemberException || e is TypeLoadException)
            {
                // a game member this build does not have: only the statics feature goes off (CONTRACT §5.4)
                _disabled = true;
                _disabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S7 statics OFF missing " + e.Message + " (no statics hosts; everything else keeps running)");
                try { CloseAll("feature off"); } catch (Exception) { }
            }
            catch (Exception e)
            {
                Errors++;
                if (ErrorLog.Allow(nowMs)) DeLog.Info("DE.Error S7 statics #" + Errors + " " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
            }
        }

        /// <summary>
 /// Review F19: S7.M8 (hosts with <c>Save = true</c>) only on a throwaway copy: null when allowed (or no session yet),
 /// else why not
 /// </summary>
        internal static string ThrowawayRefusal()
        {
            try
            {
                MySession s = MySession.Static;
                if (s == null) return null; // DEEPENGINEERS_MUTANTS at Init: checked again once the world runs (TickCore)
                string path = s.CurrentPath;
                if (string.IsNullOrEmpty(path)) return "the session has no save folder";
                return File.Exists(Path.Combine(path, "DE_THROWAWAY.txt")) ? null : "not a throwaway copy: no DE_THROWAWAY.txt in " + path;
            }
            catch (Exception e) { return "the save folder could not be checked (" + e.GetType().Name + ")"; }
        }

        private static uint _m8CheckedEpoch = uint.MaxValue;

        private static void TickCore(ulong nowMs, Link link, uint worldEpoch, bool inWorld)
        {
            _tick++;
            if (S7Mutants.M8Guard == null) S7Mutants.M8Guard = ThrowawayRefusal; // review F19 (S7Mutants is game-free)
            if (!inWorld)
            {
                if (Batches.Count > 0) CloseAll("left the world");
                return;
            }
            if (S7Mutants.M8 && _m8CheckedEpoch != worldEpoch)
            {
                // review F19: the startup check (an M8 set before the session existed is validated against this world)
                _m8CheckedEpoch = worldEpoch;
                string why = ThrowawayRefusal();
                if (why != null)
                {
                    S7Mutants.M8 = false;
                    DeLog.Info(S7Mutants.M8RefusedLine + ": " + why);
                }
            }
            if (!_worldKnown || worldEpoch != _worldEpoch)
            {
                if (_worldKnown) CloseAll("world epoch " + _worldEpoch + " -> " + worldEpoch);
                _worldKnown = true;
                _worldEpoch = worldEpoch;
                _censusIndexed = false;
            }
            SiteMap map = SiteBinding.IsOk ? SiteBinding.Map : null;
            if (map == null) return; // no site: nothing can be placed (the ring waits; C12 is drained once bound)
            if (!ReferenceEquals(map, _map))
            {
                // the primitives are SN-world data: only the hosts (built through the old map) go; the life cycle rebuilds them
                if (_map != null) CloseHostsOnly("site map rebound");
                _map = map;
            }
            Sw.Reset();
            Sw.Start();

            if (link != null && link.State == LinkState.Up)
            {
                uint gen = link.LastSnGeneration;
                if (!_genKnown || gen != _snGeneration)
                {
                    if (_genKnown) CloseLive("new Subnautica generation " + _snGeneration + " -> " + gen);
                    _genKnown = true;
                    _snGeneration = gen;
                    GenerationResets++;
                }
                Drain(link);
            }
            if (!_censusIndexed) IndexCensus();
            if (_tick % AnchorEveryTicks == 0) LifeCycle();
            if (_tick % CensusEveryTicks == 0) FindCensus();
            if (CensusPending.Count > 0 && Sw.Elapsed.TotalMilliseconds < TickBudgetMs) LoadOneCensus();
            Build();
            double ms = Sw.Elapsed.TotalMilliseconds;
            if (ms > _maxTickMs) _maxTickMs = ms;
        }

        // ============================================================================================= C12

        private static void Drain(Link link)
        {
            while (Sw.Elapsed.TotalMilliseconds < TickBudgetMs)
            {
                uint type;
                int n;
                RingReadStatus st = link.TryReadStatics(out type, Buf, out n);
                if (st != RingReadStatus.Ok) return;
                Messages++;
                if (type == C12Type.BatchStatics && S8aChartManager.InTransition) { DroppedInTransition++; continue; }   // S8a (B3 hook, CONTRACT "chart.apply"): Subnautica-positioned batches of the previous chart are dropped
                if (type == C12Type.BatchStatics) OnBatch(n, false, null);
                else if (type == C12Type.BatchClear) OnClear(n);
                else
                {
                    UnknownTypes++;
                    DeLog.Info("DE.S7 statics: unknown C12 message type " + type + " (" + n + " B) dropped");
                }
            }
        }

        private static unsafe void OnBatch(int bytes, bool census, string source)
        {
            BatchStaticsHeader h;
            int count;
            S7StaticsStatus st = S7StaticsCodec.TryDecode(Buf, bytes, out h, Scratch, out count);
            if (st != S7StaticsStatus.Ok)
            {
                Malformed[(int)st & 15]++;
                DeLog.Info("DE.S7 statics: malformed " + (census ? "census file " + source : "C12 message") + " (" + S7StaticsCodec.StatusName(st) + ", " + bytes +
                           " B, batch " + h.bx + "." + h.by + "." + h.bz + " epoch " + h.epoch + "): dropped whole");
                Row("malformed", h.bx, h.by, h.bz, h.epoch, 0, null, 0, S7StaticsCodec.StatusName(st));
                return;
            }
            long key = S7StaticsMath.BatchKey(h.bx, h.by, h.bz);
            Batch cur;
            bool have = Batches.TryGetValue(key, out cur);
            if (!S7StaticsMath.Supersedes(have, have && cur.Census, have ? cur.Epoch : 0, census, h.epoch))
            {
                Stale++;
                return;
            }
            var b = new Batch { Bx = h.bx, By = h.by, Bz = h.bz, Key = key, Census = census, Epoch = h.epoch, Generation = _snGeneration, Count = count, ReceivedTick = _tick };
            if (have)
            {
                // a newer epoch: the raised hosts of the old one stay until the new ones are up; unbuilt ones go now
                CloseRetiring(cur, "replaced again");
                if (cur.Hosts != null)
                {
                    var keep = new List<S7StaticsHost>();
                    foreach (S7StaticsHost oh in cur.Hosts)
                    {
                        if (oh.Built) keep.Add(oh);
                        else { oh.Close(); BuildQueue.Remove(oh); }
                    }
                    cur.Hosts = null;
                    if (keep.Count > 0) { b.Retiring = keep; b.RetiringEpoch = cur.Epoch; }
                }
                Batches.Remove(key);
            }
            b.Prims = new StaticPrimitive[count];
            Array.Copy(Scratch, b.Prims, count);
            Bounds(b);
            Batches[key] = b;
            if (census)
            {
                CensusLoaded++;
                Row("census", b.Bx, b.By, b.Bz, b.Epoch, b.Count, null, 0, source);
            }
            // the life cycle decides at once (an anchor may already be in range)
            Evaluate(b);
            if (b.Hosts == null) CloseRetiring(b, "replaced (epoch " + b.Epoch + "), no anchor in range");
        }

        /// <summary>Closes a batch's previous-epoch hosts (once its new hosts are up, or when it needs none).</summary>
        private static void CloseRetiring(Batch b, string why)
        {
            if (b.Retiring == null) return;
            foreach (S7StaticsHost h in b.Retiring)
            {
                bool wasBuilt = h.Built;
                h.Close();
                if (wasBuilt)
                {
                    HostsClosed++;
                    Row("closed", b.Bx, b.By, b.Bz, b.RetiringEpoch, h.Count, h, h.BuildMs, why);
                }
            }
            b.Retiring = null;
        }

        private static unsafe void OnClear(int bytes)
        {
            BatchClear c;
            fixed (byte* p = Buf)
            {
                if (!S7StaticsCodec.TryDecodeClear(p, bytes, out c)) { Malformed[(int)S7StaticsStatus.BadCount]++; return; }
            }
            Clears++;
            long key = S7StaticsMath.BatchKey(c.bx, c.by, c.bz);
            Batch b;
            if (!Batches.TryGetValue(key, out b) || b.Census || c.epoch < b.Epoch) return;
            CloseHosts(b, "BatchClear epoch " + c.epoch);
            Batches.Remove(key);
        }

        private static void Bounds(Batch b)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (int i = 0; i < b.Count; i++)
            {
                StaticPrimitive p = b.Prims[i];
                double r;
                switch (p.kind)
                {
                    case StaticKind.Capsule: r = p.size.x + p.size.y; b.Kinds1++; break;
                    case StaticKind.Sphere: r = p.size.x; b.Kinds2++; break;
                    case StaticKind.MeshObb: r = Math.Sqrt((double)p.size.x * p.size.x + (double)p.size.y * p.size.y + (double)p.size.z * p.size.z); b.Kinds3++; break;
                    default: r = Math.Sqrt((double)p.size.x * p.size.x + (double)p.size.y * p.size.y + (double)p.size.z * p.size.z); b.Kinds0++; break;
                }
                x0 = Math.Min(x0, p.centreSn.x - r); y0 = Math.Min(y0, p.centreSn.y - r); z0 = Math.Min(z0, p.centreSn.z - r);
                x1 = Math.Max(x1, p.centreSn.x + r); y1 = Math.Max(y1, p.centreSn.y + r); z1 = Math.Max(z1, p.centreSn.z + r);
            }
            if (b.Count == 0) { Vec3 c = S7StaticsMath.BatchCentreSn(b.Bx, b.By, b.Bz); x0 = x1 = c.X; y0 = y1 = c.Y; z0 = z1 = c.Z; }
            b.SnMin = new Vec3(x0, y0, z0);
            b.SnMax = new Vec3(x1, y1, z1);
        }

        // ============================================================================================= life cycle

        private static void GatherAnchors()
        {
            Anchors.Clear();
            SiteMap map = _map;
            if (map == null) return;
            foreach (GridMirrorProducer.MirroredGrid mg in GridMirrorProducer.Grids.Values)
            {
                MyEntity e = mg.Entity ?? mg.Grid;
                if (e == null || e.Closed || e.MarkedForClose) continue;
                if (mg.Missile || mg.Projection) continue; // S7p-scene-above: a missile in flight or a hologram anchors no statics host
                BoundingBoxD bb = e.PositionComp.WorldAABB;
                Vector3D c = bb.Center;
                Vec3 sn = map.SeToSn(new Vec3(c.X, c.Y, c.Z));
                Anchors.Add(new Anchor { Sn = sn, Radius = 0.5 * bb.Size.Length(), Label = "grid " + e.EntityId.ToString(CultureInfo.InvariantCulture) });
            }
            MySession session = MySession.Static;
            if (session != null && session.Players != null)
            {
                foreach (var player in session.Players.GetOnlinePlayers())
                {
                    MyCharacter ch = player != null ? player.Character : null;
                    if (ch == null || ch.Closed || ch.MarkedForClose) continue;
                    Vector3D p = ch.PositionComp.GetPosition();
                    Anchors.Add(new Anchor { Sn = map.SeToSn(new Vec3(p.X, p.Y, p.Z)), Radius = 2.0, Label = "character " + ch.EntityId.ToString(CultureInfo.InvariantCulture) });
                }
            }
        }

        private static double DistanceToBounds(Vec3 p, Vec3 lo, Vec3 hi)
        {
            double dx = Math.Max(0.0, Math.Max(lo.X - p.X, p.X - hi.X));
            double dy = Math.Max(0.0, Math.Max(lo.Y - p.Y, p.Y - hi.Y));
            double dz = Math.Max(0.0, Math.Max(lo.Z - p.Z, p.Z - hi.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static void LifeCycle()
        {
            GatherAnchors();
            var drop = new List<long>();
            foreach (Batch b in Batches.Values)
            {
                Decide(b);
                if (b.Census && b.Hosts == null && b.Distance > S7Conventions.StaticsLimits.CloseRadiusM + 2 * CensusMarginM) drop.Add(b.Key);
            }
            foreach (long k in drop) Batches.Remove(k); // a census batch far from every anchor: forgotten, re-read when needed
        }

        /// <summary>A batch that just arrived is decided at once against fresh anchors.</summary>
        private static void Evaluate(Batch b)
        {
            GatherAnchors();
            Decide(b);
        }

        private static void Decide(Batch b)
        {
            double best = double.PositiveInfinity;
            string label = null;
            for (int i = 0; i < Anchors.Count; i++)
            {
                double d = Math.Max(0.0, DistanceToBounds(Anchors[i].Sn, b.SnMin, b.SnMax) - Anchors[i].Radius);
                if (d < best) { best = d; label = Anchors[i].Label; }
            }
            b.Distance = best;
            b.LastAnchor = label;
            bool exists = b.Hosts != null;
            bool want = b.Count > 0 && S7StaticsMath.HostWanted(exists, best);
            if (want && !exists) CreateHosts(b);
            else if (!want && exists) CloseHosts(b, "no anchor within " + S7Conventions.StaticsLimits.CloseRadiusM + " m (nearest " + Fmt(best) + " m)");
        }

        private static void CreateHosts(Batch b)
        {
            int parts = S7StaticsMath.HostParts(b.Count);
            b.Hosts = new List<S7StaticsHost>(parts);
            for (int k = 0; k < parts; k++)
            {
                int start, n;
                S7StaticsMath.HostRange(k, b.Count, out start, out n);
                string name = S7StaticsMath.HostName(b.Bx, b.By, b.Bz, b.Epoch, parts == 1 ? -1 : k);
                var h = new S7StaticsHost(name, parts == 1 ? -1 : k, _map, b.Prims, start, n);
                b.Hosts.Add(h);
                BuildQueue.Add(h);
            }
        }

        private static void CloseHosts(Batch b, string why)
        {
            CloseRetiring(b, why);
            if (b.Hosts == null) return;
            foreach (S7StaticsHost h in b.Hosts)
            {
                bool wasBuilt = h.Built;
                h.Close();
                BuildQueue.Remove(h);
                if (wasBuilt)
                {
                    HostsClosed++;
                    Row("closed", b.Bx, b.By, b.Bz, b.Epoch, h.Count, h, h.BuildMs, why);
                }
            }
            b.Hosts = null;
        }

        private static void Build()
        {
            bool saveMutant = S7Mutants.M8;
            while (BuildQueue.Count > 0 && Sw.Elapsed.TotalMilliseconds < TickBudgetMs)
            {
                S7StaticsHost h = BuildQueue[0];
                if (!h.Step(S7StaticsHost.AddsPerTick, TickBudgetMs, Sw, saveMutant)) return; // continues next tick
                BuildQueue.RemoveAt(0);
                Batch b = BatchOf(h);
                if (h.Failed)
                {
                    HostFailures++;
                    if (b != null && b.Retiring != null && b.Hosts != null && b.Hosts.TrueForAll(x => x.Built || x.Failed || x.Closed))
                        CloseRetiring(b, "superseded by epoch " + b.Epoch + " (a host failed)");
                    DeLog.Info("DE.S7 statics: host " + h.Name + " not raised (" + h.Error + "): that batch has no collision");
                    if (b != null) Row("failed", b.Bx, b.By, b.Bz, b.Epoch, h.Count, h, h.BuildMs, h.Error);
                    continue;
                }
                if (h.Closed) continue;
                HostsCreated++;
                if (b != null && b.Retiring != null && b.Hosts != null && b.Hosts.TrueForAll(x => x.Built || x.Failed || x.Closed))
                    CloseRetiring(b, "superseded by epoch " + b.Epoch);
                PrimitivesDropped += h.Skipped;
                if (h.Skipped > 0) DeLog.Info("DE.S7 statics: host " + h.Name + " dropped " + h.Skipped + " primitive(s) (" + h.LastDrop + ")");
                if (saveMutant) DeLog.Info("DE.S7 statics MUTANT S7.M8: host " + h.Name + " raised with Save = true (throwaway copy only)");
                if (b != null) Row("created", b.Bx, b.By, b.Bz, b.Epoch, h.Count, h, h.BuildMs, b.LastAnchor, h.BakeMs, h.RaiseMs);
            }
        }

        private static Batch BatchOf(S7StaticsHost h)
        {
            foreach (Batch b in Batches.Values)
                if (b.Hosts != null && b.Hosts.Contains(h)) return b;
            return null;
        }

        private static void CloseLive(string why)
        {
            var drop = new List<long>();
            foreach (Batch b in Batches.Values)
                if (!b.Census) { CloseHosts(b, why); drop.Add(b.Key); }
            foreach (long k in drop) Batches.Remove(k);
            if (drop.Count > 0) DeLog.Info("DE.S7 statics: " + drop.Count + " live batch(es) closed (" + why + ")");
        }

        private static void CloseAll(string why)
        {
            int n = 0;
            foreach (Batch b in Batches.Values) { if (b.Hosts != null) n += b.Hosts.Count; CloseHosts(b, why); }
            Batches.Clear();
            BuildQueue.Clear();
            CensusPending.Clear();
            CensusPendingSet.Clear();
            if (n > 0) DeLog.Info("DE.S7 statics: every host closed (" + n + "; " + why + ")");
        }

        private static void CloseHostsOnly(string why)
        {
            foreach (Batch b in Batches.Values) CloseHosts(b, why);
            BuildQueue.Clear();
        }

        /// <summary>World unload (session component): every host closed, the state cleared (S7.N10: nothing to save).</summary>
        public static void OnUnload()
        {
            try { CloseAll("world unload"); }
            catch (Exception e) { DeLog.Info("DE.Error S7 statics unload " + e.GetType().Name + ": " + e.Message); }
            _worldKnown = false;
            _genKnown = false;
            _map = null;
            _censusIndexed = false;
            CensusIndex.Clear();
        }

        // ============================================================================================= census cache

        private static void IndexCensus()
        {
            _censusIndexed = true;
            CensusIndex.Clear();
            try
            {
                _censusDir = S7StaticsMath.CensusBatchesFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (!Directory.Exists(_censusDir))
                {
                    DeLog.Info("DE.S7 statics: no census cache (" + _censusDir + "): statics collide only where Subnautica streamed around the camera");
                    return;
                }
                foreach (string f in Directory.GetFiles(_censusDir, "*" + S7StaticsMath.CensusExtension))
                {
                    int bx, by, bz;
                    if (S7StaticsMath.TryParseCensusFileName(Path.GetFileName(f), out bx, out by, out bz)) CensusIndex.Add(S7StaticsMath.BatchKey(bx, by, bz));
                }
                DeLog.Info("DE.S7 statics: census cache " + CensusIndex.Count + " batch file(s) in " + _censusDir);
            }
            catch (Exception e)
            {
                CensusErrors++;
                DeLog.Info("DE.S7 statics: census cache not readable (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        private static void FindCensus()
        {
            if (CensusIndex.Count == 0) return;
            if (Anchors.Count == 0) GatherAnchors();
            double reach = S7Conventions.StaticsLimits.CreateRadiusM + CensusMarginM;
            for (int i = 0; i < Anchors.Count; i++)
            {
                Vec3 p = Anchors[i].Sn;
                double r = reach + Anchors[i].Radius;
                int bx0, by0, bz0, bx1, by1, bz1;
                S7StaticsMath.BatchOfSn(new Vec3(p.X - r, p.Y - r, p.Z - r), out bx0, out by0, out bz0);
                S7StaticsMath.BatchOfSn(new Vec3(p.X + r, p.Y + r, p.Z + r), out bx1, out by1, out bz1);
                for (int bx = bx0; bx <= bx1; bx++)
                    for (int by = by0; by <= by1; by++)
                        for (int bz = bz0; bz <= bz1; bz++)
                        {
                            long key = S7StaticsMath.BatchKey(bx, by, bz);
                            if (!CensusIndex.Contains(key) || Batches.ContainsKey(key) || CensusPendingSet.Contains(key)) continue;
                            if (S7StaticsMath.DistanceToBatchSn(p, bx, by, bz) > r) continue;
                            CensusPendingSet.Add(key);
                            CensusPending.Add(key);
                        }
            }
        }

        private static void LoadOneCensus()
        {
            long key = CensusPending[0];
            CensusPending.RemoveAt(0);
            CensusPendingSet.Remove(key);
            if (Batches.ContainsKey(key)) return; // a live message came first
            int bx, by, bz;
            S7StaticsMath.UnpackBatchKey(key, out bx, out by, out bz);
            string path = Path.Combine(_censusDir ?? "", S7StaticsMath.CensusFileName(bx, by, bz));
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > Buf.Length) { CensusErrors++; DeLog.Info("DE.S7 statics: census file " + path + " missing or too large"); return; }
                int n;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    n = 0;
                    int r;
                    while (n < Buf.Length && (r = f.Read(Buf, n, Buf.Length - n)) > 0) n += r;
                }
                OnBatch(n, true, Path.GetFileName(path));
            }
            catch (Exception e)
            {
                CensusErrors++;
                DeLog.Info("DE.S7 statics: census file " + path + " unreadable (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        // ============================================================================================= queries

 /// <summary>True when the entity is one of our statics hosts (alive in this session).</summary>
        public static bool IsHost(IMyEntity e)
        {
            if (e == null) return false;
            foreach (Batch b in Batches.Values)
            {
                if (b.Hosts != null) foreach (S7StaticsHost h in b.Hosts) if (h.Built && h.EntityId == e.EntityId) return true;
                if (b.Retiring != null) foreach (S7StaticsHost h in b.Retiring) if (h.Built && h.EntityId == e.EntityId) return true;
            }
            var me = e as MyEntity;
            return me != null && S7StaticsMath.IsHostName(me.DisplayName);
        }

        /// <summary>Entity ids of every raised host (the S7.N10 save check looks for them).</summary>
        public static List<long> HostIds()
        {
            var l = new List<long>();
            foreach (Batch b in Batches.Values)
            {
                if (b.Hosts != null) foreach (S7StaticsHost h in b.Hosts) if (h.Built) l.Add(h.EntityId);
                if (b.Retiring != null) foreach (S7StaticsHost h in b.Retiring) if (h.Built) l.Add(h.EntityId);
            }
            return l;
        }

        // ============================================================================================= commands

 /// <summary>
 /// <c>de.statics {action: status | hosts | rays, rays: [{o, d, len, dSn?}] | probe, entity, down | census}</c>
 /// (dev builds; the SE half of S7.N5, S7.N10). Routed by <c>S7Commands</c>. Update thread.
 /// </summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            try
            {
                string action = (MiniJson.GetString(args, "action") ?? "status").Trim().ToLowerInvariant();
                switch (action)
                {
                    case "status": return Ok(Status(), StatusLine());
                    case "hosts": return Ok(Hosts(), StatusLine());
                    case "rays": return Rays(args);
                    case "probe": return Probe(args);
                    case "census":
                        _censusIndexed = false;
                        IndexCensus();
                        return Ok(Status(), "DE.S7 statics census cache re-indexed: " + CensusIndex.Count + " file(s)");
                    default:
                        return Fail("de.statics: action status | hosts | rays {rays: [{o, d, len, dSn}]} | probe {entity, down} | census");
                }
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Error de.statics " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return Fail("de.statics failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static string StatusLine()
        {
            int hosts = 0, built = 0;
            foreach (Batch b in Batches.Values)
                if (b.Hosts != null) foreach (S7StaticsHost h in b.Hosts) { hosts++; if (h.Built) built++; }
            return "DE.S7 statics batches=" + Batches.Count + " hosts=" + built + "/" + hosts + " messages=" + Messages + " malformed=" + Malformed.Sum() +
                   " census=" + CensusIndex.Count + " files (" + CensusLoaded + " loaded) snGen=" + _snGeneration + (S7Mutants.M8 ? " MUTANT S7.M8" : "");
        }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            int live = 0, census = 0, hosts = 0, built = 0, prims = 0, building = BuildQueue.Count;
            foreach (Batch b in Batches.Values)
            {
                if (b.Census) census++; else live++;
                prims += b.Count;
                if (b.Hosts != null) foreach (S7StaticsHost h in b.Hosts) { hosts++; if (h.Built) built++; }
            }
            d["tick"] = (long)_tick;
            d["snGeneration"] = (long)_snGeneration;
            d["siteBound"] = _map != null;
            d["batches"] = Batches.Count;
            d["liveBatches"] = live;
            d["censusBatches"] = census;
            d["primitives"] = prims;
            d["hosts"] = hosts;
            d["hostsBuilt"] = built;
            d["building"] = building;
            d["messages"] = Messages;
            var mal = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 1; i < Malformed.Length; i++) if (Malformed[i] != 0) mal[S7StaticsCodec.StatusName((S7StaticsStatus)i)] = Malformed[i];
            d["malformed"] = mal;
            d["clears"] = Clears;
            d["stale"] = Stale;
            d["unknownTypes"] = UnknownTypes;
            d["hostsCreated"] = HostsCreated;
            d["hostsClosed"] = HostsClosed;
            d["hostFailures"] = HostFailures;
            d["primitivesDropped"] = PrimitivesDropped;
            d["censusFiles"] = CensusIndex.Count;
            d["censusLoaded"] = CensusLoaded;
            d["censusPending"] = CensusPending.Count;
            d["censusErrors"] = CensusErrors;
            d["censusDir"] = _censusDir;
            d["anchors"] = Anchors.Count;
            d["generationResets"] = GenerationResets;
            d["errors"] = Errors;
            d["maxTickMs"] = _maxTickMs;
            d["raysCast"] = RaysCast;
            d["mutantM8"] = S7Mutants.M8;
            if (_disabled) d["disabled"] = _disabledWhy;
            d["hostIds"] = HostIds().Select(x => (object)x.ToString(CultureInfo.InvariantCulture)).ToList();
            return d;
        }

        private static Dictionary<string, object> Hosts()
        {
            var d = Status();
            var list = new List<object>();
            foreach (Batch b in Batches.Values.OrderBy(x => x.Distance))
            {
                var hl = new List<object>();
                if (b.Hosts != null)
                    foreach (S7StaticsHost h in b.Hosts)
                        hl.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "name", h.Name }, { "entityId", h.EntityId.ToString(CultureInfo.InvariantCulture) }, { "built", h.Built }, { "failed", h.Error },
                            { "primitives", h.Count }, { "added", h.Added }, { "skipped", h.Skipped }, { "ms", Math.Round(h.BuildMs, 3) },
                            { "origin", new object[] { h.Origin.X, h.Origin.Y, h.Origin.Z } },
                        });
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "batch", new object[] { b.Bx, b.By, b.Bz } }, { "epoch", (long)b.Epoch }, { "source", b.Census ? "census" : "live" },
                    { "primitives", b.Count }, { "kinds", new object[] { b.Kinds0, b.Kinds1, b.Kinds2, b.Kinds3 } },
                    { "distance", double.IsInfinity(b.Distance) ? (object)null : Math.Round(b.Distance, 2) }, { "anchor", b.LastAnchor }, { "hosts", hl },
                });
            }
            d["list"] = list;
            return d;
        }

        /// <summary>
 /// S7.N5, the SE half: each ray (SN world: origin o, unit direction d, length len, optional Subnautica distance dSn,
 /// optional <c>static</c>: Subnautica's first hit is one of its static colliders) cast from F⁻¹(o) to F⁻¹(o + d·len).
 /// Review F6: every hit along the ray
 /// and the first hit
 /// whose entity is a statics host is the one compared (voxel terrain of the S4 provider or another entity in front no
 /// longer counts as a mismatch). Per ray: hit, host, dSe, dSnOfSe = |F(host hit) − o|, diff, firstHost (the first hit
 /// is a host), the first hit's name. Summary: n, hits, hostHits, bothHit, p95 of |dSnOfSe − dSn| over rays with SN
 /// <c>static</c> and a host hit, and the counters <c>snStatic</c>, <c>seMissWhereSnStatic</c> (Subnautica hit a
 /// static, SE has no host along the ray: a coverage gap, S7.M3), <c>firstHitNotHost</c> and <c>snNotStatic</c>.
 /// </summary>
        private static Dictionary<string, object> Rays(IDictionary<string, object> args)
        {
            SiteMap map = _map ?? (SiteBinding.IsOk ? SiteBinding.Map : null);
            if (map == null) return Fail("de.statics rays: the site is not bound");
            object v;
            var rays = args != null && args.TryGetValue("rays", out v) ? v as IList<object> : null;
            if (rays == null || rays.Count == 0) return Fail("de.statics rays: {rays: [{o: [x,y,z], d: [x,y,z], len, dSn}]} (the rays of Subnautica's de.statics rays reply)");
            var outList = new List<object>();
            var diffs = new List<double>();
            int hits = 0, hostHits = 0, both = 0, snStatic = 0, seMiss = 0, firstNotHost = 0, snNotStatic = 0;
            var all = new List<MyPhysics.HitInfo>();
            for (int i = 0; i < rays.Count; i++)
            {
                var r = rays[i] as IDictionary<string, object>;
                Vec3 o, dir;
                if (r == null || !TryVec(r, "o", out o) || !TryVec(r, "d", out dir)) { outList.Add(new Dictionary<string, object> { { "i", i }, { "error", "o and d needed" } }); continue; }
                double len = MiniJson.GetNumber(r, "len") ?? 60.0;
                double? dSn = MiniJson.GetNumber(r, "dSn");
                bool? isStatic = MiniJson.GetBool(r, "static");
                Vec3 from = map.SnToSe(o), to = map.SnToSe(o + dir.Normalized() * len);
                var fromD = new Vector3D(from.X, from.Y, from.Z);
                RaysCast++;
                MyPhysics.CastRay(fromD, new Vector3D(to.X, to.Y, to.Z), all, 0);
                all.Sort((x, y) => Vector3D.DistanceSquared(x.Position, fromD).CompareTo(Vector3D.DistanceSquared(y.Position, fromD)));
                var row = new Dictionary<string, object>(StringComparer.Ordinal) { { "i", i } };
                int hostAt = -1;
                IMyEntity firstEntity = null, hostEntity = null;
                for (int k = 0; k < all.Count; k++)
                {
                    IMyEntity he = null;
                    try { he = all[k].HkHitInfo.GetHitEntity(); } catch (Exception) { }
                    if (k == 0) firstEntity = he;
                    if (IsHost(he)) { hostAt = k; hostEntity = he; break; }
                }
                if (isStatic == true) snStatic++;
                else if (isStatic == false) snNotStatic++;
                if (all.Count > 0)
                {
                    hits++;
                    row["hit"] = true;
                    row["firstHost"] = hostAt == 0;
                    row["firstName"] = firstEntity is MyEntity ? ((MyEntity)firstEntity).DisplayName : firstEntity != null ? firstEntity.GetType().Name : null;
                    if (hostAt != 0) firstNotHost++;
                }
                else row["hit"] = false;
                if (hostAt >= 0)
                {
                    hostHits++;
                    Vector3D hp = all[hostAt].Position;
                    var hpv = new Vec3(hp.X, hp.Y, hp.Z);
                    double dSe = (hpv - from).Length;
                    double dSnOfSe = (map.SeToSn(hpv) - o).Length;
                    row["host"] = true;
                    row["dSe"] = dSe;
                    row["dSnOfSe"] = dSnOfSe;
                    row["entity"] = hostEntity != null ? hostEntity.EntityId.ToString(CultureInfo.InvariantCulture) : null;
                    row["name"] = hostEntity is MyEntity ? ((MyEntity)hostEntity).DisplayName : null;
                    if (dSn.HasValue && isStatic != false) { both++; double diff = Math.Abs(dSnOfSe - dSn.Value); diffs.Add(diff); row["diff"] = diff; }
                }
                else
                {
                    row["host"] = false;
                    if (isStatic == true) seMiss++; // Subnautica hit one of its statics, SE has no host along the ray
                }
                if (dSn.HasValue) row["dSn"] = dSn.Value;
                if (isStatic.HasValue) row["static"] = isStatic.Value;
                outList.Add(row);
            }
            all.Clear();
            diffs.Sort();
            double p95 = diffs.Count > 0 ? diffs[Math.Min(diffs.Count - 1, (int)Math.Ceiling(0.95 * diffs.Count) - 1)] : double.NaN;
            // S7-1 fix: hosts follow anchors (mirrored grids, online characters), never the spectator camera; the
 // S7-1 run cast its rays with the character 1 km away (no host anywhere): the reply names the nearest anchor
            string site = MiniJson.GetString(args, "site");
            string anchorLabel = null;
            double anchorM = double.PositiveInfinity;
            Vec3 o0;
            var r0 = rays[0] as IDictionary<string, object>;
            if (r0 != null && TryVec(r0, "o", out o0))
            {
                GatherAnchors();
                for (int k = 0; k < Anchors.Count; k++)
                {
                    double dk = (Anchors[k].Sn - o0).Length;
                    if (dk < anchorM) { anchorM = dk; anchorLabel = Anchors[k].Label; }
                }
            }
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "n", rays.Count }, { "hits", hits }, { "hostHits", hostHits }, { "bothHit", both }, { "p95DiffM", double.IsNaN(p95) ? (object)null : p95 },
                { "snStatic", snStatic }, { "seMissWhereSnStatic", seMiss }, { "firstHitNotHost", firstNotHost }, { "snNotStatic", snNotStatic },
                { "site", site }, { "nearestAnchorM", double.IsInfinity(anchorM) ? (object)null : Math.Round(anchorM, 2) }, { "nearestAnchor", anchorLabel },
                { "rays", outList },
            };
            RowRays(rays.Count, hits, hostHits, both, p95, snStatic, seMiss, firstNotHost, snNotStatic, site, anchorM);
            return Ok(d, "DE.S7 statics rays n=" + rays.Count + " hits=" + hits + " hostHits=" + hostHits + " snStatic=" + snStatic + " seMiss=" + seMiss +
                         " firstHitNotHost=" + firstNotHost + " p95|dSE-dSN|=" + (double.IsNaN(p95) ? "n/a" : Fmt(p95)) + " m");
        }

        /// <summary>S7.N5 far grid: a ray from an entity's box centre down (towards the planet centre) by <c>down</c> m (default 60).</summary>
        private static Dictionary<string, object> Probe(IDictionary<string, object> args)
        {
            SiteMap map = _map ?? (SiteBinding.IsOk ? SiteBinding.Map : null);
            if (map == null) return Fail("de.statics probe: the site is not bound");
            Vector3D from, to;
            string idText = MiniJson.GetString(args, "entity");
            long id;
            if (idText != null && long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                MyEntity e;
                if (!MyEntities.TryGetEntityById(id, out e) || e == null) return Fail("de.statics probe: entity " + id + " not found");
                from = e.PositionComp.WorldAABB.Center;
                Vector3D up = Vector3D.Normalize(from - new Vector3D(map.C.X, map.C.Y, map.C.Z));
                double down = MiniJson.GetNumber(args, "down") ?? 60.0;
                to = from - up * down;
            }
            else
            {
                Vec3 f, t;
                var a = args as IDictionary<string, object>;
                if (!TryVec(a, "from", out f) || !TryVec(a, "to", out t)) return Fail("de.statics probe: {entity, down} or {from: [SE x,y,z], to: [SE x,y,z]}");
                from = new Vector3D(f.X, f.Y, f.Z); to = new Vector3D(t.X, t.Y, t.Z);
            }
            RaysCast++;
            MyPhysics.HitInfo? hi = MyPhysics.CastRay(from, to, 0);
            var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "from", new object[] { from.X, from.Y, from.Z } }, { "to", new object[] { to.X, to.Y, to.Z } } };
            string line;
            if (hi.HasValue)
            {
                IMyEntity he = null;
                try { he = hi.Value.HkHitInfo.GetHitEntity(); } catch (Exception) { }
                bool host = IsHost(he);
                d["hit"] = true;
                d["distance"] = (hi.Value.Position - from).Length();
                d["host"] = host;
                d["entity"] = he != null ? he.EntityId.ToString(CultureInfo.InvariantCulture) : null;
                d["name"] = he is MyEntity ? ((MyEntity)he).DisplayName : null;
                line = "DE.S7 statics probe hit " + (host ? "a statics host " : "") + (d["name"] ?? "?") + " at " + Fmt((double)d["distance"]) + " m";
            }
            else
            {
                d["hit"] = false;
                line = "DE.S7 statics probe: no hit";
            }
            return Ok(d, line);
        }

        private static bool TryVec(IDictionary<string, object> o, string key, out Vec3 v)
        {
            v = Vec3.Zero;
            object x;
            if (o == null || !o.TryGetValue(key, out x)) return false;
            var l = x as IList<object>;
            if (l == null || l.Count != 3) return false;
            double[] d = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (l[i] is double) d[i] = (double)l[i];
                else if (!(l[i] is string) || !double.TryParse((string)l[i], NumberStyles.Float, CultureInfo.InvariantCulture, out d[i])) return false;
            }
            v = new Vec3(d[0], d[1], d[2]);
            return v.IsFinite;
        }

        // ============================================================================================= rows

        private static void Row(string state, int bx, int by, int bz, uint epoch, int primitives, S7StaticsHost h, double ms, string note)
        {
            Row(state, bx, by, bz, epoch, primitives, h, ms, note, double.NaN, double.NaN);
        }

        /// <param name="bakeMs">review F22: the single <c>Bake</c> call's time (NaN: not a created row)</param>
 /// <param name="raiseMs">review F22: <c>MyEntities.Add</c> + <c>CreateFromCollisionObject</c> (NaN: not a created row)</param>
        private static void Row(string state, int bx, int by, int bz, uint epoch, int primitives, S7StaticsHost h, double ms, string note, double bakeMs, double raiseMs)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.statics" }, { "tick", (long)FrameWriter.LastTick },
                { "staticsTick", (long)_tick }, { "batch", new object[] { bx, by, bz } }, { "epoch", (long)epoch }, { "primitives", primitives }, { "state", state },
                { "host", h != null ? h.Name : null }, { "entityId", h != null && h.EntityId != 0 ? h.EntityId.ToString(CultureInfo.InvariantCulture) : null },
                { "added", h != null ? h.Added : 0 }, { "skipped", h != null ? h.Skipped : 0 }, { "ms", Math.Round(ms, 3) },
                { "snGeneration", (long)_snGeneration }, { "note", note }, { "save", h != null && S7Mutants.M8 },
            };
            if (!double.IsNaN(bakeMs)) d["bakeMs"] = Math.Round(bakeMs, 3);
            if (!double.IsNaN(raiseMs)) d["raiseMs"] = Math.Round(raiseMs, 3);
            Jsonl.WriteLineBuffered(JsonText.Serialize(d));
        }

        private static void RowRays(int n, int hits, int hostHits, int both, double p95, int snStatic, int seMiss, int firstNotHost, int snNotStatic,
                                    string site, double anchorM)
        {
            // S7-1 fix: "tick" is the C1 tick (FrameWriter.LastTick) like every other S7 row; the S7-1 run's rows carried this
 // class's own counter (about 590 ticks ahead of C1), so the analyzer filed them under the next n5 site window
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7.statics" }, { "tick", (long)FrameWriter.LastTick },
                { "staticsTick", (long)_tick }, { "state", "rays" }, { "site", site },
                { "n", n }, { "hits", hits }, { "hostHits", hostHits }, { "bothHit", both }, { "p95DiffM", double.IsNaN(p95) ? (object)null : p95 },
                { "snStatic", snStatic }, { "seMissWhereSnStatic", seMiss }, { "firstHitNotHost", firstNotHost }, { "snNotStatic", snNotStatic },
                { "nearestAnchorM", double.IsInfinity(anchorM) ? (object)null : Math.Round(anchorM, 2) },
            };
            Jsonl.WriteLineBuffered(JsonText.Serialize(d));
        }

        private static string Fmt(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        private static Dictionary<string, object> Ok(Dictionary<string, object> d, string line)
        {
            d["ok"] = true;
            d["line"] = line;
            return d;
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S7 statics " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
