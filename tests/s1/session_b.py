"""Session B, the scripted parts (docs/S1-STATUS.md section 7.4): S1.N3 streaming jumps, the drive gate, the S1.N9
capture, and the S1.N12 spy part.N8 swim/sit) is not here.

 python -I tests/s1/session_b.py run --launch --stop [--parts n12,n3,gate,n9] [--out DIR]
 python -I tests/s1/session_b.py run # the games are already running (launcher.py run --continue)
 python -I tests/s1/session_b.py analyze <DIR> # the table again from a collected folder
 python -I tests/s1/session_b.py --selftest # the analyser on synthetic logs (seeded failures must be red)

What it does (no game window is touched; every command goes through C11 with tools/lab/se_cmd.py):
- --launch: starts `launcher.py run --continue` as a child (its output in <out>/launcher.txt). The launcher's
 preflight needs backups newer than the last DE session (`um backup create...` first) and refuses otherwise.
- n12: polls the section every 0.2 s (read-only) and records the local time at which sePhase first read
 LoadingWorld and InWorld; the SE log's `DE.Phase Menu -> LoadingWorld` stamp must be within 1 s of it.
- waits until C7 reads snPhase Active (C1 drives Subnautica's camera: the site is OK and SE is in the world);
- n3: `de.teleport` to (0,-150,0), then 0.5 km, 1 km and 2 km away (SN metres, yaw 0), waiting 15/20/20/20 s;
 Subnautica logs `DE.Stream jump dist=... settledMs= camBoxMs= fwdBoxMs=` per jump;
- gate: teleports to 8,000 m (beyond the 7,900 m dive zone) and to +400 m (above +350 m), back to (0,-150,0) in
 between (5 s each): `DE.Cam gate HOLD: rho... (FAR)`, `... y... > +350 m...`, and `DE.Cam gate OPEN` after each
 return;
- n9: teleport to (-1029,-150,-1116), `de.capture path=s1manual/n9` to Subnautica: the PNG + sidecar JSON under
 %LOCALAPPDATA%\\DeepEngineers\\captures, the next `DE.Audio 60s... suppressed{...}` line, C7 uiHidden;
- --stop: `launcher.py stop` (SE closed normally, Subnautica quits by itself), then the logs and the capture are
 copied into <out> (default tests/out/s1/sessionB-<stamp>/) and the table is printed and written as
 sessionB-results.json. Exit 0 when every required row is PASS.
Stdlib only (plus the repository's Python stand-in modules).
"""
from __future__ import annotations

import argparse
import datetime
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (os.path.join(_ROOT, "tools", "lab"), os.path.join(_ROOT, "tests", "wire")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402
import se_cmd  # noqa: E402

LAUNCHER = os.path.join(_ROOT, "src", "DeepEngineers.Launcher", "launcher.py")
SUBNAUTICA = r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica"
LOGOUTPUT = os.path.join(SUBNAUTICA, "BepInEx", "LogOutput.log")
SE_LOG_DIR = os.path.join(os.environ.get("APPDATA", ""), "SpaceEngineers")
CAPTURES = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "captures")
OUT_ROOT = os.path.join(_ROOT, "tests", "out", "s1")
ALL_PARTS = ("n12", "n3", "gate", "n9")

# (x, y, z, yawDeg, seconds to wait after) -- SN metres, y up
N3_STEPS = [(0, -150, 0, 0, 15), (500, -150, 0, 0, 20), (1500, -150, 0, 0, 20), (-500, -150, 0, 0, 20)]
N3_JUMPS_M = (500.0, 1000.0, 2000.0)
GATE_STEPS = [(0, -150, 0, 0, 5), (8000, -150, 0, 0, 5), (0, -150, 0, 0, 5), (0, 400, 0, 0, 5), (0, -150, 0, 0, 5)]
N9_POINT = (-1029, -150, -1116, 0, 5)
N9_CAPTURE = "s1manual/n9"
JUMP_TOL = 0.15                 # the measured jump distance against the commanded one
LOAD_STAMP_TOL_S = 1.0          # S1.N12 spy part: sePhase LoadingWorld seen within 1 s of the SE log stamp
AUDIO_WAIT_S = 75.0             # one DE.Audio 60s line per minute in the host world

