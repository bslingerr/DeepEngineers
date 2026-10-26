"""S0.N4 cross-language check (ROADMAP S0 oracle), plus the C#-vs-Python layout table (S0.N1 cross).

The C# half is the console tests/DeepEngineers.Wire.CrossTest (build it first:
`dotnet build tests/DeepEngineers.Wire.CrossTest -c Release`). This script drives it against the Python stand-in and
the Python section code, on private section names (`Local\\DeepEngineers_xtest_<pid>_<x>`), never the real one.

Checks:
 S0.N1.cross C# layout (struct offsets/sizes read with Marshal.OffsetOf/SizeOf, region map, ring control
 offsets, canonical text, hash) equals the hand-written Python layout, record by record
 S0.N4.py-creates fake_sn_host.py creates the section (ctypes CreateFileMappingW + SDDL, creator rules); the C#
 console opens it, validates (steps 1-6 + region table), reads magic, PIDs, generations and
 heartbeats, reads C7 by seqlock, attaches as SE; Python reads the SE block back
 S0.N4.cs-creates the C# SectionCreator creates the section; Python opens it (OpenFileMappingW), validates, reads
 the SDDL, magic, PIDs, generations, heartbeats; both exchange seqlock records (C5, C7) and ring
 traffic (C11c Python->C#, C11d and C8 C#->Python, with wraps and pad records) and check every
 byte
 S0.M1.cross.* seeded failures: the Python host publishes magic DEEQ / wireVersion 2; the C# opener must
 REJECT (naming the field and both values) and write nothing (sePid 0, SE-owned bytes unchanged)
 S0.DEV.cas  the Python harness (S.Harness, compare-and-swap) and the SE relay rule (C#
 Interlocked.CompareExchange) contend for devControl for 3 s from two processes: no side ever
 sees the other's bit while holding its own, no bit is lost, the word ends at 0
 S0.DEV.cas.mutant-rmw seeded failure: the same run with the pre-review read-then-write harness must go red

Run (repo root): python -I tests/wire/cross_test.py [--exe PATH] [--traffic N]
Exit code 0 when every check passes. Logs: tests/out/cross/<timestamp>/.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import struct
import subprocess
import sys
import threading
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE,):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

DEFAULT_EXE = os.path.join(REPO, "tests", "DeepEngineers.Wire.CrossTest", "bin", "Release", "net48",
                           "DeepEngineers.Wire.CrossTest.exe")
FAKE_HOST = os.path.join(REPO, "tests", "standins", "fake_sn_host.py")
READ_CONTROL = 0x00020000
MAGIC_DEEQ = 0x51454544
SALT_TO_SN, SALT_FROM_SN = 0x11, 0x22
MESSAGE_TYPE = 1

RESULTS = []
LOG_DIR = None

def record(cid: str, ok: bool, detail: str):
    RESULTS.append((cid, ok, detail))
    print("%s %s %s" % ("PASS" if ok else "FAIL", cid, detail), flush=True)

def log_path(name: str) -> str:
    return os.path.join(LOG_DIR, name)

# ----------------------------------------------------------------------------------------------------------------
# Synthetic traffic (same definition as the C# console)
# ----------------------------------------------------------------------------------------------------------------

def message_length(i: int) -> int:
    return 8 + (i * 7919) % 4000

def make_message(i: int, salt: int) -> bytes:
    n = message_length(i)
    body = bytes(((i * 31 + j * 7 + salt) & 0xFF) for j in range(8, n))
    return struct.pack("<II", i, n) + body

# ----------------------------------------------------------------------------------------------------------------
# Processes
# ----------------------------------------------------------------------------------------------------------------

class CsProcess:
    """The C# console with a reader thread collecting its JSON lines."""

    def __init__(self, exe: str, args, logname: str):
        self.log = open(log_path(logname), "w", encoding="utf-8")
        self.p = subprocess.Popen([exe] + list(args), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  cwd=REPO, text=True, encoding="utf-8")
        self.events = []
        self.lock = threading.Lock()
        self.t = threading.Thread(target=self._read, daemon=True)
        self.t.start()

    def _read(self):
        for line in self.p.stdout:
            self.log.write(line)
            self.log.flush()
            line = line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except ValueError:
                ev = {"event": "text", "text": line}
            with self.lock:
                self.events.append(ev)

    @property
    def pid(self) -> int:
        return self.p.pid

    def event(self, name: str):
        with self.lock:
            for ev in self.events:
                if ev.get("event") == name:
                    return ev
        return None

    def wait_event(self, name: str, timeout: float):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            ev = self.event(name)
            if ev is not None:
                return ev
            if self.p.poll() is not None:
                self.t.join(2)
                return self.event(name)
            time.sleep(0.01)
        return None

    def wait(self, timeout: float) -> int:
        try:
            rc = self.p.wait(timeout)
        except subprocess.TimeoutExpired:
            self.p.kill()
            rc = self.p.wait(10)
        self.t.join(5)
        self.log.close()
        return rc

