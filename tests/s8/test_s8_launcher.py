"""S8 minimum, launcher side: the exit path, the renderer restart
and the settings restore of src/DeepEngineers.Launcher/launcher_s8.py, without a game.

 python -I tests/s8/test_s8_launcher.py [--quick] # --quick: the pure parts only (no stand-in processes)

Pure parts: ExitWatch (normal exit, SE gone and Subnautica hanging: one CloseMainWindow at 15 s, a kill OFFER at 30 s,
never a kill; a 30 s heartbeat hang of either game; 150 s in a loading phase; re-armed when responding again; the clean
end), relaunch_check (refuse without SE, wait for the old PID, the crash handler and the 20 s cool-down, refuse a section
owned by another Subnautica), the settings readers / writers on fixture files (every other byte kept, UTF-8 with or
without BOM, UTF-16 with BOM) and restore_plan (the verb; the next clean start restores only bridge-made values after
an unclean end).
Stand-in parts (tests/standins/fake_sn_host.py and fake_se_producer.py as the two games, own section names; every
stand-in ends by itself, no process is killed): `run` with a normal SE exit (seExiting seen, Subnautica quits by itself
after SE, the session clean); `run` with a Subnautica that does not follow SE (the CloseMainWindow and the kill offer
printed, nothing killed, its own exit code 0); `restart-renderer` while the SE stand-in holds the section (it waits for
the old PID, then the cool-down, starts the new renderer, which re-initialises the section with generation + 1).
Prints PASS/FAIL per check; exit code 0 only when every check passes. Logs in tests/out/s8/<stamp>/.
"""
from __future__ import annotations

import datetime
import json
import os
import re
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
LAUNCHER_DIR = os.path.join(REPO, "src", "DeepEngineers.Launcher")
WIRE = os.path.join(REPO, "tests", "wire")
STANDINS = os.path.join(REPO, "tests", "standins")
for _p in (LAUNCHER_DIR, WIRE):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import launcher_s8 as S8  # noqa: E402
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

