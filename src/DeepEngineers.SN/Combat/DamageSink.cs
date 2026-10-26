using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The C8 producer (CONTRACT §5.1, §5.4 C8, §4.2): records built by <see cref="DamageRecordBuilder"/> go through
 /// <see cref="OverflowQueue{T}.WriteOrQueue"/> (the queue is flushed first, so order is kept; at most 4,096 wait,
 /// the oldest dropped and counted: C7 damageDropped); <see cref="Flush"/> every frame. Capture mode (the self-check)
 /// keeps the full records local and never writes C8 (the Player record would hurt the SE character).
 /// Main thread only.
 /// </summary>
    internal sealed class DamageSink
    {
        private readonly EntryRing _c8;
        private readonly OverflowQueue<DamageRecord> _queue = new OverflowQueue<DamageRecord>(CombatConventions.Limits.OverflowRecords);
        public readonly DamageRecordBuilder Builder;
        public bool CaptureMode;
        public readonly List<DamageRecord> Captured = new List<DamageRecord>();
        public long Built, Written, Queued, Corrupt, Captures;
        public long ZeroAmount, TypeNotForwarded, PlayerNotAtCharacter, Refused, Gate, NoDealerPoint, RadiusDropped, NoCharacter;
        public readonly long[] SkippedByType = new long[32];

        public DamageSink(EntryRing c8, uint generation)
        {
            _c8 = c8;
            Builder = new DamageRecordBuilder(generation);
        }

        public bool HasRing { get { return _c8 != null; } }
        public long Dropped { get { return _queue.Dropped; } }
        public int Depth { get { return _queue.Count; } }

        public void Count(S6Skip s, int type)
        {
            switch (s)
            {
                case S6Skip.Gate: Gate++; break;
                case S6Skip.ZeroAmount: ZeroAmount++; break;
                case S6Skip.TypeNotForwarded: TypeNotForwarded++; if (type >= 0 && type < SkippedByType.Length) SkippedByType[type]++; break;
                case S6Skip.Refused: Refused++; break;
                case S6Skip.PlayerNotAtCharacter: PlayerNotAtCharacter++; break;
                case S6Skip.NoCharacter: NoCharacter++; break;
                case S6Skip.RadiusMutant: RadiusDropped++; break;
            }
        }

        /// <summary>Builds one record and writes it (or captures it). Returns the record and whether it reached the ring now.</summary>
        public DamageRecord Emit(ref HitInput h, uint worldEpoch, out bool queued)
        {
            queued = false;
            DamageRecord r = Builder.Build(ref h, worldEpoch, CaptureMode);
            Built++;
            if (CaptureMode)
            {
                Captures++;
                Captured.Add(r);
                return r;
            }
            if (_c8 == null) { queued = true; return r; }
            RingWriteStatus st = _queue.WriteOrQueue(_c8, r);
            if (st == RingWriteStatus.Ok) Written++;
            else
            {
                queued = true;
                Queued++;
                if (st == RingWriteStatus.Corrupt)
                {
                    Corrupt++;
                    DeLog.WarnOnce("s6.c8corrupt", "DE.S6 C8 ring counters impossible (the consumer broke the protocol?): records wait in the overflow queue");
                }
            }
            return r;
        }

        /// <summary>
 /// S11 hook: an S11 record (unit 17-22, filled by) takes the
 /// next dedupSeq of this generation and the same overflow queue and ring as S6's records, so SE's one dedup window covers
 /// both. Never in capture mode (the self-check's records stay local). False = not written (capture mode).
 /// </summary>
        public bool EmitPrepared(ref DamageRecord r, uint worldEpoch, out bool queued)
        {
            queued = false;
            if (CaptureMode) return false;
            r.dedupSeq = Builder.NextSeq();
            r.snGeneration = Builder.Generation;
            r.worldEpoch = worldEpoch;
            Built++;
            if (_c8 == null) { queued = true; return true; }
            RingWriteStatus st = _queue.WriteOrQueue(_c8, r);
            if (st == RingWriteStatus.Ok) Written++;
            else
            {
                queued = true;
                Queued++;
                if (st == RingWriteStatus.Corrupt) Corrupt++;
            }
            return true;
        }

        /// <summary>Every frame: queued records into the ring while there is room (order kept).</summary>
        public void Flush()
        {
            if (_c8 == null || _queue.Count == 0) return;
            Written += _queue.FlushTo(_c8);
        }
    }
}
