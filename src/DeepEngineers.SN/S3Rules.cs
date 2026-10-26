using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // S3 Subnautica side, Unity-free part 4 of 4: the Frame/Entity pose math against WireMath, the main-thread upload
 // scheduler (≤ 2 ms per frame), the mirror worker thread, the lab plan (capture poses P1–P4, the reference Seamoth
 // spot), the capture list parser, the S3 options and the S3 mutant set. ROADMAP S3; CONTRACT §2.3, §2.5;
 // ARCHITECTURE §5.4, §5.8, §5.13.

 /// <summary>
 /// The Unity hierarchy of a mirrored entity as plain math (CONTRACT §2.5): Frame (position o, rotation identity,
 /// scale (s, 1, s)) → Entity (rotation q, scale (k, k, k)) → Unity-local data u = Mirror(SE-local l). The renderer
 /// matrix is T(o)·S·R(q)·k, sheared when the hull tilts: <c>world = o + S·(q·(k·u))</c> = WireMath.Apply(o, q, s, k·l).
 /// </summary>
    internal static class MirrorPoseMath
    {
        /// <summary>World point of a Unity-local point (already mirrored).</summary>
        public static Vec3 ApplyUnityLocal(EntityPose p, Vec3 u)
        {
            Vec3 r = p.Q.Rotate(u * p.K);
            return new Vec3(p.Ox + p.S * r.X, p.Oy + r.Y, p.Oz + p.S * r.Z);
        }

        /// <summary>World point of an SE-local point: WireMath.Apply(o, q, s, k·l) (the shared C# copy of the contract).</summary>
        public static Vec3 ApplySeLocal(EntityPose p, Vec3 l)
        {
            return WireMath.Apply(p.O, p.Q, p.S, l * p.K);
        }

        /// <summary>
 /// The Frame·Entity matrix in doubles, column-major 3×4: m[0..8] the linear part (columns), m[9..11] the
 /// translation. Built as Unity composes it: diag(s, 1, s) · R(q) · k, then translated by o.
 /// </summary>
        public static double[] HierarchyMatrix(EntityPose p)
        {
            Quat q = p.Q;
            Vec3 cx = q.Rotate(Vec3.UnitX) * p.K, cy = q.Rotate(Vec3.UnitY) * p.K, cz = q.Rotate(Vec3.UnitZ) * p.K;
            return new[]
            {
                cx.X * p.S, cx.Y, cx.Z * p.S,
                cy.X * p.S, cy.Y, cy.Z * p.S,
                cz.X * p.S, cz.Y, cz.Z * p.S,
                p.Ox, p.Oy, p.Oz,
            };
        }

        public static Vec3 Transform(double[] m, Vec3 v)
        {
            return new Vec3(m[0] * v.X + m[3] * v.Y + m[6] * v.Z + m[9], m[1] * v.X + m[4] * v.Y + m[7] * v.Z + m[10], m[2] * v.X + m[5] * v.Y + m[8] * v.Z + m[11]);
        }

        /// <summary>The 8 corners of an AABB, corner i = (i&amp;1 ? max.x: min.x, i&amp;2 ? max.y: min.y, i&amp;4 ? max.z: min.z).</summary>
        public static Vec3[] Corners(Vec3 min, Vec3 max)
        {
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++) c[i] = new Vec3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
            return c;
        }

        /// <summary>Largest distance between measured world points and the emulated Apply of their Unity-local points.</summary>
        public static double MaxDeviation(Vec3[] actualWorld, Vec3[] unityLocal, EntityPose p)
        {
            double worst = 0;
            for (int i = 0; i < actualWorld.Length; i++)
            {
                double d = (actualWorld[i] - ApplyUnityLocal(p, unityLocal[i])).Length;
                if (d > worst) worst = d;
            }
            return worst;
        }

        /// <summary>A rotation of <paramref name="deg"/> degrees about <paramref name="axis"/> (Unity AngleAxis sense, w ≥ 0 not enforced).</summary>
        public static Quat AngleAxis(double deg, Vec3 axis)
        {
            Vec3 a = axis.Normalized();
            double h = deg * Math.PI / 360.0;
            double sn = Math.Sin(h);
            return new Quat(a.X * sn, a.Y * sn, a.Z * sn, Math.Cos(h));
        }
    }

    internal enum UploadKind { Mesh = 0, Texture = 1, Compress = 2 }

    /// <summary>One main-thread upload step. <see cref="Run"/> returns true when the job is finished (false: run again next frame).</summary>
    internal sealed class UploadJob
    {
        public UploadKind Kind;
        public string Name;
        public Func<bool> Run;
    }

    /// <summary>
 /// The main-thread upload budget (ARCHITECTURE §5.13: ≤ 2 ms per frame for mesh uploads): meshes first, then
 /// textures, FIFO within a kind; the first job of a frame always runs (a single upload larger than the budget is
 /// counted, never starved); later jobs start only while the frame's elapsed time is under the budget.
 /// <c>Texture2D.Compress</c> jobs run at most once every <see cref="CompressEveryFrames"/> frames and only as the
 /// first job of a frame. A job that throws is dropped and counted. Unity-free (clock injected).
 /// </summary>
    internal sealed class UploadScheduler
    {
        private readonly Queue<UploadJob>[] _q = { new Queue<UploadJob>(), new Queue<UploadJob>(), new Queue<UploadJob>() };
        //: an edited cell's upload runs before the mesh and texture queues (the streaming backlog)
        private readonly Queue<UploadJob> _urgent = new Queue<UploadJob>();
        public long UrgentJobs;
        private readonly Func<long> _now;
        private readonly double _ticksPerMs;
        private long _lastCompressFrame = long.MinValue / 2;

        public double BudgetMs = 2.0;
        public int CompressEveryFrames = 30;
        public long Frames, Jobs, OverBudgetFrames, Failures, CompressRuns;
        public double MaxFrameMs, LastFrameMs;
        public string LastError;

        public UploadScheduler(Func<long> now, long ticksPerSecond)
        {
            _now = now;
            _ticksPerMs = ticksPerSecond / 1000.0;
        }

        public int Pending { get { return _urgent.Count + _q[0].Count + _q[1].Count + _q[2].Count; } }
        public int PendingOf(UploadKind k) { return _q[(int)k].Count; }

        public void Enqueue(UploadJob j)
        {
            if (j == null || j.Run == null) throw new ArgumentNullException("j");
            _q[(int)j.Kind].Enqueue(j);
        }

        /// <summary>Like <see cref="Enqueue"/>, run before every other queue.</summary>
        public void EnqueueUrgent(UploadJob j)
        {
            if (j == null || j.Run == null) throw new ArgumentNullException("j");
            _urgent.Enqueue(j);
            UrgentJobs++;
        }

        public void Clear()
        {
            _urgent.Clear();
            foreach (Queue<UploadJob> q in _q) q.Clear();
        }

        private double Elapsed(long start) { return (_now() - start) / _ticksPerMs; }

        /// <summary>Runs this frame's share; returns the number of job steps run.</summary>
        public int RunFrame(long frame)
        {
            long start = _now();
            int ran = 0;
            while (_urgent.Count > 0)
            {
                if (ran > 0 && Elapsed(start) >= BudgetMs) goto Done;
                UploadJob uj = _urgent.Peek();
                bool ufinished = Step(uj);
                ran++;
                if (ufinished) _urgent.Dequeue();
                else break;
            }
            for (int k = 0; k < 2; k++)
            {
                Queue<UploadJob> q = _q[k];
                while (q.Count > 0)
                {
                    if (ran > 0 && Elapsed(start) >= BudgetMs) goto Done;
                    UploadJob j = q.Peek();
                    bool finished = Step(j);
                    ran++;
                    if (finished) q.Dequeue();
                    else break; // a multi-step job continues next frame
                }
            }
            if (ran == 0 && _q[2].Count > 0 && frame - _lastCompressFrame >= Math.Max(1, CompressEveryFrames))
            {
                UploadJob j = _q[2].Peek();
                if (Step(j)) _q[2].Dequeue();
                _lastCompressFrame = frame;
                CompressRuns++;
                ran++;
            }
        Done:
            if (ran > 0)
            {
                Frames++;
                LastFrameMs = Elapsed(start);
                if (LastFrameMs > MaxFrameMs) MaxFrameMs = LastFrameMs;
                if (LastFrameMs > BudgetMs) OverBudgetFrames++;
            }
            return ran;
        }

        private bool Step(UploadJob j)
        {
            Jobs++;
            try
            {
                return j.Run();
            }
            catch (Exception e)
            {
                Failures++;
                LastError = j.Name + ": " + e.GetType().Name + ": " + e.Message;
                return true;
            }
        }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "pending=" + _q[0].Count + "/" + _q[1].Count + "/" + _q[2].Count + " jobs=" + Jobs + " frames=" + Frames + " overBudget=" + OverBudgetFrames +
                   " maxMs=" + MaxFrameMs.ToString("0.###", ci) + " budgetMs=" + BudgetMs.ToString("0.##", ci) + " failures=" + Failures +
                   (LastError != null ? " lastError=" + LastError : "");
        }
    }

    /// <summary>A failed worker job (the exception is reported on the main thread).</summary>
    internal sealed class WorkerFailure
    {
        public string Name;
        public Exception Error;
    }

    /// <summary>
 /// The mirror's worker thread (ARCHITECTURE §3.2: "SN worker threads build vertex arrays from copies, never from the
 /// section"): model parsing and preparation, cell builds, DDS reads and texture repacks. Jobs are closures over
 /// immutable copies; results come back through a concurrent queue drained on the main thread. Never touches Unity.
 /// </summary>
    internal sealed class MirrorWorker : IDisposable
    {
        private readonly Queue<KeyValuePair<string, Func<object>>> _jobs = new Queue<KeyValuePair<string, Func<object>>>();

        // preview mesh go in front of the streaming backlog (taken before any job of the ordinary queue)
        private readonly Queue<KeyValuePair<string, Func<object>>> _urgent = new Queue<KeyValuePair<string, Func<object>>>();
        public long UrgentPosted;
        private readonly ConcurrentQueue<object> _results = new ConcurrentQueue<object>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _stop;
        private int _queued;

        public long Done, Failed;
        public int Queued { get { return Volatile.Read(ref _queued); } }

        public void Post(string name, Func<object> job)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (_stop) return;
            lock (_jobs) _jobs.Enqueue(new KeyValuePair<string, Func<object>>(name, job));
            Interlocked.Increment(ref _queued);
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "DeepEngineers.MirrorWorker", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            _wake.Set();
        }

        /// <summary>Like <see cref="Post"/>, taken before every ordinary job (an edit the player is looking at).</summary>
        public void PostUrgent(string name, Func<object> job)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (_stop) return;
            lock (_jobs) _urgent.Enqueue(new KeyValuePair<string, Func<object>>(name, job));
            Interlocked.Increment(ref _queued);
            Interlocked.Increment(ref UrgentPosted);
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "DeepEngineers.MirrorWorker", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
            _wake.Set();
        }

        public bool TryTake(out object result)
        {
            return _results.TryDequeue(out result);
        }

        private void Run()
        {
            while (!_stop)
            {
                KeyValuePair<string, Func<object>> job;
                bool have;
                lock (_jobs)
                {
                    if (_urgent.Count > 0) { have = true; job = _urgent.Dequeue(); }
                    else
                    {
                        have = _jobs.Count > 0;
                        job = have ? _jobs.Dequeue() : default(KeyValuePair<string, Func<object>>);
                    }
                }
                if (!have)
                {
                    _wake.WaitOne(250);
                    continue;
                }
                object r;
                try
                {
                    r = job.Value();
                    Interlocked.Increment(ref Done);
                }
                catch (Exception e)
                {
                    r = new WorkerFailure { Name = job.Key, Error = e };
                    Interlocked.Increment(ref Failed);
                }
                Interlocked.Decrement(ref _queued);
                if (r != null && !_stop) _results.Enqueue(r);
            }
        }

        /// <summary>Drops queued jobs and pending results (new SE generation, teardown). A job in progress finishes and is discarded by its tag check.</summary>
        public void Clear()
        {
            lock (_jobs)
            {
                Interlocked.Add(ref _queued, -_jobs.Count);
                _jobs.Clear();
            }
            object o;
            while (_results.TryDequeue(out o)) { }
        }

        /// <summary>
 /// Stops the worker without waiting for it: a job in progress finishes on the background thread and its result is
 /// discarded (<c>_stop</c>); the thread is a background thread, so it never holds the process.
 /// </summary>
        public void Dispose()
        {
            _stop = true;
            try { _wake.Set(); } catch (Exception) { }
            lock (_jobs)
            {
                Interlocked.Add(ref _queued, -_jobs.Count);
                _jobs.Clear();
            }
            _thread = null;
        }

        /// <summary>True once <see cref="Dispose"/> ran (no new job is accepted).</summary>
        public bool Stopped { get { return _stop; } }
    }

    /// <summary>
 /// The lab plan (ROADMAP S3 "Lab scene command"; probe-ship-render §7.2): the block centre B at the seabed, the lab's
 /// heading ψ (forward F = (sin ψ, 0, cos ψ), right R = (cos ψ, 0, −sin ψ) in SN world, Unity left-handed), the
 /// reference Seamoth 6 m to the right of B, M the midpoint of B and the Seamoth. Capture poses (SN world):
 /// P1 front 6 m (camera at M − 6F, 1 m above B, looking at M, so the block and the Seamoth are framed symmetrically);
 /// P2 side 6 m at 0.3 m above the seabed (camera at B − 6R, looking at B: the buried half must be hidden by the
 /// terrain); P3 10 m above M looking down (F up on screen); P4 front 40 m (as P1 at 40 m: fog).
 /// </summary>
    internal sealed class LabPlan
    {
        public const double SeamothSideM = 6.0;
        public const double EyeAboveBlockM = 1.0;
        public const double P2AboveSeabedM = 0.3;
        public const double P3AboveM = 10.0;

        public Vec3 B, M, SeamothSpot, F, R;
        public double SeabedY, HeadingDeg, SideM;

        public static LabPlan Make(Vec3 blockCentre, double seabedY, double headingDeg, double sideM)
        {
            double psi = headingDeg * Math.PI / 180.0;
            var p = new LabPlan
            {
                B = blockCentre, SeabedY = seabedY, HeadingDeg = headingDeg, SideM = sideM,
                F = new Vec3(Math.Sin(psi), 0, Math.Cos(psi)),
                R = new Vec3(Math.Cos(psi), 0, -Math.Sin(psi)),
            };
            p.SeamothSpot = blockCentre + p.R * sideM;
            p.M = (blockCentre + p.SeamothSpot) * 0.5;
            return p;
        }

        public static readonly string[] PoseNames = { "P1", "P2", "P3", "P4" };

        /// <summary>The camera pose for a capture name (P1..P4; case-insensitive); false for an unknown name.</summary>
        public bool TryPose(string name, out Vec3 pos, out Quat rot)
        {
            pos = Vec3.Zero;
            rot = Quat.Identity;
            if (name == null) return false;
            string n = name.ToUpperInvariant();
            var up = Vec3.UnitY;
            switch (n)
            {
                case "P1":
                case "P4":
                {
                    double d = n == "P1" ? 6.0 : 40.0;
                    Vec3 c = M - F * d;
                    pos = new Vec3(c.X, B.Y + EyeAboveBlockM, c.Z);
                    Vec3 target = new Vec3(M.X, B.Y + 0.5, M.Z);
                    rot = Quat.LookRotation(target - pos, up);
                    return true;
                }
                case "P2":
                {
                    Vec3 c = B - R * 6.0;
                    pos = new Vec3(c.X, SeabedY + P2AboveSeabedM, c.Z);
                    rot = Quat.LookRotation(B - pos, up);
                    return true;
                }
                case "P3":
                    pos = new Vec3(M.X, B.Y + P3AboveM, M.Z);
                    rot = Quat.LookRotation(new Vec3(0, -1, 0), F);
                    return true;
            }
            return false;
        }

        /// <summary>Yaw (degrees, 0 = +Z north, 90 = +X east) and pitch (degrees, up positive) of a pose, for SE's de.teleport.</summary>
        public static void YawPitch(Quat q, out double yaw, out double pitch)
        {
            Vec3 f = q.Rotate(Vec3.UnitZ);
            yaw = Math.Atan2(f.X, f.Z) * 180.0 / Math.PI;
            pitch = Math.Atan2(f.Y, Math.Sqrt(f.X * f.X + f.Z * f.Z)) * 180.0 / Math.PI;
        }
    }

    /// <summary>One capture of the lab list.</summary>
    internal struct CaptureStep
    {
        /// <summary>P1..P4.</summary>
        public string Pose;
        /// <summary>File and sidecar label (P1, P3b, P3_017...).</summary>
        public string Label;
        /// <summary>True: the camera is already there (no move, no settle frames); at least <see cref="MinGapS"/> after the previous capture.</summary>
        public bool Repeat;
        public double MinGapS;
    }

    /// <summary>
 /// The lab's capture list: "P1".."P4" (move, settle N frames, capture); "P3+1s" (the same pose again at least 1 s
 /// after the previous capture: S3.C10's two P3 captures one second apart); "P3*60" (60 back-to-back captures at
 /// P3: S3.C8's temporal standard deviation). Accepts a JSON list or a comma-separated string.
 /// </summary>
    internal static class CaptureList
    {
        public const int MaxBurst = 240;

        public static bool TryParse(object v, out List<CaptureStep> steps, out string error)
        {
            steps = new List<CaptureStep>();
            error = null;
            var items = new List<string>();
            var list = v as List<object>;
            var s = v as string;
            if (list != null) foreach (object o in list) items.Add(o as string ?? Convert.ToString(o, CultureInfo.InvariantCulture));
            else if (s != null) foreach (string part in s.Split(',', ';', ' ')) { if (part.Trim().Length > 0) items.Add(part.Trim()); }
            else if (v == null) items.AddRange(LabPlan.PoseNames);
            else { error = "captures: a list or a comma-separated string"; return false; }
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string last = null;
            foreach (string raw in items)
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                int plus = t.IndexOf('+');
                int star = t.IndexOf('*');
                string pose = t;
                double gap = 0;
                int burst = 1;
                bool repeat = false;
                if (plus > 0)
                {
                    pose = t.Substring(0, plus);
                    string g = t.Substring(plus + 1).TrimEnd('s', 'S');
                    if (!double.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out gap) || gap < 0 || gap > 600)
                    {
                        error = "bad gap in '" + t + "' (e.g. P3+1s)";
                        return false;
                    }
                    repeat = true;
                }
                else if (star > 0)
                {
                    pose = t.Substring(0, star);
                    if (!int.TryParse(t.Substring(star + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out burst) || burst < 1 || burst > MaxBurst)
                    {
                        error = "bad burst in '" + t + "' (1.." + MaxBurst + ")";
                        return false;
                    }
                }
                pose = pose.ToUpperInvariant();
                if (Array.IndexOf(LabPlan.PoseNames, pose) < 0) { error = "unknown pose '" + pose + "' (P1..P4)"; return false; }
                if (repeat && !string.Equals(last, pose, StringComparison.OrdinalIgnoreCase))
                {
                    error = "'" + t + "' repeats " + pose + " but the previous capture is " + (last ?? "none");
                    return false;
                }
                int count;
                seen.TryGetValue(pose, out count);
                for (int i = 0; i < burst; i++)
                {
                    string label;
                    if (burst > 1) label = pose + "_" + i.ToString("000", CultureInfo.InvariantCulture);
                    else if (count == 0) label = pose;
                    else label = pose + (char)('a' + Math.Min(25, count));
                    steps.Add(new CaptureStep { Pose = pose, Label = label, Repeat = repeat || (burst > 1 && i > 0), MinGapS = repeat ? gap : 0 });
                    count++;
                }
                seen[pose] = count;
                last = pose;
            }
            if (steps.Count == 0) { error = "no capture"; return false; }
            return true;
        }
    }

    /// <summary>Runtime options of the S3 mirror (de.s3 {action: "config"}); defaults are the ROADMAP's.</summary>
    internal sealed class S3Options
    {
        public UvRule Uv = UvRule.Exporter;
        public bool VFlip;
        /// <summary>
 /// _BumpMap G = 1 − Y. On by default: SE negates the normal map's y before using its stored bitangent
 /// (Content\Shaders\PixelUtils.hlsli, adjust_normalmap_precomputed_tangents), while Unity's binormal
 /// Cross(n′, t′)·w with w = sign(dot(Cross(n′, t′), b′)) is the mirrored stored bitangent itself; the same
 /// conclusion as Wire MeshConventions.SeNormalMapGreenSign. HYPOTHESIS until the S3.N1 "bump faces the sun" capture.
 /// </summary>
        public bool NormalFlipY = true;
        /// <summary>Largest texture side after skipping mips (ROADMAP: 1024 px).</summary>
        public int MaxTextureSide = 1024;
        /// <summary>0: derived maps stay RGBA32 (v1); n &gt; 0: Texture2D.Compress one texture per n frames.</summary>
        public int CompressEveryFrames;
        public double UploadBudgetMs = 2.0;
        /// <summary>CONTRACT §5.1: the consumer drains C3+C4 up to 4 ms or 48 MiB per frame.</summary>
        public double DrainBudgetMs = 4.0;
        public long DrainBudgetBytes = 48L << 20;
        /// <summary>The GPU conversion of _ng/_add (off: _MainTex only).</summary>
        public bool GpuConvert = true;
        public bool Shadows = true;

        private BuildOptions _built;

        /// <summary>
 /// The build options for the current options and mutants; the same (immutable once handed out) instance while
 /// nothing changed, so a frame without dirty cells allocates nothing.
 /// </summary>
        public BuildOptions Build(S3MutantSet m)
        {
            BuildOptions b = _built;
            if (b != null && b.Uv == Uv && b.VFlip == VFlip && b.MirrorZ == !m.M1 && b.SwapTris == !m.M2) return b;
            _built = b = new BuildOptions { Uv = Uv, VFlip = VFlip, MirrorZ = !m.M1, SwapTris = !m.M2 };
            return b;
        }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "uv=" + (Uv == UvRule.Exporter ? "exporter" : "renderer") + " vFlip=" + VFlip + " normalFlipY=" + NormalFlipY +
                   " maxTextureSide=" + MaxTextureSide + " compressEveryFrames=" + CompressEveryFrames + " uploadBudgetMs=" +
                   UploadBudgetMs.ToString("0.##", ci) + " drainBudgetMs=" + DrainBudgetMs.ToString("0.##", ci) + " gpuConvert=" + GpuConvert +
                   " shadows=" + Shadows;
        }

        /// <summary>
 /// Applies the keys present in <paramref name="a"/>. <paramref name="rebuildMeshes"/>: chunk UVs changed;
 /// <paramref name="reloadTextures"/>: the derived maps changed.
 /// </summary>
        public bool TryApply(Dictionary<string, object> a, out bool rebuildMeshes, out bool reloadTextures, out string error)
        {
            rebuildMeshes = false;
            reloadTextures = false;
            error = null;
            object v;
            bool b;
            double d;
            if (a.TryGetValue("uv", out v))
            {
                string s = v as string;
                if (s == "exporter") Uv = UvRule.Exporter;
                else if (s == "renderer") Uv = UvRule.Renderer;
                else { error = "uv: exporter|renderer"; return false; }
                rebuildMeshes = true;
            }
            if (a.TryGetValue("vFlip", out v)) { if (!S3Args.TryBool(v, out b)) { error = "vFlip: true|false"; return false; } VFlip = b; rebuildMeshes = true; }
            if (a.TryGetValue("normalFlipY", out v)) { if (!S3Args.TryBool(v, out b)) { error = "normalFlipY: true|false"; return false; } NormalFlipY = b; reloadTextures = true; }
            if (a.TryGetValue("gpuConvert", out v)) { if (!S3Args.TryBool(v, out b)) { error = "gpuConvert: true|false"; return false; } GpuConvert = b; reloadTextures = true; }
            if (a.TryGetValue("shadows", out v)) { if (!S3Args.TryBool(v, out b)) { error = "shadows: true|false"; return false; } Shadows = b; }
            if (a.TryGetValue("maxTextureSide", out v))
            {
                if (!S1Args.TryDouble(v, out d) || d < 16 || d > 8192) { error = "maxTextureSide: 16..8192"; return false; }
                MaxTextureSide = (int)d;
                reloadTextures = true;
            }
            if (a.TryGetValue("compressEveryFrames", out v))
            {
                if (!S1Args.TryDouble(v, out d) || d < 0 || d > 10000) { error = "compressEveryFrames: 0..10000"; return false; }
                CompressEveryFrames = (int)d;
                reloadTextures = true;
            }
            if (a.TryGetValue("uploadBudgetMs", out v)) { if (!S1Args.TryDouble(v, out d) || d <= 0 || d > 50) { error = "uploadBudgetMs: (0, 50]"; return false; } UploadBudgetMs = d; }
            if (a.TryGetValue("drainBudgetMs", out v)) { if (!S1Args.TryDouble(v, out d) || d <= 0 || d > 50) { error = "drainBudgetMs: (0, 50]"; return false; } DrainBudgetMs = d; }
            return true;
        }
    }

    /// <summary>The S3 seeded failures that are on (DevMutants, by ROADMAP id), as a comparable set.</summary>
    internal struct S3MutantSet : IEquatable<S3MutantSet>
    {
        public bool M1, M2, M3, M4, M5, M6, M7, M8;

        public static S3MutantSet Current()
        {
            return new S3MutantSet
            {
                M1 = DevMutants.IsOn(DevMutants.S3ZMirrorDropped),
                M2 = DevMutants.IsOn(DevMutants.S3TriSwapDropped),
                M3 = DevMutants.IsOn(DevMutants.S3NoSkyApplier),
                M4 = DevMutants.IsOn(DevMutants.S3PreviousSnapshot),
                M5 = DevMutants.IsOn(DevMutants.S3CaptureReused),
                M6 = DevMutants.IsOn(DevMutants.S3RootPreserved),
                M7 = DevMutants.IsOn(DevMutants.S3RigidShear),
                M8 = DevMutants.IsOn(DevMutants.S3NormalSrgbTarget),
            };
        }

        public bool Equals(S3MutantSet o)
        {
            return M1 == o.M1 && M2 == o.M2 && M3 == o.M3 && M4 == o.M4 && M5 == o.M5 && M6 == o.M6 && M7 == o.M7 && M8 == o.M8;
        }

        public override bool Equals(object obj) { return obj is S3MutantSet && Equals((S3MutantSet)obj); }
        public override int GetHashCode() { return (M1 ? 1 : 0) | (M2 ? 2 : 0) | (M3 ? 4 : 0) | (M4 ? 8 : 0) | (M5 ? 16 : 0) | (M6 ? 32 : 0) | (M7 ? 64 : 0) | (M8 ? 128 : 0); }

        public string Describe()
        {
            var parts = new List<string>();
            if (M1) parts.Add("S3.M1"); if (M2) parts.Add("S3.M2"); if (M3) parts.Add("S3.M3"); if (M4) parts.Add("S3.M4");
            if (M5) parts.Add("S3.M5"); if (M6) parts.Add("S3.M6"); if (M7) parts.Add("S3.M7"); if (M8) parts.Add("S3.M8");
            return parts.Count == 0 ? "none" : string.Join(",", parts.ToArray());
        }
    }

    /// <summary>
 /// C11 argument helpers of de.s3 / de.time: a JSON object, or the SE chat relay's {"argv": [...]}, lists or comma strings for vectors and capture lists.
 /// </summary>
    internal static class S3Args
    {
        public static Dictionary<string, object> ToDict(object args)
        {
            var dict = args as Dictionary<string, object>;
            object argv;
            if (dict != null && !(dict.Count == 1 && dict.TryGetValue("argv", out argv) && argv is List<object>)) return dict;
            var list = S1Args.Unwrap(args) as List<object>;
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            if (list == null) return r;
            foreach (object o in list)
            {
                string t = o as string;
                if (t == null) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) { if (!r.ContainsKey("action")) r["action"] = t; continue; }
                string k = t.Substring(0, eq), val = t.Substring(eq + 1);
                double d;
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r[k] = d;
                else if (val == "true" || val == "false") r[k] = val == "true";
                else r[k] = val;
            }
            return r;
        }

        public static bool TryBool(object v, out bool b)
        {
            b = false;
            if (v is bool) { b = (bool)v; return true; }
            if (v is double) { b = (double)v != 0; return true; }
            var s = v as string;
            if (s == null) return false;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "on" || s == "1" || s == "yes") { b = true; return true; }
            if (s == "false" || s == "off" || s == "0" || s == "no") { b = false; return true; }
            return false;
        }

        public static string Str(Dictionary<string, object> a, string key, string def)
        {
            object v;
            if (!a.TryGetValue(key, out v) || v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(Dictionary<string, object> a, string key, double def)
        {
            object v;
            double d;
            return a.TryGetValue(key, out v) && S1Args.TryDouble(v, out d) ? d : def;
        }

        public static bool Flag(Dictionary<string, object> a, string key, bool def)
        {
            object v;
            bool b;
            return a.TryGetValue(key, out v) && TryBool(v, out b) ? b : def;
        }

        /// <summary>
 /// A vector from [x, y, z], [x, z] (y NaN), {x, y, z} or "x,z" / "x,y,z"; false when absent or malformed.
 /// </summary>
        public static bool TryVec(object v, out double x, out double y, out double z)
        {
            x = y = z = double.NaN;
            var list = v as List<object>;
            var s = v as string;
            var dict = v as Dictionary<string, object>;
            var nums = new List<double>();
            if (list != null)
            {
                foreach (object o in list) { double d; if (!S1Args.TryDouble(o, out d)) return false; nums.Add(d); }
            }
            else if (s != null)
            {
                foreach (string p in s.Split(','))
                {
                    double d;
                    if (!double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
                    nums.Add(d);
                }
            }
            else if (dict != null)
            {
                object ox, oy, oz;
                if (!dict.TryGetValue("x", out ox) || !dict.TryGetValue("z", out oz) || !S1Args.TryDouble(ox, out x) || !S1Args.TryDouble(oz, out z)) return false;
                if (dict.TryGetValue("y", out oy) && !S1Args.TryDouble(oy, out y)) return false;
                return true;
            }
            else return false;
            if (nums.Count == 2) { x = nums[0]; z = nums[1]; return true; }
            if (nums.Count == 3) { x = nums[0]; y = nums[1]; z = nums[2]; return true; }
            return false;
        }
    }
}

namespace DeepEngineers.SN
{
    /// <summary>
 /// A small nested JSON writer for the S3 sidecars and structured log payloads (JsonObjectWriter is flat). Numbers in
 /// invariant "R" form, NaN/Infinity as null. Unity-free.
 /// </summary>
    internal sealed class JsonOut
    {
        private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(1024);
        private readonly System.Collections.Generic.Stack<bool> _first = new System.Collections.Generic.Stack<bool>();

        public JsonOut() { _sb.Append('{'); _first.Push(true); }

        private void Sep()
        {
            bool f = _first.Pop();
            if (!f) _sb.Append(',');
            _first.Push(false);
        }

        private JsonOut Key(string name)
        {
            Sep();
            if (name != null) { Json.AppendString(_sb, name); _sb.Append(':'); }
            return this;
        }

        public JsonOut Obj(string name) { Key(name); _sb.Append('{'); _first.Push(true); return this; }
        public JsonOut Arr(string name) { Key(name); _sb.Append('['); _first.Push(true); return this; }
        public JsonOut EndObj() { _first.Pop(); _sb.Append('}'); return this; }
        public JsonOut EndArr() { _first.Pop(); _sb.Append(']'); return this; }

        public JsonOut Str(string name, string v) { Key(name); Json.AppendString(_sb, v); return this; }
        public JsonOut Bool(string name, bool v) { Key(name); _sb.Append(v ? "true" : "false"); return this; }
        public JsonOut Num(string name, long v) { Key(name); _sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); return this; }
        public JsonOut Num(string name, ulong v) { Key(name); _sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); return this; }

        public JsonOut Num(string name, double v)
        {
            Key(name);
            if (double.IsNaN(v) || double.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return this;
        }

        public JsonOut Vec(string name, double x, double y, double z)
        {
            Arr(name); Num(null, x); Num(null, y); Num(null, z); return EndArr();
        }

        public JsonOut Vec(string name, Vec3 v) { return Vec(name, v.X, v.Y, v.Z); }

        public JsonOut Quat4(string name, double x, double y, double z, double w)
        {
            Arr(name); Num(null, x); Num(null, y); Num(null, z); Num(null, w); return EndArr();
        }

        /// <summary>The finished text (closes the root object; call once).</summary>
        public override string ToString()
        {
            return _sb.ToString() + "}";
        }
    }

    /// <summary>
 /// A procedural cube model in SE's MyModel.Triangles convention (SE-space Cross(p1 − p0, p2 − p0) outwards, UVs 0..1 per
 /// face, right-handed tangent frames): the shear test's mesh (S3.G1-E2) and the same shape the S3a fake producer sends.
 /// </summary>
    internal static class ProceduralCube
    {
        public static ModelData Model(ulong key, float half, ulong materialKey)
        {
            var normals = new[] { new Vec3(1, 0, 0), new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1), new Vec3(0, 0, -1) };
            var m = new ModelData
            {
                Key = key, VertexCount = 24, IndexCount = 36, SubmeshCount = 1, PatternScale = 1f,
                Pos = new float[72], Nrm = new float[72], Tan = new float[72], Bin = new float[72], Uv = new float[48], Indices = new int[36],
                Submeshes = new[] { new SubmeshInfo { MaterialKey = materialKey, TriStart = 0, TriCount = 12 } },
                BMinX = -half, BMinY = -half, BMinZ = -half, BMaxX = half, BMaxY = half, BMaxZ = half,
            };
            float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
            for (int f = 0; f < 6; f++)
            {
                Vec3 n = normals[f];
                Vec3 t = Math.Abs(n.Y) > 0.5 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                Vec3 b = Vec3.Cross(n, t);
                Vec3 c = n * half;
                var corners = new[] { c - t * half - b * half, c + t * half - b * half, c + t * half + b * half, c - t * half + b * half };
                for (int i = 0; i < 4; i++)
                {
                    int v = f * 4 + i;
                    m.Pos[v * 3] = (float)corners[i].X; m.Pos[v * 3 + 1] = (float)corners[i].Y; m.Pos[v * 3 + 2] = (float)corners[i].Z;
                    m.Nrm[v * 3] = (float)n.X; m.Nrm[v * 3 + 1] = (float)n.Y; m.Nrm[v * 3 + 2] = (float)n.Z;
                    m.Tan[v * 3] = (float)t.X; m.Tan[v * 3 + 1] = (float)t.Y; m.Tan[v * 3 + 2] = (float)t.Z;
                    m.Bin[v * 3] = (float)b.X; m.Bin[v * 3 + 1] = (float)b.Y; m.Bin[v * 3 + 2] = (float)b.Z;
                    m.Uv[v * 2] = uvs[i * 2]; m.Uv[v * 2 + 1] = uvs[i * 2 + 1];
                }
                int[] tri = { 0, 1, 2, 0, 2, 3 };
                for (int k = 0; k < 6; k++) m.Indices[f * 6 + k] = f * 4 + tri[k];
            }
            return m;
        }
    }
}
