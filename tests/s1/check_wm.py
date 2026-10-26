"""S1 world-map oracle: ROADMAP S1.WM-2, S1.WM-3, S1.WM-4, S1.G1-E5 (and S1.N7 from the same samples).

Recomputes F(P) and mapdir for every logged SE sample with the stdlib references tools/worldmap/worldmap_ref.py and
tools/worldmap/site_v1.py (CONTRACT §2.3, §3), independent of the C# WireMath: the SE log carries the SE-world camera
P, forward and up in doubles plus the planet centre C and Rs; this file maps them itself and compares the result with
what SE published in C1 and with what Subnautica applied to its camera, pairing the two JSONL logs by SE tick.

 python -I tests/s1/check_wm.py --se SE.jsonl [--se...] --sn SN.jsonl [--session S.jsonl] [--only wm2,wm3,...]
 [--json OUT] [--quick] [--lenient]
 python -I tests/s1/check_wm.py --selftest # synthetic logs in memory: green run, then the mutants red
 python -I tests/s1/check_wm.py --print-plan # the S1 plan (targets, labels, C11 steps) as JSON
 python -I tests/s1/check_wm.py --g1e5-coverage A/s1-results.json C/s1-results.json D/s1-results.json
 # S1.G1-E5 exit: three distinct FOVs, every case PASS (one per session)

Exit code 0 when every required row passes (MISSING data counts as a failure unless --lenient).

S1 PLAN (single source for these checks, the launcher's `s1` session and the fake SE producer's scripted paths):
WM-2 known point (-1029, -150, -1116) facing SN north, still >= 2 s; WM-3 2 km great circle east from the anchor at
y = -100 m, 121 samples; WM-4 anchor (0, 0, 0),
tile corner (2048, -500, 2048), deepest floor (198, -1657, -518), looking along local E, N and down; G1-E5 at 500 m,
1,657 m and 2,000 m depth, elevations 0/45/90 deg, a point on SE's view axis 10 m ahead, at SE's configured FOV; 50 yaw steps of 30 deg (latency.py); a 60 s hold (fmod_events.py, S1.N6).

Thresholds (ROADMAP S1 oracle): positions 1 cm (WM-2, WM-3 x), y -100.00 +- 0.01 (WM-3), forward 0.05 deg (WM-2,
WM-4), mapped gravity (0, -1, 0) within 0.05 deg (WM-4), SE GetDepth -150.00 +- 0.01 and SN Ocean.GetDepthOf
150.00 +- 0.01 and biome grandReef (WM-2); G1-E5: SN pixel - SE pixel = (s - 1)/2 * sin(2 alpha) projected with the
logged SN FOV and screen height (pixels from the top-left corner, y down, so a positive value is below the centre),
within +-1 px at 45 deg, and 0 +- 0.5 px for level and vertical looks; S1.N7 on every traced frame: the fieldOfView
Subnautica's camera had when the plugin looked in onPreCull, BEFORE its per-frame assertion (fovSeen), equals the C1
vFovDeg the plugin had applied (fovApplied) within 0.01 deg, the rendered FOV equals the frame's C1 vFovDeg, and the
plugin's own drift counter (DE.Cam stats fovDrift) stays 0;
Subnautica camera placement: the Unity transform after placement within 1 mm and 0.01 deg of the commanded pose.

S1 JSONL EVENT SCHEMA ("s1/1"; one JSON object per line, UTF-8). The event name is in "ev" (SE plugin, stand-ins) or
"event" (SN plugin). Payload fields are top-level, or inside a "data" object, or a JSON object written after the
event name in the "msg"/"text" line (e.g. `s1.cam {"frame",...}`). Doubles are written round-trip (C# "R"). QPC
values are raw QueryPerformanceCounter counts (integers, one clock for both processes on this PC).
SE side:
 s1.site C[3], Rs, siteVersion, siteHash, worldEpoch, qpcFrequency (once per world, at DE.Site OK)
 s1.frame tick, worldEpoch, qpcCapture, label, camSe[3], fwdSe[3], upSe[3] (SE world doubles: camera position,
 unit forward and up), camPos[3], camRot[4] (the C1 floats as written), vFovDeg, near, aspect, cameraMode,
 flags, gravSe[3] (natural gravity at camSe, m/s2; needed by WM-4), depthWm (Water Mod GetDepth at camSe;
 needed by WM-2) (every published C1 while a test command is active)
 s1.cmd cmd, id, label, phase start|done|error, tick, args
 s1.yawStep index, tick (first C1 tick carrying the new yaw), qpc (QPC when the step was applied), yawFromDeg,
 yawToDeg, label
 s1.pixel label, tick, sePx[2], screen[2], vFovDeg, aheadM, camSe[3], fwdSe[3], upSe[3], pointSe[3], pointSn[3]
 s1.hold label, phase begin|end, qpc (optional; the launcher session log has the holds)
SN side:
 s1.cam frame, qpcUsed (QPC in Camera.onPreCull of MainCamera.camera), tickOld, tickNew (the position bracket),
 tickRot (snapshot used for the orientation: the newest), alpha, outcome bracketed|extrapolated|held|snap|
 torn, qpcCaptureNew, qpcCaptureRot, worldEpoch, camPos[3], camRot[4] (the Unity camera transform after
 placement), camPosCmd[3], camRotCmd[4] (the commanded pose), fov (rendered), fovSeen (found before the
 per-frame assertion), fovApplied (C1 vFovDeg applied before it), c1Fov (this frame's C1), near, screen[2]
 (every presented frame; SN plugin: DE.CamTrace text)
 s1.camStats frames, bracketed, extrapolated, held, snap, torn, fovDrift, gated, resamplesMax (every 300 frames,
 window counts; SN plugin: DE.Cam stats text)
 s1.camRig optionsFov (Subnautica's options FOV when the rig was taken: S1.N7 needs C1 vFovDeg to differ from it)
 s1.probe frame, seTick, camPos[3], depthOf (Ocean.GetDepthOf), oceanLevel, biome, isUnderwater, cameraInside,
 playerPos[3], playerToCamM, statsFrozen (1 Hz)
 s1.pixel label, frame, seTick, pointSn[3], snPx[2] (top-left origin, y down: Unity's y flipped), screen[2], fov
 (SN plugin: DE.SnPixel text, Camera.WorldToScreenPoint at the first frame presenting SE tick >= minSeTick)
 s1.fmod frame, qpc, path, source, isUnderwater
 s1.capture label, path, ok, frame, seTick
Launcher session log (tests/out/s1/<stamp>/s1-session.jsonl): "cmd", "reply", "hold", "pixel", "capture" events with
utc and qpc (fallback source for labels and pixel pairs).
"""
from __future__ import annotations