LAUNCHER = os.path.join(LAUNCHER_DIR, "launcher.py")
HOST = os.path.join(STANDINS, "fake_sn_host.py")
SEP = os.path.join(STANDINS, "fake_se_producer.py")
OUT = os.path.join(REPO, "tests", "out", "s8", datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
RESULTS = []
_n = [0]

def check(cid: str, ok: bool, text: str = ""):
    RESULTS.append((cid, bool(ok)))
    print("%s %s %s" % ("PASS" if ok else "FAIL", cid, text), flush=True)

def name(tag: str) -> str:
    _n[0] += 1
    return "Local\\DeepEngineers_v1_s8t%d_%d_%s" % (os.getpid(), _n[0], tag)

# ----------------------------------------------------------------------------------------------------------------
# pure: ExitWatch
# ----------------------------------------------------------------------------------------------------------------

def run_watch(w, steps):
    out = []
    for t, kw in steps:
        for e in w.step(t, **kw):
            out.append((t, e))
    return out

def test_exitwatch():
    # normal exit: SE writes seExiting, exits; Subnautica quits 3 s later by itself
    w = S8.ExitWatch(hang_s=30, ask_s=15)
    ev = run_watch(w, [(0, dict(se_alive=True, sn_alive=True, sn_hb_age=0.1, se_hb_age=0.1, se_linked=True)),
                       (10, dict(se_alive=True, sn_alive=True, se_exiting=True, se_linked=True)),
                       (10.5, dict(se_alive=False, sn_alive=True)),
                       (13.5, dict(se_alive=False, sn_alive=False))])
    check("S8.L.exit.normal", [e for _t, e in ev] == ["se_exit", "sn_exit", "done"] and w.clean() and w.offers == 0, str(ev))
    # SE gone, Subnautica hangs: CloseMainWindow at 15 s, the offer at 30 s, never repeated, never a kill event
    w = S8.ExitWatch(hang_s=30, ask_s=15)
    steps = [(0, dict(se_alive=True, sn_alive=True, se_linked=True))] + \
            [(float(t), dict(se_alive=False, sn_alive=True)) for t in range(1, 80)]
    ev = run_watch(w, steps)
    names = [e for _t, e in ev]
    times = {e: t for t, e in ev}
    check("S8.L.exit.hangAfterSe", names == ["se_exit", "ask_sn_quit", "offer_kill_sn"] and times["ask_sn_quit"] == 16.0 and
          times["offer_kill_sn"] == 31.0 and not any("kill" in e and not e.startswith("offer") for e in names), str(ev))
    check("S8.L.exit.uncleanWithoutSeExiting", not w.clean(), "linked, no seExiting -> unclean")
    # a hung Subnautica while SE runs (heartbeat 30 s old, process alive), then responding again; SE in a long load
    w = S8.ExitWatch(hang_s=30, ask_s=15, loading_hang_s=150)
    ev = run_watch(w, [(0, dict(se_alive=True, sn_alive=True, sn_hb_age=29.9)),
                       (1, dict(se_alive=True, sn_alive=True, sn_hb_age=30.0)),
                       (2, dict(se_alive=True, sn_alive=True, sn_hb_age=31.0)),
                       (3, dict(se_alive=True, sn_alive=True, sn_hb_age=0.1)),
                       (4, dict(se_alive=True, sn_alive=True, se_hb_age=100.0, se_loading=True)),
                       (5, dict(se_alive=True, sn_alive=True, se_hb_age=150.0, se_loading=True)),
                       (6, dict(se_alive=True, sn_alive=True, sn_hb_age=60.0, sn_loading=True))])
    check("S8.L.exit.hangHeartbeat", [e for _t, e in ev] == ["offer_kill_sn", "responding_sn", "offer_kill_se"], str(ev))
    # SE never linked (no section): a clean end for the settings
    w = S8.ExitWatch()
    run_watch(w, [(0, dict(se_alive=False, sn_alive=False))])
    check("S8.L.exit.neverLinkedClean", w.clean())

# ----------------------------------------------------------------------------------------------------------------
# pure: relaunch_check
# ----------------------------------------------------------------------------------------------------------------

def test_relaunch_check():
    base = dict(se_alive=True, old_sn_pid=100, old_sn_alive=False, crash_handlers=[], since_exit_s=25.0, cooldown_s=20.0,
                section="present", recorded_sn_pid=100, recorded_sn_alive=False, sn_holders=[])
    cases = [
        ("go", {}),
        ("refuse", {"se_alive": False}),
        ("wait", {"old_sn_alive": True}),
        ("wait", {"crash_handlers": [4242]}),
        ("wait", {"since_exit_s": 19.9}),
        ("wait", {"since_exit_s": None}),
        ("refuse", {"recorded_sn_alive": True, "recorded_sn_pid": 200}),
        ("refuse", {"sn_holders": [300]}),
        ("refuse", {"section": "error"}),
        ("go", {"section": "absent"}),
    ]
    bad = []
    for want, over in cases:
        f = dict(base)
        f.update(over)
        got, why = S8.relaunch_check(f)
        if got != want:
            bad.append((over, got, why))
    check("S8.L.relaunch.table", not bad, str(bad) if bad else "%d cases" % len(cases))
    w, why = S8.relaunch_check(dict(base, crash_handlers=[4242]))
    check("S8.L.relaunch.crashHandlerText", "4242" in why and "kill --pid" in why, why)

# ----------------------------------------------------------------------------------------------------------------
# settings fixtures
# ----------------------------------------------------------------------------------------------------------------

CFG = """<?xml version="1.0" encoding="utf-8"?>
<MyObjectBuilder_Config xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Dictionary>
    <dictionary>
      <item>
        <Key>GameVolume</Key>
        <Value>
          <Value xsi:type="xsd:string">%s</Value>
        </Value>
      </item>
      <item>
        <Key>Music_Volume</Key>
        <Value>
          <Value xsi:type="xsd:string">%s</Value>
        </Value>
      </item>
      <item>
        <Key>VoiceChatVolume</Key>
        <Value>
          <Value xsi:type="xsd:string">5</Value>
        </Value>
      </item>
    </dictionary>
  </Dictionary>
</MyObjectBuilder_Config>
"""

WATER = """<?xml version="1.0" encoding="utf-16"?>
<WaterClientSettings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Quality>1.5</Quality>
  <Volume>%s</Volume>
  <Silent>false</Silent>
</WaterClientSettings>"""

def test_settings():
    d = os.path.join(OUT, "settings")
    os.makedirs(d, exist_ok=True)
    cfg, water, bdir = os.path.join(d, "SpaceEngineers.cfg"), os.path.join(d, "WaterClientSettings.xml"), os.path.join(d, "restore")
    with open(cfg, "wb") as f:
        f.write(b"\xef\xbb\xbf" + (CFG % ("0.8", "0.6")).replace("\n", "\r\n").encode("utf-8"))
    with open(water, "wb") as f:
        f.write((WATER % "1").encode("utf-8"))
    vals = S8.flat_values(S8.read_cfg_audio(cfg), S8.read_water_volume(water))
    check("S8.L.settings.read", vals == {"secfg.GameVolume": "0.8", "secfg.Music_Volume": "0.6", "secfg.VoiceChatVolume": "5",
                                          "water.Volume": "1"}, str(vals))
    before = open(cfg, "rb").read()
    changed = S8.write_cfg_audio(cfg, {"GameVolume": "0.56"}, bdir)
    after = open(cfg, "rb").read()
    check("S8.L.settings.writeKeepsBytes", changed == [("0.8", "0.56")] and after.startswith(b"\xef\xbb\xbf") and
          after.replace(b"0.56", b"0.8") == before and len(os.listdir(bdir)) == 1, str(changed))
    check("S8.L.settings.writeNoopWhenEqual", S8.write_cfg_audio(cfg, {"GameVolume": "0.56"}, bdir) == [] and len(os.listdir(bdir)) == 1)
    # UTF-16 with BOM round trip
    w16 = os.path.join(d, "w16.xml")
    with open(w16, "wb") as f:
        f.write(b"\xff\xfe" + (WATER % "0").encode("utf-16-le"))
    S8.write_water_volume(w16, "0.75")
    check("S8.L.settings.utf16", S8.read_water_volume(w16) == "0.75" and open(w16, "rb").read().startswith(b"\xff\xfe"))

    backup = {"secfg.GameVolume": "0.8", "secfg.Music_Volume": "0.6", "secfg.VoiceChatVolume": "5", "water.Volume": "1"}
    cur = {"secfg.GameVolume": "0.56", "secfg.Music_Volume": "0", "secfg.VoiceChatVolume": "4", "water.Volume": "0"}
    verb = S8.restore_plan(backup, cur, "verb")
    check("S8.L.settings.planVerb", [k for k, _c, _t, _w in verb] == ["secfg.GameVolume", "secfg.Music_Volume", "secfg.VoiceChatVolume", "water.Volume"], str(verb))
    clean = S8.restore_plan(backup, cur, "clean-start", backup, {"clean": True})
    unclean = S8.restore_plan(backup, cur, "clean-start", backup, {"clean": False})
    unknown = S8.restore_plan(backup, cur, "clean-start", backup, None)
    check("S8.L.settings.planCleanStart", clean == [] and [k for k, *_r in unclean] == ["water.Volume", "secfg.GameVolume", "secfg.Music_Volume"]
          and unknown == unclean, "unclean: %s" % unclean)
    own = S8.restore_plan(backup, {"secfg.GameVolume": "0.3", "secfg.Music_Volume": "0.6", "water.Volume": "0.5"}, "clean-start", backup, {"clean": False})
    check("S8.L.settings.ownChangesKept", own == [], "the player's own values are never restored at start: %s" % own)
    # the files as an unclean end leaves them (the "cur" values), then the plan of the next clean start
    with open(cfg, "wb") as f:
        f.write((CFG % ("0.56", "0")).encode("utf-8"))
    with open(water, "wb") as f:
        f.write((WATER % "0").encode("utf-8"))
    done = S8.apply_plan(unclean, cfg, water, bdir)
    vals = S8.flat_values(S8.read_cfg_audio(cfg), S8.read_water_volume(water))
    check("S8.L.settings.apply", vals == backup and len(done) == 3, str(done))

    # the restore-settings verb on the fixtures (overrides keep it away from the real files)
    with open(cfg, "wb") as f:
        f.write((CFG % ("0.1", "0.6")).encode("utf-8"))
    bpath = os.path.join(d, "settings-backup.json")
    with open(bpath, "w", encoding="utf-8") as f:
        json.dump({"values": backup}, f)
    r = subprocess.run([sys.executable, "-I", LAUNCHER, "restore-settings", "--only", "water,secfg", "--cfg", cfg, "--water", water,
                        "--backup", bpath], capture_output=True, text=True, timeout=60)
    check("S8.L.settings.verbDryRun", r.returncode == 0 and "secfg.GameVolume 0.1 -> 0.8" in r.stdout and "dry run" in r.stdout and
          S8.read_cfg_audio(cfg)["GameVolume"] == "0.1", r.stdout.strip()[-200:])
    r = subprocess.run([sys.executable, "-I", LAUNCHER, "restore-settings", "--only", "water,secfg", "--cfg", cfg, "--water", water,
                        "--backup", bpath, "--yes"], capture_output=True, text=True, timeout=60)
    check("S8.L.settings.verbYes", r.returncode == 0 and S8.read_cfg_audio(cfg)["GameVolume"] == "0.8", r.stdout.strip()[-200:])

# ----------------------------------------------------------------------------------------------------------------
# stand-in processes
# ----------------------------------------------------------------------------------------------------------------

class Run:
    """A command whose stdout lines are collected with their arrival time (monotonic)."""

    def __init__(self, label, cmd, env=None):
        self.label = label
        self.lines = []
        self.f = open(os.path.join(OUT, label + ".txt"), "w", encoding="utf-8")
        e = dict(os.environ)
        e.pop(L.BRIDGE_ENV, None)
        e.update(env or {})
        self.p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                                  errors="replace", env=e, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.t = threading.Thread(target=self._read, daemon=True)
        self.t.start()

    def _read(self):
        for line in self.p.stdout:
            self.lines.append((time.monotonic(), line.rstrip("\n")))
            self.f.write(line)
            self.f.flush()

    def wait(self, timeout):
        try:
            self.p.wait(timeout)
        except subprocess.TimeoutExpired:
            return None
        self.t.join(5)
        return self.p.returncode

    def text(self):
        return "\n".join(l for _t, l in self.lines)

    def first(self, rx):
        for t, l in self.lines:
            if re.search(rx, l):
                return t, l
        return None, None

def py(script, *args):
    return [sys.executable, "-I", script] + [str(a) for a in args]

def test_run_normal_exit():
    n = name("run")
    sn = json.dumps(py(HOST, "--name", n, "--quit-on-se-exit", "--quit-delay", "1.0", "--ready-after", "0.3", "--hold-seconds", "60", "--quiet"))
    se = json.dumps(py(SEP, "--name", n, "--run-seconds", "4", "--quiet"))
    r = Run("run-normal", py(LAUNCHER, "--name", n, "run", "--skip-preflight", "--timeout", "30", "--sn-cmd", sn, "--se-cmd", se))
    rc = r.wait(60)
    out = r.text()
    t_se, _l = r.first(r"^SE exited")
    t_sn, l_sn = r.first(r"^Subnautica exited")
    gap = (t_sn - t_se) if (t_se is not None and t_sn is not None) else None
    check("S8.L.run.normal", rc == 0 and "normal exit (seExiting written before the process ended)" in out and
          "session end: clean" in out and "never ends a game by itself" not in out, "rc=%s, SN %s" % (rc, l_sn))
    check("S8.L.run.snAfterSe", gap is not None and 0 <= gap <= S8.ASK_QUIT_AFTER_SE_S, "Subnautica followed SE in %s s" % (
        "%.1f" % gap if gap is not None else "?"))

def test_run_hang_offer():
    n = name("hang")
    sn = json.dumps(py(HOST, "--name", n, "--ready-after", "0.3", "--hold-seconds", "10", "--quiet"))   # ignores SE's exit
    se = json.dumps(py(SEP, "--name", n, "--run-seconds", "2", "--quiet"))
    r = Run("run-hang", py(LAUNCHER, "--name", n, "run", "--skip-preflight", "--timeout", "30", "--sn-cmd", sn, "--se-cmd", se),
            env={"DEEPENG_S8_HANG_S": "3", "DEEPENG_S8_ASK_S": "1.5"})
    rc = r.wait(60)
    out = r.text()
    m = re.search(r"Subnautica exited \(code (\S+)\)", out)
    check("S8.L.run.hangOffer", rc == 0 and "CloseMainWindow(" in out and "The launcher never ends a game by itself" in out and
          "kill --pid" in out and "killed" not in out, "rc=%s" % rc)
    check("S8.L.run.noKill", m is not None and m.group(1) == "0", "the stand-in ended by itself with code %s" % (m.group(1) if m else "?"))

def test_restart_renderer():
    n = name("relaunch")
    host1 = Run("relaunch-host1", py(HOST, "--name", n, "--no-token-check", "--ready-after", "0.3", "--hold-seconds", "6", "--quiet"))
    sec = None
    for _ in range(100):
        sec = S.Section.open(n, readonly=True)
        if sec is not None and sec.hdr("magic") == L.MAGIC:
            break
        if sec is not None:
            sec.close()
            sec = None
        time.sleep(0.1)
    if sec:
        sec.close()
    se = Run("relaunch-se", py(SEP, "--name", n, "--run-seconds", "22", "--quiet"))
    time.sleep(1.5)
    sn2 = json.dumps(py(HOST, "--name", n, "--quit-on-se-exit", "--ready-after", "0.3", "--hold-seconds", "60", "--quiet"))
    t0 = time.monotonic()
    r = Run("relaunch-launcher", py(LAUNCHER, "--name", n, "restart-renderer", "--se-pid", se.p.pid, "--sn-cmd", sn2, "--cooldown", "3",
                                    "--no-foreground-wait", "--no-wait-exits", "--timeout", "30", "--wait", "60"))
    rc = r.wait(90)
    out = r.text()
    h1 = host1.wait(5)
    t_h1 = None
    for t, l in host1.lines:
        t_h1 = t   # the old renderer's last output line (its quit)
    t_wait, _ = r.first(r"WAIT: the old Subnautica")
    t_cool, _ = r.first(r"WAIT: cool-down")
    t_go, l_go = r.first(r"GO: old renderer gone")
    t_start, _ = r.first(r"Subnautica started: pid")
    gen = pid = None
    sec = S.Section.open(n, readonly=True)
    if sec is not None:
        try:
            pid, gen = sec.identity("sn")
        finally:
            sec.close()
    m = re.search(r"Subnautica started: pid (\d+)", out)
    check("S8.L.relaunch.waitsOldPid", t_wait is not None and h1 is not None and (t_cool is None or t_cool > t_wait), "old renderer exit code %s" % h1)
    gone = float(re.search(r"gone (\d+)", l_go).group(1)) if l_go else -1
    check("S8.L.relaunch.cooldown", t_go is not None and t_start is not None and t_start >= t_go and gone >= 3 - 0.5, l_go or "no GO")
    check("S8.L.relaunch.newGeneration", rc == 0 and m is not None and pid == int(m.group(1)) and gen == 2,
          "rc=%s snPid=%s gen=%s (creator rule 3 re-initialised the SE-held section)" % (rc, pid, gen))
    check("S8.L.relaunch.noKill", "killed" not in out)
    # every stand-in ends by itself: the SE stand-in at 22 s (seExiting), the new renderer follows it
    se.wait(40)
    left = [p for p in S.list_processes() if p["pid"] in (host1.p.pid, se.p.pid)]
    check("S8.L.relaunch.standinsEnded", not left, "left: %s" % left)
    _ = t0

def test_restart_refused_without_se():
    r = subprocess.run([sys.executable, "-I", LAUNCHER, "--name", name("nose"), "restart-renderer", "--se-pid", "4", "--dry-run",
                        "--no-foreground-wait", "--wait", "1"], capture_output=True, text=True, timeout=60)
    check("S8.L.relaunch.refusedWithoutSe", r.returncode == 1 and "REFUSE: Space Engineers is not running" in r.stdout, r.stdout.strip()[-160:])

def main() -> int:
    quick = "--quick" in sys.argv
    os.makedirs(OUT, exist_ok=True)
    test_exitwatch()
    test_relaunch_check()
    test_settings()
    test_restart_refused_without_se()
    if not quick:
        test_run_normal_exit()
        test_run_hang_offer()
        test_restart_renderer()
    fails = [c for c, ok in RESULTS if not ok]
    print("S8 LAUNCHER: %d checks, %d FAIL%s (logs %s)" % (len(RESULTS), len(fails), (": " + ", ".join(fails)) if fails else "", OUT), flush=True)
    return 0 if not fails else 1

if __name__ == "__main__":
    sys.exit(main())
