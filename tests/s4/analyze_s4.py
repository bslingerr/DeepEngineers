"""S4 "Planet + terrain": the oracle over a launcher `s4` session folder (stdlib only; `python -I`).

 python -I tests/s4/analyze_s4.py tests/out/s4/<stamp> [--sn-log FILE...] [--octrees DIR] [--baseline DIR]
 [--no-plugin-log FILE] [--lenient] [--json OUT]
 python -I tests/s4/analyze_s4.py --selftest
Exit 0 = every required row PASS, 1 = a red row, 2 = usage.

The launcher's s4 session (src/DeepEngineers.Launcher/launcher.py, `s4 --mode S4se|S4both`) drives the SE plugin's
`de.terrain` command and the Subnautica plugin's `de.s4` raycast service over C11, records every command and reply in
s4-session.jsonl, and collects the SE JSONL (se-*.jsonl), the SE game log, the Subnautica JSONL and BepInEx log. This
script joins them with the REFERENCE HEIGHTS of Subnautica's own terrain: tools/worldmap/worldmap_ref.py
(OctreeColumns: the trilinear field D of CONTRACT 3.1 "Content mapping", SAMPLE_OFFSET 0.5, read in place from the
user's CompiledOctreesCache; exact roots per column) at the SN column of every SE ray (an SE vertical ray is radial, so
it maps through F to one SN column; site v1, tools/worldmap/site_v1.py). When tools/sn-octrees/sn_field.py is importable, a sample of columns is cross-checked against it (row S4.REF, INFO).

ROWS (ROADMAP S4 oracle; * = INFO or OTHER, not required)
 S4.SOURCE the launcher's own read-only listing of the octree folder vs the pins (build 20241558, 5,416 files,
 1,203,085,204 bytes, rows 7-19, manifestHash 0x2cc277ce91763285, index.txt lines 2-5) and vs SE's reply
 S4.GATE de.terrain status: gate OK, provider registered, source = the pins (index ok), terrainUp, no fail-safe;
 the fresh `gate` measure agrees; "DE.Terrain OK tiles=34 siteVersion=1" in the SE log
 S4.FAILSAFE the fail-safe state agrees with the gate (OK: inactive; REFUSE: active, 0 tiles; "DE.Terrain FAILSAFE ON")
 S4.TILES de.terrain tiles: exactly the 34 slots of CONTRACT 3 (rows 0 and 1 full, row 2 (1,0) and (1,1)), each
 exists, not closed, save false; min corners = C + the lattice min corners (one C for all, 1e-6 m); each
 map placed at its min corner (placedMin = min: S4.M2 moves it)
 S4.IDS each id = MyEntityIdentifier.ConstructId(ENTITY, 0x44455400000000 | 1 << 16 | k << 8 | j << 4 | i);
 34 "DE.Terrain TILE i,j,k id=0x... " lines
 S4.N3 de.terrain n3 (the in-game provider self-check) passes
 S4.A-T0b (part at0b, the in-SE fallback) de.terrain at0b passes (S4.M6 turns it red)
 S4.A-T1.1 grid rays (64 x 64 at 2.5 m, prefetched): |castSnY - ref| P95 <= 0.25 m and max <= 1.0 m on columns
 WITHOUT OVERHANGS (exactly one entry crossing of D between fromY and toY) whose cells were all built;
 SE hits where the reference has no terrain ("false hits") must be 0
 S4.A-T1.1h holes: grid rays with every cell built over terrain (an entry crossing in range) that hit nothing or hit
 more than 1.0 m below the first entry (through the crust): 0
 S4.A-T1.1u harness: rays with cellsBuilt < cells or an error: 0 (an unbuilt cell is a harness error, not a hole;
 S4.M7 turns this row red and S4.A-T1.1h must stay green)
 S4.A-T1.1r the 1,000 random rays: the 1.1 and 1.1h verdicts together
 S4.A-T1.1b bake vs provider: rays of set "bake" against the provider rays (part bake: the same columns with the bake
 map removed): |delta| <= 1 mm on storageSnY (castSnY when absent)
 S4.A-T1.2d drops: each 1-block large grid rests within +-0.3 m of the reference band under its footprint
 S4.A-T1.2w walk: the character's feet never more than 0.5 m below the reference
 S4.A-T1.3m tile RAM <= 1.5 GB (the plugin's figure, else the SE private-bytes delta against --baseline)
 S4.A-T1.3r ReadRange p99 <= 2 ms (de.terrain status readRange.p99us)
 S4.A-T1.3c < 1 ms of ReadRange per 8 m physics cell: the s4.n3 cellP99us; else s4.cell readUs/readMs; else the p99 proxy
 S4.A-T1.1x 0 rays whose own Storage.Intersect answer was Disjoint/Contains on a non-uniform cell
 S4.A-T1.1 also needs >= 90 % of the grid rays classified (not unbuilt/error/no-reference/start-inside) and >= 64
 single-entry columns; S4.A-T2 needs >= 90 % of the verified Subnautica probes paired with an SE hit
 S4.A-T1.3f frames > 5 ms <= 1 % during the 100 m/s fly-over (s4.perf)
 S4.A-T1.3s* INFO: shape-build time per 8 m cell (s4.cell buildMs; decides 1024 vs 2048 tiles)
 S4.A-T2 both games: Subnautica's layer-30 hit vs SE CastRay at the same columns: P95 |delta| <= 0.3 m
 S4.A-T2n* INFO: Subnautica collider vs render mesh (renderDelta) at those columns: the native budget
 S4.WM-6a the mirrored cube's bottom face vs Subnautica's terrain under it: within the A-T2 deviation measured at
 that column + 0.05 m, and <= 0.3 m
 S4.WM-6b the mirrored bottom face vs SE's resting bottom mapped through F: <= 0.05 m (the map check)
 S4.N1 the character teleported onto the seabed: feet vs the reference (and vs the plugin's storageSnY, and in
 S4both vs Subnautica's layer-30 hit): <= 0.3 m
 S4.N2.<path> edits blocked, per path (cutout, shipdrill, explosion, meteor, voxelhand, writerange): blocked, content
 hash unchanged, IsRangeModified false, no ore, "DE.Terrain EditBlocked <path>" logged
 S4.G2-E2a the save holds no tile (no DE.Terrain storage name, no DE.Terrain*.vx2, no tile entity id as a voxel map)
 S4.G2-E2b a resting grid in the footprint moved < 1 cm between the save before the reload and the save >= 10 s
 the stricter form of "< 1 cm in the first 10 s")
 S4.G2-E2c* OTHER unless --no-plugin-log: the world loads without the plugin (no KeyNotFoundException)
 S4.G2-E3b a landing-gear-locked ship is still locked after the reload, AttachedEntityId = a tile id of this session
 S4.G2-E5* INFO: what SE did with the world mod removed (CONTRACT 7 "Uninstall" HYPOTHESIS)
 S4.
 S4.
 S4.REF* INFO: the reference source, sample offset, columns evaluated, the sn_field.py cross-check
 S4.M<n>.red in a session with seeded failures: the rows the mutant must turn red are FAIL (those rows and the rows
 the mutant drags along are reported but not required; rows that must stay green are checked here)

SE INPUTS (the SE plugin's src/DeepEngineers.SE/Terrain S4Commands / S4Harness,: se-*.jsonl rows {"t",
"ev": "s4.<name>", fields top-level (or in "data", or text "s4.<name> {json}")}; log lines {"ev": "log", "msg":
"DE.Terrain..."} and the game log's lines). Field names read (first present wins; older aliases kept):
 s4.ray set (grid|random|bake: a ray over the bake map writes a provider row and a "bake" row), snX, snZ, fromY, toY,
 prefetch, cells, cellsBuilt, waitMs, castHit, castSe [x,y,z], castSnY, storageHit, storageSnY, tile, err
 s4.cell built, waitedMs, buildMs (null in the plugin today: S4.A-T1.3s then INFO "-"), readUs | readMs
 s4.drop snX, snZ, restSnY (the bottom-face centre), restSn [x,y,z] (where it came to rest: the reference column),
 lowestCornerSnY, surfaceSnY, err (the plugin closes the dropped grids when it reports)
 s4.walk per sample {feetSn [x,y,z], surfaceSnY} (or {snX, snZ, feetSnY}), then {done: true, maxBelow}
 s4.n1 feetSn [x,y,z], storageSnY, delta (de.terrain n1: the standing character vs the storage under it)
 s4.perf frames, fracHitches | hitchesOver5ms (frames > median + 5 ms), framesOver5ms (absolute, information),
 privateBefore / privateAfter
 s4.stats privateBefore / privateAfter, calls, p99us, physP99us, physMaxUs
 s4.edit path, blocked, count, hashUnchanged, hashBefore / hashAfter (per-box lists), modified, ore ({percent,
 material, voxels} for cutout); a second row with final: true 2 s later (the witness judged)
 s4.n3 pass, violations; s4.at0b per (height, lod): vertices, meanErr, maxAbsErr, quantization, isoMedian
de.terrain replies (session "cmd" events): status {gate, condition, reason, tiles, terrainUp, failSafe {active,
reason, saveBlocked}, provider {registered, error}, source {path, build, files, bytes, rows, manifestHash, indexOk,...}, readRange {calls, p50us, p99us, maxUs, physP99us, physMaxUs}, ram {privateBefore, privateAfter}, harness
{job, state}}; gate {match, field, found, expected, manifest}; tiles {tiles: [{i, j, k, id, idDec, min, placedMin,
exists, closed, save, bodies}]}; hash; crater / edit (pending; the final row follows); n1; n3 {pass, violations};
at0b {rows}. Job ends are the lines "DE.Terrain AT1|DROP|WALK|PERF done|aborted" and "DE.Terrain BAKE ready".

SUBNAUTICA INPUTS: the launcher's "sn_probe" session events (de.s4 status results: i, x, z, hit, y, collider, layer,
camY, waitMs, renderY, renderDelta, renderNote, error); else sn-*.jsonl / --sn-log rows "s4.probe" or text lines
"DE.S4 PROBE i=.. x=.. z=.. hit=.. y=.. collider=.. layer=30 camY=.. waitMs=.. renderY=.. delta=.."; "DE.Mirror placed
entity=0x.. tick=.. cornersSn=x,y,z;.." lines (the mirrored cube's 8 corners in SN metres).

SESSION (s4-session.jsonl, launcher.py s4): start {mode, parts, mutants, world, octrees, spots, qpcFrequency,...};
plan; cmd {side, name, args, reply, ms, label, part}; part_start / part_end {name}; rays {part, label, expected, rows};
drops {part, rows}; walks {part, label, rows}; perf {part, rows}; edit {path, reply, row}; sn_probe {part, request,
results}; wm6 {spot, drop, probe, placement}; n1 {points, walks, probes}; world_scan {when, folder, scan}
{step, prompt, done}; source {measure}; sample {label, se {privateBytes, workingSet}}; end {rc, stats}.
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import shutil
import sys
import tempfile

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE, os.path.join(_ROOT, "tests", "s2"), os.path.join(_ROOT, "tools", "worldmap"),
           os.path.join(_ROOT, "tools", "sn-octrees")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import s2_common as C  # noqa: E402
import worldmap_ref as W  # noqa: E402
import site_v1 as SV  # noqa: E402

# ------------------------------------------------------------------------------------------------------ constants
SITE_VERSION = 1
PINS = {"build": 20241558, "files": 5416, "bytes": 1203085204, "rows": [7, 19], "manifestHash": 0x2cc277ce91763285,
        "index": ["4096 3200 4096", "128 100 128", "32", "5 5 5"]}
TILE_SLOTS = tuple([(i, j, k) for k in (0, 1) for j in range(4) for i in range(4)] + [(1, 0, 2), (1, 1, 2)])
EDIT_PATHS = ("cutout", "shipdrill", "explosion", "meteor", "voxelhand", "writerange")
AT1_P95, AT1_MAX = 0.25, 1.0
AT1_MIN_COVERAGE, AT1_MIN_SINGLE = 0.9, 64     #  no verdict on a handful of rays
AT2_MIN_SHARE = 0.9                            #  paired columns / verified probes
HOLE_BELOW_M = 1.0          # a hit this far below the first entry went through the crust
BAKE_TOL = 0.001
DROP_TOL = 0.3
DROP_HALF = 1.25            # a large block: 2.5 m, footprint +-1.25 m
WALK_SINK = 0.5
RAM_MAX = 1.5 * 1024 ** 3
RR_P99_US = 2000.0
CELL_US = 1000.0
FRAMES_5MS = 0.01
AT2_P95 = 0.3
WM6_EXTRA = 0.05
WM6_MAP = 0.05
N1_TOL = 0.3
E2B_MOVE = 0.01
TILE_TOL = 1e-6
FOOTPRINT = 2100.0          # CONTRACT 7: max(|x|, |z|) <= 2,100 m and y < 0 (SN)
# seeded failures: (rows that must turn red, rows the mutant drags along, rows that must stay green)
MUTANT_ROWS = {
    "S4.M2": (("S4.A-T1.1",), ("S4.A-T1.1r", "S4.A-T1.1h", "S4.A-T1.2d", "S4.A-T1.2w", "S4.N1", "S4.A-T1.1b",
                               "S4.TILES"), ()),
    "S4.M3": (("S4.A-T1.1x",), (), ("S4.A-T1.1h", "S4.A-T1.1")),   #  prefetch never calls Intersect
    "S4.M4": (("S4.G2-E2a",), (), ()),
    "S4.M5": (("S4.GATE",), ("S4.TILES", "S4.IDS", "S4.N3", "S4.SOURCE"), ("S4.FAILSAFE",)),
    "S4.M6": (("S4.A-T0b",), ("S4.A-T1.1", "S4.A-T1.1r", "S4.A-T1.1h", "S4.A-T1.1b", "S4.A-T1.2d", "S4.A-T1.2w",
                              "S4.N1", "S4.A-T2", "S4.WM-6a"), ()),
    "S4.M7": (("S4.A-T1.1u",), ("S4.A-T1.1", "S4.A-T1.1r"), ("S4.A-T1.1h",)),
    "S4.M8": (("S4.A-T1.1h",), ("S4.A-T1.1", "S4.A-T1.1r", "S4.N1", "S4.A-T1.2d", "S4.A-T1.2w"), ()),
    "S4.M8b": (("S4.N3",), (), ("S4.A-T1.1h",)),
    "S4.M10": (("S4.G2-E3b",), ("S4.IDS",), ()),
}
for _p in EDIT_PATHS:
    MUTANT_ROWS["S4.M9.%s" % _p] = (("S4.N2.%s" % _p,), (), ())

_KV = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(\S+)")
_RX_PROBE = re.compile(r"DE\.S4 PROBE (.*)$")
_RX_MIRROR = re.compile(r"DE\.Mirror placed entity=(0x[0-9a-fA-F]+) tick=(\d+).*?cornersSn=(\S+)")
_RX_TILE = re.compile(r"DE\.Terrain TILE (\d+),(\d+),(\d+) id=(0x[0-9a-fA-F]+)")
_RX_EDIT = re.compile(r"DE\.Terrain EditBlocked (\w+)")

def tile_id(i, j, k, site_version=SITE_VERSION):
    """CONTRACT 3 terrain.entityId: MyEntityIdentifier.ConstructId(ENTITY = 1, 0x44455400000000 | sv<<16 | k<<8 | j<<4 | i)."""
    n = 0x44455400000000 | site_version << 16 | k << 8 | j << 4 | i
    return (n & 0x00FFFFFFFFFFFFFF) | (1 << 56)

TILE_IDS = {tile_id(*s): s for s in TILE_SLOTS}

# ------------------------------------------------------------------------------------------------------ helpers
def pick(d, *keys):
    for k in keys:
        if isinstance(d, dict) and d.get(k) is not None:
            return d[k]
    return None

def fnum(d, *keys):
    for k in keys:
        v = d.get(k) if isinstance(d, dict) else None
        if isinstance(v, bool):
            continue
        if isinstance(v, (int, float)) and v == v:
            return float(v)
        if isinstance(v, str):
            try:
                return float(int(v, 16)) if v.lower().startswith("0x") else float(v)
            except ValueError:
                continue
    return None

def fbool(d, *keys):
    return C.flag(d, *keys)

def as_int(v):
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, int):
        return v
    if isinstance(v, float):
        return int(v)
    if isinstance(v, str):
        s = v.strip()
        try:
            return int(s, 16) if s.lower().startswith("0x") else int(s)
        except ValueError:
            return None
    return None

def rows_pair(v):
    """[min, max] from [7, 19], "7..19" or "7-19"."""
    if isinstance(v, (list, tuple)) and len(v) == 2:
        return [as_int(v[0]), as_int(v[1])]
    m = re.match(r"\s*(\d+)\D+(\d+)\s*$", str(v))
    return [int(m.group(1)), int(m.group(2))] if m else None

def ore_amount(ore):
    """Total ore of an edit result: a number, a {material: amount} map, a list of those, or text."""
    if ore is None or isinstance(ore, bool):
        return 1.0 if ore is True else 0.0
    if isinstance(ore, (int, float)):
        return abs(float(ore))
    if isinstance(ore, dict):
        return sum(ore_amount(v) for v in ore.values())
    if isinstance(ore, (list, tuple)):
        return sum(ore_amount(v) for v in ore)
    if isinstance(ore, str):
        try:
            return abs(float(ore))
        except ValueError:
            return 0.0 if ore.strip().lower() in ("", "none", "0", "no") else 1.0
    return 1.0

def vec3(v):
    if isinstance(v, (list, tuple)) and len(v) >= 3 and all(isinstance(c, (int, float)) and not isinstance(c, bool)
                                                            for c in v[:3]):
        return (float(v[0]), float(v[1]), float(v[2]))
    if isinstance(v, str):
        parts = [p for p in re.split(r"[,\s()\[\]]+", v) if p]
        try:
            if len(parts) >= 3:
                return (float(parts[0]), float(parts[1]), float(parts[2]))
        except ValueError:
            return None
    return None

def pct(values, p):
    return C.percentile(values, p)

def fmt(v, f="%.3f"):
    return f % v if isinstance(v, (int, float)) else "-"

def _kvline(text):
    out = {}
    for k, v in _KV.findall(text):
        out[k] = C._value(v)
    return out

def _utc_s(u):
    return C.Session._utc_seconds(u)

# ------------------------------------------------------------------------------------------------------ the folder
class S4Session:
    """A collected s4 session folder (or a synthetic one of --selftest)."""

    def __init__(self, folder, sn_logs=(), octrees=None, oracle=None, baseline=None, no_plugin_log=None):
        self.dir = folder
        self.session = C.read_jsonl(os.path.join(folder, "s4-session.jsonl"))
        self.start = next((r for r in self.session if r.get("ev") == "start"), {})
        self.parts = list(self.start.get("parts") or [])
        self.mutants = list(self.start.get("mutants") or [])
        self.mode = self.start.get("mode") or "S4se"
        self.se_files = sorted(glob.glob(os.path.join(folder, "se-*.jsonl")))
        self.sn_files = sorted(glob.glob(os.path.join(folder, "sn-*.jsonl")))
        self.se_raw = C.read_jsonl(self.se_files)
        self.sn_raw = C.read_jsonl(self.sn_files)
        self.se_ev = C.events(self.se_raw, ("s4.",))
        self.sn_ev = C.events(self.sn_raw, ("s4.",))
        self.se_text = C.text_lines(self.se_raw)
        for p in sorted(glob.glob(os.path.join(folder, "SpaceEngineers_*.log"))):
            with open(p, encoding="utf-8", errors="replace") as f:
                self.se_text += [ln.rstrip("\n") for ln in f if any(k in ln for k in (
                    "DE.Terrain", "KeyNotFoundException", "DE.Site", "DE_4546B", "Exception"))]
        self.sn_text = C.text_lines(self.sn_raw)
        extra = list(sn_logs or [])
        p = os.path.join(folder, "BepInEx-LogOutput.log")
        if os.path.exists(p) and p not in extra:
            extra.append(p)
        for path in extra:
            if not path or not os.path.exists(path):
                continue
            with open(path, encoding="utf-8", errors="replace") as f:
                for ln in f:
                    ln = ln.strip()
                    if ln.startswith("{"):
                        try:
                            rec = json.loads(ln)
                        except ValueError:
                            rec = None
                        if isinstance(rec, dict):
                            e = C.normalise(rec, ("s4.",))
                            if e:
                                self.sn_ev.append(e)
                            for k in ("msg", "text", "line"):
                                if isinstance(rec.get(k), str):
                                    self.sn_text.append(rec[k])
                                    break
                            continue
                    self.sn_text.append(ln)
        self.octrees = octrees or self.start.get("octrees")
        self._oracle = oracle
        self.oracle_error = None
        self.baseline = baseline
        self.no_plugin_log = no_plugin_log
        self._ref_cache = {}
        self.site = self._site()

    # ------------------------------------------------------------------------------------------- lookups
    def ev(self, name):
        return [r for r in self.session if r.get("ev") == name]

    def cmds(self, name=None, action=None, part=None):
        out = []
        for r in self.session:
            if r.get("ev") != "cmd":
                continue
            if name and r.get("name") != name:
                continue
            args = r.get("args") or {}
            if action and (not isinstance(args, dict) or args.get("action") != action):
                continue
            if part and r.get("part") != part:
                continue
            out.append(r)
        return out

    def reply(self, action, last=True, part=None):
        rs = [r.get("reply") for r in self.cmds("de.terrain", action, part) if isinstance(r.get("reply"), dict)]
        if not rs:
            return None
        return rs[-1] if last else rs[0]

    def se_rows(self, name):
        return [e for e in self.se_ev if e.get("_ev") == name]

    def ran(self, part):
        return not self.parts or part in self.parts

    @property
    def oracle(self):
        if self._oracle is None and self.oracle_error is None:
            try:
                self._oracle = W.OctreeColumns(self.octrees)
            except (OSError, ImportError) as e:
                self.oracle_error = "%s: %s" % (type(e).__name__, e)
        return self._oracle

    def _site(self):
        """Site v1 with C read from the tiles reply (tile min corner - lattice offset), else the S1 lab centre."""
        c = None
        rep = self.reply("tiles")
        if isinstance(rep, dict):
            for t in rep.get("tiles") or []:
                m = vec3(pick(t, "min", "minWorld"))
                i, j, k = as_int(t.get("i")), as_int(t.get("j")), as_int(t.get("k"))
                if m and None not in (i, j, k):
                    off = SV.tile_min(i, j, k)
                    c = (m[0] - off[0], m[1] - off[1], m[2] - off[2])
                    break
        self.C = c or SV.TEST_C
        self.C_source = "tiles reply" if c else "site_v1.TEST_C (the DE Lab planet centre)"
        return W.Site(C=self.C, Rs=SV.RS_EXPECTED, U0=SV.ANCHOR["U0"], N0=SV.ANCHOR["N0"], psi_deg=SV.ANCHOR["psi_deg"],
                      O=SV.ANCHOR["O"])

    def crossings(self, x, z, y_top, y_bottom):
        key = (round(x, 4), round(z, 4), round(y_top, 3), round(y_bottom, 3))
        got = self._ref_cache.get(key)
        if got is None:
            o = self.oracle
            got = o.crossings(x, z, y_top, y_bottom) if o is not None else None
            self._ref_cache[key] = got
        return got

    def ref(self, x, z, y_top=200.0, y_bottom=-1950.0):
        cr = self.crossings(x, z, y_top, y_bottom)
        if cr is None:
            return None
        return next((y for y, k in cr if k > 0), None)

    def text_has(self, lines, pattern):
        rx = re.compile(pattern)
        return [t for t in lines if rx.search(t)]

# ------------------------------------------------------------------------------------------------------ rows
def evaluate_source(s, rows):
    src = next((r for r in reversed(s.ev("source"))), None)
    if src is None:
        return
    m = src.get("measure") or {}
    diffs = []
    for k in ("build", "files", "bytes"):
        if m.get(k) != PINS[k]:
            diffs.append("%s %s (pin %s)" % (k, m.get(k), PINS[k]))
    if list(m.get("rows") or []) != PINS["rows"]:
        diffs.append("rows %s (pin %s)" % (m.get("rows"), PINS["rows"]))
    if as_int(m.get("manifestHash")) != PINS["manifestHash"]:
        diffs.append("manifestHash %s (pin 0x%x)" % (m.get("manifestHash"), PINS["manifestHash"]))
    if list(m.get("index") or []) != PINS["index"]:
        diffs.append("index.txt lines %s" % m.get("index"))
    if m.get("other"):
        diffs.append("%d other file(s) in the folder" % len(m.get("other")))
    st = s.reply("status") or {}
    se_src = st.get("source") if isinstance(st.get("source"), dict) else {}
    agree = []
    for k in ("build", "files", "bytes"):
        if se_src.get(k) is not None and as_int(se_src.get(k)) != m.get(k):
            agree.append("SE %s %s" % (k, se_src.get(k)))
    if se_src.get("manifestHash") is not None and as_int(se_src.get("manifestHash")) != as_int(m.get("manifestHash")):
        agree.append("SE manifestHash %s" % se_src.get("manifestHash"))
    C.row(rows, "S4.SOURCE", "FAIL" if diffs or agree else "PASS",
          "build %s files %s bytes %s" % (m.get("build"), m.get("files"), m.get("bytes")),
          "= the pins (and SE's)", "octree folder measured by the launcher (read-only listing): %s" % (
              "; ".join(diffs + agree) if diffs or agree else "every pin equal, hash 0x%x" % PINS["manifestHash"]),
          "gate", path=m.get("path"), diffs=diffs, seDisagrees=agree)

def evaluate_gate(s, rows):
    if not s.ran("gate"):
        return
    st = s.reply("status")
    if st is None:
        C.row(rows, "S4.GATE", "MISSING", "no de.terrain status reply", "gate OK", "terrain gate (CONTRACT 1.2)", "gate")
        C.row(rows, "S4.FAILSAFE", "MISSING", "no status", "agrees with the gate", "fail-safe state", "gate")
        return
    if st.get("ok") is False and st.get("gate") is None:
        C.row(rows, "S4.GATE", "MISSING", "status refused: %s" % str(st.get("error"))[:22], "gate OK",
              "terrain gate (CONTRACT 1.2): de.terrain status answered %s" % st.get("error"), "gate")
        C.row(rows, "S4.FAILSAFE", "MISSING", "no status", "agrees with the gate", "fail-safe state", "gate")
        return
    gate = str(st.get("gate") or "").upper()
    src = st.get("source") if isinstance(st.get("source"), dict) else {}
    prov = st.get("provider") if isinstance(st.get("provider"), dict) else {}
    fs = st.get("failSafe") if isinstance(st.get("failSafe"), dict) else {}
    probs = []
    if gate != "OK":
        probs.append("gate %s %s %s" % (gate or "?", st.get("condition") or "", st.get("reason") or ""))
    if prov.get("registered") is not True:
        probs.append("provider not registered (%s)" % prov.get("error"))
    for k in ("build", "files", "bytes"):
        if as_int(src.get(k)) != PINS[k]:
            probs.append("source %s %s" % (k, src.get(k)))
    if as_int(src.get("manifestHash")) != PINS["manifestHash"]:
        probs.append("manifestHash %s" % src.get("manifestHash"))
    if src.get("rows") is not None and rows_pair(src.get("rows")) != PINS["rows"]:
        probs.append("rows %s" % src.get("rows"))
    if src.get("indexOk") is False:
        probs.append("index.txt not ok")
    if st.get("terrainUp") is not True:
        probs.append("terrainUp %s" % st.get("terrainUp"))
    if fs.get("active") is True:
        probs.append("fail-safe active (%s)" % fs.get("reason"))
    fresh = s.reply("gate")
    if isinstance(fresh, dict) and fresh.get("ok") is False:
        probs.append("fresh gate measure: %s" % (fresh.get("error") or fresh.get("reason")))
    elif isinstance(fresh, dict):
        fsrc = next((fresh[k] for k in ("source", "manifest") if isinstance(fresh.get(k), dict)), fresh)
        if fsrc.get("files") is not None and as_int(fsrc.get("files")) != PINS["files"]:
            probs.append("fresh gate files %s" % fsrc.get("files"))
        if fresh.get("match") is False or fresh.get("matches") is False:
            probs.append("fresh gate: no match (%s)" % (fresh.get("field") or fresh.get("reason")))
    ok_line = s.text_has(s.se_text, r"DE\.Terrain OK tiles=34 siteVersion=%d" % SITE_VERSION)
    if not ok_line and gate == "OK":
        probs.append("no 'DE.Terrain OK tiles=34 siteVersion=1' line")
    C.row(rows, "S4.GATE", "PASS" if not probs else "FAIL", "gate %s tiles %s" % (gate or "?", st.get("tiles")),
          "OK, 34, pins", "terrain gate (CONTRACT 1.2 conditions 1-7): %s" % ("; ".join(probs) if probs else
                                                                             "provider registered, source = pins"),
          "gate", problems=probs, condition=st.get("condition"), reason=st.get("reason"))
    tiles_n = as_int(st.get("tiles"))
    fs_line = s.text_has(s.se_text, r"DE\.Terrain FAILSAFE ON")
    if gate == "OK":
        good = fs.get("active") is not True and not fs_line
        what = "gate OK: fail-safe inactive, no FAILSAFE line"
    elif gate in ("REFUSE",):
        good = fs.get("active") is True and (tiles_n or 0) == 0 and st.get("terrainUp") is not True and bool(fs_line)
        what = "gate REFUSE: fail-safe active, 0 tiles, terrainUp 0, 'DE.Terrain FAILSAFE ON' logged"
    elif gate == "SKIP":
        good = fs.get("active") is not True and (tiles_n or 0) == 0
        what = "gate SKIP: no tiles, no fail-safe (not a DE world / multiplayer)"
    else:
        good, what = False, "gate %s" % (gate or "not evaluated")
    C.row(rows, "S4.FAILSAFE", "PASS" if good else "FAIL", "active %s saveBlocked %s" % (fs.get("active"),
          fs.get("saveBlocked")), "agrees with the gate", what, "gate", line=fs_line[:1])

def evaluate_tiles(s, rows):
    if not s.ran("tiles"):
        return
    rep = s.reply("tiles")
    if not isinstance(rep, dict) or not isinstance(rep.get("tiles"), list):
        C.row(rows, "S4.TILES", "MISSING", "no de.terrain tiles reply", "34 tiles", "tile lattice (CONTRACT 3)", "tiles")
        C.row(rows, "S4.IDS", "MISSING", "no tiles reply", "34 ids", "deterministic entity ids", "tiles")
        return
    tl = rep["tiles"]
    slots, probs, cs, id_bad = {}, [], [], []
    for t in tl:
        i, j, k = as_int(t.get("i")), as_int(t.get("j")), as_int(t.get("k"))
        if None in (i, j, k):
            probs.append("tile without i,j,k")
            continue
        slots[(i, j, k)] = t
        if t.get("exists") is False:
            probs.append("%d,%d,%d missing" % (i, j, k))
        if t.get("closed") is True:
            probs.append("%d,%d,%d closed" % (i, j, k))
        if t.get("save") is not False:
            probs.append("%d,%d,%d save=%s" % (i, j, k, t.get("save")))
        m = vec3(pick(t, "min", "minWorld"))
        if m is None:
            probs.append("%d,%d,%d no min" % (i, j, k))
        else:
            off = SV.tile_min(i, j, k)
            cs.append((m[0] - off[0], m[1] - off[1], m[2] - off[2]))
            pm = vec3(t.get("placedMin"))
            if pm is not None and max(abs(pm[a] - m[a]) for a in range(3)) > TILE_TOL:
                probs.append("%d,%d,%d placed %.3f m off its lattice min" % (i, j, k, math.dist(pm, m)))
        tid = as_int(pick(t, "idDec", "id"))
        if tid != tile_id(i, j, k):
            id_bad.append("%d,%d,%d id %s (want 0x%016x)" % (i, j, k, pick(t, "id", "idDec"), tile_id(i, j, k)))
    want = set(TILE_SLOTS)
    got = set(slots)
    if got != want:
        probs.append("slots: missing %s, extra %s" % (sorted(want - got)[:6], sorted(got - want)[:6]))
    spread = 0.0
    if cs:
        c0 = cs[0]
        spread = max(max(abs(c[a] - c0[a]) for a in range(3)) for c in cs)
        if spread > TILE_TOL:
            probs.append("min corners imply different centres (spread %.3g m)" % spread)
    C.row(rows, "S4.TILES", "PASS" if not probs else "FAIL", "%d tiles, C spread %.2g m" % (len(got), spread),
          "the 34 slots, save false", "tiles exist, never saved, min corner = C + lattice (C = %s)" % (
              fmt(cs[0][0], "%.3f") + "," + fmt(cs[0][1], "%.3f") + "," + fmt(cs[0][2], "%.3f") if cs else "-"),
          "tiles", problems=probs[:12])
    lines = s.text_has(s.se_text, r"DE\.Terrain TILE ")
    seen = {}
    for ln in lines:
        m = _RX_TILE.search(ln)
        if m:
            seen[(int(m.group(1)), int(m.group(2)), int(m.group(3)))] = int(m.group(4), 16)
    for sl, v in seen.items():
        if sl in want and v != tile_id(*sl):
            id_bad.append("log %d,%d,%d id 0x%x" % (sl + (v,)))
    if len(seen) != 34 and not id_bad:
        id_bad.append("%d 'DE.Terrain TILE' lines (want 34)" % len(seen))
    C.row(rows, "S4.IDS", "PASS" if not id_bad else "FAIL", "%d ids bad, %d log lines" % (len(id_bad), len(seen)),
          "ConstructId formula", "tile ids 0x%016x + 0x100k + 0x10j + i (siteVersion 1)" % tile_id(0, 0, 0), "tiles",
          problems=id_bad[:10])

AT0B_EXTRA = 1e-3      # m: the mesh may sit up to 2 byte steps (truncation + SE's iso byte) + 1 mm off the plane

def evaluate_selfchecks(s, rows):
    if s.ran("n3"):
        what = "in-game provider self-check (ReadRange flags, detectOnly, Intersect boxes, materials, no allocation)"
        rep = s.reply("n3")
        row = next((r for r in reversed(s.se_rows("s4.n3"))), None)
        src = row if row is not None else rep
        if not isinstance(src, dict) or (src.get("ok") is False and src.get("pass") is None):
            C.row(rows, "S4.N3", "MISSING", "no de.terrain n3 result", "pass", what, "n3",
                  error=(src or {}).get("error") if isinstance(src, dict) else None)
        else:
            passed = fbool(src, "pass", "passed")
            fails = src.get("failures") or src.get("failed") or []
            viol = fnum(src, "violations") or (fnum(rep or {}, "violations") if isinstance(rep, dict) else None) or 0
            if passed is None:
                passed = bool(src.get("ok")) and not fails and not viol
            nfail = (len(fails) if isinstance(fails, list) else 1) + int(viol)
            C.row(rows, "S4.N3", "PASS" if passed and not nfail else "FAIL", "%s; %d violation(s)" % (
                "pass" if passed else "fail", nfail), "pass, 0 violations", what, "n3",
                failures=fails[:10] if isinstance(fails, list) else fails,
                firstViolation=(rep or {}).get("firstViolation") if isinstance(rep, dict) else None)
    if "at0b" in s.parts:
        what = ("A-T0b in SE: SE's IsoMesher over a synthetic plane served by the provider: every vertex within 2 byte "
                "steps (0.5/127.5 x 2^L m each) + 1 mm of the plane at LOD 0..2")
        rep = s.reply("at0b") or {}
        meas = [r for r in (rep.get("rows") or []) if isinstance(r, dict)] or s.se_rows("s4.at0b")
        if not meas:
            C.row(rows, "S4.A-T0b", "MISSING", "no at0b rows", "within quantization", what, "at0b",
                  error=rep.get("error"))
        else:
            bad, isos, worst = [], [], 0.0
            for r in meas:
                q = fnum(r, "quantization") or 0.5 / 127.5 * 2 ** (fnum(r, "lod") or 0)
                e = fnum(r, "maxAbsErr")
                if fnum(r, "vertices") in (None, 0) or e is None:
                    bad.append("h=%s lod%s: no vertices" % (r.get("height"), r.get("lod")))
                    continue
                worst = max(worst, e / (2 * q + AT0B_EXTRA))
                if e > 2 * q + AT0B_EXTRA:
                    bad.append("h=%s lod%s: max|err| %.4f m > %.4f" % (r.get("height"), r.get("lod"), e, 2 * q + AT0B_EXTRA))
                if fnum(r, "isoMedian") is not None:
                    isos.append(fnum(r, "isoMedian"))
            C.row(rows, "S4.A-T0b", "PASS" if not bad else "FAIL", "%d rows, worst %.2f of the bound" % (len(meas), worst),
                  "<= 2 bytes + 1 mm", what + "; SE's iso byte (median) %s" % (fmt(pct(isos, 0.5), "%.2f")), "at0b",
                  problems=bad[:8], isoByteMedian=pct(isos, 0.5))

# --------------------------------------------------------------------------------------------- A-T1 rays
def _rays(s, part):
    """Ray rows of a part: the launcher's "rays" events (rows it tailed), else the SE JSONL s4.ray rows (all)."""
    evs = [e for e in s.ev("rays") if e.get("part") == part]
    if evs:
        out = []
        for e in evs:
            for r in e.get("rows") or []:
                rr = dict(r)
                rr["_label"] = e.get("label")
                out.append(rr)
        return out
    if any(e.get("ev") == "rays" for e in s.session):
        return []
    return s.se_rows("s4.ray") if part == "at1" else []

