using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The C4 type 6 <c>SkinSet</c> payload (CONTRACT §5.4 "C4 type 6 SkinSet (S6)"; layout 1.0 records
 /// <see cref="SkinSetHeader"/> 16 B, <see cref="SkinSection"/> 24 B, <see cref="SkinBox"/> 64 B; little-endian, without
 /// the 8-byte C4 message header): header {u64 entityId, u32 skinVersion, u32 sectionCount}, then per section
 /// {u32 sectionId, f32 centre[3], u32 boxCount, u32 kind} and boxCount × {f32 centre[3], f32 half[3], f32 axes[9]
 /// (rows e0, e1, e2), u32 flags}. Python twin: <c>wire_ref.s6_skin_payload</c> / <c>s6_skin_decode</c>.
 /// </summary>
    public static unsafe class SkinCodec
    {
        public const int HeaderBytes = 16, SectionBytesFixed = 24, BoxBytes = 64;
        /// <summary>The largest accepted half extent: 4.0 m + 1e-4 m of rounding.</summary>
        public const double MaxHalfAccepted = CollisionSkinMath.MaxHalfExtent + 1e-4;
        /// <summary>Axes tolerance: |eᵢ·eⱼ − δᵢⱼ| ≤ 1e-5.</summary>
        public const double AxisTolerance = 1e-5;

        /// <summary>16 + Σ (24 + 64·boxCount); int.MaxValue when that does not fit.</summary>
        public static int PayloadBytes(IList<SkinSectionData> sections)
        {
            long n = HeaderBytes;
            if (sections != null)
                foreach (SkinSectionData s in sections) n += SectionBytesFixed + (long)BoxBytes * (s == null ? 0 : s.Boxes.Count);
            return n > int.MaxValue ? int.MaxValue : (int)n;
        }

        /// <summary>
 /// The payload of one SkinSet message. Throws <see cref="ArgumentException"/> for a null or empty section or
 /// section ids that do not ascend strictly; box values are written as given (check them with <see cref="Validate"/>).
 /// </summary>
        public static byte[] Encode(ulong entityId, uint skinVersion, IList<SkinSectionData> sections)
        {
            if (sections == null) sections = new SkinSectionData[0];
            CheckStructure(sections);
            int n = PayloadBytes(sections);
            if (n == int.MaxValue) throw new ArgumentException("skin too large to encode");
            var buf = new byte[n];
            fixed (byte* p = buf)
            {
                *(ulong*)p = entityId;
                *(uint*)(p + 8) = skinVersion;
                *(uint*)(p + 12) = (uint)sections.Count;
                int off = HeaderBytes;
                foreach (SkinSectionData s in sections) off = WriteSection(p, off, s);
            }
            return buf;
        }

        /// <summary>The 24 + 64·n encoded bytes of one section (what <see cref="CollisionSkinMath.SectionContentHash"/> hashes).</summary>
        public static byte[] SectionBytes(SkinSectionData s)
        {
            if (s == null) throw new ArgumentNullException("s");
            var buf = new byte[SectionBytesFixed + BoxBytes * s.Boxes.Count];
            fixed (byte* p = buf) WriteSection(p, 0, s);
            return buf;
        }

        private static void CheckStructure(IList<SkinSectionData> sections)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                SkinSectionData s = sections[i];
                if (s == null) throw new ArgumentException("section " + i + " is null");
                if (s.Boxes.Count < 1) throw new ArgumentException("section 0x" + s.SectionId.ToString("x8", CultureInfo.InvariantCulture) + " has no box");
                if (i > 0 && s.SectionId <= sections[i - 1].SectionId) throw new ArgumentException("section ids must ascend strictly");
            }
        }

        private static int WriteSection(byte* p, int off, SkinSectionData s)
        {
            *(uint*)(p + off) = s.SectionId;
            PutVec(p + off + 4, s.Centre);
            *(uint*)(p + off + 16) = (uint)s.Boxes.Count;
            *(uint*)(p + off + 20) = s.Kind;
            off += SectionBytesFixed;
            foreach (SkinBoxData b in s.Boxes)
            {
                PutVec(p + off, b.Centre);
                PutVec(p + off + 12, b.Half);
                PutVec(p + off + 24, b.E0);
                PutVec(p + off + 36, b.E1);
                PutVec(p + off + 48, b.E2);
                *(uint*)(p + off + 60) = b.Flags;
                off += BoxBytes;
            }
            return off;
        }

        private static void PutVec(byte* p, Vec3 v)
        {
            *(float*)p = (float)v.X;
            *(float*)(p + 4) = (float)v.Y;
            *(float*)(p + 8) = (float)v.Z;
        }

        private static Vec3 GetVec(byte* p)
        {
            return new Vec3(*(float*)p, *(float*)(p + 4), *(float*)(p + 8));
        }

        /// <summary>
 /// The checks a consumer applies to every box (after float32 rounding): finite centre; 0 &lt; half ≤ 4.0001 m;
 /// rows finite, orthonormal (|eᵢ·eⱼ − δᵢⱼ| ≤ 1e-5) and right-handed; flags only <c>Rotated</c> and the source
 /// nibble; <c>Rotated</c> clear only with exactly the identity rows.
 /// </summary>
        public static bool IsValidBox(SkinBoxData b, out string error)
        {
            if (!b.Centre.IsFinite) { error = "box centre not finite"; return false; }
            if (!(b.Half.X > 0 && b.Half.Y > 0 && b.Half.Z > 0 && b.Half.X <= MaxHalfAccepted && b.Half.Y <= MaxHalfAccepted && b.Half.Z <= MaxHalfAccepted))
            {
                error = "half extent out of (0, 4.0001]: " + b.Half;
                return false;
            }
            if (!b.E0.IsFinite || !b.E1.IsFinite || !b.E2.IsFinite) { error = "axes not finite"; return false; }
            Vec3[] e = { b.E0, b.E1, b.E2 };
            for (int i = 0; i < 3; i++)
                for (int j = i; j < 3; j++)
                    if (Math.Abs(Vec3.Dot(e[i], e[j]) - (i == j ? 1.0 : 0.0)) > AxisTolerance) { error = "axes not orthonormal"; return false; }
            if (Vec3.Dot(b.E0, Vec3.Cross(b.E1, b.E2)) <= 0) { error = "axes not right-handed"; return false; }
            const uint known = CombatConventions.SkinBoxFlags.Rotated | CombatConventions.SkinBoxFlags.SourceMask;
            if ((b.Flags & ~known) != 0) { error = "unknown box flag bits 0x" + (b.Flags & ~known).ToString("x", CultureInfo.InvariantCulture); return false; }
            if (!b.IsRotated && !b.HasIdentityAxes) { error = "axes not the identity on a box without the Rotated flag"; return false; }
            error = null;
            return true;
        }

        /// <summary>
 /// Everything <see cref="TryDecode(byte*, int, out SkinSetHeader, List{SkinSectionData}, out string)"/> would
 /// reject, checked on the values as they will travel (float32): structure, section kind 1 or 2, finite centres,
 /// boxes (<see cref="IsValidBox"/>) and the 2 MiB limit.
 /// </summary>
        public static bool Validate(IList<SkinSectionData> sections, out string error)
        {
            if (sections == null) { error = null; return true; }
            try { CheckStructure(sections); }
            catch (ArgumentException e) { error = e.Message; return false; }
            if (PayloadBytes(sections) > CombatConventions.Limits.SkinMaxBytes) { error = "skin larger than " + CombatConventions.Limits.SkinMaxBytes + " bytes"; return false; }
            foreach (SkinSectionData s in sections)
            {
                if (s.Kind != SkinSectionKind.Grid && s.Kind != SkinSectionKind.Character) { error = "section kind " + s.Kind; return false; }
                if (!s.Centre.RoundToFloat32().IsFinite) { error = "section centre not finite"; return false; }
                foreach (SkinBoxData b in s.Boxes)
                {
                    var f = new SkinBoxData
                    {
                        Centre = b.Centre.RoundToFloat32(), Half = b.Half.RoundToFloat32(), E0 = b.E0.RoundToFloat32(),
                        E1 = b.E1.RoundToFloat32(), E2 = b.E2.RoundToFloat32(), Flags = b.Flags,
                    };
                    if (!IsValidBox(f, out error)) { error = "section 0x" + s.SectionId.ToString("x8", CultureInfo.InvariantCulture) + ": " + error; return false; }
                }
            }
            error = null;
            return true;
        }

        /// <summary>
 /// Decodes a SkinSet payload (without the 8-byte message header) into <paramref name="sections"/> (cleared
 /// first, and again on failure). Checks: the exact length the counts imply and at most 2 MiB; skinVersion ≥ 1;
 /// section ids ascending and unique; kind 1 (a kind-2 section is valid but skipped: CONTRACT §5.4 says a
 /// consumer ignores it and counts it, see the overload's count); boxCount ≥ 1; finite centres; every box
 /// <see cref="IsValidBox"/>. Each section's <see cref="SkinSectionData.ContentHash"/> is the FNV-1a 64 of its raw
 /// bytes. False (skinBad) with a reason on the first failure.
 /// </summary>
        public static bool TryDecode(byte* p, int bytes, out SkinSetHeader header, List<SkinSectionData> sections, out string error)
        {
            int ignored;
            return TryDecode(p, bytes, out header, sections, out ignored, out error);
        }

        /// <summary>
 /// <see cref="TryDecode(byte*, int, out SkinSetHeader, List{SkinSectionData}, out string)"/>, also counting the
 /// kind-2 (character) sections it skipped.
 /// </summary>
        public static bool TryDecode(byte* p, int bytes, out SkinSetHeader header, List<SkinSectionData> sections, out int ignoredSections, out string error)
        {
            if (sections == null) throw new ArgumentNullException("sections");
            sections.Clear();
            header = default(SkinSetHeader);
            ignoredSections = 0;
            if (p == null || bytes < HeaderBytes) { error = "payload shorter than the 16-byte header"; return false; }
            if (bytes > CombatConventions.Limits.SkinMaxBytes) { error = "payload larger than " + CombatConventions.Limits.SkinMaxBytes + " bytes"; return false; }
            header.entityId = *(ulong*)p;
            header.skinVersion = *(uint*)(p + 8);
            header.sectionCount = *(uint*)(p + 12);
            if (header.skinVersion == 0) { error = "skinVersion 0"; return false; }
            if (header.sectionCount > (uint)((bytes - HeaderBytes) / (SectionBytesFixed + BoxBytes)))
            {
                error = "sectionCount " + header.sectionCount + " does not fit " + bytes + " bytes";
                return false;
            }
            int off = HeaderBytes;
            bool havePrev = false;
            uint prevId = 0;
            for (uint i = 0; i < header.sectionCount; i++)
            {
                if (bytes - off < SectionBytesFixed) { error = "truncated at section " + i; sections.Clear(); return false; }
                byte* sp = p + off;
                uint id = *(uint*)sp;
                Vec3 centre = GetVec(sp + 4);
                uint boxCount = *(uint*)(sp + 16);
                uint kind = *(uint*)(sp + 20);
                if (havePrev && id <= prevId) { error = "section ids not ascending at 0x" + id.ToString("x8", CultureInfo.InvariantCulture); sections.Clear(); return false; }
                havePrev = true;
                prevId = id;
                if (kind != SkinSectionKind.Grid && kind != SkinSectionKind.Character) { error = "section kind " + kind; sections.Clear(); return false; }
                if (boxCount < 1) { error = "section 0x" + id.ToString("x8", CultureInfo.InvariantCulture) + " has no box"; sections.Clear(); return false; }
                if (!centre.IsFinite) { error = "section centre not finite"; sections.Clear(); return false; }
                long need = SectionBytesFixed + (long)BoxBytes * boxCount;
                if (bytes - off < need) { error = "truncated in section 0x" + id.ToString("x8", CultureInfo.InvariantCulture); sections.Clear(); return false; }
                var s = new SkinSectionData { SectionId = id, Centre = centre, Kind = kind };
                byte* bp = sp + SectionBytesFixed;
                for (uint k = 0; k < boxCount; k++, bp += BoxBytes)
                {
                    var b = new SkinBoxData
                    {
                        Centre = GetVec(bp), Half = GetVec(bp + 12), E0 = GetVec(bp + 24), E1 = GetVec(bp + 36), E2 = GetVec(bp + 48),
                        Flags = *(uint*)(bp + 60),
                    };
                    string why;
                    if (!IsValidBox(b, out why))
                    {
                        error = "section 0x" + id.ToString("x8", CultureInfo.InvariantCulture) + " box " + k + ": " + why;
                        sections.Clear();
                        return false;
                    }
                    s.Boxes.Add(b);
                }
                s.ContentHash = LayoutHash.Fnv1a64(sp, need);
                off += (int)need;
                if (kind == SkinSectionKind.Character) ignoredSections++;
                else sections.Add(s);
            }
            if (off != bytes) { error = "length " + bytes + " differs from the " + off + " bytes the counts imply"; sections.Clear(); return false; }
            error = null;
            return true;
        }

        /// <summary>Managed overload of <see cref="TryDecode(byte*, int, out SkinSetHeader, List{SkinSectionData}, out string)"/>.</summary>
        public static bool TryDecode(byte[] payload, int offset, int count, out SkinSetHeader header, List<SkinSectionData> sections, out string error)
        {
            int ignored;
            return TryDecode(payload, offset, count, out header, sections, out ignored, out error);
        }

        /// <summary>Managed overload counting the skipped kind-2 sections.</summary>
        public static bool TryDecode(byte[] payload, int offset, int count, out SkinSetHeader header, List<SkinSectionData> sections, out int ignoredSections, out string error)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset > payload.Length || payload.Length - offset < count) throw new ArgumentOutOfRangeException("count");
            if (count == 0)
            {
                if (sections == null) throw new ArgumentNullException("sections");
                sections.Clear();
                header = default(SkinSetHeader);
                ignoredSections = 0;
                error = "payload shorter than the 16-byte header";
                return false;
            }
            fixed (byte* p = &payload[offset]) return TryDecode(p, count, out header, sections, out ignoredSections, out error);
        }
    }
}
