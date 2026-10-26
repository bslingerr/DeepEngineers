"""Read-only view of the live DeepEngineers section (ROADMAP §5.8, §6; used in every real run).

Opens the section with OpenFileMappingW + MapViewOfFile(FILE_MAP_READ): it can never write, and it never creates the
section (no mmap tag names). Prints the C0 identity words, both header blocks (PID and liveness, generation,
heartbeat age, phase, flags), C5/C7 (epochs, acks, counters), the seq counters of every seqlock record, ring heads,
tails and fill, and region checksums (BLAKE2b-64: fast, for "unchanged" comparisons; `--fnv` adds FNV-1a 64 of small
regions, the hash the C# Resync.RegionChecksum uses).

 python -I tools/spy/de_spy.py # one sample
 python -I tools/spy/de_spy.py --watch 1 --count 60 # once per second for 60 s
 python -I tools/spy/de_spy.py --json --watch 1 # JSON lines
 python -I tools/spy/de_spy.py --watch 1 --count 60 --expect-heartbeat-ms 100 --expect-generations 1/1 # S0.N6
 python -I tools/spy/de_spy.py --holders # processes holding a handle to the section
 python -I tools/spy/de_spy.py --watch 0.2 --s1 # S1: only the C1 camera and C7 presentation lines

S1 (ROADMAP S1): C1 SeFrame (seq, tick, age of qpcCapture, world epoch, flags, cameraMode, camPos, heading/pitch of
camRot's forward, vFovDeg, near, aspect, characterId, simTimeSec) and the C7 S1 fields (presentedSeTick, its lag
behind the C1 tick and the CONTRACT 5.4 freshness rule presentedSeTick >= tick - 4, areaBuiltSeTick/areaBuiltPos,
resamplesLastFrame, frameMsP50/P99, perfModeApplied, vSyncCountApplied, targetFrameRateApplied, fixedDeltaTime).

Exit code: 0 OK; 2 section absent (one-shot); 1 an --expect-* condition failed.
"""
from __future__ import annotations

import argparse
import datetime
import json
import math
import os
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_WIRE = os.path.normpath(os.path.join(_HERE, "..", "..", "tests", "wire"))
if _WIRE not in sys.path:
    sys.path.insert(0, _WIRE)
import layout_py as L  # noqa: E402
import section_py as S  # noqa: E402

SEQLOCKS = [("C1", L.REGION["C1"].offset), ("C2", L.REGION["C2"].offset), ("C5", L.REGION["C5"].offset),
            ("C6", L.REGION["C6"].offset), ("C7", L.REGION["C7"].offset), ("C9", L.REGION["C9"].offset),
            ("C10req", L.REGION["C10"].offset + L.C10_REQUEST_OFFSET),
            ("C10res", L.REGION["C10"].offset + L.C10_RESULT_OFFSET)]

def flag_names(value: int, table: dict) -> str:
    names = [n for n, b in sorted(table.items(), key=lambda kv: kv[1]) if value & (1 << b)]
    rest = value & ~sum(1 << b for b in table.values())
    if rest:
        names.append("0x%x" % rest)
    return ",".join(names) or "-"

CAMERA_MODES = {v: k for k, v in L.CONSTS["CameraMode"].items()}
PERF_MODES = {v: k for k, v in L.CONSTS["PerfMode"].items()}
FRESH_TICKS = 4          # CONTRACT 5.4 C7: presentedSeTick >= tick - 4

def qrot(q, v):
    """Unity `q * v` for q = (x, y, z, w)."""
    u = (q[0], q[1], q[2])
    t = (2 * (u[1] * v[2] - u[2] * v[1]), 2 * (u[2] * v[0] - u[0] * v[2]), 2 * (u[0] * v[1] - u[1] * v[0]))
    c = (u[1] * t[2] - u[2] * t[1], u[2] * t[0] - u[0] * t[2], u[0] * t[1] - u[1] * t[0])
    return (v[0] + q[3] * t[0] + c[0], v[1] + q[3] * t[1] + c[1], v[2] + q[3] * t[2] + c[2])

