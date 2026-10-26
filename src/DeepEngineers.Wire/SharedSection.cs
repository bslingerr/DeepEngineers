using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// A named, page-file-backed section mapped with P/Invoke (CONTRACT §5, §6). The creator (Subnautica) uses Create
 /// with the CONTRACT security descriptor; SE and every tool use OpenExisting (never create). Never blocks.
 /// The view is unmapped by Dispose; ring and seqlock objects built on Base must not outlive it.
 /// </summary>
    public sealed unsafe class SharedSection : IDisposable
    {
        private IntPtr _handle;
        private IntPtr _view;

        public string Name { get; }
        /// <summary>Size the caller asked for.</summary>
        public long RequestedSize { get; }
        /// <summary>Size actually mapped (the whole section; VirtualQuery region size).</summary>
        public long MappedSize { get; }
        /// <summary>True when Create found an existing section (ERROR_ALREADY_EXISTS).</summary>
        public bool Existed { get; }
        public bool ReadOnly { get; }
        public byte* Base { get { return (byte*)_view; } }
        public bool IsOpen { get { return _view != IntPtr.Zero; } }
        /// <summary>The section handle (tests read its security descriptor).</summary>
        public IntPtr Handle { get { return _handle; } }

        private SharedSection(string name, long requested, long mapped, IntPtr handle, IntPtr view, bool existed, bool readOnly)
        {
            Name = name; RequestedSize = requested; MappedSize = mapped; _handle = handle; _view = view;
            Existed = existed; ReadOnly = readOnly;
        }

        /// <summary>`Local\` is prepended to a bare name; a name with a namespace prefix is kept as is.</summary>
        public static string FullName(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name");
            return name.IndexOf('\\') >= 0 ? name : @"Local\" + name;
        }

        /// <summary>
 /// CreateFileMappingW with a security descriptor built from <paramref name="sddl"/> (never default security).
 /// A new section is mapped at <paramref name="size"/> and is zero-filled by the OS. If it already existed
 /// (<paramref name="existed"/>), the whole existing section is mapped (whatever its size) so the creator rules
 /// can read its header and refuse without writing.
 /// </summary>
        public static SharedSection Create(string name, long size, string sddl, out bool existed)
        {
            string full = FullName(name);
            if (size <= 0 || size > uint.MaxValue) throw new ArgumentOutOfRangeException("size");
            if (string.IsNullOrEmpty(sddl)) throw new ArgumentException("a security descriptor is required (CONTRACT §6)", "sddl");

            IntPtr sd = Win32.SecurityDescriptorFromSddl(sddl);
            IntPtr h;
            int err;
            try
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = 0,
                };
                h = Kernel32.CreateFileMappingW(Kernel32.INVALID_HANDLE_VALUE, ref sa, Kernel32.PAGE_READWRITE,
                    (uint)((ulong)size >> 32), (uint)((ulong)size & 0xFFFFFFFF), full);
                err = Marshal.GetLastWin32Error();
            }
            finally { Win32.FreeSecurityDescriptor(sd); }
            if (h == IntPtr.Zero) throw new Win32Exception(err, "CreateFileMappingW(" + full + ")");
            existed = err == Kernel32.ERROR_ALREADY_EXISTS;

            IntPtr v = Kernel32.MapViewOfFile(h, Kernel32.FILE_MAP_READ | Kernel32.FILE_MAP_WRITE, 0, 0,
                existed ? UIntPtr.Zero : (UIntPtr)(ulong)size);
            if (v == IntPtr.Zero)
            {
                err = Marshal.GetLastWin32Error();
                Kernel32.CloseHandle(h);
                throw new Win32Exception(err, "MapViewOfFile(" + full + ")");
            }
            long mapped = QueryViewSize(v, existed ? 0 : size);
            return new SharedSection(full, size, mapped, h, v, existed, false);
        }

        /// <summary>
 /// OpenFileMappingW (open only, never create). Returns null when the section does not exist
 /// (ERROR_FILE_NOT_FOUND). The whole section is mapped; compare MappedSize with <paramref name="size"/>
 /// (SectionValidation does). Read-only opens use FILE_MAP_READ (the spy).
 /// </summary>
        public static SharedSection OpenExisting(string name, long size, bool readOnly = false)
        {
            string full = FullName(name);
            uint access = readOnly ? Kernel32.FILE_MAP_READ : (Kernel32.FILE_MAP_READ | Kernel32.FILE_MAP_WRITE);
            IntPtr h = Kernel32.OpenFileMappingW(access, false, full);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Kernel32.ERROR_FILE_NOT_FOUND) return null;
                throw new Win32Exception(err, "OpenFileMappingW(" + full + ")");
            }
            IntPtr v = Kernel32.MapViewOfFile(h, access, 0, 0, UIntPtr.Zero);
            if (v == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Kernel32.CloseHandle(h);
                throw new Win32Exception(err, "MapViewOfFile(" + full + ")");
            }
            long mapped = QueryViewSize(v, 0);
            return new SharedSection(full, size, mapped, h, v, true, readOnly);
        }

        private static long QueryViewSize(IntPtr view, long fallback)
        {
            MEMORY_BASIC_INFORMATION mbi;
            UIntPtr n = Kernel32.VirtualQuery(view, out mbi, (UIntPtr)(ulong)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));
            if (n == UIntPtr.Zero) return fallback;
            return (long)(ulong)mbi.RegionSize;
        }

        public HeaderView Header
        {
            get
            {
                if (!IsOpen) throw new ObjectDisposedException(Name);
                return new HeaderView(Base);
            }
        }

        public void Dispose()
        {
            IntPtr v = _view, h = _handle;
            _view = IntPtr.Zero; _handle = IntPtr.Zero;
            if (v != IntPtr.Zero) Kernel32.UnmapViewOfFile(v);
            if (h != IntPtr.Zero) Kernel32.CloseHandle(h);
        }
    }

    /// <summary>
 /// Volatile accessors for the C0 header words. Each side writes only its own block (SN 0x040-0x07F, SE
 /// 0x080-0x0FF); the creator writes the common words; `devControl` belongs to whoever CONTRACT §5.1 says.
 /// </summary>
    public readonly unsafe struct HeaderView
    {
        private readonly byte* _b;

        public HeaderView(byte* sectionBase)
        {
            if (sectionBase == null) throw new ArgumentNullException("sectionBase");
            _b = sectionBase;
        }

        private uint U32(int off) { return Volatile.Read(ref *(uint*)(_b + off)); }
        private void U32(int off, uint v) { Volatile.Write(ref *(uint*)(_b + off), v); }
        private ulong U64(int off) { return Volatile.Read(ref *(ulong*)(_b + off)); }
        private void U64(int off, ulong v) { Volatile.Write(ref *(ulong*)(_b + off), v); }
        private ushort U16(int off) { return Volatile.Read(ref *(ushort*)(_b + off)); }
        private void U16(int off, ushort v) { Volatile.Write(ref *(ushort*)(_b + off), v); }

        /// <summary>A plain copy of the 256-byte header record (fields may come from different moments).</summary>
        public SectionHeader Snapshot()
        {
            Thread.MemoryBarrier();
            return *(SectionHeader*)_b;
        }

        public uint Magic { get { return U32(0x000); } }
        /// <summary>Creator only: magic is written last with release semantics.</summary>
        public void PublishMagic() { Thread.MemoryBarrier(); U32(0x000, WireInfo.Magic); }
        /// <summary>Creator only: magic cleared first when an existing section is re-initialised.</summary>
        public void ClearMagic() { U32(0x000, 0); Thread.MemoryBarrier(); }
        internal void SetMagicRaw(uint value) { U32(0x000, value); }

        public ushort LayoutMajor { get { return U16(0x004); } set { U16(0x004, value); } }
        public ushort LayoutMinor { get { return U16(0x006); } set { U16(0x006, value); } }
        public uint ByteOrderMark { get { return U32(0x008); } set { U32(0x008, value); } }
        public uint HeaderSize { get { return U32(0x00C); } set { U32(0x00C, value); } }
        public ulong SectionSize { get { return U64(0x010); } set { U64(0x010, value); } }
        public ulong LayoutHash { get { return U64(0x018); } set { U64(0x018, value); } }
        public uint WireVersion { get { return U32(0x020); } set { U32(0x020, value); } }
        public uint SiteVersion { get { return U32(0x024); } set { U32(0x024, value); } }
        public uint DamageScaleVersion { get { return U32(0x028); } set { U32(0x028, value); } }
        public uint RegionCount { get { return U32(0x02C); } set { U32(0x02C, value); } }
        public ulong QpcFrequency { get { return U64(0x030); } set { U64(0x030, value); } }
        public uint DevControl { get { return U32(0x038); } set { U32(0x038, value); } }

        public uint SnPid { get { return U32(0x040); } set { U32(0x040, value); } }
        public uint SnGeneration { get { return U32(0x044); } set { U32(0x044, value); } }
        public ulong SnHeartbeatMs { get { return U64(0x048); } set { U64(0x048, value); } }
        public uint SnPhase { get { return U32(0x050); } set { U32(0x050, value); } }
        public uint SnFlags { get { return U32(0x054); } set { U32(0x054, value); } }
        public ulong SnHwnd { get { return U64(0x058); } set { U64(0x058, value); } }

        public uint SePid { get { return U32(0x080); } set { U32(0x080, value); } }
        public uint SeGeneration { get { return U32(0x084); } set { U32(0x084, value); } }
        public ulong SeHeartbeatMs { get { return U64(0x088); } set { U64(0x088, value); } }
        public uint SePhase { get { return U32(0x090); } set { U32(0x090, value); } }
        public uint SeFlags { get { return U32(0x094); } set { U32(0x094, value); } }
        public ulong SeHwnd { get { return U64(0x098); } set { U64(0x098, value); } }
        public uint SeLoadStep { get { return U32(0x0A0); } set { U32(0x0A0, value); } }

        /// <summary>Region-table entry i (0x100 + i × 32).</summary>
        public RegionEntry Region(int index)
        {
            if (index < 0 || Layout.RegionTableOffset + (index + 1) * Layout.RegionEntrySize > Layout.HeaderSize)
                throw new ArgumentOutOfRangeException("index");
            return *(RegionEntry*)(_b + Layout.RegionTableOffset + index * Layout.RegionEntrySize);
        }

        // `snPid`/`snGeneration` (0x040/0x044) and `sePid`/`seGeneration` (0x080/0x084) are adjacent u32 words on an
 // 8-byte boundary: each pair is written and read as one aligned u64 (atomic on x64), so a peer never sees a new
 // PID with an old generation or the reverse. Layout unchanged: the low word is the PID, the high word the generation.

 /// <summary>Atomic read of (snPid, snGeneration).</summary>
        public void ReadSnIdentity(out uint pid, out uint generation)
        {
            ulong v = U64(0x040);
            pid = (uint)v; generation = (uint)(v >> 32);
        }

        /// <summary>Atomic write of (snPid, snGeneration): creator only.</summary>
        public void WriteSnIdentity(uint pid, uint generation) { U64(0x040, ((ulong)generation << 32) | pid); }

        /// <summary>Atomic read of (sePid, seGeneration).</summary>
        public void ReadSeIdentity(out uint pid, out uint generation)
        {
            ulong v = U64(0x080);
            pid = (uint)v; generation = (uint)(v >> 32);
        }

        /// <summary>Atomic write of (sePid, seGeneration): SE only.</summary>
        public void WriteSeIdentity(uint pid, uint generation) { U64(0x080, ((ulong)generation << 32) | pid); }

        /// <summary>
 /// Opener (SE) attach: generation = the value found + 1 (never 0). The heartbeat is written first, then PID and
 /// generation together (one u64 store), so the peer sees a fresh heartbeat with the new identity. SE block only.
 /// </summary>
        public uint AttachSe(uint pid, ulong nowMs)
        {
            uint gen = SeGeneration + 1;
            if (gen == 0) gen = 1;
            SeHeartbeatMs = nowMs;
            WriteSeIdentity(pid, gen);
            return gen;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
 // Creator rules (CONTRACT §6)
 // ------------------------------------------------------------------------------------------------------------

    public enum CreatorOutcome
    {
        /// <summary>A new, zero-filled section was initialised (generation 1).</summary>
        Created = 0,
        /// <summary>An existing compatible section with a dead snPid was re-initialised in place (generation + 1).</summary>
        Reinitialised = 1,
        /// <summary>An existing section differs or its snPid is alive: nothing was written; stay dormant.</summary>
        Refused = 2,
        /// <summary>A native call failed (Message has the error); nothing usable.</summary>
        Failed = 3,
    }

    public sealed class CreatorOptions
    {
        public string Name = WireInfo.SectionName;
        public long Size = Layout.SectionSize;
        /// <summary>Null: CONTRACT §6 SDDL for the current user's SID.</summary>
        public string Sddl;
        public uint Pid = Win32.CurrentProcessId();
        public uint SnFlags = Wire.SnFlags.BridgeMode;
        public ulong SnHwnd;
        /// <summary>Creator rule 2 liveness check (OpenProcess + WaitForSingleObject(h, 0)).</summary>
        public Func<uint, bool> IsProcessAlive = Win32.IsProcessAlive;
        public Func<ulong> NowMs = Win32.TickCount64;
        /// <summary>Null: QueryPerformanceFrequency.</summary>
        public ulong? QpcFrequency;
        /// <summary>
 /// Rule 3 "rewrite its own seqlock records in place", called while `magic` is 0 (re-initialisation only).
 /// Null: C7, the C9 header and the C10 result are rewritten with zero payloads, their `seq` continuing.
 /// </summary>
        public Action<SharedSection> RewriteOwnRecords;

        /// <summary>S0.M7 mutant: also zero every producer head word (and the SN consumer tails with them).</summary>
        internal bool MutantZeroProducerHeads;
    }

    public sealed class CreatorResult
    {
        public CreatorOutcome Outcome { get; internal set; }
        /// <summary>The mapped section (Created / Reinitialised only); the caller owns it.</summary>
        public SharedSection Section { get; internal set; }
        public uint Generation { get; internal set; }
        /// <summary>Refused/Failed: the field that differs, and both values.</summary>
        public string Field { get; internal set; }
        public string Expected { get; internal set; }
        public string Found { get; internal set; }
        public string Message { get; internal set; }

        public override string ToString()
        {
            switch (Outcome)
            {
                case CreatorOutcome.Created: return "created generation=" + Generation;
                case CreatorOutcome.Reinitialised: return "reinitialised generation=" + Generation;
                case CreatorOutcome.Refused: return "REFUSE field=" + Field + " expected=" + Expected + " found=" + Found + (Message != null ? " (" + Message + ")" : "");
                default: return "FAILED " + Message;
            }
        }
    }

    /// <summary>
 /// The creator rules of CONTRACT §6 (Subnautica, in bridge mode, holding its mutex). Reusable by the SN plugin and
 /// by the in-process tests; the Python stand-in implements the same rules independently.
 /// </summary>
    public static unsafe class SectionCreator
    {
        public static CreatorResult CreateOrAttach(CreatorOptions o)
        {
            if (o == null) o = new CreatorOptions();
            SharedSection s;
            bool existed;
            try
            {
                string sddl = o.Sddl ?? WireInfo.SectionSddlForCurrentUser();
                s = SharedSection.Create(o.Name, o.Size, sddl, out existed);
            }
            catch (Exception e)
            {
                return new CreatorResult { Outcome = CreatorOutcome.Failed, Message = e.GetType().Name + ": " + e.Message };
            }

            if (!existed)
            {
                uint gen = InitializeNew(s, o);
                return new CreatorResult { Outcome = CreatorOutcome.Created, Section = s, Generation = gen };
            }

            CreatorResult refusal = CheckExisting(s.Base, s.MappedSize, o);
            if (refusal != null)
            {
                s.Dispose();
                return refusal;
            }
            uint g = Reinitialise(s, o);
            return new CreatorResult { Outcome = CreatorOutcome.Reinitialised, Section = s, Generation = g };
        }

        /// <summary>Rule 1: header and region table on a zero-filled section, SN block, then the magic last.</summary>
        public static uint InitializeNew(SharedSection s, CreatorOptions o)
        {
            if (o == null) o = new CreatorOptions();
            byte* b = s.Base;
            var hv = new HeaderView(b);
            hv.LayoutMajor = Layout.Major;
            hv.LayoutMinor = Layout.Minor;
            hv.ByteOrderMark = WireInfo.ByteOrderMark;
            hv.HeaderSize = Layout.HeaderSize;
            hv.SectionSize = (ulong)Layout.SectionSize;
            hv.LayoutHash = LayoutHash.Compiled;
            hv.WireVersion = WireInfo.WireVersion;
            hv.SiteVersion = WireInfo.SiteVersion;
            hv.DamageScaleVersion = WireInfo.DamageScaleVersion;
            hv.RegionCount = (uint)Layout.RegionCount;
            hv.QpcFrequency = o.QpcFrequency ?? (ulong)Win32.QpcFrequency();
            hv.DevControl = 0;
            WriteRegionTable(b);
            hv.SnPhase = SnPhase.Booting;
            hv.SnHeartbeatMs = o.NowMs();
            hv.WriteSnIdentity(o.Pid, 1);
            hv.SnFlags = o.SnFlags;
            hv.SnHwnd = o.SnHwnd;
            hv.PublishMagic();
            return 1;
        }

        /// <summary>Writes the C0 region table (0x100, regionCount × 32 B) from Layout.</summary>
        public static void WriteRegionTable(byte* sectionBase)
        {
            for (int i = 0; i < Layout.RegionCount; i++)
            {
                RegionDef g = Layout.Regions[i];
                var e = new RegionEntry
                {
                    id = (uint)g.Id, kind = (uint)g.Kind, offset = (ulong)g.Offset, size = (ulong)g.Size,
                    recordSize = (uint)g.RecordSize, version = g.Version,
                };
                *(RegionEntry*)(sectionBase + Layout.RegionTableOffset + i * Layout.RegionEntrySize) = e;
            }
        }

        /// <summary>
 /// Rule 2 on an existing section: magic, layoutMajor, sectionSize (header word and mapped size) and layoutHash
 /// must match (CONTRACT §6 rule 2), then every other identity word of validation steps 1-5 (byteOrderMark,
 /// headerSize, wireVersion, siteVersion, damageScaleVersion, qpcFrequency) and the C0 region table must equal the
 /// creator's own, and the recorded snPid must not be a live process. Returns null when re-initialisation may
 /// proceed, else a Refused result naming the field and both values. Reads only.
 /// <para>
 /// Why the version words: rule 3 rewrites only the SN block, so re-initialising a section
 /// whose words came from a creator of another wire/site/damage version would leave that version's identity in
 /// place and let both sides accept a link that validation step 4 exists to refuse. A creator of another
 /// version therefore refuses like one of another layout ("a holder of another version keeps it").
 /// </para>
 /// </summary>
        public static CreatorResult CheckExisting(byte* b, long mappedSize, CreatorOptions o)
        {
            if (o == null) o = new CreatorOptions();
            if (mappedSize < Layout.HeaderSize)
                return Refuse("sectionSize", Hex((ulong)Layout.SectionSize), Hex((ulong)mappedSize) + " (mapped)");
            var hv = new HeaderView(b);
            uint magic = hv.Magic;
            if (magic != WireInfo.Magic) return Refuse("magic", Hex(WireInfo.Magic), Hex(magic));
            ushort major = hv.LayoutMajor;
            if (major != Layout.Major) return Refuse("layoutMajor", Dec(Layout.Major), Dec(major));
            ulong size = hv.SectionSize;
            if (size != (ulong)Layout.SectionSize) return Refuse("sectionSize", Hex((ulong)Layout.SectionSize), Hex(size));
            if (mappedSize != Layout.SectionSize)
                return Refuse("sectionSize", Hex((ulong)Layout.SectionSize), Hex((ulong)mappedSize) + " (mapped)");
            ulong hash = hv.LayoutHash;
            if (hash != LayoutHash.Compiled) return Refuse("layoutHash", LayoutHash.Format(LayoutHash.Compiled), LayoutHash.Format(hash));

            // The remaining identity words and the region table: the validation of both sides (steps 1-5) run on the
 // existing header, before anything is written. Step 6 (the WM-1 self-check) is not about the section.
            ValidationResult v = SectionValidation.Validate(b, mappedSize,
                new ValidationOptions { QpcFrequency = o.QpcFrequency, CheckRegionTable = true });
            if (!v.Ok)
            {
                CreatorResult r = Refuse(v.Field, v.Expected, v.Found);
                r.Message = "validation step " + v.Step + " on the existing section (a creator of another version holds it)";
                return r;
            }

            uint pid = hv.SnPid;
            // PID reuse (integration fix, matches the Python stand-in): a recorded snPid equal to the creator's own PID
 // can only be a dead predecessor whose PID the OS gave to this process (the SN mutex is held by the
 // caller), so it counts as stale. Without this the creator would see itself as "alive" and refuse for
 // as long as SE keeps the section open. Not in CONTRACT §6 rule 2 (reported in docs/S0-STATUS.md).
 // PID reuse by another process is excluded by the opener: SE keeps its SYNCHRONIZE handle on the recorded
 // snPid until it sees a new generation, so while SE holds the section the dead PID
 // cannot be given to another process and OpenProcess returns the exited (signalled) process object.
            if (pid != 0 && pid != o.Pid && o.IsProcessAlive(pid))
            {
                CreatorResult r = Refuse("snPid", "a dead process", Dec(pid) + " (alive)");
                r.Message = "the recorded Subnautica process is alive";
                return r;
            }
            return null;
        }

        /// <summary>
 /// Rule 3: snPhase = Booting, magic:= 0 (release), SN block rewritten (generation = found + 1, PID, heartbeat),
 /// tails of the rings Subnautica consumes (C3, C4, C11c):= heads, producer heads and every seqlock seq kept,
 /// own seqlock records rewritten in place, magic last. Returns the new generation.
 /// </summary>
        public static uint Reinitialise(SharedSection s, CreatorOptions o)
        {
            if (o == null) o = new CreatorOptions();
            byte* b = s.Base;
            var hv = new HeaderView(b);
            hv.SnPhase = SnPhase.Booting;
            hv.ClearMagic();
            uint gen = hv.SnGeneration + 1;
            if (gen == 0) gen = 1;
            hv.SnHeartbeatMs = o.NowMs();
            hv.WriteSnIdentity(o.Pid, gen);
            hv.SnFlags = o.SnFlags;
            hv.SnHwnd = o.SnHwnd;
            if (o.MutantZeroProducerHeads)
            {
                for (int r = 0; r < Layout.RingCount; r++) RingControl.PublishHead(b, (RingIndex)r, 0);
            }
            Resync.ConsumerTailsToHeads(b, Resync.SnConsumes);
            if (o.RewriteOwnRecords != null) o.RewriteOwnRecords(s);
            else RewriteSnRecords(b);
            hv.PublishMagic();
            return gen;
        }

        /// <summary>Default rule-3 rewrite: C7 SnStatus (snPhase Booting), C9 header and C10 result, seq continuing.</summary>
        public static void RewriteSnRecords(byte* b)
        {
            var status = new SnStatus { snPhase = SnPhase.Booting, hostKind = HostKind.Subnautica1 };
            Seqlock.RewriteInPlace(Layout.RegionPtr(b, RegionId.C7SnStatus), status);
            Seqlock.RewriteInPlace(Layout.RegionPtr(b, RegionId.C9SnCreatures), new SnCreaturesHeader());
            Seqlock.RewriteInPlace(Layout.RegionPtr(b, RegionId.C10Capture) + CaptureLayout.ResultOffset, new CaptureResult());
        }

        private static CreatorResult Refuse(string field, string expected, string found)
        {
            return new CreatorResult { Outcome = CreatorOutcome.Refused, Field = field, Expected = expected, Found = found };
        }

        internal static string Hex(ulong v) { return "0x" + v.ToString("x", CultureInfo.InvariantCulture); }
        internal static string Dec(ulong v) { return v.ToString(CultureInfo.InvariantCulture); }
    }

    // ------------------------------------------------------------------------------------------------------------
 // Validation on both sides (CONTRACT §6)
 // ------------------------------------------------------------------------------------------------------------

    public enum ValidationOutcome
    {
        Ok = 0,
        /// <summary>`magic` is 0: the creator is (re)initialising. Log `DE.Link WAIT` and retry later.</summary>
        NotReady = 1,
        /// <summary>A field differs: display bridge off, log the field and both values (`DE.Link REJECT`), write nothing.</summary>
        Reject = 2,
    }

    public sealed class ValidationResult
    {
        public ValidationOutcome Outcome { get; internal set; }
        public bool Ok { get { return Outcome == ValidationOutcome.Ok; } }
        /// <summary>CONTRACT §6 validation step (1-6; 0 when OK; 7 for the optional region-table check).</summary>
        public int Step { get; internal set; }
        public string Field { get; internal set; }
        public string Expected { get; internal set; }
        public string Found { get; internal set; }
        /// <summary>Accepted differences that must be logged (a different layoutMinor).</summary>
        public List<string> Notes { get; } = new List<string>();

        public override string ToString()
        {
            string notes = Notes.Count > 0 ? " notes: " + string.Join("; ", Notes.ToArray()) : "";
            if (Outcome == ValidationOutcome.Ok) return "OK" + notes;
            return (Outcome == ValidationOutcome.NotReady ? "NOT READY" : "REJECT") + " step " + Step + " field=" + Field +
                   " expected=" + Expected + " found=" + Found + notes;
        }
    }

    public sealed class ValidationOptions
    {
        /// <summary>Null: the local QueryPerformanceFrequency.</summary>
        public ulong? QpcFrequency;
        /// <summary>Step 6: the WM-1 in-game self-check result, when known (null = not checked here).</summary>
        public bool? SelfTestPass;
        /// <summary>Also compare the C0 region table with Layout (not a CONTRACT step; off by default).</summary>
        public bool CheckRegionTable;
    }

    /// <summary>The validation steps of CONTRACT §6, identical on both sides. Reads only.</summary>
    public static unsafe class SectionValidation
    {
        public static ValidationResult Validate(SharedSection s, ValidationOptions o = null)
        {
            if (s == null || !s.IsOpen) throw new ArgumentException("section not open");
            return Validate(s.Base, s.MappedSize, o);
        }

        public static ValidationResult Validate(byte* b, long mappedSize, ValidationOptions o = null)
        {
            if (o == null) o = new ValidationOptions();
            var r = new ValidationResult();
            if (mappedSize < Layout.HeaderSize)
                return Fail(r, ValidationOutcome.Reject, 1, "sectionSize", Hex((ulong)Layout.SectionSize), Hex((ulong)mappedSize) + " (mapped)");
            var hv = new HeaderView(b);

            // 1. magic, byteOrderMark, headerSize, sectionSize
            uint magic = hv.Magic; // acquire: header words written before the magic are visible
            if (magic == 0) return Fail(r, ValidationOutcome.NotReady, 1, "magic", Hex(WireInfo.Magic), Hex(0));
            if (magic != WireInfo.Magic) return Fail(r, ValidationOutcome.Reject, 1, "magic", Hex(WireInfo.Magic), Hex(magic));
            uint bom = hv.ByteOrderMark;
            if (bom != WireInfo.ByteOrderMark) return Fail(r, ValidationOutcome.Reject, 1, "byteOrderMark", Hex(WireInfo.ByteOrderMark), Hex(bom));
            uint hs = hv.HeaderSize;
            if (hs != Layout.HeaderSize) return Fail(r, ValidationOutcome.Reject, 1, "headerSize", Hex(Layout.HeaderSize), Hex(hs));
            ulong ss = hv.SectionSize;
            if (ss != (ulong)Layout.SectionSize) return Fail(r, ValidationOutcome.Reject, 1, "sectionSize", Hex((ulong)Layout.SectionSize), Hex(ss));
            if (mappedSize != Layout.SectionSize)
                return Fail(r, ValidationOutcome.Reject, 1, "sectionSize", Hex((ulong)Layout.SectionSize), Hex((ulong)mappedSize) + " (mapped)");

            // 2. layoutMajor equal; a different minor is accepted and logged
            ushort major = hv.LayoutMajor;
            if (major != Layout.Major) return Fail(r, ValidationOutcome.Reject, 2, "layoutMajor", Dec(Layout.Major), Dec(major));
            ushort minor = hv.LayoutMinor;
            if (minor != Layout.Minor) r.Notes.Add("layoutMinor differs: local " + Layout.Minor + ", section " + minor + " (accepted)");

            // 3. layoutHash and record sizes
            ulong hash = hv.LayoutHash;
            if (hash != LayoutHash.Compiled) return Fail(r, ValidationOutcome.Reject, 3, "layoutHash", LayoutHash.Format(LayoutHash.Compiled), LayoutHash.Format(hash));
            string layoutError;
            if (!Layout.TryValidate(out layoutError)) return Fail(r, ValidationOutcome.Reject, 3, "recordSizes", "Layout.Validate OK", layoutError);

            // 4. versions
            uint wv = hv.WireVersion;
            if (wv != WireInfo.WireVersion) return Fail(r, ValidationOutcome.Reject, 4, "wireVersion", Dec(WireInfo.WireVersion), Dec(wv));
            uint sv = hv.SiteVersion;
            if (sv != WireInfo.SiteVersion) return Fail(r, ValidationOutcome.Reject, 4, "siteVersion", Dec(WireInfo.SiteVersion), Dec(sv));
            uint dv = hv.DamageScaleVersion;
            if (dv != WireInfo.DamageScaleVersion) return Fail(r, ValidationOutcome.Reject, 4, "damageScaleVersion", Dec(WireInfo.DamageScaleVersion), Dec(dv));

            // 5. qpcFrequency
            ulong localQpf = o.QpcFrequency ?? (ulong)Win32.QpcFrequency();
            ulong qpf = hv.QpcFrequency;
            if (qpf != localQpf) return Fail(r, ValidationOutcome.Reject, 5, "qpcFrequency", Dec(localQpf), Dec(qpf));

            // 6. WM-1 in-game self-check
            if (o.SelfTestPass.HasValue && !o.SelfTestPass.Value)
                return Fail(r, ValidationOutcome.Reject, 6, "wm1SelfTest", "PASS", "FAIL");

            // optional: region table equals Layout
            if (o.CheckRegionTable)
            {
                uint count = hv.RegionCount;
                if (count != (uint)Layout.RegionCount) return Fail(r, ValidationOutcome.Reject, 7, "regionCount", Dec((ulong)Layout.RegionCount), Dec(count));
                for (int i = 0; i < Layout.RegionCount; i++)
                {
                    RegionEntry e = hv.Region(i);
                    RegionDef g = Layout.Regions[i];
                    if (e.id != (uint)g.Id || e.kind != (uint)g.Kind || e.offset != (ulong)g.Offset || e.size != (ulong)g.Size ||
                        e.recordSize != (uint)g.RecordSize || e.version != g.Version)
                        return Fail(r, ValidationOutcome.Reject, 7, "region[" + i + "]", Describe(g), Describe(e));
                }
            }
            r.Outcome = ValidationOutcome.Ok;
            return r;
        }

        private static string Describe(RegionDef g)
        {
            return "{id " + Hex((uint)g.Id) + " kind " + (uint)g.Kind + " " + Hex((ulong)g.Offset) + " " + Hex((ulong)g.Size) + " " + g.RecordSize + " v" + g.Version + "}";
        }

        private static string Describe(RegionEntry e)
        {
            return "{id " + Hex(e.id) + " kind " + e.kind + " " + Hex(e.offset) + " " + Hex(e.size) + " " + e.recordSize + " v" + e.version + "}";
        }

        private static ValidationResult Fail(ValidationResult r, ValidationOutcome outcome, int step, string field, string expected, string found)
        {
            r.Outcome = outcome; r.Step = step; r.Field = field; r.Expected = expected; r.Found = found;
            return r;
        }

        private static string Hex(ulong v) { return SectionCreator.Hex(v); }
        private static string Dec(ulong v) { return SectionCreator.Dec(v); }
    }
}
