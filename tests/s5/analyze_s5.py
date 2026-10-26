"""S5 "Whole grids + character" oracle table: joins the SE and Subnautica logs, the launcher's session log, the
measure.ps1 CSVs and the section samples of one collected session folder (tests/out/s5/<stamp>/ from `launcher.py s5`,
or tests/out/tp1/<stamp>/ from `launcher.py tp1`) into the rows of ROADMAP S5 (G1-E4, N1-N7, T-P1) and the seeded
failures S5.M1-M5. Stdlib only; S5.N1.C2 goes through tests/s3/check_placement.py, poses through
tools/worldmap/wire_ref.py (independent of WireMath).

 python -I tests/s5/analyze_s5.py DIR [--baseline DIR] [--baseline-logs DIR] [--lenient] [--json]
 python -I tests/s5/analyze_s5.py --selftest # synthetic sessions: every row PASS, then each of S5.M1-M5 red

S5 LOG CONTRACT (what the rows read). One JSON object per line; the event name in "ev" (SE plugin, stand-ins) or
"event" (SN plugin); the payload top-level, in "data", as a JSON object after the name in a "msg"/"text" line
(`s5.grid {...}`), or a "DE.S5 <name> key=value..." text line (vectors as (x, y, z) or [..]). Ids and keys: decimal,
0x-hex strings or numbers. Matrices: 12 floats, SE rows Right, Up, Backward, Translation (VRage row-vector order);
doubles for world matrices (round-trip precision: the SE origin is ~1.5e6 m away). SE side (se-*.jsonl or the stand-in's
se-standin.jsonl):
 s5.grid tick, entityId, rootId, parentId (0 = root), targetClass, flags, parts, verts, tris (model totals of the
 grid's parts), byClass, label ("spawned", "count",...) [N1.C1]
 s5.part tick, entityId (the owning grid), rootId, partKey, modelKey, cls, local[12] (grid-local), toRoot[12] (part
 frame -> root grid-local; or rel[12] = owning grid -> root, toRoot = local x rel), seMatrix[12] (the ROOT's
 world rows), pivot[3] (P*, SE world), k, site {C, Rs}, bounds[6] (model AABB, model space) [N1.C2]
 s5.pose tick, entityId (root), seMatrix, pivot, k, site: whenever the root's matrix or P* changes [N1.C2, N3]
 s5.edit tick (SE tick of the event), editId, edit (weld|stage|damage|paint|grind), entityId, partKeys[], records,
 partsAfter (SE's part count of the grid after the edit) [N2]
 s5.sub tick, kind (rotor|piston|door|subgrid|subpart), ownerId, partKey (subgrid id or subpart key), rootId,
 local[12] (the C2 row), toRoot[12], seMatrix, pivot, k, site, bounds[6] (child-local AABB) [N3]
 s5.model modelKey, path, verts, tris, loadMs | exception, glass, alphaMasked, materials, thread, label [N5, G1-E4]
 s5.coh tick, qpc, px[2] (SE pixel of the cockpit-fixed point), point [N4, info]
 s5.char tick, characterId, modelKey, flags, fallback, reason, charO[3], charQ[4], charS (the C1 wire pose),
 bones {pelvis, head, rpalm: 12 floats each, SE model-space absolute rows}, cameraMode [N7]
 s5.budget per second: c3Ms, c3Bytes, c4Ms, c4Bytes, queues, backPressure, c2Rows, c2Dropped, entities [N6 info]
 sample the plugin's 1 Hz perf record (frameTimeAvgMs, frameTimeMaxMs, gameplayTick) and SE's STATISTICS lines of
 SpaceEngineers_*.log (FrameTime) [T-P1]
Subnautica side (sn-*.jsonl or sn-standin.jsonl):
 s5.model modelKey, agree, total, frac (raw agreement before any flip), flipped, glass, alphaMasked [G1-E4]
 (also the S3 forms "DE.Mirror model key=... winding=99.9% flipped=" and s3.winding)
 s5.grid entityId, parts, tris, verts (model totals drawn), chunks, renderers, pending [N1.C1]
 (also "DE.Mirror built grid= gridParts= gridModelVerts= gridModelTris=")
 s5.placed entityId (owning grid), partKey, seTick (C2 tick of the pose applied), corners[8][3] (SN world positions
 of 8 drawn vertices: per corner direction of the part's model box the uploaded vertex whose model-space
 position is the most extreme, through its renderer's localToWorldMatrix), localCorners[8][3] (those
 vertices' model-space positions) [N1.C2]
 s5.count phase (before|after), roots, preserved, meshes (ours alive), renderers (after `de.s5 teardown`; also the S3
 form "DE.S3 COUNT rootsAlive= ourMeshesAlive=") [N1.C12]
 s5.edit entityId, partKey | partKeys, kind (add|remove|recolor), presentedSeTick (the SE tick presented in the frame
 where the change became visible), parts (the grid's part count after it) [N2]
 s5.sub seTick, alpha (1 = the alpha-1 evaluation), ownerId, partKey, kind, corners[8][3] (SN world of the
 child-local points), localCorners[8][3] (child-local) [N3]
 s5.coh frame, qpc, seTick, px[2] (Camera.WorldToScreenPoint of the cockpit-fixed point, top-left) [N4]
 s5.scale per second: chunks, renderers, drawEstimate, vertices, meshBytes, textureBytes, vramMb, frameMsP50,
 frameMsP99, uploadMs [N6]
 s5.char seTick, alpha, source (SeBones|Retarget|Bind), cameraMode (1 first person), entityPos[3], bones {pelvis,
 head, rpalm: [3] SN world}, head1stDrawn, bodyDrawn, swimming, nonFinite, followErrM [N7]
 s5.rig animator, isHuman, avatar, avatarHuman, bones, requiredMapped (of 15), names, clips [N7c]
 s5.perf per second: state (viewState/approach), recipe (Idle|Track|Full), camerasOn, fps, frameMsP50,
 frameMsP99, targetFps, vSync [T-P1]
Launcher session (s5-session.jsonl or tp1-session.jsonl): start {mode, parts, mutants, kind "s5"|"tp1", states},
part {name, phase begin|end, qpc, tick}, reply (every C11 command), sample (section reads at 10 Hz: c1Tick, c5, c7),
mem {commitTotal, commitLimit, physTotal, availPhys} at 1 Hz, measure {part, csv}, end. measure-<part>.csv: tools/
measure/measure.ps1 rows (cpu_pct, gpu3d_pct, ws_mb per process: Legacy = SE, Subnautica).

ROWS (required unless marked *): S5.G1-E4 (+ S5.G1-E4.census), S5.N1.C1, S5.N1.C2 (+.affine*), S5.N1.C11, S5.N1.C12,
S5.N2.<weld|stage|damage|paint|grind>, S5.N3.<rotor|piston|door>, S5.N4, S5.N5, S5.N6.<500|2000|5000>, S5.N7a, S5.N7b,
S5.N7c (+.T-P1.<base-se|base-sn|space|track-hover|track-sail|prime|underwater|ccx1*>, S5.Mx.red for the
mutants of the session. Rows of parts the session did not run are not listed (a tp1 folder has only T-P1 rows).
Exit code 0 when every required row passes (MISSING fails unless --lenient).
"""
from __future__ import annotations

import csv
import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
for _p in (HERE, os.path.join(ROOT, "tests", "s2"), os.path.join(ROOT, "tests", "s3"), os.path.join(ROOT, "tests", "wire"),
           os.path.join(ROOT, "tools", "worldmap"), os.path.join(ROOT, "tests", "standins")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C          # noqa: E402 (rows, table, verdict, key=value parser, jsonl reader)
import check_placement as CP   # noqa: E402 (S3.C2 machinery, reused for S5.N1.C2)
import wire_ref as W           # noqa: E402

G1E4_MODELS = 200
G1E4_MIN_AGREE = 0.99
CENSUS_INVERTED = ("roundcornertrianglerightplate", "insetconnectorconstruction_1", "grinderreskindisk2_lod3",
                   "cylinder_low", "cockpitglass")              # gap-1 §2.3 (block, tool and glass parts)
CENSUS_REQUIRED = ("roundcornertrianglerightplate", "insetconnectorconstruction_1")   # ROADMAP S5.G1-E4
N1_PARTS = 20
TOL_M = 1e-3                    # N1.C2, N3
N2_MAX_TICKS = 10               # HYPOTHESIS (ROADMAP)
N2_LEAD_TICKS = 2             # a change presented up to 2 ticks before SE's logged event tick still counts (see n2_rows)
N2_EDITS = ("weld", "stage", "damage", "paint", "grind")
N2_SN_KINDS = {"weld": ("add",), "stage": ("add",), "damage": ("remove", "add"), "paint": ("recolor",),
               "grind": ("remove",)}
N3_KINDS = ("rotor", "piston", "door")
N4_MAX_PX = 0.5
N4_SECONDS = 10.0
N5_MODELS = 200
N6_SIZES = ("500", "2000", "5000")
N6_P99_MS = 16.7                # HYPOTHESIS budget (ARCHITECTURE 5.13)
N7_TOL_M = 0.01
N7C_SHARE = 0.95
REQUIRED_HUMAN = 15
TP1_STATES = ("base-se", "base-sn", "space", "track-hover", "track-sail", "prime", "underwater", "ccx1")
TP1_SIM_MIN = 0.98
TP1_FRAME_TOL = 0.05
TP1_SN_P99_MS = 16.7
TP1_COMMIT_MAX = 0.85
TP1_HITCH_MS = 50.0
TP1_IDLE_FPS_MAX = 11.0
TP1_TRACK_GPU_MAX = 5.0
TP1_SHARE = 0.99
MUTANT_ROWS = {"S5.M1": ("S5.N4",), "S5.M2": ("S5.G1-E4",), "S5.M3": ("S5.N1.C1",), "S5.M4": ("S5.T-P1.space",),
               "S5.M5": ("S5.T-P1.track-hover", "S5.T-P1.track-sail")}
_EMBED = re.compile(r"\b(s5\.[A-Za-z0-9_]+)\s+(\{.*\})\s*$")
TEXT_RULES = (("DE.S5 grid", "s5.grid"), ("DE.S5 part", "s5.part"), ("DE.S5 pose", "s5.pose"),
              ("DE.S5 edit", "s5.edit"), ("DE.S5 sub", "s5.sub"), ("DE.S5 model", "s5.model"),
              ("DE.S5 coh", "s5.coh"), ("DE.S5 char", "s5.char"), ("DE.S5 budget", "s5.budget"),
              ("DE.S5 placed", "s5.placed"), ("DE.S5 count", "s5.count"), ("DE.S5 scale", "s5.scale"),
              ("DE.S5 rig", "s5.rig"), ("DE.S5 perf", "s5.perf"), ("DE.S5 teardown", "s5.teardown"),
              # the S3 forms the Subnautica plugin keeps for every model and grid
              ("DE.Mirror model", "s5.model"), ("DE.Mirror winding", "s5.model"), ("DE.Mirror built", "s5.grid"),
              ("DE.S3 COUNT", "s5.count"), ("DE.S3 TEARDOWN", "s5.teardown"))

# ----------------------------------------------------------------------------------------------------------------
# Parsing
# ----------------------------------------------------------------------------------------------------------------

def gid(v):
    """An id or key (int, float, decimal or 0x-hex string) -> int, else None."""
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, int):
        return v & 0xFFFFFFFFFFFFFFFF
    if isinstance(v, float):
        return int(v)
    s = str(v).strip()
    try:
        return int(s, 16) if s.lower().startswith("0x") else int(s)
    except ValueError:
        return None

def num(d, *names):
    for k in names:
        v = d.get(k) if isinstance(d, dict) else None
        if isinstance(v, bool):
            continue
        if isinstance(v, (int, float)):
            return float(v)
        if isinstance(v, str):
            t = v.strip().rstrip("%")
            try:
                return float(t) / (100.0 if v.strip().endswith("%") else 1.0)
            except ValueError:
                continue
    return None

def flag(d, *names):
    return C.flag(d, *names)

def vecn(v, n):
    if isinstance(v, dict):
        v = [v.get(c) for c in "xyzw"[:n]]
    if isinstance(v, str):
        v = [x for x in re.split(r"[,; ()\[\]]+", v) if x]
    if not isinstance(v, (list, tuple)) or len(v) != n:
        return None
    try:
        return tuple(float(x) for x in v)
    except (TypeError, ValueError):
        return None

def mat12(v):
    m = CP._matrix(v)
    return m

def triples8(v):
    if isinstance(v, str):
        pts = [vecn(p, 3) for p in v.split(";") if p.strip()]
    elif isinstance(v, (list, tuple)):
        pts = [vecn(p, 3) for p in v]
    else:
        return None
    if len(pts) < 1 or any(p is None for p in pts):
        return None
    return pts

def _adapt(name, data, text):
    """The plugins' S3-form lines onto the S5 field names (both stay accepted)."""
    t = text or ""
    if t.startswith("DE.Mirror model") or t.startswith("DE.Mirror winding") or name == "s3.winding":
        w = data.get("winding")
        if data.get("frac") is None and w is not None:
            f = num({"w": w}, "w")
            if f is not None:
                data["frac"] = f if isinstance(w, str) and w.strip().endswith("%") else (f / 100.0 if f > 1 else f)
        data.setdefault("modelKey", data.get("key", data.get("model")))
        data["_s3form"] = True
    if t.startswith("DE.Mirror built"):
        data.setdefault("entityId", data.get("grid"))
        for a, b in (("parts", "gridParts"), ("tris", "gridModelTris"), ("verts", "gridModelVerts")):
            if data.get(b) is not None:
                data[a] = data[b]
    if t.startswith("DE.S3 COUNT"):
        data.setdefault("roots", data.get("rootsAlive"))
        data.setdefault("meshes", data.get("ourMeshesAlive"))
        data.setdefault("phase", "after")
    return data

_BONE_ALIASES = {"pelvis": ("pelvis", "SE_RigPelvis"), "head": ("head", "SE_RigHead"),
                 "rpalm": ("rpalm", "SE_RigRPalm", "rightPalm")}
_UNIT_POINTS = [(-1.0 if i & 1 == 0 else 1.0, -1.0 if i & 2 == 0 else 1.0, -1.0 if i & 4 == 0 else 1.0) for i in range(8)]

def _first(d, *names):
    for n in names:
        v = d.get(n)
        if v is not None:
            return v
    return None

def _flat12(v):
    m = mat12(v)
    return [x for r in m for x in r] if m is not None else None

def _unity_points(cols12, pts):
    """Unity world positions of SE-local points through a Unity localToWorld matrix logged as 4 columns of 3 (X axis,
 Y axis, Z axis, translation): the SE point l is the Unity-local point Mirror(l) = (x, y, -z)."""
    c = cols12
    out = []
    for lx, ly, lz in pts:
        mz = -lz
        out.append(tuple(c[0 + r] * lx + c[3 + r] * ly + c[6 + r] * mz + c[9 + r] for r in range(3)))
    return out