def start_host(name: str, logname: str, extra=(), token: bool = True) -> subprocess.Popen:
    env = os.environ.copy()
    if token:
        env[L.BRIDGE_ENV] = "1"
    else:
        env.pop(L.BRIDGE_ENV, None)
    out = open(log_path(logname), "w", encoding="utf-8")
    return subprocess.Popen([sys.executable, "-I", FAKE_HOST, "--name", name, "--quiet"] + list(extra),
                            stdout=out, stderr=subprocess.STDOUT, env=env, cwd=REPO)

def stop(p: subprocess.Popen):
    if p.poll() is None:
        p.terminate()
        try:
            p.wait(10)
        except subprocess.TimeoutExpired:
            p.kill()

def wait_section(name: str, timeout: float, want_magic=None, want_phase=None):
    """Opens the section read-only once it exists and its magic (and optionally snPhase) is as wanted."""
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        sec = S.Section.open(name, readonly=True)
        if sec is not None:
            magic = sec.hdr("magic")
            ok = magic != 0 if want_magic is None else magic == want_magic
            if ok and (want_phase is None or sec.hdr("snPhase") == want_phase):
                return sec
            sec.close()
        time.sleep(0.02)
    return None

# ----------------------------------------------------------------------------------------------------------------
# S0.N1.cross
# ----------------------------------------------------------------------------------------------------------------

