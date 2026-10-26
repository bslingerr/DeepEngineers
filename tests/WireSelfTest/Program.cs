using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace WireSelfTest
{
    /// <summary>
 /// Cross-process C# ↔ C# check of the CONTRACT layout (rewritten in S0 from the pre-contract prototype test, ROADMAP
 /// S0 "Carried-in code"). The parent plays Subnautica: it creates a private test section with the creator rules
 /// (SectionCreator, CONTRACT §6 security descriptor), then starts itself with `--peer NAME`. The peer plays SE: it
 /// opens the section (open only), validates it (CONTRACT §6 steps 1-6), attaches its SE block, writes C1 SeFrame by
 /// seqlock in a tight loop and streams C4 byte-ring messages of random sizes (several wraps, pad records). The
 /// parent checks every accepted C1 copy for coherence and every C4 message byte for byte.
 /// Thread-level torture, ring edge cases and the seeded mutants are in tests/DeepEngineers.Wire.Tests; the
 /// C# ↔ Python check is tests/wire/cross_test.py.
 /// Run: dotnet build tests/WireSelfTest -c Release; tests\WireSelfTest\bin\Release\net48\WireSelfTest.exe
 /// </summary>
    internal static unsafe class Program
    {
        private const int Messages = 20000;
        private const uint MessageType = 3; // any non-pad type; this is a private test section

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 2 && args[0] == "--peer") return Peer(args[1]);
                return Parent();
            }
            catch (Exception e)
            {
                Console.WriteLine("WIRESELFTEST FAIL exception " + e);
                return 1;
            }
        }

        private static int MessageLength(int i) { return 8 + (int)(((long)i * 2654435761L) % 3000); }

        private static void Fill(byte[] buf, int i, int n)
        {
            BitConverter.GetBytes(i).CopyTo(buf, 0);
            BitConverter.GetBytes(n).CopyTo(buf, 4);
            for (int j = 8; j < n; j++) buf[j] = (byte)(i * 7 + j);
        }

        // ------------------------------------------------------------------------------------------------- parent
        private static int Parent()
        {
            string name = @"Local\DeepEngineers_xtest_selftest_" + Win32.CurrentProcessId().ToString(CultureInfo.InvariantCulture);
            CreatorResult c = SectionCreator.CreateOrAttach(new CreatorOptions { Name = name });
            Console.WriteLine("parent: " + c + " section " + name);
            if (c.Section == null) { Console.WriteLine("WIRESELFTEST FAIL creator"); return 1; }
            using (SharedSection s = c.Section)
            {
                HeaderView hv = s.Header;
                var c4 = new ByteRing(s.Base, RingIndex.C4SeParts);
                byte* c1 = Layout.RegionPtr(s.Base, RegionId.C1SeFrame);
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--peer \"" + name + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };
                Process peer = Process.Start(psi);
                var peerOut = peer.StandardOutput.ReadToEndAsync();

                var buf = new byte[4096];
                var frame = default(SeFrame);
                int received = 0;
                long bad = 0, corrupt = 0, c1Accepted = 0, c1Torn = 0, c1Busy = 0, c1Backwards = 0;
                ulong lastTick = 0;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 60)
                {
                    hv.SnHeartbeatMs = Win32.TickCount64();
                    for (;;)
                    {
                        uint type;
                        int n;
                        RingReadStatus r = c4.TryRead(out type, buf, out n);
                        if (r == RingReadStatus.Empty) break;
                        if (r != RingReadStatus.Ok) { corrupt++; c4.ResyncTail(); break; }
                        if (type != MessageType || n != MessageLength(received) || BitConverter.ToInt32(buf, 0) != received ||
                            BitConverter.ToInt32(buf, 4) != n) bad++;
                        else for (int j = 8; j < n; j++) if (buf[j] != (byte)(received * 7 + j)) { bad++; break; }
                        received++;
                    }
                    for (int k = 0; k < 64; k++)
                    {
                        uint seq;
                        SeqlockReadStatus st = Seqlock.TryRead(c1, ref frame, out seq);
                        if (st == SeqlockReadStatus.Fresh)
                        {
                            c1Accepted++;
                            if ((ulong)frame.qpcCapture != 3 * frame.tick || frame.characterId != 7 * frame.tick || frame.simTimeSec != frame.tick) c1Torn++;
                            if (frame.tick < lastTick) c1Backwards++;
                            lastTick = frame.tick;
                        }
                        else if (st == SeqlockReadStatus.Busy) c1Busy++;
                    }
                    if (peer.HasExited && RingControl.ReadHead(s.Base, RingIndex.C4SeParts) == c4.ConsumerTail) break;
                }
                if (!peer.WaitForExit(10000)) peer.Kill();
                Console.Write(peerOut.Result);
                uint sePid = hv.SePid, seGen = hv.SeGeneration;
                bool ok = peer.ExitCode == 0 && received == Messages && bad == 0 && corrupt == 0 && c1Accepted > 0 && c1Torn == 0 &&
                          c1Backwards == 0 && sePid == (uint)peer.Id && seGen == 1;
                ulong wraps = RingControl.ReadHead(s.Base, RingIndex.C4SeParts) / (ulong)RingControl.Capacity(RingIndex.C4SeParts);
                Console.WriteLine("parent: C4 " + received + "/" + Messages + " messages (" + wraps + " wraps) bad=" + bad + " corrupt=" + corrupt +
                                  "; C1 accepted=" + c1Accepted + " torn=" + c1Torn + " busy=" + c1Busy + " backwards=" + c1Backwards +
                                  " lastTick=" + lastTick + "; sePid=" + sePid + " (peer " + peer.Id + ") seGeneration=" + seGen +
                                  "; peer exit " + peer.ExitCode + "; " + sw.ElapsedMilliseconds + " ms");
                Console.WriteLine(ok ? "WIRESELFTEST PASS" : "WIRESELFTEST FAIL");
                return ok ? 0 : 1;
            }
        }

        // --------------------------------------------------------------------------------------------------- peer
        private static int Peer(string name)
        {
            SharedSection s = SharedSection.OpenExisting(name, Layout.SectionSize);
            if (s == null) { Console.WriteLine("peer: section absent"); return 2; }
            using (s)
            {
                ValidationResult v = SectionValidation.Validate(s, new ValidationOptions { CheckRegionTable = true, SelfTestPass = true });
                Console.WriteLine("peer: validation " + v);
                if (!v.Ok) return 3;
                HeaderView hv = s.Header;
                uint gen = hv.AttachSe(Win32.CurrentProcessId(), Win32.TickCount64());
                var c4 = new ByteRing(s.Base, RingIndex.C4SeParts);
                byte* c1 = Layout.RegionPtr(s.Base, RegionId.C1SeFrame);
                var buf = new byte[4096];
                int sent = 0;
                long full = 0, errors = 0;
                ulong tick = 0;
                var sw = Stopwatch.StartNew();
                while (sent < Messages && sw.Elapsed.TotalSeconds < 30)
                {
                    hv.SeHeartbeatMs = Win32.TickCount64();
                    for (int k = 0; k < 256; k++)
                    {
                        tick++;
                        var f = default(SeFrame);
                        f.tick = tick;
                        f.qpcCapture = (long)(3 * tick);
                        f.characterId = 7 * tick;
                        f.simTimeSec = tick;
                        Seqlock.Write(c1, f);
                    }
                    int n = MessageLength(sent);
                    Fill(buf, sent, n);
                    RingWriteStatus w = c4.TryWrite(MessageType, buf, 0, n);
                    if (w == RingWriteStatus.Ok) sent++;
                    else if (w == RingWriteStatus.Full) full++;
                    else errors++;
                }
                Console.WriteLine("peer: seGeneration " + gen + ", C4 sent " + sent + " (full retries " + full + ", errors " + errors +
                                  "), C1 writes " + tick);
                return sent == Messages && errors == 0 ? 0 : 1;
            }
        }
    }
}
