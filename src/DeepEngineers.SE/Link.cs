using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;

namespace DeepEngineers.SE
{
    /// <summary>SE's side of the link (CONTRACT §6 "Opener").</summary>
    internal enum LinkState
    {
        /// <summary>No section mapped: "renderer offline". The section is opened once per second (every 5 s after a minute).</summary>
        Searching = 0,
        /// <summary>Section validated and attached: SE writes its block, heartbeat and C5 every tick.</summary>
        Up = 1,
        /// <summary>Another SE instance holds `Local\DeepEngineers_v1_se`: nothing is opened again in this process.</summary>
        Dormant = 2,
    }

    /// <summary>What SE concluded about Subnautica (CONTRACT §5.1 C0, §7; ARCHITECTURE §4.3 overlays).</summary>
    internal enum PeerStatus
    {
        /// <summary>A new peer generation was seen; its heartbeat has not been judged yet.</summary>
        Unknown = 0,
        Alive = 1,
        /// <summary>Heartbeat older than 3 s (120 s while either side is in a loading phase), process alive.</summary>
        Stalled = 2,
        /// <summary>Process exit, `snPhase ∈ {Quitting, None}`, or `snPid` 0.</summary>
        Exited = 3,
    }

    /// <summary>What the plugin knows about SE this tick (filled on the update thread from game state).</summary>
    internal struct SeTickInput
    {
        /// <summary>C0 sePhase (Wire.SePhase).</summary>
        public uint Phase;
        /// <summary>C0 seFlags (Wire.SeFlags: gateOpen, displayRefused).</summary>
        public uint SeFlags;
        /// <summary>C5 flags (Wire.SeStateFlags: seForeground, sePaused, seExiting).</summary>
        public uint StateFlags;
        /// <summary>`MySession.Static.GameplayFrameCounter`, 0 outside a world.</summary>
        public ulong Tick;
        public uint WorldEpoch;
        /// <summary>= worldEpoch while the hello is published, else 0.</summary>
        public uint HelloEpoch;
        /// <summary>Hello Rs (CONTRACT §6 "Hello"); 0 when no hello is published.</summary>
        public double Rs;
        public ulong SeHwnd;

        // ---- S1 additions (C5). False/zero keeps the S0 values: zone off-site, perfMode Idle, targetFps 10.
 /// <summary>The site is bound and the hello published: <see cref="Zone"/>, <see cref="PerfMode"/> and the camera fields are valid.</summary>
        public bool SiteActive;
        /// <summary>C5 zone (Wire.Zone), from the map of the camera (ZoneTracker).</summary>
        public uint Zone;
        /// <summary>C5 perfMode (Wire.PerfMode) and targetFps (S1Perf).</summary>
        public uint PerfMode;
        public uint TargetFps;
        /// <summary>C5 camSnY (diagnostic) and camVelSn (SN m/s), from the last C1 frame.</summary>
        public float CamSnY;
        public F32x3 CamVelSn;

        // ---- S2 additions (C5, S2Host). False keeps the S1 values: view state Space, approach none, display F.
 /// <summary>The S2 fields below are valid (S2 runtime on); PerfMode/TargetFps then come from the view state machine.</summary>
        public bool S2Active;
        public uint ViewState;
        public uint ApproachMode;
        public uint DisplayMode;
        /// <summary>C5 flags of S2 (Wire.SeStateFlags: seeThrough, marginsOn, zOrderOk, seMixActive, guiScreenOpen), OR'ed in.</summary>
        public uint S2Flags;
        public float SnBusTarget;
        public float CrossfadeMs;
        public I32x4 SeRect;
        public ulong CrossingTick;
    }

    /// <summary>
 /// The SE end of the shared section (CONTRACT §5, §6, §7; ROADMAP S0 "SE plugin"). Open only, never create; validate
 /// (§6 steps 1-6) before writing anything; take the SE mutex; write `sePid`, `seGeneration` (found + 1), the
 /// heartbeat (update thread, every tick), `sePhase`, `seFlags`, `seHwnd` and C5; watch the Subnautica block for a
 /// new generation (re-sync of SE's consumer tails C8, C12, C11a), a stall (link epoch on recovery) and an exit.
 /// <para>
 /// Threads: <see cref="Tick"/> and the C11 helpers run on SE's update thread only. <see cref="WritePhaseNow"/>,
 /// <see cref="Beat"/> and <see cref="OnExiting"/> may come from other threads (Harmony prefixes, loading
 /// callbacks); every write into the section happens under <see cref="_lock"/>, which also orders detaching
 /// (unmapping) against those writers, so the C5 seqlock always has a single writer at a time.
 /// </para>
 /// The only pointer code of the SE plugin lives in this file; it only hands the mapped base to Wire primitives,
 /// except the atomic compare-and-swap on the shared `devControl` word.
 /// </summary>
    internal sealed unsafe class Link
    {
        // ---------------------------------------------------------------------------------------- CONTRACT values
 /// <summary>§6 "Opener": SE retries once per second, every 5 s after the first minute.</summary>
        public const ulong OpenIntervalMs = 1000, OpenIntervalSlowMs = 5000, SlowAfterMs = 60000;
        /// <summary>ROADMAP S0.N7: at most one `DE.Link WAIT` per 10 s.</summary>
        public const ulong WaitLogIntervalMs = 10000;
        /// <summary>A REJECT with the same field and value is repeated at most once a minute.</summary>
        public const ulong RejectRepeatMs = 60000;
        /// <summary>§5.1 C0: heartbeat older than 3 s in play, 120 s while either side is in a loading phase.</summary>
        public const ulong StallTimeoutMs = 3000, StallTimeoutLoadingMs = 120000;
        /// <summary>ARCHITECTURE §5.14: process exit polled once per second (every tick only while margins are on, S2).</summary>
        public const ulong PeerProcessPollMs = 1000;
        /// <summary>Re-initialisation left with magic 0 by a dead creator: detach after this long (see Detach).</summary>
        public const ulong StuckMagicMs = 10000;
        /// <summary>ARCHITECTURE §4.2 Space state: Subnautica Idle at 10 fps; §5.4 C5 crossfadeMs = 300.</summary>
        public const uint IdleTargetFps = 10;
        public const float CrossfadeMs = 300f;

        private static readonly RingIndex[] RawConsumedRings = { RingIndex.C8SnDamage, RingIndex.C12SnStatics };
        /// <summary>S6: C8 has a consumer object (it caches its tail), so it is re-synced through it; C12 stays raw.</summary>
        private static readonly RingIndex[] RawConsumedRingsS6 = { RingIndex.C12SnStatics };
        private static readonly int DevControlOffset = (int)Marshal.OffsetOf(typeof(SectionHeader), "devControl");

        private readonly object _lock = new object();
        private readonly uint _pid;
        private readonly Func<bool> _selfTestPass;
        private readonly string _sectionName;
        private readonly string _mutexName;

        private LinkState _state = LinkState.Searching;
        private SharedSection _section;
        private byte* _base;
        private NamedMutex _mutex;

        private bool _searchStarted;
        private ulong _searchStartMs;
        private ulong _nextOpenMs;
        /// <summary>One limiter for every `DE.Link WAIT` line (searching and peer re-initialisation alike), never reset.</summary>
        private RateLimit _waitLog = new RateLimit(WaitLogIntervalMs);
        private string _lastRejectKey;
        private ulong _lastRejectLogMs;

        private uint _seGeneration;
        private readonly PeerWatch _peer = new PeerWatch();
        private ProcessWatch _peerProc;
        private ulong _nextProcPollMs;
        private PeerStatus _peerStatus;
        private string _peerExitReason;
        private ulong _stallSinceMs;
        private uint _linkEpoch;
        private bool _revalidate;
        private ulong _magicBadSinceMs;
        // S0.M5c state: last heartbeat value and when it last changed
        private bool _hbChangeKnown;
        private ulong _lastHbValue, _lastHbChangeMs;