def normalize(name, data, side):
    """The plugins' own field names onto the names the rows read;
 both stay accepted. Ids: the decimal string form wins over a JSON number (doubles lose ids above 2^53)."""
    d = data
    ent = _first(d, "entityIdStr", "entityId", "entity", "grid", "characterIdStr", "characterId", "character")
    if ent is not None:
        d["entityId"] = ent
    if d.get("partKey") is None and _first(d, "part", "key") is not None and name != "s5.model":
        d["partKey"] = _first(d, "part", "key")
    if _first(d, "ownerIdStr", "owner") is not None and (d.get("ownerId") is None or d.get("ownerIdStr") is not None):
        d["ownerId"] = _first(d, "ownerIdStr", "owner")
    if d.get("rootIdStr") is not None:
        d["rootId"] = d["rootIdStr"]
    if d.get("path") is None and d.get("asset") is not None:
        d["path"] = d["asset"]
    if name == "s5.model":
        if d.get("exception") is None and d.get("error") is not None:
            d["exception"] = d["error"]
        if d.get("modelKey") is None and _first(d, "model", "key") is not None:
            d["modelKey"] = _first(d, "model", "key")
        if side == "sn" and d.get("frac") is None and num(d, "winding") is not None and not d.get("_s3form"):
            w = num(d, "winding")
            d["frac"] = w if w <= 1.0 else w / 100.0
    elif name == "s5.grid":
        if d.get("tris") is None and d.get("modelTris") is not None:
            d["tris"] = d["modelTris"]
        if d.get("verts") is None and d.get("modelVerts") is not None:
            d["verts"] = d["modelVerts"]
        if d.get("parts") is None and d.get("heldParts") is not None:
            d["parts"] = d["heldParts"]
    elif name == "s5.edit":
        if d.get("partKeys") is None and isinstance(d.get("keys"), list):
            d["partKeys"] = d["keys"]
        if side == "se" and d.get("partsAfter") is None and d.get("parts") is not None:
            d["partsAfter"] = d["parts"]
        if side == "se" and d.get("eventTick") is not None:
            d["recordTick"] = d.get("tick")
            d["tick"] = d["eventTick"]
    elif name == "s5.part" and side == "se":
        if d.get("seMatrix") is None and d.get("rootWorld") is not None:
            d["seMatrix"] = _flat12(d["rootWorld"])
        if d.get("toRoot") is None:
            loc, rel = mat12(d.get("local")), mat12(d.get("rel"))
            if loc is not None:
                d["toRoot"] = [x for r in (W_mm(loc, rel) if rel is not None else loc) for x in r]
    elif name == "s5.placed" and side == "sn":
        dr = vecn(d.get("drawn"), 3)
        if d.get("corners") is None and dr is not None:      # one drawn point: the part origin
            d["corners"] = [list(dr)] * 8
            d["localCorners"] = [[0.0, 0.0, 0.0]] * 8
        if d.get("seTick") is None and d.get("tick") is not None:
            d["seTick"] = d["tick"]
    elif name == "s5.sub":
        if side == "se":
            if d.get("toRoot") is None and _first(d, "localD", "local") is not None:
                d["_c2row"] = _first(d, "localD", "local")
            if d.get("seMatrix") is None and d.get("ownerWorld") is not None:
                d["_ownerWorld"] = _flat12(d["ownerWorld"])
        else:
            if d.get("seTick") is None and d.get("tick") is not None:
                d["seTick"] = d["tick"]
            uw = d.get("unityWorld")
            if d.get("corners") is None and isinstance(uw, list) and len(uw) == 12:
                d["corners"] = [list(p) for p in _unity_points([float(x) for x in uw], _UNIT_POINTS)]
                d["localCorners"] = [list(p) for p in _UNIT_POINTS]
    elif name == "s5.coh" and side == "sn":
        if not isinstance(d.get("px"), list) and num(d, "px") is not None and num(d, "py") is not None:
            d["px"] = [num(d, "px"), num(d, "py")]
        if d.get("seTick") is None and d.get("presentedSeTick") is not None:
            d["seTick"] = d["presentedSeTick"]
    elif name in ("s5.scale", "s5.perf"):
        for a, b in (("frameMsP99", "frameP99Ms"), ("frameMsP50", "frameP50Ms")):
            if d.get(a) is None and d.get(b) is not None:
                d[a] = d[b]
        if d.get("camerasOn") is None and d.get("mainCameraEnabled") is not None:
            d["camerasOn"] = d["mainCameraEnabled"]
    elif name == "s5.rig":
        pass    # review F2: snBoneMapped (path G) is not the required-bone count; s5.char carries requiredMapped
    elif name == "s5.budget":
        c2 = d.get("c2") if isinstance(d.get("c2"), dict) else {}
        if d.get("c2Dropped") is None and c2.get("c2Dropped") is not None:
            d["c2Dropped"] = c2["c2Dropped"]
    elif name == "s5.char":
        bones = d.get("bones")
        if isinstance(bones, list):               # Subnautica: [{name, snWorld, seAbs}]
            sn_b, se_b = {}, {}
            for b in bones:
                if not isinstance(b, dict):
                    continue
                key = next((k for k, al in _BONE_ALIASES.items() if b.get("name") in al), None)
                if key:
                    if b.get("snWorld") is not None:
                        sn_b[key] = b["snWorld"]
                    if b.get("seAbs") is not None:
                        se_b[key] = b["seAbs"]
            d["bones"] = sn_b
            d["seBones"] = se_b
        elif side == "se" and not isinstance(bones, dict):   # SE: the bone arrays at the top level, by SE name
            got = {k: _first(d, *al) for k, al in _BONE_ALIASES.items()}
            if any(v is not None for v in got.values()):
                d["bones"] = {k: v for k, v in got.items() if v is not None}
        if d.get("entityPos") is None and d.get("entityWorld") is not None:
            d["entityPos"] = d["entityWorld"]
        for a, b in (("charO", "c1O"), ("charQ", "c1Q"), ("charS", "c1S")):
            if d.get(a) is None and d.get(b) is not None:
                d[a] = d[b]
        if d.get("seTick") is None and d.get("tick") is not None and side == "sn":
            d["seTick"] = d["tick"]
        parts = d.get("parts")
        if d.get("head1stDrawn") is None and isinstance(parts, list):
            hidden = [p for p in parts if isinstance(p, dict) and p.get("hiddenInFirstPerson")]
            if hidden:
                d["head1stDrawn"] = any(bool(p.get("drawn")) for p in hidden)
        if side == "sn" and isinstance(d.get("swimming"), (int, float)) and not isinstance(d.get("swimming"), bool):
            d["swimFrames"] = d["swimming"]          # per-second aggregate: frames swimming, of them on Retarget
            d["swimRetargetFrames"] = d.get("swimmingOnRetarget", 0)
            d["swimming"] = d["swimming"] > 0
    return d

# S7a (CONTRACT §5.4 "S7a conventions", "Placement ghost"): the placement ghost's synthetic entity id ("DEG" 1). It is
# never an S5 entity (neither plugin writes s5.* rows for it); rows naming it are skipped as a safety net.
S7A_GHOST_ENTITY_ID = 0x0144454700000001

def parse_record(rec: dict, side: str):
    """One JSONL record -> (event name, data) for the S5 events, else None."""
    name = rec.get("ev") or rec.get("event")
    data = None
    if isinstance(name, str) and (name.startswith("s5.") or name in ("sample", "s3.winding", "s3.mirror")):
        data = dict(rec["data"]) if isinstance(rec.get("data"), dict) else \
            {k: v for k, v in rec.items() if k not in ("ev", "event", "data")}
        if name == "s3.mirror":
            name = "s5.grid"
        elif name == "s3.winding":
            name = "s5.model"
    text = rec.get("msg") if isinstance(rec.get("msg"), str) else rec.get("text")
    if isinstance(text, str):
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
        if text.startswith("DE."):
            for prefix, ev in sorted(TEXT_RULES, key=lambda r: -len(r[0])):
                if text.startswith(prefix + " ") or text == prefix:
                    kv = C._kv(text[len(prefix):])
                    if data is None or not (isinstance(name, str) and name.startswith("s5.")):
                        name, data = ev, dict(kv)
                    else:
                        for k, v in kv.items():
                            data.setdefault(k, v)
                    break
    if data is None or not isinstance(name, str):
        return None
    if not (name.startswith("s5.") or name == "sample"):
        return None
    data = _adapt(name, data, text if isinstance(text, str) else "")
    data = normalize(name, data, side)
    if gid(data.get("entityId")) == S7A_GHOST_ENTITY_ID:  # S7a: the placement ghost is not an S5 entity
        return None
    data["_side"] = side
    data["_t"] = rec.get("t", rec.get("utc"))
    data["_level"] = rec.get("level")
    return name, data

class Logs:
    """A collected S5 or T-P1 session folder."""

    def __init__(self, folder: str):
        self.folder = folder
        self.se_files = sorted(set(glob.glob(os.path.join(folder, "se-*.jsonl"))))
        self.sn_files = sorted(set(glob.glob(os.path.join(folder, "sn-*.jsonl"))))
        self.se_raw = C.read_jsonl(self.se_files)
        self.sn_raw = C.read_jsonl(self.sn_files)
        self.se = [e for e in (parse_record(r, "se") for r in self.se_raw) if e]
        self.sn = [e for e in (parse_record(r, "sn") for r in self.sn_raw) if e]
        sp = [p for p in (os.path.join(folder, "s5-session.jsonl"), os.path.join(folder, "tp1-session.jsonl"))
              if os.path.exists(p)]
        self.session = C.read_jsonl(sp)
        st = self.start()
        self.qpcf = int(st.get("qpcFrequency") or C.qpc_frequency(self.session, default=10_000_000))
        self.utc_offset = self._utc_offset()
        for _n, d in self.se + self.sn:
            d["_qpc"] = self.qpc_of(d)
        self.samples = sorted((r for r in self.session if r.get("ev") == "sample" and r.get("qpc")),
                              key=lambda r: r["qpc"])
        self.stats = self._statistics()

    def start(self):
        return next((r for r in self.session if r.get("ev") == "start"), {})

    def se_ev(self, name):
        return [d for n, d in self.se if n == name]

    def sn_ev(self, name):
        return [d for n, d in self.sn if n == name]

    def sess(self, name):
        return [r for r in self.session if r.get("ev") == name]

    # ---- time ------------------------------------------------------------------------------------------------------
    def _utc_offset(self):
        d = []
        for r in self.session:
            u = C.Session._utc_seconds(r.get("utc"))
            if u is not None and isinstance(r.get("qpc"), int):
                d.append(r["qpc"] - u * self.qpcf)
        return sorted(d)[len(d) // 2] if d else None

    def qpc_of(self, d):
        q = d.get("qpc")
        if isinstance(q, int) and not isinstance(q, bool):
            return q
        t = d.get("_t")
        if isinstance(t, (int, float)) and not isinstance(t, bool):
            return int(float(t) * self.qpcf)          # the stand-ins' "t" = QPC seconds
        if isinstance(t, str) and self.utc_offset is not None:
            s = C.Session._utc_seconds(t)
            if s is not None:
                return int(s * self.qpcf + self.utc_offset)
        return None

    def window(self, part):
        b = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == part and
                  r.get("phase") == "begin"), None)
        e = next((r for r in self.session if r.get("ev") == "part" and r.get("name") == part and
                  r.get("phase") == "end"), None)
        if b is None:
            return None
        return {"q0": b.get("qpc"), "q1": (e or {}).get("qpc") or (1 << 62), "t0": b.get("tick"),
                "t1": (e or {}).get("tick")}

    def in_window(self, rows, part, tick_keys=("tick", "seTick", "presentedSeTick")):
        w = self.window(part)
        if w is None:
            return []
        out = []
        for d in rows:
            q = d.get("_qpc")
            if q is not None and w["q0"] is not None:
                if w["q0"] <= q <= w["q1"]:
                    out.append(d)
                continue
            t = next((num(d, k) for k in tick_keys if num(d, k) is not None), None)
            if t is not None and w["t0"] is not None and w["t0"] <= t <= (w["t1"] if w["t1"] is not None else 1e18):
                out.append(d)
        return out

    def parts_run(self):
        return [r.get("name") for r in self.session if r.get("ev") == "part" and r.get("phase") == "begin"]

    def _statistics(self):
        """SE's own 60 s STATISTICS lines (FrameTime, FrameTimeGPU,...) of SpaceEngineers_*.log with their QPC."""
        import datetime
        out = []
        for path in sorted(glob.glob(os.path.join(self.folder, "SpaceEngineers_*.log"))):
            legend = None
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    i = line.find("STATISTICS")
                    if i < 0:
                        continue
                    body = line[i:].strip()
                    if body.startswith("STATISTICS LEGEND,"):
                        legend = [x.strip() for x in body[len("STATISTICS LEGEND,"):].split(",")]
                        continue
                    if not body.startswith("STATISTICS,") or not legend:
                        continue
                    rec = {}
                    for k, v in zip(legend, body[len("STATISTICS,"):].split(",")):
                        try:
                            rec[k] = float(v)
                        except ValueError:
                            rec[k] = v
                    try:
                        t = datetime.datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f")
                        utc = datetime.datetime.fromtimestamp(t.timestamp(), datetime.timezone.utc)
                        s = C.Session._utc_seconds(utc.strftime("%Y-%m-%dT%H:%M:%S.%f"))
                        rec["_qpc"] = int(s * self.qpcf + self.utc_offset) if self.utc_offset is not None else None
                    except ValueError:
                        rec["_qpc"] = None
                    out.append(rec)
        return out

def row(rows, cid, status, measured, threshold, what, part="", required=True, **detail):
    return C.row(rows, cid, status, measured, threshold, what, part, required, **detail)

def basename_of(path):
    b = re.split(r"[\\/]", str(path or ""))[-1].lower()
    return b[:-4] if b.endswith(".mwm") else b

def census_name(path):
    b = basename_of(path)
    b2 = re.sub(r"_lod\d+$", "", b)
    for c in CENSUS_INVERTED:
        if b == c or b2 == c or b.startswith(c):
            return c
    return None

# ----------------------------------------------------------------------------------------------------------------
# S5.G1-E4: winding over the first 200 distinct real models
# ----------------------------------------------------------------------------------------------------------------

