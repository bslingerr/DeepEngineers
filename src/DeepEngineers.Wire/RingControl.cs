using System;
using System.Threading;

namespace DeepEngineers.Wire
{
    /// <summary>Who produces or consumes a ring (CONTRACT §5.1, ARCHITECTURE §3.2 thread ownership).</summary>
    public enum Party
    {
        None = 0,
        Se = 1,
        Sn = 2,
        /// <summary>The Python test harness (C11, dev builds only).</summary>
        Harness = 3,
    }

    /// <summary>Static facts about one ring of the control page.</summary>
    public sealed class RingDef
    {
        public RingIndex Index { get; }
        public RegionId Region { get; }
        /// <summary>The producer. C11c (CtlToSn) has two possible producers, exclusive by devControl: the harness
 /// (bit 0) or the SE chat relay (bit 1); Producer names the harness, ProducerAlt the SE relay.</summary>
        public Party Producer { get; }
        public Party ProducerAlt { get; }
        public Party Consumer { get; }

        public RingDef(RingIndex index, RegionId region, Party producer, Party producerAlt, Party consumer)
        {
            Index = index; Region = region; Producer = producer; ProducerAlt = producerAlt; Consumer = consumer;
        }
    }

    /// <summary>
 /// The ring-control page (CONTRACT §5.2): ring r has its head (producer, u64) at 0x1000 + r·0x80 and its tail
 /// (consumer, u64) at 0x1000 + r·0x80 + 0x40, on separate 64-byte lines. Counters are monotonically increasing
 /// byte (or record, for C8) counts. A side writes only the words it owns: the producer its head, the consumer its
 /// tail. Nobody ever zeroes a head.
 /// </summary>
    public static unsafe class RingControl
    {
        private static readonly RingDef[] Defs =
        {
            new RingDef(RingIndex.C3SeModels,    RegionId.C3SeModels,    Party.Se,      Party.None, Party.Sn),
            new RingDef(RingIndex.C4SeParts,     RegionId.C4SeParts,     Party.Se,      Party.None, Party.Sn),
            new RingDef(RingIndex.C8SnDamage,    RegionId.C8SnDamage,    Party.Sn,      Party.None, Party.Se),
            new RingDef(RingIndex.C12SnStatics,  RegionId.C12SnStatics,  Party.Sn,      Party.None, Party.Se),
            new RingDef(RingIndex.C11aCtlToSe,   RegionId.C11aCtlToSe,   Party.Harness, Party.None, Party.Se),
            new RingDef(RingIndex.C11bCtlFromSe, RegionId.C11bCtlFromSe, Party.Se,      Party.None, Party.Harness),
            new RingDef(RingIndex.C11cCtlToSn,   RegionId.C11cCtlToSn,   Party.Harness, Party.Se,   Party.Sn),
            new RingDef(RingIndex.C11dCtlFromSn, RegionId.C11dCtlFromSn, Party.Sn,      Party.None, Party.Harness),
        };

        public static RingDef Def(RingIndex ring)
        {
            int r = (int)ring;
            if (r < 0 || r >= Defs.Length) throw new ArgumentOutOfRangeException("ring");
            return Defs[r];
        }

        /// <summary>Rings whose tail the party owns (it consumes them).</summary>
        public static RingIndex[] ConsumedBy(Party party)
        {
            var list = new System.Collections.Generic.List<RingIndex>();
            foreach (RingDef d in Defs) if (d.Consumer == party) list.Add(d.Index);
            return list.ToArray();
        }

        /// <summary>Rings whose head the party may own (it produces them; C11c only while its devControl bit is set).</summary>
        public static RingIndex[] ProducedBy(Party party)
        {
            var list = new System.Collections.Generic.List<RingIndex>();
            foreach (RingDef d in Defs) if (d.Producer == party || d.ProducerAlt == party) list.Add(d.Index);
            return list.ToArray();
        }

        public static ulong* HeadPtr(byte* sectionBase, RingIndex ring)
        {
            return (ulong*)(sectionBase + Layout.HeadOffset(ring));
        }

        public static ulong* TailPtr(byte* sectionBase, RingIndex ring)
        {
            return (ulong*)(sectionBase + Layout.TailOffset(ring));
        }

        public static ulong ReadHead(byte* sectionBase, RingIndex ring) { return Volatile.Read(ref *HeadPtr(sectionBase, ring)); }
        public static ulong ReadTail(byte* sectionBase, RingIndex ring) { return Volatile.Read(ref *TailPtr(sectionBase, ring)); }

        /// <summary>Producer only.</summary>
        public static void PublishHead(byte* sectionBase, RingIndex ring, ulong head) { Volatile.Write(ref *HeadPtr(sectionBase, ring), head); }

        /// <summary>Consumer only.</summary>
        public static void PublishTail(byte* sectionBase, RingIndex ring, ulong tail) { Volatile.Write(ref *TailPtr(sectionBase, ring), tail); }

        /// <summary>Pointer to the ring's data region.</summary>
        public static byte* DataPtr(byte* sectionBase, RingIndex ring)
        {
            return sectionBase + Layout.Region(Layout.RingRegion(ring)).Offset;
        }

        /// <summary>Capacity in bytes (byte rings) or records (C8).</summary>
        public static long Capacity(RingIndex ring)
        {
            RegionDef g = Layout.Region(Layout.RingRegion(ring));
            return g.Kind == RegionKind.RecordRing ? g.Size / g.RecordSize : g.Size;
        }
    }
}