        /// <summary>S0.M5b seeded failure: the stall timeout in play is 30 s instead of 3 s.</summary>
        public const ulong MutantStallTimeoutMs = 30000;

        /// <summary>False under the S0.M5a / S0.M5c seeded failures (no WaitForSingleObject on the peer process).</summary>
        private static bool ExitPollEnabled
        {
            get { return !Mutants.IgnoreProcessExit && !Mutants.HeartbeatLiveness; }
        }

        private ByteRing _c11aIn;   // CtlToSe: SE consumes (harness produces, devControl bit 0)
        private ByteRing _c11bOut;  // CtlFromSe: SE produces (harness consumes)
        private ByteRing _c11cOut;  // CtlToSn: SE produces while it holds devControl bit 1 (chat relay)
        private ByteRing _c11dIn;   // CtlFromSn: SE consumes while it holds bit 1 (see DevControl)
        private bool _relayHeld;
        // S3: SE produces C3 (models) and C4 (grids, parts) from the update thread (GridMirrorProducer); both continue
 // from the head found in the section (a restarted producer never zeroes a head, CONTRACT §5.2)
        private ByteRing _c3Out;
        private ByteRing _c4Out;
        // S6: SE consumes C8 (SnDamage) on the update thread (Combat.DamageConsumer); created at attach
        private EntryRing _c8In;
        // S7 (statics into SE): SE consumes C12 (SnStatics) on the update thread (S7Statics); created at attach
        private ByteRing _c12In;

        private SeqlockReader<SnStatus> _c7;
        private ulong _nextC7PollMs;
        private SnStatus _lastLoggedC7;
        private bool _c7Logged;

        private SeTickInput _last;
        private bool _hasLast;
        private ulong _writtenHwnd;
        private uint _lastLoggedHello;

        // snapshot of the last tick, for logs and commands
        public uint LastSnPid { get; private set; }
        public uint LastSnGeneration { get; private set; }
        public uint LastSnPhase { get; private set; }
        public ulong LastSnHeartbeatAgeMs { get; private set; }
        public long Attaches { get; private set; }
        public long Resyncs { get; private set; }
        public long Stalls { get; private set; }
        public long PeerExits { get; private set; }
        public long NewPeers { get; private set; }
        /// <summary>S8: how long the last PeerStalled lasted (set at its recovery; CONTRACT §6 "≥ 10 s resends").</summary>
        public ulong LastStallForMs { get; private set; }
        /// <summary>S8: link-epoch recoveries (PeerStalled → Alive with the same PID and generation).</summary>
        public long Recoveries { get; private set; }
        /// <summary>C0 snHwnd at the last tick (S2).</summary>
        public ulong LastSnHwnd { get; private set; }

        /// <summary>
 /// S2 (ARCHITECTURE §5.14): poll the peer process every tick (true while the DWM margins are on) instead of once
 /// per second. Set by the S2 runtime on the update thread.
 /// </summary>
        public bool FastExitPoll { get; set; }

        /// <summary>
 /// S2 (ARCHITECTURE §4.3 PeerExited: "snHwnd no longer a visible window"): the render thread's window watch found
 /// Subnautica's window gone while it was on screen. Update thread.
 /// </summary>
        public void ReportPeerExited(string reason)
        {
            ReportPeerExited(reason, false);
        }

        /// <param name="recoverable">
 /// Review fix (finding 3): the window still exists but was hidden for a while (Subnautica's own
 /// restyle hides it for a moment): the exit is withdrawn by <see cref="RecoverWindowExit"/> when the same window
 /// of the same peer (PID and generation) shows again. A destroyed window is not recoverable (CONTRACT §5.1 C0:
 /// PeerExited lasts until a new generation).
 /// </param>
        public void ReportPeerExited(string reason, bool recoverable)
        {
            if (_state != LinkState.Up || _peerStatus == PeerStatus.Exited) return;
            MarkExited(reason);
            _windowExitRecoverable = recoverable;
            _windowExitPid = LastSnPid;
            _windowExitGen = LastSnGeneration;
        }

        /// <summary>
 /// Withdraws a recoverable window exit (update thread): only while that exit is still the current one (no process
 /// exit, quit phase or new generation since) and the same Subnautica process is alive. The peer is judged again
 /// on this tick's heartbeat (Unknown, then Alive or Stalled); the view state machine sees a peer that was not
 /// alive and enters Approach again through its usual guards. Returns true when withdrawn.
 /// </summary>
        public bool RecoverWindowExit(string reason)
        {
            if (_state != LinkState.Up || _peerStatus != PeerStatus.Exited || !_windowExitRecoverable) return false;
            _windowExitRecoverable = false;
            if (LastSnPid != _windowExitPid || LastSnGeneration != _windowExitGen || !PeerProcessAlive(_windowExitPid))
            {
                DeLog.Info("DE.Link window exit NOT withdrawn (" + reason + "): snPid/snGen " + LastSnPid + "/" + LastSnGeneration +
                           " vs " + _windowExitPid + "/" + _windowExitGen + " at the exit, or the process is gone");
                return false;
            }
            _peerStatus = PeerStatus.Unknown;
            _peerExitReason = null;
            WindowExitRecoveries++;
            DeLog.Info("DE.Link UP again: PeerExited withdrawn (" + reason + ") snPid=" + LastSnPid + " snGen=" + LastSnGeneration +
                       " (same peer; judged again on its heartbeat)");
            return true;
        }

        private bool _windowExitRecoverable;
        private uint _windowExitPid, _windowExitGen;
        public long WindowExitRecoveries { get; private set; }

        /// <param name="selfTestPass">CONTRACT §6 validation step 6 (the SE WM-1 subset result).</param>
 /// <param name="sectionName">The section to open; the CONTRACT name unless a test isolates itself.</param>
 /// <param name="mutexName">The SE mutex; the CONTRACT name unless a test isolates itself.</param>
        public Link(Func<bool> selfTestPass, string sectionName = WireInfo.SectionName, string mutexName = WireInfo.MutexSe)
        {
            _selfTestPass = selfTestPass ?? (() => true);
            _sectionName = sectionName ?? WireInfo.SectionName;
            _mutexName = mutexName ?? WireInfo.MutexSe;
            _pid = Win32.CurrentProcessId();
        }

        public string SectionName { get { return _sectionName; } }

        public LinkState State { get { return _state; } }
        public PeerStatus Peer { get { return _peerStatus; } }
        public string PeerExitReason { get { return _peerExitReason; } }
        public uint SeGeneration { get { return _seGeneration; } }
        public uint LinkEpoch { get { return _linkEpoch; } }
        public bool RelayHeld { get { return _relayHeld; } }
        public SnStatus LastSnStatus { get { return _c7 != null ? _c7.Value : default(SnStatus); } }
        public bool HasSnStatus { get { return _c7 != null && _c7.HasValue; } }

        // ==================================================================================================== tick

 /// <summary>Update thread, once per SE tick (plugin Update).</summary>
        public void Tick(ulong nowMs, ref SeTickInput input)
        {
            switch (_state)
            {
                case LinkState.Dormant:
                    return;
                case LinkState.Searching:
                    if (!_searchStarted) { _searchStarted = true; _searchStartMs = nowMs; _nextOpenMs = nowMs; }
                    if (nowMs >= _nextOpenMs) TryOpenAndAttach(nowMs, ref input);
                    return;
                default:
                    TickUp(nowMs, ref input);
                    return;
            }
        }

        private void ScheduleNextOpen(ulong nowMs)
        {
            ulong interval = nowMs - _searchStartMs < SlowAfterMs ? OpenIntervalMs : OpenIntervalSlowMs;
            _nextOpenMs = nowMs + interval;
        }

        private ValidationOptions Options()
        {
            return new ValidationOptions { SelfTestPass = _selfTestPass() };
        }

