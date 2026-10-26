"""S7 statics reference (stdlib only): the C12 codec, the Subnautica-side rules and the SE-side geometry of the second
terrain path, written from CONTRACT section 2.3 (staticAxes) and section 5.4 "S7 conventions" (`format statics.axes`,
`statics.capsule`, `statics.host`, "C12 static primitives (S7; S7Statics)", "C12 messages"), independently of the C#
code (src/DeepEngineers.Wire/S7Statics.cs). The site map F, its inverse and mapdir come from the reference
tools/worldmap/worldmap_ref.py (site v1 around site_v1.TEST_C, as SiteMap.SiteV1Test in C#).

Usage (python -I):
 s7_ref.py vectors [OUT] write the vector file (default tests/s7/vectors/s7_statics.tsv); deterministic
 s7_ref.py check regenerate in memory and compare with the committed vector file (exit 1 on a difference)
 s7_ref.py selftest the reference's own checks (round trips, closed forms, rules), then `check`

Vector file: UTF-8 text, LF; '#' lines are comments; every other line is `<kind>\t<key>=<value>\t...`. Values: numbers
in Python repr (doubles; wire inputs are float32 values), lists comma-separated with an `L` prefix, byte strings as hex
with an `x` prefix, strings as UTF-8 hex with an `s` prefix. Read by tests/DeepEngineers.Wire.Tests/S7StaticsVectorTests.cs.
"""
from __future__ import annotations

import math
import os
import random
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import worldmap_ref as wm  # noqa: E402
import site_v1  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_VECTORS = os.path.join(REPO, "tests", "s7", "vectors", "s7_statics.tsv")

# ---------------------------------------------------------------- the S7 constants this file uses (CONTRACT 5.4)
C12_VERSION = 1
STATIC_FLAGS = {"Census": 1, "AllowListed": 2, "SkewApprox": 4}
LIMITS = {"BatchM": 160, "CreateRadiusM": 300, "ScanRadiusM": 320, "CloseRadiusM": 350, "MaxPerHost": 1024,
          "MinRescanMs": 2000, "MaxPerBatch": 4096}
KIND = {"Box": 1, "Capsule": 2, "Sphere": 3, "MeshObb": 4}
LAND_ORIGIN = (-2048.0, -3040.0, -2048.0)     # LargeWorldStreamer land origin (S4 SnOctreeFormat.Origin*)
BATCH_COUNT = (26, 20, 26)
HOST_PREFIX = "DE.Statics."
MIN_HALF = 0.005
MAX_HALF = 2048.0
SHEAR_TOL = 0.01
DET_TOL = 1e-3
HEADER = 16
PRIM = 48

STATUS = ["ok", "tooShort", "badCount", "tooMany", "badVersion", "badKind", "nonFinite", "badSize", "badAxis",
          "badRotation", "bufferTooSmall"]
REASONS = ["none", "disabled", "trigger", "layer", "proxy", "bridge", "notWorld", "creature", "pickupable", "vehicle",
           "freezeWhenFar", "worldForces", "rigidbody", "liveMixin", "mutantM3capsule", "aboveSea", "degenerate",
           "duplicate", "full"]

def f32(x):
    return struct.unpack("<f", struct.pack("<f", float(x)))[0]

def add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def mul(a, k): return (a[0] * k, a[1] * k, a[2] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def cross(a, b): return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def norm(a): return math.sqrt(dot(a, a))

def qnorm(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)

def qrot(q, v):
    """Unity `q * v` for a unit quaternion (x, y, z, w)."""
    u = (q[0], q[1], q[2])
    t = mul(cross(u, v), 2.0)
    return add(add(v, mul(t, q[3])), cross(u, t))

AXES = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))

# ---------------------------------------------------------------- the C12 codec
def encode(bx, by, bz, epoch, prims, or_flags=0):
    """BatchStatics payload: {i16 bx, by, bz; u16 version 1; u32 epoch; u32 count} + 48 B per primitive {u8 kind,
 u8 flags, u16 pad, f32 centre[3], f32 q[4], f32 size[3], u32 classIdHash}."""
    out = bytearray(struct.pack("<hhhHII", bx, by, bz, C12_VERSION, epoch, len(prims)))
    for p in prims:
        out += struct.pack("<BBH3f4f3fI", p["kind"], (p["flags"] | or_flags) & 0xFF, 0, *p["c"], *p["q"], *p["size"],
                           p["cls"] & 0xFFFFFFFF)
    return bytes(out)

def check_prim(p):
    if not (1 <= p["kind"] <= 4):
        return "badKind"
    vals = list(p["c"]) + list(p["q"]) + list(p["size"])
    if any(math.isnan(v) or math.isinf(v) for v in vals):
        return "nonFinite"
    ql = math.sqrt(sum(float(c) * float(c) for c in p["q"]))
    if abs(ql - 1.0) > 1e-3:
        return "badRotation"
    s = p["size"]
    if p["kind"] in (1, 4):
        if not all(0 < v <= MAX_HALF for v in s):
            return "badSize"
    elif p["kind"] == 2:
        if not (0 < s[0] <= MAX_HALF) or not (0 <= s[1] <= MAX_HALF):
            return "badSize"
        if s[2] not in (0.0, 1.0, 2.0):
            return "badAxis"
    else:
        if not (0 < s[0] <= MAX_HALF):
            return "badSize"
    return "ok"

