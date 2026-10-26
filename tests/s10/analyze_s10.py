"""S10 part 1 analyzer: the measurement rig. Every verdict is INFO in part 1 (the baseline is honest: the pass marks are printed beside the numbers
and become PASS/FAIL when the rewrite parts land).

 python -I tests/s10/analyze_s10.py DIR [--machine desktop|laptop] [--site site/data/requirements.json] [--json]
 python -I tests/s10/analyze_s10.
 python -I tests/s10/analyze_s10.py --load-history [LOG_DIR] [--json] (every world load in the logs folder)
 python -I tests/s10/analyze_s10.py --selftest

Sources (read-only; frame times are the engines' own, our timers only for our own stages):
 SE se-*.jsonl: sample (MyFpsManager frame time avg/min/max, fps, the S8 memory), s10.se (MyFpsManager.FrameTime per
 update: p50/p95/max, the update interval), s10.paste (selection lag, placement wait), s5.budget (grids, parts,
 sn.pendingParts: Subnautica's spawn of the pasted ship), s8a.window (the window's batch backlog), s4.stats and s8a.perf
 (our voxel providers' read cost), the DE.* log lines of the world load; SpaceEngineers_*.log "Loading duration".
 SN sn-*.jsonl: s10.perf (Unity's unscaledDeltaTime p50/p95/max, fps, the stage clock: kit, place, mirror, colliders,
 lights, window; private/working set), s5.perf (fallback frame times), s8a.status (pending, inFlight, jobGiveUps,
 reloads), s7a.picture (the first presented SE tick after a load).
 session s10-session.jsonl: part begin/end events (the scenes' windows).

Rows (per scene: spawn, paste, flight; "free" for a session without scenes):
 S10.PERF.<S>.SN Subnautica fps p50 over the scene's seconds, frame p50/p95/max (mark: 60 desktop / 30 laptop, paste)
 S10.PERF.<S>.SE SE fps p50, engine frame p50/p95/max, the worst update interval
 S10.PERF.<S>.MIRROR the stage clock: ms per second (mean, worst second) and the worst frame per stage
 S10.PERF.<S>.WINDOW SE's batch backlog (max pending, ring), Subnautica's reload queue (max pending/inFlight), give-ups
 S10.PERF.<S>.MEM SE + Subnautica private MB (max) (mark: fits a 16 GB machine)
 S10.PERF.PASTE.SELECT selection lag (first Paste -> preview active), PLACE placement wait (second Paste -> grid
 added), SPAWN Subnautica's spawn (placed -> pendingParts 0 with the parts mirrored)
 S10.PERF. launcher_s10 --notes)
 S10.LOAD.<PHASE> per world load: PRE (RunLoad prefix -> MySession.OnLoading), ENTITIES (LoadData -> BeforeStart),
 BEFORESTART (BeforeStart -> RunLoad returned), CLIPMAPS

 HELLO (first update -> Subnautica's hello ack), PICTURE (ack -> the first
 presented frame), TOTAL; SE's own "Loading duration"
 S10.LOAD.<SUB> the subsystems seen in the load (site bind, terrain tiles, prefetch, provider reads, chunk library,
 window start, ore palette, outcrops, planet list, room snapshots, kit stream, statics census)
"""
from __future__ import annotations

import datetime
import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
LOG_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "DeepEngineers", "logs")
SCENES = ("spawn", "paste", "flight")
MACHINES = ("desktop", "laptop")

# the roadmap's pass marks (ROADMAP §14 "### S10"): INFO in part 1
PASS_MARKS = {
    "pasteSnFps": {"desktop": 60.0, "laptop": 30.0, "text": "10,000-block blueprint: Subnautica >= 60 fps desktop, >= 30 on the Radeon 780M laptop"},
    "grid30k": {"text": "30,000-block grid playable through the merged hull and distance LOD (part 3; not a part-1 scene)"},
    "memoryMB": {"value": 13000.0, "text": "a 16 GB machine: SE + Subnautica private <= 13,000 MB (3 GB left to Windows)"},
    "giveUps": {"value": 0, "text": "window streaming: no batch reload given up under a burst (ROADMAP: 151 of ~170 gave up)"},
    "sharedGpuCap": {"value": 30.0, "text": "shared GPUs: Subnautica capped at 30 fps (preset, part 6)"},
}

# the paste scene's ship: tests the twin of S10Blueprint (src/DeepEngineers.Wire/S10PerfRules.cs)
BP_WIDTH, BP_HEIGHT, BP_DECKS = 20, 12, (4, 8)
BP_KINDS = ("LargeBlockArmorBlock", "LargeHeavyBlockArmorBlock", "SmallLight", "LargeBlockConveyor")

# ----------------------------------------------------------------------------------------------------------------------
# twins of the C# rules
# ----------------------------------------------------------------------------------------------------------------------

def percentile(sorted_vals, q):
    """Nearest rank: index ceil(q n) - 1, clamped (S10PerfRules.Percentile)."""
    n = len(sorted_vals)
    if n == 0:
        return None
    i = int(math.ceil(q * n)) - 1
    return sorted_vals[max(0, min(n - 1, i))]

# the twin of Wire S10Presets.CanonicalText (CONTRACT §5.4 "S10 presets", pinned 0x42e90bc51d397ecf)
PRESETS_TEXT = ("ext s10 presets 1\n"
                "preset low fpsCap=30 lightsPerGrid=6 previewCap=500 colliderNearM=100 lod=on lodM=100 life=0.5\n"
                "preset mid fpsCap=0 lightsPerGrid=12 previewCap=1000 colliderNearM=150 lod=off lodM=150 life=1.0\n"
                "preset high fpsCap=0 lightsPerGrid=24 previewCap=3000 colliderNearM=250 lod=off lodM=150 life=1.5\n"
                "auto sharedGpu or ramGB<20 -> low; ramGB>=30 and dedicatedGpu -> high; else mid\n")
PRESETS_HASH = 0x42e90bc51d397ecf

def fnv1a64(text):
    h = 0xcbf29ce484222325
    for b in text.encode("ascii"):
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def bp_kind_at(x, y, z):
    if x == 0 or x == BP_WIDTH - 1 or y == 0 or y == BP_HEIGHT - 1:
        return 1 if y == 0 else 0
    for d in BP_DECKS:
        if y == d:
            return 0
        if y == d + 1 and x % 6 == 3 and z % 3 == 0:
            return 2
    if y == 2 and x == BP_WIDTH // 2:
        return 3
    return -1

def blueprint_counts(blocks):
    """(per-kind counts, slices) of S10Blueprint.Layout(blocks)."""
    n = max(1, min(60000, int(blocks)))
    counts = [0, 0, 0, 0]
    got, z = 0, 0
    while got < n:
        for y in range(BP_HEIGHT):
            for x in range(BP_WIDTH):
                if got >= n:
                    break
                k = bp_kind_at(x, y, z)
                if k >= 0:
                    counts[k] += 1
                    got += 1
        z += 1
    return counts, z

# ----------------------------------------------------------------------------------------------------------------------
# reading
# ----------------------------------------------------------------------------------------------------------------------

def ts(s):
    """ISO UTC ('...Z', up to 7 decimals) -> epoch seconds; None when unreadable."""
    if not s:
        return None
    try:
        s = s.rstrip("Z")
        frac = 0.0
        if "." in s:
            s, f = s.split(".", 1)
            frac = float("0." + re.sub(r"\D", "", f)[:7] or "0")
        d = datetime.datetime.strptime(s[:19], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=datetime.timezone.utc)
        return d.timestamp() + frac
    except (ValueError, TypeError):
        return None

SE_ROWS = ("sample", "s10.se", "s10.paste", "s5.budget", "s8a.window", "s4.stats", "s8a.perf", "s8a.outcrops", "s10.mem", "s10.curtain")
SN_ROWS = ("s10.perf", "s5.perf", "s8a.status", "s7a.picture", "s8a.palette.ore", "s8a.palette", "s10.mem")

# load-time patterns: (id, side, regex, ms group or None); SE lines are "msg", Subnautica's "text"
LOAD_MARKS = (
    ("runload", "se", r"^DE\.Phase LoadingWorld \(MyGuiScreenLoading\.RunLoad prefix\)", None),
    ("onloading", "se", r"^DE\.LoadOrder MySession\.OnLoading", None),
    ("loaddata", "se", r"^DE\.Site LoadData", None),
    ("waterapi", "se", r"^DE\.WaterApi RECEIVED", None),
    ("beforestart", "se", r"^DE\.Site BeforeStart", None),
    ("runloadret", "se", r"^DE\.Phase RunLoad returned", None),
    ("firstupdate", "se", r"^DE\.Site first UpdateBeforeSimulation", None),
    ("worldload", "se", r"^DE\.WorldLoad S1\.N12", None),
    ("helloack", "se", r"^DE\.Hello ACK", None),
    ("underwater", "se", r"^DE\.View .*-> Underwater", None),
)
LOAD_SUBS = (
    ("SITE", "se", r"^DE\.Site OK site v\d+ bound", None, "the site bind (Water Mod API, first update)"),
    ("TILES", "se", r"^DE\.Terrain OK tiles=\d+.*createMs=(?P<ms>[\d.]+)", "ms", "the S4 terrain tiles (BeforeStart)"),
    ("PREFETCH", "se", r"^DE\.Terrain PREFETCH done .* (?P<ms>\d+) ms", "ms", "the S4 octree prefetch (background thread)"),
    ("CHUNKLIB", "se", r"^DE\.S8a sample ready: .*init (?P<ms>\d+) ms", "ms", "the S8a chunk library (sample chunk)"),
    ("WINDOW", "se", r"^DE\.S8a window started", None, "the S8a window start (store folder, chart)"),
    ("OUTCROPS", "se", r"^DE\.S8a outcrops?\b.*?(?P<ms>[\d.]+) ms", "ms", "the S8a outcrop scan"),
    ("PLANETS", "se", r"^DE\.S15 sky list", None, "the S15 planet list"),
    ("ROOMS", "se", r"^DE\.S12 .*snapshot.*?(?P<ms>[\d.]+) ms", "ms", "the S12 room snapshots"),
    ("STATICS", "se", r"^DE\.S7 statics:", None, "the S7 statics census"),
    ("KIT", "se", r"^DE\.S3 stream complete .* in (?P<ms>\d+) ms", "ms", "the S3/S5 kit stream (models, materials, grids)"),
    ("ORES", "sn", r"^DE\.S8a .*ore palette.*?(?P<ms>[\d.]+) ms", "ms", "the S8a ore palette (Subnautica WorldStreamer.Start)"),
)