        private void TryOpenAndAttach(ulong nowMs, ref SeTickInput input)
        {
            ScheduleNextOpen(nowMs);
            SharedSection s;
            try
            {
                s = SharedSection.OpenExisting(_sectionName, Layout.SectionSize);
            }
            catch (Exception e)
            {
                LogWait(nowMs, "open failed: " + e.GetType().Name + " " + e.Message);
                return;
            }
            if (s == null)
            {
                LogWait(nowMs, "section " + _sectionName + " absent (Subnautica not started, dormant or refused)");
                return;
            }

            ValidationResult v;
            try { v = SectionValidation.Validate(s, Options()); }
            catch (Exception e)
            {
                s.Dispose();
                LogWait(nowMs, "validation error: " + e.GetType().Name + " " + e.Message);
                return;
            }
            if (v.Outcome == ValidationOutcome.NotReady)
            {
                s.Dispose();
                LogWait(nowMs, "section not ready (magic 0: the creator is initialising)");
                return;
            }
            if (v.Outcome == ValidationOutcome.Reject)
            {
                s.Dispose(); // closing the handle lets a rejected section disappear with its creator (S0.M1 second run)
                LogReject(nowMs, v, "open");
                return;
            }

            NamedMutex m;
            int err;
            MutexAcquire mr;
            try { mr = NamedMutex.TryAcquire(_mutexName, out m, out err); }
            catch (Exception e) { m = null; err = 0; mr = MutexAcquire.Error; DeLog.Info("DE.Link mutex error " + e.GetType().Name + ": " + e.Message); }
            if (mr == MutexAcquire.HeldElsewhere || mr == MutexAcquire.Error)
            {
                s.Dispose();
                _state = LinkState.Dormant;
                DeLog.Info(mr == MutexAcquire.HeldElsewhere
                    ? "DE.Link REFUSE second instance (" + _mutexName + " is held by another SE process; this one stays dormant and writes nothing)"
                    : "DE.Link REFUSE mutex " + _mutexName + " error " + err + " (dormant; nothing written)");
                return;
            }
            if (mr == MutexAcquire.AcquiredAbandoned)
                DeLog.Info("DE.Link note: " + _mutexName + " was abandoned by a previous SE process");

            Attach(nowMs, s, m, v, ref input);
        }

        private void Attach(ulong nowMs, SharedSection s, NamedMutex m, ValidationResult v, ref SeTickInput input)
        {
            uint snPid, snGen, snPhase;
            ulong snHb;
            bool staleRelayBit = false;
            lock (_lock)
            {
                _section = s;
                _base = s.Base;
                var hv = new HeaderView(_base);
                _seGeneration = hv.AttachSe(_pid, nowMs);
                hv.SePhase = input.Phase;
                hv.SeFlags = input.SeFlags;
                hv.SeHwnd = input.SeHwnd;
                _writtenHwnd = input.SeHwnd;

                // link epoch: continue from the value a previous SE left in C5 (never reset a published counter)
                SeState prev = default(SeState);
                uint seq;
                if (Seqlock.TryRead(Layout.RegionPtr(_base, RegionId.C5SeState), ref prev, out seq) == SeqlockReadStatus.Fresh)
                    _linkEpoch = prev.linkEpoch;

                // devControl bit 1 belongs to the SE relay and only one SE holds the SE mutex: a bit 1 found now was
 // left by an SE that died while relaying, and would lock both the relay and the harness out of C11c.
                if (ClearRelayBit()) staleRelayBit = true;

                _c11aIn = new ByteRing(_base, RingIndex.C11aCtlToSe);
                _c11bOut = new ByteRing(_base, RingIndex.C11bCtlFromSe);
                _c3Out = new ByteRing(_base, RingIndex.C3SeModels);
                _c4Out = new ByteRing(_base, RingIndex.C4SeParts);
                _c8In = new EntryRing(_base, RingIndex.C8SnDamage);
                _c12In = new ByteRing(_base, RingIndex.C12SnStatics); // S7: continues from the tail found (a consumer never moves the head)
                _c7 = new SeqlockReader<SnStatus>(Layout.RegionPtr(_base, RegionId.C7SnStatus));
                _relayHeld = false;
                _c11cOut = null;
                _c11dIn = null;

                hv.ReadSnIdentity(out snPid, out snGen);
                snPhase = hv.SnPhase; snHb = hv.SnHeartbeatMs;
                WriteC5(ref input);
                _last = input; _hasLast = true;
            }
            _mutex = m;
            _state = LinkState.Up;
            Attaches++;
            _peer.Forget();
            _peerStatus = PeerStatus.Unknown;
            _peerExitReason = null;
            _revalidate = false;
            _magicBadSinceMs = 0;
            _c7Logged = false;
            _nextC7PollMs = nowMs;
            _lastRejectKey = null;

            DeLog.Info("DE.Link UP seGen=" + _seGeneration + " sePid=" + _pid + " snPid=" + snPid + " snGen=" + snGen +
                       " snPhase=" + SnPhase.Name(snPhase) + " snHeartbeatAge=" + Age(nowMs, snHb) + "ms layoutHash=" +
                       LayoutHash.Format(LayoutHash.Compiled) + " linkEpoch=" + _linkEpoch + " mutex=" + _mutexName +
                       (v.Notes.Count > 0 ? " notes: " + string.Join("; ", v.Notes.ToArray()) : ""));
            if (staleRelayBit) DeLog.Info("DE.Dev cleared a stale devControl bit 1 left by a previous SE process");
            TickUp(nowMs, ref input);
        }