def g1e4_rows(L, rows):
    what = "C3 winding over the first %d distinct real models: > 99 %% per model (census-listed inverted models " \
           "flipped and logged; glass and alpha-masked parts excluded)" % G1E4_MODELS
    se_models = {}
    for d in L.se_ev("s5.model"):
        k = gid(d.get("modelKey", d.get("key")))
        if k is not None:
            se_models.setdefault(k, d)
    order, seen = [], {}
    for d in L.sn_ev("s5.model"):
        k = gid(d.get("modelKey", d.get("key", d.get("model"))))
        frac = num(d, "frac")
        if frac is None and num(d, "agree") is not None and num(d, "total"):
            frac = num(d, "agree") / num(d, "total")
        if k is None or frac is None:
            continue
        if k not in seen:
            seen[k] = d
            order.append(k)
    if not order:
        row(rows, "S5.G1-E4", "MISSING", "no Subnautica winding lines (s5.model)", "> 99 % per model", what, "g1e4")
        return
    first = order[:G1E4_MODELS]
    fails, flipped, excluded, census_seen = [], [], [], set()
    worst = None
    for k in first:
        d = seen[k]
        se = se_models.get(k, {})
        path = se.get("path") or d.get("path") or ""
        cname = census_name(path)
        if cname:
            census_seen.add(cname)
        raw = num(d, "frac")
        if raw is None:
            raw = num(d, "agree") / num(d, "total")
        flip = bool(flag(d, "flipped"))
        eff = (1.0 - raw) if flip else raw
        glass = bool(flag(d, "glass") or flag(se, "glass"))
        alpha = bool(flag(d, "alphaMasked") or flag(se, "alphaMasked"))
        if flip:
            flipped.append(basename_of(path) or "0x%016x" % k)
        if glass or alpha:
            excluded.append(basename_of(path) or "0x%016x" % k)
            continue
        if worst is None or eff < worst[0]:
            worst = (eff, basename_of(path) or "0x%016x" % k)
        if eff <= G1E4_MIN_AGREE:
            why = "flip skipped" if (raw < 0.01 and not flip) else ("census-listed" if cname else "")
            fails.append("%s %.2f%%%s" % (basename_of(path) or "0x%016x" % k, eff * 100.0, (" (" + why + ")") if why
                                           else ""))
    n = len(first)
    if fails:
        status = "FAIL"
    elif n < G1E4_MODELS:
        status = "MISSING"
    else:
        status = "PASS"
    row(rows, "S5.G1-E4", status, "%d/%d models > 99 %%%s" % (n - len(fails) - len(excluded), n - len(excluded),
                                                             "; only %d models" % n if n < G1E4_MODELS else ""),
        "> 99 %% per model (%d models)" % G1E4_MODELS, what, "g1e4", fails=fails[:12], flipped=flipped[:12],
        excluded=excluded[:12], worst=worst, unnamed=sum(1 for k in first if k not in se_models))
    miss = [c for c in CENSUS_REQUIRED if c not in census_seen]
    row(rows, "S5.G1-E4.census", "PASS" if not miss else "MISSING",
        "census models in the set: %s" % (",".join(sorted(census_seen)) or "none"),
        "RoundCornerTriangleRightPlate + InsetConnectorConstruction_1",
        "the set holds the census-listed inverted models, so S5.M2 (flip skipped) can turn S5.G1-E4 red (names from "
        "SE's s5.model rows)", "g1e4", missing=miss)

# ----------------------------------------------------------------------------------------------------------------
# S5.N1: counts per grid, 20 random parts, no harm, teardown
# ----------------------------------------------------------------------------------------------------------------

def latest_by_entity(rows_):
    out = {}
    for d in rows_:
        g = gid(d.get("entityId", d.get("grid", d.get("entity"))))
        if g is not None:
            out[g] = d
    return out

def n1c1_rows(L, rows):
    what = "per grid at scale: Subnautica's part and model-triangle counts equal SE's (latest counts at the end of n1)"
    # the counts at the end of the n1 part (a later resend, e.g. a recording's restart, heals what S5.M3 broke), else
 # the session's latest
    w = L.window("n1")
    q_end = w["q1"] if w and w["q1"] != (1 << 62) else None

    def before_end(d):
        return q_end is None or d.get("_qpc") is None or d["_qpc"] <= q_end
    se = {}
    for n, d in L.se:     # SE's count: s5.grid rows with parts, or the held count after an edit (s5.edit parts)
        g = gid(d.get("entityId"))
        if g is None or not before_end(d):
            continue
        if n == "s5.grid" and num(d, "parts") is not None:
            se[g] = {"parts": num(d, "parts"), "tris": num(d, "tris")}
        elif n == "s5.edit" and num(d, "partsAfter") is not None:
            se[g] = {"parts": num(d, "partsAfter"), "tris": num(d, "trisAfter")}
    sn = {}
    for d in L.sn_ev("s5.grid"):
        g = gid(d.get("entityId"))
        if g is not None and num(d, "parts") is not None and before_end(d):
            sn[g] = d
    if not se:
        row(rows, "S5.N1.C1", "MISSING", "no SE per-grid count (s5.grid parts / s5.edit parts)", "equal per grid", what,
            "n1")
        return
    if not sn:
        row(rows, "S5.N1.C1", "MISSING", "no Subnautica s5.grid / DE.Mirror built", "equal per grid", what, "n1")
        return
    bad, ok = [], 0
    for g, d in sorted(se.items()):
        s_ = sn.get(g)
        sp, st = d["parts"], d["tris"]
        if s_ is None:
            bad.append("0x%x absent in Subnautica (SE %s parts)" % (g, sp))
            continue
        np_, nt = num(s_, "parts"), num(s_, "tris")
        if sp != np_ or (st is not None and nt is not None and st != nt):
            bad.append("0x%x parts %s/%s tris %s/%s" % (g, np_, sp, nt, st))
        else:
            ok += 1
    row(rows, "S5.N1.C1", "PASS" if not bad else "FAIL", "%d/%d grids equal" % (ok, len(se)), "equal per grid", what,
        "n1", mismatches=bad[:10])

def n1c2_records(L):
    """check_placement records (s3.pose / s3.placement) per part: SE corners in the ROOT's grid-local frame."""
    recs = []
    se_parts = {}
    for d in L.se_ev("s5.part"):
        k = (gid(d.get("entityId")), gid(d.get("partKey")))
        if None not in k:
            se_parts[k] = d
    poses = {}
    for d in L.se_ev("s5.pose"):
        r = gid(d.get("entityId"))
        if r is not None and mat12(d.get("seMatrix")) is not None:
            poses.setdefault(r, []).append(d)
    for (g, k), d in se_parts.items():
        ent = "%x:%x" % (g, k)
        to_root = mat12(d.get("toRoot"))
        if to_root is None:
            loc, rel = mat12(d.get("local")), mat12(d.get("rel"))
            to_root = loc if rel is None else (W_mm(loc, rel) if loc is not None else None)
        if to_root is None:
            continue
        b = d.get("bounds")
        se_local = None
        if isinstance(b, (list, tuple)) and len(b) == 6:
            se_local = [W_pt(to_root, c) for c in W.aabb_corners(tuple(b[:3]), tuple(b[3:]))]
        root = gid(d.get("rootId"))
        base = {"entityId": ent, "k": d.get("k", 1.0), "site": d.get("site")}
        recs.append(dict(base, ev="s3.pose", tick=d.get("tick"), seMatrix=d.get("seMatrix"), pivot=d.get("pivot"),
                         **({"localCorners": [list(p) for p in se_local]} if se_local else {}), wire=d.get("wire")))
        for p in poses.get(root, []):
            recs.append(dict(base, ev="s3.pose", tick=p.get("tick"), seMatrix=p.get("seMatrix"), pivot=p.get("pivot"),
                             k=p.get("k", 1.0), site=p.get("site") or d.get("site"),
                             **({"localCorners": [list(q) for q in se_local]} if se_local else {})))
        d["_toRoot"] = to_root
    matched = 0
    for s in L.sn_ev("s5.placed"):
        key = (gid(s.get("entityId", s.get("entity"))), gid(s.get("partKey")))
        d = se_parts.get(key)
        corners = triples8(s.get("corners", s.get("cornersSn")))
        if d is None or "_toRoot" not in d or not corners or len(corners) != 8:
            continue
        lc = triples8(s.get("localCorners", s.get("seLocalCorners")))
        rec = {"ev": "s3.placement", "entityId": "%x:%x" % key, "seTick": s.get("seTick", s.get("tick")),
               "corners": [list(c) for c in corners]}
        if lc and len(lc) == 8:
            rec["localCorners"] = [list(W_pt(d["_toRoot"], c)) for c in lc]
        recs.append(rec)
        matched += 1
    return recs, len(se_parts), matched

def W_pt(m, p):
    return tuple(p[0] * m[0][j] + p[1] * m[1][j] + p[2] * m[2][j] + m[3][j] for j in range(3))

def W_mm(a, b):
    rot = tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))
    t = tuple(sum(a[3][k] * b[k][j] for k in range(3)) + b[3][j] for j in range(3))
    return rot + (t,)

def n1c2_rows(L, rows):
    what = "%d random parts drawn within 1 mm of Apply(o, q, s) recomputed from SE's logged matrices and P* " \
           "(tests/s3/check_placement.py, wire_ref.wire_pose)" % N1_PARTS
    recs, n_se, n_sn = n1c2_records(L)
    if not n_se:
        row(rows, "S5.N1.C2", "MISSING", "no SE s5.part rows", "<= 1 mm, %d parts" % N1_PARTS, what, "n1")
        return
    if not n_sn:
        row(rows, "S5.N1.C2", "MISSING", "no Subnautica s5.placed rows for SE's parts (%d SE parts)" % n_se,
            "<= 1 mm, %d parts" % N1_PARTS, what, "n1")
        return
    cp = CP.analyze(recs, tol_m=TOL_M)
    c2 = next((r for r in cp if r["id"] == "S3.C2"), None)
    aff = next((r for r in cp if r["id"] == "S3.C2.affine"), None)
    det = (c2 or {}).get("detail") or {}
    m = det.get("matched", 0)
    ents = len({r["entityId"] for r in recs if r["ev"] == "s3.placement"})
    status = c2["status"] if c2 else "MISSING"
    if status == "PASS" and ents < min(N1_PARTS, n_se):
        status = "MISSING"
    row(rows, "S5.N1.C2", status, "%s over %d parts (%d records)" % (c2["measured"] if c2 else "-", ents, m),
        "<= 1 mm, %d parts" % N1_PARTS, what, "n1", **det)
    if aff:
        row(rows, "S5.N1.C2.affine", aff["status"], aff["measured"], aff["threshold"], aff["what"], "n1",
            required=False)

def n1c11_rows(L, rows, baseline_dir=None):
    what = "no harm: no new error lines in LogOutput.log / Player.log and none of ours in either plugin's JSONL"
    files = [p for p in (os.path.join(L.folder, "BepInEx-LogOutput.log"), os.path.join(L.folder, "Player.log"))
             if os.path.exists(p)]
    rx = re.compile(r"\[(Error|Fatal)\b|Exception\b|^\s*at DeepEngineers|NullReference")
    ours_rx = re.compile(r"DeepEngineers|\bDE\.[A-Z]")
    sig = lambda s: re.sub(r"[0-9a-fx]{4,}|\d+", "#", s.strip())[:160]  # noqa: E731
    base = set()
    if baseline_dir:
        for n in ("BepInEx-LogOutput.log", "Player.log"):
            p = os.path.join(baseline_dir, n)
            if os.path.exists(p):
                with open(p, encoding="utf-8", errors="replace") as f:
                    base |= {sig(x) for x in f if rx.search(x)}
    ours, new = [], []
    for p in files:
        with open(p, encoding="utf-8", errors="replace") as f:
            for line in f:
                if rx.search(line):
                    if ours_rx.search(line):
                        ours.append(line.strip()[:160])
                    elif baseline_dir and sig(line) not in base:
                        new.append(line.strip()[:160])
    for r in L.sn_raw + L.se_raw:
        lev = str(r.get("level", "")).lower()
        if lev in ("error", "fatal"):
            ours.append(str(r.get("text", r.get("msg", "")))[:160])
    if not files and not L.sn_raw:
        row(rows, "S5.N1.C11", "MISSING", "no game logs collected", "0 new", what, "n1")
        return
    row(rows, "S5.N1.C11", "PASS" if not ours and not new else "FAIL", "ours %d, new %d%s" % (
        len(ours), len(new), "" if baseline_dir else " (no baseline: only ours judged)"), "0", what, "n1",
        ours=ours[:8], new=new[:8], files=[os.path.basename(f) for f in files])

def n1c12_rows(L, rows):
    what = "clean teardown: after de.s5 teardown our mirror roots, preserved roots and meshes are gone"
    seq = [(n, d) for n, d in L.sn if n in ("s5.count", "s5.teardown")]
    last_td = max((i for i, (n, d) in enumerate(seq) if n == "s5.teardown"), default=None)
    after = [d for i, (n, d) in enumerate(seq) if n == "s5.count" and (last_td is None or i > last_td) and
             str(d.get("phase", "after")) == "after"]
    if not after:
        td = [d for n, d in seq if n == "s5.teardown" and str(d.get("phase", "")) == "after" and
              num(d, "roots") is not None]
        after = td
    if not after:
        row(rows, "S5.N1.C12", "MISSING", "no Subnautica s5.count after a teardown", "roots 0, meshes 0", what,
            "teardown")
        return
    d = after[-1]
    roots, meshes, pres = num(d, "roots"), num(d, "meshes", "ourMeshes"), num(d, "preserved")
    ok = roots == 0 and meshes == 0 and not pres
    row(rows, "S5.N1.C12", "PASS" if ok else "FAIL", "roots %s, preserved %s, meshes %s" % (roots, pres, meshes),
        "all 0", what, "teardown", renderers=num(d, "renderers"))

# ----------------------------------------------------------------------------------------------------------------
# S5.N2: live edits
# ----------------------------------------------------------------------------------------------------------------

def window_kind(L, d, prefix, kinds):
    """The launcher's sub-window (part <prefix><kind>) containing a row, by QPC, else by SE tick; or None."""
    q = d.get("_qpc")
    t = num(d, "tick", "seTick", "presentedSeTick")
    for k in kinds:
        w = L.window(prefix + k)
        if w is None:
            continue
        if q is not None and w["q0"] is not None and w["q0"] <= q <= w["q1"]:
            return k
        if q is None and t is not None and w["t0"] is not None and w["t0"] <= t <= (w["t1"] if w["t1"] is not None
                                                                                   else 1e18):
            return k
    return None

