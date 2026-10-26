using System;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// One C1 `SeFrame` snapshot as the camera driver uses it (CONTRACT §5.4 C1 fixed part; the bones are S5). SN-world
 /// positions (`camPos = F(Pcam)` exactly, `pelvisSn`), the Unity camera quaternion, the vertical FOV in degrees and
 /// the near plane. Unity-free (tested without the game).
 /// </summary>
    internal struct CamSnapshot
    {
        public uint Seq;
        public uint Flags;
        public ulong Tick;
        public long Qpc;
        public uint WorldEpoch;
        public double Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float VFovDeg, Near, Aspect;
        public uint CameraMode;
        public ulong CharacterId;
        public double PelX, PelY, PelZ;

        public bool Has(uint flag) { return (Flags & flag) != 0; }

        /// <summary>
 /// The ghost Player rule's input: the pelvis when a
 /// character is present, not seated, and the camera is that character's own view (C1 cameraMode first or third
 /// person); the camera otherwise: seated, spectator (cameraMode 4, also while a character stands elsewhere),
 /// cockpit, any other camera, or no character. In spectator the view, streaming, biome, music and creature AI
 /// must follow the camera, not a body left behind.
 /// </summary>
        public bool PlayerAtPelvis
        {
            get
            {
                return Has(SeFrameFlags.CharacterPresent) && !Has(SeFrameFlags.CharacterSeated) &&
                       (CameraMode == DeepEngineers.Wire.CameraMode.FirstPerson || CameraMode == DeepEngineers.Wire.CameraMode.ThirdPerson);
            }
        }

        public static CamSnapshot FromFrame(ref SeFrame f, uint seq)
        {
            var s = new CamSnapshot
            {
                Seq = seq,
                Flags = f.flags,
                Tick = f.tick,
                Qpc = f.qpcCapture,
                WorldEpoch = f.worldEpoch,
                Px = f.camPos.x, Py = f.camPos.y, Pz = f.camPos.z,
                Qx = f.camRot.x, Qy = f.camRot.y, Qz = f.camRot.z, Qw = f.camRot.w,
                VFovDeg = f.vFovDeg, Near = f.near, Aspect = f.aspect,
                CameraMode = f.cameraMode,
                CharacterId = f.characterId,
                PelX = f.pelvisSn.x, PelY = f.pelvisSn.y, PelZ = f.pelvisSn.z,
            };
            return s;
        }

        /// <summary>Null when the snapshot can drive the camera; otherwise why not.</summary>
        public string Invalid()
        {
            if (!Finite(Px) || !Finite(Py) || !Finite(Pz) || Math.Abs(Px) > 1e6 || Math.Abs(Py) > 1e6 || Math.Abs(Pz) > 1e6)
                return "camPos not finite or beyond 1e6 m";
            if (!Finite(Qx) || !Finite(Qy) || !Finite(Qz) || !Finite(Qw)) return "camRot not finite";
            double n = (double)Qx * Qx + (double)Qy * Qy + (double)Qz * Qz + (double)Qw * Qw;
            if (n < 0.25 || n > 4.0) return "camRot norm^2 " + n.ToString("0.###", CultureInfo.InvariantCulture);
            if (!Finite(VFovDeg) || VFovDeg <= 1f || VFovDeg >= 179f) return "vFovDeg " + VFovDeg.ToString("R", CultureInfo.InvariantCulture);
            if (Qpc <= 0) return "qpcCapture <= 0";
            return null;
        }

        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }

    internal enum PushResult
    {
        /// <summary>History was empty.</summary>
        First = 0,
        /// <summary>A newer tick appended.</summary>
        Pushed = 1,
        /// <summary>Same tick published again: the newest entry was replaced by the newer copy.</summary>
        Replaced = 2,
        /// <summary>Camera jump (> 20 m or > 45° from the previous snapshot) or the SE teleport flag: history restarts here.</summary>
        Snap = 3,
        /// <summary>World epoch changed: history restarts here.</summary>
        ResetEpoch = 4,
        /// <summary>Tick went backwards, or a newer tick carried an older QPC stamp: history restarts here.</summary>
        ResetClock = 5,
        /// <summary>Not used (inWorld flag clear: SE is not in a world).</summary>
        NotInWorld = 6,
        /// <summary>Not used (non-finite or out-of-range fields).</summary>
        Invalid = 7,
        /// <summary>Same tick with an older or equal QPC stamp: ignored.</summary>
        Ignored = 8,
    }

    /// <summary>
 /// The last <see cref="Capacity"/> C1 snapshots, oldest first (ARCHITECTURE §5.8: "keeps the last 4 snapshots"). A
 /// camera jump of more than 20 m or 45° between consecutive snapshots, or the SE teleport flag, restarts the history
 /// at the jump (CONTRACT §2.8: snap; interpolation never crosses a jump).
 /// </summary>
    internal sealed class CamHistory
    {
        public const int Capacity = 4;
        public const double JumpMetres = 20.0;
        public const double JumpDegrees = 45.0;

        private readonly CamSnapshot[] _s = new CamSnapshot[Capacity];

        public int Count { get; private set; }

        public CamSnapshot this[int i]
        {
            get
            {
                if ((uint)i >= (uint)Count) throw new IndexOutOfRangeException();
                return _s[i];
            }
        }

        public CamSnapshot Newest { get { return _s[Count - 1]; } }

        public void Clear() { Count = 0; }

        public PushResult Push(CamSnapshot s, out string why)
        {
            why = s.Invalid();
            if (why != null) return PushResult.Invalid;
            if (!s.Has(SeFrameFlags.InWorld)) { why = "inWorld flag clear"; return PushResult.NotInWorld; }
            if (Count == 0) { Restart(s); return PushResult.First; }

            CamSnapshot n = _s[Count - 1];
            if (s.WorldEpoch != n.WorldEpoch)
            {
                why = "worldEpoch " + n.WorldEpoch + " -> " + s.WorldEpoch;
                Restart(s);
                return PushResult.ResetEpoch;
            }
            if (s.Tick < n.Tick)
            {
                why = "tick " + n.Tick + " -> " + s.Tick;
                Restart(s);
                return PushResult.ResetClock;
            }
            if (s.Tick == n.Tick)
            {
                if (s.Qpc <= n.Qpc) { why = "same tick, not newer"; return PushResult.Ignored; }
                CamSnapshot before = Count >= 2 ? _s[Count - 2] : n;
                if (Count >= 2 && IsJump(before, s, out why)) { Restart(s); return PushResult.Snap; }
                _s[Count - 1] = s;
                return PushResult.Replaced;
            }
            if (s.Qpc <= n.Qpc)
            {
                why = "tick " + n.Tick + " -> " + s.Tick + " with qpc not increasing";
                Restart(s);
                return PushResult.ResetClock;
            }
            if (IsJump(n, s, out why))
            {
                Restart(s);
                return PushResult.Snap;
            }
            if (Count == Capacity)
            {
                for (int i = 1; i < Capacity; i++) _s[i - 1] = _s[i];
                Count = Capacity - 1;
            }
            _s[Count++] = s;
            return PushResult.Pushed;
        }

        private void Restart(CamSnapshot s)
        {
            _s[0] = s;
            Count = 1;
        }

        public static bool IsJump(CamSnapshot a, CamSnapshot b, out string why)
        {
            why = null;
            if (b.Has(SeFrameFlags.Teleported)) { why = "SE teleport flag"; return true; }
            double d = CamMath.Distance(a.Px, a.Py, a.Pz, b.Px, b.Py, b.Pz);
            if (d > JumpMetres) { why = "jump " + d.ToString("0.00", CultureInfo.InvariantCulture) + " m"; return true; }
            double ang = CamMath.AngleDeg(a.Qx, a.Qy, a.Qz, a.Qw, b.Qx, b.Qy, b.Qz, b.Qw);
            if (ang > JumpDegrees) { why = "turn " + ang.ToString("0.0", CultureInfo.InvariantCulture) + " deg"; return true; }
            return false;
        }
    }

    internal enum SampleOutcome
    {
        /// <summary>No usable snapshot: the camera is not driven.</summary>
        None = 0,
        /// <summary>Render time between two snapshots: position interpolated.</summary>
        Bracketed = 1,
        /// <summary>Render time past the newest snapshot by at most one SE tick: position extrapolated.</summary>
        Extrapolated = 2,
        /// <summary>Render time past the newest snapshot by more than one tick, before the oldest one, or one snapshot only.</summary>
        Held = 3,
    }

    /// <summary>The camera pose for one render (CONTRACT §2.8) and the ghost Player's target (ARCHITECTURE §5.11).</summary>
    internal struct CamSample
    {
        public SampleOutcome Outcome;
        /// <summary>Bracket alpha in [0, 1]; > 1 when extrapolated; 0 or 1 when held.</summary>
        public double Alpha;
        public double Px, Py, Pz;
        /// <summary>The C1 poses of the bracket's own ticks (OlderTick, PresentedTick) and of the snapshot before the older one (S8a: the camera jump across a chart change, and the motion per tick before it).</summary>
        public double OlderPx, OlderPy, OlderPz, NewerPx, NewerPy, NewerPz, PrevPx, PrevPy, PrevPz;
        public ulong PrevTick;
        /// <summary>Orientation of the newest snapshot (never interpolated).</summary>
        public float Qx, Qy, Qz, Qw;
        public float VFovDeg, Near, Aspect;
        public uint Flags, CameraMode;
        public bool PlayerAtPelvis;
        public double PlayerX, PlayerY, PlayerZ;
        /// <summary>
 /// CONTRACT §5.4 C7 `presentedSeTick` candidate: the newer bracket tick (the tick of the snapshot whose position
 /// is the newer end of the interpolation; the newest tick when extrapolated or held past the newest; the held
 /// snapshot's tick otherwise). 0 when nothing was sampled.
 /// </summary>
        public ulong PresentedTick;
        public ulong OlderTick;
        public ulong NewestTick;
        /// <summary>Position of the newest snapshot (S5 review F1: the camera's group is posed at the newest tick and shifted by
 /// (P - NewestP), the camera's own interpolation offset).</summary>
        public double NewestPx, NewestPy, NewestPz;
        public long NewestQpc;
        public long RenderQpc;
        /// <summary>The newest snapshot is older than 100 ms (CONTRACT §5.1 C1: hold, counted).</summary>
        public bool Stale;
    }

    /// <summary>
 /// The interpolation policy of CONTRACT §2.8 on QPC stamps: render time t = qpcNow − D with D = 1.25 SE ticks; the
 /// position is interpolated between the two snapshots that bracket t (one alpha); past the newest snapshot it is
 /// extrapolated with the last velocity (per SE tick, from the tick difference) for at most one tick, else held
 /// (CONTRACT §5.1 C1: no extrapolation beyond one tick); the orientation, FOV and near plane are always the newest
 /// snapshot's. Unity-free.
 /// </summary>
    internal sealed class FrameSampler
    {
        public const double SeTicksPerSecond = 60.0;
        public const double DelayTicks = 1.25;
        public const double StaleSeconds = 0.100;

        public readonly long QpcFrequency;
        public readonly double TickQpc;
        public readonly double DelayQpc;
        public readonly double StaleQpc;

        public FrameSampler(long qpcFrequency) : this(qpcFrequency, DelayTicks) { }

        public FrameSampler(long qpcFrequency, double delayTicks)
        {
            if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException("qpcFrequency");
            QpcFrequency = qpcFrequency;
            TickQpc = qpcFrequency / SeTicksPerSecond;
            DelayQpc = delayTicks * TickQpc;
            StaleQpc = StaleSeconds * qpcFrequency;
        }

        public CamSample Sample(CamHistory h, long qpcNow)
        {
            var r = new CamSample();
            int n = h.Count;
            if (n == 0) return r;
            CamSnapshot newest = h.Newest;
            double t = qpcNow - DelayQpc;
            r.RenderQpc = (long)t;
            r.NewestTick = newest.Tick;
            r.NewestQpc = newest.Qpc;
            r.NewestPx = newest.Px; r.NewestPy = newest.Py; r.NewestPz = newest.Pz;
            r.Stale = qpcNow - newest.Qpc > StaleQpc;
            r.Qx = newest.Qx; r.Qy = newest.Qy; r.Qz = newest.Qz; r.Qw = newest.Qw;
            r.VFovDeg = newest.VFovDeg; r.Near = newest.Near; r.Aspect = newest.Aspect;
            r.Flags = newest.Flags; r.CameraMode = newest.CameraMode;

            CamSnapshot oldest = h[0];
            if (t < oldest.Qpc)
            {
                // before the history (one snapshot, or just after a snap/reset): hold the oldest snapshot
                Hold(ref r, oldest, 0.0);
                return r;
            }
            if (t >= newest.Qpc)
            {
                double dt = t - newest.Qpc;
                if (n >= 2 && dt <= TickQpc)
                {
                    CamSnapshot prev = h[n - 2];
                    // Velocity per SE tick from the tick difference, not from the QPC span: SE stamps
 // qpcCapture after a simulation of variable length, so a short span (say 5 ms for one tick) would
 // extrapolate about 3x one tick's motion. With dt <= one tick, k <= 1 / ticks <= 1: at most one
 // tick's motion past the newest snapshot. Push keeps ticks strictly increasing (ticks >= 1).
                    double ticks = newest.Tick > prev.Tick ? (double)(newest.Tick - prev.Tick) : 1.0;
                    double k = dt / (ticks * TickQpc);
                    if (k > 1.0) k = 1.0;
                    r.Outcome = SampleOutcome.Extrapolated;
                    r.Alpha = 1.0 + k;
                    r.Px = newest.Px + (newest.Px - prev.Px) * k;
                    r.Py = newest.Py + (newest.Py - prev.Py) * k;
                    r.Pz = newest.Pz + (newest.Pz - prev.Pz) * k;
                    r.PresentedTick = newest.Tick;
                    r.OlderTick = prev.Tick;
                    r.OlderPx = prev.Px; r.OlderPy = prev.Py; r.OlderPz = prev.Pz;
                    r.NewerPx = newest.Px; r.NewerPy = newest.Py; r.NewerPz = newest.Pz;
                    CamSnapshot pp = n >= 3 ? h[n - 3] : prev;
                    r.PrevTick = pp.Tick; r.PrevPx = pp.Px; r.PrevPy = pp.Py; r.PrevPz = pp.Pz;
                    PlayerTarget(ref r, prev, newest, 1.0 + k);
                    return r;
                }
                Hold(ref r, newest, 1.0);
                return r;
            }
            // oldest.Qpc <= t < newest.Qpc: the newest pair that brackets t
            int i = n - 2;
            while (i > 0 && h[i].Qpc > t) i--;
            CamSnapshot a = h[i], b = h[i + 1];
            double alpha = (t - a.Qpc) / (double)(b.Qpc - a.Qpc);
            if (alpha < 0) alpha = 0;
            if (alpha > 1) alpha = 1;
            r.Outcome = SampleOutcome.Bracketed;
            r.Alpha = alpha;
            r.Px = a.Px + (b.Px - a.Px) * alpha;
            r.Py = a.Py + (b.Py - a.Py) * alpha;
            r.Pz = a.Pz + (b.Pz - a.Pz) * alpha;
            r.PresentedTick = b.Tick;
            r.OlderTick = a.Tick;
            r.OlderPx = a.Px; r.OlderPy = a.Py; r.OlderPz = a.Pz;
            r.NewerPx = b.Px; r.NewerPy = b.Py; r.NewerPz = b.Pz;
            CamSnapshot pa = i >= 1 ? h[i - 1] : a;
            r.PrevTick = pa.Tick; r.PrevPx = pa.Px; r.PrevPy = pa.Py; r.PrevPz = pa.Pz;
            PlayerTarget(ref r, a, b, alpha);
            return r;
        }

        private static void Hold(ref CamSample r, CamSnapshot s, double alpha)
        {
            r.Outcome = SampleOutcome.Held;
            r.Alpha = alpha;
            r.Px = s.Px; r.Py = s.Py; r.Pz = s.Pz;
            r.PresentedTick = s.Tick;
            r.OlderTick = s.Tick;
            r.PlayerAtPelvis = s.PlayerAtPelvis;
            if (r.PlayerAtPelvis) { r.PlayerX = s.PelX; r.PlayerY = s.PelY; r.PlayerZ = s.PelZ; }
            else { r.PlayerX = r.Px; r.PlayerY = r.Py; r.PlayerZ = r.Pz; }
        }

        /// <summary>
 /// The ghost Player rule: the pelvis of the newer snapshot when a character is present and not seated (lerped
 /// with the camera's alpha when both ends carry the same character), else the camera position just computed.
 /// </summary>
        private static void PlayerTarget(ref CamSample r, CamSnapshot a, CamSnapshot b, double alpha)
        {
            r.PlayerAtPelvis = b.PlayerAtPelvis;
            if (!r.PlayerAtPelvis)
            {
                r.PlayerX = r.Px; r.PlayerY = r.Py; r.PlayerZ = r.Pz;
                return;
            }
            if (a.PlayerAtPelvis && a.CharacterId == b.CharacterId)
            {
                r.PlayerX = a.PelX + (b.PelX - a.PelX) * alpha;
                r.PlayerY = a.PelY + (b.PelY - a.PelY) * alpha;
                r.PlayerZ = a.PelZ + (b.PelZ - a.PelZ) * alpha;
            }
            else
            {
                r.PlayerX = b.PelX; r.PlayerY = b.PelY; r.PlayerZ = b.PelZ;
            }
        }
    }

    internal enum C1Poll
    {
        /// <summary>`seq` 0: SE never wrote C1.</summary>
        Never = 0,
        Unchanged = 1,
        /// <summary>A coherent new copy was read (see <see cref="FrameReader.LastPush"/>).</summary>
        Fresh = 2,
        /// <summary>The writer kept the record busy for 16 tries: the last coherent copy is kept (counted "torn").</summary>
        Torn = 3,
    }

    /// <summary>
 /// C1 consumer: seqlock read of the `SeFrame` fixed part (CONTRACT §5.3: ≤ 16 tries, keep the last coherent copy),
 /// the snapshot history and the outcome counters (bracketed / extrapolated / held / torn, logged every 300 frames).
 /// Unity-free; polled from the camera driver's LateUpdateLast and once more in the main camera's onPreCull.
 /// </summary>
    internal sealed unsafe class FrameReader
    {
        private readonly byte* _c1;
        private uint _lastSeq;

        public readonly CamHistory History = new CamHistory();
        public readonly FrameSampler Sampler;

        public long Fresh, Torn, Invalid, NotInWorld, Snaps, Resets, Replaced, Ignored;
        public PushResult LastPush { get; private set; }
        public string LastPushWhy { get; private set; }

        // S5a: the C1 swim extension of the newest coherent record (CONTRACT §5.4 "C1 swim extension"; Wire SwimWire),
 // decoded with every fresh copy whatever the history did with it (a NotInWorld or Invalid frame still carries
 // the bytes). SwimValid = version 1; SwimRaw.Version is the raw version byte (0 = an SE build before S5a).
        private SwimWireState _swim;
        /// <summary>The decoded extension of the newest record (default when <see cref="SwimValid"/> is false).</summary>
        public SwimWireState Swim { get { return _swim; } }
        /// <summary>The newest record carried a version-1 extension.</summary>
        public bool SwimValid { get; private set; }
        /// <summary>The raw version byte of the newest record (0 = none).</summary>
        public byte SwimVersion { get; private set; }
        /// <summary>The C1 tick of the record <see cref="Swim"/> came from (the join key of the DE.Arms / DE.O2 lines).</summary>
        public ulong SwimTick { get; private set; }
        /// <summary>A record was read since the construction or the last <see cref="Reset"/>.</summary>
        public bool SwimSeen { get; private set; }
        public long SwimReads;

        // S7a: the C1 locomotion extension of the same record (CONTRACT §5.4 "C1 locomotion extension"; Wire S7aLoco,
 // bytes 0xA0-0xAF): LocoValid = version 1; LocoVersion is the raw byte (0 = an SE build before S7a); with the
 // pelvis height for the `diving` rule (C1 pelvisSn.y)
        private LocoWireState _loco;
        public LocoWireState Loco { get { return _loco; } }
        public bool LocoValid { get; private set; }
        public byte LocoVersion { get; private set; }
        public float PelvisSnY { get; private set; }

        // S7: the C1 interior extension of the same record (CONTRACT §5.4 "C1 interior extension"; Wire S7InteriorWire, bytes
 // 0xB0-0xBF): InteriorValid = version 1; InRoom = the extension's InRoom, else C1 flags bit 3 (an SE before S7);
 // InteriorMs = when this record was read (the inside-walkable rule's 100 ms freshness, InteriorRules.InsideRule)
        public C1Interior Interior { get; private set; }
        public bool InteriorValid { get; private set; }
        public bool InteriorInRoom { get; private set; }
        public bool InteriorSeen { get; private set; }
        public ulong InteriorMs { get; private set; }
        public ulong InteriorTick { get; private set; }

        // S13 hook: the C1 S13 extension of the same record (CONTRACT §5.4 "S13 conventions"; Wire S13CharWire, bytes 0xC0-0xC7):
 // the character's velocity in its look frame; S13Valid = version 1 (0 = an SE build before S13: S5a's input rule stays)
        private S13CharWireState _s13;
        public S13CharWireState S13Char { get { return _s13; } }
        public bool S13Valid { get; private set; }

        /// <summary><paramref name="c1"/>: the C1 record (region C1SeFrame, offset 0).</summary>
        public FrameReader(byte* c1, long qpcFrequency)
        {
            if (c1 == null) throw new ArgumentNullException("c1");
            _c1 = c1;
            Sampler = new FrameSampler(qpcFrequency);
        }

        public C1Poll Poll()
        {
            uint cur = Seqlock.Current(_c1);
            if (cur == 0) return C1Poll.Never;
            if (cur == _lastSeq) return C1Poll.Unchanged;
            SeFrame f;
            uint seq;
            SeqlockReadStatus st = Seqlock.TryReadBytes(_c1, (byte*)&f, sizeof(SeFrame), Seqlock.MaxTries, out seq);
            if (st == SeqlockReadStatus.Busy) { Torn++; return C1Poll.Torn; }
            if (st == SeqlockReadStatus.Never) return C1Poll.Never;
            if (seq == _lastSeq) return C1Poll.Unchanged;
            _lastSeq = seq;
            Fresh++;
            SwimWireState sw;
            SwimValid = SwimWire.TryRead(ref f, out sw);
            _swim = sw;
            SwimVersion = sw.Version;
            SwimTick = f.tick;
            SwimSeen = true;
            SwimReads++;
            LocoWireState lw;
            LocoValid = S7aLoco.TryRead(ref f, out lw);
            _loco = lw;
            LocoVersion = S7aLoco.ReadVersion(ref f);
            PelvisSnY = f.pelvisSn.y;
            C1Interior iw;
            InteriorValid = S7InteriorWire.TryRead(ref f, out iw);
            Interior = iw;
            InteriorInRoom = InteriorValid ? iw.InRoom : (f.flags & SeFrameFlags.CameraInAirtightRoom) != 0;
            InteriorSeen = true;
            InteriorMs = DeepEngineers.Wire.Native.Win32.TickCount64();
            InteriorTick = f.tick;
            S13CharWireState s13;
            S13Valid = S13CharWire.TryRead(ref f, out s13); // S13 hook
            _s13 = s13;
            string why;
            PushResult pr = History.Push(CamSnapshot.FromFrame(ref f, seq), out why);
            LastPush = pr;
            LastPushWhy = why;
            switch (pr)
            {
                case PushResult.Snap: Snaps++; break;
                case PushResult.ResetEpoch:
                case PushResult.ResetClock: Resets++; break;
                case PushResult.Invalid: Invalid++; break;
                case PushResult.NotInWorld: NotInWorld++; break;
                case PushResult.Replaced: Replaced++; break;
                case PushResult.Ignored: Ignored++; break;
            }
            return C1Poll.Fresh;
        }

        /// <summary>
 /// New SE generation (or a reset requested by the driver): the history is dropped and the C1 content present now
 /// is ignored until its `seq` changes (a C1 left by an earlier SE is never taken for a new frame).
 /// </summary>
        public void Reset()
        {
            History.Clear();
            _lastSeq = Seqlock.Current(_c1);
            Resets++;
            _swim = default(SwimWireState);
            SwimValid = false;
            SwimVersion = 0;
            SwimTick = 0;
            SwimSeen = false;
            _loco = default(LocoWireState);
            LocoValid = false;
            LocoVersion = 0;
            Interior = default(C1Interior);
            InteriorValid = false;
            InteriorInRoom = false;
            InteriorSeen = false;
            InteriorMs = 0;
            InteriorTick = 0;
            _s13 = default(S13CharWireState); // S13 hook
            S13Valid = false;
        }

        public CamSample Sample(long qpcNow) { return Sampler.Sample(History, qpcNow); }
    }

    /// <summary>
 /// Per-frame outcome counters of the presented frames (one count per main-camera re-sample). Frames held by the
 /// <see cref="DriveGate"/> are counted in <see cref="Gated"/> only (not in <see cref="Frames"/>: they present no C1
 /// pose, so they take no part in S1.N1).
 /// </summary>
    internal sealed class CamCounters
    {
        public long Frames, Bracketed, Extrapolated, Held, None, Stale, FovDrift, ExtraPreCull, Gated;

        public void Count(SampleOutcome o, bool stale)
        {
            Frames++;
            switch (o)
            {
                case SampleOutcome.Bracketed: Bracketed++; break;
                case SampleOutcome.Extrapolated: Extrapolated++; break;
                case SampleOutcome.Held: Held++; break;
                default: None++; break;
            }
            if (stale) Stale++;
        }

        public CamCounters Copy()
        {
            return (CamCounters)MemberwiseClone();
        }

        public void Reset()
        {
            Frames = Bracketed = Extrapolated = Held = None = Stale = FovDrift = ExtraPreCull = Gated = 0;
        }
    }

    /// <summary>
 /// Where Subnautica may be driven
 /// Unity-free.
 /// <para>
 /// Open (the camera and the Player follow C1) only while all hold, with hysteresis:
 /// <list type="bullet">
 /// <item>ρ∞ = max(|x|, |z|) of the sample ≤ 7,800 m to open, &gt; 7,900 m closes (C5 zone FAR);</item>
 /// <item>y ≤ +300 m to open, &gt; +350 m closes (ARCHITECTURE §4.2 Approach → Space);</item>
 /// <item>y ≥ −7,800 m to open, &lt; −7,900 m closes (OutOfBoundsWarp's lower face; below the −3,000 m floor);</item>
 /// <item>the latest C5 (when written within the last second) does not say FAR or off-site: SE decides the zone with
 /// its own hysteresis and the closest-water test (another planet's ocean), one tick after the C1 it was computed
 /// from, which is why the geometric limits above are also applied to the snapshot itself.</item>
 /// </list>
 /// Closed = "hold": nothing moves the camera or the Player; they stay at the last driven pose, no presentedSeTick is
 /// stamped, and the frames are counted as gated. S1 has no Idle recipe (D73): holding is its stand-in for Idle.
 /// </para>
 /// </summary>
    internal sealed class DriveGate
    {
        public const double ZoneInM = 7800.0, ZoneOutM = 7900.0;
        /// <summary>
 /// The altitude rule is only the fallback while C5 is stale: with a fresh C5 the gate follows SE's view state
 ///.
 /// </summary>
        public const double AltitudeInM = 300.0, AltitudeOutM = 350.0;
        public const double DepthInM = -7800.0, DepthOutM = -7900.0;
        public const ulong C5FreshMs = 1000;

        private bool _open;
        private uint _c5Zone = Zone.OffSite;
        private uint _c5View = Wire.ViewState.Space;
        private ulong _c5Ms;

        public bool Open { get { return _open; } }
        /// <summary>Why the gate is closed (null while open).</summary>
        public string Reason { get; private set; }
        public long Closings, Openings;

        public DriveGate()
        {
            Reason = "no C1 sample yet";
        }

        /// <summary>A fresh C5 was read (its zone and view state, CONTRACT §5.4 C5).</summary>
        public void NoteC5(uint zone, uint viewState, ulong nowMs)
        {
            _c5Zone = zone;
            _c5View = viewState;
            _c5Ms = nowMs;
        }

        public void Reset()
        {
            _open = false;
            Reason = "reset";
            _c5Ms = 0;
            _c5Zone = Zone.OffSite;
            _c5View = Wire.ViewState.Space;
        }

        /// <summary>
 /// Decides for a camera sample at SN (x, y, z); true when it may drive. Idempotent for the same inputs (called in
 /// LateUpdateLast and again in onPreCull). A transition is reported through <paramref name="changed"/>.
 /// </summary>
        public bool Decide(double x, double y, double z, ulong nowMs, out bool changed)
        {
            double rho = Math.Max(Math.Abs(x), Math.Abs(z));
            bool c5Fresh = _c5Ms != 0 && nowMs - _c5Ms <= C5FreshMs;
            bool c5Away = c5Fresh && (_c5Zone == Zone.Far || _c5Zone == Zone.OffSite);
            // S7a-1: with a fresh C5 the band lives in SE's view state (Space = above the band, or leaving);
 // the local altitude rule is the fallback while C5 is stale
            bool c5Space = c5Fresh && _c5View == Wire.ViewState.Space;
            string why = null;
            if (double.IsNaN(rho) || double.IsNaN(y)) why = "position not finite";
            else if (c5Away) why = "C5 zone " + (_c5Zone == Zone.Far ? "FAR" : "off-site");
            else if (c5Space) why = "C5 view Space (above the band, which lives in SE)";
            else if (_open)
            {
                if (rho > ZoneOutM) why = "rho " + rho.ToString("0", CultureInfo.InvariantCulture) + " m > " + ZoneOutM + " m (FAR)";
                else if (!c5Fresh && y > AltitudeOutM) why = "y " + y.ToString("0.0", CultureInfo.InvariantCulture) + " m > +" + AltitudeOutM + " m (above the Approach band; C5 stale)";
                else if (y < DepthOutM) why = "y " + y.ToString("0", CultureInfo.InvariantCulture) + " m < " + DepthOutM + " m (OutOfBoundsWarp margin)";
            }
            else
            {
                if (rho > ZoneInM) why = "rho " + rho.ToString("0", CultureInfo.InvariantCulture) + " m > " + ZoneInM + " m (enter at or below)";
                else if (!c5Fresh && y > AltitudeInM) why = "y " + y.ToString("0.0", CultureInfo.InvariantCulture) + " m > +" + AltitudeInM + " m (enter at or below; C5 stale)";
                else if (y < DepthInM) why = "y " + y.ToString("0", CultureInfo.InvariantCulture) + " m < " + DepthInM + " m (enter at or above)";
            }
            bool open = why == null;
            changed = open != _open;
            if (changed)
            {
                if (open) Openings++;
                else Closings++;
            }
            _open = open;
            Reason = why;
            return open;
        }
    }

    /// <summary>
 /// One traced presented frame (S1 tests: `de.trace on`). Filled on the main thread in onPreCull (a struct copy, no
 /// allocation); formatted and written to the JSONL by <see cref="CamTraceWriter"/> on a background thread once per
 /// second.
 /// </summary>
    internal struct CamTraceEntry
    {
        public int Frame;
        public ulong TickMs, SnFrame;
        public long QpcUsed, NewestQpc;
        public ulong NewestTick, PresentedTick, OlderTick;
        public SampleOutcome Outcome;
        public double Alpha;
        /// <summary>The Unity camera transform after placement (what is rendered).</summary>
        public float X, Y, Z, Qx, Qy, Qz, Qw;
        /// <summary>The commanded pose (interpolated C1 position, newest C1 orientation).</summary>
        public float Tx, Ty, Tz, Tqx, Tqy, Tqz, Tqw;
        /// <summary>
 /// fieldOfView found in onPreCull before the per-frame assertion (<see cref="FovSeen"/>), the C1 vFovDeg the plugin
 /// had applied before it (<see cref="FovApplied"/>; −1 before the first application), the rendered fieldOfView
 /// (<see cref="Fov"/>) and this sample's C1 vFovDeg (<see cref="C1Fov"/>). S1.N7: FovSeen = FovApplied (nothing
 /// else moved the FOV, e.g. PDACameraFOVControl under S1.M6) and Fov = C1Fov.
 /// </summary>
        public float FovSeen, FovApplied, Fov, C1Fov;
    }

    /// <summary>
 /// Formats <see cref="CamTraceEntry"/> records as the `DE.CamTrace` JSONL records of tests/s1/check_wm.py and
 /// latency.py. Unity-free (tested). Text, one line per frame:
 /// `DE.CamTrace f qpcUsed qpcFreq newestTick newestQpc presented older outcome alpha yaw pitch x y z qx qy qz qw tx ty tz
 /// tqx tqy tqz tqw fov fovSeen fovApplied c1Fov` (x… qw: the Unity camera transform; tx… tqw: the commanded pose;
 /// yaw/pitch of the transform).
 /// </summary>
    internal static class CamTraceFormat
    {
        public static void AppendRecord(StringBuilder sb, ref CamTraceEntry e, long qpcFreq, DateTime utc)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append("{\"t\":\"").Append(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", ci));
            sb.Append("\",\"tickMs\":").Append(e.TickMs.ToString(ci));
            sb.Append(",\"frame\":").Append(e.SnFrame.ToString(ci));
            sb.Append(",\"level\":\"info\",\"event\":\"DE.CamTrace\",\"text\":\"");
            AppendText(sb, ref e, qpcFreq);
            sb.Append("\"}\n");
        }

        /// <summary>The text of one record (only ASCII letters, digits, '.', '-', '+', 'E', '=' and spaces: no JSON escaping needed).</summary>
        public static void AppendText(StringBuilder sb, ref CamTraceEntry e, long qpcFreq)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append("DE.CamTrace f=").Append(e.Frame.ToString(ci))
              .Append(" qpcUsed=").Append(e.QpcUsed.ToString(ci))
              .Append(" qpcFreq=").Append(qpcFreq.ToString(ci))
              .Append(" newestTick=").Append(e.NewestTick.ToString(ci))
              .Append(" newestQpc=").Append(e.NewestQpc.ToString(ci))
              .Append(" presented=").Append(e.PresentedTick.ToString(ci))
              .Append(" older=").Append(e.OlderTick.ToString(ci))
              .Append(" outcome=").Append(OutcomeName(e.Outcome))
              .Append(" alpha=").Append(e.Alpha.ToString("0.0000", ci))
              .Append(" yaw=").Append(CamMath.YawDeg(e.Qx, e.Qy, e.Qz, e.Qw).ToString("0.000", ci))
              .Append(" pitch=").Append(CamMath.PitchDeg(e.Qx, e.Qy, e.Qz, e.Qw).ToString("0.000", ci));
            F(sb, " x=", e.X); F(sb, " y=", e.Y); F(sb, " z=", e.Z);
            F(sb, " qx=", e.Qx); F(sb, " qy=", e.Qy); F(sb, " qz=", e.Qz); F(sb, " qw=", e.Qw);
            F(sb, " tx=", e.Tx); F(sb, " ty=", e.Ty); F(sb, " tz=", e.Tz);
            F(sb, " tqx=", e.Tqx); F(sb, " tqy=", e.Tqy); F(sb, " tqz=", e.Tqz); F(sb, " tqw=", e.Tqw);
            F(sb, " fov=", e.Fov); F(sb, " fovSeen=", e.FovSeen); F(sb, " fovApplied=", e.FovApplied); F(sb, " c1Fov=", e.C1Fov);
        }

        private static void F(StringBuilder sb, string key, float v)
        {
            sb.Append(key).Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        public static string OutcomeName(SampleOutcome o)
        {
            switch (o)
            {
                case SampleOutcome.Bracketed: return "Bracketed";
                case SampleOutcome.Extrapolated: return "Extrapolated";
                case SampleOutcome.Held: return "Held";
                default: return "None";
            }
        }
    }

    /// <summary>
 /// The trace writer: the main thread appends <see cref="CamTraceEntry"/> structs to a
 /// preallocated buffer (<see cref="Add"/>, allocation-free); once per second <see cref="Flush"/> hands the filled
 /// buffer to a background thread, which formats it and appends it to the JSONL in one write through
 /// <see cref="Sink"/>. Two buffers alternate; when the writer is still busy, entries accumulate up to the capacity and
 /// the excess is dropped and counted. <see cref="Stop"/> writes what is left on the calling thread. Unity-free.
 /// </summary>
    internal sealed class CamTraceWriter
    {
        public const int Capacity = 2048;

        private readonly long _qpcFreq;
        private CamTraceEntry[] _fill = new CamTraceEntry[Capacity];
        private CamTraceEntry[] _spare = new CamTraceEntry[Capacity];
        private int _fillCount;
        private CamTraceEntry[] _pending;
        private int _pendingCount;
        private readonly object _lock = new object();
        private readonly StringBuilder _sb = new StringBuilder(64 * 1024);
        private System.Threading.Thread _thread;
        private System.Threading.AutoResetEvent _wake;
        private volatile bool _stop;

        /// <summary>Writes one batch of complete JSONL lines (DeLog.AppendJsonlBatch in the plugin); called on the writer thread.</summary>
        public readonly Action<StringBuilder> Sink;
        public long Written, Dropped, Batches;

        public CamTraceWriter(long qpcFrequency, Action<StringBuilder> sink)
        {
            _qpcFreq = qpcFrequency;
            Sink = sink;
        }

        /// <summary>Main thread, per traced frame.</summary>
        public void Add(ref CamTraceEntry e)
        {
            if (_fillCount >= _fill.Length) { Dropped++; return; }
            _fill[_fillCount++] = e;
        }

        public int Buffered { get { return _fillCount; } }

        /// <summary>Main thread, once per second: hands the buffer to the writer thread (skipped while it is busy).</summary>
        public void Flush()
        {
            if (_fillCount == 0) return;
            lock (_lock)
            {
                if (_pending != null) return; // the writer still holds the previous batch: keep accumulating
                _pending = _fill;
                _pendingCount = _fillCount;
                _fill = _spare;
                _spare = null;
                _fillCount = 0;
            }
            EnsureThread();
            _wake.Set();
        }

        private void EnsureThread()
        {
            if (_thread != null) return;
            _wake = new System.Threading.AutoResetEvent(false);
            _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "DeepEngineers.CamTraceWriter" };
            _thread.Start();
        }

        private void Run()
        {
            while (!_stop)
            {
                _wake.WaitOne(1000);
                WritePending();
            }
        }

        private void WritePending()
        {
            CamTraceEntry[] batch;
            int n;
            lock (_lock)
            {
                batch = _pending;
                n = _pendingCount;
            }
            if (batch == null) return;
            try
            {
                Format(batch, n);
            }
            catch (Exception)
            {
                // logging must never take the game down
            }
            lock (_lock)
            {
                _spare = batch;
                _pending = null;
                _pendingCount = 0;
            }
        }

        private void Format(CamTraceEntry[] batch, int n)
        {
            _sb.Length = 0;
            DateTime utc = DateTime.UtcNow;
            for (int i = 0; i < n; i++)
            {
                CamTraceFormat.AppendRecord(_sb, ref batch[i], _qpcFreq, utc);
                if (_sb.Length > 48 * 1024)
                {
                    Sink(_sb);
                    _sb.Length = 0;
                }
            }
            if (_sb.Length > 0) Sink(_sb);
            Written += n;
            Batches++;
        }

        /// <summary>Stops the thread and writes what is left on the calling thread (bridge disposal, before the JSONL closes).</summary>
        public void Stop()
        {
            _stop = true;
            if (_thread != null)
            {
                bool joined = false;
                _wake.Set();
                try { joined = _thread.Join(2000); } catch (Exception) { }
                if (!joined) return; // still formatting: never touch its buffers from here
            }
            WritePending();
            if (_fillCount > 0)
            {
                CamTraceEntry[] rest = _fill;
                int n = _fillCount;
                _fillCount = 0;
                try { Format(rest, n); } catch (Exception) { }
            }
        }
    }

    /// <summary>
 /// Fixed-bin latency histogram (0.25 ms bins up to 250 ms, plus an overflow bin) for the S1.N1 transport histogram
 /// (`qpcCapture` of the newest snapshot → `qpcUsed` in onPreCull). Allocation-free after construction.
 /// </summary>
    internal sealed class LatencyHistogram
    {
        public const double BinMs = 0.25;
        public const int Bins = 1000;
        private readonly long[] _bins = new long[Bins + 1];

        public long Count { get; private set; }
        public double MaxMs { get; private set; }

        public void Add(double ms)
        {
            if (double.IsNaN(ms)) return;
            if (ms < 0) ms = 0;
            int i = (int)(ms / BinMs);
            if (i > Bins) i = Bins;
            _bins[i]++;
            Count++;
            if (ms > MaxMs) MaxMs = ms;
        }

        /// <summary>The upper edge of the bin holding the p-quantile (p in [0, 1]); NaN when empty.</summary>
        public double Percentile(double p)
        {
            if (Count == 0) return double.NaN;
            long rank = (long)Math.Ceiling(p * Count);
            if (rank < 1) rank = 1;
            long acc = 0;
            for (int i = 0; i <= Bins; i++)
            {
                acc += _bins[i];
                if (acc >= rank) return i == Bins ? MaxMs : (i + 1) * BinMs;
            }
            return MaxMs;
        }

        public void Clear()
        {
            Array.Clear(_bins, 0, _bins.Length);
            Count = 0;
            MaxMs = 0;
        }

        public string Describe()
        {
            if (Count == 0) return "n=0";
            var ci = CultureInfo.InvariantCulture;
            return "n=" + Count.ToString(ci) + " p50=" + Percentile(0.50).ToString("0.00", ci) + " p95=" + Percentile(0.95).ToString("0.00", ci) +
                   " p99=" + Percentile(0.99).ToString("0.00", ci) + " max=" + MaxMs.ToString("0.00", ci);
        }
    }

    /// <summary>Small Unity-free quaternion and vector helpers (Unity conventions: forward +Z, up +Y, left-handed).</summary>
    internal static class CamMath
    {
        public static double Distance(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Rotation angle between two quaternions, compared as rotations (sign-insensitive), in degrees.</summary>
        public static double AngleDeg(float ax, float ay, float az, float aw, float bx, float by, float bz, float bw)
        {
            double na = Math.Sqrt((double)ax * ax + (double)ay * ay + (double)az * az + (double)aw * aw);
            double nb = Math.Sqrt((double)bx * bx + (double)by * by + (double)bz * bz + (double)bw * bw);
            if (na == 0 || nb == 0) return 180.0;
            double dot = Math.Abs(((double)ax * bx + (double)ay * by + (double)az * bz + (double)aw * bw) / (na * nb));
            if (dot > 1) dot = 1;
            return 2.0 * Math.Acos(dot) * 180.0 / Math.PI;
        }

        /// <summary>Forward vector q·(0, 0, 1) of a Unity quaternion.</summary>
        public static void Forward(float x, float y, float z, float w, out double fx, out double fy, out double fz)
        {
            fx = 2.0 * ((double)x * z + (double)w * y);
            fy = 2.0 * ((double)y * z - (double)w * x);
            fz = 1.0 - 2.0 * ((double)x * x + (double)y * y);
        }

        /// <summary>Heading of the forward vector about +Y in degrees, 0 = +Z (SN north), 90 = +X (SN east).</summary>
        public static double YawDeg(float x, float y, float z, float w)
        {
            double fx, fy, fz;
            Forward(x, y, z, w, out fx, out fy, out fz);
            return Math.Atan2(fx, fz) * 180.0 / Math.PI;
        }

        /// <summary>Elevation of the forward vector in degrees (positive up).</summary>
        public static double PitchDeg(float x, float y, float z, float w)
        {
            double fx, fy, fz;
            Forward(x, y, z, w, out fx, out fy, out fz);
            return Math.Atan2(fy, Math.Sqrt(fx * fx + fz * fz)) * 180.0 / Math.PI;
        }

        /// <summary>Smallest absolute difference of two headings in degrees, in [0, 180].</summary>
        public static double YawDelta(double a, double b)
        {
            double d = (b - a) % 360.0;
            if (d < -180) d += 360;
            if (d > 180) d -= 360;
            return Math.Abs(d);
        }
    }
}