        private void TickUp(ulong nowMs, ref SeTickInput input)
        {
            var hv = new HeaderView(_base);
            uint magic = hv.Magic;

            // Re-initialisation by a restarted Subnautica (creator rule 3) clears the magic first. Keep our own block
 // alive (the creator never touches it), re-validate once the magic is back.
            if (magic != WireInfo.Magic)
            {
                if (magic != 0)
                {
                    string found = "0x" + magic.ToString("x", CultureInfo.InvariantCulture);
                    DeLog.Info("DE.Link REJECT step 1 field=magic expected=0x" + WireInfo.Magic.ToString("x", CultureInfo.InvariantCulture) +
                               " found=" + found + " (while linked; display bridge off, nothing more written)");
                    Detach(nowMs, "magic changed to " + found + " (incompatible creator)");
                    return;
                }
                if (_magicBadSinceMs == 0) _magicBadSinceMs = nowMs;
                _revalidate = true;
                if (_waitLog.Allow(nowMs))
                    DeLog.Info("DE.Link WAIT peer re-initialising the section (magic 0) for " + (nowMs - _magicBadSinceMs) + " ms");
                uint pidNow, genNow;
                hv.ReadSnIdentity(out pidNow, out genNow);
                // Review finding 6: the exit check keeps running while the magic is 0, on the peer known so far, so a
 // creator that dies in the middle of a re-initialisation is reported as exited (not Alive for 10 s).
                if (_peer.Known && _peerStatus != PeerStatus.Exited && ExitPollEnabled && nowMs >= _nextProcPollMs)
                {
                    _nextProcPollMs = nowMs + PeerProcessPollMs;
                    if (!PeerProcessAlive(_peer.Pid)) MarkExited("process " + _peer.Pid + " exited (section being re-initialised, magic 0)");
                }
                if (nowMs - _magicBadSinceMs >= StuckMagicMs && !PeerProcessAlive(pidNow))
                {
                    Detach(nowMs, "re-initialisation stuck: magic 0 for " + (nowMs - _magicBadSinceMs) + " ms and snPid " + pidNow + " is not alive");
                    return;
                }
                WriteOwn(nowMs, ref input);
                return;
            }
            _magicBadSinceMs = 0;

            bool validatedThisTick = false;
            if (_revalidate)
            {
                ValidationResult v = SectionValidation.Validate(_section, Options());
                if (v.Outcome == ValidationOutcome.NotReady) { WriteOwn(nowMs, ref input); return; }
                if (v.Outcome == ValidationOutcome.Reject)
                {
                    LogReject(nowMs, v, "re-validation after a peer re-initialisation");
                    Detach(nowMs, "re-validation failed (step " + v.Step + " " + v.Field + ")");
                    return;
                }
                _revalidate = false;
                validatedThisTick = true;
                uint rp, rg;
                hv.ReadSnIdentity(out rp, out rg);
                DeLog.Info("DE.Link UP re-validated after a peer re-initialisation snPid=" + rp + " snGen=" + rg +
                           (v.Notes.Count > 0 ? " notes: " + string.Join("; ", v.Notes.ToArray()) : ""));
            }

            uint snPid, snGen;
            hv.ReadSnIdentity(out snPid, out snGen);
            uint snPhase = hv.SnPhase;
            ulong snHb = hv.SnHeartbeatMs;
            LastSnPid = snPid; LastSnGeneration = snGen; LastSnPhase = snPhase;
            LastSnHwnd = hv.SnHwnd; // S2: C0 snHwnd (the C7 copy is preferred when set)
            ulong age = Age(nowMs, snHb);
            LastSnHeartbeatAgeMs = age;

            // S0.M5c (seeded failure): identity by PID only, liveness by "heartbeat value changed".
            bool m5c = Mutants.HeartbeatLiveness;
            uint identityGen = m5c ? 0u : snGen;

            // Restart detection (§6): a changed PID or generation is a new peer. Review finding 1: the section is
 // validated again (steps 1-6) before a new generation is accepted, whether or not this side happened to
 // sample the magic at 0 during the re-initialisation.
            if (_peer.Peek(snPid, identityGen) == PeerChange.NewPeer && !validatedThisTick)
            {
                ValidationResult v = SectionValidation.Validate(_section, Options());
                if (v.Outcome == ValidationOutcome.NotReady)
                {
                    _revalidate = true; // the creator cleared the magic after our read: wait for it
                    WriteOwn(nowMs, ref input);
                    return;
                }
                if (v.Outcome == ValidationOutcome.Reject)
                {
                    LogReject(nowMs, v, "re-validation of a new Subnautica generation snPid=" + snPid + " snGen=" + snGen);
                    Detach(nowMs, "re-validation of a new Subnautica generation failed (step " + v.Step + " " + v.Field + ")");
                    return;
                }
            }
            bool hadPeer = _peer.Known;
            uint prevPid = _peer.Pid, prevGen = _peer.Generation;
            PeerChange change = _peer.Observe(snPid, identityGen);
            if (change == PeerChange.NewPeer) OnNewPeer(nowMs, snPid, snGen, snPhase, age, hadPeer, prevPid, prevGen);
            else if (change == PeerChange.Gone) MarkExited("snPid 0");

            // exit
            if (_peerStatus != PeerStatus.Exited)
            {
                if (snPhase == SnPhase.Quitting || snPhase == SnPhase.None)
                    MarkExited("snPhase " + SnPhase.Name(snPhase));
                else if (ExitPollEnabled && (FastExitPoll || nowMs >= _nextProcPollMs))
                {
                    // S2: every tick while the DWM margins are on (ARCHITECTURE §5.14), once per second otherwise
                    _nextProcPollMs = nowMs + (FastExitPoll ? 0UL : PeerProcessPollMs);
                    if (snPid != 0 && !PeerProcessAlive(snPid)) MarkExited("process " + snPid + " exited");
                }
            }

            // stall (heartbeat age), recovery with a link epoch (§6 "Link epoch")
            if (_peerStatus != PeerStatus.Exited)
            {
                ulong stallAge = age;
                if (m5c)
                {
                    if (!_hbChangeKnown || snHb != _lastHbValue) { _hbChangeKnown = true; _lastHbValue = snHb; _lastHbChangeMs = nowMs; }
                    stallAge = nowMs - _lastHbChangeMs;
                }
                bool loading = SnPhase.IsLoading(snPhase) || SePhase.IsLoading(input.Phase);
                ulong limit = loading ? StallTimeoutLoadingMs : (Mutants.StallTimeout30s ? MutantStallTimeoutMs : StallTimeoutMs);
                if (stallAge > limit)
                {
                    if (_peerStatus != PeerStatus.Stalled)
                    {
                        _peerStatus = PeerStatus.Stalled;
                        _stallSinceMs = nowMs - stallAge;
                        Stalls++;
                        DeLog.Info("DE.Link STALL snHeartbeatAge=" + age + "ms limit=" + limit + "ms snPhase=" + SnPhase.Name(snPhase) +
                                   " sePhase=" + SePhase.Name(input.Phase) + " (PeerStalled; process alive)");
                    }
                }
                else
                {
                    if (_peerStatus == PeerStatus.Stalled)
                    {
                        _linkEpoch++;
                        ResyncConsumers();
                        LastStallForMs = nowMs - _stallSinceMs;
                        Recoveries++;
                        DeLog.Info("DE.Link RECOVER linkEpoch=" + _linkEpoch + " stalledFor=" + LastStallForMs +
                                   "ms snPid=" + snPid + " snGen=" + snGen + " (consumer tails re-synced: C8, C12, C11a; S8: the stream gate " +
                                   "resends everything once Subnautica answers linkAckEpoch" + (LastStallForMs >= S8Rules.ResendAfterStallMs ? ", as CONTRACT §6 requires after >= 10 s)" : ")"));
                    }
                    _peerStatus = PeerStatus.Alive;
                }
            }

            WriteOwn(nowMs, ref input);
            PollSnStatus(nowMs);
            if (input.HelloEpoch != _lastLoggedHello)
            {
                _lastLoggedHello = input.HelloEpoch;
                if (input.HelloEpoch != 0)
                    DeLog.Info("DE.Hello SE published helloEpoch=" + input.HelloEpoch + " Rs=" + input.Rs.ToString("R", CultureInfo.InvariantCulture) +
                               " siteHash=" + LayoutHash.Format(SiteV1.SiteHash) + " siteVersion=" + SiteV1.SiteVersion);
            }
        }

        private void OnNewPeer(ulong nowMs, uint snPid, uint snGen, uint snPhase, ulong age, bool hadPeer, uint prevPid, uint prevGen)
        {
            NewPeers++;
            if (hadPeer && _peerStatus != PeerStatus.Exited)
            {
                // CONTRACT §5.1 C0 / ARCHITECTURE §5.14: a changed PID or generation is PeerExited for the previous
 // peer at once, then the new peer is judged like any other.
                PeerExits++;
                DeLog.Info("DE.Link DOWN peer exited (" + (snPid != prevPid ? "snPid changed" : "snGeneration changed") + ") snPid=" +
                           prevPid + " snGen=" + prevGen + " -> snPid=" + snPid + " snGen=" + snGen);
            }
            // Open the new watch before closing the old one: the old handle was kept after the peer exited so that the
 // dead snPid stays pinned (no PID reuse) until a new generation appears.
            ProcessWatch old = _peerProc;
            int err;
            _peerProc = snPid != 0 ? ProcessWatch.Open(snPid, out err) : null; // holding the handle pins the PID
            if (old != null) old.Dispose();
            _hbChangeKnown = false;
            _peerStatus = PeerStatus.Unknown;
            _peerExitReason = null;
            _windowExitRecoverable = false;
            _nextProcPollMs = nowMs;
            if (_c7 != null) { _c7.Reset(); _c7Logged = false; }
            ResyncConsumers();
            DeLog.Info("DE.Link PEER snPid=" + snPid + " snGen=" + snGen + " snPhase=" + SnPhase.Name(snPhase) + " snHeartbeatAge=" + age +
                       "ms (new Subnautica generation: consumer tails re-synced C8, C12, C11a" + (_relayHeld ? ", C11d" : "") + ")");
        }

        private void MarkExited(string reason)
        {
            if (_peerStatus == PeerStatus.Exited) return;
            _peerStatus = PeerStatus.Exited;
            _peerExitReason = reason;
            _windowExitRecoverable = false; // set again by ReportPeerExited(…, true) for a hidden window only
            PeerExits++;
            // The process handle is NOT closed here: while SE holds a handle on the exited
 // Subnautica, Windows cannot give its PID to another process, so a restarted Subnautica's creator rule 2
 // (OpenProcess + WaitForSingleObject) sees the recorded snPid as dead instead of refusing a live stranger.
 // It is released when a new generation appears (OnNewPeer) or on Detach.
            DeLog.Info("DE.Link DOWN peer exited (" + reason + ") snPid=" + LastSnPid + " snGen=" + LastSnGeneration +
                       "; SE keeps the section and its heartbeat, a restarted Subnautica re-initialises it with generation + 1");
        }