def say(msg):
    sys.stderr.write("%s %s\n" % (datetime.datetime.now().strftime("%H:%M:%S"), msg))
    sys.stderr.flush()

def now_iso():
    return datetime.datetime.now().isoformat(timespec="milliseconds")

# ------------------------------------------------------------------------------------------------------ section reads
def open_ro(name):
    try:
        return S.Section.open(name, readonly=True)
    except OSError:
        return None

def read_c7(sec):
    st, seq, data = S.seqlock_read(sec, L.REGION["C7"].offset, 256)
    if not data:
        return None
    v = L.SN_STATUS.unpack(data)
    flags = [k for k, b in L.SN_STATUS_FLAGS.items() if v["flags"] & (1 << b)]
    return {"snPhase": L.SN_PHASES.get(v["snPhase"], v["snPhase"]), "flags": sorted(flags), "snFrame": v["snFrame"],
            "presentedSeTick": v["presentedSeTick"], "seq": seq}

class LoadWatcher(threading.Thread):
    """S1.N12 spy part: the local time at which sePhase first read LoadingWorld, then InWorld (0.2 s polling)."""

    def __init__(self, name, timeout_s):
        super().__init__(daemon=True)
        self.name_ = name
        self.timeout_s = timeout_s
        self.loading_at = None
        self.inworld_at = None
        self.samples = 0
        self.stop = threading.Event()

    def run(self):
        t0 = time.monotonic()
        sec = None
        try:
            while not self.stop.is_set() and time.monotonic() - t0 < self.timeout_s:
                if sec is None:
                    sec = open_ro(self.name_)
                    if sec is None:
                        time.sleep(0.5)
                        continue
                if sec.mapped_size >= L.HEADER_SIZE and sec.hdr("magic") == L.MAGIC:
                    self.samples += 1
                    ph = sec.hdr("sePhase")
                    if ph == L.SE_PHASE["LoadingWorld"] and self.loading_at is None:
                        self.loading_at = now_iso()
                    if ph == L.SE_PHASE["InWorld"] and self.loading_at is not None:
                        self.inworld_at = now_iso()
                        return
                time.sleep(0.2)
        finally:
            if sec is not None:
                sec.close()

def read_c5(sec):
    st, seq, data = S.seqlock_read(sec, L.REGION["C5"].offset, 256)
    if not data:
        return None
    v = L.SE_STATE.unpack(data)
    return {"worldEpoch": v["worldEpoch"], "siteHashOk": v["siteHash"] == L.SITE_HASH_V1, "Rs": v["Rs"], "seq": seq}

def read_c1_tick(sec):
    st, seq, data = S.seqlock_read(sec, L.REGION["C1"].offset, L.SE_FRAME.size)
    return L.SE_FRAME.unpack(data)["tick"] if data else None

def wait_ready(name, timeout_s):
    """Until C7 reads snPhase ReadyIdle or later (host world loaded, camera rig ready) and the SE block reads InWorld
 with the site bound (C5 worldEpoch >= 1, siteHash v1). WarmingUp/Active are S2 phases: in S1 Subnautica idles in
 ReadyIdle (S1Host). Returns {"c7", "c5", "sePhase"} or None."""
    t0 = time.monotonic()
    last = None
    sec = None
    ready_phases = {L.SN_PHASE[p] for p in ("ReadyIdle", "WarmingUp", "Active")}
    try:
        while time.monotonic() - t0 < timeout_s:
            if sec is None:
                sec = open_ro(name)
                if sec is None:
                    time.sleep(1.0)
                    continue
            if sec.mapped_size >= L.SECTION_SIZE and sec.hdr("magic") == L.MAGIC:
                c7, c5 = read_c7(sec), read_c5(sec)
                se_phase = L.SE_PHASES.get(sec.hdr("sePhase"), sec.hdr("sePhase"))
                state = (c7["snPhase"] if c7 else None, se_phase)
                if state != last:
                    last = state
                    say("snPhase %s, sePhase %s after %.0f s" % (state[0], state[1], time.monotonic() - t0))
                if c7 and L.SN_PHASE.get(c7["snPhase"]) in ready_phases and se_phase == "InWorld" and c5 and \
                        c5["worldEpoch"] >= 1 and c5["siteHashOk"]:
                    return {"c7": c7, "c5": c5, "sePhase": se_phase}
            time.sleep(1.0)
        return None
    finally:
        if sec is not None:
            sec.close()