def decode(data, capacity=LIMITS["MaxPerBatch"]):
    """(status, header dict, prims); the whole message is refused on any bad primitive."""
    if len(data) < HEADER:
        return "tooShort", None, []
    bx, by, bz, ver, epoch, count = struct.unpack_from("<hhhHII", data, 0)
    h = {"bx": bx, "by": by, "bz": bz, "version": ver, "epoch": epoch, "count": count}
    if count > LIMITS["MaxPerBatch"]:
        return "tooMany", h, []
    if len(data) != HEADER + PRIM * count:
        return "badCount", h, []
    if ver != C12_VERSION:
        return "badVersion", h, []
    if count > capacity:
        return "bufferTooSmall", h, []
    prims = []
    for i in range(count):
        v = struct.unpack_from("<BBH3f4f3fI", data, HEADER + PRIM * i)
        p = {"kind": v[0], "flags": v[1], "c": v[3:6], "q": v[6:10], "size": v[10:13], "cls": v[13]}
        st = check_prim(p)
        if st != "ok":
            return st, h, []
        prims.append(p)
    return "ok", h, prims

# ---------------------------------------------------------------- ids, keys, sizes
def class_id_hash(s):
    if s is None:
        return 0
    h = 0x811c9dc5
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * 0x01000193) & 0xFFFFFFFF
    return h

def quant(v):
    x = float(v) * 100.0
    q = math.floor(abs(x) + 0.5) * (1 if x >= 0 else -1)   # half away from zero
    return max(-2 ** 31, min(2 ** 31 - 1, int(q)))

def quant_rot(v):
    x = float(v) * 1000.0
    q = math.floor(abs(x) + 0.5) * (1 if x >= 0 else -1)   # half away from zero
    return max(-2 ** 31, min(2 ** 31 - 1, int(q)))

def dedup_key(kind, c, size, q, cls):
    """S7 review F16: (kind, centre and size to 1 cm, rotation to 1/1000, class hash) -> FNV-1a 64 of 45 bytes."""
    data = struct.pack("<B3i3i4iI", kind, quant(c[0]), quant(c[1]), quant(c[2]), quant(size[0]), quant(size[1]), quant(size[2]),
                       quant_rot(q[0]), quant_rot(q[1]), quant_rot(q[2]), quant_rot(q[3]), cls & 0xFFFFFFFF)
    h = 0xcbf29ce484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

def clamp_half(v):
    return MIN_HALF if v < MIN_HALF else v

def box_half(size, scale):
    return tuple(clamp_half(abs(0.5 * size[i] * scale[i])) for i in range(3))

def mesh_half(ext, scale):
    return tuple(clamp_half(abs(ext[i] * scale[i])) for i in range(3))

def capsule_size(radius, height, direction, scale):
    s = [abs(x) for x in scale]
    along = s[direction]
    across = max(s[(direction + 1) % 3], s[(direction + 2) % 3])
    r = clamp_half(abs(radius) * across)
    h = abs(height) * along
    return (r, max(0.0, 0.5 * h - r), float(direction))

def sphere_radius(radius, scale):
    return clamp_half(abs(radius) * max(abs(x) for x in scale))

def shear(c0, c1, c2):
    l0, l1, l2 = norm(c0), norm(c1), norm(c2)
    if l0 <= 0 or l1 <= 0 or l2 <= 0:
        return 1.0
    return max(abs(dot(c0, c1)) / (l0 * l1), abs(dot(c0, c2)) / (l0 * l2), abs(dot(c1, c2)) / (l1 * l2))

def lowest_y(p):
    q = qnorm(p["q"])
    cy = float(p["c"][1])
    s = p["size"]
    if p["kind"] == 2:
        a = qrot(q, AXES[int(s[2])])
        return cy - abs(a[1]) * s[1] - s[0]
    if p["kind"] == 3:
        return cy - s[0]
    return cy - sum(abs(qrot(q, AXES[i])[1]) * s[i] for i in range(3))

# ---------------------------------------------------------------- batches
def batch_mins(bx, by, bz):
    m = LIMITS["BatchM"]
    return (LAND_ORIGIN[0] + bx * m, LAND_ORIGIN[1] + by * m, LAND_ORIGIN[2] + bz * m)

def batch_of(p):
    m = LIMITS["BatchM"]
    return tuple(int(math.floor((p[i] - LAND_ORIGIN[i]) / m)) for i in range(3))

def distance_to_batch(p, b):
    lo = batch_mins(*b)
    m = LIMITS["BatchM"]
    d = [max(0.0, lo[i] - p[i], p[i] - (lo[i] + m)) for i in range(3)]
    return math.sqrt(dot(d, d))

# ---------------------------------------------------------------- SE geometry
def det_rows(r):
    return dot(r[0], cross(r[1], r[2]))

