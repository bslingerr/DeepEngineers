using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace DeepEngineers.SE
{
    /// <summary>Stub of the game-specific VersionGate that DevChannel reads (the real one reads MyPerGameSettings).</summary>
    internal static class VersionGate
    {
        public static bool Refused;
    }
}

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// Test rig for the SE link: captures every `DE.` log line, owns a virtual tick clock (GetTickCount64 values passed
 /// to Link.Tick), and creates private sections and mutexes (never the CONTRACT names). Disposing it switches every
 /// SE mutant off.
 /// </summary>
    internal sealed unsafe class Rig : IDisposable
    {
        public readonly List<string> Logs = new List<string>();
        public readonly List<ulong> WaitTimes = new List<ulong>();
        public readonly List<string> Chat = new List<string>();
        public ulong Now = 1000000;
        public static readonly uint SelfPid = Win32.CurrentProcessId();
        private readonly TestContext _ctx;

        public Rig(TestContext ctx)
        {
            _ctx = ctx;
            Mutants.ClearAll();
            DeLog.GameSink = s =>
            {
                lock (Logs)
                {
                    Logs.Add(s);
                    if (s.StartsWith("DE.Link WAIT", StringComparison.Ordinal)) WaitTimes.Add(Now);
                }
                if (_ctx != null) _ctx.WriteLine("    | " + s);
            };
        }

        public void Dispose()
        {
            Mutants.ClearAll();
            DeLog.GameSink = null;
        }

        public int Count(string prefix) { lock (Logs) return Logs.Count(l => l.StartsWith(prefix, StringComparison.Ordinal)); }
        public bool Has(string contains) { lock (Logs) return Logs.Any(l => l.Contains(contains)); }
        public void ClearLogs() { lock (Logs) { Logs.Clear(); WaitTimes.Clear(); } }

        public static string Name() { return @"Local\DeepEngineers_v1_setest_" + Guid.NewGuid().ToString("N"); }
        public static string MutexName() { return @"Local\DeepEngineers_v1_setest_mx_" + Guid.NewGuid().ToString("N"); }

        public static SeTickInput Input(uint phase = SePhase.Menu)
        {
            return new SeTickInput { Phase = phase, SeFlags = 0, StateFlags = SeStateFlags.SeForeground, Tick = 0, WorldEpoch = 0, HelloEpoch = 0, SeHwnd = 0x1234 };
        }

        /// <summary>The Subnautica side by the Wire creator rules (what the SN plugin runs), on the virtual clock.</summary>
        public CreatorResult CreateSn(string name, uint pid, Func<uint, bool> alive = null)
        {
            var o = new CreatorOptions { Name = name, Sddl = WireInfo.SectionSddlForCurrentUser(), Pid = pid, NowMs = () => Now };
            if (alive != null) o.IsProcessAlive = alive;
            return SectionCreator.CreateOrAttach(o);
        }

        public static SeState ReadC5(byte* b)
        {
            SeState st = default(SeState);
            uint seq;
            SeqlockReadStatus r = Seqlock.TryRead(Layout.RegionPtr(b, RegionId.C5SeState), ref st, out seq);
            if (r != SeqlockReadStatus.Fresh) throw new InvalidOperationException("C5 not fresh: " + r);
            return st;
        }

        /// <summary>Ticks the link at 60 Hz (16 ms steps) for `ms`, keeping the SN heartbeat fresh when `snBeat`.</summary>
        public void Run(Link link, ref SeTickInput input, ulong ms, HeaderView? sn = null, bool snBeat = true, DevChannel dev = null)
        {
            ulong end = Now + ms;
            while (Now < end)
            {
                Now += 16;
                if (sn.HasValue && snBeat) { HeaderView h = sn.Value; h.SnHeartbeatMs = Now; }
                link.Tick(Now, ref input);
                if (dev != null) dev.Tick(Now);
            }
        }

        /// <summary>Ticks until `done` or `ms` elapse; returns the virtual time used (ms), or ulong.MaxValue if never.</summary>
        public ulong RunUntil(Link link, ref SeTickInput input, ulong ms, Func<bool> done, HeaderView? sn = null, bool snBeat = true)
        {
            ulong start = Now, end = Now + ms;
            while (Now < end)
            {
                Now += 16;
                if (sn.HasValue && snBeat) { HeaderView h = sn.Value; h.SnHeartbeatMs = Now; }
                link.Tick(Now, ref input);
                if (done()) return Now - start;
            }
            return ulong.MaxValue;
        }

        private static uint _dead;
        /// <summary>A PID with no process behind it.</summary>
        public static uint DeadPid
        {
            get
            {
                if (_dead != 0) return _dead;
                for (uint p = 0x7FFFFFF0; p > 0x10000000; p -= 4)
                    if (Win32.QueryProcess(p) == ProcessState.NotFound) { _dead = p; return p; }
                throw new InvalidOperationException("no free pid");
            }
        }

        /// <summary>A live child process standing in for Subnautica (a PID that OpenProcess can watch).</summary>
        public static Process StartLiveChild()
        {
            return Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            });
        }

        public static void Kill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { p.WaitForExit(10000); } catch (Exception) { }
        }
    }
}