def iter_jsonl(path, wanted=None, text_filter=None):
    """Rows of one JSONL: dicts with '_t' (epoch s). wanted: row names kept; text_filter(text) for log lines."""
    try:
        f = open(path, encoding="utf-8", errors="replace")
    except OSError:
        return
    with f:
        for line in f:
            ev = None
            m = re.search(r'"ev":"([^"]+)"', line)
            if m:
                ev = m.group(1)
            is_text = ev in (None, "log") or ('"text":"' in line and '"data":' not in line)
            if is_text:
                if text_filter is None or not ('"msg":"DE.' in line or '"text":"DE.' in line):
                    continue
            elif wanted is not None and ev not in wanted:
                continue
            try:
                o = json.loads(line)
            except ValueError:
                continue
            o["_t"] = ts(o.get("t") or o.get("utc"))
            if is_text:
                txt = o.get("msg") or o.get("text") or ""
                if text_filter is not None and not text_filter(txt):
                    continue
                o["_text"] = txt
            yield o

_LOAD_RE = [re.compile(p) for (_i, _s, p, _g) in LOAD_MARKS] + [re.compile(p) for (_i, _s, p, _g, _d) in LOAD_SUBS]

def load_filter(txt):
    return txt.startswith("DE.S8a world up") or any(r.search(txt) for r in _LOAD_RE)   # round 3 F: the world-up minutes

def read_side(paths, wanted):
    rows = {k: [] for k in wanted}
    text = []
    for p in paths:
        for o in iter_jsonl(p, set(wanted), load_filter):
            if "_text" in o:
                text.append(o)
                continue
            ev = o.get("ev")
            if ev in rows:
                if "data" in o and isinstance(o["data"], dict):
                    d = dict(o["data"])
                    d["_t"] = o["_t"]
                    o = d
                rows[ev].append(o)
    return rows, text

def scene_windows(session_path):
    """[(name, t0, t1)] from s10-session.jsonl part events."""
    out, open_ = [], {}
    try:
        f = open(session_path, encoding="utf-8")
    except OSError:
        return out
    with f:
        for line in f:
            try:
                o = json.loads(line)
            except ValueError:
                continue
            if o.get("ev") != "part":
                continue
            t = ts(o.get("utc"))
            if o.get("phase") == "begin":
                open_[o.get("name")] = t
            elif o.get("phase") == "end" and o.get("name") in open_:
                out.append((o["name"], open_.pop(o["name"]), t))
    return out

# ----------------------------------------------------------------------------------------------------------------------
# rows
# ----------------------------------------------------------------------------------------------------------------------

def row(rid, value, detail, verdict="INFO", data=None):
    return {"id": rid, "verdict": verdict, "value": value, "detail": detail, "data": data or {}}

def _in(rows, t0, t1):
    return [r for r in rows if r.get("_t") is not None and t0 <= r["_t"] <= t1]

def _vals(rows, key):
    out = []
    for r in rows:
        v = r
        for k in key.split("."):
            v = v.get(k) if isinstance(v, dict) else None
        if isinstance(v, (int, float)) and not isinstance(v, bool) and not (isinstance(v, float) and math.isnan(v)):
            out.append(float(v))
    return out

def _stat(vals, q=0.5):
    s = sorted(vals)
    return percentile(s, q) if s else None

def _f(v, fmt="%.1f"):
    return "n/a" if v is None else fmt % v

def scene_rows(name, t0, t1, se, sn, machine):
    S = name.upper()
    out = []
    snp = _in(sn["s10.perf"], t0, t1)
    src = "s10.perf"
    if snp:
        fps = _vals(snp, "fps")
        p50, p95, mx = _vals(snp, "frameP50Ms"), _vals(snp, "frameP95Ms"), _vals(snp, "frameMaxMs")
    else:
        snp = _in(sn["s5.perf"], t0, t1)
        src = "s5.perf (no s10.perf: an older build)"
        fps = _vals(snp, "frames")
        p50, p95, mx = _vals(snp, "frameP50Ms"), _vals(snp, "frameP99Ms"), _vals(snp, "frameMaxMs")
    mark = PASS_MARKS["pasteSnFps"].get(machine) if name == "paste" else None
    fps50 = _stat(fps)
    d = {"seconds": len(snp), "fpsP50": fps50, "fpsP05": _stat(fps, 0.05), "frameP50Ms": _stat(p50), "frameP95Ms": _stat(p95, 0.95),
         "frameMaxMs": max(mx) if mx else None, "source": src}
    out.append(row("S10.PERF.%s.SN" % S, fps50, "Subnautica fps p50 %s (worst 5 %% of seconds %s), frame p50 %s / p95 %s / max %s ms over %d s [%s]%s" % (
        _f(fps50), _f(d["fpsP05"]), _f(d["frameP50Ms"]), _f(d["frameP95Ms"]), _f(d["frameMaxMs"]), len(snp), src,
        (" | mark %.0f fps on the %s: %s" % (mark, machine, "met" if fps50 is not None and fps50 >= mark else "NOT met")) if mark else ""), data=d))
    ses = _in(se["s10.se"], t0, t1)
    sam = _in(se["sample"], t0, t1)
    sfps = _vals(sam, "fps")
    d = {"seconds": len(sam), "fpsP50": _stat(sfps), "fpsP05": _stat(sfps, 0.05), "engineFrameP50Ms": _stat(_vals(ses, "engineFrameP50Ms")),
         "engineFrameP95Ms": _stat(_vals(ses, "engineFrameP95Ms"), 0.95),
         "engineFrameMaxMs": max(_vals(ses, "engineFrameMaxMs") + _vals(sam, "frameTimeMaxMs") or [None], key=lambda v: -1 if v is None else v),
         "updateMaxMs": max(_vals(ses, "updateMaxMs") or [None], key=lambda v: -1 if v is None else v)}
    out.append(row("S10.PERF.%s.SE" % S, d["fpsP50"], "SE fps p50 %s (worst 5 %% %s), engine frame p50 %s / p95 %s / max %s ms, worst update interval %s ms" % (
        _f(d["fpsP50"]), _f(d["fpsP05"]), _f(d["engineFrameP50Ms"]), _f(d["engineFrameP95Ms"]), _f(d["engineFrameMaxMs"]), _f(d["updateMaxMs"])), data=d))
    stages = {}
    parts = []
    for st in SN_STAGES:
        ms = _vals(snp, "stages.%s.ms" % st) if src == "s10.perf" else []
        mf = _vals(snp, "stages.%s.maxFrameMs" % st) if src == "s10.perf" else []
        stages[st] = {"msPerSecMean": (sum(ms) / len(ms)) if ms else None, "msPerSecMax": max(ms) if ms else None, "worstFrameMs": max(mf) if mf else None}
        parts.append("%s %s/%s ms/s (frame %s)" % (st, _f(stages[st]["msPerSecMean"]), _f(stages[st]["msPerSecMax"]), _f(stages[st]["worstFrameMs"])))
    out.append(row("S10.PERF.%s.MIRROR" % S, stages["mirror"]["msPerSecMean"], "stage clock mean/worst-second (worst frame): " + "; ".join(parts), data=stages))
    # round 2 A: who owns the spikes (frames >= 100 ms): our bridge tick ("plugin" >= half the worst frame), the collector (a
 # GC counted in that second), or Subnautica itself (the rest)
    spikes = [r for r in snp if (r.get("frameMaxMs") or 0) >= 100] if src == "s10.perf" else []
    ours = sum(1 for r in spikes if ((r.get("stages") or {}).get("plugin") or {}).get("maxFrameMs", 0) >= 0.5 * r["frameMaxMs"])
    gcs, prev = 0, None
    for r in snp:
        if prev is not None and (r.get("frameMaxMs") or 0) >= 100 and (r.get("gc0") or 0) > (prev.get("gc0") or 0):
            gcs += 1
        prev = r
    d = {"spikeSeconds": len(spikes), "ours": ours, "withGc": gcs}
    out.append(row("S10.PERF.%s.SPIKES" % S, len(spikes), "%d seconds with a frame >= 100 ms: our bridge tick >= half of the worst frame in %d, a GC in %d, the rest Subnautica's own" % (
        len(spikes), ours, gcs), data=d))
    se_st, se_parts = {}, []
    for st in ("export", "skin", "rooms", "producer", "hooks", "s8a"):
        ms = _vals(ses, "stages.%s.ms" % st)
        mu = _vals(ses, "stages.%s.maxUpdateMs" % st)
        se_st[st] = {"msPerSecMean": (sum(ms) / len(ms)) if ms else None, "worstUpdateMs": max(mu) if mu else None}
        se_parts.append("%s %s ms/s (update %s)" % (st, _f(se_st[st]["msPerSecMean"]), _f(se_st[st]["worstUpdateMs"])))
    out.append(row("S10.PERF.%s.SESTAGES" % S, se_st["producer"]["worstUpdateMs"], "SE update thread, mean (worst update): " + "; ".join(se_parts), data=se_st))
    win = _in(se["s8a.window"], t0, t1)
    sts = _in(sn["s8a.status"], t0, t1)
    gu = _vals(sts, "jobGiveUps")
    d = {"sePendingMax": max(_vals(win, "pending") or [None], key=lambda v: -1 if v is None else v),
         "seRingMax": max(_vals(win, "ring") or [None], key=lambda v: -1 if v is None else v),
         "seBatchMsP95": _stat(_vals(win, "msP95"), 0.95),
         "snPendingMax": max(_vals(sts, "pending") or [None], key=lambda v: -1 if v is None else v),
         "snInFlightMax": max(_vals(sts, "inFlight") or [None], key=lambda v: -1 if v is None else v),
         "giveUps": (max(gu) - min(gu)) if gu else None,
         "reloads": (max(_vals(sts, "reloads")) - min(_vals(sts, "reloads"))) if _vals(sts, "reloads") else None}
    out.append(row("S10.PERF.%s.WINDOW" % S, d["giveUps"], "SE backlog max %s (ring %s, batch p95 %s ms); Subnautica reload queue max %s, in flight %s, %s reloads, %s give-ups (mark %d)" % (
        _f(d["sePendingMax"], "%.0f"), _f(d["seRingMax"], "%.0f"), _f(d["seBatchMsP95"]), _f(d["snPendingMax"], "%.0f"), _f(d["snInFlightMax"], "%.0f"), _f(d["reloads"], "%.0f"), _f(d["giveUps"], "%.0f"), PASS_MARKS["giveUps"]["value"]), data=d))
    se_mb = _vals(sam, "privateMB")
    sn_mb = _vals(snp, "privateMB")
    tot = (max(se_mb) if se_mb else 0) + (max(sn_mb) if sn_mb else 0)
    d = {"sePrivateMB": max(se_mb) if se_mb else None, "snPrivateMB": max(sn_mb) if sn_mb else None, "totalMB": tot if (se_mb or sn_mb) else None}
    out.append(row("S10.PERF.%s.MEM" % S, d["totalMB"], "private MB max: SE %s + Subnautica %s = %s (mark <= %.0f for a 16 GB machine)" % (
        _f(d["sePrivateMB"], "%.0f"), _f(d["snPrivateMB"], "%.0f"), _f(d["totalMB"], "%.0f"), PASS_MARKS["memoryMB"]["value"]), data=d))
    return out