def to_se(site, p):
    """CONTRACT 2.3 / format statics.*: box & MeshObb: centre F^-1(c), rows (mapdir^-1(q*x), mapdir^-1(q*y),
 mapdir^-1(q*(-z))) at the centre, half_i * sqrt((a_i.x^2 + a_i.z^2)/s^2 + a_i.y^2); capsule ends F^-1(c -/+ a*h),
 radius r/s(c); sphere centre F^-1(c), radius r/s(c)."""
    c = tuple(float(v) for v in p["c"])
    q = qnorm(tuple(float(v) for v in p["q"]))
    s3 = p["size"]
    if p["kind"] in (1, 4):
        P = site.sn_to_se(c)
        s = site.Rs / norm(sub(P, site.C))
        rows = (site.dir_sn_to_se(P, qrot(q, (1.0, 0.0, 0.0))), site.dir_sn_to_se(P, qrot(q, (0.0, 1.0, 0.0))),
                site.dir_sn_to_se(P, qrot(q, (0.0, 0.0, -1.0))))
        half = []
        for i in range(3):
            a = qrot(q, AXES[i])
            half.append(float(s3[i]) * math.sqrt((a[0] * a[0] + a[2] * a[2]) / (s * s) + a[1] * a[1]))
        d = det_rows(rows)
        return {"centre": P, "rows": rows, "half": tuple(half), "s": s, "det": d, "ok": abs(d - 1.0) <= DET_TOL}
    if p["kind"] == 2:
        a = qrot(q, AXES[int(s3[2])])
        h = float(s3[1])
        A = site.sn_to_se(sub(c, mul(a, h)))
        B = site.sn_to_se(add(c, mul(a, h)))
        P = site.sn_to_se(c)
        s = site.Rs / norm(sub(P, site.C))
        return {"a": A, "b": B, "centre": mul(add(A, B), 0.5), "radius": float(s3[0]) / s, "s": s, "ok": True}
    P = site.sn_to_se(c)
    s = site.Rs / norm(sub(P, site.C))
    return {"centre": P, "radius": float(s3[0]) / s, "s": s, "ok": True}

# ---------------------------------------------------------------- hosts, census files, rules
def host_name(bx, by, bz, epoch, part):
    n = "%s%d.%d.%d.e%d" % (HOST_PREFIX, bx, by, bz, epoch)
    return n + (".p%d" % part if part >= 0 else "")

def parse_host_name(name):
    if not name.startswith(HOST_PREFIX):
        return None
    t = name[len(HOST_PREFIX):].split(".")
    if len(t) not in (4, 5) or not t[3].startswith("e"):
        return None
    try:
        bx, by, bz = int(t[0]), int(t[1]), int(t[2])
        epoch = int(t[3][1:])
        part = -1
        if len(t) == 5:
            if not t[4].startswith("p"):
                return None
            part = int(t[4][1:])
    except ValueError:
        return None
    return bx, by, bz, epoch, part

def host_parts(count):
    m = LIMITS["MaxPerHost"]
    return 1 if count <= m else (count + m - 1) // m

def host_range(part, count):
    m = LIMITS["MaxPerHost"]
    start = part * m
    return start, max(0, min(m, count - start))

def host_wanted(exists, d):
    return d <= (LIMITS["CloseRadiusM"] if exists else LIMITS["CreateRadiusM"])

def supersedes(have, cur_census, cur_epoch, in_census, in_epoch):
    if not have:
        return True
    if cur_census != in_census:
        return not in_census
    return in_epoch > cur_epoch

def census_file_name(bx, by, bz):
    return "%d.%d.%d.c12" % (bx, by, bz)

def filter_decide(c, static_or_kinematic=False, layer_mask=1):
    """CONTRACT 5.4 "C12 static primitives (S7)": by entity type with the census allow-list; S7.M7 = static-or-kinematic.
 S7 review: the layer test takes the reporter's mask (F5; default the Default layer only), and WorldForces is waived
 for a class on the census list allowWorldForces while its body is kinematic (F3, key "allowWf")."""
    if not c["enabled"]:
        return "disabled"
    if c["trigger"]:
        return "trigger"
    if not (0 <= c["layer"] <= 31) or not (layer_mask >> c["layer"]) & 1:
        return "layer"
    if c["proxy"]:
        return "proxy"
    if c["bridge"]:
        return "bridge"
    if not c["world"]:
        return "notWorld"
    if static_or_kinematic:
        return "rigidbody" if c["rb"] and not c["kin"] else "none"
    for k, r in (("creature", "creature"), ("pickupable", "pickupable"), ("vehicle", "vehicle"), ("freeze", "freezeWhenFar")):
        if c[k]:
            return r
    if c["worldForces"] and not (c.get("allowWf") and c["rb"] and c["kin"]):
        return "worldForces"
    if c["rb"] and not c["allow"]:
        return "rigidbody"
    if c["live"] and not c["allow"]:
        return "liveMixin"
    return "none"

def player_solid_mask(player_collides, terrain_layer, trigger_layer):
    """S7 review F5 (twin of S7StaticsFilter.PlayerSolidMask): the Default layer plus every layer the player collides
 with, minus TerrainCollider and Trigger (an int32 like the C# mask)."""
    mask = 1
    for layer in range(32):
        if layer in (terrain_layer, trigger_layer):
            continue
        if player_collides(layer):
            mask |= 1 << layer
    return mask - (1 << 32) if mask >= (1 << 31) else mask

