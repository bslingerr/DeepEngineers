"""S7p-gameplay oracle (ROADMAP "### S7p Polish pass" P11-P14): reads the SE and Subnautica JSONL logs of one free-play
session and turns the S7p-gameplay rows (CONTRACT §5.4 "S7p-gameplay") into verdicts the project's report can be checked
against. Stdlib only; run with python -I.

 python -I tests/s7p/analyze_s7p_gameplay.py # the newest se-*.jsonl / sn-*.jsonl pair in
 # %LOCALAPPDATA%\\DeepEngineers\\logs
 python -I tests/s7p/analyze_s7p_gameplay.py --se SE.jsonl --sn SN.jsonl [--json]
 python -I tests/s7p/analyze_s7p_gameplay.py DIR [--json] # the newest pair inside DIR
 python -I tests/s7p/analyze_s7p_gameplay.py --selftest

LOG CONTRACT. SE rows are flat {"t", "ev",...} (log lines: {"ev": "log", "msg"}); Subnautica rows are
{"t", "event", "ev", "data": {...}} (log lines: {"event", "text"}). Rows read:
 SN s7p.mirror {parts, dirtyCells, buildsInFlight, uploadsPending, childBacklog, drainMsMax, frameMaxMs, busy,...}
 SE s7p.export {parts, cellsQueued, blocksQueued, readMsMax, skippedTicks, debtMs, busy,...}
 SN s7p.damage {shots, blasts, toolSteps, hits, kills, hp,...}; SN s7p.hit {creature, hp, killed,...}
 SE s7p.hold {entity, buoyancyN, massKg, offsetMs, vUpMs, applications,...}
 SE s7p.camera {why, yawDeg, pitchDeg, yawInputTicks, pitchInputTicks, blockedTicks, yawStuck, pitchStuck, fixes,...}
 SN s7p.cam {frames, yawDeg, pitchDeg, rollDeg, nonFinite}
 SE log "DE.Link STALL..." / "DE.Link RECOVER..."; SN log "DE.Link STALLED..."
Verdicts: PASS, FAIL, INFO, MISSING.
"""
import glob
import json
import os
import sys

def read_jsonl(path):
    rows = []
    if not path or not os.path.exists(path):
        return rows
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                rows.append(json.loads(line))
            except ValueError:
                continue
    return rows

def se_rows(rows, ev):
    return [r for r in rows if r.get("ev") == ev]

def sn_rows(rows, ev):
    return [r.get("data", {}) for r in rows if r.get("ev") == ev and isinstance(r.get("data"), dict)]

def se_log(rows, prefix):
    return [r.get("msg", "") for r in rows if r.get("ev") == "log" and str(r.get("msg", "")).startswith(prefix)]

def sn_log(rows, prefix):
    return [r.get("text", "") for r in rows if str(r.get("text", "")).startswith(prefix)]

def verdict(rid, v, detail):
    return {"id": rid, "verdict": v, "detail": detail}