def c1_view(v: dict, seq: int) -> dict:
    f = qrot(v["camRot"], (0.0, 0.0, 1.0))
    age = (S.qpc() - v["qpcCapture"]) * 1000.0 / S.QPC_FREQUENCY if v["qpcCapture"] else None
    return {"seq": seq, "tick": v["tick"], "ageMs": round(age, 2) if age is not None else None,
            "worldEpoch": v["worldEpoch"], "flags": flag_names(v["flags"], L.SE_FRAME_FLAGS),
            "cameraMode": CAMERA_MODES.get(v["cameraMode"], v["cameraMode"]),
            "camPos": [round(x, 4) for x in v["camPos"]], "camRot": [round(x, 6) for x in v["camRot"]],
            "headingDeg": round(math.degrees(math.atan2(f[0], f[2])), 3),
            "pitchDeg": round(math.degrees(math.asin(max(-1.0, min(1.0, f[1])))), 3), "vFovDeg": round(v["vFovDeg"], 4),
            "near": round(v["near"], 4), "aspect": round(v["aspect"], 4), "characterId": v["characterId"],
            "simTimeSec": round(v["simTimeSec"], 3), "pelvisSn": [round(x, 3) for x in v["pelvisSn"]]}

def magic_text(m: int) -> str:
    b = m.to_bytes(4, "little")
    return (b.decode("ascii") if all(32 <= c < 127 for c in b) else "....") + " (0x%08x)" % m