FILTER_FIELDS = ("enabled", "trigger", "proxy", "bridge", "world", "creature", "pickupable", "vehicle", "freeze",
                 "worldForces", "rb", "kin", "live", "allow", "allowWf")

class BatchSet:
    """Twin of S7StaticsBatchSet: grow-only, dedup by (kind, centre and size to 1 cm, rotation to 1/1000, class hash),
 MaxPerBatch, epochs."""

    def __init__(self, b):
        self.b, self.items, self.keys, self.epoch, self.dirty = b, [], set(), 0, False

    def add(self, p):
        k = dedup_key(p["kind"], p["c"], p["size"], p["q"], p["cls"])
        if k in self.keys:
            return "duplicate"
        if len(self.items) >= LIMITS["MaxPerBatch"]:
            return "full"
        self.keys.add(k)
        self.items.append(p)
        self.dirty = True
        return "added"

    def next_epoch(self):
        self.epoch += 1
        self.dirty = False
        return self.epoch

# ---------------------------------------------------------------- vectors
def site():
    return site_v1.SITE_V1

def fmt(v):
    if isinstance(v, bool):
        return "1" if v else "0"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return repr(v)
    if isinstance(v, (bytes, bytearray)):
        return "x" + bytes(v).hex()
    if isinstance(v, str):
        return "s" + v.encode("utf-8").hex()
    if isinstance(v, (list, tuple)):
        flat = []
        for x in v:
            if isinstance(x, (list, tuple)):
                flat.extend(x)
            else:
                flat.append(x)
        return "L" + ",".join(fmt(float(x)) if isinstance(x, float) else str(x) for x in flat)
    raise TypeError(type(v))

def line(_tag, **kv):
    return _tag + "".join("\t%s=%s" % (k, fmt(v)) for k, v in kv.items())

def random_unit_quat(rnd):
    while True:
        q = tuple(rnd.uniform(-1, 1) for _ in range(4))
        n = math.sqrt(sum(c * c for c in q))
        if 0.2 < n <= 1.0:
            q = tuple(c / n for c in q)
            if q[3] < 0:
                q = tuple(-c for c in q)
            return tuple(f32(c) for c in q)

def random_prim(rnd, kind=None, near=None):
    kind = kind or rnd.choice((1, 2, 3, 4))
    base = near or (rnd.uniform(-1800, 1800), rnd.uniform(-1500, -2), rnd.uniform(-1800, 1800))
    c = tuple(f32(base[i] + rnd.uniform(-60, 60)) for i in range(3))
    q = random_unit_quat(rnd) if rnd.random() < 0.8 else (0.0, 0.0, 0.0, 1.0)
    if kind in (1, 4):
        size = tuple(f32(rnd.uniform(0.05, 40.0)) for _ in range(3))
    elif kind == 2:
        size = (f32(rnd.uniform(0.05, 3.0)), f32(rnd.uniform(0.0, 12.0)), float(rnd.choice((0, 1, 2))))
    else:
        size = (f32(rnd.uniform(0.05, 20.0)), 0.0, 0.0)
    cls = class_id_hash("%08x-class-%d" % (rnd.getrandbits(32), kind))
    flags = rnd.choice((0, STATIC_FLAGS["AllowListed"], STATIC_FLAGS["SkewApprox"]))
    return {"kind": kind, "flags": flags, "c": c, "q": q, "size": size, "cls": cls}