# round 2 addendum (16 GB acceptance): the memory budget (MB, private bytes)
MEM_BUDGET = {"se": 7000.0, "sn": 5000.0, "spawn": 12000.0, "paste": 13500.0, "growthMBPerMin": 10.0}

def _slope_mb_per_min(rows):
    pts = [(r["_t"], r.get("privateMB")) for r in rows if r.get("_t") is not None and isinstance(r.get("privateMB"), (int, float))]
    if len(pts) < 3 or pts[-1][0] - pts[0][0] < 120:
        return None
    n = len(pts)
    mt = sum(t for t, _ in pts) / n
    mv = sum(v for _, v in pts) / n
    den = sum((t - mt) ** 2 for t, _ in pts)
    return (sum((t - mt) * (v - mv) for t, v in pts) / den) * 60.0 if den > 0 else None

def mem_rows(name, t0, t1, se, sn):
    """S10.MEM.<S>.*: the 16 GB acceptance from the s10.mem rows (fallback: sample / s10.perf private bytes)."""
    S = name.upper()
    sem = _in(se["s10.mem"], t0, t1) or _in(se["sample"], t0, t1)
    snm = _in(sn["s10.mem"], t0, t1) or _in(sn["s10.perf"], t0, t1)
    se_mb, sn_mb = _vals(sem, "privateMB"), _vals(snm, "privateMB")
    out = []
    for side, vals, cap in (("SE", se_mb, MEM_BUDGET["se"]), ("SN", sn_mb, MEM_BUDGET["sn"])):
        mx = max(vals) if vals else None
        out.append(row("S10.MEM.%s.%s" % (S, side), mx, "%s private max %s MB (budget %.0f MB)" % (side, _f(mx, "%.0f"), cap),
                       "MISSING" if mx is None else ("PASS" if mx <= cap else "FAIL")))
    both_cap = MEM_BUDGET["paste"] if name == "paste" else MEM_BUDGET["spawn"]
    both = (max(se_mb) + max(sn_mb)) if se_mb and sn_mb else None
    out.append(row("S10.MEM.%s.BOTH" % S, both, "SE + Subnautica private %s MB (budget %.0f MB on a 16 GB machine)" % (_f(both, "%.0f"), both_cap),
                   "MISSING" if both is None else ("PASS" if both <= both_cap else "FAIL")))
    faults = _vals(_in(se["s10.mem"], t0, t1), "pageFaultsPerSec") + _vals(_in(sn["s10.mem"], t0, t1), "pageFaultsPerSec")
    out.append(row("S10.MEM.%s.FAULTS" % S, max(faults) if faults else None, "page faults per second, worst 10 s window of either game: %s (soft and hard together)" % (
        _f(max(faults) if faults else None, "%.0f"))))
    last_sn = _in(sn["s10.mem"], t0, t1)[-1:] or [{}]
    last_se = _in(se["s10.mem"], t0, t1)[-1:] or [{}]
    b, e = last_sn[0], last_se[0]
    out.append(row("S10.MEM.%s.BREAKDOWN" % S, None, "Subnautica: unity allocated %s / reserved %s MB, mono %s MB, %s batch roots, mirror meshes %s MB (CPU copies %s MB, %s/%s chunks readable), kit %s MB (%s models), %s texture sets | SE: model payloads %s MB (%s models), C3 queue %s MB, %s grids, gc heap %s MB, tiles step %s MB" % (
        _f(b.get("unityAllocatedMB"), "%.0f"), _f(b.get("unityReservedMB"), "%.0f"), _f(b.get("monoUsedMB"), "%.0f"), b.get("snBatchRoots"),
        _f(b.get("mirrorMeshMB"), "%.0f"), _f(b.get("mirrorMeshCpuMB"), "%.0f"), b.get("chunksReadable"), b.get("chunks"), _f(b.get("kitMB"), "%.0f"),
        b.get("kitModels"), b.get("textureSets"), _f(e.get("modelPayloadMB"), "%.0f"), e.get("mirrorModels"), _f(e.get("c3QueueMB"), "%.1f"),
        e.get("mirroredGrids"), _f(e.get("gcHeapMB"), "%.0f"), _f(e.get("tilesStepMB"), "%.0f")), data={"sn": b, "se": e}))
    if name in ("flight", "free"):
        # round 3: the leak check starts once the world load has settled (SE's first in-world s10.mem row + SETTLE_S): the load
 # ramp (tiles, clipmaps, the Subnautica transition) is reported apart, it is not a growth (00:02 session: SE 5.4 -> 15.2 GB
 # during the load, then flat at 13.45 GB)
        inw = [r["_t"] for r in sem if r.get("inWorld") is True and r.get("_t") is not None]
        settle = (min(inw) + SETTLE_S) if inw else None
        sem_s = [r for r in sem if settle is None or (r.get("_t") or 0) >= settle]
        snm_s = [r for r in snm if settle is None or (r.get("_t") or 0) >= settle]
        gs, gn = _slope_mb_per_min(sem_s), _slope_mb_per_min(snm_s)
        worst = max([g for g in (gs, gn) if g is not None], default=None)

        def ramp(rows):
            v = [(r["_t"], r.get("privateMB")) for r in rows if r.get("_t") is not None and isinstance(r.get("privateMB"), (int, float))]
            if not v:
                return "n/a"
            pre = [x for t, x in v if settle is None or t < settle] or [v[0][1]]
            post = [x for t, x in v if settle is not None and t >= settle]
            return "%.0f -> peak %.0f -> settled %s MB" % (v[0][1], max(x for _, x in v), ("%.0f" % post[-1]) if post else "n/a")
        out.append(row("S10.MEM.%s.GROWTH" % S, worst, "private-bytes growth after the load settled (%s s after SE entered the world): SE %s, Subnautica %s MB/min (budget %.0f: grows neither); the load ramp apart: SE %s, Subnautica %s" % (
            SETTLE_S, _f(gs), _f(gn), MEM_BUDGET["growthMBPerMin"], ramp(sem), ramp(snm)),
            "MISSING" if worst is None else ("PASS" if worst <= MEM_BUDGET["growthMBPerMin"] else "FAIL"),
            data={"seMBPerMin": gs, "snMBPerMin": gn, "settleT": settle}))
    return out

SETTLE_S = 60.0

SN_STAGES = ("kit", "place", "mirror", "colliders", "lights", "window", "plugin", "fx", "view", "hosts")
SE_STAGES = ("export", "skin", "rooms", "producer", "hooks", "s8a")

def _alloc_split(rows, stages, total_key):
    """Round 3 E: allocation per stage (MB/s from stages.*.allocKB over each row's span), the total and the collections,
 for the seconds with the cuts on and off (de.s10 {allocCuts} or the env switch: before/after in one session)."""
    out = {}
    for label, want in (("after", True), ("before", False)):
        rs = [r for r in rows if bool(r.get("allocCuts")) == want and "allocCuts" in r]
        if not rs:
            continue
        d = {"seconds": len(rs), "stages": {}}
        for st in stages:
            mbps = []
            for r in rs:
                kb = ((r.get("stages") or {}).get(st) or {}).get("allocKB")
                span = r.get("spanS") or 1.0
                if isinstance(kb, (int, float)) and not isinstance(kb, bool):
                    mbps.append(kb / 1024.0 / max(span, 1e-3))
            d["stages"][st] = {"mbpsMean": (sum(mbps) / len(mbps)) if mbps else None, "mbpsMax": max(mbps) if mbps else None}
        tot = _vals(rs, total_key)
        d["totalMBpsMean"] = (sum(tot) / len(tot)) if tot else None
        d["totalMBpsP50"] = _stat(tot)
        d["totalMBpsMax"] = max(tot) if tot else None
        g = _vals(rs, "gc0")
        d["gcEveryS"] = (len(rs) / (max(g) - min(g))) if len(g) > 1 and max(g) > min(g) else None
        out[label] = d
    return out

def alloc_rows(name, t0, t1, se, sn):
    """S10.ALLOC.<S>.SN /.SE (round 3 E, required): the allocation rate per stage, before (cuts off) and after (cuts on)."""
    S = name.upper()
    out = []
    for side, rows, stages, total in (("SN", _in(sn["s10.perf"], t0, t1), SN_STAGES, "monoAllocMBps"),
                                      ("SE", _in(se["s10.se"], t0, t1), SE_STAGES, "updateAllocMBps")):
        rid = "S10.ALLOC.%s.%s" % (S, side)
        if not rows:
            out.append(row(rid, None, "no %s rows in the scene" % ("s10.perf" if side == "SN" else "s10.se"), "MISSING"))
            continue
        split = _alloc_split(rows, stages, total)
        if not split:
            tot = _vals(rows, total)
            out.append(row(rid, (sum(tot) / len(tot)) if tot else None,
                           "no per-stage allocation fields (a build before round 3 E): total %s MB/s mean, p50 %s, max %s over %d s" % (
                               _f((sum(tot) / len(tot)) if tot else None), _f(_stat(tot)), _f(max(tot) if tot else None), len(rows)), "MISSING"))
            continue
        parts = []
        for label in ("before", "after"):
            d = split.get(label)
            if not d:
                continue
            top = sorted(((v["mbpsMean"] or 0.0, k) for k, v in d["stages"].items()), reverse=True)
            parts.append("%s (cuts %s, %d s): total %s MB/s mean (p50 %s, max %s), a collection every %s s; per stage MB/s mean: %s" % (
                label, "on" if label == "after" else "off", d["seconds"], _f(d["totalMBpsMean"], "%.2f"), _f(d["totalMBpsP50"], "%.2f"),
                _f(d["totalMBpsMax"]), _f(d["gcEveryS"]), ", ".join("%s %.2f" % (k, v) for v, k in top)))
        value = (split.get("after") or split.get("before"))["totalMBpsMean"]
        what = "Mono heap growth, all threads (a lower bound)" if side == "SN" else "SE update thread, exact"
        out.append(row(rid, value, "%s: %s" % (what, " | ".join(parts)), data=split))
    return out