def check_layout(exe: str):
    out = log_path("cs-layout.json")
    cs = CsProcess(exe, ["layout", out], "cs-layout.log")
    rc = cs.wait(60)
    if rc != 0 or not os.path.exists(out):
        record("S0.N1.cross", False, "C# layout mode exited %s" % rc)
        return
    with open(out, encoding="utf-8") as f:
        cj = json.load(f)
    errs = []
    py_text = L.canonical_text()
    if cj["canonicalText"] != py_text:
        errs.append("canonical texts differ (C# %d bytes, Python %d bytes)" % (len(cj["canonicalText"]), len(py_text)))
    if int(cj["layoutHash"], 16) != L.layout_hash() or int(cj["pinned"], 16) != L.PINNED_LAYOUT_HASH_1_0:
        errs.append("hash C# %s pinned %s, Python %s" % (cj["layoutHash"], cj["pinned"], L.hex64(L.layout_hash())))
    if not cj["validate"]:
        errs.append("C# Layout.TryValidate: %s" % cj["validateError"])
    if cj["sectionSize"] != L.SECTION_SIZE:
        errs.append("sectionSize")
    cregs = {r["id"]: r for r in cj["regions"]}
    if len(cregs) != len(L.REGIONS):
        errs.append("region count C# %d Python %d" % (len(cregs), len(L.REGIONS)))
    for g in L.REGIONS_BY_ID:
        c = cregs.get(g.id)
        if c is None:
            errs.append("region 0x%02x missing in C#" % g.id)
            continue
        mine = (g.kind, g.offset, g.size, g.record_size, g.version)
        theirs = (c["kind"], c["offset"], c["size"], c["recordSize"], c["version"])
        if mine != theirs:
            errs.append("region %s: Python %s C# %s" % (g.key, mine, theirs))
    for r in L.RINGS:
        c = cj["rings"][r.index]
        if (c["regionId"], c["head"], c["tail"]) != (L.REGION[r.key].id, L.ring_head_offset(r.key), L.ring_tail_offset(r.key)):
            errs.append("ring %d %s: C# %s" % (r.index, r.key, c))
    crecs = cj["records"]
    if set(crecs) != set(L.RECORDS):
        errs.append("record names differ: only C# %s, only Python %s" % (sorted(set(crecs) - set(L.RECORDS)),
                                                                         sorted(set(L.RECORDS) - set(crecs))))
    nfields = 0
    for name, rec in sorted(L.RECORDS.items()):
        c = crecs.get(name)
        if c is None:
            continue
        if c["size"] != rec.size or c["managedSize"] != rec.size:
            errs.append("%s size: Python %d, C# Marshal.SizeOf %d sizeof %d" % (name, rec.size, c["size"], c["managedSize"]))
        mine = [[f.name, f.offset, f.size, f.type] for f in rec.fields]
        if c["fields"] != mine:
            for a, b in zip(mine, c["fields"]):
                if a != b:
                    errs.append("%s field: Python %s C# %s" % (name, a, b))
                    break
            if len(mine) != len(c["fields"]):
                errs.append("%s field count Python %d C# %d" % (name, len(mine), len(c["fields"])))
        nfields += len(mine)
    record("S0.N1.cross", not errs,
           ("C# (net48 struct offsets) == Python: %d regions, %d ring entries, %d records, %d fields; canonical text "
            "%d bytes identical; hash %s" % (len(cregs), len(cj["rings"]), len(crecs), nfields, len(py_text), cj["layoutHash"]))
           if not errs else "; ".join(errs[:8]))

# ----------------------------------------------------------------------------------------------------------------
# S0.N4.py-creates
# ----------------------------------------------------------------------------------------------------------------

