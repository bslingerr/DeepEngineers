"""`launcher.py s5` and `launcher.py tp1` (ROADMAP S5 "Builder contract"; CONTRACT §5.4 "C1–C4 conventions for S5").
Lives next to launcher.py, which imports it and passes itself (`run(LCH, log, a)`, `add_parsers(sub, LCH)`): the
session machinery (preflight, start_games, the in-world wait, the C11 harness, close_session, collect) is launcher.py's.

 python -I src/DeepEngineers.Launcher/launcher.py s5 --mode S5a|S5b [--parts g1e4,n1,...] [--synth 500,2000,5000 |
 --replay NAME] [--mutants S5.M1,...|--mutant-group NAME] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py s5 --list-mutant-groups | --analyze tests/out/s5/<stamp>
 python -I src/DeepEngineers.Launcher/launcher.py tp1 --state base-se|base-sn|space|track-hover|track-sail|prime|
 underwater[,...] [--ccx1] [--seconds 300] [--baseline DIR] [--dry-run]
 python -I src/DeepEngineers.Launcher/launcher.py tp1 --analyze tests/out/tp1/<stamp> [--baseline DIR]

`s5` runs one scripted S5 session. S5a (default): Subnautica and, in SE's place, the stand-in
tests/standins/fake_se_producer.py --s5 --s2 (the S5 stream replayer / stand-in at scale: --synth builds ships of 500,
2,000 and 5,000 blocks from SE's real models read in place before the link, --replay streams a.derec recording); S5b:
both games (Legacy.exe -continue). After ReadyIdle and the in-world/hello wait the parts run through the C11 harness in
the plugins' own syntax (S5Runner.SE_ACTIONS / SN_ACTIONS: SE de.s5 {status | models | export | coh | trace | character
| record}, Subnautica de.s5 {action: sub | coh | placed | status}, de.s5 {fpbody}, de.s3 {action: teardown | count});
the steps only the fake producer can script (spawn, edits, rotor, piston, door, fly, cockpit, count, teardown) are project
steps in S5b. Every command and reply goes into s5-session.jsonl, the section is sampled at 10 Hz during every part, SE's
s5.* rows are tailed while the session runs (the root grid id, the parts of de.s5 export, the coh point):
 g1e4 S5a: the 500-block ship (or the replay) at the deep spot, mirrored; S5b: SE de.s5 models {count 200, send}
 then the project's 500-block ship: Subnautica's s5.model winding rows of the first 200 distinct models
 n1 per-grid counts (SE s5.grid / s5.edit parts, Subnautica s5.grid); 20 random parts (SE de.s5 export ->
 Subnautica de.s5 placed per entity); one grind; the counts again
 n2 weld, stage, damage, paint, grind, each in its own window n2.<edit> (the analyzer names SE's edit rows by it)
 n3 rotor 10 rpm, piston out and in, door open and closed, windows n3.<kind>, SE de.s5 trace + Subnautica
 de.s5 sub rows (interpolated frames compared through the bracket's two ticks)
 n4 camera in the cockpit, ship at 20 m/s with a 0.5 Hz yaw sine for 10 s, both sides' s5.coh rows of one point
 n5 SE de.s5 models {count 200, send false}: the worker-thread loader's s5.model rows
 n6 500, 2,000 and 5,000 blocks in turn underwater, Subnautica's s5.scale rows and measure.ps1
 n7 the character (S5b): SE de.s5 character on, Subnautica fpbody=se, a swim script (de.swim) in third then first
 person, s5.char / s5.rig rows; fpbody back to diver
 record SE's recorder (de.s5 record {name, c1, c2}... stop) and a check of the.derec (header, framing, sidecar)
 tp1 the combined T-P1 states in this launch (as `tp1`; baselines need their own launches)
 teardown the ships removed, Subnautica de.s3 teardown + count (S5.N1.C12)
Seeded failures (--mutants, --mutant-group): S5.M1, M2, M4, M5 in Subnautica (DEEPENGINEERS_MUTANT, plus de.mutant
{name, on}); S5.M3 at the producer (S5a: fake --s5-mutant M3; S5b: SE's DEEPENGINEERS_MUTANTS); `S5.M1@producer` is the
stand-in's form of M1 (S5a only). Output tests/out/s5/<stamp>/ (s5-session.jsonl, se-*/sn-*.jsonl, the game logs,
measure-*.csv) and the S5 ORACLE TABLE of tests/s5/analyze_s5.py (s5-table.txt, s5-results.json).

`tp1` runs the T-P1 states (ROADMAP S5.T-P1), `--seconds` each (300 by default): measure.ps1 at 1 s, the section at
10 Hz, the machine's commit charge at 1 Hz (GetPerformanceInfo), both plugins' per-second JSONL and SE's STATISTICS
lines (collected). base-se: SE alone (the passive stand-in host keeps the section) at the Space, Track-hover and
Track-sail spots in turn; base-sn: Subnautica alone with the fake producer's camera at the underwater spot; the combined
states (one launch for any list of them): space (the band exit + 300 m), track-hover (+50 m), track-sail (+1.5 m, 5 m/s along +x),
prime (a vertical sine between +0.5 and +5.5 m), underwater (-60 m at the grandReef spot, 5 m/s past the 2,000-block
ship). The motion is the scripted autopilot of de.motion. --ccx1: Subnautica's affinity set to 0xFF00 (CCX1) and the underwater state recorded as `ccx1` (an experiment
only, D10). Output tests/out/tp1/<stamp>/ (tp1-session.jsonl, measure-tp1.<state>.csv, logs); the analyzer's T-P1 rows
(SE frame time "within 5 % of SE alone" needs the base-se folder: --baseline).
Games are never piped and only closed by PID (CloseMainWindow; the stand-ins by TerminateProcess)."""
from __future__ import annotations

import ctypes
import datetime
import json
import math
import os
import subprocess
import sys
import time

S5_PARTS = ("g1e4", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "record", "tp1", "teardown")
S5_DEFAULT_PARTS = {"S5a": ("g1e4", "n1", "n2", "n3", "n4", "n6", "teardown"),
                    "S5b": ("g1e4", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "teardown")}
S5_SN_MUTANTS = ("S5.M1", "S5.M2", "S5.M4", "S5.M5")
S5_ALL_MUTANTS = S5_SN_MUTANTS + ("S5.M3", "S5.M1@producer")
# runbook groups (one launch each): the mutant, the parts that show it, the rows that must turn red
S5_MUTANT_GROUPS = {
    "lag": {"mutants": ("S5.M1",), "parts": ("g1e4", "n4"), "red": ("S5.N4",)},
    "flip": {"mutants": ("S5.M2",), "parts": ("g1e4",), "red": ("S5.G1-E4",)},
    "remove": {"mutants": ("S5.M3",), "parts": ("g1e4", "n1"), "red": ("S5.N1.C1",)},
    "idle": {"mutants": ("S5.M4",), "parts": ("tp1",), "states": ("space",), "red": ("S5.T-P1.space",)},
    "cameras": {"mutants": ("S5.M5",), "parts": ("tp1",), "states": ("track-hover", "track-sail"),
                "red": ("S5.T-P1.track-hover", "S5.T-P1.track-sail")},
}
DEEP = (-1029.0, -1116.0)          # grandReef (SN x, z): seabed about -235 m
SHALLOW = (0.0, 0.0)               # Safe Shallows anchor: seabed -17.2 m
PARK_Y, PARK_DX = -8.0, 24.0       # the real SE character parked in open water at the session start (as S4_PARK_*)
SHIP_Y = -150.0                    # the ships float well above the grandReef seabed
SHIP_AHEAD = 45.0                  # camera this far south of the ship's origin, looking north (yaw 0)
N6_BACK = 90.0                     # n6: the camera farther back (the 5,000-block hull is about 85 m long)
SHIP_HEADING = 30.0
N4 = {"speedMps": 20.0, "yawAmpDeg": 10.0, "yawHz": 0.5, "seconds": 10.0}
N3_RPM = 10.0
N1_PARTS = 20
SYNTH_SIZES = (500, 2000, 5000)
MIRROR_WAIT_S = {500: 90.0, 2000: 180.0, 5000: 300.0}
def band_from_env():
    """The plugin's picture band (enter, exit) in metres: env DEEPENGINEERS_BAND='enter,exit' (the same variable SE reads),
 default +1,000 / +1,100 m again since S7t T3."""
    v = os.environ.get("DEEPENGINEERS_BAND", "")
    try:
        parts = [float(x) for x in v.replace(";", ",").split(",") if x.strip()]
        if len(parts) == 2 and 0.0 <= parts[0] < parts[1]:
            return parts[0], parts[1]
    except ValueError:
        pass
    return 1000.0, 1100.0   # S7t T3: the band back at 1 km (crossfade, horizon/haze, look match, impostors)