def classify_ray(s, r):
    """One ray -> dict(kind, delta, ref, entries): kind in built-unbuilt-error / start-inside / no-terrain / hit /
 hole-miss / hole-through / false-hit."""
    x, z = fnum(r, "snX", "x"), fnum(r, "snZ", "z")
    y0, y1 = fnum(r, "fromY") or 20.0, fnum(r, "toY") or -400.0
    out = {"x": x, "z": z, "intersectHole": fbool(r, "intersectHole") is True}
    if r.get("err"):
        out["kind"] = "error"
        return out
    cells, built = fnum(r, "cells"), fnum(r, "cellsBuilt")
    if cells is not None and built is not None and built < cells:
        out["kind"] = "unbuilt"
        return out
    if x is None or z is None:
        out["kind"] = "error"
        return out
    cr = s.crossings(x, z, y0, y1)
    if cr is None:
        out["kind"] = "noref"
        return out
    entries = [y for y, k in cr if k > 0]
    out["entries"] = len(entries)
    out["ref"] = entries[0] if entries else None
    d0, _same = s.oracle.d(x, y0, z)
    if d0 > 0:
        out["kind"] = "start-inside"
        return out
    hit = fbool(r, "castHit", "hit")
    y = fnum(r, "castSnY")
    if y is None and vec3(r.get("castSe")):
        y = s.site.se_to_sn(vec3(r.get("castSe")))[1]
    if hit and y is None:
        hit = False
    if out["ref"] is None:
        out["kind"] = "false-hit" if hit else "no-terrain"
        if hit:
            out["delta"] = None
            out["y"] = y
        return out
    if not hit:
        out["kind"] = "hole-miss"
        return out
    out["y"] = y
    out["delta"] = y - out["ref"]
    out["kind"] = "hole-through" if out["delta"] < -HOLE_BELOW_M else "hit"
    st = fnum(r, "storageSnY")
    if st is not None:
        out["storageDelta"] = st - out["ref"]
    return out