def check_python_creates(exe: str, tag: str):
    name = "Local\\DeepEngineers_xtest_%d_%s" % (os.getpid(), tag)
    host = start_host(name, "host-%s.log" % tag, ["--hold-seconds", "30", "--damage-rate", "0"])
    try:
        sec = wait_section(name, 15, want_magic=L.MAGIC, want_phase=L.SN_PHASE["Menu"])
        if sec is None:
            record("S0.N4.py-creates", False, "fake_sn_host did not reach Menu within 15 s")
            return
        cs = CsProcess(exe, ["open", name, "3", "--attach-se"], "cs-open-%s.log" % tag)
        se_pid_seen, se_gen_seen, se_hb_distinct, se_max_age, last = 0, 0, 0, 0, None
        while cs.p.poll() is None:
            pid = sec.hdr("sePid")
            if pid:
                se_pid_seen, se_gen_seen = pid, sec.hdr("seGeneration")
                hb = sec.hdr("seHeartbeatMs")
                if hb != last:
                    se_hb_distinct, last = se_hb_distinct + 1, hb
                se_max_age = max(se_max_age, S.tick64() - hb)
            time.sleep(0.003)
        rc = cs.wait(10)
        v = cs.event("validation") or {}
        summ = cs.event("summary") or {}
        errs = []
        if rc != 0:
            errs.append("C# exit %s" % rc)
        if v.get("outcome") != "Ok":
            errs.append("C# validation %s" % v.get("text"))
        if v.get("magic") != "0x%08x" % L.MAGIC:
            errs.append("magic %s" % v.get("magic"))
        if v.get("snPid") != host.pid or v.get("snGeneration") != 1 or v.get("regionCount") != len(L.REGIONS):
            errs.append("C# read snPid %s (host %d) snGeneration %s regionCount %s" % (
                v.get("snPid"), host.pid, v.get("snGeneration"), v.get("regionCount")))
        if int(v.get("layoutHash", "0"), 16) != L.layout_hash():
            errs.append("C# read layoutHash %s" % v.get("layoutHash"))
        if not (summ.get("snHeartbeatDistinct", 0) >= 50 and summ.get("snHeartbeatMaxAgeMs", 1e9) < 100
                and summ.get("snIdentityStable")):
            errs.append("SN heartbeat as read by C#: distinct %s max age %s ms stable %s" % (
                summ.get("snHeartbeatDistinct"), summ.get("snHeartbeatMaxAgeMs"), summ.get("snIdentityStable")))
        if not (summ.get("c7Fresh", 0) > 0 and summ.get("c7FrameBackwards", 1) == 0
                and summ.get("c7LastSnPhase") == L.SN_PHASE["Menu"]):
            errs.append("C7 by C# seqlock: fresh %s backwards %s phase %s" % (
                summ.get("c7Fresh"), summ.get("c7FrameBackwards"), summ.get("c7LastSnPhase")))
        if se_pid_seen != cs.pid or se_gen_seen != 1 or se_hb_distinct < 50 or se_max_age >= 100:
            errs.append("SE block as read by Python: sePid %d (C# %d) seGeneration %d heartbeat distinct %d max age %d ms" % (
                se_pid_seen, cs.pid, se_gen_seen, se_hb_distinct, se_max_age))
        record("S0.N4.py-creates", not errs,
               ("Python host pid %d gen 1 -> C# read magic %s snPid %s snGeneration %s, SN heartbeat %s distinct "
                "values in 3 s, max age %s ms, C7 %s fresh seqlock reads (last snFrame %s, phase Menu); C# attached as SE "
                "pid %d -> Python read sePid %d seGeneration %d, SE heartbeat %d distinct, max age %d ms" % (
                    host.pid, v.get("magic"), v.get("snPid"), v.get("snGeneration"), summ.get("snHeartbeatDistinct"),
                    summ.get("snHeartbeatMaxAgeMs"), summ.get("c7Fresh"), summ.get("c7LastSnFrame"), cs.pid,
                    se_pid_seen, se_gen_seen, se_hb_distinct, se_max_age))
               if not errs else "; ".join(errs))
        sec.close()
    finally:
        stop(host)

# ----------------------------------------------------------------------------------------------------------------
# S0.N4.cs-creates
# ----------------------------------------------------------------------------------------------------------------

