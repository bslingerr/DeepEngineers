using System;
using System.IO;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace DeepEngineers.Wire.Tests
{
    /// <summary>Unmanaged, zeroed, 64-byte aligned memory for rings and records outside a section.</summary>
    internal sealed unsafe class NativeBuffer : IDisposable
    {
        private IntPtr _raw;
        public byte* Ptr { get; private set; }
        public long Length { get; }

        public NativeBuffer(long length)
        {
            Length = length;
            _raw = Marshal.AllocHGlobal(new IntPtr(length + 64));
            long a = (_raw.ToInt64() + 63) & ~63L;
            Ptr = (byte*)a;
            for (long i = 0; i < length; i++) Ptr[i] = 0;
        }

        public void Dispose()
        {
            if (_raw != IntPtr.Zero) { Marshal.FreeHGlobal(_raw); _raw = IntPtr.Zero; Ptr = null; }
        }
    }

    /// <summary>A byte ring over private memory: head and tail on separate 64-byte lines, then the data.</summary>
    internal sealed unsafe class TestRingMemory : IDisposable
    {
        private readonly NativeBuffer _buf;
        public ulong* Head { get { return (ulong*)_buf.Ptr; } }
        public ulong* Tail { get { return (ulong*)(_buf.Ptr + 64); } }
        public byte* Data { get { return _buf.Ptr + 128; } }
        public long Capacity { get; }

        public TestRingMemory(long capacity)
        {
            Capacity = capacity;
            _buf = new NativeBuffer(128 + capacity);
        }

        public ByteRing NewByteRing() { return new ByteRing(Head, Tail, Data, Capacity); }

        public ulong DataChecksum() { return LayoutHash.Fnv1a64(Data, Capacity); }

        public void Dispose() { _buf.Dispose(); }
    }

    internal static unsafe class TestUtil
    {
        public static string UniqueSectionName()
        {
            return @"Local\DeepEngineers_v1_test_" + Guid.NewGuid().ToString("N");
        }

        private static string _sddl;
        public static string Sddl
        {
            get { return _sddl ?? (_sddl = WireInfo.SectionSddlForCurrentUser()); }
        }

        private static uint _deadPid;
        /// <summary>A PID with no process behind it (OpenProcess fails with ERROR_INVALID_PARAMETER).</summary>
        public static uint DeadPid
        {
            get
            {
                if (_deadPid != 0) return _deadPid;
                for (uint p = 0x7FFFFFF0; p > 0x10000000; p -= 4)
                    if (Win32.QueryProcess(p) == ProcessState.NotFound) { _deadPid = p; return p; }
                throw new InvalidOperationException("no free pid found");
            }
        }

        public static CreatorOptions SnOptions(string name, uint pid)
        {
            return new CreatorOptions { Name = name, Sddl = Sddl, Pid = pid };
        }

        /// <summary>Fixture path in the output directory (Fixtures/** is copied there).</summary>
        public static string FixturePath(string fileName)
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", fileName);
        }

        public static void AssertChecksumsEqual(System.Collections.Generic.Dictionary<uint, ulong> before,
            System.Collections.Generic.Dictionary<uint, ulong> after, string what)
        {
            var diff = Resync.Diff(before, after);
            Assert.AreEqual(0, diff.Count, what + ": regions changed: " + string.Join(", ", diff.ToArray()));
        }

        /// <summary>splitmix64.</summary>
        public static ulong Mix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary>
 /// Self-checking message payloads for ring tests: [u64 seq][u64 checksum][body bytes]. The checksum covers the
 /// sequence number and the body, so a stale, duplicated, torn or misframed message is detected.
 /// </summary>
    internal static unsafe class Msg
    {
        public const int Header = 16;

        public static int Fill(byte[] buffer, ulong seq, int bodyBytes)
        {
            int n = Header + bodyBytes;
            ulong s = TestUtil.Mix(seq);
            for (int i = 0; i < bodyBytes; i++)
            {
                if ((i & 7) == 0) s = TestUtil.Mix(s + (ulong)i);
                buffer[Header + i] = (byte)(s >> ((i & 7) * 8));
            }
            fixed (byte* p = buffer)
            {
                *(ulong*)p = seq;
                *(ulong*)(p + 8) = Checksum(seq, p + Header, bodyBytes);
            }
            return n;
        }

        public static ulong Checksum(ulong seq, byte* body, int bodyBytes)
        {
            ulong h = LayoutHash.Fnv1a64Continue(LayoutHash.FnvOffsetBasis, (byte*)&seq, 8);
            return LayoutHash.Fnv1a64Continue(h, body, bodyBytes);
        }

        /// <summary>True when the payload is a well-formed test message; seq is returned.</summary>
        public static bool Check(byte* payload, int payloadBytes, out ulong seq)
        {
            seq = 0;
            if (payloadBytes < Header) return false;
            seq = *(ulong*)payload;
            ulong expected = *(ulong*)(payload + 8);
            return Checksum(seq, payload + Header, payloadBytes - Header) == expected;
        }
    }
}
