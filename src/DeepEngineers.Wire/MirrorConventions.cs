using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 "Whole grids + character": the meaning S5 gives to fields that layout 1.0 carries but does not pin
 // (CONTRACT §5.2 "Layout decision for S5", §5.4 "C1–C4 conventions for S5"). Layout 1.0 is unchanged (hash
 // 0x4f3a35f6fc1eaf31; no record, field, offset, capacity, message type or enumeration value changed): PartAdd.flags
 // and skinId, GridAdd.flags, CharacterAdd.flags, C1 bonesVersion and the opaque model/part key texts get their S5
 // meaning here, pinned on their own by the canonical text below (FNV-1a 64 0x31dfd6f9befdef5d, 37 lines, 1,539
 // bytes), as the C1 swim extension is (SwimWire). The text is built from the constants of this file, so a changed
 // constant changes the hash and turns MirrorConventionsTests red. Python twin: tools/worldmap/wire_ref.py (S5
 // section: its own table, keys and payloads), compared by tests/DeepEngineers.Wire.Tests/S5VectorTests.cs.
 //
 // Version nibble: bits 28-31 of PartAdd.flags, GridAdd.flags and CharacterAdd.flags = 1 from an S5 producer. 0 = an
 // S3 producer (a PartAdd is a rigid chunked cube part with shadows; a GridAdd is a grid; a CharacterAdd is ignored).
 // Any other value: the record is ignored and counted (conventionsRejected), not an error.
 // =================================================================================================================

 /// <summary>C4 <c>CharacterAdd.flags</c> (S5).</summary>
    public static class MirrorCharacterFlags
    {
        /// <summary>The character is the player's (C1 <c>characterId</c>); the only one mirrored in v1.</summary>
        public const uint Player = 1u;
        /// <summary>The default Engineer replaced the player's own model (its export failed; <c>DE.S5 char FALLBACK</c>).</summary>
        public const uint Fallback = 2u;
        /// <summary>Version nibble 1 (bits 28-31).</summary>
        public const uint Version1 = 1u << 28;
    }

    /// <summary>Part keys of a character's parts (C4 PartAdd <c>partKey</c> under the character's entity id).</summary>
    public static class MirrorCharacterPart
    {
        /// <summary>The body model (<c>|char=body</c>): every submesh not disabled in first person.</summary>
        public const ulong Body = 1;
        /// <summary>The head model (<c>|char=head1st</c>): <c>MaterialsDisabledIn1st</c>; flag <c>HiddenInFirstPerson</c>.</summary>
        public const ulong Head1st = 2;
        /// <summary>The held tool (class Tool, <c>Dynamic</c>, posed by its C2 row); optional in S5.</summary>
        public const ulong Tool = 3;
    }

    /// <summary>C4 <c>GridAdd.flags</c> (S5).</summary>
    public static class MirrorGridFlags
    {
        /// <summary><c>MyCubeGrid.IsStatic</c>.</summary>
        public const uint Static = 1u;
        /// <summary>Not the root of its physical hierarchy: <c>parentEntityId</c> = the root (flattened).</summary>
        public const uint Subgrid = 2u;
        /// <summary>The registration of a <c>MyFloatingObject</c> (<c>targetClass</c> 2, ignored).</summary>
        public const uint FloatingObject = 4u;
        /// <summary>Version nibble 1 (bits 28-31).</summary>
        public const uint Version1 = 1u << 28;
    }

    /// <summary>Limits of the S5 conventions.</summary>
    public static class MirrorLimits
    {
        /// <summary>Bone influences per skinned vertex (the 76 B vertex holds 4).</summary>
        public const int MaxSkinWeights = 4;
        /// <summary>Bones of the canonical skeleton <c>canon1</c> (<see cref="MirrorSkeleton"/>).</summary>
        public const int CanonicalBones = 78;
    }

    /// <summary>The class of a PartAdd, <c>flags &amp; ClassMask</c> (S5). 0 from a version-1 producer means CubePart.</summary>
    public static class MirrorPartClass
    {
        /// <summary><c>grid.RenderData.Cells[*].CubeParts</c> (S3's parts); chunked.</summary>
        public const uint CubePart = 1;
        /// <summary>An armor edge (the render cell's <c>m_edgesToRender</c>); chunked, <c>NoShadows</c>.</summary>
        public const uint Edge = 2;
        /// <summary><c>MySlimBlock.FatBlock.Model</c> at the fat block matrix; chunked.</summary>
        public const uint FatBlock = 3;
        /// <summary><c>MyEntity.Subparts</c> (nested), composed grid-local; chunked unless <c>Dynamic</c>.</summary>
        public const uint Subpart = 4;
        /// <summary>A skinned character part (<see cref="MirrorCharacterPart"/> Body, Head1st).</summary>
        public const uint CharacterBody = 5;
        /// <summary>The held tool (optional); own child, <c>Dynamic</c>.</summary>
        public const uint Tool = 6;
        /// <summary>A floating object's model (part key 1); own child of its Entity.</summary>
        public const uint FloatingObject = 7;
    }

    /// <summary>C4 <c>PartAdd.flags</c> bits (S5).</summary>
    public static class MirrorPartFlags
    {
        /// <summary>The part class (<see cref="MirrorPartClass"/>).</summary>
        public const uint ClassMask = 15u;
        /// <summary>Its own child transform posed from C2's subpart table, never merged into a chunk.</summary>
        public const uint Dynamic = 16u;
        /// <summary>Not drawn while C1 <c>cameraMode</c> is FirstPerson.</summary>
        public const uint HiddenInFirstPerson = 32u;
        /// <summary>The model is a <c>BuildProgressModels[i]</c> stage (information, N2 logs).</summary>
        public const uint BuildStage = 64u;
        /// <summary>SE deforms this instance (armor skinning); not drawn deformed in v1, counted.</summary>
        public const uint Deformed = 128u;
        /// <summary>Drawn without shadows (edges).</summary>
        public const uint NoShadows = 256u;
        /// <summary>SE's MetalnessColorable render flag (characters, skins, fat blocks); used by the S3.C13 paint pass.</summary>
        public const uint MetalnessColorable = 512u;
        /// <summary>Version nibble 1 (bits 28-31).</summary>
        public const uint Version1 = 1u << 28;
        /// <summary>The version nibble.</summary>
        public const uint VersionMask = 0xF0000000u;
    }

    /// <summary>How a consumer treats a PartAdd, GridAdd or CharacterAdd by its version nibble.</summary>
    public enum MirrorRecordVersion
    {
        /// <summary>Nibble 0: an S3 producer (S3 semantics; a CharacterAdd is ignored).</summary>
        Legacy = 0,
        /// <summary>Nibble 1: these conventions.</summary>
        Version1 = 1,
        /// <summary>Any other nibble: ignore the record and count it (<c>conventionsRejected</c>).</summary>
        Rejected = 2,
    }

    /// <summary>
 /// The S5 mirror conventions (CONTRACT §5.4 "C1–C4 conventions for S5"): canonical text and hash, the version nibble,
 /// flag builders, model/part/skin keys and the character records. No allocation except in the key text helpers.
 /// </summary>
    public static class MirrorConventions
    {
        /// <summary>The <c>ext s5 mirror &lt;n&gt;</c> version of this text (and the version nibble it implies).</summary>
        public const int ExtensionVersion = 1;
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (CONTRACT §5.4, §6 "Extensions pinned outside the layout hash").</summary>
        public const ulong PinnedHash = 0x31dfd6f9befdef5dUL;
        /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 37, CanonicalBytes = 1539;
        /// <summary>The version nibble's shift (bits 28-31).</summary>
        public const int VersionShift = 28;
        /// <summary>FNV-1a 32 parameters (skinId).</summary>
        public const uint Fnv32OffsetBasis = 0x811c9dc5u, Fnv32Prime = 0x01000193u;
        /// <summary><c>partKey.floating</c>: the one part of a floating object.</summary>
        public const ulong FloatingPartKey = 1;

        private struct ConstEntry
        {
            public readonly string Group, Name;
            public readonly ulong Value;
            public ConstEntry(string group, string name, ulong value) { Group = group; Name = name; Value = value; }
        }

        private static ConstEntry[] Entries()
        {
            return new[]
            {
                new ConstEntry("MirrorCharacterFlags", "Player", MirrorCharacterFlags.Player),
                new ConstEntry("MirrorCharacterFlags", "Fallback", MirrorCharacterFlags.Fallback),
                new ConstEntry("MirrorCharacterFlags", "Version1", MirrorCharacterFlags.Version1),
                new ConstEntry("MirrorCharacterPart", "Body", MirrorCharacterPart.Body),
                new ConstEntry("MirrorCharacterPart", "Head1st", MirrorCharacterPart.Head1st),
                new ConstEntry("MirrorCharacterPart", "Tool", MirrorCharacterPart.Tool),
                new ConstEntry("MirrorGridFlags", "Static", MirrorGridFlags.Static),
                new ConstEntry("MirrorGridFlags", "Subgrid", MirrorGridFlags.Subgrid),
                new ConstEntry("MirrorGridFlags", "FloatingObject", MirrorGridFlags.FloatingObject),
                new ConstEntry("MirrorGridFlags", "Version1", MirrorGridFlags.Version1),
                new ConstEntry("MirrorLimits", "MaxSkinWeights", (ulong)MirrorLimits.MaxSkinWeights),
                new ConstEntry("MirrorLimits", "CanonicalBones", (ulong)MirrorLimits.CanonicalBones),
                new ConstEntry("MirrorPartClass", "CubePart", MirrorPartClass.CubePart),
                new ConstEntry("MirrorPartClass", "Edge", MirrorPartClass.Edge),
                new ConstEntry("MirrorPartClass", "FatBlock", MirrorPartClass.FatBlock),
                new ConstEntry("MirrorPartClass", "Subpart", MirrorPartClass.Subpart),
                new ConstEntry("MirrorPartClass", "CharacterBody", MirrorPartClass.CharacterBody),
                new ConstEntry("MirrorPartClass", "Tool", MirrorPartClass.Tool),
                new ConstEntry("MirrorPartClass", "FloatingObject", MirrorPartClass.FloatingObject),
                new ConstEntry("MirrorPartFlags", "ClassMask", MirrorPartFlags.ClassMask),
                new ConstEntry("MirrorPartFlags", "Dynamic", MirrorPartFlags.Dynamic),
                new ConstEntry("MirrorPartFlags", "HiddenInFirstPerson", MirrorPartFlags.HiddenInFirstPerson),
                new ConstEntry("MirrorPartFlags", "BuildStage", MirrorPartFlags.BuildStage),
                new ConstEntry("MirrorPartFlags", "Deformed", MirrorPartFlags.Deformed),
                new ConstEntry("MirrorPartFlags", "NoShadows", MirrorPartFlags.NoShadows),
                new ConstEntry("MirrorPartFlags", "MetalnessColorable", MirrorPartFlags.MetalnessColorable),
                new ConstEntry("MirrorPartFlags", "Version1", MirrorPartFlags.Version1),
                new ConstEntry("MirrorPartFlags", "VersionMask", MirrorPartFlags.VersionMask),
            };
        }

        /// <summary>The <c>format</c> lines (the key definitions), in text order.</summary>
        public static readonly string[] FormatLines =
        {
            "modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]",
            "partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)",
            "partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])",
            "partKey.floating 1",
            "skinId fnv1a32(utf8 lower(<skin>)) or 0",
        };

        private static string _text;
        private static ulong _hash;

        /// <summary>
 /// The canonical text (CONTRACT §5.4; ASCII, '\n' after every line): <c>ext s5 mirror 1</c>; one
 /// <c>const &lt;group&gt; &lt;name&gt; &lt;value&gt;</c> line per constant sorted by group, then value, then name (ordinal);
 /// the <c>format</c> lines; <c>skeleton canon1 78 0x&lt;hash16&gt;</c> from the compiled skeleton table.
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
            var sb = new StringBuilder(1600);
            sb.Append("ext s5 mirror ").Append(ExtensionVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (ConstEntry c in e)
                sb.Append("const ").Append(c.Group).Append(' ').Append(c.Name).Append(' ')
                  .Append(c.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string f in FormatLines) sb.Append("format ").Append(f).Append('\n');
            sb.Append("skeleton ").Append(MirrorSkeleton.Name).Append(' ')
              .Append(MirrorSkeleton.BoneCount.ToString(CultureInfo.InvariantCulture)).Append(" 0x")
              .Append(MirrorSkeleton.Hash.ToString("x16", CultureInfo.InvariantCulture)).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (compiled; equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash
        {
            get
            {
                if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
                return _hash;
            }
        }

        // ------------------------------------------------------------------------------------------------ version nibble

 /// <summary>Bits 28-31 of a PartAdd, GridAdd or CharacterAdd <c>flags</c>.</summary>
        public static uint VersionOf(uint flags) { return flags >> VersionShift; }

        /// <summary>Legacy (nibble 0), Version1 (1) or Rejected (anything else: ignore and count).</summary>
        public static MirrorRecordVersion Classify(uint flags)
        {
            uint v = flags >> VersionShift;
            return v == 0 ? MirrorRecordVersion.Legacy : (v == 1 ? MirrorRecordVersion.Version1 : MirrorRecordVersion.Rejected);
        }

        /// <summary>
 /// The class a consumer acts on: CubePart for a legacy record (nibble 0) and for a version-1 class 0, else
 /// <c>flags &amp; ClassMask</c> (may be an unknown class 8-15: see <see cref="IsKnownPartClass"/>). 0 for a rejected record.
 /// </summary>
        public static uint EffectivePartClass(uint flags)
        {
            switch (Classify(flags))
            {
                case MirrorRecordVersion.Legacy: return MirrorPartClass.CubePart;
                case MirrorRecordVersion.Version1:
                    uint c = flags & MirrorPartFlags.ClassMask;
                    return c == 0 ? MirrorPartClass.CubePart : c;
                default: return 0;
            }
        }

        /// <summary>Classes 1-7.</summary>
        public static bool IsKnownPartClass(uint partClass) { return partClass >= MirrorPartClass.CubePart && partClass <= MirrorPartClass.FloatingObject; }

        /// <summary>True when a version-1 part has the bit (false for legacy and rejected records).</summary>
        public static bool HasPartFlag(uint flags, uint bit) { return Classify(flags) == MirrorRecordVersion.Version1 && (flags & bit) != 0; }

        /// <summary>
 /// A version-1 PartAdd <c>flags</c>: Version1 | class | extra (extra must not touch the class field or the nibble).
 /// </summary>
        public static uint PartFlags(uint partClass, uint extra = 0)
        {
            if (!IsKnownPartClass(partClass)) throw new ArgumentOutOfRangeException("partClass", "part class 1..7");
            if ((extra & (MirrorPartFlags.ClassMask | MirrorPartFlags.VersionMask)) != 0)
                throw new ArgumentException("extra flags overlap the class field or the version nibble", "extra");
            return MirrorPartFlags.Version1 | partClass | extra;
        }

        /// <summary>A version-1 GridAdd <c>flags</c>.</summary>
        public static uint GridFlags(bool isStatic, bool subgrid, bool floatingObject = false)
        {
            return MirrorGridFlags.Version1 | (isStatic ? MirrorGridFlags.Static : 0u) | (subgrid ? MirrorGridFlags.Subgrid : 0u)
                | (floatingObject ? MirrorGridFlags.FloatingObject : 0u);
        }

        /// <summary>A version-1 CharacterAdd <c>flags</c> (always Player in v1).</summary>
        public static uint CharacterFlags(bool fallback)
        {
            return MirrorCharacterFlags.Version1 | MirrorCharacterFlags.Player | (fallback ? MirrorCharacterFlags.Fallback : 0u);
        }

        // ------------------------------------------------------------------------------------------------ keys

 /// <summary>
 /// <c>format modelKey</c>: <see cref="ModelCodec.ModelKeyText"/> (normalised path, <c>|lod&lt;n&gt;</c>, <c>|scale=</c>
 /// when ≠ 1), then <c>|skin=&lt;lower-case skin&gt;</c> when <paramref name="skin"/> is not empty (only when the skin
 /// overrides at least one material of that model: the caller decides), then <c>|char=body</c> or
 /// <c>|char=head1st</c> for <see cref="MirrorCharacterPart.Body"/> / <see cref="MirrorCharacterPart.Head1st"/>
 /// (0 = not a character model).
 /// </summary>
        public static string ModelKeyText(string path, uint lod, float scale = 1f, string skin = null, ulong characterPart = 0)
        {
            string text = ModelCodec.ModelKeyText(path, lod, scale);
            if (!string.IsNullOrEmpty(skin)) text += "|skin=" + skin.ToLowerInvariant();
            if (characterPart == MirrorCharacterPart.Body) text += "|char=body";
            else if (characterPart == MirrorCharacterPart.Head1st) text += "|char=head1st";
            else if (characterPart != 0) throw new ArgumentOutOfRangeException("characterPart", "0, Body or Head1st");
            return text;
        }

        /// <summary>FNV-1a 64 of <see cref="ModelKeyText"/> (UTF-8). Equals <see cref="ModelCodec.ModelKey"/> without skin and character part.</summary>
        public static ulong ModelKey(string path, uint lod, float scale = 1f, string skin = null, ulong characterPart = 0)
        {
            return LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(ModelKeyText(path, lod, scale, skin, characterPart)));
        }

        /// <summary>
 /// <c>partKey.cube</c> = FNV-1a 64 over (u64 modelKey, f32 tx, ty, tz, packedRotation), little-endian: S3's
 /// <c>PartDiff.PartKey</c> over the instance's packed ortho matrix (translation and the packed rotation in w).
 /// </summary>
        public static ulong CubePartKey(ulong modelKey, float tx, float ty, float tz, float packedRotation)
        {
            ulong h = LayoutHash.FnvOffsetBasis;
            h = AddU64(h, modelKey);
            h = AddF32(h, tx); h = AddF32(h, ty); h = AddF32(h, tz); h = AddF32(h, packedRotation);
            return h;
        }

        /// <summary><c>partKey.edge</c> = FNV-1a 64 over (u32 2, u64 modelKey, f32 tx, ty, tz, packedRotation) of <c>EdgeInfo.LocalOrthoMatrix</c>.</summary>
        public static ulong EdgePartKey(ulong modelKey, float tx, float ty, float tz, float packedRotation)
        {
            ulong h = AddU32(LayoutHash.FnvOffsetBasis, MirrorPartClass.Edge);
            h = AddU64(h, modelKey);
            h = AddF32(h, tx); h = AddF32(h, ty); h = AddF32(h, tz); h = AddF32(h, packedRotation);
            return h;
        }

        /// <summary><c>fat|&lt;minX&gt;,&lt;minY&gt;,&lt;minZ&gt;</c> (invariant decimal) from <c>MySlimBlock.Min</c>.</summary>
        public static string FatPartKeyText(int minX, int minY, int minZ)
        {
            return "fat|" + minX.ToString(CultureInfo.InvariantCulture) + "," + minY.ToString(CultureInfo.InvariantCulture) + ","
                + minZ.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary><c>partKey.fat</c> = FNV-1a 64 of <see cref="FatPartKeyText"/> (UTF-8).</summary>
        public static ulong FatPartKey(int minX, int minY, int minZ)
        {
            return LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(FatPartKeyText(minX, minY, minZ)));
        }

        /// <summary>
 /// <c>sub|&lt;fatKey as 16 lower-case hex digits&gt;|&lt;name&gt;[/&lt;name&gt;…]</c>: the path of
 /// <c>MyEntity.Subparts</c> dictionary keys from the block (e.g. <c>TurretBase/TurretBarrel</c>).
 /// </summary>
        public static string SubpartKeyText(ulong fatKey, string namePath)
        {
            if (string.IsNullOrEmpty(namePath)) throw new ArgumentException("subpart path is empty", "namePath");
            return "sub|" + fatKey.ToString("x16", CultureInfo.InvariantCulture) + "|" + namePath;
        }

        /// <summary><c>partKey.subpart</c> = FNV-1a 64 of <see cref="SubpartKeyText"/> (UTF-8).</summary>
        public static ulong SubpartKey(ulong fatKey, string namePath)
        {
            return LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(SubpartKeyText(fatKey, namePath)));
        }

        /// <summary><c>partKey.subpart</c> of a nested path given as its names, outermost first.</summary>
        public static ulong SubpartKey(ulong fatKey, IList<string> names)
        {
            if (names == null || names.Count == 0) throw new ArgumentException("subpart path is empty", "names");
            return SubpartKey(fatKey, string.Join("/", ToArray(names)));
        }

        private static string[] ToArray(IList<string> names)
        {
            var a = new string[names.Count];
            for (int i = 0; i < a.Length; i++) a[i] = names[i];
            return a;
        }

        /// <summary>
 /// <c>skinId</c> = FNV-1a 32 (offset 0x811c9dc5, prime 0x01000193) of the UTF-8 lower-case (invariant) skin subtype
 /// (<c>MyStringHash.String</c>); 0 for none (null or empty). Information only: the skin is resolved into the model.
 /// </summary>
        public static uint SkinId(string skinSubtype)
        {
            if (string.IsNullOrEmpty(skinSubtype)) return 0;
            return Fnv1a32(Encoding.UTF8.GetBytes(skinSubtype.ToLowerInvariant()));
        }

        /// <summary>FNV-1a 32 of a byte array.</summary>
        public static uint Fnv1a32(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            uint h = Fnv32OffsetBasis;
            for (int i = 0; i < data.Length; i++) h = (h ^ data[i]) * Fnv32Prime;
            return h;
        }

        /// <summary>C1 <c>bonesVersion</c>: the low 32 bits of the current CharacterAdd <c>modelKey</c> (0 = no bones).</summary>
        public static uint BonesVersion(ulong characterModelKey) { return unchecked((uint)characterModelKey); }

        private static ulong AddU64(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++) { h = (h ^ (byte)v) * LayoutHash.FnvPrime; v >>= 8; }
            return h;
        }

        private static ulong AddU32(ulong h, uint v)
        {
            for (int i = 0; i < 4; i++) { h = (h ^ (byte)v) * LayoutHash.FnvPrime; v >>= 8; }
            return h;
        }

        private static unsafe ulong AddF32(ulong h, float f)
        {
            return AddU32(h, *(uint*)&f);
        }

        // ------------------------------------------------------------------------------------------------ records

 /// <summary>
 /// C4 CharacterAdd: entityId = C1 <c>characterId</c>, <c>modelKey</c> = the body model's key, flags Version1 | Player
 /// (| Fallback), <c>boneCount</c> 78 (canonical).
 /// </summary>
        public static CharacterAdd MakeCharacterAdd(ulong characterId, ulong bodyModelKey, bool fallback)
        {
            return new CharacterAdd
            {
                entityId = characterId,
                modelKey = bodyModelKey,
                flags = CharacterFlags(fallback),
                boneCount = (uint)MirrorLimits.CanonicalBones,
            };
        }

        /// <summary>
 /// The PartAdd of a character part (class CharacterBody, key Body or Head1st): local identity, HSV =
 /// <c>MyCharacter.ColorMask</c>, MetalnessColorable, and HiddenInFirstPerson for Head1st.
 /// </summary>
        public static PartAdd MakeCharacterPart(ulong characterId, ulong characterPart, ulong modelKey, Vec3 colorMaskHsv, uint skinId)
        {
            uint extra = MirrorPartFlags.MetalnessColorable;
            if (characterPart == MirrorCharacterPart.Head1st) extra |= MirrorPartFlags.HiddenInFirstPerson;
            else if (characterPart != MirrorCharacterPart.Body) throw new ArgumentOutOfRangeException("characterPart", "Body or Head1st");
            return ModelCodec.MakePartAdd(characterId, characterPart, modelKey, InstanceMath.Identity, colorMaskHsv, 0f, 0f,
                skinId, PartFlags(MirrorPartClass.CharacterBody, extra));
        }

        /// <summary>The optional held-tool PartAdd (class Tool, key 3, Dynamic; posed by its C2 row in the character's model space).</summary>
        public static PartAdd MakeToolPart(ulong characterId, ulong modelKey, Vec3 colorMaskHsv, uint skinId = 0)
        {
            return ModelCodec.MakePartAdd(characterId, MirrorCharacterPart.Tool, modelKey, InstanceMath.Identity, colorMaskHsv, 0f, 0f,
                skinId, PartFlags(MirrorPartClass.Tool, MirrorPartFlags.Dynamic));
        }

        /// <summary>A floating object's PartAdd (class FloatingObject, key 1, local identity; its uniform scale is C2 <c>k</c>).</summary>
        public static PartAdd MakeFloatingObjectPart(ulong entityId, ulong modelKey, Vec3 colorMaskHsv)
        {
            return ModelCodec.MakePartAdd(entityId, FloatingPartKey, modelKey, InstanceMath.Identity, colorMaskHsv, 0f, 0f, 0u,
                PartFlags(MirrorPartClass.FloatingObject));
        }

        /// <summary>
 /// One-line description of a part's flags for logs, e.g. <c>v1 subpart dynamic noShadows</c>, <c>legacy</c>,
 /// <c>rejected(v3)</c>.
 /// </summary>
        public static string DescribePartFlags(uint flags)
        {
            switch (Classify(flags))
            {
                case MirrorRecordVersion.Legacy: return "legacy";
                case MirrorRecordVersion.Rejected: return "rejected(v" + VersionOf(flags).ToString(CultureInfo.InvariantCulture) + ")";
            }
            var sb = new StringBuilder("v1 ");
            sb.Append(PartClassName(EffectivePartClass(flags)));
            if ((flags & MirrorPartFlags.Dynamic) != 0) sb.Append(" dynamic");
            if ((flags & MirrorPartFlags.HiddenInFirstPerson) != 0) sb.Append(" hidden1st");
            if ((flags & MirrorPartFlags.BuildStage) != 0) sb.Append(" stage");
            if ((flags & MirrorPartFlags.Deformed) != 0) sb.Append(" deformed");
            if ((flags & MirrorPartFlags.NoShadows) != 0) sb.Append(" noShadows");
            if ((flags & MirrorPartFlags.MetalnessColorable) != 0) sb.Append(" metalCol");
            uint unknown = flags & ~(MirrorPartFlags.VersionMask | MirrorPartFlags.ClassMask | MirrorPartFlags.Dynamic
                | MirrorPartFlags.HiddenInFirstPerson | MirrorPartFlags.BuildStage | MirrorPartFlags.Deformed
                | MirrorPartFlags.NoShadows | MirrorPartFlags.MetalnessColorable);
            if (unknown != 0) sb.Append(" unknown=0x").Append(unknown.ToString("x", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>cube, edge, fat, subpart, body, tool, floating, or class&lt;n&gt;.</summary>
        public static string PartClassName(uint partClass)
        {
            switch (partClass)
            {
                case MirrorPartClass.CubePart: return "cube";
                case MirrorPartClass.Edge: return "edge";
                case MirrorPartClass.FatBlock: return "fat";
                case MirrorPartClass.Subpart: return "subpart";
                case MirrorPartClass.CharacterBody: return "body";
                case MirrorPartClass.Tool: return "tool";
                case MirrorPartClass.FloatingObject: return "floating";
                default: return "class" + partClass.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
