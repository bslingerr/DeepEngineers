using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.Wire.CrossTest
{
    /// <summary>
 /// S0.N4 (cross-language), C# half. Every mode prints JSON lines on stdout ({"event"...}); the Python driver
 /// (tests/wire/cross_test.py) owns the verdict. Exit codes: 0 ok, 1 usage/failure, 2 section absent,
 /// 3 rejected/refused (nothing written), 5 not ready (magic 0).
 ///
 /// Synthetic traffic (both languages generate and check the same deterministic content):
 /// message i on C11c (salt 0x11) / C11d (salt 0x22): type 1, n = 8 + (i * 7919) % 4000 bytes,
 /// payload u32 i, u32 n, then byte j = (i * 31 + j * 7 + salt) &amp; 0xFF;
 /// C8 record i: u32 dedupSeq = i + 1, u32 snGeneration, byte j (8..127) = (i * 13 + j) &amp; 0xFF;
 /// C7 SnStatus (C# writes): snFrame = k, presentedSeTick = 3k, c3BytesDrained = 7k, snPhase, hostKind 1;
 /// C5 SeState (Python writes): tick = k, crossingTick = 3k, siteHash = 7k.
 /// </summary>
    internal static unsafe class Program
    {
        private const uint MessageType = 1;
        private const byte SaltToSn = 0x11, SaltFromSn = 0x22;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && args[0] == "layout") return LayoutMode(args[1]);
                if (args.Length >= 3 && args[0] == "open") return OpenMode(args[1], ParseD(args[2]), Has(args, "--attach-se"));
                if (args.Length >= 3 && args[0] == "create") return CreateMode(args[1], ParseD(args[2]), IntOpt(args, "--traffic", 400), IntOpt(args, "--torture-ms", 2000) / 1000.0);
                if (args.Length >= 3 && args[0] == "devcontrol") return DevControlMode(args[1], ParseD(args[2]));
            }
            catch (Exception e)
            {
                Emit(new J().S("event", "exception").S("type", e.GetType().FullName).S("message", e.Message).S("stack", e.StackTrace));
                return 1;
            }
            Console.Error.WriteLine("usage: layout <out.json> | open <name> <seconds> [--attach-se] | create <name> <seconds> [--traffic N] [--torture-ms MS]");
            return 1;
        }

        // ============================================================================================== layout
        private static int LayoutMode(string outPath)
        {
            string err;
            bool ok = Layout.TryValidate(out err);
            var sb = new StringBuilder();
            sb.Append("{\"layout\":\"").Append(Layout.Major).Append('.').Append(Layout.Minor).Append("\"");
            sb.Append(",\"validate\":").Append(ok ? "true" : "false");
            sb.Append(",\"validateError\":").Append(Str(err));
            sb.Append(",\"layoutHash\":").Append(Str(LayoutHash.Format(LayoutHash.Compiled)));
            sb.Append(",\"pinned\":").Append(Str(LayoutHash.Format(LayoutHash.PinnedLayout10)));
            sb.Append(",\"sectionSize\":").Append(Layout.SectionSize.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"regions\":[");
            for (int i = 0; i < Layout.Regions.Count; i++)
            {
                RegionDef g = Layout.Regions[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append((uint)g.Id).Append(",\"kind\":").Append((uint)g.Kind)
                  .Append(",\"offset\":").Append(g.Offset).Append(",\"size\":").Append(g.Size)
                  .Append(",\"recordSize\":").Append(g.RecordSize).Append(",\"version\":").Append(g.Version)
                  .Append(",\"name\":").Append(Str(g.Name)).Append('}');
            }
            sb.Append("],\"rings\":[");
            for (int r = 0; r < Layout.RingCount; r++)
            {
                if (r > 0) sb.Append(',');
                var ring = (RingIndex)r;
                sb.Append("{\"index\":").Append(r).Append(",\"regionId\":").Append((uint)Layout.RingRegion(ring))
                  .Append(",\"head\":").Append(Layout.HeadOffset(ring)).Append(",\"tail\":").Append(Layout.TailOffset(ring))
                  .Append(",\"name\":").Append(Str(ring.ToString())).Append('}');
            }
            sb.Append("],\"records\":{");
            bool first = true;
            foreach (RecordDef rec in Layout.Records)
            {
                if (!first) sb.Append(',');
                first = false;
                // Offsets and sizes are read from the compiled structs (Marshal.OffsetOf / SizeOf), not from the table.
                sb.Append(Str(rec.Name)).Append(":{\"size\":").Append(Marshal.SizeOf(rec.ClrType))
                  .Append(",\"managedSize\":").Append(Layout.ManagedSize(rec.ClrType)).Append(",\"fields\":[");
                for (int i = 0; i < rec.Fields.Count; i++)
                {
                    FieldDef f = rec.Fields[i];
                    FieldInfo fi = rec.ClrType.GetField(f.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    long off = fi != null ? Marshal.OffsetOf(rec.ClrType, f.Member).ToInt64() : -1;
                    int size = fi != null ? Marshal.SizeOf(fi.FieldType) : -1;
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(Str(f.Name)).Append(',').Append(off).Append(',').Append(size).Append(',').Append(Str(f.Type)).Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("},\"canonicalText\":").Append(Str(LayoutHash.CanonicalText())).Append('}');
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            Emit(new J().S("event", "layout").S("out", outPath).B("validate", ok).S("layoutHash", LayoutHash.Format(LayoutHash.Compiled)));
            return ok ? 0 : 1;
        }

        // ================================================================================================ open
        private static int OpenMode(string name, double seconds, bool attachSe)
        {
            SharedSection s = SharedSection.OpenExisting(name, Layout.SectionSize);
            if (s == null)
            {
                Emit(new J().S("event", "absent").S("name", name));
                return 2;
            }
            using (s)
            {
                HeaderView hv = s.Header;
                ValidationResult v = SectionValidation.Validate(s, new ValidationOptions { CheckRegionTable = true, SelfTestPass = true });
                Emit(Header(new J().S("event", "validation").S("outcome", v.Outcome.ToString()).N("step", v.Step)
                    .S("field", v.Field).S("expected", v.Expected).S("found", v.Found).S("text", v.ToString())
                    .N("mappedSize", s.MappedSize), hv));
                if (v.Outcome == ValidationOutcome.NotReady) return 5;
                if (!v.Ok) return 3; // REJECT: nothing written

                uint me = Win32.CurrentProcessId();
                uint gen = 0;
                if (attachSe)
                {
                    gen = hv.AttachSe(me, Win32.TickCount64());
                    hv.SePhase = SePhase.Menu;
                    Emit(new J().S("event", "attached").N("sePid", me).N("seGeneration", gen));
                }

                byte* c7 = Layout.RegionPtr(s.Base, RegionId.C7SnStatus);
                var status = default(SnStatus);
                long samples = 0, c7Fresh = 0, c7Busy = 0, c7Never = 0, hbDistinct = 0;
                ulong lastHb = 0, maxAge = 0;
                ulong lastFrame = 0;
                long frameBackwards = 0;
                uint snPidFirst = hv.SnPid, snGenFirst = hv.SnGeneration;
                bool snIdentityStable = true;
                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end)
                {
                    ulong now = Win32.TickCount64();
                    if (attachSe) hv.SeHeartbeatMs = now;
                    ulong hb = hv.SnHeartbeatMs;
                    if (hb != lastHb) { hbDistinct++; lastHb = hb; }
                    ulong age = now >= hb ? now - hb : 0;
                    if (age > maxAge) maxAge = age;
                    if (hv.SnPid != snPidFirst || hv.SnGeneration != snGenFirst) snIdentityStable = false;
                    uint seq;
                    SeqlockReadStatus st = Seqlock.TryRead(c7, ref status, out seq);
                    if (st == SeqlockReadStatus.Fresh)
                    {
                        c7Fresh++;
                        if (status.snFrame < lastFrame) frameBackwards++;
                        lastFrame = status.snFrame;
                    }
                    else if (st == SeqlockReadStatus.Busy) c7Busy++;
                    else if (st == SeqlockReadStatus.Never) c7Never++;
                    samples++;
                    Thread.Sleep(2);
                }
                Emit(Header(new J().S("event", "summary").N("samples", samples).N("snHeartbeatDistinct", hbDistinct)
                    .N("snHeartbeatMaxAgeMs", (long)maxAge).B("snIdentityStable", snIdentityStable)
                    .N("c7Fresh", c7Fresh).N("c7Busy", c7Busy).N("c7Never", c7Never).N("c7LastSnFrame", (long)lastFrame)
                    .N("c7LastSnPhase", status.snPhase).N("c7FrameBackwards", frameBackwards)
                    .N("myPid", me).N("mySeGeneration", gen), hv));
                return 0;
            }
        }

        // ============================================================================================== create
        private static int CreateMode(string name, double seconds, int traffic, double tortureSeconds)
        {
            if (!name.Contains("DeepEngineers_xtest_")) { Console.Error.WriteLine("create: test section names only (DeepEngineers_xtest_*)"); return 1; }
            CreatorResult res = SectionCreator.CreateOrAttach(new CreatorOptions { Name = name });
            Emit(new J().S("event", "created").S("outcome", res.Outcome.ToString()).N("generation", res.Generation)
                .N("pid", Win32.CurrentProcessId()).S("text", res.ToString())
                .N("mappedSize", res.Section != null ? res.Section.MappedSize : 0));
            if (res.Outcome != CreatorOutcome.Created && res.Outcome != CreatorOutcome.Reinitialised) return 3;

            using (SharedSection s = res.Section)
            {
                byte* b = s.Base;
                HeaderView hv = s.Header;
                uint gen = res.Generation;
                var c11d = new ByteRing(b, RingIndex.C11dCtlFromSn);   // SN produces
                var c11c = new ByteRing(b, RingIndex.C11cCtlToSn);     // SN consumes
                var c8 = new EntryRing(b, RingIndex.C8SnDamage);       // SN produces
                byte* c7 = Layout.RegionPtr(b, RegionId.C7SnStatus);
                byte* c5 = Layout.RegionPtr(b, RegionId.C5SeState);

                var outBuf = new byte[8 + 4000];
                var inBuf = new byte[64 * 1024];
                byte* rec = stackalloc byte[128];
                int sentD = 0, sentC8 = 0, recvC = 0;
                long badC = 0, corruptC = 0, writeErrors = 0, fullD = 0, fullC8 = 0;
                long c5Fresh = 0, c5Torn = 0, c5Backwards = 0, c5Busy = 0;
                ulong c5LastTick = 0;
                var se = default(SeState);
                ulong k = 0;
                uint sePidSeen = 0, seGenSeen = 0;
                long seHbDistinct = 0;
                ulong seLastHb = 0, seMaxAge = 0;
                var t0 = DateTime.UtcNow;
                DateTime end = t0.AddSeconds(seconds);
                bool done = false;
                while (DateTime.UtcNow < end)
                {
                    ulong now = Win32.TickCount64();
                    hv.SnHeartbeatMs = now;
                    if ((DateTime.UtcNow - t0).TotalMilliseconds > 300 && hv.SnPhase == SnPhase.Booting) hv.SnPhase = SnPhase.Menu;

                    // C7 seqlock (several writes per loop: the Python reader races them)
                    for (int w = 0; w < 50; w++)
                    {
                        k++;
                        var st = default(SnStatus);
                        st.snPhase = hv.SnPhase;
                        st.snFrame = k;
                        st.presentedSeTick = 3 * k;
                        st.c3BytesDrained = 7 * k;
                        st.hostKind = HostKind.Subnautica1;
                        Seqlock.Write(c7, st);
                    }

                    // C11d producer
                    while (sentD < traffic)
                    {
                        int n = MessageLength(sentD);
                        FillMessage(outBuf, sentD, n, SaltFromSn);
                        RingWriteStatus w = c11d.TryWrite(MessageType, outBuf, 0, n);
                        if (w == RingWriteStatus.Ok) { sentD++; continue; }
                        if (w == RingWriteStatus.Full) fullD++; else writeErrors++;
                        break;
                    }
                    // C8 producer
                    while (sentC8 < traffic * 4)
                    {
                        FillRecord(rec, sentC8, gen);
                        RingWriteStatus w = c8.TryWrite(rec, 128);
                        if (w == RingWriteStatus.Ok) { sentC8++; continue; }
                        if (w == RingWriteStatus.Full) fullC8++; else writeErrors++;
                        break;
                    }
                    // C11c consumer
                    for (;;)
                    {
                        uint type;
                        int n;
                        RingReadStatus r = c11c.TryRead(out type, inBuf, out n);
                        if (r == RingReadStatus.Empty) break;
                        if (r != RingReadStatus.Ok) { corruptC++; break; }
                        if (type != MessageType || !CheckMessage(inBuf, n, recvC, SaltToSn)) badC++;
                        recvC++;
                    }
                    // C5 seqlock reader
                    uint seq;
                    SeqlockReadStatus rs = Seqlock.TryRead(c5, ref se, out seq);
                    if (rs == SeqlockReadStatus.Fresh)
                    {
                        c5Fresh++;
                        if (se.crossingTick != 3 * se.tick || se.siteHash != 7 * se.tick) c5Torn++;
                        if (se.tick < c5LastTick) c5Backwards++;
                        c5LastTick = se.tick;
                    }
                    else if (rs == SeqlockReadStatus.Busy) c5Busy++;

                    // SE block written by Python
                    uint sp = hv.SePid;
                    if (sp != 0)
                    {
                        sePidSeen = sp;
                        seGenSeen = hv.SeGeneration;
                        ulong shb = hv.SeHeartbeatMs;
                        if (shb != seLastHb) { seHbDistinct++; seLastHb = shb; }
                        ulong age = now >= shb ? now - shb : 0;
                        if (age > seMaxAge) seMaxAge = age;
                    }

                    bool drained = RingControl.ReadTail(b, RingIndex.C11dCtlFromSn) == RingControl.ReadHead(b, RingIndex.C11dCtlFromSn) &&
                                   RingControl.ReadTail(b, RingIndex.C8SnDamage) == RingControl.ReadHead(b, RingIndex.C8SnDamage);
                    if (sentD == traffic && sentC8 == traffic * 4 && recvC >= traffic && drained && c5Fresh > 0 && seHbDistinct > 20)
                    {
                        done = true;
                        break;
                    }
                    Thread.Sleep(1);
                }

                // Seqlock torture across languages: C7 written in a tight loop while Python reads it, C5 read in a
 // tight loop while Python writes it; every accepted copy must be coherent.
                long tortureWrites = 0, t5Fresh = 0, t5Torn = 0, t5Busy = 0;
                if (done)
                {
                    DateTime tEnd = DateTime.UtcNow.AddSeconds(tortureSeconds);
                    int it = 0;
                    while (DateTime.UtcNow < tEnd)
                    {
                        for (int w = 0; w < 64; w++)
                        {
                            k++;
                            var st = default(SnStatus);
                            st.snPhase = SnPhase.Menu;
                            st.snFrame = k;
                            st.presentedSeTick = 3 * k;
                            st.c3BytesDrained = 7 * k;
                            st.hostKind = HostKind.Subnautica1;
                            Seqlock.Write(c7, st);
                            tortureWrites++;
                        }
                        uint seq;
                        SeqlockReadStatus rs = Seqlock.TryRead(c5, ref se, out seq);
                        if (rs == SeqlockReadStatus.Fresh)
                        {
                            t5Fresh++;
                            if (se.crossingTick != 3 * se.tick || se.siteHash != 7 * se.tick) t5Torn++;
                            if (se.tick < c5LastTick) c5Backwards++;
                            c5LastTick = se.tick;
                        }
                        else if (rs == SeqlockReadStatus.Busy) t5Busy++;
                        if ((++it & 63) == 0)
                        {
                            ulong now = Win32.TickCount64();
                            hv.SnHeartbeatMs = now;
                            ulong shb = hv.SeHeartbeatMs;
                            if (shb != seLastHb) { seHbDistinct++; seLastHb = shb; }
                            ulong age = now >= shb ? now - shb : 0;
                            if (age > seMaxAge) seMaxAge = age;
                        }
                    }
                }
                Emit(Header(new J().S("event", "summary").B("done", done).N("generation", gen).N("traffic", traffic)
                    .N("tortureC7Writes", tortureWrites).N("tortureC5Fresh", t5Fresh).N("tortureC5Torn", t5Torn).N("tortureC5Busy", t5Busy)
                    .N("c11dSent", sentD).N("c8Sent", sentC8).N("c11cReceived", recvC).N("c11cBad", badC)
                    .N("c11cCorrupt", corruptC + c11c.CorruptCount).N("writeErrors", writeErrors).N("c11dFull", fullD).N("c8Full", fullC8)
                    .N("c7Writes", (long)k).N("c5Fresh", c5Fresh).N("c5Torn", c5Torn).N("c5Backwards", c5Backwards).N("c5Busy", c5Busy)
                    .N("c5LastTick", (long)c5LastTick).N("sePidSeen", sePidSeen).N("seGenerationSeen", seGenSeen)
                    .N("seHeartbeatDistinct", seHbDistinct).N("seHeartbeatMaxAgeMs", (long)seMaxAge)
                    .N("c11dHead", (long)RingControl.ReadHead(b, RingIndex.C11dCtlFromSn))
                    .N("c11cTail", (long)RingControl.ReadTail(b, RingIndex.C11cCtlToSn))
                    .N("c8Head", (long)RingControl.ReadHead(b, RingIndex.C8SnDamage)), hv));
                return done ? 0 : 1;
            }
        }

        // ========================================================================================= traffic
        private static int MessageLength(int i) { return 8 + (int)(((long)i * 7919) % 4000); }

        private static void FillMessage(byte[] buf, int i, int n, byte salt)
        {
            BitConverter.GetBytes((uint)i).CopyTo(buf, 0);
            BitConverter.GetBytes((uint)n).CopyTo(buf, 4);
            for (int j = 8; j < n; j++) buf[j] = (byte)((i * 31 + j * 7 + salt) & 0xFF);
        }

        private static bool CheckMessage(byte[] buf, int n, int i, byte salt)
        {
            if (n != MessageLength(i) || n < 8) return false;
            if (BitConverter.ToUInt32(buf, 0) != (uint)i || BitConverter.ToUInt32(buf, 4) != (uint)n) return false;
            for (int j = 8; j < n; j++) if (buf[j] != (byte)((i * 31 + j * 7 + salt) & 0xFF)) return false;
            return true;
        }

        private static void FillRecord(byte* rec, int i, uint generation)
        {
            *(uint*)rec = (uint)(i + 1);
            *(uint*)(rec + 4) = generation;
            for (int j = 8; j < 128; j++) rec[j] = (byte)((i * 13 + j) & 0xFF);
        }

        // ============================================================================================ output
        private static J Header(J j, HeaderView hv)
        {
            return j.S("magic", "0x" + hv.Magic.ToString("x8")).N("layoutMajor", hv.LayoutMajor).N("layoutMinor", hv.LayoutMinor)
                .S("layoutHash", LayoutHash.Format(hv.LayoutHash)).N("wireVersion", hv.WireVersion)
                .N("regionCount", hv.RegionCount).N("snPid", hv.SnPid).N("snGeneration", hv.SnGeneration).N("snPhase", hv.SnPhase)
                .N("snHeartbeatMs", (long)hv.SnHeartbeatMs).N("sePid", hv.SePid).N("seGeneration", hv.SeGeneration)
                .N("sePhase", hv.SePhase).N("seHeartbeatMs", (long)hv.SeHeartbeatMs).N("nowMs", (long)Win32.TickCount64());
        }

        private static void Emit(J j) { Console.Out.WriteLine(j.ToString()); Console.Out.Flush(); }

        private static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ========================================================================================== devcontrol
 /// <summary>
 /// Review finding 3: the SE relay's devControl rule (src/DeepEngineers.SE/Link.cs TryAcquireRelay/ReleaseRelay:
 /// Interlocked.CompareExchange, bit 1 only while bit 0 is clear) run in a loop for `seconds` against the
 /// Python harness on the same word (tests/wire/cross_test.py drives the other side). While it holds bit 1 it
 /// checks that bit 0 is clear and that its own bit is still set; any miss is a violation (two producers of
 /// C11c, or a bit erased by the other writer).
 /// </summary>
        private static int DevControlMode(string name, double seconds)
        {
            SharedSection s = SharedSection.OpenExisting(name, 0);
            if (s == null) { Emit(new J().S("event", "absent")); return 2; }
            using (s)
            {
                int* word = (int*)(s.Base + (int)Marshal.OffsetOf(typeof(SectionHeader), "devControl"));
                const int Harness = (int)DevControl.HarnessAttached, Relay = (int)DevControl.SeChatRelay;
                long acquired = 0, refused = 0, contended = 0, otherBitSeen = 0, ownBitLost = 0;
                var rng = new Random(12345);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Emit(new J().S("event", "started").N("pid", Win32.CurrentProcessId()));
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    bool got = false;
                    for (int i = 0; i < 8 && !got; i++)
                    {
                        int cur = Volatile.Read(ref *word);
                        if ((cur & (Harness | Relay)) != 0) { refused++; break; }
                        if (Interlocked.CompareExchange(ref *word, cur | Relay, cur) == cur) got = true;
                        else contended++;
                    }
                    if (!got) { Thread.SpinWait(20); continue; }
                    acquired++;
                    // hold bit 1 for 2-40 us (the relay holds it for up to 60 s), checking exclusivity all along
                    long holdUntil = sw.ElapsedTicks + MicrosToTicks(rng.Next(2, 40));
                    while (sw.ElapsedTicks < holdUntil)
                    {
                        int v = Volatile.Read(ref *word);
                        if ((v & Harness) != 0) { otherBitSeen++; break; }
                        if ((v & Relay) == 0) { ownBitLost++; break; }
                    }
                    for (int i = 0; i < 1000; i++)
                    {
                        int cur = Volatile.Read(ref *word);
                        if ((cur & Relay) == 0) break;
                        if (Interlocked.CompareExchange(ref *word, cur & ~Relay, cur) == cur) break;
                    }
                    // idle 1-8 us, then take the bit again: the harness gets its chances (and a racy one collides)
                    long idleUntil = sw.ElapsedTicks + MicrosToTicks(rng.Next(1, 8));
                    while (sw.ElapsedTicks < idleUntil) Thread.SpinWait(4);
                }
                Emit(new J().S("event", "result").N("acquired", acquired).N("refused", refused).N("contended", contended)
                    .N("otherBitSeen", otherBitSeen).N("ownBitLost", ownBitLost).N("finalWord", Volatile.Read(ref *word)));
            }
            return 0;
        }

        private static long MicrosToTicks(int us) { return Math.Max(1L, us * System.Diagnostics.Stopwatch.Frequency / 1000000L); }

        private static double ParseD(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        private static bool Has(string[] a, string flag) { return Array.IndexOf(a, flag) >= 0; }

        private static int IntOpt(string[] a, string flag, int dflt)
        {
            int i = Array.IndexOf(a, flag);
            return i >= 0 && i + 1 < a.Length ? int.Parse(a[i + 1], CultureInfo.InvariantCulture) : dflt;
        }

        /// <summary>A tiny ordered JSON object writer.</summary>
        private sealed class J
        {
            private readonly List<string> _parts = new List<string>();
            public J S(string k, string v) { _parts.Add(Str(k) + ":" + Str(v)); return this; }
            public J N(string k, long v) { _parts.Add(Str(k) + ":" + v.ToString(CultureInfo.InvariantCulture)); return this; }
            public J B(string k, bool v) { _parts.Add(Str(k) + ":" + (v ? "true" : "false")); return this; }
            public override string ToString() { return "{" + string.Join(",", _parts.ToArray()) + "}"; }
        }
    }
}
