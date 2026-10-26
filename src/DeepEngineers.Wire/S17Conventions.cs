using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S17 "Subnautica's small fish feed the SE character" (docs/S17-BRIEF.md; CONTRACT §5.4 "S17 conventions"). Layout 1.0
 // is unchanged (hash 0x4f3a35f6fc1eaf31): Subnautica fills the C9 SnCreatures table (laid out since S0, never written
 // before S17) with the nearby fish of the kind table, C9 flags bit 8 = Catchable (bits 0-1 keep the layout's meaning);
 // SE's catch request travels in the C5 SeState pad after S15's block (0xCC-0xEB), Subnautica's answer in the C7
 // SnStatus pad after S14's block (0xC8-0xD7), version byte 0 = absent. One request in flight, a sequence number per
 // request, so each side acts once per request without a ring. Pinned on its own by the canonical text below (a changed
 // number changes the hash and turns S17ConventionsTests red). Python twin: tools/s17/s17_ref.py (text, hash, codec,
 // aim rule, vectors in tests/s17/vectors/). The kinds are the edible fish of the S8a spawn table (S8aBiomes
 // "spawn.table"; Mesmer is not edible in Subnautica); S11's tables hold no small fish, so they are not the source.
 // =================================================================================================================

 /// <summary>One catchable kind: the Subnautica TechType (name and value) and the world mod's items.</summary>
    public struct S17Kind
    {
        public string Name, Display;
        public int TechType;
        public S17Kind(string name, int techType, string display) { Name = name; TechType = techType; Display = display; }
        public string RawSubtype { get { return "DE_Fish_" + Name + "_Raw"; } }
        public string CookedSubtype { get { return "DE_Fish_" + Name + "_Cooked"; } }
        public string Blueprint { get { return "DE_CookFish_" + Name; } }
    }

    public static class S17Conventions
    {
        public const string Header = "ext s17 fish 2";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S17 conventions".</summary>
        public const ulong PinnedHash = 0x120ee00b7501f416UL;
        public const int CanonicalLines = 70, CanonicalBytes = 5469;

        /// <summary>The fish list Subnautica writes into C9 (format c9.s17).</summary>
        public static class C9
        {
            public const int RowsMax = 32, RadiusCm = 2000, PeriodMs = 100, StaleMs = 1000;
        }

        public static class C9Flags
        {
            /// <summary>Bit 8 (bits 0-1 are the layout's hunting / targetIsMirror).</summary>
            public const uint Catchable = 256;
        }

        /// <summary>The catch rules (format aim, catch.se, catch.sn).</summary>
        public static class Catch
        {
            public const int ReachCm = 250, AimCm = 60, LeadMaxMs = 200, SlackCm = 150, AckTimeoutMs = 1000, UnpublishedMs = 5000, ForgetMs = 30000;
        }

        /// <summary>The C5 request block (SeState 0xCC-0xEB, inside layout 1.0's pad_80 after S15's 0xAC-0xCB).</summary>
        public static class C5
        {
            public const int Offset = 0xCC, Size = 0x20, Version = 1;
        }

        public static class C5Flags
        {
            /// <summary>The request waits for its ack (cleared on the ack or after AckTimeoutMs).</summary>
            public const byte Active = 1;
        }

        /// <summary>The C7 answer block (SnStatus 0xC8-0xD7, inside pad_8c after S14's 0xC4-0xC7).</summary>
        public static class C7
        {
            public const int Offset = 0xC8, Size = 0x10, Version = 1;
        }

        /// <summary>Subnautica's answer to a request.</summary>
        public static class Result
        {
            public const byte Caught = 1, Gone = 2, Moved = 3, Kind = 4, Off = 5;
            public static readonly string[] Names = { "none", "Caught", "Gone", "Moved", "Kind", "Off" };
            public static string Name(int r) { return r >= 0 && r < Names.Length ? Names[r] : "result" + r.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>SE's own refusals (rows s17.catch phase refused / timeout / late).</summary>
        public static class Refusal
        {
            public const int Busy = 1, Again = 2, Timeout = 3, Late = 4, NoItem = 5, NotCaught = 6;
            public static readonly string[] Names = { "none", "Busy", "Again", "Timeout", "Late", "NoItem", "NotCaught" };
            public static string Name(int r) { return r >= 0 && r < Names.Length ? Names[r] : "refusal" + r.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>The items' values (format items): SE's meal pack scale.</summary>
        public static class Food
        {
            /// <summary>Food per TimeS in millionths: raw = MealPack_KelpCrisp (0.00267, SE's smallest meal pack), cooked about twice.</summary>
            public const int RawMicro = 2670, CookedMicro = 5340, TimeS = 30, CookS = 4;
        }

        public static class Item
        {
            public const int MassG = 1500, VolumeMl = 1250, SizeMm = 150;
        }

        /// <summary>The kinds.</summary>
        public static readonly S17Kind[] Kinds =
        {
            new S17Kind("Peeper", 2504, "Peeper"),
            new S17Kind("Bladderfish", 2515, "Bladderfish"),
            new S17Kind("Boomerang", 2510, "Boomerang"),
            new S17Kind("Hoopfish", 2531, "Hoopfish"),
            new S17Kind("Spadefish", 2520, "Spadefish"),
            new S17Kind("Reginald", 2519, "Reginald"),
            new S17Kind("Eyeye", 2513, "Eyeye"),
            new S17Kind("HoleFish", 2501, "Holefish"),
            new S17Kind("Spinefish", 2546, "Spinefish"),
            new S17Kind("GarryFish", 2507, "Garryfish"),
        };

        public static bool TryKind(int techType, out S17Kind k)
        {
            foreach (S17Kind x in Kinds) if (x.TechType == techType) { k = x; return true; }
            k = default(S17Kind);
            return false;
        }

        public static bool TryKindByName(string name, out S17Kind k)
        {
            foreach (S17Kind x in Kinds) if (string.Equals(x.Name, name, StringComparison.Ordinal)) { k = x; return true; }
            k = default(S17Kind);
            return false;
        }

        public static readonly string[][] C5Fields =
        {
            new[] { "version", "0xcc", "1", "u8" },
            new[] { "flags", "0xcd", "1", "u8" },
            new[] { "reserved", "0xce", "2", "u16" },
            new[] { "reqSeq", "0xd0", "4", "u32" },
            new[] { "instanceId", "0xd4", "4", "u32" },
            new[] { "techType", "0xd8", "4", "i32" },
            new[] { "posSn", "0xdc", "12", "f32x3" },
            new[] { "worldEpoch", "0xe8", "4", "u32" },
        };

        public static readonly string[][] C7Fields =
        {
            new[] { "version", "0xc8", "1", "u8" },
            new[] { "result", "0xc9", "1", "u8" },
            new[] { "reserved", "0xca", "2", "u16" },
            new[] { "ackSeq", "0xcc", "4", "u32" },
            new[] { "instanceId", "0xd0", "4", "u32" },
            new[] { "techType", "0xd4", "4", "i32" },
        };

        public static readonly string[] FormatLines =
        {
            "aim the target is the Catchable C9 row nearest the eye with t = (p - cam) . fwd > 0, |(p - cam) - t * fwd| <= AimCm / 100 and |p - eye| <= ReachCm / 100; p = SnToSe(posSn + velSn * min(age, LeadMaxMs)), age = since the C9 read; eye = the character's head; empty hands, on foot (no seat), alive, no SE use object in front (the character detector's UseObject null), C9 fresh (its seq changed within StaleMs), the chart not in transition",
            "c5.s17 SeState 0xcc-0xeb written by SE every tick while S17 runs (version 0 = absent): the last catch request; flags Active while it waits for its ack (cleared on the ack or after AckTimeoutMs); reqSeq starts at a per-session value and grows by one per request",
            "c7.s17 SnStatus 0xc8-0xd7 written by Subnautica every frame while S17 runs (version 0 = absent): the answer to the last request handled, ackSeq = its reqSeq, result = Result",
            "c9.s17 Subnautica writes C9 every PeriodMs: the RowsMax nearest creatures of a kind row within RadiusCm of the camera, dead ones too (none in a containment: WaterParkCreature), nearest first; the overlap's collider buffer grows (up to 4096) when a scan fills it; instanceId = GameObject.GetInstanceID as u32, techType, posSn, velSn (its Rigidbody), aggression 0, flags Catchable, targetEntityId 0; count 0 when none",
            "catch.se F (SE's USE control, newly pressed) on a target sends one request when none waits (Busy) and the fish was not requested within ForgetMs (Again; a Moved answer or a timeout forgets it); the ack with ackSeq = reqSeq within AckTimeoutMs of the request's first write into C5 (UnpublishedMs from F while it was never written) gives the item (Caught) or refuses (NotCaught); none: Timeout, no item then; a later Caught ack (Late) still gives the item",
            "catch.sn a request (version 1, Active, reqSeq not the last handled) is handled once: the creature of instanceId among the last C9 rows, Gone when its object no longer exists, Kind when it is not a kind row, Moved when it lies over SlackCm from posSn, else Caught: its GameObject destroyed (no death, no loot) and acked; Gone too when the request's worldEpoch is not its C5 record's; a WaterParkCreature is Kind; a fish of an S8a spawn point leaves a tombstone there (no respawn until the camera is beyond the spawns' DespawnM); Off while Subnautica has no world loaded",
            "item.give one raw item of the kind into the character's inventory (MyInventory.AddItems), dropped at its feet (MyFloatingObjects.Spawn) when it does not fit; NoItem when the world mod's definition is missing",
            "items ConsumableItem DE_Fish_<Kind>_Raw and DE_Fish_<Kind>_Cooked: DisplayName <name> (raw) / <name> (cooked), Mass MassG / 1000 kg, Volume VolumeMl / 1000 L, Size SizeMm / 1000 m (SE's meal packs), one Food stat RawMicro or CookedMicro / 1e6 per TimeS (raw = MealPack_KelpCrisp, SE's smallest meal pack), no Health stat; blueprint DE_CookFish_<Kind>: 1 raw -> 1 cooked in CookS s, class FoodProcessorRations",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, long v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        private static void Fields(StringBuilder sb, string tag, string[][] fields)
        {
            foreach (string[] f in fields)
                sb.Append("field ").Append(tag).Append(' ').Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
        }

        /// <summary>The canonical text (ASCII, '\n' after every line; header, const groups, fields, kinds, format lines sorted).</summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(4096);
            sb.Append(Header).Append('\n');
            C(sb, "C9", "RowsMax", C9.RowsMax);
            C(sb, "C9", "RadiusCm", C9.RadiusCm);
            C(sb, "C9", "PeriodMs", C9.PeriodMs);
            C(sb, "C9", "StaleMs", C9.StaleMs);
            C(sb, "C9Flags", "Catchable", C9Flags.Catchable);
            C(sb, "Catch", "ReachCm", Catch.ReachCm);
            C(sb, "Catch", "AimCm", Catch.AimCm);
            C(sb, "Catch", "LeadMaxMs", Catch.LeadMaxMs);
            C(sb, "Catch", "SlackCm", Catch.SlackCm);
            C(sb, "Catch", "AckTimeoutMs", Catch.AckTimeoutMs);
            C(sb, "Catch", "UnpublishedMs", Catch.UnpublishedMs);
            C(sb, "Catch", "ForgetMs", Catch.ForgetMs);
            C(sb, "C5", "Offset", C5.Offset);
            C(sb, "C5", "Size", C5.Size);
            C(sb, "C5", "Version", C5.Version);
            C(sb, "C5Flags", "Active", C5Flags.Active);
            Fields(sb, "s17", C5Fields);
            C(sb, "C7", "Offset", C7.Offset);
            C(sb, "C7", "Size", C7.Size);
            C(sb, "C7", "Version", C7.Version);
            Fields(sb, "s17ack", C7Fields);
            for (int r = 1; r < Result.Names.Length; r++) C(sb, "Result", Result.Names[r], r);
            for (int r = 1; r < Refusal.Names.Length; r++) C(sb, "Refusal", Refusal.Names[r], r);
            C(sb, "Food", "RawMicro", Food.RawMicro);
            C(sb, "Food", "CookedMicro", Food.CookedMicro);
            C(sb, "Food", "TimeS", Food.TimeS);
            C(sb, "Food", "CookS", Food.CookS);
            C(sb, "Item", "MassG", Item.MassG);
            C(sb, "Item", "VolumeMl", Item.VolumeMl);
            C(sb, "Item", "SizeMm", Item.SizeMm);
            foreach (S17Kind k in Kinds)
                sb.Append("kind ").Append(k.Name).Append(' ').Append(k.TechType.ToString(CultureInfo.InvariantCulture)).Append(" name ").Append(k.Display)
                  .Append(" raw ").Append(k.RawSubtype).Append(" cooked ").Append(k.CookedSubtype).Append(" blueprint ").Append(k.Blueprint).Append('\n');
            var formats = (string[])FormatLines.Clone();
            Array.Sort(formats, string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/>.</summary>
        public static ulong Hash()
        {
            if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
            return _hash;
        }
    }

    // ================================================================================================================ rules

 /// <summary>The C5 request (format c5.s17).</summary>
    public struct S17Request
    {
        public byte Version, Flags;
        public uint ReqSeq, InstanceId, WorldEpoch;
        public int TechType;
        public float X, Y, Z;
        public bool Active { get { return (Flags & S17Conventions.C5Flags.Active) != 0; } }
    }

    /// <summary>The C7 answer (format c7.s17).</summary>
    public struct S17Ack
    {
        public byte Version, Result;
        public uint AckSeq, InstanceId;
        public int TechType;
    }

    /// <summary>Codec of the C5 request block (SeState 0xCC-0xEB) and the C7 answer block (SnStatus 0xC8-0xD7).</summary>
    public static unsafe class S17Wire
    {
        public static void WriteRequest(ref SeState st, ref S17Request r)
        {
            fixed (uint* p = &st.seq) WriteRequest((byte*)p + S17Conventions.C5.Offset, ref r);
        }

        public static void WriteRequest(byte* b, ref S17Request r)
        {
            b[0x00] = (byte)S17Conventions.C5.Version;
            b[0x01] = r.Flags;
            *(ushort*)(b + 0x02) = 0;
            *(uint*)(b + 0x04) = r.ReqSeq;
            *(uint*)(b + 0x08) = r.InstanceId;
            *(int*)(b + 0x0C) = r.TechType;
            *(float*)(b + 0x10) = r.X;
            *(float*)(b + 0x14) = r.Y;
            *(float*)(b + 0x18) = r.Z;
            *(uint*)(b + 0x1C) = r.WorldEpoch;
        }

        public static void ClearRequest(ref SeState st)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S17Conventions.C5.Offset;
                for (int i = 0; i < S17Conventions.C5.Size; i++) b[i] = 0;
            }
        }

        /// <summary>False when the record carries no S17 request block (version 0 or unknown).</summary>
        public static bool TryReadRequest(ref SeState st, out S17Request r)
        {
            fixed (uint* p = &st.seq) return TryReadRequest((byte*)p + S17Conventions.C5.Offset, out r);
        }

        public static bool TryReadRequest(byte* b, out S17Request r)
        {
            r = default(S17Request);
            r.Version = b[0x00];
            if (r.Version != S17Conventions.C5.Version) return false;
            r.Flags = b[0x01];
            r.ReqSeq = *(uint*)(b + 0x04);
            r.InstanceId = *(uint*)(b + 0x08);
            r.TechType = *(int*)(b + 0x0C);
            r.X = *(float*)(b + 0x10);
            r.Y = *(float*)(b + 0x14);
            r.Z = *(float*)(b + 0x18);
            r.WorldEpoch = *(uint*)(b + 0x1C);
            return true;
        }

        public static void WriteAck(ref SnStatus st, ref S17Ack a)
        {
            fixed (uint* p = &st.seq) WriteAck((byte*)p + S17Conventions.C7.Offset, ref a);
        }

        public static void WriteAck(byte* b, ref S17Ack a)
        {
            b[0x00] = (byte)S17Conventions.C7.Version;
            b[0x01] = a.Result;
            *(ushort*)(b + 0x02) = 0;
            *(uint*)(b + 0x04) = a.AckSeq;
            *(uint*)(b + 0x08) = a.InstanceId;
            *(int*)(b + 0x0C) = a.TechType;
        }

        /// <summary>False when the record carries no S17 answer block (version 0 or unknown).</summary>
        public static bool TryReadAck(ref SnStatus st, out S17Ack a)
        {
            fixed (uint* p = &st.seq) return TryReadAck((byte*)p + S17Conventions.C7.Offset, out a);
        }

        public static bool TryReadAck(byte* b, out S17Ack a)
        {
            a = default(S17Ack);
            a.Version = b[0x00];
            if (a.Version != S17Conventions.C7.Version) return false;
            a.Result = b[0x01];
            a.AckSeq = *(uint*)(b + 0x04);
            a.InstanceId = *(uint*)(b + 0x08);
            a.TechType = *(int*)(b + 0x0C);
            return true;
        }

        /// <summary>The request block's bytes (tests and the Python twin's vectors).</summary>
        public static byte[] RequestBytes(ref S17Request r)
        {
            var o = new byte[S17Conventions.C5.Size];
            fixed (byte* b = o) WriteRequest(b, ref r);
            return o;
        }

        public static byte[] AckBytes(ref S17Ack a)
        {
            var o = new byte[S17Conventions.C7.Size];
            fixed (byte* b = o) WriteAck(b, ref a);
            return o;
        }
    }

    /// <summary>
 /// The C9 fish list (format c9.s17): Subnautica's writer and SE's reader over the region's seqlock. The reader copies into
 /// its table only on a coherent read.
 /// </summary>
    public static unsafe class S17C9
    {
        public const int Bytes = SnCreaturesLimits.RecordsOffset + S17Conventions.C9.RowsMax * SnCreaturesLimits.RecordSize;

        /// <summary>Writes <paramref name="count"/> rows (at most RowsMax) and the header through the region's seqlock; <paramref name="scratch"/> holds Bytes.</summary>
        public static void Write(byte* region, byte[] scratch, SnCreature[] rows, int count, ulong snFrame)
        {
            if (count > S17Conventions.C9.RowsMax) count = S17Conventions.C9.RowsMax;
            if (count < 0) count = 0;
            fixed (byte* b = scratch)
            {
                var h = (SnCreaturesHeader*)b;
                h->count = (uint)count;
                h->snFrame = snFrame;
                for (int i = 0; i < count; i++) *(SnCreature*)(b + SnCreaturesLimits.RecordsOffset + i * SnCreaturesLimits.RecordSize) = rows[i];
                Seqlock.WriteBytes(region, b, SnCreaturesLimits.RecordsOffset + count * SnCreaturesLimits.RecordSize);
            }
        }

        /// <summary>One coherent copy into <paramref name="table"/> (Bytes) via <paramref name="scratch"/>; false (the table untouched) when busy or never written.</summary>
        public static bool TryRead(byte* region, byte[] scratch, byte[] table, out uint seq, out int count)
        {
            seq = 0;
            count = 0;
            fixed (byte* s = scratch)
            {
                if (Seqlock.TryReadBytes(region, s, Bytes, 16, out seq) != SeqlockReadStatus.Fresh) return false;
                uint n = ((SnCreaturesHeader*)s)->count;
                count = (int)Math.Min(n, (uint)S17Conventions.C9.RowsMax);
            }
            Buffer.BlockCopy(scratch, 0, table, 0, Bytes);
            return true;
        }

        /// <summary>Row <paramref name="i"/> of a table copy.</summary>
        public static SnCreature Row(byte[] table, int i)
        {
            fixed (byte* b = table) return *(SnCreature*)(b + SnCreaturesLimits.RecordsOffset + i * SnCreaturesLimits.RecordSize);
        }
    }

    /// <summary>The aim rule (format aim): pure, in SE world metres.</summary>
    public static class S17Aim
    {
        /// <summary>True when <paramref name="p"/> qualifies; <paramref name="reach"/> = |p - eye| (the ranking key).</summary>
        public static bool Qualifies(Vec3 cam, Vec3 fwdUnit, Vec3 eye, Vec3 p, out double reach)
        {
            reach = (p - eye).Length;
            Vec3 d = p - cam;
            double t = Vec3.Dot(d, fwdUnit);
            if (!(t > 0)) return false;
            Vec3 perp = d - fwdUnit * t;
            if (perp.Length > S17Conventions.Catch.AimCm / 100.0) return false;
            return reach <= S17Conventions.Catch.ReachCm / 100.0;
        }

        /// <summary>The lead applied to a C9 position (seconds, clamped to [0, LeadMaxMs]).</summary>
        public static double LeadS(double ageS)
        {
            if (!(ageS > 0)) return 0.0;
            return Math.Min(ageS, S17Conventions.Catch.LeadMaxMs / 1000.0);
        }

        /// <summary>Subnautica's position check (format catch.sn): the live fish within SlackCm of the requested position.</summary>
        public static bool WithinSlack(double dx, double dy, double dz)
        {
            double s = S17Conventions.Catch.SlackCm / 100.0;
            return dx * dx + dy * dy + dz * dz <= s * s;
        }
    }

    /// <summary>
 /// SE's request bookkeeping (format catch.se): one request in flight, no fish requested twice within ForgetMs, the ack
 /// matched by its sequence, the timeout. Game-free (SE game thread; Wire tests).
 /// </summary>
    public sealed class S17CatchBook
    {
        public enum AckOutcome { None, Matched, Late }

        private readonly Dictionary<uint, ulong> _requested = new Dictionary<uint, ulong>();
        private uint _nextSeq;
        private uint _lastTimedOut;
        private uint _lastSeenAck;
        private bool _ackPrimed;

        public bool Pending { get; private set; }
        public S17Request Current;
        /// <summary>When F sent the request (game clock ms).</summary>
        public ulong SentMs { get; private set; }
        /// <summary>The request was written into C5.</summary>
        public bool Published { get; private set; }
        public ulong PublishedMs { get; private set; }

        public S17CatchBook(uint firstSeq) { _nextSeq = firstSeq == 0 ? 1u : firstSeq; }

        public uint NextSeq { get { return _nextSeq; } }

        /// <summary>0 = the request is sent (<see cref="Current"/>), else a <see cref="S17Conventions.Refusal"/> (Busy, Again).</summary>
        public int TryRequest(ulong nowMs, uint instanceId, int techType, float x, float y, float z, uint worldEpoch)
        {
            if (Pending) return S17Conventions.Refusal.Busy;
            ulong at;
            if (_requested.TryGetValue(instanceId, out at) && nowMs - at < (ulong)S17Conventions.Catch.ForgetMs) return S17Conventions.Refusal.Again;
            if (_requested.Count > 256) Prune(nowMs);
            _requested[instanceId] = nowMs;
            var r = new S17Request { Version = (byte)S17Conventions.C5.Version, Flags = S17Conventions.C5Flags.Active, ReqSeq = _nextSeq, InstanceId = instanceId, TechType = techType, X = x, Y = y, Z = z, WorldEpoch = worldEpoch };
            _nextSeq = _nextSeq == uint.MaxValue ? 1u : _nextSeq + 1u;
            Current = r;
            Pending = true;
            SentMs = nowMs;
            Published = false;
            PublishedMs = 0;
            return 0;
        }

        /// <summary>The C5 writer published request <paramref name="seq"/>: the ack clock starts now (once per request).</summary>
        public void MarkPublished(uint seq, ulong nowMs)
        {
            if (!Pending || Published || seq != Current.ReqSeq) return;
            Published = true;
            PublishedMs = nowMs;
        }

        /// <summary>A fresh C7 answer: Matched (the pending request's; the request is done), Late (one that timed out), else None.</summary>
        public AckOutcome OnAck(ref S17Ack a)
        {
            if (a.Version != S17Conventions.C7.Version) return AckOutcome.None;
            if (_ackPrimed && a.AckSeq == _lastSeenAck) return AckOutcome.None;
            _ackPrimed = true;
            _lastSeenAck = a.AckSeq;
            if (Pending && a.AckSeq == Current.ReqSeq)
            {
                Pending = false;
                Current.Flags = 0;
                if (a.Result == S17Conventions.Result.Moved || a.Result == S17Conventions.Result.Off) _requested.Remove(a.InstanceId);
                return AckOutcome.Matched;
            }
            if (_lastTimedOut != 0 && a.AckSeq == _lastTimedOut) return AckOutcome.Late;
            return AckOutcome.None;
        }

        /// <summary>
 /// True once when the pending request passed AckTimeoutMs after its first write into C5, or UnpublishedMs after F while
 /// it was never written.
 /// </summary>
        public bool TimedOut(ulong nowMs)
        {
            if (!Pending) return false;
            if (Published ? nowMs - PublishedMs < (ulong)S17Conventions.Catch.AckTimeoutMs : nowMs - SentMs < (ulong)S17Conventions.Catch.UnpublishedMs) return false;
            Pending = false;
            Current.Flags = 0;
            _lastTimedOut = Current.ReqSeq;
            _requested.Remove(Current.InstanceId);
            return true;
        }

        /// <summary>The answer already in C7 when the session starts is not an answer to us.</summary>
        public void Prime(ref S17Ack a)
        {
            if (a.Version != S17Conventions.C7.Version || _ackPrimed) return;
            _ackPrimed = true;
            _lastSeenAck = a.AckSeq;
        }

        public void Reset()
        {
            Pending = false;
            Current = default(S17Request);
            _requested.Clear();
            _lastTimedOut = 0;
            _ackPrimed = false;
        }

        private void Prune(ulong nowMs)
        {
            var old = new List<uint>();
            foreach (KeyValuePair<uint, ulong> kv in _requested) if (nowMs - kv.Value >= (ulong)S17Conventions.Catch.ForgetMs) old.Add(kv.Key);
            foreach (uint k in old) _requested.Remove(k);
        }
    }
}
