"""S6 stream tools for the stand-ins, the launcher and the S6 analyzer (Python 3.10 stdlib; ROADMAP S6 "tools/tests",
CONTRACT §2.6 / §2.7 / §4.1 / §5.4 "C4 type 6 SkinSet (S6)", "C8 damage record" and "S6 combat conventions").

 python -I tests/standins/s6_stream.py selftest # conventions twin (hash 0xb6ee880712f8253b), lattice,
 # codec round trip, synthetic hulls, damage plans
 python -I tests/standins/s6_stream.py hull bt1|bt1k|rotated|hull100|rotor|none [--json]
 # the synthetic skin of a stand-in hull (sections, boxes)
 python -I tests/standins/s6_stream.py plumbing [--seconds 25] [--keep DIR]
 # fake producer --s6 + fake host --s6 --damage over a
 # private section: SkinSet -> host, records -> C8 -> s6.c8

What lives here (one module so the fake producer, the fake host, the launcher and the analyzer share it; independent
of Wire's C#, it reads only tests/wire/layout_py.py for the record layouts):
- the S6 combat conventions twin: every constant of CONTRACT §5.4 "S6 combat conventions", its canonical text rebuilt
 from them and checked against the pinned FNV-1a 64 (so a constant that drifts from the contract fails the selftest);
- the section lattice (`round_away`, `section_index`, `section_id`, `section_centre`) and the C4 type 6 SkinSet codec
 (`encode_skin`, `decode_skin` with SkinCodec.TryDecode's checks), the section content hash and rho_max;
- the synthetic hulls of the stand-in producer (`Hull`): bt1 (ROADMAP S6.B-T1: 20 x 6 x 6 m, 50 boxes, 2 sections),
 bt1k (the same envelope in 1,000 boxes: B-T1 (3)), rotated (rotated boxes: G1-E3 through the live proxy path),
 hull100 (a 100 m hull of 100 boxes: S6.N4), rotor (a large root grid and a small-grid head turning at 10 rpm as a
 subgrid: subgrid sections, S6.N8 in stand-in form), none (the bt1 visual without a skin); every box is also a visual
 part (a procedural box model of its half extents), so Subnautica's mirror gets a view and a pose for the proxy;
- C8 damage records as Subnautica builds them (`damage_record`) and the fake host's plans: n1 (S6.N1: unique bites per
 target plus duplicates, amounts on a 1/8 grid so every sum is exact in float32), n10 (S6.N10: 100 malformed records
 each tagged with the DamageReject reason SE must give it), stream (valid records at a rate);
- `Dedup`, the SE dedup window twin (CONTRACT §4.1), for the stand-ins' counters.
Nothing is copied from either game.
"""
from __future__ import annotations

import json
import math
import os
import random
import struct
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, "..", ".."))
for _p in (_HERE, os.path.join(_REPO, "tests", "wire"), os.path.join(_REPO, "tools", "worldmap")):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import layout_py as L  # noqa: E402

# ======================================================================================================================
# S6 combat conventions
# ======================================================================================================================

HEADER = "ext s6 combat 1"
PINNED_HASH = 0xb6ee880712f8253b
PINNED_LINES, PINNED_BYTES = 54, 2068
LIMITS = {"RefreshMm": 10, "SkinDebounceTicks": 30, "CapsuleRadiusMm": 250, "MaxShapesPerStep": 256,
          "MaxAmountSnHp": 500, "CapsuleHeightMm": 1800, "MaxHalfExtentMm": 4000, "OverflowRecords": 4096,
          "DedupWindow": 8192, "SkinMaxBytes": 2097152}
REJECT = {"None": 0, "Generation": 1, "Epoch": 2, "Sequence": 3, "Duplicate": 4, "TooOld": 5, "Unit": 6, "Amount": 7,
          "Kind": 8, "Type": 9, "Entity": 10, "Section": 11, "Frame": 12, "HitPoint": 13, "NoBlock": 14,
          "NotDestructible": 15, "CannotDie": 16, "NotApplied": 17}
REJECT_NAMES = {v: k for k, v in REJECT.items()}
DAMAGE_SCALE = {"KChar": 1.0, "KSmall": 1.0, "Version": 1, "KLarge": 25.0}
SKIN_BOX_FLAGS = {"Rotated": 1, "SourceShift": 8, "SourceMask": 0xF00}
SKIN_BOX_SOURCE = {"Segment": 0, "ModelShapeObb": 1, "Synthetic": 15}
LATTICE = {"MaxBoxCellsLarge": 3, "SectionCellsLarge": 6, "SectionIdBits": 10, "MaxBoxCellsSmall": 16,
           "SectionCellsSmall": 32, "SectionIdBias": 512}
SN_DAMAGE_FORWARD = {"Normal": 0, "Explosive": 7, "Electrical": 8}
C8_FLAGS = {"MouthContact": 1, "Radius": 2, "FallbackHitPoint": 4, "FrameSubgrid": 8, "GhostPlayer": 16}
SE_DAMAGE_TYPE = {0: "DeepBite", 7: "DeepBlast", 8: "DeepShock"}
FORMATS = (
    ("characterSection", "id 0, capsule r 0.25 h 1.8 centred on the C1 pelvis bone, axis pelvis->head"),
    ("hitId", "snGeneration:dedupSeq, dedupSeq 1.. per Subnautica generation"),
    ("seDamageType", "0=DeepBite 7=DeepBlast 8=DeepShock"),
    ("sectionCentre", "(i*SectionCells+(SectionCells-1)/2)*GridSize per axis"),
    ("sectionId", "((ix+512)<<20)|((iy+512)<<10)|(iz+512), i=floorDiv(roundAway(c/GridSize),SectionCells), c=box centre"),
    ("skinVersion", "1.. per entity per stream, a SkinSet replaces the whole skin"),
)
TARGET_KIND = {"grid": 1, "character": 2}
GRID_CLASS = {"large": 1, "small": 2}
GRID_SIZE = {1: 2.5, 2: 0.5}
UNIT_SN_HP = 1
MAX_HALF = 4.0001            # SkinCodec: 0 < half <= 4.0 m (+1e-4 rounding)
ORTHO_TOL = 1e-5

def _num_text(v) -> str:
    if isinstance(v, float) and v == int(v):
        return "%d" % int(v)
    return str(v)

def conventions_text() -> str:
    """The canonical text of CONTRACT §5.4 "S6 combat conventions", rebuilt from this module's constants: `const` lines
 sorted by group, then value, then name; `format` lines sorted by key; '\\n' after every line."""
    groups = {"C8FlagsExt": {"GhostPlayer": C8_FLAGS["GhostPlayer"]}, "CombatLimits": LIMITS,
              "DamageReject": {k: v for k, v in REJECT.items() if k != "None"}, "DamageScale": DAMAGE_SCALE,
              "SkinBoxFlags": SKIN_BOX_FLAGS, "SkinBoxSource": SKIN_BOX_SOURCE, "SkinLattice": LATTICE,
              "SnDamageForward": SN_DAMAGE_FORWARD}
    lines = [HEADER]
    for g in sorted(groups):
        for name, v in sorted(groups[g].items(), key=lambda kv: (kv[1], kv[0])):
            lines.append("const %s %s %s" % (g, name, _num_text(v)))
    for key, text in sorted(FORMATS):
        lines.append("format %s %s" % (key, text))
    return "".join(x + "\n" for x in lines)

def conventions_hash() -> int:
    return L.fnv1a64(conventions_text().encode("ascii"))

def se_damage_type(sn_type: int):
    return SE_DAMAGE_TYPE.get(int(sn_type))

def factor(target_kind: int, target_class: int) -> float:
    """CONTRACT §4.1: character 1.0; GridAdd targetClass 1 (large) 25, 2 (small) 1.0."""
    if int(target_kind) == TARGET_KIND["character"]:
        return DAMAGE_SCALE["KChar"]
    return DAMAGE_SCALE["KLarge"] if int(target_class) == GRID_CLASS["large"] else DAMAGE_SCALE["KSmall"]