def n2_rows(L, rows):
    what = "live edit visible in Subnautica within %d SE ticks of SE's event (HYPOTHESIS); part counts equal after" % \
        N2_MAX_TICKS
    edits = L.se_ev("s5.edit")
    sn = L.sn_ev("s5.edit")
    sn_grid = L.sn_ev("s5.grid")
    if not edits:
        return
    by_kind = {}
    for e in edits:
        k = str(e.get("edit", e.get("kind", "?")))
        if k not in N2_EDITS:          # the SE plugin names the source (cell, block,...): the launcher's window decides
            k = window_kind(L, e, "n2.", N2_EDITS) or k
        by_kind.setdefault(k, []).append(e)
    for kind in N2_EDITS:
        es = by_kind.get(kind)
        if not es:
            continue
        if not sn:
            row(rows, "S5.N2.%s" % kind, "MISSING", "no Subnautica s5.edit rows", "<= %d ticks; counts equal" %
                N2_MAX_TICKS, what, "n2")
            continue
        lat, problems = [], []
        for e in es:
            t0 = num(e, "tick")
            ent = gid(e.get("entityId"))
            keys = {gid(k) for k in (e.get("partKeys") or [])} - {None}
            want = set(N2_SN_KINDS.get(kind, ()))
            vis = {}
            for s in sn:
                if gid(s.get("entityId", s.get("entity"))) != ent:
                    continue
                pt = num(s, "presentedSeTick", "seTick")
                # SE logs the edit row the tick after it enqueued the records, so Subnautica may present the change one
 # or two ticks before t0
                if pt is None or t0 is None or pt < t0 - N2_LEAD_TICKS:
                    continue
                sk = {gid(k) for k in (s.get("partKeys") or [])} | {gid(s.get("partKey"))}
                hit = (sk & keys) if keys else sk
                if not hit:
                    continue
                kd = str(s.get("kind", ""))
                if want and kd and kd not in want:
                    continue
                for k in hit:
                    vis[k] = min(vis.get(k, pt), pt)
            if keys and set(vis) < keys:
                problems.append("#%s: %d/%d keys never visible" % (e.get("editId", e.get("id")), len(set(vis) & keys), len(keys)))
                continue
            if not vis:
                problems.append("#%s: no Subnautica change" % e.get("editId", e.get("id")))
                continue
            dt = max(0.0, max(vis.values()) - t0)
            lat.append(dt)
            after = num(e, "partsAfter")
            later = [s for s in sn if gid(s.get("entityId", s.get("entity"))) == ent and
                     num(s, "presentedSeTick", "seTick") is not None and
                     num(s, "presentedSeTick", "seTick") <= max(vis.values()) + 1e-9 and num(s, "parts") is not None]
            n_after = num(later[-1], "parts") if later else None
            if n_after is None:   # the grid's next per-second count after the change became visible
                vis_rows = [s for s in sn if gid(s.get("entityId", s.get("entity"))) == ent and
                            num(s, "presentedSeTick", "seTick") is not None and s.get("_qpc") is not None and
                            num(s, "presentedSeTick", "seTick") <= max(vis.values()) + 1e-9]
                q_vis = max((s["_qpc"] for s in vis_rows), default=None)
                g_after = [g for g in sn_grid if gid(g.get("entityId")) == ent and num(g, "parts") is not None]
                if q_vis is not None:
                    nxt = [g for g in g_after if g.get("_qpc") is not None and g["_qpc"] >= q_vis]
                    g_after = nxt[:1] or g_after
                n_after = num(g_after[-1] if q_vis is None else g_after[0], "parts") if g_after else None
            if after is not None and n_after is not None and after != n_after:
                problems.append("#%s: parts %s in Subnautica, %s in SE" % (e.get("editId", e.get("id")), n_after, after))
        worst = max(lat) if lat else None
        status = "FAIL" if problems or (worst is not None and worst > N2_MAX_TICKS) else ("PASS" if lat else "MISSING")
        row(rows, "S5.N2.%s" % kind, status, "worst %s ticks over %d edits%s" % (
            "%d" % worst if worst is not None else "-", len(es), "; " + problems[0] if problems else ""),
            "<= %d ticks; counts equal" % N2_MAX_TICKS, what, "n2", latencies=lat, problems=problems[:6])

# ----------------------------------------------------------------------------------------------------------------
# S5.N3: subgrids and subparts at alpha 1
# ----------------------------------------------------------------------------------------------------------------

def _pose_of(d):
    m = mat12(d.get("seMatrix"))
    piv = vecn(d.get("pivot"), 3)
    if m is None or piv is None:
        return None
    site, _txt = CP.make_site(d)
    o, q, s = W.wire_pose(site, m, piv)
    k = num(d, "k")
    return o, q, s, (k if k is not None else 1.0)

def _se_root_context(L):
    """Per root grid: [(tick, pivot, site, k)] from SE's s5.part / s5.pose rows (s5.sub rows of the SE plugin carry no
 P*: the latest pivot logged for that root at or before the tick is used)."""
    out = {}
    for n, d in L.se:
        if n not in ("s5.part", "s5.pose"):
            continue
        r = gid(d.get("rootId")) if n == "s5.part" else gid(d.get("entityId"))
        piv = vecn(d.get("pivot"), 3)
        if r is None or piv is None:
            continue
        out.setdefault(r, []).append((num(d, "tick") or 0, piv, d.get("site"), num(d, "k")))
    return out

def _child_pose(d, ctx, by_tick):
    """(o, q, s, k, toRoot) of an SE s5.sub row: the root's pose from its matrix and P* (the row's, else the latest

 """
    t = num(d, "tick")
    to_root = mat12(d.get("toRoot"))
    root = gid(d.get("rootId"))
    m = mat12(d.get("seMatrix"))
    if to_root is None:
        row_ = mat12(d.get("_c2row"))
        if row_ is None:
            return None
        owner = gid(d.get("ownerId"))
        o_row = by_tick.get((owner, int(t))) if t is not None else None
        if o_row is not None and mat12(o_row.get("_c2row")) is not None:     # a subpart on a subgrid
            to_root = W_mm(row_, mat12(o_row["_c2row"]))
            root = root or gid(o_row.get("ownerId"))
            m = m or mat12(o_row.get("_ownerWorld"))
        else:
            to_root = row_
            root = root or owner
            m = m or mat12(d.get("_ownerWorld"))
    piv = vecn(d.get("pivot"), 3)
    site_d = d if d.get("site") else None
    k = num(d, "k")
    if piv is None:
        cands = [c for c in ctx.get(root, []) if t is None or c[0] <= t] or ctx.get(root, [])
        if cands:
            _t, piv, site, k2 = max(cands, key=lambda c: c[0])
            site_d = {"site": site} if site else None
            k = k if k is not None else k2
    if m is None or piv is None:
        return None
    site, _txt = CP.make_site(site_d or {})
    o, q, s_ = W.wire_pose(site, m, piv)
    return o, q, s_, (k if k is not None else 1.0), to_root

def n3_rows(L, rows):
    what = "mirrored child pose vs SE's (rotor 10 rpm, piston, door): Apply of the root pose to the child's points " \
           "(interpolated as CONTRACT 2.8 between the bracket's ticks when alpha < 1), within 1 mm (wire_ref)"
    se = L.se_ev("s5.sub")
    sn = L.sn_ev("s5.sub")
    if not se:
        return
    by_tick = {}
    for d in se:
        if num(d, "tick") is not None:
            by_tick[(gid(d.get("partKey")), int(num(d, "tick")))] = d
    ctx = _se_root_context(L)
    poses = {}

    def pose_at(key, t):
        if (key, t) not in poses:
            d = by_tick.get((key, t))
            poses[(key, t)] = _child_pose(d, ctx, by_tick) if d is not None else None
        return poses[(key, t)]

    def kind_of(s_row, d_row):
        k = str((d_row or {}).get("kind", ""))
        if k in N3_KINDS:
            return k
        return window_kind(L, s_row, "n3.", N3_KINDS) or (window_kind(L, d_row, "n3.", N3_KINDS) if d_row else None)
    stats = {}
    for s_row in sn:
        pk = gid(s_row.get("partKey"))
        t = num(s_row, "seTick", "tick")
        corners = triples8(s_row.get("corners", s_row.get("cornersSn")))
        if pk is None or t is None or not corners:
            continue
        t = int(t)
        newer = pose_at(pk, t)
        kind = kind_of(s_row, by_tick.get((pk, t)))
        if kind is None:
            continue
        st = stats.setdefault(kind, {"worst": None, "n": 0, "unmatched": 0, "interp": 0})
        a = num(s_row, "alpha")
        a = 1.0 if a is None else a
        older = pose_at(pk, t - 1) if a < 0.999 else None
        lc = triples8(s_row.get("localCorners"))
        if newer is None or (a < 0.999 and older is None) or not lc or len(lc) != len(corners):
            st["unmatched"] += 1
            continue
        st["n"] += 1
        st["interp"] += a < 0.999
        for l, c in zip(lc, corners):
            o, q, s_, k, tr = newer
            pn = W.sn_apply_k(o, q, s_, k, W_pt(tr, l))
            if a < 0.999:
                o2, q2, s2, k2, tr2 = older
                po = W.sn_apply_k(o2, q2, s2, k2, W_pt(tr2, l))
                pn = tuple(po[i] + (pn[i] - po[i]) * a for i in range(3))
            e = math.dist(c, pn)
            if st["worst"] is None or e > st["worst"][0]:
                st["worst"] = (e, t)
    kinds = [k for k in N3_KINDS if k in stats or any(str(d.get("kind")) == k for d in se) or L.window("n3." + k)]
    for kind in kinds:
        st = stats.get(kind)
        if not st or st["n"] == 0:
            row(rows, "S5.N3.%s" % kind, "MISSING", "no Subnautica s5.sub row matched (SE %d rows, unmatched %d)" % (
                len(se), (st or {}).get("unmatched", 0)), "<= 1 mm", what, "n3")
        else:
            w = st["worst"]
            row(rows, "S5.N3.%s" % kind, "PASS" if w[0] <= TOL_M else "FAIL", "%.4f mm over %d frames" % (
                w[0] * 1000.0, st["n"]), "<= 1 mm", what, "n3", worstTick=w[1], unmatched=st["unmatched"],
                interpolated=st["interp"])

# ----------------------------------------------------------------------------------------------------------------
# S5.N4: coherence of a cockpit-fixed point
# ----------------------------------------------------------------------------------------------------------------

def _std2(pts):
    if len(pts) < 2:
        return None
    mx = sum(p[0] for p in pts) / len(pts)
    my = sum(p[1] for p in pts) / len(pts)
    return math.sqrt(sum((p[0] - mx) ** 2 + (p[1] - my) ** 2 for p in pts) / len(pts))

def n4_rows(L, rows):
    what = "coherence (ship-render 6.7 #2): camera in a cockpit, ship at 20 m/s with a 0.5 Hz yaw sine; screen " \
           "std-dev of a cockpit-fixed point over %g s (Subnautica WorldToScreenPoint)" % N4_SECONDS
    sn = L.sn_ev("s5.coh")
    if "n4" in L.parts_run():
        sn = L.in_window(sn, "n4") or sn
    if not sn:
        if "n4" in L.parts_run() or L.se_ev("s5.coh"):
            row(rows, "S5.N4", "MISSING", "no Subnautica s5.coh rows", "< %.1f px" % N4_MAX_PX, what, "n4")
        return
    # review F11: only frames with the point in front of the camera (depth > 0.5 m) and inside the picture count, and
 # at least 90 % of the rows must be such frames; SE's own pixel motion (head look, shake) voids the measurement
    total = len(sn)
    valid = []
    for d in sn:
        p = vecn(d.get("px"), 2)
        if p is None:
            continue
        dep = num(d, "depth")
        if dep is not None and dep <= 0.5:
            continue
        w, h = num(d, "w", "screenW"), num(d, "h", "screenH")
        if w and h and not (0 <= p[0] <= w and 0 <= p[1] <= h):
            continue
        valid.append(d)
    sn = valid
    pts = [vecn(d.get("px"), 2) for d in sn]
    pts = [p for p in pts if p is not None]
    qs = [d.get("_qpc") for d in sn if d.get("_qpc") is not None]
    span = (max(qs) - min(qs)) / float(L.qpcf) if len(qs) > 1 else None
    sd = _std2(pts)
    se_pts = [vecn(d.get("px"), 2) for d in L.se_ev("s5.coh")]
    se_sd = _std2([p for p in se_pts if p is not None])
    if sd is None:
        row(rows, "S5.N4", "MISSING", "no pixel in s5.coh", "< %.1f px" % N4_MAX_PX, what, "n4")
        return
    short = (span is not None and span < 0.9 * N4_SECONDS) or (span is None and len(pts) < 300)
    sparse = total > 0 and len(pts) < 0.9 * total
    se_moving = se_sd is not None and se_sd > 0.1
    status = "MISSING" if (short or sparse or se_moving) else ("FAIL" if sd >= N4_MAX_PX else "PASS")   # validity first
    row(rows, "S5.N4", status, "%.3f px over %s (%d frames)" % (sd, "%.1f s" % span if span is not None else "?",
                                                              len(pts)), "< %.1f px over %g s" % (N4_MAX_PX, N4_SECONDS),
        what + ("; MISSING: SE's own point moved %.2f px" % se_sd if se_moving else "") + ("; MISSING: only %d of %d frames valid" % (len(pts), total) if sparse else ""),
        "n4", seStdPx=se_sd, validFrames=len(pts), samples=total)

# ----------------------------------------------------------------------------------------------------------------
# S5.N5 loader, S5.N6 scale
# ----------------------------------------------------------------------------------------------------------------

def n5_rows(L, rows):
    what = "worker-thread loader: %d distinct models loaded with no exception (ship-render 1.4)" % N5_MODELS
    rs = L.se_ev("s5.model")
    note = ""
    if "n5" in L.parts_run():
        # S5-2a: `de.s5 models` had already loaded the set during g1e4, so the n5 window held no load rows; the
 # session's rows are the evidence either way (the loader ran on the worker thread once), say so in the row
        win = L.in_window(rs, "n5")
        if any(num(d, "loadMs") is not None or num(d, "verts") is not None for d in win):
            rs = win
        elif rs:
            note = " (loaded before the n5 window: the set was already resident)"
    if not rs:
        if "n5" in L.parts_run():
            row(rows, "S5.N5", "MISSING", "no SE s5.model rows", ">= %d, 0 exceptions" % N5_MODELS, what, "n5")
        return
    ok, exc = set(), []
    for d in rs:
        key = d.get("path") or d.get("modelKey")
        if d.get("exception") or d.get("error"):
            exc.append("%s: %s" % (key, d.get("exception") or d.get("error")))
        elif num(d, "loadMs") is not None or num(d, "verts") is not None:
            ok.add(str(key).lower())
    status = "FAIL" if exc else ("PASS" if len(ok) >= N5_MODELS else "MISSING")
    row(rows, "S5.N5", status, "%d loaded, %d exceptions%s" % (len(ok), len(exc), note), ">= %d, 0 exceptions" % N5_MODELS,
        what, "n5", exceptions=exc[:6],
        loadMsMax=max((num(d, "loadMs") for d in rs if num(d, "loadMs") is not None), default=None))

def _p(values, q):
    v = sorted(x for x in values if x is not None)
    if not v:
        return None
    return v[min(len(v) - 1, max(0, int(round(q * (len(v) - 1)))))]