        /// <summary>True while this link holds a SYNCHRONIZE handle on process <paramref name="pid"/> (tests, logs).</summary>
        internal bool HoldsPeerProcessHandle(uint pid)
        {
            ProcessWatch w = _peerProc;
            return w != null && w.Pid == pid;
        }

        private bool PeerProcessAlive(uint pid)
        {
            if (pid == 0) return false;
            if (_peerProc != null && _peerProc.Pid == pid) return !_peerProc.HasExited();
            ProcessState st = Win32.QueryProcess(pid);
            return !(st == ProcessState.NotFound || st == ProcessState.Exited);
        }

        /// <summary>Consumer re-sync (§5.2): tail:= head of every ring SE consumes. Producer heads are never written.</summary>
        private void ResyncConsumers()
        {
            Resyncs++;
            if (_c11aIn != null) _c11aIn.ResyncTail();
            if (_c8In != null)
            {
                _c8In.ResyncTail(); // S6: through the consumer object (EntryRing caches its tail)
                if (_c12In != null) _c12In.ResyncTail(); // S7: through the consumer object (ByteRing caches its tail)
                else Resync.ConsumerTailsToHeads(_base, RawConsumedRingsS6); // C12: no consumer object yet
            }
            else Resync.ConsumerTailsToHeads(_base, RawConsumedRings); // C8, C12: no consumer objects in S0
            if (_relayHeld && _c11dIn != null) _c11dIn.ResyncTail();
        }

        // =================================================================================================== writes

        private void WriteOwn(ulong nowMs, ref SeTickInput input)
        {
            lock (_lock)
            {
                if (_section == null) return;
                var hv = new HeaderView(_base);
                hv.SeHeartbeatMs = nowMs;
                hv.SePhase = input.Phase;
                hv.SeFlags = input.SeFlags;
                if (input.SeHwnd != _writtenHwnd) { hv.SeHwnd = input.SeHwnd; _writtenHwnd = input.SeHwnd; }
                WriteC5(ref input);
                _last = input; _hasLast = true;
            }
        }

        /// <summary>
 /// C5 SeState. S0: phases, flags, epochs and the hello fields; view state Space. S1: zone, perfMode, targetFps,
 /// camSnY and camVelSn from the bound site (<see cref="SeTickInput.SiteActive"/>); the view state stays Space
 /// (SE's picture; the display switch is S2).
 /// </summary>
        private void WriteC5(ref SeTickInput input)
        {
            uint overlays = 0;
            if ((input.StateFlags & SeStateFlags.SePaused) != 0) overlays |= Overlays.Paused;
            if ((input.StateFlags & SeStateFlags.SeForeground) == 0) overlays |= Overlays.Unfocused;
            if (_peerStatus == PeerStatus.Stalled) overlays |= Overlays.PeerStalled;
            if (_peerStatus == PeerStatus.Exited) overlays |= Overlays.PeerExited;

            var st = new SeState();
            st.flags = input.StateFlags;
            st.tick = input.Tick;
            st.viewState = ViewState.Space;
            st.approachMode = ApproachMode.None;
            st.displayMode = DisplayMode.F;
            if (input.S2Active)
            {
                // S2 (S2Host): the view state machine, the display design and the S2 flags (CONTRACT §5.4 C5)
                st.flags |= input.S2Flags;
                st.viewState = input.ViewState;
                st.approachMode = input.ApproachMode;
                st.displayMode = input.DisplayMode;
                st.seRect = input.SeRect;
                st.crossingTick = input.CrossingTick;
            }
            st.overlays = overlays;
            st.zone = input.SiteActive ? input.Zone : Zone.OffSite;
            st.worldEpoch = input.WorldEpoch;
            st.perfMode = input.SiteActive ? input.PerfMode : PerfMode.Idle;
            st.targetFps = input.SiteActive ? input.TargetFps : IdleTargetFps;
            if (input.SiteActive)
            {
                st.camSnY = input.CamSnY;
                st.camVelSn = input.CamVelSn;
            }
            st.snBusTarget = input.S2Active ? input.SnBusTarget : 0f;
            st.crossfadeMs = input.S2Active && input.CrossfadeMs > 0f ? input.CrossfadeMs : CrossfadeMs;
            st.helloEpoch = input.HelloEpoch;
            st.linkEpoch = _linkEpoch;
            st.Rs = input.HelloEpoch != 0 ? input.Rs : 0.0;
            st.siteHash = SiteV1.SiteHash;
            st.siteVersion = (uint)SiteV1.SiteVersion;
            S16SeWire.Fill(ref st); // S16 hook: the C5 extension at 0x80-0xA9 (CONTRACT §5.4 "S16 conventions"; version 0 while S16 is inactive)
            S15SeWire.Fill(ref st); // S15 hook: the C5 block at 0xAC-0xCB, the voice counters (CONTRACT §5.4 "S15 conventions"; version 0 while S15 is inactive)
            S17SeWire.Fill(ref st); // S17 hook: the C5 catch request at 0xCC-0xEB (CONTRACT §5.4 "S17 conventions"; version 0 while S17 is inactive)
            Seqlock.Write(Layout.RegionPtr(_base, RegionId.C5SeState), st);
        }

        /// <summary>
 /// C1 SeFrame (CONTRACT §5.4 C1), seqlock-written in place (S1: no bones, so only the 0x100-byte head is part
 /// of the record). Update thread, from the <c>MySandboxGame.PrepareForDraw</c> postfix. Returns false when not
 /// linked. The <c>seq</c> continues from the value found in the section (a restarted SE never resets it).
 /// </summary>
        public bool WriteSeFrame(ref SeFrame frame)
        {
            return WriteSeFrame(ref frame, null, 0);
        }

        /// <summary>
 /// S5: C1 with the character's bones (CONTRACT §5.4 C1: <c>boneCount</c> × 48 B from 0x100, canon1 order) in the same
 /// seqlock window as the head (the reader copies 0x100 + boneCount × 48 B). <paramref name="bones"/> holds 12 floats per
 /// bone; <paramref name="boneCount"/> 0 writes the head only (the S1 behaviour). The head's <c>boneCount</c> must agree.
 /// </summary>
        public bool WriteSeFrame(ref SeFrame frame, float[] bones, int boneCount)
        {
            if (boneCount < 0 || boneCount > SeFrameLimits.MaxBones || (boneCount > 0 && (bones == null || bones.Length < 12 * boneCount)))
                throw new ArgumentOutOfRangeException("boneCount");
            lock (_lock)
            {
                if (_section == null || _state != LinkState.Up) return false;
            }
            // S3: C2 for the same tick first, then C1 (CONTRACT §5.1 C2: C2's tick must equal C1's). A Subnautica reader
 // that saw C1(N) then finds C2(N) already published
            Action<ulong, long> before = BeforeSeFrame;
            if (before != null)
            {
                try { before(frame.tick, frame.qpcCapture); }
                catch (Exception e) { DeLog.Info("DE.Error before-C1 hook " + e.GetType().Name + ": " + e.Message); }
            }
            lock (_lock)
            {
                if (_section == null || _state != LinkState.Up) return false;
                byte* rec = Layout.RegionPtr(_base, RegionId.C1SeFrame);
                if (boneCount == 0) Seqlock.Write(rec, frame);
                else
                {
                    uint odd = Seqlock.BeginWrite(rec);
                    SeFrame copy = frame;
                    byte* src = (byte*)&copy;
                    Buffer.MemoryCopy(src + 4, rec + 4, SeFrameLimits.BonesOffset - 4, SeFrameLimits.BonesOffset - 4);
                    float* dst = (float*)(rec + SeFrameLimits.BonesOffset);
                    int n = 12 * boneCount;
                    for (int i = 0; i < n; i++) dst[i] = bones[i];
                    Seqlock.EndWrite(rec, odd);
                }
                SeFramesWritten++;
            }
            return true;
        }