BAND = band_from_env()
TP1_STATES = ("base-se", "base-sn", "space", "band-hover", "track-hover", "track-sail", "prime", "underwater", "window")
TP1_COMBINED = ("space", "band-hover", "track-hover", "track-sail", "prime", "underwater", "window")
TP1_BASE_SE_SPOTS = ("space", "band-hover", "track-hover", "track-sail")
TP1_PLAN = {   # state -> (teleport x, y, z, yawDeg), the view state to wait for, the motion (or None)
    "space": ((0.0, BAND[1] + 300.0, 0.0, 0.0), ("Space",), None),
    # review S7a F12: Approach at the band from below (the latch on, Subnautica Full at 1 km of altitude), hovering
 # inside the hysteresis; the latch-off warm-up window is measured inside the s7a N2 descents (no hover can hold it)
    "band-hover": ((0.0, BAND[0] - 50.0, 0.0, 0.0), ("Track",),
                   {"kind": "bob", "centreY": (BAND[0] + BAND[1]) / 2.0, "amplitudeM": 20.0, "hz": 0.05}),
    "track-hover": ((0.0, 50.0, 0.0, 0.0), ("Track",), None),
    "track-sail": ((0.0, 1.5, 0.0, 90.0), ("Track", "Prime"), {"kind": "line", "dx": 1.0, "speedMps": 5.0}),
    "prime": ((0.0, 3.0, 0.0, 0.0), ("Prime", "Track"), {"kind": "bob", "centreY": 3.0, "amplitudeM": 2.5, "hz": 0.1}),
    "underwater": ((DEEP[0] - 300.0, -60.0, DEEP[1], 90.0), ("Underwater",),
                   {"kind": "line", "dx": 1.0, "speedMps": 5.0}),
    # S8a (B4 hook, ROADMAP S8a.T-P1r): over the generated seabed beyond the crater at 30 m/s with the window worker busy;
 # 10 m under the surface (the sculpt's ridges reach -15 m), 9 km along +z inside the window universe (z < 7,712)
    "window": ((2500.0, -10.0, -2000.0, 0.0), ("Underwater",),
               {"kind": "line", "fromSn": [2500.0, -10.0, -2000.0], "toSn": [2500.0, -10.0, 7000.0], "speedMps": 30.0}),
}
TP1_SHIP = 2000

# ----------------------------------------------------------------------------------------------------------------
# helpers
# ----------------------------------------------------------------------------------------------------------------

class _PERFINFO(ctypes.Structure):
    _fields_ = [("cb", ctypes.c_uint32), ("CommitTotal", ctypes.c_size_t), ("CommitLimit", ctypes.c_size_t),
                ("CommitPeak", ctypes.c_size_t), ("PhysicalTotal", ctypes.c_size_t),
                ("PhysicalAvailable", ctypes.c_size_t), ("SystemCache", ctypes.c_size_t),
                ("KernelTotal", ctypes.c_size_t), ("KernelPaged", ctypes.c_size_t),
                ("KernelNonpaged", ctypes.c_size_t), ("PageSize", ctypes.c_size_t), ("HandleCount", ctypes.c_uint32),
                ("ProcessCount", ctypes.c_uint32), ("ThreadCount", ctypes.c_uint32)]

def perf_info():
    """The machine's commit charge, commit limit and physical memory in bytes (K32GetPerformanceInfo; read-only)."""
    try:
        pi = _PERFINFO()
        pi.cb = ctypes.sizeof(pi)
        if not ctypes.windll.kernel32.K32GetPerformanceInfo(ctypes.byref(pi), pi.cb):
            return None
        ps = pi.PageSize
        return {"commitTotal": pi.CommitTotal * ps, "commitLimit": pi.CommitLimit * ps,
                "commitPeak": pi.CommitPeak * ps, "physTotal": pi.PhysicalTotal * ps,
                "availPhys": pi.PhysicalAvailable * ps}
    except (OSError, AttributeError):
        return None

def set_affinity(pid, mask):
    """SetProcessAffinityMask on a process this launcher started (the --ccx1 experiment); (ok, error)."""
    try:
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = ctypes.c_void_p
        h = k32.OpenProcess(0x0200 | 0x0400, False, int(pid))      # SET_INFORMATION | QUERY_INFORMATION
        if not h:
            return False, ctypes.get_last_error()
        try:
            ok = bool(k32.SetProcessAffinityMask(ctypes.c_void_p(h), ctypes.c_size_t(mask)))
            return ok, 0 if ok else ctypes.get_last_error()
        finally:
            k32.CloseHandle(ctypes.c_void_p(h))
    except (OSError, AttributeError) as e:
        return False, str(e)

def s5_stream_module(LCH):
    p = os.path.join(LCH.REPO, "tests", "standins")
    if p not in sys.path:
        sys.path.insert(0, p)
    import s5_stream   # noqa: E402
    return s5_stream

def analyzer(LCH):
    p = os.path.join(LCH.REPO, "tests", "s5")
    if p not in sys.path:
        sys.path.insert(0, p)
    import analyze_s5   # noqa: E402
    return analyze_s5

class MemSampler:
    """1 Hz "mem" events (commit charge, commit limit, physical memory, and each game process's working set and private
 bytes as <name>WorkingSetMB / <name>PrivateMB) tagged with the current part."""

    def __init__(self, sess, procs=None, process_memory=None):
        import threading
        self.sess, self.part = sess, None
        self.procs, self.process_memory = procs or {}, process_memory
        self.stop_ev = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)

    def start(self):
        self.thread.start()
        return self

    def stop(self):
        self.stop_ev.set()
        self.thread.join(3.0)

    def run(self):
        while not self.stop_ev.is_set():
            part = self.part
            if part is not None:
                pi = perf_info() or {}
                for name, proc in self.procs.items():
                    pid = getattr(proc, "pid", None)
                    if not pid or self.process_memory is None:
                        continue
                    try:
                        m = self.process_memory(pid)
                    except Exception:
                        m = None
                    if m:
                        pi[name + "WorkingSetMB"] = round(m["workingSet"] / 1048576.0, 1)
                        pi[name + "PrivateMB"] = round(m["privateBytes"] / 1048576.0, 1)
                if pi:
                    self.sess.event("mem", part=part, **pi)
            self.stop_ev.wait(1.0)

# ----------------------------------------------------------------------------------------------------------------
# the runner
# ----------------------------------------------------------------------------------------------------------------

class S5Tail:
    """The SE side's s5.* rows appended since the session start (read-only): the stand-in's stdout (S5a,
 <out>/se-standin.jsonl) or the SE plugin's se-*.jsonl in %LOCALAPPDATA%\\DeepEngineers\\logs (S5b). Rows are parsed
 with tests/s5/analyze_s5.py's reader (every accepted form)."""

    def __init__(self, LCH, out, t_start, standin_se):
        self.AN = analyzer(LCH)
        self.paths = [os.path.join(out, "se-standin.jsonl")] if standin_se else None
        self.pattern = os.path.join(LCH.LOG_DIR, "se-*.jsonl")
        self.t_start = t_start
        self.offsets, self.partial, self.rows = {}, {}, []

    def poll(self):
        import glob
        files = self.paths or [p for p in sorted(glob.glob(self.pattern)) if os.path.getmtime(p) >= self.t_start - 5]
        for p in files:
            try:
                with open(p, "rb") as f:
                    f.seek(self.offsets.get(p, 0))
                    data = f.read()
                    self.offsets[p] = f.tell()
            except OSError:
                continue
            if not data:
                continue
            text = self.partial.pop(p, "") + data.decode("utf-8", errors="replace")
            lines = text.split("\n")
            if not text.endswith("\n"):
                self.partial[p] = lines.pop()
            for ln in lines:
                ln = ln.strip()
                if not ln.startswith("{"):
                    continue
                try:
                    rec = json.loads(ln)
                except ValueError:
                    continue
                got = self.AN.parse_record(rec, "se") if isinstance(rec, dict) else None
                if got:
                    self.rows.append(got)
        return len(self.rows)

    def mark(self):
        return self.poll()

    def since(self, mark, name=None):
        self.poll()
        return [d for n, d in self.rows[mark:] if name is None or n == name]

