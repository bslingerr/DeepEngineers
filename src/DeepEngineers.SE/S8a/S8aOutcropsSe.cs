using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Wire;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRage.Voxels;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// docs/S8A-PLANET-V2.md R8; Wire <see cref="S8aOutcrops"/>): SE's own
 /// voxels around the camera scanned per ore-detector cell
 /// at LOD 1: per ore its volume, its
 /// exposed voxels (solid ore with an empty face neighbour) and the detector's centroid (the same formula). An outcrop gets a
 /// record: anchored at the exposed voxel horizontally nearest that centroid, sized by the first-sighting exposed area and shrunk
 /// by the cube root of the volume left (the drill mines it: the storage loses the voxels, RangeChanged marks the cell, a rescan
 /// follows). The records go to the store folder (<see cref="S8aOutcrops.FileName"/>, SN coordinates of the current chart, at
 /// most <see cref="S8aOutcrops.WriteHz"/>); Subnautica keeps one of its own large deposits per record (S8aOreDeposits).
 /// Every voxel body near the camera is scanned (the planet and the tiles); an outcrop within 8 m of a deposit Subnautica reported
 /// itself (crater-deposits.tsv: its pocket) is left to Subnautica's own deposit. A worker does the reads; the main thread plans,
 /// absorbs and writes. <c>de.s8a outcrops {status | probe {radius} | detect {radius}}</c>: detect runs SE's own MyDepositQuery
 /// (reflection; the oracle of S8a.NO.vein / NO.agree). Off with DEEPENGINEERS_S8A_ORES_VISIBLE=off.
 /// </summary>
    internal static class S8aOutcropsSe
    {
        public const string EnvSwitch = "DEEPENGINEERS_S8A_ORES_VISIBLE";
        public const ulong ScanEveryMs = 500;
        public const int MaxCellsPerJob = 48;

        private struct CellId : IEquatable<CellId>
        {
            public long Body;
            public int X, Y, Z;
            public bool Equals(CellId o) { return Body == o.Body && X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object o) { return o is CellId && Equals((CellId)o); }
            public override int GetHashCode() { unchecked { return (int)Body * 73856093 ^ X * 19349663 ^ Y * 83492791 ^ Z; } }
        }

        private sealed class OreRec
        {
            public int Ore;
            public int OreVoxels, ExposedVoxels;
            public Vector3D Anchor;        // world
            public Vector3 Normal;         // world
            public Vector3D Det;           // world (the detector's centroid; the exposed centroid when the detector saw none)
            public bool HasDet;
            public int LocalVoxels;        // a test run: ore voxels within LocalRadiusVoxels of the first anchor (this anchor on the first sighting)
            public int AnchorPacked = -1;  // the anchor's packed inner LOD 1 voxel
        }

        private sealed class CellTask
        {
            public CellId Id;
            public MyVoxelBase Body;
            public Vector3D Corner;
            public Vector3 Up;
            public List<OreRec> Ores;
            /// <summary>a test run: per ore index the first sighting's anchor (packed inner LOD 1 voxel), -1 none (from the memory, main thread).</summary>
            public int[] FirstAnchor;
            public bool Solid;
            public string Error;
        }

        private sealed class Memory { public double FirstExposed, FirstVolume, FirstLocal; public int FirstAnchorPacked = -1; public bool Mined; }

        private sealed class Job
        {
            public readonly List<CellTask> Cells = new List<CellTask>();
            public volatile bool Done;
            public long Ms;
        }

        private static bool _envRead, _on = true;
        private static sbyte[] _oreOf;
        private static readonly Dictionary<CellId, CellTask> Cells = new Dictionary<CellId, CellTask>();
        private static readonly Dictionary<ulong, Memory> Mem = new Dictionary<ulong, Memory>();
        private static readonly HashSet<CellId> Dirty = new HashSet<CellId>();

        // large range (a tile created or reset, a big write) expanded every 32-voxel cell of it into Dirty under the lock, on the
 // thread that changed the storage, while Plan on the update thread took the same lock once per candidate cell. A range of
 // more than BigRangeCells cells is now kept as one box (O(1) under the lock) and a cell is dirty while a box newer than its
 // last consumption covers it: the same cells are dirty, consumed once, as before. Env DEEPENGINEERS_S10_OUTCROPSWAIT off
 // restores the per-cell expansion. Counters in the s8a.outcrops row.
        private struct DirtyBox { public long Body, Seq; public int X0, X1, Y0, Y1, Z0, Z1; }
        public const long BigRangeCells = 65536;
        public const int MaxBoxes = 256;
        private static readonly List<DirtyBox> Boxes = new List<DirtyBox>();
        private static readonly Dictionary<CellId, long> Consumed = new Dictionary<CellId, long>();
        private static long _boxSeq;
        public static long S10BoxesMade, S10BoxesMerged;
        private static long _s10HoldMax, _s10WaitMax, _s10RangeMax;
        public static readonly bool S10BoxesOn = S10EnvOn("DEEPENGINEERS_S10_OUTCROPSWAIT", true);

        private static bool S10EnvOn(string name, bool dflt)
        {
            string s = null;
            try { s = Environment.GetEnvironmentVariable(name); } catch (Exception) { }
            return S10AllocCuts.Parse(s, dflt);
        }

        /// <summary>Plan's check (update thread): the cell is dirty (marked or covered by a box newer than its last consumption);
 /// a dirty cell is consumed. With no box, exactly the old Dirty.Remove.</summary>
        private static bool TakeDirty(CellId id)
        {
            long w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (Dirty)
            {
                long w = System.Diagnostics.Stopwatch.GetTimestamp() - w0;
                if (w > _s10WaitMax) _s10WaitMax = w;
                bool d = Dirty.Remove(id);
                if (Boxes.Count == 0) return d;
                long seen;
                Consumed.TryGetValue(id, out seen);
                if (!d)
                    foreach (DirtyBox b in Boxes)
                        if (b.Seq > seen && b.Body == id.Body && id.X >= b.X0 && id.X <= b.X1 && id.Y >= b.Y0 && id.Y <= b.Y1 && id.Z >= b.Z0 && id.Z <= b.Z1) { d = true; break; }
                if (d) Consumed[id] = _boxSeq;
                return d;
            }
        }

        /// <summary>A box is added (under the Dirty lock). Beyond <see cref="MaxBoxes"/> the two oldest of the same body merge into
 /// their bounding box (more cells rescanned, never fewer).</summary>
        private static void AddBox(DirtyBox nb)
        {
            nb.Seq = ++_boxSeq;
            Boxes.Add(nb);
            S10BoxesMade++;
            if (Boxes.Count <= MaxBoxes) return;
            for (int i = 0; i < Boxes.Count; i++)
                for (int k = i + 1; k < Boxes.Count; k++)
                {
                    DirtyBox a = Boxes[i], b = Boxes[k];
                    if (a.Body != b.Body) continue;
                    var m = new DirtyBox { Body = a.Body, Seq = Math.Max(a.Seq, b.Seq), X0 = Math.Min(a.X0, b.X0), X1 = Math.Max(a.X1, b.X1), Y0 = Math.Min(a.Y0, b.Y0),
                                           Y1 = Math.Max(a.Y1, b.Y1), Z0 = Math.Min(a.Z0, b.Z0), Z1 = Math.Max(a.Z1, b.Z1) };
                    Boxes[i] = m;
                    Boxes.RemoveAt(k);
                    S10BoxesMerged++;
                    return;
                }
            Boxes.RemoveAt(0); // only one box per body and more bodies than boxes: unreachable in practice; the oldest goes
        }
        private static readonly Dictionary<long, KeyValuePair<MyStorageBase, Action<Vector3I, Vector3I, MyStorageDataTypeFlags>>> Subs =
            new Dictionary<long, KeyValuePair<MyStorageBase, Action<Vector3I, Vector3I, MyStorageDataTypeFlags>>>();
        private static Job _job;
        private static ulong _nextScanMs, _nextWriteMs;
        private static bool _changed;
        private static uint _chartSeqWritten = uint.MaxValue;
        private static long _writeSeq;
        private static List<S8aOutcrop> _records = new List<S8aOutcrop>();
        private static Dictionary<ulong, CellId> _keyCell = new Dictionary<ulong, CellId>();
        public static long Jobs, CellsScanned, Writes, Errors, Excluded;
        public static long LastJobMs;
        // the detector oracle
        private static readonly object DetLock = new object();
        private static List<Dictionary<string, object>> _det;
        private static int _detPending;
        private static long _detRuns;

        public static bool On { get { ReadEnv(); return _on; } }

        private static void ReadEnv()
        {
            if (_envRead) return;
            _envRead = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvSwitch); } catch (Exception) { }
            if (!string.IsNullOrEmpty(v) && (v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) || v.Trim() == "0")) _on = false;
        }

        public static void Reset()
        {
            foreach (var kv in Subs) { try { kv.Value.Key.RangeChanged -= kv.Value.Value; } catch (Exception) { } }
            Subs.Clear();
            lock (Dirty) { Dirty.Clear(); Boxes.Clear(); Consumed.Clear(); }
            Cells.Clear();
            Mem.Clear();
            _job = null;
            _records = new List<S8aOutcrop>();
            _chartSeqWritten = uint.MaxValue;
            _oreOf = null;
        }

        private static void BuildTable()
        {
            var t = new sbyte[256];
            for (int i = 0; i < 256; i++)
            {
                t[i] = -1;
                try
                {
                    MyVoxelMaterialDefinition d = MyDefinitionManager.Static.GetVoxelMaterialDefinition((byte)i);
                    if (d != null) t[i] = (sbyte)S8aOutcrops.OreIndex(d.Id.SubtypeName);
                }
                catch (Exception) { }
            }
            _oreOf = t;
        }

        /// <summary>Per tick (game thread): absorb a finished job, plan the next, write the file when it changed.</summary>
        public static void Tick(ulong nowMs, Vec3 camSe, bool haveCam, ulong storeKey)
        {
            if (!On || !haveCam || storeKey == 0) return;
            SiteMap map = SiteBinding.Map;
            if (map == null) return;
            if (_oreOf == null) BuildTable();
            Job j = _job;
            long s10 = System.Diagnostics.Stopwatch.GetTimestamp(); // S10 hook (round 3): the step's three parts timed (s8a.outcrops s10.*MsMax)
            if (j != null && j.Done)
            {
                _job = null;
                Absorb(j, map, nowMs);
            }
            s10 = S10Part(ref _s10AbsorbMax, s10);
            if (_job == null && nowMs >= _nextScanMs)
            {
                _nextScanMs = nowMs + ScanEveryMs;
                Plan(camSe, map);
            }
            s10 = S10Part(ref _s10PlanMax, s10);
            uint cs = S8aChartManager.ChartSeq;
            if ((_changed || cs != _chartSeqWritten) && nowMs >= _nextWriteMs)
            {
                _nextWriteMs = nowMs + (ulong)(1000.0 / S8aOutcrops.WriteHz);
                Write(map, storeKey, cs, camSe);
            }
            S10Part(ref _s10WriteMax, s10);
        }

        private static long _s10AbsorbMax, _s10PlanMax, _s10WriteMax, _s10FileMax;

        private static long S10Part(ref long max, long t0)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp();
            if (t - t0 > max) max = t - t0;
            return t;
        }

        // S10 (round 3): the outcrops file written by one background thread in FIFO order (the same text, every version, in
 // order) instead of File.WriteAllText + File.Replace on the update thread (env DEEPENGINEERS_S10_OUTCROPSWAIT off restores)
        private static readonly System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<string, string>> FileQueue =
            new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<string, string>>();
        private static readonly AutoResetEvent FileWake = new AutoResetEvent(false);
        private static Thread _fileThread;
        private static readonly object FileThreadLock = new object();

        private static void QueueFile(string path, string text)
        {
            FileQueue.Enqueue(new KeyValuePair<string, string>(path, text));
            lock (FileThreadLock)
                if (_fileThread == null)
                {
                    _fileThread = new Thread(FileRun) { Name = "DE.S8a outcrops writer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    _fileThread.Start();
                }
            FileWake.Set();
        }

        private static void FileRun()
        {
            while (true)
            {
                KeyValuePair<string, string> w;
                if (!FileQueue.TryDequeue(out w)) { FileWake.WaitOne(1000); continue; }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                WriteFile(w.Key, w.Value);
                long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                if (d > Interlocked.Read(ref _s10FileMax)) Interlocked.Exchange(ref _s10FileMax, d);
            }
        }

        private static void WriteFile(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                Interlocked.Increment(ref Writes);
            }
            catch (Exception e) { long n = Interlocked.Increment(ref Errors); if (n <= 3) DeLog.Info("DE.S8a outcrops not written: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Plan(Vec3 camSe, SiteMap map)
        {
            var bodies = new List<MyVoxelBase>();
            var cam = new Vector3D(camSe.X, camSe.Y, camSe.Z);
            var sphere = new BoundingSphereD(cam, S8aOutcrops.ScanRadiusM + 32.0);
            try { MyGamePruningStructure.GetAllVoxelMapsInSphere(ref sphere, bodies); } catch (Exception) { }
            MyPlanet planet = SiteBinding.Planet;
            if (planet != null && !bodies.Contains(planet)) bodies.Add(planet);
            var want = new List<KeyValuePair<double, CellTask>>();
            var keep = new HashSet<CellId>();
            foreach (MyVoxelBase b in bodies)
            {
                if (b == null || b.Closed || b.MarkedForClose) continue;
                var st = b.Storage as MyStorageBase;
                if (st == null) continue;
                Subscribe(b, st);
                Vector3D corner = b.PositionLeftBottomCorner;
                Vector3D local = cam - corner;
                double r = S8aOutcrops.ScanRadiusM;
                Vector3I size = st.Size;
                int c0x = Math.Max(0, (int)Math.Floor((local.X - r) / S8aOutcrops.CellM)), c1x = Math.Min(size.X / S8aOutcrops.CellM - 1, (int)Math.Floor((local.X + r) / S8aOutcrops.CellM));
                int c0y = Math.Max(0, (int)Math.Floor((local.Y - r) / S8aOutcrops.CellM)), c1y = Math.Min(size.Y / S8aOutcrops.CellM - 1, (int)Math.Floor((local.Y + r) / S8aOutcrops.CellM));
                int c0z = Math.Max(0, (int)Math.Floor((local.Z - r) / S8aOutcrops.CellM)), c1z = Math.Min(size.Z / S8aOutcrops.CellM - 1, (int)Math.Floor((local.Z + r) / S8aOutcrops.CellM));
                for (int cz = c0z; cz <= c1z; cz++)
                    for (int cy = c0y; cy <= c1y; cy++)
                        for (int cx = c0x; cx <= c1x; cx++)
                        {
                            Vector3D centre = corner + new Vector3D((cx + 0.5) * S8aOutcrops.CellM, (cy + 0.5) * S8aOutcrops.CellM, (cz + 0.5) * S8aOutcrops.CellM);
                            double d = (centre - cam).Length();
                            if (d > r + 28.0) continue;
                            var id = new CellId { Body = b.EntityId, X = cx, Y = cy, Z = cz };
                            keep.Add(id);
                            bool dirty = TakeDirty(id); // S10 hook (round 3): with no big box, exactly the old Dirty.Remove
                            if (Cells.ContainsKey(id) && !dirty) continue;
                            Vec3 e, u, n;
                            map.FrameAt(new Vec3(centre.X, centre.Y, centre.Z), out e, out u, out n);
                            var task = new CellTask { Id = id, Body = b, Corner = corner, Up = new Vector3((float)u.X, (float)u.Y, (float)u.Z), FirstAnchor = new int[S8aOutcrops.Ores.Length] };
                            for (int oi = 0; oi < task.FirstAnchor.Length; oi++)
                            {
                                Memory fm;
                                task.FirstAnchor[oi] = Mem.TryGetValue(S8aOutcrops.Key(id.Body, id.X, id.Y, id.Z, oi), out fm) ? fm.FirstAnchorPacked : -1;
                            }
                            want.Add(new KeyValuePair<double, CellTask>(dirty ? -1.0 : d, task));
                        }
            }
            // cells far from the camera are forgotten (their memory stays: a return shows the same first sighting)
            var drop = new List<CellId>();
            foreach (var kv in Cells)
            {
                if (keep.Contains(kv.Key)) continue;
                MyVoxelBase b = kv.Value.Body;
                Vector3D centre = kv.Value.Corner + new Vector3D((kv.Key.X + 0.5) * S8aOutcrops.CellM, (kv.Key.Y + 0.5) * S8aOutcrops.CellM, (kv.Key.Z + 0.5) * S8aOutcrops.CellM);
                if (b == null || b.Closed || (centre - cam).Length() > S8aOutcrops.DropCellsM) drop.Add(kv.Key);
            }
            foreach (CellId id in drop) { Cells.Remove(id); _changed = true; }
            if (Mem.Count > 50000) Mem.Clear();
            if (want.Count == 0) return;
            want.Sort((a, b) => a.Key.CompareTo(b.Key));
            var job = new Job();
            for (int i = 0; i < want.Count && i < MaxCellsPerJob; i++) job.Cells.Add(want[i].Value);
            for (int i = MaxCellsPerJob; i < want.Count; i++) if (want[i].Key < 0) lock (Dirty) Dirty.Add(want[i].Value.Id);    // keep the rest dirty
            _job = job;
            sbyte[] oreOf = _oreOf;
            ThreadPool.QueueUserWorkItem(_ => Run(job, oreOf));
        }

        private static void Subscribe(MyVoxelBase b, MyStorageBase st)
        {
            if (Subs.ContainsKey(b.EntityId)) return;
            long body = b.EntityId;
            Action<Vector3I, Vector3I, MyStorageDataTypeFlags> h = (min, max, flags) =>
            {
                // a changed range (a drill): its cells and their neighbours (a wall exposed across a cell face) are scanned again
                int x0 = (min.X - 2) >> 5, x1 = (max.X + 2) >> 5, y0 = (min.Y - 2) >> 5, y1 = (max.Y + 2) >> 5, z0 = (min.Z - 2) >> 5, z1 = (max.Z + 2) >> 5;
                long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                long h0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (Dirty)
                {
                    if (cells > _s10RangeMax) _s10RangeMax = cells;
                    if (S10BoxesOn && cells > BigRangeCells) AddBox(new DirtyBox { Body = body, X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Z0 = z0, Z1 = z1 }); // S10 hook: O(1) under the lock
                    else
                        for (int z = z0; z <= z1; z++)
                            for (int y = y0; y <= y1; y++)
                                for (int x = x0; x <= x1; x++)
                                    Dirty.Add(new CellId { Body = body, X = x, Y = y, Z = z });
                    long held = System.Diagnostics.Stopwatch.GetTimestamp() - h0;
                    if (held > _s10HoldMax) _s10HoldMax = held;
                }
            };
            try { st.RangeChanged += h; Subs[body] = new KeyValuePair<MyStorageBase, Action<Vector3I, Vector3I, MyStorageDataTypeFlags>>(st, h); }
            catch (Exception) { }
        }

        /// <summary>The worker: every cell of the job read and scanned.</summary>
        private static void Run(Job job, sbyte[] oreOf)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var lod2 = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            var lod1 = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            int s = S8aOutcrops.ScanN + 2;
            var content = new byte[s * s * s];
            var material = new byte[s * s * s];
            foreach (CellTask c in job.Cells)
            {
                try { Scan(c, oreOf, lod2, lod1, content, material); }
                catch (Exception e) { c.Error = e.GetType().Name + ": " + e.Message; Interlocked.Increment(ref Errors); }
            }
            job.Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            job.Done = true;
        }

        private static void Scan(CellTask c, sbyte[] oreOf, MyStorageData lod2, MyStorageData lod1, byte[] content, byte[] material)
        {
            var st = c.Body.Storage as MyStorageBase;
            c.Ores = new List<OreRec>();
            if (st == null) return;
            // 1. SE's detector replica: LOD 2, 8^3, content > 127, per material the centre of its voxels ((p + cell<<3) * 4 + 2, LOD 0 local)
            var m2 = new Vector3I(c.Id.X << 3, c.Id.Y << 3, c.Id.Z << 3);
            lod2.Resize(new Vector3I(8));
            st.ReadRange(lod2, MyStorageDataTypeFlags.Content, 2, m2, m2 + 7);
            if (!lod2.ContainsVoxelsAboveIsoLevel()) return;
            c.Solid = true;
            var rf = MyVoxelRequestFlags.PreciseOrePositions;
            st.ReadRange(lod2, MyStorageDataTypeFlags.Material, 2, m2, m2 + 7, ref rf);
            var detSum = new Vector3D[S8aOutcrops.Ores.Length];
            var detN = new int[S8aOutcrops.Ores.Length];
            var p = new Vector3I();
            for (p.Z = 0; p.Z < 8; p.Z++)
                for (p.Y = 0; p.Y < 8; p.Y++)
                    for (p.X = 0; p.X < 8; p.X++)
                    {
                        int li = lod2.ComputeLinear(ref p);
                        if (lod2.Content(li) <= 127) continue;
                        int ore = oreOf[lod2.Material(li)];
                        if (ore < 0) continue;
                        detSum[ore] += new Vector3D((p.X + m2.X) * 4.0 + 2.0, (p.Y + m2.Y) * 4.0 + 2.0, (p.Z + m2.Z) * 4.0 + 2.0);
                        detN[ore]++;
                    }
            // 2. LOD 1 with a one-voxel margin (outside the body: solid, a tile's edge is no exposure)
            int n = S8aOutcrops.ScanN, s = n + 2;
            Vector3I size1 = new Vector3I(st.Size.X >> 1, st.Size.Y >> 1, st.Size.Z >> 1);
            var a = new Vector3I(c.Id.X * n - 1, c.Id.Y * n - 1, c.Id.Z * n - 1);
            var b = new Vector3I(a.X + s - 1, a.Y + s - 1, a.Z + s - 1);
            var ra = Vector3I.Max(a, Vector3I.Zero);
            var rb = Vector3I.Min(b, new Vector3I(size1.X - 1, size1.Y - 1, size1.Z - 1));
            for (int i = 0; i < content.Length; i++) { content[i] = 255; material[i] = 0; }
            if (ra.X <= rb.X && ra.Y <= rb.Y && ra.Z <= rb.Z)
            {
                lod1.Resize(ra, rb);
                var rf1 = MyVoxelRequestFlags.PreciseOrePositions;
                st.ReadRange(lod1, MyStorageDataTypeFlags.ContentAndMaterial, 1, ra, rb, ref rf1);
                var q = new Vector3I();
                for (q.Z = ra.Z; q.Z <= rb.Z; q.Z++)
                    for (q.Y = ra.Y; q.Y <= rb.Y; q.Y++)
                        for (q.X = ra.X; q.X <= rb.X; q.X++)
                        {
                            Vector3I r = q - ra;
                            int li = lod1.ComputeLinear(ref r);
                            int di = (q.X - a.X) + s * ((q.Y - a.Y) + s * (q.Z - a.Z));
                            content[di] = lod1.Content(li);
                            material[di] = lod1.Material(li);
                        }
            }
            List<S8aOutcropCellOre> found = S8aOutcrops.ScanCell(content, material, n, oreOf);
            var up = new Vector3D(c.Up.X, c.Up.Y, c.Up.Z);
            foreach (S8aOutcropCellOre o in found)
            {
                var rec = new OreRec { Ore = o.Ore, OreVoxels = o.OreVoxels, ExposedVoxels = o.ExposedVoxels };
                Vector3D refL;
                if (detN[o.Ore] > 0) { refL = detSum[o.Ore] / detN[o.Ore]; rec.HasDet = true; }
                else
                {
                    var sum = Vector3D.Zero;
                    foreach (int pk in o.Exposed) sum += VoxelCentre(c, pk);
                    refL = o.Exposed.Count > 0 ? sum / o.Exposed.Count : Vector3D.Zero;
                }
                rec.Det = c.Corner + refL;
                if (o.Exposed.Count > 0)
                {
                    int best = -1;
                    double bestH = double.MaxValue, bestUp = double.MinValue;
                    for (int k = 0; k < o.Exposed.Count; k++)
                    {
                        Vector3D d = VoxelCentre(c, o.Exposed[k]) - refL;
                        double along = Vector3D.Dot(d, up);
                        double hz = (d - along * up).Length();
                        if (hz < bestH - 1e-6 || (Math.Abs(hz - bestH) <= 1e-6 && along > bestUp)) { bestH = hz; bestUp = along; best = k; }
                    }
                    int pb = o.Exposed[best];
                    int bx = pb & 31, by = (pb >> 5) & 31, bz = (pb >> 10) & 31;
                    var nsum = Vector3.Zero;
                    foreach (int pk in o.Exposed)
                    {
                        int x = pk & 31, y = (pk >> 5) & 31, z = (pk >> 10) & 31;
                        if (Math.Abs(x - bx) > 2 || Math.Abs(y - by) > 2 || Math.Abs(z - bz) > 2) continue;
                        int dx, dy, dz;
                        S8aOutcrops.MaskDirection(pk >> 15, out dx, out dy, out dz);
                        nsum += new Vector3(dx, dy, dz);
                    }
                    rec.Normal = nsum.LengthSquared() > 1e-6f ? Vector3.Normalize(nsum) : c.Up;
                    rec.Anchor = c.Corner + VoxelCentre(c, pb);
                    rec.AnchorPacked = pb & 0x7FFF;
                    // a test run: the ore near the first anchor (a drill there shrinks this deposit, not 1 % of the cell)
                    int fa = c.FirstAnchor != null && o.Ore < c.FirstAnchor.Length && c.FirstAnchor[o.Ore] >= 0 ? c.FirstAnchor[o.Ore] : rec.AnchorPacked;
                    rec.LocalVoxels = S8aOutcrops.LocalOreVoxels(content, material, n, oreOf, o.Ore, fa & 31, (fa >> 5) & 31, (fa >> 10) & 31, S8aOutcrops.LocalRadiusVoxels);
                }
                c.Ores.Add(rec);
            }
        }

        /// <summary>The LOD-0 local centre of a packed LOD-1 voxel of a cell (inner coordinates 0..15).</summary>
        private static Vector3D VoxelCentre(CellTask c, int packed)
        {
            int x = packed & 31, y = (packed >> 5) & 31, z = (packed >> 10) & 31;
            int n = S8aOutcrops.ScanN;
            return new Vector3D((c.Id.X * n + x) * 2.0 + 1.0, (c.Id.Y * n + y) * 2.0 + 1.0, (c.Id.Z * n + z) * 2.0 + 1.0);
        }

        private static void Absorb(Job j, SiteMap map, ulong nowMs)
        {
            Jobs++;
            LastJobMs = j.Ms;
            CellsScanned += j.Cells.Count;
            foreach (CellTask c in j.Cells)
            {
                if (c.Error != null) continue;
                Cells[c.Id] = c;
                _changed = true;
                foreach (OreRec o in c.Ores)
                {
                    ulong key = S8aOutcrops.Key(c.Id.Body, c.Id.X, c.Id.Y, c.Id.Z, o.Ore);
                    double exposed = o.ExposedVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM, volume = o.OreVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM;
                    double local = o.LocalVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM;
                    Memory m;
                    if (!Mem.TryGetValue(key, out m)) { Mem[key] = m = new Memory { FirstExposed = exposed, FirstVolume = volume, FirstLocal = local, FirstAnchorPacked = o.AnchorPacked }; }
                    else if (!m.Mined)
                    {
                        // a test run (S8a.NO.shrink: 64 m3 less was "not mined" under the 1 % rule, and the cavity's new walls grew the first
 // exposure, so the deposit grew 2.06 -> 2.54): one LOD 1 voxel less is mined, and the first sighting stays as it was
                        if (S8aOutcrops.MinedSince(volume, m.FirstVolume, local, m.FirstLocal)) m.Mined = true;
                        else
                        {
                            m.FirstExposed = Math.Max(m.FirstExposed, exposed); m.FirstVolume = Math.Max(m.FirstVolume, volume); m.FirstLocal = Math.Max(m.FirstLocal, local);
                            if (m.FirstAnchorPacked < 0) m.FirstAnchorPacked = o.AnchorPacked;
                        }
                    }
                }
            }
            string row = S4Rows.Row("s8a.outcrops", "tick", (long)GameState.GameplayTick, "cells", (long)j.Cells.Count, "ms", j.Ms, "known", (long)Cells.Count,
                                    "records", (long)_records.Count, "errors", Errors, "excluded", Excluded, "writes", Writes, "s10", S10Counters());
            if (S10BoxesOn) Jsonl.WriteLineBuffered(row); else Jsonl.WriteLine(row); // S10 hook (round 3): no flush on the update thread
        }

        /// <summary>S10 (round 3, s10.mem): the sizes this scan keeps (dirty cells, boxes, consumed marks, known cells, first-sighting memory).</summary>
        public static void S10Sizes(out long dirty, out long boxes, out long consumed, out long cells, out long memory)
        {
            lock (Dirty) { dirty = Dirty.Count; boxes = Boxes.Count; consumed = Consumed.Count; }
            cells = Cells.Count;
            memory = Mem.Count;
        }

        /// <summary>S10 (round 3): the Dirty lock's worst hold (a RangeChanged handler) and worst wait (Plan) since the last row, the
 /// largest range in cells, the set and box sizes.</summary>
        private static Dictionary<string, object> S10Counters()
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (Dirty)
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "boxesOn", S10BoxesOn }, { "dirty", (long)Dirty.Count }, { "boxes", (long)Boxes.Count }, { "consumed", (long)Consumed.Count },
                    { "boxesMade", S10BoxesMade }, { "boxesMerged", S10BoxesMerged }, { "holdMsMax", Math.Round(_s10HoldMax * f, 3) },
                    { "waitMsMax", Math.Round(_s10WaitMax * f, 3) }, { "rangeCellsMax", _s10RangeMax },
                    { "absorbMsMax", Math.Round(_s10AbsorbMax * f, 3) }, { "planMsMax", Math.Round(_s10PlanMax * f, 3) }, { "writeMsMax", Math.Round(_s10WriteMax * f, 3) },
                    { "fileMsMax", Math.Round(Interlocked.Exchange(ref _s10FileMax, 0) * f, 3) }, { "fileQueue", (long)FileQueue.Count },
                };
                _s10HoldMax = _s10WaitMax = _s10RangeMax = 0;
                _s10AbsorbMax = _s10PlanMax = _s10WriteMax = 0;
                return d;
            }
        }

        private static void Write(SiteMap map, ulong storeKey, uint chartSeq, Vec3 camSe)
        {
            var recs = new List<S8aOutcrop>();
            var keyCell = new Dictionary<ulong, CellId>();
            IList<S8aCraterDeposit> own = null;
            try { own = TerrainTiles.CraterOres != null ? TerrainTiles.CraterOres.Deposits : null; } catch (Exception) { }
            foreach (var kv in Cells)
            {
                CellTask c = kv.Value;
                if (c.Ores == null) continue;
                foreach (OreRec o in c.Ores)
                {
                    if (o.ExposedVoxels == 0) continue;
                    ulong key = S8aOutcrops.Key(c.Id.Body, c.Id.X, c.Id.Y, c.Id.Z, o.Ore);
                    Memory m;
                    if (!Mem.TryGetValue(key, out m)) continue;
                    double exposed = o.ExposedVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM, volume = o.OreVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM;
                    double local = o.LocalVoxels * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM * S8aOutcrops.VoxelM;
                    if (!S8aOutcrops.Shown(exposed, local, m.FirstLocal)) continue;
                    Vec3 aw = new Vec3(o.Anchor.X, o.Anchor.Y, o.Anchor.Z);
                    Vec3 sn = map.SeToSn(aw);
                    if (S8aMutants.M23) sn = new Vec3(sn.X + 8.0, sn.Y, sn.Z);              // S8a.M23: the anchors 8 m off (NO.agree red)
                    Vec3 ns = map.DirSeToSn(aw, new Vec3(o.Normal.X, o.Normal.Y, o.Normal.Z));
                    Vec3 det = map.SeToSn(new Vec3(o.Det.X, o.Det.Y, o.Det.Z));
                    string ore = S8aOutcrops.Ores[o.Ore];
                    if (own != null)
                    {
                        bool mine = false;
                        foreach (S8aCraterDeposit d in own)
                            if (string.Equals(S8aCraterOres.OreOfTechType(d.TechType), ore, StringComparison.Ordinal) && S8aOutcrops.HorizontalM(sn.X, sn.Z, d.X, d.Z) <= 8.0) { mine = true; break; }
                        if (mine) { Excluded++; continue; }
                    }
                    keyCell[key] = c.Id;
                    recs.Add(new S8aOutcrop
                    {
                        Key = key, Ore = ore, X = sn.X, Y = sn.Y, Z = sn.Z, NX = ns.X, NY = ns.Y, NZ = ns.Z, ExposedM2 = exposed, VolumeM3 = volume,
                        FirstExposedM2 = m.FirstExposed, FirstVolumeM3 = m.FirstVolume, DetX = det.X, DetY = det.Y, DetZ = det.Z, Drilled = m.Mined,
                        Zone = c.Body is MyPlanet ? "planet" : "tile", Scale = S8aOutcrops.ScaleOf(m.FirstExposed, local, m.FirstLocal), LocalM3 = local, FirstLocalM3 = m.FirstLocal,
                    });
                }
            }
            recs = S8aOutcrops.Merge(recs);
            recs.Sort((x, y) => x.Key.CompareTo(y.Key));
            _records = recs;
            _keyCell = keyCell;
            _changed = false;
            _chartSeqWritten = chartSeq;
            _writeSeq++;
            string path = S8aOutcrops.PathOf(S8aStore.DefaultRoot(), storeKey);
            string text;
            try { text = S8aOutcrops.Format(_writeSeq, chartSeq, S8aChartManager.ChartKey, recs); }
            catch (Exception e) { Errors++; if (Errors <= 3) DeLog.Info("DE.S8a outcrops not written: " + e.GetType().Name + ": " + e.Message); return; }
            if (S10BoxesOn) QueueFile(path, text); // S10 hook (round 3): the file I/O off the update thread, FIFO, the same text
            else WriteFile(path, text);
        }

        // ------------------------------------------------------------------------------------------ the command

        public static Dictionary<string, object> Command(IDictionary<string, object> args, Vec3 camSe, bool haveCam)
        {
            args = args ?? new Dictionary<string, object>();
            SiteMap map = SiteBinding.Map;
            double radius = MiniJson.GetNumber(args, "radius") ?? S8aOutcrops.SpawnRadiusM;
            if (MiniJson.GetBool(args, "probe") == true)
            {
                if (map == null || !haveCam) return Fail("de.s8a outcrops probe: no site or no camera");
                Vec3 cs = map.SeToSn(camSe);
                var list = new List<object>();
                var sb = new StringBuilder();
                foreach (S8aOutcrop r in _records)
                {
                    double dx = r.X - cs.X, dy = r.Y - cs.Y, dz = r.Z - cs.Z;
                    if (dx * dx + dy * dy + dz * dz > radius * radius) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        { "key", r.Key.ToString("x16", CultureInfo.InvariantCulture) }, { "ore", r.Ore }, { "x", r.X }, { "y", r.Y }, { "z", r.Z }, { "exposedM2", r.ExposedM2 },
                        { "volumeM3", r.VolumeM3 }, { "firstVolumeM3", r.FirstVolumeM3 }, { "scale", r.Scale }, { "drilled", r.Drilled }, { "detX", r.DetX }, { "detY", r.DetY },
                        { "detZ", r.DetZ }, { "zone", r.Zone }, { "nx", r.NX }, { "ny", r.NY }, { "nz", r.NZ }, { "localM3", r.LocalM3 }, { "firstLocalM3", r.FirstLocalM3 },
                    });
                    CellId ci;
                    if (_keyCell.TryGetValue(r.Key, out ci)) ((Dictionary<string, object>)list[list.Count - 1])["cell"] = ci.Body + ":" + ci.X + "," + ci.Y + "," + ci.Z;
                    sb.Append(sb.Length > 0 ? ";" : "").Append(r.Key.ToString("x16", CultureInfo.InvariantCulture)).Append(':').Append(r.Ore).Append(':')
                      .Append(r.X.ToString("0.0", CultureInfo.InvariantCulture)).Append(':').Append(r.Y.ToString("0.0", CultureInfo.InvariantCulture)).Append(':')
                      .Append(r.Z.ToString("0.0", CultureInfo.InvariantCulture)).Append(':').Append(r.Scale.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
                      .Append(r.VolumeM3.ToString("0", CultureInfo.InvariantCulture)).Append(':').Append(r.Drilled ? '1' : '0');
                    CellId cc;
                    if (_keyCell.TryGetValue(r.Key, out cc)) sb.Append(':').Append(cc.Body).Append(':').Append(cc.X).Append(',').Append(cc.Y).Append(',').Append(cc.Z);
                }
                Jsonl.WriteLine(S4Rows.Row("s8a.outcrops.probe", "tick", (long)GameState.GameplayTick, "camX", cs.X, "camY", cs.Y, "camZ", cs.Z, "radius", radius,
                                           "records", (long)list.Count, "list", sb.ToString(), "label", MiniJson.GetString(args, "label") ?? ""));
                return new Dictionary<string, object> { { "ok", true }, { "records", list }, { "cam", new object[] { cs.X, cs.Y, cs.Z } }, { "line", "de.s8a outcrops probe: " + list.Count + " records within " + radius + " m" } };
            }
            if (MiniJson.GetBool(args, "detect") == true)
            {
                if (map == null || !haveCam) return Fail("de.s8a outcrops detect: no site or no camera");
                string why = StartDetect(camSe, radius, map, MiniJson.GetString(args, "label") ?? "");
                return why == null ? new Dictionary<string, object> { { "ok", true }, { "started", true }, { "line", "de.s8a outcrops detect: started" } } : Fail("de.s8a outcrops detect: " + why);
            }
            var st = new Dictionary<string, object>
            {
                { "ok", true }, { "on", On }, { "cells", Cells.Count }, { "records", _records.Count }, { "jobs", Jobs }, { "cellsScanned", CellsScanned }, { "writes", Writes },
                { "errors", Errors }, { "excluded", Excluded }, { "lastJobMs", LastJobMs }, { "writeSeq", _writeSeq }, { "detectRuns", _detRuns },
            };
            S8aCraterOreRule rule = TerrainTiles.CraterOres;
            st["craterCubeRule"] = rule != null ? (object)rule.CubeRule : null;
            st["craterDepositsPerKm2"] = rule != null ? (object)rule.DepositsPerKm2 : null;
            st["craterPockets"] = rule != null ? (object)rule.DepositCount : null;
            st["craterCapped"] = rule != null ? (object)rule.Capped : null;
            st["planetRules"] = S8aHost.PlanetRules.ToString();
            lock (DetLock)
            {
                st["detectPending"] = _detPending;
                st["detect"] = _det != null && _detPending == 0 ? (object)new List<object>(_det.ToArray()) : null;
            }
            st["line"] = "de.s8a outcrops: " + _records.Count + " records, " + Cells.Count + " cells, " + Jobs + " jobs";
            return st;
        }

        /// <summary>SE's own detector over the cells within radius of the camera, per voxel body (MyDepositQuery.Start by reflection).</summary>
        private static string StartDetect(Vec3 camSe, double radius, SiteMap map, string label)
        {
            Type t = typeof(MyEntityOreDeposit).Assembly.GetType("Sandbox.Game.Entities.Cube.MyDepositQuery");
            MethodInfo start = t != null ? t.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            if (start == null) return "MyDepositQuery.Start not found";
            var bodies = new List<MyVoxelBase>();
            var cam = new Vector3D(camSe.X, camSe.Y, camSe.Z);
            var sphere = new BoundingSphereD(cam, radius + 32.0);
            try { MyGamePruningStructure.GetAllVoxelMapsInSphere(ref sphere, bodies); } catch (Exception) { }
            MyPlanet planet = SiteBinding.Planet;
            if (planet != null && !bodies.Contains(planet)) bodies.Add(planet);
            lock (DetLock) { _det = new List<Dictionary<string, object>>(); _detPending = 0; }
            _detRuns++;
            foreach (MyVoxelBase b in bodies)
            {
                var bst = b != null && !b.Closed ? b.Storage as MyStorageBase : null;
                if (bst == null) continue;
                Vector3D local = cam - b.PositionLeftBottomCorner;
                Vector3I size = bst.Size;
                var min = new Vector3I(Math.Max(0, (int)Math.Floor((local.X - radius) / 32.0)), Math.Max(0, (int)Math.Floor((local.Y - radius) / 32.0)), Math.Max(0, (int)Math.Floor((local.Z - radius) / 32.0)));
                var max = new Vector3I(Math.Min(size.X / 32 - 1, (int)Math.Floor((local.X + radius) / 32.0)), Math.Min(size.Y / 32 - 1, (int)Math.Floor((local.Y + radius) / 32.0)),
                                       Math.Min(size.Z / 32 - 1, (int)Math.Floor((local.Z + radius) / 32.0)));
                if (min.X > max.X || min.Y > max.Y || min.Z > max.Z) continue;
                MyVoxelBase body = b;
                Action<List<MyEntityOreDeposit>, List<Vector3I>> cb = (deps, empty) =>
                {
                    try
                    {
                        Vector3D refPos = body.PositionComp.GetPosition() - (Vector3D)body.StorageMin;
                        lock (DetLock)
                        {
                            if (deps != null)
                                foreach (MyEntityOreDeposit d in deps)
                                    foreach (MyEntityOreDeposit.Data m in d.Materials)
                                    {
                                        string sub = m.Material != null ? m.Material.Id.SubtypeName : null;
                                        if (S8aOutcrops.OreIndex(sub) < 0) continue;
                                        Vector3D w = refPos + m.AverageLocalPosition;
                                        Vec3 sn = map.SeToSn(new Vec3(w.X, w.Y, w.Z));
                                        _det.Add(new Dictionary<string, object>
                                        {
                                            { "ore", sub }, { "x", sn.X }, { "y", sn.Y }, { "z", sn.Z }, { "body", body.EntityId },
                                            { "cell", new object[] { d.CellCoord.X, d.CellCoord.Y, d.CellCoord.Z } },
                                        });
                                    }
                            _detPending--;
                            if (_detPending == 0) DetRow(label);
                        }
                    }
                    catch (Exception e) { Errors++; DeLog.Info("DE.S8a outcrops detect callback: " + e.GetType().Name + ": " + e.Message); }
                };
                lock (DetLock) _detPending++;
                try { start.Invoke(null, new object[] { min, max, 0x5E8A0E5L, body, cb }); }
                catch (Exception e) { lock (DetLock) _detPending--; return "MyDepositQuery.Start: " + (e.InnerException ?? e).Message; }
            }
            lock (DetLock) if (_detPending == 0) DetRow(label);
            return null;
        }

        private static void DetRow(string label)
        {
            var sb = new StringBuilder();
            foreach (Dictionary<string, object> d in _det)
            {
                var cell = (object[])d["cell"];
                sb.Append(sb.Length > 0 ? ";" : "").Append((string)d["ore"]).Append(':').Append(((double)d["x"]).ToString("0.0", CultureInfo.InvariantCulture)).Append(':')
                  .Append(((double)d["y"]).ToString("0.0", CultureInfo.InvariantCulture)).Append(':').Append(((double)d["z"]).ToString("0.0", CultureInfo.InvariantCulture)).Append(':')
                  .Append((long)d["body"]).Append(':').Append(cell[0]).Append(',').Append(cell[1]).Append(',').Append(cell[2]);
            }
            Jsonl.WriteLine(S4Rows.Row("s8a.oredetect", "tick", (long)GameState.GameplayTick, "deposits", (long)_det.Count, "list", sb.ToString(), "label", label));
        }

        private static Dictionary<string, object> Fail(string error)
        {
            DeLog.Info("DE.S8a " + error);
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", error }, { "line", error } };
        }
    }
}