def check_cs_creates(exe: str, tag: str, traffic: int):
    name = "Local\\DeepEngineers_xtest_%d_%s" % (os.getpid(), tag)
    # precomputed traffic: the Python loop must stay fast enough to keep its SE heartbeat < 100 ms old
    out_msgs = [make_message(i, SALT_TO_SN) for i in range(traffic)]
    in_msgs = [make_message(i, SALT_FROM_SN) for i in range(traffic)]
    in_recs = [bytes(((i * 13 + j) & 0xFF) for j in range(8, 128)) for i in range(traffic * 4)]
    cs = CsProcess(exe, ["create", name, "45", "--traffic", str(traffic)], "cs-create-%s.log" % tag)
    created = cs.wait_event("created", 15)
    if not created or created.get("outcome") != "Created":
        record("S0.N4.cs-creates", False, "C# creator: %s" % created)
        cs.wait(5)
        return
    sec = None
    end = time.monotonic() + 10
    while sec is None and time.monotonic() < end:
        sec = S.Section.open(name)
        if sec is None:
            time.sleep(0.01)
    if sec is None:
        record("S0.N4.cs-creates", False, "Python could not open the C#-created section")
        cs.wait(5)
        return
    errs = []
    try:
        # identity and validation, read by Python
        v = S.validate(sec)
        if v["outcome"] != "ok":
            errs.append("Python validation: %s" % S.describe_validation(v))
        table = sec.region_table()
        want = [dict(id=g.id, kind=g.kind, offset=g.offset, size=g.size, recordSize=g.record_size, version=g.version)
                for g in L.REGIONS_BY_ID]
        if table != want:
            errs.append("region table written by C# differs from the Python layout")
        h = S._OpenFileMappingW(READ_CONTROL, False, S.Section.full_name(name))
        sddl = ""
        if h:
            try:
                sddl = S.handle_sddl(h)
            finally:
                S._CloseHandle(h)
        sid = S.current_user_sid()
        sddl_ok = (sddl.startswith("D:P") and sddl.count("(A;") == 3 and ";;;SY)" in sddl and ";;;BA)" in sddl
                   and (";;;%s)" % sid) in sddl and "(ML;;NW;;;ME)" in sddl)
        if not sddl_ok:
            errs.append("SDDL of the C#-created section: %r" % sddl)
        magic, sn_pid, sn_gen = sec.hdr("magic"), sec.hdr("snPid"), sec.hdr("snGeneration")
        if magic != L.MAGIC or sn_pid != cs.pid or sn_gen != 1 or created.get("generation") != 1:
            errs.append("Python read magic 0x%08x snPid %d (C# %d) snGeneration %d" % (magic, sn_pid, cs.pid, sn_gen))

        # attach as SE
        me = S.current_pid()
        se_gen = (sec.hdr("seGeneration") + 1) & 0xFFFFFFFF or 1
        sec.set_hdr("seHeartbeatMs", S.tick64())
        sec.set_identity("se", me, se_gen)      # heartbeat first, then PID + generation as one u64 (finding 9)
        sec.set_hdr("sePhase", L.SE_PHASE["Menu"])

        c11c = S.ByteRing(sec, "C11c")
        c11d = S.ByteRing(sec, "C11d")
        c8 = S.EntryRing(sec, "C8")
        c5_off, c7_off = L.REGION["C5"].offset, L.REGION["C7"].offset
        sent_c, recv_d, bad_d, recv_c8, bad_c8, corrupt, full_c = 0, 0, 0, 0, 0, 0, 0
        k = 0
        c7_fresh = c7_busy = c7_torn = c7_back = 0
        c7_last = 0
        sn_hb_distinct, sn_max_age, sn_last = 0, 0, None
        pad_seen = 0
        c7_distinct, c7_last_seq = 0, 0
        tight = False      # after the ring traffic: seqlock torture across languages (no sleep)
        t_end = time.monotonic() + 50
        while time.monotonic() < t_end:
            now = S.tick64()
            sec.set_hdr("seHeartbeatMs", now)
            hb = sec.hdr("snHeartbeatMs")
            if hb != sn_last:
                sn_hb_distinct, sn_last = sn_hb_distinct + 1, hb
            sn_max_age = max(sn_max_age, now - hb)
            for _ in range(40 if tight else 5):
                k += 1
                S.seqlock_write(sec, c5_off, L.SE_STATE.pack(tick=k, crossingTick=3 * k, siteHash=7 * k))
            budget = 16
            while sent_c < traffic and budget > 0:
                budget -= 1
                before = c11c.producer_head
                st = c11c.try_write(MESSAGE_TYPE, out_msgs[sent_c])
                if st == S.RW_OK:
                    if (c11c.producer_head - before) > L.framed_size(message_length(sent_c)):
                        pad_seen += 1
                    sent_c += 1
                    continue
                if st == S.RW_FULL:
                    full_c += 1
                else:
                    corrupt += 1
                break
            for _ in range(16):
                st, mtype, payload = c11d.try_read()
                if st == S.RR_EMPTY:
                    break
                if st != S.RR_OK:
                    corrupt += 1
                    break
                if mtype != MESSAGE_TYPE or recv_d >= traffic or payload != in_msgs[recv_d]:
                    bad_d += 1
                recv_d += 1
            for _ in range(64):
                st, data = c8.try_read()
                if st == S.RR_EMPTY:
                    break
                if st != S.RR_OK:
                    corrupt += 1
                    break
                seq, gen = struct.unpack_from("<II", data, 0)
                if (recv_c8 >= traffic * 4 or seq != recv_c8 + 1 or gen != created.get("generation")
                        or data[8:] != in_recs[recv_c8]):
                    bad_c8 += 1
                recv_c8 += 1
            for _ in range(40 if tight else 10):
                st, seq, data = S.seqlock_read(sec, c7_off, L.SN_STATUS.size)
                if st == S.SEQ_FRESH:
                    d = L.SN_STATUS.unpack(data)
                    c7_fresh += 1
                    if seq != c7_last_seq:
                        c7_distinct, c7_last_seq = c7_distinct + 1, seq
                    f = d["snFrame"]
                    if d["presentedSeTick"] != 3 * f or d["c3BytesDrained"] != 7 * f or d["hostKind"] != 1:
                        c7_torn += 1
                    if f < c7_last:
                        c7_back += 1
                    c7_last = f
                elif st == S.SEQ_BUSY:
                    c7_busy += 1
            if cs.p.poll() is not None:
                break
            tight = sent_c == traffic and recv_d == traffic and recv_c8 == traffic * 4
            if not tight:
                time.sleep(0.001)
        rc = cs.wait(15)
        summ = cs.event("summary") or {}
        if rc != 0 or not summ.get("done"):
            errs.append("C# exit %s done %s" % (rc, summ.get("done")))
        if summ.get("c11cReceived") != traffic or summ.get("c11cBad") or summ.get("c11cCorrupt") or summ.get("writeErrors"):
            errs.append("C# C11c: received %s bad %s corrupt %s writeErrors %s" % (
                summ.get("c11cReceived"), summ.get("c11cBad"), summ.get("c11cCorrupt"), summ.get("writeErrors")))
        if recv_d != traffic or bad_d or recv_c8 != traffic * 4 or bad_c8 or corrupt:
            errs.append("Python: C11d %d/%d bad %d, C8 %d/%d bad %d, corrupt %d" % (
                recv_d, traffic, bad_d, recv_c8, traffic * 4, bad_c8, corrupt))
        if not (summ.get("c5Fresh", 0) > 0 and summ.get("c5Torn", 1) == 0 and summ.get("c5Backwards", 1) == 0):
            errs.append("C5 read by C#: fresh %s torn %s backwards %s" % (
                summ.get("c5Fresh"), summ.get("c5Torn"), summ.get("c5Backwards")))
        if not (c7_fresh > 0 and c7_torn == 0 and c7_back == 0):
            errs.append("C7 read by Python: fresh %d torn %d backwards %d" % (c7_fresh, c7_torn, c7_back))
        if not (summ.get("tortureC5Fresh", 0) > 0 and summ.get("tortureC5Torn", 1) == 0 and summ.get("tortureC7Writes", 0) > 0):
            errs.append("seqlock torture: C# C5 fresh %s torn %s, C7 writes %s" % (
                summ.get("tortureC5Fresh"), summ.get("tortureC5Torn"), summ.get("tortureC7Writes")))
        if c7_busy + summ.get("c5Busy", 0) + summ.get("tortureC5Busy", 0) == 0:
            errs.append("seqlock torture saw no contention (0 busy reads on either side): not exercised")
        if summ.get("sePidSeen") != me or summ.get("seGenerationSeen") != se_gen or summ.get("seHeartbeatMaxAgeMs", 1e9) >= 100:
            errs.append("SE block read by C#: sePid %s (Python %d) gen %s max age %s ms" % (
                summ.get("sePidSeen"), me, summ.get("seGenerationSeen"), summ.get("seHeartbeatMaxAgeMs")))
        if sn_hb_distinct < 20 or sn_max_age >= 100:
            errs.append("SN heartbeat read by Python: distinct %d max age %d ms" % (sn_hb_distinct, sn_max_age))
        wraps_c = c11c.producer_head // L.REGION["C11c"].size
        wraps_d = summ.get("c11dHead", 0) // L.REGION["C11d"].size
        wraps_8 = summ.get("c8Head", 0) // L.ring_capacity("C8")
        if wraps_c < 1 or wraps_d < 1 or wraps_8 < 1:
            errs.append("traffic did not wrap every ring (C11c %d, C11d %d, C8 %d)" % (wraps_c, wraps_d, wraps_8))
        record("S0.N4.cs-creates", not errs,
               ("C# creator pid %d gen %d; Python: validation OK, region table equal, SDDL %s, magic DEEP, snPid/generation "
                "read back, SN heartbeat %d distinct (max age %d ms); Python attached as SE pid %d gen %d -> C# read it "
                "(max age %s ms). Traffic: C11c Python->C# %d msgs (%d B, %d wraps, %d pads) 0 bad; C11d C#->Python "
                "%d msgs (%d wraps) 0 bad; C8 C#->Python %d records (%d wraps) 0 bad. Seqlock (traffic + %.1f s torture): "
                "C5 Python->C# %d writes, %s accepted copies 0 torn (%s busy reads); C7 C#->Python %s writes, %d "
                "accepted copies (%d distinct seq) 0 torn (%d busy reads)" % (
                    cs.pid, created.get("generation"), sddl, sn_hb_distinct, sn_max_age, me, se_gen,
                    summ.get("seHeartbeatMaxAgeMs"), sent_c, c11c.producer_head, wraps_c, pad_seen, recv_d, wraps_d,
                    recv_c8, wraps_8, 2.0, k, summ.get("c5Fresh", 0) + summ.get("tortureC5Fresh", 0),
                    summ.get("c5Busy", 0) + summ.get("tortureC5Busy", 0), summ.get("c7Writes", 0) + summ.get("tortureC7Writes", 0),
                    c7_fresh, c7_distinct, c7_busy))
               if not errs else "; ".join(errs))
    finally:
        sec.close()
        if cs.p.poll() is None:
            cs.wait(5)

