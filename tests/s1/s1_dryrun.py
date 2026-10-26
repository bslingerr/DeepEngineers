"""S1 stand-in dry run (synthetic test, ROADMAP §3 rule 2): the S1 oracles and the launcher's `s1` session against the
two Python stand-ins talking through a real section (a test name, never Local\\DeepEngineers_v1). No game is started.

 python -I tests/s1/s1_dryrun.py [--only selftest,base,analyze,mapdir,naive,prevtick,auto] [--full]

Runs, each with its expected outcome:
- selftest check_wm.py, latency.py and fmod_events.py self-tests (synthetic logs in memory; each has its own mutants)
- base `launcher.py s1 --quick` (all parts) with tests/standins/fake_sn_host.py --s1 and fake_se_producer.py
 --s1: every required row of the S1 oracle table PASS (WM-2, WM-3, WM-4 with the placement rows, G1-E5, N1,
 N2, N7, N9 PDA, G2-E4, N4 on a scratch "host slot", N5 and N13 from the stand-ins' emulated lines)
- analyze `launcher.py s1 --analyze <base>` gives the same verdict from the collected folder
- mapdir the same session with the SE stand-in's `--mutant mapdir-origin` (mapdir at the site entity origin instead
 of at the camera point, the S0.M4 mutant on the camera): check_wm red on WM-2 forward, WM-4 and G1-E5
- naive `--mutant naive-map` (S1.M1): WM-2 misses by ~19.6 m, WM-3 y drifts to ~-133.3 m: red
- prevtick `--mutant prev-tick` (S1.M2: the previous tick's camera, stamped at publication, as the SE plugin does it)
 against the base run (--baseline): the S1.N2 step p50 shifts by ~1 tick (SHIFT); S1.N1's transport is
 reported as information
- auto the stand-ins alone with `fake_se_producer.py --s1-script wm2,wm3,wm4,yaw` (no launcher, no harness):
 check_wm.py (wm2, wm3, wm4) and latency.py PASS on their logs
Exit code 0 only when every item has its expected outcome. Outputs: tests/out/s1/dryrun-<stamp>/<item>/.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import subprocess
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import check_wm as W  # noqa: E402
import latency as LAT  # noqa: E402
import fmod_events as FM  # noqa: E402

PY = sys.executable
LAUNCHER = os.path.join(_REPO, "src", "DeepEngineers.Launcher", "launcher.py")
HOST = os.path.join(_REPO, "tests", "standins", "fake_sn_host.py")
SE = os.path.join(_REPO, "tests", "standins", "fake_se_producer.py")
RESULTS = []
_n = [0]

def record(item, ok, text):
    RESULTS.append((item, ok, text))
    print("%s %-9s %s" % ("PASS" if ok else "FAIL", item, text), flush=True)

def section_name(tag):
    _n[0] += 1
    return "Local\\DeepEngineers_v1_s1dry%d_%d_%s" % (os.getpid(), _n[0], tag)

def host_cmd(name, out, extra=()):
    return [PY, "-I", HOST, "--name", name, "--s1", "--quit-on-se-exit", "--captures-dir", os.path.join(out, "captures"),
            "--fmod-every", "1", "--quiet"] + list(extra)

def se_cmd(name, extra=()):
    return [PY, "-I", SE, "--name", name, "--s1", "--quiet"] + list(extra)

def launcher_s1(item, out, parts=None, se_extra=(), baseline=None, full=False):
    name = section_name(item)
    os.makedirs(out, exist_ok=True)
    slot = os.path.join(out, "fake-host-slot")
    os.makedirs(slot, exist_ok=True)
    with open(os.path.join(slot, "gameinfo.json"), "w", encoding="utf-8") as f:
        json.dump({"note": "scratch stand-in slot for the S1.N4 hash check", "gameMode": 1}, f)
    cmd = [PY, "-I", LAUNCHER, "--name", name, "s1", "--skip-preflight", "--out", out, "--host-slot-dir", slot,
           "--sn-cmd", json.dumps(host_cmd(name, out)), "--se-cmd", json.dumps(se_cmd(name, se_extra))]
    if not full:
        cmd.append("--quick")
    if parts:
        cmd += ["--parts", parts]
    if baseline:
        cmd += ["--baseline", baseline]
    t0 = time.monotonic()
    env = dict(os.environ)
    env.pop("DEEPENGINEERS_BRIDGE", None)
    p = subprocess.run(cmd, cwd=_REPO, env=env, capture_output=True, text=True, timeout=900)
    with open(os.path.join(out, "launcher-stdout.txt"), "w", encoding="utf-8") as f:
        f.write(p.stdout + "\n--- stderr ---\n" + p.stderr)
    res = None
    rp = os.path.join(out, "s1-results.json")
    if os.path.exists(rp):
        with open(rp, encoding="utf-8") as f:
            res = json.load(f)
    return p.returncode, res, time.monotonic() - t0

def rows_of(res, cid=None, status=None):
    return [r for r in (res or {}).get("rows", []) if (cid is None or r["id"] == cid) and
            (status is None or r["status"] == status)]

def required_bad(res):
    return [r for r in (res or {}).get("rows", []) if r.get("required", True) and r["status"] in ("FAIL", "MISSING")]

def item_selftest():
    rc = W.selftest(), LAT.selftest(), FM.selftest()
    record("selftest", rc == (0, 0, 0), "check_wm %s, latency %s, fmod_events %s" % tuple(
        "PASS" if x == 0 else "FAIL" for x in rc))

def item_base(root, full):
    out = os.path.join(root, "base")
    rc, res, secs = launcher_s1("base", out, full=full)
    bad = required_bad(res)
    must = ("S1.WM-2", "S1.WM-3", "S1.WM-4", "S1.G1-E5", "S1.N1", "S1.N2", "S1.N7", "S1.N9", "S1.N4", "S1.N5",
            "S1.N13", "S1.G2-E4")
    missing = [m for m in must if not rows_of(res, m, "PASS")]
    ok = rc == 0 and res is not None and res.get("ok") and not bad and not missing
    lat = (res or {}).get("latency", {})
    record("base", ok, "launcher s1 rc %s in %.0f s; %d rows, %d required FAIL/MISSING%s; transport p95 %s ms, "
                       "extrapolated %s, step p50/p95 %s/%s ms" % (
                           rc, secs, len((res or {}).get("rows", [])), len(bad),
                           (" (%s)" % "; ".join("%s %s %s" % (r["id"], r["what"][:50], r["measured"]) for r in bad[:4]))
                           if bad else "" + (" no PASS row for %s" % missing if missing else ""),
                           _r((lat.get("transport") or {}).get("p95")),
                           (lat.get("transport") or {}).get("outcomes", {}).get("extrapolated", 0),
                           _r((lat.get("step") or {}).get("p50")), _r((lat.get("step") or {}).get("p95"))))
    return out if res else None

def _r(v):
    return "-" if v is None else "%.2f" % v

def item_analyze(base_out):
    if not base_out:
        record("analyze", False, "no base run")
        return
    cmd = [PY, "-I", LAUNCHER, "s1", "--analyze", base_out]
    p = subprocess.run(cmd, cwd=_REPO, capture_output=True, text=True, timeout=300)
    with open(os.path.join(base_out, "s1-results.json"), encoding="utf-8") as f:
        res = json.load(f)
    record("analyze", p.returncode == 0 and res.get("ok"), "launcher s1 --analyze rc %d, ok %s" % (
        p.returncode, res.get("ok")))

def item_mutant(root, item, mutant, parts, expect_red, baseline=None):
    out = os.path.join(root, item)
    rc, res, secs = launcher_s1(item, out, parts=parts, se_extra=("--mutant", mutant), baseline=baseline)
    red_ids = sorted({r["id"] for r in rows_of(res, status="FAIL")})
    worst = {}
    for r in rows_of(res, status="FAIL"):
        worst.setdefault(r["id"], "%s: %s" % (r["what"][:60], r["measured"]))
    hit = all(x in red_ids for x in expect_red)
    record(item, rc != 0 and hit, "--mutant %s: launcher rc %s in %.0f s; red %s (expected %s) %s" % (
        mutant, rc, secs, red_ids, list(expect_red), "RED (expected)" if hit and rc != 0 else "GREEN (unexpected)"))
    for k in expect_red:
        if k in worst:
            print("          %s %s" % (k, worst[k]))
    return res

def item_prevtick(root, base_out):
    if not base_out:
        record("prevtick", False, "no base run")
        return
    out = os.path.join(root, "prevtick")
    rc, res, secs = launcher_s1("prevtick", out, parts="hold,yaw", se_extra=("--mutant", "prev-tick"),
                                baseline=os.path.join(base_out, "s1-results.json"))
    m2 = (res or {}).get("latency", {}).get("m2") or {}
    shifted = rows_of(res, "S1.M2", "SHIFT")
    ok = len(shifted) == 1 and "S1.N2" in shifted[0]["what"] and abs(m2.get("step", 0.0)) >= LAT.TICK_MS / 2
    record("prevtick", ok, "--mutant prev-tick vs base: step p50 %+.2f ms (SHIFT expected), transport p50 %+.2f ms "
                           "(info) -> %s" % (m2.get("step", 0.0), m2.get("transport", 0.0),
                                             "RED (expected)" if ok else "GREEN (unexpected)"))

def item_auto(root):
    out = os.path.join(root, "auto")
    os.makedirs(out, exist_ok=True)
    name = section_name("auto")
    env = dict(os.environ)
    env["DEEPENGINEERS_BRIDGE"] = "1"
    sn_path, se_path = os.path.join(out, "sn-standin.jsonl"), os.path.join(out, "se-standin.jsonl")
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    with open(sn_path, "wb") as fo, open(sn_path + ".err", "wb") as fe:
        # --s1-check: the host checks the SE log against worldmap_ref itself at exit (the "SE without Subnautica" path)
        host = subprocess.Popen(host_cmd(name, out, ("--s1-check", se_path, "--s1-quick")), cwd=_REPO, env=env,
                                stdout=fo, stderr=fe, creationflags=flags)
    time.sleep(2.5)
    env.pop("DEEPENGINEERS_BRIDGE", None)
    with open(se_path, "wb") as fo, open(se_path + ".err", "wb") as fe:
        se = subprocess.Popen(se_cmd(name, ("--s1-script", "wm2,wm3,wm4,yaw", "--s1-quick",
                                            "--s1-exit-after-script")),
                              cwd=_REPO, env=env, stdout=fo, stderr=fe, creationflags=flags)
    try:
        se.wait(timeout=300)
        host.wait(timeout=30)
    except subprocess.TimeoutExpired:
        for p in (se, host):
            if p.poll() is None:
                p.kill()
    se_rec, sn_rec = W.read_jsonl(se_path), W.read_jsonl(sn_path)
    q = W.plan(True)
    wm = W.evaluate(se_rec, sn_rec, (), q, only=["wm2", "wm3", "wm4", "n7"])
    lat = LAT.evaluate(se_rec, sn_rec, q)
    with open(os.path.join(out, "results.json"), "w", encoding="utf-8") as f:
        json.dump({"checkWm": wm, "latency": {k: v for k, v in lat.items() if k != "rows"}, "latencyRows": lat["rows"]},
                  f, indent=1, default=str)
    bad = [r for r in wm["rows"] + lat["rows"] if r.get("required", True) and r["status"] in ("FAIL", "MISSING")]
    host_check = [r for r in sn_rec if r.get("ev") == "s1_check"]
    host_ok = bool(host_check) and host_check[-1].get("ok") is True
    record("auto", wm["ok"] and lat["ok"] and host_ok,
           "--s1-script wm2,wm3,wm4,yaw: SE exit %s, host exit %s; check_wm %s, latency %s, host --s1-check %s%s" % (
               se.returncode, host.returncode, "PASS" if wm["ok"] else "FAIL", "PASS" if lat["ok"] else "FAIL",
               ("PASS" if host_ok else "FAIL") if host_check else "missing",
               (" (%s)" % "; ".join("%s %s" % (r["id"], r["what"][:40]) for r in bad[:4])) if bad else ""))

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S1 stand-in dry run")
    ap.add_argument("--only", default="selftest,base,analyze,mapdir,naive,prevtick,auto")
    ap.add_argument("--full", action="store_true", help="the base run uses the full plan (60 s hold, 10,000 frames)")
    ap.add_argument("--out", default=None)
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    only = set(x.strip() for x in a.only.split(",") if x.strip())
    root = a.out or os.path.join(_REPO, "tests", "out", "s1", "dryrun-" + datetime.datetime.now().strftime(
        "%Y%m%d-%H%M%S"))
    os.makedirs(root, exist_ok=True)
    print("S1 dry run -> %s" % root, flush=True)
    t0 = time.monotonic()
    if "selftest" in only:
        item_selftest()
    base = None
    if only & {"base", "analyze", "prevtick"}:
        base = item_base(root, a.full)
    if "analyze" in only:
        item_analyze(base)
    if "mapdir" in only:
        item_mutant(root, "mapdir", "mapdir-origin", "wm2,wm4,g1e5", ("S1.WM-2", "S1.WM-4", "S1.G1-E5"))
    if "naive" in only:
        item_mutant(root, "naive", "naive-map", "wm2,wm3", ("S1.WM-2", "S1.WM-3"))
    if "prevtick" in only:
        item_prevtick(root, base)
    if "auto" in only:
        item_auto(root)
    ok = all(r[1] for r in RESULTS)
    with open(os.path.join(root, "summary.json"), "w", encoding="utf-8") as f:
        json.dump({"ok": ok, "results": RESULTS, "seconds": round(time.monotonic() - t0, 1)}, f, indent=1)
    print("S1 DRY RUN %s: %d/%d items as expected, %.0f s (%s)" % ("PASS" if ok else "FAIL",
                                                                  sum(1 for r in RESULTS if r[1]), len(RESULTS),
                                                                  time.monotonic() - t0, root))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