class S5Runner:
    """The scripted S5 parts through the C11 harness. One table per side names every action in the syntax the plugins
 take today (SE: src/DeepEngineers.SE/S5Commands.cs; Subnautica: src/DeepEngineers.SN/S5Host.cs, the S3 lab's
 teardown and count), so a renamed command changes in one place. STANDIN_ONLY actions exist only in the fake producer
 (spawn, edits, rotor, piston, door, fly, cockpit, count, teardown): their reply must echo "de.s5 <action>" (the SE
 plugin answers an unknown action with its status); in S5b the same steps are project steps, else skipped (their rows stay MISSING). An action a plugin refuses as unknown is
 not sent again. Never touches a game window."""

    SE_ACTIONS = {
        "status": ("de.s5", lambda: {"status": True}),
        "models": ("de.s5", lambda n, send: {"models": {"count": int(n), "send": bool(send)}}),
        "export": ("de.s5", lambda entity, n, seed: {"export": entity, "sample": int(n), "seed": int(seed)}),
        "coh": ("de.s5", lambda entity, point, secs: {"coh": dict({"entityId": entity, "seconds": float(secs)}, **(
            {"point": list(point)} if point is not None else {}))}),
        "trace": ("de.s5", lambda secs: {"trace": float(secs)}),
        "character": ("de.s5", lambda on: {"character": "on" if on else "off"}),
        "record": ("de.s5", lambda name: {"record": {"name": name, "c1": True, "c2": True}}),
        "recordStop": ("de.s5", lambda: {"record": "stop"}),
        "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
        # the fake producer only (S5a)
        "spawn": ("de.s5", lambda ship, sn, yaw: dict({"action": "spawn", "sn": list(sn), "yawDeg": float(yaw)},
                                                       **({"blueprint": ship} if isinstance(ship, str) else
                                                          {"ship": int(ship)}))),
        "count": ("de.s5", lambda label: {"action": "count", "label": label}),
        "edit": ("de.s5", lambda kind: {"action": "edit", "kind": kind}),
        "rotor": ("de.s5", lambda rpm: {"action": "rotor", "rpm": float(rpm)}),
        "piston": ("de.s5", lambda m: {"action": "piston", "extendM": float(m)}),
        "door": ("de.s5", lambda on: {"action": "door", "open": bool(on)}),
        "fly": ("de.s5", lambda: dict({"action": "fly", "cockpit": True}, **N4)),
        "cockpit": ("de.s5", lambda on: {"action": "cockpit", "on": bool(on)}),
        "teardown": ("de.s5", lambda: {"action": "teardown"}),
    }
    STANDIN_ONLY = ("spawn", "count", "edit", "rotor", "piston", "door", "fly", "cockpit", "teardown")
    SN_ACTIONS = {
        "sub": ("de.s5", lambda secs: {"action": "sub", "seconds": float(secs)}),
        "coh": ("de.s5", lambda entity, local, secs: {"action": "coh", "entity": entity, "local": list(local),
                                                      "seconds": float(secs)}),
        "placed": ("de.s5", lambda entity, keys: {"action": "placed", "entity": entity, "parts": list(keys)}),
        "fpbody": ("de.s5", lambda which: {"fpbody": which}),
        "status": ("de.s5", lambda: {"action": "status"}),
        "teardown": ("de.s3", lambda: {"action": "teardown"}),
        "count": ("de.s3", lambda: {"action": "count"}),
        "mutant": ("de.mutant", lambda name, on: {"name": name, "on": bool(on)}),
    }

    def __init__(self, LCH, log, a, sess, sn, se, out, stamp, standin_sn, standin_se, se_alone=False, t_start=None):
        self.LCH, self.L, self.S = LCH, LCH.L, LCH.S
        self.log, self.a, self.sess, self.sn, self.se, self.out = log, a, sess, sn, se, out
        self.stamp, self.standin_sn, self.standin_se, self.se_alone = stamp, standin_sn, standin_se, se_alone
        self.q = getattr(a, "quick", False)
        self.sec = self.S.Section.open(a.name)
        self.h = self.S.Harness(self.sec) if self.sec else None
        self.sampler = LCH.SectionSampler(a.name, sess, 10.0).start()
        self.mem = MemSampler(sess, {"se": se, "sn": sn}, getattr(LCH, "process_memory", None)).start()
        self.tail = S5Tail(LCH, out, t_start or time.time(), standin_se)
        self.stats = {"sent": 0, "ok": 0, "failed": 0, "noReply": 0, "unknown": []}
        self.unsupported = set()
        self.bg = []
        self.spawned = None          # the stand-in's spawn reply
        self.root = None             # the root grid's entity id (int)

 # ---- plumbing -------------------------------------------------------------------------------------------------
    def alive(self):
        return self.se.poll() is None and (self.sn is None or self.sn.poll() is None)

    def attach(self):
        if self.h is None:
            return False, "section absent"
        ok, why = self.h.attach()
        if not ok:
            return False, why
        for side in (("se",) if self.se_alone else ("se", "sn")):
            if self.call(side, "de.ping", {}) is None:
                return False, "%s does not answer C11 (dev build needed)" % side.upper()
        return True, ""

    def close(self):
        for p in self.bg:
            if p.poll() is None:
                try:
                    p.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    p.terminate()
        self.sampler.stop()
        self.mem.stop()
        if self.h:
            self.h.detach()
        if self.sec:
            self.sec.close()

    def wait(self, seconds):
        end = time.monotonic() + seconds
        while time.monotonic() < end and self.alive():
            self.h.poll_replies()
            time.sleep(min(0.02, max(0.0, end - time.monotonic())))

    def hold(self, seconds, quick=None):
        return self.wait(seconds if not self.q else (quick if quick is not None else min(seconds, 2.0)))

    def call(self, side, cmd, args=None, timeout=None, label=None):
        t0 = time.monotonic()
        q0 = self.S.qpc()
        rid, st = self.h.send(side, cmd, args or {})
        self.stats["sent"] += 1
        rep = self.h.wait_reply(rid, timeout or self.a.cmd_timeout) if st == self.S.RW_OK else None
        self.sess.event("reply", side=side, cmd=cmd, args=args or {}, id=rid, reply=rep, qpc=q0, label=label,
                        ms=round((time.monotonic() - t0) * 1000, 1))
        if rep is None:
            self.stats["noReply"] += 1
        elif rep.get("ok"):
            self.stats["ok"] += 1
        else:
            self.stats["failed"] += 1
            if "unknown" in str(rep.get("error", "")).lower() and cmd not in self.stats["unknown"]:
                self.stats["unknown"].append(cmd)
        return rep

    @staticmethod
    def ok(rep):
        return bool(rep) and bool(rep.get("ok"))

    def park(self):
        """The real SE only: DE Lab's saved character stands ~125 m under the Safe Shallows seabed (inside the S4 tiles);
 park it in open water first, as the s4 runner does."""
        if self.standin_se:
            return
        x, z = SHALLOW[0] + PARK_DX, SHALLOW[1]
        rep = self.call("se", "de.swim", {"teleportChar": [x, PARK_Y, z], "yawDeg": 0.0}, label="park")
        if not self.ok(rep):
            self.log.say("  S5 park: de.swim teleportChar not acknowledged (%s); the character stays where the save put "
                         "it" % (json.dumps(rep)[:160] if rep else "no reply"), ev="s5_park_failed")
        self.wait(2.0)

    def act(self, side, action, *args, label=None, timeout=None):
        """One side's action from its table. An action refused as unknown, or a stand-in-only action the SE plugin
 answers with its status, is marked unsupported (not sent again; its rows stay MISSING)."""
        key = (side, action)
        if key in self.unsupported:
            return None
        table = self.SE_ACTIONS if side == "se" else self.SN_ACTIONS
        cmd, build = table[action]
        rep = self.call(side, cmd, build(*args), timeout=timeout, label=label or "%s.%s" % (side, action))
        why = None
        if rep is not None and not rep.get("ok") and "unknown" in str(rep.get("error", "")).lower():
            why = rep.get("error")
        elif side == "se" and action in self.STANDIN_ONLY and rep is not None and rep.get("ok") and \
                not str(rep.get("line", "")).startswith("de.s5 %s" % action):
            why = "answered with %r (not the stand-in's action)" % str(rep.get("line", ""))[:60]
            rep = dict(rep, ok=False, error=why)
        if why:
            self.unsupported.add(key)
            self.log.say("  S5: %s does not take %s (%s): its rows will be MISSING" % (side.upper(), action, why))
        return rep

    def can(self, side, action):
        if side == "se" and action in self.STANDIN_ONLY and not self.standin_se:
            return False
        return (side, action) not in self.unsupported

    def owner(self, step, prompt, seconds):
        """An project step (S5b): the instruction is printed and the launcher waits `seconds`; without --owner the step
 is skipped (logged) and the caller's rows stay MISSING."""
        if not getattr(self.a, "owner", False):
            self.sess.event("owner", step=step, phase="skipped", qpc=self.S.qpc(), prompt=prompt)
            self.log.say("  S5 owner step %s skipped (no --owner): %s" % (step, prompt), ev="s5_owner_skipped")
            return False
        self.log.say("  OWNER (%d s): %s" % (seconds, prompt), ev="owner_prompt", step=step)
        self.sess.event("owner", step=step, phase="begin", qpc=self.S.qpc(), prompt=prompt)
        self.wait(seconds)
        self.sess.event("owner", step=step, phase="end", qpc=self.S.qpc())
        return True

    def part(self, name, phase):
        st = self.state()
        self.sess.event("part", name=name, phase=phase, qpc=self.S.qpc(), tick=st.get("c1Tick"), c7=st.get("c7"))
        self.sampler.part = name if phase == "begin" else None
        self.mem.part = name if phase == "begin" else None
        self.log.say("  S5 part %s %s" % (name, phase), ev="s5_part", part=name, phase=phase)

    def state(self):
        try:
            return self.LCH.read_state(self.sec) if self.sec else {}
        except (OSError, ValueError):
            return {}

    def c7(self):
        st, _q, d = self.S.seqlock_read(self.sec, self.L.REGION["C7"].offset, self.L.SN_STATUS.size)
        if not d:
            return {}
        return self.L.SN_STATUS.unpack(d)

    def view(self):
        c5 = self.state().get("c5") or {}
        v, m = c5.get("viewState"), c5.get("approachMode")
        names = ("Space", "Approach", "Underwater", "Surfacing")
        name = names[v] if isinstance(v, int) and v < 4 else "?"
        return {1: "Track", 2: "Prime"}.get(m, "Approach") if name == "Approach" else name

    def wait_view(self, want, timeout):
        t0 = time.monotonic()
        while time.monotonic() - t0 < timeout and self.alive():
            if self.view() in want:
                return round(time.monotonic() - t0, 2)
            self.wait(0.1)
        return None

    def teleport(self, x, y, z, yaw=0.0, label=None):
        return self.call("se", "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw, "label": label or ""}, label=label)

    def motion(self, args, label):
        return self.call("se", "de.motion", dict(args, label=label), label=label)

    def wait_mirrored(self, grids, timeout, label):
        """Until C7 shows `grids` mirrored entities with no pending part for 1 s (or the timeout)."""
        t0 = time.monotonic()
        since = None
        last = {}
        while time.monotonic() - t0 < timeout and self.alive():
            v = self.c7()
            last = {k: v.get(k) for k in ("mirroredEntityCount", "pendingParts", "c3BytesDrained")}
            if v and v.get("mirroredEntityCount", 0) >= grids and v.get("pendingParts", 1) == 0:
                since = since or time.monotonic()
                if time.monotonic() - since >= 1.0:
                    break
            else:
                since = None
            self.wait(0.25)
        secs = round(time.monotonic() - t0, 2)
        done = since is not None
        self.sess.event("mirrored", label=label, grids=grids, done=done, seconds=secs, c7=last)
        self.log.say("  S5 %s: %s after %.1f s (C7 %s)" % (label, "mirrored" if done else "NOT mirrored", secs,
                                                           json.dumps(last)), ev="s5_mirrored", done=done)
        return done

    def measure(self, part, seconds):
        csv_path = os.path.join(self.out, "measure-%s.csv" % part)
        f = open(os.path.join(self.out, "measure-%s.txt" % part), "w", encoding="utf-8")
        p = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                              os.path.join(self.LCH.TOOLS, "measure", "measure.ps1"), "-Seconds", str(int(seconds)),
                              "-Interval", "1", "-Csv", csv_path, "-Names", "Legacy,Subnautica"], stdout=f,
                             stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.bg.append(p)
        self.sess.event("measure", part=part, csv=os.path.basename(csv_path), seconds=seconds)
        return p

    # ---- the ship -------------------------------------------------------------------------------------------------
    def ship_arg(self, size):
        ships = getattr(self.a, "ships_map", {}) or {}
        return ships.get(int(size), int(size))

    def spawn(self, size, back=SHIP_AHEAD, label="spawn", y=SHIP_Y):
        """A ship of `size` blocks at the deep spot, the camera `back` m south of it: the fake producer's synthetic ship
 (S5a), or the project's blueprint found in SE's s5.grid rows."""
        x, z = DEEP
        if self.spawned is not None and self.can("se", "teardown"):
            self.act("se", "teardown", label="%s.teardownPrevious" % label)
            self.wait(2.0)
        self.teleport(x, y, z - back, 0.0, label=label + ".camera")
        self.wait(1.0)
        if self.can("se", "spawn"):
            rep = self.act("se", "spawn", self.ship_arg(size), (x, y, z), SHIP_HEADING, label=label, timeout=30.0)
            if self.ok(rep):
                self.spawned = rep
                self.root = _int_id(rep.get("rootId"))
                self.wait_mirrored(len(rep.get("grids") or []) or 1,
                                   MIRROR_WAIT_S.get(size, 120.0) * (0.5 if self.q else 1.0), label)
            return rep
        mark = self.tail.mark()
        bp = self.ship_arg(size)
        if self.owner(label, "spawn the %s-block test ship (%s) at SN (%g, %g, %g), in front of the camera, and leave "
                             "it there" % (size, bp if isinstance(bp, str) else "your blueprint", x, y, z), 60.0):
            self.root = self.find_root(mark) or self.root
            self.wait_mirrored(1, MIRROR_WAIT_S.get(size, 120.0), label)
        return None

    def find_root(self, mark=0):
        """The nearest root grid of SE's s5.grid rows since `mark` (kind grid, not a subgrid), else None."""
        best = None
        for d in self.tail.since(mark, "s5.grid"):
            kind = str(d.get("kind", "grid"))
            if kind not in ("grid", "root") or _int_id(d.get("parentId")) not in (None, 0):
                continue
            dist = d.get("distance")
            dist = dist if isinstance(dist, (int, float)) else 1e9
            gid = _int_id(d.get("entityIdStr", d.get("entityId")))
            if gid and (best is None or dist < best[0]):
                best = (dist, gid)
        if best:
            self.sess.event("root", entityId=best[1], distance=best[0])
        return best[1] if best else None

    def root_for_se(self):
        if self.root is None:
            self.root = self.find_root(0)
        if self.root is None:
            return None
        # the SE plugin takes a decimal string (S5Args.TryEntityId): C11 JSON numbers are doubles, and SE entity ids
 # are above 2^53, so a bare number would be refused
        return ("0x%016x" % self.root) if self.standin_se else str(self.root)

    def root_hex(self):
        return ("0x%016x" % self.root) if self.root is not None else None

    # ---- parts ------------------------------------------------------------------------------------------------------
    def p_g1e4(self):
        """the model set: S5a: the fake producer's 500-block ship (or the replay) at the deep spot, mirrored; S5b: SE's
 de.s5 models {count 200, send} (the census-listed models first), then the project's 500-block ship;
 Subnautica's s5.model winding rows of the first 200 distinct models (S5.G1-E4)"""
        self.part("g1e4", "begin")
        if self.a.mode == "S5a" and getattr(self.a, "replay", None):
            self.teleport(DEEP[0], SHIP_Y, DEEP[1] - SHIP_AHEAD, 0.0, label="g1e4.camera")
            self.wait_mirrored(1, 120.0, "g1e4.replay")
        elif self.standin_se:
            self.spawn(500, label="g1e4")
        else:
            mark = self.tail.mark()
            self.act("se", "models", 200, True, timeout=10.0)
            t0 = time.monotonic()
            while time.monotonic() - t0 < (30.0 if self.q else 180.0) and self.alive():
                if len(self.tail.since(mark, "s5.model")) >= 200:
                    break
                self.wait(1.0)
            self.hold(15.0, 3.0)
            self.spawn(500, label="g1e4.ship")
        self.part("g1e4", "end")

    def p_n1(self):
        """per-grid counts (SE s5.grid / s5.edit, Subnautica s5.grid every second); 20 random parts (SE de.s5 export ->
 Subnautica de.s5 placed per entity); one grind; the counts again (S5.N1.C1,
 C2; S5.M3 shows here)"""
        if self.root is None and self.spawned is None and not getattr(self.a, "replay", None):
            self.spawn(500, label="n1")
        self.part("n1", "begin")
        if self.can("se", "count"):
            self.act("se", "count", "n1.before")
        root = self.root_for_se()
        if root is not None:
            mark = self.tail.mark()
            rep = self.act("se", "export", root, N1_PARTS, self.a.seed)
            self.wait(1.0)
            rows = self.tail.since(mark, "s5.part")
            if not rows and self.ok(rep):
                rows = rep.get("parts") or []
            by = {}
            for d in rows:
                e = _int_id(d.get("entityIdStr", d.get("entityId")))
                k = d.get("partKey")
                if e is not None and k is not None:
                    by.setdefault(e, []).append(k if isinstance(k, str) else "0x%016x" % int(k))
            for e, keys in sorted(by.items()):
                self.act("sn", "placed", "0x%016x" % e, keys)
            self.sess.event("n1_parts", parts=sum(len(v) for v in by.values()), entities=len(by))
        else:
            self.log.say("  S5 n1: no root grid known (no spawn reply, no SE s5.grid row): de.s5 export skipped")
        self.wait(2.0)
        if self.can("se", "edit"):
            self.act("se", "edit", "grind", label="n1.grind")
        else:
            self.owner("n1.grind", "grind away ONE armor block of the test ship (any block)", 30.0)
        self.wait(3.0)
        if self.can("se", "count"):
            self.act("se", "count", "n1.afterGrind")
        self.wait(2.0)
        self.part("n1", "end")

    def p_n2(self):
        """weld, stage, damage, paint, grind, each in its own window n2.<edit>: SE's
 s5.edit rows, Subnautica's s5.edit rows (presentedSeTick) and counts (S5.N2.<edit>)"""
        if self.root is None and self.spawned is None and not getattr(self.a, "replay", None):
            self.spawn(500, label="n2")
            # real run S5-1: the first edits queued behind the ship's initial chunk builds (171-473 ticks);
 # an edit latency is only meaningful once the ship is built and settled
            self.wait_mirrored(1, 60.0, "n2.spawn")
            self.wait(4.0)
        prompts = {"weld": "place and weld ONE new armor block on the test ship",
                   "stage": "weld that block one build stage further (or place a block and stop welding halfway)",
                   "damage": "damage ONE complete block below its build ratio (grinder, a few seconds)",
                   "paint": "paint ONE block of the test ship another colour", "grind": "grind away ONE block"}
        self.part("n2", "begin")
        for kind in ("weld", "stage", "damage", "paint", "grind"):
            self.part("n2.%s" % kind, "begin")
            if self.can("se", "edit"):
                self.act("se", "edit", kind, label="n2.%s" % kind)
                self.wait(2.0)
            else:
                self.owner("n2.%s" % kind, prompts[kind], 25.0)
                self.wait(2.0)
            self.part("n2.%s" % kind, "end")
        if self.can("se", "count"):
            self.act("se", "count", "n2.after")
        self.wait(2.0)
        self.part("n2", "end")

    def p_n3(self):
        """rotor at 10 rpm, piston out and back, door open and closed, each in its own window n3.<kind>, with SE's de.s5 trace and Subnautica's de.s5 sub rows (alpha 1; S5.N3.<kind>)"""
        if self.root is None and self.spawned is None and not getattr(self.a, "replay", None):
            self.spawn(500, label="n3")
        total = 45.0 if not self.q else 20.0
        if not self.can("se", "rotor"):
            total += 60.0
        self.part("n3", "begin")
        root = self.root_for_se()
        if root is not None:     # one s5.part row: the root's matrix and P* at this tick (the SE s5.sub rows carry no P*)
            self.act("se", "export", root, 1, 1, label="n3.pivot")
        self.act("se", "trace", total)
        self.act("sn", "sub", total)
        steps = (("rotor", [("rotor", N3_RPM, 10.0, 3.0), ("rotor", 0.0, 0.5, 0.5)],
                  "set the test rotor to 10 rpm for 10 s, then 0"),
                 ("piston", [("piston", 2.0, 5.0, 3.0), ("piston", 0.0, 5.0, 3.0)],
                  "extend the test piston 2 m and retract it"),
                 ("door", [("door", True, 3.0, 2.5), ("door", False, 3.0, 2.5)], "open the test door and close it"))
        for kind, acts, prompt in steps:
            self.part("n3.%s" % kind, "begin")
            if self.can("se", kind):
                for action, value, secs, quick in acts:
                    self.act("se", action, value)
                    self.hold(secs, quick)
            else:
                self.owner("n3.%s" % kind, prompt, 20.0)
            self.part("n3.%s" % kind, "end")
        self.part("n3", "end")

    def p_n4(self):
        """camera in the cockpit, ship at 20 m/s with a 0.5 Hz yaw sine (10 deg) for 10 s; SE's de.s5 coh rows (SE pixel) and Subnautica's s5.coh rows of the same cockpit-fixed point (S5.N4;
 S5.M1 shows here)"""
        if self.root is None and self.spawned is None and not getattr(self.a, "replay", None):
            self.spawn(500, label="n4")
        root = self.root_for_se()
        secs = N4["seconds"] + 2.0
        point = (self.spawned or {}).get("cohPoint")
        if self.can("se", "cockpit"):
            self.act("se", "cockpit", True, label="n4.cockpit")
            self.wait(1.5)
        else:
            self.owner("n4.seat", "sit in the test ship's cockpit (first person) and get ready to fly", 20.0)
        if root is None:
            self.log.say("  S5 n4: no root grid known: coh skipped")
        mark = self.tail.mark()
        if root is not None:
            self.act("se", "coh", root, point, secs + (20.0 if not self.can("se", "fly") else 0.0))
        self.wait(0.5)
        if point is None:
            rows = self.tail.since(mark, "s5.coh")
            point = next((r.get("local") for r in rows if isinstance(r.get("local"), list)), None)
        if root is not None and point is not None:
            self.act("sn", "coh", self.root_hex(), point, secs + (20.0 if not self.can("se", "fly") else 0.0))
        self.part("n4", "begin")
        if self.can("se", "fly"):
            self.act("se", "fly", label="n4.fly")
            self.wait(N4["seconds"] + 0.3)
        else:
            self.owner("n4.fly", "fly straight at about 20 m/s, yawing gently left and right (about 1 s each way) for "
                                 "15 s", 15.0)
        self.part("n4", "end")
        if self.can("se", "cockpit"):
            self.act("se", "cockpit", False)

    def p_n5(self):
        """SE's worker-thread loader: de.s5 models {count 200, send false} (one s5.model row per model; S5.N5)"""
        self.part("n5", "begin")
        mark = self.tail.mark()
        self.act("se", "models", 200, False, timeout=60.0)
        t0 = time.monotonic()
        while time.monotonic() - t0 < (20.0 if self.q else 180.0) and self.alive():
            if len(self.tail.since(mark, "s5.model")) >= 200:
                break
            self.wait(1.0)
        self.part("n5", "end")

    def p_n6(self):
        """500, 2,000 and 5,000 blocks in turn underwater, 30 s each
 (5 s quick) with Subnautica's s5.scale rows, C7 samples and measure.ps1 (S5.N6.<size>)"""
        for size in SYNTH_SIZES:
            rep = self.spawn(size, back=N6_BACK, label="n6.%d" % size)
            if not self.ok(rep) and self.can("se", "spawn"):
                continue
            self.wait_view(("Underwater",), 20.0)
            self.part("n6.%d" % size, "begin")
            secs = 5.0 if self.q else 30.0
            if not (self.standin_sn and self.standin_se):
                self.measure("n6.%d" % size, secs)
            self.wait(secs + 1.0)
            self.part("n6.%d" % size, "end")

    def p_n7(self):
        """the character (S5b): SE de.s5 character on, Subnautica fpbody=se; a swim script (de.swim: model on,
 teleportChar under water, forward 8 s) in third then first person; s5.char /
 s5.rig rows (S5.N7a-c); fpbody back to diver"""
        x, z = SHALLOW
        self.part("n7", "begin")
        self.act("se", "character", True)
        self.act("sn", "fpbody", "se")
        # S5-2d: a SeBones phase first (swim model off: SE's own bones drive the rig, judged by S5.N7a's bone check),
 # then the swim model on for the Retarget phases
        self.call("se", "de.swim", {"model": "off", "rows": "on", "teleportChar": [x + 12.0, -8.0, z], "yawDeg": 0},
                  label="n7.seBones")
        self.wait(4.0)
        self.call("se", "de.swim", {"model": "on"}, label="n7.swimOn")
        self.wait(3.0)
        for mode in ("third", "first"):
            if not self.owner("n7.camera.%s" % mode, "switch SE to %s-person view (V) and keep the mouse still" % mode, 6.0):
                # S5-2c: without --owner the plugin switches SE's camera itself (de.swim camera), so N7a's third-person
 # entity rows and N7b's third-person rows exist in an unattended session
                self.call("se", "de.swim", {"camera": mode}, label="n7.camera.%s" % mode)
                self.wait(1.5)
            self.call("se", "de.swim", {"script": [{"label": "n7.fwd", "seconds": 8.0 if not self.q else 3.0,
                                                    "forward": 1.0}, {"label": "n7.release", "seconds": 1.0}],
                                        "label": "n7.%s" % mode}, label="n7.swim.%s" % mode)
            self.wait(9.5 if not self.q else 4.5)
        self.act("sn", "fpbody", "diver")
        self.part("n7", "end")

    def p_record(self):
        """SE's recorder: de.s5 record {name s5-<stamp>, c1, c2}, 10 s of the scene (the stand-in turns the rotor and
 paints), de.s5 record stop; the.derec checked (header hashes, framing, the sidecar's sha256)"""
        X = s5_stream_module(self.LCH)
        name = "s5-%s" % self.stamp
        self.part("record", "begin")
        rep = self.act("se", "record", name)
        if self.ok(rep):
            if self.can("se", "rotor"):
                self.act("se", "rotor", N3_RPM)
            self.wait(5.0)
            if self.can("se", "edit"):
                self.act("se", "edit", "paint")
            self.wait(5.0)
            if self.can("se", "rotor"):
                self.act("se", "rotor", 0.0)
            stop = self.act("se", "recordStop")
            path = X.recording_path(name)
            t0 = time.monotonic()
            while time.monotonic() - t0 < 15.0 and not os.path.exists(os.path.splitext(path)[0] + ".json"):
                self.wait(0.5)        # the SE writer thread writes the sidecar when it is done
            info, probs = None, ["file absent: %s" % path]
            if os.path.exists(path):
                info = X.summarize(path)
                probs = info.get("problems") or []
            self.sess.event("recording", name=name, path=path, stop=stop, summary=info, problems=probs)
            self.log.say("  S5 record %s: %s" % (name, "OK" if not probs else "; ".join(probs)), ev="s5_recording",
                         problems=probs)
        self.part("record", "end")

    def p_tp1(self):
        """the combined T-P1 states in this launch (--tp1-states, --tp1-seconds; the baselines need their own launches)"""
        states = [s for s in (self.a.tp1_states or ",".join(TP1_COMBINED)).split(",") if s.strip()]
        self.run_tp1(states, self.a.tp1_seconds)

    def p_teardown(self):
        """the ships removed, then Subnautica's world-loss teardown and count
 (de.s3 {action: teardown | count}: roots, preserved roots, our meshes; S5.N1.C12)"""
        self.part("teardown", "begin")
        if self.can("se", "teardown"):
            self.act("se", "teardown")
        else:
            self.owner("teardown", "delete the test ships", 30.0)
        self.wait(3.0)
        self.act("sn", "teardown")
        self.wait(2.0)
        self.act("sn", "count")
        self.spawned = None
        self.part("teardown", "end")

    # ---- T-P1 -------------------------------------------------------------------------------------------------------
    def run_tp1(self, states, seconds):
        secs = float(seconds if not self.q else min(seconds, 10.0))
        for st in states:
            if not self.alive():
                break
            if st == "base-se":
                for spot in TP1_BASE_SE_SPOTS:
                    self.tp1_state(spot, secs, part="tp1.base-se.%s" % spot)
                continue
            if st == "base-sn":
                self.tp1_state("underwater", secs, part="tp1.base-sn", ship=False)
                continue
            self.tp1_state(st, secs, part="tp1.%s" % ("ccx1" if getattr(self.a, "ccx1", False) else st))

    def tp1_state(self, state, secs, part, ship=True):
        (x, y, z, yaw), views, motion = TP1_PLAN[state]
        if state == "underwater" and ship and not self.se_alone and self.spawned is None and self.root is None:
            sx = x + secs * (motion or {}).get("speedMps", 5.0) * 0.5
            self.teleport(sx, y, z - SHIP_AHEAD, 0.0, label="%s.shipCamera" % part)
            self.wait(1.0)
            if self.can("se", "spawn"):
                rep = self.act("se", "spawn", self.ship_arg(TP1_SHIP), (sx, y, z + 30.0), 90.0,
                               label="%s.spawn" % part, timeout=30.0)
                if self.ok(rep):
                    self.spawned = rep
                    self.wait_mirrored(len(rep.get("grids") or []) or 1, 180.0 * (0.3 if self.q else 1.0), part)
            else:
                self.owner("%s.ship" % part, "spawn the 2,000-block test ship at SN (%g, %g, %g)" % (sx, y, z + 30.0),
                           60.0)
        self.teleport(x, y, z, yaw, label=part)
        got = None if self.se_alone else self.wait_view(views, 30.0)
        self.sess.event("tp1_view", part=part, want=list(views), seconds=got, view=self.view())
        self.hold(10.0, 2.0)
        if getattr(self.a, "owner", False) and getattr(self.a, "cmd", "") == "tp1":
            self.log.say("  OWNER: fly the %s state now (%d s); the launcher measures" % (state, secs),
                         ev="owner_prompt", part=part)
        self.part(part, "begin")
        if not (self.standin_sn and self.standin_se):
            self.measure(part, secs)
        if motion and not (getattr(self.a, "owner", False) and getattr(self.a, "cmd", "") == "tp1"):
            if motion["kind"] == "line":
                self.motion({"kind": "line", "fromSn": [x, y, z], "toSn": [x + motion["dx"] * motion["speedMps"] * secs,
                                                                            y, z], "speedMps": motion["speedMps"]}, part)
            else:
                self.motion({"kind": "bob", "x": x, "z": z, "centreY": motion["centreY"],
                             "amplitudeM": motion["amplitudeM"], "hz": motion["hz"], "seconds": secs}, part)
        self.wait(secs + 1.0)
        self.part(part, "end")

