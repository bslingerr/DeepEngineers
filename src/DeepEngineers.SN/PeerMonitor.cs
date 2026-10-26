using System;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SN
{
    /// <summary>What the SN side currently believes about the SE peer.</summary>
    internal enum SePeerState
    {
        /// <summary>No SE has attached to this section (sePid 0).</summary>
        None = 0,
        Alive = 1,
        /// <summary>Process alive, heartbeat older than the timeout (3 s; 120 s while either side is loading).</summary>
        Stalled = 2,
        /// <summary>Process exit seen, or `sePhase = Exiting` / C5 `seExiting`. Terminal for this PID and generation.</summary>
        Exited = 3,
        /// <summary>sePid went back to 0 without an exit being seen.</summary>
        Detached = 4,
        /// <summary>
 /// The SE block was already dead when first seen (a re-initialised section keeps the SE block of an SE that
 /// exited: process not running or not ours, `sePhase = Exiting`, or a heartbeat older than the timeout). Never
 /// treated as an SE exit (no quit); becomes Alive if that process turns out to be live and beating.
 /// </summary>
        Stale = 5,
    }

    internal enum SePeerEvent
    {
        None = 0,
        /// <summary>An SE block appeared, or its PID or generation changed: re-synchronise the consumer tails.</summary>
        NewPeer = 1,
        Stalled = 2,
        /// <summary>Stalled → Alive, or Stale → Alive.</summary>
        Recovered = 3,
        /// <summary>An SE that was seen alive has exited (process exit, `sePhase = Exiting`, C5 `seExiting`).</summary>
        Exited = 4,
        Detached = 5,
    }

    /// <summary>
 /// SE peer watchdog on the Subnautica side (CONTRACT §6 restart detection, §7 lifecycle; ARCHITECTURE §5.14):
 /// <list type="bullet">
 /// <item>PID and generation read every frame from the SE header block; a change is a new peer (Wire PeerWatch).</item>
 /// <item>Process exit: an OpenProcess(SYNCHRONIZE) handle on sePid polled once per second with
 /// WaitForSingleObject(h, 0) (Wire ProcessWatch), on GetTickCount64 (unscaled). The handle also pins the PID.</item>
 /// <item>`sePhase = Exiting` (header) is an exit signal too; the bridge adds C5 `seExiting`.</item>
 /// <item>Heartbeat age = now − seHeartbeatMs (both GetTickCount64): ≥ 3 s, or ≥ 120 s while SE is in a loading phase
 /// (LoadingWorld, Saving) or Subnautica is (snPhase 1-3, Menu included), is a stall; fresh again is a recovery.
 /// Subnautica never quits on heartbeat loss. A heartbeat that merely changes never proves liveness (S0.M5c).</item>
 /// <item>Only an SE first seen alive (process handle open and not signalled, heartbeat fresh, not Exiting) can
 /// produce <see cref="SePeerEvent.Exited"/>; a dead block found in a re-initialised section is
 /// <see cref="SePeerState.Stale"/>.</item>
 /// </list>
 /// All checks are non-blocking. Logs `DE.Link UP|STALE|STALLED|RECOVERED|EXITED|DETACHED`.
 /// </summary>
    internal sealed class SePeerMonitor : IDisposable
    {
        public const ulong StallTimeoutMs = 3000;
        public const ulong StallTimeoutLoadingMs = 120000;
        public const ulong ProcessPollMs = 1000;
        private const int ErrorAccessDenied = 5;

        private readonly PeerWatch _watch = new PeerWatch();
        private ProcessWatch _process;
        private int _processOpenError;
        private ulong _nextPollMs;
        private ulong _stallSinceMs;
        private bool _staleGoneLogged;

        public SePeerState State { get; private set; }
        public uint Pid { get { return _watch.Pid; } }
        public uint Generation { get { return _watch.Generation; } }
        public bool Known { get { return _watch.Known; } }
        public ulong HeartbeatAgeMs { get; private set; }
        public uint LastSePhase { get; private set; }
        public string ExitReason { get; private set; }

        public long NewPeers { get; private set; }
        public long Stalls { get; private set; }
        public long Recoveries { get; private set; }
        public long ProcessPolls { get; private set; }

        public static ulong TimeoutFor(uint sePhase, uint snPhase)
        {
            return SePhase.IsLoading(sePhase) || SnPhase.IsLoading(snPhase) ? StallTimeoutLoadingMs : StallTimeoutMs;
        }

        /// <summary>One frame of watching. <paramref name="snPhase"/> selects the loading timeout.</summary>
        public SePeerEvent Tick(ulong nowMs, HeaderView hv, uint snPhase)
        {
            uint pid, gen;
            hv.ReadSeIdentity(out pid, out gen); // one u64 read: never a new PID with an old generation (finding 9)
            uint previousPid = _watch.Pid, previousGen = _watch.Generation;
            bool wasKnown = _watch.Known;
            PeerChange change = _watch.Observe(pid, gen);

            uint sePhase = hv.SePhase;
            ulong beat = hv.SeHeartbeatMs;
            ulong age = nowMs > beat ? nowMs - beat : 0;
            ulong timeout = TimeoutFor(sePhase, snPhase);
            LastSePhase = sePhase;
            HeartbeatAgeMs = age;

            if (change == PeerChange.NewPeer)
            {
                OpenProcessWatch(pid);
                ExitReason = null;
                _stallSinceMs = 0;
                _staleGoneLogged = false;
                _nextPollMs = nowMs + ProcessPollMs;
                NewPeers++;
                string was = wasKnown ? " (new peer; was pid=" + previousPid + " gen=" + previousGen + ")" : " (first SE on this section)";
                string stale = StaleReason(sePhase, age, timeout);
                if (stale == null)
                {
                    State = SePeerState.Alive;
                    DeLog.Info("DE.Link UP se pid=" + pid + " gen=" + gen + was + " sePhase=" + SePhase.Name(sePhase) + " hbAgeMs=" + age +
                               (_process == null ? " (no process handle: error " + _processOpenError + "; exit seen by sePhase only)" : ""));
                }
                else
                {
                    State = SePeerState.Stale;
                    DeLog.Warn("DE.Link STALE se block ignored pid=" + pid + " gen=" + gen + was + ": " + stale +
                               " (left by an SE that is gone; waiting for a live SE)");
                }
                return SePeerEvent.NewPeer;
            }
            if (change == PeerChange.Gone)
            {
                CloseProcessWatch();
                SePeerState before = State;
                State = SePeerState.Detached;
                if (before == SePeerState.Exited || before == SePeerState.Stale) return SePeerEvent.None;
                DeLog.Warn("DE.Link DETACHED se (sePid back to 0, was pid=" + previousPid + " gen=" + previousGen + ")");
                return SePeerEvent.Detached;
            }
            if (!_watch.Known || State == SePeerState.Exited || State == SePeerState.Detached) return SePeerEvent.None;

            if (State == SePeerState.Stale)
            {
                if (nowMs >= _nextPollMs)
                {
                    _nextPollMs = nowMs + ProcessPollMs;
                    ProcessPolls++;
                    if (_process != null && _process.HasExited())
                    {
                        CloseProcessWatch();
                        _processOpenError = Kernel32ErrorInvalidParameter;
                        if (!_staleGoneLogged) DeLog.Info("DE.Link stale se pid=" + pid + " process is gone (still waiting for a live SE)");
                        _staleGoneLogged = true;
                    }
                }
                if (StaleReason(sePhase, age, timeout) != null) return SePeerEvent.None;
                State = SePeerState.Alive;
                Recoveries++;
                DeLog.Info("DE.Link UP se pid=" + pid + " gen=" + gen + " (stale block came back to life) hbAgeMs=" + age);
                return SePeerEvent.Recovered;
            }

            // Alive or Stalled: an SE we saw alive.
            if (sePhase == SePhase.Exiting) return MarkExited("sePhase=Exiting");
            if (nowMs >= _nextPollMs)
            {
                _nextPollMs = nowMs + ProcessPollMs;
                ProcessPolls++;
                if (_process != null)
                {
                    if (_process.HasExited()) return MarkExited("process exit (WaitForSingleObject)");
                }
                else
                {
                    ProcessState ps = Win32.QueryProcess(pid);
                    if (ps == ProcessState.NotFound || ps == ProcessState.Exited) return MarkExited("process not found (" + ps + ")");
                }
            }
            if (State == SePeerState.Alive && age >= timeout)
            {
                State = SePeerState.Stalled;
                _stallSinceMs = nowMs;
                Stalls++;
                DeLog.Warn("DE.Link STALLED se pid=" + pid + " gen=" + gen + " hbAgeMs=" + age + " timeoutMs=" + timeout +
                           " sePhase=" + SePhase.Name(sePhase) + " (Subnautica never quits on heartbeat loss)");
                return SePeerEvent.Stalled;
            }
            if (State == SePeerState.Stalled && age < timeout)
            {
                State = SePeerState.Alive;
                Recoveries++;
                DeLog.Info("DE.Link RECOVERED se pid=" + pid + " gen=" + gen + " after " + (nowMs - _stallSinceMs) +
                           " ms stalled (waiting for SE's link epoch)");
                return SePeerEvent.Recovered;
            }
            return SePeerEvent.None;
        }

        private const int Kernel32ErrorInvalidParameter = 87;

        /// <summary>
 /// Null when the SE block looks live: not Exiting, heartbeat fresh, and a process handle that is not signalled
 /// (or, without a handle, OpenProcess refused with access denied: another integrity level, still possibly our SE).
 /// </summary>
        private string StaleReason(uint sePhase, ulong age, ulong timeout)
        {
            if (sePhase == SePhase.Exiting) return "sePhase=Exiting";
            if (age >= timeout) return "heartbeat " + age + " ms old (timeout " + timeout + " ms)";
            if (_process != null) return _process.HasExited() ? "process has exited" : null;
            return _processOpenError == ErrorAccessDenied ? null : "process not running (OpenProcess error " + _processOpenError + ")";
        }

        /// <summary>An exit signal from outside the header (C5 `seExiting`). Only an SE seen alive can exit.</summary>
        public SePeerEvent MarkExited(string reason)
        {
            if (!_watch.Known || (State != SePeerState.Alive && State != SePeerState.Stalled)) return SePeerEvent.None;
            State = SePeerState.Exited;
            ExitReason = reason;
            CloseProcessWatch();
            DeLog.Info("DE.Link EXITED se pid=" + _watch.Pid + " gen=" + _watch.Generation + " reason=" + reason);
            return SePeerEvent.Exited;
        }

        private void OpenProcessWatch(uint pid)
        {
            CloseProcessWatch();
            _process = ProcessWatch.Open(pid, out _processOpenError);
        }

        private void CloseProcessWatch()
        {
            ProcessWatch p = _process;
            _process = null;
            if (p != null) p.Dispose();
        }

        public void Dispose()
        {
            CloseProcessWatch();
        }

        public static string Name(SePeerState s)
        {
            switch (s)
            {
                case SePeerState.None: return "None";
                case SePeerState.Alive: return "Alive";
                case SePeerState.Stalled: return "Stalled";
                case SePeerState.Exited: return "Exited";
                case SePeerState.Detached: return "Detached";
                case SePeerState.Stale: return "Stale";
                default: return "Unknown";
            }
        }
    }
}