def sample(sec: S.Section, checksums: bool, fnv_regions) -> dict:
    now = S.tick64()
    h = sec.header()
    out = {"time": datetime.datetime.now().isoformat(timespec="milliseconds"), "name": sec.name,
           "mappedSize": sec.mapped_size}
    out["identity"] = {
        "magic": h["magic"], "magicOk": h["magic"] == L.MAGIC, "layout": "%d.%d" % (h["layoutMajor"], h["layoutMinor"]),
        "layoutHash": L.hex64(h["layoutHash"]), "layoutHashOk": h["layoutHash"] == L.layout_hash(),
        "byteOrderMarkOk": h["byteOrderMark"] == L.BYTE_ORDER_MARK, "headerSize": h["headerSize"],
        "sectionSize": h["sectionSize"], "wireVersion": h["wireVersion"], "siteVersion": h["siteVersion"],
        "damageScaleVersion": h["damageScaleVersion"], "regionCount": h["regionCount"],
        "qpcFrequency": h["qpcFrequency"], "qpcFrequencyOk": h["qpcFrequency"] == S.QPC_FREQUENCY,
        "devControl": h["devControl"]}
    if h["regionCount"] and sec.mapped_size >= L.HEADER_SIZE:
        table = sec.region_table()
        want = [dict(id=r.id, kind=r.kind, offset=r.offset, size=r.size, recordSize=r.record_size, version=r.version)
                for r in L.REGIONS_BY_ID]
        out["identity"]["regionTableOk"] = table == want
    for side in ("sn", "se"):
        pid = h[side + "Pid"]
        hb = h[side + "HeartbeatMs"]
        phase = h[side + "Phase"]
        phases = L.SN_PHASES if side == "sn" else L.SE_PHASES
        out[side] = {"pid": pid, "process": S.process_state(pid) if pid else "-",
                     "generation": h[side + "Generation"], "heartbeatAgeMs": (now - hb) if hb and now >= hb else None,
                     "phase": phase, "phaseName": phases.get(phase, "?%d" % phase),
                     "flags": flag_names(h[side + "Flags"], L.SN_FLAGS if side == "sn" else L.SE_FLAGS),
                     "hwnd": h[side + "Hwnd"]}
    out["se"]["loadStep"] = h["seLoadStep"]
    big = sec.mapped_size >= L.SECTION_SIZE
    if big:
        seqs = {}
        for key, off in SEQLOCKS:
            seqs[key] = sec.u32(off)
        out["seq"] = seqs
        st, seq, data = S.seqlock_read(sec, L.REGION["C5"].offset, 256)
        if data:
            v = L.SE_STATE.unpack(data)
            out["c5"] = {"seq": seq, "worldEpoch": v["worldEpoch"], "helloEpoch": v["helloEpoch"],
                         "linkEpoch": v["linkEpoch"], "overlays": flag_names(v["overlays"], L.OVERLAYS),
                         "flags": flag_names(v["flags"], L.SE_STATE_FLAGS), "viewState": v["viewState"],
                         "perfMode": v["perfMode"], "Rs": v["Rs"], "siteHash": L.hex64(v["siteHash"]),
                         "siteHashOk": v["siteHash"] == L.SITE_HASH_V1, "siteVersion": v["siteVersion"]}
        else:
            out["c5"] = {"status": st}
        st, seq, data = S.seqlock_read(sec, L.REGION["C7"].offset, 256)
        if data:
            v = L.SN_STATUS.unpack(data)
            out["c7"] = {"seq": seq, "snPhase": L.SN_PHASES.get(v["snPhase"], v["snPhase"]),
                         "flags": flag_names(v["flags"], L.SN_STATUS_FLAGS), "snFrame": v["snFrame"],
                         "helloAckEpoch": v["helloAckEpoch"], "linkAckEpoch": v["linkAckEpoch"],
                         "mirroredEntityCount": v["mirroredEntityCount"], "pendingParts": v["pendingParts"],
                         "damageDropped": v["damageDropped"], "damageOverflowDepth": v["damageOverflowDepth"],
                         "c3BytesDrained": v["c3BytesDrained"], "hostKind": v["hostKind"],
                         "capabilities": flag_names(v["capabilities"], L.CAPABILITIES),
                         "presentedSeTick": v["presentedSeTick"], "areaBuiltSeTick": v["areaBuiltSeTick"],
                         "areaBuiltPos": [round(x, 3) for x in v["areaBuiltPos"]],
                         "resamplesLastFrame": v["resamplesLastFrame"], "frameMsP50": round(v["frameMsP50"], 3),
                         "frameMsP99": round(v["frameMsP99"], 3),
                         "perfModeApplied": PERF_MODES.get(v["perfModeApplied"], v["perfModeApplied"]),
                         "vSyncCountApplied": v["vSyncCountApplied"],
                         "targetFrameRateApplied": v["targetFrameRateApplied"],
                         "fixedDeltaTime": round(v["fixedDeltaTime"], 4)}
        else:
            out["c7"] = {"status": st}
        st, seq, data = S.seqlock_read(sec, L.REGION["C1"].offset, L.SE_FRAME.size)
        if data:
            out["c1"] = c1_view(L.SE_FRAME.unpack(data), seq)
            if "presentedSeTick" in out["c7"]:
                out["c7"]["lagTicks"] = out["c1"]["tick"] - out["c7"]["presentedSeTick"]
                out["c7"]["fresh"] = out["c7"]["presentedSeTick"] >= out["c1"]["tick"] - FRESH_TICKS
        else:
            out["c1"] = {"status": st}
        rings = {}
        for r in L.RINGS:
            hd, tl = sec.ring_head(r.key), sec.ring_tail(r.key)
            cap = L.ring_capacity(r.key)
            used = (hd - tl) & S.MASK64
            rings[r.key] = {"head": hd, "tail": tl, "used": used if used <= cap else None, "capacity": cap,
                            "fill": round(100.0 * used / cap, 2) if used <= cap else None,
                            "unit": "records" if r.key == "C8" else "bytes", "producer": r.producer,
                            "consumer": r.consumer}
        out["rings"] = rings
    if checksums:
        out["checksums"] = sec.region_checksums()
    if fnv_regions:
        fnv = {}
        for k in fnv_regions:
            g = L.REGION.get(k)
            if g and g.offset + g.size <= sec.mapped_size:
                fnv[k] = L.hex64(L.fnv1a64(sec.read(g.offset, g.size)))
        out["fnv1a64"] = fnv
    return out

