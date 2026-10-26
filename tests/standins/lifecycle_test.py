"""S0 synthetic lifecycle test (ROADMAP S0.T-L1s, S0.N11, plus stand-in runs of S0.N10, S0.M1, S0.M6) and its
seeded failures (S0.M5a, S0.M5b, S0.M5c, S0.M7), with the two Python stand-ins as subprocesses. No game is started.

 python -I tests/standins/lifecycle_test.py [--only L,P,I,M] [--keep-logs]

Every check prints `PASS`/`FAIL <id>...` with its numbers; every mutant run prints `RED (expected)` or
`GREEN (unexpected)`. Exit code 0 only when every check passes and every mutant turns its check red. Raw JSON event
logs of every process go to tests/out/standins/<timestamp>/.

Thresholds (CONTRACT §5.1, §6, §7; ROADMAP S0.T-L1s):
- Exited <= 1.0 s after the host process is killed (measured from just before TerminateProcess);
- Stalled <= 3.1 s after the host's last heartbeat write (rule: heartbeat older than 3 s; 0.1 s covers one SE tick
 and the 15.6 ms GetTickCount64 granularity);
- a stall that ends: linkEpoch + 1, then a resend request after linkAckEpoch, and the resend completes (the stall
 lasts 11 s so that CONTRACT 6's ">= 10 s" rule makes the resend deterministic; after a shorter stall the resend
 depends on whether the counters disagree, i.e. on whether the host drained the rings before SE bumped the epoch);
- with the host at Menu (snPhase 1-3 are loading phases) a 4 s gap is not a stall (120 s rule);
- restart: generation + 1, consumer tails:= heads on both sides, no producer head lower than before, the resend
 completes (C7 mirroredEntityCount = grids sent, pendingParts = 0);
- S0.N11: while C3/C4 are being filled and C8 drained: 0 misparsed, 0 stale, 0 ring-corrupt messages on both sides.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import queue
import subprocess
import sys
import threading
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_WIRE = os.path.normpath(os.path.join(_HERE, "..", "wire"))
for _p in (_WIRE, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
HOST = os.path.join(_HERE, "fake_sn_host.py")
SE = os.path.join(_HERE, "fake_se_producer.py")
EXIT_LIMIT_S = 1.0
STALL_LIMIT_S = 3.1
STALL_SECONDS = 11.0      # >= 10 s: the link recovery always resends (CONTRACT 6); a shorter stall resends only
 # when the counters disagree, which depends on whether the host drained first (a race)
MUTANT_STALL_SECONDS = 5.0

RESULTS = []      # (kind, id, ok, text)
LOG_DIR = None
_name_counter = [0]

def section_name(tag: str) -> str:
    _name_counter[0] += 1
    return "Local\\DeepEngineers_v1_lt%d_%d_%s" % (os.getpid(), _name_counter[0], tag)

def record(cid: str, ok: bool, text: str, kind: str = "check"):
    RESULTS.append((kind, cid, ok, text))
    if kind == "check":
        print("%s %s %s" % ("PASS" if ok else "FAIL", cid, text), flush=True)

# ----------------------------------------------------------------------------------------------------------------
# Child processes with JSON event streams
# ----------------------------------------------------------------------------------------------------------------

class Proc:
    def __init__(self, label: str, script: str, args, env_extra=None):
        self.label = label
        env = dict(os.environ)
        env.pop(L.BRIDGE_ENV, None)
        if env_extra:
            env.update(env_extra)
        self.cmd = [sys.executable, "-I", script] + [str(x) for x in args]
        self.events = []
        self.lock = threading.Condition()
        self.raw = open(os.path.join(LOG_DIR, "%s.jsonl" % label), "w", encoding="utf-8")
        self.p = subprocess.Popen(self.cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, text=True,
                                  encoding="utf-8", errors="replace", bufsize=1,
                                  creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.pid = self.p.pid
        self.t = threading.Thread(target=self._reader, daemon=True)
        self.t.start()

    def _reader(self):
        for line in self.p.stdout:
            self.raw.write(line)
            line = line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except ValueError:
                ev = {"ev": "raw", "line": line, "t": S.now_s()}
            with self.lock:
                self.events.append(ev)
                self.lock.notify_all()
        with self.lock:
            self.lock.notify_all()

    def wait_event(self, pred, timeout: float, after: float = None):
        """First event matching pred (a str event name or a callable), optionally with t >= after."""
        if isinstance(pred, str):
            name = pred
            pred = lambda e: e.get("ev") == name  # noqa: E731
        end = time.monotonic() + timeout
        with self.lock:
            while True:
                for e in self.events:
                    if (after is None or e.get("t", 0) >= after) and pred(e):
                        return e
                left = end - time.monotonic()
                if left <= 0 or (self.p.poll() is not None and not self.t.is_alive()):
                    for e in self.events:   # last look after the stream closed
                        if (after is None or e.get("t", 0) >= after) and pred(e):
                            return e
                    return None
                self.lock.wait(min(left, 0.05))

    def last(self, name: str):
        with self.lock:
            for e in reversed(self.events):
                if e.get("ev") == name:
                    return e
        return None

    def all(self, name: str):
        with self.lock:
            return [e for e in self.events if e.get("ev") == name]

    def kill(self) -> float:
        """TerminateProcess; returns the QPC time just before the call."""
        t = S.now_s()
        try:
            self.p.kill()
        except OSError:
            pass
        try:
            self.p.wait(10)
        except subprocess.TimeoutExpired:
            pass
        return t

    def wait_exit(self, timeout: float):
        try:
            return self.p.wait(timeout)
        except subprocess.TimeoutExpired:
            return None

    def stop(self):
        if self.p.poll() is None:
            self.kill()
        self.t.join(2)
        try:
            self.raw.close()
        except OSError:
            pass

READY = ("--ready-after", 0.5)   # snPhase 1-3 (Booting, Menu, LoadingSave) are loading phases: 120 s rule

def start_host(label, name, *extra, token=True, ready=True):
    env = {L.BRIDGE_ENV: "1"} if token else None
    return Proc(label, HOST, ["--name", name, "--fps", 60] + (list(READY) if ready else []) + list(extra), env)

def start_se(label, name, *extra):
    return Proc(label, SE, ["--name", name] + list(extra))

def open_view(name):
    return S.Section.open(name, readonly=True)

def setup_pair(tag, host_args=(), se_args=(), timeout=15.0, ready=True):
    """Host + SE up to the initial resend complete (host in ReadyIdle unless ready=False). Returns
 (name, host, se, created_event) or raises."""
    name = section_name(tag)
    host = start_host("%s-host1" % tag, name, *host_args, ready=ready)
    created = host.wait_event(lambda e: e.get("ev") in ("created", "reinitialised", "refuse", "failed", "dormant"),
                              10)
    if not created or created["ev"] not in ("created", "reinitialised"):
        raise RuntimeError("host did not create the section: %s" % (created,))
    se = start_se("%s-se1" % tag, name, *se_args)
    for ev in ("link_up", "hello_acked"):
        if not se.wait_event(ev, timeout):
            raise RuntimeError("SE never reached %s" % ev)
    if not se.wait_event(lambda e: e.get("ev") == "resend_complete" and e.get("reason") == "initial", timeout):
        raise RuntimeError("initial stream never completed")
    if ready and not host.wait_event(lambda e: e.get("ev") == "phase" and e.get("name") == "ReadyIdle", timeout):
        raise RuntimeError("host never reached ReadyIdle")
    return name, host, se, created

class HarnessHandle:
    def __init__(self, name):
        self.sec = S.Section.open(name)
        if self.sec is None:
            raise RuntimeError("section absent")
        self.h = S.Harness(self.sec)
        ok, why = self.h.attach()
        if not ok:
            raise RuntimeError("harness attach refused: %s" % why)

    def command(self, side, cmd, args=None, timeout=3.0):
        rid, st = self.h.send(side, cmd, args)
        if st != S.RW_OK:
            return None
        return self.h.wait_reply(rid, timeout)

    def close(self):
        try:
            self.h.detach()
        finally:
            self.sec.close()

def stats_of(proc, ev="stats"):
    return proc.last(ev) or {}

# ----------------------------------------------------------------------------------------------------------------
# Measurements (shared by the green runs and the mutant runs)
# ----------------------------------------------------------------------------------------------------------------

def measure_stall(host, se, harness, seconds=STALL_SECONDS):
    """Stalls the host via C11 de.stallSn; returns (detected_after_s or None, stall_begin, peer_stalled, why)."""
    t_before = S.now_s()
    rep = harness.command("sn", "de.stallSn", {"seconds": seconds})
    if not rep or not rep.get("ok"):
        return None, None, None, "de.stallSn not acknowledged: %s" % (rep,)
    sb = host.wait_event("stall_begin", 3, after=t_before)
    ps = se.wait_event("peer_stalled", min(seconds, 6.0) + 1.0, after=t_before)
    if not sb:
        return None, None, ps, "no stall_begin event"
    if not ps:
        return None, sb, None, "no PeerStalled within %.1f s" % (min(seconds, 6.0) + 1)
    return ps["t"] - sb["lastHeartbeatT"], sb, ps, ""

def check_stall(tag, host, se, harness, cid_prefix="S0.T-L1s"):
    lat, sb, ps, why = measure_stall(host, se, harness)
    ok = lat is not None and lat <= STALL_LIMIT_S
    record("%s.stalled" % cid_prefix, ok, "Stalled %s after the last heartbeat (limit %.1f s; rule 3 s)%s" % (
        ("%.3f s" % lat) if lat is not None else "never", STALL_LIMIT_S, (" - " + why) if why else ""))
    return ok, sb

def check_stall_end(host, se, sb):
    """After the stall: link epoch +1, a resend request after the link ack, and the resend completes."""
    t_end_wait = (sb["t"] if sb else S.now_s())
    le = se.wait_event("link_epoch", STALL_SECONDS + 3, after=t_end_wait)
    la = se.wait_event("link_ack", 5, after=t_end_wait) if le else None
    rr = se.wait_event(lambda e: e.get("ev") == "resend_request" and "link epoch" in e.get("reason", ""), 5,
                       after=t_end_wait) if la and la.get("decision") == "resend" else None
    rc = se.wait_event(lambda e: e.get("ev") == "resend_complete" and "link epoch" in e.get("reason", ""), 10,
                       after=t_end_wait) if rr else None
    prev = None
    ups = se.all("link_epoch")
    if le and len(ups) >= 1:
        prev = le["linkEpoch"] - 1
    hl = host.wait_event("link_ack", 3, after=t_end_wait) if le else None
    ok = bool(le and la and rr and rc and hl and hl["linkEpoch"] == le["linkEpoch"])
    record("S0.T-L1s.stall-end", ok, "linkEpoch %s -> %s after %s ms of stall; host linkAck %s; %s; %s; resend %s" % (
        prev, le and le["linkEpoch"], le and le["stallMs"], hl and hl["linkEpoch"],
        la and ("decision=%s host=%s sent=%s" % (la["decision"], la["hostGrids"], la["sentGrids"])),
        rr and rr["reason"], ("complete in %.0f ms" % rc["ms"]) if rc else "NOT complete"))
    return ok

def check_exit(host, se, cid="S0.T-L1s.exited"):
    t_kill = host.kill()
    pe = se.wait_event("peer_exited", 5, after=t_kill)
    lat = (pe["t"] - t_kill) if pe else None
    ok = lat is not None and lat <= EXIT_LIMIT_S and pe.get("reason") == "process exit"
    record(cid, ok, "Exited %s after the host process was killed (limit %.1f s)%s" % (
        ("%.3f s" % lat) if lat is not None else "never within 5 s", EXIT_LIMIT_S,
        (", reason=%s" % pe["reason"]) if pe else ""))
    return ok, t_kill

def check_restart(tag, name, se, gen_before, heads_before, host_args=(), cid_prefix="S0.T-L1s.restart",
                  label="host2", settle=3.0):
    """Starts a second host; checks generation +1, both re-syncs, monotonic heads, resend, N11 parse counters."""
    t0 = S.now_s()
    host2 = start_host("%s-%s" % (tag, label), name, *host_args)
    ri = host2.wait_event(lambda e: e.get("ev") in ("reinitialised", "created", "refuse", "failed"), 10)
    np_ = se.wait_event("new_peer", 10, after=t0)
    tr = se.wait_event("tails_resynced", 10, after=t0) if np_ else None
    rc = se.wait_event(lambda e: e.get("ev") == "resend_complete" and e.get("reason") == "new generation", 12,
                       after=t0) if np_ else None
    gen_ok = bool(ri and ri["ev"] == "reinitialised" and ri["generation"] == gen_before + 1 and np_ and
                  np_["snGeneration"] == gen_before + 1)
    sn_tails_ok = bool(ri and ri["ev"] == "reinitialised" and
                       all(ri["tails"][k] == ri["heads"][k] for k in L.SN_CONSUMES))
    se_tails_ok = bool(tr and all(tr["tails"][k] == tr["heads"][k] for k in tr["tails"]))
    heads_after = ri["heads"] if ri and ri["ev"] == "reinitialised" else {}
    lowered = [k for k in heads_before if heads_after.get(k, 0) < heads_before[k]]
    record("%s.generation" % cid_prefix, gen_ok, "host generation %s -> %s; SE new_peer generation %s" % (
        gen_before, ri and ri.get("generation"), np_ and np_["snGeneration"]))
    record("%s.tails" % cid_prefix, sn_tails_ok and se_tails_ok and not lowered,
           "host tails:=heads %s; SE tails:=heads %s; producer heads lowered: %s" % (
               sn_tails_ok, se_tails_ok, ", ".join("%s %d->%d" % (k, heads_before[k], heads_after.get(k, 0))
                                                   for k in lowered) or "none"))
    record("%s.resend" % cid_prefix, bool(rc), "resend after the new generation %s" % (
        ("complete in %.0f ms (%d grids)" % (rc["ms"], rc["grids"])) if rc else "NOT complete within 12 s"))
    # S0.N11: keep traffic running, then read both sides' parse counters
    time.sleep(settle)
    hs = host2.wait_event(lambda e: e.get("ev") == "stats", 3, after=S.now_s())
    ss = se.wait_event(lambda e: e.get("ev") == "stats", 3, after=S.now_s())
    hs, ss = hs or {}, ss or {}
    n11 = bool(hs and ss and hs.get("misparse") == 0 and hs.get("stale") == 0 and hs.get("ringCorrupt") == 0 and
               ss.get("c8Misparse") == 0 and ss.get("c8Stale") == 0 and ss.get("producerCorrupt") == 0 and
               ss.get("c8Corrupt") == 0 and rc is not None and not lowered and gen_ok)
    record("S0.N11" if cid_prefix == "S0.T-L1s.restart" else cid_prefix + ".n11", n11,
           "after restart: host c3=%s c4=%s msgs, misparse=%s stale=%s ringCorrupt=%s orphans=%s; SE c8 records=%s "
           "misparse=%s stale=%s gaps=%s, producerCorrupt=%s%s" % (
               hs.get("c3Messages"), hs.get("c4Messages"), hs.get("misparse"), hs.get("stale"),
               hs.get("ringCorrupt"), hs.get("orphans"), ss.get("c8Records"), ss.get("c8Misparse"),
               ss.get("c8Stale"), ss.get("c8Gaps"), ss.get("producerCorrupt"),
               ("; details: %s" % hs.get("details")) if hs.get("details") else ""))
    return host2, ri, n11 and gen_ok and sn_tails_ok and se_tails_ok and bool(rc) and not lowered

# ----------------------------------------------------------------------------------------------------------------
# Scenarios
# ----------------------------------------------------------------------------------------------------------------

def scenario_lifecycle():
    """Stall, exit, restart (S0.N11 under traffic), SE restart, clean quit."""
    procs = []
    harness = None
    try:
        name, host, se, created = setup_pair("L")
        procs += [host, se]
        sddl = created.get("sddl", "")
        sid = S.current_user_sid()
        want = ["(A;;CCDCLCSWRPSDRCWDWO;;;SY)", "(A;;CCDCLCSWRPSDRCWDWO;;;BA)",
                "(A;;CCDCLCSWRPSDRCWDWO;;;%s)" % sid, "(ML;;NW;;;ME)"]
        ok = sddl.startswith("D:P") and all(w in sddl for w in want) and sddl.count("(A;") == 3
        record("S0.SEC.sddl", ok, "section DACL/label as created: %s" % sddl)
        record("S0.T-L1s.link", True, "host generation %d, SE linked, hello acked, initial stream complete" %
               created["generation"])
        harness = HarnessHandle(name)
        ok_stall, sb = check_stall("L", host, se, harness)
        check_stall_end(host, se, sb)
        # exit, then restart under traffic (S0.N11)
        with open_view(name) as v:
            heads_before = v.ring_heads()
            gen_before = v.hdr("snGeneration")
        check_exit(host, se)
        host2, ri, _ = check_restart("L", name, se, gen_before, heads_before)
        procs.append(host2)
        # SE restart: the host must see a new SE generation, re-sync and get the stream again
        t0 = S.now_s()
        se_gen_before = se.wait_event("link_up", 1)["seGeneration"]
        se.kill()
        se2 = start_se("L-se2", name)
        procs.append(se2)
        ng = host2.wait_event("se_new_generation", 10, after=t0)
        ha = se2.wait_event("hello_acked", 10, after=t0)
        rc = se2.wait_event(lambda e: e.get("ev") == "resend_complete", 12, after=t0)
        tails_ok = bool(ng and all(ng["tails"][k] is not None for k in ("C3", "C4", "C11c")))
        record("S0.T-L1s.se-restart", bool(ng and ng["seGeneration"] == se_gen_before + 1 and tails_ok and ha and rc),
               "host saw SE generation %s -> %s (tails re-synced %s); new SE hello acked %s; stream %s" % (
                   se_gen_before, ng and ng["seGeneration"], ng and ng.get("tails"), bool(ha),
                   ("complete in %.0f ms" % rc["ms"]) if rc else "NOT complete"))
        time.sleep(1.5)
        hs = host2.wait_event(lambda e: e.get("ev") == "stats", 3, after=S.now_s()) or {}
        record("S0.T-L1s.se-restart.parse", hs.get("misparse") == 0 and hs.get("stale") == 0,
               "host after the SE restart: misparse=%s stale=%s orphans=%s" % (hs.get("misparse"), hs.get("stale"),
                                                                              hs.get("orphans")))
        # clean quit through C11
        t0 = S.now_s()
        rep = harness.command("sn", "de.quit", {})
        code = host2.wait_exit(5)
        q = host2.wait_event("quit", 1)
        pe = se2.wait_event("peer_exited", 3, after=t0)
        record("S0.T-L1s.quit", bool(rep and rep.get("ok") and code == 0 and pe and "Quitting" in pe["reason"] and q
                                     and q.get("seAck")),
               "de.quit reply %s; host exit code %s; SE PeerExited reason %s; host saw the SE ack: %s" % (
                   rep and rep.get("ok"), code, pe and pe["reason"], q and q.get("seAck")))
    except RuntimeError as e:
        record("S0.T-L1s.setup", False, str(e))
    finally:
        if harness:
            harness.close()
        for p in procs:
            p.stop()

def scenario_pid_reuse(se_mutant=None):
    """Two hosts that write the same snPid (PID reuse simulated with --pid-override = this test's PID, which stays
 alive). Correct detector: generation + 1 -> new peer and resend. Returns True when the restart was detected."""
    procs = []
    tag = "P" if not se_mutant else "P-" + se_mutant
    try:
        pid = os.getpid()
        name, host, se, created = setup_pair(tag, host_args=("--pid-override", pid),
                                             se_args=(("--mutant", se_mutant) if se_mutant else ()))
        procs += [host, se]
        with open_view(name) as v:
            heads_before = v.ring_heads()
            gen_before = v.hdr("snGeneration")
        host.kill()
        t0 = S.now_s()
        host2 = start_host("%s-host2" % tag, name, "--pid-override", pid)
        procs.append(host2)
        ri = host2.wait_event(lambda e: e.get("ev") in ("reinitialised", "refuse", "failed"), 10)
        np_ = se.wait_event("new_peer", 8, after=t0)
        rc = se.wait_event(lambda e: e.get("ev") == "resend_complete" and e.get("reason") == "new generation", 10,
                           after=t0) if np_ else None
        detected = bool(ri and ri["ev"] == "reinitialised" and np_ and np_["snGeneration"] == gen_before + 1 and rc)
        cid = "S0.T-L1s.pid-reuse" + ("" if not se_mutant else "[%s]" % se_mutant)
        if not se_mutant:
            record(cid, detected, "same snPid %d: host generation %s -> %s; SE new_peer %s; resend %s" % (
                pid, gen_before, ri and ri.get("generation"), np_ and np_["snGeneration"],
                "complete" if rc else "NOT complete"))
            # here SE never saw a process exit, so it kept writing C3/C4 while the new host re-initialised
            time.sleep(2.0)
            hs = host2.wait_event(lambda e: e.get("ev") == "stats", 3, after=S.now_s()) or {}
            ss = se.wait_event(lambda e: e.get("ev") == "stats", 3, after=S.now_s()) or {}
            lowered = [k for k in heads_before if ri and ri.get("heads", {}).get(k, 0) < heads_before[k]]
            record("S0.N11.concurrent", bool(hs and ss and hs.get("misparse") == 0 and hs.get("stale") == 0 and
                                             hs.get("ringCorrupt") == 0 and ss.get("c8Misparse") == 0 and
                                             ss.get("c8Stale") == 0 and ss.get("producerCorrupt") == 0 and
                                             not lowered),
                   "producer writing during the re-initialisation: host c3=%s c4=%s msgs misparse=%s stale=%s "
                   "orphans=%s; SE c8 misparse=%s stale=%s producerCorrupt=%s; heads lowered %s" % (
                       hs.get("c3Messages"), hs.get("c4Messages"), hs.get("misparse"), hs.get("stale"),
                       hs.get("orphans"), ss.get("c8Misparse"), ss.get("c8Stale"), ss.get("producerCorrupt"),
                       lowered or "none"))
        else:
            le = se.all("link_epoch")
            print("      mutant run: host generation %s -> %s; SE new_peer %s; link_epoch events %d; resend %s" % (
                gen_before, ri and ri.get("generation"), np_ and np_.get("snGeneration"), len(le),
                "complete" if rc else "none"), flush=True)
        return detected
    except RuntimeError as e:
        record("S0.T-L1s.pid-reuse.setup", False, str(e))
        return False
    finally:
        for p in procs:
            p.stop()

def scenario_identity():
    """S0.N10 (stand-in), S0.M6 and S0.M1 seeded identities, with region checksums."""
    # N10a: no bridge token -> dormant, no section
    name = section_name("I-dormant")
    p = start_host("I-dormant", name, token=False)
    code = p.wait_exit(10)
    ev = p.wait_event("dormant", 1)
    state, _err = S.section_exists(name)
    record("S0.N10.dormant", code == 2 and ev is not None and state == "absent",
           "no token: exit code %s, %s, section %s" % (code, ev and ev.get("msg"), state))
    p.stop()

    # N10b: second bridged instance refused, first session's checksums unchanged (first host frozen by de.stallSn)
    name = section_name("I-second")
    first = start_host("I-first", name)
    harness = None
    try:
        if not first.wait_event("created", 10):
            raise RuntimeError("first host did not create")
        harness = HarnessHandle(name)
        rep = harness.command("sn", "de.stallSn", {"seconds": 4})
        first.wait_event("stall_begin", 3)
        time.sleep(0.2)
        before = harness.sec.region_checksums()
        second = start_host("I-second", name)
        code = second.wait_exit(10)
        ev = second.wait_event("refuse", 1)
        after = harness.sec.region_checksums()
        diff = [k for k in before if before[k] != after.get(k)]
        record("S0.N10.second-instance", bool(rep and code == 3 and ev and "second instance" in ev["msg"] and not diff),
               "second bridged host: exit code %s, %s; region checksums changed: %s" % (
                   code, ev and ev["msg"], diff or "none"))
        second.stop()
    except RuntimeError as e:
        record("S0.N10.second-instance", False, str(e))
    finally:
        if harness:
            harness.close()
        first.stop()

    # S0.M6: a section pre-created with layout 2 or another size -> a correct host refuses and writes nothing
    for mutant, field in (("layout2", "layoutMajor"), ("size", "sectionSize")):
        name = section_name("I-%s" % mutant)
        seed = start_host("I-seed-%s" % mutant, name, "--mutant", mutant, "--passive", "--no-token-check",
                          token=False)
        try:
            if not seed.wait_event("passive", 10):
                raise RuntimeError("seed did not create")
            v = S.Section.open(name, readonly=True)
            before = v.region_checksums()
            h = start_host("I-host-%s" % mutant, name)
            code = h.wait_exit(10)
            ev = h.wait_event("refuse", 1)
            after = v.region_checksums()
            v.close()
            diff = [k for k in before if before[k] != after.get(k)]
            record("S0.M6.%s" % mutant, bool(code == 3 and ev and ev.get("field") == field and not diff),
                   "seeded %s: correct host exit code %s, %s; checksums changed: %s" % (
                       mutant, code, ev and ev.get("msg"), diff or "none"))
            h.stop()
        except RuntimeError as e:
            record("S0.M6.%s" % mutant, False, str(e))
        finally:
            seed.stop()

    # S0.M1: host publishes magic DEEQ / wireVersion 2 -> SE rejects, writes nothing
    for mutant, field in (("magic", "magic"), ("wire2", "wireVersion")):
        name = section_name("I-%s" % mutant)
        h = start_host("I-m1-%s" % mutant, name, "--mutant", mutant)
        try:
            if not h.wait_event("created", 10):
                raise RuntimeError("mutant host did not create")
            v = S.Section.open(name, readonly=True)
            before = v.se_owned_checksum()
            se = start_se("I-m1-se-%s" % mutant, name, "--run-seconds", 3, "--fast-retry")
            rj = se.wait_event("reject", 5)
            se.wait_exit(8)
            after = v.se_owned_checksum()
            se_pid = v.hdr("sePid")
            v.close()
            up = se.last("link_up")
            record("S0.M1.%s" % mutant, bool(rj and rj.get("field") == field and not up and se_pid == 0 and
                                            before == after),
                   "SE: %s; link_up %s; sePid %d; SE-owned bytes %s" % (
                       rj and rj.get("msg"), bool(up), se_pid, "unchanged" if before == after else "CHANGED"))
            se.stop()
        except RuntimeError as e:
            record("S0.M1.%s" % mutant, False, str(e))
        finally:
            h.stop()

def scenario_version_restart():
    """Review finding 1: SE holds the section; the host dies; a host of another wireVersion restarts. It must refuse
 (creator rule 2 covers the identity words of validation steps 1-5), write nothing into the SN block or the common
 words, and SE must see no new peer. A correct host started afterwards still takes the section over (gen + 1)."""
    procs = []
    try:
        name, host, se, created = setup_pair("V")
        procs += [host, se]
        with open_view(name) as v:
            gen_before = v.hdr("snGeneration")
        check_exit(host, se, cid="S0.T-L1s.version-restart.exited")
        with open_view(name) as v:
            words_before = v.read(0, 0x80)          # common words + SN block (SE writes only 0x080-0x0FF)
        t0 = S.now_s()
        bad = start_host("V-host2-wire2", name, "--mutant", "wire2")
        procs.append(bad)
        code = bad.wait_exit(10)
        rf = bad.wait_event("refuse", 1)
        np_ = se.wait_event("new_peer", 1.5, after=t0)
        with open_view(name) as v:
            words_after = v.read(0, 0x80)
            wire_after = v.hdr("wireVersion")
        ok = bool(code == 3 and rf and rf.get("field") == "wireVersion" and not np_ and words_before == words_after
                  and wire_after == L.WIRE_VERSION)
        record("S0.T-L1s.version-restart", ok,
               "host of wireVersion 2 after a host exit: exit code %s, %s; SE new_peer %s; common words + SN block %s; "
               "section wireVersion %s" % (code, rf and rf.get("msg"), "SEEN" if np_ else "none",
                                           "unchanged" if words_before == words_after else "CHANGED", wire_after))
        t1 = S.now_s()
        good = start_host("V-host3", name)
        procs.append(good)
        ri = good.wait_event(lambda e: e.get("ev") in ("reinitialised", "created", "refuse", "failed"), 10)
        np2 = se.wait_event("new_peer", 10, after=t1)
        record("S0.T-L1s.version-restart.recover", bool(ri and ri["ev"] == "reinitialised" and
                                                        ri["generation"] == gen_before + 1 and np2 and
                                                        np2["snGeneration"] == gen_before + 1),
               "then a host of the right version: %s generation %s (before %s); SE new_peer generation %s" % (
                   ri and ri["ev"], ri and ri.get("generation"), gen_before, np2 and np2["snGeneration"]))
    except RuntimeError as e:
        record("S0.T-L1s.version-restart", False, str(e))
    finally:
        for p in procs:
            p.stop()

def scenario_loading_rule():
    """Host parked at Menu (a loading phase, CONTRACT 5.4): a 4 s heartbeat gap is NOT a stall (120 s rule)."""
    procs = []
    harness = None
    try:
        name, host, se, _c = setup_pair("Menu", ready=False)
        procs += [host, se]
        harness = HarnessHandle(name)
        t0 = S.now_s()
        rep = harness.command("sn", "de.stallSn", {"seconds": 4.0})
        sb = host.wait_event("stall_begin", 3, after=t0)
        se_end = host.wait_event("stall_end", 6, after=t0)
        ps = se.wait_event("peer_stalled", 0.5, after=t0)
        phase = host.last("phase")
        record("S0.T-L1s.loading-limit", bool(rep and sb and se_end and not ps and phase and phase["name"] == "Menu"),
               "host at %s stalled %.1f s: PeerStalled %s (limit 120 s while snPhase is 1-3)" % (
                   phase and phase["name"], sb and sb["seconds"] or 0, "reported" if ps else "not reported"))
    except RuntimeError as e:
        record("S0.T-L1s.loading-limit", False, str(e))
    finally:
        if harness:
            harness.close()
        for p in procs:
            p.stop()

def mutant(mid: str, desc: str, red: bool, text: str):
    RESULTS.append(("mutant", mid, red, text))
    print("%s %s %s: %s" % ("RED (expected)" if red else "GREEN (unexpected)", mid, desc, text), flush=True)

def scenario_mutants():
    # S0.M5a: detector without WaitForSingleObject -> "Exited <= 1 s" goes red
    procs = []
    try:
        name, host, se, _c = setup_pair("M5a", se_args=("--mutant", "no-exit-poll"))
        procs += [host, se]
        t_kill = host.kill()
        pe = se.wait_event("peer_exited", 2.5, after=t_kill)
        lat = (pe["t"] - t_kill) if pe else None
        green = lat is not None and lat <= EXIT_LIMIT_S
        mutant("S0.M5a", "detector ignores process exit", not green,
               "Exited %s (limit %.1f s)" % (("after %.3f s" % lat) if lat is not None else "not seen within 2.5 s",
                                             EXIT_LIMIT_S))
    except RuntimeError as e:
        mutant("S0.M5a", "detector ignores process exit", False, "setup failed: %s" % e)
    finally:
        for p in procs:
            p.stop()

    # S0.M5b: timeout 30 s -> "Stalled <= 3 s" goes red
    procs = []
    harness = None
    try:
        name, host, se, _c = setup_pair("M5b", se_args=("--mutant", "timeout30"))
        procs += [host, se]
        harness = HarnessHandle(name)
        lat, _sb, _ps, why = measure_stall(host, se, harness, seconds=MUTANT_STALL_SECONDS)
        green = lat is not None and lat <= STALL_LIMIT_S
        mutant("S0.M5b", "stall timeout 30 s instead of 3 s", not green,
               "Stalled %s" % (("after %.3f s" % lat) if lat is not None else "not detected (%s)" % why))
    except RuntimeError as e:
        mutant("S0.M5b", "stall timeout 30 s instead of 3 s", False, "setup failed: %s" % e)
    finally:
        if harness:
            harness.close()
        for p in procs:
            p.stop()

    # S0.M5c: liveness = heartbeat changed, identity = PID only, with a reused PID -> restart detection red
    detected = scenario_pid_reuse(se_mutant="heartbeat-liveness")
    mutant("S0.M5c", "liveness on 'heartbeat changed' with a reused PID", not detected,
           "restart %s" % ("detected" if detected else "NOT detected (no new_peer with generation + 1)"))

    # S0.M7: the new host zeroes the producer heads on re-initialisation -> S0.N11 goes red
    procs = []
    try:
        name, host, se, _c = setup_pair("M7")
        procs += [host, se]
        time.sleep(1.0)
        with open_view(name) as v:
            heads_before = v.ring_heads()
            gen_before = v.hdr("snGeneration")
        host.kill()
        print("      mutant run (checks below are expected to FAIL):", flush=True)
        host2, _ri, green = check_restart("M7", name, se, gen_before, heads_before, host_args=("--mutant",
                                                                                               "zero-heads"),
                                          cid_prefix="S0.M7.run", label="host2-zero-heads")
        procs.append(host2)
        mutant("S0.M7", "creator zeroes the producer heads on re-initialisation", not green,
               "S0.N11 %s" % ("still green" if green else "red"))
    except RuntimeError as e:
        mutant("S0.M7", "creator zeroes the producer heads", False, "setup failed: %s" % e)
    finally:
        for p in procs:
            p.stop()

def main(argv=None) -> int:
    global LOG_DIR
    ap = argparse.ArgumentParser(description="S0 synthetic lifecycle test with the Python stand-ins")
    ap.add_argument("--only", default="L,P,I,M", help="scenarios: L lifecycle, P pid reuse, I identity, M mutants")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    LOG_DIR = os.path.join(REPO, "tests", "out", "standins", stamp)
    os.makedirs(LOG_DIR, exist_ok=True)
    errs = L.selftest(verbose=True)
    record("S0.N1.python", not errs, "python layout selftest (%d regions, %d records, hash %s)" % (
        len(L.REGIONS), len(L.RECORDS), L.hex64(L.layout_hash())))
    only = set(x.strip().upper() for x in a.only.split(","))
    t0 = time.monotonic()
    S.fine_timer(True)
    try:
        if "L" in only:
            print("== lifecycle (stall, exit, restart under traffic, SE restart, quit)", flush=True)
            scenario_lifecycle()
        if "P" in only:
            print("== restart with a reused PID; stall while the host is at Menu (loading phase)", flush=True)
            scenario_pid_reuse()
            scenario_loading_rule()
        if "I" in only:
            print("== identity and creator rules (N10, M6, M1, restart of another version)", flush=True)
            scenario_identity()
            scenario_version_restart()
        if "M" in only:
            print("== seeded failures (each must turn its check red)", flush=True)
            scenario_mutants()
    finally:
        S.fine_timer(False)
    checks = [r for r in RESULTS if r[0] == "check" and not r[1].startswith("S0.M7.run")]
    mutants = [r for r in RESULTS if r[0] == "mutant"]
    failed = [r for r in checks if not r[2]]
    green_mutants = [r for r in mutants if not r[2]]
    print("== summary: %d/%d checks PASS, %d/%d mutants RED; %.1f s; logs %s" % (
        len(checks) - len(failed), len(checks), len(mutants) - len(green_mutants), len(mutants),
        time.monotonic() - t0, LOG_DIR), flush=True)
    ok = not failed and not green_mutants
    print("LIFECYCLE TEST %s" % ("PASS" if ok else "FAIL"), flush=True)
    with open(os.path.join(LOG_DIR, "summary.json"), "w", encoding="utf-8") as f:
        json.dump([{"kind": k, "id": i, "ok": o, "text": t} for (k, i, o, t) in RESULTS], f, indent=1)
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