def _ray_stats(cls):
    single = [c for c in cls if c.get("kind") in ("hit", "hole-through") and c.get("entries") == 1]
    d = [abs(c["delta"]) for c in single]
    return {"n": len(cls), "single": len(single), "p95": pct(d, 0.95), "max": max(d) if d else None,
            "overhang": sum(1 for c in cls if c.get("kind") in ("hit", "hole-through") and (c.get("entries") or 0) > 1),
            "holes": [c for c in cls if c.get("kind") in ("hole-miss", "hole-through")],
            "false": [c for c in cls if c.get("kind") == "false-hit"],
            "unbuilt": sum(1 for c in cls if c.get("kind") == "unbuilt"),
            "errors": sum(1 for c in cls if c.get("kind") == "error"),
            "inside": sum(1 for c in cls if c.get("kind") == "start-inside"),
            "noref": sum(1 for c in cls if c.get("kind") == "noref"),
            "ixholes": [c for c in cls if c.get("intersectHole")],
            "storage": [abs(c["storageDelta"]) for c in cls if c.get("storageDelta") is not None]}

def evaluate_at1(s, rows):
    if not s.ran("at1"):
        return
    rays = _rays(s, "at1")
    if not rays:
        C.row(rows, "S4.A-T1.1", "MISSING", "no s4.ray rows", "P95<=0.25 max<=1.0", "A-T1 vertical rays", "at1")
        C.row(rows, "S4.A-T1.1h", "MISSING", "no rays", "0 holes", "A-T1 holes", "at1")
        C.row(rows, "S4.A-T1.1u", "MISSING", "no rays", "0 unbuilt", "A-T1 harness", "at1")
        C.row(rows, "S4.A-T1.1r", "MISSING", "no rays", "as 1.1", "A-T1 random rays", "at1")
        return
    if s.oracle is None:
        msg = "no reference: %s" % s.oracle_error
        for cid in ("S4.A-T1.1", "S4.A-T1.1h", "S4.A-T1.1r"):
            C.row(rows, cid, "MISSING", msg[:34], "reference heights", msg, "at1")
    grid = [r for r in rays if (r.get("set") or "grid") == "grid"]
    rnd = [r for r in rays if r.get("set") == "random"]
    allc = []
    if s.oracle is not None:
        cg = [classify_ray(s, r) for r in grid]
        cr = [classify_ray(s, r) for r in rnd]
        allc = cg + cr
        g = _ray_stats(cg)
        cov = (g["n"] - g["unbuilt"] - g["errors"] - g["inside"] - g["noref"]) / float(g["n"]) if g["n"] else 0.0
        ok = (g["single"] >= AT1_MIN_SINGLE and cov >= AT1_MIN_COVERAGE and g["p95"] is not None and g["p95"] <= AT1_P95
              and g["max"] <= AT1_MAX and not g["false"])
        C.row(rows, "S4.A-T1.1", "PASS" if ok else ("MISSING" if not g["single"] else "FAIL"),
              "P95 %s max %s (%d cols, cov %.0f %%)" % (fmt(g["p95"]), fmt(g["max"]), g["single"], 100 * cov),
              "P95<=%.2f max<=%.1f cov>=%.0f%% cols>=%d" % (AT1_P95, AT1_MAX, 100 * AT1_MIN_COVERAGE, AT1_MIN_SINGLE),
              "grid rays vs the reference on columns without overhangs (%d rays; %d overhang columns, %d start "
              "inside, %d false hits; storage vs ref P95 %s)" % (g["n"], g["overhang"], g["inside"], len(g["false"]),
                                                                  fmt(pct(g["storage"], 0.95))), "at1",
              falseHits=[(c["x"], c["z"], c.get("y")) for c in g["false"][:10]])
        holes = g["holes"]
        C.row(rows, "S4.A-T1.1h", "PASS" if not holes else "FAIL", "%d holes" % len(holes), "0",
              "grid rays with every cell built over terrain that miss or pass > %.1f m below the first entry" %
              HOLE_BELOW_M, "at1", holes=[(c["x"], c["z"], c["kind"], c.get("delta")) for c in holes[:20]])
        ix = g["ixholes"] + _ray_stats(cr)["ixholes"]
        C.row(rows, "S4.A-T1.1x", "PASS" if not ix else "FAIL", "%d rays" % len(ix), "0",
              "rays whose own Storage.Intersect answer was Disjoint/Contains on a non-uniform cell (intersectHole: the "
              "harness asks per cell; SE's physics prefetch never calls Intersect, so a cast cannot show S4.M3)", "at1",
              intersectHoles=[(c["x"], c["z"]) for c in ix[:20]])
        if rnd:
            r_ = _ray_stats(cr)
            okr = (r_["single"] > 0 and r_["p95"] is not None and r_["p95"] <= AT1_P95 and r_["max"] <= AT1_MAX and
                   not r_["holes"] and not r_["false"])
            C.row(rows, "S4.A-T1.1r", "PASS" if okr else "FAIL", "P95 %s max %s holes %d" % (
                fmt(r_["p95"]), fmt(r_["max"]), len(r_["holes"])), "as 1.1 + 1.1h",
                "the %d random rays (%d single-entry columns)" % (r_["n"], r_["single"]), "at1",
                holes=[(c["x"], c["z"], c["kind"]) for c in r_["holes"][:10]])
        else:
            C.row(rows, "S4.A-T1.1r", "MISSING", "no random rays", "1,000 random rays", "A-T1 random rays", "at1")
    unb = sum(1 for r in rays if r.get("err") or (fnum(r, "cells") is not None and fnum(r, "cellsBuilt") is not None
                                                  and fnum(r, "cellsBuilt") < fnum(r, "cells")))
    errs = sum(1 for r in rays if r.get("err"))
    C.row(rows, "S4.A-T1.1u", "PASS" if unb == 0 else "FAIL", "%d of %d rays (%d errors)" % (unb, len(rays), errs),
          "0", "harness: rays with an unbuilt cell or an error (a harness error, never a hole)", "at1",
          prefetch=sorted({str(r.get("prefetch")) for r in rays}))
    s.at1_classified = allc