import argparse
import copy
import json
import math
import os
import re
import struct
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
_WORLDMAP = os.path.join(_REPO, "tools", "worldmap")
for _p in (_WORLDMAP, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import worldmap_ref as wm  # noqa: E402
import site_v1  # noqa: E402

TICK_HZ = 60.0
SCHEMA = "s1/1"

# ----------------------------------------------------------------------------------------------------------------
# The S1 plan
# ----------------------------------------------------------------------------------------------------------------

WM2_POINT = (-1029.0, -150.0, -1116.0)

PLAN = {
    "schema": SCHEMA,
    "wm2": {"label": "wm2", "sn": WM2_POINT, "headingDeg": 0.0, "pitchDeg": 0.0, "holdS": 2.0, "dwellS": 2.6,
            "forward": (0.0, 0.0, 1.0), "posTolM": 0.01, "fwdTolDeg": 0.05, "seDepth": -150.0, "snDepth": 150.0,
            "depthTolM": 0.01, "biome": "grandReef", "capture": "wm2.png"},
    "hold": {"label": "hold60", "sn": WM2_POINT, "headingDeg": 0.0, "seconds": 60.0},
    "wm3": {"label": "wm3", "fromSn": (0.0, -100.0, 0.0), "headingDeg": 90.0, "lengthM": 2000.0, "samples": 121,
            "ticksPerSample": 6, "y": -100.0, "yTolM": 0.01, "xTolM": 0.01, "lengthTolM": 1.0},
    "wm4": {"label": "wm4", "points": (("anchor", (0.0, 0.0, 0.0)), ("corner", (2048.0, -500.0, 2048.0)),
                                       ("floor", (198.0, -1657.0, -518.0))),
            "dirs": (("E", (1.0, 0.0, 0.0)), ("N", (0.0, 0.0, 1.0)), ("down", (0.0, -1.0, 0.0))),
            "settleS": 0.5, "holdS": 1.0, "tolDeg": 0.05, "gravity": (0.0, -1.0, 0.0), "posTolM": 0.01},
    "yaw": {"label": "yaw", "sn": WM2_POINT, "count": 50, "stepDeg": 30.0, "intervalS": 0.5, "detectDeg": 15.0,
            "p95MaxMs": 33.0},
    "g1e5": {"label": "g1e5", "xz": (-400.0, 250.0), "depths": (500.0, 1657.0, 2000.0), "elevs": (0.0, 45.0, 90.0),
             "fovsForExit": 3, "fovDistinctDeg": 1.0, "aheadM": 10.0, "settleS": 0.4, "tolPx": 1.0, "zeroTolPx": 0.5},
    "n1": {"minFrames": 10000, "p95MaxMs": 25.0, "extrapMaxFrac": 0.01},
    "n7": {"tolDeg": 0.01, "optionsDiffMinDeg": 1.0},
    "place": {"posTolM": 0.001, "rotTolDeg": 0.01},
    "parts": ("wm2", "hold", "wm3", "wm4", "yaw", "g1e5"),
}

def plan(quick: bool = False) -> dict:
    """The S1 plan; `quick` shortens the holds and the WM-3 run for stand-in dry runs (same targets and labels)."""
    p = copy.deepcopy(PLAN)
    if quick:
        p["quick"] = True
        p["hold"]["seconds"] = 5.0
        p["wm3"]["ticksPerSample"] = 2
        p["wm4"]["settleS"] = 0.3
        p["wm4"]["holdS"] = 0.5
        p["yaw"]["intervalS"] = 0.3
        p["g1e5"]["settleS"] = 0.3
        p["n1"]["minFrames"] = 2000
    return p

def g1e5_label(depth, elev) -> str:
    """One label per case; the FOV is SE's configured one (logged), one per session."""
    return "g1e5.d%d.e%d" % (round(depth), round(elev))

def wm4_label(point: str, d: str = None) -> str:
    return "wm4.%s" % point + ("" if d is None else ".%s" % d)

def teleport_args(sn, heading, label, vfov="unset") -> dict:
    """de.teleport arguments in both spellings: {x, y, z, yawDeg} and
 {sn: [x, y, z], headingDeg} (this plan's); a level look along the heading; vFovDeg optional (None: SE restores
 its configured FOV)."""
    a = {"x": float(sn[0]), "y": float(sn[1]), "z": float(sn[2]), "yawDeg": float(heading), "sn": [float(v) for v in sn],
         "headingDeg": float(heading), "label": label}
    if vfov != "unset":
        a["vFovDeg"] = vfov
    return a

def steps(p: dict = None, parts=None, capture_prefix: str = "s1") -> list:
    """The S1 session as an ordered list of steps (used by `launcher.py s1` and `fake_se_producer.py --s1-script`).
 kinds: se / sn (C11 command to that side, then wait), hold (wait, logged as a hold), pixel (de.pixel to SE, then to
 SN with the SE reply's point and tick), capture (de.capture to SE, relayed to Subnautica through C10).
 Commands (CONTRACT §5.4 C11 names de.teleport and de.capture; the rest are the S1 test commands of ROADMAP S1, in
 the spelling of the plugins' S1 work where it exists, with this plan's aliases added): SE de.teleport
 {x, y, z, yawDeg | sn, headingDeg, vFovDeg?, label}, de.look {dir: E | N | down | yaw+30 | pitch+45 | pitch+90,
 label}, de.path {lengthM, samples, altitude, headingDeg | fromSn, ticksPerSample, label}, de.pixel {ahead | aheadM,
 label}, de.capture {path, label, kind}; SN de.trace {on} (per-frame camera trace), de.underwater {mode off|global}
 (S1.N6 variants), de.testSave (S1.N4 trigger), de.pixel {pointSn, minSeTick, label}."""
    p = p or PLAN
    parts = tuple(parts or p["parts"])
    out = []

    def se(cmd, args, wait, part):
        out.append({"kind": "se", "cmd": cmd, "args": args, "wait": wait, "part": part})

    def sn(cmd, args, wait, part):
        out.append({"kind": "sn", "cmd": cmd, "args": args, "wait": wait, "part": part})

    sn("de.trace", {"on": True}, 0.2, "start")
    if "wm2" in parts:
        c = p["wm2"]
        se("de.teleport", teleport_args(c["sn"], c["headingDeg"], c["label"]), c["dwellS"], "wm2")
        out.append({"kind": "capture", "label": c["label"], "path": "%s/%s" % (capture_prefix, c["capture"]),
                    "part": "wm2"})
    if "hold" in parts:
        c = p["hold"]
        se("de.teleport", teleport_args(c["sn"], c["headingDeg"], c["label"]), 0.0, "hold")
        sn("de.underwater", {"mode": "off"}, 0.0, "hold")
        out.append({"kind": "hold", "label": c["label"], "seconds": c["seconds"], "part": "hold"})
        se("de.teleport", teleport_args(c["sn"], c["headingDeg"], c["label"] + "u"), 0.0, "hold")
        sn("de.underwater", {"mode": "global"}, 0.0, "hold")
        out.append({"kind": "hold", "label": c["label"] + "u", "seconds": c["seconds"], "part": "hold"})
        sn("de.underwater", {"mode": "off"}, 0.0, "hold")
        sn("de.testSave", {}, 1.0, "hold")
    if "wm3" in parts:
        c = p["wm3"]
        ticks = c["samples"] * c["ticksPerSample"]
        se("de.path", {"kind": "greatCircle", "fromSn": list(c["fromSn"]), "altitude": c["fromSn"][1],
                       "headingDeg": c["headingDeg"], "lengthM": c["lengthM"], "samples": c["samples"],
                       "ticksPerSample": c["ticksPerSample"], "label": c["label"]}, ticks / TICK_HZ + 1.5, "wm3")
    if "wm4" in parts:
        c = p["wm4"]
        for name, pt in c["points"]:
            se("de.teleport", teleport_args(pt, 0.0, wm4_label(name)), c["settleS"], "wm4")
            for d, _axis in c["dirs"]:
                se("de.look", {"dir": d, "label": wm4_label(name, d)}, c["holdS"], "wm4")
    if "yaw" in parts:
        c = p["yaw"]
        se("de.teleport", teleport_args(c["sn"], 0.0, c["label"]), 0.5, "yaw")
        for i in range(c["count"]):
            se("de.look", {"dir": "yaw+%g" % c["stepDeg"], "index": i, "label": c["label"]}, c["intervalS"], "yaw")
    if "g1e5" in parts:
        c = p["g1e5"]
        for d in c["depths"]:
            for e in c["elevs"]:
                lab = g1e5_label(d, e)
                se("de.teleport", teleport_args((c["xz"][0], -d, c["xz"][1]), 0.0, lab),
                   0.05 if e else c["settleS"], "g1e5")
                if e:
                    se("de.look", {"dir": "pitch+%g" % e, "label": lab}, c["settleS"], "g1e5")
                out.append({"kind": "pixel", "label": lab, "aheadM": c["aheadM"], "part": "g1e5"})
        se("de.teleport", teleport_args(p["wm2"]["sn"], 0.0, "end"), 0.5, "g1e5")
    sn("de.trace", {"on": False}, 0.0, "end")
    return out

# ----------------------------------------------------------------------------------------------------------------
# Small vector helpers (own code: the oracle shares only worldmap_ref/site_v1 with the stand-ins)
# ----------------------------------------------------------------------------------------------------------------

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a): return math.sqrt(dot(a, a))

def unit(a):
    n = norm(a)
    return (a[0] / n, a[1] / n, a[2] / n) if n > 0 else (0.0, 0.0, 0.0)

def dist(a, b): return norm(sub(a, b))

def lerp(a, b, t): return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t)

def angle_deg(a, b) -> float:
    """Angle between two vectors (robust for tiny angles)."""
    return math.degrees(math.atan2(norm(cross(a, b)), dot(a, b)))

def qrot(q, v):
    """Unity `q * v` for q = (x, y, z, w)."""
    u = (q[0], q[1], q[2])
    t = mul(cross(u, v), 2.0)
    return add(add(v, mul(t, q[3])), cross(u, t))

def f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]

def vec(d: dict, *names, n=3):
    for k in names:
        v = d.get(k)
        if isinstance(v, (list, tuple)) and len(v) >= n and all(isinstance(c, (int, float)) for c in v[:n]):
            return tuple(float(c) for c in v[:n])
    return None

def num(d: dict, *names):
    for k in names:
        v = d.get(k)
        if isinstance(v, bool):
            continue
        if isinstance(v, (int, float)):
            return v
    return None

def pick(d: dict, *names):
    for k in names:
        if isinstance(d, dict) and k in d and d[k] is not None:
            return d[k]
    return None

# ----------------------------------------------------------------------------------------------------------------
# JSONL reading
# ----------------------------------------------------------------------------------------------------------------

_EMBED = re.compile(r"(s1\.[A-Za-z0-9_.]+)\s+(\{.*\})\s*$")
_KV = re.compile(r"(\w+)=(\([^)]*\)|\[[^\]]*\]|\S+)")
_NUM = re.compile(r"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?")

def _kv_value(v: str):
    v = v.rstrip(",;")
    if v[:1] in "([" and v[-1:] in ")]":
        parts = [x.strip() for x in v[1:-1].split(",") if x.strip()]
        try:
            return [float(x) for x in parts]
        except ValueError:
            return parts
    if v in ("true", "True"):
        return True
    if v in ("false", "False"):
        return False
    m = _NUM.match(v)
    if m and m.end() == len(v):
        return float(v) if any(c in v for c in ".eE") else int(v)
    if m and v[m.end():] in ("m", "ms", "deg", "s"):          # "ahead=10m"
        return float(m.group(0))
    return v

def parse_kv(text: str) -> dict:
    """`key=value` pairs of a DE.* log line; tuples "(a, b, c)" become lists of floats."""
    return {k: _kv_value(v) for k, v in _KV.findall(text)}

def _fwd_from_yaw_pitch(yaw_deg, pitch_deg):
    y, p = math.radians(yaw_deg), math.radians(pitch_deg)
    return (math.sin(y) * math.cos(p), math.sin(p), math.cos(y) * math.cos(p))

def _nums(kv, *keys):
    vals = [kv.get(k) for k in keys]
    return [float(v) for v in vals] if all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in vals) \
        else None

def _from_text(text: str):
    """Adapters for the plugins' DE.* text lines: SN `DE.CamTrace`, `DE.CamStep`, `DE.S1 sec` (1 Hz), `DE.Cam stats` (every 300 frames), `DE.Cam rig` (once), `DE.SnPixel`
 (de.pixel); SE `DE.Look`, `DE.Pixel`. Returns (name, fields) or None."""
    if text.startswith("DE.CamTrace f="):
        kv = parse_kv(text)
        d = {"frame": kv.get("f"), "qpcUsed": kv.get("qpcUsed"), "qpcFrequency": kv.get("qpcFreq"),
             "tickRot": kv.get("newestTick"), "qpcCaptureRot": kv.get("newestQpc"), "tickNew": kv.get("presented"),
             "tickOld": kv.get("older"), "alpha": kv.get("alpha"), "outcome": str(kv.get("outcome", "")).lower(),
             "fov": kv.get("fov"), "fovSeen": kv.get("fovSeen"), "fovApplied": kv.get("fovApplied"),
             "c1Fov": kv.get("c1Fov"), "yawDeg": kv.get("yaw"), "pitchDeg": kv.get("pitch"), "source": "DE.CamTrace"}
        if _nums(kv, "x", "y", "z"):
            d["camPos"] = _nums(kv, "x", "y", "z")
        if _nums(kv, "qx", "qy", "qz", "qw"):
            d["camRot"] = _nums(kv, "qx", "qy", "qz", "qw")
        if _nums(kv, "tx", "ty", "tz"):
            d["camPosCmd"] = _nums(kv, "tx", "ty", "tz")
        if _nums(kv, "tqx", "tqy", "tqz", "tqw"):
            d["camRotCmd"] = _nums(kv, "tqx", "tqy", "tqz", "tqw")
        if isinstance(kv.get("yaw"), (int, float)) and isinstance(kv.get("pitch"), (int, float)):
            d["camFwd"] = list(_fwd_from_yaw_pitch(kv["yaw"], kv["pitch"]))
        return "s1.cam", d
    if text.startswith("DE.Cam stats "):
        kv = parse_kv(text)
        return "s1.camStats", {k: kv.get(k) for k in ("frames", "bracketed", "extrapolated", "held", "fovDrift", "gated",
                                                      "extraPreCull", "resamplesLastFrame")}
    if text.startswith("DE.Cam rig "):
        kv = parse_kv(text)
        return "s1.camRig", {"optionsFov": kv.get("optionsFov"), "fov": kv.get("fov")}
    if text.startswith("DE.SnPixel label="):
        kv = parse_kv(text)
        return "s1.pixel", {"label": kv.get("label"), "seTick": kv.get("seTick"), "frame": kv.get("frame"),
                            "snPx": kv.get("px"), "screen": kv.get("screen"), "fov": kv.get("fov"),
                            "pointSn": kv.get("pointSn"), "camPos": kv.get("camPos"), "camRot": kv.get("camRot"),
                            "source": "DE.SnPixel"}
    if text.startswith("DE.CamStep "):
        kv = parse_kv(text)
        return "s1.camStep", {"qpcUsed": kv.get("qpcUsed"), "deltaYawDeg": kv.get("deltaYawDeg"),
                              "yawDeg": kv.get("yawDeg"), "tickRot": kv.get("newestTick"), "frame": kv.get("frame")}
    if text.startswith("DE.S1 sec "):
        kv = parse_kv(text)
        return "s1.probe", {"camPos": kv.get("cam"), "depthOf": kv.get("depth"), "oceanLevel": kv.get("oceanLevel"),
                            "biome": kv.get("biome"), "isUnderwater": kv.get("isUnderwater"),
                            "cameraInside": kv.get("fogCameraInside"), "playerToCamM": kv.get("playerCamDist"),
                            "statsFrozen": kv.get("frozenStats"), "seTick": kv.get("presentedSeTick"),
                            "fov": kv.get("fov"), "source": "DE.S1 sec"}
    if text.startswith("DE.Look "):
        kv = parse_kv(text)
        return "s1.look", {"dir": kv.get("dir"), "tick": kv.get("cmdTick"), "qpc": kv.get("cmdQpc"),
                           "step": kv.get("step"), "forwardSn": kv.get("forwardSn")}
    if text.startswith("DE.Pixel "):
        kv = parse_kv(text)
        return "s1.pixel", adapt_se_pixel({"px": kv.get("px"), "sn": kv.get("sn"), "se": kv.get("se"),
                                           "vFovDeg": kv.get("vFovDeg"), "viewport": kv.get("viewport"),
                                           "tick": kv.get("tick"), "s": kv.get("s"), "ahead": kv.get("ahead")})
    return None

