using System;
using System.Collections.Generic;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// Re-synchronisation after a restart (CONTRACT §5.2, §6 "Restart detection" and "Link epoch"): when a consumer sees
 /// a new peer generation or a new link epoch, it sets its tail:= the current head of every ring it consumes,
 /// dropping in-flight data, and never writes the producer's word. Producers continue from the head they find.
 /// Also: region checksums (FNV-1a 64) so tests, the spy and the mutants can prove "nothing written".
 /// </summary>
    public static unsafe class Resync
    {
        /// <summary>Rings Subnautica consumes (creator rule 3: C3, C4, C11c).</summary>
        public static readonly RingIndex[] SnConsumes = { RingIndex.C3SeModels, RingIndex.C4SeParts, RingIndex.C11cCtlToSn };
        /// <summary>Rings SE consumes (C8, C12, C11a).</summary>
        public static readonly RingIndex[] SeConsumes = { RingIndex.C8SnDamage, RingIndex.C12SnStatics, RingIndex.C11aCtlToSe };
        /// <summary>Rings the harness consumes (C11b, C11d).</summary>
        public static readonly RingIndex[] HarnessConsumes = { RingIndex.C11bCtlFromSe, RingIndex.C11dCtlFromSn };

        public static RingIndex[] ConsumedBy(Party party)
        {
            switch (party)
            {
                case Party.Sn: return (RingIndex[])SnConsumes.Clone();
                case Party.Se: return (RingIndex[])SeConsumes.Clone();
                case Party.Harness: return (RingIndex[])HarnessConsumes.Clone();
                default: return new RingIndex[0];
            }
        }

        /// <summary>tail:= head for each ring (consumer words only; heads are only read).</summary>
        public static void ConsumerTailsToHeads(byte* sectionBase, RingIndex[] rings)
        {
            if (sectionBase == null) throw new ArgumentNullException("sectionBase");
            foreach (RingIndex r in rings)
            {
                ulong h = RingControl.ReadHead(sectionBase, r);
                RingControl.PublishTail(sectionBase, r, h);
            }
        }

        public static void ConsumerTailsToHeads(byte* sectionBase, Party consumer)
        {
            ConsumerTailsToHeads(sectionBase, ConsumedBy(consumer));
        }

        // -------------------------------------------------------------------------------------------- checksums

 /// <summary>FNV-1a 64 over a byte range.</summary>
        public static ulong RangeChecksum(byte* start, long length)
        {
            return LayoutHash.Fnv1a64(start, length);
        }

        /// <summary>FNV-1a 64 over one region (the region must be inside the mapped size).</summary>
        public static ulong RegionChecksum(byte* sectionBase, RegionId id)
        {
            RegionDef g = Layout.Region(id);
            return LayoutHash.Fnv1a64(sectionBase + g.Offset, g.Size);
        }

        /// <summary>
 /// Checksums of every region of the map that lies inside <paramref name="mappedSize"/> (keyed by region id),
 /// plus key 0xFFFFFFFF for the whole mapped range. A section of another size or layout is covered by the
 /// whole-range entry.
 /// </summary>
        public static Dictionary<uint, ulong> Snapshot(byte* sectionBase, long mappedSize)
        {
            var map = new Dictionary<uint, ulong>();
            foreach (RegionDef g in Layout.Regions)
                if (g.Offset + g.Size <= mappedSize) map[(uint)g.Id] = LayoutHash.Fnv1a64(sectionBase + g.Offset, g.Size);
            map[WholeSectionKey] = LayoutHash.Fnv1a64(sectionBase, mappedSize);
            return map;
        }

        public const uint WholeSectionKey = 0xFFFFFFFFu;

        /// <summary>Names of the entries that differ (empty when equal).</summary>
        public static List<string> Diff(Dictionary<uint, ulong> before, Dictionary<uint, ulong> after)
        {
            var changed = new List<string>();
            foreach (var kv in before)
            {
                ulong v;
                if (!after.TryGetValue(kv.Key, out v) || v != kv.Value)
                    changed.Add(kv.Key == WholeSectionKey ? "whole section" : Layout.Region((RegionId)kv.Key).Name);
            }
            foreach (var kv in after)
                if (!before.ContainsKey(kv.Key)) changed.Add(kv.Key == WholeSectionKey ? "whole section" : Layout.Region((RegionId)kv.Key).Name);
            return changed;
        }
    }

    /// <summary>What a side concluded from the peer's header block.</summary>
    public enum PeerChange
    {
        /// <summary>Same PID and generation as before (or still absent).</summary>
        None = 0,
        /// <summary>A peer appeared, or its PID or generation changed: re-synchronise consumer tails, resend.</summary>
        NewPeer = 1,
        /// <summary>The peer's PID went to 0.</summary>
        Gone = 2,
    }

    /// <summary>
 /// Restart detection (CONTRACT §6): each side writes its PID and bumps its generation on every attach; the peer
 /// treats a changed PID or generation as a new peer. A heartbeat that merely changes is never proof of liveness
 /// (S0.M5c); process liveness is ProcessWatch's job.
 /// </summary>
    public sealed class PeerWatch
    {
        public uint Pid { get; private set; }
        public uint Generation { get; private set; }
        public bool Known { get; private set; }

        /// <summary>What <see cref="Observe"/> would return, without recording anything.</summary>
        public PeerChange Peek(uint pid, uint generation)
        {
            if (pid == 0) return Known ? PeerChange.Gone : PeerChange.None;
            if (Known && pid == Pid && generation == Generation) return PeerChange.None;
            return PeerChange.NewPeer;
        }

        public PeerChange Observe(uint pid, uint generation)
        {
            if (pid == 0)
            {
                if (!Known) return PeerChange.None;
                Known = false; Pid = 0; Generation = 0;
                return PeerChange.Gone;
            }
            if (Known && pid == Pid && generation == Generation) return PeerChange.None;
            Known = true; Pid = pid; Generation = generation;
            return PeerChange.NewPeer;
        }

        public void Forget() { Known = false; Pid = 0; Generation = 0; }
    }

    /// <summary>Change detector for an epoch word (link epoch, world epoch, hello epoch).</summary>
    public sealed class EpochWatch
    {
        public uint Value { get; private set; }
        public bool Known { get; private set; }

        /// <summary>True when the epoch differs from the last observed value (the first observation counts as a change).</summary>
        public bool Observe(uint epoch)
        {
            if (Known && epoch == Value) return false;
            Known = true; Value = epoch;
            return true;
        }

        public void Forget() { Known = false; Value = 0; }
    }
}