def evaluate_bake(s, rows):
    if not s.ran("bake"):
        return
    rays = _rays(s, "bake")
    bake = [r for r in rays if r.get("set") == "bake"]
    prov = [r for r in rays if r.get("set") != "bake"]       # the same at1: every ray over the bake map has both
    if not bake:
        C.row(rows, "S4.A-T1.1b", "MISSING", "no rays of set bake", "<= 1 mm", "bake map vs provider", "bake")
        return
    idx = {}
    for r in prov:
        x, z = fnum(r, "snX"), fnum(r, "snZ")
        if x is not None and z is not None:
            idx[(round(x, 3), round(z, 3))] = r
    diffs = []
    for r in bake:
        x, z = fnum(r, "snX"), fnum(r, "snZ")
        p = idx.get((round(x, 3), round(z, 3))) if x is not None and z is not None else None
        if p is None:
            continue
        a, b = fnum(r, "storageSnY"), fnum(p, "storageSnY")
        if a is None or b is None:
            a, b = fnum(r, "castSnY"), fnum(p, "castSnY")
        if a is not None and b is not None:
            diffs.append(abs(a - b))
    if not diffs:
        C.row(rows, "S4.A-T1.1b", "MISSING", "%d bake rays, 0 pairs" % len(bake), "<= 1 mm", "bake map vs provider",
              "bake")
        return
    mx = max(diffs)
    C.row(rows, "S4.A-T1.1b", "PASS" if mx <= BAKE_TOL else "FAIL", "max %.4f m (%d pairs)" % (mx, len(diffs)),
          "<= 0.001 m", "bake (WriteRange into MyOctreeStorage(null, 256)) vs provider hit heights at the same columns",
          "bake")

def _rows_of(s, ev_name, part, se_name):
    evs = [e for e in s.ev(ev_name) if e.get("part") == part]
    if evs:
        return [dict(r, _label=e.get("label")) for e in evs for r in (e.get("rows") or [])]
    if any(e.get("ev") == ev_name for e in s.session):
        return []
    return s.se_rows(se_name)

def evaluate_drop(s, rows):
    if not s.ran("drop"):
        return
    drops = _rows_of(s, "drops", "drop", "s4.drop")
    if not drops:
        C.row(rows, "S4.A-T1.2d", "MISSING", "no s4.drop rows", "+-0.3 m", "1-block grids dropped at 10 points", "drop")
        return
    if s.oracle is None:
        C.row(rows, "S4.A-T1.2d", "MISSING", "no reference", "+-0.3 m", "drops: %s" % s.oracle_error, "drop")
        return
    bad, devs, n = [], [], 0
    for r in drops:
        x, z = fnum(r, "snX", "x"), fnum(r, "snZ", "z")
        y = fnum(r, "restSnY", "bottomSnY", "restBottomSnY", "snY", "restY")
        rs = vec3(r.get("restSn"))
        if rs is not None:                 # the bottom-face centre where the block came to rest (it may slide)
            x, z = rs[0], rs[2]
        if r.get("err"):
            bad.append("(%s, %s): %s" % (x, z, str(r.get("err"))[:60]))
            continue
        if None in (x, z, y):
            bad.append("row without snX/snZ/rest height")
            continue
        n += 1
        foot = [s.ref(x + dx, z + dz, y + 20.0, y - 60.0) for dx in (-DROP_HALF, 0.0, DROP_HALF)
                for dz in (-DROP_HALF, 0.0, DROP_HALF)]
        foot = [f for f in foot if f is not None]
        if not foot:
            bad.append("(%g, %g): no reference terrain" % (x, z))
            continue
        lo, hi = min(foot), max(foot)
        dev = 0.0 if lo - DROP_TOL <= y <= hi + DROP_TOL else min(abs(y - (lo - DROP_TOL)), abs(y - (hi + DROP_TOL)))
        devs.append(y - foot[4] if len(foot) == 9 else y - hi)
        if dev > 0:
            bad.append("(%g, %g) rest %.3f vs band [%.3f, %.3f]" % (x, z, y, lo, hi))
        if fbool(r, "rested", "settled") is False:
            bad.append("(%g, %g) not rested" % (x, z))
    C.row(rows, "S4.A-T1.2d", "PASS" if not bad and n else ("MISSING" if not n else "FAIL"),
          "%d of %d ok; centre dev max %s" % (n - len(bad), n, fmt(max((abs(d) for d in devs), default=None))),
          "+-0.3 m of the footprint band", "1-block large grids dropped at %d points rest on the reference" % n, "drop",
          problems=bad[:10])

def _walk_samples(rows_):
    out = []
    for r in rows_:
        if r.get("done") is True:
            continue                                   # the walk's summary row (dist, ticks, maxBelow)
        f = vec3(r.get("feetSn"))
        if f is not None:                              # the plugin's shape: feetSn [x, y, z], surfaceSnY
            out.append({"x": f[0], "y": f[1], "z": f[2], "storage": fnum(r, "surfaceSnY", "storageSnY"),
                        "label": r.get("label") or r.get("_label")})
            continue
        smp = r.get("samples") or r.get("path")
        if isinstance(smp, list):
            for p in smp:
                v = vec3(p) if not isinstance(p, dict) else (fnum(p, "snX", "x"), fnum(p, "feetSnY", "feetY", "snY", "y"),
                                                             fnum(p, "snZ", "z"))
                if v and None not in v:
                    out.append({"x": v[0], "y": v[1], "z": v[2], "storage": None, "label": r.get("label") or
                                r.get("_label")})
            continue
        x, z = fnum(r, "snX", "x"), fnum(r, "snZ", "z")
        y = fnum(r, "feetSnY", "feetY", "snY")
        if None not in (x, y, z):
            out.append({"x": x, "y": y, "z": z, "storage": fnum(r, "storageSnY", "groundSnY"),
                        "label": r.get("label") or r.get("_label")})
    return out

def evaluate_walk(s, rows):
    if not s.ran("walk"):
        return
    smp = _walk_samples(_rows_of(s, "walks", "walk", "s4.walk"))
    if not smp:
        C.row(rows, "S4.A-T1.2w", "MISSING", "no s4.walk samples", "sink <= 0.5 m", "scripted 50 m walk", "walk")
        return
    if s.oracle is None:
        C.row(rows, "S4.A-T1.2w", "MISSING", "no reference", "sink <= 0.5 m", "walk: %s" % s.oracle_error, "walk")
        return
    sinks = []
    for p in smp:
        ref = s.ref(p["x"], p["z"], p["y"] + 20.0, p["y"] - 60.0)
        if ref is not None:
            sinks.append(ref - p["y"])
    if not sinks:
        C.row(rows, "S4.A-T1.2w", "MISSING", "no reference under the path", "sink <= 0.5 m", "walk", "walk")
        return
    mx = max(sinks)
    C.row(rows, "S4.A-T1.2w", "PASS" if mx <= WALK_SINK else "FAIL", "max sink %.3f m (%d samples)" % (mx, len(sinks)),
          "<= 0.5 m", "the character's feet never sink below the reference by more than 0.5 m (50 m walk)", "walk",
          maxAbove=round(-min(sinks), 3))

def _ram_figure(s):
    for src in [s.reply("status")] + list(reversed(s.se_rows("s4.stats"))) + list(reversed(s.se_rows("s4.perf"))):
        ram = (src or {}).get("ram") if isinstance((src or {}).get("ram"), dict) else src
        b, a = fnum(ram or {}, "privateBefore"), fnum(ram or {}, "privateAfter")
        if b and a:
            return a - b, "SE private bytes after - before the 34 tiles (the plugin's TerrainTiles)"
        v = fnum(src or {}, "tileRamBytes", "ramBytes", "providerBytes", "tileBytes")
        if v is not None:
            return v, "plugin figure"
        if isinstance(src, dict) and isinstance(src.get("ram"), dict):
            v = fnum(src["ram"], "tileBytes", "bytes", "deltaBytes")
            if v is not None:
                return v, "plugin figure"
    samples = [e for e in s.ev("sample") if isinstance(e.get("se"), dict)]
    pb = [fnum(e["se"], "privateBytes") for e in samples if fnum(e["se"], "privateBytes")]
    if pb and s.baseline:
        base = C.read_jsonl(os.path.join(s.baseline, "s4-session.jsonl"))
        bpb = [fnum(e["se"], "privateBytes") for e in base if e.get("ev") == "sample" and isinstance(e.get("se"), dict)
               and fnum(e["se"], "privateBytes")]
        if bpb:
            return max(pb) - max(bpb), "SE private bytes - baseline %s" % os.path.basename(s.baseline.rstrip("\\/"))
    return None, ("SE private bytes max %.2f GB (no baseline)" % (max(pb) / 1024 ** 3)) if pb else "no figure"