def adapt_se_pixel(d: dict) -> dict:
    """An SE pixel reply or log in either shape -> sePx, screen, viewport, vFovDeg, pointSn, pointSe, tick, aheadM."""
    if not isinstance(d, dict):
        return d
    out = dict(d)
    if "sePx" not in out and isinstance(d.get("px"), (list, tuple)):
        out["sePx"] = list(d["px"])
    if "pointSn" not in out and isinstance(d.get("sn"), (list, tuple)):
        out["pointSn"] = list(d["sn"])
    if "pointSe" not in out and isinstance(d.get("se"), (list, tuple)):
        out["pointSe"] = list(d["se"])
    vp = d.get("viewport")
    if "screen" not in out and isinstance(vp, (list, tuple)) and len(vp) >= 4:
        out["screen"] = [vp[2], vp[3]]
    if "aheadM" not in out and isinstance(d.get("ahead"), (int, float)):
        out["aheadM"] = d["ahead"]
    if out.get("tick") is None and isinstance(d.get("line"), str):
        m = re.search(r"\btick=(\d+)", d["line"])
        if m:
            out["tick"] = int(m.group(1))
    return out

# SE frame records of the SE plugin's own S1 log (ev "wm3" for WM-3 path samples, "c1trace" for the frames after a
# teleport or look): "seP" is the SE camera point, "check" its label; mapped onto s1.frame
SE_FRAME_ALIASES = ("wm3", "c1trace")

def normalise(rec: dict):
    """One raw JSON record -> an S1 event dict with "_ev" (name) and "_t" (raw time), or None."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if isinstance(name, str) and (name.startswith("s1.") or name in SE_FRAME_ALIASES):
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
            {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
        if name in SE_FRAME_ALIASES:
            if "camSe" not in data and "seP" in data:
                data["camSe"] = data["seP"]
            data.setdefault("label", "wm3" if name == "wm3" else None)
            data["traceLabel"] = data.get("check")
            name = "s1.frame"
    text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
    if isinstance(text, str) and "s1." in text:
        m = _EMBED.search(text)
        if m:
            try:
                emb = json.loads(m.group(2))
            except ValueError:
                emb = None
            if isinstance(emb, dict):
                name = m.group(1)
                data = dict(data or {})
                data.update(emb)
    if data is None and isinstance(text, str) and text.startswith("DE."):
        got = _from_text(text)
        if got:
            name, data = got
            data = {k: v for k, v in data.items() if v is not None}
    if data is None or not isinstance(name, str):
        return None
    data["_ev"] = name
    data["_t"] = rec.get("t", rec.get("utc"))
    return data

def read_jsonl(paths) -> list:
    """Every JSON object of the files (non-JSON lines skipped)."""
    out = []
    if isinstance(paths, str):
        paths = [paths]
    for path in paths or ():
        if not path or not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line.startswith("{"):
                    continue
                try:
                    rec = json.loads(line)
                except ValueError:
                    continue
                if isinstance(rec, dict):
                    out.append(rec)
    return out

def s1_events(records) -> list:
    out = []
    for rec in records:
        ev = normalise(rec)
        if ev is not None:
            out.append(ev)
    return out

def text_lines(records) -> list:
    """Every log text line of the records ("msg" or "text"), for greps of DE.* lines."""
    out = []
    for rec in records:
        for k in ("msg", "text"):
            v = rec.get(k)
            if isinstance(v, str):
                out.append(v)
                break
    return out

def qpc_frequency(*event_lists) -> int:
    for evs in event_lists:
        for e in evs:
            f = num(e, "qpcFrequency")
            if f:
                return int(f)
    if os.name == "nt":
        try:
            import ctypes
            v = ctypes.c_int64()
            ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(v))
            if v.value > 0:
                return int(v.value)
        except (OSError, AttributeError):
            pass
    return 10_000_000

class Logs:
    """Indexes the S1 events of one session (SE log, SN log, optional launcher session log)."""

    def __init__(self, se_events, sn_events, session_records=()):
        self.se = [e for e in se_events]
        self.sn = [e for e in sn_events]
        self.session = list(session_records)
        by = lambda evs, n: [e for e in evs if e["_ev"] == n]  # noqa: E731
        sites = by(self.se, "s1.site")
        self.site = sites[-1] if sites else None
        self.frames = sorted(by(self.se, "s1.frame"), key=lambda f: (num(f, "worldEpoch") or 0, num(f, "tick") or 0))
        if self.site is None:
            # the SE plugin's own frame records carry C and Rs in every line
            withc = [f for f in self.frames if vec(f, "C") and num(f, "Rs") is not None]
            if withc:
                self.site = {"_ev": "s1.site", "C": withc[-1]["C"], "Rs": withc[-1]["Rs"], "source": "frame records"}
        self.frame_by_tick = {}
        epochs = set()
        for f in self.frames:
            t = num(f, "tick")
            if t is None:
                continue
            epochs.add(num(f, "worldEpoch") or 0)
            self.frame_by_tick[int(t)] = f       # a later epoch wins (warned below)
        self.multi_epoch = len(epochs) > 1
        self.cmds = by(self.se, "s1.cmd")
        self.yaw = sorted(by(self.se, "s1.yawStep"), key=lambda e: num(e, "qpc") or num(e, "tick") or 0)
        if not self.yaw:
            # the SE plugin's DE.Look lines for "yaw+N" looks (cmdTick, cmdQpc)
            looks = [e for e in by(self.se, "s1.look") if str(e.get("dir", "")).startswith("yaw")]
            self.yaw = [dict(e, index=i) for i, e in enumerate(sorted(looks, key=lambda e: num(e, "qpc") or 0))]
        self.se_pixels = {e.get("label"): e for e in by(self.se, "s1.pixel") if e.get("label")}
        self.se_holds = by(self.se, "s1.hold")
        self.cams = sorted(by(self.sn, "s1.cam"), key=lambda e: (num(e, "qpcUsed") or 0, num(e, "frame") or 0))
        self.cam_stats = by(self.sn, "s1.camStats")
        rigs = by(self.sn, "s1.camRig")
        self.cam_rig = rigs[-1] if rigs else None
        self.probes = by(self.sn, "s1.probe")
        self.sn_pixels = {e.get("label"): e for e in by(self.sn, "s1.pixel")}
        self.fmod = by(self.sn, "s1.fmod")
        self.captures = by(self.sn, "s1.capture")
        self.freq = qpc_frequency(self.se, self.sn)
        self._assign_labels()

    def _assign_labels(self):
        """Frames without a plan label take the label of the latest command: s1.cmd events (tick), else the launcher's
 session log, by QPC: a frame captured at least one SE tick after the harness sent a command carries that
 command's label (the command runs in SE's next update, so an earlier frame may still show the old pose)."""
        sent = [r for r in self.session if r.get("ev") == "cmd" and r.get("side") == "se" and
                isinstance(r.get("args"), dict) and r["args"].get("label") and num(r, "qpc") is not None]
        plan_labels = {r["args"]["label"] for r in sent} | {"wm3"}
        if all(f.get("label") for f in self.frames) and (not sent or all(f.get("label") in plan_labels
                                                                         for f in self.frames)):
            return
        if sent and any(num(f, "qpcCapture") is not None for f in self.frames):
            settle = self.freq / TICK_HZ
            marks = sorted((num(r, "qpc"), r["args"]["label"]) for r in sent)
            i = -1
            for f in sorted(self.frames, key=lambda f: num(f, "qpcCapture") or 0):
                if f.get("label") in plan_labels:
                    continue
                q = num(f, "qpcCapture")
                if q is None:
                    continue
                while i + 1 < len(marks) and marks[i + 1][0] <= q:
                    i += 1
                # within one tick of the latest command the frame may still show the previous pose: no label
                f["label"] = marks[i][1] if i >= 0 and q >= marks[i][0] + settle else None
            return
        marks = []
        for c in self.cmds:
            if c.get("phase") in (None, "start") and num(c, "tick") is not None and c.get("label"):
                marks.append((int(num(c, "tick")), c["label"]))
        if not marks:
            for r in self.session:
                if r.get("ev") == "reply" and isinstance(r.get("reply"), dict) and r.get("side") == "se":
                    t = num(r["reply"], "tick", "seTick", "startTick")
                    lab = (r.get("args") or {}).get("label")
                    if t is not None and lab:
                        marks.append((int(t), lab))
        marks.sort()
        if not marks:
            return
        i = -1
        for f in self.frames:
            if f.get("label"):
                continue
            t = num(f, "tick") or 0
            while i + 1 < len(marks) and marks[i + 1][0] <= t:
                i += 1
            if i >= 0:
                f["label"] = marks[i][1]

    def frames_labelled(self, label: str, prefix: bool = False) -> list:
        if prefix:
            return [f for f in self.frames if str(f.get("label") or "").startswith(label)]
        return [f for f in self.frames if f.get("label") == label]

    def sn_paired(self, ticks: set) -> list:
        """SN frames whose position bracket (tickOld, tickNew) and orientation snapshot lie in `ticks`."""
        out = []
        for c in self.cams:
            tn, to = num(c, "tickNew"), num(c, "tickOld")
            tr = num(c, "tickRot")
            if tn is None:
                continue
            if int(tn) in ticks and (to is None or int(to) in ticks) and (tr is None or int(tr) in ticks):
                out.append(c)
        return out

# ----------------------------------------------------------------------------------------------------------------
# The oracle (worldmap_ref + site_v1)
# ----------------------------------------------------------------------------------------------------------------

class Oracle:
    def __init__(self, C, Rs):
        a = site_v1.ANCHOR
        self.C, self.Rs = tuple(C), float(Rs)
        self.site = wm.Site(C=self.C, Rs=self.Rs, U0=a["U0"], N0=a["N0"], psi_deg=a["psi_deg"], O=a["O"])

    def F(self, P):
        return self.site.se_to_sn(P)

    def mapdir(self, P, w):
        return self.site.dir_se_to_sn(P, w)

    def s(self, P):
        return self.site.scale_at(P)

def oracle_for(lg: Logs, report=None):
    if lg.site is not None:
        C, Rs = vec(lg.site, "C"), num(lg.site, "Rs")
        if C is not None and Rs is not None:
            return Oracle(C, Rs), "s1.site"
    return None, "no s1.site record in the SE log"

# ----------------------------------------------------------------------------------------------------------------
# Report rows
# ----------------------------------------------------------------------------------------------------------------

PASS, FAIL, MISSING, MANUAL, INFO, RECORD = "PASS", "FAIL", "MISSING", "MANUAL", "INFO", "RECORD"

class Report:
    def __init__(self, strict: bool = True):
        self.rows = []
        self.strict = strict

    def row(self, cid, what, status, measured="", threshold="", part=None, required=True, **detail):
        r = {"id": cid, "what": what, "status": status, "measured": measured, "threshold": threshold, "part": part,
             "required": required}
        if detail:
            r["detail"] = detail
        self.rows.append(r)
        return r

    def check(self, cid, what, value, limit, part, fmt="%.4g", unit="", required=True, cmp="le", **detail):
        """value <= limit (cmp 'le') or value >= limit (cmp 'ge'); None value -> MISSING."""
        if value is None:
            return self.row(cid, what, MISSING, "not logged", ("<= " if cmp == "le" else ">= ") + (fmt % limit) +
                            unit, part, required, **detail)
        ok = value <= limit if cmp == "le" else value >= limit
        return self.row(cid, what, PASS if ok else FAIL, (fmt % value) + unit,
                        ("<= " if cmp == "le" else ">= ") + (fmt % limit) + unit, part, required, **detail)

    def ok(self) -> bool:
        bad = (FAIL, MISSING) if self.strict else (FAIL,)
        return not any(r["required"] and r["status"] in bad for r in self.rows)

def table(rows, width=150) -> str:
    """Plain-text table: check, status, measured, threshold, what."""
    out = ["%-12s %-7s %-26s %-22s %s" % ("check", "status", "measured", "threshold", "what")]
    out.append("-" * min(width, 140))
    for r in rows:
        out.append("%-12s %-7s %-26s %-22s %s" % (r["id"], r["status"] + ("" if r.get("required", True) else "*"),
                                                    str(r["measured"])[:26], str(r["threshold"])[:22], r["what"]))
    return "\n".join(out)

# ----------------------------------------------------------------------------------------------------------------
# Checks
# ----------------------------------------------------------------------------------------------------------------

def _still_runs(frames, pos_eps=1e-3, dir_eps=1e-6) -> list:
    runs, cur = [], []
    for f in frames:
        P, fw = vec(f, "camSe"), vec(f, "fwdSe")
        if P is None:
            continue
        if cur:
            Pp, fp = vec(cur[-1], "camSe"), vec(cur[-1], "fwdSe")
            t, tp = num(f, "tick") or 0, num(cur[-1], "tick") or 0
            same = dist(P, Pp) <= pos_eps and (fw is None or fp is None or dist(fw, fp) <= dir_eps) and t - tp <= 2
            if not same:
                runs.append(cur)
                cur = []
        cur.append(f)
    if cur:
        runs.append(cur)
    return runs

def _expected_sn_pos(lg: Logs, orc: Oracle, cam):
    """Oracle position for one SN frame: lerp(F(P_old), F(P_new), alpha), or F(P_new) for a snap/held frame."""
    tn = num(cam, "tickNew")
    to = num(cam, "tickOld")
    fn = lg.frame_by_tick.get(int(tn)) if tn is not None else None
    if fn is None or vec(fn, "camSe") is None:
        return None
    pn = orc.F(vec(fn, "camSe"))
    out = cam.get("outcome")
    a = num(cam, "alpha")
    if out in ("snap", "held") or to is None or a is None:
        return pn
    fo = lg.frame_by_tick.get(int(to))
    if fo is None or vec(fo, "camSe") is None:
        return None
    return lerp(orc.F(vec(fo, "camSe")), pn, a)

def _sn_forward(cam):
    """The applied camera's forward: camRot * +Z, or camFwd (from the logged yaw and pitch)."""
    q = vec(cam, "camRot", n=4)
    if q:
        return qrot(q, (0.0, 0.0, 1.0))
    return vec(cam, "camFwd")