def hit_id(gen: int, seq: int) -> str:
    return "%d:%d" % (int(gen), int(seq))

# ======================================================================================================================
# lattice
# ======================================================================================================================

def round_away(x: float) -> int:
    """Math.Round(x, MidpointRounding.AwayFromZero) as VRage Vector3I.Round (exact: no x + 0.5 rounding step)."""
    ax = abs(x)
    r = math.floor(ax)
    if ax - r >= 0.5:
        r += 1
    return int(-r if x < 0 else r)

def section_cells(large: bool) -> int:
    return LATTICE["SectionCellsLarge"] if large else LATTICE["SectionCellsSmall"]

def max_box_cells(large: bool) -> int:
    return LATTICE["MaxBoxCellsLarge"] if large else LATTICE["MaxBoxCellsSmall"]

def section_index(c, grid_size: float, large: bool):
    """(ix, iy, iz) of a box centre (SE-local metres), or None outside [-512, 511]."""
    S = section_cells(large)
    out = []
    for v in c:
        i = round_away(float(v) / grid_size) // S
        if i < -LATTICE["SectionIdBias"] or i > LATTICE["SectionIdBias"] - 1:
            return None
        out.append(i)
    return tuple(out)

def section_id(ix: int, iy: int, iz: int) -> int:
    b = LATTICE["SectionIdBias"]
    return ((ix + b) << 20) | ((iy + b) << 10) | (iz + b)

def section_index_of(sid: int):
    b = LATTICE["SectionIdBias"]
    return ((sid >> 20) & 0x3FF) - b, ((sid >> 10) & 0x3FF) - b, (sid & 0x3FF) - b

def section_centre(ix: int, iy: int, iz: int, grid_size: float, large: bool):
    S = section_cells(large)
    return tuple((i * S + (S - 1) / 2.0) * grid_size for i in (ix, iy, iz))

def section_cube(sid: int, grid_size: float, large: bool):
    """The SE-local AABB (min, max) of a section's lattice cube: cells i*S.. i*S + S - 1 per axis."""
    S = section_cells(large)
    idx = section_index_of(sid)
    lo = tuple((i * S - 0.5) * grid_size for i in idx)
    hi = tuple(((i + 1) * S - 0.5) * grid_size for i in idx)
    return lo, hi

def hit_cell(local, grid_size: float):
    return tuple(round_away(float(v) / grid_size) for v in local)

# ======================================================================================================================
# boxes, sections and the SkinSet codec
# ======================================================================================================================

IDENTITY_AXES = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))

def _f32(x):
    return struct.unpack("<f", struct.pack("<f", float(x)))[0]

def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

def _norm(a):
    return math.sqrt(_dot(a, a))

def _unit(a):
    n = _norm(a) or 1.0
    return (a[0] / n, a[1] / n, a[2] / n)

def rot_axes(yaw_deg=0.0, pitch_deg=0.0, roll_deg=0.0):
    """Orthonormal right-handed box axes (rows e0, e1, e2): roll about Z, then pitch about X, then yaw about Y."""
    def rx(a):
        c, s = math.cos(a), math.sin(a)
        return ((1, 0, 0), (0, c, -s), (0, s, c))

    def ry(a):
        c, s = math.cos(a), math.sin(a)
        return ((c, 0, s), (0, 1, 0), (-s, 0, c))

    def rz(a):
        c, s = math.cos(a), math.sin(a)
        return ((c, -s, 0), (s, c, 0), (0, 0, 1))

    def mm(a, b):
        return tuple(tuple(sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)) for i in range(3))
    R = mm(ry(math.radians(yaw_deg)), mm(rx(math.radians(pitch_deg)), rz(math.radians(roll_deg))))
    cols = tuple(tuple(R[i][j] for i in range(3)) for j in range(3))     # e_j = column j of R
    e0, e1 = _unit(cols[0]), _unit(cols[1])
    return (e0, e1, _cross(e0, e1))

def make_box(centre, half, axes=IDENTITY_AXES, source=SKIN_BOX_SOURCE["Synthetic"]):
    axes = tuple(tuple(float(x) for x in e) for e in axes)
    rotated = any(abs(axes[i][j] - (1.0 if i == j else 0.0)) > 0.0 for i in range(3) for j in range(3))
    if not rotated:
        axes = IDENTITY_AXES
    flags = (SKIN_BOX_FLAGS["Rotated"] if rotated else 0) | (int(source) << SKIN_BOX_FLAGS["SourceShift"])
    return {"c": tuple(float(v) for v in centre), "h": tuple(float(v) for v in half), "axes": axes, "flags": flags}

def box_bytes(b) -> bytes:
    rows = [x for e in b["axes"] for x in e]
    return L.RECORDS["SkinBox"].pack(centre=b["c"], half=b["h"], axes=rows, flags=b["flags"])

def section_bytes(s) -> bytes:
    head = L.RECORDS["SkinSection"].pack(sectionId=s["id"], centre=s["centre"], boxCount=len(s["boxes"]),
                                         kind=s.get("kind", 1))
    return head + b"".join(box_bytes(b) for b in s["boxes"])

def content_hash(s) -> int:
    """FNV-1a 64 of the section's 24 + 64 n encoded bytes (CollisionSkinMath.SectionContentHash)."""
    return L.fnv1a64(section_bytes(s))

def rho_max(s) -> float:
    """max over the section's boxes of |c_box - c_sec| + |h_box| (CONTRACT §2.6)."""
    c = s["centre"]
    return max((_norm(tuple(b["c"][i] - c[i] for i in range(3))) + _norm(b["h"]) for b in s["boxes"]), default=0.0)

def build_sections(boxes, grid_size: float, large: bool):
    """Assigns boxes to the lattice section of their centre; ascending section ids; inside a section, by source
 (Segment, ModelShapeObb, Synthetic) then centre z, y, x. Raises on a box outside the lattice range."""
    by = {}
    for b in boxes:
        idx = section_index(b["c"], grid_size, large)
        if idx is None:
            raise ValueError("box centre %r outside the section lattice" % (b["c"],))
        by.setdefault(section_id(*idx), (idx, []))[1].append(b)
    out = []
    for sid in sorted(by):
        idx, bs = by[sid]
        bs = sorted(bs, key=lambda b: ((b["flags"] >> 8) & 0xF, b["c"][2], b["c"][1], b["c"][0]))
        s = {"id": sid, "index": idx, "centre": section_centre(*idx, grid_size, large), "kind": 1, "boxes": bs}
        s["hash"] = content_hash(s)
        out.append(s)
    return out

def payload_bytes(sections) -> int:
    return 16 + sum(24 + 64 * len(s["boxes"]) for s in sections)

def encode_skin(entity_id: int, version: int, sections) -> bytes:
    """C4 type 6 SkinSet payload (no message header): SkinSetHeader, then per section SkinSection + boxCount SkinBox."""
    out = bytearray(L.RECORDS["SkinSetHeader"].pack(entityId=entity_id, skinVersion=version,
                                                     sectionCount=len(sections)))
    for s in sections:
        out += section_bytes(s)
    return bytes(out)

