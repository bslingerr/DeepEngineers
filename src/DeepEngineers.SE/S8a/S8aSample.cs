using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a planet v2, route B's first slice (docs/S8A-PLANET-V2.md section 12; CONTRACT §3.3 "The sample"): one chunk of the real
 /// Subnautica map (Grassy Plateaus, 640 m; DeepEngineers.Terrain.S8aChunkField, the twin of tools/planet/s8a_chunks.py) served by
 /// the planet's own generator at a fixed planet point 20 km from the site, exactly as Stage F2 serves the ring's brushes: the same
 /// <c>MyPlanetStorageProvider.ReadRange</c> prefix
 /// answers content reads at LOD &lt;= S8aRing.MaxLod whose box meets the sample's zone, the
 /// Intersect postfix widens such boxes, the window classifies them as surface; materials stay SE's; everything else is SE's
 /// own generator. Deterministic (IEEE double, square roots only; vectors tests/s8a/vectors/sample/). The chunk's entities, as
 /// placed, go to Subnautica through <c>sample-entities.tsv</c> (Wire S8aSampleEntities) in the current chart's SN coordinates.
 /// Off unless DEEPENGINEERS_S8A=on and DEEPENGINEERS_S8A_SAMPLE=on (the launcher sets it for the parts ns and nl only); with it
 /// on the window's and the charts' keys carry the sample's identity, so no window cache made without it is served.
 /// </summary>
    internal static class S8aSample
    {
        public const string EnvSample = "DEEPENGINEERS_S8A_SAMPLE";
        public const double NearM = 1500.0;            // the entity file is kept while the camera is this close to the anchor's column
        private static readonly object InitLock = new object();
        private static volatile S8aChunkField _field;
        private static volatile int _state;             // 0 untried, 1 ready, -1 refused
        private static string _refusal, _initLine;
        private static bool _initLogged, _envRead;
        private static double _initMs;
        private static int _kicked;
        private static double _ax = double.NaN, _ay, _az;   // the anchor (planet-local, from the placement text): the cheap pre-check
        private static double _preR;
        private static long _reads, _voxels, _detects, _errors, _intersectWidened, _servedOutside, _surfaces;
        private static string _lastError;
        private static ulong _identity;
        private static bool _identityDone;
        private static readonly object StatLock = new object();
        private static readonly float[][] Res = NewRes();
        private static readonly long[] ResCount = new long[16], ResVoxels = new long[16];
        private static readonly double[] ResMax = new double[16], ResTotal = new double[16];
        // entities
        private static double[] _ex, _ey, _ez;          // planet-local positions of the chunk's entities as placed
        private static Quat[] _eq;                      // their rotations in the tangent frame (turned)
        private static long _writeSeq, _writes, _writeErrors, _lastCount;
        private static ulong _nextWriteMs;
        private static Vec3 _lastWriteCam;
        private static uint _lastChartSeq = uint.MaxValue;
        private static bool _wroteEmpty;
        private static string _lastFile;

        public static bool Setting { get; private set; }
        public static S8aChunkField Field { get { return _state == 1 ? _field : null; } }

        private static float[][] NewRes()
        {
            var r = new float[16][];
            for (int i = 0; i < r.Length; i++) r[i] = new float[1024];
            return r;
        }

        public static void ReadEnv()
        {
            if (_envRead) return;
            _envRead = true;
            S8aOverlay.ReadEnv();
            Setting = S8aOverlay.Enabled && string.Equals(Environment.GetEnvironmentVariable(EnvSample), "on", StringComparison.OrdinalIgnoreCase);
            if (!Setting) return;
            try
            {
                S8aSamplePlacement p = S8aSamplePlacement.Parse(File.ReadAllText(S8aSamplePlacement.DefaultPath()));
                _ax = p.Ax * RingRules.Rs; _ay = p.Ay * RingRules.Rs; _az = p.Az * RingRules.Rs;
                _preR = p.HalfM * 1.5 + 700.0;
            }
            catch (Exception e) { _refusal = "no placement (" + e.GetType().Name + ": " + e.Message + ")"; _state = -1; }
        }

        // ================================================================================================= the identity (store keys)

        private static string FileFnv(string path)
        {
            try { return File.Exists(path) ? RingHash.Fnv(File.ReadAllBytes(path)).ToString("x16", CultureInfo.InvariantCulture) : "missing"; }
            catch (Exception) { return "missing"; }
        }

        /// <summary>fnv1a64(utf8 'sample|' + placement x16 + '|' + chunk bin x16 + '|' + chunk index x16) of the store's files (S8aChunkField.Identity).</summary>
        public static ulong Identity()
        {
            if (_identityDone) return _identity;
            string name = "gp1";
            try { name = S8aSamplePlacement.Parse(File.ReadAllText(S8aSamplePlacement.DefaultPath())).Chunk; } catch (Exception) { }
            string dir = S8aChunk.DefaultFolder();
            // "types": since the window takes the chunk's native block types (ns 20261024-134042), so no window file made before is served
            _identity = RingHash.FnvUtf8("sample|types|" + FileFnv(S8aSamplePlacement.DefaultPath()) + "|" + FileFnv(Path.Combine(dir, S8aChunk.BinName(name))) + "|" +
                                         FileFnv(Path.Combine(dir, S8aChunk.IndexName(name))));
            _identityDone = true;
            return _identity;
        }

        /// <summary>The window's sculpt hash with the sample on: fnv1a64(utf8 sculptHash x16 + '|sample|' + identity x16).</summary>
        public static ulong FoldInto(ulong sculptHash)
        {
            return RingHash.FnvUtf8(sculptHash.ToString("x16", CultureInfo.InvariantCulture) + "|sample|" + Identity().ToString("x16", CultureInfo.InvariantCulture));
        }

        // ================================================================================================= the field (once)

        private static S8aChunkField Ensure(MyPlanetStorageProvider p)
        {
            if (_state == 1) return _field;
            if (_state == -1 || p == null) return null;
            lock (InitLock)
            {
                if (_state != 0) return _field;
                var sw = Stopwatch.StartNew();
                try
                {
                    S8aSamplePlacement place = S8aSamplePlacement.Parse(File.ReadAllText(S8aSamplePlacement.DefaultPath()));
                    string dir = S8aChunk.DefaultFolder();
                    S8aChunk chunk = S8aChunk.Load(Path.Combine(dir, S8aChunk.BinName(place.Chunk)), Path.Combine(dir, S8aChunk.IndexName(place.Chunk)));
                    if (place.ChunkFnv != 0 && place.ChunkFnv != chunk.BinFnv) throw new InvalidDataException("the placement was made for another chunk (" + S8aStore.Hex16(place.ChunkFnv) + ")");
                    RingBase b = S8aRing.SharedBase(p);
                    var f = new S8aChunkField(chunk, place, b);
                    PlaceEntities(f);
                    _field = f;
                    _state = 1;
                    _initMs = sw.Elapsed.TotalMilliseconds;
                    _initLine = "DE.S8a sample ready: chunk " + place.Chunk + " " + chunk.Nx + "x" + chunk.Ny + "x" + chunk.Nz + " octrees (" +
                                (chunk.Nodes.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB), " + chunk.Entities.Count + " entities (" +
                                chunk.Excluded + " left out), site-v1 (" + place.SnX.ToString("0", CultureInfo.InvariantCulture) + ", " + place.SnZ.ToString("0", CultureInfo.InvariantCulture) +
                                "), turns " + place.Turns + ", re-base " + place.RebaseM.ToString("0", CultureInfo.InvariantCulture) + " m, zone y " + f.YLo.ToString("0", CultureInfo.InvariantCulture) +
                                ".." + f.YHi.ToString("0", CultureInfo.InvariantCulture) + " m, identity " + S8aStore.Hex16(f.Identity()) + ", init " + _initMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
                }
                catch (Exception e)
                {
                    _refusal = e.GetType().Name + ": " + e.Message;
                    _state = -1;
                    _initMs = sw.Elapsed.TotalMilliseconds;
                    _initLine = "DE.S8a sample REFUSED (" + _refusal + "): SE's heightmap serves the sample's point";
                }
                return _field;
            }
        }

        private static void PlaceEntities(S8aChunkField f)
        {
            int n = f.Chunk.Entities.Count;
            _ex = new double[n]; _ey = new double[n]; _ez = new double[n];
            _eq = new Quat[n];
            double half = 0.5 * Math.PI * (((f.Place.Turns % 4) + 4) % 4) * 0.5;      // Euler(0, 90 k, 0) = (0, sin 45k, 0, cos 45k)
            var yaw = new Quat(0.0, Math.Sin(half), 0.0, Math.Cos(half));
            for (int i = 0; i < n; i++)
            {
                S8aChunkEntity e = f.Chunk.Entities[i];
                f.NativeToLocal(e.X, e.Y, e.Z, out _ex[i], out _ey[i], out _ez[i]);
                _eq[i] = (yaw * new Quat(e.Qx, e.Qy, e.Qz, e.Qw)).Normalized();
            }
        }

        // ================================================================================================= the provider hooks (S8aRing)

 /// <summary>The cheap pre-check (no field needed): the box's centre within the anchor's reach.</summary>
        private static bool Near(double ax, double ay, double az, double bx, double by, double bz)
        {
            if (double.IsNaN(_ax)) return false;
            double cx = 0.5 * (ax + bx) - _ax, cy = 0.5 * (ay + by) - _ay, cz = 0.5 * (az + bz) - _az;
            double half = 0.5 * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            return Math.Sqrt(cx * cx + cy * cy + cz * cz) <= _preR + half;
        }

        /// <summary>
 /// From S8aRing's ReadRange prefix: serve the box when it meets the sample's zone at LOD &lt;= MaxLod (true: served, the prefix
 /// skips the original). Box corners planet-local (vanilla frame); <paramref name="ls"/> the LOD's voxel size. Never throws.
 /// </summary>
        public static bool TryServe(MyPlanetStorageProvider inst, ref MyVoxelDataRequest req, bool detect, double ax, double ay, double az, double bx, double by, double bz,
                                    double ls, int lodIndex, int sx, int sy, int sz, long t0)
        {
            if (!Setting || _state == -1 || lodIndex > S8aRing.MaxLod || !Near(ax, ay, az, bx, by, bz)) return false;
            try
            {
                S8aChunkField f = Ensure(inst);
                if (f == null || !f.Touches(ax, ay, az, bx, by, bz, ls)) return false;
                int n = sx * sy * sz;
                byte[] dst;
                int origin, stepY, stepZ;
                byte[] scratch = null;
                if (detect)
                {
                    scratch = new byte[n];
                    dst = scratch;
                    origin = 0; stepY = sx; stepZ = sx * sy;
                }
                else
                {
                    MyStorageData target = req.Target;
                    dst = target[MyStorageDataTypeEnum.Content];
                    Vector3I step = target.Step;
                    Vector3I o = req.Offset;
                    origin = target.ComputeLinear(ref o);
                    stepY = step.Y; stepZ = step.Z;
                }
                int all = f.ServeBox(ax, ay, az, sx, sy, sz, ls, dst, origin, stepY, stepZ);
                // the original's flags, as S8aRing
                MyVoxelRequestFlags rf = req.RequestFlags & ~(MyVoxelRequestFlags.EmptyData | MyVoxelRequestFlags.FullContent | MyVoxelRequestFlags.ContentChecked | MyVoxelRequestFlags.ContentCheckedDeep);
                if (detect)
                {
                    if ((all & 1) != 0) rf |= MyVoxelRequestFlags.EmptyData;
                    else if ((all & 2) != 0) rf |= MyVoxelRequestFlags.FullContent;
                    Interlocked.Increment(ref _detects);
                }
                req.Flags = rf;
                req.RequestFlags |= MyVoxelRequestFlags.ConsiderContent;
                if ((req.RequestedData & MyStorageDataTypeFlags.Material) != 0 && (req.Flags & MyVoxelRequestFlags.EmptyData) == 0)
                    inst.Material.ReadMaterialRange(ref req, detect);
                Interlocked.Increment(ref _reads);
                Interlocked.Add(ref _voxels, n);
                // S8a.NS.cost.scope: an independent check that the served box lies in the square (padded by the box's own size)
                double u, v, y, d, dx, dz;
                f.Tangent(0.5 * (ax + bx), 0.5 * (ay + by), 0.5 * (az + bz), out u, out v, out y, out d);
                f.Unturn(u, v, out dx, out dz);
                double halfBox = 0.5 * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) + 2.0 * ls;
                if (Math.Abs(dx) - halfBox > f.Place.HalfM || Math.Abs(dz) - halfBox > f.Place.HalfM) Interlocked.Increment(ref _servedOutside);
                Record(lodIndex, n, Stopwatch.GetTimestamp() - t0);
                return true;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _errors);
                _lastError = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static void Record(int lod, int voxels, long ticks)
        {
            int l = Math.Max(0, Math.Min(7, lod));
            int t = S8aRing.OnGameThread ? 0 : 1;
            int k = l * 2 + t;
            float ms = (float)(ticks * 1000.0 / Stopwatch.Frequency);
            lock (StatLock)
            {
                Res[k][(int)(ResCount[k] % Res[k].Length)] = ms;
                ResCount[k]++;
                ResVoxels[k] += voxels;
                ResTotal[k] += ms;
                if (ms > ResMax[k]) ResMax[k] = ms;
            }
        }

        /// <summary>Intersect postfix and the window's classification: a planet-local box the sample may fill (lod-0 corners).</summary>
        public static bool MayFill(double ax, double ay, double az, double bx, double by, double bz)
        {
            S8aChunkField f = _state == 1 ? _field : null;
            if (f == null || !Near(ax, ay, az, bx, by, bz)) return false;
            return f.Touches(ax, ay, az, bx, by, bz, 1.0);
        }

        public static void CountIntersect() { Interlocked.Increment(ref _intersectWidened); }
        public static void CountSurface() { Interlocked.Increment(ref _surfaces); }

        private static volatile bool _originSet;
        private static double _ox, _oy, _oz;

        /// <summary>
 /// The window converters' native type (S8aFieldSampler.NativeType): at an SE world point where the chunk's weight is at least
 /// one half, the chunk's own Subnautica block type of the native voxel there (or of the first solid voxel within 2 m under it), so
 /// Subnautica draws the real plateau's grass-bearing, blend and rock types; 0 elsewhere. Any thread (read-only field).
 /// </summary>
        public static byte NativeTypeAt(Vec3 w)
        {
            S8aChunkField f = _state == 1 ? _field : null;
            if (f == null || !_originSet) return 0;
            double px = w.X - _ox, py = w.Y - _oy, pz = w.Z - _oz;
            if (!Near(px, py, pz, px, py, pz)) return 0;
            double u, v, y, d, dx, dz;
            f.Tangent(px, py, pz, out u, out v, out y, out d);
            if (!(d > 0)) return 0;
            f.Unturn(u, v, out dx, out dz);
            double half = f.Place.HalfM;
            double e = Math.Min(half - Math.Abs(dx), half - Math.Abs(dz));
            if (e < 0.5 * f.Place.BlendM) return 0;
            int ix = (int)Math.Floor(half + dx), iz = (int)Math.Floor(half + dz);
            int iy = (int)Math.Floor((y - f.Place.RebaseM) - f.Chunk.YFloor);
            for (int k = 0; k <= 2; k++)
            {
                byte t = f.Chunk.TypeAt(ix, iy - k, iz);
                if (t != 0) return t;
            }
            return 0;
        }

        /// <summary>The converters' hook (null while the sample is off or not ready).</summary>
        public static Func<Vec3, byte> NativeTypeFunc()
        {
            if (!Setting || _state != 1 || !_originSet) return null;
            return NativeTypeAt;
        }

        // ================================================================================================= game thread

 /// <summary>Per tick (S8aHost.Tick): the init kick and its line once; the entity file near the sample.</summary>
        public static void Tick(ulong nowMs, Vec3 camSe, bool haveCam, ulong storeKey)
        {
            if (!Setting) return;
            if (_state == 0 && Interlocked.CompareExchange(ref _kicked, 1, 0) == 0)
            {
                MyPlanet planet = SiteBinding.Planet;
                MyPlanetStorageProvider prov = planet != null ? planet.Provider : null;
                if (prov != null) ThreadPool.QueueUserWorkItem(_ => { try { Ensure(prov); } catch (Exception) { } });
                else Interlocked.Exchange(ref _kicked, 0);
            }
            if (!_initLogged && _state != 0 && _initLine != null)
            {
                _initLogged = true;
                DeLog.Info(_initLine);
                S8aChunkField f0 = _field;
                Jsonl.WriteLine(S4Rows.Row("s8a.sample.init", "ok", _state == 1, "refusal", _refusal, "initMs", _initMs, "identity", S8aStore.Hex16(Identity()),
                                           "entities", f0 != null ? f0.Chunk.Entities.Count : 0, "excluded", f0 != null ? f0.Chunk.Excluded : 0,
                                           "chunkFnv", f0 != null ? S8aStore.Hex16(f0.Chunk.BinFnv) : null, "placementFnv", f0 != null ? S8aStore.Hex16(f0.Place.TextFnv) : null,
                                           "yLo", f0 != null ? f0.YLo : 0.0, "yHi", f0 != null ? f0.YHi : 0.0, "maxLod", S8aRing.MaxLod));
            }
            S8aChunkField f = Field;
            MyPlanet pl0 = SiteBinding.Planet;
            if (f != null && pl0 != null && pl0.Provider != null && !_originSet)
            {
                Vec3 o = LocalOrigin(pl0);
                _ox = o.X; _oy = o.Y; _oz = o.Z;
                _originSet = true;
            }
            if (f == null || !haveCam || storeKey == 0 || nowMs < _nextWriteMs) return;
            SiteMap map = SiteBinding.Map;
            MyPlanet pl = SiteBinding.Planet;
            if (map == null || pl == null || pl.Provider == null) return;
            uint cs = S8aChartManager.ChartSeq;
            bool moved = (camSe - _lastWriteCam).Length >= S8aSampleEntities.RewriteMoveM;
            if (!moved && cs == _lastChartSeq) return;
            _nextWriteMs = nowMs + (ulong)(1000.0 / S8aSampleEntities.WriteHz);
            Write(f, map, pl, camSe, cs, storeKey);
        }

        private static Vec3 LocalOrigin(MyPlanet pl)
        {
            Vector3D c = pl.PositionLeftBottomCorner;
            double tr = pl.Provider.StorageSize.X * 0.5;
            return new Vec3(c.X + tr, c.Y + tr, c.Z + tr);
        }

        private static void Write(S8aChunkField f, SiteMap map, MyPlanet pl, Vec3 camSe, uint chartSeq, ulong storeKey)
        {
            Vec3 o = LocalOrigin(pl);
            Vec3 cl = camSe - o;
            double cu, cv, cy, cd;
            f.Tangent(cl.X, cl.Y, cl.Z, out cu, out cv, out cy, out cd);
            bool near = cd > 0 && Math.Abs(cu) <= f.Place.HalfM + NearM && Math.Abs(cv) <= f.Place.HalfM + NearM;
            var recs = new List<S8aSampleRecord>();
            double nx = 0, ny = 0, nz = 0;
            if (near)
            {
                f.LocalToNative(cl.X, cl.Y, cl.Z, out nx, out ny, out nz);
                var E = new Vec3(f.Place.Ex, f.Place.Ey, f.Place.Ez);
                var N = new Vec3(f.Place.Nx, f.Place.Ny, f.Place.Nz);
                List<S8aChunkEntity> ents = f.Chunk.Entities;
                for (int i = 0; i < ents.Count; i++)
                {
                    double dx = _ex[i] - cl.X, dy = _ey[i] - cl.Y, dz = _ez[i] - cl.Z;
                    double r = S8aSampleEntities.RadiusOf(ents[i].Level) + S8aSampleEntities.FileMarginM;
                    if (dx * dx + dy * dy + dz * dz > r * r) continue;
                    var p = new Vec3(_ex[i] + o.X, _ey[i] + o.Y, _ez[i] + o.Z);
                    Vec3 sn = map.SeToSn(p);
                    // the tangent frame at the entity (radial up), mapped into the chart's SN axes: the rotation's frame
                    Vec3 up = (p - map.C).Normalized();
                    Vec3 e = (E - up * Vec3.Dot(E, up)).Normalized();
                    Vec3 nn = (N - up * Vec3.Dot(N, up) - e * Vec3.Dot(N, e)).Normalized();
                    Quat q = Quat.FromColumns(map.DirSeToSn(p, e), map.DirSeToSn(p, up), map.DirSeToSn(p, nn)) * _eq[i];
                    S8aChunkEntity ce = ents[i];
                    recs.Add(new S8aSampleRecord
                    {
                        Key = (ulong)ce.Index, Slot = ce.Slot, ClassId = ce.ClassId, Family = ce.Family, Level = ce.Level, X = sn.X, Y = sn.Y, Z = sn.Z,
                        Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, Sx = ce.Sx, Sy = ce.Sy, Sz = ce.Sz, SlotBiome = ce.SlotBiome, SlotAllowed = ce.SlotAllowed, SlotDensity = ce.SlotDensity,
                    });
                }
            }
            if (!near && _wroteEmpty && chartSeq == _lastChartSeq) { _lastWriteCam = camSe; return; }
            _wroteEmpty = !near;
            _lastWriteCam = camSe;
            _lastChartSeq = chartSeq;
            _writeSeq++;
            _lastCount = recs.Count;
            string path = S8aSampleEntities.PathOf(S8aStore.DefaultRoot(), storeKey);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, S8aSampleEntities.Format(_writeSeq, chartSeq, S8aChartManager.ChartKey, Identity(), nx, ny, nz, recs));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                _writes++;
                _lastFile = path;
            }
            catch (Exception e) { _writeErrors++; if (_writeErrors <= 3) DeLog.Info("DE.S8a sample entities not written: " + e.GetType().Name + ": " + e.Message); }
        }

        // ================================================================================================= commands (de.s8a sample)

        private static double Pct(float[] sorted, double q)
        {
            if (sorted.Length == 0) return 0.0;
            int i = (int)Math.Ceiling(q * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, i))];
        }

        private static List<object> CostTable()
        {
            var rows = new List<object>();
            lock (StatLock)
            {
                for (int k = 0; k < 16; k++)
                {
                    if (ResCount[k] == 0) continue;
                    int n = (int)Math.Min(ResCount[k], Res[k].Length);
                    var v = new float[n];
                    Array.Copy(Res[k], v, n);
                    Array.Sort(v);
                    rows.Add(new Dictionary<string, object>
                    {
                        { "cat", "sample" }, { "lod", k / 2 }, { "thread", k % 2 == 0 ? "game" : "worker" }, { "count", ResCount[k] }, { "p50", Pct(v, 0.5) },
                        { "p95", Pct(v, 0.95) }, { "max", ResMax[k] }, { "nsPerVoxel", ResVoxels[k] > 0 ? ResTotal[k] * 1e6 / ResVoxels[k] : 0.0 },
                    });
                }
            }
            return rows;
        }

        public static Dictionary<string, object> Status()
        {
            S8aChunkField f = Field;
            var st = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "setting", Setting }, { "patch", S8aRing.PatchInstalled }, { "maxLod", S8aRing.MaxLod }, { "state", _state == 1 ? "ready" : _state == -1 ? "refused" : "untried" },
                { "refusal", _refusal }, { "initMs", _initMs }, { "identity", S8aStore.Hex16(Identity()) },
                { "reads", Interlocked.Read(ref _reads) }, { "voxels", Interlocked.Read(ref _voxels) }, { "detects", Interlocked.Read(ref _detects) },
                { "errors", Interlocked.Read(ref _errors) }, { "lastError", _lastError }, { "intersectWidened", Interlocked.Read(ref _intersectWidened) },
                { "windowSurfaces", Interlocked.Read(ref _surfaces) }, { "servedOutside", Interlocked.Read(ref _servedOutside) },
                { "cost", CostTable() }, { "vanilla", S8aRing.CostTablePublic() },
                { "entityWrites", _writes }, { "entityWriteErrors", _writeErrors }, { "entitiesInFile", _lastCount }, { "entityFile", _lastFile },
                { "nativeTypedVoxels", System.Threading.Interlocked.Read(ref S8aFieldSampler.NativeTypedVoxels) },
            };
            if (f != null)
            {
                st["chunk"] = f.Place.Chunk;
                st["chunkFnv"] = S8aStore.Hex16(f.Chunk.BinFnv);
                st["indexFnv"] = S8aStore.Hex16(f.Chunk.IndexFnv);
                st["placementFnv"] = S8aStore.Hex16(f.Place.TextFnv);
                st["heightsFnv"] = S8aStore.Hex16(f.Base.HeightsFnv);
                st["sn"] = new object[] { f.Place.SnX, f.Place.SnZ };
                st["turns"] = f.Place.Turns;
                st["rebaseM"] = f.Place.RebaseM;
                st["halfM"] = f.Place.HalfM;
                st["blendM"] = f.Place.BlendM;
                st["zoneY"] = new object[] { f.YLo, f.YHi };
                st["entities"] = f.Chunk.Entities.Count;
                st["excluded"] = f.Chunk.Excluded;
                st["octrees"] = new object[] { f.Chunk.Nx, f.Chunk.Ny, f.Chunk.Nz };
                st["nodesMB"] = f.Chunk.Nodes.Length / 1048576.0;
            }
            return st;
        }

        /// <summary>de.s8a sample {...}: status; {serve: boxes} (NS.determinism); {columns, fromY, toY} (NS.terrain, NS.seam: the third witness).</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> a)
        {
            a = a ?? new Dictionary<string, object>();
            object v;
            if (a.TryGetValue("serve", out v) && v is IList<object>) return Serve((IList<object>)v);
            if (a.TryGetValue("columns", out v) && v is IList<object>)
                return Columns((IList<object>)v, MiniJson.GetNumber(a, "fromY") ?? 40.0, MiniJson.GetNumber(a, "toY") ?? -600.0);
            if (a.TryGetValue("where", out v) && v is IList<object>) return Where((IList<object>)v);
            Dictionary<string, object> st = Status();
            st["ok"] = true;
            st["line"] = "de.s8a sample: " + st["state"] + (st["refusal"] != null ? " (" + st["refusal"] + ")" : "") + ", reads " + st["reads"] + " served, errors " + st["errors"] +
                         ", entity writes " + st["entityWrites"] + " (" + st["entitiesInFile"] + " in the file)";
            return st;
        }

        private static Dictionary<string, object> Fail(string why) { return new Dictionary<string, object> { { "ok", false }, { "error", why } }; }

        /// <summary>
 /// Per SN column of the current chart: the sample field's own top (C#), the chunk's own top at the native column (no blend), the
 /// heightmap's seabed, the chunk's weight there and the native column (the analyzer re-reads the chunk's tops offline).
 /// </summary>
        public static Dictionary<string, object> Columns(IList<object> cols, double fromY, double toY)
        {
            S8aChunkField f = Field;
            SiteMap map = SiteBinding.Map;
            MyPlanet pl = SiteBinding.Planet;
            if (f == null || map == null || pl == null || pl.Provider == null) return Fail(f == null ? "the sample is not ready (" + (_refusal ?? "untried") + ")" : "no bound site");
            Vec3 o = LocalOrigin(pl);
            var res = new List<object>();
            foreach (object ob in cols)
            {
                var c = ob as IList<object>;
                if (c == null || c.Count < 2) continue;
                double x = Convert.ToDouble(c[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(c[1], CultureInfo.InvariantCulture);
                Vec3 se = map.SnToSe(new Vec3(x, 0.0, z));
                Vec3 l = se - o;
                double top = f.TopAlong(l.X, l.Y, l.Z, fromY, toY);
                double nx, ny, nz, h, w;
                f.LocalToNative(l.X, l.Y, l.Z, out nx, out ny, out nz);
                double n = l.Length;
                f.Sample(l.X / n * (RingRules.Rs + (double.IsNaN(top) ? -100.0 : top)), l.Y / n * (RingRules.Rs + (double.IsNaN(top) ? -100.0 : top)),
                         l.Z / n * (RingRules.Rs + (double.IsNaN(top) ? -100.0 : top)), out h, out w);
                double own = f.NativeTop(nx, nz);
                // SE's surface at that SN column is reported in SN y of the chart: the field's altitude top is SE's radial altitude; the chart's
 // SN y at that point (r - Rs + O.Y) is the same altitude plus the chart's O.Y (0 for every chart: chart.anchor keeps O'.y = 0)
                res.Add(new object[] { x, z, double.IsNaN(top) ? null : (object)top, double.IsNaN(own) ? null : (object)own, double.IsNaN(h) ? null : (object)h, w, nx, nz });
            }
            return new Dictionary<string, object>
            {
                { "ok", true }, { "results", res }, { "rebaseM", f.Place.RebaseM }, { "halfM", f.Place.HalfM }, { "blendM", f.Place.BlendM },
                { "chunkFnv", S8aStore.Hex16(f.Chunk.BinFnv) }, { "line", "de.s8a sample columns: " + res.Count },
            };
        }

        /// <summary>
 /// {where: [[u, v],...]}: tangent points of the sample (metres from its centre along its east and north) in SN coordinates of the
 /// current chart, with the field's top there (the launcher aims its columns, seams and cameras with them); the camera's own
 /// tangent and native coordinates.
 /// </summary>
        public static Dictionary<string, object> Where(IList<object> pts)
        {
            S8aChunkField f = Field;
            SiteMap map = SiteBinding.Map;
            MyPlanet pl = SiteBinding.Planet;
            if (f == null || map == null || pl == null || pl.Provider == null) return Fail(f == null ? "the sample is not ready (" + (_refusal ?? "untried") + ")" : "no bound site");
            Vec3 o = LocalOrigin(pl);
            var res = new List<object>();
            foreach (object ob in pts)
            {
                var c = ob as IList<object>;
                if (c == null || c.Count < 2) continue;
                double u = Convert.ToDouble(c[0], CultureInfo.InvariantCulture), v = Convert.ToDouble(c[1], CultureInfo.InvariantCulture);
                double px, py, pz;
                f.LocalOfTangent(u, v, 0.0, out px, out py, out pz);
                Vec3 sn = map.SeToSn(new Vec3(px + o.X, py + o.Y, pz + o.Z));
                double top = f.TopAlong(px, py, pz, 40.0, -600.0);
                res.Add(new object[] { u, v, sn.X, sn.Z, double.IsNaN(top) ? null : (object)(top + (sn.Y - 0.0)) });
            }
            var r = new Dictionary<string, object> { { "ok", true }, { "results", res }, { "halfM", f.Place.HalfM }, { "blendM", f.Place.BlendM }, { "rebaseM", f.Place.RebaseM },
                                                     { "line", "de.s8a sample where: " + res.Count } };
            Vec3 cam;
            if (S8aHost.Camera(out cam))
            {
                Vec3 l = cam - o;
                double cu, cv, cy, cd, nx, ny, nz;
                f.Tangent(l.X, l.Y, l.Z, out cu, out cv, out cy, out cd);
                f.LocalToNative(l.X, l.Y, l.Z, out nx, out ny, out nz);
                Vec3 csn = map.SeToSn(cam);
                r["camera"] = new object[] { cu, cv, cy, nx, ny, nz, csn.X, csn.Y, csn.Z };
            }
            return r;
        }

        /// <summary>{serve: [[ax, ay, az, sx, sy, sz, lod],...]} (NS.determinism): per box the FNV of S8aChunkField.ServeBox and of the planet's own generator read through the prefix.</summary>
        public static Dictionary<string, object> Serve(IList<object> boxes)
        {
            S8aChunkField f = Field;
            MyPlanet planet = SiteBinding.Planet;
            if (f == null || planet == null || planet.Provider == null) return Fail(f == null ? "the sample is not ready" : "no planet");
            var res = new List<object>();
            double tr = planet.Provider.StorageSize.X * 0.5;
            foreach (object ob in boxes)
            {
                var b = ob as IList<object>;
                if (b == null || b.Count < 7) continue;
                double ax = Convert.ToDouble(b[0], CultureInfo.InvariantCulture), ay = Convert.ToDouble(b[1], CultureInfo.InvariantCulture), az = Convert.ToDouble(b[2], CultureInfo.InvariantCulture);
                int sx = Convert.ToInt32(b[3], CultureInfo.InvariantCulture), sy = Convert.ToInt32(b[4], CultureInfo.InvariantCulture), sz = Convert.ToInt32(b[5], CultureInfo.InvariantCulture);
                int lod = Convert.ToInt32(b[6], CultureInfo.InvariantCulture);
                int lodIndex = lod == 1 ? 0 : lod == 2 ? 1 : lod == 4 ? 2 : lod == 8 ? 3 : -1;
                var row = new Dictionary<string, object> { { "box", b } };
                if (lodIndex < 0) { row["error"] = "bad lod"; res.Add(row); continue; }
                var c = new byte[sx * sy * sz];
                f.ServeBox(ax, ay, az, sx, sy, sz, lod, c, 0, sx, sx * sy);
                row["fieldFnv"] = RingHash.Fnv(c).ToString("x16", CultureInfo.InvariantCulture);
                double gx = (ax + tr) / lod, gy = (ay + tr) / lod, gz = (az + tr) / lod;
                if (gx != Math.Floor(gx) || gy != Math.Floor(gy) || gz != Math.Floor(gz)) row["providerError"] = "the box does not sit on the LOD grid (StorageSize " + planet.Provider.StorageSize.X + ")";
                else
                {
                    try
                    {
                        var data = new MyStorageData(MyStorageDataTypeFlags.Content);
                        var mn = new Vector3I((int)gx, (int)gy, (int)gz);
                        var mx = new Vector3I((int)gx + sx - 1, (int)gy + sy - 1, (int)gz + sz - 1);
                        data.Resize(Vector3I.Zero, mx - mn);
                        Vector3I off = Vector3I.Zero;
                        planet.Provider.ReadRange(data, MyStorageDataTypeFlags.Content, ref off, lodIndex, ref mn, ref mx);
                        byte[] arr = data[MyStorageDataTypeEnum.Content];
                        var copy = new byte[sx * sy * sz];
                        Buffer.BlockCopy(arr, 0, copy, 0, copy.Length);
                        row["providerFnv"] = RingHash.Fnv(copy).ToString("x16", CultureInfo.InvariantCulture);
                    }
                    catch (Exception e) { row["providerError"] = e.GetType().Name + ": " + e.Message; }
                }
                res.Add(row);
            }
            return new Dictionary<string, object>
            {
                { "ok", true }, { "results", res }, { "chunkFnv", S8aStore.Hex16(f.Chunk.BinFnv) }, { "placementFnv", S8aStore.Hex16(f.Place.TextFnv) },
                { "heightsFnv", S8aStore.Hex16(f.Base.HeightsFnv) }, { "line", "de.s8a sample serve: " + res.Count + " boxes" },
            };
        }
    }
}