def s1_lines(s: dict) -> list:
    """The S1 lines: C1 camera and the C7 presentation fields."""
    out = []
    c = s.get("c1") or {}
    if "tick" in c:
        p = c["camPos"]
        out.append("C1  seq %d  tick %d (age %s ms)  epoch %d  %s  flags %s  camPos (%.3f, %.3f, %.3f)  heading %.2f "
                   "pitch %.2f deg  vFov %.3f  near %.3f  aspect %.4f  character %d" % (
                       c["seq"], c["tick"], c["ageMs"], c["worldEpoch"], c["cameraMode"], c["flags"], p[0], p[1], p[2],
                       c["headingDeg"], c["pitchDeg"], c["vFovDeg"], c["near"], c["aspect"], c["characterId"]))
    elif c:
        out.append("C1  %s" % c.get("status"))
    c7 = s.get("c7") or {}
    if "presentedSeTick" in c7:
        fresh = "-" if "fresh" not in c7 else ("fresh" if c7["fresh"] else "STALE (rule >= tick - %d)" % FRESH_TICKS)
        out.append("C7  presentedSeTick %d (lag %s ticks, %s)  areaBuilt tick %d at %s  resamples %d  frame p50 %.2f "
                   "p99 %.2f ms  perf %s  vSync %d  fps %d  fixedDt %.3f  snFrame %d" % (
                       c7["presentedSeTick"], c7.get("lagTicks", "-"), fresh, c7["areaBuiltSeTick"],
                       c7["areaBuiltPos"], c7["resamplesLastFrame"], c7["frameMsP50"], c7["frameMsP99"],
                       c7["perfModeApplied"], c7["vSyncCountApplied"], c7["targetFrameRateApplied"],
                       c7["fixedDeltaTime"], c7["snFrame"]))
    return out