def decode_skin(payload: bytes):
    """(header, sections, error): the consumer's checks (CONTRACT §5.4; Wire SkinCodec.TryDecode, wire_ref
 s6_skin_decode): exact length, at most 2 MiB, skinVersion >= 1, ascending unique section ids, boxCount >= 1, kind 1
 (a kind-2 section is ignored and counted in header["ignoredKind2"], any other kind refuses the message), finite
 values, 0 < half <= 4.0001, orthonormal right-handed rows within 1e-5, the identity rows on a box without the
 Rotated flag, no unknown flag bits. error is None when the message is good."""
    R = L.RECORDS
    if len(payload) < 16:
        return None, [], "payload %d B < 16" % len(payload)
    h = R["SkinSetHeader"].unpack(payload)
    h["ignoredKind2"] = 0
    if len(payload) > LIMITS["SkinMaxBytes"]:
        return h, [], "payload %d B > SkinMaxBytes" % len(payload)
    if h["skinVersion"] == 0:
        return h, [], "skinVersion 0"
    pos, sections, last = 16, [], None
    for i in range(h["sectionCount"]):
        if pos + 24 > len(payload):
            return h, sections, "section %d header beyond the payload" % i
        s = R["SkinSection"].unpack(payload, pos)
        n = s["boxCount"]
        if n < 1:
            return h, sections, "section %d boxCount 0" % i
        if s["kind"] not in (1, 2):
            return h, sections, "section %d kind %d" % (i, s["kind"])
        if not all(math.isfinite(v) for v in s["centre"]):
            return h, sections, "section %d centre not finite" % i
        if last is not None and s["sectionId"] <= last:
            return h, sections, "section ids not ascending/unique at %d (0x%08x after 0x%08x)" % (i, s["sectionId"], last)
        last = s["sectionId"]
        end = pos + 24 + 64 * n
        if end > len(payload):
            return h, sections, "section %d boxes beyond the payload" % i
        boxes = []
        for j in range(n):
            b = R["SkinBox"].unpack(payload, pos + 24 + 64 * j)
            vals = list(b["centre"]) + list(b["half"]) + list(b["axes"])
            if not all(math.isfinite(v) for v in vals):
                return h, sections, "section %d box %d not finite" % (i, j)
            if not all(0.0 < v <= MAX_HALF for v in b["half"]):
                return h, sections, "section %d box %d half %r out of (0, 4.0001]" % (i, j, b["half"])
            ax = (tuple(b["axes"][0:3]), tuple(b["axes"][3:6]), tuple(b["axes"][6:9]))
            for a in range(3):
                for c in range(3):
                    if abs(_dot(ax[a], ax[c]) - (1.0 if a == c else 0.0)) > ORTHO_TOL:
                        return h, sections, "section %d box %d axes not orthonormal" % (i, j)
            if _dot(_cross(ax[0], ax[1]), ax[2]) <= 0:
                return h, sections, "section %d box %d axes left-handed" % (i, j)
            flags = b["flags"]
            if flags & ~(SKIN_BOX_FLAGS["Rotated"] | SKIN_BOX_FLAGS["SourceMask"]):
                return h, sections, "section %d box %d unknown flag bits 0x%x" % (i, j, flags)
            if not flags & SKIN_BOX_FLAGS["Rotated"] and ax != IDENTITY_AXES:
                return h, sections, "section %d box %d: axes not the identity without the Rotated flag" % (i, j)
            boxes.append({"c": tuple(b["centre"]), "h": tuple(b["half"]), "axes": ax, "flags": flags})
        sec = {"id": s["sectionId"], "index": section_index_of(s["sectionId"]), "centre": tuple(s["centre"]),
               "kind": s["kind"], "boxes": boxes}
        sec["hash"] = L.fnv1a64(payload[pos:end])
        if s["kind"] == 2:
            h["ignoredKind2"] += 1             # never sent in v1: ignored and counted (CONTRACT §5.4)
        else:
            sections.append(sec)
        pos = end
    if pos != len(payload):
        return h, sections, "length %d B, the counts imply %d B" % (len(payload), pos)
    return h, sections, None

def skin_summary(entity_id, version, sections, detail=False) -> dict:
    out = {"entity": "0x%016x" % entity_id, "version": version, "sections": len(sections),
           "boxes": sum(len(s["boxes"]) for s in sections), "bytes": payload_bytes(sections),
           "rotated": sum(1 for s in sections for b in s["boxes"] if b["flags"] & 1)}
    if detail:
        out["detail"] = [{"id": "0x%08x" % s["id"], "c": [round(v, 4) for v in s["centre"]], "n": len(s["boxes"]),
                          "hash": "0x%016x" % s["hash"], "rhoMax": round(rho_max(s), 4)} for s in sections]
    return out

# ======================================================================================================================
# synthetic hulls of the stand-in producer
# ======================================================================================================================

SYNTH_BASE = 0x0044455336000000          # "DES6": stand-in entity ids never collide with SE's
HULL_IDS = {"bt1": SYNTH_BASE + 0x01, "bt1k": SYNTH_BASE + 0x02, "rotated": SYNTH_BASE + 0x03,
            "hull100": SYNTH_BASE + 0x04, "rotor": SYNTH_BASE + 0x05, "rotorHead": SYNTH_BASE + 0x06,
            "none": SYNTH_BASE + 0x08}
HULL_KINDS = ("bt1", "bt1k", "rotated", "hull100", "rotor", "none")
DEFAULT_SPEED = {"bt1": 5.0, "bt1k": 5.0, "rotated": 0.0, "hull100": 0.0, "rotor": 0.0, "none": 5.0}
ROTOR_RPM = 10.0
GRID_V1, GRID_SUBGRID = 1 << 28, 2
PART_V1, PART_FAT = 1 << 28, 3

def _bt1_boxes(nx, ny, nz):
    """The B-T1 envelope (SE-local, root large grid): x in [-1.75, 4.25], y in [-1.75, 4.25] (6 m), z in [-11.25, 8.75]
 (20 m), cut into nx * ny * nz equal boxes. With 5 x 2 x 5 the box centres fall into exactly 2 lattice sections
 (iz -1 and 0); see tests/standins/s6_stream.py selftest."""
    lo, hi = (-1.75, -1.75, -11.25), (4.25, 4.25, 8.75)
    n = (nx, ny, nz)
    size = tuple((hi[i] - lo[i]) / n[i] for i in range(3))
    out = []
    for k in range(nz):
        for j in range(ny):
            for i in range(nx):
                c = (lo[0] + size[0] * (i + 0.5), lo[1] + size[1] * (j + 0.5), lo[2] + size[2] * (k + 0.5))
                out.append(make_box(c, tuple(s / 2.0 for s in size)))
    return out

def _hull100_boxes():
    """100 m x 10 m x 10 m (z, x, y): 25 x 2 x 2 boxes of 4 x 5 x 5 m; y in [-7.5, 2.5] so the upper sections' roots
 (lattice y index 0, centre +6.25 m) sit above the hull while the hull's centre (-2.5 m) is 8.75 m lower (S6.N4:
 bury the hull centre under the seabed and the bitten upper sections still see the creature)."""
    out = []
    for k in range(25):
        for j, y in enumerate((-5.0, 0.0)):
            for x in (-2.5, 2.5):
                out.append(make_box((x, y, -48.0 + 4.0 * k), (2.5, 2.5, 2.0)))
    return out

def _rotated_boxes():
    """Eight boxes, six of them rotated (yaw-only, pitch, roll, compound), half extents up to 4 m (G1-E3 shapes)."""
    spec = (((0.0, 0.0, 0.0), (3.0, 1.0, 2.0), (30.0, 0.0, 0.0)),
            ((6.0, 2.0, -4.0), (2.0, 2.0, 1.0), (0.0, 45.0, 0.0)),
            ((-5.0, 1.0, 6.0), (1.0, 3.0, 1.5), (60.0, 0.0, 20.0)),
            ((2.0, -2.0, 9.0), (1.25, 1.25, 1.25), (0.0, 0.0, 0.0)),
            ((-6.0, -1.0, -8.0), (4.0, 0.5, 0.5), (15.0, 10.0, 5.0)),
            ((9.0, 0.0, 4.0), (0.5, 4.0, 2.5), (-35.0, -20.0, 0.0)),
            ((-2.0, 4.0, -2.0), (2.0, 0.75, 3.5), (90.0, 0.0, 0.0)),
            ((4.0, -3.0, -10.0), (1.5, 1.5, 3.0), (0.0, 0.0, 0.0)))
    return [make_box(c, h, rot_axes(*r)) for c, h, r in spec]