def _sn_basis(cam):
    """(forward, up) of the applied camera; without camRot the roll is taken as 0 (yaw/pitch traces)."""
    q = vec(cam, "camRot", n=4)
    if q:
        return qrot(q, (0.0, 0.0, 1.0)), qrot(q, (0.0, 1.0, 0.0))
    f = vec(cam, "camFwd")
    if f is None:
        return None, None
    y, p = num(cam, "yawDeg"), num(cam, "pitchDeg")
    if y is None or p is None:
        y, p = math.degrees(math.atan2(f[0], f[2])), math.degrees(math.asin(max(-1.0, min(1.0, f[1]))))
    yr, pr = math.radians(y), math.radians(p)
    return f, (-math.sin(yr) * math.sin(pr), math.cos(pr), -math.cos(yr) * math.sin(pr))

def _pub_forward(frame):
    q = vec(frame, "camRot", n=4)
    return qrot(q, (0.0, 0.0, 1.0)) if q else None

def _maxv(values):
    vals = [v for v in values if v is not None]
    return max(vals) if vals else None

def _qangle_deg(a, b) -> float:
    """Rotation angle between two quaternions (sign-insensitive), degrees."""
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(x * x for x in b))
    if na == 0 or nb == 0:
        return 180.0
    d = min(1.0, abs(sum(x * y for x, y in zip(a, b))) / (na * nb))
    return 2.0 * math.degrees(math.acos(d))

def check_placement(cid, part, cams, p, rep: Report):
    """Review finding 7: the Unity camera transform after placement (camPos/camRot) vs the commanded pose
 (camPosCmd/camRotCmd) on the frames of one part; required (MISSING when the trace has no commanded pose)."""
    c = p.get("place", PLAN["place"])
    pos = [dist(vec(x, "camPos"), vec(x, "camPosCmd")) for x in cams if vec(x, "camPos") and vec(x, "camPosCmd")]
    rot = [_qangle_deg(vec(x, "camRot", n=4), vec(x, "camRotCmd", n=4)) for x in cams
           if vec(x, "camRot", n=4) and vec(x, "camRotCmd", n=4)]
    rep.check(cid, "Subnautica camera placement: Unity transform vs the commanded position (%d frames)" % len(pos),
              _maxv(pos), c["posTolM"], part, "%.3g", " m")
    rep.check(cid, "Subnautica camera placement: Unity transform vs the commanded orientation (%d frames)" % len(rot),
              _maxv(rot), c["rotTolDeg"], part, "%.3g", " deg")

def check_site(lg: Logs, orc, why, rep: Report):
    if orc is None:
        rep.row("S1.site", "site record (C, Rs) logged by SE (s1.site)", MISSING, why, "present", "site")
        return
    rs = orc.Rs
    exact = struct.pack("<f", rs) == struct.pack("<f", site_v1.RS_EXPECTED) and f32(rs) == rs
    rep.row("S1.G2-E4", "Rs in the s1.site record equals 60000.00390625f bit for bit (Water Mod GetPhysicalData)",
            PASS if exact else FAIL, repr(rs), repr(site_v1.RS_EXPECTED), "site")
    if lg.multi_epoch:
        rep.row("S1.site", "SE frames from more than one world epoch (ticks keyed by the latest)", INFO, "", "", "site",
                required=False)

def check_wm2(lg: Logs, orc: Oracle, p: dict, rep: Report):
    c = p["wm2"]
    part, cid = "wm2", "S1.WM-2"
    target, fwd_axis = tuple(c["sn"]), tuple(c["forward"])
    frames = [f for f in lg.frames_labelled(c["label"]) if vec(f, "camSe")]
    if not frames:
        rep.row(cid, "SE samples labelled %r (de.teleport to F^-1%s)" % (c["label"], target), MISSING, "0 frames",
                ">= %d ticks" % int(c["holdS"] * TICK_HZ), part)
        return
    run = max(_still_runs(frames), key=len)
    hold_se = (num(run[-1], "tick") - num(run[0], "tick") + 1) / TICK_HZ
    ticks = {int(num(f, "tick")) for f in run}
    fp = [orc.F(vec(f, "camSe")) for f in run]
    # the same hold seen by Subnautica: the longest streak of presented frames at that F(P) (1 mm), by qpcUsed
    hold_sn, streak = 0.0, []
    for cam in lg.cams:
        v, q = vec(cam, "camPos"), num(cam, "qpcUsed")
        if v is not None and q is not None and dist(v, fp[0]) <= 1e-3:
            streak.append(q)
            hold_sn = max(hold_sn, (streak[-1] - streak[0]) / lg.freq)
        else:
            streak = []
    hold_s = max(hold_se, hold_sn)
    rep.check(cid, "camera held still (SE ticks %.2f s, Subnautica frames %.2f s)" % (hold_se, hold_sn), hold_s,
              c["holdS"], part, "%.2f", " s", cmp="ge")
    rep.check(cid, "oracle F(P) vs the commanded SN point %s (SE F^-1 and teleport)" % (target,),
              max(dist(x, target) for x in fp), c["posTolM"], part, "%.4g", " m")
    pub = [dist(vec(f, "camPos"), x) for f, x in zip(run, fp) if vec(f, "camPos")]
    rep.check(cid, "SE C1 camPos vs oracle F(P)", _maxv(pub), c["posTolM"], part, "%.4g", " m")
    ofw = [angle_deg(orc.mapdir(vec(f, "camSe"), vec(f, "fwdSe")), fwd_axis) for f in run if vec(f, "fwdSe")]
    rep.check(cid, "oracle forward mapdir(P, fwdSe) vs (0, 0, 1)", _maxv(ofw), c["fwdTolDeg"], part, "%.4g", " deg")
    pfw = [angle_deg(_pub_forward(f), fwd_axis) for f in run if _pub_forward(f)]
    rep.check(cid, "SE C1 camRot forward vs (0, 0, 1)", _maxv(pfw), c["fwdTolDeg"], part, "%.4g", " deg")
    sn = lg.sn_paired(ticks)
    perr = []
    for cam in sn:
        e = _expected_sn_pos(lg, orc, cam)
        if e is not None and vec(cam, "camPos"):
            perr.append(dist(vec(cam, "camPos"), e))
    rep.check(cid, "Subnautica camera position vs oracle F(P) (%d frames)" % len(perr), _maxv(perr), c["posTolM"],
              part, "%.4g", " m")
    sfw = [angle_deg(_sn_forward(cam), fwd_axis) for cam in sn if _sn_forward(cam)]
    rep.check(cid, "Subnautica camera forward vs (0, 0, 1) (%d frames)" % len(sfw), _maxv(sfw), c["fwdTolDeg"], part,
              "%.4g", " deg")
    check_placement(cid, part, sn, p, rep)
    dw = [num(f, "depthWm") for f in run if num(f, "depthWm") is not None]
    rep.check(cid, "SE Water Mod GetDepth - (%.2f)" % c["seDepth"],
              max(abs(d - c["seDepth"]) for d in dw) if dw else None, c["depthTolM"], part, "%.4g", " m")
    dy = [abs(num(f, "depthWm") - orc.F(vec(f, "camSe"))[1]) for f in run if num(f, "depthWm") is not None]
    rep.check(cid, "SE GetDepth vs oracle y", _maxv(dy), c["depthTolM"], part, "%.4g", " m")
    lo, hi = min(ticks), max(ticks)
    probes = [pr for pr in lg.probes if num(pr, "seTick") is not None and lo <= num(pr, "seTick") <= hi]
    dd = [abs(num(pr, "depthOf") - c["snDepth"]) for pr in probes if num(pr, "depthOf") is not None]
    rep.check(cid, "Subnautica Ocean.GetDepthOf - %.2f (%d probes)" % (c["snDepth"], len(dd)), _maxv(dd),
              c["depthTolM"], part, "%.4g", " m")
    biomes = sorted({str(pr.get("biome")) for pr in probes if pr.get("biome") is not None})
    if not biomes:
        rep.row(cid, "Subnautica biome at the camera", MISSING, "not logged", c["biome"], part)
    else:
        ok = biomes == [c["biome"]]
        emulated = any(pr.get("emulated") for pr in probes)
        rep.row(cid, "Subnautica biome at the camera%s" % (" (emulated by the stand-in)" if emulated else ""),
                PASS if ok else FAIL, ",".join(biomes), c["biome"], part)
    caps = [x for x in lg.captures if x.get("label") == c["label"] or c["capture"] in str(x.get("path", ""))]
    caps += [r for r in lg.session if r.get("ev") == "capture" and r.get("label") == c["label"]]
    if caps:
        paths = sorted({str(x.get("path") or (x.get("reply") or {}).get("path")) for x in caps})
        rep.row(cid, "capture shows the reef below (look at it)", MANUAL, ";".join(paths)[:60], "owner looks", part,
                required=False)
    else:
        rep.row(cid, "capture shows the reef below (look at it)", MISSING, "no capture logged", "owner looks", part,
                required=False)

