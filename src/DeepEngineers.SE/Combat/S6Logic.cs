using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE.Combat
{
    // =====================================================================================================================
 // The game-independent parts of the S6 SE plugin (ROADMAP S6 "SE plugin"; CONTRACT §2.6, §4.1, §5.4 "C4 type 6
 // SkinSet (S6)", "S6 combat conventions"): settings, the sent-skin record, the per-block skin rule, the skin build and
 // its version stream, the combat gates, the damage check order, the de.s6 / de.spawn syntax, the test-record build pass,
 // the s6.* JSONL rows and the SE self-check. Compiled into tests/DeepEngineers.SE.Tests (no SE assembly). The game
 // adapters are CollisionSkin.cs, DamageConsumer.cs and S6Commands.cs.
 // =====================================================================================================================

 /// <summary>S6 on/off: environment variable <c>DEEPENGINEERS_S6=on|off</c> (default on), read once.</summary>
    internal static class S6Settings
    {
        public const string EnvVar = "DEEPENGINEERS_S6";

        public static readonly bool Enabled = Read();

        private static bool Read()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable(EnvVar);
                if (string.IsNullOrEmpty(v)) return true;
                v = v.Trim().ToLowerInvariant();
                return !(v == "off" || v == "0" || v == "false" || v == "no");
            }
            catch (Exception) { return true; }
        }

        /// <summary>Damage records drained per tick (the ring's capacity, CONTRACT §4.1).</summary>
        public const int MaxRecordsPerTick = 1024;
        /// <summary>Block collection budget per tick (CONTRACT §5.4 "Decomposition": ≤ 0.5 ms per tick, one grid at a time).</summary>
        public const double BuildBudgetMs = 0.5;
        /// <summary>Coverage is checked (union == cells, no overlap) on grids up to this many cells (cost bound).</summary>
        public const int CoverageCheckMaxCells = 50000;
        /// <summary>s6.reject rows: the first 200 per reason per minute.</summary>
        public const int RejectRowsPerMinute = 200;
        public const ulong RejectRowWindowMs = 60000;
        /// <summary>s6.stats once per second while something changed.</summary>
        public const ulong StatsRowMs = 1000;
        /// <summary>Box centres per section in a <c>de.s6 skin</c> reply (the launcher's damage plan hits box centres).</summary>
        public const int CensusBoxesPerSection = 8;
        /// <summary>Sections listed in a <c>de.s6 skin</c> reply (a C11 reply must stay far below half the 256 KiB ring).</summary>
        public const int CensusMaxSections = 128;
    }

    /// <summary>
 /// What SE last sent for one grid or subgrid (CONTRACT §4.1 step 5: the entity, its root, the current and the previous
 /// SkinSet's section ids). Written by <see cref="CollisionSkin"/>, read by the damage consumer.
 /// </summary>
    internal sealed class SentSkin
    {
        public long EntityId, RootId;
        public bool Subgrid, Large;
        public double GridSize;
        public uint Version;
        public HashSet<uint> Sections = new HashSet<uint>();
        public HashSet<uint> PreviousSections = new HashSet<uint>();
        public int Boxes;
        public int Bytes;
        /// <summary>S11 hook: the box around every skin box (S11GrabRules.SkinExtents of the sections sent), the grab size class's bounds.</summary>
        public Vec3 BoxMin, BoxMax;
        public bool HasBox;

        public bool KnowsSection(uint id) { return Sections.Contains(id) || PreviousSections.Contains(id); }
    }

    /// <summary>Which part of the skin a block gives (CONTRACT §5.4 "Decomposition", items a–c).</summary>
    internal enum BlockSkinKind { None = 0, Cells = 1, Shapes = 2 }

    internal static class SkinRules
    {
        /// <summary>
 /// (a) <c>BlockTopology == Cube</c>: its cells, whatever the PhysicsOption (slopes and deformed armor as full cells
 /// in v1); (b) another topology with PhysicsOption ≠ None: one OBB per convex piece of the model collision when it
 /// has shapes, else its cells (as SE's <c>AddShapesBox</c>); (c) PhysicsOption None, or no physics: nothing.
 /// </summary>
        public static BlockSkinKind Classify(bool hasPhysics, bool cubeTopology, bool physicsOptionNone, bool hasCollisionShapes)
        {
            if (!hasPhysics) return BlockSkinKind.None;
            if (cubeTopology) return BlockSkinKind.Cells;
            if (physicsOptionNone) return BlockSkinKind.None;
            return hasCollisionShapes ? BlockSkinKind.Shapes : BlockSkinKind.Cells;
        }
    }

    /// <summary>
 /// One skin build of one grid (game-independent): the cells that enter the segmentation, the OBBs of the model
 /// collision pieces (split so no half extent exceeds 4 m), then the sections through Wire's
 /// <see cref="CollisionSkinMath.BuildSections"/>. Cells are kept sorted (z, y, x) for a deterministic segmentation;
 /// OBBs in the order of CONTRACT §5.4 (block position z, y, x, then shape index, then split piece).
 /// </summary>
    internal sealed class SkinBuild
    {
        private struct ObbEntry
        {
            public int X, Y, Z, Shape, Piece;
            public SkinBoxData Box;
        }

        public readonly double GridSize;
        public readonly bool Large;
        public readonly List<I32x3> Cells = new List<I32x3>();
        private readonly List<ObbEntry> _obbs = new List<ObbEntry>();
        private readonly List<SkinBoxData> _split = new List<SkinBoxData>();
        public int Blocks, CellBlocks, ShapeBlocks, NoneBlocks, ShapeErrors;

        public SkinBuild(double gridSize, bool large)
        {
            GridSize = gridSize;
            Large = large;
        }

        public int ObbCount { get { return _obbs.Count; } }

        public void AddCells(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                        Cells.Add(new I32x3(x, y, z));
        }

        /// <summary>One convex piece of a block's model collision (already placed in the grid frame), split at 4 m.</summary>
        public void AddObb(int blockX, int blockY, int blockZ, int shapeIndex, SkinBoxData box)
        {
            _split.Clear();
            CollisionSkinMath.SplitObb(box, CombatConventions.Limits.MaxHalfExtentMm / 1000.0, _split);
            for (int i = 0; i < _split.Count; i++)
                _obbs.Add(new ObbEntry { X = blockX, Y = blockY, Z = blockZ, Shape = shapeIndex, Piece = i, Box = _split[i] });
        }

        /// <summary>The cells in (z, y, x) order (deterministic segmentation input).</summary>
        public void SortCells()
        {
            Cells.Sort((a, b) => a.z != b.z ? a.z.CompareTo(b.z) : a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        }

        public List<SkinBoxData> OrderedObbs()
        {
            var e = new List<ObbEntry>(_obbs);
            e.Sort((a, b) =>
            {
                if (a.Z != b.Z) return a.Z.CompareTo(b.Z);
                if (a.Y != b.Y) return a.Y.CompareTo(b.Y);
                if (a.X != b.X) return a.X.CompareTo(b.X);
                if (a.Shape != b.Shape) return a.Shape.CompareTo(b.Shape);
                return a.Piece.CompareTo(b.Piece);
            });
            var l = new List<SkinBoxData>(e.Count);
            foreach (ObbEntry x in e) l.Add(x.Box);
            return l;
        }

        /// <summary>The sections from the segments of <see cref="Cells"/> (SE's segmentation or Wire's greedy stand-in) and the OBBs.</summary>
        public List<SkinSectionData> Finish(IList<CellRange> segments)
        {
            int dropped;
            List<SkinSectionData> l = CollisionSkinMath.BuildSections(segments, OrderedObbs(), GridSize, Large, out dropped);
            Dropped = dropped;
            return l;
        }

        /// <summary>Pieces whose centre fell outside the section lattice (±512 sections per axis) in the last <see cref="Finish"/>.</summary>
        public int Dropped { get; private set; }

        /// <summary>The greedy stand-in for <c>MyVoxelSegmentation</c> (Wire's <see cref="CollisionSkinMath.MergeCellsGreedy"/>).</summary>
        public List<CellRange> GreedySegments()
        {
            var l = new List<CellRange>();
            CollisionSkinMath.MergeCellsGreedy(Cells, l);
            return l;
        }
    }

    /// <summary>
 /// The SkinSet version stream of one entity (CONTRACT §5.4 "Resend rule"): version 1, 2, 3… per entity per stream; a new
 /// complete SkinSet only when a rebuild changed a section (ids and content hashes compared) or a resend is forced; a
 /// stream restart (ClearAll) or the entity's EntityRemove starts again at 1. Keeps the current and the previous sent
 /// section ids (the damage check accepts either).
 /// </summary>
    internal sealed class SkinStream
    {
        private readonly Dictionary<uint, ulong> _sent = new Dictionary<uint, ulong>();
        public uint Version { get; private set; }
        public HashSet<uint> Current = new HashSet<uint>();
        public HashSet<uint> Previous = new HashSet<uint>();
        public long Restarts { get; private set; }

        /// <summary>True when <paramref name="built"/> differs from what was sent (or nothing was sent in this stream).</summary>
        public bool Differs(IList<SkinSectionData> built)
        {
            if (Version == 0) return true;
            if (built.Count != _sent.Count) return true;
            foreach (SkinSectionData s in built)
            {
                ulong h;
                if (!_sent.TryGetValue(s.SectionId, out h) || h != s.ContentHash) return true;
            }
            return false;
        }

        /// <summary>The sections that changed against the sent set (ids added, removed or with a new content hash).</summary>
        public int ChangedSections(IList<SkinSectionData> built)
        {
            int n = 0;
            var seen = new HashSet<uint>();
            foreach (SkinSectionData s in built)
            {
                seen.Add(s.SectionId);
                ulong h;
                if (!_sent.TryGetValue(s.SectionId, out h) || h != s.ContentHash) n++;
            }
            foreach (uint id in _sent.Keys) if (!seen.Contains(id)) n++;
            return n;
        }

        /// <summary>The next version for <paramref name="built"/>; call <see cref="Commit"/> once it is queued.</summary>
        public uint NextVersion { get { return Version + 1; } }

        public void Commit(IList<SkinSectionData> built)
        {
            Version++;
            Previous = Current;
            Current = new HashSet<uint>();
            _sent.Clear();
            foreach (SkinSectionData s in built)
            {
                Current.Add(s.SectionId);
                _sent[s.SectionId] = s.ContentHash;
            }
        }

        /// <summary>A stream restart: the next SkinSet is version 1 and is sent whatever it holds. Section ids stay known.</summary>
        public void Restart()
        {
            Version = 0;
            _sent.Clear();
            Restarts++;
        }
    }

    /// <summary>The application gates of CONTRACT §4.1 (decision tables; the game adapter reads the inputs).</summary>
    internal static class CombatGate
    {
        /// <summary>Blocks: world <c>DestructibleBlocks</c>, then <c>grid.BlocksDestructionEnabled || block.ForceBlockDestructible</c>.</summary>
        public static bool Blocks(bool worldDestructibleBlocks, bool gridDestructionEnabled, bool blockForceDestructible, out string reason)
        {
            if (!worldDestructibleBlocks) { reason = "world DestructibleBlocks off"; return false; }
            if (!gridDestructionEnabled && !blockForceDestructible) { reason = "grid not destructible (its DestructibleBlocks off, or immune)"; return false; }
            reason = null;
            return true;
        }

        /// <summary>Character: <c>MyCharacter.CharactersCanDie</c> (false in Creative), then the instance's <c>CharacterCanDie</c>.</summary>
        public static bool Character(bool creative, bool charactersCanDie, bool characterCanDie, out string reason)
        {
            if (!charactersCanDie) { reason = creative ? "Creative (MyCharacter.CharactersCanDie false)" : "MyCharacter.CharactersCanDie false"; return false; }
            if (!characterCanDie) { reason = "this character cannot die (CharacterCanDie false)"; return false; }
            reason = null;
            return true;
        }

        /// <summary>The applied factor (CONTRACT §4.1 table; S6.M7 swaps the two grid factors).</summary>
        public static double AppliedFactor(uint targetKind, bool largeGrid, bool swapGridFactors)
        {
            if (targetKind == DamageTargetKind.GridSection && swapGridFactors) largeGrid = !largeGrid;
            return DamageRules.Factor(targetKind, largeGrid);
        }

        /// <summary>The SE damage type name (<c>DeepBite</c> | <c>DeepBlast</c> | <c>DeepShock</c>), null when not forwarded.</summary>
        public static string SeType(ushort snDamageType) { return CombatConventions.SeDamageTypeName(snDamageType); }
    }

    /// <summary>The outcome of the checks before the application (CONTRACT §4.1 steps 1–5).</summary>
    internal struct DamageVerdict
    {
        public DamageReject Reject;
        public string Detail;
        public bool Character;
        public SentSkin Skin;
        /// <summary>The factor SE applies (its own; S6.M7 swapped).</summary>
        public double K;
        /// <summary>Subnautica's expected factor differs from SE's (counted, logged; SE always uses its own).</summary>
        public bool ScaleMismatch;
    }

    /// <summary>
 /// The damage check order of CONTRACT §4.1 on one record, with the Wire rules: 1 Generation, 2 Epoch, 3 the dedup window
 /// (Sequence, Duplicate, TooOld; the id is marked here, so a replay of a record rejected later is a Duplicate), 4 Unit,
 /// Amount, Kind, Type (and a non-finite hit point, Wire's <c>CheckPayload</c>), 5 Entity, Section, Frame. The
 /// application gates (NoBlock, NotDestructible, CannotDie, NotApplied) are the game adapter's.
 /// </summary>
    internal static class DamagePipeline
    {
        /// <param name="dedup">The window of the current generation; null = a dry run (no id is marked; seq 0 still rejected).</param>
 /// <param name="skinOf">The sent skin of an entity id, or null.</param>
 /// <param name="characterId">The character SE exports in C1 (0 = none).</param>
        public static DamageVerdict Check(DamageRecord r, uint snGeneration, uint worldEpoch, DedupWindow dedup, Func<long, SentSkin> skinOf,
                                          long characterId, bool swapGridFactors)
        {
            var v = new DamageVerdict();
            v.Reject = DamageRules.CheckHeader(r, snGeneration, worldEpoch);
            if (v.Reject != DamageReject.None)
            {
                v.Detail = v.Reject == DamageReject.Generation
                    ? "snGeneration " + r.snGeneration + " != current " + snGeneration
                    : "worldEpoch " + r.worldEpoch + " != current " + worldEpoch;
                return v;
            }
            if (dedup != null)
            {
                v.Reject = dedup.Admit(r.dedupSeq);
                if (v.Reject != DamageReject.None)
                {
                    v.Detail = "seq " + r.dedupSeq + " (highWater " + dedup.HighWater + ")";
                    return v;
                }
            }
            else if (r.dedupSeq == 0)
            {
                v.Reject = DamageReject.Sequence;
                v.Detail = "dedupSeq 0";
                return v;
            }
            v.Reject = DamageRules.CheckPayload(r);
            if (v.Reject != DamageReject.None)
            {
                v.Detail = PayloadDetail(r, v.Reject);
                return v;
            }
            long target = (long)r.targetEntityId;
            if (r.targetKind == DamageTargetKind.Character)
            {
                v.Character = true;
                if (characterId == 0 || target != characterId)
                {
                    v.Reject = DamageReject.Entity;
                    v.Detail = characterId == 0 ? "no character exported in C1" : "character " + target + " is not the exported character " + characterId;
                    return v;
                }
                if ((long)r.rootEntityId != target)
                {
                    v.Reject = DamageReject.Entity;
                    v.Detail = "character root " + (long)r.rootEntityId + " != the character " + target;
                    return v;
                }
                if (r.sectionId != 0)
                {
                    v.Reject = DamageReject.Section;
                    v.Detail = "character section " + r.sectionId + " (only 0)";
                    return v;
                }
                v.Reject = DamageRules.CheckFrame(r, false);
                if (v.Reject != DamageReject.None) { v.Detail = "flags 0x" + r.flags.ToString("x", CultureInfo.InvariantCulture) + ": frame = subgrid on the character"; return v; }
                v.K = CombatGate.AppliedFactor(DamageTargetKind.Character, false, swapGridFactors);
            }
            else
            {
                SentSkin s = skinOf != null ? skinOf(target) : null;
                v.Skin = s;
                if (s == null)
                {
                    v.Reject = DamageReject.Entity;
                    v.Detail = "entity " + target + " is not mirrored with a sent skin";
                    return v;
                }
                if ((long)r.rootEntityId != s.RootId)
                {
                    v.Reject = DamageReject.Entity;
                    v.Detail = "root " + (long)r.rootEntityId + " != the entity's root " + s.RootId;
                    return v;
                }
                if (!s.KnowsSection(r.sectionId))
                {
                    v.Reject = DamageReject.Section;
                    v.Detail = "section 0x" + r.sectionId.ToString("x8", CultureInfo.InvariantCulture) + " not in skin v" + s.Version + " nor the previous one";
                    return v;
                }
                v.Reject = DamageRules.CheckFrame(r, s.Subgrid);
                if (v.Reject != DamageReject.None)
                {
                    v.Detail = "flags 0x" + r.flags.ToString("x", CultureInfo.InvariantCulture) + (s.Subgrid ? ": frame = subgrid missing on a subgrid" : ": frame = subgrid on a root grid");
                    return v;
                }
                v.K = CombatGate.AppliedFactor(DamageTargetKind.GridSection, s.Large, swapGridFactors);
            }
            v.ScaleMismatch = !DamageRules.ScaleMatches(r.seScaleExpected, v.K);
            return v;
        }

        private static string PayloadDetail(DamageRecord r, DamageReject why)
        {
            switch (why)
            {
                case DamageReject.Unit: return "unit " + r.unit;
                case DamageReject.Amount: return "amount " + r.amount.ToString("R", CultureInfo.InvariantCulture);
                case DamageReject.Kind: return "targetKind " + r.targetKind;
                case DamageReject.Type: return "snDamageType " + r.snDamageType;
                case DamageReject.HitPoint: return "localHit not finite";
                default: return why.ToString();
            }
        }

        public static Vec3 Local(DamageRecord r) { return new Vec3(r.localHit.x, r.localHit.y, r.localHit.z); }

        /// <summary>How the hit point was found (s6.damage "via").</summary>
        public static string Via(ushort flags)
        {
            if ((flags & CombatConventions.C8FlagsExt.GhostPlayer) != 0) return "player";
            if ((flags & DamageFlags.Radius) != 0) return "radius";
            if ((flags & DamageFlags.MouthContact) != 0) return "mouth";
            if ((flags & DamageFlags.FallbackHitPoint) != 0) return "fallback";
            return "none";
        }
    }

    /// <summary>
 /// Builds test damage records for <c>de.s6 damage</c> (CONTRACT §5.4 "S6 control commands"; the SE-alone N1/N10 harness
 /// when the fake SN host is not used). Every field defaults to a valid record for the named target; any field can be
 /// overridden (the malformed cases of S6.N10).
 /// </summary>
    internal static class S6Inject
    {
        public static bool TryBuild(IDictionary<string, object> o, uint defaultGeneration, uint defaultEpoch, Func<uint> nextSeq,
                                    Func<long, SentSkin> skinOf, long characterId, out DamageRecord r, out string error)
        {
            r = default(DamageRecord);
            error = null;
            if (o == null) { error = "a record object"; return false; }
            object tv;
            o.TryGetValue("target", out tv);
            string kindText = (MiniJson.GetString(o, "kind") ?? "auto").Trim().ToLowerInvariant();
            double? kindNum = MiniJson.GetNumber(o, "kind");
            long target = 0;
            var ts = tv as string;
            bool charWord = ts != null && (ts.Trim().Equals("character", StringComparison.OrdinalIgnoreCase) || ts.Trim().Equals("char", StringComparison.OrdinalIgnoreCase));
            if (charWord) target = characterId;
            else if (tv != null && !S5Args.TryEntityId(tv, out target)) { error = "target: an entity id (decimal string), or \"character\""; return false; }
            if (tv == null && kindText == "character") target = characterId;
            if (tv == null && !(kindText == "character")) { error = "target required"; return false; }

            uint kind;
            if (kindNum.HasValue) kind = (uint)kindNum.Value;
            else if (kindText == "grid") kind = DamageTargetKind.GridSection;
            else if (kindText == "character" || kindText == "char") kind = DamageTargetKind.Character;
            else if (kindText == "auto") kind = charWord || (characterId != 0 && target == characterId) ? DamageTargetKind.Character : DamageTargetKind.GridSection;
            else { error = "kind: grid | character | auto"; return false; }

            double[] local;
            if (!Vec(o, "local", out local)) { error = "local: [x, y, z] SE-local metres of the target"; return false; }
            double? amount = MiniJson.GetNumber(o, "amount");
            if (!amount.HasValue)
            {
                object av;
                if (!(o.TryGetValue("amount", out av) && av is string && ((string)av).Trim().ToLowerInvariant() == "nan")) { error = "amount (SN_HP)"; return false; }
                amount = double.NaN;
            }
            ushort type;
            if (!TryType(o.ContainsKey("type") ? o["type"] : null, out type)) { error = "type: 0|7|8 or bite|blast|shock"; return false; }

            SentSkin skin = kind == DamageTargetKind.GridSection && skinOf != null ? skinOf(target) : null;
            uint section;
            object sv;
            o.TryGetValue("section", out sv);
            if (sv == null || (sv is string && ((string)sv).Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)))
                section = kind == DamageTargetKind.Character ? 0u : AutoSection(skin, new Vec3(local[0], local[1], local[2]));
            else
            {
                double? sn = sv is double ? (double?)(double)sv : null;
                uint parsed;
                var sstr = sv as string;
                if (sn.HasValue) section = (uint)sn.Value;
                else if (sstr != null && sstr.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                         uint.TryParse(sstr.Trim().Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) section = parsed;
                else if (sstr != null && uint.TryParse(sstr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) section = parsed;
                else { error = "section: a u32, \"0x…\" or \"auto\""; return false; }
            }

            long root;
            object rv;
            if (o.TryGetValue("root", out rv) && rv != null)
            {
                if (!S5Args.TryEntityId(rv, out root)) { error = "root: an entity id"; return false; }
            }
            else root = kind == DamageTargetKind.Character ? target : skin != null ? skin.RootId : target;

            ushort flags;
            double? fl = MiniJson.GetNumber(o, "flags");
            if (fl.HasValue) flags = (ushort)fl.Value;
            else
            {
                flags = DamageFlags.FallbackHitPoint;
                if (skin != null && skin.Subgrid) flags |= DamageFlags.FrameSubgrid;
            }

            double? k = MiniJson.GetNumber(o, "k");
            bool large = skin != null && skin.Large;
            double kExpected = k ?? DamageRules.Factor(kind == DamageTargetKind.Character ? DamageTargetKind.Character : DamageTargetKind.GridSection, large);

            double? seq = MiniJson.GetNumber(o, "seq");
            double? gen = MiniJson.GetNumber(o, "gen");
            double? epoch = MiniJson.GetNumber(o, "epoch");
            double? unit = MiniJson.GetNumber(o, "unit");
            r.dedupSeq = seq.HasValue ? (uint)seq.Value : nextSeq();
            r.snGeneration = gen.HasValue ? (uint)gen.Value : defaultGeneration;
            r.unit = unit.HasValue ? (uint)unit.Value : DamageUnit.SnHp;
            r.amount = (float)amount.Value;
            r.seScaleExpected = (float)kExpected;
            r.snDamageType = type;
            r.flags = flags;
            r.targetKind = kind;
            r.sectionId = section;
            r.targetEntityId = (ulong)target;
            r.rootEntityId = (ulong)root;
            r.snapshotTick = 0;
            r.localHit = new F32x3((float)local[0], (float)local[1], (float)local[2]);
            double[] w;
            if (Vec(o, "w", out w)) r.snWorldHit = new F32x3((float)w[0], (float)w[1], (float)w[2]);
            r.dealerTechType = (int)(MiniJson.GetNumber(o, "dealerTechType") ?? 0);
            r.worldEpoch = epoch.HasValue ? (uint)epoch.Value : defaultEpoch;
            r.snTimeSec = 0;
            return true;
        }

        /// <summary>The lattice section of the point when the skin has it, else the skin's section whose centre is nearest.</summary>
        public static uint AutoSection(SentSkin skin, Vec3 local)
        {
            if (skin == null) return 0;
            int ix, iy, iz;
            if (CollisionSkinMath.TrySectionIndex(local, skin.GridSize, skin.Large, out ix, out iy, out iz))
            {
                uint id = CollisionSkinMath.SectionId(ix, iy, iz);
                if (skin.Sections.Contains(id)) return id;
            }
            uint best = 0;
            double bestD = double.PositiveInfinity;
            foreach (uint id in skin.Sections)
            {
                int x, y, z;
                CollisionSkinMath.SectionIndexOf(id, out x, out y, out z);
                double d = (CollisionSkinMath.SectionCentre(x, y, z, skin.GridSize, skin.Large) - local).LengthSquared;
                if (d < bestD || (d == bestD && id < best)) { bestD = d; best = id; }
            }
            return best;
        }

        public static bool TryType(object v, out ushort type)
        {
            type = CombatConventions.SnDamageForward.Normal;
            if (v == null) return true;
            if (v is double) { double d = (double)v; if (d < 0 || d > ushort.MaxValue || d != Math.Floor(d)) return false; type = (ushort)d; return true; }
            var s = v as string;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "bite": case "normal": case "deepbite": type = CombatConventions.SnDamageForward.Normal; return true;
                case "blast": case "explosive": case "deepblast": type = CombatConventions.SnDamageForward.Explosive; return true;
                case "shock": case "electrical": case "deepshock": type = CombatConventions.SnDamageForward.Electrical; return true;
            }
            ushort n;
            if (ushort.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { type = n; return true; }
            return false;
        }

        private static bool Vec(IDictionary<string, object> o, string key, out double[] v)
        {
            v = null;
            object x;
            if (!o.TryGetValue(key, out x)) return false;
            var l = x as List<object>;
            if (l == null || l.Count != 3) return false;
            v = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (l[i] is double) v[i] = (double)l[i];
                else if (l[i] is string && ((string)l[i]).Trim().ToLowerInvariant() == "nan") v[i] = double.NaN;
                else return false;
            }
            return true;
        }
    }

    /// <summary>s6.reject rows: the first 200 per reason per minute (the counters keep everything).</summary>
    internal sealed class RejectRowLimiter
    {
        private readonly Dictionary<DamageReject, ulong> _start = new Dictionary<DamageReject, ulong>();
        private readonly Dictionary<DamageReject, int> _count = new Dictionary<DamageReject, int>();
        public long Suppressed { get; private set; }

        public bool Allow(DamageReject reason, ulong nowMs)
        {
            ulong s;
            if (!_start.TryGetValue(reason, out s) || nowMs - s >= S6Settings.RejectRowWindowMs)
            {
                _start[reason] = nowMs;
                _count[reason] = 0;
            }
            int n = _count[reason];
            if (n >= S6Settings.RejectRowsPerMinute) { Suppressed++; return false; }
            _count[reason] = n + 1;
            return true;
        }

        public void Clear() { _start.Clear(); _count.Clear(); }
    }

    /// <summary>de.s6 and de.spawn syntax (C11 args and the <c>/de s6</c>, <c>/de spawn</c> chat forms).</summary>
    internal static class S6Args
    {
        public const string ChatUsage = "/de s6 status | skin <entityId> [rows] | damage <entityId|character> <x> <y> <z> <amount> [type 0|7|8|bite|blast|shock] [apply] | selftest | resend <entityId|all>";
        public const string SpawnUsage = "/de spawn <TechType> [n=1] [dist=12] (relayed to Subnautica as de.spawn; n 1..20, dist 1..100 m)";

        public static string ActionOf(IDictionary<string, object> args)
        {
            if (args == null) return "status";
            foreach (string k in new[] { "damage", "skin", "resend", "selftest", "spawn" })
                if (args.ContainsKey(k)) return k;
            return "status";
        }

        /// <summary>argv[0] is "s6"; returns the C11 args, or null with <paramref name="usage"/> set.</summary>
        public static Dictionary<string, object> ParseChat(string[] argv, out string usage)
        {
            usage = null;
            var a = new Dictionary<string, object>(StringComparer.Ordinal);
            var words = new List<string>();
            for (int i = 1; i < argv.Length; i++) words.Add(argv[i]);
            string verb = words.Count > 0 ? words[0].ToLowerInvariant() : "status";
            switch (verb)
            {
                case "status":
                    if (words.Count > 1) { usage = ChatUsage; return null; }
                    a["status"] = true;
                    return a;
                case "selftest":
                    if (words.Count > 1) { usage = ChatUsage; return null; }
                    a["selftest"] = true;
                    return a;
                case "skin":
                {
                    long id;
                    if (words.Count < 2 || words.Count > 3 || !long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) { usage = ChatUsage; return null; }
                    if (words.Count == 3 && !words[2].Equals("rows", StringComparison.OrdinalIgnoreCase)) { usage = ChatUsage; return null; }
                    a["skin"] = words[1];
                    a["rows"] = words.Count == 3;
                    return a;
                }
                case "resend":
                {
                    long id;
                    if (words.Count != 2) { usage = ChatUsage; return null; }
                    if (words[1].Equals("all", StringComparison.OrdinalIgnoreCase)) { a["resend"] = "all"; return a; }
                    if (!long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) { usage = ChatUsage; return null; }
                    a["resend"] = words[1];
                    return a;
                }
                case "damage":
                {
                    // damage <id|character> <x> <y> <z> <amount> [type] [apply]
                    if (words.Count < 6 || words.Count > 8) { usage = ChatUsage; return null; }
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    long id;
                    string t = words[1];
                    if (t.Equals("character", StringComparison.OrdinalIgnoreCase) || t.Equals("char", StringComparison.OrdinalIgnoreCase)) d["target"] = "character";
                    else if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) d["target"] = t;
                    else { usage = ChatUsage; return null; }
                    var p = new List<object>();
                    for (int i = 2; i < 5; i++)
                    {
                        double x;
                        if (!Num(words[i], out x)) { usage = ChatUsage; return null; }
                        p.Add(x);
                    }
                    d["local"] = p;
                    double amount;
                    if (!Num(words[5], out amount)) { usage = ChatUsage; return null; }
                    d["amount"] = amount;
                    d["section"] = "auto";
                    bool apply = false;
                    for (int i = 6; i < words.Count; i++)
                    {
                        if (words[i].Equals("apply", StringComparison.OrdinalIgnoreCase)) { apply = true; continue; }
                        ushort type;
                        object tv = words[i];
                        double tn;
                        if (Num(words[i], out tn)) tv = tn;
                        if (!S6Inject.TryType(tv, out type) || d.ContainsKey("type")) { usage = ChatUsage; return null; }
                        d["type"] = (double)type;
                    }
                    a["damage"] = d;
                    a["apply"] = apply;
                    return a;
                }
                default:
                    usage = ChatUsage;
                    return null;
            }
        }

        /// <summary><c>/de spawn &lt;TechType&gt; [n] [dist]</c> → the <c>de.spawn</c> args {techType, count, dist}; null with usage.</summary>
        public static Dictionary<string, object> ParseSpawn(string[] argv, out string usage)
        {
            usage = null;
            if (argv.Length < 2 || argv.Length > 4) { usage = SpawnUsage; return null; }
            string tech = argv[1].Trim();
            if (tech.Length == 0 || tech.Length > 64) { usage = SpawnUsage; return null; }
            foreach (char c in tech) if (!(char.IsLetterOrDigit(c) || c == '_')) { usage = SpawnUsage; return null; }
            double n = 1, dist = 12;
            if (argv.Length > 2 && (!Num(argv[2], out n) || n < 1 || n > 20 || n != Math.Floor(n))) { usage = SpawnUsage; return null; }
            if (argv.Length > 3 && (!Num(argv[3], out dist) || dist < 1 || dist > 100)) { usage = SpawnUsage; return null; }
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "techType", tech }, { "count", n }, { "dist", dist } };
        }

        public static bool Num(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }

    /// <summary>The s6.* JSONL rows of the SE plugin (formats: the S6 status document; ids as in s5 rows: number and Str).</summary>
    internal static class S6Rows
    {
        public static StringBuilder Head(string utc, string name)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s6." + name);
            return sb;
        }

        public static void Cell(StringBuilder sb, string key, I32x3? c)
        {
            sb.Append(",\"").Append(key).Append("\":");
            if (!c.HasValue) { sb.Append("null"); return; }
            sb.Append('[').Append(c.Value.x.ToString(CultureInfo.InvariantCulture)).Append(',').Append(c.Value.y.ToString(CultureInfo.InvariantCulture))
              .Append(',').Append(c.Value.z.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        public static void F3(StringBuilder sb, string key, F32x3 v)
        {
            sb.Append(",\"").Append(key).Append("\":[").Append(S3Rows.G(v.x)).Append(',').Append(S3Rows.G(v.y)).Append(',').Append(S3Rows.G(v.z)).Append(']');
        }

        public static void Hex32(StringBuilder sb, string key, uint v)
        {
            sb.Append(",\"").Append(key).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"").Append(key).Append("Hex\":\"0x").Append(v.ToString("x8", CultureInfo.InvariantCulture)).Append('"');
        }

        /// <summary>s6.reject {ev, id, reason, detail, kind, target, section, amount, type}.</summary>
        public static string Reject(string utc, DamageRecord r, DamageReject reason, string detail, bool injected)
        {
            var sb = Head(utc, "reject");
            S5Rows.Str(sb, "id", DamageRules.HitId(r.snGeneration, r.dedupSeq));
            S5Rows.Str(sb, "reason", reason.ToString());
            S5Rows.Str(sb, "detail", detail);
            S5Rows.Str(sb, "kind", KindName(r.targetKind));
            S5Rows.Id(sb, "target", (long)r.targetEntityId);
            Hex32(sb, "section", r.sectionId);
            S5Rows.Num(sb, "amount", r.amount);
            S5Rows.Int(sb, "type", r.snDamageType);
            if (injected) S5Rows.Bool(sb, "injected", true);
            return S5Rows.End(sb);
        }

        public static string KindName(uint targetKind)
        {
            return targetKind == DamageTargetKind.GridSection ? "grid" : targetKind == DamageTargetKind.Character ? "character" : "kind" + targetKind;
        }

        /// <summary>Everything an applied record's s6.damage row and DE.Damage line carry.</summary>
        internal sealed class Applied
        {
            public DamageRecord R;
            public double K, KExpected, Se, AppliedAmount;
            public string SeType, BlockDef;
            public I32x3? Block;
            public bool Searched;
            /// <summary>Review F2: max |cell from F⁻¹(snWorldHit) via the live grid matrix − cell from localHit|; −1 unknown.</summary>
            public int WorldCellDelta = -1;
            public double EffBefore = double.NaN, EffAfter = double.NaN, HpBefore = double.NaN, HpAfter = double.NaN;
            public ulong Tick;
            public bool Injected;
        }

        /// <summary>s6.damage {ev, id, gen, seq, kind, target, root, section, flags, via, snType, seType, amount, k, kExpected, se, block, blockDef, searched, effBefore, effAfter, applied, hpBefore, hpAfter, local, tick, snTime, dealer}.</summary>
        public static string Damage(string utc, Applied a)
        {
            DamageRecord r = a.R;
            var sb = Head(utc, "damage");
            S5Rows.Str(sb, "id", DamageRules.HitId(r.snGeneration, r.dedupSeq));
            S5Rows.Int(sb, "gen", r.snGeneration);
            S5Rows.Int(sb, "seq", r.dedupSeq);
            S5Rows.Str(sb, "kind", KindName(r.targetKind));
            S5Rows.Id(sb, "target", (long)r.targetEntityId);
            S5Rows.Id(sb, "root", (long)r.rootEntityId);
            Hex32(sb, "section", r.sectionId);
            S5Rows.Int(sb, "flags", r.flags);
            S5Rows.Str(sb, "via", DamagePipeline.Via(r.flags));
            S5Rows.Int(sb, "snType", r.snDamageType);
            S5Rows.Str(sb, "seType", a.SeType);
            S5Rows.Num(sb, "amount", r.amount);
            S5Rows.Num(sb, "k", a.K);
            S5Rows.Num(sb, "kExpected", a.KExpected);
            S5Rows.Num(sb, "se", a.Se);
            Cell(sb, "block", a.Block);
            S5Rows.Str(sb, "blockDef", a.BlockDef);
            S5Rows.Bool(sb, "searched", a.Searched);
            S5Rows.Int(sb, "worldCellDelta", a.WorldCellDelta);   // review F2
            Num(sb, "effBefore", a.EffBefore);
            Num(sb, "effAfter", a.EffAfter);
            S5Rows.Num(sb, "applied", a.AppliedAmount);
            Num(sb, "hpBefore", a.HpBefore);
            Num(sb, "hpAfter", a.HpAfter);
            F3(sb, "local", r.localHit);
            S5Rows.Int(sb, "tick", (long)a.Tick);
            S5Rows.Num(sb, "snTime", r.snTimeSec);
            S5Rows.Int(sb, "dealer", r.dealerTechType);
            if (a.Injected) S5Rows.Bool(sb, "injected", true);
            return S5Rows.End(sb);
        }

        /// <summary>The <c>DE.Damage applied …</c> line (CONTRACT §4.1).</summary>
        public static string AppliedLine(Applied a)
        {
            DamageRecord r = a.R;
            var sb = new StringBuilder(256);
            sb.Append("DE.Damage applied id=").Append(DamageRules.HitId(r.snGeneration, r.dedupSeq))
              .Append(" unit=SN_HP k=").Append(S3Rows.G(a.K))
              .Append(" se=").Append(S3Rows.G(a.Se))
              .Append(" applied=").Append(S3Rows.G(Math.Round(a.AppliedAmount, 4)))
              .Append(" target=").Append(((long)r.targetEntityId).ToString(CultureInfo.InvariantCulture))
              .Append(" kind=").Append(KindName(r.targetKind))
              .Append(" section=").Append(r.sectionId.ToString(CultureInfo.InvariantCulture))
              .Append(" block=");
            if (a.Block.HasValue) sb.Append(a.Block.Value.x).Append(',').Append(a.Block.Value.y).Append(',').Append(a.Block.Value.z);
            else sb.Append('-');
            sb.Append(" type=").Append(a.SeType)
              .Append(" amount=").Append(S3Rows.G(r.amount));
            if (a.Injected) sb.Append(" (de.s6 damage)");
            return sb.ToString();
        }

        private static void Num(StringBuilder sb, string key, double v)
        {
            if (double.IsNaN(v)) { sb.Append(",\"").Append(key).Append("\":null"); return; }
            S5Rows.Num(sb, key, v);
        }

        /// <summary>s6.skin {ev, entity, root, subgrid, large, gridSize, version, why, sections, boxes, segmentBoxes, obbBoxes, cells, bytes, buildMs, coverage, lane, detail?}.</summary>
        public static string Skin(string utc, string ev, SentSkin s, string why, IList<SkinSectionData> sections, int cells, int bytes, double buildMs,
                                  string coverage, int lane, bool detail, string note)
        {
            var sb = Head(utc, "skin");
            S5Rows.Str(sb, "op", ev);
            S5Rows.Id(sb, "entity", s.EntityId);
            S5Rows.Id(sb, "root", s.RootId);
            S5Rows.Bool(sb, "subgrid", s.Subgrid);
            S5Rows.Bool(sb, "large", s.Large);
            S5Rows.Num(sb, "gridSize", s.GridSize);
            S5Rows.Int(sb, "version", s.Version);
            S5Rows.Str(sb, "why", why);
            int boxes = 0, seg = 0, obb = 0;
            if (sections != null)
                foreach (SkinSectionData sec in sections)
                    foreach (SkinBoxData b in sec.Boxes)
                    {
                        boxes++;
                        uint src = (b.Flags & CombatConventions.SkinBoxFlags.SourceMask) >> (int)CombatConventions.SkinBoxFlags.SourceShift;
                        if (src == CombatConventions.SkinBoxSource.ModelShapeObb) obb++; else seg++;
                    }
            S5Rows.Int(sb, "sections", sections != null ? sections.Count : 0);
            S5Rows.Int(sb, "boxes", boxes);
            S5Rows.Int(sb, "segmentBoxes", seg);
            S5Rows.Int(sb, "obbBoxes", obb);
            S5Rows.Int(sb, "cells", cells);
            S5Rows.Int(sb, "bytes", bytes);
            S5Rows.Num(sb, "buildMs", Math.Round(buildMs, 4));
            S5Rows.Str(sb, "coverage", coverage);
            S5Rows.Int(sb, "lane", lane);
            if (note != null) S5Rows.Str(sb, "note", note);
            if (detail && sections != null)
            {
                sb.Append(",\"detail\":[");
                for (int i = 0; i < sections.Count; i++)
                {
                    SkinSectionData sec = sections[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(sec.SectionId.ToString(CultureInfo.InvariantCulture)).Append(",\"c\":[");
                    S3Rows.Vec(sb, sec.Centre);
                    sb.Append("],\"n\":").Append(sec.Boxes.Count).Append(",\"hash\":\"0x").Append(sec.ContentHash.ToString("x16", CultureInfo.InvariantCulture)).Append("\"}");
                }
                sb.Append(']');
            }
            return S5Rows.End(sb);
        }
    }

    /// <summary>
 /// The SE half of the S6 self-check (<c>de.s6 {selftest}</c>): the combat conventions hash against its pin, a SkinSet
 /// round trip through Wire's codec, the section lattice, the dedup window (new, duplicate, too old, reordered), the hit
 /// cell rounding (half away from zero), the factors and the gate tables. Game-independent; the game adapter adds what
 /// it can see (segmentation available, the world's gates).
 /// </summary>
    internal static class S6SelfTest
    {
        public static bool Run(out string detail)
        {
            var bad = new List<string>();
            try
            {
                if (CombatConventions.Hash() != CombatConventions.PinnedHash) bad.Add("conventions hash 0x" + CombatConventions.Hash().ToString("x16", CultureInfo.InvariantCulture));
                uint scaleVersion = CombatConventions.DamageScale.Version;
                if (scaleVersion != WireInfo.DamageScaleVersion) bad.Add("damageScaleVersion");

                // a small large-grid hull: 8×2×2 cells and one rotated model-shape piece
                var b = new SkinBuild(2.5, true);
                b.AddCells(0, 0, 0, 7, 1, 1);
                b.SortCells();
                List<CellRange> seg = b.GreedySegments();
                string cov;
                if (!CollisionSkinMath.CheckCoverage(b.Cells, seg, out cov)) bad.Add("coverage " + cov);
                List<SkinSectionData> secs = b.Finish(seg);
                if (secs.Count != 2) bad.Add("sections " + secs.Count + " (expected 2 for x 0..7 at 6 cells per section)");
                byte[] payload = SkinCodec.Encode(42, 1, secs);
                if (payload.Length != SkinCodec.PayloadBytes(secs)) bad.Add("payload length");
                var back = new List<SkinSectionData>();
                SkinSetHeader h;
                string err;
                if (!SkinCodec.TryDecode(payload, 0, payload.Length, out h, back, out err)) bad.Add("decode " + err);
                else if (h.entityId != 42 || h.skinVersion != 1 || back.Count != secs.Count) bad.Add("round trip header");
                else
                    for (int i = 0; i < back.Count; i++)
                        if (back[i].SectionId != secs[i].SectionId || CollisionSkinMath.SectionContentHash(back[i]) != secs[i].ContentHash) bad.Add("round trip section " + i);

                // dedup
                var w = new DedupWindow();
                w.Reset(7);
                if (w.Admit(1) != DamageReject.None || w.Admit(1) != DamageReject.Duplicate || w.Admit(0) != DamageReject.Sequence) bad.Add("dedup basic");
                if (w.Admit(10000) != DamageReject.None || w.Admit(2) != DamageReject.TooOld || w.Admit(9000) != DamageReject.None || w.Reordered < 1) bad.Add("dedup window");

                // hit cell: half away from zero
                I32x3 c = DamageRules.HitCell(new Vec3(1.25, -1.25, 3.74), 2.5);
                if (c.x != 1 || c.y != -1 || c.z != 1) bad.Add("hit cell " + c.x + "," + c.y + "," + c.z);

                // factors and gates
                if (DamageRules.Factor(DamageTargetKind.GridSection, true) != 25.0 || DamageRules.Factor(DamageTargetKind.GridSection, false) != 1.0 ||
                    DamageRules.Factor(DamageTargetKind.Character, false) != 1.0) bad.Add("factors");
                string why;
                if (CombatGate.Character(true, false, false, out why) || CombatGate.Blocks(false, true, true, out why) || !CombatGate.Blocks(true, false, true, out why))
                    bad.Add("gates");
                if (CombatConventions.SeDamageTypeName(0) != "DeepBite" || CombatConventions.SeDamageTypeName(7) != "DeepBlast" ||
                    CombatConventions.SeDamageTypeName(8) != "DeepShock" || CombatConventions.SeDamageTypeName(3) != null) bad.Add("type names");
            }
            catch (Exception e) { bad.Add(e.GetType().Name + ": " + e.Message); }
            detail = bad.Count == 0 ? "conventions 0x" + CombatConventions.PinnedHash.ToString("x16", CultureInfo.InvariantCulture) + ", codec, lattice, dedup, hit cell, factors, gates"
                                    : string.Join("; ", bad.ToArray());
            return bad.Count == 0;
        }
    }
}