        /// <summary>C1 records written since the plugin started.</summary>
        public long SeFramesWritten { get; private set; }

        /// <summary>
 /// S7a: C6 SeTime (CONTRACT §5.4 C6, "C6 written by SE"), seqlock-written in place every tick by the C1 writer
 /// (<c>SunSync</c>). Update thread. False when not linked. The <c>seq</c> continues from the value in the section.
 /// </summary>
        public bool WriteSeTime(ref SeTime t)
        {
            lock (_lock)
            {
                if (_section == null || _state != LinkState.Up) return false;
                Seqlock.Write(Layout.RegionPtr(_base, RegionId.C6SeTime), t);
                S15SeWire.WriteC6(Layout.RegionPtr(_base, RegionId.C6SeTime), t.tick); // S15 hook: the sky frame and body list in C6's region space past SeTime (+0x100, +0x200; own seqlocks; never throws)
                S12SeWire.WriteC6(Layout.RegionPtr(_base, RegionId.C6SeTime), t.tick); // S12 hook: the flood and vent rows at C6 region +0x800 (own seqlock; version 0 while inactive; never throws)
                SeTimesWritten++;
            }
            return true;
        }

        /// <summary>C6 records written since the plugin started (S7a).</summary>
        public long SeTimesWritten { get; private set; }

        /// <summary>
 /// S3: called on the update thread right before a C1 record is written, with its tick and qpcCapture (the grid
 /// mirror writes C2 for the same tick there, so C2 is published before C1). Never throws into the C1 writer.
 /// </summary>
        internal Action<ulong, long> BeforeSeFrame;

        /// <summary>S8a (B2 hook; CONTRACT §5.4 "S8a conventions"): a writer of C2 bytes past S7's event window, called inside the C2 seqlock of every <c>WriteSeEntities</c> (the S8a header and the notice rows).</summary>
        internal delegate void C2ExtraWriter(byte* c2Region, ulong tick);
        /// <summary>S8a: set by <c>S8aHost.Init</c> while S8a is on; null = nothing written there (the bytes stay zero: version 0).</summary>
        internal static C2ExtraWriter C2Extra;

        /// <summary>
 /// S3: C2 SeEntities (CONTRACT §5.4 C2), seqlock table written in place: header {entityCount, tick, worldEpoch,
 /// subpartCount 0, lightCount 0} and <paramref name="count"/> entity records from 0x40. Subparts and lights are
 /// not written in S3. The <c>seq</c> continues from the value found in the section. Update thread.
 /// </summary>
        public bool WriteSeEntities(ulong tick, uint worldEpoch, SeEntity[] entities, int count)
        {
            return WriteSeEntities(tick, worldEpoch, entities, count, null, 0);
        }

        /// <summary>S5: C2 with the subpart rows (subgrids, dynamic subparts, the held tool; CONTRACT §5.4 "C2 subpart table (S5 rows)").</summary>
        public bool WriteSeEntities(ulong tick, uint worldEpoch, SeEntity[] entities, int count, SeSubpart[] subparts, int subpartCount)
        {
            if (count < 0 || count > SeEntitiesLimits.MaxEntities || (count > 0 && (entities == null || entities.Length < count)))
                throw new ArgumentOutOfRangeException("count");
            if (subpartCount < 0 || subpartCount > SeEntitiesLimits.MaxSubparts || (subpartCount > 0 && (subparts == null || subparts.Length < subpartCount)))
                throw new ArgumentOutOfRangeException("subpartCount");
            lock (_lock)
            {
                if (_section == null || _state != LinkState.Up) return false;
                byte* rec = Layout.RegionPtr(_base, RegionId.C2SeEntities);
                uint odd = Seqlock.BeginWrite(rec);
                var h = (SeEntitiesHeader*)rec;
                h->entityCount = (uint)count;
                h->tick = tick;
                h->worldEpoch = worldEpoch;
                h->subpartCount = (uint)subpartCount;
                h->lightCount = 0;
                var e = (SeEntity*)(rec + SeEntitiesLimits.EntitiesOffset);
                for (int i = 0; i < count; i++) e[i] = entities[i];
                var sp = (SeSubpart*)(rec + SeEntitiesLimits.SubpartsOffset);
                for (int i = 0; i < subpartCount; i++) sp[i] = subparts[i];
                C2ExtraWriter x8 = C2Extra;
                if (x8 != null) x8(rec, tick); // S8a: the header and rows at 0x50000, same seqlock window
                Seqlock.EndWrite(rec, odd);
                SeEntitiesWritten++;
                return true;
            }
        }

        /// <summary>
 /// S7 (B2 hook; CONTRACT §5.4 "C2 lights (S7)", "C2 effect events"): C2 as the S5 overload plus the light rows
 /// (<paramref name="lightCount"/> × 64 B at 0x38040, header <c>lightCount</c>), the effect-event window
 /// (<paramref name="eventCount"/> × 64 B at 0x40000) and the header extension in <c>pad_1c</c>, all inside the same
 /// seqlock window. The extension is written with <c>eventCount</c> = <paramref name="eventCount"/> and <c>tickLow</c>
 /// = the low 32 bits of <paramref name="tick"/> whatever <paramref name="ext"/> says (a version-0 <paramref name="ext"/>
 /// writes version 0: Subnautica then ignores the rows). Update thread.
 /// </summary>
        public bool WriteSeEntities(ulong tick, uint worldEpoch, SeEntity[] entities, int count, SeSubpart[] subparts, int subpartCount,
                                    SeLight[] lights, int lightCount, S7EventRow[] events, int eventCount, in C2Ext ext)
        {
            if (count < 0 || count > SeEntitiesLimits.MaxEntities || (count > 0 && (entities == null || entities.Length < count)))
                throw new ArgumentOutOfRangeException("count");
            if (subpartCount < 0 || subpartCount > SeEntitiesLimits.MaxSubparts || (subpartCount > 0 && (subparts == null || subparts.Length < subpartCount)))
                throw new ArgumentOutOfRangeException("subpartCount");
            if (lightCount < 0 || lightCount > SeEntitiesLimits.MaxLights || (lightCount > 0 && (lights == null || lights.Length < lightCount)))
                throw new ArgumentOutOfRangeException("lightCount");
            if (eventCount < 0 || eventCount > S7Conventions.C2Events.MaxRows || (eventCount > 0 && (events == null || events.Length < eventCount)))
                throw new ArgumentOutOfRangeException("eventCount");
            C2Ext x = ext;
            x.eventCount = (ushort)eventCount;
            x.tickLow = (uint)tick;
            lock (_lock)
            {
                if (_section == null || _state != LinkState.Up) return false;
                byte* rec = Layout.RegionPtr(_base, RegionId.C2SeEntities);
                uint odd = Seqlock.BeginWrite(rec);
                var h = (SeEntitiesHeader*)rec;
                h->entityCount = (uint)count;
                h->tick = tick;
                h->worldEpoch = worldEpoch;
                h->subpartCount = (uint)subpartCount;
                h->lightCount = (uint)lightCount;
                var e = (SeEntity*)(rec + SeEntitiesLimits.EntitiesOffset);
                for (int i = 0; i < count; i++) e[i] = entities[i];
                var sp = (SeSubpart*)(rec + SeEntitiesLimits.SubpartsOffset);
                for (int i = 0; i < subpartCount; i++) sp[i] = subparts[i];
                var l = (SeLight*)(rec + SeEntitiesLimits.LightsOffset);
                for (int i = 0; i < lightCount; i++) l[i] = lights[i];
                var ev = (S7EventRow*)(rec + S7Conventions.C2Events.Offset);
                for (int i = 0; i < eventCount; i++) ev[i] = events[i];
                C2Ext.Write(rec, x);
                C2ExtraWriter x8 = C2Extra;
                if (x8 != null) x8(rec, tick); // S8a: the header and rows at 0x50000, same seqlock window
                Seqlock.EndWrite(rec, odd);
                SeEntitiesWritten++;
                return true;
            }
        }

