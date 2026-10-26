using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Engine.Voxels;
using Sandbox.Engine.Voxels.Planet;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S8a Stage F2 (CONTRACT §3.3 "Stage F2: the ring served by the planet's generator"): the ring's 3D field
 /// (DeepEngineers.Terrain.RingField, the C# twin of tools/planet/s8a_ring3d.py) served by the planet's own generator. A Harmony
 /// prefix on <c>MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool)</c>
 /// writes the field's content for reads at LOD
 /// &lt;= <see cref="MaxLod"/> that meet the ring zone (SN radius <see cref="ZoneInnerM"/>-<see cref="ZoneOuterM"/> and the field's
 /// reach above and below the seabed) and lets SE's own generator answer everything else: the heightmap stays the base and the
 /// fallback (coarser LODs, outside the zone, before the field is ready, after any failure: the original runs on the same
 /// request). The field's base is that very heightmap (Catmull-Rom like MyPlanetShapeProvider.SampleHeightBicubic),
 /// so the two meet without a seam. Materials stay SE's
 /// Drilling is SE's planet edit over this content; the window's edit path serves it to Subnautica.
 /// A postfix on <c>Intersect(BoundingBoxI, int)</c> answers Intersects for boxes the field may fill.
 /// Why not S4 voxel-map tiles in a moving window (the Stage F brief): a tile overlaps the planet's own rock and SE has no
 /// subtraction between voxel maps (each MyVoxelBase meshes and collides on its own storage), so caves and overhang undersides
 /// under the seabed would stay filled by the planet; through the planet's generator nothing overlaps, there are no tile seams,
 /// and SE's clipmap LODs are the moving window (the field is computed only where SE reads at LOD &lt;= 3, near the camera).
 /// Determinism (saved drill deltas sit on the generator's output): the served bytes are <see cref="RingField.ServeBox"/>, IEEE
 /// double arithmetic only (the site mapping's arctan a polynomial), vectors twin = C# (tests/s8a/vectors/ring3d/ "serve").
 /// Off unless DEEPENGINEERS_S8A=on and DEEPENGINEERS_S8A_RING=on.
 /// </summary>
    internal static class S8aRing
    {
        public const string EnvRing = "DEEPENGINEERS_S8A_RING", EnvLod = "DEEPENGINEERS_S8A_RING_LOD";
        /// <summary>The SN radii between which the field may differ from the heightmap (3D rules fade in over 3,600-3,800 and out over 12,000-12,200 m; stamps reach 65 m past their cells).</summary>
        public const double ZoneInnerM = 3530.0, ZoneOuterM = 12270.0;
        private const double ColumnMarginM = 60.0;

        public static bool Setting { get; private set; }
        public static int MaxLod { get; private set; }
        public static bool PatchInstalled { get; private set; }
        private static bool _envRead;

        private static readonly object InitLock = new object();
        private static volatile RingField _field;
        private static volatile int _state; // 0 untried, 1 ready, -1 refused
        private static string _refusal, _initLine, _source;
        private static bool _initLogged;
        private static double _initMs;
        private static long _ramBefore, _ramAfter;
        private static readonly SiteMap Map0 = SiteMap.ForSiteV1(Vec3.Zero, SiteV1.RsExpected);
        private static volatile int _gameThreadId = -1;
        private static int _kicked;
        private static int _storageSizeX;

        // ------------------------------------------------------------------------------------------ the counters (S8a.NF.cost,.scope)
        private enum Cat { Served = 0, VanillaZone = 1, VanillaOut = 2 }
        private static readonly string[] CatNames = { "served", "vanillaZone", "vanillaOutside" };
        private sealed class Reservoir
        {
            public readonly float[] V = new float[1024];
            public long Count, Voxels;
            public double Max, TotalMs;
        }
        private static readonly object StatLock = new object();
        private static readonly Reservoir[,,] _res = NewReservoirs();
        private static long _reads, _voxels, _passes, _detects, _errors, _intersectWidened, _servedOutside, _servedAboveLod, _failInjected;
        private static string _lastError;
        private static ulong _identity;
        private static bool _identityDone;
        /// <summary>S8a.M15: the field fails on reads whose box holds this SN column (SE's heightmap then serves them).</summary>
        private static double _failX = double.NaN, _failZ = double.NaN;

        [ThreadStatic] private static List<RingPlacement> _near;
        [ThreadStatic] private static byte[] _scratch;
        [ThreadStatic] private static long _tsStart;
        [ThreadStatic] private static int _tsCat, _tsLod, _tsVoxels;
        [ThreadStatic] private static bool _tsPending;

        public static RingField Field { get { return _state == 1 ? _field : null; } }

        private static Reservoir[,,] NewReservoirs()
        {
            var r = new Reservoir[3, 8, 2];
            for (int c = 0; c < 3; c++)
                for (int l = 0; l < 8; l++)
                    for (int t = 0; t < 2; t++) r[c, l, t] = new Reservoir();
            return r;
        }

        public static void ReadEnv()
        {
            if (_envRead) return;
            _envRead = true;
            S8aOverlay.ReadEnv();
            string v = Environment.GetEnvironmentVariable(EnvRing);
            Setting = S8aOverlay.Enabled && string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
            int lod = 3;
            string l = Environment.GetEnvironmentVariable(EnvLod);
            int parsed;
            if (!string.IsNullOrEmpty(l) && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) lod = Math.Max(0, Math.Min(3, parsed));
            MaxLod = lod;
            S8aSample.ReadEnv();
        }

        /// <summary>Plugin ApplyHarmony (S8aHost.ApplyPatches): the provider prefix and postfix and the Intersect postfix, only with the ring on.</summary>
        public static void InstallPatches(Harmony h)
        {
            ReadEnv();
            if (!Setting && !S8aSample.Setting)
            {
                DeLog.Info("DE.S8a ring off (" + EnvRing + " not 'on' or S8a off), sample off (" + S8aSample.EnvSample + "): the planet's generator is SE's own");
                return;
            }
            MethodInfo read = AccessTools.Method(typeof(MyPlanetStorageProvider), "ReadRange", new[] { typeof(MyVoxelDataRequest).MakeByRefType(), typeof(bool) });
            MethodInfo inter = AccessTools.Method(typeof(MyPlanetStorageProvider), "Intersect", new[] { typeof(BoundingBoxI), typeof(int) });
            if (read == null || inter == null || inter.ReturnType != typeof(ContainmentType))
            {
                DeLog.Info("DE.S8a MISSING MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool) / Intersect(BoundingBoxI, int): the ring field is off (SE's heightmap serves the ring)");
                return;
            }
            try
            {
                h.Patch(read, prefix: new HarmonyMethod(typeof(S8aRing).GetMethod(nameof(ReadRangePrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(S8aRing).GetMethod(nameof(ReadRangePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                h.Patch(inter, postfix: new HarmonyMethod(typeof(S8aRing).GetMethod(nameof(IntersectPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                PatchInstalled = true;
                DeLog.Info("DE.S8a PATCH ring.field ok (ring " + (Setting ? "on" : "off") + ", sample " + (S8aSample.Setting ? "on" : "off") + ") MyPlanetStorageProvider.ReadRange prefix + postfix (timing) + Intersect postfix (LOD <= " + MaxLod + ", SN radius " +
                           ZoneInnerM.ToString("0", CultureInfo.InvariantCulture) + "-" + ZoneOuterM.ToString("0", CultureInfo.InvariantCulture) + " m, generator " + SiteV1.GeneratorSubtypeId + ")");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.S8a MISSING ring.field patch failed " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool Ours(MyPlanetStorageProvider p)
        {
            try { return p != null && !p.Closed && p.Generator != null && p.Generator.Id.SubtypeName == SiteV1.GeneratorSubtypeId && p.Shape != null && p.Shape.Heightmap != null; }
            catch (Exception) { return false; }
        }

        // ================================================================================================= the identity (store keys)

 /// <summary>
 /// The ring field's identity for the window's keys (conventions "store.key"): fnv1a64 of 'ring|' + the rules text's FNV +
 /// '|' + the library's FNV + '|' + the index's FNV (x16 each; 'missing' for a library not built).
 /// </summary>
        public static ulong Identity()
        {
            if (_identityDone) return _identity;
            string bin = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.BinName), idx = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.IndexName);
            string lib = "missing", ix = "missing";
            try { if (File.Exists(bin)) lib = RingHash.Fnv(File.ReadAllBytes(bin)).ToString("x16", CultureInfo.InvariantCulture); } catch (Exception) { }
            try { if (File.Exists(idx)) ix = RingHash.Fnv(File.ReadAllBytes(idx)).ToString("x16", CultureInfo.InvariantCulture); } catch (Exception) { }
            _identity = RingHash.FnvUtf8("ring|" + RingRules.TextHash(RingRules.Seed).ToString("x16", CultureInfo.InvariantCulture) + "|" + lib + "|" + ix);
            _identityDone = true;
            return _identity;
        }

        /// <summary>The window's sculpt hash with the ring on (conventions "store.key"): fnv1a64(utf8 sculptHash x16 + '|ring|' + identity x16).</summary>
        public static ulong FoldInto(ulong sculptHash)
        {
            return RingHash.FnvUtf8(sculptHash.ToString("x16", CultureInfo.InvariantCulture) + "|ring|" + Identity().ToString("x16", CultureInfo.InvariantCulture));
        }

        // ================================================================================================= the field (lazy, once)

        private static RingField EnsureField(MyPlanetStorageProvider p)
        {
            if (_state == 1) return _field;
            if (_state == -1) return null;
            lock (InitLock)
            {
                if (_state != 0) return _field;
                var sw = Stopwatch.StartNew();
                _ramBefore = PrivateBytes();
                try
                {
                    RingBase b = SharedBase(p);
                    string bin = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.BinName), idx = Path.Combine(RingBrushLibrary.DefaultFolder(), RingBrushLibrary.IndexName);
                    if (!File.Exists(bin) || !File.Exists(idx)) throw new FileNotFoundException("no brush library in " + RingBrushLibrary.DefaultFolder() + " (python tools/planet/s8a_brushes.py build)");
                    RingBrushLibrary lib = RingBrushLibrary.Load(bin, idx);
                    var f = new RingField(b, lib, RingRules.Seed);
                    f.Place();
                    _storageSizeX = p.StorageSize.X;
                    _field = f;
                    _state = 1;
                    _initMs = sw.Elapsed.TotalMilliseconds;
                    _ramAfter = PrivateBytes();
                    int used;
                    double nearest = f.NearestSameBrushM(out used);
                    _initLine = "DE.S8a ring field ready: " + f.Placements.Count + " placements of " + used + " brushes (library " + lib.Brushes.Count + ", " +
                                (lib.DecodedBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB decoded), nearest same brush " +
                                nearest.ToString("0", CultureInfo.InvariantCulture) + " m, reach +" + f.ReachAboveM.ToString("0", CultureInfo.InvariantCulture) + " / -" +
                                f.ReachBelowM.ToString("0", CultureInfo.InvariantCulture) + " m, base " + _source + ", place " + f.PlaceMs.ToString("0", CultureInfo.InvariantCulture) +
                                " ms, init " + _initMs.ToString("0", CultureInfo.InvariantCulture) + " ms, privateMB " + Mb(_ramBefore) + " -> " + Mb(_ramAfter) +
                                ", rules " + RingRules.TextHash(RingRules.Seed).ToString("x16", CultureInfo.InvariantCulture) + ", placements hash " + f.PlacementsHash().ToString("x16", CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    _refusal = e.GetType().Name + ": " + e.Message;
                    _state = -1;
                    _initMs = sw.Elapsed.TotalMilliseconds;
                    _initLine = "DE.S8a ring field REFUSED (" + _refusal + "): SE's heightmap serves the ring";
                }
                return _field;
            }
        }

        private static readonly object BaseLock = new object();
        private static RingBase _sharedBase;

        /// <summary>The base from SE's own face-1 maps, made once for the ring field and the sample (S8aSample) alike.</summary>
        internal static RingBase SharedBase(MyPlanetStorageProvider p)
        {
            lock (BaseLock)
            {
                if (_sharedBase == null) _sharedBase = BaseOf(p);
                return _sharedBase;
            }
        }

        /// <summary>Whether the calling thread is SE's game thread (the cost rows' split).</summary>
        internal static bool OnGameThread { get { return Thread.CurrentThread.ManagedThreadId == _gameThreadId; } }

        /// <summary>The cost table (SE's own generator on the calls the prefix let through: the sample's reference, same run).</summary>
        internal static List<object> CostTablePublic() { return CostTable(); }

        /// <summary>The base from SE's own face-1 maps (what SE renders); the world mod's back_mat.png when SE has no biome map.</summary>
        private static RingBase BaseOf(MyPlanetStorageProvider p)
        {
            MyHeightmapFace face = p.Shape.Heightmap.Faces[1];
            int res = face.Resolution;
            if (res != RingRules.Res) throw new InvalidDataException("face 1 is " + res + " px, the field wants " + RingRules.Res);
            var h = new ushort[res * res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                {
                    ushort v;
                    face.GetValue(x, y, out v);
                    h[y * res + x] = v;
                }
            byte[] g = null;
            string pngDir = S8aHost.FindPlanetDataDirPublic();
            try
            {
                MyCubemap bm = p.Material != null && p.Material.Maps != null && p.Material.Maps.Length > 1 ? p.Material.Maps[1] : null;
                if (bm != null && bm.Faces != null && bm.Faces.Length > 1 && bm.Faces[1] != null && bm.Faces[1].Resolution == res)
                {
                    g = new byte[res * res];
                    MyCubemapData<byte> bf = bm.Faces[1];
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                        {
                            byte v;
                            bf.GetValue(x, y, out v);
                            g[y * res + x] = v;
                        }
                }
            }
            catch (Exception) { g = null; }
            string gSource = "SE's biome map";
            if (g == null)
            {
                if (pngDir == null) throw new InvalidDataException("SE has no face-1 biome map and the world mod's planet data folder is not found");
                g = RingPng.Channel(RingPng.Read(Path.Combine(pngDir, "back_mat.png")), 1);
                gSource = "back_mat.png G";
            }
            var b = new RingBase(h, g, res);
            string check = "";
            if (pngDir != null)
            {
                try
                {
                    ushort[] ph = RingPng.Gray16(RingPng.Read(Path.Combine(pngDir, "back.png")));
                    var hb = new byte[ph.Length * 2];
                    Buffer.BlockCopy(ph, 0, hb, 0, hb.Length);
                    ulong hf = RingHash.Fnv(hb), gf = RingHash.Fnv(RingPng.Channel(RingPng.Read(Path.Combine(pngDir, "back_mat.png")), 1));
                    check = hf == b.HeightsFnv && gf == b.BiomesFnv ? " (= the world mod's back.png / back_mat.png)" :
                            " (DIFFERS from the world mod's PNGs: heights " + (hf == b.HeightsFnv ? "same" : "differ") + ", biomes " + (gf == b.BiomesFnv ? "same" : "differ") + ")";
                }
                catch (Exception e) { check = " (PNG check failed: " + e.GetType().Name + ")"; }
            }
            _source = "SE's face-1 heightmap + " + gSource + check;
            return b;
        }

        // ================================================================================================= the provider hooks

 /// <summary>
 /// SE's planet generator, content reads: the ring field where it may differ from the heightmap, else the original (timed by
 /// <see cref="ReadRangePostfix"/>). Positional names: __0 = the request (ref), __1 = detectOnly. Never throws (a failure, S8a.M15
 /// included, answers with the original: SE's heightmap).
 /// </summary>
        private static bool ReadRangePrefix(MyPlanetStorageProvider __instance, ref MyVoxelDataRequest __0, bool __1)
        {
            _tsPending = false;
            if ((__0.RequestedData & MyStorageDataTypeFlags.Content) == 0 || !Ours(__instance)) return true;
            long t0 = Stopwatch.GetTimestamp();
            Vector3I min = __0.MinInLod, max = __0.MaxInLod;
            int lodIndex = __0.Lod;
            int sx = max.X - min.X + 1, sy = max.Y - min.Y + 1, sz = max.Z - min.Z + 1;
            int n = sx * sy * sz;
            // a test run (nf 20261023-214851: "untried: no planet read yet"): the field was made on the first read at LOD <= 3, but near the
 // crater SE's planet is 3 km down (every near read Disjoint, never read) and nf asked before the camera reached the ring; any
 // read of the planet (its far LODs during the load) makes it now, and S8aRing.Tick starts it on a pool thread at world up
            double ls = 1 << Math.Min(lodIndex, 30);
            double tr = __instance.StorageSize.X * 0.5;
            double ax = min.X * ls - tr, ay = min.Y * ls - tr, az = min.Z * ls - tr;
            double bx = max.X * ls - tr, by = max.Y * ls - tr, bz = max.Z * ls - tr;
            // planet v2 route B (S8aSample, DEEPENGINEERS_S8A_SAMPLE=on): the sample's chunk where its zone meets the box
            if (S8aSample.Setting && S8aSample.TryServe(__instance, ref __0, __1, ax, ay, az, bx, by, bz, ls, lodIndex, sx, sy, sz, t0)) return false;
            RingField field = Setting ? EnsureField(__instance) : null;
            double x0 = 0, x1 = 0, y0 = 0, y1 = 0, z0 = 0, z1 = 0;
            bool zone = false, touches = false;
            if (field != null) touches = Touches(field, ax, ay, az, bx, by, bz, ls, out x0, out x1, out y0, out y1, out z0, out z1, out zone);
            if (!touches || lodIndex > MaxLod)
            {
                Interlocked.Increment(ref _passes);
                _tsPending = true;
                _tsStart = t0;
                _tsCat = (int)(zone ? Cat.VanillaZone : Cat.VanillaOut);
                _tsLod = lodIndex;
                _tsVoxels = n;
                return true;
            }
            try
            {
                if (S8aMutants.M15 && !double.IsNaN(_failX) && _failX >= x0 && _failX <= x1 && _failZ >= z0 && _failZ <= z1)
                {
                    Interlocked.Increment(ref _failInjected);
                    throw new InvalidOperationException("S8a.M15: the ring field fails on the column (" + _failX.ToString("0.0", CultureInfo.InvariantCulture) + ", " +
                                                        _failZ.ToString("0.0", CultureInfo.InvariantCulture) + ")");
                }
                List<RingPlacement> near = _near ?? (_near = new List<RingPlacement>(16));
                field.Near(x0 - 2.0, x1 + 2.0, z0 - 2.0, z1 + 2.0, near);
                bool detect = __1;
                byte[] dst;
                int origin, stepY, stepZ;
                if (detect)
                {
                    if (_scratch == null || _scratch.Length < n) _scratch = new byte[Math.Max(n, 4096)];
                    dst = _scratch;
                    origin = 0; stepY = sx; stepZ = sx * sy;
                }
                else
                {
                    MyStorageData target = __0.Target;
                    dst = target[MyStorageDataTypeEnum.Content];
                    Vector3I step = target.Step;
                    Vector3I o = __0.Offset;
                    origin = target.ComputeLinear(ref o);
                    stepY = step.Y; stepZ = step.Z;
                }
                int all = field.ServeBox(ax, ay, az, sx, sy, sz, ls, dst, origin, stepY, stepZ, near);
                // the original's flags
                MyVoxelRequestFlags rf = __0.RequestFlags & ~(MyVoxelRequestFlags.EmptyData | MyVoxelRequestFlags.FullContent | MyVoxelRequestFlags.ContentChecked | MyVoxelRequestFlags.ContentCheckedDeep);
                if (detect)
                {
                    if ((all & 1) != 0) rf |= MyVoxelRequestFlags.EmptyData;
                    else if ((all & 2) != 0) rf |= MyVoxelRequestFlags.FullContent;
                    Interlocked.Increment(ref _detects);
                }
                __0.Flags = rf;
                __0.RequestFlags |= MyVoxelRequestFlags.ConsiderContent;
                if ((__0.RequestedData & MyStorageDataTypeFlags.Material) != 0 && (__0.Flags & MyVoxelRequestFlags.EmptyData) == 0)
                    __instance.Material.ReadMaterialRange(ref __0, detect);
                Interlocked.Increment(ref _reads);
                Interlocked.Add(ref _voxels, n);
                // S8a.NF.scope: an independent check (SiteMap's own arctan) that a served box lies in the zone at LOD <= MaxLod
                if (lodIndex > MaxLod) Interlocked.Increment(ref _servedAboveLod);
                Vec3 c = Map0.SeToSn(new Vec3(0.5 * (ax + bx), 0.5 * (ay + by), 0.5 * (az + bz)));
                double rc = Math.Sqrt(c.X * c.X + c.Z * c.Z), half = 0.5 * Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az)) + 2.0 * ls;
                if (rc + half < ZoneInnerM || rc - half > ZoneOuterM) Interlocked.Increment(ref _servedOutside);
                Record(Cat.Served, lodIndex, n, Stopwatch.GetTimestamp() - t0);
                return false;
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _errors);
                _lastError = e.GetType().Name + ": " + e.Message;
                _tsPending = true;
                _tsStart = t0;
                _tsCat = (int)Cat.VanillaZone;
                _tsLod = lodIndex;
                _tsVoxels = n;
                return true;
            }
        }

        /// <summary>SE's own generator's time on the calls the prefix let through (the cost row's reference, same run).</summary>
        private static void ReadRangePostfix()
        {
            if (!_tsPending) return;
            _tsPending = false;
            Record((Cat)_tsCat, _tsLod, _tsVoxels, Stopwatch.GetTimestamp() - _tsStart);
        }

        private static void Record(Cat cat, int lod, int voxels, long ticks)
        {
            int l = Math.Max(0, Math.Min(7, lod));
            int t = Thread.CurrentThread.ManagedThreadId == _gameThreadId ? 0 : 1;
            float ms = (float)(ticks * 1000.0 / Stopwatch.Frequency);
            lock (StatLock)
            {
                Reservoir r = _res[(int)cat, l, t];
                r.V[(int)(r.Count % r.V.Length)] = ms;
                r.Count++;
                r.Voxels += voxels;
                r.TotalMs += ms;
                if (ms > r.Max) r.Max = ms;
            }
        }

        /// <summary>Boxes the field may fill answer Intersects (never Disjoint / Contains), so SE reads them. __0 = the box (LOD-0 storage voxels), __1 = lod.</summary>
        private static void IntersectPostfix(MyPlanetStorageProvider __instance, BoundingBoxI __0, int __1, ref ContainmentType __result)
        {
            try
            {
                if (__result == ContainmentType.Intersects || !Ours(__instance)) return;
                double tr = __instance.StorageSize.X * 0.5;
                if (S8aSample.Setting && S8aSample.MayFill(__0.Min.X - tr, __0.Min.Y - tr, __0.Min.Z - tr, __0.Max.X - tr, __0.Max.Y - tr, __0.Max.Z - tr))
                {
                    __result = ContainmentType.Intersects;
                    S8aSample.CountIntersect();
                    return;
                }
                RingField field = _state == 1 ? _field : null;
                if (field == null) return;
                double x0, x1, y0, y1, z0, z1;
                bool zone;
                if (Touches(field, __0.Min.X - tr, __0.Min.Y - tr, __0.Min.Z - tr, __0.Max.X - tr, __0.Max.Y - tr, __0.Max.Z - tr, 1.0, out x0, out x1, out y0, out y1, out z0, out z1, out zone)
                    && FeaturesIn(field, x0, x1, y0, y1, z0, z1))
                {
                    __result = ContainmentType.Intersects;
                    Interlocked.Increment(ref _intersectWidened);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
 /// Whether a planet-local box (vanilla frame) may hold content that differs from the heightmap's: its Subnautica bounds (the 8
 /// corners by <see cref="RingField.SnBounds"/>, padded by a voxel) meet the ring zone (<paramref name="zone"/>) and its height
 /// range meets the seabed's reach at 25 columns. Deterministic (no transcendental call).
 /// </summary>
        private static bool Touches(RingField field, double ax, double ay, double az, double bx, double by, double bz, double pad,
                                    out double x0, out double x1, out double y0, out double y1, out double z0, out double z1, out bool zone)
        {
            zone = false;
            if (!RingField.SnBounds(ax, ay, az, bx, by, bz, out x0, out x1, out y0, out y1, out z0, out z1)) return false;
            x0 -= pad; x1 += pad; y0 -= pad; y1 += pad; z0 -= pad; z1 += pad;
            double cx = Math.Max(x0, Math.Min(0.0, x1)), cz = Math.Max(z0, Math.Min(0.0, z1));
            double rmin = Math.Sqrt(cx * cx + cz * cz);
            double fx = Math.Max(Math.Abs(x0), Math.Abs(x1)), fz = Math.Max(Math.Abs(z0), Math.Abs(z1));
            double rmax = Math.Sqrt(fx * fx + fz * fz);
            if (rmax < ZoneInnerM || rmin > ZoneOuterM) return false;
            zone = true;
            double hmin, hmax;
            bool caves, arches;
            Columns(field, x0, x1, z0, z1, out hmin, out hmax, out caves, out arches);
            if (double.IsNaN(hmin)) return false;
            return y0 <= hmax + field.ReachAboveM + ColumnMarginM && y1 >= hmin - field.ReachBelowM - ColumnMarginM;
        }

        private static void Columns(RingField field, double x0, double x1, double z0, double z1, out double hmin, out double hmax, out bool caves, out bool arches)
        {
            hmin = double.PositiveInfinity;
            hmax = double.NegativeInfinity;
            caves = arches = false;
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                {
                    double h;
                    int key;
                    field.Base.Column(x0 + (x1 - x0) * a / 4.0, z0 + (z1 - z0) * b / 4.0, out h, out key);
                    if (double.IsNaN(h)) continue;
                    hmin = Math.Min(hmin, h);
                    hmax = Math.Max(hmax, h);
                    RingRule r = key >= 0 ? RingRules.RuleOfKey[key] : null;
                    if (r != null) { caves |= r.Caves; arches |= r.Arches; }
                }
            if (double.IsInfinity(hmin)) hmin = hmax = double.NaN;
        }

        /// <summary>Whether an SN box may hold a stamp, a cave or an arch (the window's classification and the Intersect postfix).</summary>
        private static bool FeaturesIn(RingField field, double x0, double x1, double y0, double y1, double z0, double z1)
        {
            double hmin, hmax;
            bool caves, arches;
            Columns(field, x0, x1, z0, z1, out hmin, out hmax, out caves, out arches);
            if (double.IsNaN(hmin)) return false;
            hmin -= ColumnMarginM;
            hmax += ColumnMarginM;
            if (caves && y1 >= hmin - RingRules.CaveB3 && y0 <= hmax - RingRules.CaveB0 + 1.0) return true;
            if (arches && y1 >= hmin + RingRules.ArchB0 - 1.0 && y0 <= hmax + RingRules.ArchB3) return true;
            var near = new List<RingPlacement>();
            field.Near(x0, x1, z0, z1, near);
            foreach (RingPlacement p in near)
            {
                double below = p.Ground * p.Scale + 1.0, above = (p.Brush.Size - 1 - p.Ground) * p.Scale + 1.0;
                if (y1 >= hmin - below && y0 <= hmax + above) return true;
            }
            return false;
        }

        /// <summary>
 /// The S8a window's LOD-3 classification (S8aPlanetSampler.Classify): an SE box (LOD-0 planet voxels) that may hold a stamp,
 /// a cave or an arch is a surface box, whatever its LOD-3 content says (a 3 m pillar or a tunnel falls between 8 m samples).
 /// </summary>
        public static bool MayHoldFeatures(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, int storageSizeX)
        {
            double tr = storageSizeX * 0.5;
            if (S8aSample.Setting && S8aSample.MayFill(minX - tr, minY - tr, minZ - tr, maxX + 1 - tr, maxY + 1 - tr, maxZ + 1 - tr))
            {
                S8aSample.CountSurface();
                return true;
            }
            RingField field = _state == 1 ? _field : null;
            if (field == null) return false;
            double x0, x1, y0, y1, z0, z1;
            bool zone;
            return Touches(field, minX - tr, minY - tr, minZ - tr, maxX + 1 - tr, maxY + 1 - tr, maxZ + 1 - tr, 1.0, out x0, out x1, out y0, out y1, out z0, out z1, out zone)
                   && FeaturesIn(field, x0, x1, y0, y1, z0, z1);
        }

        // ================================================================================================= game thread: log, status, probes

 /// <summary>Per tick (S8aHost.Tick, the game thread): its thread id for the cost rows, then the init line and row, once.</summary>
        public static void Tick()
        {
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
            if (_state == 0 && Setting && PatchInstalled && Interlocked.CompareExchange(ref _kicked, 1, 0) == 0)
            {
                MyPlanet planet = SiteBinding.Planet;
                MyPlanetStorageProvider prov = planet != null ? planet.Provider : null;
                if (prov != null && Ours(prov)) ThreadPool.QueueUserWorkItem(_ => { try { EnsureField(prov); } catch (Exception) { } });
                else Interlocked.Exchange(ref _kicked, 0);
            }
            if (_initLogged || _state == 0 || _initLine == null) return;
            _initLogged = true;
            DeLog.Info(_initLine);
            RingField f = _field;
            int used = 0;
            double nearest = f != null ? f.NearestSameBrushM(out used) : double.NaN;
            Jsonl.WriteLine(S4Rows.Row("s8a.ring.init", "ok", _state == 1, "refusal", _refusal, "initMs", _initMs, "placeMs", f != null ? f.PlaceMs : 0.0,
                                       "placements", f != null ? f.Placements.Count : 0, "brushesUsed", used, "nearestSameBrushM", double.IsInfinity(nearest) ? -1.0 : nearest,
                                       "library", f != null ? f.Library.Brushes.Count : 0, "forbidden", f != null ? f.Library.Forbidden().Count : -1,
                                       "privateBefore", _ramBefore, "privateAfter", _ramAfter, "source", _source, "maxLod", MaxLod,
                                       "placementsHash", f != null ? f.PlacementsHash().ToString("x16", CultureInfo.InvariantCulture) : null,
                                       "rules", RingRules.TextHash(RingRules.Seed).ToString("x16", CultureInfo.InvariantCulture)));
        }

        /// <summary>The cost table: per category, LOD and thread (game / other) with samples: count, p50, p95, max (ms), ns per voxel.</summary>
        private static List<object> CostTable()
        {
            var rows = new List<object>();
            lock (StatLock)
            {
                for (int c = 0; c < 3; c++)
                    for (int l = 0; l < 8; l++)
                        for (int t = 0; t < 2; t++)
                        {
                            Reservoir r = _res[c, l, t];
                            if (r.Count == 0) continue;
                            int n = (int)Math.Min(r.Count, r.V.Length);
                            var v = new float[n];
                            Array.Copy(r.V, v, n);
                            Array.Sort(v);
                            rows.Add(new Dictionary<string, object>
                            {
                                { "cat", CatNames[c] }, { "lod", l }, { "thread", t == 0 ? "game" : "worker" }, { "count", r.Count }, { "p50", Pct(v, 0.5) },
                                { "p95", Pct(v, 0.95) }, { "max", r.Max }, { "nsPerVoxel", r.Voxels > 0 ? r.TotalMs * 1e6 / r.Voxels : 0.0 },
                            });
                        }
            }
            return rows;
        }

        public static Dictionary<string, object> Status()
        {
            RingField f = _state == 1 ? _field : null;
            int used = 0;
            double nearest = f != null ? f.NearestSameBrushM(out used) : double.NaN;
            List<int> forbidden = f != null ? f.Library.Forbidden() : null;
            int forbiddenUsed = 0;
            if (f != null && forbidden.Count > 0)
            {
                var bad = new HashSet<int>(forbidden);
                foreach (RingPlacement p in f.Placements) if (bad.Contains(p.Brush.Id)) forbiddenUsed++;
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "setting", Setting }, { "patch", PatchInstalled }, { "maxLod", MaxLod }, { "state", _state == 1 ? "ready" : _state == -1 ? "refused" : "untried" },
                { "refusal", _refusal }, { "source", _source }, { "initMs", _initMs }, { "privateBeforeMB", _ramBefore / 1048576.0 }, { "privateAfterMB", _ramAfter / 1048576.0 },
                { "privateNowMB", PrivateBytes() / 1048576.0 }, { "identity", Identity().ToString("x16", CultureInfo.InvariantCulture) },
                { "rules", RingRules.TextHash(RingRules.Seed).ToString("x16", CultureInfo.InvariantCulture) }, { "fieldVersion", RingRules.FieldVersion },
                { "heightsFnv", f != null ? f.Base.HeightsFnv.ToString("x16", CultureInfo.InvariantCulture) : null },
                { "biomesFnv", f != null ? f.Base.BiomesFnv.ToString("x16", CultureInfo.InvariantCulture) : null },
                { "libraryFnv", f != null ? f.Library.BinFnv.ToString("x16", CultureInfo.InvariantCulture) : null },
                { "indexFnv", f != null ? f.Library.IndexFnv.ToString("x16", CultureInfo.InvariantCulture) : null },
                { "placements", f != null ? f.Placements.Count : 0 }, { "brushesUsed", used }, { "nearestSameBrushM", f == null || double.IsInfinity(nearest) ? -1.0 : nearest },
                { "repeatM", RingRules.RepeatM }, { "library", f != null ? f.Library.Brushes.Count : 0 }, { "forbiddenBrushes", forbidden != null ? forbidden.Count : -1 },
                { "forbiddenPlacements", forbiddenUsed }, { "placeMs", f != null ? f.PlaceMs : 0.0 },
                { "placementsHash", f != null ? f.PlacementsHash().ToString("x16", CultureInfo.InvariantCulture) : null },
                { "reachAboveM", f != null ? f.ReachAboveM : 0.0 }, { "reachBelowM", f != null ? f.ReachBelowM : 0.0 },
                { "reads", Interlocked.Read(ref _reads) }, { "voxels", Interlocked.Read(ref _voxels) }, { "passes", Interlocked.Read(ref _passes) },
                { "detects", Interlocked.Read(ref _detects) }, { "errors", Interlocked.Read(ref _errors) }, { "lastError", _lastError },
                { "intersectWidened", Interlocked.Read(ref _intersectWidened) }, { "servedOutside", Interlocked.Read(ref _servedOutside) },
                { "servedAboveLod", Interlocked.Read(ref _servedAboveLod) }, { "failInjected", Interlocked.Read(ref _failInjected) },
                { "failAt", double.IsNaN(_failX) ? null : new object[] { _failX, _failZ } }, { "m15", S8aMutants.M15 },
                { "zoneM", new object[] { ZoneInnerM, ZoneOuterM } }, { "storageSizeX", _storageSizeX },
                { "cost", CostTable() }, { "blend", _blend },
            };
        }

        private static double Pct(float[] sorted, double q)
        {
            if (sorted.Length == 0) return 0.0;
            int i = (int)Math.Ceiling(q * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, i))];
        }

        private static long PrivateBytes()
        {
            try { using (var p = Process.GetCurrentProcess()) return p.PrivateMemorySize64; }
            catch (Exception) { return -1; }
        }

        private static string Mb(long b) { return b < 0 ? "?" : (b / 1048576.0).ToString("0", CultureInfo.InvariantCulture); }

        /// <summary>de.s8a ring {failAt: [x, z]} (S8a.M15's column, site-v1 SN); null clears it.</summary>
        public static Dictionary<string, object> SetFailAt(IList<object> xz)
        {
            if (xz == null || xz.Count < 2) { _failX = _failZ = double.NaN; }
            else { _failX = Convert.ToDouble(xz[0], CultureInfo.InvariantCulture); _failZ = Convert.ToDouble(xz[1], CultureInfo.InvariantCulture); }
            return new Dictionary<string, object> { { "ok", true }, { "failAt", double.IsNaN(_failX) ? null : new object[] { _failX, _failZ } }, { "m15", S8aMutants.M15 },
                                                    { "line", "de.s8a ring failAt " + (double.IsNaN(_failX) ? "cleared" : _failX.ToString("0.0", CultureInfo.InvariantCulture) + ", " + _failZ.ToString("0.0", CultureInfo.InvariantCulture)) } };
        }

        /// <summary>
 /// de.s8a ring {pick}: spots for the nf part (site-v1 SN): per ring biome its first placements within the dive zone (rho &lt;=
 /// maxRho); up to <paramref name="cliffs"/> steep spots on a window batch seam (SN x = -2048 + 160 k): wall / overhang placements
 /// crossed by a seam line, ranked by the field's surface step across it; and plain spots (no placement within 150 m, no cave
 /// or arch biome, the field equal to the heightmap) for the fallback (S8a.M15).
 /// </summary>
        public static Dictionary<string, object> Pick(double maxRho, int perBiome, int cliffs)
        {
            RingField f = Field;
            if (f == null) return new Dictionary<string, object> { { "ok", false }, { "error", "the ring field is not ready (" + (_refusal ?? "untried: no planet read yet") + ")" } };
            var spots = new List<object>();
            var count = new Dictionary<string, int>();
            foreach (RingPlacement p in f.Placements)
            {
                double r = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (r > maxRho) continue;
                int c;
                count.TryGetValue(p.Biome, out c);
                if (c >= perBiome) continue;
                count[p.Biome] = c + 1;
                spots.Add(PlacementRow(p));
            }
            var seamRows = new List<KeyValuePair<double, object>>();
            foreach (RingPlacement p in f.Placements)
            {
                if (p.Brush.Class != "wall" && p.Brush.Class != "overhang") continue;
                double r = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (r > maxRho - 100.0) continue;
                double seam = -2048.0 + 160.0 * Math.Round((p.X + 2048.0) / 160.0);
                if (Math.Abs(seam - p.X) > p.RadiusM * 0.6) continue;
                double best = 0, bestZ = p.Z;
                for (double dz = -p.RadiusM * 0.6; dz <= p.RadiusM * 0.6; dz += 2.0)
                {
                    double a = TopAt(f, seam - 1.0, p.Z + dz, p.H + 140.0, p.H - 140.0), b = TopAt(f, seam + 1.0, p.Z + dz, p.H + 140.0, p.H - 140.0);
                    double a2 = TopAt(f, seam, p.Z + dz - 1.0, p.H + 140.0, p.H - 140.0), b2 = TopAt(f, seam, p.Z + dz + 1.0, p.H + 140.0, p.H - 140.0);
                    double slope = Math.Max(Math.Abs(a - b), Math.Abs(a2 - b2)) / 2.0;
                    if (!double.IsNaN(slope) && slope > best) { best = slope; bestZ = p.Z + dz; }
                }
                if (best <= 0.5) continue;
                var row = PlacementRow(p);
                row["seamX"] = seam;
                row["seamZ"] = bestZ;
                row["slope"] = best;
                seamRows.Add(new KeyValuePair<double, object>(best, row));
            }
            seamRows.Sort((a, b) => b.Key.CompareTo(a.Key));
            var seams = new List<object>();
            var usedSeams = new HashSet<long>();
            foreach (var kv in seamRows)
            {
                var row = (Dictionary<string, object>)kv.Value;
                long key = (long)Math.Round((double)row["seamX"]) * 100000L + (long)Math.Round((double)row["z"] / 200.0);
                if (!usedSeams.Add(key)) continue;
                seams.Add(row);
                if (seams.Count >= cliffs) break;
            }
            var plain = new List<object>();
            var near = new List<RingPlacement>();
            for (int k = 0; k < 400 && plain.Count < 2; k++)
            {
                double ang = k * 0.61803398875 * 2.0 * Math.PI, rad = 4200.0 + (k % 7) * 450.0;
                if (rad > maxRho - 150.0) rad = maxRho - 150.0;
                double x = rad * Math.Cos(ang), z = rad * Math.Sin(ang);
                double h;
                int key;
                f.Base.Column(x, z, out h, out key);
                if (double.IsNaN(h) || h > -20.0) continue;
                RingRule rule = key >= 0 ? RingRules.RuleOfKey[key] : null;
                if (rule != null && (rule.Caves || rule.Arches)) continue;
                f.Near(x - 150.0, x + 150.0, z - 150.0, z + 150.0, near);
                if (near.Count > 0) continue;
                plain.Add(new Dictionary<string, object> { { "x", x }, { "z", z }, { "h", h }, { "biome", key >= 0 ? RingRules.Keys[key] : null } });
            }
            return new Dictionary<string, object> { { "ok", true }, { "spots", spots }, { "seams", seams }, { "plain", plain }, { "placements", f.Placements.Count },
                                                    { "line", "de.s8a ring pick: " + spots.Count + " spots, " + seams.Count + " steep seam spots (of " + seamRows.Count + "), " + plain.Count + " plain spots" } };
        }

        private static Dictionary<string, object> PlacementRow(RingPlacement p)
        {
            return new Dictionary<string, object>
            {
                { "i", p.Index }, { "x", p.X }, { "z", p.Z }, { "h", p.H }, { "biome", p.Biome }, { "class", p.Brush.Class }, { "brush", p.Brush.Id },
                { "size", p.Brush.Size }, { "scale", p.Scale }, { "radius", p.RadiusM }, { "mirror", p.Mirror },
            };
        }

        /// <summary>The field's top surface at an SN column (the first rock from fromY down, 0.25 m steps then bisection), NaN without rock.</summary>
        public static double TopAt(RingField f, double x, double z, double fromY, double toY)
        {
            double h;
            int key;
            f.Base.Column(x, z, out h, out key);
            if (double.IsNaN(h)) return double.NaN;
            var near = new List<RingPlacement>();
            f.Near(x, x, z, z, near);
            double prev = fromY, fp = f.Sample(x, prev, z, h, key, near);
            if (fp > 0) return fromY;
            for (double y = fromY - 0.25; y >= toY; y -= 0.25)
            {
                double fy = f.Sample(x, y, z, h, key, near);
                if (fy > 0)
                {
                    double lo = y, hi = prev;
                    for (int it = 0; it < 12; it++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (f.Sample(x, mid, z, h, key, near) > 0) lo = mid; else hi = mid;
                    }
                    return 0.5 * (lo + hi);
                }
                prev = y;
            }
            return double.NaN;
        }

        /// <summary>de.s8a ring {columns, fromY, toY}: the field's own top surface and the heightmap seabed at SN columns (the analyzer's third witness).</summary>
        public static Dictionary<string, object> Surface(IList<object> columns, double fromY, double toY)
        {
            RingField f = Field;
            if (f == null) return new Dictionary<string, object> { { "ok", false }, { "error", "the ring field is not ready" } };
            var res = new List<object>();
            foreach (object o in columns)
            {
                var c = o as IList<object>;
                if (c == null || c.Count < 2) continue;
                double x = Convert.ToDouble(c[0], CultureInfo.InvariantCulture), z = Convert.ToDouble(c[1], CultureInfo.InvariantCulture);
                double y = TopAt(f, x, z, fromY, toY);
                double h;
                int key;
                f.Base.Column(x, z, out h, out key);
                res.Add(new object[] { x, z, double.IsNaN(y) ? null : (object)y, double.IsNaN(h) ? null : (object)h });
            }
            return new Dictionary<string, object> { { "ok", true }, { "results", res }, { "line", "de.s8a ring surface: " + res.Count + " columns" } };
        }

        /// <summary>
 /// de.s8a ring {serve: [[ax, ay, az, sx, sy, sz, lod],...]} (S8a.NF.determinism): per planet-local box (the vectors' "serve"
 /// lines) the FNV of <see cref="RingField.ServeBox"/> and of the planet's own generator read through the prefix
 /// and the field's inputs' FNVs.
 /// </summary>
        public static Dictionary<string, object> Serve(IList<object> boxes)
        {
            RingField f = Field;
            MyPlanet planet = SiteBinding.Planet;
            if (f == null || planet == null || planet.Provider == null) return new Dictionary<string, object> { { "ok", false }, { "error", f == null ? "the ring field is not ready" : "no planet" } };
            var res = new List<object>();
            double tr = planet.Provider.StorageSize.X * 0.5;
            foreach (object o in boxes)
            {
                var b = o as IList<object>;
                if (b == null || b.Count < 7) continue;
                double ax = Convert.ToDouble(b[0], CultureInfo.InvariantCulture), ay = Convert.ToDouble(b[1], CultureInfo.InvariantCulture), az = Convert.ToDouble(b[2], CultureInfo.InvariantCulture);
                int sx = Convert.ToInt32(b[3], CultureInfo.InvariantCulture), sy = Convert.ToInt32(b[4], CultureInfo.InvariantCulture), sz = Convert.ToInt32(b[5], CultureInfo.InvariantCulture);
                int lod = Convert.ToInt32(b[6], CultureInfo.InvariantCulture);
                double ls = lod;
                int lodIndex = lod == 1 ? 0 : lod == 2 ? 1 : lod == 4 ? 2 : lod == 8 ? 3 : -1;
                var row = new Dictionary<string, object> { { "box", b } };
                double x0, x1, y0, y1, z0, z1;
                if (lodIndex < 0 || !RingField.SnBounds(ax, ay, az, ax + (sx - 1) * ls, ay + (sy - 1) * ls, az + (sz - 1) * ls, out x0, out x1, out y0, out y1, out z0, out z1))
                {
                    row["error"] = "bad box";
                    res.Add(row);
                    continue;
                }
                var near = new List<RingPlacement>();
                f.Near(x0 - 2.0, x1 + 2.0, z0 - 2.0, z1 + 2.0, near);
                var c = new byte[sx * sy * sz];
                f.ServeBox(ax, ay, az, sx, sy, sz, ls, c, 0, sx, sx * sy, near);
                row["fieldFnv"] = RingHash.Fnv(c).ToString("x16", CultureInfo.InvariantCulture);
                // the generator itself: storage voxel coordinates of the box at its LOD (the box's corner a multiple of the LOD voxel)
                double gx = (ax + tr) / ls, gy = (ay + tr) / ls, gz = (az + tr) / ls;
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
            Dictionary<string, object> st = Status();
            return new Dictionary<string, object>
            {
                { "ok", true }, { "results", res }, { "heightsFnv", st["heightsFnv"] }, { "biomesFnv", st["biomesFnv"] }, { "libraryFnv", st["libraryFnv"] },
                { "indexFnv", st["indexFnv"] }, { "rules", st["rules"] }, { "placementsHash", st["placementsHash"] },
                { "line", "de.s8a ring serve: " + res.Count + " boxes" },
            };
        }

        // ------------------------------------------------------------------------------------------ S8a.F.blend from the served planet

        private static volatile Dictionary<string, object> _blend;
        private static int _blendRunning;

        /// <summary>
 /// de.s8a ring {blend: n}: on a pool thread, n placements spread over the ring within the dive zone: their profiles along SN x
 /// and z through the centre (1 m columns over the brush's diameter + 8 m each side) read back from the planet's storage (what SE
 /// serves, edits included; LOD 0, trilinear content, the 0.5 crossing from the top), the surface step between neighbour columns
 /// split into border pairs (one column inside the stamp, one outside) and inner pairs (both with weight 1): S8a.F.blend.
 /// </summary>
        public static Dictionary<string, object> StartBlend(int count)
        {
            RingField f = Field;
            MyPlanet planet = SiteBinding.Planet;
            if (f == null || planet == null) return new Dictionary<string, object> { { "ok", false }, { "error", f == null ? "the ring field is not ready" : "no planet" } };
            if (Interlocked.CompareExchange(ref _blendRunning, 1, 0) != 0) return new Dictionary<string, object> { { "ok", true }, { "running", true }, { "line", "de.s8a ring blend: already running" } };
            _blend = new Dictionary<string, object> { { "running", true }, { "count", count } };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var res = new Dictionary<string, object> { { "running", false }, { "count", count } };
                try { Blend(f, planet, count, res); }
                catch (Exception e) { res["error"] = e.GetType().Name + ": " + e.Message; }
                _blend = res;
                Interlocked.Exchange(ref _blendRunning, 0);
            });
            return new Dictionary<string, object> { { "ok", true }, { "running", true }, { "line", "de.s8a ring blend: started over " + count + " placements" } };
        }

        private static void Blend(RingField f, MyPlanet planet, int count, Dictionary<string, object> res)
        {
            var st = planet.Storage as MyStorageBase;
            if (st == null) throw new InvalidOperationException("the planet's storage is not a MyStorageBase");
            double tr = st.Size.X * 0.5;
            var border = new List<double>();
            var inner = new List<double>();
            var placements = new List<object>();
            var sw = Stopwatch.StartNew();
            int stepN = Math.Max(1, f.Placements.Count / Math.Max(1, count * 2));
            var data = new MyStorageData(MyStorageDataTypeFlags.Content);
            int done = 0;
            for (int pi = 0; pi < f.Placements.Count && done < count; pi += stepN)
            {
                RingPlacement p = f.Placements[pi];
                double r = Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (r < RingRules.RhoMin || r > 7600.0) continue;
                done++;
                double half = p.RadiusM + 8.0;
                double top = p.H + (p.Brush.Size - p.Ground) * p.Scale + 4.0, bottom = p.H - p.Ground * p.Scale - 12.0;
                int nb = 0, ni = 0;
                for (int axis = 0; axis < 2; axis++)
                {
                    int n = (int)Math.Ceiling(2 * half);
                    var tops = new double[n];
                    var w = new double[n];
                    double bx0 = double.PositiveInfinity, by0 = double.PositiveInfinity, bz0 = double.PositiveInfinity, bx1 = double.NegativeInfinity, by1 = double.NegativeInfinity, bz1 = double.NegativeInfinity;
                    for (int e = 0; e < 4; e++)
                    {
                        double t = e % 2 == 0 ? -half : half, y = e < 2 ? top : bottom;
                        Vec3 se = Map0.SnToSe(new Vec3(p.X + (axis == 0 ? t : 0), y, p.Z + (axis == 1 ? t : 0)));
                        bx0 = Math.Min(bx0, se.X); by0 = Math.Min(by0, se.Y); bz0 = Math.Min(bz0, se.Z);
                        bx1 = Math.Max(bx1, se.X); by1 = Math.Max(by1, se.Y); bz1 = Math.Max(bz1, se.Z);
                    }
                    var min = new Vector3I((int)Math.Floor(bx0 + tr) - 2, (int)Math.Floor(by0 + tr) - 2, (int)Math.Floor(bz0 + tr) - 2);
                    var max = new Vector3I((int)Math.Ceiling(bx1 + tr) + 2, (int)Math.Ceiling(by1 + tr) + 2, (int)Math.Ceiling(bz1 + tr) + 2);
                    data.Resize(min, max);
                    st.ReadRange(data, MyStorageDataTypeFlags.Content, 0, min, max);
                    byte[] c = data[MyStorageDataTypeEnum.Content];
                    int sx = max.X - min.X + 1, sy = max.Y - min.Y + 1;
                    Func<double, double, double, double> at = (x, y, z) =>
                    {
                        Vec3 se = Map0.SnToSe(new Vec3(x, y, z));
                        double lx = se.X + tr - min.X, ly = se.Y + tr - min.Y, lz = se.Z + tr - min.Z;
                        int ix = (int)Math.Floor(lx), iy = (int)Math.Floor(ly), iz = (int)Math.Floor(lz);
                        if (ix < 0 || iy < 0 || iz < 0 || ix + 1 > max.X - min.X || iy + 1 > max.Y - min.Y || iz + 1 > max.Z - min.Z) return double.NaN;
                        double fx = lx - ix, fy = ly - iy, fz = lz - iz;
                        Func<int, int, int, double> v = (a, b, d) => c[(ix + a) + sx * ((iy + b) + sy * (iz + d))];
                        double c00 = v(0, 0, 0) + fx * (v(1, 0, 0) - v(0, 0, 0)), c10 = v(0, 1, 0) + fx * (v(1, 1, 0) - v(0, 1, 0));
                        double c01 = v(0, 0, 1) + fx * (v(1, 0, 1) - v(0, 0, 1)), c11 = v(0, 1, 1) + fx * (v(1, 1, 1) - v(0, 1, 1));
                        double c0 = c00 + fy * (c10 - c00), c1 = c01 + fy * (c11 - c01);
                        return c0 + fz * (c1 - c0);
                    };
                    for (int k = 0; k < n; k++)
                    {
                        double t = -half + k + 0.5;
                        double x = p.X + (axis == 0 ? t : 0), z = p.Z + (axis == 1 ? t : 0);
                        double h;
                        int key;
                        f.Base.Column(x, z, out h, out key);
                        w[k] = RingField.Weight(p, x, h, z, h);
                        tops[k] = double.NaN;
                        double prev = at(x, top, z);
                        for (double y = top - 0.25; y >= bottom; y -= 0.25)
                        {
                            double cur = at(x, y, z);
                            if (!double.IsNaN(cur) && !double.IsNaN(prev) && cur >= 127.5 && prev < 127.5)
                            {
                                tops[k] = y + 0.25 * (cur - 127.5) / (cur - prev);
                                break;
                            }
                            prev = cur;
                        }
                    }
                    for (int k = 0; k + 1 < n; k++)
                    {
                        if (double.IsNaN(tops[k]) || double.IsNaN(tops[k + 1])) continue;
                        double s = Math.Abs(tops[k + 1] - tops[k]);
                        if ((w[k] > 0) != (w[k + 1] > 0)) { border.Add(s); nb++; }
                        else if (w[k] >= 0.99 && w[k + 1] >= 0.99) { inner.Add(s); ni++; }
                    }
                }
                placements.Add(new Dictionary<string, object> { { "i", p.Index }, { "x", p.X }, { "z", p.Z }, { "class", p.Brush.Class }, { "border", nb }, { "inner", ni } });
            }
            border.Sort();
            inner.Sort();
            Func<List<double>, double, double> pct = (l, q) => l.Count == 0 ? 0.0 : l[Math.Max(0, Math.Min(l.Count - 1, (int)Math.Ceiling(q * l.Count) - 1))];
            double b95 = pct(border, 0.95), i95 = pct(inner, 0.95);
            res["placements"] = placements;
            res["borderPairs"] = border.Count;
            res["innerPairs"] = inner.Count;
            res["borderP95"] = b95;
            res["innerP95"] = i95;
            res["bound"] = Math.Max(2.0 * i95, 2.0);
            res["ok"] = border.Count > 0 && b95 <= Math.Max(2.0 * i95, 2.0);
            res["ms"] = sw.Elapsed.TotalMilliseconds;
        }
    }
}