# ----------------------------------------------------------------------------------------------------------------
# S0.M1 cross-language (seeded failures)
# ----------------------------------------------------------------------------------------------------------------

def check_reject(exe: str, mutant: str, tag: str, want_step: int, want_field: str, want_found: str, magic):
    name = "Local\\DeepEngineers_xtest_%d_%s" % (os.getpid(), tag)
    host = start_host(name, "host-%s.log" % tag, ["--no-token-check", "--mutant", mutant, "--passive",
                                                  "--hold-seconds", "30"], token=False)
    try:
        sec = wait_section(name, 15, want_magic=magic)
        if sec is None:
            record("S0.M1.cross." + mutant, False, "the seeded section did not appear")
            return
        time.sleep(0.2)
        before_se = sec.se_owned_checksum()
        before_all = sec.region_checksums()
        cs = CsProcess(exe, ["open", name, "1", "--attach-se"], "cs-open-%s.log" % tag)
        rc = cs.wait(30)
        v = cs.event("validation") or {}
        after_se = sec.se_owned_checksum()
        after_all = sec.region_checksums()
        changed = sorted(k for k in before_all if before_all[k] != after_all.get(k))
        ok = (rc == 3 and v.get("outcome") == "Reject" and v.get("step") == want_step and v.get("field") == want_field
              and v.get("found") == want_found and cs.event("attached") is None and sec.hdr("sePid") == 0
              and before_se == after_se and not changed)
        record("S0.M1.cross." + mutant, ok,
               "C# opener (seeded %s): exit %s, %s; sePid %d; SE-owned bytes %s; region checksums changed: %s" % (
                   mutant, rc, v.get("text"), sec.hdr("sePid"), "unchanged" if before_se == after_se else "CHANGED",
                   ", ".join(changed) or "none"))
        sec.close()
    finally:
        stop(host)