def build_vectors():
    rnd = random.Random(0x5e7a7)
    st = site()
    out = ["# S7 statics vectors (tools/worldmap/s7_ref.py vectors; deterministic, seed 0x5e7a7). Site: site_v1.SITE_V1.",
           line("site", C=st.C, Rs=st.Rs)]
    # --- geometry: boxes, mesh OBBs, capsules, spheres (incl. axis-aligned and the shallow surface band)
    for i in range(60):
        kind = (1, 4, 2, 3)[i % 4]
        near = None
        if i % 10 == 0:
            near = (rnd.uniform(-50, 50), -3.0, rnd.uniform(-50, 50))      # near the anchor, just under the sea
        if i % 10 == 5:
            near = (1990.0, -1700.0, -1990.0)                              # a far corner, deep
        p = random_prim(rnd, kind, near)
        r = to_se(st, p)
        prim = dict(kind=p["kind"], c=p["c"], q=p["q"], size=p["size"])
        if kind in (1, 4):
            out.append(line("box", **prim, centre=r["centre"], rows=r["rows"], half=r["half"], s=r["s"], det=r["det"]))
        elif kind == 2:
            out.append(line("capsule", **prim, a=r["a"], b=r["b"], centre=r["centre"], radius=r["radius"], s=r["s"]))
        else:
            out.append(line("sphere", **prim, centre=r["centre"], radius=r["radius"], s=r["s"]))
    # --- lowest y / above the sea
    for i in range(24):
        p = random_prim(rnd, (1, 2, 3, 4)[i % 4], (rnd.uniform(-100, 100), rnd.uniform(-20, 30), rnd.uniform(-100, 100)))
        out.append(line("lowest", kind=p["kind"], c=p["c"], q=p["q"], size=p["size"], y=lowest_y(p), above=lowest_y(p) > 0))
    # --- Unity collider sizes
    for i in range(24):
        scale = tuple(f32(rnd.choice((1.0, -1.0)) * rnd.uniform(0.2, 3.0)) for _ in range(3))
        if i % 3 == 0:
            scale = (1.0, 1.0, 1.0)
        size = tuple(f32(rnd.uniform(0.0, 10.0)) for _ in range(3))
        out.append(line("boxhalf", size=size, scale=scale, half=box_half(size, scale), mesh=mesh_half(size, scale)))
        r, hgt, d = f32(rnd.uniform(0.0, 3.0)), f32(rnd.uniform(0.0, 12.0)), rnd.choice((0, 1, 2))
        out.append(line("capsize", radius=r, height=hgt, dir=d, scale=scale, out=capsule_size(r, hgt, d, scale),
                        sphere=sphere_radius(r, scale)))
    for i in range(12):
        cols = [tuple(rnd.uniform(-2, 2) for _ in range(3)) for _ in range(3)]
        if i % 3 == 0:     # an orthogonal frame with a non-uniform scale: no shear
            q = qnorm(tuple(rnd.uniform(-1, 1) for _ in range(4)))
            cols = [mul(qrot(q, AXES[k]), rnd.uniform(0.5, 3.0)) for k in range(3)]
        sh = shear(*cols)
        out.append(line("shear", c0=cols[0], c1=cols[1], c2=cols[2], shear=sh, skewed=sh > SHEAR_TOL))
    # --- ids and keys
    for cid in ("", "a", "8e1d3c1c-8c4a-4c7e-9a4b-0f3a2b1c5d6e", "Aurora_Main", "été-世界", "ReaperLeviathan"):
        out.append(line("hash", id=cid, fnv32=class_id_hash(cid)))
    for i in range(30):
        c = tuple(f32(rnd.uniform(-2000, 2000)) for _ in range(3))
        if i % 5 == 0:
            c = tuple(f32(k + 0.005) for k in (rnd.randint(-300, 300) / 100.0 for _ in range(3)))   # the rounding edge
        kind = rnd.choice((1, 2, 3, 4))
        cls = rnd.getrandbits(32)
        size = tuple(f32(rnd.uniform(0.005, 40.0)) for _ in range(3))
        rot = random_unit_quat(rnd)
        if i % 5 == 1:
            size = tuple(f32(k + 0.005) for k in (rnd.randint(1, 300) / 100.0 for _ in range(3)))   # the size rounding edge
        out.append(line("dedup", kind=kind, c=c, cls=cls, q=[quant(v) for v in c], size=size, qs=[quant(v) for v in size], rot=rot,
                        qr=[quant_rot(v) for v in rot], key=dedup_key(kind, c, size, rot, cls)))
    # --- batches
    for i in range(30):
        p = (rnd.uniform(-2100, 2100), rnd.uniform(-3100, 200), rnd.uniform(-2100, 2100))
        if i % 6 == 0:
            p = (-2048.0 + 160.0 * rnd.randint(0, 25), -3040.0 + 160.0 * rnd.randint(0, 19), -2048.0)   # exact edges
        b = batch_of(p)
        out.append(line("batch", p=p, b=list(b), mins=batch_mins(*b), d=distance_to_batch(p, (b[0] + 1, b[1], b[2] - 1))))
    # --- hosts and census names
    for bx, by, bz, ep, part in ((0, 0, 0, 1, -1), (12, 18, 7, 3, -1), (25, 19, 25, 4294967295, 2), (-1, -2, -3, 7, 0), (3, 17, 9, 12, 11)):
        out.append(line("host", b=[bx, by, bz], epoch=ep, part=part, name=host_name(bx, by, bz, ep, part),
                        census=census_file_name(bx, by, bz)))
    for n in (0, 1, 1023, 1024, 1025, 2048, 2049, 4096):
        parts = host_parts(n)
        ranges = [host_range(k, n) for k in range(parts)]
        out.append(line("parts", count=n, parts=parts, ranges=ranges))
    for exists in (False, True):
        for d in (0.0, 299.9, 300.0, 300.1, 349.9, 350.0, 350.1, 1000.0):
            out.append(line("wanted", exists=exists, d=d, want=host_wanted(exists, d)))
    for have in (False, True):
        for cc in (False, True):
            for ic in (False, True):
                for ce, ie in ((1, 1), (1, 2), (2, 1)):
                    out.append(line("supersedes", have=have, curCensus=cc, curEpoch=ce, inCensus=ic, inEpoch=ie,
                                    out=supersedes(have, cc, ce, ic, ie)))
    # --- the filter table: every single flag on top of a passing static, and a few combinations, both modes
    base = {"enabled": True, "trigger": False, "layer": 0, "proxy": False, "bridge": False, "world": True,
            "creature": False, "pickupable": False, "vehicle": False, "freeze": False, "worldForces": False,
            "rb": False, "kin": False, "live": False, "allow": False, "allowWf": False}
    cases = [dict(base)]
    for k in FILTER_FIELDS:
        c = dict(base)
        c[k] = not c[k]
        cases.append(c)
    cases.append(dict(base, layer=4))
    cases.append(dict(base, rb=True, allow=True))
    cases.append(dict(base, live=True, allow=True))
    cases.append(dict(base, rb=True, kin=True))                     # a kinematic body: still refused (not allow-listed)
    cases.append(dict(base, creature=True, freeze=True, rb=True, kin=True, live=True))   # the frozen reaper
    cases.append(dict(base, creature=True, allow=True))             # the allow-list never waives the entity types
 # review F3: the WorldForces waiver (allowWorldForces) holds only for a kinematic body, never for the entity types
    cases.append(dict(base, worldForces=True, rb=True, kin=True, allow=True, allowWf=True))          # a kinematic wreck: reported
    cases.append(dict(base, worldForces=True, rb=True, kin=False, allow=True, allowWf=True))         # a dynamic body: refused
    cases.append(dict(base, worldForces=True, rb=True, kin=True, allow=True, allowWf=False))         # not on the list: refused
    cases.append(dict(base, worldForces=True, rb=True, kin=True, allowWf=True))                      # WorldForces waived, Rigidbody not
    cases.append(dict(base, creature=True, freeze=True, worldForces=True, rb=True, kin=True, allowWf=True))   # the reaper stays out
    for c in cases:
        for m in (False, True):
            out.append(line("filter", **{k: c[k] for k in FILTER_FIELDS}, layer=c["layer"], mutant=m,
                            reason=REASONS.index(filter_decide(c, m))))
    # minus
 # TerrainCollider 30 and Trigger 21)
    mask = player_solid_mask(lambda layer: layer != 9, 30, 21)
    for layer in (0, 4, 9, 13, 21, 26, 30):
        c = dict(base, layer=layer)
        out.append(line("filtermask", **{k: c[k] for k in FILTER_FIELDS}, layer=layer, mask=mask, mutant=False,
                        reason=REASONS.index(filter_decide(c, False, mask))))
    # --- payloads byte for byte, and malformed ones
    for i, n in enumerate((0, 1, 3, 17)):
        prims = [random_prim(rnd) for _ in range(n)]
        b = (rnd.randint(0, 25), rnd.randint(0, 19), rnd.randint(0, 25))
        ep = rnd.randint(1, 1000)
        data = encode(b[0], b[1], b[2], ep, prims)
        cen = encode(b[0], b[1], b[2], ep, prims, STATIC_FLAGS["Census"])
        out.append(line("payload", b=list(b), epoch=ep, count=n, hex=data, census=cen,
                        prims=[[p["kind"], p["flags"], *p["c"], *p["q"], *p["size"], p["cls"]] for p in prims]))
    good = encode(1, 2, 3, 9, [random_prim(rnd, 1), random_prim(rnd, 2)])
    bad = []
    bad.append(("tooShort", good[:15]))
    bad.append(("badCount", good + b"\0" * 8))
    bad.append(("badCount", good[:-48]))
    bad.append(("badVersion", good[:6] + struct.pack("<H", 0) + good[8:]))
    bad.append(("badVersion", good[:6] + struct.pack("<H", 2) + good[8:]))
    bad.append(("tooMany", good[:12] + struct.pack("<I", 4097) + good[16:]))
    for st_name, field_off, val in (("badKind", 0, struct.pack("<B", 0)), ("badKind", 0, struct.pack("<B", 5)),
                                    ("nonFinite", 4, struct.pack("<f", float("nan"))),
                                    ("nonFinite", 0x24, struct.pack("<f", float("inf"))),
                                    ("badRotation", 0x10, struct.pack("<4f", 0.0, 0.0, 0.0, 0.5)),
                                    ("badSize", 0x20, struct.pack("<f", 0.0)),
                                    ("badSize", 0x20, struct.pack("<f", -1.0)),
                                    ("badSize", 0x20, struct.pack("<f", 5000.0))):
        d = bytearray(good)
        o = HEADER + field_off
        d[o:o + len(val)] = val
        bad.append((st_name, bytes(d)))
    d = bytearray(good)   # the capsule (second primitive): axis 3
    d[HEADER + PRIM + 0x28:HEADER + PRIM + 0x2C] = struct.pack("<f", 3.0)
    bad.append(("badAxis", bytes(d)))
    d = bytearray(good)
    d[HEADER + PRIM + 0x28:HEADER + PRIM + 0x2C] = struct.pack("<f", 0.5)
    bad.append(("badAxis", bytes(d)))
    for name, data in bad:
        st_got, _, _ = decode(data)
        assert st_got == name, (name, st_got)
        out.append(line("bad", status=STATUS.index(name), hex=data))
    # --- the grow-only batch set (dedup, epochs)
    bs = BatchSet((4, 5, 6))
    p0 = random_prim(rnd, 1)
    seq = [p0, dict(p0), dict(p0, c=(f32(p0["c"][0] + 0.004), p0["c"][1], p0["c"][2])),
           dict(p0, c=(f32(p0["c"][0] + 0.02), p0["c"][1], p0["c"][2])), dict(p0, kind=4), dict(p0, cls=p0["cls"] ^ 1),
           dict(p0, size=(f32(p0["size"][0] * 0.5), p0["size"][1], p0["size"][2])),      # review F16: a nested box, same centre
           dict(p0, q=(0.0, 0.0, f32(math.sqrt(0.5)), f32(math.sqrt(0.5)))),              # its rotated twin, same centre
           dict(p0, q=(0.0, 0.0, f32(math.sqrt(0.5)), f32(math.sqrt(0.5))))]              # and that twin again: duplicate
    for p in seq:
        r = bs.add(p)
        out.append(line("set", kind=p["kind"], c=p["c"], size=p["size"], rot=p["q"], cls=p["cls"],
                        result=("added", "duplicate", "full").index(r), count=len(bs.items)))
    out.append("")
    return "\n".join(out)