        /// <summary>C2 records written since the plugin started.</summary>
        public long SeEntitiesWritten { get; private set; }

        /// <summary>S3: one C3 (models) message, tried once (CONTRACT §5.1: no room → the caller keeps it queued). Update thread.</summary>
        public RingWriteStatus WriteC3(uint type, byte[] payload, int count)
        {
            ByteRing r = _c3Out;
            if (r == null || _state != LinkState.Up) return RingWriteStatus.Full;
            return r.TryWrite(type, payload, 0, count);
        }

        /// <summary>S3: one C4 (grids, parts) message, tried once. Update thread.</summary>
        public RingWriteStatus WriteC4(uint type, byte[] payload, int count)
        {
            ByteRing r = _c4Out;
            if (r == null || _state != LinkState.Up) return RingWriteStatus.Full;
            return r.TryWrite(type, payload, 0, count);
        }

        /// <summary>
 /// S6: one C8 damage record (CONTRACT §5.3 SPSC fixed-record ring, SE the consumer), false when none or not linked.
 /// Impossible counters are counted (<see cref="DamageRingCorrupt"/>) and the tail is re-synced to the head (§5.2).
 /// Update thread.
 /// </summary>
        public bool TryReadDamage(out DamageRecord r)
        {
            EntryRing ring = _c8In;
            if (ring == null || _state != LinkState.Up) { r = default(DamageRecord); return false; }
            RingReadStatus st = ring.TryRead(out r);
            if (st == RingReadStatus.Ok) return true;
            if (st == RingReadStatus.Corrupt)
            {
                DamageRingCorrupt++;
                ulong h = ring.Head, t = ring.ConsumerTail;
                ring.ResyncTail();
                DeLog.Info("DE.Link C8 counters impossible (head " + h + ", tail " + t + "): tail re-synced to the head (corrupt #" + DamageRingCorrupt + ")");
            }
            return false;
        }

        /// <summary>S6: C8 reads that found impossible counters (each re-synced the tail).</summary>
        public long DamageRingCorrupt { get; private set; }

        /// <summary>
 /// S7 (statics into SE): the next C12 message copied into <paramref name="buffer"/> and consumed (CONTRACT §5.3
 /// byte ring, SE the consumer). A message larger than the buffer (no valid BatchStatics is) is consumed and counted
 /// (<see cref="StaticsOversized"/>); impossible counters are counted (<see cref="StaticsRingCorrupt"/>) and the tail
 /// re-synced to the head (§5.2). Empty when not linked. Update thread.
 /// </summary>
        public RingReadStatus TryReadStatics(out uint type, byte[] buffer, out int payloadBytes)
        {
            type = 0; payloadBytes = 0;
            ByteRing ring = _c12In;
            if (ring == null || _state != LinkState.Up) return RingReadStatus.Empty;
            RingReadStatus st = ring.TryRead(out type, buffer, out payloadBytes);
            if (st == RingReadStatus.BufferTooSmall)
            {
                StaticsOversized++;
                byte* p;
                uint t2;
                int n2;
                if (ring.TryPeek(out t2, out p, out n2) == RingReadStatus.Ok) ring.Advance();
                DeLog.Info("DE.Link C12 message type " + type + " of " + payloadBytes + " B is larger than any valid one: consumed and dropped (#" + StaticsOversized + ")");
            }
            else if (st == RingReadStatus.Corrupt)
            {
                StaticsRingCorrupt++;
                ulong h = ring.Head, t = ring.ConsumerTail;
                ring.ResyncTail();
                DeLog.Info("DE.Link C12 counters impossible (head " + h + ", tail " + t + "): tail re-synced to the head (corrupt #" + StaticsRingCorrupt + ")");
            }
            return st;
        }

        /// <summary>S7: C12 reads that found impossible counters (each re-synced the tail).</summary>
        public long StaticsRingCorrupt { get; private set; }
        /// <summary>S7: C12 messages larger than the reader's buffer (consumed and dropped).</summary>
        public long StaticsOversized { get; private set; }

        /// <summary>S3: producer head and consumer tail of C3 / C4 (0 while not linked): the resend-complete rule and stats.</summary>
        public void RingCounters(out ulong c3Head, out ulong c3Tail, out ulong c4Head, out ulong c4Tail)
        {
            ByteRing a = _c3Out, b = _c4Out;
            c3Head = a != null ? a.ProducerHead : 0; c3Tail = a != null ? a.Tail : 0;
            c4Head = b != null ? b.ProducerHead : 0; c4Tail = b != null ? b.Tail : 0;
        }

        /// <summary>
 /// Immediate `sePhase` write from a Harmony prefix or finalizer (any thread), so the phase changes before the
 /// blocking work starts (CONTRACT §7 "World load"). The heartbeat is refreshed with it.
 /// </summary>
        public void WritePhaseNow(uint phase, ulong nowMs)
        {
            if (!Monitor.TryEnter(_lock, 100)) return;
            try
            {
                if (_section == null) return;
                var hv = new HeaderView(_base);
                hv.SePhase = phase;
                hv.SeHeartbeatMs = nowMs;
            }
            finally { Monitor.Exit(_lock); }
        }

        /// <summary>Heartbeat (and `seLoadStep`) from `MySession.LoadingStep`/`LoadingLocal*` during a synchronous world load.</summary>
        public void Beat(ulong nowMs, int loadStep)
        {
            if (!Monitor.TryEnter(_lock, 50)) return;
            try
            {
                if (_section == null) return;
                var hv = new HeaderView(_base);
                hv.SeHeartbeatMs = nowMs;
                if (loadStep >= 0) hv.SeLoadStep = (uint)loadStep;
            }
            finally { Monitor.Exit(_lock); }
        }

        /// <summary>
 /// `MySandboxGame.ExitThreadSafe` prefix (priority First, any thread): `sePhase = Exiting` and C5 `seExiting`
 /// before Pulsar's own prefix kills the process (CONTRACT §7). Nothing else: no exit cleanup.
 /// </summary>
        public void OnExiting(ulong nowMs)
        {
            if (!Monitor.TryEnter(_lock, 200)) return;
            try
            {
                if (_section == null) return;
                var hv = new HeaderView(_base);
                hv.SePhase = SePhase.Exiting;
                hv.SeHeartbeatMs = nowMs;
                SeTickInput input = _hasLast ? _last : default(SeTickInput);
                input.Phase = SePhase.Exiting;
                input.StateFlags |= SeStateFlags.SeExiting;
                WriteC5(ref input);
                _last = input;
            }
            finally { Monitor.Exit(_lock); }
        }

        // ================================================================================================== detach

 /// <summary>
 /// Unmaps the section and releases the mutex (update thread), writing nothing more. Used when a re-validation
 /// rejects the section (a restarted, incompatible creator) and when a creator died in the middle of a
 /// re-initialisation (magic left at 0): closing SE's handle lets the stale section disappear, so the next
 /// Subnautica can create a fresh one instead of refusing it for as long as SE runs.
 /// </summary>
        private void Detach(ulong nowMs, string reason)
        {
            lock (_lock)
            {
                _c11aIn = null; _c11bOut = null; _c11cOut = null; _c11dIn = null; _c7 = null;
                _c3Out = null; _c4Out = null; _c8In = null; _c12In = null;
                _relayHeld = false;
                if (_section != null) { _section.Dispose(); _section = null; }
                _base = null;
                _hasLast = false;
            }
            if (_mutex != null) { _mutex.Dispose(); _mutex = null; }
            if (_peerProc != null) { _peerProc.Dispose(); _peerProc = null; }
            _peer.Forget();
            _peerStatus = PeerStatus.Unknown;
            _state = LinkState.Searching;
            _searchStarted = true;
            _searchStartMs = nowMs;
            _nextOpenMs = nowMs + OpenIntervalMs;
            DeLog.Info("DE.Link DOWN detached: " + reason + " (renderer offline; searching again)");
        }