def n6_rows(L, rows):
    what = "scale: sum of vertices, draw calls, Subnautica frame time and VRAM logged; underwater p99 <= %.1f ms with the " \
           "2,000-block ship (HYPOTHESIS); p99 = 90th percentile of the per-second p99 values" % N6_P99_MS
    run = L.parts_run()
    for size in N6_SIZES:
        part = "n6.%s" % size
        if part not in run:
            continue
        sc = L.in_window(L.sn_ev("s5.scale"), part)
        perf = L.in_window(L.sn_ev("s5.perf"), part)
        smp = [s for s in L.samples if s.get("part") == part]
        p99s = [num(d, "frameMsP99") for d in sc + perf] + [num(s.get("c7") or {}, "frameMsP99") for s in smp]
        p99s = [x for x in p99s if x is not None and x > 0]
        p99s = p99s[2:] if len(p99s) > 4 else p99s
        p99 = _p(p99s, 0.9)
        # review F8: the faithful criterion is the share of frames above 16.7 ms over the whole part (s5.scale rows)
        over_n = sum(num(d, "framesOver16_7") or 0 for d in sc if num(d, "framesOver16_7") is not None)
        frames_n = sum(num(d, "frames") or 0 for d in sc if num(d, "framesOver16_7") is not None)
        over_share = (over_n / frames_n) if frames_n else None
        vert = max((num(d, "vertices", "verts") or 0 for d in sc), default=None)
        draws = max((num(d, "drawEstimate", "drawCalls", "renderers") or 0 for d in sc), default=None)
        vram = max(((num(d, "vramMb") or ((num(d, "meshBytes") or 0) + (num(d, "textureBytes") or 0)) / 2 ** 20)
                    for d in sc), default=None)
        budget = L.in_window(L.se_ev("s5.budget"), part)
        dropped = max((num(d, "c2Dropped") or 0 for d in budget), default=None)
        logged = bool(sc) and p99 is not None
        if size == "2000":
            if over_share is not None:
                status = "PASS" if over_share <= 0.01 else "FAIL"
            else:
                status = "MISSING" if p99 is None else ("PASS" if p99 <= N6_P99_MS and sc else
                                                         ("FAIL" if p99 > N6_P99_MS else "MISSING"))
        else:
            status = "PASS" if logged else "MISSING"
        row(rows, "S5.N6.%s" % size, status, "p99 %s ms, %s verts, %s draws, %s MB" % (
            "%.2f" % p99 if p99 is not None else "-", "%d" % vert if vert is not None else "-",
            "%d" % draws if draws is not None else "-", "%.0f" % vram if vram is not None else "-"),
            "p99 <= %.1f ms" % N6_P99_MS if size == "2000" else "logged", what, part, c2Dropped=dropped,
            seconds=len(sc))

# ----------------------------------------------------------------------------------------------------------------
# S5.N7: the player's character
# ----------------------------------------------------------------------------------------------------------------

def _first_person(d):
    v = d.get("cameraMode")
    if isinstance(v, str):
        return v.lower().startswith("first")
    return num(d, "cameraMode") == 1

def n7_rows(L, rows):
    se = L.se_ev("s5.char")
    sn = L.sn_ev("s5.char")
    if not se and not sn and "n7" not in L.parts_run():
        return
    # N7a
    what_a = "body entity pose follows SE's character within 1 cm (third person); SeBones: pelvis, head, right palm " \
             "within 1 cm of SE's absolute bones through Apply (wire_ref)"
    idx = {int(num(d, "tick")): d for d in se if num(d, "tick") is not None}
    worst_e, worst_b, n_e, n_b = None, None, 0, 0
    for s in sn:
        a = num(s, "alpha")
        # S5-2d: a third-person sample sits between two C1 ticks (alpha < 1); Subnautica writes the older frame's origin
 # (c1Oo) so the entity check judges lerp(older, newer, alpha); bones are judged on exact-tick samples only
        oo = vecn(s.get("c1Oo"), 3)
        interp = a is not None and a < 0.999
        if interp and oo is None:
            continue
        t = num(s, "seTick", "tick")
        d = idx.get(int(t)) if t is not None else None
        if d is None and s.get("charO") is not None:
            d = s              # the C1 pose Subnautica received (c1O/c1Q/c1S) with the SE bones it carried (seAbs)
        if d is None:
            continue
        o = vecn(d.get("charO"), 3) or vecn(s.get("charO"), 3)
        q = vecn(d.get("charQ"), 4) or vecn(s.get("charQ"), 4)
        sc = num(d, "charS") if num(d, "charS") is not None else num(s, "charS")
        if o is None or q is None or sc is None:
            continue
        ep = vecn(s.get("entityPos"), 3)
        if ep is not None and not _first_person(s):
            e = math.dist(ep, [oo[i] + (o[i] - oo[i]) * a for i in range(3)] if interp else o)
            n_e += 1
            worst_e = e if worst_e is None else max(worst_e, e)
        # S5-2e: a row written inside the 0.25 s source blend (part of its second on another source) mixes two poses;
 # the bone check wants seconds that were SeBones throughout
        pure = not (num(s, "retarget") or 0) and not (num(s, "bind") or 0)    # (seBones is the bone dict after parsing)
        if str(s.get("source", "")) == "SeBones" and not interp and pure:
            # S5-2e: the bones compose under the pose Subnautica applied, which differs from the newer C1 origin by the camera's interpolation offset while moving
            ob = vecn(s.get("o"), 3) or o
            qb = vecn(s.get("q"), 4) or q
            scb = num(s, "s") if num(s, "s") is not None else sc
            for b in ("pelvis", "head", "rpalm"):
                sb = vecn((s.get("bones") or {}).get(b), 3)
                m = mat12((d.get("bones") or {}).get(b)) or mat12((s.get("seBones") or {}).get(b))
                if sb is None or m is None:
                    continue
                e = math.dist(sb, W.sn_apply_k(ob, qb, scb, 1.0, m[3]))
                n_b += 1
                worst_b = e if worst_b is None else max(worst_b, e)
    if n_e == 0 and n_b == 0:
        row(rows, "S5.N7a", "MISSING", "no matched character rows (SE %d, SN %d)" % (len(se), len(sn)), "<= 1 cm",
            what_a, "n7")
    else:
        bad = (worst_e or 0) > N7_TOL_M or (worst_b or 0) > N7_TOL_M
        status = "FAIL" if bad else ("PASS" if n_e and n_b else "MISSING")
        row(rows, "S5.N7a", status, "entity %s cm (%d), bones %s cm (%d)" % (
            "%.2f" % (worst_e * 100) if worst_e is not None else "-", n_e,
            "%.2f" % (worst_b * 100) if worst_b is not None else "-", n_b), "<= 1 cm", what_a, "n7")
    # N7b
    what_b = "the HiddenInFirstPerson part is not drawn in first person and is drawn in third person"
    fp = [s for s in sn if _first_person(s) and flag(s, "head1stDrawn") is not None]
    tp = [s for s in sn if not _first_person(s) and flag(s, "head1stDrawn") is not None]
    if not fp or not tp:
        row(rows, "S5.N7b", "MISSING", "first person %d rows, third person %d rows" % (len(fp), len(tp)),
            "hidden 1st, drawn 3rd", what_b, "n7")
    else:
        bad1 = sum(1 for s in fp if flag(s, "head1stDrawn"))
        bad3 = sum(1 for s in tp if not flag(s, "head1stDrawn"))
        row(rows, "S5.N7b", "PASS" if not bad1 and not bad3 else "FAIL", "drawn in 1st %d/%d, hidden in 3rd %d/%d" % (
            bad1, len(fp), bad3, len(tp)), "0 / 0", what_b, "n7")
    # N7c
    what_c = "while swimming the source is Retarget on >= 95 % of frames, all 15 required human bones mapped, no " \
             "non-finite bone; the fallback model only with a logged reason (HYPOTHESIS O22)"
    swim = [s for s in sn if flag(s, "swimming", "inWater")]
    rig = L.sn_ev("s5.rig")
    # review F2: the retarget reports its own required-bone count (path H: human bones, path G: snBones) on s5.char
    mapped = max((num(c, "requiredMapped") for c in sn if num(c, "requiredMapped") is not None), default=None)
    if mapped is None:
        mapped = max((num(r, "requiredMapped") or 0 for r in rig), default=None)
    if mapped is None:
        for r in rig:
            m = re.match(r"(\d+)\s*/\s*(\d+)", str(r.get("required", "")))
            if m:
                mapped = float(m.group(1))
    nonfinite = sum(int(num(s, "nonFinite") or 0) for s in sn)
    fallback_bad = [d for d in se if flag(d, "fallback") and not str(d.get("reason", "")).strip()]
    agg = [s for s in sn if num(s, "swimFrames") is not None]
    if agg:        # Subnautica's per-second aggregates: frames swimming, of them on Retarget
        n_sw = sum(num(s, "swimFrames") for s in agg)
        swim = swim if n_sw > 0 else []
    if not swim:
        row(rows, "S5.N7c", "MISSING", "no swimming frames", ">= 95 %% Retarget, %d/15" % REQUIRED_HUMAN, what_c, "n7")
    else:
        if agg:
            share = sum(num(s, "swimRetargetFrames") or 0 for s in agg) / float(n_sw)
        else:
            share = sum(1 for s in swim if str(s.get("source", "")) == "Retarget") / float(len(swim))
        bad = share < N7C_SHARE or (mapped is not None and mapped < REQUIRED_HUMAN) or nonfinite or fallback_bad
        status = "FAIL" if bad else ("PASS" if mapped is not None else "MISSING")
        row(rows, "S5.N7c", status, "Retarget %.1f %% of %d frames, mapped %s/15, nonFinite %d, fallback w/o reason %d" % (
            share * 100.0, int(n_sw) if agg else len(swim), "%d" % mapped if mapped is not None else "?", nonfinite,
            len(fallback_bad)),
            ">= 95 %, 15/15, 0, 0", what_c, "n7", rig=[{k: r.get(k) for k in ("animator", "isHuman", "avatarHuman")}
                                                     for r in rig[:2]])
    row(rows, "S5.N7c.owner", "INFO", "owner look (swimming third person, first-person arms with fpbody=se)", "verdict",
        "recorded in MODLOG by the orchestrator", "n7", required=False)

# ----------------------------------------------------------------------------------------------------------------
# S5.T-P1: performance states
# ----------------------------------------------------------------------------------------------------------------