def _terrain_minutes(rows, stage, key, steps_key, step_key):
    """Per minute: the host's worst frame (stages.<stage>.<key>) and the step owning the second that held it."""
    mins = {}
    for r in rows:
        st = ((r.get("stages") or {}).get(stage) or {}).get(key)
        if not isinstance(st, (int, float)) or r.get("_t") is None:
            continue
        m = int(r["_t"] // 60)
        if m not in mins or st > mins[m][0]:
            steps = r.get(steps_key) or {}
            owner = max(steps.items(), key=lambda kv: (kv[1] or {}).get(step_key, 0))[0] if steps else None
            mins[m] = (st, owner)
    return mins

def terrain_rows(name, t0, t1, se, sn, se_text):
    """S10.TERRAIN.<S>.SN /.SE (round 3 F, required): the S8a terrain hosts' worst frame per minute (Subnautica's window
 stage, SE's s8a stage), the minutes of a world up apart (SE's window start, under the curtain) from the play minutes, the
 step that owned each worst frame (the lap clocks), and on Subnautica before/after the time slicing (terrainSlice)."""
    S = name.upper()
    up = set()
    for o in se_text or []:
        if o.get("_t") is not None and t0 - 60 <= o["_t"] <= t1 and (o.get("_text") or "").startswith("DE.S8a world up"):
            up.add(int(o["_t"] // 60))
            up.add(int((o["_t"] + 5) // 60))
    out = []
    for side, rows, stage, key, steps_key, step_key, flag in (
            ("SN", _in(sn["s10.perf"], t0, t1), "window", "maxFrameMs", "terrain", "maxFrameMs", "terrainSlice"),
            ("SE", _in(se["s10.se"], t0, t1), "s8a", "maxUpdateMs", "s8aSteps", "maxUpdateMs", "outcropsBoxes")):
        rid = "S10.TERRAIN.%s.%s" % (S, side)
        if not rows:
            out.append(row(rid, None, "no %s rows in the scene" % ("s10.perf" if side == "SN" else "s10.se"), "MISSING"))
            continue
        what_on = "slice" if side == "SN" else "outcrops boxes"
        groups = [("all", rows)] if flag is None or not any(flag in r for r in rows) else \
                 [(lab, [r for r in rows if bool(r.get(flag)) == want]) for lab, want in (("before (%s off)" % what_on, False), ("after (%s on)" % what_on, True))]
        data, parts, value = {}, [], None
        for lab, rs in groups:
            mins = _terrain_minutes(rs, stage, key, steps_key, step_key)
            if not mins:
                continue
            play = sorted(v[0] for m, v in mins.items() if m not in up)
            wup = [v[0] for m, v in mins.items() if m in up]
            owners = {}
            for m, (v, o) in mins.items():
                if o is not None and v >= 20.0:
                    owners[o] = owners.get(o, 0) + 1
            oc = {}
            for r in rs:                                   # the outcrops step's worst update per minute (SE; round 3, the 6 s wait)
                v = ((r.get(steps_key) or {}).get("outcrops") or {}).get(step_key)
                if isinstance(v, (int, float)) and r.get("_t") is not None:
                    m = int(r["_t"] // 60)
                    oc[m] = max(oc.get(m, 0.0), v)
            d = {"outcropsWorstMs": max(oc.values()) if oc else None, "outcropsPerMinute": {str(m): round(v, 1) for m, v in sorted(oc.items())},
                 "minutes": len(mins), "playWorstMs": play[-1] if play else None, "playMedianMs": _stat(play), "worldUpWorstMs": max(wup) if wup else None,
                 "worldUpMinutes": len(wup), "perMinute": {str(m): round(v[0], 1) for m, v in sorted(mins.items())}, "ownersOver20Ms": owners}
            data[lab] = d
            if value is None or lab.startswith("after"):
                value = d["playWorstMs"]
            parts.append("%s: play minutes worst %s ms (median of the minute worsts %s, %d min), world-up minutes worst %s ms (%d)%s" % (
                lab, _f(d["playWorstMs"]), _f(d["playMedianMs"]), len(play), _f(d["worldUpWorstMs"]), len(wup),
                ("; steps owning minutes >= 20 ms: " + ", ".join("%s %d" % kv for kv in sorted(owners.items(), key=lambda kv: -kv[1]))) if owners else "")
                + ("; outcrops step worst %s ms (per minute %s)" % (_f(d["outcropsWorstMs"]), ",".join(str(v) for v in d["outcropsPerMinute"].values())) if d["outcropsWorstMs"] is not None else ""))
        what = "Subnautica's S8a terrain host (window stage)" if side == "SN" else "SE's S8a host (s8a stage)"
        out.append(row(rid, value, "%s, worst frame per minute: %s" % (what, " | ".join(parts) if parts else "no stage values"), data=data))
    return out

def export_rows(name, t0, t1, se):
    """S10.EXPORT.<S> (round 3 G, required): SE's block export on the update thread, per second and worst update; from the
 round 3 G rows the reads of SE's cells and fat blocks (the snapshot) apart from the commit (diff, record serialization,
 C4 enqueue, rows), and the parts committed; older rows give the export stage alone (before)."""
    S = name.upper()
    rows = _in(se["s10.se"], t0, t1)
    rid = "S10.EXPORT.%s" % S
    if not rows:
        return [row(rid, None, "no s10.se rows in the scene", "MISSING")]
    tot = _vals(rows, "stages.export.ms")
    worst = _vals(rows, "stages.export.maxUpdateMs")
    d = {"seconds": len(rows), "exportMsPerSecMean": (sum(tot) / len(tot)) if tot else None, "exportMsPerSecMax": max(tot) if tot else None,
         "exportWorstUpdateMs": max(worst) if worst else None}
    split = [r for r in rows if isinstance(r.get("export"), dict)]
    detail = "export on SE's update thread: %s ms/s mean (worst second %s), worst update %s ms over %d s" % (
        _f(d["exportMsPerSecMean"], "%.2f"), _f(d["exportMsPerSecMax"]), _f(d["exportWorstUpdateMs"]), len(rows))
    if split:
        rd, cm = _vals(split, "export.readMs"), _vals(split, "export.commitMs")
        cmax, parts = _vals(split, "export.commitMaxUpdateMs"), _vals(split, "export.parts")
        d.update({"readMsPerSecMean": (sum(rd) / len(rd)) if rd else None, "commitMsPerSecMean": (sum(cm) / len(cm)) if cm else None,
                  "commitWorstUpdateMs": max(cmax) if cmax else None, "parts": sum(parts) if parts else 0,
                  "commitUsPerPart": (sum(cm) * 1000.0 / sum(parts)) if parts and sum(parts) > 0 else None,
                  "worker": any(r["export"].get("thread") for r in split)})
        detail += "; reads (the snapshot) %s ms/s, commit (diff + serialization + enqueue) %s ms/s, worst update's commit %s ms, %d parts (%s us per part), serialization %s" % (
            _f(d["readMsPerSecMean"], "%.2f"), _f(d["commitMsPerSecMean"], "%.2f"), _f(d["commitWorstUpdateMs"]), d["parts"], _f(d["commitUsPerPart"]),
            "on a worker" if d["worker"] else "on the update thread")
    else:
        detail += "; no read/commit split (a build before round 3 G)"
    return [row(rid, d["exportWorstUpdateMs"], detail, data=d)]

def trim_rows(se):
    """S10.MEM.TRIM (round 4): SE's one compacting collection per world load under the curtain: private bytes before/after,
 the managed heap, the pause (it falls under the curtain)."""
    tr = [r for r in se["s10.mem"] if r.get("phase") == "trim"]
    if not tr:
        return [row("S10.MEM.TRIM", None, "no GC trim in the session (a build before round 4, DEEPENGINEERS_S10_GCTRIM off, or no curtain after a world load)")]
    freed = [(r.get("privateBeforeMB") or 0) - (r.get("privateAfterMB") or 0) for r in tr]
    parts = ["private %.0f -> %.0f MB, managed %.0f -> %.0f MB, pause %.0f ms%s" % (r.get("privateBeforeMB") or 0, r.get("privateAfterMB") or 0, r.get("gcHeapBeforeMB") or 0,
             r.get("gcHeapAfterMB") or 0, r.get("pauseMs") or 0, " (server GC)" if r.get("serverGc") else "") for r in tr]
    return [row("S10.MEM.TRIM", max(freed), "%d trim(s): %s" % (len(tr), "; ".join(parts)), data={"trims": len(tr), "freedMB": freed})]

def curtain_rows(se):
    """Round 2 B (addendum 2): no SE frame presented under the switch altitude without Subnautica's picture or the curtain
 (s10.curtain frame counts from SE's GUI pass), and Subnautica's showing time per curtain episode."""
    secs = [r for r in se["s10.curtain"] if r.get("phase") == "second"]
    eps = [r for r in se["s10.curtain"] if r.get("phase") == "episode"]
    if not secs:
        return [row("S10.CURTAIN.EXPOSED", None, "no s10.curtain rows (a build before round 2 B)", "MISSING")]
    exposed = sum(int(r.get("framesExposed") or 0) for r in secs)
    zone = sum(int(r.get("framesZone") or 0) for r in secs)
    cur = sum(int(r.get("framesCurtain") or 0) for r in secs)
    out = [row("S10.CURTAIN.EXPOSED", exposed, "%d SE frames under the switch altitude showed SE's own picture (of %d in the zone; %d under the curtain)" % (
        exposed, zone, cur), "PASS" if exposed == 0 else "FAIL", data={"zone": zone, "curtain": cur})]
    by = {}
    for e in eps:
        by.setdefault(e.get("cause") or "?", []).append(float(e.get("showMs") or 0))
    parts = ["%s %d (p50 %s, max %s ms)" % (k, len(v), _f(_stat(v), "%.0f"), _f(max(v), "%.0f")) for k, v in sorted(by.items())]
    allv = [x for v in by.values() for x in v]
    out.append(row("S10.CURTAIN.SHOWMS", _stat(allv), "Subnautica's showing time per curtain episode: " + ("; ".join(parts) if parts else "no episode"),
                   data={k: {"n": len(v), "p50": _stat(v), "max": max(v)} for k, v in by.items()}))
    return out

def paste_rows(se, t0, t1):
    pr = [r for r in _in(se["s10.paste"], t0, t1)]
    done = [r for r in pr if r.get("phase") in ("done", "failed")]
    if not done:
        return [row("S10.PERF.PASTE.SELECT", None, "no s10.paste result in the scene", "MISSING")]
    r = done[-1]
    out = [row("S10.PERF.PASTE.SELECT", r.get("selectMs"), "selection lag %s ms (first Paste() %s ms on the update thread), path %s, %s blocks%s" % (
        _f(r.get("selectMs"), "%.0f"), _f(r.get("firstCallMs"), "%.0f"), r.get("path"), r.get("blocks"), (" (%s)" % r["why"]) if r.get("why") else ""), data=r),
           row("S10.PERF.PASTE.PLACE", r.get("placeMs"), "placement wait %s ms (second call %s ms), %s blocks in SE, %s tries; result %s" % (
               _f(r.get("placeMs"), "%.0f"), _f(r.get("secondCallMs"), "%.0f"), r.get("blocksSeen"), r.get("tries"), r.get("phase")), data=r)]
    placed = [x for x in pr if x.get("phase") == "placed"]
    t_placed = placed[-1]["_t"] if placed else None
    spawn = None
    if t_placed is not None:
        bud = [b for b in se["s5.budget"] if b.get("_t") is not None and b["_t"] >= t_placed]
        seen_pending = False
        for b in bud:
            pend = ((b.get("sn") or {}).get("pendingParts"))
            if pend:
                seen_pending = True
            if seen_pending and pend == 0 and (b.get("parts") or 0) > 0:
                spawn = (b["_t"] - t_placed) * 1000.0
                break
    out.append(row("S10.PERF.PASTE.SPAWN", spawn, "Subnautica's spawn %s ms after SE placed it (s5.budget sn.pendingParts back to 0)" % _f(spawn, "%.0f"),
                   "INFO" if spawn is not None else "MISSING"))
    return out

def find_loads(se_text, sn_text, sn_rows, se_game_logs=(), se_rows=None):
    """One dict per world load: the marks (epoch s) and the subsystem durations."""
    marks = [(o["_t"], o["_text"]) for o in se_text if o.get("_t") is not None]
    marks.sort()
    loads, cur = [], None
    regs = [(i, re.compile(p)) for (i, s, p, g) in LOAD_MARKS]
    for t, txt in marks:
        for i, rg in regs:
            if rg.search(txt):
                if i == "runload":
                    cur = {"marks": {"runload": t}, "subs": {}, "lines": {}}
                    loads.append(cur)
                elif cur is not None and i not in cur["marks"]:
                    cur["marks"][i] = t
                    cur["lines"][i] = txt[:200]
                break
    for ld in loads:
        t0 = ld["marks"]["runload"]
        nxt = [x["marks"]["runload"] for x in loads if x["marks"]["runload"] > t0]
        t_end = min(nxt) if nxt else float("inf")
        side_text = {"se": [(o["_t"], o["_text"]) for o in se_text if o.get("_t") and t0 <= o["_t"] < t_end],
                     "sn": [(o["_t"], o["_text"]) for o in sn_text if o.get("_t") and t0 - 60 <= o["_t"] < t_end]}
        for sid, side, pat, grp, desc in LOAD_SUBS:
            rg = re.compile(pat)
            for t, txt in side_text[side]:
                m = rg.search(txt)
                if m:
                    ms = float(m.group("ms")) if grp and m.groupdict().get("ms") else None
                    ld["subs"][sid] = {"t": t, "ms": ms, "atS": t - t0, "desc": desc, "line": txt[:160]}
                    break
        # rows that carry their own time: the S8a outcrop scan (s8a.outcrops ms, SE) and the ore palette (Subnautica: its own
 # save load, before SE's world load when the host save loads first: a negative +s says it is not on SE's screen)
        oc = [r for r in (se_rows or {}).get("s8a.outcrops", []) if r.get("_t") and t0 <= r["_t"] < t_end and r.get("ms") is not None]
        if "OUTCROPS" not in ld["subs"] and oc:
            ld["subs"]["OUTCROPS"] = {"t": oc[0]["_t"], "ms": float(oc[0]["ms"]), "atS": oc[0]["_t"] - t0, "desc": "the S8a outcrop scan", "line": "s8a.outcrops"}
        po = [r for r in sn_rows.get("s8a.palette.ore", []) if r.get("_t") and t0 - 600 <= r["_t"] < t_end]
        if "ORES" not in ld["subs"] and po:
            ld["subs"]["ORES"] = {"t": po[-1]["_t"], "ms": None, "atS": po[-1]["_t"] - t0,
                                  "desc": "the S8a ore palette (Subnautica WorldStreamer.Start)", "line": "s8a.palette.ore"}
        pic = [r for r in sn_rows.get("s7a.picture", []) if r.get("_t") and t0 <= r["_t"] < t_end and (r.get("presentedSeTick") or 0) > 0]
        if pic:
            ld["marks"]["picture"] = pic[0]["_t"]
        if "underwater" in ld["marks"] and "picture" not in ld["marks"]:
            ld["marks"]["picture"] = ld["marks"]["underwater"]
        ld["seLoadingDuration"] = None
    for p in se_game_logs:
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                durs = [float(m.group(1)) for m in re.finditer(r"Loading duration: ([\d.]+)", f.read())]
        except OSError:
            durs = []
        for ld, dur in zip(loads, durs):
            ld["seLoadingDuration"] = dur
    return loads

PHASES = (("PRE", "runload", "onloading", "SE before the session (world file, mods)"),
          ("ENTITIES", "loaddata", "beforestart", "MyEntities.Load (planets, grids; the Water Mod's API)"),
          ("BEFORESTART", "beforestart", "runloadret", "BeforeStart (our tiles, site, drill store, prefetch plan)"),
          ("CLIPMAPS", "runloadret", "firstupdate", "the loading screen waiting for ClipmapsReady (voxel meshes read through our providers)"),
          ("HELLO", "firstupdate", "helloack", "first update -> Subnautica's hello ack (streams, window start)"),
          ("PICTURE", "helloack", "picture", "hello ack -> the first Subnautica picture under water"),
          ("TOTAL", "runload", "picture", "the loading screen's start -> the first Subnautica picture"))

def launcher_events(paths, t_from=None, t_to=None):
    """sn_started / sn_ready / se_started / s10_parallel_start from launcher-*.jsonl (the newest start inside the window)."""
    ev = {}
    for p in paths:
        try:
            f = open(p, encoding="utf-8", errors="replace")
        except OSError:
            continue
        with f:
            for line in f:
                try:
                    o = json.loads(line)
                except ValueError:
                    continue
                k = o.get("ev")
                if k not in ("sn_started", "sn_ready", "se_started", "s10_parallel_start"):
                    continue
                t = ts(o.get("utc"))
                if t is None or (t_from is not None and t < t_from) or (t_to is not None and t > t_to):
                    continue
                if k == "sn_started":
                    ev = {}
                ev[k] = (t, o)
    return ev

def startup_rows(ev, loads):
    """Round 2 C: Subnautica's ready time (launcher sn_ready) and the start to the first picture (both games' starts)."""
    if "sn_started" not in ev:
        return [row("S10.LOAD.SNREADY", None, "no launcher start in the session (launcher-*.jsonl)", "MISSING")]
    out = []
    t_sn = ev["sn_started"][0]
    rd = ev.get("sn_ready")
    out.append(row("S10.LOAD.SNREADY", rd[1].get("seconds") * 1000.0 if rd and rd[1].get("seconds") is not None else None,
                   "Subnautica launched -> ReadyIdle: %s s (the owner waited this long before SE started, unless the parallel start ran)" % (
                       rd[1].get("seconds") if rd else "n/a"), "INFO" if rd else "MISSING"))
    se_t = ev.get("se_started", (None,))[0]
    pic = next((l["marks"].get("picture") for l in loads if l["marks"].get("picture") and l["marks"]["picture"] > t_sn), None)
    total = (pic - t_sn) * 1000.0 if pic else None
    out.append(row("S10.LOAD.STARTUP", total, "launch -> first Subnautica picture %s s (SE started %s s after Subnautica; parallel start %s)" % (
        _f(total / 1000.0 if total else None), _f(se_t - t_sn if se_t else None), "yes" if "s10_parallel_start" in ev else "no"),
        "INFO" if total else "MISSING", data={"seAfterSnS": (se_t - t_sn) if se_t else None, "parallel": "s10_parallel_start" in ev}))
    return out

def load_rows(loads, se_rows):
    out = []
    for n, ld in enumerate(loads):
        tag = "" if len(loads) == 1 else "#%d" % (n + 1)
        m = ld["marks"]
        for pid, a, b, desc in PHASES:
            v = (m[b] - m[a]) * 1000.0 if a in m and b in m else None
            out.append(row("S10.LOAD%s.%s" % (tag, pid), v, "%s ms: %s" % (_f(v, "%.0f"), desc), "INFO" if v is not None else "MISSING",
                           data={"from": a, "to": b}))
        if ld.get("seLoadingDuration") is not None:
            out.append(row("S10.LOAD%s.SEOWN" % tag, ld["seLoadingDuration"] * 1000.0, "SE's own 'Loading duration: %.1f' s (SpaceEngineers_*.log)" % ld["seLoadingDuration"]))
        t0 = m["runload"]
        t1 = m.get("picture") or m.get("firstupdate") or (t0 + 300)
        st = [r for r in se_rows.get("s4.stats", []) if r.get("_t") and t0 <= r["_t"] <= t1 + 30]
        if st:
            r = st[0]
            busy = (r.get("calls") or 0) * (r.get("meanUs") or 0) / 1000.0
            out.append(row("S10.LOAD%s.READS" % tag, busy, "our voxel providers during the load: %s ReadRange calls, mean %s us, p99 %s us = %.0f ms of reads (worker threads) by the first s4.stats row" % (
                r.get("calls"), _f(r.get("meanUs"), "%.0f"), r.get("p99us"), busy), data={k: r.get(k) for k in ("calls", "meanUs", "p50us", "p99us", "maxUs")}))
        sp = [r for r in se_rows.get("s8a.perf", []) if r.get("_t") and t0 <= r["_t"] <= t1 + 30]
        if sp:
            out.append(row("S10.LOAD%s.RINGHOOK" % tag, sp[0].get("hookUsMax"), "the S8a ring-field ReadRange hook: p99 %s us, max %s us (first s8a.perf row)" % (
                sp[0].get("hookUsP99"), sp[0].get("hookUsMax"))))
        for sid, side, pat, grp, desc in LOAD_SUBS:
            s = ld["subs"].get(sid)
            if s:
                out.append(row("S10.LOAD%s.%s" % (tag, sid), s["ms"], "%s: %s at +%.1f s%s" % (desc, ("%.0f ms" % s["ms"]) if s["ms"] is not None else "seen", s["atS"],
                                                                                            "" if s["ms"] is not None else " (no duration in its line)"), data=s))
            else:
                out.append(row("S10.LOAD%s.%s" % (tag, sid), None, "%s: not in this load's lines" % desc, "MISSING"))
    return out

# ----------------------------------------------------------------------------------------------------------------------
# evaluate, table, site JSON
# ----------------------------------------------------------------------------------------------------------------------

def evaluate(folder=None, se_logs=(), sn_logs=(), se_game_logs=(), machine="desktop", launcher_logs=()):
    se_logs, sn_logs, se_game_logs, launcher_logs = list(se_logs), list(sn_logs), list(se_game_logs), list(launcher_logs)
    windows = []
    if folder:
        se_logs += sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        sn_logs += sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
        se_game_logs += sorted(glob.glob(os.path.join(folder, "SpaceEngineers_*.log")))
        launcher_logs += sorted(glob.glob(os.path.join(folder, "launcher-*.jsonl")))
        windows = scene_windows(os.path.join(folder, "s10-session.jsonl"))
    se, se_text = read_side(se_logs, SE_ROWS)
    sn, sn_text = read_side(sn_logs, SN_ROWS)
    rows = []
    if not windows:
        ts_all = [r["_t"] for k in ("s10.perf", "s5.perf") for r in sn[k] if r.get("_t")] or [r["_t"] for r in se["sample"] if r.get("_t")]
        if ts_all:
            windows = [("free", min(ts_all), max(ts_all))]
    for name, t0, t1 in windows:
        rows += scene_rows(name, t0, t1, se, sn, machine)
        rows += mem_rows(name, t0, t1, se, sn)
        rows += alloc_rows(name, t0, t1, se, sn)
        rows += terrain_rows(name, t0, t1, se, sn, se_text)
        rows += export_rows(name, t0, t1, se)
        if name == "paste":
            rows += paste_rows(se, t0, t1)
    rows += curtain_rows(se)
    rows += trim_rows(se)
    snp = sn["s10.perf"]
    if snp:
        last = snp[-1]
        alloc = _vals(snp, "monoAllocMBps")
        rows.append(row("S10.PRESET", last.get("preset"), "preset %s (SE %s), fps cap %s (%s decisions capped), telemetry rows skipped %s, managed allocation %s MB/s p50 (max %s)" % (
            last.get("preset"), (se["s10.se"][-1].get("preset") if se["s10.se"] else "n/a"), last.get("fpsCap"), last.get("fpsCapped"), last.get("rowsSkipped"),
            _f(_stat(alloc)), _f(max(alloc) if alloc else None)), "INFO" if last.get("preset") else "MISSING"))
    rows.append(row("S10.PERF.GPU", None, "no GPU time in either game's rows (Subnautica's player has no FrameTimingManager, SE logs none): read GPU load and VRAM on the RTSS overlay"))
    loads = find_loads(se_text, sn_text, sn, se_game_logs, se)
    se_rows_all = dict(se)
    rows += load_rows(loads, se_rows_all)
    if not launcher_logs and se_logs:
        try:
            t_se = min(os.path.getctime(p) for p in se_logs)
            launcher_logs = [p for p in glob.glob(os.path.join(LOG_DIR, "launcher-*.jsonl")) if t_se - 900 <= os.path.getmtime(p) <= t_se + 120]
        except OSError:
            launcher_logs = []
    t0s = [l["marks"]["runload"] for l in loads]
    rows += startup_rows(launcher_events(launcher_logs, (min(t0s) - 900) if t0s else None, max(t0s) if t0s else None), loads)
    return rows

def verdict(rows, strict=True):
    bad = [r for r in rows if r["verdict"] == "FAIL" or (strict and r["verdict"] == "MISSING")]
    return not bad

def table(rows):
    w = max([len(r["id"]) for r in rows] + [10])
    return "\n".join("%-*s %-7s %s" % (w, r["id"], r["verdict"], r["detail"]) for r in rows)

def site_entry(rows, machine, spec=None):
    """The site's JSON block of one machine from one perf session."""
    by = {r["id"]: r for r in rows}
    scenes = {}
    for s in SCENES:
        S = s.upper()
        if "S10.PERF.%s.SN" % S not in by:
            continue
        sn, se_, mem, win = (by.get("S10.PERF.%s.%s" % (S, k), {}).get("data", {}) for k in ("SN", "SE", "MEM", "WINDOW"))
        e = {"snFps": sn.get("fpsP50"), "snFrameP95Ms": sn.get("frameP95Ms"), "snFrameMaxMs": sn.get("frameMaxMs"),
             "seFps": se_.get("fpsP50"), "seFrameP95Ms": se_.get("engineFrameP95Ms"), "memoryMB": mem.get("totalMB"), "giveUps": win.get("giveUps")}
        if s == "paste":
            e["selectMs"] = by.get("S10.PERF.PASTE.SELECT", {}).get("value")
            e["placeMs"] = by.get("S10.PERF.PASTE.PLACE", {}).get("value")
            e["spawnMs"] = by.get("S10.PERF.PASTE.SPAWN", {}).get("value")
        scenes[s] = e
    load = {k.split(".")[-1]: by[k]["value"] for k in by if k.startswith("S10.LOAD.") and k.split(".")[-1] in [p[0] for p in PHASES]}
    return {"machine": machine, "spec": spec or "", "measured": datetime.datetime.utcnow().strftime("%Y-%m-%d"), "scenes": scenes, "loadMs": load}

def write_site(path, entry):
    try:
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
    except (OSError, ValueError):
        doc = {}
    doc.setdefault("machines", {})[entry["machine"]] = entry
    doc["passMarks"] = PASS_MARKS
    doc["generated"] = datetime.datetime.utcnow().isoformat(timespec="seconds") + "Z"
    doc["note"] = "Measured by tests/s10/analyze_s10.py from the plugins' rows (launcher.py s10 --part perf). Part 1 baseline: pass marks are informative until the S10 rewrite lands."
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    return doc

# ----------------------------------------------------------------------------------------------------------------------
# the load history (every world load in the logs folder)
# ----------------------------------------------------------------------------------------------------------------------

_HIST = re.compile(r'"t":"(?P<t>[^"]+)".{0,40}"msg":"(?P<m>DE\.(?:Phase LoadingWorld \(MyGuiScreenLoading|LoadOrder MySession\.OnLoading|Site LoadData|Site BeforeStart|Phase RunLoad returned|Site first UpdateBeforeSimulation|Hello ACK|S8a on:|S8a drill ON|Terrain OK tiles|S8a window started|S8a sample ready)[^"]*)')

def load_history(log_dir=LOG_DIR):
    """[(file, date, phases ms, flags)] for every world load of every se-*.jsonl (a fast text scan)."""
    out = []
    for p in sorted(glob.glob(os.path.join(log_dir, "se-*.jsonl"))):
        loads, cur, flags = [], None, {}
        try:
            f = open(p, encoding="utf-8", errors="replace")
        except OSError:
            continue
        with f:
            for line in f:
                if '"ev":"s4.stats"' in line and cur is not None and "reads" not in cur:
                    try:
                        o = json.loads(line)
                        cur["reads"] = {"calls": o.get("calls"), "p50us": o.get("p50us"), "meanUs": round(o.get("meanUs") or 0),
                                        "busyMs": round((o.get("calls") or 0) * (o.get("meanUs") or 0) / 1000.0)}
                    except ValueError:
                        pass
                    continue
                if '"msg":"DE.' not in line:
                    continue
                m = _HIST.search(line)
                if not m:
                    continue
                t, msg = ts(m.group("t")), m.group("m")
                if msg.startswith("DE.S8a on:"):
                    flags["window"] = "window=on" in msg
                    flags["ring"] = "ring=on" in msg
                    flags["sample"] = "sample=on" in msg
                    continue
                if msg.startswith("DE.Phase LoadingWorld"):
                    cur = {"runload": t, "flags": dict(flags)}
                    loads.append(cur)
                    continue
                if cur is None:
                    continue
                key = ("onloading" if "OnLoading" in msg else "loaddata" if "LoadData" in msg else "beforestart" if "BeforeStart" in msg else
                       "runloadret" if "RunLoad returned" in msg else "firstupdate" if "first UpdateBeforeSimulation" in msg else
                       "helloack" if "Hello ACK" in msg else "tiles" if "Terrain OK" in msg else "window" if "window started" in msg else
                       "sample" if "sample ready" in msg else "drill" if "drill ON" in msg else None)
                if key == "drill":
                    wm = re.search(r"world=(.*?) tiles=", msg)
                    if wm:
                        cur["world"] = os.path.basename(wm.group(1).rstrip("\\/"))
                    continue
                if key and key not in cur:
                    cur[key] = t
        for ld in loads:
            def d(a, b):
                return round((ld[b] - ld[a]) * 1000.0) if a in ld and b in ld else None
            out.append({"file": os.path.basename(p), "date": datetime.datetime.utcfromtimestamp(ld["runload"]).strftime("%Y-%m-%d %H:%M"),
                        "world": ld.get("world"), "flags": ld.get("flags"),
                        "pre": d("runload", "onloading"), "entities": d("loaddata", "beforestart"), "beforeStart": d("beforestart", "runloadret"),
                        "clipmaps": d("runloadret", "firstupdate"), "hello": d("firstupdate", "helloack"), "toFirstUpdate": d("runload", "firstupdate"),
                        "tiles": "tiles" in ld, "reads": ld.get("reads")})
    return out

def history_table(hist):
    lines = ["%-16s %-22s %7s %8s %8s %8s %7s %9s %5s %18s  flags" % ("date (UTC)", "world", "pre", "entities", "bStart", "clipmaps", "hello", "toUpdate",
                                                                      "tiles", "reads n/p50us/busyMs")]
    for h in hist:
        fl = h.get("flags") or {}
        r = h.get("reads") or {}
        lines.append("%-16s %-22s %7s %8s %8s %8s %7s %9s %5s %18s  %s" % (h["date"], (h.get("world") or "?")[:22], h["pre"], h["entities"], h["beforeStart"], h["clipmaps"],
                                                                      h["hello"], h["toFirstUpdate"], "yes" if h.get("tiles") else "no",
                                                                      ("%s/%s/%s" % (r.get("calls"), r.get("p50us"), r.get("busyMs"))) if r else "-",
                                                                      ",".join(k for k, v in sorted(fl.items()) if v) or "-"))
    return "\n".join(lines)

# ----------------------------------------------------------------------------------------------------------------------
# self-test
# ----------------------------------------------------------------------------------------------------------------------

def _j(o):
    """Compact JSON, as the plugins write it (the readers match '"ev":"' without spaces)."""
    return json.dumps(o, separators=(",", ":"))

def _iso(t):
    return datetime.datetime.utcfromtimestamp(t).strftime("%Y-%m-%dT%H:%M:%S.%f") + "Z"

def selftest():
    import tempfile
    res = []

    def check(ok, what):
        res.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))

    print("S10 analyzer self-test (tests/s10/analyze_s10.py --selftest)")
    v = list(range(1, 101))
    check((percentile(v, 0.5), percentile(v, 0.95), percentile(v, 0.99), percentile([7], 0.95), percentile([], 0.5)) == (50, 95, 99, 7, None),
          "nearest-rank percentile: the C# twin's vector (S10PerfRulesTests)")
    counts, slices = blueprint_counts(10000)
    check(sum(counts) == 10000 and counts == [7675, 2020, 204, 101] and slices == 101, "the 10,000-block ship: %s in %d slices (S10PerfRulesTests)" % (counts, slices))
    check(sum(blueprint_counts(1)[0]) == 1 and sum(blueprint_counts(99999)[0]) == 60000, "the layout clamps 1 .. 60,000 blocks")
    check(ts("2026-10-24T13:42:27.9431014Z") - ts("2026-10-24T13:42:27.943Z") < 0.001, "7-decimal SE and 3-decimal SN timestamps")
    d = tempfile.mkdtemp(prefix="s10-selftest-")
    t0 = 1_800_000_000.0
    with open(os.path.join(d, "s10-session.jsonl"), "w", encoding="utf-8") as f:
        for name, a, b in (("spawn", 100, 160), ("paste", 170, 260)):
            f.write(json.dumps({"utc": _iso(t0 + a), "ev": "part", "name": name, "phase": "begin"}) + "\n")
            f.write(json.dumps({"utc": _iso(t0 + b), "ev": "part", "name": name, "phase": "end"}) + "\n")
    se = open(os.path.join(d, "se-20261024-000000.jsonl"), "w", encoding="utf-8")
    sn = open(os.path.join(d, "sn-20261024_000000-1.jsonl"), "w", encoding="utf-8")

    def log(f, t, msg, side="se"):
        if side == "se":
            f.write(_j({"t": _iso(t0 + t), "ev": "log", "msg": msg}) + "\n")
        else:
            f.write(_j({"t": _iso(t0 + t), "tickMs": 1, "frame": 1, "level": "info", "event": msg.split(" ")[0], "text": msg}) + "\n")

    log(se, 0, "DE.Phase LoadingWorld (MyGuiScreenLoading.RunLoad prefix) target=MyGuiScreenGamePlay worldEpoch=1")
    log(se, 12, "DE.LoadOrder MySession.OnLoading +17 s")
    log(se, 12.1, "DE.Site LoadData worldEpoch=1")
    log(se, 21, "DE.Site BeforeStart (after MyEntities.Load): planets")
    log(se, 21.2, "DE.Terrain OK tiles=34 siteVersion=1 createMs=84.8 privateMB 5177 -> 5178")
    log(se, 21.5, "DE.Phase RunLoad returned +27.542 s after Init")
    se.write(_j({"t": _iso(t0 + 30), "ev": "s4.stats", "calls": 9000, "meanUs": 5000, "p50us": 6800, "p99us": 15000, "maxUs": 29744}) + "\n")
    log(se, 41.5, "DE.Site first UpdateBeforeSimulation worldEpoch=1 +41500 ms")
    log(se, 41.6, "DE.Site OK site v1 bound: one DE_4546B")
    log(se, 43, "DE.Hello ACK helloAckEpoch=1")
    log(se, 44.5, "DE.S8a sample ready: chunk gp1, init 1228 ms")
    log(se, 46, "DE.View -> Underwater (enter) tick=1")
    for s in range(100, 262):
        sn.write(_j({"t": _iso(t0 + s), "ev": "s10.perf", "data": {"fps": 120.0 if s < 170 else 20.0, "frameP50Ms": 8.3, "frameP95Ms": 9.0, "frameMaxMs": 12.0,
                                                                           "privateMB": 4000.0, "stages": {"mirror": {"ms": 2.0 if s < 170 else 600.0, "maxFrameMs": 1.0, "calls": 120}}}}) + "\n")
        se.write(_j({"t": _iso(t0 + s + 0.3), "ev": "sample", "fps": 120, "frameTimeMaxMs": 9.0, "privateMB": 9000.0}) + "\n")
        sn.write(_j({"t": _iso(t0 + s + 0.4), "ev": "s8a.status", "data": {"pending": 3, "inFlight": 1, "jobGiveUps": 0 if s < 200 else 2, "reloads": s}}) + "\n")
    for s_ in range(100, 262, 10):
        se.write(_j({"t": _iso(t0 + s_ + 0.5), "ev": "s10.mem", "privateMB": 6000.0 + (s_ - 100) * 0.1, "pageFaultsPerSec": 50.0}) + "\n")
        sn.write(_j({"t": _iso(t0 + s_ + 0.6), "ev": "s10.mem", "data": {"privateMB": 4000.0 if s_ < 170 else 5200.0, "mirrorMeshMB": 120.0, "chunks": 80}}) + "\n")
    se.write(_j({"t": _iso(t0 + 171), "ev": "s10.paste", "phase": "start", "blocks": 10000}) + "\n")
    se.write(_j({"t": _iso(t0 + 180), "ev": "s10.paste", "phase": "placed", "blocks": 10000, "selectMs": 4200.0, "placeMs": 3100.0}) + "\n")
    se.write(_j({"t": _iso(t0 + 180.1), "ev": "s10.paste", "phase": "done", "blocks": 10000, "selectMs": 4200.0, "placeMs": 3100.0, "path": "clipboard", "blocksSeen": 10000, "tries": 1}) + "\n")
    se.write(_j({"t": _iso(t0 + 101), "ev": "s10.curtain", "phase": "second", "framesZone": 120, "framesCurtain": 30, "framesSn": 90, "framesExposed": 0}) + "\n")
    se.write(_j({"t": _iso(t0 + 101.5), "ev": "s10.curtain", "phase": "episode", "cause": "streaming", "showMs": 840.0}) + "\n")
    for s, pend in ((181, 6608), (190, 120), (200, 0)):
        se.write(_j({"t": _iso(t0 + s), "ev": "s5.budget", "parts": 10000, "sn": {"pendingParts": pend}}) + "\n")
    se.close()
    sn.close()
    rows = evaluate(d, machine="desktop")
    by = {r["id"]: r for r in rows}
    check(abs(by["S10.PERF.SPAWN.SN"]["value"] - 120.0) < 1e-9 and abs(by["S10.PERF.PASTE.SN"]["value"] - 20.0) < 1e-9,
          "the scenes' windows slice the s10.perf rows (spawn 120 fps, paste 20)")
    check("NOT met" in by["S10.PERF.PASTE.SN"]["detail"] and by["S10.PERF.PASTE.SN"]["verdict"] == "INFO", "the paste mark is printed, the verdict stays INFO (part 1)")
    check(by["S10.PERF.PASTE.WINDOW"]["value"] == 2 and by["S10.PERF.SPAWN.WINDOW"]["value"] == 0, "give-ups counted per scene")
    check(by["S10.PERF.PASTE.SELECT"]["value"] == 4200.0 and by["S10.PERF.PASTE.PLACE"]["value"] == 3100.0, "selection lag and placement wait read from s10.paste")
    check(abs(by["S10.PERF.PASTE.SPAWN"]["value"] - 20000.0) < 1.0, "Subnautica's spawn: placed -> pendingParts 0 (20 s)")
    check(abs(by["S10.PERF.PASTE.MEM"]["value"] - 13000.0) < 1e-6, "memory: SE + Subnautica private max")
    check(abs(by["S10.LOAD.CLIPMAPS"]["value"] - 20000.0) < 1.0 and abs(by["S10.LOAD.TOTAL"]["value"] - 46000.0) < 1.0,
          "load phases: the clipmap wait 20 s, the total to the first picture 46 s")
    check(abs(by["S10.LOAD.READS"]["value"] - 45000.0) < 1e-6 and by["S10.LOAD.TILES"]["value"] == 84.8 and by["S10.LOAD.CHUNKLIB"]["value"] == 1228.0,
          "load subsystems: provider reads 45 s, tiles 84.8 ms, chunk library 1228 ms")
    check(by["S10.LOAD.ROOMS"]["verdict"] == "MISSING", "a subsystem absent from the lines is MISSING, not invented")
    check(by["S10.MEM.SPAWN.SE"]["verdict"] == "PASS" and by["S10.MEM.PASTE.SN"]["verdict"] == "FAIL" and abs(by["S10.MEM.PASTE.BOTH"]["value"] - 11215.0) < 1.0
          and by["S10.MEM.PASTE.BOTH"]["verdict"] == "PASS", "the 16 GB rows: SE 6.0 GB passes, Subnautica 5.2 GB fails its 5 GB, both 11.2 GB pass 13.5 GB")
    check(fnv1a64(PRESETS_TEXT) == PRESETS_HASH, "the presets' canonical text hashes to the pinned value (the C# twin's S10Presets.ConventionsHash)")
    check(by["S10.CURTAIN.EXPOSED"]["verdict"] == "PASS" and by["S10.CURTAIN.SHOWMS"]["value"] == 840.0, "the curtain rows: no exposed frame, the showing time")
    entry = site_entry(rows, "desktop", "test rig")
    check(entry["scenes"]["paste"]["snFps"] == 20.0 and entry["scenes"]["paste"]["selectMs"] == 4200.0 and entry["loadMs"]["TOTAL"] is not None, "the site entry")
    sp = os.path.join(d, "requirements.json")
    write_site(sp, entry)
    doc = write_site(sp, dict(entry, machine="laptop"))
    check(set(doc["machines"]) == {"desktop", "laptop"} and "pasteSnFps" in doc["passMarks"], "the site JSON keeps one block per machine")
    # round 3 E: the allocation rows split the seconds by the cuts' switch (before/after in one session)
    snr = [{"_t": 10.0 + i, "spanS": 1.0, "allocCuts": i >= 4, "monoAllocMBps": 40.0 if i < 4 else 10.0, "gc0": i // 2 if i < 4 else 2 + (i - 4) // 4,
            "stages": {"place": {"allocKB": 20480.0 if i < 4 else 5120.0}, "plugin": {"allocKB": 30720.0 if i < 4 else 8192.0}}} for i in range(12)]
    ser = [{"_t": 10.5 + i, "spanS": 1.0, "allocCuts": True, "updateAllocMBps": 3.0, "gc0": i, "stages": {"export": {"allocKB": 1024.0}}} for i in range(5)]
    ar = {r["id"]: r for r in alloc_rows("free", 0.0, 100.0, {"s10.se": ser}, {"s10.perf": snr})}
    a_sn = ar["S10.ALLOC.FREE.SN"]
    check(a_sn["verdict"] == "INFO" and abs(a_sn["data"]["before"]["stages"]["place"]["mbpsMean"] - 20.0) < 1e-9
          and abs(a_sn["data"]["after"]["stages"]["place"]["mbpsMean"] - 5.0) < 1e-9 and abs(a_sn["value"] - 10.0) < 1e-9
          and abs(a_sn["data"]["before"]["gcEveryS"] - 4.0) < 1e-9 and abs(a_sn["data"]["after"]["gcEveryS"] - 8.0) < 1e-9,
          "S10.ALLOC: per-stage MB/s before (cuts off) and after (cuts on), the collection interval of each")
    check(abs(ar["S10.ALLOC.FREE.SE"]["data"]["after"]["stages"]["export"]["mbpsMean"] - 1.0) < 1e-9 and "before" not in ar["S10.ALLOC.FREE.SE"]["data"],
          "S10.ALLOC: the SE side alone (cuts on only)")
    old = {r["id"]: r for r in alloc_rows("free", 0.0, 100.0, {"s10.se": []}, {"s10.perf": [{"_t": 1.0, "monoAllocMBps": 3.0}]})}
    check(old["S10.ALLOC.FREE.SN"]["verdict"] == "MISSING" and old["S10.ALLOC.FREE.SE"]["verdict"] == "MISSING",
          "S10.ALLOC: a build before round 3 E (no per-stage fields) is MISSING, its total still printed")
    # round 3 F: the terrain rows: worst frame per minute, world-up minutes apart, the owning step, before/after the slicing
    base = 6000 * 60.0
    snr = []
    for i in range(240):
        sl = i >= 120
        worst = 30.0 if i % 60 == 7 else 5.0
        if i == 70:
            worst = 220.0                                    # a world up in minute 6001
        snr.append({"_t": base + i, "terrainSlice": sl, "stages": {"window": {"maxFrameMs": worst / (2.0 if sl and worst > 20 else 1.0)}},
                    "terrain": {"manifests": {"maxFrameMs": worst * 0.8}, "flora": {"maxFrameMs": 1.0}}})
    ser = [{"_t": base + i, "stages": {"s8a": {"maxUpdateMs": 150.0 if i == 70 else 12.0}}, "s8aSteps": {"window": {"maxUpdateMs": 140.0 if i == 70 else 2.0}}} for i in range(240)]
    txt = [{"_t": base + 68, "_text": "DE.S8a world up: storeKey=1"}]
    tr = {r["id"]: r for r in terrain_rows("free", 0.0, 1e12, {"s10.se": ser}, {"s10.perf": snr}, txt)}
    tsn, tse = tr["S10.TERRAIN.FREE.SN"], tr["S10.TERRAIN.FREE.SE"]
    check(tsn["data"]["before (slice off)"]["playWorstMs"] == 30.0 and tsn["data"]["before (slice off)"]["worldUpWorstMs"] == 220.0
          and tsn["data"]["after (slice on)"]["playWorstMs"] == 15.0 and tsn["value"] == 15.0 and tsn["data"]["before (slice off)"]["ownersOver20Ms"] == {"manifests": 2},
          "S10.TERRAIN: the worst frame per minute, the world-up minute apart, the owning step, before/after the slicing")
    ser2 = [{"_t": base + i, "outcropsBoxes": i >= 120, "stages": {"s8a": {"maxUpdateMs": 6000.0 if i == 30 else 10.0}},
             "s8aSteps": {"outcrops": {"maxUpdateMs": 6000.0 if i == 30 else 1.0}}} for i in range(240)]
    t2 = terrain_rows("free", 0.0, 1e12, {"s10.se": ser2}, {"s10.perf": []}, [])
    t2se = [r for r in t2 if r["id"] == "S10.TERRAIN.FREE.SE"][0]
    check(t2se["data"]["before (outcrops boxes off)"]["outcropsWorstMs"] == 6000.0 and t2se["data"]["after (outcrops boxes on)"]["outcropsWorstMs"] == 1.0
          and "outcrops step worst" in t2se["detail"], "S10.TERRAIN.SE: the outcrops step's worst update per minute, before/after the boxes")
    check(tse["data"]["all"]["worldUpWorstMs"] == 150.0 and tse["data"]["all"]["playWorstMs"] == 12.0 and tse["data"]["all"]["ownersOver20Ms"] == {"window": 1},
          "S10.TERRAIN: SE's s8a stage (one group: SE never slices)")
    # round 3 G: the export row: the stage alone (before), the read/commit split (after)
    ex_old = export_rows("free", 0.0, 1e12, {"s10.se": [{"_t": 1.0, "stages": {"export": {"ms": 4.0, "maxUpdateMs": 30.0}}}]})[0]
    ex_new = export_rows("free", 0.0, 1e12, {"s10.se": [{"_t": 1.0 + i, "stages": {"export": {"ms": 10.0, "maxUpdateMs": 12.0}},
                                                          "export": {"readMs": 9.0, "commitMs": 1.0, "commitMaxUpdateMs": 0.5, "parts": 100, "thread": False}} for i in range(4)]})[0]
    check(ex_old["value"] == 30.0 and "before round 3 G" in ex_old["detail"] and ex_new["value"] == 12.0 and abs(ex_new["data"]["commitUsPerPart"] - 10.0) < 1e-9
          and abs(ex_new["data"]["readMsPerSecMean"] - 9.0) < 1e-9, "S10.EXPORT: the worst update, the read/commit split and the cost per part")
    tm = trim_rows({"s10.mem": [{"phase": "trim", "privateBeforeMB": 17600.0, "privateAfterMB": 11000.0, "gcHeapBeforeMB": 1500.0, "gcHeapAfterMB": 1200.0, "pauseMs": 900.0}]})[0]
    check(tm["value"] == 6600.0 and "pause 900 ms" in tm["detail"] and trim_rows({"s10.mem": []})[0]["value"] is None, "S10.MEM.TRIM: the private bytes the trim returned")
    hist = load_history(d)
    check(len(hist) == 1 and hist[0]["clipmaps"] == 20000 and hist[0]["toFirstUpdate"] == 41500, "the load history scan (one load, clipmaps 20 s)")
    import shutil
    shutil.rmtree(d, ignore_errors=True)
    ok = all(res)
    print("S10 ANALYZER SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv=None):
    import argparse
    ap = argparse.ArgumentParser(description="S10 part 1: perf scenes and the load-time breakdown (all INFO)")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--se-log", action="append", default=[])
    ap.add_argument("--sn-log", action="append", default=[])
    ap.add_argument("--se-game-log", action="append", default=[])
    ap.add_argument("--machine", default="desktop", choices=MACHINES)
    ap.add_argument("--spec", default="")
    ap.add_argument("--site", default=None, help="write the machine's block into this requirements.json")
    ap.add_argument("--load-history", action="store_true")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if a.load_history:
        hist = load_history(a.folder or LOG_DIR)
        print(json.dumps(hist, indent=1) if a.json else history_table(hist))
        return 0
    rows = evaluate(a.folder, a.se_log, a.sn_log, a.se_game_log, a.machine)
    if a.json:
        print(json.dumps(rows, indent=1, default=str))
    else:
        print(table(rows))
    if a.folder:
        with open(os.path.join(a.folder, "s10-results.json"), "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1, default=str)
        with open(os.path.join(a.folder, "s10-table.txt"), "w", encoding="utf-8") as f:
            f.write(table(rows) + "\n")
    if a.site:
        write_site(a.site, site_entry(rows, a.machine, a.spec))
        print("site: %s (%s)" % (a.site, a.machine))
    print("S10 TABLE %s (part 1: INFO baseline)" % ("COMPLETE" if verdict(rows, strict=True) else "WITH MISSING ROWS"))
    return 0

if __name__ == "__main__":
    sys.exit(main())