def check_wm3(lg: Logs, orc: Oracle, p: dict, rep: Report):
    c = p["wm3"]
    part, cid = "wm3", "S1.WM-3"
    frames = [f for f in lg.frames_labelled(c["label"]) if vec(f, "camSe")]
    distinct = {tuple(round(x, 6) for x in vec(f, "camSe")) for f in frames}
    rep.check(cid, "distinct SE path samples", len(distinct), c["samples"], part, "%d", "", cmp="ge")
    if not frames:
        return
    fp = [orc.F(vec(f, "camSe")) for f in frames]
    rep.check(cid, "oracle y of the SE path - (%.2f) (SE at constant altitude)" % c["y"],
              max(abs(x[1] - c["y"]) for x in fp), c["yTolM"], part, "%.4g", " m")
    rep.check(cid, "oracle z of the SE path - %.2f (great circle heading east)" % c["fromSn"][2],
              max(abs(x[2] - c["fromSn"][2]) for x in fp), c["yTolM"], part, "%.4g", " m")
    span = max(x[0] for x in fp) - min(x[0] for x in fp)
    rep.row(cid, "path length along SN x (oracle)", PASS if abs(span - c["lengthM"]) <= c["lengthTolM"] else FAIL,
            "%.3f m" % span, "%.0f +- %.0f m" % (c["lengthM"], c["lengthTolM"]), part, required=False)
    pub = [vec(f, "camPos") for f in frames]
    rep.check(cid, "SE C1 camPos.y - (%.2f)" % c["y"], _maxv(abs(v[1] - c["y"]) for v in pub if v), c["yTolM"], part,
              "%.4g", " m")
    rep.check(cid, "SE C1 camPos vs oracle F(P)", _maxv(dist(v, x) for v, x in zip(pub, fp) if v), c["xTolM"], part,
              "%.4g", " m")
    ticks = {int(num(f, "tick")) for f in frames}
    sn = lg.sn_paired(ticks)
    ey, ex, n = [], [], 0
    for cam in sn:
        e = _expected_sn_pos(lg, orc, cam)
        v = vec(cam, "camPos")
        if e is None or v is None:
            continue
        n += 1
        ey.append(abs(v[1] - c["y"]))
        ex.append(abs(v[0] - e[0]))
    rep.check(cid, "Subnautica camera y - (%.2f) at every sample (%d frames)" % (c["y"], n), _maxv(ey), c["yTolM"],
              part, "%.4g", " m")
    rep.check(cid, "Subnautica camera x vs oracle (interpolated by the logged alpha)", _maxv(ex), c["xTolM"], part,
              "%.4g", " m")
    rep.check(cid, "Subnautica frames paired with path ticks", n, c["samples"], part, "%d", "", cmp="ge")
    interp = sum(1 for cam in sn if cam.get("outcome") == "bracketed" and num(cam, "tickOld") is not None and
                 num(cam, "tickNew") is not None and num(cam, "tickOld") != num(cam, "tickNew") and
                 0.0 < (num(cam, "alpha") or 0.0) < 1.0)
    rep.row(cid, "Subnautica frames interpolated between two path samples (review finding 18: steps < 20 m)",
            INFO if interp else MISSING, "%d" % interp, "> 0", part, required=False)
    check_placement(cid, part, sn, p, rep)

def check_wm4(lg: Logs, orc: Oracle, p: dict, rep: Report):
    c = p["wm4"]
    part = "wm4"
    allsn = []
    for name, pt in c["points"]:
        cid = "S1.WM-4"
        allf = [f for f in lg.frames_labelled(wm4_label(name), prefix=True) if vec(f, "camSe")]
        if not allf:
            rep.row(cid, "%s %s: SE samples" % (name, tuple(pt)), MISSING, "0 frames", "present", part)
            continue
        perr = max(dist(orc.F(vec(f, "camSe")), tuple(pt)) for f in allf)
        rep.check(cid, "%s: oracle F(P) vs the commanded point" % name, perr, c["posTolM"], part, "%.4g", " m",
                  required=False)
        g = [angle_deg(orc.mapdir(vec(f, "camSe"), vec(f, "gravSe")), c["gravity"]) for f in allf if vec(f, "gravSe")]
        rep.check(cid, "%s: mapped SE gravity direction vs (0, -1, 0)" % name, _maxv(g), c["tolDeg"], part, "%.4g",
                  " deg")
        for d, axis in c["dirs"]:
            fr = [f for f in lg.frames_labelled(wm4_label(name, d)) if vec(f, "camSe")]
            if not fr:
                rep.row(cid, "%s look %s: SE samples" % (name, d), MISSING, "0 frames", "present", part)
                continue
            o = [angle_deg(orc.mapdir(vec(f, "camSe"), vec(f, "fwdSe")), axis) for f in fr if vec(f, "fwdSe")]
            pub = [angle_deg(_pub_forward(f), axis) for f in fr if _pub_forward(f)]
            paired = lg.sn_paired({int(num(f, "tick")) for f in fr})
            allsn += paired
            sn = [angle_deg(_sn_forward(cam), axis) for cam in paired if _sn_forward(cam)]
            rep.check(cid, "%s look %s: oracle forward vs %s" % (name, d, axis), _maxv(o), c["tolDeg"], part,
                      "%.4g", " deg")
            rep.check(cid, "%s look %s: SE C1 forward" % (name, d), _maxv(pub), c["tolDeg"], part, "%.4g", " deg")
            rep.check(cid, "%s look %s: Subnautica forward (%d frames)" % (name, d, len(sn)), _maxv(sn), c["tolDeg"],
                      part, "%.4g", " deg")
    check_placement("S1.WM-4", part, allsn, p, rep)

def _pixel_pairs(lg: Logs):
    """label -> (se pixel event, sn pixel event); the launcher session log is the fallback source. When Subnautica
 logged no pixel for a label, it is computed from the logged Subnautica camera (position, orientation, FOV) of the
 first frame presenting the SE tick, through a pinhole projection on the SE screen size ("computed": the
 WorldToScreenPoint path itself is then not exercised)."""
    pairs = {}
    for lab, e in lg.se_pixels.items():
        pairs.setdefault(lab, [None, None])[0] = adapt_se_pixel(e)
    for lab, e in lg.sn_pixels.items():
        pairs.setdefault(lab, [None, None])[1] = e
    for r in lg.session:
        if r.get("ev") == "pixel" and r.get("label"):
            pr = pairs.setdefault(r["label"], [None, None])
            if pr[0] is None and isinstance(r.get("se"), dict) and r["se"].get("ok", True):
                pr[0] = adapt_se_pixel(r["se"])
            if pr[1] is None and isinstance(r.get("sn"), dict) and r["sn"].get("ok", True) and r["sn"].get("snPx"):
                pr[1] = r["sn"]
    for lab, pr in pairs.items():
        if pr[0] is not None and pr[1] is None:
            pr[1] = _computed_sn_pixel(lg, pr[0])
    return pairs

def _computed_sn_pixel(lg: Logs, se: dict):
    tick, point, screen = num(se, "tick"), vec(se, "pointSn"), vec(se, "screen", n=2)
    if tick is None or point is None or screen is None:
        return None
    for cam in lg.cams:
        t = num(cam, "tickRot", "tickNew")
        if t is None or t < tick or cam.get("outcome") in ("snap", "torn"):
            continue
        pos, (f, u) = vec(cam, "camPos"), _sn_basis(cam)
        fov = num(cam, "fov")
        if pos is None or f is None or fov is None:
            continue
        W, H = screen
        right = cross(u, f)
        d = sub(point, pos)
        lz = dot(d, f)
        if lz <= 0:
            return None
        k = (H / 2.0) / math.tan(math.radians(fov) / 2.0)
        return {"snPx": [W / 2.0 + k * dot(d, right) / lz, H / 2.0 - k * dot(d, u) / lz], "screen": [W, H],
                "fov": fov, "seTick": t, "computed": True}
    return None

def _tan_offset(px, centre, half_h, fov_deg):
    return (px - centre) / half_h * math.tan(math.radians(fov_deg) / 2.0)