# ----------------------------------------------------------------------------------------------------------------
# devControl: the Python harness against the SE relay rule, on one word from two processes
# ----------------------------------------------------------------------------------------------------------------

def run_devcontrol(exe: str, tag: str, mutant_rmw: bool, seconds: float = 3.0):
    """(green, detail). The C# console runs SE's rule (Interlocked.CompareExchange on bit 1 while bit 0 is clear);
 this process runs the real S.Harness attach/detach (bit 0). Green: both acquired their bit many times, nobody saw
 the other bit while holding its own, nobody lost its own bit, and the word ends at 0."""
    name = "Local\\DeepEngineers_xtest_%d_%s" % (os.getpid(), tag)
    sec, _existed = S.Section.create(name, 0x2000, S.section_sddl_for_current_user())
    off = L.HDR.offset("devControl")
    try:
        cs = CsProcess(exe, ["devcontrol", name, "%.1f" % seconds], "cs-devcontrol-%s.log" % tag)
        if not cs.wait_event("started", 15):
            cs.wait(5)
            return False, "the C# devcontrol worker did not start"
        h = S.Harness(sec, mutant_rmw=mutant_rmw)
        acquired = refused = other = lost = 0
        end = time.monotonic() + seconds - 0.3
        while time.monotonic() < end:
            ok, _why = h.attach()
            if not ok:
                refused += 1
                continue
            acquired += 1
            for _ in range(20):
                v = sec.u32(off)
                if v & S.RELAY_BIT:
                    other += 1
                    break
                if not v & S.HARNESS_BIT:
                    lost += 1
                    break
            h.detach()
        rc = cs.wait(seconds + 30)
        res = cs.event("result") or {}
        final = sec.u32(off)
        violations = other + lost + res.get("otherBitSeen", 0) + res.get("ownBitLost", 0)
        green = bool(rc == 0 and res and violations == 0 and final == 0 and acquired > 0 and res.get("acquired", 0) > 0)
        detail = ("python harness%s: %d attaches, %d refused, saw bit 1 while holding %d, lost bit 0 %d; C# relay rule: "
                  "%s acquisitions, %s refused, %s CAS retries, saw bit 0 while holding %s, lost bit 1 %s; final word %d"
                  % (" (seeded read-then-write)" if mutant_rmw else " (compare-and-swap)", acquired, refused, other, lost,
                     res.get("acquired"), res.get("refused"), res.get("contended"), res.get("otherBitSeen"),
                     res.get("ownBitLost"), final))
        return green, detail
    finally:
        sec.close()