def wait_driven(name, timeout_s):
    """After the arrival teleport: until C7 presentedSeTick is within 4 ticks of the C1 tick (the camera is driven from
 C1). Returns the seconds it took, or None."""
    t0 = time.monotonic()
    sec = open_ro(name)
    if sec is None:
        return None
    try:
        while time.monotonic() - t0 < timeout_s:
            c7, tick = read_c7(sec), read_c1_tick(sec)
            if c7 and tick and c7["presentedSeTick"] >= tick - 4:
                return round(time.monotonic() - t0, 2)
            time.sleep(0.25)
        return None
    finally:
        sec.close()

# ------------------------------------------------------------------------------------------------------ commands
def send(name, cmd, args, to="se", timeout=30.0):
    rc, rep = se_cmd.run(name, cmd, args, timeout, True, to)
    line = (rep or {}).get("line") or (rep or {}).get("error") or ""
    say("%s %s -> rc %d %s" % (to, cmd, rc, line[:140]))
    return rc, rep

def teleport_steps(name, steps, events, label):
    ok = True
    for x, y, z, yaw, wait in steps:
        rc, rep = send(name, "de.teleport", {"x": x, "y": y, "z": z, "yawDeg": yaw})
        events.append({"ev": "teleport", "part": label, "at": now_iso(), "to": [x, y, z], "yawDeg": yaw, "rc": rc,
                       "reply": rep})
        ok = ok and rc == 0
        time.sleep(wait)
    return ok

def count_lines(path, needle):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return sum(1 for ln in f if needle in ln)
    except OSError:
        return 0

# ------------------------------------------------------------------------------------------------------ run
def launch(out, name):
    os.makedirs(out, exist_ok=True)
    f = open(os.path.join(out, "launcher.txt"), "w", encoding="utf-8")
    cmd = [sys.executable, "-I", LAUNCHER, "run", "--continue"]
    say("launch: " + subprocess.list2cmdline(cmd))
    return subprocess.Popen(cmd, stdout=f, stderr=subprocess.STDOUT, cwd=_ROOT,
                            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0))

def stop_games():
    say("launcher.py stop")
    r = subprocess.run([sys.executable, "-I", LAUNCHER, "stop"], capture_output=True, text=True, cwd=_ROOT, timeout=300)
    for ln in (r.stdout + r.stderr).splitlines()[-6:]:
        say("  " + ln)
    return r.returncode

def newest_se_log(after_epoch):
    logs = [p for p in glob.glob(os.path.join(SE_LOG_DIR, "SpaceEngineers_*.log")) if os.path.getmtime(p) >= after_epoch - 5]
    return max(logs, key=os.path.getmtime) if logs else None