class Hull:
    """One synthetic stand-in ship: grids {id: {cls, parent, g, flags, boxes}}, its sections per grid, and the visual
 parts (one per box: (grid, partKey, half, rows12))."""

    def __init__(self, kind: str):
        if kind not in HULL_KINDS:
            raise ValueError("unknown hull %r (known: %s)" % (kind, ", ".join(HULL_KINDS)))
        self.kind = kind
        self.root = HULL_IDS[kind]
        self.grids = {}
        self.skinned = kind != "none"
        self.rotor_rpm = ROTOR_RPM if kind == "rotor" else 0.0
        self.rotor_angle = 0.0
        if kind == "bt1" or kind == "none":
            self._grid(self.root, 1, 0, _bt1_boxes(5, 2, 5))
        elif kind == "bt1k":
            self._grid(self.root, 1, 0, _bt1_boxes(10, 10, 10))
            self.visual_boxes = {self.root: _bt1_boxes(5, 2, 5)}       # the coarse 50-box visual, the fine skin
        elif kind == "rotated":
            self._grid(self.root, 1, 0, _rotated_boxes())
        elif kind == "hull100":
            self._grid(self.root, 1, 0, _hull100_boxes())
        elif kind == "rotor":
            base = [make_box((0.0, 0.0, 0.0), (3.75, 1.25, 3.75)), make_box((0.0, 2.5, 0.0), (1.25, 1.25, 1.25))]
            self._grid(self.root, 1, 0, base)
            head = HULL_IDS["rotorHead"]
            arm = [make_box((-1.5, 0.25, 0.0), (1.5, 0.25, 0.5)), make_box((1.5, 0.25, 0.0), (1.5, 0.25, 0.5)),
                   make_box((0.0, 0.75, 0.0), (0.5, 0.25, 0.5))]
            self._grid(head, 2, self.root, arm)
            self.rotor_base = (0.0, 4.0, 0.0)        # the head's origin in the root frame (on top of the stator box)

    def _grid(self, gid, cls, parent, boxes):
        g = GRID_SIZE[cls]
        self.grids[gid] = {"cls": cls, "parent": parent, "g": g, "large": cls == 1,
                           "flags": GRID_V1 | (GRID_SUBGRID if parent else 0), "boxes": boxes,
                           "sections": build_sections(boxes, g, cls == 1)}

    def sections(self, gid):
        return self.grids[gid]["sections"]

    def visual(self):
        """[(grid, partKey, half, rows12 local)] one part per visual box (rows: e0, e1, e2, centre)."""
        out = []
        vb = getattr(self, "visual_boxes", {})
        for n, (gid, g) in enumerate(sorted(self.grids.items())):
            for i, b in enumerate(vb.get(gid, g["boxes"])):
                key = L.fnv1a64(("s6part|%016x|%d" % (gid, i)).encode("ascii"))
                rows = tuple(b["axes"][0]) + tuple(b["axes"][1]) + tuple(b["axes"][2]) + tuple(b["c"])
                out.append((gid, key, b["h"], rows))
        return out

    def bounds(self, gid=None):
        """SE-local AABB of a grid's boxes (corners of every box)."""
        gid = self.root if gid is None else gid
        lo, hi = [1e30] * 3, [-1e30] * 3
        for b in self.grids[gid]["boxes"]:
            for sx in (-1, 1):
                for sy in (-1, 1):
                    for sz in (-1, 1):
                        p = [b["c"][a] + sx * b["h"][0] * b["axes"][0][a] + sy * b["h"][1] * b["axes"][1][a] +
                             sz * b["h"][2] * b["axes"][2][a] for a in range(3)]
                        for a in range(3):
                            lo[a], hi[a] = min(lo[a], p[a]), max(hi[a], p[a])
        return tuple(lo), tuple(hi)

    def advance(self, dt: float):
        self.rotor_angle = (self.rotor_angle + 2.0 * math.pi * self.rotor_rpm / 60.0 * dt) % (2.0 * math.pi)

    def rel(self, gid):
        """Subgrid world x root world^-1 as SE rows ((R), (U), (B), (T)): the C2 subgrid row (rotation about +Y)."""
        if gid == HULL_IDS["rotorHead"] and gid in self.grids:
            a = self.rotor_angle
            c, s = math.cos(a), math.sin(a)
            return ((c, 0.0, -s), (0.0, 1.0, 0.0), (s, 0.0, c), self.rotor_base)
        return ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))

    def subgrids(self):
        return [gid for gid, g in sorted(self.grids.items()) if g["parent"]]

    def census(self, gid, rows=False) -> dict:
        g = self.grids[gid]
        out = {"entity": "0x%016x" % gid, "root": "0x%016x" % (g["parent"] or gid), "subgrid": bool(g["parent"]),
               "large": g["large"], "gridSize": g["g"], "targetClass": g["cls"],
               "sections": len(g["sections"]), "boxes": len(g["boxes"]), "bytes": payload_bytes(g["sections"])}
        out["detail"] = [{"id": "0x%08x" % s["id"], "c": list(s["centre"]), "n": len(s["boxes"]),
                          "hash": "0x%016x" % s["hash"], "rhoMax": round(rho_max(s), 4)} for s in g["sections"]]
        if rows:
            for d, s in zip(out["detail"], g["sections"]):
                d["boxes"] = [{"c": list(b["c"]), "h": list(b["h"]), "axes": [list(e) for e in b["axes"]],
                               "flags": b["flags"]} for b in s["boxes"]]
        return out

# ---- the procedural box model of a visual part (C3), the same conventions as the S3a cube --------------------------

def box_model(half) -> dict:
    """A closed box of half extents (hx, hy, hz) in SE-local space, 4 vertices and 2 triangles per face, triangles in the
 MyModel.Triangles convention (cross(V1 - V0, V2 - V0) is the outward normal), UVs over the left square half of SE's
 2:1 armor atlases per face, tangent = +u, binormal = +v."""
    hx, hy, hz = half
    faces = (((1, 0, 0), (0, 0, -1), (0, 1, 0)), ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
             ((0, 1, 0), (1, 0, 0), (0, 0, -1)), ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
             ((0, 0, 1), (1, 0, 0), (0, 1, 0)), ((0, 0, -1), (-1, 0, 0), (0, 1, 0)))
    verts, tris = [], []
    h = (hx, hy, hz)
    for n, right, up in faces:
        c = tuple(n[a] * h[a] for a in range(3))
        ext_r = sum(abs(right[a]) * h[a] for a in range(3))
        ext_u = sum(abs(up[a]) * h[a] for a in range(3))
        base = len(verts)
        for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = tuple(c[a] + su * ext_r * right[a] + sv * ext_u * up[a] for a in range(3))
            verts.append({"pos": p, "normal": tuple(float(x) for x in n), "tangent": tuple(float(x) for x in right),
                          "binormal": tuple(-float(x) for x in up), "uv": (0.25 + 0.25 * su, 0.5 - 0.5 * sv)})
        # counter-clockwise seen from outside: (0, 1, 2), (0, 2, 3) has the outward normal for these (right, up, n)
        tris += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
        a0, a1, a2 = (verts[base + k]["pos"] for k in (0, 1, 2))
        g = _cross(tuple(a1[i] - a0[i] for i in range(3)), tuple(a2[i] - a0[i] for i in range(3)))
        if _dot(g, n) < 0:
            tris[-2:] = [(base, base + 2, base + 1), (base, base + 3, base + 2)]
    return {"verts": verts, "tris": tris, "bounds": (-hx, -hy, -hz, hx, hy, hz)}

def box_model_path(half) -> str:
    return "deepengineers/standin/s6_box_%gx%gx%g.mwm" % tuple(round(2 * v, 4) for v in half)

# ======================================================================================================================
# C8 records and the fake host's plans
# ======================================================================================================================

def damage_record(**kw) -> bytes:
    """One 128-byte C8 DamageRecord (CONTRACT §5.4 field table); missing fields 0."""
    kw.setdefault("unit", UNIT_SN_HP)
    return L.RECORDS["DamageRecord"].pack(**kw)

def unpack_record(rec: bytes) -> dict:
    return L.RECORDS["DamageRecord"].unpack(rec)