def _int_id(v):
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, int):
        return v & 0xFFFFFFFFFFFFFFFF
    if isinstance(v, float):
        return int(v)
    s = str(v).strip()
    try:
        return (int(s, 16) if s.lower().startswith("0x") else int(s)) & 0xFFFFFFFFFFFFFFFF
    except ValueError:
        return None

# ----------------------------------------------------------------------------------------------------------------
# sessions
# ----------------------------------------------------------------------------------------------------------------

def _mutant_plan(a, mutants):
    """(Subnautica env, SE env, producer args) for the session's mutants."""
    sn_m = [m for m in mutants if m in S5_SN_MUTANTS]
    sn_env = {"DEEPENGINEERS_MUTANT": ",".join(sn_m)} if sn_m else {}
    se_env, prod = {"DEEPENGINEERS_S5": "on"}, []   # review F9: the S5 sessions are the ones that want S5 on
    if "S5.M3" in mutants:
        if a.mode == "S5a" or getattr(a, "standin_producer", False):
            prod += ["--s5-mutant", "M3"]
        else:
            se_env["DEEPENGINEERS_MUTANTS"] = "S5.M3"
    if "S5.M1@producer" in mutants:
        prod += ["--s5-mutant", "M1"]
    return sn_env, se_env, prod

def producer_cmd(LCH, a, extra=()):
    cmd = [sys.executable, "-I", os.path.join(LCH.REPO, "tests", "standins", "fake_se_producer.py"), "--name", a.name,
           "--s5", "--s2", "--quiet", "--load-after", "1.0", "--load-seconds", "1.0", "--screen", a.screen,
           "--fov", "%g" % a.fov, "--s5-heading", "%g" % SHIP_HEADING]
    if getattr(a, "replay", None):
        cmd += ["--replay", a.replay]
    else:
        cmd += ["--synth", getattr(a, "synth", None) or ",".join(str(s) for s in SYNTH_SIZES)]
    return cmd + list(extra)