def run(a):
    parts = [p.strip() for p in a.parts.split(",") if p.strip()]
    for p in parts:
        if p not in ALL_PARTS:
            say("unknown part %r (known: %s)" % (p, ", ".join(ALL_PARTS)))
            return 4
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out = a.out or os.path.join(OUT_ROOT, "sessionB-" + stamp)
    os.makedirs(out, exist_ok=True)
    t_start = time.time()
    events = []
    results = {"stamp": stamp, "parts": parts, "events": events, "startedAt": now_iso()}
    watcher = None
    if "n12" in parts:
        watcher = LoadWatcher(a.name, a.timeout + 300)
        watcher.start()
    proc = None
    if a.launch:
        if open_ro(a.name) is not None:
            say("REFUSE: the section exists already (a game is running): --launch needs both games closed")
            return 3
        proc = launch(out, a.name)
    say("waiting for snPhase ReadyIdle + SE InWorld with the site bound (%.0f s)" % a.timeout)
    ready = wait_ready(a.name, a.timeout)
    if ready is None:
        say("not ready in time: nothing driven (launcher.txt, LogOutput.log, the SE log)")
        results["ready"] = None
    else:
        results["ready"] = ready
        say("ready: C7 flags %s, C5 worldEpoch %d Rs %r" % (",".join(ready["c7"]["flags"]), ready["c5"]["worldEpoch"],
                                                            ready["c5"]["Rs"]))
        time.sleep(3.0)
        # arrival: SE's camera may be anywhere in the world; C1 is off-site until the spectator is at the site
        teleport_steps(a.name, [(0, -150, 0, 0, 1)], events, "arrive")
        results["drivenAfterS"] = wait_driven(a.name, 30.0)
        say("camera driven from C1 %s" % ("after %.2f s" % results["drivenAfterS"] if results["drivenAfterS"] is not None
                                            else "NOT within 30 s (C7 presentedSeTick stale)"))
        time.sleep(5.0)
        if "n3" in parts:
            results["n3Ok"] = teleport_steps(a.name, N3_STEPS, events, "n3")
        if "gate" in parts:
            results["gateOk"] = teleport_steps(a.name, GATE_STEPS, events, "gate")
        if "n9" in parts:
            audio_before = count_lines(LOGOUTPUT, "DE.Audio 60s")
            teleport_steps(a.name, [N9_POINT], events, "n9")
            rc, rep = send(a.name, "de.capture", {"path": N9_CAPTURE}, to="sn")
            events.append({"ev": "capture", "at": now_iso(), "rc": rc, "reply": rep})
            png = (rep or {}).get("path")
            results["capturePath"] = png
            if png:
                t0 = time.monotonic()
                while time.monotonic() - t0 < 30 and not (os.path.exists(png) and os.path.exists(png + ".json")):
                    time.sleep(0.5)
                say("capture files present: %s" % (os.path.exists(png) and os.path.exists(png + ".json")))
            t0 = time.monotonic()
            while time.monotonic() - t0 < AUDIO_WAIT_S and count_lines(LOGOUTPUT, "DE.Audio 60s") <= audio_before:
                time.sleep(2.0)
            sec = open_ro(a.name)
            if sec is not None:
                results["c7AtEnd"] = read_c7(sec)
                sec.close()
            say("C7 at the end: %s" % json.dumps(results.get("c7AtEnd")))
    if watcher is not None:
        watcher.stop.set()
        watcher.join(2.0)
        results["load"] = {"loadingAt": watcher.loading_at, "inWorldAt": watcher.inworld_at, "samples": watcher.samples}
        say("S1.N12 spy: LoadingWorld seen at %s, InWorld at %s (%d samples)" % (watcher.loading_at, watcher.inworld_at,
                                                                                 watcher.samples))
    if a.stop:
        stop_games()
        if proc is not None:
            t0 = time.monotonic()
            while proc.poll() is None and time.monotonic() - t0 < 120:
                time.sleep(1.0)
            say("launcher run exited: %s" % proc.poll())
    # collect
    try:
        shutil.copy2(LOGOUTPUT, os.path.join(out, "BepInEx-LogOutput.log"))
    except OSError as e:
        say("LogOutput.log not copied: %s" % e)
    se_log = newest_se_log(t_start)
    if se_log:
        try:
            shutil.copy2(se_log, os.path.join(out, os.path.basename(se_log)))
        except OSError as e:
            say("SE log not copied: %s" % e)
    results["seLog"] = os.path.basename(se_log) if se_log else None
    png = results.get("capturePath")
    if png and os.path.exists(png):
        for p in (png, png + ".json"):
            if os.path.exists(p):
                shutil.copy2(p, os.path.join(out, os.path.basename(p)))
    with open(os.path.join(out, "sessionB-run.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1, sort_keys=True, default=str)
    say("collected into " + out)
    return analyze_dir(out)

# ------------------------------------------------------------------------------------------------------ analysis
RE_JUMP = re.compile(r"DE\.Stream jump dist=([0-9.]+)m to=(?:\([^)]*\)|\S+) \(([^)]*)\) settledMs=(\S+)(?: busy=(\S+))? "
                     r"camBoxMs=(\S+) fwdBoxMs=(\S+)(.*)")
RE_HOLD = re.compile(r"DE\.Cam gate HOLD: (.*?) at SN \(")
RE_OPEN = re.compile(r"DE\.Cam gate OPEN at SN")
RE_CAPTURE = re.compile(r"DE\.Capture (OK|FAILED) .*?full=(\S+) ")
RE_AUDIO = re.compile(r"DE\.Audio 60s (.*?) suppressed\{(.*?)\}")
# The SE plugin publishes LoadingWorld on the section from the MyGuiScreenLoading.RunLoad prefix (logged at once as
# "DE.Phase LoadingWorld (MyGuiScreenLoading.RunLoad prefix)"); the "DE.Phase Menu -> LoadingWorld" transition line is
# written by the update thread, which RunLoad blocks for the whole load. The spy is
# compared with the earliest of the two.
RE_SE_LOAD = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) .*DE\.Phase (?:Menu -> LoadingWorld|LoadingWorld \(MyGuiScreenLoading\.RunLoad prefix\))")

def analyze(sn_log_text, se_log_text, run_info, files_exist):
    """Rows from the texts (unit-testable). files_exist(path) -> bool."""
    rows = []
    parts = run_info.get("parts", list(ALL_PARTS))

    def row(check, status, measured, threshold, what, required=True):
        rows.append({"check": check, "status": status, "measured": measured, "threshold": threshold, "what": what,
                     "required": required})

    sn_lines = sn_log_text.splitlines()
    if any(p in parts for p in ("n3", "gate", "n9")):
        d = run_info.get("drivenAfterS")
        row("drive", "PASS" if d is not None else "FAIL", ("C7 fresh %.2f s after the arrival teleport" % d) if d is not None
            else "C7 presentedSeTick never within 4 ticks of C1", "fresh within 30 s",
            "Subnautica's camera driven from C1 once the spectator is at the site")
    if "n3" in parts:
        jumps = []
        for ln in sn_lines:
            m = RE_JUMP.search(ln)
            if m:
                tail = m.group(7)
                mb = re.search(r"camBuiltHalf=(-?\d+)", tail)
                md = re.search(r"cam\d+=(\S+)", tail)
                jumps.append({"dist": float(m.group(1)), "why": m.group(2), "settledMs": m.group(3), "camBoxMs": m.group(5),
                              "fwdBoxMs": m.group(6), "timeout": "TIMEOUT" in tail, "busy": m.group(4) == "yes",
                              "builtHalf": mb.group(1) if mb else "-", "cam56": md.group(1) if md else "-"})
        for want in N3_JUMPS_M:
            hits = [j for j in jumps if abs(j["dist"] - want) <= JUMP_TOL * want]
            good = [j for j in hits if not j["timeout"] and "never" not in (j["settledMs"], j["camBoxMs"], j["fwdBoxMs"])]
            j = good[-1] if good else (hits[-1] if hits else None)
            measured = ("settled %s camBox %s fwdBox %s ms, busy %s, builtHalf %s, cam56 %s%s" % (
                j["settledMs"], j["camBoxMs"], j["fwdBoxMs"], "yes" if j["busy"] else "no", j["builtHalf"], j["cam56"],
                " TIMEOUT" if j["timeout"] else "")) if j else "no DE.Stream jump line"
            row("S1.N3", "PASS" if good else "FAIL", measured, "all three times, no TIMEOUT",
                "streaming after a %.0f m camera jump (dist within %d %%)" % (want, JUMP_TOL * 100))
        row("S1.N3", "INFO", "%d jump lines: %s" % (len(jumps), ", ".join("%.0f m" % j["dist"] for j in jumps)), "-",
            "every DE.Stream jump line of the session", required=False)
    if "gate" in parts:
        holds, opens, seq = [], 0, []
        for ln in sn_lines:
            m = RE_HOLD.search(ln)
            if m:
                holds.append(m.group(1))
                seq.append("HOLD")
            elif RE_OPEN.search(ln):
                opens += 1
                seq.append("OPEN")
        far = [h for h in holds if "(FAR)" in h or "C5 zone FAR" in h]
        high = [h for h in holds if "> +350" in h]
        row("gate", "PASS" if far else "FAIL", far[-1] if far else "no HOLD at 8,000 m", "HOLD rho > 7900 m or C5 zone FAR",
            "drive gate holds at 8,000 m east")
        row("gate", "PASS" if high else "FAIL", high[-1] if high else "no HOLD above +350 m", "HOLD y > +350 m",
            "drive gate holds at +400 m")
        row("gate", "PASS" if opens >= 2 else "FAIL", "%d OPEN, %d HOLD (%s)" % (opens, len(holds), " ".join(seq[-8:])),
            ">= 2 OPEN after the returns", "the gate re-opens after each return to (0,-150,0)")
    if "n9" in parts:
        caps = [RE_CAPTURE.search(ln) for ln in sn_lines]
        caps = [m for m in caps if m and N9_CAPTURE.replace("/", os.sep) in m.group(2) or (m and N9_CAPTURE in m.group(2))]
        ok = [m for m in caps if m.group(1) == "OK"]
        png = ok[-1].group(2) if ok else (run_info.get("capturePath") or "")
        present = bool(png) and files_exist(png) and files_exist(png + ".json")
        row("S1.N9", "PASS" if ok and present else "FAIL",
            ("DE.Capture OK " + png) if ok else ("no DE.Capture OK for " + N9_CAPTURE), "capture OK + PNG + sidecar JSON",
            "manual capture at (-1029,-150,-1116): look at it (no uGUI pixels)")
        audios = [RE_AUDIO.search(ln) for ln in sn_lines]
        audios = [m for m in audios if m]
        row("S1.N9", "PASS" if audios else "FAIL", ("suppressed{%s}" % audios[-1].group(2)) if audios else "no DE.Audio 60s line",
            "one DE.Audio 60s line", "UI/audio suppression summary logged every 60 s in the host world")
        errs = sum(1 for ln in sn_lines if "ErrorMessage" in ln)
        row("S1.N9", "PASS" if errs == 0 else "FAIL", "%d ErrorMessage lines" % errs, "0", "no Subnautica ErrorMessage shown")
        c7 = run_info.get("c7AtEnd") or {}
        flags = c7.get("flags") or []
        row("S1.N9", "PASS" if "uiHidden" in flags else "FAIL", "C7 flags %s" % ",".join(flags) if flags else "C7 not read",
            "uiHidden set", "C7 uiHidden while in the host world")
        row("S1.N11", "PASS" if "runInBackgroundForced" in flags else "FAIL", "runInBackgroundForced %s" % ("runInBackgroundForced" in flags),
            "set", "Subnautica runs in the background (C7)", required=False)
    if "n12" in parts:
        load = run_info.get("load") or {}
        seen = load.get("loadingAt")
        stamp = None
        for ln in se_log_text.splitlines():
            m = RE_SE_LOAD.match(ln)
            if m:
                stamp = m.group(1)
                break
        if seen and stamp:
            t_seen = datetime.datetime.fromisoformat(seen)
            t_log = datetime.datetime.strptime(stamp, "%Y-%m-%d %H:%M:%S.%f")
            d = (t_seen - t_log).total_seconds()
            row("S1.N12", "PASS" if abs(d) <= LOAD_STAMP_TOL_S else "FAIL", "spy %+.3f s after the SE log stamp %s" % (d, stamp),
                "within %.1f s" % LOAD_STAMP_TOL_S,
                "sePhase LoadingWorld seen on the section within 1 s of the SE log's first LoadingWorld line (RunLoad prefix)")
        else:
            row("S1.N12", "FAIL", "spy %s, SE log stamp %s" % (seen or "not seen", stamp or "absent"),
                "both present", "sePhase LoadingWorld seen on the section within 1 s of the SE log's first LoadingWorld line")
    return rows

def render(rows):
    w = [max(len(str(r[k])) for r in rows) for k in ("check", "status", "measured", "threshold")]
    out = ["%-*s  %-*s  %-*s  %-*s  what" % (w[0], "check", w[1], "status", w[2], "measured", w[3], "threshold")]
    for r in rows:
        out.append("%-*s  %-*s  %-*s  %-*s  %s%s" % (w[0], r["check"], w[1], r["status"], w[2], r["measured"], w[3],
                                                   r["threshold"], r["what"], "" if r["required"] else " (*)"))
    return "\n".join(out)

def analyze_dir(out):
    def read(p):
        try:
            with open(p, "r", encoding="utf-8", errors="replace") as f:
                return f.read()
        except OSError:
            return ""
    info_path = os.path.join(out, "sessionB-run.json")
    info = json.loads(read(info_path) or "{}")
    sn = read(os.path.join(out, "BepInEx-LogOutput.log"))
    se = read(os.path.join(out, info.get("seLog") or "")) if info.get("seLog") else ""
    rows = analyze(sn, se, info, os.path.exists)
    print("SESSION B TABLE (%s)  (* = not required)" % out)
    print(render(rows))
    req = [r for r in rows if r["required"]]
    fails = [r for r in req if r["status"] != "PASS"]
    verdict = "PASS" if req and not fails else "FAIL"
    print("SESSION B %s: %d rows, %d required, %d FAIL" % (verdict, len(rows), len(req), len(fails)))
    with open(os.path.join(out, "sessionB-results.json"), "w", encoding="utf-8") as f:
        json.dump({"verdict": verdict, "rows": rows}, f, indent=1, sort_keys=True)
    return 0 if verdict == "PASS" else 1

# ------------------------------------------------------------------------------------------------------ selftest
GOOD_SN = """[Info   :DeepEngineers.SN] DE.Stream jump dist=500.0m to=(500.0, -150.0, 0.0) (teleport) settledMs=812 camBoxMs=1203 fwdBoxMs=1410
[Info   :DeepEngineers.SN] DE.Stream jump dist=1000.0m to=(1500.0, -150.0, 0.0) (teleport) settledMs=1560 camBoxMs=2100 fwdBoxMs=2300
[Info   :DeepEngineers.SN] DE.Stream jump dist=2000.0m to=(-500.0, -150.0, 0.0) (teleport) settledMs=2400 camBoxMs=3100 fwdBoxMs=3300
[Warning:DeepEngineers.SN] DE.Cam gate HOLD: rho 8000 m > 7900 m (FAR) at SN (8000.0, -150.0, 0.0): camera and Player held
[Info   :DeepEngineers.SN] DE.Cam gate OPEN at SN (0.0, -150.0, 0.0): C1 drives the camera and the Player again (#2)
[Warning:DeepEngineers.SN] DE.Cam gate HOLD: y 400.0 m > +350 m (above the Approach band) at SN (0.0, 400.0, 0.0): camera and Player held
[Info   :DeepEngineers.SN] DE.Cam gate OPEN at SN (0.0, -150.0, 0.0): C1 drives the camera and the Player again (#3)
[Info   :DeepEngineers.SN] DE.Capture OK manual s1manual/n9 full=C:\\cap\\s1manual\\n9.png snFrame=1 unityFrame=2 seTickUsed=3 outcome=Fresh alpha=0.5 (written)
[Info   :DeepEngineers.SN] DE.Audio 60s events=12 suppressed{uiHidden=true hideApplications=true} saveBlocked=0
"""
GOOD_SE = "2026-10-12 07:00:00.500 - Thread:   1 ->  DE.Phase Menu -> LoadingWorld worldEpoch=1 target=MyGuiScreenGamePlay\n"
GOOD_INFO = {"parts": list(ALL_PARTS), "c7AtEnd": {"flags": ["uiHidden", "runInBackgroundForced", "worldLoaded"]},
             "load": {"loadingAt": "2026-10-12T07:00:01.100"}, "capturePath": "C:\\cap\\s1manual\\n9.png",
             "drivenAfterS": 0.75}

def selftest():
    results = []

    def check(label, cond, detail=""):
        results.append(cond)
        print("SELFTEST %-52s %s %s" % (label, "PASS" if cond else "FAIL", detail))

    exists = lambda p: p.startswith("C:\\cap\\")  # noqa: E731
    rows = analyze(GOOD_SN, GOOD_SE, GOOD_INFO, exists)
    req = [r for r in rows if r["required"]]
    bad = [r for r in req if r["status"] != "PASS"]
    check("good logs: every required row PASS", not bad, "%d rows%s" % (len(rows), "".join(
        "; %s %s: %s" % (r["check"], r["status"], r["measured"]) for r in bad)))
    rows = analyze(GOOD_SN.replace("settledMs=2400", "settledMs=never") + "", GOOD_SE, GOOD_INFO, exists)
    check("mutant: 2 km jump never settled -> S1.N3 FAIL", any(r["check"] == "S1.N3" and r["status"] == "FAIL" and "2000" in r["what"] for r in rows))
    rows = analyze("\n".join(l for l in GOOD_SN.splitlines() if "(#3)" not in l), GOOD_SE, GOOD_INFO, exists)
    check("mutant: one OPEN missing -> gate FAIL", any(r["check"] == "gate" and r["status"] == "FAIL" for r in rows))
    rows = analyze(GOOD_SN.replace("DE.Capture OK", "DE.Capture FAILED"), GOOD_SE, GOOD_INFO, exists)
    check("mutant: capture FAILED -> S1.N9 FAIL", any(r["check"] == "S1.N9" and r["status"] == "FAIL" for r in rows))
    rows = analyze(GOOD_SN, GOOD_SE, GOOD_INFO, lambda p: False)
    check("mutant: PNG missing on disk -> S1.N9 FAIL", any(r["check"] == "S1.N9" and r["status"] == "FAIL" and "capture OK" in r["threshold"] for r in rows))
    info = json.loads(json.dumps(GOOD_INFO))
    info["c7AtEnd"]["flags"] = ["worldLoaded"]
    rows = analyze(GOOD_SN, GOOD_SE, info, exists)
    check("mutant: C7 uiHidden clear -> S1.N9 FAIL", any(r["check"] == "S1.N9" and r["status"] == "FAIL" and "uiHidden" in r["threshold"] for r in rows))
    info = json.loads(json.dumps(GOOD_INFO))
    info["load"]["loadingAt"] = "2026-10-12T07:00:02.100"
    rows = analyze(GOOD_SN, GOOD_SE, info, exists)
    check("mutant: spy 1.6 s late -> S1.N12 FAIL", any(r["check"] == "S1.N12" and r["status"] == "FAIL" for r in rows))
    rows = analyze(GOOD_SN + "[Error  : Unity Log] ErrorMessage: something\n", GOOD_SE, GOOD_INFO, exists)
    check("mutant: an ErrorMessage line -> S1.N9 FAIL", any(r["check"] == "S1.N9" and r["status"] == "FAIL" and "ErrorMessage" in r["what"] for r in rows))
    se2 = ("2026-10-12 07:00:00.500 - Thread:   1 ->  DE.Phase LoadingWorld (MyGuiScreenLoading.RunLoad prefix) target=X worldEpoch=1\n"
           "2026-10-12 07:00:24.000 - Thread:   1 ->  DE.Phase Menu -> LoadingWorld worldEpoch=1 target=X\n")
    rows = analyze(GOOD_SN, se2, GOOD_INFO, exists)
    check("RunLoad prefix line is the reference (spy 0.6 s after it) -> S1.N12 PASS",
          any(r["check"] == "S1.N12" and r["status"] == "PASS" for r in rows))
    info = json.loads(json.dumps(GOOD_INFO))
    info["load"]["loadingAt"] = "2026-10-12T07:00:24.300"
    rows = analyze(GOOD_SN, se2, info, exists)
    check("mutant: spy only at the late transition line -> S1.N12 FAIL",
          any(r["check"] == "S1.N12" and r["status"] == "FAIL" for r in rows))
    info = json.loads(json.dumps(GOOD_INFO))
    info["drivenAfterS"] = None
    rows = analyze(GOOD_SN, GOOD_SE, info, exists)
    check("mutant: camera never driven -> drive FAIL", any(r["check"] == "drive" and r["status"] == "FAIL" for r in rows))
    ok = all(results)
    print("SESSION_B SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(results), len(results)))
    return 0 if ok else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="mode")
    p = sub.add_parser("run")
    p.add_argument("--parts", default=",".join(ALL_PARTS))
    p.add_argument("--launch", action="store_true", help="start launcher.py run --continue (both games closed)")
    p.add_argument("--stop", action="store_true", help="launcher.py stop at the end")
    p.add_argument("--out", default=None)
    p.add_argument("--name", default=L.SECTION_NAME)
    p.add_argument("--timeout", type=float, default=420.0, help="wait for snPhase Active (s)")
    p = sub.add_parser("analyze")
    p.add_argument("dir")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.mode == "run":
        return run(a)
    if a.mode == "analyze":
        return analyze_dir(a.dir)
    ap.print_usage(sys.stderr)
    return 4

if __name__ == "__main__":
    sys.exit(main())