        /// <summary>S17 hook: one coherent copy of the C9 fish list into <paramref name="table"/> (Wire S17C9.TryRead: the table is
 /// written only on a coherent read); false while the link is down, C9 was never written or the writer kept it busy.</summary>
        public bool TryReadC9(byte[] scratch, byte[] table, out uint seq, out int count)
        {
            seq = 0;
            count = 0;
            if (_base == null || State != LinkState.Up) return false;
            return S17C9.TryRead(Layout.RegionPtr(_base, RegionId.C9SnCreatures), scratch, table, out seq, out count);
        }

        // ================================================================================================== C7 read

        private void PollSnStatus(ulong nowMs)
        {
            if (_c7 == null) return;
            // S2: C7 is read every tick (snReady, presentedSeTick, snHwnd); the status line is still at most once a second
            _c7.Poll();
            if (nowMs < _nextC7PollMs || !_c7.HasValue) return;
            _nextC7PollMs = nowMs + 1000;
            SnStatus s = _c7.Value;
            if (_c7Logged && s.snPhase == _lastLoggedC7.snPhase && s.flags == _lastLoggedC7.flags &&
                s.helloAckEpoch == _lastLoggedC7.helloAckEpoch && s.linkAckEpoch == _lastLoggedC7.linkAckEpoch &&
                s.hostKind == _lastLoggedC7.hostKind && s.capabilities == _lastLoggedC7.capabilities)
                return;
            bool helloAck = s.helloAckEpoch != 0 && s.helloAckEpoch != _lastLoggedC7.helloAckEpoch;
            _lastLoggedC7 = s;
            _c7Logged = true;
            DeLog.Info("DE.Link SN status snPhase=" + SnPhase.Name(s.snPhase) + " flags=0x" + s.flags.ToString("x", CultureInfo.InvariantCulture) +
                       " wireSelfTestPass=" + ((s.flags & SnStatusFlags.WireSelfTestPass) != 0) + " hostKind=" + s.hostKind +
                       " capabilities=0x" + s.capabilities.ToString("x", CultureInfo.InvariantCulture) +
                       " helloAckEpoch=" + s.helloAckEpoch + " linkAckEpoch=" + s.linkAckEpoch);
            if (helloAck) DeLog.Info("DE.Hello ACK helloAckEpoch=" + s.helloAckEpoch + " hostKind=" + s.hostKind + " capabilities=0x" + s.capabilities.ToString("x", CultureInfo.InvariantCulture));
        }

        // ============================================================================================ C11 (dev only)

 /// <summary>The C0 `devControl` word (0 when not linked).</summary>
        public uint DevControlWord
        {
            get { return _state == LinkState.Up && _base != null ? new HeaderView(_base).DevControl : 0u; }
        }

        /// <summary>

 /// atomic compare-and-swap, only while bit 0 (harness) is clear (CONTRACT §5.1: the two bits are exclusive).
 /// </summary>
        public bool TryAcquireRelay(out string why)
        {
            why = null;
            if (_state != LinkState.Up || _base == null) { why = "not linked"; return false; }
            if (_relayHeld) return true;
            int* word = (int*)(_base + DevControlOffset);
            for (int i = 0; i < 8; i++)
            {
                int cur = Volatile.Read(ref *word);
                if ((cur & (int)DevControl.HarnessAttached) != 0) { why = "a test harness is attached (devControl bit 0)"; return false; }
                if ((cur & (int)DevControl.SeChatRelay) != 0) { why = "devControl bit 1 is already set by another producer"; return false; }
                if (Interlocked.CompareExchange(ref *word, cur | (int)DevControl.SeChatRelay, cur) == cur)
                {
                    _c11cOut = new ByteRing(_base, RingIndex.C11cCtlToSn); // continues from the head found
                    _c11dIn = new ByteRing(_base, RingIndex.C11dCtlFromSn);
                    _c11dIn.ResyncTail(); // drop replies nobody consumed before SE became the relay
                    _relayHeld = true;
                    DeLog.Info("DE.Dev relay ON (devControl bit 1: SE produces CtlToSn and reads CtlFromSn)");
                    return true;
                }
            }
            why = "devControl contended";
            return false;
        }

        public void ReleaseRelay(string reason)
        {
            if (!_relayHeld) return;
            _relayHeld = false;
            _c11cOut = null;
            _c11dIn = null;
            if (_state != LinkState.Up || _base == null) return;
            ClearRelayBit();
            DeLog.Info("DE.Dev relay OFF (" + reason + ")");
        }

        /// <summary>Atomically clears devControl bit 1 (SE's own bit); true when it was set.</summary>
        private bool ClearRelayBit()
        {
            int* word = (int*)(_base + DevControlOffset);
            for (int i = 0; i < 8; i++)
            {
                int cur = Volatile.Read(ref *word);
                if ((cur & (int)DevControl.SeChatRelay) == 0) return false;
                if (Interlocked.CompareExchange(ref *word, cur & ~(int)DevControl.SeChatRelay, cur) == cur) return true;
            }
            return false;
        }

        public RingWriteStatus SendToSn(byte[] payload, int count)
        {
            if (!_relayHeld || _c11cOut == null) return RingWriteStatus.Corrupt;
            return _c11cOut.TryWriteOrDrop(C11Type.Json, payload, 0, count);
        }

        public RingReadStatus ReadFromSn(byte[] buffer, out uint type, out int count)
        {
            type = 0; count = 0;
            if (!_relayHeld || _c11dIn == null) return RingReadStatus.Empty;
            return ReadRing(_c11dIn, buffer, out type, out count);
        }

        public RingReadStatus ReadToSe(byte[] buffer, out uint type, out int count)
        {
            type = 0; count = 0;
            if (_c11aIn == null) return RingReadStatus.Empty;
            return ReadRing(_c11aIn, buffer, out type, out count);
        }

        public RingWriteStatus ReplyFromSe(byte[] payload, int count)
        {
            if (_c11bOut == null) return RingWriteStatus.Corrupt;
            return _c11bOut.TryWriteOrDrop(C11Type.Json, payload, 0, count);
        }

        public long DroppedReplies { get { return (_c11bOut != null ? _c11bOut.Dropped : 0) + (_c11cOut != null ? _c11cOut.Dropped : 0); } }

        private static RingReadStatus ReadRing(ByteRing ring, byte[] buffer, out uint type, out int count)
        {
            RingReadStatus st = ring.TryRead(out type, buffer, out count);
            if (st == RingReadStatus.Corrupt || st == RingReadStatus.BufferTooSmall)
                ring.ResyncTail(); // a consumer never stalls on a bad message: drop what is in flight
            return st;
        }

        // ================================================================================================= helpers

        private void LogWait(ulong nowMs, string why)
        {
            if (!_waitLog.Allow(nowMs)) return;
            ulong interval = nowMs - _searchStartMs < SlowAfterMs ? OpenIntervalMs : OpenIntervalSlowMs;
            int suppressed = _waitLog.TakeSuppressed();
            DeLog.Info("DE.Link WAIT " + why + " (renderer offline; next try in " + interval / 1000 + " s" +
                       (suppressed > 0 ? "; " + suppressed + " repeats not logged" : "") + ")");
        }

        private void LogReject(ulong nowMs, ValidationResult v, string when)
        {
            string key = v.Step + "|" + v.Field + "|" + v.Found;
            if (key == _lastRejectKey && nowMs - _lastRejectLogMs < RejectRepeatMs) return;
            _lastRejectKey = key;
            _lastRejectLogMs = nowMs;
            DeLog.Info("DE.Link REJECT step " + v.Step + " field=" + v.Field + " expected=" + v.Expected + " found=" + v.Found +
                       " (" + when + "; display bridge off, nothing written, SE plays normally)");
        }

        private static ulong Age(ulong nowMs, ulong thenMs)
        {
            return nowMs > thenMs ? nowMs - thenMs : 0;
        }
    }
}