def record_row(v: dict) -> dict:
    """The s6.c8 row of the stand-in producer (and the fields the analyzer reads) for one unpacked record."""
    kind = {1: "grid", 2: "character"}.get(v["targetKind"], v["targetKind"])
    return {"id": hit_id(v["snGeneration"], v["dedupSeq"]), "gen": v["snGeneration"], "seq": v["dedupSeq"],
            "kind": kind, "target": "0x%016x" % v["targetEntityId"], "root": "0x%016x" % v["rootEntityId"],
            "section": "0x%08x" % v["sectionId"], "type": v["snDamageType"], "seType": se_damage_type(v["snDamageType"]),
            "amount": _jnum(v["amount"]), "kExpected": _jnum(v["seScaleExpected"]), "flags": v["flags"],
            "unit": v["unit"], "local": [_jnum(x) for x in v["localHit"]], "w": [_jnum(x) for x in v["snWorldHit"]],
            "dealerTechType": v["dealerTechType"], "dealerId": v["dealerInstanceId"], "epoch": v["worldEpoch"],
            "snapshotTick": v["snapshotTick"], "snTime": _jnum(v["snTimeSec"])}

def _jnum(x):
    """JSON-safe float (NaN/inf as strings, the analyzer reads them back)."""
    x = float(x)
    if math.isnan(x):
        return "NaN"
    if math.isinf(x):
        return "inf" if x > 0 else "-inf"
    return round(x, 6)

def fnum(v, default=None):
    """The analyzer's reader for numbers that may be written as strings ("NaN", "inf", "1.5")."""
    if isinstance(v, bool) or v is None:
        return default
    if isinstance(v, (int, float)):
        return float(v)
    try:
        return float(str(v).strip())
    except ValueError:
        return default

def quantized(rnd, lo: float, hi: float, step: float = 0.125) -> float:
    """Uniform on [lo, hi] on a 1/8 grid: every sum and every product by 1 or 25 is exact in float32."""
    n = int(round((hi - lo) / step))
    return lo + step * rnd.randint(0, n)

class Target:
    """A damage target as the fake host knows it from C4 (GridAdd + SkinSet) or C1 (the character)."""

    def __init__(self, entity, kind, cls=0, parent=0, sections=None):
        self.entity, self.kind, self.cls, self.parent = int(entity), kind, int(cls), int(parent)
        self.sections = sections or []

    @property
    def root(self):
        return self.parent or self.entity

    @property
    def subgrid(self):
        return self.kind == "grid" and self.parent != 0

    def k(self):
        return factor(TARGET_KIND[self.kind], self.cls)

    def describe(self):
        return {"entity": "0x%016x" % self.entity, "kind": self.kind, "cls": self.cls, "root": "0x%016x" % self.root,
                "subgrid": self.subgrid, "k": self.k(), "sections": len(self.sections)}

def _record_for(t: Target, seq, gen, epoch, amount, rnd, tick=0, sn_time=0.0, snt=0, flags_extra=0, box=None):
    if t.kind == "character":
        sid, local = 0, (0.0, 0.9, 0.0)
        flags = C8_FLAGS["MouthContact"]
    else:
        boxes = [(s["id"], b) for s in t.sections for b in s["boxes"]]
        sid, b = box if box is not None else boxes[rnd.randrange(len(boxes))]
        local = tuple(_f32(v) for v in b["c"])
        flags = C8_FLAGS["MouthContact"] | (C8_FLAGS["FrameSubgrid"] if t.subgrid else 0)
    flags |= flags_extra
    return dict(dedupSeq=seq, snGeneration=gen, unit=UNIT_SN_HP, amount=float(amount),
                seScaleExpected=float(t.k()), snDamageType=snt, flags=flags,
                targetKind=TARGET_KIND[t.kind], sectionId=sid, targetEntityId=t.entity, rootEntityId=t.root,
                snapshotTick=tick, localHit=local, snWorldHit=(0.0, 0.0, 0.0), attackDirSn=(0.0, 0.0, 0.0),
                dealerTechType=0, dealerInstanceId=0, worldEpoch=epoch, snTimeSec=float(sn_time))