def analyze(se, sn):
    out = []
    # ---------------------------------------------------------------- P14: no stall, a readable backlog
    mirror = sn_rows(sn, "s7p.mirror")
    export = se_rows(se, "s7p.export")
    stalls = se_log(se, "DE.Link STALL")
    if not mirror and not export:
        if stalls:
            out.append(verdict("S7p.P14.stall", "FAIL", "SE saw Subnautica stall %d time(s) (no s7p rows: a log from before S7p): %s" % (len(stalls), stalls[0][:120])))
        else:
            out.append(verdict("S7p.P14.stall", "MISSING", "no s7p.mirror / s7p.export rows (nothing mirrored in the session)"))
    else:
        worst_drain = max([float(m.get("drainMsMax", 0) or 0) for m in mirror] or [0])
        worst_frame = max([float(m.get("frameMaxMs", 0) or 0) for m in mirror] or [0])
        if stalls or worst_frame >= 3000:
            out.append(verdict("S7p.P14.stall", "FAIL", "SE saw Subnautica stall %d time(s); worst Subnautica frame %.0f ms, drain %.0f ms" % (len(stalls), worst_frame, worst_drain)))
        else:
            out.append(verdict("S7p.P14.stall", "PASS", "no DE.Link STALL on SE; worst Subnautica frame %.0f ms, worst drain %.0f ms" % (worst_frame, worst_drain)))
        max_parts = max([int(m.get("parts", 0) or 0) for m in mirror] or [0])
        busy = [m for m in mirror if m.get("busy")]
        out.append(verdict("S7p.P14.backlog", "INFO", "largest mirror %d parts; %d busy seconds; max dirty cells %d, max child backlog %d, uploads pending max %d" % (
            max_parts, len(busy), max([int(m.get("dirtyCells", 0) or 0) for m in mirror] or [0]),
            max([int(m.get("childBacklog", 0) or 0) for m in mirror] or [0]), max([int(m.get("uploadsPending", 0) or 0) for m in mirror] or [0]))))
        if export:
            out.append(verdict("S7p.P14.export", "INFO", "SE worst read tick %.1f ms; %d ticks skipped to pay the debt; max cells queued %d" % (
                max([float(e.get("readMsMax", 0) or 0) for e in export] or [0]), sum([int(e.get("skippedTicks", 0) or 0) for e in export]),
                max([int(e.get("cellsQueued", 0) or 0) for e in export] or [0]))))
    # ---------------------------------------------------------------- P11: creatures take SE's damage
    dmg = sn_rows(sn, "s7p.damage")
    hits = sn_rows(sn, "s7p.hit")
    shots = sum([int(d.get("shots", 0) or 0) + int(d.get("blasts", 0) or 0) + int(d.get("toolSteps", 0) or 0) for d in dmg])
    kills = [h for h in hits if h.get("killed")]
    if hits:
        names = sorted(set([str(h.get("creature")) for h in hits]))
        out.append(verdict("S7p.P11.damage", "PASS", "%d hits on %s; %d kill(s)" % (len(hits), ", ".join(names[:8]), len(kills))))
    elif shots > 0:
        blocked = sum([int(d.get("blocked", 0) or 0) for d in dmg[-1:]])
        out.append(verdict("S7p.P11.damage", "INFO", "%d shots/blasts/tool steps, no creature hit (blocked %d): aim at a creature to judge" % (shots, blocked)))
    else:
        out.append(verdict("S7p.P11.damage", "MISSING", "no gun, explosion or tool event reached Subnautica's damage reader"))
    # ---------------------------------------------------------------- P12: a ship with dampeners holds its depth
    hold = [h for h in se_rows(se, "s7p.hold") if h.get("vUpMs") is not None and int(h.get("applications", 0) or 0) >= 30]
    if not hold:
        out.append(verdict("S7p.P12.hold", "MISSING", "no s7p.hold row (no ship under water with dampeners on)"))
    else:
        v = sorted([abs(float(h["vUpMs"])) for h in hold])
        med = v[len(v) // 2]
        out.append(verdict("S7p.P12.hold", "PASS" if med <= 0.1 else ("FAIL" if med > 0.3 else "INFO"),
                           "median |vertical speed| %.3f m/s over %d held seconds (buoyancy up to %.0f kN)" % (med, len(v), max([float(h.get("buoyancyN", 0)) for h in hold]) / 1000.0)))
    # ---------------------------------------------------------------- P13: the camera turns on both axes
    cam = [c for c in se_rows(se, "s7p.camera") if c.get("why") == "second"]
    fixes = [c for c in se_rows(se, "s7p.camera") if c.get("why") == "fix"]
    if not cam:
        out.append(verdict("S7p.P13.axes", "MISSING", "no s7p.camera row (the local character never moved the camera)"))
    else:
        yaw_stuck = [c for c in cam if c.get("yawStuck")]
        pitch_stuck = [c for c in cam if c.get("pitchStuck")]
        unexplained = [c for c in yaw_stuck if int(c.get("blockedTicks", 0) or 0) < 20] + pitch_stuck
        lingering = [c for c in yaw_stuck if int(c.get("blockedTicks", 0) or 0) >= 20 and c.get("fixOn")]
        if unexplained:
            c = unexplained[0]
            out.append(verdict("S7p.P13.axes", "FAIL", "%d second(s) with an axis stuck for another cause (first: tick %s yawStuck=%s pitchStuck=%s groundState=%s headX=%s controller=%s)" % (
                len(unexplained), c.get("tick"), c.get("yawStuck"), c.get("pitchStuck"), c.get("groundState"), c.get("headX"), c.get("cameraController"))))
        elif lingering:
            out.append(verdict("S7p.P13.axes", "FAIL", "%d second(s) of the yaw lock despite the fix (fixes %d)" % (len(lingering), len(fixes))))
        else:
            out.append(verdict("S7p.P13.axes", "PASS", "%d seconds with camera input, no stuck axis; %d yaw unlock(s)" % (len(cam), len(fixes))))
    snc = sn_rows(sn, "s7p.cam")
    nf = sum([int(c.get("nonFinite", 0) or 0) for c in snc])
    out.append(verdict("S7p.P13.nan", "INFO" if snc else "MISSING", "%d non-finite C1 poses held on Subnautica's camera" % nf))
    return out

def newest(pattern):
    files = glob.glob(pattern)
    return max(files, key=os.path.getmtime) if files else None

def selftest():
    se = [
        {"ev": "s7p.export", "readMsMax": 12.5, "skippedTicks": 3, "cellsQueued": 40, "busy": True},
        {"ev": "s7p.hold", "vUpMs": 0.02, "applications": 60, "buoyancyN": 400000},
        {"ev": "s7p.hold", "vUpMs": -0.01, "applications": 60, "buoyancyN": 400000},
        {"ev": "s7p.camera", "why": "fix", "fixes": 1},
        {"ev": "s7p.camera", "why": "second", "yawStuck": False, "pitchStuck": False, "blockedTicks": 0, "fixOn": True},
    ]
    sn = [
        {"ev": "s7p.mirror", "data": {"parts": 100669, "drainMsMax": 30, "frameMaxMs": 60, "busy": True, "dirtyCells": 400}},
        {"ev": "s7p.damage", "data": {"shots": 5, "hits": 1}},
        {"ev": "s7p.hit", "data": {"creature": "Stalker", "hp": 30, "killed": False}},
        {"ev": "s7p.cam", "data": {"frames": 60, "nonFinite": 0}},
    ]
    got = dict((r["id"], r["verdict"]) for r in analyze(se, sn))
    want = {"S7p.P14.stall": "PASS", "S7p.P11.damage": "PASS", "S7p.P12.hold": "PASS", "S7p.P13.axes": "PASS"}
    ok = all(got.get(k) == v for k, v in want.items())
    # the free play of: a stall, a drift, a stuck yaw for another cause, nothing hit
    se2 = [{"ev": "log", "msg": "DE.Link STALL snHeartbeatAge=3062ms"}, {"ev": "s7p.hold", "vUpMs": 0.9, "applications": 60, "buoyancyN": 1},
           {"ev": "s7p.camera", "why": "second", "yawStuck": True, "pitchStuck": False, "blockedTicks": 0, "fixOn": True}]
    sn2 = [{"ev": "s7p.mirror", "data": {"parts": 100669, "drainMsMax": 4473, "frameMaxMs": 4500}}, {"ev": "s7p.damage", "data": {"shots": 3}}]
    got2 = dict((r["id"], r["verdict"]) for r in analyze(se2, sn2))
    want2 = {"S7p.P14.stall": "FAIL", "S7p.P12.hold": "FAIL", "S7p.P13.axes": "FAIL", "S7p.P11.damage": "INFO"}
    ok2 = all(got2.get(k) == v for k, v in want2.items())
    empty = dict((r["id"], r["verdict"]) for r in analyze([], []))
    ok3 = all(empty.get(k) == "MISSING" for k in ("S7p.P14.stall", "S7p.P11.damage", "S7p.P12.hold", "S7p.P13.axes"))
    print("selftest " + ("PASS" if ok and ok2 and ok3 else "FAIL") + " " + json.dumps([got, got2, empty]))
    return 0 if ok and ok2 and ok3 else 1

def main(argv):
    if "--selftest" in argv:
        return selftest()
    as_json = "--json" in argv
    se_path = sn_path = None
    if "--se" in argv:
        se_path = argv[argv.index("--se") + 1]
    if "--sn" in argv:
        sn_path = argv[argv.index("--sn") + 1]
    folder = None
    for a in argv:
        if not a.startswith("--") and os.path.isdir(a):
            folder = a
    if not se_path or not sn_path:
        base = folder or os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")
        se_path = se_path or newest(os.path.join(base, "se-*.jsonl"))
        sn_path = sn_path or newest(os.path.join(base, "sn-*.jsonl"))
    res = analyze(read_jsonl(se_path), read_jsonl(sn_path))
    if as_json:
        print(json.dumps({"se": se_path, "sn": sn_path, "rows": res}, indent=1))
    else:
        print("S7p-gameplay oracle: se=%s sn=%s" % (se_path, sn_path))
        for r in res:
            print("%-16s %-8s %s" % (r["id"], r["verdict"], r["detail"]))
    return 1 if any(r["verdict"] == "FAIL" for r in res) else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