def evaluate_perf(s, rows):
    if not (s.ran("at1") or s.ran("perf")):
        return
    v, how = _ram_figure(s)
    C.row(rows, "S4.A-T1.3m", ("PASS" if v <= RAM_MAX else "FAIL") if v is not None else "MISSING",
          "%.3f GB" % (v / 1024 ** 3) if v is not None else how[:34], "<= 1.5 GB", "tile RAM after the 34 tiles (%s)" %
          how, "perf")
    st = s.reply("status") or {}
    rr = st.get("readRange") if isinstance(st.get("readRange"), dict) else None
    if rr is None:
        rr = next((e.get("readRange") for e in reversed(s.se_rows("s4.stats")) if isinstance(e.get("readRange"), dict)),
                  None)
    p99 = fnum(rr or {}, "p99us", "p99Us")
    C.row(rows, "S4.A-T1.3r", ("PASS" if p99 <= RR_P99_US else "FAIL") if p99 is not None else "MISSING",
          "p99 %s us (%s calls)" % (fmt(p99, "%.0f"), (rr or {}).get("calls")), "<= 2000 us",
          "ReadRange p99 (de.terrain status readRange)", "perf", readRange=rr)
    cells = s.se_rows("s4.cell")
    phys = fnum(rr or {}, "physP99us")
    reads = [fnum(c, "readUs") for c in cells if fnum(c, "readUs") is not None]
    reads += [1000.0 * fnum(c, "readMs") for c in cells if fnum(c, "readUs") is None and fnum(c, "readMs") is not None]
    n3 = [e for e in s.se_rows("s4.n3") if fnum(e, "cellP99us") is not None]
    if n3:
        e = n3[-1]
        c99, cmx = fnum(e, "cellP99us"), fnum(e, "cellMaxUs")
        C.row(rows, "S4.A-T1.3c", "PASS" if c99 < CELL_US else "FAIL", "p99 %.0f us max %s us (1,000 12^3 reads)" % (
            c99, fmt(cmx, "%.0f")), "< 1000 us per cell", "ReadRange per 8 m physics cell: whole 12^3 content+material "
            "reads timed in game (de.terrain n3; SE issues two provider calls per cell, so a per-call p99 halves the cost)",
            "perf", cellP50us=fnum(e, "cellP50us"), cellP99us=c99, cellMaxUs=cmx)
    elif reads:
        p = pct(reads, 0.99)
        C.row(rows, "S4.A-T1.3c", "PASS" if max(reads) < CELL_US else "FAIL", "max %.0f us p99 %.0f us (%d cells)" % (
            max(reads), p, len(reads)), "< 1000 us per cell", "ReadRange per 8 m physics cell (RequestShapeBlocking)",
            "perf")
    elif phys is not None:
        C.row(rows, "S4.A-T1.3c", "PASS" if phys < CELL_US else "FAIL", "phys p99 %.0f us max %s us" % (
            phys, fmt(fnum(rr or {}, "physMaxUs"), "%.0f")), "< 1000 us per cell",
            "ReadRange of the 8 m physics cells (readRange.physP99us: RequestShapeBlocking / prefetch reads)", "perf")
    elif p99 is not None:
        C.row(rows, "S4.A-T1.3c", "PASS" if p99 < CELL_US else "FAIL", "proxy p99 %.0f us" % p99, "< 1000 us per cell",
              "ReadRange per 8 m physics cell: proxy = the ReadRange p99 (s4.cell rows carry no read time)", "perf")
    else:
        C.row(rows, "S4.A-T1.3c", "MISSING", "no per-cell read time", "< 1000 us per cell", "ReadRange per cell", "perf")
    if s.ran("perf"):
        pf = [e for e in s.se_rows("s4.perf")] + [r for e in s.ev("perf") for r in (e.get("rows") or [])]
        frac, how, absolute = None, "", None
        for r in reversed(pf):
            # a frame "> 5 ms" is a hitch: more than 5 ms over the flight's median frame (at 60 fps every frame takes
 # ~16.7 ms, so an absolute 5 ms test would always fail); the absolute count is shown for information
            frac = fnum(r, "fracHitches")
            how = "frames > median + 5 ms"
            n = fnum(r, "frames")
            if frac is None and n and fnum(r, "hitchesOver5ms") is not None:
                frac = fnum(r, "hitchesOver5ms") / n
            if frac is None:
                frac = fnum(r, "over5msFrac", "fracOver5ms")
                how = "frames over 5 ms"
            if frac is None and n and fnum(r, "framesOver5ms", "over5ms", "over5msFrames") is not None:
                frac = fnum(r, "framesOver5ms", "over5ms", "over5msFrames") / n
                how = "frames over 5 ms (absolute)"
            if n and fnum(r, "framesOver5ms") is not None:
                absolute = fnum(r, "framesOver5ms") / n
            if frac is not None:
                break
        C.row(rows, "S4.A-T1.3f", ("PASS" if frac <= FRAMES_5MS else "FAIL") if frac is not None else "MISSING",
              "%.2f %%" % (100 * frac) if frac is not None else "no s4.perf row", "<= 1 %",
              "%s during the 100 m/s fly-over%s" % (how, "; absolute > 5 ms: %.1f %%" % (100 * absolute)
                                                     if absolute is not None else ""), "perf")
    builds = [fnum(c, "buildMs") for c in cells if fnum(c, "buildMs") is not None]
    C.row(rows, "S4.A-T1.3s", "INFO", "p50 %s p95 %s max %s ms" % (fmt(pct(builds, 0.5), "%.2f"), fmt(
        pct(builds, 0.95), "%.2f"), fmt(max(builds) if builds else None, "%.2f")), "decides 1024 vs 2048",
        "shape-build time per 8 m cell (%d cells; built %d)" % (len(cells), sum(1 for c in cells if c.get("built"))),
        "perf", required=False)

# --------------------------------------------------------------------------------------------- Subnautica side
def sn_probes(s, part=None):
    out = []
    for e in s.ev("sn_probe"):
        if part and e.get("part") != part:
            continue
        for r in e.get("results") or []:
            if isinstance(r, dict):
                out.append(dict(r, _part=e.get("part")))
    if out or any(e.get("ev") == "sn_probe" for e in s.session):
        return out
    for e in s.sn_ev:
        if e.get("_ev") == "s4.probe":
            out.append(e)
    if not out:
        for t in s.sn_text:
            m = _RX_PROBE.search(t)
            if m:
                d = _kvline(m.group(1))
                if "delta" in d and "renderDelta" not in d:
                    d["renderDelta"] = d["delta"]
                out.append(d)
    return out

def _sn_y(p):
    if fbool(p, "hit") is False or p.get("error"):
        return None
    return fnum(p, "y")

def evaluate_at2(s, rows):
    if not s.ran("at2") or s.mode != "S4both" and "at2" not in s.parts:
        return
    probes = sn_probes(s, "at2")
    se_rays = _rays(s, "at2")
    idx = {}
    for r in se_rays:
        x, z = fnum(r, "snX"), fnum(r, "snZ")
        y = fnum(r, "castSnY")
        if x is not None and z is not None and y is not None and fbool(r, "castHit", "hit") is not False:
            idx[(round(x, 2), round(z, 2))] = y
    pairs = []
    unverified = 0
    valid = 0
    for p in probes:
        if fbool(p, "verified") is False:        #  an unverified Subnautica hit is not a reference
            unverified += 1
            continue
        x, z, y = fnum(p, "x"), fnum(p, "z"), _sn_y(p)
        if None in (x, z, y):
            continue
        valid += 1
        se_y = idx.get((round(x, 2), round(z, 2)))
        if se_y is not None:
            pairs.append((x, z, y, se_y, y - se_y))
    s.at2_pairs = {(round(x, 2), round(z, 2)): d for x, z, _y, _sy, d in pairs}
    if not pairs:
        C.row(rows, "S4.A-T2", "MISSING", "%d SN probes, %d SE rays, 0 pairs" % (len(probes), len(se_rays)),
              "P95 <= 0.3 m", "Subnautica layer-30 vs SE CastRay at the same columns", "at2")
    else:
        d = [abs(p[4]) for p in pairs]
        p95 = pct(d, 0.95)
        share = len(pairs) / float(valid) if valid else 0.0
        C.row(rows, "S4.A-T2", "PASS" if p95 <= AT2_P95 and share >= AT2_MIN_SHARE else "FAIL",
              "P95 %.3f max %.3f (%d of %d cols, %d unverified)" % (p95, max(d), len(d), valid, unverified),
              "P95 <= 0.3 m, >= 90 %% paired", "Subnautica layer-30 raycast vs SE CastRay at the same mapped columns "
              "(mean SN-SE %+.3f m: the sample-offset evidence of CONTRACT 3.1; a column one side missed counts against "
              "the share)" % (sum(p[4] for p in pairs) / len(pairs)),
              "at2", worst=sorted(pairs, key=lambda p: -abs(p[4]))[:5], paired=len(pairs), verifiedProbes=valid,
              unverified=unverified)
    rd = [abs(fnum(p, "renderDelta")) for p in probes if fnum(p, "renderDelta") is not None]
    C.row(rows, "S4.A-T2n", "INFO", "P50 %s P95 %s max %s m (%d)" % (fmt(pct(rd, 0.5)), fmt(pct(rd, 0.95)), fmt(
        max(rd) if rd else None), len(rd)), "native budget", "Subnautica collider vs its render mesh at the A-T2 "
        "columns (mesh-only ray on the chunk's hiFilters mesh)", "at2", required=False,
        notes=sorted({str(p.get("renderNote")) for p in probes if p.get("renderNote")})[:5])

def mirror_lines(s):
    out = []
    for t in s.sn_text:
        m = _RX_MIRROR.search(t)
        if m:
            try:
                corners = [tuple(float(c) for c in tr.split(",")) for tr in m.group(3).split(";")]
            except ValueError:
                continue
            if len(corners) == 8:
                out.append({"entity": m.group(1), "tick": int(m.group(2)), "corners": corners})
    return out

def evaluate_wm6(s, rows):
    if "wm6" not in s.parts:
        return
    w = next((e for e in reversed(s.ev("wm6"))), None)
    if w is None:
        for cid in ("S4.WM-6a", "S4.WM-6b"):
            C.row(rows, cid, "MISSING", "no wm6 record", "see ROADMAP", "WM-6 cube", "wm6")
        return
    spot = w.get("spot") or [0.0, 0.0]
    drop = w.get("drop") or {}
    se_bottom = fnum(drop, "restSnY", "bottomSnY", "restBottomSnY", "snY")
    sn_y = _sn_y(w.get("probe") or {})
    mir = [m for m in mirror_lines(s)]
    best = None
    for m in mir:
        cx = sum(c[0] for c in m["corners"]) / 8.0
        cz = sum(c[2] for c in m["corners"]) / 8.0
        dist = math.hypot(cx - spot[0], cz - spot[1])
        if dist < 5.0 and (best is None or m["tick"] >= best["tick"]):
            best = m
    if best is None:
        bottom = None
    else:
        ys = sorted(c[1] for c in best["corners"])
        bottom = sum(ys[:4]) / 4.0
    # the A-T2 deviation at that column: the wm6 part's own SE ray and Subnautica hit there, else the at2 grid's pair
    ray = w.get("seRay") if isinstance(w.get("seRay"), dict) else {}
    col = None
    if sn_y is not None and fnum(ray, "castSnY") is not None and fbool(ray, "castHit", "hit") is not False:
        col = sn_y - fnum(ray, "castSnY")
    if col is None:
        col = getattr(s, "at2_pairs", {}).get((round(float(spot[0]), 2), round(float(spot[1]), 2)))
    tol_a = min(AT2_P95, abs(col) + WM6_EXTRA) if col is not None else AT2_P95
    if bottom is None or sn_y is None:
        C.row(rows, "S4.WM-6a", "MISSING", "mirror %s, SN hit %s" % ("yes" if best else "no", fmt(sn_y)), "<= A-T2 dev",
              "mirrored bottom face vs Subnautica's terrain under the cube", "wm6")
    else:
        dv = bottom - sn_y
        C.row(rows, "S4.WM-6a", "PASS" if abs(dv) <= tol_a else "FAIL", "%+.3f m" % dv, "<= %.3f m" % tol_a,
              "mirrored bottom face (%s) vs Subnautica's layer-30 terrain under it at SN (%g, %g)" % (
                  best["entity"], spot[0], spot[1]), "wm6", at2Column=col)
    if bottom is None or se_bottom is None:
        C.row(rows, "S4.WM-6b", "MISSING", "mirror %s, SE rest %s" % ("yes" if best else "no", fmt(se_bottom)),
              "<= 0.05 m", "mirrored bottom vs SE's rest mapped through F", "wm6")
    else:
        dv = bottom - se_bottom
        C.row(rows, "S4.WM-6b", "PASS" if abs(dv) <= WM6_MAP else "FAIL", "%+.4f m" % dv, "<= 0.05 m",
              "mirrored bottom face vs SE's own resting bottom mapped through F (the map check)", "wm6")

def evaluate_n1(s, rows):
    if not s.ran("n1"):
        return
    rec = next((e for e in reversed(s.ev("n1"))), None)
    samples = []
    for st in (rec or {}).get("stands") or []:         # de.terrain n1: feetSn and the storage under it, standing
        src = st.get("row") if isinstance(st.get("row"), dict) else (st.get("reply") or {})
        f = vec3(src.get("feetSn"))
        if f is not None:
            samples.append({"x": f[0], "y": f[1], "z": f[2], "storage": fnum(src, "storageSnY"), "label": "n1"})
    if not samples and not rec:
        samples = [dict(p, y=p["y"]) for p in _walk_samples(s.se_rows("s4.n1"))]
    if not samples:
        samples = _walk_samples((rec or {}).get("walks") or []) if rec else _walk_samples(
            [r for r in s.se_rows("s4.walk") if (r.get("label") or "") == "n1"])
    if not samples:
        C.row(rows, "S4.N1", "MISSING", "no feet samples", "<= 0.3 m", "SE character on Subnautica's seabed", "n1")
        return
    by_pt = {}
    for p in samples:
        by_pt.setdefault((round(p["x"], 1), round(p["z"], 1)), []).append(p)
    probes = {}
    for p in ((rec or {}).get("probes") or []):
        if isinstance(p, dict) and fnum(p, "x") is not None:
            probes[(round(fnum(p, "x"), 1), round(fnum(p, "z"), 1))] = _sn_y(p)
    worst, details, missing = 0.0, [], 0
    for key, pts in by_pt.items():
        feet = pts[-1]["y"]          # the settled sample: the last of the stand
        cmp = {}
        if s.oracle is not None:
            ref = s.ref(pts[-1]["x"], pts[-1]["z"], feet + 20.0, feet - 60.0)
            if ref is not None:
                cmp["ref"] = feet - ref
        if pts[-1].get("storage") is not None:
            cmp["storage"] = feet - pts[-1]["storage"]
        if probes.get(key) is not None:
            cmp["sn"] = feet - probes[key]
        if "ref" not in cmp and "sn" not in cmp:
            missing += 1
            continue
        w = max(abs(v) for v in cmp.values())
        worst = max(worst, w)
        details.append({"x": key[0], "z": key[1], "feet": round(feet, 3), **{k: round(v, 3) for k, v in cmp.items()}})
    if not details:
        C.row(rows, "S4.N1", "MISSING", "no reference at %d point(s)" % missing, "<= 0.3 m", "feet vs terrain", "n1")
        return
    C.row(rows, "S4.N1", "PASS" if worst <= N1_TOL else "FAIL", "worst %.3f m (%d points)" % (worst, len(details)),
          "<= 0.3 m", "SE character standing on the seabed: feet vs the reference%s" % (
              ", the storage and Subnautica's layer-30 hit" if probes else " and the storage height"), "n1",
          points=details)