def write_vectors(path):
    text = build_vectors()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return text

def check(path=DEFAULT_VECTORS):
    if not os.path.isfile(path):
        print("FAIL vector file missing: %s" % path)
        return 1
    with open(path, "r", encoding="utf-8", newline="") as f:
        have = f.read()
    want = build_vectors()
    if have != want:
        a, b = have.split("\n"), want.split("\n")
        for i, (x, y) in enumerate(zip(a, b)):
            if x != y:
                print("FAIL vector file differs at line %d (regenerate: python -I tools/worldmap/s7_ref.py vectors)" % (i + 1))
                return 1
        print("FAIL vector file length differs (%d vs %d lines)" % (len(a), len(b)))
        return 1
    print("PASS vector file matches (%d lines)" % have.count("\n"))
    return 0

# ---------------------------------------------------------------- selftest
def selftest():
    fails = []

    def ok(cond, what):
        if not cond:
            fails.append(what)

    st = site()
    rnd = random.Random(7)
    # codec round trip and refusal of every corruption
    prims = [random_prim(rnd) for _ in range(9)]
    data = encode(3, 4, 5, 77, prims)
    s, h, back = decode(data)
    ok(s == "ok" and h["epoch"] == 77 and h["version"] == 1 and len(back) == 9, "codec round trip")
    ok(all(a["c"] == b["c"] and a["q"] == b["q"] and a["size"] == b["size"] and a["cls"] == b["cls"] for a, b in zip(prims, back)),
       "codec fields")
    ok(len(encode(0, 0, 0, 1, [random_prim(rnd)] * LIMITS["MaxPerBatch"])) == 16 + 48 * 4096, "max payload size")
    ok(16 + 48 * 4096 < (4 << 20) // 2 - 8, "max payload fits half the 4 MiB ring")
    cen = encode(3, 4, 5, 77, prims, STATIC_FLAGS["Census"])
    ok(all(p["flags"] & 1 for p in decode(cen)[2]), "census flag set on every primitive")
    # box: the mapped OBB contains the mapped corners within the shear bound; det +1
    for _ in range(200):
        p = random_prim(rnd, 1)
        r = to_se(st, p)
        ok(r["ok"] and abs(r["det"] - 1.0) < 1e-9, "box determinant +1")
        ok(abs(norm(sub(st.se_to_sn(r["centre"]), p["c"]))) < 1e-6, "box centre round trip")
        # the half extents of an axis that is vertical in SN are unchanged; horizontal ones are divided by s
        for i in range(3):
            a = qrot(qnorm(p["q"]), AXES[i])
            lo, hi = p["size"][i] / r["s"], p["size"][i]
            ok(min(lo, hi) - 1e-9 <= r["half"][i] <= max(lo, hi) + 1e-9, "box half between h/s and h")
            if abs(a[1]) > 1 - 1e-12:
                ok(abs(r["half"][i] - p["size"][i]) < 1e-9, "vertical half unchanged")
    # capsule: end points map exactly, their SE distance ~ 2h (s ~ 1 near the surface), radius r/s
    for _ in range(100):
        p = random_prim(rnd, 2)
        r = to_se(st, p)
        a = qrot(qnorm(p["q"]), AXES[int(p["size"][2])])
        ok(norm(sub(st.se_to_sn(r["a"]), sub(p["c"], mul(a, p["size"][1])))) < 1e-6, "capsule end A exact")
        ok(norm(sub(st.se_to_sn(r["b"]), add(p["c"], mul(a, p["size"][1])))) < 1e-6, "capsule end B exact")
        ok(abs(r["radius"] * r["s"] - p["size"][0]) < 1e-9, "capsule radius r/s")
    # sphere
    p = random_prim(rnd, 3)
    r = to_se(st, p)
    ok(abs(st.Rs / norm(sub(r["centre"], st.C)) - r["s"]) < 1e-12, "sphere s")
    # a non-unit determinant is refused: mirrored axes (the S0 mutant) give det -1
    rows = to_se(st, random_prim(rnd, 1))["rows"]
    ok(det_rows((rows[0], rows[1], mul(rows[2], -1.0))) < 0, "mirrored rows give det -1")
    # sizes
    ok(capsule_size(0.5, 3.0, 1, (1, 1, 1)) == (0.5, 1.0, 1.0), "capsule 0.5/3 -> h 1")
    ok(capsule_size(1.0, 1.0, 1, (1, 1, 1))[1] == 0.0, "capsule shorter than its caps -> h 0")
    ok(capsule_size(0.5, 2.0, 2, (2.0, 3.0, 1.0)) == (1.5, 0.0, 2.0), "capsule radius x max across scale")
    ok(box_half((2, 0, 4), (1, 1, -2)) == (1.0, MIN_HALF, 4.0), "box half |size/2 x scale| clamped")
    ok(sphere_radius(2.0, (1, -3, 2)) == 6.0, "sphere x max |scale|")
    ok(shear((1, 0, 0), (0, 2, 0), (0, 0, 3)) == 0.0 and shear((1, 0, 0), (0.1, 1, 0), (0, 0, 1)) > SHEAR_TOL, "shear")
    # ids and keys
    ok(class_id_hash("") == 0x811c9dc5 and class_id_hash("a") == 0xe40c292c, "FNV-1a 32 known values")
    ok(quant(0.005) == 1 and quant(-0.005) == -1 and quant(0.0049) == 0, "quant half away from zero")
    one, ident = (1.0, 1.0, 1.0), (0.0, 0.0, 0.0, 1.0)
    ok(dedup_key(1, (1.0, 2.0, 3.0), one, ident, 5) == dedup_key(1, (1.004, 2.0, 3.0), one, ident, 5), "dedup within 1 cm")
    ok(dedup_key(1, (1.0, 2.0, 3.0), one, ident, 5) != dedup_key(2, (1.0, 2.0, 3.0), one, ident, 5), "dedup by kind")
    ok(dedup_key(1, (1.0, 2.0, 3.0), one, ident, 5) != dedup_key(1, (1.0, 2.0, 3.0), (0.5, 1.0, 1.0), ident, 5), "F16: nested boxes kept")
    ok(dedup_key(1, (1.0, 2.0, 3.0), one, ident, 5) != dedup_key(1, (1.0, 2.0, 3.0), one, (0.0, 0.0, 0.7071068, 0.7071068), 5),
       "F16: a rotated twin kept")
    ok(quant_rot(0.0005) == 1 and quant_rot(-0.0005) == -1 and quant_rot(0.00049) == 0, "quant_rot half away from zero")
    # batches
    ok(batch_of((-2048.0, -3040.0, -2048.0)) == (0, 0, 0) and batch_of((2047.9, 159.9, 2047.9)) == (25, 19, 25), "batch lattice")
    ok(distance_to_batch((0, 0, 0), batch_of((0, 0, 0))) == 0.0, "inside a batch: distance 0")
    # hosts
    ok(host_name(1, 2, 3, 4, -1) == "DE.Statics.1.2.3.e4" and host_name(1, 2, 3, 4, 0) == "DE.Statics.1.2.3.e4.p0", "host names")
    ok(parse_host_name("DE.Statics.-1.2.3.e4.p5") == (-1, 2, 3, 4, 5) and parse_host_name("DE.Static.1.2.3.e4") is None, "host parse")
    ok(host_parts(1024) == 1 and host_parts(1025) == 2 and host_range(1, 1025) == (1024, 1), "host split")
    ok(host_wanted(False, 300) and not host_wanted(False, 300.01) and host_wanted(True, 350) and not host_wanted(True, 350.01), "hysteresis")
    # filter: the frozen reaper is refused by entity type, reported by the S7.M7 mutant
    reaper = {"enabled": True, "trigger": False, "layer": 0, "proxy": False, "bridge": False, "world": True, "creature": True,
              "pickupable": False, "vehicle": False, "freeze": True, "worldForces": True, "rb": True, "kin": True,
              "live": True, "allow": False}
    ok(filter_decide(reaper) == "creature" and filter_decide(reaper, True) == "none", "frozen reaper: refused / M7 reports it")
    wreck = dict(reaper, creature=False, freeze=False, live=False, allow=True, allowWf=True)
    ok(filter_decide(wreck) == "none" and filter_decide(dict(wreck, kin=False)) == "worldForces" and
       filter_decide(dict(wreck, allowWf=False)) == "worldForces" and filter_decide(dict(reaper, allowWf=True)) == "creature",
       "F3: kinematic wreck waived; dynamic or unlisted refused; the reaper stays out")
    m = player_solid_mask(lambda layer: layer != 9, 30, 21)
    ok(m & 1 and (m >> 26) & 1 and not (m >> 9) & 1 and not (m >> 30) & 1 and not (m >> 21) & 1, "F5: the player mask")
    ok(filter_decide(dict(wreck, layer=26, worldForces=False, rb=False)) == "layer" and
       filter_decide(dict(wreck, layer=26, worldForces=False, rb=False), False, m) == "none", "F5: DiveReel in with the mask")
    # batch set
    bs = BatchSet((0, 0, 0))
    p = random_prim(rnd, 1)
    ok(bs.add(p) == "added" and bs.add(dict(p)) == "duplicate" and bs.next_epoch() == 1 and not bs.dirty, "batch set")
    for f in fails:
        print("FAIL", f)
    if fails:
        print("s7_ref selftest: %d failure(s)" % len(fails))
        return 1
    print("PASS s7_ref selftest")
    return check()

def main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    if argv[0] == "vectors":
        path = argv[1] if len(argv) > 1 else DEFAULT_VECTORS
        text = write_vectors(path)
        print("wrote %s (%d lines)" % (path, text.count("\n")))
        return 0
    if argv[0] == "check":
        return check(argv[1] if len(argv) > 1 else DEFAULT_VECTORS)
    if argv[0] in ("selftest", "--selftest"):
        return selftest()
    print("unknown command %r (vectors | check | selftest)" % argv[0])
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