def s5_plan_text(a, parts, sn_env, se_env, prod) -> list:
    out = ["  mode %s; SE: %s" % (a.mode, "the fake producer (tests/standins/fake_se_producer.py --s5 --s2, %s)" % (
        "--replay %s" % a.replay if a.replay else "--synth %s" % (a.synth or "500,2000,5000"))
        if a.mode == "S5a" else "Space Engineers (Legacy.exe -continue)"),
        "  spot: ships at SN (%g, %g, %g) heading %g deg, camera %g m south looking north (n6: %g m); ships: %s" % (
            DEEP[0], SHIP_Y, DEEP[1], SHIP_HEADING, SHIP_AHEAD, N6_BACK,
            ", ".join("%d=%s" % kv for kv in sorted((a.ships_map or {}).items())) or "synthetic 500/2000/5000"),
        "  mutants: Subnautica env %s; SE env %s; producer %s" % (sn_env or "-", se_env or "-", " ".join(prod) or "-")]
    for p in parts:
        doc = (getattr(S5Runner, "p_" + p).__doc__ or "").strip().replace("\n", " ")
        out.append("  part %-9s %s" % (p, " ".join(doc.split())))
    out.append("  SE actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in S5Runner.SE_ACTIONS.items()))
    out.append("  Subnautica actions: " + ", ".join("%s=%s" % (k, v[0]) for k, v in S5Runner.SN_ACTIONS.items()))
    return out

def s5_analyze(LCH, log, a, out) -> int:
    AN = analyzer(LCH)
    try:
        rows, base = AN.evaluate(out, getattr(a, "baseline", None), getattr(a, "baseline_logs", None))
    except Exception as e:     # an analyser bug must not hide the session
        import traceback
        rows, base = [{"id": "S5", "status": "FAIL", "measured": "analyser error %s" % type(e).__name__,
                       "threshold": "-", "what": str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[-1][
                           :120], "part": "all", "required": True}], {}
    strict = not getattr(a, "lenient", False)
    ok = AN.write_results(out, rows, strict, {"tool": "launcher %s" % a.cmd, "tp1Baseline": base})
    print("\nS5 ORACLE TABLE (%s)  (* = not required; INFO = information)" % out)
    try:
        print(AN.C.table(rows))
    except UnicodeEncodeError:
        print(AN.C.table(rows).encode("ascii", "replace").decode("ascii"))
    log.say("S5 ORACLE %s: %d rows, %d FAIL, %d MISSING -> %s" % (
        "PASS" if ok else "FAIL", len(rows), sum(1 for r in rows if r["status"] == "FAIL" and r.get("required", True)),
        sum(1 for r in rows if r["status"] == "MISSING" and r.get("required", True)),
        os.path.join(out, "s5-results.json")), ev="s5_result", ok=ok)
    return 0 if ok else 1

def _ships_map(text):
    out = {}
    for item in (text or "").split(","):
        if "=" in item:
            k, v = item.split("=", 1)
            out[int(k.strip())] = v.strip()
    return out

def s5(LCH, log, a) -> int:
    S = LCH.S
    if a.list_mutant_groups:
        for name, g in S5_MUTANT_GROUPS.items():
            print("  %-8s mutants %-8s parts %-12s%s red %s" % (name, ",".join(g["mutants"]), ",".join(g["parts"]),
                                                               " states %s" % ",".join(g["states"]) if g.get("states")
                                                               else "", ",".join(g["red"])))
        return 0
    if a.analyze:
        return s5_analyze(LCH, log, a, a.analyze)
    a.ships_map = _ships_map(a.ships)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "s5", stamp)
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    parts = None
    if a.mutant_group:
        g = S5_MUTANT_GROUPS.get(a.mutant_group)
        if g is None:
            log.say("unknown mutant group %s (known: %s)" % (a.mutant_group, ",".join(S5_MUTANT_GROUPS)))
            return 2
        mutants = list(g["mutants"])
        parts = list(g["parts"])
        if g.get("states") and not a.tp1_states:
            a.tp1_states = ",".join(g["states"])
    if a.parts:
        parts = [x.strip() for x in a.parts.split(",") if x.strip()]
    parts = parts or list(S5_DEFAULT_PARTS[a.mode])
    unknown = [x for x in parts if x not in S5_PARTS]
    if unknown:
        log.say("unknown parts %s (known: %s)" % (unknown, ",".join(S5_PARTS)))
        return 2
    bad_m = [m for m in mutants if m not in S5_ALL_MUTANTS]
    if bad_m:
        log.say("unknown mutants %s (known: %s)" % (bad_m, ",".join(S5_ALL_MUTANTS)))
        return 2
    if "S5.M1@producer" in mutants and a.mode != "S5a":
        log.say("S5.M1@producer is the stand-in's form of S5.M1 (S5a only)")
        return 2
    sn_env, se_env, prod = _mutant_plan(a, mutants)
    # S7a hook: S5's oracles predate the out-of-water Retarget (S7a walk source `retarget`, CONTRACT §5.4 "C1 locomotion
 # extension"); S5.N7a judges SeBones seconds, so the s5 session keeps S5's source rule
    sn_env.setdefault("DEEPENGINEERS_SN_WALKSOURCE", "sebones")
    a.cont = not a.no_continue
    if a.mode == "S5a" and not a.se_cmd:
        a.se_cmd = json.dumps(producer_cmd(LCH, a, prod))
    sn_cmd, se_cmd = LCH.session_commands(a)
    log.say("S5 session %s (%s): parts %s%s%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, a.mode, ",".join(parts), " (quick)" if a.quick else "", " mutants %s" % ",".join(mutants) if mutants else
        "", subprocess.list2cmdline(sn_cmd), subprocess.list2cmdline(se_cmd), out), ev="s5_plan")
    if a.dry_run:
        for line in s5_plan_text(a, parts, sn_env, se_env, prod):
            print(line)
        if "tp1" in parts:
            print("  tp1 states %s, %g s each" % (a.tp1_states or ",".join(TP1_COMBINED), a.tp1_seconds))
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "s5-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", kind="s5", stamp=stamp, mode=a.mode, parts=parts, mutants=mutants, name=a.name,
               argv=sys.argv[1:], quick=a.quick, standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd,
               seCmd=se_cmd, qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, replay=a.replay,
               ships=a.ships_map, states=(a.tp1_states or ",".join(TP1_COMBINED)).split(",") if "tp1" in parts else [])
    S.fine_timer(True)
    t_start = time.time()
    rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        if rc is None and LCH.wait_in_world(log, sess, a, sn, se):
            runner = S5Runner(LCH, log, a, sess, sn, se, out, stamp, standin_sn, standin_se, t_start=t_start)
            ok, why = runner.attach()
            if not ok:
                log.say("S5: harness not usable: %s" % why, ev="s5_harness_refused")
                rc = 1
            else:
                runner.park()
                for m in mutants:     # the switch the plugins expose (the environment already set it at start)
                    if m == "S5.M1@producer":
                        continue
                    side = "sn" if m in S5_SN_MUTANTS else "se"
                    runner.act(side, "mutant", m, True, label="mutant.%s" % m)
                for p in parts:
                    if not runner.alive():
                        log.say("S5: a game exited before part %s" % p, ev="s5_exit_during_script")
                        break
                    try:
                        getattr(runner, "p_" + p)()
                    except Exception as e:
                        log.say("S5 part %s failed: %r" % (p, e), ev="s5_part_error", part=p)
                        sess.event("part_error", name=p, error=repr(e))
                log.say("S5 script finished: %s" % json.dumps(runner.stats), ev="s5_script_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, sn, se, standin_se, standin_sn)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, sn, standin_sn, standin_se, stamp)
        _collect_player_log(LCH, log, out, t_start, standin_sn)
    if rc is not None:
        log.say("S5 session aborted (rc %d); analysing what was collected" % rc)
    res = s5_analyze(LCH, log, a, out)
    return rc if rc is not None else res

def _collect_player_log(LCH, log, out, t_start, standin_sn):
    p = getattr(LCH, "PLAYER_LOG", None)
    if not standin_sn and p and os.path.exists(p) and os.path.getmtime(p) >= t_start - 5:
        try:
            import shutil
            shutil.copy2(p, os.path.join(out, "Player.log"))
        except OSError as e:
            log.say("  collect: Player.log not copied (%s)" % e)

def tp1(LCH, log, a) -> int:
    S = LCH.S
    if a.analyze:
        return s5_analyze(LCH, log, a, a.analyze)
    a.ships_map = _ships_map(a.ships)
    states = [s.strip() for s in (a.state or "").split(",") if s.strip()]
    if a.ccx1:
        states = ["underwater"]
    unknown = [s for s in states if s not in TP1_STATES]
    if not states or unknown:
        log.say("tp1 --state: one or more of %s (unknown: %s)" % (",".join(TP1_STATES), unknown or "none given"))
        return 2
    alone = [s for s in states if s.startswith("base")]
    if alone and len(states) > 1:
        log.say("tp1: %s needs its own launch (one game alone); run the combined states separately" % alone[0])
        return 2
    mutants = [m.strip() for m in (a.mutants or "").split(",") if m.strip()]
    bad_m = [m for m in mutants if m not in S5_ALL_MUTANTS]
    if bad_m:
        log.say("unknown mutants %s" % bad_m)
        return 2
    a.mode = "S5b"
    sn_env, se_env, prod = _mutant_plan(a, mutants)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(LCH.REPO, "tests", "out", "tp1", stamp + "-" + ("ccx1" if a.ccx1 else "-".join(states)))
    a.cont = not a.no_continue
    se_alone = states == ["base-se"]
    if states == ["base-sn"] and not a.se_cmd:
        a.synth = a.synth or "500"                # the producer only drives the camera (no ship is spawned)
        a.se_cmd = json.dumps(producer_cmd(LCH, a, prod))
    sn_cmd, se_cmd = LCH.session_commands(a)
    secs = a.seconds if not a.quick else min(a.seconds, 10.0)
    log.say("T-P1 %s: states %s, %g s each%s; Subnautica: %s; SE: %s; out %s" % (
        stamp, ",".join(states), secs, " (CCX1 affinity 0xFF00)" if a.ccx1 else "",
        "none (SE alone: a passive stand-in host keeps the section)" if se_alone else subprocess.list2cmdline(sn_cmd),
        subprocess.list2cmdline(se_cmd), out), ev="tp1_plan")
    if a.dry_run:
        for st in states:
            for sub in ((["base-se.%s" % s for s in TP1_BASE_SE_SPOTS]) if st == "base-se" else
                        (["ccx1"] if a.ccx1 else [st])):
                key = sub.split(".")[-1] if sub.startswith("base-se") else ("underwater" if sub in ("base-sn", "ccx1")
                                                                              else sub)
                (x, y, z, yaw), views, motion = TP1_PLAN[key]
                print("  state %-22s teleport (%g, %g, %g) yaw %g, wait view %s, motion %s, measure.ps1 1 s + section 10 "
                      "Hz + commit 1 Hz for %g s" % ("tp1." + sub, x, y, z, yaw, "/".join(views),
                                                    json.dumps(motion) if motion else "none (hold)", secs))
        print("  mutants: Subnautica env %s; SE env %s; producer %s" % (sn_env or "-", se_env or "-",
                                                                       " ".join(prod) or "-"))
        log.say("dry run: nothing started")
        return 0
    os.makedirs(out, exist_ok=True)
    sess = LCH.S2SessionLog(os.path.join(out, "tp1-session.jsonl"))
    standin_sn, standin_se = bool(a.sn_cmd), bool(a.se_cmd)
    sess.event("start", kind="tp1", stamp=stamp, states=states, parts=["tp1"], mutants=mutants, name=a.name, band=list(BAND),
               argv=sys.argv[1:], seconds=secs, ccx1=a.ccx1, quick=a.quick, seAlone=se_alone,
               standin={"sn": standin_sn, "se": standin_se}, snCmd=sn_cmd, seCmd=se_cmd,
               qpcFrequency=S.QPC_FREQUENCY, snEnv=sn_env, seEnv=se_env, memory=perf_info())
    S.fine_timer(True)
    t_start = time.time()
    if se_alone:
        rc, sn, se = LCH.start_se_alone(log, a, out, se_extra_env=se_env)
    else:
        rc, sn, se = LCH.start_games(log, a, stdout_dir=out, sn_extra_env=sn_env, se_extra_env=se_env)
    runner = None
    try:
        ready = rc is None and (LCH.wait_se_in_world(log, a, se, a.world_timeout) if se_alone else
                                LCH.wait_in_world(log, sess, a, sn, se))
        if ready:
            if a.ccx1 and sn is not None:
                ok, err = set_affinity(sn.pid, 0xFF00)
                sess.event("affinity", pid=sn.pid, mask="0xFF00", ok=ok, error=err)
                log.say("  T-P1 --ccx1: Subnautica pid %d affinity 0xFF00: %s" % (sn.pid, "ok" if ok else err))
            runner = S5Runner(LCH, log, a, sess, None if se_alone else sn, se, out, stamp, standin_sn, standin_se,
                              se_alone=se_alone, t_start=t_start)
            ok, why = runner.attach()
            if not ok:
                log.say("T-P1: harness not usable: %s" % why, ev="tp1_harness_refused")
                rc = 1
            else:
                runner.park()
                for m in mutants:
                    if m != "S5.M1@producer":
                        runner.act("sn" if m in S5_SN_MUTANTS else "se", "mutant", m, True)
                runner.run_tp1(states, a.seconds)
                log.say("T-P1 finished: %s" % json.dumps(runner.stats), ev="tp1_done", stats=runner.stats)
        elif rc is None:
            rc = 1
    finally:
        if runner:
            runner.close()
        if rc is not None and sn is not None and se is None:
            log.say("SE was not started; Subnautica pid %d left as it is (launcher.py stop closes it)" % sn.pid)
        elif not a.keep_running:
            LCH.close_session(log, a, None if se_alone else sn, se, standin_se, standin_sn)
        if se_alone and sn is not None and sn.poll() is None:
            S.terminate_pid(sn.pid, 0)        # the passive stand-in host (our own Python process, never a game)
        S.fine_timer(False)
        sess.event("end", rc=rc, stats=runner.stats if runner else None,
                   unsupported=sorted("%s.%s" % k for k in runner.unsupported) if runner else None)
        sess.close()
        LCH.collect(log, out, t_start, None if se_alone else sn, standin_sn or se_alone, standin_se, stamp)
    if rc is not None:
        log.say("T-P1 session aborted (rc %d); analysing what was collected" % rc)
    res = s5_analyze(LCH, log, a, out)
    return rc if rc is not None else res

# ----------------------------------------------------------------------------------------------------------------
# argparse and dispatch (called by launcher.py)
# ----------------------------------------------------------------------------------------------------------------

def _common(p, LCH):
    p.add_argument("--ready-phase", default="ReadyIdle", choices=("Menu", "ReadyIdle"))
    p.add_argument("--timeout", type=float, default=LCH.L.READY_WAIT_S, help="Subnautica ready wait (s)")
    p.add_argument("--world-timeout", type=float, default=600.0, help="SE world load + hello wait (s)")
    p.add_argument("--no-continue", action="store_true", help="do not pass -continue to Legacy.exe")
    p.add_argument("--skip-preflight", action="store_true")
    p.add_argument("--no-create-backup", action="store_true")
    p.add_argument("--refresh-saves-backup", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="print the plan; start nothing")
    p.add_argument("--sn-cmd", help="JSON list replacing the Subnautica command (stand-in plumbing only)")
    p.add_argument("--se-cmd", help="JSON list replacing the SE command (S5a default: the fake producer)")
    p.add_argument("--any-sn-pid", action="store_true")
    p.add_argument("--mutants", default="", help="comma list of %s" % ",".join(S5_ALL_MUTANTS))
    p.add_argument("--ships", default="", help="the owner's blueprints: 500=NAME,2000=NAME,5000=NAME (S5b; default: "
                                               "the SE plugin's synthesised ships through de.s5 spawn {ship: N})")
    p.add_argument("--screen", default="2560x1440", help="fake producer screen (S5a; its pixel maths)")
    p.add_argument("--fov", type=float, default=70.0, help="fake producer vertical FOV (S5a)")
    p.add_argument("--quick", action="store_true", help="short holds (stand-in dry runs)")
    p.add_argument("--owner", action="store_true", help="T-P1: the owner flies each state (no scripted motion)")
    p.add_argument("--baseline", default=None, help="T-P1: a base-se folder (SE alone) for the 5 %% rule")
    p.add_argument("--baseline-logs", default=None, help="a folder whose LogOutput.log / Player.log are the baseline")
    p.add_argument("--out", default=None)
    p.add_argument("--keep-running", action="store_true")
    p.add_argument("--cmd-timeout", type=float, default=5.0)
    p.add_argument("--lenient", action="store_true", help="MISSING data does not fail the oracle table")
    p.add_argument("--analyze", default=None, metavar="DIR", help="only re-run the S5 oracles on a collected folder")

def add_parsers(sub, LCH):
    p = sub.add_parser("s5", help="one scripted S5 'Whole grids + character' session (S5a: Subnautica + the fake "
                                  "producer replaying or synthesising ships; S5b: both games): the S5 oracle table")
    p.add_argument("--mode", choices=("S5a", "S5b"), default="S5a",
                   help="S5a: SE is tests/standins/fake_se_producer.py --s5 --s2 (unless --se-cmd); S5b: both games")
    _common(p, LCH)
    p.add_argument("--parts", default="", help="comma list of %s (default per mode)" % ",".join(S5_PARTS))
    p.add_argument("--mutant-group", default=None, help="a runbook group: %s" % ",".join(S5_MUTANT_GROUPS))
    p.add_argument("--list-mutant-groups", action="store_true")
    p.add_argument("--replay", default=None, metavar="NAME|FILE|DIR", help="S5a: the producer replays this .derec")
    p.add_argument("--synth", default=None, help="S5a: ship sizes the producer builds (default 500,2000,5000)")
    p.add_argument("--seed", type=int, default=7, help="n1: the random parts' seed")
    p.add_argument("--tp1-states", default="", help="tp1 part: states (default %s)" % ",".join(TP1_COMBINED))
    p.add_argument("--tp1-seconds", type=float, default=300.0, help="tp1 part: seconds per state")
    p = sub.add_parser("tp1", help="T-P1 states (ROADMAP S5.T-P1): measure.ps1, section samples, commit charge, both "
                                   "plugins' per-second JSONL; base-se and base-sn in their own launches")
    _common(p, LCH)
    p.add_argument("--state", default="", help="comma list of %s" % ",".join(TP1_STATES))
    p.add_argument("--seconds", type=float, default=300.0, help="per state (ROADMAP: 5 min)")
    p.add_argument("--ccx1", action="store_true", help="Subnautica on CCX1 (affinity 0xFF00), underwater; experiment")
    p.add_argument("--replay", default=None, help=argparse_suppress())
    p.add_argument("--synth", default=None, help=argparse_suppress())

def argparse_suppress():
    import argparse
    return argparse.SUPPRESS

def run(LCH, log, a) -> int:
    if a.cmd == "s5":
        return s5(LCH, log, a)
    if a.cmd == "tp1":
        return tp1(LCH, log, a)
    return 2