def evaluate_n2(s, rows):
    if "n2" not in s.parts:
        return
    blocked_lines = {}
    for t in s.se_text:
        m = _RX_EDIT.search(t)
        if m:
            blocked_lines[m.group(1)] = blocked_lines.get(m.group(1), 0) + 1
    recs = {}
    for e in s.ev("edit"):
        p = e.get("path")
        if p:
            recs[p] = e
    for r in s.se_rows("s4.edit"):
        p = r.get("path")
        if p and p not in recs:
            recs[p] = {"path": p, "row": r}
    for r in s.se_rows("s4.edit"):                  # without launcher events: the plugin's final rows win
        p = r.get("path")
        if p and r.get("final") and "row" in recs.get(p, {}) and not recs[p].get("reply"):
            recs[p] = {"path": p, "row": r, "first": recs[p].get("row")}
    for path in EDIT_PATHS:
        e = recs.get(path)
        src = None
        if e is not None:
            # the final witness (2 s after the call) when there is one; the immediate row / the reply otherwise
            src = e.get("final") if isinstance(e.get("final"), dict) else (
                e.get("row") if isinstance(e.get("row"), dict) else e.get("reply"))
            if isinstance(e.get("reply"), dict) and isinstance(src, dict) and src is not e.get("reply"):
                src = dict(e["reply"], **src)
        if not isinstance(src, dict):
            C.row(rows, "S4.N2.%s" % path, "MISSING", "no result", "blocked, hash same", "edit guard %s" % path, "n2")
            continue
        probs = []
        if fbool(src, "blocked") is not True:
            probs.append("blocked %s" % src.get("blocked"))
        hb, ha = pick(src, "hashBefore"), pick(src, "hashAfter")
        same = src.get("hashUnchanged") if isinstance(src.get("hashUnchanged"), bool) else None
        if same is None:
            same = hb is not None and ha is not None and json.dumps(hb, sort_keys=True) == json.dumps(ha, sort_keys=True)
        if not same:
            probs.append("hash changed %s -> %s" % (json.dumps(hb)[:24], json.dumps(ha)[:24]))
        if fbool(src, "modified") is not False:
            probs.append("modified %s" % src.get("modified"))
        ore = None
        for o in (src, e.get("first"), e.get("reply"), e.get("row")):
            if isinstance(o, dict) and o.get("ore") is not None:
                ore = o.get("ore")
                break
        if ore_amount(ore) > 0:
            probs.append("ore %s" % json.dumps(ore)[:40])
        if not blocked_lines.get(path):
            probs.append("no 'DE.Terrain EditBlocked %s' line" % path)
        C.row(rows, "S4.N2.%s" % path, "PASS" if not probs else "FAIL", "; ".join(probs)[:34] or "blocked, hash same",
              "blocked, hash same, no ore", "edit on a tile through the %s path is blocked and leaves no trace" % path,
              "n2", problems=probs)

