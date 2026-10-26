using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    // Slice S5 "Whole grids + character", SE side: the game-independent parts (compiled into tests/DeepEngineers.SE.Tests
 // too). The S5 mirror conventions as the SE producer applies them (a facade over Wire's MirrorConventions; CONTRACT
 // §5.4 "C1–C4 conventions for S5"), the entity set with its hysteresis, the prioritised C4 lanes with back-pressure, the
 // C2 subpart-row plan, the dynamic-subpart rule, the S5 part diff and per-cell debounce, the character split by
 // material, the skin resolution, the expected chunk count, the de.s5 syntax and settings, and the s5.* JSONL rows. The
 // shared math is Wire's (MirrorSkeleton/SkinMath for canon1 and the skinned export, InstanceMath for SE row matrices,
 // ChunkMath, Derec). The game-facing code is in EntitySet.cs, GridExporter.cs, SubgridPoses.cs, CharacterExport.cs,
 // S5Commands.cs, S5Patches.cs, ModelLoader.cs and GridMirrorProducer(.S5).cs.

 /// <summary>S5 numbers in one place (CONTRACT §5.4 "Entity set (SE)", "Budgets and back-pressure (S5)").</summary>
    internal static class S5Defaults
    {
        /// <summary>Enter radius: water visibility fallback 200 m + 50 m (SE does not know Subnautica's biome visibility).</summary>
        public const double EnterM = 250.0;
        /// <summary>Leave radius (hysteresis).</summary>
        public const double LeaveM = 300.0;
        /// <summary>An entity outside the leave radius this long gets EntityRemove.</summary>
        public const ulong KeepMs = 60000;
        /// <summary>C2 capacity (layout 1.0).</summary>
        public const int MaxEntities = SeEntitiesLimits.MaxEntities;
        public const int MaxSubpartRows = SeEntitiesLimits.MaxSubparts;
        /// <summary>C3: ≤ 2 ms per tick and ≤ 16 MiB written per second (per tick at 60 Hz).</summary>
        public const double C3BudgetMs = 2.0;
        public const long C3BytesPerSecond = 16L << 20;
        public const long C3BytesPerTick = C3BytesPerSecond / 60;
        public const int C3MaxMessagesPerTick = 256;
        /// <summary>C4: ≤ 1 ms and ≤ 512 KiB per tick.</summary>
        public const double C4BudgetMs = 1.0;
        public const long C4BytesPerTick = 512L << 10;
        public const int C4MaxMessagesPerTick = 8192;
        /// <summary>Back-pressure: C4 free below this → lanes 1–2 only; C3 free below <see cref="C3LowFreeBytes"/> → no new model starts.</summary>
        public const long C4LowFreeBytes = 1L << 20;
        public const long C3LowFreeBytes = 8L << 20;
        /// <summary>Edits within this distance of the camera go to lane 2, the rest to lane 3.</summary>
        public const double NearEditM = 50.0;
        /// <summary>Reads of cells and blocks per tick (update thread): time budget.</summary>
        public const double ReadBudgetMs = 1.5;
        /// <summary>Static subparts checked for motion per tick (round robin).</summary>
        public const int MotionChecksPerTick = 256;
        /// <summary>Fat blocks re-read per tick by the sweep (round robin over the mirrored grids, nearest first; a safety net).</summary>
        public const int BlockSweepPerTick = 8;
        /// <summary>One render cell is re-signed every this many ticks by the sweep (a cell can hold thousands of parts).</summary>
        public const int CellSweepTicks = 30;
        /// <summary>The default Engineer (fallback character model;</summary>
        public const string DefaultCharacter = "Default_Astronaut";
        /// <summary>Per-second s5.budget rows.</summary>
        public const ulong BudgetRowMs = 1000;
    }

    // ===================================================================================================== conventions

 /// <summary>
 /// The S5 mirror conventions as the SE producer applies them (CONTRACT §5.4 "C1–C4 conventions for S5", FNV-1a 64
 /// <c>0x31dfd6f9befdef5d</c>): a thin facade over Wire's <see cref="MirrorConventions"/> (the shared definition, pinned by
 /// MirrorConventionsTests and its Python twin), so the producer code reads as the contract table and every value comes
 /// from the one DLL both plugins ship.
 /// </summary>
    internal static class S5Conv
    {
        public const ulong PinnedHash = MirrorConventions.PinnedHash;
        public const uint Version1 = MirrorPartFlags.Version1;
        public const uint VersionMask = MirrorPartFlags.VersionMask;

        public static class CharFlags { public const uint Player = MirrorCharacterFlags.Player, Fallback = MirrorCharacterFlags.Fallback, Version1 = MirrorCharacterFlags.Version1; }
        public static class CharPart { public const ulong Body = MirrorCharacterPart.Body, Head1st = MirrorCharacterPart.Head1st, Tool = MirrorCharacterPart.Tool; }
        public static class GridFlags { public const uint Static = MirrorGridFlags.Static, Subgrid = MirrorGridFlags.Subgrid, FloatingObject = MirrorGridFlags.FloatingObject, Version1 = MirrorGridFlags.Version1; }
        public static class PartClass
        {
            public const uint CubePart = MirrorPartClass.CubePart, Edge = MirrorPartClass.Edge, FatBlock = MirrorPartClass.FatBlock, Subpart = MirrorPartClass.Subpart,
                CharacterBody = MirrorPartClass.CharacterBody, Tool = MirrorPartClass.Tool, FloatingObject = MirrorPartClass.FloatingObject;
        }
        public static class PartFlags
        {
            public const uint ClassMask = MirrorPartFlags.ClassMask, Dynamic = MirrorPartFlags.Dynamic, HiddenInFirstPerson = MirrorPartFlags.HiddenInFirstPerson,
                BuildStage = MirrorPartFlags.BuildStage, Deformed = MirrorPartFlags.Deformed, NoShadows = MirrorPartFlags.NoShadows,
                MetalnessColorable = MirrorPartFlags.MetalnessColorable, Version1 = MirrorPartFlags.Version1, VersionMask = MirrorPartFlags.VersionMask;
        }
        public const ulong FloatingPartKey = MirrorConventions.FloatingPartKey;

        public static string CanonicalText() { return MirrorConventions.CanonicalText(); }
        public static ulong Hash() { return MirrorConventions.Hash; }

        // ------------------------------------------------------------------------------------------------- flags

        public static uint Part(uint cls, uint extra) { return MirrorConventions.PartFlags(cls, extra); }
        public static uint ClassOf(uint flags) { return flags & PartFlags.ClassMask; }
        public static uint VersionOf(uint flags) { return MirrorConventions.VersionOf(flags); }
        public static uint Grid(bool isStatic, bool subgrid, bool floating) { return MirrorConventions.GridFlags(isStatic, subgrid, floating); }
        public static uint Character(bool fallback) { return MirrorConventions.CharacterFlags(fallback); }

        // ------------------------------------------------------------------------------------------------- keys

 /// <summary>partKey.cube = S3's PartDiff.PartKey (modelKey, packed ortho matrix).</summary>
        public static ulong CubeKey(ulong modelKey, float tx, float ty, float tz, float packedRotation) { return MirrorConventions.CubePartKey(modelKey, tx, ty, tz, packedRotation); }
        /// <summary>partKey.edge = fnv1a64(u32 2, u64 modelKey, f32 tx, ty, tz, packedRotation) of <c>EdgeInfo.LocalOrthoMatrix</c>.</summary>
        public static ulong EdgeKey(ulong modelKey, float tx, float ty, float tz, float packedRotation) { return MirrorConventions.EdgePartKey(modelKey, tx, ty, tz, packedRotation); }
        /// <summary>partKey.fat = fnv1a64(utf8 "fat|minX,minY,minZ") of <c>MySlimBlock.Min</c>.</summary>
        public static ulong FatKey(int x, int y, int z) { return MirrorConventions.FatPartKey(x, y, z); }
        /// <summary>partKey.subpart = fnv1a64(utf8 "sub|&lt;fatKey x16&gt;|&lt;name&gt;[/&lt;name&gt;…]").</summary>
        public static ulong SubpartKey(ulong fatKey, string path) { return MirrorConventions.SubpartKey(fatKey, path); }
        public static string SubpartKeyText(ulong fatKey, string path) { return MirrorConventions.SubpartKeyText(fatKey, path); }
        /// <summary>skinId = FNV-1a 32 of the lower-case skin subtype; 0 = none.</summary>
        public static uint SkinId(string skin) { return string.IsNullOrEmpty(skin) ? 0u : MirrorConventions.SkinId(skin); }

        /// <summary>modelKey text with <c>|skin=</c> (lower case) and <c>|char=body|head1st</c> (<paramref name="charPart"/> "body" or "head1st").</summary>
        public static string ModelKeyText(string path, int lod, float scale, string skin, string charPart)
        {
            return MirrorConventions.ModelKeyText(path, (uint)lod, scale, skin, CharPartOf(charPart));
        }

        public static ulong ModelKey(string path, int lod, float scale, string skin, string charPart)
        {
            return MirrorConventions.ModelKey(path, (uint)lod, scale, skin, CharPartOf(charPart));
        }

        private static ulong CharPartOf(string charPart)
        {
            if (string.IsNullOrEmpty(charPart)) return 0;
            if (charPart == "body") return MirrorCharacterPart.Body;
            if (charPart == "head1st") return MirrorCharacterPart.Head1st;
            throw new ArgumentOutOfRangeException("charPart", "body or head1st");
        }
    }

    // ===================================================================================================== dynamic subparts

 /// <summary>S5 "Dynamic subparts": a subpart becomes Dynamic on its first composed-matrix change above 1e-4 m or 1e-4 rad (Wire's InstanceMath.HasMoved).</summary>
    internal static class SubpartMotion
    {
        public static bool Moved(float[] sent, SeMatrix now)
        {
            if (sent == null || sent.Length != 12) return false;
            return InstanceMath.HasMoved(SeMatrix.FromRows(sent), now);
        }

        /// <summary>Block types whose subparts SE animates (pre-marked Dynamic): a name heuristic over the fat block's type.</summary>
        public static bool AnimatesByType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            foreach (string k in AnimatingTypes)
                if (typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static readonly string[] AnimatingTypes =
            { "Door", "Turret", "Piston", "Drill", "Grinder", "Welder", "WindTurbine", "Gatling", "Missile", "Hangar", "Parachute", "Searchlight", "Radar", "Antenna" };

        /// <summary>An SE matrix as the 12 PartAdd floats (rows Right, Up, Backward, Translation).</summary>
        public static float[] Floats(SeMatrix m)
        {
            var r = new float[12];
            m.CopyRowsTo(r, 0);
            return r;
        }
    }

    // ===================================================================================================== part diff (S5)

 /// <summary>
 /// The S5 part diff: like S3's <see cref="PartDiff"/> but a part that changed other than colour under the same key is a
 /// replace (PartAdd only: CONTRACT §5.4 "A PartAdd for a known (entity, partKey) replaces the part"; a fat block keeps
 /// its key across build stages), and the signature includes the flags.
 /// </summary>
    internal static class S5PartDiff
    {
        public static void Compute(Dictionary<ulong, PartSnapshot> held, Dictionary<ulong, PartSnapshot> now,
                                   List<PartSnapshot> adds, List<PartSnapshot> replaces, List<PartSnapshot> removes, List<PartSnapshot> recolors)
        {
            adds.Clear(); replaces.Clear(); removes.Clear(); recolors.Clear();
            foreach (var kv in now)
            {
                PartSnapshot old;
                if (!held.TryGetValue(kv.Key, out old)) { adds.Add(kv.Value); continue; }
                if (!kv.Value.SameExceptColour(old)) { replaces.Add(kv.Value); continue; }
                if (!kv.Value.SameColour(old)) recolors.Add(kv.Value);
            }
            foreach (var kv in held)
                if (!now.ContainsKey(kv.Key)) removes.Add(kv.Value);
            Comparison<PartSnapshot> byKey = ByKey;
            adds.Sort(byKey); replaces.Sort(byKey); removes.Sort(byKey); recolors.Sort(byKey);
        }

        private static readonly Comparison<PartSnapshot> ByKey = (a, b) => a.PartKey.CompareTo(b.PartKey);

        /// <summary>One part's contribution to an order-independent set signature.</summary>
        public static ulong PartHash(ulong partKey, ulong modelKey, uint flags, float h, float s, float v, float u, float uv, uint skin)
        {
            ulong x = Fnv.AddU64(Fnv.Offset, partKey);
            x = Fnv.AddU64(x, modelKey);
            x = Fnv.AddU64(x, flags);
            x = Fnv.AddF32(x, h); x = Fnv.AddF32(x, s); x = Fnv.AddF32(x, v);
            x = Fnv.AddF32(x, u); x = Fnv.AddF32(x, uv);
            x = Fnv.AddU64(x, skin);
            return x;
        }

        /// <summary>Order-independent combination (sum and mixed xor, as S3's).</summary>
        public struct SigAcc
        {
            public ulong Sum, X;
            public int Count;
            public void Add(ulong h) { Sum += h; X ^= h * 0x9E3779B97F4A7C15UL; Count++; }
            public ulong Value { get { return Sum ^ (X << 1) ^ (ulong)Count; } }
        }

        public static ulong Signature(Dictionary<ulong, PartSnapshot> set)
        {
            var acc = new SigAcc();
            foreach (var kv in set) acc.Add(SnapshotHash(kv.Value));
            return acc.Value;
        }

        public static ulong SnapshotHash(PartSnapshot p)
        {
            ulong h = PartHash(p.PartKey, p.ModelKey, p.Flags, p.H, p.S, p.V, p.UvU, p.UvV, p.SkinId);
            if (p.Local != null) for (int i = 0; i < p.Local.Length; i++) h = Fnv.AddF32(h, p.Local[i]);
            return h;
        }
    }

    /// <summary>
 /// The debounce of one render cell (S3's rule, per cell): a cheap signature is read every time the cell is touched; a
 /// full read and diff happen only once the signature read the same twice in a row (one tick apart) and differs from
 /// what Subnautica holds.
 /// </summary>
    internal sealed class SignatureDebounce
    {
        private ulong _last;
        private bool _hasLast;
        private int _stable;
        public ulong HeldSig;
        public bool HasHeld;

        /// <summary>Feeds one signature; true when stable (read the same <paramref name="stablePolls"/> times) and different from the held one.</summary>
        public bool Offer(ulong sig, int stablePolls)
        {
            if (_hasLast && sig == _last) _stable++;
            else { _stable = 1; _last = sig; _hasLast = true; }
            if (_stable < stablePolls) return false;
            return !HasHeld || sig != HeldSig;
        }

        /// <summary>True while the last reads have not settled.</summary>
        public bool Unsettled(int stablePolls) { return _hasLast && _stable < stablePolls; }

        /// <summary>True when the last signature is stable and equal to the held one (nothing to do).</summary>
        public bool Settled(int stablePolls) { return _hasLast && _stable >= stablePolls && HasHeld && _last == HeldSig; }

        public void Commit(ulong sig) { HeldSig = sig; HasHeld = true; }

        public void Forget() { _hasLast = false; _stable = 0; HasHeld = false; HeldSig = 0; }
    }

    // ===================================================================================================== entity set

    internal enum S5EntityKind { Grid = 1, FloatingObject = 2 }

    /// <summary>One entity the pruning query (or a hierarchy walk) found this tick.</summary>
    internal struct EntityCandidate
    {
        public long Id;
        /// <summary>Root of its physical hierarchy (itself for a root grid or a floating object).</summary>
        public long RootId;
        public S5EntityKind Kind;
        /// <summary>Distance from the SE camera to its world AABB (0 inside).</summary>
        public double Distance;

        public EntityCandidate(long id, long rootId, S5EntityKind kind, double distance)
        {
            Id = id; RootId = rootId; Kind = kind; Distance = distance;
        }
    }

    /// <summary>
 /// The S5 entity set (CONTRACT §5.4 "Entity set (SE)"): a hierarchy (grids sharing a physical root, or one floating
 /// object) enters when any member is within <see cref="EnterM"/> of the camera and adds are allowed (C5 zone CORE, TILE
 /// VOID or OUTER VOID); it stays while any member is within <see cref="LeaveM"/>; outside that for <see cref="KeepMs"/>
 /// it leaves (EntityRemove of every member). Members that join an in-set hierarchy enter at once; at most
 /// <see cref="MaxEntities"/> entities, new hierarchies nearest first (the rest wait). Pinned entities (de.s3 registrations)
 /// never leave by distance. Game-independent; the caller removes closed entities with <see cref="Remove"/>.
 /// </summary>
    internal sealed class EntitySetTracker
    {
        public double EnterM = S5Defaults.EnterM, LeaveM = S5Defaults.LeaveM;
        public ulong KeepMs = S5Defaults.KeepMs;
        public int MaxEntities = S5Defaults.MaxEntities;

        internal sealed class Member
        {
            public long Id;
            public long RootId;
            public S5EntityKind Kind;
            public double Distance = double.PositiveInfinity;
            public bool Pinned;
            public bool SeenThisTick;
        }

        private readonly Dictionary<long, Member> _members = new Dictionary<long, Member>();
        private readonly Dictionary<long, ulong> _outsideSince = new Dictionary<long, ulong>();
        // scratch (reused)
        private readonly Dictionary<long, double> _hierDist = new Dictionary<long, double>();
        private readonly Dictionary<long, int> _hierSize = new Dictionary<long, int>();
        private readonly HashSet<long> _hierInSet = new HashSet<long>();
        private readonly List<KeyValuePair<long, double>> _newHier = new List<KeyValuePair<long, double>>();
        private readonly List<long> _scratch = new List<long>();

        public int Count { get { return _members.Count; } }
        public long Waiting { get; private set; }
        public long CapRefusals { get; private set; }

        public bool Contains(long id) { return _members.ContainsKey(id); }

        public bool TryGet(long id, out Member m) { return _members.TryGetValue(id, out m); }

        public IEnumerable<Member> Members { get { return _members.Values; } }

        /// <summary>Adds an entity that must stay mirrored whatever its distance (until <see cref="Remove"/>); false at capacity.</summary>
        public bool Pin(long id, long rootId, S5EntityKind kind)
        {
            Member m;
            if (_members.TryGetValue(id, out m)) { m.Pinned = true; return true; }
            if (_members.Count >= MaxEntities) { CapRefusals++; return false; }
            _members[id] = new Member { Id = id, RootId = rootId, Kind = kind, Pinned = true };
            return true;
        }

        public bool Remove(long id)
        {
            return _members.Remove(id);
        }

        public void Clear()
        {
            _members.Clear();
            _outsideSince.Clear();
        }

        /// <summary>
 /// One tick. <paramref name="seen"/>: every candidate found this tick (inside the leave radius, plus the members of
 /// in-set hierarchies wherever they are); <paramref name="added"/> gets the new entity ids (parents before
 /// children is the caller's order: roots are listed first within a hierarchy); <paramref name="removed"/> gets
 /// (id, reason) of entities leaving by distance.
 /// </summary>
        public void Update(ulong nowMs, IList<EntityCandidate> seen, bool allowAdd, List<long> added, List<KeyValuePair<long, string>> removed)
        {
            added.Clear();
            removed.Clear();
            _hierDist.Clear();
            _hierSize.Clear();
            _hierInSet.Clear();
            foreach (Member m in _members.Values) { m.SeenThisTick = false; m.Distance = double.PositiveInfinity; }

            for (int i = 0; i < seen.Count; i++)
            {
                EntityCandidate c = seen[i];
                double d;
                if (!_hierDist.TryGetValue(c.RootId, out d) || c.Distance < d) _hierDist[c.RootId] = c.Distance;
                int n;
                _hierSize[c.RootId] = _hierSize.TryGetValue(c.RootId, out n) ? n + 1 : 1;
                Member m;
                if (_members.TryGetValue(c.Id, out m))
                {
                    m.SeenThisTick = true;
                    m.Distance = c.Distance;
                    m.RootId = c.RootId;
                    m.Kind = c.Kind;
                }
            }
            foreach (Member m in _members.Values) _hierInSet.Add(m.RootId);

            // members of in-set hierarchies that are not in the set yet (an attach) enter at once
            for (int i = 0; i < seen.Count; i++)
            {
                EntityCandidate c = seen[i];
                if (_members.ContainsKey(c.Id) || !_hierInSet.Contains(c.RootId)) continue;
                if (_members.Count >= MaxEntities) { CapRefusals++; continue; }
                _members[c.Id] = new Member { Id = c.Id, RootId = c.RootId, Kind = c.Kind, Distance = c.Distance, SeenThisTick = true };
                added.Add(c.Id);
            }

            // hysteresis per hierarchy (by root id)
            _scratch.Clear();
            foreach (long root in _hierInSet) _scratch.Add(root);
            foreach (long root in _scratch)
            {
                double d;
                if (!_hierDist.TryGetValue(root, out d)) d = double.PositiveInfinity;
                if (d <= LeaveM) { _outsideSince.Remove(root); continue; }
                ulong since;
                if (!_outsideSince.TryGetValue(root, out since)) { _outsideSince[root] = nowMs; continue; }
                if (nowMs - since < KeepMs) continue;
                bool pinned = false;
                foreach (Member m in _members.Values) if (m.RootId == root && m.Pinned) { pinned = true; break; }
                if (pinned) continue;
                string why = "outside " + LeaveM.ToString("0", CultureInfo.InvariantCulture) + " m for " + (KeepMs / 1000) + " s";
                var gone = new List<long>();
                foreach (Member m in _members.Values) if (m.RootId == root) gone.Add(m.Id);
                // children first, the root last (the caller writes EntityRemove in this order)
                gone.Sort((a, b) => (a == root ? 1 : 0).CompareTo(b == root ? 1 : 0));
                foreach (long id in gone)
                {
                    _members.Remove(id);
                    removed.Add(new KeyValuePair<long, string>(id, why));
                }
                _outsideSince.Remove(root);
            }
            // forget timers of hierarchies that no longer exist in the set
            if (_outsideSince.Count > 0)
            {
                _scratch.Clear();
                foreach (long root in _outsideSince.Keys)
                {
                    bool any = false;
                    foreach (Member m in _members.Values) if (m.RootId == root) { any = true; break; }
                    if (!any) _scratch.Add(root);
                }
                foreach (long r in _scratch) _outsideSince.Remove(r);
            }

            // new hierarchies within the enter radius, nearest first, while there is room
            Waiting = 0;
            if (!allowAdd) return;
            _newHier.Clear();
            foreach (var kv in _hierDist)
            {
                if (_hierInSet.Contains(kv.Key) || kv.Value > EnterM) continue;
                _newHier.Add(kv);
            }
            if (_newHier.Count == 0) return;
            _newHier.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.Key.CompareTo(b.Key));
            foreach (var h in _newHier)
            {
                int size = _hierSize[h.Key];
                if (_members.Count + size > MaxEntities) { Waiting += size; continue; }
                // the root first, then the other members in query order
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < seen.Count; i++)
                    {
                        EntityCandidate c = seen[i];
                        if (c.RootId != h.Key || _members.ContainsKey(c.Id)) continue;
                        if ((pass == 0) != (c.Id == h.Key)) continue;
                        _members[c.Id] = new Member { Id = c.Id, RootId = c.RootId, Kind = c.Kind, Distance = c.Distance, SeenThisTick = true };
                        added.Add(c.Id);
                    }
            }
        }

        /// <summary>Distance from a point to an axis-aligned box (0 inside).</summary>
        public static double BoxDistance(double px, double py, double pz, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
        {
            double dx = px < minX ? minX - px : px > maxX ? px - maxX : 0;
            double dy = py < minY ? minY - py : py > maxY ? py - maxY : 0;
            double dz = pz < minZ ? minZ - pz : pz > maxZ ? pz - maxZ : 0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // ===================================================================================================== C4 lanes

 /// <summary>
 /// The prioritised C4 queue of S5 (CONTRACT §5.4 "Budgets and back-pressure (S5)"): lane 1 ClearAll, EntityRemove,
 /// GridAdd (first and re-parent) and CharacterAdd with its parts; lane 2 edits within 50 m of the camera; lane 3 other
 /// edits; lane 4 first sends. Pumped in lane order within one per-tick budget (time, messages, bytes); each message is
 /// tried once (a full ring stops the tick: try-once, never wait); with the ring's free bytes below
 /// <see cref="LowFreeBytes"/> only lanes 1–2 are written. Within a lane the order is FIFO (per-key order of PartRemove /
 /// PartAdd pairs is kept). A ticket (lane, sequence) tells when a message has left its lane; <see cref="Purge"/> drops
 /// an entity's queued messages from lanes 2–4 (before its EntityRemove). Update thread only. Game-independent.
 /// </summary>
    internal sealed class C4Lanes
    {
        public const int Lanes = 4;
        public const int Control = 1, NearEdit = 2, Edit = 3, FirstSend = 4;

        private struct Entry
        {
            public OutMessage Msg;
            public long Seq;
        }

        private readonly LinkedList<Entry>[] _q = new LinkedList<Entry>[Lanes + 1];
        private readonly long[] _nextSeq = new long[Lanes + 1];
        private readonly string _name;

        public long LowFreeBytes = S5Defaults.C4LowFreeBytes;

        public C4Lanes(string name)
        {
            _name = name;
            for (int i = 1; i <= Lanes; i++) { _q[i] = new LinkedList<Entry>(); _nextSeq[i] = 1; }
        }

        public string Name { get { return _name; } }
        public int Count { get { int n = 0; for (int i = 1; i <= Lanes; i++) n += _q[i].Count; return n; } }
        public int LaneCount(int lane) { return _q[lane].Count; }
        public long PendingBytes { get; private set; }
        public long TotalWritten { get; private set; }
        public long TotalBytes { get; private set; }
        public long FullEvents { get; private set; }
        public long BudgetStops { get; private set; }
        public long BackPressureTicks { get; private set; }
        public long DroppedTooLarge { get; private set; }
        public long Purged { get; private set; }
        public bool LastBackPressure { get; private set; }

        /// <summary>Queues a message; returns its ticket (sequence × 8 + lane).</summary>
        public long Enqueue(int lane, OutMessage m)
        {
            if (lane < 1 || lane > Lanes) throw new ArgumentOutOfRangeException("lane");
            long seq = _nextSeq[lane]++;
            _q[lane].AddLast(new Entry { Msg = m, Seq = seq });
            PendingBytes += m.Payload != null ? m.Payload.Length : 0;
            return seq * 8 + lane;
        }

        /// <summary>S3 compatibility: queue on the first-send lane (FIFO as S3's OutboundQueue).</summary>
        public long Enqueue(OutMessage m) { return Enqueue(FirstSend, m); }

        public static int LaneOf(long ticket) { return (int)(ticket & 7); }

        /// <summary>True once the ticket's message left its lane (written, dropped or purged). A negative ticket is never done.</summary>
        public bool IsDone(long ticket)
        {
            if (ticket < 0) return false;
            int lane = LaneOf(ticket);
            if (lane < 1 || lane > Lanes) return false;
            long seq = ticket >> 3;
            LinkedList<Entry> q = _q[lane];
            if (q.Count == 0) return seq < _nextSeq[lane];
            return seq < q.First.Value.Seq;
        }

        public void Clear()
        {
            for (int i = 1; i <= Lanes; i++) _q[i].Clear();
            PendingBytes = 0;
        }

        /// <summary>Drops every queued message of <paramref name="entity"/> from lanes 2–4 (lane 1 keeps its order). Returns the count.</summary>
        public int Purge(long entity)
        {
            if (entity == 0) return 0;
            int n = 0;
            for (int lane = 2; lane <= Lanes; lane++)
            {
                LinkedListNode<Entry> node = _q[lane].First;
                while (node != null)
                {
                    LinkedListNode<Entry> next = node.Next;
                    if (node.Value.Msg.Entity == entity)
                    {
                        PendingBytes -= node.Value.Msg.Payload != null ? node.Value.Msg.Payload.Length : 0;
                        _q[lane].Remove(node);
                        n++;
                    }
                    node = next;
                }
            }
            Purged += n;
            return n;
        }

        /// <summary>
 /// Writes from lane 1 down while the budget lasts. <paramref name="ringFreeBytes"/> &lt; 0 = unknown (no back-pressure).
 /// The first message of a tick is always attempted (progress never depends on the budget).
 /// </summary>
        public PumpResult Pump(IMessageSink sink, PumpBudget budget, Func<long> clock, long ringFreeBytes)
        {
            var r = new PumpResult { Stop = PumpStop.Empty };
            if (sink == null) { r.Stop = Count > 0 ? PumpStop.Held : PumpStop.Empty; return r; }
            bool pressure = ringFreeBytes >= 0 && ringFreeBytes < LowFreeBytes;
            LastBackPressure = pressure;
            if (pressure) BackPressureTicks++;
            int maxLane = pressure ? NearEdit : Lanes;
            long t0 = clock();
            long limit = (long)(budget.MaxMs * Stopwatch.Frequency / 1000.0);
            for (int lane = 1; lane <= maxLane; lane++)
            {
                LinkedList<Entry> q = _q[lane];
                while (q.Count > 0)
                {
                    if (r.Written > 0 &&
                        (r.Written >= budget.MaxMessages || (budget.MaxBytes > 0 && r.Bytes >= budget.MaxBytes) || clock() - t0 >= limit))
                    {
                        r.Stop = PumpStop.Budget;
                        BudgetStops++;
                        r.Ms = (clock() - t0) * 1000.0 / Stopwatch.Frequency;
                        return r;
                    }
                    OutMessage m = q.First.Value.Msg;
                    int n = m.Payload != null ? m.Payload.Length : 0;
                    RingWriteStatus st = sink.TryWrite(m.Type, m.Payload, n);
                    if (st == RingWriteStatus.Ok)
                    {
                        q.RemoveFirst();
                        PendingBytes -= n;
                        r.Written++;
                        r.Bytes += n;
                        TotalWritten++;
                        TotalBytes += n;
                        continue;
                    }
                    if (st == RingWriteStatus.Full)
                    {
                        r.Stop = PumpStop.Full;
                        FullEvents++;
                        r.Ms = (clock() - t0) * 1000.0 / Stopwatch.Frequency;
                        return r;
                    }
                    if (st == RingWriteStatus.Corrupt)
                    {
                        r.Stop = PumpStop.Corrupt;
                        r.Ms = (clock() - t0) * 1000.0 / Stopwatch.Frequency;
                        return r;
                    }
                    int group = m.Group;
                    q.RemoveFirst();
                    PendingBytes -= n;
                    r.Dropped++;
                    DroppedTooLarge++;
                    while (group != 0 && q.Count > 0 && q.First.Value.Msg.Group == group)
                    {
                        PendingBytes -= q.First.Value.Msg.Payload != null ? q.First.Value.Msg.Payload.Length : 0;
                        q.RemoveFirst();
                        r.Dropped++;
                        DroppedTooLarge++;
                    }
                }
            }
            if (pressure && Count > 0) r.Stop = PumpStop.Full;
            r.Ms = (clock() - t0) * 1000.0 / Stopwatch.Frequency;
            return r;
        }
    }

    /// <summary>
 /// Which lane an edit of one entity goes to: lane 4 while that entity's first-send messages are still queued, else 2
 /// (within 50 m) or 3; never a higher-priority lane than one still holding that entity's earlier messages (so a
 /// later PartRemove can never overtake an earlier PartAdd of the same key).
 /// </summary>
    internal sealed class LaneChooser
    {
        public long LastLane2 = -1, LastLane3 = -1, LastLane4 = -1;

        public int Choose(C4Lanes lanes, bool near)
        {
            if (LastLane4 >= 0 && !lanes.IsDone(LastLane4)) return C4Lanes.FirstSend;
            if (LastLane3 >= 0 && !lanes.IsDone(LastLane3)) return C4Lanes.Edit;
            return near ? C4Lanes.NearEdit : C4Lanes.Edit;
        }

        public void Note(long ticket)
        {
            switch (C4Lanes.LaneOf(ticket))
            {
                case 2: LastLane2 = ticket; break;
                case 3: LastLane3 = ticket; break;
                case 4: LastLane4 = ticket; break;
            }
        }

        public bool AllDone(C4Lanes lanes)
        {
            return (LastLane2 < 0 || lanes.IsDone(LastLane2)) && (LastLane3 < 0 || lanes.IsDone(LastLane3)) && (LastLane4 < 0 || lanes.IsDone(LastLane4));
        }

        public void Reset() { LastLane2 = LastLane3 = LastLane4 = -1; }
    }

    // ===================================================================================================== C2 rows

    internal enum C2RowKind { Subgrid = 0, DynamicSubpart = 1, Tool = 2 }

    internal struct C2RowCandidate
    {
        public C2RowKind Kind;
        public double Distance;
        public SeSubpart Row;
    }

    /// <summary>
 /// The C2 subpart table plan (CONTRACT §5.4 "C2 subpart table (S5 rows)"): subgrids (by root distance), then dynamic
 /// subparts (nearest first), then the held tool; rows past the table's 2,048 are dropped and counted (the consumer holds
 /// a dropped child's last pose).
 /// </summary>
    internal static class C2RowPlanner
    {
        private static readonly Comparison<C2RowCandidate> Order = (a, b) =>
        {
            if (a.Kind != b.Kind) return ((int)a.Kind).CompareTo((int)b.Kind);
            if (a.Distance != b.Distance) return a.Distance.CompareTo(b.Distance);
            if (a.Row.ownerId != b.Row.ownerId) return a.Row.ownerId.CompareTo(b.Row.ownerId);
            return a.Row.partKey.CompareTo(b.Row.partKey);
        };

        /// <summary>Sorts <paramref name="rows"/> in place and copies at most <paramref name="max"/> into <paramref name="output"/>.</summary>
        public static int Plan(List<C2RowCandidate> rows, SeSubpart[] output, int max, out int dropped)
        {
            rows.Sort(Order);
            int cap = Math.Min(max, output.Length);
            int n = Math.Min(rows.Count, cap);
            for (int i = 0; i < n; i++) output[i] = rows[i].Row;
            dropped = rows.Count - n;
            return n;
        }

    }

    // ===================================================================================================== character split

 /// <summary>
 /// The character model split (CONTRACT §5.4 "Character"): submeshes whose material name is in
 /// <c>MyCharacterDefinition.MaterialsDisabledIn1st</c> (ordinal, case-insensitive) form <c>|char=head1st</c>, the rest
 /// <c>|char=body</c>. The geometry split is Wire's <see cref="SkinMath.SplitByMaterial"/> (each part keeps only the
 /// vertices it uses, renumbered in first-use order; skinning arrays carried); this wrapper keeps the SE exporter's
 /// material names and MaterialInfos with each part. A part without triangles is null.
 /// </summary>
    internal static class CharacterSplit
    {
        public static void Split(ExportedModel full, ICollection<string> headMaterials, out ExportedModel body, out ExportedModel head)
        {
            var headSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (headMaterials != null) foreach (string s in headMaterials) if (!string.IsNullOrEmpty(s)) headSet.Add(s);
            var flags = new bool[full.Submeshes.Length];
            for (int i = 0; i < flags.Length; i++) flags[i] = full.Submeshes[i].MaterialName != null && headSet.Contains(full.Submeshes[i].MaterialName);
            ModelGeometry g = S3Codec.ToGeometry(full);
            ModelGeometry gb, gh;
            SkinMath.SplitByMaterial(g, flags, out gb, out gh);
            body = gb == null ? null : FromGeometry(gb, full, flags, false);
            head = gh == null ? null : FromGeometry(gh, full, flags, true);
        }

        /// <summary>An exporter model from a split part: arrays from the geometry, names and materials from the source submeshes.</summary>
        private static ExportedModel FromGeometry(ModelGeometry g, ExportedModel full, bool[] headFlags, bool wantHead)
        {
            // the part's submeshes are the source's with this flag that own triangles, in source order (SplitByMaterial)
            var names = new List<string>();
            for (int s = 0; s < full.Submeshes.Length; s++)
                if (headFlags[s] == wantHead && full.Submeshes[s].TriCount > 0) names.Add(full.Submeshes[s].MaterialName);
            int n = g.VertexCount;
            var m = new ExportedModel
            {
                AssetName = full.AssetName,
                Lod = full.Lod,
                PatternScale = g.PatternScale,
                Scale = full.Scale,
                VertexCount = n,
                SeVertexCount = full.SeVertexCount,
                SeTriangleCount = full.SeTriangleCount,
                Positions = (float[])g.Positions.Clone(), Normals = (float[])g.Normals.Clone(), Tangents = (float[])g.Tangents.Clone(),
                Binormals = (float[])g.Binormals.Clone(), Uvs = (float[])g.Uvs.Clone(),
                Indices = new int[g.IndexCount],
                Submeshes = new ExportedSubmesh[g.SubmeshCount],
                HasTangents = full.HasTangents,
                TangentSource = full.TangentSource,
                LoadMs = full.LoadMs,
                SkinnedBind = full.SkinnedBind,
            };
            for (int i = 0; i < g.IndexCount; i++) m.Indices[i] = (int)g.Indices[i];
            if (g.Skinned) { m.BoneIndices = (byte[])g.BoneIndices.Clone(); m.BoneWeights = (float[])g.BoneWeights.Clone(); }
            for (int s = 0; s < g.SubmeshCount; s++)
            {
                SubmeshRange r = g.Submeshes[s];
                m.Submeshes[s] = new ExportedSubmesh
                {
                    MaterialKey = r.MaterialKey, TriStart = (int)r.TriStart, TriCount = (int)r.TriCount, DrawTechnique = r.DrawTechnique,
                    MaterialName = names.Count == g.SubmeshCount ? names[s] : null,
                };
            }
            var mats = new List<MaterialInfo>();
            var seen = new HashSet<ulong>();
            if (full.Materials != null)
                foreach (ExportedSubmesh s in m.Submeshes)
                    foreach (MaterialInfo mi in full.Materials)
                        if (mi.Key == s.MaterialKey && seen.Add(mi.Key)) { mats.Add(mi); break; }
            m.Materials = mats.ToArray();
            return m;
        }
    }

    // ===================================================================================================== skins

 /// <summary>One skin's texture change for a material name (raw names as the definition stores them; null = unchanged).</summary>
    internal sealed class SkinTextureChange
    {
        public string ColorMetal, NormalGloss, Extensions, AlphaMask;
    }

    /// <summary>
 /// Skins resolved SE-side (CONTRACT §5.4 "Model keys and skins"): a skin's <c>SkinTextureChanges</c> (by material name)
 /// rewrites matching submesh materials; the variant is a separate model with its own material keys. A texture that
 /// cannot be resolved drops that override only.
 /// </summary>
    internal static class SkinResolve
    {
        /// <summary>True when <paramref name="changes"/> names at least one of the model's submesh materials.</summary>
        public static bool Overrides(ExportedModel baseModel, IDictionary<string, SkinTextureChange> changes)
        {
            if (baseModel == null || changes == null || changes.Count == 0 || baseModel.Submeshes == null) return false;
            foreach (ExportedSubmesh s in baseModel.Submeshes)
                if (s.MaterialName != null && changes.ContainsKey(s.MaterialName)) return true;
            return false;
        }

        /// <summary>
 /// The variant of <paramref name="baseModel"/> under <paramref name="changes"/>: same geometry arrays (shared,
 /// read-only), materials rebuilt through <paramref name="resolve"/> (raw names → MaterialInfo with absolute paths and
 /// key); <paramref name="dropped"/> lists overrides whose texture did not resolve (kept as the original).
 /// </summary>
        public static ExportedModel Variant(ExportedModel baseModel, ulong variantKey, IDictionary<string, SkinTextureChange> changes,
                                            Func<MaterialInfo, SkinTextureChange, MaterialInfo> apply)
        {
            var subs = new ExportedSubmesh[baseModel.Submeshes.Length];
            var mats = new List<MaterialInfo>();
            var byKey = new Dictionary<ulong, MaterialInfo>();
            for (int i = 0; i < subs.Length; i++)
            {
                ExportedSubmesh s = baseModel.Submeshes[i];
                MaterialInfo orig = null;
                if (baseModel.Materials != null) foreach (MaterialInfo mi in baseModel.Materials) if (mi.Key == s.MaterialKey) { orig = mi; break; }
                MaterialInfo use = orig;
                SkinTextureChange ch;
                if (orig != null && s.MaterialName != null && changes != null && changes.TryGetValue(s.MaterialName, out ch) && ch != null)
                    use = apply(orig, ch) ?? orig;
                if (use != null && !byKey.ContainsKey(use.Key)) { byKey[use.Key] = use; mats.Add(use); }
                subs[i] = new ExportedSubmesh { MaterialKey = use != null ? use.Key : s.MaterialKey, MaterialName = s.MaterialName, DrawTechnique = s.DrawTechnique, TriStart = s.TriStart, TriCount = s.TriCount };
            }
            return new ExportedModel
            {
                AssetName = baseModel.AssetName,
                ModelKey = variantKey,
                Lod = baseModel.Lod,
                PatternScale = baseModel.PatternScale,
                Scale = baseModel.Scale,
                VertexCount = baseModel.VertexCount,
                Positions = baseModel.Positions, Normals = baseModel.Normals, Tangents = baseModel.Tangents, Binormals = baseModel.Binormals, Uvs = baseModel.Uvs,
                Indices = baseModel.Indices,
                BoneIndices = baseModel.BoneIndices, BoneWeights = baseModel.BoneWeights, SkinnedBind = baseModel.SkinnedBind,
                Submeshes = subs,
                Materials = mats.ToArray(),
                HasTangents = baseModel.HasTangents,
                TangentSource = baseModel.TangentSource,
                SeVertexCount = baseModel.SeVertexCount,
                SeTriangleCount = baseModel.SeTriangleCount,
                LoadMs = baseModel.LoadMs,
            };
        }

        /// <summary>Merges several skins' changes in list order (a later skin wins per material and per texture).</summary>
        public static Dictionary<string, SkinTextureChange> Merge(IList<IDictionary<string, SkinTextureChange>> skins)
        {
            var r = new Dictionary<string, SkinTextureChange>(StringComparer.Ordinal);
            if (skins == null) return r;
            foreach (var s in skins)
            {
                if (s == null) continue;
                foreach (var kv in s)
                {
                    if (kv.Value == null) continue;
                    SkinTextureChange cur;
                    if (!r.TryGetValue(kv.Key, out cur)) { cur = new SkinTextureChange(); r[kv.Key] = cur; }
                    if (!string.IsNullOrEmpty(kv.Value.ColorMetal)) cur.ColorMetal = kv.Value.ColorMetal;
                    if (!string.IsNullOrEmpty(kv.Value.NormalGloss)) cur.NormalGloss = kv.Value.NormalGloss;
                    if (!string.IsNullOrEmpty(kv.Value.Extensions)) cur.Extensions = kv.Value.Extensions;
                    if (!string.IsNullOrEmpty(kv.Value.AlphaMask)) cur.AlphaMask = kv.Value.AlphaMask;
                }
            }
            return r;
        }
    }

    // ===================================================================================================== chunks

 /// <summary>
 /// The expected Subnautica chunk count of a part set (CONTRACT §5.4 "Chunk key and consumer rules", Wire's
 /// <see cref="ChunkMath"/>): distinct (entity, cell, materialKey, colour key, flags &amp; (NoShadows | MetalnessColorable))
 /// over the chunked parts. The SE side only logs it next to Subnautica's s5.scale (S5.N6).
 /// </summary>
    internal static class S5Chunks
    {
        /// <summary>Returns the chunk count; <paramref name="ownParts"/> counts the parts drawn as their own renderer (Dynamic, tool, floating, character).</summary>
        public static int Count(long entity, bool large, IEnumerable<PartSnapshot> parts, Func<ulong, IList<ulong>> materialsOf, out int ownParts)
        {
            var keys = new HashSet<ChunkKey>();
            ownParts = 0;
            uint targetClass = large ? GridClass.LargeGrid : GridClass.SmallGrid;
            foreach (PartSnapshot p in parts)
            {
                if (!ChunkMath.IsChunked(p.Flags)) { ownParts++; continue; }
                IList<ulong> mats = materialsOf(p.ModelKey);
                if (mats == null || p.Local == null) continue;
                ChunkCell cell = ChunkMath.CellOf(p.Local[9], p.Local[10], p.Local[11], targetClass);
                long ck = ChunkMath.ColourKey(p.H, p.S, p.V);
                uint f = ChunkMath.ChunkFlags(p.Flags);
                foreach (ulong mk in mats) keys.Add(new ChunkKey((ulong)entity, cell, mk, ck, f));
            }
            return keys.Count;
        }
    }

    // ===================================================================================================== de.s5 args

 /// <summary>
 /// The <c>de.s5</c> command syntax (C11 args and the <c>/de s5</c> chat verb): <c>{status: true}</c>, <c>{stats:
 /// true}</c>, <c>{set: {enabled, character, enterM, leaveM, keepS, maxEntities, readBudgetMs}}</c> (or those keys at the
 /// top level with <c>set: true</c>), <c>{export: entityId, sample: 20, seed}</c> (s5.part rows for N1), <c>{character:
 /// "on" | "off"}</c>, <c>{record: {name, c1, c2}}</c> / <c>{record: "stop"}</c>, <c>{models: {count: 200, send:
 /// true}}</c> (G1-E4 / N5 loader set), <c>{coh: {entityId, point: [x, y, z], seconds}}</c> (s5.coh rows for N4),
 /// <c>{trace: seconds}</c> (s5.sub and s5.char bone rows every tick for N3/N7a).
 /// </summary>
    internal static class S5Args
    {
        public const string ChatUsage = "/de s5 status | stats | set key=value… (enabled, character, enterM, leaveM, keepS, maxEntities, readBudgetMs) | export <entityId> [sample=20] [seed=1] | character on|off | record <name> [c1=true] [c2=true] | record stop | models [count] [send=true] | coh <entityId> [x y z] [seconds=10] | trace [seconds]";

        public static string ActionOf(IDictionary<string, object> args)
        {
            if (args == null) return "status";
            foreach (string k in new[] { "set", "export", "character", "record", "models", "coh", "trace" })
                if (args.ContainsKey(k)) return k;
            if (MiniJson.GetBool(args, "stats") == true) return "stats";
            return "status";
        }

        /// <summary>argv[0] is "s5"; returns the C11 args, or null with <paramref name="usage"/> set.</summary>
        public static Dictionary<string, object> ParseChat(string[] argv, out string usage)
        {
            usage = null;
            var a = new Dictionary<string, object>(StringComparer.Ordinal);
            var kv = new Dictionary<string, object>(StringComparer.Ordinal);
            var words = new List<string>();
            for (int i = 1; i < argv.Length; i++)
            {
                string t = argv[i];
                int eq = t.IndexOf('=');
                if (eq > 0) kv[t.Substring(0, eq).Trim()] = Value(t.Substring(eq + 1).Trim());
                else words.Add(t);
            }
            string verb = words.Count > 0 ? words[0].ToLowerInvariant() : "status";
            switch (verb)
            {
                case "status":
                case "stats":
                    if (words.Count > 1) { usage = ChatUsage; return null; }
                    a[verb] = true;
                    return a;
                case "set":
                    if (words.Count > 1 || kv.Count == 0) { usage = ChatUsage; return null; }
                    a["set"] = kv;
                    return a;
                case "export":
                {
                    long id;
                    if (words.Count != 2 || !long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) { usage = ChatUsage; return null; }
                    a["export"] = (double)id;
                    a["entityId"] = words[1];
                    foreach (var p in kv) a[p.Key] = p.Value;
                    return a;
                }
                case "character":
                    if (words.Count != 2 || !(words[1].Equals("on", StringComparison.OrdinalIgnoreCase) || words[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
                    { usage = ChatUsage; return null; }
                    a["character"] = words[1].ToLowerInvariant();
                    return a;
                case "record":
                    if (words.Count != 2) { usage = ChatUsage; return null; }
                    if (words[1].Equals("stop", StringComparison.OrdinalIgnoreCase)) { a["record"] = "stop"; return a; }
                    {
                        var r = new Dictionary<string, object>(StringComparer.Ordinal) { { "name", words[1] } };
                        foreach (var p in kv) r[p.Key] = p.Value;
                        a["record"] = r;
                        return a;
                    }
                case "models":
                {
                    var m = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (words.Count > 2) { usage = ChatUsage; return null; }
                    if (words.Count == 2)
                    {
                        double c;
                        if (!Num(words[1], out c)) { usage = ChatUsage; return null; }
                        m["count"] = c;
                    }
                    foreach (var p in kv) m[p.Key] = p.Value;
                    a["models"] = m;
                    return a;
                }
                case "coh":
                {
                    long id;
                    if (words.Count != 2 && words.Count != 5) { usage = ChatUsage; return null; }
                    if (!long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) { usage = ChatUsage; return null; }
                    var c = new Dictionary<string, object>(StringComparer.Ordinal) { { "entityId", words[1] } };
                    if (words.Count == 5)
                    {
                        var pt = new List<object>();
                        for (int i = 2; i < 5; i++)
                        {
                            double d;
                            if (!Num(words[i], out d)) { usage = ChatUsage; return null; }
                            pt.Add(d);
                        }
                        c["point"] = pt;
                    }
                    foreach (var p in kv) c[p.Key] = p.Value;
                    a["coh"] = c;
                    return a;
                }
                case "trace":
                {
                    double s = 10;
                    if (words.Count > 2 || (words.Count == 2 && !Num(words[1], out s))) { usage = ChatUsage; return null; }
                    a["trace"] = s;
                    return a;
                }
                default:
                    usage = ChatUsage;
                    return null;
            }
        }

        /// <summary>An entity id given as a number or a decimal string (C11 JSON numbers are doubles: ids above 2^53 need the string).</summary>
        public static bool TryEntityId(object v, out long id)
        {
            id = 0;
            if (v == null) return false;
            var s = v as string;
            if (s != null) return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            if (v is double)
            {
                double d = (double)v;
                if (Math.Abs(d) > 9007199254740992.0 || d != Math.Floor(d)) return false;
                id = (long)d;
                return true;
            }
            if (v is long) { id = (long)v; return true; }
            return false;
        }

        public static bool? Bool(object v)
        {
            if (v == null) return null;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            var s = v as string;
            if (s == null) return null;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "on" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "off" || s == "0" || s == "no") return false;
            return null;
        }

        public static double? Number(object v)
        {
            if (v == null) return null;
            if (v is double) return (double)v;
            double d;
            var s = v as string;
            return s != null && Num(s, out d) ? (double?)d : null;
        }

        private static object Value(string s)
        {
            double d;
            if (Num(s, out d)) return d;
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return s;
        }

        private static bool Num(string s, out double d)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
    }

    /// <summary>The run-time S5 settings (<c>de.s5 set</c>, environment <c>DEEPENGINEERS_S5</c> = on|off at Init).</summary>
    internal sealed class S5Settings
    {
        public const string EnvVar = "DEEPENGINEERS_S5";
        public bool Enabled = true;
        public bool Character = true;
        public double EnterM = S5Defaults.EnterM, LeaveM = S5Defaults.LeaveM;
        public double KeepS = S5Defaults.KeepMs / 1000.0;
        public int MaxEntities = S5Defaults.MaxEntities;
        public double ReadBudgetMs = S5Defaults.ReadBudgetMs;

        /// <summary>Applies a <c>set</c> object; returns the changed keys or null with <paramref name="why"/>.</summary>
        public List<string> Apply(IDictionary<string, object> set, out string why)
        {
            why = null;
            var changed = new List<string>();
            if (set == null || set.Count == 0) { why = "nothing to set"; return null; }
            var next = (S5Settings)MemberwiseClone();
            foreach (var kv in set)
            {
                string k = kv.Key;
                switch (k)
                {
                    case "enabled":
                    case "character":
                    {
                        bool? b = S5Args.Bool(kv.Value);
                        if (!b.HasValue) { why = k + " must be on/off"; return null; }
                        if (k == "enabled") next.Enabled = b.Value; else next.Character = b.Value;
                        break;
                    }
                    case "enterM":
                    case "leaveM":
                    case "keepS":
                    case "maxEntities":
                    case "readBudgetMs":
                    {
                        double? d = S5Args.Number(kv.Value);
                        if (!d.HasValue || double.IsNaN(d.Value) || double.IsInfinity(d.Value) || d.Value < 0) { why = k + " must be a number ≥ 0"; return null; }
                        if (k == "enterM") next.EnterM = d.Value;
                        else if (k == "leaveM") next.LeaveM = d.Value;
                        else if (k == "keepS") next.KeepS = d.Value;
                        else if (k == "maxEntities") next.MaxEntities = (int)Math.Min(S5Defaults.MaxEntities, Math.Floor(d.Value));
                        else next.ReadBudgetMs = Math.Min(16, d.Value);
                        break;
                    }
                    default:
                        why = "unknown setting " + k + " (enabled, character, enterM, leaveM, keepS, maxEntities, readBudgetMs)";
                        return null;
                }
                changed.Add(k);
            }
            if (next.LeaveM < next.EnterM) { why = "leaveM must be ≥ enterM"; return null; }
            Enabled = next.Enabled; Character = next.Character; EnterM = next.EnterM; LeaveM = next.LeaveM; KeepS = next.KeepS;
            MaxEntities = next.MaxEntities; ReadBudgetMs = next.ReadBudgetMs;
            return changed;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "enabled", Enabled }, { "character", Character }, { "enterM", EnterM }, { "leaveM", LeaveM }, { "keepS", KeepS },
                { "maxEntities", MaxEntities }, { "readBudgetMs", ReadBudgetMs },
            };
        }
    }

    // ===================================================================================================== rows

 /// <summary>
 /// The S5 JSONL rows of se-*.jsonl (<c>{"t", "ev": "s5.&lt;name&gt;", …}</c>; ROADMAP S5 "Builder contract"): s5.grid,
 /// s5.part, s5.edit, s5.sub, s5.char, s5.model, s5.coh, s5.budget. Doubles G17, keys as 0x-hex strings, entity ids as
 /// decimal numbers and strings (<c>entityId</c>, <c>entityIdStr</c>: JSON readers with doubles lose ids above 2^53).
 /// </summary>
    internal static class S5Rows
    {
        public static StringBuilder Head(string utc, string name)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s5." + name);
            return sb;
        }

        public static void Id(StringBuilder sb, string key, long id)
        {
            sb.Append(",\"").Append(key).Append("\":").Append(id.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"").Append(key).Append("Str\":\"").Append(id.ToString(CultureInfo.InvariantCulture)).Append('"');
        }

        public static void Hex(StringBuilder sb, string key, ulong v)
        {
            sb.Append(",\"").Append(key).Append("\":\"0x").Append(v.ToString("x16", CultureInfo.InvariantCulture)).Append('"');
        }

        public static void Str(StringBuilder sb, string key, string v)
        {
            sb.Append(",\"").Append(key).Append("\":");
            if (v == null) sb.Append("null"); else JsonText.AppendString(sb, v);
        }

        public static void Num(StringBuilder sb, string key, double v) { sb.Append(",\"").Append(key).Append("\":").Append(S3Rows.G(v)); }
        public static void Int(StringBuilder sb, string key, long v) { sb.Append(",\"").Append(key).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        public static void Bool(StringBuilder sb, string key, bool v) { sb.Append(",\"").Append(key).Append("\":").Append(v ? "true" : "false"); }

        public static void Floats(StringBuilder sb, string key, float[] v)
        {
            sb.Append(",\"").Append(key).Append("\":[");
            if (v != null) for (int i = 0; i < v.Length; i++) { if (i > 0) sb.Append(','); sb.Append(S3Rows.G(v[i])); }
            sb.Append(']');
        }

        public static void Doubles(StringBuilder sb, string key, double[] v, int o, int n)
        {
            sb.Append(",\"").Append(key).Append("\":[");
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(S3Rows.G(v[o + i])); }
            sb.Append(']');
        }

        public static void Matrix(StringBuilder sb, string key, SeMatrix m)
        {
            sb.Append(",\"").Append(key).Append("\":[");
            S3Rows.Vec(sb, m.Right); sb.Append(','); S3Rows.Vec(sb, m.Up); sb.Append(','); S3Rows.Vec(sb, m.Backward); sb.Append(','); S3Rows.Vec(sb, m.Translation);
            sb.Append(']');
        }

        public static void Vec(StringBuilder sb, string key, Vec3 v)
        {
            sb.Append(",\"").Append(key).Append("\":["); S3Rows.Vec(sb, v); sb.Append(']');
        }

        public static string End(StringBuilder sb) { sb.Append('}'); return sb.ToString(); }

        /// <summary>s5.part (S5.</summary>
        public static string Part(string utc, ulong tick, long ownerId, long rootId, PartSnapshot p, SeMatrix ownerWorld, SeMatrix rootWorld, SeMatrix? rel,
                                  Vec3 pivot, WireEntityPose rootPose, SiteMap site, string op)
        {
            StringBuilder sb = Head(utc, "part");
            Str(sb, "op", op);
            Int(sb, "tick", (long)tick);
            Id(sb, "entityId", ownerId);
            Id(sb, "rootId", rootId);
            Hex(sb, "partKey", p.PartKey);
            Hex(sb, "modelKey", p.ModelKey);
            Str(sb, "asset", p.AssetName);
            Int(sb, "class", S5Conv.ClassOf(p.Flags));
            Int(sb, "flags", p.Flags);
            Floats(sb, "local", p.Local);
            sb.Append(",\"hsv\":[").Append(S3Rows.G(p.H)).Append(',').Append(S3Rows.G(p.S)).Append(',').Append(S3Rows.G(p.V)).Append(']');
            sb.Append(",\"uvOffset\":[").Append(S3Rows.G(p.UvU)).Append(',').Append(S3Rows.G(p.UvV)).Append(']');
            Int(sb, "skinId", p.SkinId);
            Matrix(sb, "ownerWorld", ownerWorld);
            Matrix(sb, "rootWorld", rootWorld);
            if (rel.HasValue) Matrix(sb, "rel", rel.Value);
            Vec(sb, "pivot", pivot);
            WireEntityPose w = rootPose.RoundToFloat32();
            Vec(sb, "o", w.O);
            sb.Append(",\"q\":[").Append(S3Rows.G(w.Q.X)).Append(',').Append(S3Rows.G(w.Q.Y)).Append(',').Append(S3Rows.G(w.Q.Z)).Append(',').Append(S3Rows.G(w.Q.W)).Append(']');
            Num(sb, "s", w.S);
            Num(sb, "k", w.K);
            if (site != null)
            {
                sb.Append(",\"site\":{\"C\":["); S3Rows.Vec(sb, site.C);
                sb.Append("],\"Rs\":").Append(S3Rows.G(site.Rs)).Append('}');
            }
            return End(sb);
        }
    }
}
