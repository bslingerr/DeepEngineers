using System;
using System.Collections.Generic;
using C2Events = DeepEngineers.Wire.S7Conventions.C2Events;
using EventCaps = DeepEngineers.Wire.S7Conventions.EventCaps;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventLimits = DeepEngineers.Wire.S7Conventions.EventLimits;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The SE event buffer and the 8-tick C2 window (CONTRACT §5.4 "C2 effect events", <c>format event.window</c>,
 /// <c>event.priority</c>; "Window and caps"). Sources append to a fixed per-tick candidate buffer with
 /// <see cref="TryAdd"/> (any thread; O(1); never a growing queue: a full buffer drops and counts). At the C2 write
 /// <see cref="EndTick"/> selects at most <see cref="S7Conventions.EventCaps"/> events per type and
 /// <see cref="S7Conventions.C2Events.MaxPerTick"/> in all, by priority (Stop rows, Explosion, HullBreak, …, Thruster,
 /// BulletStreak; newest first within a type), drops and counts the rest per type, gives the accepted ones consecutive
 /// <c>seq</c> (kept in their raise order) and keeps every accepted event of the last <see cref="WindowTicks"/> ticks
 /// (≤ 1,024 rows). A new <see cref="Stream"/> restarts seq at 1. No allocation after construction.
 /// Seeded failures: <see cref="WindowTicks"/> = 1 (S7.M17), <see cref="CapsOff"/> (S7.M20).
 /// </summary>
    public sealed class S7EventWindow
    {
        /// <summary>Candidates one tick can hold before the caps (raised beyond: dropped and counted).</summary>
        public const int CandidateCapacity = 1024;

        private readonly object _lock = new object();
        private readonly S7EventRow[] _cand = new S7EventRow[CandidateCapacity];
        private readonly bool[] _take = new bool[CandidateCapacity];
        private int _candCount;

        private readonly S7EventRow[] _rows = new S7EventRow[C2Events.MaxRows];
        private readonly ulong[] _rowTick = new ulong[C2Events.MaxRows];
        private int _head, _count;
        private uint _nextSeq = 1;
        private uint _stream;
        private bool _streamSet;
        private readonly int[] _typeTaken = new int[S7EventRules.TypeSlots];

        /// <summary>Window length in ticks (8; S7.M17 sets 1).</summary>
        public int WindowTicks = C2Events.WindowTicks;
        /// <summary>S7.M20: every raised event accepted (up to the candidate buffer and the 1,024-row window).</summary>
        public bool CapsOff;

        // counters (cumulative since construction unless noted)
        public readonly long[] Raised = new long[S7EventRules.TypeSlots];
        public readonly long[] Accepted = new long[S7EventRules.TypeSlots];
        public readonly long[] Dropped = new long[S7EventRules.TypeSlots];
        /// <summary>The last closed tick only (the stress check: exactly 128 accepted, the rest dropped).</summary>
        public readonly int[] LastRaised = new int[S7EventRules.TypeSlots];
        public readonly int[] LastAccepted = new int[S7EventRules.TypeSlots];
        public readonly int[] LastDropped = new int[S7EventRules.TypeSlots];
        public int LastRaisedTotal, LastAcceptedTotal, LastDroppedTotal;
        /// <summary>Rows refused by <see cref="TryAdd"/> as malformed (type, NaN).</summary>
        public long Invalid;
        /// <summary>Cumulative cap drops in this stream (<c>c2ext.eventsDropped</c>).</summary>
        public uint EventsDropped { get; private set; }
        public ulong LastTick { get; private set; }
        public long Ticks { get; private set; }

        /// <summary>The current stream id (<c>c2ext.eventStream</c>).</summary>
        public uint Stream { get { lock (_lock) return _stream; } }

        /// <summary>Rows in the window now.</summary>
        public int Count { get { lock (_lock) return _count; } }

        /// <summary><c>eventFirstSeq</c>: the oldest row's seq, or the next seq when the window is empty.</summary>
        public uint FirstSeq { get { lock (_lock) return _count > 0 ? _rows[_head].seq : _nextSeq; } }

        /// <summary>Candidates waiting for <see cref="EndTick"/>.</summary>
        public int Pending { get { lock (_lock) return _candCount; } }

        /// <summary>
 /// Sets the stream; a changed value (or the first) clears the window and the candidates and restarts seq at 1 and
 /// the per-stream dropped count at 0. Returns true when it changed.
 /// </summary>
        public bool SetStream(uint stream)
        {
            lock (_lock)
            {
                if (_streamSet && stream == _stream) return false;
                _streamSet = true;
                _stream = stream;
                _head = _count = 0;
                _candCount = 0;
                _nextSeq = 1;
                EventsDropped = 0;
                return true;
            }
        }

        /// <summary>Clears the window and the candidates; seq continues (a world unload keeps the stream).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _head = _count = 0;
                _candCount = 0;
            }
        }

        /// <summary>
 /// Appends one raised event to this tick's candidates (any thread). False when the row is malformed (counted
 /// <see cref="Invalid"/>) or the candidate buffer is full (counted dropped for its type). <c>seq</c> is assigned at
 /// <see cref="EndTick"/>.
 /// </summary>
        public bool TryAdd(in S7EventRow row)
        {
            if (!row.IsValid)
            {
                lock (_lock) Invalid++;
                return false;
            }
            lock (_lock)
            {
                Raised[row.type]++;
                if (_candCount >= CandidateCapacity)
                {
                    Dropped[row.type]++;
                    EventsDropped++;
                    _overflowRaised[row.type]++;
                    return false;
                }
                _cand[_candCount++] = row;
                return true;
            }
        }

        private readonly int[] _overflowRaised = new int[S7EventRules.TypeSlots];

        /// <summary>
 /// Closes the tick: selects this tick's candidates by priority and caps, appends the accepted ones with consecutive
 /// seq (rows without a <c>tickLow</c> get the low 32 bits of <paramref name="tick"/>), counts the rest dropped, and
 /// evicts rows older than <c>tick − (WindowTicks − 1)</c>. Returns the number accepted.
 /// </summary>
        public int EndTick(ulong tick)
        {
            lock (_lock)
            {
                LastTick = tick;
                Ticks++;
                Array.Clear(LastRaised, 0, LastRaised.Length);
                Array.Clear(LastAccepted, 0, LastAccepted.Length);
                Array.Clear(LastDropped, 0, LastDropped.Length);
                Array.Clear(_typeTaken, 0, _typeTaken.Length);
                for (int t = 0; t < _overflowRaised.Length; t++)
                {
                    LastRaised[t] += _overflowRaised[t];
                    LastDropped[t] += _overflowRaised[t];
                    _overflowRaised[t] = 0;
                }
                int n = _candCount;
                for (int i = 0; i < n; i++)
                {
                    _take[i] = false;
                    LastRaised[_cand[i].type]++;
                }

                int taken = 0;
                if (CapsOff)
                {
                    for (int i = 0; i < n; i++) _take[i] = true;
                    taken = n;
                }
                else
                {
                    // rank 0: Stop rows of any type; ranks 1..13: the priority order; newest first within a class
                    for (int rank = 0; rank <= S7EventRules.PriorityOrder.Length && taken < C2Events.MaxPerTick; rank++)
                    {
                        for (int i = n - 1; i >= 0 && taken < C2Events.MaxPerTick; i--)
                        {
                            if (_take[i]) continue;
                            byte type = _cand[i].type;
                            if (S7EventRules.PriorityRank(type, _cand[i].flags) != rank) continue;
                            if (_typeTaken[type] >= EventCaps.Of(type)) continue;
                            _take[i] = true;
                            _typeTaken[type]++;
                            taken++;
                        }
                    }
                }

                // evict: the window holds ticks [tick - (WindowTicks - 1), tick]; the row capacity bounds CapsOff
                int keep = Math.Max(1, WindowTicks);
                while (_count > 0 && _rowTick[_head] + (ulong)keep <= tick)
                {
                    _head = (_head + 1) % _rows.Length;
                    _count--;
                }
                uint tickLow = (uint)tick;
                for (int i = 0; i < n; i++)
                {
                    byte type = _cand[i].type;
                    if (!_take[i])
                    {
                        Dropped[type]++;
                        LastDropped[type]++;
                        EventsDropped++;
                        continue;
                    }
                    if (_count == _rows.Length)
                    {
                        _head = (_head + 1) % _rows.Length;
                        _count--;
                    }
                    int slot = (_head + _count) % _rows.Length;
                    S7EventRow r = _cand[i];
                    r.seq = _nextSeq++;
                    if (_nextSeq == 0) _nextSeq = 1; // seq 0 is never used
                    if (r.tickLow == 0) r.tickLow = tickLow;
                    _rows[slot] = r;
                    _rowTick[slot] = tick;
                    _count++;
                    Accepted[type]++;
                    LastAccepted[type]++;
                }
                _candCount = 0;
                LastRaisedTotal = 0; LastAcceptedTotal = 0; LastDroppedTotal = 0;
                for (int t = 0; t < S7EventRules.TypeSlots; t++)
                {
                    LastRaisedTotal += LastRaised[t];
                    LastAcceptedTotal += LastAccepted[t];
                    LastDroppedTotal += LastDropped[t];
                }
                return taken;
            }
        }

        /// <summary>Copies the window's rows in seq order into <paramref name="dst"/>; returns the count (≤ dst.Length).</summary>
        public int CopyRows(S7EventRow[] dst)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            lock (_lock)
            {
                int n = Math.Min(_count, dst.Length);
                for (int i = 0; i < n; i++) dst[i] = _rows[(_head + i) % _rows.Length];
                return n;
            }
        }

        /// <summary>
 /// The header extension for this window (<c>Events</c> set; <paramref name="extraFlags"/> may add <c>Lights</c>),
 /// with <paramref name="rowCount"/> = what <see cref="CopyRows"/> returned.
 /// </summary>
        public C2Ext Extension(ulong tick, int rowCount, byte extraFlags, uint lightsFound)
        {
            lock (_lock)
            {
                uint first = _count > 0 ? _rows[_head].seq : _nextSeq;
                return C2Ext.For(tick, (byte)(S7Conventions.C2ExtFlags.Events | extraFlags), rowCount, first, _stream, EventsDropped, lightsFound);
            }
        }

        /// <summary>The oldest tick still in the window (0 when empty).</summary>
        public ulong OldestTick { get { lock (_lock) return _count > 0 ? _rowTick[_head] : 0; } }
    }

    /// <summary>What a sustain observation asks the caller to send (<c>format event.sustain</c>).</summary>
    public enum SustainEmit : byte
    {
        /// <summary>Nothing this tick.</summary>
        None = 0,
        /// <summary>The first row of a new sustain.</summary>
        Start = 1,
        /// <summary>|Δ intensity| ≥ SustainStepPermille / 1,000 since the last row.</summary>
        Step = 2,
        /// <summary>An attached move &gt; 0.05 m since the last row.</summary>
        Move = 3,
        /// <summary>The source flags changed (Underwater, Surface, Hit, …) since the last row.</summary>
        Flags = 4,
        /// <summary>SustainRefreshTicks since the last row.</summary>
        Refresh = 5,
    }

    /// <summary>
 /// The sustain rule (CONTRACT <c>format event.sustain</c>): per (entityId, key) a row on start, on a change of intensity
 /// ≥ 0.05, on an attached move &gt; 0.05 m (and on a change of the source flags), else every 15 ticks; rows carry
 /// <c>Sustain</c> and <c>lifetimeMs</c> = 500; a sustain not observed in a tick ends with a <c>Stop</c> row
 /// (<see cref="CollectStops"/>). Fixed capacity, no allocation per observation (the index dictionary grows to its
 /// capacity once). Update thread. <c>refresh: false</c> in <see cref="Observe"/> is S7.M14 (start rows only).
 /// <para>Step lag: per sustain, the tick its source intensity first differed by ≥ 0.05
 /// from the last row's is kept until a row carries it; <see cref="TakeStepLagMax"/> answers the largest lag (closed
 /// steps, and steps still waiting at <paramref name="tick"/>) since the previous call: the <c>s7.fx</c> row's
 /// <c>stepLagMaxTicks</c>, which S7.N4.thruster.log judges (≤ 1 tick).</para>
 /// </summary>
    public sealed class S7SustainTracker
    {
        public const int Capacity = 2048;
        /// <summary>|Δ intensity| that sends a row.</summary>
        public const float StepIntensity = EventLimits.SustainStepPermille / 1000f;
        /// <summary>A move of the attached point that sends a row (m).</summary>
        public const float MoveM = 0.05f;

        private struct Key : IEquatable<Key>
        {
            public readonly ulong Entity, K;
            public Key(ulong entity, ulong k) { Entity = entity; K = k; }
            public bool Equals(Key o) { return Entity == o.Entity && K == o.K; }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode() { return (Entity.GetHashCode() * 397) ^ K.GetHashCode(); }
        }

        private struct Slot
        {
            public bool Live;
            public uint SeenEpoch;
            public ulong LastRowTick;
            /// <summary>The tick the source intensity first differed by ≥ StepIntensity from <see cref="Last"/> (0 = none waiting).</summary>
            public ulong StepSince;
            public S7EventRow Last;
        }

        private readonly Slot[] _slots = new Slot[Capacity];
        private readonly Dictionary<Key, int> _index = new Dictionary<Key, int>(Capacity);
        private readonly int[] _free = new int[Capacity];
        private int _freeCount;
        private uint _epoch = 1;

        public long Starts, Steps, Moves, FlagChanges, Refreshes, Stops, Overflow;
        /// <summary>Largest closed step lag (ticks from the source's ≥ 0.05 change to the row carrying it) since <see cref="TakeStepLagMax"/>.</summary>
        private ulong _stepLagMax;
        /// <summary>Steps whose lag was measured (closed), cumulative.</summary>
        public long StepLagSamples;

        public S7SustainTracker()
        {
            for (int i = 0; i < Capacity; i++) _free[i] = Capacity - 1 - i;
            _freeCount = Capacity;
        }

        /// <summary>Live sustains.</summary>
        public int Count { get { return _index.Count; } }

        /// <summary>Live sustains per event type into <paramref name="dst"/> (indexed by type, cleared first; rows, 1 Hz).</summary>
        public void CountByType(int[] dst)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            Array.Clear(dst, 0, dst.Length);
            for (int i = 0; i < Capacity; i++)
            {
                if (!_slots[i].Live) continue;
                int t = _slots[i].Last.type;
                if (t >= 0 && t < dst.Length) dst[t]++;
            }
        }

        /// <summary>Starts a tick's observations (sustains not observed until <see cref="CollectStops"/> end).</summary>
        public void BeginTick() { _epoch++; if (_epoch == 0) _epoch = 1; }

        /// <summary>
 /// One live source this tick. <paramref name="row"/> carries type, entityId, key, pos, dir, intensity, size,
 /// subtype and the source flags; on a non-None answer it is completed (Sustain flag, lifetimeMs 500, tickLow) and
 /// is the row to raise. <paramref name="refresh"/> false (S7.M14): only the Start row is ever sent.
 /// </summary>
        public SustainEmit Observe(ref S7EventRow row, ulong tick, bool refresh = true)
        {
            var k = new Key(row.entityId, row.key);
            row.flags = (byte)((row.flags & S7EventRules.SourceFlagMask) | EventFlags.Sustain);
            row.lifetimeMs = (ushort)EventLimits.SustainLifetimeMs;
            row.seq = 0;
            if (row.tickLow == 0) row.tickLow = (uint)tick;
            int i;
            if (!_index.TryGetValue(k, out i))
            {
                if (_freeCount == 0) { Overflow++; return SustainEmit.None; }
                i = _free[--_freeCount];
                _index[k] = i;
                _slots[i].Live = true;
                _slots[i].SeenEpoch = _epoch;
                _slots[i].LastRowTick = tick;
                _slots[i].Last = row;
                Starts++;
                return SustainEmit.Start;
            }
            _slots[i].SeenEpoch = _epoch;
            S7EventRow last = _slots[i].Last;
            SustainEmit e = SustainEmit.None;
            bool stepDue = Math.Abs(row.intensity - last.intensity) >= StepIntensity - 1e-6f;
            if (stepDue && _slots[i].StepSince == 0) _slots[i].StepSince = tick == 0 ? 1 : tick;
            else if (!stepDue) _slots[i].StepSince = 0; // the source came back within 0.05 of the last row: nothing waits
            if (refresh)
            {
                if (stepDue) e = SustainEmit.Step;
                else if (row.Attached && Dist2(row.pos, last.pos) > MoveM * MoveM) e = SustainEmit.Move;
                else if (row.flags != last.flags) e = SustainEmit.Flags;
                else if (tick >= _slots[i].LastRowTick + (ulong)EventLimits.SustainRefreshTicks) e = SustainEmit.Refresh;
            }
            if (e == SustainEmit.None) return e;
            if (_slots[i].StepSince != 0)
            {
                // any row carries the current intensity: the step that waited is answered now
                ulong lag = tick >= _slots[i].StepSince ? tick - _slots[i].StepSince : 0;
                if (lag > _stepLagMax) _stepLagMax = lag;
                StepLagSamples++;
                _slots[i].StepSince = 0;
            }
            switch (e)
            {
                case SustainEmit.Step: Steps++; break;
                case SustainEmit.Move: Moves++; break;
                case SustainEmit.Flags: FlagChanges++; break;
                default: Refreshes++; break;
            }
            _slots[i].LastRowTick = tick;
            _slots[i].Last = row;
            return e;
        }

        /// <summary>
 /// Ends the tick: every live sustain not observed since <see cref="BeginTick"/> is removed and its Stop row (the
 /// last row with <c>Stop</c>, intensity 0) written to <paramref name="dst"/>. Returns the number written; stops that
 /// do not fit stay for the next call.
 /// </summary>
        public int CollectStops(ulong tick, S7EventRow[] dst)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            int n = 0;
            for (int i = 0; i < Capacity && n < dst.Length; i++)
            {
                if (!_slots[i].Live || _slots[i].SeenEpoch == _epoch) continue;
                S7EventRow r = _slots[i].Last;
                r.flags = (byte)((r.flags & S7EventRules.SourceFlagMask) | EventFlags.Sustain | EventFlags.Stop);
                r.intensity = 0f;
                r.seq = 0;
                r.tickLow = (uint)tick;
                r.lifetimeMs = (ushort)EventLimits.SustainLifetimeMs;
                dst[n++] = r;
                Remove(i);
                Stops++;
            }
            return n;
        }

        /// <summary>
 /// The largest step lag since the previous call (ticks): closed steps, and steps of live sustains still waiting at
 /// <paramref name="tick"/> (S7.M14 sends start rows only, so its steps wait and the lag grows); then the closed
 /// maximum is reset. 1 Hz (scans the slots).
 /// </summary>
        public ulong TakeStepLagMax(ulong tick)
        {
            ulong m = _stepLagMax;
            for (int i = 0; i < Capacity; i++)
            {
                if (!_slots[i].Live || _slots[i].StepSince == 0 || tick < _slots[i].StepSince) continue;
                ulong open = tick - _slots[i].StepSince;
                if (open > m) m = open;
            }
            _stepLagMax = 0;
            return m;
        }

        /// <summary>Forgets every sustain without Stop rows (a new stream: Subnautica resets too).</summary>
        public void Clear()
        {
            _index.Clear();
            for (int i = 0; i < Capacity; i++) _slots[i] = default(Slot);
            for (int i = 0; i < Capacity; i++) _free[i] = Capacity - 1 - i;
            _freeCount = Capacity;
        }

        private void Remove(int i)
        {
            _index.Remove(new Key(_slots[i].Last.entityId, _slots[i].Last.key));
            _slots[i] = default(Slot);
            _free[_freeCount++] = i;
        }

        private static float Dist2(F32x3 a, F32x3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