def plan_n1(targets, gen, epoch, next_seq, seed=1, unique=30, dups=None, tick=0, sn_time=0.0):
    """S6.N1: `unique` bites per target (amounts on a 1/8 grid: grids [1, 3] SN_HP, the character [1, 2]; hit points at
 box centres), then `dups` (per-target counts, a dict entity -> n, or an int split as evenly as possible) re-sends of
 already-sent records, appended after every unique record. next_seq returns the next dedupSeq.
 Returns (records [(bytes, meta)], plan dict)."""
    rnd = random.Random(seed)
    recs, plan = [], {"mode": "n1", "seed": seed, "gen": gen, "epoch": epoch, "targets": []}
    if isinstance(dups, int) or dups is None:
        total = 10 if dups is None else dups
        n = len(targets)
        dups = {t.entity: total // n + (1 if i < total % n else 0) for i, t in enumerate(targets)} if n else {}
    sent_by = {}
    for t in targets:
        ids, amounts = [], []
        for _ in range(unique):
            amt = quantized(rnd, 1.0, 2.0 if t.kind == "character" else 3.0)
            seq = next_seq()
            f = _record_for(t, seq, gen, epoch, amt, rnd, tick, sn_time)
            rec = damage_record(**f)
            recs.append((rec, {"id": hit_id(gen, seq), "target": t.entity, "dup": False}))
            sent_by.setdefault(t.entity, []).append(rec)
            ids.append(hit_id(gen, seq))
            amounts.append(amt)
        total = sum(amounts)
        plan["targets"].append(dict(t.describe(), unique=unique, ids=ids, amounts=amounts, sum=total,
                                    expectedSe=total * t.k(), dupIds=[]))
    for t, pt in zip(targets, plan["targets"]):
        pool = sent_by.get(t.entity, [])
        for _ in range(int(dups.get(t.entity, 0)) if pool else 0):
            rec = pool[rnd.randrange(len(pool))]
            v = unpack_record(rec)
            did = hit_id(v["snGeneration"], v["dedupSeq"])
            recs.append((rec, {"id": did, "target": t.entity, "dup": True}))
            pt["dupIds"].append(did)
    plan["records"] = len(recs)
    plan["dups"] = sum(len(p["dupIds"]) for p in plan["targets"])
    return recs, plan

N10_MIX = (("Generation", 10), ("Epoch", 10), ("Sequence", 5), ("Unit", 5), ("Amount", 20), ("Kind", 5),
           ("Type", 5), ("Entity", 15), ("Section", 10), ("Frame", 5), ("HitPoint", 10))

def plan_n10(grids, character, gen, epoch, next_seq, seed=1, tick=0, sn_time=0.0):
    """S6.N10: 100 malformed records with fresh ids, each tagged with the DamageReject reason SE must give it under the
 CONTRACT §4.1 check order (a record is malformed in exactly one way and valid in every check before it).
 grids: [Target] with skins (at least one), character: Target or None. Returns (records, plan)."""
    if not grids:
        raise ValueError("n10 needs at least one mirrored grid with a skin")
    rnd = random.Random(seed)
    recs, rows = [], []
    nan, inf = float("nan"), float("inf")

    def add(reason, f, note):
        rec = damage_record(**f)
        rid = hit_id(f["snGeneration"], f["dedupSeq"])
        recs.append((rec, {"id": rid, "reason": reason, "note": note}))
        rows.append({"id": rid, "reason": reason, "note": note, "target": "0x%016x" % f["targetEntityId"]})

    for reason, count in N10_MIX:
        for i in range(count):
            g = grids[i % len(grids)]
            base = _record_for(g, next_seq(), gen, epoch, 1.0, rnd, tick, sn_time)
            note = ""
            if reason == "Generation":
                base["snGeneration"] = (gen - 1) & 0xFFFFFFFF if gen > 1 else gen + 1000
                note = "snGeneration %d" % base["snGeneration"]
            elif reason == "Epoch":
                base["worldEpoch"] = (epoch + 1 + i) & 0xFFFFFFFF
                note = "worldEpoch %d" % base["worldEpoch"]
            elif reason == "Sequence":
                base["dedupSeq"] = 0
                note = "dedupSeq 0"
            elif reason == "Unit":
                base["unit"] = (2, 0, 3, 7, 0xFFFF)[i % 5]
                note = "unit %d" % base["unit"]
            elif reason == "Amount":
                base["amount"] = (0.0, -3.0, 501.0, 1e6, nan, inf, -0.0, 500.125)[i % 8]
                note = "amount %r" % base["amount"]
            elif reason == "Kind":
                base["targetKind"] = (0, 3, 7, 0xFFFFFFFF, 4)[i % 5]
                note = "targetKind %d" % base["targetKind"]
            elif reason == "Type":
                base["snDamageType"] = (1, 2, 3, 9, 13)[i % 5]
                note = "snDamageType %d" % base["snDamageType"]
            elif reason == "Entity":
                j = i % 3
                if j == 0 or character is None:
                    base["targetEntityId"] = base["rootEntityId"] = 0x0DE5600000000001 + i
                    note = "unknown grid id"
                elif j == 1:
                    c = _record_for(character, base["dedupSeq"], gen, epoch, 1.0, rnd, tick, sn_time)
                    base = dict(c, targetEntityId=character.entity ^ 0x5A5A, rootEntityId=character.entity ^ 0x5A5A)
                    note = "character id not SE's"
                else:
                    base["rootEntityId"] = base["targetEntityId"] ^ 0x77
                    note = "rootEntityId not the grid's root"
            elif reason == "Section":
                if i % 3 == 2 and character is not None:
                    c = _record_for(character, base["dedupSeq"], gen, epoch, 1.0, rnd, tick, sn_time)
                    base = dict(c, sectionId=1 + i)
                    note = "character section %d" % base["sectionId"]
                else:
                    known = {s["id"] for s in g.sections}
                    sid = section_id(500 - i, 500, 500)
                    while sid in known:
                        sid += 1
                    base["sectionId"] = sid
                    note = "section 0x%08x not in the skin" % sid
            elif reason == "Frame":
                if i % 2 == 1 and character is not None:
                    c = _record_for(character, base["dedupSeq"], gen, epoch, 1.0, rnd, tick, sn_time)
                    base = dict(c, flags=c["flags"] | C8_FLAGS["FrameSubgrid"])
                    note = "FrameSubgrid on the character"
                else:
                    base["flags"] ^= C8_FLAGS["FrameSubgrid"]
                    note = "FrameSubgrid %s for a %s" % ("set" if base["flags"] & 8 else "clear",
                                                         "subgrid" if g.subgrid else "root grid")
            elif reason == "HitPoint":
                base["localHit"] = ((nan, 0.0, 0.0), (inf, 0.0, 0.0), (0.0, -inf, 0.0), (0.0, 0.0, nan))[i % 4]
                note = "localHit %r" % (base["localHit"],)
            add(reason, base, note)
    plan = {"mode": "n10", "seed": seed, "gen": gen, "epoch": epoch, "records": len(recs),
            "reasons": {r: n for r, n in N10_MIX}, "list": rows,
            "targets": [t.describe() for t in grids] + ([character.describe()] if character else [])}
    return recs, plan

def wm1_boxes(path, limit=None):
    """The `box` records of a WM-1 vector file in file order, each with its hull's depth (-o.y, SN) and tilt (the angle
 between q*Y and the vertical): the full file (wire_ref.py vectors) gives the pose by index into its `pose` records,
 the in-game subset (tests/wm1/subset-sn.jsonl) embeds pose_o / pose_q. [{index, depth, tilt, rotated, pose}]"""
    poses, out = [], []
    with open(path, encoding="utf-8") as f:
        for ln in f:
            if '"pose"' not in ln and '"box"' not in ln:
                continue
            try:
                r = json.loads(ln)
            except ValueError:
                continue
            kind = r.get("kind")
            if kind == "pose":
                poses.append(r)
                continue
            if kind != "box":
                continue
            o, q = r.get("pose_o"), r.get("pose_q")
            if o is None or q is None:
                i = r.get("pose")
                if isinstance(i, int) and 0 <= i < len(poses):
                    o, q = poses[i].get("o"), poses[i].get("q")
            if o is None or q is None:
                continue
            upy = 1.0 - 2.0 * (q[0] * q[0] + q[2] * q[2])
            out.append({"index": len(out), "depth": -float(o[1]), "rotated": bool(r.get("rotated")),
                        "tilt": math.degrees(math.acos(max(-1.0, min(1.0, upy)))), "pose": r.get("pose")})
            if limit and len(out) >= limit:
                break
    return out

class Dedup:
    """SE's dedup window (CONTRACT §4.1): per generation, the high water mark and a bitmap of the last 8,192 ids."""

    def __init__(self, window=LIMITS["DedupWindow"]):
        self.window = window
        self.generation = None
        self.hw = 0
        self.seen = set()
        self.counts = {"admitted": 0, "duplicates": 0, "tooOld": 0, "reordered": 0, "gaps": 0, "sequence": 0}

    def reset(self, generation):
        self.generation, self.hw, self.seen = generation, 0, set()

    def admit(self, gen, seq) -> str:
        if gen != self.generation:
            self.reset(gen)
        if seq == 0:
            self.counts["sequence"] += 1
            return "Sequence"
        if seq > self.hw:
            if seq > self.hw + 1:
                self.counts["gaps"] += seq - self.hw - 1
            self.hw = seq
            self.seen = {s for s in self.seen if s > self.hw - self.window}
            self.seen.add(seq)
            self.counts["admitted"] += 1
            return "None"
        if seq <= self.hw - self.window:
            self.counts["tooOld"] += 1
            return "TooOld"
        if seq in self.seen:
            self.counts["duplicates"] += 1
            return "Duplicate"
        self.seen.add(seq)
        self.counts["reordered"] += 1
        self.counts["admitted"] += 1
        return "None"

# ======================================================================================================================
# selftest and CLI
# ======================================================================================================================

def selftest(verbose=True) -> int:
    results = []

    def check(ok, what, detail=""):
        results.append(bool(ok))
        if verbose:
            print("  %s %s%s" % ("PASS" if ok else "FAIL", what, (": " + detail) if detail and not ok else ""))

    print("S6 stream self-test (tests/standins/s6_stream.py)")
    text = conventions_text()
    check(conventions_hash() == PINNED_HASH and text.count("\n") == PINNED_LINES and len(text) == PINNED_BYTES,
          "conventions twin: %d lines, %d bytes, FNV-1a 64 0x%016x (pinned 0x%016x)" % (
              text.count("\n"), len(text), conventions_hash(), PINNED_HASH))
    W = None
    try:
        import wire_ref as W
        if not hasattr(W, "s6_conventions_text"):
            W = None
    except ImportError:
        W = None
    if W is not None:
        check(W.s6_conventions_text() == text, "wire_ref.s6_conventions_text() equals this twin byte for byte")
        check(W.s6_section_id(-1, 0, 7) == section_id(-1, 0, 7) and all(
            W.s6_round_away(x) == round_away(x) for x in (0.5, -0.5, 2.5, -2.5, 0.49999999999999994, -7.4999)),
            "wire_ref.s6_section_id / s6_round_away agree")
    check(L.CONSTS["DamageFlags"] == {k: v for k, v in C8_FLAGS.items() if k != "GhostPlayer"},
          "layout DamageFlags untouched (GhostPlayer = 16 is the S6 extension)")
    check(L.RECORDS["SkinBox"].size == 64 and L.RECORDS["SkinSection"].size == 24 and
          L.RECORDS["SkinSetHeader"].size == 16 and L.RECORDS["DamageRecord"].size == 128, "record sizes 64/24/16/128")
    check([round_away(x) for x in (0.5, -0.5, 1.5, -1.5, 0.49999999999999994, 2.4999999, -2.5)] ==
          [1, -1, 2, -2, 0, 2, -3], "round_away = MidpointRounding.AwayFromZero (exact at 0.49999999999999994)")
    check(section_id(0, 0, 0) == (512 << 20) | (512 << 10) | 512 and section_index_of(section_id(-512, 511, -3)) ==
          (-512, 511, -3), "section id packing and unpacking")
    check(section_centre(0, 0, 0, 2.5, True) == (6.25, 6.25, 6.25) and section_centre(-1, 0, 0, 0.5, False)[0] == -8.25,
          "section centres (i*S + (S-1)/2)*g: large 6.25 m, small -8.25 m")
    check(section_index((-1.25, 13.7, 13.75), 2.5, True) == (-1, 0, 1), "lattice edges: -1.25 -> -1, 13.7 -> 0, "
                                                                       "13.75 -> 1 (round half away from zero)")
    hulls = {k: Hull(k) for k in HULL_KINDS}
    bt1 = hulls["bt1"]
    s = bt1.sections(bt1.root)
    check(len(bt1.grids[bt1.root]["boxes"]) == 50 and len(s) == 2, "bt1: 50 boxes in 2 sections (%s)" % [
        (section_index_of(x["id"]), len(x["boxes"])) for x in s])
    lo, hi = bt1.bounds()
    check(all(abs((hi[i] - lo[i]) - d) < 1e-9 for i, d in enumerate((6.0, 6.0, 20.0))), "bt1 envelope 6 x 6 x 20 m")
    k = hulls["bt1k"]
    check(len(k.grids[k.root]["boxes"]) == 1000 and len(k.visual()) == 50, "bt1k: 1,000 skin boxes, 50 visual parts")
    rot = hulls["rotated"]
    nrot = sum(1 for b in rot.grids[rot.root]["boxes"] if b["flags"] & 1)
    check(nrot == 6, "rotated: 6 of 8 boxes carry the Rotated flag")
    h100 = hulls["hull100"]
    lo, hi = h100.bounds()
    check(len(h100.grids[h100.root]["boxes"]) == 100 and abs(hi[2] - lo[2] - 100.0) < 1e-9, "hull100: 100 boxes, 100 m")
    r = hulls["rotor"]
    head = HULL_IDS["rotorHead"]
    check(r.grids[head]["parent"] == r.root and r.grids[head]["cls"] == 2 and len(r.sections(head)) == 2,
          "rotor: a small-grid head subgrid in 2 sections of the 16 m small lattice")
    r.advance(1.5)       # 10 rpm for 1.5 s = 90 degrees
    rr = r.rel(head)
    check(abs(rr[0][0]) < 1e-9 and abs(abs(rr[0][2]) - 1.0) < 1e-9, "rotor rel: 10 rpm turns 90 degrees in 1.5 s")
    for kind, h in hulls.items():
        for gid, g in h.grids.items():
            payload = encode_skin(gid, 3, g["sections"])
            hd, secs, err = decode_skin(payload)
            ok = err is None and hd["entityId"] == gid and hd["skinVersion"] == 3 and len(payload) == payload_bytes(
                g["sections"]) and [x["hash"] for x in secs] == [x["hash"] for x in g["sections"]]
            check(ok, "codec round trip %s/%x (%d B)" % (kind, gid & 0xFF, len(payload)), err or "")
            for sec in g["sections"]:
                if not all(section_index(b["c"], g["g"], g["large"]) == sec["index"] for b in sec["boxes"]):
                    check(False, "%s box in the wrong section" % kind)
    if W is not None:
        okw = True
        for kind, h in hulls.items():
            for gid, g in h.grids.items():
                payload = encode_skin(gid, 2, g["sections"])
                (eid, ver, cnt), wsecs, ign = W.s6_skin_decode(payload)
                okw &= eid == gid and ver == 2 and [x["hash"] for x in wsecs] == [x["hash"] for x in g["sections"]]
                ws = [W.s6_synthetic_section(x["id"], x["centre"], [W.s6_box(b["c"], b["h"], b["axes"], b["flags"])
                                                                    for b in x["boxes"]]) for x in g["sections"]]
                okw &= W.s6_skin_payload(gid, 2, ws) == payload
                if g["large"] or g["cls"] == 2:
                    okw &= all(W.s6_section_index(b["c"], g["g"], g["large"]) == x["index"]
                               for x in g["sections"] for b in x["boxes"])
        check(okw, "every stand-in skin: wire_ref s6_skin_decode / s6_skin_payload / s6_section_index agree byte for byte")
    payload = bytearray(encode_skin(bt1.root, 1, s))
    struct.pack_into("<f", payload, 16 + 24 + 12, 4.5)
    check(decode_skin(bytes(payload))[2] is not None, "decode refuses a 4.5 m half extent")
    check(decode_skin(encode_skin(bt1.root, 1, s) + b"\0")[2] is not None, "decode refuses a trailing byte")
    sw = encode_skin(bt1.root, 1, list(reversed(s)))
    check(decode_skin(sw)[2] is not None, "decode refuses descending section ids")
    m = box_model((0.6, 1.5, 2.0))
    okw = True
    for t in m["tris"]:
        a, b, c = (m["verts"][i]["pos"] for i in t)
        gn = _cross(tuple(b[i] - a[i] for i in range(3)), tuple(c[i] - a[i] for i in range(3)))
        okw &= _dot(gn, m["verts"][t[0]]["normal"]) > 0
    check(okw and len(m["verts"]) == 24 and len(m["tris"]) == 12, "box model: 24 vertices, 12 outward triangles")
    # plans
    seq = [0]

    def nxt():
        seq[0] += 1
        return seq[0]
    small = Target(HULL_IDS["rotorHead"], "grid", 2, r.root, r.sections(head))
    large = Target(bt1.root, "grid", 1, 0, s)
    char = Target(0x7000000000000001, "character")
    recs, plan = plan_n1([small, large, char], 5, 2, nxt, seed=1)
    check(len(recs) == 100 and plan["dups"] == 10 and all(len(p["ids"]) == 30 for p in plan["targets"]),
          "n1 plan: 3 x 30 unique + 10 duplicates = 100 records")
    exact = all(_f32(x) == x for p in plan["targets"] for x in p["amounts"])
    check(exact and plan["targets"][1]["expectedSe"] == plan["targets"][1]["sum"] * 25.0,
          "n1 amounts on the 1/8 grid (float32-exact); large expectation = sum x 25")
    d = Dedup()
    outcomes = [d.admit(unpack_record(rec)["snGeneration"], unpack_record(rec)["dedupSeq"]) for rec, _m in recs]
    check(outcomes.count("Duplicate") == 10 and outcomes.count("None") == 90, "dedup twin: 90 new, 10 Duplicate")
    v = unpack_record(recs[0][0])
    check(v["flags"] & C8_FLAGS["FrameSubgrid"] and v["rootEntityId"] == r.root and v["seScaleExpected"] == 1.0,
          "small-grid head record: FrameSubgrid, root = the rotor's root grid, k 1.0")
    recs10, p10 = plan_n10([large, small], char, 5, 2, nxt, seed=1)
    reasons = {}
    for _rec, meta in recs10:
        reasons[meta["reason"]] = reasons.get(meta["reason"], 0) + 1
    check(len(recs10) == 100 and reasons == dict(N10_MIX), "n10 plan: 100 records, mix %s" % reasons)
    ids = [meta["id"] for _rec, meta in recs10 if meta["reason"] not in ("Generation", "Sequence")]
    check(len(set(ids)) == len(ids), "n10: fresh ids")
    if W is not None:
        tg = {t.entity: t for t in (small, large)}

        def target(r):
            if r["targetKind"] == 2:
                if r["targetEntityId"] != char.entity or r["rootEntityId"] != char.entity:
                    return "Entity", False
                return ("None" if r["sectionId"] == 0 else "Section"), False
            t = tg.get(r["targetEntityId"])
            if t is None or r["rootEntityId"] != t.root:
                return "Entity", False
            if r["sectionId"] not in {x["id"] for x in t.sections}:
                return "Section", t.subgrid
            return "None", t.subgrid
        dd = W.S6Dedup()
        got = [W.s6_validate(W.s6_damage_unpack(rec), 5, 2, dd, target) for rec, _m in recs]
        check(got.count("None") == 90 and got.count("Duplicate") == 10,
              "n1 records through wire_ref.s6_validate: 90 accepted, 10 Duplicate")
        dd = W.S6Dedup()
        dd.reset(5)
        dd.admit(seq[0] + 1000)            # a high water mark beyond the n1 ids, as after the n1 part
        wrong = [(meta["reason"], W.s6_validate(W.s6_damage_unpack(rec), 5, 2, dd, target)) for rec, meta in recs10]
        wrong = [w for w in wrong if w[0] != w[1]]
        check(not wrong, "every n10 record gets its tagged reason from wire_ref.s6_validate", str(wrong[:5]))
    ok = sum(results)
    print("S6 STREAM SELFTEST %s (%d/%d)" % ("PASS" if ok == len(results) else "FAIL", ok, len(results)))
    return 0 if ok == len(results) else 1

def plumbing(seconds=25.0, keep=None) -> int:
    """The two stand-ins over a private section: fake_sn_host.py --s6 --damage n1 --damage-auto (it builds its n1 plan
 from the SkinSets it receives) and fake_se_producer.py --s6 --skin synth:rotor (a large root grid and a small-grid
 head: both target classes); halfway, the launcher's path: a harness sends the host de.s6 {action: skins} and
 {action: damage, mode n10} over C11c. The producer's s6.c8 rows must hold every planned record (duplicates
 included), the n1 subgrid records the head's id and FrameSubgrid."""
    import subprocess
    import tempfile
    out = keep or tempfile.mkdtemp(prefix="de-s6-plumbing-")
    os.makedirs(out, exist_ok=True)
    name = "Local\\DeepEngineers_s6_plumbing_%d" % os.getpid()
    py = [sys.executable, "-I"]
    host = py + [os.path.join(_HERE, "fake_sn_host.py"), "--name", name, "--no-token-check", "--s1", "--s3", "--s6",
                 "--damage", "n1", "--damage-auto", "--damage-rate", "0", "--hold-seconds", "%g" % seconds, "--quiet"]
    prod = py + [os.path.join(_HERE, "fake_se_producer.py"), "--name", name, "--s6", "--skin", "synth:rotor",
                 "--skin-spawn-at", "0,-8,20", "--quiet", "--load-after", "0.5", "--load-seconds", "0.5",
                 "--run-seconds", "%g" % (seconds - 3.0)]
    print("plumbing in %s (section %s)" % (out, name))
    with open(os.path.join(out, "sn-standin.jsonl"), "wb") as fh, open(os.path.join(out, "se-standin.jsonl"), "wb") as fp:
        ph = subprocess.Popen(host, stdout=fh, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, cwd=_REPO)
        import time
        time.sleep(1.5)
        pp = subprocess.Popen(prod, stdout=fp, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, cwd=_REPO)
        # the launcher's path: de.s6 commands to the host over C11c (skins, then an n10 plan on what it knows)
        import section_py as S
        time.sleep(min(10.0, seconds / 2.0))
        cmd = {}
        sec = S.Section.open(name)
        if sec is not None:
            h = S.Harness(sec)
            if h.attach()[0]:
                for key, args in (("skins", {"action": "skins"}),
                                  ("n10", {"action": "damage", "mode": "n10", "targets": "auto", "seed": 2})):
                    for _ in range(5):
                        rid, _st = h.send("sn", "de.s6", args)
                        rep = h.wait_reply(rid, 3.0)
                        if rep is not None:
                            cmd[key] = rep
                            break
                h.detach()
            sec.close()
        try:
            pp.wait(timeout=seconds + 30)
            ph.wait(timeout=seconds + 30)
        except subprocess.TimeoutExpired:
            for p in (pp, ph):
                if p.poll() is None:
                    p.terminate()

    def rows(path):
        out_ = []
        with open(path, encoding="utf-8", errors="replace") as f:
            for ln in f:
                ln = ln.strip()
                if ln.startswith("{"):
                    try:
                        out_.append(json.loads(ln))
                    except ValueError:
                        pass
        return out_
    hr = rows(os.path.join(out, "sn-standin.jsonl"))
    pr = rows(os.path.join(out, "se-standin.jsonl"))
    plans = [x for x in hr if x.get("ev") == "s6.plan"]
    skins = [x for x in hr if x.get("ev") == "s6.skin"]
    c8 = [x for x in pr if x.get("ev") == "s6.c8"]
    results = []

    def check(ok, what):
        results.append(bool(ok))
        print("  %s %s" % ("PASS" if ok else "FAIL", what))
    check(len(skins) >= 2 and all(not x.get("bad") for x in skins), "host decoded %d SkinSets (bad 0)" % len(skins))
    n1 = [x for x in plans if x.get("mode") == "n1"]
    n10 = [x for x in plans if x.get("mode") == "n10"]
    check(len(n1) == 1, "host built one n1 plan by itself (--damage-auto: %d)" % len(n1))
    sk = (cmd.get("skins") or {}).get("skins") or []
    check(len(sk) == 2 and sorted(s["cls"] for s in sk) == [1, 2], "de.s6 {action: skins}: the two grids and classes")
    rep10 = cmd.get("n10") or {}
    check(rep10.get("ok") and len(n10) == 1 and n10[0]["records"] == 100, "de.s6 {action: damage, mode n10}: 100 "
                                                                          "malformed records planned")
    if n1:
        p = n1[0]
        want = sum(len(t["ids"]) + len(t["dupIds"]) for t in p["targets"]) + (n10[0]["records"] if n10 else 0)
        got_ids = [x["id"] for x in c8 if x["id"] in {i for t in p["targets"] for i in t["ids"]}]
        check(len(c8) == want, "producer drained %d of %d planned records into s6.c8 rows" % (len(c8), want))
        dup = sum(len(t["dupIds"]) for t in p["targets"])
        check(len(got_ids) - len(set(got_ids)) == dup, "duplicates arrive as the same ids (%d)" % dup)
        classes = sorted(t["cls"] for t in p["targets"])
        check(classes == [1, 2], "targets: one large and one small grid (%s)" % classes)
        n1_ids = {i for t in p["targets"] for i in t["ids"]}
        sub = [x for x in c8 if x["id"] in n1_ids and x["flags"] & C8_FLAGS["FrameSubgrid"]]
        check(sub and all(x["target"] == "0x%016x" % HULL_IDS["rotorHead"] for x in sub),
              "n1 subgrid records carry the head's id and FrameSubgrid")
    ok = all(results) and results
    print("S6 PLUMBING %s (%d/%d) -> %s" % ("PASS" if ok else "FAIL", sum(results), len(results), out))
    return 0 if ok else 1

def main(argv=None) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="S6 stream tools (stand-ins)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("selftest")
    p = sub.add_parser("hull")
    p.add_argument("kind", choices=HULL_KINDS)
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("plumbing")
    p.add_argument("--seconds", type=float, default=25.0)
    p.add_argument("--keep", default=None)
    sub.add_parser("conventions")
    a = ap.parse_args(argv)
    if a.cmd == "selftest":
        return selftest()
    if a.cmd == "conventions":
        sys.stdout.write(conventions_text())
        print("FNV-1a 64 0x%016x" % conventions_hash())
        return 0
    if a.cmd == "plumbing":
        return plumbing(a.seconds, a.keep)
    h = Hull(a.kind)
    out = [h.census(gid, rows=a.json) for gid in sorted(h.grids)]
    if a.json:
        print(json.dumps(out, indent=1))
    else:
        for c in out:
            print("%s %s cls %d: %d sections, %d boxes, %d B" % (a.kind, c["entity"], c["targetClass"], c["sections"],
                                                                 c["boxes"], c["bytes"]))
            for d in c["detail"]:
                print("   section %s centre %s boxes %d hash %s rhoMax %.3f" % (d["id"], d["c"], d["n"], d["hash"],
                                                                                d["rhoMax"]))
    return 0

if __name__ == "__main__":
    sys.exit(main())