def read_measure(path):
    out = {}
    if not path or not os.path.exists(path):
        return out
    with open(path, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            out.setdefault(r.get("name", "?"), []).append(r)
    return out

def _floats(rs, key):
    v = []
    for r in rs or ():
        try:
            if r.get(key) not in (None, ""):
                v.append(float(r[key]))
        except ValueError:
            pass
    return v

def _mean(v):
    v = [x for x in v if x is not None]
    return sum(v) / len(v) if v else None

def sim_ratio(samples, qpcf):
    """(mean, min) SE simulation ratio from the C1 tick of the launcher samples, over >= 1 s windows."""
    pts = [(s["qpc"], s.get("c1Tick")) for s in samples if s.get("c1Tick") is not None]
    rat = []
    i = 0
    for j in range(1, len(pts)):
        dt = (pts[j][0] - pts[i][0]) / float(qpcf)
        if dt >= 1.0:
            rat.append((pts[j][1] - pts[i][1]) / (60.0 * dt))
            i = j
    if not rat:
        return None, None
    return sum(rat) / len(rat), min(rat)

def sn_fps(samples, qpcf):
    pts = [(s["qpc"], (s.get("c7") or {}).get("snFrame")) for s in samples if (s.get("c7") or {}).get("snFrame") is not None]
    if len(pts) < 2 or pts[-1][0] == pts[0][0]:
        return None
    return (pts[-1][1] - pts[0][1]) / ((pts[-1][0] - pts[0][0]) / float(qpcf))

def se_frame_ms(L, part):
    """SE per-second frame time (ms) of a part: the plugin's 1 Hz sample records, else SE's STATISTICS FrameTime."""
    rs = L.in_window([d for n, d in L.se if n == "sample"], part)
    v = [num(d, "frameTimeAvgMs", "frameMsP50") for d in rs]
    v = [x for x in v if x is not None]
    mx = [num(d, "frameTimeMaxMs", "frameMsMax") for d in rs]
    mx = [x for x in mx if x is not None]
    if not v:
        w = L.window(part)
        if w:
            v = [r["FrameTime"] for r in L.stats if isinstance(r.get("FrameTime"), float) and r.get("_qpc") and
                 w["q0"] <= r["_qpc"] <= w["q1"]]
    return v, mx

def tp1_state_data(L, state, measure_dir=None):
    part = "tp1.%s" % state
    smp = [s for s in L.samples if s.get("part") == part]
    mem = [r for r in L.sess("mem") if r.get("part") == part]
    m = read_measure(os.path.join(measure_dir or L.folder, "measure-%s.csv" % part))
    perf = L.in_window(L.sn_ev("s5.perf"), part)
    fr, mx = se_frame_ms(L, part)
    sim_mean, sim_min = sim_ratio(smp, L.qpcf)
    c7 = [s.get("c7") or {} for s in smp if s.get("c7")]
    c5 = [s.get("c5") or {} for s in smp if s.get("c5")]
    p99s = [num(d, "frameMsP99") for d in perf] + [num(c, "frameMsP99") for c in c7]
    p99s = [x for x in p99s if x is not None and x > 0]
    over_n = sum(num(d, "framesOver16_7") or 0 for d in perf if num(d, "framesOver16_7") is not None)
    frames_n = sum(num(d, "frames") or 0 for d in perf if num(d, "framesOver16_7") is not None)
    commit = [num(r, "commitTotal") / num(r, "physTotal") for r in mem if num(r, "commitTotal") and num(r, "physTotal")]
    limits = [num(r, "commitLimit") for r in mem if num(r, "commitLimit")]
    return {
        "part": part, "samples": len(smp), "simMean": sim_mean, "simMin": sim_min,
        "seFrameMs": _p(fr, 0.5), "seFrameMaxMs": max(mx) if mx else None, "seSeconds": len(fr),
        "snFps": sn_fps(smp, L.qpcf), "snP99": _p(p99s, 0.9), "snOver16_7": (over_n / frames_n) if frames_n else None,
        "perfModes": [c.get("perfModeApplied") for c in c7],
        "vsTarget": [(c.get("vSyncCountApplied"), c.get("targetFrameRateApplied")) for c in c7],
        "primeShare": (sum(1 for c in c5 if c.get("viewState") == 1 and c.get("approachMode") == 2) / float(len(c5))
                       if c5 else None),
        "camerasOn": [flag(d, "camerasOn") for d in perf if flag(d, "camerasOn") is not None],
        "recipes": [str(d.get("recipe")) for d in perf if d.get("recipe") is not None],
        "snGpu": _mean(_floats(m.get("Subnautica", []), "gpu3d_pct")), "snCpu": _mean(_floats(m.get("Subnautica", []),
                                                                                             "cpu_pct")),
        "seGpu": _mean(_floats(m.get("Legacy", []), "gpu3d_pct")), "seCpu": _mean(_floats(m.get("Legacy", []), "cpu_pct")),
        "commitMax": max(commit) if commit else None,
        "pageFileGrowth": (max(limits) - min(limits)) if limits else None,
        "measureRows": sum(len(v) for v in m.values()),
    }

def load_tp1_baseline(path):
    """{'base-se[.<spot>]': {'seFrameMs': x}, 'base-sn': {'snP99': y}} from a base-se / base-sn T-P1 folder or its
 s5-results.json (tp1Baseline)."""
    if not path:
        return {}
    if os.path.isdir(path):
        p = os.path.join(path, "s5-results.json")
        if os.path.exists(p):
            path = p
        else:
            L = Logs(path)
            out = {}
            for st in [x[4:] for x in L.parts_run() if x and x.startswith("tp1.base")]:
                d = tp1_state_data(L, st)
                out[st] = {"seFrameMs": d["seFrameMs"], "snP99": d["snP99"]}
            return out
    with open(path, encoding="utf-8") as f:
        return (json.load(f).get("tp1Baseline") or {})

def tp1_rows(L, rows, baseline=None):
    run = [p[4:] for p in L.parts_run() if p and p.startswith("tp1.")]
    if not run:
        return {}
    what = "T-P1 (ROADMAP S5): SE simulation ratio >= 0.98; SE frame time in Space/Track within 5 % of SE alone; " \
           "Prime duty cycle logged and warm-up hitch < 50 ms; Subnautica underwater p99 <= 16.7 ms; commit < 85 % " \
           "of RAM, no page-file growth; Space: Subnautica's Idle recipe (<= 11 fps); Track: cameras off"
    data = {st: tp1_state_data(L, st) for st in run}
    base = dict(baseline or {})
    for st, d in data.items():
        if st.startswith("base"):
            base[st] = {"seFrameMs": d["seFrameMs"], "snP99": d["snP99"]}

    def se_base_for(state):
        """SE alone at the same spot (base-se.<state>), else SE alone anywhere (base-se)."""
        for k in ("base-se." + state, "base-se"):
            v = (base.get(k) or {}).get("seFrameMs")
            if v is not None:
                return v
        return None
    # the SE-alone baseline: one row over its spots (base-se.space, base-se.track-hover, base-se.track-sail)
    bse = sorted(st for st in data if st.startswith("base-se"))
    if bse:
        vals = ["%s %s ms" % (st[8:] or "all", _fmt(data[st]["seFrameMs"])) for st in bse]
        ok = any(data[st]["seFrameMs"] is not None for st in bse)
        row(rows, "S5.T-P1.base-se", "INFO" if ok else "MISSING", "; ".join(vals)[:34], "baseline", what,
            "tp1.base-se", data={st: data[st] for st in bse})
    for st in [x for x in TP1_STATES if x != "base-se"]:
        if st not in data:
            continue
        d = data[st]
        se_base = se_base_for(st)
        crit = []          # (name, status, measured)

        def c(name, status, measured):
            crit.append((name, status, measured))

        if st == "ccx1":
            row(rows, "S5.T-P1.ccx1", "INFO", "sim %s, SN p99 %s ms, SE %s ms" % (
                _fmt(d["simMean"]), _fmt(d["snP99"]), _fmt(d["seFrameMs"])), "experiment (D10: no affinity in v1)",
                what, "tp1.ccx1", required=False, data=d)
            continue
        if st.startswith("base"):
            ok = (d["seFrameMs"] is not None) if st == "base-se" else (d["snP99"] is not None or d["snFps"] is not None)
            row(rows, "S5.T-P1.%s" % st, "INFO" if ok else "MISSING", "SE %s ms, sim %s; SN p99 %s ms, %s fps" % (
                _fmt(d["seFrameMs"]), _fmt(d["simMean"]), _fmt(d["snP99"]), _fmt(d["snFps"])), "baseline", what,
                "tp1.%s" % st, required=True, data=d)
            continue
        if d["simMean"] is None:
            c("sim", "MISSING", "no C1 samples")
        else:
            c("sim", "PASS" if d["simMean"] >= TP1_SIM_MIN else "FAIL", "%.3f (min %.3f)" % (d["simMean"], d["simMin"]))
        if d["commitMax"] is None:
            c("commit", "MISSING", "no mem samples")
        else:
            c("commit", "PASS" if d["commitMax"] < TP1_COMMIT_MAX else "FAIL", "%.1f %%" % (d["commitMax"] * 100))
            c("pagefile", "PASS" if not d["pageFileGrowth"] else "FAIL", "growth %s B" % _fmt(d["pageFileGrowth"]))
        if st in ("space", "track-hover", "track-sail"):
            if d["seFrameMs"] is None or se_base is None:
                c("seFrame", "MISSING", "SE %s ms, SE alone %s ms" % (_fmt(d["seFrameMs"]), _fmt(se_base)))
            else:
                r = d["seFrameMs"] / se_base - 1.0
                c("seFrame", "PASS" if r <= TP1_FRAME_TOL else "FAIL", "%+.1f %% (%.3f vs %.3f ms)" % (
                    r * 100, d["seFrameMs"], se_base))
        if st == "space":
            modes = [m for m in d["perfModes"] if m is not None]
            idle = sum(1 for m in modes if m == 0) / float(len(modes)) if modes else None
            fps = d["snFps"]
            if idle is None and fps is None:
                c("snIdle", "MISSING", "no C7 samples")
            else:
                ok = (idle is None or idle >= TP1_SHARE) and (fps is None or fps <= TP1_IDLE_FPS_MAX)
                c("snIdle", "PASS" if ok else "FAIL", "Idle %s of samples, %s fps" % (
                    "%.0f %%" % (idle * 100) if idle is not None else "?", _fmt(fps)))
        if st in ("track-hover", "track-sail"):
            cams = d["camerasOn"]
            if cams:
                off = sum(1 for x in cams if not x) / float(len(cams))
                c("camerasOff", "PASS" if off >= TP1_SHARE else "FAIL", "off %.0f %% of %d s" % (off * 100, len(cams)))
            elif d["snGpu"] is not None:
                c("camerasOff", "PASS" if d["snGpu"] <= TP1_TRACK_GPU_MAX else "FAIL", "SN GPU %.1f %%" % d["snGpu"])
            else:
                c("camerasOff", "MISSING", "no s5.perf camerasOn, no measure.ps1 GPU")
        if st == "prime":
            c("dutyCycle", "INFO", "Prime %s of samples" % ("%.0f %%" % (d["primeShare"] * 100) if d["primeShare"]
                                                             is not None else "?"))
            if d["seFrameMaxMs"] is None:
                c("hitch", "MISSING", "no SE per-second max frame time")
            else:
                c("hitch", "PASS" if d["seFrameMaxMs"] < TP1_HITCH_MS else "FAIL", "max %.1f ms" % d["seFrameMaxMs"])
        if st == "underwater":
            if d["snP99"] is None:
                c("snP99", "MISSING", "no Subnautica p99")
            else:
                c("snP99", "PASS" if d["snP99"] <= TP1_SN_P99_MS else "FAIL", "%.2f ms" % d["snP99"])
        sts = [x[1] for x in crit]
        status = "FAIL" if "FAIL" in sts else ("MISSING" if "MISSING" in sts else "PASS")
        row(rows, "S5.T-P1.%s" % st, status, "; ".join("%s %s" % (n, m) for n, s, m in crit if s != "PASS")[:34] or
            "all criteria", "every criterion", what, "tp1.%s" % st,
            criteria=[{"name": n, "status": s, "measured": m} for n, s, m in crit], data=d)
    return base

def _fmt(v):
    if v is None:
        return "-"
    if isinstance(v, float):
        return "%.3f" % v
    return str(v)

# ----------------------------------------------------------------------------------------------------------------
# Evaluation
# ----------------------------------------------------------------------------------------------------------------

def evaluate(folder: str, baseline=None, baseline_logs=None):
    L = Logs(folder)
    rows = []
    st = L.start()
    kind = st.get("kind") or ("tp1" if os.path.exists(os.path.join(folder, "tp1-session.jsonl")) else "s5")
    parts = set(st.get("parts") or [])
    tp1_base = {}
    steps = []
    if kind != "tp1":
        steps += [("G1-E4", lambda: g1e4_rows(L, rows), "g1e4"), ("N1.C1", lambda: n1c1_rows(L, rows), "n1"),
                  ("N1.C2", lambda: n1c2_rows(L, rows), "n1"), ("N1.C11", lambda: n1c11_rows(L, rows, baseline_logs), "n1"),
                  ("N1.C12", lambda: n1c12_rows(L, rows), "teardown"), ("N2", lambda: n2_rows(L, rows), "n2"),
                  ("N3", lambda: n3_rows(L, rows), "n3"), ("N4", lambda: n4_rows(L, rows), "n4"),
                  ("N5", lambda: n5_rows(L, rows), "n5"), ("N6", lambda: n6_rows(L, rows), "n6"),
                  ("N7", lambda: n7_rows(L, rows), "n7")]
    res = {}
    steps.append(("T-P1", lambda: res.update(tp1=tp1_rows(L, rows, load_tp1_baseline(baseline))), "tp1"))
    for name, fn, part in steps:
        if parts and part not in parts and kind != "tp1" and part not in ("tp1",) and not _has_data(L, part):
            continue
        try:
            fn()
        except Exception as e:  # noqa: BLE001 - an analyser bug is a FAIL row, never a crash
            import traceback
            row(rows, "S5.%s" % name, "FAIL", "analyser error %s" % type(e).__name__, "-", str(e)[:100] + " | " +
                traceback.format_exc().strip().splitlines()[-1][:100], part)
    tp1_base = res.get("tp1") or {}
    end = next((r for r in reversed(L.session) if r.get("ev") == "end"), None)
    if end is not None:
        row(rows, "S5.harness", "INFO", "unsupported %s" % (",".join(end.get("unsupported") or []) or "none"), "-",
            "session plumbing: commands %s; actions a plugin refused as unknown leave their rows MISSING" %
            json.dumps(end.get("stats"))[:120], "session", required=False)
    for m in st.get("mutants") or []:
        mid = m.split("@")[0]
        want = MUTANT_ROWS.get(mid, ())
        measured = [r for r in rows if r["id"] in want and r["status"] in ("PASS", "FAIL")]
        red = [r["id"] for r in measured if r["status"] == "FAIL"]
        status = "MISSING" if not measured else ("PASS" if len(red) == len(measured) else "FAIL")
        row(rows, "%s.red" % mid, status, "red: %s" % (",".join(red) or "none"), "red: %s" % ",".join(want),
            "seeded failure %s turns its rows red" % mid, "mutants", required=True)
    return rows, tp1_base

def _has_data(L, part):
    probe = {"g1e4": ("s5.model",), "n1": ("s5.grid", "s5.part"), "teardown": ("s5.count", "s5.teardown"),
             "n2": ("s5.edit",), "n3": ("s5.sub",), "n4": ("s5.coh",), "n5": (), "n6": ("s5.scale",),
             "n7": ("s5.char",)}.get(part, ())
    return any(L.se_ev(p) or L.sn_ev(p) for p in probe)

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s5", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s5-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s5-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

# ----------------------------------------------------------------------------------------------------------------
# Self-test: synthetic sessions written in the log contract (a faithful Subnautica side computed with wire_ref)
# ----------------------------------------------------------------------------------------------------------------

def _jsonl(path, recs):
    with open(path, "w", encoding="utf-8") as f:
        for r in recs:
            f.write(json.dumps(r, default=str) + "\n")

def synth_session(folder, mutant=None, kind="s5"):
    """One synthetic collected folder: SE rows from the stand-in's real ship geometry (s5_stream.Ship when the SE
 Content folder is present, else a procedural one), the Subnautica rows computed with wire_ref, launcher events, CSVs."""
    import random
    import site_v1
    import s5_stream as X
    rnd = random.Random(5)
    os.makedirs(folder, exist_ok=True)
    f = 10_000_000
    q = [10 ** 12]

    def qpc(dt=0.0):
        q[0] += int(dt * f)
        return q[0]

    se, sn, sess = [], [], []
    site = {"C": list(site_v1.TEST_C), "Rs": site_v1.RS_EXPECTED}
    mutants = [mutant] if mutant else []
    parts_all = ["g1e4", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "teardown"] if kind == "s5" else []
    states = ["base-se.space", "base-se.track-hover", "base-se.track-sail", "base-sn", "space", "track-hover",
              "track-sail", "prime", "underwater"] if kind == "tp1" else []
    sess.append({"ev": "start", "kind": kind, "parts": parts_all + ["tp1"] * bool(states), "mutants": mutants,
                 "qpcFrequency": f, "qpc": qpc(), "utc": "2026-10-17T10:00:00.000Z", "states": states})

    def part(name, phase, tick=None):
        sess.append({"ev": "part", "name": name, "phase": phase, "qpc": qpc(0.01), "tick": tick})

    tick = 1000
    wm = __import__("worldmap_ref")
    S = wm.Site(C=tuple(site["C"]), Rs=site["Rs"], U0=site_v1.ANCHOR["U0"], N0=site_v1.ANCHOR["N0"],
                psi_deg=site_v1.ANCHOR["psi_deg"], O=site_v1.ANCHOR["O"])
    if kind == "s5":
        T = S.sn_to_se((-1029.0, -150.0, -1100.0))
        E, U, N = S.frame_at(T)
        h = math.radians(30.0)
        fwd = tuple(N[i] * math.cos(h) + E[i] * math.sin(h) for i in range(3))
        right = (fwd[1] * U[2] - fwd[2] * U[1], fwd[2] * U[0] - fwd[0] * U[2], fwd[0] * U[1] - fwd[1] * U[0])
        M = (right, tuple(U), tuple(-x for x in fwd), tuple(T))
        piv = tuple(T[i] + 3.0 * U[i] for i in range(3))
        o, qq, s_ = W.wire_pose(S, M, piv)
        f32 = W.f32v

        def apply(l):
            return tuple(W.f32(x) for x in W.sn_apply_k(o, qq, s_, 1.0, l))
        # ---- G1-E4: 221 models, two census-listed inverted ones
        part("g1e4", "begin", tick)
        names = ["Models\\Cubes\\Large\\Armor\\Model%03d.mwm" % i for i in range(219)] + \
            ["Models\\Cubes\\Large\\Armor\\RoundCornerTriangleRightPlate.mwm",
             "Models\\Cubes\\Small\\InsetConnectorConstruction_1.mwm"]
        names = names[:150] + names[219:] + names[150:219]
        for i, nme in enumerate(names):
            mk = X.model_key(nme)
            inv = census_name(nme) is not None
            se.append({"ev": "s5.model", "modelKey": "0x%016x" % mk, "path": nme, "verts": 100, "tris": 80,
                       "loadMs": 1.0, "glass": False, "alphaMasked": False, "t": qpc(0.001) / f})
            raw = 0.0 if inv else (0.9995 if i % 7 else 1.0)
            flipped = inv and mutant != "S5.M2"
            sn.append({"event": "s5.model", "data": {"modelKey": "0x%016x" % mk, "agree": int(raw * 80), "total": 80,
                                                     "frac": raw, "flipped": flipped}, "t": qpc(0.001) / f})
        part("g1e4", "end", tick)
        # ---- N1: counts, 20 parts, (grind) recount
        part("n1", "begin", tick)
        grids = {0x44455335000001: 488, 0x44455335000002: 5, 0x44455335000003: 3, 0x44455335000004: 9}
        for g, n in grids.items():
            se.append({"ev": "s5.grid", "tick": tick, "entityId": "0x%016x" % g, "parts": n, "tris": n * 80,
                       "label": "count", "t": qpc(0.001) / f})
            sn.append({"event": "s5.grid", "data": {"entityId": "0x%016x" % g, "parts": n, "tris": n * 80},
                       "t": qpc(0.001) / f})
        for i in range(20):
            g = list(grids)[i % 4]
            ang = rnd.uniform(0, 2 * math.pi)
            rot = X.m_rot("y", ang)
            to_root = X.m_mul(rot, X.m_trans((rnd.uniform(-10, 10), rnd.uniform(-5, 5), rnd.uniform(-20, 20))))
            b = (-1.25, -1.25, -1.25, 1.25, 1.25, 1.25)
            key = 0x1000 + i
            se.append({"ev": "s5.part", "tick": tick, "entityId": "0x%016x" % g, "rootId": "0x%016x" % 0x44455335000001,
                       "partKey": "0x%016x" % key, "toRoot": X.m_rows12(to_root), "seMatrix": [c for r in M for c in r],
                       "pivot": list(piv), "k": 1.0, "site": site, "bounds": list(b), "t": qpc(0.001) / f})
            lc = X.aabb_corners(b)
            corners = [apply(W_pt(to_root, c)) for c in lc]
            sn.append({"event": "s5.placed", "data": {"entityId": "0x%016x" % g, "partKey": "0x%016x" % key,
                                                      "seTick": tick + 2, "corners": [list(c) for c in corners],
                                                      "localCorners": [list(c) for c in lc]}, "t": qpc(0.001) / f})
        # grind on the root, then recount (S5.M3: the PartRemove never arrives)
        tick += 30
        se.append({"ev": "s5.grid", "tick": tick, "entityId": "0x%016x" % 0x44455335000001, "parts": 487,
                   "tris": 487 * 80, "label": "afterGrind", "t": qpc(0.001) / f})
        sn.append({"event": "s5.grid", "data": {"entityId": "0x%016x" % 0x44455335000001,
                                                "parts": 488 if mutant == "S5.M3" else 487,
                                                "tris": (488 if mutant == "S5.M3" else 487) * 80}, "t": qpc(0.001) / f})
        part("n1", "end", tick)
        # ---- N2: five edits
        part("n2", "begin", tick)
        count = 487
        for j, kd in enumerate(N2_EDITS):
            tick += 20
            key = 0x9000 + j
            count += {"weld": 1, "stage": 0, "damage": 0, "paint": 0, "grind": -1}[kd]
            se.append({"ev": "s5.edit", "edit": kd, "editId": j + 1, "tick": tick, "entityId": "0x%016x" % 0x44455335000001,
                       "partKeys": ["0x%016x" % key], "partsAfter": count, "t": qpc(0.01) / f})
            for sk in N2_SN_KINDS[kd]:
                if kd == "grind" and mutant == "S5.M3":
                    continue
                sn.append({"event": "s5.edit", "data": {"entityId": "0x%016x" % 0x44455335000001,
                                                        "partKey": "0x%016x" % key, "kind": sk,
                                                        "presentedSeTick": tick + 3, "parts": count},
                           "t": qpc(0.01) / f})
        part("n2", "end", tick)
        # ---- N3: rotor, piston, door at alpha 1
        part("n3", "begin", tick)
        for kd in N3_KINDS:
            for j in range(40):
                tick += 1
                if kd == "rotor":
                    tr = X.m_mul(X.m_rot("y", math.radians(j)), X.m_trans((0.0, 10.0, 0.0)))
                elif kd == "piston":
                    tr = X.m_trans((12.0 + 0.01 * j, 0.0, 0.0))
                else:
                    tr = X.m_trans((-0.6 - 0.02 * j, -5.0, -20.0))
                b = (-1.25, -1.25, -1.25, 1.25, 1.25, 1.25)
                se.append({"ev": "s5.sub", "tick": tick, "kind": kd, "ownerId": "1", "partKey": "0x%x" % (0x500 +
                           N3_KINDS.index(kd)), "toRoot": X.m_rows12(tr), "seMatrix": [c for r in M for c in r],
                           "pivot": list(piv), "k": 1.0, "site": site, "bounds": list(b), "t": qpc(0.016) / f})
                lc = X.aabb_corners(b)
                sn.append({"event": "s5.sub", "data": {"seTick": tick, "alpha": 1.0, "partKey": "0x%x" % (
                    0x500 + N3_KINDS.index(kd)), "corners": [list(apply(W_pt(tr, c))) for c in lc],
                    "localCorners": [list(c) for c in lc]}, "t": qpc(0.001) / f})
        part("n3", "end", tick)
        # ---- N4: 10 s of cockpit-point pixels (S5.M1: the ship one tick behind -> a yaw-driven sway)
        part("n4", "begin", tick)
        for fr in range(int(N4_SECONDS * 60) + 6):
            tt = fr / 60.0
            sway = 25.0 * math.cos(2 * math.pi * 0.5 * tt) if mutant == "S5.M1" else 0.0
            sn.append({"event": "s5.coh", "data": {"frame": fr, "seTick": tick + fr,
                                                   "px": [1280.0 + sway + rnd.gauss(0, 0.05), 900.0 + rnd.gauss(0, 0.05)],
                                                   "qpc": qpc(1.0 / 60.0)}})
        part("n4", "end", tick + 600)
        tick += 600
        # ---- N5: 200 models loaded
        part("n5", "begin", tick)
        for i in range(200):
            se.append({"ev": "s5.model", "path": "Models\\Cubes\\Large\\Armor\\L%03d.mwm" % i, "loadMs": 2.0,
                       "label": "loader", "thread": "DE.ModelLoader", "t": qpc(0.001) / f})
        part("n5", "end", tick)
        # ---- N6: three sizes, 10 s each
        for size, p99 in (("500", 9.0), ("2000", 12.5), ("5000", 21.0)):
            part("n6.%s" % size, "begin", tick)
            for sec_ in range(10):
                sn.append({"event": "s5.scale", "data": {"vertices": int(size) * 120, "drawEstimate": int(size) // 8,
                                                         "renderers": int(size) // 8, "meshBytes": int(size) * 9000,
                                                         "textureBytes": 300 << 20, "frameMsP50": p99 * 0.6,
                                                         "frameMsP99": p99}, "t": qpc(1.0) / f})
                se.append({"ev": "s5.budget", "c2Dropped": 0, "c3Ms": 1.0, "t": qpc(0.0) / f})
            part("n6.%s" % size, "end", tick)
        # ---- N7: the character (SeBones third person, first person, swimming with Retarget)
        part("n7", "begin", tick)
        co, cq, cs = W.wire_pose(S, M, M[3])
        bones = {"pelvis": X.m_rows12(X.m_trans((0.0, 1.0, 0.0))), "head": X.m_rows12(X.m_trans((0.0, 1.7, 0.05))),
                 "rpalm": X.m_rows12(X.m_trans((-0.57, 1.06, 0.0)))}
        for j in range(60):
            tick += 1
            se.append({"ev": "s5.char", "tick": tick, "characterId": "77", "charO": list(co), "charQ": list(cq),
                       "charS": cs, "bones": bones, "fallback": False, "t": qpc(0.016) / f})
            fp = j % 3 == 0
            swim = j >= 30
            bw = {b: list(W.sn_apply_k(co, cq, cs, 1.0, X.m_from12(v)[3])) for b, v in bones.items()}
            sn.append({"event": "s5.char", "data": {"seTick": tick, "alpha": 1.0, "source": "Retarget" if swim else
                                                    "SeBones", "cameraMode": 1 if fp else 2, "entityPos": list(co),
                                                    "bones": bw, "head1stDrawn": not fp, "bodyDrawn": True,
                                                    "swimming": swim, "nonFinite": 0}, "t": qpc(0.016) / f})
        sn.append({"event": "s5.rig", "data": {"animator": "player_view", "isHuman": True, "requiredMapped": 15},
                   "t": qpc(0.01) / f})
        part("n7", "end", tick)
        # ---- teardown
        part("teardown", "begin", tick)
        sn.append({"event": "s5.teardown", "data": {"phase": "done"}, "t": qpc(0.1) / f})
        sn.append({"event": "s5.count", "data": {"phase": "after", "roots": 0, "preserved": 0, "meshes": 0},
                   "t": qpc(0.1) / f})
        part("teardown", "end", tick)
    else:
        se_ms = {"base-se.space": 8.33, "base-se.track-hover": 8.40, "base-se.track-sail": 8.40, "space": 8.40,
                 "track-hover": 8.50, "track-sail": 8.45, "prime": 9.5, "underwater": 8.6, "base-sn": None}
        for st in states:
            part("tp1.%s" % st, "begin", tick)
            mrows = []
            for s10 in range(300):          # 30 s at 10 Hz
                qq = qpc(0.1)
                tick += 6
                view, appr, mode, fps_frame = {"space": (0, 0, 0, 10), "track-hover": (1, 1, 1, 10),
                                               "track-sail": (1, 1, 1, 10), "prime": (1, 2, 2, 100),
                                               "underwater": (2, 0, 2, 140), "base-sn": (2, 0, 2, 140)}.get(
                    st, (0, 0, 0, 0))
                if st == "space" and mutant == "S5.M4":
                    mode, fps_frame = 2, 140
                sess.append({"ev": "sample", "part": "tp1.%s" % st, "qpc": qq, "qpcFrequency": f, "c1Tick": tick,
                             "c5": {"viewState": view, "approachMode": appr},
                             "c7": {"perfModeApplied": mode, "snFrame": int(s10 * fps_frame / 10),
                                    "frameMsP99": 11.0 if st in ("underwater", "base-sn") else None}})
                if s10 % 10 == 0:
                    sess.append({"ev": "mem", "part": "tp1.%s" % st, "qpc": qq, "commitTotal": 20e9,
                                 "commitLimit": 40e9, "physTotal": 32e9})
                    if se_ms.get(st):
                        se.append({"ev": "sample", "qpc": qq, "frameTimeAvgMs": se_ms[st], "frameTimeMaxMs": 20.0,
                                   "gameplayTick": tick})
                    if not st.startswith("base-se"):
                        cams = st in ("track-hover", "track-sail") and mutant == "S5.M5"
                        sn.append({"event": "s5.perf", "data": {"recipe": "Full" if mode == 2 else "Idle",
                                                                "camerasOn": cams or mode == 2, "fps": fps_frame,
                                                                "frameMsP99": 11.0, "qpc": qq}})
                    mrows.append({"time": "", "t_s": s10 / 10, "name": "Subnautica", "pid": 1, "cpu_pct": 3.0,
                                  "ws_mb": 2000, "gpu3d_pct": 1.0})
            with open(os.path.join(folder, "measure-tp1.%s.csv" % st), "w", encoding="utf-8", newline="") as fh:
                w = csv.DictWriter(fh, fieldnames=["time", "t_s", "name", "pid", "cpu_pct", "ws_mb", "gpu3d_pct"])
                w.writeheader()
                for r_ in mrows:
                    w.writerow(r_)
            part("tp1.%s" % st, "end", tick)
    sess.append({"ev": "end", "rc": None, "stats": {"sent": 1}, "unsupported": [], "qpc": qpc(0.1),
                 "utc": "2026-10-17T10:30:00.000Z"})
    _jsonl(os.path.join(folder, "se-standin.jsonl"), se)
    _jsonl(os.path.join(folder, "sn-standin.jsonl"), sn)
    _jsonl(os.path.join(folder, "%s-session.jsonl" % ("tp1" if kind == "tp1" else "s5")), sess)
    return folder

def synth_plugin_session(folder, mutant=None):
    """A synthetic session in the plugins' own row forms:
 decimal entity ids with *Str twins, nested 4x3 matrices, s5.part rootWorld + rel + local, one drawn point per
 s5.placed, s5.sub with ownerWorld/localD (SE) and unityWorld + alpha < 1 (Subnautica), s5.coh px/py, s5.model
 model/winding, s5.grid entity/modelTris, s5.edit eventTick/keys/parts (SE) in the launcher's n2.<edit> windows,
 s5.char per-second aggregates. Every required row must PASS through the aliases."""
    import random
    import site_v1
    import worldmap_ref as wm
    import s5_stream as X
    rnd = random.Random(11)
    os.makedirs(folder, exist_ok=True)
    f = 10_000_000
    q = [2 * 10 ** 12]

    def qpc(dt=0.0):
        q[0] += int(dt * f)
        return q[0]

    def utc():
        import datetime
        base = datetime.datetime(2026, 10, 17, 12, 0, 0, tzinfo=datetime.timezone.utc)
        return (base + datetime.timedelta(seconds=(q[0] - 2 * 10 ** 12) / f)).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"

    se, sn, sess = [], [], []
    site = {"C": list(site_v1.TEST_C), "Rs": site_v1.RS_EXPECTED}
    S = wm.Site(C=tuple(site["C"]), Rs=site["Rs"], U0=site_v1.ANCHOR["U0"], N0=site_v1.ANCHOR["N0"],
                psi_deg=site_v1.ANCHOR["psi_deg"], O=site_v1.ANCHOR["O"])
    sess.append({"ev": "start", "kind": "s5", "parts": ["n1", "n2", "n3", "n4", "n7"], "mutants": [mutant] if mutant
                 else [], "qpcFrequency": f, "qpc": qpc(), "utc": utc()})

    def part(name, phase, tick):
        sess.append({"ev": "part", "name": name, "phase": phase, "qpc": qpc(0.01), "tick": tick, "utc": utc()})

    T = S.sn_to_se((-1029.0, -150.0, -1100.0))
    E, U, N = S.frame_at(T)
    fwd = tuple(N[i] * math.cos(0.5) + E[i] * math.sin(0.5) for i in range(3))
    right = (fwd[1] * U[2] - fwd[2] * U[1], fwd[2] * U[0] - fwd[0] * U[2], fwd[0] * U[1] - fwd[1] * U[0])
    M = (right, tuple(U), tuple(-x for x in fwd), tuple(T))
    nested = [list(r) for r in M]
    piv = tuple(T[i] + 2.0 * U[i] for i in range(3))
    o, qq, s_ = W.wire_pose(S, M, piv)
    root, sub = 73014444032, 73014444099
    tick = 5000

    def apply(l):
        return tuple(W.f32(x) for x in W.sn_apply_k(o, qq, s_, 1.0, l))
    # N1: SE export rows (rootWorld, rel for the subgrid), Subnautica's single drawn point per part
    part("n1", "begin", tick)
    rel_sub = X.m_mul(X.m_rot("y", 0.3), X.m_trans((0.0, 7.5, 0.0)))
    for i in range(20):
        g = root if i % 3 else sub
        local = X.m_mul(X.m_rot("x", rnd.choice((0.0, math.pi / 2))), X.m_trans((2.5 * rnd.randint(-5, 5),
                                                                                   2.5 * rnd.randint(-3, 3),
                                                                                   2.5 * rnd.randint(-8, 8))))
        key = "0x%016x" % (0xabc000 + i)
        se.append({"t": utc(), "ev": "s5.part", "op": "export", "tick": tick, "entityId": float(g), "entityIdStr": str(g),
                   "rootId": float(root), "rootIdStr": str(root), "partKey": key, "local": X.m_rows12(local),
                   "rootWorld": nested, "rel": X.m_rows12(rel_sub) if g == sub else None, "pivot": list(piv),
                   "site": site, "o": list(o), "q": list(qq), "s": s_, "k": 1.0})
        if se[-1]["rel"] is None:
            del se[-1]["rel"]
        to_root = X.m_mul(local, rel_sub) if g == sub else local
        sn.append({"t": utc(), "event": "s5.placed", "ev": "s5.placed", "data": {
            "entity": "0x%016x" % g, "part": key, "drawn": list(apply(to_root[3])), "tick": tick + 1}})
        qpc(0.001)
    for g, n in ((root, 300), (sub, 20)):
        se.append({"t": utc(), "ev": "s5.edit", "kind": "cells", "tick": tick, "eventTick": tick - 1, "entityId": float(g),
                   "entityIdStr": str(g), "adds": 0, "removes": 0, "parts": n, "keys": []})
        sn.append({"t": utc(), "event": "s5.grid", "ev": "s5.grid", "data": {"entity": "0x%016x" % g, "parts": n,
                                                                              "modelTris": n * 10}})
    part("n1", "end", tick)
    # N2: SE's edit rows name their source; the launcher's windows name the edit
    count = 300
    for j, kd in enumerate(N2_EDITS):
        tick += 30
        part("n2.%s" % kd, "begin", tick)
        qpc(0.5)
        count += {"weld": 1, "stage": 0, "damage": 0, "paint": 0, "grind": -1}[kd]
        key = "0x%016x" % (0xed000 + j)
        se.append({"t": utc(), "ev": "s5.edit", "kind": "cells", "tick": tick + 1, "eventTick": tick,
                   "entityId": float(root), "entityIdStr": str(root), "parts": count, "keys": [key]})
        for sk in N2_SN_KINDS[kd]:
            qpc(0.05)
            sn.append({"t": utc(), "event": "s5.edit", "ev": "s5.edit", "data": {
                "entity": "0x%016x" % root, "part": key, "kind": sk, "presentedSeTick": tick + 4, "alpha": 0.3}})
        qpc(0.6)
        sn.append({"t": utc(), "event": "s5.grid", "ev": "s5.grid", "data": {"entity": "0x%016x" % root,
                                                                              "parts": count, "modelTris": count * 10}})
        part("n2.%s" % kd, "end", tick + 20)
    # N3: the rotor subgrid, interpolated frames (alpha 0.5), Unity matrices
    tick += 100
    part("n3", "begin", tick)
    part("n3.rotor", "begin", tick)
    for j in range(30):
        tk = tick + j
        rel = X.m_mul(X.m_rot("y", math.radians(tk)), X.m_trans((0.0, 7.5, 0.0)))
        se.append({"t": utc(), "ev": "s5.sub", "kind": "subgrid", "tick": tk, "ownerId": float(root),
                   "ownerIdStr": str(root), "partKey": "0x%016x" % sub, "local": X.m_rows12(rel),
                   "localD": X.m_rows12(rel), "ownerWorld": nested})
        if j == 0:
            continue
        a = 0.5
        rel0 = X.m_mul(X.m_rot("y", math.radians(tk - 1)), X.m_trans((0.0, 7.5, 0.0)))

        def world(lu):    # Unity-local child point -> world, interpolated between the two ticks (lerp of points)
            l = (lu[0], lu[1], -lu[2])
            p1 = W.sn_apply_k(o, qq, s_, 1.0, W_pt(rel, l))
            p0 = W.sn_apply_k(o, qq, s_, 1.0, W_pt(rel0, l))
            return tuple(p0[i] + (p1[i] - p0[i]) * a for i in range(3))
        w0 = world((0.0, 0.0, 0.0))
        cols = []
        for e in ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)):
            we = world(e)
            cols += [we[i] - w0[i] for i in range(3)]
        cols += list(w0)
        sn.append({"t": utc(), "event": "s5.sub", "ev": "s5.sub", "data": {
            "kind": "subgrid", "owner": "0x%016x" % root, "key": "0x%016x" % sub, "tick": tk, "alpha": a,
            "seLocal": X.m_rows12(rel), "unityWorld": cols}})
        qpc(1.0 / 60.0)
    part("n3.rotor", "end", tick + 30)
    part("n3", "end", tick + 30)
    # N4: Subnautica's px/py rows (S5.M1: one tick behind -> a sway)
    part("n4", "begin", tick)
    for fr in range(int(N4_SECONDS * 60) + 6):
        sway = 20.0 * math.cos(math.pi * fr / 60.0) if mutant == "S5.M1" else 0.0
        qpc(1.0 / 60.0)
        sn.append({"t": utc(), "event": "s5.coh", "ev": "s5.coh", "data": {"entity": "0x%016x" % root,
                                                                           "px": 1280.0 + sway, "py": 900.0,
                                                                           "presentedSeTick": tick + fr}})
    part("n4", "end", tick + 600)
    # N7: SE bone rows (top-level SE bone names), Subnautica per-second aggregates
    part("n7", "begin", tick)
    co, cq, cs = W.wire_pose(S, M, M[3])
    bones = {"SE_RigPelvis": X.m_rows12(X.m_trans((0.0, 1.0, 0.0))),
             "SE_RigHead": X.m_rows12(X.m_trans((0.0, 1.7, 0.05))), "SE_RigRPalm": X.m_rows12(X.m_trans((-0.57, 1.06, 0)))}
    for j in range(6):
        tick += 60
        se.append(dict({"t": utc(), "ev": "s5.char", "op": "bones", "tick": tick, "characterId": 77.0,
                        "characterIdStr": "77", "world": nested}, **bones))
        fp = j % 2 == 0
        bl = [{"index": i, "name": nme, "snWorld": list(W.sn_apply_k(co, cq, cs, 1.0, X.m_from12(v)[3])), "seAbs": v}
              for i, (nme, v) in enumerate(bones.items())]
        sn.append({"t": utc(), "event": "s5.char", "ev": "s5.char", "data": {
            "character": "0x4d", "source": "SeBones" if j < 3 else "Retarget", "frames": 60, "requiredMapped": 15, "swimming": 0 if j < 3
            else 60, "swimmingOnRetarget": 0 if j < 3 else 59, "nonFinite": 0, "cameraMode": 1 if fp else 2,
            "parts": [{"key": 1, "hiddenInFirstPerson": False, "drawn": True},
                      {"key": 2, "hiddenInFirstPerson": True, "drawn": not fp}],
            "tick": tick, "alpha": 1.0, "c1O": list(co), "c1Q": list(cq), "c1S": cs, "entityWorld": list(co),
            "bones": bl}})
        qpc(1.0)
    sn.append({"t": utc(), "event": "s5.rig", "ev": "s5.rig", "data": {"animator": "player_view", "isHuman": True,
                                                                       "snBoneMapped": 0}})
    part("n7", "end", tick)
    sess.append({"ev": "end", "rc": None, "stats": {}, "unsupported": [], "qpc": qpc(0.1), "utc": utc()})
    _jsonl(os.path.join(folder, "se-20261017.jsonl"), se)
    _jsonl(os.path.join(folder, "sn-20261017-123.jsonl"), sn)
    _jsonl(os.path.join(folder, "s5-session.jsonl"), sess)
    return folder