def g1e5_case(lg: Logs, orc: Oracle, se, sn, elev_deg: float):
    """Measured, predicted and exact (oracle projection) SN-minus-SE pixel offsets, in SN screen pixels."""
    se_px, sn_px = vec(se, "sePx", n=2), vec(sn, "snPx", n=2)
    se_scr, sn_scr = vec(se, "screen", n=2), vec(sn, "screen", n=2)
    se_fov, sn_fov = num(se, "vFovDeg", "fov"), num(sn, "fov", "vFovDeg")
    if None in (se_px, sn_px, se_scr, sn_scr, se_fov, sn_fov):
        return None
    W, H = sn_scr
    hh = H / 2.0
    vp = se.get("viewport")
    ox, oy = (vp[0], vp[1]) if isinstance(vp, (list, tuple)) and len(vp) >= 4 else (0.0, 0.0)
    # SE point's tangent offsets from SE's own centre, mapped into the SN screen (both projections are pinhole)
    se_tx = _tan_offset(se_px[0], ox + se_scr[0] / 2.0, se_scr[1] / 2.0, se_fov)
    se_ty = _tan_offset(se_px[1], oy + se_scr[1] / 2.0, se_scr[1] / 2.0, se_fov)
    k = hh / math.tan(math.radians(sn_fov) / 2.0)
    meas = ((sn_px[0] - W / 2.0) - se_tx * k, (sn_px[1] - hh) - se_ty * k)
    P = vec(se, "camSe")
    fw, up = vec(se, "fwdSe"), vec(se, "upSe")
    fr = lg.frame_by_tick.get(int(num(se, "tick") or -1))
    if P is None and fr is not None:
        P, fw, up = vec(fr, "camSe"), vec(fr, "fwdSe"), vec(fr, "upSe")
    # s at the camera: the oracle's Rs/|P - C|; elevation alpha: of mapdir(P, fwdSe), else of the forward SE published
 # in C1 at that tick (SE frame records without fwdSe), else of the Subnautica camera's forward
    if P is not None:
        s = orc.s(P)
    elif num(sn, "camY") is not None or vec(sn, "camPos"):
        s = orc.Rs / (orc.Rs + (num(sn, "camY") if num(sn, "camY") is not None else vec(sn, "camPos")[1]))
    else:
        return None
    if P is not None and fw is not None:
        d_sn = unit(orc.mapdir(P, fw))
    elif fr is not None and vec(fr, "camRot", n=4):
        d_sn = qrot(vec(fr, "camRot", n=4), (0.0, 0.0, 1.0))
    elif vec(sn, "camRot", n=4):
        d_sn = qrot(vec(sn, "camRot", n=4), (0.0, 0.0, 1.0))
    else:
        return None
    alpha = math.asin(max(-1.0, min(1.0, d_sn[1])))
    delta = (s - 1.0) / 2.0 * math.sin(2.0 * alpha)
    pred = (0.0, k * math.tan(delta))
    exact = None
    if P is not None and fw is not None and up is not None:
        ahead = num(se, "aheadM") or 10.0
        X = add(P, mul(unit(fw), ahead))
        cam, fwd = orc.F(P), d_sn
        upv = orc.mapdir(P, up)
        upv = unit(sub(upv, mul(fwd, dot(upv, fwd))))
        right = cross(upv, fwd)          # Unity: local +X = Cross(up, forward)
        dvec = sub(orc.F(X), cam)
        lz = dot(dvec, fwd)
        if lz > 0:
            exact = (k * dot(dvec, right) / lz, -k * dot(dvec, upv) / lz)
    return {"measured": meas, "predicted": pred, "exact": exact, "s": s, "alphaDeg": math.degrees(alpha),
            "snFov": float(sn_fov), "seFov": float(se_fov), "snScreen": sn_scr}

def check_g1e5(lg: Logs, orc: Oracle, p: dict, rep: Report):
    """Nine cases per session at SE's configured FOV (D69); every row is labelled with the FOVs actually logged, and a RECORD row lists the FOV(s) this session covered (ROADMAP's three FOVs: --g1e5-coverage)."""
    c = p["g1e5"]
    part, cid = "g1e5", "S1.G1-E5"
    pairs = _pixel_pairs(lg)
    fovs = []
    for d in c["depths"]:
        for e in c["elevs"]:
            lab = g1e5_label(d, e)
            se, sn = pairs.get(lab, (None, None))
            if se is None or sn is None:
                rep.row(cid, "%s: SE and SN pixel logs" % lab, MISSING,
                        "se %s, sn %s" % ("ok" if se else "missing", "ok" if sn else "missing"), "both", part)
                continue
            r = g1e5_case(lg, orc, se, sn, e)
            if r is None:
                rep.row(cid, "%s: pixel fields" % lab, MISSING, "incomplete", "sePx/snPx/screen/fov/camSe", part)
                continue
            mx, my = r["measured"]
            px_, py_ = r["predicted"]
            level = abs(e) < 1e-6 or abs(e - 90.0) < 1e-6
            if level:
                err = max(abs(mx), abs(my))
                lim, what = c["zeroTolPx"], "|SN - SE| px (level/vertical look: 0)"
            else:
                err = max(abs(mx - px_), abs(my - py_))
                lim, what = c["tolPx"], "SN - SE px vs (s-1)/2 sin 2a = %+.2f px" % py_
            st = PASS if err <= lim else FAIL
            ex = r["exact"]
            if sn.get("computed"):
                what += " [SN pixel computed from the logged SN camera]"
            fovs.append(r["seFov"])
            rep.row(cid, "%s @ SE vFov %.2f / SN fov %.2f deg: %s" % (lab, r["seFov"], r["snFov"], what), st,
                    "dy %+.2f dx %+.2f" % (my, mx), "+-%.1f px" % lim, part,
                    s=round(r["s"], 6), alphaDeg=round(r["alphaDeg"], 4), predictedPx=round(py_, 3),
                    exactPx=None if ex is None else [round(ex[0], 3), round(ex[1], 3)], snFov=r["snFov"],
                    seFov=r["seFov"], snScreen=list(r["snScreen"]), case=lab, snComputed=bool(sn.get("computed")))
    distinct = _distinct_fovs(fovs, c.get("fovDistinctDeg", 1.0))
    rep.row(cid, "FOVs covered by this session: %s deg (ROADMAP needs %d distinct FOVs over sessions A, C, D: "
                 "check_wm.py --g1e5-coverage)" % (", ".join("%.2f" % f for f in distinct) or "-",
                                                    c.get("fovsForExit", 3)),
            RECORD if distinct else MISSING, "%d FOV(s)" % len(distinct), ">= %d over sessions" % c.get("fovsForExit", 3),
            part, required=False, fovs=distinct)

def _distinct_fovs(fovs, tol):
    out = []
    for f in sorted(x for x in fovs if x is not None):
        if not out or abs(f - out[-1]) >= tol:
            out.append(f)
    return out

def g1e5_coverage(paths, p: dict = None) -> dict:
    """S1.G1-E5 exit over several sessions (one FOV each, D69): every session's nine cases PASS, and at least
 `fovsForExit` distinct SE FOVs (`fovDistinctDeg` apart) among the sessions. Reads s1-results.json files."""
    p = p or PLAN
    c = p["g1e5"]
    need = len(c["depths"]) * len(c["elevs"])
    rows, fovs, ok_all = [], [], True
    for path in paths:
        if isinstance(path, dict):
            res, path = path, path.get("out", "(in memory)")
        else:
            with open(path, encoding="utf-8") as f:
                res = json.load(f)
        cases = [r for r in res.get("rows", []) if r.get("id") == "S1.G1-E5" and (r.get("detail") or {}).get("case")]
        passed = [r for r in cases if r.get("status") == PASS]
        sfov = sorted({round(r["detail"]["seFov"], 2) for r in cases if r["detail"].get("seFov") is not None})
        good = len(cases) == need and len(passed) == need and len(sfov) == 1
        ok_all &= good
        if good:
            fovs.append(sfov[0])
        rows.append({"session": path, "cases": len(cases), "passed": len(passed), "fovs": sfov, "ok": good})
    distinct = _distinct_fovs(fovs, c.get("fovDistinctDeg", 1.0))
    ok = ok_all and len(distinct) >= c.get("fovsForExit", 3)
    return {"ok": ok, "sessions": rows, "distinctFovs": distinct, "need": c.get("fovsForExit", 3)}

def check_n7(lg: Logs, p: dict, rep: Report):
    """S1.N7. The plugin corrects any drift in onPreCull, so the rendered FOV alone can never fail:
 the rows use what the camera had BEFORE that correction (fovSeen, against the C1 vFovDeg the plugin had applied,
 fovApplied), the rendered FOV against this frame's C1 (c1Fov), and the plugin's drift counter. Required. Under
 S1.M6 (PDACameraFOVControl left on) the FOV is pulled toward the options value every frame: rows 1 and 3 turn red."""
    c = p["n7"]
    tol = c["tolDeg"]
    seen = [(abs(num(x, "fovSeen") - num(x, "fovApplied")), x) for x in lg.cams
            if num(x, "fovSeen") is not None and num(x, "fovApplied") is not None and num(x, "fovApplied") > 0]
    errs = [e for e, _x in seen]
    bad = sum(1 for e in errs if e > tol)
    span = None
    qs = [num(x, "qpcUsed") for _e, x in seen if num(x, "qpcUsed") is not None]
    if len(qs) >= 2:
        span = (max(qs) - min(qs)) / float(lg.freq)
    rep.row("S1.N7", "Subnautica fieldOfView found before the per-frame assertion = the C1 vFovDeg applied, every traced "
                     "frame (%d frames over %s s, %d over)" % (len(errs), "%.0f" % span if span is not None else "-", bad),
            (PASS if bad == 0 else FAIL) if errs else MISSING, "max %.4g deg" % max(errs) if errs else "not logged",
            "<= %.2f deg" % tol, "n7")
    rend = [abs(num(x, "fov") - num(x, "c1Fov")) for x in lg.cams if num(x, "fov") is not None and num(x, "c1Fov") is not None]
    rep.row("S1.N7", "rendered fieldOfView = this frame's C1 vFovDeg (%d frames)" % len(rend),
            (PASS if max(rend) <= tol else FAIL) if rend else MISSING, "max %.4g deg" % max(rend) if rend else "not logged",
            "<= %.2f deg" % tol, "n7")
    drift = [num(s, "fovDrift") for s in lg.cam_stats if num(s, "fovDrift") is not None]
    rep.row("S1.N7", "plugin FOV drift counter (DE.Cam stats fovDrift, %d windows)" % len(drift),
            (PASS if sum(drift) == 0 else FAIL) if drift else MISSING, "%d" % sum(drift) if drift else "not logged", "0",
            "n7")
    # cross-check through the SE log (transport of vFovDeg): the rendered FOV vs SE's c1trace vFovDeg at that tick
    pair = []
    for cam in lg.cams:
        t = num(cam, "tickRot", "tickNew")
        f = lg.frame_by_tick.get(int(t)) if t is not None else None
        fov, sefov = num(cam, "fov"), (num(f, "vFovDeg") if f else None)
        if fov is not None and sefov is not None:
            pair.append(abs(fov - sefov))
    if pair:
        rep.row("S1.N7", "rendered fieldOfView vs SE's logged vFovDeg at the same tick (%d paired frames)" % len(pair),
                PASS if max(pair) <= tol else FAIL, "max %.4g deg" % max(pair), "<= %.2f deg" % tol, "n7",
                required=False)
    # precondition: SE's FOV must differ from Subnautica's options FOV, else neither S1.N7 nor S1.M6 discriminates
    opt = num(lg.cam_rig, "optionsFov") if lg.cam_rig else None
    c1 = [num(x, "c1Fov") for x in lg.cams if num(x, "c1Fov") is not None] or \
        [num(f, "vFovDeg") for f in lg.frames if num(f, "vFovDeg") is not None]
    if opt is None or not c1:
        rep.row("S1.N7", "precondition: C1 vFovDeg differs from Subnautica's options FOV", MISSING,
                "optionsFov %s" % ("-" if opt is None else "%.2f" % opt), ">= %.1f deg apart" % c["optionsDiffMinDeg"],
                "n7")
    else:
        diff = min(abs(v - opt) for v in c1)
        rep.row("S1.N7", "precondition: C1 vFovDeg %.2f vs options FOV %.2f deg" % (c1[-1], opt),
                PASS if diff >= c["optionsDiffMinDeg"] else FAIL, "%.2f deg apart" % diff,
                ">= %.1f deg apart" % c["optionsDiffMinDeg"], "n7")

CHECKS = {"wm2": check_wm2, "wm3": check_wm3, "wm4": check_wm4, "g1e5": check_g1e5}