def check_devcontrol(exe: str):
    green, detail = run_devcontrol(exe, "e", False)
    record("S0.DEV.cas", green, detail)
    green_m, detail_m = run_devcontrol(exe, "f", True)
    record("S0.DEV.cas.mutant-rmw", not green_m, ("RED as expected: " if not green_m else "GREEN (the check missed the "
                                                   "seeded read-then-write): ") + detail_m)

def main(argv=None) -> int:
    global LOG_DIR
    ap = argparse.ArgumentParser(description="S0.N4 cross-language check (C# console <-> Python)")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--traffic", type=int, default=400, help="messages per direction (C8 records: 4x)")
    a = ap.parse_args(argv)
    if not os.path.exists(a.exe):
        print("C# console not found: %s\nbuild it: dotnet build tests/DeepEngineers.Wire.CrossTest -c Release" % a.exe)
        print("CROSS TEST FAIL")
        return 1
    LOG_DIR = os.path.join(REPO, "tests", "out", "cross", datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
    os.makedirs(LOG_DIR, exist_ok=True)
    t0 = time.monotonic()
    S.fine_timer(True)
    try:
        check_layout(a.exe)
        check_python_creates(a.exe, "a")
        check_cs_creates(a.exe, "b", a.traffic)
        check_reject(a.exe, "magic", "c", 1, "magic", "0x%x" % MAGIC_DEEQ, MAGIC_DEEQ)
        check_reject(a.exe, "wire2", "d", 4, "wireVersion", "2", L.MAGIC)
        check_devcontrol(a.exe)
    finally:
        S.fine_timer(False)
    n_ok = sum(1 for _, ok, _ in RESULTS if ok)
    print("== summary: %d/%d checks PASS; %.1f s; logs %s" % (n_ok, len(RESULTS), time.monotonic() - t0, LOG_DIR))
    ok = n_ok == len(RESULTS) and len(RESULTS) == 7
    print("CROSS TEST PASS" if ok else "CROSS TEST FAIL")
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