def selftest() -> int:
    import tempfile
    fails = []

    def check(ok, what, detail=""):
        print("  %-4s %s%s" % ("ok" if ok else "FAIL", what, (" (" + detail + ")") if detail and not ok else ""))
        if not ok:
            fails.append(what)

    print("S5 analyzer self-test")
    base = tempfile.mkdtemp(prefix="de-s5-an-")
    good = synth_session(os.path.join(base, "good"))
    rows, _b = evaluate(good)
    bad = [r for r in rows if r.get("required", True) and r["status"] != "PASS"]
    check(not bad, "faithful S5 session: every required row PASS (%d rows)" % len(rows),
          "; ".join("%s %s %s" % (r["id"], r["status"], r["measured"]) for r in bad[:4]))
    ids = {r["id"] for r in rows}
    want = {"S5.G1-E4", "S5.G1-E4.census", "S5.N1.C1", "S5.N1.C2", "S5.N1.C12", "S5.N4", "S5.N5", "S5.N6.500",
            "S5.N6.2000", "S5.N6.5000", "S5.N7a", "S5.N7b", "S5.N7c"} | {"S5.N2.%s" % k for k in N2_EDITS} | \
        {"S5.N3.%s" % k for k in N3_KINDS}
    check(want <= ids, "every S5 row present", ",".join(sorted(want - ids)))
    n6 = next(r for r in rows if r["id"] == "S5.N6.5000")
    check(n6["status"] == "PASS", "S5.N6.5000 only logs its p99 (21 ms is above the 2,000-block budget, not judged)")
    tp = synth_session(os.path.join(base, "tp1"), kind="tp1")
    rows_t, _b = evaluate(tp)
    bad = [r for r in rows_t if r.get("required", True) and r["status"] not in ("PASS", "INFO")]
    check(not bad and {"S5.T-P1.%s" % s for s in ("space", "track-hover", "track-sail", "prime", "underwater")} <=
          {r["id"] for r in rows_t}, "faithful T-P1 session: every state PASS (%d rows)" % len(rows_t),
          "; ".join("%s %s %s" % (r["id"], r["status"], r["measured"]) for r in bad[:4]))
    # each seeded failure turns its row red (and its.red row PASS)
    for m, kind in (("S5.M1", "s5"), ("S5.M2", "s5"), ("S5.M3", "s5"), ("S5.M4", "tp1"), ("S5.M5", "tp1")):
        d = synth_session(os.path.join(base, m.replace(".", "")), mutant=m, kind=kind)
        rs, _b = evaluate(d)
        red = [r["id"] for r in rs if r["id"] in MUTANT_ROWS[m] and r["status"] == "FAIL"]
        mrow = next((r for r in rs if r["id"] == m + ".red"), None)
        others = [r["id"] for r in rs if r["status"] == "FAIL" and r["id"] not in MUTANT_ROWS[m] and
                  not r["id"].endswith(".red") and not (m == "S5.M3" and r["id"] == "S5.N2.grind")]
        check(len(red) == len(MUTANT_ROWS[m]) and mrow and mrow["status"] == "PASS" and not others,
              "%s: %s red, %s.red PASS, nothing else red" % (m, ",".join(MUTANT_ROWS[m]), m),
              "red %s, other FAIL %s" % (red, others))
    # N1.C2 and N3 see a 2 mm displacement and a pose one tick behind
    d = synth_session(os.path.join(base, "shift"))
    p = os.path.join(d, "sn-standin.jsonl")
    recs = C.read_jsonl(p)
    for r in recs:
        if r.get("event") == "s5.placed":
            r["data"]["corners"][3][0] += 0.002
        if r.get("event") == "s5.sub" and int(r["data"]["partKey"], 16) == 0x500:
            r["data"]["seTick"] += 1           # the rotor evaluated against the next tick's matrix (1 deg at 10 m)
    _jsonl(p, recs)
    rs, _b = evaluate(d)
    st = {r["id"]: r["status"] for r in rs}
    check(st.get("S5.N1.C2") == "FAIL" and st.get("S5.N3.rotor") == "FAIL" and st.get("S5.N3.piston") == "PASS",
          "S5.N1.C2 red on a 2 mm corner shift; S5.N3.rotor red one tick off (piston untouched)",
          "%s %s %s" % (st.get("S5.N1.C2"), st.get("S5.N3.rotor"), st.get("S5.N3.piston")))
    # the plugins' own row forms (aliases): every required row PASS, then S5.M1 red through them
    d = synth_plugin_session(os.path.join(base, "plugin"))
    rs, _b = evaluate(d)
    want = {"S5.N1.C1", "S5.N1.C2", "S5.N3.rotor", "S5.N4", "S5.N7a", "S5.N7b", "S5.N7c"} | \
        {"S5.N2.%s" % k for k in N2_EDITS}
    st = {r["id"]: r for r in rs}
    bad = [i for i in sorted(want) if i not in st or st[i]["status"] != "PASS"]
    check(not bad, "the plugins' row forms (decimal ids, nested matrices, one drawn point, unityWorld at alpha 0.5, "
          "px/py, launcher windows, per-second character aggregates): every row PASS",
          "; ".join("%s %s %s" % (i, st[i]["status"], st[i]["measured"]) if i in st else i + " absent" for i in bad))
    d = synth_plugin_session(os.path.join(base, "pluginM1"), mutant="S5.M1")
    rs, _b = evaluate(d)
    st = {r["id"]: r["status"] for r in rs}
    check(st.get("S5.N4") == "FAIL" and st.get("S5.M1.red") == "PASS", "the plugins' forms: S5.M1 turns S5.N4 red")
    # S7a: rows naming the placement ghost (SE decimal string, SN hex) are skipped; an ordinary grid's row is kept
    gh_se = parse_record({"ev": "s5.grid", "entityId": float(S7A_GHOST_ENTITY_ID), "entityIdStr": str(S7A_GHOST_ENTITY_ID),
                          "parts": 1}, "se")
    gh_sn = parse_record({"event": "s5.grid", "data": {"entity": "0x%016x" % S7A_GHOST_ENTITY_ID, "parts": 1}}, "sn")
    keep = parse_record({"event": "s5.grid", "data": {"entity": "0x%016x" % 0x44455335000001, "parts": 1}}, "sn")
    check(gh_se is None and gh_sn is None and keep is not None,
          "S7a: s5.* rows naming the placement ghost id are skipped, an ordinary grid's row is kept")
    # an empty folder: MISSING rows, no crash
    e = os.path.join(base, "empty")
    os.makedirs(e, exist_ok=True)
    _jsonl(os.path.join(e, "s5-session.jsonl"), [{"ev": "start", "kind": "s5", "parts": ["g1e4", "n1", "n4"]},
                                                 {"ev": "part", "name": "n4", "phase": "begin", "qpc": 1}])
    rs, _b = evaluate(e)
    check(all(r["status"] in ("MISSING", "INFO") for r in rs) and any(r["id"] == "S5.G1-E4" for r in rs),
          "an empty session: MISSING rows, no analyser error")
    print("S5 ANALYZER SELFTEST %s (%d failed)" % ("PASS" if not fails else "FAIL", len(fails)))
    return 0 if not fails else 1

def main(argv) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S5 oracle table")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--baseline", default=None, help="a T-P1 base-se folder (or its s5-results.json)")
    ap.add_argument("--baseline-logs", default=None, help="a folder whose LogOutput.log / Player.log are the baseline")
    ap.add_argument("--lenient", action="store_true", help="MISSING does not fail")
    ap.add_argument("--json", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder:
        ap.error("folder or --selftest")
    rows, base = evaluate(a.folder, a.baseline, a.baseline_logs)
    ok = write_results(a.folder, rows, not a.lenient, {"tp1Baseline": base})
    print(json.dumps(rows, indent=1, default=str) if a.json else C.table(rows))
    print("S5 ORACLE %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