def evaluate(se_records, sn_records, session_records=(), p: dict = None, only=None, strict: bool = True) -> dict:
    p = p or PLAN
    lg = Logs(s1_events(se_records), s1_events(sn_records), session_records)
    rep = Report(strict)
    orc, why = oracle_for(lg)
    check_site(lg, orc, why, rep)
    parts = [x for x in (only or ("wm2", "wm3", "wm4", "g1e5")) if x in CHECKS]
    if orc is not None:
        for part in parts:
            CHECKS[part](lg, orc, p, rep)
    else:
        for part in parts:
            rep.row("S1." + {"wm2": "WM-2", "wm3": "WM-3", "wm4": "WM-4", "g1e5": "G1-E5"}[part],
                    "needs the SE site record", MISSING, why, "s1.site", part)
    if not only or "n7" in (only or ()):
        check_n7(lg, p, rep)
    summary = {"seFrames": len(lg.frames), "snFrames": len(lg.cams), "probes": len(lg.probes),
               "sePixels": len(lg.se_pixels), "snPixels": len(lg.sn_pixels), "site": why,
               "C": list(orc.C) if orc else None, "Rs": orc.Rs if orc else None}
    return {"tool": "check_wm", "schema": SCHEMA, "ok": rep.ok(), "rows": rep.rows, "summary": summary,
            "quick": bool(p.get("quick"))}

# ----------------------------------------------------------------------------------------------------------------
# Self-test: synthetic logs generated with the reference map (green), then two mutants (red)
# ----------------------------------------------------------------------------------------------------------------

def _look_q(f, u):
    """Unity LookRotation(f, u) as (x, y, z, w), w >= 0 (own small copy for the synthetic data)."""
    Z = unit(f)
    X = unit(cross(u, Z))
    Y = cross(Z, X)
    m00, m01, m02, m10, m11, m12, m20, m21, m22 = X[0], Y[0], Z[0], X[1], Y[1], Z[1], X[2], Y[2], Z[2]
    tr = m00 + m11 + m22
    if tr > 0:
        S = math.sqrt(tr + 1.0) * 2
        q = ((m21 - m12) / S, (m02 - m20) / S, (m10 - m01) / S, 0.25 * S)
    elif m00 > m11 and m00 > m22:
        S = math.sqrt(1.0 + m00 - m11 - m22) * 2
        q = (0.25 * S, (m01 + m10) / S, (m02 + m20) / S, (m21 - m12) / S)
    elif m11 > m22:
        S = math.sqrt(1.0 + m11 - m00 - m22) * 2
        q = ((m01 + m10) / S, 0.25 * S, (m12 + m21) / S, (m02 - m20) / S)
    else:
        S = math.sqrt(1.0 + m22 - m00 - m11) * 2
        q = ((m02 + m20) / S, (m12 + m21) / S, 0.25 * S, (m10 - m01) / S)
    n = math.sqrt(sum(x * x for x in q))
    q = tuple(x / n for x in q)
    return tuple(-x for x in q) if q[3] < 0 else q

SESSION_FOV = 70.0
OPTIONS_FOV = 60.0

def synthetic_logs(mutant: str = None, p: dict = None, fov: float = SESSION_FOV):
    """In-memory SE and SN records for wm2/wm3/wm4/g1e5 at 60 Hz, SN = the snapshot itself (alpha 1), at one session FOV.
 Mutants: mapdir-origin, naive-map (the map), fov-drift (S1.M6: PDACameraFOVControl pulls the FOV toward the options
 value before the plugin's assertion), placement (the Unity camera 2 mm off the commanded pose)."""
    p = p or plan(quick=True)
    s = site_v1.SITE_V1
    C, Rs = s.C, s.Rs
    E0, U0, N0 = s.E0, s.U0, s.N0

    def frame_at(T):
        return (E0, U0, N0) if mutant in ("mapdir-origin", "naive-map") else s.frame_at(T)

    def F(T):
        if mutant == "naive-map":
            v = sub(T, C)
            return (dot(v, E0), dot(v, U0) - Rs, dot(v, N0))
        return s.se_to_sn(T)

    def Finv(Q):
        if mutant == "naive-map":
            return add(C, add(add(mul(E0, Q[0]), mul(U0, Rs + Q[1])), mul(N0, Q[2])))
        return s.sn_to_se(Q)

    def md(T, w):
        E, U, N = frame_at(T)
        return (dot(w, E), dot(w, U), dot(w, N))

    def mdi(T, d):
        E, U, N = frame_at(T)
        return add(add(mul(E, d[0]), mul(U, d[1])), mul(N, d[2]))

    se, sn = [{"ev": "s1.site", "C": list(C), "Rs": Rs, "siteVersion": 1, "qpcFrequency": 10_000_000}], []
    sn.append({"event": "s1.camRig", "data": {"optionsFov": OPTIONS_FOV, "fov": OPTIONS_FOV}})
    tick = [1000]
    session_fov = fov

    def emit(T, fw, up, label, fov=session_fov, n=1):
        for _ in range(n):
            tick[0] += 1
            cp = tuple(f32(x) for x in F(T))
            q = tuple(f32(x) for x in _look_q(md(T, fw), md(T, up)))
            grav = mul(unit(sub(T, C)), -9.81)
            se.append({"ev": "s1.frame", "tick": tick[0], "label": label, "camSe": list(T), "fwdSe": list(fw),
                       "upSe": list(up), "camPos": list(cp), "camRot": list(q), "vFovDeg": fov, "gravSe": list(grav),
                       "depthWm": norm(sub(T, C)) - Rs, "qpcCapture": tick[0] * 166667})
            actual = [cp[0] + (0.002 if mutant == "placement" else 0.0), cp[1], cp[2]]
            seen = f32(fov + (OPTIONS_FOV - fov) * 0.02) if mutant == "fov-drift" else fov
            sn.append({"event": "s1.cam", "data": {"tickNew": tick[0], "tickOld": tick[0], "tickRot": tick[0],
                                                   "alpha": 1.0, "outcome": "bracketed", "camPos": actual,
                                                   "camRot": list(q), "camPosCmd": list(cp), "camRotCmd": list(q),
                                                   "fov": fov, "fovSeen": seen, "fovApplied": fov, "c1Fov": fov,
                                                   "screen": [2560, 1440], "qpcUsed": tick[0] * 166667 + 50000,
                                                   "qpcCaptureRot": tick[0] * 166667}})
            if tick[0] % 300 == 0:
                sn.append({"event": "s1.camStats", "data": {"frames": 300, "bracketed": 300, "extrapolated": 0,
                                                            "held": 0, "gated": 0,
                                                            "fovDrift": 300 if mutant == "fov-drift" else 0}})
        return tick[0]

    def orient(T, heading, pitch):
        h, pi = math.radians(heading), math.radians(pitch)
        d = (math.sin(h) * math.cos(pi), math.sin(pi), math.cos(h) * math.cos(pi))
        u = (-math.sin(h) * math.sin(pi), math.cos(pi), -math.cos(h) * math.sin(pi))
        return unit(mdi(T, d)), unit(mdi(T, u))

    c = p["wm2"]
    T = Finv(c["sn"])
    fw, up = orient(T, 0.0, 0.0)
    t = emit(T, fw, up, "wm2", n=int(c["dwellS"] * TICK_HZ))
    sn.append({"ev": "s1.probe", "seTick": t - 5, "depthOf": max(0.0, -s.se_to_sn(T)[1]), "biome": "grandReef"})
    c = p["wm3"]
    A = Finv(c["fromSn"])
    uA = unit(sub(A, C))
    r = norm(sub(A, C))
    DA = unit(mdi(A, (1.0, 0.0, 0.0)))
    N = c["samples"] * c["ticksPerSample"]
    for k in range(N + 1):
        th = c["lengthM"] / Rs * k / N
        T = add(C, mul(add(mul(uA, math.cos(th)), mul(DA, math.sin(th))), r))
        fw = add(mul(uA, -math.sin(th)), mul(DA, math.cos(th)))
        emit(T, fw, unit(sub(T, C)), "wm3")
    c = p["wm4"]
    for name, pt in c["points"]:
        T = Finv(pt)
        E, U, Nn = frame_at(T)
        emit(T, Nn, U, wm4_label(name), n=3)
        for d, _axis in c["dirs"]:
            fw, up = {"E": (E, U), "N": (Nn, U), "down": (mul(U, -1.0), Nn)}[d]
            emit(T, fw, up, wm4_label(name, d), n=5)
    c = p["g1e5"]
    for d in c["depths"]:
        for e in c["elevs"]:
            f = session_fov
            lab = g1e5_label(d, e)
            T = Finv((c["xz"][0], -d, c["xz"][1]))
            fw, up = orient(T, 0.0, e)
            t = emit(T, fw, up, lab, fov=f, n=3)
            W, H = 2560.0, 1440.0
            X = add(T, mul(fw, c["aheadM"]))
            se.append({"ev": "s1.pixel", "label": lab, "tick": t, "sePx": [W / 2, H / 2], "screen": [W, H],
                       "vFovDeg": f, "aheadM": c["aheadM"], "camSe": list(T), "fwdSe": list(fw),
                       "upSe": list(up), "pointSn": list(F(X))})
            cp, q = F(T), _look_q(md(T, fw), md(T, up))
            dv = sub(F(X), cp)
            qi = (-q[0], -q[1], -q[2], q[3])
            loc = qrot(qi, dv)
            k = (H / 2) / math.tan(math.radians(f) / 2)
            sn.append({"event": "s1.pixel", "label": lab, "seTick": t,
                       "snPx": [W / 2 + k * loc[0] / loc[2], H / 2 - k * loc[1] / loc[2]], "screen": [W, H],
                       "fov": f, "pointSn": list(F(X))})
    return se, sn