# --------------------------------------------------------------------------------------------- world scans
def scan_world(folder, tile_ids=None):
    """Read-only scan of an SE world folder for S4.G2-E2/E3b: DE.Terrain storages (.vx2 names, StorageName in the
 sector), voxel maps carrying a tile id, grids (id, static, position, name) and landing gears (grid, LockMode,
 IsLocked, AttachedEntityId). Text only; the files are never written."""
    tile_ids = set(tile_ids or TILE_IDS)
    out = {"folder": folder, "vx2": [], "tileVx2": [], "storageNames": [], "tileVoxelMaps": [], "grids": [],
           "gears": [], "error": None}
    try:
        names = sorted(os.listdir(folder))
    except OSError as e:
        out["error"] = str(e)
        return out
    out["vx2"] = [n for n in names if n.lower().endswith(".vx2")]
    out["tileVx2"] = [n for n in out["vx2"] if n.startswith("DE.Terrain")]
    for n in ("Sandbox.sbc", "Sandbox_config.sbc"):
        p = os.path.join(folder, n)
        if os.path.exists(p):
            out.setdefault("mtimes", {})[n] = os.path.getmtime(p)
    p = os.path.join(folder, "SANDBOX_0_0_0_.sbs")
    if not os.path.exists(p):
        out["error"] = "no SANDBOX_0_0_0_.sbs"
        return out
    with open(p, encoding="utf-8-sig", errors="replace") as f:
        text = f.read()
    out["storageNames"] = [n for n in re.findall(r"<StorageName>([^<]*)</StorageName>", text) if n.startswith("DE.Terrain")]
    for m in re.finditer(r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_VoxelMap">(.*?)</MyObjectBuilder_EntityBase>',
                         text, re.S):
        mid = re.search(r"<EntityId>(-?\d+)</EntityId>", m.group(1))
        if mid and int(mid.group(1)) in tile_ids:
            out["tileVoxelMaps"].append(int(mid.group(1)))
    for m in re.finditer(r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">', text):
        end = text.find("</MyObjectBuilder_EntityBase>", m.end())
        # nested EntityBase blocks do not occur inside a grid (blocks are MyObjectBuilder_CubeBlock)
        blk = text[m.end():end]
        gid = re.search(r"<EntityId>(-?\d+)</EntityId>", blk)
        pos = re.search(r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"\s*/>', blk)
        static = re.search(r"<IsStatic>(true|false)</IsStatic>", blk)
        name = re.search(r"<DisplayName>([^<]*)</DisplayName>", blk)
        g = {"id": int(gid.group(1)) if gid else None, "static": bool(static and static.group(1) == "true"),
             "name": name.group(1) if name else None,
             "pos": [float(pos.group(1)), float(pos.group(2)), float(pos.group(3))] if pos else None}
        out["grids"].append(g)
        for gm in re.finditer(r'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_LandingGear">(.*?)</MyObjectBuilder_CubeBlock>',
                              blk, re.S):
            b = gm.group(1)
            bid = re.search(r"<EntityId>(-?\d+)</EntityId>", b)
            att = re.search(r"<AttachedEntityId>(-?\d+)</AttachedEntityId>", b)
            mode = re.search(r"<LockMode>(\w+)</LockMode>", b)
            locked = re.search(r"<IsLocked>(true|false)</IsLocked>", b)
            out["gears"].append({"grid": g["id"], "id": int(bid.group(1)) if bid else None,
                                 "attached": int(att.group(1)) if att else None, "lockMode": mode.group(1) if mode else None,
                                 "isLocked": bool(locked and locked.group(1) == "true")})
    return out

def _in_footprint(site, pos):
    if pos is None:
        return False
    q = site.se_to_sn(tuple(pos))
    return max(abs(q[0]), abs(q[2])) <= FOOTPRINT and q[1] < 0.0

def evaluate_saves(s, rows):
    scans = s.ev("world_scan")
    ran = [p for p in ("g2e2save", "g2e3b", "m4", "m10") if p in s.parts]
    if ran:
        after = [e for e in scans if str(e.get("when", "")).startswith("after")]
        if not after:
            C.row(rows, "S4.G2-E2a", "MISSING", "no scan after a save", "no tile in the save",
                  "save portability (the owner's save step)", "g2e2save")
        else:
            bad = []
            for e in after:
                sc = e.get("scan") or {}
                if sc.get("tileVx2") or sc.get("storageNames") or sc.get("tileVoxelMaps"):
                    bad.append("%s: vx2 %s names %s maps %s" % (e.get("when"), sc.get("tileVx2")[:3],
                                                                 sc.get("storageNames")[:3], sc.get("tileVoxelMaps")[:3]))
            C.row(rows, "S4.G2-E2a", "PASS" if not bad else "FAIL", "%d save(s), %d with tiles" % (len(after), len(bad)),
                  "0 tiles saved", "the save holds no tile, no DE.Terrain .vx2 and no tile voxel map", "g2e2save",
                  problems=bad)
    if any(p in s.parts for p in ("g2e3b", "m10")):
        pre = next((e.get("scan") for e in scans if e.get("when") == "before-load"), None)
        post = next((e.get("scan") for e in reversed(scans) if str(e.get("when", "")).startswith("after")), None)
        if not pre or not post:
            C.row(rows, "S4.G2-E2b", "MISSING", "scans: before %s after %s" % (bool(pre), bool(post)), "< 1 cm",
                  "resting grid across a reload", "g2e3b")
            C.row(rows, "S4.G2-E3b", "MISSING", "scans: before %s after %s" % (bool(pre), bool(post)), "still locked",
                  "landing gear across a reload", "g2e3b")
        else:
            prev = {g["id"]: g for g in pre.get("grids") or [] if g.get("id") is not None}
            moves = []
            for g in post.get("grids") or []:
                p0 = prev.get(g.get("id"))
                if not p0 or g.get("static") or not g.get("pos") or not p0.get("pos"):
                    continue
                if not _in_footprint(s.site, p0["pos"]):
                    continue
                moves.append((g.get("name") or g.get("id"), math.dist(g["pos"], p0["pos"])))
            if not moves:
                C.row(rows, "S4.G2-E2b", "MISSING", "no dynamic grid in the footprint", "< 1 cm",
                      "a resting grid moves < 1 cm after the reload", "g2e3b")
            else:
                mx = max(m[1] for m in moves)
                C.row(rows, "S4.G2-E2b", "PASS" if mx < E2B_MOVE else "FAIL", "max %.4f m (%d grids)" % (mx, len(moves)),
                      "< 0.01 m", "dynamic grids in the footprint: position in the save before the reload vs the save "
                      ">= 10 s after it", "g2e3b", moves=[(str(n), round(d, 4)) for n, d in moves[:10]])
            tiles_now = set()
            rep = s.reply("tiles")
            for t in (rep or {}).get("tiles") or []:
                v = as_int(pick(t, "idDec", "id"))
                if v is not None:
                    tiles_now.add(v)
            tiles_now = tiles_now or set(TILE_IDS)
            locked0 = [g for g in pre.get("gears") or [] if g.get("lockMode") == "Locked" and g.get("attached") is not None
                       and g["attached"] in (set(TILE_IDS) | tiles_now)]
            if not locked0:
                C.row(rows, "S4.G2-E3b", "MISSING", "no gear locked to a tile before", "still locked",
                      "a ship landing-gear-locked to a tile (the owner prepares it in the copy)", "g2e3b")
            else:
                now = {g["id"]: g for g in post.get("gears") or []}
                bad = []
                for g in locked0:
                    n = now.get(g["id"])
                    if n is None or n.get("lockMode") != "Locked" or not n.get("isLocked") or n.get("attached") not in tiles_now:
                        bad.append("gear %s: %s attached %s" % (g["id"], (n or {}).get("lockMode"), (n or {}).get("attached")))
                C.row(rows, "S4.G2-E3b", "PASS" if not bad else "FAIL", "%d of %d still locked" % (
                    len(locked0) - len(bad), len(locked0)), "all locked to a tile id",
                    "landing gears locked to a tile stay locked across save and reload (AttachedEntityId = the "
                    "tile's deterministic id)", "g2e3b", problems=bad[:6])
    if "g2e2noplugin" in s.parts or s.no_plugin_log:
        if s.no_plugin_log and os.path.exists(s.no_plugin_log):
            with open(s.no_plugin_log, encoding="utf-8", errors="replace") as f:
                txt = f.read()
            knf = "KeyNotFoundException" in txt
            C.row(rows, "S4.G2-E2c", "FAIL" if knf else "PASS", "KeyNotFound %s" % knf, "loads without the plugin",
                  "the world copy loads in SE without our plugin (owner's Pulsar profile without it): no "
                  "KeyNotFoundException from ReadDataProvider; a grid in the footprint sinks (expected)", "g2e2noplugin",
                  required=False)
        else:
            C.row(rows, "S4.G2-E2c", "OTHER", "owner step", "loads without the plugin", "SE without the plugin on the "
                  "throwaway copy (pass its SpaceEngineers_*.log with --no-plugin-log)", "g2e2noplugin", required=False)
    if "g2e5" in s.parts:
        st = s.reply("status")
        e5 = next((e for e in reversed(s.ev("g2e5"))), {})
        lines = [t.strip()[-160:] for t in s.se_text if any(k in t for k in ("DE.Site", "DE.Terrain", "Exception",
                                                                             "DE_4546B"))][:6]
        C.row(rows, "S4.G2-E5", "INFO", "gate %s; in world %s" % ((st or {}).get("gate"), e5.get("inWorld")),
              "record", "world mod removed on a copy: what SE did (CONTRACT 7 Uninstall HYPOTHESIS): %s" % (
                  " | ".join(lines) or "no DE.Site/DE.Terrain/exception line"), "g2e5", required=False, lines=lines)

def evaluate_reference(s, rows):
    o = s.oracle
    if o is None:
        C.row(rows, "S4.REF", "INFO", "no octrees", "-", "reference heights unavailable: %s" % s.oracle_error, "ref",
              required=False)
        return
    note = ""
    try:
        import sn_field  # noqa: F401
        cls = [c for c in getattr(s, "at1_classified", []) if c.get("ref") is not None][:40]
        if cls and hasattr(sn_field, "Field") and isinstance(o, W.OctreeColumns):
            f = sn_field.Field(o.root)
            worst = 0.0
            for c in cls:
                if hasattr(f, "column_surfaces"):       # [(y, "top"|"bottom")]: the first "top" is the entry
                    y = next((yy for yy, kind in f.column_surfaces(c["x"], c["z"], c["ref"] + 2.0, c["ref"] - 2.0)
                              if kind == "top"), None)
                elif hasattr(f, "column_crossings"):
                    ys = list(f.column_crossings(c["x"], c["z"], c["ref"] + 0.5, c["ref"] - 0.5))
                    y = min(ys, key=lambda v: abs(v - c["ref"])) if ys else None
                else:
                    y = None
                worst = max(worst, abs(y - c["ref"])) if y is not None else float("inf")
            note = "; sn_field.py cross-check on %d columns: worst %.4f m" % (len(cls), worst)
    except ImportError:
        note = "; sn_field.py not importable (cross-check skipped)"
    except Exception as e:     # the other reference is optional: never hide the table
        note = "; sn_field.py cross-check error %s" % type(e).__name__
    C.row(rows, "S4.REF", "INFO", "offset %.2f" % getattr(o, "o", W.SAMPLE_OFFSET), "-",
          "reference: %s; C from %s%s" % (getattr(o, "root", "synthetic"), s.C_source, note), "ref", required=False)

def evaluate_other(s, rows):
    C.row(rows, "S4.G2-E1", "OTHER", "owner", "flat cut faces", "Subnautica warps 880 -700 -816 (ceiling 640 +-2 m), "
          "-560 -1950 112 (ceiling 1,920 m), -250 -900 1008 facing -X (wall at x = -288): the owner's dormant session",
          "other", required=False)
    if "capture" in s.parts:
        caps = s.ev("capture")
        C.row(rows, "S4.A-T2c", "OTHER", "%d capture(s)" % sum(1 for c in caps if c.get("written")), "look at it",
              "captures of the SE-mirrored cube on the seabed from 3 m (owner looks)", "capture", required=False,
              files=[c.get("png") for c in caps][:6])

def evaluate_mutants(s, rows):
    for m in s.mutants:
        spec = MUTANT_ROWS.get(m)
        if spec is None:
            C.row(rows, "%s.red" % m, "INFO", "no rows", "-", "mutant without an oracle row here", "mutants",
                  required=False)
            continue
        red, follow, green = spec
        for r in rows:
            if r["id"] in red or r["id"] in follow:
                r["required"] = False
        got_red = [w for w in red if any(r["id"] == w and r["status"] == "FAIL" for r in rows)]
        absent = [w for w in red + green if not any(r["id"] == w and r["status"] in ("PASS", "FAIL") for r in rows)]
        not_green = [w for w in green if any(r["id"] == w and r["status"] == "FAIL" for r in rows)]
        status = "PASS" if len(got_red) == len(red) and not not_green else ("MISSING" if absent else "FAIL")
        C.row(rows, "%s.red" % m, status, "red: %s%s" % (",".join(got_red) or "none", "; green broken: %s" %
              ",".join(not_green) if not_green else ""), "red: %s%s" % (",".join(red), "; green: %s" % ",".join(green)
                                                                          if green else ""),
              "seeded failure %s turns its rows red" % m, "mutants", required=True)

def evaluate(folder, sn_logs=(), octrees=None, oracle=None, baseline=None, no_plugin_log=None) -> list:
    s = S4Session(folder, sn_logs, octrees, oracle, baseline, no_plugin_log)
    rows = []
    if not s.session:
        C.row(rows, "S4", "MISSING", "no s4-session.jsonl", "a launcher s4 session", "session log", "all")
        return rows
    for fn in (evaluate_source, evaluate_gate, evaluate_tiles, evaluate_selfchecks, evaluate_at1, evaluate_bake,
               evaluate_drop, evaluate_walk, evaluate_perf, evaluate_at2, evaluate_wm6, evaluate_n1, evaluate_n2,
               evaluate_saves, evaluate_reference, evaluate_other):
        try:
            fn(s, rows)
        except Exception as e:     # an analyser bug must not hide the other rows
            import traceback
            C.row(rows, "S4.%s" % fn.__name__.split("_", 1)[-1].upper(), "FAIL", "analyser error %s" % type(e).__name__,
                  "-", str(e)[:120] + " | " + traceback.format_exc().strip().splitlines()[-1][:120], "all")
    evaluate_mutants(s, rows)
    if isinstance(s._oracle, W.OctreeColumns):
        s._oracle.close()
    return rows

# ============================================================================================== selftest
class Synth:
    """Writes a synthetic s4 session folder: the launcher's session log, the SE JSONL rows and the Subnautica probe
 results as the contract describes them, over an analytic terrain (worldmap_ref.SyntheticField)."""

    def __init__(self, folder, field, mode="S4se", parts=(), mutants=(), world=None):
        self.dir, self.f, self.mode = folder, field, mode
        self.parts, self.mut = list(parts), set(mutants)
        os.makedirs(folder, exist_ok=True)
        self.sess = open(os.path.join(folder, "s4-session.jsonl"), "w", encoding="utf-8")
        self.se = open(os.path.join(folder, "se-20261016-000000.jsonl"), "w", encoding="utf-8")
        self.t = 0
        self.ev("start", mode=mode, parts=self.parts, mutants=sorted(self.mut), world=world, octrees=None,
                qpcFrequency=10_000_000)

    def ev(self, _event, **kw):
        self.t += 1
        rec = {"utc": "2026-10-16T12:00:%02d.%03dZ" % (self.t // 1000 % 60, self.t % 1000), "qpc": self.t, "ev": _event}
        rec.update(kw)
        self.sess.write(json.dumps(rec) + "\n")

    def se_row(self, _event, **kw):
        rec = {"t": "2026-10-16T12:00:00.0000000Z", "ev": _event}
        rec.update(kw)
        self.se.write(json.dumps(rec) + "\n")

    def se_log(self, msg):
        self.se.write(json.dumps({"t": "2026-10-16T12:00:00.0000000Z", "ev": "log", "msg": msg}) + "\n")

    def cmd(self, part, action, reply, side="se", name="de.terrain", **args):
        a = dict(args)
        if name == "de.terrain":
            a["action"] = action
        self.ev("cmd", side=side, name=name, args=a, reply=reply, ms=3.0, label=action, part=part)

    def close(self):
        self.ev("end", rc=None)
        self.sess.close()
        self.se.close()

    # ---------------------------------------------------------------------------------------- parts
    def gate(self):
        refuse = "S4.M5" in self.mut
        src = {"path": "X", "build": PINS["build"], "files": PINS["files"],
               "bytes": PINS["bytes"], "rows": PINS["rows"], "manifestHash": "0x%x" % PINS["manifestHash"], "indexOk": True,
               "batchesIndexed": 0, "batchesMapped": 0, "bytesMapped": 0, "badBatches": 0}
        st = {"ok": True, "gate": "REFUSE" if refuse else "OK", "condition": "source" if refuse else None,
              "reason": "files found=5416 expected=5417" if refuse else None, "tiles": 0 if refuse else 34,
              "terrainUp": not refuse, "failSafe": {"active": refuse, "reason": "source" if refuse else None,
                                                     "saveBlocked": 0},
              "provider": {"registered": True, "error": None}, "source": src,
              "readRange": {"calls": 120000, "p50us": 40, "p99us": 650, "maxUs": 1800, "physP99us": 700,
                            "physMaxUs": 1500},
              "ram": {"privateBefore": 4 * 1024 ** 3, "privateAfter": int(4.8 * 1024 ** 3)}}
        self.ev("source", measure={"path": "X", "build": PINS["build"], "files": PINS["files"], "bytes": PINS["bytes"],
                                   "rows": PINS["rows"], "manifestHash": "0x%x" % PINS["manifestHash"],
                                   "index": PINS["index"], "other": []})
        self.ev("part_start", name="gate")
        self.cmd("gate", "status", st)
        self.cmd("gate", "gate", {"ok": True, "match": not refuse, "source": src})
        self.ev("part_end", name="gate")
        if refuse:
            self.se_log("DE.Terrain REFUSE source files found=5416 expected=5417")
            self.se_log("DE.Terrain FAILSAFE ON source")
        else:
            self.se_log("DE.Terrain OK tiles=34 siteVersion=1 C=(-1000000,250000,1500000)")

    def tiles(self):
        refuse = "S4.M5" in self.mut
        tl = []
        import random as _r
        rnd = _r.Random(7)
        for (i, j, k) in ([] if refuse else TILE_SLOTS):
            tid = tile_id(i, j, k) if "S4.M10" not in self.mut else rnd.getrandbits(62)
            m = SV.tile_min(i, j, k)
            mn = [SV.TEST_C[0] + m[0], SV.TEST_C[1] + m[1], SV.TEST_C[2] + m[2]]
            pm = [mn[0], mn[1], mn[2] + (2.0 if "S4.M2" in self.mut else 0.0)]
            tl.append({"i": i, "j": j, "k": k, "id": "0x%016x" % tid, "idDec": tid, "min": mn, "placedMin": pm,
                       "exists": True, "closed": False, "save": False, "bodies": 1})
            self.se_log("DE.Terrain TILE %d,%d,%d id=0x%016x min=(%g,%g,%g)" % (i, j, k, tid, mn[0], mn[1], mn[2]))
        self.ev("part_start", name="tiles")
        self.cmd("tiles", "tiles", {"ok": True, "tiles": tl})
        self.ev("part_end", name="tiles")

    def n3(self):
        bad = "S4.M8b" in self.mut
        self.cmd("n3", "n3", {"ok": True, "pass": not bad, "violations": 3 if bad else 0,
                              "firstViolation": "detectOnly EmptyData on a mixed box" if bad else None})

    def at0b(self):
        off = 0.5 if "S4.M6" in self.mut else 0.0
        rows_ = [{"height": h, "lod": lod, "vertices": 40, "meanErr": off + 0.001, "maxAbsErr": off + 0.003 * 2 ** lod,
                  "isoMedian": 127.4, "quantization": 0.5 / 127.5 * 2 ** lod} for h in (-17.5, -20.25) for lod in (0, 1, 2)]
        self.cmd("at0b", "at0b", {"ok": True, "rows": rows_})

    def rays(self, part, label, cols, set_="grid", y0=20.0, y1=-400.0, rnd=None):
        rows_ = []
        for (x, z) in cols:
            ref = self.f.first_entry(x, z, y0, y1)
            r = {"set": set_, "snX": x, "snZ": z, "fromY": y0, "toY": y1, "prefetch": "S4.M7" not in self.mut,
                 "cells": 53, "cellsBuilt": 53, "waitMs": 3.0, "castHit": ref is not None, "err": None}
            if ref is not None:
                y = ref + (rnd.uniform(-0.08, 0.08) if rnd else 0.0)
                if "S4.M2" in self.mut:
                    y += 2.0
                if "S4.M6" in self.mut:
                    y += 0.5
                r["castSnY"] = y
                r["storageHit"] = True
                r["storageSnY"] = y + 0.0004
            in_cell = abs(x) < 4.0 and abs(z) < 4.0
            if "S4.M8" in self.mut and in_cell and ref is not None:
                r["castHit"] = False
                r.pop("castSnY", None)
            if "S4.M3" in self.mut and in_cell and ref is not None:
                r["intersectHole"] = True     #  the cast still hits (prefetch never calls Intersect)
            if "S4.M7" in self.mut and rnd is not None and rnd.random() < 0.3:
                r["cellsBuilt"] = 40
            rows_.append(r)
            self.se_row("s4.ray", **r)
        self.ev("rays", part=part, label=label, expected=len(cols), rows=rows_)
        return rows_

    def at1(self, rnd):
        cols = [(-18.75 + 2.5 * i, -18.75 + 2.5 * j) for i in range(16) for j in range(16)]
        rcols = [(rnd.uniform(-60, 60), rnd.uniform(-60, 60)) for _ in range(60)]
        self.ev("part_start", name="at1")
        self.cmd("at1", "at1", {"ok": True, "pending": True})
        self.rays("at1", "at1", cols, "grid", rnd=rnd)
        self.rays("at1", "at1", rcols, "random", rnd=rnd)
        for c in range(20):
            self.se_row("s4.cell", i=c, j=0, k=0, x=0, y=0, z=0, built=True, waitedMs=2.0, buildMs=0.4 + 0.01 * c,
                        readUs=300 + c)
        self.ev("part_end", name="at1")

    def bake(self):
        cols = [(-48.0 + 2.5 * i, -48.0 + 2.5 * j) for i in range(6) for j in range(6)]
        self.ev("part_start", name="bake")
        self.rays("bake", "bake.on", cols, "bake")
        self.rays("bake", "bake.off", cols, "grid")
        self.ev("part_end", name="bake")

    def drop(self):
        pts = [(-30.0 + 6.0 * i, 10.0) for i in range(10)]
        rows_ = []
        for x, z in pts:
            ref = self.f.first_entry(x, z, 20, -400) + (2.0 if "S4.M2" in self.mut else 0.0)
            rows_.append({"i": len(rows_), "snX": x, "snZ": z, "restSnY": ref + 0.04, "restSn": [x + 0.1, ref + 0.04, z],
                          "lowestCornerSnY": ref, "speed": 0.0})
            self.se_row("s4.drop", **rows_[-1])
        self.ev("drops", part="drop", rows=rows_)

    def walk(self):
        rows_ = []
        for i in range(50):
            x, z = 5.0 + i, -5.0
            rows_.append({"tick": 10 * i, "feetSn": [x, self.f.first_entry(x, z, 20, -400) + 0.02, z],
                          "surfaceSnY": self.f.first_entry(x, z, 20, -400)})
            self.se_row("s4.walk", **rows_[-1])
        rows_.append({"done": True, "dist": 50.0, "samples": 50, "maxBelow": 0.0})
        self.ev("walks", part="walk", label="walk", rows=rows_)

    def perf(self):
        self.se_row("s4.perf", frames=6000, framesOver5ms=6000, hitchesOver5ms=24, fracHitches=0.004, frameP99Ms=19.2,
                    speed=100)
        self.ev("perf", part="perf", rows=[{"frames": 6000, "framesOver5ms": 6000, "hitchesOver5ms": 24,
                                            "fracHitches": 0.004}])
        self.ev("sample", label="end", se={"privateBytes": 5.1 * 1024 ** 3, "workingSet": 4.0 * 1024 ** 3})

    def n1(self, sn=False):
        pts = [(2.0, 3.0), (-12.0, 20.0), (25.0, -14.0)]
        walks, probes = [], []
        stands = []
        for x, z in pts:
            ref = self.f.first_entry(x, z, 20, -400)
            walks.append({"feetSn": [x, ref + 0.05, z], "surfaceSnY": ref, "label": "n1"})
            row = {"feetSe": [0, 0, 0], "feetSn": [x, ref + 0.05, z], "storageHit": True, "storageSnY": ref + 0.01,
                   "delta": 0.04}
            stands.append({"point": [x, z], "reply": dict(row, ok=True), "row": row})
            if sn:
                probes.append({"x": x, "z": z, "hit": True, "y": ref - 0.02, "layer": 30})
        self.ev("n1", points=pts, walks=walks, stands=stands, probes=probes)

    def n2(self):
        for p in EDIT_PATHS:
            off = "S4.M9.%s" % p in self.mut
            hb = [{"tile": "1,1,0", "content": "0x1234abcd", "material": "0x77"}]
            ha = [{"tile": "1,1,0", "content": "0x99990000" if off else "0x1234abcd", "material": "0x77"}]
            ore = {"percent": 0.4 if off else 0.0, "material": "Sand_02" if off else None, "voxels": 120 if off else 0} \
                if p == "cutout" else None
            first = {"path": p, "blocked": not off, "count": 1, "hashUnchanged": not off, "modified": off,
                     "hashBefore": hb, "hashAfter": ha, "ore": ore}
            final = dict(first, final=True)
            final.pop("ore")
            self.se_row("s4.edit", **first)
            self.se_row("s4.edit", **final)
            self.ev("edit", path=p, reply=dict(first, ok=True, pending=True), row=final, first=first, final=final)
            if not off:
                self.se_log("DE.Terrain EditBlocked %s n=1" % p)

    def at2_wm6(self, rnd):
        cols = [(-5.0 + 2.5 * i, -5.0 + 2.5 * j) for i in range(5) for j in range(5)]
        self.ev("part_start", name="at2")
        rows_ = self.rays("at2", "at2.shallow", cols, "grid", rnd=rnd)
        res = []
        for k, r in enumerate(rows_):
            ref = self.f.first_entry(r["snX"], r["snZ"], 20, -400)
            res.append({"i": k, "x": r["snX"], "z": r["snZ"], "hit": True, "y": ref + rnd.uniform(-0.1, 0.1),
                        "collider": "Override", "layer": 30, "camY": ref + 8, "waitMs": 400, "renderY": ref + 0.05,
                        "renderDelta": 0.05, "renderNote": None, "error": None})
        self.ev("sn_probe", part="at2", request={"probe": [[c[0], c[1]] for c in cols]}, results=res)
        self.ev("part_end", name="at2")
        x, z = 0.0, 0.0
        ref = self.f.first_entry(x, z, 20, -400)
        drop = {"snX": x, "snZ": z, "restSnY": ref + 0.03, "rested": True}
        self.ev("wm6", spot=[x, z], drop=drop, probe={"x": x, "z": z, "hit": True, "y": ref - 0.02})
        bottom = ref + 0.03 + (0.2 if "wm6map" in self.mut else 0.0)
        corners = ";".join("%g,%g,%g" % (x + dx, bottom + dy, z + dz) for dx in (-1.25, 1.25) for dy in (0.0, 2.5)
                           for dz in (-1.25, 1.25))
        with open(os.path.join(self.dir, "sn-20261016_000000-1.jsonl"), "w", encoding="utf-8") as f:
            f.write(json.dumps({"t": "x", "event": "DE.Mirror", "text": "DE.Mirror placed entity=0x1c0ffee tick=900 how=c3 "
                                "seLocalMin=0,0,0 seLocalMax=1,1,1 seLocalCorners=0,0,0 cornersSn=%s maxDevVsApplyM=0" %
                                corners}) + "\n")

    def saves(self, base_ids=None):
        def scan(when, gear_attached, lock, pos):
            sc = {"folder": "W", "vx2": ["DE_4546B-4546d120000.vx2"] + (["DE.Terrain.0.0.0.s1.vx2"] if "S4.M4" in self.mut
                                                                       and when.startswith("after") else []),
                  "tileVx2": ["DE.Terrain.0.0.0.s1.vx2"] if "S4.M4" in self.mut and when.startswith("after") else [],
                  "storageNames": [], "tileVoxelMaps": [],
                  "grids": [{"id": 11, "static": False, "name": "Rover", "pos": pos},
                            {"id": 12, "static": False, "name": "Lander", "pos": [-1000010.0, 250005.0, 1559982.0]}],
                  "gears": [{"grid": 12, "id": 121, "attached": gear_attached, "lockMode": lock, "isLocked": lock == "Locked"}]}
            self.ev("world_scan", when=when, folder="W", scan=sc)
        p0 = list(SV.SITE_V1.sn_to_se((3.0, -16.0, 4.0)))
        scan("before-load", tile_id(1, 1, 0), "Locked", p0)
        p1 = [p0[0], p0[1], p0[2] - (0.2 if "sink" in self.mut else 0.002)]
        m10 = "S4.M10" in self.mut
        scan("after-save", tile_id(1, 1, 0) if not m10 else None, "Unlocked" if m10 else "Locked", p1)

def _write(folder, field, mode="S4se", parts=(), mutants=(), rnd_seed=1):
    import random as _r
    rnd = _r.Random(rnd_seed)
    sy = Synth(folder, field, mode, parts, mutants)
    for p in parts:
        if p == "gate":
            sy.gate()
        elif p == "tiles":
            sy.tiles()
        elif p == "n3":
            sy.n3()
        elif p == "at0b":
            sy.at0b()
        elif p == "at1":
            sy.at1(rnd)
        elif p == "bake":
            sy.bake()
        elif p == "drop":
            sy.drop()
        elif p == "walk":
            sy.walk()
        elif p == "perf":
            sy.perf()
        elif p == "n1":
            sy.n1(sn=mode == "S4both")
        elif p == "n2":
            sy.n2()
        elif p == "at2":
            sy.at2_wm6(rnd)
        elif p in ("g2e2save", "g2e3b"):
            if p == "g2e3b":
                sy.saves()
    sy.close()

def selftest() -> int:
    field = W.SyntheticField(base=-17.2, sx=0.02, sz=-0.015, overhangs=[(10.0, 14.0, 10.0, 14.0, -6.0, -9.0)])
    tmp = tempfile.mkdtemp(prefix="s4-selftest-")
    checks = []

    def run(name, mode, parts, mutants=(), **kw):
        d = os.path.join(tmp, name)
        _write(d, field, mode, parts, mutants)
        return {r["id"]: r for r in evaluate(d, [], oracle=field, **kw)}

    def check(name, cond, info=""):
        checks.append((name, bool(cond), info))

    try:
        se_parts = ("gate", "tiles", "n3", "at0b", "at1", "bake", "drop", "walk", "n1", "perf")
        rows = run("clean", "S4se", se_parts)
        req_bad = [(k, r["status"]) for k, r in rows.items() if r.get("required", True) and r["status"] != "PASS"]
        check("clean S4se: every required row PASS", not req_bad, str(req_bad))
        for cid in ("S4.GATE", "S4.FAILSAFE", "S4.TILES", "S4.IDS", "S4.N3", "S4.A-T1.1", "S4.A-T1.1h", "S4.A-T1.1u",
                    "S4.A-T1.1r", "S4.A-T1.1b", "S4.A-T1.2d", "S4.A-T1.2w", "S4.A-T1.3m", "S4.A-T1.3r", "S4.A-T1.3c",
                    "S4.A-T1.3f", "S4.N1", "S4.SOURCE", "S4.A-T0b"):
            check("clean S4se has %s" % cid, cid in rows and rows[cid]["status"] == "PASS",
                  rows.get(cid, {}).get("measured"))
        check("overhang columns excluded from 1.1 (synthetic arch counted)", "overhang" in rows["S4.A-T1.1"]["what"]
              and " 0 overhang" not in rows["S4.A-T1.1"]["what"], rows["S4.A-T1.1"]["what"])
        rows = run("both", "S4both", ("gate", "tiles", "at2", "wm6", "n1"))
        req_bad = [(k, r["status"]) for k, r in rows.items() if r.get("required", True) and r["status"] != "PASS"]
        check("clean S4both: every required row PASS", not req_bad, str(req_bad))
        for cid in ("S4.A-T2", "S4.WM-6a", "S4.WM-6b", "S4.N1"):
            check("clean S4both has %s PASS" % cid, rows.get(cid, {}).get("status") == "PASS",
                  rows.get(cid, {}).get("measured"))
        rows = run("wm6map", "S4both", ("gate", "tiles", "at2", "wm6"), ("wm6map",))
        check("WM-6b red when the mirror is 0.2 m off the SE rest", rows["S4.WM-6b"]["status"] == "FAIL",
              rows["S4.WM-6b"]["measured"])
        rows = run("n2clean", "S4se", ("gate", "tiles", "n2"))
        check("clean n2: six paths PASS", all(rows.get("S4.N2.%s" % p, {}).get("status") == "PASS" for p in EDIT_PATHS),
              str([(p, rows.get("S4.N2.%s" % p, {}).get("status")) for p in EDIT_PATHS]))
        rows = run("e3clean", "S4se", ("gate", "tiles", "g2e2save", "g2e3b"))
        check("clean saves: G2-E2a/E2b/E3b PASS", all(rows.get(c, {}).get("status") == "PASS" for c in
                                                    ("S4.G2-E2a", "S4.G2-E2b", "S4.G2-E3b")),
              str([(c, rows.get(c, {}).get("status"), rows.get(c, {}).get("measured")) for c in
                   ("S4.G2-E2a", "S4.G2-E2b", "S4.G2-E3b")]))
        # ----------------------------------------------------------------------------------- seeded failures
        for m, parts, red in (("S4.M2", ("gate", "tiles", "at1"), "S4.A-T1.1"),
                              ("S4.M3", ("gate", "tiles", "at1"), "S4.A-T1.1x"),
                              ("S4.M5", ("gate", "tiles"), "S4.GATE"),
                              ("S4.M7", ("gate", "tiles", "at1"), "S4.A-T1.1u"),
                              ("S4.M8", ("gate", "tiles", "at1"), "S4.A-T1.1h"),
                              ("S4.M8b", ("gate", "tiles", "n3", "at1"), "S4.N3"),
                              ("S4.M6", ("gate", "tiles", "at1"), "S4.A-T1.1"),
                              ("S4.M6", ("gate", "tiles", "at0b", "at1"), "S4.A-T0b"),
                              ("S4.M4", ("gate", "tiles", "g2e2save", "g2e3b"), "S4.G2-E2a"),
                              ("S4.M10", ("gate", "tiles", "g2e2save", "g2e3b"), "S4.G2-E3b")):
            rows = run(m.replace(".", "_") + "_" + red.replace(".", "_"), "S4se", parts, (m,))
            check("%s turns %s red" % (m, red), rows.get(red, {}).get("status") == "FAIL",
                  rows.get(red, {}).get("measured"))
            if m == "S4.M2":
                check("S4.M2 also shows in S4.TILES (placedMin != min)", rows.get("S4.TILES", {}).get("status") == "FAIL",
                      rows.get("S4.TILES", {}).get("measured"))
            if m != "S4.M6" or "at0b" in parts:
                check("%s.red row PASS" % m, rows.get("%s.red" % m, {}).get("status") == "PASS",
                      rows.get("%s.red" % m, {}).get("measured"))
        rows = run("M7green", "S4se", ("gate", "tiles", "at1"), ("S4.M7",))
        check("S4.M7 leaves 1.1h green (unbuilt is not a hole)", rows["S4.A-T1.1h"]["status"] == "PASS",
              rows["S4.A-T1.1h"]["measured"])
        rows = run("M5fs", "S4se", ("gate", "tiles"), ("S4.M5",))
        check("S4.M5: the fail-safe agrees (active, 0 tiles)", rows["S4.FAILSAFE"]["status"] == "PASS",
              rows["S4.FAILSAFE"]["what"])
        for p in EDIT_PATHS:
            m = "S4.M9.%s" % p
            rows = run("M9_" + p, "S4se", ("gate", "tiles", "n2"), (m,))
            others = [q for q in EDIT_PATHS if q != p and rows.get("S4.N2.%s" % q, {}).get("status") != "PASS"]
            check("%s turns only S4.N2.%s red" % (m, p), rows["S4.N2.%s" % p]["status"] == "FAIL" and not others and
                  rows["%s.red" % m]["status"] == "PASS", "others red: %s" % others)
        rows = run("sink", "S4se", ("gate", "tiles", "g2e2save", "g2e3b"), ("sink",))
        check("a grid that sinks 0.2 m turns G2-E2b red", rows["S4.G2-E2b"]["status"] == "FAIL",
              rows["S4.G2-E2b"]["measured"])
        # --------------------------------------------------------------------------------- formats and ids
        check("tile id (0,0,0) = 91,274,119,533,887,488", tile_id(0, 0, 0) == 91274119533887488, str(tile_id(0, 0, 0)))
        check("tile id (1,1,2) = 0x0144455400010211", tile_id(1, 1, 2) == 0x0144455400010211, hex(tile_id(1, 1, 2)))
        d = _kvline("i=3 x=1.5 z=-2 hit=true y=-17.25 collider=Override layer=30 camY=-9.2 waitMs=410 renderY=-17.2 "
                    "delta=0.05")
        check("DE.S4 PROBE key=value line parses", d.get("hit") is True and d.get("y") == -17.25 and d.get("layer") == 30,
              str(d))
        empty = os.path.join(tmp, "empty")
        os.makedirs(empty)
        rows = evaluate(empty, [], oracle=field)
        check("an empty folder gives one MISSING row, no crash", len(rows) == 1 and rows[0]["status"] == "MISSING")
        # the real reference, when the user's octrees are present (read-only)
        try:
            o = W.OctreeColumns()
        except (OSError, ImportError):
            o = None
        if o is not None:
            y0 = o.first_entry(0.0, 0.0, 20.0, -400.0)
            check("real octrees: Safe Shallows (0, 0) seabed -17.21 +- 0.02 (S3 layer-30 -17.212)",
                  y0 is not None and abs(y0 + 17.212) < 0.02, "%s" % y0)
            o.close()
        else:
            print("  (octree folder absent: the real-reference check is skipped)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    for name, ok, info in checks:
        print("  %-4s %s%s" % ("ok" if ok else "FAIL", name, "" if ok else "  [%s]" % info))
    n_bad = sum(1 for c in checks if not c[1])
    print("S4 ANALYZER SELFTEST %s (%d checks, %d failed)" % ("PASS" if not n_bad else "FAIL", len(checks), n_bad))
    return 0 if not n_bad else 1

def write_results(folder, rows, strict=True, extra=None) -> bool:
    ok = C.verdict(rows, strict)
    res = {"tool": "analyze_s4", "out": folder, "ok": ok, "rows": rows}
    res.update(extra or {})
    with open(os.path.join(folder, "s4-results.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, indent=1, default=str)
    with open(os.path.join(folder, "s4-table.txt"), "w", encoding="utf-8") as f:
        f.write(C.table(rows) + "\n")
    return ok

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="S4 oracle over a launcher s4 session folder")
    ap.add_argument("folder", nargs="?")
    ap.add_argument("--sn-log", action="append", default=[], help="the Subnautica plugin's log (repeatable)")
    ap.add_argument("--octrees", default=None, help="CompiledOctreesCache folder (default: the Steam install)")
    ap.add_argument("--baseline", default=None, help="a no-tile s4 session folder (S4.M5) for the RAM delta")
    ap.add_argument("--no-plugin-log", default=None, help="SpaceEngineers_*.log of the owner's load without the plugin")
    ap.add_argument("--lenient", action="store_true", help="MISSING rows do not fail")
    ap.add_argument("--json")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest()
    if not a.folder:
        ap.print_help()
        return 2
    rows = evaluate(a.folder, a.sn_log, a.octrees, None, a.baseline, a.no_plugin_log)
    print(C.table(rows))
    ok = C.verdict(rows, not a.lenient)
    print("S4 ORACLE %s" % ("PASS" if ok else "FAIL"))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"tool": "analyze_s4", "folder": a.folder, "ok": ok, "rows": rows}, f, indent=1, default=str)
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())
