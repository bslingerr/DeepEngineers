using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S6 "Collision B + combat": the meaning S6 gives to fields and bits that layout 1.0 carries but does not pin
 // (CONTRACT §5.4 "S6 combat conventions"; §2.6, §2.7, §4.1, §4.2). Layout 1.0 is unchanged (hash 0x4f3a35f6fc1eaf31;
 // no record, field, offset, capacity, message type or enumeration value changed): SkinBox.flags, DamageRecord.flags
 // bit 4, the section-id and section-centre formulas, the reject reasons, the dedup window and the damage factors get
 // their S6 meaning here, pinned on their own by the canonical text below (FNV-1a 64 0xb6ee880712f8253b, 54 lines,
 // 2,068 bytes), as S5 pinned MirrorConventions. The text is built from the constants of this file, so a changed
 // constant changes the hash and turns CombatConventionsTests red. Python twin: tools/worldmap/wire_ref.py
 // (s6_conventions_text), compared by tests/DeepEngineers.Wire.Tests/S6VectorTests.cs.
 // =================================================================================================================

 /// <summary>
 /// Why SE drops a C8 damage record (CONTRACT §4.1 "Application rules"; the first failing check in the order of
 /// <see cref="DamageRules.Validate"/>). The values are pinned by the S6 canonical text.
 /// </summary>
    public enum DamageReject
    {
        /// <summary>Applied (or, for a check, passed).</summary>
        None = 0,
        /// <summary><c>snGeneration</c> is not the current Subnautica generation (C0).</summary>
        Generation = 1,
        /// <summary><c>worldEpoch</c> is not SE's current world epoch.</summary>
        Epoch = 2,
        /// <summary><c>dedupSeq</c> 0 (never sent).</summary>
        Sequence = 3,
        /// <summary>The hit id was already admitted (exactly once).</summary>
        Duplicate = 4,
        /// <summary>The hit id is older than the dedup window (seq ≤ highWater − 8,192).</summary>
        TooOld = 5,
        /// <summary><c>unit</c> ≠ 1 (SN_HP).</summary>
        Unit = 6,
        /// <summary><c>amount</c> not finite, ≤ 0 or &gt; 500 SN_HP.</summary>
        Amount = 7,
        /// <summary><c>targetKind</c> not 1 (grid section) or 2 (character).</summary>
        Kind = 8,
        /// <summary><c>snDamageType</c> not Normal 0, Explosive 7 or Electrical 8.</summary>
        Type = 9,
        /// <summary>Unknown target entity, or a wrong root for it.</summary>
        Entity = 10,
        /// <summary>A section id not in the current or the previous SkinSet SE sent (a character section other than 0).</summary>
        Section = 11,
        /// <summary>The "frame = subgrid" flag disagrees with the target (set for a root grid or the character, clear for a subgrid).</summary>
        Frame = 12,
        /// <summary>A non-finite <c>localHit</c>.</summary>
        HitPoint = 13,
        /// <summary>No block at the hit cell nor in its 26 neighbours.</summary>
        NoBlock = 14,
        /// <summary>World <c>DestructibleBlocks</c> off, or the grid immune and the block not force-destructible.</summary>
        NotDestructible = 15,
        /// <summary><c>MyCharacter.CharactersCanDie</c> or the instance's <c>CharacterCanDie</c> is false (Creative).</summary>
        CannotDie = 16,
        /// <summary>SE's damage call changed nothing (safe zone, a fat block consumed it, admin invulnerability).</summary>
        NotApplied = 17,
    }

    /// <summary>The S6 combat conventions (CONTRACT §5.4): constants, the canonical text and its hash, the SE damage type names.</summary>
    public static class CombatConventions
    {
        /// <summary>The first line of the canonical text (<c>ext s6 combat &lt;n&gt;</c>).</summary>
        public const string Header = "ext s6 combat 1";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4.</summary>
        public const ulong PinnedHash = 0xb6ee880712f8253bUL;
        /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 54, CanonicalBytes = 2068;

        /// <summary>C8 <c>DamageRecord.flags</c> bits beyond the layout-pinned <see cref="DamageFlags"/> (bits 0-3).</summary>
        public static class C8FlagsExt
        {
            /// <summary>Bit 4: the hit was on <c>Player.main.liveMixin</c> (a character record).</summary>
            public const ushort GhostPlayer = 16;
        }

        /// <summary>Limits (CONTRACT §2.6, §4.1, §5.1, §5.4).</summary>
        public static class Limits
        {
            /// <summary>Proxy refresh threshold, millimetres (§2.6: 1 cm).</summary>
            public const int RefreshMm = 10;
            /// <summary>A skin rebuild follows block add/remove at most once per grid per this many SE ticks.</summary>
            public const int SkinDebounceTicks = 30;
            /// <summary>Character capsule radius, millimetres.</summary>
            public const int CapsuleRadiusMm = 250;
            /// <summary>Collider creations or refreshes per physics step, round robin.</summary>
            public const int MaxShapesPerStep = 256;
            /// <summary>The largest amount SE accepts, SN_HP.</summary>
            public const int MaxAmountSnHp = 500;
            /// <summary>Character capsule height, millimetres.</summary>
            public const int CapsuleHeightMm = 1800;
            /// <summary>The largest box half extent, millimetres (4 m).</summary>
            public const int MaxHalfExtentMm = 4000;
            /// <summary>Subnautica's C8 overflow queue (records; = <see cref="Layout.C8OverflowCapacity"/>).</summary>
            public const int OverflowRecords = 4096;
            /// <summary>SE's dedup window (hit ids per generation).</summary>
            public const int DedupWindow = 8192;
            /// <summary>The largest SkinSet payload, bytes (2 MiB).</summary>
            public const int SkinMaxBytes = 2097152;
        }

        /// <summary>Damage factors, SE units per SN_HP (CONTRACT §4.1, <c>damageScaleVersion</c> 1).</summary>
        public static class DamageScale
        {
            /// <summary>Character health points per SN_HP.</summary>
            public const double KChar = 1.0;
            /// <summary>Small-grid block integrity per SN_HP.</summary>
            public const double KSmall = 1.0;
            /// <summary>Large-grid block integrity per SN_HP.</summary>
            public const double KLarge = 25.0;
            /// <summary>Equals <see cref="WireInfo.DamageScaleVersion"/>.</summary>
            public const uint Version = 1;
        }

        /// <summary><c>SkinBox.flags</c> (CONTRACT §5.4 "C4 type 6 SkinSet (S6)").</summary>
        public static class SkinBoxFlags
        {
            /// <summary>Bit 0: the axes are not the identity rows (Subnautica puts the box on an orthonormalised child).</summary>
            public const uint Rotated = 1u;
            /// <summary>Bits 8-11: the source (<see cref="SkinBoxSource"/>).</summary>
            public const uint SourceShift = 8, SourceMask = 0xF00u;
        }

        /// <summary>The source nibble of <c>SkinBox.flags</c>.</summary>
        public static class SkinBoxSource
        {
            /// <summary>Merged box cells (segmentation).</summary>
            public const uint Segment = 0;
            /// <summary>A convex piece of a block's model collision as an OBB.</summary>
            public const uint ModelShapeObb = 1;
            /// <summary>The stand-in producer's synthetic boxes.</summary>
            public const uint Synthetic = 15;
        }

        /// <summary>The fixed section lattice (anchored at the grid's cell 0) and the box split limits.</summary>
        public static class SkinLattice
        {
            /// <summary>Cells per box axis, large grid (3 × 2.5 m: half extent 3.75 m).</summary>
            public const int MaxBoxCellsLarge = 3;
            /// <summary>Cells per section axis, large grid (15 m).</summary>
            public const int SectionCellsLarge = 6;
            /// <summary>Bits per axis in a section id.</summary>
            public const int SectionIdBits = 10;
            /// <summary>Cells per box axis, small grid (16 × 0.5 m: half extent 4.0 m).</summary>
            public const int MaxBoxCellsSmall = 16;
            /// <summary>Cells per section axis, small grid (16 m).</summary>
            public const int SectionCellsSmall = 32;
            /// <summary>Added to each section index (i ∈ [−512, 511]).</summary>
            public const int SectionIdBias = 512;
        }

        /// <summary>The Subnautica <c>DamageType</c> values that are forwarded (Normal, Explosive, Electrical).</summary>
        public static class SnDamageForward
        {
            public const ushort Normal = 0, Explosive = 7, Electrical = 8;
        }

        /// <summary>
 /// The SE damage type name (<c>MyStringHash.GetOrCompute</c>) for a forwarded Subnautica damage type: Normal →
 /// "DeepBite", Explosive → "DeepBlast", Electrical → "DeepShock"; null when the type is not forwarded.
 /// </summary>
        public static string SeDamageTypeName(ushort snDamageType)
        {
            switch (snDamageType)
            {
                case SnDamageForward.Normal: return "DeepBite";
                case SnDamageForward.Explosive: return "DeepBlast";
                case SnDamageForward.Electrical: return "DeepShock";
                default: return null;
            }
        }

        /// <summary>True for Normal, Explosive and Electrical.</summary>
        public static bool IsForwarded(ushort snDamageType) { return SeDamageTypeName(snDamageType) != null; }

        private struct ConstEntry
        {
            public readonly string Group, Name;
            public readonly ulong Value;
            public ConstEntry(string group, string name, ulong value) { Group = group; Name = name; Value = value; }
        }

        private static ConstEntry[] Entries()
        {
            var list = new List<ConstEntry>
            {
                new ConstEntry("C8FlagsExt", "GhostPlayer", C8FlagsExt.GhostPlayer),
                new ConstEntry("CombatLimits", "RefreshMm", Limits.RefreshMm),
                new ConstEntry("CombatLimits", "SkinDebounceTicks", Limits.SkinDebounceTicks),
                new ConstEntry("CombatLimits", "CapsuleRadiusMm", Limits.CapsuleRadiusMm),
                new ConstEntry("CombatLimits", "MaxShapesPerStep", Limits.MaxShapesPerStep),
                new ConstEntry("CombatLimits", "MaxAmountSnHp", Limits.MaxAmountSnHp),
                new ConstEntry("CombatLimits", "CapsuleHeightMm", Limits.CapsuleHeightMm),
                new ConstEntry("CombatLimits", "MaxHalfExtentMm", Limits.MaxHalfExtentMm),
                new ConstEntry("CombatLimits", "OverflowRecords", Limits.OverflowRecords),
                new ConstEntry("CombatLimits", "DedupWindow", Limits.DedupWindow),
                new ConstEntry("CombatLimits", "SkinMaxBytes", Limits.SkinMaxBytes),
                new ConstEntry("DamageScale", "KChar", Exact(DamageScale.KChar)),
                new ConstEntry("DamageScale", "KSmall", Exact(DamageScale.KSmall)),
                new ConstEntry("DamageScale", "KLarge", Exact(DamageScale.KLarge)),
                new ConstEntry("DamageScale", "Version", DamageScale.Version),
                new ConstEntry("SkinBoxFlags", "Rotated", SkinBoxFlags.Rotated),
                new ConstEntry("SkinBoxFlags", "SourceShift", SkinBoxFlags.SourceShift),
                new ConstEntry("SkinBoxFlags", "SourceMask", SkinBoxFlags.SourceMask),
                new ConstEntry("SkinBoxSource", "Segment", SkinBoxSource.Segment),
                new ConstEntry("SkinBoxSource", "ModelShapeObb", SkinBoxSource.ModelShapeObb),
                new ConstEntry("SkinBoxSource", "Synthetic", SkinBoxSource.Synthetic),
                new ConstEntry("SkinLattice", "MaxBoxCellsLarge", SkinLattice.MaxBoxCellsLarge),
                new ConstEntry("SkinLattice", "SectionCellsLarge", SkinLattice.SectionCellsLarge),
                new ConstEntry("SkinLattice", "SectionIdBits", SkinLattice.SectionIdBits),
                new ConstEntry("SkinLattice", "MaxBoxCellsSmall", SkinLattice.MaxBoxCellsSmall),
                new ConstEntry("SkinLattice", "SectionCellsSmall", SkinLattice.SectionCellsSmall),
                new ConstEntry("SkinLattice", "SectionIdBias", SkinLattice.SectionIdBias),
                new ConstEntry("SnDamageForward", "Normal", SnDamageForward.Normal),
                new ConstEntry("SnDamageForward", "Explosive", SnDamageForward.Explosive),
                new ConstEntry("SnDamageForward", "Electrical", SnDamageForward.Electrical),
            };
            foreach (DamageReject r in (DamageReject[])Enum.GetValues(typeof(DamageReject)))
                if (r != DamageReject.None) list.Add(new ConstEntry("DamageReject", r.ToString(), (ulong)(int)r));
            return list.ToArray();
        }

        /// <summary>A factor as an integer for the text (the v1 factors are whole numbers; anything else throws so a retune updates the text format).</summary>
        private static ulong Exact(double k)
        {
            if (k < 0 || k != Math.Floor(k) || k > 1e9) throw new InvalidOperationException("damage factor " + k.ToString("R", CultureInfo.InvariantCulture) + " is not a whole number: extend the canonical text");
            return (ulong)k;
        }

        /// <summary>The <c>format</c> lines (sorted by key), in text order.</summary>
        public static readonly string[] FormatLines =
        {
            "characterSection id 0, capsule r 0.25 h 1.8 centred on the C1 pelvis bone, axis pelvis->head",
            "hitId snGeneration:dedupSeq, dedupSeq 1.. per Subnautica generation",
            "seDamageType 0=DeepBite 7=DeepBlast 8=DeepShock",
            "sectionCentre (i*SectionCells+(SectionCells-1)/2)*GridSize per axis",
            "sectionId ((ix+512)<<20)|((iy+512)<<10)|(iz+512), i=floorDiv(roundAway(c/GridSize),SectionCells), c=box centre",
            "skinVersion 1.. per entity per stream, a SkinSet replaces the whole skin",
        };

        private static string _text;
        private static ulong _hash;

        /// <summary>
 /// The canonical text (CONTRACT §5.4; ASCII, '\n' after every line, the last included): <see cref="Header"/>; one
 /// <c>const &lt;group&gt; &lt;name&gt; &lt;value&gt;</c> line per constant sorted by group, then value, then name
 /// (ordinal); the <c>format</c> lines sorted by key.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            ConstEntry[] e = Entries();
            Array.Sort(e, (a, b) =>
            {
                int c = string.CompareOrdinal(a.Group, b.Group);
                if (c != 0) return c;
                c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            var sb = new StringBuilder(2200);
            sb.Append(Header).Append('\n');
            foreach (ConstEntry c in e)
                sb.Append("const ").Append(c.Group).Append(' ').Append(c.Name).Append(' ')
                  .Append(c.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            var formats = (string[])FormatLines.Clone();
            Array.Sort(formats, string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (compiled; equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash()
        {
            if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
            return _hash;
        }
    }
}