def plugin_format(se, sn, freq=10_000_000, sn_pixel=True):
    """The synthetic logs re-written in the plugins' own S1 log formats (SE frame records ev c1trace/wm3 with seP and
 no label, DE.Pixel lines; SN DE.CamTrace, DE.Cam stats, DE.Cam rig, DE.SnPixel and DE.S1 sec lines) plus a launcher
 session log carrying the labels: exercises the adapters of normalise and the QPC label assignment. sn_pixel=False
 drops the DE.SnPixel lines (the "computed from the logged camera" fallback)."""
    out_se, out_sn, session = [], [], []
    last = None
    site = next(e for e in se if e.get("ev") == "s1.site")
    for e in se:
        if e.get("ev") == "s1.frame":
            if e["label"] != last:
                last = e["label"]
                session.append({"ev": "cmd", "side": "se", "args": {"label": last}, "qpc": e["qpcCapture"] - freq // 120})
            out_se.append({"t": "2026-10-12T00:00:00Z", "ev": "wm3" if e["label"] == "wm3" else "c1trace",
                           "check": "S1.WM-3" if e["label"] == "wm3" else "teleport", "k": -1, "tick": e["tick"],
                           "qpcCapture": e["qpcCapture"], "C": site["C"], "Rs": site["Rs"], "seP": e["camSe"],
                           # written by the SE plugin's frame records since the S1 integration (S1Commands.LogFrame)
                           "fwdSe": e["fwdSe"], "upSe": e["upSe"], "gravSe": e["gravSe"], "depthWm": e["depthWm"],
                           "camPos": e["camPos"], "camRot": e["camRot"], "vFovDeg": e["vFovDeg"], "cameraMode": 4,
                           "flags": 1})
        elif e.get("ev") == "s1.pixel":
            line = ("DE.Pixel S1.G1-E5 tick=%d sn=(%r, %r, %r) ahead=10m px=(%r, %r) inFront=True viewport=(0, 0, %d, "
                    "%d) vFovDeg=%r s=1.0" % (e["tick"], e["pointSn"][0], e["pointSn"][1], e["pointSn"][2], e["sePx"][0],
                                              e["sePx"][1], e["screen"][0], e["screen"][1], e["vFovDeg"]))
            out_se.append({"t": "2026-10-12T00:00:00Z", "ev": "log", "msg": line})
            session.append({"ev": "pixel", "label": e["label"], "se": {"ok": True, "line": line, "px": e["sePx"],
                                                                       "sn": e["pointSn"], "vFovDeg": e["vFovDeg"],
                                                                       "viewport": [0, 0] + list(e["screen"])},
                            "sn": {"ok": True, "pending": True, "label": e["label"]}})
    for e in sn:
        d = e.get("data") or e
        ev = e.get("event") or e.get("ev")
        if ev == "s1.cam":
            f = qrot(d["camRot"], (0.0, 0.0, 1.0))
            yaw, pitch = math.degrees(math.atan2(f[0], f[2])), math.degrees(math.atan2(f[1], math.hypot(f[0], f[2])))
            q, tq, tp = d["camRot"], d.get("camRotCmd", d["camRot"]), d.get("camPosCmd", d["camPos"])
            text = ("DE.CamTrace f=%d qpcUsed=%d qpcFreq=%d newestTick=%d newestQpc=%d presented=%d older=%d "
                    "outcome=Bracketed alpha=1.0000 yaw=%.3f pitch=%.3f x=%r y=%r z=%r qx=%r qy=%r qz=%r qw=%r "
                    "tx=%r ty=%r tz=%r tqx=%r tqy=%r tqz=%r tqw=%r fov=%r fovSeen=%r fovApplied=%r c1Fov=%r" % (
                        d["tickNew"], d["qpcUsed"], freq, d["tickRot"], d["qpcCaptureRot"], d["tickNew"], d["tickOld"],
                        yaw, pitch, d["camPos"][0], d["camPos"][1], d["camPos"][2], q[0], q[1], q[2], q[3],
                        tp[0], tp[1], tp[2], tq[0], tq[1], tq[2], tq[3], d["fov"], d.get("fovSeen", d["fov"]),
                        d.get("fovApplied", d["fov"]), d.get("c1Fov", d["fov"])))
            out_sn.append({"t": "2026-10-12T00:00:00.000Z", "event": "DE.CamTrace", "text": text})
        elif ev == "s1.camStats":
            out_sn.append({"event": "DE.Cam", "text": "DE.Cam stats frames=%d bracketed=%d extrapolated=%d held=%d none=0 "
                           "stale=0 tornTotal=0 fovDrift=%d extraPreCull=0 gated=%d resamplesLastFrame=1" % (
                               d["frames"], d["bracketed"], d["extrapolated"], d["held"], d["fovDrift"], d["gated"])})
        elif ev == "s1.camRig":
            out_sn.append({"event": "DE.Cam", "text": "DE.Cam rig 'camera' unparented from 'x', MainCameraControl disabled; "
                           "fov=%r optionsFov=%r near=0.05" % (d["fov"], d["optionsFov"])})
        elif ev == "s1.pixel" and sn_pixel:
            out_sn.append({"event": "DE.SnPixel", "text": "DE.SnPixel label=%s seTick=%d newestTick=%d frame=1 px=(%r, %r) "
                           "depth=10 screen=(%d, %d) fov=%r pointSn=(%r, %r, %r) (Camera.WorldToScreenPoint)" % (
                               d["label"], d["seTick"], d["seTick"], d["snPx"][0], d["snPx"][1], d["screen"][0],
                               d["screen"][1], d["fov"], d["pointSn"][0], d["pointSn"][1], d["pointSn"][2])})
        elif ev == "s1.probe":
            out_sn.append({"event": "DE.S1", "text": "DE.S1 sec cam=(1, 2, 3) depth=%.4f biome=%s presentedSeTick=%d" % (
                d["depthOf"], d["biome"], d["seTick"])})
    return out_se, out_sn, session

def selftest() -> int:
    ok = True
    q = plan(quick=True)
    se, sn = synthetic_logs(None, q)
    for snpx in (True, False):
        pse, psn, sess = plugin_format(se, sn, sn_pixel=snpx)
        r = evaluate(pse, psn, sess, q, strict=False)
        bad = [x for x in r["rows"] if x["status"] == FAIL]
        need = [x for x in r["rows"] if x["status"] == PASS and ("Subnautica" in x["what"] or x["id"] == "S1.G1-E5")]
        # the SE frame records carry fwdSe/upSe/gravSe/depthWm, the SN trace the transform, the commanded pose and the
 # FOVs: no required row may be MISSING
        missing = [x for x in r["rows"] if x["status"] == MISSING and x.get("required", True)]
        g = [x for x in r["rows"] if x["id"] == "S1.G1-E5" and (x.get("detail") or {}).get("case")]
        computed = sum(1 for x in g if x["detail"].get("snComputed"))
        pf = not bad and not missing and len(need) >= 30 and len(g) == 9 and computed == (0 if snpx else 9)
        print("check_wm selftest plugin-format logs (c1trace/wm3, DE.CamTrace with transform/commanded pose/FOVs, "
              "DE.Cam stats/rig, DE.Pixel, %s, session labels): %s (%d rows, %d FAIL, %d required MISSING, %d "
              "Subnautica/G1-E5 rows PASS, %d G1-E5 cases, %d computed)" % (
                  "DE.SnPixel" if snpx else "no DE.SnPixel (computed pixels)", "PASS" if pf else "FAIL", len(r["rows"]),
                  len(bad), len(missing), len(need), len(g), computed))
        for x in (bad + missing)[:6]:
            print("   ", x["id"], x["what"], x["measured"])
        ok &= pf
    se, sn = synthetic_logs(None, q)
    r = evaluate(se, sn, (), q, strict=False)
    bad = [x for x in r["rows"] if x["status"] == FAIL]
    print("check_wm selftest green run: %s (%d rows, %d FAIL)" % ("PASS" if r["ok"] else "FAIL", len(r["rows"]),
                                                                   len(bad)))
    for x in bad[:8]:
        print("   ", x["id"], x["what"], x["measured"])
    ok &= r["ok"]
    for mutant, must in (("mapdir-origin", ("S1.WM-4", "S1.WM-2", "S1.G1-E5")), ("naive-map", ("S1.WM-2", "S1.WM-3")),
                         ("fov-drift", ("S1.N7",)), ("placement", ("S1.WM-2", "S1.WM-3", "S1.WM-4"))):
        se, sn = synthetic_logs(mutant, q)
        r = evaluate(se, sn, (), q, strict=False)
        red = sorted({x["id"] for x in r["rows"] if x["status"] == FAIL})
        hit = all(m in red for m in must)
        if mutant == "placement":   # only the placement rows go red: 2 mm is inside the 1 cm position rows
            hit &= all("placement" in x["what"] for x in r["rows"] if x["status"] == FAIL)
        print("check_wm selftest mutant %-14s: %s (red: %s)" % (mutant, "RED (expected)" if hit else
                                                               "GREEN (unexpected)", ", ".join(red)))
        ok &= hit
    # precondition of S1.N7: the session FOV equal to the options FOV -> red (neither N7 nor M6 would discriminate)
    se, sn = synthetic_logs(None, q, fov=OPTIONS_FOV)
    r = evaluate(se, sn, (), q, only=["n7"], strict=False)
    pre = [x for x in r["rows"] if "precondition" in x["what"]]
    hit = bool(pre) and pre[0]["status"] == FAIL
    print("check_wm selftest S1.N7 precondition (SE FOV = options FOV): %s" % ("RED (expected)" if hit else
                                                                             "GREEN (unexpected)"))
    ok &= hit
    # S1.G1-E5 exit over sessions: three FOVs green; two FOVs (or a red case) not enough
    res = []
    for f in (50.0, 70.0, 90.0):
        se, sn = synthetic_logs(None, q, fov=f)
        res.append(evaluate(se, sn, (), q, only=["g1e5"], strict=False))
    cov3 = g1e5_coverage(res, q)
    cov2 = g1e5_coverage(res[:2] + [res[1]], q)
    hit = cov3["ok"] and not cov2["ok"] and cov3["distinctFovs"] == [50.0, 70.0, 90.0]
    print("check_wm selftest --g1e5-coverage: 3 FOVs %s, 2 FOVs %s -> %s" % (
        "PASS" if cov3["ok"] else "FAIL", "PASS" if cov2["ok"] else "FAIL", "PASS" if hit else "FAIL"))
    ok &= hit
    print("CHECK_WM SELFTEST", "PASS" if ok else "FAIL")
    return 0 if ok else 1

# ----------------------------------------------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------------------------------------------

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S1 world-map oracle (WM-2/3/4, G1-E5, N7)")
    ap.add_argument("--se", action="append", default=[], help="SE JSONL log (repeatable)")
    ap.add_argument("--sn", action="append", default=[], help="SN JSONL log (repeatable)")
    ap.add_argument("--session", default=None, help="launcher s1-session.jsonl (labels and pixel fallbacks)")
    ap.add_argument("--only", default="", help="comma list of wm2,wm3,wm4,g1e5,n7")
    ap.add_argument("--quick", action="store_true", help="use the quick plan (stand-in dry runs)")
    ap.add_argument("--lenient", action="store_true", help="MISSING data does not fail the run")
    ap.add_argument("--json", default=None, help="write the result JSON here")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--print-plan", action="store_true")
    ap.add_argument("--g1e5-coverage", nargs="+", default=None, metavar="S1_RESULTS_JSON",
                    help="S1.G1-E5 exit over sessions (one FOV each): every case PASS and >= 3 distinct FOVs")
    a = ap.parse_args(sys.argv[1:] if argv is None else argv)
    if a.selftest:
        return selftest()
    if a.g1e5_coverage:
        cov = g1e5_coverage(a.g1e5_coverage, plan(a.quick))
        for r in cov["sessions"]:
            print("  %-60s cases %d, PASS %d, SE FOV %s -> %s" % (r["session"][-60:], r["cases"], r["passed"], r["fovs"],
                                                                 "ok" if r["ok"] else "NOT OK"))
        print("S1.G1-E5 COVERAGE %s: distinct FOVs %s (need %d)" % ("PASS" if cov["ok"] else "FAIL", cov["distinctFovs"],
                                                                   cov["need"]))
        return 0 if cov["ok"] else 1
    p = plan(a.quick)
    if a.print_plan:
        print(json.dumps({"plan": p, "steps": steps(p)}, indent=1))
        return 0
    if not a.se or not a.sn:
        ap.error("--se and --sn are required (or --selftest / --print-plan)")
    only = [x.strip() for x in a.only.split(",") if x.strip()] or None
    r = evaluate(read_jsonl(a.se), read_jsonl(a.sn), read_jsonl(a.session) if a.session else (), p, only,
                 strict=not a.lenient)
    print(table(r["rows"]))
    print("summary: %s" % json.dumps(r["summary"]))
    print("CHECK_WM %s" % ("PASS" if r["ok"] else "FAIL"))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1)
    return 0 if r["ok"] else 1

if __name__ == "__main__":
    sys.exit(main())
