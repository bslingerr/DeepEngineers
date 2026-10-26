using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Sandbox.Engine.Voxels;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// Wire <see cref="S8aOreOverlay"/>, docs/S8A-PLANET-V2.md
 /// section 13): the crater's veins keep the cover rule's 2 m of the real map above them, and the map's own surface voxels over a
 /// vein take the vein's Subnautica type through the drill layer (the same batch versions, store-keyed, the drill converter's
 /// worker and writer). The game thread plans: every <see cref="PlanEveryMs"/> the crater batches within <see cref="RadiusM"/> of
 /// the camera not painted at the rule's current generation are queued, nearest first. The worker takes one per wake
 /// (<see cref="WorkerStep"/>; a batch with a pending drill job is left to that job), skips a batch with no surface octree in the
 /// install (cached), loads the base (the newest store version, else the install), runs the pass and writes a version only when
 /// a type changed (not live: a function of the saved edits and the rule). A drill job runs the pass inline over its edits grown
 /// by the overlay's reach (<see cref="ApplyInline"/>: a vein drilled out loses its paint in the same version) and the batches
 /// within the reach of its edits are painted again (<see cref="InvalidateAround"/>). Off: DEEPENGINEERS_S8A_ORE_OVERLAY=off;
 /// S8a.M31 writes nothing. <c>de.s8a oreoverlay {status | cover {radius, sn} | vein {sn, radius} | expect {points}}</c>.
 /// </summary>
    internal static class S8aOreOverlaySe
    {
        public const string EnvSwitch = "DEEPENGINEERS_S8A_ORE_OVERLAY";
        /// <summary>The planner's reach around the camera (metres, to the batch's box).</summary>
        public const double RadiusM = 160.0;
        public const ulong PlanEveryMs = 1000;
        /// <summary>At most one ore version per this many milliseconds (Subnautica reloads every batch written).</summary>
        public const long WriteGapMs = 400;
        public const int MaxQueue = 512;
        /// <summary>The cover scan's octree cap (<c>cover</c>; game thread).</summary>
        public const int MaxCoverOctrees = 12;

        private static readonly object Lock = new object();
        private static readonly List<KeyValuePair<double, Int3Key>> _queue = new List<KeyValuePair<double, Int3Key>>();
        private static readonly HashSet<Int3Key> _queued = new HashSet<Int3Key>();
        private static readonly Dictionary<Int3Key, int> _done = new Dictionary<Int3Key, int>();
        private static readonly Dictionary<Int3Key, bool> _hasSurface = new Dictionary<Int3Key, bool>();   // worker only
        private static S8aOreOverlay _worker;
        private static S8aCraterOreRule _workerRule;
        private static ulong _nextPlanMs;
        private static long _lastWriteMs;
        private static long _passes, _writes, _painted, _restored, _errors, _inline, _inlineChanged, _skippedPending, _noSurface, _lastOreSurface;
        private static double _lastMs, _maxMs;
        private static string _last;
        private static bool _envRead, _envOn = true;

        public static bool On { get { ReadEnv(); return _envOn && TerrainTiles.CraterOres != null; } }

        private static void ReadEnv()
        {
            if (_envRead) return;
            _envRead = true;
            _envOn = S8aOverlay.EnvOn(EnvSwitch, true);

            // DEEPENGINEERS_S8A_ORE_SHOW_M, default 0 = no paint (the pass still runs: it restores what an earlier session painted)
            double show = 0.0;
            try
            {
                string v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S8A_ORE_SHOW_M");
                double d;
                if (!string.IsNullOrEmpty(v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d >= 0.0)
                    show = Math.Min(d, S8aOreOverlay.ShowRadiusM);
            }
            catch (Exception) { }
            S8aOreOverlay.ShowRadiusSetting = show;
        }

        /// <summary>A new converter run: nothing queued, nothing painted yet this session (a pass that finds the store painted writes nothing).</summary>
        public static void Reset()
        {
            lock (Lock)
            {
                _queue.Clear();
                _queued.Clear();
                _done.Clear();
                _hasSurface.Clear();
            }
            _nextPlanMs = 0;
            _lastWriteMs = 0;
            _passes = _writes = _painted = _restored = _errors = _inline = _inlineChanged = _skippedPending = _noSurface = _lastOreSurface = 0;
            _lastMs = _maxMs = 0;
            _last = null;
        }

        // ============================================================================================ the planner (game thread)

        public static void Tick(ulong nowMs, Vec3 camSe, bool haveCam)
        {
            if (!haveCam || nowMs < _nextPlanMs || !S8aDrillConverter.Running || !On) return;
            _nextPlanMs = nowMs + PlanEveryMs;
            SiteMap map = TerrainTiles.Map;
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            if (map == null || rule == null) return;
            Vec3 cam = map.SeToSn(camSe);
            if (Math.Abs(cam.X) > S8aCraterOres.MapHalfM + RadiusM || Math.Abs(cam.Z) > S8aCraterOres.MapHalfM + RadiusM) return;
            int gen = rule.Generation;
            int bv = SnWorld.BatchVoxels;
            int r = (int)Math.Ceiling(RadiusM);
            int cvx = (int)Math.Floor(cam.X - SnWorld.OriginX), cvy = (int)Math.Floor(cam.Y - SnWorld.OriginY), cvz = (int)Math.Floor(cam.Z - SnWorld.OriginZ);
            int b0x = S8aConvert.BatchOfVoxel(cvx - r), b1x = S8aConvert.BatchOfVoxel(cvx + r);
            int b0z = S8aConvert.BatchOfVoxel(cvz - r), b1z = S8aConvert.BatchOfVoxel(cvz + r);
            lock (Lock)
            {
                for (int bx = b0x; bx <= b1x; bx++)
                    for (int bz = b0z; bz <= b1z; bz++)
                    {
                        if (!S8aBatchFile.IsCraterId(bx, 0, bz)) continue;
                        double hx = Gap(cvx, bx * bv, bx * bv + bv - 1), hz = Gap(cvz, bz * bv, bz * bv + bv - 1);
                        if (hx * hx + hz * hz > RadiusM * RadiusM) continue;
                        for (int by = 0; by < SnWorld.BatchesY; by++)
                        {
                            var id = new Int3Key(bx, by, bz);
                            int g;
                            if (_queued.Contains(id) || (_done.TryGetValue(id, out g) && g == gen)) continue;
                            _queued.Add(id);
                            _queue.Add(new KeyValuePair<double, Int3Key>(0.0, id));
                        }
                    }
                // nearest first (the camera's distance to the batch's box), the rest dropped beyond the cap
                for (int i = 0; i < _queue.Count; i++)
                {
                    Int3Key id = _queue[i].Value;
                    double dx = Gap(cvx, id.X * bv, id.X * bv + bv - 1), dy = Gap(cvy, id.Y * bv, id.Y * bv + bv - 1), dz = Gap(cvz, id.Z * bv, id.Z * bv + bv - 1);
                    _queue[i] = new KeyValuePair<double, Int3Key>(dx * dx + dy * dy + dz * dz, id);
                }
                _queue.Sort((a, b) => a.Key.CompareTo(b.Key));
                while (_queue.Count > MaxQueue)
                {
                    _queued.Remove(_queue[_queue.Count - 1].Value);
                    _queue.RemoveAt(_queue.Count - 1);
                }
            }
        }

        private static double Gap(int v, int lo, int hi) { return v < lo ? lo - v : v > hi ? v - hi : 0.0; }

        /// <summary>The batches within the overlay's reach of the SN voxel box (inclusive) paint again (worker, after a drill version).</summary>
        public static void InvalidateAround(int x0, int y0, int z0, int x1, int y1, int z1, Int3Key except)
        {
            int g = S8aOreOverlay.Reach + 1;
            int b0x = S8aConvert.BatchOfVoxel(x0 - g), b1x = S8aConvert.BatchOfVoxel(x1 + g);
            int b0y = S8aConvert.BatchOfVoxel(y0 - g), b1y = S8aConvert.BatchOfVoxel(y1 + g);
            int b0z = S8aConvert.BatchOfVoxel(z0 - g), b1z = S8aConvert.BatchOfVoxel(z1 + g);
            lock (Lock)
                for (int bx = b0x; bx <= b1x; bx++)
                    for (int by = b0y; by <= b1y; by++)
                        for (int bz = b0z; bz <= b1z; bz++)
                        {
                            var id = new Int3Key(bx, by, bz);
                            if (!id.Equals(except)) _done.Remove(id);
                        }
        }

        // ============================================================================================ the worker

        private static S8aOreOverlay Worker(S8aCraterOreRule rule)
        {
            if (_worker == null || !ReferenceEquals(_workerRule, rule))
            {
                _worker = new S8aOreOverlay(rule, S8aOreOverlay.DefaultRowTypes());
                _workerRule = rule;
            }
            return _worker;
        }

        /// <summary>True when the install holds a near-surface octree in the batch (cached; SnOctreeSource's index, worker thread).</summary>
        private static bool HasSurface(Int3Key id)
        {
            bool has;
            lock (Lock) if (_hasSurface.TryGetValue(id, out has)) return has;
            has = false;
            SnOctreeSource src = TerrainTiles.Source;
            int per = SnWorld.OctreesPerBatch;
            if (src != null)
                for (int ix = 0; ix < per && !has; ix++)
                    for (int iy = 0; iy < per && !has; iy++)
                        for (int iz = 0; iz < per && !has; iz++)
                            has = src.GetOctreeState(id.X * per + ix, id.Y * per + iy, id.Z * per + iz) == OctreeState.Mixed;
            lock (Lock) _hasSurface[id] = has;
            return has;
        }

        /// <summary>One queued batch: its pass, a version when a type changed (worker; called once per wake by the drill converter).</summary>
        public static void WorkerStep(long now, Func<Int3Key, bool> drillPending)
        {
            if (!On || now - _lastWriteMs < WriteGapMs) return;
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            if (rule == null) return;
            Int3Key id;
            lock (Lock)
            {
                if (_queue.Count == 0) return;
                id = _queue[0].Value;
                _queue.RemoveAt(0);
                _queued.Remove(id);
            }
            if (drillPending != null && drillPending(id)) { _skippedPending++; return; }   // its drill job paints it; the planner queues it again
            int gen = rule.Generation;
            if (!HasSurface(id))
            {
                _noSurface++;
                lock (Lock) _done[id] = gen;
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            S8aBatchFile batch;
            string from;
            if (!S8aDrillConverter.LoadBaseFor(id, out batch, out from)) return;
            S8aOreOverlay core = Worker(rule);
            core.SolidOutside = NeighbourSolid();
            S8aOreOverlayResult res = core.Apply(batch, () => S8aDrillConverter.LoadInstall(id));
            core.SolidOutside = null;
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _passes++;
            _lastMs = ms;
            if (ms > _maxMs) _maxMs = ms;
            _lastOreSurface = res.OreSurface;
            _last = id + " octrees " + res.OctreesVisited + " ore " + res.OctreesWithOre + " rewritten " + res.OctreesRewritten + " painted " + res.Painted + " restored " + res.Restored +
                    " oreSurface " + res.OreSurface + " vein " + res.VeinVoxels + " mined " + res.Mined + " (" + from + ", " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms)";
            if (res.Changed && S8aDrillConverter.WriteOreVersion(id, batch, res, from, t0))
            {
                _writes++;
                _painted += res.Painted;
                _restored += res.Restored;
                _lastWriteMs = now;
            }
            lock (Lock) _done[id] = gen;
        }

        /// <summary>The pass over a drill job's batch, the octrees within the overlay's reach of the edits' SN box (worker); null when off.</summary>
        public static S8aOreOverlayResult ApplyInline(S8aBatchFile batch, int ex0, int ey0, int ez0, int ex1, int ey1, int ez1)
        {
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            if (!On || rule == null || batch == null) return null;
            int g = S8aOreOverlay.Reach + 1, bv = SnWorld.BatchVoxels;
            int vbx = batch.Bx * bv, vby = batch.By * bv, vbz = batch.Bz * bv;
            var id = new Int3Key(batch.Bx, batch.By, batch.Bz);
            S8aOreOverlay core = Worker(rule);
            core.SolidOutside = NeighbourSolid();
            S8aOreOverlayResult res;
            try
            {
                res = core.Apply(batch, () => S8aDrillConverter.LoadInstall(id), oi =>
                {
                    int ix, iy, iz;
                    S8aBatchFile.OctreeCoords(oi, out ix, out iy, out iz);
                    int x0 = vbx + ix * 32, y0 = vby + iy * 32, z0 = vbz + iz * 32;
                    return x0 <= ex1 + g && x0 + 31 >= ex0 - g && y0 <= ey1 + g && y0 + 31 >= ey0 - g && z0 <= ez1 + g && z0 + 31 >= ez0 - g;
                });
            }
            finally { core.SolidOutside = null; }
            _inline++;
            if (res.Changed)
            {
                _inlineChanged++;
                _painted += res.Painted;
                _restored += res.Restored;
            }
            return res;
        }

        /// <summary>
 /// The base's solidity at a world voxel of any batch (the newest store version, else the install, else empty), each file
 /// loaded and each octree decoded once per closure.
 /// </summary>
        internal static Func<int, int, int, bool> NeighbourSolid()
        {
            var files = new Dictionary<Int3Key, S8aBatchFile>();
            var dec = new Dictionary<long, byte[]>();
            int bv = SnWorld.BatchVoxels;
            return (wx, wy, wz) =>
            {
                int bx = S8aConvert.BatchOfVoxel(wx), by = S8aConvert.BatchOfVoxel(wy), bz = S8aConvert.BatchOfVoxel(wz);
                if (by < 0 || by >= SnWorld.BatchesY) return false;
                var id = new Int3Key(bx, by, bz);
                S8aBatchFile f;
                if (!files.TryGetValue(id, out f))
                {
                    string from;
                    if (!S8aDrillConverter.LoadBaseFor(id, out f, out from)) f = null;
                    files[id] = f;
                }
                if (f == null) return true;
                int lx = wx - bx * bv, ly = wy - by * bv, lz = wz - bz * bv;
                int oi = S8aBatchFile.OctreeIndex(lx >> 5, ly >> 5, lz >> 5);
                if (!f.Has(oi)) return false;
                long key = ((((long)(bx + 64) * 128 + (by + 64)) * 128 + (bz + 64)) << 8) | (long)oi;
                byte[] r;
                if (!dec.TryGetValue(key, out r))
                {
                    var t = new byte[S8aOctree.Voxels];
                    var d = new byte[S8aOctree.Voxels];
                    f.DecodeOctree(oi, t, d);
                    r = new byte[2 * S8aOctree.Voxels];
                    Buffer.BlockCopy(t, 0, r, 0, S8aOctree.Voxels);
                    Buffer.BlockCopy(d, 0, r, S8aOctree.Voxels, S8aOctree.Voxels);
                    dec[key] = r;
                }
                int li = S8aOctree.Index(lx & 31, ly & 31, lz & 31);
                return S8aOctree.IsBelowSurface(r[li], r[S8aOctree.Voxels + li]);
            };
        }

        // ============================================================================================ status, commands (game thread)

        public static Dictionary<string, object> Status()
        {
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            int queued, done;
            lock (Lock) { queued = _queue.Count; done = _done.Count; }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "on", On }, { "m31", S8aWireMutants.M31OverlayOff }, { "radiusM", RadiusM }, { "showRadiusM", S8aOreOverlay.ShowRadiusM },
                { "minCoverM", rule != null ? rule.MinCoverM : 0.0 }, { "coverOn", rule != null && rule.SolidAt != null }, { "coverDropped", rule != null ? rule.CoverDropped : 0L },
                { "generation", rule != null ? (long)rule.Generation : -1L }, { "queued", (long)queued }, { "done", (long)done }, { "passes", _passes }, { "writes", _writes },
                { "painted", _painted }, { "restored", _restored }, { "inline", _inline }, { "inlineChanged", _inlineChanged }, { "skippedPending", _skippedPending },
                { "noSurface", _noSurface }, { "lastOreSurface", _lastOreSurface }, { "errors", _errors }, { "lastMs", _lastMs }, { "maxMs", _maxMs }, { "last", _last },
            };
        }

        public static Dictionary<string, object> Command(IDictionary<string, object> args, Vec3 camSe, bool haveCam)
        {
            args = args ?? new Dictionary<string, object>();
            SiteMap map = TerrainTiles.Map;
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            Dictionary<string, object> st = Status();
            st["ok"] = true;
            if (args.ContainsKey("cover")) return Cover(args, camSe, haveCam, map, st);
            if (args.ContainsKey("vein")) return Vein(args, map, rule, st);
            if (args.ContainsKey("expect")) return Expect(args, map, rule, st);
            st["line"] = "de.s8a oreoverlay: " + (On ? "on" : "off") + (S8aWireMutants.M31OverlayOff ? " (S8a.M31 ON: writes nothing)" : "") + ", queued " + st["queued"] + ", done " + st["done"] +
                         ", passes " + _passes + ", writes " + _writes + ", painted " + _painted + ", restored " + _restored + ", inline " + _inline + "/" + _inlineChanged +
                         ", cover " + st["minCoverM"] + " m (dropped " + st["coverDropped"] + ")";
            return st;
        }

        private static Dictionary<string, object> Fail(string error) { return new Dictionary<string, object> { { "ok", false }, { "error", error }, { "line", "de.s8a oreoverlay: " + error } }; }

        private static bool Point(IDictionary<string, object> a, string key, out Vec3 p)
        {
            p = Vec3.Zero;
            object o;
            var l = a.TryGetValue(key, out o) ? o as IList<object> : null;
            if (l == null || l.Count < 3) return false;
            p = new Vec3(Convert.ToDouble(l[0], CultureInfo.InvariantCulture), Convert.ToDouble(l[1], CultureInfo.InvariantCulture), Convert.ToDouble(l[2], CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
 /// <c>cover {radius, sn}</c> (S8a.NV.cover): SE's own ore voxels (the tiles' storages at LOD 0, edits included) in the install's
 /// near-surface octrees within radius of the point (default the camera), against the real map (the install's octrees): an
 /// ore voxel where the map is not solid is above its surface, one with an empty face neighbour on it, one with an empty voxel in
 /// its 3 x 3 x 3 neighbourhood within the cover.
 /// </summary>
        private static Dictionary<string, object> Cover(IDictionary<string, object> a, Vec3 camSe, bool haveCam, SiteMap map, Dictionary<string, object> st)
        {
            SnOctreeSource src = TerrainTiles.Source;
            if (map == null || src == null || TerrainTiles.Tiles.Count == 0) return Fail("cover: no terrain tiles");
            Vec3 c;
            if (!Point(a, "sn", out c))
            {
                if (!haveCam) return Fail("cover: no camera and no sn");
                c = map.SeToSn(camSe);
            }
            double radius = Math.Max(4.0, Math.Min(64.0, MiniJson.GetNumber(a, "radius") ?? 24.0));
            double up = MiniJson.GetNumber(a, "up") ?? 64.0;
            int per = SnWorld.OctreesPerBatch;
            var cand = new List<KeyValuePair<double, int[]>>();
            int o0x = (int)Math.Floor((c.X - radius - SnWorld.OriginX) / 32.0), o1x = (int)Math.Floor((c.X + radius - SnWorld.OriginX) / 32.0);
            int o0z = (int)Math.Floor((c.Z - radius - SnWorld.OriginZ) / 32.0), o1z = (int)Math.Floor((c.Z + radius - SnWorld.OriginZ) / 32.0);
            int o0y = (int)Math.Floor((c.Y - up - SnWorld.OriginY) / 32.0), o1y = (int)Math.Floor((c.Y + 16.0 - SnWorld.OriginY) / 32.0);
            for (int ox = o0x; ox <= o1x; ox++)
                for (int oz = o0z; oz <= o1z; oz++)
                    for (int oy = o0y; oy <= o1y; oy++)
                    {
                        if (oy < 0 || oy >= SnWorld.BatchesY * per) continue;
                        if (src.GetOctreeState(ox, oy, oz) != OctreeState.Mixed) continue;
                        double cx = SnWorld.OriginX + ox * 32 + 16 - c.X, cy = SnWorld.OriginY + oy * 32 + 16 - c.Y, cz = SnWorld.OriginZ + oz * 32 + 16 - c.Z;
                        cand.Add(new KeyValuePair<double, int[]>(cx * cx + cy * cy + cz * cz, new[] { ox, oy, oz }));
                    }
            cand.Sort((x, y) => x.Key.CompareTo(y.Key));
            var stats = new S8aOreCoverStats();
            var core = new S8aConvertCore();
            var source = new S8aDrillConverter.TileSource(new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial));
            Func<int, int, int, bool> mapSolid = (wx, wy, wz) =>
            {
                byte t, d;
                int leaf;
                return src.TrySampleVoxel(wx, wy, wz, out t, out d, out leaf) && S8aOctree.IsBelowSurface(t, d);
            };
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < cand.Count && i < MaxCoverOctrees; i++)
                core.ScanOreCover(map, source, cand[i].Value[0], cand[i].Value[1], cand[i].Value[2], mapSolid, stats);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            var by = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<byte, long> kv in stats.BySnType) by[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            var ex = new List<object>();
            foreach (double[] e in stats.Examples) ex.Add(new object[] { e[0], e[1], e[2], (long)e[3], (long)e[4] });
            var r = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ok", true }, { "action", "cover" }, { "sn", new object[] { c.X, c.Y, c.Z } }, { "radius", radius }, { "candidates", (long)cand.Count },
                { "octrees", (long)stats.Octrees }, { "skipped", (long)stats.Skipped }, { "sampled", stats.Sampled }, { "ore", stats.Ore }, { "above", stats.Above },
                { "face", stats.Face }, { "cube", stats.Cube }, { "bySnType", by }, { "examples", ex }, { "ms", ms }, { "minCoverM", st["minCoverM"] }, { "coverOn", st["coverOn"] },
            };
            r["line"] = "de.s8a oreoverlay cover: " + stats.Ore + " ore voxels in " + stats.Octrees + " octrees (" + stats.Sampled + " sampled), above the map " + stats.Above +
                        ", on its surface " + stats.Face + ", within the cover " + stats.Cube + " (" + ms.ToString("0", CultureInfo.InvariantCulture) + " ms)";
            return r;
        }

        private static void WorldVoxel(Vec3 sn, out int wx, out int wy, out int wz)
        {
            wx = (int)Math.Floor(sn.X - SnWorld.OriginX);
            wy = (int)Math.Floor(sn.Y - SnWorld.OriginY);
            wz = (int)Math.Floor(sn.Z - SnWorld.OriginZ);
        }

        /// <summary><c>vein {sn, radius}</c>: the vein voxels the base still holds within radius voxels (a cube) of the point: count, centroid, extent, rows.</summary>
        private static Dictionary<string, object> Vein(IDictionary<string, object> a, SiteMap map, S8aCraterOreRule rule, Dictionary<string, object> st)
        {
            if (rule == null || !S8aDrillConverter.Running) return Fail("vein: no crater ore rule or no drill converter");
            Vec3 p;
            if (!Point(a, "sn", out p)) return Fail("vein: sn [x, y, z] (SN metres)");
            int radius = (int)Math.Max(1, Math.Min(16, MiniJson.GetNumber(a, "radius") ?? 8));
            int wx, wy, wz;
            WorldVoxel(p, out wx, out wy, out wz);
            var core = new S8aOreOverlay(rule, S8aOreOverlay.DefaultRowTypes());
            var vox = new List<int[]>();
            int n = core.VeinAround(wx, wy, wz, radius, NeighbourSolid(), vox, 1 << 16);
            double sx = 0, sy = 0, sz = 0;
            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (int[] v in vox)
            {
                sx += v[0]; sy += v[1]; sz += v[2];
                string ore = S8aCraterOres.Rows[v[3]].Subtype;
                object k;
                rows[ore] = (rows.TryGetValue(ore, out k) ? (long)k : 0L) + 1L;
            }
            var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "action", "vein" }, { "sn", new object[] { p.X, p.Y, p.Z } }, { "radius", (long)radius }, { "count", (long)n }, { "rows", rows } };
            if (vox.Count > 0)
            {
                double off = S8aConvert.SampleOffset;
                double cx = sx / vox.Count, cy = sy / vox.Count, cz = sz / vox.Count, far = 0;
                foreach (int[] v in vox) far = Math.Max(far, Math.Sqrt((v[0] - cx) * (v[0] - cx) + (v[1] - cy) * (v[1] - cy) + (v[2] - cz) * (v[2] - cz)));
                r["centroid"] = new object[] { SnWorld.OriginX + cx + off, SnWorld.OriginY + cy + off, SnWorld.OriginZ + cz + off };
                r["extentM"] = far;
                var list = new List<object>();
                for (int i = 0; i < vox.Count && i < 64; i++) list.Add(new object[] { SnWorld.OriginX + vox[i][0] + off, SnWorld.OriginY + vox[i][1] + off, SnWorld.OriginZ + vox[i][2] + off, S8aCraterOres.Rows[vox[i][3]].Subtype });
                r["voxels"] = list;
            }
            r["line"] = "de.s8a oreoverlay vein: " + n + " vein voxels within " + radius + " of " + p.X.ToString("0.0", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.0", CultureInfo.InvariantCulture) + "," +
                        p.Z.ToString("0.0", CultureInfo.InvariantCulture);
            return r;
        }

        /// <summary><c>expect {points: [[x, y, z],...]}</c>: per SN point the type the overlay gives that voxel now (0: none), from the rule and the base.</summary>
        private static Dictionary<string, object> Expect(IDictionary<string, object> a, SiteMap map, S8aCraterOreRule rule, Dictionary<string, object> st)
        {
            if (rule == null || !S8aDrillConverter.Running) return Fail("expect: no crater ore rule or no drill converter");
            object po;
            var pts = a.TryGetValue("points", out po) ? po as IList<object> : null;
            if (pts == null) return Fail("expect: points [[x, y, z], ...]");
            var core = new S8aOreOverlay(rule, S8aOreOverlay.DefaultRowTypes());
            Func<int, int, int, bool> solid = NeighbourSolid();
            var types = new List<object>(pts.Count);
            foreach (object o in pts)
            {
                var l = o as IList<object>;
                if (l == null || l.Count < 3) { types.Add(null); continue; }
                int wx, wy, wz;
                WorldVoxel(new Vec3(Convert.ToDouble(l[0], CultureInfo.InvariantCulture), Convert.ToDouble(l[1], CultureInfo.InvariantCulture), Convert.ToDouble(l[2], CultureInfo.InvariantCulture)), out wx, out wy, out wz);
                types.Add((long)core.ExpectedType(wx, wy, wz, solid));
            }
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", true }, { "action", "expect" }, { "types", types }, { "line", "de.s8a oreoverlay expect: " + types.Count + " points" } };
        }
    }
}
