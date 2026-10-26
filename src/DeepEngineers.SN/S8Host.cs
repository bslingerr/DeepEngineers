using System;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S8: Subnautica's robustness glue around <see cref="S8Rules"/>, main thread only,
 /// never throws into the bridge (every call is guarded by its caller and inside):
 /// <list type="bullet">
 /// <item><c>s8.peer</c> row on every SE peer event {event, se, sePid, seGen, sePhase, hbAgeMs, mute, freeze, idle,
 /// minimise, quit, why, linkAck} (S8.T-L1c/e/f);</item>
 /// <item><c>s8.quit</c> row when the quit is requested and issued {step, reason, path, seExited, fallbackAfterMs};</item>
 /// <item>the Alt+Tab / ESC restore check (S8.N4): <see cref="S8Rules.AuditDelayMs"/> after every change of SE's focus,
 /// one
 /// <c>s8.restore</c> row {edge, ok, failures, frozenWanted, frozen, muted, muteWhy, stylesOk, peer} each;</item>
 /// <item>the S8.N3 memory counters (working set, private bytes, the Mono heap, collections, the machine's load and
 /// commit) appended to the per-second <c>DE.S2 sec</c> line.</item>
 /// </list>
 /// </summary>
    internal static class S8Host
    {
        public static long PeerRows, QuitRows, Audits, AuditFailures;
        private static uint _key = uint.MaxValue, _auditFrom;
        private static ulong _auditAtMs;
        private static bool _auditPending;
        private static string _lastFailure;

        // ============================================================================================== peer rows

        public static void PeerEvent(SePeerEvent e, SePeerMonitor peer, ulong snFrame, uint linkAck)
        {
            if (e == SePeerEvent.None) return;
            try
            {
                S8Rules.Reaction r = S8Rules.React(peer.State);
                JsonOut j = S5Rows.Begin("s8.peer");
                j.Str("event", EventName(e)).Str("se", SePeerMonitor.Name(peer.State)).Num("sePid", (long)peer.Pid).Num("seGen", (long)peer.Generation)
                 .Str("sePhase", SePhase.Name(peer.LastSePhase)).Num("hbAgeMs", peer.HeartbeatAgeMs).Num("snFrame", snFrame).Num("linkAck", (long)linkAck)
                 .Bool("mute", r.Mute).Bool("freeze", r.Freeze).Bool("idle", r.Idle).Bool("minimise", r.Minimise).Bool("quit", r.Quit && S8Rules.QuitOn(e))
                 .Str("why", r.Why).Str("exitReason", peer.ExitReason ?? "");
                S5Rows.End(j);
                PeerRows++;
            }
            catch (Exception ex) { DeLog.WarnOnce("s8.peerrow", "DE.S8 s8.peer row not written: " + ex.GetType().Name + ": " + ex.Message); }
        }

        public static string EventName(SePeerEvent e)
        {
            switch (e)
            {
                case SePeerEvent.NewPeer: return "NewPeer";
                case SePeerEvent.Stalled: return "Stalled";
                case SePeerEvent.Recovered: return "Recovered";
                case SePeerEvent.Exited: return "Exited";
                case SePeerEvent.Detached: return "Detached";
                default: return "None";
            }
        }

        // ============================================================================================== quit rows

        public static void QuitRow(string step, string reason, S8Rules.QuitPath path, bool seExited)
        {
            try
            {
                JsonOut j = S5Rows.Begin("s8.quit");
                j.Str("step", step).Str("reason", reason ?? "").Str("path", S8Rules.Name(path)).Bool("seExited", seExited)
                 .Num("fallbackAfterMs", S8Rules.FallbackAfterMs(seExited));
                S5Rows.End(j);
                QuitRows++;
            }
            catch (Exception) { }
        }

        // ============================================================================================ restore audit

 /// <summary>
 /// One frame: watch the edges, run the audit when due. <paramref name="s2"/> may be null (no S2: nothing to check);
 /// <paramref name="snPhase"/> outside ReadyIdle..Active (booting, loading the host save, quitting) suspends it.
 /// </summary>
        public static void Frame(ulong nowMs, SePeerState peer, bool c5Valid, ref SeState c5, S2Host s2, bool quitting, uint snPhase)
        {
            if (s2 == null) return;
            if (snPhase < SnPhase.ReadyIdle || snPhase > SnPhase.Active)
            {
                _key = uint.MaxValue;   // the first ready frame takes the state as it is; only later edges are audited
                _auditPending = false;
                return;
            }
            try
            {
                bool valid = c5Valid && peer == SePeerState.Alive;
                uint key = S8Rules.EdgeKey(valid, valid ? c5.flags : 0u, valid ? c5.overlays : 0u, peer);
                if (key != _key)
                {
                    if (_key != uint.MaxValue)
                    {
                        if (!_auditPending) _auditFrom = _key;
                        _auditPending = true;
                        _auditAtMs = nowMs + S8Rules.AuditDelayMs;
                    }
                    _key = key;
                }
                if (!_auditPending || nowMs < _auditAtMs) return;
                _auditPending = false;
                bool fg = valid && (c5.flags & SeStateFlags.SeForeground) != 0 && (c5.overlays & Overlays.Unfocused) == 0;
                var a = new S8Rules.AuditInputs
                {
                    Valid = valid,
                    SeForeground = fg,
                    Quitting = quitting,
                    BusTarget = valid ? c5.snBusTarget : 0f,
                    FreezeWanted = s2.Freeze.Wanted,
                    Frozen = s2.Freeze.Frozen,
                    Muted = s2.Audio.Muted,
                    WindowApplied = s2.Window.Enabled && s2.Window.Applied,
                    StylesOk = s2.Window.StylesOk,
                };
                string fail = S8Rules.AuditFailures(ref a);
                Audits++;
                if (fail != null) AuditFailures++;
                string edge = S8Rules.DescribeEdge(_auditFrom, key);
                JsonOut j = S5Rows.Begin("s8.restore");
                j.Str("edge", edge).Bool("ok", fail == null).Str("failures", fail ?? "").Bool("frozenWanted", a.FreezeWanted).Bool("frozen", a.Frozen)
                 .Bool("muted", a.Muted).Str("muteWhy", s2.Audio.MuteWhy ?? "").Bool("stylesOk", a.StylesOk).Bool("seForeground", fg)
                 .Num("busTarget", (double)a.BusTarget).Str("peer", SePeerMonitor.Name(peer)).Bool("quitting", quitting);
                S5Rows.End(j);
                if (fail != null && fail != _lastFailure)
                    DeLog.Warn("DE.S8 restore check after '" + edge + "': " + fail + " (S8.N4: the state must be restored each time)");
                _lastFailure = fail;
            }
            catch (Exception ex) { DeLog.WarnOnce("s8.audit", "DE.S8 restore check failed: " + ex.GetType().Name + ": " + ex.Message); }
        }

        // ================================================================================================= memory

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint cb);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        /// <summary>The S8 part of the per-second <c>DE.S2 sec</c> line: the audit counters and the memory (S8.N3).</summary>
        public static string SecondText()
        {
            long ws = 0, priv = 0, mono = 0;
            int g0 = 0, g1 = 0, g2 = 0;
            uint load = 0;
            double commit = double.NaN;
            try
            {
                ProcessMemoryCountersEx c;
                if (K32GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx))))
                {
                    ws = (long)c.WorkingSetSize.ToUInt64();
                    priv = (long)c.PrivateUsage.ToUInt64();
                }
            }
            catch (Exception) { }
            try
            {
                mono = GC.GetTotalMemory(false);
                g0 = GC.CollectionCount(0);
                g1 = GC.CollectionCount(1);
                g2 = GC.CollectionCount(2);
            }
            catch (Exception) { }
            try
            {
                var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref m))
                {
                    load = m.dwMemoryLoad;
                    commit = S8Rules.CommitPct(m.ullTotalPageFile, m.ullAvailPageFile);
                }
            }
            catch (Exception) { }
            return "s8{audits=" + Audits + " fails=" + AuditFailures + " peerRows=" + PeerRows + "} " +
                   S8Rules.MemoryText(ws, priv, mono, g0, g1, g2, load, commit);
        }
    }
}
