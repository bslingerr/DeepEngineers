using System;
using System.Collections.Generic;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S15 rules both plugins use (CONTRACT §5.4 "S15 conventions"); pure, tested in Wire.Tests against the Python twin's
 // vectors (tools/s15/s15_ref.py --vectors -> tests/s15/vectors/*.tsv).
 // =================================================================================================================

 /// <summary>What the S15 gate decided for one sample (format voice.gate).</summary>
    public enum S15GateResult
    {
        /// <summary>No rising edge (the condition is unchanged, or it came back within RearmMs).</summary>
        None = 0,
        /// <summary>An event that is sent: the C5 counter goes up, Subnautica plays the line.</summary>
        Played = 1,
        /// <summary>An event inside S16VoiceRule's cooldown for this key.</summary>
        Cooldown = 2,
        /// </summary>
        SeAudio = 4,
    }

    /// <summary>
 /// The S15 voice gate (format voice.gate): rising edges per (event, key) and S16's voice rule (<see cref="S16VoiceRule"/>,
 /// one per event with its cooldown) for the decision; never a second cooldown rule, no once-per-session rule. An edge met

 /// is gone; it survives only as the seeded failure S15.M17, <paramref name="holdMutant"/> of <see cref="Step"/>). A grid
 /// that becomes aboard takes its states as the baseline (<see cref="Baseline"/>). Pure; time in ms of the caller's clock.
 /// <paramref name="noRules"/> (S15.M1) sends every rising edge.
 /// </summary>
    public sealed class S15VoiceGate
    {
        /// <summary>S15.M17 only: how long a skipped edge was held for Subnautica's audio.</summary>
        public const int MutantPendingMs = 60000;

        private struct State
        {
            public bool On, Ever, Pending;
            public long LastOnMs, PendingSinceMs;
        }

        private readonly Dictionary<long, State>[] _state = new Dictionary<long, State>[S15Conventions.Event.Count + 1];
        private readonly S16VoiceRule[] _rules = new S16VoiceRule[S15Conventions.Event.Count + 1];

        public S15VoiceGate()
        {
            for (int i = 1; i <= S15Conventions.Event.Count; i++)
            {
                _state[i] = new Dictionary<long, State>();
                _rules[i] = new S16VoiceRule(S15Conventions.VoiceOf(i).CooldownMs);
            }
        }

        public void Clear()
        {
            for (int i = 1; i <= S15Conventions.Event.Count; i++) { _state[i].Clear(); _rules[i].Clear(); }
        }

        /// <summary>The key's condition went away without a sample (the grid left the aboard set): an off sample at <paramref name="nowMs"/>.</summary>
        public void Off(int ev, long key, long nowMs) { Step(ev, key, false, nowMs, true, false); }

        /// <summary>A held edge of this key (S15.M17 only).</summary>
        public bool IsPending(int ev, long key)
        {
            State s;
            return ev >= 1 && ev <= S15Conventions.Event.Count && _state[ev].TryGetValue(key, out s) && s.Pending;
        }

        /// <summary>
 /// format voice.gate, the baseline: the state of a grid that just became aboard, taken without an event (a grid already
 /// powered, a switch already off are not news).
 /// </summary>
        public void Baseline(int ev, long key, bool condition, long nowMs)
        {
            if (S15Conventions.VoiceOf(ev) == null) return;
            Dictionary<long, State> map = _state[ev];
            State s;
            map.TryGetValue(key, out s);
            s.On = condition;
            s.Pending = false;
            if (condition) { s.Ever = true; s.LastOnMs = nowMs; }
            map[key] = s;
        }

        /// <summary>
 /// One sample of event <paramref name="ev"/>'s condition for <paramref name="key"/> at <paramref name="nowMs"/>;
 /// <paramref name="snAudioLive"/> = Subnautica's audio is live (the S2 rule: C5 snBusTarget at least SnBusLivePermille).
 /// </summary>
        public S15GateResult Step(int ev, long key, bool condition, long nowMs, bool snAudioLive, bool noRules, bool holdMutant = false)
        {
            S15Conventions.VoiceRow row = S15Conventions.VoiceOf(ev);
            if (row == null) return S15GateResult.None;
            Dictionary<long, State> map = _state[ev];
            State s;
            map.TryGetValue(key, out s);
            S15GateResult r = S15GateResult.None;
            if (condition && !s.On)
            {
                long offFor = s.Ever ? nowMs - s.LastOnMs : long.MaxValue;
                if (noRules) r = S15GateResult.Played;
                else if (s.Ever && offFor < row.RearmMs) r = S15GateResult.None;
                else if (!snAudioLive)
                {
                    r = S15GateResult.SeAudio;
                    if (holdMutant) { s.Pending = true; s.PendingSinceMs = nowMs; }
                }
                else if (!_rules[ev].TryFire(key, nowMs)) r = S15GateResult.Cooldown;
                else r = S15GateResult.Played;
            }
            else if (condition && s.Pending)
            {
                // S15.
                if (nowMs - s.PendingSinceMs > MutantPendingMs) s.Pending = false;
                else if (snAudioLive)
                {
                    s.Pending = false;
                    r = _rules[ev].TryFire(key, nowMs) ? S15GateResult.Played : S15GateResult.Cooldown;
                }
            }
            s.On = condition;
            if (condition) { s.Ever = true; s.LastOnMs = nowMs; }
            else s.Pending = false;
            map[key] = s;
            return r;
        }

        public static bool SnAudioLive(float snBusTarget) { return snBusTarget * 1000f >= S15Conventions.Voice.SnBusLivePermille; }
    }

    /// <summary>The aboard rule's hold (format aboard): the raw aboard grid must stay the same AboardMs before it counts.</summary>
    public sealed class S15AboardHold
    {
        private long _candidate, _since;
        public long Current { get; private set; }

        public void Reset() { _candidate = 0; _since = 0; Current = 0; }

        /// <summary>One sample: <paramref name="raw"/> = the grid the player is aboard now (0 = none); returns the counted grid.</summary>
        public long Step(long raw, long nowMs, bool noHold)
        {
            if (raw != _candidate) { _candidate = raw; _since = nowMs; }
            if (raw == 0) { Current = 0; return 0; }
            if (noHold || nowMs - _since >= S15Conventions.Voice.AboardMs) Current = raw;
            else if (Current != raw) Current = 0;
            return Current;
        }
    }

    /// <summary>
 /// format voice.power, EnginePowerDown's condition: the grid's power switch off, after it went off while the grid was
 /// powered. A new
 /// grid (or the first sample) is its baseline: a switch already off is no condition. Pure.
 /// </summary>
    public sealed class S15PowerSwitch
    {
        private bool _have, _switchOn, _lastPowered, _poweredAtOff;
        private long _grid;

        public void Reset() { _have = false; _poweredAtOff = false; }

        public void Baseline(long grid, bool switchOn, bool powered)
        {
            _have = true;
            _grid = grid;
            _switchOn = switchOn;
            _lastPowered = powered;
            _poweredAtOff = false;
        }

        /// <summary>One scan of the aboard grid: true while EnginePowerDown's condition holds.</summary>
        public bool Step(long grid, bool switchOn, bool powered)
        {
            if (!_have || grid != _grid) { Baseline(grid, switchOn, powered); return false; }
            if (_switchOn && !switchOn) _poweredAtOff = _lastPowered || powered;
            else if (switchOn) _poweredAtOff = false;
            _switchOn = switchOn;
            _lastPowered = powered;
            return !switchOn && _poweredAtOff;
        }
    }

    /// <summary>The C5 S15 block (format c5.s15, version 2).</summary>
    public struct S15WireState
    {
        public byte Version, Flags;
        public uint WorldEpoch, WelcomeSeq, PowerUpSeq, PowerDownSeq, SmallWelcomeSeq;
        public long GridId;

        public uint SeqOf(int ev)
        {
            switch (ev)
            {
                case S15Conventions.Event.Welcome: return WelcomeSeq;
                case S15Conventions.Event.EnginePowerUp: return PowerUpSeq;
                case S15Conventions.Event.EnginePowerDown: return PowerDownSeq;
                case S15Conventions.Event.SmallWelcome: return SmallWelcomeSeq;
                default: return 0;
            }
        }
    }

    public static unsafe class S15Wire
    {
        public static void Write(ref SeState st, ref S15WireState w)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S15Conventions.C5.Offset;
                b[0x00] = (byte)S15Conventions.C5.Version;
                b[0x01] = w.Flags;
                *(ushort*)(b + 0x02) = 0;
                *(uint*)(b + 0x04) = w.WorldEpoch;
                *(uint*)(b + 0x08) = w.WelcomeSeq;
                *(uint*)(b + 0x0C) = w.PowerUpSeq;
                *(uint*)(b + 0x10) = w.PowerDownSeq;
                *(uint*)(b + 0x14) = w.SmallWelcomeSeq;
                *(long*)(b + 0x18) = w.GridId;
            }
        }

        public static void Clear(ref SeState st)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S15Conventions.C5.Offset;
                for (int i = 0; i < S15Conventions.C5.Size; i++) b[i] = 0;
            }
        }

        /// <summary>False when the record carries no S15 block of this version (0, the older 1, or unknown).</summary>
        public static bool TryRead(ref SeState st, out S15WireState w)
        {
            w = default(S15WireState);
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S15Conventions.C5.Offset;
                w.Version = b[0x00];
                if (w.Version != S15Conventions.C5.Version) return false;
                w.Flags = b[0x01];
                w.WorldEpoch = *(uint*)(b + 0x04);
                w.WelcomeSeq = *(uint*)(b + 0x08);
                w.PowerUpSeq = *(uint*)(b + 0x0C);
                w.PowerDownSeq = *(uint*)(b + 0x10);
                w.SmallWelcomeSeq = *(uint*)(b + 0x14);
                w.GridId = *(long*)(b + 0x18);
            }
            return true;
        }
    }

    /// <summary>The events block (format c6.s15events, version 2: the sonar); one instance is reused by its reader (no allocation per frame).</summary>
    public sealed class S15EventsState
    {
        public byte Version, Flags;
        public ushort Count;
        public uint WorldEpoch, SonarSeq;

        public bool Valid { get { return Version == S15Conventions.Events.Version; } }

        public void CopyTo(S15EventsState o)
        {
            o.Version = Version; o.Flags = Flags; o.Count = Count; o.WorldEpoch = WorldEpoch; o.SonarSeq = SonarSeq;
        }
    }

    /// <summary>The C6 region-space codec of the events block (its own seqlock word at its offset 0).</summary>
    public static unsafe class S15EventsWire
    {
        public static void Encode(byte* buf, S15EventsState e)
        {
            for (int i = 0; i < S15Conventions.Events.Size; i++) buf[i] = 0;
            buf[0x04] = (byte)S15Conventions.Events.Version;
            buf[0x05] = e.Flags;
            *(ushort*)(buf + 0x06) = 0;
            *(uint*)(buf + 0x08) = e.WorldEpoch;
            *(uint*)(buf + 0x0C) = e.SonarSeq;
        }

        public static void Decode(byte* buf, S15EventsState e)
        {
            e.Version = buf[0x04];
            e.Flags = buf[0x05];
            e.Count = *(ushort*)(buf + 0x06);
            e.WorldEpoch = *(uint*)(buf + 0x08);
            e.SonarSeq = *(uint*)(buf + 0x0C);
        }

        public static void Write(byte* c6Region, S15EventsState e)
        {
            byte* buf = stackalloc byte[S15Conventions.Events.Size];
            Encode(buf, e);
            Seqlock.WriteBytes(c6Region + S15Conventions.Events.Offset, buf, S15Conventions.Events.Size);
        }

        /// <summary>Fresh = a coherent copy into <paramref name="into"/> (any version: check <see cref="S15EventsState.Valid"/>).</summary>
        public static SeqlockReadStatus TryRead(byte* c6Region, S15EventsState into, out uint seq)
        {
            byte* buf = stackalloc byte[S15Conventions.Events.Size];
            SeqlockReadStatus st = Seqlock.TryReadBytes(c6Region + S15Conventions.Events.Offset, buf, S15Conventions.Events.Size, Seqlock.MaxTries, out seq);
            if (st == SeqlockReadStatus.Fresh) Decode(buf, into);
            return st;
        }
    }

    /// <summary>The kept picks' small rules (formats night.lights, fish.scare, sonar.ping): pure functions both plugins, the tests and the twin share.</summary>
    public static class S15PickRules
    {
        /// <summary>The block lights' factor at the day's light scalar (format night.lights).</summary>
        public static double NightFactor(double lightScalar)
        {
            double n = S15Conventions.Picks.NightLightPermille / 1000.0;
            double l = Math.Max(0.0, Math.Min(1.0, lightScalar));
            return n + (1.0 - n) * l;
        }

        /// <summary>A section's speed scares fish (format fish.scare).</summary>
        public static bool Scares(double speedMps) { return speedMps * 10.0 > S15Conventions.Picks.FishScareSpeedDms && !Jump(speedMps); }

        /// <summary>A speed no ship reaches: a teleport or a re-pose (format fish.scare).</summary>
        public static bool Jump(double speedMps) { return speedMps * 10.0 > S15Conventions.Picks.FishJumpSpeedDms; }

        /// <summary>The sonar switch: a block name holding SonarTag in any case.</summary>
        public static bool IsSonarName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf(S15Conventions.Picks.SonarTag, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
 /// format audio.groups: Subnautica's music and voice groups across the surface (pure). The S2 rule's target t (C5
 /// snBusTarget: 1 under water, 0 above) moves the effects group to t and the music and voice groups between their above-water
 /// level and 1; the VCA gets the player's own slider value times the group's level.
 /// </summary>
    public static class S15MixRules
    {
        public static double Clamp01(double v) { return double.IsNaN(v) ? 0.0 : v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v; }

        /// <summary>A live group's target for the S2 target <paramref name="t"/> and its above-water level.</summary>
        public static double GroupTarget(double t, double aboveLevel)
        {
            double a = Clamp01(aboveLevel);
            return a + (1.0 - a) * Clamp01(t);
        }

        /// <summary>The VCA volume written: the player's own slider value times the group's level.</summary>
        public static double VcaVolume(double user, double level) { return Clamp01(user) * Clamp01(level); }

        /// <summary>A live group's above-water level from its per-mille constant.</summary>
        public static double Above(int permille) { return Clamp01(permille / 1000.0); }
    }

    /// <summary>The sky frame (format c6.s15frame): where the camera is and how SE directions map to SN at it.</summary>
    public struct S15SkyFrame
    {
        public byte Version, Flags;
        public ushort BodyCount;
        public uint ListSeq, WorldEpoch;
        public ulong Tick;
        /// <summary>P - C in SE world (the camera relative to the bound planet's centre).</summary>
        public Vec3 CamRel;
        /// <summary>Rows of mapdir at P (SN = M x SE), row-major.</summary>
        public double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public bool Valid { get { return Version == S15Conventions.Sky.Version && (Flags & S15Conventions.SkyFlags.Valid) != 0; } }

        public Vec3 Map(Vec3 w)
        {
            return new Vec3(M00 * w.X + M01 * w.Y + M02 * w.Z, M10 * w.X + M11 * w.Y + M12 * w.Z, M20 * w.X + M21 * w.Y + M22 * w.Z);
        }
    }

    /// <summary>One body of the list (format c6.s15list).</summary>
    public struct S15Body
    {
        /// <summary>The body's centre - C (SE world).</summary>
        public Vec3 Rel;
        public float RadiusM, AtmosphereM;
        public ushort Palette;
        public byte Flags;
        public string Name;

        public bool Bound { get { return (Flags & S15Conventions.BodyFlags.Bound) != 0; } }
        public bool HasAtmosphere { get { return (Flags & S15Conventions.BodyFlags.Atmosphere) != 0; } }
    }

    /// <summary>The C6 region-space codec of the sky frame and the body list (each with its own seqlock word at its offset 0).</summary>
    public static unsafe class S15SkyWire
    {
        public static void WriteFrame(byte* c6Region, ref S15SkyFrame f)
        {
            byte* buf = stackalloc byte[S15Conventions.Sky.FrameSize];
            for (int i = 0; i < S15Conventions.Sky.FrameSize; i++) buf[i] = 0;
            buf[0x04] = (byte)S15Conventions.Sky.Version;
            buf[0x05] = f.Flags;
            *(ushort*)(buf + 0x06) = f.BodyCount;
            *(uint*)(buf + 0x08) = f.ListSeq;
            *(uint*)(buf + 0x0C) = f.WorldEpoch;
            *(ulong*)(buf + 0x10) = f.Tick;
            *(double*)(buf + 0x18) = f.CamRel.X;
            *(double*)(buf + 0x20) = f.CamRel.Y;
            *(double*)(buf + 0x28) = f.CamRel.Z;
            float* m = (float*)(buf + 0x30);
            m[0] = (float)f.M00; m[1] = (float)f.M01; m[2] = (float)f.M02;
            m[3] = (float)f.M10; m[4] = (float)f.M11; m[5] = (float)f.M12;
            m[6] = (float)f.M20; m[7] = (float)f.M21; m[8] = (float)f.M22;
            Seqlock.WriteBytes(c6Region + S15Conventions.Sky.FrameOffset, buf, S15Conventions.Sky.FrameSize);
        }

        /// <summary>Fresh = a coherent copy (any version: check <see cref="S15SkyFrame.Valid"/>); seq = its seqlock word.</summary>
        public static SeqlockReadStatus TryReadFrame(byte* c6Region, out S15SkyFrame f, out uint seq)
        {
            f = default(S15SkyFrame);
            byte* buf = stackalloc byte[S15Conventions.Sky.FrameSize];
            SeqlockReadStatus st = Seqlock.TryReadBytes(c6Region + S15Conventions.Sky.FrameOffset, buf, S15Conventions.Sky.FrameSize, Seqlock.MaxTries, out seq);
            if (st != SeqlockReadStatus.Fresh) return st;
            f.Version = buf[0x04];
            f.Flags = buf[0x05];
            f.BodyCount = *(ushort*)(buf + 0x06);
            f.ListSeq = *(uint*)(buf + 0x08);
            f.WorldEpoch = *(uint*)(buf + 0x0C);
            f.Tick = *(ulong*)(buf + 0x10);
            f.CamRel = new Vec3(*(double*)(buf + 0x18), *(double*)(buf + 0x20), *(double*)(buf + 0x28));
            float* m = (float*)(buf + 0x30);
            f.M00 = m[0]; f.M01 = m[1]; f.M02 = m[2];
            f.M10 = m[3]; f.M11 = m[4]; f.M12 = m[5];
            f.M20 = m[6]; f.M21 = m[7]; f.M22 = m[8];
            return st;
        }

        private const int ListBytes = S15Conventions.Sky.ListHeader + S15Conventions.Sky.MaxBodies * S15Conventions.Sky.RowSize;

        /// <summary>Writes up to MaxBodies bodies (names cut to NameMax ASCII characters, others as '?').</summary>
        public static int WriteList(byte* c6Region, uint listSeq, uint worldEpoch, IList<S15Body> bodies)
        {
            byte* buf = stackalloc byte[ListBytes];
            for (int i = 0; i < ListBytes; i++) buf[i] = 0;
            int n = bodies == null ? 0 : Math.Min(bodies.Count, S15Conventions.Sky.MaxBodies);
            buf[0x04] = (byte)S15Conventions.Sky.Version;
            buf[0x05] = (byte)n;
            *(uint*)(buf + 0x08) = listSeq;
            *(uint*)(buf + 0x0C) = worldEpoch;
            for (int i = 0; i < n; i++)
            {
                S15Body b = bodies[i];
                byte* r = buf + S15Conventions.Sky.ListHeader + i * S15Conventions.Sky.RowSize;
                *(double*)(r + 0x00) = b.Rel.X;
                *(double*)(r + 0x08) = b.Rel.Y;
                *(double*)(r + 0x10) = b.Rel.Z;
                *(float*)(r + 0x18) = b.RadiusM;
                *(float*)(r + 0x1C) = b.AtmosphereM;
                *(ushort*)(r + 0x20) = b.Palette;
                r[0x22] = b.Flags;
                string name = b.Name ?? "";
                int len = Math.Min(name.Length, S15Conventions.Sky.NameMax);
                r[0x23] = (byte)len;
                for (int k = 0; k < len; k++) { char c = name[k]; r[0x24 + k] = c >= 32 && c < 127 ? (byte)c : (byte)'?'; }
            }
            Seqlock.WriteBytes(c6Region + S15Conventions.Sky.ListOffset, buf, ListBytes);
            return n;
        }

        /// <summary>Fresh = a coherent copy with this version; <paramref name="bodies"/> is cleared and filled.</summary>
        public static SeqlockReadStatus TryReadList(byte* c6Region, List<S15Body> bodies, out uint listSeq, out uint worldEpoch, out uint seq)
        {
            listSeq = worldEpoch = 0;
            byte* buf = stackalloc byte[ListBytes];
            SeqlockReadStatus st = Seqlock.TryReadBytes(c6Region + S15Conventions.Sky.ListOffset, buf, ListBytes, Seqlock.MaxTries, out seq);
            if (st != SeqlockReadStatus.Fresh) return st;
            if (buf[0x04] != S15Conventions.Sky.Version) return SeqlockReadStatus.Never;
            int n = Math.Min((int)buf[0x05], S15Conventions.Sky.MaxBodies);
            listSeq = *(uint*)(buf + 0x08);
            worldEpoch = *(uint*)(buf + 0x0C);
            bodies.Clear();
            var sb = new StringBuilder(S15Conventions.Sky.NameMax);
            for (int i = 0; i < n; i++)
            {
                byte* r = buf + S15Conventions.Sky.ListHeader + i * S15Conventions.Sky.RowSize;
                var b = new S15Body
                {
                    Rel = new Vec3(*(double*)(r + 0x00), *(double*)(r + 0x08), *(double*)(r + 0x10)),
                    RadiusM = *(float*)(r + 0x18),
                    AtmosphereM = *(float*)(r + 0x1C),
                    Palette = *(ushort*)(r + 0x20),
                    Flags = r[0x22],
                };
                int len = Math.Min((int)r[0x23], S15Conventions.Sky.NameMax);
                sb.Length = 0;
                for (int k = 0; k < len; k++) sb.Append((char)r[0x24 + k]);
                b.Name = sb.ToString();
                bodies.Add(b);
            }
            return st;
        }
    }

    /// <summary>The sky math (formats c6.s15frame, sky.dir, sky.size, sky.disc).</summary>
    public static class S15Sky
    {
        /// <summary>The frame at SE camera point <paramref name="camSe"/> for the map (the current chart): P - C and mapdir's rows.</summary>
        public static S15SkyFrame FrameAt(SiteMap map, Vec3 camSe, bool identityMutant)
        {
            var f = new S15SkyFrame { Version = (byte)S15Conventions.Sky.Version, Flags = (byte)S15Conventions.SkyFlags.Valid, CamRel = camSe - map.C };
            if (identityMutant)
            {
                f.M00 = 1; f.M11 = 1; f.M22 = 1;
                return f;
            }
            Vec3 e, u, n;
            map.FrameAt(camSe, out e, out u, out n);
            double c = Math.Cos(map.Psi), s = Math.Sin(map.Psi);
            // DirSeToSn(e, u, n, w) = (c (w.e) + s (w.n), w.u, -s (w.e) + c (w.n)) (SiteMap.Yaw, sign +1)
            Vec3 rx = e * c + n * s, rz = e * (-s) + n * c;
            f.M00 = rx.X; f.M01 = rx.Y; f.M02 = rx.Z;
            f.M10 = u.X; f.M11 = u.Y; f.M12 = u.Z;
            f.M20 = rz.X; f.M21 = rz.Y; f.M22 = rz.Z;
            return f;
        }

        /// <summary>
 /// A body seen from the frame's camera: its SN direction (unit), its angular radius (rad) and distance (m); false when
 /// the camera is inside it (or at its centre).
 /// </summary>
        public static bool View(ref S15SkyFrame f, Vec3 bodyRel, double radiusM, out Vec3 dirSn, out double angRadius, out double distM)
        {
            Vec3 d = bodyRel - f.CamRel;
            distM = d.Length;
            dirSn = Vec3.Zero;
            angRadius = 0;
            if (!(distM > radiusM) || !(radiusM > 0)) return false;
            dirSn = f.Map(d / distM).Normalized();
            angRadius = Math.Asin(radiusM / distM);
            return true;
        }

        /// <summary>The same in SE world: the unit direction from the camera to the body and its angular radius.</summary>
        public static bool ViewSe(Vec3 camRel, Vec3 bodyRel, double radiusM, out Vec3 dirSe, out double angRadius)
        {
            Vec3 d = bodyRel - camRel;
            double dist = d.Length;
            dirSe = Vec3.Zero;
            angRadius = 0;
            if (!(dist > radiusM) || !(radiusM > 0)) return false;
            dirSe = d / dist;
            angRadius = Math.Asin(radiusM / dist);
            return true;
        }

        /// <summary>A direction in a camera frame: (right, up, forward) components.</summary>
        public static Vec3 InCamera(Vec3 dir, Vec3 right, Vec3 up, Vec3 forward)
        {
            return new Vec3(Vec3.Dot(dir, right), Vec3.Dot(dir, up), Vec3.Dot(dir, forward));
        }

        /// <summary>The angle between two directions, degrees (0..180; NaN for a zero vector).</summary>
        public static double AngleDeg(Vec3 a, Vec3 b)
        {
            double la = a.Length, lb = b.Length;
            if (!(la > 0) || !(lb > 0)) return double.NaN;
            double c = Vec3.Dot(a, b) / (la * lb);
            if (c > 1) c = 1; else if (c < -1) c = -1;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>
 /// The light on a disc point (format sky.disc): <paramref name="a"/>, <paramref name="b"/> the point on the unit disc
 /// along its right and up axes, <paramref name="toBody"/> the unit direction from the camera to the body, the disc
 /// axes <paramref name="right"/>, <paramref name="up"/> (both unit, perpendicular to it), <paramref name="toSun"/> the
 /// unit direction to the sun. The sphere's normal there faces the camera; the light is max(floor, n. s).
 /// </summary>
        public static double Light(double a, double b, Vec3 toBody, Vec3 right, Vec3 up, Vec3 toSun)
        {
            double r2 = a * a + b * b;
            double z = r2 < 1.0 ? Math.Sqrt(1.0 - r2) : 0.0;
            Vec3 n = right * a + up * b - toBody * z;
            double l = Vec3.Dot(n, toSun);
            double floor = S15Conventions.Sky.AmbientPermille / 1000.0;
            return l > floor ? l : floor;
        }

        /// <summary>The limb weight at disc radius <paramref name="r"/> (0..1): 0 inside LimbPermille, rising to 1 at the edge.</summary>
        public static double LimbWeight(double r)
        {
            double r0 = S15Conventions.Sky.LimbPermille / 1000.0;
            if (r <= r0) return 0;
            if (r >= 1) return 1;
            double t = (r - r0) / (1 - r0);
            return t * t * (3 - 2 * t);
        }
    }

    /// <summary>The flat palettes of the sky bodies.</summary>
    public static class S15Palette
    {
        public static int Of(string subtype)
        {
            if (!string.IsNullOrEmpty(subtype))
                for (int i = 1; i < S15Conventions.Palettes.Length; i++)
                    if (subtype.StartsWith(S15Conventions.Palettes[i].Prefix, StringComparison.OrdinalIgnoreCase)) return S15Conventions.Palettes[i].Id;
            return 0;
        }

        public static S15Conventions.PaletteRow Row(int id)
        {
            foreach (S15Conventions.PaletteRow p in S15Conventions.Palettes) if (p.Id == id) return p;
            return S15Conventions.Palettes[0];
        }
    }

    /// <summary>The colour checks of the B rows (T5's: per channel and Rec. 709 luma on the sRGB bytes).</summary>
    public static class S15Colour
    {
        public static double Luma(double r, double g, double b) { return 0.2126 * r + 0.7152 * g + 0.0722 * b; }

        public static bool Within(double r, double g, double b, BandRgb target, out double dMax, out double dL)
        {
            dMax = Math.Max(Math.Abs(r - target.R), Math.Max(Math.Abs(g - target.G), Math.Abs(b - target.B)));
            dL = Math.Abs(Luma(r, g, b) - Luma(target.R, target.G, target.B));
            return dMax <= S15Conventions.Colour.TolChannel && dL <= S15Conventions.Colour.TolLuma;
        }
    }
}