def render(s: dict) -> str:
    i = s["identity"]
    lines = ["%s  %s  mapped 0x%x  (read-only)" % (s["time"], s["name"], s["mappedSize"])]
    lines.append("identity  magic %s  layout %s  hash %s%s  wire %d site %d damage %d  bom %s  header 0x%x  size 0x%x"
                 "  regions %d%s  qpf %d%s  devControl 0x%x" % (
                     magic_text(i["magic"]), i["layout"], i["layoutHash"], "" if i["layoutHashOk"] else " (!= python)",
                     i["wireVersion"], i["siteVersion"], i["damageScaleVersion"],
                     "ok" if i["byteOrderMarkOk"] else "BAD", i["headerSize"], i["sectionSize"], i["regionCount"],
                     "" if i.get("regionTableOk", True) else " (table != python)", i["qpcFrequency"],
                     "" if i["qpcFrequencyOk"] else " (!= local)", i["devControl"]))
    for side in ("sn", "se"):
        b = s[side]
        lines.append("%s  pid %d [%s]  generation %d  heartbeat %s  phase %s(%d)  flags %s  hwnd 0x%x%s" % (
            side.upper(), b["pid"], b["process"], b["generation"],
            ("%d ms" % b["heartbeatAgeMs"]) if b["heartbeatAgeMs"] is not None else "-", b["phaseName"], b["phase"],
            b["flags"], b["hwnd"], ("  loadStep %d" % b["loadStep"]) if side == "se" else ""))
    if "c5" in s:
        c = s["c5"]
        if "seq" in c:
            lines.append("C5  seq %d  worldEpoch %d  hello %d  link %d  overlays %s  flags %s  Rs %.8f  siteHash %s%s"
                         "  siteVersion %d" % (c["seq"], c["worldEpoch"], c["helloEpoch"], c["linkEpoch"],
                                               c["overlays"], c["flags"], c["Rs"], c["siteHash"],
                                               "" if c["siteHashOk"] else " (!= v1)", c["siteVersion"]))
        else:
            lines.append("C5  %s" % c["status"])
        c = s["c7"]
        if "seq" in c:
            lines.append("C7  seq %d  snPhase %s  helloAck %d  linkAck %d  mirrored %d  pending %d  damageDropped %d"
                         "  overflow %d  c3Drained %d  hostKind %d  caps %s  flags %s" % (
                             c["seq"], c["snPhase"], c["helloAckEpoch"], c["linkAckEpoch"], c["mirroredEntityCount"],
                             c["pendingParts"], c["damageDropped"], c["damageOverflowDepth"], c["c3BytesDrained"],
                             c["hostKind"], c["capabilities"], c["flags"]))
        else:
            lines.append("C7  %s" % c["status"])
        lines.extend(s1_lines(s))
        lines.append("seq  " + "  ".join("%s %d" % kv for kv in s["seq"].items()))
        for k, r in s["rings"].items():
            lines.append("ring %-4s head %12d  tail %12d  used %s / %d %s (%s%%)  %s -> %s" % (
                k, r["head"], r["tail"], r["used"] if r["used"] is not None else "IMPOSSIBLE", r["capacity"],
                r["unit"], r["fill"], r["producer"], r["consumer"]))
    if "checksums" in s:
        lines.append("blake2b-64  " + "  ".join("%s %s" % kv for kv in s["checksums"].items()))
    if "fnv1a64" in s:
        lines.append("fnv1a-64    " + "  ".join("%s %s" % kv for kv in s["fnv1a64"].items()))
    return "\n".join(lines)

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="read-only spy of the DeepEngineers section")
    ap.add_argument("--name", default=L.SECTION_NAME)
    ap.add_argument("--watch", type=float, nargs="?", const=1.0, default=None, help="repeat every N s (default 1)")
    ap.add_argument("--count", type=int, default=0, help="number of samples in watch mode (0: until Ctrl-C)")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--no-checksums", action="store_true")
    ap.add_argument("--fnv", default="", help="comma-separated regions for FNV-1a 64 (slow in Python; small ones)")
    ap.add_argument("--holders", action="store_true", help="list processes holding a handle to the section")
    ap.add_argument("--expect-heartbeat-ms", type=float, default=None)
    ap.add_argument("--expect-generations", default=None, help="SN/SE, e.g. 1/1")
    ap.add_argument("--s1", action="store_true", help="print only the S1 lines (C1 camera, C7 presentation)")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    fnv_regions = [x.strip() for x in a.fnv.split(",") if x.strip()]
    if a.holders:
        state, pids, note = S.section_holders(a.name)
        names = {p["pid"]: p["name"] for p in S.list_processes()} if pids else {}
        print("%s: %s%s%s" % (a.name, state, (" holders: " + ", ".join("%d %s" % (p, names.get(p, "?"))
                                                                    for p in pids)) if pids else
                              (" holders: none besides this process" if pids == [] and state == "present" else ""),
                              (" (%s)" % note) if note else ""))
        if a.watch is None:
            return 0 if state != "error" else 1
    n = 0
    worst = {"sn": 0.0, "se": 0.0}
    gens = set()
    problems = []
    sec = None
    try:
        while True:
            if sec is None:
                sec = S.Section.open(a.name, readonly=True)
            if sec is None:
                msg = {"time": datetime.datetime.now().isoformat(timespec="milliseconds"), "name": a.name,
                       "absent": True}
                print(json.dumps(msg) if a.json else "%s  %s: section absent" % (msg["time"], a.name), flush=True)
                if a.watch is None:
                    return 2
                problems.append("absent at %s" % msg["time"])
            else:
                s = sample(sec, not a.no_checksums and not a.s1, fnv_regions)
                if a.s1 and not a.json:
                    print("%s  %s" % (s["time"], "\n    ".join(s1_lines(s)) or "no C1/C7 data"), flush=True)
                else:
                    print(json.dumps(s, separators=(",", ":")) if a.json else render(s) + ("\n" if a.watch else ""),
                          flush=True)
                for side in ("sn", "se"):
                    age = s[side]["heartbeatAgeMs"]
                    if age is not None:
                        worst[side] = max(worst[side], age)
                    if a.expect_heartbeat_ms is not None and (age is None or age >= a.expect_heartbeat_ms):
                        problems.append("%s heartbeat %s ms at %s" % (side, age, s["time"]))
                gens.add("%d/%d" % (s["sn"]["generation"], s["se"]["generation"]))
            n += 1
            if a.watch is None or (a.count and n >= a.count):
                break
            time.sleep(a.watch)
    except KeyboardInterrupt:
        pass
    finally:
        if sec:
            sec.close()
    if a.expect_generations is not None:
        if gens != {a.expect_generations}:
            problems.append("generations seen %s, expected %s" % (sorted(gens), a.expect_generations))
    if a.expect_heartbeat_ms is not None or a.expect_generations is not None:
        print("SPY CHECK %s: %d samples, worst heartbeat age SN %.0f ms SE %.0f ms, generations %s%s" % (
            "PASS" if not problems else "FAIL", n, worst["sn"], worst["se"], sorted(gens),
            ("; " + "; ".join(problems[:5])) if problems else ""))
        return 1 if problems else 0
    return 0

if __name__ == "__main__":
    sys.exit(main())
